using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using VibeCode.Services;
using VibeCode.UI;

namespace VibeCode;

// Appearance › Workflow wall: the wall's celebrations, sounds, guild colours and town improvements. An open wall
// picks every change up on its next frame or snapshot.
public partial class SettingsWindow
{
    private sealed record WallChoice(string Key, string Label);

    private static readonly WallChoice[] CelebrationChoices =
        [new(WorkflowCelebrations.Off, "Off"), new(WorkflowCelebrations.Occasional, "Occasional"), new(WorkflowCelebrations.Every, "Every finish")];

    private void InitializeWallExtras()
    {
        var settings = AppSettings.Current;
        WallCelebrationsPicker.ItemsSource = CelebrationChoices;
        WallCelebrationsPicker.SelectedValue = WorkflowCelebrations.Normalize(settings.WorkflowCelebrations);
        WallSoundsToggle.IsChecked = settings.WorkflowWallSounds;
        GuildColoursToggle.IsChecked = settings.WorkflowGuildColours;
        TownImprovementsToggle.IsChecked = settings.WorkflowTownImprovements;
    }

    private void OnWallCelebrationsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || sender is not ComboBox { SelectedValue: string mode }) return;
        AppSettings.Current.WorkflowCelebrations = mode;
        AppSettings.Current.Save();
    }

    private void OnWallSoundsChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready || sender is not ToggleButton toggle) return;
        AppSettings.Current.WorkflowWallSounds = toggle.IsChecked == true;
        AppSettings.Current.Save();
    }

    private void OnGuildColoursChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready || sender is not ToggleButton toggle) return;
        AppSettings.Current.WorkflowGuildColours = toggle.IsChecked == true;
        AppSettings.Current.Save();
    }

    private void OnTownImprovementsChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready || sender is not ToggleButton toggle) return;
        AppSettings.Current.WorkflowTownImprovements = toggle.IsChecked == true;
        AppSettings.Current.Save();
    }
}
