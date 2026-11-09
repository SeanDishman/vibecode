using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using VibeCode;
using VibeCode.Services;
using VibeCode.Terminal;
using VibeCode.UI;

internal static partial class Program
{
    private static void VerifyTerminalChatEntry()
    {
        var (vm, active) = BridgeHost("terminal-menu", "codex", "Active chat");
        var (_, clicked) = BridgeHost("terminal-clicked", "codex", "Clicked chat");
        vm.Chats.Add(clicked); Call(vm, "Track", clicked);
        clicked.Items.Add(new TextItem { Text = "Existing selected chat history" });
        var originalSession = Steerable(clicked);
        var originalId = clicked.SessionId;
        var shell = (MainWindow)typeof(MainWindow).GetConstructors(Flags).Single(c => c.GetParameters().Length == 3).Invoke([vm, null, true]);
        shell.Left = 6100; shell.Top = 250; shell.WindowStartupLocation = WindowStartupLocation.Manual;
        try
        {
            shell.Show(); Settle();
            var row = Descendants<Button>((DependencyObject)shell.Content).First(b => ReferenceEquals(b.DataContext, clicked) && b.ContextMenu is not null);
            var menu = row.ContextMenu;
            menu.PlacementTarget = row; Settle();
            var entry = menu.Items.OfType<MenuItem>().Single(i => i.Tag as string == "OpenInTerminal");
            void Refresh() => menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            AppSettings.Current.TerminalEnabled = false; Refresh();
            Check("terminal menu action is hidden when the switch is off", entry.Visibility == Visibility.Collapsed);
            var blocked = false;
            try { vm.OpenChatInTerminal(clicked); } catch (InvalidOperationException) { blocked = true; }
            Check("disabled terminal cannot bypass the menu gate", blocked && vm.TerminalTeamRoster(clicked).Count == 0);
            AppSettings.Current.TerminalEnabled = true; Refresh();
            Check("right-click offers terminal for the clicked chat without selecting it", entry.Visibility == Visibility.Visible && entry.IsEnabled
                && ReferenceEquals(menu.DataContext, clicked) && ReferenceEquals(vm.ActiveChat, active));
            menu.Measure(new Size(320, 600)); menu.Arrange(new Rect(menu.DesiredSize)); menu.UpdateLayout();
            RenderMetadataPopup(menu, "terminal-chat-menu.png");

            vm.BridgePanes.Add(clicked); Refresh();
            Check("classic Bridge menu action is disabled with an explanation", !entry.IsEnabled && entry.ToolTip.ToString()!.Contains("Bridge"));
            blocked = false;
            try { vm.OpenChatInTerminal(clicked); } catch (InvalidOperationException) { blocked = true; }
            Check("classic Bridge is rejected by the launch path", blocked && !clicked.IsAgentTeamMember);
            vm.BridgePanes.Clear();
            Property(clicked, "IsAdvancedBridgeHost", true); Refresh();
            blocked = false;
            try { vm.OpenChatInTerminal(clicked); } catch (InvalidOperationException) { blocked = true; }
            Check("Advanced Bridge is blocked in both menu and launch", !entry.IsEnabled && blocked);
            Property(clicked, "IsAdvancedBridgeHost", false);

            var saved = new SavedBridgeState { HostSessionId = clicked.SessionId!, Provider = clicked.Provider,
                Peers = [new SavedBridgePane { SessionId = "saved-terminal-bridge-peer", Provider = "codex" }] };
            AppSettings.Current.SavedBridges.Add(saved); Refresh();
            Check("dormant saved Bridges are also unavailable", !entry.IsEnabled && vm.TerminalUnavailableReason(clicked) is not null);
            AppSettings.Current.SavedBridges.Remove(saved);

            var host = vm.OpenChatInTerminal(clicked);
            using var terminal = new TerminalSession(vm, host, Dispatcher.CurrentDispatcher);
            Check("opening the clicked chat preserves identity, history and the existing provider session", ReferenceEquals(host, clicked)
                && host.SessionId == originalId && ReferenceEquals(Steerable(host), originalSession)
                && terminal.Snapshot().Rows!.Any(r => r.Text.Contains("Existing selected chat history")) && ReferenceEquals(vm.ActiveChat, active));
            Check("reopening a terminal team reuses its roster", ReferenceEquals(vm.OpenChatInTerminal(clicked), host)
                && vm.TerminalTeamRoster(host).Count == 1);
            Refresh(); Check("a pure agent team remains supported", entry.IsEnabled);
            Check("opening an existing idle chat reuses its agent for new prompts", terminal.HandleInput("Continue from the selected chat history").Accepted
                && vm.TerminalTeamRoster(host).Count == 1 && vm.TerminalTeamRoster(host).Single() == clicked);
            PumpUntil(() => originalSession.Sent.Count == 1, 30_000);
            Check("reused terminal agent keeps its provider session and history", ReferenceEquals(Steerable(clicked), originalSession)
                && clicked.SessionId == originalId && originalSession.Sent[0].Contains("Continue from the selected chat history")
                && clicked.Items.OfType<TextItem>().Any(t => t.Text == "Existing selected chat history"));
            AppSettings.Current.TerminalEnabled = false; Refresh();
            Check("turning the switch off hides launch without closing existing agents", entry.Visibility == Visibility.Collapsed
                && vm.TerminalTeamRoster(host).Count == 1 && host.Status != "closed");
        }
        finally { shell.Close(); AppSettings.Current.TerminalEnabled = false; }
    }
}
