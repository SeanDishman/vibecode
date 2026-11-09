using System.Windows;
using VibeCode.Services;
using VibeCode.UI;

namespace VibeCode;

/// <summary>The status bar's "Continue in a fresh chat" handoff and the Bridge ⋮ menu's Bridge prompt.</summary>
public partial class MainWindow
{
    /// <summary>Providers that cannot compact get a new chat in the same project, provider, account and model, with an
    /// editable first prompt built from the full chat's checkpoint waiting in its composer. Nothing is sent for the user.</summary>
    private void OnContinueInFreshChat(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not ChatViewModel chat) return;
        var dialog = new HandoffDraftWindow(chat.FreshChatHandoffDraft(), chat.ProviderDisplay) { Owner = this };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.Draft)) return;
        var fresh = _vm.NewChat(chat.Cwd, provider: chat.Provider, accountId: chat.AccountId, configure: c =>
        {
            c.Model = chat.ChosenModel;
            c.Effort = chat.Effort;
        });
        fresh.Draft = dialog.Draft;
        if (ReferenceEquals(ComposerChat(), fresh)) ShowChatInComposer(fresh);
        InputBox?.Focus();
    }

    /// <summary>The Bridge ⋮ menu's "Bridge prompt". The menu closes first, so the dialog is not drawn behind it.</summary>
    private void OnOpenBridgePrompt(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        BridgeMoreToggle.IsChecked = false;
        BridgeMorePopup.IsOpen = false;
        if (BridgeRosterForWindow.FirstOrDefault() is not { } host) return;
        if (_vm.BridgeUserPromptKeyFor(host) is not { } key)
        {
            MessageBox.Show(this, "The Bridge prompt can be set once this Bridge's first agent has replied.", "Bridge prompt",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new BridgePromptWindow(_vm.LoadBridgeUserPrompt(key).Text) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        try { _vm.SaveBridgeUserPrompt(key, BridgeUserPrompt.From(dialog.Prompt)); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.IO.InvalidDataException)
        {
            MessageBox.Show(this, "The Bridge prompt was not saved: " + ex.Message, "Bridge prompt", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
