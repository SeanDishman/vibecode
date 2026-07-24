using System.Windows.Media;

namespace VibeCode.Services;

/// <summary>
/// The categorical colors the usage charts identify models by, plus the model naming they share.
///
/// The eight hues and their ORDER are not cosmetic. The order is the colorblind-safety mechanism: neighbouring
/// slots are what end up beside each other in a legend, a stacked bar, and a donut ring, so the sequence was
/// chosen by enumerating orderings and keeping the one that maximises the minimum adjacent separation. Measured
/// against this app's own card surface (#27282C) it clears every gate in the data-viz method:
///   lightness band PASS - chroma floor PASS - worst adjacent CVD dE 9.1 (deutan), tritan 17.3 (target >= 8)
///   worst adjacent normal-vision dE 26.5 (floor 15) - all eight >= 3:1 contrast on the surface.
/// It also passes unchanged on Bg1, Bg0 and the CLI theme's pure black, so one table serves both themes.
/// Re-run the validator before changing a value or the order.
/// </summary>
public static class UsagePalette
{
    private static readonly Color[] Slots =
    {
        Rgb(0xD5, 0x51, 0x81),   // 0 magenta
        Rgb(0x0A, 0x9A, 0x0A),   // 1 green
        Rgb(0xE6, 0x67, 0x67),   // 2 red
        Rgb(0x39, 0x87, 0xE5),   // 3 blue
        Rgb(0xC9, 0x85, 0x00),   // 4 yellow
        Rgb(0x90, 0x85, 0xE9),   // 5 violet
        Rgb(0xD9, 0x59, 0x26),   // 6 orange
        Rgb(0x19, 0x9E, 0x70),   // 7 aqua
    };

    /// <summary>Everything past the eight slots. A ninth generated hue would be indistinguishable from an
    /// existing slot under CVD, so the tail folds into one neutral instead of inventing colors.</summary>
    private static readonly Color OtherColor = Rgb(0x8A, 0x8C, 0x95);

    /// <summary>
    /// Fixed model-tier -> slot assignment. Colour follows the ENTITY, never its rank: this table is written in
    /// provider/tier order, not usage order, so changing the time window or dropping a model out of the top
    /// three never repaints the survivors. Eight is a hard budget - anything not listed folds into "Other"
    /// rather than getting a generated hue.
    ///
    /// Point releases share their tier's colour on purpose ("Opus" is one thing to a reader, not five); the
    /// table still names each id explicitly so a new point release is a deliberate one-line addition instead of
    /// a string-parsing guess.
    /// </summary>
    private static readonly Dictionary<string, int> SlotByModel = new(StringComparer.OrdinalIgnoreCase)
    {
        // 0 - Claude frontier
        ["claude-fable-5"] = 0,
        ["claude-mythos-5"] = 0,
        // 1 - Claude Opus
        ["claude-opus-5"] = 1,
        ["claude-opus-4-8"] = 1,
        ["claude-opus-4-7"] = 1,
        ["claude-opus-4-6"] = 1,
        ["claude-opus-4-5"] = 1,
        // 2 - Claude Sonnet
        ["claude-sonnet-5"] = 2,
        ["claude-sonnet-4-6"] = 2,
        ["claude-sonnet-4-5"] = 2,
        // 3 - Claude Haiku
        ["claude-haiku-4-5"] = 3,
        // 4 - OpenAI premium tier ($5 / $30 per Mtok)
        ["gpt-5.6-sol"] = 4,
        ["gpt-5.5"] = 4,
        // 5 - OpenAI efficient tiers
        ["gpt-5.6-terra"] = 5,
        ["gpt-5.6-luna"] = 5,
        // 6 - Moonshot Kimi
        ["k3"] = 6,
        ["kimi-k3"] = 6,
        ["kimi-code/k3"] = 6,
        ["kimi-k2.7-code"] = 6,
        ["kimi-for-coding"] = 6,
        ["kimi-code/kimi-for-coding"] = 6,
        ["kimi-k2.7-code-highspeed"] = 6,
        ["kimi-for-coding-highspeed"] = 6,
        ["kimi-code/kimi-for-coding-highspeed"] = 6,
        // 7 - xAI Grok
        ["grok-4.5"] = 7,
        ["grok-4-5"] = 7,
    };

    /// <summary>Slots handed to ids this build doesn't know about (a model released after it shipped). Assigned
    /// on first sight and then held for the rest of the session, so an unknown model still keeps ONE colour
    /// across every chart and every window change rather than flickering between renders.</summary>
    private static readonly Dictionary<string, int> Improvised = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    public static Color ColorFor(string? modelId)
    {
        var slot = SlotFor(modelId);
        return slot < 0 || slot >= Slots.Length ? OtherColor : Slots[slot];
    }

    public static SolidColorBrush BrushFor(string? modelId)
    {
        var brush = new SolidColorBrush(ColorFor(modelId));
        brush.Freeze();
        return brush;
    }

    private static int SlotFor(string? modelId)
    {
        var id = ModelPricing.CanonicalId(modelId);
        if (SlotByModel.TryGetValue(id, out var slot)) return slot;
        lock (Gate)
        {
            if (Improvised.TryGetValue(id, out slot)) return slot;
            // Only slots no listed model claims are available; past those, fold into "Other".
            var taken = new HashSet<int>(SlotByModel.Values);
            foreach (var used in Improvised.Values) taken.Add(used);
            slot = -1;
            for (var candidate = 0; candidate < Slots.Length; candidate++)
                if (taken.Add(candidate)) { slot = candidate; break; }
            Improvised[id] = slot;
            return slot;
        }
    }

    /// <summary>Human name for a model id, e.g. "claude-opus-5" -> "Opus 5", "gpt-5.6-sol" -> "GPT-5.6 Sol".</summary>
    public static string DisplayName(string? modelId)
    {
        var id = ModelPricing.CanonicalId(modelId);
        if (id == "unknown") return "Unknown model";
        if (KnownDisplay(id) is { } known) return known;

        // Unrecognised id: title-case the segments rather than dumping a raw slug into the legend.
        var name = id.Contains('/') ? id[(id.LastIndexOf('/') + 1)..] : id;
        if (name.Contains(':')) name = name[(name.LastIndexOf(':') + 1)..];
        var words = name.Split('-', '_', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Length <= 2 || !char.IsLetter(w[0]) ? w : char.ToUpperInvariant(w[0]) + w[1..]);
        var display = string.Join(' ', words);
        return display.Length == 0 ? id : display;
    }

    /// <summary>Shipped name for a model id ("claude-opus-4-8" -> "Opus 4.8"), or null if we don't name it.</summary>
    public static string? KnownName(string? modelId) => KnownDisplay(ModelPricing.CanonicalId(modelId));

    private static string? KnownDisplay(string id) => id switch
    {
        "claude-fable-5" => "Fable 5",
        "claude-mythos-5" => "Mythos 5",
        "claude-opus-5" => "Opus 5",
        "claude-opus-4-8" => "Opus 4.8",
        "claude-opus-4-7" => "Opus 4.7",
        "claude-opus-4-6" => "Opus 4.6",
        "claude-opus-4-5" => "Opus 4.5",
        "claude-sonnet-5" => "Sonnet 5",
        "claude-sonnet-4-6" => "Sonnet 4.6",
        "claude-sonnet-4-5" => "Sonnet 4.5",
        "claude-haiku-4-5" => "Haiku 4.5",
        "gpt-5.6-sol" => "GPT-5.6 Sol",
        "gpt-5.6-terra" => "GPT-5.6 Terra",
        "gpt-5.6-luna" => "GPT-5.6 Luna",
        "gpt-5.5" => "GPT-5.5",
        "k3" or "kimi-k3" or "kimi-code/k3" => "Kimi K3",
        "kimi-k2.7-code" or "kimi-for-coding" or "kimi-code/kimi-for-coding" => "Kimi K2.7",
        "kimi-k2.7-code-highspeed" or "kimi-for-coding-highspeed" or "kimi-code/kimi-for-coding-highspeed"
            => "Kimi K2.7 Turbo",
        "grok-4.5" or "grok-4-5" => "Grok 4.5",
        _ => null,
    };

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
}
