using System.Text.Json.Nodes;

namespace VibeCode.Services;

public sealed class McpConfigSuggestion
{
    public required McpServerDefinition Definition { get; init; }
    public string Notes { get; init; } = "";
}

/// <summary>Parses and safety-normalizes an AI-authored MCP draft before the settings UI displays it.</summary>
public static class McpConfigSuggestionParser
{
    public static bool TryParse(string text, out McpConfigSuggestion suggestion)
    {
        suggestion = null!;
        if (!TryExtractObject(text, out var json)) return false;
        try
        {
            var transport = NodeText(json["transport"]).Trim().ToLowerInvariant();
            if (transport is not (McpCatalog.StdioTransport or McpCatalog.HttpTransport or McpCatalog.SseTransport))
                transport = McpCatalog.StdioTransport;
            var definition = new McpServerDefinition
            {
                Name = NodeText(json["name"]).Trim(),
                Transport = transport,
                Command = NodeText(json["command"]).Trim(),
                Arguments = ReadStrings(json["arguments"]),
                Environment = ProtectSecretValues(ReadMap(json["environment"])),
                Url = NodeText(json["url"]).Trim(),
                Headers = ProtectAuthorization(ReadMap(json["headers"])),
                BearerTokenEnvironmentVariable = NullIfWhiteSpace(NodeText(json["bearerTokenEnvironmentVariable"])),
                StartupTimeoutSeconds = ReadInt(json["startupTimeoutSeconds"], McpCatalog.DefaultStartupTimeoutSeconds),
                ToolTimeoutSeconds = ReadInt(json["toolTimeoutSeconds"], McpCatalog.DefaultToolTimeoutSeconds),
                UseClaude = ReadBool(json["useClaude"], true),
                UseCodex = ReadBool(json["useCodex"], true) && transport != McpCatalog.SseTransport,
                UseKimi = ReadBool(json["useKimi"], true),
                UseGrok = ReadBool(json["useGrok"], true),
                Enabled = true,
            };
            suggestion = new McpConfigSuggestion
            {
                Definition = definition,
                Notes = NodeText(json["notes"]).Trim(),
            };
            return true;
        }
        catch { return false; }
    }

    private static bool TryExtractObject(string text, out JsonObject result)
    {
        result = null!;
        var trimmed = text.Trim();
        var first = trimmed.IndexOf('{');
        var last = trimmed.LastIndexOf('}');
        if (first < 0 || last <= first) return false;
        try
        {
            result = JsonNode.Parse(trimmed[first..(last + 1)]) as JsonObject ?? null!;
            return result is not null;
        }
        catch { return false; }
    }

    private static List<string> ReadStrings(JsonNode? node) => node is JsonArray array
        ? array.Select(NodeText).Where(value => value.Length > 0).Take(128).ToList()
        : new List<string>();

    private static Dictionary<string, string> ReadMap(JsonNode? node)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (node is not JsonObject map) return result;
        foreach (var pair in map.Take(128))
        {
            if (string.IsNullOrWhiteSpace(pair.Key)) continue;
            result[pair.Key.Trim()] = NodeText(pair.Value);
        }
        return result;
    }

    private static Dictionary<string, string> ProtectSecretValues(Dictionary<string, string> values)
    {
        foreach (var key in values.Keys.ToArray())
        {
            if (!LooksSecret(key) || values[key].Contains("${", StringComparison.Ordinal)) continue;
            values[key] = "${" + key + "}";
        }
        return values;
    }

    private static Dictionary<string, string> ProtectAuthorization(Dictionary<string, string> values)
    {
        foreach (var key in values.Keys.ToArray())
        {
            if (!key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                || values[key].Contains("${", StringComparison.Ordinal)) continue;
            values[key] = "Bearer ${MCP_AUTH_TOKEN}";
        }
        return values;
    }

    private static bool LooksSecret(string name) =>
        name.Contains("TOKEN", StringComparison.OrdinalIgnoreCase)
        || name.Contains("SECRET", StringComparison.OrdinalIgnoreCase)
        || name.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("_KEY", StringComparison.OrdinalIgnoreCase)
        || name.Equals("API_KEY", StringComparison.OrdinalIgnoreCase);

    private static int ReadInt(JsonNode? node, int fallback)
    {
        if (node is null) return fallback;
        try { return checked((int)node.GetValue<long>()); }
        catch
        {
            try { return checked((int)Math.Round(node.GetValue<double>())); }
            catch { return int.TryParse(NodeText(node), out var value) ? value : fallback; }
        }
    }

    private static bool ReadBool(JsonNode? node, bool fallback = false)
    {
        if (node is null) return fallback;
        try { return node.GetValue<bool>(); }
        catch { return bool.TryParse(NodeText(node), out var value) ? value : fallback; }
    }

    private static string NodeText(JsonNode? node)
    {
        if (node is null) return "";
        try { return node.GetValue<string>(); }
        catch { return node.ToString(); }
    }

    private static string? NullIfWhiteSpace(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}