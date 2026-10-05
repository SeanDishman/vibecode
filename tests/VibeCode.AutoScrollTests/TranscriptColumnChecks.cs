using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;

internal static partial class Program
{
    /// <summary>The main transcript is one fixed, centred reading column whatever rows happen to be realized. A
    /// content-sized presenter was centred at the width of the rows IN VIEW while the VirtualizingStackPanel laid
    /// every realized row - the off-screen cache page too - at the widest one's width, so a chat opened on tool
    /// cards and a 600px prompt under a long reply sat ~100px right of the composer.</summary>
    private static void VerifyTranscriptColumn(Grid host, Window window, ListBox list)
    {
        var originalSize = new Size(window.Width, window.Height);
        try
        {
            window.Width = 1400;
            window.Height = 700;
            var wide = new ObservableCollection<TranscriptRow>();
            for (var i = 0; i < 30; i++) wide.Add(Row(120, Reply()));
            CreateTranscript(new ListCollectionView(wide), list);
            host.Children.Add(list);
            Pump("transcript of wide replies loads in a wide window");
            FindScrollViewer(list)!.ScrollToBottom();
            Pump("wide transcript settles at the bottom");
            CheckColumn(list, "chat of wide replies");

            // The reported chat: a long reply just above the fold, then only narrow rows in view.
            var narrow = new ObservableCollection<TranscriptRow> { Row(200, Reply()) };
            for (var i = 0; i < 20; i++) narrow.Add(Row(40, Card()));
            narrow.Add(Row(60, new Border { Width = 600, HorizontalAlignment = HorizontalAlignment.Right }));
            for (var i = 0; i < 3; i++) narrow.Add(Row(40, Card()));
            list.ItemsSource = new ListCollectionView(narrow);
            Pump("switching to a chat of tool cards and a prompt loads");
            FindScrollViewer(list)!.ScrollToBottom();
            Pump("the cache page above the narrow rows is realized");
            CheckColumn(list, "chat opened on narrow rows under a long reply");
        }
        finally
        {
            host.Children.Clear();
            window.Width = originalSize.Width;
            window.Height = originalSize.Height;
        }

        static TranscriptRow Row(double height, UIElement body) => new() { Height = height, Body = body };
        static TextBlock Reply() => new()
        {
            Text = string.Concat(Enumerable.Repeat("A long assistant reply wraps across the whole reading column. ", 14)),
            TextWrapping = TextWrapping.Wrap,
        };
        static Border Card() => new() { Width = 240, HorizontalAlignment = HorizontalAlignment.Left };
    }

    private static void CheckColumn(ListBox list, string chat)
    {
        var viewport = FindVisual<ScrollContentPresenter>(FindScrollViewer(list)!)!;
        var presenter = FindVisual<ItemsPresenter>(list)!;
        var column = presenter.TransformToAncestor(viewport).TransformBounds(new Rect(presenter.RenderSize));
        var measure = Math.Min(presenter.MaxWidth, viewport.ActualWidth - presenter.Margin.Left - presenter.Margin.Right);
        Check(Math.Abs(column.Width - measure) < 1,
            $"{chat}: column keeps its full {measure:0}px measure (was {column.Width:0}px)");
        Check(Math.Abs(column.Left - (viewport.ActualWidth - column.Right)) < 1,
            $"{chat}: column is centred (x {column.Left:0}..{column.Right:0} of {viewport.ActualWidth:0})");
        for (var i = 0; i < list.Items.Count; i++)
        {
            if (list.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement row || !row.IsVisible) continue;
            var bounds = row.TransformToAncestor(viewport).TransformBounds(new Rect(row.RenderSize));
            Check(bounds.Left >= column.Left - 0.5 && bounds.Right <= column.Right + 0.5,
                $"{chat}: row {i} stays inside the column (row x {bounds.Left:0}..{bounds.Right:0}, " +
                $"column x {column.Left:0}..{column.Right:0})");
        }
    }
}
