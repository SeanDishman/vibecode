using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using VibeCode.Services;
using VibeCode.UI;

namespace VibeCode;

/// <summary>One account in the Discord pings list: ticked means its resets ping the channel.</summary>
public sealed class DiscordPingRow(string key, string label, string detail, bool isOn) : Observable
{
    public string Key { get; } = key;
    public string Label { get; } = label;
    public string Detail { get; } = detail;

    private bool _isOn = isOn;
    public bool IsOn { get => _isOn; set => Set(ref _isOn, value); }
}

/// <summary>The account manager's Discord reset pings page: the webhook, a test ping, and which accounts ping.</summary>
public partial class MainWindow
{
    private readonly ObservableCollection<DiscordPingRow> _discordRows = new();

    // Null-safe: the account manager's refresh can run while InitializeComponent is still building the page.
    private bool IsDiscordPingsOpen => DiscordPingsPanel?.Visibility == Visibility.Visible;

    private void OnToggleDiscordPings(object sender, RoutedEventArgs e) => ShowDiscordPings(!IsDiscordPingsOpen);

    /// <summary>Swap the account list for the Discord page, or back. The page takes the list's place rather than
    /// opening over it, so the hidden list's controls are out of the tab order too.</summary>
    private void ShowDiscordPings(bool show)
    {
        DiscordPingsPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        var accounts = show ? Visibility.Collapsed : Visibility.Visible;
        AccountManagerFilters.Visibility = accounts;
        AccountManagerListCard.Visibility = accounts;
        AccountManagerFooter.Visibility = accounts;
        DiscordPingsButtonLabel.Text = show ? "Back to accounts" : "Discord pings";
        if (!show) return;
        ShowDiscordProblem("");
        RefreshDiscordRows();
        Dispatcher.BeginInvoke(() =>
        {
            if (IsDiscordPingsOpen && !DiscordResetPings.Instance.HasWebhook) DiscordWebhookBox.Focus();
        }, DispatcherPriority.Input);
    }

    /// <summary>Re-read which accounts exist. An unchanged roster is updated in place, so a background usage
    /// refresh landing while the page is open neither scrolls the list nor undoes a click.</summary>
    private void RefreshDiscordRows()
    {
        if (DiscordAccountList is null) return;
        DiscordAccountList.ItemsSource ??= _discordRows;
        var pings = DiscordResetPings.Instance;
        var accounts = _vm.ReadResetPingSnapshot().Accounts;
        if (_discordRows.Select(row => row.Key).SequenceEqual(accounts.Select(account => account.Key)))
            foreach (var row in _discordRows) row.IsOn = pings.Includes(row.Key);
        else
        {
            _discordRows.Clear();
            foreach (var account in accounts)
                _discordRows.Add(new DiscordPingRow(account.Key, account.Label, account.Detail, pings.Includes(account.Key)));
        }
        UpdateDiscordAllState();
    }

    private void UpdateDiscordAllState()
    {
        var total = _discordRows.Count;
        var on = _discordRows.Count(row => row.IsOn);
        DiscordAllAccounts.IsEnabled = total > 0;
        DiscordAllAccounts.IsChecked = total > 0 && on == total;
        DiscordAccountCount.Text = total == 0 ? "No accounts with a usage limit yet"
            : on == total ? $"All {total} ping"
            : on == 0 ? "None ping"
            : $"{on} of {total} ping";
    }

    /// <summary>Unticked shows whenever any account is left out, so clicking it ticks every one; ticked unticks all.</summary>
    private void OnDiscordAllAccounts(object sender, RoutedEventArgs e)
    {
        var on = DiscordAllAccounts.IsChecked == true;
        DiscordResetPings.Instance.SetAllIncluded(_discordRows.Select(row => row.Key), on);
        foreach (var row in _discordRows) row.IsOn = on;
        UpdateDiscordAllState();
    }

    private void OnDiscordAccountToggled(object sender, RoutedEventArgs e)
    {
        // Click is raised after the toggle, so the binding has already written IsOn.
        if ((sender as FrameworkElement)?.DataContext is not DiscordPingRow row) return;
        DiscordResetPings.Instance.SetIncluded(row.Key, row.IsOn);
        UpdateDiscordAllState();
    }

    private bool SaveDiscordWebhookFromBox()
    {
        if (!DiscordResetPings.TryParseWebhook(DiscordWebhookBox.Text, out var url, out var problem))
        {
            ShowDiscordProblem(problem);
            return false;
        }
        if (!DiscordResetPings.Instance.SaveWebhook(url))
        {
            ShowDiscordProblem("Windows couldn't seal the webhook to your account, so it wasn't saved.");
            return false;
        }
        DiscordWebhookBox.Clear();   // only the masked form stays on screen
        ShowDiscordProblem("");
        return true;
    }

    private void OnSaveDiscordWebhook(object sender, RoutedEventArgs e) => SaveDiscordWebhookFromBox();

    private void OnDiscordWebhookKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        SaveDiscordWebhookFromBox();
        e.Handled = true;
    }

    /// <summary>A URL still in the box is saved first, so paste-then-test works in one click.</summary>
    private async void OnTestDiscordWebhook(object sender, RoutedEventArgs e)
    {
        if (DiscordWebhookBox.Text.Trim().Length > 0 && !SaveDiscordWebhookFromBox()) return;
        if (!DiscordResetPings.Instance.HasWebhook)
        {
            ShowDiscordProblem("Paste a webhook URL first.");
            return;
        }
        DiscordTestButton.IsEnabled = false;
        try { await DiscordResetPings.Instance.SendTestAsync(); }
        finally { DiscordTestButton.IsEnabled = true; }
    }

    private void OnRemoveDiscordWebhook(object sender, RoutedEventArgs e)
    {
        DiscordResetPings.Instance.RemoveWebhook();
        ShowDiscordProblem("");
    }

    private void ShowDiscordProblem(string text)
    {
        DiscordWebhookProblem.Text = text;
        DiscordWebhookProblem.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
