using System.Net.Http;
using System.Text.Json.Nodes;

namespace VibeCode.Services;

/// <summary>
/// A provider's public Statuspage summary (summary.json), read only while a <see cref="ProviderHealthGate"/> is open so a
/// waiting pane can say what the provider itself reports. Best effort by design: a page that cannot be reached or read
/// changes nothing, and recovery never waits for it.
/// </summary>
public static class ProviderStatusPage
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(2);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    internal static Func<Uri, Task<string>> Fetch { get; set; } = uri => Http.GetStringAsync(uri);

    public static Uri? SummaryUrl(string provider) => provider switch
    {
        "claude" => new Uri("https://status.claude.com/api/v2/summary.json"),
        "codex" => new Uri("https://status.openai.com/api/v2/summary.json"),
        _ => null,
    };

    // A page covers more than the CLI uses (claude.ai, ChatGPT, Sora...); only the parts a coding agent depends on count.
    private static string[] RelevantComponents(string provider) => provider switch
    {
        "claude" => ["api", "claude code"],
        "codex" => ["api", "codex"],
        _ => [],
    };

    /// <summary>Read the page once. <c>Read</c> is false when it could not be fetched or parsed.</summary>
    internal static async Task<(bool Read, string? Incident)> TryReadAsync(Uri url, string provider)
    {
        try { return Parse(await Fetch(url), provider); }
        catch { return (false, null); }   // offline, blocked or rate limited: the gate carries on without it
    }

    /// <summary>The name of the worst unresolved incident touching the provider's API or CLI; failing that, a degraded
    /// component of theirs; null when they are operational.</summary>
    public static (bool Read, string? Incident) Parse(string json, string provider)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (System.Text.Json.JsonException) { return (false, null); }
        if (root is not JsonObject page) return (false, null);
        var keywords = RelevantComponents(provider);
        bool Relevant(string? name) => name is not null && keywords.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase));

        var incident = (page["incidents"] as JsonArray ?? new JsonArray())
            .OfType<JsonObject>()
            .Where(i => i["status"]?.ToString() is not ("resolved" or "postmortem" or "completed")
                        && (i["components"] is not JsonArray { Count: > 0 } affected
                            || affected.OfType<JsonObject>().Any(c => Relevant(c["name"]?.ToString()))))
            .Select(i => (Name: i["name"]?.ToString().Trim(), Rank: Rank(i["impact"]?.ToString())))
            .Where(i => !string.IsNullOrEmpty(i.Name))
            .OrderByDescending(i => i.Rank)
            .FirstOrDefault().Name;
        if (incident is not null) return (true, incident);

        var degraded = (page["components"] as JsonArray ?? new JsonArray())
            .OfType<JsonObject>()
            .Select(c => (Name: c["name"]?.ToString().Trim(), Status: c["status"]?.ToString()))
            .Where(c => Relevant(c.Name) && c.Status is { Length: > 0 } and not "operational")
            .OrderByDescending(c => Rank(c.Status))
            .FirstOrDefault();
        return (true, degraded.Name is null ? null : $"{degraded.Name}: {degraded.Status!.Replace('_', ' ')}");
    }

    private static int Rank(string? severity) => severity switch
    {
        "critical" or "major_outage" => 3,
        "major" or "partial_outage" => 2,
        "minor" or "degraded_performance" => 1,
        _ => 0,
    };
}
