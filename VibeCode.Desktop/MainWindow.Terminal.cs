using System.Windows;
using System.Windows.Controls;
using VibeCode.Services;
using VibeCode.Terminal;
using VibeCode.UI;

namespace VibeCode;

public partial class MainWindow
{
    private TerminalWindowCollection? _terminalWindows;
    private WindowState _beforeTerminal;
    private bool _terminalOwnerClosed;

    private void OnChatMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        var item = menu.Items.OfType<MenuItem>().FirstOrDefault(i => i.Tag as string == "OpenInTerminal");
        if (item is null) return;
        var chat = (menu.PlacementTarget as FrameworkElement)?.DataContext as ChatViewModel ?? menu.DataContext as ChatViewModel;
        item.Visibility = AppSettings.Current.TerminalEnabled ? Visibility.Visible : Visibility.Collapsed;
        var reason = chat is null ? "Choose an open chat." : _vm.TerminalUnavailableReason(chat);
        item.IsEnabled = reason is null;
        item.ToolTip = reason ?? "Open this chat in Vibe Code Terminal.";
    }

    private async void OnOpenChatInTerminal(object sender, RoutedEventArgs e)
    {
        if (Ctx<ChatViewModel>(sender) is not { } chat) return;
        try
        {
            await OpenTerminalAsync(chat);
            MinimizeForTerminal();
        }
        catch (Exception ex)
        {
            // Owner shutdown cancels pending launches; there is no window left to own an error dialog.
            if (_terminalOwnerClosed || App.IsExiting) return;
            CrashLog.Write(ex, "Terminal.Launch");
            MessageBox.Show(this, ex.Message, "Could not open Terminal", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    internal async Task OpenTerminalAsync(ChatViewModel chat)
    {
        // Recheck on invocation: a menu can remain open while the setting or chat's roster changes.
        var host = _vm.OpenChatInTerminal(chat);
        if (_terminalWindows is null)
        {
            _terminalWindows = new TerminalWindowCollection(host => new TerminalSession(_vm, host, Dispatcher));
            _terminalWindows.DesktopRequested += session => RestoreFromTerminal(session, showTeam: true);
            _terminalWindows.Ended += session =>
            {
                // Closing one console must not pull focus from the other consoles still in use.
                if (_terminalWindows.Count == 0) RestoreFromTerminal(session, showTeam: true);
            };
            Closed += OnTerminalOwnerClosed;
        }
        if (WindowState != WindowState.Minimized) _beforeTerminal = WindowState;
        await _terminalWindows.OpenAsync(host);
    }

    internal void MinimizeForTerminal()
    {
        if (_terminalWindows?.Count > 0) WindowState = WindowState.Minimized;
    }

    private void RestoreFromTerminal(TerminalSession session, bool showTeam)
    {
        if (App.IsExiting) return;
        if (showTeam && _vm.TerminalTeamRoster(session.Host).Count > 0) OpenChatForWindow(session.Host);
        Show();
        WindowState = _beforeTerminal;
        Activate();
        Focus();
    }

    private void OnTerminalOwnerClosed(object? sender, EventArgs e)
    {
        _terminalOwnerClosed = true;
        Closed -= OnTerminalOwnerClosed;
        _terminalWindows?.Dispose();
    }
}
