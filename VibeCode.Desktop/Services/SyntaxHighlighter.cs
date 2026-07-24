using System.IO;

namespace VibeCode.Services;

/// <summary>Token categories a line is broken into for coloring. The viewer maps each to a brush.</summary>
public enum TokKind { Default, Keyword, Control, Type, Function, String, Number, Comment, Punct, Tag, Attr, Meta }

/// <summary>One colored run of text on a line.</summary>
public readonly record struct Tok(string Text, TokKind Kind);

/// <summary>Carry-over highlight state between lines (open block comment / multi-line string / open XML tag).</summary>
public struct HState
{
    public bool InBlock;      // inside a /* */ (or <!-- -->) that opened on an earlier line
    public string? InString;  // delimiter of a multi-line string (""" ''' ` @" ) still open
    public bool InTag;        // XML: inside a <tag ...> whose attributes span lines
}

/// <summary>
/// Dependency-free source tokenizer. Good enough to make code read nicely (keywords, strings, numbers,
/// comments, types) across the languages VibeCode actually edits. Not a compiler - a fast, forgiving scanner.
/// </summary>
public static class SyntaxHighlighter
{
    /// <summary>Human label for the status bar ("C#", "TypeScript", ...).</summary>
    public static string LanguageName(string? path) => LangFor(path).Name;

    /// <summary>Tokenize the whole text into per-line runs, carrying multi-line comment/string state.</summary>
    public static List<List<Tok>> Highlight(string text, string? path)
    {
        var lang = LangFor(path);
        var lines = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var outp = new List<List<Tok>>(lines.Length);
        var state = new HState();
        foreach (var line in lines)
        {
            var toks = lang.Xml ? ScanXml(lang, line, ref state) : ScanCode(lang, line, ref state);
            Coalesce(toks);
            outp.Add(toks);
        }
        return outp;
    }

    /// <summary>Tokenize a single standalone line (no carried state) - used for diff-hunk fallback rendering.</summary>
    public static List<Tok> HighlightLine(string line, string? path)
    {
        var lang = LangFor(path);
        var state = new HState();
        var toks = lang.Xml ? ScanXml(lang, line ?? "", ref state) : ScanCode(lang, line ?? "", ref state);
        Coalesce(toks);
        return toks;
    }

    // ---------------------------------------------------------------- code scanner

    private static List<Tok> ScanCode(Lang lang, string line, ref HState st)
    {
        var toks = new List<Tok>();
        int i = 0, n = line.Length;

        // continue an open block comment
        if (st.InBlock && lang.Block is { } bc0)
        {
            int end = line.IndexOf(bc0.Close, StringComparison.Ordinal);
            if (end < 0) { if (n > 0) toks.Add(new Tok(line, TokKind.Comment)); return toks; }
            toks.Add(new Tok(line[..(end + bc0.Close.Length)], TokKind.Comment));
            i = end + bc0.Close.Length; st.InBlock = false;
        }
        // continue an open multi-line string
        else if (st.InString is { } open)
        {
            int end = FindStringEnd(line, 0, open, lang);
            if (end < 0) { if (n > 0) toks.Add(new Tok(line, TokKind.String)); return toks; }
            toks.Add(new Tok(line[..end], TokKind.String));
            i = end; st.InString = null;
        }

        while (i < n)
        {
            char c = line[i];

            if (char.IsWhiteSpace(c)) { int s = i; while (i < n && char.IsWhiteSpace(line[i])) i++; toks.Add(new Tok(line[s..i], TokKind.Default)); continue; }

            // line comment
            bool lc = false;
            foreach (var m in lang.LineComments)
                if (Match(line, i, m)) { toks.Add(new Tok(line[i..], TokKind.Comment)); i = n; lc = true; break; }
            if (lc) break;

            // block comment
            if (lang.Block is { } bc && Match(line, i, bc.Open))
            {
                int end = line.IndexOf(bc.Close, i + bc.Open.Length, StringComparison.Ordinal);
                if (end < 0) { toks.Add(new Tok(line[i..], TokKind.Comment)); st.InBlock = true; i = n; break; }
                toks.Add(new Tok(line[i..(end + bc.Close.Length)], TokKind.Comment)); i = end + bc.Close.Length; continue;
            }

            // decorator / attribute meta (@ident) - python/ts
            if (lang.MetaAt && c == '@' && i + 1 < n && (char.IsLetter(line[i + 1]) || line[i + 1] == '_'))
            {
                int s = i; i++; while (i < n && (char.IsLetterOrDigit(line[i]) || line[i] == '_' || line[i] == '.')) i++;
                toks.Add(new Tok(line[s..i], TokKind.Meta)); continue;
            }

            // string (longest delimiter first; supports verbatim @" and $" prefixes)
            var delim = MatchString(line, ref i, lang, out int strStart, out bool multiline);
            if (delim is not null)
            {
                int end = FindStringEnd(line, i, delim, lang);
                if (end < 0)
                {
                    toks.Add(new Tok(line[strStart..], TokKind.String));
                    st.InString = multiline ? delim : null;   // only real multi-line delimiters carry over
                    i = n; break;
                }
                toks.Add(new Tok(line[strStart..end], TokKind.String)); i = end; continue;
            }

            // number
            if (char.IsDigit(c) || (c == '.' && i + 1 < n && char.IsDigit(line[i + 1])))
            {
                int s = i; i = ScanNumber(line, i); toks.Add(new Tok(line[s..i], TokKind.Number)); continue;
            }

            // identifier / keyword
            if (char.IsLetter(c) || c == '_' || c == '$')
            {
                int s = i; while (i < n && (char.IsLetterOrDigit(line[i]) || line[i] == '_' || line[i] == '$')) i++;
                string word = line[s..i];
                TokKind k;
                if (lang.Control.Contains(word)) k = TokKind.Control;
                else if (lang.Keywords.Contains(word)) k = TokKind.Keyword;
                else if (lang.Types.Contains(word)) k = TokKind.Type;
                else if (NextNonSpace(line, i) == '(') k = TokKind.Function;
                else if (lang.CaseTypes && word.Length > 1 && char.IsUpper(word[0]) && HasLower(word)) k = TokKind.Type;
                else k = TokKind.Default;
                toks.Add(new Tok(word, k)); continue;
            }

            // punctuation / operator
            toks.Add(new Tok(c.ToString(), TokKind.Punct)); i++;
        }
        return toks;
    }

    // ---------------------------------------------------------------- xml/xaml/html scanner

    private static List<Tok> ScanXml(Lang lang, string line, ref HState st)
    {
        var toks = new List<Tok>();
        int i = 0, n = line.Length;

        if (st.InBlock)
        {
            int end = line.IndexOf("-->", StringComparison.Ordinal);
            if (end < 0) { if (n > 0) toks.Add(new Tok(line, TokKind.Comment)); return toks; }
            toks.Add(new Tok(line[..(end + 3)], TokKind.Comment)); i = end + 3; st.InBlock = false;
        }

        while (i < n)
        {
            char c = line[i];
            if (!st.InTag)
            {
                if (Match(line, i, "<!--"))
                {
                    int end = line.IndexOf("-->", i + 4, StringComparison.Ordinal);
                    if (end < 0) { toks.Add(new Tok(line[i..], TokKind.Comment)); st.InBlock = true; i = n; break; }
                    toks.Add(new Tok(line[i..(end + 3)], TokKind.Comment)); i = end + 3; continue;
                }
                if (c == '<')
                {
                    int s = i; i++; if (i < n && (line[i] == '/' || line[i] == '?' || line[i] == '!')) i++;
                    toks.Add(new Tok(line[s..i], TokKind.Punct));
                    int ns = i; while (i < n && (char.IsLetterOrDigit(line[i]) || line[i] is '.' or ':' or '_' or '-')) i++;
                    if (i > ns) toks.Add(new Tok(line[ns..i], TokKind.Tag));
                    st.InTag = true; continue;
                }
                // text content
                int ts = i; while (i < n && line[i] != '<') i++; toks.Add(new Tok(line[ts..i], TokKind.Default)); continue;
            }
            else
            {
                if (char.IsWhiteSpace(c)) { int s = i; while (i < n && char.IsWhiteSpace(line[i])) i++; toks.Add(new Tok(line[s..i], TokKind.Default)); continue; }
                if (Match(line, i, "/>") || c == '>' || c == '?')
                {
                    int s = i; if (Match(line, i, "/>")) i += 2; else i++;
                    toks.Add(new Tok(line[s..i], TokKind.Punct)); if (line[s] != '?' || Match(line, s, "?>")) st.InTag = false; continue;
                }
                if (c is '"' or '\'')
                {
                    int s = i; char q = c; i++; while (i < n && line[i] != q) i++; if (i < n) i++;
                    toks.Add(new Tok(line[s..i], TokKind.String)); continue;
                }
                if (c == '=') { toks.Add(new Tok("=", TokKind.Punct)); i++; continue; }
                if (char.IsLetter(c) || c == '_')
                {
                    int s = i; while (i < n && (char.IsLetterOrDigit(line[i]) || line[i] is '.' or ':' or '_' or '-')) i++;
                    toks.Add(new Tok(line[s..i], TokKind.Attr)); continue;
                }
                toks.Add(new Tok(c.ToString(), TokKind.Punct)); i++;
            }
        }
        return toks;
    }

    // ---------------------------------------------------------------- helpers

    private static int ScanNumber(string line, int i)
    {
        int n = line.Length;
        if (line[i] == '0' && i + 1 < n && (line[i + 1] is 'x' or 'X' or 'b' or 'B' or 'o' or 'O'))
        {
            i += 2; while (i < n && (Uri.IsHexDigit(line[i]) || line[i] == '_')) i++;
        }
        else
        {
            while (i < n && (char.IsDigit(line[i]) || line[i] == '_')) i++;
            if (i < n && line[i] == '.' && i + 1 < n && char.IsDigit(line[i + 1])) { i++; while (i < n && (char.IsDigit(line[i]) || line[i] == '_')) i++; }
            if (i < n && (line[i] is 'e' or 'E')) { i++; if (i < n && (line[i] is '+' or '-')) i++; while (i < n && char.IsDigit(line[i])) i++; }
        }
        while (i < n && (line[i] is 'f' or 'F' or 'd' or 'D' or 'm' or 'M' or 'l' or 'L' or 'u' or 'U')) i++; // numeric suffixes
        return i;
    }

    /// <summary>If a string opens at <paramref name="i"/>, advance past the opening delimiter and return it.</summary>
    private static string? MatchString(string line, ref int i, Lang lang, out int start, out bool multiline)
    {
        start = i; multiline = false;
        int p = i, n = line.Length;
        // optional prefixes: @ (verbatim), $ (interpolated), r/b/f (python/rust-ish), u
        while (p < n && (line[p] is '@' or '$' or 'r' or 'b' or 'f' or 'u' or 'R' or 'B' or 'F')) { if (p + 1 < n && (line[p + 1] is '"' or '\'')) { p++; break; } else break; }
        foreach (var d in lang.Strings)
        {
            if (Match(line, p, d))
            {
                bool verbatim = start < p && line[start] is '@';
                multiline = d.Length >= 3 || (d == "`") || verbatim;   // triple-quote, template literal, or C# verbatim
                i = p + d.Length;
                return d;
            }
        }
        return null;
    }

    private static int FindStringEnd(string line, int from, string delim, Lang lang)
    {
        int n = line.Length, i = from;
        while (i < n)
        {
            if (lang.Escapes && line[i] == '\\' && delim.Length == 1 && delim != "`") { i += 2; continue; }
            if (Match(line, i, delim)) return i + delim.Length;
            i++;
        }
        return -1;
    }

    private static bool Match(string s, int i, string m)
    {
        if (i < 0 || i + m.Length > s.Length) return false;
        for (int k = 0; k < m.Length; k++) if (s[i + k] != m[k]) return false;
        return true;
    }

    private static char NextNonSpace(string s, int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; return i < s.Length ? s[i] : '\0'; }
    private static bool HasLower(string w) { foreach (var ch in w) if (char.IsLower(ch)) return true; return false; }

    /// <summary>Merge adjacent runs of the same kind so the viewer builds far fewer Inline objects.</summary>
    private static void Coalesce(List<Tok> toks)
    {
        for (int i = toks.Count - 1; i > 0; i--)
            if (toks[i].Kind == toks[i - 1].Kind) { toks[i - 1] = new Tok(toks[i - 1].Text + toks[i].Text, toks[i - 1].Kind); toks.RemoveAt(i); }
    }

    // ---------------------------------------------------------------- language table

    private sealed class Lang
    {
        public string Name = "Text";
        public string[] LineComments = Array.Empty<string>();
        public (string Open, string Close)? Block;
        public string[] Strings = Array.Empty<string>();  // longest-first
        public bool Escapes = true;
        public bool Xml;
        public bool MetaAt;
        public bool CaseTypes;
        public HashSet<string> Keywords = new();
        public HashSet<string> Control = new();
        public HashSet<string> Types = new();
    }

    private static readonly Dictionary<string, Lang> _cache = new(StringComparer.OrdinalIgnoreCase);

    private static Lang LangFor(string? path)
    {
        var ext = (Path.GetExtension(path ?? "") ?? "").ToLowerInvariant();
        var key = ext.Length == 0 ? "" : ext;
        if (_cache.TryGetValue(key, out var cached)) return cached;
        var lang = Build(ext);
        _cache[key] = lang;
        return lang;
    }

    private static Lang Build(string ext) => ext switch
    {
        ".cs" => CSharp(),
        ".xaml" or ".axaml" or ".xml" or ".html" or ".htm" or ".csproj" or ".props" or ".targets"
            or ".svg" or ".resx" or ".config" or ".xsd" or ".xslt" or ".vsixmanifest" or ".manifest" => Xml(),
        ".json" or ".jsonc" or ".json5" => Json(),
        ".ts" or ".tsx" or ".js" or ".jsx" or ".mjs" or ".cjs" => JsTs(),
        ".py" or ".pyw" or ".pyi" => Python(),
        ".css" or ".scss" or ".less" => Css(),
        ".md" or ".markdown" => Markdown(),
        _ => Generic(),
    };

    private static HashSet<string> Set(string words) => new(words.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

    private static Lang CSharp() => new()
    {
        Name = "C#", LineComments = new[] { "//" }, Block = ("/*", "*/"),
        Strings = new[] { "\"\"\"", "\"", "'" }, CaseTypes = true,
        Keywords = Set("abstract as async await base checked class const delegate enum event explicit extern false fixed get global implicit in init interface internal is lock nameof namespace new null operator out override params partial private protected public readonly record ref remove sealed set sizeof stackalloc static struct this throw true typeof unchecked unsafe using value virtual volatile where with add extern"),
        Control = Set("if else for foreach while do switch case break continue return goto throw try catch finally yield when default"),
        Types = Set("bool byte char decimal double float int long object sbyte short string uint ulong ushort void var dynamic nint nuint Task"),
    };

    private static Lang JsTs() => new()
    {
        Name = "TypeScript", LineComments = new[] { "//" }, Block = ("/*", "*/"),
        Strings = new[] { "\"", "'", "`" }, MetaAt = true, CaseTypes = true,
        Keywords = Set("abstract as async await class const declare default delete enum export extends false from function get implements import in instanceof interface is keyof let module namespace new null of package private protected public readonly satisfies set static super this true type typeof undefined var with yield override infer asserts"),
        Control = Set("if else for while do switch case break continue return throw try catch finally yield await"),
        Types = Set("any boolean number string symbol void undefined null never unknown object bigint Array Promise Record"),
    };

    private static Lang Python() => new()
    {
        Name = "Python", LineComments = new[] { "#" }, Block = null,
        Strings = new[] { "\"\"\"", "'''", "\"", "'" }, MetaAt = true,
        Keywords = Set("and as assert async await class def del False from global import in is lambda None nonlocal not or pass self cls True with as"),
        Control = Set("if elif else for while try except finally return yield raise break continue pass match case"),
        Types = Set("int float str bool bytes list dict set tuple frozenset object complex bytearray range type"),
    };

    private static Lang Css() => new()
    {
        Name = "CSS", LineComments = Array.Empty<string>(), Block = ("/*", "*/"),
        Strings = new[] { "\"", "'" }, MetaAt = true,
        Keywords = Set("important inherit initial unset auto none flex grid block inline absolute relative fixed static hidden solid dashed dotted bold normal center left right top bottom"),
        Control = Set(""),
        Types = Set(""),
    };

    private static Lang Json() => new()
    {
        Name = "JSON", LineComments = new[] { "//" }, Block = ("/*", "*/"),
        Strings = new[] { "\"" },
        Keywords = Set("true false null"),
        Control = Set(""), Types = Set(""),
    };

    private static Lang Markdown() => new()
    {
        Name = "Markdown", LineComments = Array.Empty<string>(), Block = null,
        Strings = new[] { "`" }, Escapes = false,
        Keywords = Set(""), Control = Set(""), Types = Set(""),
    };

    private static Lang Xml() => new() { Name = "XML", Xml = true, Strings = new[] { "\"", "'" } };

    private static Lang Generic() => new()
    {
        Name = "Text", LineComments = new[] { "//", "#" }, Block = ("/*", "*/"),
        Strings = new[] { "\"", "'", "`" }, CaseTypes = false,
        Keywords = Set("if else for while do return function func fn def class struct enum interface import from export public private protected static const let var new try catch finally switch case break continue throw true false null nil void package namespace using module type val fun end then begin"),
        Control = Set("if else for while do return switch case break continue throw try catch finally"),
        Types = Set("int string bool float double char long short byte void object"),
    };
}
