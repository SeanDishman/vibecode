using System.Buffers.Binary;
using System.Diagnostics;

namespace VibeCode.Services;

/// <summary>Measures signed little-endian PCM16 without changing or retaining the capture buffer.</summary>
public static class MicAmplitudePolicy
{
    public readonly record struct Amplitude(double Rms, double Peak, double Level);

    public static Amplitude MeasurePcm16(ReadOnlySpan<byte> pcm)
    {
        var samples = pcm.Length / 2;
        if (samples == 0) return default;
        double sum = 0, peak = 0;
        for (var i = 0; i < samples; i++)
        {
            // Convert to double before taking abs: Int16.MinValue is a valid full-scale sample.
            var sample = BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(i * 2, 2)) / 32768.0;
            sum += sample * sample;
            peak = Math.Max(peak, Math.Abs(sample));
        }
        var rms = Math.Sqrt(sum / samples);
        // A fixed -54 dB noise floor and -6 dB ceiling keep quiet voices visible without
        // making room noise fill the meter. Silence has no manufactured motion.
        var level = rms <= 0 ? 0 : Math.Clamp((20 * Math.Log10(rms) + 54) / 48, 0, 1);
        return new Amplitude(rms, peak, level);
    }

    public static double Smooth(double current, double target, double elapsedSeconds)
    {
        target = double.IsFinite(target) ? Math.Clamp(target, 0, 1) : 0;
        var seconds = double.IsFinite(elapsedSeconds) ? Math.Clamp(elapsedSeconds, 0, 1) : 0;
        var tau = target > current ? 0.035 : 0.14;
        var value = current + (target - current) * (1 - Math.Exp(-seconds / tau));
        return value < 0.001 ? 0 : Math.Clamp(value, 0, 1);
    }
}

/// <summary>An immutable measurement of seven consecutive slices of one real microphone buffer.</summary>
public sealed class MicLevelFrame
{
    public const int BarCount = 7;
    private readonly double[] _levels;
    public int CaptureToken { get; }
    public long CapturedAt { get; }
    public MicAmplitudePolicy.Amplitude Amplitude { get; }
    public double GetBarLevel(int index) => _levels[index];

    private MicLevelFrame(int token, double[] levels, MicAmplitudePolicy.Amplitude amplitude)
    {
        CaptureToken = token;
        _levels = levels;
        Amplitude = amplitude;
        CapturedAt = Stopwatch.GetTimestamp();
    }

    public static MicLevelFrame FromPcm16(int token, ReadOnlySpan<byte> pcm)
    {
        var samples = pcm.Length / 2;
        var levels = new double[BarCount];
        for (var i = 0; i < BarCount; i++)
        {
            var start = samples * i / BarCount;
            var end = samples * (i + 1) / BarCount;
            levels[i] = MicAmplitudePolicy.MeasurePcm16(pcm.Slice(start * 2, (end - start) * 2)).Level;
        }
        return new MicLevelFrame(token, levels, MicAmplitudePolicy.MeasurePcm16(pcm));
    }
}
