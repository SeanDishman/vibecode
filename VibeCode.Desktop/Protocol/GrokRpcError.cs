using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace VibeCode.Protocol;

/// <summary>Grok puts the actionable provider error in ACP error.data, behind "Internal error".</summary>
internal static partial class GrokRpcError
{
    internal static string Describe(string? message, JsonNode? data = null, int code = 0)
    {
        // Read only diagnostic fields. Serializing the entire data object can expose auth metadata.
        var detail = Detail(data) ?? message ?? "Grok request failed.";
        var status = data is JsonObject obj ? Status(obj["http_status"]) : 0;
        detail = Sanitize(detail);

        if (status == 426)
            return "Grok rejected the CLI as outdated (HTTP 426). Update VibeCode's bundled Grok runtime " +
                   "or set VIBECODE_GROK_PATH to an updated CLI. " + detail;
        if (status == 401 || code == -32000
            || detail.Contains("not authenticated", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("refresh token", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("session has expired", StringComparison.OrdinalIgnoreCase))
            return "Grok sign-in needs to be renewed. Open VibeCode's Grok account menu and sign in again. " + detail;

        return status > 0 && !detail.Contains(status.ToString(), StringComparison.Ordinal)
            ? $"Grok returned HTTP {status}: {detail}"
            : detail;
    }

    private static string? Detail(JsonNode? data) => data switch
    {
        JsonValue value => Text(value),
        JsonObject obj => Text(obj["message"]) ?? Detail(obj["error"]),
        _ => null,
    };

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;

    private static int Status(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<int>(out var status) ? status
        : int.TryParse(Text(node), out status) ? status : 0;

    private static string Sanitize(string detail)
    {
        // Provider messages can quote a request URL or a header. Never put credentials into a saved chat.
        detail = Url().Replace(detail, "[URL omitted]");
        detail = Bearer().Replace(detail, "Bearer [redacted]");
        detail = Credential().Replace(detail, "$1[redacted]");
        detail = Jwt().Replace(detail, "[redacted]");
        detail = ApiKey().Replace(detail, "[redacted]").Trim();
        return detail.Length <= 2000 ? detail : detail[..2000] + "…";
    }

    [GeneratedRegex("https?://[^\\s\"<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex Url();

    [GeneratedRegex("\\bBearer\\s+[^\\s\",;}]+", RegexOptions.IgnoreCase)]
    private static partial Regex Bearer();

    [GeneratedRegex("((?:access_token|refresh_token|id_token|api_key|authorization|cookie)\\s*[\"']?\\s*[:=]\\s*[\"']?)[^\\s\"',;}]+", RegexOptions.IgnoreCase)]
    private static partial Regex Credential();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+")]
    private static partial Regex Jwt();

    [GeneratedRegex(@"\bxai-[A-Za-z0-9_-]+", RegexOptions.IgnoreCase)]
    private static partial Regex ApiKey();
}
