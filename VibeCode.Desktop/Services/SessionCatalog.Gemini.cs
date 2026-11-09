using System.IO;
using System.Text.Json.Nodes;

namespace VibeCode.Services;

public static partial class SessionCatalog
{
    /// <summary>Gemini chats from VibeCode's own index (see <see cref="GeminiHistory.Remember"/>), each with the
    /// account whose Gemini home holds it, so opening one resumes it there.</summary>
    private static void AddGeminiSessions(Dictionary<string, List<SessionEntry>> byCwd, IReadOnlySet<string>? allowedSessionIds)
    {
        try
        {
            if (!File.Exists(GeminiHistory.IndexPath) || JsonNode.Parse(File.ReadAllText(GeminiHistory.IndexPath)) is not JsonObject index) return;
            foreach (var (id, value) in index)
            {
                if (allowedSessionIds is not null && !allowedSessionIds.Contains(id)) continue;
                if (value?["cwd"]?.ToString() is not { Length: > 0 } cwd) continue;
                var updated = DateTime.TryParse(value["updated"]?.ToString(), out var timestamp) ? timestamp : DateTime.MinValue;
                var account = value["account"]?.ToString();
                if (!byCwd.TryGetValue(cwd, out var sessions)) byCwd[cwd] = sessions = new();
                sessions.Add(new(id, "Gemini chat", cwd, updated, null, "gemini", string.IsNullOrEmpty(account) ? "" : account));
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (System.Text.Json.JsonException) { }
    }
}
