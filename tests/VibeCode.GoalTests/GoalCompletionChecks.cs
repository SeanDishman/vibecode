using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    private static void RunGoalCompletionChecks()
    {
        var firstTurn = NewFake("complete-first-turn");
        Send(firstTurn, "/goal Finish the task and verify it", 1);
        var goal = firstTurn.Goal!;
        var reply = End(firstTurn, "Finished and verified.\n" + GoalPolicy.CompleteMarker(goal));
        Check("completion on the work turn settles the goal immediately", firstTurn.Goal is { Completed: true, Paused: false });
        Check("completion preserves the report and hides the protocol", reply.RenderText == "Finished and verified.");
        Check("completion is confirmed in the transcript", firstTurn.Items.OfType<BannerItem>().Any(b => b.Text == "Goal completed: " + goal.Text));
        Quiet(firstTurn, 1, "a finished work turn does not send another goal request");
        Send(firstTurn, "Explain the result", 2);
        Check("ordinary follow-ups do not reactivate a completed goal", !Session(firstTurn).Sent[1].Contains("[VIBECODE ACTIVE GOAL]"));
        End(firstTurn, "Here is the explanation.");
        Quiet(firstTurn, 2, "a follow-up to a completed goal stays quiet");

        Send(firstTurn, "/goal A different task", 3);
        Check("an explicit replacement starts a fresh goal", firstTurn.Goal is { Completed: false, Paused: false } && firstTurn.Goal.Id != goal.Id);
        Check("every work turn carries its own completion protocol", Session(firstTurn).Sent[2].Contains(GoalPolicy.CompleteMarker(firstTurn.Goal!))
            && Session(firstTurn).Sent[2].Contains(GoalPolicy.WaitingMarker(firstTurn.Goal!)));
        End(firstTurn, "Old completion.\n" + GoalPolicy.CompleteMarker(goal));
        WaitSent(firstTurn, 4);
        Check("completion from a replaced goal cannot finish its replacement", !firstTurn.Goal!.Completed);
        End(firstTurn, "New task verified.\n" + GoalPolicy.CompleteMarker(firstTurn.Goal!));

        var waiting = NewFake("wait-first-turn");
        Send(waiting, "/goal Use the user's answer", 1);
        End(waiting, "Which color?\n" + GoalPolicy.WaitingMarker(waiting.Goal!));
        Check("a work turn can immediately wait for the user", waiting.Goal is { Completed: false, Paused: true });
        Quiet(waiting, 1, "a waiting work turn does not repeat the question");
        Send(waiting, "Turquoise", 2);
        End(waiting, "Color accepted.\n" + GoalPolicy.CompleteMarker(waiting.Goal!));
        Quiet(waiting, 2, "finishing after an answer does not trigger another confirmation");

        var formats = new Func<string, string>[]
        {
            marker => "`" + marker + "`",
            marker => "**" + marker + "**",
            marker => "__" + marker + "__",
            marker => "**`" + marker + "`**",
            marker => "```\n" + marker + "\n```",
            marker => "```text\r\n" + marker + "\r\n```\r\n",
        };
        for (var i = 0; i < formats.Length; i++)
        {
            var formatted = NewFake("formatted-completion-" + i);
            Send(formatted, "/goal Verify formatted completion " + i, 1);
            End(formatted, "Still working.");
            WaitSent(formatted, 2);
            var marker = GoalPolicy.CompleteMarker(formatted.Goal!);
            var text = "Verified.\n" + formats[i](marker);
            var report = End(formatted, text);
            Check("formatted goal-check completion is recognized: " + i, formatted.Goal!.Completed);
            Check("formatted metadata is hidden without losing the report: " + i, report.RenderText == "Verified." && report.Text == text);
            Quiet(formatted, 2, "formatted completion stops further requests: " + i);
        }

        var unfinished = NewFake("completion-boundaries");
        Send(unfinished, "/goal Finish all remaining work", 1);
        End(unfinished, "Example only:\n" + GoalPolicy.CompleteMarker(unfinished.Goal!) + "\nThere is still work left.");
        WaitSent(unfinished, 2);
        Check("a nonterminal marker does not claim completion", !unfinished.Goal!.Completed);
        End(unfinished, "The goal is not complete.");
        WaitSent(unfinished, 3);
        Check("ordinary goal wording does not accidentally claim completion", !unfinished.Goal!.Completed);
        unfinished.Interrupt();
        End(unfinished, "Finished.\n" + GoalPolicy.CompleteMarker(unfinished.Goal!));
        Check("Stop wins over completion metadata in an interrupted turn", unfinished.Goal is { Completed: false, Paused: true });
        Quiet(unfinished, 3, "an interrupted goal remains stopped");
    }
}
