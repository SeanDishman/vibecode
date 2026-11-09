using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VibeCode.AgentStatus.Mcp.Contracts;

namespace VibeCode.AgentStatus.Mcp.Bridge;

/// <summary>Private local transport. A random, per-session pipe binds the caller; models cannot choose a sender.</summary>
public static class BridgeMcpClient
{
    public const string PipeEnvironment = "VIBECODE_BRIDGE_PIPE";
    public const int MaxRequestCharacters = 65_536; // 6,000 Unicode characters may each use a six-byte JSON escape.
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    public static JsonObject Invoke(string pipeName, string tool, JsonObject arguments) =>
        InvokeAsync(pipeName, tool, arguments).GetAwaiter().GetResult();

    public static async Task<JsonObject> InvokeAsync(string pipeName, string tool, JsonObject arguments,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        var writeStarted = false;
        var phase = "connect";
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            // ConnectAsync on Windows can occupy a thread-pool thread while the single local
            // endpoint is busy. A burst then starves the server's own continuations. Only
            // connection establishment is retried; requests are written exactly once.
            while (!pipe.IsConnected)
            {
                timeout.Token.ThrowIfCancellationRequested();
                try { pipe.Connect(0); }
                catch (TimeoutException) { await Task.Delay(10, timeout.Token).ConfigureAwait(false); }
            }
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            phase = "send";
            writeStarted = true;
            await writer.WriteLineAsync(new JsonObject { ["tool"] = tool, ["arguments"] = arguments.DeepClone() }
                .ToJsonString().AsMemory(), timeout.Token).ConfigureAwait(false);
            phase = "receive";
            var line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            var response = line is null ? null : JsonNode.Parse(line) as JsonObject;
            if (response?["error_details"] is JsonObject details)
            {
                if (new[] { "code", "message", "next_action", "request_status" }
                    .Any(key => string.IsNullOrWhiteSpace(AgentStatusReport.Text(details[key]))))
                    throw new BridgeMcpException("invalid_response", "VibeCode returned an incomplete Bridge error receipt.",
                        BridgeMcpException.CheckReceipt, "unknown").WithContext(tool, phase);
                throw BridgeMcpException.FromDetails(details).WithContext(tool, "execution");
            }
            if (AgentStatusReport.Text(response?["error"]) is { } error)
                throw new BridgeMcpException("request_rejected", error, BridgeMcpException.CheckReceipt, "unknown")
                    .WithContext(tool, "execution");
            // The MCP host owns the returned node when it wraps structuredContent. Detach it from the pipe envelope.
            if (response?["result"] is not JsonObject result)
                throw new BridgeMcpException(line is null ? "connection_closed" : "invalid_response",
                    line is null ? "The VibeCode Bridge connection closed before returning a receipt. The chat or app may have closed."
                        : "VibeCode returned a response without a valid Bridge receipt.", BridgeMcpException.CheckReceipt, "unknown")
                    .WithContext(tool, phase);
            response.Remove("result");
            return result;
        }
        catch (JsonException)
        {
            throw new BridgeMcpException("invalid_response", "VibeCode returned malformed JSON instead of a Bridge receipt.",
                BridgeMcpException.CheckReceipt, "unknown").WithContext(tool, phase);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            var code = ex is OperationCanceledException
                ? cancellationToken.IsCancellationRequested ? "request_cancelled" : "connection_timeout"
                : ex is UnauthorizedAccessException ? "connection_access_denied" : "connection_unavailable";
            var reason = ex is OperationCanceledException
                ? cancellationToken.IsCancellationRequested ? "The Bridge call was cancelled" : "The Bridge call timed out after 30 seconds"
                : ex is UnauthorizedAccessException ? "Access to the local Bridge connection was denied" : "The local Bridge connection was lost or is unavailable";
            throw new BridgeMcpException(code, reason + (writeStarted
                    ? " while waiting for a receipt. The request may already have taken effect."
                    : " before the request was sent. The chat may have closed or the MCP connection may be stale."),
                writeStarted ? BridgeMcpException.CheckReceipt : "Refresh bridge_list_agents. If the connection stays unavailable, reconnect the chat's MCP server in VibeCode.",
                writeStarted ? "unknown" : "not_started").WithContext(tool, phase);
        }
    }

    public static async Task<int> RunConsoleAsync()
    {
        var pipeName = Environment.GetEnvironmentVariable(PipeEnvironment);
        if (string.IsNullOrWhiteSpace(pipeName)) return 1;
        using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false, true));
        using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        try
        {
            var host = new AgentStatusMcpHost(BridgeMcpTools.Create((tool, args) => Invoke(pipeName, tool, args)),
                serverName: "vibecode-bridge", instructions: BridgeMcpTools.Instructions,
                maxMessageCharacters: MaxRequestCharacters);
            await host.RunAsync(input, output).ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or DecoderFallbackException or InvalidOperationException)
        {
            await Console.Error.WriteLineAsync("Bridge MCP transport closed or received an invalid frame.").ConfigureAwait(false);
            return 1;
        }
    }
}
