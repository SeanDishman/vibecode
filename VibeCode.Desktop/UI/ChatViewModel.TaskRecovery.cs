using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using VibeCode.AgentStatus.Mcp.Contracts;
using VibeCode.Services;

namespace VibeCode.UI;

public sealed partial class ChatViewModel
{
    private TaskCheckpoint? _taskCheckpoint;
    private bool _taskRecoveryLoaded;
    private bool _taskRecoveryPending;
    private string? _taskRecoveryContext;
    private bool _taskRecoveryShown;
    private bool _checkpointTurnActive;
    private bool _checkpointSaveFailed;
    private DispatcherTimer? _checkpointTimer;
    private readonly HashSet<TodoEntry> _checkpointTodos = new();
    public TaskCheckpoint? RecoveryCheckpoint => _taskCheckpoint;

    private static string RecoveryExcerpt(string text, int limit) => text.Length <= limit ? text : text[..limit] + " [truncated]";

    private async Task RestoreTaskRecoveryAndStartAsync(int version, string id)
    {
        TaskCheckpoint? checkpoint = null;
        string? error = null;
        try { checkpoint = await Task.Run(() => TaskCheckpointStore.Load(Provider, id, Cwd)); }
        catch (Exception ex) { error = ex.Message; }
        if (version != Volatile.Read(ref _startVersion) || Status == "closed") return;
        _taskRecoveryLoaded = true;
        _taskCheckpoint = checkpoint;
        if (checkpoint is { Explicit: true }) _lastCheckpointAt = checkpoint.SavedAt;
        _taskRecoveryPending = checkpoint is not null;
        _taskRecoveryContext = checkpoint?.Context();
        if (error is not null) Items.Add(new BannerItem { Level = "warn", Text = error + " The provider transcript is still available." });
        StartWithTranscript(version);
    }

    private void ShowTaskRecovery()
    {
        if (_taskRecoveryShown || !_taskRecoveryPending || _taskCheckpoint is not { } checkpoint) return;
        _taskRecoveryShown = true;
        var lines = new List<string> { $"Task checkpoint restored · {checkpoint.SavedAt.LocalDateTime:g}", checkpoint.Objective };
        if (checkpoint.RecoveredFromBackup) lines.Add("Recovered the previous saved checkpoint. Newer progress may be missing.");
        if (checkpoint.Completed.Length > 0) lines.Add("Reported complete: " + string.Join("; ", checkpoint.Completed));
        if (checkpoint.Remaining.Length > 0) lines.Add("Remaining: " + string.Join("; ", checkpoint.Remaining));
        if (checkpoint.Constraints.Length > 0) lines.Add("Constraints: " + string.Join("; ", checkpoint.Constraints));
        // A PreCompact snapshot is taken mid-turn, so a session that never got further was interrupted too.
        if (checkpoint.TurnState is "running" or "interrupted" or "pre_compact") lines.Add("The previous turn was interrupted. Check existing work before retrying actions.");
        Items.Add(new BannerItem { Level = "info", Text = string.Join("\n\n", lines) });
        ItemsChanged?.Invoke();
    }

    private void StageTaskRecovery()
    {
        _taskRecoveryContext = _taskCheckpoint?.Context();
        _taskRecoveryPending = _taskRecoveryContext is not null;
    }

    private void FinishTaskProgress(string state)
    {
        if (!_checkpointTurnActive) return;
        SaveTaskProgress(state, response: LastTurnReplyText());
        _checkpointTurnActive = false;
        if (state == "interrupted") StageTaskRecovery();
        else ArmMemoryReviewIdle();
    }

    internal JsonObject SaveTaskCheckpoint(JsonObject args)
    {
        if (SessionId is null) throw new StatusValidationException("Wait for this chat's conversation ID before saving a checkpoint.");
        string[] Rows(string key) => (args[key] as JsonArray)?.Select(n => n!.GetValue<string>()).ToArray() ?? [];
        var checkpoint = (_taskCheckpoint ?? new TaskCheckpoint()) with
        {
            Provider = Provider, SessionId = SessionId, Cwd = Cwd, SavedAt = DateTimeOffset.UtcNow,
            Objective = args["objective"]!.GetValue<string>(), Completed = Rows("completed"), Remaining = Rows("remaining"),
            Constraints = Rows("constraints"), Decisions = Rows("decisions"), Evidence = Rows("evidence"), Explicit = true,
            TurnState = _checkpointTurnActive ? "running" : "turn_finished", RecoveredFromBackup = false,
        };
        try { TaskCheckpointStore.Save(checkpoint); }
        catch (Exception ex) { throw new StatusValidationException("Checkpoint not saved; the previous saved revision is retained. " + ex.Message); }
        _taskCheckpoint = checkpoint;
        _checkpointSaveFailed = false;
        if (_taskRecoveryPending) StageTaskRecovery();
        Raise(nameof(RecoveryCheckpoint));
        NoteCheckpointSaved(checkpoint.SavedAt);
        return new JsonObject { ["saved"] = true, ["saved_at"] = checkpoint.SavedAt.ToString("O"),
            ["scope"] = "current_chat", ["note"] = "Progress is recorded as reported. This does not verify completion or start another turn." };
    }

    private void SaveTaskProgress(string state, string? request = null, string? response = null)
    {
        _checkpointTimer?.Stop();
        if (request is not null) CancelMemoryReviewIdle();   // a new prompt: the chat is not idle any more
        if (_taskCheckpoint is null && request is null) return;
        var previous = _taskCheckpoint ?? new TaskCheckpoint { Objective = RecoveryExcerpt(request!, 2000) };
        var requests = request is null ? previous.RecentRequests : previous.RecentRequests.Append(RecoveryExcerpt(request, 4000)).TakeLast(4).ToArray();
        var checkpoint = previous with
        {
            Provider = Provider, SessionId = SessionId ?? "", Cwd = Cwd, SavedAt = DateTimeOffset.UtcNow, TurnState = state,
            RecoveredFromBackup = false,
            RecentRequests = requests, LastResponse = response is null ? previous.LastResponse : RecoveryExcerpt(response, 4000),
            Completed = previous.Explicit ? previous.Completed : Todos.Where(t => t.Done).Take(20).Select(t => RecoveryExcerpt(t.Text, 260)).ToArray(),
            Remaining = previous.Explicit ? previous.Remaining : Todos.Where(t => !t.Done).Take(20).Select(t => RecoveryExcerpt(t.Text, 260)).ToArray(),
            ExcerptsTruncated = previous.ExcerptsTruncated || request?.Length > 4000 || response?.Length > 4000 || previous.RecentRequests.Length >= 4,
        };
        // Retain a first prompt until the provider assigns its session ID, then persist from init.
        if (SessionId is null) { _taskCheckpoint = checkpoint; return; }
        try
        {
            TaskCheckpointStore.Save(checkpoint);
            _taskCheckpoint = checkpoint;
            _checkpointSaveFailed = false;
        }
        catch (Exception ex)
        {
            if (!_checkpointSaveFailed)
                Items.Add(new BannerItem { Level = "warn", Text = "Latest task checkpoint was not saved. " + ex.Message });
            _checkpointSaveFailed = true;
        }
    }

    private void OnRecoveryTodosChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var old in _checkpointTodos.Where(t => !Todos.Contains(t)).ToArray())
        {
            old.PropertyChanged -= OnRecoveryTodoChanged;
            _checkpointTodos.Remove(old);
        }
        foreach (var item in Todos.Where(t => !_checkpointTodos.Contains(t)))
        {
            _checkpointTodos.Add(item);
            item.PropertyChanged += OnRecoveryTodoChanged;
        }
        QueueTaskCheckpoint();
    }
    private void OnRecoveryTodoChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TodoEntry.Status) or nameof(TodoEntry.Text)) QueueTaskCheckpoint();
    }
    private void QueueTaskCheckpoint()
    {
        if (!_checkpointTurnActive || IsLoadingHistory) return;
        if (_checkpointTimer is null)
        {
            _checkpointTimer = new DispatcherTimer(DispatcherPriority.Background, _ui) { Interval = TimeSpan.FromSeconds(1) };
            _checkpointTimer.Tick += (_, _) => SaveTaskProgress("running");
        }
        if (!_checkpointTimer.IsEnabled) _checkpointTimer.Start();
    }
}
