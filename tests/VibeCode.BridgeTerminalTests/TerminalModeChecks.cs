using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using VibeCode;
using VibeCode.Protocol;
using VibeCode.Services;
using VibeCode.Terminal;
using VibeCode.UI;

internal static partial class Program
{
    private static void VerifyTerminalMode(bool headless = false)
    {
        VerifyTerminalTextTranscript();
        var log = Path.Combine(_root, "terminal-provider.log");
        Environment.SetEnvironmentVariable("VIBECODE_TEAM_FIXTURE_LOG", log);
        SeedBridgeCodexAccount("terminal-fixture", "Terminal fixture");
        Application.Current.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(new Uri("/VibeCode;component/Themes/Dark.xaml", UriKind.Relative)));
        var (vm, source) = BridgeHost("terminal-workspace", "codex", "Existing work");
        source.SetMode("default", remember: false);
        var host = vm.CreateTerminalTeam(source);
        Chats.Add(host);
        using var terminal = new TerminalSession(vm, host, Dispatcher.CurrentDispatcher);
        Check("terminal creates a dedicated parked team without changing the active chat", vm.ActiveChat == source && !vm.IsBridge
            && vm.TerminalTeamRoster(host).Count == 1 && source.Items.Count == 0);
        Check("opening terminal starts no provider for its waiting host", typeof(ChatViewModel).GetField("_session", Flags)!.GetValue(host) is null);
        Check("terminal preserves provider, model, account and permissions", host.Provider == source.Provider && host.Model == source.ChosenModel
            && host.AccountId == source.AccountId && host.Mode == source.Mode);
        Check("first prompt accepted", terminal.HandleInput("Create the first fixture file").Accepted);
        PumpUntil(() => host.Items.OfType<TextItem>().Any() && host.Status == "idle", 30_000);
        var firstSession = host.SessionId;
        Check("second prompt accepted while first agent is idle", terminal.HandleInput("Create a second independent fixture file").Accepted);
        PumpUntil(() => host.Items.OfType<UserItem>().Count(u => u.IsUserPrompt) == 2 && host.Status == "idle", 30_000);
        Check("second idle prompt reuses the first identity and session", vm.TerminalTeamRoster(host).Count == 1 && host.SessionId == firstSession);
        host.Status = "running";
        Check("a new teammate joins while the first agent is busy", terminal.HandleInput("Work alongside the first agent").Accepted);
        var second = vm.TerminalTeamRoster(host).Last(); Chats.Add(second);
        PumpUntil(() => second.Items.OfType<TextItem>().Any() && second.Status == "idle", 30_000);
        Check("busy fallback creates a distinct identity and session", vm.TerminalTeamRoster(host).Count == 2 && second != host
            && second.BridgeAgentId != host.BridgeAgentId && second.SessionId != host.SessionId);
        Check("a finished teammate is reused while the first is still busy", terminal.HandleInput("Review the two fixture files").Accepted
            && vm.TerminalTeamRoster(host).Count == 2);
        PumpUntil(() => second.Items.OfType<UserItem>().Count(u => u.IsUserPrompt) == 2 && second.Status == "idle", 30_000);
        second.Status = "running";
        Check("third agent joins only when both teammates are busy", terminal.HandleInput("Check another fixture").Accepted && vm.TerminalTeamRoster(host).Count == 3);
        var third = vm.TerminalTeamRoster(host).Last(); Chats.Add(third);
        PumpUntil(() => third.Items.OfType<TextItem>().Any() && third.Status == "idle", 30_000);
        Check("accepted prompts stay with their selected agents", host.Items.OfType<UserItem>().Count(u => u.IsUserPrompt) == 2
            && second.Items.OfType<UserItem>().Count(u => u.IsUserPrompt) == 2 && third.Items.OfType<UserItem>().Count(u => u.IsUserPrompt) == 1);
        Check("Bridge roster and all three identities are available through real MCP handlers",
            Tool(host, "bridge_list_agents")["agents"]!.AsArray().Count == 3);
        Check("normal provider setup and team context reach the fixture", ReadShared(log).Any(line => line.Contains("turn/start")
            && line.Contains("bridge_list_agents") && line.Contains("Create a second independent fixture file")));
        Tool(host, "bridge_send_message", new() { ["recipient"] = third.BridgeAgentId, ["message"] = "Keep your changes in the second fixture file." });
        Check("terminal agents communicate through their existing MCP mailboxes", Tool(third, "bridge_read_messages")["messages"]!.AsArray()
            .Any(m => m?["sender_id"]?.ToString() == host.BridgeAgentId && m?["message"]?.ToString().Contains("second fixture file") == true));
        var limit = AppSettings.Current.BridgeAgentLimit;
        AppSettings.Current.BridgeAgentLimit = 4;
        third.Status = "running";
        Check("fourth prompt fills the minimum supported team cap", terminal.HandleInput("Check fixture integration").Accepted);
        var fourth = vm.TerminalTeamRoster(host).Last(); Chats.Add(fourth);
        PumpUntil(() => fourth.Items.OfType<TextItem>().Any() && fourth.Status == "idle", 30_000);
        Check("a finished agent can accept another prompt at the limit", terminal.HandleInput("Reuse the finished fourth agent").Accepted
            && vm.TerminalTeamRoster(host).Count == 4);
        PumpUntil(() => fourth.Items.OfType<UserItem>().Count(u => u.IsUserPrompt) == 2 && fourth.Status == "idle", 30_000);
        fourth.Status = "running";
        var refused = terminal.HandleInput("All four agents are busy");
        Check("agent limit refuses only when nobody is free", !refused.Accepted && vm.TerminalTeamRoster(host).Count == 4 && !host.HasQueued);
        AppSettings.Current.BridgeAgentLimit = limit;
        foreach (var agent in vm.TerminalTeamRoster(host)) agent.Status = "idle";

        var stream = new TextItem { Text = "Streaming starts" }; second.Items.Add(stream);
        Check("shared terminal includes outputs from every agent", terminal.Snapshot().Rows!.Any(r => r.Text == "Streaming starts"));
        stream.Text += " and updates";
        Check("streamed updates retain the row identity", terminal.Snapshot().Rows!.Any(r => r.Text == "Streaming starts and updates"));
        Check("unknown commands preserve the user's draft", !terminal.HandleInput("/does-not-exist").Accepted);
        Check("oversized prompts are refused", !terminal.HandleInput(new string('x', TerminalWire.MaxPromptLength + 1)).Accepted);

        Call(host, "OnPermissionRequested", new PermissionRequest
        { RequestId = "terminal-question", ToolName = "AskUserQuestion", ToolUseId = "terminal-q", Input = CodexColorQuestion() });
        var question = host.Items.OfType<PermItem>().Last();
        var requestId = (long)Property(question, "DisplaySequence")!;
        Check("question is visible with its command and options", terminal.Snapshot().Rows!.Any(r => r.Kind == "request" && r.Text.Contains("/answer " + requestId) && r.Text.Contains("Blue")));
        Check("allow never chooses a preselected question answer", !terminal.HandleInput("/allow " + requestId).Accepted && question.IsPending);
        var idleSetting = AppSettings.Current.IdleSleepMinutes;
        AppSettings.Current.IdleSleepMinutes = 10;
        third.Status = "running";
        fourth.Status = "running";
        var now = DateTimeOffset.UtcNow;
        Console.WriteLine("Idle guards: " + string.Join("; ", vm.TerminalTeamRoster(host).Select((a, i) => (i + 1) + ": " + Property(a, "IdleSleepBlocker"))));
        terminal.ReleaseIdleAgents(now);
        var released = terminal.ReleaseIdleAgents(now.AddSeconds(50));
        Check("idle release sleeps a completed agent but preserves questions and running work", released == 1 && second.IsIdleAsleep && !host.IsIdleAsleep && !third.IsIdleAsleep);
        var oldSession = second.SessionId;
        Check("an ordinary prompt reuses an idle sleeper", terminal.HandleInput("Verify your saved work").Accepted
            && vm.TerminalTeamRoster(host).Count == 4);
        PumpUntil(() => !second.IsIdleAsleep && second.Status == "idle" && second.Items.OfType<UserItem>().Any(u => u.Text.Contains("Verify your saved work")), 30_000);
        Check("waking preserves session and team identity", second.SessionId == oldSession && vm.TerminalTeamRoster(host).Count == 4);
        third.Status = "idle";
        fourth.Status = "idle";
        AppSettings.Current.IdleSleepMinutes = 0;
        Check("disabled idle sleeping is respected", terminal.ReleaseIdleAgents(now.AddHours(1)) == 0);
        AppSettings.Current.IdleSleepMinutes = idleSetting;
        Check("answer goes to the requesting agent", terminal.HandleInput($"/answer {requestId} Blue").Accepted && !question.IsPending
            && question.Questions.Single().Custom == "Blue");
        Check("a stale permission cannot be approved", !terminal.HandleInput("/allow " + requestId).Accepted);

        for (var i = 0; i < 500; i++) host.Items.Add(new TextItem { Text = "Historical output " + i });
        Check("terminal retains a bounded viewport while desktop keeps history", terminal.Snapshot().Rows!.Length <= 240 && host.Items.Count > 500);
        for (var i = 0; i < 200; i++) host.Items.Add(new TextItem { Text = new string('\u732b', 12_000) });
        var bounded = terminal.Snapshot();
        Check("large non-ASCII histories stay inside both display and packet budgets", bounded.Rows!.Sum(r => r.Text.Length) <= 128_000
            && System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(bounded).Length < TerminalWire.MaxPacketBytes);
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 50; i++) terminal.Snapshot();
        watch.Stop();
        File.WriteAllText(Path.Combine(_root, "snapshot-timing.txt"), $"50 bounded snapshots: {watch.Elapsed.TotalMilliseconds:F1} ms\n");
        Check("new team leaves the previous team's identities intact", terminal.HandleInput("/new").Accepted && terminal.Host != host
            && vm.TerminalTeamRoster(host).Count == 4 && vm.TerminalTeamRoster(terminal.Host).Count == 1);
        Chats.Add(terminal.Host);
        var requestedDesktop = false;
        terminal.DesktopRequested += () => requestedDesktop = true;
        Check("desktop command explicitly requests return to the GUI", terminal.HandleInput("/desktop").Accepted && requestedDesktop);
        terminal.Dispose();
        Check("disconnecting terminal preserves the agent sessions", host.Status != "closed" && second.Status != "closed" && vm.TerminalTeamRoster(host).Count == 4);

        VerifyTerminalWire();
        VerifyTerminalPermissions();
        if (!headless)
        {
            VerifyTerminalSettings();
            VerifyTerminalChatEntry();
            VerifyRealTerminalConsole();
            VerifyTerminalDesktopLifecycle();
        }
        foreach (var chat in vm.TerminalTeamRoster(host)) chat.Close();
    }

    private static void VerifyTerminalPermissions()
    {
        var (vm, team) = Team("terminal-permissions", 2);
        foreach (var agent in team) agent.SetMode("default", remember: false);
        using var terminal = new TerminalSession(vm, team[0], Dispatcher.CurrentDispatcher);
        var owner = team[1];
        foreach (var allow in new[] { true, false })
        {
            Call(owner, "OnPermissionRequested", new PermissionRequest { RequestId = "terminal-tool-" + allow, ToolName = "FixtureTool", Input = new JsonObject { ["value"] = "offline fixture" } });
            var request = owner.Items.OfType<PermItem>().Last();
            var before = Session(team[0]).PermissionResponses.Count;
            var result = terminal.HandleInput((allow ? "/allow " : "/deny ") + Property(request, "DisplaySequence") + (allow ? "" : " Keep the existing file"));
            Check((allow ? "approval" : "denial") + " reaches only the requesting agent", result.Accepted && !request.IsPending
                && Session(owner).PermissionResponses.Last()["behavior"]?.ToString() == (allow ? "allow" : "deny")
                && Session(team[0]).PermissionResponses.Count == before);
            if (!allow) Check("denial preserves the user's reason", Session(owner).PermissionResponses.Last()["message"]?.ToString() == "Keep the existing file");
        }
    }

    private static void VerifyTerminalWire()
    {
        Task.Run(async () =>
        {
            using var buffer = new ShortReadStream();
            await TerminalWire.WriteAsync(buffer, new("input", "Unicode \u732b\nsecond line"));
            await TerminalWire.WriteAsync(buffer, new("ack", "kept", false));
            buffer.Position = 0;
            var prompt = await TerminalWire.ReadAsync(buffer);
            var ack = await TerminalWire.ReadAsync(buffer);
            Check("pipe protocol handles fragmented frames and multiline Unicode", prompt?.Text == "Unicode \u732b\nsecond line" && ack is { Accepted: false, Text: "kept" });
            Check("pipe EOF is an ordinary disconnect", await TerminalWire.ReadAsync(buffer) is null);
            using var bad = new MemoryStream(BitConverter.GetBytes(TerminalWire.MaxPacketBytes + 1));
            var rejected = false;
            try { await TerminalWire.ReadAsync(bad); } catch (InvalidDataException) { rejected = true; }
            Check("oversized frames are rejected before allocation", rejected);
        }).GetAwaiter().GetResult();
        Check("model output cannot inject terminal escapes or clipboard commands", !TerminalWire.DisplayText("safe\u001b]52;c;secret\a\u202e").Any(c => c is '\u001b' or '\a' or '\u202e'));
        Check("wide Unicode wraps without splitting surrogate pairs", TerminalConsole.Wrap("A猫😀B", 3).SequenceEqual(new[] { "A猫", "😀B" }));
    }

    private sealed class ShortReadStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => base.ReadAsync(buffer[..Math.Min(3, buffer.Length)], token);
    }

    private static void VerifyTerminalSettings()
    {
        AppSettings.Current.TerminalEnabled = false;
        var window = new SettingsWindow { Left = 6100, Top = 300, WindowStartupLocation = WindowStartupLocation.Manual };
        try
        {
            window.Show(); Settle(); window.SelectCategory("Developer"); window.UpdateLayout();
            Check("Developer category opens the CLI page", ((ScrollViewer)window.FindName("PaneDeveloper")).Visibility == Visibility.Visible);
            Check("CLI switch is searchable", ((IEnumerable<object>)Call(window, "FindSettings", "terminal")!).Any());
            var toggle = (ToggleButton)window.FindName("TerminalEnabledToggle");
            Check("Developer CLI has an off-by-default switch and no launch button", toggle.IsChecked == false && !new AppSettings().TerminalEnabled
                && !Descendants<Button>((ScrollViewer)window.FindName("PaneDeveloper")).Any(b => b.Name == "OpenTerminalButton"));
            toggle.IsChecked = true; Settle();
            var saved = JsonNode.Parse(File.ReadAllText(Path.Combine(AppSettings.Dir, "settings.json")))!;
            Check("enabling CLI persists without closing settings or opening a console", AppSettings.Current.TerminalEnabled
                && saved[nameof(AppSettings.TerminalEnabled)]!.GetValue<bool>() && window.IsVisible && window.WindowState != WindowState.Minimized);
            CaptureShell(window, 1060, 760, "terminal-developer-settings.png");
            toggle.IsChecked = false; Settle();
            saved = JsonNode.Parse(File.ReadAllText(Path.Combine(AppSettings.Dir, "settings.json")))!;
            Check("disabling CLI persists immediately", !AppSettings.Current.TerminalEnabled && !saved[nameof(AppSettings.TerminalEnabled)]!.GetValue<bool>());
        }
        finally { window.Close(); }
    }
}
