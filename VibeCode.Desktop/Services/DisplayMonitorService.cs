using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace VibeCode.Services;

internal readonly record struct MonitorWorkArea(nint Handle, int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public long CenterX => (long)Left + Width / 2L;
    public long CenterY => (long)Top + Height / 2L;
}

/// <summary>Small Win32 adapter for active-monitor discovery and pixel-correct placement in a PerMonitorV2 app.</summary>
internal static class DisplayMonitorService
{
    private const int SmCmonitors = 80;
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint DisplayDeviceAttachedToDesktop = 0x00000001;
    private const uint DisplayDeviceMirroringDriver = 0x00000008;

    public static int ActiveMonitorCount => Math.Max(0, GetSystemMetrics(SmCmonitors));

    /// <summary>
    /// The display a window is on. Pass <paramref name="ensureHandle"/> to answer for a window that has not been
    /// shown yet: that forces the HWND into existence, which applies the window's Left/Top, so the caller learns
    /// where it WOULD appear and can move it before the first frame instead of letting the user watch it jump.
    /// </summary>
    public static nint MonitorFor(Window window, bool ensureHandle = false)
    {
        var helper = new WindowInteropHelper(window);
        var hwnd = ensureHandle ? helper.EnsureHandle() : helper.Handle;
        return hwnd == nint.Zero ? nint.Zero : MonitorFromWindow(hwnd, MonitorDefaultToNearest);
    }

    public static bool TryGetCompanionWorkArea(Window mainWindow, out MonitorWorkArea target)
    {
        target = default;
        if (ActiveMonitorCount < 2) return false; // excludes mirror/pseudo-monitor-only configurations

        var monitors = EnumerateWorkAreas();
        if (monitors.Count < 2) return false;

        var currentHandle = MonitorFor(mainWindow);
        var current = monitors.FirstOrDefault(x => x.Handle == currentHandle);
        var candidates = monitors.Where(x => x.Handle != currentHandle).ToList();
        if (candidates.Count == 0) return false;

        // Deterministic and topology-agnostic: choose the physically nearest other display. Coordinates may be
        // negative and the display containing VibeCode is not necessarily Windows' primary display.
        target = current.Handle == nint.Zero
            ? candidates.OrderBy(x => x.Left).ThenBy(x => x.Top).First()
            : candidates.OrderBy(x => SquaredCenterDistance(current, x)).ThenBy(x => x.Left).ThenBy(x => x.Top).First();
        return true;
    }

    /// <summary>
    /// Place the HWND with native pixel coordinates before optionally maximizing. WPF Window.Left/Top are DIPs, so
    /// assigning a MONITORINFO rectangle to them directly would misplace the window when displays use different scaling.
    /// Callers launching a non-activating window must pass false, Show it, then maximize (a WPF ordering requirement).
    /// </summary>
    public static void PlaceOnMonitor(Window window, MonitorWorkArea target, bool maximize)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        if (hwnd == nint.Zero || target.Width <= 0 || target.Height <= 0) return;

        window.WindowState = WindowState.Normal;
        if (!SetWindowPos(hwnd, nint.Zero, target.Left, target.Top, target.Width, target.Height,
                SwpNoZOrder | SwpNoActivate))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not place the Bridge monitor window.");

        // A topology change can invalidate an HMONITOR between enumeration and SetWindowPos. In that race the call
        // may still succeed after Windows clips the HWND elsewhere; verify before hiding panes from the main surface.
        if (MonitorFromWindow(hwnd, MonitorDefaultToNearest) != target.Handle)
            throw new InvalidOperationException("The selected Bridge display is no longer available.");
        if (maximize) window.WindowState = WindowState.Maximized;
    }

    /// <summary>
    /// Move a window, at the size it already is, to the middle of a target display's work area.
    ///
    /// Native pixels throughout, and the size is read back off the HWND rather than from Width/Height: those are
    /// DIPs measured against whatever display the window is on now, so on a 150%/100% pair they are the wrong
    /// pixel count for the destination. Shrinks to fit rather than hanging off the edge of a smaller display.
    /// </summary>
    public static void CentreOnMonitor(Window window, MonitorWorkArea target)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        if (hwnd == nint.Zero || target.Width <= 0 || target.Height <= 0) return;
        if (!GetWindowRect(hwnd, out var current))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not measure the telemetry window.");

        var width = Math.Min(current.Right - current.Left, target.Width);
        var height = Math.Min(current.Bottom - current.Top, target.Height);
        window.WindowState = WindowState.Normal;
        if (!SetWindowPos(hwnd, nint.Zero, target.Left + (target.Width - width) / 2,
                target.Top + (target.Height - height) / 2, width, height, SwpNoZOrder | SwpNoActivate))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not place the telemetry window.");
    }

    private static long SquaredCenterDistance(MonitorWorkArea a, MonitorWorkArea b)
    {
        var dx = a.CenterX - b.CenterX;
        var dy = a.CenterY - b.CenterY;
        return dx * dx + dy * dy;
    }

    private static List<MonitorWorkArea> EnumerateWorkAreas()
    {
        var result = new List<MonitorWorkArea>();
        var activeDevices = EnumerateActiveDisplayDevices();
        bool AddMonitor(nint monitor, nint hdc, ref NativeRect bounds, nint data)
        {
            var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            if (GetMonitorInfo(monitor, ref info)
                && (activeDevices.Count == 0 || activeDevices.Contains(info.DeviceName))
                && info.Work.Right > info.Work.Left
                && info.Work.Bottom > info.Work.Top)
            {
                result.Add(new MonitorWorkArea(monitor, info.Work.Left, info.Work.Top,
                    info.Work.Right, info.Work.Bottom));
            }
            return true;
        }
        MonitorEnumProc callback = AddMonitor;
        EnumDisplayMonitors(nint.Zero, nint.Zero, callback, nint.Zero);
        return result;
    }

    private static HashSet<string> EnumerateActiveDisplayDevices()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (uint index = 0; ; index++)
        {
            var device = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
            if (!EnumDisplayDevices(null, index, ref device, 0)) break;
            if ((device.StateFlags & DisplayDeviceAttachedToDesktop) != 0
                && (device.StateFlags & DisplayDeviceMirroringDriver) == 0
                && !string.IsNullOrWhiteSpace(device.DeviceName))
                result.Add(device.DeviceName);
        }
        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool MonitorEnumProc(nint monitor, nint hdc, ref NativeRect bounds, nint data);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(nint hdc, nint clip,
        MonitorEnumProc callback, nint data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfoEx info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string? device, uint deviceIndex, ref DisplayDevice displayDevice,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);
}
