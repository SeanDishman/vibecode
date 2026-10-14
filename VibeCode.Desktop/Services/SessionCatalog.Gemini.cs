using System.IO;
using System.Text.Json.Nodes;

namespace VibeCode.Services;

public static partial class SessionCatalog
{
    private static void AddGeminiSessions(Dictionary<string, List<SessionEntry>> byCwd, IReadOnlySet<string>? allowedSessionIds)
    {
        try
        {
            if (!File.Exists(LegacyGeminiHistory.IndexPath) || JsonNode.Parse(File.ReadAllText(LegacyGeminiHistory.IndexPath)) is not JsonObject index) return;
            foreach (var (id, value) in index)
            {
                if (allowedSessionIds is not null && !allowedSessionIds.Contains(id)) continue;
                if (value?["cwd"]?.ToString() is not { Length: > 0 } cwd) continue;
                var updated = DateTime.TryParse(value["updated"]?.ToString(), out var timestamp) ? timestamp : DateTime.MinValue;
                if (!byCwd.TryGetValue(cwd, out var sessions)) byCwd[cwd] = sessions = new();
                sessions.Add(new(id, "Gemini chat", cwd, updated, null, "gemini"));
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (System.Text.Json.JsonException) { }
    }
}
