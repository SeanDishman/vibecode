using System.Windows;
using VibeCode.Services;

namespace VibeCode;

public partial class SettingsWindow
{
    private void OnTerminalEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        AppSettings.Current.TerminalEnabled = TerminalEnabledToggle.IsChecked == true;
        AppSettings.Current.Save();
    }
}
