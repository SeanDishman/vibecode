using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using VibeCode;
using VibeCode.Protocol;
using VibeCode.UI;

internal static partial class Program
{
    // These are wire fixtures from `codex app-server generate-json-schema --experimental` (0.159.2).
    // They exercise the adapter and the real chat view model together without launching any model or child.
    private static void VerifyCodexSubagents()
    {
        var (_, peers) = Team("codex-subagents-protocol", 2);
        var chat = peers[0];
        chat.Status = "idle";
        using var session = new CodexSession(new() { Cwd = _root });
        Call(session, "PrimeNotificationFixture", "parent");
        var events = new List<JsonNode>();
        session.MessageReceived += message => { events.Add(message.DeepClone()); Call(chat, "IngestSdk", message); };
        void Notify(string method, JsonObject args) => Call(session, "HandleNotificationFixture", method, args);
        void Item(JsonObject item, string thread = "parent", bool complete = true) => Notify(complete ? "item/completed" : "item/started",
            new() { ["threadId"] = thread, ["turnId"] = "turn", ["item"] = item });
        static JsonObject Collab(string tool, string id, string? status = null, string? message = null) => new()
        {
            ["type"] = "collabAgentToolCall",
            ["id"] = "call-" + tool + id,
            ["tool"] = tool,
            ["senderThreadId"] = "parent",
            ["receiverThreadIds"] = new JsonArray(JsonValue.Create(id)),
            ["status"] = "completed",
            ["prompt"] = "Review the build and report concrete findings.",
            ["agentsStates"] = status is null ? new JsonObject() : new JsonObject
            { [id] = new JsonObject { ["status"] = status, ["message"] = message } },
        };
        void Activity(string id, string kind) => Item(new()
        {
            ["type"] = "subAgentActivity",
            ["id"] = id + kind,
            ["agentThreadId"] = id,
            ["agentPath"] = "/root/" + id,
            ["kind"] = kind,
        });

        Notify("turn/started", new() { ["threadId"] = "parent", ["turn"] = new JsonObject { ["id"] = "parent-turn" } });
        Item(Collab("spawnAgent", "reviewer", "pendingInit"), complete: false);
        var reviewer = chat.Subagents.Single();
        Check("native Codex spawn is visible in the main transcript immediately", chat.Items.OfType<SubagentItem>().Single() == reviewer);
        Check("inline worker and header roster share one model", reviewer.Task.Contains("concrete findings") && reviewer.Status == "pendingInit");
        Item(Collab("spawnAgent", "reviewer", "pendingInit"));
        Activity("reviewer", "started");
        Check("started/completed spawn notifications produce one card with its named worker", chat.Items.OfType<SubagentItem>().Count() == 1
            && reviewer.Label == "reviewer" && reviewer.IsActive);
        Notify("turn/started", new() { ["threadId"] = "reviewer", ["turn"] = new JsonObject { ["id"] = "review-turn" } });
        Item(Collab("spawnAgent", "reviewer"));
        Check("a delayed spawn acknowledgement cannot reset a running child to starting", reviewer.Status == "running");
        Item(Collab("spawnAgent", "tests", "running"));
        var tests = chat.Subagents.Single(a => a.ThreadId == "tests");
        Notify("item/agentMessage/delta", new() { ["threadId"] = "reviewer", ["itemId"] = "report", ["delta"] = "Checking the build." });
        Check("child output streams into its own expandable transcript", reviewer.Transcript.OfType<TextItem>().Single().Text == "Checking the build."
            && !chat.Items.OfType<TextItem>().Any(t => t.Text.Contains("Checking")) && tests.Transcript.Count == 0);
        Item(new() { ["type"] = "agentMessage", ["id"] = "report", ["text"] = "The build passes; one packaging issue needs attention." }, "reviewer");
        Check("final child text replaces its streaming placeholder", reviewer.Transcript.OfType<TextItem>().Count() == 1
            && reviewer.Transcript.OfType<TextItem>().Single().Text.Contains("packaging"));
        Item(new()
        {
            ["type"] = "commandExecution",
            ["id"] = "test-command",
            ["command"] = "dotnet test",
            ["status"] = "completed",
            ["aggregatedOutput"] = "33 tests passed",
            ["exitCode"] = 0
        }, "tests");
        Check("child tools remain in the correct worker", tests.Transcript.Count == 1 && reviewer.Transcript.OfType<ToolItem>().Count() == 0);
        Check("another Bridge pane never receives this worker or output", peers[1].Subagents.Count == 0 && !peers[1].Items.OfType<SubagentItem>().Any());
        chat.OpenSubagent(reviewer);
        Check("the roster inspector reads the same live output as the card", ReferenceEquals(chat.SelectedSubagent?.Transcript, reviewer.Transcript));
        var completeCount = events.Count(e => e["type"]?.ToString() == "result");
        Notify("turn/completed", new() { ["threadId"] = "parent", ["turn"] = new JsonObject { ["id"] = "parent-turn", ["status"] = "completed" } });
        Check("parent completion waits for genuinely active workers", events.Count(e => e["type"]?.ToString() == "result") == completeCount);
        Activity("reviewer", "completed");
        Check("completed subAgentActivity settles its worker even without a child turn/completed", reviewer.Status == "completed" && chat.ActiveSubagentCount == 1);
        Check("one worker finishing does not finish the other", tests.IsActive && events.Count(e => e["type"]?.ToString() == "result") == completeCount);
        Item(Collab("wait", "tests", "completed", "All 33 tests passed."));
        Check("the last worker completion releases the parent result", events.Count(e => e["type"]?.ToString() == "result") == completeCount + 1
            && chat.ActiveSubagentCount == 0);
        Check("parent-reported final output is readable when detailed child messages are absent",
            tests.Transcript.OfType<TextItem>().Single().Text == "All 33 tests passed.");
        Item(Collab("wait", "tests", "completed", "All 33 tests passed."));
        Item(new() { ["type"] = "agentMessage", ["id"] = "late-report", ["text"] = "All 33 tests passed." }, "tests");
        Check("repeated snapshots and a late native final message do not duplicate the report", tests.Transcript.OfType<TextItem>().Count() == 1);
        Item(Collab("followupTask", "reviewer"));
        Check("a follow-up reuses and reactivates the same worker card", reviewer.IsActive && chat.Items.OfType<SubagentItem>().Count() == 2);
        Item(Collab("interruptAgent", "reviewer"));
        Check("interrupted workers remain readable inline after leaving the live roster", reviewer.Status == "interrupted" && reviewer.IsAbandoned
            && chat.Items.Contains(reviewer) && reviewer.HasTranscript);
        Item(Collab("closeAgent", "tests"));
        Check("closed worker has a terminal state", tests.Status == "shutdown" && !tests.IsActive);

        // Published docs also describe this older singular shape. History takes the same translation path.
        Item(new()
        {
            ["type"] = "collabToolCall",
            ["id"] = "legacy-call",
            ["tool"] = "spawnAgent",
            ["status"] = "completed",
            ["newThreadId"] = "legacy",
            ["prompt"] = "Read the changelog.",
            ["agentStatus"] = "running"
        });
        Check("documented singular collaboration items also create visible workers", chat.Subagents.Any(a => a.ThreadId == "legacy" && a.IsActive));
        Item(new() { ["type"] = "collabToolCall", ["id"] = "legacy-close", ["tool"] = "closeAgent", ["status"] = "completed", ["receiverThreadId"] = "legacy" });
        Item(Collab("spawnAgent", "cancelled", "pendingInit"));
        Call(session, "PrepareInterruptFixture");
        Activity("cancelled", "started");
        Check("late startup cannot undo an explicit Stop", chat.Subagents.Single(a => a.ThreadId == "cancelled").Status == "interrupted");
        Item(new() { ["type"] = "commandExecution", ["id"] = "late-command", ["command"] = "dotnet test" }, "cancelled", complete: false);
        Check("late child tool events cannot revive a stopped worker", chat.Subagents.Single(a => a.ThreadId == "cancelled").Status == "interrupted");
        var failedSpawn = Collab("spawnAgent", "failed");
        failedSpawn["status"] = "failed";
        Item(failedSpawn);
        Check("a failed launch shows an error instead of a perpetual starting state", chat.Subagents.Single(a => a.ThreadId == "failed").Status == "errored"
            && chat.Items.OfType<SubagentItem>().Any(a => a.ThreadId == "failed"));
        Item(new() { ["type"] = "agentMessage", ["id"] = "first-child-event", ["text"] = "Child output arrived before the spawn acknowledgement." }, "early");
        var early = chat.Subagents.Single(a => a.ThreadId == "early");
        Item(Collab("spawnAgent", "early", "completed"));
        Check("out-of-order child events still yield one visible card", chat.Items.OfType<SubagentItem>().Count(a => a.ThreadId == "early") == 1 && early.HasTranscript);
        var copied = (string)typeof(MainWindow).GetMethod("ConversationText", Flags)!.Invoke(null, [chat])!;
        Check("copy conversation includes worker identity and output", copied.Contains("reviewer") && copied.Contains("packaging"));
        var (_, otherProviders) = Team("claude-subagent-no-duplicate", 1, "claude");
        Call(otherProviders[0], "IngestSdk", new JsonObject
        {
            ["type"] = "system",
            ["subtype"] = "subagent_update",
            ["agents"] = new JsonArray(new JsonObject
            { ["thread_id"] = "claude-child", ["label"] = "Reviewer", ["status"] = "running" }),
        });
        Check("providers with existing Agent tool cards do not get duplicate inline rows", otherProviders[0].Subagents.Count == 1
            && !otherProviders[0].Items.OfType<SubagentItem>().Any());
        Call(chat, "ForgetItem", early);
        Check("rewinding a worker card also removes its roster row", !chat.Subagents.Contains(early));
        VerifyCodexSubagentViews();
    }

    private static void VerifyCodexSubagentViews()
    {
        var resources = Application.Current.Resources.MergedDictionaries;
        var original = resources.ToArray();
        try
        {
            foreach (var theme in new[] { "Dark", "Borderless", "Cli" })
            {
                resources.Clear();
                resources.Add((ResourceDictionary)Application.LoadComponent(new Uri(
                    $"/VibeCode;component/Themes/{(theme == "Cli" ? "Cli" : "Dark")}.xaml", UriKind.Relative)));
                if (theme == "Borderless") resources.Add((ResourceDictionary)Application.LoadComponent(
                    new Uri("/VibeCode;component/Themes/Borderless.xaml", UriKind.Relative)));
                var (vm, peers) = Team("codex-subagents-view-" + theme, 2);
                var chat = peers[0];
                foreach (var peer in peers) { peer.Items.Clear(); peer.Status = "idle"; }
                chat.Title = "Review build";
                chat.Items.Add(new UserItem { Text = "Review the build and run the tests." });
                chat.Items.Add(new TextItem { Text = "I have a reviewer checking the package while a second worker runs the tests." });
                Call(chat, "IngestSdk", new JsonObject
                {
                    ["type"] = "system",
                    ["subtype"] = "subagent_update",
                    ["agents"] = new JsonArray(
                        new JsonObject
                        {
                            ["thread_id"] = "review",
                            ["label"] = "Package reviewer",
                            ["task"] = "Inspect the release package and document any defects.",
                            ["activity"] = "Checking the entry point and bundled dependencies",
                            ["status"] = "running"
                        },
                        new JsonObject
                        {
                            ["thread_id"] = "tests",
                            ["label"] = "Test runner",
                            ["task"] = "Run the build verification suite.",
                            ["activity"] = "All 33 tests passed",
                            ["status"] = "completed",
                            ["report"] = "All 33 tests passed."
                        },
                        new JsonObject
                        {
                            ["thread_id"] = "stopped",
                            ["label"] = "Earlier reviewer",
                            ["task"] = "Inspect the previous build.",
                            ["activity"] = "Stopped by request",
                            ["status"] = "interrupted"
                        }),
                });
                var reviewer = chat.Subagents.Single(a => a.ThreadId == "review");
                reviewer.Transcript.Add(new TextItem { Text = "The entry point opens successfully. I am checking the remaining dependencies and recording the results." });
                Property(vm, "ActiveChat", chat);
                Property(vm, "ShowBridge", false);
                var shell = (MainWindow)typeof(MainWindow).GetConstructors(Flags)
                    .Single(c => c.GetParameters().Length == 3).Invoke([vm, null, true]);
                shell.WindowStartupLocation = WindowStartupLocation.Manual;
                shell.Left = -30000; shell.Top = -30000; shell.ShowInTaskbar = false; shell.ShowActivated = false;
                shell.Width = 1100; shell.Height = 850;
                try
                {
                    shell.Show();
                    shell.UpdateLayout();
                    Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                    var toggle = Descendants<ToggleButton>(shell).Single(t => t.Name == "SubagentCardToggle" && t.IsVisible && ReferenceEquals(t.DataContext, reviewer));
                    Check(theme + " native child card is visible in an ordinary chat", toggle.ActualWidth > 100);
                    Check(theme + " card has an accessible expand control", AutomationProperties.GetName(toggle).Contains("Package reviewer"));
                    var body = Descendants<ContentControl>(shell).First(c => c.Name == "SubagentTranscriptBody" && ReferenceEquals(c.DataContext, reviewer));
                    Check(theme + " folded card does not instantiate child transcript controls", body.Template is null);
                    toggle.IsChecked = true;
                    shell.UpdateLayout();
                    Check(theme + " opening a card displays its live transcript", reviewer.Expanded && body.Template is not null
                        && Descendants<ItemsControl>(body).Any(c => ReferenceEquals(c.ItemsSource, reviewer.Transcript)));
                    CaptureSubagentShell(shell, 1100, 850, theme.ToLowerInvariant() + "-subagents-chat.png");
                    toggle.IsChecked = false;
                    shell.UpdateLayout();
                    Check(theme + " folding releases the controls and preserves output", body.Template is null && reviewer.Transcript.Count == 1);
                    reviewer.Expanded = true;
                    vm.SetBridgeTerminalMode(false);
                    Property(vm, "ShowBridge", true);
                    CaptureSubagentShell(shell, 1380, 850, theme.ToLowerInvariant() + "-subagents-bridge.png");
                    Check(theme + " Bridge panes render the same worker card", Descendants<ToggleButton>(shell).Any(t => t.Name == "SubagentCardToggle"
                        && t.IsVisible && ReferenceEquals(t.DataContext, reviewer)));
                    Check(theme + " a stopped worker remains inspectable in the conversation", Descendants<TextBlock>(shell).Any(t => t.IsVisible && t.Text == "Earlier reviewer"));
                    vm.SetBridgeTerminalMode(true);
                    shell.UpdateLayout();
                    Check(theme + " shared Bridge terminal also visualizes workers", Descendants<ToggleButton>(shell).Any(t => t.Name == "SubagentCardToggle"
                        && t.IsVisible && ReferenceEquals(t.DataContext, reviewer)));
                }
                finally { shell.Close(); }
            }
        }
        finally
        {
            resources.Clear();
            foreach (var resource in original) resources.Add(resource);
        }
    }

    private static void CaptureSubagentShell(Window shell, int width, int height, string name)
    {
        shell.Width = width; shell.Height = height;
        shell.UpdateLayout();
        // Text cards fade in over 120 ms. Keep dispatching frames before recording the settled UI.
        var readyAt = DateTime.UtcNow.AddMilliseconds(250);
        PumpUntil(() => DateTime.UtcNow >= readyAt);
        CaptureShell(shell, width, height, name);
    }
}
