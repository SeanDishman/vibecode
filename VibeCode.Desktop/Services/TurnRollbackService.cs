using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace VibeCode.Services;

/// <summary>The outcome of restoring the files captured for one prompt.</summary>
public sealed record TurnRollbackResult(bool Success, int RestoredFiles, string Message,
    IReadOnlyList<string>? Conflicts = null);

/// <summary>
/// A provider-neutral, per-prompt filesystem checkpoint. VibeCode captures source-sized files before dispatching,
/// combines provider paths with command-scoped filesystem observation, and reconciles the full eligible tree so an
/// unreported change disables undo instead of being silently left behind. Restore validates post-turn hashes, keeps a
/// durable recovery journal, preserves dirty pre-prompt work, and refuses to overwrite later user/agent edits.
/// </summary>
public sealed class TurnRollbackCheckpoint
{
    private const long MaxFileBytes = 16L * 1024 * 1024;
    private const long MaxSnapshotBytes = 256L * 1024 * 1024;
    private const int MaxSnapshotFiles = 20_000;

    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".idea", ".vs", ".agents", ".codex", ".claude", ".vibecode",
        "bin", "obj", "node_modules", "publish", "dist", "build", "target",
        ".next", "coverage", ".cache", ".venv", "venv", "__pycache__",
    };

    // Build tools scatter variant output folders alongside the canonical bin/obj: bin-agent2-desktop,
    // obj-release, obj-agent5-rewind, and so on. They are regenerated output, never hand-edited source, yet a
    // busy repo can accumulate dozens of them - enough on their own to blow past MaxSnapshotFiles. Because a
    // failed capture sets _failureReason and that silently disables the rewind button, prune the whole
    // bin-*/obj-* family, not just the two exact names.
    private static bool IsIgnoredDirectory(string name) =>
        IgnoredDirectories.Contains(name)
        || name.StartsWith("bin-", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("bin_", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("obj-", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("obj_", StringComparison.OrdinalIgnoreCase);

    // Bridge panes deliberately rewrite this project-root status board throughout overlapping turns. It is app
    // coordination metadata, not user source: snapshotting or attributing it to a shell command makes every pane look
    // like it edited the same file and disables rewind across the whole Bridge.
    private static bool IsIgnoredWorkspaceFile(string relativePath) =>
        string.Equals(relativePath, ".vibecode-bridge.md", StringComparison.OrdinalIgnoreCase);

    private static readonly object TimelineGate = new();
    private static readonly Dictionary<string, List<WeakReference<TurnRollbackCheckpoint>>> Timeline =
        new(StringComparer.OrdinalIgnoreCase);
    private static long TimelineSequence;

    // Offline regression hook; null in the desktop app. Tests use it to fault the transaction after a real move.
    internal static Action<string, string>? TestFaultInjector { get; set; }

    private readonly object _gate = new();
    private long _sequence;
    private readonly string _root;
    private readonly string _rootPrefix;
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private readonly HashSet<string> _touched = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _observedDuringTools = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _activeToolCaptures = new(StringComparer.Ordinal);
    private readonly HashSet<string> _uncapturedExisting = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CurrentFile> _unrestorableBefore = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _reparseBefore = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _controlStateBefore = new(StringComparer.OrdinalIgnoreCase);
    private int _timelineRegistered;
    private Dictionary<string, CapturedFile>? _before;
    private List<FileDelta> _deltas = new();
    private FileSystemWatcher? _watcher;
    private DateTime _toolCaptureUntilUtc;
    private bool _unsupportedToolMutation;
    private bool _usedUnnamedMutationTool;
    private DateTime? _completedUtc;
    private string? _failureReason;
    private bool _rolledBack;
    // Complete() now runs on a worker thread (sealing scans the whole workspace, which used to freeze the UI at
    // every turn end). _completedUtc flips immediately so overlap/ordering checks see the turn as finished, while
    // _reconciled stays false until the slow reconcile lands - undo is gated on it so a click mid-seal cannot
    // roll back against half-computed deltas.
    private bool _reconciled;

    private TurnRollbackCheckpoint(string cwd, bool activate)
    {
        var fullRoot = Path.GetFullPath(cwd);
        var volumeRoot = Path.GetPathRoot(fullRoot);
        _root = string.Equals(fullRoot, volumeRoot, StringComparison.OrdinalIgnoreCase)
            ? fullRoot
            : fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        _rootPrefix = _root.EndsWith(Path.DirectorySeparatorChar) || _root.EndsWith(Path.AltDirectorySeparatorChar)
            ? _root
            : _root + Path.DirectorySeparatorChar;
        try
        {
            using var lease = WorkspaceLease.Acquire(_root, TimeSpan.FromSeconds(5));
            if (!lease.Acquired) throw new IOException("Another VibeCode process is restoring this workspace.");
            RecoverPendingJournals();
            _before = CaptureWorkspace();
            foreach (var pair in CaptureControlState()) _controlStateBefore[pair.Key] = pair.Value;
            StartWatcher();
        }
        catch (Exception ex) { _failureReason = "Couldn't create a safe pre-prompt checkpoint: " + ex.Message; }
        if (activate) Activate();
    }

    public event Action? StateChanged;

    public bool CaptureSucceeded
    {
        get { lock (_gate) return _before is not null && _failureReason is null; }
    }

    public bool IsCompleted
    {
        get { lock (_gate) return _completedUtc is not null && _reconciled; }
    }

    /// <summary>Wait without blocking the UI while a stopped turn's final workspace reconciliation finishes. The
    /// rewind control uses this to turn an in-flight click into Stop-then-undo instead of silently discarding it.</summary>
    public async Task<bool> WaitUntilCompletedAsync(TimeSpan timeout)
    {
        if (IsCompleted) return true;
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnStateChanged()
        {
            if (IsCompleted) ready.TrySetResult(true);
        }

        StateChanged += OnStateChanged;
        try
        {
            OnStateChanged(); // close the subscribe/check race
            await ready.Task.WaitAsync(timeout).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        finally
        {
            StateChanged -= OnStateChanged;
        }
    }

    public bool WasRolledBack
    {
        get { lock (_gate) return _rolledBack; }
    }

    public bool CanRollback
    {
        get
        {
            lock (_gate)
                if (_completedUtc is null || !_reconciled || _failureReason is not null || _rolledBack) return false;
            return !BlockedByNewerTurn;
        }
    }

    public bool BlockedByNewerTurn => HasNewerUnresolvedTurn();

    public int ChangedFileCount
    {
        get { lock (_gate) return _deltas.Count; }
    }

    public string? UnavailableReason
    {
        get { lock (_gate) return _failureReason; }
    }

    public static TurnRollbackCheckpoint Begin(string cwd) => new(cwd, activate: true);

    /// <summary>
    /// Build the disk-bound part of a checkpoint away from the caller. The checkpoint is deliberately kept out of
    /// the shared workspace timeline until <see cref="Activate"/> runs on the UI thread immediately before dispatch;
    /// that keeps existing undo bindings thread-affine while still ensuring the snapshot predates the provider turn.
    /// </summary>
    internal static Task<TurnRollbackCheckpoint> PrepareAsync(string cwd) =>
        Task.Run(() => new TurnRollbackCheckpoint(cwd, activate: false));

    /// <summary>Regression seam that proves the supplied worker action cannot block the calling/UI thread.</summary>
    internal static Task<TurnRollbackCheckpoint> PrepareAsync(string cwd, Action workerAction) =>
        Task.Run(() =>
        {
            workerAction();
            return new TurnRollbackCheckpoint(cwd, activate: false);
        });

    /// <summary>Publish a prepared checkpoint to the per-workspace ordering timeline just before the prompt is sent.</summary>
    internal void Activate()
    {
        if (Interlocked.CompareExchange(ref _timelineRegistered, 1, 0) == 0)
        {
            _sequence = Interlocked.Increment(ref TimelineSequence);
            RegisterOnTimeline();
        }
    }

    /// <summary>
    /// Release a prepared checkpoint when its session closed before dispatch. Unlike <see cref="Complete"/>, this does
    /// not rescan the workspace: no provider turn ran, so there is nothing to reconcile or retain in undo history.
    /// </summary>
    internal void AbandonBeforeDispatch()
    {
        FileSystemWatcher? watcher;
        lock (_gate)
        {
            if (_completedUtc is not null) return;
            _completedUtc = DateTime.UtcNow;
            _reconciled = true;   // nothing ran, so there is nothing slow to reconcile
            _failureReason ??= "The prompt was canceled before it reached the provider.";
            _before = null;
            watcher = _watcher;
            _watcher = null;
        }
        if (watcher is not null)
        {
            try { watcher.EnableRaisingEvents = false; }
            catch { /* already disposed */ }
            watcher.Dispose();
        }
        UnregisterFromTimeline();
        StateChanged?.Invoke();
    }

    /// <summary>Observe filesystem events while a shell/MCP-style tool may mutate files without naming them.</summary>
    public void BeginToolCapture(string toolUseId)
    {
        if (string.IsNullOrWhiteSpace(toolUseId)) return;
        lock (_gate)
        {
            if (_completedUtc is null)
            {
                _activeToolCaptures.Add(toolUseId);
                _usedUnnamedMutationTool = true;
            }
        }
    }

    public void EndToolCapture(string toolUseId)
    {
        if (string.IsNullOrWhiteSpace(toolUseId)) return;
        lock (_gate)
        {
            if (_activeToolCaptures.Remove(toolUseId))
                _toolCaptureUntilUtc = DateTime.UtcNow.AddMilliseconds(750);
        }
    }

    public void NoteToolWorkingDirectory(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return;
        try
        {
            var full = Path.GetFullPath(Path.IsPathRooted(cwd) ? cwd : Path.Combine(_root, cwd));
            if (!string.Equals(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), _root,
                    StringComparison.OrdinalIgnoreCase)
                && !full.StartsWith(_rootPrefix, StringComparison.OrdinalIgnoreCase))
                MarkUnsafe("A tool ran outside the chat folder, so its filesystem effects cannot be undone safely.");
        }
        catch (Exception ex) { MarkUnsafe("A tool working directory was invalid: " + ex.Message); }
    }

    /// <summary>Associate one provider-reported file edit with this prompt.</summary>
    public void TrackPath(string path)
    {
        string? full;
        try { full = NormalizePath(path); }
        catch (Exception ex)
        {
            MarkUnsafe("A changed path couldn't be checkpointed safely: " + ex.Message);
            return;
        }
        if (full is null) return;
        lock (_gate)
        {
            if (_completedUtc is not null) return;
            _touched.Add(full);
        }
    }

    /// <summary>Seal the prompt checkpoint after its provider result arrives.</summary>
    public void Complete()
    {
        Dictionary<string, CapturedFile>? before;
        string[] touched;
        FileSystemWatcher? watcher;
        lock (_gate)
        {
            if (_completedUtc is not null) return;
            _completedUtc = DateTime.UtcNow;
            foreach (var observed in _observedDuringTools) _touched.Add(observed);
            before = _before;
            touched = _touched.ToArray();
            watcher = _watcher;
            _watcher = null;
        }
        if (watcher is not null)
        {
            try { watcher.EnableRaisingEvents = false; }
            catch { /* already disposed */ }
            watcher.Dispose();
        }

        if (before is not null && UnavailableReason is null)
        {
            try
            {
                var currentTree = ScanCurrentWorkspace();
                VerifyControlState();
                var changed = ChangedPaths(before, currentTree);
                var ownedElsewhere = OverlappingTouchedPaths();
                var unreported = changed.Where(path => !touched.Contains(path, StringComparer.OrdinalIgnoreCase)
                                                       && !ownedElsewhere.Contains(path)).ToList();
                if (unreported.Count > 0)
                {
                    var shown = string.Join(", ", unreported.Take(4).Select(Relative));
                    if (unreported.Count > 4) shown += $" and {unreported.Count - 4} more";
                    throw new InvalidOperationException(
                        "The provider changed files without reporting ownership (" + shown + "). "
                        + "Undo is disabled rather than risk reverting another editor's work.");
                }

                var deltas = new List<FileDelta>();
                foreach (var path in touched)
                {
                    if (_uncapturedExisting.Contains(path))
                        throw new InvalidOperationException($"{Relative(path)} was too large or unsafe to snapshot.");

                    before.TryGetValue(path, out var old);
                    currentTree.TryGetValue(path, out var current);
                    if (old is null && current is null) continue;
                    if (old is not null && MatchesCaptured(current, old)) continue;
                    deltas.Add(new FileDelta(path, old, current));
                }
                lock (_gate) _deltas = deltas;
            }
            catch (Exception ex)
            {
                MarkUnsafe("This turn can't be undone safely: " + ex.Message);
            }
        }

        lock (_gate)
        {
            _before = null; // retain only the files this turn actually changed
            _reconciled = true;
        }
        DetectOverlappingEdits();
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Validate every affected file first, then restore with same-directory temporary files. If any file differs
    /// from the recorded post-turn state, no file is touched.
    /// </summary>
    public TurnRollbackResult Rollback()
    {
        List<FileDelta> deltas;
        lock (_gate)
        {
            if (_completedUtc is null || !_reconciled)
                return new(false, 0, "Wait for this turn to finish before undoing it.");
            if (_rolledBack)
                return new(false, 0, "This prompt's changes have already been undone.");
            if (_failureReason is not null)
                return new(false, 0, _failureReason);
            deltas = _deltas.ToList();
        }

        // Restoring only the prompt/transcript performs no workspace mutation, so another active Bridge pane cannot
        // race it. Keeping the global writer gate here made research-only turns impossible to rewind while peers ran.
        if (deltas.Count == 0)
        {
            lock (_gate) _rolledBack = true;
            StateChanged?.Invoke();
            NotifyTimelineStateChanged();
            return new(true, 0, "No file changes were recorded; the prompt was restored to the composer.");
        }

        if (HasOtherActiveTurn())
            return new(false, 0, "Another chat or Bridge agent is still working in this folder. Wait for it to finish, then try again.");
        if (HasNewerUnresolvedTurn())
            return new(false, 0, "Undo the newer prompt that also changed one of these files first. Overlapping edits must be rewound newest-to-oldest, including across Bridge panes.");

        using var lease = WorkspaceLease.Acquire(_root, TimeSpan.Zero);
        if (!lease.Acquired)
            return new(false, 0, "Another VibeCode process is restoring this folder. Try again when it finishes.");

        var conflicts = ValidatePostStates(deltas);
        if (conflicts.Count > 0)
        {
            var shown = string.Join(", ", conflicts.Take(4));
            if (conflicts.Count > 4) shown += $" and {conflicts.Count - 4} more";
            return new(false, 0,
                "Undo stopped because these files changed again after this prompt: " + shown,
                conflicts);
        }


        var operationId = Guid.NewGuid().ToString("N");
        var prepared = new List<PreparedChange>();
        var succeeded = false;
        string? journalPath = null;
        try
        {
            foreach (var delta in deltas)
            {
                ValidateMutationPath(delta.Path);
                var directory = Path.GetDirectoryName(delta.Path)
                    ?? throw new InvalidOperationException("A changed file has no parent directory.");
                Directory.CreateDirectory(directory);
                ValidateMutationPath(delta.Path);
                var stem = "." + Path.GetFileName(delta.Path) + ".vibecode-undo-" + operationId;
                string? restore = null;
                if (delta.Before is not null)
                {
                    restore = Path.Combine(directory, stem + ".restore");
                    WriteDurableFile(restore, delta.Before.Content);
                    TrySetMetadata(restore, delta.Before);
                }
                prepared.Add(new PreparedChange(delta, restore, Path.Combine(directory, stem + ".backup")));
            }

            journalPath = WriteUndoJournal(prepared);

            var appliedCount = 0;
            foreach (var change in prepared)
            {
                // Close the validation-to-write race. A Bridge peer touching the file now aborts and rolls back any
                // earlier operations from this transaction.
                var now = ReadCurrent(change.Delta.Path);
                if (!SameState(now, change.Delta.After))
                    throw new IOException($"{Relative(change.Delta.Path)} changed while undo was starting.");
                ValidateMutationPath(change.Delta.Path);
                if (appliedCount == 0) TestFaultInjector?.Invoke("before-first-swap", change.Delta.Path);

                if (change.Delta.Before is not null && change.Delta.After is not null)
                {
                    File.Replace(change.RestorePath!, change.Delta.Path, change.BackupPath, ignoreMetadataErrors: true);
                    change.BackupMoved = true;
                    change.RestoreMoved = true;
                    TrySetMetadata(change.Delta.Path, change.Delta.Before);
                }
                else if (File.Exists(change.Delta.Path))
                {
                    File.Move(change.Delta.Path, change.BackupPath);
                    change.BackupMoved = true;
                }
                if (change.BackupMoved && !SameState(ReadCurrent(change.BackupPath), change.Delta.After))
                    throw new IOException($"{Relative(change.Delta.Path)} changed during the atomic swap; its newer bytes were kept in a recovery file.");
                if (!change.RestoreMoved && change.RestorePath is not null)
                {
                    File.Move(change.RestorePath, change.Delta.Path);
                    change.RestoreMoved = true;
                    TrySetMetadata(change.Delta.Path, change.Delta.Before!);
                }
                if (appliedCount++ == 0) TestFaultInjector?.Invoke("after-first-apply", change.Delta.Path);
            }

            // Catch a concurrent writer that landed between an individual move and transaction completion.
            foreach (var change in prepared)
                if (!MatchesCaptured(ReadCurrent(change.Delta.Path), change.Delta.Before))
                    throw new IOException($"{Relative(change.Delta.Path)} changed while undo was finishing.");
            foreach (var change in prepared.Where(change => change.BackupMoved))
                if (!SameState(ReadCurrent(change.BackupPath), change.Delta.After))
                    throw new IOException($"The recovery backup for {Relative(change.Delta.Path)} could not be verified.");

            foreach (var change in prepared)
                TryDelete(change.BackupPath);
            if (journalPath is not null) TryDelete(journalPath);

            succeeded = true;
            lock (_gate) _rolledBack = true;
            StateChanged?.Invoke();
            NotifyTimelineStateChanged();
            return new(true, deltas.Count,
                $"Undid this prompt's changes in {deltas.Count} file{(deltas.Count == 1 ? "" : "s")}.");
        }
        catch (Exception ex)
        {
            var recoveryErrors = Recover(prepared);
            if (recoveryErrors.Count == 0 && journalPath is not null) TryDelete(journalPath);
            var suffix = recoveryErrors.Count == 0
                ? " No files were left partially restored."
                : " Recovery also had trouble with: " + string.Join(", ", recoveryErrors.Take(4))
                  + ". Hidden recovery files were kept beside the affected paths.";
            return new(false, 0, "Undo failed: " + ex.Message + suffix);
        }
        finally
        {
            foreach (var change in prepared)
            {
                if (change.RestorePath is not null && (succeeded || !change.RecoveryFailed))
                    TryDelete(change.RestorePath);
                // A backup that recovery could not move back is the user's last recoverable copy. Never delete it.
                if (succeeded || !change.BackupMoved) TryDelete(change.BackupPath);
            }
        }
    }

    private Dictionary<string, CapturedFile> CaptureWorkspace()
    {
        if (!Directory.Exists(_root)) throw new DirectoryNotFoundException(_root);
        if (File.GetAttributes(_root).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("The chat folder itself is a symlink or junction.");
        var result = new Dictionary<string, CapturedFile>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(_root);
        long totalBytes = 0;

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            FileSystemInfo[] entries;
            try { entries = new DirectoryInfo(directory).GetFileSystemInfos(); }
            catch (Exception ex) { throw new IOException($"Couldn't read {Relative(directory)}: {ex.Message}", ex); }

            foreach (var entry in entries)
            {
                if (entry is DirectoryInfo child)
                {
                    if (IsIgnoredDirectory(child.Name)) continue;
                    if (child.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        _reparseBefore[Path.GetFullPath(child.FullName)] = ReparseFingerprint(child);
                        continue;
                    }
                    pending.Push(child.FullName);
                    continue;
                }
                if (entry is not FileInfo file) continue;
                var full = Path.GetFullPath(file.FullName);
                if (IsIgnoredWorkspaceFile(Path.GetRelativePath(_root, full))) continue;
                if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    _reparseBefore[full] = ReparseFingerprint(file);
                    _uncapturedExisting.Add(full);
                    continue;
                }
                if (file.Length > MaxFileBytes)
                {
                    _unrestorableBefore[full] = ReadCurrent(full)
                        ?? throw new IOException($"{Relative(full)} disappeared during checkpoint capture.");
                    _uncapturedExisting.Add(full);
                    continue;
                }
                if (result.Count >= MaxSnapshotFiles)
                    throw new IOException($"The project has more than {MaxSnapshotFiles:N0} source files.");
                if (totalBytes + file.Length > MaxSnapshotBytes)
                    throw new IOException($"The checkpoint would exceed {MaxSnapshotBytes / 1024 / 1024:N0} MB.");
                var captured = ReadCaptured(full);
                if (totalBytes + captured.Content.LongLength > MaxSnapshotBytes)
                    throw new IOException($"The checkpoint would exceed {MaxSnapshotBytes / 1024 / 1024:N0} MB.");
                result[full] = captured;
                totalBytes += captured.Content.LongLength;
            }
        }
        return result;
    }

    private Dictionary<string, CurrentFile> ScanCurrentWorkspace()
    {
        var result = new Dictionary<string, CurrentFile>(StringComparer.OrdinalIgnoreCase);
        var reparses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(_root);
        var files = 0;

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            FileSystemInfo[] entries;
            try { entries = new DirectoryInfo(directory).GetFileSystemInfos(); }
            catch (Exception ex) { throw new IOException($"Couldn't reconcile {Relative(directory)}: {ex.Message}", ex); }

            foreach (var entry in entries)
            {
                if (entry is DirectoryInfo child)
                {
                    if (IsIgnoredDirectory(child.Name)) continue;
                    if (child.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        reparses[Path.GetFullPath(child.FullName)] = ReparseFingerprint(child);
                        continue;
                    }
                    pending.Push(child.FullName);
                    continue;
                }
                if (entry is not FileInfo file) continue;
                var full = Path.GetFullPath(file.FullName);
                if (IsIgnoredWorkspaceFile(Path.GetRelativePath(_root, full))) continue;
                if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    reparses[full] = ReparseFingerprint(file);
                    continue;
                }
                if (++files > MaxSnapshotFiles)
                    throw new IOException($"The project grew beyond {MaxSnapshotFiles:N0} source files during this turn.");
                result[full] = ReadCurrent(full)
                    ?? throw new IOException($"{Relative(full)} disappeared while the turn was being reconciled.");
            }
        }

        if (!DictionaryEqual(_reparseBefore, reparses))
            throw new IOException("A symlink or junction changed during this turn; undo is disabled for safety.");
        return result;
    }

    private HashSet<string> ChangedPaths(Dictionary<string, CapturedFile> before,
        Dictionary<string, CurrentFile> current)
    {
        var paths = new HashSet<string>(before.Keys, StringComparer.OrdinalIgnoreCase);
        paths.UnionWith(_unrestorableBefore.Keys);
        paths.UnionWith(current.Keys);
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            before.TryGetValue(path, out var captured);
            _unrestorableBefore.TryGetValue(path, out var unrestorable);
            current.TryGetValue(path, out var now);
            var differs = captured is not null ? !MatchesCaptured(now, captured)
                : unrestorable is not null ? !SameState(now, unrestorable)
                : now is not null;
            if (differs)
            {
                if (unrestorable is not null)
                    throw new IOException($"{Relative(path)} changed but exceeded the per-file restore limit.");
                changed.Add(path);
            }
        }
        return changed;
    }

    private Dictionary<string, string> CaptureControlState()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dotGit = Path.Combine(_root, ".git");
        if (File.Exists(dotGit))
        {
            var pointer = ReadSharedText(dotGit);
            result[dotGit] = HashFile(dotGit);
            const string prefix = "gitdir:";
            if (!pointer.Trim().StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The .git worktree pointer is invalid, so Git state cannot be checkpointed safely.");
            var value = pointer.Trim()[prefix.Length..].Trim();
            if (value.Length == 0)
                throw new IOException("The .git worktree pointer has no Git directory.");
            var gitDirectory = Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(_root, value));
            if (!Directory.Exists(gitDirectory))
                throw new IOException("The linked Git worktree metadata directory does not exist.");
            CaptureGitControlDirectory(result, gitDirectory, followCommonDirectory: true);
            return result;
        }
        if (!Directory.Exists(dotGit)) return result;
        CaptureGitControlDirectory(result, dotGit, followCommonDirectory: true);
        return result;
    }

    private void CaptureGitControlDirectory(Dictionary<string, string> result, string directory,
        bool followCommonDirectory)
    {
        var fullDirectory = Path.GetFullPath(directory);
        foreach (var name in new[] { "HEAD", "index", "packed-refs", "ORIG_HEAD", "commondir" })
        {
            var path = Path.Combine(fullDirectory, name);
            if (File.Exists(path)) result[path] = HashFile(path);
        }

        CaptureGitRefs(result, Path.Combine(fullDirectory, "refs"));
        if (!followCommonDirectory) return;

        var commonPointer = Path.Combine(fullDirectory, "commondir");
        if (!File.Exists(commonPointer)) return;
        var value = ReadSharedText(commonPointer).Trim();
        if (value.Length == 0)
            throw new IOException("The linked worktree commondir pointer is empty.");
        var commonDirectory = Path.GetFullPath(Path.IsPathRooted(value)
            ? value
            : Path.Combine(fullDirectory, value));
        if (!Directory.Exists(commonDirectory))
            throw new IOException("The linked worktree common Git directory does not exist.");
        if (!string.Equals(commonDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                fullDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
            CaptureGitControlDirectory(result, commonDirectory, followCommonDirectory: false);
    }

    private static void CaptureGitRefs(Dictionary<string, string> result, string refsDirectory)
    {
        if (!Directory.Exists(refsDirectory)) return;
        var pending = new Stack<string>();
        pending.Push(refsDirectory);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var entry in new DirectoryInfo(current).GetFileSystemInfos())
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException("Git refs contain a symlink or junction that cannot be checkpointed safely.");
                if (entry is DirectoryInfo child) pending.Push(child.FullName);
                else if (entry is FileInfo file) result[file.FullName] = HashFile(file.FullName);
            }
        }
    }

    private static string ReadSharedText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private void VerifyControlState()
    {
        if (_unsupportedToolMutation)
            throw new IOException("A tool changed Git metadata or another excluded control path.");
        if (_usedUnnamedMutationTool && _reparseBefore.Count > 0)
            throw new IOException("A shell or MCP tool ran while the chat folder contained a symlink or junction; its outside-folder effects cannot be undone safely.");
        var current = CaptureControlState();
        if (!DictionaryEqual(_controlStateBefore, current))
            throw new IOException("Git metadata changed during this turn. Use Git-aware recovery for this prompt.");
    }

    private static bool DictionaryEqual(IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right) =>
        left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) && value == pair.Value);

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string ReparseFingerprint(FileSystemInfo info) =>
        $"{(int)info.Attributes}:{info.LinkTarget ?? "?"}";

    private void StartWatcher()
    {
        _watcher = new FileSystemWatcher(_root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024,
        };
        _watcher.Created += OnObservedChange;
        _watcher.Changed += OnObservedChange;
        _watcher.Deleted += OnObservedChange;
        _watcher.Renamed += OnObservedRename;
        _watcher.EnableRaisingEvents = true;
    }

    private void OnObservedChange(object sender, FileSystemEventArgs e) => ObserveToolPath(e.FullPath);

    private void OnObservedRename(object sender, RenamedEventArgs e)
    {
        ObserveToolPath(e.OldFullPath);
        ObserveToolPath(e.FullPath);
    }

    private void ObserveToolPath(string path)
    {
        lock (_gate)
        {
            if (_completedUtc is not null
                || (_activeToolCaptures.Count == 0 && DateTime.UtcNow > _toolCaptureUntilUtc)) return;
            string full;
            try { full = Path.GetFullPath(path); }
            catch { _unsupportedToolMutation = true; return; }
            if (!full.StartsWith(_rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                _unsupportedToolMutation = true;
                return;
            }
            var relative = Path.GetRelativePath(_root, full);
            if (IsIgnoredWorkspaceFile(relative)) return;
            var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (parts.Any(part => string.Equals(part, ".git", StringComparison.OrdinalIgnoreCase))
                || parts.Any(IsIgnoredDirectory)
                || HasReparsePointBetweenRootAnd(full))
            {
                _unsupportedToolMutation = true;
                return;
            }
            _observedDuringTools.Add(full);
        }
    }

    private void ValidateMutationPath(string path)
    {
        if (File.GetAttributes(_root).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("The chat folder became a symlink or junction after this prompt.");
        var normalized = NormalizePath(path);
        if (normalized is null || !string.Equals(normalized, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
            throw new IOException("A restore path no longer resolves inside the chat folder.");
    }

    private string? NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(_root, path));
        if (!full.StartsWith(_rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("a tool changed a file outside the chat folder");
        var relative = Path.GetRelativePath(_root, full);
        if (IsIgnoredWorkspaceFile(relative)) return null;
        if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(IsIgnoredDirectory))
            throw new InvalidOperationException($"{relative} is inside an excluded generated/cache directory");
        if (HasReparsePointBetweenRootAnd(full))
            throw new InvalidOperationException($"{relative} crosses a symlink or junction");
        if (Directory.Exists(full) && !File.Exists(full))
            throw new InvalidOperationException($"{relative} is a directory, not a file");
        return full;
    }

    private bool HasReparsePointBetweenRootAnd(string path)
    {
        var current = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        while (!string.IsNullOrEmpty(current) && current.StartsWith(_rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) return true;
            }
            catch (FileNotFoundException) { /* a parent may be created later */ }
            catch (DirectoryNotFoundException) { /* a parent may be created later */ }
            current = Path.GetDirectoryName(current);
        }
        return false;
    }

    private CapturedFile ReadCaptured(string path)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var before = new FileInfo(path);
            var length = before.Length;
            if (length > MaxFileBytes)
                throw new IOException($"{Relative(path)} grew beyond the {MaxFileBytes / 1024 / 1024:N0} MB per-file checkpoint limit.");
            var written = before.LastWriteTimeUtc;
            var attributes = before.Attributes;
            var bytes = File.ReadAllBytes(path);
            var after = new FileInfo(path);
            if (after.Exists && after.Length == length && after.LastWriteTimeUtc == written)
                return new(bytes, Hash(bytes), attributes, before.CreationTimeUtc, before.LastWriteTimeUtc);
        }
        throw new IOException($"{Relative(path)} changed while its checkpoint was being captured.");
    }

    private CurrentFile? ReadCurrent(string path)
    {
        if (!File.Exists(path)) return null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var before = new FileInfo(path);
            var length = before.Length;
            var written = before.LastWriteTimeUtc;
            var attributes = before.Attributes;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            var after = new FileInfo(path);
            if (after.Exists && after.Length == length && after.LastWriteTimeUtc == written)
                return new(hash, attributes, before.CreationTimeUtc, before.LastWriteTimeUtc);
        }
        throw new IOException($"{Relative(path)} changed while it was being checked.");
    }

    private List<string> ValidatePostStates(IEnumerable<FileDelta> deltas)
    {
        var conflicts = new List<string>();
        foreach (var delta in deltas)
        {
            try
            {
                if (!SameState(ReadCurrent(delta.Path), delta.After)) conflicts.Add(Relative(delta.Path));
            }
            catch { conflicts.Add(Relative(delta.Path)); }
        }
        return conflicts;
    }

    private static bool SameState(CurrentFile? left, CurrentFile? right) =>
        left is null ? right is null
            : right is not null && left.Hash == right.Hash && left.Attributes == right.Attributes;

    private static bool MatchesCaptured(CurrentFile? current, CapturedFile? captured) =>
        current is null ? captured is null
            : captured is not null && current.Hash == captured.Hash && current.Attributes == captured.Attributes;

    private static bool MatchesHash(CurrentFile? current, bool exists, string? hash) =>
        exists ? current is not null && hash is not null && current.Hash == hash : current is null;

    private static void WriteDurableFile(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            64 * 1024, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private string WriteUndoJournal(IReadOnlyList<PreparedChange> prepared)
    {
        var directory = Path.Combine(_root, ".vibecode", "undo-journals");
        EnsureInternalDirectory(directory);
        var changes = new JsonArray();
        foreach (var change in prepared)
            changes.Add(new JsonObject
            {
                ["path"] = change.Delta.Path,
                ["restore"] = change.RestorePath,
                ["backup"] = change.BackupPath,
                ["beforeExists"] = change.Delta.Before is not null,
                ["beforeHash"] = change.Delta.Before?.Hash,
                ["beforeAttributes"] = change.Delta.Before is null ? null : (int)change.Delta.Before.Attributes,
                ["beforeCreationTicks"] = change.Delta.Before?.CreationTimeUtc.Ticks,
                ["beforeLastWriteTicks"] = change.Delta.Before?.LastWriteTimeUtc.Ticks,
                ["afterExists"] = change.Delta.After is not null,
                ["afterHash"] = change.Delta.After?.Hash,
            });
        var json = new JsonObject { ["version"] = 1, ["changes"] = changes }.ToJsonString();
        var path = Path.Combine(directory, "undo-" + Guid.NewGuid().ToString("N") + ".json");
        WriteDurableFile(path, Encoding.UTF8.GetBytes(json));
        return path;
    }

    private void RecoverPendingJournals()
    {
        var directory = Path.Combine(_root, ".vibecode", "undo-journals");
        if (!Directory.Exists(directory)) return;
        EnsureInternalDirectory(directory);
        foreach (var journalPath in Directory.EnumerateFiles(directory, "undo-*.json", SearchOption.TopDirectoryOnly))
            RecoverPendingJournal(journalPath);
    }

    private void RecoverPendingJournal(string journalPath)
    {
        JsonObject journal;
        try { journal = JsonNode.Parse(File.ReadAllText(journalPath, Encoding.UTF8))?.AsObject()
                            ?? throw new InvalidDataException("empty journal"); }
        catch (Exception ex) { throw new IOException($"Couldn't read pending undo journal {Path.GetFileName(journalPath)}: {ex.Message}", ex); }
        if (journal["version"]?.GetValue<int>() != 1 || journal["changes"] is not JsonArray changes)
            throw new IOException($"Pending undo journal {Path.GetFileName(journalPath)} has an unsupported format.");

        var pending = new List<PendingJournalChange>();
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var auxiliaries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawNode in changes)
        {
            if (rawNode is not JsonObject node)
                throw new IOException("A pending undo journal contains an invalid change entry.");
            var path = node["path"]?.GetValue<string>()
                ?? throw new IOException("A pending undo journal is missing its target path.");
            var restore = node["restore"]?.GetValue<string>();
            var backup = node["backup"]?.GetValue<string>()
                ?? throw new IOException("A pending undo journal is missing its backup path.");
            var beforeExists = node["beforeExists"]?.GetValue<bool>() == true;
            var beforeHash = node["beforeHash"]?.GetValue<string>();
            var beforeAttributes = (FileAttributes)(node["beforeAttributes"]?.GetValue<int>() ?? (int)FileAttributes.Normal);
            var beforeCreationTicks = node["beforeCreationTicks"]?.GetValue<long>() ?? 0;
            var beforeLastWriteTicks = node["beforeLastWriteTicks"]?.GetValue<long>() ?? 0;
            var afterExists = node["afterExists"]?.GetValue<bool>() == true;
            var afterHash = node["afterHash"]?.GetValue<string>();

            path = Path.GetFullPath(path);
            restore = restore is null ? null : Path.GetFullPath(restore);
            backup = Path.GetFullPath(backup);
            ValidateJournalPath(path, restore, backup);
            ValidateMutationPath(path);
            if (!targets.Add(path) || auxiliaries.Contains(path))
                throw new IOException("A pending undo journal names the same target more than once.");
            foreach (var auxiliary in new[] { restore, backup }.Where(value => value is not null).Cast<string>())
            {
                if (targets.Contains(auxiliary) || !auxiliaries.Add(auxiliary))
                    throw new IOException("A pending undo journal reuses a target or recovery path.");
            }
            if ((beforeExists && string.IsNullOrWhiteSpace(beforeHash))
                || (afterExists && string.IsNullOrWhiteSpace(afterHash))
                || (!beforeExists && !afterExists)
                || (!beforeExists && restore is not null))
                throw new IOException("A pending undo journal has an invalid before/after state.");
            pending.Add(new PendingJournalChange(path, restore, backup, beforeExists, beforeHash,
                beforeAttributes, beforeCreationTicks, beforeLastWriteTicks, afterExists, afterHash));
        }

        // Phase one is deliberately read-only. A conflict in any later journal entry must not leave earlier files
        // partially rewound merely because it appeared later in the JSON array.
        foreach (var change in pending)
        {
            var current = ReadCurrent(change.Path);
            var matchesBefore = MatchesHash(current, change.BeforeExists, change.BeforeHash);
            var matchesAfter = MatchesHash(current, change.AfterExists, change.AfterHash);
            if (!matchesBefore && !matchesAfter)
                throw new IOException($"Pending undo recovery stopped because {Relative(change.Path)} has newer content.");
            change.InitiallyBefore = matchesBefore;

            if (change.RestorePath is not null && File.Exists(change.RestorePath)
                && !MatchesHash(ReadCurrent(change.RestorePath), change.BeforeExists, change.BeforeHash))
                throw new IOException($"Pending undo data for {Relative(change.Path)} is damaged.");
            if (File.Exists(change.BackupPath)
                && !MatchesHash(ReadCurrent(change.BackupPath), change.AfterExists, change.AfterHash))
                throw new IOException($"Pending undo backup for {Relative(change.Path)} contains unexpected content.");

            if (!change.InitiallyBefore)
            {
                if (File.Exists(change.BackupPath))
                    throw new IOException($"Pending undo state for {Relative(change.Path)} is inconsistent.");
                if (change.BeforeExists
                    && (change.RestorePath is null || !File.Exists(change.RestorePath)))
                    throw new IOException($"Pending undo data for {Relative(change.Path)} is missing.");
            }
        }

        // Phase two uses only no-overwrite moves or File.Replace with a same-directory backup. The backup is checked
        // immediately, so a writer landing between validation and the swap is preserved and stops recovery.
        foreach (var change in pending)
        {
            ValidateMutationPath(change.Path);
            if (change.InitiallyBefore)
            {
                if (!MatchesHash(ReadCurrent(change.Path), change.BeforeExists, change.BeforeHash))
                    throw new IOException($"Pending undo recovery stopped because {Relative(change.Path)} changed while recovery was starting.");
                ApplyJournalMetadata(change);
                continue;
            }

            if (!MatchesHash(ReadCurrent(change.Path), change.AfterExists, change.AfterHash)
                || File.Exists(change.BackupPath))
                throw new IOException($"Pending undo recovery stopped because {Relative(change.Path)} changed while recovery was starting.");

            var parent = Path.GetDirectoryName(change.Path)
                ?? throw new IOException("A pending undo path has no parent directory.");
            Directory.CreateDirectory(parent);
            ValidateMutationPath(change.Path);
            if (!change.BeforeExists)
            {
                File.Move(change.Path, change.BackupPath); // no overwrite; the moved bytes are the recovery copy
            }
            else if (change.AfterExists)
            {
                if (!MatchesHash(ReadCurrent(change.RestorePath!), exists: true, change.BeforeHash))
                    throw new IOException($"Pending undo data for {Relative(change.Path)} changed during recovery.");
                File.Replace(change.RestorePath!, change.Path, change.BackupPath, ignoreMetadataErrors: true);
                ApplyJournalMetadata(change);
            }
            else
            {
                if (!MatchesHash(ReadCurrent(change.RestorePath!), exists: true, change.BeforeHash))
                    throw new IOException($"Pending undo data for {Relative(change.Path)} changed during recovery.");
                File.Move(change.RestorePath!, change.Path); // no overwrite protects a concurrently recreated path
                ApplyJournalMetadata(change);
            }

            if (!MatchesHash(ReadCurrent(change.Path), change.BeforeExists, change.BeforeHash))
                throw new IOException($"Pending undo recovery could not verify {Relative(change.Path)}.");
            if (File.Exists(change.BackupPath)
                && !MatchesHash(ReadCurrent(change.BackupPath), change.AfterExists, change.AfterHash))
                throw new IOException($"Pending undo recovery preserved newer bytes for {Relative(change.Path)} in its recovery backup.");
        }

        // Nothing is discarded until every target is back at its before-state and every backup is proven to be the
        // recorded after-state. This makes a multi-file journal all-or-validated before cleanup.
        foreach (var change in pending)
        {
            if (!MatchesHash(ReadCurrent(change.Path), change.BeforeExists, change.BeforeHash))
                throw new IOException($"Pending undo recovery stopped because {Relative(change.Path)} changed while recovery was finishing.");
            if (File.Exists(change.BackupPath)
                && !MatchesHash(ReadCurrent(change.BackupPath), change.AfterExists, change.AfterHash))
                throw new IOException($"Pending undo backup for {Relative(change.Path)} changed while recovery was finishing.");
        }
        foreach (var change in pending)
        {
            if (change.RestorePath is not null) TryDelete(change.RestorePath);
            TryDelete(change.BackupPath);
        }
        if (pending.All(change => (change.RestorePath is null || !File.Exists(change.RestorePath))
                                  && !File.Exists(change.BackupPath)))
            TryDelete(journalPath);
        try { if (!Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(journalPath)!).Any()) Directory.Delete(Path.GetDirectoryName(journalPath)!); }
        catch { /* journal directory cleanup is cosmetic */ }
    }

    private static void ApplyJournalMetadata(PendingJournalChange change)
    {
        if (!change.BeforeExists || change.BeforeCreationTicks <= 0 || change.BeforeLastWriteTicks <= 0) return;
        TrySetMetadata(change.Path, JournalMetadata(change.BeforeHash!, change.BeforeAttributes,
            change.BeforeCreationTicks, change.BeforeLastWriteTicks));
    }

    private void ValidateJournalPath(string target, string? restore, string backup)
    {
        var fullTarget = Path.GetFullPath(target);
        if (!fullTarget.StartsWith(_rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new IOException("A pending undo journal points outside the chat folder.");
        var directory = Path.GetDirectoryName(fullTarget) ?? "";
        foreach (var auxiliary in new[] { restore, backup }.Where(value => value is not null).Cast<string>())
        {
            var full = Path.GetFullPath(auxiliary);
            if (!string.Equals(Path.GetDirectoryName(full), directory, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(full).Contains(".vibecode-undo-", StringComparison.Ordinal))
                throw new IOException("A pending undo journal contains an invalid recovery path.");
        }
    }

    private static CapturedFile JournalMetadata(string hash, FileAttributes attributes,
        long creationTicks, long lastWriteTicks) => new(Array.Empty<byte>(), hash, attributes,
        new DateTime(creationTicks, DateTimeKind.Utc), new DateTime(lastWriteTicks, DateTimeKind.Utc));

    private void EnsureInternalDirectory(string directory)
    {
        var full = Path.GetFullPath(directory);
        if (!full.StartsWith(_rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The undo journal directory resolves outside the chat folder.");
        Directory.CreateDirectory(full);
        if (HasReparsePointBetweenRootAnd(full))
            throw new IOException("The undo journal directory crosses a symlink or junction.");
    }

    private List<string> Recover(IEnumerable<PreparedChange> prepared)
    {
        var errors = new List<string>();
        foreach (var change in prepared.Reverse())
        {
            if (!change.BackupMoved && !change.RestoreMoved) continue;
            try
            {
                var current = ReadCurrent(change.Delta.Path);
                // Recovery returns the file to the recorded post-turn state. If another writer touched the restored
                // path during this tiny window, preserve their bytes and never overwrite them.
                CurrentFile? backupState = null;
                var backupMatchesRecordedAfter = true;
                if (change.BackupMoved)
                {
                    backupState = ReadCurrent(change.BackupPath)
                        ?? throw new IOException("the post-turn recovery backup is missing");
                    backupMatchesRecordedAfter = SameState(backupState, change.Delta.After);
                }

                if (change.RestoreMoved)
                {
                    if (!MatchesCaptured(current, change.Delta.Before))
                        throw new IOException("the restored path changed again");
                    if (current is not null)
                    {
                        change.RecoveryCurrentPath = change.BackupPath + ".current";
                        File.Move(change.Delta.Path, change.RecoveryCurrentPath); // no overwrite: captures exact bytes atomically
                        if (!MatchesCaptured(ReadCurrent(change.RecoveryCurrentPath), change.Delta.Before))
                        {
                            if (!File.Exists(change.Delta.Path))
                                try { File.Move(change.RecoveryCurrentPath, change.Delta.Path); }
                                catch { /* both copies remain discoverable */ }
                            throw new IOException("the file moved for recovery did not match the installed pre-turn state");
                        }
                    }
                }
                else if (current is not null)
                {
                    throw new IOException("a new file appeared at the restore path");
                }

                if (change.BackupMoved)
                {
                    File.Move(change.BackupPath, change.Delta.Path); // never overwrite a writer that raced recovery
                    if (!SameState(ReadCurrent(change.Delta.Path), backupState))
                        throw new IOException("the recovered post-turn file could not be verified");
                    change.BackupMoved = false;
                    if (!backupMatchesRecordedAfter)
                    {
                        // A writer landed after validation but before the atomic swap and was captured as the backup.
                        // Put those newest bytes back in the visible path; retain the pre-turn copy and journal for
                        // manual inspection instead of pretending the failed undo was fully recovered.
                        change.RestoreMoved = false;
                        change.RecoveryFailed = true;
                        errors.Add(Relative(change.Delta.Path));
                        continue;
                    }
                }
                else if (File.Exists(change.Delta.Path))
                {
                    throw new IOException("a writer recreated a path that recovery expected to remain absent");
                }

                if (change.RecoveryCurrentPath is not null) TryDelete(change.RecoveryCurrentPath);
                change.RestoreMoved = false;
            }
            catch
            {
                change.RecoveryFailed = true;
                errors.Add(Relative(change.Delta.Path));
            }
        }
        return errors;
    }

    private void RegisterOnTimeline()
    {
        List<TurnRollbackCheckpoint> existing = new();
        lock (TimelineGate)
        {
            if (!Timeline.TryGetValue(_root, out var entries)) Timeline[_root] = entries = new();
            entries.RemoveAll(x => !x.TryGetTarget(out _));
            existing.AddRange(entries.Select(reference => reference.TryGetTarget(out var target) ? target : null)
                .OfType<TurnRollbackCheckpoint>());
            entries.Add(new WeakReference<TurnRollbackCheckpoint>(this));
            if (entries.Count > 256) entries.RemoveRange(0, entries.Count - 256);
        }
        foreach (var checkpoint in existing) checkpoint.StateChanged?.Invoke();
    }

    private void UnregisterFromTimeline()
    {
        if (Interlocked.Exchange(ref _timelineRegistered, 0) == 0) return;
        List<TurnRollbackCheckpoint> remaining = new();
        lock (TimelineGate)
        {
            if (!Timeline.TryGetValue(_root, out var entries)) return;
            entries.RemoveAll(reference => !reference.TryGetTarget(out var checkpoint)
                                           || ReferenceEquals(checkpoint, this));
            if (entries.Count == 0)
            {
                Timeline.Remove(_root);
                return;
            }
            remaining.AddRange(entries.Select(reference => reference.TryGetTarget(out var target) ? target : null)
                .OfType<TurnRollbackCheckpoint>());
        }
        foreach (var checkpoint in remaining) checkpoint.StateChanged?.Invoke();
    }

    private void DetectOverlappingEdits()
    {
        List<TurnRollbackCheckpoint> conflicts = new();
        lock (TimelineGate)
        {
            if (!Timeline.TryGetValue(_root, out var entries)) return;
            foreach (var weak in entries)
            {
                if (!weak.TryGetTarget(out var other) || ReferenceEquals(other, this)) continue;
                if (!IntervalsOverlap(other)) continue;
                var ours = TouchedSnapshot();
                var theirs = other.TouchedSnapshot();
                var shared = ours.FirstOrDefault(theirs.Contains);
                if (shared is not null) conflicts.Add(other);
            }
        }
        foreach (var other in conflicts)
        {
            var shared = SharedPathWith(other);
            var reason = $"Two overlapping turns changed {Relative(shared)}; undo is disabled to protect the other agent's work.";
            MarkUnsafe(reason);
            other.MarkUnsafe(reason);
        }
    }

    private bool IntervalsOverlap(TurnRollbackCheckpoint other)
    {
        DateTime thisEnd;
        DateTime otherEnd;
        lock (_gate) thisEnd = _completedUtc ?? DateTime.MaxValue;
        lock (other._gate) otherEnd = other._completedUtc ?? DateTime.MaxValue;
        return _startedUtc < otherEnd && other._startedUtc < thisEnd;
    }

    private string SharedPathWith(TurnRollbackCheckpoint other)
    {
        var ours = TouchedSnapshot();
        var theirs = other.TouchedSnapshot();
        return ours.First(theirs.Contains);
    }

    private HashSet<string> TouchedSnapshot()
    {
        lock (_gate) return new HashSet<string>(_touched, StringComparer.OrdinalIgnoreCase);
    }

    private HashSet<string> OverlappingTouchedPaths()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        lock (TimelineGate)
        {
            if (!Timeline.TryGetValue(_root, out var entries)) return result;
            foreach (var weak in entries)
            {
                if (!weak.TryGetTarget(out var other) || ReferenceEquals(other, this) || !IntervalsOverlap(other)) continue;
                result.UnionWith(other.TouchedSnapshot());
            }
        }
        return result;
    }

    private bool HasOtherActiveTurn()
    {
        lock (TimelineGate)
        {
            if (!Timeline.TryGetValue(_root, out var entries)) return false;
            foreach (var weak in entries)
                if (weak.TryGetTarget(out var other) && !ReferenceEquals(other, this))
                    lock (other._gate)
                        if (other._completedUtc is null) return true;
            return false;
        }
    }

    // A newer turn only blocks rewinding this one if it changed a file THIS turn also changed: restoring the older
    // turn first would strand the newer edit to that shared file. Newer turns on disjoint files - the common case
    // across Bridge panes working different areas of one project - impose no ordering, so each pane can rewind its own
    // turn independently. Same-session newest-first ordering is enforced separately by ChatViewModel.RefreshUndoStack,
    // and per-file safety still runs at rollback time (ValidatePostStates) and for concurrent edits (DetectOverlappingEdits).
    private bool HasNewerUnresolvedTurn()
    {
        var mine = TouchedSnapshot();
        if (mine.Count == 0) return false; // an older turn that changed nothing can't strand any newer edit
        lock (TimelineGate)
        {
            if (!Timeline.TryGetValue(_root, out var entries)) return false;
            foreach (var weak in entries)
            {
                if (!weak.TryGetTarget(out var other) || ReferenceEquals(other, this) || other._sequence <= _sequence) continue;
                bool alreadyRolledBack;
                lock (other._gate) alreadyRolledBack = other._rolledBack;
                if (alreadyRolledBack) continue;
                if (other.TouchedSnapshot().Overlaps(mine)) return true;
            }
            return false;
        }
    }

    private void NotifyTimelineStateChanged()
    {
        List<TurnRollbackCheckpoint> checkpoints = new();
        lock (TimelineGate)
            if (Timeline.TryGetValue(_root, out var entries))
                checkpoints.AddRange(entries.Select(reference => reference.TryGetTarget(out var target) ? target : null)
                    .OfType<TurnRollbackCheckpoint>());
        foreach (var checkpoint in checkpoints)
            if (!ReferenceEquals(checkpoint, this)) checkpoint.StateChanged?.Invoke();
    }

    private void MarkUnsafe(string reason)
    {
        var changed = false;
        lock (_gate)
        {
            if (_rolledBack || _failureReason is not null) return;
            _failureReason = reason;
            changed = true;
        }
        if (changed) StateChanged?.Invoke();
    }

    private string Relative(string path)
    {
        var value = Path.GetRelativePath(_root, path);
        return value == "." ? Path.GetFileName(_root) : value;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* cleanup is best effort; never turn a successful restore into data loss */ }
    }

    private static void TrySetMetadata(string path, CapturedFile captured)
    {
        try
        {
            var attributes = captured.Attributes & ~FileAttributes.ReparsePoint;
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            File.SetCreationTimeUtc(path, captured.CreationTimeUtc);
            File.SetLastWriteTimeUtc(path, captured.LastWriteTimeUtc);
            File.SetAttributes(path, attributes);
        }
        catch { /* contents are authoritative; metadata restoration is best effort */ }
    }

    private sealed record CapturedFile(byte[] Content, string Hash, FileAttributes Attributes,
        DateTime CreationTimeUtc, DateTime LastWriteTimeUtc);
    private sealed record CurrentFile(string Hash, FileAttributes Attributes,
        DateTime CreationTimeUtc, DateTime LastWriteTimeUtc);
    private sealed record FileDelta(string Path, CapturedFile? Before, CurrentFile? After);

    private sealed class PendingJournalChange
    {
        public PendingJournalChange(string path, string? restorePath, string backupPath, bool beforeExists,
            string? beforeHash, FileAttributes beforeAttributes, long beforeCreationTicks,
            long beforeLastWriteTicks, bool afterExists, string? afterHash)
        {
            Path = path;
            RestorePath = restorePath;
            BackupPath = backupPath;
            BeforeExists = beforeExists;
            BeforeHash = beforeHash;
            BeforeAttributes = beforeAttributes;
            BeforeCreationTicks = beforeCreationTicks;
            BeforeLastWriteTicks = beforeLastWriteTicks;
            AfterExists = afterExists;
            AfterHash = afterHash;
        }

        public string Path { get; }
        public string? RestorePath { get; }
        public string BackupPath { get; }
        public bool BeforeExists { get; }
        public string? BeforeHash { get; }
        public FileAttributes BeforeAttributes { get; }
        public long BeforeCreationTicks { get; }
        public long BeforeLastWriteTicks { get; }
        public bool AfterExists { get; }
        public string? AfterHash { get; }
        public bool InitiallyBefore { get; set; }
    }

    private sealed class PreparedChange
    {
        public PreparedChange(FileDelta delta, string? restorePath, string backupPath)
        {
            Delta = delta;
            RestorePath = restorePath;
            BackupPath = backupPath;
        }

        public FileDelta Delta { get; }
        public string? RestorePath { get; }
        public string BackupPath { get; }
        public bool BackupMoved { get; set; }
        public bool RestoreMoved { get; set; }
        public bool RecoveryFailed { get; set; }
        public string? RecoveryCurrentPath { get; set; }
    }

    private sealed class WorkspaceLease : IDisposable
    {
        private readonly Mutex? _mutex;
        public bool Acquired { get; }

        private WorkspaceLease(Mutex? mutex, bool acquired)
        {
            _mutex = mutex;
            Acquired = acquired;
        }

        public static WorkspaceLease Acquire(string root, TimeSpan timeout)
        {
            Mutex? mutex = null;
            try
            {
                var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())))[..32];
                mutex = new Mutex(initiallyOwned: false, "Local\\VibeCode.Undo." + id);
                var acquired = false;
                try { acquired = mutex.WaitOne(timeout); }
                catch (AbandonedMutexException) { acquired = true; }
                return new WorkspaceLease(mutex, acquired);
            }
            catch
            {
                mutex?.Dispose();
                return new WorkspaceLease(null, acquired: false);
            }
        }

        public void Dispose()
        {
            if (_mutex is null) return;
            if (Acquired)
                try { _mutex.ReleaseMutex(); }
                catch { /* an abandoned/disposed lease is already exclusive no longer */ }
            _mutex.Dispose();
        }
    }
}
