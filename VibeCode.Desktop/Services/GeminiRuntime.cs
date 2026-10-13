using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VibeCode.Protocol;

namespace VibeCode.Services;

/// <summary>Launches the unmodified Google CLI. OAuth refresh and browser consent belong to that CLI.</summary>
public static class GeminiRuntime
{
    public const string SupportedVersion = "0.63.0";
    public static string InstallDirectory => Path.Combine(CliInstallationService.Root, "gemini");
    public static string Home => Environment.GetEnvironmentVariable("GEMINI_CLI_HOME") is { Length: > 0 } custom
        ? Path.GetFullPath(custom) : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static string ConfigDirectory => Path.Combine(Home, ".gemini");
    public static bool UsesEncryptedCredentials => Environment.GetEnvironmentVariable("GEMINI_FORCE_ENCRYPTED_FILE_STORAGE") == "true";
    public static bool HasLogin => UsesEncryptedCredentials ? AccountEmail is not null : File.Exists(Path.Combine(ConfigDirectory, "oauth_creds.json"));
    public static string? AccountEmail
    {
        get
        {
            try
            {
                var account = JsonNode.Parse(File.ReadAllText(Path.Combine(ConfigDirectory, "google_accounts.json")))?["active"]?.ToString();
                return string.IsNullOrWhiteSpace(account) ? null : account;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
        }
    }
    public static bool IsInstalled { get { try { ResolveCliPath(); return true; } catch (FileNotFoundException) { return false; } } }

    public static string ResolveCliPath()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("VIBECODE_GEMINI_PATH"),
            Path.Combine(InstallDirectory, "node_modules", "@google", "gemini-cli", "bundle", "gemini.js"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules", "@google", "gemini-cli", "bundle", "gemini.js"),
        };
        foreach (var path in candidates)
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) return Path.GetFullPath(path);
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var path = Path.Combine(directory.Trim('"'), "node_modules", "@google", "gemini-cli", "bundle", "gemini.js");
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException("Gemini CLI is not installed. Open AI accounts → Gemini to install and sign in with Google.");
    }

    public static ProcessStartInfo CreateStartInfo(string cwd, params string[] arguments)
    {
        var path = ResolveCliPath();
        var script = Path.GetExtension(path) is ".js" or ".mjs";
        var psi = new ProcessStartInfo(script ? CliInstallationService.NodePath : path)
        {
            WorkingDirectory = cwd,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        if (script) psi.ArgumentList.Add(path);
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);
        // A Google subscription must never silently become a separately billed API/Vertex session.
        foreach (var key in new[] { "GEMINI_API_KEY", "GOOGLE_API_KEY", "GOOGLE_GENAI_USE_VERTEXAI", "GOOGLE_GENAI_USE_GCA", "GOOGLE_GEMINI_BASE_URL" })
            psi.Environment.Remove(key);
        psi.Environment["GEMINI_TELEMETRY_ENABLED"] = "false";
        psi.Environment["GEMINI_CLI_SYSTEM_SETTINGS_PATH"] = OAuthSettingsPath();
        psi.Environment["NO_COLOR"] = "1";
        return psi;
    }

    private static readonly object SettingsGate = new();
    private static string OAuthSettingsPath()
    {
        lock (SettingsGate)
        {
            var source = Environment.GetEnvironmentVariable("GEMINI_CLI_SYSTEM_SETTINGS_PATH")
                ?? @"C:\ProgramData\gemini-cli\settings.json";
            var settings = File.Exists(source) ? JsonNode.Parse(File.ReadAllText(source), documentOptions:
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })?.AsObject() ?? new() : new JsonObject();
            var security = settings["security"] as JsonObject ?? new JsonObject();
            if (security.Parent is null) settings["security"] = security;
            var auth = security["auth"] as JsonObject ?? new JsonObject();
            if (auth.Parent is null) security["auth"] = auth;
            if (auth["enforcedType"]?.ToString() is { Length: > 0 } enforced && enforced != "oauth-personal")
                throw new InvalidOperationException("Your Gemini administrator requires a different authentication method. Google subscription sign-in is unavailable under that policy.");
            auth["selectedType"] = "oauth-personal";
            auth["enforcedType"] = "oauth-personal";
            // Keep every existing system policy, MCP server and setting; only this process's authentication is selected.
            var path = Path.Combine(AppSettings.Dir, "gemini", "google-sign-in-settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, settings.ToJsonString());
            return path;
        }
    }

    public static Task InstallAsync(CancellationToken cancellationToken = default) => CliInstallationService.InstallAsync("gemini", cancellationToken: cancellationToken);

    /// <summary>No session or model request is created by signing in.</summary>
    public static async Task SignInAsync(CancellationToken cancellationToken = default)
    {
        if (!CliInstallationService.IsReady("gemini")) await InstallAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        using var process = new Process { StartInfo = CreateStartInfo(Home, "--acp") };
        process.Start();
        ProcessJob.Assign(process);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await Request(1, "initialize", new JsonObject
            {
                ["protocolVersion"] = 1,
                ["clientCapabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "vibecode", ["version"] = "1.0" },
            });
            await Request(2, "authenticate", new JsonObject { ["methodId"] = "oauth-personal" });
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            try { await stderr; } catch (OperationCanceledException) { }
        }

        async Task Request(int id, string method, JsonObject parameters)
        {
            var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters };
            await process.StandardInput.WriteLineAsync(request.ToJsonString().AsMemory(), timeout.Token);
            await process.StandardInput.FlushAsync(timeout.Token);
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                JsonNode? reply;
                try { reply = JsonNode.Parse(line); } catch { continue; }
                if (reply?["id"]?.ToString() != id.ToString()) continue;
                if (reply["error"] is { } error) throw new InvalidOperationException(error["message"]?.ToString() ?? "Google sign-in failed.");
                return;
            }
            throw new IOException("Gemini closed before Google sign-in completed.");
        }
    }
}
