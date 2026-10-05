using System.IO;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using VibeCode.UI;

namespace VibeCode.Services;

/// <summary>Where one edit landed: lines <see cref="Start"/>..<see cref="End"/> of the file right after that edit.
/// A pure deletion leaves no lines behind, so it is pinned to the line just above the removed block (0 = top of file).</summary>
public sealed record BridgeEditRange(int Start, int End, int Added, int Removed)
{
    [JsonIgnore] public bool DeletionOnly => Added == 0;

    /// <summary>True when the edit touched any line in <paramref name="first"/>..<paramref name="last"/>. A deletion
    /// counts for both lines around the gap it left.</summary>
    public bool Overlaps(int first, int last) => DeletionOnly
        ? first <= Start + 1 && last >= Start
        : first <= End && last >= Start;
}

/// <summary>One successful file-edit tool call as its provider reported it. <see cref="Result"/> is the provider's
/// structured tool result when it has one (Claude's structuredPatch); everything else is derived from the input.
/// A shell command's change (Tool "Shell") instead carries the file's text from before the command.</summary>
public sealed record BridgeEditObservation(string Tool, JsonObject Input, JsonObject? Result, string Cwd)
{
    /// <summary>Shell edits: the file's text before the command; null when it did not exist.</summary>
    public string? Before { get; init; }
    /// <summary>Shell edits: false when nothing recorded the earlier text, so the lines cannot be worked out.</summary>
    public bool BeforeKnown { get; init; }
    /// <summary>Shell edits: another agent's shell command was running at the same time, so the author is a best guess.</summary>
    public bool Shared { get; init; }
}

/// <summary>What one edit did to one file. <see cref="Exact"/> is false when the line numbers had to be located by
/// searching the file afterwards and the search was ambiguous or failed.</summary>
public sealed record BridgeEditChange(string FullPath, string Change, IReadOnlyList<BridgeEditRange> Ranges,
    int Added, int Removed, bool Exact);

/// <summary>
/// Turns provider edit payloads into line ranges. Exact hunks are used whenever the provider supplies them (Claude's
/// structuredPatch, numbered unified-diff headers); otherwise the new text is located in the file as it is right after
/// the edit. Every provider funnels its edits into the same tool names (Edit, MultiEdit, Write, NotebookEdit,
/// CodexEdit), so this covers Claude, Codex, Kimi, Grok and GLM alike.
/// </summary>
public static class BridgeEditDiff
{
    public const int MaxRanges = 40;
    private const int MaxSourceChars = 8 * 1024 * 1024;
    private static readonly Regex HunkHeader = new(@"^@@ -\d+(?:,\d+)? \+(\d+)(?:,\d+)? @@", RegexOptions.CultureInvariant);

    public static BridgeEditChange? Analyze(BridgeEditObservation edit, Func<string, string?> readFile)
    {
        var input = edit.Input;
        var raw = Text(input["file_path"]) ?? Text(input["notebook_path"]) ?? Text(input["path"]);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string full;
        try { full = Path.GetFullPath(Path.IsPathRooted(raw) ? raw : Path.Combine(edit.Cwd, raw)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }

        SourceText? source = null;
        var loaded = false;
        SourceText? Source()
        {
            if (loaded) return source;
            loaded = true;
            var text = readFile(full);
            return source = text is null || text.Length > MaxSourceChars ? null : new SourceText(text);
        }

        var lines = new LineTally();
        // Claude reports the exact hunks it applied, for Edit, MultiEdit and Write alike.
        if (edit.Result?["structuredPatch"] is JsonArray patch)
        {
            if (Text(edit.Result["type"]) == "create")
                return Whole(full, "create", Text(edit.Result["content"]) ?? Text(input["content"]) ?? "");
            foreach (var hunk in patch.OfType<JsonObject>())
                lines.Walk(Number(hunk["newStart"]), (hunk["lines"] as JsonArray ?? []).Select(Text).OfType<string>().Select(Kind));
            return lines.Result(full, "edit");
        }

        switch (edit.Tool)
        {
            case "CodexEdit":
            {
                var kind = Text(input["kind"]);
                var diff = Text(input["diff"]) ?? "";
                var change = kind switch { "add" => "create", "delete" => "delete", _ => "edit" };
                // app-server sends full source text for add/delete and a unified diff for update.
                if (Text(input["diff_format"]) == "content" || (kind is "add" or "delete" && !LooksLikeDiff(diff)))
                    return kind == "delete" ? Deleted(full, CountLines(diff)) : Whole(full, "create", diff);
                lines.UnifiedDiff(diff, Source);
                return change == "delete" ? Deleted(full, lines.Removed) : lines.Result(full, change);
            }
            case "Write":
                return Whole(full, "write", Text(input["content"]) ?? "");
            case "Edit":
                lines.Snippet(Text(input["old_string"]) ?? "", Text(input["new_string"]) ?? "", ReplaceAll(input), Source);
                return lines.Result(full, "edit");
            case "MultiEdit":
                foreach (var one in (input["edits"] as JsonArray ?? []).OfType<JsonObject>())
                    lines.Snippet(Text(one["old_string"]) ?? "", Text(one["new_string"]) ?? "", ReplaceAll(one), Source);
                return lines.Result(full, "edit");
            case "NotebookEdit":
                // Notebook cells have no stable file line numbers; record the file without inventing any.
                return new BridgeEditChange(full, "edit", [], 0, 0, Exact: false);
            case "Shell":
            {
                var current = readFile(full);
                if (current is null && File.Exists(full)) return null; // unreadable or oversized now: nothing reliable to say
                if (!edit.BeforeKnown) return current is null ? null : new BridgeEditChange(full, "edit", [], 0, 0, Exact: false);
                if (edit.Before is null)
                    return current is null ? null : Whole(full, "create", current) with { Exact = !edit.Shared };
                if (current is null) return Deleted(full, CountLines(edit.Before)) with { Exact = !edit.Shared };
                lines.Files(edit.Before, current);
                return lines.Result(full, "edit") is { } changed ? changed with { Exact = changed.Exact && !edit.Shared } : null;
            }
            default:
                return null;
        }
    }

    /// <summary>"120-135, 140, after 41 (3 deleted)" — where the changes are, read by agents and people alike.</summary>
    public static string Describe(string change, IReadOnlyList<BridgeEditRange> ranges, bool exact)
    {
        if (change == "delete") return "whole file deleted";
        if (ranges.Count == 0) return "unknown";
        var text = string.Join(", ", ranges.Select(r => r.DeletionOnly
            ? (r.Start == 0 ? "top of file" : "after " + r.Start) + $" ({r.Removed} deleted)"
            : r.Start == r.End ? r.Start.ToString() : $"{r.Start}-{r.End}"));
        return exact ? text : "about " + text;
    }

    private static BridgeEditChange Whole(string full, string change, string content)
    {
        var count = CountLines(content);
        return new BridgeEditChange(full, change, count == 0 ? [] : [new BridgeEditRange(1, count, count, 0)], count, 0, true);
    }

    private static BridgeEditChange Deleted(string full, int removed) => new(full, "delete", [], 0, removed, true);

    private static bool LooksLikeDiff(string text) =>
        text.StartsWith("diff --git ", StringComparison.Ordinal) || text.StartsWith("--- ", StringComparison.Ordinal)
        || text.StartsWith("@@", StringComparison.Ordinal);

    private static int CountLines(string text)
    {
        var normalized = Normalize(text);
        if (normalized.Length == 0) return 0;
        var count = normalized.Count(c => c == '\n') + 1;
        return normalized.EndsWith('\n') ? count - 1 : count;
    }

    private static string[] SplitLines(string text)
    {
        var normalized = Normalize(text);
        if (normalized.Length == 0) return [];
        var lines = normalized.Split('\n');
        return normalized.EndsWith('\n') ? lines[..^1] : lines;
    }

    private static bool ReplaceAll(JsonObject edit) =>
        edit["replace_all"] is JsonValue value && value.TryGetValue<bool>(out var all) && all;

    private static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

    private static char Kind(string line) => line.Length == 0 ? ' ' : line[0] is '+' or '-' or '\\' ? line[0] : ' ';

    private static string? Text(JsonNode? node)
    {
        try { return node?.GetValue<string>(); }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException) { return null; }
    }

    private static int? Number(JsonNode? node)
    {
        try { return node?.GetValue<int>(); }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException) { return null; }
    }

    /// <summary>A file's text with a line index, for locating where an edit landed.</summary>
    private sealed class SourceText
    {
        private readonly List<int> _lineStarts = [0];
        public SourceText(string text)
        {
            Content = Normalize(text);
            for (var i = 0; i < Content.Length; i++)
                if (Content[i] == '\n') _lineStarts.Add(i + 1);
        }
        public string Content { get; }
        /// <summary>1-based line holding character <paramref name="index"/>.</summary>
        public int LineOf(int index)
        {
            var found = _lineStarts.BinarySearch(index);
            return (found >= 0 ? found : ~found - 1) + 1;
        }

        /// <summary>1-based lines where <paramref name="needle"/> starts; at most two unless every match is wanted.</summary>
        public List<int> Find(string needle, bool all)
        {
            var hits = new List<int>();
            if (needle.Trim().Length == 0) return hits;
            for (var at = Content.IndexOf(needle, StringComparison.Ordinal); at >= 0 && hits.Count < (all ? MaxRanges : 2);
                 at = Content.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
                hits.Add(LineOf(at));
            return hits;
        }
    }

    /// <summary>Walks hunks as a sequence of '+', '-' and ' ' line kinds and collects the changed blocks.</summary>
    private sealed class LineTally
    {
        private readonly List<BridgeEditRange> _ranges = [];
        private bool _exact = true;
        public int Added { get; private set; }
        public int Removed { get; private set; }

        /// <summary>Walk one hunk whose first line sits at new-file line <paramref name="newStart"/>. A null start
        /// means the position is unknown: the counts still add up, but no range is claimed.</summary>
        public void Walk(int? newStart, IEnumerable<char> kinds)
        {
            if (newStart is null) _exact = false;
            var line = Math.Max(1, newStart ?? 1);
            int? blockStart = null;
            int blockAdded = 0, blockRemoved = 0;
            foreach (var kind in kinds)
            {
                if (kind == '\\') continue; // "\ No newline at end of file"
                if (kind == '+') { blockStart ??= line; blockAdded++; line++; }
                else if (kind == '-') { blockStart ??= line; blockRemoved++; }
                else { Flush(); line++; }
            }
            Flush();

            void Flush()
            {
                if (blockStart is { } start)
                {
                    Added += blockAdded;
                    Removed += blockRemoved;
                    if (newStart is not null)
                        _ranges.Add(blockAdded > 0
                            ? new BridgeEditRange(start, start + blockAdded - 1, blockAdded, blockRemoved)
                            : new BridgeEditRange(start - 1, start - 1, 0, blockRemoved));
                }
                blockStart = null;
                blockAdded = blockRemoved = 0;
            }
        }

        /// <summary>An old/new text replacement (Edit, MultiEdit, ACP diffs). The new text is located in the file
        /// right after the edit; partial-line snippets work because the search is by character, not by line.</summary>
        public void Snippet(string oldText, string newText, bool replaceAll, Func<SourceText?> source)
        {
            var kinds = Diff.Lines(Normalize(oldText), Normalize(newText))
                .Select(l => l.Kind switch { "add" => '+', "del" => '-', _ => ' ' }).ToArray();
            if (kinds.All(k => k == ' ')) return;
            var anchors = source()?.Find(Normalize(newText), replaceAll) ?? [];
            if (anchors.Count == 0) { Walk(null, kinds); return; }
            if (!replaceAll && anchors.Count > 1) _exact = false; // the same text appears twice; the first is a guess
            // replace_all changed every occurrence, so each one is a real change at its own lines.
            foreach (var anchor in replaceAll ? anchors : anchors.Take(1)) Walk(anchor, kinds);
        }

        /// <summary>Whole-file before/after (shell edits). Unchanged leading and trailing lines are skipped first, so a
        /// local change in a large file is diffed exactly instead of hitting the line-diff size cap.</summary>
        public void Files(string before, string after)
        {
            var a = SplitLines(before);
            var b = SplitLines(after);
            var head = 0;
            while (head < a.Length && head < b.Length && a[head] == b[head]) head++;
            var tail = 0;
            while (tail < a.Length - head && tail < b.Length - head && a[^(tail + 1)] == b[^(tail + 1)]) tail++;
            var removed = a[head..^tail];
            var added = b[head..^tail];
            IEnumerable<char> kinds = removed.Length == 0 ? added.Select(_ => '+')
                : added.Length == 0 ? removed.Select(_ => '-')
                : Diff.Lines(string.Join("\n", removed), string.Join("\n", added)).Select(l => l.Kind switch { "add" => '+', "del" => '-', _ => ' ' });
            Walk(head + 1, kinds);
        }

        public void UnifiedDiff(string diff, Func<SourceText?> source)
        {
            int? header = null;
            var body = new List<string>();
            var inHunk = false;
            foreach (var line in Normalize(diff).Split('\n'))
            {
                if (line.StartsWith("@@", StringComparison.Ordinal))
                {
                    if (inHunk) Hunk();
                    inHunk = true;
                    var match = HunkHeader.Match(line);
                    header = match.Success ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
                    continue;
                }
                if (line.StartsWith("diff --git ", StringComparison.Ordinal)) { if (inHunk) Hunk(); inHunk = false; continue; }
                if (!inHunk) continue;
                if (line.Length > 0 && line[0] is '+' or '-' or ' ' or '\\') body.Add(line);
                else if (line.Length == 0) body.Add(" ");
            }
            if (inHunk) Hunk();

            void Hunk()
            {
                var start = header;
                if (start == 0)
                {
                    // A whole-file deletion reports "+0,0": nothing is left to point at, but the count is exact.
                    Removed += body.Count(l => l[0] == '-');
                    Added += body.Count(l => l[0] == '+');
                }
                else
                {
                    // Numbered headers are exact. apply_patch-style hunks ("@@ class Foo") carry no numbers, so their
                    // context and added lines are located in the file instead.
                    if (start is null)
                    {
                        var after = string.Join("\n", body.Where(l => l[0] is ' ' or '+').Select(l => l[1..]));
                        var hits = source()?.Find(after, all: false) ?? [];
                        if (hits.Count > 1) _exact = false;
                        start = hits.Count > 0 ? hits[0] : null;
                    }
                    Walk(start, body.Select(Kind));
                }
                body.Clear();
                header = null;
            }
        }

        public BridgeEditChange? Result(string full, string change)
        {
            if (Added == 0 && Removed == 0 && change == "edit") return null; // nothing actually changed
            var ranges = _ranges.OrderBy(r => r.Start).ThenBy(r => r.End).ToList();
            var exact = _exact;
            if (ranges.Count > MaxRanges) { ranges = ranges.Take(MaxRanges).ToList(); exact = false; }
            return new BridgeEditChange(full, change, ranges, Added, Removed, exact);
        }
    }
}
