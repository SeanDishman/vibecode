using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace VibeCode.UI;

/// <summary>
/// Drag-select across message bubbles in a transcript <see cref="ListBox"/>.
/// <para>
/// Every message renders into its own text host - a read-only TextBox for prompts and thinking blocks, an
/// MdXaml FlowDocument viewer for replies, a card for tool calls - and a native WPF selection cannot leave
/// the control it started in. Dragging from one message into the next therefore left the second one
/// untouched: you could only ever copy one message at a time.
/// </para>
/// <para>
/// This behaviour watches the drag one level up, on the list itself. Inside a single message nothing changes -
/// the message's own control does its usual character-level selection. The moment the pointer leaves the
/// message it started in, the list takes the mouse over, drops the half-finished native selection and
/// highlights every message from the anchor to the pointer. Ctrl+C, or "Copy N messages" from the right-click
/// menu, then yields all of them.
/// </para>
/// <para>
/// Granularity is deliberately whole-message once the drag crosses a boundary: a range spanning a tool card
/// and a markdown reply has no common notion of a character offset, so what is highlighted is exactly what
/// lands on the clipboard rather than an approximation of it.
/// </para>
/// </summary>
public static class TranscriptSelection
{
    /// <summary>Turns one selected item into clipboard text. Defaults to the same block text the per-message
    /// "Copy" produces, so a two-message range reads exactly like copying each one in turn.</summary>
    public static Func<object, string> ItemText { get; set; } =
        item => item is ItemVm vm ? MainWindow.BlockText(vm) : item?.ToString() ?? "";

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled", typeof(bool), typeof(TranscriptSelection),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static void SetIsEnabled(DependencyObject target, bool value) => target.SetValue(IsEnabledProperty, value);
    public static bool GetIsEnabled(DependencyObject target) => (bool)target.GetValue(IsEnabledProperty);

    private static readonly DependencyProperty SessionProperty =
        DependencyProperty.RegisterAttached("Session", typeof(Session), typeof(TranscriptSelection));

    private static void OnIsEnabledChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not ListBox list) return;
        var existing = (Session?)list.GetValue(SessionProperty);
        if (e.NewValue is true)
        {
            if (existing is not null) return;
            list.SetValue(SessionProperty, new Session(list));
        }
        else
        {
            existing?.Detach();
            list.ClearValue(SessionProperty);
        }
    }

    /// <summary>Test seam: the range currently held on this list as (first, last) item indexes, or (-1, -1).</summary>
    internal static (int First, int Last) SelectedRange(ListBox list) =>
        ((Session?)list.GetValue(SessionProperty))?.Range ?? (-1, -1);

    /// <summary>Test seam: the text a copy would put on the clipboard right now.</summary>
    internal static string SelectedText(ListBox list) =>
        ((Session?)list.GetValue(SessionProperty))?.RangeText() ?? "";

    /// <summary>Per-list state. One instance is attached to the list for as long as the behaviour is on.</summary>
    private sealed class Session
    {
        // ===================== DRAGGING PAST THE EDGE =====================
        // The transcript is a pixel-scrolling VIRTUALIZING list of wildly variable-height messages, which means
        // ScrollViewer.VerticalOffset is not a stable number: the panel estimates the extent from the containers
        // it has realised so far, and every tick of an auto-scroll realises more of them and re-estimates, nudging
        // the offset. Stepping with "VerticalOffset + step" fed that estimate straight back in as the next input,
        // so the correction and the step compounded and the transcript visibly shook.
        //
        // The timer now owns a target of its own and never reads the live offset back, so re-estimation moves the
        // content once instead of being amplified into an oscillation. Smaller steps at screen rate rather than
        // 34px lurches five times a second: the same speed, realising a few containers per frame instead of a
        // screenful at once.
        private const double EdgeZone = 26;      // px from the top/bottom edge that starts auto-scrolling
        private const double EdgeTickMs = 16;    // one step per frame
        private const double EdgeSlowPxPerSec = 260;   // just inside the edge
        private const double EdgeFastPxPerSec = 1400;  // pushed well past it

        private readonly ListBox _list;

        private int _anchor = -1;               // index the drag started in
        private int _focus = -1;                // index the pointer is over now
        private bool _armed;                    // button is down inside a message; the drag has not left it yet
        private bool _dragging;                 // we hold the mouse; the message's own control no longer sees it
        private bool _active;                   // a cross-message range exists and is being drawn
        private Point _down;

        private RangeAdorner? _adorner;
        private ContextMenu? _menu;
        private DispatcherTimer? _edgeTimer;
        private int _edgeDirection;
        private double _edgeSpeed;              // px/sec, ramped by how far past the edge the pointer is
        private double _edgeTarget;             // where WE are scrolling to; never read back from the ScrollViewer
        private ScrollViewer? _scroller;        // cached: the tree walk to find it ran on every single tick

        public Session(ListBox list)
        {
            _list = list;
            list.PreviewMouseLeftButtonDown += OnMouseDown;
            list.PreviewMouseMove += OnMouseMove;
            list.PreviewMouseLeftButtonUp += OnMouseUp;
            list.PreviewKeyDown += OnKeyDown;
            // ContextMenuOpening rather than the right-button events: this is the hook WPF itself consults
            // before opening a menu, so handling it here reliably pre-empts the per-message BlockMenu.
            list.AddHandler(FrameworkElement.ContextMenuOpeningEvent,
                new ContextMenuEventHandler(OnContextMenuOpening), handledEventsToo: true);
            list.LostMouseCapture += OnLostCapture;
            list.DataContextChanged += OnDataContextChanged;
            list.Unloaded += OnUnloaded;
            // The highlight is painted in list coordinates, so it has to be repainted as the content moves.
            list.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScrollChanged));
        }

        public void Detach()
        {
            Clear();
            _list.PreviewMouseLeftButtonDown -= OnMouseDown;
            _list.PreviewMouseMove -= OnMouseMove;
            _list.PreviewMouseLeftButtonUp -= OnMouseUp;
            _list.PreviewKeyDown -= OnKeyDown;
            _list.RemoveHandler(FrameworkElement.ContextMenuOpeningEvent,
                new ContextMenuEventHandler(OnContextMenuOpening));
            _list.LostMouseCapture -= OnLostCapture;
            _list.DataContextChanged -= OnDataContextChanged;
            _list.Unloaded -= OnUnloaded;
            _list.RemoveHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScrollChanged));
        }

        public (int First, int Last) Range =>
            _active && _anchor >= 0 && _focus >= 0
                ? (Math.Min(_anchor, _focus), Math.Max(_anchor, _focus))
                : (-1, -1);

        // ---------- mouse ----------

        private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            // A press on a scrollbar is a scroll, not a selection, and must not arm the drag. IndexAt resolves a
            // point by Y ALONE - X is deliberately ignored so gaps between bubbles still hit a message - which means
            // a press way out at the right-hand edge, on the thumb, still resolved to whatever message sat at that
            // height. Dragging the thumb then moved into the next message, BeginDrag took the mouse capture away
            // from the thumb, and the transcript painted a range instead of scrolling: the scrollbar appeared dead
            // and the messages highlighted themselves. Any nested scrollbar (a code block's) is covered too.
            // Note this returns BEFORE the Clear() below: scrolling is not "clicking away", so a range the user has
            // already made survives them scrolling to look at something.
            if (OverScrollBar(e.OriginalSource)) return;
            // A fresh click anywhere drops the previous range - same as clicking away from a text selection.
            if (_active) Clear();
            _down = e.GetPosition(_list);
            _anchor = IndexAt(_down);
            _focus = _anchor;
            _armed = _anchor >= 0;
            // Deliberately not handled: within one message the message's own control still does the selecting.
        }

        /// <summary>True when the event came from inside a scrollbar. Walks visual parents, falling back to the
        /// logical tree because a press on markdown reports a FlowDocument text element, which is not a Visual and
        /// would otherwise stop the walk dead.</summary>
        private static bool OverScrollBar(object? source)
        {
            for (var node = source as DependencyObject; node is not null;)
            {
                if (node is ScrollBar) return true;
                node = node is Visual or System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(node)
                    : LogicalTreeHelper.GetParent(node);
            }
            return false;
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (!_armed) return;
            if (e.LeftButton != MouseButtonState.Pressed) { _armed = false; return; }

            var point = e.GetPosition(_list);
            if (!_dragging)
            {
                // Only take over once the pointer is genuinely in a different message. Below that threshold the
                // drag belongs to the message it started in, and stealing it would break ordinary selection.
                var over = IndexAt(point);
                if (over < 0 || over == _anchor) return;
                if (Math.Abs(point.Y - _down.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                BeginDrag();
            }

            SetFocus(IndexAt(point));
            UpdateEdgeScroll(point);
            e.Handled = true;
        }

        private void OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            _armed = false;
            if (!_dragging) return;
            EndDrag();
            e.Handled = true;   // the release belongs to the range drag, not to the message under the pointer
        }

        private void OnLostCapture(object sender, MouseEventArgs e) => EndDrag();

        private void BeginDrag()
        {
            _dragging = true;
            _active = true;
            ClearNativeSelections();
            Mouse.Capture(_list, CaptureMode.SubTree);
            // The range belongs to the list now, so Ctrl+C has to reach the list. Without this the keyboard
            // focus can still be sitting outside the transcript (the composer, a header button) and the copy
            // silently does nothing - which looks exactly like the selection not having worked.
            if (!_list.IsKeyboardFocusWithin) _list.Focus();
            ShowAdorner();
        }

        private void EndDrag()
        {
            if (!_dragging) return;
            _dragging = false;
            StopEdgeScroll();
            if (ReferenceEquals(Mouse.Captured, _list)) _list.ReleaseMouseCapture();
        }

        private void SetFocus(int index)
        {
            if (index < 0 || index == _focus) return;
            _focus = index;
            _adorner?.InvalidateVisual();
        }

        private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            if (!_active) return;
            var (first, last) = Range;
            // e.CursorLeft/Top are relative to whichever element raised the event, so read the pointer against
            // the list directly instead of trying to convert them.
            var over = IndexAt(Mouse.GetPosition(_list));
            // Right-clicking outside the range drops it and lets the message's own menu open normally.
            if (over < first || over > last) { Clear(); return; }
            e.Handled = true;                       // pre-empt the per-message BlockMenu
            ShowRangeMenu(last - first + 1);
        }

        // ---------- keyboard ----------

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (!_active) return;
            if (e.Key == Key.Escape) { Clear(); e.Handled = true; return; }
            // Preview, so this wins over the (now empty) native selection in the message we dragged out of.
            if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { Copy(); e.Handled = true; }
        }

        // ---------- lifetime ----------

        /// <summary>The highlight is painted in list coordinates, so it is repainted as the content moves — and
        /// while an edge drag is running this is also where the focused row is re-resolved. The pointer is
        /// stationary but the content under it is not, and doing it here means once per scroll that actually
        /// happened, after layout, instead of a hit test posted from every timer tick whether the view moved
        /// or not.</summary>
        private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            _adorner?.InvalidateVisual();
            if (_dragging && _edgeDirection != 0) SetFocus(IndexAt(Mouse.GetPosition(_list)));
        }

        // Both also drop the cached ScrollViewer: a new DataContext or a re-templated list can hand this behaviour
        // a different one, and a stale reference would auto-scroll a viewer nobody is looking at.
        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            _scroller = null;
            Clear();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _scroller = null;
            Clear();
        }

        public void Clear()
        {
            StopEdgeScroll();
            if (_dragging)
            {
                _dragging = false;
                if (ReferenceEquals(Mouse.Captured, _list)) _list.ReleaseMouseCapture();
            }
            _active = false;
            _armed = false;
            _anchor = -1;
            _focus = -1;
            HideAdorner();
        }

        // ---------- clipboard ----------

        private void Copy()
        {
            var text = RangeText();
            if (string.IsNullOrEmpty(text)) return;
            // Clipboard access throws when another process briefly holds it - never let a copy crash the app.
            try { Clipboard.SetText(text); }
            catch { /* transient clipboard lock */ }
        }

        public string RangeText()
        {
            var (first, last) = Range;
            if (first < 0) return "";
            var parts = new List<string>();
            for (var i = first; i <= last && i < _list.Items.Count; i++)
            {
                if (_list.Items[i] is not { } item) continue;
                string text;
                try { text = ItemText(item); }
                catch { continue; }
                if (!string.IsNullOrWhiteSpace(text)) parts.Add(text.Trim());
            }
            return string.Join("\n\n", parts);
        }

        // ---------- range menu ----------

        private void ShowRangeMenu(int count)
        {
            _menu ??= new ContextMenu
            {
                Style = Application.Current?.TryFindResource("DarkContextMenu") as Style,
                MinWidth = 190,
            };
            _menu.Items.Clear();
            _menu.Items.Add(new MenuItem
            {
                Header = count == 1 ? "Copy message" : $"Copy {count} messages",
                InputGestureText = "Ctrl+C",
                Command = new Relay(Copy),
            });
            _menu.Items.Add(new MenuItem { Header = "Clear selection", Command = new Relay(Clear) });
            _menu.PlacementTarget = _list;
            _menu.Placement = PlacementMode.MousePoint;
            _menu.IsOpen = true;
        }

        // ---------- hit testing ----------

        /// <summary>
        /// The item index under (or nearest to) a point in list coordinates. Nearest rather than strictly under,
        /// so the gaps between bubbles and a pointer dragged clean off the top or bottom of the list still
        /// resolve to a message instead of cancelling the drag.
        /// </summary>
        private int IndexAt(Point point)
        {
            if (ItemsPanel() is not { } panel) return -1;
            var nearest = -1;
            var best = double.MaxValue;
            foreach (var child in panel.Children)
            {
                if (child is not ListBoxItem container || !container.IsVisible) continue;
                var index = _list.ItemContainerGenerator.IndexFromContainer(container);
                if (index < 0) continue;
                var bounds = BoundsOf(container);
                if (bounds.IsEmpty) continue;
                if (point.Y >= bounds.Top && point.Y <= bounds.Bottom) return index;
                var gap = point.Y < bounds.Top ? bounds.Top - point.Y : point.Y - bounds.Bottom;
                if (gap < best) { best = gap; nearest = index; }
            }
            return nearest;
        }

        private Rect BoundsOf(Visual visual)
        {
            try
            {
                var size = visual is FrameworkElement fe ? fe.RenderSize : default;
                return visual.TransformToAncestor(_list).TransformBounds(new Rect(size));
            }
            catch { return Rect.Empty; }   // container detached mid-virtualization
        }

        /// <summary>The list's own items host. Breadth-first, so the list's ItemsPresenter is found before any
        /// ItemsPresenter belonging to a control inside a message.</summary>
        private Panel? ItemsPanel()
        {
            if (FindDescendant<ItemsPresenter>(_list) is not { } presenter) return null;
            return VisualTreeHelper.GetChildrenCount(presenter) > 0
                ? VisualTreeHelper.GetChild(presenter, 0) as Panel
                : null;
        }

        private ScrollViewer? Scroller() => FindDescendant<ScrollViewer>(_list);

        // ---------- highlight ----------

        private void ShowAdorner()
        {
            if (_adorner is not null) return;
            if (AdornerLayer.GetAdornerLayer(_list) is not { } layer) return;
            _adorner = new RangeAdorner(_list, this);
            layer.Add(_adorner);
        }

        private void HideAdorner()
        {
            if (_adorner is null) return;
            AdornerLayer.GetAdornerLayer(_list)?.Remove(_adorner);
            _adorner = null;
        }

        /// <summary>Rectangles to paint, in list coordinates: one per realized message in the range.</summary>
        public IEnumerable<Rect> HighlightRects()
        {
            var (first, last) = Range;
            if (first < 0 || ItemsPanel() is not { } panel) yield break;
            foreach (var child in panel.Children)
            {
                if (child is not ListBoxItem container || !container.IsVisible) continue;
                var index = _list.ItemContainerGenerator.IndexFromContainer(container);
                if (index < first || index > last) continue;
                var bounds = BoundsOf(BubbleOf(container) ?? container);
                if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) continue;
                yield return Rect.Inflate(bounds, 3, 1);
            }
        }

        /// <summary>
        /// The bubble inside a message row, when the row has one. A prompt is a card sized to its text and floated
        /// to the right of a full-width row, so highlighting the row itself would paint a band across the empty
        /// half; an assistant reply has no card and correctly falls back to the whole row.
        /// <para>
        /// Matched on HEIGHT rather than width: the bubble is as tall as the row it fills, while the decorations
        /// nested inside it - icon chips, status pills, attachment thumbnails - are not. Width is no use as a test
        /// because a short message produces a perfectly valid but very narrow bubble.
        /// </para>
        /// Breadth-first, so the outer card wins over anything drawn inside it.
        /// </summary>
        private static FrameworkElement? BubbleOf(FrameworkElement row)
        {
            var queue = new Queue<(DependencyObject Node, int Depth)>();
            queue.Enqueue((row, 0));
            while (queue.Count > 0)
            {
                var (node, depth) = queue.Dequeue();
                if (depth > 6) continue;
                var count = VisualTreeHelper.GetChildrenCount(node);
                for (var i = 0; i < count; i++)
                {
                    var child = VisualTreeHelper.GetChild(node, i);
                    if (child is Border { Background: not null } border &&
                        border.CornerRadius.TopLeft > 0 &&
                        border.ActualWidth >= 40 &&
                        border.ActualWidth <= row.ActualWidth &&
                        border.ActualHeight >= row.ActualHeight * 0.55)
                    {
                        return border;
                    }
                    queue.Enqueue((child, depth + 1));
                }
            }
            return null;
        }

        /// <summary>
        /// Drop whatever half-selection the message's own control had made before the drag crossed out of it.
        /// Leaving it would show two different highlights claiming to be the same selection - and Ctrl+C would
        /// then be ambiguous about which one it meant.
        /// </summary>
        private void ClearNativeSelections()
        {
            foreach (var node in Descendants(_list))
            {
                try
                {
                    switch (node)
                    {
                        case TextBox { SelectionLength: > 0 } box:
                            box.Select(box.SelectionStart, 0);
                            break;
                        case RichTextBox rich when !rich.Selection.IsEmpty:
                            rich.Selection.Select(rich.Selection.Start, rich.Selection.Start);
                            break;
                        case FlowDocumentScrollViewer { Selection: { IsEmpty: false } selection }:
                            selection.Select(selection.Start, selection.Start);
                            break;
                    }
                }
                catch { /* a control mid-teardown is not worth failing the drag over */ }
            }
        }

        // ---------- auto-scroll at the edges ----------

        private void UpdateEdgeScroll(Point point)
        {
            // How far PAST the edge the pointer is, so pushing further scrolls faster - the behaviour every other
            // list drag has, and it removes the need for one coarse step size to serve both "nudge" and "get me
            // to the bottom".
            var over = point.Y < EdgeZone ? EdgeZone - point.Y
                : point.Y > _list.ActualHeight - EdgeZone ? point.Y - (_list.ActualHeight - EdgeZone)
                : 0;

            _edgeDirection = over <= 0 ? 0 : point.Y < EdgeZone ? -1 : 1;
            if (_edgeDirection == 0) { StopEdgeScroll(); return; }

            var ramp = Math.Clamp(over / (EdgeZone * 3), 0, 1);
            _edgeSpeed = EdgeSlowPxPerSec + (EdgeFastPxPerSec - EdgeSlowPxPerSec) * ramp;
            if (_edgeTimer is not null) return;

            // Seeded ONCE, from the live offset, at the moment the drag reaches the edge. Every step after this is
            // ours alone.
            _scroller ??= Scroller();
            if (_scroller is null) return;
            _edgeTarget = _scroller.VerticalOffset;

            _edgeTimer = new DispatcherTimer(DispatcherPriority.Input)
            {
                Interval = TimeSpan.FromMilliseconds(EdgeTickMs),
            };
            _edgeTimer.Tick += (_, _) =>
            {
                if (!_dragging || _edgeDirection == 0 || _scroller is null) { StopEdgeScroll(); return; }

                _edgeTarget += _edgeDirection * _edgeSpeed * (EdgeTickMs / 1000.0);
                // Clamped against the CURRENT extent, which is the one thing worth re-reading: it grows as the
                // panel realises more, and a target sailing past the end would sit there doing nothing while the
                // list quietly grew underneath it.
                _edgeTarget = Math.Clamp(_edgeTarget, 0, Math.Max(0, _scroller.ScrollableHeight));
                _scroller.ScrollToVerticalOffset(_edgeTarget);
                // Nothing is posted here: the row under the pointer is re-resolved by OnScrollChanged, which
                // fires exactly when the scroll has actually been laid out.
            };
            _edgeTimer.Start();
        }

        private void StopEdgeScroll()
        {
            _edgeTimer?.Stop();
            _edgeTimer = null;
            _edgeDirection = 0;
            _edgeSpeed = 0;
        }
    }

    /// <summary>Paints the range behind the messages. An adorner rather than item chrome, because the containers
    /// are recycled by virtualization and must stay exactly as the message templates built them.</summary>
    private sealed class RangeAdorner : Adorner
    {
        private readonly Session _session;

        public RangeAdorner(ListBox list, Session session) : base(list)
        {
            _session = session;
            IsHitTestVisible = false;
        }

        protected override void OnRender(DrawingContext context)
        {
            var list = (ListBox)AdornedElement;
            var fill = Application.Current?.TryFindResource("TranscriptSelectionFill") as Brush
                       ?? new SolidColorBrush(Color.FromArgb(0x4C, 0x4C, 0x8D, 0xF5));

            context.PushClip(new RectangleGeometry(new Rect(list.RenderSize)));
            foreach (var rect in _session.HighlightRects())
                context.DrawRoundedRectangle(fill, null, rect, 8, 8);
            context.Pop();
        }
    }

    /// <summary>Minimal always-executable command, so the range menu can be built without a command binding.</summary>
    private sealed class Relay(Action run) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => run();
    }

    // ---------- visual tree helpers ----------

    /// <summary>Breadth-first search, so the shallowest match wins.</summary>
    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            var count = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (child is T match) return match;
                queue.Enqueue(child);
            }
        }
        return null;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            var count = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                yield return child;
                stack.Push(child);
            }
        }
    }
}
