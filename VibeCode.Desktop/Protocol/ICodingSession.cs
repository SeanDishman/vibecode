using System.Text.Json.Nodes;

namespace VibeCode.Protocol;

/// <summary>
/// Provider-neutral surface used by the chat view model. Claude speaks its stream-json
/// protocol directly; CodexSession, KimiSession, and GrokSession adapt their JSON-RPC protocols to the
/// same message stream so the rest of the UI can stay provider agnostic.
/// </summary>
public interface ICodingSession : IDisposable
{
    event Action<JsonNode>? MessageReceived;
    event Action<PermissionRequest>? PermissionRequested;
    event Action<string>? PermissionCancelled;
    event Action<int, string>? Exited;
    event Action? Initialized;

    JsonArray Commands { get; }
    JsonArray Models { get; }
    string? SessionId { get; }
    bool HasExited { get; }

    void Start();
    void SendUser(JsonNode content);
    Task InterruptAsync();
    Task SetPermissionModeAsync(string mode);
    Task SetModelAsync(string? model, string? effort = null);
    void RespondPermission(string requestId, JsonObject result, string? toolUseId);
}
