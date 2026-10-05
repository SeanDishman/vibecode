using System.Text.Json.Nodes;

namespace VibeCode.AgentStatus.Mcp.Tools;

public interface IStatusTool
{
    string Name { get; }
    JsonObject Definition { get; }
    JsonObject Invoke(JsonObject arguments);
}
