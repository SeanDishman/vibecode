using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    static void VerifyCentralErrorRetry()
    {
        Environment.SetEnvironmentVariable("REVIEW_PLANNING_TRANSIENT", "1");
        AppSettings.Current.ContinueAfterLimitResets = false;
        try
        {
            var host = Chat("central-transient-recovery", "codex");
            var config = new BridgeAgentConfiguration("codex", "gpt-6-astra", "medium");
            BridgeOrchestratorGroup[] groups = [new("a", 1, "Implement account settings"), new("b", 1, "Verify account settings")];
            var stages = new List<string>();
            var waiting = new Stopwatch();
            var before = PlanningTraces();
            var task = BridgeCentralOrchestratorService.DivideAsync(host, config, groups, CancellationToken.None,
                stageChanged: stage => { stages.Add(stage); if (stage.StartsWith("Temporary provider error")) waiting.Start(); });
            var elapsed = Stopwatch.StartNew();
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            timer.Tick += (_, _) => { if (task.IsCompleted || elapsed.Elapsed > TimeSpan.FromSeconds(85)) frame.Continue = false; };
            timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
            Check("central provider retries after a real minute even with quota recovery off", task.IsCompletedSuccessfully
                && waiting.Elapsed >= TimeSpan.FromMinutes(1) && task.Result.Count == 2);
            Check("central activity explains the delay and resumption", stages.Any(s => s.Contains("Continuing in 1 minute"))
                && stages.Any(s => s.Contains("after the provider error")));
            VerifyTransientPlannerTrace(before, resumed: true);
            Console.WriteLine($"Central planner retry delay: {waiting.Elapsed.TotalSeconds:F2} seconds.");

            before = PlanningTraces();
            using var cancellation = new CancellationTokenSource();
            var cancelled = BridgeCentralOrchestratorService.DivideAsync(host, config, groups, cancellation.Token,
                stageChanged: stage => { if (stage.StartsWith("Temporary provider error")) cancellation.Cancel(); });
            Pump(() => cancelled.IsCompleted);
            Check("closing the team cancels the central provider retry", cancelled.IsCanceled);
            VerifyTransientPlannerTrace(before, resumed: false);
        }
        finally { Environment.SetEnvironmentVariable("REVIEW_PLANNING_TRANSIENT", null); }
    }

    static void VerifyTransientPlannerTrace(HashSet<string> before, bool resumed)
    {
        var traces = PlanningTraces().Except(before).ToArray();
        Check("provider retry owns just one central process " + resumed, traces.Length == 1);
        var rows = ReadTraceRows(traces.Single());
        var turns = rows.Where(r => r["method"]?.ToString() == "turn/start").ToArray();
        Check("central sends only the expected turns " + resumed, turns.Length == (resumed ? 2 : 1));
        Check("central retry retains its existing thread, model and effort " + resumed,
            rows.Count(r => r["method"]?.ToString() == "thread/start") == 1
            && turns.All(r => r["params"]?["model"]?.ToString() == "gpt-6-astra" && r["params"]?["effort"]?.ToString() == "medium"));
        if (resumed) Check("central continuation reuses the assignment context", CentralInput(turns[1]) is null
            && turns[1].ToJsonString().Contains("Continue the central assignment after the temporary provider error"));
        var pid = int.Parse(Path.GetFileNameWithoutExtension(traces[0])["trace-".Length..]);
        bool Closed() { try { using var process = Process.GetProcessById(pid); return process.HasExited; } catch (ArgumentException) { return true; } }
        Pump(Closed);
        Check("central process closes after finish or cancellation " + resumed, Closed());
    }
}
