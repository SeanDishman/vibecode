using VibeCode.AgentStatus.Mcp.Bridge;

namespace VibeCode.Services;

/// <summary>
/// The Advanced Bridge "Fact check orchestrator" option. A model can end a run with the user's request still
/// unfinished, and once every agent has stopped nothing is left to notice. With this on, each orchestrator is asked
/// whether it really did everything the user asked, and only its explicit <see cref="BridgeMcpTools.MarkTaskDoneToolName"/>
/// answer ends the check. An unanswered check is asked again, at most <see cref="MaxChecks"/> times per request.
/// </summary>
public static class BridgeFactCheckPolicy
{
    /// <summary>Recognised as app traffic (never the user's own prompt) and shown as a "Fact check" card.</summary>
    public const string Header = "👑 [MANAGER FACT CHECK] ";
    public const string CardKind = "Fact check";
    public const int MaxChecks = 3;
    private const int RequestLimit = 3000;
    private const string Tool = BridgeMcpTools.MarkTaskDoneToolName;

    /// <summary>The question for one orchestrator. <paramref name="scope"/> is its share of a multi-group request.</summary>
    public static string Prompt(string objective, string scope, int check)
    {
        objective = objective.Trim();
        if (objective.Length > RequestLimit)
            objective = objective[..RequestLimit] + "… (shortened; the full request is earlier in this chat)";
        var request = objective.Length == 0
            ? "The user's request is earlier in this chat."
            : "The user's request:\n───\n" + objective + "\n───";
        var part = scope.Trim() is { Length: > 0 } assigned ? "\nYour assigned part of it: " + assigned : "";
        var again = check > 1 ? $" You were asked before and have not confirmed with {Tool} yet." : "";
        return Header + "Every agent in this Advanced Bridge has stopped: you and your workers are idle, with nothing queued or retrying." +
               again + " Before this task can end, check your work against what the user asked.\n\n" +
               request + part + "\nAlso count anything the user asked later in this chat.\n\n" +
               "Did you do everything the user asked, completely and to the best of your ability? Re-read the request and check the " +
               "actual results (files, behavior, checks), not only your workers' reports.\n" +
               "- Something is missing, incomplete or unverified: finish it now. Dispatch the remaining work to your own workers " +
               "with bridge_dispatch_task (for failed or interrupted steps, inspect existing work, then use bridge_retry_task) and " +
               "finish your turn; their results arrive automatically.\n" +
               $"- Everything is done and verified: call {Tool} with status=done and a summary of what was delivered and how you " +
               "checked it, then report to the user and stop.\n" +
               $"- You cannot finish without the user (a decision, credentials or access): call {Tool} with status=blocked and " +
               "say exactly what you need.\n" +
               $"Never mark it done just to end this check. Fact check {check} of {MaxChecks}.";
    }

    public static string UnconfirmedNotice(string orchestrator) =>
        $"Fact check stopped: {orchestrator} was asked {MaxChecks} times whether it finished everything you asked and never " +
        $"confirmed it with {Tool}. Check its work, or send it a message to continue.";

    public static string BlockedNotice(string orchestrator, string summary) =>
        $"Fact check: {orchestrator} needs you before it can finish. {summary}";
}
