using System.IO.Pipes;
using System.Text;
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
            await writer.WriteLineAsync(new JsonObject { ["tool"] = tool, ["arguments"] = arguments.DeepClone() }
                .ToJsonString().AsMemory(), timeout.Token).ConfigureAwait(false);
            var line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            var response = line is null ? null : JsonNode.Parse(line) as JsonObject;
            if (response?["error"] is JsonValue error)
                throw new StatusValidationException(error.GetValue<string>());
            // The MCP host owns the returned node when it wraps structuredContent. Detach it from the pipe envelope.
            if (response?["result"] is not JsonObject result) throw new StatusValidationException("Bridge returned no receipt.");
            response.Remove("result");
            return result;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            throw new StatusValidationException("Bridge connection is unavailable or timed out. Check bridge_list_agents before retrying a send; delivery may already have occurred.");
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
