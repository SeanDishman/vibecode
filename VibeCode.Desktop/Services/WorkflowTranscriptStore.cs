using System.IO;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace VibeCode.Services;

/// <summary>
/// Reads a running Workflow's own on-disk record of its subagents.
///
/// ============================ WHY THIS IS THE ONLY WAY TO SEE INSIDE A WORKFLOW ============================
/// An ordinary Agent/Task subagent streams its whole conversation to us: its assistant messages arrive on the
/// session stream carrying <c>parent_tool_use_id</c>, which is how the Agent tool card gets a live child
/// transcript. A Workflow's agents do NOT. Verified against a real run on this machine
/// (projects/&lt;project&gt;/&lt;session&gt;/…): the parent session transcript contained 455 assistant messages and
/// ZERO sidechain rows, while the workflow's six agents had produced 137, 139, 140, 103, 93 and 110 rows each —
/// none of which ever reached the session stream. All the app was ever given about them is
/// <c>task_progress.workflow_progress</c>: a label, a state, and counters.
///
/// What the CLI does instead is hand us a path, in the launch result's "Transcript dir:" line
/// (see <see cref="DirectoryFromToolResult"/> — reading that line is load-bearing for everything below).
/// That directory holds the real thing:
///
///     journal.jsonl              {"type":"started","key":"v2:…","agentId":"a0aa…"}
///                                {"type":"result","key":"v2:…","agentId":"a0aa…","result":{…}}
///     agent-&lt;agentId&gt;.jsonl      the agent's FULL transcript, same envelope shape as a session transcript —
///                                measured on one agent: 31 thinking blocks, 49 tool_use, 49 tool_result, 5 text
///     agent-&lt;agentId&gt;.meta.json  {"agentType":"workflow-subagent","spawnDepth":1}
///
/// So this class does two jobs the stream cannot do: it says which agents have actually FINISHED (a journal
/// <c>result</c> row), and it hands back their messages so the inspector can show what they were thinking.
/// ===========================================================================================================
///
/// Everything here is best effort and non-throwing: the directory belongs to another process that is writing to
/// it, and a workflow whose files we cannot read must degrade to the old behaviour, never break the chat.
/// </summary>
public sealed class WorkflowTranscriptStore
{
    public string Dir { get; }

    private readonly object _gate = new();
    private readonly List<string> _started = [];                 // agent ids, in the order the run created them
    private readonly HashSet<string> _finished = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _offsets = new(StringComparer.Ordinal);
    private long _journalOffset;

    public WorkflowTranscriptStore(string dir) => Dir = dir;

    /// <summary>
    /// Agent ids in the order the journal recorded them starting.
    ///
    /// NOT the order the script lists them in, and never treat it as one: a run is appended to as each agent wins a
    /// concurrency slot, so a six-agent <c>parallel()</c> here started 3,1,4,2,6,5. Which row an id belongs to comes
    /// from the run's own progress events, not from a position. This is here to describe what has begun.
    /// </summary>
    public IReadOnlyList<string> StartedOrder { get { lock (_gate) return _started.ToArray(); } }

    public bool IsFinished(string agentId) { lock (_gate) return _finished.Contains(agentId); }

    public bool AllFinished
    {
        get { lock (_gate) return _started.Count > 0 && _finished.Count >= _started.Count; }
    }

    /// <summary>Re-read whatever the journal has gained since last time. Returns true if anything changed, so a
    /// caller can skip the work of re-deriving state on an idle tick.</summary>
    public bool RefreshJournal()
    {
        var path = Path.Combine(Dir, "journal.jsonl");
        var changed = false;
        foreach (var node in ReadNewLines(path, ref _journalOffset))
        {
            var agentId = node["agentId"]?.GetValue<string>();
            if (string.IsNullOrEmpty(agentId)) continue;
            lock (_gate)
            {
                switch (node["type"]?.GetValue<string>())
                {
                    case "started":
                        if (!_started.Contains(agentId)) { _started.Add(agentId); changed = true; }
                        break;
                    case "result":
                        if (!_started.Contains(agentId)) _started.Add(agentId);
                        if (_finished.Add(agentId)) changed = true;
                        break;
                }
            }
        }
        return changed;
    }

    /// <summary>The agent's messages that have appeared since the last call. First call returns the whole file,
    /// so opening the inspector on an agent that has been running for a while shows all of it.</summary>
    public IReadOnlyList<JsonObject> ReadNewMessages(string agentId)
    {
        var path = Path.Combine(Dir, $"agent-{agentId}.jsonl");
        long offset;
        lock (_gate) _offsets.TryGetValue(agentId, out offset);
        var rows = ReadNewLines(path, ref offset);
        lock (_gate) _offsets[agentId] = offset;
        return rows;
    }

    /// <summary>Forget how far we have read, so the next call replays the agent from the beginning.</summary>
    public void Rewind(string agentId) { lock (_gate) _offsets.Remove(agentId); }

    /// <summary>
    /// Read whole JSON lines added since <paramref name="offset"/> and advance it past the last COMPLETE one.
    ///
    /// The offset stops at the last newline rather than at the end of the file on purpose: the workflow appends to
    /// these files while we read, so the tail is regularly a half-written line. Advancing past it would drop that
    /// message for good once the rest of it landed.
    /// </summary>
    private static List<JsonObject> ReadNewLines(string path, ref long offset)
    {
        var rows = new List<JsonObject>();
        try
        {
            if (!File.Exists(path)) return rows;
            // ReadWrite share: the CLI holds these files open for appending, and anything less fails outright.
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < offset) offset = 0;             // truncated/replaced (a resumed run): start over
            if (fs.Length == offset) return rows;
            fs.Position = offset;
            using var reader = new StreamReader(fs, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
            var consumed = offset;
            while (reader.ReadLine() is { } line)
            {
                // A line the reader returns without a trailing newline is the file's incomplete tail.
                var complete = reader.Peek() >= 0 || EndsWithNewline(fs);
                if (!complete) break;
                consumed += System.Text.Encoding.UTF8.GetByteCount(line) + 1;
                if (line.Length == 0) continue;
                try { if (JsonNode.Parse(line) is JsonObject obj) rows.Add(obj); }
                catch { /* a torn line: skip it rather than lose the whole poll */ }
            }
            offset = consumed;
        }
        catch (IOException) { /* mid-write lock contention; the next tick picks it up */ }
        catch (UnauthorizedAccessException) { /* not ours to read */ }
        return rows;
    }

    private static bool EndsWithNewline(FileStream fs)
    {
        try
        {
            if (fs.Length == 0) return false;
            var saved = fs.Position;
            fs.Position = fs.Length - 1;
            var last = fs.ReadByte();
            fs.Position = saved;
            return last is '\n' or '\r';
        }
        catch { return false; }
    }

    /// <summary>
    /// Pull the transcript directory out of a Workflow tool_result.
    ///
    /// A launch result is PLAIN TEXT. Verbatim, from a real run on this machine:
    ///
    ///     Workflow launched in background. Task ID: ww9jkqkri
    ///     Summary: Build the MAINFRAME news ingestion backend: …
    ///     Transcript dir: C:\…\subagents\workflows\wf_614c73a3-b4a
    ///     Script file: C:\…\workflows\scripts\mainframe-news-backend-wf_614c73a3-b4a.js
    ///     (Edit this file with Write/Edit and re-invoke Workflow with {scriptPath: "C:\…js"} to iterate …)
    ///     Run ID: wf_614c73a3-b4a
    ///
    /// This used to read it as JSON — take everything from the first '{' and parse. Against the text above, the
    /// first '{' opens that <c>{scriptPath: …}</c> fragment in the parenthetical, which is not JSON at all (bare
    /// key, unescaped backslashes). The parse threw, this returned null, and so NO store was ever created for ANY
    /// workflow: no agent id was resolved, no transcript was read — the inspector sat on "Waiting for this agent's
    /// transcript to appear on disk…" while the files sat on disk the whole time — and nothing could see the
    /// journal say an agent had finished, so the roster counted a dozen agents running long after they returned.
    ///
    /// Three readings, most specific first, so a reworded launch message cannot break it the same silent way
    /// again: the labelled line, the structured JSON shape, and finally the run directory's own unmistakable
    /// …/subagents/workflows/wf_* shape anywhere in the text.
    /// </summary>
    public static string? DirectoryFromToolResult(string? result) =>
        string.IsNullOrWhiteSpace(result)
            ? null
            : FromLabelledLine(result) ?? FromJson(result) ?? FromRunDirShape(result);

    private static readonly Regex LabelledDirLine = new(
        @"^[ \t]*transcript[ \t]*(?:dir|directory)[ \t]*:[ \t]*(?<path>\S.*)$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly Regex RunDirShape = new(
        @"[\\/]subagents[\\/]workflows[\\/]wf_[A-Za-z0-9._-]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Where a Windows path starts, so the left edge can be found without cutting at a space.</summary>
    private static readonly Regex PathRoot = new(@"[A-Za-z]:[\\/]|\\\\", RegexOptions.CultureInvariant);

    private static string? FromLabelledLine(string result) =>
        LabelledDirLine.Match(result) is { Success: true } m ? Clean(m.Groups["path"].Value) : null;

    private static string? FromJson(string result)
    {
        var start = result.IndexOf('{');
        if (start < 0) return null;
        try
        {
            return JsonNode.Parse(result[start..]) is JsonObject obj
                ? Clean(obj["transcriptDir"]?.GetValue<string>())
                : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Last resort: find the run directory by its own shape, wherever in the message it appears.
    ///
    /// The left edge is walked back to the drive root (or the label's ": ") rather than to the first space, because
    /// a Windows profile path can legitimately contain one and cutting there hands back half a path.
    /// </summary>
    private static string? FromRunDirShape(string result)
    {
        if (RunDirShape.Match(result) is not { Success: true } m) return null;
        var head = result[..m.Index];
        var start = head.LastIndexOfAny(['\n', '\r']) + 1;
        if (head.LastIndexOf(": ", StringComparison.Ordinal) is var label && label >= start) start = label + 2;
        // The LAST root on the line: a message that names some other path first must not drag the left edge back.
        if (PathRoot.Matches(head[start..]).LastOrDefault() is { } root) start += root.Index;
        return Clean(result[start..(m.Index + m.Length)]);
    }

    /// <summary>Strip the decoration a path picks up inside a sentence, and reject anything that is not one —
    /// a store built on a word like "none" would poll a directory that can never exist.</summary>
    private static string? Clean(string? value)
    {
        var path = value?.Trim().Trim('"', '\'').TrimEnd('.', ',').Trim();
        return string.IsNullOrEmpty(path) || path.IndexOfAny(['\\', '/']) < 0 ? null : path;
    }
}
