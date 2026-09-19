using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace VibeCode.Protocol;

/// <summary>The outcome of running one tool: what to show, and whether it failed.</summary>
public sealed record ToolOutcome(string Text, bool IsError)
{
    public static ToolOutcome Ok(string text) => new(text, false);
    public static ToolOutcome Fail(string text) => new(text, true);
}

/// <summary>
/// The tools a GLM session can run, and the code that runs them.
///
/// The names are deliberately Claude's (<c>Read</c>, <c>Write</c>, <c>Edit</c>, <c>Bash</c>, ...) rather than
/// anything GLM-specific. VibeCode's transcript already knows how to render those: an <c>Edit</c> draws a
/// real diff, <c>Bash</c> gets a terminal block, <c>Read</c> collapses to a file chip. Inventing new names would
/// have meant reimplementing all of that presentation for one provider.
///
/// Every path is resolved inside the session's working directory and rejected if it escapes, so a model that
/// invents "../../Windows/System32/..." gets an error rather than a write.
/// </summary>
internal static class GlmTools
{
    /// <summary>Cap on what a single tool may put back into the context window.</summary>
    private const int MaxOutputChars = 30_000;
    private const int DefaultReadLines = 2_000;

    /// <summary>Tools whose effect is visible outside the process, so they need the user's approval.</summary>
    public static bool NeedsApproval(string tool) =>
        tool is "Write" or "Edit" or "Bash" or "PowerShell";

    /// <summary>The OpenAI function-tool schema advertised on every request.</summary>
    public static JsonArray Schema() =>
    [
        Function("Read", "Read a UTF-8 text file from the workspace.", new JsonObject
        {
            ["file_path"] = Param("string", "Path to the file, absolute or relative to the workspace root."),
            ["offset"] = Param("integer", "1-based first line to read. Optional."),
            ["limit"] = Param("integer", $"How many lines to read. Defaults to {DefaultReadLines}."),
        }, "file_path"),

        Function("Write", "Create a file, or replace an existing file's entire contents.", new JsonObject
        {
            ["file_path"] = Param("string", "Path to write, absolute or relative to the workspace root."),
            ["content"] = Param("string", "The complete new contents of the file."),
        }, "file_path", "content"),

        Function("Edit",
            "Replace an exact substring in a file. Prefer this over Write when changing part of a file.",
            new JsonObject
            {
                ["file_path"] = Param("string", "Path to the file to change."),
                ["old_string"] = Param("string", "Exact text to find, including indentation. Must be unique unless replace_all is true."),
                ["new_string"] = Param("string", "Text to put in its place."),
                ["replace_all"] = Param("boolean", "Replace every occurrence instead of requiring a unique match."),
            }, "file_path", "old_string", "new_string"),

        Function("LS", "List the entries of a directory.", new JsonObject
        {
            ["path"] = Param("string", "Directory to list. Defaults to the workspace root."),
        }),

        Function("Glob", "Find files by glob pattern, newest first.", new JsonObject
        {
            ["pattern"] = Param("string", "Glob such as **/*.cs or src/*.ts."),
            ["path"] = Param("string", "Directory to search from. Defaults to the workspace root."),
        }, "pattern"),

        Function("Grep", "Search file contents with a regular expression.", new JsonObject
        {
            ["pattern"] = Param("string", "The regular expression to search for."),
            ["path"] = Param("string", "File or directory to search. Defaults to the workspace root."),
            ["glob"] = Param("string", "Only search files matching this glob, e.g. *.cs."),
        }, "pattern"),

        Function("PowerShell",
            "Run a PowerShell command in the workspace and return its combined output. This is the machine's "
            + "primary shell — prefer it for Windows commands and .NET/dotnet tooling.", new JsonObject
        {
            ["command"] = Param("string", "The PowerShell command to run."),
            ["timeout_ms"] = Param("integer", "How long to allow, in milliseconds. Default 120000, max 600000."),
        }, "command"),

        Function("Bash",
            "Run a shell command via bash (Git Bash / WSL) when available, else cmd.exe. Use for POSIX-style "
            + "commands; use PowerShell for Windows cmdlets.", new JsonObject
        {
            ["command"] = Param("string", "The command line to run."),
            ["timeout_ms"] = Param("integer", "How long to allow, in milliseconds. Default 120000, max 600000."),
        }, "command"),
    ];

    private static JsonObject Function(string name, string description, JsonObject properties, params string[] required)
    {
        var requiredArray = new JsonArray();
        foreach (var item in required) requiredArray.Add(item);
        return new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = name,
                ["description"] = description,
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = properties,
                    ["required"] = requiredArray,
                },
            },
        };
    }

    private static JsonObject Param(string type, string description) =>
        new() { ["type"] = type, ["description"] = description };

    // ---------------- execution ----------------

    public static async Task<ToolOutcome> RunAsync(string tool, JsonObject input, string cwd, CancellationToken cancel)
    {
        try
        {
            return tool switch
            {
                "Read" => Read(input, cwd),
                "Write" => Write(input, cwd),
                "Edit" => Edit(input, cwd),
                "LS" => List(input, cwd),
                "Glob" => Glob(input, cwd),
                "Grep" => Grep(input, cwd),
                "PowerShell" => await PowerShellAsync(input, cwd, cancel).ConfigureAwait(false),
                "Bash" => await BashAsync(input, cwd, cancel).ConfigureAwait(false),
                _ => ToolOutcome.Fail($"There is no tool called \"{tool}\"."),
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // A tool that throws must come back as a readable tool_result, never as a dead turn: the model can
            // usually recover from "file not found" on its own if it is simply told.
            return ToolOutcome.Fail($"{tool} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Resolve a model-supplied path against the workspace and refuse anything outside it.
    /// </summary>
    private static string Resolve(string? path, string cwd)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("no path was given");
        var root = Path.GetFullPath(cwd);
        var full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!full.Equals(root, StringComparison.OrdinalIgnoreCase)
            && !full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException($"\"{path}\" is outside this chat's folder ({root}).");
        return full;
    }

    private static ToolOutcome Read(JsonObject input, string cwd)
    {
        var path = Resolve(Text(input, "file_path"), cwd);
        if (!File.Exists(path)) return ToolOutcome.Fail($"No such file: {path}");
        var offset = Math.Max(1, Int(input, "offset") ?? 1);
        var limit = Math.Max(1, Int(input, "limit") ?? DefaultReadLines);

        var builder = new StringBuilder();
        var line = 0;
        var shown = 0;
        foreach (var text in File.ReadLines(path))
        {
            line++;
            if (line < offset) continue;
            if (shown >= limit) { builder.Append("… (truncated; ask for more with offset/limit)\n"); break; }
            builder.Append(line).Append('\t').Append(text).Append('\n');
            shown++;
            if (builder.Length > MaxOutputChars) { builder.Append("… (truncated: too large)\n"); break; }
        }
        return shown == 0
            ? ToolOutcome.Ok($"({Path.GetFileName(path)} is empty, or offset {offset} is past its end)")
            : ToolOutcome.Ok(builder.ToString());
    }

    private static ToolOutcome Write(JsonObject input, string cwd)
    {
        var path = Resolve(Text(input, "file_path"), cwd);
        var content = Text(input, "content") ?? "";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var existed = File.Exists(path);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return ToolOutcome.Ok($"{(existed ? "Updated" : "Created")} {path} ({content.Length} characters).");
    }

    private static ToolOutcome Edit(JsonObject input, string cwd)
    {
        var path = Resolve(Text(input, "file_path"), cwd);
        if (!File.Exists(path)) return ToolOutcome.Fail($"No such file: {path}");
        var oldText = Text(input, "old_string") ?? "";
        var newText = Text(input, "new_string") ?? "";
        if (oldText.Length == 0) return ToolOutcome.Fail("old_string was empty. Use Write to create a whole file.");

        var original = File.ReadAllText(path);
        var occurrences = CountOccurrences(original, oldText);
        if (occurrences == 0)
            return ToolOutcome.Fail("old_string was not found in the file. Read it again - the text must match exactly, including indentation.");

        var all = input["replace_all"]?.GetValue<bool>() ?? false;
        if (occurrences > 1 && !all)
            return ToolOutcome.Fail($"old_string appears {occurrences} times. Add more surrounding context to make it unique, or set replace_all.");

        var updated = all
            ? original.Replace(oldText, newText, StringComparison.Ordinal)
            : ReplaceFirst(original, oldText, newText);
        File.WriteAllText(path, updated, new UTF8Encoding(false));
        return ToolOutcome.Ok($"Edited {path} ({(all ? occurrences : 1)} replacement{(all && occurrences != 1 ? "s" : "")}).");
    }

    private static string ReplaceFirst(string haystack, string needle, string replacement)
    {
        var at = haystack.IndexOf(needle, StringComparison.Ordinal);
        return at < 0 ? haystack : haystack[..at] + replacement + haystack[(at + needle.Length)..];
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static ToolOutcome List(JsonObject input, string cwd)
    {
        var path = Resolve(Text(input, "path") ?? ".", cwd);
        if (!Directory.Exists(path)) return ToolOutcome.Fail($"No such directory: {path}");
        var builder = new StringBuilder();
        foreach (var dir in Directory.EnumerateDirectories(path).OrderBy(d => d))
            builder.Append(Path.GetFileName(dir)).Append("/\n");
        foreach (var file in Directory.EnumerateFiles(path).OrderBy(f => f))
            builder.Append(Path.GetFileName(file)).Append('\n');
        return ToolOutcome.Ok(builder.Length == 0 ? "(empty directory)" : Clamp(builder.ToString()));
    }

    private static ToolOutcome Glob(JsonObject input, string cwd)
    {
        var root = Resolve(Text(input, "path") ?? ".", cwd);
        var pattern = Text(input, "pattern") ?? "*";
        var result = Walk(root)
            .Where(p => GlobMatches(pattern, Path.GetRelativePath(root, p)))
            .Select(p => new FileInfo(p))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Take(300)
            .Select(f => f.FullName)
            .ToList();
        return ToolOutcome.Ok(result.Count == 0
            ? $"No files matched {pattern}."
            : Clamp(string.Join('\n', result)));
    }

    private static ToolOutcome Grep(JsonObject input, string cwd)
    {
        var target = Resolve(Text(input, "path") ?? ".", cwd);
        var pattern = Text(input, "pattern") ?? "";
        if (pattern.Length == 0) return ToolOutcome.Fail("No pattern was given.");
        System.Text.RegularExpressions.Regex regex;
        try
        {
            regex = new System.Text.RegularExpressions.Regex(pattern,
                System.Text.RegularExpressions.RegexOptions.Compiled);
        }
        catch (ArgumentException ex) { return ToolOutcome.Fail($"That is not a valid regular expression: {ex.Message}"); }

        IEnumerable<string> files;
        if (File.Exists(target)) files = [target];
        else
        {
            var glob = Text(input, "glob");
            files = Walk(target).Where(p => string.IsNullOrWhiteSpace(glob)
                                            || GlobMatches(glob!, Path.GetRelativePath(target, p)));
        }

        var builder = new StringBuilder();
        var hits = 0;
        foreach (var file in files)
        {
            if (builder.Length > MaxOutputChars || hits >= 400) break;
            string[] lines;
            try { lines = File.ReadAllLines(file); }
            catch { continue; }   // binary or locked
            for (var i = 0; i < lines.Length; i++)
            {
                if (!regex.IsMatch(lines[i])) continue;
                builder.Append(file).Append(':').Append(i + 1).Append(": ").Append(lines[i].Trim()).Append('\n');
                if (++hits >= 400) break;
            }
        }
        return ToolOutcome.Ok(hits == 0 ? $"No matches for {pattern}." : Clamp(builder.ToString()));
    }

    /// <summary>
    /// Runs a PowerShell command. This is the primary shell on this machine, and the transcript renders a
    /// "PowerShell" card for it (icon + command header + output body) exactly like the CLI providers' shells.
    /// </summary>
    private static Task<ToolOutcome> PowerShellAsync(JsonObject input, string cwd, CancellationToken cancel)
    {
        var command = Text(input, "command") ?? "";
        if (command.Trim().Length == 0) return Task.FromResult(ToolOutcome.Fail("No command was given."));

        var psi = ShellStartInfo("powershell.exe", cwd);
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(command);
        return RunShellAsync(psi, Timeout(input), cancel);
    }

    /// <summary>
    /// Runs a shell command. Prefers a real bash (Git Bash / WSL) so bash-idiom commands work, and falls back to
    /// cmd.exe when none is installed. Renders as a "Bash" card either way.
    /// </summary>
    private static Task<ToolOutcome> BashAsync(JsonObject input, string cwd, CancellationToken cancel)
    {
        var command = Text(input, "command") ?? "";
        if (command.Trim().Length == 0) return Task.FromResult(ToolOutcome.Fail("No command was given."));

        ProcessStartInfo psi;
        if (FindBash() is { } bash)
        {
            psi = ShellStartInfo(bash, cwd);
            psi.ArgumentList.Add("-lc");
            psi.ArgumentList.Add(command);
        }
        else
        {
            // No bash on this box — run through cmd so the tool still works, and the model learns why a bashism
            // failed rather than getting a silent nonzero exit.
            psi = ShellStartInfo("cmd.exe", cwd);
            psi.ArgumentList.Add("/d");
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(command);
        }
        return RunShellAsync(psi, Timeout(input), cancel);
    }

    private static int Timeout(JsonObject input) => Math.Clamp(Int(input, "timeout_ms") ?? 120_000, 1_000, 600_000);

    private static ProcessStartInfo ShellStartInfo(string fileName, string cwd) => new()
    {
        FileName = fileName,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        WorkingDirectory = cwd,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8,
    };

    /// <summary>Locates a bash interpreter, or null when the box has none. Checked once and cached.</summary>
    private static readonly Lazy<string?> BashPath = new(() =>
    {
        var candidates = new[]
        {
            @"C:\Program Files\Git\bin\bash.exe",
            @"C:\Program Files\Git\usr\bin\bash.exe",
            @"C:\Windows\System32\bash.exe",   // WSL launcher
        };
        foreach (var path in candidates)
            if (File.Exists(path)) return path;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try { var p = Path.Combine(dir.Trim(), "bash.exe"); if (File.Exists(p)) return p; }
            catch { /* a malformed PATH entry */ }
        }
        return null;
    });

    private static string? FindBash() => BashPath.Value;

    private static async Task<ToolOutcome> RunShellAsync(ProcessStartInfo psi, int timeout, CancellationToken cancel)
    {
        using var process = new Process { StartInfo = psi };
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };

        process.Start();
        ProcessJob.Assign(process);   // dies with VibeCode rather than outliving the app
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            cancel.ThrowIfCancellationRequested();   // a user interrupt is not a tool failure
            lock (output) return ToolOutcome.Fail($"Timed out after {timeout} ms.\n{Clamp(output.ToString())}");
        }

        string text;
        lock (output) text = output.ToString();
        var body = text.Trim().Length == 0 ? "(no output)" : Clamp(text);
        return process.ExitCode == 0
            ? ToolOutcome.Ok(body)
            : ToolOutcome.Fail($"exit code {process.ExitCode}\n{body}");
    }

    /// <summary>Directories that are never worth walking and would otherwise dominate every search.</summary>
    private static readonly string[] SkipDirectories =
        [".git", "node_modules", "bin", "obj", ".vs", ".idea", "packages", "dist", "build", ".gradle"];

    /// <summary>Every file under a root, skipping the usual build spoil heaps. Errors are stepped over, not thrown:
    /// one unreadable directory must not fail an entire search.</summary>
    private static IEnumerable<string> Walk(string root)
    {
        if (!Directory.Exists(root)) yield break;
        var pending = new Stack<string>();
        pending.Push(root);
        var budget = 60_000;   // hard ceiling so a search of C:\ cannot run forever
        while (pending.Count > 0 && budget > 0)
        {
            var dir = pending.Pop();
            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch { continue; }
            foreach (var file in files)
            {
                if (--budget <= 0) yield break;
                yield return file;
            }
            string[] children;
            try { children = Directory.GetDirectories(dir); }
            catch { continue; }
            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (SkipDirectories.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                pending.Push(child);
            }
        }
    }

    /// <summary>
    /// Glob matching for the subset that actually gets used: <c>*</c>, <c>?</c>, and <c>**</c>.
    ///
    /// Written out rather than pulling in Microsoft.Extensions.FileSystemGlobbing, because one provider's file
    /// search is not worth adding a package to a desktop app's dependency closure.
    /// </summary>
    internal static bool GlobMatches(string pattern, string relativePath)
    {
        var path = relativePath.Replace('\\', '/');
        var glob = pattern.Replace('\\', '/').TrimStart('.', '/');
        // A bare "*.cs" is universally meant as "anywhere below here", which is what every other tool does too.
        if (!glob.Contains('/') && glob.StartsWith('*')) glob = "**/" + glob;

        var regex = new System.Text.StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                // "**/" may match nothing at all, so "**/x" also matches a bare "x" at the root.
                if (i + 2 < glob.Length && glob[i + 2] == '/') { regex.Append("(?:.*/)?"); i += 2; }
                else { regex.Append(".*"); i++; }
                continue;
            }
            regex.Append(c switch
            {
                '*' => "[^/]*",
                '?' => "[^/]",
                _ => System.Text.RegularExpressions.Regex.Escape(c.ToString()),
            });
        }
        regex.Append('$');
        try
        {
            return System.Text.RegularExpressions.Regex.IsMatch(path, regex.ToString(),
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }
        catch { return false; }
    }

    private static string Clamp(string text) =>
        text.Length <= MaxOutputChars ? text : text[..MaxOutputChars] + "\n… (output truncated)";

    private static string? Text(JsonObject input, string name) =>
        input[name] is JsonValue value
            ? value.TryGetValue<string>(out var text) ? text : value.ToString()
            : null;

    private static int? Int(JsonObject input, string name)
    {
        if (input[name] is not JsonValue value) return null;
        if (value.TryGetValue<int>(out var number)) return number;
        return int.TryParse(value.ToString(), out var parsed) ? parsed : null;
    }
}
