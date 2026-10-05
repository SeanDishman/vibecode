using System.Text.Json.Nodes;

namespace VibeCode.Protocol;

/// <summary>A native context-compaction operation, separate from an ordinary model prompt.</summary>
public interface ICompactableSession
{
    Task CompactAsync(string? instructions = null);
}

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

/// <summary>
/// A session whose running turn can take another user message without being interrupted ("steering"). Codex has a
/// steer request, Claude Code folds a mid-turn user message into the turn, Grok interjects a queued prompt, and GLM
/// is VibeCode's own loop. Kimi's ACP adapter rejects any prompt while a turn runs, so it is not steerable.
/// </summary>
public interface ISteerableSession
{
    /// <summary>True while a root turn is running that would accept guidance now.</summary>
    bool CanSteer { get; }

    /// <summary>Hand <paramref name="content"/> (a string or text/image blocks) to the running turn. Throws when the
    /// provider refuses it, so the caller can keep the message instead of losing it.</summary>
    Task SteerAsync(JsonNode content);
}
