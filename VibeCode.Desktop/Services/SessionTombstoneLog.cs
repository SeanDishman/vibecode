using System.IO;
using System.Text;

namespace VibeCode.Services;

/// <summary>
/// Deleted-chat tombstones (<see cref="AppSettings.DeletedSessions"/>) as an append-only text file,
/// %APPDATA%\VibeCode\deleted-sessions.txt, one session id per line. A tombstone is only ever added, so deleting a chat
/// appends one short line instead of every settings.json save rewriting ~1,500 ids. Duplicate, blank and torn lines
/// are dropped when the file is compacted at load.
/// </summary>
internal sealed class SessionTombstoneLog
{
    private const string Header = "# VibeCode deleted chats: one session id per line. Append-only; these chats stay hidden.";

    public static string FilePath => Path.Combine(AppSettings.Dir, "deleted-sessions.txt");

    /// <summary>The ids this instance knows the file holds - what an append does not need to write again.</summary>
    private readonly HashSet<string> _onDisk = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The file as this instance last read or wrote it. Null = unknown, which counts as changed.</summary>
    private FileStamp? _stamp;

    /// <summary>Someone else appended to (or compacted) the file since this instance last read or wrote it.</summary>
    public bool ChangedOnDisk => _stamp is null || FileStamp.Of(FilePath) is not { } now || now != _stamp;

    /// <summary>Every tombstone in the file. Compacts it when it carries duplicates, blanks or a line torn by a crash.</summary>
    public HashSet<string> Load()
    {
        SweepStaleTemps();
        var stamp = FileStamp.Of(FilePath);   // before the read: an append landing during it must still look new
        var ids = ReadAll(out var redundant);
        if (redundant)
        {
            Compact(ids);
            stamp = FileStamp.Of(FilePath);
        }
        _stamp = stamp;
        return ids;
    }

    /// <summary>Re-read after another writer changed the file. Also re-baselines what this instance believes is on
    /// disk, so an id that someone else's compaction dropped is appended again by the next save.</summary>
    public HashSet<string> Reload()
    {
        var stamp = FileStamp.Of(FilePath);   // before the read: an append landing during it must still look new
        var ids = ReadAll(out _);
        _stamp = stamp;
        return ids;
    }

    /// <summary>Append the tombstones the file does not hold yet, flushed to disk. Throws when that fails: settings.json
    /// may only stop carrying a tombstone once it is safely here. Returns how many were appended.</summary>
    public int Append(IEnumerable<string> ids)
    {
        // A line break cannot live inside one line; no provider issues such ids, and they stay in memory regardless.
        var missing = ids.Where(id => !string.IsNullOrWhiteSpace(id) && id.IndexOfAny(['\r', '\n']) < 0 && !_onDisk.Contains(id))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (missing.Count == 0) return 0;
        Directory.CreateDirectory(AppSettings.Dir);
        var text = new StringBuilder();
        // FileShare.Read: two writers never interleave inside one append.
        using (var stream = new FileStream(FilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
        {
            if (stream.Length == 0) text.Append(Header).Append('\n');
            else
            {
                stream.Seek(-1, SeekOrigin.End);
                if (stream.ReadByte() != '\n') text.Append('\n');   // close a line torn by a crash mid-append
            }
            foreach (var id in missing) text.Append(id).Append('\n');
            stream.Seek(0, SeekOrigin.End);
            stream.Write(Encoding.UTF8.GetBytes(text.ToString()));
            stream.Flush(flushToDisk: true);
        }
        _onDisk.UnionWith(missing);
        _stamp = FileStamp.Of(FilePath);
        return missing.Count;
    }

    private HashSet<string> ReadAll(out bool redundant)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        redundant = false;
        string? text = null;
        // A sharing violation from a writer mid-append is momentary; give it a moment before treating the file as unreadable.
        for (var attempt = 0; text is null && attempt < 4; attempt++)
        {
            try { text = File.Exists(FilePath) ? File.ReadAllText(FilePath, Encoding.UTF8) : ""; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Thread.Sleep(25); }
        }
        // Still unreadable: adopt nothing and keep what this instance believed. Nothing is ever truncated from here, so
        // the file keeps every id for the next launch even if this one cannot see them.
        if (text is null) return ids;

        var lines = text.Split('\n');
        redundant = text.Length > 0 && (!text.StartsWith(Header + "\n", StringComparison.Ordinal) || !text.EndsWith('\n'));
        for (var i = 0; i < lines.Length; i++)
        {
            var id = lines[i].Trim();
            if (i == lines.Length - 1 && id.Length == 0) continue;   // the empty remainder after the final newline
            if (id.Length == 0 || id.StartsWith('#')) { redundant |= i > 0 || id.Length == 0; continue; }
            if (!ids.Add(id)) redundant = true;
        }
        _onDisk.Clear();
        _onDisk.UnionWith(ids);
        return ids;
    }

    /// <summary>Rewrite the file with each id once. Failure is harmless: the old file still holds every id.</summary>
    private static void Compact(IEnumerable<string> ids)
    {
        var tmp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var text = new StringBuilder(Header).Append('\n');
                foreach (var id in ids) text.Append(id).Append('\n');
                stream.Write(Encoding.UTF8.GetBytes(text.ToString()));
                stream.Flush(flushToDisk: true);
            }
            File.Replace(tmp, FilePath, null, ignoreMetadataErrors: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* swept at the next load */ }
        }
    }

    // A crash between writing a compaction temp and the Replace strands it; only old ones, since a fresh temp may
    // belong to another instance mid-compaction.
    private static void SweepStaleTemps()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(AppSettings.Dir, Path.GetFileName(FilePath) + ".*.tmp"))
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > TimeSpan.FromHours(1))
                    try { File.Delete(file); } catch { /* best effort */ }
        }
        catch { /* no folder yet / unreadable - nothing to sweep */ }
    }
}

/// <summary>Size and last-write time of a file: enough to tell "still exactly what this instance wrote" from "another
/// writer touched it". <see cref="Missing"/> stands for a file that does not exist; null means it could not be read.</summary>
internal sealed record FileStamp(long Length, long LastWriteUtcTicks)
{
    public static readonly FileStamp Missing = new(-1, 0);

    public static FileStamp? Of(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? new FileStamp(info.Length, info.LastWriteTimeUtc.Ticks) : Missing;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
