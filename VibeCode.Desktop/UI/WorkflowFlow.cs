namespace VibeCode.UI;

/// <summary>
/// How a working connection streams on the workflow wall, taken from the public OpenRouter throughput of the agent's
/// model rather than from this machine's own token counters.
///
/// The local counters are the wrong source for motion. Providers report usage in bursts - nothing for half a minute
/// of tool calls, then a whole turn's cached input at once - so a rate measured from them reads 0, then 300k, then 0
/// again. Driving animation from that makes a calm agent look frantic and a busy one look stalled. The OpenRouter
/// figure is a 30-minute median of how fast the model actually generates, so it is steady, and it is the honest
/// answer to the question the motion asks: how fast is this agent's model writing while it works.
///
/// Each travelling packet stands for <see cref="TokensPerPacket"/> tokens. Speed grows with the square root of
/// throughput and spacing shrinks with it, so across the usual 10-400 tok/s range packets per second are exactly
/// tokens per second ÷ 20, while the fastest models remain readable instead of a blur. Clamped at both ends so a
/// 2,000 tok/s endpoint and a 5 tok/s one both still look like a stream.
/// </summary>
public readonly record struct WorkflowFlow(double PixelsPerSecond, double Spacing, double TokensPerSecond, bool Measured)
{
    public const double TokensPerPacket = 20;
    public const double MinSpeed = 48, MaxSpeed = 320;
    public const double MinSpacing = 16, MaxSpacing = 120;

    /// <summary>The pace used when OpenRouter has no reading for a model. Deliberately unremarkable, and drawn dimmer,
    /// so a missing reading never looks like a measured one.</summary>
    public static readonly WorkflowFlow Unmeasured = new(84, 56, 0, false);

    public static WorkflowFlow For(double? tokensPerSecond)
    {
        if (tokensPerSecond is not { } rate || !double.IsFinite(rate) || rate <= 0) return Unmeasured;
        var speed = Math.Clamp(16 * Math.Sqrt(rate), MinSpeed, MaxSpeed);
        var spacing = Math.Clamp(speed * TokensPerPacket / rate, MinSpacing, MaxSpacing);
        return new(speed, spacing, rate, true);
    }

    /// <summary>Seconds for the stream to advance one packet. One animation cycle, so the loop closes exactly.</summary>
    public double Period => Spacing / PixelsPerSecond;

    /// <summary>Packets leaving the agent each second.</summary>
    public double PacketsPerSecond => PixelsPerSecond / Spacing;

    /// <summary>Fill of the card's speed meter on a log scale: 5 tok/s is empty, 400 tok/s and above is full.</summary>
    public double Meter => !Measured ? 0 : Math.Clamp(Math.Log10(TokensPerSecond / 5) / Math.Log10(400.0 / 5), 0.06, 1);
}
