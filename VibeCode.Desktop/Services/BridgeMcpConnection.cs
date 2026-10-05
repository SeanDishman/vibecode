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
    internal static string ChatTitleToolNameFor(string provider) => provider switch
    {
        "grok" => ServerName + "__chat_set_title",
        "glm" => "chat_set_title",
        _ => ChatTitleToolName,
    };

    internal const string TaskTitleTool = "bridge_set_task_title";
    internal static readonly string TaskTitleToolName = "mcp__" + ServerName + "__" + TaskTitleTool;
    internal static string TaskTitleToolNameFor(string provider) => provider switch
    {
        "grok" => ServerName + "__" + TaskTitleTool,
        "glm" => TaskTitleTool,
        _ => TaskTitleToolName,
    };

    internal static string ChatTitleCallInstructions(string provider)
    {
        const string authorization = "This built-in naming action is already authorized in every chat mode; do not ask for permission. ";
        return authorization + TitleToolCall(provider, ChatTitleToolNameFor(provider), "chat_set_title");
    }

    internal static string TaskTitleCallInstructions(string provider) =>
        "This built-in title action is already authorized in every mode; do not ask for permission. " +
        TitleToolCall(provider, TaskTitleToolNameFor(provider), TaskTitleTool);

    private static string TitleToolCall(string provider, string name, string function) => provider switch
    {
        // Grok's MCP catalog uses server__tool and calls it through search_tool/use_tool.
        // https://github.com/xai-org/grok-build/blob/main/crates/codegen/xai-grok-pager/docs/user-guide/07-mcp-servers.md
        "grok" => $"Its exact MCP catalog name is {name}. Discover it with search_tool if needed, then call use_tool "
            + $"with tool_name=\"{name}\" and inline tool_input={{\"title\":\"your summary\"}}. Do not create argument files. ",
        "glm" => $"Its exact function name is {function}; call it directly with the title argument. ",
        _ => $"Its full MCP name is {name}. If it is deferred, discover it with tool search and then call it. ",
    };

    /// <summary>The two built-in title tools (chat name and bridge task title) only relabel the caller's own chat or
    /// pane, so they skip permission cards in every mode.</summary>
    internal static bool IsChatTitlePermission(string provider, string toolName, JsonNode? input)
    {
        if (toolName == ChatTitleToolNameFor(provider) || toolName == TaskTitleToolNameFor(provider)) return true;
        // Trust only an inline call to our exact Grok registration, never a similarly named third-party
        // tool, an arbitrary use_tool call, or an invocation file whose actual target isn't in this request.
        return provider == "grok" && toolName == "use_tool" && input is JsonObject envelope
            && envelope["tool_name"] is JsonValue target && target.TryGetValue<string>(out var name)
            && (name == ChatTitleToolNameFor(provider) || name == TaskTitleToolNameFor(provider))
            && !envelope.ContainsKey("file") && !envelope.ContainsKey("tool_input_file")
            && envelope["tool_input"] is JsonObject arguments && arguments.Count == 1
            && arguments["title"] is JsonValue title && title.TryGetValue<string>(out _);
    }

    internal static bool IsChatTitleMcpName(string name) => name == ChatTitleToolName || name == ChatTitleToolNameFor("grok")
        || name == TaskTitleToolName || name == TaskTitleToolNameFor("grok");

    /// <summary>Our exact task-title tool as it appears on a transcript card: Claude/Codex/Kimi "mcp__server__tool",
    /// Grok "server__tool", or GLM's bare function, which GLM only ever routes to this bridge. Never a lookalike.</summary>
    internal static bool IsTaskTitleToolName(string name) =>
        name == TaskTitleToolName || name == TaskTitleToolNameFor("grok") || name == TaskTitleTool;
    private readonly CancellationTokenSource _stop = new();
    private readonly Dispatcher _dispatcher;
    private readonly Func<string, JsonObject, JsonObject> _invoke;
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
        definition.UseClaude = definition.UseCodex = definition.UseKimi = definition.UseGrok = true;
        definition.Environment[BridgeMcpClient.PipeEnvironment] = PipeName;
        // The helper is a second launch of the whole single-file VibeCode.exe, which unpacks its compressed bundle
        // first: ~1.6 s on an idle machine and 3-5 s when a Bridge starts several agents at once. The 5 s startup
        // budget inherited from the status reporter therefore dropped the bridge for some of those agents
        // (CONNECT_TIMEOUT), leaving them with no bridge tools at all. Its 10 s tool budget also cut off requests that
        // the pipe itself allows BridgeMcpClient.RequestTimeout (30 s) for.
        definition.StartupTimeoutSeconds = StartupTimeoutSeconds;
        definition.ToolTimeoutSeconds = (int)BridgeMcpClient.RequestTimeout.TotalSeconds + 15;
        return definition;
    }

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
                        if (line.Length + length > BridgeMcpClient.MaxRequestCharacters) throw new StatusValidationException("Bridge request is too large.");
                        line.Append(buffer, 0, length);
                        if (newline >= 0) break;
                    }
                    var request = JsonNode.Parse(line.ToString()) as JsonObject;
                    var name = McpText.Text(request?["tool"]);
                    var tool = BridgeMcpTools.Create(_invoke).FirstOrDefault(t => t.Name == name)
                        ?? throw new StatusValidationException("Unknown bridge tool.");
                    if (request?["arguments"] is not JsonObject args) throw new StatusValidationException("arguments must be an object.");
                    var result = await _dispatcher.InvokeAsync(() => tool.Invoke(args), DispatcherPriority.Normal,
                        requestTimeout.Token).Task.ConfigureAwait(false);
                    response = new JsonObject { ["result"] = result };
                }
                catch (StatusValidationException ex) { response = new JsonObject { ["error"] = ex.Message }; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { response = new JsonObject { ["error"] = "Bridge mailbox storage is unavailable. Retry after storage access is restored." }; }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
                { response = new JsonObject { ["error"] = "Invalid bridge request." }; }
                await writer.WriteLineAsync(response.ToJsonString().AsMemory(), requestTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
    }

    public void Dispose() => _stop.Cancel();
}
