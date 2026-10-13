using System.Text.Json.Nodes;
using System.Windows.Threading;
using VibeCode.Protocol;
using VibeCode.Services;

namespace VibeCode.UI;

public sealed partial class ChatViewModel
{
    private ICodingSession? _bridgeRetrySession;
    private DispatcherTimer? _bridgeRetryTimer;
    private DateTimeOffset _bridgeRetryAt;
    private string? _bridgeRetryModel, _bridgeRetryAccount, _bridgeRetryCoordinator, _bridgeRetryCompact;
    private BridgeWorkState? _bridgeRetryWork;
    private bool _bridgeRetryManager;
    private bool _bridgeRetryExtendedChunk;

    public bool WaitingForBridgeRetry => _bridgeRetrySession is not null;
    private bool IsAdvancedBridgeParticipant => IsBridgeAgent && (BridgeCoordinatesOnly || BridgeCoordinatorAgentId is not null);
    public string BridgeRetryText => $"Retrying in {Math.Max(0, (int)Math.Ceiling((_bridgeRetryAt - DateTimeOffset.Now).TotalSeconds))}s";

    private void TrackBridgeErrorRecovery(JsonNode result, bool eligible, string? compactCommand)
    {
        CancelBridgeErrorRecovery();
        if (!eligible || !IsAdvancedBridgeParticipant || IsPeerNotificationTurn || _session is not { HasExited: false }
            || !BridgeErrorRecovery.IsTransient(result)) return;
        _bridgeRetrySession = _session;
        _bridgeRetryModel = _model;
        _bridgeRetryAccount = AccountId;
        _bridgeRetryWork = BridgeWork;
        _bridgeRetryCoordinator = BridgeCoordinatorAgentId;
        _bridgeRetryManager = IsBridgeManager;
        _bridgeRetryCompact = compactCommand;
        _bridgeRetryExtendedChunk = _activeExtendedDispatch is not null;
        _bridgeRetryAt = DateTimeOffset.Now + BridgeErrorRecovery.Delay;
        _bridgeRetryTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _bridgeRetryTimer.Tick += OnBridgeRetryTick;
        _bridgeRetryTimer.Start();
        RaiseBridgeRetryProperties();
    }

    private void RaiseBridgeRetryProperties()
    {
        Raise(nameof(WaitingForBridgeRetry)); Raise(nameof(CanInterrupt));
        Raise(nameof(ShowWorkingText)); Raise(nameof(ChatListIsWorking));
        Raise(nameof(WorkingText)); Raise(nameof(WorkingStatus));
        Raise(nameof(BridgeRetryText)); Raise(nameof(BridgeAvailabilityText));
    }

    private void CancelBridgeErrorRecovery()
    {
        if (_bridgeRetryTimer is { } timer)
        {
            timer.Stop();
            timer.Tick -= OnBridgeRetryTick;
            _bridgeRetryTimer = null;
        }
        var wasWaiting = WaitingForBridgeRetry;
        _bridgeRetrySession = null;
        _bridgeRetryWork = null;
        _bridgeRetryCompact = null;
        if (wasWaiting) RaiseBridgeRetryProperties();
    }

    private void OnBridgeRetryTick(object? sender, EventArgs e) => CheckBridgeErrorRecovery();

    private void CheckBridgeErrorRecovery()
    {
        if (!WaitingForBridgeRetry) return;
        if (!IsAdvancedBridgeParticipant || _session is null || _session.HasExited || Status == "closed" || IsWorking
            || !ReferenceEquals(_session, _bridgeRetrySession) || !ReferenceEquals(BridgeWork, _bridgeRetryWork)
            || _model != _bridgeRetryModel || AccountId != _bridgeRetryAccount
            || BridgeCoordinatorAgentId != _bridgeRetryCoordinator || IsBridgeManager != _bridgeRetryManager || _interruptRequested)
        {
            CancelBridgeErrorRecovery();
            return;
        }
        RaiseBridgeRetryProperties();
        if (DateTimeOffset.Now < _bridgeRetryAt || RewindHoldsDispatch || _pendingPerms.Count > 0 || _steerSubmitting) return;
        var compact = _bridgeRetryCompact;
        var retryChunk = _bridgeRetryExtendedChunk;
        CancelBridgeErrorRecovery(); // Take ownership before dispatch; a queued timer tick cannot send twice.
        Items.Add(new BannerItem { Level = "info", Text = "Continuing after the temporary provider error." });
        if (compact is not null) StartManualCompaction(compact, null, recovering: true);
        else if (retryChunk && ExtendedQueuePaused) ResumeExtendedQueue();
        else
        {
            // Later queued requests must not replace the interrupted task. Continue it first, then let the
            // existing FIFO drain after that turn succeeds. A failed extended chunk instead retries its head.
            if (ExtendedQueuePaused) { _extendedQueuePauseReason = null; RefreshQueueState(); }
            SendAutomaticContinuation(BridgeErrorRecovery.ContinuePrompt);
        }
    }
}
