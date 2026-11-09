using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32.SafeHandles;
using VibeCode;
using VibeCode.Services;
using VibeCode.Terminal;
using VibeCode.UI;

internal static partial class Program
{
    private static void VerifyTerminalControls(bool headless = false)
    {
        var log = Path.Combine(_root, "controls-provider.log");
        File.WriteAllText(log, "");
        Environment.SetEnvironmentVariable("VIBECODE_TEAM_FIXTURE_LOG", log);
        SeedBridgeCodexAccount("controls-fixture", "Terminal controls fixture");
        Application.Current.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(new Uri("/VibeCode;component/Themes/Dark.xaml", UriKind.Relative)));
        var (vm, source) = BridgeHost("terminal-controls", "codex", "Existing work");
        source.Model = "gpt-6-astra"; source.Effort = "high"; source.FastMode = false; source.UltrafastMode = false;
        source.SetMode("default", remember: false);
        var host = vm.CreateTerminalTeam(source); Chats.Add(host);
        using var terminal = new TerminalSession(vm, host, Dispatcher.CurrentDispatcher);
        var originalDefaults = (AppSettings.Current.DefaultCodexModel, AppSettings.Current.DefaultCodexEffort, AppSettings.Current.FastMode);
        var initial = terminal.Snapshot().Controls!;
        Check("terminal lists actual provider models and filters account mode aliases", initial.Models.Any(m => m.Value == "gpt-6-sol")
            && !initial.Models.Any(m => m.Value?.StartsWith("vibecode:") == true));
        Check("terminal effort choices follow model capabilities", initial.Efforts.Any(e => e.Value == "high") && initial.Efforts.Any(e => e.Value is null));
        Check("unavailable models and unknown settings are rejected", !terminal.HandleSetting(new("model", "invented-model")).Accepted
            && !terminal.HandleSetting(new("unknown", "on")).Accepted);
        Check("terminal accepts a supported model, effort and fast mode", terminal.HandleSetting(new("model", "gpt-6-sol")).Accepted
            && terminal.HandleSetting(new("effort", "low")).Accepted && terminal.HandleSetting(new("fast", "on")).Accepted);
        Check("invalid efforts and malformed speed settings are rejected", !terminal.HandleSetting(new("effort", "invented-effort")).Accepted
            && !terminal.HandleSetting(new("fast", "sometimes")).Accepted);
        Check("controls preserve current agents and global new-chat defaults", host.Model == "gpt-6-astra" && host.Effort == "high" && !host.FastMode
            && source.Model == "gpt-6-astra" && originalDefaults == (AppSettings.Current.DefaultCodexModel, AppSettings.Current.DefaultCodexEffort, AppSettings.Current.FastMode));
        terminal.SwitchHost(host);
        Check("reopening the same terminal preserves its next-prompt selection", terminal.Snapshot().Controls is { Model: "gpt-6-sol", Effort: "low", FastMode: true });
        Check("configured first prompt is accepted", terminal.HandleInput("FIRST CONTROLLED PROMPT").Accepted);
        PumpUntil(() => host.Status == "idle" && host.Items.OfType<TextItem>().Any(), 30_000);
        Check("first agent receives the selected launch configuration", host.ChosenModel == "gpt-6-sol" && host.Effort == "low" && host.FastMode);
        Check("first provider turn uses selected model, effort and priority", ControlledTurn(log, "FIRST CONTROLLED PROMPT", "gpt-6-sol", "low", "priority"));
        Check("next-prompt selection can change independently", terminal.HandleSetting(new("model", "gpt-6-astra")).Accepted
            && terminal.HandleSetting(new("effort", "high")).Accepted && terminal.HandleSetting(new("fast", "off")).Accepted
            && host.ChosenModel == "gpt-6-sol" && host.Effort == "low" && host.FastMode);
        var sessionId = host.SessionId;
        Check("second configured prompt reuses the finished agent", terminal.HandleInput("SECOND CONTROLLED PROMPT").Accepted
            && vm.TerminalTeamRoster(host).Count == 1);
        try { PumpUntil(() => host.Status == "idle" && host.Items.OfType<UserItem>().Count(u => u.IsUserPrompt) == 2, 30_000); }
        catch
        {
            Console.WriteLine("Reuse diagnostic: " + host.Status + "; queued=" + host.HasQueued + "; users="
                + string.Join(" | ", host.Items.OfType<UserItem>().Select(u => u.Text)));
            Console.WriteLine("Reuse notices: " + string.Join(" | ", host.Items.OfType<BannerItem>().Select(b => b.Text)));
            throw;
        }
        Check("reused agent receives the updated model, effort and fast mode", host.SessionId == sessionId && host.ChosenModel == "gpt-6-astra"
            && host.Effort == "high" && !host.FastMode);
        Check("reused provider turn uses the updated configuration", ControlledTurn(log, "SECOND CONTROLLED PROMPT", "gpt-6-astra", "high", "default"));
        host.Status = "running";
        Check("changing controls leaves the busy agent unchanged", terminal.HandleSetting(new("model", "gpt-6-sol")).Accepted
            && terminal.HandleSetting(new("effort", "low")).Accepted && terminal.HandleSetting(new("fast", "on")).Accepted
            && host.ChosenModel == "gpt-6-astra" && host.Effort == "high" && !host.FastMode);
        Check("busy team creates a configured teammate", terminal.HandleInput("THIRD CONTROLLED PROMPT").Accepted);
        var second = vm.TerminalTeamRoster(host).Last(); Chats.Add(second);
        PumpUntil(() => second.Status == "idle" && second.Items.OfType<TextItem>().Any(), 30_000);
        Check("fresh teammate receives the new configuration without changing the first", second != host && second.ChosenModel == "gpt-6-sol"
            && second.Effort == "low" && second.FastMode && host.ChosenModel == "gpt-6-astra" && !host.FastMode);
        Check("new teammate provider turn uses updated launch configuration", ControlledTurn(log, "THIRD CONTROLLED PROMPT", "gpt-6-sol", "low", "priority"));
        host.Status = "idle";
        Check("fixture agent can release its idle provider", (bool)Call(host, "SleepWhileIdle")!);
        Check("a sleeping agent accepts the next configured prompt", terminal.HandleInput("FOURTH CONTROLLED PROMPT").Accepted
            && vm.TerminalTeamRoster(host).Count == 2);
        PumpUntil(() => host.Status == "idle" && host.Items.OfType<UserItem>().Count(u => u.IsUserPrompt) == 3, 30_000);
        Check("resumed provider keeps its session and uses the selected settings", host.SessionId == sessionId
            && ControlledTurn(log, "FOURTH CONTROLLED PROMPT", "gpt-6-sol", "low", "priority"));
        Check("reusing agents leaves global defaults untouched", originalDefaults ==
            (AppSettings.Current.DefaultCodexModel, AppSettings.Current.DefaultCodexEffort, AppSettings.Current.FastMode));

        var (otherVm, others) = Team("unsupported-controls", 1, "gemini");
        var other = others[0];
        other.Models.Clear();
        other.Models.Add(new ModelChoice { Provider = "gemini", Value = "fixture-basic", Display = "Basic", SupportsEffort = false, SupportsFastMode = false });
        other.Model = "fixture-basic";
        using var basic = new TerminalSession(otherVm, other, Dispatcher.CurrentDispatcher);
        Check("unsupported effort and fast controls are unavailable", basic.Snapshot().Controls is { FastAvailable: false, Efforts.Length: 0 }
            && !basic.HandleSetting(new("effort", "high")).Accepted && !basic.HandleSetting(new("fast", "on")).Accepted);
        Check("fast mode can always be switched off", basic.HandleSetting(new("fast", "off")).Accepted);
        terminal.SwitchHost(other);
        Check("opening a different chat resets the terminal launch selection", terminal.Snapshot().Controls is { Model: "fixture-basic", FastAvailable: false }
            && terminal.Host == other);
        terminal.SwitchHost(host);
        Check("returning to a chat reflects its own saved settings", terminal.Snapshot().Controls is { Model: "gpt-6-sol", Effort: "low", FastMode: true });
        Check("new team preserves explicitly selected prompt settings", terminal.HandleSetting(new("effort", "high")).Accepted
            && terminal.HandleInput("/new").Accepted && terminal.Snapshot().Controls is { Effort: "high" });
        Chats.Add(terminal.Host);
        if (!headless) VerifyTerminalControlConsole();
    }

    private static bool ControlledTurn(string log, string marker, string model, string effort, string? tier) =>
        ReadShared(log).Select(line => JsonNode.Parse(line)).Any(node => node?["method"]?.ToString() == "turn/start"
            && node["params"]?.ToJsonString().Contains(marker) == true && node["params"]?["model"]?.ToString() == model
            && node["params"]?["effort"]?.ToString() == effort && node["params"]?["serviceTier"]?.ToString() == tier);

    private static void VerifyTerminalControlConsole()
    {
        var name = TerminalWire.PipePrefix + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var start = new ProcessStartInfo(Path.ChangeExtension(typeof(App).Assembly.Location, ".exe")) { UseShellExecute = false };
        start.ArgumentList.Add("--vibecode-terminal"); start.ArgumentList.Add(name);
        using var client = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        TerminalPacket Read() => TerminalWire.ReadAsync(server, timeout.Token).GetAwaiter().GetResult()!;
        void Write(TerminalPacket packet) => TerminalWire.WriteAsync(server, packet, timeout.Token).GetAwaiter().GetResult();
        var controls = new TerminalControls("astra", "GPT-6 Astra", [new("astra", "GPT-6 Astra"), new("sol", "GPT-6 Sol"), new("basic", "Basic")],
            "high", "High", [new(null, "Model default"), new("low", "Low"), new("high", "High")], false, true);
        TerminalPacket Snapshot() => new("snapshot", Agents: [new(1, "Sage", "Codex", "astra", "running")],
            Rows: [new(1, "Sage", "reply", "Your current agent keeps working with its original settings.")], Project: @"C:\work\terminal-controls", Controls: controls);
        void Ack(bool accepted = true, string message = "Updated for the next prompt.") => Write(new("settings", message, accepted, Controls: controls));
        try
        {
            server.WaitForConnectionAsync(timeout.Token).GetAwaiter().GetResult();
            Check("controls console connects to its local pipe", Read().Type == "hello");
            Write(Snapshot());
            Thread.Sleep(150);
            ProbeConsole(client, "controls-before-resize");
            ControlProbe(client, new ControlAction("resize", X: 100, Y: 28));
            var capture = ProbeConsole(client, "controls-initial", "Keep my draft");
            Check("compact header shows model, effort and fast mode", capture.Text.Contains("Model: GPT-6 Astra") && capture.Text.Contains("Effort: High") && capture.Text.Contains("Fast: Off"));
            ControlProbe(client, new ControlAction("click", X: 2, Y: 3));
            capture = ProbeConsole(client, "controls-model-open");
            Check("clicking the model control opens its dropdown", capture.Text.Contains("model · next prompt") && capture.Text.Contains("* GPT-6 Astra") && capture.Text.Contains("GPT-6 Sol"));
            CaptureNativeConsole(new IntPtr(capture.Window), "model-dropdown.png");
            ControlProbe(client, new ControlAction("click", X: 4, Y: 6));
            Check("clicking a model sends a setting packet", Read() is { Type: "configure", Setting: { Name: "model", Value: "sol" } });
            controls = controls with { Model = "sol", ModelLabel = "GPT-6 Sol" }; Ack();
            capture = ProbeConsole(client, "controls-model-applied");
            Check("settings acknowledgment preserves the unsent draft", capture.Text.Contains("Model: GPT-6 Sol") && capture.Text.Contains("Keep my draft"));

            ControlProbe(client, new("key", Key: ConsoleKey.F3), new("key", Key: ConsoleKey.Home), new("key", Key: ConsoleKey.DownArrow), new("key", Key: ConsoleKey.Enter));
            Check("effort dropdown supports keyboard selection", Read() is { Type: "configure", Setting: { Name: "effort", Value: "low" } });
            controls = controls with { Effort = "low", EffortLabel = "Low" }; Ack();
            ProbeConsole(client, "controls-effort-applied");
            ControlProbe(client, new("key", Key: ConsoleKey.F4), new("key", Key: ConsoleKey.End), new("key", Key: ConsoleKey.Enter));
            Check("fast mode dropdown sends On", Read() is { Type: "configure", Setting: { Name: "fast", Value: "on" } });
            controls = controls with { FastMode = true }; Ack();
            capture = ProbeConsole(client, "controls-fast-on");
            Check("fast mode selection is displayed", capture.Text.Contains("Fast: On"));
            ControlProbe(client, new("key", Key: ConsoleKey.F4), new("key", Key: ConsoleKey.Home), new("key", Key: ConsoleKey.Enter));
            Check("fast mode dropdown sends Off", Read() is { Type: "configure", Setting: { Name: "fast", Value: "off" } });
            controls = controls with { FastMode = false }; Ack();
            ProbeConsole(client, "controls-fast-off");

            ControlProbe(client, new("resize", X: 40, Y: 18), new("click", X: 2, Y: 3));
            capture = ProbeConsole(client, "controls-narrow-open");
            Check("narrow terminal keeps controls and draft visible", capture.Text.Contains("model · next prompt") && capture.Text.Contains("Keep my draft"));
            Check("resizing clears stale composer rows", capture.Text.Split("Keep my draft").Length == 2);
            CaptureNativeConsole(new IntPtr(capture.Window), "model-dropdown-narrow.png");
            ControlProbe(client, new ControlAction("click", X: 4, Y: 5));
            Check("dropdown hit testing wins over wrapped controls underneath", Read() is { Type: "configure", Setting: { Name: "model", Value: "astra" } });
            Ack(false, "Model changed; choose again.");
            capture = ProbeConsole(client, "controls-rejected");
            Check("rejected settings show the reason and retain draft and selection", capture.Text.Contains("Model changed; choose again.") && capture.Text.Contains("Model: GPT-6 Sol") && capture.Text.Contains("Keep my draft"));

            controls = controls with { Models = Enumerable.Range(1, 30).Select(i => new TerminalOption("model-" + i, "Available model " + i)).ToArray() };
            Write(Snapshot()); ProbeConsole(client, "controls-long-list");
            ControlProbe(client, new("key", Key: ConsoleKey.F2), new("key", Key: ConsoleKey.End));
            capture = ProbeConsole(client, "controls-list-end");
            Check("long dropdowns scroll to the final model", capture.Text.Contains("Available model 30") && capture.Text.Contains("30/30"));
            controls = controls with { Models = [new("sol", "GPT-6 Sol"), new("basic", "Basic")] };
            Write(Snapshot());
            capture = ProbeConsole(client, "controls-list-shrunk");
            Check("live catalog shrink keeps the open picker usable", !client.HasExited && capture.Text.Contains("2/2") && capture.Text.Contains("Basic"));
            ControlProbe(client, new ControlAction("key", Key: ConsoleKey.Escape));
            controls = controls with { FastAvailable = false, Efforts = [], Effort = null, EffortLabel = "Unavailable" }; Write(Snapshot());
            ProbeConsole(client, "controls-disabled");
            ControlProbe(client, new ControlAction("key", Key: ConsoleKey.F4));
            capture = ProbeConsole(client, "controls-disabled-fast");
            Check("unsupported fast mode explains why it is disabled", capture.Text.Contains("Fast: Unavailable") && capture.Text.Contains("Fast mode unavailable"));
            ControlProbe(client, new("key", Key: ConsoleKey.Escape), new("key", Key: ConsoleKey.Enter));
            Check("picker interactions never replace or submit the draft prematurely", Read() is { Type: "input", Text: "Keep my draft" });
            Write(new("ack")); ProbeConsole(client, "controls-draft-sent");
            ProbeConsole(client, "controls-exit", "/exit\r", allowExit: true);
            Check("controls console exits cleanly", Read().Type == "exit" && client.WaitForExit(5000) && client.ExitCode == 0);
        }
        finally { if (!client.HasExited) client.Kill(entireProcessTree: true); }
    }

    private sealed record ControlAction(string Kind, int X = 0, int Y = 0, ConsoleKey Key = 0, uint Modifiers = 0);

    private static void ControlProbe(Process client, params ControlAction[] actions)
    {
        var output = Path.Combine(_root, "controls-probe-result.txt");
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden, CreateNoWindow = true };
        start.ArgumentList.Add("--terminal-controls-probe"); start.ArgumentList.Add(client.Id.ToString()); start.ArgumentList.Add(output);
        start.ArgumentList.Add(Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(actions)));
        using var process = Process.Start(start)!;
        if (!process.WaitForExit(5000)) { process.Kill(); throw new TimeoutException("Control input probe timed out."); }
        if (process.ExitCode != 0) throw new InvalidOperationException(File.ReadAllText(output));
    }

    private static int RunTerminalControlsProbe(string[] args)
    {
        try
        {
            TerminalNative.FreeConsole();
            if (!TerminalNative.AttachConsole(uint.Parse(args[1]))) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            using var input = TerminalNative.CreateFile("CONIN$", 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            using var output = TerminalNative.CreateFile("CONOUT$", 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            foreach (var action in JsonSerializer.Deserialize<ControlAction[]>(Convert.FromBase64String(args[3]))!)
            {
                if (!TerminalNative.GetConsoleScreenBufferInfo(output, out var info)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                if (action.Kind == "resize")
                {
                    // Resize this test's actual window: the legacy viewport API targets the main buffer,
                    // while the terminal is displaying its alternate buffer.
                    var window = IntPtr.Zero;
                    var title = "Vibe Code Terminal fixture " + args[1];
                    TerminalNative.EnumWindows((candidate, _) =>
                    {
                        var caption = new StringBuilder(512); TerminalNative.GetWindowText(candidate, caption, caption.Capacity);
                        if (caption.ToString().Contains(title, StringComparison.Ordinal) && TerminalNative.IsWindowVisible(candidate)) window = candidate;
                        return true;
                    }, IntPtr.Zero);
                    if (window == IntPtr.Zero || !TerminalNative.GetWindowRect(window, out var bounds)
                        || !ControlNative.GetCurrentConsoleFont(output, false, out var font))
                        throw new InvalidOperationException("The fixture console does not expose a resizable window.");
                    var width = bounds.Right - bounds.Left + (action.X - (info.Window.Right - info.Window.Left + 1)) * font.Size.X;
                    var height = bounds.Bottom - bounds.Top + (action.Y - (info.Window.Bottom - info.Window.Top + 1)) * font.Size.Y;
                    if (!TerminalNative.SetWindowPos(window, IntPtr.Zero, 0, 0, width, height, 0x16)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                    Thread.Sleep(180);
                    TerminalNative.GetConsoleScreenBufferInfo(output, out var resized);
                    if (resized.Window.Right - resized.Window.Left + 1 != action.X || resized.Window.Bottom - resized.Window.Top + 1 != action.Y)
                        throw new InvalidOperationException($"Viewport did not resize to {action.X}x{action.Y}; was {resized.Window.Right - resized.Window.Left + 1}x{resized.Window.Bottom - resized.Window.Top + 1}.");
                }
                else
                {
                    var records = action.Kind == "click" ? new[]
                    {
                        new ControlNative.InputRecord { EventType = 2, X = (short)(info.Window.Left + action.X), Y = (short)(info.Window.Top + action.Y), Buttons = 1 },
                        new ControlNative.InputRecord { EventType = 2, X = (short)(info.Window.Left + action.X), Y = (short)(info.Window.Top + action.Y) },
                    } : new[] { new ControlNative.InputRecord { EventType = 1, Down = 1, Repeat = 1, Key = (short)action.Key, Modifiers = action.Modifiers,
                        Character = action.Key == ConsoleKey.Enter ? '\r' : action.Key == ConsoleKey.Escape ? '\u001b' : action.Key == ConsoleKey.Tab ? '\t' : '\0' } };
                    if (!ControlNative.WriteConsoleInput(input, records, (uint)records.Length, out _)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                }
                Thread.Sleep(80);
            }
            File.WriteAllText(args[2], "OK");
            return 0;
        }
        catch (Exception ex) { File.WriteAllText(args[2], ex.ToString()); return 1; }
        finally { TerminalNative.FreeConsole(); }
    }

    private static class ControlNative
    {
        [StructLayout(LayoutKind.Explicit, Size = 20, CharSet = CharSet.Unicode)] internal struct InputRecord
        {
            [FieldOffset(0)] public short EventType; [FieldOffset(4)] public int Down;
            [FieldOffset(8)] public short Repeat; [FieldOffset(10)] public short Key;
            [FieldOffset(14)] public char Character; [FieldOffset(16)] public uint Modifiers;
            [FieldOffset(4)] public short X; [FieldOffset(6)] public short Y; [FieldOffset(8)] public uint Buttons;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool WriteConsoleInput(SafeFileHandle input, InputRecord[] records, uint count, out uint written);
        [StructLayout(LayoutKind.Sequential)] internal struct FontInfo { public uint Index; public TerminalNative.Coord Size; }
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetCurrentConsoleFont(SafeFileHandle output, bool maximum, out FontInfo font);
    }
}
