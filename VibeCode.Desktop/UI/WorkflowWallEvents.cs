using System.Windows;

namespace VibeCode.UI;

public enum WallEventKind { Started, Finished, RunCompleted, NeedsYou, Failed }

/// <summary>Why a finish counts as one: its Bridge task was reported complete, its todo list was ticked off, or the
/// central planner delivered its assignments.</summary>
public enum WallFinish { None, Task, Plan, Planner }

/// <summary>One fleet transition between two consecutive wall snapshots. A run completion names every agent of the run
/// on the wall in <paramref name="Team"/>.</summary>
public sealed record WallEvent(WallEventKind Kind, AgentActivityNode Agent, WallFinish Finish = WallFinish.None,
    IReadOnlyList<AgentActivityNode>? Team = null);

/// <summary>
/// What changed on the wall since its last snapshot, for the celebrations, the earcons and the day recap. It diffs the
/// window's own one-second snapshots - nothing is read from disk or a provider - so it only knows what happened while
/// the wall was on screen. After a gap (the wall hidden or minimized) the next snapshot only becomes the new baseline,
/// so coming back to the wall never replays everything that changed meanwhile as a burst of finishes.
///
/// A finish is a real one: a Bridge task flipping to Completed, a todo list reaching all done, or the central planner
/// delivering. A turn that merely ends, an error and a user's Stop (a paused Bridge agent) never count.
/// </summary>
public sealed class WorkflowWallEvents
{
    public static readonly TimeSpan MaxGap = TimeSpan.FromSeconds(5);
    private Dictionary<string, AgentActivityNode> _previous = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _runsDone = new(StringComparer.Ordinal);
    private readonly Dictionary<DateTime, (string Label, TimeSpan Length)> _longest = new();
    private DateTime _seenUtc = DateTime.MinValue;

    /// <summary>The longest turn that ended on each local day while the wall was open, and whose it was.</summary>
    public IReadOnlyDictionary<DateTime, (string Label, TimeSpan Length)> LongestTurns => _longest;

    public IReadOnlyList<WallEvent> Observe(IReadOnlyList<AgentActivityProject> projects, DateTime nowUtc)
    {
        var current = new Dictionary<string, AgentActivityNode>(StringComparer.Ordinal);
        foreach (var project in projects)
            foreach (var agent in project.Agents) current.TryAdd(agent.Id, agent);
        var baseline = _seenUtc == DateTime.MinValue || nowUtc - _seenUtc > MaxGap || nowUtc < _seenUtc;
        var events = new List<WallEvent>();
        foreach (var agent in current.Values)
        {
            var before = _previous.GetValueOrDefault(agent.Id);
            if (baseline) continue;
            // Only a turn seen ending counts: across a gap it may have ended long before this snapshot.
            if (before is { State: AgentActivityState.Working, WorkingSinceUtc: { } since } && agent.State != AgentActivityState.Working)
                NoteTurn(before.Label, nowUtc - since, nowUtc);
            if (agent.State == AgentActivityState.Working && before?.State != AgentActivityState.Working)
                events.Add(new(WallEventKind.Started, agent));
            if (NeedsYou(agent) && (before is null || !NeedsYou(before)))
                events.Add(new(WallEventKind.NeedsYou, agent));
            if (agent.State == AgentActivityState.Error && before?.State != AgentActivityState.Error)
                events.Add(new(WallEventKind.Failed, agent));
            if (before is not null && FinishOf(before, agent) is var finish and not WallFinish.None)
                events.Add(new(WallEventKind.Finished, agent, finish));
        }
        var runs = Runs(current.Values).ToList();
        foreach (var (run, team) in runs)
        {
            var done = RunDone(team);
            if (!baseline && done && _runsDone.TryGetValue(run, out var was) && !was)
                events.Add(new(WallEventKind.RunCompleted, team[0], Team: team));
            _runsDone[run] = done;
        }
        // Runs that left the wall are forgotten once there are enough of them to matter.
        if (_runsDone.Count > 64)
            foreach (var run in _runsDone.Keys.Where(k => runs.All(r => r.Run != k)).ToArray()) _runsDone.Remove(run);
        _previous = current;
        _seenUtc = nowUtc;
        return events;
    }

    private void NoteTurn(string label, TimeSpan length, DateTime nowUtc)
    {
        if (length <= TimeSpan.Zero) return;
        var day = nowUtc.ToLocalTime().Date;
        if (!_longest.TryGetValue(day, out var best) || length > best.Length) _longest[day] = (label, length);
        if (_longest.Count > 400) _longest.Remove(_longest.Keys.Min());
    }

    /// <summary>How <paramref name="after"/> finished since <paramref name="before"/>, if it really did.</summary>
    public static WallFinish FinishOf(AgentActivityNode before, AgentActivityNode after)
    {
        // An error or a Stop is never a finish, whatever the todo list says.
        if (after.State is AgentActivityState.Error or AgentActivityState.Paused) return WallFinish.None;
        var completed = after.Status == "Completed" && before.Status != "Completed";
        if (completed) return after.IsCentralPlanner ? WallFinish.Planner : WallFinish.Task;
        return before.PlanTotal > 0 && before.PlanDone < before.PlanTotal && after.PlanTotal > 0 && after.PlanDone >= after.PlanTotal
            && after.State != AgentActivityState.Waiting ? WallFinish.Plan : WallFinish.None;
    }

    /// <summary>An agent at the door for you: an approval, a sign-in or a question. A usage limit or a Bridge retry
    /// waits on the clock instead, as on the office floor.</summary>
    public static bool NeedsYou(AgentActivityNode agent) =>
        agent.State == AgentActivityState.Waiting && agent.Status is "Approval needed" or "Sign-in needed"
        || agent.State == AgentActivityState.Working && agent.Now?.Glyph == WorkflowActivity.ApprovalGlyph;

    /// <summary>The Bridge run an agent belongs to: its planned run, or for a Bridge without a plan the Bridge agents of
    /// its project. Plain chats belong to none.</summary>
    public static string? RunKey(AgentActivityNode agent) =>
        agent.Chat.BridgeWork?.RunId is { Length: > 0 } run ? "run:" + run
        : agent.Chat.IsBridgeAgent || agent.IsCentralPlanner ? "bridge:" + agent.ProjectKey : null;

    private static IEnumerable<(string Run, List<AgentActivityNode> Team)> Runs(IEnumerable<AgentActivityNode> agents) =>
        agents.Select(a => (Run: RunKey(a), Agent: a)).Where(x => x.Run is not null)
            .GroupBy(x => x.Run!, StringComparer.Ordinal).Select(g => (g.Key, g.Select(x => x.Agent).ToList()));

    /// <summary>A planned run is done when every task in it is; an unplanned Bridge when every agent on the wall reported
    /// its task complete.</summary>
    public static bool RunDone(IReadOnlyList<AgentActivityNode> team)
    {
        var tasks = team.Select(a => a.Chat.BridgeWork).FirstOrDefault(w => w is { Tasks.Count: > 0 })?.Tasks;
        if (tasks is not null) return tasks.All(t => t.State == "completed");
        return team.Count > 0 && team.All(a => a.Status == "Completed");
    }
}

/// <summary>Which finishes get a moment on the wall (Settings › Appearance › Celebrations).</summary>
public static class WorkflowCelebrations
{
    public const string Off = "off", Occasional = "occasional", Every = "every";
    /// <summary>How long a moment lasts. Long enough to catch from across the room, short enough never to linger.</summary>
    public const double Seconds = 2.6;

    public static string Normalize(string? mode) => mode?.Trim().ToLowerInvariant() switch
    {
        Off => Off, Every => Every, _ => Occasional,
    };

    /// <summary>Whether a finish is celebrated. Occasional is Asana's "sometimes": roughly one finish in three
    /// (<paramref name="roll"/> is uniform in 0..1), but always when a whole Bridge run completes.</summary>
    public static bool ShouldCelebrate(string? mode, bool wholeRun, double roll) => Normalize(mode) switch
    {
        Off => false,
        Every => true,
        _ => wholeRun || roll < 1 / 3.0,
    };

    /// <summary>The wall's own off switches for motion, the same ones every drawn view honours. A celebration is motion,
    /// so with any of them off there is none.</summary>
    public static bool MotionAllowed(FrameworkElement view, bool motionEnabled) =>
        view.IsLoaded && view.IsVisible && motionEnabled && HudMotion.Enabled && SystemParameters.ClientAreaAnimation
        && Window.GetWindow(view)?.WindowState != WindowState.Minimized;
}

/// <summary>A wall view that can play a finish over one agent.</summary>
public interface IWorkflowCelebrating
{
    /// <summary>Play the moment over <paramref name="agent"/>; <paramref name="wholeRun"/> marks the bigger one for a
    /// whole Bridge run. Does nothing while motion is off or the agent is not drawn.</summary>
    void Celebrate(AgentActivityNode agent, bool wholeRun);
}
