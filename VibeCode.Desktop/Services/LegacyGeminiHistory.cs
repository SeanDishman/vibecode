using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VibeCode.Services;

/// <summary>Read-only access to conversations saved before Gemini CLI support was removed.
/// This never loads credentials, starts a CLI, or contacts Google.</summary>
internal static class LegacyGeminiHistory
{
    internal const string RemovalNotice = "Gemini CLI support has been removed. This saved chat is read-only. Start a new chat with another provider to continue.";
    internal static string IndexPath => Path.Combine(AppSettings.Dir, "gemini", "sessions.json");

    internal static List<TranscriptMessage> LoadTranscript(string sessionId)
    {
        if (!GlmTranscriptStore.IsValidId(sessionId)) return [];
        var home = Environment.GetEnvironmentVariable("GEMINI_CLI_HOME");
        if (string.IsNullOrWhiteSpace(home)) home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = Path.Combine(home, ".gemini", "tmp");
        if (!Directory.Exists(root)) return [];
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, $"session-*{sessionId[..Math.Min(8, sessionId.Length)]}*.json*", SearchOption.AllDirectories))
            {
                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);
                    if (Parse(reader.ReadToEnd(), sessionId) is { } messages) return messages;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return [];
    }

    internal static List<TranscriptMessage>? Parse(string contents, string sessionId)
    {
        var records = new List<JsonObject>();
        var identified = false;
        try
        {
            if (JsonNode.Parse(contents) is JsonObject document && document["messages"] is JsonArray messages)
            {
                if (document["sessionId"]?.ToString() != sessionId) return null;
                identified = true;
                records.AddRange(messages.OfType<JsonObject>());
            }
        }
        catch (JsonException) { }
        if (!identified)
        {
            var upserts = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            foreach (var line in contents.Split('\n'))
            {
                try
                {
                    if (JsonNode.Parse(line) is not JsonObject record) continue;
                    if (record["sessionId"] is { } id)
                    {
                        if (id.ToString() != sessionId) return null;
                        identified = true;
                    }
                    if (record["id"]?.ToString() is not { Length: > 0 } messageId) continue;
                    if (!upserts.TryGetValue(messageId, out var message))
                    {
                        upserts[messageId] = message = new JsonObject();
                        records.Add(message);
                    }
                    foreach (var (key, value) in record) message[key] = value?.DeepClone();
                }
                catch (JsonException) { /* tolerate an interrupted final append */ }
            }
        }
        if (!identified) return null;
        var result = new List<TranscriptMessage>();
        foreach (var record in records)
        {
            var role = record["type"]?.ToString() switch { "user" => "user", "gemini" => "assistant", _ => null };
            if (role is null) continue;
            var content = Text(record["content"]);
            if (string.IsNullOrWhiteSpace(content)) continue;
            JsonNode blocks = role == "assistant"
                ? new JsonArray(new JsonObject { ["type"] = "text", ["text"] = content })
                : JsonValue.Create(content)!;
            result.Add(new TranscriptMessage(role, new JsonObject { ["role"] = role, ["content"] = blocks }, null));
        }
        return result;
    }

    private static string Text(JsonNode? node) => node switch
    {
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        JsonArray parts => string.Join("\n", parts.Select(Text).Where(text => text.Length > 0)),
        JsonObject part when part["text"] is JsonValue value && value.TryGetValue<string>(out var text) => text,
        JsonObject content when content["parts"] is JsonArray parts => Text(parts),
        _ => "",
    };
}
