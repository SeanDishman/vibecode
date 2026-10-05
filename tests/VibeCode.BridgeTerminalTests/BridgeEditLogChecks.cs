using System.IO;
using System.Text.Json.Nodes;
using VibeCode.AgentStatus.Mcp;
using VibeCode.AgentStatus.Mcp.Bridge;
using VibeCode.Protocol;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    private static void VerifyBridgeEditLog()
    {
        VerifyEditLineMath();
        VerifyEditLedgerStorage();
        VerifyEditLogThroughBridge();
        VerifyShellEditCapture();
        VerifyCodexTurnDiffPaths();
    }

    /// <summary>Codex's turn diff names files relative to the git root; a project in a repo subfolder must still
    /// resolve them, and an edit a native fileChange already showed must not appear (or be logged) twice.</summary>
    private static void VerifyCodexTurnDiffPaths()
    {
        var repo = Path.Combine(_root, "repo-" + Guid.NewGuid().ToString("N"));
        var project = Path.Combine(repo, "apps", "web");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        Directory.CreateDirectory(Path.Combine(project, "src"));
        File.WriteAllText(Path.Combine(project, "src", "config.txt"), "A\nb\n");
        File.WriteAllText(Path.Combine(project, "src", "other.txt"), "x\nY\n");
        using var session = new CodexSession(new CodexSessionOptions { Cwd = project });
        Property(session, "SessionId", "diff-root");
        var messages = new List<JsonNode>();
        session.MessageReceived += m => messages.Add(m.DeepClone());
        ((HashSet<string>)typeof(CodexSession).GetField("_turnFileChangePaths", Flags)!.GetValue(session)!).Add(Path.Combine(project, "src", "config.txt"));
        Call(session, "CaptureTurnDiff", null, "diff --git a/apps/web/src/config.txt b/apps/web/src/config.txt\n--- a/apps/web/src/config.txt\n" +
            "+++ b/apps/web/src/config.txt\n@@ -1,2 +1,2 @@\n-a\n+A\n b\ndiff --git a/apps/web/src/other.txt b/apps/web/src/other.txt\n" +
            "--- a/apps/web/src/other.txt\n+++ b/apps/web/src/other.txt\n@@ -1,2 +1,2 @@\n x\n-y\n+Y\n");
        Call(session, "EmitTurnDiffTools", null, null);
        var cards = messages.Where(m => m["type"]?.ToString() == "assistant").Select(m => m["message"]!["content"]![0]!).ToArray();
        Check("Codex turn diffs from a repo root resolve inside a subfolder project and do not repeat native edits",
            cards.Length == 1 && cards[0]["name"]!.ToString() == "CodexEdit" && cards[0]["input"]!["file_path"]!.ToString() == Path.Combine(project, "src", "other.txt"));
    }

    /// <summary>A shell command's tool card opens, the "command" changes files on disk, then its result lands.</summary>
    private static void ShellCommand(ChatViewModel pane, string id, Action change, bool failed = false)
    {
        Call(pane, "IngestSdk", new JsonObject
        {
            ["type"] = "assistant",
            ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = new JsonArray(new JsonObject
                { ["type"] = "tool_use", ["id"] = id, ["name"] = "Bash", ["input"] = new JsonObject { ["command"] = "edit files" } }) },
        });
        change();
        Thread.Sleep(500); // the watcher reports changes on its own threads, before the provider's result arrives
        Call(pane, "IngestSdk", new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = new JsonArray(new JsonObject
                { ["type"] = "tool_result", ["tool_use_id"] = id, ["content"] = failed ? "exit code 1" : "done", ["is_error"] = failed }) },
        });
        if (!BridgeEditLedger.PendingWrites.Wait(10_000)) throw new TimeoutException("Edit log write did not finish.");
    }

    private static void VerifyShellEditCapture()
    {
        var (vm, team) = Team("shell-edits", 3);
        var root = team[0].Cwd;
        var src = Path.Combine(root, "src");
        Directory.CreateDirectory(src);
        var script = Path.Combine(src, "retry.ps1");
        var original = Enumerable.Range(1, 30).Select(i => $"$value{i} = {i}").ToList();
        File.WriteAllLines(script, original);
        File.WriteAllText(Path.Combine(src, "notes.md"), "# Notes\n\nfirst\n");
        File.WriteAllText(Path.Combine(src, "shared.md"), "one\ntwo\nthree\n");
        File.WriteAllText(Path.Combine(src, "legacy.txt"), "old\n");
        // Every real turn takes a pre-prompt snapshot; shell diffs start from it.
        foreach (var pane in team.Skip(1)) typeof(ChatViewModel).GetField("_activeRollback", Flags)!.SetValue(pane, TurnRollbackCheckpoint.Begin(root));
        BridgeEditEntry[] Logged(string file) => BridgeEditLedger.Read(root, DateTimeOffset.UtcNow).Where(e => e.File == file).ToArray();

        ShellCommand(team[1], "sed-1", () =>
        {
            var lines = original.ToList();
            lines[6] = "$value7 = 70";
            lines.Insert(20, "$extra = 1");
            File.WriteAllLines(script, lines);
        });
        var edited = Logged("src/retry.ps1");
        Check("a shell edit is logged with its author and exact lines", edited.Length == 1 && edited[0].AgentId == team[1].BridgeAgentId
            && edited[0].Tool == "Shell" && edited[0].Lines == "7, 21" && edited[0].Added == 2 && edited[0].Removed == 1);

        ShellCommand(team[1], "create-1", () =>
        {
            File.WriteAllText(Path.Combine(src, "new.ps1"), "a\nb\nc\n");
            Directory.CreateDirectory(Path.Combine(root, "bin"));
            File.WriteAllText(Path.Combine(root, "bin", "out.txt"), "build output");
            File.WriteAllText(Path.Combine(root, "build.log"), "log");
            Directory.CreateDirectory(Path.Combine(root, ".vibecode"));
            File.WriteAllText(Path.Combine(root, ".vibecode", "x.md"), "metadata");
            File.WriteAllText(Path.Combine(root, ".vibecode-bridge.md"), "board");
        });
        Check("a file created by a shell command is logged whole", Logged("src/new.ps1") is [{ Change: "create", Lines: "1-3" }]);
        Check("build output, logs, app metadata and the status board are not logged", Logged("bin/out.txt").Length == 0
            && Logged("build.log").Length == 0 && Logged(".vibecode/x.md").Length == 0 && Logged(".vibecode-bridge.md").Length == 0);

        ShellCommand(team[2], "rm-1", () => File.Delete(Path.Combine(src, "notes.md")), failed: true);
        Check("a deletion is logged even when the command then failed", Logged("src/notes.md") is [{ Change: "delete", Lines: "whole file deleted", Removed: 3 }]
            && Logged("src/notes.md")[0].AgentId == team[2].BridgeAgentId);

        // An edit tool then a shell command on the same file: each change is logged once, at its own lines.
        var current = File.ReadAllLines(script).ToList();
        current[2] = "$value3 = 300";
        File.WriteAllLines(script, current);
        IngestEdit(team[1], "edit-3", "Edit", new() { ["file_path"] = script, ["old_string"] = "$value3 = 3", ["new_string"] = "$value3 = 300" },
            new() { ["structuredPatch"] = new JsonArray(Hunk(2, " $value2 = 2", "-$value3 = 3", "+$value3 = 300", " $value4 = 4")) });
        ShellCommand(team[1], "sed-2", () =>
        {
            Thread.Sleep(400); // a model takes far longer than this to issue its next command
            var lines = File.ReadAllLines(script).ToList();
            lines[8] = "$value9 = 900";
            File.WriteAllLines(script, lines);
        });
        Check("an edit tool's change is not repeated by the next shell diff", Logged("src/retry.ps1").Select(e => (e.Tool, e.Lines))
            .SequenceEqual([("Shell", "9"), ("Edit", "3"), ("Shell", "7, 21")]));

        // Replayed history never logs, even if files change meanwhile.
        foreach (var historic in new[] { "assistant", "user" })
        {
            Call(team[2], "IngestMessagePayload", historic, historic == "assistant"
                ? new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_use", ["id"] = "old-bash", ["name"] = "Bash", ["input"] = new JsonObject { ["command"] = "x" } }) }
                : new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = "old-bash", ["content"] = "ok" }) },
                null, false, null);
            if (historic == "assistant") { File.WriteAllText(Path.Combine(src, "replayed.md"), "x"); Thread.Sleep(500); }
        }
        BridgeEditLedger.PendingWrites.Wait(10_000);
        Check("replayed shell history logs nothing", Logged("src/replayed.md").Length == 0);
        Thread.Sleep(2000); // let that unattributed write age out of the next command's look-back

        // Agent 3 runs a long command while Agent 2 edits with a quick one and with an edit tool.
        Call(team[2], "IngestSdk", new JsonObject { ["type"] = "assistant", ["message"] = new JsonObject { ["role"] = "assistant",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_use", ["id"] = "long-build", ["name"] = "Bash", ["input"] = new JsonObject { ["command"] = "dotnet test" } }) } });
        ShellCommand(team[1], "sed-3", () => File.WriteAllText(Path.Combine(src, "shared.md"), "one\nTWO\nthree\n"));
        current = File.ReadAllLines(script).ToList();
        current[29] = "$value30 = 3000";
        File.WriteAllLines(script, current);
        IngestEdit(team[1], "edit-30", "Edit", new() { ["file_path"] = script, ["old_string"] = "$value30 = 30", ["new_string"] = "$value30 = 3000" },
            new() { ["structuredPatch"] = new JsonArray(Hunk(30, "-$value30 = 30", "+$value30 = 3000")) });
        Thread.Sleep(500);
        Call(team[2], "IngestSdk", new JsonObject { ["type"] = "user", ["message"] = new JsonObject { ["role"] = "user",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = "long-build", ["content"] = "Passed" }) } });
        BridgeEditLedger.PendingWrites.Wait(10_000);
        Check("the quick command that finished first gets the change, marked approximate because another command was running",
            Logged("src/shared.md") is [{ Lines: "about 2", Exact: false }] && Logged("src/shared.md")[0].AgentId == team[1].BridgeAgentId);
        Check("a peer's long command does not take credit for edits made while it ran", BridgeEditLedger.Read(root, DateTimeOffset.UtcNow)
            .All(e => e.AgentId != team[2].BridgeAgentId || e.File == "src/notes.md"));

        ShellCommand(team[0], "no-snapshot", () =>
        {
            File.AppendAllText(Path.Combine(src, "new.ps1"), "d\n");
            File.AppendAllText(Path.Combine(src, "legacy.txt"), "appended\n");
        });
        Check("without a snapshot a remembered file still gets exact lines, and an unseen one is logged with lines unknown",
            BridgeEditLedger.Read(root, DateTimeOffset.UtcNow).Where(e => e.AgentId == team[0].BridgeAgentId).OrderBy(e => e.File)
                .Select(e => (e.File, e.Lines)).SequenceEqual([("src/legacy.txt", "unknown"), ("src/new.ps1", "4")]));
    }

    private static BridgeEditChange? Analyze(string tool, JsonObject input, JsonObject? result = null, string? file = null) =>
        BridgeEditDiff.Analyze(new BridgeEditObservation(tool, input, result, @"C:\work"), _ => file);

    private static JsonObject Hunk(int newStart, params string[] lines) => new()
        { ["oldStart"] = newStart, ["oldLines"] = 0, ["newStart"] = newStart, ["newLines"] = 0, ["lines"] = new JsonArray(lines.Select(l => (JsonNode?)l).ToArray()) };

    private static void VerifyEditLineMath()
    {
        var claude = Analyze("Edit", new() { ["file_path"] = @"src\a.ts", ["old_string"] = "c", ["new_string"] = "C\nD" },
            new() { ["structuredPatch"] = new JsonArray(Hunk(10, " a", " b", "-c", "+C", "+D", " e")) })!;
        Check("Claude structuredPatch gives the exact changed lines", claude.Ranges.Single() == new BridgeEditRange(12, 13, 2, 1)
            && claude.Exact && claude.Added == 2 && claude.Removed == 1 && claude.FullPath == @"C:\work\src\a.ts"
            && BridgeEditDiff.Describe(claude.Change, claude.Ranges, claude.Exact) == "12-13");
        var deletion = Analyze("Edit", new() { ["file_path"] = "a.ts" },
            new() { ["structuredPatch"] = new JsonArray(Hunk(5, " a", "-b", "-c", " d"), Hunk(40, "+x", " y")) })!;
        Check("a pure deletion is pinned below the line above it", deletion.Ranges[0] == new BridgeEditRange(5, 5, 0, 2)
            && BridgeEditDiff.Describe(deletion.Change, deletion.Ranges, deletion.Exact) == "after 5 (2 deleted), 40");
        Check("deletion ranges match queries on either side of the gap", deletion.Ranges[0].Overlaps(6, 9) && deletion.Ranges[0].Overlaps(1, 5) && !deletion.Ranges[0].Overlaps(7, 9));
        var created = Analyze("Write", new() { ["file_path"] = "n.ts", ["content"] = "1\n2\n3\n" },
            new() { ["type"] = "create", ["structuredPatch"] = new JsonArray(), ["content"] = "1\n2\n3\n" })!;
        Check("Claude Write create covers the whole new file", created.Change == "create" && created.Ranges.Single() == new BridgeEditRange(1, 3, 3, 0));
        Check("an edit that changed nothing is not recorded", Analyze("Write", new() { ["file_path"] = "n.ts", ["content"] = "x" },
            new() { ["type"] = "update", ["structuredPatch"] = new JsonArray() }) is null);

        var numbered = Analyze("CodexEdit", new()
        {
            ["file_path"] = "b.cs", ["kind"] = "update", ["diff_format"] = "unified",
            ["diff"] = "--- a/b.cs\n+++ b/b.cs\n@@ -1,3 +1,4 @@\n one\n+two\n three\n four\n@@ -20,2 +21,2 @@\n-old\n+new\n same\n",
        })!;
        Check("Codex numbered unified diffs are exact", numbered.Exact && numbered.Ranges.SequenceEqual([new BridgeEditRange(2, 2, 1, 0), new BridgeEditRange(21, 21, 1, 1)]));
        var applyPatch = Analyze("CodexEdit", new()
            { ["file_path"] = "c.cs", ["kind"] = "update", ["diff_format"] = "unified", ["diff"] = "@@ beta\n beta\n-gamma\n+GAMMA\n delta" },
            file: "alpha\r\nbeta\r\nGAMMA\r\ndelta\r\nepsilon\r\n")!;
        Check("apply_patch hunks without numbers are located in the edited file", applyPatch.Exact && applyPatch.Ranges.Single() == new BridgeEditRange(3, 3, 1, 1));
        var unlocated = Analyze("CodexEdit", new() { ["file_path"] = "c.cs", ["kind"] = "update", ["diff"] = "@@\n-gone\n+here\n" }, file: "something else\n")!;
        Check("an unlocatable hunk keeps its counts but claims no lines", !unlocated.Exact && unlocated.Ranges.Count == 0 && unlocated.Added == 1
            && BridgeEditDiff.Describe(unlocated.Change, unlocated.Ranges, unlocated.Exact) == "unknown");
        var added = Analyze("CodexEdit", new() { ["file_path"] = "d.cs", ["kind"] = "add", ["diff_format"] = "content", ["diff"] = "a\nb\n" })!;
        var removed = Analyze("CodexEdit", new() { ["file_path"] = "d.cs", ["kind"] = "delete", ["diff_format"] = "content", ["diff"] = "a\nb\nc" })!;
        var turnDelete = Analyze("CodexEdit", new() { ["file_path"] = "e.cs", ["kind"] = "delete", ["diff_format"] = "unified",
            ["diff"] = "diff --git a/e.cs b/e.cs\ndeleted file mode 100644\n--- a/e.cs\n+++ /dev/null\n@@ -1,2 +0,0 @@\n-a\n-b" })!;
        Check("Codex file creation and deletion are recorded as such", added.Change == "create" && added.Ranges.Single() == new BridgeEditRange(1, 2, 2, 0)
            && removed.Change == "delete" && removed.Removed == 3 && turnDelete.Change == "delete" && turnDelete.Removed == 2
            && BridgeEditDiff.Describe(removed.Change, removed.Ranges, removed.Exact) == "whole file deleted");

        const string source = "one\ntwo\n    var retries = 5;\nfoo();\nx = 1;\nfoo();\nx = 1;\n";
        var partial = Analyze("Edit", new() { ["file_path"] = "r.ts", ["old_string"] = "retries = 3", ["new_string"] = "retries = 5" }, file: source)!;
        Check("snippet edits (Kimi, Grok, GLM) find a partial-line change in the edited file", partial.Exact && partial.Ranges.Single() == new BridgeEditRange(3, 3, 1, 1));
        var ambiguous = Analyze("Edit", new() { ["file_path"] = "r.ts", ["old_string"] = "x = 0;", ["new_string"] = "x = 1;" }, file: source)!;
        Check("a snippet that appears twice is marked approximate", !ambiguous.Exact && ambiguous.Ranges.Single().Start == 5
            && BridgeEditDiff.Describe(ambiguous.Change, ambiguous.Ranges, ambiguous.Exact) == "about 5");
        var everywhere = Analyze("Edit", new() { ["file_path"] = "r.ts", ["old_string"] = "bar();", ["new_string"] = "foo();", ["replace_all"] = true }, file: source)!;
        Check("replace_all records every replaced occurrence", everywhere.Exact && everywhere.Ranges.Select(r => r.Start).SequenceEqual([4, 6]) && everywhere.Added == 2);
        var multi = Analyze("MultiEdit", new() { ["file_path"] = "r.ts", ["edits"] = new JsonArray(
            new JsonObject { ["old_string"] = "x = 0;\nfoo();", ["new_string"] = "x = 1;\nfoo();" },
            new JsonObject { ["old_string"] = "TWO", ["new_string"] = "two" }) }, file: source.Replace("x = 1;\nfoo();\nx = 1;", "x = 1;\nfoo();\nx = 2;"))!;
        Check("MultiEdit records each edit at its own line", multi.Ranges.Select(r => r.Start).SequenceEqual([2, 5]));
        var wholeFile = Analyze("Edit", new() { ["file_path"] = "r.ts", ["old_string"] = "one\ntwo\n", ["new_string"] = source }, file: source)!;
        Check("ACP whole-file old/new text maps straight onto file lines", wholeFile.Exact && wholeFile.Ranges.Single() == new BridgeEditRange(3, 7, 5, 0));
        var blank = Analyze("Edit", new() { ["file_path"] = "r.ts", ["old_string"] = "foo();\n", ["new_string"] = "" }, file: source)!;
        Check("a deletion snippet that cannot be located says unknown instead of guessing", !blank.Exact && blank.Ranges.Count == 0 && blank.Removed == 1);
        Check("tools that do not edit files are ignored", Analyze("Bash", new() { ["command"] = "ls" }) is null && Analyze("Edit", new() { ["old_string"] = "a" }) is null);
    }

    private static void VerifyEditLedgerStorage()
    {
        var workspace = Path.Combine(_root, "edit-ledger-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        var directory = BridgeEditLedger.DirectoryFor(workspace);
        BridgeEditEntry Entry(DateTimeOffset at, string file, string agent = "a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1") => new()
        {
            Id = Guid.NewGuid().ToString("N")[..12], At = at, AgentId = agent, Agent = 1, Label = "Codex 1", Provider = "codex",
            File = file, Ranges = [new BridgeEditRange(1, 2, 2, 0)], Added = 2, Tool = "Edit",
        };
        string Day(string date) => Path.Combine(directory, "edits-" + date + ".jsonl");

        var fixedNow = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
        BridgeEditLedger.Append(workspace, Entry(fixedNow.AddHours(-1), "src/a.ts"));
        BridgeEditLedger.Append(workspace, Entry(fixedNow.AddMinutes(-5), "src/b.ts"));
        Check("the log lives in the project's hidden .vibecode folder and is git-ignored",
            directory == Path.Combine(workspace, ".vibecode", "bridge-edits") && File.ReadAllText(Path.Combine(directory, ".gitignore")).Trim() == "*"
            && File.ReadAllText(Path.Combine(directory, "README.md")).Contains("bridge_file_edits"));
        var line = JsonNode.Parse(File.ReadLines(Day("2026-10-04")).First())!;
        Check("each line is one self-describing JSON edit", line["agent_id"]?.ToString() == "a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1"
            && line["file"]?.ToString() == "src/a.ts" && line["ranges"]?[0]?["start"]?.GetValue<int>() == 1 && line["ranges"]?[0]?["deletion_only"] is null);
        var read = BridgeEditLedger.Read(workspace, fixedNow);
        Check("entries read back newest first", read.Select(e => e.File).SequenceEqual(["src/b.ts", "src/a.ts"]));

        BridgeEditLedger.Append(workspace, Entry(new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero), "old/9-days.ts"));
        BridgeEditLedger.Append(workspace, Entry(new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero), "old/8-days.ts"));
        BridgeEditLedger.Append(workspace, Entry(new DateTimeOffset(2026, 9, 27, 6, 0, 0, TimeSpan.Zero), "old/7-days-6-hours.ts"));
        BridgeEditLedger.Append(workspace, Entry(new DateTimeOffset(2026, 9, 27, 18, 0, 0, TimeSpan.Zero), "old/6-days-18-hours.ts"));
        File.WriteAllText(Path.Combine(directory, "notes.txt"), "unrelated");
        File.WriteAllText(Path.Combine(directory, "edits-2020-01-01.jsonl.bak"), "unrelated");
        File.AppendAllText(Day("2026-10-04"), "{torn line\n" + new string('x', 70_000) + "\n");
        var visible = BridgeEditLedger.Read(workspace, fixedNow).Select(e => e.File).ToArray();
        Check("entries older than seven days are never served", visible.SequenceEqual(["src/b.ts", "src/a.ts", "old/6-days-18-hours.ts"]));
        Check("torn or oversized lines never hide the rest of a day", visible.Contains("src/a.ts"));
        Check("cleanup deletes day files whose entries are all older than seven days", BridgeEditLedger.CleanupExpired(workspace, fixedNow) == 2
            && !File.Exists(Day("2026-09-25")) && !File.Exists(Day("2026-09-26")));
        Check("cleanup keeps a day that still has fresh entries, and anything it does not own", File.Exists(Day("2026-09-27")) && File.Exists(Day("2026-10-04"))
            && File.Exists(Path.Combine(directory, "notes.txt")) && File.Exists(Path.Combine(directory, "edits-2020-01-01.jsonl.bak"))
            && File.Exists(Path.Combine(directory, ".gitignore")) && File.Exists(Path.Combine(directory, "README.md")));
        var nextDay = fixedNow.AddDays(1);
        Check("a day file goes once its last entry passes seven days", BridgeEditLedger.CleanupExpired(workspace, nextDay) == 1 && !File.Exists(Day("2026-09-27"))
            && BridgeEditLedger.Read(workspace, nextDay).All(e => e.At >= nextDay.AddDays(-7)));
        var eightDaysLater = fixedNow.AddDays(8);
        Check("after a week of silence the whole log is cleaned out", BridgeEditLedger.CleanupExpired(workspace, eightDaysLater) == 1
            && BridgeEditLedger.Read(workspace, eightDaysLater).Count == 0 && File.Exists(Path.Combine(directory, "notes.txt")));

        var full = Day("2026-10-02");
        File.WriteAllText(full, new string(' ', (int)BridgeEditLedger.MaxDayFileBytes) + "\n");
        BridgeEditLedger.Append(workspace, Entry(new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero), "capped.ts"));
        Check("a runaway day stops growing at its size cap", new FileInfo(full).Length == BridgeEditLedger.MaxDayFileBytes + 1);
        File.Delete(full);

        var concurrent = Path.Combine(_root, "edit-ledger-parallel-" + Guid.NewGuid().ToString("N"));
        var now = DateTimeOffset.UtcNow;
        Parallel.For(0, 8, worker => { for (var i = 0; i < 50; i++) BridgeEditLedger.Append(concurrent, Entry(now, $"w{worker}/{i}.ts")); });
        Check("parallel appends keep all 400 entries intact", BridgeEditLedger.Read(concurrent, now.AddSeconds(1)).Select(e => e.File).Distinct().Count() == 400);

        var author = new BridgeEditAuthor("b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2", 2, "Claude 2", "claude", "claude-opus-5-5", "worker", "Fix login", "run1");
        var outside = Path.Combine(Path.GetTempPath(), "vibecode-outside-" + Guid.NewGuid().ToString("N") + ".txt");
        foreach (var path in new[] { Path.Combine(workspace, "src", "c.ts"), Path.Combine(workspace, ".vibecode-bridge.md"),
                     Path.Combine(workspace, ".vibecode", "bridge-messages", "x.md"), outside })
            BridgeEditLedger.Record(workspace, author, new BridgeEditObservation("Write", new() { ["file_path"] = path, ["content"] = "a\nb" }, null, workspace));
        BridgeEditLedger.PendingWrites.Wait(10_000);
        var recorded = BridgeEditLedger.Read(workspace, DateTimeOffset.UtcNow);
        Check("recording stores project-relative paths and skips the status board and app metadata",
            recorded.Count(e => e.AgentId == author.AgentId) == 2 && recorded.Any(e => e.File == "src/c.ts" && e.Role == "worker" && e.Task == "Fix login" && e.Lines == "1-2")
            && recorded.Any(e => e.File == outside.Replace('\\', '/')) && BridgeEditLedger.LastError is null);
    }

    private static void IngestEdit(ChatViewModel pane, string id, string tool, JsonObject input, JsonObject? toolUseResult = null, bool failed = false)
    {
        Call(pane, "IngestSdk", new JsonObject
        {
            ["type"] = "assistant",
            ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = new JsonArray(new JsonObject
                { ["type"] = "tool_use", ["id"] = id, ["name"] = tool, ["input"] = input }) },
        });
        var user = new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = new JsonArray(new JsonObject
                { ["type"] = "tool_result", ["tool_use_id"] = id, ["content"] = failed ? "String to replace not found" : "ok", ["is_error"] = failed }) },
        };
        if (toolUseResult is not null) user["tool_use_result"] = toolUseResult;
        Call(pane, "IngestSdk", user);
        if (!BridgeEditLedger.PendingWrites.Wait(10_000)) throw new TimeoutException("Edit log write did not finish.");
    }

    private static void VerifyEditLogThroughBridge()
    {
        var (vm, team) = Team("edit-log", 3);
        var root = team[0].Cwd;
        var app = Path.Combine(root, "src", "app.ts");
        Directory.CreateDirectory(Path.GetDirectoryName(app)!);
        var lines = Enumerable.Range(1, 40).Select(i => $"line {i};").ToArray();
        lines[29] = "const retries = 5;";
        lines[34] = "const delay = 250;";
        File.WriteAllLines(app, lines);
        Property(team[1], "BridgeTaskName", "Tune retry backoff");

        IngestEdit(team[1], "claude-1", "Edit", new() { ["file_path"] = app, ["old_string"] = "line 12;", ["new_string"] = "line 12;\nline 12b;" },
            new() { ["filePath"] = app, ["structuredPatch"] = new JsonArray(Hunk(10, " line 10;", " line 11;", "-line 12;", "+line twelve;", "+line 12b;", " line 13;")) });
        IngestEdit(team[2], "codex-1", "CodexEdit", new() { ["file_path"] = "src/app.ts", ["kind"] = "update", ["diff_format"] = "unified",
            ["diff"] = "@@ -21,1 +21,1 @@\n-line 21;\n+line twenty-one;\n" });
        IngestEdit(team[2], "codex-failed", "CodexEdit", new() { ["file_path"] = "src/app.ts", ["kind"] = "update", ["diff"] = "@@ -2,1 +2,1 @@\n-a\n+b\n" }, failed: true);
        // Two back-to-back snippet edits to one file share a transcript card; each must still be logged with its own lines.
        IngestEdit(team[1], "kimi-1", "Edit", new() { ["file_path"] = app, ["old_string"] = "const retries = 3;", ["new_string"] = "const retries = 5;" });
        IngestEdit(team[1], "kimi-2", "Edit", new() { ["file_path"] = app, ["old_string"] = "const delay = 100;", ["new_string"] = "const delay = 250;" });
        var folded = team[1].Items.OfType<CompactToolGroupItem>().SelectMany(g => g.Tools).Where(t => t.Name == "Edit").ToArray();
        Check("three back-to-back edits to one file fold into one transcript card", folded.Length == 1 && folded[0].EditCount == 3);
        IngestEdit(team[0], "board", "Edit", new() { ["file_path"] = Path.Combine(root, ".vibecode-bridge.md"), ["old_string"] = "a", ["new_string"] = "b" },
            new() { ["structuredPatch"] = new JsonArray(Hunk(3, "-a", "+b")) });
        foreach (var historic in new[] { "assistant", "user" })
            Call(team[2], "IngestMessagePayload", historic, historic == "assistant"
                ? new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_use", ["id"] = "replayed", ["name"] = "Write", ["input"] = new JsonObject { ["file_path"] = app, ["content"] = "x" } }) }
                : new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = "replayed", ["content"] = "ok" }) },
                null, false, null);
        var solo = new ChatViewModel(root, provider: "codex");
        typeof(ChatViewModel).GetField("_session", Flags)!.SetValue(solo, new FakeSession());
        Chats.Add(solo); vm.Chats.Add(solo); Call(vm, "Track", solo);
        IngestEdit(solo, "solo-1", "Write", new() { ["file_path"] = Path.Combine(root, "solo.txt"), ["content"] = "solo" });
        Property(team[0], "IsBridgeManager", true);
        Property(team[2], "BridgeCoordinatorAgentId", team[0].BridgeAgentId);
        Property(team[2], "BridgeTaskName", "Wire config loader");
        IngestEdit(team[2], "worker-1", "Write", new() { ["file_path"] = Path.Combine(root, "src", "config.ts"), ["content"] = "export const a = 1;\nexport const b = 2;\n" });

        var all = Tool(team[0], "bridge_file_edits");
        var edits = all["edits"]!.AsArray();
        Check("only successful live edits by bridge panes are logged", all["matched"]!.GetValue<int>() == 5
            && edits.All(e => e!["file"]!.ToString() is "src/app.ts" or "src/config.ts"));
        Check("newest edit comes first with its author, role and task", edits[0]!["file"]!.ToString() == "src/config.ts" && edits[0]!["agent"]!.ToString() == "Codex 3"
            && edits[0]!["role"]!.ToString() == "worker" && edits[0]!["task"]!.ToString() == "Wire config loader" && edits[0]!["change"]!.ToString() == "write"
            && edits[0]!["lines"]!.ToString() == "1-2" && edits[0]!["in_your_bridge"]!.GetValue<bool>() && !edits[0]!["you"]!.GetValue<bool>());
        Check("each logged edit keeps its own lines even when cards are folded", edits.Select(e => e!["lines"]!.ToString()).SequenceEqual(["1-2", "35", "30", "21", "12-13"])
            && edits.All(e => e!["lines_exact"]!.GetValue<bool>()));
        Check("an overview lists files with the agents that touched them", all["files"]!.AsArray().First()!["file"]!.ToString() == "src/config.ts"
            && all["files"]!.AsArray().Single(f => f!["file"]!.ToString() == "src/app.ts")!["agents"]!.AsArray().Select(a => a!.ToString()).SequenceEqual(["Codex 2", "Codex 3"]));
        var lineQuery = Tool(team[0], "bridge_file_edits", new() { ["path"] = "app.ts", ["start_line"] = 20, ["end_line"] = 22 });
        Check("asking about specific lines returns only the edits that touched them", lineQuery["matched"]!.GetValue<int>() == 1
            && lineQuery["edits"]![0]!["agent"]!.ToString() == "Codex 3" && lineQuery["files"] is null);
        Check("an absolute or folder path finds the same file", Tool(team[0], "bridge_file_edits", new() { ["path"] = app })["matched"]!.GetValue<int>() == 4
            && Tool(team[0], "bridge_file_edits", new() { ["path"] = "src/" })["matched"]!.GetValue<int>() == 5
            && Tool(team[0], "bridge_file_edits", new() { ["path"] = "missing.ts" })["note"]!.ToString().StartsWith("No recorded edits match"));
        var mine = Tool(team[1], "bridge_file_edits", new() { ["agent"] = "me" });
        Check("agent=me returns only the caller's own edits", mine["matched"]!.GetValue<int>() == 3 && mine["edits"]!.AsArray().All(e => e!["you"]!.GetValue<bool>()));
        var recipient = Tool(team[0], "bridge_list_agents")["agents"]!.AsArray().Single(a => a!["agent_id"]!.ToString() == team[2].BridgeAgentId)!["message_recipient"]!.ToString();
        Check("agent accepts a roster number, label, message_recipient or agent_id", new[] { "3", "Agent 3", "#3", "Codex 3", recipient, team[2].BridgeAgentId }
            .All(agent => Tool(team[0], "bridge_file_edits", new() { ["agent"] = agent })["matched"]!.GetValue<int>() == 2));
        var page = Tool(team[0], "bridge_file_edits", new() { ["limit"] = 2, ["since_minutes"] = 60 });
        Check("limit pages the newest edits", page["returned"]!.GetValue<int>() == 2 && page["has_more"]!.GetValue<bool>());
        Reject("start_line needs a path", () => Tool(team[0], "bridge_file_edits", new() { ["start_line"] = 3 }));
        Reject("an agent outside the bridge is reported, not silently empty", () => Tool(team[0], "bridge_file_edits", new() { ["agent"] = "Agent 9" }));
        Reject("unknown arguments are rejected", () => Tool(team[0], "bridge_file_edits", new() { ["file"] = "app.ts" }));
        Reject("limit stays within 1 to 100", () => Tool(team[0], "bridge_file_edits", new() { ["limit"] = 101 }));

        var roster = Tool(team[0], "bridge_list_agents")["agents"]!.AsArray();
        var recent = roster.Single(a => a!["agent_id"]!.ToString() == team[1].BridgeAgentId)!["recent_edits"]!.AsArray().Select(r => r!.ToString()).ToArray();
        Check("the roster shows each agent's recent files and lines", recent.SequenceEqual(["src/app.ts: 35 (just now)"])
            && roster.Single(a => a!["agent_id"]!.ToString() == team[0].BridgeAgentId)!["recent_edits"]!.AsArray().Count == 0);

        var pipe = (string)Property(Call(team[0], "EnsureBridgeMcp")!, "PipeName")!;
        var request = Task.Run(() => BridgeMcpClient.InvokeAsync(pipe, "bridge_file_edits", new() { ["path"] = "src/config.ts" }));
        PumpUntil(() => request.IsCompleted);
        Check("the real named-pipe MCP endpoint serves the edit log", request.GetAwaiter().GetResult()["edits"]![0]!["task"]!.ToString() == "Wire config loader");

        var host = new AgentStatusMcpHost(BridgeMcpTools.Create((_, _) => new JsonObject()), serverName: "vibecode-bridge", instructions: BridgeMcpTools.Instructions);
        host.Dispatch(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "initialize", ["params"] = new JsonObject
            { ["protocolVersion"] = "2025-11-25", ["capabilities"] = new JsonObject(), ["clientInfo"] = new JsonObject { ["name"] = "fixture", ["version"] = "1" } } });
        host.Dispatch(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" });
        var definition = host.Dispatch(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = "tools/list" })!["result"]!["tools"]!.AsArray()
            .Single(t => t!["name"]!.ToString() == "bridge_file_edits")!;
        Check("MCP advertises bridge_file_edits as a read-only tool with optional filters", definition["annotations"]!["readOnlyHint"]!.GetValue<bool>()
            && definition["inputSchema"]!["properties"]!.AsObject().Select(p => p.Key).SequenceEqual(["path", "agent", "start_line", "end_line", "since_minutes", "limit"])
            && definition["inputSchema"]!["required"]!.AsArray().Count == 0 && BridgeMcpTools.Instructions.Contains("bridge_file_edits"));
        Check("every provider receives the tool", McpCatalog.BuildCodexProjection([(McpServerDefinition)Call(Call(team[0], "EnsureBridgeMcp")!, "Registration")!])
            .ConfigOverrides.Any(c => c.Contains("bridge_file_edits")) && ((JsonArray)Call(new GlmSession(new GlmSessionOptions
            { Cwd = root, ApiKeys = ["offline-fixture"], BridgeMcpPipe = pipe }), "ToolSchema")!).Any(t => t?["function"]?["name"]?.ToString() == "bridge_file_edits"));
        Check("orchestrators are told how to review a worker's lines", BridgeOrchestratorPolicy.Instructions.Contains("bridge_file_edits(agent=<worker>)"));

        vm.RemoveBridgePane(team[1]);
        var afterLeave = Tool(team[0], "bridge_file_edits", new() { ["path"] = "src/app.ts" })["edits"]!.AsArray();
        Check("a departed agent's edits stay attributed but leave the bridge", afterLeave.Where(e => e!["agent_id"]!.ToString() == team[1].BridgeAgentId)
            .All(e => !e!["in_your_bridge"]!.GetValue<bool>() && e["message_recipient"] is null && e["agent"]!.ToString() == "Codex 2"));
        Check("a renumbered agent is shown by its current label", afterLeave.Where(e => e!["agent_id"]!.ToString() == team[2].BridgeAgentId)
            .All(e => e!["agent"]!.ToString() == team[2].BridgeLabel && e["agent_number"]!.GetValue<int>() == 2));
        Check("departed agents can still be filtered by their full agent_id", Tool(team[0], "bridge_file_edits", new() { ["agent"] = team[1].BridgeAgentId })["matched"]!.GetValue<int>() == 3);
    }
}
