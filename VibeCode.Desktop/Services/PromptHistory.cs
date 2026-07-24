namespace VibeCode.Services;

/// <summary>
/// Keeps a small, in-memory command-line-style history for one composer.
/// Entries are stored oldest-to-newest; the cursor just past the newest entry represents the live draft.
/// </summary>
public sealed class PromptHistory
{
    public const int DefaultCapacity = 10;

    private readonly int _capacity;
    private readonly List<string> _entries = new();
    private int _cursor;
    private string _liveDraft = "";

    public PromptHistory(int capacity = DefaultCapacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    /// <summary>Remember a submitted text prompt and begin the next navigation from the live draft.</summary>
    public void Record(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return;

        _entries.Add(prompt);
        if (_entries.Count > _capacity)
            _entries.RemoveRange(0, _entries.Count - _capacity);
        ResetNavigation();
    }

    /// <summary>
    /// Move through history. A direction of -1 moves older and +1 moves newer. Moving newer past the
    /// latest prompt restores the draft that was present when navigation began.
    /// </summary>
    public bool TryNavigate(int direction, string currentText, out string text)
    {
        if (direction is not (-1 or 1))
            throw new ArgumentOutOfRangeException(nameof(direction), "Direction must be -1 or +1.");

        text = currentText;
        if (_entries.Count == 0) return false;

        if (direction < 0)
        {
            if (_cursor == _entries.Count) _liveDraft = currentText;
            if (_cursor > 0) _cursor--;
            text = _entries[_cursor];
            return true; // consume Up at the oldest entry instead of moving its caret
        }

        if (_cursor >= _entries.Count) return false;
        _cursor++;
        text = _cursor == _entries.Count ? _liveDraft : _entries[_cursor];
        return true;
    }

    /// <summary>True while the composer still contains the history entry most recently returned.</summary>
    public bool IsBrowsing(string currentText) =>
        _cursor < _entries.Count && string.Equals(currentText, _entries[_cursor], StringComparison.Ordinal);

    public void ResetNavigation()
    {
        _cursor = _entries.Count;
        _liveDraft = "";
    }
}
