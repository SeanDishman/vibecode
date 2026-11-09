using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using VibeCode.UI;

namespace VibeCode.Terminal;

public sealed record TerminalClipboard(string[]? Files = null, BitmapSource? Image = null, string? Text = null);
public interface ITerminalAttachmentSource
{
    string[] PickFiles();
    TerminalClipboard ReadClipboard();
}

internal sealed class DesktopTerminalAttachments : ITerminalAttachmentSource
{
    public string[] PickFiles()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Vibe Code Terminal · Attach files", Multiselect = true,
            Filter = "Images and files|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp;*.pdf;*.txt;*.md;*.csv;*.json;*.log;*.xml;*.yml;*.yaml|All files|*.*" };
        return dialog.ShowDialog() == true ? dialog.FileNames : [];
    }

    public TerminalClipboard ReadClipboard()
    {
        if (Clipboard.ContainsFileDropList()) return new(Files: Clipboard.GetFileDropList().Cast<string>().ToArray());
        if (Clipboard.ContainsImage()) return new(Image: Clipboard.GetImage());
        return new(Text: Clipboard.ContainsText() ? Clipboard.GetText() : null);
    }
}

public sealed partial class TerminalSession
{
    private readonly ITerminalAttachmentSource _attachmentSource;
    private readonly Dictionary<Attachment, string> _attachmentIds = [];
    public const int MaxAttachments = 12;
    public const long MaxAttachmentBytes = 32 * 1024 * 1024;
    public const long MaxAttachmentDraftBytes = 64 * 1024 * 1024;

    private static long AttachmentBytes(Attachment attachment) => attachment.Data?.LongLength
        ?? (attachment.Text is null ? 0 : Encoding.UTF8.GetByteCount(attachment.Text));

    private TerminalAttachment[] AttachmentSnapshot()
    {
        foreach (var old in _attachmentIds.Keys.Where(a => !Host.Attachments.Contains(a)).ToArray()) _attachmentIds.Remove(old);
        return Host.Attachments.Select(a => new TerminalAttachment(
            _attachmentIds.TryGetValue(a, out var id) ? id : _attachmentIds[a] = Guid.NewGuid().ToString("N"),
            a.FileName, a.Kind, a.SizeText)).ToArray();
    }

    public async Task<TerminalPacket> HandleAttachmentsAsync(TerminalPacket request)
    {
        TerminalPacket Reply(string text, bool accepted = true, string type = "attachments") =>
            new(type, text, accepted, ContextId: Host.BridgeAgentId, Attachments: AttachmentSnapshot());
        if (_disposed || request.ContextId != Host.BridgeAgentId) return Reply("The chat changed. Attach the files to the current chat again.", false);
        var owner = Host;
        try
        {
            if (request.Text == "clear") { owner.Attachments.Clear(); MarkDirty(); return Reply("Attachments removed. Your text draft is kept."); }
            if (request.Text == "remove")
            {
                AttachmentSnapshot();
                var entry = _attachmentIds.FirstOrDefault(p => p.Value == request.AttachmentId).Key;
                if (entry is null || !owner.Attachments.Remove(entry)) return Reply("That attachment is no longer in this draft.", false);
                MarkDirty(); return Reply("Attachment removed.");
            }
            string[] paths;
            Attachment[] loaded;
            if (request.Text == "paste")
            {
                var clipboard = _attachmentSource.ReadClipboard();
                if (clipboard.Files is { Length: > 0 }) paths = clipboard.Files;
                else if (clipboard.Image is { } image)
                {
                    // Copy the clipboard image while on its STA owner, then encode away from the UI thread.
                    var frozen = image.Clone(); frozen.Freeze();
                    loaded = [await Task.Run(() => MainWindow.LoadClipboardImage(frozen), _lifetime.Token)];
                    return Stage(loaded);
                }
                else if (clipboard.Text is { Length: > 0 } text)
                    return text.Length <= TerminalWire.MaxPromptLength ? Reply(text, type: "clipboard-text")
                        : Reply("Clipboard text exceeds the 100,000-character prompt limit.", false);
                else return Reply("The clipboard has no image, files or text.", false);
            }
            else if (request.Text == "pick") paths = _attachmentSource.PickFiles();
            else if (request.Text == "paths") paths = request.Paths ?? [];
            else return Reply("Unknown attachment command.", false);
            if (paths.Length == 0) return Reply("No files selected. Your draft is kept.");
            if (paths.Length + owner.Attachments.Count > MaxAttachments) return Reply($"A draft can contain up to {MaxAttachments} attachments.", false);
            var cwd = owner.Cwd;
            var available = MaxAttachmentDraftBytes - owner.Attachments.Sum(AttachmentBytes);
            loaded = await Task.Run(() =>
            {
                var resolved = paths.Select(path => Path.GetFullPath(path.Trim().Trim('"'), cwd)).ToArray();
                long total = 0;
                foreach (var path in resolved)
                {
                    var info = new FileInfo(path);
                    if (!info.Exists) throw new IOException($"File not found: {path}");
                    if (info.Length > MaxAttachmentBytes) throw new IOException($"{info.Name} exceeds the 32 MiB attachment limit.");
                    total += info.Length;
                }
                if (total > available) throw new IOException("Attachments exceed the 64 MiB draft limit.");
                return resolved.Select(path => MainWindow.LoadAttachment(path, preview: false)).ToArray();
            }, _lifetime.Token);
            return Stage(loaded);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return Reply("Could not attach: " + ex.Message + " Your draft is kept.", false); }

        TerminalPacket Stage(Attachment[] loaded)
        {
            if (_disposed || !ReferenceEquals(Host, owner)) return Reply("The chat changed before attaching finished. Select the files again.", false);
            if (owner.Attachments.Count + loaded.Length > MaxAttachments || loaded.Any(a => AttachmentBytes(a) > MaxAttachmentBytes)
                || owner.Attachments.Sum(AttachmentBytes) + loaded.Sum(AttachmentBytes) > MaxAttachmentDraftBytes)
                return Reply("Attachment limit: 12 files, 32 MiB per file and 64 MiB per draft.", false);
            foreach (var attachment in loaded) owner.Attachments.Add(attachment);
            MarkDirty();
            return Reply($"Attached {loaded.Length} file(s). Enter sends with your draft · /attachments lists them.");
        }
    }

    private TerminalPacket HandleSubmission(TerminalPacket packet)
    {
        if (packet.ContextId.Length > 0 && packet.ContextId != Host.BridgeAgentId)
            return new("ack", "The chat changed before sending. Your draft is kept.", false, ContextId: Host.BridgeAgentId, Attachments: AttachmentSnapshot());
        return HandleInput(packet.Text) with { ContextId = Host.BridgeAgentId, Attachments = AttachmentSnapshot() };
    }
}
