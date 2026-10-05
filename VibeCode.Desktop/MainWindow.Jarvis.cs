using System.Windows;

namespace VibeCode;

public partial class MainWindow
{
    private JarvisWindow? _jarvisWindow;
    internal UI.JarvisViewModel JarvisAssistant => _vm.Jarvis;
    private bool _secondBrainEnabledSnapshot = Services.AppSettings.Current.SecondBrainEnabled;

    private void OnJarvisSettingsRequested(string category) => Dispatcher.BeginInvoke(
        System.Windows.Threading.DispatcherPriority.Background, new Action(() => ShowSettingsDialog(category)));
    private void OnJarvisAppearanceRequested() => Dispatcher.BeginInvoke(
        System.Windows.Threading.DispatcherPriority.Background, new Action(() => App.ApplyAppearanceChange(reopenSettings: false)));

    private void RefreshSecondBrainExtension()
    {
        var enabled = Services.AppSettings.Current.SecondBrainEnabled;
        _vm.RefreshSecondBrainExtensionVisibility();
        if (_secondBrainEnabledSnapshot == enabled) return;
        _secondBrainEnabledSnapshot = enabled;
        Services.AgentMemoryService.Instance.ApplyExtensionState();
        foreach (var chat in _vm.Chats.Concat(_vm.LiveBridgePeers).Distinct()) chat.RefreshSecondBrainState();
    }

    private void OnOpenJarvis(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var host = _isBridgeMonitor ? _primaryWindow : this;
        if (host is null) return;
        if (host._jarvisWindow is { IsVisible: true } visible)
        {
            if (visible.WindowState == WindowState.Minimized) visible.WindowState = WindowState.Normal;
            visible.Activate();
            return;
        }
        void OpenSettings(string? category) =>
            (Application.Current.Windows.OfType<MainWindow>().FirstOrDefault(window => window.IsPrimaryShell) ?? host)
                .ShowSettingsDialog(category);
        var dialog = new JarvisWindow(_vm.Jarvis, () => OpenSettings("Jarvis"), () => OpenSettings(null));
        host._jarvisWindow = dialog;
        dialog.Closed += (_, _) => host._jarvisWindow = null;
        dialog.Show();
    }
}
