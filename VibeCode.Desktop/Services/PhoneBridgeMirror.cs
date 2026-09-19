using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using VibeCode.UI;

namespace VibeCode.Services;

/// <summary>
/// A thread-safe, UI-free copy of what the desktop is showing, kept for the phone to read.
///
/// HTTP request threads must never touch a <see cref="ChatViewModel"/>: every property on it is owned by the WPF
/// dispatcher, and blocking a socket thread on Invoke while the UI thread is mid-turn is how you deadlock an app
/// that was working fine on its own. So the dispatcher rebuilds a plain-JSON mirror on a timer and the socket
/// threads only ever read that.
///
/// The rebuild is <b>refcount-gated and pull-driven</b>: it does nothing at all unless a phone has actually asked
/// for something in the last <see cref="IdleTimeout"/>, and full transcripts are only mirrored for the chats a
/// phone is currently looking at. A bridge nobody has opened costs one idle timer tick and nothing else.
/// </summary>
public sealed class PhoneBridgeMirror
{
    /// <summary>How long after a phone's last request the mirror keeps refreshing. Covers the gap between two
    /// long-polls without keeping the timer alive for a phone that went into a pocket.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(2);
    private const int TailLimit = 300;          // items mirrored per chat - a phone never scrolls past this
    private const int ChangeLogDepth = 512;     // versions we can serve an incremental diff for
    private const int TextCap = 24_000;         // per-message text ceiling
    private const int ResultCap = 2_000;        // tool output is the noisiest field; cap it harder

    private readonly object _gate = new();
    private readonly Dispatcher _ui;
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _timer;

    // --- published state (read under _gate by socket threads) ---
    private string _chatsJson = "[]";
    private int _listVersion;
    private readonly Dictionary<string, ChatMirror> _chats = new(StringComparer.Ordinal);

    // --- demand tracking ---
    private DateTime _lastRequest = DateTime.MinValue;
    private readonly Dictionary<string, DateTime> _watched = new(StringComparer.Ordinal);

    /// <summary>Pulsed after every rebuild that changed something, so long-polls wake immediately instead of
    /// waiting out their timeout.</summary>
    private TaskCompletionSource _pulse = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PhoneBridgeMirror(MainViewModel vm, Dispatcher ui)
    {
        _vm = vm;
        _ui = ui;
        _timer = new DispatcherTimer(DispatcherPriority.Background, ui)
        {
            Interval = TimeSpan.FromMilliseconds(400),
        };
        _timer.Tick += (_, _) => Rebuild();
    }

    /// <summary>One chat's mirrored transcript plus the change log that makes incremental fetches possible.</summary>
    private sealed class ChatMirror
    {
        public string Summary = "{}";
        /// <summary>Everything the phone's control surface needs that is not a transcript row - todos, the send
        /// queue, live token counts, the model/effort/mode pills. Built only for watched chats, and versioned
        /// alongside the transcript so a todo flipping to "completed" wakes a long poll like any other change.</summary>
        public string Detail = "{}";
        public int Version;
        public List<string> Items = new();
        /// <summary>(version, lowest index that changed at that version), oldest first.</summary>
        public List<(int Version, int MinIndex)> Changes = new();
        public bool Full = true;    // transcript mirrored, or summary only
    }

    // ---------------- demand ----------------

    /// <summary>Called on every authenticated request. Starts the rebuild timer if it was asleep.</summary>
    public void Touch()
    {
        bool wake;
        lock (_gate)
        {
            wake = DateTime.UtcNow - _lastRequest > IdleTimeout;
            _lastRequest = DateTime.UtcNow;
        }
        if (wake) _ui.BeginInvoke(() => { if (!_timer.IsEnabled) { _timer.Start(); Rebuild(); } });
    }

    /// <summary>Marks a chat as being read right now, which is what promotes it from summary-only to a full
    /// mirrored transcript.</summary>
    public void Watch(string chatId)
    {
        lock (_gate) _watched[chatId] = DateTime.UtcNow;
        Touch();
    }

    // ---------------- reads (socket threads) ----------------

    /// <summary>False until the first rebuild has run. A request that arrives before it would otherwise be answered
    /// with an empty chat list that looks authoritative.</summary>
    public bool Built
    {
        get { lock (_gate) return _built; }
    }
    private bool _built;

    public string ChatsJson(out int version)
    {
        lock (_gate) { version = _listVersion; return _chatsJson; }
    }

    /// <summary>Assembles a messages response for a client that last saw <paramref name="clientVersion"/>.
    /// Returns null when nothing has changed since.</summary>
    public string? MessagesJson(string chatId, int clientVersion)
    {
        lock (_gate)
        {
            if (!_chats.TryGetValue(chatId, out var chat)) return null;
            if (!chat.Full) return null;                         // first poll on this chat: nothing mirrored yet
            if (clientVersion == chat.Version) return null;      // caller is already current

            var count = chat.Items.Count;
            var baseIndex = 0;
            if (clientVersion > 0 && chat.Changes.Count > 0 && clientVersion >= chat.Changes[0].Version)
            {
                var min = count;
                foreach (var (v, idx) in chat.Changes)
                    if (v > clientVersion && idx < min) min = idx;
                baseIndex = Math.Min(min, count);
            }

            var sb = new StringBuilder(1024);
            sb.Append("{\"version\":").Append(chat.Version)
              .Append(",\"total\":").Append(count)
              .Append(",\"base\":").Append(baseIndex)
              .Append(",\"chat\":").Append(chat.Summary)
              .Append(",\"detail\":").Append(chat.Detail)
              .Append(",\"items\":[");
            for (var i = baseIndex; i < count; i++)
            {
                if (i > baseIndex) sb.Append(',');
                sb.Append(chat.Items[i]);
            }
            sb.Append("]}");
            return sb.ToString();
        }
    }

    public int VersionOf(string chatId)
    {
        lock (_gate) return _chats.TryGetValue(chatId, out var c) ? c.Version : 0;
    }

    public bool Knows(string chatId)
    {
        lock (_gate) return _chats.ContainsKey(chatId);
    }

    /// <summary>Awaits the next rebuild that actually changed something, or the timeout.</summary>
    public async Task<bool> WaitForChangeAsync(TimeSpan timeout, CancellationToken cancel)
    {
        Task pulse;
        lock (_gate) pulse = _pulse.Task;
        var done = await Task.WhenAny(pulse, Task.Delay(timeout, cancel)).ConfigureAwait(false);
        return done == pulse;
    }

    // ---------------- rebuild (UI thread) ----------------

    private void Rebuild()
    {
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            if (now - _lastRequest > IdleTimeout)
            {
                // Nobody is listening. Stop the timer and drop the mirrored transcripts - they can be several MB
                // and there is no reason to hold them for a phone that is not looking.
                _timer.Stop();
                _chats.Clear();
                _watched.Clear();
                _built = false;
                return;
            }
        }

        HashSet<string> watched;
        lock (_gate)
        {
            foreach (var stale in _watched.Where(w => now - w.Value > IdleTimeout).Select(w => w.Key).ToList())
                _watched.Remove(stale);
            watched = new HashSet<string>(_watched.Keys, StringComparer.Ordinal);
        }

        var changed = false;
        var listBuilder = new StringBuilder("[");
        var live = new HashSet<string>(StringComparer.Ordinal);
        var summaries = new Dictionary<string, string>(StringComparer.Ordinal);
        var first = true;

        foreach (var chat in _vm.Chats.ToList())
        {
            var id = chat.BridgeId;
            live.Add(id);
            var summary = SummaryJson(chat);
            summaries[id] = summary;
            if (!first) listBuilder.Append(',');
            first = false;
            listBuilder.Append(summary);
        }
        listBuilder.Append(']');
        var listJson = listBuilder.ToString();

        lock (_gate)
        {
            if (!string.Equals(listJson, _chatsJson, StringComparison.Ordinal))
            {
                _chatsJson = listJson;
                _listVersion++;
                changed = true;
            }
            foreach (var gone in _chats.Keys.Where(k => !live.Contains(k)).ToList())
                _chats.Remove(gone);
        }

        foreach (var chat in _vm.Chats.ToList())
        {
            var id = chat.BridgeId;
            var wanted = watched.Contains(id);
            ChatMirror mirror;
            lock (_gate)
            {
                if (!_chats.TryGetValue(id, out mirror!)) { mirror = new ChatMirror { Full = false }; _chats[id] = mirror; }
            }

            if (!wanted)
            {
                // Not being read: keep the summary current but throw the transcript away.
                lock (_gate)
                {
                    if (mirror.Full) { mirror.Full = false; mirror.Items = new List<string>(); mirror.Changes.Clear(); }
                    mirror.Summary = summaries[id];
                }
                continue;
            }

            var items = SerializeItems(chat);
            var detail = DetailJson(chat);
            lock (_gate)
            {
                mirror.Summary = summaries[id];
                var wasFull = mirror.Full;
                var minChanged = FirstDifference(mirror.Items, items);
                var detailChanged = !string.Equals(mirror.Detail, detail, StringComparison.Ordinal);
                if (!wasFull || minChanged >= 0 || detailChanged)
                {
                    mirror.Items = items;
                    mirror.Detail = detail;
                    mirror.Full = true;
                    mirror.Version++;
                    if (!wasFull) mirror.Changes.Clear();
                    // A detail-only change resends no transcript rows at all: `base` == the item count leaves the
                    // phone's existing list untouched while still delivering the new version and detail block.
                    var changeIndex = minChanged >= 0 ? minChanged : items.Count;
                    mirror.Changes.Add((mirror.Version, changeIndex));
                    if (mirror.Changes.Count > ChangeLogDepth) mirror.Changes.RemoveRange(0, mirror.Changes.Count - ChangeLogDepth);
                    changed = true;
                }
            }
        }

        lock (_gate)
        {
            if (!_built) { _built = true; changed = true; }
        }

        if (!changed) return;
        TaskCompletionSource old;
        lock (_gate)
        {
            old = _pulse;
            _pulse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        old.TrySetResult();
    }

    /// <summary>Lowest index at which the two snapshots differ, or -1 when they are identical.</summary>
    private static int FirstDifference(List<string> a, List<string> b)
    {
        var n = Math.Min(a.Count, b.Count);
        for (var i = 0; i < n; i++)
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return i;
        return a.Count == b.Count ? -1 : n;
    }

    // ---------------- serialisation ----------------

    private static string SummaryJson(ChatViewModel chat)
    {
        var attention = false;
        foreach (var item in chat.Items)
            if (item is PermItem { IsPending: true }) { attention = true; break; }

        var preview = "";
        for (var i = chat.Items.Count - 1; i >= 0 && preview.Length == 0; i--)
        {
            preview = chat.Items[i] switch
            {
                TextItem t => CompactToolPresentation.ToSingleLine(t.Text),
                UserItem u => CompactToolPresentation.ToSingleLine(u.Text),
                _ => "",
            };
        }

        var todosDone = 0;
        foreach (var todo in chat.Todos) if (todo.Done) todosDone++;

        var o = new JsonObject
        {
            ["id"] = chat.BridgeId,
            ["title"] = string.IsNullOrWhiteSpace(chat.Title) ? "New chat" : chat.Title,
            ["provider"] = chat.Provider,
            ["providerLabel"] = chat.ProviderDisplay,
            ["cwd"] = chat.Cwd,
            ["folder"] = FolderName(chat.Cwd),
            ["status"] = chat.Status,
            ["working"] = chat.IsWorking,
            ["queued"] = chat.HasQueued,
            ["pinned"] = chat.Pinned,
            ["model"] = chat.ModelDisplay,
            ["attention"] = attention,
            ["messages"] = chat.Items.Count,
            ["preview"] = Cap(preview, 160),
            // Control-surface state, cheap enough to carry on every list row so the phone can show the pills
            // without opening the chat.
            ["mode"] = chat.Mode,
            ["modeLabel"] = chat.ModeDisplay,
            ["effort"] = chat.Effort,
            ["fast"] = chat.FastMode,
            ["canFast"] = chat.ShowFastMode,
            ["interrupt"] = chat.CanInterrupt,
            ["todosDone"] = todosDone,
            ["todosTotal"] = chat.Todos.Count,
        };
        return o.ToJsonString();
    }

    /// <summary>
    /// The non-transcript half of an open chat: the task list, the pending send queue, live usage, and the model
    /// pills. Kept out of <see cref="SummaryJson"/> deliberately - todos and token counts churn on every turn, and
    /// folding them into the chat-list payload would invalidate the list version several times a second for a
    /// phone that is only looking at the sidebar.
    /// </summary>
    private static string DetailJson(ChatViewModel chat)
    {
        var todos = new JsonArray();
        foreach (var todo in chat.Todos.ToList())
            todos.Add(new JsonObject { ["s"] = todo.Status, ["t"] = Cap(todo.Text, 400) });

        // Queue entries are addressed by ordinal rather than an id, because QueuedItem has none. The ordinal is
        // stable for as long as the queue is - and the desktop re-reads it under the dispatcher before acting, so
        // a queue that shifted between poll and tap fails the bounds check instead of cancelling the wrong prompt.
        var queue = new JsonArray();
        var ordinal = 0;
        foreach (var item in chat.Items.ToList())
            if (item is QueuedItem q)
                queue.Add(new JsonObject { ["ord"] = ordinal++, ["t"] = Cap(q.Text, 2_000) });

        var files = new JsonArray();
        foreach (var file in chat.Files.ToList().TakeLast(60))
            files.Add(new JsonObject { ["p"] = file.Path, ["n"] = file.FileName, ["w"] = file.Writes });

        return new JsonObject
        {
            ["todos"] = todos,
            ["queue"] = queue,
            ["files"] = files,
            ["model"] = chat.Model,
            ["modelLabel"] = chat.ModelDisplay,
            ["effort"] = chat.Effort,
            ["effortLabel"] = chat.EffortDisplay,
            ["mode"] = chat.Mode,
            ["modeLabel"] = chat.ModeDisplay,
            ["fast"] = chat.FastMode,
            ["canFast"] = chat.ShowFastMode,
            ["canFastNow"] = chat.CanToggleFastMode,
            ["tokensIn"] = chat.TotalIn,
            ["tokensOut"] = chat.TotalOut,
            ["tokensLabel"] = chat.TokensText,
            ["cost"] = chat.Cost,
            ["costLabel"] = chat.CostText,
            ["canInterrupt"] = chat.CanInterrupt,
            ["canSendQueuedNow"] = chat.CanSendQueuedNow,
        }.ToJsonString();
    }

    private static string FolderName(string cwd)
    {
        try
        {
            var name = Path.GetFileName(cwd.TrimEnd('\\', '/'));
            return string.IsNullOrEmpty(name) ? cwd : name;
        }
        catch { return cwd; }
    }

    private static List<string> SerializeItems(ChatViewModel chat)
    {
        var source = chat.Items.ToList();
        // Only the tail is mirrored. `base` in the wire response is an index into THIS list, so the phone's
        // indices stay consistent as long as the list is rebuilt the same way every tick.
        var start = Math.Max(0, source.Count - TailLimit);

        // Prompt and queue ordinals count from the beginning of the transcript, not from the start of the mirrored
        // tail: the desktop resolves them by counting the same way over chat.Items, and an ordinal that meant
        // something different on each side would rewind the wrong turn.
        var userOrdinal = 0;
        var queuedOrdinal = 0;
        for (var i = 0; i < start; i++)
        {
            if (source[i] is UserItem) userOrdinal++;
            else if (source[i] is QueuedItem) queuedOrdinal++;
        }

        var result = new List<string>(source.Count - start);
        for (var i = start; i < source.Count; i++)
        {
            var ordinal = source[i] switch
            {
                UserItem => userOrdinal++,
                QueuedItem => queuedOrdinal++,
                _ => -1,
            };
            foreach (var json in Serialize(source[i], ordinal))
                result.Add(json);
        }
        return result;
    }

    /// <summary>One transcript row becomes zero, one, or (for a compacted tool group) several wire items.</summary>
    private static IEnumerable<string> Serialize(ItemVm item, int ordinal)
    {
        switch (item)
        {
            case UserItem u:
                yield return new JsonObject
                {
                    ["k"] = "user",
                    ["t"] = Cap(u.Text, TextCap),
                    ["sub"] = u.FromSubagent,
                    ["att"] = u.Attachments?.Count ?? 0,
                    ["ord"] = ordinal,
                    // Whether the rewind arrow would do anything if the phone tapped it right now. Sent per-poll
                    // because it flips as the turn completes and its checkpoint seals.
                    ["undo"] = u.CanRequestUndo,
                    ["undone"] = u.HasUndoStatus,
                    ["cascades"] = u.NewerPendingTurns,
                }.ToJsonString();
                break;

            case TextItem t:
                yield return new JsonObject
                {
                    ["k"] = "assistant",
                    ["t"] = Cap(t.Text, TextCap),
                    ["live"] = t.Streaming,
                }.ToJsonString();
                break;

            case ThinkingItem th:
                yield return new JsonObject
                {
                    ["k"] = "thinking",
                    ["t"] = Cap(th.Text, TextCap),
                    ["live"] = th.Streaming,
                }.ToJsonString();
                break;

            case ToolItem tool:
                yield return ToolJson(tool);
                break;

            case CompactToolGroupItem group:
                foreach (var child in group.Tools.ToList()) yield return ToolJson(child);
                break;

            case PermItem perm:
                yield return PermJson(perm);
                break;

            case BannerItem banner:
                yield return new JsonObject
                {
                    ["k"] = "banner",
                    ["level"] = banner.Level,
                    ["t"] = Cap(banner.Text, 2_000),
                }.ToJsonString();
                break;

            case DividerItem divider:
                yield return new JsonObject { ["k"] = "divider", ["t"] = divider.Label }.ToJsonString();
                break;

            case QueuedItem queued:
                yield return new JsonObject
                {
                    ["k"] = "queued",
                    ["t"] = Cap(queued.Text, TextCap),
                    ["ord"] = ordinal,
                }.ToJsonString();
                break;

            case PendingItem pending:
                yield return new JsonObject { ["k"] = "pending", ["quiet"] = pending.Quiet }.ToJsonString();
                break;
        }
    }

    private static string ToolJson(ToolItem tool) => new JsonObject
    {
        ["k"] = "tool",
        ["n"] = tool.DisplayName,
        ["st"] = tool.Status,
        ["sum"] = Cap(tool.HeaderSummary, 400),
        ["t"] = Cap(tool.Result ?? "", ResultCap),
        ["err"] = tool.IsError,
        ["agent"] = tool.IsAgent,
        ["add"] = tool.Added,
        ["del"] = tool.Removed,
    }.ToJsonString();

    private static string PermJson(PermItem perm)
    {
        var o = new JsonObject
        {
            ["k"] = "perm",
            ["id"] = perm.RequestId,
            ["n"] = perm.ToolName,
            ["kind"] = perm.Kind,
            ["st"] = perm.State,
            ["sum"] = Cap(perm.Summary, 400),
            ["t"] = Cap(perm.Kind == "plan" ? perm.PlanMarkdown : perm.BodyText, 4_000),
            // "Always allow" widens the permission rules for the rest of the session, so the phone only offers it
            // when the CLI actually supplied a rule to add. Offering it otherwise would produce a button that
            // silently behaves like a plain Allow.
            ["always"] = perm.Suggestions is JsonArray { Count: > 0 },
        };
        if (perm.IsEdit)
        {
            var diff = new JsonArray();
            foreach (var line in perm.DiffLines.Take(400))
                diff.Add(new JsonObject { ["s"] = line.Kind, ["t"] = Cap(line.Text, 400) });
            o["diff"] = diff;
        }
        if (perm.Kind == "question")
        {
            var questions = new JsonArray();
            foreach (var q in perm.Questions)
            {
                var options = new JsonArray();
                foreach (var opt in q.Options) options.Add(opt.Label);
                questions.Add(new JsonObject
                {
                    ["q"] = q.Question,
                    ["multi"] = q.MultiSelect,
                    ["options"] = options,
                });
            }
            o["questions"] = questions;
        }
        return o.ToJsonString();
    }

    private static string Cap(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text.Length <= max ? text : text[..max] + "\n… (truncated)";
    }
}
