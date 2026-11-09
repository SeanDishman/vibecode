using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using VibeCode.Protocol;

namespace VibeCode.Services;

/// <summary>
/// Gemini chats as Gemini CLI saves them: <c>&lt;home&gt;/.gemini/tmp/&lt;project&gt;/chats/session-…-&lt;id8&gt;.jsonl</c>, one
/// metadata record followed by message records that are re-appended whole when they change. Read-only: this never
/// loads credentials, starts a CLI, or contacts Google, so a resumed chat shows its history even while signed out.
/// </summary>
internal static class GeminiHistory
{
    private const string SystemContextOpen = "<vibecode-system-context>";
    private const string SystemContextClose = "</vibecode-system-context>";
    private const int MaxToolResultCharacters = 20_000;
    private static readonly object IndexGate = new();

    /// <summary>VibeCode's own list of Gemini chats for the project browser: id → cwd, last update, account.</summary>
    internal static string IndexPath => Path.Combine(AppSettings.Dir, "gemini", "sessions.json");

    /// <summary>Record that a Gemini chat exists, so the project browser can list and resume it under its account.</summary>
    internal static void Remember(string sessionId, string cwd, string? accountId)
    {
        if (!GlmTranscriptStore.IsValidId(sessionId) || string.IsNullOrWhiteSpace(cwd)) return;
        try
        {
            lock (IndexGate)
            {
                var index = File.Exists(IndexPath) && JsonNode.Parse(File.ReadAllText(IndexPath)) is JsonObject existing
                    ? existing
                    : new JsonObject();
                index[sessionId] = new JsonObject
                {
                    ["cwd"] = cwd,
                    ["updated"] = DateTime.UtcNow.ToString("O"),
                    ["account"] = string.IsNullOrEmpty(accountId) ? null : accountId,
                };
                Directory.CreateDirectory(Path.GetDirectoryName(IndexPath)!);
                var temporary = IndexPath + ".tmp-" + Environment.ProcessId;
                File.WriteAllText(temporary, index.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temporary, IndexPath, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // The browser list is a convenience; the chat itself is unaffected.
        }
    }

    /// <summary>A saved conversation's transcript, from the Gemini home of the account it ran under.</summary>
    internal static List<TranscriptMessage> LoadTranscript(string sessionId, string? accountId = null)
    {
        if (!GlmTranscriptStore.IsValidId(sessionId)) return [];
        var root = Path.Combine(HistoryHomeFor(accountId), ".gemini", "tmp");
        if (!Directory.Exists(root)) return [];
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, $"session-*{sessionId[..Math.Min(8, sessionId.Length)]}*.json*",
                         SearchOption.AllDirectories))
            {
                // A backup or temp copy next to the live file (".jsonl.unreadable-…", ".jsonl.tmp-…") is not the chat.
                if (!file.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
                    && !file.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
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
            // Releases before JSONL wrote one JSON document per chat.
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
            // JSONL: a changed message is appended again with the same id; the last copy wins.
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
            switch (record["type"]?.ToString())
            {
                case "user":
                {
                    var text = StripSystemContext(Text(record["displayContent"]) is { Length: > 0 } shown
                        ? shown : Text(record["content"]));
                    if (string.IsNullOrWhiteSpace(text)) break;
                    result.Add(new TranscriptMessage("user",
                        new JsonObject { ["role"] = "user", ["content"] = text }, null));
                    break;
                }
                case "gemini":
                    AddModelTurn(record, result);
                    break;
            }
        }
        return result;
    }

    /// <summary>One Gemini answer: its thoughts, text and tool calls, then the tool results as the next user turn.</summary>
    private static void AddModelTurn(JsonObject record, List<TranscriptMessage> result)
    {
        var blocks = new JsonArray();
        foreach (var thought in (record["thoughts"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            var subject = thought["subject"]?.ToString();
            var description = thought["description"]?.ToString();
            var thinking = string.IsNullOrWhiteSpace(subject) ? description : $"**{subject}**\n{description}";
            if (!string.IsNullOrWhiteSpace(thinking)) blocks.Add(new JsonObject { ["type"] = "thinking", ["thinking"] = thinking });
        }
        var text = Text(record["content"]);
        if (!string.IsNullOrWhiteSpace(text)) blocks.Add(new JsonObject { ["type"] = "text", ["text"] = text });

        var results = new JsonArray();
        foreach (var call in (record["toolCalls"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            if (call["id"]?.ToString() is not { Length: > 0 } id) continue;
            var (name, input) = ToolCall(call["name"]?.ToString() ?? "tool", call["args"] as JsonObject);
            blocks.Add(new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = name, ["input"] = input });
            var status = call["status"]?.ToString();
            if (status is null) continue;
            results.Add(new JsonObject
            {
                ["type"] = "tool_result",
                ["tool_use_id"] = id,
                ["content"] = ToolResultText(call),
                ["is_error"] = status is "error" or "cancelled",
            });
        }
        if (blocks.Count > 0)
            result.Add(new TranscriptMessage("assistant",
                new JsonObject { ["role"] = "assistant", ["content"] = blocks }, null));
        if (results.Count > 0)
            result.Add(new TranscriptMessage("user", new JsonObject { ["role"] = "user", ["content"] = results }, null));
    }

    /// <summary>Gemini's built-in tools under the names VibeCode's tool rows know, with their argument names.</summary>
    internal static (string Name, JsonObject Input) ToolCall(string name, JsonObject? args)
    {
        args ??= new JsonObject();
        string? Arg(params string[] keys) =>
            keys.Select(key => args[key]?.ToString()).FirstOrDefault(value => !string.IsNullOrEmpty(value));
        JsonNode? Copy(params string[] keys) =>
            keys.Select(key => args[key]).FirstOrDefault(value => value is not null && value.ToString() is { Length: > 0 })
                ?.DeepClone();
        JsonObject With(params (string Key, JsonNode? Value)[] values)
        {
            var input = new JsonObject();
            foreach (var (key, value) in values)
                if (value is not null) input[key] = value;
            ToolFileRange.NormalizeGemini(input);
            return input;
        }
        JsonNode? Text(params string[] keys) => Arg(keys) is { } value ? JsonValue.Create(value) : null;
        return name switch
        {
            "read_file" or "view_file" => ("Read", With(("file_path", Text("file_path", "AbsolutePath", "absolute_path", "path")),
                ("start_line", Copy("start_line", "StartLine")), ("end_line", Copy("end_line", "EndLine")),
                ("offset", Copy("offset")), ("limit", Copy("limit")))),
            "write_file" or "write_to_file" => ("Write", With(("file_path", Text("file_path", "TargetFile", "path")),
                ("content", Text("content", "CodeContent")),
                ("start_line", Copy("start_line", "StartLine")), ("end_line", Copy("end_line", "EndLine")))),
            "replace" or "replace_file_content" => ("Edit", With(("file_path", Text("file_path", "TargetFile", "path")),
                ("old_string", Text("old_string", "TargetContent") ?? JsonValue.Create("")),
                ("new_string", Text("new_string", "ReplacementContent") ?? JsonValue.Create("")),
                ("start_line", Copy("start_line", "StartLine")), ("end_line", Copy("end_line", "EndLine")))),
            "run_shell_command" => ("Bash", With(("command", Text("command")), ("description", Text("description")))),
            "search_file_content" or "grep_search" => ("Grep", With(("pattern", Text("pattern")),
                ("path", Text("dir_path", "path")), ("glob", Text("include")))),
            "glob" => ("Glob", With(("pattern", Text("pattern")), ("path", Text("dir_path", "path")))),
            "list_directory" => ("LS", With(("path", Text("dir_path", "path")))),
            "google_web_search" => ("WebSearch", With(("query", Text("query")))),
            "web_fetch" => ("WebFetch", With(("prompt", Text("prompt")), ("url", Text("url")))),
            _ => GeminiMcp(name, args),
        };
    }

    /// <summary>MCP calls in Gemini's session file keep the CLI's function name and often wrap fields in arguments.</summary>
    private static (string Name, JsonObject Input) GeminiMcp(string name, JsonObject args)
    {
        var input = args.DeepClone().AsObject();
        BridgeMcpConnection.LiftGeminiArguments(input);
        if (BridgeMcpConnection.CanonicalGeminiName(name) is { } canonical) name = canonical;
        return (name, input);
    }

    private static string ToolResultText(JsonObject call)
    {
        var text = call["resultDisplay"] switch
        {
            JsonValue value when value.TryGetValue<string>(out var display) => display,
            JsonObject diff when diff["fileDiff"]?.ToString() is { Length: > 0 } fileDiff => fileDiff,
            _ => null,
        };
        if (string.IsNullOrEmpty(text))
            text = string.Join("\n", (call["result"] as JsonArray)?.OfType<JsonObject>()
                .Select(part => part["functionResponse"]?["response"] as JsonObject)
                .Select(response => response?["output"]?.ToString() ?? response?["error"]?.ToString())
                .Where(value => !string.IsNullOrEmpty(value)) ?? []);
        return text.Length <= MaxToolResultCharacters ? text : text[..MaxToolResultCharacters] + "…";
    }

    /// <summary>VibeCode's session instructions ride on the first prompt; they are not part of what the user typed.</summary>
    private static string StripSystemContext(string text)
    {
        if (!text.StartsWith(SystemContextOpen, StringComparison.Ordinal)) return text;
        var end = text.IndexOf(SystemContextClose, StringComparison.Ordinal);
        return end < 0 ? "" : text[(end + SystemContextClose.Length)..].TrimStart('\r', '\n');
    }

    private static string Text(JsonNode? node) => node switch
    {
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        JsonArray parts => string.Join("\n", parts.Select(Text).Where(text => text.Length > 0)),
        JsonObject part when part["text"] is JsonValue value && value.TryGetValue<string>(out var text) => text,
        JsonObject content when content["parts"] is JsonArray parts => Text(parts),
        _ => "",
    };
    internal static string HistoryHomeFor(string? accountId)
    {
        if (accountId is { Length: > 0 } && accountId.All(char.IsAsciiLetterOrDigit)
            && Path.Combine(AppSettings.Dir, "gemini", "homes", accountId) is var orphan && Directory.Exists(orphan))
            return orphan;
        return Environment.GetEnvironmentVariable("GEMINI_CLI_HOME") is { Length: > 0 } configured
            ? configured.Trim('"') : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }
}
