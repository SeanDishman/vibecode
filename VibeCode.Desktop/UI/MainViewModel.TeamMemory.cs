using System.IO;
using System.Text.Json.Nodes;
using VibeCode.AgentStatus.Mcp.Contracts;
using VibeCode.Services;

namespace VibeCode.UI;

/// <summary>Team memory for Bridges and agent teams: the Bridge prompt (the user's text at the top of every message),
/// targeted bridge_read_chat views, and which teammate a newcomer forks from.</summary>
public sealed partial class MainViewModel
{
    private readonly Dictionary<string, BridgeUserPrompt> _bridgeUserPrompts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _bridgeUserPromptTouched = new(StringComparer.Ordinal);

    /// <summary>A Bridge's prompt key: its host conversation, which is also how a saved Bridge is found again. Null
    /// until the host has a session id.</summary>
    private static string? BridgeUserPromptKey(IReadOnlyList<ChatViewModel> panes) =>
        panes.FirstOrDefault() is { SessionId: { } hostId } host ? BridgeUserPromptStore.Key(host.Provider, hostId) : null;

    /// <summary>The prompt key for the Bridge <paramref name="pane"/> belongs to; null outside a live Bridge.</summary>
    public string? BridgeUserPromptKeyFor(ChatViewModel pane) =>
        TryGetLiveBridge(pane, out var bridge) ? BridgeUserPromptKey(bridge.Panes) : null;

    public BridgeUserPrompt LoadBridgeUserPrompt(string key)
    {
        if (_bridgeUserPrompts.TryGetValue(key, out var cached)) return cached;
        return _bridgeUserPrompts[key] = BridgeUserPromptStore.Load(key);
    }

    public void SaveBridgeUserPrompt(string key, BridgeUserPrompt prompt)
    {
        BridgeUserPromptStore.Save(key, prompt);
        _bridgeUserPrompts[key] = prompt;
    }

    /// <summary>The host left and the Bridge goes on under a new first pane, so its key changed: the prompt moves with it
    /// instead of silently dropping off every message.</summary>
    private void MoveBridgeUserPrompt(string? fromKey, IReadOnlyList<ChatViewModel> panes)
    {
        if (fromKey is null || BridgeUserPromptKey(panes) is not { } toKey || toKey == fromKey) return;
        var prompt = LoadBridgeUserPrompt(fromKey);
        if (prompt.IsEmpty) return;
        try
        {
            SaveBridgeUserPrompt(toKey, prompt);
            SaveBridgeUserPrompt(fromKey, new BridgeUserPrompt());   // an empty prompt removes the old file
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _bridgeUserPrompts[toKey] = prompt;   // not saved, but this session's agents keep getting it
        }
    }

    /// <summary>The block every message to an agent of <paramref name="chat"/>'s Bridge opens with; null without a prompt.</summary>
    private string? BridgeUserPromptFor(ChatViewModel chat)
    {
        if (BridgeUserPromptKeyFor(chat) is not { } key) return null;
        var prompt = LoadBridgeUserPrompt(key);
        if (prompt.IsEmpty) return null;
        // Riding a prompt is use; touching the file at most hourly keeps retention honest without a write per turn.
        var now = DateTimeOffset.UtcNow;
        if (!_bridgeUserPromptTouched.TryGetValue(key, out var touched) || now - touched > TimeSpan.FromHours(1))
        {
            _bridgeUserPromptTouched[key] = now;
            BridgeUserPromptStore.Touch(key, now);
        }
        return prompt.Block();
    }

    /// <summary>bridge_read_chat with a view: one bounded page instead of the full transcript.</summary>
    private JsonObject ReadBridgeChatView(ChatViewModel caller, LiveBridge bridge, JsonObject args)
    {
        var requested = args["agent"]!.GetValue<string>();
        string id;
        try { id = EditAgentId(caller, bridge, requested); }
        catch (StatusValidationException) { throw ViewTargetNotFound(); }
        var target = bridge.Panes.FirstOrDefault(p => p.BridgeAgentId == id && p.Status != "closed") ?? throw ViewTargetNotFound();
        var view = args["view"]?.GetValue<string>() ?? throw new StatusValidationException("term is only used with view=query.");
        if (args["cursor"] is not null)
            throw new StatusValidationException("cursor pages the full read. Omit it with view, or omit view to keep paging.");
        var snapshot = BridgeChatMirror.Capture(target, BridgeNumberOf(target), active: true) with { ChatId = id };
        return BridgeChatViews.Build(view, args["term"]?.GetValue<string>(), snapshot, target.RecoveryCheckpoint);

        static StatusValidationException ViewTargetNotFound() => new(
            "That agent is not a live member of your Bridge. Use an agent_id or message_recipient from bridge_list_agents.");
    }

    /// <summary>Whose conversation a newcomer copies, by free context rather than recency (see <see cref="TeamMemoryPolicy"/>).
    /// With <paramref name="compactWhenFull"/> (the provider compacts sessions) a team that is full everywhere still hands
    /// over its emptiest copy, with <paramref name="compactFirst"/> set; otherwise <paramref name="skippedFull"/> is true
    /// when copies were possible but every one was too full.</summary>
    private static ChatViewModel? EmptiestForkSource(IEnumerable<ChatViewModel> eligible, bool compactWhenFull,
        out double? percent, out bool skippedFull, out bool compactFirst)
    {
        var candidates = eligible.Select(p => new ForkCandidate<ChatViewModel>(p, TeamSpeaker(p),
            p.CtxHasData && p.CtxPercentValue > 0 ? p.CtxPercentValue : null, LastPromptSequence(p))).ToList();
        var choice = TeamMemoryPolicy.Choose(candidates);
        compactFirst = choice is null && compactWhenFull && candidates.Count > 0;
        if (compactFirst) choice = TeamMemoryPolicy.ChooseToCompact(candidates);
        percent = choice is null ? null : choice.ContextPercent ?? 0;
        skippedFull = choice is null && candidates.Count > 0;
        return choice?.Agent;
    }

    /// <summary>Each teammate's checkpoint, for a newcomer that starts with the team log instead of a copy.</summary>
    private static string TeamCheckpointContext(IEnumerable<ChatViewModel> panes) =>
        TeamMemoryPolicy.TeamCheckpoints(panes.Select(p => (TeamSpeaker(p), p.RecoveryCheckpoint)));
}
