using System.Windows;

namespace VibeCode.UI;

/// <summary>
/// Keeps a pinned ("Keep on top") companion window on screen while VibeCode's main window is minimized. Windows hides an

/// the main window even when pinned. While pinned the window is released from its owner; unpinned it is owned again and
/// minimizes and restores with the app as before. Pinned or not, it still closes with the main window.
/// </summary>
internal sealed class PinnedWindowOwner
{
    private readonly Window _window;

    public PinnedWindowOwner(Window window, Window? anchor)
    {
        _window = window;
        Anchor = anchor;
        if (anchor is null) return;
        anchor.Closed += OnAnchorClosed;
        window.Closed += (_, _) => anchor.Closed -= OnAnchorClosed;
    }

    /// <summary>The main window this one belongs to, whether or not it owns it at the moment.</summary>
    public Window? Anchor { get; }

    /// <summary>The anchor when it is on screen; otherwise the window itself. A window opened from a pinned companion while
    /// the app is minimized must not be owned by the minimized main window, or Windows keeps it hidden too.</summary>
    public Window VisibleOwner => Anchor is { WindowState: not WindowState.Minimized } anchor ? anchor : _window;

    /// <summary>Release the window from its owner while pinned; own it again when unpinned.</summary>
    public void Apply(bool pinned)
    {
        var wanted = pinned ? null : Anchor;
        if (!ReferenceEquals(_window.Owner, wanted)) _window.Owner = wanted;
    }

    private void OnAnchorClosed(object? sender, EventArgs e) => _window.Close();
}
