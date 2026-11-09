using System.Text.Json.Nodes;
using VibeCode.AgentStatus.Mcp.Contracts;

namespace VibeCode.AgentStatus.Mcp.Bridge;

/// <summary>A Bridge failure that survives both the private pipe and MCP tool-result envelopes.</summary>
public sealed class BridgeMcpException : StatusValidationException
{
    private readonly JsonObject _details;

    public BridgeMcpException(string code, string message, string nextAction, string requestStatus = "rejected",
        JsonObject? context = null) : base(message)
    {
        _details = context is null ? new JsonObject() : (JsonObject)context.DeepClone();
        _details["code"] = code;
        _details["message"] = message;
        _details["next_action"] = nextAction;
        _details["request_status"] = requestStatus;
    }

    private BridgeMcpException(JsonObject details) : base(AgentStatusReport.Text(details["message"]) ?? "Bridge request failed.")
        => _details = (JsonObject)details.DeepClone();

    public static BridgeMcpException FromDetails(JsonObject details) => new(details);
    public JsonObject Details => (JsonObject)_details.DeepClone();

    public BridgeMcpException WithContext(string? tool, string phase)
    {
        if (tool is not null) _details["tool"] = tool;
        if (!_details.ContainsKey("phase")) _details["phase"] = phase;
        return this;
    }

    // Keep the original text field for older helper processes; newer ones retain the structured diagnosis too.
    public JsonObject PipeResponse() => new() { ["error"] = Message, ["error_details"] = Details };
    public JsonObject ToolResult()
    {
        var payload = new JsonObject { ["error"] = Details };
        return new JsonObject
        {
            ["isError"] = true, ["structuredContent"] = payload,
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = payload.ToJsonString() }),
        };
    }

    public const string CheckReceipt = "Check bridge_list_agents for the current scope and bridge_read_messages for an existing outgoing message before retrying a send. Do not repeat a message whose delivery is uncertain.";
}
