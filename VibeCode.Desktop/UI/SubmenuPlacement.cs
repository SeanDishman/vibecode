using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace VibeCode.UI;

/// <summary>Separate cascading menus and keep their chevrons in sync with WPF's screen-edge placement.</summary>
public static class SubmenuPlacement
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(SubmenuPlacement), new PropertyMetadata(false, OnEnabledChanged));

    private static readonly DependencyPropertyKey OpensLeftPropertyKey = DependencyProperty.RegisterAttachedReadOnly(
        "OpensLeft", typeof(bool), typeof(SubmenuPlacement), new PropertyMetadata(false));
    public static readonly DependencyProperty OpensLeftProperty = OpensLeftPropertyKey.DependencyProperty;

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);
    public static bool GetOpensLeft(DependencyObject element) => (bool)element.GetValue(OpensLeftProperty);

    private static void OnEnabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not Popup popup) return;
        if ((bool)args.OldValue) popup.Opened -= OnOpened;
        if ((bool)args.NewValue)
        {
            popup.Opened += OnOpened;
            popup.CustomPopupPlacementCallback = (size, target, offset) => Positions(popup, size, target, offset);
        }
        else popup.CustomPopupPlacementCallback = null;
    }

    private static CustomPopupPlacement[] Positions(Popup popup, Size size, Size target, Point offset)
    {
        var owner = popup.TemplatedParent as MenuItem;
        double leftInset = 0, rightInset = 0;
        if (owner is not null)
        {
            // Align against the menu's outer surface, not the inset row. Otherwise the two panels overlap
            // by the menu padding, even when the popup has a positive offset.
            for (var parent = VisualTreeHelper.GetParent(owner); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            {
                if (parent is not Border surface) continue;
                leftInset = owner.TranslatePoint(new Point(), surface).X;
                rightInset = surface.ActualWidth - leftInset - target.Width;
                break;
            }
        }
        var right = new CustomPopupPlacement(new Point(target.Width + rightInset + 3 + offset.X, offset.Y), PopupPrimaryAxis.Horizontal);
        var left = new CustomPopupPlacement(new Point(-size.Width - leftInset - 3 + offset.X, offset.Y), PopupPrimaryAxis.Horizontal);
        return SystemParameters.MenuDropAlignment || owner?.FlowDirection == FlowDirection.RightToLeft
            ? [left, right] : [right, left];
    }

    private static void OnOpened(object? sender, EventArgs args)
    {
        if (sender is not Popup popup) return;
        // WPF chooses whichever candidate fits the monitor. Read the final screen position so a fallback
        // to the left is reflected in the chevron without changing native menu focus or navigation.
        popup.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!popup.IsOpen || popup.TemplatedParent is not MenuItem owner
                || popup.Child is not FrameworkElement child
                || PresentationSource.FromVisual(owner) is null || PresentationSource.FromVisual(child) is null) return;
            var ownerCenter = owner.PointToScreen(new Point(owner.ActualWidth / 2, 0));
            var popupCenter = child.PointToScreen(new Point(child.ActualWidth / 2, 0));
            owner.SetValue(OpensLeftPropertyKey, popupCenter.X < ownerCenter.X);
        }));
    }
}
