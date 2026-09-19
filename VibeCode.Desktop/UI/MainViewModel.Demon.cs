using VibeCode.Services;

namespace VibeCode.UI;

/// <summary>
/// Demon Mode: a preset Bridge of 4-17 sessions with one locked orchestrator and the rest read-only workers.
///
/// It deliberately reuses the Bridge roster and the manager dispatch loop rather than inventing a second session
/// mechanism — the orchestrator IS the crowned manager, and @@DISPATCH is still how work reaches a worker. What Demon
/// Mode adds on top is a roster whose size is fixed for the life of the team, a single typable pane, an orchestrator
/// brief that stops at planning and dispatching, and a teardown that never leaves worker CLI processes behind.
///
/// A Demon team is intentionally NOT persisted: it exists for one objective, and resuming half a swarm days later
/// would restore sessions whose lanes no longer mean anything. Closing it stops every worker.
/// </summary>
public sealed partial class MainViewModel
{
    private bool _isDemonMode;

    /// <summary>True while a Demon Mode team is alive. Drives the mode's behaviour — supervision, teardown, the
    /// single-surface rule — but NOT its rendering: a live team can be parked behind another chat's Bridge, and
    /// <see cref="IsDemonSurface"/> is what decides whether the demon wall is on screen.</summary>
    public bool IsDemonMode
    {
        get => _isDemonMode;
        private set
        {
            if (!Set(ref _isDemonMode, value)) return;
            Raise(nameof(IsDemonSurface));
            Raise(nameof(DemonSummary));
        }
    }

    /// <summary>The crowned, typable pane of the live Demon team (null outside Demon Mode).</summary>
    public ChatViewModel? DemonOrchestrator =>
        _isDemonMode ? BridgePanes.FirstOrDefault(p => p.IsDemonOrchestrator) : null;

    /// <summary>
    /// True when the Demon roster is the one the Bridge surface is actually showing.
    ///
    /// This is deliberately narrower than <see cref="IsDemonMode"/>. Opening another chat's Bridge parks the Demon
    /// roster without ending it — every worker keeps running — and for as long as that is true the surface belongs
    /// to a completely unrelated roster. Rendering the demon wall off <c>IsDemonMode</c> put the orchestrator's
    /// oversized pane on top of whatever Bridge the user opened next: Demon Mode appearing in chats that have
    /// nothing to do with it. Ask what is ON SCREEN, not what exists.
    /// </summary>
    public bool IsDemonSurface => DemonOrchestrator is not null;

    public string DemonSummary
    {
        get
        {
            if (!IsDemonSurface) return "";
            var workers = BridgePanes.Count(p => p.IsDemonWorker);
            var busy = BridgePanes.Count(p => p.IsDemonWorker && p.IsWorking);
            return $"😈 Demon Mode · 1 + {workers} worker{(workers == 1 ? "" : "s")} · {busy} working";
        }
    }

    /// <summary>Only the orchestrator accepts typed input, and only when a Demon team is live. Everything the UI does
    /// to lock a worker (disabled composer, blocked Enter, blocked paste/drop/dictate) funnels through this.</summary>
    public bool CanTypeInto(ChatViewModel pane) => !pane.InputLocked;

    /// <summary>
    /// Stand up a Demon Mode team in <paramref name="cwd"/>: an orchestrator plus workers up to the roster size.
    /// Returns the orchestrator, or null when the folder is unusable. The crown is deliberately placed LAST — crowning
    /// first would make every one of the worker spawns fire a "fold this worker into the plan" update at a manager
    /// that has not been given a task yet.
    /// </summary>
    /// <param name="accountId">The AI account every session signs in as, chosen alongside the model when the team is
    /// started. Null falls back to whatever account that provider currently has selected — which is what the
    /// automation hook and any older caller get. A full team on one account is a lot of quota, so which account
    /// spends it is the user's call, not an inherited default.</param>
    /// <param name="setup">Model, thinking level and permission mode for the whole roster, chosen once when the team
    /// is started. Applied to the orchestrator before it spawns; every worker then inherits from it, so the whole
    /// roster is guaranteed to launch identically. Null keeps whatever the app defaults are.</param>
    /// <param name="sessionCount">How many sessions to stand up, orchestrator included, as chosen in Demon Mode
    /// setup. Clamped to the supported range. Null takes the remembered size — the automation hook and any older
    /// caller land there rather than silently getting the biggest team the machine allows.</param>
    public ChatViewModel? ActivateDemonMode(string cwd, string? provider = null, string? accountId = null,
        SessionSetup? setup = null, int? sessionCount = null)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return null;
        provider = ProviderModelCatalog.Normalize(provider ?? AppSettings.Current.DefaultProvider);

        // One Demon team at a time: a second one would compete for the same surface and the same worker budget. If
        // the existing one is parked behind another chat's Bridge, bring it BACK rather than reporting that a team
        // could not be started — the honest answer to "start a team" when one is already running is to show it.
        if (_isDemonMode)
        {
            if (DemonOrchestrator is null && ParkedDemonHost() is { } parked)
            {
                if (IsBridge) ParkActiveBridge(clearSurface: false);
                ActivateParkedBridge(parked);
            }
            ShowBridge = true;
            NoteBridgeActivity();
            return DemonOrchestrator;
        }
        // An ordinary bridge keeps running in the background; the Demon team just takes the surface.
        if (IsBridge) ParkActiveBridge();

        var roster = DemonModePolicy.RosterFor(sessionCount);
        var workers = roster - 1;
        _demonDispatched = _demonStallNudged = false;

        _bridgeBoard = DefaultBridgeBoard;   // a Demon team is never a fork; it always owns the project's own board
        WriteBridgeFile(cwd);
        AppendPeerToBridgeFile(cwd, DemonModePolicy.OrchestratorNumber);

        // Applied through `configure` rather than after the fact: NewChat spawns the session at the end, and the CLI
        // is handed model+effort at spawn. Setting them afterwards would run the orchestrator's own kickoff turn on
        // the wrong model. Workers then copy these off the orchestrator in SpawnDemonWorker, before they spawn too.
        var orchestrator = NewChat(cwd, title: "Demon Mode", provider: provider, accountId: accountId,
            activatePrimary: true, configure: c => setup?.ApplyToUnstarted(c));
        orchestrator.IsDemonOrchestrator = true;
        orchestrator.BridgeLabel = AgentLabel(orchestrator, DemonModePolicy.OrchestratorNumber);
        orchestrator.IsBridgeHost = true;
        orchestrator.Title = "Demon Mode";
        orchestrator.Items.Add(new DividerItem
        {
            Label = $"😈 Demon Mode — you are the ORCHESTRATOR. {workers} workers are starting; only this pane takes your input.",
        });
        BridgePanes.Add(orchestrator);
        IsDemonMode = true;

        // Every worker pane exists on the wall from this moment; none of their CLI processes do yet. See
        // StartDemonLaunchPump for why the launches are spread out instead of fired in this loop.
        for (var k = 2; k <= roster; k++) QueueDemonWorker(cwd, k, provider, orchestrator, workers);

        // Now that the whole roster exists, brief everyone with the final numbering in one pass. This runs before a
        // single worker process starts, so — unlike when the loop above spawned as it went — the brief is on the
        // command line of the session it describes rather than only reaching it on a later restart.
        RefreshDemonBriefs();

        // Crowning drives the existing manager loop: @@DISPATCH routing, worker-result relays, the lot.
        orchestrator.IsBridgeManager = true;
        foreach (var p in BridgePanes) _bridgeSeenStatus[p] = p.Status;
        orchestrator.Send(DemonModePolicy.OrchestratorKickoff(workers, cwd));

        StartSupervision(orchestrator);
        SupervisionLog.Write("Demon Mode", "TEAM-OPENED",
            $"{roster} sessions ({provider}) in {cwd}; orchestrator = agent #{DemonModePolicy.OrchestratorNumber}");

        ShowBridge = true;
        NoteBridgeActivity();
        StartBridgeIdleTimer();
        RaiseBridgeUi();
        RaiseDemonUi();
        // Last, deliberately: the wall is complete and on screen before the first worker process is launched.
        StartDemonLaunchPump();
        return orchestrator;
    }

    // ---------------------------------------------------------------- staggered launch
    //
    // Standing a team up used to run every session's spawn inside ActivateDemonMode, in one unbroken pass on the UI
    // thread, and only then set ShowBridge. Two things came out of that, and the user saw them as one long freeze:
    // the dispatcher never yielded, so nothing painted and no input was processed until the last session was away;
    // and seventeen copies of a ~244 MB CLI - each of which then starts its own MCP servers - were launched in the
    // same instant, which saturates disk and memory on any normal machine. Cold, that is minutes.
    //
    // So the roster is built first (panes, labels, briefs - all cheap, and all of it is what the user needs to SEE),
    // the surface is shown, and the processes are then started one per timer tick. Each tick is a separate dispatcher
    // op, so layout, painting and input all get their turn in between, and the machine ramps up instead of being hit
    // with the whole team at once.

    /// <summary>Gap between worker launches. Long enough that a CLI is meaningfully underway before the next one is
    /// asked for, short enough that a full roster is away in about four seconds — well before an orchestrator that
    /// has to boot, read the board and wait for the user's objective could have anything to dispatch.</summary>
    private static readonly TimeSpan DemonLaunchInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Workers whose pane exists but whose process has not been launched yet, in roster order.</summary>
    private readonly Queue<ChatViewModel> _demonLaunchQueue = new();
    private System.Windows.Threading.DispatcherTimer? _demonLaunchTimer;

    private void StartDemonLaunchPump()
    {
        if (_demonLaunchQueue.Count == 0) return;
        _demonLaunchTimer ??= new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background);
        _demonLaunchTimer.Interval = DemonLaunchInterval;
        _demonLaunchTimer.Tick -= OnDemonLaunchTick;
        _demonLaunchTimer.Tick += OnDemonLaunchTick;
        _demonLaunchTimer.Start();
    }

    private void OnDemonLaunchTick(object? sender, EventArgs e)
    {
        // The team can end between two ticks. CloseDemonTeam empties the queue, but say it here too so a tick that
        // was already in flight cannot outlive the team it belongs to.
        if (!_isDemonMode) { StopDemonLaunchPump(); return; }
        while (_demonLaunchQueue.Count > 0)
        {
            var worker = _demonLaunchQueue.Dequeue();
            // Closed out from under us while it waited its turn. Deliberately NOT "absent from BridgePanes": opening
            // another chat's Bridge PARKS the demon roster, which empties that collection without ending the team —
            // skipping on that would strand every unlaunched worker for good, and the user would come back to a wall
            // of panes that never start.
            if (worker.Status == "closed") continue;
            worker.Start();
            return;                        // one per tick — spreading the launches out is the entire point
        }
        StopDemonLaunchPump();
    }

    private void StopDemonLaunchPump()
    {
        _demonLaunchQueue.Clear();
        if (_demonLaunchTimer is null) return;
        _demonLaunchTimer.Stop();
        _demonLaunchTimer.Tick -= OnDemonLaunchTick;
    }

    /// <summary>
    /// Launch one worker out of turn, because something needs it live NOW.
    ///
    /// <see cref="ChatViewModel.Send"/> is a no-op on a pane with no session yet, and the dispatch router reads that
    /// as "not delivered" — so a work order aimed at a worker still sitting in the launch queue would leave its lane
    /// recorded as unassigned and never run. In practice the queue drains in about four seconds and an orchestrator
    /// cannot have an objective that fast, but a lane dying in silence is not a failure worth leaving to timing.
    /// No-op for any pane that is not queued, which is every pane outside a starting Demon team.
    /// </summary>
    private void StartQueuedDemonWorker(ChatViewModel pane)
    {
        if (!_demonLaunchQueue.Contains(pane)) return;
        var remaining = _demonLaunchQueue.Where(queued => !ReferenceEquals(queued, pane)).ToList();
        _demonLaunchQueue.Clear();
        foreach (var queued in remaining) _demonLaunchQueue.Enqueue(queued);
        if (pane.Status != "closed") pane.Start();
    }

    /// <summary>Build one read-only worker and put it on the wall, ready to launch. Unlike <see cref="AddBridgeAgent"/>
    /// this does not announce the join to the rest of the roster: sixteen sequential joins would otherwise stack
    /// fifteen "agent #k joined" notes onto every pane's first prompt, for a roster the whole team is briefed on once
    /// at the end anyway. The CLI process itself is started later, by the launch pump.</summary>
    private void QueueDemonWorker(string cwd, int number, string provider, ChatViewModel orchestrator, int workerCount)
    {
        AppendPeerToBridgeFile(cwd, number);
        var worker = new ChatViewModel(cwd, accountId: orchestrator.AccountId, provider: provider)
        {
            Title = $"Demon · worker {number}",
            ExcludeFromMemory = orchestrator.ExcludeFromMemory,
        };
        worker.IsDemonWorker = true;
        worker.BridgeLabel = AgentLabel(worker, number);
        worker.SetMode(orchestrator.Mode);      // workers inherit the orchestrator's permission mode
        worker.Model = orchestrator.Model;
        worker.Effort = orchestrator.Effort;
        worker.FastMode = orchestrator.FastMode;
        // Context rather than a standby TURN: sixteen "reply that you are ready" round-trips would burn a full turn
        // per worker before the user has even stated the objective. It rides the first work order instead — which is
        // exactly why it must not contain instructions of its own (see WorkerStandby).
        worker.Prelude = DemonModePolicy.WorkerStandby(number, workerCount);
        worker.Items.Add(new DividerItem { Label = $"😈 worker #{number} — read-only; waiting for the orchestrator" });
        worker.SupervisionStatus = "standing by";
        Track(worker);
        BridgePanes.Add(worker);
        _demonLaunchQueue.Enqueue(worker);
    }

    /// <summary>Rebuild every Demon pane's system-prompt appendix against the final roster, so a session that restarts
    /// later is briefed exactly as the running one was.</summary>
    private void RefreshDemonBriefs()
    {
        var roster = BridgeNumbers();
        var managerNumber = DemonModePolicy.OrchestratorNumber;
        foreach (var p in BridgePanes)
        {
            var n = BridgeNumberOf(p);
            if (n > 0) p.AppendSystemPrompt = BridgePrompt(p, n, roster, managerNumber);
        }
    }

    /// <summary>
    /// Tear the Demon team down. Every worker process is stopped, the supervision ledger is closed with a reason, and
    /// the orchestrator survives as an ordinary chat so its transcript (the plan, the decisions) stays readable.
    /// Called by the host pane's X, the idle timeout, an unrecoverable failure, and window close.
    /// </summary>
    public void CloseDemonTeam(string reason)
    {
        if (!_isDemonMode) return;
        // Before anything else: a team can be switched off while its roster is still coming up, and a worker that is
        // still in the launch queue must never spawn a process after the team it belongs to has been torn down.
        StopDemonLaunchPump();
        // Tear down the DEMON roster wherever it lives. It may be parked behind another chat's Bridge, in which case
        // the panes on screen belong to somebody else: clearing those would take an unrelated roster off the surface
        // and leave fifteen demon workers running with nothing left to switch them off.
        var onSurface = IsDemonSurface;
        var roster = onSurface ? BridgePanes.ToList() : TakeParkedDemonRoster();
        var orchestrator = roster.FirstOrDefault(p => p.IsDemonOrchestrator);
        var workers = roster.Where(p => p.IsDemonWorker).ToList();
        var wasShowing = ShowBridge && onSurface;

        StopSupervision(reason);

        foreach (var worker in workers)
        {
            worker.IsDemonWorker = false;
            worker.SupervisionStatus = "";
            ForgetPeerState(worker);
            worker.Close();
        }
        SupervisionLog.Write("Demon Mode", "TEAM-CLOSED", $"{workers.Count} workers stopped — {reason}");

        if (onSurface)
        {
            BridgePanes.Clear();
            _bridgeErrored.Clear();
            _peerTraffic.Clear();   // the team is gone; its agent numbers must not budget whatever roster comes next
        }
        _demonDispatched = _demonStallNudged = false;
        IsDemonMode = false;

        if (orchestrator is not null)
        {
            orchestrator.IsDemonOrchestrator = false;
            orchestrator.IsBridgeManager = false;
            orchestrator.IsBridgeHost = false;
            orchestrator.BridgeHasWorkingPane = false;
            orchestrator.BridgeLabel = "";
            orchestrator.SupervisionStatus = "";
            orchestrator.SupervisionAlert = false;
            // Same hole the collapsing bridge had: without this the orchestrator's session keeps the roster appendix
            // it was launched with and any staged worker note, so it goes on dispatching to workers that are closed.
            ForgetPeerState(orchestrator);
            RetireBridgeAgentContext(orchestrator, DemonTeamEndedNotice, divider: null);
            orchestrator.Items.Add(new DividerItem { Label = $"😈 Demon Mode ended — {reason}" });
            if (!Chats.Contains(orchestrator))
            {
                Chats.Insert(0, orchestrator);
                if (Chats.Any(c => c.Pinned)) ReorderPinned();
            }
            MarkOwned(orchestrator.SessionId);
            // A Demon team is never resumable, so make sure a snapshot from some earlier bridge on this host cannot
            // resurrect itself behind the orchestrator's "resume" cue.
            RemoveSavedBridgeFor(orchestrator, save: false);
        }

        if (_parkedBridges.Count == 0 && BridgePanes.Count == 0) StopBridgeIdleTimer();
        // Only take the surface away when the team WAS the surface: closing a parked team must leave whatever Bridge
        // the user is actually looking at exactly where it was.
        if (wasShowing)
        {
            ActiveChat = orchestrator ?? Chats.FirstOrDefault();
            ShowBridge = false;
            SecondaryShowBridge = false;
        }
        RaiseBridgeUi();
        RaiseDemonUi();
        SaveSession();
    }

    /// <summary>The host of a Demon roster that is parked behind another chat's Bridge, or null when the team is on
    /// screen (or there is no team).</summary>
    private ChatViewModel? ParkedDemonHost() =>
        _parkedBridges.FirstOrDefault(parked => parked.Value.Panes.Any(p => p.IsDemonOrchestrator)).Key;

    /// <summary>Lift the parked Demon roster out of the park list so it can be torn down. Returns its panes (empty
    /// when there is nothing parked, which is what a team closing mid-teardown looks like).</summary>
    private List<ChatViewModel> TakeParkedDemonRoster()
    {
        if (ParkedDemonHost() is not { } host || !_parkedBridges.Remove(host, out var parked))
            return new List<ChatViewModel>();
        return parked.Panes.ToList();
    }

    /// <summary>True once any work order has actually reached a worker in this team's lifetime.</summary>
    private bool _demonDispatched;
    private bool _demonStallNudged;

    /// <summary>
    /// The orchestrator finished a turn without writing a single dispatch block. That is fine most of the time — it
    /// is still waiting for the objective, or answering a question — but there is one case where it means the team
    /// is dead on the runway: the user HAS given a task, and not one work order has ever been delivered. That is what
    /// an orchestrator looks like when it tried to reach its workers with a messaging tool instead of @@DISPATCH:
    /// fifteen idle sessions behind a pane that reads "Dispatching all 15 lanes". The malformed-dispatch nudge cannot
    /// catch it, because a reply that never contains the literal "@@DISPATCH" never reaches the router.
    ///
    /// Deliberately fires at most once, and only before the first successful delivery: after the team is running, a
    /// dispatch-free turn is just the orchestrator talking, and nudging it would be noise.
    /// </summary>
    private void NoteDemonTurnWithoutDispatch(ChatViewModel orchestrator, IReadOnlyList<ChatViewModel> panes)
    {
        if (!_isDemonMode || _demonDispatched || _demonStallNudged) return;
        if (!ReferenceEquals(orchestrator, DemonOrchestrator)) return;
        if (_supervisionObjective.Length == 0) return;   // no task yet — standing by is the correct behaviour
        var idle = panes.Where(p => p.IsDemonWorker).Select(BridgeNumberOf).Where(n => n > 0).ToList();
        if (idle.Count == 0) return;

        _demonStallNudged = true;
        orchestrator.Items.Add(new DividerItem
        {
            Label = "😈 nothing was dispatched — that reply contained no @@DISPATCH block, so no worker was given work",
        });
        SupervisionLog.Write(orchestrator.BridgeLabel, "DISPATCH-MISSING",
            $"objective set but no work order has ever been delivered; {idle.Count} workers idle");
        SendManagerUpdate(orchestrator,
            $"NOTHING WAS DISPATCHED. Your last turn assigned no work: all {idle.Count} workers are still idle and " +
            "no session has ever received an order. If you called a tool to message them — SendMessage, Task, Agent " +
            "or anything similar — that is why: those reach subagents you spawned, and these workers are separate " +
            "CLI sessions that no tool can address. The ONLY channel is text in your reply. Send the lanes again now, " +
            "as literal blocks, one per worker:\n" +
            "@@DISPATCH agent=<number>\n<the complete self-contained prompt>\n@@END\n" +
            $"Workers waiting: {string.Join(", ", idle.Select(n => "#" + n))}.");
        NoteBridgeActivity();
    }

    /// <summary>Re-read everything the demon surface is drawn from. Called whenever the roster on the Bridge changes,
    /// not only when a team opens or closes: parking and un-parking a Demon team changes what is on screen without
    /// changing whether one exists.</summary>
    private void RaiseDemonUi()
    {
        Raise(nameof(IsDemonMode));
        Raise(nameof(DemonOrchestrator));
        Raise(nameof(IsDemonSurface));
        Raise(nameof(DemonSummary));
    }
}
