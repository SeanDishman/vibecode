using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VibeCode;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    private static void VerifyChatConfirmations()
    {
        foreach (var theme in new[] { "Dark", "Borderless", "Cli" })
        {
            var resources = Application.Current.Resources.MergedDictionaries;
            resources.Clear();
            resources.Add((ResourceDictionary)Application.LoadComponent(
                new Uri($"/VibeCode;component/Themes/{(theme == "Cli" ? "Cli" : "Dark")}.xaml", UriKind.Relative)));
            if (theme == "Borderless") resources.Add((ResourceDictionary)Application.LoadComponent(
                new Uri("/VibeCode;component/Themes/Borderless.xaml", UriKind.Relative)));
            var (vm, team) = Team("chat-confirmation-" + theme, 2);
            var chat = team[0];
            chat.Status = "idle";
            vm.RenameChat(chat, "Automatic Claude Model Recovery");
            var shell = (MainWindow)typeof(MainWindow).GetConstructors(Flags)
                .Single(c => c.GetParameters().Length == 3).Invoke([vm, null, true]);
            new WindowInteropHelper(shell).EnsureHandle();
            try
            {
                foreach (var deleting in new[] { true, false })
                {
                    chat.Status = deleting ? "idle" : "running";
                    var dialog = (Window)Call(shell, "CreateChatConfirmation", chat, deleting)!;
                    Check(theme + " confirmation uses owned borderless chrome", dialog.WindowStyle == WindowStyle.None
                        && dialog.AllowsTransparency && !dialog.ShowInTaskbar && ReferenceEquals(dialog.Owner, shell));
                    var result = InspectDialog(dialog, () =>
                    {
                        var cancel = DialogButton(dialog, "CancelChatRemoval");
                        var confirm = DialogButton(dialog, "ConfirmChatRemoval");
                        Check(theme + " cancellation is the default action", cancel.IsDefault && cancel.IsCancel && !confirm.IsDefault);
                        Check(theme + " keyboard focus has a visible cue", cancel.FocusVisualStyle is not null && confirm.FocusVisualStyle is not null);
                        Check(theme + " action names the consequence", confirm.Content?.ToString() == (deleting ? "Delete chat" : "Close chat"));
                        var texts = Descendants<TextBlock>(dialog).Select(t => t.Text).ToArray();
                        Check(theme + " identifies the chat", texts.Any(t => t.Contains(chat.Title)));
                        Check(theme + " preserves removal explanation", deleting
                            ? texts.Any(t => t.Contains("provider's own transcript file"))
                            : texts.Any(t => t.Contains("will be stopped")));
                        SaveConfirmation(dialog, theme.ToLowerInvariant() + (deleting ? "-delete.png" : "-close-running.png"));
                        cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    });
                    Check(theme + " Cancel declines confirmation", result != true && !Session(chat).HasExited);
                }

                var escaped = (Window)Call(shell, "CreateChatConfirmation", chat, true)!;
                Check(theme + " Escape cancels", InspectDialog(escaped, () => escaped.RaiseEvent(new KeyEventArgs(
                    Keyboard.PrimaryDevice, PresentationSource.FromVisual(escaped), 0, Key.Escape)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent })) != true);
                var closed = (Window)Call(shell, "CreateChatConfirmation", chat, true)!;
                Check(theme + " titlebar X cancels", InspectDialog(closed, () =>
                    DialogButton(closed, "DialogClose").RaiseEvent(new RoutedEventArgs(Button.ClickEvent))) != true);

                if (theme != "Borderless") continue;
                vm.RenameChat(chat, string.Join(" ", Enumerable.Repeat("A very long project and chat title", 22)));
                var longTitle = (Window)Call(shell, "CreateChatConfirmation", chat, true)!;
                InspectDialog(longTitle, () =>
                {
                    var action = DialogButton(longTitle, "ConfirmChatRemoval");
                    Check("long names keep the actions inside the dialog", action.IsVisible &&
                        action.TranslatePoint(new Point(0, action.ActualHeight), longTitle).Y <= longTitle.ActualHeight);
                    Check("long names wrap inside a bounded scroll area", Descendants<ScrollViewer>(longTitle)
                        .Any(s => s.ScrollableHeight > 0 && s.ActualHeight <= s.MaxHeight));
                    SaveConfirmation(longTitle, "borderless-long-title.png");
                    DialogButton(longTitle, "CancelChatRemoval").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                });
                vm.RenameChat(chat, "Automatic Claude Model Recovery");
                VerifyChatConfirmationActions(shell, vm, chat);
            }
            finally { shell.Close(); }
        }
    }

    private static void VerifyChatConfirmationActions(MainWindow shell, MainViewModel vm, ChatViewModel chat)
    {
        var sender = new Button { DataContext = chat };
        // Exercise the actual menu handler against fixture sessions and isolated application data.
        RespondToNextChatDialog(shell, false);
        Call(shell, "OnDeleteChatMenu", sender, new RoutedEventArgs());
        Check("cancelling Delete keeps the chat and session", vm.Chats.Contains(chat) && !Session(chat).HasExited
            && !AppSettings.Current.DeletedSessions.Contains(chat.SessionId!));

        RespondToNextChatDialog(shell, false);
        Check("Close uses the same themed cancel flow", Call(shell, "ConfirmCloseChat", chat) is false);
        RespondToNextChatDialog(shell, true);
        Check("explicit Close confirms without deleting history", Call(shell, "ConfirmCloseChat", chat) is true
            && !AppSettings.Current.DeletedSessions.Contains(chat.SessionId!));

        vm.ToggleChatLock(chat);
        Call(shell, "OnDeleteChatMenu", sender, new RoutedEventArgs());
        Check("locked chats cannot reach Delete confirmation", vm.Chats.Contains(chat)
            && !Application.Current.Windows.OfType<Window>().Any(w => w.Owner == shell));
        vm.ToggleChatLock(chat);
        RespondToNextChatDialog(shell, true);
        Call(shell, "OnDeleteChatMenu", sender, new RoutedEventArgs());
        Check("explicit Delete removes the selected fixture chat", !vm.Chats.Contains(chat)
            && Session(chat).HasExited && AppSettings.Current.DeletedSessions.Contains(chat.SessionId!));
    }

    private static void RespondToNextChatDialog(MainWindow shell, bool confirm) =>
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            var dialog = Application.Current.Windows.OfType<Window>().Single(w => w.Owner == shell);
            DialogButton(dialog, confirm ? "ConfirmChatRemoval" : "CancelChatRemoval")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }));

    private static bool? InspectDialog(Window dialog, Action inspect)
    {
        dialog.WindowStartupLocation = WindowStartupLocation.Manual;
        dialog.Left = -32000; dialog.Top = -32000; dialog.ShowActivated = false;
        Exception? error = null;
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            try { inspect(); }
            catch (Exception ex) { error = ex; dialog.Close(); }
        }));
        var result = dialog.ShowDialog();
        if (error is not null) throw error;
        return result;
    }

    private static Button DialogButton(Window dialog, string name) => Descendants<Button>(dialog).Single(b => b.Name == name);

    private static void SaveConfirmation(Window dialog, string name)
    {
        dialog.UpdateLayout();
        var root = (FrameworkElement)dialog.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(dialog.ActualWidth), (int)Math.Ceiling(dialog.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(_root, name)); encoder.Save(output);
    }
}
