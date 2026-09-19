using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using VibeCode.Services;

namespace VibeCode.Protocol;

public sealed class GrokSessionOptions
{
    public required string Cwd { get; init; }
    /// <summary>Account-specific auth.json. GROK_HOME remains shared so config, plugins, and sessions stay unified.</summary>
    public string? AuthFilePath { get; init; }
    public string? Resume { get; init; }
    public bool ForkSession { get; init; }
    public string? Model { get; init; }
    public string? Effort { get; init; }
    public string PermissionMode { get; init; } = "default";
    public string? AppendSystemPrompt { get; init; }
    public IReadOnlyList<McpServerDefinition>? McpServers { get; init; }
}

/// <summary>
/// First-class Grok session facade. Grok and Kimi both speak ACP, so the facade deliberately reuses the hardened
/// stream, attachment, tool, and permission translation while selecting Grok's lifecycle and model semantics.
/// </summary>
public sealed class GrokSession : ICodingSession
{
    private const string GrokAuthPathEnvironment = "GROK_AUTH_PATH";
    private const string GrokAuthEnvironment = "GROK_AUTH";
    private readonly KimiSession _inner;

    public GrokSession(GrokSessionOptions options)
    {
        _ = ResolveCliPath();
        _inner = new KimiSession(new KimiSessionOptions
        {
            Cwd = options.Cwd,
            Resume = options.Resume,
            ForkSession = options.ForkSession,
            Model = options.Model,
            Effort = options.Effort,
            PermissionMode = options.PermissionMode,
            AppendSystemPrompt = options.AppendSystemPrompt,
            McpServers = options.McpServers,
            UseGrokProtocol = true,
            GrokAuthFilePath = options.AuthFilePath,
        });
    }

    public event Action<JsonNode>? MessageReceived { add => _inner.MessageReceived += value; remove => _inner.MessageReceived -= value; }
    public event Action<PermissionRequest>? PermissionRequested { add => _inner.PermissionRequested += value; remove => _inner.PermissionRequested -= value; }
    public event Action<string>? PermissionCancelled { add => _inner.PermissionCancelled += value; remove => _inner.PermissionCancelled -= value; }
    public event Action<int, string>? Exited { add => _inner.Exited += value; remove => _inner.Exited -= value; }
    public event Action? Initialized { add => _inner.Initialized += value; remove => _inner.Initialized -= value; }
    public JsonArray Commands => _inner.Commands;
    public JsonArray Models => _inner.Models;
    public string? SessionId => _inner.SessionId;
    public bool HasExited => _inner.HasExited;
    public void Start() => _inner.Start();
    public void SendUser(JsonNode content) => _inner.SendUser(content);
    public Task InterruptAsync() => _inner.InterruptAsync();
    public Task SetPermissionModeAsync(string mode) => _inner.SetPermissionModeAsync(mode);
    public Task SetModelAsync(string? model, string? effort = null) => _inner.SetModelAsync(model, effort);
    public void RespondPermission(string requestId, JsonObject result, string? toolUseId) =>
        _inner.RespondPermission(requestId, result, toolUseId);
    public void Dispose() => _inner.Dispose();

    public static string ResolveCliPath()
    {
        foreach (var candidate in CliCandidates())
        {
            try { if (File.Exists(candidate)) return Path.GetFullPath(candidate); }
            catch { }
        }
        throw new FileNotFoundException("Install the Grok CLI or set VIBECODE_GROK_PATH to its executable.");
    }

    internal static IReadOnlyList<string> CliCandidates()
    {
        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable("VIBECODE_GROK_PATH") is { Length: > 0 } configured)
            candidates.Add(configured.Trim('"'));
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var grokHome = Environment.GetEnvironmentVariable("GROK_HOME");
        if (string.IsNullOrWhiteSpace(grokHome)) grokHome = Path.Combine(profile, ".grok");
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "grok.exe"));
        candidates.Add(Path.Combine(grokHome.Trim('"'), "bin", "grok.exe"));
        candidates.Add(Path.Combine(profile, ".local", "bin", "grok.exe"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "grok.cmd"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links", "grok.exe"));
        foreach (var value in new[] { Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) })
        foreach (var directory in (value ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        foreach (var name in new[] { "grok.exe", "grok.cmd" })
            candidates.Add(Path.Combine(directory.Trim('"'), name));
        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static ProcessStartInfo CreateCliStartInfo(string? workingDirectory, params string[] args)
        => CreateCliStartInfoForAuth(workingDirectory, null, args);

    /// <summary>Launch Grok against one managed credential file without splitting the user's config/session home.</summary>
    public static ProcessStartInfo CreateCliStartInfoForAuth(string? workingDirectory, string? authFilePath,
        params string[] args)
    {
        var cli = ResolveCliPath();
        var extension = Path.GetExtension(cli).ToLowerInvariant();
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        if (!string.IsNullOrWhiteSpace(workingDirectory)) psi.WorkingDirectory = workingDirectory;
        if (extension is ".cmd" or ".bat")
        {
            psi.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            psi.ArgumentList.Add("/d");
            psi.ArgumentList.Add("/s");
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(QuoteCmd(cli, args));
        }
        else if (extension == ".ps1")
        {
            psi.FileName = "powershell.exe";
            psi.ArgumentList.Add("-NoLogo");
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(cli);
            foreach (var arg in args) psi.ArgumentList.Add(arg);
        }
        else if (extension is ".mjs" or ".js")
        {
            psi.FileName = "node.exe";
            psi.ArgumentList.Add(cli);
            foreach (var arg in args) psi.ArgumentList.Add(arg);
        }
        else
        {
            psi.FileName = cli;
            foreach (var arg in args) psi.ArgumentList.Add(arg);
        }
        if (!string.IsNullOrWhiteSpace(authFilePath))
        {
            var fullAuthPath = Path.GetFullPath(authFilePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullAuthPath)!);
            psi.Environment[GrokAuthPathEnvironment] = fullAuthPath;
            // Inline credentials outrank GROK_AUTH_PATH in Grok Build. A parent-shell override must never make a
            // managed row silently run as a different account.
            psi.Environment.Remove(GrokAuthEnvironment);
        }
        // A selected xAI API-key account replaces the auth-file login for this launch only.
        VibeCode.Services.ApiKeyAccountService.Instance.ApplyTo(psi, "grok");
        return psi;
    }

    private static string QuoteCmd(string executable, IEnumerable<string> args)
    {
        static string Q(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
        return Q(executable) + " " + string.Join(" ", args.Select(Q));
    }
}
