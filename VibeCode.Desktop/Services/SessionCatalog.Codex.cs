using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using VibeCode.Protocol;

namespace VibeCode.Services;

public static partial class SessionCatalog
{
    private static void AddCodexSessions(Dictionary<string, List<SessionEntry>> byCwd, IReadOnlySet<string>? allowedIds)
    {
        var homes = new List<(string Home, string? Account)>();
        var store = Environment.GetEnvironmentVariable("VIBECODE_CODEX_ACCOUNT_STORE") ?? Path.Combine(AppSettings.Dir, "codex-accounts");
        try
        {
            if (Directory.Exists(store))
                foreach (var directory in Directory.GetDirectories(store))
                {
                    try
                    {
                        var metadata = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "account.json")));
                        var id = metadata?["Id"]?.GetValue<string>();
                        if (id is not null)
                        {
                            var folder = metadata?["HomeFolder"]?.GetValue<string>();
                            var home = metadata?["UsesLegacyHome"]?.GetValue<bool>() == true
                                ? Environment.GetEnvironmentVariable("VIBECODE_CODEX_LEGACY_HOME") ?? CodexEnvironment.HomeDirectory
                                : Path.Combine(directory, string.IsNullOrWhiteSpace(folder) ? "home" : folder);
                            homes.Add((home, id));
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        homes.Add((Environment.GetEnvironmentVariable("VIBECODE_CODEX_LEGACY_HOME") ?? CodexEnvironment.HomeDirectory, null));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (home, account) in homes)
            foreach (var folder in new[] { "sessions", "archived_sessions" })
            {
                string[] files;
                try { files = Directory.Exists(Path.Combine(home, folder)) ? Directory.GetFiles(Path.Combine(home, folder), "*.jsonl", SearchOption.AllDirectories) : []; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                foreach (var file in files)
                {
                    try
                    {
                        var name = Path.GetFileNameWithoutExtension(file);
                        // Rollout filenames end with the UUID. Filter before opening unrelated transcripts.
                        var suffix = name.Length >= 36 ? name[^36..] : name;
                        if (allowedIds is not null && !allowedIds.Contains(suffix)) continue;
                        using var reader = new StreamReader(file);
                        var head = JsonNode.Parse(reader.ReadLine() ?? "{}");
                        var payload = head?["payload"];
                        var id = payload?["id"]?.GetValue<string>();
                        var cwd = payload?["cwd"]?.GetValue<string>();
                        if (id is null || cwd is null || (allowedIds is not null && !allowedIds.Contains(id)) || !seen.Add(id)) continue;
                        var title = id[..Math.Min(8, id.Length)];
                        // The app's saved title overrides this fallback when publishing the history list.
                        for (var i = 0; i < 30 && reader.ReadLine() is { } line; i++)
                        {
                            JsonNode? row;
                            try { row = JsonNode.Parse(line); } catch { continue; }
                            if (row?["type"]?.GetValue<string>() == "event_msg" && row["payload"]?["type"]?.GetValue<string>() == "user_message")
                            {
                                title = row["payload"]?["message"]?.GetValue<string>() ?? title;
                                title = title.Replace('\r', ' ').Replace('\n', ' ').Trim();
                                if (title.Length > 80) title = title[..80] + "…";
                                break;
                            }
                        }
                        if (!byCwd.TryGetValue(cwd, out var list)) byCwd[cwd] = list = new();
                        list.Add(new SessionEntry(id, title, cwd, File.GetLastWriteTime(file), null, "codex", account, ReadLastAssistantTime(file)));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException) { }
                }
            }
    }

    // A bounded tail read avoids parsing megabytes of transcript on each catalog refresh. The persisted
    // activity metadata is authoritative for new replies; file mtime remains the fallback for legacy histories.
    private static DateTimeOffset? ReadLastAssistantTime(string file)
    {
        const int maxBytes = 128 * 1024;
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var offset = Math.Max(0, stream.Length - maxBytes);
            stream.Seek(offset, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            if (offset > 0) reader.ReadLine();
            DateTimeOffset? latest = null;
            while (reader.ReadLine() is { } line)
            {
                try
                {
                    var row = JsonNode.Parse(line);
                    var type = row?["type"]?.GetValue<string>();
                    var payload = row?["payload"];
                    var assistant = type == "assistant" || (type == "response_item" && payload?["role"]?.GetValue<string>() == "assistant")
                        || (type == "event_msg" && payload?["type"]?.GetValue<string>() == "agent_message");
                    if (assistant && DateTimeOffset.TryParse(row?["timestamp"]?.GetValue<string>(), out var timestamp)
                        && (latest is null || timestamp > latest)) latest = timestamp;
                }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException) { }
            }
            return latest;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
