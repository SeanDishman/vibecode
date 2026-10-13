using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using VibeCode.Protocol;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    private static object Planner(string provider, string model, string? tier = null, Action<BridgeUsageSummary>? changed = null) =>
        Activator.CreateInstance(typeof(ChatViewModel).Assembly.GetType("VibeCode.Services.BridgeSuggestionUsage")!,
            [provider, model, Root, tier, changed])!;

    private static BridgeUsageSummary Summary(object planner) =>
        (BridgeUsageSummary)planner.GetType().GetProperty("Summary")!.GetValue(planner)!;

    private static void VerifySingleTerminalTeamUsage()
    {
        using var watch = LiveTurnTelemetry.Instance.Watch();
        var before = UsageLog.Instance.Entries().Sum(e => e.Total);
        var work = new BridgeWorkState();
        var team = Enumerable.Range(1, 8).Select(i => new ChatViewModel(Root,
            resume: Guid.NewGuid().ToString("N"), provider: "codex")
        {
            Model = "gpt-6-luna",
            BridgeLabel = "Codex " + i,
            Status = "running",
            IsBridgeManager = i is 1 or 5,
        }).ToArray();
        foreach (var chat in team)
        {
            Chats.Add(chat);
            typeof(ChatViewModel).GetProperty("BridgeCoordinatesOnly", Hidden)!.SetValue(chat, chat.IsBridgeManager);
            typeof(ChatViewModel).GetProperty("BridgeSingleTerminal", Hidden)!.SetValue(chat, true);
            typeof(ChatViewModel).GetProperty("BridgeWork", Hidden)!.SetValue(chat, work);
            if (!chat.IsBridgeManager)
                typeof(ChatViewModel).GetProperty("BridgeCoordinatorAgentId", Hidden)!.SetValue(chat,
                    team[Array.IndexOf(team, chat) < 4 ? 0 : 4].BridgeAgentId);
            Call(chat, "BeginTurnPricing");
        }
        using var panel = new BridgeSharedTerminalViewModel(_ => { }, (_, _) => { });
        Call(panel, "Reconcile", new object[] { team });
        var adapters = new List<CodexSession>();
        try
        {
            for (var i = 0; i < team.Length; i++)
            {
                var chat = team[i];
                var adapter = new CodexSession(new CodexSessionOptions { Cwd = Root, Model = "gpt-6-luna" });
                adapters.Add(adapter);
                Call(adapter, "PrimeNotificationFixture", chat.SessionId!);
                adapter.MessageReceived += node => Call(chat, "IngestSdk", node);
                var n = i + 1;
                var first = CodexUsage(100 * n, 40 * n, 10 * n, 100 * n, 40 * n, 10 * n);
                Call(adapter, "CaptureUsage", first, chat.SessionId);
                Call(adapter, "CaptureUsage", first.DeepClone().AsObject(), chat.SessionId); // duplicate notification
                Call(adapter, "CaptureUsage", CodexUsage(50 * n, 20 * n, 5 * n, 150 * n, 60 * n, 15 * n), chat.SessionId);
            }
            Call(adapters[2], "CaptureUsage", CodexUsage(30, 10, 3, 30, 10, 3), "native-child");
            Check("single terminal sums all eight agents, multiple requests and a native child exactly once",
                panel.InlineUsageText.StartsWith("5.4k read · 543 write") && panel.UsageToolTip.Contains($"{5_430:N0}"));
            Check("Usage live view includes the same eight-agent total with cached input counted once",
                LiveTurnTelemetry.Instance.Merge(UsageLog.Instance.Entries()).Sum(e => e.Total) - before == 5_973);
            var all = panel.InlineUsageText;
            panel.Target = team[6]; panel.Review(team[3]);
            Check("single-terminal recipient and review filters never narrow usage", panel.InlineUsageText == all);

            for (var i = 0; i < 7; i++)
            {
                var usage = (JsonObject)typeof(CodexSession).GetField("_lastUsage", Hidden)!.GetValue(adapters[i])!;
                Call(team[i], "ApplyResult", new JsonObject { ["usage"] = usage.DeepClone() });
            }
            team[7].Close(); // The eighth agent is interrupted before its final result.
            Check("all eight agents are logged, including an interrupted worker", UsageLog.Instance.Entries()
                .Count(e => team.Any(a => a.SessionId == e.SessionId)) == 8);
            Check("completed Usage history and footer retain identical token totals",
                UsageLog.Instance.Entries().Sum(e => e.Total) - before == 5_973 &&
                panel.InlineUsageText.StartsWith("5.4k read · 543 write") && LiveTurnTelemetry.Instance.StreamingCount == 0);

            var plannerId = Guid.NewGuid().ToString("N");
            var planner = Planner("codex", "gpt-6-luna", changed: usage =>
            {
                work.PlanningUsage = usage;
                Call(team[0], "RaiseBridgeProgress");
            });
            using ((IDisposable)planner)
            {
                var live = new JsonObject { ["type"] = "system", ["subtype"] = "usage_update", ["usage"] = Usage() };
                Call(planner, "Observe", live, plannerId, new JsonArray());
                Call(planner, "Observe", live.DeepClone(), plannerId, new JsonArray());
                Check("central planner live usage is included once alongside every permanent agent",
                    panel.UsageToolTip.Contains($"{5_680:N0}") && panel.InlineUsageText.Contains("553 write") &&
                    panel.UsageToolTip.Contains("central planner"));
                Check("Usage page includes active central planning without a committed duplicate",
                    LiveTurnTelemetry.Instance.Merge(UsageLog.Instance.Entries()).Sum(e => e.Total) - before == 6_233);
            }
            ((IDisposable)planner).Dispose();
            Check("closing a temporary planner preserves its tokens in both the footer and durable usage",
                panel.UsageToolTip.Contains($"{5_680:N0}") && panel.InlineUsageText.Contains("553 write") &&
                UsageLog.Instance.Entries().Count(e => e.SessionId == plannerId) == 1 && LiveTurnTelemetry.Instance.StreamingCount == 0);
            var committedCost = UsageLog.Instance.Entries().Where(e => e.SessionId == plannerId || team.Any(a => a.SessionId == e.SessionId)).Sum(e => e.CostUsd);
            Check("single-terminal cost matches every recorded agent and planner turn",
                panel.InlineUsageText.EndsWith($"~${committedCost:0.00##}"));
            var restored = JsonSerializer.Deserialize<BridgeWorkState>(JsonSerializer.Serialize(work))!;
            restored.Recover();
            foreach (var chat in team) typeof(ChatViewModel).GetProperty("BridgeWork", Hidden)!.SetValue(chat, restored);
            Call(panel, "Reconcile", new object[] { team });
            Check("saved bridge retains planning usage after the temporary process is gone",
                restored.PlanningUsage == work.PlanningUsage && panel.UsageToolTip.Contains($"{5_680:N0}"));
            var report = UsageAnalytics.BuildSince(UsageLog.Instance.Entries().Where(e => e.SessionId == plannerId || team.Any(a => a.SessionId == e.SessionId)).ToArray(),
                DateTimeOffset.Now.AddHours(-1), DateTimeOffset.Now.AddHours(1));
            Check("Usage analytics count all nine sessions with the same combined tokens", report.Turns == 9 && report.Total == 6_233);
        }
        finally { foreach (var adapter in adapters) adapter.Dispose(); }
    }

    private static JsonObject CodexUsage(int input, int cached, int output, int totalInput, int totalCached, int totalOutput) => new()
    {
        ["tokenUsage"] = new JsonObject
        {
            ["last"] = new JsonObject
            {
                ["inputTokens"] = input,
                ["cachedInputTokens"] = cached,
                ["outputTokens"] = output,
                ["reasoningOutputTokens"] = output / 2
            },
            ["total"] = new JsonObject
            {
                ["inputTokens"] = totalInput,
                ["cachedInputTokens"] = totalCached,
                ["outputTokens"] = totalOutput,
                ["reasoningOutputTokens"] = totalOutput / 2
            },
        },
    };

    private static void VerifyPlanningUsage()
    {
        var retry = Planner("claude", "claude-opus-5-5");
        using ((IDisposable)retry)
        {
            var first = new JsonObject { ["is_error"] = true, ["usage"] = Usage(), ["total_cost_usd"] = 0.3 };
            Call(retry, "Record", first, "planning-retry", new JsonArray());
            Call(retry, "Record", first, "planning-retry", new JsonArray());
            Call(retry, "BeginTurn");
            Call(retry, "Record", new JsonObject { ["usage"] = Usage(2), ["total_cost_usd"] = 1.0 }, "planning-retry", new JsonArray());
            var rows = UsageLog.Instance.Entries().Where(e => e.SessionId == "planning-retry").ToArray();
            Check("planner retries log every attempt once without repeating cumulative provider cost",
                rows.Length == 2 && rows.Sum(e => e.Total) == 780 && Math.Abs(rows.Sum(e => e.CostUsd) - 1) < 1e-10 && Summary(retry).Cost == 1);
        }

        var streaming = Planner("claude", "claude-fable-5-1", "fast");
        using ((IDisposable)streaming)
        {
            var message = new JsonObject { ["id"] = "planner-message", ["model"] = "claude-fable-5-1", ["usage"] = Usage() };
            Call(streaming, "Observe", new JsonObject
            {
                ["type"] = "stream_event",
                ["event"] = new JsonObject
                { ["type"] = "message_start", ["message"] = message.DeepClone() }
            }, "planning-stream", new JsonArray());
            Call(streaming, "Observe", new JsonObject
            {
                ["type"] = "stream_event",
                ["event"] = new JsonObject
                { ["type"] = "message_delta", ["usage"] = new JsonObject { ["output_tokens"] = 10 } }
            }, "planning-stream", new JsonArray());
            message["usage"]!["output_tokens"] = 0; // An older assistant snapshot can follow a newer stream delta.
            Call(streaming, "Observe", new JsonObject { ["type"] = "assistant", ["message"] = message }, "planning-stream", new JsonArray());
            Check("streamed and final Claude planner snapshots count one message", Summary(streaming).Read == 250 && Summary(streaming).Write == 10);
        }
        Check("interrupted Claude planning uses the selected fast-tier estimate",
            Math.Abs(UsageLog.Instance.Entries().Single(e => e.SessionId == "planning-stream").CostUsd -
                ModelPricing.TurnCost("claude-fable-5-1", 200, 30, 20, 10, "fast")) < 1e-10);

        var kimi = Planner("kimi", "kimi-k2.5");
        using ((IDisposable)kimi)
        {
            Call(kimi, "Record", new JsonObject { ["session_usage"] = Usage() }, "planning-kimi", new JsonArray());
            Call(kimi, "BeginTurn");
            Call(kimi, "Record", new JsonObject { ["session_usage"] = Usage(2) }, "planning-kimi", new JsonArray());
            Check("Kimi planner retries log only new consumption from cumulative snapshots",
                UsageLog.Instance.Entries().Where(e => e.SessionId == "planning-kimi").Sum(e => e.Total) == 520);
        }

        var checkpoint = Planner("codex", "gpt-6-luna");
        using ((IDisposable)checkpoint)
        {
            Call(checkpoint, "Observe", new JsonObject { ["type"] = "system", ["subtype"] = "codex_usage_checkpoint", ["usage"] = Usage() }, "planning-credit", new JsonArray());
            Call(checkpoint, "Observe", new JsonObject { ["type"] = "system", ["subtype"] = "codex_reserve_activated", ["model"] = "gpt-6-sol" }, "planning-credit", new JsonArray());
            Call(checkpoint, "Observe", new JsonObject { ["type"] = "system", ["subtype"] = "usage_update", ["usage"] = Usage(2) }, "planning-credit", new JsonArray());
            Call(checkpoint, "Observe", new JsonObject
            {
                ["type"] = "stream_event",
                ["event"] = new JsonObject
                { ["type"] = "message_start", ["message"] = new JsonObject { ["id"] = "next-item" } }
            }, "planning-credit", new JsonArray());
            Call(checkpoint, "Observe", new JsonObject
            {
                ["type"] = "system",
                ["subtype"] = "usage_update",
                ["usage"] = new JsonObject { ["context_input_tokens"] = 9000 }
            }, "planning-credit", new JsonArray());
            Check("stream boundaries and context occupancy never erase or inflate planner tokens", Summary(checkpoint).Read == 750);
            Call(checkpoint, "Record", new JsonObject { ["usage"] = Usage(2) }, "planning-credit", new JsonArray());
            Check("Codex planner credit recovery retains both sides of its usage checkpoint",
                UsageLog.Instance.Entries().Where(e => e.SessionId == "planning-credit").Sum(e => e.Total) == 780 && Summary(checkpoint).Read == 750);
            Call(checkpoint, "BeginTurn");
            Call(checkpoint, "Record", new JsonObject { ["usage"] = Usage() }, "planning-credit", new JsonArray());
            var rows = UsageLog.Instance.Entries().Where(e => e.SessionId == "planning-credit").ToArray();
            Check("planner model fallback persists across retries with the correct cost estimates",
                rows.Select(e => e.Model).SequenceEqual(new[] { "gpt-6-luna", "gpt-6-sol", "gpt-6-sol" }) &&
                Math.Abs(rows.Sum(e => e.CostUsd) - ModelPricing.TurnCost("gpt-6-luna", 200, 30, 20, 10)
                    - ModelPricing.TurnCost("gpt-6-sol", 600, 90, 60, 30)) < 1e-10);
        }

        var zero = Planner("codex", "gpt-6-luna");
        using ((IDisposable)zero)
        {
            Call(zero, "Observe", new JsonObject { ["type"] = "system", ["subtype"] = "usage_update", ["usage"] = Usage() }, "planning-zero", new JsonArray());
            Call(zero, "Record", new JsonObject { ["usage"] = Usage(0) }, "planning-zero", new JsonArray());
        }
        Check("authoritative zero usage removes provisional planner tokens", UsageLog.Instance.Entries().All(e => e.SessionId != "planning-zero") && !Summary(zero).HasUsage);
        WaitForUsageHistory();
        var reloaded = (UsageLog)Activator.CreateInstance(typeof(UsageLog), nonPublic: true)!;
        Check("fresh usage-log loading retains every agent and planner row", reloaded.Count == UsageLog.Instance.Count &&
            reloaded.Entries().Sum(e => e.Total) == UsageLog.Instance.Entries().Sum(e => e.Total));
    }
}
