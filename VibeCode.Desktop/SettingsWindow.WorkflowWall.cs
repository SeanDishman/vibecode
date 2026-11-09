using System.Windows.Controls;
using VibeCode.Services;
using VibeCode.UI;

namespace VibeCode;

public partial class SettingsWindow
{
    private bool _refreshingWorkflowWallStyle;

    private void InitializeWorkflowWallStylePicker()
    {
        WorkflowWallStylePicker.ItemsSource = WorkflowViews.All;
        RefreshWorkflowWallStyle(AppSettings.Current.WorkflowWallView);
        InitializeWallExtras();
        // The wall's own View menu and this picker are one choice: either one moves the other.
        WorkflowViews.Chosen += RefreshWorkflowWallStyle;
        Closed += (_, _) => WorkflowViews.Chosen -= RefreshWorkflowWallStyle;
    }

    private void RefreshWorkflowWallStyle(string key)
    {
        _refreshingWorkflowWallStyle = true;
        try { WorkflowWallStylePicker.SelectedItem = WorkflowViews.For(key); }
        finally { _refreshingWorkflowWallStyle = false; }
    }

    private void OnWorkflowWallStyleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _refreshingWorkflowWallStyle || WorkflowWallStylePicker.SelectedItem is not WorkflowViewChoice choice) return;
        WorkflowViews.Choose(choice.Key);
    }
}
