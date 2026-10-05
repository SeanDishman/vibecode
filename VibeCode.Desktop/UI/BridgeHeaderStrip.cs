using System.Windows;
using System.Windows.Controls;

namespace VibeCode.UI;

/// <summary>
/// One-line Bridge pane header. When everything fits it shows the full readout (the <see cref="Slot.Wide"/> children:
/// supervision, working text, tokens, rates, cost). When the pane is too narrow it switches to the compact form:
/// identity, the task title trimmed to the remaining room, the short status word, and an info icon whose hover card
/// holds the rest. As room shrinks further the title goes first, then the status word; the info icon stays, since its
/// card carries both.
///
/// The inactive set is arranged at zero size instead of being collapsed. A collapsed element reports no desired
/// width, which would leave the strip unable to tell when the full readout fits again.
/// </summary>
public sealed class BridgeHeaderStrip : Panel
{
    /// <summary>Always: identity. Title: trims first. Wide: the full readout. Status: compact-only, dropped when
    /// there is no room. Compact: compact-only and kept (the info icon).</summary>
    public enum Slot { Always, Title, Wide, Status, Compact }

    public static readonly DependencyProperty SlotProperty = DependencyProperty.RegisterAttached(
        "Slot", typeof(Slot), typeof(BridgeHeaderStrip),
        new FrameworkPropertyMetadata(Slot.Always, FrameworkPropertyMetadataOptions.AffectsParentMeasure));
    public static Slot GetSlot(DependencyObject element) => (Slot)element.GetValue(SlotProperty);
    public static void SetSlot(DependencyObject element, Slot value) => element.SetValue(SlotProperty, value);

    /// <summary>The full readout must clear the width by this much before a compact header expands again, so a
    /// ticking timer or token rate cannot flip the header back and forth at the boundary.</summary>
    private const double Hysteresis = 16;
    /// <summary>A title squeezed below this is dropped rather than shown as a bare "· …".</summary>
    private const double MinimumTitle = 36;

    private static readonly DependencyPropertyKey IsCompactKey = DependencyProperty.RegisterReadOnly(
        nameof(IsCompact), typeof(bool), typeof(BridgeHeaderStrip), new PropertyMetadata(false));
    /// <summary>True while the compact form shows. Compact-only controls bind their focusability to it, so Tab never
    /// lands on the info icon while it is folded away.</summary>
    public static readonly DependencyProperty IsCompactProperty = IsCompactKey.DependencyProperty;
    public bool IsCompact => (bool)GetValue(IsCompactProperty);
    private bool _statusFits = true;

    private bool Shows(UIElement child) => GetSlot(child) switch
    {
        Slot.Wide => !IsCompact,
        Slot.Status => IsCompact && _statusFits,
        Slot.Compact => IsCompact,
        _ => true,
    };

    protected override Size MeasureOverride(Size available)
    {
        var unbounded = new Size(double.PositiveInfinity, available.Height);
        double always = 0, title = 0, wide = 0, status = 0, compact = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(unbounded);
            var width = child.DesiredSize.Width;
            switch (GetSlot(child))
            {
                case Slot.Title: title += width; break;
                case Slot.Wide: wide += width; break;
                case Slot.Status: status += width; break;
                case Slot.Compact: compact += width; break;
                default: always += width; break;
            }
        }

        var full = always + title + wide;
        var compactForm = !double.IsPositiveInfinity(available.Width)
            && full > available.Width - (IsCompact ? Hysteresis : 0);
        // Only focusability binds to this, so changing it here cannot invalidate this measure pass.
        if (compactForm != IsCompact) SetValue(IsCompactKey, compactForm);
        _statusFits = true;

        if (compactForm)
        {
            // Identity and the info icon are fixed; the status word outranks the title, which takes what is left.
            var room = Math.Max(0, available.Width - always - compact);
            _statusFits = status <= room;
            if (_statusFits) room -= status;
            foreach (UIElement child in InternalChildren)
            {
                if (GetSlot(child) != Slot.Title) continue;
                // Re-measure only a title that must shrink: a constraint equal to its layout-rounded width would
                // already trim the last fraction of a pixel into an ellipsis.
                if (child.DesiredSize.Width > room)
                    child.Measure(new Size(room < MinimumTitle ? 0 : room, available.Height));
                room -= child.DesiredSize.Width;
            }
        }

        double used = 0, height = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (!Shows(child)) continue;
            used += child.DesiredSize.Width;
            height = Math.Max(height, child.DesiredSize.Height);
        }
        return new Size(double.IsPositiveInfinity(available.Width) ? used : Math.Min(used, available.Width), height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        // No ClipToBounds: the manager crown deliberately floats above the status dot. Clamping each slot to the room
        // left lets an element's own layout clip trim it instead, so nothing can spill under the header buttons.
        double x = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (!Shows(child)) { child.Arrange(new Rect(x, 0, 0, 0)); continue; }
            var width = Math.Min(child.DesiredSize.Width, Math.Max(0, final.Width - x));
            child.Arrange(new Rect(x, 0, width, final.Height));
            x += width;
        }
        return final;
    }
}
