using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using VibeCode.Terminal;

internal static partial class Program
{
    private static int RunTerminalRenderingChecks(bool baseline)
    {
        _root = Environment.GetEnvironmentVariable("VIBECODE_TEST_OUTPUT")
            ?? Path.Combine(Environment.CurrentDirectory, "artifacts", "terminal-typing-flicker");
        Directory.CreateDirectory(_root);
        try
        {
            using var fixture = new TerminalRenderFixture();
            var screen = new AnsiTestScreen(100, 32);
            screen.Apply(fixture.Draw());
            var original = screen.Text();
            fixture.Key(ConsoleKey.S, 's');
            var typed = fixture.Draw();
            screen.Apply(typed);
            var metrics = new { ansiBytes = Encoding.UTF8.GetByteCount(typed), rowsRepainted = screen.TouchedRows.Count,
                transcriptRowsRepainted = screen.TouchedRows.Count(row => row < screen.Height - 2) };
            File.WriteAllText(Path.Combine(_root, baseline ? "baseline.json" : "after.json"), JsonSerializer.Serialize(metrics));
            Console.WriteLine(JsonSerializer.Serialize(metrics));
            if (baseline)
            {
                Check("baseline reproduces transcript repaint on a single key", metrics.transcriptRowsRepainted > 0);
                return 0;
            }
            Check("typing repaints only the composer", screen.TouchedRows.SetEquals([screen.Height - 2]));
            Check("typing preserves every transcript and header row", screen.Text().Take(screen.Height - 2).SequenceEqual(original.Take(screen.Height - 2)));
            Check("typed character appears with the caret after it", screen.Text()[^2] == "> s" && screen.CursorX == 3 && screen.CursorY == screen.Height - 2);
            Check("typing emits a small patch without clearing the viewport", typed.Length < 200 && !typed.Contains("\u001b[J") && !typed.Contains("\u001b[2J"));

            fixture.Key(ConsoleKey.LeftArrow);
            screen.Apply(fixture.Draw());
            Check("moving the caret does not repaint text", screen.TouchedRows.Count == 0 && screen.CursorX == 2);
            fixture.Key(ConsoleKey.S, 's');
            screen.Apply(fixture.Draw());
            fixture.Key(ConsoleKey.Delete);
            screen.Apply(fixture.Draw());
            Check("delete clears the old suffix without touching history", screen.Text()[^2] == "> s" && screen.TouchedRows.SetEquals([screen.Height - 2]));
            fixture.Key(ConsoleKey.Backspace);
            screen.Apply(fixture.Draw());
            Check("backspace clears the last character", screen.Text()[^2] == ">");
            Check("an identical frame writes nothing", fixture.Draw().Length == 0);

            fixture.Field("_snapshot", fixture.Snapshot with { });
            Check("identical new snapshot objects do not repaint", fixture.Draw().Length == 0);
            fixture.Field("_notice", "Draft kept for retry.");
            screen.Apply(fixture.Draw());
            Check("a status change paints only the notice row", screen.TouchedRows.SetEquals([screen.Height - 3]) && screen.Text()[^3] == "Draft kept for retry.");
            fixture.Field("_notice", "");
            screen.Apply(fixture.Draw());

            fixture.Key(ConsoleKey.F2);
            screen.Apply(fixture.Draw());
            Check("model menu opens and hides the input caret", screen.Text().Any(row => row.Contains("model · next prompt")) && !screen.CursorVisible);
            var menuBefore = screen.Text();
            fixture.Key(ConsoleKey.DownArrow);
            screen.Apply(fixture.Draw());
            Check("menu navigation leaves unrelated transcript rows alone", screen.TouchedRows.All(row => row is >= 3 and <= 9)
                && screen.Text().Skip(10).SequenceEqual(menuBefore.Skip(10)));
            fixture.Key(ConsoleKey.Escape);
            screen.Apply(fixture.Draw());
            Check("closing a menu restores its underlying transcript", screen.Text().SequenceEqual(original) && screen.CursorVisible);

            fixture.Key(ConsoleKey.PageUp);
            screen.Apply(fixture.Draw());
            Check("scrolling moves history without repainting the header", !screen.TouchedRows.Any(row => row < 5) && !screen.Text().SequenceEqual(original));
            fixture.Key(ConsoleKey.Escape);
            screen.Apply(fixture.Draw());
            Check("returning to live output restores the frame", screen.Text().SequenceEqual(original));

            fixture.Field("_snapshot", fixture.Snapshot with { Rows = [.. fixture.Snapshot.Rows!, new(2, "Cedar", "reply", "NEW STREAMING TEXT")] });
            screen.Apply(fixture.Draw());
            Check("streamed text is rendered", screen.Text().Any(row => row.Contains("NEW STREAMING TEXT")));
            Check("streaming does not repaint unchanged header or draft", screen.TouchedRows.All(row => row >= 5 && row < screen.Height - 3));

            fixture.Field("_input", new string('x', 150) + "中🙂");
            fixture.Field("_cursor", 153);
            screen.Apply(fixture.Draw());
            Check("wide Unicode draft scrolls horizontally with its caret", screen.Text()[^2].EndsWith("中🙂") && screen.CursorX < screen.Width - 1);
            fixture.Field("_input", ""); fixture.Field("_cursor", 0);
            screen.Apply(fixture.Draw());
            Check("shortened draft clears its entire old tail", screen.Text()[^2] == ">");

            fixture.Field("_snapshot", fixture.Snapshot with { Attachments = [new("a", "photo.png", "image", "1 KB")] });
            screen.Apply(fixture.Draw());
            Check("attachment row appears above the notice", screen.Text()[^4].Contains("1 attached") && screen.Text()[^4].Contains("photo.png"));
            fixture.Field("_snapshot", fixture.Snapshot with { Attachments = [] });
            screen.Apply(fixture.Draw());
            Check("removing attachments restores transcript space", !screen.Text().Any(row => row.Contains("1 attached")));

            foreach (var (width, height) in new[] { (42, 18), (12, 10), (12, 8), (120, 45), (100, 32) })
            {
                fixture.Field("_width", width); fixture.Field("_height", height);
                screen = new AnsiTestScreen(width, height, stale: true);
                screen.Apply(fixture.Draw());
                Check($"resize {width}x{height} repaints every row without scrolling", screen.TouchedRows.Count == height && screen.Scrolls == 0);
                Check($"resize {width}x{height} places the caret in the composer", screen.CursorY == height - 2 && screen.CursorX == 2);
                var beforeKey = screen.Text();
                fixture.Key(ConsoleKey.A, 'a');
                screen.Apply(fixture.Draw());
                Check($"typing at {width}x{height} leaves the transcript untouched", screen.TouchedRows.SetEquals([height - 2])
                    && screen.Text().Take(height - 2).SequenceEqual(beforeKey.Take(height - 2)));
                fixture.Key(ConsoleKey.Backspace); screen.Apply(fixture.Draw());
                if (width == 12 && height == 10)
                {
                    fixture.Field("_snapshot", fixture.Snapshot with { Attachments = [new("a", "photo.png", "image", "1 KB")] });
                    screen.Apply(fixture.Draw());
                    Check("short narrow viewport keeps attachments and input visible", screen.Text()[^4].Contains("1 attached")
                        && screen.Text()[^2] == ">" && screen.Scrolls == 0);
                    fixture.Field("_snapshot", fixture.Snapshot with { Attachments = [] });
                    screen.Apply(fixture.Draw());
                }
            }

            fixture.Field("_overlay", "help"); fixture.Field("_scroll", int.MaxValue);
            screen.Apply(fixture.Draw());
            Check("help and both teammate-message commands still render", screen.Text().Any(row => row.Contains("/message") && row.Contains("/msg")));
            fixture.Field("_overlay", ""); fixture.Field("_scroll", 0);
            screen.Apply(fixture.Draw());
            var beforeDisconnect = screen.Text();
            fixture.Field("_connected", false); fixture.Field("_notice", "Disconnected. Draft kept.");
            screen.Apply(fixture.Draw());
            Check("disconnect updates controls and notice while preserving history", screen.Text().Skip(5).Take(24).SequenceEqual(beforeDisconnect.Skip(5).Take(24))
                && screen.Text()[^3] == "Disconnected. Draft kept.");

            Console.WriteLine($"PASS: {_checks} headless terminal rendering checks; no console allocation, native input, windows or provider calls.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private sealed class TerminalRenderFixture : IDisposable
    {
        private readonly NamedPipeClientStream _pipe = new(TerminalWire.PipePrefix + "render-fixture");
        private readonly object _client;
        public TerminalPacket Snapshot => (TerminalPacket)typeof(TerminalConsole).GetField("_snapshot", Flags)!.GetValue(_client)!;

        public TerminalRenderFixture()
        {
            _client = typeof(TerminalConsole).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
                null, [typeof(NamedPipeClientStream)], null)!.Invoke([_pipe]);
            Field("_width", 100); Field("_height", 32); Field("_notice", "");
            Field("_snapshot", new TerminalPacket("snapshot", "Peer communication on", Project: @"C:\work\fixture",
                Agents: [new(1, "Cedar", "Claude", "fixture", "running")],
                Rows: [new(1, "Cedar", "reply", string.Join('\n', Enumerable.Range(0, 70).Select(i => $"Stable transcript line {i:D2}: reply, 6 ms, TTL 56")))],
                Controls: new("first", "First model", [new("first", "First model"), new("second", "Second model"), new("third", "Third model")],
                    "high", "High", [new("low", "Low"), new("high", "High")], false, true),
                Speech: new("idle", "F5 to dictate"), Attachments: []));
        }

        public void Field(string name, object value) => typeof(TerminalConsole).GetField(name, Flags)!.SetValue(_client, value);
        public void Key(ConsoleKey key, char value = '\0') => Call(_client, "HandleKey", new ConsoleKeyInfo(value, key, false, false, false));
        public string Draw()
        {
            var previous = Console.Out;
            using var output = new StringWriter(CultureInfo.InvariantCulture);
            try { Console.SetOut(output); Call(_client, "Draw"); return output.ToString(); }
            finally { Console.SetOut(previous); }
        }
        public void Dispose()
        {
            _pipe.Dispose();
            ((AutoResetEvent)typeof(TerminalConsole).GetField("_changed", Flags)!.GetValue(_client)!).Dispose();
        }
    }

    // Minimal independent terminal model: writes and erases are tracked even when the final cells match.
    // That catches the reported flicker, which a screenshot of a finished full repaint cannot detect.
    private sealed class AnsiTestScreen
    {
        private readonly string[,] _cells;
        public int Width { get; }
        public int Height { get; }
        public int CursorX { get; private set; }
        public int CursorY { get; private set; }
        public bool CursorVisible { get; private set; } = true;
        public int Scrolls { get; private set; }
        public HashSet<int> TouchedRows { get; } = [];
        public AnsiTestScreen(int width, int height, bool stale = false)
        {
            Width = width; Height = height; _cells = new string[height, width];
            for (var y = 0; y < height; y++) for (var x = 0; x < width; x++) _cells[y, x] = stale ? "#" : " ";
        }
        public string[] Text() => Enumerable.Range(0, Height)
            .Select(y => string.Concat(Enumerable.Range(0, Width).Select(x => _cells[y, x])).TrimEnd()).ToArray();
        public void Apply(string output)
        {
            TouchedRows.Clear();
            for (var i = 0; i < output.Length;)
            {
                var value = output[i++];
                if (value == '\u001b')
                {
                    if (output[i++] != '[') throw new Exception("Unexpected terminal escape.");
                    var start = i;
                    while (i < output.Length && output[i] is not (>= '@' and <= '~')) i++;
                    var parameters = output[start..i]; var command = output[i++];
                    if (parameters == "?25") { CursorVisible = command == 'h'; continue; }
                    var numbers = parameters.Split(';').Select(v => int.TryParse(v, out var n) ? n : 0).ToArray();
                    switch (command)
                    {
                        case 'H': CursorY = Math.Clamp(numbers[0] - 1, 0, Height - 1); CursorX = Math.Clamp((numbers.Length > 1 ? numbers[1] : 1) - 1, 0, Width - 1); break;
                        case 'G': CursorX = Math.Clamp(numbers[0] - 1, 0, Width - 1); break;
                        case 'm': break;
                        case 'K': Erase(CursorY, numbers[0] == 2 ? 0 : CursorX); break;
                        case 'J':
                            for (var y = numbers[0] == 2 ? 0 : CursorY; y < Height; y++) Erase(y, numbers[0] == 2 || y != CursorY ? 0 : CursorX);
                            break;
                        default: throw new Exception("Unhandled terminal command: " + command);
                    }
                    continue;
                }
                if (value == '\r') { CursorX = 0; continue; }
                if (value == '\n') { CursorY++; if (CursorY >= Height) { Scrolls++; CursorY = Height - 1; } continue; }
                var rune = char.IsHighSurrogate(value) ? new Rune(value, output[i++]) : new Rune(value);
                // The fixtures use ASCII, UI symbols, and these explicit double-width glyphs.
                var cells = rune.Value is 0x4e2d or 0x1f642 ? 2 : 1;
                if (CursorX >= Width) { CursorX = 0; CursorY++; }
                if (CursorY >= Height) { Scrolls++; CursorY = Height - 1; }
                TouchedRows.Add(CursorY);
                _cells[CursorY, CursorX++] = rune.ToString();
                if (cells == 2 && CursorX < Width) _cells[CursorY, CursorX++] = "";
            }
        }
        private void Erase(int row, int from)
        {
            TouchedRows.Add(row);
            for (var x = from; x < Width; x++) _cells[row, x] = " ";
        }
    }
}
