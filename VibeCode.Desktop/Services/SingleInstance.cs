using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace VibeCode.Services;

/// <summary>
/// One VibeCode per data directory.
///
/// ==================================== WHY THIS EXISTS ====================================
/// The installer keeps every version it has ever put down
/// (<c>%LOCALAPPDATA%\Programs\VibeCode\versions\&lt;stamp&gt;-&lt;hash&gt;\VibeCode.exe</c>), and nothing stopped two of
/// them running at once. Observed on this machine: PID 23040 from build 20260818-181055 and PID 12512 from build
/// 20260819-171819, both live, both reading and writing the same %APPDATA%\VibeCode\settings.json.
///
/// <see cref="AppSettings.Current"/> is a load-once static that rewrites the WHOLE document on every save, and the
/// app saves constantly (open chats, recent folders, bridges). <see cref="AppSettings"/> survives that only
/// because it three-way merges against the file first — which holds only while EVERY running writer does the same.
/// A build that predates the merge republishes its stale copy whole, and the fields that lose are the ones written
/// once and then never again: Borderless, GroqSpeechEnabled. It reads as "the app forgot my setting on restart".
///
/// Keyed on the DATA DIRECTORY rather than on the exe or a fixed name, for two reasons: two builds sharing one
/// settings.json is exactly the case being prevented, and every test harness in this tree runs with its own
/// VIBECODE_DATA_DIR — those must never collide with the user's real app, or a harness would either refuse to
/// start or, worse, pull their window around.
/// =========================================================================================
///
/// Every failure path here returns "you own it". Refusing to launch because a mutex misbehaved would be a far
/// worse bug than the one this prevents.
/// </summary>
public static class SingleInstance
{
    /// <summary>Raised on a background thread when another launch asked the running instance to come forward.
    /// Marshal to the UI thread before touching windows.</summary>
    public static event Action? ShowRequested;

    private static FileSystemWatcher? _watcher;

    private static string SignalPath => Path.Combine(AppSettings.Dir, "show.signal");

    /// <summary>Claim this data directory. Returns false only when another live VibeCode already holds it, in
    /// which case it has been asked to show itself and this process should exit without touching anything.</summary>
    public static bool TryAcquire(out IDisposable? lease)
    {
        lease = null;
        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(initiallyOwned: false, "Local\\VibeCode.Instance." + KeyForDataDir());
            bool owned;
            try { owned = mutex.WaitOne(TimeSpan.Zero); }
            // The previous owner died without releasing (killed, or a hard crash). The directory is ours.
            catch (AbandonedMutexException) { owned = true; }

            if (!owned)
            {
                mutex.Dispose();
                AskRunningInstanceToShow();
                return false;
            }

            StartSignalWatch();
            lease = new Lease(mutex);
            return true;
        }
        catch (Exception ex)
        {
            // Includes the mutex being unopenable for permission reasons. Log it and carry on as the only
            // instance: a launch that never happens is not an acceptable failure mode.
            try { CrashLog.Write(ex, "SingleInstance.TryAcquire"); } catch { /* never throw from startup */ }
            try { mutex?.Dispose(); } catch { /* ignore */ }
            return true;
        }
    }

    /// <summary>A stable, filename-safe id for this data directory. Case-folded because Windows paths are.</summary>
    private static string KeyForDataDir()
    {
        var dir = AppSettings.Dir.TrimEnd('\\', '/').ToLowerInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dir)))[..16];
    }

    /// <summary>Bring the instance that owns this directory to the front.
    ///
    /// Two mechanisms, because neither covers everything: the signal file reaches an instance that is CLOSED TO
    /// THE NOTIFICATION AREA (it has no window to activate, so SetForegroundWindow has nothing to aim at), and
    /// the direct activation covers the moment before that instance has started watching.</summary>
    private static void AskRunningInstanceToShow()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Dir);
            File.WriteAllText(SignalPath, DateTime.UtcNow.ToString("O"));
        }
        catch { /* the direct activation below may still work */ }

        try
        {
            using var me = Process.GetCurrentProcess();
            foreach (var other in Process.GetProcessesByName("VibeCode"))
            {
                using (other)
                {
                    if (other.Id == me.Id) continue;
                    if (other.MainWindowHandle != IntPtr.Zero)
                    {
                        ShowWindow(other.MainWindowHandle, SW_RESTORE);
                        SetForegroundWindow(other.MainWindowHandle);
                    }
                }
            }
        }
        catch { /* best effort; the signal file is the reliable half */ }
    }

    /// <summary>Watch for a second launch asking us to come forward. The file is deleted as it is consumed, so a
    /// leftover from a crash cannot keep re-raising it.</summary>
    private static void StartSignalWatch()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Dir);
            try { if (File.Exists(SignalPath)) File.Delete(SignalPath); } catch { /* stale, harmless */ }

            _watcher = new FileSystemWatcher(AppSettings.Dir, "show.signal")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                EnableRaisingEvents = true,
            };
            _watcher.Created += (_, _) => Consume();
            _watcher.Changed += (_, _) => Consume();
        }
        catch (Exception ex)
        {
            try { CrashLog.Write(ex, "SingleInstance.StartSignalWatch"); } catch { /* ignore */ }
        }
    }

    private static void Consume()
    {
        try { if (File.Exists(SignalPath)) File.Delete(SignalPath); }
        catch { /* another handler got there first, or it is still being written */ }
        try { ShowRequested?.Invoke(); } catch { /* a subscriber fault must not kill the watcher */ }
    }

    private sealed class Lease(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            try { _watcher?.Dispose(); } catch { /* ignore */ }
            _watcher = null;
            try { mutex.ReleaseMutex(); } catch { /* not held / already gone */ }
            try { mutex.Dispose(); } catch { /* ignore */ }
        }
    }

    private const int SW_RESTORE = 9;
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
