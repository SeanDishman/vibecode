using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using VibeCode.AgentStatus.Mcp.Contracts;
using VibeCode.AgentStatus.Mcp.Tools;
using VibeCode.Protocol;

namespace VibeCode.Services;

// The MCP client's stdio connection owns the helper lifetime, independently of display windows.
internal static class AgentStatusMcpRegistration
{
    public const string ManagedId = "vibecode-agent-status-v1";
    public const string ManagedName = "agent_status";
    public static string RuntimeName { get; } = McpCatalog.RuntimeServerName(new() { Id = ManagedId, Name = ManagedName });

    public static McpServerDefinition Create(string? desktopDirectory = null)
    {
        var directory = desktopDirectory ?? AppContext.BaseDirectory;
        var executable = Path.Combine(directory, "VibeCode.exe");
        if (desktopDirectory is null && System.Reflection.Assembly.GetEntryAssembly() == typeof(AgentStatusMcpRegistration).Assembly
            && Environment.ProcessPath is { } currentExecutable
            && !string.Equals(Path.GetFileNameWithoutExtension(currentExecutable), "dotnet", StringComparison.OrdinalIgnoreCase))
            executable = currentExecutable; // also works when a single-file desktop binary has been renamed
        var hasAppHost = File.Exists(executable);
        var assembly = Path.Combine(directory, "VibeCode.dll");
        if (!hasAppHost && !File.Exists(assembly)) throw new FileNotFoundException("The bundled agent-status MCP host is missing.");
        var definition = new McpServerDefinition
        {
            Id = ManagedId, Name = ManagedName, Command = hasAppHost ? executable : "dotnet",
            Arguments = hasAppHost ? new() { "--agent-status-mcp" } : new() { assembly, "--agent-status-mcp" },
            UseClaude = false, UseCodex = true, UseKimi = false, UseGrok = false,
            StartupTimeoutSeconds = 5, ToolTimeoutSeconds = 10,
        };
        return definition;
    }

    public static List<McpServerDefinition> ForLaunch(
        IEnumerable<McpServerDefinition> servers, bool codex, bool enabled, string? provider = null, string? model = null)
    {
        var snapshot = McpCatalog.Snapshot(servers);
        snapshot.RemoveAll(server => server.Id == ManagedId);
        if (codex && enabled) snapshot.Add(Create());
        return snapshot;
    }

    public static AgentStatusReport? ReadReceipt(JsonNode? item)
    {
        if (item is not JsonObject || AgentStatusReport.Text(item["type"]) != "mcpToolCall"
            || AgentStatusReport.Text(item["server"]) != RuntimeName
            || AgentStatusReport.Text(item["tool"]) != ReportStatusTool.ToolName
            || AgentStatusReport.Text(item["status"]) != "completed" || item["error"] is not null
            || item["result"] is not JsonObject result || result["isError"]?.ToString() == "true") return null;
        if (result["structuredContent"] is { } structured) return AgentStatusReport.FromReceipt(structured);
        // Older clients flatten structuredContent into text. Only our named tool is eligible, and the
        // receipt is still validated; arbitrary tool output or the model's arguments never set status.
        if (result["content"] is not JsonArray content) return null;
        foreach (var block in content.OfType<JsonObject>())
        {
            if (AgentStatusReport.Text(block["type"]) != "text" || AgentStatusReport.Text(block["text"]) is not { Length: <= 4096 } text) continue;
            try
            {
                if (AgentStatusReport.FromReceipt(JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { MaxDepth = 8 })) is { } report) return report;
            }
            catch (JsonException) { }
        }
        return null;
    }
}
