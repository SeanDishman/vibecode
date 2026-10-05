using System.Text.Json.Nodes;

namespace VibeCode.AgentStatus.Mcp.Contracts;

public sealed class StatusValidationException(string message) : Exception(message);

public static class McpText
{
    public static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
