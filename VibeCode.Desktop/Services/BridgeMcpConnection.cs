using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using VibeCode.AgentStatus.Mcp.Bridge;
using VibeCode.AgentStatus.Mcp.Contracts;

namespace VibeCode.Services;

/// <summary>A chat's built-in bridge endpoint exists before joining a roster, so a running host needs no restart.</summary>
internal sealed class BridgeMcpConnection : IDisposable
{
    public const string ManagedId = "vibecode-bridge-v1";
    internal const int StartupTimeoutSeconds = 30;
    private static readonly string ServerName = McpCatalog.RuntimeServerName(new McpServerDefinition { Id = ManagedId, Name = "bridge" });
    /// <summary>The bridge server's name in a provider's MCP catalog, e.g. "vc_bridge_4efdec509159".</summary>
    internal static string RuntimeName => ServerName;
    internal static readonly string ChatTitleToolName = "mcp__" + ServerName + "__chat_set_title";
    /// <summary>Gemini CLI registers an MCP tool as mcp_&lt;server&gt;_&lt;tool&gt; (single underscores). Antigravity's
    /// ACP title often drops the mcp_ prefix and shows &lt;server&gt;_&lt;tool&gt; instead.</summary>
    private static string GeminiToolName(string tool) => "mcp_" + ServerName + "_" + tool;
    private static readonly string GeminiMcpPrefix = "mcp_" + ServerName + "_";
    private static readonly string GeminiTitlePrefix = ServerName + "_";
    private static readonly string ClaudePrefix = "mcp__" + ServerName + "__";
    internal static string ChatTitleToolNameFor(string provider) => provider switch
    {
        "glm" => "chat_set_title",
        _ => ChatTitleToolName,
    };

    internal const string TaskTitleTool = "bridge_set_task_title";
    internal static string CheckpointToolNameFor(string provider) => provider switch
    {

        "gemini" => GeminiToolName("chat_checkpoint"),
        "glm" => "chat_checkpoint",
        _ => "mcp__" + ServerName + "__chat_checkpoint",
    };

    internal static bool IsTaskCheckpointPermission(string provider, string toolName, JsonNode? input) =>
        MatchesOurTool(toolName, "chat_checkpoint");
    internal const string TodoTool = BridgeMcpTools.TodoToolName;
    internal static readonly string TodoToolName = "mcp__" + ServerName + "__" + TodoTool;
    internal static string TodoToolNameFor(string provider) => provider switch
    {

        "gemini" => GeminiToolName(TodoTool),
        "glm" => TodoTool,
        _ => TodoToolName,
    };

    /// <summary>todo_write only replaces the caller's own checklist, so like the checkpoint tool it skips permission
    /// cards in every mode. Exact registration only: another server's todo_write still asks.</summary>
    internal static bool IsTodoPermission(string provider, string toolName, JsonNode? input) =>
        MatchesOurTool(toolName, TodoTool);

    /// or its use_tool envelope, GLM's bare function, or Gemini's mcp_server_tool / server_tool ACP title.</summary>
    internal static bool IsTodoToolCall(string toolName, JsonNode? input) =>
        MatchesOurTool(toolName, TodoTool)
        || toolName == "use_tool" && input is JsonObject envelope && envelope["tool_name"] is JsonValue target
            && target.TryGetValue<string>(out var name) && MatchesOurTool(name, TodoTool);

    /// <summary>The checklist such a card carries, or null for any other tool or a card whose arguments are still streaming.</summary>
    internal static JsonArray? TodoListOf(string toolName, JsonNode? input)
    {
        if (!IsTodoToolCall(toolName, input)) return null;
        var payload = ToolArguments(toolName == "use_tool" ? (input as JsonObject)?["tool_input"] : input);
        return (payload as JsonObject)?["todos"] as JsonArray;
    }

    internal static string TodoCallInstructions(string provider) =>
        "This built-in checklist action is already authorized in every mode; do not ask for permission. " + provider switch
        {


            "glm" => $"Its exact function name is {TodoTool}; call it directly with the todos argument. ",
            "gemini" => $"Its exact tool name is {TodoToolNameFor(provider)}; call it directly with the todos argument. ",
            // Codex's turn/plan/updated is translated into the same checklist.
            "codex" => $"Its full MCP name is {TodoToolName}. Your built-in update_plan fills the same checklist and also satisfies this rule; use one of them for a task, never both. ",
            _ => $"Its full MCP name is {TodoToolName}. If it is deferred, discover it with tool search and then call it. ",
        };
    internal static readonly string TaskTitleToolName = "mcp__" + ServerName + "__" + TaskTitleTool;
    internal static string TaskTitleToolNameFor(string provider) => provider switch
    {
        "glm" => TaskTitleTool,
        _ => TaskTitleToolName,
    };

    internal const string NameTool = BridgeMcpTools.AgentNameToolName;
    internal static readonly string NameToolName = "mcp__" + ServerName + "__" + NameTool;
    internal static string NameToolNameFor(string provider) => provider switch
    {

        "gemini" => GeminiToolName(NameTool),
        "glm" => NameTool,
        _ => NameToolName,
    };

    /// <summary>How an agent-team member reaches bridge_set_name with its own provider's catalog name.</summary>
    internal static string NameCallInstructions(string provider) =>
        "This built-in naming action is already authorized in every mode; do not ask for permission. " + provider switch
        {

            "glm" => $"Its exact function name is {NameTool}; call it directly with the name argument. ",
            "gemini" => $"Its exact tool name is {NameToolNameFor(provider)}; call it directly with the name argument. ",
            _ => $"Its full MCP name is {NameToolName}. If it is deferred, discover it with tool search and then call it. ",
        };

    internal static string ChatTitleCallInstructions(string provider)
    {
        const string authorization = "This built-in naming action is already authorized in every chat mode; do not ask for permission. ";
        return authorization + TitleToolCall(provider, ChatTitleToolNameFor(provider), "chat_set_title");
    }

    internal static string TaskTitleCallInstructions(string provider) =>
        "This built-in title action is already authorized in every mode; do not ask for permission. " +
        TitleToolCall(provider, TaskTitleToolNameFor(provider), TaskTitleTool);

    internal static string RegularBridgeToolGuideFor(string provider) => BridgeMcpTools.RegularBridgeToolGuide + "\n" +
        (provider switch
        {

            "glm" => "GLM: call bridge_read_chat directly with agent and, for subsequent pages, cursor.",
            "gemini" => $"Gemini: call mcp_{ServerName}_bridge_read_chat directly with agent and, for subsequent pages, cursor.",
            _ => $"The full transcript tool name is mcp__{ServerName}__bridge_read_chat. Discover it with tool search if deferred, then call it with agent and, for subsequent pages, cursor.",
        }) + "\n[END BRIDGE TOOLS]";

    /// <summary>How an ordinary project chat's provider spells project_read_chat. Kept on one line: it rides inside the
    /// [VIBECODE PROJECT CHATS] block, which transcript replay strips as one unit.</summary>
    internal static string ProjectChatReadGuideFor(string provider)
    {
        const string tool = BridgeMcpTools.ProjectReadChatToolName;
        return provider switch
        {

            "glm" => $"GLM: call {tool} directly. ",
            "gemini" => $"Gemini: call {GeminiToolName(tool)} directly. ",
            _ => $"Its full MCP name is {ClaudePrefix}{tool}; discover it with tool search if deferred. ",
        };
    }

    private static string TitleToolCall(string provider, string name, string function) => provider switch
    {
        "glm" => $"Its exact function name is {function}; call it directly with the title argument. ",
        "gemini" => $"Its exact tool name is {name}; call it directly with the title argument. ",
        _ => $"Its full MCP name is {name}. If it is deferred, discover it with tool search and then call it. ",
    };

    /// <summary>The built-in label tools (chat name, bridge task title and an agent-team member's own name) only
    /// relabel the caller's own chat or pane, so they skip permission cards in every mode.</summary>
    internal static bool IsChatTitlePermission(string provider, string toolName, JsonNode? input)
    {
        if (toolName == ChatTitleToolNameFor(provider) || toolName == TaskTitleToolNameFor(provider)) return true;

        // tool, an arbitrary use_tool call, or an invocation file whose actual target isn't in this request.
        return false;
    }

    internal static bool IsChatTitleMcpName(string name) => name == ChatTitleToolName
        || name == TaskTitleToolName;

    internal static bool IsTaskTitleToolName(string name) =>
    name == TaskTitleToolName || name == TaskTitleTool;
    internal static bool MatchesOurTool(string? name, string tool)
    {
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(tool)) return false;
        return name == tool
            || name == ClaudePrefix + tool
            || name == GeminiMcpPrefix + tool
            || name == GeminiTitlePrefix + tool;
    }

    /// <summary>The catalog function inside a provider MCP identity, so "Name chat" / "Update todos" cards can
    /// recognise Claude's mcp__server__tool and Gemini's mcp_server_tool / server_tool titles.</summary>
    internal static string CatalogToolName(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        foreach (var prefix in new[] { ClaudePrefix, GeminiMcpPrefix, GeminiTitlePrefix })
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal) || name.Length <= prefix.Length) continue;
            // Single-underscore prefixes must not match double-underscore registrations.
            if ((prefix == GeminiMcpPrefix || prefix == GeminiTitlePrefix) && name[prefix.Length] == '_') continue;
            return name[prefix.Length..];
        }
        var index = name.LastIndexOf("__", StringComparison.Ordinal);
        return index >= 0 ? name[(index + 2)..] : name;
    }

    /// <summary>Antigravity/Gemini ACP titles for our MCP tools, with or without the mcp_ prefix, with or without
    /// a trailing (args) display. Returns the mcp_server_tool identity instructions tell Gemini to call.</summary>
    internal static string? CanonicalGeminiName(string? title)
    {
        if (string.IsNullOrEmpty(title)) return null;
        var name = title;
        var paren = name.IndexOf('(');
        if (paren > 0) name = name[..paren];
        if (IsGeminiMcpIdentity(name, GeminiMcpPrefix)) return name;
        if (IsGeminiMcpIdentity(name, GeminiTitlePrefix)) return "mcp_" + name;
        return null;
    }

    /// <summary>A single-underscore Gemini identity, not a double-underscore registration.</summary>
    private static bool IsGeminiMcpIdentity(string name, string prefix) =>
        name.StartsWith(prefix, StringComparison.Ordinal) && name.Length > prefix.Length && name[prefix.Length] != '_';

    /// <summary>Gemini MCP rawInput wraps the real fields in <c>arguments</c> and often repeats the tool title
    /// beside them. Lift the payload so todo cards, Name chat and Set task title see the fields they expect.</summary>
    internal static void LiftGeminiArguments(JsonObject input)
    {
        if (input["arguments"] is not JsonObject wrapped) return;
        var args = wrapped.DeepClone().AsObject();
        input.Remove("arguments");
        foreach (var property in args)
            input[property.Key] = property.Value?.DeepClone();
    }

    /// <summary>Read-only view of a tool's real arguments, including Gemini's <c>arguments</c> envelope.</summary>
    internal static JsonNode? ToolArguments(JsonNode? input)
    {
        if (input is not JsonObject obj || obj["arguments"] is not JsonObject args) return input;
        var merged = obj.DeepClone().AsObject();
        merged.Remove("arguments");
        foreach (var property in args.DeepClone().AsObject())
            merged[property.Key] = property.Value?.DeepClone();
        return merged;
    }
    private readonly CancellationTokenSource _stop = new();
    private readonly Dispatcher _dispatcher;
    private readonly Func<string, JsonObject, JsonObject> _invoke;
    private readonly object _httpGate = new();
    private (McpLoopbackHost Host, string Token)? _http;
    public string PipeName { get; } = "vibecode-bridge-" + Guid.NewGuid().ToString("N");

    public BridgeMcpConnection(Dispatcher dispatcher, Func<string, JsonObject, JsonObject> invoke)
    {
        _dispatcher = dispatcher;
        _invoke = invoke;
        _ = Task.Run(ServeAsync);
    }

    public McpServerDefinition Registration()
    {
        // Reuse the packaged/self-contained executable discovery, never a developer-machine path.
        var definition = AgentStatusMcpRegistration.Create();
        definition.Id = ManagedId;
        definition.Name = "bridge";
        definition.Arguments[^1] = "--bridge-mcp";
        definition.UseClaude = definition.UseCodex = true;
        definition.Environment[BridgeMcpClient.PipeEnvironment] = PipeName;
        // The helper is a second launch of the whole single-file VibeCode.exe, which unpacks its compressed bundle
        // first: ~1.6 s on an idle machine and 3-5 s when a Bridge starts several agents at once. The 5 s startup
        // budget inherited from the status reporter therefore dropped the bridge for some of those agents
        // (CONNECT_TIMEOUT), leaving them with no bridge tools at all. Its 10 s tool budget also cut off requests that
        // the pipe itself allows BridgeMcpClient.RequestTimeout (30 s) for.
        definition.StartupTimeoutSeconds = StartupTimeoutSeconds;
        definition.ToolTimeoutSeconds = (int)BridgeMcpClient.RequestTimeout.TotalSeconds + 15;
        return InProcess(definition);
    }

    /// <summary>This chat's route on VibeCode's own loopback MCP endpoint, which needs no helper process; the stdio
    /// relay when the setting is off or the endpoint cannot listen. The pipe keeps serving either way: it is the
    /// relay's transport, GLM's direct one, and what an ACP agent without HTTP MCP support falls back to.</summary>
    private McpServerDefinition InProcess(McpServerDefinition stdio)
    {
        if (!AppSettings.Current.BridgeToolsInProcess) return stdio;
        lock (_httpGate)
        {
            if (_stop.IsCancellationRequested) return stdio;
            if (_http is null)
            {
                if (McpLoopbackHost.Shared is not { } host) return stdio;
                _http = (host, host.Register("/bridge", new BridgeMcpHttpEndpoint(_dispatcher, _invoke), stdio));
            }
            return _http.Value.Host.HttpDefinition(_http.Value.Token, stdio);
        }
    }

    /// <summary>Point each stdio server marked "Share one instance across agents" in <paramref name="servers"/> at its
    /// one shared process. This connection holds the chat's leases, so they end when the chat closes.</summary>
    public void ShareStdioServers(List<McpServerDefinition> servers) => SharedMcpHub.Instance.Project(servers, this);

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                requestTimeout.CancelAfter(BridgeMcpClient.RequestTimeout);
                using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                JsonObject response;
                string? name = null;
                var phase = "request";
                try
                {
                    // Bound input before parsing; this private endpoint exposes only the fixed bridge contract.
                    var line = new StringBuilder();
                    var buffer = new char[1024];
                    int read;
                    while ((read = await reader.ReadAsync(buffer.AsMemory(), requestTimeout.Token).ConfigureAwait(false)) > 0)
                    {
                        var newline = Array.IndexOf(buffer, '\n', 0, read);
                        var length = newline >= 0 ? newline : read;
                        if (line.Length + length > BridgeMcpClient.MaxRequestCharacters)
                            throw new BridgeMcpException("request_too_large", $"Bridge request exceeds {BridgeMcpClient.MaxRequestCharacters} characters.",
                                "Shorten the request to the tool's documented limits.", "not_started");
                        line.Append(buffer, 0, length);
                        if (newline >= 0) break;
                    }
                    if (JsonNode.Parse(line.ToString()) is not JsonObject request)
                        throw new BridgeMcpException("invalid_request", "The Bridge request must be a JSON object containing tool and arguments.",
                            "Use a tool name from tools/list and an arguments object matching its schema.", "not_started");
                    name = AgentStatusReport.Text(request["tool"]);
                    if (string.IsNullOrWhiteSpace(name) || name.Length > 80)
                        throw new BridgeMcpException("invalid_request", "tool must be nonempty text of at most 80 characters.",
                            "Use a tool name from tools/list.", "not_started");
                    var tool = BridgeMcpTools.Create(_invoke).FirstOrDefault(t => t.Name == name)
                        ?? throw new BridgeMcpException("unknown_tool", $"Unknown Bridge tool '{name}'.",
                            "Call tools/list for the tools available on this MCP server.", "not_started");
                    if (request["arguments"] is not JsonObject args)
                        throw new BridgeMcpException("invalid_arguments", "arguments must be a JSON object, including {} for a tool without arguments.",
                            "Correct arguments to match this tool's input schema.", "not_started");
                    phase = "execution";
                    // Input priority, not Normal: WPF runs Input-and-below work only while no real mouse or keyboard
                    // input is waiting. At Normal, a busy team's tool calls (bridge_read_chat rebuilding a 17 MB
                    // transcript, rosters, todos) all ran ahead of the user's clicks, and Stop or "Send prompt now"
                    // took effect tens of seconds late. They still run before Background chores (event drains, saves).
                    var result = await _dispatcher.InvokeAsync(() => tool.Invoke(args), DispatcherPriority.Input,
                        requestTimeout.Token).Task.ConfigureAwait(false);
                    phase = "receipt";
                    response = new JsonObject { ["result"] = result.DeepClone() };
                }
                catch (BridgeMcpException ex) { response = ex.WithContext(name, phase).PipeResponse(); }
                catch (JsonException ex) when (phase == "request")
                {
                    response = new BridgeMcpException("invalid_json",
                        $"Bridge request is malformed JSON at line {ex.LineNumber}, byte {ex.BytePositionInLine}.",
                        "Send one valid JSON object with tool and arguments.", "not_started")
                        .WithContext(name, phase).PipeResponse();
                }
                catch (IOException) when (phase == "request") { throw; }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { response = BridgeMcpDiagnostics.Failure(ex, name, phase).PipeResponse(); }
                await writer.WriteLineAsync(response.ToJsonString().AsMemory(), requestTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        lock (_httpGate)
        {
            if (_http is { } http) http.Host.Unregister(http.Token);   // a closed chat's token stops working at once
            _http = null;
        }
        SharedMcpHub.Instance.Release(this);   // the chat's leases on shared stdio servers end with it too
    }
}
