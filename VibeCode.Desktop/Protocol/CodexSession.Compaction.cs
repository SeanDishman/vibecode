using System.Text.Json.Nodes;
using VibeCode.Services;

namespace VibeCode.Protocol;

public sealed partial class CodexSession : ICompactableSession
{
    internal async Task<UsageRecoverySnapshot> ReadRecoveryUsageAsync()
    {
        var response = await RequestAsync("account/rateLimits/read").WaitAsync(TimeSpan.FromSeconds(15));
        return UsageLimitRecovery.CodexSnapshot(response?["result"], ApiModel(_model));
    }

    public async Task CompactAsync(string? instructions = null)
    {
        if (HasExited || SessionId is null) throw new InvalidOperationException("Codex is not connected to a thread.");
        if (!string.IsNullOrWhiteSpace(instructions))
            throw new InvalidOperationException("Codex supports /compact without additional instructions.");
        // Completion arrives through turn/completed, not this immediate acknowledgement.
        await RequestAsync("thread/compact/start", new JsonObject { ["threadId"] = SessionId });
    }
}
