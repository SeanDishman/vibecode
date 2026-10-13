using System.Diagnostics;
using System.Reflection;
using VibeCode.Services;

var storage = Path.Combine(Path.GetTempPath(), "VibeCode CLI tests " + Guid.NewGuid().ToString("N"));
Environment.SetEnvironmentVariable("VIBECODE_RUNTIME_DIR", storage);
Directory.CreateDirectory(storage);
var checks = 0;
void Check(bool condition, string title) { if (!condition) throw new Exception(title); checks++; Console.WriteLine("PASS " + title); }

if (args.Contains("--install") || args.Contains("--install-kimi"))
{
    // Opt-in network integration: an empty, isolated managed-tools directory, with no provider login or model request.
    var providers = args.Contains("--install-kimi") ? new[] { "kimi" } : new[] { "claude", "codex", "gemini", "kimi" };
    foreach (var provider in providers)
    {
        await CliInstallationService.InstallAsync(provider, new Progress<string>(s => Console.WriteLine(provider + ": " + s)));
        Check(CliInstallationService.IsReady(provider), provider + " fresh install and version check");
    }
    if (args.Contains("--install-kimi"))
    {
        var git = Path.Combine(storage, "git", "cmd", "git.exe");
        Check(File.Exists(git) && (await CliInstallationService.RunAsync(git, ["--version"], CancellationToken.None)).StartsWith("git version"), "fresh Git dependency starts successfully");
    }
    Console.WriteLine("Installed runtimes retained for inspection: " + storage);
    Console.WriteLine($"{checks} integration checks passed.");
    return;
}

try
{
    foreach (var provider in new[] { "claude", "codex", "gemini", "kimi" })
        Check(!CliInstallationService.IsReady(provider), provider + " starts missing in an isolated tool directory");
    foreach (var (provider, relative) in new[] {
        ("claude", "node_modules/@anthropic-ai/claude-code/bin/claude.exe"),
        ("codex", "node_modules/@openai/codex-win32-x64/vendor/x86_64-pc-windows-msvc/codex/codex.exe"),
        ("gemini", "node_modules/@google/gemini-cli/bundle/gemini.js"),
        ("kimi", "bin/kimi.exe") })
    {
        var file = Path.Combine(storage, provider, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "fixture");
        Check(CliInstallationService.ManagedPath(provider) == file.Replace('/', Path.DirectorySeparatorChar), provider + " discovery handles spaces and native package layout");
    }
    Check(!CliInstallationService.Supports("glm"), "API provider needs no CLI");
    try { await CliInstallationService.InstallAsync("invalid"); throw new Exception("Unsupported provider accepted"); }
    catch (ArgumentException) { checks++; Console.WriteLine("PASS unsupported installation rejected"); }
    using (var canceled = new CancellationTokenSource())
    {
        canceled.Cancel();
        try { await CliInstallationService.InstallAsync("claude", cancellationToken: canceled.Token); throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { checks++; Console.WriteLine("PASS canceled installation stops before work"); }
    }
    var ps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
    var echo = await CliInstallationService.RunAsync(ps, ["-NoProfile", "-NonInteractive", "-Command", "[Console]::Write('cli-ready')"], CancellationToken.None);
    Check(echo == "cli-ready", "installer captures child output");
    using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(700)))
    {
        var elapsed = Stopwatch.StartNew();
        try { await CliInstallationService.RunAsync(ps, ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30"], cancel.Token); throw new Exception("Child cancellation ignored"); }
        catch (OperationCanceledException) { Check(elapsed.Elapsed < TimeSpan.FromSeconds(8), "cancel terminates the child without waiting for its work"); }
    }
    var guard = typeof(CliInstallationService).GetMethod("RequireManagedChild", BindingFlags.Static | BindingFlags.NonPublic)!;
    try { guard.Invoke(null, [Path.GetTempPath()]); throw new Exception("Out-of-root target accepted"); }
    catch (TargetInvocationException e) when (e.InnerException is IOException) { checks++; Console.WriteLine("PASS runtime writes reject paths outside the managed directory"); }
    Console.WriteLine($"{checks} checks passed.");
}
finally
{
    var resolved = Path.GetFullPath(storage);
    if (resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
        && Path.GetFileName(resolved).StartsWith("VibeCode CLI tests ", StringComparison.Ordinal)) Directory.Delete(resolved, true);
}
