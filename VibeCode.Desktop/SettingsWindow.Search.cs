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
            IndexSettings((DependencyObject)pane.Content, index, category, category, "");
        }
        _settingsSearchReady = true;
        UpdateSettingsSearch();
    }

    /// <summary>Everything the index knows is written beside the thing it describes: a page's heading, a group and
    /// a row each carry their own title, explanation and extra search words in the XAML.</summary>
    private void IndexSettings(DependencyObject node, int pane, string category, string path, string keywords)
    {
        switch (node)
        {
            case SettingHeading heading:
                _settingsSearchIndex.Add(new(pane, "Category", category, heading.Description, heading, heading.Keywords));
                return;
            case SettingGroup group:
                // A group is findable in its own right: Notifications, Privacy, Projects, About and MCP servers
                // were categories once, and those names are still what people type.
                keywords = string.Join(' ', keywords, group.Header, group.Keywords);
                if (group.Header.Length > 0)
                {
                    _settingsSearchIndex.Add(new(pane, category, group.Header, group.Description, group, group.Keywords));
                    path = category + " › " + group.Header;
                }
                break;
            case SettingCard card:
                _settingsSearchIndex.Add(new(pane, path, card.Title, card.Description, card,
                    string.Join(' ', keywords, card.Keywords)));
                return;
            // Index labels only, never TextBox/PasswordBox values, account data, or generated list rows.
            case Control control and (ButtonBase or TextBox or PasswordBox or ComboBox):
                var title = BindingOperations.IsDataBound(control, AutomationProperties.NameProperty)
                    ? "" : AutomationProperties.GetName(control);
                if (string.IsNullOrWhiteSpace(title) && control is ContentControl { Content: string content }
                    && !BindingOperations.IsDataBound(control, ContentControl.ContentProperty)) title = content;
                if (!string.IsNullOrWhiteSpace(title) && title.Any(char.IsLetter))
                    _settingsSearchIndex.Add(new(pane, path, title, "", control, keywords));
                break;
        }
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
            IndexSettings(child, pane, category, path, keywords);
    }

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

    /// <summary>The ✕ inside any search field (the template is shared by the settings, hidden-project and weather
    /// searches). Focus goes back to the field, because clearing is nearly always followed by typing.</summary>
    private void OnSearchFieldClear(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.TemplatedParent is not TextBox field) return;
        field.Clear();
        if (IsActive) field.Focus();
    }

    private void OnOpenSettingsSearchResult(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SettingsSearchResult entry }) OpenSettingsSearchResult(entry);
    }

    internal void OpenSettingsSearchResult(SettingsSearchResult entry)
    {
        SettingsSearchBox.Clear();
        Rail.SelectedIndex = entry.PaneIndex;
        Reveal(entry.Target, focus: true);
    }

    private FrameworkElement? _pendingReveal;

    /// <summary>Brings one row or section to the top of its page, opens it if it is a dropdown, and marks it for a
    /// moment so the eye lands on it. Used by search results and by <see cref="SelectCategory"/>, which can be
    /// called before the window has been shown - that request waits for the first layout.</summary>
    private void Reveal(FrameworkElement target, bool focus)
    {
        if (!IsLoaded)
        {
            if (_pendingReveal is null) Loaded += (_, _) => { if (_pendingReveal is { } pending) Reveal(pending, focus: false); };
            _pendingReveal = target;
            return;
        }
        _pendingReveal = null;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!IsLoaded) return;
            ScrollViewer? pane = null;
            for (DependencyObject? node = target; node is not null; node = LogicalTreeHelper.GetParent(node))
            {
                // A dropdown row the target is, or sits inside, has to be open before anything is measured.
                if (node is SettingCard { IsExpandable: true } dropdown) dropdown.IsExpanded = true;
                if (node is ScrollViewer scroller && Panes.Contains(scroller)) { pane = scroller; break; }
            }
            UpdateLayout();
            if (pane is not null && target.IsDescendantOf(pane))
            {
                // A group brings its own caption; a row is shown with enough above it to keep its group's caption
                // (or the row before it) in sight, so it is never the first thing under the window's edge.
                var top = target is SettingHeading ? 0
                    : target.TransformToAncestor(pane).Transform(new Point()).Y + pane.VerticalOffset
                      - (target is SettingCard ? 44 : 16);
                pane.ScrollToVerticalOffset(Math.Max(0, top));
            }
            // Navigation only. Never invoke a button or toggle the setting as a side effect of searching.
            switch (target)
            {
                case SettingCard card:
                    card.Flash();
                    if (focus && IsActive) card.FocusPrimaryControl();
                    break;
                case SettingGroup group:
                    group.Flash();
                    break;
                default:
                    if (!focus || !IsActive) break;
                    if (target.Focusable) target.Focus();
                    else target.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
                    break;
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
