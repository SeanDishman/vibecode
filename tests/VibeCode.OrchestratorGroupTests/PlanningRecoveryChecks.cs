using System.Diagnostics;
using System.IO;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    static void VerifyPlanningRecovery()
    {
        Environment.SetEnvironmentVariable("REVIEW_PLANNING_QUOTA", "1");
        AppSettings.Current.ContinueAfterLimitResets = true;
        var host = Chat("planning-recovery", "codex");
        var config = new BridgeAgentConfiguration("codex", "gpt-6-astra", "medium");
        BridgeOrchestratorGroup[] groups = [new("a", 1, "Build account settings"), new("b", 1, "Build usage charts")];
        var waiting = new List<bool>();
        try
        {
            var before = PlanningTraces();
            var division = BridgeCentralOrchestratorService.DivideAsync(host, config, groups, CancellationToken.None, waiting.Add);
            Pump(() => division.IsCompleted);
            Check("central planner keeps its session and resumes after fresh allowance", division.GetAwaiter().GetResult().Count == 2
                && waiting.SequenceEqual(new[] { true, false }));
            VerifyPlanningTrace(before, resumed: true, "central planner", "Continue the central assignment after the usage limit");
            waiting.Clear();
            before = PlanningTraces();
            var suggestion = BridgeTeamSuggestionService.SuggestAsync(host, "Implement account settings and usage charts", 4,
                orchestratorCount: 2, configuration: config, waitingForLimit: waiting.Add);
            Pump(() => suggestion.IsCompleted);
            Check("automatic team sizing also resumes after quota reset", suggestion.GetAwaiter().GetResult().WorkerCount == 2
                && waiting.SequenceEqual(new[] { true, false }));
            VerifyPlanningTrace(before, resumed: true, "team sizing", "Continue after the usage limit");
            using var cancellation = new CancellationTokenSource();
            before = PlanningTraces();
            var cancelled = BridgeCentralOrchestratorService.DivideAsync(host, config, groups, cancellation.Token,
                isWaiting => { if (isWaiting) cancellation.Cancel(); });
            Pump(() => cancelled.IsCompleted);
            Check("closing central setup cancels quota wait", cancelled.IsCanceled);
            VerifyPlanningTrace(before, resumed: false, "cancelled planner", null);
            before = PlanningTraces();
            var disabled = BridgeCentralOrchestratorService.DivideAsync(host, config, groups, CancellationToken.None,
                isWaiting => { if (isWaiting) AppSettings.Current.ContinueAfterLimitResets = false; });
            Pump(() => disabled.IsCompleted);
            Check("turning off global recovery ends central quota wait", disabled.IsFaulted && disabled.Exception!.ToString().Contains("usage limit"));
            VerifyPlanningTrace(before, resumed: false, "disabled planner", null);
        }
        finally
        {
            Environment.SetEnvironmentVariable("REVIEW_PLANNING_QUOTA", null);
            AppSettings.Current.ContinueAfterLimitResets = false;
        }
    }

    static HashSet<string> PlanningTraces() => Directory.GetFiles(Root, "trace-*.jsonl").ToHashSet();

    static void VerifyPlanningTrace(HashSet<string> before, bool resumed, string label, string? continuation)
    {
        var paths = PlanningTraces().Except(before).ToArray();
        Check(label + " launches exactly one provider process", paths.Length == 1);
        if (paths.Length != 1) return;
        var rows = ReadTraceRows(paths[0]);
        var turns = rows.Select((row, index) => (Row: row, Index: index)).Where(r => r.Row["method"]?.ToString() == "turn/start").ToArray();
        Check(label + " sends exactly the expected number of prompts", turns.Length == (resumed ? 2 : 1));
        Check(label + " retains the original thread and settings", rows.Count(r => r["method"]?.ToString() == "thread/start") == 1
            && turns.Select(t => t.Row["params"]?["threadId"]?.ToString()).Distinct().Count() == 1
            && turns.All(t => t.Row["params"]?["model"]?.ToString() == "gpt-6-astra" && t.Row["params"]?["effort"]?.ToString() == "medium"));
        if (resumed && turns.Length == 2)
        {
            Check(label + " reads fresh quota between the failed prompt and continuation", rows.Skip(turns[0].Index + 1)
                .Take(turns[1].Index - turns[0].Index - 1).Any(r => r["method"]?.ToString() == "account/rateLimits/read"));
            Check(label + " nudges once without resubmitting the original task", turns[1].Row.ToJsonString().Contains(continuation!)
                && CentralInput(turns[1].Row) is null && !turns[1].Row.ToJsonString().Contains("Suggest between"));
        }
        var pid = int.Parse(Path.GetFileNameWithoutExtension(paths[0])["trace-".Length..]);
        bool Exited() { try { using var process = Process.GetProcessById(pid); return process.HasExited; } catch (ArgumentException) { return true; } }
        Pump(Exited);
        Check(label + " disposes the provider process after finishing", Exited());
    }
}
