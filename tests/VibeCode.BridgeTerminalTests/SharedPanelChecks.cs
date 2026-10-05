using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using VibeCode;
using VibeCode.AgentStatus.Mcp.Contracts;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    private static void VerifyDividerPreference(MainWindow shell, BridgeSharedTerminal panel, Border rowBorder)
    {
        var settings = new SettingsWindow();
        settings.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        var toggle = (ToggleButton)settings.FindName("AgentMessageDividersToggle");
        var original = AppSettings.Current.ShowAgentMessageDividers;
        var activity = ((ListBox)panel.FindName("SharedTranscript")).ItemsSource;
        var target = ((BridgeSharedTerminalViewModel)panel.DataContext).Target;
        // The render fixture never shows its shell. Wire the same settings callback normally attached on Loaded.
        Action refresh = () => { Call(shell, "OnSettingsChanged"); };
        AppSettings.Changed += refresh;
        try
        {
            Check("older settings keep agent message dividers enabled", JsonSerializer.Deserialize<AppSettings>("{}")!.ShowAgentMessageDividers && toggle.IsChecked == true);
            toggle.IsChecked = false;
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            shell.UpdateLayout();
            Check("settings toggle hides dividers in the current shared terminal without changing its conversation", rowBorder.BorderThickness == new Thickness(0) &&
                !AppSettings.Current.ShowAgentMessageDividers && ReferenceEquals(((ListBox)panel.FindName("SharedTranscript")).ItemsSource, activity) &&
                ReferenceEquals(((BridgeSharedTerminalViewModel)panel.DataContext).Target, target));
            var saved = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path.Combine(AppSettings.Dir, "settings.json")))!;
            Check("disabled agent message dividers survive settings persistence", !saved.ShowAgentMessageDividers);
            var reopened = new BridgeSharedTerminal();
            Check("new shared terminals honor the saved divider preference", Equals(reopened.Resources["AgentMessageDividerThickness"], new Thickness(0)));
            CaptureShell(shell, 1600, 950, "bridge-shell-dark-no-dividers.png");
            CaptureShell(shell, 1000, 850, "bridge-shell-dark-no-dividers-narrow.png");
            var results = (System.Collections.IEnumerable)Call(settings, "FindSettings", "dividers")!;
            Check("divider preference is searchable in Settings", results.Cast<object>().Any(result => (string)Property(result, "Title")! == "Agent message dividers"));
            ((ListBox)settings.FindName("Rail")).SelectedIndex = 1;
            ((ScrollViewer)settings.FindName("PaneAppearance")).ScrollToVerticalOffset(260);
            CaptureShell(settings, 900, 780, "agent-message-dividers-setting.png");
            toggle.IsChecked = true;
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            shell.UpdateLayout();
            Check("settings toggle restores dividers immediately", AppSettings.Current.ShowAgentMessageDividers && rowBorder.BorderThickness == new Thickness(0, 0, 0, 1));
        }
        finally
        {
            AppSettings.Changed -= refresh;
            AppSettings.Current.ShowAgentMessageDividers = original;
            AppSettings.Current.Save();
            panel.RefreshMessageDividers();
            settings.Close();
        }
    }

    private static void VerifySharedActivity()
    {
        var (vm, team) = Team("shared-activity", 3);
        foreach (var agent in team) agent.Status = "idle";
        team[0].Draft = "Keep my draft";
        team[1].Draft = "A different follow-up";
        vm.SetBridgeTerminalMode(true);
        var panel = vm.SharedBridgeTerminal;
        var view = panel.Activity;
        var first = new TextItem { Text = "First agent is implementing search." };
        var second = new TextItem { Text = "Second agent is verifying the index." };
        team[0].Items.Add(first);
        team[1].Items.Add(second);
        Check("shared stream interleaves original items from multiple agents", panel.Entries.Count == 2 &&
            ReferenceEquals(panel.Entries[0].Item, first) && ReferenceEquals(panel.Entries[1].Item, second));
        vm.SelectBridgeTerminal(team[1]);
        Check("recipient changes preserve the same shared activity view", ReferenceEquals(panel.Activity, view) &&
            panel.Activity.Cast<BridgeTranscriptEntry>().Count() == 2 && ReferenceEquals(panel.Target, team[1]));
        Check("each recipient keeps its draft and original item ownership", team[0].Draft == "Keep my draft" &&
            team[1].Draft == "A different follow-up" && ReferenceEquals(panel.Entries[1].Owner, team[1]));
        second.Text += " The check passed.";
        Check("streaming updates use the live item without copying", ((TextItem)panel.Entries[1].Item).Text.EndsWith("passed."));
        panel.Review(team[1]);
        Check("review filters activity without hiding the roster", panel.Activity.Cast<BridgeTranscriptEntry>().Count() == 1 && panel.Agents.Count == 3);
        panel.ShowAll();
        Check("returning to all activity keeps the viewport and both agents", ReferenceEquals(panel.Activity, view) && panel.Activity.Cast<BridgeTranscriptEntry>().Count() == 2);
        var pending = new PendingItem();
        var queued = new QueuedItem { Owner = team[0], Text = "Queued follow-up" };
        team[0].Items.Add(pending);
        team[0].Items.Add(queued);
        var third = new TextItem { Text = "Another agent responds while the first waits." };
        team[2].Items.Add(third);
        Check("shared pending and queued tails stay below new activity", ReferenceEquals(panel.Entries[^1].Item, queued) &&
            ReferenceEquals(panel.Entries[^2].Item, pending) && ReferenceEquals(panel.Entries[^3].Item, third));
        var notice = new DividerItem { Label = "Message queued for 1 bridge agent(s)" };
        var empty = new TextItem();
        var thinking = new ThinkingItem();
        var coordinationText = char.ConvertFromUtf32(0x1F451) + " [MANAGER UPDATE] Another orchestrator joined the bridge.";
        var coordination = new UserItem { Text = coordinationText };
        var queuedCoordination = new QueuedItem { Owner = team[0], Text = coordinationText };
        team[0].Items.Add(notice);
        team[0].Items.Add(coordination);
        team[0].Items.Add(queuedCoordination);
        team[1].Items.Add(empty);
        team[2].Items.Add(thinking);
        Check("shared activity omits routing notices, idle indicators and empty messages", panel.Activity.Cast<BridgeTranscriptEntry>()
            .All(entry => entry.Item != pending && entry.Item != notice && entry.Item != empty && entry.Item != thinking));
        Check("individual terminals retain their routing notices and idle indicators", team[0].Items.Contains(notice) && team[0].Items.Contains(pending));
        Check("shared activity omits automatic coordination chatter while individual terminals retain it", panel.Activity.Cast<BridgeTranscriptEntry>()
            .All(entry => entry.Item != coordination && entry.Item != queuedCoordination) && team[0].Items.Contains(coordination) && team[0].Items.Contains(queuedCoordination));
        empty.Append("A streamed reply now has content.");
        thinking.Append("Checking the rendered layout.");
        Check("initially empty streamed messages appear when content arrives", panel.Activity.Cast<BridgeTranscriptEntry>()
            .Count(entry => entry.Item == empty || entry.Item == thinking) == 2);
        empty.Text = " ";
        thinking.Text = "";
        panel.Review(team[1]);
        Check("agent review also removes messages whose content was cleared", panel.Activity.Cast<BridgeTranscriptEntry>()
            .All(entry => entry.Item != empty && entry.Item != thinking && entry.Item != notice && entry.Item != pending));
        panel.ShowAll();
        team[1].Items.Clear();
        Check("rewind/reset removes only the owning agent's activity", !panel.Entries.Any(p => ReferenceEquals(p.Owner, team[1])) && panel.Entries.Any(p => ReferenceEquals(p.Item, first)));
        vm.SetBridgeTerminalMode(false);
        vm.SetBridgeTerminalMode(true);
        Check("layout changes do not duplicate shared items", panel.Entries.Select(p => p.Item).Distinct().Count() == panel.Entries.Count);

        foreach (var agent in team) agent.Items.Clear();
        var message = new TextItem { Text = "Checking the worker's update." };
        var streamedPeer = new TextItem();
        var web = new CompactToolGroupItem(CompactToolGroupItem.WebKind, true);
        web.Add(new ToolItem { Id = "grouped-web", Name = "WebSearch", Status = "done" });
        var peer = new TextItem { Text = "Worker checks passed." };
        team[0].Items.Add(message);
        team[1].Items.Add(streamedPeer);
        team[0].Items.Add(new DividerItem { Label = "Internal routing notice" });
        team[0].Items.Add(web);
        team[1].Items.Add(peer);
        BridgeTranscriptEntry Entry(ItemVm item) => panel.Entries.Single(entry => ReferenceEquals(entry.Item, item));
        Check("consecutive text and tools share one agent label and divider despite hidden rows",
            Entry(message).StartsAgentGroup && !Entry(message).EndsAgentGroup &&
            !Entry(web).StartsAgentGroup && Entry(web).EndsAgentGroup && Entry(peer).StartsAgentGroup);
        streamedPeer.Append("A peer reply arrived between the text and tool.");
        Check("content arriving from another agent separates the surrounding activity groups",
            Entry(message).EndsAgentGroup && Entry(streamedPeer).StartsAgentGroup &&
            Entry(streamedPeer).EndsAgentGroup && Entry(web).StartsAgentGroup);
        vm.SharedBridgeTerminal.Review(team[0]);
        Check("agent review joins that agent's text and tools across filtered peer activity",
            panel.Activity.Cast<BridgeTranscriptEntry>().Count() == 2 && !Entry(message).EndsAgentGroup && !Entry(web).StartsAgentGroup);
        panel.ShowAll();
        Check("returning to all activity restores the intervening agent boundary", Entry(message).EndsAgentGroup && Entry(web).StartsAgentGroup);
        team[1].Items.Remove(streamedPeer);
        Check("removing intervening peer activity rejoins the neighbouring agent rows", !Entry(message).EndsAgentGroup && !Entry(web).StartsAgentGroup);
        team[0].Items.Remove(message);
        Check("removing a group header promotes the next surviving row", Entry(web).StartsAgentGroup && Entry(web).EndsAgentGroup);
        team[1].Items.Clear();
        Check("clearing an agent transcript leaves a complete final group", panel.Activity.Cast<BridgeTranscriptEntry>().Single() == Entry(web) && Entry(web).EndsAgentGroup);
    }

    private static void VerifySharedUsage()
    {
        var (vm, team) = Team("shared-usage", 4);
        foreach (var agent in team) { agent.Status = "idle"; agent.Model = "gpt-6.1-sol"; }
        team[0].IsBridgeManager = team[2].IsBridgeManager = true;
        vm.SetBridgeTerminalMode(true);
        var panel = vm.SharedBridgeTerminal;
        Check("shared totals stay hidden until team usage arrives", !panel.HasUsage && panel.UsageText == "");
        for (var i = 0; i < team.Length; i++)
        {
            team[i].TotalIn = 100 * (i + 1); team[i].TotalOut = 10 * (i + 1);
            team[i].TotalTokens = team[i].TotalIn + team[i].TotalOut; team[i].Cost = 0.1 * (i + 1);
        }
        Check("team totals sum every worker and orchestrator", panel.HasUsage && panel.UsageText == "Total · 1.0k read · 100 write · $1.00");
        var total = panel.UsageText;
        vm.SelectBridgeTerminal(team[3]);
        panel.Review(team[2]);
        Check("recipient and review selection do not narrow team usage totals", panel.UsageText == total);
        panel.ShowAll();
        var live = new JsonObject { ["input_tokens"] = 50, ["cache_creation_input_tokens"] = 10,
            ["cache_read_input_tokens"] = 20, ["output_tokens"] = 5 };
        Call(team[1], "SetLiveUsage", Call(team[1], "UsageOf", live));
        var liveCost = ModelPricing.TurnCost("gpt-6.1-sol", 50, 10, 20, 5);
        Check("background worker live usage includes cache reads, cache creation and estimated cost",
            panel.UsageText == $"Total · 1.1k read · 105 write · ~${1 + liveCost:0.00##}" && panel.UsageToolTip.Contains($"{1_080:N0}"));
        team[1].TotalIn += 80; team[1].TotalOut += 5; team[1].TotalTokens += 85; team[1].Cost += liveCost;
        Call(team[1], "ResetLiveUsage");
        Check("completed turns replace live estimates without counting the turn twice", panel.UsageText == $"Total · 1.1k read · 105 write · ${1 + liveCost:0.00##}");
        vm.RemoveBridgePane(team[3]);
        Check("departed agents leave the current team's totals", panel.UsageText == $"Total · 680 read · 65 write · ${0.6 + liveCost:0.00##}");
        total = panel.UsageText;
        team[3].TotalIn += 100_000; team[3].Cost += 10;
        Check("departed agent updates cannot change the team's totals", panel.UsageText == total);
        panel.Dispose();
        Check("disposing a shared terminal clears its usage", !panel.HasUsage && panel.UsageText == "");
    }

    private static void VerifyOrchestratorGroups()
    {
        var (vm, team) = Team("two-orchestrators", 6);
        foreach (var agent in team) agent.Status = "idle";
        var a = team[0]; var b = team[3];
        vm.ConfigureBridgeOrchestrator(a, 2, "Build a store website.", true);
        // Exercise peer scope negotiation in a manually arranged group. The central
        // planner used by the setup UI is covered by OrchestratorGroupTests.
        Call(vm, "PrepareBridgeOrchestrator", b, 2, true, null, null);
        var kickoff = a.Items.OfType<UserItem>().First(u => u.Text.StartsWith("[BRIDGE ORCHESTRATOR]"));
        Check("orchestrator kickoff stays an automated turn with readable goal presentation",
            (bool)Call(a, "IsSystemInjectedPrompt", kickoff.Text)! && kickoff.IsAgentMessage && kickoff.AgentKind == "Goal" &&
            kickoff.AgentBody.Contains("Build a store website") && !kickoff.AgentBody.Contains("bridge_dispatch_task"));
        var update = new UserItem { Text = "👑 [MANAGER UPDATE] Agent 4 finished catalogue verification." };
        Check("coordination updates render their content without protocol headers", update.IsAgentMessage && update.AgentKind == "Coordination update" &&
            update.AgentBody == "Agent 4 finished catalogue verification." && update.Text.StartsWith("👑 [MANAGER UPDATE]"));
        Check("multiple orchestrators retain separate worker groups", a.IsBridgeManager && b.IsBridgeManager &&
            team[1].BridgeCoordinatorAgentId == a.BridgeAgentId && team[2].BridgeCoordinatorAgentId == a.BridgeAgentId &&
            team[4].BridgeCoordinatorAgentId == b.BridgeAgentId && team[5].BridgeCoordinatorAgentId == b.BridgeAgentId);
        var listing = Tool(a, "bridge_list_agents");
        Check("MCP roster exposes group ownership and agreement state", listing["coordination_ready"]!.ToString() == "false" &&
            listing["agents"]!.AsArray().Count(p => p!["role"]!.ToString() == "orchestrator") == 2 &&
            listing["agents"]![0]!["worker_ids"]!.AsArray().Count == 2);
        Reject("dispatch waits until orchestrators agree scopes", () => Tool(a, "bridge_dispatch_task", new()
            { ["recipient"] = team[1].BridgeAgentId, ["task_name"] = "Build catalogue", ["message"] = "Own the catalogue." }));
        Reject("scope cannot be confirmed without reading peer proposals", () => Tool(a, "bridge_agree_scope", new() { ["plan_version"] = 1, ["scope"] = "Catalogue and checkout." }));
        Tool(a, "bridge_send_message", new() { ["recipient"] = b.BridgeAgentId, ["message"] = "My group will own catalogue and checkout. Can your group own marketing pages and search?" });
        Tool(b, "bridge_send_message", new() { ["recipient"] = a.BridgeAgentId, ["message"] = "Agreed. My group owns marketing pages and search; your group owns catalogue and checkout." });
        Tool(a, "bridge_read_messages");
        Tool(b, "bridge_read_messages");
        var firstAgreement = Tool(a, "bridge_agree_scope", new() { ["plan_version"] = 1, ["scope"] = "Catalogue and checkout, excluding marketing pages and search." });
        Check("one agreement does not unlock the other group's dispatch", firstAgreement["coordination_ready"]!.ToString() == "false");
        var agreement = Tool(b, "bridge_agree_scope", new() { ["plan_version"] = 1, ["scope"] = "Marketing pages and search, excluding catalogue and checkout." });
        Check("both agreements unlock owned-worker dispatch", agreement["coordination_ready"]!.ToString() == "true");
        var unchanged = Tool(a, "bridge_agree_scope", new() { ["plan_version"] = 1, ["scope"] = "Take the other group's marketing and search." });
        Check("repeated confirmation cannot silently replace the agreed division", unchanged["already_confirmed"]!.ToString() == "true" &&
            a.BridgeOrchestrationScope == "Catalogue and checkout, excluding marketing pages and search.");
        Reject("orchestrator cannot take another group's worker", () => Tool(a, "bridge_dispatch_task", new()
            { ["recipient"] = team[4].BridgeAgentId, ["task_name"] = "Steal search", ["message"] = "Own search." }));
        Reject("orchestrator cannot dispatch its peer as a worker", () => Tool(a, "bridge_dispatch_task", new()
            { ["recipient"] = b.BridgeAgentId, ["task_name"] = "Peer assignment", ["message"] = "Implement my task." }));
        Tool(a, "bridge_dispatch_task", new() { ["recipient"] = team[1].BridgeAgentId, ["task_name"] = "Build catalogue", ["message"] = "Own catalogue.cs. Verify product filtering." });
        Tool(b, "bridge_dispatch_task", new() { ["recipient"] = team[4].BridgeAgentId, ["task_name"] = "Implement search", ["message"] = "Own search.cs. Verify empty results." });
        PumpUntil(() => Session(team[1]).Sent.Count > 0 && Session(team[4]).Sent.Count > 0);
        Check("each group's assignment reaches its own worker", Session(team[1]).Sent.Any(s => s.Contains("catalogue.cs")) && Session(team[4]).Sent.Any(s => s.Contains("search.cs")));
        team[1].Items.Add(new TextItem { Text = "Catalogue implementation verified with product filtering regression checks." });
        Call(team[1], "ApplyResult", new JsonObject { ["type"] = "result", ["is_error"] = false });
        Check("worker result returns only to its own orchestrator", a.Items.OfType<QueuedItem>().Any(q => q.Text.Contains("Catalogue implementation verified")) &&
            !b.Items.OfType<QueuedItem>().Any(q => q.Text.Contains("Catalogue implementation verified")));
        try { vm.AssignBridgeWorker(team[4], a); throw new Exception("Busy reassignment succeeded"); }
        catch (InvalidOperationException) { Check("running workers cannot switch orchestrators mid-assignment", true); }
        Call(vm, "SaveBridge", vm.BridgePanes, ".vibecode-bridge.md");
        var saved = AppSettings.Current.SavedBridges.Single(s => s.HostSessionId == a.SessionId);
        var restoredSnapshot = JsonSerializer.Deserialize<SavedBridgeState>(JsonSerializer.Serialize(saved))!;
        Check("saved groups reference resumable coordinator sessions", restoredSnapshot.Peers.Single(p => p.SessionId == team[1].SessionId).CoordinatorSessionId == a.SessionId &&
            restoredSnapshot.Peers.Single(p => p.SessionId == team[4].SessionId).CoordinatorSessionId == b.SessionId &&
            restoredSnapshot.Peers.Single(p => p.SessionId == b.SessionId).IsManager && restoredSnapshot.HostIsManager);
        Check("agreed scopes are persisted for both orchestrators", restoredSnapshot.HostOrchestrationScope!.Contains("Catalogue") &&
            restoredSnapshot.Peers.Single(p => p.SessionId == b.SessionId).OrchestrationScope!.Contains("Marketing"));
        var resumedVm = new MainViewModel();
        var resumed = team.Select(original =>
        {
            var chat = new ChatViewModel(original.Cwd, provider: original.Provider) { Status = "idle" };
            typeof(ChatViewModel).GetField("_session", Flags)!.SetValue(chat, new FakeSession());
            Property(chat, "SessionId", original.SessionId!);
            Chats.Add(chat); resumedVm.Chats.Add(chat); Call(resumedVm, "Track", chat);
            return chat;
        }).ToArray();
        Check("saved multi-orchestrator bridge restores using existing sessions", resumedVm.RestoreBridge(resumed[0]) && resumedVm.BridgePanes.Count == 6);
        Check("restored workers retain stable IDs for their own coordinators", resumed[0].BridgeAgentId == a.BridgeAgentId &&
            resumed[1].BridgeCoordinatorAgentId == resumed[0].BridgeAgentId && resumed[4].BridgeCoordinatorAgentId == resumed[3].BridgeAgentId);
        Check("both restored coordinators retain their exact agreed scopes", resumed[0].IsBridgeManager && resumed[3].IsBridgeManager &&
            resumed[0].BridgeOrchestrationScope.Contains("Catalogue") && resumed[3].BridgeOrchestrationScope.Contains("Marketing") &&
            Tool(resumed[0], "bridge_list_agents")["coordination_ready"]!.ToString() == "true");
        var shared = vm.SharedBridgeTerminal;
        Check("roster groups each orchestrator with its own workers", shared.Agents[0] == a && shared.Agents[1] == team[1] && shared.Agents[3] == b && shared.Agents[4] == team[4]);
        vm.ToggleBridgeManager(b);
        Check("stepping one orchestrator down preserves the other group", a.IsBridgeManager && !b.IsBridgeManager &&
            team[1].BridgeCoordinatorAgentId == a.BridgeAgentId && team[4].BridgeCoordinatorAgentId is null);
        RejectArgument("invalid new-group requests do not create sessions", () => vm.LaunchBridgeOrchestrator(a, 0, "Invalid"));
        Check("invalid group request preserves the roster", vm.BridgePanes.Count == 6);
    }

    private static void VerifyMessagingRecovery()
    {
        var (_, team) = Team("message-recovery", 2);
        foreach (var agent in team) agent.Status = "running";
        var alias = Tool(team[0], "bridge_send_message", new() { ["agent_id"] = team[1].BridgeAgentId, ["message"] = "Legacy destination alias." });
        Check("destination agent_id alias sends without changing caller identity", alias["delivered"]!.AsArray().Count == 1 &&
            Tool(team[1], "bridge_read_messages")["messages"]![0]!["sender_id"]!.ToString() == team[0].BridgeAgentId);
        Reject("conflicting destination aliases are rejected", () => Tool(team[0], "bridge_send_message", new()
            { ["agent_id"] = team[1].BridgeAgentId, ["recipient"] = team[0].BridgeAgentId, ["message"] = "Ambiguous." }));
        try
        {
            Tool(team[0], "bridge_send_message", new() { ["recipient"] = team[1].BridgeAgentId, ["message"] = "Spoof", ["sender_id"] = team[1].BridgeAgentId });
            throw new Exception("Caller spoof succeeded");
        }
        catch (StatusValidationException ex) { Check("unknown-argument error identifies the field and recovery", ex.Message.Contains("sender_id") && ex.Message.Contains("recipient")); }
        var (failureVm, failureTeam) = Team("durable-failure", 2);
        foreach (var agent in failureTeam) agent.Status = "running";
        Tool(failureTeam[1], "bridge_read_messages");
        var receiver = (BridgeMailboxStore.Mailbox)Property(failureTeam[1], "PeerMailbox")!;
        Directory.CreateDirectory(Path.GetDirectoryName(receiver.FilePath)!);
        File.WriteAllText(receiver.FilePath, "User-replaced metadata must stay intact.");
        for (var i = 0; i < 5; i++)
            Reject("failed durable sends do not consume retry slots " + i, () => Tool(failureTeam[0], "bridge_send_message", new()
                { ["recipient"] = failureTeam[1].BridgeAgentId, ["message"] = "Retry after storage recovers." }));
        Check("failed sends preserve the replacement file", File.ReadAllText(receiver.FilePath) == "User-replaced metadata must stay intact.");
        File.Move(receiver.FilePath, receiver.FilePath + ".held");
        var retry = Tool(failureTeam[0], "bridge_send_message", new() { ["recipient"] = failureTeam[1].BridgeAgentId, ["message"] = "Retry after storage recovers." });
        Check("same message succeeds after storage recovery", retry["delivered"]!.AsArray().Count == 1 && receiver.Messages.Count == 1);
    }

    private static void VerifyMailboxRetention()
    {
        var store = new BridgeMailboxStore(Path.Combine(_root, "mailbox-retention"));
        var sender = store.Register(1, "Sender"); var receiver = store.Register(2, "Receiver");
        var messages = Enumerable.Range(0, BridgeMailboxStore.Capacity).Select(i => store.Deliver(sender, receiver, "Unread message " + i, 1, DateTimeOffset.UtcNow)).ToArray();
        try { store.Deliver(sender, receiver, "Sixth message", 1, DateTimeOffset.UtcNow); throw new Exception("Unread message evicted"); }
        catch (InvalidOperationException) { Check("full unread inbox reports backpressure without dropping messages", receiver.Messages.Count == BridgeMailboxStore.Capacity && messages.All(m => receiver.Messages.Contains(m))); }
        store.Mark(receiver, messages[0].Id, false, DateTimeOffset.UtcNow);
        var accepted = store.Deliver(sender, receiver, "Sixth message", 1, DateTimeOffset.UtcNow);
        Check("reading an incoming message makes room without discarding other unread messages", receiver.Messages.Contains(accepted) && messages.Skip(1).All(m => receiver.Messages.Contains(m)));
        var firstPage = store.ReadPage(receiver, null, 20, DateTimeOffset.UtcNow, out _);
        var cursor = firstPage.Last().Cursor;
        store.Deliver(sender, receiver, "A newer arrival between pages", 1, DateTimeOffset.UtcNow);
        Check("page cursor survives eviction of its read message during a new arrival", !receiver.Messages.Contains(firstPage.Last()) &&
            store.ReadPage(receiver, cursor, 50, DateTimeOffset.UtcNow, out _).All(m => m.Sequence < firstPage.Last().Sequence));
        var ledger = new PeerTrafficLedger();
        var now = DateTime.Now;
        Check("policy initially admits durable delivery", ledger.Admit(1, 2, "Retryable message", 1, now) == PeerMessageVerdict.Deliver);
        ledger.RollbackDelivery(1, 2, "Retryable message", now);
        Check("failed durable delivery can retry the same message", ledger.Admit(1, 2, "Retryable message", 1, now.AddSeconds(1)) == PeerMessageVerdict.Deliver);
        var prefix = new string('a', 500);
        Check("long messages with different endings have distinct identities", PeerTrafficLedger.Fingerprint(prefix + " first") != PeerTrafficLedger.Fingerprint(prefix + " second"));
    }
}
