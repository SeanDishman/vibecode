using System.Windows.Threading;
using VibeCode.Services;

namespace VibeCode.UI;

public sealed partial class ChatViewModel
{
    private ChatGoal? _goal;
    public ChatGoal? Goal
    {
        get => _goal;
        private set => Set(ref _goal, value);
    }

    private sealed record GoalTurn(ChatGoal Goal, bool IsCheck);
    private GoalTurn? _activeGoalTurn;
    private DispatcherTimer? _goalCheckTimer;

    public void RestoreGoal(ChatGoal? goal)
    {
        CancelGoalCheck();
        Goal = GoalPolicy.Normalize(goal);
        MaybeScheduleGoalCheck();
    }

    private bool ValidateGoalCommand(string text)
    {
        if (!GoalPolicy.TryParseCommand(text, out var goal) || goal.Length > 0) return true;
        ShowGoalNotice(Goal is null ? GoalStatusText()
            : GoalStatusText() + "\nUse /goal followed by a new goal to replace it, or /goal pause, /goal resume or /goal clear.");
        return false;
    }

    /// <summary>/goal status, pause, resume and clear manage the current goal without starting a turn, even while one

    private bool ApplyGoalControl(string text, GoalControl control, IReadOnlyList<Attachment>? attachments)
    {
        if (attachments is { Count: > 0 })
        {
            ShowGoalNotice($"Remove attachments before using {text.Trim()}.");
            return false;
        }
        _promptHistory.Record(text);
        switch (control)
        {
            case GoalControl.Pause when Goal is { Completed: false, Paused: false }:
                PauseGoal();
                ShowGoalNotice("Goal paused. Send a message or use /goal resume to continue.");
                break;
            case GoalControl.Resume when Goal is { Completed: false, Paused: true } paused:
                Goal = paused with { Paused = false };
                ShowGoalNotice("Goal resumed.");
                // Like a sent message, resuming retries a failed turn instead of waiting behind its error state.
                if (_status == "error")
                {
                    CancelBridgeErrorRecovery();
                    CancelLimitRecovery();
                    Status = "idle";
                }
                else MaybeScheduleGoalCheck();
                break;
            case GoalControl.Clear when Goal is not null:
                CancelGoalCheck();
                Goal = null;
                ShowGoalNotice("Goal cleared.");
                break;
            default:
                ShowGoalNotice(GoalStatusText());
                break;
        }
        return true;
    }

    private string GoalStatusText() => Goal switch
    {
        null => "Use /goal followed by the goal you want completed.",
        { Completed: true } done => "Goal completed: " + done.Text,
        { Paused: true } paused => "Goal paused: " + paused.Text,
        var active => "Goal active: " + active.Text,
    };

    private void ShowGoalNotice(string text)
    {
        InsertBeforeQueued(new BannerItem { Level = "info", Text = text });
        ItemsChanged?.Invoke();
    }

    private GoalTurn? BeginGoalTurn(string text, bool goalCheck)
    {
        CancelGoalCheck();
        if (!goalCheck && GoalPolicy.TryParseCommand(text, out var goal) && goal.Length > 0)
            Goal = new ChatGoal(Guid.NewGuid().ToString("N"), goal);
        else if (!IsSystemInjectedPrompt(text) && Goal is { Completed: false, Paused: true } paused)
            Goal = paused with { Paused = false };

        return _activeGoalTurn = Goal is { Completed: false, Paused: false } active
            ? new GoalTurn(active, goalCheck) : null;
    }

    private void FinishGoalTurn(bool stopped)
    {
        var turn = _activeGoalTurn;
        _activeGoalTurn = null;
        if (turn is null || Goal?.Id != turn.Goal.Id) return;
        if (stopped)
        {
            PauseGoal();
            return;
        }
        var reply = LastTurnReplyText();
        var complete = GoalPolicy.CompleteMarker(turn.Goal);
        var waiting = GoalPolicy.WaitingMarker(turn.Goal);
        var marker = GoalPolicy.EndsWithMarker(reply, complete) ? complete
            : GoalPolicy.EndsWithMarker(reply, waiting) ? waiting : null;
        if (marker is null) return;

        // Every active work turn carries this goal's protocol. Honor its terminal signal immediately,
        // including after a user answer, instead of sending another request for already completed work.
        // Raw text stays intact for transcript reconciliation.
        foreach (var item in Items.OfType<TextItem>().Reverse())
        {
            if (!GoalPolicy.EndsWithMarker(item.Text, marker)) continue;
            item.HideGoalStatus(marker);
            break;
        }
        Goal = turn.Goal with { Completed = marker == complete, Paused = marker == waiting };
        CancelGoalCheck();
        if (Goal.Completed)
            Items.Add(new BannerItem { Level = "info", Text = "Goal completed: " + Goal.Text });
    }

    private void PauseGoal()
    {
        CancelGoalCheck();
        if (Goal is { Completed: false, Paused: false } active) Goal = active with { Paused = true };
    }

    private bool CanCheckGoal() => Goal is { Completed: false, Paused: false }
        && _status == "idle" && _session is { HasExited: false }
        && !HasPendingDispatch && string.IsNullOrWhiteSpace(Draft) && Attachments.Count == 0
        && !ModelRestartHoldsDispatch && !_steerSubmitting && !_steerQueueHold
        && !ExtendedQueuePaused && _pendingPerms.Count == 0;

    private void MaybeScheduleGoalCheck()
    {
        if (!CanCheckGoal() || _goalCheckTimer is not null) return;
        var goalId = Goal!.Id;
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        timer.Tick += (_, _) =>
        {
            CancelGoalCheck();
            // Recheck at dispatch: a user message, edited draft, closed tab, or replacement goal always wins.
            if (!CanCheckGoal() || Goal!.Id != goalId) return;
            var goal = Goal!;
            SendNow(GoalPolicy.BuildCheck(goal), null, goalCheck: true);
        };
        _goalCheckTimer = timer;
        timer.Start();
    }

    private void CancelGoalCheck()
    {
        _goalCheckTimer?.Stop();
        _goalCheckTimer = null;
    }
}
