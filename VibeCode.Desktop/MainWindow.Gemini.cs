using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VibeCode.Services;

namespace VibeCode;

public partial class MainWindow
{
    private async void OnManageGeminiAccount(object sender, RoutedEventArgs e)
    {
        CloseAccountManager();
        await ManageGeminiAccountAsync();
    }

    private void OnRefreshGeminiUsage(object sender, RoutedEventArgs e) => GeminiUsageService.Instance.Refresh(force: true);

    private async Task ManageGeminiAccountAsync(bool signIn = false)
    {
        if (!await EnsureCliInstalledAsync("gemini")) return;
        if (!signIn && _vm.IsGeminiSignedIn)
        {
            SetNewChatProvider("gemini", persist: true);
            GeminiUsageService.Instance.Refresh();
            return;
        }
        using var cancellation = new CancellationTokenSource();
        var status = new TextBlock
        {
            Text = GeminiRuntime.IsInstalled
                ? "Opening Google sign-in…\nChoose the Google account with your AI Pro or Ultra subscription."
                : "Installing Gemini CLI…\nGoogle sign-in will open when installation finishes.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
        };
        status.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        var cancel = new Button { Content = "Cancel", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0), Padding = new Thickness(14, 6, 14, 6) };
        cancel.SetResourceReference(StyleProperty, "GhostButton");
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(status); panel.Children.Add(cancel);
        var dialog = new Window
        {
            Title = "Connect Gemini",
            Owner = this,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Content = panel,
            Background = (Brush)FindResource("Bg1"),
            ShowInTaskbar = false,
        };
        cancel.Click += (_, _) => dialog.Close();
        dialog.Closed += (_, _) => cancellation.Cancel();
        var connected = false;
        dialog.Loaded += async (_, _) =>
        {
            try
            {
                await GeminiRuntime.SignInAsync(cancellation.Token);
                connected = true;
                dialog.Close();
            }
            catch (OperationCanceledException)
            {
                if (!cancellation.IsCancellationRequested) { status.Text = "Google sign-in timed out. Close this window and try again."; cancel.Content = "Close"; }
            }
            catch (Exception ex) { status.Text = ex.Message; cancel.Content = "Close"; }
        };
        dialog.ShowDialog();
        if (connected)
        {
            _vm.RefreshGeminiAccount();
            SetNewChatProvider("gemini", persist: true);
            GeminiUsageService.Instance.Refresh(force: true);
        }
        await Task.CompletedTask;
    }
}
