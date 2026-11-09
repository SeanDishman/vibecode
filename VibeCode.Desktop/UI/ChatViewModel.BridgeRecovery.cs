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
    private bool _bridgeRetryTransient;
    private bool _bridgeUnknownRetryUsed;
    private bool _bridgeRetryReconnecting;

    public bool WaitingForBridgeRetry => _bridgeRetrySession is not null;
    private bool IsAdvancedBridgeParticipant => IsBridgeAgent && (BridgeCoordinatesOnly || BridgeCoordinatorAgentId is not null);
    public string BridgeRetryText => _bridgeRetryGate is { } gate ? HealthGateText(gate)
        : $"Retrying in {Math.Max(0, (int)Math.Ceiling((_bridgeRetryAt - DateTimeOffset.Now).TotalSeconds))}s";

    private void TrackBridgeErrorRecovery(JsonNode result, bool eligible, string? compactCommand)
    {
        SettleHealthProbe(result, eligible);   // before the cancel below: this turn may be the gate's probe
        CancelBridgeErrorRecovery();
        if (!eligible) _bridgeUnknownRetryUsed = false;
        if (!eligible || !IsAdvancedBridgeParticipant || IsGemini || BridgeWorkerPaused || IsPeerNotificationTurn || _session is null
            || !BridgeErrorRecovery.CanRetry(result)) return;
        var transient = BridgeErrorRecovery.IsTransient(result);
        // Unknown terminal failures get one generic continuation; recognized transport failures retain their paced retry.
        if (!transient && _bridgeUnknownRetryUsed) return;
        _bridgeRetryTransient = transient;
        _bridgeRetrySession = _session;
        _bridgeRetryModel = _model;
        _bridgeRetryAccount = AccountId;
        _bridgeRetryWork = BridgeWork;
        _bridgeRetryCoordinator = BridgeCoordinatorAgentId;
        _bridgeRetryManager = IsBridgeManager;
        _bridgeRetryCompact = compactCommand;
        _bridgeRetryExtendedChunk = _activeExtendedDispatch is not null;
        if (transient)
        {
            // A recognized outage waits on its provider and account's shared gate: one pane probes, the rest resume with it.
            JoinHealthGate();
            RaiseBridgeRetryProperties();
            return;
        }
        _bridgeRetryAt = DateTimeOffset.Now + BridgeErrorRecovery.Delay;
        _bridgeRetryTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _bridgeRetryTimer.Tick += OnBridgeRetryTick;
        _bridgeRetryTimer.Start();
        RaiseBridgeRetryProperties();
    }

    private void RaiseBridgeRetryProperties()
    {
        Raise(nameof(WaitingForBridgeRetry)); Raise(nameof(CanInterrupt)); Raise(nameof(ShowsStopButton));
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
        LeaveHealthGate();
        var wasWaiting = WaitingForBridgeRetry;
        _bridgeRetrySession = null;
        _bridgeRetryWork = null;
        _bridgeRetryCompact = null;
        _bridgeRetryReconnecting = false;
        if (wasWaiting) RaiseBridgeRetryProperties();
    }

    private void OnBridgeRetryTick(object? sender, EventArgs e) => CheckBridgeErrorRecovery();

    private void StopBridgeErrorRecovery()
    {
        var reconnecting = _bridgeRetryReconnecting;
        CancelBridgeErrorRecovery();
        if (!reconnecting) return;
        // Invalidate delayed startup callbacks too, so Stop cannot turn into a queued dispatch after reconnect.
        Interlocked.Increment(ref _startVersion);
        var session = _session;
        _session = null;
        session?.Dispose();
        Status = "error";
    }

    private void FailBridgeReconnect()
    {
        CancelBridgeErrorRecovery();
        BridgeTaskState = "failed";
        if (Status == "error") Raise(nameof(Status));
        else Status = "error";
    }

    private void CheckBridgeErrorRecovery()
    {
        if (!WaitingForBridgeRetry) return;
        var reconnectingStartup = _bridgeRetryReconnecting && Status == "starting";
        if (AuthNeeded || _bridgeRetryReconnecting && Status == "error" && _session is null)
        {
            FailBridgeReconnect();
            return;
        }
        if (!IsAdvancedBridgeParticipant || BridgeWorkerPaused || AuthNeeded || Status == "closed"
            || IsWorking && !reconnectingStartup
            || !(_session is null && reconnectingStartup) && !ReferenceEquals(_session, _bridgeRetrySession)
            || !ReferenceEquals(BridgeWork, _bridgeRetryWork)
            || _model != _bridgeRetryModel || AccountId != _bridgeRetryAccount
            || BridgeCoordinatorAgentId != _bridgeRetryCoordinator || IsBridgeManager != _bridgeRetryManager || _interruptRequested)
        {
            CancelBridgeErrorRecovery();
            return;
        }
        RaiseBridgeRetryProperties();
        // Due last: asking a gate whether it is this pane's turn can elect it, and a pane held up here would stall the rest.
        if (ModelRestartHoldsDispatch || _pendingPerms.Count > 0 || _steerSubmitting || _steerQueueHold || !BridgeRetryDue) return;
        if (_session is null || reconnectingStartup) return;
        if (_session.HasExited)
        {
            if (_bridgeRetryReconnecting) { FailBridgeReconnect(); return; }
            _bridgeRetryReconnecting = true;
            var previous = _session;
            _session = null;
            previous.Dispose();
            ResumeSessionId = SessionId ?? ResumeSessionId;
            ForkSession = false;
            _transcriptLoaded = true;
            Status = "starting";
            Items.Add(new DividerItem { Label = "Automatic recovery · reconnecting chat" });
            try { Start(); }
            catch (Exception ex)
            {
                FailBridgeReconnect();
                Items.Add(new BannerItem { Level = "error", Text = "Could not reconnect this agent: " + ex.Message });
                return;
            }
            if (AuthNeeded || _session is null && Status != "starting") FailBridgeReconnect();
            else if (_session is not null) _bridgeRetrySession = _session;
            return;
        }
        var compact = _bridgeRetryCompact;
        var retryChunk = _bridgeRetryExtendedChunk;
        if (!_bridgeRetryTransient) _bridgeUnknownRetryUsed = true;
        TakeHealthGateTurn();
        CancelBridgeErrorRecovery(); // Take ownership before dispatch; a queued timer tick cannot send twice.
        Items.Add(new BannerItem { Level = "info", Text = "Continuing after the provider error." });
        if (compact is not null) StartManualCompaction(compact, null, recovering: true);
        else if (retryChunk && ExtendedQueuePaused) ResumeExtendedQueue();
        else
        {
            // Later queued requests must not replace the interrupted task. Continue it first, then let the
            // existing FIFO drain after that turn succeeds. A failed extended chunk instead retries its head.
            if (ExtendedQueuePaused) { _extendedQueuePauseReason = null; RefreshQueueState(); }
            SendAutomaticContinuation(IsBridgeManager ? BridgeErrorRecovery.OrchestratorContinuePrompt : BridgeErrorRecovery.WorkerContinuePrompt);
        }
    }
}
