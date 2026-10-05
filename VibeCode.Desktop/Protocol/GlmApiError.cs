using System.Text.Json.Nodes;

namespace VibeCode.Protocol;

/// <summary>Z.ai reports subscription failures and transient rate limits with the same HTTP 429 status.</summary>
public sealed record GlmApiError(string? Code, string? Message)
{
    // https://docs.z.ai/api-reference/api-code
    public bool IsAccountLimit => Code is "1113" or "1308" or "1309" or "1310" or "1311"
        or "1313" or "1314" or "1315" or "1316" or "1317" or "1318" or "1319" or "1320";

    public static GlmApiError Parse(string body)
    {
        try
        {
            var error = (JsonNode.Parse(body) as JsonObject)?["error"];
            return error is JsonObject obj
                ? new(obj["code"]?.ToString(), obj["message"]?.ToString())
                : new(null, error?.ToString());
        }
        catch (System.Text.Json.JsonException) { return new(null, null); }
    }

    public string Describe(string backend)
    {
        var name = GlmPreset.BackendName(backend);
        var detail = Message ?? (Code is null ? "The request failed." : "The account is currently unavailable.");
        return $"{name}{(Code is null ? "" : $" ({Code})")}: {detail}";
    }
}
