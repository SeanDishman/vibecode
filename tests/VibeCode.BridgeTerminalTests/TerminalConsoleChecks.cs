using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Microsoft.Win32.SafeHandles;
using VibeCode;
using VibeCode.Services;
using VibeCode.Terminal;
using VibeCode.UI;

internal static partial class Program
{
    private sealed record ConsoleCapture(string Text, long Window, int Width, int Height);

    private static void VerifyTerminalDesktopLifecycle()
    {
        var (vm, clicked) = BridgeHost("terminal-lifecycle", "codex", "Clicked chat");
        var active = new ChatViewModel(clicked.Cwd, title: "Other chat", provider: "codex");
        Chats.Add(active); vm.Chats.Add(active); Call(vm, "Track", active); vm.ActiveChat = active;
        clicked.Items.Add(new TextItem { Text = "Selected chat transcript" });
        var shell = (MainWindow)typeof(MainWindow).GetConstructors(Flags).Single(c => c.GetParameters().Length == 3).Invoke([vm, null, true]);
        shell.Left = 6100; shell.Top = 250; shell.WindowStartupLocation = WindowStartupLocation.Manual;
        try
        {
            shell.Show(); Settle();
            AppSettings.Current.TerminalEnabled = false;
            var blocked = false;
            try { ((Task)Call(shell, "OpenTerminalAsync", clicked)!).GetAwaiter().GetResult(); }
            catch (InvalidOperationException) { blocked = true; }
            Check("disabled switch rejects direct console launch before creating a session", blocked
                && typeof(MainWindow).GetField("_terminalWindows", Flags)!.GetValue(shell) is null && vm.TerminalTeamRoster(clicked).Count == 0);
            AppSettings.Current.TerminalEnabled = true;
            var starting = (Task)Call(shell, "OpenTerminalAsync", clicked)!;
            PumpUntil(() => starting.IsCompleted, 30_000); starting.GetAwaiter().GetResult();
            var windows = (TerminalWindowCollection)typeof(MainWindow).GetField("_terminalWindows", Flags)!.GetValue(shell)!;
            var terminal = windows.Find(clicked)!;
            var client = (Process)typeof(TerminalSession).GetField("_process", Flags)!.GetValue(terminal)!;
            Check("actual console opens the clicked chat instead of the active chat", terminal.Host == clicked && vm.ActiveChat == active
                && terminal.Snapshot().Rows!.Any(r => r.Text.Contains("Selected chat transcript")));
            VerifyQuietTerminalConsole(terminal, client);
            var action = new System.Windows.Controls.MenuItem { DataContext = clicked };
            Call(shell, "OnOpenChatInTerminal", action, new RoutedEventArgs());
            Check("desktop minimizes after a connected terminal is ready", shell.WindowState == WindowState.Minimized);
            Check("opening the same chat reuses its console connection", windows.Find(clicked) == terminal && windows.Count == 1);
            ProbeConsole(client, "lifecycle-desktop", "/desktop\r");
            PumpUntil(() => shell.WindowState != WindowState.Minimized);
            Check("desktop command restores the shell and opens the terminal team", shell.WindowState == WindowState.Normal && vm.TerminalTeamRoster(terminal.Host).Count == 1);
            active.Items.Add(new TextItem { Text = "Second selected chat transcript" });
            starting = (Task)Call(shell, "OpenTerminalAsync", active)!;
            PumpUntil(() => starting.IsCompleted, 30_000); starting.GetAwaiter().GetResult();
            var second = windows.Find(active)!;
            var secondClient = (Process)typeof(TerminalSession).GetField("_process", Flags)!.GetValue(second)!;
            PumpUntil(() => ProbeConsole(secondClient, "second-chat").Text.Contains("Second selected chat transcript"));
            Check("opening another chat creates a separate console without merging histories", terminal.Host == clicked && second.Host == active
                && windows.Count == 2 && terminal != second && terminal.PipeName != second.PipeName
                && terminal.Snapshot().Rows!.All(r => !r.Text.Contains("Second selected chat transcript")) && vm.TerminalTeamRoster(clicked).Count == 1);
            Call(shell, "MinimizeForTerminal");
            ProbeConsole(client, "lifecycle-exit", "/exit\r", allowExit: true);
            PumpUntil(() => windows.Count == 1);
            Check("closing one console leaves the other active without restoring the desktop", windows.Find(active) == second
                && shell.WindowState == WindowState.Minimized && !secondClient.HasExited);
            ProbeConsole(secondClient, "lifecycle-last-exit", "/exit\r", allowExit: true);
            PumpUntil(() => windows.Count == 0);
            Check("last console exit restores the desktop without closing either team", shell.WindowState == WindowState.Normal
                && clicked.Status != "closed" && active.Status != "closed" && vm.TerminalTeamRoster(clicked).Count == 1 && vm.TerminalTeamRoster(active).Count == 1);
        }
        finally { shell.Close(); AppSettings.Current.TerminalEnabled = false; }
    }

    private static void VerifyRealTerminalConsole()
    {
        var name = TerminalWire.PipePrefix + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var executable = Path.ChangeExtension(typeof(App).Assembly.Location, ".exe");
        Check("production console executable is available", File.Exists(executable));
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        start.ArgumentList.Add("--vibecode-terminal"); start.ArgumentList.Add(name);
        using var client = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            server.WaitForConnectionAsync(timeout.Token).GetAwaiter().GetResult();
            Check("actual console process completes its IPC handshake", TerminalWire.ReadAsync(server, timeout.Token).GetAwaiter().GetResult()?.Type == "hello");
            TerminalWire.WriteAsync(server, new("snapshot", "Peer communication on", Agents: [new(1, "Verdant", "Codex", "fixture", "running")],
                Rows: [new(1, "Verdant", "reply", "Fixture one is running.\nTools and team messages stay available.")], Project: @"C:\work\terminal-fixture")).GetAwaiter().GetResult();
            Thread.Sleep(250);
            var capture = ProbeConsole(client, "initial");
            Check("real console buffer renders brand, transcript and project", capture.Text.Contains("VIBE CODE TERMINAL") && capture.Text.Contains("Fixture one is running.") && capture.Text.Contains("terminal-fixture"));
            CaptureNativeConsole(new IntPtr(capture.Window), "terminal-console.png");
            ProbeConsole(client, "typed", "Check the actual input\r");
            var input = TerminalWire.ReadAsync(server, timeout.Token).GetAwaiter().GetResult();
            Check("console keyboard submits one ordinary prompt", input is { Type: "input", Text: "Check the actual input" });
            TerminalWire.WriteAsync(server, new("ack", "Team is full. Draft kept.", false)).GetAwaiter().GetResult();
            Thread.Sleep(100);
            capture = ProbeConsole(client, "refused");
            Check("rejected prompt stays editable in the real console", capture.Text.Contains("Check the actual input") && capture.Text.Contains("Draft kept"));
            // Remove the rejected draft, then bracketed multiline paste must stay one prompt.
            ProbeConsole(client, "paste", new string('\b', "Check the actual input".Length) + "\u001b[200~first line\r\nsecond line\u001b[201~");
            capture = ProbeConsole(client, "pasted-draft");
            Check("bracketed paste remains one unsent multiline draft", capture.Text.Contains("first line") && capture.Text.Contains("second line") && capture.Text.Contains("one prompt"));
            ProbeConsole(client, "send-paste", "\r");
            input = TerminalWire.ReadAsync(server, timeout.Token).GetAwaiter().GetResult();
            Check("pasted newlines do not create extra agents", input is { Type: "input", Text: "first line\nsecond line" });
            TerminalWire.WriteAsync(server, new("ack")).GetAwaiter().GetResult();
            Thread.Sleep(100);
            client.Refresh(); var cpuBefore = client.TotalProcessorTime; var watch = Stopwatch.StartNew();
            Thread.Sleep(3000); client.Refresh(); watch.Stop();
            var cpuMs = (client.TotalProcessorTime - cpuBefore).TotalMilliseconds;
            var modules = client.Modules.Cast<ProcessModule>().Select(m => m.ModuleName).ToArray();
            var metrics = $"Production console process only; backend/provider/console-host memory excluded.\nWorking set: {client.WorkingSet64 / 1048576d:F1} MiB\nPrivate bytes: {client.PrivateMemorySize64 / 1048576d:F1} MiB\nIdle CPU: {cpuMs:F1} ms over {watch.Elapsed.TotalSeconds:F2} s ({100 * cpuMs / watch.Elapsed.TotalMilliseconds:F2}% of one core).\n";
            File.WriteAllText(Path.Combine(_root, "terminal-resources.txt"), metrics);
            Console.WriteLine(metrics);
            Check("console does not load WPF rendering assemblies", !modules.Any(m => m.Equals("PresentationFramework.dll", StringComparison.OrdinalIgnoreCase) || m.Equals("PresentationCore.dll", StringComparison.OrdinalIgnoreCase)));
            ProbeConsole(client, "help", "/help\r");
            capture = ProbeConsole(client, "help-visible");
            Check("terminal help exposes both message aliases, permissions and memory release", capture.Text.Contains("/message") && capture.Text.Contains("/msg") && capture.Text.Contains("/allow") && capture.Text.Contains("/sleep"));
            ControlProbe(client, new ControlAction("key", Key: ConsoleKey.PageDown));
            capture = ProbeConsole(client, "help-attachments");
            Check("help scrolls to attachment and microphone controls", capture.Text.Contains("/attach") && capture.Text.Contains("/detach") && capture.Text.Contains("F6") && capture.Text.Contains("/mic"));
            ProbeConsole(client, "exit", "/exit\r", allowExit: true);
            using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Check("console sends an explicit exit packet", TerminalWire.ReadAsync(server, exitTimeout.Token).GetAwaiter().GetResult()?.Type == "exit");
            Check("terminal exits cleanly on its own command", client.WaitForExit(5000) && client.ExitCode == 0);
        }
        finally { if (!client.HasExited) client.Kill(entireProcessTree: true); }
    }

    private static ConsoleCapture ProbeConsole(Process client, string label, string input = "", bool allowExit = false)
    {
        var output = Path.Combine(_root, "console-" + label + ".json");
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden, CreateNoWindow = true };
        start.ArgumentList.Add("--terminal-buffer-probe"); start.ArgumentList.Add(client.Id.ToString());
        start.ArgumentList.Add(output); start.ArgumentList.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(input)));
        using var probe = Process.Start(start)!;
        if (!probe.WaitForExit(5000)) { probe.Kill(); throw new TimeoutException("Console probe timed out."); }
        if (probe.ExitCode != 0 && !allowExit) throw new InvalidOperationException("Console probe failed: " + File.ReadAllText(output));
        return File.Exists(output) && probe.ExitCode == 0 ? JsonSerializer.Deserialize<ConsoleCapture>(File.ReadAllText(output))! : new("", 0, 0, 0);
    }

    private static int RunTerminalBufferProbe(string[] args)
    {
        try
        {
            TerminalNative.FreeConsole();
            if (!TerminalNative.AttachConsole(uint.Parse(args[1]))) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            // GetConsoleWindow may be a hidden message-only window under Windows Terminal. Locate only the
            // test's uniquely titled visible host before moving/capturing it, never whatever is on the desktop.
            var title = "Vibe Code Terminal fixture " + args[1];
            TerminalNative.SetConsoleTitle(title);
            Thread.Sleep(80);
            var window = IntPtr.Zero;
            TerminalNative.EnumWindows((candidate, _) =>
            {
                var caption = new StringBuilder(512); TerminalNative.GetWindowText(candidate, caption, caption.Capacity);
                if (caption.ToString().Contains(title, StringComparison.Ordinal) && TerminalNative.IsWindowVisible(candidate)) window = candidate;
                return true;
            }, IntPtr.Zero);
            if (window != IntPtr.Zero) TerminalNative.SetWindowPos(window, IntPtr.Zero, 6100, 250, 0, 0, 0x15);
            using var output = TerminalNative.CreateFile("CONOUT$", 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            using var input = TerminalNative.CreateFile("CONIN$", 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            // Printable Unicode input has no layout-specific virtual key. ASCII punctuation values are not key codes
            // (for example, a quote's value would falsely inject PageDown and a period would inject Delete).
            var keys = Encoding.UTF8.GetString(Convert.FromBase64String(args[3])).Select(c => new TerminalNative.InputRecord
            { EventType = 1, Down = 1, Repeat = 1, VirtualKey = c is '\r' or '\n' ? (short)13 : c == '\b' ? (short)8 : c == '\u001b' ? (short)27 : (short)0, Character = c }).ToArray();
            if (keys.Length > 0 && !TerminalNative.WriteConsoleInput(input, keys, (uint)keys.Length, out _)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            Thread.Sleep(180);
            if (!TerminalNative.GetConsoleScreenBufferInfo(output, out var info)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var result = new StringBuilder();
            for (var row = info.Window.Top; row <= info.Window.Bottom; row++)
            {
                var line = new StringBuilder(info.Size.X + 1);
                TerminalNative.ReadConsoleOutputCharacter(output, line, (uint)info.Size.X, new() { X = 0, Y = row }, out var read);
                result.AppendLine(line.ToString()[..Math.Min(line.Length, (int)read)].TrimEnd());
            }
            File.WriteAllText(args[2], JsonSerializer.Serialize(new ConsoleCapture(result.ToString(), window.ToInt64(), info.Size.X, info.Window.Bottom - info.Window.Top + 1)));
            return 0;
        }
        catch (Exception ex) { File.WriteAllText(args[2], ex.ToString()); return 1; }
        finally { TerminalNative.FreeConsole(); }
    }

    private static void CaptureNativeConsole(IntPtr window, string name)
    {
        if (window == IntPtr.Zero || !TerminalNative.IsWindowVisible(window))
        {
            File.WriteAllText(Path.Combine(_root, "console-capture-note.txt"), "The console host did not expose a visible capturable window. Actual screen buffer evidence is in console-initial.json.");
            return;
        }
        TerminalNative.GetWindowRect(window, out var bounds);
        var screen = TerminalNative.GetDC(IntPtr.Zero);
        var memory = TerminalNative.CreateCompatibleDC(screen);
        var bitmap = TerminalNative.CreateCompatibleBitmap(screen, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
        var previous = TerminalNative.SelectObject(memory, bitmap);
        try
        {
            if (!TerminalNative.PrintWindow(window, memory, 2))
            {
                // Some console hosts do not implement WM_PRINT. Capture this test-owned window briefly on screen.
                TerminalNative.ShowWindow(window, 4);
                TerminalNative.SetWindowPos(window, new IntPtr(-1), 40, 40, 0, 0, 0x51);
                Thread.Sleep(300);
                TerminalNative.GetWindowRect(window, out var shown);
                if (!TerminalNative.BitBlt(memory, 0, 0, shown.Right - shown.Left, shown.Bottom - shown.Top, screen, shown.Left, shown.Top, 0x00CC0020))
                    throw new InvalidOperationException("Could not capture console window.");
                TerminalNative.SetWindowPos(window, new IntPtr(-2), 6100, 250, 0, 0, 0x11);
            }
            TerminalNative.SelectObject(memory, previous);
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source));
            using var file = File.Create(Path.Combine(_root, name)); encoder.Save(file);
        }
        finally { TerminalNative.SelectObject(memory, previous); TerminalNative.DeleteObject(bitmap); TerminalNative.DeleteDC(memory); TerminalNative.ReleaseDC(IntPtr.Zero, screen); }
    }

    private static class TerminalNative
    {
        [StructLayout(LayoutKind.Sequential)] internal struct Coord { public short X, Y; }
        [StructLayout(LayoutKind.Sequential)] internal struct Rect { public short Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] internal struct BufferInfo { public Coord Size, Cursor; public short Attributes; public Rect Window; public Coord Maximum; }
        [StructLayout(LayoutKind.Sequential)] internal struct Bounds { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Explicit, Size = 20, CharSet = CharSet.Unicode)] internal struct InputRecord
        {
            [FieldOffset(0)] public short EventType; [FieldOffset(4)] public int Down;
            [FieldOffset(8)] public short Repeat; [FieldOffset(10)] public short VirtualKey;
            [FieldOffset(14)] public char Character;
        }
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool AttachConsole(uint process);
        [DllImport("kernel32.dll")] internal static extern bool FreeConsole();
        [DllImport("kernel32.dll")] internal static extern IntPtr GetConsoleWindow();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern bool SetConsoleTitle(string title);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern SafeFileHandle CreateFile(string file, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetConsoleScreenBufferInfo(SafeFileHandle output, out BufferInfo info);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool ReadConsoleOutputCharacter(SafeFileHandle output, StringBuilder text, uint length, Coord coordinate, out uint read);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool WriteConsoleInput(SafeFileHandle input, InputRecord[] records, uint count, out uint written);
        [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        internal delegate bool EnumWindowCallback(IntPtr window, IntPtr data);
        [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowCallback callback, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(IntPtr window, StringBuilder text, int max);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr window, out Bounds bounds);
        [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll")] internal static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
        [DllImport("user32.dll")] internal static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] internal static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [DllImport("gdi32.dll")] internal static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] internal static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
        [DllImport("gdi32.dll")] internal static extern IntPtr SelectObject(IntPtr dc, IntPtr item);
        [DllImport("gdi32.dll")] internal static extern bool DeleteObject(IntPtr item);
        [DllImport("gdi32.dll")] internal static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] internal static extern bool BitBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
    }
}
