using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace VibeCode.Services;

/// <summary>
/// Notices Discord's in-game overlay hooked into VibeCode and says so, once per session. Discord injects
/// DiscordHook64.dll into whatever it treats as a game. On VibeCode it can never draw: WPF renders through Direct3D 9Ex,
/// where the overlay's texture creation fails (D3DERR_INVALIDCALL), so it retries in a tight fail-and-reset loop and every
/// round leaves a ~16 MB heap segment behind. Measured on a real session: 1,504 such segments, 24.7 GB committed, Windows
/// at 94% memory, all of it from the hook's own log ("InitOverlay::CheckDevice: failed to create texture"). VibeCode
/// cannot safely unload another program's hook, and memory leaked inside the process only comes back on restart, so the
/// fix is Discord's overlay switch for VibeCode. This makes sure the user hears about it before the memory is gone.
/// </summary>
internal static class OverlayHookWatch
{
    internal const string DiscordHookModule = "DiscordHook64.dll";
    private static DispatcherTimer? _timer;
    private static bool _warned;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string moduleName);

    /// <summary>True while Discord's overlay hook is loaded in this process. A loader lookup, so cheap enough to poll.</summary>
    internal static bool DiscordOverlayHooked() => GetModuleHandleW(DiscordHookModule) != 0;

    /// <summary>Checks now and then once a minute: Discord can inject long after launch, whenever it decides VibeCode is
    /// a game that is running.</summary>
    public static void Start()
    {
        if (_timer is not null) return;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(1) };
        _timer.Tick += (_, _) => Check();
        _timer.Start();
        Check();
    }

    /// <summary>Warns once when the hook is present. The hook never unloads, so after the warning there is nothing left
    /// to watch for until the next launch.</summary>
    internal static void Check()
    {
        if (_warned || !DiscordOverlayHooked()) return;
        _warned = true;
        _timer?.Stop();
        long privateBytes;
        using (var me = Process.GetCurrentProcess()) privateBytes = me.PrivateMemorySize64;
        CrashLog.Note("OverlayHook", $"{DiscordHookModule} is loaded; private memory {privateBytes >> 20} MB. Warned the user.");
        NotificationService.NotifyDiscordOverlayHooked(privateBytes);
    }
}
