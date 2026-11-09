using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using VibeCode.Protocol;

namespace VibeCode.Services;

/// <summary>
/// A skill VibeCode installed: a folder whose SKILL.md (YAML frontmatter with a name and a description) Claude Code and
/// Codex load when a task calls for it. VibeCode keeps its own copy per CLI and writes it into that CLI's skills folder.
/// </summary>
public sealed class SkillDefinition
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>An https GitHub repository (a /tree/ or /blob/ link inside one also works) or a direct .zip/.skill archive.</summary>
    public string Source { get; set; } = "";
    /// <summary>Branch, tag or commit; empty for the repository's default branch.</summary>
    public string Ref { get; set; } = "";
    /// <summary>The folder inside the source that holds SKILL.md; empty to let VibeCode find it.</summary>
    public string Folder { get; set; } = "";
    /// <summary>The folders each CLI's copy came from. They differ when a source ships one version of a skill per agent.</summary>
    public string ClaudeFolder { get; set; } = "";
    public string CodexFolder { get; set; } = "";
    public bool UseClaude { get; set; } = true;
    public bool UseCodex { get; set; } = true;
    /// <summary>The Claude copy's content, which Claude account homes are synced against.</summary>
    public string ContentHash { get; set; } = "";
    public string CodexHash { get; set; } = "";
    public DateTime InstalledAtUtc { get; set; }

    [JsonIgnore] public string TargetsSummary => (UseClaude, UseCodex) switch
    {
        (true, true) => "Claude · Codex",
        (true, false) => "Claude",
        (false, true) => "Codex",
        _ => "No CLI",
    };

    [JsonIgnore] public string SourceSummary
    {
        get
        {
            static string Show(string folder) => folder.Length == 0 ? "(root)" : folder;
            var source = Source.TrimEnd('/');
            if (UseClaude && UseCodex && !string.Equals(ClaudeFolder, CodexFolder, StringComparison.OrdinalIgnoreCase))
                return $"{source} › {Show(ClaudeFolder)} for Claude, {Show(CodexFolder)} for Codex";
            var folder = UseClaude ? ClaudeFolder : UseCodex ? CodexFolder : Folder;
            return folder.Length == 0 ? source : source + " › " + folder;
        }
    }

    [JsonIgnore] public string ShortDescription => SkillCatalog.Shorten(Description);

    public SkillDefinition Clone() => (SkillDefinition)MemberwiseClone();
}

/// <summary>A skill found in a CLI's skills folder that VibeCode did not install. Listed, never changed.</summary>
public sealed record DiscoveredSkill(string Name, string Description, string Location, string Folder)
{
    public string ShortDescription => SkillCatalog.Shorten(Description);
}

public sealed class SkillInstallResult
{
    public required SkillDefinition Definition { get; init; }
    public List<string> Installed { get; } = new();
    public List<string> Skipped { get; } = new();
}

/// <summary>
/// Installs skills from a GitHub repository or an archive into Claude Code's and Codex's skills folders. VibeCode keeps
/// a copy per CLI in its data folder and marks every copy it writes, so it only ever updates or removes its own: a skill
/// installed some other way is left exactly as it is. Claude accounts that VibeCode runs with their own config folder
/// receive their copy when a session starts; every Codex home already links the shared Codex skills folder.
/// </summary>
public static class SkillCatalog
{
    public const string SkillFile = "SKILL.md";
    public const string MarkerFile = ".vibecode-skill.json";
    public const int MaxNameLength = 64;
    internal const long MaxArchiveBytes = 64L * 1024 * 1024;
    internal const long MaxSkillBytes = 100L * 1024 * 1024;
    internal const long MaxFileBytes = 25L * 1024 * 1024;
    internal const int MaxSkillFiles = 2000;

    private static readonly Regex NamePattern = new("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant);
    private static readonly Regex OwnerPattern = new("^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$", RegexOptions.CultureInvariant);
    private static readonly Regex RepositoryPattern = new("^[A-Za-z0-9._-]{1,100}$", RegexOptions.CultureInvariant);
    private static readonly Regex RefPattern = new("^[A-Za-z0-9._-]+(?:/[A-Za-z0-9._-]+)*$", RegexOptions.CultureInvariant);
    // Copies under these folders are a source's own test data or samples, not the skill it publishes.
    private static readonly string[] FixtureFolders = { "test", "tests", "testdata", "__tests__", "fixture", "fixtures", "example", "examples" };
    private static readonly object Gate = new();

    // Regression seams: the harness points these at temporary folders so nothing touches the real CLI homes.
    internal static string? ClaudeSkillsRootOverride { get; set; }
    internal static string? CodexSkillsRootOverride { get; set; }

    /// <summary>VibeCode's own copies of the skills it installed, one folder per CLI.</summary>
    public static string StoreRoot => Path.Combine(AppSettings.Dir, "skills");

    /// <summary>Claude Code's user skills folder, outside VibeCode's per-account Claude homes.</summary>
    public static string ClaudeSkillsRoot => ClaudeSkillsRootOverride ?? Path.Combine(
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } configured
            ? Path.GetFullPath(configured.Trim('"'))
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"),
        "skills");

    /// <summary>Codex's skills folder. Every VibeCode Codex home links its own skills folder to this one.</summary>
    public static string CodexSkillsRoot => CodexSkillsRootOverride ?? Path.Combine(CodexEnvironment.StandaloneHomeDirectory, "skills");

    private static string DeploymentsPath => Path.Combine(StoreRoot, ".deployments.json");

    /// <summary>VibeCode's copy of a skill for one CLI ("claude" or "codex").</summary>
    public static string StoredFolder(string cli, string name) => Path.Combine(StoreRoot, cli, name);

    /// <summary>The copy Settings opens: the Claude one when the skill is installed for Claude, otherwise the Codex one.</summary>
    public static string? StoredFolder(SkillDefinition skill) =>
        IsValidName(skill.Name) ? StoredFolder(skill.UseClaude ? "claude" : "codex", skill.Name) : null;

    public static bool IsValidName(string? name) =>
        name is { Length: > 0 and <= MaxNameLength } && NamePattern.IsMatch(name);

    internal static string Shorten(string text)
    {
        var flat = string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= 220 ? flat : flat[..217].TrimEnd() + "…";
    }

    /// <summary>Problems a user (or the AI draft) can fix before anything is downloaded.</summary>
    public static List<string> Validate(SkillDefinition skill)
    {
        var errors = new List<string>();
        if (!TryResolveArchive(skill, out _, out var sourceError)) errors.Add(sourceError);
        if (!string.IsNullOrWhiteSpace(skill.Name) && !IsValidName(skill.Name.Trim()))
            errors.Add($"Skill name must be 1-{MaxNameLength} lowercase letters, digits and single hyphens, like frontend-design.");
        if (!skill.UseClaude && !skill.UseCodex) errors.Add("Choose Claude, Codex or both.");
        return errors;
    }

    /// <summary>A download, the folder asked for inside it, a label for progress, and the source's own name.</summary>
    internal readonly record struct ArchiveLocation(Uri Url, string Folder, string Label, string Name);

    /// <summary>Where to download a source: GitHub's archive of the ref (HEAD is the default branch) or the archive itself.</summary>
    internal static bool TryResolveArchive(SkillDefinition skill, out ArchiveLocation location, out string error)
    {
        location = default;
        error = "";
        var source = skill.Source?.Trim() ?? "";
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0)
        {
            error = "Source must be an https address: a GitHub repository, or a .zip or .skill archive.";
            return false;
        }
        var folder = NormalizeFolder(skill.Folder);
        if (folder is null)
        {
            error = "Folder must be a path inside the source, like skills/frontend-design, with no .. or drive.";
            return false;
        }
        var gitRef = (skill.Ref ?? "").Trim().Trim('/');
        if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase))
        {
            var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToList();
            if (parts.Count < 2)
            {
                error = "A GitHub source needs the owner and the repository, like https://github.com/owner/repo.";
                return false;
            }
            var owner = parts[0];
            var repository = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1];
            if (!OwnerPattern.IsMatch(owner) || !RepositoryPattern.IsMatch(repository))
            {
                error = "That GitHub address does not name a repository.";
                return false;
            }
            if (parts.Count > 2)
            {
                if (parts.Count < 4 || parts[2] is not ("tree" or "blob"))
                {
                    error = "Use the repository address, or a /tree/ or /blob/ link inside it.";
                    return false;
                }
                // github.com/owner/repo/tree/<ref>/<folder>. A ref containing a slash is ambiguous in such a link, so a
                // Ref the user typed wins; otherwise the link's first segment is the ref.
                var rest = string.Join('/', parts.Skip(3));
                if (gitRef.Length == 0)
                {
                    var slash = rest.IndexOf('/');
                    gitRef = slash < 0 ? rest : rest[..slash];
                    rest = slash < 0 ? "" : rest[(slash + 1)..];
                }
                else if (rest == gitRef) rest = "";
                else if (rest.StartsWith(gitRef + "/", StringComparison.Ordinal)) rest = rest[(gitRef.Length + 1)..];
                if (parts[2] == "blob" && (rest.Equals(SkillFile, StringComparison.OrdinalIgnoreCase)
                                           || rest.EndsWith("/" + SkillFile, StringComparison.OrdinalIgnoreCase)))
                    rest = rest.Length == SkillFile.Length ? "" : rest[..^(SkillFile.Length + 1)];
                if (folder.Length == 0) folder = NormalizeFolder(rest) ?? "";
            }
            if (gitRef.Length > 0 && (!RefPattern.IsMatch(gitRef) || gitRef.Split('/').Any(segment => segment is "." or "..")))
            {
                error = "Ref may contain only letters, digits, dots, hyphens, underscores and slashes.";
                return false;
            }
            var archiveRef = gitRef.Length == 0 ? "HEAD" : string.Join('/', gitRef.Split('/').Select(Uri.EscapeDataString));
            location = new ArchiveLocation(
                new Uri($"https://github.com/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}/archive/{archiveRef}.zip"),
                folder, $"{owner}/{repository}", repository);
            return true;
        }
        if (uri.AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || uri.AbsolutePath.EndsWith(".skill", StringComparison.OrdinalIgnoreCase))
        {
            location = new ArchiveLocation(uri, folder, uri.Host, Path.GetFileNameWithoutExtension(Uri.UnescapeDataString(uri.AbsolutePath)));
            return true;
        }
        error = "Source must be a GitHub repository, or a direct link to a .zip or .skill archive.";
        return false;
    }

    /// <summary>Forward slashes, no leading or trailing slash, "" for the root; null when the path could leave the source.</summary>
    private static string? NormalizeFolder(string? folder)
    {
        var value = (folder ?? "").Trim().Replace('\\', '/').Trim('/');
        if (value is "" or ".") return "";
        return IsSafeArchivePath(value) ? value : null;
    }

    private static bool IsSafeArchivePath(string path)
    {
        if (path.Length is 0 or > 400 || path.StartsWith('/') || path.Contains(':') || path.Any(char.IsControl)) return false;
        var segments = path.Split('/');
        return segments.All(segment => segment.Length is > 0 and <= 255 && segment is not ("." or "..")
                                       && segment.IndexOfAny(Path.GetInvalidFileNameChars()) < 0);
    }

    public static async Task<SkillInstallResult> InstallAsync(SkillDefinition request, HttpClient http,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var errors = Validate(request);
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        TryResolveArchive(request, out var location, out _);
        progress?.Report($"Downloading {location.Label}…");
        // No UI work happens here, so nothing resumes on the caller's thread: a caller that waits synchronously on
        // a dispatcher thread (as the regression harness does) cannot deadlock against its own continuation.
        var archive = await DownloadAsync(http, location.Url, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report("Checking and installing the skill…");
        return await Task.Run(() => InstallArchive(request, location, archive), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> DownloadAsync(HttpClient http, Uri url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("VibeCode-Skills");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException($"Nothing was found at {url}. Check the repository, the ref, and that it is public.");
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"The download failed ({(int)response.StatusCode} {response.ReasonPhrase}).");
        if (response.RequestMessage?.RequestUri is { } final && final.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("The download was redirected away from https and was stopped.");
        if (response.Content.Headers.ContentLength > MaxArchiveBytes)
            throw new InvalidOperationException($"The source is larger than {MaxArchiveBytes / (1024 * 1024)} MB, which is too big for a skill.");
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxArchiveBytes)
                throw new InvalidOperationException($"The source is larger than {MaxArchiveBytes / (1024 * 1024)} MB, which is too big for a skill.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private sealed record ArchiveFile(string Path, ZipArchiveEntry Entry);

    /// <summary>Unpack the skill from a downloaded archive, then install it. Separate from the download so the harness can use local archives.</summary>
    internal static SkillInstallResult InstallArchive(SkillDefinition request, ArchiveLocation location, byte[] archive)
    {
        ZipArchive zip;
        try { zip = new ZipArchive(new MemoryStream(archive, writable: false), ZipArchiveMode.Read); }
        catch (InvalidDataException) { throw new InvalidOperationException("The download is not a zip archive."); }
        using (zip)
        {
            var files = ReadEntries(zip);
            var (claudeFolder, codexFolder) = ResolveFolders(files, location.Folder, (request.Name ?? "").Trim(), location.Name);
            Directory.CreateDirectory(StoreRoot);
            var staged = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                string Stage(string folder)
                {
                    if (staged.TryGetValue(folder, out var existing)) return existing;
                    var path = Path.Combine(StoreRoot, ".staging-" + Guid.NewGuid().ToString("N"));
                    staged[folder] = path;
                    Extract(files, folder, path);
                    return path;
                }

                var primaryFolder = request.UseClaude ? claudeFolder : codexFolder;
                var primary = Stage(primaryFolder);
                var (name, description) = ReadFrontmatter(File.ReadAllText(Path.Combine(primary, SkillFile)));
                if (name.Length == 0) name = (primaryFolder.Length > 0 ? LastSegment(primaryFolder) : location.Name).ToLowerInvariant();
                if (!IsValidName(name))
                    throw new InvalidOperationException($"This skill's SKILL.md names it '{name}', which is not a valid skill name (lowercase letters, digits and hyphens).");
                if (description.Length == 0)
                    throw new InvalidOperationException("This skill's SKILL.md has no description, so Claude and Codex could not tell when to use it.");
                var claudeCopy = request.UseClaude ? Stage(claudeFolder) : null;
                var codexCopy = request.UseCodex ? Stage(codexFolder) : null;
                foreach (var copy in new[] { claudeCopy, codexCopy })
                {
                    if (copy is null || copy == primary) continue;
                    var other = ReadFrontmatter(File.ReadAllText(Path.Combine(copy, SkillFile))).Name;
                    if (other.Length > 0 && !other.Equals(name, StringComparison.Ordinal))
                        throw new InvalidOperationException($"This source's Claude and Codex versions name the skill differently ('{name}' and '{other}'). Set the folder to choose one.");
                }

                var definition = new SkillDefinition
                {
                    Name = name,
                    Description = description,
                    Source = request.Source.Trim(),
                    Ref = (request.Ref ?? "").Trim(),
                    Folder = primaryFolder,
                    ClaudeFolder = claudeCopy is null ? "" : claudeFolder,
                    CodexFolder = codexCopy is null ? "" : codexFolder,
                    UseClaude = request.UseClaude,
                    UseCodex = request.UseCodex,
                    ContentHash = claudeCopy is null ? "" : HashFolder(claudeCopy),
                    CodexHash = codexCopy is null ? "" : HashFolder(codexCopy),
                    InstalledAtUtc = DateTime.UtcNow,
                };
                lock (Gate)
                {
                    var result = new SkillInstallResult { Definition = definition };
                    foreach (var (cli, label, root, copy, folder) in new[]
                             {
                                 ("claude", "Claude", ClaudeSkillsRoot, claudeCopy, claudeFolder),
                                 ("codex", "Codex", CodexSkillsRoot, codexCopy, codexFolder),
                             })
                    {
                        var stored = StoredFolder(cli, name);
                        if (copy is null)
                        {
                            // "Install for" is authoritative: a CLI unchecked on reinstall loses VibeCode's own copy.
                            var previous = Path.Combine(root, name);
                            if (Directory.Exists(previous) && IsManaged(previous, name)) Directory.Delete(previous, recursive: true);
                            if (Directory.Exists(stored)) Directory.Delete(stored, recursive: true);
                            continue;
                        }
                        if (Directory.Exists(stored)) Directory.Delete(stored, recursive: true);
                        CopyDirectory(copy, stored);
                        WriteMarker(stored, definition, folder, cli == "claude" ? definition.ContentHash : definition.CodexHash);
                        var target = Deploy(name, stored, root, out var skipped);
                        if (target is not null)
                        {
                            result.Installed.Add($"{label}: {target}");
                            continue;
                        }
                        // Someone else's skill of that name lives there: the skill is not installed for that CLI.
                        Directory.Delete(stored, recursive: true);
                        result.Skipped.Add($"{label}: {skipped}");
                        if (cli == "claude") { definition.UseClaude = false; definition.ClaudeFolder = definition.ContentHash = ""; }
                        else { definition.UseCodex = false; definition.CodexFolder = definition.CodexHash = ""; }
                    }
                    return result;
                }
            }
            finally
            {
                foreach (var path in staged.Values)
                    if (Directory.Exists(path)) try { Directory.Delete(path, recursive: true); } catch { }
            }
        }
    }

    private static List<ArchiveFile> ReadEntries(ZipArchive zip)
    {
        var files = new List<ArchiveFile>();
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.EndsWith('/')) continue;                                         // a directory record
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) continue;      // links are never extracted
            if (!IsSafeArchivePath(name))
                throw new InvalidOperationException($"The archive contains an unsafe path ({name}) and was not installed.");
            files.Add(new ArchiveFile(name, entry));
        }
        // GitHub (like most zip tools) wraps everything in one top folder, such as impeccable-main/.
        var tops = files.Select(file => file.Path.Split('/')[0]).Distinct(StringComparer.Ordinal).ToList();
        if (tops.Count == 1 && files.All(file => file.Path.Contains('/')))
            files = files.Select(file => file with { Path = file.Path[(tops[0].Length + 1)..] }).ToList();
        return files;
    }

    private static string LastSegment(string path) => path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;

    /// <summary>
    /// The folder each CLI's copy comes from. A folder the user (or the draft) named that holds a SKILL.md is used for
    /// both. Otherwise the skill's name - the request's, the folder's last segment, or the source's own name - picks among
    /// the SKILL.md folders, preferring each CLI's own version when a source ships one per agent (.claude/skills/x for
    /// Claude; .codex/skills/x, then .agents/skills/x for Codex) and ignoring copies kept as tests or examples.
    /// </summary>
    private static (string Claude, string Codex) ResolveFolders(List<ArchiveFile> files, string folder, string requestedName, string sourceName)
    {
        var candidates = files.Where(file => LastSegment(file.Path).Equals(SkillFile, StringComparison.OrdinalIgnoreCase))
            .Select(file => file.Path.Contains('/') ? file.Path[..file.Path.LastIndexOf('/')] : "")
            .Distinct(StringComparer.Ordinal).ToList();
        if (candidates.Count == 0) throw new InvalidOperationException("No SKILL.md was found in this source, so it is not a skill.");
        var exact = candidates.FirstOrDefault(candidate => candidate.Equals(folder, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return (exact, exact);
        var usable = candidates.Where(candidate => !candidate.Split('/').Any(segment => FixtureFolders.Contains(segment, StringComparer.OrdinalIgnoreCase))).ToList();
        if (usable.Count == 0) usable = candidates;
        if (folder.Length == 0 && usable.Count == 1) return (usable[0], usable[0]);
        var hints = new[] { requestedName, LastSegment(folder), sourceName }
            .Select(hint => (hint ?? "").Trim()).Where(hint => hint.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var hint in hints)
        {
            var named = usable.Where(candidate => LastSegment(candidate).Equals(hint, StringComparison.OrdinalIgnoreCase)).ToList();
            if (named.Count == 0) continue;
            string? Pick(params string[] prefixes) => prefixes
                .Select(prefix => named.FirstOrDefault(candidate => candidate.Equals(prefix + hint, StringComparison.OrdinalIgnoreCase)))
                .FirstOrDefault(candidate => candidate is not null);
            var claude = Pick(".claude/skills/", "skills/", ".agents/skills/", "") ?? (named.Count == 1 ? named[0] : null);
            var codex = Pick(".codex/skills/", ".agents/skills/", "skills/", ".claude/skills/", "") ?? (named.Count == 1 ? named[0] : null);
            if (claude is not null && codex is not null) return (claude, codex);
        }
        var listed = string.Join(", ", usable.Take(12).Select(candidate => candidate.Length == 0 ? "(the root)" : candidate));
        throw new InvalidOperationException((folder.Length == 0 ? "This source contains several skills." : $"There is no SKILL.md in '{folder}'.")
                                            + " Set the folder to one of: " + listed + (usable.Count > 12 ? ", …" : "") + ".");
    }

    private static void Extract(List<ArchiveFile> files, string skillFolder, string staging)
    {
        var prefix = skillFolder.Length == 0 ? "" : skillFolder + "/";
        var selected = files.Where(file => file.Path.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        if (selected.Count > MaxSkillFiles)
            throw new InvalidOperationException($"This skill has more than {MaxSkillFiles} files, which is too many to install.");
        var root = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
        long total = 0;
        foreach (var file in selected)
        {
            var relative = file.Path[prefix.Length..];
            if (relative.Equals(SkillFile, StringComparison.OrdinalIgnoreCase)) relative = SkillFile;   // the CLIs look for SKILL.md
            var target = Path.GetFullPath(Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"The archive contains an unsafe path ({file.Path}) and was not installed.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = file.Entry.Open();
            using var output = File.Create(target);
            var chunk = new byte[81920];
            long written = 0;
            int read;
            while ((read = input.Read(chunk, 0, chunk.Length)) > 0)
            {
                written += read;
                total += read;
                if (written > MaxFileBytes) throw new InvalidOperationException($"{file.Path} is larger than {MaxFileBytes / (1024 * 1024)} MB.");
                if (total > MaxSkillBytes) throw new InvalidOperationException($"This skill is larger than {MaxSkillBytes / (1024 * 1024)} MB.");
                output.Write(chunk, 0, read);
            }
        }
        if (!File.Exists(Path.Combine(staging, SkillFile)))
            throw new InvalidOperationException("No SKILL.md was found in the skill's folder.");
    }

    /// <summary>The name and description from SKILL.md's YAML frontmatter (plain, quoted, or block scalars).</summary>
    internal static (string Name, string Description) ReadFrontmatter(string text)
    {
        var lines = text.TrimStart('﻿').Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---") return ("", "");
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Trim() == "---") break;
            if (line.Length == 0 || char.IsWhiteSpace(line[0]) || line.StartsWith('#')) continue;
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (value is "|" or ">" or "|-" or ">-" or "|+" or ">+")
            {
                var folded = value[0] == '>';
                var parts = new List<string>();
                while (i + 1 < lines.Length && lines[i + 1].Trim() != "---"
                                            && (lines[i + 1].Length == 0 || char.IsWhiteSpace(lines[i + 1][0])))
                    parts.Add(lines[++i].Trim());
                value = string.Join(folded ? " " : "\n", parts).Trim();
            }
            else if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
                value = value[0] == '\'' ? value[1..^1].Replace("''", "'") : value[1..^1].Replace("\\\"", "\"");
            values.TryAdd(key, value);
        }
        return (values.GetValueOrDefault("name", "").Trim(), values.GetValueOrDefault("description", "").Trim());
    }

    internal static string HashFolder(string folder)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Select(file => (Full: file, Relative: Path.GetRelativePath(folder, file).Replace('\\', '/')))
            .Where(file => !file.Relative.Equals(MarkerFile, StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file.Relative, StringComparer.Ordinal);
        foreach (var file in files)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(file.Relative + "\n"));
            hash.AppendData(File.ReadAllBytes(file.Full));
            hash.AppendData(new byte[] { 0 });
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void WriteMarker(string folder, SkillDefinition skill, string sourceFolder, string hash) =>
        File.WriteAllText(Path.Combine(folder, MarkerFile), new JsonObject
        {
            ["managedBy"] = "VibeCode",
            ["name"] = skill.Name,
            ["source"] = skill.Source,
            ["ref"] = skill.Ref,
            ["folder"] = sourceFolder,
            ["hash"] = hash,
            ["installedAtUtc"] = skill.InstalledAtUtc.ToString("O"),
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));

    private static JsonObject? ReadMarker(string folder)
    {
        try { return JsonNode.Parse(File.ReadAllText(Path.Combine(folder, MarkerFile))) as JsonObject; }
        catch { return null; }
    }

    /// <summary>True for a skill folder VibeCode wrote: the only kind it ever replaces or deletes.</summary>
    internal static bool IsManaged(string folder, string name)
    {
        try
        {
            return ReadMarker(folder) is { } marker
                   && string.Equals(marker["managedBy"]?.GetValue<string>(), "VibeCode", StringComparison.Ordinal)
                   && string.Equals(marker["name"]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }   // a damaged marker means "not provably ours", so the folder is left alone
    }

    /// <summary>Copy one of VibeCode's stored copies into a skills folder; null (with a reason) when someone else's skill is there.</summary>
    private static string? Deploy(string name, string stored, string skillsRoot, out string skipped)
    {
        skipped = "";
        var target = Path.Combine(skillsRoot, name);
        if (Directory.Exists(target) && !IsManaged(target, name))
        {
            skipped = $"{target} already holds a '{name}' skill that VibeCode did not install, so it was left as is.";
            return null;
        }
        // Stage beside the skills folder rather than in it, so a CLI starting meanwhile never lists a half-copied skill.
        var staging = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(skillsRoot))!, ".vibecode-skill-staging", Guid.NewGuid().ToString("N"));
        try
        {
            CopyDirectory(stored, staging);
            Directory.CreateDirectory(skillsRoot);
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.Move(staging, target);
        }
        finally
        {
            if (Directory.Exists(staging)) try { Directory.Delete(staging, recursive: true); } catch { }
            try
            {
                var parent = Path.GetDirectoryName(staging)!;
                if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent);
            }
            catch { }
        }
        RecordDeployment(name, target);
        return target;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), overwrite: true);
    }

    private static Dictionary<string, List<string>> ReadDeployments()
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(DeploymentsPath))
                   is { } saved ? new Dictionary<string, List<string>>(saved, StringComparer.OrdinalIgnoreCase)
                   : new(StringComparer.OrdinalIgnoreCase);
        }
        catch { return new(StringComparer.OrdinalIgnoreCase); }
    }

    private static void WriteDeployments(Dictionary<string, List<string>> deployments)
    {
        Directory.CreateDirectory(StoreRoot);
        var temporary = DeploymentsPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(deployments, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        File.Move(temporary, DeploymentsPath, overwrite: true);
    }

    private static void RecordDeployment(string name, string target)
    {
        var deployments = ReadDeployments();
        if (!deployments.TryGetValue(name, out var places)) deployments[name] = places = new List<string>();
        if (!places.Contains(target, StringComparer.OrdinalIgnoreCase)) places.Add(target);
        WriteDeployments(deployments);
    }

    /// <summary>
    /// Give one Claude config folder the Claude skills VibeCode installed. VibeCode runs each saved Claude account with
    /// its own config folder, and Claude Code reads skills from there, so a session start copies any missing or outdated
    /// ones in. Copies VibeCode did not write are never replaced.
    /// </summary>
    public static void SyncClaudeHome(string? configDirectory)
    {
        if (string.IsNullOrWhiteSpace(configDirectory)) return;
        var root = Path.Combine(Path.GetFullPath(configDirectory), "skills");
        if (string.Equals(root, Path.GetFullPath(ClaudeSkillsRoot), StringComparison.OrdinalIgnoreCase)) return;   // installed there directly
        var skills = AppSettings.Current.Skills.Where(skill => skill.UseClaude && IsValidName(skill.Name)).ToArray();
        if (skills.Length == 0) return;
        lock (Gate)
        {
            foreach (var skill in skills)
            {
                try
                {
                    var stored = StoredFolder("claude", skill.Name);
                    if (!Directory.Exists(stored)) continue;
                    var target = Path.Combine(root, skill.Name);
                    if (Directory.Exists(target)
                        && (!IsManaged(target, skill.Name)
                            || string.Equals(ReadMarker(target)?["hash"]?.GetValue<string>(), skill.ContentHash, StringComparison.Ordinal)))
                        continue;
                    Deploy(skill.Name, stored, root, out _);
                }
                catch { /* the next session start tries again; a missing skill never blocks a chat */ }
            }
        }
    }

    /// <summary>Delete every copy VibeCode wrote of this skill, and its stored copies. Skills installed some other way stay.</summary>
    public static List<string> Remove(SkillDefinition skill)
    {
        var removed = new List<string>();
        if (!IsValidName(skill.Name)) return removed;
        lock (Gate)
        {
            var deployments = ReadDeployments();
            var places = (deployments.TryGetValue(skill.Name, out var recorded) ? recorded : new List<string>())
                .Append(Path.Combine(ClaudeSkillsRoot, skill.Name))
                .Append(Path.Combine(CodexSkillsRoot, skill.Name))
                .Append(StoredFolder("claude", skill.Name))
                .Append(StoredFolder("codex", skill.Name))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var place in places)
            {
                if (!Directory.Exists(place) || !Path.GetFileName(place).Equals(skill.Name, StringComparison.OrdinalIgnoreCase)
                    || !IsManaged(place, skill.Name)) continue;
                Directory.Delete(place, recursive: true);
                removed.Add(place);
            }
            if (deployments.Remove(skill.Name)) WriteDeployments(deployments);
        }
        return removed;
    }

    /// <summary>Skills in the Claude and Codex skills folders that VibeCode did not install, for display only.</summary>
    public static List<DiscoveredSkill> FindUnmanaged()
    {
        var found = new List<DiscoveredSkill>();
        foreach (var (root, label) in new[] { (ClaudeSkillsRoot, "Claude"), (CodexSkillsRoot, "Codex") })
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (var folder in Directory.EnumerateDirectories(root))
                {
                    var name = Path.GetFileName(folder);
                    var file = Path.Combine(folder, SkillFile);
                    if (name.StartsWith('.') || !File.Exists(file) || File.Exists(Path.Combine(folder, MarkerFile))) continue;
                    var description = "";
                    try { description = ReadFrontmatter(File.ReadAllText(file)).Description; } catch { }
                    found.Add(new DiscoveredSkill(name, description, label, folder));
                }
            }
            catch { /* an unreadable folder is simply not listed */ }
        }
        return found.OrderBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase).ThenBy(skill => skill.Location, StringComparer.Ordinal).ToList();
    }
}
