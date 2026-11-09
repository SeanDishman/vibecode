using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace VibeCode.Services;

public enum ProviderFailureKind { Unknown, Authentication, Permission, RateLimit, Timeout, Network, Unavailable }

public sealed record ProviderFailure(ProviderFailureKind Kind, string Summary, string Recovery)
{
    public bool RequiresSignIn => Kind == ProviderFailureKind.Authentication;
    public string Describe(string detail) => $"{Summary} {Recovery}" +
        (string.IsNullOrWhiteSpace(detail) ? "" : "\n\nDetails: " + (detail.Length <= 500 ? detail : detail[..500] + "…"));

    /// <summary>

    /// Those are model refusals, not account-permission failures, and must not use the Access denied title.
    /// </summary>
    public static bool IsContentFilterWrapUp(string? detail, JsonNode? error = null, Exception? exception = null)
    {
        var text = string.Join(" ", new[] { detail, exception?.Message, error?.ToJsonString() }
            .Where(s => !string.IsNullOrWhiteSpace(s))).ToLowerInvariant();
        return text.Contains("can't help with that request")
            || text.Contains("cannot help with that request")
            || text.Contains("content policy")
            || text.Contains("cybersecurity risk");
    }

    // Inspect error metadata only. A 401 in a tool ID, token count, or ordinary response is not a login verdict.
    public static ProviderFailure Classify(string? detail, JsonNode? error = null, Exception? exception = null)
    {
        int? status = exception is HttpRequestException http && http.StatusCode is { } code ? (int)code : null;
        var codes = new List<string>();
        void Read(JsonNode? node, int depth = 0)
        {
            if (node is not JsonObject obj || depth > 4) return;
            foreach (var key in new[] { "status_code", "statusCode", "http_status", "httpStatus", "httpStatusCode", "status", "code" })
                if (int.TryParse(obj[key]?.ToString(), out var value) && value is >= 400 and <= 599) status ??= value;
            foreach (var key in new[] { "code", "type", "subtype" })
                if (obj[key] is JsonValue value) codes.Add(value.ToString().ToLowerInvariant());
            if (obj["usage_limit"]?.ToString() == "true") codes.Add("usage limit");
            Read(obj["error"], depth + 1);
            Read(obj["data"], depth + 1);
            Read(obj["provider_error"], depth + 1);
        }
        Read(error);
        var text = (detail ?? exception?.Message ?? "").ToLowerInvariant();
        if (status is null)
        {
            var match = Regex.Match(text, @"\b(?:http(?:/\d(?:\.\d)?)?\s+|status(?:\s+code)?[\s:=]+|response\s+status\s+code[^\d]*)([45]\d{2})\b");
            if (match.Success) status = int.Parse(match.Groups[1].Value);
        }
        bool Has(params string[] phrases) => phrases.Any(p => text.Contains(p, StringComparison.Ordinal) || codes.Contains(p));
        var contentFilter = IsContentFilterWrapUp(detail, error, exception);
        var kind = status switch
        {
            401 => ProviderFailureKind.Authentication,
            _ when contentFilter => ProviderFailureKind.Unknown,
            403 => ProviderFailureKind.Permission,
            408 or 504 => ProviderFailureKind.Timeout,
            429 => ProviderFailureKind.RateLimit,
            >= 500 => ProviderFailureKind.Unavailable,
            _ when Has("invalid_api_key", "invalid api key", "authentication_error", "unauthorized", "not authenticated", "not logged in", "not signed in", "token expired", "expired token", "session has expired", "invalid_grant", "credentials expired", "authentication required") => ProviderFailureKind.Authentication,
            _ when Has("permission_denied", "permission denied", "forbidden", "access denied", "insufficient permissions") => ProviderFailureKind.Permission,
            _ when UsageLimitRecovery.IsLimitError(new JsonObject
                { ["is_error"] = true, ["result"] = text, ["subtype"] = string.Join(" ", codes) }) => ProviderFailureKind.RateLimit,
            _ when exception is TimeoutException or TaskCanceledException || Has("timed out", "timeout", "deadline exceeded") => ProviderFailureKind.Timeout,
            _ when exception is HttpRequestException or SocketException || Has("connection refused", "connection reset", "network is unreachable", "network error", "name resolution", "no such host", "dns", "failed to connect", "unable to connect", "connection closed", "disconnected") => ProviderFailureKind.Network,
            _ when Has("service unavailable", "overloaded", "internal server error", "bad gateway") => ProviderFailureKind.Unavailable,
            _ => ProviderFailureKind.Unknown,
        };
        return kind switch
        {
            ProviderFailureKind.Authentication => new(kind, "Sign-in required.", "Reconnect this chat's account, then retry."),
            ProviderFailureKind.Permission => new(kind, "Access denied.", "Check this account's permissions for the selected model or service."),
            ProviderFailureKind.RateLimit => new(kind, "Rate or usage limit reached.", "Wait for the provider's allowance to return before retrying."),
            ProviderFailureKind.Timeout => new(kind, "The provider timed out.", "Check the connection and retry when it responds."),
            ProviderFailureKind.Network => new(kind, "Could not connect to the provider.", "Check your network or proxy, then reconnect this chat."),
            ProviderFailureKind.Unavailable => new(kind, "The provider is temporarily unavailable.", "Retry after the service recovers."),
            _ => new(kind, "The provider request failed.", "Check the details before retrying."),
        };
    }
}
