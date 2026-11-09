using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VibeCode;
using VibeCode.UI;

internal static partial class Program
{
    private static void VerifyConfirmationAndComposers()
    {
        var resources = Application.Current.Resources;
        resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(
            new Uri("/VibeCode;component/Themes/Dark.xaml", UriKind.Relative)));
        resources["BoolVis"] = new BooleanToVisibilityConverter();
        resources["ShowIf"] = new NonEmptyToVisibilityConverter();
        var vm = new MainViewModel();
        var chat = Chat("normal-button", "claude", false);
        vm.Chats.Add(chat);
        Property(vm, "ActiveChat", chat);
        var shell = (MainWindow)typeof(MainWindow).GetConstructors(Hidden)
            .Single(ctor => ctor.GetParameters().Length == 3).Invoke([vm, null, true]);
        shell.Width = 1400;
        shell.Height = 900;
        _ = new WindowInteropHelper(shell).EnsureHandle();
        var input = (TextBox)shell.FindName("InputBox");
        var artifactDirectory = Path.Combine(Environment.CurrentDirectory, "artifacts", "rewind-verification");
        Directory.CreateDirectory(artifactDirectory);

        VerifyButton(bridge: false);
        VerifyButton(bridge: true);
        VerifyUnavailableButton(bridge: false);
        VerifyUnavailableButton(bridge: true);

        void VerifyButton(bool bridge)
        {
            if (bridge)
            {
                chat = Chat("bridge-button", "glm", true);
                vm.Chats.Add(chat);
                vm.BridgePanes.Add(chat);
                var other = Chat("bridge-neighbor", "glm", true);
                other.Draft = "neighbor draft";
                vm.Chats.Add(other);
                vm.BridgePanes.Add(other);
                Property(vm, "ShowBridge", true);
            }
            var label = bridge ? "bridge button" : "normal button";
            var source = Path.Combine(chat.Cwd, "source.cs");
            File.WriteAllText(source, "original");
            var prompt = Prompt(chat, "Restore this original prompt", source, "agent edit", completed: true);
            var original = new Attachment { Kind = "text", FileName = "original.txt", Text = "original attachment" };
            Property(prompt, "Attachments", new[] { original });
            chat.Items.Add(new TextItem { Text = "Reply that will be removed." });
            if (bridge) Prompt(chat, "Newer prompt", source, "newer edit", completed: true);
            Call(chat, "RefreshUndoStack");
            chat.Status = "idle";
            var staged = new Attachment { Kind = "text", FileName = "draft.txt", Text = "staged attachment" };
            chat.Attachments.Add(staged);
            chat.Draft = "unsent user draft";
            input.Text = bridge ? "normal composer stays" : "unsent user draft";
            Render(shell, Path.Combine(artifactDirectory, bridge ? "bridge-rewind.png" : "chat-rewind.png"));
            var list = bridge
                ? Descendants<ListBox>((DependencyObject)shell.Content).Single(candidate => ReferenceEquals(candidate.DataContext, chat)
                    && candidate.Items.Contains(prompt))
                : (ListBox)shell.FindName("MsgList");
            var button = Descendants<Button>(list).Single(candidate => ReferenceEquals(candidate.DataContext, prompt)
                && AutomationProperties.GetName(candidate) == "Undo this prompt and restore it to the message input");
            Check(label + ": actual arrow is actionable", button.IsEnabled && button.IsHitTestVisible);

            var cancelled = AnswerDialog(yes: false);
            InvokeArrow(button);
            PumpUntil(() => cancelled.IsCompleted);
            var dialog = cancelled.GetAwaiter().GetResult();
            Check(label + ": confirmation shown with default No", dialog.Title == "Confirm rewind" && dialog.DefaultButton == 7);
            Check(label + ": draft replacement disclosed", dialog.Text.Contains("unsent message and staged attachments"));
            Check(label + ": cascade disclosed when needed", bridge
                ? dialog.Text.Contains("1 newer prompt") : dialog.Text.Contains("Go back to this prompt?"));
            Check(label + ": Cancel preserves files, transcript and attachments", !Checkpoint(prompt).WasRolledBack
                && chat.Items.Contains(prompt) && chat.Attachments.Contains(staged)
                && File.ReadAllText(source) == (bridge ? "newer edit" : "agent edit"));
            File.WriteAllText(Path.Combine(artifactDirectory, bridge ? "bridge-confirmation.txt" : "chat-confirmation.txt"), dialog.Text);

            var confirmed = AnswerDialog(yes: true);
            InvokeArrow(button);
            PumpUntil(() => confirmed.IsCompleted && Checkpoint(prompt).WasRolledBack);
            _ = confirmed.GetAwaiter().GetResult();
            Check(label + ": confirmation restores files and removes prompt", File.ReadAllText(source) == "original"
                && !chat.Items.Contains(prompt));
            Check(label + ": original attachments restored", chat.Attachments.Count == 1 && ReferenceEquals(chat.Attachments[0], original));
            Check(label + ": correct composer restored", bridge
                ? chat.Draft == prompt.Text && input.Text == "normal composer stays"
                    && vm.BridgePanes.Single(other => !ReferenceEquals(other, chat)).Draft == "neighbor draft"
                : input.Text == prompt.Text);
        }

        void VerifyUnavailableButton(bool bridge)
        {
            var chat = Chat(bridge ? "bridge-unavailable-button" : "normal-unavailable-button", "codex", bridge);
            vm.Chats.Add(chat);
            if (bridge) vm.BridgePanes.Add(chat);
            else Property(vm, "ActiveChat", chat);
            Property(vm, "ShowBridge", bridge);
            var checkpoint = BudgetFailure(chat);
            var prompt = new UserItem { Text = "The prompt whose snapshot failed", Owner = chat };
            Call(prompt, "AttachRollbackCheckpoint", checkpoint);
            chat.Items.Add(prompt);
            Field(chat, "_activeRollback", checkpoint);
            chat.Status = "running";
            chat.Draft = "preserve this draft";
            input.Text = "preserve main composer";
            Render(shell, Path.Combine(artifactDirectory, bridge ? "bridge-unavailable.png" : "chat-unavailable.png"));
            var list = bridge
                ? Descendants<ListBox>((DependencyObject)shell.Content).Single(candidate => ReferenceEquals(candidate.DataContext, chat)
                    && candidate.Items.Contains(prompt))
                : (ListBox)shell.FindName("MsgList");
            var button = Descendants<Button>(list).Single(candidate => ReferenceEquals(candidate.DataContext, prompt)
                && AutomationProperties.GetName(candidate) == "Undo this prompt and restore it to the message input");
            var label = bridge ? "bridge failed arrow" : "normal failed arrow";
            Check(label + ": blocked checkpoint remains clickable", button.IsEnabled);
            var preservedDraft = chat.Draft;
            var preservedInput = input.Text;
            var answered = AnswerDialog(yes: true);
            // Exercise the XAML Click subscription through the routed input event.
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
            PumpUntil(() => answered.IsCompleted);
            var dialog = answered.GetAwaiter().GetResult();
            Check(label + ": click explains failure before requesting confirmation", dialog.Title == "Couldn't undo prompt"
                && dialog.Text.Contains("256 MB") && dialog.Text.Contains(".vibecode-undoignore"));
            Check(label + ": click leaves running agent and unsent input intact", Session(chat).Interrupts == 0
                && chat.Status == "running" && chat.Items.Contains(prompt) && chat.Draft == preservedDraft
                && input.Text == preservedInput);
            checkpoint.Complete();
            Field(chat, "_activeRollback", null);
            chat.Status = "idle";
        }
    }

    private static void InvokeArrow(Button button)
    {
        // Exercise the actual XAML Click subscription through the button's accessible action.
        // Calling OnUndoPrompt directly would miss a broken binding or disconnected event handler.
        var peer = new ButtonAutomationPeer(button);
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
    }

    private static Task<DialogCapture> AnswerDialog(bool yes)
    {
        var processId = Environment.ProcessId;
        return Task.Run(async () =>
        {
            nint dialog = 0;
            for (var attempt = 0; attempt < 800 && dialog == 0; attempt++)
            {
                EnumWindows((window, _) =>
                {
                    GetWindowThreadProcessId(window, out var ownerProcess);
                    if (ownerProcess != processId || !IsWindowVisible(window)) return true;
                    if (WindowText(window) is "Confirm rewind" or "Couldn't undo prompt") dialog = window;
                    return true;
                }, 0);
                if (dialog == 0) await Task.Delay(10);
            }
            if (dialog == 0) throw new TimeoutException("The button did not show a rewind confirmation.");
            var text = new List<string>();
            EnumChildWindows(dialog, (child, _) => { text.Add(WindowText(child)); return true; }, 0);
            var title = WindowText(dialog);
            var defaultButton = 0;
            // DM_GETDEFID is needed only for the Yes/No confirmation. Bound the cross-thread send so a native
            // dialog cannot hang the test runner while the WPF automation action is on the dispatcher.
            if (title == "Confirm rewind")
            {
                SendMessageTimeout(dialog, 0x0400, 0, 0, 2, 1000, out var defaultId);
                defaultButton = (int)(defaultId & 0xffff);
            }
            var capture = new DialogCapture(title, string.Join("\n", text), defaultButton);
            if (capture.Title == "Confirm rewind")
                PostMessage(dialog, 0x0111, yes ? 6 : 7, 0); // WM_COMMAND, IDYES / IDNO
            else
                PostMessage(dialog, 0x0010, 0, 0); // WM_CLOSE; the native OK button's control ID varies by Windows version
            return capture;
        });
    }

    private static string WindowText(nint window)
    {
        var text = new StringBuilder(4096);
        GetWindowText(window, text, text.Capacity);
        return text.ToString();
    }
    private static void Render(Window shell, string path)
    {
        shell.Measure(new Size(1400, 900));
        shell.Arrange(new Rect(0, 0, 1400, 900));
        shell.UpdateLayout();
        var content = (FrameworkElement)shell.Content;
        content.Measure(new Size(1400, 900));
        content.Arrange(new Rect(0, 0, 1400, 900));
        content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1400, 900, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private sealed record DialogCapture(string Title, string Text, int DefaultButton);
    private delegate bool EnumerateWindow(nint window, nint state);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumerateWindow callback, nint state);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint window, EnumerateWindow callback, nint state);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int capacity);
    [DllImport("user32.dll")] private static extern nint SendMessageTimeout(nint window, uint message, nint wParam,
        nint lParam, uint flags, uint timeout, out nuint result);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
}
