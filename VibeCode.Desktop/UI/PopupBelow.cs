using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using VibeCode.Services;

namespace VibeCode.UI;

/// <summary>
/// Keeps a <c>Placement="Bottom"</c> popup under its button. WPF flips a popup above its target as soon as it is too
/// tall for the room below, so a card that grows while open - the subagent roster swapping to one subagent's
/// transcript - jumped up over the pane above. Capping the card at the room below means it always fits there; its
/// own scroll area takes up the difference.
/// </summary>
public static class PopupBelow
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(PopupBelow), new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    /// <summary>Gap kept between the card and the bottom of the screen, in DIPs.</summary>
    private const double ScreenGap = 8;
    /// <summary>The card never shrinks below this, even with the button at the very bottom of the screen.</summary>
    private const double Smallest = 140;

    private static void OnEnabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not Popup popup) return;
        popup.Opened -= OnOpened;
        if ((bool)args.NewValue) popup.Opened += OnOpened;
    }

    private static void OnOpened(object? sender, EventArgs args)
    {
        if (sender is Popup { Child: FrameworkElement card, PlacementTarget: FrameworkElement target } popup
            && Room(target, popup.VerticalOffset) is { } room)
            card.MaxHeight = room;
    }

    /// <summary>DIPs from the bottom of <paramref name="target"/>, less the popup's offset, to the bottom of the usable
    /// screen it is on. Null while the target is not on screen.</summary>
    internal static double? Room(FrameworkElement target, double offset)
    {
        if (PresentationSource.FromVisual(target) is null || Window.GetWindow(target) is not { } window) return null;
        var bottom = target.PointToScreen(new Point(0, target.ActualHeight)).Y;
        var work = DisplayMonitorService.WorkAreaFor(window);
        return Math.Max(Smallest, (work.Bottom - bottom) / VisualTreeHelper.GetDpi(target).DpiScaleY - offset - ScreenGap);
    }
}
