using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using VibeCode.AgentStatus.Mcp.Bridge;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    private static void VerifyPeerTranscripts()
    {
        var (vm, team) = Team("peer-transcript", 3);
        var caller = team[0];
        var peer = team[1];
        peer.Items.Clear();
        var original = new string('a', BridgeChatReader.PageCharacters - "Oldest question".Length - 1)
            + char.ConvertFromUtf32(0x1F680) + new string('z', 47000) + "END OF LONG REPLY";
        peer.Items.Add(new UserItem { Text = "Oldest question" });
        var streaming = new TextItem { Text = original, Streaming = true };
        peer.Items.Add(streaming);
        var group = new CompactToolGroupItem(CompactToolGroupItem.BashKind, true);
        var output = new string('t', 43000) + "END OF TOOL OUTPUT";
        var tool = new ToolItem { Id = "shell", Name = "Bash", Input = new JsonObject { ["command"] = "fixture command" }, Result = output, Status = "done" };
        tool.Children.Add(new TextItem { Text = "PRIVATE CHILD TRANSCRIPT" });
        group.Add(tool);
        peer.Items.Add(group);
        for (var i = 0; i < 650; i++) peer.Items.Add(new TextItem { Text = "Historical reply " + i });
        peer.Items.Add(new BannerItem { Level = "info", Text = "Visible system notice" });
        peer.Items.Add(new ThinkingItem { Text = "HIDDEN REASONING" });
        peer.Items.Add(new QueuedItem { Owner = peer, Text = "UNSENT QUEUED PROMPT" });
        peer.Items.Add(new UserItem { Text = "CHILD USER PROMPT", FromSubagent = true });
        peer.Status = "running";

        var first = Tool(caller, "bridge_read_chat", new() { ["agent"] = "Codex 2" });
        Check("reading starts at the oldest message, with the selected peer's stable identity",
            first["agent_id"]!.ToString() == peer.BridgeAgentId && first["messages"]![0]!["text"]!.ToString() == "Oldest question"
            && first["has_more"]!.GetValue<bool>() && first["message_count"]!.GetValue<int>() == 654);
        var firstCursor = first["next_cursor"]!.ToString();
        streaming.Text = "Changed while reading";
        peer.Items.Insert(0, new UserItem { Text = "Inserted during read" });
        peer.Items.Add(new TextItem { Text = "New live reply" });
        var pages = ReadTranscriptPages(caller, first);
        var messages = AssembleTranscript(pages);
        Check("all 654 snapshot messages survive paging, including history beyond 400 messages",
            messages.Count == 654 && messages[0] == "Oldest question" && messages[652] == "Historical reply 649"
            && messages[653] == "Visible system notice");
        Check("long replies and emoji survive page boundaries exactly", messages[1] == original);
        Check("collapsed tool input and the entire long output are included",
            messages[2].Contains("fixture command") && messages[2].EndsWith(output)
            && pages.SelectMany(p => p["messages"]!.AsArray()).Any(m => m!["tool"]?.ToString() == "Bash"));
        Check("fixed snapshot pages are unaffected by live edits and insertions",
            pages.All(p => p["snapshot_id"]!.ToString() == first["snapshot_id"]!.ToString()
                && p["captured_at"]!.ToString() == first["captured_at"]!.ToString())
            && !messages.Values.Any(t => t.Contains("Changed while reading") || t.Contains("New live reply")));
        Check("each response is bounded while streaming state and transcript coverage are explicit",
            pages.All(p => p["messages"]!.AsArray().Count <= BridgeChatReader.PageMessages
                && p["messages"]!.AsArray().Sum(m => m!["text"]!.ToString().Length) <= BridgeChatReader.PageCharacters)
            && first["messages"]![1]!["in_progress"]!.GetValue<bool>() && first["coverage"]!.ToString().Contains("Hidden reasoning"));
        Check("hidden reasoning, drafts and nested child conversations are not exported",
            !string.Join("\n", messages.Values).Contains("HIDDEN REASONING")
            && !string.Join("\n", messages.Values).Contains("UNSENT QUEUED PROMPT")
            && !string.Join("\n", messages.Values).Contains("PRIVATE CHILD TRANSCRIPT")
            && !string.Join("\n", messages.Values).Contains("CHILD USER PROMPT"));
        var repeat = Tool(caller, "bridge_read_chat", new() { ["agent"] = peer.BridgeAgentId, ["cursor"] = firstCursor });
        Check("retrying a cursor returns the same page", JsonNode.DeepEquals(repeat, pages[1]));
        var fresh = Tool(caller, "bridge_read_chat", new() { ["agent"] = "2" });
        Check("omitting cursor explicitly refreshes the transcript", fresh["snapshot_id"]!.ToString() != first["snapshot_id"]!.ToString()
            && fresh["messages"]![0]!["text"]!.ToString() == "Inserted during read");
        var roster = Tool(caller, "bridge_list_agents")["agents"]!.AsArray();
        var address = roster.Single(a => a!["agent_id"]!.ToString() == peer.BridgeAgentId)!["message_recipient"]!.ToString();
        Check("short roster addresses select the same chat", Tool(caller, "bridge_read_chat", new() { ["agent"] = address })["agent_id"]!.ToString() == peer.BridgeAgentId);
        var empty = Tool(caller, "bridge_read_chat", new() { ["agent"] = team[2].BridgeAgentId });
        Check("an empty conversation is a complete empty transcript", empty["messages"]!.AsArray().Count == 0
            && !empty["has_more"]!.GetValue<bool>() && empty["next_cursor"] is null);
        Check("reads neither message nor interrupt the active owner", Session(peer).Sent.Count == 0
            && Session(peer).Interrupts == 0 && peer.Status == "running" && peer.UnreadPeerMessageCount == 0);

        Reject("an agent is required", () => Tool(caller, "bridge_read_chat"));
        Reject("caller-controlled file paths are not accepted", () => Tool(caller, "bridge_read_chat", new() { ["agent"] = address, ["path"] = "outside.json" }));
        Reject("invalid cursors are explicit errors", () => Tool(caller, "bridge_read_chat", new() { ["agent"] = address, ["cursor"] = "bogus" }));
        Reject("a cursor cannot select a different chat", () => Tool(caller, "bridge_read_chat", new() { ["agent"] = team[2].BridgeAgentId, ["cursor"] = firstCursor }));
        Reject("a caller cannot use another caller's cursor", () => Tool(team[2], "bridge_read_chat", new() { ["agent"] = peer.BridgeAgentId, ["cursor"] = firstCursor }));
        var (_, outsider) = Team("unrelated-transcript", 1);
        Reject("a different Bridge's chat is inaccessible", () => Tool(caller, "bridge_read_chat", new() { ["agent"] = outsider[0].BridgeAgentId }));
        var ordinary = ProjectChat(vm, peer.Cwd, "Ordinary chat", steerable: false);
        Reject("ordinary same-project chats cannot read Bridge transcripts", () => Tool(ordinary, "bridge_read_chat", new() { ["agent"] = peer.BridgeAgentId }));
        Reject("a Bridge cannot read an ordinary chat's transcript", () => Tool(caller, "bridge_read_chat", new() { ["agent"] = ordinary.BridgeAgentId }));
        var traffic = typeof(MainViewModel).GetField("_peerTraffic", Flags)!;
        var previousTraffic = traffic.GetValue(vm);
        traffic.SetValue(vm, new PeerTrafficLedger());
        Reject("cursors stay with their original Bridge even when agent identities are reused", () => Tool(caller, "bridge_read_chat", new() { ["agent"] = peer.BridgeAgentId, ["cursor"] = firstCursor }));
        traffic.SetValue(vm, previousTraffic);
        peer.Status = "closed";
        Reject("membership is rechecked before continuing a departed chat", () => Tool(caller, "bridge_read_chat", new() { ["agent"] = peer.BridgeAgentId, ["cursor"] = firstCursor }));
        peer.Status = "running";

        var pipe = (string)Property(Call(caller, "EnsureBridgeMcp")!, "PipeName")!;
        var wire = Task.Run(() => BridgeMcpClient.InvokeAsync(pipe, "bridge_read_chat", new() { ["agent"] = address }));
        PumpUntil(() => wire.IsCompleted);
        Check("the real named-pipe endpoint serves transcript pages", wire.GetAwaiter().GetResult()["messages"]![0]!["text"]!.ToString() == "Inserted during read");
        var definition = BridgeMcpTools.Create((_, _) => new()).Single(t => t.Name == "bridge_read_chat").Definition;
        Check("the MCP contract advertises a read-only transcript tool with full-read guidance",
            definition["annotations"]!["readOnlyHint"]!.GetValue<bool>()
            && definition["inputSchema"]!["required"]!.AsArray().Single()!.ToString() == "agent"
            && BridgeMcpTools.Instructions.Contains("until has_more=false"));

        VerifyTranscriptCursorLifetime();
        VerifyTranscriptMirrorGroups(vm, peer, group, tool);
        VerifyResumedPeerTranscript();
        var codexResume = new ChatViewModel(peer.Cwd, resume: Guid.NewGuid().ToString("N"), provider: "codex") { BridgeLabel = "Codex 4" };
        Chats.Add(codexResume); vm.Chats.Add(codexResume); vm.BridgePanes.Add(codexResume); Call(vm, "Track", codexResume);
        codexResume.Items.Add(new TextItem { Text = "Restored Codex conversation" });
        Reject("a Codex resume is held until its provider finishes history hydration",
            () => Tool(caller, "bridge_read_chat", new() { ["agent"] = codexResume.BridgeAgentId }));
        typeof(ChatViewModel).GetField("_bridgeSessionInitialized", Flags)!.SetValue(codexResume, true);
        Check("completed Codex hydration becomes readable without starting a new turn",
            Tool(caller, "bridge_read_chat", new() { ["agent"] = codexResume.BridgeAgentId })["messages"]![0]!["text"]!.ToString() == "Restored Codex conversation");
    }

    private static List<JsonObject> ReadTranscriptPages(ChatViewModel caller, JsonObject first)
    {
        var pages = new List<JsonObject> { first };
        while (pages[^1]["has_more"]!.GetValue<bool>())
        {
            if (pages.Count >= 200) throw new Exception("Transcript did not finish paging.");
            var previous = pages[^1];
            // Round-trip through JSON to catch loss from splitting Unicode pairs across responses.
            pages.Add(JsonNode.Parse(Tool(caller, "bridge_read_chat", new()
            {
                ["agent"] = first["agent_id"]!.ToString(),
                ["cursor"] = previous["next_cursor"]!.ToString(),
            }).ToJsonString())!.AsObject());
        }
        return pages;
    }

    private static Dictionary<int, string> AssembleTranscript(IEnumerable<JsonObject> pages)
    {
        var bodies = new Dictionary<int, StringBuilder>();
        foreach (var row in pages.SelectMany(p => p["messages"]!.AsArray()).Select(m => m!.AsObject()))
        {
            var index = row["index"]!.GetValue<int>();
            if (!bodies.TryGetValue(index, out var body)) bodies[index] = body = new();
            if (body.Length != row["text_offset"]!.GetValue<int>()) throw new Exception("Skipped or repeated transcript text.");
            body.Append(row["text"]!.ToString());
            if (row["message_complete"]!.GetValue<bool>() && body.Length != row["text_length"]!.GetValue<int>())
                throw new Exception("Incomplete message reported complete.");
        }
        return bodies.ToDictionary(p => p.Key, p => p.Value.ToString());
    }

    private static void VerifyTranscriptCursorLifetime()
    {
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid().ToString("N");
        var snapshot = new BridgeChatSnapshot(id, 1, "claude", "Fixture", null, "idle", true, now,
            [new(0, "assistant", new string('x', 40000))]);
        var reader = new BridgeChatReader();
        var first = reader.Read(id, null, () => snapshot, now);
        var cursor = first["next_cursor"]!.ToString();
        Reject("overflowing cursor offsets are rejected", () => reader.Read(id, first["snapshot_id"] + ":0:999999999999", () => snapshot, now));
        Reject("negative cursor indexes are rejected", () => reader.Read(id, first["snapshot_id"] + ":-1:0", () => snapshot, now));
        Reject("out-of-bounds cursor offsets are rejected", () => reader.Read(id, first["snapshot_id"] + ":0:90000", () => snapshot, now));
        Reject("expired snapshots are not silently replaced with a different conversation", () => reader.Read(id, cursor, () => snapshot, now.AddMinutes(31)));
        first = reader.Read(id, null, () => snapshot, now.AddMinutes(32));
        for (var i = 0; i < 4; i++) reader.Read(id, null, () => snapshot, now.AddMinutes(33 + i));
        Reject("retention is bounded and evicted snapshots fail explicitly", () => reader.Read(id, first["next_cursor"]!.ToString(), () => snapshot, now.AddMinutes(38)));
    }

    private static void VerifyTranscriptMirrorGroups(MainViewModel vm, ChatViewModel peer, CompactToolGroupItem group, ToolItem tool)
    {
        Call(vm, "RefreshBridgeChatAccess", peer);
        var mirror = Property(peer, "PeerChatMirror")!;
        var store = (BridgeChatStore)Property(mirror, "Store")!;
        Call(mirror, "Flush");
        store.PendingWrites.GetAwaiter().GetResult();
        var timer = (DispatcherTimer)mirror.GetType().GetField("_timer", Flags)!.GetValue(mirror)!;
        tool.Result = "Updated grouped tool output";
        Check("updating a tool inside a collapsed group schedules the archive", timer.IsEnabled);
        Call(mirror, "Flush");
        group.Add(new ToolItem { Id = "later", Name = "Bash", Result = "Late grouped tool", Status = "done" });
        Check("adding a tool to an existing group schedules the archive", timer.IsEnabled);
        Call(mirror, "Flush");
        store.PendingWrites.GetAwaiter().GetResult();
        var archive = JsonNode.Parse(File.ReadAllText(Path.Combine(store.DirectoryPath, peer.BridgeId + ".json")))!;
        var messages = archive["chat"]!["messages"]!.AsArray();
        Check("the existing archive also contains current grouped tool results",
            messages.Any(m => m!["text"]!.ToString().Contains("Updated grouped tool output"))
            && messages.Any(m => m!["text"]!.ToString().Contains("Late grouped tool")));
        group.Tools.Clear();
        Call(mirror, "Flush");
        tool.Result = "Removed tool output";
        Check("clearing a group detaches its previous tool subscriptions", !timer.IsEnabled);
        group.Add(tool);
        Call(mirror, "Flush");
        peer.Items.Remove(group);
        Call(mirror, "Flush");
        tool.Result = "Detached group output";
        Check("removing a whole group detaches its tool subscriptions", !timer.IsEnabled);
    }

    private static void VerifyResumedPeerTranscript()
    {
        var (vm, team) = Team("resumed-peer-transcript", 1, "claude");
        var fixtureHome = Path.Combine(team[0].Cwd, "provider-fixture");
        var project = Path.Combine(fixtureHome, "projects", "fixture");
        Directory.CreateDirectory(project);
        var sessionId = Guid.NewGuid().ToString("N");
        File.WriteAllLines(Path.Combine(project, sessionId + ".jsonl"), Enumerable.Range(0, 901).Select(i =>
            new JsonObject { ["type"] = "user", ["message"] = new JsonObject { ["role"] = "user", ["content"] = "Restored user message " + i } }.ToJsonString()));
        var oldHome = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        var oldAccounts = Environment.GetEnvironmentVariable("VIBECODE_CLAUDE_ACCOUNT_STORE");
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", fixtureHome);
        Environment.SetEnvironmentVariable("VIBECODE_CLAUDE_ACCOUNT_STORE", Path.Combine(fixtureHome, "empty-accounts"));
        try
        {
            var peer = new ChatViewModel(team[0].Cwd, resume: sessionId, provider: "claude") { BridgeLabel = "Claude 2" };
            Chats.Add(peer); vm.Chats.Add(peer); vm.BridgePanes.Add(peer); Call(vm, "Track", peer);
            Reject("a partially hydrated resumed chat is not presented as a whole transcript",
                () => Tool(team[0], "bridge_read_chat", new() { ["agent"] = peer.BridgeAgentId }));
            // Cancel only the provider-start step after the last fixture row has replayed. No CLI is launched.
            peer.Items.CollectionChanged += (_, _) =>
            {
                if (peer.Items.OfType<UserItem>().LastOrDefault()?.Text == "Restored user message 900")
                    typeof(ChatViewModel).GetField("_startVersion", Flags)!.SetValue(peer, 1);
            };
            var replay = (Task)Call(peer, "ReplayTranscriptAndStartAsync", 0, sessionId)!;
            PumpUntil(() => replay.IsCompleted);
            replay.GetAwaiter().GetResult();
            Check("resumed Claude history loads all 901 messages instead of stopping at 400", peer.Items.OfType<UserItem>().Count() == 901);
            typeof(ChatViewModel).GetField("_transcriptLoaded", Flags)!.SetValue(peer, true);
            var first = Tool(team[0], "bridge_read_chat", new() { ["agent"] = peer.BridgeAgentId });
            var restored = AssembleTranscript(ReadTranscriptPages(team[0], first));
            Check("the transcript tool returns the complete restored conversation in order",
                restored.Count == 901 && restored[0] == "Restored user message 0" && restored[900] == "Restored user message 900");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", oldHome);
            Environment.SetEnvironmentVariable("VIBECODE_CLAUDE_ACCOUNT_STORE", oldAccounts);
        }
    }
}
