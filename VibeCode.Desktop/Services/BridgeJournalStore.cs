using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VibeCode.Services;

/// <summary>
/// Saved Bridges' task journals (<see cref="SavedBridgeState.Work"/>), one small file per Bridge in
/// %APPDATA%\VibeCode\bridges\. They used to be embedded in settings.json, where three busy Advanced Bridge journals
/// were ~330 KB of a ~725 KB document that every save re-read, re-serialized and rewrote on the UI thread. Kept apart,
/// a journal is rewritten only when it changed, and SaveWork's "on disk before dispatch" rule costs one small file
/// instead of the whole settings document.
/// <para>Every write is atomic: a unique temp file, flushed to disk, then File.Replace, which also rotates the previous
/// copy into <c>.bak.json</c>. A crash leaves the old journal or the new one, never a torn one.</para>
/// </summary>
public static class BridgeJournalStore
{
    public static string DirectoryPath => Path.Combine(AppSettings.Dir, "bridges");

    /// <summary>The journal file of one saved Bridge. Named by a hash of its provider and host session, so any session
    /// id gives a valid fixed-length name, and keys that compare equal ignoring case share one file.</summary>
    public static string PathFor(string? hostSessionId, string? provider) =>
        Path.Combine(DirectoryPath, Stem(hostSessionId, provider) + ".json");

    /// <summary>One journal as read back. The Work JSON stays bytes, so a dormant Bridge's journal is never parsed into
    /// objects unless that Bridge is actually reopened.</summary>
    internal sealed record Journal(byte[] Work, DateTime SavedAt, string? RunId);

    /// <summary>The journal, from the main file or else from its previous copy. Null when neither exists or parses.</summary>
    internal static Journal? TryRead(string? hostSessionId, string? provider)
    {
        var path = PathFor(hostSessionId, provider);
        return TryReadFile(path, hostSessionId, provider) ?? TryReadFile(BackupPathFor(path), hostSessionId, provider);
    }

    /// <summary>Publish one Bridge's journal. Throws when it could not be written: the caller decides whether that
    /// blocks dispatch (SaveWork) or fails the save it belongs to.</summary>
    internal static void Write(string? hostSessionId, string? provider, DateTime savedAt, byte[] work)
    {
        var path = PathFor(hostSessionId, provider);
        Directory.CreateDirectory(DirectoryPath);
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var writer = new Utf8JsonWriter(stream))
                {
                    writer.WriteStartObject();
                    writer.WriteString("Provider", ProviderOrDefault(provider));
                    writer.WriteString("HostSessionId", hostSessionId);
                    writer.WriteString("SavedAt", savedAt);
                    writer.WritePropertyName("Work");
                    writer.WriteRawValue(work, skipInputValidation: true);   // already serialized by the snapshot
                    writer.WriteEndObject();
                }
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path))
            {
                try { File.Replace(tmp, path, BackupPathFor(path), ignoreMetadataErrors: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
                {
                    File.Move(tmp, path, overwrite: true);   // Replace needs both files intact; Move always works
                }
            }
            else File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* the load-time sweep removes it later */ }
            throw;
        }
    }

    /// <summary>Delete a journal and its previous copy. A file that is locked right now is left for the load-time sweep.</summary>
    internal static void Delete(string path)
    {
        foreach (var file in new[] { path, BackupPathFor(path) })
            try { if (File.Exists(file)) File.Delete(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Remove journals no saved Bridge points at any more, and temp files stranded by a crash mid-write. Temps go
    /// after an hour (a fresh one may belong to a write under way). Journals wait a month: removing or pruning a Bridge
    /// deletes its journal straight away, so an orphan only comes from a crash or an older build, and a quarantined
    /// settings file the user may still recover by hand points at it. Returns how many files were removed.</summary>
    internal static int SweepOrphans(IEnumerable<SavedBridgeState> bridges)
    {
        var live = bridges.Select(bridge => Stem(bridge.HostSessionId, bridge.Provider)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = 0;
        try
        {
            if (!Directory.Exists(DirectoryPath)) return 0;
            foreach (var file in Directory.EnumerateFiles(DirectoryPath))
            {
                var name = Path.GetFileName(file);
                // Only names this store writes: <stem>.json, <stem>.bak.json and <stem>.json.<guid>.tmp.
                if (name.Length < StemLength + 5 || !name[..StemLength].All(Uri.IsHexDigit) || name[StemLength] != '.') continue;
                var temp = name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
                if (!temp && live.Contains(name[..StemLength])) continue;
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < (temp ? TimeSpan.FromHours(1) : TimeSpan.FromDays(30))) continue;
                try { File.Delete(file); removed++; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* unreadable folder: nothing to sweep */ }
        return removed;
    }

    private const int StemLength = 32;

    private static string Stem(string? hostSessionId, string? provider) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            AppSettings.BridgeKey(hostSessionId, provider).ToUpperInvariant())), 0, StemLength / 2).ToLowerInvariant();

    private static string BackupPathFor(string path) => Path.ChangeExtension(path, ".bak.json");

    private static string ProviderOrDefault(string? provider) => string.IsNullOrWhiteSpace(provider) ? "claude" : provider;

    private static Journal? TryReadFile(string path, string? hostSessionId, string? provider)
    {
        try
        {
            if (!File.Exists(path)) return null;
            byte[]? bytes = null;
            // A scanner or indexer holding the file is momentary; a journal must not read as missing because of it.
            for (var attempt = 0; bytes is null; attempt++)
            {
                try { bytes = File.ReadAllBytes(path); }
                catch (IOException) when (attempt < 3) { Thread.Sleep(25); }
            }
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            // The name is only a hash; the identity inside is what proves the file belongs to this Bridge.
            if (!string.Equals(root.GetProperty("HostSessionId").GetString(), hostSessionId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(root.GetProperty("Provider").GetString(), ProviderOrDefault(provider), StringComparison.OrdinalIgnoreCase))
                return null;
            var work = root.GetProperty("Work");
            if (work.ValueKind != JsonValueKind.Object) return null;
            return new Journal(Encoding.UTF8.GetBytes(work.GetRawText()), root.GetProperty("SavedAt").GetDateTime(),
                work.TryGetProperty(nameof(BridgeWorkState.RunId), out var run) && run.ValueKind == JsonValueKind.String ? run.GetString() : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                       or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return null;   // unreadable: the caller tries the previous copy, then whatever settings.json carried
        }
    }
}
