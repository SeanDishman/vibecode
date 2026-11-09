using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using VibeCode.Terminal;

internal static partial class Program
{
    private static int RunTerminalInputChecks(bool baseline)
    {
        _root = Environment.GetEnvironmentVariable("VIBECODE_TEST_OUTPUT")
            ?? Path.Combine(Environment.CurrentDirectory, "artifacts", "terminal-input-editing");
        Directory.CreateDirectory(_root);
        try
        {
            if (!baseline) VerifyTerminalInputEditing();
            using (var warmup = new TerminalInputFixture()) { warmup.Feed(new string('w', 256)); warmup.Draw(); }
            var measurements = new List<object>();
            foreach (var length in new[] { 4096, 32768 })
            {
                using var fixture = new TerminalInputFixture();
                fixture.Draw();
                var paste = new string('x', length);
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                var watch = Stopwatch.StartNew();
                fixture.Feed(paste);
                watch.Stop();
                var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
                measurements.Add(new { operation = "native paste", length, elapsedMs = watch.Elapsed.TotalMilliseconds, allocatedBytes = bytes });
                Check($"{length}-character paste stays exact and unsent", fixture.Input == paste && fixture.Value<string>("_pending") == "");
                if (!baseline) Check($"{length}-character paste avoids per-character draft copies", bytes < length * 512L);
                allocated = GC.GetAllocatedBytesForCurrentThread(); watch.Restart();
                for (var i = 0; i < 20; i++) fixture.Draw();
                watch.Stop(); bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
                measurements.Add(new { operation = "20 draft frames", length, elapsedMs = watch.Elapsed.TotalMilliseconds, allocatedBytes = bytes });
                if (!baseline) Check($"render cost stays bounded with {length} characters", bytes < 2_000_000);
            }
            File.WriteAllText(Path.Combine(_root, baseline ? "baseline.json" : "after.json"), JsonSerializer.Serialize(measurements, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(JsonSerializer.Serialize(measurements));
            Console.WriteLine($"PASS: {_checks} headless input checks; no console allocation, windows or provider calls.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void VerifyTerminalInputEditing()
    {
        using var fixture = new TerminalInputFixture(connected: true);
        var screen = new AnsiTestScreen(100, 32);
        fixture.Draft("Select the whole draft");
        screen.Apply(fixture.Draw());
        var history = screen.Text().Take(30).ToArray();
        fixture.Key(ConsoleKey.A, '\u0001', ctrl: true);
        var selected = fixture.Draw(); screen.Apply(selected);
        Check("Ctrl+A selects and visibly highlights the entire draft", fixture.Selection == (0, fixture.Input.Length)
            && selected.Contains("\u001b[7m") && selected.Contains("\u001b[27m") && screen.TouchedRows.SetEquals([30]));
        Check("selecting the draft does not repaint transcript rows", screen.Text().Take(30).SequenceEqual(history));
        fixture.Key(ConsoleKey.X, 'x');
        Check("typing replaces the selected draft", fixture.Input == "x" && fixture.Selection.Length == 0);
        foreach (var key in new[] { ConsoleKey.Delete, ConsoleKey.Backspace })
        {
            fixture.Draft("Delete all of this"); fixture.Key(ConsoleKey.A, '\u0001', ctrl: true); fixture.Key(key);
            Check($"{key} removes the selection", fixture.Input == "" && fixture.Value<int>("_cursor") == 0);
        }
        fixture.Draft("abc"); fixture.Key(ConsoleKey.A, '\u0001', ctrl: true); fixture.Key(ConsoleKey.LeftArrow); fixture.Key(ConsoleKey.X, 'x');
        Check("Left collapses selection to its start", fixture.Input == "xabc");
        fixture.Key(ConsoleKey.A, '\u0001', ctrl: true); fixture.Key(ConsoleKey.RightArrow); fixture.Key(ConsoleKey.Y, 'y');
        Check("Right collapses selection to its end", fixture.Input == "xabcy");
        fixture.Draft("A🙂B"); fixture.Key(ConsoleKey.Home); fixture.Key(ConsoleKey.RightArrow, shift: true); fixture.Key(ConsoleKey.RightArrow, shift: true);
        Check("Shift+arrows select Unicode without splitting surrogate pairs", fixture.Selection == (0, 3));
        fixture.Key(ConsoleKey.Delete);
        Check("deleting a Unicode selection preserves the remaining text", fixture.Input == "B");
        fixture.Draft("abcdef", 2); fixture.Key(ConsoleKey.End, shift: true); fixture.Key(ConsoleKey.Q, 'Q');
        Check("Shift+End replacement starts at the selection anchor", fixture.Input == "abQ");
        fixture.Key(ConsoleKey.Home, shift: true); fixture.Key(ConsoleKey.Backspace);
        Check("Shift+Home selects the prefix", fixture.Input == "");
        fixture.Draft("abc"); fixture.Key(ConsoleKey.LeftArrow, shift: true); fixture.Key(ConsoleKey.RightArrow, shift: true);
        fixture.Key(ConsoleKey.Backspace); fixture.Key(ConsoleKey.X, 'x');
        Check("backspace after a zero-length selection leaves a valid caret", fixture.Input == "abx" && fixture.Selection.Length == 0);

        var copied = "";
        fixture.Set("_copyToClipboard", new Func<string, bool>(text => { copied = text; return true; }));
        fixture.Draft("copy\n🙂text"); fixture.Key(ConsoleKey.A, '\u0001', ctrl: true); fixture.Key(ConsoleKey.C, '\u0003', ctrl: true);
        Check("Ctrl+C copies the selected raw text without stopping the team", copied == "copy\n🙂text" && fixture.Input == copied
            && fixture.Value<string>("_notice") == "Selection copied.");
        fixture.Key(ConsoleKey.X, '\u0018', ctrl: true);
        Check("Ctrl+X cuts after a successful clipboard write", fixture.Input == "" && copied == "copy\n🙂text");
        fixture.Draft("keep on clipboard failure"); fixture.Key(ConsoleKey.A, '\u0001', ctrl: true);
        fixture.Set("_copyToClipboard", new Func<string, bool>(_ => false)); fixture.Key(ConsoleKey.X, '\u0018', ctrl: true);
        Check("clipboard contention keeps the selected draft", fixture.Input == "keep on clipboard failure" && fixture.Selection.Length == fixture.Input.Length);
        fixture.Key(ConsoleKey.Escape, '\u001b');
        Check("Escape clears draft selection", fixture.Selection.Length == 0);
        fixture.Key(ConsoleKey.C, '\u0003', ctrl: true);
        Check("Ctrl+C without selection retains the stop-team shortcut", fixture.Read() is { Type: "input", Text: "/stop" });

        fixture.Draft("old text"); fixture.Key(ConsoleKey.A, '\u0001', ctrl: true);
        fixture.Clipboard("first\r\nsecond\rthird");
        Check("clipboard text replaces selection and normalizes newlines", fixture.Input == "first\nsecond\nthird" && fixture.Value<string>("_pending") == "");
        fixture.Draft(new string('o', TerminalWire.MaxPromptLength)); fixture.Key(ConsoleKey.A, '\u0001', ctrl: true);
        fixture.Clipboard(new string('n', TerminalWire.MaxPromptLength));
        Check("full-size clipboard replacement counts only retained text toward the limit", fixture.Input == new string('n', TerminalWire.MaxPromptLength));
        fixture.Draft("keep"); fixture.Key(ConsoleKey.A, '\u0001', ctrl: true); fixture.Clipboard(new string('x', TerminalWire.MaxPromptLength + 1));
        Check("oversized clipboard text keeps the draft and selection", fixture.Input == "keep" && fixture.Selection == (0, 4));
        fixture.Feed("\u001b[200~one\r\ntwo🙂\u001b[201~");
        Check("bracketed paste replaces selection as one unsent multiline draft", fixture.Input == "one\ntwo🙂" && fixture.Value<string>("_pending") == "");
        fixture.Draft("preserve me"); fixture.Key(ConsoleKey.A, '\u0001', ctrl: true);
        fixture.Feed("\u001b[200~" + new string('x', TerminalWire.MaxPromptLength + 1) + "\u001b[201~");
        Check("oversized bracketed paste never silently truncates or removes the selection", fixture.Input == "preserve me"
            && fixture.Selection == (0, 11) && fixture.Value<string>("_notice").Contains("limit"));
        fixture.Draft("");
        fixture.Keys([new('a', ConsoleKey.A, false, false, false), new('b', ConsoleKey.B, false, false, false),
            new('\0', ConsoleKey.LeftArrow, false, false, false), new('Z', ConsoleKey.Z, false, false, false)]);
        Check("batched text and navigation retain their event order", fixture.Input == "aZb");
        fixture.Key(ConsoleKey.A, '\u0001', ctrl: true); fixture.Key(ConsoleKey.Enter, '\r', shift: true);
        Check("Shift+Enter replaces selection with an unsent newline", fixture.Input == "\n" && fixture.Value<string>("_pending") == "");

        fixture.Draft("old speech"); fixture.Key(ConsoleKey.A, '\u0001', ctrl: true); fixture.Set("_heldDictation", "new speech");
        fixture.Invoke("InsertDictation");
        Check("dictation replaces selected text without leftover spacing", fixture.Input == "new speech");
        fixture.Draft("full draft"); fixture.Key(ConsoleKey.A, '\u0001', ctrl: true); fixture.Key(ConsoleKey.Enter, '\r');
        Check("Enter sends the full draft once and clears selection", fixture.Read() is { Type: "input", Text: "full draft" }
            && fixture.Input == "" && fixture.Selection.Length == 0);
        fixture.Key(ConsoleKey.A, '\u0001', ctrl: true); fixture.Feed("cannot edit a pending send");
        Check("pending send still blocks draft edits", fixture.Input == "" && fixture.Selection.Length == 0 && fixture.Value<string>("_pending") == "full draft");
        fixture.Set("_pending", ""); fixture.Key(ConsoleKey.UpArrow);
        Check("history recall restores text without stale selection", fixture.Input == "full draft" && fixture.Selection.Length == 0);

        fixture.Draft(new string('x', 30_000) + "中🙂tail\n\tend");
        screen.Apply(fixture.Draw());
        Check("long draft rendering shows Unicode, newline and tab near the caret", screen.Text()[^2].EndsWith("中🙂tail↵    end") && screen.CursorX < 99);
        fixture.Key(ConsoleKey.Home); screen.Apply(fixture.Draw());
        Check("Home renders the start of a long draft immediately", screen.Text()[^2].StartsWith("> xxxxx") && screen.CursorX == 2);
        fixture.Draft("safe\u001b[2J\u202e\u0007text");
        var safe = fixture.Draw(); screen.Apply(safe);
        Check("bounded rendering still strips untrusted terminal and bidi controls", !safe.Contains("\u001b[2J") && !safe.Contains('\u202e')
            && !safe.Contains('\u0007') && screen.Text()[^2] == "> safe[2Jtext");
        fixture.Set("_snapshot", new TerminalPacket("snapshot", Project: "Files", ContextId: "same",
            Attachments: [new("file", "long-text.txt", "text", "32 KB")]));
        fixture.Draft("selected draft"); fixture.Key(ConsoleKey.A, '\u0001', ctrl: true); fixture.Clipboard("replacement", "same");
        Check("editing a draft with a pasted file leaves its attachment intact", fixture.Input == "replacement"
            && fixture.Value<TerminalPacket>("_snapshot").Attachments is [{ Id: "file" }]);
        fixture.Clipboard("stale text", "different");
        Check("clipboard responses from another chat cannot alter this draft", fixture.Input == "replacement");
    }

    private sealed class TerminalInputFixture : IDisposable
    {
        private readonly NamedPipeClientStream _pipe;
        private readonly NamedPipeServerStream? _server;
        private Task<TerminalPacket?>? _reading;
        private readonly object _client;
        private readonly Action<ConsoleKeyInfo> _key;
        private readonly Action _draw;
        private readonly Action<ConsoleKeyInfo[], int>? _batch;
        public string Input => Value<string>("_input");
        public (int Start, int Length) Selection => ((int)typeof(TerminalConsole).GetProperty("SelectionStart", Flags)!.GetValue(_client)!,
            (int)typeof(TerminalConsole).GetProperty("SelectionLength", Flags)!.GetValue(_client)!);
        public TerminalInputFixture(bool connected = false)
        {
            var name = TerminalWire.PipePrefix + Guid.NewGuid().ToString("N");
            _pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            if (connected)
            {
                _server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                var wait = _server.WaitForConnectionAsync(); _pipe.Connect(1000); wait.GetAwaiter().GetResult();
                _reading = TerminalWire.ReadAsync(_server);
            }
            _client = typeof(TerminalConsole).GetConstructor(Flags, null, [typeof(NamedPipeClientStream)], null)!.Invoke([_pipe]);
            _key = (Action<ConsoleKeyInfo>)typeof(TerminalConsole).GetMethod("HandleKey", Flags)!.CreateDelegate(typeof(Action<ConsoleKeyInfo>), _client);
            _draw = (Action)typeof(TerminalConsole).GetMethod("Draw", Flags)!.CreateDelegate(typeof(Action), _client);
            _batch = typeof(TerminalConsole).GetMethod("HandleKeyBatch", Flags)?.CreateDelegate(typeof(Action<ConsoleKeyInfo[], int>), _client) as Action<ConsoleKeyInfo[], int>;
            Set("_width", 100); Set("_height", 32); Set("_notice", "");
            Set("_snapshot", new TerminalPacket("snapshot", "Peer communication on", Project: "Input fixture",
                Rows: [new(1, "Agent", "reply", "History remains untouched while editing.")], Attachments: []));
        }
        public T Value<T>(string name) => (T)typeof(TerminalConsole).GetField(name, Flags)!.GetValue(_client)!;
        public void Set(string name, object value) => typeof(TerminalConsole).GetField(name, Flags)!.SetValue(_client, value);
        public void Key(ConsoleKey key, char value = '\0', bool shift = false, bool ctrl = false) => _key(new(value, key, shift, false, ctrl));
        public void Draft(string value, int? cursor = null)
        { Set("_input", value); Set("_cursor", cursor ?? value.Length); Set("_selectionAnchor", -1); Set("_escape", ""); }
        public void Keys(ConsoleKeyInfo[] keys) { if (_batch is not null) _batch(keys, keys.Length); else foreach (var key in keys) _key(key); }
        public void Clipboard(string text, string context = "") => Call(_client, "ReceiveAttachments", new TerminalPacket("clipboard-text", text, ContextId: context));
        public void Invoke(string name) => Call(_client, name);
        public TerminalPacket Read()
        {
            var packet = _reading!.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult()!;
            _reading = TerminalWire.ReadAsync(_server!);
            return packet;
        }
        public void Feed(string text)
        {
            var keys = new ConsoleKeyInfo[128];
            for (var start = 0; start < text.Length; start += keys.Length)
            {
                var count = Math.Min(keys.Length, text.Length - start);
                for (var i = 0; i < count; i++) keys[i] = new(text[start + i], ConsoleKey.X, false, false, false);
                if (_batch is null) { for (var i = 0; i < count; i++) _key(keys[i]); }
                else _batch(keys, count);
                Draw();
            }
        }
        public string Draw()
        {
            var previous = Console.Out;
            using var output = new StringWriter(CultureInfo.InvariantCulture);
            try { Console.SetOut(output); _draw(); return output.ToString(); }
            finally { Console.SetOut(previous); }
        }
        public void Dispose()
        {
            _pipe.Dispose();
            _server?.Dispose();
            Value<AutoResetEvent>("_changed").Dispose();
        }
    }
}
