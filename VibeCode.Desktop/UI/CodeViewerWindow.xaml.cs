using System.IO;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using VibeCode.Services;

namespace VibeCode.UI;

/// <summary>
/// A Sublime-style read-only code viewer. Opens the file a tool card edited, syntax-highlights it, and overlays a
/// full-file unified diff: lines the edit(s) added glow green, lines they removed show red - accumulated across a run
/// of consecutive edits to the same file. Falls back to a changes-only hunk view when the file isn't on disk.
/// </summary>
public partial class CodeViewerWindow : Window
{
    private const int MaxRows = 9000;            // guard pathological files; note truncation
    private string _copyText = "";

    /// <summary>Open a viewer for the file this tool touched.</summary>
    public static void Open(ToolItem tool, Window? owner)
    {
        var w = new CodeViewerWindow(tool) { Owner = owner };
        if (Environment.GetEnvironmentVariable("VIBECODE_HIDDEN") == "1")
        { w.WindowStartupLocation = WindowStartupLocation.Manual; w.Left = 6200; w.Top = 220; }
        w.Show();
    }

    public CodeViewerWindow(ToolItem tool)
    {
        InitializeComponent();
        try { Build(tool); }
        catch (Exception ex)
        {
            FootMode.Text = "Could not render: " + ex.Message;
            TitleFile.Text = tool.FilePath is { } p ? Path.GetFileName(p) : "Code";
        }
    }

    private void Build(ToolItem tool)
    {
        var path = tool.FilePath;
        Title = path is { } ? Path.GetFileName(path) : "Code";
        TitleFile.Text = path is { } ? Path.GetFileName(path) : (tool.Name + " output");
        TitleDir.Text = path is { } ? (Path.GetDirectoryName(path) ?? "") : "";
        TitleLang.Text = SyntaxHighlighter.LanguageName(path);

        var disk = TryReadFile(path);
        var ops = tool.EditOps;

        List<CodeRow> rows;
        int added = 0, removed = 0;
        bool fullFile;

        bool haveContent = disk is not null || (ops.Count > 0 && ops[^1].New.Length > 0);
        if (ops.Count > 0 && haveContent)
        {
            var newFull = disk ?? ops[^1].New;                 // Write-without-disk: the op's New IS the full file
            var oldFull = CodeDiff.ReverseApply(newFull, ops);
            rows = CodeDiff.Unified(oldFull, newFull, path, out added, out removed);
            fullFile = true;
        }
        else if (tool.DiffLines.Count > 0)
        {
            rows = CodeDiff.FromHunks(tool.DiffLines, path, out added, out removed);
            fullFile = false;
        }
        else if (disk is not null)
        {
            rows = CodeDiff.Plain(disk, path);
            fullFile = true;
        }
        else
        {
            FootMode.Text = "No file content available.";
            return;
        }

        _copyText = disk ?? (ops.Count > 0 ? ops[^1].New : string.Join("\n", rows.Select(TextOf)));

        Render(rows);
        TitleStat.Inlines.Clear();
        if (added > 0 || removed > 0)
        {
            TitleStat.Inlines.Add(new Run($"+{added}") { Foreground = Brush("#7ED0A6") });
            TitleStat.Inlines.Add(new Run($"  −{removed}") { Foreground = Brush("#E86A78") });
        }
        var shown = Math.Min(rows.Count, MaxRows);
        FootMode.Text = fullFile
            ? (tool.EditCount > 1 ? $"Full file · {tool.EditCount} edits combined" : "Full file with changes highlighted")
            : "Changes only (file not found on disk)";
        FootInfo.Text = $"{shown} lines · {SyntaxHighlighter.LanguageName(path)}";
    }

    private static string? TryReadFile(string? path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            if (new FileInfo(path).Length > 4_000_000) return null;    // too big - fall back to hunk view
            return File.ReadAllText(path);
        }
        catch { return null; }
    }

    private static string TextOf(CodeRow r) { var sb = new System.Text.StringBuilder(); foreach (var t in r.Tokens) sb.Append(t.Text); return sb.ToString(); }

    // ---------------------------------------------------------------- rendering

    private void Render(IReadOnlyList<CodeRow> rows)
    {
        int count = Math.Min(rows.Count, MaxRows);
        int gutter = Math.Max(2, (count == 0 ? 1 : count).ToString().Length);
        double maxLen = 8;

        var doc = new FlowDocument { PagePadding = new Thickness(0), FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12.5 };
        for (int i = 0; i < count; i++)
        {
            var r = rows[i];
            var para = new Paragraph { Margin = new Thickness(0), Padding = new Thickness(0), LineHeight = 17, TextAlignment = TextAlignment.Left, KeepTogether = true };

            var num = (r.NewNo ?? r.OldNo)?.ToString() ?? "";
            char sign = r.Kind == DiffRowKind.Add ? '+' : r.Kind == DiffRowKind.Del ? '-' : ' ';
            para.Inlines.Add(new Run(num.PadLeft(gutter) + " ") { Foreground = GutterBrush });
            para.Inlines.Add(new Run(sign + " ") { Foreground = r.Kind == DiffRowKind.Add ? AddSign : r.Kind == DiffRowKind.Del ? DelSign : GutterBrush });

            int len = gutter + 3;
            foreach (var tk in r.Tokens)
            {
                if (tk.Text.Length == 0) continue;
                para.Inlines.Add(new Run(tk.Text) { Foreground = BrushFor(tk.Kind) });
                len += tk.Text.Length;
            }
            if (len > maxLen) maxLen = len;

            if (r.Kind == DiffRowKind.Add) { para.Background = AddBg; para.BorderBrush = AddSign; para.BorderThickness = new Thickness(3, 0, 0, 0); }
            else if (r.Kind == DiffRowKind.Del) { para.Background = DelBg; para.BorderBrush = DelSign; para.BorderThickness = new Thickness(3, 0, 0, 0); }
            else para.BorderThickness = new Thickness(3, 0, 0, 0);   // keep code aligned under the diff stripe

            doc.Blocks.Add(para);
        }

        if (rows.Count > MaxRows)
            doc.Blocks.Add(new Paragraph(new Run($"… {rows.Count - MaxRows} more lines not shown")) { Foreground = GutterBrush, Margin = new Thickness(0, 6, 0, 0) });

        doc.PageWidth = Math.Max(760, maxLen * 7.7 + 48);   // wide enough to horizontal-scroll instead of wrap
        Code.Document = doc;
    }

    // ---------------------------------------------------------------- palette (VS Code Dark+ inspired)

    private static readonly Dictionary<string, SolidColorBrush> _brushes = new();
    private static SolidColorBrush Brush(string hex)
    {
        if (_brushes.TryGetValue(hex, out var b)) return b;
        b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); _brushes[hex] = b; return b;
    }

    private static readonly SolidColorBrush GutterBrush = Brush("#5B5E66");
    private static readonly SolidColorBrush AddSign = Brush("#5FB984");
    private static readonly SolidColorBrush DelSign = Brush("#E86A78");
    private static readonly SolidColorBrush AddBg = Brush("#2E7ED0A6");   // soft green wash (theme GreenSoft, a touch stronger)
    private static readonly SolidColorBrush DelBg = Brush("#22E86A78");   // soft red wash (theme RedSoft)

    private static SolidColorBrush BrushFor(TokKind k) => k switch
    {
        TokKind.Keyword => Brush("#569CD6"),
        TokKind.Control => Brush("#C586C0"),
        TokKind.Type => Brush("#4EC9B0"),
        TokKind.Function => Brush("#DCDCAA"),
        TokKind.String => Brush("#CE9178"),
        TokKind.Number => Brush("#B5CEA8"),
        TokKind.Comment => Brush("#6A9955"),
        TokKind.Tag => Brush("#569CD6"),
        TokKind.Attr => Brush("#9CDCFE"),
        TokKind.Meta => Brush("#C586C0"),
        TokKind.Punct => Brush("#C8CDD3"),
        _ => Brush("#D4D4D4"),
    };

    // ---------------------------------------------------------------- window chrome

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try { if (!string.IsNullOrEmpty(_copyText)) Clipboard.SetText(_copyText); } catch { /* clipboard busy */ }
    }
}
