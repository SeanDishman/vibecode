using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace VibeCode.Terminal;

/// <summary>The real console client. This entry point never constructs an Application or a WPF window.</summary>
public sealed partial class TerminalConsole
{
    private const string Green = "\u001b[38;2;118;236;161m";
    private const string Text = "\u001b[38;2;211;233;218m";
    private const string Muted = "\u001b[38;2;139;168;148m";
    private const string Amber = "\u001b[38;2;255;208;128m";
    private const string Red = "\u001b[38;2;255;150;150m";
    private readonly object _gate = new();
    private readonly AutoResetEvent _changed = new(false);
    private readonly NamedPipeClientStream _pipe;
    private readonly List<string> _history = [];
    private TerminalPacket _snapshot = new("snapshot");
    private string _input = "", _notice = "Connecting to VibeCode…", _pending = "", _overlay = "";
    private int _cursor, _historyIndex, _scroll;
    private long _clearedThrough;
    private bool _dirty = true, _connected = true, _exit;
    private int _width, _height;
    private string _escape = "";
    private readonly StringBuilder _paste = new();
    private bool _pasting;
    private DateTime _escapeUntil;
    private TerminalPacket? _wrappedSnapshot;
    private string _wrappedOverlay = "";
    private int _wrappedWidth;
    private long _wrappedClearedThrough;
    private List<(string, string)> _wrappedLines = [];

    private TerminalConsole(NamedPipeClientStream pipe) => _pipe = pipe;

    public static int Run(string[] args)
    {
        var index = Array.IndexOf(args, "--vibecode-terminal");
        if (index < 0 || index + 1 >= args.Length || !args[index + 1].StartsWith(TerminalWire.PipePrefix, StringComparison.Ordinal)) return 2;
        if (!OperatingSystem.IsWindows()) return 2;
        // Also detach when invoked from a shell: this mode owns a separate console window.
        Native.FreeConsole();
        Native.AllocConsole();
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
        Console.SetIn(new StreamReader(Console.OpenStandardInput(), Encoding.UTF8));
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;
        Console.Title = "Vibe Code Terminal";
        Console.TreatControlCAsInput = true;
        var output = Native.GetStdHandle(-11);
        Native.GetConsoleMode(output, out var mode);
        Native.SetConsoleMode(output, mode | 4); // ENABLE_VIRTUAL_TERMINAL_PROCESSING
        var input = Native.GetStdHandle(-10);
        Native.GetConsoleMode(input, out var inputMode);
        Native.SetConsoleMode(input, (inputMode | 0x98) & ~0x60u); // WINDOW_INPUT + MOUSE_INPUT + EXTENDED_FLAGS; no Quick Edit pause
        using var icon = new ConsoleIdentity();
        using var pipe = new NamedPipeClientStream(".", args[index + 1], PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            pipe.Connect(20_000);
            TerminalWire.WriteAsync(pipe, new("hello")).GetAwaiter().GetResult();
            Console.Write("\u001b[?1049h\u001b[40m\u001b[2J\u001b[?2004h");
            var client = new TerminalConsole(pipe);
            _ = client.ReceiveAsync();
            client.InputLoop();
            return 0;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
        {
            Console.WriteLine("Vibe Code Terminal could not connect: " + TerminalWire.DisplayText(ex.Message));
            Console.WriteLine("Open VibeCode and choose Settings > Developer > CLI again.");
            return 1;
        }
        finally
        {
            Console.Write("\u001b[?2004l\u001b[0m\u001b[?25h\u001b[?1049l");
            Native.SetConsoleMode(input, inputMode);
        }
    }

    private async Task ReceiveAsync()
    {
        try
        {
            while (await TerminalWire.ReadAsync(_pipe) is { } packet)
            {
                lock (_gate)
                {
                    if (packet.Type == "snapshot")
                    {
                        if (_snapshot.Project.Length == 0) _notice = "";
                        if (packet.ContextId != _snapshot.ContextId) { _dictationPending = false; _heldDictation = ""; _attachmentPending = false; }
                        _snapshot = packet;
                    }
                    else if (packet.Type is "dictation-state" or "dictation-result") ReceiveDictation(packet);
                    else if (packet.Type is "attachments" or "clipboard-text") ReceiveAttachments(packet);
                    else if (packet.Type == "settings")
                    {
                        _settingPending = false;
                        _notice = packet.Text;
                        if (packet.Controls is not null) _snapshot = _snapshot with { Controls = packet.Controls };
                    }
                    else if (packet.Type == "ack")
                    {
                        _notice = packet.Text;
                        if (!packet.Accepted) { _input = _pending; _cursor = _input.Length; _selectionAnchor = -1; }
                        if (packet.Attachments is not null && packet.ContextId == _snapshot.ContextId)
                            _snapshot = _snapshot with { Attachments = packet.Attachments };
                        _pending = "";
                        _sendingAttachments = false;
                    }
                    else if (packet.Type == "focus")
                    {
                        var window = Native.GetConsoleWindow();
                        Native.ShowWindow(window, 9);
                        Native.SetForegroundWindow(window);
                    }
                    _dirty = true;
                    _changed.Set();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or System.Text.Json.JsonException) { }
        finally
        {
            lock (_gate)
            {
                if (!_pasting && _escape.Length > 0 && DateTime.UtcNow > _escapeUntil) _escape = "";
                _connected = false;
                _settingPending = false;
                _dictationPending = false;
                _attachmentPending = _sendingAttachments = false;
                _openControl = -1;
                _notice = "VibeCode disconnected. Press Enter to close; saved conversations remain in the desktop.";
                if (_pending.Length > 0) { _input = _pending; _cursor = _input.Length; _pending = ""; _selectionAnchor = -1; }
                _dirty = true;
                _changed.Set();
            }
        }
    }

    private void InputLoop()
    {
        var input = Native.GetStdHandle(-10);
        var handles = new[] { input, _changed.SafeWaitHandle.DangerousGetHandle() };
        var events = new Native.InputRecord[1024];
        var keys = new ConsoleKeyInfo[events.Length];
        while (!_exit)
        {
            lock (_gate)
            {
                if (!_pasting && _escape.Length > 0 && DateTime.UtcNow > _escapeUntil) _escape = "";
                var width = Math.Max(12, Console.WindowWidth);
                var height = Math.Max(8, Console.WindowHeight);
                if (width != _width || height != _height) { _width = width; _height = height; _dirty = true; }
                if (_dirty) { Draw(); _dirty = false; }
            }
            // The console input handle signals for keys and resize events. The other handle signals snapshots
            // and acknowledgements. No keyboard/status polling or redraw timer runs while idle.
            var signaled = Native.WaitForMultipleObjects(2, handles, false, uint.MaxValue);
            if (signaled == 1) continue;
            if (signaled != 0) throw new IOException("The console input handle closed.");
            if (!Native.ReadConsoleInput(input, events, (uint)events.Length, out var count))
                throw new IOException("The console input handle closed.");
            lock (_gate)
            {
                var keyCount = 0;
                void FlushKeys() { if (keyCount > 0) HandleKeyBatch(keys, keyCount); keyCount = 0; }
                for (var i = 0; i < count && !_exit; i++)
                {
                    var item = events[i];
                    if (item.EventType == 4) { _dirty = true; continue; }
                    if (item.EventType == 2)
                    {
                        FlushKeys();
                        HandleMouse(item.MouseX - Console.WindowLeft, item.MouseY - Console.WindowTop, item.MouseButtons, item.MouseFlags);
                        continue;
                    }
                    if (item.EventType != 1 || item.Down == 0) continue;
                    var key = new ConsoleKeyInfo(item.Character, (ConsoleKey)item.VirtualKey,
                        (item.Modifiers & 0x10) != 0, (item.Modifiers & 3) != 0, (item.Modifiers & 12) != 0);
                    for (var repeat = 0; repeat < Math.Max(1, (int)item.Repeat) && !_exit; repeat++)
                    {
                        keys[keyCount++] = key;
                        if (keyCount == keys.Length) FlushKeys();
                    }
                }
                FlushKeys();
            }
        }
        if (_connected) Send(new("exit"));
    }

    private void HandleKey(ConsoleKeyInfo key)
    {
        _dirty = true;
        if (!_pasting && key.Key == ConsoleKey.F6 && _snapshot.Attachments is not null) { RequestAttachment("pick"); return; }
        if (!_pasting && HandleSpeechKey(key)) return;
        if (!_pasting && HandleControlKey(key)) return;
        if (!HasPending && HandleBracketedPaste(key)) return;
        var ctrl = (key.Modifiers & ConsoleModifiers.Control) != 0 && (key.Modifiers & ConsoleModifiers.Alt) == 0;
        var shift = (key.Modifiers & ConsoleModifiers.Shift) != 0;
        if (ctrl && key.Key == ConsoleKey.A) { if (!HasPending) { _selectionAnchor = 0; _cursor = _input.Length; } return; }
        if (ctrl && key.Key == ConsoleKey.C)
        {
            if (SelectionLength > 0) CopySelection(cut: false);
            else { if (_connected) Send(new("input", "/stop")); _notice = "Stop requested for this team."; }
            return;
        }
        if (ctrl && key.Key == ConsoleKey.X) { if (!HasPending) CopySelection(cut: true); return; }
        if (ctrl && key.Key == ConsoleKey.D) { _exit = true; return; }
        if (ctrl && key.Key == ConsoleKey.L) { ClearViewport(); return; }
        if (ctrl && key.Key == ConsoleKey.V) { if (!HasPending) PasteClipboard(); return; }
        if (key.Key == ConsoleKey.PageUp) { _scroll += Math.Max(1, _height - 8); return; }
        if (key.Key == ConsoleKey.PageDown) { _scroll = Math.Max(0, _scroll - Math.Max(1, _height - 8)); return; }
        if (key.Key == ConsoleKey.Escape) { _overlay = ""; _scroll = 0; return; }
        if (HasPending || (_settingPending || _attachmentPending) && key.Key == ConsoleKey.Enter) return;
        switch (key.Key)
        {
            case ConsoleKey.Enter:
                if (!_connected) { _exit = true; return; }
                if ((key.Modifiers & ConsoleModifiers.Shift) != 0) Insert("\n");
                else Submit();
                break;
            case ConsoleKey.Backspace:
                if (DeleteSelection()) break;
                _selectionAnchor = -1;
                if (_cursor > 0)
                {
                    var count = _cursor >= 2 && char.IsLowSurrogate(_input[_cursor - 1]) && char.IsHighSurrogate(_input[_cursor - 2]) ? 2 : 1;
                    _input = _input.Remove(_cursor - count, count); _cursor -= count;
                }
                break;
            case ConsoleKey.Delete:
                if (DeleteSelection()) break;
                _selectionAnchor = -1;
                if (_cursor < _input.Length) _input = _input.Remove(_cursor, NextDraftPosition(_cursor) - _cursor);
                break;
            case ConsoleKey.LeftArrow:
                MoveDraftCursor(!shift && SelectionLength > 0 ? SelectionStart : PreviousDraftPosition(_cursor), shift);
                break;
            case ConsoleKey.RightArrow:
                MoveDraftCursor(!shift && SelectionLength > 0 ? SelectionStart + SelectionLength : NextDraftPosition(_cursor), shift);
                break;
            case ConsoleKey.Home: MoveDraftCursor(0, shift); break;
            case ConsoleKey.End: MoveDraftCursor(_input.Length, shift); if (ctrl) _scroll = 0; break;
            case ConsoleKey.UpArrow: Recall(-1); break;
            case ConsoleKey.DownArrow: Recall(1); break;
            default:
                if (!char.IsControl(key.KeyChar)) Insert(key.KeyChar.ToString());
                break;
        }
    }

    private bool HandleBracketedPaste(ConsoleKeyInfo key)
    {
        if (!_pasting && _escape.Length > 0 && DateTime.UtcNow > _escapeUntil) _escape = "";
        // Windows Terminal brackets pasted text. CR/LF within that block must stay in one draft, never create
        // a wave of agents. A plain Escape still closes the help/roster immediately.
        if (key.KeyChar == '\u001b')
        {
            _escape = "\u001b";
            _escapeUntil = DateTime.UtcNow.AddMilliseconds(150);
            if (!_pasting) { _overlay = ""; _scroll = 0; _escapeSelectionAnchor = _selectionAnchor; _selectionAnchor = -1; }
            return true;
        }
        if (_escape.Length > 0)
        {
            _escape += key.KeyChar;
            if (_escape == "\u001b[200~")
            { _pasting = true; _pasteOverflowed = false; _paste.Clear(); _escape = ""; _selectionAnchor = _escapeSelectionAnchor; return true; }
            if (_escape == "\u001b[201~")
            {
                var inserted = _pasting && !_pasteOverflowed && Insert(_paste.ToString().Replace("\r\n", "\n").Replace('\r', '\n'));
                if (_pasteOverflowed) _notice = "Prompt limit: 100,000 characters. Your draft is kept.";
                _pasting = false; _paste.Clear(); _escape = "";
                if (inserted) _notice = "Pasted into your draft. Enter sends it as one prompt.";
                return true;
            }
            if ("\u001b[200~".StartsWith(_escape, StringComparison.Ordinal) || "\u001b[201~".StartsWith(_escape, StringComparison.Ordinal)) return true;
            _escape = "";
            // Unrelated native keys after Escape still work normally.
        }
        if (!_pasting) return false;
        if (key.KeyChar != '\0')
        {
            if (_paste.Length < TerminalWire.MaxPromptLength) _paste.Append(key.KeyChar);
            else _pasteOverflowed = true;
        }
        return true;
    }

    private bool Insert(string text)
    {
        if (_input.Length - SelectionLength + text.Length > TerminalWire.MaxPromptLength)
        { _notice = "Prompt limit: 100,000 characters. Your draft is kept."; return false; }
        if (text.Length == 0) return true;
        var start = SelectionStart;
        _input = string.Concat(_input.AsSpan(0, start), text.AsSpan(), _input.AsSpan(start + SelectionLength));
        _cursor = start + text.Length; _selectionAnchor = -1;
        return true;
    }

    private void Recall(int delta)
    {
        _historyIndex = Math.Clamp(_historyIndex + delta, 0, _history.Count);
        _input = _historyIndex < _history.Count ? _history[_historyIndex] : "";
        _cursor = _input.Length;
        _selectionAnchor = -1;
    }

    private void Submit()
    {
        var text = _input.Trim();
        if (AttachmentCommand(text)) return;
        if ((_dictationPending || Dictating) && text is not ("/mic" or "/mic cancel" or "/exit" or "/quit"))
        { _notice = "Finish dictation with F5, or cancel with Ctrl+G, before sending."; return; }
        if (text.Length == 0 && !HasAttachments) return;
        _history.Add(text);
        if (_history.Count > 100) _history.RemoveAt(0);
        _historyIndex = _history.Count;
        _input = ""; _cursor = 0; _scroll = 0; _selectionAnchor = -1;
        switch (text.ToLowerInvariant())
        {
            case "/exit": case "/quit": _exit = true; return;
            case "/help": _overlay = "help"; _scroll = int.MaxValue; _notice = "Help · PgUp/PgDn scroll · Esc returns to live output"; return;
            case "/agents": _overlay = "agents"; return;
            case "/requests": _overlay = "requests"; return;
            case "/clear": ClearViewport(); return;
            case "/paste": PasteClipboard(); return;
            case "/mic": ToggleDictation(); return;
            case "/mic cancel": CancelDictation(); return;
        }
        _overlay = "";
        _pending = text;
        _sendingAttachments = HasAttachments;
        _notice = "Sending…";
        Send(new("input", text, ContextId: _snapshot.ContextId));
    }

    private void ClearViewport()
    {
        _clearedThrough = _snapshot.Rows?.Select(r => r.Id).DefaultIfEmpty(0).Max() ?? 0;
        _overlay = ""; _scroll = 0;
        _notice = "Viewport cleared. The full transcript is saved in VibeCode.";
    }

    private void PasteClipboard()
    {
        if (_snapshot.Attachments is not null) { RequestAttachment("paste"); return; }
        var text = Native.ReadClipboard();
        if (text is null) { _notice = "The clipboard has no text."; return; }
        if (Insert(text.Replace("\r\n", "\n").Replace('\r', '\n')))
            _notice = "Pasted into your draft. Enter sends it as one prompt.";
    }

    private void Send(TerminalPacket packet)
    {
        try { TerminalWire.WriteAsync(_pipe, packet).GetAwaiter().GetResult(); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        { _connected = false; _notice = "Connection closed. Press Enter to exit."; }
    }

    private void Draw()
    {
        var width = _width - 1;
        var controlRows = LayoutControls(width);
        var attachmentRows = HasAttachments && _height >= 10 ? 1 : 0;
        var bodyHeight = Math.Max(0, _height - 7 - controlRows - attachmentRows);
        // Typing, scrolling and acknowledgement notices reuse wrapped content. Only a changed transcript,
        // overlay or width needs to walk the retained history again.
        if (!ReferenceEquals(_snapshot, _wrappedSnapshot) || width != _wrappedWidth || _overlay != _wrappedOverlay || _clearedThrough != _wrappedClearedThrough)
        {
            _wrappedLines = Content(width).ToList();
            _wrappedSnapshot = _snapshot; _wrappedWidth = width;
            _wrappedOverlay = _overlay; _wrappedClearedThrough = _clearedThrough;
        }
        var lines = _wrappedLines;
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, lines.Count - bodyHeight));
        var start = Math.Max(0, lines.Count - bodyHeight - _scroll);
        var frame = new List<string>(_height);
        void Line(string value, string color)
        { frame.Add(color + Fit(value, width) + "\u001b[K"); }
        Line("VIBE CODE TERMINAL", Green);
        Line(_snapshot.Project.Length == 0 ? "Connecting…" : _snapshot.Project, Muted);
        var agents = _snapshot.Agents ?? [];
        Line($"{agents.Length} agents  ·  {agents.Count(a => a.Status is "running" or "starting" or "preparing")} working  ·  Free agent first  ·  /agents", Green);
        DrawControls(frame, width, controlRows);
        Line(new string('─', Math.Max(1, width)), Muted);
        for (var row = 0; row < bodyHeight; row++)
        {
            var line = start + row < lines.Count ? lines[start + row] : ("", Text);
            Line(line.Item1, line.Item2);
        }
        if (attachmentRows > 0) Line($"{_snapshot.Attachments!.Length} attached · " + string.Join(" · ", _snapshot.Attachments.Select(a => a.Name)), Green);
        var hint = _notice.Length > 0 ? _notice : Dictating ? _snapshot.Speech!.Hint : _scroll > 0 ? "Scrollback · PgDn returns to live output" : _snapshot.Text + "  ·  /help  ·  /desktop  ·  Ctrl+C stop";
        Line(hint, _connected ? Muted : Amber);
        var prefix = HasPending ? "… " : "> ";
        var available = Math.Max(1, width - 2);
        var draft = DraftViewport(available);
        frame.Add(Green + prefix + Text + draft.Text + "\u001b[K");
        // Include the spare bottom row so resize reflow is cleared without erasing the screen on each key.
        while (frame.Count < _height) frame.Add("\u001b[K");
        DrawControlMenu(frame, width);
        PaintFrame(frame, _height - 1, Math.Clamp(draft.CursorCells + 3, 1, width),
            _openControl < 0 && _controlFocus < 0);
    }

    private IEnumerable<(string, string)> Content(int width)
    {
        if (_overlay == "attachments")
        {
            var attachments = _snapshot.Attachments ?? [];
            yield return (attachments.Length == 0 ? "No files attached. Click Attach or press F6." : "Attached to your next prompt:", Green);
            for (var i = 0; i < attachments.Length; i++)
                foreach (var line in Wrap($"{i + 1}. {attachments[i].Name} [{attachments[i].Kind}, {attachments[i].Size}]", width)) yield return (line, Text);
            foreach (var line in Wrap("/detach <number> removes a file · /detach all clears files · Esc returns", width)) yield return (line, Muted);
            yield break;
        }
        if (_overlay == "help")
        {
            foreach (var line in Help.Split('\n')) foreach (var part in Wrap(line, width)) yield return (part, Text);
            yield break;
        }
        if (_overlay == "agents")
        {
            foreach (var agent in _snapshot.Agents ?? [])
                foreach (var part in Wrap($"{agent.Number}  {agent.Name}  [{agent.Status}]  {agent.Provider} / {agent.Model}", width)) yield return (part, Green);
            yield return ("Esc returns to the transcript.", Muted);
            yield break;
        }
        var rows = (_snapshot.Rows ?? []).Where(row => _overlay == "requests" ? row.Kind == "request" : row.Id > _clearedThrough).ToArray();
        if (rows.Length == 0)
        {
            yield return (_overlay == "requests" ? "No pending requests." : "Describe the work. A free teammate takes your prompt.", Green);
            yield return ("Agents share this project and can communicate through their Bridge tools.", Muted);
            yield return ("/help for commands · /paste for a multiline draft · /exit returns to VibeCode", Muted);
        }
        foreach (var row in rows)
        {
            var color = row.Kind switch { "error" => Red, "request" => Amber, "tool" or "notice" => Muted, _ => Text };
            var label = row.Kind == "user" ? "You → " + row.Speaker : row.Speaker + " · " + row.Kind;
            foreach (var part in Wrap(label, width)) yield return (part, Green);
            foreach (var part in Wrap(row.Text, Math.Max(1, width - 2))) yield return ("  " + part, color);
            yield return ("", Text);
        }
    }

    public static IEnumerable<string> Wrap(string text, int width)
    {
        width = Math.Max(1, width);
        foreach (var line in TerminalWire.DisplayText(text).Split('\n'))
        {
            var current = new StringBuilder(); var used = 0;
            foreach (var rune in line.EnumerateRunes())
            {
                var cells = RuneWidth(rune);
                if (used + cells > width && current.Length > 0) { yield return current.ToString(); current.Clear(); used = 0; }
                current.Append(rune.ToString()); used += cells;
            }
            yield return current.ToString();
        }
    }

    private static string Fit(string text, int width)
    {
        var result = new StringBuilder(); var used = 0;
        foreach (var rune in TerminalWire.DisplayText(text).Replace('\n', ' ').EnumerateRunes())
        {
            used += RuneWidth(rune);
            if (used > width) break;
            result.Append(rune.ToString());
        }
        return result.ToString();
    }

    private static int CellWidth(string value) => value.EnumerateRunes().Sum(RuneWidth);
    private static int RuneWidth(Rune rune)
    {
        if (Rune.GetUnicodeCategory(rune) is System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.EnclosingMark) return 0;
        return rune.Value is >= 0x1100 and <= 0x115f or >= 0x2e80 and <= 0xa4cf or >= 0xac00 and <= 0xd7a3
            or >= 0xf900 and <= 0xfaff or >= 0xfe10 and <= 0xfe6f or >= 0xff01 and <= 0xff60 or >= 0x1f300 and <= 0x1faff ? 2 : 1;
    }

    private const string Help = "Vibe Code Terminal\n\nPrompts reuse a free agent. A new one joins only when every teammate is busy.\n" +
        "The current desktop chat supplies the project, provider, model and account.\n\n" +
        "/agents                 Show teammates and their live status\n" +
        "/message <name> <text>  Address an existing teammate (alias: /msg)\n" +
        "/stop [name]            Stop one agent, or all working agents\n" +
        "/sleep                  Release eligible idle agent processes\n" +
        "/requests               Show pending questions and permissions\n" +
        "/answer <id> <text>     Answer a question (use | between answers)\n" +
        "/allow <id>             Approve that request once\n" +
        "/deny <id> [reason]     Decline that request\n" +
        "/new                    Start another team with the same configuration\n" +
        "/paste                  Paste clipboard text, images or files\n" +
        "/attach [\"path\" ...]    Choose files or attach quoted paths\n" +
        "/attachments            List attached images and files\n" +
        "/detach <number|all>    Remove attachments from the draft\n" +
        "/clear                  Clear this viewport; saved history stays intact\n" +
        "/desktop                Open this team in VibeCode\n" +
        "/mic                    Start/stop microphone dictation into the draft\n" +
        "/mic cancel             Cancel dictation without changing your draft\n" +
        "/exit                   Close the console and return to VibeCode\n\n" +
        "Enter sends · Shift+Enter adds a line · Up/Down history\n" +
        "Ctrl+A selects draft · Shift+arrows extend · Ctrl+C copies selection · Ctrl+X cuts\n" +
        "Click Model / Effort / Fast · F2/F3/F4 open their menus\n" +
        "Click Mic or F5 to dictate · Ctrl+G cancels · Enter sends afterward\n" +
        "Click Attach or F6 for files · Ctrl+V pastes images, files or text\n" +
        "Tab moves focus · arrows choose · Enter applies · Esc cancels\n" +
        "PgUp/PgDn scroll · Ctrl+L clear · Ctrl+C stop team · Ctrl+D exit\n" +
        "Esc returns to live output. Closing this window keeps agents running.";

    private sealed class ConsoleIdentity : IDisposable
    {
        private string? _path;
        private IntPtr _icon;
        public ConsoleIdentity()
        {
            Native.SetCurrentProcessExplicitAppUserModelID("VibeCode.Terminal");
            using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("VibeCode.Terminal.logo.ico");
            if (resource is null) return;
            _path = Path.Combine(Path.GetTempPath(), "vibecode-terminal-" + Guid.NewGuid().ToString("N") + ".ico");
            using (var file = File.Create(_path)) resource.CopyTo(file);
            _icon = Native.LoadImage(IntPtr.Zero, _path, 1, 32, 32, 0x10);
            if (_icon != IntPtr.Zero)
            {
                Native.SetConsoleIcon(_icon);
                var window = Native.GetConsoleWindow();
                Native.SendMessage(window, 0x80, IntPtr.Zero, _icon); // WM_SETICON / ICON_SMALL
                Native.SendMessage(window, 0x80, new IntPtr(1), _icon); // ICON_BIG
            }
        }
        public void Dispose()
        {
            if (_icon != IntPtr.Zero) Native.DestroyIcon(_icon);
            try { if (_path is not null) File.Delete(_path); } catch (IOException) { }
        }
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Explicit, Size = 20, CharSet = CharSet.Unicode)] internal struct InputRecord
        {
            [FieldOffset(0)] public short EventType;
            [FieldOffset(4)] public int Down;
            [FieldOffset(8)] public ushort Repeat;
            [FieldOffset(10)] public ushort VirtualKey;
            [FieldOffset(14)] public char Character;
            [FieldOffset(16)] public uint Modifiers;
            [FieldOffset(4)] public short MouseX;
            [FieldOffset(6)] public short MouseY;
            [FieldOffset(8)] public uint MouseButtons;
            [FieldOffset(16)] public uint MouseFlags;
        }
        [DllImport("kernel32.dll")] internal static extern uint WaitForMultipleObjects(uint count, IntPtr[] handles, bool all, uint milliseconds);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern bool ReadConsoleInput(IntPtr input, [Out] InputRecord[] records, uint length, out uint read);
        [DllImport("kernel32.dll")] internal static extern bool FreeConsole();
        [DllImport("kernel32.dll")] internal static extern bool AllocConsole();
        [DllImport("kernel32.dll")] internal static extern IntPtr GetStdHandle(int handle);
        [DllImport("kernel32.dll")] internal static extern bool GetConsoleMode(IntPtr handle, out uint mode);
        [DllImport("kernel32.dll")] internal static extern bool SetConsoleMode(IntPtr handle, uint mode);
        [DllImport("kernel32.dll")] internal static extern IntPtr GetConsoleWindow();
        [DllImport("kernel32.dll")] internal static extern bool SetConsoleIcon(IntPtr icon);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] internal static extern int SetCurrentProcessExplicitAppUserModelID(string id);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int x, int y, uint flags);
        [DllImport("user32.dll")] internal static extern bool DestroyIcon(IntPtr icon);
        [DllImport("user32.dll")] internal static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr parameter, IntPtr value);
        [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr owner);
        [DllImport("user32.dll")] private static extern bool CloseClipboard();
        [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr handle);
        [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr handle);
        internal static string? ReadClipboard()
        {
            if (!OpenClipboard(IntPtr.Zero)) return null;
            try
            {
                var handle = GetClipboardData(13);
                if (handle == IntPtr.Zero) return null;
                var data = GlobalLock(handle);
                try { return data == IntPtr.Zero ? null : Marshal.PtrToStringUni(data); }
                finally { if (data != IntPtr.Zero) GlobalUnlock(handle); }
            }
            finally { CloseClipboard(); }
        }
    }
}
