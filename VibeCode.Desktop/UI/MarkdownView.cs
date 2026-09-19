using System.Windows;
using System.Windows.Documents;
using VibeCode.Services;

namespace VibeCode.UI;

/// <summary>
/// The transcript's markdown surface: MdXaml's viewer with its code blocks turned back into text.
/// <para>
/// MdXaml renders every fenced block as a BlockUIContainer holding an AvalonEdit TextEditor - an entire editor
/// control parked in the middle of the text flow. To the FlowDocument's own TextSelection that control is not
/// text at all. Dragging across a reply skips straight over it (the code never highlights, which is what makes
/// selecting a reply look broken), and every copy path - Ctrl+C, the right-click "Copy", the transcript's range
/// menu, all of which end up at the same selection - hands back the surrounding prose with a single space where
/// the code used to be. Copying a path or a command out of an answer, the most common thing anyone does with a
/// reply, silently produced nothing.
/// </para>
/// <para>
/// So the container is swapped for an ordinary Paragraph holding the same characters, coloured with the app's own
/// tokenizer. Nothing is lost by dropping the editor: measured on a real reply, MdXaml leaves its SyntaxHighlighting
/// null and its line numbers off, so it was an editor rendering plain unhighlighted text. Text in, text out -
/// selection, the highlight and every copy path work again because there is nothing there but text. A long
/// transcript also stops carrying one full editor control per code block.
/// </para>
/// </summary>
public partial class MarkdownView : MdXaml.MarkdownScrollViewer
{
    /// <summary>
    /// How many inlines all of one reply's code may add up to before the colouring is given up.
    /// <para>
    /// A FlowDocument lays out flat up to roughly 16 000 inlines and then falls off a cliff. Measured on this box at
    /// the transcript's 800 px width: a 1 600-line fence coloured token by token is 16 000 inlines and 124 ms, while
    /// a 2 000-line one is 20 000 inlines and 7.5 s. The shape is irrelevant - one Run per line hits the identical
    /// wall at the identical count. So colour is spent from a budget: token by token while there is room, then a
    /// single Run per line, which is nine times cheaper for the same characters. Only the colour is ever given up,
    /// never a character of the text - the whole point of this class is that the code can be selected and copied.
    /// </para>
    /// </summary>
    private const int InlineBudget = 8_000;

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        // Every Markdown change builds a whole new FlowDocument, so this is the one hook that catches all of
        // them - including the throttled snapshots a streaming reply renders as it arrives.
        if (e.Property != DocumentProperty || e.NewValue is not FlowDocument document) return;
        var budget = InlineBudget;
        Inline(document.Blocks, ref budget);
        ConfigureLinks(document);
    }

    /// <summary>
    /// Replace the code-block containers with text. Recursive: a fence can sit inside a list item, a blockquote or a
    /// table cell. The budget is shared across the whole document, because so is the layout.
    /// <para>
    /// EVERY collection walked here is snapshotted first, and that is load-bearing rather than defensive. A
    /// FlowDocument's collections all share one TextContainer, and a <see cref="TextElementCollection{T}"/> enumerator
    /// checks that container's generation on each step — so the InsertAfter/Remove below invalidates not just the
    /// collection being edited but every live enumerator anywhere in the document. Walking `list.ListItems` directly
    /// therefore threw "Collection was modified" as soon as one item held a fence, which is what a numbered procedure
    /// with a code block in a step looks like — an extremely ordinary agent reply. It surfaced as a
    /// XamlParseException out of template load, because that is where the first render happens.
    /// </para>
    /// </summary>
    private static void Inline(BlockCollection blocks, ref int budget)
    {
        foreach (var block in blocks.ToList())
        {
            switch (block)
            {
                // Only an AvalonEdit editor: MdXaml also uses BlockUIContainer for images, which must stay.
                case BlockUIContainer { Child: ICSharpCode.AvalonEdit.TextEditor editor } container:
                    blocks.InsertAfter(container, CodeParagraph(editor.Text, editor.Tag as string, ref budget));
                    blocks.Remove(container);
                    break;
                case Section section:
                    Inline(section.Blocks, ref budget);
                    break;
                case List list:
                    foreach (var item in list.ListItems.ToList()) Inline(item.Blocks, ref budget);
                    break;
                case Table table:
                    // Materialized in full before the first edit: the chain is lazy over three live collections,
                    // so without this the rows and cells are still being enumerated while a cell is being rewritten.
                    foreach (var cell in table.RowGroups.SelectMany(g => g.Rows).SelectMany(r => r.Cells).ToList())
                        Inline(cell.Blocks, ref budget);
                    break;
            }
        }
    }

    /// <summary>A fenced block as a code-styled paragraph. Resource references rather than fixed brushes, so the
    /// block follows a theme switch the way the rest of the transcript does.</summary>
    private static Paragraph CodeParagraph(string code, string? language, ref int budget)
    {
        var paragraph = new Paragraph
        {
            FontSize = 12,
            Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(0, 8, 0, 8),
            BorderThickness = new Thickness(1),
            TextIndent = 0,
            LineHeight = 17,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        };
        paragraph.SetResourceReference(TextElement.FontFamilyProperty, "Mono");
        paragraph.SetResourceReference(TextElement.ForegroundProperty, "Text");
        paragraph.SetResourceReference(TextElement.BackgroundProperty, "CodeBg");
        paragraph.SetResourceReference(Block.BorderBrushProperty, "BorderSoft");

        // The fence's closing newline is part of the editor's text and would otherwise render a blank last row.
        var lines = SyntaxHighlighter.Highlight((code ?? "").TrimEnd('\n', '\r'), FileNameFor(language));
        var tokens = lines.Sum(line => line.Count);
        var coloured = tokens + lines.Count <= budget;
        budget -= (coloured ? tokens : lines.Count) + lines.Count;

        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0) paragraph.Inlines.Add(new LineBreak());
            if (coloured)
            {
                foreach (var token in lines[i])
                    paragraph.Inlines.Add(new Run(token.Text) { Foreground = CodeViewerWindow.BrushFor(token.Kind) });
            }
            else if (lines[i].Count > 0)
            {
                // One Run for the line, in the paragraph's own foreground. The tokens partition the line exactly,
                // so concatenating them gives back the source character for character.
                paragraph.Inlines.Add(new Run(string.Concat(lines[i].Select(token => token.Text))));
            }
        }
        return paragraph;
    }

    /// <summary>The fence's language tag as a filename, because the tokenizer picks its language off an extension.
    /// Anything unrecognised falls through to the generic scanner - which is what an untagged fence gets anyway.</summary>
    private static string FileNameFor(string? language) => "fenced" + (language?.Trim().ToLowerInvariant() switch
    {
        "cs" or "c#" or "csharp" => ".cs",
        "ts" or "typescript" or "tsx" => ".ts",
        "js" or "javascript" or "jsx" or "mjs" or "cjs" or "node" => ".js",
        "py" or "python" or "python3" => ".py",
        "json" or "jsonc" or "json5" => ".json",
        "xml" or "html" or "htm" or "xaml" or "axaml" or "svg" or "csproj" => ".xml",
        "css" or "scss" or "less" or "sass" => ".css",
        "md" or "markdown" => ".md",
        _ => "",
    });
}
