using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VibeCode.Services;

public sealed record MemoryRetentionReport(int Days, int TaskCheckpoints, int PinnedCards, int Suggestions, int ReviewMarks,
    int BridgeUserPrompts, int CliMemoryFiles, int PeerChatArchives, int BrainMemories = 0, int BrainSessions = 0)
{
    public int Total => TaskCheckpoints + PinnedCards + Suggestions + ReviewMarks + BridgeUserPrompts + CliMemoryFiles
                        + PeerChatArchives + BrainMemories + BrainSessions;

    public override string ToString() =>
        $"Removed memory and context data unused for {Days} days: {TaskCheckpoints} task checkpoints, {PinnedCards} pinned cards, " +
        $"{Suggestions} review suggestions, {ReviewMarks} review marks, {BridgeUserPrompts} Bridge prompts, {CliMemoryFiles} CLI memory files, " +
        $"{PeerChatArchives} peer-chat archives, {BrainMemories} Second Brain memories, {BrainSessions} Second Brain sessions.";
}

/// <summary>
/// The retention period (seven days unless the user changes it) for VibeCode's memory and context data only: task
/// checkpoints, pinned cards, review suggestions, Bridge prompts, CLI memory files in VibeCode's own folders, Bridge
/// peer-chat archives and, when the brain is already on and online, its stored memories. Chats and provider transcripts
/// are never deleted: every store is an app-owned folder, each is matched by the exact file names VibeCode writes there,
/// no reparse point is followed, and account homes are never enumerated. Runs off the UI thread at startup and every
/// <see cref="CheckInterval"/>; only counts are logged.
/// </summary>
public static class MemoryRetention
{
    public const int DefaultDays = 7;
    public const int MinimumDays = 1;
    public const int MaximumDays = 90;
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    private const int RegistryMaxBytes = 64 * 1024;
    private const int RegistryMaxWorkspaces = 200;

    private static readonly Regex CheckpointFile = new(@"^([0-9A-F]{64})\.json(?:\.bak|\.[0-9a-f]{32}\.tmp)?$", RegexOptions.CultureInvariant);
    private static readonly Regex PromptFile = new(@"^[0-9A-F]{64}\.json(?:\.[0-9a-f]{32}\.tmp)?$", RegexOptions.CultureInvariant);
    private static readonly Regex ArchiveFolder = new(@"^[0-9a-f]{32}$", RegexOptions.CultureInvariant);
    private static readonly Regex ArchiveFile = new(@"^(?:index\.json|[0-9a-fA-F]+\.json|[0-9a-f]{32}\.tmp|\.gitignore)$", RegexOptions.CultureInvariant);
    private static readonly ConcurrentDictionary<string, byte> LiveArchives = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object RegistryGate = new();
    private static Timer? _timer;
    private static int _sweeping;

    public static int Days => Math.Clamp(AppSettings.Current.MemoryRetentionDays, MinimumDays, MaximumDays);
    public static string TaskCheckpointDirectory => Path.Combine(AppSettings.Dir, "task-checkpoints");
    private static string RegistryPath => Path.Combine(MemoryContextFiles.Root, "archive-workspaces.json");

    public static void Start()
    {
        if (_timer is not null) return;
        // The first sweep waits until startup has settled; the timer thread keeps all disk work off the dispatcher.
        _timer = new Timer(_ => _ = RunScheduledAsync(), null, TimeSpan.FromSeconds(30), CheckInterval);
    }

    private static async Task RunScheduledAsync()
    {
        if (Interlocked.Exchange(ref _sweeping, 1) == 1) return;
        try
        {
            var now = DateTimeOffset.UtcNow;
            var report = Sweep(now);
            if (AppSettings.Current.SecondBrainEnabled)
            {
                var (memories, sessions) = await AgentMemoryService.Instance.ForgetOlderThanAsync(now - TimeSpan.FromDays(report.Days))
                    .ConfigureAwait(false);
                report = report with { BrainMemories = memories, BrainSessions = sessions };
            }
            if (report.Total > 0) CrashLog.Note("MemoryRetention", report.ToString());
        }
        catch (Exception ex) { CrashLog.Note("MemoryRetention", "Sweep stopped early: " + ex.Message); }
        finally { Volatile.Write(ref _sweeping, 0); }
    }

    /// <summary>Apply the retention period to every file store as of <paramref name="now"/>.</summary>
    public static MemoryRetentionReport Sweep(DateTimeOffset now)
    {
        var days = Days;
        var cutoff = now - TimeSpan.FromDays(days);
        return new MemoryRetentionReport(days,
            TaskCheckpoints: Guard(() => RemoveTaskCheckpoints(cutoff)),
            PinnedCards: Guard(() => PinnedMemory.RemoveUnusedSince(cutoff)),
            Suggestions: Guard(() => MemorySuggestionInbox.RemoveOlderThan(cutoff)),
            ReviewMarks: Guard(() => MemoryReviewService.RemoveMarksOlderThan(cutoff)),
            BridgeUserPrompts: Guard(() => RemoveFiles(BridgeUserPromptStore.DirectoryPath, PromptFile, cutoff)),
            CliMemoryFiles: Guard(() => CliNativeMemory.RemoveExpired(cutoff)),
            PeerChatArchives: Guard(() => RemovePeerChatArchives(cutoff)));
    }

    /// <summary>One store failing (a locked file, a permissions change) must not stop the others.</summary>
    private static int Guard(Func<int> sweep)
    {
        try { return sweep(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException) { return 0; }
    }

    /// <summary>A checkpoint and its backup revision go together, judged by the newer of the two.</summary>
    private static int RemoveTaskCheckpoints(DateTimeOffset cutoff)
    {
        var directory = TaskCheckpointDirectory;
        if (!Directory.Exists(directory) || CliNativeMemory.IsReparse(directory)) return 0;
        var removed = 0;
        foreach (var group in Directory.EnumerateFiles(directory)
                     .Select(path => (Path: path, Match: CheckpointFile.Match(Path.GetFileName(path))))
                     .Where(file => file.Match.Success && !CliNativeMemory.IsReparse(file.Path))
                     .GroupBy(file => file.Match.Groups[1].Value, StringComparer.Ordinal))
        {
            if (group.Max(file => File.GetLastWriteTimeUtc(file.Path)) >= cutoff.UtcDateTime) continue;
            foreach (var file in group) File.Delete(file.Path);
            removed++;
        }
        return removed;
    }

    private static int RemoveFiles(string directory, Regex names, DateTimeOffset cutoff)
    {
        if (!Directory.Exists(directory) || CliNativeMemory.IsReparse(directory)) return 0;
        var removed = 0;
        foreach (var path in Directory.EnumerateFiles(directory).ToList())
        {
            if (!names.IsMatch(Path.GetFileName(path)) || CliNativeMemory.IsReparse(path)) continue;
            if (File.GetLastWriteTimeUtc(path) >= cutoff.UtcDateTime) continue;
            File.Delete(path);
            removed++;
        }
        return removed;
    }

    /// <summary>Remember a workspace that holds peer-chat archives. Recent-folder history forgets idle folders after
    /// seven days, exactly when their archives fall due, so the sweep keeps its own short list.</summary>
    public static void RegisterArchiveWorkspace(string workspace, string archiveDirectory)
    {
        LiveArchives[Path.GetFullPath(archiveDirectory)] = 0;
        lock (RegistryGate)
        {
            try
            {
                var root = MemoryContextFiles.ProjectKey(workspace);
                var known = MemoryContextFiles.Read<List<string>>(RegistryPath, RegistryMaxBytes) ?? [];
                if (known.Contains(root, StringComparer.OrdinalIgnoreCase)) return;
                known.Insert(0, root);
                MemoryContextFiles.Write(RegistryPath, known.Take(RegistryMaxWorkspaces).ToList(), RegistryMaxBytes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException) { }
        }
    }

    private static int RemovePeerChatArchives(DateTimeOffset cutoff)
    {
        List<string> workspaces;
        lock (RegistryGate) workspaces = MemoryContextFiles.Read<List<string>>(RegistryPath, RegistryMaxBytes) ?? [];
        var removed = 0;
        var stillUsed = new List<string>();
        foreach (var workspace in workspaces)
        {
            var archives = Path.Combine(workspace, ".vibecode", "bridge-chats");
            if (!Directory.Exists(archives) || CliNativeMemory.IsReparse(Path.Combine(workspace, ".vibecode"))
                || CliNativeMemory.IsReparse(archives)) continue;
            var remaining = false;
            foreach (var archive in Directory.EnumerateDirectories(archives).ToList())
            {
                if (!ArchiveFolder.IsMatch(Path.GetFileName(archive))) continue;
                if (LiveArchives.ContainsKey(Path.GetFullPath(archive)) || !IsArchiveExpired(archive, cutoff) || !RemoveArchive(archive))
                {
                    remaining = true;
                    continue;
                }
                removed++;
            }
            if (remaining) stillUsed.Add(workspace);
        }
        if (stillUsed.Count != workspaces.Count)
            lock (RegistryGate)
            {
                // Keep folders registered since the sweep began.
                var current = MemoryContextFiles.Read<List<string>>(RegistryPath, RegistryMaxBytes) ?? [];
                var dropped = workspaces.Except(stillUsed, StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
                MemoryContextFiles.Write(RegistryPath, current.Where(path => !dropped.Contains(path)).ToList(), RegistryMaxBytes);
            }
        return removed;
    }

    /// <summary>Only a folder BridgeChatStore wrote: its index names this archive, and nothing inside is redirected.</summary>
    private static bool IsArchiveExpired(string archive, DateTimeOffset cutoff)
    {
        if (CliNativeMemory.IsReparse(archive)) return false;
        var index = Path.Combine(archive, "index.json");
        if (!File.Exists(index) || CliNativeMemory.IsReparse(index) || new FileInfo(index).Length > 4 * 1024 * 1024) return false;
        using (var document = JsonDocument.Parse(File.ReadAllText(index)))
            if (!document.RootElement.TryGetProperty("archiveId", out var owner) || owner.ValueKind != JsonValueKind.String
                || owner.GetString() != Path.GetFileName(archive)) return false;
        var entries = Directory.EnumerateFileSystemEntries(archive, "*", SearchOption.AllDirectories).ToList();
        if (entries.Any(CliNativeMemory.IsReparse)) return false;
        return entries.Where(File.Exists).All(file => File.GetLastWriteTimeUtc(file) < cutoff.UtcDateTime);
    }

    /// <summary>Delete only the files an archive is made of; anything else keeps the folder (and is left alone).</summary>
    private static bool RemoveArchive(string archive)
    {
        var skill = Path.Combine(archive, "bridge-peer-chats");
        foreach (var file in new[] { Path.Combine(skill, "SKILL.md"), Path.Combine(skill, "scripts", "query-peer-chats.ps1") })
            if (File.Exists(file)) File.Delete(file);
        foreach (var file in Directory.EnumerateFiles(archive).Where(path => ArchiveFile.IsMatch(Path.GetFileName(path))).ToList())
            File.Delete(file);
        foreach (var folder in new[] { Path.Combine(skill, "scripts"), skill, archive })
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        return !Directory.Exists(archive);
    }
}
