using System.IO;

namespace VibeCode.Services;

/// <summary>A path plus the moment that folder was last used to start or continue a chat.</summary>
public sealed record RecentDirectoryCandidate(string Cwd, string Name, DateTimeOffset LastUsed);

/// <summary>Canonicalizes, deduplicates, and ranks the folders shown on the new-chat screen.</summary>
public static class RecentDirectoryHistory
{
    public const int SuggestionCount = 5;
    public const int MaxRemembered = 30;

    public static string? NormalizePath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            var full = Path.GetFullPath(raw.Trim().Trim('"'));
            return Path.TrimEndingDirectorySeparator(full);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException
                                   or PathTooLongException or System.Security.SecurityException)
        {
            return null;
        }
    }

    public static bool PathsEqual(string? left, string? right)
    {
        var a = NormalizePath(left);
        var b = NormalizePath(right);
        return a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    public static string DisplayName(string cwd)
    {
        var normalized = NormalizePath(cwd) ?? cwd;
        return Path.GetFileName(normalized) is { Length: > 0 } name ? name : normalized;
    }

    public static List<RecentDirectoryState> NormalizeRemembered(IEnumerable<RecentDirectoryState>? entries)
    {
        var byPath = new Dictionary<string, RecentDirectoryState>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries ?? Enumerable.Empty<RecentDirectoryState>())
        {
            if (entry is null || NormalizePath(entry.Cwd) is not { } cwd) continue;
            var lastUsed = entry.LastUsed == default ? DateTimeOffset.MinValue : entry.LastUsed.ToUniversalTime();
            if (byPath.TryGetValue(cwd, out var existing) && existing.LastUsed >= lastUsed) continue;
            byPath[cwd] = new RecentDirectoryState { Cwd = cwd, LastUsed = lastUsed };
        }

        return byPath.Values
            .OrderByDescending(entry => entry.LastUsed)
            .ThenBy(entry => entry.Cwd, StringComparer.OrdinalIgnoreCase)
            .Take(MaxRemembered)
            .ToList();
    }

    public static IReadOnlyList<RecentDirectoryCandidate> SelectSuggestions(
        IEnumerable<RecentDirectoryCandidate>? candidates,
        IEnumerable<string>? excludedDirectories = null,
        int limit = SuggestionCount)
    {
        if (limit <= 0) return Array.Empty<RecentDirectoryCandidate>();

        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in excludedDirectories ?? Enumerable.Empty<string>())
            if (NormalizePath(path) is { } normalized) excluded.Add(normalized);

        var byPath = new Dictionary<string, (RecentDirectoryCandidate Candidate, int Order)>(StringComparer.OrdinalIgnoreCase);
        var order = 0;
        foreach (var candidate in candidates ?? Enumerable.Empty<RecentDirectoryCandidate>())
        {
            if (NormalizePath(candidate.Cwd) is not { } cwd || excluded.Contains(cwd))
            {
                order++;
                continue;
            }

            var normalized = candidate with
            {
                Cwd = cwd,
                Name = string.IsNullOrWhiteSpace(candidate.Name) ? DisplayName(cwd) : candidate.Name,
                LastUsed = candidate.LastUsed == default ? DateTimeOffset.MinValue : candidate.LastUsed.ToUniversalTime(),
            };
            if (!byPath.TryGetValue(cwd, out var existing) || normalized.LastUsed > existing.Candidate.LastUsed)
                byPath[cwd] = (normalized, order);
            order++;
        }

        return byPath.Values
            .OrderByDescending(entry => entry.Candidate.LastUsed)
            .ThenBy(entry => entry.Order)
            .Select(entry => entry.Candidate)
            .Take(limit)
            .ToList();
    }
}
