using VibeCode.Services;

namespace VibeCode.UI;

public enum DiffRowKind { Context, Add, Del }

/// <summary>One rendered line: its old/new line numbers (null when the side doesn't have it), its diff role,
/// and the syntax-colored tokens to draw.</summary>
public readonly record struct CodeRow(int? OldNo, int? NewNo, DiffRowKind Kind, IReadOnlyList<Tok> Tokens);

/// <summary>
/// Pure (WPF-free, unit-testable) construction of the code-viewer's line model: reconstruct a file's pre-edit
/// text, then produce a full-file unified diff whose lines carry syntax tokens for coloring.
/// </summary>
public static class CodeDiff
{
    public static string Norm(string? t) => (t ?? "").Replace("\r\n", "\n").Replace('\r', '\n');

    private static string[] SplitLines(string t) => t.Length == 0 ? Array.Empty<string>() : t.Split('\n');

    /// <summary>Rebuild the file's pre-edit text by undoing each edit (newest first) on the post-edit text.</summary>
    public static string ReverseApply(string finalText, IReadOnlyList<(string Old, string New)> ops)
    {
        var t = finalText ?? "";
        for (int i = ops.Count - 1; i >= 0; i--)
        {
            var (o, nw) = ops[i];
            if (nw.Length == 0) continue;                          // pure insertion - nothing to locate/undo
            int idx = t.IndexOf(nw, StringComparison.Ordinal);
            if (idx < 0) continue;                                 // file drifted since the edit - best effort
            t = string.Concat(t.AsSpan(0, idx), o, t.AsSpan(idx + nw.Length));
        }
        return t;
    }

    /// <summary>Full-file unified diff with new/old line numbers, sharing syntax tokens from each side.</summary>
    public static List<CodeRow> Unified(string oldText, string newText, string? path, out int added, out int removed)
    {
        added = 0; removed = 0;
        var oldL = SplitLines(Norm(oldText));   // "" => 0 lines (an empty file has no content, not one blank line)
        var newL = SplitLines(Norm(newText));
        var tOld = SyntaxHighlighter.Highlight(oldText, path);
        var tNew = SyntaxHighlighter.Highlight(newText, path);
        var rows = new List<CodeRow>(newL.Length + 16);

        int p = 0;
        while (p < oldL.Length && p < newL.Length && oldL[p] == newL[p]) p++;
        int s = 0;
        while (s < oldL.Length - p && s < newL.Length - p && oldL[^(s + 1)] == newL[^(s + 1)]) s++;

        for (int i = 0; i < p; i++) rows.Add(new CodeRow(i + 1, i + 1, DiffRowKind.Context, tNew[i]));

        int oStart = p, oEnd = oldL.Length - s, nStart = p, nEnd = newL.Length - s;
        int m = oEnd - oStart, k = nEnd - nStart;

        if ((long)m * k > 4_000_000)     // pathological middle: dump del-block then add-block
        {
            for (int i = oStart; i < oEnd; i++) { rows.Add(new CodeRow(i + 1, null, DiffRowKind.Del, tOld[i])); removed++; }
            for (int j = nStart; j < nEnd; j++) { rows.Add(new CodeRow(null, j + 1, DiffRowKind.Add, tNew[j])); added++; }
        }
        else if (m > 0 || k > 0)
        {
            var lcs = new int[m + 1, k + 1];
            for (int i = m - 1; i >= 0; i--)
                for (int j = k - 1; j >= 0; j--)
                    lcs[i, j] = oldL[oStart + i] == newL[nStart + j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            int x = 0, y = 0;
            while (x < m && y < k)
            {
                if (oldL[oStart + x] == newL[nStart + y]) { rows.Add(new CodeRow(oStart + x + 1, nStart + y + 1, DiffRowKind.Context, tNew[nStart + y])); x++; y++; }
                else if (lcs[x + 1, y] >= lcs[x, y + 1]) { rows.Add(new CodeRow(oStart + x + 1, null, DiffRowKind.Del, tOld[oStart + x])); x++; removed++; }
                else { rows.Add(new CodeRow(null, nStart + y + 1, DiffRowKind.Add, tNew[nStart + y])); y++; added++; }
            }
            while (x < m) { rows.Add(new CodeRow(oStart + x + 1, null, DiffRowKind.Del, tOld[oStart + x])); x++; removed++; }
            while (y < k) { rows.Add(new CodeRow(null, nStart + y + 1, DiffRowKind.Add, tNew[nStart + y])); y++; added++; }
        }

        for (int i = 0; i < s; i++) rows.Add(new CodeRow(oEnd + i + 1, nEnd + i + 1, DiffRowKind.Context, tNew[nEnd + i]));
        return rows;
    }

    public static List<CodeRow> Plain(string text, string? path)
    {
        var toks = SyntaxHighlighter.Highlight(text, path);
        var rows = new List<CodeRow>(toks.Count);
        for (int i = 0; i < toks.Count; i++) rows.Add(new CodeRow(i + 1, i + 1, DiffRowKind.Context, toks[i]));
        return rows;
    }

    /// <summary>Changes-only view built straight from a tool card's accumulated diff hunks (file not on disk).</summary>
    public static List<CodeRow> FromHunks(IReadOnlyList<DiffLine> lines, string? path, out int added, out int removed)
    {
        added = 0; removed = 0;
        var rows = new List<CodeRow>(lines.Count);
        int no = 0;
        foreach (var d in lines)
        {
            var kind = d.Kind switch { "add" => DiffRowKind.Add, "del" => DiffRowKind.Del, _ => DiffRowKind.Context };
            var toks = SyntaxHighlighter.HighlightLine(d.Text ?? "", path);
            switch (kind)
            {
                case DiffRowKind.Add: no++; rows.Add(new CodeRow(null, no, kind, toks)); added++; break;
                case DiffRowKind.Del: rows.Add(new CodeRow(null, null, kind, toks)); removed++; break;
                default: no++; rows.Add(new CodeRow(null, no, kind, toks)); break;
            }
        }
        return rows;
    }
}
