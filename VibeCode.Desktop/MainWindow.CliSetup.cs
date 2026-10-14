using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Media;
using VibeCode.Services;

namespace VibeCode;

public partial class MainWindow
{
    private bool _cliSetupOpen;

    private void UpdateCliInstallAction()
    {
        if (AccountManagerAddLabel is null || AccountManagerFooterHint is null) return;
        var provider = SelectedAccountManagerProvider;
        if (CliInstallationService.Supports(provider) && !CliInstallationService.IsReady(provider))
        {
            AccountManagerAddLabel.Text = $"Install {CliInstallationService.DisplayName(provider)} CLI";
            AccountManagerFooterHint.Text = "Install the official CLI and its required tools, then sign in. No manual file setup needed.";
        }
        AutomationProperties.SetName(AccountManagerAddButton, AccountManagerAddLabel.Text);
    }

    private async Task<bool> EnsureCliInstalledAsync(string provider)
    {
        if (!CliInstallationService.Supports(provider) || CliInstallationService.IsReady(provider)) return true;
        if (_cliSetupOpen) return false;
        _cliSetupOpen = true;
        using var lifetime = new CancellationTokenSource();
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        attempt.CancelAfter(TimeSpan.FromMinutes(20));
        var status = new TextBlock
        {
            Text = $"Installing {CliInstallationService.DisplayName(provider)} CLI…",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            MinHeight = 44,
            Foreground = (Brush)FindResource("Text"),
        };
        var bar = new ProgressBar { IsIndeterminate = true, Height = 3, Margin = new Thickness(0, 14, 0, 18) };
        var close = new Button { Content = "Cancel", Padding = new Thickness(14, 7, 14, 7), IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        close.SetResourceReference(StyleProperty, "GhostButton");
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(status); panel.Children.Add(bar); panel.Children.Add(close);
        var dialog = CreateThemedDialog($"Set up {CliInstallationService.DisplayName(provider)}", panel, 460);
        var installed = false;
        Task? work = null;
        close.Click += (_, _) => dialog.Close();
        dialog.Closed += (_, _) => lifetime.Cancel();
        dialog.Loaded += (_, _) => work = Install();
        async Task Install()
        {
            try
            {
                await CliInstallationService.InstallAsync(provider, new Progress<string>(text => status.Text = text), attempt.Token);
                installed = true;
                dialog.Close();
            }
            catch (OperationCanceledException)
            {
                if (!lifetime.IsCancellationRequested) status.Text = "Installation timed out. Close setup and choose Install CLI to try again.";
            }
            catch (Exception ex) { status.Text = ex.Message + "\nClose setup and choose Install CLI to try again."; }
            finally { bar.Visibility = Visibility.Collapsed; close.Content = "Close"; }
        }
        try
        {
            dialog.ShowDialog();
            if (work is not null) await work;
            return installed;
        }
        finally { _cliSetupOpen = false; UpdateAccountManagerProvider(); }
    }
}
