using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using VibeCode.Protocol;
using VibeCode.Services;

namespace VibeCode.UI;

public sealed class FileArtifact : Observable
{
    public required string Path { get; init; }
    private int _writes;
    public int Writes { get => _writes; set => Set(ref _writes, value); }
    public string FileName => System.IO.Path.GetFileName(Path);
    public bool IsHtml => Path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || Path.EndsWith(".htm", StringComparison.OrdinalIgnoreCase);
}

public sealed class ModelChoice
{
    /// <summary>The CLI provider that supplied this model. Used only to keep cross-provider picker previews safe.</summary>
    public string Provider { get; init; } = "";
    public required string Value { get; init; }
    public required string Display { get; init; }
    public string? Description { get; init; }
    // Per-model reasoning metadata straight from the CLI initialize response.
    public string? ResolvedModel { get; init; }
    public IReadOnlyList<string> EffortLevels { get; init; } = Array.Empty<string>();
    public bool SupportsEffort { get; init; }
    public bool SupportsAutoMode { get; init; }
    public bool SupportsFastMode { get; init; }

    /// <summary>Compact model name for the composer pill. Descriptions stay secondary text in the model picker.</summary>
    public string ShortName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Description))
            {
                // Some catalogs prefix a tagline with the product name ("Name · ..."). Keep that
                // compatibility, but never promote a plain description sentence into the model title.
                var i = Description.IndexOf('·');   // middle dot
                if (i > 0)
                {
                    var name = Description[..i].Trim();
                    if (name.Length > 0) return name;
                }
            }
            return Display;
        }
    }

    public override string ToString() => Display;
}

/// <summary>One reasoning-effort option in the composer picker. Value null = Auto (CLI default).</summary>
public sealed class EffortChoice : Observable
{
    private bool _isSelected;
    public string? Value { get; init; }          // null = auto | low | medium | high | xhigh | max | ultra
    public required string Label { get; init; }
    public string? Description { get; init; }
    public int Rank { get; init; }               // 0 auto .. N, where N is this model's number of effort tiers
    public int MeterSteps { get; init; }         // per-model: Grok currently has 3; GPT catalogs can expose more
    public string Filled => new('●', Rank);        // ● filled dots
    public string Empty => new('○', Math.Max(0, MeterSteps - Rank)); // ○ empty dots
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}

public sealed class CommandChoice
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? ArgumentHint { get; init; }
}

public sealed class ChatViewModel : Observable
{
    private readonly Dispatcher _ui;
    private ICodingSession? _session;
    private readonly Dictionary<string, ToolItem> _toolById = new();
    private readonly Dictionary<string, List<ItemVm?>> _streams = new(); // parentKey -> index-mapped streamed items
    // Claude emits one usage snapshot per streamed model message. Keep them keyed by message id so
    // message_start, message_delta, and the final assistant envelope update the same entry instead of
    // double-counting it. Codex bypasses these maps and sends a turn-wide usage_update snapshot.
    private readonly Dictionary<string, LiveUsage> _liveUsageByMessage = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _liveMessageByStream = new(StringComparer.Ordinal);
    private int _anonymousLiveMessageId;
    private readonly Dictionary<string, PermItem> _pendingPerms = new();
    private bool _autoApprovalIntegrityWarningShown;
    private bool _interruptRequested;
    private readonly Queue<QueuedItem> _sendQueue = new();   // messages typed while a turn is running; auto-sent when it ends
    private bool _sendAllQueuedNowRequested;
    private sealed record ExtendedQueueDispatch(IReadOnlyList<QueuedItem> Items, UserItem UserItem);
    private ExtendedQueueDispatch? _activeExtendedDispatch;
    private bool _extendedQueueEnabled;
    private int _extendedQueueChunkSize = 1;
    private string? _extendedQueuePauseReason;
    private DateTimeOffset _extendedQueueNextUsageCheck;
    private DispatcherTimer? _extendedQueueUsageTimer;
    private bool _extendedQueueUsageCheckRunning;
    private int _extendedQueueInconclusiveChecks;
    private static readonly TimeSpan ExtendedQueueUsagePollInterval = TimeSpan.FromMinutes(3);
    private SwarmLease? _activeSwarmLease;
    private bool? _sessionSwarmsEnabled;
    private int? _sessionSwarmWorkerCap;
    private readonly PromptHistory _promptHistory = new();
    private TurnRollbackCheckpoint? _activeRollback;
    private UserItem? _undoRequestInFlight;
    private string? _lastLocalUserText;
    private DateTime _lastLocalUserAt;
    private int _startVersion;
    private bool _transcriptLoaded;
    private const int TranscriptReplayBatchSize = 24;

    public string Cwd { get; }
    public string? ResumeSessionId { get; }
    public bool ForkSession { get; }
    public string Provider { get; }
    public bool IsCodex => Provider == "codex";
    public bool IsKimi => Provider == "kimi";
    public bool IsGrok => Provider == "grok";
    public bool IsClaude => Provider == "claude";
    public string ProviderDisplay => Provider switch
    {
        "codex" => "OpenAI Codex",
        "kimi" => "Kimi Code",
        "grok" => "Grok",
        _ => "Claude Code",
    };
    public bool CanBridge => true;
    public bool CanFork => !IsKimi && !IsGrok && SessionId is not null;
    public string AgentDisplay => Provider switch { "codex" => "Codex", "kimi" => "Kimi", "grok" => "Grok", _ => "Claude" };

    /// <summary>CLI-mode empty-chat ASCII banner for this chat's provider. Display-only (never an Items entry).</summary>
    public string CliWelcomeArt => ProviderAsciiArt.For(Provider);
    /// <summary>One-line tag under the CLI empty-chat banner.</summary>
    public string CliWelcomeTagline => ProviderAsciiArt.TaglineFor(Provider);
    /// <summary>True only while the app is in CLI mode — drives the empty-state ASCII overlay.</summary>
    public bool ShowCliWelcome => AppSettings.IsCliMode;
    /// <summary>Normal (non-CLI) empty-state branding: logo + "What are we building…".</summary>
    public bool ShowNormalEmpty => !AppSettings.IsCliMode;
    public string BridgeMenuDescription => "Pick any provider for agent 2 (uses your Settings limit)";
    public string RemoveBridgeAgentToolTip => $"Close this {AgentDisplay} bridge agent";
    public string ExpandBridgeAgentToolTip => $"Expand this {AgentDisplay} agent (fill the bridge)";
    public string ExpandBridgeAgentAutomationName => $"Expand this {AgentDisplay} agent";
    public string ComposerPlaceholder => $"Message {AgentDisplay}…";
    public string ArtifactsEmptyText => $"Files {AgentDisplay} creates or edits show up here.";
    public string TodosEmptyText => $"{AgentDisplay}'s task list appears here once it starts planning.";
    public string SignInButtonText => Provider switch
    {
        "codex" => "Sign in to Codex",
        "kimi" => "Sign in to Kimi",
        "grok" => "Sign in to Grok",
        _ => "Sign in to Claude",
    };
    public string AuthMessage => Provider switch
    {
        "codex" => "OpenAI Codex isn't signed in on this machine. Sign in once and every Codex chat works.",
        "kimi" => "Kimi Code isn't signed in on this machine. Sign in once and every Kimi chat works.",
        "grok" => "Grok isn't signed in on this machine. Sign in once and every Grok chat works.",
        _ => "Claude Code isn't signed in on this machine. Sign in once and every chat works.",
    };

    // ---- Bridge (multiple coding-agent providers/threads on one project) ----
    /// <summary>Extra system-prompt text applied at Start (Bridge peer-awareness).</summary>
    public string? AppendSystemPrompt { get; set; }
    /// <summary>A one-time note prepended to the next user message (tells an already-running chat about new peers).</summary>
    public string? Prelude { get; set; }
    private string _bridgeLabel = "";
    private bool? _lastBridgeSwarmsEnabled;
    public string BridgeLabel
    {
        get => _bridgeLabel;
        set
        {
            if (!Set(ref _bridgeLabel, value)) return;
            // A bridge pane must always expose its OWN provider's models. The ordinary chat picker can preview the
            // globally selected new-chat provider, but that would make a Claude pane inside a Codex bridge look locked.
            RefreshModelPicker(AppSettings.Current.DefaultProvider);
            _lastBridgeSwarmsEnabled = IsBridgeAgent
                ? AppSettings.Current.AgentSwarmsEnabled && AppSettings.Current.AgentSwarmsInBridge
                : null;
            RaiseSwarmProperties();
        }
    }   // e.g. "Claude 1" / "Codex 1"
    public bool IsBridgeAgent => !string.IsNullOrWhiteSpace(_bridgeLabel);
    private bool _isBridgeHost;
    /// <summary>True while this chat is agent 1 of a live bridge (even one running hidden in the background),
    /// so the sidebar can show a "bridge running - click to return" cue.</summary>
    public bool IsBridgeHost { get => _isBridgeHost; set => Set(ref _isBridgeHost, value); }
    private bool _isBridgeManager;
    /// <summary>True when this pane is the bridge's designated MANAGER (the "brain"): the user directs the project
    /// through it, the app routes its @@DISPATCH blocks into the other panes as work orders, and worker turn results
    /// are relayed back to it automatically. Drives the crown drawn over the pane's status dot.</summary>
    public bool IsBridgeManager
    {
        get => _isBridgeManager;
        set { if (Set(ref _isBridgeManager, value)) Raise(nameof(ManagerToolTip)); }
    }
    public string ManagerToolTip => _isBridgeManager
        ? "Bridge manager — click to step it down"
        : $"Make this {AgentDisplay} the manager: you talk to it, and it dispatches work to the other agents";

    /// <summary>The assistant prose of the most recent turn (every text block after the last user message),
    /// concatenated in order. The Bridge manager loop scans this for @@DISPATCH blocks in the manager's reply and
    /// relays a worker's tail as its report; tool output and thinking are deliberately excluded.</summary>
    internal string LastTurnReplyText()
    {
        var parts = new List<string>();
        for (var i = Items.Count - 1; i >= 0; i--)
        {
            if (Items[i] is UserItem) break;
            if (Items[i] is TextItem { HasText: true } t) parts.Add(t.Text);
        }
        parts.Reverse();
        return string.Join("\n\n", parts).Trim();
    }
    private bool _bridgeHasWorkingPane;
    /// <summary>Aggregate live-bridge activity copied onto the host for its sidebar status dot.</summary>
    internal bool BridgeHasWorkingPane
    {
        get => _bridgeHasWorkingPane;
        set { if (Set(ref _bridgeHasWorkingPane, value)) Raise(nameof(ChatListIsWorking)); }
    }
    /// <summary>The sidebar row pulses for this chat itself or for any agent in the bridge it hosts.</summary>
    public bool ChatListIsWorking => IsWorking || BridgeHasWorkingPane;
    private string _draft = "";
    public string Draft { get => _draft; set => Set(ref _draft, value); }                     // per-pane composer text
    public bool TryNavigatePromptHistory(int direction, string currentText, out string text) =>
        _promptHistory.TryNavigate(direction, currentText, out text);
    public bool IsBrowsingPromptHistory(string currentText) => _promptHistory.IsBrowsing(currentText);
    public void ResetPromptHistoryNavigation() => _promptHistory.ResetNavigation();
    private bool _bridgeExpanded;
    /// <summary>True when this pane is expanded to fill the whole bridge. At most one at a time.</summary>
    public bool BridgeExpanded { get => _bridgeExpanded; set => Set(ref _bridgeExpanded, value); }
    private bool _bridgeVisible = true;
    /// <summary>False when a PEER pane is expanded, so this pane's container collapses (the UniformGrid then gives the
    /// expanded pane the whole area). Defaults true so a pane is visible unless something else is focused.</summary>
    public bool BridgeVisible
    {
        get => _bridgeVisible;
        set { if (Set(ref _bridgeVisible, value)) Raise(nameof(BridgePaneShown)); }
    }
    private bool _bridgeMinimized;
    /// <summary>User hid this bridge agent temporarily (still running). Independent of <see cref="BridgeVisible"/>
    /// expand-focus collapse. Restore chips in the bridge header unminimize without killing the agent.</summary>
    public bool BridgeMinimized
    {
        get => _bridgeMinimized;
        set { if (Set(ref _bridgeMinimized, value)) Raise(nameof(BridgePaneShown)); }
    }
    /// <summary>True when this pane should occupy a grid cell: not expand-collapsed and not user-minimized.</summary>
    public bool BridgePaneShown => _bridgeVisible && !_bridgeMinimized;

    private bool _onSecondMonitor;
    /// <summary>True when this agent lives on the companion (second display) Bridge surface. Ownership is per-pane and
    /// STABLE: an agent added on a display stays on that display, and adding or removing peers never migrates it.
    /// Cleared whenever the split collapses, because every agent then returns to the single window.</summary>
    public bool OnSecondMonitor { get => _onSecondMonitor; set => Set(ref _onSecondMonitor, value); }

    public TranscriptItems Items { get; } = new();

    /// <summary>A live, filtered projection of <see cref="Items"/> holding only the user's own text prompts
    /// (excludes subagent echoes). Backs the "jump to your messages" header navigator (normal chat + bridge).
    /// Separate CollectionView from the transcript ListBox's default view, so filtering never hides rows.</summary>
    public ICollectionView UserMessages { get; }
    private readonly CollectionViewSource _userMessagesSource;

    public ObservableCollection<TodoEntry> Todos { get; } = new();
    /// <summary>Provider-native Claude/Codex child workers for the shared chat and Bridge roster.</summary>
    public ObservableCollection<SubagentItem> Subagents { get; } = new();
    private SubagentItem? _selectedSubagent;
    /// <summary>The child whose live transcript is open in the shared normal-chat/Bridge inspector.</summary>
    public SubagentItem? SelectedSubagent
    {
        get => _selectedSubagent;
        private set
        {
            if (!Set(ref _selectedSubagent, value)) return;
            Raise(nameof(HasSelectedSubagent));
        }
    }
    public bool HasSelectedSubagent => SelectedSubagent is not null;
    public bool HasSubagents => Subagents.Count > 0;
    public int ActiveSubagentCount => Subagents.Count(a => a.IsActive);
    public bool SupportsSwarms => SwarmPolicy.SupportsProvider(Provider);
    public bool SwarmsAvailable => SupportsSwarms
        && AppSettings.Current.AgentSwarmsEnabled
        && _sessionSwarmsEnabled != false
        && (!IsBridgeAgent || AppSettings.Current.AgentSwarmsInBridge);
    public bool ShowSwarmControl => SupportsSwarms && (SwarmsAvailable || HasSubagents);

    /// <summary>Both agent buttons show for any provider that can run child agents, in normal chats and Bridge
    /// panes alike. The swarm one is DISABLED rather than hidden when swarms are switched off, so the capability
    /// stays discoverable and its tooltip can say why it is unavailable instead of the control just vanishing.</summary>
    public bool ShowAgentButtons => SupportsSwarms;

    public string SwarmButtonToolTip => !SwarmsAvailable
        ? SwarmAvailabilityText
        : SwarmNextTurn
            ? $"Agent swarm armed for the next message — up to {SwarmMaxWorkers} workers. Click to cancel."
            : $"Send the next message as an agent swarm — up to {SwarmMaxWorkers} workers";

    public string SubagentsButtonToolTip => ActiveSubagentCount switch
    {
        > 1 => $"{ActiveSubagentCount} subagents working · open the roster",
        1 => "1 subagent working · open the roster",
        _ when Subagents.Count > 0 =>
            $"{Subagents.Count} finished subagent{(Subagents.Count == 1 ? "" : "s")} · open the roster",
        _ => "Subagents this chat has spawned — none yet",
    };
    public int SwarmMaxWorkers
    {
        get
        {
            var configured = SwarmPolicy.ClampMaxWorkers(AppSettings.Current.SwarmMaxWorkers);
            return _sessionSwarmWorkerCap is { } runtime ? Math.Min(configured, runtime) : configured;
        }
    }
    public int SwarmAvailableWorkers => SwarmBudget.AvailableWorkers;
    private bool _swarmNextTurn;
    /// <summary>One-shot request captured by Send/queue; ordinary prompts never fan out merely because tools exist.</summary>
    public bool SwarmNextTurn
    {
        get => _swarmNextTurn;
        set
        {
            var allowed = value && SwarmsAvailable;
            if (!Set(ref _swarmNextTurn, allowed)) return;
            Raise(nameof(SubagentButtonToolTip));
            Raise(nameof(SwarmButtonToolTip));
            Raise(nameof(SwarmNextTurnText));
        }
    }
    public string SwarmNextTurnText
    {
        get
        {
            var grant = Math.Min(SwarmMaxWorkers, SwarmAvailableWorkers);
            if (grant == 0) return "Will wait for shared capacity (16 app-wide)";
            return SwarmNextTurn
                ? $"Armed for the next message (up to {grant})"
                : $"Use on the next message (up to {grant}; 16 app-wide)";
        }
    }
    public string SwarmAvailabilityText => IsBridgeAgent && !AppSettings.Current.AgentSwarmsInBridge
        ? "Bridge swarms are off in Settings"
        : AppSettings.Current.AgentSwarmsEnabled
            ? $"{AgentDisplay} chooses the smallest useful swarm, never more than {SwarmMaxWorkers}."
            : "Agent swarms are off in Settings";
    public string SubagentPanelTitle => ActiveSubagentCount > 0
        ? $"SUBAGENTS · {ActiveSubagentCount} ACTIVE"
        : Subagents.Count > 0 ? $"SUBAGENTS · {Subagents.Count} DONE" : "AGENT SWARM";
    public string SubagentButtonToolTip => ActiveSubagentCount switch
    {
        > 1 => $"{ActiveSubagentCount} subagents working · click for details",
        1 => "1 subagent working · click for details",
        _ when SwarmNextTurn => $"Swarm armed for next message · up to {SwarmMaxWorkers} workers",
        _ when SwarmsAvailable => $"Use a swarm on the next message · up to {SwarmMaxWorkers} workers",
        _ => $"{Subagents.Count} completed subagent{(Subagents.Count == 1 ? "" : "s")} · click for details",
    };

    public void OpenSubagent(SubagentItem subagent)
    {
        if (Subagents.Contains(subagent)) SelectedSubagent = subagent;
    }

    public void CloseSubagentInspector() => SelectedSubagent = null;

    /// <summary>Refresh live controls after Settings saves. Launch caps apply on the next session start; the
    /// per-turn ceiling and Bridge opt-in are enforced immediately.</summary>
    public void RefreshSwarmSettings()
    {
        var bridgeEnabled = AppSettings.Current.AgentSwarmsEnabled && AppSettings.Current.AgentSwarmsInBridge;
        if (IsBridgeAgent && _lastBridgeSwarmsEnabled is { } previous && previous != bridgeEnabled)
        {
            var note = "[BRIDGE SETTINGS] " + SwarmPolicy.BridgeRuntimeRule(bridgeEnabled, SwarmMaxWorkers);
            Prelude = string.IsNullOrWhiteSpace(Prelude) ? note : Prelude + "\n" + note;
        }
        if (IsBridgeAgent) _lastBridgeSwarmsEnabled = bridgeEnabled;
        if (!SwarmsAvailable) SwarmNextTurn = false;
        RaiseSwarmProperties();
        if (_status == "idle" && _sendQueue.Count > 0) Post(FlushQueue);
    }

    private void RaiseSwarmProperties()
    {
        Raise(nameof(IsBridgeAgent));
        Raise(nameof(SwarmsAvailable));
        Raise(nameof(ShowSwarmControl));
        Raise(nameof(ShowAgentButtons));
        Raise(nameof(SwarmButtonToolTip));
        Raise(nameof(SubagentsButtonToolTip));
        Raise(nameof(SwarmMaxWorkers));
        Raise(nameof(SwarmAvailableWorkers));
        Raise(nameof(SwarmNextTurnText));
        Raise(nameof(SwarmAvailabilityText));
        Raise(nameof(SubagentPanelTitle));
        Raise(nameof(SubagentButtonToolTip));
    }

    /// <summary>Switch existing top-level and subagent activity groups without reloading or losing live tool state.</summary>
    public void SetCompactMode(bool enabled) => SetCompactMode(Items, enabled);

    private static void SetCompactMode(IEnumerable<ItemVm> items, bool enabled)
    {
        foreach (var item in items)
        {
            if (item is CompactToolGroupItem group)
            {
                group.CompactEnabled = enabled;
                foreach (var tool in group.Tools) SetCompactMode(tool.Children, enabled);
            }
            else if (item is ToolItem tool)
            {
                SetCompactMode(tool.Children, enabled);
            }
        }
    }

    // The current CLI has no TodoWrite; it manages its checklist with the Task* tool family (TaskCreate/TaskUpdate/…).
    // We rebuild the live checklist from those incremental calls. Tasks are numbered 1,2,3… in creation order (the id
    // TaskUpdate references), so a simple counter reproduces the CLI's ids on both live turns and transcript replay.
    private readonly Dictionary<string, TodoEntry> _tasksById = new();                   // CLI taskId -> entry
    private readonly Dictionary<string, string> _taskIdByTool = new();                   // tool_use id -> taskId (idempotency)
    private readonly Dictionary<string, (string Subject, string? ActiveForm)> _taskText = new();
    // Provisional ids whose TaskCreate result ARRIVED but named no id we could parse. Only these are candidates for
    // TaskUpdate's reconcile - a provisional still waiting for its result may yet adopt a different id, and stealing
    // it would attach one task's row to another task's updates.
    private readonly HashSet<string> _adoptFailed = new();
    private int _taskSeq;
    public ObservableCollection<FileArtifact> Files { get; } = new();
    public ObservableCollection<ModelChoice> Models { get; } = new();
    /// <summary>
    /// What the model popup displays. Unlike <see cref="Models"/>, this follows the account/provider selected for
    /// new chats; rows from another provider are preview-only and can never be sent to this live session.
    /// </summary>
    public ObservableCollection<ModelPickerChoice> PickerModels { get; } = new();
    public ObservableCollection<CommandChoice> Commands { get; } = new();
    /// <summary>Files/images staged in the composer, sent with the next message.</summary>
    public ObservableCollection<Attachment> Attachments { get; } = new();
    public bool HasAttachments => Attachments.Count > 0;

    /// <summary>Raised when a turn finishes; the window flashes the taskbar if it isn't focused.</summary>
    public event Action? TurnCompleted;
    /// <summary>Raised when the provider needs the user (a permission/question/plan request arrived).</summary>
    public event Action? AttentionNeeded;
    /// <summary>Raised when the user sends (or queues) a message here; floats this chat to the top of the sidebar list.</summary>
    public event Action? MessageSent;

    private string _title = "";
    private bool _pinned;
    private string _status = "starting"; // starting | preparing | running | idle | error | closed
    private string _mode = "default";
    private string? _userMode;   // the mode the user explicitly picked - re-asserted across CLI re-inits
    private string? _model;   // provider-specific default, assigned in the constructor
    private string _modelPickerProvider = "";
    private string? _sessionId;
    private double _cost;
    private long _ctxUsed;
    private long _ctxWindow = 1_000_000;   // most models are 1M; refined per-model + from usage reports
    private double _thinkingTokens;
    private double _totalTokens;      // cumulative "all tokens used" this session (prompt + cache + output, summed each turn)
    private double _totalIn;           // cumulative input side (prompt + cache creation + cache read)
    private double _totalOut;          // cumulative output side (generated tokens)
    private double _estCost;           // running estimated equivalent-API cost (USD) from ModelPricing + per-turn usage
    private LiveUsage _liveTurnUsage;  // current, uncommitted turn; visible while the provider is still working
    private LiveUsage _loggedKimiUsage;   // last Kimi whole-session snapshot written to the usage history
    private double _loggedKimiCost;       // its estimated cost, so the next snapshot logs only the difference
    private FileArtifact? _selectedFile;
    private string _previewText = "";
    private bool _authNeeded;
    private string? _retryNote;   // set while the CLI is retrying after an API error/overload

    private readonly record struct LiveUsage(double Input, double CacheWrite, double CacheRead, double Output)
    {
        public double TotalIn => Input + CacheWrite + CacheRead;
        public double Total => TotalIn + Output;
        public bool HasTokens => Total > 0;
    }

    // Titles come from the first user message, which is often multi-line. TextWrapping="NoWrap" does not
    // suppress explicit newlines, so an unsanitized title renders two lines and doubles the header/sidebar row.
    public string Title { get => _title; set => Set(ref _title, OneLine(value)); }

    /// <summary>Collapses every whitespace run (newlines included) to a single space.</summary>
    private static string OneLine(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        var space = false;
        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c)) { space = sb.Length > 0; continue; }
            if (space) { sb.Append(' '); space = false; }
            sb.Append(c);
        }
        return sb.ToString();
    }
    /// <summary>Pinned chats appear in the dedicated top sidebar section and show a pin marker.</summary>
    public bool Pinned { get => _pinned; set { if (Set(ref _pinned, value)) { Raise(nameof(PinLabel)); Raise(nameof(PinGlyph)); Raise(nameof(SidebarSection)); } } }
    public string PinLabel => _pinned ? "Unpin" : "Pin";
    public string PinGlyph => _pinned ? "" : "";   // UnPin : Pin (Segoe MDL2)
    public string SidebarSection => _pinned ? "PINNED CHATS" : "CHATS";
    public string Status
    {
        get => _status;
        set
        {
            if (!Set(ref _status, value)) return;
            TrackWorkingElapsed();
            Raise(nameof(IsWorking));
            Raise(nameof(ChatListIsWorking));
            Raise(nameof(CanInterrupt));
            Raise(nameof(CanSendQueuedNow));
            Raise(nameof(WorkingText));
            if (value == "idle" && _sendQueue.Count > 0) Post(FlushQueue);
        }
    }
    public string Mode { get => _mode; set { if (Set(ref _mode, value)) { Raise(nameof(ModeDisplay)); Raise(nameof(ModeIcon)); } } }

    // ---- reasoning effort (per-model, from the CLI's supportedEffortLevels) ----
    private string? _effort;   // provider-specific default, assigned in the constructor
    private string? _appliedEffort = "\0";                          // sentinel: nothing pushed yet
    public string? Effort { get => _effort; set { if (Set(ref _effort, value)) { Raise(nameof(EffortDisplay)); Raise(nameof(EffortFilled)); Raise(nameof(EffortEmpty)); } } }
    public string EffortDisplay => _effort is null ? "effort auto" : $"effort {_effort}";
    // dot-meter for the composer pill (matches the effort popup): filled dots = current level, empty = the rest
    private int EffortRankNow => _effort is null ? 0 : EffortOptions.FirstOrDefault(o =>
        string.Equals(o.Value, _effort, StringComparison.OrdinalIgnoreCase))?.Rank ?? 0;
    private int EffortMeterSteps => EffortOptions.Count == 0 ? 0 : EffortOptions.Max(o => o.MeterSteps);
    public string EffortFilled => new('●', EffortRankNow);
    public string EffortEmpty => new('○', Math.Max(0, EffortMeterSteps - EffortRankNow));
    /// <summary>Effort options for the currently selected model. Empty when the model has no effort control (e.g. Haiku).</summary>
    public ObservableCollection<EffortChoice> EffortOptions { get; } = new();
    public bool HasEffort => EffortOptions.Count > 0;

    // ---- fast mode (faster output; per the CLI's fastMode setting, on models that support it) ----
    private bool _fastMode = AppSettings.Current.FastMode;
    public bool FastMode { get => _fastMode; set => Set(ref _fastMode, value); }
    /// <summary>The catalog entry for the model this chat will actually run. Tolerant of the 1M-context variant
    /// suffix: the CLI's init catalog reports resolvedModel as "claude-opus-5[1m]", but a running turn's
    /// system/init (and assistant messages) report the plain "claude-opus-5" - so an exact string compare never
    /// matches once a turn has run. We strip the "[…]" variant tag before comparing, and fall back to the "default"
    /// entry when nothing matches (mirrors ModelDisplay) so a fresh chat still resolves to the CLI's real default.</summary>
    internal ModelChoice? CurrentModel =>
        Models.FirstOrDefault(m => m.Value == _model || m.ResolvedModel == _model
                                || StripVariant(m.Value) == StripVariant(_model)
                                || StripVariant(m.ResolvedModel) == StripVariant(_model))
        ?? Models.FirstOrDefault(m => m.Value == "default");
    private static string StripVariant(string? s) => s is null ? "" : (s.IndexOf('[') is int i and >= 0 ? s[..i] : s);
    /// <summary>Whether the current model supports fast mode (drives showing the toggle in the effort dropdown).</summary>
    public bool CanFastMode => CurrentModel?.SupportsFastMode ?? false;
    /// <summary>Toggle fast mode. Persisted globally; applies to a chat's next start (it's a session --settings flag).</summary>
    public void SetFastMode(bool on)
    {
        FastMode = on;
        AppSettings.Current.FastMode = on;
        AppSettings.Current.Save();
    }

    public string? Model
    {
        get => _model;
        set
        {
            if (!Set(ref _model, value)) return;
            Raise(nameof(ModelDisplay));
            Raise(nameof(ModelPickerHint));
            // Denominator for the context bar: Haiku is 200k, everything else 1M (usage reports still override).
            var resolved = Models.FirstOrDefault(m => m.Value == value || m.ResolvedModel == value)?.ResolvedModel ?? value;
            _ctxWindow = resolved is not null && resolved.Contains("haiku", StringComparison.OrdinalIgnoreCase) ? 200_000 : 1_000_000;
            Raise(nameof(CtxText));
            Raise(nameof(CtxPercentValue));
            Raise(nameof(CtxDetailText));
            Raise(nameof(CanFastMode));   // fast-mode availability is per-model
            Raise(nameof(CostText));      // a live turn's estimate is priced using the active model
        }
    }
    public string? SessionId { get => _sessionId; set { if (Set(ref _sessionId, value)) { Raise(nameof(SessionShort)); Raise(nameof(CanFork)); } } }
    public double Cost { get => _cost; set { if (Set(ref _cost, value)) Raise(nameof(CostText)); } }
    public double ThinkingTokens { get => _thinkingTokens; set { if (Set(ref _thinkingTokens, value)) Raise(nameof(WorkingText)); } }
    /// <summary>Cumulative tokens this whole conversation has burned (all turns). Session-scoped: resets to 0 when the
    /// chat is (re)opened. Split into <see cref="TotalIn"/> / <see cref="TotalOut"/>; rendered via <see cref="TokensText"/>.</summary>
    public double TotalTokens
    {
        get => _totalTokens;
        set
        {
            if (!Set(ref _totalTokens, value)) return;
            Raise(nameof(TokensText));
            Raise(nameof(HasTokens));
        }
    }
    /// <summary>Cumulative input-side tokens (prompt + cache creation + cache read).</summary>
    public double TotalIn { get => _totalIn; set { if (Set(ref _totalIn, value)) Raise(nameof(TokensText)); } }
    /// <summary>Cumulative output-side tokens (generated).</summary>
    public double TotalOut { get => _totalOut; set { if (Set(ref _totalOut, value)) Raise(nameof(TokensText)); } }
    /// <summary>True once completed or live turn usage exists — gates the persistent <see cref="TokensText"/> readout.</summary>
    public bool HasTokens => _totalTokens + _liveTurnUsage.Total > 0;

    public bool AuthNeeded { get => _authNeeded; set => Set(ref _authNeeded, value); }

    /// <summary>Opt-in FIFO for long unattended runs. Unlike the lightweight send-while-busy queue, every prompt is
    /// kept as its own entry and only the configured number are handed to one provider turn.</summary>
    public bool ExtendedQueueEnabled
    {
        get => _extendedQueueEnabled;
        set
        {
            if (!Set(ref _extendedQueueEnabled, value)) return;
            RaiseExtendedQueueProperties();
        }
    }

    public int ExtendedQueueChunkSize
    {
        get => _extendedQueueChunkSize;
        private set
        {
            value = Math.Clamp(value, 1, 3);
            if (!Set(ref _extendedQueueChunkSize, value)) return;
            RaiseExtendedQueueProperties();
        }
    }

    public bool ExtendedQueueChunkIs1 => ExtendedQueueChunkSize == 1;
    public bool ExtendedQueueChunkIs2 => ExtendedQueueChunkSize == 2;
    public bool ExtendedQueueChunkIs3 => ExtendedQueueChunkSize == 3;
    public bool ExtendedQueuePaused => _extendedQueuePauseReason is not null;
    public bool ExtendedQueuePausedForUsage => _extendedQueuePauseReason == "usage";
    public int ExtendedQueuedCount => _sendQueue.Count(item => item.Extended);
    public string ExtendedQueueDisplay
    {
        get
        {
            var pending = ExtendedQueuedCount;
            if (ExtendedQueuePaused) return $"queue paused{(pending > 0 ? $" · {pending}" : "")}";
            if (!ExtendedQueueEnabled) return $"extended queue{(pending > 0 ? $" · {pending}" : "")}";
            return $"queue ×{ExtendedQueueChunkSize}{(pending > 0 ? $" · {pending}" : "")}";
        }
    }
    public string ExtendedQueueToggleText => ExtendedQueueEnabled
        ? "Turn extended queue off"
        : "Turn extended queue on";
    public string ExtendedQueueDescription => ExtendedQueuePaused
        ? ExtendedQueuePauseText
        : ExtendedQueueEnabled
            ? $"New prompts stay separate and run in FIFO chunks of {ExtendedQueueChunkSize}."
            : "Keep many prompts in order and send only 1–3 in each turn.";
    public string ExtendedQueuePauseText
    {
        get
        {
            if (_extendedQueuePauseReason == "usage")
            {
                var wait = _extendedQueueNextUsageCheck - DateTimeOffset.Now;
                var when = wait <= TimeSpan.Zero ? "checking usage now" : $"checks again {ShortWait(wait)}";
                return $"Extended queue · paused for usage · {when}";
            }
            return _extendedQueuePauseReason is { Length: > 0 } reason
                ? $"Extended queue · paused after {reason}"
                : "Extended queue";
        }
    }

    public void SetExtendedQueueEnabled(bool enabled) => ExtendedQueueEnabled = enabled;
    public void SetExtendedQueueChunkSize(int size) => ExtendedQueueChunkSize = size;

    private static string ShortWait(TimeSpan wait)
    {
        if (wait.TotalMinutes < 1) return "in under a minute";
        if (wait.TotalHours < 1) return $"in {Math.Max(1, (int)Math.Ceiling(wait.TotalMinutes))}m";
        return wait.TotalDays < 2
            ? $"in {(int)Math.Floor(wait.TotalHours)}h {wait.Minutes}m"
            : $"in {(int)Math.Floor(wait.TotalDays)}d {wait.Hours}h";
    }

    private void RaiseExtendedQueueProperties()
    {
        Raise(nameof(ExtendedQueueEnabled));
        Raise(nameof(ExtendedQueueChunkSize));
        Raise(nameof(ExtendedQueueChunkIs1));
        Raise(nameof(ExtendedQueueChunkIs2));
        Raise(nameof(ExtendedQueueChunkIs3));
        Raise(nameof(ExtendedQueuePaused));
        Raise(nameof(ExtendedQueuePausedForUsage));
        Raise(nameof(ExtendedQueuedCount));
        Raise(nameof(ExtendedQueueDisplay));
        Raise(nameof(ExtendedQueueToggleText));
        Raise(nameof(ExtendedQueueDescription));
        Raise(nameof(ExtendedQueuePauseText));
        foreach (var item in _sendQueue) item.RefreshQueueState();
    }

    public bool IsWorking => _status is "running" or "starting" or "preparing";
    /// <summary>True only while an actual interruptible turn is in flight (the CLI is processing a sent message).
    /// Drives the composer's Send→Stop morph and the Esc-to-interrupt gesture. Deliberately EXCLUDES "starting"
    /// (session boot/checkpoint preparation, before anything is sent) so the action button stays a Send button while
    /// those non-interruptible steps finish.</summary>
    public bool CanInterrupt => _status == "running";
    /// <summary>The queue-head action can interrupt a live turn or dispatch early while the provider is starting.</summary>
    public bool CanSendQueuedNow => HasQueued && !_interruptRequested && !_sendAllQueuedNowRequested
                                    && (_status is "starting" or "running"
                                        || ExtendedQueuePaused && _sendQueue.TryPeek(out var head) && head.Extended
                                           && _status is "idle" or "error");

    // ---- "working" elapsed clock ----
    private DateTime? _workStartedAt;
    private System.Windows.Threading.DispatcherTimer? _workTimer;

    /// <summary>
    /// Start/stop the elapsed clock as the turn starts and ends. The stopwatch only runs while the
    /// agent is genuinely busy, so interrupting a prompt (status → idle) clears it rather than leaving
    /// a stale duration on screen, and the next turn counts from zero.
    /// </summary>
    private void TrackWorkingElapsed()
    {
        if (IsWorking)
        {
            _workStartedAt ??= DateTime.UtcNow;
            if (_workTimer is null)
            {
                _workTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(1),
                };
                // Only the label needs repainting; the tick does no other work.
                _workTimer.Tick += (_, _) => Raise(nameof(WorkingText));
            }
            _workTimer.Start();
        }
        else
        {
            _workStartedAt = null;
            _workTimer?.Stop();
        }
    }

    /// <summary>"working… 42s" / "working… 3m 07s" — how long this turn has been going.</summary>
    private string Elapsed
    {
        get
        {
            if (_workStartedAt is not { } start) return "";
            var s = DateTime.UtcNow - start;
            if (s.TotalSeconds < 1) return "";
            return s.TotalMinutes >= 60
                ? $" {(int)s.TotalHours}h {s.Minutes:00}m"
                : s.TotalSeconds >= 60 ? $" {(int)s.TotalMinutes}m {s.Seconds:00}s"
                : $" {s.Seconds}s";
        }
    }

    public string WorkingText
    {
        get
        {
            var t = Elapsed;
            if (_status == "starting") return "starting session…" + t;
            if (_status == "preparing") return "preparing prompt…" + t;
            return _retryNote is not null ? _retryNote
                : ActiveSubagentCount > 1 ? $"{ActiveSubagentCount} subagents working…{t}"
                : ActiveSubagentCount == 1 ? $"1 subagent working…{t}"
                : _thinkingTokens > 0 ? $"thinking · {_thinkingTokens / 1000:0.0}k tokens{t}"
                : $"working…{t}";
        }
    }
    /// <summary>Persistent cumulative token readout — total then (input/output), e.g. "2.5M (1.8M/700k)". Unlike
    /// <see cref="WorkingText"/> it stays visible when the chat is idle, so a stopped bridge agent still shows what it
    /// burned. Numbers only, no in/out labels. Empty until the first turn reports usage.</summary>
    public string TokensText
    {
        get
        {
            var input = _totalIn + _liveTurnUsage.TotalIn;
            var output = _totalOut + _liveTurnUsage.Output;
            var total = input + output;
            return total > 0 ? $"{FmtTokens(total)} ({FmtTokens(input)}/{FmtTokens(output)})" : "";
        }
    }
    /// <summary>Compact, label-less token count for the status line: 842 / 47.8k / 2.4M.</summary>
    internal static string FmtTokens(double t) =>
        t >= 1_000_000 ? $"{t / 1_000_000:0.0}M" : t >= 1_000 ? $"{t / 1_000:0.0}k" : $"{t:0}";
    /// <summary>Compact context count with a suffix on both sides of the used/window readout.</summary>
    internal static string FmtContextTokens(long t) =>
        t >= 1_000_000 ? $"{t / 1_000_000.0:0.#}M" : t >= 1_000 ? $"{t / 1_000.0:0.#}k" : $"{t}";
    public string ModeDisplay => _mode switch
    {
        "acceptEdits" => "accept edits",
        "plan" => "plan",
        "bypassPermissions" => "bypass",
        "dontAsk" => "don't ask",
        "auto" => "auto",
        _ => "ask",
    };
    /// <summary>Fluent glyph that matches the current permission mode (so the pill icon changes with it).</summary>
    public string ModeIcon => _mode switch
    {
        "acceptEdits" => "",       // lightning
        "plan" => "",              // document
        "bypassPermissions" => "", // warning
        "auto" => "",              // sparkle
        _ => "",                   // lock (ask)
    };
    public string ModelDisplay =>
        (Models.FirstOrDefault(m => m.Value == _model || m.ResolvedModel == _model)
         ?? Models.FirstOrDefault(m => m.Value == "default"))   // fresh chat: _model not set yet -> show the default model's name
        ?.ShortName ?? _model ?? "model";
    /// <summary>Explains why a running GPT/Claude pill may intentionally open a different provider's catalog.</summary>
    public string ModelPickerHint => ProviderModelCatalog.Normalize(_modelPickerProvider) == ProviderModelCatalog.Normalize(Provider)
        ? ""
        : $"{ProviderModelCatalog.DisplayName(_modelPickerProvider)} models for new chats · this {AgentDisplay} keeps running {ModelDisplay}";
    public string SessionShort => _sessionId is null ? "" : "#" + _sessionId[..8];
    // Prefer the CLI's exact completed total_cost_usd. During a live turn, add its list-price estimate and
    // prefix the combined number with "~"; once the result arrives the provider's exact total takes over.
    public string CostText
    {
        get
        {
            // Grok subscription/OAuth responses commonly omit cost. Falling through to another provider's
            // list-price fallback made a Grok turn look like OpenAI/Claude spend; show only server-reported Grok
            // cost and keep its account limits completely separate.
            if (IsGrok) return _cost > 0 ? $"${_cost:0.####}" : "";
            var liveCost = _liveTurnUsage.HasTokens
                ? ModelPricing.TurnCost(CurrentModel?.ResolvedModel ?? _model,
                    _liveTurnUsage.Input, _liveTurnUsage.CacheWrite, _liveTurnUsage.CacheRead, _liveTurnUsage.Output)
                : 0;
            if (liveCost > 0)
            {
                var completedCost = _cost > 0 ? _cost : _estCost;
                return $"~${completedCost + liveCost:0.00##}";
            }
            return _cost > 0 ? $"${_cost:0.####}" : (_estCost > 0 ? $"~${_estCost:0.00##}" : "");
        }
    }
    public string CtxText => _ctxUsed > 0 ? $"{Math.Min(100.0, 100.0 * _ctxUsed / _ctxWindow):0}% context" : "";
    // context-window details for the usage panel (always shown for an active chat)
    public bool CtxHasData => true;
    public double CtxPercentValue => _ctxUsed > 0 ? Math.Min(100.0, 100.0 * _ctxUsed / _ctxWindow) : 0;
    public string CtxDetailText => _ctxUsed > 0
        ? $"{FmtContextTokens(_ctxUsed)} / {FmtContextTokens(_ctxWindow)} tokens"
        : "no messages yet";
    public FileArtifact? SelectedFile
    {
        get => _selectedFile;
        set { if (Set(ref _selectedFile, value)) LoadPreview(); }
    }
    public string PreviewText { get => _previewText; set => Set(ref _previewText, value); }

    public event Action? ItemsChanged; // scroll-to-bottom signal

    /// <summary>The Claude, Codex, or Grok account this chat runs under (captured at creation). Each provider receives that
    /// account's isolated token/home, so the chat stays put even if the user switches accounts afterward.</summary>
    public string? AccountId { get; private set; }

    /// <summary>Display name of <see cref="AccountId"/>, shown in the chat header. Without this an already-open chat
    /// legitimately keeps running under the old account after a switch and NOTHING tells the user - which is exactly
    /// what makes a correct switch look like it "didn't register".</summary>
    public string AccountLabel
    {
        get
        {
            if (IsKimi) return "Kimi login";
            if (_accountLabel is null) ResolveAccountChip();
            return _accountLabel!;
        }
    }

    /// <summary>Only worth showing the "running as …" chip when more than one account is saved.</summary>
    public bool ShowAccountLabel
    {
        get
        {
            if (!IsClaude && !IsCodex && !IsGrok) return false;
            if (_showAccountLabel is null) ResolveAccountChip();
            return _showAccountLabel is true;
        }
    }

    /// <summary>The authoritative xAI billing snapshot for the isolated Grok login used by this chat.</summary>
    public GrokAccountInfo? GrokAccount
    {
        get
        {
            if (IsGrok && !_grokAccountResolved) ResolveAccountChip();
            return _grokAccount;
        }
    }

    /// <summary>Compact real account quota for the chat status pill (never session token counts).</summary>
    public string GrokUsageSummary => GrokAccount?.UsageSummary ?? (IsGrok ? "usage unavailable" : "");

    private string? _accountLabel;
    private bool? _showAccountLabel;
    private GrokAccountInfo? _grokAccount;
    private bool _grokAccountResolved;

    // Both chip properties used to call AccountService.List() on EVERY binding evaluation - two full synchronous disk
    // sweeps (every account.json + credentials.json + ~/.claude.json) on the UI thread per header bind. Resolve once
    // and hold it until the account set actually changes.
    private void ResolveAccountChip()
    {
        if (IsCodex)
        {
            var list = CodexAccountService.Instance.List();
            _accountLabel = list.FirstOrDefault(x => x.Id == AccountId)?.Label ?? "OpenAI login";
            _showAccountLabel = list.Count > 1;
        }
        else if (IsGrok)
        {
            var list = GrokAccountService.Instance.List();
            _grokAccount = list.FirstOrDefault(x => x.Id == AccountId);
            _grokAccountResolved = true;
            _accountLabel = _grokAccount?.Label ?? "Grok login";
            _showAccountLabel = list.Count > 1;
        }
        else
        {
            var list = AccountService.Instance.List();
            _accountLabel = list.FirstOrDefault(x => x.Id == AccountId)?.Label ?? "default login";
            _showAccountLabel = list.Count > 1;
        }
    }

    // Without this the chip is evaluated once at bind and never again: add a second account to a one-account setup and
    // the already-open chat stays chip-less forever - the original "my switch didn't register" symptom, unfixed.
    private void OnAccountsChanged() => _ui.BeginInvoke(() =>
    {
        _accountLabel = null;
        _showAccountLabel = null;
        _grokAccount = null;
        _grokAccountResolved = false;
        Raise(nameof(AccountLabel));
        Raise(nameof(ShowAccountLabel));
        Raise(nameof(GrokAccount));
        Raise(nameof(GrokUsageSummary));
    });

    /// <summary>
    /// Codex chats stay pinned to the account they were created under (same as Claude/Grok).
    /// Previously this re-homed every chat onto the active login at spawn, which mixed workspaces when
    /// switching accounts. Explicit re-home still goes through <c>MoveCodexChatToAccount</c>.
    /// </summary>
    public void SyncCodexAccountWithActive()
    {
        // No-op by design: AccountId captured at construction / restore is authoritative.
        // Keep the method so StartCore call sites stay stable.
    }

    public ChatViewModel(string cwd, string? resume = null, bool fork = false, string? title = null,
        string? accountId = null, string provider = "claude")
    {
        _ui = Application.Current.Dispatcher;
        // A private CollectionView (not the shared default view Items' ListBox uses) filtered to the user's own
        // prompts, so the navigator can list them without ever hiding transcript rows. Live: new prompts appear here.
        _userMessagesSource = new CollectionViewSource { Source = Items };
        _userMessagesSource.Filter += OnFilterUserMessages;
        UserMessages = _userMessagesSource.View;
        Cwd = cwd;
        ResumeSessionId = resume;
        ForkSession = fork;
        Provider = provider?.Trim().ToLowerInvariant() switch { "codex" => "codex", "kimi" => "kimi", "grok" => "grok", _ => "claude" };
        _model = Provider switch
        {
            "codex" => AppSettings.Current.DefaultCodexModel,
            "kimi" => AppSettings.Current.DefaultKimiModel,
            "grok" => AppSettings.Current.DefaultGrokModel,
            _ => AppSettings.Current.DefaultModel,
        };
        _effort = Provider switch
        {
            "codex" => AppSettings.Current.DefaultCodexEffort,
            "kimi" => AppSettings.Current.DefaultKimiEffort,
            "grok" => AppSettings.Current.DefaultGrokEffort,
            _ => AppSettings.Current.DefaultEffort,
        };
        RefreshModelPicker(AppSettings.Current.DefaultProvider);
        AccountId = IsClaude
            ? accountId ?? AccountService.Instance.ActiveId
            : IsCodex
                ? accountId ?? CodexAccountService.Instance.ActiveId
                : IsGrok ? accountId ?? GrokAccountService.Instance.ActiveId : null;
        _title = OneLine(title ?? Path.GetFileName(cwd.TrimEnd('\\', '/')));
        if (resume is not null && !fork) _sessionId = resume;
        Attachments.CollectionChanged += (_, _) => Raise(nameof(HasAttachments));
        ModelSpeedService.Instance.Updated += OnModelSpeedsUpdated;          // singleton event: detached in Close()
        if (IsClaude) AccountService.AccountsChanged += OnAccountsChanged;   // detached in Close() so closed chats can't leak
        if (IsCodex) CodexAccountService.AccountsChanged += OnAccountsChanged;
        if (IsGrok) GrokAccountService.AccountsChanged += OnAccountsChanged;
        if (SupportsSwarms) SwarmBudget.CapacityChanged += OnSwarmCapacityChanged;
    }

    // Only the user's own text prompts belong in the navigator: subagent echoes, attachment-only rows, and
    // app-injected Bridge/manager traffic ([BRIDGE], manager kickoff/updates, work orders, announcements) are noise.
    private static void OnFilterUserMessages(object sender, FilterEventArgs e)
        => e.Accepted = e.Item is UserItem { HasText: true, FromSubagent: false } u
                        && !IsSystemInjectedPrompt(u.Text);

    /// <summary>True for prompts the app (not the human) stuffed into a pane as a user turn — bridge preludes,
    /// manager loop traffic, and broadcast announcements. Those stay in the transcript but must not clutter
    /// "Your messages".</summary>
    internal static bool IsSystemInjectedPrompt(string text)
    {
        var t = text.AsSpan().TrimStart();
        // Join/peer notes. StripInjectedPrelude drops these before they ever reach the transcript; this stays as a
        // backstop for older saved sessions whose stored turns still have the prelude glued on front.
        if (t.StartsWith("[BRIDGE]", StringComparison.OrdinalIgnoreCase)) return true;
        // Manager kickoff / updates / worker orders (with or without the leading crown glyph).
        if (t.StartsWith("👑 [MANAGER", StringComparison.Ordinal)
            || t.StartsWith("👑 [FROM MANAGER", StringComparison.Ordinal)
            || t.StartsWith("[MANAGER", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("[FROM MANAGER", StringComparison.OrdinalIgnoreCase)) return true;
        // Bridge broadcast (AnnounceToBridge).
        if (t.StartsWith("📢 ANNOUNCEMENT", StringComparison.Ordinal)
            || t.StartsWith("ANNOUNCEMENT (broadcast", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Staged on <see cref="Prelude"/> after a rewind so the next turn knows the workspace moved under it.</summary>
    internal const string RewindNote = "VibeCode restored the local workspace to before an earlier user prompt and "
                                       + "removed that prompt and everything after it from the visible transcript. "
                                       + "Treat the next user message as a fresh request and inspect the files again; "
                                       + "do not assume the reverted edits from your earlier response are still present.";

    /// <summary>
    /// A bridge/swarm turn goes on the wire as "&lt;app-injected notes&gt;\n\n&lt;what the human typed&gt;" — the model
    /// must read the notes, but the user should only ever see their own words. Return the text with those leading
    /// notes removed ("" when the whole turn was app-injected). Providers echo the wire form back, and a resumed
    /// transcript replays it, so both paths would otherwise render a peer-join notice glued onto the user's bubble.
    /// </summary>
    internal static string StripInjectedPrelude(string text)
    {
        // Every part of the wire form is separated by a blank line, so whole notes drop out block by block. A note
        // that spans lines (the bridge rules, the swarm contract) is one block and goes as a unit.
        var blocks = text.Replace("\r\n", "\n").Split("\n\n");
        var i = 0;
        // An empty block is only a separator once a note has been taken - a human message may open with a blank line.
        while (i < blocks.Length && (IsInjectedBlock(blocks[i]) || (i > 0 && blocks[i].Trim().Length == 0))) i++;
        return i == 0 ? text : string.Join("\n\n", blocks.Skip(i)).Trim();
    }

    private static bool IsInjectedBlock(string block)
    {
        var t = block.AsSpan().TrimStart();
        // "[BRIDGE]" peer notes, "[BRIDGE SETTINGS]", and the "[BRIDGE MODE]" rules appendix.
        return t.StartsWith("[BRIDGE", StringComparison.OrdinalIgnoreCase)
               || t.StartsWith("[VIBECODE SWARM REQUEST]", StringComparison.Ordinal)
               || t.StartsWith(RewindNote, StringComparison.Ordinal);
    }

    private void OnSwarmCapacityChanged()
    {
        Post(() =>
        {
            RaiseSwarmProperties();
            if (_status == "idle" && _sendQueue.Count > 0) FlushQueue();
        });
    }

    public void Start()
    {
        var version = Interlocked.Increment(ref _startVersion);
        if (IsClaude && ResumeSessionId is not null && !_transcriptLoaded)
        {
            _ = ReplayTranscriptAndStartAsync(version, ResumeSessionId);
            return;
        }

        ContinueStart(version);
    }

    /// <summary>
    /// Transcript discovery and JSON parsing are disk-bound and used to run on the dispatcher once for every resumed
    /// bridge pane. Replay them in small background-priority batches so opening a four-agent chat paints immediately
    /// and remains interactive while its one-time history hydration finishes.
    /// </summary>
    private async Task ReplayTranscriptAndStartAsync(int version, string sessionId)
    {
        List<TranscriptMessage> transcript;
        try
        {
            transcript = await Task.Run(() => SessionCatalog.LoadTranscript(Cwd, sessionId)).ConfigureAwait(false);
        }
        catch
        {
            transcript = new List<TranscriptMessage>();
        }

        for (var offset = 0; offset < transcript.Count; offset += TranscriptReplayBatchSize)
        {
            if (version != Volatile.Read(ref _startVersion)) return;
            var batch = transcript.Skip(offset).Take(TranscriptReplayBatchSize).ToList();
            await _ui.InvokeAsync(() =>
            {
                if (version != Volatile.Read(ref _startVersion) || Status == "closed") return;
                foreach (var message in batch)
                    IngestMessagePayload(message.Type, message.Message, message.ParentToolUseId, live: false);
            }, DispatcherPriority.Background);
        }

        await _ui.InvokeAsync(() =>
        {
            if (version != Volatile.Read(ref _startVersion) || Status == "closed") return;
            _transcriptLoaded = true;
            if (transcript.Count > 0) Items.Add(new DividerItem { Label = "Resumed session" });
            // Replayed Claude Agent cards describe a previous process. Sidechain rows are intentionally omitted by
            // SessionCatalog, so no old child can still be running even when a truncated transcript lacks its result.
            foreach (var subagent in Subagents.Where(agent => agent.IsActive))
            {
                subagent.Status = "completed";
                subagent.Activity = "Previous run";
            }
            if (Subagents.Count > 0) RaiseSubagentRosterProperties();
            ContinueStart(version);
        }, DispatcherPriority.Background);
    }

    private void ContinueStart(int version)
    {
        if (version != Volatile.Read(ref _startVersion) || Status == "closed") return;

        // An EXPIRED per-account token makes the CLI silently fall back to the shared ~/.claude login - i.e. this chat
        // would run as a DIFFERENT account than the one it claims ("I'm on javier but it thinks I'm steve"). Refresh
        // the stored token first; only spawn once we either have a live token or have told the user loudly.
        if (IsClaude && AccountService.Instance.TokenExpiring(AccountId, TimeSpan.FromMinutes(5)))
        {
            _ = StartAfterTokenRefreshAsync(version);
            return;
        }

        StartCore();
    }

    /// <summary>Refresh this chat's account token, then spawn. If the login is truly dead (refresh token consumed or
    /// revoked), refuse to spawn as the wrong account - show what happened and how to fix it instead.</summary>
    private async Task StartAfterTokenRefreshAsync(int version)
    {
        try { await AccountService.Instance.RefreshStoredTokenAsync(AccountId!); }
        catch { /* verdict below is based on the resulting token state */ }
        Post(() =>
        {
            if (version != Volatile.Read(ref _startVersion) || Status == "closed") return;
            if (AccountService.Instance.TokenExpiring(AccountId, TimeSpan.FromMinutes(1)))
            {
                Status = "error";
                Items.Add(new BannerItem
                {
                    Level = "error",
                    Text = $"{AccountLabel}'s saved login has expired and couldn't be refreshed, so this chat can't " +
                           "start under that account (it would silently run as whichever account the shared login " +
                           "holds). Fix: account menu → \"Add another account\" and sign in as " + AccountLabel +
                           " once — then start a new chat.",
                });
                return;
            }
            StartCore();   // token is live again - spawn as the right account
        });
    }

    private void StartCore()
    {
        SyncCodexAccountWithActive();   // must run BEFORE HomeFor(AccountId) picks this session's CODEX_HOME
        var swarmsEnabled = SupportsSwarms && AppSettings.Current.AgentSwarmsEnabled;
        _sessionSwarmsEnabled = swarmsEnabled;
        _sessionSwarmWorkerCap = swarmsEnabled
            ? SwarmPolicy.ClampMaxWorkers(AppSettings.Current.SwarmMaxWorkers)
            : null;
        var childAgentPolicy = SupportsSwarms ? SwarmPolicy.SessionRuntimeRule(swarmsEnabled) : null;
        var sessionSystemPrompt = string.Join("\n\n", new[] { AppendSystemPrompt, childAgentPolicy }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        var mcpServers = McpCatalog.Snapshot(AppSettings.Current.McpServers);
        RaiseSwarmProperties();
        try
        {
            _autoApprovalIntegrityWarningShown = false;
            _session = Provider switch
            {
                "codex" => new CodexSession(new CodexSessionOptions
                {
                    Cwd = Cwd,
                    HomeDirectory = CodexAccountService.Instance.HomeFor(AccountId),
                    Resume = ResumeSessionId,
                    ForkSession = ForkSession,
                    Model = string.Equals(_model, "default", StringComparison.OrdinalIgnoreCase) ? null : _model,
                    Effort = _effort,
                    Title = _title,
                    PermissionMode = _mode,
                    AppendSystemPrompt = sessionSystemPrompt,
                    SwarmsEnabled = swarmsEnabled,
                    SwarmMaxWorkers = SwarmMaxWorkers,
                    McpServers = mcpServers,
                }),
                "kimi" => new KimiSession(new KimiSessionOptions
                {
                    Cwd = Cwd,
                    Resume = ResumeSessionId,
                    ForkSession = ForkSession,
                    Model = string.Equals(_model, "default", StringComparison.OrdinalIgnoreCase) ? null : _model,
                    Effort = _effort,
                    PermissionMode = _mode,
                    AppendSystemPrompt = AppendSystemPrompt,
                    McpServers = mcpServers,
                }),
                "grok" => new GrokSession(new GrokSessionOptions
                {
                    Cwd = Cwd,
                    AuthFilePath = GrokAccountService.Instance.AuthPathFor(AccountId),
                    Resume = ResumeSessionId,
                    ForkSession = ForkSession,
                    Model = string.Equals(_model, "default", StringComparison.OrdinalIgnoreCase) ? null : _model,
                    Effort = _effort,
                    PermissionMode = _mode,
                    AppendSystemPrompt = sessionSystemPrompt,
                    McpServers = mcpServers,
                }),
                _ => new ClaudeSession(new ClaudeSessionOptions
                {
                    Cwd = Cwd,
                    Resume = ResumeSessionId,
                    ForkSession = ForkSession,
                    // restore the last-used model. "default" is the CLI's own default and is rejected as a --model
                    // value, so send nothing in that case (same result).
                    Model = string.Equals(_model, "default", StringComparison.OrdinalIgnoreCase) ? null : _model,
                    Effort = _effort,
                    AppendSystemPrompt = sessionSystemPrompt,
                    ConfigDirectory = AccountService.Instance.ConfigDirectory(AccountId),
                    FastMode = _fastMode,
                    SwarmsEnabled = swarmsEnabled,
                    SwarmMaxWorkers = SwarmMaxWorkers,
                    McpServers = mcpServers,
                }),
            };
        }
        catch (FileNotFoundException ex)
        {
            Status = "error";
            if (IsKimi || IsGrok) AuthNeeded = true;
            Items.Add(new BannerItem { Level = "error", Text = ex.Message });
            return;
        }

        var session = _session!;
        // One dispatcher op per message melted down on Codex: it streams reasoning/message deltas per token, so a
        // long turn queued tens of thousands of Background ops, each appending a few characters to a huge string
        // (a fresh full copy every time — gigabytes of LOH churn on big messages). Batch instead: enqueue on the
        // reader thread, drain everything queued in one op, and merge consecutive text/thinking deltas for the
        // same block into a single append before ingesting.
        var pendingSdk = new System.Collections.Concurrent.ConcurrentQueue<JsonNode>();
        var drainScheduled = 0;
        session.MessageReceived += node =>
        {
            pendingSdk.Enqueue(node);
            if (Interlocked.Exchange(ref drainScheduled, 1) == 1) return;
            Post(() =>
            {
                Interlocked.Exchange(ref drainScheduled, 0);
                var batch = new List<JsonNode>();
                while (pendingSdk.TryDequeue(out var queued)) batch.Add(queued);
                if (!ReferenceEquals(_session, session)) return;
                foreach (var merged in CoalesceStreamDeltas(batch)) IngestSdk(merged);
            });
        };
        session.PermissionRequested += req => Post(() =>
        {
            if (ReferenceEquals(_session, session)) OnPermissionRequested(req);
        });
        session.PermissionCancelled += id => Post(() =>
        {
            if (!ReferenceEquals(_session, session)) return;
            if (_pendingPerms.Remove(id, out var item)) item.State = "cancelled";
        });
        session.Initialized += () => Post(() =>
        {
            if (!ReferenceEquals(_session, session)) return;
            Commands.Clear();
            foreach (var c in session.Commands.OfType<JsonObject>())
                Commands.Add(new CommandChoice
                {
                    Name = c["name"]?.GetValue<string>() ?? "",
                    Description = c["description"]?.GetValue<string>(),
                    ArgumentHint = c["argumentHint"]?.GetValue<string>(),
                });
            Models.Clear();
            foreach (var m in session.Models.OfType<JsonObject>())
            {
                var levels = (m["supportedEffortLevels"] as JsonArray)?
                    .Select(x => x?.GetValue<string>()).OfType<string>().ToList() ?? new List<string>();
                Models.Add(new ModelChoice
                {
                    Provider = Provider,
                    Value = m["value"]?.GetValue<string>() ?? "",
                    Display = m["displayName"]?.GetValue<string>() ?? m["value"]?.GetValue<string>() ?? "?",
                    Description = m["description"]?.GetValue<string>(),
                    ResolvedModel = m["resolvedModel"]?.GetValue<string>(),
                    EffortLevels = levels,
                    SupportsEffort = m["supportsEffort"]?.GetValue<bool>() ?? false,
                    SupportsAutoMode = m["supportsAutoMode"]?.GetValue<bool>() ?? false,
                    SupportsFastMode = m["supportsFastMode"]?.GetValue<bool>() ?? false,
                });
            }
            // Live Claude catalogs lag new IDs (Opus 5). ClaudeSession also injects, but merge here so the
            // pane's own Models list (what the picker binds when CanApply) always has the extras.
            if (IsClaude) ProviderModelCatalog.EnsureClaudeModelsVisible(Models);
            ProviderModelCatalog.Remember(Provider, Models);
            RefreshModelPicker(AppSettings.Current.DefaultProvider);
            RebuildEffortOptions();
            Raise(nameof(ModelDisplay));
            if (Status == "starting") Status = "idle";
            if (IsClaude) UsageService.Instance.Refresh();
            // Kimi's access token only lives ~15 minutes; a session that just started has refreshed it on disk, so
            // this is the moment the quota read is most likely to succeed.
            if (IsKimi) KimiUsageService.Instance.Refresh();
        });
        session.Exited += (code, stderr) => Post(() =>
        {
            if (!ReferenceEquals(_session, session)) return;
            ReleaseSwarmLease();
            CompleteActiveRollback();
            if (Status is "closed") return;
            if (_activeExtendedDispatch is not null) RequeueActiveExtendedDispatch();
            if (_sendQueue.Any(item => item.Extended))
                PauseExtendedQueue("the provider exiting", usage: false);
            Status = "error";
            var tail = string.IsNullOrWhiteSpace(stderr) ? "" : $"\n\n```\n{stderr[Math.Max(0, stderr.Length - 600)..]}\n```";
            Items.Add(new BannerItem { Level = "error", Text = $"{ProviderDisplay} exited (code {code}).{tail}" });
            ItemsChanged?.Invoke();
        });
        try { session.Start(); }
        catch (Exception ex)
        {
            session.Dispose();
            if (!ReferenceEquals(_session, session)) return;
            Status = "error";
            if (IsKimi || IsGrok) AuthNeeded = true;
            Items.Add(new BannerItem { Level = "error", Text = $"Could not start {ProviderDisplay}: {ex.Message}" });
            ItemsChanged?.Invoke();
        }
    }

    private void Post(Action action) => _ui.BeginPriorityInvoke(action);

    // ---------------- send / control ----------------

    /// <summary>Returns true if the message was actually sent (false when the session is gone or there's nothing to send).</summary>
    public bool Send(string text, IReadOnlyList<Attachment>? attachments = null)
    {
        if (_session is null || _session.HasExited) return false;
        var hasText = !string.IsNullOrWhiteSpace(text);
        var atts = attachments is { Count: > 0 } ? attachments.ToList() : null;
        if (!hasText && atts is null) return false;     // nothing to send

        // Record when the composer accepts the prompt, not when a queued turn eventually reaches the CLI. This makes
        // Up recall the text immediately after Send clears the composer and avoids a duplicate when FlushQueue runs.
        if (hasText) _promptHistory.Record(text);

        var useSwarm = SwarmNextTurn && SwarmsAvailable;
        if (SwarmNextTurn) SwarmNextTurn = false; // one shot: capture now, including while the prompt is queued

        // Extended mode deliberately routes even an idle prompt through the FIFO. That makes the first request and
        // every later request follow the same retry bookkeeping, so a usage-limit failure cannot consume the head and
        // leave only the requests behind it to resume. App-injected Bridge/manager traffic keeps the lightweight queue.
        var extended = ExtendedQueueEnabled && !IsSystemInjectedPrompt(text);
        if (extended)
        {
            QueuePrompt(text, atts, useSwarm, waitingForCapacity: false, extended: true);
            if (!IsWorking && !ExtendedQueuePaused) Post(FlushQueue);
            MessageSent?.Invoke();
            return true;
        }

        // A turn is already running: queue this message (shown greyed at the bottom) and auto-send it when the turn
        // finishes - same feel as the Claude Code CLI. Multiple queued messages fire one per turn, in order.
        if (IsWorking || _sendQueue.Count > 0)
        {
            QueuePrompt(text, atts, useSwarm, waitingForCapacity: !IsWorking, extended: false);
            if (!IsWorking) Post(FlushQueue); // preserve FIFO behind a capacity-waiting swarm
            MessageSent?.Invoke();
            return true;
        }
        if (SendNow(text, atts, useSwarm) is null)
            QueuePrompt(text, atts, useSwarm, waitingForCapacity: true, extended: false);
        MessageSent?.Invoke();
        return true;
    }

    public bool HasQueued => _sendQueue.Count > 0;

    private void QueuePrompt(string text, IReadOnlyList<Attachment>? atts, bool useSwarm, bool waitingForCapacity,
        bool extended)
    {
        // Fold a second prompt into the one already pending instead of stacking another card. Only the queue head
        // owns "Send prompt now", so every card below it was unreachable except through its own X.
        // Two prompts must still queue separately: the one-shot swarm choice is captured per prompt, and
        // manager-injected work orders are recognised by their text prefix (see PurgeManagerInjectedQueue), so
        // blending one with a user prompt would make the pair purge — or survive — as a unit.
        if (!extended && _sendQueue.Count > 0 && !IsManagerInjectedPrompt(text))
        {
            var tail = _sendQueue.Last();
            if (!tail.Extended && tail.UseSwarm == useSwarm && !IsManagerInjectedPrompt(tail.Text) && Items.Contains(tail))
            {
                tail.Text = string.IsNullOrWhiteSpace(tail.Text) ? text : $"{tail.Text}\n\n{text}";
                if (atts is { Count: > 0 })
                    tail.Attachments = tail.Attachments is { Count: > 0 } prev ? prev.Concat(atts).ToList() : atts;
                PinQueuedItemsToEnd();
                ItemsChanged?.Invoke();
                return;
            }
        }

        var q = new QueuedItem
        {
            Text = text,
            Owner = this,
            Attachments = atts,
            UseSwarm = useSwarm,
            Extended = extended,
            WaitingForSwarmCapacity = waitingForCapacity,
            IsQueueHead = _sendQueue.Count == 0,
        };
        _sendQueue.Enqueue(q);
        // Always land at the true end of the transcript — stream/tool cards keep
        // appending above any already-queued prompts via InsertBeforeQueued.
        Items.Add(q);
        PinQueuedItemsToEnd();
        RefreshQueueState();
        ItemsChanged?.Invoke();
    }

    /// <summary>Index of the first still-queued prompt in the transcript, or -1.</summary>
    private int FirstQueuedIndex()
    {
        for (var i = 0; i < Items.Count; i++)
            if (Items[i] is QueuedItem) return i;
        return -1;
    }

    /// <summary>Insert a root transcript item above any greyed "Queued · send when done" cards so the
    /// pending prompts stay pinned at the bottom while the agent keeps talking and editing.</summary>
    private void InsertBeforeQueued(ItemVm item)
    {
        var i = FirstQueuedIndex();
        if (i < 0) Items.Add(item);
        else Items.Insert(i, item);
    }

    /// <summary>Re-park every QueuedItem at the end of Items, preserving FIFO order from _sendQueue.
    /// No-op when none exist or they are already a contiguous tail.</summary>
    private void PinQueuedItemsToEnd()
    {
        if (_sendQueue.Count == 0) return;
        var ordered = new List<QueuedItem>(_sendQueue.Count);
        foreach (var q in _sendQueue)
            if (Items.Contains(q)) ordered.Add(q);
        if (ordered.Count == 0) return;

        // Already a contiguous suffix in queue order?
        var start = Items.Count - ordered.Count;
        if (start >= 0)
        {
            var ok = true;
            for (var i = 0; i < ordered.Count; i++)
            {
                if (!ReferenceEquals(Items[start + i], ordered[i])) { ok = false; break; }
            }
            if (ok) return;
        }

        foreach (var q in ordered) Items.Remove(q);
        foreach (var q in ordered) Items.Add(q);
    }

    /// <summary>Re-number the visible FIFO and refresh every queue-derived binding after an enqueue, dispatch,
    /// cancellation, retry, or chunk-size change.</summary>
    private void RefreshQueueState()
    {
        var total = _sendQueue.Count;
        var position = 0;
        foreach (var item in _sendQueue) item.UpdateQueuePosition(++position, total);

        if (total == 0 && _activeExtendedDispatch is null)
        {
            _extendedQueuePauseReason = null;
            _extendedQueueUsageTimer?.Stop();
        }

        Raise(nameof(HasQueued));
        Raise(nameof(CanSendQueuedNow));
        RaiseExtendedQueueProperties();
    }

    /// <summary>Actually dispatch a message to the CLI. Null means an explicit swarm is waiting for app capacity.</summary>
    private UserItem? SendNow(string text, IReadOnlyList<Attachment>? atts, bool useSwarm = false)
    {
        useSwarm = useSwarm && SwarmsAvailable;
        var wasStarting = _status == "starting";
        SwarmLease? swarmLease = null;
        if (useSwarm)
        {
            swarmLease = SwarmBudget.TryAcquire(SwarmMaxWorkers);
            if (swarmLease is null) return null;
            _activeSwarmLease = swarmLease;
        }
        var session = _session!;
        var hasText = !string.IsNullOrWhiteSpace(text);
        // Startup sessions already carry their requested model/effort in their launch options. A pre-init runtime
        // set-model request can race provider initialization, so only push it once the session is ready.
        if (!wasStarting) PushEffort();
        var userItem = new UserItem { Text = text, Attachments = atts, Owner = this };
        Items.Add(userItem);
        _lastLocalUserText = hasText ? text : null;
        _lastLocalUserAt = DateTime.UtcNow;
        if (hasText && (_title.Length == 0 || _title == Path.GetFileName(Cwd.TrimEnd('\\', '/'))))
        {
            var flat = OneLine(text);                       // truncate the one-line form, not the raw multi-line text
            Title = flat.Length > 60 ? flat[..60] + "…" : flat;
        }
        ResetLiveUsage();
        _interruptRequested = false;
        AuthNeeded = false;
        Status = "preparing";
        ItemsChanged?.Invoke();

        // A safe undo baseline reads and hashes the eligible project tree. On a cold first prompt that can take a few
        // hundred milliseconds, so doing it here used to freeze WPF for both ordinary chats and every Bridge pane.
        // Keep the provider turn ordered behind the snapshot, but let the dispatcher paint and accept input meanwhile.
        _ = PrepareCheckpointAndSendAsync(session, userItem, text, atts, hasText, swarmLease);
        return userItem;
    }

    private async Task PrepareCheckpointAndSendAsync(ICodingSession session, UserItem userItem, string text,
        IReadOnlyList<Attachment>? atts, bool hasText, SwarmLease? swarmLease)
    {
        TurnRollbackCheckpoint checkpoint;
        try
        {
            // Intentionally retain the WPF synchronization context: everything after the disk task touches bindings
            // and the provider session owned by this view model.
            checkpoint = await TurnRollbackCheckpoint.PrepareAsync(Cwd);
        }
        catch (Exception ex)
        {
            ReleaseSwarmLease(swarmLease);
            if (!ReferenceEquals(_session, session) || Status == "closed") return;
            if (ReferenceEquals(_activeExtendedDispatch?.UserItem, userItem))
            {
                RequeueActiveExtendedDispatch();
                PauseExtendedQueue("prompt preparation failing", usage: false);
            }
            Status = "error";
            Items.Add(new BannerItem
            {
                Level = "error",
                Text = $"Could not prepare the prompt for {ProviderDisplay}: {ex.Message}",
            });
            ItemsChanged?.Invoke();
            return;
        }

        // The pane can close, reconnect, or lose its provider while the background snapshot is running. Never send a
        // prompt into that stale session, and tear down the unused watcher without doing a second workspace scan.
        if (!ReferenceEquals(_session, session) || session.HasExited || _status != "preparing")
        {
            checkpoint.AbandonBeforeDispatch();
            ReleaseSwarmLease(swarmLease);
            if (ReferenceEquals(_activeExtendedDispatch?.UserItem, userItem))
            {
                RequeueActiveExtendedDispatch();
                PauseExtendedQueue("the provider becoming unavailable", usage: false);
            }
            return;
        }

        checkpoint.Activate();
        userItem.AttachRollbackCheckpoint(checkpoint);
        _activeRollback = checkpoint;
        RefreshUndoStack();
        Status = "running";

        // Bridge notices and the one-shot swarm contract ride on the wire but aren't shown as the user's text.
        var wireParts = new List<string>(3);
        if (Prelude is { Length: > 0 } pre) wireParts.Add(pre);
        if (swarmLease is not null)
            wireParts.Add(SwarmPolicy.BuildTurnDirective(Provider, swarmLease.GrantedWorkers, IsBridgeAgent));
        if (hasText) wireParts.Add(text);
        var wireText = string.Join("\n\n", wireParts.Where(part => !string.IsNullOrWhiteSpace(part)));
        Prelude = null;
        try
        {
            session.SendUser(BuildContent(wireText, !string.IsNullOrWhiteSpace(wireText), atts));
        }
        catch (Exception ex)
        {
            ReleaseSwarmLease();
            CompleteActiveRollback();
            if (ReferenceEquals(_activeExtendedDispatch?.UserItem, userItem))
            {
                RequeueActiveExtendedDispatch();
                PauseExtendedQueue("a send failure", usage: false);
            }
            Status = "error";
            Items.Add(new BannerItem { Level = "error", Text = $"Could not send to {ProviderDisplay}: {ex.Message}" });
        }
        ItemsChanged?.Invoke();
    }

    /// <summary>Turn finished: if the user queued messages while it ran, dequeue the next and send it now.</summary>
    private void FlushQueue() => FlushQueue(allowStarting: false);

    private void FlushQueue(bool allowStarting)
    {
        var canDispatch = _status == "idle" || (allowStarting && _status == "starting");
        if (!canDispatch || _sendQueue.Count == 0 || ExtendedQueuePaused) return;
        if (_session is null || _session.HasExited)
        {
            if (_sendQueue.Any(item => item.Extended))
            {
                PauseExtendedQueue("the provider becoming unavailable", usage: false);
                return;
            }
            foreach (var queued in _sendQueue) Items.Remove(queued);
            _sendQueue.Clear();
            _sendAllQueuedNowRequested = false;
            RefreshQueueState();
            return;
        }
        if (_sendAllQueuedNowRequested)
        {
            var batch = _sendQueue.ToList();
            var text = string.Join("\n\n", batch.Select(item => item.Text)
                .Where(part => !string.IsNullOrWhiteSpace(part)));
            var allAttachments = batch.SelectMany(item => item.Attachments ?? Array.Empty<Attachment>()).ToList();
            IReadOnlyList<Attachment>? attachments = allAttachments.Count == 0 ? null : allAttachments;
            var useSwarm = batch.Any(item => item.UseSwarm);
            if (SendNow(text, attachments, useSwarm) is null)
            {
                foreach (var item in batch) item.WaitingForSwarmCapacity = true;
                return;
            }

            foreach (var item in batch)
            {
                item.IsQueueHead = false;
                Items.Remove(item);
            }
            _sendQueue.Clear();
            _sendAllQueuedNowRequested = false;
            RefreshQueueState();
            return;
        }

        var q = _sendQueue.Peek();
        if (q.Extended)
        {
            DispatchExtendedQueueChunk();
            return;
        }

        if (SendNow(q.Text, q.Attachments, q.UseSwarm) is null)
        {
            q.WaitingForSwarmCapacity = true;
            return;
        }
        _sendQueue.Dequeue();
        q.IsQueueHead = false;
        if (_sendQueue.TryPeek(out var next)) next.IsQueueHead = true;
        Items.Remove(q);            // drop the greyed placeholder; SendNow already re-added it as a real user message
        RefreshQueueState();
    }

    private void DispatchExtendedQueueChunk()
    {
        var batch = _sendQueue.TakeWhile(item => item.Extended).Take(ExtendedQueueChunkSize).ToList();
        if (batch.Count == 0) return;

        var text = ExtendedQueueChunkText(batch);
        var allAttachments = batch.SelectMany(item => item.Attachments ?? Array.Empty<Attachment>()).ToList();
        IReadOnlyList<Attachment>? attachments = allAttachments.Count == 0 ? null : allAttachments;
        var useSwarm = batch.Any(item => item.UseSwarm);
        var userItem = SendNow(text, attachments, useSwarm);
        if (userItem is null)
        {
            foreach (var item in batch) item.WaitingForSwarmCapacity = true;
            return;
        }

        foreach (var item in batch)
        {
            var removed = _sendQueue.Dequeue();
            if (!ReferenceEquals(removed, item))
                throw new InvalidOperationException("Extended queue FIFO changed while a chunk was dispatching.");
            item.WaitingForSwarmCapacity = false;
            item.IsQueueHead = false;
            Items.Remove(item);
        }
        if (_sendQueue.TryPeek(out var next)) next.IsQueueHead = true;
        _activeExtendedDispatch = new ExtendedQueueDispatch(batch, userItem);
        RefreshQueueState();
    }

    /// <summary>One request stays byte-for-byte familiar. A 2-3 request chunk is explicitly framed so the model
    /// handles each independent instruction in FIFO order instead of blending them into one ambiguous paragraph.</summary>
    private static string ExtendedQueueChunkText(IReadOnlyList<QueuedItem> batch)
    {
        if (batch.Count == 1) return batch[0].Text;

        var text = new StringBuilder();
        text.AppendLine($"[EXTENDED QUEUE CHUNK · {batch.Count} REQUESTS]");
        text.AppendLine("Handle each independent request below in order before ending this turn.");
        for (var i = 0; i < batch.Count; i++)
        {
            var item = batch[i];
            text.AppendLine().AppendLine($"--- REQUEST {i + 1} OF {batch.Count} ---");
            if (!string.IsNullOrWhiteSpace(item.Text)) text.AppendLine(item.Text.Trim());
            else text.AppendLine("(Attachment-only request.)");
            if (item.Attachments is { Count: > 0 })
                text.AppendLine($"Attachments for this request: {string.Join(", ", item.Attachments.Select(a => a.FileName))}");
        }
        return text.ToString().TrimEnd();
    }


    /// <summary>Normal queue heads preserve the historical "send everything" shortcut. Extended heads resume a
    /// paused conveyor or interrupt the current turn and dispatch only the next configured chunk.</summary>
    public bool SendQueuedNow(QueuedItem q)
    {
        if (_sendQueue.Count == 0 || !ReferenceEquals(_sendQueue.Peek(), q) || !CanSendQueuedNow) return false;
        if (q.Extended)
        {
            if (ExtendedQueuePaused)
            {
                ResumeExtendedQueue();
                return true;
            }
            if (_status == "starting") FlushQueue(allowStarting: true);
            else Interrupt();
            return true;
        }

        _sendAllQueuedNowRequested = true;
        Raise(nameof(CanSendQueuedNow));
        if (_status == "starting") FlushQueue(allowStarting: true);
        else Interrupt();
        return true;
    }

    /// <summary>Remove a still-queued message (its X) before it gets sent.</summary>
    public bool CancelQueued(QueuedItem q)
    {
        if (!_sendQueue.Contains(q)) return false;
        var wasHead = ReferenceEquals(_sendQueue.Peek(), q);
        // rebuild the queue without q (Queue has no remove)
        var kept = _sendQueue.Where(x => !ReferenceEquals(x, q)).ToList();
        _sendQueue.Clear();
        foreach (var x in kept) _sendQueue.Enqueue(x);
        q.IsQueueHead = false;
        if (wasHead && _sendQueue.TryPeek(out var next)) next.IsQueueHead = true;
        Items.Remove(q);
        if (_sendQueue.Count == 0) _sendAllQueuedNowRequested = false;
        RefreshQueueState();
        if (_status == "idle" && _sendQueue.Count > 0 && !ExtendedQueuePaused) Post(FlushQueue);
        return true;
    }

    private void PauseExtendedQueue(string reason, bool usage)
    {
        if (_sendQueue.All(item => !item.Extended) && _activeExtendedDispatch is null) return;
        _extendedQueuePauseReason = usage ? "usage" : reason;
        _extendedQueueInconclusiveChecks = 0;
        if (usage)
        {
            ScheduleNextExtendedQueueUsageCheck();
            EnsureExtendedQueueUsageTimer();
        }
        else
        {
            _extendedQueueUsageTimer?.Stop();
        }
        RefreshQueueState();
    }

    /// <summary>Manual resume button and automatic availability checks converge here. A provider result error leaves
    /// the session alive, so returning it to idle is enough to restart the ordinary FIFO dispatcher.</summary>
    public void ResumeExtendedQueue()
    {
        if (!ExtendedQueuePaused) return;
        _extendedQueuePauseReason = null;
        _extendedQueueUsageTimer?.Stop();
        RefreshQueueState();
        if (_session is null || _session.HasExited || Status == "closed") return;
        if (_status == "error") Status = "idle"; // Status posts FlushQueue for us
        else if (_status == "idle") Post(FlushQueue);
    }

    private void RequeueActiveExtendedDispatch()
    {
        if (_activeExtendedDispatch is not { } dispatch) return;
        _activeExtendedDispatch = null;

        var pending = _sendQueue.ToList();
        _sendQueue.Clear();
        foreach (var item in dispatch.Items)
        {
            item.RetryCount++;
            item.WaitingForSwarmCapacity = false;
            _sendQueue.Enqueue(item);
            if (!Items.Contains(item)) Items.Add(item);
        }
        foreach (var item in pending) _sendQueue.Enqueue(item);
        foreach (var item in _sendQueue) item.IsQueueHead = false;
        if (_sendQueue.TryPeek(out var head)) head.IsQueueHead = true;
        PinQueuedItemsToEnd();
        RefreshQueueState();
    }

    private void EnsureExtendedQueueUsageTimer()
    {
        if (_extendedQueueUsageTimer is null)
        {
            _extendedQueueUsageTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            _extendedQueueUsageTimer.Tick += async (_, _) =>
            {
                if (!ExtendedQueuePausedForUsage || _sendQueue.Count == 0)
                {
                    _extendedQueueUsageTimer.Stop();
                    return;
                }
                RaiseExtendedQueueProperties(); // refresh the "checks again in …" countdown
                if (DateTimeOffset.Now >= _extendedQueueNextUsageCheck)
                    await CheckExtendedQueueUsageAsync();
            };
        }
        _extendedQueueUsageTimer.Start();
    }

    private void ScheduleNextExtendedQueueUsageCheck()
    {
        var now = DateTimeOffset.Now;
        var fallback = now + ExtendedQueueUsagePollInterval;
        var reset = KnownUsageResetAt();
        _extendedQueueNextUsageCheck = reset is { } at && at > now && at + TimeSpan.FromSeconds(15) < fallback
            ? at + TimeSpan.FromSeconds(15)
            : fallback;
        RaiseExtendedQueueProperties();
    }

    private async Task CheckExtendedQueueUsageAsync()
    {
        if (_extendedQueueUsageCheckRunning || !ExtendedQueuePausedForUsage || _sendQueue.Count == 0) return;
        _extendedQueueUsageCheckRunning = true;
        bool? available = null;
        try { available = await ProviderUsageAvailableAsync(); }
        catch { /* two inconclusive checks fall back to one real queued request below */ }
        finally { _extendedQueueUsageCheckRunning = false; }

        if (!ExtendedQueuePausedForUsage || _sendQueue.Count == 0) return;
        if (available == true)
        {
            ResumeExtendedQueue();
            return;
        }
        if (available is null && ++_extendedQueueInconclusiveChecks >= 2)
        {
            // Some logins expose no readable usage endpoint. A single real request every six minutes is the honest
            // fallback: if quota is still exhausted its result re-pauses the preserved chunk, never flooding the API.
            ResumeExtendedQueue();
            return;
        }
        if (available == false) _extendedQueueInconclusiveChecks = 0;
        ScheduleNextExtendedQueueUsageCheck();
    }

    private async Task<bool?> ProviderUsageAvailableAsync()
    {
        if (IsClaude)
        {
            if (AccountId is { Length: > 0 })
            {
                var usage = await AccountService.Instance.RefreshUsageAsync(AccountId);
                return usage is { HasNumbers: true } ? !usage.AtLimit : null;
            }
            UsageService.Instance.Refresh(force: true);
            return UsageService.Instance.HasData
                ? !UsageService.Instance.Limits.Any(limit => limit.Percent >= 100)
                : null;
        }

        if (IsCodex)
        {
            var accounts = await CodexAccountService.Instance.RefreshAllAsync(forceRefresh: false);
            var account = accounts.FirstOrDefault(item => item.Id == AccountId);
            return account is null ? null : !account.AtLimit;
        }

        if (IsKimi)
        {
            KimiUsageService.Instance.Refresh(force: true);
            await Task.Delay(1500);
            return KimiUsageService.Instance.HasData ? !KimiUsageService.Instance.AtLimit : null;
        }

        if (IsGrok)
        {
            var accounts = await GrokAccountService.Instance.RefreshAllAsync();
            var account = accounts.FirstOrDefault(item => item.Id == AccountId);
            return account is null ? null : !account.AtLimit;
        }

        return null;
    }

    private DateTimeOffset? KnownUsageResetAt()
    {
        if (IsClaude && AccountId is { Length: > 0 }
            && AccountService.Instance.CachedUsage(AccountId)?.SessionReset is { Length: > 0 } raw)
        {
            var zone = raw.IndexOf(" (", StringComparison.Ordinal);
            if (zone > 0) raw = raw[..zone];
            if (DateTime.TryParse(raw, out var local))
            {
                if (local < DateTime.Now.AddDays(-1)) local = local.AddYears(1);
                return new DateTimeOffset(local);
            }
        }

        if (IsCodex)
        {
            var account = CodexAccountService.Instance.List().FirstOrDefault(item => item.Id == AccountId);
            return account?.UsageLimits
                .Where(limit => limit.Percent >= 100 || limit.HasStatus)
                .Select(limit => limit.ResetsAtUnixSeconds is { } unix
                    ? DateTimeOffset.FromUnixTimeSeconds(unix)
                    : (DateTimeOffset?)null)
                .Where(reset => reset is not null)
                .Min();
        }

        if (IsKimi)
            return KimiUsageService.Instance.Limits.Where(limit => limit.Percent >= 100)
                .Select(limit => limit.ResetsAt).Where(reset => reset is not null).Min();

        if (IsGrok)
            return GrokAccountService.Instance.List().FirstOrDefault(item => item.Id == AccountId)?.UsageResetsAt;

        return null;
    }

    internal static bool IsUsageLimitResult(JsonNode result)
    {
        if (result["is_error"]?.GetValue<bool>() != true) return false;
        var detail = new StringBuilder(result["subtype"]?.ToString());
        detail.Append(' ').Append(result["result"]?.ToString());
        if (result["errors"] is JsonArray errors)
            foreach (var error in errors) detail.Append(' ').Append(error?.ToString());
        var text = detail.ToString().ToLowerInvariant();
        return text.Contains("rate_limit") || text.Contains("rate limit") || text.Contains("rate-limit")
               || text.Contains("usage limit") || text.Contains("usage cap") || text.Contains("usage exhausted")
               || text.Contains("out of usage") || text.Contains("hit your limit")
               || text.Contains("quota") || text.Contains("insufficient_quota")
               || text.Contains("resource exhausted") || text.Contains("resource_exhausted")
               || text.Contains("too many requests") || text.Contains("credits depleted")
               || text.Contains("credit limit") || text.Contains("billing cycle")
               || System.Text.RegularExpressions.Regex.IsMatch(text, @"(?:http|status|code)\D{0,8}429\b");
    }

    /// <summary>Drop every still-queued app-injected manager work order / manager update so a stepped-down crown
    /// cannot keep firing into workers (or the former manager) after the user turns management off.</summary>
    public int PurgeManagerInjectedQueue()
    {
        if (_sendQueue.Count == 0) return 0;
        var drop = _sendQueue.Where(q => IsManagerInjectedPrompt(q.Text)).ToList();
        if (drop.Count == 0) return 0;
        var kept = _sendQueue.Where(q => !IsManagerInjectedPrompt(q.Text)).ToList();
        _sendQueue.Clear();
        foreach (var q in drop)
        {
            q.IsQueueHead = false;
            Items.Remove(q);
        }
        foreach (var q in kept) _sendQueue.Enqueue(q);
        if (_sendQueue.TryPeek(out var head)) head.IsQueueHead = true;
        if (_sendQueue.Count == 0) _sendAllQueuedNowRequested = false;
        RefreshQueueState();
        return drop.Count;
    }

    /// <summary>True for prompts the bridge manager loop stuffed into a pane (work orders + manager updates +
    /// manager kickoff). Used to purge the queue when the crown is removed.</summary>
    public static bool IsManagerInjectedPrompt(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.AsSpan().TrimStart();
        if (t.StartsWith("👑 [FROM MANAGER", StringComparison.Ordinal)) return true;
        if (t.StartsWith("👑 [MANAGER", StringComparison.Ordinal)) return true; // kickoff + MANAGER UPDATE
        if (t.StartsWith("[FROM MANAGER", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.StartsWith("[MANAGER", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Drop manager-loop notes that were staged on <see cref="Prelude"/> so the next human/agent send does
    /// not still ride a "fold this worker into the plan" order after the crown is gone.</summary>
    public void ClearManagerPreludes()
    {
        if (string.IsNullOrEmpty(Prelude)) return;
        var kept = Prelude.Split('\n')
            .Where(line =>
            {
                var s = line.TrimStart();
                if (s.StartsWith("👑 [FROM MANAGER", StringComparison.Ordinal)) return false;
                if (s.StartsWith("👑 [MANAGER", StringComparison.Ordinal)) return false;
                if (s.Contains("was just made this bridge's MANAGER", StringComparison.OrdinalIgnoreCase)) return false;
                if (s.Contains("just JOINED the bridge and is idle", StringComparison.OrdinalIgnoreCase)) return false;
                if (s.Contains("Fold it into the plan and put it to work", StringComparison.OrdinalIgnoreCase)) return false;
                if (s.Contains("@@DISPATCH", StringComparison.OrdinalIgnoreCase)) return false;
                return true;
            });
        var next = string.Join("\n", kept).Trim();
        Prelude = string.IsNullOrEmpty(next) ? null : next;
    }

    /// <summary>Make the rewind arrow a complete lifecycle action. If this exact prompt is still running, Stop first
    /// reaches the provider's parent and child-agent turns; then wait for the post-turn checkpoint scan before touching
    /// files. This is shared by normal chats and Bridge panes.</summary>
    public async Task<TurnRollbackResult> StopAndUndoPromptAsync(UserItem item)
    {
        if (_undoRequestInFlight is not null)
            return new(false, 0, $"A rewind is already in progress for {AgentDisplay}.");
        if (!Items.Contains(item) || item.RollbackCheckpoint is not { } checkpoint)
            return new(false, 0, "This prompt does not have a local file checkpoint.");

        RefreshUndoStack();
        if (item.UndoBlockedByLaterTurn || checkpoint.BlockedByNewerTurn)
            return new(false, 0, "Undo the newer prompt first. Rewinding in order keeps later file changes from being stranded on an older workspace state.");
        if (IsWorking && !ReferenceEquals(_activeRollback, checkpoint))
            return new(false, 0, $"Wait for the newer {AgentDisplay} turn to finish before undoing this prompt.");

        _undoRequestInFlight = item;
        item.SetUndoInProgress(true);
        try
        {
            if (IsWorking)
            {
                if (!CanInterrupt)
                    return new(false, 0, $"Wait for {AgentDisplay} to finish preparing this prompt, then try again.");
                Interrupt(); // every provider's interrupt covers its parent turn; Codex also targets active child turns
            }

            if (!checkpoint.IsCompleted
                && !await checkpoint.WaitUntilCompletedAsync(TimeSpan.FromSeconds(30)))
            {
                return new(false, 0,
                    $"Stop was sent to {AgentDisplay}, including its subagents, but the turn has not finished shutting down. "
                    + "No files were rewound; try the arrow again after Stop completes.");
            }

            return UndoPrompt(item);
        }
        finally
        {
            item.SetUndoInProgress(false);
            if (ReferenceEquals(_undoRequestInFlight, item)) _undoRequestInFlight = null;
        }
    }

    /// <summary>Restore one locally-sent prompt's verified file checkpoint. The window restores its composer only
    /// after this succeeds, so a conflict can never discard an unsent draft.</summary>
    public TurnRollbackResult UndoPrompt(UserItem item)
    {
        if (IsWorking)
            return new(false, 0, $"Wait for {AgentDisplay} to finish before undoing an earlier prompt.");
        if (!Items.Contains(item) || item.RollbackCheckpoint is not { } checkpoint)
            return new(false, 0, "This prompt does not have a local file checkpoint.");
        RefreshUndoStack();
        if (item.UndoBlockedByLaterTurn)
            return new(false, 0, "Undo the newer prompt first. Rewinding in order keeps later file changes from being stranded on an older workspace state.");

        var result = checkpoint.Rollback();
        if (!result.Success) return result;
        var removed = TrimTranscriptFrom(item);
        RefreshUndoStack();

        Prelude = string.IsNullOrWhiteSpace(Prelude) ? RewindNote : Prelude + "\n\n" + RewindNote;

        foreach (var file in Files.Where(file => !File.Exists(AbsoluteArtifactPath(file.Path))).ToList())
            Files.Remove(file);
        if (SelectedFile is not null && !Files.Contains(SelectedFile))
            SelectedFile = Files.FirstOrDefault();
        else
            RefreshPreview();
        ItemsChanged?.Invoke();
        return removed == 0
            ? result
            : result with { Message = result.Message + $" Removed {removed} transcript item{(removed == 1 ? "" : "s")}." };
    }

    /// <summary>
    /// Drop the rewound prompt and everything the agent produced after it, so the visible transcript matches the
    /// restored workspace. Queued-but-unsent prompts are deliberately kept — they are the user's pending input, not
    /// output of the undone turn. Nested subagent/compact-group tools are unregistered as well, otherwise a stale
    /// tool_use id would let a late tool_result reattach to a card that is no longer displayed.
    /// </summary>
    private int TrimTranscriptFrom(UserItem item)
    {
        var index = Items.IndexOf(item);
        if (index < 0) return 0;

        var keptQueued = new List<QueuedItem>();
        var removed = 0;
        for (var i = Items.Count - 1; i >= index; i--)
        {
            if (Items[i] is QueuedItem queued)
            {
                keptQueued.Insert(0, queued);
                Items.RemoveAt(i);
                continue;
            }
            ForgetItem(Items[i]);
            Items.RemoveAt(i);
            removed++;
        }
        foreach (var queued in keptQueued) Items.Add(queued);

        // The streamed-block slots only map the turn that just ended; keeping them would let a trailing delta append
        // text to an item that is no longer in the transcript.
        _streams.Clear();
        _lastLocalUserText = null;
        Raise(nameof(HasQueued));
        Raise(nameof(CanSendQueuedNow));
        return removed;
    }

    private void ForgetItem(ItemVm item)
    {
        switch (item)
        {
            case ToolItem tool:
                _toolById.Remove(tool.Id);
                foreach (var child in tool.Children) ForgetItem(child);
                break;
            case CompactToolGroupItem group:
                foreach (var tool in group.Tools) ForgetItem(tool);
                break;
            case PermItem perm:
                if (_pendingPerms.Remove(perm.RequestId)) perm.State = "cancelled";
                break;
        }
    }

    private void CompleteActiveRollback()
    {
        var checkpoint = _activeRollback;
        _activeRollback = null;
        if (checkpoint is null) { RefreshUndoStack(); return; }
        // Sealing a checkpoint rescans and hashes the whole eligible workspace. Inline on the dispatcher it froze
        // the window at every turn boundary (Stop, a queued send dispatching, closing a chat) for as long as the
        // scan took. Complete() is internally locked, and undo stays unavailable until the seal lands, so it can
        // run on a worker while the UI keeps painting.
        _ = Task.Run(() =>
        {
            try { checkpoint.Complete(); }
            finally { Post(RefreshUndoStack); }
        });
    }

    private void ReleaseSwarmLease()
    {
        var lease = Interlocked.Exchange(ref _activeSwarmLease, null);
        lease?.Dispose();
    }

    /// <summary>Release only the lease owned by a particular preparing send; a reconnected pane may own a newer one.</summary>
    private void ReleaseSwarmLease(SwarmLease? expected)
    {
        if (expected is null) return;
        var released = Interlocked.CompareExchange(ref _activeSwarmLease, null, expected);
        if (ReferenceEquals(released, expected)) expected.Dispose();
    }

    private void RefreshUndoStack()
    {
        var checkpointed = Items.OfType<UserItem>().Where(item => item.RollbackCheckpoint is not null).ToList();
        var latestPending = checkpointed.LastOrDefault(item => item.RollbackCheckpoint?.WasRolledBack == false);
        foreach (var item in checkpointed)
            item.SetUndoBlockedByLaterTurn(latestPending is not null
                                           && !ReferenceEquals(item, latestPending)
                                           && item.RollbackCheckpoint?.WasRolledBack == false);
    }

    private string AbsoluteArtifactPath(string path)
    {
        try { return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(Cwd, path)); }
        catch { return path; }
    }

    /// <summary>
    /// Build the user-message content: a bare string when there are no attachments, otherwise an
    /// array of content blocks (images/documents first, text last) per the Messages API shape.
    /// </summary>
    private static JsonNode BuildContent(string text, bool hasText, IReadOnlyList<Attachment>? atts)
    {
        if (atts is null) return JsonValue.Create(text)!;
        var arr = new JsonArray();
        foreach (var a in atts)
        {
            switch (a.Kind)
            {
                case "image" when a.Data is not null:
                    arr.Add(new JsonObject
                    {
                        ["type"] = "image",
                        ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = a.MediaType ?? "image/png", ["data"] = a.Base64 },
                    });
                    break;
                case "document" when a.Data is not null:
                    arr.Add(new JsonObject
                    {
                        ["type"] = "document",
                        ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = a.MediaType ?? "application/pdf", ["data"] = a.Base64 },
                    });
                    break;
                case "text" when a.Text is not null:
                    arr.Add(new JsonObject { ["type"] = "text", ["text"] = $"Attached file `{a.FileName}`:\n\n{a.Text}" });
                    break;
            }
        }
        if (hasText) arr.Add(new JsonObject { ["type"] = "text", ["text"] = text });
        return arr;
    }

    /// <summary>Push the current effort to the CLI (runtime path is set_model carrying an effort field).</summary>
    private void PushEffort()
    {
        var target = HasEffort ? _effort : null;      // never send effort to a model that has no effort control
        if (_appliedEffort == target) return;
        _appliedEffort = target;
        _ = _session?.SetModelAsync(string.IsNullOrEmpty(_model) ? null : _model, target);
    }

    public void SetEffort(string? level)
    {
        Effort = level;
        foreach (var o in EffortOptions)
            o.IsSelected = string.Equals(o.Value, level, StringComparison.OrdinalIgnoreCase);
        if (IsCodex) AppSettings.Current.DefaultCodexEffort = level;
        else if (IsKimi) AppSettings.Current.DefaultKimiEffort = level;
        else if (IsGrok) AppSettings.Current.DefaultGrokEffort = level;
        else AppSettings.Current.DefaultEffort = level;
        AppSettings.Current.Save();
        PushEffort();
    }

    /// <summary>Rebuild the effort picker from the currently-selected model's CLI-reported capabilities.</summary>
    private void RebuildEffortOptions()
    {
        EffortOptions.Clear();
        var m = Models.FirstOrDefault(x => x.Value == _model || (x.ResolvedModel is { } r && r == _model))
                ?? Models.FirstOrDefault(x => x.Value == "default");
        if (m is { SupportsEffort: true, EffortLevels.Count: > 0 })
        {
            var levels = m.EffortLevels.Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(EffortSortRank).ThenBy(lvl => lvl, StringComparer.OrdinalIgnoreCase).ToList();
            var meterSteps = levels.Count;
            if (m.SupportsAutoMode)
                EffortOptions.Add(new EffortChoice { Value = null, Label = "Auto", Description = $"{AgentDisplay} decides (default)", Rank = 0, MeterSteps = meterSteps, IsSelected = _effort is null });
            for (var i = 0; i < levels.Count; i++)
            {
                var lvl = levels[i];
                EffortOptions.Add(new EffortChoice
                {
                    Value = lvl,
                    Label = EffortLabel(lvl),
                    Description = EffortDesc(lvl),
                    Rank = i + 1,
                    MeterSteps = meterSteps,
                    IsSelected = string.Equals(lvl, _effort, StringComparison.OrdinalIgnoreCase),
                });
            }
            // If the remembered effort isn't offered by this model, fall back to Auto.
            if (_effort is not null && !levels.Contains(_effort, StringComparer.OrdinalIgnoreCase)) Effort = null;
        }
        Raise(nameof(HasEffort));
        Raise(nameof(EffortFilled));
        Raise(nameof(EffortEmpty));
        Raise(nameof(CanFastMode));   // model list just (re)built - refresh fast-mode availability
    }

    private static int EffortSortRank(string lvl) => lvl.ToLowerInvariant() switch
    {
        "low" => 1, "medium" => 2, "high" => 3, "xhigh" => 4, "max" => 5, "ultra" => 6, _ => 99,
    };
    private static string EffortLabel(string lvl) => lvl.ToLowerInvariant() switch
    {
        "xhigh" => "X-High",
        _ => lvl.Length == 0 ? lvl : char.ToUpper(lvl[0]) + lvl[1..],
    };
    private static string EffortDesc(string lvl) => lvl.ToLowerInvariant() switch
    {
        "low" => "Fastest - minimal thinking",
        "medium" => "Balanced",
        "high" => "More reasoning",
        "xhigh" => "Deep reasoning - coding default",
        "max" => "Maximum - slowest, most thorough",
        _ => "",
    };

    public void Interrupt()
    {
        if (!CanInterrupt || _session is null || _session.HasExited) return;
        _interruptRequested = true;
        Raise(nameof(CanSendQueuedNow));
        _ = _session.InterruptAsync();
    }

    public void CycleMode()
    {
        var order = new[] { "default", "auto", "plan" };
        SetMode(order[(Array.IndexOf(order, _mode) + 1 + order.Length) % order.Length]);
    }

    public void SetMode(string mode)
    {
        _userMode = mode;   // remember the user's choice so a CLI re-init can't silently revert it
        Mode = mode;
        _ = _session?.SetPermissionModeAsync(SessionMode(mode));
    }

    // VibeCode's "auto" is a CLIENT-side policy (allow edits+bash, prompt only on dangerous commands), so
    // the CLI stays in "default" and forwards every edit/bash to us. The CLI's own "auto" mode is different
    // (a server classifier that DENIES risky ops and bypasses our prompt), so we never send it. Other modes map 1:1.
    private static string ToCliMode(string mode) => mode == "auto" ? "default" : mode;
    private string SessionMode(string mode) => IsCodex ? mode : ToCliMode(mode);

    /// <summary>
    /// Refresh only the popup rows from the globally selected provider. The compact pill and <see cref="Models"/>
    /// remain owned by this running session, so changing accounts cannot restart it or send it a foreign model id.
    /// </summary>
    public void RefreshModelPicker(string? selectedProvider)
    {
        _modelPickerProvider = ProviderModelCatalog.Normalize(IsBridgeAgent ? Provider : selectedProvider);
        var ownProvider = ProviderModelCatalog.Normalize(Provider);
        var canApply = string.Equals(_modelPickerProvider, ownProvider, StringComparison.OrdinalIgnoreCase);
        // Live Claude catalogs lag new Anthropic IDs; re-merge before building the popup so Opus 5
        // appears even if this pane already loaded models before the extra was known.
        if (canApply && IsClaude && Models.Count > 0)
            ProviderModelCatalog.EnsureClaudeModelsVisible(Models);
        var catalog = canApply && Models.Count > 0
            ? (IReadOnlyList<ModelChoice>)Models.ToList()
            : ProviderModelCatalog.For(_modelPickerProvider);

        PickerModels.Clear();
        foreach (var model in catalog)
            PickerModels.Add(new ModelPickerChoice { Model = model, CanApply = canApply });
        Raise(nameof(ModelPickerHint));
        // Look the rows' live tok/s + latency up in the background; rows fill in through OnModelSpeedsUpdated.
        ModelSpeedService.Instance.Prefetch(catalog);
    }

    private void OnModelSpeedsUpdated()
    {
        foreach (var choice in PickerModels) choice.RefreshSpeed();
    }

    /// <summary>Apply a popup choice only when it belongs to this pane's live provider.</summary>
    public void SetPickerModel(ModelPickerChoice choice)
    {
        if (!choice.CanApply
            || !string.Equals(ProviderModelCatalog.Normalize(choice.Model.Provider),
                ProviderModelCatalog.Normalize(Provider), StringComparison.OrdinalIgnoreCase))
            return;
        SetModel(choice.Value);
    }

    public void SetModel(string? model)
    {
        Model = string.IsNullOrEmpty(model) ? null : model;
        if (IsCodex) AppSettings.Current.DefaultCodexModel = _model;
        else if (IsKimi) AppSettings.Current.DefaultKimiModel = _model;
        else if (IsGrok) AppSettings.Current.DefaultGrokModel = _model;
        else AppSettings.Current.DefaultModel = _model;
        AppSettings.Current.Save();
        RebuildEffortOptions();                          // effort tiers are per-model
        var effortForModel = HasEffort ? _effort : null; // one combined set_model call carries model + effort
        _appliedEffort = effortForModel;
        _ = _session?.SetModelAsync(string.IsNullOrEmpty(model) ? null : model, effortForModel);
    }

    public void Close()
    {
        Interlocked.Increment(ref _startVersion);   // cancel any background transcript replay / delayed token refresh
        CompleteActiveRollback();
        Status = "closed";
        _extendedQueueUsageTimer?.Stop();
        if (SupportsSwarms) SwarmBudget.CapacityChanged -= OnSwarmCapacityChanged;
        ModelSpeedService.Instance.Updated -= OnModelSpeedsUpdated;
        if (IsClaude) AccountService.AccountsChanged -= OnAccountsChanged;   // static event: without this every closed chat is pinned
        if (IsCodex) CodexAccountService.AccountsChanged -= OnAccountsChanged;
        if (IsGrok) GrokAccountService.AccountsChanged -= OnAccountsChanged;
        _session?.Dispose();
        ReleaseSwarmLease(); // release only after the provider process tree has stopped
    }

    /// <summary>Reconnect an auth-blocked pane after the account manager completes sign-in, preserving its cwd,
    /// resume id, model, mode, bridge identity, and visible transcript.</summary>
    public void RetryAfterSignIn()
    {
        if (!AuthNeeded) return;
        ReleaseSwarmLease();
        CompleteActiveRollback();
        if (IsCodex) AccountId = CodexAccountService.Instance.ActiveId;
        if (IsGrok)
        {
            AccountId = GrokAccountService.Instance.ActiveId;
            _accountLabel = null;
            _showAccountLabel = null;
            _grokAccount = null;
            _grokAccountResolved = false;
            Raise(nameof(AccountLabel));
            Raise(nameof(ShowAccountLabel));
            Raise(nameof(GrokAccount));
            Raise(nameof(GrokUsageSummary));
        }
        _session?.Dispose();
        _session = null;
        AuthNeeded = false;
        Status = "starting";
        Items.Add(new DividerItem { Label = $"Signed in · reconnecting {AgentDisplay}" });
        Start();
    }

    // ---------------- permissions ----------------

    private void OnPermissionRequested(PermissionRequest req)
    {
        // Questions/plans are interactive prompts, not tool permissions - always surface them.
        var interactive = req.ToolName is "AskUserQuestion" or "ExitPlanMode";

        // Bypass: auto-approve everything (the CLI usually already skips prompts in bypass; this is the safety net).
        if (_mode == "bypassPermissions" && !interactive) { AutoAllow(req); return; }

        // Kimi ACP does not expose Claude's "accept edits" mode. It stays in manual mode and sends requests here;
        // approve only file mutation tools so choosing "accept edits" after a plan can never imply shell yolo.
        if (_mode == "acceptEdits" && !interactive && IsFileEditTool(req.ToolName))
        {
            AutoAllow(req);
            return;
        }

        // Auto: let Claude edit files and run bash freely - only stop for a genuinely dangerous command.
        if (_mode == "auto" && !interactive)
        {
            if (IsCodex && !req.IntegrityVerified)
            {
                if (!_autoApprovalIntegrityWarningShown)
                {
                    _autoApprovalIntegrityWarningShown = true;
                    Items.Add(new BannerItem
                    {
                        Level = "warn",
                        Text = "Auto approval paused because VibeCode could not verify this Codex runtime/request. " +
                               "Review the permission manually. " + (req.DecisionReason ?? "Integrity validation failed."),
                    });
                }
            }
            else
            {
                // Every shell tool, not just "Bash" - on Windows the CLI's shell tool is named "PowerShell", and
                // matching only "Bash" left cmd null, so Auto mode waved through PowerShell commands unchecked.
                var cmd = ToolItem.IsShellTool(req.ToolName) ? req.Input?["command"]?.GetValue<string>() : null;
                if (!IsDangerousBash(cmd)) { AutoAllow(req); return; }
                // a dangerous command falls through and shows a permission card
            }
        }

        var item = new PermItem
        {
            RequestId = req.RequestId,
            ToolName = req.ToolName,
            Input = req.Input,
            Suggestions = req.Suggestions,
            ToolUseId = req.ToolUseId,
            Owner = this,   // answer through the chat that raised it (matters for bridge panes, not just ActiveChat)
        };
        if (item.IsEdit && req.Input is JsonObject io)
            foreach (var l in Diff.Lines(io["old_string"]?.GetValue<string>() ?? "", io["new_string"]?.GetValue<string>() ?? ""))
                item.DiffLines.Add(l);
        if (item.Kind == "question" && req.Input?["questions"] is JsonArray questions)
        {
            foreach (var q in questions.OfType<JsonObject>())
            {
                var entry = new QuestionEntry
                {
                    Question = q["question"]?.GetValue<string>() ?? "",
                    MultiSelect = q["multiSelect"]?.GetValue<bool>() ?? false,
                };
                foreach (var o in (q["options"] as JsonArray ?? new JsonArray()))
                {
                    if (o is JsonObject oo)
                        entry.Options.Add(new QuestionOption { Label = oo["label"]?.GetValue<string>() ?? "", Description = oo["description"]?.GetValue<string>() });
                    else if (o is JsonValue ov)
                        entry.Options.Add(new QuestionOption { Label = ov.GetValue<string>() });
                }
                item.Questions.Add(entry);
            }
        }
        _pendingPerms[req.RequestId] = item;
        Items.Add(item);
        AttentionNeeded?.Invoke();
        NotificationService.NotifyAwaitingUser(this, item);   // opt-in toast; silent while the chat is on screen
        ItemsChanged?.Invoke();
    }

    /// <summary>Approve a tool without showing a card (used by bypass / auto).</summary>
    private void AutoAllow(PermissionRequest req) =>
        _session?.RespondPermission(req.RequestId,
            new JsonObject { ["behavior"] = "allow", ["updatedInput"] = req.Input?.DeepClone() },
            req.ToolUseId);

    private static bool IsFileEditTool(string toolName) => toolName is
        "Edit" or "Write" or "MultiEdit" or "NotebookEdit" or "CodexEdit";

    // Commands that "take control of the machine" - in Auto mode these still ask; everything else runs freely.
    // Seeded from Claude Code's own auto-mode "blocked by default" taxonomy; tune to taste.
    private static readonly System.Text.RegularExpressions.Regex[] DangerousBash =
        new[]
        {
            @"\brm\s+-[a-z]*[rf]",                       // rm -rf / -fr / -r (recursive/forced delete)
            @"\b(sudo|doas)\b", @"\bsu\s",               // privilege escalation
            @"\bdd\b", @"\bmkfs", @"\bfdisk\b", @"\bdiskpart\b", @"\bformat\s+[a-z]:", // disk / filesystem
            @"\b(shutdown|reboot|halt|poweroff)\b",      // power
            @"[|]\s*(sudo\s+)?(bash|sh|zsh|python[0-9.]*|node|pwsh|powershell)\b", // curl ... | sh  (pipe-to-shell)
            @"\bgit\s+push\b[^\n]*(--force\b|\s-f\b)",   // force push
            @">\s*/dev/[sh]d", @"\bof=/dev/",            // writing raw devices
            @"\bchmod\s+-R?\s*777\s+/", @"\bchown\s+-R\s+[^\s]+\s+/", // recursive perms on root
            @"\b(terraform|pulumi|cdk)\s+destroy\b",     // infra teardown
            @"\breg\s+delete\b", @"\brd\s+/s\b", @"\bdel\s+/[a-z]*[sq]", // windows destructive
            // PowerShell equivalents (the Windows shell tool). -Recurse is the "rm -rf" of PowerShell; a bare
            // -Force on one file is routine, so it is deliberately NOT enough on its own.
            @"\bRemove-Item\b[^\n]*\s-Recurse\b", @"\b(ri|rmdir)\b[^\n]*\s-Recurse\b",
            @"\b(Format-Volume|Clear-Disk|Initialize-Disk|Remove-Partition)\b",
            @"\b(Stop-Computer|Restart-Computer)\b",
            @"\bStart-Process\b[^\n]*-Verb\s+RunAs\b",   // elevation
            @"\bSet-ExecutionPolicy\b",
            @"\b(iwr|irm|Invoke-WebRequest|Invoke-RestMethod)\b[^\n]*[|][^\n]*\b(iex|Invoke-Expression)\b", // download-and-run
            @":\s*\(\s*\)\s*\{\s*:\s*\|", @"\bmkfs",      // fork bomb / mkfs
            @"\b(mv|cp)\s+[^\n]*\s+/(bin|boot|etc|usr|sys|lib)\b", // clobbering system dirs
        }
        .Select(p => new System.Text.RegularExpressions.Regex(p, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        .ToArray();

    private static bool IsDangerousBash(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        foreach (var rx in DangerousBash) if (rx.IsMatch(command)) return true;
        return false;
    }

    public void RespondPermission(PermItem item, bool allow, bool always = false, string? denyMessage = null)
    {
        if (!_pendingPerms.Remove(item.RequestId)) return;
        JsonObject result;
        if (allow)
        {
            result = new JsonObject { ["behavior"] = "allow", ["updatedInput"] = item.Input?.DeepClone() };
            if (always && item.Suggestions is JsonArray s && s.Count > 0)
                result["updatedPermissions"] = s.DeepClone();
            item.State = "allow";
        }
        else
        {
            result = new JsonObject
            {
                ["behavior"] = "deny",
                ["message"] = string.IsNullOrWhiteSpace(denyMessage) ? "The user declined this action." : denyMessage,
                ["interrupt"] = false,
            };
            item.State = "deny";
        }
        _session?.RespondPermission(item.RequestId, result, item.ToolUseId);
    }

    public void AnswerQuestion(PermItem item)
    {
        if (!_pendingPerms.Remove(item.RequestId)) return;
        var answers = new JsonObject();
        foreach (var q in item.Questions)
        {
            var chosen = q.Options.Where(o => o.Selected).Select(o => o.Label).ToList();
            if (!string.IsNullOrWhiteSpace(q.Custom)) chosen.Add(q.Custom.Trim());
            answers[q.Question] = string.Join(", ", chosen);
        }
        var updated = item.Input?.DeepClone() as JsonObject ?? new JsonObject();
        updated["answers"] = answers;
        item.State = "allow";
        _session?.RespondPermission(item.RequestId, new JsonObject { ["behavior"] = "allow", ["updatedInput"] = updated }, item.ToolUseId);
    }

    public void DecidePlan(PermItem item, bool approve, bool autoAccept, string? feedback)
    {
        if (approve)
        {
            RespondPermission(item, allow: true);
            SetMode(autoAccept ? "acceptEdits" : "default");
        }
        else
        {
            var msg = "Stay in plan mode - the user wants to keep refining the plan.";
            if (!string.IsNullOrWhiteSpace(feedback)) msg += $"\n\nUser feedback: {feedback!.Trim()}";
            RespondPermission(item, allow: false, denyMessage: msg);
        }
    }

    // ---------------- ingest ----------------

    private IList<ItemVm> Container(string? parentToolUseId, string? subagentThreadId = null)
    {
        if (!string.IsNullOrWhiteSpace(subagentThreadId))
            return EnsureSubagent(subagentThreadId!).Transcript;
        if (parentToolUseId is not null && _toolById.TryGetValue(parentToolUseId, out var parent))
        {
            parent.IsAgent = true;
            if (!parent.Expanded) parent.Expanded = true;
            return parent.Children;
        }
        return Items;
    }

    /// <summary>Append to a container, keeping any root-level QueuedItems pinned at the bottom.</summary>
    private void AppendToContainer(IList<ItemVm> container, ItemVm item)
    {
        if (ReferenceEquals(container, Items)) InsertBeforeQueued(item);
        else container.Add(item);
    }

    private void IngestSdk(JsonNode m)
    {
        var type = m["type"]?.GetValue<string>();
        var subagentThreadId = NodeString(m["subagent_thread_id"]);
        switch (type)
        {
            case "system": ApplySystem(m, subagentThreadId); break;
            case "assistant":
                if (IsClaude)
                    CaptureClaudeAssistantUsage(m["message"],
                        mainThread: subagentThreadId is null && NodeString(m["parent_tool_use_id"]) is null);
                IngestMessagePayload("assistant", m["message"]!, NodeString(m["parent_tool_use_id"]), live: true,
                    subagentThreadId);
                break;
            case "user": ApplyUser(m, live: true, subagentThreadId); break;
            case "stream_event": ApplyStreamEvent(m); break;
            case "tool_progress":
            {
                if (m["tool_use_id"]?.GetValue<string>() is { } id && _toolById.TryGetValue(id, out var t))
                    t.Elapsed = DoubleOrZero(m["elapsed_time_seconds"]);
                break;
            }
            case "tool_use_summary":
            {
                var ids = m["preceding_tool_use_ids"] as JsonArray;
                if (ids?.LastOrDefault()?.GetValue<string>() is { } lastId && _toolById.TryGetValue(lastId, out var t))
                    t.SummaryOverride = m["summary"]?.GetValue<string>();
                break;
            }
            // Only a ROOT result ends the turn. A result scoped to a live Agent tool_use is a subagent finishing;
            // treating it as terminal would seal this prompt's rollback checkpoint while the main agent is still
            // editing, so its later edits go untracked and undo reports "no file changes were recorded". The id must
            // name a tool we know — an unrecognized parent is treated as terminal so no envelope shape can hang a turn.
            case "result":
                if (m["parent_tool_use_id"]?.GetValue<string>() is { } resultParent
                    && _toolById.ContainsKey(resultParent)) break;
                ApplyResult(m);
                break;
        }
        // Child-thread collections notify their inspector directly. Do not yank the root conversation to the bottom
        // every time a Codex worker streams a token or tool event while the user is reading the parent transcript.
        if (subagentThreadId is null) ItemsChanged?.Invoke();
    }

    private void ApplySystem(JsonNode m, string? subagentThreadId = null)
    {
        switch (m["subtype"]?.GetValue<string>())
        {
            case "init":
                SessionId = m["session_id"]?.GetValue<string>();
                Model = m["model"]?.GetValue<string>();
                var cliMode = m["permissionMode"]?.GetValue<string>() ?? "default";
                // The CLI re-inits (and resets the mode to default) after a set_model - e.g. when you
                // change the model or push effort. Don't let that stomp a mode the user chose: re-assert it.
                if (_userMode is not null)
                {
                    Mode = _userMode;                                    // keep the user's chosen VibeCode mode
                    var desired = SessionMode(_userMode);
                    if (desired != cliMode) _ = _session?.SetPermissionModeAsync(desired);
                }
                else
                {
                    Mode = cliMode;
                }
                RebuildEffortOptions();
                if (Status == "starting") Status = "idle";
                break;
            case "turn_activity":
                // Protocol liveness is authoritative. In particular, Codex can multiplex child threads over the
                // parent connection; a live parent/child must be able to restore Stop after any stale UI transition.
                // Claude reports only its ROOT turn here on purpose: its background (run_in_background) children
                // outlive the turn, and counting them would hold the chat at "running" until the whole swarm ended -
                // leaving Stop/send-now, which interrupt, as the only way back to the composer. Those children stay
                // visible through the Subagents roster while the chat is idle and accepting messages.
                if (m["active"]?.GetValue<bool>() == true && Status != "closed") Status = "running";
                break;
            case "subagent_update":
                ApplySubagentUpdate(m["agents"] as JsonArray);
                break;
            case "compact_boundary":
                AppendToContainer(Container(null, subagentThreadId), new DividerItem { Label = "Context compacted" });
                break;
            case "resume_boundary":
                AppendToContainer(Container(null, subagentThreadId), new DividerItem { Label = $"Resumed {AgentDisplay} thread" });
                break;
            case "permission_denied":
                Items.Add(new BannerItem { Level = "warn", Text = $"{m["tool_name"]} auto-denied - {m["message"] ?? m["decision_reason"] ?? "permission rule"}" });
                break;
            case "task_progress":
            {
                if (m["tool_use_id"]?.GetValue<string>() is { } id && _toolById.TryGetValue(id, out var t))
                {
                    var usage = m["usage"];
                    var tokens = DoubleOrZero(usage?["total_tokens"]);
                    var uses = DoubleOrZero(usage?["tool_uses"]);
                    var last = m["last_tool_name"]?.GetValue<string>();
                    t.ProgressText = $"{tokens / 1000:0.0}k tokens · {uses:0} tool uses{(last is null ? "" : $" · → {last}")}";
                }
                break;
            }
            case "thinking_tokens":
                ThinkingTokens = DoubleOrZero(m["estimated_tokens"]);
                break;
            case "usage_update":
                SetLiveUsage(UsageOf(m["usage"]));
                UpdateContext(m["usage"], null);
                break;
            case "artifact_update":
                if (m["paths"] is JsonArray paths)
                    foreach (var path in paths.Select(x => x?.GetValue<string>()).OfType<string>()) TrackArtifact(path);
                break;
            case "todo_update":
                if (m["todos"] is JsonArray todos) ReplaceTodos(todos);
                break;
            case "api_retry":
            {
                // The CLI emits this while backing off after an API error/overload. Field names vary
                // by build (retryAttempt/maxRetries/retryInMs vs attempt/max_retries/retry_delay_ms).
                // Read defensively - never throw mid-turn if a field is a string or missing.
                int attempt = NumOrZero(m["retryAttempt"]); if (attempt == 0) attempt = NumOrZero(m["attempt"]);
                int max = NumOrZero(m["maxRetries"]); if (max == 0) max = NumOrZero(m["max_retries"]);
                int status = NumOrZero(m["status"]);
                var note = "retrying" + (attempt > 0 ? max > 0 ? $" {attempt}/{max}" : $" (attempt {attempt})" : "");
                if (status == 429) note += " · rate limited";
                else if (status >= 500) note += " · overloaded";
                else if (status > 0) note += $" · HTTP {status}";
                _retryNote = note;
                Raise(nameof(WorkingText));
                break;
            }
        }
    }

    private void ApplySubagentUpdate(JsonArray? snapshot)
    {
        if (snapshot is null) return;
        var byId = Subagents.ToDictionary(a => a.ThreadId, StringComparer.Ordinal);
        var byParentTool = Subagents
            .Where(a => !string.IsNullOrWhiteSpace(a.ParentToolUseId))
            .GroupBy(a => a.ParentToolUseId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sourceOrder = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var node in snapshot.OfType<JsonObject>())
        {
            var threadId = NodeString(node["thread_id"]);
            if (string.IsNullOrWhiteSpace(threadId) || !seen.Add(threadId)) continue;
            var parentToolUseId = NodeString(node["parent_tool_use_id"]);
            sourceOrder[threadId] = sourceOrder.Count;
            if (!byId.TryGetValue(threadId, out var agent))
            {
                // Claude replaces its provisional tool-use id with a background task id. Reconcile by the stable
                // parent tool instead of replacing the row (which would close an inspector the user is reading).
                if (!string.IsNullOrWhiteSpace(parentToolUseId)
                    && byParentTool.TryGetValue(parentToolUseId!, out agent))
                {
                    byId.Remove(agent.ThreadId);
                    agent.ThreadId = threadId;
                }
                else
                {
                    agent = new SubagentItem { ThreadId = threadId };
                    Subagents.Add(agent);
                }
                byId[threadId] = agent;
            }

            if (!string.IsNullOrWhiteSpace(parentToolUseId))
            {
                agent.ParentToolUseId = parentToolUseId;
                byParentTool[parentToolUseId!] = agent;
                if (_toolById.TryGetValue(parentToolUseId!, out var parentTool))
                    agent.UseTranscript(parentTool.Children);
            }
            agent.Label = node["label"]?.GetValue<string>() ?? agent.Label;
            agent.Task = node["task"]?.GetValue<string>() ?? agent.Task;
            agent.Activity = node["activity"]?.GetValue<string>() ?? agent.Activity;
            agent.Status = node["status"]?.GetValue<string>() ?? agent.Status;
        }
        foreach (var stale in Subagents.Where(a => !seen.Contains(a.ThreadId)).ToList())
        {
            Subagents.Remove(stale);
            if (ReferenceEquals(SelectedSubagent, stale)) SelectedSubagent = null;
        }

        // Keep live work immediately visible while retaining the provider's stable order within each status group.
        var ordered = Subagents
            .OrderBy(a => a.IsActive ? 0 : 1)
            .ThenBy(a => sourceOrder[a.ThreadId])
            .ToList();
        for (var targetIndex = 0; targetIndex < ordered.Count; targetIndex++)
        {
            var currentIndex = Subagents.IndexOf(ordered[targetIndex]);
            if (currentIndex != targetIndex) Subagents.Move(currentIndex, targetIndex);
        }

        RaiseSubagentRosterProperties();
    }

    private SubagentItem EnsureSubagent(string threadId)
    {
        var existing = Subagents.FirstOrDefault(agent =>
            string.Equals(agent.ThreadId, threadId, StringComparison.Ordinal));
        if (existing is not null) return existing;
        var created = new SubagentItem { ThreadId = threadId };
        Subagents.Add(created);
        RaiseSubagentRosterProperties();
        return created;
    }

    /// <summary>
    /// Connect a Claude Agent/Task or Grok <c>spawn_subagent</c> tool card to the Subagents roster + inspector.
    /// Grok uses the task tool name <c>spawn_subagent</c> (not Claude's Agent/Task); without this mapping the
    /// workers still run but never appear in the Subagents menu.
    /// </summary>
    private void AttachAgentToolTranscript(ToolItem tool)
    {
        if (!ToolItem.IsSubagentSpawnTool(tool.Name)) return;
        tool.IsAgent = true;
        var agent = Subagents.FirstOrDefault(candidate =>
            string.Equals(candidate.ParentToolUseId, tool.Id, StringComparison.Ordinal)
            || string.Equals(candidate.ThreadId, tool.Id, StringComparison.Ordinal));
        if (agent is null)
        {
            var kind = tool.AgentType;
            var label = string.IsNullOrWhiteSpace(kind)
                        || string.Equals(kind, "agent", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(kind, "general-purpose", StringComparison.OrdinalIgnoreCase)
                ? $"Subagent {Subagents.Count + 1}"
                : kind;
            agent = new SubagentItem
            {
                ThreadId = tool.Id,
                ParentToolUseId = tool.Id,
                Label = label,
                Task = tool.Summary,
                Activity = tool.Status == "running" ? "Working" : "Finished",
                Status = tool.Status == "running" ? "running"
                    : tool.Status == "error" ? "errored" : "completed",
            };
            Subagents.Add(agent);
            RaiseSubagentRosterProperties();
        }
        else
        {
            agent.ParentToolUseId = tool.Id;
            if (!string.IsNullOrWhiteSpace(tool.Summary)) agent.Task = tool.Summary;
            SyncSubagentStatusFromTool(agent, tool);
        }
        agent.UseTranscript(tool.Children);
    }

    /// <summary>Keep roster status in sync when a spawn tool settles (Grok spawn_subagent completes as a normal tool_result).</summary>
    private void SyncSubagentStatusFromTool(SubagentItem agent, ToolItem tool)
    {
        agent.Status = tool.Status switch
        {
            "running" or "starting" => agent.Status is "pendingInit" ? "pendingInit" : "running",
            "error" => "errored",
            "done" => "completed",
            _ => agent.Status,
        };
        agent.Activity = tool.Status switch
        {
            "running" or "starting" => string.IsNullOrWhiteSpace(agent.Activity) ? "Working" : agent.Activity,
            "error" => "Failed",
            "done" => "Finished",
            _ => agent.Activity,
        };
        // Grok often returns a subagent id in the spawn result text — promote ThreadId when we can parse one.
        if (tool.Status == "done" && !string.IsNullOrWhiteSpace(tool.Result)
            && TryParseGrokSubagentId(tool.Result, out var realId)
            && !string.Equals(agent.ThreadId, realId, StringComparison.Ordinal))
        {
            agent.ThreadId = realId!;
        }
        RaiseSubagentRosterProperties();
    }

    /// <summary>Best-effort extract of a Grok spawn_subagent / task-tool id from tool result text.</summary>
    private static bool TryParseGrokSubagentId(string result, out string? id)
    {
        id = null;
        if (string.IsNullOrWhiteSpace(result)) return false;
        // JSON: {"subagent_id":"..."} / {"id":"..."} / {"task_id":"..."}
        try
        {
            if (JsonNode.Parse(result) is JsonObject obj)
            {
                foreach (var key in new[] { "subagent_id", "subagentId", "task_id", "taskId", "id", "thread_id", "threadId" })
                {
                    var v = obj[key]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(v) && v!.Length >= 4) { id = v.Trim(); return true; }
                }
            }
        }
        catch { /* plain text below */ }

        // Plain text: "subagent_id: abc" / "Subagent ID: abc" / "task_id=abc"
        foreach (var pattern in new[]
                 {
                     @"subagent[_\s-]?id\s*[:=]\s*([A-Za-z0-9_\-./]+)",
                     @"task[_\s-]?id\s*[:=]\s*([A-Za-z0-9_\-./]+)",
                     @"thread[_\s-]?id\s*[:=]\s*([A-Za-z0-9_\-./]+)",
                 })
        {
            var m = System.Text.RegularExpressions.Regex.Match(result, pattern,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success && m.Groups[1].Value.Length >= 4)
            {
                id = m.Groups[1].Value.Trim();
                return true;
            }
        }
        return false;
    }

    private void RaiseSubagentRosterProperties()
    {
        Raise(nameof(HasSubagents));
        Raise(nameof(ActiveSubagentCount));
        Raise(nameof(ShowSwarmControl));
        Raise(nameof(SubagentPanelTitle));
        Raise(nameof(SubagentButtonToolTip));
        Raise(nameof(SubagentsButtonToolTip));
        Raise(nameof(WorkingText));
    }

    private void ApplyResult(JsonNode m)
    {
        // Claude labels an intentional stop between tool-use messages as error_during_execution. Classify the
        // terminal envelope once here: this path is shared by normal chats and every Bridge pane, so an expected stop
        // becomes idle without an error banner or a false "agent hit an error and stopped" Bridge announcement.
        var disposition = TurnResultClassifier.Classify(Provider, m, _interruptRequested);
        _interruptRequested = false;
        var isError = disposition == TurnResultDisposition.Failed;
        var usageLimited = isError && IsUsageLimitResult(m);
        ReleaseSwarmLease();
        CompleteActiveRollback();
        ThinkingTokens = 0;
        _retryNote = null;

        // Do this before Status becomes idle: that setter posts the next FlushQueue. A successful chunk is committed;
        // any failed chunk is restored to the FIFO head before the queue is paused, so no request disappears. Quota
        // failures can resume automatically; other provider failures wait for an explicit retry.
        var failedExtendedChunk = isError && _activeExtendedDispatch is not null;
        if (_activeExtendedDispatch is not null)
        {
            if (isError) RequeueActiveExtendedDispatch();
            else _activeExtendedDispatch = null;
        }
        if (isError && _sendQueue.Any(item => item.Extended))
            PauseExtendedQueue(usageLimited ? "usage" : "a provider error", usageLimited);

        Status = isError ? "error" : "idle";
        TurnCompleted?.Invoke();
        NotificationService.NotifyTurnFinished(this, isError);   // opt-in toast; silent while the chat is on screen
        if (IsClaude) UsageService.Instance.Refresh();
        if (IsKimi) KimiUsageService.Instance.Refresh();
        // total_cost_usd is the session running total, so a provider-billed turn's own cost is the delta.
        var costBefore = _cost;
        Cost = DoubleOrNull(m["total_cost_usd"]) ?? _cost;
        // The result's usage is the whole turn added up, so it can only be trusted for the context WINDOW
        // (via modelUsage), never for how full that window is - the per-request updates above own occupancy.
        UpdateContext(m["usage"], m["modelUsage"], aggregate: true);
        // Kimi's local /usage command gives authoritative whole-session totals and a per-model breakdown. Replace
        // our counters from that snapshot so resumed sessions and repeated result handling cannot double-count.
        // Other providers report a turn delta, which still follows the incremental path below.
        var committedKimiSnapshot = false;
        if (IsKimi && m["session_usage"] is JsonObject kimiSessionUsage)
        {
            var session = UsageOf(kimiSessionUsage);
            if (session.HasTokens)
            {
                TotalIn = session.TotalIn;
                TotalOut = session.Output;
                TotalTokens = session.Total;
                _estCost = EstimateSessionCost(m["session_model_usage"] as JsonObject, session,
                    CurrentModel?.ResolvedModel ?? _model);
                Raise(nameof(CostText));
                committedKimiSnapshot = true;
                // The snapshot is a whole-session total, so the permanent usage history gets the delta since the
                // last one - logging the snapshot itself would re-log every earlier turn on every turn.
                LogTurnUsage(
                    session.Input - _loggedKimiUsage.Input,
                    session.CacheWrite - _loggedKimiUsage.CacheWrite,
                    session.CacheRead - _loggedKimiUsage.CacheRead,
                    session.Output - _loggedKimiUsage.Output,
                    _estCost - _loggedKimiCost,
                    reported: false);
                _loggedKimiUsage = session;
                _loggedKimiCost = _estCost;
            }
        }

        // Cumulative "all tokens used" + estimated cost: fold this turn's usage into the running totals.
        // Input is split into three cache tiers (fresh / cache-write / cache-read) so cost can price each
        // correctly; the token readout just sums them as the input side.
        if (!committedKimiSnapshot && m["usage"] is JsonObject tu)
        {
            double input = DoubleOrZero(tu["input_tokens"]);
            double cacheWrite = DoubleOrZero(tu["cache_creation_input_tokens"]);
            double cacheRead = DoubleOrZero(tu["cache_read_input_tokens"]);
            double output = DoubleOrZero(tu["output_tokens"]);
            double tin = input + cacheWrite + cacheRead;
            if (tin + output > 0)
            {
                TotalIn = _totalIn + tin;
                TotalOut = _totalOut + output;
                TotalTokens = _totalTokens + tin + output;
                // Price this turn at its own model (handles mid-session model switches) and accumulate.
                // Grok reports real per-call spend, so it is billed from that instead of the list-price estimate.
                var turnCost = IsGrok
                    ? Math.Max(0, _cost - costBefore)
                    : ModelPricing.TurnCost(CurrentModel?.ResolvedModel ?? _model, input, cacheWrite, cacheRead, output);
                if (!IsGrok) _estCost += turnCost;
                Raise(nameof(CostText));
                LogTurnUsage(input, cacheWrite, cacheRead, output, turnCost, reported: IsGrok);
            }
        }
        // The result usage above is authoritative and has now been committed once. Drop the live
        // snapshot so the persistent header does not count the just-finished turn a second time.
        ResetLiveUsage();
        if (isError)
        {
            var subtype = m["subtype"]?.GetValue<string>() ?? "error";
            var detail = m["result"]?.GetValue<string>() ?? string.Join("; ", (m["errors"] as JsonArray)?.Select(e => e?.ToString()) ?? Array.Empty<string>());
            var authText = detail.ToLowerInvariant();
            if (authText.Contains("not logged in") || authText.Contains("not signed in")
                || authText.Contains("sign in") || authText.Contains("unauthor") || authText.Contains("401"))
                AuthNeeded = true;
            var queueNote = ExtendedQueuePausedForUsage
                ? " The active chunk was kept at the front of the extended queue; it will resume after usage is available."
                : ExtendedQueuePaused
                    ? failedExtendedChunk
                        ? " The failed chunk was kept at the front of the extended queue; use Resume queue when the provider is ready."
                        : " The remaining extended queue is paused; use Resume queue when the provider is ready."
                    : "";
            Items.Add(new BannerItem
            {
                Level = "error",
                Text = $"Turn ended with {subtype}: {ToolItem.Trunc(detail, 500)}{queueNote}",
            });
        }
        // (queued messages auto-flush from the Status setter when Status becomes "idle" - covers init + turn-end)
    }

    /// <summary>
    /// Append one committed turn to the permanent usage history behind Settings > Usage. Session counters are
    /// per-chat and reset when a chat is reopened; this file is the record that outlives them, so it is written
    /// once per turn, from the same authoritative result envelope the counters use.
    /// </summary>
    private void LogTurnUsage(double input, double cacheWrite, double cacheRead, double output,
        double costUsd, bool reported)
    {
        // A resumed session replays nothing here, but a snapshot delta can still come back flat or (after a
        // provider correction) negative. Never let that write a negative row into the history.
        input = Math.Max(0, input);
        cacheWrite = Math.Max(0, cacheWrite);
        cacheRead = Math.Max(0, cacheRead);
        output = Math.Max(0, output);
        if (input + cacheWrite + cacheRead + output <= 0) return;
        UsageLog.Instance.Record(Provider, CurrentModel?.ResolvedModel ?? _model,
            input, cacheWrite, cacheRead, output, Math.Max(0, costUsd), reported, SessionId, Cwd);
    }

    private static double EstimateSessionCost(JsonObject? byModel, LiveUsage fallback, string? fallbackModel)
    {
        double cost = 0;
        var pricedAny = false;
        if (byModel is not null)
        foreach (var (model, node) in byModel)
        {
            var usage = UsageOf(node);
            if (!usage.HasTokens) continue;
            cost += ModelPricing.TurnCost(model, usage.Input, usage.CacheWrite, usage.CacheRead, usage.Output);
            pricedAny = true;
        }
        return pricedAny
            ? cost
            : ModelPricing.TurnCost(fallbackModel, fallback.Input, fallback.CacheWrite, fallback.CacheRead, fallback.Output);
    }

    /// <param name="usage">Usage for ONE model request, unless <paramref name="aggregate"/> says otherwise.</param>
    /// <param name="aggregate">True when <paramref name="usage"/> sums every model request in a turn instead of
    /// describing a single one. Context is a high-water mark, not a running total: one agentic turn routinely
    /// makes tens or hundreds of requests and every one of them re-reads the whole cached prompt, so that sum
    /// runs 50-130x the real occupancy (measured against real transcripts: 195 requests summing to 23.9M against
    /// a true 180k context). Reading it as occupancy is what pinned the bar at "3.8M / 1M tokens · 100%".</param>
    private void UpdateContext(JsonNode? usage, JsonNode? modelUsage, bool aggregate = false)
    {
        if (usage is not null)
        {
            // Codex supplies the latest request's prompt separately because a single agentic turn can make
            // several model requests; summing the whole turn would overstate current context occupancy.
            // An explicit context_input_tokens snapshot is always trusted, aggregate or not - it is already
            // "the latest request", which is exactly what we want.
            var hasContextSnapshot = usage["context_input_tokens"] is not null;
            long used = LongOrZero(usage["context_input_tokens"]);
            if (!hasContextSnapshot && !aggregate
                && usage["_vibecode_aggregate_prompt"]?.GetValue<bool>() != true)
                used = LongOrZero(usage["input_tokens"])
                    + LongOrZero(usage["cache_read_input_tokens"])
                    + LongOrZero(usage["cache_creation_input_tokens"]);
            if (hasContextSnapshot || used > 0) _ctxUsed = Math.Max(0, used);
            var directWindow = LongOrZero(usage["context_window"]);
            if (directWindow > 0) _ctxWindow = directWindow;
        }
        if (modelUsage is JsonObject mu)
        {
            // modelUsage is keyed by model id. The context bar belongs to THIS chat's model, so match on the
            // key (tolerating the "[1m]" variant tag the way CurrentModel does) rather than grabbing whichever
            // model burned the most input this turn - a subagent on a smaller-window model must never redefine
            // the chat's window. Fall back to the widest reported window when no key matches (e.g. _model is a
            // "default" alias). And only ever GROW the window: a per-turn report that comes back with the plain
            // model's base window must not shrink a 1M variant down and peg the bar at 100%.
            long widest = 0, mainWindow = 0;
            foreach (var kv in mu)
            {
                if (kv.Value is not JsonObject entry) continue;
                var w = LongOrZero(entry["contextWindow"]);
                if (w > widest) widest = w;
                if (_model is not null && StripVariant(kv.Key) == StripVariant(_model)) mainWindow = w;
            }
            var window = mainWindow > 0 ? mainWindow : widest;
            if (window > 0) _ctxWindow = Math.Max(_ctxWindow, window);
        }
        Raise(nameof(CtxText));
        Raise(nameof(CtxHasData));
        Raise(nameof(CtxPercentValue));
        Raise(nameof(CtxDetailText));
    }

    private void ApplyUser(JsonNode m, bool live, string? subagentThreadId = null)
    {
        var parent = m["parent_tool_use_id"]?.GetValue<string>();
        var message = m["message"];
        var content = message?["content"];
        var consumed = false;
        if (content is JsonArray blocks)
        {
            foreach (var block in blocks.OfType<JsonObject>())
            {
                if (block["type"]?.GetValue<string>() != "tool_result") continue;
                consumed = true;
                var id = block["tool_use_id"]?.GetValue<string>();
                if (id is not null) _activeRollback?.EndToolCapture(id);
                if (id is null || !_toolById.TryGetValue(id, out var tool)) continue;
                var isErr = block["is_error"]?.GetValue<bool>() == true;
                // A coalesced edit card is targeted by several tool_use ids, so several results land on it. An error
                // must NOT be masked by a later sibling's success - keep the failure visible, and settle the card to
                // "error" if ANY edit failed, else "done" (never leave it stuck "running").
                if (isErr)
                {
                    tool.Result = NormalizeResult(block["content"]);
                    tool.IsError = true;
                    // Deliberately does NOT expand the card. Throwing it open on failure sounds helpful and is
                    // not: a rejected edit unrolls its whole diff into the transcript and shoves the rest of the
                    // turn off screen, and it fires hardest on the turns that are already going badly. The red
                    // card and its error row carry the failure; the body is one click away for anyone who wants
                    // it. Shell tools were already exempt on size grounds - that turned out to be the right rule
                    // for every tool, not a special case.
                }
                else if (!tool.IsError)   // only replace the shown result with a success if no sibling errored
                {
                    tool.Result = NormalizeResult(block["content"]);
                }
                tool.Status = tool.IsError ? "error" : "done";
                // The CLI's real task id only appears in TaskCreate's result text; our provisional id is a guess.
                if (tool.Name == "TaskCreate" && !tool.IsError) AdoptTaskId(tool);
                // Grok spawn_subagent (and Claude Agent/Task) finish as ordinary tool_results — settle the roster row.
                if (ToolItem.IsSubagentSpawnTool(tool.Name))
                {
                    var agent = Subagents.FirstOrDefault(a =>
                        string.Equals(a.ParentToolUseId, tool.Id, StringComparison.Ordinal)
                        || string.Equals(a.ThreadId, tool.Id, StringComparison.Ordinal));
                    if (agent is not null) SyncSubagentStatusFromTool(agent, tool);
                    else AttachAgentToolTranscript(tool);
                }
            }
        }
        if (consumed) return;
        if (m["isSynthetic"]?.GetValue<bool>() == true) return;

        var text = TextOfContent(content);
        if (string.IsNullOrWhiteSpace(text)) return;

        // Stopping a turn makes the CLI inject a synthetic user message, "[Request interrupted by user]"
        // (or "...for tool use"). It's noise - the stop is already obvious from the UI - so never render it
        // as a chat bubble, live or when restoring history.
        var trimmed = text.Trim();
        if (trimmed.StartsWith("[Request interrupted by user", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal))
            return;

        // Slash-command echoes arrive as user messages wrapped in <local-command-*>/<command-*> tags.
        // Don't render the raw protocol text as a chat bubble; surface the command's stdout though.
        if (text.TrimStart().StartsWith('<'))
        {
            if (text.Contains("local-command-stdout"))
            {
                var mm = System.Text.RegularExpressions.Regex.Match(
                    text, "<local-command-stdout>(.*?)</local-command-stdout>",
                    System.Text.RegularExpressions.RegexOptions.Singleline);
                var inner = (mm.Success ? mm.Groups[1].Value
                    : System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", "")).Trim();
                if (inner.Length > 0)
                {
                    // short output reads best as a compact divider; longer output (e.g. /help,
                    // /context, /release-notes) renders as a normal text block instead of vanishing.
                    if (inner.Length < 100) AppendToContainer(Container(parent, subagentThreadId), new DividerItem { Label = inner });
                    else AppendToContainer(Container(parent, subagentThreadId), new TextItem { Text = inner });
                }
            }
            return; // drop command-name/message/args/caveat noise
        }

        // What came back is the wire form, so a bridge notice or swarm directive may still be glued on front. Show
        // only the human's part: the echo then matches what Send recorded, and a replayed turn renders clean too.
        var shown = StripInjectedPrelude(text);
        if (shown.Length == 0) return;   // the whole turn was an app-injected notice - never the user's own message

        if (live && _lastLocalUserText == shown && (DateTime.UtcNow - _lastLocalUserAt).TotalSeconds < 15)
        {
            _lastLocalUserText = null;
            return;
        }
        if (live && text.Contains("Not logged in", StringComparison.OrdinalIgnoreCase)) AuthNeeded = true;
        // Local live echoes returned above because Send already recorded them. Any remaining top-level user message is
        // restored history (or came from another client), so include it for resumed Claude, Codex, Kimi, and Grok chats too.
        if (parent is null && subagentThreadId is null) _promptHistory.Record(shown);
        AppendToContainer(Container(parent, subagentThreadId), new UserItem
        {
            Text = shown,
            FromSubagent = parent is not null || subagentThreadId is not null,
            Owner = this,
        });
    }

    private void IngestMessagePayload(string type, JsonNode message, string? parent, bool live,
        string? subagentThreadId = null)
    {
        if (type == "user")
        {
            var wrapper = new JsonObject { ["message"] = message.DeepClone(), ["parent_tool_use_id"] = parent };
            ApplyUser(wrapper, live, subagentThreadId);
            return;
        }
        // assistant - the CLI may deliver one final message per content block, so match
        // streamed placeholders by type (consuming them) rather than by block index.
        var key = StreamKey(parent, subagentThreadId);
        _streams.TryGetValue(key, out var streamed);
        var blocks = message["content"] as JsonArray ?? new JsonArray();
        var container = Container(parent, subagentThreadId);
        foreach (var node in blocks)
        {
            if (node is not JsonObject block) continue;
            var bt = block["type"]?.GetValue<string>();
            switch (bt)
            {
                case "text":
                {
                    var text = block["text"]?.GetValue<string>() ?? "";
                    if (live && text.Contains("Not logged in", StringComparison.OrdinalIgnoreCase)) AuthNeeded = true;
                    if (TakeStreamed<TextItem>(streamed) is { } ti) { ti.Text = text; ti.Streaming = false; }
                    else if (!string.IsNullOrWhiteSpace(text)) container.Add(new TextItem { Text = text });
                    break;
                }
                case "thinking" or "redacted_thinking":
                {
                    // Prefer the finished block's thinking text when present. When display is "omitted" (or the
                    // harness redacts), the final block has an empty thinking field + signature only — do NOT
                    // overwrite deltas we already streamed, or every "Thought process" card goes blank.
                    var text = block["thinking"]?.GetValue<string>();
                    if (TakeStreamed<ThinkingItem>(streamed) is { } th)
                    {
                        if (!string.IsNullOrEmpty(text)) th.Text = text;
                        else if (bt == "redacted_thinking" && string.IsNullOrEmpty(th.Text))
                            th.Text = "(redacted)";
                        th.Streaming = false;
                    }
                    else if (!string.IsNullOrEmpty(text)) container.Add(new ThinkingItem { Text = text });
                    else if (bt == "redacted_thinking") container.Add(new ThinkingItem { Text = "(redacted)" });
                    // else: signature-only / restored transcript with no plaintext — skip blank cards.
                    break;
                }
                case "tool_use" or "server_tool_use" or "mcp_tool_use":
                {
                    var id = block["id"]?.GetValue<string>();
                    var match = streamed?.OfType<ToolItem>().FirstOrDefault(t => id is null || t.Id == id);
                    if (match is not null) streamed!.Remove(match);
                    RegisterTool(container, block, match);
                    break;
                }
                // Server-tool results (web search/fetch) come back in the assistant turn alongside the
                // server_tool_use block; without this the tool card would spin forever.
                case "web_search_tool_result" or "web_fetch_tool_result":
                {
                    if (block["tool_use_id"]?.GetValue<string>() is { } sid && _toolById.TryGetValue(sid, out var st))
                    {
                        var (summary, err) = SummarizeServerTool(bt, block["content"]);
                        st.Result = summary;
                        st.IsError = err;
                        st.Status = err ? "error" : "done";
                        // Same policy as the tool_result path above: a failure colours the card, it does not
                        // open it. One rule for every tool beats a table of which failures are worth unrolling.
                    }
                    break;
                }
            }
        }
        // Only the main thread's requests describe THIS chat's context. A subagent runs its own, far smaller
        // conversation, so letting one report occupancy makes the bar collapse mid-turn and climb back after.
        if (parent is null && subagentThreadId is null) UpdateContext(message["usage"], null);
    }

    private static T? TakeStreamed<T>(List<ItemVm?>? streamed) where T : ItemVm
    {
        var match = streamed?.OfType<T>().FirstOrDefault();
        if (match is not null) streamed!.Remove(match);
        return match;
    }

    private void AddDisplayItem(IList<ItemVm> container, ItemVm item)
    {
        // Root transcript: never push content below greyed queued prompts. Subagent
        // / nested agent children lists have no queue cards, so they can Append freely.
        var root = ReferenceEquals(container, Items);

        if (item is not ToolItem tool || CompactToolGroupItem.KindFor(tool.Name) is not { } kind)
        {
            if (root) InsertBeforeQueued(item);
            else container.Add(item);
            return;
        }

        // Compact groups: attach to the last non-queued item of the same kind.
        var lastIdx = container.Count - 1;
        if (root)
        {
            var q = FirstQueuedIndex();
            lastIdx = q < 0 ? container.Count - 1 : q - 1;
        }
        if (lastIdx >= 0 && container[lastIdx] is CompactToolGroupItem { Kind: var previousKind } previous
                            && previousKind == kind)
        {
            previous.Add(tool);
            return;
        }

        var group = new CompactToolGroupItem(kind, AppSettings.Current.CompactMode);
        group.Add(tool);
        if (root) InsertBeforeQueued(group);
        else container.Add(group);
    }

    private ToolItem RegisterTool(IList<ItemVm> container, JsonObject block, ToolItem? existing)
    {
        var id = block["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString();
        if (existing is null) _toolById.TryGetValue(id, out existing);
        ToolItem tool;
        if (existing is not null)
        {
            tool = existing;
            tool.Input = block["input"] ?? tool.Input;
        }
        else
        {
            tool = new ToolItem { Id = id, Name = block["name"]?.GetValue<string>() ?? "?", Input = block["input"] };
            _toolById[id] = tool;
            AddDisplayItem(container, tool);
        }

        ApplyToolSideEffects(tool);
        AttachAgentToolTranscript(tool);
        // Coalesce a run of consecutive edits to the SAME file into one card, stacking every diff and summing the
        // +/- totals, so "edited foo.cs 4 times in a row" reads as one card whose "View full code" shows all green/red.
        if (tool.Name is "Edit" or "MultiEdit" && MergeIntoPrevEdit(container, tool) is { } merged)
        {
            _toolById[id] = merged;
            return merged;
        }
        var captureUnnamedChanges = ShouldCaptureUnnamedFileChanges(tool.Name);
        if (captureUnnamedChanges && _activeRollback is { } rollback && tool.Input is JsonObject toolInput)
        {
            rollback.NoteToolWorkingDirectory(NodeString(toolInput["cwd"] ?? toolInput["working_directory"] ?? toolInput["workdir"]));
        }
        if (captureUnnamedChanges) _activeRollback?.BeginToolCapture(tool.Id);
        // The Task* list-management calls (status flips, reads) are folded into the Todos panel - don't leave a chat
        // card for each one (TaskUpdate alone fires many times a turn). TaskCreate stays so the plan shows inline.
        if (tool.Name is "TaskUpdate" or "TaskList" or "TaskGet" or "TaskOutput" or "TaskStop")
        {
            container.Remove(tool);
            _toolById.Remove(tool.Id);
        }
        return tool;
    }

    private static bool ShouldCaptureUnnamedFileChanges(string name) => name switch
    {
        // These either report an exact path through TrackArtifact or are known read-only/control operations.
        "Write" or "Edit" or "MultiEdit" or "NotebookEdit" or "CodexEdit"
            or "Read" or "Glob" or "Grep" or "LS" or "WebSearch" or "WebFetch" or "Think"
            or "TodoWrite" or "TaskCreate" or "TaskUpdate" or "TaskList" or "TaskGet" or "TaskOutput"
            or "TaskStop" or "AskUserQuestion" or "ExitPlanMode" => false,
        _ => true, // Bash/Shell, MCP tools, agents, and future tools are observed conservatively.
    };

    private static string? NodeString(JsonNode? node)
    {
        try { return node?.GetValue<string>(); }
        catch { return null; }
    }

    /// <summary>Diff lines for an Edit (old→new) or MultiEdit (each edit, separated by a blank context line).</summary>
    private static IEnumerable<DiffLine> EditDiffLines(JsonObject input, string name)
    {
        if (name == "Edit")
        {
            foreach (var l in Diff.Lines(input["old_string"]?.GetValue<string>() ?? "", input["new_string"]?.GetValue<string>() ?? ""))
                yield return l;
        }
        else if (input["edits"] is JsonArray edits)
        {
            foreach (var e in edits.OfType<JsonObject>())
            {
                foreach (var l in Diff.Lines(e["old_string"]?.GetValue<string>() ?? "", e["new_string"]?.GetValue<string>() ?? ""))
                    yield return l;
                yield return new DiffLine { Kind = "ctx", Text = "" };
            }
        }
    }

    /// <summary>Ordered (old,new) text pairs for each edit, so the viewer can rebuild the file's pre-edit state
    /// by reverse-applying them to the on-disk (post-edit) content.</summary>
    private static IEnumerable<(string Old, string New)> EditOpsFor(JsonObject input, string name)
    {
        if (name == "Write") { yield return ("", input["content"]?.GetValue<string>() ?? ""); yield break; }
        if (name == "Edit") { yield return (input["old_string"]?.GetValue<string>() ?? "", input["new_string"]?.GetValue<string>() ?? ""); yield break; }
        if (input["edits"] is JsonArray edits)
            foreach (var e in edits.OfType<JsonObject>())
                yield return (e["old_string"]?.GetValue<string>() ?? "", e["new_string"]?.GetValue<string>() ?? "");
    }

    /// <summary>If the edit immediately before <paramref name="tool"/> targets the same file, fold this edit's diff +
    /// ops into it, drop this card, and return the survivor. Edits normally live inside a CompactToolGroupItem.Tools.</summary>
    private ToolItem? MergeIntoPrevEdit(IList<ItemVm> container, ToolItem tool)
    {
        var path = tool.FilePath;
        if (path is null) return null;
        for (int c = container.Count - 1; c >= 0; c--)
        {
            if (container[c] is CompactToolGroupItem g && g.Tools.IndexOf(tool) is var gi && gi >= 0)
            {
                if (gi <= 0 || g.Tools[gi - 1] is not { Name: "Edit" or "MultiEdit" } prev || !PathEq(prev.FilePath, path))
                    return null;
                FoldEdit(prev, tool); g.Tools.RemoveAt(gi); return prev;
            }
            if (ReferenceEquals(container[c], tool))   // ungrouped fallback (edits are usually grouped)
            {
                if (c <= 0 || container[c - 1] is not ToolItem { Name: "Edit" or "MultiEdit" } prev || !PathEq(prev.FilePath, path))
                    return null;
                FoldEdit(prev, tool); container.RemoveAt(c); return prev;
            }
        }
        return null;
    }

    private static bool PathEq(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static void FoldEdit(ToolItem prev, ToolItem tool)
    {
        if (prev.DiffLines.Count > 0) prev.DiffLines.Add(new DiffLine { Kind = "ctx", Text = "" });   // gap between edits
        foreach (var l in tool.DiffLines) prev.DiffLines.Add(l);
        prev.EditOps.AddRange(tool.EditOps);
        prev.EditCount += 1;
        prev.NotifyDiffChanged();
        if (prev.Status != "running") prev.Status = "running";   // the last edit's result re-settles the merged card
    }

    private void ApplyToolSideEffects(ToolItem tool)
    {
        var input = tool.Input as JsonObject;
        if (input is null) return;
        if (tool.Name == "TodoWrite" && input["todos"] is JsonArray todos)
        {
            tool.Todos.Clear();
            ReplaceTodos(todos, tool.Todos);
        }
        else if (tool.Name == "TaskCreate")
        {
            var subject = input["subject"]?.GetValue<string>() ?? "";
            var activeForm = input["activeForm"]?.GetValue<string>();
            if (_taskIdByTool.TryGetValue(tool.Id, out var known) && _tasksById.TryGetValue(known, out var dup))
            {
                _taskText[known] = (subject, activeForm);   // re-ingest of the same call (streaming->final): refresh, don't duplicate
                dup.Text = TaskText(known, dup.Status);
            }
            else
            {
                // Provisional only. Real CLI task ids are workspace-persistent (they continue across sessions), so
                // they are NOT 1,2,3 per session - AdoptTaskId re-keys this entry once TaskCreate's result names it.
                var id = "new:" + (++_taskSeq);
                _taskIdByTool[tool.Id] = id;
                _taskText[id] = (subject, activeForm);
                var entry = new TodoEntry { Status = "pending", Text = subject };
                _tasksById[id] = entry;
                Todos.Add(entry);
            }
            RaiseTodoProps();
        }
        else if (tool.Name == "TaskUpdate")
        {
            if (input["taskId"]?.GetValue<string>() is { } tid)
            {
                // A task created in an earlier session (or lost to a transcript restore) has no entry here. Create one
                // on the fly rather than dropping the update, so the panel still reflects what the CLI is doing.
                if (!_tasksById.TryGetValue(tid, out var entry))
                {
                    if (input["status"]?.GetValue<string>() == "deleted") return;
                    // Adoption may have failed (the CLI reworded/localized its result, or returned structured JSON we
                    // couldn't parse). If exactly ONE provisional TaskCreate row is still unadopted, this update is
                    // almost certainly about it - reconcile onto it instead of adding a permanent duplicate row.
                    var unadopted = _tasksById.Keys.Where(k => IsProvisional(k) && _adoptFailed.Contains(k)).Take(2).ToList();
                    if (unadopted.Count == 1)
                    {
                        ReKeyTask(unadopted[0], tid);
                        foreach (var toolId in _taskIdByTool.Where(kv => kv.Value == unadopted[0]).Select(kv => kv.Key).ToList())
                            _taskIdByTool[toolId] = tid;
                    }
                    if (!_tasksById.TryGetValue(tid, out entry))
                    {
                        // Real TaskUpdate payloads are only {taskId,status} - they never carry a subject - so seed a
                        // readable placeholder. A text-less row would render as a blank bullet yet still count in "n/m".
                        if (!_taskText.ContainsKey(tid)) _taskText[tid] = ("Task #" + tid, null);
                        entry = new TodoEntry { Status = "pending", Text = TaskText(tid, "pending") };
                        _tasksById[tid] = entry;
                        Todos.Add(entry);
                    }
                }
                var (subject, activeForm) = _taskText.TryGetValue(tid, out var cur) ? cur : ("", (string?)null);
                if (input["subject"]?.GetValue<string>() is { } ns) subject = ns;
                if (input["activeForm"]?.GetValue<string>() is { } na) activeForm = na;
                // No code path may write an empty label: a blank bullet still counts in "n/m" and reads as a bug.
                if (string.IsNullOrWhiteSpace(subject)) subject = "Task #" + tid;
                _taskText[tid] = (subject, activeForm);
                if (input["status"]?.GetValue<string>() == "deleted")
                {
                    Todos.Remove(entry);
                    _tasksById.Remove(tid);
                }
                else
                {
                    if (input["status"]?.GetValue<string>() is { } st) entry.Status = st;
                    entry.Text = TaskText(tid, entry.Status);
                }
                RaiseTodoProps();
            }
        }
        if (tool.Name is "Edit" or "MultiEdit" && tool.DiffLines.Count == 0)
        {
            foreach (var l in EditDiffLines(input, tool.Name)) tool.DiffLines.Add(l);
            tool.NotifyDiffChanged();
            if (tool.EditOps.Count == 0) tool.EditOps.AddRange(EditOpsFor(input, tool.Name));
        }
        else if (tool.Name == "Write")
        {
            // Full-file write: treat every line of content as added so the card shows "+N" like Edit does.
            // Rebuild whenever input is (re)applied so late/final payloads still update the count.
            if (tool.EditOps.Count == 0) tool.EditOps.AddRange(EditOpsFor(input, "Write"));
            else
            {
                tool.EditOps.Clear();
                tool.EditOps.AddRange(EditOpsFor(input, "Write"));
            }
            tool.DiffLines.Clear();
            var content = input["content"]?.GetValue<string>() ?? "";
            if (content.Length > 0)
            {
                foreach (var line in content.Replace("\r\n", "\n").Split('\n'))
                    tool.DiffLines.Add(new DiffLine { Kind = "add", Text = line });
            }
            tool.NotifyDiffChanged();
        }
        else if (tool.Name == "CodexEdit" && input["diff"]?.GetValue<string>() is { } unified)
        {
            foreach (var line in unified.Replace("\r\n", "\n").Split('\n'))
            {
                if (line.StartsWith("+++ ", StringComparison.Ordinal) || line.StartsWith("--- ", StringComparison.Ordinal)
                    || line.StartsWith("@@", StringComparison.Ordinal)) continue;
                var kind = line.StartsWith('+') ? "add" : line.StartsWith('-') ? "del" : "ctx";
                tool.DiffLines.Add(new DiffLine { Kind = kind, Text = line.Length > 0 && kind != "ctx" ? line[1..] : line.TrimStart(' ') });
            }
            tool.NotifyDiffChanged();
        }
        if (tool.Name is "Write" or "Edit" or "MultiEdit" or "NotebookEdit" or "CodexEdit"
            && (input["file_path"] ?? input["notebook_path"])?.GetValue<string>() is { } path)
        {
            TrackArtifact(path);
        }
    }

    private void TrackArtifact(string path)
    {
        _activeRollback?.TrackPath(path);
        var existing = Files.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing is null) Files.Insert(0, existing = new FileArtifact { Path = path, Writes = 1 });
        else existing.Writes++;
        if (SelectedFile is null || SelectedFile.Path == existing.Path) SelectedFile = existing;
    }

    /// <summary>Replace the visible checklist from either Claude's TodoWrite or Codex's turn/plan/updated.</summary>
    private void ReplaceTodos(JsonArray todos, ICollection<TodoEntry>? toolTodos = null)
    {
        Todos.Clear();
        // A full replacement supersedes Task* bookkeeping too; leaving these maps populated would let an old
        // TaskUpdate mutate a newly supplied Codex/Claude plan.
        _tasksById.Clear();
        _taskIdByTool.Clear();
        _taskText.Clear();
        _adoptFailed.Clear();
        foreach (var t in todos.OfType<JsonObject>())
        {
            var status = t["status"]?.GetValue<string>() ?? "pending";
            var text = (status == "in_progress" ? t["activeForm"]?.GetValue<string>() : null)
                       ?? t["content"]?.GetValue<string>() ?? "";
            if (string.IsNullOrWhiteSpace(text)) continue;
            var entry = new TodoEntry { Status = status, Text = text };
            Todos.Add(entry);
            toolTodos?.Add(entry);
        }
        RaiseTodoProps();
    }

    /// <summary>Re-key a TaskCreate entry from its provisional id onto the real one the CLI announces in the tool
    /// result ("Task #22 created successfully: …"). Without this every later TaskUpdate misses and silently no-ops.</summary>
    private void AdoptTaskId(ToolItem tool)
    {
        if (!_taskIdByTool.TryGetValue(tool.Id, out var prov)) return;
        if (ExtractTaskId(tool.Result) is not { } real)
        {
            // The result named no id we recognise. Flag the row so a later TaskUpdate can reconcile onto it instead of
            // opening a second, permanently duplicated row for the same task.
            if (IsProvisional(prov)) _adoptFailed.Add(prov);
            return;
        }
        if (real == prov) return;
        _taskIdByTool[tool.Id] = real;
        ReKeyTask(prov, real);
    }

    // Provisional ids are minted locally by TaskCreate and replaced once the CLI names the real one.
    private static bool IsProvisional(string id) => id.StartsWith("new:", StringComparison.Ordinal);

    /// <summary>Pull the real task id out of a TaskCreate result. The CLI's prose ("Task #22 created successfully")
    /// is only one of the shapes it has used - also accept a structured result ({"taskId":"22"} / {"id":22}) and a
    /// bare number, so a wording change can't strand the row under its provisional id forever.</summary>
    private static string? ExtractTaskId(string? result)
    {
        if (string.IsNullOrWhiteSpace(result)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(result, @"[Tt]ask #(\d+)");
        if (m.Success) return m.Groups[1].Value;
        m = System.Text.RegularExpressions.Regex.Match(result, @"""(?:taskId|task_id|id)""\s*:\s*""?(\d+)""?");
        if (m.Success) return m.Groups[1].Value;
        var trimmed = result.Trim();
        return trimmed.Length is > 0 and <= 18 && trimmed.All(char.IsAsciiDigit) ? trimmed : null;
    }

    private static int StatusRank(string? s) => s switch
    {
        "completed" => 3,
        "in_progress" => 2,
        "pending" => 1,
        _ => 0,
    };

    /// <summary>Move a task's row and text from one id to another, MERGING with anything already filed under the
    /// target. One CLI task must occupy exactly one row no matter what order the payloads arrive in, so the merge
    /// keeps the most advanced status seen on either row and the best known text (a real subject always beats the
    /// "Task #N" placeholder).</summary>
    private void ReKeyTask(string from, string to)
    {
        if (from == to) return;
        _adoptFailed.Remove(from);
        _taskText.Remove(from, out var fromText);
        _taskText.TryGetValue(to, out var toText);
        _taskText[to] = MergeText(fromText, toText, to);

        if (!_tasksById.Remove(from, out var entry))
        {
            if (_tasksById.TryGetValue(to, out var only)) { only.Text = TaskText(to, only.Status); RaiseTodoProps(); }
            return;
        }
        // Evict any entry already filed under the real id: overwriting the map alone would strand its row in Todos
        // forever - unreachable from any map, so no later TaskUpdate could ever advance or remove it. Its STATUS is
        // not garbage though: an update may already have moved it to in_progress/completed, so carry that across
        // rather than regressing the task to the provisional row's "pending".
        if (_tasksById.TryGetValue(to, out var stale) && !ReferenceEquals(stale, entry))
        {
            if (StatusRank(stale.Status) > StatusRank(entry.Status)) entry.Status = stale.Status;
            Todos.Remove(stale);
        }
        _tasksById[to] = entry;
        entry.Text = TaskText(to, entry.Status);
        RaiseTodoProps();
    }

    // Best-known text for a merged row: a real subject beats a "Task #N" placeholder, which beats nothing.
    private static (string Subject, string? ActiveForm) MergeText(
        (string Subject, string? ActiveForm) a, (string Subject, string? ActiveForm) b, string id)
    {
        var placeholder = "Task #" + id;
        bool Real(string? s) => !string.IsNullOrWhiteSpace(s) && s != placeholder && !IsPlaceholderOfAnyId(s);
        var subject = Real(a.Subject) ? a.Subject
            : Real(b.Subject) ? b.Subject
            : !string.IsNullOrWhiteSpace(a.Subject) ? a.Subject
            : !string.IsNullOrWhiteSpace(b.Subject) ? b.Subject
            : placeholder;
        return (subject, a.ActiveForm ?? b.ActiveForm);
    }

    private static bool IsPlaceholderOfAnyId(string s) =>
        System.Text.RegularExpressions.Regex.IsMatch(s, @"^Task #\d+$");

    // Show the present-tense activeForm while a task runs, else its subject (mirrors TodoWrite's behaviour).
    private string TaskText(string id, string status)
    {
        var (subject, activeForm) = _taskText.TryGetValue(id, out var t) ? t : ("", (string?)null);
        return status == "in_progress" && !string.IsNullOrEmpty(activeForm) ? activeForm! : subject;
    }

    private void RaiseTodoProps()
    {
        Raise(nameof(TodoSummary)); Raise(nameof(TodoDone)); Raise(nameof(TodoTotal));
        Raise(nameof(TodoRemaining)); Raise(nameof(TodoProgress)); Raise(nameof(HasTodos));
    }

    public string TodoSummary
    {
        get
        {
            if (Todos.Count == 0) return "";
            var done = Todos.Count(t => t.Status == "completed");
            return $"{done}/{Todos.Count}";
        }
    }

    public int TodoDone => Todos.Count(t => t.Status == "completed");
    public int TodoTotal => Todos.Count;
    /// <summary>Outstanding (not-yet-completed) todos. The pane badge binds THIS and hides at 0, so a finished list
    /// stops advertising a stale count (e.g. "5" when all five are already done).</summary>
    public int TodoRemaining => Todos.Count(t => t.Status != "completed");
    public double TodoProgress => Todos.Count == 0 ? 0 : (double)TodoDone / Todos.Count;
    public bool HasTodos => Todos.Count > 0;

    private static LiveUsage UsageOf(JsonNode? usage) => new(
        LongOrZero(usage?["input_tokens"]),
        LongOrZero(usage?["cache_creation_input_tokens"]),
        LongOrZero(usage?["cache_read_input_tokens"] ?? usage?["cached_input_tokens"]),
        LongOrZero(usage?["output_tokens"]));

    private void SetLiveUsage(LiveUsage usage)
    {
        if (_liveTurnUsage == usage) return;
        _liveTurnUsage = usage;
        Raise(nameof(TokensText));
        Raise(nameof(HasTokens));
        Raise(nameof(CostText));
    }

    private void ResetLiveUsage()
    {
        _liveUsageByMessage.Clear();
        _liveMessageByStream.Clear();
        _anonymousLiveMessageId = 0;
        SetLiveUsage(default);
    }

    /// <param name="mainThread">False for a subagent's request. Its tokens still count toward spend, but its
    /// context is a separate, much smaller conversation and must not redefine this chat's occupancy.</param>
    private void CaptureClaudeAssistantUsage(JsonNode? message, bool mainThread = true)
    {
        var usage = message?["usage"];
        if (usage is null) return;
        var id = message?["id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(id)) id = $"assistant:{++_anonymousLiveMessageId}";
        _liveUsageByMessage[id] = UsageOf(usage);
        if (mainThread) UpdateContext(usage, null);
        RefreshClaudeLiveUsage();
    }

    private void CaptureClaudeMessageStart(string streamKey, JsonNode ev, bool mainThread = true)
    {
        var message = ev["message"];
        var id = message?["id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(id)) id = $"stream:{streamKey}:{++_anonymousLiveMessageId}";
        _liveMessageByStream[streamKey] = id;
        if (message?["usage"] is not { } usage) return;
        _liveUsageByMessage[id] = UsageOf(usage);
        if (mainThread) UpdateContext(usage, null);
        RefreshClaudeLiveUsage();
    }

    private void CaptureClaudeMessageDelta(string streamKey, JsonNode ev)
    {
        if (ev["usage"] is not { } usage) return;
        if (!_liveMessageByStream.TryGetValue(streamKey, out var id))
        {
            id = $"stream:{streamKey}:{++_anonymousLiveMessageId}";
            _liveMessageByStream[streamKey] = id;
        }
        _liveUsageByMessage.TryGetValue(id, out var current);
        _liveUsageByMessage[id] = new LiveUsage(
            usage["input_tokens"] is null ? current.Input : LongOrZero(usage["input_tokens"]),
            usage["cache_creation_input_tokens"] is null ? current.CacheWrite : LongOrZero(usage["cache_creation_input_tokens"]),
            usage["cache_read_input_tokens"] is null ? current.CacheRead : LongOrZero(usage["cache_read_input_tokens"]),
            usage["output_tokens"] is null ? current.Output : LongOrZero(usage["output_tokens"]));
        RefreshClaudeLiveUsage();
    }

    private void RefreshClaudeLiveUsage()
    {
        double input = 0, cacheWrite = 0, cacheRead = 0, output = 0;
        foreach (var usage in _liveUsageByMessage.Values)
        {
            input += usage.Input;
            cacheWrite += usage.CacheWrite;
            cacheRead += usage.CacheRead;
            output += usage.Output;
        }
        SetLiveUsage(new LiveUsage(input, cacheWrite, cacheRead, output));
    }

    /// <summary>
    /// Merge runs of consecutive text/thinking deltas addressed to the same content block into one event.
    /// Token-level streams (Codex especially) otherwise cost a full string copy of the whole message per token.
    /// Only adjacent events merge, and only within one drained batch, so ordering with every other event kind
    /// (block start/stop, tool events, results) is preserved exactly.
    /// </summary>
    private static IEnumerable<JsonNode> CoalesceStreamDeltas(List<JsonNode> batch)
    {
        for (var i = 0; i < batch.Count; i++)
        {
            var node = batch[i];
            if (!TryReadDelta(node, out var key, out var index, out var deltaType, out var field, out var text))
            {
                yield return node;
                continue;
            }
            System.Text.StringBuilder? combined = null;
            while (i + 1 < batch.Count
                   && TryReadDelta(batch[i + 1], out var nextKey, out var nextIndex, out var nextType, out _, out var nextText)
                   && nextKey == key && nextIndex == index && nextType == deltaType)
            {
                (combined ??= new System.Text.StringBuilder(text)).Append(nextText);
                i++;
            }
            if (combined is not null && node["event"]?["delta"] is JsonObject delta)
                delta[field] = combined.ToString();
            yield return node;
        }
    }

    private static string StreamKey(string? parentToolUseId, string? subagentThreadId) =>
        string.IsNullOrWhiteSpace(subagentThreadId)
            ? parentToolUseId ?? "root"
            : $"subagent:{subagentThreadId}:{parentToolUseId ?? "root"}";

    private static string StreamKey(JsonNode node) =>
        StreamKey(NodeString(node["parent_tool_use_id"]), NodeString(node["subagent_thread_id"]));

    private static bool TryReadDelta(JsonNode node, out string key, out int index, out string deltaType, out string field, out string text)
    {
        key = ""; index = -1; deltaType = ""; field = ""; text = "";
        if (node is not JsonObject o || o["type"]?.GetValue<string>() != "stream_event") return false;
        if (o["event"] is not JsonObject ev || ev["type"]?.GetValue<string>() != "content_block_delta") return false;
        if (ev["delta"] is not JsonObject delta) return false;
        deltaType = delta["type"]?.GetValue<string>() ?? "";
        field = deltaType switch { "text_delta" => "text", "thinking_delta" => "thinking", _ => "" };
        if (field.Length == 0 || delta[field] is not JsonValue value) return false;
        try { text = value.GetValue<string>(); }
        catch { return false; }
        key = StreamKey(o);
        index = ev["index"]?.GetValue<int>() ?? 0;
        return true;
    }

    private void ApplyStreamEvent(JsonNode m)
    {
        var parent = NodeString(m["parent_tool_use_id"]);
        var subagentThreadId = NodeString(m["subagent_thread_id"]);
        var key = StreamKey(parent, subagentThreadId);
        var ev = m["event"];
        if (ev is null) return;
        var eventType = ev["type"]?.GetValue<string>();
        if (IsClaude)
        {
            if (eventType == "message_start")
                CaptureClaudeMessageStart(key, ev, mainThread: parent is null && subagentThreadId is null);
            else if (eventType == "message_delta") CaptureClaudeMessageDelta(key, ev);
        }
        switch (eventType)
        {
            case "message_start":
                _streams[key] = new List<ItemVm?>();
                if (_retryNote is not null) { _retryNote = null; Raise(nameof(WorkingText)); }
                break;
            case "content_block_start":
            {
                if (!_streams.TryGetValue(key, out var st)) _streams[key] = st = new List<ItemVm?>();
                var index = ev["index"]?.GetValue<int>() ?? st.Count;
                while (st.Count <= index) st.Add(null);
                var cb = ev["content_block"] as JsonObject;
                var cbType = cb?["type"]?.GetValue<string>();
                ItemVm? item = cbType switch
                {
                    "text" => new TextItem { Text = cb?["text"]?.GetValue<string>() ?? "", Streaming = true },
                    // Seed any initial thinking text from content_block_start (mirrors text blocks). Empty is
                    // normal at start; thinking_delta then fills it when display is summarized.
                    "thinking" or "redacted_thinking" => new ThinkingItem
                    {
                        Text = cb?["thinking"]?.GetValue<string>() ?? "",
                        Streaming = true,
                    },
                    "tool_use" or "server_tool_use" or "mcp_tool_use" => new ToolItem
                    {
                        Id = cb?["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString(),
                        Name = cb?["name"]?.GetValue<string>() ?? "?",
                    },
                    _ => null,
                };
                if (item is ToolItem tool) _toolById[tool.Id] = tool;
                if (item is not null) AddDisplayItem(Container(parent, subagentThreadId), item);
                st[index] = item;
                break;
            }
            case "content_block_delta":
            {
                if (!_streams.TryGetValue(key, out var st)) break;
                var index = ev["index"]?.GetValue<int>() ?? 0;
                if (index >= st.Count) break;
                var delta = ev["delta"] as JsonObject;
                switch (delta?["type"]?.GetValue<string>())
                {
                    case "text_delta" when st[index] is TextItem ti:
                        ti.Append(delta["text"]?.GetValue<string>() ?? "");
                        break;
                    case "thinking_delta" when st[index] is ThinkingItem th:
                    {
                        // Omitted-display streams often send thinking:"" with estimated_tokens only — skip no-ops.
                        var chunk = delta["thinking"]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(chunk)) th.Append(chunk);
                        break;
                    }
                }
                break;
            }
            case "content_block_stop":
            {
                if (!_streams.TryGetValue(key, out var st)) break;
                var index = ev["index"]?.GetValue<int>() ?? 0;
                if (index < st.Count)
                {
                    if (st[index] is TextItem ti) ti.Streaming = false;
                    if (st[index] is ThinkingItem th) th.Streaming = false;
                }
                break;
            }
        }
    }

    private static string TextOfContent(JsonNode? content)
    {
        if (content is JsonValue v) return v.GetValue<string>();
        if (content is JsonArray arr)
            return string.Join("\n", arr.OfType<JsonObject>()
                .Where(b => b["type"]?.GetValue<string>() == "text")
                .Select(b => b["text"]?.GetValue<string>()));
        return "";
    }

    /// <summary>Read a JSON number as int without throwing if it's missing or a string.</summary>
    private static int NumOrZero(JsonNode? n)
    {
        if (n is null) return 0;
        try { return (int)n.GetValue<double>(); }
        catch { return int.TryParse(n.ToString(), out var v) ? v : 0; }
    }

    /// <summary>Read a JSON number regardless of whether the parser stored it as an integer or floating point.</summary>
    private static double? DoubleOrNull(JsonNode? n)
    {
        if (n is null) return null;
        try { return n.GetValue<double>(); }
        catch
        {
            try { return n.GetValue<long>(); }
            catch
            {
                return double.TryParse(n.ToString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : null;
            }
        }
    }

    private static double DoubleOrZero(JsonNode? n) => DoubleOrNull(n) ?? 0;

    /// <summary>Read an integral JSON value regardless of whether its runtime representation is int, long or double.</summary>
    private static long LongOrZero(JsonNode? n)
    {
        if (n is null) return 0;
        try { return n.GetValue<long>(); }
        catch
        {
            try { return checked((long)n.GetValue<double>()); }
            catch { return long.TryParse(n.ToString(), out var value) ? value : 0; }
        }
    }

    /// <summary>Turn a web_search/web_fetch tool-result block into readable markdown for the tool card.</summary>
    private static (string text, bool error) SummarizeServerTool(string blockType, JsonNode? content)
    {
        // Error shape: content is an object whose type ends in "_error".
        if (content is JsonObject eo && (eo["type"]?.GetValue<string>()?.EndsWith("_error") ?? false))
            return ($"Error: {eo["error_code"]?.GetValue<string>() ?? "request failed"}", true);

        if (blockType == "web_search_tool_result" && content is JsonArray results)
        {
            var lines = results.OfType<JsonObject>()
                .Where(r => r["type"]?.GetValue<string>() == "web_search_result")
                .Select(r =>
                {
                    var title = r["title"]?.GetValue<string>() ?? "(untitled)";
                    var url = r["url"]?.GetValue<string>() ?? "";
                    return $"- [{title}]({url})";
                }).ToList();
            var header = $"{lines.Count} result{(lines.Count == 1 ? "" : "s")}";
            return (lines.Count == 0 ? header : $"{header}\n{string.Join("\n", lines)}", false);
        }

        if (blockType == "web_fetch_tool_result" && content is JsonObject fetch)
        {
            var url = fetch["url"]?.GetValue<string>();
            var src = fetch["content"]?["source"];
            var body = src?["type"]?.GetValue<string>() == "text" ? src?["data"]?.GetValue<string>() : null;
            var head = url is null ? "Fetched page" : $"Fetched {url}";
            return (body is null ? head : $"{head}\n\n{ToolItem.Trunc(body, 2000)}", false);
        }

        return (NormalizeResult(content), false);
    }

    private static string NormalizeResult(JsonNode? content)
    {
        if (content is null) return "";
        if (content is JsonValue v) return v.GetValue<string>();
        if (content is JsonArray arr)
            return string.Join("\n", arr.Select(b =>
                b is JsonObject o && o["type"]?.GetValue<string>() == "text"
                    ? o["text"]?.GetValue<string>() ?? ""
                    : b?.ToJsonString() ?? ""));
        return content.ToJsonString();
    }

    public void RefreshPreview() => LoadPreview();

    /// <summary>Clear the artifacts panel: drop the preview and every tracked file entry.
    /// Only clears the in-app list - it never deletes the files on disk.</summary>
    public void ClearFiles()
    {
        SelectedFile = null;   // also clears the preview via LoadPreview
        Files.Clear();
    }

    private void LoadPreview()
    {
        if (_selectedFile is null) { PreviewText = ""; return; }
        try
        {
            var info = new FileInfo(_selectedFile.Path);
            if (!info.Exists) { PreviewText = "(file not found)"; return; }
            if (info.Length > 400_000) { PreviewText = "(file too large to preview - use Open)"; return; }
            PreviewText = File.ReadAllText(_selectedFile.Path);
        }
        catch (Exception ex) { PreviewText = $"(could not read file: {ex.Message})"; }
    }
}

internal static class DispatcherExtensions
{
    public static void BeginPriorityInvoke(this Dispatcher d, Action a) =>
        d.BeginInvoke(a, DispatcherPriority.Background);
}
