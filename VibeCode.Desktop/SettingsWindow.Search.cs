using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using VibeCode.UI;

namespace VibeCode;

internal sealed record SettingsSearchResult(int PaneIndex, string Category, string Title, string Description,
    FrameworkElement Target, string Keywords);

public partial class SettingsWindow
{
    private readonly List<SettingsSearchResult> _settingsSearchIndex = new();
    private bool _settingsSearchReady;

    private void InitializeSettingsSearch()
    {
        _settingsSearchIndex.Clear();
        var panes = Panes;
        for (var index = 0; index < panes.Length; index++)
        {
            var pane = (ScrollViewer)panes[index];
            var category = AutomationProperties.GetName((ListBoxItem)Rail.Items[index]);
            var root = (FrameworkElement)pane.Content;
            var description = root is Panel panel
                ? panel.Children.OfType<TextBlock>().Skip(1).FirstOrDefault()?.Text ?? ""
                : "";
            _settingsSearchIndex.Add(new(index, category, category, description, root, CategoryKeywords(index)));
            IndexSettings(root, index, category, "");
        }
        _settingsSearchIndex.Add(new(1, "Appearance", "Background images",
            "Choose backgrounds, shuffle images and adjust their visibility.", BackgroundSection,
            "wallpaper gif animation opacity random background"));
        _settingsSearchReady = true;
        UpdateSettingsSearch();
    }

    private void IndexSettings(DependencyObject node, int pane, string category, string group)
    {
        if (node is SettingGroup settingGroup) group = settingGroup.Header + " " + settingGroup.Description;
        if (node is SettingCard card)
        {
            _settingsSearchIndex.Add(new(pane, category, card.Title, card.Description, card, group));
            return;
        }
        if (node is ExtensionCard extension)
        {
            _settingsSearchIndex.Add(new(pane, category, extension.Title, extension.Description, extension,
                group + " " + ExtensionKeywords(extension.Title)));
            return;
        }
        // Index labels only, never TextBox/PasswordBox values, account data, or generated list rows.
        if (node is Control control && control is ButtonBase or TextBox or PasswordBox or ComboBox)
        {
            var title = BindingOperations.IsDataBound(control, AutomationProperties.NameProperty)
                ? "" : AutomationProperties.GetName(control);
            if (string.IsNullOrWhiteSpace(title) && control is ContentControl { Content: string content }
                && !BindingOperations.IsDataBound(control, ContentControl.ContentProperty)) title = content;
            if (!string.IsNullOrWhiteSpace(title) && title.Any(char.IsLetter))
                _settingsSearchIndex.Add(new(pane, category, title, "", control, group));
        }
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
            IndexSettings(child, pane, category, group);
    }

    private static string CategoryKeywords(int index) => index switch
    {
        1 => "theme terminal CLI look background",
        3 => "bridge agents peer messages mailbox unread read answered swarm workers supervision",
        4 => "tokens cost history statistics electricity energy water telemetry charts",
        5 => "projects chats accounts hidden restore",
        6 => "model context protocol servers tools stdio http",
        9 => "version build application",
        _ => "",
    };

    private static string ExtensionKeywords(string title) => title.ToLowerInvariant() switch
    {
        "spotify" => "music playback client id connect account premium",
        "weather" => "forecast city location temperature celsius fahrenheit",
        var label when label.Contains("dictation") || label.Contains("speech") => "speech voice microphone groq whisper api key offline",
        "phone" => "mobile remote pairing chats bridge",
        _ => "",
    };

    internal IReadOnlyList<SettingsSearchResult> FindSettings(string query)
    {
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) return Array.Empty<SettingsSearchResult>();
        return _settingsSearchIndex.Where(entry => SearchTargetAvailable(entry))
            .Where(entry => terms.All(term => (entry.Title + " " + entry.Description + " " + entry.Category + " " + entry.Keywords)
                .Contains(term, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(entry => entry.Title.Equals(query.Trim(), StringComparison.OrdinalIgnoreCase) ? 0
                : entry.Title.StartsWith(query.Trim(), StringComparison.OrdinalIgnoreCase) ? 1
                : terms.All(term => entry.Title.Contains(term, StringComparison.OrdinalIgnoreCase)) ? 2 : 3)
            .ThenBy(entry => entry.PaneIndex).ThenBy(entry => entry.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private bool SearchTargetAvailable(SettingsSearchResult entry)
    {
        // Ignore the pane's collapsed state (inactive categories), but not a mode-hidden setting inside it.
        for (DependencyObject? current = entry.Target; current is not null && !ReferenceEquals(current, Panes[entry.PaneIndex]);
             current = LogicalTreeHelper.GetParent(current))
            if (current is UIElement { Visibility: Visibility.Collapsed or Visibility.Hidden }) return false;
        return true;
    }

    private void OnSettingsSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_settingsSearchReady) UpdateSettingsSearch();
    }

    private void UpdateSettingsSearch()
    {
        var active = !string.IsNullOrWhiteSpace(SettingsSearchBox.Text);
        SettingsSearchHint.Visibility = SettingsSearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearSettingsSearch.Visibility = SettingsSearchBox.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        SettingsSearchPane.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        var panes = Panes;
        for (var i = 0; i < panes.Length; i++)
            panes[i].Visibility = !active && i == Rail.SelectedIndex ? Visibility.Visible : Visibility.Collapsed;
        var results = active ? FindSettings(SettingsSearchBox.Text) : Array.Empty<SettingsSearchResult>();
        SettingsSearchResults.ItemsSource = results;
        SettingsSearchResults.SelectedIndex = results.Count > 0 ? 0 : -1;
        SettingsSearchCount.Text = results.Count == 1 ? "1 setting found" : $"{results.Count} settings found";
        SettingsSearchEmpty.Visibility = active && results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnClearSettingsSearch(object sender, RoutedEventArgs e)
    {
        SettingsSearchBox.Clear();
        if (IsActive) SettingsSearchBox.Focus();
    }

    private void OnOpenSettingsSearchResult(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SettingsSearchResult entry }) OpenSettingsSearchResult(entry);
    }

    internal void OpenSettingsSearchResult(SettingsSearchResult entry)
    {
        SettingsSearchBox.Clear();
        Rail.SelectedIndex = entry.PaneIndex;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!IsLoaded) return;
            UpdateLayout();
            entry.Target.BringIntoView();
            // Navigation only. Never invoke a button or toggle the setting as a side effect of searching.
            if (IsActive)
            {
                if (entry.Target.Focusable) entry.Target.Focus();
                else entry.Target.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            }
        }));
    }

    private void OnSettingsSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SettingsSearchBox.Focus(); SettingsSearchBox.SelectAll(); e.Handled = true;
        }
        else if (e.Key == Key.Escape && SettingsSearchBox.Text.Length > 0)
        {
            SettingsSearchBox.Clear(); SettingsSearchBox.Focus(); e.Handled = true;
        }
        else if (e.Key == Key.Down && SettingsSearchBox.IsKeyboardFocusWithin && SettingsSearchResults.Items.Count > 0)
        {
            SettingsSearchResults.Focus();
            if (SettingsSearchResults.ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem item) item.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && (SettingsSearchBox.IsKeyboardFocusWithin || SettingsSearchResults.IsKeyboardFocusWithin)
                 && SettingsSearchResults.SelectedItem is SettingsSearchResult entry)
        {
            OpenSettingsSearchResult(entry); e.Handled = true;
        }
    }
}
