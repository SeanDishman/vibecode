using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VibeCode.Services;

/// <summary>
/// Windows' own low-memory signal (CreateMemoryResourceNotification). The handle is a level rather than an event: it stays
/// set for as long as memory is low, so after each signal this waits a cooldown before listening again. That is long enough
/// for the agents just put to sleep to exit, and for Windows to say whether that was enough.
/// </summary>
internal sealed class LowMemorySignal
{
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(2);
    private readonly SignalHandle _handle;
    private readonly Action _onLowMemory;

    private LowMemorySignal(SignalHandle handle, Action onLowMemory)
    {
        _handle = handle;
        _onLowMemory = onLowMemory;
        Listen();
    }

    /// <summary>Listen for the rest of the app's life. The thread pool's registered wait keeps the listener alive; where
    /// Windows offers no such notification there is nothing to listen to.</summary>
    public static void Start(Action onLowMemory)
    {
        var handle = CreateMemoryResourceNotification(LowMemoryResourceNotification);
        if (handle != 0) _ = new LowMemorySignal(new SignalHandle(handle), onLowMemory);
    }

    private void Listen() =>
        ThreadPool.RegisterWaitForSingleObject(_handle, OnSignaled, null, Timeout.Infinite, executeOnlyOnce: true);

    private void OnSignaled(object? state, bool timedOut)
    {
        try { _onLowMemory(); }
        catch (Exception ex) { CrashLog.Write(ex, "LowMemorySignal"); }
        _ = Task.Delay(Cooldown).ContinueWith(_ => Listen(), TaskScheduler.Default);
    }

    /// <summary>Working set of every process VibeCode started, recursively: each CLI, its MCP servers and helpers.</summary>
    public static long ChildProcessMemory()
    {
        var snapshot = CreateToolhelp32Snapshot(SnapshotProcesses, 0);
        if (snapshot == InvalidHandle) return 0;
        var children = new Dictionary<uint, List<uint>>();
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            for (var more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
            {
                if (!children.TryGetValue(entry.ParentProcessId, out var list)) children[entry.ParentProcessId] = list = new();
                list.Add(entry.ProcessId);
            }
        }
        finally { CloseHandle(snapshot); }

        long total = 0;
        var pending = new Stack<uint>(children.GetValueOrDefault((uint)Environment.ProcessId) ?? []);
        var seen = new HashSet<uint>();
        while (pending.Count > 0)
        {
            var pid = pending.Pop();
            if (!seen.Add(pid)) continue;
            try
            {
                using var process = Process.GetProcessById((int)pid);
                total += process.WorkingSet64;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { continue; }   // exited since the snapshot
            foreach (var child in children.GetValueOrDefault(pid) ?? []) pending.Push(child);
        }
        return total;
    }

    private sealed class SignalHandle : WaitHandle
    {
        public SignalHandle(nint handle) => SafeWaitHandle = new SafeWaitHandle(handle, ownsHandle: true);
    }

    private const int LowMemoryResourceNotification = 0;
    private const uint SnapshotProcesses = 0x2;
    private static readonly nint InvalidHandle = -1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public nint DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateMemoryResourceNotification(int notificationType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(nint snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(nint snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);
}
