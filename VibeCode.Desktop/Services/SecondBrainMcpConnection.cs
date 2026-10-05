using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VibeCode.AgentStatus.Mcp.Memory;

namespace VibeCode.Services;

/// <summary>All providers use this private chat endpoint, including tools preapproved by the provider.</summary>
internal sealed class SecondBrainMcpConnection : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<AgentMemoryChatContext> _context;
    public string PipeName { get; } = "vibecode-memory-" + Guid.NewGuid().ToString("N");

    public SecondBrainMcpConnection(Func<AgentMemoryChatContext> context)
    {
        _context = context;
        _ = Task.Run(ServeAsync);
    }

    public static McpServerDefinition CreateRegistration()
    {
        var definition = AgentStatusMcpRegistration.Create();
        definition.Id = AgentMemoryService.ManagedMcpId;
        definition.Name = "agentmemory";
        definition.Arguments[^1] = "--second-brain-mcp";
        definition.UseClaude = definition.UseCodex = definition.UseKimi = definition.UseGrok = true;
        definition.StartupTimeoutSeconds = 15;
        definition.ToolTimeoutSeconds = 75;
        return definition;
    }

    public McpServerDefinition Registration()
    {
        var definition = CreateRegistration();
        definition.Environment[SecondBrainMcpHost.PipeEnvironment] = PipeName;
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
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(70));
                using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                JsonObject envelope;
                try
                {
                    var line = await SecondBrainMcpHost.ReadFrameAsync(reader, SecondBrainMcpHost.MaxRequestCharacters,
                        timeout.Token).ConfigureAwait(false);
                    var request = line is null ? null : JsonNode.Parse(line,
                        documentOptions: new JsonDocumentOptions { MaxDepth = 32 }) as JsonObject;
                    if (request?["method"] is not JsonValue value || !value.TryGetValue<string>(out var method)
                        || method is not ("tools/list" or "tools/call") || request["params"] is not JsonObject parameters)
                        throw new InvalidOperationException("Invalid Second Brain request.");
                    var result = await AgentMemoryService.Instance.InvokeMemoryMcpAsync(_context(), method, parameters,
                        timeout.Token).ConfigureAwait(false);
                    // The flag may have changed while the daemon answered. Never return a stale successful receipt.
                    var context = _context();
                    if (!AgentMemoryService.CanAccessMemory(context)) throw new InvalidOperationException(
                        AppSettings.Current.SecondBrainEnabled ? AgentMemoryService.MutedExplanation : AgentMemoryService.DisabledExplanation);
                    envelope = new JsonObject { ["result"] = result };
                }
                catch (OperationCanceledException) when (!_stop.IsCancellationRequested)
                {
                    envelope = new JsonObject { ["error"] = AppSettings.Current.SecondBrainEnabled
                        ? "Second Brain access was revoked or timed out; no memory result was returned."
                        : AgentMemoryService.DisabledExplanation };
                }
                catch (InvalidOperationException ex) { envelope = new JsonObject { ["error"] = ex.Message }; }
                catch (Exception ex) when (ex is IOException or JsonException or System.Net.Http.HttpRequestException)
                { envelope = new JsonObject { ["error"] = "Second Brain is unavailable; no memory result was returned." }; }
                await writer.WriteLineAsync(envelope.ToJsonString().AsMemory(), timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException) { }
        }
    }

    public void Dispose() => _stop.Cancel();
}
