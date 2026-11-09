using System.IO;
using System.Reflection;
using System.Windows.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using VibeCode.Services;

namespace VibeCode.UI;

/// <summary>A family of wall earcons, highest priority last.</summary>
public enum WallSound { Start, Finish, RunComplete, NeedsYou, Fail }

/// <summary>
/// Earcons for the open wall (Settings › Appearance › Workflow wall › Sounds, off by default), after Peep's network auralizer: an
/// agent starting work rises, a real finish falls, a whole Bridge run completing plays the arpeggio, someone at the
/// door asks a question and a failure knocks. They are the bundled chimes from <see cref="NotificationSounds"/>.
///
/// Calm by construction: everything that lands within half a second becomes one cue (the most important of them),
/// each family sounds at most once every twelve seconds, the pitch wanders a few percent so a repeat does not wear
/// thin, and the window only offers events from snapshots taken while it is on screen. Each cue opens its own output
/// device rather than going through NotificationSounds.Play, whose one-sound-at-a-time guard would otherwise let a
/// wall earcon cut off a toast's chime.
///
/// There is no ambient layer (rain, thunder, hearth): the bundled set has no such recordings, and synthesized noise
/// would not be the calm, recorded texture that layer needs.
/// </summary>
public sealed class WorkflowWallSounds : IDisposable
{
    public static readonly TimeSpan MergeWindow = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan FamilyGap = TimeSpan.FromSeconds(12);
    private const float Volume = 0.42f;
    private static readonly Dictionary<WallSound, byte[]?> Clips = new();
    private readonly Gate _gate = new();
    private readonly DispatcherTimer _due = new(DispatcherPriority.Background) { Interval = MergeWindow };
    private bool _disposed;

    public WorkflowWallSounds() => _due.Tick += (_, _) =>
    {
        _due.Stop();
        if (_gate.Take(DateTime.UtcNow) is { } sound) Play(sound, 0.96 + Random.Shared.NextDouble() * 0.08);
        else if (_gate.Waiting) _due.Start();   // the timer came in a hair early: the window is still open
    };

    /// <summary>The bundled chime each family plays.</summary>
    public static string ClipOf(WallSound sound) => sound switch
    {
        WallSound.Start => "11-rise", WallSound.Finish => "12-fall", WallSound.RunComplete => "15-arpeggio",
        WallSound.NeedsYou => "16-question", _ => "08-wood-block",
    };

    /// <summary>The family a wall event sounds as.</summary>
    public static WallSound Of(WallEvent e) => e.Kind switch
    {
        WallEventKind.Started => WallSound.Start, WallEventKind.Finished => WallSound.Finish,
        WallEventKind.RunCompleted => WallSound.RunComplete, WallEventKind.NeedsYou => WallSound.NeedsYou, _ => WallSound.Fail,
    };

    /// <summary>Queue the events of one snapshot; the cue plays when the merge window closes.</summary>
    public void Offer(IReadOnlyList<WallEvent> events)
    {
        if (_disposed || events.Count == 0) return;
        var now = DateTime.UtcNow;
        foreach (var e in events) _gate.Offer(Of(e), now);
        if (!_due.IsEnabled) _due.Start();
    }

    public void Dispose()
    {
        _disposed = true;
        _due.Stop();
    }

    /// <summary>A family's cue at <paramref name="pitch"/> (1 is the chime as recorded), ready for an output; null when
    /// the chime is missing. Kept apart from the device so the tests can read the samples.</summary>
    internal static ISampleProvider? Cue(WallSound sound, double pitch)
    {
        if (Clip(sound) is not { } bytes) return null;
        var source = new WaveFileReader(new MemoryStream(bytes, writable: false)).ToSampleProvider();
        // Relabelling the rate shifts pitch (and length, by the same few percent); the resampler brings it back to a
        // rate every device takes.
        var shifted = new WdlResamplingSampleProvider(new Varispeed(source, pitch), source.WaveFormat.SampleRate);
        return new VolumeSampleProvider(shifted) { Volume = Volume };
    }

    private static void Play(WallSound sound, double pitch) => Task.Run(() =>
    {
        try
        {
            if (Cue(sound, pitch) is not { } cue) return;
            using var output = new WaveOutEvent();
            output.Init(cue);
            output.Play();
            var limit = DateTime.UtcNow + TimeSpan.FromSeconds(6);
            while (output.PlaybackState == PlaybackState.Playing && DateTime.UtcNow < limit) Thread.Sleep(25);
        }
        catch { /* no device, or a missing chime: the wall stays silent rather than complaining */ }
    });

    private static byte[]? Clip(WallSound sound)
    {
        lock (Clips)
        {
            if (Clips.TryGetValue(sound, out var cached)) return cached;
            var resource = NotificationSounds.All.FirstOrDefault(s => s.Id == ClipOf(sound))?.Resource;
            using var stream = resource is null ? null : Assembly.GetExecutingAssembly().GetManifestResourceStream(resource);
            byte[]? bytes = null;
            if (stream is not null)
            {
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                bytes = copy.ToArray();
            }
            return Clips[sound] = bytes;
        }
    }

    private sealed class Varispeed(ISampleProvider source, double factor) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(
            (int)Math.Round(source.WaveFormat.SampleRate * factor), source.WaveFormat.Channels);
        public int Read(float[] buffer, int offset, int count) => source.Read(buffer, offset, count);
    }

    /// <summary>The merge window and the per-family rate limit, apart from any audio so the tests can drive the clock.</summary>
    public sealed class Gate
    {
        private readonly HashSet<WallSound> _pending = new();
        private readonly Dictionary<WallSound, DateTime> _played = new();
        private DateTime _opened;

        public bool Waiting => _pending.Count > 0;

        /// <summary>Add a family to the cue being gathered; the first one opens the half-second window.</summary>
        public void Offer(WallSound sound, DateTime nowUtc)
        {
            if (_pending.Count == 0) _opened = nowUtc;
            _pending.Add(sound);
        }

        /// <summary>Once the window has closed: the most important family gathered that has not sounded in the last
        /// twelve seconds, or null. Either way the window is spent.</summary>
        public WallSound? Take(DateTime nowUtc)
        {
            if (_pending.Count == 0 || nowUtc - _opened < MergeWindow) return null;
            WallSound? chosen = null;
            foreach (var sound in _pending.OrderByDescending(s => s))
            {
                if (_played.TryGetValue(sound, out var last) && nowUtc - last < FamilyGap) continue;
                chosen = sound;
                break;
            }
            _pending.Clear();
            if (chosen is { } play) _played[play] = nowUtc;
            return chosen;
        }
    }
}
