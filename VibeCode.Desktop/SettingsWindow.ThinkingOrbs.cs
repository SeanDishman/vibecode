using System.Windows;
using System.Windows.Controls;
using VibeCode.Services;

namespace VibeCode;

public partial class SettingsWindow
{
    private bool _refreshingThinkingOrbs;

    private void InitializeThinkingOrbPicker()
    {
        ThinkingOrbPicker.ItemsSource = ThinkingOrbStyles.All;
        RefreshThinkingOrbSelection();
    }

    private void RefreshThinkingOrbSelection()
    {
        _refreshingThinkingOrbs = true;
        try { ThinkingOrbPicker.SelectedItem = ThinkingOrbStyles.Resolve(AppSettings.Current.ThinkingOrbStyle); }
        finally { _refreshingThinkingOrbs = false; }
    }

    private void OnThinkingOrbChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _refreshingThinkingOrbs || ThinkingOrbPicker.SelectedItem is not ThinkingOrbOption option) return;
        var settings = AppSettings.Current;
        var previous = settings.ThinkingOrbStyle;
        if (previous == option.Id) return;
        settings.ThinkingOrbStyle = option.Id;
        var error = settings.TrySave();
        if (error is null) return;
        settings.ThinkingOrbStyle = previous;
        App.RefreshThinkingOrbStyle();
        RefreshThinkingOrbSelection();
        MessageBox.Show(this, "The thinking orb selection could not be saved. Try again.\n\n" + error.Message,
            "Thinking orbs", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
