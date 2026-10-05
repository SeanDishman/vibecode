using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VibeCode.AgentStatus.Mcp.Contracts;
using VibeCode.AgentStatus.Mcp.Tools;

namespace VibeCode.AgentStatus.Mcp;

// Dependency-free, newline-delimited stdio transport for the negotiated 2025 MCP protocol family.
// Tool definitions/validation/publishing are separate so an SDK or HTTP host can reuse them later.
// No listener, commands, filesystem access or credentials are exposed to model calls.
public sealed class AgentStatusMcpHost
{
    public const string ServerName = "vibecode-agent-status";
    public const int MaxMessageCharacters = 16_384;

    public const string Instructions = "Use the tools enabled by the connected VibeCode host.";
    private readonly Dictionary<string, IStatusTool> _tools;
    private readonly string _serverName;
    private readonly string _instructions;
    private readonly int _maxMessageCharacters;
    private bool _initialized, _ready;

    public AgentStatusMcpHost(IEnumerable<IStatusTool>? tools = null,
        string serverName = ServerName, string instructions = Instructions, int maxMessageCharacters = MaxMessageCharacters)
    {
        _serverName = serverName;
        _instructions = instructions;
        _maxMessageCharacters = maxMessageCharacters;
        _tools = (tools ?? []).ToDictionary(t => t.Name, StringComparer.Ordinal);
    }

    public static async Task<int> RunConsoleAsync()
    {
        try
        {
            using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false, true));
            using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
            await new AgentStatusMcpHost().RunAsync(input, output).ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or DecoderFallbackException or InvalidOperationException)
        {
            // stdout is exclusively JSON-RPC. Never include request content in diagnostics.
            await Console.Error.WriteLineAsync("Agent status MCP transport closed or received an invalid frame.").ConfigureAwait(false);
            return 1;
        }
    }

    public async Task RunAsync(TextReader input, TextWriter output, CancellationToken cancellationToken = default)
    {
        var buffer = new char[2048];
        var line = new StringBuilder();
        while (true)
        {
            var count = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0) return;
            for (var i = 0; i < count; i++)
            {
                if (buffer[i] != '\n')
                {
                    if (line.Length >= _maxMessageCharacters) throw new InvalidOperationException("Frame too large.");
                    line.Append(buffer[i]);
                    continue;
                }
                JsonObject? reply;
                try { reply = Dispatch(JsonNode.Parse(line.ToString(), documentOptions: new JsonDocumentOptions { MaxDepth = 16 })); }
                catch (JsonException) { reply = Error(null, -32700, "Invalid JSON."); }
                line.Clear();
                if (reply is not null)
                {
                    await output.WriteLineAsync(reply.ToJsonString().AsMemory(), cancellationToken).ConfigureAwait(false);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    public JsonObject? Dispatch(JsonNode? message)
    {
        if (message is not JsonObject request || McpText.Text(request["jsonrpc"]) != "2.0"
            || McpText.Text(request["method"]) is not { } method)
            return Error(null, -32600, "Invalid JSON-RPC request.");
        var id = request["id"];
        if (id is null)
        {
            if (method == "notifications/initialized" && _initialized) _ready = true;
            return null; // notifications never receive a reply or invoke a tool
        }
        if (id is not JsonValue scalar || !(scalar.TryGetValue<string>(out _) || scalar.TryGetValue<long>(out _) || scalar.TryGetValue<int>(out _)))
            return Error(null, -32600, "Invalid request id.");
        if (request["params"] is not null and not JsonObject) return Error(id, -32602, "params must be an object.");
        var p = request["params"] as JsonObject ?? new JsonObject();
        if (method == "initialize")
        {
            if (_initialized) return Error(id, -32600, "Connection is already initialized.");
            var version = McpText.Text(p["protocolVersion"]);
            if (version is null || p["capabilities"] is not JsonObject || p["clientInfo"] is not JsonObject client
                || McpText.Text(client["name"]) is null || McpText.Text(client["version"]) is null)
                return Error(id, -32602, "protocolVersion, capabilities and clientInfo are required.");
            _initialized = true;
            return Result(id, new JsonObject
            {
                ["protocolVersion"] = version is "2024-11-05" or "2025-03-26" or "2025-06-18" or "2025-11-25" ? version : "2025-11-25",
                ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                ["serverInfo"] = new JsonObject { ["name"] = _serverName, ["version"] = "1.0.0" },
                ["instructions"] = _instructions,
            });
        }
        if (method == "ping") return Result(id, new JsonObject());
        if (!_ready) return Error(id, -32002, "Initialize this MCP connection first.");
        if (method == "tools/list")
        {
            if (p["cursor"] is not null) return Error(id, -32602, "This tool list has no pagination cursor.");
            return Result(id, new JsonObject { ["tools"] = new JsonArray(_tools.Values.Select(t => (JsonNode?)t.Definition).ToArray()) });
        }
        if (method != "tools/call") return Error(id, -32601, "Method not supported.");
        if (McpText.Text(p["name"]) is not { } name || !_tools.TryGetValue(name, out var tool))
            return Error(id, -32602, "Unknown tool.");
        if (p["arguments"] is not null and not JsonObject) return Error(id, -32602, "arguments must be an object.");
        try
        {
            var payload = tool.Invoke(p["arguments"] as JsonObject ?? new JsonObject());
            return Result(id, new JsonObject
            {
                ["isError"] = false, ["structuredContent"] = payload,
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = payload.ToJsonString() }),
            });
        }
        catch (StatusValidationException ex) { return ToolError(id, ex.Message); }
        catch (Exception) { return ToolError(id, "Tool call failed; no successful receipt was issued."); }
    }
    private static JsonObject Result(JsonNode id, JsonObject result) => new() { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result };
    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };
    private static JsonObject ToolError(JsonNode id, string message) => Result(id, new JsonObject
    {
        ["isError"] = true, ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = message }),
    });
}
