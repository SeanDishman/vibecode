using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VibeCode.Services;

/// <summary>One editor VibeCode knows how to hand a file to.</summary>
public sealed record ExternalEditor(
    string Id,
    string Name,
    string Executable,
    ExternalEditorStyle Style)
{
    /// <summary>"VS Code · 1.2.3" style secondary line; just the folder for now.</summary>
    public string Location => Path.GetDirectoryName(Executable) ?? Executable;
}

/// <summary>How to phrase "open this file at this line" for a given family of editors.</summary>
public enum ExternalEditorStyle
{
    /// <summary>VS Code and its forks: <c>-g file:line:col</c>.</summary>
    VsCodeGoto,
    /// <summary>JetBrains IDEs: <c>--line N file</c>.</summary>
    JetBrainsLine,
    /// <summary>Sublime Text and Zed: <c>file:line</c>.</summary>
    ColonSuffix,
    /// <summary>Notepad++: <c>-nLINE file</c>.</summary>
    NotepadPlusPlus,
    /// <summary>Visual Studio: <c>/edit file</c> (no line jump).</summary>
    VisualStudioEdit,
    /// <summary>Anything else: just pass the path.</summary>
    PlainPath,
    /// <summary>Not an editor - hand the file to Windows' default association.</summary>
    ShellDefault,
    /// <summary>Not an editor - highlight the file in File Explorer.</summary>
    RevealInExplorer,
}

/// <summary>
/// Finds the code editors actually installed on this machine and opens a file in one, jumping to a line
/// where the editor supports it.
///
/// Detection is deliberately belt-and-braces: PATH first (which is how most of these register), then the
/// known per-user and per-machine install locations, because a great many people install VS Code or Cursor
/// without ever adding the shim to PATH — checking only PATH would report "no editors found" on a machine
/// that plainly has three.
/// </summary>
public static class ExternalEditorService
{
    private static List<ExternalEditor>? _cache;

    private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string Roaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static string PF => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    private static string PF86 => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

    private static string PrefFile => Path.Combine(AppSettings.Dir, "editor-preference.json");

    /// <summary>The editor the user last opened a file with, if it is still installed.</summary>
    public static string? PreferredId
    {
        get
        {
            try
            {
                if (!File.Exists(PrefFile)) return null;
                return JsonSerializer.Deserialize<Pref>(File.ReadAllText(PrefFile))?.Id;
            }
            catch { return null; }
        }
        set
        {
            try
            {
                Directory.CreateDirectory(AppSettings.Dir);
                File.WriteAllText(PrefFile, JsonSerializer.Serialize(new Pref { Id = value }));
            }
            catch { /* preference is a convenience, not worth failing the open over */ }
        }
    }

    private sealed class Pref { public string? Id { get; set; } }

    public static void InvalidateCache() => _cache = null;

    /// <summary>Every editor found on this machine, best-known first, always ending with the two fallbacks.</summary>
    public static IReadOnlyList<ExternalEditor> Detect(bool refresh = false)
    {
        if (!refresh && _cache is not null) return _cache;

        var found = new List<ExternalEditor>();
        void Add(string id, string name, ExternalEditorStyle style, params string?[] candidates)
        {
            if (found.Any(f => f.Id == id)) return;
            foreach (var c in candidates)
            {
                if (string.IsNullOrWhiteSpace(c)) continue;
                var resolved = Resolve(c!);
                if (resolved is null) continue;
                found.Add(new ExternalEditor(id, name, resolved, style));
                return;
            }
        }

        // ── VS Code and its forks (all share the -g file:line:col contract)
        Add("vscode", "Visual Studio Code", ExternalEditorStyle.VsCodeGoto,
            "code.cmd", "code",
            Path.Combine(Local, @"Programs\Microsoft VS Code\bin\code.cmd"),
            Path.Combine(PF, @"Microsoft VS Code\bin\code.cmd"),
            Path.Combine(PF86, @"Microsoft VS Code\bin\code.cmd"));
        Add("vscode-insiders", "VS Code Insiders", ExternalEditorStyle.VsCodeGoto,
            "code-insiders.cmd", "code-insiders",
            Path.Combine(Local, @"Programs\Microsoft VS Code Insiders\bin\code-insiders.cmd"));
        Add("cursor", "Cursor", ExternalEditorStyle.VsCodeGoto,
            "cursor.cmd", "cursor",
            Path.Combine(Local, @"Programs\cursor\resources\app\bin\cursor.cmd"),
            Path.Combine(Local, @"Programs\Cursor\resources\app\bin\cursor.cmd"));
        Add("windsurf", "Windsurf", ExternalEditorStyle.VsCodeGoto,
            "windsurf.cmd", "windsurf",
            Path.Combine(Local, @"Programs\Windsurf\bin\windsurf.cmd"));
        Add("trae", "Trae", ExternalEditorStyle.VsCodeGoto,
            "trae.cmd", "trae",
            Path.Combine(Local, @"Programs\Trae\bin\trae.cmd"));
        Add("vscodium", "VSCodium", ExternalEditorStyle.VsCodeGoto,
            "codium.cmd", "codium",
            Path.Combine(Local, @"Programs\VSCodium\bin\codium.cmd"));

        // ── JetBrains. Toolbox writes .cmd shims; standalone installs live under Program Files.
        AddJetBrains(found, "rider", "JetBrains Rider", "rider", "rider64.exe");
        AddJetBrains(found, "idea", "IntelliJ IDEA", "idea", "idea64.exe");
        AddJetBrains(found, "webstorm", "WebStorm", "webstorm", "webstorm64.exe");
        AddJetBrains(found, "pycharm", "PyCharm", "pycharm", "pycharm64.exe");
        AddJetBrains(found, "clion", "CLion", "clion", "clion64.exe");
        AddJetBrains(found, "goland", "GoLand", "goland", "goland64.exe");
        AddJetBrains(found, "phpstorm", "PhpStorm", "phpstorm", "phpstorm64.exe");
        AddJetBrains(found, "rubymine", "RubyMine", "rubymine", "rubymine64.exe");

        // ── standalone editors
        Add("sublime", "Sublime Text", ExternalEditorStyle.ColonSuffix,
            "subl.exe", "subl",
            Path.Combine(PF, @"Sublime Text\subl.exe"),
            Path.Combine(PF, @"Sublime Text 3\subl.exe"),
            Path.Combine(PF86, @"Sublime Text\subl.exe"));
        Add("zed", "Zed", ExternalEditorStyle.ColonSuffix,
            "zed.exe", "zed",
            Path.Combine(Local, @"Zed\bin\zed.exe"),
            Path.Combine(Local, @"Programs\Zed\bin\zed.exe"));
        Add("notepadpp", "Notepad++", ExternalEditorStyle.NotepadPlusPlus,
            "notepad++.exe",
            Path.Combine(PF, @"Notepad++\notepad++.exe"),
            Path.Combine(PF86, @"Notepad++\notepad++.exe"));
        Add("neovim", "Neovim", ExternalEditorStyle.PlainPath,
            "nvim.exe", "nvim");

        AddVisualStudio(found);

        // ── always-available fallbacks
        found.Add(new ExternalEditor("notepad", "Notepad", "notepad.exe", ExternalEditorStyle.PlainPath));
        found.Add(new ExternalEditor("shell", "System default app", "", ExternalEditorStyle.ShellDefault));
        found.Add(new ExternalEditor("explorer", "Show in File Explorer", "", ExternalEditorStyle.RevealInExplorer));

        _cache = found;
        return found;
    }

    private static void AddJetBrains(List<ExternalEditor> found, string id, string name, string shim, string exe)
    {
        if (found.Any(f => f.Id == id)) return;

        var candidates = new List<string>
        {
            shim + ".cmd", shim,
            Path.Combine(Local, @"JetBrains\Toolbox\scripts", shim + ".cmd"),
        };
        // Toolbox keeps versioned app directories; take the newest bin\<exe> it has.
        foreach (var root in new[]
                 {
                     Path.Combine(Local, @"JetBrains\Toolbox\apps"),
                     Path.Combine(PF, "JetBrains"),
                     Path.Combine(PF86, "JetBrains"),
                 })
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                var hits = Directory.EnumerateFiles(root, exe, SearchOption.AllDirectories)
                    .Where(p => p.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(p => p)
                    .Take(1);
                candidates.AddRange(hits);
            }
            catch { /* permission or reparse-point noise while probing */ }
        }

        foreach (var c in candidates)
        {
            var resolved = Resolve(c);
            if (resolved is null) continue;
            found.Add(new ExternalEditor(id, name, resolved, ExternalEditorStyle.JetBrainsLine));
            return;
        }
    }

    private static void AddVisualStudio(List<ExternalEditor> found)
    {
        try
        {
            var vswhere = Path.Combine(PF86, @"Microsoft Visual Studio\Installer\vswhere.exe");
            if (File.Exists(vswhere))
            {
                var psi = new ProcessStartInfo(vswhere,
                    "-latest -prerelease -property productPath -format value")
                { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                using var p = Process.Start(psi);
                var path = p?.StandardOutput.ReadToEnd().Trim();
                p?.WaitForExit(4000);
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    found.Add(new ExternalEditor("vs", "Visual Studio", path!, ExternalEditorStyle.VisualStudioEdit));
                    return;
                }
            }
        }
        catch { /* vswhere missing or misbehaving just means no VS entry */ }

        foreach (var root in new[] { PF, PF86 })
        {
            try
            {
                var baseDir = Path.Combine(root, "Microsoft Visual Studio");
                if (!Directory.Exists(baseDir)) continue;
                var devenv = Directory.EnumerateFiles(baseDir, "devenv.exe", SearchOption.AllDirectories)
                    .OrderByDescending(p => p).FirstOrDefault();
                if (devenv is not null)
                {
                    found.Add(new ExternalEditor("vs", "Visual Studio", devenv, ExternalEditorStyle.VisualStudioEdit));
                    return;
                }
            }
            catch { /* keep probing */ }
        }
    }

    /// <summary>Absolute path if it exists, otherwise the first PATH hit, otherwise null.</summary>
    private static string? Resolve(string candidate)
    {
        try
        {
            if (candidate.Contains(Path.DirectorySeparatorChar) || candidate.Contains(Path.AltDirectorySeparatorChar))
                return File.Exists(candidate) ? candidate : null;

            var paths = Environment.GetEnvironmentVariable("PATH") ?? "";
            var exts = new[] { "", ".exe", ".cmd", ".bat" };
            foreach (var dir in paths.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                foreach (var ext in exts)
                {
                    var full = Path.Combine(dir.Trim(), candidate + ext);
                    if (File.Exists(full)) return full;
                }
            }
        }
        catch { /* a malformed PATH entry must not break detection */ }
        return null;
    }

    /// <summary>Open <paramref name="file"/> in <paramref name="editor"/>, at <paramref name="line"/> when supported.</summary>
    public static bool Open(ExternalEditor editor, string file, int? line = null)
    {
        try
        {
            file = Path.GetFullPath(file);
        }
        catch { return false; }

        try
        {
            switch (editor.Style)
            {
                case ExternalEditorStyle.ShellDefault:
                    Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
                    return true;

                case ExternalEditorStyle.RevealInExplorer:
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\""));
                    return true;
            }

            var psi = new ProcessStartInfo(editor.Executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(file) ?? "",
            };

            switch (editor.Style)
            {
                case ExternalEditorStyle.VsCodeGoto:
                    psi.ArgumentList.Add("-g");
                    psi.ArgumentList.Add(line is > 0 ? $"{file}:{line}" : file);
                    break;
                case ExternalEditorStyle.JetBrainsLine:
                    if (line is > 0) { psi.ArgumentList.Add("--line"); psi.ArgumentList.Add(line.Value.ToString()); }
                    psi.ArgumentList.Add(file);
                    break;
                case ExternalEditorStyle.ColonSuffix:
                    psi.ArgumentList.Add(line is > 0 ? $"{file}:{line}" : file);
                    break;
                case ExternalEditorStyle.NotepadPlusPlus:
                    if (line is > 0) psi.ArgumentList.Add($"-n{line}");
                    psi.ArgumentList.Add(file);
                    break;
                case ExternalEditorStyle.VisualStudioEdit:
                    psi.ArgumentList.Add("/edit");
                    psi.ArgumentList.Add(file);
                    break;
                default:
                    psi.ArgumentList.Add(file);
                    break;
            }

            // .cmd/.bat shims (VS Code and its forks, JetBrains Toolbox) can't be started without a shell, and the
            // shell re-parses the command line: a space splits a path in two, and & or %VAR% in a file name - which
            // comes from whatever repository an agent edited - runs as part of a command. So start the program the
            // shim wraps directly; only when that can't be found go through cmd.exe, with every argument quoted.
            var ext = Path.GetExtension(editor.Executable).ToLowerInvariant();
            if (ext is ".cmd" or ".bat")
            {
                if (ShimTarget(editor.Executable) is { } target)
                {
                    psi.FileName = target;
                    psi.Environment.Remove("ELECTRON_RUN_AS_NODE");
                }
                else if (!RouteThroughCmd(psi, editor.Executable))
                {
                    return false;
                }
            }

            Process.Start(psi);
            PreferredId = editor.Id;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static readonly Regex QuotedExe = new("\"(?<path>[^\"]+?\\.exe)\"",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Interpreters a shim may name before its real target; starting one of these with the editor's
    /// arguments would not open the editor.</summary>
    private static readonly HashSet<string> NotEditors = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd.exe", "node.exe", "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe", "conhost.exe",
    };

    /// <summary>The program a .cmd/.bat editor shim starts, when the script names it as a quoted path: VS Code's
    /// <c>"%~dp0..\Code.exe" "%~dp0..\resources\app\out\cli.js" %*</c> (likewise Cursor, Windsurf, Trae, VSCodium)
    /// or JetBrains Toolbox's <c>start "" "C:\...\rider64.exe" %*</c>. Null when none can be resolved.</summary>
    private static string? ShimTarget(string shim)
    {
        try
        {
            var info = new FileInfo(shim);
            if (!info.Exists || info.Length > 64 * 1024) return null;
            var folder = info.DirectoryName ?? "";
            foreach (Match match in QuotedExe.Matches(File.ReadAllText(shim)))
            {
                var path = match.Groups["path"].Value
                    .Replace("%~dp0", folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                if (path.Contains('%')) continue;   // depends on a variable only the shell would know
                var full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(folder, path));
                if (!NotEditors.Contains(Path.GetFileName(full)) && File.Exists(full)) return full;
            }
        }
        catch { /* unreadable shim: the quoted cmd.exe route still works */ }
        return null;
    }

    /// <summary>Start <paramref name="shim"/> through cmd.exe with the shim and every argument quoted, which keeps
    /// spaces, &amp; | ^ ( ) &lt; &gt; literal. cmd still expands %VAR% inside quotes and cannot escape it there, so
    /// an argument containing % (or a quote or a line break) is refused instead of launched.</summary>
    private static bool RouteThroughCmd(ProcessStartInfo psi, string shim)
    {
        var parts = new List<string> { shim };
        parts.AddRange(psi.ArgumentList);
        if (parts.Any(part => part.IndexOfAny(['%', '"', '\r', '\n', '\0']) >= 0)) return false;

        psi.ArgumentList.Clear();
        psi.FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        // /s /c "<line>": cmd removes exactly the outer pair of quotes and runs the rest as written.
        psi.Arguments = "/d /v:off /s /c \"" + string.Join(" ", parts.Select(part => "\"" + part + "\"")) + "\"";
        // Never the edited file's folder: that would let a bare command name resolve to a program sitting next to it.
        psi.WorkingDirectory = Path.GetDirectoryName(shim) ?? Environment.SystemDirectory;
        return true;
    }
}
