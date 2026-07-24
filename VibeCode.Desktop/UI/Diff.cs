namespace VibeCode.UI;

public static class Diff
{
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
