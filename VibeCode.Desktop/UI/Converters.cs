using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace VibeCode.UI;

/// <summary>Attached behavior for controls that never scroll internally (the markdown viewer has its scrollbars
/// disabled; the read-only message TextBoxes size to their content). WPF otherwise lets those controls swallow the
/// mouse wheel, so scrolling "breaks" whenever the pointer is over a message. This re-raises the wheel to the parent
/// so the chat scrolls normally. NOT applied to code blocks (they have a MaxHeight and legitimately scroll).</summary>
public static class WheelForward
{
    public static readonly DependencyProperty ToParentProperty = DependencyProperty.RegisterAttached(
        "ToParent", typeof(bool), typeof(WheelForward), new PropertyMetadata(false, OnChanged));

    public static void SetToParent(DependencyObject o, bool v) => o.SetValue(ToParentProperty, v);
    public static bool GetToParent(DependencyObject o) => (bool)o.GetValue(ToParentProperty);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement el || e.NewValue is not true) return;
        el.PreviewMouseWheel += (_, a) =>
        {
            if (a.Handled) return;
            Forward(el, a);
        };
    }

    /// <summary>Attached behavior for controls that DO scroll internally (code blocks: CodeBox has a MaxHeight +
    /// Auto scrollbar). Let the control scroll itself until it hits the top/bottom edge, THEN hand the wheel off to
    /// the enclosing chat ScrollViewer - instead of trapping the wheel at the edge (which freezes page scrolling).</summary>
    public static readonly DependencyProperty AtBoundaryProperty = DependencyProperty.RegisterAttached(
        "AtBoundary", typeof(bool), typeof(WheelForward), new PropertyMetadata(false, OnAtBoundaryChanged));

    public static void SetAtBoundary(DependencyObject o, bool v) => o.SetValue(AtBoundaryProperty, v);
    public static bool GetAtBoundary(DependencyObject o) => (bool)o.GetValue(AtBoundaryProperty);

    private static void OnAtBoundaryChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement el || e.NewValue is not true) return;
        el.PreviewMouseWheel += (_, a) =>
        {
            if (a.Handled) return;
            var sv = FindScrollViewer(el);
            if (sv is null || sv.ScrollableHeight <= 0) { Forward(el, a); return; }   // nothing to scroll here -> page
            var atTop = sv.VerticalOffset <= 0.5;
            var atBottom = sv.VerticalOffset >= sv.ScrollableHeight - 0.5;
            // still room to scroll in this direction -> let the code box handle it (do nothing)
            if ((a.Delta > 0 && !atTop) || (a.Delta < 0 && !atBottom)) return;
            Forward(el, a);   // at the edge -> hand off to the page
        };
    }

    /// <summary>
    /// Hand the wheel to whatever encloses this control, as a bubbling event.
    /// <para>
    /// Raised from the PARENT, never from the control itself. A bubble route starts AT its source, so re-raising on
    /// the control put the control's own class handler first in line - and that handler is exactly what was eating
    /// the wheel in the first place. FlowDocumentScrollViewer (what the markdown viewer is) swallows it outright,
    /// which is why scrolling died whenever the pointer sat over an assistant reply while the plain TextBox
    /// messages either side of it scrolled fine. A code block at its edge has the same problem with ScrollViewer's
    /// handler. Starting one level up skips that handler and reaches the transcript.
    /// </para>
    /// </summary>
    private static void Forward(UIElement el, MouseWheelEventArgs a)
    {
        a.Handled = true;   // stop this control from swallowing the wheel
        var parent = VisualTreeHelper.GetParent(el) as UIElement ?? el;
        parent.RaiseEvent(new MouseWheelEventArgs(a.MouseDevice, a.Timestamp, a.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = parent,
        });
    }

    /// <summary>Trap the wheel inside a popup / overlay: scroll the first nested ScrollViewer if it has room,
    /// and ALWAYS mark the event handled so the chat transcript (or anything underneath a Popup) never scrolls
    /// at the same time. Used by the "Your messages" navigator list.</summary>
    public static readonly DependencyProperty ContainProperty = DependencyProperty.RegisterAttached(
        "Contain", typeof(bool), typeof(WheelForward), new PropertyMetadata(false, OnContainChanged));

    public static void SetContain(DependencyObject o, bool v) => o.SetValue(ContainProperty, v);
    public static bool GetContain(DependencyObject o) => (bool)o.GetValue(ContainProperty);

    private static void OnContainChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement el || e.NewValue is not true) return;
        el.PreviewMouseWheel += (_, a) =>
        {
            if (a.Handled) return;
            var sv = FindScrollViewer(el);
            if (sv is not null && sv.ScrollableHeight > 0.5)
            {
                var notches = Math.Abs(a.Delta) / 120.0;
                var lines = SystemParameters.WheelScrollLines;
                var distance = lines < 0
                    ? sv.ViewportHeight * notches
                    : Math.Max(1, lines) * 18.0 * notches;
                var target = sv.VerticalOffset - Math.Sign(a.Delta) * distance;
                sv.ScrollToVerticalOffset(Math.Clamp(target, 0, sv.ScrollableHeight));
            }
            // Always consume — even at top/bottom — so the transcript under the popup never moves with the list.
            a.Handled = true;
        };
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;
        var n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < n; i++)
            if (FindScrollViewer(System.Windows.Media.VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }
}

/// <summary>Attached behavior: keep a ScrollViewer pinned to the bottom as its content grows (used by Bridge panes),
/// but only while the user is already at the bottom - if they scrolled up to read, incoming output and expanding a
/// card leave their position alone (no yank to the bottom).</summary>
public static class AutoScroll
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(AutoScroll), new PropertyMetadata(false, OnEnabledChanged));

    public static void SetEnabled(DependencyObject o, bool v) => o.SetValue(EnabledProperty, v);
    public static bool GetEnabled(DependencyObject o) => (bool)o.GetValue(EnabledProperty);

    public static readonly DependencyProperty ScrollToBottomOnShowProperty = DependencyProperty.RegisterAttached(
        "ScrollToBottomOnShow", typeof(bool), typeof(AutoScroll), new PropertyMetadata(false));

    public static void SetScrollToBottomOnShow(DependencyObject o, bool v) => o.SetValue(ScrollToBottomOnShowProperty, v);
    public static bool GetScrollToBottomOnShow(DependencyObject o) => (bool)o.GetValue(ScrollToBottomOnShowProperty);

    private static readonly DependencyProperty CleanupProperty = DependencyProperty.RegisterAttached(
        "Cleanup", typeof(Action), typeof(AutoScroll), new PropertyMetadata(null));

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        (d.GetValue(CleanupProperty) as Action)?.Invoke();
        d.ClearValue(CleanupProperty);
        if (e.NewValue is not true) return;
        if (d is not FrameworkElement fe) return;

        ScrollViewer? attached = null;
        Action? detach = null;
        DispatcherOperation? pendingReset = null;

        void Resolve(bool reset = false)
        {
            if (!fe.IsLoaded || !fe.IsVisible || !GetEnabled(fe)) return;
            if (fe is Control control) control.ApplyTemplate();
            var inner = fe as ScrollViewer ?? FindDescendant<ScrollViewer>(fe);
            if (inner is not null)
            {
                fe.LayoutUpdated -= OnLayoutUpdated;
                if (ReferenceEquals(inner, attached) && !reset) return;
                detach?.Invoke();
                attached = inner;
                // Rebinding a shared feed can settle by a couple of text lines as its
                // virtualized message groups are measured. Keep that small gap following.
                detach = Attach(inner, GetScrollToBottomOnShow(fe) ? 48 : 24);
            }
            else
            {
                // Collapsed bridge transcripts may have no template yet. Retrying with BeginInvoke at Loaded
                // priority spins forever and starves input/rendering. Wait for actual layout instead, without
                // scheduling any work ourselves, and stop listening when hidden, unloaded, or resolved.
                fe.LayoutUpdated -= OnLayoutUpdated;
                fe.LayoutUpdated += OnLayoutUpdated;
            }
        }

        void OnLayoutUpdated(object? sender, EventArgs args) => Resolve();
        void OnLoaded(object sender, RoutedEventArgs args) => Resolve();
        void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
        {
            if (fe.IsVisible) Resolve(reset: GetScrollToBottomOnShow(fe));
            else
            {
                pendingReset?.Abort();
                pendingReset = null;
                fe.LayoutUpdated -= OnLayoutUpdated;
            }
        }
        void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs args)
        {
            // A shared terminal can display a different bridge without being hidden first.
            // Wait for its ItemsSource binding and virtualized layout to adopt that conversation.
            if (!GetScrollToBottomOnShow(fe) || !fe.IsLoaded || !fe.IsVisible) return;
            pendingReset?.Abort();
            pendingReset = fe.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                pendingReset = null;
                Resolve(reset: true);
            }));
        }
        void OnUnloaded(object sender, RoutedEventArgs args)
        {
            pendingReset?.Abort();
            pendingReset = null;
            fe.LayoutUpdated -= OnLayoutUpdated;
            detach?.Invoke();
            detach = null;
            attached = null;
        }

        fe.Loaded += OnLoaded;
        fe.Unloaded += OnUnloaded;
        fe.IsVisibleChanged += OnVisibilityChanged;
        fe.DataContextChanged += OnDataContextChanged;
        fe.SetValue(CleanupProperty, (Action)(() =>
        {
            fe.Loaded -= OnLoaded;
            fe.Unloaded -= OnUnloaded;
            fe.IsVisibleChanged -= OnVisibilityChanged;
            fe.DataContextChanged -= OnDataContextChanged;
            OnUnloaded(fe, new RoutedEventArgs());
        }));
        Resolve();
    }

    private static Action Attach(ScrollViewer sv, double tolerance)
    {
        var stick = true;   // start pinned to the newest message
        void Pin() => sv.ScrollToBottom();

        void OnScrollChanged(object sender, ScrollChangedEventArgs a)
        {
            // ScrollChanged BUBBLES. A markdown viewer or code block inside a message is a scroller of its own, and
            // realizing one raises an extent change carrying ITS numbers - which read here as "the transcript grew"
            // and scrolled the transcript for a reason that had nothing to do with it.
            if (!ReferenceEquals(a.OriginalSource, sv)) return;

            var layoutChanged = a.ExtentHeightChange != 0 || a.ViewportHeightChange != 0 || a.ViewportWidthChange != 0;
            stick = ShouldFollowBottom(sv, a, stick, tolerance);

            // Keep following through every estimate refinement, rather than giving up after a fixed number of
            // layout passes. A viewport change (resizing/composer growth) moves the bottom without growing content.
            if (stick && layoutChanged && sv.VerticalOffset < sv.ScrollableHeight - 0.5) Pin();
        }
        // Expanding a card focuses/grows a child; stop WPF from auto-scrolling it into view (that's the "jumps to bottom").
        RequestBringIntoViewEventHandler onBringIntoView = (_, ev) => ev.Handled = true;
        sv.ScrollChanged += OnScrollChanged;
        sv.AddHandler(FrameworkElement.RequestBringIntoViewEvent, onBringIntoView, true);

        // A pane whose transcript is already IN it when the scroller is built never sees the extent grow, so following
        // growth is not enough on its own - it would sit at the top until the agent happened to say something. That is
        // every re-entry: leaving a chat parks the roster, and coming back rebuilds all five panes from scratch.
        Pin();
        // Navigating away only COLLAPSES the surface, which keeps the same scroller and skips Attach entirely. Re-pin
        // when it comes back - unless the user had deliberately scrolled up, whose place is theirs to keep.
        void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is true && stick) Pin();
        }
        sv.IsVisibleChanged += OnVisibilityChanged;
        return () =>
        {
            sv.ScrollChanged -= OnScrollChanged;
            sv.RemoveHandler(FrameworkElement.RequestBringIntoViewEvent, onBringIntoView);
            sv.IsVisibleChanged -= OnVisibilityChanged;
        };
    }

    internal static bool ShouldFollowBottom(ScrollViewer sv, ScrollChangedEventArgs change, bool wasFollowing, double tolerance)
    {
        if (sv.VerticalOffset >= sv.ScrollableHeight - tolerance) return true;
        var layoutChanged = change.ExtentHeightChange != 0 || change.ViewportHeightChange != 0 || change.ViewportWidthChange != 0;
        var bottomChange = change.ExtentHeightChange - change.ViewportHeightChange;
        // Recycling and streaming can adjust both the extent AND offset in one event. Even a negative offset
        // change can simply follow a shrinking extent or growing viewport. Only an upward move beyond that
        // layout correction detaches the reader; it also wins over output arriving in the same layout pass.
        var movedUp = change.VerticalChange < -0.5 && change.VerticalChange < Math.Min(0, bottomChange) - 0.5;
        if (movedUp || !layoutChanged && change.VerticalChange != 0) return false;
        return wasFollowing;
    }

    // First ScrollViewer in the visual subtree (the scroll host inside a ListBox/control template).
    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            var deeper = FindDescendant<T>(child);
            if (deeper != null) return deeper;
        }
        return null;
    }
}

/// <summary>Attached DP that proxies an animatable double onto <see cref="ScrollViewer.ScrollToVerticalOffset"/>.
/// ScrollViewer.VerticalOffset is read-only, so the "fly to this message" navigation animates THIS property and each
/// tick pushes the value into the scroller - giving a smooth glide instead of an instant jump.</summary>
public static class ScrollAnimationBehavior
{
    public static readonly DependencyProperty VerticalOffsetProperty = DependencyProperty.RegisterAttached(
        "VerticalOffset", typeof(double), typeof(ScrollAnimationBehavior), new PropertyMetadata(0.0, OnVerticalOffsetChanged));

    public static void SetVerticalOffset(DependencyObject o, double v) => o.SetValue(VerticalOffsetProperty, v);
    public static double GetVerticalOffset(DependencyObject o) => (double)o.GetValue(VerticalOffsetProperty);

    private static void OnVerticalOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ScrollViewer sv) sv.ScrollToVerticalOffset((double)e.NewValue);
    }
}

/// <summary>Registers a chat/Bridge message ListBox against the ChatViewModel it currently hosts, so the floating
/// "jump to your messages" navigator can resolve which transcript to scroll from a clicked prompt (whose Owner is
/// that VM). The main chat list re-points as the active chat switches (via DataContextChanged); Bridge panes each
/// keep their own. Weak keys mean closed chats/panes fall out of the table on their own.</summary>
public static class NavHost
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(NavHost), new PropertyMetadata(false, OnIsEnabledChanged));

    public static void SetIsEnabled(DependencyObject o, bool v) => o.SetValue(IsEnabledProperty, v);
    public static bool GetIsEnabled(DependencyObject o) => (bool)o.GetValue(IsEnabledProperty);

    private static readonly ConditionalWeakTable<ChatViewModel, ListBox> _hosts = new();

    /// <summary>The realized message ListBox currently showing <paramref name="chat"/>, if any.</summary>
    public static ListBox? ForChat(ChatViewModel? chat) =>
        chat is not null && _hosts.TryGetValue(chat, out var list) ? list : null;

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListBox list) return;
        if (e.NewValue is true)
        {
            Bind(list);   // DataContext may not have inherited yet; Loaded/DataContextChanged cover the rest
            list.Loaded += OnHostLoaded;
            list.DataContextChanged += OnHostDataContextChanged;
            list.Unloaded += OnHostUnloaded;
        }
        else
        {
            list.Loaded -= OnHostLoaded;
            list.DataContextChanged -= OnHostDataContextChanged;
            list.Unloaded -= OnHostUnloaded;
            if (list.DataContext is ChatViewModel vm) Drop(vm, list);
        }
    }

    private static void OnHostLoaded(object sender, RoutedEventArgs e) { if (sender is ListBox l) Bind(l); }

    private static void OnHostDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not ListBox list) return;
        if (e.OldValue is ChatViewModel oldVm) Drop(oldVm, list);
        if (e.NewValue is ChatViewModel newVm) _hosts.AddOrUpdate(newVm, list);
    }

    private static void OnHostUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is ListBox { DataContext: ChatViewModel vm } list) Drop(vm, list);
    }

    private static void Bind(ListBox list)
    {
        if (list.DataContext is ChatViewModel vm) _hosts.AddOrUpdate(vm, list);
    }

    // Only forget the mapping if it still points at THIS list; a newer host may already own the VM.
    private static void Drop(ChatViewModel vm, ListBox list)
    {
        if (_hosts.TryGetValue(vm, out var current) && ReferenceEquals(current, list)) _hosts.Remove(vm);
    }
}

internal static class Palette
{
    // Follow the active theme dictionary so code-built brushes (status dots, banners) match CLI mode
    // too; the literals are the dark-theme values, kept as a fallback for design time / unit harnesses.
    public static readonly SolidColorBrush Accent = FromTheme("Accent", 0x4C, 0x8D, 0xF5);
    public static readonly SolidColorBrush Green = FromTheme("Green", 0x7E, 0xD0, 0xA6);
    public static readonly SolidColorBrush Red = FromTheme("Red", 0xE8, 0x6A, 0x78);
    public static readonly SolidColorBrush Amber = FromTheme("Amber", 0xE0, 0xB2, 0x4D);
    public static readonly SolidColorBrush Muted = FromTheme("Muted", 0x9E, 0xA0, 0xA8);
    public static readonly SolidColorBrush Faint = FromTheme("Faint", 0x6C, 0x6E, 0x77);

    private static SolidColorBrush FromTheme(string key, byte r, byte g, byte b)
    {
        if (System.Windows.Application.Current?.TryFindResource(key) is SolidColorBrush themed)
        {
            var brush = new SolidColorBrush(themed.Color);
            brush.Freeze();
            return brush;
        }
        return Frozen(r, g, b);
    }

    public static SolidColorBrush Frozen(byte r, byte g, byte b, byte a = 0xFF)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}

public sealed class StatusBrushConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value switch
    {
        "running" => Palette.Accent,
        "starting" => Palette.Amber,
        "preparing" => Palette.Amber,
        "error" => Palette.Red,
        "closed" => Palette.Faint,
        _ => Palette.Green,
    };
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class DiffBgConverter : IValueConverter
{
    private static readonly SolidColorBrush Add = Palette.Frozen(0x7E, 0xD0, 0xA6, 0x1C);
    private static readonly SolidColorBrush Del = Palette.Frozen(0xE8, 0x6A, 0x78, 0x1C);
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value switch
    {
        "add" => Add,
        "del" => Del,
        _ => Brushes.Transparent,
    };
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class DiffFgConverter : IValueConverter
{
    private static readonly SolidColorBrush Add = Palette.Frozen(0xBC, 0xE0, 0xB8);
    private static readonly SolidColorBrush Del = Palette.Frozen(0xF2, 0xBD, 0xC3);
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value switch
    {
        "add" => Add,
        "del" => Del,
        _ => Palette.Muted,
    };
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class DiffSignConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value switch
    {
        "add" => "+", "del" => "−", _ => " ",
    };
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class TodoFgConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value switch
    {
        "completed" => Palette.Faint,
        "in_progress" => Palette.Accent,
        _ => Palette.Muted,
    };
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class BannerBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Error = Palette.Frozen(0xE8, 0x6A, 0x78, 0x16);
    private static readonly SolidColorBrush Warn = Palette.Frozen(0xE0, 0xB2, 0x4D, 0x16);
    private static readonly SolidColorBrush Info = Palette.Frozen(0x8F, 0xAE, 0xE6, 0x14);
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value switch
    {
        "error" or "auth" => Error,
        "warn" => Warn,
        _ => Info,
    };
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class BannerFgConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value switch
    {
        "error" or "auth" => Palette.Red,
        "warn" => Palette.Amber,
        _ => Palette.Frozen(0x8F, 0xAE, 0xE6),
    };
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class NonEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var visible = value switch
        {
            null => false,
            string s => !string.IsNullOrWhiteSpace(s),
            int i => i > 0,
            bool b => b,
            System.Collections.ICollection col => col.Count > 0,
            _ => true,
        };
        if (string.Equals(p as string, "invert", StringComparison.OrdinalIgnoreCase)) visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Grey hint text sitting on top of an input. Visible only while the box is BOTH empty AND unfocused -
/// binding it to the text alone left the hint sitting under a blinking caret the whole time you were about to
/// type. Feed it [Text, IsKeyboardFocusWithin] of the TextBox it labels.</summary>
public sealed class HintVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type t, object? p, CultureInfo c)
    {
        var text = values.Length > 0 ? values[0] as string : null;
        var focused = values.Length > 1 && values[1] is bool b && b;
        return !focused && string.IsNullOrWhiteSpace(text) ? Visibility.Visible : Visibility.Collapsed;
    }
    public object[] ConvertBack(object? v, Type[] t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class UsageBrushConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var pct = value switch { int i => i, double d => d, long l => l, _ => 0.0 };
        return pct >= 95 ? Palette.Red : pct >= 80 ? Palette.Amber : Palette.Accent;
    }
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Shortens a usage summary for the bridge header pills. A bridge can run four providers at once, and the
/// long forms ("49% session · 36% week", "2 accounts · max 72%") pushed Announce and Add agent off the header. This
/// only abbreviates whole words - the numbers, separators and ordering are left exactly as the provider produced
/// them - so an unfamiliar summary ("checking…", "sign in again") still passes through readable rather than mangled.
/// The pill's tooltip and popup keep the full wording.</summary>
public sealed class CompactUsageConverter : IValueConverter
{
    private static readonly (string Long, string Short)[] Abbreviations =
    [
        ("session", "ses"), ("sessions", "ses"),
        ("weekly", "wk"), ("week", "wk"),
        ("monthly", "mo"), ("month", "mo"),
        ("daily", "day"),
        ("accounts", "accts"), ("account", "acct"),
        ("unavailable", "n/a"),
    ];

    private static readonly Regex Word = new(@"[A-Za-z]+", RegexOptions.Compiled);

    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        if (value is not string text || string.IsNullOrWhiteSpace(text)) return "";
        return Word.Replace(text.Trim(), match =>
        {
            foreach (var (word, shortened) in Abbreviations)
                if (match.Value.Equals(word, StringComparison.OrdinalIgnoreCase))
                    return shortened;
            return match.Value;
        });
    }

    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class TimeAgoConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        if (value is not DateTime dt) return "";
        var s = (DateTime.Now - dt).TotalSeconds;
        return s switch
        {
            < 90 => "now",
            < 3600 => $"{s / 60:0}m",
            < 86400 => $"{s / 3600:0}h",
            _ => $"{s / 86400:0}d",
        };
    }
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}
