using System.IO;
using System.Reflection;
using System.Text.Json;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    private static void VerifyCrashRecovery()
    {
        var settings = AppSettings.Current;
        var oldOpen = settings.OpenChats;
        var oldSaved = settings.SavedBridges.ToList();
        var previousRuntime = Environment.GetEnvironmentVariable("VIBECODE_CODEX_PATH");
        Environment.SetEnvironmentVariable("VIBECODE_CODEX_PATH", Path.ChangeExtension(Assembly.GetExecutingAssembly().Location, ".exe"));
        try
        {
            settings.SavedBridges.Clear();
            var (original, team) = Team("crash-recovery", 3);
            foreach (var pane in team) pane.Status = "idle";
            original.ConfigureBridgeOrchestrator(team[0], 2, "Restore Bridge tools and verify dispatch.", true);
            PumpUntil(() => Session(team[0]).Sent.Count > 0);
            original.SaveBridge();
            var saved = JsonSerializer.Deserialize<SavedBridgeState>(JsonSerializer.Serialize(settings.SavedBridges.Single(state => state.HostSessionId == team[0].SessionId)))!;
            var ids = team.Select(p => p.BridgeAgentId).ToArray();
            // End the original runtime, then restore exclusively from the persisted snapshot.
            original.CloseBridge();
            team[0].Close();
            saved.SavedAt = DateTime.Now;
            settings.UpsertSavedBridge(saved);
            settings.Save();

            var restored = new MainViewModel();
            var host = new ChatViewModel(team[0].Cwd, resume: saved.HostSessionId, provider: "codex") { Status = "idle" };
            typeof(ChatViewModel).GetField("_session", Flags)!.SetValue(host, new FakeSession());
            typeof(ChatViewModel).GetField("_bridgeSessionInitialized", Flags)!.SetValue(host, true);
            restored.Chats.Add(host); Chats.Add(host); Call(restored, "Track", host);
            Check("saved orchestrator reconnects its roster", restored.RestoreBridge(host));
            foreach (var pane in restored.BridgePanes) if (!Chats.Contains(pane)) Chats.Add(pane);
            Check("agent IDs retained in a resumed conversation remain usable", restored.BridgePanes.Select(p => p.BridgeAgentId).SequenceEqual(ids));
            Check("restored host exposes a joined messaging and dispatch roster", Tool(host, "bridge_list_agents")["joined"]!.GetValue<bool>() && restored.IsSingleTerminalBridge);
            restored.CloseBridge();

            saved.SavedAt = DateTime.Now;
            settings.UpsertSavedBridge(saved);
            settings.Save();
            settings.OpenChats = [new() { Cwd = team[0].Cwd, SessionId = saved.HostSessionId, Provider = "codex", Active = false },
                new() { Cwd = team[0].Cwd, Draft = "A different ordinary chat", Provider = "codex", Active = true }];
            settings.BridgeVisible = settings.SecondaryBridgeVisible = false;
            var background = new MainViewModel();
            background.RestoreSession();
            var backgroundHost = background.Chats.Single(p => p.SessionId == saved.HostSessionId);
            foreach (var pane in background.Chats) if (!Chats.Contains(pane)) Chats.Add(pane);
            Check("background Bridge reconnects even when another chat is selected at crash", Tool(backgroundHost, "bridge_list_agents")["joined"]!.GetValue<bool>());
            Check("background restoration preserves the ordinary chat selection", background.ActiveChat != backgroundHost && !background.ShowBridge);
            background.OpenChat(backgroundHost);
            foreach (var pane in background.BridgePanes) if (!Chats.Contains(pane)) Chats.Add(pane);
            Check("opening a restored background Bridge preserves its coordinator and terminal", background.IsSingleTerminalBridge && backgroundHost.IsBridgeManager && backgroundHost.BridgeCoordinatesOnly);
            background.CloseBridge();

            var closed = new MainViewModel();
            closed.RestoreSession();
            var dormant = closed.Chats.Single(p => p.SessionId == saved.HostSessionId);
            foreach (var pane in closed.Chats) if (!Chats.Contains(pane)) Chats.Add(pane);
            Check("an explicitly closed Bridge stays dormant after restarting", !Tool(dormant, "bridge_list_agents")["joined"]!.GetValue<bool>() && !closed.IsBridge);

            var manual = new MainViewModel();
            var resumed = manual.ResumeSession(new(saved.HostSessionId, "Recovered coordinator", saved.Cwd, DateTime.Now, null, "codex"));
            foreach (var pane in manual.BridgePanes) if (!Chats.Contains(pane)) Chats.Add(pane);
            if (!Chats.Contains(resumed)) Chats.Add(resumed);
            Check("resuming a coordinator from history automatically reconnects its Bridge", Tool(resumed, "bridge_list_agents")["joined"]!.GetValue<bool>() && manual.BridgePanes.Count == 3);
            var roster = manual.BridgePanes.ToArray();
            manual.ResumeSession(new(saved.HostSessionId, "Recovered coordinator", saved.Cwd, DateTime.Now, null, "codex"));
            Check("reopening the same coordinator does not duplicate workers", manual.BridgePanes.SequenceEqual(roster));
            var peer = manual.ResumeSession(new(saved.Peers[0].SessionId!, "Recovered worker", saved.Cwd, DateTime.Now, null, "codex"));
            Check("resuming a worker from history selects its existing Bridge pane", ReferenceEquals(peer, roster[1]) && peer.BridgeTerminalSelected && manual.BridgePanes.SequenceEqual(roster));
            resumed.Status = "error";
            var restarted = manual.ResumeSession(new(saved.HostSessionId, "Recovered coordinator", saved.Cwd, DateTime.Now, null, "codex"));
            Chats.Add(restarted);
            PumpUntil(() => restarted.Status is "idle" or "error");
            Check("a crashed coordinator reconnects without replacing healthy workers", !ReferenceEquals(restarted, resumed) && restarted.Status == "idle" && manual.BridgePanes.Skip(1).SequenceEqual(roster.Skip(1)));
            Check("coordinator process recovery retains its tools, ID and worker ownership", restarted.BridgeAgentId == ids[0] && Tool(restarted, "bridge_list_agents")["joined"]!.GetValue<bool>() && manual.BridgePanes.Skip(1).All(p => p.BridgeCoordinatorAgentId == restarted.BridgeAgentId));
            manual.CloseBridge();
            VerifyForcedQuitRecovery();
        }
        finally
        {
            Environment.SetEnvironmentVariable("VIBECODE_CODEX_PATH", previousRuntime);
            settings.OpenChats = oldOpen;
            settings.SavedBridges.Clear();
            foreach (var state in oldSaved) settings.UpsertSavedBridge(state);
        }
    }
}
