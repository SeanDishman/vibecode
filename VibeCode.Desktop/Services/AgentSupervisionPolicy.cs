using System.IO;
using System.Text;

namespace VibeCode.Services;

/// <summary>What a supervisor concluded about one delegated agent on this pass.</summary>
public enum SupervisionHealth
{
    /// <summary>Producing output, or legitimately between assignments.</summary>
    Healthy,
    /// <summary>Mid-turn but has emitted nothing for longer than the stale threshold.</summary>
    Stale,
    /// <summary>Holds an assignment yet is not running and never returned a result.</summary>
    Idle,
    /// <summary>Repeating the same output/tool cycle instead of converging.</summary>
    Looping,
    /// <summary>Its session reported an error, or the provider process died.</summary>
    Failed,
    /// <summary>Stopped on a question, plan review or permission card. In Demon Mode nobody can answer it — the
    /// worker's pane is read-only — so this is a deadlock, not patience.</summary>
    Blocked,
}

/// <summary>How far up the escalation ladder a delegated agent has already been pushed. Strictly monotonic:
/// an agent never walks back down, which is what makes the ladder terminate.</summary>
public enum SupervisionStage
{
    Watching,
    Intervened,
    Restarted,
    TakenOver,
}

/// <summary>The terminal state of one delegated task. Nothing is allowed to stay <see cref="Unresolved"/> once the
/// team closes — that is the whole point of supervising.</summary>
public enum AgentResolution
{
    Unresolved,
    /// <summary>The agent returned a result for its assignment.</summary>
    Completed,
    /// <summary>Stopped deliberately with a stated reason (user closed it, objective withdrawn).</summary>
    Cancelled,
    /// <summary>Cancelled and re-run as a fresh session on the same task definition.</summary>
    Replaced,
    /// <summary>The orchestrator finished the work itself after the retry budget ran out.</summary>
    CompletedByOrchestrator,
    /// <summary>Reported, in its own words, that it cannot complete the task.</summary>
    Failed,
}

/// <summary>The escalation step a supervisor should take right now.</summary>
public enum SupervisionAction
{
    None,
    /// <summary>Ask the agent to summarise, finish, return a result, or declare that it cannot.</summary>
    Intervene,
    /// <summary>Cancel and re-run the same task definition in a fresh session.</summary>
    Restart,
    /// <summary>Hand the task to the orchestrator to complete directly.</summary>
    TakeOver,
}

/// <summary>
/// Every timeout, threshold and retry limit the supervisor obeys, resolved once per scan so a settings change
/// applies to a running team. Environment overrides exist only so the regression harness can compress a
/// five-minute ladder into a few seconds; production reads <see cref="AppSettings"/>.
/// </summary>
public sealed class SupervisionSettings
{
    /// <summary>Mid-turn silence (no new transcript activity) that counts as stale.</summary>
    public int StaleSeconds { get; init; } = 300;
    /// <summary>Holding an assignment while not running, with no result returned.</summary>
    public int IdleSeconds { get; init; } = 90;
    /// <summary>How long an intervention has to produce a response before escalating past it.</summary>
    public int InterventionGraceSeconds { get; init; } = 120;
    /// <summary>Absolute ceiling on one assignment, however healthy it looks. 0 disables the cap.</summary>
    public int MaxTaskSeconds { get; init; } = 3600;
    /// <summary>Interventions per assignment before the supervisor stops asking and starts restarting.</summary>
    public int MaxInterventions { get; init; } = 1;
    /// <summary>Restarts per assignment before the orchestrator takes the task over itself.</summary>
    public int MaxRestarts { get; init; } = 1;
    /// <summary>Identical consecutive turn outputs that count as a loop.</summary>
    public int LoopRepeatThreshold { get; init; } = 3;
    /// <summary>How often the supervisor scans. Kept small enough that the shortest threshold is still observable.</summary>
    public int ScanSeconds { get; init; } = 5;

    public static SupervisionSettings Current => Resolve(AppSettings.Current);

    public static SupervisionSettings Resolve(AppSettings settings) => new()
    {
        StaleSeconds = Env("VIBECODE_SUPERVISION_STALE_SECONDS", Positive(settings.SupervisionStaleSeconds, 300)),
        IdleSeconds = Env("VIBECODE_SUPERVISION_IDLE_SECONDS", Positive(settings.SupervisionIdleSeconds, 90)),
        InterventionGraceSeconds = Env("VIBECODE_SUPERVISION_GRACE_SECONDS",
            Positive(settings.SupervisionInterventionGraceSeconds, 120)),
        MaxTaskSeconds = Env("VIBECODE_SUPERVISION_MAX_TASK_SECONDS",
            Math.Max(0, settings.SupervisionMaxTaskSeconds)),
        MaxInterventions = Env("VIBECODE_SUPERVISION_MAX_INTERVENTIONS",
            Math.Clamp(settings.SupervisionMaxInterventions, 0, 5)),
        MaxRestarts = Env("VIBECODE_SUPERVISION_MAX_RESTARTS", Math.Clamp(settings.SupervisionMaxRestarts, 0, 5)),
        LoopRepeatThreshold = Env("VIBECODE_SUPERVISION_LOOP_REPEATS",
            Math.Max(2, settings.SupervisionLoopRepeatThreshold)),
        ScanSeconds = Env("VIBECODE_SUPERVISION_SCAN_SECONDS", 5),
    };

    private static int Positive(int value, int fallback) => value > 0 ? value : fallback;

    private static int Env(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v >= 0 ? v : fallback;

    /// <summary>The scan cadence must be able to observe the tightest threshold, or a short test timeout would be
    /// crossed and re-crossed between two ticks and the ladder would appear to skip a rung.</summary>
    public TimeSpan ScanInterval
    {
        get
        {
            var tightest = new[] { StaleSeconds, IdleSeconds, InterventionGraceSeconds }.Where(v => v > 0).DefaultIfEmpty(60).Min();
            return TimeSpan.FromSeconds(Math.Clamp(Math.Min(ScanSeconds, Math.Max(1, tightest / 3.0)), 1, 60));
        }
    }
}

/// <summary>
/// The pure decision half of agent supervision: given what has been observed about one delegated agent, say whether
/// it is stuck and which rung of the ladder comes next. Deliberately free of view-models and processes so the rules
/// have exactly one definition and can be reasoned about (and tested) on their own.
/// </summary>
public static class AgentSupervisionPolicy
{
    /// <summary>Classify a delegated agent from observations only. <paramref name="hasAssignment"/> is false for an
    /// agent that legitimately has nothing to do — an idle worker awaiting dispatch is healthy, not stuck.</summary>
    public static SupervisionHealth Diagnose(
        bool hasAssignment,
        bool isWorking,
        bool sessionFailed,
        double secondsSinceActivity,
        double secondsSinceAssigned,
        int repeatCount,
        SupervisionSettings cfg,
        bool blockedOnUser = false)
    {
        if (sessionFailed) return SupervisionHealth.Failed;
        if (!hasAssignment) return SupervisionHealth.Healthy;
        // Blocked outranks the clocks: an agent sitting on an unanswerable permission card is not "still thinking",
        // and waiting out a five-minute stale timer before saying so helps nobody.
        if (blockedOnUser) return SupervisionHealth.Blocked;
        if (repeatCount >= cfg.LoopRepeatThreshold) return SupervisionHealth.Looping;
        // The absolute cap outranks the liveness checks: an agent can emit output forever and still never land.
        if (cfg.MaxTaskSeconds > 0 && secondsSinceAssigned >= cfg.MaxTaskSeconds) return SupervisionHealth.Stale;
        if (isWorking) return secondsSinceActivity >= cfg.StaleSeconds ? SupervisionHealth.Stale : SupervisionHealth.Healthy;
        // Not running, still holding the assignment: it ended a turn without returning anything usable.
        return secondsSinceActivity >= cfg.IdleSeconds ? SupervisionHealth.Idle : SupervisionHealth.Healthy;
    }

    /// <summary>The next rung for an agent already judged unhealthy. Budgets are per-assignment, so a restarted agent
    /// gets a fresh intervention allowance but the restart budget keeps counting — that is what bounds the ladder.</summary>
    /// <param name="restartIsPointless">Set when a fresh session would hit the identical wall — a blocked permission
    /// card being the case that matters. Spending the restart budget there just delays the takeover that was always
    /// going to be needed.</param>
    public static SupervisionAction Escalate(SupervisionStage stage, int interventions, int restarts,
        SupervisionSettings cfg, bool restartIsPointless = false)
    {
        if (stage == SupervisionStage.TakenOver) return SupervisionAction.None;   // terminal; nothing left to try
        if (interventions < cfg.MaxInterventions) return SupervisionAction.Intervene;
        if (!restartIsPointless && restarts < cfg.MaxRestarts) return SupervisionAction.Restart;
        return SupervisionAction.TakeOver;
    }

    /// <summary>True once an intervention has gone unanswered for its whole grace period. Until then the supervisor
    /// waits: an agent that is genuinely finishing up must not be restarted out from under itself.</summary>
    public static bool InterventionExpired(double secondsSinceIntervention, SupervisionSettings cfg) =>
        secondsSinceIntervention >= cfg.InterventionGraceSeconds;

    /// <summary>The intervention text. One definition, because every escalation path quotes it and the four options it
    /// offers ARE the contract: summarise, finish, return a result, or say plainly that you cannot.</summary>
    public static string InterventionMessage(string agentName, SupervisionHealth health, int elapsedSeconds, string task)
    {
        var symptom = health switch
        {
            SupervisionHealth.Stale => "you have produced no output for a while",
            SupervisionHealth.Idle => "your turn ended without returning a result for this assignment",
            SupervisionHealth.Looping => "you appear to be repeating the same step instead of converging",
            SupervisionHealth.Failed => "your session reported an error",
            SupervisionHealth.Blocked => "you are stopped waiting on a human answer that CANNOT reach you — your pane " +
                                         "is read-only, so nobody will ever approve that prompt",
            _ => "your assignment looks stalled",
        };
        return $"⏱ [SUPERVISOR → {agentName}] It has been {Duration(elapsedSeconds)} on this assignment and {symptom}. " +
               "Reply NOW, in this order:\n" +
               "1. PROGRESS — one short paragraph on what you have actually done and verified so far.\n" +
               "2. Then do ONE of:\n" +
               $"   a. finish the remaining work and return your final result, ending with \"{CompletionMarker}\", or\n" +
               $"   b. if it is already done, return the final result now, ending with \"{CompletionMarker}\", or\n" +
               $"   c. if you cannot complete it, say exactly \"{FailureMarker}\" followed by the reason and what you " +
               "would need.\n" +
               "Answering with anything else — including \"ready\", \"standing by\", or a question — leaves this task " +
               "unresolved and will get your session cancelled and restarted.\n" +
               "Do not start anything new and do not re-plan. This is the assignment:\n" +
               "───\n" + Excerpt(task, 1200) + "\n───";
    }

    /// <summary>The re-dispatch text for a restarted agent. It carries the ORIGINAL task verbatim — a restart must not
    /// quietly become a different job — plus whatever the dead session had managed to report.</summary>
    public static string RestartMessage(string agentName, string task, string priorProgress, int attempt)
    {
        var carried = string.IsNullOrWhiteSpace(priorProgress)
            ? "The previous session left no usable progress report, so assume nothing was finished and verify the " +
              "current state on disk before you change anything."
            : "This is what the previous session reported before it stopped — verify it against the actual files " +
              "rather than trusting it:\n───\n" + Excerpt(priorProgress, 1500) + "\n───";
        return $"⏱ [SUPERVISOR → {agentName}] Your previous session stalled and was cancelled. You are attempt " +
               $"{attempt} on the SAME assignment, unchanged:\n───\n" + Excerpt(task, 4000) + "\n───\n" + carried +
               $"\nFinish it and end with a short factual report followed by \"{CompletionMarker}\", or say " +
               $"\"{FailureMarker}\" with the reason. Do not reply with anything else — an acknowledgement is not a result.";
    }

    /// <summary>The takeover brief handed to the orchestrator once an agent has exhausted its retries.</summary>
    public static string TakeoverMessage(string agentName, string task, string priorProgress, string reason)
    {
        return $"⏱ [SUPERVISOR] {agentName} exhausted its retry budget ({reason}) and has been stopped. Its task is " +
               "now YOURS to complete directly — do not re-dispatch it to another worker, and do not drop it.\n" +
               "The task, unchanged:\n───\n" + Excerpt(task, 4000) + "\n───\n" +
               (string.IsNullOrWhiteSpace(priorProgress)
                   ? "No usable progress was reported; verify the current state on disk before changing anything."
                   : "Last reported progress (verify it, do not trust it):\n───\n" + Excerpt(priorProgress, 1500) + "\n───") +
               "\nDo the work in this session, then record it as done in your plan.";
    }

    /// <summary>The final reconciliation prompt, sent once every delegated task has reached a terminal state.</summary>
    public static string ReconcileMessage(IReadOnlyList<string> lines, string objective)
    {
        return "⏱ [SUPERVISOR] Every delegated task has reached a terminal state. Final ledger:\n" +
               string.Join("\n", lines.Select(l => "  " + l)) + "\n\n" +
               (string.IsNullOrWhiteSpace(objective) ? "" : "The original objective was:\n───\n" + Excerpt(objective, 2000) + "\n───\n") +
               "Now close this out: (1) reconcile the workers' outputs against each other and against the objective, " +
               "(2) run a real verification pass (build / tests / run it) rather than assuming, (3) complete anything " +
               "a worker could not, and (4) tell the user plainly whether the objective is COMPLETE or what is still " +
               "missing. Then stop dispatching.";
    }

    /// <summary>
    /// The two words that end an assignment. Requiring an explicit marker is the difference between "the agent said
    /// something" and "the agent finished": a worker that replies "Ready, standing by" produces a perfectly non-empty
    /// turn, and treating that as a delivered result silently marks undone work as done — the exact failure this whole
    /// mechanism exists to prevent. An agent that finishes but forgets the marker simply gets one nudge asking for it.
    /// </summary>
    public const string CompletionMarker = "TASK COMPLETE";
    public const string FailureMarker = "CANNOT COMPLETE";

    /// <summary>An agent's own admission that it cannot finish. Recognised so the ladder stops instead of restarting a
    /// session that has already given a clear, final answer.</summary>
    public static bool DeclaresCannotComplete(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return false;
        return reply.Contains(FailureMarker, StringComparison.OrdinalIgnoreCase)
               || reply.Contains("cannot complete this task", StringComparison.OrdinalIgnoreCase)
               || reply.Contains("unable to complete this task", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An agent's own claim that the assignment is finished. Checked AFTER
    /// <see cref="DeclaresCannotComplete"/>, so a reply carrying both is treated as the refusal it is.</summary>
    public static bool DeclaresComplete(string? reply) =>
        !string.IsNullOrWhiteSpace(reply) && reply.Contains(CompletionMarker, StringComparison.OrdinalIgnoreCase);

    /// <summary>A stable, cheap signature of a turn's output, used to notice an agent emitting the same thing over and
    /// over. Whitespace and digits are normalised away so "attempt 3 of 5" style counters don't hide a real loop.</summary>
    public static string Fingerprint(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var sb = new StringBuilder(Math.Min(text.Length, 512));
        foreach (var ch in text)
        {
            if (sb.Length >= 512) break;
            if (char.IsWhiteSpace(ch)) { if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' '); continue; }
            if (char.IsDigit(ch)) continue;
            sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString().Trim();
    }

    public static string Duration(int seconds) => seconds >= 3600
        ? $"{seconds / 3600}h {seconds % 3600 / 60}m"
        : seconds >= 60 ? $"{seconds / 60}m {seconds % 60}s" : $"{seconds}s";

    public static string Excerpt(string? s, int max)
    {
        s = (s ?? "").Trim();
        return s.Length <= max ? s : s[..max] + "\n…(truncated)";
    }
}

/// <summary>
/// Append-only record of every intervention, cancellation, restart, takeover and final decision. Supervision that
/// isn't legible is indistinguishable from an app that silently killed the user's agents, so each entry goes to disk
/// AND is raised for the UI. Never throws — a logging failure must not take a running team down.
/// </summary>
public static class SupervisionLog
{
    private const long MaxLogBytes = 512 * 1024;
    private static readonly object Gate = new();

    public static string LogPath => Path.Combine(AppSettings.Dir, "supervision-log.txt");

    /// <summary>Raised on every entry (agent label, event, detail). Subscribers must not throw.</summary>
    public static event Action<string, string, string>? Logged;

    public static void Write(string agent, string @event, string detail = "")
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  [{@event}]  {agent}" +
                   (string.IsNullOrWhiteSpace(detail) ? "" : "  — " + Collapse(detail));
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppSettings.Dir);
                try
                {
                    if (new FileInfo(LogPath) is { Exists: true, Length: > MaxLogBytes })
                        File.Move(LogPath, LogPath + ".1", overwrite: true);
                }
                catch { /* rotation is a nicety; never lose the entry over it */ }
                File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch { /* diagnostics must never take the app down */ }

        foreach (var handler in Logged?.GetInvocationList().Cast<Action<string, string, string>>()
                                ?? Array.Empty<Action<string, string, string>>())
            try { handler(agent, @event, detail); } catch { /* one bad subscriber must not silence the rest */ }
    }

    /// <summary>One-line form: the log is meant to be skimmed, and a pasted stack trace would bury the next entry.</summary>
    private static string Collapse(string s)
    {
        var flat = s.Replace("\r", " ").Replace("\n", " ").Trim();
        while (flat.Contains("  ", StringComparison.Ordinal)) flat = flat.Replace("  ", " ");
        return flat.Length <= 400 ? flat : flat[..400] + "…";
    }
}
