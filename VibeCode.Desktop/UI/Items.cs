using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VibeCode.Services;

namespace VibeCode.UI;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

public abstract class ItemVm : Observable;

/// <summary>Backing collection for a chat transcript. A plain Add lands ABOVE any pending <see cref="QueuedItem"/>,
/// so the greyed "Queued · sends when the agent finishes" cards stay parked at the very bottom where they read as
/// "still to come". Cards are appended here from several layers — stream/tool output, permission requests, and the
/// bridge/manager notices MainViewModel posts straight onto a peer's transcript — and enforcing the rule inside the
/// collection keeps all of them honest instead of trusting every caller to remember.</summary>
public sealed class TranscriptItems : ObservableCollection<ItemVm>
{
    protected override void InsertItem(int index, ItemVm item)
    {
        // Only plain appends are redirected. An explicit Insert at a known index is left alone, and a QueuedItem
        // itself must still be able to reach the true end.
        if (index == Count && item is not QueuedItem)
            for (var i = 0; i < Count; i++)
                if (this[i] is QueuedItem) { index = i; break; }
        base.InsertItem(index, item);
    }
}

/// <summary>
/// A file/image the user attached to a message. Carries both the wire payload
/// (base64 bytes or inlined text) and a thumbnail for display.
/// </summary>
public sealed class Attachment : Observable
{
    public required string Kind { get; init; }        // image | document | text
    public required string FileName { get; init; }
    public string? MediaType { get; init; }           // image/png, image/jpeg, application/pdf, ...
    public byte[]? Data { get; init; }                // image/document bytes
    public string? Text { get; init; }                // text-file contents (inlined as a text block)
    public BitmapSource? Preview { get; init; }        // thumbnail for image attachments (null => show a chip)

    public bool IsImage => Kind == "image";
    public bool HasPreview => Preview is not null;
    public string Base64 => Data is null ? "" : System.Convert.ToBase64String(Data);
    /// <summary>Segoe Fluent glyph for the chip when there's no image thumbnail.</summary>
    public string Glyph => Kind switch { "document" => "", _ => "" };   // document / generic file
    public string SizeText
    {
        get
        {
            var bytes = Data?.Length ?? (Text is null ? 0 : System.Text.Encoding.UTF8.GetByteCount(Text));
            return bytes >= 1024 * 1024 ? $"{bytes / 1024.0 / 1024.0:0.0} MB"
                 : bytes >= 1024 ? $"{bytes / 1024.0:0} KB"
                 : $"{bytes} B";
        }
    }
}

/// <summary>One Codex child thread surfaced by app-server's collabAgentToolCall/subAgentActivity lifecycle.</summary>
public sealed class SubagentItem : Observable
{
    private string _threadId = "";
    private string? _parentToolUseId;
    private string _label = "Subagent";
    private string _task = "";
    private string _activity = "Starting";
    private string _status = "pendingInit";
    private ObservableCollection<ItemVm> _transcript = new();

    public SubagentItem() => _transcript.CollectionChanged += OnTranscriptChanged;

    public required string ThreadId { get => _threadId; set => Set(ref _threadId, value); }
    /// <summary>The Claude Agent/Task tool that owns this worker. Codex child threads do not need one.</summary>
    public string? ParentToolUseId { get => _parentToolUseId; set => Set(ref _parentToolUseId, value); }
    public string Label { get => _label; set => Set(ref _label, value); }
    public string Task { get => _task; set => Set(ref _task, value); }
    public string Activity { get => _activity; set => Set(ref _activity, value); }
    public string Status
    {
        get => _status;
        set
        {
            if (!Set(ref _status, value)) return;
            Raise(nameof(IsActive));
            Raise(nameof(StatusText));
            Raise(nameof(VisualStatus));
            Raise(nameof(TranscriptEmptyText));
        }
    }

    /// <summary>
    /// Live child conversation. Claude aliases this to the existing Agent tool's Children collection; Codex writes
    /// child-thread events here directly. A shared collection keeps the inline Agent card and inspector in sync.
    /// </summary>
    public ObservableCollection<ItemVm> Transcript => _transcript;
    public bool HasTranscript => _transcript.Count > 0;
    public string TranscriptEmptyText => IsActive
        ? "Waiting for this subagent's first detailed event…"
        : "This provider did not retain detailed output for this subagent.";

    internal void UseTranscript(ObservableCollection<ItemVm> transcript)
    {
        if (ReferenceEquals(_transcript, transcript)) return;
        _transcript.CollectionChanged -= OnTranscriptChanged;
        _transcript = transcript;
        _transcript.CollectionChanged += OnTranscriptChanged;
        Raise(nameof(Transcript));
        Raise(nameof(HasTranscript));
    }

    private void OnTranscriptChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        Raise(nameof(HasTranscript));

    public bool IsActive => _status is "pendingInit" or "running";
    public string StatusText => _status switch
    {
        "pendingInit" => "starting",
        "running" => "running",
        "completed" => "done",
        "interrupted" => "stopped",
        "errored" => "error",
        "shutdown" => "closed",
        "notFound" => "unavailable",
        _ => _status,
    };
    /// <summary>Normalized status understood by the shared status-dot brush converter.</summary>
    public string VisualStatus => _status switch
    {
        "pendingInit" => "starting",
        "running" => "running",
        "errored" => "error",
        "interrupted" or "shutdown" or "notFound" => "closed",
        _ => "done",
    };
}

public sealed class UserItem : ItemVm
{
    private TurnRollbackCheckpoint? _rollbackCheckpoint;
    private bool _undoBlockedByLaterTurn;
    private bool _undoInProgress;

    public required string Text { get; init; }
    /// <summary>The chat/pane that owns this row. Kept explicitly so shared message templates never have to guess
    /// ownership from the visual tree (which differs between the normal transcript and Bridge panes).</summary>
    public ChatViewModel? Owner { get; init; }
    public bool FromSubagent { get; init; }
    public IReadOnlyList<Attachment>? Attachments { get; init; }
    public bool HasAttachments => Attachments is { Count: > 0 };
    public bool HasText => !string.IsNullOrWhiteSpace(Text);
    /// <summary>Single-line snippet of the prompt for the "jump to your messages" navigator list (newlines collapsed).</summary>
    public string Preview => CompactToolPresentation.ToSingleLine(Text);
    /// <summary>Short hover text for the navigator row — full Preview can be thousands of chars (bridge/system
    /// prompts) and turns the default ToolTip into a screen-covering blob.</summary>
    public string ToolTipPreview
    {
        get
        {
            var p = Preview;
            const int max = 140;
            if (p.Length <= max) return p;
            return p[..(max - 1)] + "…";
        }
    }

    /// <summary>Only locally-sent prompts have a checkpoint. Restored/history messages deliberately do not.</summary>
    public bool ShowUndo => _rollbackCheckpoint is not null && !FromSubagent;
    public bool CanUndo => !_undoBlockedByLaterTurn && _rollbackCheckpoint?.CanRollback == true;
    /// <summary>The rewind button stays actionable while the current turn is running or its checkpoint is sealing.
    /// Clicking that state requests Stop first; completed older turns remain ordered by CanUndo.</summary>
    public bool CanRequestUndo => !_undoInProgress
                                  && !_undoBlockedByLaterTurn
                                  && _rollbackCheckpoint is { WasRolledBack: false } checkpoint
                                  && checkpoint.UnavailableReason is null
                                  && !checkpoint.BlockedByNewerTurn
                                  && (checkpoint.CanRollback || !checkpoint.IsCompleted);
    public bool HasUndoStatus => _rollbackCheckpoint?.WasRolledBack == true;
    public int UndoChangedFileCount => _rollbackCheckpoint?.ChangedFileCount ?? 0;
    public string UndoStatusText => HasUndoStatus ? "Changes undone · prompt restored" : "";
    public string UndoToolTip
    {
        get
        {
            if (_rollbackCheckpoint is null) return "Undo is unavailable for restored history";
            if (_undoInProgress) return "Stopping this turn and restoring the prompt…";
            if (_rollbackCheckpoint.WasRolledBack) return "This prompt's file changes have already been undone";
            if (_undoBlockedByLaterTurn) return "Undo the newer prompt first so file history stays consistent";
            if (_rollbackCheckpoint.BlockedByNewerTurn) return "Undo the newer prompt that also edited these files first (including other Bridge panes)";
            if (_rollbackCheckpoint.UnavailableReason is { Length: > 0 } reason) return reason;
            if (!_rollbackCheckpoint.IsCompleted) return "Stop this turn and its subagents, then restore the prompt and undo its changes";
            var files = _rollbackCheckpoint.ChangedFileCount;
            return files == 0
                ? "Restore this prompt and its attachments to the message input"
                : $"Undo this prompt's changes in {files} file{(files == 1 ? "" : "s")} and restore it to the message input";
        }
    }

    internal TurnRollbackCheckpoint? RollbackCheckpoint => _rollbackCheckpoint;
    internal bool UndoBlockedByLaterTurn => _undoBlockedByLaterTurn;

    internal void SetUndoInProgress(bool inProgress)
    {
        if (!Set(ref _undoInProgress, inProgress)) return;
        Raise(nameof(CanRequestUndo));
        Raise(nameof(UndoToolTip));
    }

    internal void AttachRollbackCheckpoint(TurnRollbackCheckpoint checkpoint)
    {
        if (_rollbackCheckpoint is not null)
            _rollbackCheckpoint.StateChanged -= OnRollbackStateChanged;
        _rollbackCheckpoint = checkpoint;
        checkpoint.StateChanged += OnRollbackStateChanged;
        OnRollbackStateChanged();
    }

    internal void SetUndoBlockedByLaterTurn(bool blocked)
    {
        if (_undoBlockedByLaterTurn == blocked) return;
        _undoBlockedByLaterTurn = blocked;
        Raise(nameof(CanUndo));
        Raise(nameof(CanRequestUndo));
        Raise(nameof(UndoToolTip));
    }

    private void OnRollbackStateChanged()
    {
        Raise(nameof(ShowUndo));
        Raise(nameof(CanUndo));
        Raise(nameof(CanRequestUndo));
        Raise(nameof(HasUndoStatus));
        Raise(nameof(UndoChangedFileCount));
        Raise(nameof(UndoStatusText));
        Raise(nameof(UndoToolTip));
    }
}

/// <summary>A message accepted while an agent was still working. Normal queue entries may be folded together;
/// extended-queue entries stay independent so a long FIFO can be dispatched in controlled chunks.</summary>
public sealed class QueuedItem : ItemVm
{
    private string _text = "";
    /// <summary>Settable, not init-only: a prompt typed while this one is still pending is folded into it rather
    /// than stacking a second card (see ChatViewModel.QueuePrompt). One pending card, one "Send prompt now".</summary>
    public required string Text
    {
        get => _text;
        set { if (Set(ref _text, value)) Raise(nameof(HasText)); }
    }
    /// <summary>The chat or Bridge pane that owns this queue entry. Shared item templates must not infer ownership
    /// from their visual ancestors because the normal transcript and Bridge panes have different trees.</summary>
    public required ChatViewModel Owner { get; init; }
    private IReadOnlyList<Attachment>? _attachments;
    /// <summary>Grows when a folded-in prompt brings its own attachments.</summary>
    public IReadOnlyList<Attachment>? Attachments
    {
        get => _attachments;
        set { if (Set(ref _attachments, value)) Raise(nameof(HasAttachments)); }
    }
    /// <summary>The one-shot choice is captured with the queued prompt so later UI toggles cannot change it.</summary>
    public bool UseSwarm { get; init; }
    /// <summary>True when this entry belongs to the opt-in extended queue. Extended entries are never folded into
    /// their neighbour and are dispatched in the owner's configured 1-3 request chunks.</summary>
    public bool Extended { get; init; }
    private bool _waitingForSwarmCapacity;
    public bool WaitingForSwarmCapacity { get => _waitingForSwarmCapacity; set { if (Set(ref _waitingForSwarmCapacity, value)) Raise(nameof(QueueStatusText)); } }
    private bool _isQueueHead;
    /// <summary>Only the oldest pending prompt owns the shared "Send prompt now" action.</summary>
    public bool IsQueueHead { get => _isQueueHead; internal set => Set(ref _isQueueHead, value); }
    private int _queuePosition;
    private int _queueTotal;
    private int _retryCount;
    public int QueuePosition { get => _queuePosition; private set => Set(ref _queuePosition, value); }
    public int QueueTotal { get => _queueTotal; private set => Set(ref _queueTotal, value); }
    public int RetryCount
    {
        get => _retryCount;
        internal set { if (Set(ref _retryCount, value)) RefreshQueueState(); }
    }
    public string QueueStatusText => WaitingForSwarmCapacity
        ? "Queued · waiting for shared swarm capacity"
        : Extended && Owner.ExtendedQueuePaused
            ? Owner.ExtendedQueuePauseText
            : Extended
                ? $"Extended queue · {QueuePosition}/{QueueTotal} · {Owner.ExtendedQueueChunkSize} per turn" +
                  (RetryCount > 0 ? $" · retry {RetryCount}" : "")
                : "Queued · sends when the agent finishes";
    public string QueueActionText => Extended
        ? Owner.ExtendedQueuePaused ? "Resume queue" : "Send next chunk now"
        : "Send prompt now";
    public string QueueActionToolTip => Extended
        ? Owner.ExtendedQueuePaused
            ? "Resume the extended queue now instead of waiting for the next usage check"
            : $"Stop the active turn if needed, then send the next {Owner.ExtendedQueueChunkSize} queued request(s)"
        : "Stop the active turn if needed, then send all queued prompts together";

    internal void UpdateQueuePosition(int position, int total)
    {
        QueuePosition = position;
        QueueTotal = total;
        RefreshQueueState();
    }

    internal void RefreshQueueState()
    {
        Raise(nameof(QueueStatusText));
        Raise(nameof(QueueActionText));
        Raise(nameof(QueueActionToolTip));
    }
    public bool HasText => !string.IsNullOrWhiteSpace(Text);
    public bool HasAttachments => Attachments is { Count: > 0 };
}

public sealed class TextItem : ItemVm
{
    // A streaming message renders as markdown too, not as raw source. The template binds RenderText (not Text):
    // MdXaml rebuilds the whole FlowDocument on every parse and the CLI emits deltas far faster than that, so the
    // snapshot is published on a throttle whose interval grows with the message. Without this a long turn showed
    // literal "[file.cs:27](C:/...)" links and "1." / "- " list source for as long as the turn ran.

    // U+258C LEFT HALF BLOCK, built from its code point: a literal glyph here would be mangled the next time this
    // file is round-tripped through PowerShell (see the TodoEntry.Mark / ToolItem.Icon mojibake trap).
    private static readonly string Cursor = char.ConvertFromUtf32(0x258C);
    // Measured against MdXaml 1.27.0, which parses synchronously on the UI thread: ~0.02 ms per character
    // (~17 ms at 1k chars, ~97 ms at 4k, ~356 ms at 20k). A fixed interval would therefore peg the UI thread on a
    // long message, so the interval scales with length and holds the parse at roughly an eighth of wall time.
    private const double IntervalPerChar = 0.16;
    private const int MinIntervalMs = 120, MaxIntervalMs = 3_000;

    private string _text = "", _render = "";
    private bool _streaming;
    private DateTime _published = DateTime.MinValue;
    private DispatcherTimer? _flush;

    /// <summary>The raw message text — what Copy yields and what the final assistant message reconciles against.</summary>
    public string Text
    {
        get => _text;
        set { if (Set(ref _text, value)) { Raise(nameof(HasText)); Publish(); } }
    }

    public bool Streaming
    {
        get => _streaming;
        // Both edges publish immediately: the first delta shouldn't wait for a tick, and the finished text must
        // land exactly (no cursor, no synthetic fence) the moment the block stops.
        set { if (Set(ref _streaming, value)) Publish(force: true); }
    }

    /// <summary>Markdown handed to the viewer: the finished text once idle, otherwise a throttled snapshot.</summary>
    public string RenderText => _render;

    public bool HasText => !string.IsNullOrWhiteSpace(_text);

    public void Append(string delta) { _text += delta; Raise(nameof(Text)); Raise(nameof(HasText)); Publish(); }

    private void Publish(bool force = false)
    {
        var interval = (int)Math.Clamp(_text.Length * IntervalPerChar, MinIntervalMs, MaxIntervalMs);
        if (!force && _streaming && (DateTime.UtcNow - _published).TotalMilliseconds < interval)
        {
            ScheduleFlush(interval);   // a stream that stalls mid-throttle still lands its last delta
            return;
        }
        StopFlush();
        _published = DateTime.UtcNow;
        var next = Snapshot();
        if (next == _render) return;
        _render = next;
        Raise(nameof(RenderText));
    }

    // Closing an unterminated fence keeps a half-streamed code block rendering as code instead of leaking its
    // source into the prose; it is dropped again as soon as the model writes the real closing fence.
    private string Snapshot() =>
        !_streaming ? _text : IsFenceOpen(_text) ? _text + Cursor + "\n```" : _text + Cursor;

    private static bool IsFenceOpen(string s)
    {
        var open = false;
        for (var i = 0; i < s.Length;)
        {
            var end = s.IndexOf('\n', i);
            if (end < 0) end = s.Length;
            if (s.AsSpan(i, end - i).TrimStart().StartsWith("```")) open = !open;
            i = end + 1;
        }
        return open;
    }

    private void ScheduleFlush(int interval)
    {
        if (_flush is not null) return;
        _flush = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(interval) };
        _flush.Tick += (_, _) => Publish(force: true);
        _flush.Start();
    }

    private void StopFlush()
    {
        if (_flush is null) return;
        _flush.Stop();
        _flush = null;
    }
}

public sealed class ThinkingItem : ItemVm
{
    private string _text = "";
    private bool _streaming;
    public string Text { get => _text; set { if (Set(ref _text, value)) Raise(nameof(HasText)); } }
    public bool Streaming { get => _streaming; set { if (Set(ref _streaming, value)) Raise(nameof(Header)); } }
    public string Header => Streaming ? "Thinking…" : "Thought process";
    // HasText gates the expander in the template: real reasoning text -> clickable card that expands to the
    // body; no text -> a static "Thought process" marker (never a dead expander that opens an empty box).
    // Claude: newer models default thinking.display to "omitted" (empty thinking + signature only). We launch
    // with --thinking-display summarized so thinking_delta carries plaintext summaries like interactive
    // Claude Code. Codex/Kimi/Grok already stream thinking text into this field.
    public bool HasText => _text.Length > 0;
    public void Append(string delta)
    {
        if (string.IsNullOrEmpty(delta)) return;
        _text += delta;
        Raise(nameof(Text));
        Raise(nameof(HasText));
    }
}

public sealed class DiffLine : Observable
{
    public required string Kind { get; init; } // add | del | ctx
    public required string Text { get; init; }
}

public sealed class TodoEntry : Observable
{
    private string _status = "pending";
    private string _text = "";
    public required string Status { get => _status; set { if (Set(ref _status, value)) { Raise(nameof(Mark)); Raise(nameof(Done)); } } }  // pending | in_progress | completed
    public required string Text { get => _text; set => Set(ref _text, value); }
    /// <summary>Segoe Fluent Icons glyph: checked box / dash box / empty box.</summary>
    public string Mark => Status switch { "completed" => "", "in_progress" => "", _ => "" };
    public bool Done => Status == "completed";
}

public sealed class ToolItem : ItemVm
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string DisplayName => Name == "CodexEdit" ? "Edit" : Name;

    private JsonNode? _input;
    private string _status = "running";  // running | done | error
    private string? _result;
    private bool _isError;
    private bool _expanded;
    private bool _isAgent;
    private string? _progressText;
    private double _elapsed;
    private string? _summaryOverride;

    public JsonNode? Input { get => _input; set { _input = value; RaiseAll(); } }
    public string Status { get => _status; set { if (Set(ref _status, value)) { Raise(nameof(IsRunning)); } } }
    public string? Result { get => _result; set { if (Set(ref _result, value)) Raise(nameof(ResultCapped)); } }
    public bool IsError { get => _isError; set => Set(ref _isError, value); }
    public bool Expanded { get => _expanded; set => Set(ref _expanded, value); }
    public bool IsAgent { get => _isAgent; set { if (Set(ref _isAgent, value)) Raise(nameof(Icon)); } }
    public string? ProgressText { get => _progressText; set => Set(ref _progressText, value); }
    public double Elapsed { get => _elapsed; set { if (Set(ref _elapsed, value)) Raise(nameof(ElapsedText)); } }
    public string? SummaryOverride
    {
        get => _summaryOverride;
        set
        {
            if (!Set(ref _summaryOverride, value)) return;
            Raise(nameof(Summary));
            Raise(nameof(HeaderSummary));
        }
    }

    public ObservableCollection<ItemVm> Children { get; } = new();
    public ObservableCollection<DiffLine> DiffLines { get; } = new();
    public ObservableCollection<TodoEntry> Todos { get; } = new();

    public bool IsRunning => _status == "running";
    public string ElapsedText => _elapsed > 0 && IsRunning ? $"{_elapsed:0}s" : "";

    // ---- edit coalescing + line stats (Edit/MultiEdit) ----
    private int _editCount = 1;
    /// <summary>How many consecutive edits to the same file this one card represents (coalesced display).</summary>
    public int EditCount { get => _editCount; set { if (Set(ref _editCount, value)) { Raise(nameof(EditCountText)); Raise(nameof(IsMultiEdit)); } } }
    public bool IsMultiEdit => _editCount > 1;
    public string EditCountText => _editCount > 1 ? $"{_editCount} edits" : "";
    public int Added => DiffLines.Count(d => d.Kind == "add");
    public int Removed => DiffLines.Count(d => d.Kind == "del");
    /// <summary>True when the header should show a green/red line count (edits + full-file Write).</summary>
    public bool HasDiffStat => (Added > 0 || Removed > 0) && Name is "Edit" or "MultiEdit" or "CodexEdit" or "Write";
    /// <summary>"+12  −3" (uses the real minus sign so it lines up). Write is all adds, so − stays 0.</summary>
    public string DiffStat => $"+{Added}  −{Removed}";
    /// <summary>Call after DiffLines change so the +/- stat + count refresh.</summary>
    public void NotifyDiffChanged() { Raise(nameof(Added)); Raise(nameof(Removed)); Raise(nameof(DiffStat)); Raise(nameof(HasDiffStat)); }

    /// <summary>Ordered (old,new) string pairs for every edit this card represents - accumulates across coalesced
    /// same-file edits so the code viewer can reconstruct the pre-edit file and show cumulative green/red.</summary>
    public List<(string Old, string New)> EditOps { get; } = new();
    /// <summary>Absolute path of the file this tool targets (Edit/Write/MultiEdit/NotebookEdit), if any.</summary>
    public string? FilePath => Str(Input?["file_path"]) ?? Str(Input?["notebook_path"]);

    /// <summary>
    /// Best guess at the line this edit landed on, so "Open in editor" can put the caret there. Located by
    /// searching the file on disk for the edit's first replaced line rather than by tracking offsets: the
    /// file has usually moved on by the time the user clicks, and a stale offset would jump somewhere wrong.
    /// Null when it cannot be pinned down, which simply means the editor opens at the top.
    /// </summary>
    public int? FirstEditedLine
    {
        get
        {
            var path = FilePath;
            if (string.IsNullOrWhiteSpace(path)) return null;
            var needle = Str(Input?["new_string"]) ?? Str(Input?["old_string"]) ?? Str(Input?["content"]);
            if (string.IsNullOrWhiteSpace(needle)) return null;
            try
            {
                var probe = needle.Split('\n')[0].TrimEnd('\r').Trim();
                if (probe.Length < 4) return null;          // too generic to match reliably
                var lines = System.IO.File.ReadAllLines(path);
                for (var i = 0; i < lines.Length; i++)
                    if (lines[i].Contains(probe, StringComparison.Ordinal)) return i + 1;
            }
            catch { /* unreadable file just means no line hint */ }
            return null;
        }
    }
    /// <summary>Whether the "View full code" affordance applies (file-editing tools).</summary>
    public bool CanViewCode => Name is "Edit" or "MultiEdit" or "CodexEdit" or "Write" or "NotebookEdit";
    private static string? Str(JsonNode? n) { try { return n?.GetValue<string>(); } catch { return null; } }

    /// <summary>
    /// The shell tools - "Bash" on POSIX, "PowerShell" on Windows - share one display policy: a single call can
    /// return pages of compiler/test output, so a failure must never auto-open the card, and they group together
    /// in compact mode. Anything that special-cases "Bash" almost certainly means "any shell".
    /// </summary>
    public static bool IsShellTool(string name) => name is "Bash" or "PowerShell" or "BashOutput" or "KillShell";

    /// <summary>Segoe Fluent Icons glyph for the tool.</summary>
    public string Icon => Name switch
    {
        "Bash" or "PowerShell" or "BashOutput" or "KillShell" => "",    // command prompt
        "Read" => "",                                   // page
        "Write" => "",                                  // document
        "Edit" or "MultiEdit" or "NotebookEdit" or "CodexEdit" => "",  // pencil
        "Grep" or "Glob" or "LS" => "",                 // search
        "WebSearch" or "WebFetch" => "",                // globe
        "Task" or "Agent" or "spawn_subagent" => "",    // people (Claude Agent/Task + Grok spawn_subagent)
        "TodoWrite" => "",                              // checked box
        "AskUserQuestion" => "",                        // help
        "ExitPlanMode" or "EnterPlanMode" => "",        // document
        "Skill" or "SlashCommand" => "",                // lightning
        _ when Name.StartsWith("mcp__") => "",          // plug
        _ => "",                                        // gear
    };

    public string Summary
    {
        get
        {
            if (_summaryOverride is not null) return _summaryOverride;
            var i = _input;
            if (i is null) return "";
            return Name switch
            {
                "Bash" or "PowerShell" => S(i["command"]) ?? S(i["description"]) ?? "",
                "Read" or "Write" => S(i["file_path"]) ?? "",
                "Edit" or "MultiEdit" or "CodexEdit" => S(i["file_path"]) ?? "",
                "NotebookEdit" => S(i["notebook_path"]) ?? "",
                "Grep" => $"{S(i["pattern"])}{(S(i["path"]) is { } p ? $"  in {p}" : "")}",
                "Glob" => S(i["pattern"]) ?? "",
                "WebFetch" => S(i["url"]) ?? "",
                "WebSearch" => WebSearchSummary(i),
                "Task" or "Agent" or "spawn_subagent" =>
                    S(i["description"]) ?? Trunc(S(i["prompt"]) ?? "", 90),
                "TaskCreate" => S(i["subject"]) ?? "",
                "TaskUpdate" => S(i["subject"]) ?? (S(i["status"]) is { } us ? $"#{S(i["taskId"])}: {us}" : $"#{S(i["taskId"])}"),
                "TodoWrite" => $"{(i["todos"] as JsonArray)?.Count ?? 0} items",
                "ExitPlanMode" => "proposed a plan",
                "Skill" or "SlashCommand" => S(i["command"]) ?? S(i["skill"]) ?? "",
                _ => Trunc(i.ToJsonString(), 90),
            };
            static string? S(JsonNode? n) => n?.GetValue<string>();
        }
    }

    /// <summary>
    /// A genuinely single-line form for collapsed headers. TextWrapping="NoWrap" does not suppress explicit CR/LF
    /// characters, which previously let multiline PowerShell commands expand a supposedly collapsed tool row.
    /// </summary>
    public string HeaderSummary => CompactToolPresentation.ToSingleLine(Summary);

    public string AgentType => (Input?["subagent_type"]?.GetValue<string>())
        ?? (Input?["agent_type"]?.GetValue<string>())
        ?? "agent";

    /// <summary>Claude Agent/Task and Grok <c>spawn_subagent</c> all own a child worker in the Subagents roster.</summary>
    public static bool IsSubagentSpawnTool(string? name) =>
        name is "Agent" or "Task" or "spawn_subagent"
        || string.Equals(name, "SpawnSubagent", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "spawn_agent", StringComparison.OrdinalIgnoreCase);
    public string BodyKind => Name switch
    {
        "Edit" or "MultiEdit" or "CodexEdit" => "diff",
        "TodoWrite" => "todos",
        "Write" => "write",
        "ExitPlanMode" => "markdown",
        _ => "generic",
    };
    public string InputPretty => Input?.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) ?? "";
    public string WriteContent => Trunc(Input?["content"]?.GetValue<string>() ?? "", 6000);
    public string PlanMarkdown => Input?["plan"]?.GetValue<string>() ?? "";
    public string ResultCapped => Trunc(_result ?? "", 6000);
    public string BashCommand => Input?["command"]?.GetValue<string>() ?? "";
    /// <summary>Raw request details needed by compact rows whose dedicated body would otherwise be empty.</summary>
    public string CompactFallbackInput => Name == "NotebookEdit" || Name == "WebFetch" || Name.StartsWith("mcp__", StringComparison.Ordinal)
        ? Trunc(InputPretty, 6000)
        : "";
    /// <summary>Caption for the exact Codex web action shown inside the expanded tool card.</summary>
    public string WebSearchDetailLabel
    {
        get
        {
            if (Name != "WebSearch") return "";
            var action = (Input as JsonObject)?["action"] as JsonObject;
            return NodeString(action?["type"]) switch
            {
                "openPage" => "OPENED PAGE",
                "findInPage" => "FOUND IN PAGE",
                _ => WebSearchQueries(Input).Count > 1 ? "SEARCH QUERIES" : "SEARCH QUERY",
            };
        }
    }
    /// <summary>
    /// Human-readable, lossless details for Codex's search/open/find action. Action-level query fields take
    /// precedence because they describe the actual operation; the required legacy top-level query is the fallback.
    /// </summary>
    public string WebSearchDetails
    {
        get
        {
            if (Name != "WebSearch") return "";
            var input = Input as JsonObject;
            var action = input?["action"] as JsonObject;
            return NodeString(action?["type"]) switch
            {
                "openPage" => NodeString(action?["url"]) ?? NodeString(input?["query"]) ?? "",
                "findInPage" => JoinNonEmpty(NodeString(action?["pattern"]), NodeString(action?["url"])),
                _ => string.Join(Environment.NewLine, WebSearchQueries(input)),
            };
        }
    }

    private void RaiseAll()
    {
        Raise(nameof(Input)); Raise(nameof(Summary)); Raise(nameof(HeaderSummary)); Raise(nameof(InputPretty));
        Raise(nameof(WriteContent)); Raise(nameof(PlanMarkdown)); Raise(nameof(BashCommand));
        Raise(nameof(CompactFallbackInput)); Raise(nameof(AgentType));
        Raise(nameof(WebSearchDetailLabel)); Raise(nameof(WebSearchDetails));
    }

    private static string WebSearchSummary(JsonNode? input)
    {
        var obj = input as JsonObject;
        var topLevel = NodeString(obj?["query"]);
        if (!string.IsNullOrWhiteSpace(topLevel)) return topLevel;

        var action = obj?["action"] as JsonObject;
        return NodeString(action?["type"]) switch
        {
            "openPage" => NodeString(action?["url"]) ?? "",
            "findInPage" => JoinNonEmpty(NodeString(action?["pattern"]), NodeString(action?["url"]), "  in  "),
            _ => string.Join("  ·  ", WebSearchQueries(obj)),
        };
    }

    private static List<string> WebSearchQueries(JsonNode? input)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var obj = input as JsonObject;
        var action = obj?["action"] as JsonObject;

        Add(NodeString(action?["query"]));
        if (action?["queries"] is JsonArray queries)
            foreach (var query in queries) Add(NodeString(query));
        if (result.Count == 0) Add(NodeString(obj?["query"]));
        return result;

        void Add(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value) && seen.Add(value)) result.Add(value);
        }
    }

    private static string? NodeString(JsonNode? node)
    {
        try { return node?.GetValue<string>(); }
        catch { return null; }
    }

    private static string JoinNonEmpty(string? first, string? second, string separator = "\n")
    {
        if (string.IsNullOrWhiteSpace(first)) return second ?? "";
        if (string.IsNullOrWhiteSpace(second)) return first;
        return first + separator + second;
    }

    internal static string Trunc(string s, int max) => s.Length <= max ? s : s[..max] + $"\n… (+{s.Length - max} chars)";
}

/// <summary>
/// A presentation-only run of consecutive shell calls, file lookups, edits, or web/MCP activity. The real <see cref="ToolItem"/> instances stay
/// separate so each id still receives its own result/error and compact mode can expose a second-level detail row.
/// With compact mode off the template renders <see cref="Tools"/> as ordinary cards, preserving the full feed.
/// </summary>
public sealed class CompactToolGroupItem : ItemVm
{
    public const string BashKind = "bash";
    public const string BrowseKind = "browse";
    public const string EditKind = "edit";
    public const string WebKind = "web";

    private bool _expanded;
    private bool _compactEnabled;

    public CompactToolGroupItem(string kind, bool compactEnabled)
    {
        Kind = kind;
        _compactEnabled = compactEnabled;
    }

    public string Kind { get; }
    public ObservableCollection<ToolItem> Tools { get; } = new();
    public bool Expanded { get => _expanded; set => Set(ref _expanded, value); }
    public bool CompactEnabled
    {
        get => _compactEnabled;
        set
        {
            if (Set(ref _compactEnabled, value)) Raise(nameof(UseCompactDisplay));
        }
    }

    /// <summary>Shell activity compacts immediately; other activity uses compact chrome once it forms a real group.</summary>
    public bool UseCompactDisplay => CompactToolPresentation.ShouldUseCompactDisplay(_compactEnabled, Kind, Tools.Count);
    public string Name => Kind switch
    {
        // Bash and PowerShell share this group; only label it PowerShell when every call in it is one.
        BashKind => Tools.Count > 0 && Tools.All(t => t.Name == "PowerShell") ? "PowerShell" : "Bash",
        BrowseKind => Tools.Count > 0 && Tools.All(t => t.Name == "Read") ? "Read" : "Files",
        EditKind => "Edit",
        WebKind => "Web / MCP",
        _ => "Activity",
    };
    public string Icon => Kind switch
    {
        BashKind => "",
        BrowseKind => "",
        EditKind => "",
        WebKind => "",
        _ => "",
    };
    public string CountText => Kind switch
    {
        BashKind => $"{Tools.Count} command{(Tools.Count == 1 ? "" : "s")}",
        BrowseKind => Tools.Count > 0 && Tools.All(t => t.Name == "Read")
            ? $"{Tools.Count} file{(Tools.Count == 1 ? "" : "s")}"
            : $"{Tools.Count} lookup{(Tools.Count == 1 ? "" : "s")}",
        EditKind => $"{Tools.Count} edit{(Tools.Count == 1 ? "" : "s")}",
        WebKind => $"{Tools.Count} action{(Tools.Count == 1 ? "" : "s")}",
        _ => $"{Tools.Count} item{(Tools.Count == 1 ? "" : "s")}",
    };
    public string Status => Tools.Any(t => t.IsRunning) ? "running"
        : Tools.Any(t => t.Status == "error" || t.IsError) ? "error"
        : "done";
    public bool IsError => Tools.Any(t => t.Status == "error" || t.IsError);
    public int Added => Tools.Sum(t => t.Added);
    public int Removed => Tools.Sum(t => t.Removed);
    public bool HasDiffStat => Kind == EditKind && (Added > 0 || Removed > 0);

    public void Add(ToolItem tool)
    {
        Tools.Add(tool);
        tool.PropertyChanged += OnToolChanged;
        RaiseGroupState();
        Raise(nameof(UseCompactDisplay));
        Raise(nameof(CountText));
        Raise(nameof(Name));
    }

    private void OnToolChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ToolItem.Status) or nameof(ToolItem.IsRunning) or nameof(ToolItem.IsError)
            or nameof(ToolItem.Added) or nameof(ToolItem.Removed) or nameof(ToolItem.HasDiffStat))
        {
            RaiseGroupState();
            // A failing child does NOT open the group - see the tool_result handler in ChatViewModel for why.
            // A group is the worst case for it: one rejected edit used to unroll every diff in the group, so the
            // more work the turn had done, the more the failure buried it.
        }
    }

    private void RaiseGroupState()
    {
        Raise(nameof(Status));
        Raise(nameof(IsError));
        Raise(nameof(Added));
        Raise(nameof(Removed));
        Raise(nameof(HasDiffStat));
    }

    public static string? KindFor(string toolName) => toolName switch
    {
        "Bash" or "PowerShell" => BashKind,
        "Read" or "Grep" or "Glob" or "LS" => BrowseKind,
        "Write" or "Edit" or "MultiEdit" or "NotebookEdit" or "CodexEdit" => EditKind,
        "WebSearch" or "WebFetch" => WebKind,
        _ when toolName.StartsWith("mcp__", StringComparison.Ordinal) => WebKind,
        _ => null,
    };
}

public sealed class DividerItem : ItemVm
{
    public required string Label { get; init; }
}

public sealed class BannerItem : ItemVm
{
    public required string Level { get; init; } // error | warn | info | auth
    public required string Text { get; init; }
    public bool IsAuth => Level == "auth";
    /// <summary>Segoe Fluent Icons glyph per level.</summary>
    public string Glyph => Level switch { "error" or "auth" => "", "warn" => "", _ => "" };
}

public sealed class QuestionOption : Observable
{
    private bool _selected;
    public required string Label { get; init; }
    public string? Description { get; init; }
    public bool Selected { get => _selected; set => Set(ref _selected, value); }
}

public sealed class QuestionEntry
{
    public required string Question { get; init; }
    public bool MultiSelect { get; init; }
    public ObservableCollection<QuestionOption> Options { get; } = new();
    public string Custom { get; set; } = "";
}

public sealed class PermItem : ItemVm
{
    public required string RequestId { get; init; }
    public required string ToolName { get; init; }
    public JsonNode? Input { get; init; }
    public JsonNode? Suggestions { get; init; }
    public string? ToolUseId { get; init; }

    /// <summary>The chat that raised this request. Respond through THIS, never the globally-active chat: a permission
    /// card living in a bridge pane must answer the pane that owns it, or the reply no-ops against the wrong VM's
    /// pending-request table and the buttons appear dead.</summary>
    public ChatViewModel? Owner { get; init; }

    private string _state = "pending"; // pending | allow | deny | cancelled
    public string State { get => _state; set { if (Set(ref _state, value)) { Raise(nameof(IsPending)); Raise(nameof(DecisionText)); } } }
    public bool IsPending => _state == "pending";
    public string DecisionText => _state switch { "allow" => "allowed", "deny" => "denied", "cancelled" => "cancelled", _ => "" };

    public string DenyNote { get; set; } = "";
    public string Kind => ToolName switch { "AskUserQuestion" => "question", "ExitPlanMode" => "plan", _ => "perm" };

    // perm body
    public string BodyText => ToolName switch
    {
        "Bash" or "PowerShell" => Input?["command"]?.GetValue<string>() ?? "",
        "Write" => ToolItem.Trunc(Input?["content"]?.GetValue<string>() ?? "", 2500),
        _ => ToolItem.Trunc(Input?.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) ?? "", 2500),
    };
    public bool IsEdit => ToolName is "Edit" or "MultiEdit";
    public ObservableCollection<DiffLine> DiffLines { get; } = new();
    public string Summary
    {
        get
        {
            var i = Input;
            return ToolName switch
            {
                "Bash" or "PowerShell" => i?["description"]?.GetValue<string>() ?? "",
                "Edit" or "MultiEdit" or "Write" or "Read" => i?["file_path"]?.GetValue<string>() ?? "",
                _ => "",
            };
        }
    }

    // question body
    public List<QuestionEntry> Questions { get; } = new();
    // plan body
    public string PlanMarkdown => Input?["plan"]?.GetValue<string>() ?? "";
}
