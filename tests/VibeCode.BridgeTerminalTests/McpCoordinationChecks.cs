using System.IO;
using System.Reflection;
using System.Text.Json;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    private static void VerifyMcpCoordination()
    {
        // Persisted settings from an older app cannot re-enable the retired Markdown workflow.
        var oldSettings = JsonSerializer.Deserialize<AppSettings>("{\"BridgeRealtimeSharing\":true,\"BridgePeerMessaging\":false}")!;
        Check("old real-time sharing settings load without losing current preferences", !oldSettings.BridgePeerMessaging);
        var previousMessaging = AppSettings.Current.BridgePeerMessaging;
        var previousRuntime = Environment.GetEnvironmentVariable("VIBECODE_CODEX_PATH");
        Environment.SetEnvironmentVariable("VIBECODE_CODEX_PATH", Path.ChangeExtension(Assembly.GetExecutingAssembly().Location, ".exe"));
        try
        {
            var (vm, team) = Team("mcp-prompts", 2);
            foreach (var enabled in new[] { true, false })
            {
                AppSettings.Current.BridgePeerMessaging = enabled;
                foreach (var provider in new[] { "claude", "codex", "glm" })
                {
                    var pane = new ChatViewModel(team[0].Cwd, provider: provider);
                    Chats.Add(pane);
                    foreach (var (method, args) in new (string, object?[])[]
                    {
                        ("BridgePrompt", [".vibecode-bridge.md", pane, 2, new[] { 1, 2 }]),
                        ("BridgeJoinPrelude", [".vibecode-bridge.md", pane, 2, new[] { 1, 2 }]),
                        ("BridgeForkPrelude", [".vibecode-bridge-2.md", ".vibecode-bridge.md", pane, 2, new[] { 1, 2 }]),
                    })
                    {
                        var prompt = (string)Call(vm, method, args)!;
                        Check($"{provider} {method} uses MCP with messaging {enabled}",
                            prompt.Contains("bridge_list_agents") && prompt.Contains("bridge_set_task_title")
                            && prompt.Contains("do not read or write Markdown status-board files")
                            && prompt.Contains("replaces any earlier instructions")
                            && !prompt.Contains(".vibecode-bridge") && !prompt.Contains("## Active")
                            && !prompt.Contains("## Live activity"));
                    }
                }
            }
            AppSettings.Current.BridgePeerMessaging = true;

            // Exercise the actual primary lifecycle, including a user's existing legacy board.
            var (live, host) = BridgeHost("mcp-lifecycle", "codex", "Check bridge coordination");
            var board = Path.Combine(host.Cwd, ".vibecode-bridge.md");
            const string legacy = "# Old coordination notes\n\nKeep these user notes intact.\n";
            File.WriteAllText(board, legacy);
            live.ActivateBridge("codex");
            PumpUntil(() => live.BridgePanes[1].Status is "idle" or "error");
            Check("starting a bridge preserves an existing legacy board", File.ReadAllText(board) == legacy);
            Check("a normal bridge exposes its real MCP roster", Tool(host, "bridge_list_agents")["agents"]!.AsArray().Count == 2);
            live.AddBridgeAgent("codex");
            PumpUntil(() => live.BridgePanes[2].Status is "idle" or "error");
            var removed = live.BridgePanes[1];
            removed.Status = "error";
            Check("an error tells peers to use MCP status without changing the board",
                host.Prelude?.Contains("bridge_agent_status") == true && File.ReadAllText(board) == legacy);
            live.RemoveBridgePane(removed);
            Check("departure compacts the MCP roster without rewriting user notes",
                live.BridgePanes.Count == 2 && live.BridgePanes[1].BridgeLabel == "Codex 2"
                && Tool(host, "bridge_list_agents")["agents"]!.AsArray().Count == 2 && File.ReadAllText(board) == legacy);
            PumpUntil(() => live.BridgePanes[1].SessionId is not null);
            live.CloseBridge();
            live.ActivateBridge("codex");
            Check("restoring a bridge overrides legacy instructions and preserves the old file",
                live.BridgePanes.Count == 2 && host.Prelude?.Contains("replaces any earlier instructions") == true
                && File.ReadAllText(board) == legacy);
            live.CloseBridge();

            // Fresh and secondary bridges must not create a board at all.
            var (fresh, freshHost) = BridgeHost("mcp-fresh", "codex", "Fresh bridge");
            fresh.ActivateBridge("codex");
            PumpUntil(() => fresh.BridgePanes[1].Status is "idle" or "error");
            Check("a new primary bridge creates no Markdown status board", !Directory.EnumerateFiles(freshHost.Cwd, ".vibecode-bridge*.md").Any());
            fresh.CloseBridge();

            var (secondary, secondaryHost) = BridgeHost("mcp-secondary", "codex", "Second display bridge");
            Check("secondary bridge starts", secondary.ActivateBridgeOnSecondary(secondaryHost, "codex"));
            PumpUntil(() => secondary.SecondaryBridgePanes[1].Status is "idle" or "error");
            secondary.AddBridgeAgentOnSecondary("codex");
            PumpUntil(() => secondary.SecondaryBridgePanes[2].Status is "idle" or "error");
            Check("secondary startup and join create no Markdown status board",
                !Directory.EnumerateFiles(secondaryHost.Cwd, ".vibecode-bridge*.md").Any()
                && Tool(secondaryHost, "bridge_list_agents")["agents"]!.AsArray().Count == 3);
            secondary.CloseSecondaryBridge();
        }
        finally
        {
            AppSettings.Current.BridgePeerMessaging = previousMessaging;
            Environment.SetEnvironmentVariable("VIBECODE_CODEX_PATH", previousRuntime);
        }
    }
}
