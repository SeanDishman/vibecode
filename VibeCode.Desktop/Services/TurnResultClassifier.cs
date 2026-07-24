using System.Text.Json.Nodes;

namespace VibeCode.Services;

public enum TurnResultDisposition
{
    Completed,
    ExpectedStop,
    Failed,
}

/// <summary>
/// Classifies a provider's terminal result before the UI changes chat state. Claude Code currently reports an
/// intentionally interrupted turn as <c>error_during_execution</c> when the stream stops between a tool request and
/// its next assistant message. Its EDE diagnostic identifies that narrow transport state as a user stop; it is not a
/// provider failure and must not make either a normal chat or a Bridge pane look crashed.
/// </summary>
public static class TurnResultClassifier
{
    public static TurnResultDisposition Classify(string? provider, JsonNode? result, bool interruptRequested = false)
    {
        if (result?["is_error"]?.GetValue<bool>() != true)
            return TurnResultDisposition.Completed;

        return IsExpectedClaudeStop(provider, result, interruptRequested)
            ? TurnResultDisposition.ExpectedStop
            : TurnResultDisposition.Failed;
    }

    private static bool IsExpectedClaudeStop(string? provider, JsonNode result, bool interruptRequested)
    {
        if (!string.Equals(provider, "claude", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(result["subtype"]?.GetValue<string>(), "error_during_execution",
                StringComparison.OrdinalIgnoreCase))
            return false;

        var terminalReason = StringValue(result["terminal_reason"]);
        if (terminalReason is { Length: > 0 }
            && !IsBenignAbortReason(terminalReason))
            return false;

        var details = ResultDetails(result);
        if (details.Count == 0) return interruptRequested;

        // Claude builds the error list as [diagnostic, ...capturedErrors]. Do not merely search the combined text:
        // a real MCP/provider error can follow the same diagnostic and must remain visible. Every reported detail must
        // independently be either the exact four-token stop signature or a known user-interrupt acknowledgement.
        return details.All(detail => IsUserToolStopDiagnostic(detail)
                                     || interruptRequested && (IsInterruptedUserDiagnostic(detail)
                                                                || IsUserInterruptText(detail)));
    }

    private static List<string> ResultDetails(JsonNode result)
    {
        var details = new List<string>();
        AddDetail(result["result"], details);
        if (result["errors"] is JsonArray errors)
            foreach (var error in errors) AddDetail(error, details);
        return details;
    }

    private static void AddDetail(JsonNode? node, ICollection<string> details)
    {
        var text = node is JsonValue value && value.TryGetValue<string>(out var stringValue)
            ? stringValue
            : node?.ToString();
        if (!string.IsNullOrWhiteSpace(text)) details.Add(text.Trim());
    }

    private static string? StringValue(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool IsBenignAbortReason(string reason) =>
        reason.Equals("aborted_streaming", StringComparison.OrdinalIgnoreCase)
        || reason.Equals("aborted_tools", StringComparison.OrdinalIgnoreCase);

    private static bool IsUserToolStopDiagnostic(string detail)
    {
        if (!IsInterruptedUserDiagnostic(detail)) return false;
        return detail.Split((char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(token => token.Equals("stop_reason=tool_use", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsInterruptedUserDiagnostic(string detail)
    {
        var tokens = detail.Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length != 4) return false;

        var remaining = new HashSet<string>(tokens, StringComparer.OrdinalIgnoreCase);
        if (remaining.Count != 4
            || !remaining.Remove("[ede_diagnostic]")
            || !remaining.Remove("result_type=user")
            || !remaining.Remove("last_content_type=n/a")
            || remaining.Count != 1)
            return false;

        var stopReason = remaining.Single();
        return stopReason.StartsWith("stop_reason=", StringComparison.OrdinalIgnoreCase)
               && stopReason.Length > "stop_reason=".Length;
    }

    private static bool IsUserInterruptText(string detail)
    {
        var text = detail.Trim().Trim('[', ']').Trim();
        return text.Equals("Request interrupted by user", StringComparison.OrdinalIgnoreCase)
               || text.Equals("Interrupted by user", StringComparison.OrdinalIgnoreCase)
               || text.Equals("Request canceled by user", StringComparison.OrdinalIgnoreCase)
               || text.Equals("Request cancelled by user", StringComparison.OrdinalIgnoreCase);
    }
}
