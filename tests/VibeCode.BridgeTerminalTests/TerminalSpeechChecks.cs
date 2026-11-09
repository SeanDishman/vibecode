using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using VibeCode.Services;
using VibeCode.Terminal;
using VibeCode.UI;

internal static partial class Program
{
    private static void VerifyTerminalSpeech()
    {
        VerifyDictationOwnership();
        VerifyDictationDraft();
        VerifyConsoleDictation();
        VerifyNarrowMicConsole();
    }

    private static void VerifyDictationOwnership()
    {
        var source = new SpeechFixture();
        var packets = new List<TerminalPacket>();
        using var mic = new TerminalDictation(source, "chat-a", action => action());
        mic.PacketReady += packets.Add;
        Check("opening terminal speech does not capture or preload a model", source.Starts == 0 && mic.Snapshot().State == "idle");
        source.StartError = "No microphone found.";
        mic.Handle("toggle");
        Check("missing microphone reports the device error without starting", packets.Last() is { Accepted: false, Text: "No microphone found." } && !mic.IsActive);
        source.StartError = null;
        mic.Handle("toggle");
        Check("mic toggle starts a capture owned by this terminal", mic.IsActive && ReferenceEquals(source.Owner, mic) && mic.Snapshot().State == "recording");
        mic.Handle("toggle");
        Check("second mic toggle transcribes without blocking commands", source.State == SpeechState.Transcribing && mic.Snapshot().State == "transcribing");
        source.Complete("dictated sentence");
        PumpUntil(() => packets.Any(p => p.Type == "dictation-result"));
        Check("speech returns an editable transcript with the originating chat ID", packets.Last(p => p.Type == "dictation-result") is { Text: "dictated sentence", ContextId: "chat-a" }
            && !mic.IsActive && source.Resets == 0);

        mic.Handle("toggle"); mic.Handle("toggle");
        var cancelled = source.Pending!;
        mic.Handle("cancel");
        var count = packets.Count(p => p.Type == "dictation-result");
        mic.Handle("toggle");
        cancelled.TrySetResult("late cancelled words"); Settle();
        Check("late cancelled transcription cannot overwrite a newer recording", packets.Count(p => p.Type == "dictation-result") == count && mic.IsActive && source.State == SpeechState.Recording);
        mic.Handle("toggle"); var switched = source.Pending!;
        mic.SwitchContext("chat-b"); switched.TrySetResult("words for the old chat"); Settle();
        Check("switching chats cancels capture and rejects the old result", !mic.IsActive && source.State == SpeechState.Idle && mic.ContextId == "chat-b"
            && packets.Count(p => p.Type == "dictation-result") == count);

        source.ForeignCapture(); var resets = source.Resets;
        mic.Handle("toggle"); mic.Handle("cancel");
        Check("terminal cannot steal or cancel the desktop microphone", source.State == SpeechState.Recording && source.Resets == resets && mic.Snapshot().State == "busy");
        source.ForceReset(); mic.Handle("toggle"); mic.Handle("toggle");
        source.Pending!.TrySetException(new IOException("fixture transcription unavailable"));
        PumpUntil(() => packets.Last().Text.Contains("fixture transcription unavailable"));
        Check("transcription failure releases only its own capture and explains recovery", source.State == SpeechState.Idle && !mic.IsActive
            && !packets.Last().Accepted && packets.Last().Text.Contains("draft is kept"));

        source.ReadyWithoutDownload = false; mic.Handle("toggle");
        Check("first-use model download is visible before transcription", mic.Snapshot().Hint.Contains(SpeechService.ModelDownloadSize));
        mic.Handle("toggle"); source.SetState(SpeechState.Downloading); mic.RefreshProgress();
        Check("model download exposes progress and remains cancellable", mic.NeedsProgressUpdates && packets.Last().Text.Contains("50%"));
        mic.Handle("toggle");
        Check("toggling while transcribing or downloading cancels without resetting a foreign owner", !mic.NeedsProgressUpdates && !mic.IsActive && source.State == SpeechState.Idle);

        mic.Handle("toggle"); mic.Handle("toggle"); var stale = source.Pending!;
        source.ForeignCapture(); resets = source.Resets;
        stale.TrySetResult("stale foreign-owner result"); Settle(); mic.Dispose();
        Check("disposed terminal and stale callbacks leave a newer foreign capture untouched", source.Resets == resets && source.State == SpeechState.Recording
            && packets.All(p => p.Text != "stale foreign-owner result"));
        source.ForceReset();
        var disposable = new TerminalDictation(source, "closing", action => action());
        disposable.Handle("toggle"); disposable.Dispose();
        Check("closing a terminal releases its owned recording", source.State == SpeechState.Idle);
    }

    private static void VerifyDictationDraft()
    {
        using var pipe = new NamedPipeClientStream(TerminalWire.PipePrefix + "speech-draft-test");
        var client = typeof(TerminalConsole).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
            null, [typeof(NamedPipeClientStream)], null)!.Invoke([pipe]);
        void Field(string name, object value) => typeof(TerminalConsole).GetField(name, Flags)!.SetValue(client, value);
        string Draft() => (string)typeof(TerminalConsole).GetField("_input", Flags)!.GetValue(client)!;
        var idle = new TerminalSpeech("idle", "F5 or click Mic to dictate · Enter sends after transcription");
        Field("_snapshot", new TerminalPacket("snapshot", Speech: idle, ContextId: "draft-a"));
        Field("_input", "before after"); Field("_cursor", 6);
        Call(client, "ReceiveDictation", new TerminalPacket("dictation-result", "spoken words", Speech: idle, ContextId: "draft-a"));
        Check("dictation inserts at the cursor with spacing and preserves existing draft", Draft() == "before spoken words after");
        Call(client, "ReceiveDictation", new TerminalPacket("dictation-result", "wrong chat", Speech: idle, ContextId: "draft-b"));
        Check("client rejects dictation for a different chat", !Draft().Contains("wrong chat"));
        Call(client, "ReceiveDictation", new TerminalPacket("dictation-result", "", Speech: idle, ContextId: "draft-a"));
        Check("silence keeps the draft and reports no speech", Draft() == "before spoken words after"
            && ((string)typeof(TerminalConsole).GetField("_notice", Flags)!.GetValue(client)!).Contains("No speech"));
        Field("_input", new string('x', TerminalWire.MaxPromptLength)); Field("_cursor", TerminalWire.MaxPromptLength);
        Call(client, "ReceiveDictation", new TerminalPacket("dictation-result", "speech retained", Speech: idle, ContextId: "draft-a"));
        Check("oversized draft retains speech separately instead of losing it", Draft().Length == TerminalWire.MaxPromptLength
            && (string)typeof(TerminalConsole).GetField("_heldDictation", Flags)!.GetValue(client)! == "speech retained");
        Field("_input", "Shorter"); Field("_cursor", 7); Call(client, "ToggleDictation");
        Check("F5 can insert retained speech after shortening a draft", Draft() == "Shorter speech retained");
    }

    private static void VerifyConsoleDictation()
    {
        SeedBridgeCodexAccount("terminal-speech", "Speech fixture");
        var log = Path.Combine(_root, "speech-provider.log");
        Environment.SetEnvironmentVariable("VIBECODE_TEAM_FIXTURE_LOG", log);
        Application.Current.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(
            new Uri("/VibeCode;component/Themes/Dark.xaml", UriKind.Relative)));
        var (vm, sourceChat) = BridgeHost("terminal-speech", "codex", "Speech fixture");
        sourceChat.SetMode("default", remember: false);
        var host = vm.CreateTerminalTeam(sourceChat); Chats.Add(host);
        var source = new SpeechFixture();
        using var terminal = new TerminalSession(vm, host, Dispatcher.CurrentDispatcher, source);
        var ended = false; terminal.Ended += () => ended = true;
        var starting = terminal.StartAsync(); PumpUntil(() => starting.IsCompleted, 30_000); starting.GetAwaiter().GetResult();
        var process = (Process)typeof(TerminalSession).GetField("_process", Flags)!.GetValue(terminal)!;
        using var cleanupProcess = Process.GetProcessById(process.Id);
        try
        {
            var capture = ProbeConsole(process, "speech-initial", "Keep this draft");
            var rows = capture.Text.Split('\n');
            var micRow = Array.FindIndex(rows, row => row.Contains("Mic · F5"));
            Check("actual terminal displays the microphone control beside model controls", micRow >= 0 && capture.Text.Contains("Model:"));
            ControlProbe(process, new ControlAction("click", rows[micRow].IndexOf("Mic · F5", StringComparison.Ordinal) + 1, micRow));
            PumpUntil(() => source.State == SpeechState.Recording);
            PumpUntil(() => ProbeConsole(process, "speech-recording").Text.Contains("Stop mic"));
            capture = ProbeConsole(process, "speech-editing", " while speaking\r");
            Check("recording preserves typing and Enter does not send a partial prompt", capture.Text.Contains("Keep this draft while speaking")
                && !host.Items.OfType<UserItem>().Any() && source.State == SpeechState.Recording);
            CaptureNativeConsole(new IntPtr(capture.Window), "terminal-mic-recording.png");
            ControlProbe(process, new ControlAction("key", Key: ConsoleKey.F5));
            PumpUntil(() => source.State == SpeechState.Transcribing);
            Check("F5 stops recording and begins transcription over the real pipe", source.Stops == 1);
            source.Complete("add spoken words");
            PumpUntil(() => ProbeConsole(process, "speech-inserted").Text.Contains("Keep this draft while speaking add spoken words"));
            Check("transcription appears in the draft without sending", !host.Items.OfType<UserItem>().Any());

            ControlProbe(process, new ControlAction("key", Key: ConsoleKey.F5));
            PumpUntil(() => source.State == SpeechState.Recording);
            ControlProbe(process, new ControlAction("key", Key: ConsoleKey.F5));
            PumpUntil(() => source.State == SpeechState.Transcribing);
            var stale = source.Pending!;
            ControlProbe(process, new ControlAction("key", Key: ConsoleKey.G, Modifiers: 8));
            PumpUntil(() => source.State == SpeechState.Idle);
            stale.TrySetResult("discarded words"); Settle();
            capture = ProbeConsole(process, "speech-cancelled");
            Check("Ctrl+G cancels while transcription is pending and retains the draft", !capture.Text.Contains("discarded words")
                && capture.Text.Contains("Keep this draft while speaking add spoken words"));

            ProbeConsole(process, "speech-send", "\r");
            try { PumpUntil(() => host.Items.OfType<UserItem>().Any(), 30_000); }
            catch
            {
                var failed = ProbeConsole(process, "speech-send-failed");
                Console.Error.WriteLine($"Speech send: {host.Status}; waiting={Property(host, "IsWaitingForAgentTeamPrompt")}; queued={host.HasQueued}; {failed.Text}");
                throw;
            }
            Check("only explicit Enter sends the completed dictated draft", host.Items.OfType<UserItem>().Count(u => u.IsUserPrompt) == 1
                && host.Items.OfType<UserItem>().Single(u => u.IsUserPrompt).Text.Contains("Keep this draft while speaking add spoken words"));
            PumpUntil(() => host.Status is "idle" or "error", 30_000);
            ControlProbe(process, new ControlAction("key", Key: ConsoleKey.F5));
            PumpUntil(() => source.State == SpeechState.Recording);
            var other = vm.CreateTerminalTeam(host); Chats.Add(other);
            terminal.SwitchHost(other);
            Check("switching the live terminal chat releases its microphone", source.State == SpeechState.Idle);
            PumpUntil(() => ProbeConsole(process, "speech-switched").Text.Contains("Mic · F5"));
            ControlProbe(process, new ControlAction("key", Key: ConsoleKey.F5));
            PumpUntil(() => source.State == SpeechState.Recording);
            ControlProbe(process, new ControlAction("key", Key: ConsoleKey.D, Modifiers: 8));
            PumpUntil(() => source.State == SpeechState.Idle && ended);
            Check("exiting the actual console cancels recording and preserves agents", host.Status != "closed" && other.Status != "closed");
        }
        finally
        {
            terminal.Dispose();
            if (!cleanupProcess.HasExited) cleanupProcess.Kill(entireProcessTree: true);
        }
    }

    private static void VerifyNarrowMicConsole()
    {
        // The test executable inherits PowerShell's fixed-width console settings. Use the actual GUI
        // executable for resize coverage, just as the existing terminal model-control checks do.
        var name = TerminalWire.PipePrefix + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var start = new ProcessStartInfo(Path.ChangeExtension(typeof(VibeCode.App).Assembly.Location, ".exe")) { UseShellExecute = false };
        start.ArgumentList.Add("--vibecode-terminal"); start.ArgumentList.Add(name);
        using var client = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var controls = new TerminalControls("astra", "GPT-6 Astra", [new("astra", "GPT-6 Astra")],
            "high", "High", [new("high", "High")], false, true);
        try
        {
            server.WaitForConnectionAsync(timeout.Token).GetAwaiter().GetResult();
            Check("production microphone console connects", TerminalWire.ReadAsync(server, timeout.Token).GetAwaiter().GetResult()?.Type == "hello");
            TerminalWire.WriteAsync(server, new("snapshot", Controls: controls, Project: @"C:\work\dictation",
                Speech: new("idle", "F5 or click Mic to dictate · Enter sends after transcription"), ContextId: "mic-layout")).GetAwaiter().GetResult();
            ProbeConsole(client, "speech-layout-before-resize");
            ControlProbe(client, new ControlAction("resize", X: 100, Y: 28));
            ProbeConsole(client, "speech-layout-wide", "Draft stays editable");
            ControlProbe(client, new ControlAction("resize", X: 40, Y: 18));
            var capture = ProbeConsole(client, "speech-narrow");
            Check("microphone and draft remain visible in the narrow production console", capture.Text.Contains("Mic · F5") && capture.Text.Contains("Draft stays editable"));
            var rows = capture.Text.Split('\n');
            var row = Array.FindIndex(rows, line => line.Contains("Mic · F5"));
            ControlProbe(client, new ControlAction("click", rows[row].IndexOf("Mic · F5", StringComparison.Ordinal), row));
            Check("wrapped Mic control sends the dictation command", TerminalWire.ReadAsync(server, timeout.Token).GetAwaiter().GetResult() is { Type: "dictation", Text: "toggle" });
            TerminalWire.WriteAsync(server, new("dictation-state", "Listening · F5 stops and inserts · Ctrl+G cancels",
                Speech: new("recording", "Listening · F5 stops and inserts · Ctrl+G cancels"), ContextId: "mic-layout")).GetAwaiter().GetResult();
            capture = ProbeConsole(client, "speech-narrow-recording");
            Check("recording remains visible after the microphone wraps", capture.Text.Contains("Stop mic · F5") && capture.Text.Contains("Draft stays editable"));
            CaptureNativeConsole(new IntPtr(capture.Window), "terminal-mic-narrow.png");
            ControlProbe(client, new ControlAction("key", Key: ConsoleKey.D, Modifiers: 8));
            Check("production microphone console exits cleanly", TerminalWire.ReadAsync(server, timeout.Token).GetAwaiter().GetResult()?.Type == "exit"
                && client.WaitForExit(5000) && client.ExitCode == 0);
        }
        finally { if (!client.HasExited) client.Kill(entireProcessTree: true); }
    }

    private sealed class SpeechFixture : ITerminalSpeechSource
    {
        public event EventHandler? StateChanged;
        public SpeechState State { get; private set; }
        public bool ReadyWithoutDownload { get; set; } = true;
        public string? DownloadProgressText => "750 MB of 1.5 GB (50%)";
        public object? Owner;
        public string? StartError;
        public int Token, Starts, Stops, Resets;
        public TaskCompletionSource<string>? Pending;
        public bool Owns(int token) => token != 0 && token == Token;
        public bool StartRecording(out string? error, out int token, object owner)
        {
            error = StartError; token = 0;
            if (error is not null) return false;
            if (State != SpeechState.Idle) { error = "Another composer is listening."; return false; }
            Starts++; token = ++Token; Owner = owner;
            SetState(SpeechState.Recording); return true;
        }
        public Task<string> StopAndTranscribeAsync(int token)
        {
            if (!Owns(token) || State != SpeechState.Recording) throw new InvalidOperationException("Not this capture.");
            Stops++; Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            SetState(SpeechState.Transcribing); return Pending.Task;
        }
        public void Complete(string text) { SetState(SpeechState.Idle); Owner = null; Pending!.TrySetResult(text); }
        public void ForeignCapture() { Token++; Owner = new object(); SetState(SpeechState.Recording); }
        public void SetState(SpeechState state) { State = state; StateChanged?.Invoke(this, EventArgs.Empty); }
        public string? TakeTranscriptionNote() => null;
        public void ForceReset() { Token++; Resets++; Owner = null; SetState(SpeechState.Idle); }
    }
}
