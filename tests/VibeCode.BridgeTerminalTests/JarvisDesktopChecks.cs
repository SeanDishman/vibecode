using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VibeCode;
using VibeCode.Services;
using VibeCode.Protocol;
using VibeCode.UI;

internal static partial class Program
{
    private static void RunLiveJarvisDesktop()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CODEX_HOME")))
            throw new InvalidOperationException("Live test requires signed-in CODEX_HOME.");
        AppSettings.Current.AgentSwarmsEnabled = false;

        AppSettings.Current.McpServers.Clear();
        var planner = new JarvisDialogueService(Path.Combine(_root, "jarvis-dialogue-" + Guid.NewGuid().ToString("N")));
        var context = new JarvisContext(null, [], @"C:\Users\Fixture", false);
        var selection = new JarvisSelection("codex", "gpt-6-luna", "low");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(150));
        var apps = planner.PlanAsync(selection, context, [], "Can you see what apps are running on my computer?", stop.Token);
        PumpUntil(() => apps.IsCompleted, 150000);
        var appPlan = apps.GetAwaiter().GetResult().Plan;
        Check("live Jarvis plans the screenshot's running-app request", appPlan.Actions.Count == 1 && appPlan.Actions[0].Kind == "list_apps"
            && appPlan.Actions[0].Target == "");
        var everyday = planner.PlanAsync(selection, context, [], "Give me three ideas for a relaxing evening at home. No apps to open, just suggestions.", stop.Token);
        PumpUntil(() => everyday.IsCompleted, 150000);
        var everydayPlan = everyday.GetAwaiter().GetResult().Plan;
        Check("live Jarvis answers an ordinary request without a coding chat", everydayPlan.Actions.Count == 0 && everydayPlan.Reply.Length > 50
            && !everydayPlan.Reply.Contains("VibeCode", StringComparison.OrdinalIgnoreCase));
        var chats = planner.PlanAsync(selection, context, [], "Close chats containing login or checkout.", stop.Token);
        PumpUntil(() => chats.IsCompleted, 150000);
        var chatPlan = chats.GetAwaiter().GetResult().Plan;
        Check("live Jarvis can express chat filters in the provider schema", chatPlan.Actions.Count == 1
            && chatPlan.Actions[0] is { Kind: "close_chats", ChatFilter: { Match: "any" } }
            && chatPlan.Actions[0].ChatFilter!.Terms!.SequenceEqual(new[] { "login", "checkout" }));
        File.WriteAllText(Path.Combine(_root, "jarvis-desktop-planning.json"), System.Text.Json.JsonSerializer.Serialize(new
        { selection, apps = appPlan, everyday = everydayPlan, chats = chatPlan }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    private static void VerifyJarvisDesktopActions()
    {
        using (var session = new CodexSession(new CodexSessionOptions { Cwd = Environment.CurrentDirectory, DialogueOnly = true }))
        {
            var turnRequest = new JsonObject();
            Call(session, "ConfigureDialogueOnlyTurn", turnRequest);
            var shapes = turnRequest["outputSchema"]!["properties"]!["actions"]!["items"]!["anyOf"]!.AsArray();
            var kinds = shapes.SelectMany(shape => shape!["properties"]!["kind"]!["enum"]!.AsArray()).Select(kind => kind!.ToString()).ToHashSet();
            Check("Codex's actual response schema permits every desktop action", new[] { "list_apps", "open_app", "focus_app", "close_app", "open_path", "open_url", "search_web", "system_info" }.All(kinds.Contains));
            Check("Codex's actual response schema permits chat filtering", new[] { "list_chats", "open_chat", "close_chats" }.All(kinds.Contains)
                && shapes[1]!["properties"]!["terms"] is not null && shapes[1]!["properties"]!["include_subdirectories"] is not null);
            Check("the schema permits settings, MCP and new-chat actions", new[] { "read_settings", "open_settings", "update_settings", "configure_mcp", "remove_mcp", "create_chats" }.All(kinds.Contains));
            Check("the schema retains project actions and excludes arbitrary commands", kinds.Count == 20 && kinds.Contains("start_chat") && !kinds.Contains("run_command"));
        }
        const string request = "Can you see what apps are running on my computer?";
        var parsed = JarvisPlanParser.Parse(PlanJson("list_apps", request), request).Actions.Single();
        Check("running-app requests parse without a coding project", parsed.Kind == "list_apps" && parsed.Target == "");
        foreach (var kind in new[] { "open_app", "focus_app", "close_app", "search_web" })
        {
            var action = JarvisPlanParser.Parse(PlanJson(kind, request, new() { ["target"] = "Notepad" }), request).Actions.Single();
            Check(kind + " is a typed desktop action", action.Kind == kind && action.Target == "Notepad");
            RejectJarvis(kind + " requires a target", () => JarvisPlanParser.Parse(PlanJson(kind, request), request));
        }
        RejectJarvis("desktop actions still require a quote from this request", () => JarvisPlanParser.Parse(PlanJson("list_apps", "invented"), request));
        RejectJarvis("desktop actions cannot inject command arguments", () => JarvisPlanParser.Parse(PlanJson("open_app", request,
            new() { ["target"] = "Notepad", ["command"] = "anything" }), request));
        RejectJarvis("browser opening rejects script URLs", () => JarvisPlanParser.Parse(PlanJson("open_url", request,
            new() { ["target"] = "javascript:alert(1)" }), request));
        RejectJarvis("browser opening rejects file URLs", () => JarvisPlanParser.Parse(PlanJson("open_url", request,
            new() { ["target"] = "file:///C:/Windows/notepad.exe" }), request));
        RejectJarvis("folder opening requires an absolute path", () => JarvisPlanParser.Parse(PlanJson("open_path", request,
            new() { ["target"] = "../Desktop" }), request));
        Check("web searches safely encode literal query text", JarvisDesktopPolicy.WebSearchAddress("a & b # c").EndsWith("a%20%26%20b%20%23%20c"));

        var vm = new MainViewModel();
        var fakeDesktop = new JarvisDesktopFixture();
        var runtime = new JarvisRuntime(new JarvisPlanFixture(_ => new("unverified", [parsed])), vm, fakeDesktop);
        var turn = runtime.SubmitAsync(request, new("codex", null, null), CancellationToken.None);
        PumpUntil(() => turn.IsCompleted);
        Check("desktop actions run independently of a coding chat", vm.Chats.Count == 0 && fakeDesktop.Calls == 1);
        Check("desktop answers come from the action receipt", turn.GetAwaiter().GetResult() == "Apps: fixture window");
        vm.ShutdownJarvis();

        // Read-only check against the real Win32 inventory using our own off-screen test window.
        var title = "Jarvis desktop fixture " + Guid.NewGuid().ToString("N");
        var window = new Window { Title = title, Left = 6100, Top = 300, Width = 240, Height = 120, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual };
        window.Show();
        try
        {
            var desktop = new JarvisDesktopActions();
            var listing = desktop.ExecuteAsync(parsed with { Target = title }, CancellationToken.None).GetAwaiter().GetResult();
            Check("running-app inventory sees a real Windows window", listing.Message.Contains(title));
            Check("app inventory describes its visible-window scope", listing.Message.Contains("not background services"));
            try { desktop.ExecuteAsync(new("close_app", "", "", request) { Target = title }, CancellationToken.None).GetAwaiter().GetResult(); }
            catch (InvalidOperationException) { }
            Check("Jarvis cannot terminate its own host via an app-close action", window.IsVisible);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var stopped = desktop.ExecuteAsync(new("open_app", "", "", request) { Target = "Notepad" }, cancelled.Token);
            try { stopped.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
            Check("a cancelled desktop action never launches an app", stopped.IsCanceled);
        }
        finally { window.Close(); }

        var resources = Application.Current.Resources;
        resources["BoolVis"] = new BooleanToVisibilityConverter();
        resources["ShowIf"] = new NonEmptyToVisibilityConverter();
        resources.MergedDictionaries.Clear();
        resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(new Uri("/VibeCode;component/Themes/Dark.xaml", UriKind.Relative)));
        var shellHost = new MainViewModel();
        var shell = (MainWindow)typeof(MainWindow).GetConstructors(Flags).Single(c => c.GetParameters().Length == 3).Invoke([shellHost, null, true]);
        shell.Show();
        Call(shell, "OnOpenJarvis", shell, new RoutedEventArgs(Button.ClickEvent));
        var companion = (JarvisWindow)typeof(MainWindow).GetField("_jarvisWindow", Flags)!.GetValue(shell)!;
        shell.WindowState = WindowState.Minimized;
        Check("minimizing the coding window leaves Jarvis available", companion.IsVisible && companion.Owner is null);
        shellHost.Jarvis.Messages.Add(new("user", "Keep my conversation while changing the theme."));
        Call(shell, "ReloadShellForAppearanceChange", false);
        var replacement = (MainWindow)Application.Current.MainWindow;
        Check("appearance changes preserve the live Jarvis window and conversation", !shell.IsVisible && companion.IsVisible
            && ReferenceEquals(typeof(MainWindow).GetField("_jarvisWindow", Flags)!.GetValue(replacement), companion)
            && shellHost.Jarvis.Messages.Count == 1);
        replacement.Close();
        Check("closing the host leaves no disposed Jarvis window behind", !companion.IsVisible);
    }

    private static void VerifyJarvisVoiceAndWindow()
    {
        // A reply plays while it is still being synthesized, so only its first chunk is waited for. That chunk is the
        // opening sentence on its own; a reply synthesized as one 220-character block kept Jarvis silent for ~5 s.
        string[] Chunks(string reply) => ((IEnumerable<string>)typeof(JarvisSpeechService)
            .GetMethod("SpeechChunks", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [JarvisSpeechService.PrepareSpeechText(reply)])!).ToArray();
        var typical = Chunks("Sure. Your Bridge has three agents running right now. Agent one is still reviewing the recoil changes, " +
            "agent two finished the hand animation fix, and agent three is waiting on your approval to publish. Want me to approve it?");
        Check("a spoken reply starts with its opening sentence alone", typical[0] == "Sure." && typical.Length == 4);
        var opening = Chunks("Agent one is still reviewing the recoil changes, agent two finished the hand animation fix, and agent " +
            "three is waiting on your approval to publish. Want me to approve it?");
        Check("a long opening sentence is cut at a clause, not mid-phrase",
            opening[0].EndsWith("animation fix,", StringComparison.Ordinal) && opening[0].Length <= 100);
        Check("abbreviations and version numbers do not end a spoken sentence",
            Chunks("Use e.g. the v1.2 build. It's ready.").SequenceEqual(["Use e.g. the v1.2 build.", "It's ready."]));
        var spoken = JarvisSpeechService.PrepareSpeechText(string.Join(" ", Enumerable.Repeat(
            "This status update keeps going with plenty more detail, so it has to be spoken in several parts.", 20)));
        var parts = Chunks(spoken);
        Check("speech chunks cover the whole reply and stay within the model's limit",
            string.Join(" ", parts) == spoken && parts.All(part => part.Length <= 220) && parts.Length > 5);

        AppSettings.Current.JarvisVoiceEnabled = false;
        var host = new MainViewModel();
        var microphone = new JarvisMicFixture();
        using var jarvis = new JarvisViewModel(host, new JarvisPlanFixture(_ => new("I heard your request.", [])), microphone: microphone);
        var began = jarvis.ToggleListeningAsync();
        Check("one activation starts recording without a delayed capture", began.IsCompletedSuccessfully && jarvis.IsListening && microphone.Starts == 1);
        Check("recording exposes truthful toggle feedback", jarvis.PresenceText.Contains("Listening") && jarvis.TalkButtonText == "Stop and send");
        var silence = new byte[3200];
        microphone.Frame = MicLevelFrame.FromPcm16(microphone.Token, silence);
        Call(jarvis, "OnAudioTick", null, EventArgs.Empty);
        Check("silence does not fabricate a microphone signal", jarvis.MicrophoneLevel == 0);
        var pcm = new byte[3200];
        for (var i = 0; i < pcm.Length / 2; i++) BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2, 2), (short)(9000 * Math.Sin(i * .17)));
        microphone.Frame = MicLevelFrame.FromPcm16(microphone.Token, pcm);
        typeof(JarvisViewModel).GetField("_lastAudioTick", Flags)!.SetValue(jarvis, Stopwatch.GetTimestamp() - Stopwatch.Frequency / 10);
        Call(jarvis, "OnAudioTick", null, EventArgs.Empty);
        Check("measured PCM drives the ring's audio response", jarvis.MicrophoneLevel > .3);
        var ended = jarvis.ToggleListeningAsync();
        PumpUntil(() => ended.IsCompleted);
        ended.GetAwaiter().GetResult();
        Check("the second activation transcribes and sends once", microphone.Stops == 1 && jarvis.Messages.Count == 2
            && jarvis.Messages[0].Text == "What apps are running?");
        Check("finishing resets live microphone visuals", !jarvis.IsListening && jarvis.MicrophoneLevel == 0 && jarvis.TalkButtonText == "Click to talk");

        microphone.Pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        jarvis.ToggleListeningAsync().GetAwaiter().GetResult();
        var pending = jarvis.ToggleListeningAsync();
        Check("transcription has its own visible state", jarvis.State == "transcribing" && jarvis.PresenceText.Contains("Transcribing"));
        jarvis.ToggleListeningAsync().GetAwaiter().GetResult();
        Check("another activation during transcription cannot submit twice", microphone.Stops == 2 && jarvis.Messages.Count == 2);
        jarvis.Cancel();
        microphone.Pending.SetResult("Stale transcription must not send.");
        PumpUntil(() => pending.IsCompleted);
        pending.GetAwaiter().GetResult();
        Check("cancel discards a late transcription", jarvis.Messages.Count == 2 && jarvis.InputText == "" && !jarvis.IsBusy);
        microphone.Pending = null;
        microphone.OtherOwner = new object();
        Check("another composer's capture disables Jarvis recording", !jarvis.CanListen);
        microphone.OtherOwner = null;

        microphone.NextTranscript = "";
        jarvis.ToggleListeningAsync().GetAwaiter().GetResult();
        jarvis.ToggleListeningAsync().GetAwaiter().GetResult();
        Check("empty speech explains why no message was sent", jarvis.PresenceText.Contains("didn’t catch any speech") && jarvis.Messages.Count == 2);
        microphone.NextTranscript = "What apps are running?";

        var jarvisSettingsOpened = false;
        var appSettingsOpened = false;
        var window = new JarvisWindow(jarvis, () => jarvisSettingsOpened = true, () => appSettingsOpened = true);
        window.Show();
        var ring = (JarvisOrb)window.FindName("PresenceRing");
        ring.BeginAnimation(JarvisOrb.PhaseProperty, null); ring.Phase = .16;
        var waveform = (MicLevelMeter)window.FindName("InputWaveform");
        Check("waveform is bound to Jarvis's own recording owner", ReferenceEquals(waveform.RecordingOwner, jarvis));
        Check("Jarvis is independently available in the taskbar", window.ShowInTaskbar && window.Owner is null);
        var talk = (Button)window.FindName("ListenButton");
        var starts = microphone.Starts;
        talk.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
        Check("pressing without clicking does not require a hold or start capture", microphone.Starts == starts);
        talk.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check("clicking the real button starts recording", jarvis.IsListening && !talk.IsMouseCaptured);
        talk.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseUpEvent });
        ((TextBox)window.FindName("MessageBox")).Focus();
        Check("mouse release and focus changes leave toggled recording running", jarvis.IsListening);
        talk.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => !jarvis.IsBusy);
        Check("the second real button click sends exactly one request", !jarvis.IsListening && jarvis.Messages.Count == 4);
        talk.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Escape)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        Check("Escape cancels a toggle recording and clears its audio", !jarvis.IsListening && jarvis.MicrophoneLevel == 0 && window.IsVisible);
        var review = Path.Combine(Environment.CurrentDirectory, ".impeccable", "review");
        Directory.CreateDirectory(review);
        var menu = ((Button)window.FindName("OptionsButton")).ContextMenu;
        Call(window, "OnOptions", window, new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Check("options use the dark rounded template without a native icon gutter", menu.IsOpen && menu.ActualWidth >= 224
            && menu.Items.OfType<MenuItem>().All(item => item.ActualHeight >= 40));
        var menuBitmap = new RenderTargetBitmap((int)Math.Ceiling(menu.ActualWidth), (int)Math.Ceiling(menu.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        menuBitmap.Render(menu);
        var menuEncoder = new PngBitmapEncoder(); menuEncoder.Frames.Add(BitmapFrame.Create(menuBitmap));
        using (var menuOutput = File.Create(Path.Combine(review, "jarvis-options.png"))) menuEncoder.Save(menuOutput);
        menu.IsOpen = false;
        ((MenuItem)menu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        ((MenuItem)menu.Items[1]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check("the menu opens both Jarvis and general app settings", jarvisSettingsOpened && appSettingsOpened);
        jarvis.ClearConversation();
        CaptureShell(window, 1280, 800, Path.Combine(review, "jarvis-wide.png"));
        var layout = (Grid)window.Content;
        Check("the two halves have equal widths", Math.Abs(layout.ColumnDefinitions[0].ActualWidth - layout.ColumnDefinitions[1].ActualWidth) <= 1);
        Check("the ring occupies a substantial portion of its half", ring.ActualWidth > 480 && ring.ActualHeight > 450);
        jarvis.Messages.Add(new("user", "Can you see what apps are running on my computer?"));
        jarvis.Messages.Add(new("assistant", "I can list your open app windows, switch between them, and open apps, folders, or websites. What would you like to do?"));
        CaptureShell(window, 760, 520, Path.Combine(review, "jarvis-compact.png"));
        Check("compact layout keeps the input and talk button visible", ((FrameworkElement)window.FindName("ListenButton")).ActualWidth > 150
            && ((FrameworkElement)window.FindName("MessageBox")).ActualWidth > 180);

        // Use a synthetic PCM capture to render the same real-audio meter as production, without opening hardware.
        var service = SpeechService.Instance;
        var captureType = typeof(SpeechService).GetNestedType("Capture", Flags)!;
        var capture = Activator.CreateInstance(captureType, nonPublic: true)!;
        var history = new MicLevelHistory(7001);
        for (var i = 0; i < 120; i++)
        {
            var block = new byte[3200];
            for (var s = 0; s < block.Length / 2; s++) BinaryPrimitives.WriteInt16LittleEndian(block.AsSpan(s * 2, 2),
                (short)(Math.Max(0, Math.Sin(i * .3)) * 8000 * Math.Sin(s * .11)));
            history.AppendPcm16(block);
        }
        captureType.GetField("Gen")!.SetValue(capture, 7001);
        captureType.GetField("LevelHistory")!.SetValue(capture, history);
        typeof(SpeechService).GetField("_captureGeneration", Flags)!.SetValue(service, 7001);
        typeof(SpeechService).GetField("_current", Flags)!.SetValue(service, capture);
        Property(service, "RecordingOwner", jarvis);
        Property(service, "State", SpeechState.Recording);
        Property(jarvis, "State", "listening"); Property(jarvis, "IsListening", true); Property(jarvis, "MicrophoneLevel", .6);
        try
        {
            CaptureShell(window, 1280, 800, Path.Combine(review, "jarvis-listening.png"));
            Check("recording makes the real audio timeline visible", waveform.Visibility == Visibility.Visible && waveform.ActualHeight == 38);
            Check("the meter contains actual PCM history", ((MicHistorySnapshot?)typeof(MicLevelMeter).GetField("_history", Flags)!.GetValue(waveform))?.Count > 0);
        }
        finally
        {
            typeof(SpeechService).GetField("_current", Flags)!.SetValue(service, null);
            Property(service, "State", SpeechState.Idle);
            Property(service, "RecordingOwner", null!);
            Property(jarvis, "IsListening", false);
        }
        Property(jarvis, "State", "error"); Property(jarvis, "StatusText", "The microphone is unavailable. Connect a microphone or type your message.");
        CaptureShell(window, 760, 520, Path.Combine(review, "jarvis-error.png"));
        Check("errors remain visible and typing remains available", jarvis.PresenceText.Contains("microphone is unavailable") && ((TextBox)window.FindName("MessageBox")).IsEnabled);
        window.Close();
        Check("closing Jarvis clears its recording state", !jarvis.IsListening && jarvis.MicrophoneLevel == 0);
        host.ShutdownJarvis();
    }

    private sealed class JarvisDesktopFixture : IJarvisDesktopActions
    {
        public int Calls;
        public Task<JarvisActionReceipt> ExecuteAsync(JarvisAction action, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(new JarvisActionReceipt(action.Kind, "", "Apps: fixture window")); }
    }

    private sealed class JarvisMicFixture : IJarvisMicrophone
    {
        public event EventHandler? StateChanged;
        public bool HasDevice => true;
        public bool IsBusy => State != SpeechState.Idle || OtherOwner is not null;
        public bool ReadyWithoutDownload => true;
        public SpeechState State { get; private set; }
        public object? RecordingOwner { get; private set; }
        public object? OtherOwner;
        public string? DownloadProgressText => null;
        public MicLevelFrame? Frame;
        public MicLevelFrame? LevelFrame => Frame;
        public int Token = 17;
        public int Starts, Stops;
        public TaskCompletionSource<string>? Pending;
        public string NextTranscript = "What apps are running?";
        public bool Owns(int token) => token == Token;
        public bool StartRecording(out string? error, out int token, object owner)
        { Starts++; token = ++Token; error = null; RecordingOwner = owner; State = SpeechState.Recording; StateChanged?.Invoke(this, EventArgs.Empty); return true; }
        public async Task<string> StopAndTranscribeAsync(int token)
        {
            Stops++; State = SpeechState.Transcribing; StateChanged?.Invoke(this, EventArgs.Empty);
            var result = await (Pending?.Task ?? Task.FromResult(NextTranscript));
            if (Owns(token)) { State = SpeechState.Idle; RecordingOwner = null; StateChanged?.Invoke(this, EventArgs.Empty); }
            return result;
        }
        public void ForceReset()
        { Token++; RecordingOwner = null; Frame = null; State = SpeechState.Idle; StateChanged?.Invoke(this, EventArgs.Empty); }
    }
}
