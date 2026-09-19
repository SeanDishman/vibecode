using VibeCode.Services;

namespace VibeCode.UI;

/// <summary>
/// Active supervision of delegated agents.
///
/// Dispatching work is not the same as getting it done: a delegated session can hang mid-tool, end a turn with
/// nothing to say, loop on the same step, or die outright, and none of those states announce themselves. Left alone
/// the orchestrator simply waits — the user sees a busy-looking team that will never finish. This is the half that
/// refuses to wait: it keeps a ledger of every assignment, watches liveness, and walks a bounded ladder
/// (intervene → cancel and restart on the same task → the orchestrator does it itself) until every task has reached a
/// terminal state. The rules live in <see cref="AgentSupervisionPolicy"/>; this file is the driver that applies them
/// to real sessions and writes down what it did.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Everything known about one delegated task and the agent holding it. Task text is kept verbatim so an
    /// intervention, a restart and a takeover all quote the SAME assignment — silently reworded work is worse than no
    /// supervision at all, because the user cannot tell what was actually asked for.</summary>
    private sealed class SupervisedAgent
    {
        public required ChatViewModel Pane { get; set; }
        public int Number { get; set; }
        public string Label { get; set; } = "";

        /// <summary>The work order body, exactly as the orchestrator wrote it.</summary>
        public string Task { get; set; } = "";
        /// <summary>Roster numbers this task was declared to depend on (@@DISPATCH … depends=2,3).</summary>
        public List<int> Dependencies { get; } = new();

        public DateTime AssignedAt { get; set; }
        public DateTime LastActivityAt { get; set; }
        public string ActivitySignature { get; set; } = "";
        public string LastStatus { get; set; } = "";

        public SupervisionStage Stage { get; set; } = SupervisionStage.Watching;
        public int Interventions { get; set; }
        public int Restarts { get; set; }
        public DateTime? InterventionAt { get; set; }

        public string LastFingerprint { get; set; } = "";
        public int RepeatCount { get; set; }

        /// <summary>The agent's own last report — carried into a restart and a takeover as prior progress.</summary>
        public string LastReport { get; set; } = "";

        public AgentResolution Resolution { get; set; } = AgentResolution.Unresolved;
        public string ResolutionReason { get; set; } = "";

        /// <summary>A work order that could not be delivered yet (the replacement session was still booting). Flushed
        /// the moment the pane reports it is up; without this a restart can silently drop the task it exists to redo.</summary>
        public string? PendingDispatch { get; set; }

        public bool HasAssignment => Resolution == AgentResolution.Unresolved && Task.Length > 0;
        public int ElapsedSeconds => (int)Math.Max(0, (DateTime.Now - AssignedAt).TotalSeconds);
    }

    private readonly Dictionary<ChatViewModel, SupervisedAgent> _supervised = new();
    private System.Windows.Threading.DispatcherTimer? _supervisionTimer;
    private ChatViewModel? _supervisionOrchestrator;
    /// <summary>The user's original request, kept so the closing reconciliation can be checked against what was asked
    /// rather than against whatever the plan drifted into.</summary>
    private string _supervisionObjective = "";
    private bool _supervisionReconciled;

    public bool IsSupervising => _supervisionOrchestrator is not null;

    private static SupervisionSettings SupervisionConfig => SupervisionSettings.Current;

    /// <summary>Supervision is unconditional in Demon Mode — a locked read-only worker has no other way to be rescued,
    /// since the user cannot reach into its pane and unstick it by hand.</summary>
    private bool SupervisionAllowed => IsDemonMode || AppSettings.Current.AgentSupervisionEnabled;

    // ---------------------------------------------------------------- lifecycle

    public void StartSupervision(ChatViewModel orchestrator)
    {
        if (!SupervisionAllowed) return;
        _supervisionOrchestrator = orchestrator;
        _supervisionReconciled = false;
        _supervised.Clear();

        var cfg = SupervisionConfig;
        _supervisionTimer ??= new System.Windows.Threading.DispatcherTimer();
        _supervisionTimer.Interval = cfg.ScanInterval;
        _supervisionTimer.Tick -= OnSupervisionTick;
        _supervisionTimer.Tick += OnSupervisionTick;
        _supervisionTimer.Start();
        SupervisionLog.Write(orchestrator.BridgeLabel, "SUPERVISION-STARTED",
            $"stale {cfg.StaleSeconds}s, idle {cfg.IdleSeconds}s, grace {cfg.InterventionGraceSeconds}s, " +
            $"max {cfg.MaxInterventions} intervention(s), {cfg.MaxRestarts} restart(s)");
    }

    /// <summary>Stop watching and close out the ledger. Anything still open is recorded as cancelled WITH a reason —
    /// requirement one of this whole mechanism is that no delegated task is ever left in an unknown state.</summary>
    public void StopSupervision(string reason)
    {
        if (_supervisionTimer is not null)
        {
            _supervisionTimer.Stop();
            _supervisionTimer.Tick -= OnSupervisionTick;
        }
        foreach (var a in _supervised.Values.Where(a => a.HasAssignment).ToList())
            Resolve(a, AgentResolution.Cancelled, reason);
        if (_supervisionOrchestrator is not null)
            SupervisionLog.Write(_supervisionOrchestrator.BridgeLabel, "SUPERVISION-STOPPED", reason);
        _supervised.Clear();
        _supervisionOrchestrator = null;
        _supervisionObjective = "";
    }

    /// <summary>Strike one pane from the ledger, resolving any task it still held. Used when a pane is closed out from
    /// under the supervisor — the agent is gone, but its task still has to end in a known state.</summary>
    private void RetireSupervisedPane(ChatViewModel pane, string reason)
    {
        if (!_supervised.TryGetValue(pane, out var a)) return;
        if (a.HasAssignment)
        {
            Resolve(a, AgentResolution.Cancelled, reason);
            NotifyOrchestrator($"{a.Label} was closed while it still held a task ({reason}). Its lane is now unowned — " +
                               "reassign the remainder to a free worker, or do it yourself if none are free.");
        }
        _supervised.Remove(pane);
        pane.SupervisionStatus = "";
        pane.SupervisionAlert = false;
        MaybeReconcile();
    }

    /// <summary>Remember what the user actually asked for, so the final reconciliation is measured against it.</summary>
    public void NoteSupervisionObjective(string objective)
    {
        if (string.IsNullOrWhiteSpace(objective) || _supervisionOrchestrator is null) return;
        _supervisionObjective = objective.Trim();
        _supervisionReconciled = false;   // a new objective reopens the team
    }

    // ---------------------------------------------------------------- ledger

    /// <summary>Record that a work order was delivered. Every dispatch — first, re-dispatch, restart — lands here, so
    /// the ledger and the sessions can never disagree about what an agent was told to do.</summary>
    private void NoteDispatch(ChatViewModel worker, string task, IReadOnlyList<int>? dependencies)
    {
        if (_supervisionOrchestrator is null || task.Length == 0) return;
        if (!_supervised.TryGetValue(worker, out var a))
        {
            a = new SupervisedAgent { Pane = worker };
            _supervised[worker] = a;
        }
        a.Number = BridgeNumberOf(worker);
        a.Label = worker.BridgeLabel.Length > 0 ? worker.BridgeLabel : worker.AgentDisplay;
        a.Task = task;
        a.Dependencies.Clear();
        if (dependencies is { Count: > 0 }) a.Dependencies.AddRange(dependencies.Distinct().Where(d => d != a.Number));
        a.AssignedAt = DateTime.Now;
        a.LastActivityAt = DateTime.Now;
        a.ActivitySignature = ActivitySignatureOf(worker);
        a.Stage = SupervisionStage.Watching;
        a.Interventions = 0;
        a.RepeatCount = 0;
        a.LastFingerprint = "";
        a.LastReport = "";
        a.Resolution = AgentResolution.Unresolved;
        a.ResolutionReason = "";
        _supervisionReconciled = false;
        UpdatePaneSupervisionStatus(a);
        SupervisionLog.Write(a.Label, "DISPATCHED",
            (a.Dependencies.Count > 0 ? $"depends on {string.Join(", ", a.Dependencies.Select(d => "#" + d))}; " : "")
            + AgentSupervisionPolicy.Excerpt(task, 160));
    }

    private void Resolve(SupervisedAgent a, AgentResolution resolution, string reason)
    {
        a.Resolution = resolution;
        a.ResolutionReason = reason;
        a.PendingDispatch = null;
        UpdatePaneSupervisionStatus(a);
        SupervisionLog.Write(a.Label, resolution.ToString().ToUpperInvariant(), reason);
    }

    /// <summary>A cheap fingerprint of "is anything still happening in this pane". Deliberately not elapsed-time based
    /// (that always changes, so nothing would ever look stale) and not token-count based (a stalled tool call keeps
    /// none moving): transcript growth plus the tail of the newest text is what actually tracks progress.</summary>
    private static string ActivitySignatureOf(ChatViewModel pane)
    {
        var count = pane.Items.Count;
        var tailLength = 0;
        for (var i = pane.Items.Count - 1; i >= 0 && i >= pane.Items.Count - 3; i--)
            if (pane.Items[i] is TextItem { HasText: true } t) { tailLength = t.Text.Length; break; }
        return $"{pane.Status}|{count}|{tailLength}";
    }

    // ---------------------------------------------------------------- observation

    /// <summary>Fed by <see cref="Track"/> on every pane status change, alongside the manager loop. Turn endings are
    /// where a task normally resolves; errors are where the ladder normally starts.</summary>
    private void OnSupervisionStatusChanged(ChatViewModel pane, string previous)
    {
        if (_supervisionOrchestrator is null) return;

        if (pane.Status == "closed") { _supervised.Remove(pane); return; }
        if (!_supervised.TryGetValue(pane, out var a)) return;

        a.LastActivityAt = DateTime.Now;
        a.ActivitySignature = ActivitySignatureOf(pane);
        a.LastStatus = pane.Status;

        // A restarted session could not be handed its work order while it was still booting. It can now.
        if (a.PendingDispatch is { Length: > 0 } pending && pane.Status is "idle" or "running")
        {
            if (pane.Send(pending))
            {
                a.PendingDispatch = null;
                SupervisionLog.Write(a.Label, "REDISPATCH-FLUSHED", "work order delivered once the session came up");
            }
        }

        if (previous == "running" && pane.Status == "idle" && !pane.IsPeerNotificationTurn) OnSupervisedTurnEnded(a);
        UpdatePaneSupervisionStatus(a);
    }

    private void OnSupervisedTurnEnded(SupervisedAgent a)
    {
        var reply = a.Pane.LastTurnReplyText();
        var fingerprint = AgentSupervisionPolicy.Fingerprint(reply);
        if (fingerprint.Length > 0 && fingerprint == a.LastFingerprint) a.RepeatCount++;
        else { a.RepeatCount = 0; a.LastFingerprint = fingerprint; }

        if (!a.HasAssignment) return;

        // An interrupted or empty turn is NOT a result. Leaving the assignment open is the point: the scan will pick
        // it up as idle-with-unfinished-work rather than the task quietly evaporating.
        if (reply.Trim().Length == 0) return;

        a.LastReport = Tail(reply, 2000);

        if (AgentSupervisionPolicy.DeclaresCannotComplete(reply))
        {
            // A clear, final "I can't" is an answer. Restarting it would just buy the same answer more slowly, so the
            // task goes straight to the orchestrator.
            Resolve(a, AgentResolution.Failed, "agent reported it cannot complete the task");
            TakeOverTask(a, "the agent reported it cannot complete the task");
            return;
        }

        // Only the agent's own explicit completion marker closes an assignment. "It said something" is not the same as
        // "it did the work" — a worker replying "Ready, standing by" ends a perfectly non-empty turn, and accepting
        // that as a delivered result would silently record undone work as done. Anything else leaves the task open for
        // the scan to nudge.
        if (!AgentSupervisionPolicy.DeclaresComplete(reply)) return;

        Resolve(a, AgentResolution.Completed, $"returned a result after {AgentSupervisionPolicy.Duration(a.ElapsedSeconds)}");
        MaybeReconcile();
    }

    // ---------------------------------------------------------------- the scan

    private void OnSupervisionTick(object? sender, EventArgs e)
    {
        if (_supervisionOrchestrator is null) { StopSupervision("orchestrator gone"); return; }
        if (!BridgePanes.Contains(_supervisionOrchestrator))
        {
            StopSupervision("the orchestrator left the team");
            return;
        }

        var cfg = SupervisionConfig;
        if (_supervisionTimer is not null && _supervisionTimer.Interval != cfg.ScanInterval)
            _supervisionTimer.Interval = cfg.ScanInterval;   // a settings change applies to the running team

        var now = DateTime.Now;
        foreach (var a in _supervised.Values.ToList())
        {
            if (!BridgePanes.Contains(a.Pane))
            {
                if (a.HasAssignment) Resolve(a, AgentResolution.Cancelled, "its pane was closed while it held a task");
                _supervised.Remove(a.Pane);
                continue;
            }

            // Liveness heartbeat: any transcript movement counts, whatever produced it.
            var signature = ActivitySignatureOf(a.Pane);
            if (signature != a.ActivitySignature)
            {
                a.ActivitySignature = signature;
                a.LastActivityAt = now;
            }

            if (!a.HasAssignment) { UpdatePaneSupervisionStatus(a); continue; }

            var health = AgentSupervisionPolicy.Diagnose(
                hasAssignment: true,
                isWorking: a.Pane.IsWorking,
                sessionFailed: a.Pane.Status == "error",
                secondsSinceActivity: (now - a.LastActivityAt).TotalSeconds,
                secondsSinceAssigned: (now - a.AssignedAt).TotalSeconds,
                repeatCount: a.RepeatCount,
                cfg,
                blockedOnUser: IsBlockedOnUser(a.Pane));

            if (health == SupervisionHealth.Healthy) { UpdatePaneSupervisionStatus(a); continue; }

            // An intervention that is still inside its grace period must be allowed to land. Restarting an agent that
            // is in the middle of writing its answer is the one failure mode worse than waiting.
            if (a.Stage == SupervisionStage.Intervened && a.InterventionAt is { } at
                && !AgentSupervisionPolicy.InterventionExpired((now - at).TotalSeconds, cfg))
            {
                UpdatePaneSupervisionStatus(a);
                continue;
            }

            // A blocked agent is waiting on a prompt nobody can answer; a fresh session would stop at the identical
            // wall, so the restart rung is skipped rather than burned.
            var pointless = health == SupervisionHealth.Blocked;
            switch (AgentSupervisionPolicy.Escalate(a.Stage, a.Interventions, a.Restarts, cfg, pointless))
            {
                case SupervisionAction.Intervene: Intervene(a, health); break;
                case SupervisionAction.Restart: RestartAgent(a, health); break;
                case SupervisionAction.TakeOver:
                    Resolve(a, AgentResolution.Failed, pointless
                        ? "blocked on an approval its pane cannot receive"
                        : $"exhausted {a.Restarts} restart(s) while {Describe(health)}");
                    TakeOverTask(a, Describe(health));
                    break;
            }
            UpdatePaneSupervisionStatus(a);
        }

        RefreshDemonActivity();
        MaybeReconcile();
    }

    private static string Describe(SupervisionHealth health) => health switch
    {
        SupervisionHealth.Stale => "stalled with no output",
        SupervisionHealth.Idle => "idle without returning a result",
        SupervisionHealth.Looping => "repeating the same step",
        SupervisionHealth.Failed => "in an errored session",
        SupervisionHealth.Blocked => "blocked on an approval nobody can give it",
        _ => "unresponsive",
    };

    /// <summary>True when the pane is sitting on an unanswered question, plan review or permission card. For a Demon
    /// Mode worker that is a hard deadlock — its pane is read-only, so the card can never be answered.</summary>
    private static bool IsBlockedOnUser(ChatViewModel pane)
    {
        if (!pane.InputLocked) return false;   // an interactive pane's prompt is the user's to answer, not a fault
        for (var i = pane.Items.Count - 1; i >= 0 && i >= pane.Items.Count - 12; i--)
            if (pane.Items[i] is PermItem { IsPending: true }) return true;
        return false;
    }

    // ---------------------------------------------------------------- the ladder

    /// <summary>Rung one: ask. Cheapest possible fix, and it is genuinely often the right one — an agent that ended a
    /// turn early usually just needs to be told to finish and report.</summary>
    private void Intervene(SupervisedAgent a, SupervisionHealth health)
    {
        var message = AgentSupervisionPolicy.InterventionMessage(a.Label, health, a.ElapsedSeconds, a.Task);
        // A stalled turn will not read anything until it ends, so free the session first. Its queued reply is kept.
        if (a.Pane.CanInterrupt && health is SupervisionHealth.Stale or SupervisionHealth.Looping) a.Pane.Interrupt();
        if (!a.Pane.Send(message))
        {
            // The session is gone, not slow. Skip straight to a restart rather than waiting out a grace period that
            // nothing will ever answer.
            SupervisionLog.Write(a.Label, "INTERVENTION-UNDELIVERABLE", "session is not accepting input");
            a.Interventions = SupervisionConfig.MaxInterventions;
            RestartAgent(a, health);
            return;
        }

        a.Stage = SupervisionStage.Intervened;
        a.Interventions++;
        a.InterventionAt = DateTime.Now;
        a.LastActivityAt = DateTime.Now;
        a.Pane.SupervisionAlert = true;
        a.Pane.Items.Add(new DividerItem
        {
            Label = $"⏱ supervisor stepped in — {Describe(health)} after {AgentSupervisionPolicy.Duration(a.ElapsedSeconds)}",
        });
        SupervisionLog.Write(a.Label, "INTERVENED",
            $"{Describe(health)} after {AgentSupervisionPolicy.Duration(a.ElapsedSeconds)} " +
            $"(intervention {a.Interventions}/{SupervisionConfig.MaxInterventions})");
        NotifyOrchestrator($"{a.Label} was {Describe(health)} and has been asked to summarise its progress and " +
                           "finish. No action needed from you yet — keep planning.");
        NoteBridgeActivity();
    }

    /// <summary>Rung two: cancel and re-run. The replacement is a fresh session that inherits the pane's identity and
    /// is handed the ORIGINAL task verbatim plus whatever the dead one had reported. Restarting cannot become a
    /// re-scoping — that is why the task text is copied, never regenerated.</summary>
    private void RestartAgent(SupervisedAgent a, SupervisionHealth health)
    {
        var old = a.Pane;
        var paneIndex = BridgePanes.IndexOf(old);
        // The orchestrator is never restarted from under the user: it holds the plan and the conversation.
        if (paneIndex < 0 || old.IsBridgeManager || old.IsDemonOrchestrator)
        {
            Resolve(a, AgentResolution.Failed, "cannot restart this session");
            TakeOverTask(a, Describe(health));
            return;
        }

        var number = a.Number > 0 ? a.Number : BridgeNumberOf(old);
        var progress = a.LastReport.Length > 0 ? a.LastReport : Tail(old.LastTurnReplyText(), 1500);
        var attempt = a.Restarts + 2;   // the original run was attempt 1

        SupervisionLog.Write(a.Label, "CANCELLED",
            $"{Describe(health)} after {AgentSupervisionPolicy.Duration(a.ElapsedSeconds)} — cancelling for restart");
        if (old.CanInterrupt) old.Interrupt();
        MarkOwned(old.SessionId);   // keep the dead thread reachable in history; it is evidence

        var replacement = new ChatViewModel(old.Cwd, accountId: old.AccountId, provider: old.Provider)
        {
            Title = old.Title,
            ExcludeFromMemory = old.ExcludeFromMemory,
        };
        replacement.IsDemonWorker = old.IsDemonWorker;
        // Both Demon roles have to survive a restart. The orchestrator's flag is what the demon wall features and
        // what keeps its pane typable; a replacement without it silently demotes the one session the user can reach.
        replacement.IsDemonOrchestrator = old.IsDemonOrchestrator;
        replacement.BridgeLabel = old.BridgeLabel;
        replacement.SetMode(old.Mode);
        replacement.Model = old.Model;
        replacement.Effort = old.Effort;
        replacement.FastMode = old.FastMode;   // per chat, so a restart must carry it rather than re-read a default
        replacement.AppendSystemPrompt = number > 0
            ? BridgePrompt(replacement, number, BridgeNumbers(), ManagerNumberIn(BridgePanes))
            : old.AppendSystemPrompt;
        replacement.Items.Add(new DividerItem
        {
            Label = $"⏱ restarted by the supervisor — attempt {attempt} on the same task ({Describe(health)})",
        });

        old.Close();
        _bridgeSeenStatus.Remove(old);
        _bridgeErrored.Remove(old);
        _supervised.Remove(old);
        BridgePanes[paneIndex] = replacement;
        Track(replacement);
        _bridgeSeenStatus[replacement] = replacement.Status;
        replacement.Start();

        // Carry the ledger across to the new session, preserving the budget already spent.
        a.Pane = replacement;
        a.Restarts++;
        a.Stage = SupervisionStage.Restarted;
        a.Interventions = 0;            // a fresh session gets a fresh chance to be asked nicely
        a.InterventionAt = null;
        a.RepeatCount = 0;
        a.LastFingerprint = "";
        a.Resolution = AgentResolution.Unresolved;
        a.AssignedAt = DateTime.Now;
        a.LastActivityAt = DateTime.Now;
        a.ActivitySignature = ActivitySignatureOf(replacement);
        _supervised[replacement] = a;

        var order = AgentSupervisionPolicy.RestartMessage(a.Label, a.Task, progress, attempt);
        if (!replacement.Send(order)) a.PendingDispatch = order;   // still booting; flushed on its first status change
        replacement.SupervisionAlert = true;

        SupervisionLog.Write(a.Label, "RESTARTED",
            $"attempt {attempt} on the same task definition (restart {a.Restarts}/{SupervisionConfig.MaxRestarts})");
        NotifyOrchestrator($"{a.Label} was stalled ({Describe(health)}), so the app cancelled it and restarted it on " +
                           "the SAME task with its prior context. Its lane is still owned — do not reassign it.");
        NoteBridgeActivity();
        RaiseBridgeUi();
    }

    /// <summary>Rung three: the orchestrator finishes the job. This is the backstop that makes "no task is left
    /// unresolved" true even when a lane simply cannot be delegated.</summary>
    private void TakeOverTask(SupervisedAgent a, string reason)
    {
        if (_supervisionOrchestrator is not { } orchestrator) return;
        a.Stage = SupervisionStage.TakenOver;
        a.Pane.SupervisionAlert = false;
        a.Pane.SupervisionStatus = "handed to the orchestrator";
        a.Pane.Items.Add(new DividerItem { Label = $"⏱ task handed to the orchestrator — {reason}" });

        orchestrator.Send(AgentSupervisionPolicy.TakeoverMessage(a.Label, a.Task, a.LastReport, reason));
        orchestrator.Items.Add(new DividerItem { Label = $"⏱ took over {a.Label}'s task — {reason}" });
        a.Resolution = AgentResolution.CompletedByOrchestrator;
        a.ResolutionReason = $"taken over by the orchestrator — {reason}";
        SupervisionLog.Write(a.Label, "TAKEN-OVER", reason);
        NoteBridgeActivity();
        MaybeReconcile();
    }

    /// <summary>An app-generated note to the orchestrator. Kept distinct from a work order so the orchestrator can tell
    /// "the app did something to your team" from "here is a result".</summary>
    private void NotifyOrchestrator(string body)
    {
        if (_supervisionOrchestrator is not { } orchestrator) return;
        if (orchestrator.Status is "error" or "closed") return;
        orchestrator.Send("⏱ [SUPERVISOR] " + body);
    }

    // ---------------------------------------------------------------- closing the team

    /// <summary>Once every delegated task has a terminal state, ask the orchestrator to reconcile the outputs and
    /// verify the objective for real. Fires at most once per objective — a finished team must not be nagged.</summary>
    private void MaybeReconcile()
    {
        if (_supervisionReconciled || _supervisionOrchestrator is null) return;
        if (_supervised.Count == 0) return;
        if (_supervised.Values.Any(a => a.HasAssignment)) return;

        _supervisionReconciled = true;
        var ledger = _supervised.Values
            .OrderBy(a => a.Number)
            .Select(a => $"{a.Label}: {Describe(a.Resolution)} — {a.ResolutionReason}")
            .ToList();
        SupervisionLog.Write(_supervisionOrchestrator.BridgeLabel, "RECONCILING",
            string.Join(" | ", ledger));
        _supervisionOrchestrator.Send(AgentSupervisionPolicy.ReconcileMessage(ledger, _supervisionObjective));
        _supervisionOrchestrator.Items.Add(new DividerItem
        {
            Label = $"⏱ all {ledger.Count} delegated task(s) resolved — reconciling and verifying the objective",
        });
        NoteBridgeActivity();
    }

    private static string Describe(AgentResolution resolution) => resolution switch
    {
        AgentResolution.Completed => "completed",
        AgentResolution.Cancelled => "cancelled",
        AgentResolution.Replaced => "replaced",
        AgentResolution.CompletedByOrchestrator => "completed by the orchestrator",
        AgentResolution.Failed => "failed",
        _ => "unresolved",
    };

    // ---------------------------------------------------------------- pane status text

    private void UpdatePaneSupervisionStatus(SupervisedAgent a)
    {
        var pane = a.Pane;
        if (a.Resolution != AgentResolution.Unresolved)
        {
            pane.SupervisionStatus = Describe(a.Resolution);
            pane.SupervisionAlert = a.Resolution is AgentResolution.Failed or AgentResolution.Cancelled;
            return;
        }

        var elapsed = AgentSupervisionPolicy.Duration(a.ElapsedSeconds);
        pane.SupervisionStatus = a.Stage switch
        {
            SupervisionStage.Intervened => $"nudged · {elapsed}",
            SupervisionStage.Restarted => $"restarted · {elapsed}",
            SupervisionStage.TakenOver => "handed to the orchestrator",
            _ => pane.IsWorking ? $"working · {elapsed}" : $"assigned · {elapsed}",
        };
        pane.SupervisionAlert = a.Stage is SupervisionStage.Intervened or SupervisionStage.Restarted;
    }

    private void RefreshDemonActivity()
    {
        if (!IsDemonMode) return;
        Raise(nameof(DemonSummary));
    }

    private static List<int> ParseDependencies(string? attributes) =>
        DispatchBlockParser.ParseDependencies(attributes);
}
