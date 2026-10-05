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
        Items.Add(new BannerItem { Level = "info", Text = "Use /goal followed by the goal you want completed." });
        ItemsChanged?.Invoke();
        return false;
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

        // A model may remember the check protocol when answering a later user message. Hide our status line
        // there too, but only an app-generated check can settle the goal. Raw text stays intact for reconciliation.
        foreach (var item in Items.OfType<TextItem>().Reverse())
        {
            if (!GoalPolicy.EndsWithMarker(item.Text, marker)) continue;
            item.HideGoalStatus(marker);
            break;
        }
        if (!turn.IsCheck) return;
        Goal = turn.Goal with { Completed = marker == complete, Paused = marker == waiting };
        CancelGoalCheck();
    }

    private void PauseGoal()
    {
        CancelGoalCheck();
        if (Goal is { Completed: false, Paused: false } active) Goal = active with { Paused = true };
    }

    private bool CanCheckGoal() => Goal is { Completed: false, Paused: false }
        && _status == "idle" && _session is { HasExited: false }
        && !HasPendingDispatch && string.IsNullOrWhiteSpace(Draft) && Attachments.Count == 0
        && !RewindHoldsDispatch && !_steerSubmitting && !_steerQueueHold
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
