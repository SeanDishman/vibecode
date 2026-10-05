using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;
using VibeCode.Protocol;
using VibeCode.Services;

internal static partial class Program
{
    private static void VerifyBundledMcp(McpServerDefinition registration, string agentId)
    {
        var start = new ProcessStartInfo(registration.Command)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var arg in registration.Arguments) start.ArgumentList.Add(arg);
        foreach (var pair in registration.Environment) start.Environment[pair.Key] = pair.Value;
        var operation = Task.Run(async () =>
        {
            using var process = Process.Start(start)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{},\"clientInfo\":{\"name\":\"offline\",\"version\":\"1\"}}}");
                await process.StandardInput.FlushAsync();
                var init = await process.StandardOutput.ReadLineAsync(timeout.Token);
                await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
                await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"bridge_list_agents\",\"arguments\":{}}}");
                await process.StandardInput.FlushAsync();
                var reply = await process.StandardOutput.ReadLineAsync(timeout.Token);
                var valid = init?.Contains("vibecode-bridge") == true && reply?.Contains(agentId) == true;
                if (!valid) Console.WriteLine("MCP fixture response: " + init + " / " + reply);
                return valid;
            }
            finally
            {
                process.StandardInput.Close();
                if (!process.WaitForExit(2000)) process.Kill(entireProcessTree: true);
            }
        });
        PumpUntil(() => operation.IsCompleted);
        Check("bundled executable serves MCP end to end", operation.GetAwaiter().GetResult());
    }

    private static void VerifyFreshTeamStartup()
    {
        var previous = Environment.GetEnvironmentVariable("VIBECODE_CODEX_PATH");
        Environment.SetEnvironmentVariable("VIBECODE_CODEX_PATH", System.IO.Path.ChangeExtension(Assembly.GetExecutingAssembly().Location, ".exe"));
        try
        {
            var (vm, team) = Team("fresh-team", 2);
            team[0].Status = team[1].Status = "idle";
            vm.ConfigureBridgeOrchestrator(team[0], 4, "Implement independent account, storage, UI and verification work.", true);
            PumpUntil(() => vm.BridgePanes.Skip(2).All(p => p.Status is "idle" or "error"));
            Check("chosen count starts the exact additional workers", vm.BridgePanes.Count == 5 && vm.BridgePanes.Skip(2).All(p => p.Status == "idle"));
            Check("new workers use real provider adapter with local fixture", vm.BridgePanes.Skip(2).All(p => p.GetType().GetField("_session", Flags)!.GetValue(p) is CodexSession));
            var before = Session(team[0]).Sent.Count;
            team[0].Model = "gpt-6-luna";
            var usageBefore = UsageLog.Instance.Count;
            var suggestion = BridgeTeamSuggestionService.SuggestAsync(team[0], "Fix login and test it.", 8);
            PumpUntil(() => suggestion.IsCompleted);
            Check("AI count suggestion uses an isolated provider turn", suggestion.GetAwaiter().GetResult().WorkerCount == 2 && Session(team[0]).Sent.Count == before);
            var row = UsageLog.Instance.Entries().Last();
            Check("AI count suggestion logs one turn despite repeated usage snapshots", UsageLog.Instance.Count == usageBefore + 1);
            Check("planning usage preserves cache tiers without double counting", row.Input == 80 && row.CacheRead == 20 && row.Output == 5 && row.Total == 105);
            Check("planning usage belongs to its own session and the bridge project", row.SessionId != team[0].SessionId && row.Project == team[0].Cwd && row.Model == "gpt-6-luna");
            PumpUntil(() => System.IO.File.Exists(UsageLog.FilePath) && System.IO.File.ReadAllText(UsageLog.FilePath).Contains(row.SessionId!));
            Check("planning usage is written to durable history", System.IO.File.ReadAllText(UsageLog.FilePath).Contains(row.SessionId!));
            var invalid = BridgeTeamSuggestionService.SuggestAsync(team[0], "invalid team size fixture", 8);
            PumpUntil(() => invalid.IsCompleted);
            try { invalid.GetAwaiter().GetResult(); throw new Exception("Invalid fixture suggestion was accepted."); }
            catch (InvalidOperationException) { }
            Check("unusable AI count responses still log their consumed tokens", UsageLog.Instance.Count == usageBefore + 2);
            foreach (var pane in vm.BridgePanes.Skip(2)) Chats.Add(pane);
            vm.CloseBridge();
        }
        finally { Environment.SetEnvironmentVariable("VIBECODE_CODEX_PATH", previous); }
    }

    private static int RunProviderFixture()
    {
        var thread = "offline-" + Guid.NewGuid().ToString("N");
        void Emit(JsonObject value) { Console.WriteLine(value.ToJsonString()); Console.Out.Flush(); }
        void Notify(string method, JsonObject value) => Emit(new() { ["method"] = method, ["params"] = value });
        while (Console.ReadLine() is { } line)
        {
            var request = JsonNode.Parse(line)!;
            if (request["id"] is not { } id) continue;
            var method = request["method"]!.ToString();
            if (method == "thread/resume") thread = request["params"]?["threadId"]?.ToString() ?? thread;
            var result = method switch
            {
                "model/list" => new JsonObject { ["data"] = new JsonArray() },
                "thread/start" => new JsonObject { ["thread"] = new JsonObject { ["id"] = thread } },
                "thread/resume" => new JsonObject { ["thread"] = new JsonObject { ["id"] = request["params"]?["threadId"]?.ToString() ?? thread } },
                "turn/start" => new JsonObject { ["turn"] = new JsonObject { ["id"] = "fixture-turn" } },
                _ => new JsonObject(),
            };
            Emit(new() { ["id"] = id.DeepClone(), ["result"] = result });
            if (method != "turn/start") continue;
            Notify("turn/started", new() { ["threadId"] = thread, ["turn"] = new JsonObject { ["id"] = "fixture-turn" } });
            Notify("item/completed", new()
            {
                ["threadId"] = thread, ["turnId"] = "fixture-turn", ["item"] = new JsonObject
                {
                    ["type"] = "agentMessage", ["id"] = "fixture-reply",
                    ["text"] = line.Contains("invalid team size fixture")
                        ? "{\"worker_count\":99,\"reason\":\"Invalid fixture response.\"}"
                        : "{\"worker_count\":2,\"reason\":\"One implementation lane and one verification lane.\"}",
                },
            });
            for (var repeat = 0; repeat < 2; repeat++)
                Notify("thread/tokenUsage/updated", new()
                {
                    ["threadId"] = thread, ["tokenUsage"] = new JsonObject
                    {
                        ["total"] = new JsonObject { ["inputTokens"] = 100, ["cachedInputTokens"] = 20, ["outputTokens"] = 5 },
                        ["last"] = new JsonObject { ["inputTokens"] = 100, ["cachedInputTokens"] = 20, ["outputTokens"] = 5 },
                    },
                });
            Notify("turn/completed", new() { ["threadId"] = thread, ["turn"] = new JsonObject { ["id"] = "fixture-turn", ["status"] = "completed" } });
        }
        return 0;
    }
}
