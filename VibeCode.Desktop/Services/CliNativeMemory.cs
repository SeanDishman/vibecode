using System.IO;
using System.Text.RegularExpressions;

namespace VibeCode.Services;

/// <summary>One CLI memory file VibeCode owns, as the Second Brain's "CLI memories" tab lists it.</summary>
public sealed record CliMemoryFile(string Provider, string Project, string Path, string Name, long Bytes, DateTimeOffset Modified);

/// <summary>
/// The CLIs' own memories, kept in VibeCode-owned folders. Claude Code writes auto memory under its config directory, and
/// VibeCode gives every Claude account its own CLAUDE_CONFIG_DIR, so one project's memory was split across accounts and
/// a chat moved to another account forgot the project. Claude gets one folder per project through autoMemoryDirectory
/// in the session-scoped --settings; Codex keeps one memories folder per CODEX_HOME, so every account home links to one
/// shared folder. An account's existing memory is copied in once, never moved: nothing in an account home is changed.
/// </summary>
public static class CliNativeMemory
{
    public const string IndexFileName = "MEMORY.md";
    private const string ImportMarker = ".vibecode-imported.json";
    private const int ImportMaxFiles = 200;
    private const long ImportMaxBytes = 2 * 1024 * 1024;

    public static string Root => Path.Combine(AppSettings.Dir, "cli-memory");
    public static string ClaudeRoot => Path.Combine(Root, "claude");
    public static string CodexDirectory => Path.Combine(Root, "codex", "memories");

    /// <summary>Claude's own folder name for a project: every character outside [A-Za-z0-9] becomes '-'.</summary>
    public static string ClaudeProjectKey(string cwd) =>
        Regex.Replace(MemoryContextFiles.ProjectKey(cwd), "[^a-zA-Z0-9]", "-");

    public static string ClaudeDirectoryFor(string cwd) => Path.Combine(ClaudeRoot, ClaudeProjectKey(cwd));

    /// <summary>The project's memory folder for a Claude launch, created on first use with a copy of what
    /// <paramref name="configDirectory"/> (null: the CLI's default home) already remembered for this project.
    /// Null when the folder cannot be prepared; the CLI then keeps its default location.</summary>
    public static string? PrepareClaude(string cwd, string? configDirectory, DateTimeOffset now)
    {
        try
        {
            var target = ClaudeDirectoryFor(cwd);
            Directory.CreateDirectory(target);
            var home = string.IsNullOrWhiteSpace(configDirectory) ? DefaultClaudeHome() : Path.GetFullPath(configDirectory);
            ImportOnce(Path.Combine(home, "projects", ClaudeProjectKey(cwd), "memory"), target, now);
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The one shared Codex memories folder, seeded once from each existing source folder (copy, never move).</summary>
    public static string? PrepareCodex(IEnumerable<string> sources, DateTimeOffset now)
    {
        try
        {
            Directory.CreateDirectory(CodexDirectory);
            foreach (var source in sources) ImportOnce(source, CodexDirectory, now);
            return CodexDirectory;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string DefaultClaudeHome() =>
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } configured
            ? Path.GetFullPath(configured.Trim('"'))
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    /// <summary>Copy <paramref name="source"/>'s memory files into <paramref name="target"/> once per source. Existing
    /// files win; a second account's index lines are appended. Copies are dated now, so retention counts the seven days
    /// from when VibeCode took the memory into its care rather than deleting a whole history on the next sweep.</summary>
    private static void ImportOnce(string source, string target, DateTimeOffset now)
    {
        if (!Directory.Exists(source) || IsReparse(source) || SamePath(source, target)) return;
        var markerPath = Path.Combine(target, ImportMarker);
        var imported = MemoryContextFiles.Read<List<string>>(markerPath, 64 * 1024) ?? [];
        var key = Path.GetFullPath(source).TrimEnd('\\', '/');
        if (imported.Contains(key, StringComparer.OrdinalIgnoreCase)) return;
        long bytes = 0;
        var files = 0;
        foreach (var file in MarkdownFiles(source))
        {
            var info = new FileInfo(file);
            if (files >= ImportMaxFiles || bytes + info.Length > ImportMaxBytes) break;
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (!File.Exists(destination)) File.Copy(file, destination);
            else if (string.Equals(Path.GetFileName(file), IndexFileName, StringComparison.OrdinalIgnoreCase))
            {
                var present = File.ReadAllLines(destination).ToHashSet(StringComparer.Ordinal);
                var added = File.ReadAllLines(file).Where(line => line.Trim().Length > 0 && !present.Contains(line)).ToArray();
                if (added.Length > 0) File.AppendAllLines(destination, added);
            }
            else continue;
            File.SetLastWriteTimeUtc(destination, now.UtcDateTime);
            bytes += info.Length;
            files++;
        }
        imported.Add(key);
        MemoryContextFiles.Write(markerPath, imported, 64 * 1024);
    }

    /// <summary>Every VibeCode-owned CLI memory file, newest first, for the Second Brain window.</summary>
    public static IReadOnlyList<CliMemoryFile> List()
    {
        var result = new List<CliMemoryFile>();
        if (Directory.Exists(ClaudeRoot) && !IsReparse(ClaudeRoot))
            foreach (var project in Directory.EnumerateDirectories(ClaudeRoot).Where(path => !IsReparse(path)))
                result.AddRange(MarkdownFiles(project).Select(file => Describe("claude", Path.GetFileName(project), file)));
        if (Directory.Exists(CodexDirectory) && !IsReparse(CodexDirectory))
            result.AddRange(MarkdownFiles(CodexDirectory).Select(file => Describe("codex", "all projects", file)));
        return result.OrderByDescending(file => file.Modified).ToList();
    }

    /// <summary>True only for a memory file inside VibeCode's own CLI memory folders, so the editor can never be
    /// pointed at a transcript or an account home.</summary>
    public static bool IsOwnedFile(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(Root).TrimEnd('\\') + "\\";
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                   && full.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                   && !IsReparse(full) && !IsReparse(Path.GetDirectoryName(full)!);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return false; }
    }

    /// <summary>Delete memory topic files untouched since <paramref name="cutoff"/> and drop index lines that point at
    /// files no longer there. Only *.md files inside <see cref="Root"/> are ever considered. Returns files deleted.</summary>
    public static int RemoveExpired(DateTimeOffset cutoff)
    {
        if (!Directory.Exists(Root) || IsReparse(Root)) return 0;
        var folders = new List<string>();
        if (Directory.Exists(ClaudeRoot) && !IsReparse(ClaudeRoot))
            folders.AddRange(Directory.EnumerateDirectories(ClaudeRoot).Where(path => !IsReparse(path)));
        if (Directory.Exists(CodexDirectory) && !IsReparse(CodexDirectory)) folders.Add(CodexDirectory);
        var deleted = 0;
        foreach (var folder in folders)
        {
            foreach (var file in MarkdownFiles(folder).ToList())
            {
                if (string.Equals(Path.GetFileName(file), IndexFileName, StringComparison.OrdinalIgnoreCase)) continue;
                if (File.GetLastWriteTimeUtc(file) >= cutoff.UtcDateTime) continue;
                File.Delete(file);
                deleted++;
            }
            deleted += PruneIndex(folder, cutoff);
            RemoveEmptyDirectories(folder, keepRoot: folder == CodexDirectory);
        }
        return deleted;
    }

    /// <summary>Drop index lines linking to a missing file. Pruning is upkeep rather than use, so the index keeps its own
    /// time; an index that links nothing and has not changed for the period goes too. Returns 1 when it was deleted.</summary>
    private static int PruneIndex(string folder, DateTimeOffset cutoff)
    {
        var index = Path.Combine(folder, IndexFileName);
        if (!File.Exists(index) || IsReparse(index)) return 0;
        var written = File.GetLastWriteTimeUtc(index);
        var lines = File.ReadAllLines(index);
        var kept = lines.Where(line => IndexLink.Matches(line).All(link => LinkExists(folder, link.Groups[1].Value))).ToArray();
        var links = kept.Count(line => IndexLink.IsMatch(line));
        if (links == 0 && written < cutoff.UtcDateTime)
        {
            File.Delete(index);
            return 1;
        }
        if (kept.Length == lines.Length) return 0;
        File.WriteAllLines(index, kept);
        File.SetLastWriteTimeUtc(index, written);
        return 0;
    }

    /// <summary>"[Title](topic.md)" style relative links to markdown files; web links are not memory files.</summary>
    private static readonly Regex IndexLink = new(@"\]\((?!https?:|mailto:)([^)\s#]+\.md)(?:#[^)]*)?\)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static bool LinkExists(string folder, string link)
    {
        try
        {
            var path = Path.GetFullPath(Path.Combine(folder, Uri.UnescapeDataString(link).Replace('/', '\\')));
            return File.Exists(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or UriFormatException) { return true; }
    }

    private static void RemoveEmptyDirectories(string folder, bool keepRoot)
    {
        foreach (var directory in Directory.EnumerateDirectories(folder).Where(path => !IsReparse(path)).ToList())
            RemoveEmptyDirectories(directory, keepRoot: false);
        // The import marker keeps a project folder alive so the account's old memory is not copied in again.
        if (!keepRoot && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
    }

    private static IEnumerable<string> MarkdownFiles(string folder)
    {
        var pending = new Stack<string>([folder]);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            string[] files, directories;
            try
            {
                files = Directory.GetFiles(directory, "*.md");
                directories = Directory.GetDirectories(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var file in files.Where(file => !IsReparse(file))) yield return file;
            foreach (var child in directories.Where(child => !IsReparse(child))) pending.Push(child);
        }
    }

    private static CliMemoryFile Describe(string provider, string project, string file)
    {
        var info = new FileInfo(file);
        return new(provider, project, file, Path.GetFileName(file), info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    internal static bool IsReparse(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }
}
