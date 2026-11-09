using System.Text.RegularExpressions;

namespace VibeCode.Terminal;

public sealed partial class TerminalConsole
{
    private const int AttachControl = 4;
    private bool _attachmentPending, _sendingAttachments;
    private bool HasPending => _pending.Length > 0 || _sendingAttachments;
    private bool HasAttachments => _snapshot.Attachments is { Length: > 0 };
    private bool AttachEnabled => _connected && !HasPending && !_attachmentPending && !Dictating && !_dictationPending;
    private string AttachLabel => HasAttachments ? $"Files: {_snapshot.Attachments!.Length} · F6" : "Attach · F6";

    private void RequestAttachment(string command, string[]? paths = null, string? id = null)
    {
        _openControl = _controlFocus = -1;
        if (!AttachEnabled)
        {
            _notice = !_connected ? "VibeCode is disconnected." : Dictating || _dictationPending
                ? "Finish dictation with F5 before attaching files." : "Wait for the current attachment or send to finish.";
            return;
        }
        _attachmentPending = true;
        _notice = command == "pick" ? "Choose images or files in the file picker…" : "Updating attachments…";
        Send(new("attachment", command, ContextId: _snapshot.ContextId, Paths: paths, AttachmentId: id));
    }

    private void ReceiveAttachments(TerminalPacket packet)
    {
        if (packet.ContextId != _snapshot.ContextId) return;
        _attachmentPending = false;
        if (packet.Attachments is not null) _snapshot = _snapshot with { Attachments = packet.Attachments };
        if (packet.Type == "clipboard-text" && packet.Accepted)
        {
            var text = packet.Text.Replace("\r\n", "\n").Replace('\r', '\n');
            if (_input.Length - SelectionLength + text.Length > TerminalWire.MaxPromptLength)
            { _notice = "Clipboard text will not fit. Shorten the draft and paste again."; return; }
            Insert(text); _notice = "Pasted into your draft. Enter sends it as one prompt.";
        }
        else _notice = packet.Text;
    }

    private bool AttachmentCommand(string text)
    {
        if (_snapshot.Attachments is null) return false;
        var split = text.IndexOf(' ');
        var command = (split < 0 ? text : text[..split]).ToLowerInvariant();
        var rest = split < 0 ? "" : text[(split + 1)..].Trim();
        if (command is not ("/attach" or "/attachments" or "/detach")) return false;
        if (!AttachEnabled && command != "/attachments") { RequestAttachment("pick"); return true; }
        _input = ""; _cursor = 0; _selectionAnchor = -1;
        if (command == "/attachments") { _overlay = "attachments"; _scroll = int.MaxValue; _notice = "Attachments · PgUp/PgDn scroll · /detach <number> removes · Esc returns"; return true; }
        if (command == "/attach")
        {
            if (rest.Length == 0) RequestAttachment("pick");
            else RequestAttachment("paths", Regex.Matches(rest, "\"([^\"]+)\"|(\\S+)")
                .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).ToArray());
        }
        else if (rest.Equals("all", StringComparison.OrdinalIgnoreCase)) RequestAttachment("clear");
        else if (int.TryParse(rest, out var number) && number > 0 && number <= _snapshot.Attachments.Length)
            RequestAttachment("remove", id: _snapshot.Attachments[number - 1].Id);
        else _notice = "Use /attachments to list files, then /detach <number> or /detach all.";
        return true;
    }
}
