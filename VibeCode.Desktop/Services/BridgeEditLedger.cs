using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace VibeCode.Services;

/// <summary>Who made a Bridge edit, captured when the edit lands. <see cref="Number"/> and <see cref="Label"/> are
/// the roster identity at that moment; <see cref="AgentId"/> is the stable one.</summary>
public sealed record BridgeEditAuthor(string AgentId, int Number, string Label, string Provider, string? Model,
    string Role, string? Task, string? RunId);

/// <summary>One line of the edit ledger. Paths are relative to the project root with '/' separators.</summary>
public sealed class BridgeEditEntry
{
    public int V { get; set; } = 1;
    public string Id { get; set; } = "";
    public DateTimeOffset At { get; set; }
    public string? Run { get; set; }
    public string AgentId { get; set; } = "";
    public int Agent { get; set; }
    public string Label { get; set; } = "";
    public string Provider { get; set; } = "";
    public string? Model { get; set; }
    public string Role { get; set; } = "agent";
    public string? Task { get; set; }
    public string File { get; set; } = "";
    public string Change { get; set; } = "edit";
    public List<BridgeEditRange> Ranges { get; set; } = [];
    public int Added { get; set; }
    public int Removed { get; set; }
    public bool Exact { get; set; } = true;
    public string Tool { get; set; } = "";

    [JsonIgnore] public string Lines => BridgeEditDiff.Describe(Change, Ranges, Exact);
}

/// <summary>
/// Which Bridge agent edited which lines of which file, so peers can look before they touch the same code. Kept per
/// project in <c>.vibecode/bridge-edits</c> (git-ignored, beside the Bridge mailboxes and chat archive) as one
/// append-only JSONL file per UTC day. Entries older than <see cref="Retention"/> are never served, and a day file is
/// deleted as soon as everything in it has expired. Only the app writes here; agents read it through the bridge MCP.
/// </summary>
public static class BridgeEditLedger
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    public const string DirectoryName = "bridge-edits";
    public const long MaxDayFileBytes = 16L * 1024 * 1024;
    private const int MaxLineCharacters = 64 * 1024;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private static readonly Regex DayFile = new(@"^edits-(\d{4})-(\d{2})-(\d{2})\.jsonl$", RegexOptions.CultureInvariant);
    private static readonly object WriteGate = new();
    private static readonly object FileGate = new();
    private static Task _writes = Task.CompletedTask;
    private static readonly ConcurrentDictionary<string, CachedDay> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, DateTimeOffset> LastCleanup = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lazy<Timer> Maintenance = new(() => new Timer(_ =>
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var root in LastCleanup.Keys) { LastCleanup[root] = now; CleanupExpired(root, now); }
    }, null, TimeSpan.FromHours(1), TimeSpan.FromHours(1)));

    private sealed record CachedDay(long Length, DateTime WrittenUtc, IReadOnlyList<BridgeEditEntry> Entries);

    /// <summary>Last storage problem, for diagnostics. Recording never interrupts an agent's turn.</summary>
    public static string? LastError { get; private set; }

    /// <summary>Completes when every queued <see cref="Record"/> has been written.</summary>
    public static Task PendingWrites { get { lock (WriteGate) return _writes; } }

    public static string DirectoryFor(string workspace) => Path.Combine(Path.GetFullPath(workspace), ".vibecode", DirectoryName);

    /// <summary>Queue one successful edit. Line numbers are worked out and appended off the caller's thread, in order,
    /// right away, so a file read for locating a snippet sees the file as the edit left it.</summary>
    public static Task Record(string workspace, BridgeEditAuthor author, BridgeEditObservation edit, DateTimeOffset? at = null)
    {
        var when = (at ?? DateTimeOffset.UtcNow).ToUniversalTime();
        lock (WriteGate)
            return _writes = _writes.ContinueWith(_ => RecordNow(workspace, author, edit, when), CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private static void RecordNow(string workspace, BridgeEditAuthor author, BridgeEditObservation edit, DateTimeOffset at)
    {
        try
        {
            if (BridgeEditDiff.Analyze(edit, ReadSource) is not { } change) return;
            var file = ProjectPath(workspace, change.FullPath);
            if (IsCoordinationFile(file)) return;
            // The next shell command's diff for this file starts from what this edit left behind.
            BridgeShellEdits.Find(edit.Cwd)?.Remember(change.FullPath, ReadSource(change.FullPath), DateTime.UtcNow);
            Append(workspace, new BridgeEditEntry
            {
                Id = Guid.NewGuid().ToString("N")[..12], At = at, Run = author.RunId,
                AgentId = author.AgentId, Agent = author.Number, Label = author.Label, Provider = author.Provider,
                Model = string.IsNullOrWhiteSpace(author.Model) ? null : author.Model, Role = author.Role,
                Task = string.IsNullOrWhiteSpace(author.Task) ? null : author.Task,
                File = file, Change = change.Change, Ranges = change.Ranges.ToList(),
                Added = change.Added, Removed = change.Removed, Exact = change.Exact, Tool = edit.Tool,
            });
            LastError = null;
        }
        catch (Exception ex)
        {
            // Background boundary: a log that cannot be written must never surface in, or stop, an agent's turn.
            LastError = ex.Message;
        }
        ScheduleCleanup(workspace);
    }

    /// <summary>Append one entry to its UTC day file. A day file stops growing at <see cref="MaxDayFileBytes"/>.</summary>
    public static void Append(string workspace, BridgeEditEntry entry)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry, Json) + "\n");
        lock (FileGate)
        {
            var path = Path.Combine(EnsureDirectory(workspace), DayFileName(entry.At.UtcDateTime));
            if (File.Exists(path) && (Redirected(path) || new FileInfo(path).Length + bytes.Length > MaxDayFileBytes)) return;
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            stream.Write(bytes);
        }
    }

    /// <summary>Every entry from the last <see cref="Retention"/>, newest first. Unchanged day files are cached.</summary>
    public static IReadOnlyList<BridgeEditEntry> Read(string workspace, DateTimeOffset now)
    {
        var directory = DirectoryFor(workspace);
        try
        {
            if (!Directory.Exists(directory) || Redirected(directory)) return [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { LastError = ex.Message; return []; }
        var cutoff = now.ToUniversalTime() - Retention;
        var rows = new List<BridgeEditEntry>();
        for (var day = cutoff.UtcDateTime.Date; day <= now.UtcDateTime.Date; day = day.AddDays(1))
        {
            // One unreadable day must not hide the others.
            try { rows.AddRange(LoadDay(Path.Combine(directory, DayFileName(day))).Where(e => e.At >= cutoff)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { LastError = ex.Message; }
        }
        // Appends are chronological, so ties keep their order.
        return rows.Select((entry, index) => (entry, index)).OrderByDescending(x => x.entry.At)
            .ThenByDescending(x => x.index).Select(x => x.entry).ToList();
    }

    private static IReadOnlyList<BridgeEditEntry> LoadDay(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || Redirected(path) || info.Length > MaxDayFileBytes * 2) { Cache.TryRemove(path, out _); return []; }
        if (Cache.TryGetValue(path, out var cached) && cached.Length == info.Length && cached.WrittenUtc == info.LastWriteTimeUtc)
            return cached.Entries;
        var entries = new List<BridgeEditEntry>();
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        {
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (line.Length is 0 or > MaxLineCharacters) continue;
                try
                {
                    if (JsonSerializer.Deserialize<BridgeEditEntry>(line, Json) is { AgentId.Length: > 0, File.Length: > 0 } entry)
                        entries.Add(entry);
                }
                catch (JsonException) { /* A torn or foreign line never hides the rest of the day. */ }
            }
        }
        Cache[path] = new CachedDay(info.Length, info.LastWriteTimeUtc, entries);
        return entries;
    }

    /// <summary>Delete day files whose every entry is older than <see cref="Retention"/>. Only this ledger's own
    /// file names are touched; anything else in the folder, and redirected entries, are left alone.</summary>
    public static int CleanupExpired(string workspace, DateTimeOffset now)
    {
        var removed = 0;
        try
        {
            var directory = DirectoryFor(workspace);
            if (!Directory.Exists(directory) || Redirected(directory) || Redirected(Path.GetDirectoryName(directory)!)) return 0;
            var oldestKept = (now.ToUniversalTime() - Retention).UtcDateTime.Date;
            foreach (var path in Directory.EnumerateFiles(directory))
            {
                var match = DayFile.Match(Path.GetFileName(path));
                if (!match.Success || !DateTime.TryParseExact(match.Groups[1].Value + match.Groups[2].Value + match.Groups[3].Value,
                        "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var day)
                    || day.Date >= oldestKept) continue;
                try
                {
                    if (Redirected(path)) continue;
                    lock (FileGate) File.Delete(path);
                    Cache.TryRemove(path, out _);
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Retry next sweep. */ }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        return removed;
    }

    /// <summary>Clean a project's ledger now (off-thread) and hourly after that. Repeated calls within the hour are free.</summary>
    public static void ScheduleCleanup(string workspace)
    {
        string root;
        try { root = Path.GetFullPath(workspace); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return; }
        _ = Maintenance.Value;
        var now = DateTimeOffset.UtcNow;
        if (LastCleanup.TryGetValue(root, out var last) && now - last < TimeSpan.FromHours(1)) return;
        LastCleanup[root] = now;
        _ = Task.Run(() => CleanupExpired(root, now));
    }

    /// <summary>The project-relative form agents see ("src/app.ts"); files outside the project keep their full path.</summary>
    public static string ProjectPath(string workspace, string fullPath)
    {
        var root = Path.GetFullPath(workspace);
        var relative = Path.GetRelativePath(root, fullPath);
        return (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || Path.IsPathRooted(relative) ? fullPath : relative).Replace('\\', '/');
    }

    // The Bridge status board and the app's own metadata are coordination, not code.
    private static bool IsCoordinationFile(string file) =>
        string.Equals(file, ".vibecode-bridge.md", StringComparison.OrdinalIgnoreCase)
        || file.StartsWith(".vibecode/", StringComparison.OrdinalIgnoreCase);

    private static string? ReadSource(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 16L * 1024 * 1024) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string EnsureDirectory(string workspace)
    {
        var directory = DirectoryFor(workspace);
        var parent = Path.GetDirectoryName(directory)!;
        if (Directory.Exists(parent) && Redirected(parent) || Directory.Exists(directory) && Redirected(directory))
            throw new IOException("The bridge edit log folder is redirected; nothing was written through it.");
        if (Directory.Exists(directory)) return directory;
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, ".gitignore"), "*\n");
        File.WriteAllText(Path.Combine(directory, "README.md"), """
            # Bridge edit log

            VibeCode writes one line here each time a Bridge or Advanced Bridge agent edits a file: who (agent id, number,
            provider, model, role, task), which file (relative to the project), and which lines (where the change landed
            right after that edit). Agents read it with the `bridge_file_edits` bridge tool.

            - One JSON object per line, one file per UTC day: `edits-YYYY-MM-DD.jsonl`.
            - Entries older than 7 days are ignored, and a day file is deleted once everything in it is older than 7 days.
            - Coordination history only: it never locks a file. This folder is git-ignored.
            """);
        return directory;
    }

    private static string DayFileName(DateTime utc) => "edits-" + utc.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + ".jsonl";

    private static bool Redirected(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}
