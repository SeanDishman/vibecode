using System.IO;
using System.Text.Json.Nodes;

namespace VibeCode.Services;

/// <param name="AccountId">Which saved login's home holds this transcript - <c>""</c> for the shared <c>~/.claude</c>,
/// null for a provider that isn't Claude. Resuming has to spawn against the SAME home the transcript lives in: the CLI
/// looks a session up under its own CLAUDE_CONFIG_DIR only, so opening an old account's chat under the currently
/// selected account made Claude Code answer "No conversation found with session ID" - the chat read as gone.</param>
public sealed record SessionEntry(string SessionId, string Title, string Cwd, DateTime LastModified, string? GitBranch,
    string Provider = "claude", string? AccountId = null)
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

    /// <summary>
    /// Every Claude home whose transcripts belong to this user: the shared <c>~/.claude</c> plus each saved
    /// account's private home.
    ///
    /// Chats started under a saved account run with <c>CLAUDE_CONFIG_DIR</c> pointed at
    /// <c>~/.claude/vibecode-accounts/&lt;id&gt;/claude-home</c>, so their transcripts have never been anywhere near
    /// <c>~/.claude/projects</c>. Reading only the shared home meant the project browser - the one surface that can
    /// find a conversation the sidebar no longer shows - was blind to most of them.
    /// </summary>
    public static IEnumerable<string> ClaudeHomes() => ClaudeHomesWithAccounts().Select(h => h.Path);

    /// <summary>Every Claude home paired with the account that owns it (<c>""</c> = the shared <c>~/.claude</c>).
    /// The pairing is what makes a listed session resumable: see <see cref="SessionEntry.AccountId"/>.</summary>
    public static IEnumerable<(string Path, string AccountId)> ClaudeHomesWithAccounts()
    {
        // Resolve the account store exactly the way AccountService does - off the USER PROFILE, not off ClaudeDir.
        // CLAUDE_CONFIG_DIR can point at one specific account's home (that is precisely how VibeCode launches a
        // per-account chat), and hanging the store off it made the whole per-account sweep silently find nothing.
        var store = Environment.GetEnvironmentVariable("VIBECODE_CLAUDE_ACCOUNT_STORE") is { Length: > 0 } configured
            ? Path.GetFullPath(configured.Trim('"'))
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "vibecode-accounts");
        // ...which also means ClaudeDir itself may BE an account home. Tagging that as shared would resume its
        // sessions with no CLAUDE_CONFIG_DIR at all, i.e. against a home that doesn't hold them.
        yield return (ClaudeDir, AccountIdForHome(ClaudeDir, store));
        string[] accounts;
        try { accounts = Directory.Exists(store) ? Directory.GetDirectories(store) : []; }
        catch { yield break; }
        foreach (var account in accounts)
        {
            var home = Path.Combine(account, "claude-home");
            // A removed account keeps its history (see AccountService.ForgetLoginOnly), so this deliberately does
            // not require the profile to still be usable - unreachable history is the thing being fixed.
            if (Directory.Exists(home)) yield return (home, Path.GetFileName(account));
        }
    }

    /// <summary>The account id owning <paramref name="home"/> when it sits at <c>&lt;store&gt;\&lt;id&gt;\claude-home</c>,
    /// else <c>""</c> for the shared home.</summary>
    private static string AccountIdForHome(string home, string store)
    {
        try
        {
            var parent = Directory.GetParent(Path.GetFullPath(home));
            if (parent?.Parent is null) return "";
            return string.Equals(parent.Parent.FullName.TrimEnd('\\', '/'), store.TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase) ? parent.Name : "";
        }
        catch { return ""; }
    }

    public static List<ProjectEntry> ListProjects(int maxSessionsPerProject = 40)
    {
        var titles = LoadHistoryTitles();
        var byCwd = new Dictionary<string, List<SessionEntry>>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // one row per session id across homes
        foreach (var (home, accountId) in ClaudeHomesWithAccounts())
        {
            var projectsDir = Path.Combine(home, "projects");
            if (!Directory.Exists(projectsDir)) continue;
            IEnumerable<string> dirs;
            try { dirs = Directory.GetDirectories(projectsDir); }
            catch { continue; }
            foreach (var dir in dirs)
            {
                IEnumerable<string> files;
                try { files = Directory.GetFiles(dir, "*.jsonl"); }
                catch { continue; }
                foreach (var file in files)
                {
                    try
                    {
                        if (!seen.Add(Path.GetFileNameWithoutExtension(file))) continue;
                        var entry = ReadSessionHead(file, titles, accountId);
                        if (entry is null) continue;
                        if (!byCwd.TryGetValue(entry.Cwd, out var list)) byCwd[entry.Cwd] = list = new();
                        list.Add(entry);
                    }
                    catch { /* unreadable transcript */ }
                }
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
        foreach (var home in ClaudeHomes())
        {
            var path = Path.Combine(home, "history.jsonl");
            if (!File.Exists(path)) continue;
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
        }
        return map;
    }

    private static SessionEntry? ReadSessionHead(string file, Dictionary<string, string> titles, string accountId)
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
        return new SessionEntry(sessionId, finalTitle, cwd, File.GetLastWriteTime(file), branch, "claude", accountId);
    }

    /// <summary>Best-effort transcript replay for resumed sessions (main thread only).</summary>
    public static List<TranscriptMessage> LoadTranscript(string cwd, string sessionId, int maxMessages = 400)
    {
        var result = new List<TranscriptMessage>();
        // Search every Claude home, not just the shared one: a chat that ran under a saved account wrote its
        // transcript into that account's private home, and replaying it from the wrong root silently produced an
        // empty conversation - a chat that opens blank looks exactly like a chat that was deleted.
        string? file = null;
        foreach (var home in ClaudeHomes())
        {
            var projectsDir = Path.Combine(home, "projects");
            if (!Directory.Exists(projectsDir)) continue;
            try
            {
                file = Directory.GetDirectories(projectsDir)
                    .Select(d => Path.Combine(d, sessionId + ".jsonl"))
                    .FirstOrDefault(File.Exists);
            }
            catch { continue; }
            if (file is not null) break;
        }
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
