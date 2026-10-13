using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using NAudio.Wave;
using VibeCode;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    private static void VerifySpeechComposerFocus()
    {
        var resources = Application.Current.Resources;
        resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(new Uri("/VibeCode;component/Themes/Dark.xaml", UriKind.Relative)));
        resources["BoolVis"] = new BooleanToVisibilityConverter();
        resources["ShowIf"] = new NonEmptyToVisibilityConverter();
        var service = SpeechService.Instance;
        var groq = GroqSpeechService.Instance;
        var groqEnabled = groq.Enabled;
        var (vm, team) = Team("speech-focus", 5);
        foreach (var chat in team) chat.Status = "idle";
        vm.ActiveChat = team[0];
        var shell = (MainWindow)typeof(MainWindow).GetConstructors(Flags).Single(c => c.GetParameters().Length == 3)
            .Invoke([vm, null, true]);
        // Exercise the real controls off-screen without starting account/weather polling or real providers.
        shell.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), shell,
            typeof(MainWindow).GetMethod("OnAppearanceReloadLoaded", Flags)!);
        shell.WindowState = WindowState.Normal;
        shell.WindowStartupLocation = WindowStartupLocation.Manual;
        shell.Left = shell.Top = -32000;
        shell.Width = 1600; shell.Height = 900;
        shell.ShowInTaskbar = false;
        shell.ShowActivated = false;

        try
        {
            groq.Enabled = true;
            Check("speech focus fixture has no cloud credential", !groq.HasKey);
            shell.Show();
            var cases = new[]
            {
                (Name: "main empty recording", Layout: "main", Bytes: 0),
                (Name: "main transcription failure", Layout: "main", Bytes: 32000),
                (Name: "bridge empty recording", Layout: "bridge", Bytes: 0),
                (Name: "bridge transcription failure", Layout: "bridge", Bytes: 32000),
                (Name: "shared terminal empty recording", Layout: "shared", Bytes: 0),
            };
            for (var i = 0; i < cases.Length; i++)
            {
                var scenario = cases[i];
                var chat = team[i];
                vm.ActiveChat = chat;
                Property(vm, "ShowBridge", scenario.Layout != "main");
                vm.SetBridgeTerminalMode(scenario.Layout == "shared");
                if (scenario.Layout == "shared") vm.SelectBridgeTerminal(chat);
                else vm.SelectBridgePane(chat);
                Call(shell, "WireActiveChat");
                shell.UpdateLayout();
                Settle();
                var surface = scenario.Layout == "main" ? (DependencyObject)shell
                    : (DependencyObject)shell.FindName(scenario.Layout == "shared" ? "BridgeSharedPanel" : "BridgePaneList");
                var input = scenario.Layout == "main" ? (TextBox)shell.FindName("InputBox")
                    : Descendants<TextBox>(surface).Single(t => t.Name == "PaneInput" && ReferenceEquals(t.DataContext, chat));
                var mic = scenario.Layout == "main" ? (Button)shell.FindName("MicButton")
                    : Descendants<Button>(surface).Single(b => AutomationProperties.GetName(b) == "Dictate" && ReferenceEquals(b.DataContext, chat));
                var draft = "Send the existing draft after " + scenario.Name;
                input.Text = draft;
                input.Select(5, 8);

                SeedSpeechCapture(shell, mic, scenario.Layout == "main" ? null : chat, scenario.Bytes);
                Check(scenario.Name + ": mic can receive keyboard focus", mic.Focus() && mic.IsKeyboardFocused);
                mic.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => service.State == SpeechState.Idle && (int)typeof(MainWindow).GetField("_micToken", Flags)!.GetValue(shell)! == 0);
                Check(scenario.Name + ": focus returns to the same composer", input.IsKeyboardFocused && !mic.IsKeyboardFocused);
                Check(scenario.Name + ": draft and selection survive", input.Text == draft && input.SelectionStart == 5 && input.SelectionLength == 8);
                var before = Session(chat).Sent.Count;
                var token = service.CurrentToken;
                var enter = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(input), 0, Key.Enter)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                ((UIElement)Keyboard.FocusedElement).RaiseEvent(enter);
                PumpUntil(() => Session(chat).Sent.Count == before + 1);
                Check(scenario.Name + ": Enter sends the existing draft once", enter.Handled && input.Text == ""
                    && Session(chat).Sent[^1].Contains(draft));
                Check(scenario.Name + ": Enter does not restart dictation", service.State == SpeechState.Idle && service.CurrentToken == token);
                Check(scenario.Name + ": other chats receive nothing", team.Where(other => !ReferenceEquals(other, chat))
                    .All(other => !Session(other).Sent.Any(text => text.Contains(draft))));
            }
        }
        finally
        {
            service.ForceReset();
            groq.Enabled = groqEnabled;
            typeof(MainWindow).GetField("_closingForAppearanceReload", Flags)!.SetValue(shell, true);
            shell.Close();
        }
    }

    private static void SeedSpeechCapture(MainWindow shell, Button mic, ChatViewModel? owner, int sampleBytes)
    {
        var service = SpeechService.Instance;
        service.ForceReset();
        var token = service.CurrentToken + 1;
        var captureType = typeof(SpeechService).GetNestedType("Capture", Flags)!;
        var capture = Activator.CreateInstance(captureType, nonPublic: true)!;
        var buffer = new MemoryStream();
        var writer = new WaveFileWriter(buffer, new WaveFormat(16000, 16, 1));
        writer.Write(new byte[sampleBytes], 0, sampleBytes);
        captureType.GetField("Gen")!.SetValue(capture, token);
        captureType.GetField("Buffer")!.SetValue(capture, buffer);
        captureType.GetField("Writer")!.SetValue(capture, writer);
        captureType.GetField("Tcs")!.SetValue(capture, new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously));
        typeof(SpeechService).GetField("_captureGeneration", Flags)!.SetValue(service, token);
        typeof(SpeechService).GetField("_current", Flags)!.SetValue(service, capture);
        Property(service, "RecordingOwner", (object?)owner ?? mic);
        Property(service, "State", SpeechState.Recording);
        typeof(MainWindow).GetField("_micToken", Flags)!.SetValue(shell, token);
        typeof(MainWindow).GetField("_micOwner", Flags)!.SetValue(shell,
            (object?)owner ?? typeof(MainWindow).GetField("MainComposerOwner", Flags)!.GetValue(null));
        typeof(MainWindow).GetField("_micButton", Flags)!.SetValue(shell, mic);
        typeof(MainWindow).GetField("_micActive", Flags)!.SetValue(shell, true);
    }
}
