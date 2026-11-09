namespace VibeCode.Terminal;

public sealed partial class TerminalConsole
{
    private const int MicControl = 3;
    private bool _dictationPending;
    private string _heldDictation = "";
    private bool Dictating => _snapshot.Speech?.State is "recording" or "transcribing" or "downloading";
    private string MicLabel => _heldDictation.Length > 0 ? "Insert speech · F5" : _snapshot.Speech?.State switch
    {
        "recording" => "Stop mic · F5",
        "transcribing" or "downloading" => "Cancel speech · F5",
        "busy" => "Mic in use",
        _ => "Mic · F5",
    };
    private bool MicEnabled => _connected && !_dictationPending && !_attachmentPending && _snapshot.Speech is not null
        && _snapshot.Speech.State != "busy" && (!HasPending || Dictating);

    private void ToggleDictation()
    {
        _openControl = _controlFocus = -1;
        if (_heldDictation.Length > 0) { InsertDictation(); return; }
        if (!MicEnabled)
        {
            _notice = !_connected ? "VibeCode is disconnected." : _snapshot.Speech?.State == "busy"
                ? _snapshot.Speech.Hint : _dictationPending ? "Waiting for the microphone…" : "Wait for the submitted prompt before dictating.";
            return;
        }
        _dictationPending = true;
        _notice = Dictating ? "Finishing dictation…" : "Starting microphone…";
        Send(new("dictation", "toggle"));
    }

    private void CancelDictation()
    {
        _heldDictation = "";
        _openControl = _controlFocus = -1;
        if (!_connected) return;
        _dictationPending = true;
        Send(new("dictation", "cancel"));
    }

    private bool HandleSpeechKey(ConsoleKeyInfo key)
    {
        if (key.Key == ConsoleKey.F5) { ToggleDictation(); return true; }
        if (key.Key == ConsoleKey.G && (key.Modifiers & ConsoleModifiers.Control) != 0)
        { CancelDictation(); return true; }
        return false;
    }

    private void ReceiveDictation(TerminalPacket packet)
    {
        if (packet.ContextId != _snapshot.ContextId) return;
        _dictationPending = false;
        if (packet.Speech is not null) _snapshot = _snapshot with { Speech = packet.Speech };
        if (packet.Type != "dictation-result") { _notice = packet.Text; return; }
        _heldDictation = TerminalWire.DisplayText(packet.Text).Trim();
        if (_heldDictation.Length == 0)
        { _notice = "No speech detected. Try again, a bit closer to the microphone."; return; }
        InsertDictation();
        if (_heldDictation.Length == 0 && packet.Speech is { State: "idle" } speech
            && speech.Hint != "F5 or click Mic to dictate · Enter sends after transcription")
            _notice = "Dictation inserted. " + speech.Hint;
    }

    private void InsertDictation()
    {
        var start = SelectionStart;
        var end = start + SelectionLength;
        var prefix = start > 0 && !char.IsWhiteSpace(_input[start - 1]) ? " " : "";
        var suffix = end < _input.Length && !char.IsWhiteSpace(_input[end]) ? " " : "";
        var text = prefix + _heldDictation + suffix;
        if (HasPending || _input.Length - SelectionLength + text.Length > TerminalWire.MaxPromptLength)
        {
            _notice = "Speech kept. Shorten the draft, then press F5 to insert it; Ctrl+G discards it.";
            return;
        }
        Insert(text);
        _heldDictation = "";
        _notice = "Dictation inserted into your draft. Edit it or press Enter to send.";
    }
}
