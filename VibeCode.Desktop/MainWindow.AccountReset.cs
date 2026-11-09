using System.Windows;
using System.Windows.Controls;
using VibeCode.Services;

namespace VibeCode;

public partial class MainWindow
{
    private void OnCodexAccountMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        _ctxCodexAccount = (menu.PlacementTarget as FrameworkElement)?.DataContext as CodexAccountInfo;
        if (menu.Items.OfType<MenuItem>().FirstOrDefault(item => item.Tag as string == "ResetCodexUsage") is not { } reset)
            return;
        var account = _ctxCodexAccount;
        var busy = account is not null && CodexAccountService.Instance.IsResettingUsage(account.Id);
        reset.IsEnabled = account?.CanResetUsage == true && !busy;
        reset.ToolTip = busy ? "A reset for this account is already in progress." : account?.ResetUsageToolTip;
    }

    private async void OnResetCodexAccount(object sender, RoutedEventArgs e)
    {
        // Capture the clicked row before awaiting. Refreshes and other menus can change the current selection.
        if (Ctx<CodexAccountInfo>(sender) is not { } account) return;
        var service = CodexAccountService.Instance;
        if (!account.CanResetUsage || service.IsResettingUsage(account.Id)) return;
        SetAccountResetStatus($"{account.Label}: resetting usage…", "Muted");
        if (sender is MenuItem item) item.IsEnabled = false;
        try
        {
            var result = await service.ResetUsageAsync(account.Id);
            _vm.ApplyCodexAccounts(service.List());
            ScheduleAccountManagerRefresh();
            SetAccountResetStatus($"{account.Label}: {result.Message}", result.Succeeded ? "Green" : "Amber");
        }
        catch (Exception ex)
        {
            SetAccountResetStatus($"{account.Label}: {ex.Message}", "Amber");
        }
    }

    private void SetAccountResetStatus(string message, string brush)
    {
        AccountManagerActionStatus.Text = message;
        AccountManagerActionStatus.SetResourceReference(TextBlock.ForegroundProperty, brush);
        AccountManagerActionStatus.Visibility = Visibility.Visible;
    }
}
