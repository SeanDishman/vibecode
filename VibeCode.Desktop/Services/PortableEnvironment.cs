using System.IO;

namespace VibeCode.Services;

/// <summary>Make direct EXE and taskbar launches use the tools shipped in the portable bundle.</summary>
internal static class PortableEnvironment
{
    public static void Configure()
    {
        var appDirectory = AppContext.BaseDirectory;
        var root = new[] { appDirectory, Directory.GetParent(appDirectory.TrimEnd(Path.DirectorySeparatorChar))?.FullName }
            .FirstOrDefault(candidate => candidate is not null
                && File.Exists(Path.Combine(candidate, "tools", "codex", "bin", "codex.exe")));
        if (root is null) return;

        var files = new Dictionary<string, string>
        {
            ["VIBECODE_CODEX_PATH"] = @"tools\codex\bin\codex.exe",
            ["VIBECODE_CLAUDE_PATH"] = @"tools\claude\claude.exe",
            ["VIBECODE_KIMI_PATH"] = @"tools\kimi\kimi.exe",
            ["VIBECODE_GROK_PATH"] = @"tools\grok\grok.exe",
            ["CLAUDE_CODE_GIT_BASH_PATH"] = @"tools\git\bin\bash.exe",
            ["GIT_SSL_CAINFO"] = @"tools\git\mingw64\etc\ssl\certs\ca-bundle.crt",
        };
        foreach (var (name, relativePath) in files)
        {
            var path = Path.Combine(root, relativePath);
            if (File.Exists(path) && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)))
                Environment.SetEnvironmentVariable(name, path);
        }

        var browser = Path.Combine(root, "tools", "webview2");
        if (File.Exists(Path.Combine(browser, "msedgewebview2.exe"))
            && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WEBVIEW2_BROWSER_EXECUTABLE_FOLDER")))
            Environment.SetEnvironmentVariable("WEBVIEW2_BROWSER_EXECUTABLE_FOLDER", browser);

        var tools = new[]
        {
            @"tools\node", @"tools\git\cmd", @"tools\git\bin", @"tools\codex\bin",
            @"tools\codex\codex-path", @"tools\claude", @"tools\kimi", @"tools\grok",
        };
        var directories = tools.Select(relativePath => Path.Combine(root, relativePath)).Where(Directory.Exists);
        var existing = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries);
        Environment.SetEnvironmentVariable("PATH", string.Join(';', directories.Concat(existing).Distinct(StringComparer.OrdinalIgnoreCase)));
    }
}
