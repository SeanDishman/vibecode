using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
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
    /// <summary>A shell command's claim whose author is a guess: the file changed while the command ran, but either
    /// nothing recorded its earlier text or another agent's command was running too.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Uncertain { get; set; }

    [JsonIgnore] public string Lines => BridgeEditDiff.Describe(Change, Ranges, Exact);
    /// <summary>True for <see cref="Uncertain"/> claims, and for shell claims logged before that flag existed with no
    /// lines at all (the same guess, recorded as an "edit, lines unknown").</summary>
    [JsonIgnore] public bool AuthorUncertain => Uncertain || Tool == "Shell" && !Exact && Ranges.Count == 0;
}

/// <summary>
/// Which agent edited which lines of which file, shared by chats, bridges and agent teams. Kept per
/// canonical project in the user's VibeCode <c>agent-edit-history</c> directory as one
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

    public static string DirectoryFor(string workspace)
    {
        var root = CanonicalFolder(Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace))).ToUpperInvariant();
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root)))[..24].ToLowerInvariant();
        return Path.Combine(AppSettings.Dir, "agent-edit-history", key);
    }

    private static string LegacyDirectoryFor(string workspace) => Path.Combine(Path.GetFullPath(workspace), ".vibecode", DirectoryName);

    /// <summary>Capture the file when the edit settles, then compute lines and append off-thread, in order.
    /// A later edit must not move the first edit's lines while the storage queue is busy.</summary>
    public static Task Record(string workspace, BridgeEditAuthor author, BridgeEditObservation edit, DateTimeOffset? at = null)
    {
        var when = (at ?? DateTimeOffset.UtcNow).ToUniversalTime();
        try
        {
            if (BridgeEditDiff.TargetPath(edit) is not { } path || IsCoordinationFile(ProjectPath(workspace, path)))
                return Task.CompletedTask;
            var after = ReadSource(path);
            var exists = after is not null || File.Exists(path);
            if (after is not null || !exists)
                BridgeShellEdits.Find(edit.Cwd)?.Remember(Canonical(path), after, DateTime.UtcNow);
            var captured = edit with { Input = (System.Text.Json.Nodes.JsonObject)edit.Input.DeepClone(),
                Result = edit.Result?.DeepClone() as System.Text.Json.Nodes.JsonObject };
            lock (WriteGate)
                return _writes = _writes.ContinueWith(_ => RecordNow(workspace, author, captured, when, after, exists), CancellationToken.None,
                    TaskContinuationOptions.None, TaskScheduler.Default);
        }
        catch (Exception ex) { LastError = ex.Message; return Task.CompletedTask; }
    }

    private static void RecordNow(string workspace, BridgeEditAuthor author, BridgeEditObservation edit, DateTimeOffset at,
        string? after, bool exists)
    {
        try
        {
            if (BridgeEditDiff.Analyze(edit, _ => after, _ => exists) is not { } change) return;
            var file = ProjectPath(workspace, change.FullPath);
            if (IsCoordinationFile(file)) return;
            Append(workspace, new BridgeEditEntry
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                At = at,
                Run = author.RunId,
                AgentId = author.AgentId,
                Agent = author.Number,
                Label = author.Label,
                Provider = author.Provider,
                Model = string.IsNullOrWhiteSpace(author.Model) ? null : author.Model,
                Role = author.Role,
                Task = string.IsNullOrWhiteSpace(author.Task) ? null : author.Task,
                File = file, Change = change.Change, Ranges = change.Ranges.ToList(),
                Added = change.Added, Removed = change.Removed, Exact = change.Exact, Tool = edit.Tool,
                Uncertain = edit.Tool == "Shell" && (!edit.BeforeKnown || edit.Shared),
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
        var cutoff = now.ToUniversalTime() - Retention;
        var rows = new List<BridgeEditEntry>();
        // Read legacy project logs too, so upgrading does not erase the last seven days of attribution.
        foreach (var directory in new[] { LegacyDirectoryFor(workspace), DirectoryFor(workspace) })
        {
            try
            {
                if (!Directory.Exists(directory) || Redirected(directory) || Redirected(Path.GetDirectoryName(directory)!)) continue;
                for (var day = cutoff.UtcDateTime.Date; day <= now.UtcDateTime.Date; day = day.AddDays(1))
                {
                    try { rows.AddRange(LoadDay(Path.Combine(directory, DayFileName(day)), workspace).Where(e => e.At >= cutoff)); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { LastError = ex.Message; }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { LastError = ex.Message; }
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        rows.RemoveAll(e => e.Id.Length > 0 && !ids.Add(e.Id));
        // A shell command claims every file that changed while it ran, so an edit another agent's tool reports late
        // (after the command finished) was first claimed by that command as a guess. Once the real report is in, the
        // guess only misleads: it names the wrong author and knows no lines.
        var confirmed = rows.Where(e => !e.AuthorUncertain).ToLookup(e => e.File, StringComparer.OrdinalIgnoreCase);
        rows.RemoveAll(guess => guess.AuthorUncertain && confirmed[guess.File].Any(e => e.AgentId != guess.AgentId
            && e.At >= guess.At - ClaimSlack && e.At <= guess.At + LateReport));
        // Appends are chronological, so ties keep their order.
        return rows.Select((entry, index) => (entry, index)).OrderByDescending(x => x.entry.At)
            .ThenByDescending(x => x.index).Select(x => x.entry).ToList();
    }

    /// <summary>How late an edit tool's report may follow the write a peer's shell command already claimed.</summary>
    private static readonly TimeSpan LateReport = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ClaimSlack = TimeSpan.FromSeconds(5);

    private static IReadOnlyList<BridgeEditEntry> LoadDay(string path, string workspace)
    {
        var info = new FileInfo(path);
        if (!info.Exists || Redirected(path) || info.Length > MaxDayFileBytes * 2) { Cache.TryRemove(path, out _); return []; }
        if (Cache.TryGetValue(path, out var cached) && cached.Length == info.Length && cached.WrittenUtc == info.LastWriteTimeUtc)
            return cached.Entries;
        var entries = new List<BridgeEditEntry>();
        // Lines written before paths were resolved through links name some files by a second route; serve one name.
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
                    {
                        if (!names.TryGetValue(entry.File, out var name))
                            names[entry.File] = name = Path.IsPathRooted(entry.File) ? entry.File
                                : ProjectPath(workspace, Path.GetFullPath(Path.Combine(workspace, entry.File)));
                        entry.File = name;
                        entries.Add(entry);
                    }
                }
                catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException or PathTooLongException)
                {
                    /* A torn or foreign line never hides the rest of the day. */
                }
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

    /// <summary>The project-relative form agents see ("src/app.ts"); files outside the project keep their full path.
    /// The file's real location wins, so one file reached through a junction or symbolic link (C#\SRC\VibeCode.Desktop
    /// is a junction to C#\VibeCode.Desktop) is logged and found under one name, not two. A link leading out of the
    /// project keeps the name it has inside it.</summary>
    public static string ProjectPath(string workspace, string fullPath)
    {
        var root = Path.GetFullPath(workspace);
        return InProject(Canonical(root), Canonical(fullPath)) ?? InProject(root, fullPath) ?? fullPath.Replace('\\', '/');
    }

    private static string? InProject(string root, string fullPath)
    {
        var relative = Path.GetRelativePath(root, fullPath);
        return relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
               || Path.IsPathRooted(relative) ? null : relative.Replace('\\', '/');
    }

    private static readonly ConcurrentDictionary<string, string> CanonicalFolders = new(StringComparer.OrdinalIgnoreCase);

    /// <summary><paramref name="fullPath"/> with every junction or symbolic link along it replaced by its final target.
    /// Parts that don't exist (a deleted file) or can't be read are kept as written.</summary>
    internal static string Canonical(string fullPath)
    {
        try
        {
            var folder = Path.GetDirectoryName(fullPath);
            var resolvedFolder = string.IsNullOrEmpty(folder) ? folder : CanonicalFolder(folder);
            var path = string.IsNullOrEmpty(resolvedFolder) ? fullPath : Path.Combine(resolvedFolder, Path.GetFileName(fullPath));
            var file = new FileInfo(path);
            return file.Exists && file.LinkTarget is not null && file.ResolveLinkTarget(returnFinalTarget: true) is { } target
                ? target.FullName : path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return fullPath;
        }
    }

    private static string CanonicalFolder(string folder)
    {
        if (CanonicalFolders.TryGetValue(folder, out var known)) return known;
        var parent = Path.GetDirectoryName(folder);
        var resolved = string.IsNullOrEmpty(parent) ? folder : Path.Combine(CanonicalFolder(parent), Path.GetFileName(folder));
        var info = new DirectoryInfo(resolved);
        if (info.Exists && info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
            resolved = Path.TrimEndingDirectorySeparator(target.FullName);
        // Bounded: a long session can touch many folders, and a link retargeted mid-session is picked up after a clear.
        if (CanonicalFolders.Count > 4096) CanonicalFolders.Clear();
        CanonicalFolders[folder] = resolved;
        return resolved;
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
            # Agent edit history

            VibeCode writes one line here each time a chat, Bridge, Advanced Bridge or agent team edits a file: who (agent id, number,
            provider, model, role, task), which file (relative to the project), and which lines (where the change landed
            right after that edit). Agents read it with the `bridge_file_edits` bridge tool.

            - One JSON object per line, one file per UTC day: `edits-YYYY-MM-DD.jsonl`.
            - Entries older than 7 days are ignored, and a day file is deleted once everything in it is older than 7 days.
            - All chats for the same canonical project share this directory; workspace.json identifies the project.
            - Older project-local logs remain readable for their seven-day retention period.
            - Coordination history only: it never locks a file. File contents and credentials are not stored here.
            """);
        File.WriteAllText(Path.Combine(directory, "workspace.json"), JsonSerializer.Serialize(new
        {
            workspace = CanonicalFolder(Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace))),
        }, Json));
        return directory;
    }

    private static string DayFileName(DateTime utc) => "edits-" + utc.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + ".jsonl";

    private static bool Redirected(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}
