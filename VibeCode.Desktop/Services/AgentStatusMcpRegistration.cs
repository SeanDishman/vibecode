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
}
