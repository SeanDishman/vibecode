using System.IO;
using System.Text.Json.Nodes;

namespace VibeCode.Services;

public sealed record SessionEntry(string SessionId, string Title, string Cwd, DateTime LastModified, string? GitBranch,
    string Provider = "claude")
{
    public string ProviderBadge => Provider switch { "kimi" => "Kimi", "grok" => "Grok", _ => "" };
}
public sealed record ProjectEntry(string Cwd, string Name, DateTime LastModified, List<SessionEntry> Sessions);
public sealed record TranscriptMessage(string Type, JsonNode Message, string? ParentToolUseId);

/// <summary>Reads Claude Code's on-disk session store (~/.claude) - same data the CLI uses.</summary>
public static class SessionCatalog
{
    public static string ClaudeDir =>
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    public static string KimiDir =>
        Environment.GetEnvironmentVariable("KIMI_CODE_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".kimi-code");

    public static string GrokDir =>
        Environment.GetEnvironmentVariable("GROK_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".grok");

    public static List<ProjectEntry> ListProjects(int maxSessionsPerProject = 40)
    {
        var titles = LoadHistoryTitles();
        var byCwd = new Dictionary<string, List<SessionEntry>>(StringComparer.OrdinalIgnoreCase);
        var projectsDir = Path.Combine(ClaudeDir, "projects");
        if (Directory.Exists(projectsDir))
        foreach (var dir in Directory.EnumerateDirectories(projectsDir))
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.jsonl"))
            {
                try
                {
                    var entry = ReadSessionHead(file, titles);
                    if (entry is null) continue;
                    if (!byCwd.TryGetValue(entry.Cwd, out var list)) byCwd[entry.Cwd] = list = new();
                    list.Add(entry);
                }
                catch { /* unreadable transcript */ }
            }
        }

        AddKimiSessions(byCwd);
        AddGrokSessions(byCwd);

        return byCwd
            .Select(kv => new ProjectEntry(
                kv.Key,
                Path.GetFileName(kv.Key.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : kv.Key,
                kv.Value.Max(s => s.LastModified),
                kv.Value.OrderByDescending(s => s.LastModified).Take(maxSessionsPerProject).ToList()))
            .OrderByDescending(p => p.LastModified)
            .ToList();
    }

    /// <summary>Merge Grok's summary.json session store (~/.grok/sessions/&lt;cwd&gt;/&lt;id&gt;) into history.</summary>
    private static void AddGrokSessions(Dictionary<string, List<SessionEntry>> byCwd)
    {
        var root = Path.Combine(GrokDir, "sessions");
        if (!Directory.Exists(root)) return;
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(root, "summary.json", SearchOption.AllDirectories).ToArray(); }
        catch { return; }
        foreach (var file in files)
        {
            try
            {
                var summary = JsonNode.Parse(File.ReadAllText(file));
                if (summary is null || summary["hidden"]?.GetValue<bool>() == true) continue;
                var kind = summary["session_kind"]?.GetValue<string>();
                if (kind?.StartsWith("subagent", StringComparison.OrdinalIgnoreCase) == true) continue;
                var id = summary["info"]?["id"]?.GetValue<string>()
                         ?? Directory.GetParent(file)?.Name;
                var cwd = summary["info"]?["cwd"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(cwd)) continue;
                var title = summary["generated_title"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(title)) title = summary["session_summary"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(title)) title = id.Length > 8 ? id[..8] : id;
                title = title!.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Trim();
                if (title.Length > 80) title = title[..80] + "…";
                var updated = ParseDate(summary["last_active_at"]?.GetValue<string>())
                              ?? ParseDate(summary["updated_at"]?.GetValue<string>())
                              ?? File.GetLastWriteTime(file);
                var branch = summary["head_branch"]?.GetValue<string>();
                if (!byCwd.TryGetValue(cwd, out var list)) byCwd[cwd] = list = new();
                list.Add(new SessionEntry(id, title, cwd, updated, branch, "grok"));
            }
            catch { /* one partial/corrupt summary must not hide other Grok sessions */ }
        }
    }

    /// <summary>Merge Kimi Code's documented session index into the same project browser as Claude history.</summary>
    private static void AddKimiSessions(Dictionary<string, List<SessionEntry>> byCwd)
    {
        var index = Path.Combine(KimiDir, "session_index.jsonl");
        if (!File.Exists(index)) return;
        var rows = new Dictionary<string, (string Dir, string Cwd)>(StringComparer.Ordinal);
        try
        {
            foreach (var line in File.ReadLines(index))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var row = JsonNode.Parse(line);
                    var id = row?["sessionId"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    if (row?["deleted"]?.GetValue<bool>() == true) { rows.Remove(id); continue; }
                    var dir = row?["sessionDir"]?.GetValue<string>();
                    var cwd = row?["workDir"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(cwd)) continue;
                    if (!Path.IsPathRooted(dir)) dir = Path.GetFullPath(Path.Combine(KimiDir, dir));
                    rows[id] = (dir, cwd);
                }
                catch { /* append-only index may contain a partial last line while Kimi is writing */ }
            }
        }
        catch { return; }

        foreach (var (id, row) in rows)
        {
            try
            {
                if (!Directory.Exists(row.Dir)) continue;
                JsonNode? state = null;
                var statePath = Path.Combine(row.Dir, "state.json");
                if (File.Exists(statePath)) state = JsonNode.Parse(File.ReadAllText(statePath));
                if (state?["custom"]?["archived"]?.GetValue<bool>() == true) continue;
                var title = state?["customTitle"]?.GetValue<string>()
                            ?? state?["title"]?.GetValue<string>()
                            ?? state?["lastPrompt"]?.GetValue<string>()
                            ?? (id.Length > 8 ? id[..8] : id);
                title = title.Replace('\n', ' ').Trim();
                if (title.Length > 80) title = title[..80] + "…";
                var updated = ParseDate(state?["updatedAt"]?.GetValue<string>())
                              ?? File.GetLastWriteTime(row.Dir);
                if (!byCwd.TryGetValue(row.Cwd, out var list)) byCwd[row.Cwd] = list = new();
                list.Add(new SessionEntry(id, title, row.Cwd, updated, null, "kimi"));
            }
            catch { /* one corrupt session must not hide the rest of the project list */ }
        }
    }

    private static DateTime? ParseDate(string? value) =>
        DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed.ToLocalTime()
            : null;

    private static Dictionary<string, string> LoadHistoryTitles()
    {
        var map = new Dictionary<string, string>();
        var path = Path.Combine(ClaudeDir, "history.jsonl");
        if (!File.Exists(path)) return map;
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var n = JsonNode.Parse(line);
                    var id = n?["sessionId"]?.GetValue<string>();
                    var display = n?["display"]?.GetValue<string>();
                    if (id is not null && display is not null && !map.ContainsKey(id))
                        map[id] = display;
                }
                catch { /* skip bad line */ }
            }
        }
        catch { /* locked file */ }
        return map;
    }

    private static SessionEntry? ReadSessionHead(string file, Dictionary<string, string> titles)
    {
        var sessionId = Path.GetFileNameWithoutExtension(file);
        string? cwd = null, title = null, branch = null;
        using var reader = new StreamReader(file);
        for (var i = 0; i < 25 && reader.ReadLine() is { } line; i++)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonNode? n;
            try { n = JsonNode.Parse(line); } catch { continue; }
            cwd ??= n?["cwd"]?.GetValue<string>();
            branch ??= n?["gitBranch"]?.GetValue<string>();
            if (title is null && n?["type"]?.GetValue<string>() == "user" && n["isSidechain"]?.GetValue<bool>() != true)
            {
                var content = n["message"]?["content"];
                var text = content is JsonValue v ? v.GetValue<string>()
                    : (content as JsonArray)?.OfType<JsonObject>()
                        .FirstOrDefault(b => b["type"]?.GetValue<string>() == "text")?["text"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(text) && !text!.StartsWith("<")) title = text;
            }
            if (cwd is not null && title is not null) break;
        }
        if (cwd is null) return null;
        titles.TryGetValue(sessionId, out var histTitle);
        // \r breaks a TextBlock line just like \n does, so flatten both or the session row renders double height.
        var finalTitle = (histTitle ?? title ?? sessionId[..8]).Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
        if (finalTitle.Length > 80) finalTitle = finalTitle[..80] + "…";
        return new SessionEntry(sessionId, finalTitle, cwd, File.GetLastWriteTime(file), branch);
    }

    /// <summary>Best-effort transcript replay for resumed sessions (main thread only).</summary>
    public static List<TranscriptMessage> LoadTranscript(string cwd, string sessionId, int maxMessages = 400)
    {
        var result = new List<TranscriptMessage>();
        var projectsDir = Path.Combine(ClaudeDir, "projects");
        if (!Directory.Exists(projectsDir)) return result;
        string? file = Directory.EnumerateDirectories(projectsDir)
            .Select(d => Path.Combine(d, sessionId + ".jsonl"))
            .FirstOrDefault(File.Exists);
        if (file is null) return result;

        foreach (var line in File.ReadLines(file))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonNode? n;
            try { n = JsonNode.Parse(line); } catch { continue; }
            var type = n?["type"]?.GetValue<string>();
            if (type is not ("user" or "assistant")) continue;
            if (n!["isSidechain"]?.GetValue<bool>() == true) continue;
            var message = n["message"];
            if (message is null) continue;
            result.Add(new TranscriptMessage(type!, message, n["parent_tool_use_id"]?.GetValue<string>()));
            if (result.Count >= maxMessages) break;
        }
        return result;
    }
}
