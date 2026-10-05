using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using VibeCode.Protocol;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    private static void VerifyForcedQuitRecovery()
    {
        var directory = Path.Combine(_root, "forced-quit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using (var seed = RecoveryChild("--crash-seed", directory))
        {
            try
            {
                PumpUntil(() => File.Exists(Path.Combine(directory, "ready")) || seed.HasExited);
                Check("crash fixture persisted a complete live Bridge before the kill", !seed.HasExited && File.Exists(Path.Combine(directory, "data", "settings.json")));
                seed.Kill(entireProcessTree: true);
                seed.WaitForExit(5000);
                Check("force quit terminates the original runtime without graceful shutdown", seed.HasExited && !File.Exists(Path.Combine(directory, "clean-shutdown")));
            }
            finally { if (!seed.HasExited) seed.Kill(entireProcessTree: true); }
        }
        using var resumed = RecoveryChild("--crash-resume", directory);
        var stdout = resumed.StandardOutput.ReadToEndAsync();
        var stderr = resumed.StandardError.ReadToEndAsync();
        try
        {
            PumpUntil(() => resumed.HasExited, 30000);
            File.WriteAllText(Path.Combine(directory, "resume.log"), stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
            if (resumed.ExitCode != 0) Console.WriteLine(File.ReadAllText(Path.Combine(directory, "resume.log")));
            Check("fresh process restores tools, ownership and dispatch from crash-persisted state", resumed.ExitCode == 0 && File.Exists(Path.Combine(directory, "recovered")));
        }
        finally { if (!resumed.HasExited) resumed.Kill(entireProcessTree: true); }
    }

    private static Process RecoveryChild(string mode, string directory)
    {
        var start = new ProcessStartInfo(Path.ChangeExtension(Assembly.GetExecutingAssembly().Location, ".exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.ArgumentList.Add(mode); start.ArgumentList.Add(directory);
        return Process.Start(start)!;
    }

    private static int RunCrashRecoveryChild(string mode)
    {
        var runtime = Path.ChangeExtension(Assembly.GetExecutingAssembly().Location, ".exe");
        Environment.SetEnvironmentVariable("VIBECODE_CODEX_PATH", runtime);
        if (mode == "--crash-seed")
        {
            var (vm, team) = Team("forced-crash-workspace", 3);
            vm.RestoreSession();
            foreach (var pane in team) pane.Status = "idle";
            vm.ConfigureBridgeOrchestrator(team[0], 2, "Repair the startup connection.", true);
            PumpUntil(() => Session(team[0]).Sent.Count > 0);
            Tool(team[0], "bridge_dispatch_task", new() { ["recipient"] = team[1].BridgeAgentId, ["task_id"] = "crash-assignment",
                ["task_name"] = "Verify restored tools", ["message"] = "Verify this assignment reaches the resumed provider fixture." });
            Tool(team[1], "bridge_send_message", new() { ["recipient"] = team[0].BridgeAgentId, ["message"] = "Durable note sent before the forced process kill." });
            foreach (var peer in team.Skip(1)) vm.Chats.Remove(peer);
            vm.SetBridgeTerminalMode(true);
            vm.SelectBridgeTerminal(team[1]);
            team[1].Draft = "Keep this worker draft after a hard kill";
            var plain = new ChatViewModel(team[0].Cwd, provider: "codex") { Draft = "Keep this ordinary chat selected" };
            typeof(ChatViewModel).GetField("_session", Flags)!.SetValue(plain, new FakeSession());
            vm.Chats.Add(plain); Chats.Add(plain); Call(vm, "Track", plain);
            vm.OpenChat(plain);
            vm.SaveEverything();
            var saved = AppSettings.Current.FindSavedBridge(team[0].SessionId, "codex")!;
            File.WriteAllText(Path.Combine(_root, "expected.json"), JsonSerializer.Serialize(saved));
            File.WriteAllText(Path.Combine(_root, "ready"), "State was saved; waiting to be force killed.");
            Dispatcher.PushFrame(new DispatcherFrame());
            File.WriteAllText(Path.Combine(_root, "clean-shutdown"), "Graceful shutdown must not run in this test.");
            return 0;
        }

        var expected = JsonSerializer.Deserialize<SavedBridgeState>(File.ReadAllText(Path.Combine(_root, "expected.json")))!;
        var restored = new MainViewModel();
        restored.RestoreSession();
        var host = restored.Chats.Single(p => p.SessionId == expected.HostSessionId);
        Check("hard kill restoration reconnects a background host", Tool(host, "bridge_list_agents")["joined"]!.GetValue<bool>());
        Check("hard kill preserves the selected ordinary chat", restored.ActiveChat != host && !restored.ShowBridge);
        restored.OpenChat(host);
        foreach (var pane in restored.Chats.Concat(restored.BridgePanes)) if (!Chats.Contains(pane)) Chats.Add(pane);
        PumpUntil(() => restored.BridgePanes.All(p => p.Status is "idle" or "error"));
        Check("all resumed provider adapters initialize successfully", restored.BridgePanes.All(p => p.Status == "idle"));
        Check("hard kill retains the single-terminal layout and recipient", restored.IsSingleTerminalBridge && restored.BridgePanes.Single(p => p.BridgeTerminalSelected).SessionId == expected.SelectedTerminalSessionId);
        Check("hard kill retains the coordinator's stable tool identity", host.BridgeAgentId == expected.HostAgentId && host.IsBridgeManager && host.BridgeCoordinatesOnly);
        Check("worker IDs and coordinator ownership survive a process boundary", restored.BridgePanes.Skip(1).All(p => p.BridgeAgentId == expected.Peers.Single(saved => saved.SessionId == p.SessionId).AgentId && p.BridgeCoordinatorAgentId == host.BridgeAgentId));
        Check("hard kill retains the worker draft", restored.BridgePanes[1].Draft == "Keep this worker draft after a hard kill");
        var endpoint = Call(host, "EnsureBridgeMcp")!;
        var registration = (McpServerDefinition)Call(endpoint, "Registration")!;
        VerifyBundledMcp(registration, host.BridgeAgentId);
        var worker = restored.BridgePanes[1];
        var pipe = (string)Property(endpoint, "PipeName")!;
        JsonObject ThroughPipe(string tool, JsonObject args)
        {
            var request = VibeCode.AgentStatus.Mcp.Bridge.BridgeMcpClient.InvokeAsync(pipe, tool, args);
            PumpUntil(() => request.IsCompleted);
            return request.GetAwaiter().GetResult();
        }
        var message = ThroughPipe("bridge_send_message", new() { ["recipient"] = worker.BridgeAgentId, ["message"] = "Messaging is reconnected after force quit." });
        Check("resumed tool endpoint delivers peer messages", message["delivered"]!.AsArray().Count == 1 && worker.UnreadPeerMessageCount == 1);
        var recoveredTasks = ThroughPipe("bridge_list_tasks", new());
        Check("hard kill preserves original assignment and permanently closed setup", recoveredTasks["orchestrator_communication_locked"]!.GetValue<bool>() &&
            recoveredTasks["tasks"]![0]!["task_id"]!.ToString() == "crash-assignment" && recoveredTasks["tasks"]![0]!["status"]!.ToString() == "interrupted");
        Check("recovered assignment is not automatically executed", !worker.Items.OfType<TextItem>().Any(item => item.Text.Contains("worker_count")));
        Check("mailbox body survives a real forced process kill", ThroughPipe("bridge_read_messages", new())["messages"]!.AsArray().Any(m => m!["message"]!.ToString().Contains("before the forced process kill")));
        var dispatch = ThroughPipe("bridge_retry_task", new() { ["task_id"] = "crash-assignment" });
        Check("resumed tool endpoint retries the saved identity and assignment", dispatch["owner_id"]!.ToString() == worker.BridgeAgentId && dispatch["task_id"]!.ToString() == "crash-assignment");
        PumpUntil(() => worker.Items.OfType<TextItem>().Any(item => item.HasText));
        Check("the dispatched assignment receives a provider reply", worker.Items.OfType<TextItem>().Any(item => item.Text.Contains("worker_count")));
        File.WriteAllText(Path.Combine(_root, "recovered"), "Bridge tools and dispatch passed after a real forced process kill.");
        restored.CloseBridge();
        return 0;
    }
}
