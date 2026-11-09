using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;

namespace VibeCode.Services;

/// <summary>
/// Where the seconds after launch go. The StartupPhase notes covered ~1.2 s of the 4-15 s before StartupReady; this also
/// times RestoreSession, the chat history scan, every restored chat's transcript replay (messages, disk load and UI
/// time) and each Bridge reconnect, and a heartbeat thread records any UI-thread stall over 250 ms, tagged with whatever
/// was running. A minute after the workspace is ready the timeline goes to the crash log as one StartupTimeline note and
/// to startup-timeline.json, which Settings > About draws as a waterfall. Times are milliseconds since process start.
/// </summary>
public static class StartupTimeline
{
    /// <summary>One bar of the waterfall. Kind: phase, bridge, replay, stall or ready.</summary>
    public sealed record Entry(string Kind, string Name, double StartMs, double DurationMs, string? Detail = null);

    /// <summary>A finished startup, as saved and as drawn.</summary>
    public sealed record Timeline(DateTime StartedAt, double ReadyMs, List<Entry> Entries);

    public const double StallThresholdMs = 250;

    /// <summary>How long the heartbeat keeps watching after the workspace is ready.</summary>
    private static readonly TimeSpan WatchAfterReady = TimeSpan.FromMinutes(1);

    /// <summary>Stop regardless if StartupReady never arrives (a hidden smoke run, a startup that failed).</summary>
    private static readonly TimeSpan WatchCap = TimeSpan.FromMinutes(5);

    private const int MaxEntries = 400;

    private static readonly object Gate = new();
    private static readonly List<Entry> Entries = new();
    private static readonly List<string> Running = new();
    private static readonly DateTime StartedAtUtc = ProcessStartUtc();
    private static readonly long Origin = Stopwatch.GetTimestamp()
                                          - (long)((DateTime.UtcNow - StartedAtUtc).TotalSeconds * Stopwatch.Frequency);
    /// <summary>The chat whose replay batch is on the UI thread right now, if any.</summary>
    private static volatile string? _replaying;
    private static string? _lastPhase;
    private static double _readyMs = -1;
    private static int _severeStallsNoted;
    private static Thread? _heartbeat;
    private static Timer? _finishTimer;
    private static Timeline? _finished;

    public static string FilePath => Path.Combine(AppSettings.Dir, "startup-timeline.json");

    /// <summary>True until the watch window closes; after that nothing more is recorded.</summary>
    public static bool Collecting { get; private set; } = true;

    /// <summary>Milliseconds since this process started.</summary>
    public static double NowMs => (Stopwatch.GetTimestamp() - Origin) * 1000.0 / Stopwatch.Frequency;

    /// <summary>Time one step: <c>using (StartupTimeline.Phase("RestoreSession")) ...</c>. While it runs, a stall is
    /// tagged with its name. Works across an <c>await</c>, ending when the awaited work does.</summary>
    public static PhaseScope Phase(string name, string? detail = null) => new(name, detail);

    public readonly struct PhaseScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _detail;
        private readonly double _startMs;

        internal PhaseScope(string name, string? detail)
        {
            _name = name;
            _detail = detail;
            _startMs = NowMs;
            lock (Gate)
            {
                Running.Add(name);
                _lastPhase = name;
            }
        }

        public void Dispose()
        {
            if (_name is null) return;   // default(PhaseScope)
            lock (Gate) Running.Remove(_name);
            Record("phase", _name, _startMs, NowMs - _startMs, _detail);
        }
    }

    /// <summary>Add a bar measured elsewhere (a Bridge reconnect, a phase timed by its own stopwatch).</summary>
    public static void Record(string kind, string name, double startMs, double durationMs, string? detail = null)
    {
        lock (Gate)
            if (Collecting && Entries.Count < MaxEntries)
                Entries.Add(new Entry(kind, name, Math.Round(startMs, 1), Math.Round(durationMs, 1), detail));
    }

    /// <summary>Start timing one chat's transcript replay, or null once the startup window has closed.</summary>
    public static ReplayTimer? BeginReplay(string chat) => Collecting ? new ReplayTimer(chat) : null;

    /// <summary>One transcript replay: the disk load off the UI thread, then the UI-thread ingest batches. While a batch
    /// runs, a stall is attributed to this chat.</summary>
    public sealed class ReplayTimer
    {
        private readonly string _chat;
        private readonly double _startMs = NowMs;
        private double _loadMs;
        private long _uiTicks;
        private int _batches;

        internal ReplayTimer(string chat) => _chat = chat.Length > 48 ? chat[..47] + "…" : chat;

        public void Loaded() => _loadMs = NowMs - _startMs;

        public long BatchStarted()
        {
            _replaying = _chat;
            return Stopwatch.GetTimestamp();
        }

        public void BatchDone(long started)
        {
            _replaying = null;
            _uiTicks += Stopwatch.GetTimestamp() - started;   // batches run on the UI thread, one at a time
            _batches++;
        }

        /// <summary>Record the replay; <paramref name="messages"/> is the transcript size.</summary>
        public void Done(int messages) => Record("replay", _chat, _startMs, NowMs - _startMs,
            $"{messages} messages; disk {_loadMs:0} ms; UI {_uiTicks * 1000.0 / Stopwatch.Frequency:0} ms in {_batches} batches");
    }

    /// <summary>The workspace is ready (the existing StartupReady note). Watch one more minute, then publish.</summary>
    public static void Ready(double onLoadedToReadyMs)
    {
        var now = NowMs;
        lock (Gate)
        {
            if (!Collecting || _readyMs >= 0) return;
            _readyMs = now;
        }
        Record("ready", "StartupReady", now, 0, $"{onLoadedToReadyMs:0} ms after the window loaded");
        _finishTimer = new Timer(_ => Finish(), null, WatchAfterReady, Timeout.InfiniteTimeSpan);
    }

    /// <summary>The main window loaded: record the launch up to here and start watching the UI thread. Only the first
    /// call counts, so a second shell window loading later changes nothing.</summary>
    public static void WindowLoaded(Dispatcher dispatcher)
    {
        lock (Gate)
            if (_heartbeat is not null || !Collecting) return;
        Record("phase", "Launch until the window loaded", 0, NowMs);
        StartHeartbeat(dispatcher);
    }

    /// <summary>Watch the UI thread from a background thread: a ping posted at Input priority that takes longer than
    /// <see cref="StallThresholdMs"/> to run means input would have waited that long. Stops with the watch window.</summary>
    public static void StartHeartbeat(Dispatcher dispatcher)
    {
        lock (Gate)
        {
            if (_heartbeat is not null || !Collecting) return;
            _heartbeat = new Thread(() => Watch(dispatcher)) { IsBackground = true, Name = "VibeCode startup heartbeat" };
        }
        // An exit inside the window still leaves this startup's record behind.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Finish();
        _heartbeat.Start();
    }

    private static void Watch(Dispatcher dispatcher)
    {
        while (Collecting && !dispatcher.HasShutdownStarted)
        {
            if (NowMs > WatchCap.TotalMilliseconds) { Finish(); return; }
            // Not disposed: a ping stuck behind a long stall may still run - and Set it - after this loop moved on. Without
            // a WaitHandle requested it holds no kernel object, so the collector is enough.
            var answered = new ManualResetEventSlim();
            var posted = NowMs;
            try { dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(answered.Set)); }
            catch (InvalidOperationException) { return; }   // dispatcher gone
            if (!answered.Wait(TimeSpan.FromMilliseconds(StallThresholdMs)))
            {
                var during = RunningTag();   // what was running while input waited
                answered.Wait(TimeSpan.FromSeconds(30));
                var stalled = NowMs - posted;
                Record("stall", during, posted, stalled);
                // The summary waits for the window to close; a freeze this long is noted now, in case the run dies first.
                if (stalled >= 1000 && ++_severeStallsNoted <= 5)
                    CrashLog.Note("UiStall", $"The UI thread stopped answering input for {stalled:0} ms at +{posted:0} ms, {during}.");
            }
            Thread.Sleep(100);
        }
    }

    private static string RunningTag()
    {
        lock (Gate)
        {
            var parts = Running.Distinct().ToList();
            if (_replaying is { } chat) parts.Add($"the history replay of {chat}");
            return parts.Count > 0 ? "in " + string.Join(" and ", parts)
                : _lastPhase is { } last ? "between steps, after " + last : "before the first step";
        }
    }

    /// <summary>Close the window: one crash-log note and the file the About page draws.</summary>
    public static void Finish()
    {
        Timeline timeline;
        lock (Gate)
        {
            if (!Collecting) return;
            Collecting = false;
            timeline = new Timeline(StartedAtUtc.ToLocalTime(), _readyMs, Entries.OrderBy(e => e.StartMs).ToList());
            _finished = timeline;
        }
        _finishTimer?.Dispose();
        try
        {
            Directory.CreateDirectory(AppSettings.Dir);
            var tmp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(timeline));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the note below still has it */ }
        CrashLog.Note("StartupTimeline", Describe(timeline));
    }

    /// <summary>The newest finished startup: this run's once its window closed, else the previous run's file.</summary>
    public static Timeline? Latest()
    {
        lock (Gate)
            if (_finished is not null) return _finished;
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<Timeline>(File.ReadAllBytes(FilePath)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>The crash-log text: every phase, the replays and stalls summarized with the worst ones listed, so a
    /// startup costs one bounded note however many chats it restored.</summary>
    public static string Describe(Timeline timeline)
    {
        var text = new StringBuilder();
        text.AppendLine(timeline.ReadyMs >= 0
            ? $"Workspace ready {timeline.ReadyMs:0} ms after the process started ({timeline.StartedAt:HH:mm:ss})."
            : $"The workspace never reported ready (process started {timeline.StartedAt:HH:mm:ss}).");
        foreach (var entry in timeline.Entries.Where(e => e.Kind is "phase" or "bridge" or "ready"))
            text.AppendLine($"  +{entry.StartMs,7:0} ms  {entry.Name,-28} {entry.DurationMs,7:0} ms{(entry.Detail is { } d ? "  " + d : "")}");
        var replays = timeline.Entries.Where(e => e.Kind == "replay").ToList();
        if (replays.Count > 0)
        {
            text.AppendLine($"Transcript replays: {replays.Count}, the last done at +{replays.Max(r => r.StartMs + r.DurationMs):0} ms. Slowest:");
            foreach (var replay in replays.OrderByDescending(r => r.DurationMs).Take(10))
                text.AppendLine($"  +{replay.StartMs,7:0} ms  {replay.Name,-40} {replay.DurationMs,7:0} ms  {replay.Detail}");
        }
        var stalls = timeline.Entries.Where(e => e.Kind == "stall").ToList();
        text.AppendLine(stalls.Count == 0
            ? $"No UI-thread stall over {StallThresholdMs:0} ms."
            : $"UI-thread stalls over {StallThresholdMs:0} ms: {stalls.Count}, {stalls.Sum(s => s.DurationMs):0} ms in all. Worst:");
        foreach (var stall in stalls.OrderByDescending(s => s.DurationMs).Take(10))
            text.AppendLine($"  +{stall.StartMs,7:0} ms  {stall.DurationMs,7:0} ms  {stall.Name}");
        return text.ToString().TrimEnd();
    }

    private static DateTime ProcessStartUtc()
    {
        try { return Process.GetCurrentProcess().StartTime.ToUniversalTime(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return DateTime.UtcNow;
        }
    }
}
