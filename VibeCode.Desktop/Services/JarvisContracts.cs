using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VibeCode.Services;

public sealed record JarvisSelection(string Provider, string? Model, string? Effort, string? AccountId = null)
{
    public static JarvisSelection FromSettings() => new(AppSettings.Current.JarvisProvider,
        AppSettings.Current.JarvisModel, AppSettings.Current.JarvisEffort);
}

public sealed record JarvisDialogueLine(string Role, string Text)
{
    public IReadOnlyList<JarvisMemoryAccess> MemorySources { get; init; } = [];
}
public sealed record JarvisContext(string? CurrentProject, IReadOnlyList<string> KnownProjects,
    string HomeDirectory, bool MemoryEnabled, string MemoryContext = "")
{
    public string? CurrentChatId { get; init; }
    public IReadOnlyList<JarvisChatSummary> OpenChats { get; init; } = [];
    public JarvisMemoryAccess? MemoryAccess { get; init; }
    public Func<JarvisMemoryPermission>? ReadMemoryPermission { get; init; }
    public JsonObject? Settings { get; init; }
    public JarvisMemoryPermission CurrentMemoryPermission => ReadMemoryPermission?.Invoke()
        ?? new(MemoryEnabled, false, null);
}
public sealed record JarvisAction(string Kind, string Path, string Task, string RequestQuote)
{
    public JarvisChatFilter? ChatFilter { get; init; }
    public string Target { get; init; } = "";
    public int Count { get; init; } = 1;
    public IReadOnlyList<JarvisSettingChange> Changes { get; init; } = [];
    public JsonObject? McpConfiguration { get; init; }
}
public sealed record JarvisSettingChange(string Name, string Value);
public sealed record JarvisPlan(string Reply, IReadOnlyList<JarvisAction> Actions);
public sealed record JarvisActionReceipt(string Kind, string Path, string Message, string? SessionId = null,
    string? Provider = null, string? Model = null);
public sealed record JarvisTurn(JarvisPlan Plan, string Provider, string? Model, string? SessionId)
{
    public IReadOnlyList<JarvisMemoryAccess> MemorySources { get; init; } = [];
}

public interface IJarvisPlanner
{
    Task<JarvisTurn> PlanAsync(JarvisSelection selection, JarvisContext context,
        IReadOnlyList<JarvisDialogueLine> history, string request, CancellationToken cancellationToken);
}

public interface IJarvisActionHost
{
    JarvisContext GetContext();
    Task<JarvisActionReceipt> OpenProjectAsync(string path, JarvisSelection selection, CancellationToken cancellationToken);
    Task<JarvisActionReceipt> StartCodingChatAsync(string path, string task, JarvisSelection selection,
        CancellationToken cancellationToken);
}

public interface IJarvisContextHost
{
    Task<JarvisContext> GetContextAsync(string request, CancellationToken cancellationToken);
}

public interface IJarvisChatActionHost
{
    Task<JarvisActionReceipt> ExecuteChatActionAsync(JarvisAction action, CancellationToken cancellationToken);
}

public interface IJarvisSettingsActionHost
{
    Task<JarvisActionReceipt> ExecuteSettingsActionAsync(JarvisAction action, CancellationToken cancellationToken);
}

public interface IJarvisNewChatsHost
{
    Task<JarvisActionReceipt> CreateChatsAsync(string path, int count, CancellationToken cancellationToken);
}

/// <summary>Only validated, typed operations cross the desktop boundary. No command or script field exists.</summary>
public static class JarvisPlanParser
{
    // Keep provider-constrained output beside the parser. A stale provider enum can make a real action
    // impossible for the model to request, even when the prompt and desktop executor support it.
    public static JsonObject CreateOutputSchema() => JsonNode.Parse("""
        {
          "type":"object",
          "properties":{
            "reply":{"type":"string"},
            "actions":{"type":"array","items":{"anyOf":[
              {
                "type":"object",
                "properties":{
                  "kind":{"type":"string","enum":["open_project","create_project","start_chat"]},
                  "path":{"type":"string"},"task":{"type":"string"},"request_quote":{"type":"string"}
                },
                "required":["kind","path","task","request_quote"],"additionalProperties":false
              },
              {
                "type":"object",
                "properties":{
                  "kind":{"type":"string","enum":["list_chats","open_chat","close_chats"]},
                  "path":{"type":"string"},"chat_id":{"type":"string"},
                  "terms":{"type":"array","items":{"type":"string"}},
                  "match":{"type":"string","enum":["all","any"]},
                  "search_in":{"type":"string","enum":["all","title","messages"]},
                  "include_subdirectories":{"type":"boolean"},"all":{"type":"boolean"},
                  "request_quote":{"type":"string"}
                },
                "required":["kind","path","chat_id","terms","match","search_in","include_subdirectories","all","request_quote"],
                "additionalProperties":false
              },
              {
                "type":"object",
                "properties":{
                  "kind":{"type":"string","enum":["list_apps","open_app","focus_app","close_app","open_path","open_url","search_web","system_info"]},
                  "target":{"type":"string"},"request_quote":{"type":"string"}
                },
                "required":["kind","target","request_quote"],"additionalProperties":false
              },
              {
                "type":"object",
                "properties":{
                  "kind":{"type":"string","enum":["create_chats"]},
                  "path":{"type":"string"},"count":{"type":"integer"},"request_quote":{"type":"string"}
                },
                "required":["kind","path","count","request_quote"],"additionalProperties":false
              },
              {
                "type":"object",
                "properties":{
                  "kind":{"type":"string","enum":["read_settings","open_settings","update_settings"]},
                  "target":{"type":"string"},
                  "changes":{"type":"array","items":{
                    "type":"object","properties":{"name":{"type":"string"},"value":{"type":"string"}},
                    "required":["name","value"],"additionalProperties":false
                  }},
                  "request_quote":{"type":"string"}
                },
                "required":["kind","target","changes","request_quote"],"additionalProperties":false
              },
              {
                "type":"object",
                "properties":{
                  "kind":{"type":"string","enum":["configure_mcp","remove_mcp"]},
                  "target":{"type":"string"},"config_json":{"type":"string"},"request_quote":{"type":"string"}
                },
                "required":["kind","target","config_json","request_quote"],"additionalProperties":false
              }
            ]}}
          },
          "required":["reply","actions"],"additionalProperties":false
        }
        """)!.AsObject();

    public static JarvisPlan Parse(string text, string request)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var newline = trimmed.IndexOf('\n');
            if (newline < 0 || !trimmed.EndsWith("```", StringComparison.Ordinal))
                throw new InvalidOperationException("Jarvis returned an incomplete response. Please try again.");
            trimmed = trimmed[(newline + 1)..^3].Trim();
        }
        if (trimmed.Length > 24_000) throw new InvalidOperationException("Jarvis returned an oversized response.");
        try
        {
            var json = JsonNode.Parse(trimmed) as JsonObject
                ?? throw new InvalidOperationException("Jarvis returned an invalid response.");
            RejectUnknown(json, ["reply", "actions"]);
            var reply = ReadText(json, "reply", 6000, required: true);
            if (json["actions"] is not JsonArray items || items.Count > 8)
                throw new InvalidOperationException("Jarvis returned an invalid action list.");
            var actions = new List<JarvisAction>();
            foreach (var node in items)
            {
                if (node is not JsonObject item) throw new InvalidOperationException("Jarvis returned an invalid action.");
                var kind = ReadText(item, "kind", 32, true);
                if (JarvisSettingsActions.IsSettingsAction(kind))
                {
                    RejectUnknown(item, kind is "configure_mcp" or "remove_mcp"
                        ? ["kind", "target", "config_json", "request_quote"]
                        : ["kind", "target", "changes", "request_quote"]);
                    var settingsQuote = ReadText(item, "request_quote", 2000, true);
                    if (!request.Contains(settingsQuote, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("The proposed action was not grounded in your current request.");
                    var settingsAction = new JarvisAction(kind, "", "", settingsQuote)
                    {
                        Target = ReadText(item, "target", 128),
                        Changes = ReadChanges(item),
                        McpConfiguration = ReadConfiguration(item),
                    };
                    JarvisSettingsActions.Validate(settingsAction);
                    actions.Add(settingsAction);
                    continue;
                }
                if (kind == "create_chats")
                {
                    RejectUnknown(item, ["kind", "path", "count", "request_quote"]);
                    var chatPath = JarvisPathPolicy.Normalize(ReadText(item, "path", 1024, true));
                    var count = item["count"] is JsonValue countValue && countValue.TryGetValue<int>(out var amount)
                        && amount is >= 1 and <= 12 ? amount : throw new InvalidOperationException("Create between 1 and 12 chats at a time.");
                    var chatQuote = ReadText(item, "request_quote", 2000, true);
                    if (!request.Contains(chatQuote, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("The proposed action was not grounded in your current request.");
                    actions.Add(new(kind, chatPath, "", chatQuote) { Count = count });
                    continue;
                }
                if (JarvisDesktopPolicy.IsDesktopAction(kind))
                {
                    RejectUnknown(item, ["kind", "target", "request_quote"]);
                    var target = ReadText(item, "target", 2048);
                    var desktopQuote = ReadText(item, "request_quote", 2000, true);
                    if (!request.Contains(desktopQuote, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("The proposed action was not grounded in your current request.");
                    JarvisDesktopPolicy.Validate(kind, target);
                    actions.Add(new(kind, "", "", desktopQuote) { Target = target });
                    continue;
                }
                var chatAction = kind is "list_chats" or "open_chat" or "close_chats";
                if (!chatAction && kind is not ("open_project" or "create_project" or "start_chat"))
                    throw new InvalidOperationException("Jarvis requested an unsupported action.");
                RejectUnknown(item, chatAction
                    ? ["kind", "path", "task", "request_quote", "chat_id", "terms", "match", "search_in", "include_subdirectories", "all"]
                    : ["kind", "path", "task", "request_quote"]);
                var path = ReadText(item, "path", 1024, !chatAction);
                if (path.Length > 0) path = JarvisPathPolicy.Normalize(path);
                var task = ReadText(item, "task", 12_000, kind == "start_chat");
                if (kind != "start_chat" && task.Length != 0)
                    throw new InvalidOperationException("Only a coding chat may receive a coding task.");
                var quote = ReadText(item, "request_quote", 2000, true);
                if (!request.Contains(quote, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The proposed action was not grounded in your current request.");
                JarvisChatFilter? filter = null;
                if (chatAction)
                {
                    filter = new(ReadText(item, "chat_id", 128), path, ReadTerms(item),
                        ReadText(item, "match", 8) is { Length: > 0 } match ? match : "all",
                        ReadText(item, "search_in", 16) is { Length: > 0 } searchIn ? searchIn : "all",
                        ReadBoolean(item, "include_subdirectories"), ReadBoolean(item, "all"));
                    JarvisChatPolicy.Validate(kind, filter);
                }
                actions.Add(new(kind, path, task, quote) { ChatFilter = filter });
            }
            // Creating a project already opens it. Accept a model's redundant create+open plan without
            // creating a second chat; its path and literal request authorization have both been validated.
            if (actions.Count == 2 && actions[0].Kind == "create_project" && actions[1].Kind == "open_project"
                && JarvisPathPolicy.PathsEqual(actions[0].Path, actions[1].Path)) actions.RemoveAt(1);
            if (actions.Where(a => a.Kind == "create_chats").Sum(a => a.Count) > 12)
                throw new InvalidOperationException("Create at most 12 chats in one request.");
            return new(reply, actions);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            throw new InvalidOperationException("Jarvis could not validate its response. " + ex.Message, ex);
        }
    }

    private static IReadOnlyList<JarvisSettingChange> ReadChanges(JsonObject item)
    {
        if (item["changes"] is null) return [];
        if (item["changes"] is not JsonArray changes || changes.Count > 20)
            throw new InvalidOperationException("Change at most 20 settings in one action.");
        return changes.Select(node =>
        {
            if (node is not JsonObject change) throw new InvalidOperationException("Invalid setting change.");
            RejectUnknown(change, ["name", "value"]);
            return new JarvisSettingChange(JarvisSettingsCatalog.Property(ReadText(change, "name", 128, true)).Name,
                ReadText(change, "value", 4096));
        }).ToArray();
    }

    private static JsonObject? ReadConfiguration(JsonObject item)
    {
        var config = ReadText(item, "config_json", 16_000);
        return config.Length == 0 ? null : JsonNode.Parse(config) as JsonObject
            ?? throw new InvalidOperationException("An MCP configuration must be a JSON object.");
    }

    private static void RejectUnknown(JsonObject json, string[] allowed)
    {
        if (json.Any(pair => !allowed.Contains(pair.Key, StringComparer.Ordinal)))
            throw new InvalidOperationException("The response contained unsupported fields.");
    }

    private static bool ReadBoolean(JsonObject json, string key)
    {
        if (json[key] is null) return false;
        if (json[key] is JsonValue value && value.TryGetValue<bool>(out var result)) return result;
        throw new InvalidOperationException($"Invalid {key}.");
    }

    private static string[] ReadTerms(JsonObject json)
    {
        if (json["terms"] is null) return [];
        if (json["terms"] is not JsonArray terms || terms.Count > 20)
            throw new InvalidOperationException("Use at most 20 search words or phrases.");
        return terms.Select(term => term is JsonValue value && value.TryGetValue<string>(out var text)
                && !string.IsNullOrWhiteSpace(text) && text.Length <= 500 && !text.Contains('\0')
            ? text.Trim() : throw new InvalidOperationException("Search words or phrases cannot be empty.")).ToArray();
    }

    private static string ReadText(JsonObject json, string key, int maximum, bool required = false)
    {
        if (json[key] is not JsonValue value || !value.TryGetValue<string>(out var text))
        {
            if (!required && json[key] is null) return "";
            throw new InvalidOperationException($"Missing {key}.");
        }
        text = text.Trim();
        if (text.Length > maximum || (required && text.Length == 0) || text.Any(c => c == '\0'))
            throw new InvalidOperationException($"Invalid {key}.");
        return text;
    }
}

public static class JarvisPathPolicy
{
    public static string Normalize(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)
            || path.Contains('\0') || path.Split(['\\', '/']).Any(segment => segment is "." or "..")
            || path.IndexOf(':', 2) >= 0 || path.Length > 1024)
            throw new ArgumentException("Use an absolute local folder path, without traversal or device paths.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    public static bool PathsEqual(string left, string right) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);

    public static void RequireExisting(string path)
    {
        path = Normalize(path);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException($"Folder does not exist: {path}");
    }

    public static void RequireNew(string path)
    {
        path = Normalize(path);
        if (Path.GetPathRoot(path) == path || Directory.Exists(path) || File.Exists(path))
            throw new IOException("A new project must use a new folder; existing folders and files are preserved.");
        var parent = Path.GetDirectoryName(path);
        if (parent is null || !Directory.Exists(parent))
            throw new DirectoryNotFoundException("Choose an existing parent folder for the new project.");
        var name = Path.GetFileName(path);
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.') || name.EndsWith(' ')
            || System.Text.RegularExpressions.Regex.IsMatch(name, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            throw new ArgumentException("Choose a valid Windows folder name.");
        for (var directory = new DirectoryInfo(parent); directory is not null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Create the project beneath a physical folder, outside junctions and symbolic links.");
    }

    public static void CreateNew(string path)
    {
        RequireNew(path);
        // Unlike Directory.CreateDirectory, this cannot silently accept a folder created concurrently.
        if (!CreateDirectory(path, IntPtr.Zero))
            throw new IOException("The new project folder could not be created. " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
        RequireExisting(path);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectory(string path, IntPtr securityAttributes);
}
