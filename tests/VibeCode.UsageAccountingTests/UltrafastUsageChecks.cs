using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using VibeCode.Protocol;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    private static void VerifyUltrafastUsage()
    {
        // Independent expected USD values from the official 2026-10-13 rate card, per million tokens:
        // https://developers.openai.com/api/docs/pricing?latest-pricing=ultrafast
        // Subscription quota (8x) is server-reported and must not multiply these API estimates or token counts.
        foreach (var (model, input, write, read, output) in new[]
        {
            ("gpt-6-astra", 60d, 75d, 6d, 300d),
            ("gpt-6.1-sol", 12d, 15d, 0.6d, 60d),
        })
        {
            Money(model + " Ultrafast fresh-input price", ModelPricing.TurnCost(model, 1_000_000, 0, 0, 0, "ultrafast"), input);
            Money(model + " Ultrafast cache-write price", ModelPricing.TurnCost(model, 0, 1_000_000, 0, 0, "ultrafast"), write);
            Money(model + " Ultrafast cache-read price", ModelPricing.TurnCost(model, 0, 0, 1_000_000, 0, "ultrafast"), read);
            Money(model + " Ultrafast output price", ModelPricing.TurnCost(model, 0, 0, 0, 1_000_000, "ultrafast"), output);
        }
        Money("Sol Ultrafast stays short-context at 272K input",
            ModelPricing.TurnCost("gpt-6.1-sol", 172_000, 0, 100_000, 100, "ultrafast", 272_000), 2.13);
        Money("Sol Ultrafast combines long-context and speed prices above 272K",
            ModelPricing.TurnCost("gpt-6.1-sol", 172_001, 0, 100_000, 100, "ultrafast", 272_001), 4.257024);

        using var watch = LiveTurnTelemetry.Instance.Watch();
        foreach (var (model, perRequest) in new[] { ("gpt-6-astra", 0.0738), ("gpt-6.1-sol", 0.01458) })
        {
            var id = "ultrafast-usage-" + model;
            var chat = new ChatViewModel(Root, resume: id, provider: "codex")
            {
                Model = model,
                UltrafastMode = true,
                Status = "running",
                ExcludeFromMemory = true,
            };
            Chats.Add(chat);
            Call(chat, "BeginTurnPricing");
            using var adapter = new CodexSession(new() { Cwd = Root, Model = model, UltrafastMode = true });
            Call(adapter, "PrimeNotificationFixture", id);
            Call(adapter, "RememberTurnPricing", new JsonObject
            {
                ["threadId"] = id,
                ["model"] = model,
                ["serviceTier"] = "ultrafast",
            });
            JsonObject? latest = null;
            adapter.MessageReceived += node =>
            {
                if (node["usage"] is JsonObject usage) latest = usage.DeepClone().AsObject();
                Call(chat, "IngestSdk", node);
            };
            void Request(int count, string? tier = null)
            {
                var snapshot = CodexUsage(1000, 300, 100, 1000 * count, 300 * count, 100 * count);
                snapshot["serviceTier"] = tier;
                Call(adapter, "CaptureUsage", snapshot, id);
            }
            UsageEntry Live() => LiveTurnTelemetry.Instance.Merge([]).Single(e => e.SessionId == id);

            Request(1);
            Money(model + " adapter-to-chat live cost", ChatCost(chat), perRequest);
            Money(model + " live dashboard cost", Live().CostUsd, perRequest);
            Check(model + " counts cached input and reasoning output once, without speed multipliers",
                Live() is { Input: 700, CacheRead: 300, Output: 100, Total: 1100 });
            Request(1);
            Money(model + " duplicate token notifications do not duplicate cost", ChatCost(chat), perRequest);

            chat.UltrafastMode = false;
            adapter.SetSpeedAsync(false, false).GetAwaiter().GetResult();
            Request(2);
            Money(model + " in-flight requests retain the dispatched Ultrafast price", ChatCost(chat), perRequest * 2);
            Request(3, "default");
            var expected = perRequest * 2 + perRequest / 6;
            Money(model + " returned standard tier overrides requested Ultrafast", ChatCost(chat), expected);
            Money(model + " live token-flow cost adds up to the same total",
                LiveTurnTelemetry.Instance.Flow().Where(e => e.SessionId == id).Sum(e => e.CostUsd), expected);

            Call(adapter, "FinishTurn", new JsonObject { ["status"] = "completed" });
            var entry = UsageLog.Instance.Entries().Single(e => e.SessionId == id);
            Money(model + " final usage log preserves the computed cost", entry.CostUsd, expected);
            Money(model + " completion replaces the live cost without doubling it", ChatCost(chat), expected);
            Check(model + " completion preserves raw tokens and clears live usage",
                entry is { Input: 2100, CacheRead: 900, Output: 300, Total: 3300, CostReported: false }
                && !LiveTurnTelemetry.Instance.Merge([]).Any(e => e.SessionId == id));

            // A temporary Bridge planner consumes the same normalized adapter output as a regular chat.
            var plannerId = id + "-planner";
            var planner = Planner("codex", model, "ultrafast");
            using ((IDisposable)planner)
            {
                Call(planner, "Observe", new JsonObject { ["type"] = "system", ["subtype"] = "usage_update", ["usage"] = latest!.DeepClone() }, plannerId, new JsonArray());
                Money(model + " Bridge planner preserves mixed-tier live cost", Summary(planner).Cost, expected);
                Call(planner, "Record", new JsonObject { ["usage"] = latest.DeepClone() }, plannerId, new JsonArray());
                Call(planner, "Record", new JsonObject { ["usage"] = latest.DeepClone() }, plannerId, new JsonArray());
            }
            Check(model + " Bridge planner records a repeated completion only once",
                UsageLog.Instance.Entries().Count(e => e.SessionId == plannerId) == 1);
            Money(model + " Bridge planner does not multiply an already-priced result",
                UsageLog.Instance.Entries().Single(e => e.SessionId == plannerId).CostUsd, expected);

            var report = UsageAnalytics.Build(UsageLog.Instance.Entries().Where(e => e.SessionId == id || e.SessionId == plannerId).ToArray(), UsageWindow.All);
            Money(model + " usage dashboard sums chat and planner costs", report.CostUsd, expected * 2);
            Check(model + " usage dashboard totals are actual tokens", report.Total == 6600 && report.Turns == 2);
        }

        // Check fallback pricing and interrupted work when a provider omits normalized dollar metadata.
        var interrupted = Planner("codex", "gpt-6.1-sol", "ultrafast");
        using ((IDisposable)interrupted)
        {
            Call(interrupted, "Observe", new JsonObject
            {
                ["type"] = "system",
                ["subtype"] = "usage_update",
                ["usage"] = new JsonObject { ["input_tokens"] = 700, ["cache_read_input_tokens"] = 300, ["output_tokens"] = 100 },
            }, "ultrafast-interrupted-planner", new JsonArray());
            Money("Ultrafast planner fallback prices normalized token buckets", Summary(interrupted).Cost, 0.01458);
        }
        Money("interrupted Ultrafast planner keeps its usage cost",
            UsageLog.Instance.Entries().Single(e => e.SessionId == "ultrafast-interrupted-planner").CostUsd, 0.01458);

        WaitForUsageHistory();
        var saved = File.ReadLines(UsageLog.FilePath).Select(line => JsonSerializer.Deserialize<UsageEntry>(line)!)
            .Where(e => e.SessionId?.StartsWith("ultrafast-", StringComparison.Ordinal) == true).ToArray();
        Money("Ultrafast costs survive durable history serialization", saved.Sum(e => e.CostUsd), 0.39756);
        Check("saved Ultrafast history preserves every real token", saved.Length == 5 && saved.Sum(e => e.Total) == 14300);
    }

    private static double ChatCost(ChatViewModel chat) =>
        ((ValueTuple<double, bool>)typeof(ChatViewModel).GetProperty("DisplayedCost", Hidden)!.GetValue(chat)!).Item1;

    private static void Money(string label, double actual, double expected) =>
        Check(label + $" (${actual:0.########})", Math.Abs(actual - expected) < 1e-9);
}
