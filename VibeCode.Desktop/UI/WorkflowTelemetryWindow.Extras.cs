using System.Windows;
using System.Windows.Controls;
using VibeCode.Services;

namespace VibeCode.UI;

// The wall's extras, all hanging off the snapshot it already takes every second: finish celebrations, earcons, the
// town's milestones and the Town Crier's notice board.
public partial class WorkflowTelemetryWindow
{
    private const int MaxRunCelebrations = 8;
    private readonly WorkflowWallEvents _events = new();
    private WorkflowWallSounds? _sounds;
    private WorkflowRecapPanel? _recap;

    /// <summary>The notice board, once it has been opened. Exposed for verification.</summary>
    public WorkflowRecapPanel? Recap => _recap;

    /// <summary>Diff the newest snapshot against the last one and let the views celebrate, the earcons sound and the
    /// town note what it saw. Only called while the wall is on screen.</summary>
    private void ObserveFleet(DateTime nowUtc)
    {
        WorkflowTown.Instance.Track(_projects, nowUtc);
        var events = _events.Observe(_projects, nowUtc);
        if (events.Count == 0) return;
        Celebrate(events);
        if (AppSettings.Current.WorkflowWallSounds) (_sounds ??= new WorkflowWallSounds()).Offer(events);
        else { _sounds?.Dispose(); _sounds = null; }
    }

    /// <summary>Hand the active view its moments: everyone in a run that just completed, and the finishes the
    /// Celebrations setting picks (all of them, or about one in three).</summary>
    private void Celebrate(IReadOnlyList<WallEvent> events)
    {
        var mode = AppSettings.Current.WorkflowCelebrations;
        if (ActiveView is not IWorkflowCelebrating view || WorkflowCelebrations.Normalize(mode) == WorkflowCelebrations.Off) return;
        var celebrated = new HashSet<string>(StringComparer.Ordinal);
        foreach (var run in events.Where(e => e.Kind == WallEventKind.RunCompleted))
            foreach (var agent in (run.Team ?? new[] { run.Agent }).Take(MaxRunCelebrations))
                if (celebrated.Add(agent.Id)) view.Celebrate(agent, wholeRun: true);
        foreach (var finish in events.Where(e => e.Kind == WallEventKind.Finished))
            if (!celebrated.Contains(finish.Agent.Id) && WorkflowCelebrations.ShouldCelebrate(mode, false, Random.Shared.NextDouble()))
            {
                celebrated.Add(finish.Agent.Id);
                view.Celebrate(finish.Agent, wholeRun: false);
            }
    }

    private void OnNoticeBoard(object sender, RoutedEventArgs e)
    {
        if (_recap is { IsOpen: true }) { _recap.Close(); return; }
        if (_recap is null)
        {
            _recap = new WorkflowRecapPanel { PostcardSource = MapFrame, Source = BuildRecap };
            ((Grid)MapFrame.Child).Children.Add(_recap);
        }
        _recap.Open(RecapLook());
    }

    /// <summary>A parchment scroll in the tavern, a whiteboard memo in the offices, a card everywhere else.</summary>
    private WorkflowRecapPanel.Look RecapLook() => _view switch
    {
        WorkflowViews.Tavern => WorkflowRecapPanel.Look.Scroll,
        WorkflowViews.Office or WorkflowViews.Floor => WorkflowRecapPanel.Look.Whiteboard,
        _ => WorkflowRecapPanel.Look.Card,
    };

    private void RestyleRecap()
    {
        if (_recap is { IsOpen: true }) _recap.ApplyLook(RecapLook());
    }

    /// <summary>Gather the notice off the UI thread. What only the wall knows - who came to the door, the longest turn
    /// it saw end - is read here first.</summary>
    private Task<WorkflowRecap> BuildRecap(RecapPeriod period)
    {
        var now = DateTime.Now;
        var (from, to) = WorkflowRecap.Range(period, now);
        var doors = _history.DoorVisits(from, to);
        (string, TimeSpan)? longest = null;
        foreach (var (day, turn) in _events.LongestTurns)
            if (day >= from.Date && day <= to.Date && (longest is null || turn.Length > longest.Value.Item2)) longest = turn;
        var directories = _projects.Where(p => p.Directory.Length > 0).Select(p => p.Directory).ToArray();
        return Task.Run(() => WorkflowRecap.Build(period, now, UsageLog.Instance.Entries(),
            directory => BridgeEditLedger.Read(directory, DateTimeOffset.Now), doors, longest, directories));
    }

    /// <summary>Escape folds the notice away before it closes the wall.</summary>
    private bool CloseRecap()
    {
        if (_recap is not { IsOpen: true }) return false;
        _recap.Close();
        return true;
    }

    private void CloseExtras()
    {
        _sounds?.Dispose();
        _sounds = null;
    }
}
