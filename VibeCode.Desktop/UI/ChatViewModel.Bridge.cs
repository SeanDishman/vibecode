using System.Text.Json.Nodes;
using VibeCode.AgentStatus.Mcp.Contracts;
using VibeCode.Services;

namespace VibeCode.UI;

public sealed partial class ChatViewModel
{
    internal bool AvailableForBridgeAssignment => !HasQueued && !HasPendingDispatch && Status is "idle" or "starting";

    // Read by bridge_agent_status so a peer can tell "still working" from "finished" without guessing.
    internal DateTime? WorkStartedAtUtc => _workStartedAt;
    internal DateTime? WorkEndedAtUtc => _workEndedAt;
    internal int QueuedPromptCount => _sendQueue.Count;
    internal int PendingPermissionCount => _pendingPerms.Count;

    internal bool CanAcceptBridgeObjective => Status is not ("error" or "closed") &&
        (Status == "starting" || _session is { HasExited: false });

    internal bool SendBridgeObjective(string objective)
    {
        if (!CanAcceptBridgeObjective) return false;
        // Claude may still be refreshing credentials before its session exists. Keep the goal
        // in the normal queue; initialization dispatches it after the account is ready.
        if (_session is null && Status == "starting")
        {
            QueuePrompt(objective, null, useSwarm: false, waitingForCapacity: false, extended: false);
            return true;
        }
        return Send(objective);
    }

    public string BridgeAgentId { get; internal set; } = Guid.NewGuid().ToString("N");
    internal Func<string, JsonObject, JsonObject>? BridgeToolHandler { get; set; }
    private BridgeMcpConnection? _bridgeMcp;

    private BridgeMcpConnection EnsureBridgeMcp() => _bridgeMcp ??= new BridgeMcpConnection(_ui,
        (tool, args) => BridgeToolHandler?.Invoke(tool, args)
            ?? (tool == "bridge_list_agents"
                ? new JsonObject { ["joined"] = false, ["agents"] = new JsonArray() }
                : throw new StatusValidationException("This session is not in a bridge. Start or reopen a bridge first.")));

    private const string DefaultBridgeActivitySummary = "Ready for a task";
    private string _bridgeActivitySummary = DefaultBridgeActivitySummary;
    internal const string DefaultBridgeTaskName = BridgeTaskTitlePolicy.Placeholder;
    private string _bridgeTaskName = DefaultBridgeTaskName;
    public string BridgeTaskName
    {
        get => _bridgeTaskName;
        internal set
        {
            if (!Set(ref _bridgeTaskName, value)) return;
            Raise(nameof(BridgeTerminalLabel));
            Raise(nameof(BridgeHeaderTaskTitle));
        }
    }
    /// <summary>The task shown after the agent's name in its pane header ("Claude 6 · Fix login redirect"). Empty
    /// until the agent, or an Advanced Bridge assignment, names one: the "Ready" placeholder is never shown there.</summary>
    public string BridgeHeaderTaskTitle => _bridgeTaskName is { Length: > 0 } name && name != DefaultBridgeTaskName ? name : "";
    /// <summary>Regular bridges only. Advanced Bridge orchestrators and workers take their task names from
    /// assignments, so they are never asked to title themselves.</summary>
    internal bool UsesBridgeTaskTitle => IsBridgeAgent && !IsBridgeManager && !BridgeCoordinatesOnly && BridgeCoordinatorAgentId is null;
    /// <summary>Title changes accepted since the last prompt was dispatched. The MCP handler caps them so the
    /// header follows the overall task instead of narrating sub-steps.</summary>
    internal int BridgeTaskTitleChangesThisTurn { get; set; }
    public string BridgeTerminalIdentity => int.TryParse(BridgeLabel.Split(' ').LastOrDefault(), out var number)
        ? $"Agent {number}" : AgentDisplay;
    public string BridgeTerminalLabel => $"{BridgeTerminalIdentity} · {BridgeTaskName}";
    public bool BridgeCoordinatesOnly { get; internal set; }
    public string? BridgeCoordinatorAgentId { get; internal set; }
    private string _bridgeOrchestrationScope = "";
    public string BridgeOrchestrationScope
    {
        get => _bridgeOrchestrationScope;
        internal set => Set(ref _bridgeOrchestrationScope, value);
    }
    internal string BridgeCoordinationRoster { get; set; } = "";
    internal DateTimeOffset BridgeCoordinationSince { get; set; }
    internal bool BridgeScopeConfirmed { get; set; }
    internal HashSet<string> BridgeScopeSentTo { get; } = new();
    internal HashSet<string> BridgeScopeReadFrom { get; } = new();

    private string _bridgeTaskState = "ready";
    public string BridgeTaskState
    {
        get => _bridgeTaskState;
        internal set { if (Set(ref _bridgeTaskState, value)) Raise(nameof(BridgeAvailabilityText)); }
    }
    private string _bridgeReviewState = "pending";
    private string _bridgeReviewLevel = BridgeReviewPolicy.Normal;
    public string BridgeReviewLevel
    {
        get => _bridgeReviewLevel;
        internal set
        {
            if (!Set(ref _bridgeReviewLevel, BridgeReviewPolicy.Normalize(value))) return;
            // The provider catalog may still be loading. Its deferred role settings
            // must retain review choices made after the initial launch configuration.
            if (_bridgeLaunchConfiguration is { } pending)
                _bridgeLaunchConfiguration = pending with { ReviewLevel = _bridgeReviewLevel };
            Raise(nameof(BridgeReviewHint));
            Raise(nameof(BridgeReviewLabel));
            Raise(nameof(BridgeReviewComplete));
        }
    }
    public string BridgeReviewHint => BridgeReviewPolicy.Choice(BridgeReviewLevel).Description;
    internal bool BridgeReviewComplete => BridgeReviewState == "approved" ||
        BridgeReviewLevel == "none" && BridgeReviewState == "skipped";
    public string BridgeReviewState
    {
        get => _bridgeReviewState;
        internal set
        {
            if (!Set(ref _bridgeReviewState, value)) return;
            Raise(nameof(BridgeReviewLabel));
            Raise(nameof(BridgeReviewComplete));
        }
    }
    private string _bridgeReviewSummary = "";
    public string BridgeReviewSummary { get => _bridgeReviewSummary; internal set => Set(ref _bridgeReviewSummary, value); }
    public string BridgeReviewLabel => BridgeReviewState switch
    {
        "approved" => "Review passed",
        "skipped" => "Review skipped",
        "changes_requested" => "Changes requested",
        _ => BridgeReviewLevel == "none" ? "Review off" : "Final review pending",
    };
    public string BridgeAvailabilityText => Status is "closed" or "error" ? Status : BridgeTaskState switch
    {
        "completed" => Status == "idle" ? "Completed" : Status,
        "interrupted" => "Interrupted",
        "waiting" => "Waiting for results / review",
        _ => Status,
    };

    private string _bridgeGroupLabel = "Independent agent";
    public string BridgeGroupLabel
    {
        get => _bridgeGroupLabel;
        internal set => Set(ref _bridgeGroupLabel, value);
    }
    public bool BridgeHasCoordinator => BridgeCoordinatorAgentId is not null;
    internal void RaiseBridgeGroup() => Raise(nameof(BridgeHasCoordinator));
    public string BridgeActivitySummary
    {
        get => _bridgeActivitySummary;
        internal set { if (Set(ref _bridgeActivitySummary, value)) Raise(nameof(BridgeActivityDetail)); }
    }
    /// <summary>The activity sentence for the pane header's info card; empty while it is still the default.</summary>
    public string BridgeActivityDetail => _bridgeActivitySummary == DefaultBridgeActivitySummary ? "" : _bridgeActivitySummary;

    private bool _bridgeSingleTerminal;
    public bool BridgeSingleTerminal
    {
        get => _bridgeSingleTerminal;
        internal set { if (Set(ref _bridgeSingleTerminal, value)) Raise(nameof(BridgePaneShown)); }
    }

    private bool _bridgeTerminalSelected;
    public bool BridgeTerminalSelected
    {
        get => _bridgeTerminalSelected;
        internal set { if (Set(ref _bridgeTerminalSelected, value)) Raise(nameof(BridgePaneShown)); }
    }
}
