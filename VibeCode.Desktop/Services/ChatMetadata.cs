namespace VibeCode.Services;

/// <summary>App-owned chat preferences; never changes a provider's transcript.</summary>
public sealed record ChatMetadata
{
    public bool IsLocked { get; init; }
    public string? Title { get; init; }
    public bool IsTitleManual { get; init; }
    public bool HasGeneratedTitle { get; init; }

    public static string Key(string provider, string sessionId) => provider + ":" + sessionId;

    internal static Dictionary<string, ChatMetadata> Merge(Dictionary<string, ChatMetadata> mine,
        Dictionary<string, ChatMetadata>? theirs, Dictionary<string, ChatMetadata>? baseline)
    {
        var merged = new Dictionary<string, ChatMetadata>(theirs ?? new(), StringComparer.OrdinalIgnoreCase);
        foreach (var key in mine.Keys.Concat((IEnumerable<string>?)baseline?.Keys ?? Enumerable.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            mine.TryGetValue(key, out var current);
            var previous = baseline?.GetValueOrDefault(key);
            if (baseline is not null && current == previous) continue;
            if (current is null) merged.Remove(key);
            else merged[key] = current;
        }
        return merged;
    }
}
