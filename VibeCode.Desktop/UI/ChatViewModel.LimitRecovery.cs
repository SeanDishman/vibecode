using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using VibeCode.Protocol;
using VibeCode.Services;

namespace VibeCode.UI;

public sealed partial class ChatViewModel
{
    private static readonly ConcurrentDictionary<string, Lazy<Task<UsageRecoverySnapshot>>> RecoveryProbes = new();
    private JsonNode? _limitRecoveryResult;
    private ICodingSession? _limitRecoverySession;
    private DispatcherTimer? _limitRecoveryTimer;
    private DateTimeOffset _limitRecoveryNextCheck, _limitRecoveryRetryAt, _limitRecoveryEarliestResume;
    private bool _limitRecoveryHasRetryHint;
    private string? _limitRecoveryCompact;
    private bool _limitRecoveryChecking;
    private int _limitRecoveryVersion, _limitRecoveryAttempts;
    // Claude streams quota exhaustion as data before its readable result: the root assistant envelope carries
    // error "rate_limit" and a rate_limit_event carries the exact reset. The result's wording has changed before
    // ("hit your limit" became "hit your session limit"), so the turn keeps these instead of trusting prose alone.
    private bool _turnRateLimited;
    private long? _turnRateLimitResetsAt;
    internal Func<Task<UsageRecoverySnapshot>>? RecoveryUsageFixture { get; set; }

    public bool WaitingForLimitReset => _limitRecoveryResult is not null && AppSettings.Current.ContinueAfterLimitResets;

    private void NoteClaudeRateLimit(JsonNode m)
    {
        if (!IsClaude) return;
        if (NodeString(m["type"]) == "rate_limit_event")
        {
            if (m["rate_limit_info"] is not JsonObject info || NodeString(info["status"]) != "rejected") return;
            _turnRateLimited = true;
            if (long.TryParse(info["resetsAt"]?.ToString(), out var resetsAt)) _turnRateLimitResetsAt = resetsAt;
            return;
        }
        if (NodeString(m["error"]) == "rate_limit"
            || m["is_api_error_message"]?.ToString() == "true" && m["api_error_status"]?.ToString() == "429")
            _turnRateLimited = true;
    }

    private void ClearTurnRateLimit()
    {
        _turnRateLimited = false;
        _turnRateLimitResetsAt = null;
    }

    private void TrackLimitRecovery(JsonNode result, bool usageLimited, string? compactCommand)
    {
        var streamedReset = _turnRateLimitResetsAt;
        ClearTurnRateLimit();
        CancelLimitRecovery(resetAttempts: !usageLimited);
        if (!usageLimited || _session is null || _session.HasExited || _interruptRequested) return;
        _limitRecoveryResult = result.DeepClone();
        // The streamed reset is exact; the result text only prints the minute.
        if (streamedReset is { } unix && _limitRecoveryResult is JsonObject stored && stored["resetsAt"] is null)
            stored["resetsAt"] = unix;
        _limitRecoverySession = _session;
        _limitRecoveryCompact = compactCommand;
        var now = DateTimeOffset.Now;
        var retryHint = UsageLimitRecovery.RetryAt(_limitRecoveryResult, now);
        _limitRecoveryHasRetryHint = retryHint is not null;
        _limitRecoveryRetryAt = retryHint
            ?? now.AddMinutes(Math.Min(30, Math.Pow(2, Math.Min(_limitRecoveryAttempts, 4)) * 3));
        _limitRecoveryEarliestResume = now.AddSeconds(_limitRecoveryAttempts == 0 ? 5 : Math.Min(300, 30 * Math.Pow(2, Math.Min(_limitRecoveryAttempts, 4))));
        _limitRecoveryNextCheck = now.AddSeconds(5);
        _extendedQueueNextUsageCheck = _limitRecoveryNextCheck;
        OnLimitRecoverySettingsChanged();
    }

    private void OnLimitRecoverySettingsChanged()
    {
        if (!_ui.CheckAccess()) { Post(OnLimitRecoverySettingsChanged); return; }
        if (!WaitingForLimitReset)
        {
            _limitRecoveryVersion++;
            _limitRecoveryTimer?.Stop();
        }
        else
        {
            _limitRecoveryTimer ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
            _limitRecoveryTimer.Tick -= OnLimitRecoveryTick;
            _limitRecoveryTimer.Tick += OnLimitRecoveryTick;
            _limitRecoveryTimer.Start();
        }
        Raise(nameof(WaitingForLimitReset));
        Raise(nameof(CanInterrupt));
        RaiseExtendedQueueProperties();
    }

    private async void OnLimitRecoveryTick(object? sender, EventArgs e)
    {
        if (DateTimeOffset.Now >= _limitRecoveryNextCheck) await CheckLimitRecoveryAsync();
    }

    private void CancelLimitRecovery(bool resetAttempts = true)
    {
        _limitRecoveryVersion++;
        _limitRecoveryTimer?.Stop();
        _limitRecoveryResult = null;
        _limitRecoverySession = null;
        _limitRecoveryCompact = null;
        if (resetAttempts) _limitRecoveryAttempts = 0;
        Raise(nameof(WaitingForLimitReset));
        Raise(nameof(CanInterrupt));
    }

    private async Task CheckLimitRecoveryAsync()
    {
        if (_limitRecoveryChecking || !WaitingForLimitReset || IsWorking || Status == "closed"
            || _session is null || _session.HasExited || !ReferenceEquals(_session, _limitRecoverySession)
            || RewindHoldsDispatch || _pendingPerms.Count > 0 || _steerSubmitting) return;
        _limitRecoveryChecking = true;
        var version = _limitRecoveryVersion;
        var selectedModel = _model;
        var selectedAccount = AccountId;
        UsageRecoverySnapshot snapshot;
        try { snapshot = await ReadRecoveryUsageAsync(); }
        catch { snapshot = UsageRecoverySnapshot.Unknown; }
        finally { _limitRecoveryChecking = false; }
        if (version != _limitRecoveryVersion || !WaitingForLimitReset || IsWorking || Status == "closed"
            || _session is null || _session.HasExited || !ReferenceEquals(_session, _limitRecoverySession)
            || selectedModel != _model || selectedAccount != AccountId
            || RewindHoldsDispatch || _pendingPerms.Count > 0 || _steerSubmitting) return;

        var now = DateTimeOffset.Now;
        if (snapshot.Available == false && snapshot.ResetsAt is { } known && known > now)
            _limitRecoveryRetryAt = known;
        var canResume = now >= _limitRecoveryEarliestResume && (snapshot.Available == true
            || snapshot.Available is null && now >= _limitRecoveryRetryAt);
        if (_limitRecoveryHasRetryHint && now < _limitRecoveryRetryAt) canResume = false;
        if (canResume)
        {
            var compact = _limitRecoveryCompact;
            var attempts = _limitRecoveryAttempts + 1;
            CancelLimitRecovery(resetAttempts: false);
            if (compact is not null) StartManualCompaction(compact, null, recovering: true);
            else if (ExtendedQueuePausedForUsage) ResumeExtendedQueue();
            else SendNow("Continue the interrupted task from where you stopped after the usage limit. Keep completed work and do not repeat actions that already succeeded.", null);
            _limitRecoveryAttempts = attempts;
            return;
        }

        var next = now.AddMinutes(1);
        var reset = snapshot.ResetsAt ?? _limitRecoveryRetryAt;
        if (reset > now && reset.AddSeconds(2) < next) next = reset.AddSeconds(2);
        _limitRecoveryNextCheck = next;
        _extendedQueueNextUsageCheck = next;
        RaiseExtendedQueueProperties();
    }

    private async Task<UsageRecoverySnapshot> ReadRecoveryUsageAsync()
    {
        if (RecoveryUsageFixture is { } fixture) return await fixture();
        var account = _session is GlmSession glm ? glm.ActiveAccountId ?? AccountId : AccountId;
        // Coalesce simultaneous checks from bridge siblings without caching a previous successful reading.
        var key = $"{Provider}:{account}:{_model}";
        var probe = RecoveryProbes.GetOrAdd(key, _ => new Lazy<Task<UsageRecoverySnapshot>>(() => ProbeRecoveryUsageAsync(account)));
        try { return await probe.Value; }
        finally { RecoveryProbes.TryRemove(new KeyValuePair<string, Lazy<Task<UsageRecoverySnapshot>>>(key, probe)); }
    }

    private Task<UsageRecoverySnapshot> ProbeRecoveryUsageAsync(string? accountId) =>
        UsageLimitRecovery.ProbeAsync(Provider, _model, accountId, _session!);
}
