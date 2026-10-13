using System.IO;
using System.Text.Json.Nodes;
using VibeCode.AgentStatus.Mcp;
using VibeCode.AgentStatus.Mcp.Bridge;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    private static ChatViewModel ProjectChat(MainViewModel vm, string path, string title, bool steerable = true)
    {
        Directory.CreateDirectory(path);
        var chat = new ChatViewModel(path, provider: "claude") { Title = title };
        typeof(ChatViewModel).GetField("_session", Flags)!.SetValue(chat, steerable ? new SteerableSession() : new FakeSession());
        typeof(ChatViewModel).GetField("_bridgeSessionInitialized", Flags)!.SetValue(chat, true);
        Chats.Add(chat);
        vm.Chats.Add(chat);
        Call(vm, "Track", chat);
        chat.Status = "idle";
        return chat;
    }

    private static JsonObject ProjectSend(ChatViewModel sender, ChatViewModel target, string body, bool urgent = false) =>
        Tool(sender, "bridge_send_message", urgent
            ? new() { ["recipient"] = target.BridgeAgentId, ["message"] = body, ["priority"] = "urgent", ["reason"] = "Our edits overlap on the same function." }
            : new() { ["recipient"] = target.BridgeAgentId, ["message"] = body });

    private static void VerifyProjectChats()
    {
        var vm = new MainViewModel();
        var root = Path.Combine(_root, "project-peers-" + Guid.NewGuid().ToString("N"));
        var first = ProjectChat(vm, root, "Fix model recovery");
        var second = ProjectChat(vm, root + Path.DirectorySeparatorChar, "Add project awareness");
        var outsider = ProjectChat(vm, root + "-other", "Unrelated task");
        var firstSession = Steerable(first);
        var secondSession = Steerable(second);
        var roster = Tool(first, "bridge_list_agents");
        Check("standalone same-project chats discover each other without joining a bridge",
            roster["joined"]!.GetValue<bool>() == false && roster["scope"]!.ToString() == "project" && roster["agents"]!.AsArray().Count == 2);
        var pipe = (string)Property(Call(first, "EnsureBridgeMcp")!, "PipeName")!;
        var wireRoster = Task.Run(() => BridgeMcpClient.InvokeAsync(pipe, "bridge_list_agents", new()));
        PumpUntil(() => wireRoster.IsCompleted);
        Check("the actual MCP endpoint exposes ordinary project peers", wireRoster.GetAwaiter().GetResult()["agents"]!.AsArray().Count == 2);
        Reject("ordinary messages cannot cross projects", () => ProjectSend(first, outsider, "Do not deliver"));
        Reject("caller identity cannot be spoofed", () => Tool(first, "bridge_send_message", new()
        { ["recipient"] = second.BridgeAgentId, ["message"] = "test", ["sender_id"] = outsider.BridgeAgentId }));
        Reject("project chats cannot broadcast", () => Tool(first, "bridge_broadcast", new()
        { ["message"] = "test", ["affected_agent_ids"] = new JsonArray("a", "b", "c"), ["reason"] = "test" }));
        Reject("project chats cannot use shared planning", () => Tool(first, "bridge_list_tasks"));
        Reject("oversized project messages are refused", () => ProjectSend(first, second, new string('x', 1201)));
        Reject("invalid urgency is refused by the MCP schema", () => Tool(first, "bridge_send_message", new()
        { ["recipient"] = second.BridgeAgentId, ["message"] = "test", ["priority"] = "routine" }));

        first.Status = "running";
        Call(second, "SendNow", "Add project awareness", null, false);
        PumpUntil(() => secondSession.Sent.Count == 1);
        Check("a new active peer is announced inside the existing turn", firstSession.Steers.Count == 1 && firstSession.Sent.Count == 0 && firstSession.Interrupts == 0);
        Check("ordinary prompts automatically carry current project peers and anti-chatter rules",
            secondSession.Sent[0].Contains("[VIBECODE PROJECT CHATS]") && secondSession.Sent[0].Contains(first.BridgeAgentId)
            && secondSession.Sent[0].Contains("do not delegate"));
        Check("peer notices do not become user bubbles", first.Items.OfType<UserItem>().Count() == 0);
        Call(vm, "BeginProjectChatTurn", second);
        Check("presence notices are sent once per pair", firstSession.Steers.Count == 1);
        var status = Tool(second, "bridge_agent_status", new() { ["agent"] = first.BridgeAgentId });
        Check("peer activity comes from live runtime status", status["working"]!.GetValue<bool>() && status["state"]!.ToString() == "working");

        var beforeNotice = secondSession.Steers.Count;
        var sent = ProjectSend(first, second, "I am updating Claude recovery; you can keep working on project messaging.");
        Check("heads-up delivery preserves both active turns", secondSession.Steers.Count == beforeNotice + 1
            && firstSession.Interrupts == 0 && secondSession.Interrupts == 0 && secondSession.Sent.Count == 1);
        Check("delivery is retained and stays unread until read explicitly", second.UnreadPeerMessageCount == 1);
        var messageId = sent["delivered"]![0]!["message_id"]!.ToString();
        Reject("sender cannot mark its outgoing message answered", () => Tool(first, "bridge_mark_message", new() { ["message_id"] = messageId }));
        var inbox = Tool(second, "bridge_read_messages")["messages"]!.AsArray();
        Check("inbox keeps authenticated sender identity and marks only incoming reads",
            inbox.Count == 1 && inbox[0]!["sender_id"]!.ToString() == first.BridgeAgentId && second.UnreadPeerMessageCount == 0);
        Tool(second, "bridge_mark_message", new() { ["message_id"] = messageId });
        Reject("a second routine heads-up to the same peer is blocked", () => ProjectSend(first, second, "Thanks again"));
        first.Send("Queued follow-up");
        Check("user follow-ups stay queued while work runs", (int)Property(first, "QueuedPromptCount")! == 1);
        Reject("queueing a user prompt does not reset message limits", () => ProjectSend(first, second, "Another routine reply"));
        Reject("urgent messages require a reason", () => Tool(first, "bridge_send_message", new()
        { ["recipient"] = second.BridgeAgentId, ["message"] = "Collision", ["priority"] = "urgent" }));
        ProjectSend(first, second, "The recovery method changed; re-read before applying your pending patch.", true);
        ProjectSend(first, second, "The shared test helper also changed; your current replacement would remove it.", true);
        Reject("urgent exceptions stay bounded", () => ProjectSend(first, second, "One more emergency", true));

        var third = ProjectChat(vm, root, "Idle recipient");
        var thirdSession = Steerable(third);
        ProjectSend(second, third, "My edits touch only the project registry.");
        Settle();
        Check("idle recipients keep their messages without waking a model",
            thirdSession.Sent.Count == 0 && thirdSession.Steers.Count == 0 && thirdSession.Interrupts == 0 && third.Status == "idle");
        Call(third, "SendNow", "Continue my own task", null, false);
        PumpUntil(() => thirdSession.Sent.Count == 1);
        var nextWire = WireText(JsonNode.Parse(thirdSession.Sent[0].Replace("\n", "\\n"))!);
        Check("next user turn exposes the unread inbox automatically", nextWire.Contains("\"unread_messages\":1"));
        Check("project context is hidden on transcript replay", (string)Call(third, "StripInjectedPrelude", nextWire)! == "Continue my own task");
        var nonSteer = ProjectChat(vm, root, "Non-steerable recipient", false);
        nonSteer.Status = "running";
        ProjectSend(second, nonSteer, "I am changing the messaging registry only.");
        Check("providers without steering retain messages without interrupt or restart", nonSteer.UnreadPeerMessageCount == 1
            && Session(nonSteer).Sent.Count == 0 && Session(nonSteer).Interrupts == 0);
        AppSettings.Current.BridgePeerMessaging = false;
        Reject("messaging pause is respected", () => ProjectSend(third, second, "Paused"));
        AppSettings.Current.BridgePeerMessaging = true;

        Tool(second, "bridge_report_activity", new() { ["summary"] = "Updating independent chat notifications", ["task_name"] = "Project notifications" });
        Check("task summaries update without renaming a permanent chat", second.Title == "Add project awareness"
            && Tool(first, "bridge_agent_status", new() { ["agent"] = second.BridgeAgentId })["summary"]!.ToString().Contains("notifications"));
        var edited = Path.Combine(root, "shared.txt");
        File.WriteAllText(edited, "new value\n");
        Call(second, "NoteBridgeEditCall", new JsonObject
        {
            ["id"] = "project-edit",
            ["name"] = "Edit",
            ["input"] = new JsonObject
            { ["file_path"] = edited, ["old_string"] = "old value", ["new_string"] = "new value" }
        });
        Call(second, "SettleBridgeEditCall", "project-edit", true, false, null);
        if (!BridgeEditLedger.PendingWrites.Wait(10_000)) throw new TimeoutException("Project edit log did not finish.");
        var edits = Tool(first, "bridge_file_edits", new() { ["path"] = "shared.txt" });
        Check("successful ordinary-chat edits are visible to other project chats", edits["edits"]!.AsArray().Any(e => e!["agent_id"]!.ToString() == second.BridgeAgentId));
        Reject("file range lookups require a path", () => Tool(first, "bridge_file_edits", new() { ["start_line"] = 1 }));
        second.Close();
        Reject("closed peers cannot receive messages", () => ProjectSend(third, second, "stale recipient"));
        Check("closed chats leave the live project roster", Tool(first, "bridge_list_agents")["agents"]!.AsArray().All(p => p!["agent_id"]!.ToString() != second.BridgeAgentId));
        vm.BridgePanes.Add(third);
        Check("ordinary rosters exclude chats that join a bridge", Tool(first, "bridge_list_agents")["agents"]!.AsArray().All(p => p!["agent_id"]!.ToString() != third.BridgeAgentId));
        Check("bridge rosters preserve their own isolated membership", Tool(third, "bridge_list_agents")["joined"]!.GetValue<bool>());
        Reject("Bridge callers cannot reach an independent chat", () => ProjectSend(third, first, "wrong scope"));
        vm.BridgePanes.Remove(third);
        var retained = Tool(third, "bridge_read_messages")["messages"]!.AsArray();
        Check("returning from a Bridge restores the ordinary inbox without leaking its Bridge mailbox",
            retained.Any(m => m!["sender_id"]!.ToString() == second.BridgeAgentId));
        var scratch = new ChatViewModel(NoDirectoryWorkspace.Folder, provider: "claude");
        Chats.Add(scratch); vm.Chats.Add(scratch); Call(vm, "Track", scratch);
        Check("no-directory scratch chats do not discover unrelated conversations",
            Tool(scratch, "bridge_list_agents")["scope"]!.ToString() == "none" && Call(vm, "ProjectChatContext", scratch) is null);
        VerifyProjectChatRateLimits();
    }

    private static void VerifyProjectChatRateLimits()
    {
        var vm = new MainViewModel();
        var root = Path.Combine(_root, "project-message-limits-" + Guid.NewGuid().ToString("N"));
        var sender = ProjectChat(vm, root, "Sender");
        var target = ProjectChat(vm, root, "Recipient");
        ProjectSend(sender, target, "Updating the parser.");
        ProjectSend(sender, target, "Parser conflict at line 10.", true);
        ProjectSend(sender, target, "Parser conflict at line 20.", true);
        Call(sender, "SendAutomaticContinuation", "Continue the existing task");
        PumpUntil(() => Steerable(sender).Sent.Count == 1);
        Reject("automatic continuation does not reset the user-turn message budget", () => ProjectSend(sender, target, "Another heads-up"));
        Call(sender, "IngestSdk", new JsonObject { ["type"] = "result", ["subtype"] = "success", ["result"] = "Done", ["is_error"] = false });
        Call(sender, "SendNow", "Review the new parser changes", null, false);
        PumpUntil(() => Steerable(sender).Sent.Count == 2);
        Reject("duplicate detection survives a new user turn and changed urgency reason",
            () => ProjectSend(sender, target, "Updating the parser.", true));
        ProjectSend(sender, target, "I am now updating parser tests.");
        Check("a genuinely dispatched user turn gets a fresh heads-up slot", target.UnreadPeerMessageCount == 4);
        Reject("the cross-turn five-minute limit blocks further urgent traffic",
            () => ProjectSend(sender, target, "A fifth conflicting location.", true));
        var page = Tool(target, "bridge_read_messages", new() { ["limit"] = 1 });
        Check("reading a page marks only that incoming page read", page["has_more"]!.GetValue<bool>() && target.UnreadPeerMessageCount == 3);
        var older = Tool(target, "bridge_read_messages", new() { ["before"] = page["next_cursor"]!.ToString(), ["limit"] = 3 });
        Check("ordinary inbox pagination reaches retained older messages", older["messages"]!.AsArray().Count == 3 && target.UnreadPeerMessageCount == 0);
    }
}
