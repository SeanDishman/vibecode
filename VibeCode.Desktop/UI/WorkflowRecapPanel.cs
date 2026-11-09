using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VibeCode.Services;

namespace VibeCode.UI;

/// <summary>
/// The notice board over the wall: the Town Crier's recap of today, this month or this year. It takes the look of the
/// view behind it - a parchment scroll in the tavern, a whiteboard memo in the offices, a card over the graph and the
/// boards - and "Save postcard" renders the scene with the notice on it to a PNG you choose. Nothing is ever written
/// unless you click that button.
/// </summary>
public sealed class WorkflowRecapPanel : Border
{
    public enum Look { Scroll, Whiteboard, Card }

    private readonly TextBlock _heading = new() { FontSize = 12, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _title = new() { FontSize = 22, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 10) };
    private readonly UniformGrid _figures = new() { Columns = 5, Margin = new Thickness(0, 0, 0, 10) };
    private readonly StackPanel _lines = new();
    private readonly Grid _table = new() { Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _note = new() { FontSize = 10.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBlock _status = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly DockPanel _actions = new() { Margin = new Thickness(0, 14, 0, 0), LastChildFill = true };
    private readonly Button[] _periods;
    private Brush _ink = Brushes.Black, _muted = Brushes.Gray;
    private int _loading;

    /// <summary>Builds the notice for a period, off the UI thread. Supplied by the wall.</summary>
    public Func<RecapPeriod, Task<WorkflowRecap>>? Source { get; set; }
    /// <summary>What a postcard pictures: the wall's scene area, with this notice on it.</summary>
    public FrameworkElement? PostcardSource { get; set; }
    public WorkflowRecap? Shown { get; private set; }

    public WorkflowRecapPanel()
    {
        Visibility = Visibility.Collapsed;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        MaxWidth = 560;
        Margin = new Thickness(24);
        Padding = new Thickness(26, 20, 26, 18);
        Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.45 };
        AutomationProperties.SetName(this, "Notice board recap");
        _periods = [Chip("Today", RecapPeriod.Today), Chip("This month", RecapPeriod.Month), Chip("This year", RecapPeriod.Year)];
        var periods = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var chip in _periods) periods.Children.Add(chip);
        var save = new Button { Content = "Save postcard", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(8, 0, 0, 0),
            ToolTip = "Save this scene with the notice on it as a PNG" };
        AutomationProperties.SetName(save, "Save the notice board as a postcard image");
        save.Click += (_, _) => SavePostcard();
        var close = new Button { Content = "Close", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(8, 0, 0, 0) };
        AutomationProperties.SetName(close, "Close the notice board");
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        DockPanel.SetDock(save, Dock.Right);
        _actions.Children.Add(close);
        _actions.Children.Add(save);
        _actions.Children.Add(periods);
        var body = new StackPanel();
        body.Children.Add(_heading);
        body.Children.Add(_title);
        body.Children.Add(_figures);
        body.Children.Add(_lines);
        body.Children.Add(_table);
        body.Children.Add(_note);
        body.Children.Add(_actions);
        body.Children.Add(_status);
        Child = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    public bool IsOpen => Visibility == Visibility.Visible;

    /// <summary>Unroll the notice in the look of the view behind it, starting with today.</summary>
    public void Open(Look look)
    {
        ApplyLook(look);
        Visibility = Visibility.Visible;
        _ = Load(RecapPeriod.Today);
    }

    public void Close() => Visibility = Visibility.Collapsed;

    /// <summary>Restyle for another view while open: the wall switched views under the notice.</summary>
    public void ApplyLook(Look look)
    {
        BorderThickness = new Thickness(look switch { Look.Scroll => 3, Look.Whiteboard => 5, _ => 1 });
        CornerRadius = new CornerRadius(look switch { Look.Scroll => 4, Look.Whiteboard => 3, _ => 12 });
        if (look == Look.Card)
        {
            SetResourceReference(BackgroundProperty, "Bg1");
            SetResourceReference(BorderBrushProperty, "Border");
            _ink = TryFindResource("Text") as Brush ?? Brushes.White;
            _muted = TryFindResource("Muted") as Brush ?? Brushes.Gray;
        }
        else
        {
            // Parchment in the tavern, a whiteboard in the offices.
            var (paper, edge, ink, muted) = look == Look.Scroll
                ? (PixelArt.C("#F3E2B6"), PixelArt.C("#8B5E34"), PixelArt.C("#3B2A1A"), PixelArt.C("#7A5C3A"))
                : (PixelArt.C("#F6F8FA"), PixelArt.C("#9AA0A8"), PixelArt.C("#24324A"), PixelArt.C("#5A6478"));
            Background = Frozen(new SolidColorBrush(paper));
            BorderBrush = Frozen(new SolidColorBrush(edge));
            _ink = Frozen(new SolidColorBrush(ink));
            _muted = Frozen(new SolidColorBrush(muted));
        }
        _heading.Text = look switch
        {
            Look.Scroll => "HEAR YE! THE TOWN CRIER'S NOTICE",
            Look.Whiteboard => "MEMO · FROM THE FRONT DESK",
            _ => "NOTICE BOARD",
        };
        _title.Foreground = _ink;
        _heading.Foreground = _note.Foreground = _status.Foreground = _muted;
        if (Shown is { } recap) Show(recap);
    }

    private Button Chip(string label, RecapPeriod period)
    {
        var chip = new Button { Content = label, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 6, 0) };
        AutomationProperties.SetName(chip, "Show the notice for " + label.ToLowerInvariant());
        chip.Click += (_, _) => _ = Load(period);
        return chip;
    }

    private async Task Load(RecapPeriod period)
    {
        if (Source is null) return;
        var ticket = ++_loading;
        _status.Text = "Reading the ledgers…";
        foreach (var chip in _periods) chip.FontWeight = chip == _periods[(int)period] ? FontWeights.Bold : FontWeights.Normal;
        try
        {
            var recap = await Source(period);
            if (ticket != _loading) return;
            _status.Text = "";
            Show(recap);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (ticket == _loading) _status.Text = "Couldn't read the history: " + ex.Message;
        }
    }

    /// <summary>Fill the notice from a recap. Public so the tests can show a fixture without reading history.</summary>
    public void Show(WorkflowRecap recap)
    {
        Shown = recap;
        _title.Text = recap.Title;
        _figures.Children.Clear();
        Figure(recap.Turns.ToString("N0"), recap.Turns == 1 ? "turn" : "turns");
        Figure(UsageAnalytics.Tokens(recap.Tokens), "tokens");
        Figure(UsageAnalytics.Money(recap.Cost), "est. cost");
        Figure(recap.Files.ToString("N0"), recap.Files == 1 ? "file" : "files");
        Figure($"+{recap.Added:N0} −{recap.Removed:N0}", "lines");
        _lines.Children.Clear();
        if (recap.Busiest is { } busiest) Line("Busiest building", $"{busiest.Name} · {busiest.Turns:N0} turns · {UsageAnalytics.Tokens(busiest.Tokens)} tokens");
        if (recap.LongestTurn is { } longest) Line("Longest turn", longest + " (seen on the wall)");
        if (recap.BiggestTurn is { } biggest) Line("Biggest turn", biggest);
        Line("Came to the door for you", recap.DoorVisits == 1 ? "once (while the wall was open)" : $"{recap.DoorVisits:N0} times (while the wall was open)");
        BuildTable(recap.Projects.Take(6).ToList());
        _note.Text = "Costs are estimates at list price unless the provider reported them. Files and lines count Bridge agents' edits"
            + (recap.EditsPartial ? "; the edit ledger keeps a week, so this covers the last seven days." : ".");
    }

    private void Figure(string value, string caption)
    {
        var cell = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        cell.Children.Add(new TextBlock { Text = value, FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = _ink, TextTrimming = TextTrimming.CharacterEllipsis });
        cell.Children.Add(new TextBlock { Text = caption, FontSize = 10.5, Foreground = _muted });
        _figures.Children.Add(cell);
    }

    private void Line(string label, string value)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Margin = new Thickness(0, 0, 0, 4), Foreground = _ink };
        text.Inlines.Add(new System.Windows.Documents.Run(label + ": ") { FontWeight = FontWeights.SemiBold });
        text.Inlines.Add(new System.Windows.Documents.Run(value));
        _lines.Children.Add(text);
    }

    private void BuildTable(IReadOnlyList<RecapProject> rows)
    {
        _table.Children.Clear();
        _table.RowDefinitions.Clear();
        _table.ColumnDefinitions.Clear();
        if (rows.Count == 0) return;
        foreach (var width in new[] { new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto })
            _table.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        string[] header = ["Project", "Turns", "Tokens", "Cost", "Lines"];
        for (var r = 0; r <= rows.Count; r++)
        {
            _table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var row = r == 0 ? null : rows[r - 1];
            string[] cells = row is null ? header
                : [row.Name, row.Turns.ToString("N0"), UsageAnalytics.Tokens(row.Tokens), UsageAnalytics.Money(row.Cost),
                   row.Added + row.Removed > 0 ? $"+{row.Added:N0} −{row.Removed:N0}" : "–"];
            for (var c = 0; c < cells.Length; c++)
            {
                var cell = new TextBlock
                {
                    Text = cells[c], FontSize = r == 0 ? 10.5 : 12, Foreground = r == 0 ? _muted : _ink,
                    Margin = new Thickness(c == 0 ? 0 : 14, 0, 0, 3), TextTrimming = TextTrimming.CharacterEllipsis,
                    HorizontalAlignment = c == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                };
                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                _table.Children.Add(cell);
            }
        }
    }

    private void SavePostcard()
    {
        if (PostcardSource is not { } scene) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save postcard", Filter = "PNG image|*.png", DefaultExt = ".png",
            FileName = $"VibeCode postcard {DateTime.Now:yyyy-MM-dd}.png",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            SavePostcard(scene, dialog.FileName);
            _status.Text = "Saved " + Path.GetFileName(dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _status.Text = "Couldn't save the postcard: " + ex.Message;
        }
    }

    /// <summary>Render <paramref name="scene"/> - the wall's scene with this notice on it, its buttons hidden - to a PNG
    /// at <paramref name="path"/>, at the display's own resolution.</summary>
    public void SavePostcard(FrameworkElement scene, string path)
    {
        var hidden = new[] { _actions, (FrameworkElement)_status };
        foreach (var element in hidden) element.Visibility = Visibility.Collapsed;
        try
        {
            scene.UpdateLayout();
            var dpi = VisualTreeHelper.GetDpi(scene);
            var width = Math.Max(1, (int)Math.Ceiling(scene.ActualWidth * dpi.DpiScaleX));
            var height = Math.Max(1, (int)Math.Ceiling(scene.ActualHeight * dpi.DpiScaleY));
            var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            // Through a brush, so the picture starts at the scene's own corner wherever it sits in the window.
            var copy = new DrawingVisual();
            using (var dc = copy.RenderOpen())
                dc.DrawRectangle(new VisualBrush(scene), null, new Rect(0, 0, scene.ActualWidth, scene.ActualHeight));
            bitmap.Render(copy);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(path);
            encoder.Save(stream);
        }
        finally
        {
            foreach (var element in hidden) element.Visibility = Visibility.Visible;
        }
    }

    private static T Frozen<T>(T freezable) where T : Freezable { freezable.Freeze(); return freezable; }
}
