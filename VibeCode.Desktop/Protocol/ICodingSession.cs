using System.Text.Json.Nodes;

namespace VibeCode.Protocol;

/// <summary>A native context-compaction operation, separate from an ordinary model prompt.</summary>
public interface ICompactableSession
{
    Task CompactAsync(string? instructions = null);
}

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

public interface ISteerableSession
{
    /// <summary>True while a root turn is running that would accept guidance now.</summary>
    bool CanSteer { get; }

    /// <summary>Hand <paramref name="content"/> (a string or text/image blocks) to the running turn. Throws when the
    /// provider refuses it, so the caller can keep the message instead of losing it.</summary>
    Task SteerAsync(JsonNode content);
}
