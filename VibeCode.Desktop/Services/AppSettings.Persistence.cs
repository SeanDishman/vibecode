using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace VibeCode.Services;

/// <summary>
/// How settings reach disk. Two things that used to be embedded in settings.json have files of their own: each saved
/// Bridge's task journal (<see cref="BridgeJournalStore"/>) and the deleted-chat tombstones
/// (<see cref="SessionTombstoneLog"/>) - together ~530 KB of a ~725 KB document. A save whose files nobody else touched
/// since this instance's last write skips the three-way merge, and with it a full read and parse of the file. The hot
/// paths - autosave and the settings half of a Bridge journal write - only snapshot on the UI thread; one background
/// writer serializes nothing, it publishes the newest snapshot's bytes.
/// </summary>
public sealed partial class AppSettings
{
    /// <summary>settings.json's own shape: everything but the journals and tombstones. Reading is unchanged, so a
    /// settings.json written by an older build still loads both, and its first save moves them out.</summary>
    private static readonly JsonSerializerOptions PersistOptions = PersistShape(indented: true);

    /// <summary>The same shape, compact, for the merge baseline, which never needs journals or tombstones.</summary>
    private static readonly JsonSerializerOptions BaselineOptions = PersistShape(indented: false);

    private static JsonSerializerOptions PersistShape(bool indented) => new()
    {
        WriteIndented = indented,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { SkipExternalState } },
    };

    // A null getter skips a property when writing and keeps its setter for reading. ShouldSerialize would not do: it is
    // handed the value, and reading SavedBridgeState.Work rebuilds a journal that is otherwise kept as bytes.
    private static void SkipExternalState(JsonTypeInfo type)
    {
        foreach (var property in type.Properties)
            if (type.Type == typeof(AppSettings) && property.Name == nameof(DeletedSessions)
                || type.Type == typeof(SavedBridgeState) && property.Name == nameof(SavedBridgeState.Work))
                property.Get = null;
    }

    /// <summary>The tombstone log as this instance last read or wrote it.</summary>
    private SessionTombstoneLog? _tombstones;

    /// <summary>settings.json as this instance last read or wrote it. Null = unknown, which takes the full merge.</summary>
    private FileStamp? _settingsStamp;

    /// <summary>Journals this instance knows are on disk, by Bridge key (guarded by <see cref="JournalLock"/>).</summary>
    private readonly Dictionary<string, JournalOnDisk> _journalsOnDisk = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The newest snapshot published to settings.json. An older one still queued must not overwrite it.</summary>
    private long _publishedRevision;

    /// <summary>This instance's snapshot waiting for the background writer (guarded by <see cref="WriterGate"/>).</summary>
    private PersistSnapshot? _queued;

    /// <summary>Orders captures across threads: a later snapshot or journal capture always carries a higher number.</summary>
    private static long _persistRevision;

    /// <summary>Held while a journal file is written or retired. SaveWork takes only this one, never
    /// <see cref="SaveLock"/>, so a dispatch waits for at most one small file - not for a whole settings write.</summary>
    private static readonly object JournalLock = new();

    private static readonly object WriterGate = new();
    private static readonly Queue<AppSettings> WriterQueue = new();
    private static bool _writerRunning;
    private static int _writesInFlight;
    private static bool _flushOnExitRegistered;
    private static Timer? _retryTimer;
    private static int _retryAttempt;

    private sealed record JournalOnDisk(string Path, long Revision, byte[] Work);

    private sealed record JournalCapture(string? HostSessionId, string Provider, DateTime SavedAt, byte[] Work);

    private sealed record ExternalWrites(int Journals, long JournalBytes, int Tombstones);

    /// <summary>One save, frozen on the thread that owns the settings: settings.json's bytes, the journals and the
    /// tombstones. The writer reads nothing else, so it never races the UI thread over the live object.</summary>
    private sealed record PersistSnapshot(AppSettings Owner, long Revision, byte[] Settings,
        IReadOnlyList<JournalCapture> Journals, string[] Tombstones, HashSet<string> BridgeKeys)
    {
        public SynchronizationContext? Ui { get; init; }
        public long CaptureTicks { get; init; }
        /// <summary><see cref="AppSettings.NoClaudeCredit"/> at capture, mirrored into the Claude homes after the write.</summary>
        public bool NoClaudeCredit { get; init; }
    }

    /// <summary>Load-time half one: the tombstones from their log, before anything filters by them. Ids an older build
    /// embedded in settings.json stay in memory and move to the log with the first save. <paramref name="stamp"/> is
    /// settings.json as it was just before it was read, so a write landing during the read does not pass for ours.</summary>
    private void AttachTombstones(FileStamp? stamp)
    {
        _settingsStamp = stamp;
        _tombstones = new SessionTombstoneLog();
        DeletedSessions.UnionWith(_tombstones.Load());
    }

    /// <summary>Load-time half two, once the roster is final: each saved Bridge's journal from its own file. Journals an
    /// older build embedded stay in memory and move to their files with the first save.</summary>
    private void AttachJournals(bool sweepOrphans)
    {
        foreach (var bridge in SavedBridges) AttachJournal(bridge);
        // Only after a normal load: a roster recovered from a backup may lack Bridges whose journals are newer than it.
        if (sweepOrphans) BridgeJournalStore.SweepOrphans(SavedBridges);
    }

    private void AttachJournal(SavedBridgeState bridge)
    {
        if (BridgeJournalStore.TryRead(bridge.HostSessionId, bridge.Provider) is not { } journal) return;
        // The file is the journal. A copy still embedded in settings.json wins only while it continues the same run and
        // was saved later: an older build kept working on it, or this build's first save was cut short.
        if (bridge.Work is { } embedded && embedded.RunId == journal.RunId && bridge.SavedAt > journal.SavedAt) return;
        bridge.AttachWorkJson(journal.Work);
        lock (JournalLock)
            _journalsOnDisk[BridgeKey(bridge.HostSessionId, bridge.Provider)] =
                new JournalOnDisk(BridgeJournalStore.PathFor(bridge.HostSessionId, bridge.Provider), 0, journal.Work);
    }

    /// <summary>Normalize, then merge with disk - fully only when another writer touched the files since this
    /// instance's last read or write. Returns true when the merge adopted another window's <see cref="ActiveAccountId"/>.</summary>
    private bool PrepareForSave(bool merge, bool activeAccountIsDeliberate, bool defaultModeIsDeliberate)
    {
        NormalizeProviderModelSlots();
        NormalizeMcpServers();
        Skills ??= new List<SkillDefinition>();
        NormalizeRecentDirectories();
        // Disk can only differ from what this instance last wrote if something else wrote it, which the single-instance
        // gate makes rare. Then the three-way merge has nothing to adopt, and skipping its read and parse of the whole
        // file is most of what a save used to cost.
        var adopted = merge && DiskChangedSinceLastSave()
            ? MergeWithDiskAndExternalState(activeAccountIsDeliberate, defaultModeIsDeliberate)
            : ApplyLocalMergeRules();
        // Again after the merge: the three-way merge can adopt a retired model or server still sitting in a settings
        // file written by another running instance. Do not publish that stale value back to disk.
        NormalizeProviderModelSlots();
        DropRetiredCodeGraphServers();
        ApplyBundledMcpSharingDefaults();   // re-derive the shared default over any per-agent copy the merge adopted from disk
        return adopted;
    }

    /// <summary>Another writer touched settings.json or the tombstone log since this instance last read or wrote them.</summary>
    private bool DiskChangedSinceLastSave() =>
        _settingsStamp is null || FileStamp.Of(FilePath) is not { } now || now != _settingsStamp
        || _tombstones is null || _tombstones.ChangedOnDisk;

    /// <summary>The full three-way merge, extended to the state that left settings.json.</summary>
    private bool MergeWithDiskAndExternalState(bool activeAccountIsDeliberate, bool defaultModeIsDeliberate)
    {
        // Tombstones stay additive, exactly as when settings.json carried them: another writer's are adopted first, so
        // the merge's deleted-wins rules below see them too.
        _tombstones ??= new SessionTombstoneLog();
        DeletedSessions.UnionWith(_tombstones.Reload());
        var adopted = MergeWithDisk(activeAccountIsDeliberate, defaultModeIsDeliberate);
        // A Bridge adopted from the file arrives without its journal; its writer left that in the Bridge's own file.
        foreach (var bridge in SavedBridges.Where(bridge => !bridge.HasWork)) AttachJournal(bridge);
        return adopted;
    }

    /// <summary>What <see cref="MergeWithDisk"/> comes down to when disk still holds exactly what this instance last
    /// wrote: there is nothing to adopt, so only the merge's own invariants are left.</summary>
    private bool ApplyLocalMergeRules()
    {
        OwnedSessions.ExceptWith(DeletedSessions);   // deleted wins
        OpenChats.RemoveAll(chat => chat.SessionId is { } id && DeletedSessions.Contains(id));
        RecentDirectories = RecentDirectoryHistory.NormalizeRemembered((RecentDirectories ?? new List<RecentDirectoryState>())
            .Where(entry => !ChatHistoryRetention.Expired(entry.LastUsed, DateTimeOffset.UtcNow)));
        SavedBridges = MergeSavedBridges(new List<SavedBridgeState>());
        BridgeMailboxWorkspaces = BridgeMailboxWorkspaces
            .Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return false;
    }

    /// <summary>Freeze one save on the thread that owns the settings.</summary>
    private PersistSnapshot CaptureSnapshot()
    {
        var journals = new List<JournalCapture>(SavedBridges.Count);
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var bridge in SavedBridges)
        {
            keys.Add(BridgeKey(bridge.HostSessionId, bridge.Provider));
            if (bridge.CaptureWorkJson() is { } work)
                journals.Add(new JournalCapture(bridge.HostSessionId, bridge.Provider, bridge.SavedAt, work));
        }
        var settings = JsonSerializer.SerializeToUtf8Bytes(this, PersistOptions);
        return new PersistSnapshot(this, Interlocked.Increment(ref _persistRevision), settings, journals,
            DeletedSessions.ToArray(), keys) { NoClaudeCredit = NoClaudeCredit };
    }

    /// <summary>Tombstones and journals go first: settings.json stops carrying them only once they are safely in files
    /// of their own. A failure throws before settings.json is touched, so the previous complete save stays in place.</summary>
    private ExternalWrites WriteExternalState(PersistSnapshot snapshot)
    {
        var tombstones = (_tombstones ??= new SessionTombstoneLog()).Append(snapshot.Tombstones);
        var journals = 0;
        long bytes = 0;
        foreach (var journal in snapshot.Journals)
        {
            if (!WriteJournal(journal, snapshot.Revision)) continue;
            journals++;
            bytes += journal.Work.Length;
        }
        return new ExternalWrites(journals, bytes, tombstones);
    }

    /// <summary>Write one journal unless disk already holds it, or holds a newer capture written meanwhile by SaveWork.</summary>
    private bool WriteJournal(JournalCapture journal, long revision)
    {
        var key = BridgeKey(journal.HostSessionId, journal.Provider);
        var path = BridgeJournalStore.PathFor(journal.HostSessionId, journal.Provider);
        lock (JournalLock)
        {
            if (_journalsOnDisk.TryGetValue(key, out var known)
                && (known.Revision > revision || known.Work.AsSpan().SequenceEqual(journal.Work) && File.Exists(path)))
                return false;
            BridgeJournalStore.Write(journal.HostSessionId, journal.Provider, journal.SavedAt, journal.Work);
            _journalsOnDisk[key] = new JournalOnDisk(path, revision, journal.Work);
            return true;
        }
    }

    /// <summary>Write one saved Bridge's journal now, on the calling thread, and nothing else. This is SaveWork's
    /// guarantee that an assignment is on disk before it is dispatched - one small file - while the rest of the settings
    /// follow through <see cref="SaveInBackground"/>. Returns null on success.</summary>
    public Exception? TrySaveBridgeJournal(string? hostSessionId, string provider)
    {
        if (FindSavedBridge(hostSessionId, provider) is not { } bridge || bridge.CaptureWorkJson() is not { } work) return null;
        try
        {
            WriteJournal(new JournalCapture(bridge.HostSessionId, bridge.Provider, bridge.SavedAt, work),
                Interlocked.Increment(ref _persistRevision));
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    /// <summary>Publish settings.json atomically (temp file + replace). A half-written settings.json would fail Load()'s
    /// parse and get silently replaced by defaults - wiping ActiveAccountId, OpenChats and the window bounds - so the
    /// write is never done in place.</summary>
    private static void WriteSettingsFile(byte[] content)
    {
        Directory.CreateDirectory(Dir);
        // Unique temp name per write: a fixed FilePath + ".tmp" is SHARED across instances, so two windows saving
        // at once either cross-write (one publishes the other's whole settings object) or race the Move.
        var tmp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }
            // File.Replace publishes the new file AND rotates the old one into settings.bak.json in a single
            // operation, so there is always a previous good copy to recover from and never a moment with neither.
            if (File.Exists(FilePath))
            {
                try { File.Replace(tmp, FilePath, BackupPath, ignoreMetadataErrors: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
                {
                    File.Move(tmp, FilePath, overwrite: true);   // Replace needs both files intact; Move always works
                }
            }
            else File.Move(tmp, FilePath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* don't let stray temps pile up */ }
            throw;
        }
    }

    /// <summary>settings.json now holds this snapshot: it becomes the merge base, and journals it no longer lists go.</summary>
    private void Published(PersistSnapshot snapshot)
    {
        _publishedRevision = Math.Max(_publishedRevision, snapshot.Revision);
        _settingsStamp = FileStamp.Of(FilePath);
        _baseline = JsonSerializer.Deserialize<AppSettings>(snapshot.Settings);
        RetireJournals(snapshot);
        lock (WriterGate)
        {
            _retryAttempt = 0;
            _retryTimer?.Dispose();
            _retryTimer = null;
        }
    }

    /// <summary>Delete the journals of Bridges this save no longer lists - removed, pruned past
    /// <see cref="MaxSavedBridges"/>, expired, or gone with a deleted host - so no journal outlives its roster.</summary>
    private void RetireJournals(PersistSnapshot snapshot)
    {
        lock (JournalLock)
            foreach (var (key, journal) in _journalsOnDisk.ToList())
            {
                // Written after this snapshot was taken: the Bridge was saved again under the same host meanwhile.
                if (snapshot.BridgeKeys.Contains(key) || journal.Revision > snapshot.Revision) continue;
                BridgeJournalStore.Delete(journal.Path);
                _journalsOnDisk.Remove(key);
            }
    }

    /// <summary>The hot-path save: autosave, and the settings half of a Bridge journal write. The calling (UI) thread only
    /// normalizes and serializes; one background writer publishes the bytes, newest snapshot first. Returns null once
    /// queued. With no UI thread to come back to, or when another writer touched the files and the full merge is due,
    /// it saves synchronously instead and returns that result, exactly like <see cref="TrySave"/>.</summary>
    public Exception? SaveInBackground()
    {
        if (SynchronizationContext.Current is not { } ui || DiskChangedSinceLastSave()) return TrySave();
        var started = Stopwatch.GetTimestamp();
        PersistSnapshot snapshot;
        try
        {
            PrepareForSave(merge: false, activeAccountIsDeliberate: false, defaultModeIsDeliberate: false);
            snapshot = CaptureSnapshot();
        }
        catch (InvalidOperationException)
        {
            return TrySave();   // a worker thread changed a collection mid-snapshot; the locked path is the one that copes
        }
        Enqueue(snapshot with { Ui = ui, CaptureTicks = Stopwatch.GetTimestamp() - started });
        Changed?.Invoke();
        return null;
    }

    private static void Enqueue(PersistSnapshot snapshot)
    {
        lock (WriterGate)
        {
            var owner = snapshot.Owner;
            if (owner._queued is null) WriterQueue.Enqueue(owner);
            if (owner._queued is null || owner._queued.Revision < snapshot.Revision) owner._queued = snapshot;   // newest wins
            if (!_flushOnExitRegistered)
            {
                _flushOnExitRegistered = true;
                // Every exit through the runtime - Shutdown, Environment.Exit, Main returning - runs this. A kill cannot,
                // but then the atomic writes leave the previous complete save in place.
                AppDomain.CurrentDomain.ProcessExit += (_, _) => FlushPendingWrites(TimeSpan.FromSeconds(5));
            }
            if (_writerRunning) return;
            _writerRunning = true;
        }
        _ = Task.Run(RunWriter);
    }

    private static void RunWriter()
    {
        while (true)
        {
            PersistSnapshot next;
            lock (WriterGate)
            {
                if (!WriterQueue.TryDequeue(out var owner))
                {
                    _writerRunning = false;
                    return;
                }
                next = owner._queued!;
                owner._queued = null;
                _writesInFlight++;
            }
            try { next.Owner.PublishQueued(next); }
            catch (Exception ex) { CrashLog.Write(ex, "SettingsWriter"); }   // the writer must survive to drain the queue
            finally
            {
                lock (WriterGate)
                {
                    _writesInFlight--;
                    Monitor.PulseAll(WriterGate);
                }
            }
        }
    }

    /// <summary>The writer's half of a background save: the same order and atomic writes as <see cref="TrySave"/>.</summary>
    private void PublishQueued(PersistSnapshot snapshot)
    {
        var started = Stopwatch.GetTimestamp();
        ExternalWrites written;
        lock (SaveLock)
        {
            if (snapshot.Revision <= _publishedRevision) return;   // a synchronous save already wrote newer state
            // Another writer touched settings.json or the tombstones since this snapshot took the fast path. Merging means
            // changing the live settings, which belong to the UI thread, so the save goes back to it.
            if (DiskChangedSinceLastSave())
            {
                snapshot.Ui?.Post(_ => RetrySave(snapshot.Ui), null);
                return;
            }
            try
            {
                written = WriteExternalState(snapshot);
                WriteSettingsFile(snapshot.Settings);
            }
            catch (Exception ex)
            {
                ScheduleRetry(snapshot.Ui, ex);
                return;
            }
            Published(snapshot);
        }
        SyncClaudeSettings(snapshot.NoClaudeCredit);   // as a synchronous save does, but off the UI thread
        SaveTiming.Record(background: true, Milliseconds(snapshot.CaptureTicks), Milliseconds(Stopwatch.GetTimestamp() - started),
            snapshot.Settings.Length, written);
    }

    /// <summary>Re-snapshot on the UI thread after a failed or handed-back save, so whatever changed meanwhile goes too.</summary>
    private void RetrySave(SynchronizationContext? ui)
    {
        if (SaveInBackground() is { } error) ScheduleRetry(ui, error);
    }

    private void ScheduleRetry(SynchronizationContext? ui, Exception error)
    {
        SaveTiming.Failed(error);
        if (ui is null) return;
        lock (WriterGate)
        {
            var delay = TimeSpan.FromSeconds(Math.Min(30, 1 << Math.Min(_retryAttempt++, 5)));
            _retryTimer?.Dispose();
            _retryTimer = new Timer(_ => ui.Post(_ => RetrySave(ui), null), null, delay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Publish whatever the background writer still holds, on the calling thread, and wait up to
    /// <paramref name="timeout"/> for a write already under way. Process exit runs this, so a clean shutdown never
    /// leaves a queued save behind. Returns false when the wait timed out.</summary>
    public static bool FlushPendingWrites(TimeSpan timeout)
    {
        while (true)
        {
            PersistSnapshot? next = null;
            lock (WriterGate)
                if (WriterQueue.TryDequeue(out var owner))
                {
                    next = owner._queued;
                    owner._queued = null;
                    _writesInFlight++;
                }
            if (next is null) break;
            try { next.Owner.PublishQueued(next); }
            catch (Exception ex) { CrashLog.Write(ex, "SettingsWriter"); }
            finally
            {
                lock (WriterGate)
                {
                    _writesInFlight--;
                    Monitor.PulseAll(WriterGate);
                }
            }
        }
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        lock (WriterGate)
            while (_writesInFlight > 0)
            {
                var left = TimeSpan.FromSeconds((deadline - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency);
                if (left <= TimeSpan.Zero || !Monitor.Wait(WriterGate, left)) return _writesInFlight == 0;
            }
        return true;
    }

    private static double Milliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    /// <summary>A synchronous save's clock: started before the merge, stopped once settings.json is published.</summary>
    private readonly record struct SaveClock(long Started)
    {
        public static SaveClock Start() => new(Stopwatch.GetTimestamp());

        public void Done(PersistSnapshot snapshot, ExternalWrites written) => SaveTiming.Record(background: false,
            Milliseconds(Stopwatch.GetTimestamp() - Started), 0, snapshot.Settings.Length, written);
    }

    /// <summary>"SettingsSave: N ms" notes in the crash log, so what saving costs stays measurable. Rate-limited to the
    /// first save of a run, then one summary per half hour at most, plus a slow save at most every ten minutes - a few
    /// dozen short entries a day however busy autosave gets. Failures are logged at most once per ten minutes.</summary>
    private static class SaveTiming
    {
        private static readonly object Gate = new();
        private static readonly TimeSpan SummaryInterval = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan QuietInterval = TimeSpan.FromMinutes(10);
        private const double SlowUiMs = 100, SlowWriteMs = 1000;
        private static bool _noted;
        private static DateTime _lastSummary, _lastSlow, _lastFailure;
        private static int _count, _failures;
        private static double _uiTotal, _uiMax, _writeTotal, _writeMax;

        /// <summary><paramref name="uiMs"/> is the time on the calling thread: a background save's snapshot, or the whole
        /// of a synchronous save. <paramref name="writerMs"/> is the background writer's share (0 for a synchronous save).</summary>
        public static void Record(bool background, double uiMs, double writerMs, int settingsBytes, ExternalWrites written)
        {
            string note;
            lock (Gate)
            {
                _count++;
                _uiTotal += uiMs;
                _uiMax = Math.Max(_uiMax, uiMs);
                _writeTotal += writerMs;
                _writeMax = Math.Max(_writeMax, writerMs);
                var now = DateTime.UtcNow;
                var slow = (uiMs >= SlowUiMs || writerMs >= SlowWriteMs) && now - _lastSlow >= QuietInterval;
                if (_noted && !slow && now - _lastSummary < SummaryInterval) return;
                var kind = background ? $"{uiMs:0.0} ms on the UI thread, {writerMs:0} ms in the background writer"
                                      : $"{uiMs:0.0} ms, synchronous";
                note = $"SettingsSave: {kind} (settings.json {settingsBytes / 1024} KB; wrote {written.Journals} Bridge journal(s), " +
                       $"{written.JournalBytes / 1024} KB; appended {written.Tombstones} tombstone(s)).{Environment.NewLine}" +
                       $"{_count} save(s) since the last note: calling thread avg {_uiTotal / _count:0.0} / max {_uiMax:0.0} ms, " +
                       $"background writer avg {_writeTotal / _count:0} / max {_writeMax:0} ms" +
                       (_failures > 0 ? $"; {_failures} failed and were retried." : ".");
                if (slow) _lastSlow = now;
                _lastSummary = now;
                _noted = true;
                _count = _failures = 0;
                _uiTotal = _uiMax = _writeTotal = _writeMax = 0;
            }
            CrashLog.Note("SettingsSave", note);
        }

        public static void Failed(Exception error)
        {
            lock (Gate)
            {
                _failures++;
                if (DateTime.UtcNow - _lastFailure < QuietInterval) return;
                _lastFailure = DateTime.UtcNow;
            }
            CrashLog.Write(error, "SettingsSaveFailed");
        }
    }
}
