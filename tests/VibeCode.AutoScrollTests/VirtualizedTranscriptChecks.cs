using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using VibeCode.UI;

internal static partial class Program
{
    private static readonly Queue<string> ScrollEvents = new();

    private static void VerifyVirtualizedTranscript(Grid host, Window window)
    {
        var rows = new ObservableCollection<TranscriptRow>();
        for (var i = 0; i < 600; i++)
            rows.Add(new TranscriptRow { Height = i % 11 == 0 ? 480 : 32 + i % 5 * 18 });
        var hidden = new TranscriptRow { Visible = false, Height = 320 };
        rows.Insert(rows.Count - 1, hidden);
        var activity = new ListCollectionView(rows) { Filter = item => ((TranscriptRow)item).Visible };
        var list = CreateTranscript(activity);
        AutoScroll.SetEnabled(list, true);
        host.Children.Add(list);
        Pump("virtualized transcript loads without starving the dispatcher");
        var scroll = FindScrollViewer(list)!;
        scroll.ScrollChanged += (_, e) =>
        {
            if (!ReferenceEquals(e.OriginalSource, scroll)) return;
            ScrollEvents.Enqueue($"offset={e.VerticalOffset:0.##} delta={e.VerticalChange:0.##} " +
                $"extent={e.ExtentHeight:0.##} delta={e.ExtentHeightChange:0.##} viewport={e.ViewportHeight:0.##}");
            while (ScrollEvents.Count > 12) ScrollEvents.Dequeue();
        };
        Check(AtBottom(scroll), $"long virtualized transcript starts at the actual bottom ({Position(scroll)})");
        Check(list.ItemContainerGenerator.ContainerFromIndex(0) is null,
            "transcript regression exercises recycling virtualization");

        // A scrollbar/wheel arrival requests a finite offset, unlike ScrollToBottom's infinite request.
        scroll.ScrollToVerticalOffset(scroll.ScrollableHeight);
        Pump("manual arrival at the bottom settles");
        for (var burst = 0; burst < 12; burst++)
        {
            for (var i = 0; i < 3; i++) rows.Add(new TranscriptRow { Height = i == 0 ? 650 : 36 });
            Pump("interleaved agent output settles");
            Check(AtBottom(scroll), $"new virtualized messages follow after a manual arrival, burst {burst} ({Position(scroll)})");
        }

        for (var delta = 0; delta < 8; delta++)
        {
            rows[^1].Height += 80;
            Pump("streaming reply growth settles");
            Check(AtBottom(scroll), $"streaming a tall reply stays pinned, delta {delta} ({Position(scroll)})");
        }

        hidden.Visible = true;
        activity.Refresh();
        Pump("previously empty activity appears through a filter refresh");
        Check(AtBottom(scroll), $"filter refresh preserves following new content ({Position(scroll)})");
        rows.Add(new TranscriptRow { Height = 420 });
        Pump("output after filter refresh settles");
        Check(AtBottom(scroll), $"following continues after activity refresh ({Position(scroll)})");

        scroll.ScrollToVerticalOffset(scroll.ScrollableHeight);
        Pump("finite bottom before viewport change");
        window.Height -= 100;
        Pump("composer or window reducing the viewport settles");
        Check(AtBottom(scroll), $"a smaller viewport stays attached to the newest output ({Position(scroll)})");
        window.Height += 100;
        Pump("restoring viewport height settles");
        Check(AtBottom(scroll), $"a larger viewport stays at the bottom ({Position(scroll)})");

        // Queue user scrolling and growth together so WPF reports both changes in one layout pass.
        scroll.ScrollToVerticalOffset(1200);
        rows.Add(new TranscriptRow { Height = 220 });
        Pump("scrolling up during incoming output settles");
        Check(!AtBottom(scroll) && scroll.VerticalOffset < 2000,
            $"scrolling upward wins over simultaneous output ({Position(scroll)})");
        var readingOffset = scroll.VerticalOffset;
        rows.Add(new TranscriptRow { Height = 550 });
        rows[^2].Height += 160;
        Pump("new and streaming output while reading settles");
        Check(Math.Abs(scroll.VerticalOffset - readingOffset) < 1,
            $"new output preserves the reader's position ({Position(scroll)})");
        window.Height -= 60;
        Pump("viewport changes while reading settle");
        Check(!AtBottom(scroll), "resizing while reading does not jump to the newest output");
        list.Visibility = Visibility.Collapsed;
        Pump("virtualized reader hides");
        rows.Add(new TranscriptRow { Height = 360 });
        list.Visibility = Visibility.Visible;
        Pump("virtualized reader reappears");
        Check(!AtBottom(scroll), "hidden output preserves a reader's detached position");
        window.Height += 60;
        Pump("reader viewport returns to its original size");

        scroll.ScrollToBottom();
        Pump("reader returns to live output");
        Check(AtBottom(scroll), "returning to the bottom reattaches following");
        rows.Add(new TranscriptRow { Height = 720 });
        Pump("reattached incoming output settles");
        Check(AtBottom(scroll), "following works after reading older messages");
        list.Visibility = Visibility.Collapsed;
        Pump("live transcript hides");
        rows.Add(new TranscriptRow { Height = 360 });
        list.Visibility = Visibility.Visible;
        Pump("live transcript reappears");
        Check(AtBottom(scroll), "returning to a live transcript follows output received while hidden");
    }

    private static ListBox CreateTranscript(ListCollectionView activity, ListBox? list = null)
    {
        var row = new FrameworkElementFactory(typeof(ContentControl));
        row.SetBinding(FrameworkElement.HeightProperty, new Binding(nameof(TranscriptRow.Height)));
        row.SetBinding(ContentControl.ContentProperty, new Binding(nameof(TranscriptRow.Body)));
        list ??= new ListBox();
        list.ItemsSource = activity;
        list.BorderThickness = new Thickness(0);
        list.Padding = new Thickness(0);
        list.ItemTemplate = new DataTemplate { VisualTree = row };
        list.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)));
        var container = new Style(typeof(ListBoxItem));
        container.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        container.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        container.Setters.Add(new Setter(UIElement.FocusableProperty, false));
        list.ItemContainerStyle = container;
        ScrollViewer.SetCanContentScroll(list, true);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(list, ScrollUnit.Pixel);
        VirtualizingPanel.SetCacheLength(list, new VirtualizationCacheLength(1));
        VirtualizingPanel.SetCacheLengthUnit(list, VirtualizationCacheLengthUnit.Page);
        return list;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer scroll) return scroll;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }

    private static string Position(ScrollViewer scroll) =>
        $"offset {scroll.VerticalOffset:0.##}, bottom {scroll.ScrollableHeight:0.##}";

    private sealed class TranscriptRow : INotifyPropertyChanged
    {
        private double _height;
        public double Height
        {
            get => _height;
            set { _height = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Height))); }
        }
        public bool Visible { get; set; } = true;
        public UIElement? Body { get; set; }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
