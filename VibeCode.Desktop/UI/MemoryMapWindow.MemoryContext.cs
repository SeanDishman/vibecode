using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using VibeCode.Services;

namespace VibeCode.UI;

/// <summary>One pinned card in the Second Brain's rail. Edits save when the box loses focus.</summary>
public sealed class PinnedCardItem(string slot, string text) : INotifyPropertyChanged
{
    private string _text = text;
    public string Slot { get; } = slot;
    public string Title => PinnedMemory.Title(Slot) + (PinnedMemory.IsProjectSlot(Slot) ? " · this project" : " · every project");
    public string Text { get => _text; set { _text = value; PropertyChanged?.Invoke(this, new(nameof(Text))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>One "To review" suggestion; <see cref="Text"/> is what Approve saves, so the user can edit it first.</summary>
public sealed class MemorySuggestionItem(MemorySuggestion suggestion)
{
    public MemorySuggestion Suggestion { get; } = suggestion;
    public string Text { get; set; } = suggestion.Text;
    public string Heading => Suggestion.Kind switch
    {
        "digest" => "SESSION DIGEST",
        "slot" => "UPDATE " + PinnedMemory.Title(Suggestion.Slot ?? "").ToUpperInvariant(),
        _ => "MEMORY",
    };
    public string Origin => $"{Suggestion.Source} · {Path.GetFileName(Suggestion.Project)} · {Suggestion.CreatedAt.LocalDateTime:g}";
}

public sealed class CliMemoryItem(CliMemoryFile file)
{
    public CliMemoryFile File { get; } = file;
    public string Label => $"{(File.Provider == "claude" ? "Claude" : "Codex")} · {File.Name}";
    public string Detail => $"{File.Project} · {File.Modified.LocalDateTime:g} · {File.Bytes:N0} bytes";
}

/// <summary>
/// The Second Brain's memory rail: pinned cards (I29), the background review's "To review" list (I35) and the CLIs'
/// own memory files that VibeCode keeps per project (I30). Bound through properties on the window, so the rail works
/// whether or not the memory engine is online.
/// </summary>
public partial class MemoryMapWindow
{
    private string _memoryTab = "pinned";
    private string? _pinnedProject;
    private string _pinnedStatus = "";
    private CliMemoryItem? _selectedCliMemory;
    private string _cliMemoryText = "";
    private string _cliMemoryStatus = "";

    public List<PinnedCardItem> PinnedCards { get; private set; } = [];
    public List<MemorySuggestionItem> Suggestions { get; private set; } = [];
    public List<CliMemoryItem> CliMemoryFiles { get; private set; } = [];

    public bool IsPinnedTab => _memoryTab == "pinned";
    public bool IsReviewTab => _memoryTab == "review";
    public bool IsCliMemoryTab => _memoryTab == "cli";
    public Visibility PinnedVisibility => IsPinnedTab ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ReviewVisibility => IsReviewTab ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CliMemoryVisibility => IsCliMemoryTab ? Visibility.Visible : Visibility.Collapsed;

    public string ReviewTabTitle => Suggestions.Count == 0 ? "To review" : $"To review ({Suggestions.Count})";
    public string PinnedProjectText => _pinnedProject is null
        ? "No project yet: the project cards appear once a chat has a folder."
        : "Project cards apply to " + Path.GetFileName(_pinnedProject) + ".";
    public string PinnedStatus => _pinnedStatus;
    public string ReviewHint => !AppSettings.Current.BackgroundMemoryReview
        ? "Background memory review is off. Turn it on in Settings › Extensions; it uses your subscription usage. Nothing here is saved until you approve it."
        : Suggestions.Count == 0 ? "Nothing to review. Idle, closed and compacted chats are reviewed in the background."
        : "Edit a suggestion if needed, then approve it into memory or reject it.";
    public string CliMemoryHint => CliMemoryFiles.Count == 0
        ? "No CLI memory files yet. Claude Code's auto memory and Codex memories land here once a chat writes one."
        : "Claude's memory is kept per project, Codex's is shared by every account. Files untouched for the retention period are deleted.";

    public CliMemoryItem? SelectedCliMemory
    {
        get => _selectedCliMemory;
        set
        {
            _selectedCliMemory = value;
            _cliMemoryText = value is not null && CliNativeMemory.IsOwnedFile(value.File.Path) ? SafeRead(value.File.Path) : "";
            _cliMemoryStatus = value?.Detail ?? "";
            Raise();
            Raise(nameof(CliMemoryText));
            Raise(nameof(CliMemoryStatus));
            Raise(nameof(HasCliMemorySelection));
        }
    }
    public string CliMemoryText { get => _cliMemoryText; set => _cliMemoryText = value; }
    public string CliMemoryStatus => _cliMemoryStatus;
    public bool HasCliMemorySelection => _selectedCliMemory is not null;

    private void InitializeMemoryContext()
    {
        _pinnedProject = (Owner?.DataContext as MainViewModel)?.ActiveChat?.Cwd
                         ?? AppSettings.Current.RecentDirectories.OrderByDescending(d => d.LastUsed).FirstOrDefault()?.Cwd;
        if (string.IsNullOrWhiteSpace(_pinnedProject) || !Directory.Exists(_pinnedProject)) _pinnedProject = null;
        ReloadPinnedCards();
        ReloadSuggestions();
        ReloadCliMemory();
        PinnedMemory.Changed += OnPinnedMemoryChanged;
        MemorySuggestionInbox.Changed += OnSuggestionsChanged;
        Closed += (_, _) =>
        {
            PinnedMemory.Changed -= OnPinnedMemoryChanged;
            MemorySuggestionInbox.Changed -= OnSuggestionsChanged;
        };
    }

    // The review runs on a worker thread; the rail redraws on the dispatcher.
    private void OnPinnedMemoryChanged() => Dispatcher.BeginInvoke(new Action(ReloadPinnedCards));
    private void OnSuggestionsChanged() => Dispatcher.BeginInvoke(new Action(ReloadSuggestions));

    private void ReloadPinnedCards()
    {
        PinnedCards = PinnedMemory.Slots
            .Where(slot => !PinnedMemory.IsProjectSlot(slot) || _pinnedProject is not null)
            .Select(slot => new PinnedCardItem(slot, PinnedMemory.Get(slot, _pinnedProject)))
            .ToList();
        Raise(nameof(PinnedCards));
        Raise(nameof(PinnedProjectText));
    }

    private void ReloadSuggestions()
    {
        Suggestions = MemorySuggestionInbox.Load().Select(s => new MemorySuggestionItem(s)).ToList();
        Raise(nameof(Suggestions));
        Raise(nameof(ReviewTabTitle));
        Raise(nameof(ReviewHint));
    }

    private void ReloadCliMemory()
    {
        try { CliMemoryFiles = CliNativeMemory.List().Select(file => new CliMemoryItem(file)).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { CliMemoryFiles = []; }
        Raise(nameof(CliMemoryFiles));
        Raise(nameof(CliMemoryHint));
        SelectedCliMemory = null;
    }

    private void OnMemoryTab(object sender, RoutedEventArgs e)
    {
        _memoryTab = (sender as FrameworkElement)?.Tag as string ?? "pinned";
        if (IsCliMemoryTab) ReloadCliMemory();
        foreach (var name in new[] { nameof(IsPinnedTab), nameof(IsReviewTab), nameof(IsCliMemoryTab), nameof(PinnedVisibility),
                     nameof(ReviewVisibility), nameof(CliMemoryVisibility) })
            Raise(name);
    }

    private void OnPinnedCardLostFocus(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PinnedCardItem card) return;
        if (card.Text.Trim() == PinnedMemory.Get(card.Slot, _pinnedProject).Trim()) return;
        try
        {
            PinnedMemory.Set(card.Slot, _pinnedProject, card.Text, DateTimeOffset.UtcNow);
            _pinnedStatus = $"Saved {PinnedMemory.Title(card.Slot).ToLowerInvariant()}. New chats get it on their first turn.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            _pinnedStatus = "Not saved: " + ex.Message;
        }
        Raise(nameof(PinnedStatus));
    }

    private async void OnApproveSuggestion(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MemorySuggestionItem item || string.IsNullOrWhiteSpace(item.Text)) return;
        var suggestion = item.Suggestion;
        try
        {
            if (suggestion.Kind == "slot" && suggestion.Slot is { } slot)
                PinnedMemory.Set(slot, suggestion.Project, item.Text, DateTimeOffset.UtcNow);
            else
                await Service.RememberApprovedAsync(suggestion.Project, suggestion.Provider,
                    suggestion.Kind == "digest" ? $"Session digest ({suggestion.Source}):\n{item.Text}" : item.Text);
            MemorySuggestionInbox.Take(suggestion.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException
                                       or InvalidOperationException or HttpRequestException)
        {
            MessageBox.Show(this, "Couldn't save that suggestion: " + ex.Message, "Second Brain", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnRejectSuggestion(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MemorySuggestionItem item) MemorySuggestionInbox.Take(item.Suggestion.Id);
    }

    private void OnSaveCliMemory(object sender, RoutedEventArgs e)
    {
        if (_selectedCliMemory is not { } item || !CliNativeMemory.IsOwnedFile(item.File.Path)) return;
        try
        {
            File.WriteAllText(item.File.Path, _cliMemoryText);
            _cliMemoryStatus = "Saved. The CLI reads it at the start of its next session.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _cliMemoryStatus = "Not saved: " + ex.Message; }
        Raise(nameof(CliMemoryStatus));
    }

    private void OnDeleteCliMemory(object sender, RoutedEventArgs e)
    {
        if (_selectedCliMemory is not { } item || !CliNativeMemory.IsOwnedFile(item.File.Path)) return;
        if (MessageBox.Show(this, $"Delete {item.File.Name}? The CLI forgets what it noted there. Your chats are not affected.",
                "Delete CLI memory", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try { File.Delete(item.File.Path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "Couldn't delete it: " + ex.Message, "Delete CLI memory", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        ReloadCliMemory();
    }

    private static string SafeRead(string path)
    {
        try { return new FileInfo(path).Length > 512 * 1024 ? "(This file is too large to edit here.)" : File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }
}
