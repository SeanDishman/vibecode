using System.Text.Json.Nodes;
using VibeCode.Services;

namespace VibeCode.Protocol;

/// <summary>Google's official CLI, authenticated with the user's Google subscription.</summary>
public sealed class GeminiSession : ICodingSession, ICompactableSession
{
    private readonly KimiSession _acp;
    public GeminiSession(KimiSessionOptions options) => _acp = new KimiSession(new()
    {
        UseGeminiProtocol = true,
        Cwd = options.Cwd,
        Resume = options.Resume,
        Model = options.Model,
        PermissionMode = options.PermissionMode,
        AppendSystemPrompt = options.AppendSystemPrompt,
        McpServers = options.McpServers,
    });
    public event Action<JsonNode>? MessageReceived { add => _acp.MessageReceived += value; remove => _acp.MessageReceived -= value; }
    public event Action<PermissionRequest>? PermissionRequested { add => _acp.PermissionRequested += value; remove => _acp.PermissionRequested -= value; }
    public event Action<string>? PermissionCancelled { add => _acp.PermissionCancelled += value; remove => _acp.PermissionCancelled -= value; }
    public event Action<int, string>? Exited { add => _acp.Exited += value; remove => _acp.Exited -= value; }
    public event Action? Initialized { add => _acp.Initialized += value; remove => _acp.Initialized -= value; }
    public JsonArray Commands => _acp.Commands;
    public JsonArray Models => _acp.Models;
    public string? SessionId => _acp.SessionId;
    public bool HasExited => _acp.HasExited;
    internal JsonObject? UsageSnapshot() => _acp.GeminiUsageSnapshot();
    public void Start() => _acp.Start();
    public void SendUser(JsonNode content) => _acp.SendUser(content);
    public Task InterruptAsync() => _acp.InterruptAsync();
    public Task SetPermissionModeAsync(string mode) => _acp.SetPermissionModeAsync(mode);
    public Task SetModelAsync(string? model, string? effort = null) => _acp.SetModelAsync(model);
    public void RespondPermission(string id, JsonObject result, string? toolUseId) => _acp.RespondPermission(id, result, toolUseId);
    public Task CompactAsync(string? instructions = null)
    {
        if (!string.IsNullOrWhiteSpace(instructions)) throw new NotSupportedException("Gemini compaction does not accept focus instructions.");
        _acp.SendUser(JsonValue.Create("/compress")!);
        return Task.CompletedTask;
    }
    public void Dispose() => _acp.Dispose();
}
