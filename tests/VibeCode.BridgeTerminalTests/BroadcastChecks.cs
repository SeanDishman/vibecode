using System.Text.Json.Nodes;
using System.Windows.Threading;
using VibeCode.AgentStatus.Mcp.Bridge;
using VibeCode.Protocol;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    private static JsonObject BroadcastArgs(string message, params ChatViewModel[] affected) => new()
    {
        ["message"] = message,
        ["affected_agent_ids"] = new JsonArray(affected.Select(p => (JsonNode?)JsonValue.Create(p.BridgeAgentId)).ToArray()),
        ["reason"] = "These peers must update their shared schema dependencies to continue their assigned work.",
    };

    private static void VerifyBroadcastMessaging()
    {
        VerifyShortMessageAddresses();
        var (_, team) = Team("broadcast-normal", 6);
        foreach (var agent in team) { agent.Status = "idle"; agent.Draft = "Preserved user draft"; }
        var sender = team[0];
        var roster = Tool(sender, "bridge_list_agents")["agents"]!.AsArray();
        Check("roster identifies messageable peers and excludes self", !roster[0]!["can_message"]!.GetValue<bool>() && roster.Skip(1).All(p => p!["can_message"]!.GetValue<bool>()));
        var (_, outsiders) = Team("broadcast-other-roster", 1);
        outsiders[0].Status = "idle";
        Reject("broadcast rejects one affected peer", () => Tool(sender, "bridge_broadcast", BroadcastArgs("One peer", team[1])));
        Reject("broadcast rejects exactly two affected peers", () => Tool(sender, "bridge_broadcast", BroadcastArgs("Two peers", team[1], team[2])));
        Reject("broadcast rejects duplicate audience IDs", () => Tool(sender, "bridge_broadcast", BroadcastArgs("Padded audience", team[1], team[1], team[2])));
        Reject("broadcast cannot count the sender", () => Tool(sender, "bridge_broadcast", BroadcastArgs("Self audience", sender, team[1], team[2])));
        Reject("broadcast rejects cross-roster audience", () => Tool(sender, "bridge_broadcast", BroadcastArgs("Wrong bridge", outsiders[0], team[1], team[2])));
        var missingReason = BroadcastArgs("Missing justification", team[1], team[2], team[3]); missingReason.Remove("reason");
        Reject("broadcast requires its importance reason", () => Tool(sender, "bridge_broadcast", missingReason));
        var spoof = BroadcastArgs("Spoofed sender", team[1], team[2], team[3]); spoof["sender_id"] = team[1].BridgeAgentId;
        Reject("broadcast cannot spoof caller identity", () => Tool(sender, "bridge_broadcast", spoof));
        Reject("legacy recipient=all cannot bypass broadcast policy", () => Tool(sender, "bridge_send_message", new() { ["recipient"] = "all", ["message"] = "Bypass" }));
        team[3].Status = "error";
        Reject("unavailable peers cannot pad the audience", () => Tool(sender, "bridge_broadcast", BroadcastArgs("Unavailable", team[1], team[2], team[3])));
        team[3].Status = "idle";
        Check("rejected broadcasts deliver nothing", team.Skip(1).All(p => p.UnreadPeerMessageCount == 0));
        var message = BroadcastArgs("Schema v2 is required for all current consumers.", team[1], team[2], team[3]);
        AppSettings.Current.BridgePeerMessaging = false;
        try
        {
            Reject("broadcast honors messaging pause", () => Tool(sender, "bridge_broadcast", message));
            Check("paused roster exposes that no peer can be messaged", Tool(sender, "bridge_list_agents")["agents"]!.AsArray().All(p => !p!["can_message"]!.GetValue<bool>()));
        }
        finally { AppSettings.Current.BridgePeerMessaging = true; }
        var receipt = Tool(sender, "bridge_broadcast", message);
        Check("three affected peers permits broadcast to all five peers", receipt["delivered"]!.AsArray().Count == 5 && receipt["failures"]!.AsArray().Count == 0);
        Check("broadcast receipt retains its reason and affected audience", receipt["affected_agent_ids"]!.AsArray().Count == 3 && receipt["reason"]!.ToString().Length > 0);
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Check("broadcast does not wake idle workers or overwrite drafts", team.Skip(1).All(p => Session(p).Sent.Count == 0 && p.Draft == "Preserved user draft" && p.UnreadPeerMessageCount == 1));
        Check("broadcast excludes outsiders", outsiders[0].UnreadPeerMessageCount == 0);
        Reject("repeating a broadcast is deduplicated", () => Tool(sender, "bridge_broadcast", message));
        foreach (var peer in team.Skip(1))
        {
            var incoming = Tool(peer, "bridge_read_messages")["messages"]!.AsArray().Single()!;
            Check("broadcast inbox has authenticated sender " + peer.BridgeLabel, incoming["sender_id"]!.ToString() == sender.BridgeAgentId && incoming["message"]!.ToString().Contains("Schema v2"));
            Tool(peer, "bridge_mark_message", new() { ["message_id"] = incoming["message_id"]!.ToString() });
        }
        team[5].Status = "error";
        var partial = Tool(sender, "bridge_broadcast", BroadcastArgs("Schema v3 requires a second consumer migration.", team[1], team[2], team[3]));
        Check("broadcast distinguishes skipped unavailable peers from delivery failures", partial["delivered"]!.AsArray().Count == 4 && partial["failures"]!.AsArray().Count == 0 && partial["skipped"]!.AsArray().Single()!["agent_id"]!.ToString() == team[5].BridgeAgentId);

        var (_, partialTeam) = Team("broadcast-partial-delivery", 4);
        foreach (var peer in partialTeam) peer.Status = "idle";
        Tool(partialTeam[0], "bridge_send_message", new() { ["recipient"] = partialTeam[1].BridgeAgentId, ["message"] = "Shared schema dependency changed." });
        var mixed = Tool(partialTeam[0], "bridge_broadcast", BroadcastArgs("Shared schema dependency changed.", partialTeam.Skip(1).ToArray()));
        Check("actual admission failures stay distinct from intentional skips", mixed["delivered"]!.AsArray().Count == 2 && mixed["failures"]!.AsArray().Count == 1 && mixed["skipped"]!.AsArray().Count == 0 && !mixed["delivery_complete"]!.GetValue<bool>());

        foreach (var central in new[] { false, true }) VerifyAdvancedBroadcast(central);

        var (_, pipeTeam) = Team("broadcast-mcp-pipe", 4);
        foreach (var peer in pipeTeam) peer.Status = "idle";
        var endpoint = Call(pipeTeam[0], "EnsureBridgeMcp")!;
        var pipe = (string)Property(endpoint, "PipeName")!;
        var send = BridgeMcpClient.InvokeAsync(pipe, "bridge_broadcast", BroadcastArgs("MCP broadcast transport check", pipeTeam.Skip(1).ToArray()));
        PumpUntil(() => send.IsCompleted);
        Check("named pipe MCP broadcast invokes the real delivery path", send.GetAwaiter().GetResult()["delivered"]!.AsArray().Count == 3 && pipeTeam.Skip(1).All(p => p.UnreadPeerMessageCount == 1));
        var definition = BridgeMcpTools.Create((_, _) => new()).Single(t => t.Name == "bridge_broadcast").Definition;
        Check("broadcast schema exposes the distinct three-peer threshold", definition["inputSchema"]!["properties"]!["affected_agent_ids"]!["minItems"]!.GetValue<int>() == 3 && definition["inputSchema"]!["properties"]!["affected_agent_ids"]!["uniqueItems"]!.GetValue<bool>());
        Check("broadcast is correctly marked as a non-idempotent write", !definition["annotations"]!["readOnlyHint"]!.GetValue<bool>() && !definition["annotations"]!["idempotentHint"]!.GetValue<bool>());
        var registration = (McpServerDefinition)Call(endpoint, "Registration")!;
        Check("Codex projection includes broadcast approval configuration", McpCatalog.BuildCodexProjection([registration]).ConfigOverrides.Any(s => s.Contains("bridge_broadcast") && s.Contains("approval_mode")));
        using var glm = new GlmSession(new GlmSessionOptions { Cwd = _root, ApiKeys = ["offline-fixture"], BridgeMcpPipe = pipe });
        Check("in-process provider exposes broadcast alongside stdio providers", ((JsonArray)Call(glm, "ToolSchema")!).Any(t => t?["function"]?["name"]?.ToString() == "bridge_broadcast"));
        var presentation = typeof(MainViewModel).Assembly.GetType("VibeCode.UI.BridgeToolPresentation")!;
        Check("broadcast has a readable tool activity label", (string?)presentation.GetMethod("DisplayName", Flags)!.Invoke(null, ["mcp__bridge__bridge_broadcast"]) == "Broadcast to bridge");
    }

    private static void VerifyAdvancedBroadcast(bool central)
    {
        var (_, team) = Team("broadcast-advanced-" + central, 6);
        foreach (var peer in team) peer.Status = "running";
        foreach (var manager in new[] { team[0], team[3] })
        { Property(manager, "IsBridgeManager", true); Property(manager, "BridgeCoordinatesOnly", true); }
        foreach (var index in new[] { 1, 2, 4, 5 }) Property(team[index], "BridgeCoordinatorAgentId", team[index < 3 ? 0 : 3].BridgeAgentId);
        Tool(team[0], "bridge_list_agents");
        var work = (BridgeWorkState)Property(team[0], "BridgeWork")!;
        work.DispatchStarted = true;
        if (central) work.CentralPlan = new BridgeCentralPlan { State = "completed" };
        var roster = Tool(team[0], "bridge_list_agents")["agents"]!.AsArray();
        Check("advanced roster exposes caller-specific lock before a send " + central, !roster[3]!["can_message"]!.GetValue<bool>() && roster[3]!["message_blocked_reason"]!.ToString().Contains("permanently closed") && roster[1]!["can_message"]!.GetValue<bool>());
        var workerReceipt = Tool(team[1], "bridge_broadcast", BroadcastArgs("Shared schema is blocking both worker groups.", team[2], team[4], team[5]));
        Check("advanced worker broadcast crosses groups " + central, workerReceipt["delivered"]!.AsArray().Count == 5);
        Reject("advanced broadcast cannot count a locked orchestrator " + central, () => Tool(team[0], "bridge_broadcast", BroadcastArgs("Attempted locked audience", team[1], team[2], team[3])));
        var managerReceipt = Tool(team[0], "bridge_broadcast", BroadcastArgs("All workers must adopt the shared schema.", team[1], team[2], team[4]));
        Check("advanced broadcast preserves orchestrator lock " + central, managerReceipt["delivered"]!.AsArray().Count == 4 && managerReceipt["failures"]!.AsArray().Count == 0 && managerReceipt["skipped"]!.AsArray().Single()!["agent_id"]!.ToString() == team[3].BridgeAgentId);
        Check("completed broadcast explicitly prevents redundant relays " + central, managerReceipt["delivery_complete"]!.GetValue<bool>() && managerReceipt["guidance"]!.ToString().Contains("do not retry or ask a worker to relay"));
        var inbox = Tool(team[3], "bridge_read_messages")["messages"]!.AsArray();
        Check("locked orchestrator receives no forbidden broadcast " + central, inbox.All(m => m!["sender_id"]!.ToString() != team[0].BridgeAgentId));
    }

    private static void VerifyShortMessageAddresses()
    {
        var (_, team) = Team("short-message-addresses", 4);
        foreach (var peer in team) peer.Status = "idle";
        var roster = Tool(team[0], "bridge_list_agents")["agents"]!.AsArray();
        var addresses = roster.Select(p => p!["message_recipient"]!.ToString()).ToArray();
        Check("short message addresses reduce copied identifier length", addresses.All(a => a.StartsWith("peer:") && a.Length == 17) && addresses.Distinct().Count() == team.Length);
        var direct = Tool(team[0], "bridge_send_message", new() { ["recipient"] = addresses[1], ["message"] = "Direct message through a short stable address." });
        Check("short direct address resolves exactly one stable peer", direct["delivered"]!.AsArray().Single()!["agent_id"]!.ToString() == team[1].BridgeAgentId && team[1].UnreadPeerMessageCount == 1 && team[2].UnreadPeerMessageCount == 0);
        var mixedIds = BroadcastArgs("Do not count an alias twice", team[1], team[2], team[3]);
        mixedIds["affected_agent_ids"] = new JsonArray(addresses[1], team[1].BridgeAgentId, addresses[2]);
        Reject("alias and full ID cannot pad a broadcast audience", () => Tool(team[0], "bridge_broadcast", mixedIds));
        mixedIds["affected_agent_ids"] = new JsonArray(addresses[0], addresses[1], addresses[2]);
        Reject("short self address cannot count toward audience", () => Tool(team[0], "bridge_broadcast", mixedIds));
        Reject("truncated addresses are rejected without guessing", () => Tool(team[0], "bridge_send_message", new() { ["recipient"] = addresses[2][..^1], ["message"] = "Bad truncated address" }));
        var broadcastArgs = BroadcastArgs("Shared update through short stable addresses.", team.Skip(1).ToArray());
        broadcastArgs["affected_agent_ids"] = new JsonArray(addresses.Skip(1).Select(a => (JsonNode?)JsonValue.Create(a)).ToArray());
        var broadcast = Tool(team[0], "bridge_broadcast", broadcastArgs);
        Check("short broadcast addresses resolve to canonical IDs", broadcast["delivered"]!.AsArray().Count == 3 && broadcast["affected_agent_ids"]!.AsArray().Select(p => p!.ToString()).SequenceEqual(team.Skip(1).Select(p => p.BridgeAgentId)));
        var (_, outsiders) = Team("short-address-other-bridge", 1);
        outsiders[0].Status = "idle";
        Reject("short addresses never cross bridge boundaries", () => Tool(outsiders[0], "bridge_send_message", new() { ["recipient"] = addresses[1], ["message"] = "Wrong roster" }));

        var (_, collisions) = Team("short-address-collisions", 3);
        foreach (var peer in collisions) peer.Status = "idle";
        Property(collisions[1], "BridgeAgentId", "11111111111100000000000000000001");
        Property(collisions[2], "BridgeAgentId", "11111111111100000000000000000002");
        var collisionRoster = Tool(collisions[0], "bridge_list_agents")["agents"]!.AsArray();
        Check("colliding prefixes advertise full IDs", collisionRoster.Skip(1).All(p => p!["message_recipient"]!.ToString() == p["agent_id"]!.ToString()));
        Reject("ambiguous short addresses cannot misroute a message", () => Tool(collisions[0], "bridge_send_message", new() { ["recipient"] = "peer:111111111111", ["message"] = "Ambiguous destination" }));
        var exact = Tool(collisions[0], "bridge_send_message", new() { ["recipient"] = collisions[2].BridgeAgentId, ["message"] = "Exact full ID remains valid." });
        Check("full ID remains precise after a prefix collision", exact["delivered"]!.AsArray().Single()!["agent_id"]!.ToString() == collisions[2].BridgeAgentId && collisions[1].UnreadPeerMessageCount == 0);
    }
}
