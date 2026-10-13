using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using VibeCode;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    private static void VerifyChatLockBehavior()
    {
        var (vm, team) = Team("chat-lock-pin", 2);
        vm.BridgePanes.Clear();
        foreach (var chat in team) { chat.BridgeLabel = ""; chat.Status = "idle"; }
        typeof(MainViewModel).GetField("_sessionRestored", Flags)!.SetValue(vm, true);
        var first = team[0]; var second = team[1];
        Property(vm, "ActiveChat", second);
        var settingsPath = Path.Combine(Environment.GetEnvironmentVariable("VIBECODE_DATA_DIR")!, "settings.json");

        Application.Current.Resources.MergedDictionaries.Clear();
        Application.Current.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(new Uri("/VibeCode;component/Themes/Dark.xaml", UriKind.Relative)));
        var shell = (MainWindow)typeof(MainWindow).GetConstructors(Flags).Single(c => c.GetParameters().Length == 3).Invoke([vm, null, true]);
        try
        {
            var menu = (ContextMenu)shell.Resources["ChatMenu"];
            menu.PlacementTarget = new Button { DataContext = first };
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            var items = menu.Items.OfType<MenuItem>().ToArray();
            foreach (var pinned in new[] { false, true })
            {
                first.Pinned = pinned;
                Call(first, "SetChatLocked", false);
                items[0].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, items[0]));
                Check("menu lock preserves pin state " + pinned, first.IsLocked && first.Pinned == pinned && ReferenceEquals(vm.ActiveChat, second));
                items[0].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, items[0]));
                Check("menu unlock preserves pin state " + pinned, !first.IsLocked && first.Pinned == pinned && ReferenceEquals(vm.ActiveChat, second));
                var saved = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(settingsPath))!.OpenChats.Single(c => c.SessionId == first.SessionId);
                Check("unlock saves pin and lock independently " + pinned, saved.Pinned == pinned && saved.Metadata?.IsLocked == false);
            }
            vm.ToggleChatLock(first);
            items[1].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, items[1]));
            Check("pin menu changes only pinning", !first.Pinned && first.IsLocked);
            items[1].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, items[1]));
            Check("unpin then repin preserves the lock", first.Pinned && first.IsLocked);
            Check("ordinary locked chats keep close and delete protection", !first.CanCloseChat && !first.CanDeleteChat && !items[^1].IsEnabled);
            var closeUpdates = 0;
            first.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ChatViewModel.CanCloseChat)) closeUpdates++; };
            first.BridgeLabel = "Codex 1";
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Check("joining a Bridge enables close immediately without enabling Delete", first.CanCloseChat && !first.CanDeleteChat
                && !items[^1].IsEnabled && closeUpdates > 0 && first.CloseChatToolTip == "Close chat");
            first.IsBridgeHost = true;
            first.BridgeLabel = "";
            Check("a saved Bridge host can close while locked", first.CanCloseChat && !first.CanDeleteChat);
            CaptureShell(shell, 1100, 760, "locked-bridge-close.png");
            var close = Descendants<Button>((DependencyObject)shell.Content).Single(b => ReferenceEquals(b.DataContext, first)
                && b.Content?.ToString() == "\uE711");
            Check("the actual sidebar close button remains available on a locked Bridge", close.Visibility == Visibility.Visible && close.IsEnabled);
            first.IsBridgeHost = false;
            Check("leaving the Bridge restores ordinary lock protection", !first.CanCloseChat && !first.CanDeleteChat && first.Pinned);
        }
        finally { shell.Close(); }

        var blank = new ChatViewModel(first.Cwd, provider: "codex") { Pinned = true };
        Chats.Add(blank); vm.Chats.Add(blank); Call(vm, "Track", blank);
        vm.ToggleChatLock(blank);
        vm.ToggleChatLock(blank);
        var disk = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(settingsPath))!;
        Check("unlocking a pinned chat without a provider session preserves it on disk", blank.Pinned && !blank.IsLocked
            && disk.OpenChats.Any(c => c.SessionId is null && c.Pinned && c.Metadata?.IsLocked == false));
        var restore = new MainViewModel();
        restore.Chats.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is null) return;
            foreach (ChatViewModel restored in e.NewItems)
            {
                Chats.Add(restored);
                restored.Status = "closed"; // Cancel provider startup; this check only restores saved UI state.
            }
        };
        var previousOpenChats = AppSettings.Current.OpenChats;
        AppSettings.Current.OpenChats = [disk.OpenChats.Single(c => c.SessionId is null)];
        try
        {
            restore.RestoreSession();
            Check("an unlocked pinned chat with no session ID is restored on restart", restore.Chats.Count == 1
                && restore.Chats[0].Pinned && !restore.Chats[0].IsLocked);
        }
        finally { AppSettings.Current.OpenChats = previousOpenChats; }

        VerifyLockedBridgeClose();
    }

    private static void VerifyLockedBridgeClose()
    {
        foreach (var departingIndex in new[] { 0, 1 })
        {
            var (vm, team) = Team("locked-two-pane-" + departingIndex, 2);
            var departing = team[departingIndex]; var survivor = team[1 - departingIndex];
            departing.Pinned = survivor.Pinned = true;
            vm.ToggleChatLock(departing); vm.ToggleChatLock(survivor);
            var departingSession = Session(departing);
            var survivingSession = Session(survivor);
            vm.RemoveBridgePane(departing);
            Check("a locked pane can close in a two-chat Bridge: " + departingIndex,
                departing.Status == "closed" && departingSession.HasExited && !vm.BridgePanes.Contains(departing));
            Check("the locked survivor remains open and pinned as an ordinary chat: " + departingIndex,
                vm.Chats.Contains(survivor) && !survivingSession.HasExited && survivor.IsLocked && survivor.Pinned && vm.BridgePanes.Count == 0);
            Check("closing a locked Bridge pane preserves its saved lock and transcript: " + departingIndex,
                departing.IsLocked && !AppSettings.Current.DeletedSessions.Contains(departing.SessionId!)
                && AppSettings.Current.ChatMetadata[ChatMetadata.Key(departing.Provider, departing.SessionId!)].IsLocked);
        }

        var (hostVm, peers) = Team("locked-bridge-host", 3);
        var host = peers[0]; host.IsBridgeHost = true; host.Pinned = true;
        foreach (var peer in peers) hostVm.ToggleChatLock(peer);
        Check("a locked Bridge host exposes close while retaining delete protection", host.CanCloseChat && !host.CanDeleteChat);
        hostVm.DeleteChat(host);
        Check("locking still blocks deliberate Bridge deletion", hostVm.Chats.Contains(host)
            && !AppSettings.Current.DeletedSessions.Contains(host.SessionId!));
        hostVm.CloseChat(host);
        Check("closing the locked Bridge host stops its whole roster", peers.All(p => p.Status == "closed") && hostVm.BridgePanes.Count == 0 && !hostVm.Chats.Contains(host));
        var saved = AppSettings.Current.FindSavedBridge(host.SessionId, host.Provider);
        Check("closing a locked Bridge retains its resumable roster, host pin and locks", saved is { HostPinned: true }
            && saved.Peers.Count == 2 && peers.All(p => AppSettings.Current.ChatMetadata[ChatMetadata.Key(p.Provider, p.SessionId!)].IsLocked));
    }
}
