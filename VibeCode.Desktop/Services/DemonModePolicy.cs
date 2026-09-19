namespace VibeCode.Services;

/// <summary>
/// Demon Mode: a preset Bridge of up to seventeen root CLI sessions where agent 1 is a locked ORCHESTRATOR and the
/// rest are read-only WORKERS. It is deliberately not a new session mechanism — it is the existing Bridge roster
/// plus three constraints: a size chosen once when the team starts, exactly one typable pane, and an orchestrator
/// whose job stops at planning and dispatching. Keeping those constraints here gives the view-model, the prompts
/// and the UI one definition.
/// </summary>
public static class DemonModePolicy
{
    /// <summary>The largest Demon team, orchestrator included, and the size a fresh install starts out choosing.
    /// Seventeen is the wall's own arithmetic, not a round number: DemonWallPanel lays a full team out as a 4x5 grid
    /// with the orchestrator holding the top-left 2x2 block, which leaves exactly sixteen worker cells — the one
    /// roster this size where nothing has to be stretched or left empty.</summary>
    public const int SessionCount = 17;

    /// <summary>Worker sessions in a full team, i.e. every session except the orchestrator.</summary>
    public const int WorkerCount = SessionCount - 1;

    /// <summary>
    /// The smallest team the mode will start.
    ///
    /// Four rather than two, because below it the mode stops being itself: an orchestrator that plans, splits and
    /// dispatches is pure overhead against one or two workers — the user would get a slower answer than from a
    /// single chat. Three workers is the point where parallel lanes start paying for the extra turn.
    /// </summary>
    public const int MinimumSessionCount = 4;

    /// <summary>The orchestrator is always agent 1: the roster is compact and 1-based, and the host pane is the one
    /// with a sidebar row, so anchoring the crown anywhere else would leave it unreachable after a navigate-away.</summary>
    public const int OrchestratorNumber = 1;

    /// <summary>The largest team this build can actually stand up. Normally <see cref="SessionCount"/>; it bows to
    /// the app-wide agent ceiling so the two limits can never disagree.</summary>
    public static int MaximumSessionCount => Math.Min(SessionCount, BridgeAgentPolicy.MaximumAgentLimit);

    /// <summary>Hold a requested team size inside the supported range. Also the guard on the stored preference, so a
    /// hand-edited settings.json cannot ask for a roster the wall has no arrangement for.</summary>
    public static int ClampSessionCount(int count) =>
        Math.Clamp(count, MinimumSessionCount, MaximumSessionCount);

    /// <summary>
    /// How many sessions to actually stand up, given what the caller asked for.
    ///
    /// The size is the user's choice now (Demon Mode setup asks for it, and the answer is remembered), so a Demon
    /// team no longer fills the roster to the ceiling — but it still ignores the per-Bridge agent slider, which is
    /// about ordinary bridges, and it can never exceed the app-wide maximum. <c>VIBECODE_DEMON_SESSIONS</c>
    /// overrides everything for the regression harness — every role, layout and supervision path is identical at
    /// four sessions as at seventeen, and a smoke run should not have to spawn seventeen real CLI processes to
    /// exercise them, so that hook is allowed below the user-facing floor.
    /// </summary>
    public static int RosterFor(int? requested)
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("VIBECODE_DEMON_SESSIONS"), out var v) && v >= 2)
            return Math.Min(v, MaximumSessionCount);
        return ClampSessionCount(requested ?? AppSettings.Current.DemonSessionCount);
    }

    /// <summary>The roster a team started without an explicit size gets: the remembered choice, or a full team.</summary>
    public static int Roster => RosterFor(null);

    public const string OrchestratorRole = "Orchestrator";
    public const string WorkerRole = "Worker";

    /// <summary>The system-prompt appendix for the orchestrator. It differs from the ordinary Bridge manager brief in
    /// one decisive way: unless review is explicitly enabled, the orchestrator does NOT inspect, rewrite or validate
    /// worker output. It plans, dispatches, and keeps the team alive.</summary>
    public static string OrchestratorBrief(int workerCount, bool reviewsWork)
    {
        var review = reviewsWork
            ? "- Review IS enabled for this run: when a worker reports, check its work and correct the plan before " +
              "dispatching the next lane."
            : "- You do NOT review, rewrite, validate or second-guess worker output. When a worker reports, record " +
              "that its lane is done and move on. Judging the work is not your job in this mode; planning and " +
              "dispatching is. Never re-do a worker's task yourself unless the app explicitly hands it to you with a " +
              "\"⏱ [SUPERVISOR]\" takeover message.";
        return "\n[DEMON MODE — YOU ARE THE ORCHESTRATOR] This is a Demon Mode team: you plus " + workerCount +
               " worker sessions. The user can type ONLY into your pane; the workers are read-only to them, so " +
               "nothing reaches a worker except through you.\n" +
               "- Your job is PLANNING and DISPATCHING. Turn the user's request into a concrete execution plan, split " +
               "it into non-overlapping lanes (disjoint files/areas), and dispatch one lane per worker.\n" +
               review + "\n" +
               DispatchHowTo(workerCount) +
               "- If a lane genuinely cannot start until another finishes, say so on the header line: " +
               "`@@DISPATCH agent=7 depends=3,4`. That is recorded against the task so a stalled dependency is " +
               "visible rather than guessed at. Use it sparingly — parallel lanes are the point of this mode.\n" +
               "- Keep every one of the " + workerCount + " workers busy. Idle capacity is the failure mode this mode " +
               "exists to avoid. Dispatch to all of them in your FIRST reply; if the request genuinely has fewer " +
               "lanes, give the remainder verification, test, documentation or research lanes rather than nothing.\n" +
               "- Maintain the current assignments under a \"## Manager plan\" section in `.vibecode-bridge.md`. It is " +
               "your memory if anything restarts.\n" +
               "- The app messages you \"👑 [MANAGER UPDATE]\" when a worker finishes, errors, joins or leaves, and " +
               "\"⏱ [SUPERVISOR]\" when it has intervened on a stuck worker. React to both: update the plan and " +
               "dispatch the freed worker its next unclaimed lane.\n" +
               "- A stuck worker is handled BY THE APP first (it nudges, then restarts it on the same task). Only when " +
               "it hands you a takeover message is the task yours to do directly.\n" +
               "- When every lane is done you get a reconciliation prompt. Verify the objective for real, then tell " +
               "the user it is complete and STOP dispatching. Do not invent filler work.";
    }

    /// <summary>
    /// How to dispatch, stated so it cannot be mistaken for anything else.
    ///
    /// This is the one instruction the mode cannot afford to lose, and it is the one that failed in practice: an
    /// orchestrator that has a SendMessage/Task/Agent tool in its harness sees fifteen panes titled "Claude 2…16",
    /// concludes they are teammates it can address by name, and spends its whole first turn calling that tool. Nothing
    /// is delivered, no <c>@@DISPATCH</c> text is ever written, so the app's malformed-dispatch nudge never fires
    /// either — the team just sits idle looking busy. So say all three things explicitly: dispatching is WRITING, no
    /// tool can reach a worker, and the target is a number rather than a pane title.
    /// </summary>
    private static string DispatchHowTo(int workerCount) =>
        "- HOW TO DISPATCH — this is the one thing that goes wrong, so read it twice. You dispatch by WRITING TEXT in " +
        "your reply. There is NO dispatch tool, and no tool of any kind can reach a worker: they are separate CLI " +
        "sessions, not subagents of yours. Never try to reach one with SendMessage, Task, Agent, or any other " +
        "\"message a teammate\" tool — those address subagents you spawned yourself, the workers never see them, and " +
        "the lane silently never starts. The app scans your finished reply for blocks formatted EXACTLY like this and " +
        "delivers each one into that worker's session:\n" +
        "@@DISPATCH agent=<worker number, or all>\n" +
        "<that worker's complete, self-contained prompt: goal, files/areas, constraints, what NOT to touch, and what " +
        "it must report back>\n" +
        "@@END\n" +
        "The header must start its own line, and `agent=` takes the worker's NUMBER, never its pane title — the pane " +
        "labelled \"Claude 7\" is worker 7, so it is `agent=7`. A filled-in one looks like this:\n" +
        "@@DISPATCH agent=2\n" +
        "Add input validation to src/api/users.ts: reject an empty or over-64-character name with a 400 and a JSON " +
        "{error} body. Do not touch src/api/auth.ts or any test fixture. Run `npm test -- users` and report whether " +
        "it passed.\n" +
        "@@END\n" +
        "Writing " + workerCount + " lanes means writing " + workerCount + " of those blocks in one reply. A sentence " +
        "such as \"dispatching all " + workerCount + " lanes\" delivers nothing on its own — only the blocks do. Text " +
        "outside them is yours to the user; workers never see it, so a worker's prompt must stand alone: it has none " +
        "of your context.\n";

    /// <summary>The system-prompt appendix for a worker. Workers are ordinary sessions with two differences: their
    /// only inbound channel is the orchestrator, and their reply tail is contractually a status report.</summary>
    public static string WorkerBrief(int index, int workerCount)
    {
        return "\n[DEMON MODE — YOU ARE WORKER " + index + " OF " + (workerCount + 1) + "] This is a Demon Mode team. " +
               "Agent #" + OrchestratorNumber + " is the ORCHESTRATOR and the only session the user can type into. " +
               "You are read-only to the user: every instruction you get arrives from the orchestrator.\n" +
               "- Messages beginning \"👑 [FROM MANAGER\" are your work orders. Do them inside the lane they define, " +
               "honouring any \"don't touch\" constraints. Stay in your lane — the orchestrator owns lane assignment, " +
               "so never pick up unclaimed work on your own.\n" +
               "- ALWAYS end a finished assignment with a short factual report — what you did, what you actually " +
               "verified (ran it / built it / tested it), anything blocking — followed by the literal words \"" +
               AgentSupervisionPolicy.CompletionMarker + "\". That marker is how the app knows the task is done; " +
               "without it your lane stays recorded as unfinished and you will be nudged, then restarted. If you " +
               "cannot do the work, reply \"" + AgentSupervisionPolicy.FailureMarker + "\" plus the reason instead. " +
               "Never answer a work order with just an acknowledgement — \"ready\" and \"standing by\" are not results.\n" +
               "- The tail of your reply is relayed to the orchestrator automatically and is the only thing it sees.\n" +
               "- Messages beginning \"⏱ [SUPERVISOR\" come from the app because your lane looks stalled. Answer them " +
               "immediately and literally: progress first, then finish and return your result, or say \"CANNOT " +
               "COMPLETE\" plus the reason. Never ignore one — being unresponsive gets your session cancelled and " +
               "restarted from scratch.\n" +
               "- Never wait on another worker. If your lane genuinely depends on one, say so in your report and " +
               "return; the orchestrator sequences the work, you do not.";
    }

    /// <summary>The orchestrator's opening turn, sent the moment the team is up so it starts planning without the
    /// user having to prompt it twice. It repeats the dispatch grammar even though the system prompt carries it: the
    /// dispatch happens one turn later, under a wall of freshly-read project context, and a format seen once at the
    /// top of a long system prompt loses to a plausible-looking tool sitting right there in the harness.</summary>
    public static string OrchestratorKickoff(int workerCount, string projectPath) =>
        $"😈 [DEMON MODE] Your team is up: you are the orchestrator, with {workerCount} worker sessions standing by in " +
        $"`{projectPath}`. They are idle and cannot be reached by the user — only by you.\n\n" +
        "Do this now, before anything else: read `.vibecode-bridge.md` and skim the project so you can plan against " +
        "what is actually there. Then WAIT for the user's task. When it arrives, write the execution plan, record it " +
        "under \"## Manager plan\", and dispatch a lane to every worker in the same reply. Do not dispatch filler work " +
        "before the user has given you a task.\n\n" +
        $"When you do dispatch, it is TEXT you write — {workerCount} blocks in one reply, one per worker:\n" +
        "@@DISPATCH agent=<worker number>\n<that worker's complete, self-contained prompt>\n@@END\n" +
        "No tool can reach a worker (they are sibling CLI sessions, not your subagents), so do not try to message " +
        "them with SendMessage, Task or Agent — a lane dispatched that way is never delivered and never runs.";

    /// <summary>
    /// The one-shot note a freshly spawned worker carries into its first message.
    ///
    /// This must be PURE CONTEXT and must not instruct the agent to do anything. A worker's first message IS its first
    /// work order — the prelude is prepended to it — so any instruction here competes with the order it arrives glued
    /// to. An earlier version read "STAND BY: do nothing, reply that you are ready"; workers dutifully replied "Ready,
    /// standing by" and never touched the dispatched task. Say who they are, then get out of the way.
    /// </summary>
    public static string WorkerStandby(int index, int workerCount) =>
        $"[DEMON MODE] Context only — you are worker #{index} of a {workerCount + 1}-session team, and agent " +
        $"#{OrchestratorNumber} is the orchestrator (the only session the user can type into). Anything else in this " +
        "message is your actual work order: do it, and report the way your system prompt requires.";

    /// <summary>The reporting contract every work order closes with. Kept here so the dispatch wire text, the worker
    /// brief and the supervisor's completion check all quote exactly the same two markers.</summary>
    public static string ReportingContract =>
        $"When the work is done, end your reply with a short factual report (what you did, what you actually " +
        $"verified, anything blocking) followed by \"{AgentSupervisionPolicy.CompletionMarker}\". If you cannot do it, " +
        $"reply \"{AgentSupervisionPolicy.FailureMarker}\" plus the reason. An acknowledgement such as \"ready\" or " +
        "\"standing by\" is NOT a result and will leave the task recorded as unfinished.";
}
