using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VibeCode.Services;

/// <summary>
/// The on-disk home of GLM conversations, because no provider keeps one for us.
///
/// Every other provider is a CLI that writes its own transcript (~/.claude, ~/.codex, ~/.kimi-code, ~/.grok) and resumes
/// from it. GLM runs in-process (see <see cref="Protocol.GlmSession"/>), so its conversation used to live only in
/// memory: restarting VibeCode, reopening the chat from history, or anything that rebuilt the pane started the model
/// from nothing and showed a blank transcript. One file per session holds the exact OpenAI message array the next
/// request needs, so a resumed session sends the model what it would have sent had it never stopped.
/// </summary>
public static class GlmTranscriptStore
{
    private const int Version = 1;

    public static string Directory => Path.Combine(AppSettings.Dir, "glm-sessions");

    /// <param name="Messages">The conversation without the system prompt, which is rebuilt fresh on every start.</param>
    /// <param name="FailedToolCalls">Tool call ids whose result was an error. The wire format has nowhere to say so, and
    /// the replayed card should still be red.</param>
    public sealed record Snapshot(string SessionId, string Cwd, string? AccountId, string? Model, DateTime Updated,
        IReadOnlyList<JsonObject> Messages, IReadOnlyCollection<string> FailedToolCalls);

    /// <summary>Session ids become file names, and a resumed id comes back out of settings.json - so only plain ids.</summary>
    public static bool IsValidId(string? sessionId) =>
        !string.IsNullOrWhiteSpace(sessionId) && sessionId.Length <= 128
        && sessionId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static string PathFor(string directory, string sessionId) => Path.Combine(directory, sessionId + ".json");

    /// <summary>Write the whole conversation atomically, so a crash mid-save leaves the previous copy intact.</summary>
    public static void Save(string directory, Snapshot snapshot)
    {
        if (!IsValidId(snapshot.SessionId)) return;
        var messages = new JsonArray();
        foreach (var message in snapshot.Messages) messages.Add(message.DeepClone());
        var document = new JsonObject
        {
            ["version"] = Version,
            ["sessionId"] = snapshot.SessionId,
            ["cwd"] = snapshot.Cwd,
            ["accountId"] = snapshot.AccountId,
            ["model"] = snapshot.Model,
            ["updated"] = snapshot.Updated.ToUniversalTime().ToString("o"),
            ["failedToolCalls"] = new JsonArray(snapshot.FailedToolCalls.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
            ["messages"] = messages,
        };
        System.IO.Directory.CreateDirectory(directory);
        var path = PathFor(directory, snapshot.SessionId);
        var temp = path + ".tmp";
        File.WriteAllText(temp, document.ToJsonString(), new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>The saved conversation, or null when there is none or it cannot be read.</summary>
    public static Snapshot? Load(string directory, string sessionId)
    {
        if (!IsValidId(sessionId)) return null;
        var path = PathFor(directory, sessionId);
        try { return File.Exists(path) ? Parse(File.ReadAllText(path), File.GetLastWriteTime(path)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Every readable saved conversation whose id passes <paramref name="include"/>, for the history list.</summary>
    public static IEnumerable<Snapshot> List(string directory, Func<string, bool> include)
    {
        string[] files;
        try { files = System.IO.Directory.Exists(directory) ? System.IO.Directory.GetFiles(directory, "*.json") : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var file in files)
        {
            // Filter on the file name before reading: only owned sessions are listed, and each file is a whole chat.
            var id = Path.GetFileNameWithoutExtension(file);
            if (!include(id)) continue;
            if (Load(directory, id) is { } snapshot) yield return snapshot;
        }
    }

    private static Snapshot? Parse(string text, DateTime fileTime)
    {
        JsonObject? root;
        try { root = JsonNode.Parse(text) as JsonObject; }
        catch (JsonException) { return null; }
        if (root?["sessionId"] is not JsonValue idValue || !idValue.TryGetValue<string>(out var id) || !IsValidId(id))
            return null;
        var messages = (root["messages"] as JsonArray)?.OfType<JsonObject>()
            .Where(m => Text(m["role"]) is "user" or "assistant" or "tool")
            .Select(m => (JsonObject)m.DeepClone())
            .ToList() ?? [];
        var failed = (root["failedToolCalls"] as JsonArray)?.Select(Text).OfType<string>().ToList() ?? [];
        var updated = DateTime.TryParse(Text(root["updated"]), null, System.Globalization.DateTimeStyles.RoundtripKind,
            out var parsed) ? parsed.ToLocalTime() : fileTime;
        return new Snapshot(id, Text(root["cwd"]) ?? "", Text(root["accountId"]), Text(root["model"]), updated,
            messages, failed);
    }

    /// <summary>The chat's first prompt as the user typed it - the history list's fallback title.</summary>
    public static string? FirstPrompt(Snapshot snapshot)
    {
        foreach (var message in snapshot.Messages)
        {
            if (Text(message["role"]) != "user" || Text(message["content"]) is not { } content) continue;
            var typed = UI.ChatViewModel.StripInjectedPrelude(content).Trim();
            if (typed.Length > 0) return typed;
        }
        return null;
    }

    /// <summary>
    /// The saved conversation re-shaped into the Anthropic messages the transcript already replays for Claude, so a
    /// reopened GLM chat renders its prompts, replies, thinking and tool cards exactly as it did live.
    /// </summary>
    public static List<TranscriptMessage> LoadTranscript(string directory, string sessionId)
    {
        var result = new List<TranscriptMessage>();
        if (Load(directory, sessionId) is not { } snapshot) return result;
        var failed = snapshot.FailedToolCalls.ToHashSet(StringComparer.Ordinal);
        foreach (var message in snapshot.Messages)
        {
            switch (Text(message["role"]))
            {
                case "user" when Text(message["content"]) is { Length: > 0 } text:
                    result.Add(new TranscriptMessage("user",
                        new JsonObject { ["role"] = "user", ["content"] = text }, null));
                    break;
                case "assistant":
                {
                    var blocks = new JsonArray();
                    if (Text(message["reasoning_content"]) is { Length: > 0 } thinking)
                        blocks.Add(new JsonObject { ["type"] = "thinking", ["thinking"] = thinking });
                    if (Text(message["content"]) is { Length: > 0 } text)
                        blocks.Add(new JsonObject { ["type"] = "text", ["text"] = text });
                    foreach (var call in (message["tool_calls"] as JsonArray)?.OfType<JsonObject>() ?? [])
                        blocks.Add(new JsonObject
                        {
                            ["type"] = "tool_use",
                            ["id"] = Text(call["id"]) ?? "",
                            ["name"] = Text(call["function"]?["name"]) ?? "?",
                            ["input"] = Arguments(Text(call["function"]?["arguments"])),
                        });
                    if (blocks.Count > 0)
                        result.Add(new TranscriptMessage("assistant",
                            new JsonObject { ["role"] = "assistant", ["content"] = blocks }, null));
                    break;
                }
                case "tool":
                {
                    var id = Text(message["tool_call_id"]) ?? "";
                    result.Add(new TranscriptMessage("user", new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = new JsonArray(new JsonObject
                        {
                            ["type"] = "tool_result",
                            ["tool_use_id"] = id,
                            ["content"] = Text(message["content"]) ?? "",
                            ["is_error"] = failed.Contains(id),
                        }),
                    }, null));
                    break;
                }
            }
        }
        return result;
    }

    private static JsonObject Arguments(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return new JsonObject();
        try { return JsonNode.Parse(arguments) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { return new JsonObject(); }
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
