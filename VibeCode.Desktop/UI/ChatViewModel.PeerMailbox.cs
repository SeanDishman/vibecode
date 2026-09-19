using VibeCode.Services;

namespace VibeCode.UI;

public sealed partial class ChatViewModel
{
    internal BridgeMailboxStore.Mailbox? PeerMailbox { get; set; }
    private sealed record MailboxNotice(BridgeMailboxStore.Message Message, Action<int> OnDispatch,
        PeerMessageLimits Limits);
    private readonly List<MailboxNotice> _mailboxNotices = new();
    private bool HasPendingDispatch => _sendQueue.Count > 0 || _mailboxNotices.Count > 0;
    internal bool IsPeerNotificationTurn { get; private set; }

    internal bool AcceptsPeerNotifications => _status != "closed" && _session is { HasExited: false }
        && PeerMailbox?.Store.Available != false;

    /// <summary>Separate control queue: never calls Send/Interrupt, folds user text, consumes SwarmNextTurn,
    /// or changes extended-queue pause/send-now flags. One notification can cover multiple arrivals.</summary>
    internal void QueuePeerNotification(BridgeMailboxStore.Message message, PeerMessageLimits limits,
        Action<int> onDispatch)
    {
        if (!AcceptsPeerNotifications || !ReferenceEquals(message.To, PeerMailbox))
            throw new InvalidOperationException("The recipient mailbox/session is no longer available.");
        _mailboxNotices.Add(new(message, onDispatch, limits));
        _mailboxNotices.RemoveAll(n => !message.To.Messages.Contains(n.Message));
        if (_status == "idle") Post(FlushQueue);
    }

    private bool FlushPeerNotifications()
    {
        if (_status != "idle" || !AcceptsPeerNotifications || IsPeerNotificationTurn) return false;
        _mailboxNotices.RemoveAll(n => n.Message.To.Closed || !n.Message.To.Messages.Contains(n.Message)
            || n.Message.ReadAt.HasValue);
        if (_mailboxNotices.Count == 0) return false;
        var notices = _mailboxNotices.ToArray();
        _mailboxNotices.Clear();
        var latest = notices[^1];
        var box = latest.Message.To;
        var senders = string.Join(", ", notices.Select(n => n.Message.FromAtSend).Distinct().Select(n => $"Agent {n}"));
        var link = box.FilePath.Replace('\\', '/').Replace(">", "%3E");
        var notice = $"{senders} sent you a message. View it in [agent{box.Number}messages.md](<{link}>).";
        var hop = notices.Max(n => n.Message.Hop); // coalescing must never reset the loop budget
        var senderLabel = $"Agent {latest.Message.FromAtSend} ({latest.Message.From.Label})";
        var wire = PeerMessagePolicy.MailboxWire(senderLabel, latest.Message.FromAtSend, notice, box.Number, hop, latest.Limits);
        IsPeerNotificationTurn = true;
        latest.OnDispatch(hop); // assign to the actual notification turn, not whichever turn was already running
        var row = SendNow(wire, null);
        if (row is not null)
        {
            var labels = notices.Select(n => $"{n.Message.From.Label} {n.Message.FromAtSend}").Distinct();
            var displayBody = notices.Length == 1 ? latest.Message.Body
                : string.Join("\n\n---\n\n", notices.Select(n =>
                    $"**{n.Message.From.Label} {n.Message.FromAtSend}**\n\n{n.Message.Body}"));
            displayBody += $"\n\n[View agent{box.Number}messages.md](<{link}>).";
            row.SetAgentDisplay(new AgentMessageCard("💬", string.Join(", ", labels), "Peer message", displayBody));
        }
        PinQueuedItemsToEnd();
        return true;
    }

    internal void ClearPeerMailbox()
    {
        _mailboxNotices.Clear();
        IsPeerNotificationTurn = false;
        if (PeerMailbox is not { } box) return;
        try { box.Store.Close(box); }
        catch (Exception ex)
        {
            // Do not silently claim cleanup succeeded or overwrite a user-replaced file.
            Items.Add(new BannerItem { Level = "warning", Text = $"Could not remove {box.FilePath}: {ex.Message}" });
            SupervisionLog.Write(BridgeLabel, "PEER-MAILBOX-CLEANUP", ex.Message);
        }
        finally { PeerMailbox = null; }
    }
}
