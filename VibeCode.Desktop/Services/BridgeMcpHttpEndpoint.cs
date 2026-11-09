using System.Text.Json.Nodes;
using System.Windows.Threading;
using VibeCode.AgentStatus.Mcp;
using VibeCode.AgentStatus.Mcp.Bridge;
using VibeCode.AgentStatus.Mcp.Contracts;

namespace VibeCode.Services;

/// <summary>
/// One chat's Bridge tools served in-process on <see cref="McpLoopbackHost"/>, in place of its `--bridge-mcp` helper.
/// Each MCP session runs the same AgentStatusMcpHost over the same BridgeMcpTools registry as the helper, so schemas,
/// argument validation and error envelopes cannot drift between the two transports and a tool added to the registry
/// reaches both. Tool calls run on the UI dispatcher within the relay's 30 s budget, as the pipe in
/// <see cref="BridgeMcpConnection"/> runs them; only the route's bearer token says which chat is calling.
/// </summary>
internal sealed class BridgeMcpHttpEndpoint(Dispatcher dispatcher, Func<string, JsonObject, JsonObject> invoke) : IMcpHttpEndpoint
{
    public int MaxRequestCharacters => BridgeMcpClient.MaxRequestCharacters;
    public bool StreamsReplies => false;
    public bool OffersEventStream => false;   // its tool list never changes and it never asks the client anything

    public async Task<JsonObject?> HandleAsync(McpHttpSession session, JsonObject message, McpEventStream? events)
    {
        // initialize is accepted once per connection, as on stdio: one host per MCP session.
        var host = (AgentStatusMcpHost)(session.State ??= new AgentStatusMcpHost(BridgeMcpTools.Create(Execute),
            serverName: "vibecode-bridge", instructions: BridgeMcpTools.Instructions,
            maxMessageCharacters: BridgeMcpClient.MaxRequestCharacters));
        if (McpJsonRpc.Method(message) is not { } method) return null;   // a response: the Bridge never asks the client anything
        if (method != "tools/call" || !McpJsonRpc.IsRequest(message)) return host.Dispatch(message);
        using var timeout = new CancellationTokenSource(BridgeMcpClient.RequestTimeout);
        try
        {
            return await dispatcher.InvokeAsync(() => host.Dispatch(message), DispatcherPriority.Normal, timeout.Token)
                .Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The relay's own receipt when VibeCode does not answer in time. The call may still run, so the agent checks first.
            var tool = AgentStatusReport.Text((message["params"] as JsonObject)?["name"]);
            return McpJsonRpc.Result(message["id"], new BridgeMcpException("connection_timeout",
                    "The Bridge call timed out after 30 seconds while waiting for a receipt. The request may already have taken effect.",
                    BridgeMcpException.CheckReceipt, "unknown").WithContext(tool, "execution").ToolResult());
        }
    }

    public void SessionClosed(McpHttpSession session) { }

    /// <summary>What the pipe relay makes of a handler's answer: a private copy of the receipt (it went through JSON
    /// there), and any unexpected failure as the relay's diagnosed internal_error instead of a bare tool error.</summary>
    private JsonObject Execute(string tool, JsonObject arguments)
    {
        try { return (JsonObject)invoke(tool, arguments).DeepClone(); }
        catch (Exception ex) when (ex is not StatusValidationException) { throw BridgeMcpDiagnostics.Failure(ex, tool, "execution"); }
    }
}
