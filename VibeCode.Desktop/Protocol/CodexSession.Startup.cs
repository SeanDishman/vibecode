using System.Text.Json.Nodes;
using VibeCode.Services;

namespace VibeCode.Protocol;

public sealed partial class CodexSession
{
    // A living process is not proof that its startup RPCs will ever answer. Bound each step so the host
    // retains an actionable failed queue instead of showing Pending for the lifetime of that process.
    private TimeSpan _startupRequestTimeout = TimeSpan.FromSeconds(30);
    private bool _initializing;
    private string? _startupFailure;

    private async Task<JsonNode?> StartupRequestAsync(string method, JsonObject parameters)
    {
        if (_disposed) throw new OperationCanceledException();
        try
        {
            var response = await RequestAsync(method, parameters).WaitAsync(_startupRequestTimeout);
            if (_disposed) throw new OperationCanceledException();
            return response;
        }
        catch (TimeoutException ex)
        {
            var action = method switch
            {
                "initialize" => "connecting to its runtime",
                "account/read" => "checking sign-in",
                "model/list" => "loading available models",
                "thread/read" => "loading conversation history",
                "thread/name/set" => "saving the conversation name",
                "thread/inject_items" => "preparing session instructions",
                _ => "opening the conversation",
            };
            _ = Task.Run(() => CrashLog.Note("CodexStartup", $"method={method} timeout_ms={_startupRequestTimeout.TotalMilliseconds:F0}"));
            throw new TimeoutException($"Codex timed out while {action}. Reconnect this chat to retry.", ex);
        }
    }
}
