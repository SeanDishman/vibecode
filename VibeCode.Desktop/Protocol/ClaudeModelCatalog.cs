using System.Text.RegularExpressions;

namespace VibeCode.Protocol;

/// <summary>Claude choices shared by the CLI adapter, offline picker, and bridge setup.</summary>
internal static class ClaudeModelCatalog
{
    public const string Opus = "claude-opus-5-5";
    public const string Sonnet = "claude-sonnet-5-5";
    public const string Fable = "claude-fable-5-1";
    public const string Haiku = "claude-haiku-5-5";
    /// <summary>Claude Code supports Haiku 5.5 from 2.1.293 (https://code.claude.com/docs/en/model-config). 2.1.286 still runs
    /// its explicit ID (checked 2026-11-01), only warning "unrecognized_model" and budgeting a 200K window instead of 1M.
    /// This floor stays lower: when no installed runtime meets it, the first candidate is kept, which can be older still.</summary>
    public static readonly Version MinimumCliVersion = new(2, 1, 284);

    internal sealed record Spec(string Id, string Name, string Description,
        IReadOnlyList<string> EffortLevels, bool Fast = false);

    private static readonly string[] AdaptiveEfforts = ["low", "medium", "high", "xhigh", "max"];

    // Anthropic's public lineup and Claude Code capabilities, verified 2026-10-29 (Haiku 5.5 on 2026-11-01):
    // https://platform.claude.com/docs/en/models/overview
    // https://code.claude.com/docs/en/model-config
    // https://code.claude.com/docs/en/fast-mode
    // Mythos is invite-only: keep eligible live rows, but do not advertise it to every account.
    public static readonly IReadOnlyList<Spec> Models = new Spec[]
    {
        new(Opus, "Opus 5.5", "Latest Opus for complex coding and long-running agent work.", AdaptiveEfforts, true),
        new(Fable, "Fable 5.1", "Most capable Claude for demanding reasoning and long-running tasks.", AdaptiveEfforts),
        new(Sonnet, "Sonnet 5.5", "Latest Sonnet for everyday coding, balancing speed and capability.", AdaptiveEfforts),
        // Offered by request in place of Haiku 4.5, which an older CLI's "haiku" alias still resolves to.
        new(Haiku, "Haiku 5.5", "Latest Haiku, the fastest Claude for quick answers and high-volume tasks.", AdaptiveEfforts),
        // Retain the explicit choices older VibeCode builds offered.
        new("claude-opus-5", "Opus 5", "Previous Opus for complex coding and agent work.", AdaptiveEfforts, true),
        new("claude-opus-4-8", "Opus 4.8", "Earlier Opus for autonomous coding and knowledge work.", AdaptiveEfforts, true),
    };

    /// <summary>Models the picker leaves out even when Claude Code still lists them - either superseded, or kept off by
    /// request (Sonnet 4.6, Haiku 4.5 and the old non-.1 Fable 5). A chat already on one keeps running it; it just is not
    /// offered again.</summary>
    private static readonly string[] Unlisted =
        ["claude-sonnet-5", "claude-opus-4-7", "claude-opus-4-6", "claude-sonnet-4-6", "claude-haiku-4-5", "claude-fable-5"];

    public static bool IsUnlisted(string? id) => Unlisted.Contains(CanonicalId(id), StringComparer.Ordinal);

    private static readonly Spec Mythos = new("claude-mythos-5-1", "Mythos 5.1",
        "Current Mythos for accounts with Project Glasswing access.", AdaptiveEfforts);

    public static IEnumerable<Spec> ForAvailable(IEnumerable<string?> availableIds)
    {
        foreach (var model in Models) yield return model;
        // An existing Mythos row establishes that the provider offers this family. Upgrade that menu only.
        if (availableIds.Any(id => CanonicalId(id) is "mythos"
            || CanonicalId(id).StartsWith("claude-mythos-", StringComparison.Ordinal)))
            yield return Mythos;
    }

    private static readonly Regex SnapshotDate = new(@"-\d{8}$", RegexOptions.Compiled);

    public static string CanonicalId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "";
        var value = id.Trim();
        var bracket = value.IndexOf('[');
        if (bracket >= 0) value = value[..bracket];
        return SnapshotDate.Replace(value, "").ToLowerInvariant();
    }

    public static Spec? Find(string? id) =>
        Models.FirstOrDefault(model => string.Equals(model.Id, CanonicalId(id), StringComparison.Ordinal));

    public static Spec? ForAlias(string? alias) => Find(CanonicalId(alias) switch
    {
        "opus" => Opus,
        "sonnet" => Sonnet,
        "fable" => Fable,
        "haiku" => Haiku,
        _ => null,
    });
}
