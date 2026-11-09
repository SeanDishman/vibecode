using System.IO;
using System.Text.Json;

namespace VibeCode.Services;

/// <summary>Small JSON files behind the memory and context features (pinned cards, review inbox, Bridge prompts).
/// Each is size-capped and replaced atomically, so a crash mid-write leaves the previous copy rather than half a file.</summary>
internal static class MemoryContextFiles
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Not "Memory": that folder belongs to the agentmemory sidecar, whose "Delete all memories" clears it.</summary>
    public static string Root => Path.Combine(AppSettings.Dir, "memory-context");

    public static T? Read<T>(string path, int maximumBytes) where T : class
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > maximumBytes || IsRedirected(path)) return null;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    public static void Write<T>(string path, T value, int maximumBytes)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length > maximumBytes) throw new InvalidDataException($"{Path.GetFileName(path)} would exceed its {maximumBytes / 1024} KB limit.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (IsRedirected(path)) throw new IOException($"{Path.GetFileName(path)} is redirected; VibeCode only writes its own files.");
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static bool IsRedirected(string path)
    {
        try { return File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    /// <summary>The comparable form of a project folder: full path, no trailing separator.</summary>
    public static string ProjectKey(string cwd) => Path.GetFullPath(cwd).TrimEnd('\\', '/');
}
