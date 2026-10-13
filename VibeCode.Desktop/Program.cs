using System.Runtime.CompilerServices;

namespace VibeCode;

/// <summary>
/// Process entry point. The headless stdio roles run here, before any WPF object exists.
///
/// Every agent session starts one of these helpers, and the published app is a compressed single-file bundle, so
/// each process unpacks every assembly it loads into its own private memory. Started as the WPF app, a helper built an
/// Application and merged the whole theme from App.xaml only to leave from OnStartup: about 66 MB each, 27 of them
/// on 2026-10-12, on a machine whose desktop compositor then died of memory exhaustion. None of them use WPF.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--second-brain-mcp", StringComparer.Ordinal))
            return Task.Run(() => AgentStatus.Mcp.Memory.SecondBrainMcpHost.RunConsoleAsync()).GetAwaiter().GetResult();
        if (args.Contains("--bridge-mcp", StringComparer.Ordinal))
            return Task.Run(() => AgentStatus.Mcp.Bridge.BridgeMcpClient.RunConsoleAsync()).GetAwaiter().GetResult();
        if (args.Contains("--agent-status-mcp", StringComparer.Ordinal))
            return Task.Run(() => AgentStatus.Mcp.AgentStatusMcpHost.RunConsoleAsync()).GetAwaiter().GetResult();
        return RunDesktop();
    }

    /// <summary>What the generated App.Main did. A separate method so the JIT, which resolves every type a method
    /// uses before running it, does not load WPF into a helper while compiling Main.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunDesktop()
    {
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
