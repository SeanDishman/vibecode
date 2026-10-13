using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using VibeCode.Services;
using VibeCode.UI;

namespace VibeCode;

/// <summary>
/// Surviving the desktop compositor going away.
///
/// 2026-10-12 16:57: dwm.exe ran out of memory and died (exit 0xc00001ad) and Windows restarted it. VibeCode first saw
/// WindowChrome fail to extend its frame while composition was down (DWM_E_COMPOSITIONDISABLED - harmless, but it got
/// an error box), then its render thread failed for good (UCEERR_RENDERTHREADFAILURE). A WPF process whose render
/// thread has failed never draws again, so every window was frozen. Once the frozen window closed, WPF's own
/// dispatcher shutdown threw the same failure from MediaContext.Dispose, outside any handler, so the run ended in a
/// Windows Error Reporting crash - and that crashing process still held the single-instance lock, so the relaunch
/// three seconds later quietly exited as well.
///
/// Now a compositor reset is only logged. A dead render thread saves everything, hands the data directory over,
/// relaunches (chats and bridges return through the normal restore) and ends this process before WPF's shutdown can
/// crash it. Ending it is safe for the agents: every provider child runs in a KILL_ON_JOB_CLOSE job (see ProcessJob),
/// and the restore resumes their sessions. A relaunch that fails the same way within a few minutes comes back with
/// software rendering, and a third quick failure stops retrying.
/// </summary>
public partial class App
{
    private const int DwmCompositionDisabled = unchecked((int)0x80263001);
    private const int RenderThreadFailure = unchecked((int)0x88980406);
    private const string RenderRecoveryArgument = "--render-recovery=";
    internal const int SoftwareRenderingLevel = 2;
    /// <summary>A failure this soon after a recovery launch is the same problem coming back, not a new one.</summary>
    internal static readonly TimeSpan RenderRecoveryWindow = TimeSpan.FromMinutes(5);

    private static int _renderRecoveryLevel;
    private static bool _renderThreadFailed;

    /// <summary>dwm.exe is restarting. WindowChrome extends the frame again on the next composition change.</summary>
    internal static bool IsCompositorReset(Exception exception) =>
        Chain(exception).Any(e => e is COMException { HResult: DwmCompositionDisabled });

    /// <summary>This process's WPF render thread is gone; nothing will draw again until the process restarts.</summary>
    internal static bool IsRenderThreadFailure(Exception exception) =>
        Chain(exception).Any(e => e is COMException { HResult: RenderThreadFailure } ||
            e is OutOfMemoryException && e.StackTrace?.Contains("MediaContext.NotifyPartitionIsZombie", StringComparison.Ordinal) == true);

    private static IEnumerable<Exception> Chain(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException) yield return current;
    }

    /// <summary>The recovery level to relaunch at after a render failure, or 0 to stop retrying. A failure long after
    /// the last recovery starts over at 1; quick repeats escalate to software rendering, then give up.</summary>
    internal static int NextRenderRecoveryLevel(int current, TimeSpan uptime)
    {
        var next = uptime < RenderRecoveryWindow ? current + 1 : 1;
        return next > SoftwareRenderingLevel ? 0 : next;
    }

    /// <summary>Read the level a recovery launch was given. Must run before the first window exists, because the
    /// render mode is fixed per window when its HwndTarget is created.</summary>
    private static void ApplyRenderRecovery(string[] args)
    {
        var value = args.FirstOrDefault(a => a.StartsWith(RenderRecoveryArgument, StringComparison.Ordinal));
        if (value is null || !int.TryParse(value.AsSpan(RenderRecoveryArgument.Length), out var level)) return;
        _renderRecoveryLevel = Math.Clamp(level, 1, SoftwareRenderingLevel);
        var software = _renderRecoveryLevel >= SoftwareRenderingLevel;
        if (software) RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        CrashLog.Note("RenderRecovery",
            $"Relaunched after WPF's render thread failed (recovery level {_renderRecoveryLevel})." + Environment.NewLine +
            (software ? "It failed again right after the last recovery, so this run draws in software. The next normal start uses the GPU again."
                : "This run uses the GPU as usual."));
    }

    /// <summary>Save, relaunch and end this process. Never returns.</summary>
    private void RecoverFromRenderThreadFailure()
    {
        if (_renderThreadFailed) return;   // already on its way out
        _renderThreadFailed = true;
        TimeSpan uptime;
        try { using var self = Process.GetCurrentProcess(); uptime = DateTime.Now - self.StartTime; }
        catch { uptime = TimeSpan.Zero; }
        var next = NextRenderRecoveryLevel(_renderRecoveryLevel, uptime);
        BeginExit("WPF's render thread failed (the desktop compositor or display driver was reset)");

        var kept = CodeViewerWindow.PreserveUnsavedEdits();
        FlushStateSafely();
        try { _browserBridge?.Dispose(); } catch { /* the process is ending either way */ }
        try { PhoneBridgeService.Instance.Stop(remember: false); } catch { /* the socket dies with us anyway */ }

        CrashLog.Note("RenderRecovery",
            $"WPF's render thread failed {uptime.TotalSeconds:N0}s into this run, so no window can draw again." + Environment.NewLine +
            "Chats, bridges and drafts were saved; the agents stop with this process and resume when VibeCode starts again." + Environment.NewLine +
            (next > 0 ? $"Relaunching at recovery level {next}{(next >= SoftwareRenderingLevel ? " (software rendering)" : "")}."
                : "It failed again right after a software-rendering recovery, so VibeCode is not relaunching itself. Start it again by hand.") +
            (kept.Count > 0 ? Environment.NewLine + "Unsaved editor changes were copied to:" + Environment.NewLine + string.Join(Environment.NewLine, kept) : ""));

        ReleaseInstanceLease();
        if (next > 0 && Environment.ProcessPath is { Length: > 0 } exe)
        {
            try { Process.Start(new ProcessStartInfo { FileName = exe, Arguments = RenderRecoveryArgument + next, UseShellExecute = true }); }
            catch (Exception ex) { CrashLog.Write(ex, "RenderRecovery.Relaunch"); }
        }
        EndProcessWithoutWpfShutdown();
    }

    /// <summary>Hand the data directory to the next launch now instead of when this process finally disappears.</summary>
    private static void ReleaseInstanceLease()
    {
        try { _instanceLease?.Dispose(); } catch { /* released by process exit at the latest */ }
        _instanceLease = null;
    }

    /// <summary>WPF's dispatcher shutdown would hit the dead render thread again and crash. Everything worth keeping is
    /// already saved, and the provider children end with the job, so stop here.</summary>
    private static void EndProcessWithoutWpfShutdown()
    {
        CrashLog.ReleaseRunMarker();
        TerminateProcess(GetCurrentProcess(), 0);
        Environment.Exit(0);   // only reached if TerminateProcess somehow returned
    }

    /// <summary>Automation only: VIBECODE_HIDDEN=1 plus VIBECODE_SMOKE_RENDER_FAILURE=1 replays what the dispatcher saw
    /// on 2026-10-12 - the compositor reset, then the render thread failure - so recovery can be exercised against
    /// the real executable.</summary>
    private static void MaybeSimulateRenderFailureForSmoke(Dispatcher dispatcher)
    {
        if (Environment.GetEnvironmentVariable("VIBECODE_HIDDEN") != "1" ||
            Environment.GetEnvironmentVariable("VIBECODE_SMOKE_RENDER_FAILURE") != "1") return;
        dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            throw new COMException("{Desktop composition is disabled} (0x80263001)", DwmCompositionDisabled)));
        dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            throw new COMException("UCEERR_RENDERTHREADFAILURE (0x88980406)", RenderThreadFailure)));
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);
}
