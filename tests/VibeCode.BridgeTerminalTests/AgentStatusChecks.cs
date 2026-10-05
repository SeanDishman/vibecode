using System.Text.Json.Nodes;
using VibeCode.AgentStatus.Mcp.Bridge;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    /// <summary>bridge_agent_status lets an agent see whether a peer is still working or already done before it touches
    /// that peer's area. State comes from the live session, not from what the peer last reported.</summary>
    private static void VerifyBridgeAgentStatus()
    {
        AppSettings.Current.ContinueAfterLimitResets = true;
        var (_, team) = Team("agent-status", 3);
        Property(team[1], "BridgeTaskName", "Fix login redirect");
        team[1].Status = "running";
        Property(team[2], "BridgeTaskName", "Write the docs");
        team[2].Status = "running";
        team[2].Status = "idle";
        Property(team[2], "BridgeTaskState", "completed");

        var all = Tool(team[0], "bridge_agent_status");
        var rows = all["agents"]!.AsArray().Select(r => r!.AsObject()).ToArray();
        Check("without agent, every other agent is reported and the caller is left out",
            rows.Length == 2 && rows.All(r => !r["you"]!.GetValue<bool>()) && all["busy_count"]!.GetValue<int>() == 1);
        var working = rows.Single(r => r["agent_id"]!.ToString() == team[1].BridgeAgentId);
        Check("a running peer reads as working and busy with its task",
            working["state"]!.ToString() == "working" && working["working"]!.GetValue<bool>() && working["busy"]!.GetValue<bool>()
            && working["working_for"] is not null && working["verdict"]!.ToString().Contains("working right now on \"Fix login redirect\""));
        var finished = rows.Single(r => r["agent_id"]!.ToString() == team[2].BridgeAgentId);
        Check("a peer that completed its task reads as finished, not busy",
            finished["state"]!.ToString() == "finished" && !finished["working"]!.GetValue<bool>() && !finished["busy"]!.GetValue<bool>()
            && finished["idle_for"] is not null && finished["verdict"]!.ToString().Contains("finished on \"Write the docs\""));

        Check("one agent can be checked by number, label, message_recipient or me",
            Tool(team[0], "bridge_agent_status", new() { ["agent"] = "3" })["state"]!.ToString() == "finished"
            && Tool(team[0], "bridge_agent_status", new() { ["agent"] = "Codex 2" })["state"]!.ToString() == "working"
            && Tool(team[0], "bridge_agent_status", new() { ["agent"] = all["agents"]![0]!["message_recipient"]!.ToString() })["working"] is not null
            && Tool(team[0], "bridge_agent_status", new() { ["agent"] = "me" })["you"]!.GetValue<bool>());
        Reject("an agent outside the bridge is named, not silently empty",
            () => Tool(team[0], "bridge_agent_status", new() { ["agent"] = "Agent 9" }));
        Reject("unknown arguments are rejected", () => Tool(team[0], "bridge_agent_status", new() { ["path"] = "app.ts" }));

        Check("a queued follow-up is visible while the peer works", team[1].Send("Then update the changelog.")
            && Tool(team[0], "bridge_agent_status", new() { ["agent"] = "2" })["queued_prompts"]!.GetValue<int>() == 1);

        team[2].Status = "running";
        Call(team[2], "ApplyResult", new JsonObject
        {
            ["type"] = "result", ["subtype"] = "success", ["is_error"] = true,
            ["result"] = "You've hit your session limit · resets 5:40pm (America/Chicago)",
        });
        var paused = Tool(team[0], "bridge_agent_status", new() { ["agent"] = "3" });
        Check("a peer paused by a usage limit is not working but still busy, because it resumes on its own",
            paused["state"]!.ToString() == "waiting_for_limit_reset" && !paused["working"]!.GetValue<bool>() && paused["busy"]!.GetValue<bool>());

        team[1].Status = "error";
        var failed = Tool(team[0], "bridge_agent_status", new() { ["agent"] = "2" });
        Check("a peer that stopped with an error is not busy and its task is flagged as possibly unfinished",
            failed["state"]!.ToString() == "error" && !failed["busy"]!.GetValue<bool>() && failed["verdict"]!.ToString().Contains("may be unfinished"));

        var definition = BridgeMcpTools.Create((_, _) => new JsonObject()).Single(t => t.Name == "bridge_agent_status").Definition;
        Check("MCP advertises bridge_agent_status as a read-only tool with an optional agent",
            definition["annotations"]!["readOnlyHint"]!.GetValue<bool>()
            && definition["inputSchema"]!["required"]!.AsArray().Count == 0
            && definition["inputSchema"]!["properties"]!.AsObject().Single().Key == "agent"
            && BridgeMcpTools.Instructions.Contains("bridge_agent_status"));
    }
}
