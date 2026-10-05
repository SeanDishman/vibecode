namespace VibeCode.Services;

/// <summary>A bounded timeline of actual PCM, independent of UI polling or visibility.</summary>
public sealed class MicLevelHistory(int captureToken)
{
    public const double BucketSeconds = 0.1;
    public const int Capacity = 600; // One minute at ten measurements per second, about 5 KB per capture.
    private const int SamplesPerBucket = MicCapture.SampleRate / 10;
    private readonly object _gate = new();
    private readonly double[] _levels = new double[Capacity];
    private long _samples;

    public void AppendPcm16(ReadOnlySpan<byte> pcm)
    {
        lock (_gate)
        {
            var remaining = pcm.Length / 2;
            var offset = 0;
            while (remaining > 0)
            {
                var bucket = _samples / SamplesPerBucket;
                var within = (int)(_samples % SamplesPerBucket);
                var index = (int)(bucket % Capacity);
                if (within == 0) _levels[index] = 0;
                var count = Math.Min(remaining, SamplesPerBucket - within);
                // Preserve brief real speech peaks when two 50 ms device buffers share a bin.
                _levels[index] = Math.Max(_levels[index],
                    MicAmplitudePolicy.MeasurePcm16(pcm.Slice(offset * 2, count * 2)).Level);
                _samples += count;
                remaining -= count;
                offset += count;
            }
        }
    }

    public MicHistorySnapshot Snapshot()
    {
        lock (_gate)
        {
            var last = _samples == 0 ? -1 : (_samples - 1) / SamplesPerBucket;
            var first = Math.Max(0, last - Capacity + 1);
            var levels = new double[(int)(last - first + 1)];
            for (var i = 0; i < levels.Length; i++) levels[i] = _levels[(first + i) % Capacity];
            return new MicHistorySnapshot(captureToken, _samples, first * BucketSeconds, levels);
        }
    }
}

/// <summary>Immutable audio-time history; elapsed time is the duration of the captured clip.</summary>
public sealed class MicHistorySnapshot(int captureToken, long sampleCount, double startSeconds, double[] levels)
{
    public int CaptureToken { get; } = captureToken;
    public long SampleCount { get; } = sampleCount;
    public double DurationSeconds => SampleCount / (double)MicCapture.SampleRate;
    public double StartSeconds { get; } = startSeconds;
    public int Count => levels.Length;
    public double GetLevel(int index) => levels[index];

    // Fill from left to right for the first fifteen seconds; then compress to fit up to a minute.
    // Thereafter scroll the most recent minute. Missing input is never invented as silence.
    public double WindowSeconds => Math.Min(60, Math.Max(15, DurationSeconds));
    public double WindowStartSeconds => Math.Max(StartSeconds, DurationSeconds - WindowSeconds);

    public double GetDisplayLevel(int index, int columns)
    {
        if (columns <= 0 || index < 0 || index >= columns) return double.NaN;
        var from = WindowStartSeconds + WindowSeconds * index / columns;
        var to = WindowStartSeconds + WindowSeconds * (index + 1) / columns;
        if (from >= DurationSeconds || levels.Length == 0) return double.NaN;
        var first = Math.Max(0, (int)Math.Floor((from - StartSeconds) / MicLevelHistory.BucketSeconds));
        var last = Math.Min(levels.Length - 1,
            (int)Math.Ceiling((Math.Min(to, DurationSeconds) - StartSeconds) / MicLevelHistory.BucketSeconds) - 1);
        var peak = 0.0;
        for (var i = first; i <= last; i++) peak = Math.Max(peak, levels[i]);
        return peak;
    }
}
