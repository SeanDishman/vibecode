using System.Collections.Concurrent;
using System.IO;

namespace VibeCode.Services;

/// <summary>
/// Finds files a Bridge agent changed through a shell command (sed, Set-Content, a script), which no edit tool reports.
/// One watcher per project folder notes which source files changed and when; each shell command then claims the
/// changes made while it ran. The first command to finish claims a change, so a quick edit is not credited to a peer's
/// long build that happened to be running. Changes an edit tool reported are left to that tool's own log entry.
/// The text a file had before is remembered after every logged edit, so the next diff covers only the new change.
/// </summary>
public sealed class BridgeShellEdits
{
    private static readonly ConcurrentDictionary<string, BridgeShellEdits> Trackers = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>A fast command can write before its tool card reaches the UI, so a window reaches back this far.</summary>
    private static readonly TimeSpan Lead = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan Keep = TimeSpan.FromMinutes(30);
    /// <summary>An edit tool's own write lands just before its result is reported; allow for a little delivery lag.</summary>
    private static readonly TimeSpan ReportSlack = TimeSpan.FromMilliseconds(300);
    private const int MaxChanges = 20_000;
    private const int MaxFilesPerCommand = 50;
    private const int MaxRemembered = 256;
    private const int MaxRememberedChars = 1_000_000;

    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", ".vibecode", ".codex", ".claude", "bin", "obj", "node_modules", "packages", "dist", "build",
        "target", ".next", "coverage", ".cache", ".venv", "venv", "__pycache__",
    };
    private static readonly HashSet<string> IgnoredExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dll", ".exe", ".pdb", ".so", ".dylib", ".lib", ".a", ".o", ".obj", ".class", ".jar", ".pyc", ".log", ".tmp",
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp", ".mp3", ".mp4", ".wav", ".zip", ".7z", ".gz", ".tar",
        ".nupkg", ".sqlite", ".db", ".sqlite-wal", ".sqlite-shm", ".lock", ".cache",
    };

    private readonly object _gate = new();
    private readonly string _root;
    private readonly FileSystemWatcher? _watcher;
    private readonly List<(DateTime At, string Path)> _changes = [];
    private readonly Dictionary<(object Owner, string ToolId), DateTime> _open = new();
    private readonly List<(DateTime At, string Path)> _reported = [];
    private readonly Dictionary<string, DateTime> _claimed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (DateTime At, string? Text)> _remembered = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The project's tracker, starting its watcher on first use.</summary>
    public static BridgeShellEdits For(string workspace) => Trackers.GetOrAdd(Path.GetFullPath(workspace), root => new BridgeShellEdits(root));

    /// <summary>The project's tracker only if one is already running.</summary>
    public static BridgeShellEdits? Find(string workspace)
    {
        try { return Trackers.TryGetValue(Path.GetFullPath(workspace), out var tracker) ? tracker : null; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private BridgeShellEdits(string root)
    {
        _root = Path.TrimEndingDirectorySeparator(root);
        try
        {
            _watcher = new FileSystemWatcher(_root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
            _watcher.Created += (_, e) => Note(e.FullPath);
            _watcher.Changed += (_, e) => Note(e.FullPath);
            _watcher.Deleted += (_, e) => Note(e.FullPath);
            _watcher.Renamed += (_, e) => { Note(e.OldFullPath); Note(e.FullPath); };
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // No watcher (missing or unwatchable folder): shell edits simply go unlogged; edit tools still log.
            _watcher = null;
        }
    }

    /// <summary>A shell command started in this project.</summary>
    public void Open(object owner, string toolId)
    {
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            Prune(now);
            _open[(owner, toolId)] = now;
        }
    }

    /// <summary>Forget a window without claiming anything (replayed history).</summary>
    public void Discard(object owner, string toolId)
    {
        lock (_gate) _open.Remove((owner, toolId));
    }

    /// <summary>An edit tool reported a change to this file; the shell windows that saw it must not claim it too.</summary>
    public void NoteReported(string fullPath)
    {
        lock (_gate) _reported.Add((DateTime.UtcNow, Path.GetFullPath(fullPath)));
    }

    /// <summary>A shell command finished: claim the source files that changed while it ran and nobody has claimed yet.
    /// Shared is true when another agent's shell command was running too, so the author is not certain.</summary>
    public IReadOnlyList<(string Path, bool Shared)> Claim(object owner, string toolId)
    {
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            var from = (_open.Remove((owner, toolId), out var opened) ? opened : now) - Lead;
            var claims = new List<(string, bool)>();
            foreach (var file in _changes.Where(c => c.At >= from && c.At <= now).GroupBy(c => c.Path, StringComparer.OrdinalIgnoreCase))
            {
                // Writes up to an edit tool's report belong to that tool's own log entry; only later writes are this
                // command's. A file already claimed by a command that finished earlier is skipped up to that claim.
                var claimedUntil = _claimed.GetValueOrDefault(file.Key, DateTime.MinValue);
                var reportedUntil = _reported.Where(r => string.Equals(r.Path, file.Key, StringComparison.OrdinalIgnoreCase))
                    .Select(r => r.At + ReportSlack).DefaultIfEmpty(DateTime.MinValue).Max();
                var since = claimedUntil > reportedUntil ? claimedUntil : reportedUntil;
                var mine = file.Where(c => c.At > since).ToArray();
                _claimed[file.Key] = file.Max(c => c.At) > claimedUntil ? file.Max(c => c.At) : claimedUntil;
                if (mine.Length == 0) continue;
                var shared = _open.Any(o => !ReferenceEquals(o.Key.Owner, owner) && o.Value - Lead <= mine.Max(c => c.At));
                claims.Add((file.Key, shared));
                if (claims.Count == MaxFilesPerCommand) break;
            }
            return claims;
        }
    }

    /// <summary>Remember a file's text right after a logged edit; the next shell diff starts from it.</summary>
    public void Remember(string fullPath, string? text, DateTime atUtc)
    {
        lock (_gate)
        {
            if (text is { Length: > MaxRememberedChars }) { _remembered.Remove(fullPath); return; }
            _remembered[fullPath] = (atUtc, text);
            if (_remembered.Count > MaxRemembered)
                _remembered.Remove(_remembered.MinBy(pair => pair.Value.At).Key);
        }
    }

    public bool TryRecall(string fullPath, out DateTime atUtc, out string? text)
    {
        lock (_gate)
        {
            var found = _remembered.TryGetValue(fullPath, out var known);
            (atUtc, text) = found ? known : (default, null);
            return found;
        }
    }

    private void Note(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return; }
        if (!IsSource(full)) return;
        lock (_gate)
        {
            _changes.Add((DateTime.UtcNow, full));
            if (_changes.Count > MaxChanges) _changes.RemoveRange(0, _changes.Count - MaxChanges);
        }
    }

    private bool IsSource(string full)
    {
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
        var parts = full[(_root.Length + 1)..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (parts.Length == 0 || string.Equals(parts[^1], ".vibecode-bridge.md", StringComparison.OrdinalIgnoreCase)) return false;
        if (IgnoredExtensions.Contains(Path.GetExtension(parts[^1]))) return false;
        return !parts[..^1].Any(part => IgnoredDirectories.Contains(part)
            || part.StartsWith("bin-", StringComparison.OrdinalIgnoreCase) || part.StartsWith("obj-", StringComparison.OrdinalIgnoreCase)
            || part.StartsWith(".tmp-", StringComparison.OrdinalIgnoreCase));
    }

    private void Prune(DateTime now)
    {
        var horizon = now - Keep;
        _changes.RemoveAll(c => c.At < horizon);
        _reported.RemoveAll(r => r.At < horizon);
        foreach (var stale in _claimed.Where(c => c.Value < horizon).Select(c => c.Key).ToList()) _claimed.Remove(stale);
        // A command whose result never arrived (stopped turn, crashed provider) must not stay open forever.
        foreach (var stale in _open.Where(o => o.Value < now - TimeSpan.FromHours(2)).Select(o => o.Key).ToList()) _open.Remove(stale);
    }
}
