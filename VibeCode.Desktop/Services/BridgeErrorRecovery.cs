using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace VibeCode.Services;

/// <summary>Retry terminal provider failures that can clear without changing the task or account.</summary>
public static class BridgeErrorRecovery
{
    private const string HttpStatusPrefix = @"\b(?:status(?:[_\s-]*code)?|http(?:[_\s-]*status(?:[_\s-]*code)?)?|code)[\s\W_]{0,8}";
    public static readonly TimeSpan Delay = TimeSpan.FromMinutes(1);
    public const string RetryingText = "Retrying…";
    public const string ContinuePrompt = "Continue the interrupted task from where you stopped after the error. " +
        "Keep completed work, check any interrupted operation before retrying it, and do not repeat actions that already succeeded. " +
        "Please do not error again.";
    public const string WorkerContinuePrompt = "Your previous turn errored, and your orchestrator was not able to revive you. " +
        "This is a generic automatic retry prompt. " + ContinuePrompt;
    public const string OrchestratorContinuePrompt = "You errored while acting as an orchestrator, so another AI was not able to revive you. " +
        "This is a generic automatic retry prompt. " + ContinuePrompt;

    public static bool CanRetry(JsonNode result)
    {
        if (result["is_error"]?.ToString() != "true" || UsageLimitRecovery.IsLimitError(result)) return false;
        var text = string.Join(" ", new[] { "subtype", "result", "errors", "provider_error" }
            .Select(key => result[key]?.ToString())).ToLowerInvariant();
        // Permanent request/auth failures and intentional stops are never made into a retry loop, even if
        // their diagnostics also mention a transport error. Tool output is not passed into this classifier.
        return !(new[] { "unauthorized", "unauthorised", "authentication", "invalid api key", "not logged in", "not signed in",
            "permission denied", "access denied", "invalid_request", "invalid request", "context_length", "context window",
            "context limit", "maximum context", "model not found", "does not exist", "not supported", "cancelled", "canceled",
            "user interrupt", "content policy", "safety policy", "safety refusal", "request refused", "refusal" }.Any(text.Contains)
            || Regex.IsMatch(text, HttpStatusPrefix + @"(?:400|401|403|404)\b"));
    }

    public static bool IsTransient(JsonNode result)
    {
        if (!CanRetry(result)) return false;
        var text = string.Join(" ", new[] { "subtype", "result", "errors", "provider_error" }
            .Select(key => result[key]?.ToString())).ToLowerInvariant();

        var code = (result["provider_error"] as JsonObject)?["codexErrorInfo"]?.ToString().ToLowerInvariant() ?? "";
        if (new[] { "serveroverloaded", "internalservererror", "httpconnectionfailed", "responsestreamconnectionfailed",
            "responsestreamdisconnected", "responsetoomanyfailedattempts" }.Any(code.Contains)) return true;
        if (new[] { "model is at capacity", "model at capacity", "overloaded", "over capacity", "service unavailable",
            "temporarily unavailable", "internal server error", "bad gateway", "gateway timeout", "connection reset",
            "connection closed", "connection timed out", "connection timeout", "request timed out", "request timeout",
            "stream disconnected", "stream was disconnected", "stream interrupted", "network error", "failed to connect",
            "could not reach the", "error occurred while sending the request", "failed to send request",
            "error occurred while processing your request" }.Any(text.Contains)) return true;
        return Regex.IsMatch(text, HttpStatusPrefix + @"(?:408|500|502|503|504|529)\b");
    }
}
