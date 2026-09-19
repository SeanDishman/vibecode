namespace VibeCode.UI;

public static class Diff
{
    /// <summary>Codex add/delete events carry source text; update and shell-patch events carry unified diffs.</summary>
    public static IEnumerable<DiffLine> CodexLines(string text, string? kind, string? format)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (normalized.Length == 0) yield break;
        var lines = normalized.Split('\n');
        var count = lines.Length - (lines[^1].Length == 0 ? 1 : 0);
        var raw = format == "content" || (format is null && (kind is "add" or "delete")
            && !normalized.StartsWith("diff --git ", StringComparison.Ordinal)
            && !normalized.StartsWith("--- ", StringComparison.Ordinal)
            && !normalized.StartsWith("@@", StringComparison.Ordinal));
        var body = false;
        for (var i = 0; i < count; i++)
        {
            var line = lines[i];
            if (raw)
            {
                yield return new DiffLine { Kind = kind == "delete" ? "del" : "add", Text = line };
                continue;
            }
            if (line.StartsWith("@@", StringComparison.Ordinal)) { body = true; continue; }
            if (!body && (line.StartsWith("--- ", StringComparison.Ordinal)
                || line.StartsWith("+++ ", StringComparison.Ordinal)
                || line.StartsWith("diff --git ", StringComparison.Ordinal)
                || line.StartsWith("index ", StringComparison.Ordinal)
                || line.StartsWith("new file mode ", StringComparison.Ordinal)
                || line.StartsWith("deleted file mode ", StringComparison.Ordinal)
                || line.StartsWith("old mode ", StringComparison.Ordinal)
                || line.StartsWith("new mode ", StringComparison.Ordinal)
                || line.StartsWith("rename ", StringComparison.Ordinal)
                || line.StartsWith("similarity index ", StringComparison.Ordinal))) continue;
            if (line.StartsWith("\\ No newline", StringComparison.Ordinal)) continue;
            if (line.Length == 0 || line[0] is not ('+' or '-' or ' ')) continue;
            body = true;
            yield return new DiffLine
            {
                Kind = line[0] == '+' ? "add" : line[0] == '-' ? "del" : "ctx",
                Text = line[1..],
            };
        }
    }

    /// <summary>Simple LCS-based line diff, good enough for Edit tool previews.</summary>
    public static List<DiffLine> Lines(string oldText, string newText)
    {
        var a = (oldText ?? "").Replace("\r\n", "\n").Split('\n');
        var b = (newText ?? "").Replace("\r\n", "\n").Split('\n');
        var result = new List<DiffLine>();

        // Cap pathological sizes: fall back to plain del/add blocks.
        if ((long)a.Length * b.Length > 1_000_000)
        {
            foreach (var l in a) result.Add(new DiffLine { Kind = "del", Text = l });
            foreach (var l in b) result.Add(new DiffLine { Kind = "add", Text = l });
            return result;
        }

        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
            for (var j = b.Length - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        int x = 0, y = 0;
        while (x < a.Length && y < b.Length)
        {
            if (a[x] == b[y]) { result.Add(new DiffLine { Kind = "ctx", Text = a[x] }); x++; y++; }
            else if (lcs[x + 1, y] >= lcs[x, y + 1]) { result.Add(new DiffLine { Kind = "del", Text = a[x] }); x++; }
            else { result.Add(new DiffLine { Kind = "add", Text = b[y] }); y++; }
        }
        while (x < a.Length) result.Add(new DiffLine { Kind = "del", Text = a[x++] });
        while (y < b.Length) result.Add(new DiffLine { Kind = "add", Text = b[y++] });
        return result;
    }
}
