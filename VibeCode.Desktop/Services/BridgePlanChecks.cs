namespace VibeCode.Services;

public sealed record BridgePlanFinding(string Code, bool BlocksDispatch, string Message, IReadOnlyList<string> TaskIds);

/// <summary>Checks declared responsibility in Advanced Bridges. Files remain advisory, never locks.</summary>
public static class BridgePlanChecks
{
    private static string Key(string value) => string.Join(" ", value.Replace('\\', '/').Trim().Split((char[]?)null,
        StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();

    public static IReadOnlyList<BridgePlanFinding> Inspect(IReadOnlyList<BridgeWorkTask> tasks,
        IReadOnlyDictionary<string, List<string>> requiredOutputs)
    {
        var findings = new List<BridgePlanFinding>();
        var byId = tasks.ToDictionary(t => t.Id, StringComparer.Ordinal);
        // Resolve each step's prerequisites once. Re-walking a long dependency chain for every pair
        // turned a full plan inspection into cubic work on the UI thread.
        HashSet<string> Ancestors(BridgeWorkTask task)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<string>(task.Dependencies);
            while (pending.TryPop(out var id))
                if (result.Add(id) && byId.TryGetValue(id, out var dependency))
                    foreach (var prerequisite in dependency.Dependencies) pending.Push(prerequisite);
            return result;
        }
        var ancestors = tasks.ToDictionary(t => t.Id, Ancestors);
        var outputs = tasks.ToDictionary(t => t.Id, t => t.Outputs.Select(Key).ToHashSet(StringComparer.Ordinal));
        var files = tasks.ToDictionary(t => t.Id, t => t.Files.Select(Key).ToHashSet(StringComparer.Ordinal));
        var titles = tasks.ToDictionary(t => t.Id, t => Key(t.Title));
        foreach (var (coordinator, required) in requiredOutputs)
        {
            var owned = tasks.Where(t => t.CoordinatorId == coordinator).ToArray();
            var covered = owned.SelectMany(t => t.Outputs).Select(Key).ToHashSet(StringComparer.Ordinal);
            foreach (var output in required.Where(o => !covered.Contains(Key(o))))
                findings.Add(new("missing_output", true, $"Required deliverable '{output}' has no assigned step.", []));
            foreach (var task in owned.Where(t => t.Outputs.Count == 0))
                findings.Add(new("unspecified_output", true, $"Step '{task.Title}' needs an explicit deliverable.", [task.Id]));
        }
        for (var i = 0; i < tasks.Count; i++)
        for (var j = i + 1; j < tasks.Count; j++)
        {
            var a = tasks[i];
            var b = tasks[j];
            if (a.State == "completed" || b.State == "completed" || ancestors[a.Id].Contains(b.Id) || ancestors[b.Id].Contains(a.Id)) continue;
            if (outputs[a.Id].Overlaps(outputs[b.Id]))
                findings.Add(new("duplicate_output", true,
                    $"Steps '{a.Title}' and '{b.Title}' both own {string.Join(", ", outputs[a.Id].Intersect(outputs[b.Id]))}. Assign one owner or express their handoff with a dependency.", [a.Id, b.Id]));
            if (a.OwnerId != b.OwnerId && files[a.Id].Overlaps(files[b.Id]))
                findings.Add(new("shared_files", false,
                    $"Steps '{a.Title}' and '{b.Title}' name shared files without a dependency. Coordinate the affected lines; files are not exclusive.", [a.Id, b.Id]));
            if (a.OwnerId != b.OwnerId && titles[a.Id] == titles[b.Id])
                findings.Add(new("duplicate_responsibility", false,
                    $"Steps '{a.Id}' and '{b.Id}' give different workers the same responsibility '{a.Title}'. Clarify their distinct scopes or add the prerequisite.", [a.Id, b.Id]));
        }
        return findings;
    }
}
