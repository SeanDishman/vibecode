using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace VibeCode.Terminal;

public sealed partial class TerminalConsole
{
    private int _selectionAnchor = -1, _escapeSelectionAnchor = -1;
    private bool _pasteOverflowed;
    private Func<string, bool> _copyToClipboard = text =>
    {
        try { System.Windows.Clipboard.SetText(text); return true; }
        catch (ExternalException) { return false; }
    };
    private int SelectionStart => _selectionAnchor < 0 ? _cursor : Math.Min(_cursor, _selectionAnchor);
    private int SelectionLength => _selectionAnchor < 0 ? 0 : Math.Abs(_cursor - _selectionAnchor);

    private void MoveDraftCursor(int position, bool extend)
    {
        if (extend) { if (_selectionAnchor < 0) _selectionAnchor = _cursor; }
        else _selectionAnchor = -1;
        _cursor = position;
    }

    private int PreviousDraftPosition(int position) => position <= 0 ? 0
        : position >= 2 && char.IsLowSurrogate(_input[position - 1]) && char.IsHighSurrogate(_input[position - 2]) ? position - 2 : position - 1;

    private int NextDraftPosition(int position) => position >= _input.Length ? _input.Length
        : position + 1 < _input.Length && char.IsHighSurrogate(_input[position]) && char.IsLowSurrogate(_input[position + 1]) ? position + 2 : position + 1;

    private bool DeleteSelection()
    {
        if (SelectionLength == 0) return false;
        var start = SelectionStart;
        _input = _input.Remove(start, SelectionLength);
        _cursor = start; _selectionAnchor = -1;
        return true;
    }

    private void CopySelection(bool cut)
    {
        if (SelectionLength == 0) return;
        if (!_copyToClipboard(_input.Substring(SelectionStart, SelectionLength)))
        { _notice = "Clipboard is busy. Your selection is kept; try copying again."; return; }
        if (cut) DeleteSelection();
        _notice = cut ? "Selection cut." : "Selection copied.";
    }

    // Conhost pastes can arrive as ordinary key records. Insert each contiguous text run once instead of
    // allocating a new copy of the entire draft for every character. Shortcuts retain their original order.
    private void HandleKeyBatch(ConsoleKeyInfo[] keys, int count)
    {
        var text = new StringBuilder(count);
        void Flush()
        {
            if (text.Length == 0) return;
            Insert(text.ToString()); text.Clear(); _dirty = true;
        }
        for (var i = 0; i < count && !_exit; i++)
        {
            var key = keys[i];
            if (!_pasting && _escape.Length == 0 && !HasPending && _openControl < 0 && _controlFocus < 0
                && (key.Modifiers & (ConsoleModifiers.Control | ConsoleModifiers.Alt)) == 0 && !char.IsControl(key.KeyChar))
                text.Append(key.KeyChar);
            else { Flush(); HandleKey(key); }
        }
        Flush();
    }

    private Rune DraftRune(int position, out int length)
    {
        var next = NextDraftPosition(position);
        length = next - position;
        if (length == 2) return new Rune(_input[position], _input[position + 1]);
        return Rune.TryCreate(_input[position], out var rune) ? rune : Rune.ReplacementChar;
    }

    private static int DraftCells(Rune rune) => rune.Value switch
    {
        '\n' => 1,
        '\t' => 4,
        _ => Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format ? 0 : RuneWidth(rune)
    };

    private (string Text, int CursorCells) DraftViewport(int available)
    {
        // Locate the visible suffix by walking back from the caret. A long paste must not make every frame
        // sanitize and measure the whole draft (and allocate strings for every rune) under the input lock.
        var start = Math.Clamp(_cursor, 0, _input.Length);
        var cursorCells = 0;
        while (start > 0)
        {
            var previous = PreviousDraftPosition(start);
            var cells = DraftCells(DraftRune(previous, out _));
            if (cursorCells + cells >= available) break;
            start = previous; cursorCells += cells;
        }
        var output = new StringBuilder(available + 20);
        var used = 0;
        var highlighted = false;
        var selectionStart = SelectionStart;
        var selectionEnd = selectionStart + SelectionLength;
        for (var position = start; position < _input.Length;)
        {
            var rune = DraftRune(position, out var length);
            var cells = DraftCells(rune);
            if (used + cells > available) break;
            var category = Rune.GetUnicodeCategory(rune);
            if (rune.Value is '\n' or '\t' || category is not (UnicodeCategory.Control or UnicodeCategory.Format))
            {
                var selected = position >= selectionStart && position < selectionEnd;
                if (selected != highlighted) { output.Append(selected ? "\u001b[7m" : "\u001b[27m"); highlighted = selected; }
                if (rune.Value == '\n') output.Append('↵');
                else if (rune.Value == '\t') output.Append("    ");
                else output.Append(rune.ToString());
                used += cells;
            }
            position += length;
        }
        if (highlighted) output.Append("\u001b[27m");
        return (output.ToString(), cursorCells);
    }
}
