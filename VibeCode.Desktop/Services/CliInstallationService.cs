using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using VibeCode.Protocol;

namespace VibeCode.Services;

/// <summary>Installs official runtimes for this Windows user, without changing global tools or credential stores.</summary>
public static class CliInstallationService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(15) };
    static CliInstallationService() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("VibeCode/1.1.4");
    public static string Root => Path.GetFullPath(Environment.GetEnvironmentVariable("VIBECODE_RUNTIME_DIR") is { Length: > 0 } custom
        ? custom : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VibeCode", "runtimes"));
    public static bool Supports(string provider) => provider is "claude" or "codex" or "gemini";
    public static string DisplayName(string provider) => provider switch
    { "claude" => "Claude Code", "codex" => "Codex", "gemini" => "Gemini", _ => provider };

    public static string? ManagedPath(string provider) => FindInDirectory(provider, Path.Combine(Root, provider));
    internal static string? FindInDirectory(string provider, string directory)
    {
        if (provider == "gemini") return Existing(Path.Combine(directory, "node_modules", "@google", "gemini-cli", "bundle", "gemini.js"));
        if (provider == "claude") return Existing(Path.Combine(directory, "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe"));
        if (provider == "codex")
        {
            var packages = Path.Combine(directory, "node_modules", "@openai");
            if (Directory.Exists(packages)) return Directory.EnumerateFiles(packages, "codex.exe", SearchOption.AllDirectories).FirstOrDefault();
        }
        return null;
    }
    private static string? Existing(string path) => File.Exists(path) ? path : null;
    public static string? FindOnPath(string name) => (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(directory => Path.Combine(directory.Trim('"'), name)).FirstOrDefault(File.Exists);
    public static string NodePath => CompatibleNode(Path.Combine(Root, "node", "node.exe")) ?? CompatibleNode(FindOnPath("node.exe"))
        ?? throw new FileNotFoundException("A supported Node.js version is missing. Choose Install Gemini CLI in the account manager to set it up.");

    private static string? CompatibleNode(string? path)
    {
        if (path is null || !File.Exists(path)) return null;
        try { return FileVersionInfo.GetVersionInfo(path).FileMajorPart >= 20 ? path : null; }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception) { return null; }
    }

    public static bool IsReady(string provider)
    {
        if (!Supports(provider)) return true;
        try
        {
            _ = provider switch
            {
                "claude" => ClaudeSession.ResolveCliPath(),
                "codex" => CodexSession.ResolveCliPath(),
                _ => GeminiRuntime.ResolveCliPath(),
            };
            if (provider == "gemini") _ = NodePath;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    /// <summary>Process-local PATH only. A copied EXE discovers its managed tools on its next launch.</summary>
    public static void ConfigureEnvironment()
    {
        var managed = new[] { Path.Combine(Root, "node") };
        var current = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator, managed.Where(Directory.Exists).Concat(current).Distinct(StringComparer.OrdinalIgnoreCase)));
    }

    public static async Task InstallAsync(string provider, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!Supports(provider)) throw new ArgumentException("This provider does not require an installable CLI.", nameof(provider));
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Automatic CLI setup requires Windows.");
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ConfigureEnvironment();
            if (IsReady(provider)) { progress?.Report($"{DisplayName(provider)} is ready."); return; }
            Directory.CreateDirectory(Root);
            await EnsureNodeAsync(progress, cancellationToken).ConfigureAwait(false);
            ConfigureEnvironment();
            if (IsReady(provider)) return;
            var staging = Path.Combine(Root, $".install-{provider}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(staging);
            try
            {
                progress?.Report($"Installing {DisplayName(provider)} CLI…");
                var package = provider switch
                {
                    "claude" => "@anthropic-ai/claude-code@latest",
                    "codex" => "@openai/codex@latest",
                    _ => $"@google/gemini-cli@{GeminiRuntime.SupportedVersion}",
                };
                var npm = Path.Combine(Path.GetDirectoryName(NodePath)!, "node_modules", "npm", "bin", "npm-cli.js");
                await RunAsync(NodePath, [npm, "install", "--prefix", staging, package, "--include=optional", "--no-audit", "--no-fund",
                    "--registry=https://registry.npmjs.org"], cancellationToken).ConfigureAwait(false);
                var cli = FindInDirectory(provider, staging) ?? throw new IOException("The installer finished without the CLI. Retry the installation.");
                progress?.Report($"Checking {DisplayName(provider)} CLI…");
                using var check = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                check.CancelAfter(TimeSpan.FromSeconds(45));
                if (provider == "gemini") await RunAsync(NodePath, [cli, "--version"], check.Token).ConfigureAwait(false);
                else await RunAsync(cli, ["--version"], check.Token).ConfigureAwait(false);
                Promote(staging, Path.Combine(Root, provider));
                ConfigureEnvironment();
                if (!IsReady(provider)) throw new IOException("The CLI was installed but is not available. Close setup and retry.");
                progress?.Report($"{DisplayName(provider)} CLI installed. Ready to sign in.");
            }
            finally { RemoveStaging(staging); }
        }
        finally { Gate.Release(); }
    }

    private static async Task EnsureNodeAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var managed = Path.Combine(Root, "node", "node.exe");
        if (CompatibleNode(managed) is not null) return;
        // A managed LTS copy avoids depending on Node/npm being installed or present in the app's inherited PATH.
        progress?.Report("Setting up Node.js for the CLI…");
        var releases = JsonNode.Parse(await Http.GetStringAsync("https://nodejs.org/dist/index.json", cancellationToken).ConfigureAwait(false))!.AsArray();
        var version = releases.OfType<JsonObject>().Select(r => r["version"]?.ToString()).FirstOrDefault(v => v?.StartsWith("v24.", StringComparison.Ordinal) == true)
            ?? throw new IOException("Could not find the supported Node.js LTS release.");
        if (!Regex.IsMatch(version, @"^v24\.\d+\.\d+$")) throw new IOException("Invalid Node.js release metadata.");
        var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        var name = $"node-{version}-win-{arch}";
        var baseUrl = $"https://nodejs.org/dist/{version}/";
        var sums = await Http.GetStringAsync(baseUrl + "SHASUMS256.txt", cancellationToken).ConfigureAwait(false);
        var checksum = sums.Split('\n').Select(l => l.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .FirstOrDefault(parts => parts.Length == 2 && parts[1] == name + ".zip")?.FirstOrDefault()
            ?? throw new IOException("Node.js did not publish a checksum for this platform.");
        await InstallArchiveAsync(baseUrl + name + ".zip", checksum, "node", name, progress, cancellationToken).ConfigureAwait(false);
    }



    private static async Task InstallArchiveAsync(string url, string checksum, string component, string? inner,
        IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var staging = Path.Combine(Root, $".install-{component}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            var zip = Path.Combine(staging, "download.zip");
            await DownloadAsync(url, zip, checksum, progress, cancellationToken).ConfigureAwait(false);
            progress?.Report($"Unpacking {component}…");
            var unpacked = Path.Combine(staging, "unpacked");
            ZipFile.ExtractToDirectory(zip, unpacked);
            cancellationToken.ThrowIfCancellationRequested();
            Promote(inner is null ? unpacked : Path.Combine(unpacked, inner), Path.Combine(Root, component));
        }
        finally { RemoveStaging(staging); }
    }

    private static async Task DownloadAsync(string url, string destination, string checksum, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (!Regex.IsMatch(checksum, "^[a-fA-F0-9]{64}$")) throw new IOException("The publisher supplied an invalid checksum.");
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
        {
            var buffer = new byte[81920]; long total = 0; var last = -1;
            while (await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) is var count && count > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                total += count;
                if (response.Content.Headers.ContentLength is > 0 and var length)
                {
                    var percent = (int)(total * 100 / length);
                    if (percent != last) { last = percent; progress?.Report($"Downloading… {percent}%"); }
                }
            }
        }
        await using var downloaded = File.OpenRead(destination);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(downloaded, cancellationToken).ConfigureAwait(false));
        if (!string.Equals(actual, checksum, StringComparison.OrdinalIgnoreCase)) throw new IOException("The download checksum did not match. Retry to download a fresh copy.");
    }

    internal static async Task<string> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["NO_COLOR"] = "1";
        using var process = Process.Start(start) ?? throw new IOException("The CLI installer could not start.");
        ProcessJob.Assign(process);
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var result = await output.ConfigureAwait(false);
            await error.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException($"CLI setup exited with code {process.ExitCode}. Check your connection and available disk space, then retry.");
            return result;
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            try { await Task.WhenAll(output, error).ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }

    private static void Promote(string staging, string destination)
    {
        RequireManagedChild(staging); RequireManagedChild(destination);
        if (Directory.Exists(destination)) Directory.Move(destination, destination + ".previous-" + Guid.NewGuid().ToString("N"));
        Directory.Move(staging, destination);
    }
    private static void RequireManagedChild(string path)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Root)) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid managed runtime path.");
    }
    private static void RemoveStaging(string path)
    {
        RequireManagedChild(path);
        if (!Path.GetFileName(path).StartsWith(".install-", StringComparison.Ordinal)) throw new IOException("Invalid installer staging path.");
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
