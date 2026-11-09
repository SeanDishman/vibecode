using System.Text.Json.Nodes;

namespace VibeCode.Services;

/// <summary>

/// count. Gemini's current <c>read_file</c> uses 1-based inclusive <c>start_line</c>/<c>end_line</c>; older Gemini CLI
/// used a 0-based <c>offset</c>. ACP <c>locations[].line</c> is 1-based.
/// </summary>
public static class ToolFileRange
{
    public static (int Start, int End)? FromInput(JsonObject? input)
    {
        if (input is null) return null;
        var start = Positive(input["start_line"]) ?? Positive(input["StartLine"]);
        var end = Positive(input["end_line"]) ?? Positive(input["EndLine"]);
        if (start is { } first)
            return (first, end is { } last && last >= first ? last : first);

        var offset = Number(input["offset"]);
        var limit = Positive(input["limit"]);
        if (offset is not { } from || from < 0) return Positive(input["line"]) is { } line ? (line, line) : null;
        // offset 0 is Gemini's old 0-based start. Any other offset is Claude's 1-based first line.
        var begin = from == 0 ? 1 : from;
        var stop = limit is { } count ? begin + count - 1 : begin;
        return (begin, Math.Max(begin, stop));
    }

    public static string? Describe(JsonObject? input)
    {
        if (FromInput(input) is not { } range) return null;
        return range.Start == range.End ? range.Start.ToString() : $"{range.Start}-{range.End}";
    }

    public static string FileSummary(JsonObject? input, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) path = PathFromInput(input);
        if (string.IsNullOrEmpty(path)) return "";
        return Describe(input) is { } range ? path + ": " + range : path;
    }

    /// <summary>Provider spellings differ; an empty alias must not hide the next usable path.</summary>
    public static string? PathFromInput(JsonObject? input)
    {
        foreach (var key in new[] { "file_path", "path", "target_file", "absolute_path", "AbsolutePath", "TargetFile", "file", "filename", "filePath", "notebook_path", "target_directory" })
            if (Text(input?[key]) is { } value && !string.IsNullOrWhiteSpace(value)) return value;
        return null;
    }

    /// <summary>Copy ACP location line numbers onto the tool input when the provider omitted them.</summary>
    public static void ApplyLocations(JsonObject input, JsonArray? locations)
    {
        int? min = null, max = null;
        foreach (var location in locations?.OfType<JsonObject>() ?? [])
        {
            if (Positive(location["line"]) is not { } line) continue;
            min = min is { } a ? Math.Min(a, line) : line;
            max = max is { } b ? Math.Max(b, line) : line;
        }
        if (min is not { } start) return;
        var end = max ?? start;
        input["start_line"] ??= start;
        if (end != start) input["end_line"] ??= end;
        input["offset"] ??= start;
        if (end >= start && input["limit"] is null) input["limit"] = end - start + 1;
    }

    /// <summary>Gemini history and rawInput: fold start_line/end_line and legacy 0-based offset into both shapes.
    /// Antigravity's view_file / replace_file_content / write_to_file use PascalCase AbsolutePath, StartLine, EndLine,
    /// TargetFile, TargetContent, ReplacementContent and CodeContent — copy those onto the Claude-shaped keys first.</summary>
    public static void NormalizeGemini(JsonObject input)
    {
        CopyIfMissing(input, "file_path", "AbsolutePath", "TargetFile", "absolute_path");
        CopyIfMissing(input, "start_line", "StartLine");
        CopyIfMissing(input, "end_line", "EndLine");
        CopyIfMissing(input, "old_string", "TargetContent");
        CopyIfMissing(input, "new_string", "ReplacementContent");
        CopyIfMissing(input, "content", "CodeContent");
        if (input["replace_all"] is null && input["AllowMultiple"] is JsonValue allow)
            input["replace_all"] = allow.DeepClone();

        var start = Positive(input["start_line"]);
        var end = Positive(input["end_line"]);
        var offset = Number(input["offset"]);
        var limit = Positive(input["limit"]);

        if (start is null && offset is { } from && from >= 0)
        {
            start = from + 1;
            if (limit is { } count) end = start + count - 1;
        }

        if (start is not { } first) return;
        input["start_line"] = first;
        input["offset"] = first;
        if (end is not { } last || last < first) return;
        input["end_line"] = last;
        input["limit"] = last - first + 1;
    }

    private static void CopyIfMissing(JsonObject input, string dest, params string[] sources)
    {
        if (input[dest] is not null) return;
        foreach (var source in sources)
        {
            if (input[source] is not { } value) continue;
            input[dest] = value.DeepClone();
            return;
        }
    }

    private static string? Text(JsonNode? node)
    {
        try { return node?.GetValue<string>(); }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException) { return null; }
    }

    private static int? Number(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<int>(out var i)) return i;
        if (value.TryGetValue<long>(out var l) && l is >= int.MinValue and <= int.MaxValue) return (int)l;
        if (value.TryGetValue<double>(out var d) && d is >= int.MinValue and <= int.MaxValue && Math.Abs(d - Math.Round(d)) < 0.001)
            return (int)Math.Round(d);
        if (value.TryGetValue<string>(out var text)
            && int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        return null;
    }

    private static int? Positive(JsonNode? node) => Number(node) is > 0 and var n ? n : null;
}
