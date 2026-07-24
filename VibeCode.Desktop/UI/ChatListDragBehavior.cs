using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace VibeCode.UI;

/// <summary>
/// Adds pointer drag-reordering to the sidebar chat ItemsControl without coupling the behavior to MainWindow.
/// The view model remains the source of truth, so activity promotions and persisted order use the same collection.
/// </summary>
internal static class ChatListDragBehavior
{
    private const string DragFormat = "VibeCode.SidebarChat";
    private static int _registered;
    private static Point _pressedAt;
    private static ChatViewModel? _pressedChat;
    private static ItemsControl? _pressedList;

    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) != 0) return;

        EventManager.RegisterClassHandler(typeof(ItemsControl), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnLoaded), true);
        EventManager.RegisterClassHandler(typeof(ItemsControl), UIElement.PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler(OnMouseLeftButtonDown), true);
        EventManager.RegisterClassHandler(typeof(ItemsControl), UIElement.PreviewMouseMoveEvent,
            new MouseEventHandler(OnMouseMove), true);
        EventManager.RegisterClassHandler(typeof(ItemsControl), UIElement.PreviewDragOverEvent,
            new DragEventHandler(OnDragOver), true);
        EventManager.RegisterClassHandler(typeof(ItemsControl), UIElement.PreviewDropEvent,
            new DragEventHandler(OnDrop), true);
    }

    private static bool IsChatList(ItemsControl list, out MainViewModel viewModel)
    {
        if (list.DataContext is MainViewModel candidate && ReferenceEquals(list.ItemsSource, candidate.ChatGroups))
        {
            viewModel = candidate;
            return true;
        }
        viewModel = null!;
        return false;
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ItemsControl list && IsChatList(list, out _)) list.AllowDrop = true;
    }

    private static void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        ClearPressed();
        if (sender is not ItemsControl list || !IsChatList(list, out var viewModel)
            || e.OriginalSource is not DependencyObject origin)
            return;

        var row = FindRowButton(origin, list, viewModel);
        if (row?.DataContext is not ChatViewModel chat) return;

        // The row contains a close button. Clicking or moving from that nested control must remain a close gesture,
        // not unexpectedly pick the whole conversation up.
        var nearestButton = FindAncestor<ButtonBase>(origin, list);
        if (nearestButton is not null && !ReferenceEquals(nearestButton, row)) return;

        _pressedAt = e.GetPosition(list);
        _pressedChat = chat;
        _pressedList = list;
    }

    private static void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressedChat is null || _pressedList is null || !ReferenceEquals(sender, _pressedList)) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            ClearPressed();
            return;
        }

        var now = e.GetPosition(_pressedList);
        if (Math.Abs(now.X - _pressedAt.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(now.Y - _pressedAt.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var list = _pressedList;
        var chat = _pressedChat;
        ClearPressed();
        var payload = new DataObject();
        payload.SetData(DragFormat, chat);
        DragDrop.DoDragDrop(list, payload, DragDropEffects.Move);
        e.Handled = true;
    }

    private static void OnDragOver(object sender, DragEventArgs e)
    {
        if (sender is not ItemsControl list || !IsChatList(list, out var viewModel)
            || !TryGetDraggedChat(e, viewModel, out _))
            return;

        e.Effects = DragDropEffects.Move;
        e.Handled = true;
        AutoScroll(list, e.GetPosition(list));
    }

    private static void OnDrop(object sender, DragEventArgs e)
    {
        if (sender is not ItemsControl list || !IsChatList(list, out var viewModel)
            || !TryGetDraggedChat(e, viewModel, out var dragged))
            return;

        var (target, insertAfter) = ResolveTarget(list, viewModel, dragged, e.GetPosition(list));
        viewModel.MoveChat(dragged, target, insertAfter);
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
        ClearPressed();
    }

    private static bool TryGetDraggedChat(DragEventArgs e, MainViewModel viewModel, out ChatViewModel chat)
    {
        chat = null!;
        if (!e.Data.GetDataPresent(DragFormat)
            || e.Data.GetData(DragFormat) is not ChatViewModel candidate
            || !viewModel.Chats.Contains(candidate))
            return false;
        chat = candidate;
        return true;
    }

    /// <summary>Pick the closest midpoint in the dragged chat's own pinned/unpinned section.</summary>
    private static (ChatViewModel? Target, bool InsertAfter) ResolveTarget(
        ItemsControl list, MainViewModel viewModel, ChatViewModel dragged, Point point)
    {
        var rows = FindRowButtons(list, list, viewModel)
            .Where(row => row.DataContext is ChatViewModel chat && chat.Pinned == dragged.Pinned)
            .Select(row =>
            {
                try
                {
                    var top = row.TranslatePoint(new Point(0, 0), list).Y;
                    return (Row: row, Top: top);
                }
                catch (InvalidOperationException)
                {
                    return (Row: row, Top: double.NaN);
                }
            })
            .Where(entry => !double.IsNaN(entry.Top) && entry.Row.ActualHeight > 0)
            .OrderBy(entry => entry.Top)
            .ToList();

        if (rows.Count == 0) return (null, false);
        foreach (var entry in rows)
        {
            if (point.Y < entry.Top + entry.Row.ActualHeight / 2)
                return ((ChatViewModel)entry.Row.DataContext, false);
        }
        return ((ChatViewModel)rows[^1].Row.DataContext, true);
    }

    private static ButtonBase? FindRowButton(DependencyObject origin, ItemsControl list, MainViewModel viewModel)
    {
        ButtonBase? row = null;
        for (DependencyObject? current = origin; current is not null && !ReferenceEquals(current, list);
             current = ParentOf(current))
        {
            if (current is ButtonBase button && button.DataContext is ChatViewModel chat
                                             && viewModel.Chats.Contains(chat))
                row = button; // keep walking: the outermost chat button is the row, not its nested close button
        }
        return row;
    }

    private static IEnumerable<ButtonBase> FindRowButtons(
        DependencyObject root, ItemsControl list, MainViewModel viewModel)
    {
        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ButtonBase button && button.IsVisible
                                           && button.DataContext is ChatViewModel chat
                                           && viewModel.Chats.Contains(chat)
                                           && FindAncestor<ButtonBase>(ParentOf(button), list) is null)
                yield return button;

            foreach (var nested in FindRowButtons(child, list, viewModel))
                yield return nested;
        }
    }

    private static T? FindAncestor<T>(DependencyObject? start, DependencyObject? stop) where T : DependencyObject
    {
        for (var current = start; current is not null && !ReferenceEquals(current, stop); current = ParentOf(current))
            if (current is T match) return match;
        return null;
    }

    private static DependencyObject? ParentOf(DependencyObject child)
    {
        if (child is Visual or System.Windows.Media.Media3D.Visual3D)
            return VisualTreeHelper.GetParent(child);
        if (child is FrameworkContentElement content) return content.Parent;
        return LogicalTreeHelper.GetParent(child);
    }

    private static void AutoScroll(ItemsControl list, Point point)
    {
        var scroll = FindAncestor<ScrollViewer>(ParentOf(list), null);
        if (scroll is null) return;
        var inScroll = list.TranslatePoint(point, scroll);
        const double edge = 30;
        if (inScroll.Y < edge) scroll.LineUp();
        else if (inScroll.Y > scroll.ViewportHeight - edge) scroll.LineDown();
    }

    private static void ClearPressed()
    {
        _pressedChat = null;
        _pressedList = null;
    }
}