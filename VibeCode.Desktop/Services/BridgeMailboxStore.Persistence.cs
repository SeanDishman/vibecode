using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VibeCode.Services;

public sealed partial class BridgeMailboxStore
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    private string SnapshotPath => Path.Combine(_directory, "mailbox-state.json");
    private readonly HashSet<string> _setupPairs = new();
    private static readonly ConcurrentDictionary<string, LiveStores> Stores = new(StringComparer.OrdinalIgnoreCase);
    private sealed class LiveStores
    {
        private readonly List<WeakReference<BridgeMailboxStore>> _stores = new();
        public void Add(BridgeMailboxStore store)
        {
            lock (_stores)
            {
                _stores.RemoveAll(weak => !weak.TryGetTarget(out _));
                _stores.Add(new WeakReference<BridgeMailboxStore>(store));
            }
        }
        public bool InUse(DateTimeOffset now)
        {
            lock (_stores) return _stores.Any(weak => weak.TryGetTarget(out var store) &&
                (Volatile.Read(ref store._attachedCount) > 0 || now.UtcTicks - Interlocked.Read(ref store._lastAccess) < TimeSpan.FromHours(1).Ticks));
        }
    }
    private static readonly ConcurrentDictionary<string, DateTimeOffset> LastCleanup = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lazy<Timer> Maintenance = new(() => new Timer(_ =>
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var root in LastCleanup.Keys) { LastCleanup[root] = now; CleanupExpired(root, now); }
    }, null, TimeSpan.FromHours(1), TimeSpan.FromHours(1)));
    private sealed class Snapshot
    {
        public int Version { get; set; } = 1;
        public List<BoxRecord> Boxes { get; set; } = new();
        public List<MessageRecord> Messages { get; set; } = new();
        public HashSet<string> SetupPairs { get; set; } = new();
        public long Sequence { get; set; }
    }
    private sealed class BoxRecord
    {
        public string Id { get; set; } = "";
        public int Number { get; set; }
        public string Label { get; set; } = "";
        public bool Closed { get; set; }
        public List<string> Entries { get; set; } = new();
    }
    private sealed class MessageRecord
    {
        public string Id { get; set; } = "";
        public string From { get; set; } = "";
        public long Sequence { get; set; }
        public string To { get; set; } = "";
        public int FromNumber { get; set; }
        public int ToNumber { get; set; }
        public string Body { get; set; } = "";
        public int Hop { get; set; }
        public DateTimeOffset Sent { get; set; }
        public DateTimeOffset? Read { get; set; }
        public DateTimeOffset? Answered { get; set; }
    }

    private void SaveSnapshot()
    {
        VerifyOwned(SnapshotPath);
        var messages = _agents.SelectMany(b => b.Entries).Distinct().ToArray();
        var boxes = _agents.Concat(messages.SelectMany(m => new[] { m.From, m.To })).Distinct();
        var snapshot = new Snapshot
        {
            SetupPairs = _setupPairs,
            Sequence = _sequence,
            Boxes = boxes.Select(b => new BoxRecord { Id = b.AgentId, Number = b.Number, Label = b.Label,
                Closed = b.Closed, Entries = b.Entries.Select(m => m.Id).ToList() }).ToList(),
            Messages = messages.Select(m => new MessageRecord { Id = m.Id, Sequence = m.Sequence, From = m.From.AgentId, To = m.To.AgentId,
                FromNumber = m.FromAtSend, ToNumber = m.ToAtSend, Body = m.Body, Hop = m.Hop,
                Sent = m.SentAt, Read = m.ReadAt, Answered = m.AnsweredAt }).ToList(),
        };
        var temporary = WriteTemporary(Stamp + "\n" + JsonSerializer.Serialize(snapshot));
        try { File.Move(temporary, SnapshotPath, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void LoadSnapshot()
    {
        VerifyOwned(SnapshotPath);
        if (!File.Exists(SnapshotPath)) return;
        if (new FileInfo(SnapshotPath).Length > 32 * 1024 * 1024) throw new IOException("Mailbox snapshot is too large to load safely.");
        try
        {
            var text = File.ReadAllText(SnapshotPath);
            var snapshot = JsonSerializer.Deserialize<Snapshot>(text[(text.IndexOf('\n') + 1)..])!;
            if (snapshot.Version != 1 || snapshot.Boxes.Count > 128 || snapshot.Messages.Count > 128 * Capacity)
                throw new InvalidDataException("Invalid mailbox snapshot.");
            _setupPairs.UnionWith(snapshot.SetupPairs);
            _sequence = Math.Max(snapshot.Sequence, snapshot.Messages.Select(m => m.Sequence).DefaultIfEmpty().Max());
            foreach (var message in snapshot.Messages.Where(m => m.Sequence <= 0).OrderBy(m => m.Sent)) message.Sequence = ++_sequence;
            var boxes = snapshot.Boxes.ToDictionary(b => b.Id, b => new Mailbox(this, b.Number, b.Label, b.Id) { Closed = b.Closed });
            var messages = snapshot.Messages.Where(m => m.Sent >= DateTimeOffset.UtcNow - Retention).ToDictionary(m => m.Id,
                m => new Message { Id = m.Id, Sequence = m.Sequence, From = boxes[m.From], To = boxes[m.To], FromAtSend = m.FromNumber,
                    ToAtSend = m.ToNumber, Body = m.Body, Hop = m.Hop, SentAt = m.Sent, ReadAt = m.Read, AnsweredAt = m.Answered });
            foreach (var row in snapshot.Boxes.Where(b => !b.Closed))
            {
                var box = boxes[row.Id];
                box.Entries.AddRange(row.Entries.Where(messages.ContainsKey).Take(Capacity).Select(id => messages[id]));
                _agents.Add(box);
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException or NullReferenceException)
        { throw new IOException("Mailbox recovery failed; existing files were preserved.", ex); }
    }

    public void Detach(Mailbox box)
    {
        if (box.Attached) { box.Attached = false; Interlocked.Decrement(ref _attachedCount); }
        Interlocked.Exchange(ref _lastAccess, DateTimeOffset.UtcNow.UtcTicks);
    }

    /// <summary>Read only the requested page. A cursor is stable even while newer messages arrive.</summary>
    public IReadOnlyList<Message> ReadPage(Mailbox box, string? before, int limit, DateTimeOffset now, out bool hasMore)
    {
        RequireAvailable(); RequireActive(box);
        IEnumerable<Message> candidates = box.Entries;
        if (before is not null && long.TryParse(before, out var sequence) && sequence > 0)
            candidates = candidates.Where(m => m.Sequence < sequence);
        else if (before is not null)
        {
            var start = box.Entries.FindIndex(m => m.Id == before) + 1;
            if (start == 0) throw new InvalidOperationException("The page cursor expired. Read the first page again.");
            candidates = candidates.Skip(start);
        }
        var rows = candidates.Take(Math.Clamp(limit, 1, 50) + 1).ToArray();
        var page = rows.Take(Math.Clamp(limit, 1, 50)).ToArray();
        hasMore = rows.Length > page.Length;
        var changed = page.Where(m => ReferenceEquals(m.To, box) && !m.ReadAt.HasValue).ToArray();
        if (changed.Length == 0) return page;
        foreach (var message in changed) message.ReadAt = now.ToUniversalTime();
        var mirrors = _agents.Where(a => a.Entries.Any(changed.Contains)).ToArray();
        try { foreach (var mirror in mirrors) Persist(mirror); SaveSnapshot(); }
        catch
        {
            foreach (var message in changed) message.ReadAt = null;
            foreach (var mirror in mirrors) try { Persist(mirror); } catch { }
            throw;
        }
        return page;
    }

    // Deleting a chat removes its body text from both sides, including a mailbox recovered without a live pane.
    public void DeleteAgent(string agentId)
    {
        var box = _agents.FirstOrDefault(b => b.AgentId == agentId);
        var changed = _agents.Where(other =>
            other.Entries.RemoveAll(m => m.From.AgentId == agentId || m.To.AgentId == agentId) > 0).ToArray();
        if (box is not null) Close(box);
        else if (changed.Length > 0)
        {
            // A removed pane is already closed, but its messages can still be in peers' inboxes.
            foreach (var other in changed) Persist(other);
            SaveSnapshot();
        }
    }

    private void TrackStoreAndScheduleCleanup(string workspace)
    {
        Stores.GetOrAdd(_directory, _ => new LiveStores()).Add(this);
        ScheduleCleanup(workspace);
    }

    public static void ScheduleCleanup(string workspace)
    {
        var root = Path.GetFullPath(workspace);
        _ = Maintenance.Value;
        var now = DateTimeOffset.UtcNow;
        if (LastCleanup.TryGetValue(root, out var last) && now - last < TimeSpan.FromHours(1)) return;
        LastCleanup[root] = now;
        _ = Task.Run(() => CleanupExpired(root, now));
    }

    /// <summary>Metadata-only, nonrecursive cleanup. Unknown files, redirects and active stores are preserved.</summary>
    public static int CleanupExpired(string workspace, DateTimeOffset now)
    {
        var root = Path.Combine(Path.GetFullPath(workspace), ".vibecode", "bridge-messages");
        var removed = 0;
        try
        {
            if (!Directory.Exists(root) || Redirected(root) || Redirected(Directory.GetParent(root)!.FullName)) return 0;
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                try
                {
                    if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out var owner) || Redirected(directory)) continue;
                    if (Stores.TryGetValue(directory, out var stores) && stores.InUse(now)) continue;
                    var stamp = $"<!-- VibeCode bridge mailbox {owner:N} -->";
                    var files = Directory.GetFileSystemEntries(directory);
                    if (files.Any(path => Directory.Exists(path) || Redirected(path) ||
                        File.GetLastWriteTimeUtc(path) > (now - Retention).UtcDateTime ||
                        !IsMetadataName(Path.GetFileName(path)) || File.ReadLines(path).FirstOrDefault() != stamp)) continue;
                    if (files.Length == 0 && Directory.GetLastWriteTimeUtc(directory) > (now - Retention).UtcDateTime) continue;
                    foreach (var path in files) File.Delete(path);
                    Directory.Delete(directory, false);
                    Stores.TryRemove(directory, out _);
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Retry next sweep. */ }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return removed;
    }

    private static bool Redirected(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    private static bool IsMetadataName(string name) => name == "mailbox-state.json" ||
        Regex.IsMatch(name, @"^agent[1-9][0-9]*messages\.md$", RegexOptions.CultureInvariant) ||
        name.EndsWith(".tmp", StringComparison.Ordinal) && Guid.TryParseExact(Path.GetFileNameWithoutExtension(name), "N", out _);
}
