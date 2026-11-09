using System.Text;

namespace VibeCode.Terminal;

public sealed partial class TerminalConsole
{
    private string[] _paintedRows = [];
    private int _paintedWidth, _paintedHeight;
    private (int Row, int Column, bool Visible) _paintedCursor;

    private void PaintFrame(List<string> frame, int cursorRow, int cursorColumn, bool cursorVisible)
    {
        var resized = _paintedWidth != _width || _paintedHeight != _height;
        var output = new StringBuilder();
        var painting = false;
        for (var row = 0; row < frame.Count; row++)
        {
            if (!resized && row < _paintedRows.Length && frame[row] == _paintedRows[row]) continue;
            if (!painting) { output.Append("\u001b[?25l"); painting = true; }
            // Absolute positions avoid newline scrolling. Write replacement text before clearing its old tail,
            // and never clear unchanged history: a keypress normally paints only the composer.
            output.Append("\u001b[").Append(row + 1).Append(";1H\u001b[0m\u001b[40m").Append(frame[row]);
        }
        var cursor = (cursorRow, cursorColumn, cursorVisible);
        if (painting || cursor != _paintedCursor)
        {
            output.Append("\u001b[").Append(cursorRow).Append(';').Append(cursorColumn).Append('H');
            output.Append(cursorVisible ? "\u001b[?25h" : "\u001b[?25l");
        }
        if (output.Length == 0) return;
        Console.Write(output.ToString());
        _paintedRows = frame.ToArray();
        _paintedWidth = _width; _paintedHeight = _height; _paintedCursor = cursor;
    }
}
