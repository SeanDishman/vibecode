using System.Diagnostics;
using VibeCode.Services;

namespace VibeCode;

public partial class MainWindow
{
    /// <summary>Time a startup step that must keep its exceptions, unlike <see cref="RunStartupStep"/>, which contains
    /// them: noted in the crash log like the other steps and drawn on the startup timeline.</summary>
    private static void TimeStartupStep(string name, Action step)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            using (StartupTimeline.Phase(name)) step();
        }
        finally { CrashLog.Note("StartupPhase", $"{name}: {timer.ElapsedMilliseconds} ms"); }
    }

    /// <summary>The same for an awaited step; the time is until the awaited work finishes, not until it first yields.</summary>
    private static async Task TimeStartupStepAsync(string name, Func<Task> step)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            using (StartupTimeline.Phase(name)) await step();
        }
        finally { CrashLog.Note("StartupPhase", $"{name}: {timer.ElapsedMilliseconds} ms"); }
    }
}
