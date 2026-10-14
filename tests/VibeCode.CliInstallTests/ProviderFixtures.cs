using System.Diagnostics;
using VibeCode.Services;

// Resolve only the isolated managed directory. Existing developer CLIs cannot make the fresh-install test pass.
namespace VibeCode.Protocol
{
    public static class ClaudeSession { public static string ResolveCliPath() => CliInstallationService.ManagedPath("claude") ?? throw new FileNotFoundException(); }
    public static class CodexSession { public static string ResolveCliPath() => CliInstallationService.ManagedPath("codex") ?? throw new FileNotFoundException(); }
    public static class ProcessJob { public static void Assign(Process process) { } }
}
namespace VibeCode.Services
{
}
