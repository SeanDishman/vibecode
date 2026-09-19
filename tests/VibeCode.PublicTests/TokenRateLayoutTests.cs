using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using VibeCode.UI;

internal static class TokenRateLayoutTests
{
    public static void Run()
    {
        var sourceDirectory = Environment.GetEnvironmentVariable("VIBECODE_TEST_SOURCE")
            ?? Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "../source/VibeCode.Desktop"));
        var source = Path.Combine(sourceDirectory, "MainWindow.xaml");
        var document = XDocument.Load(source);
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var labels = document.Descendants(wpf + "TextBlock")
            .Where(e => (string?)e.Attribute("Text") == "{Binding TokenRatesText}").ToArray();
        if (labels.Length != 2) throw new InvalidOperationException("Expected token rates in both chat headers.");

        var resources = Application.Current.Resources;
        resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(
            new Uri("/VibeCode;component/Themes/Dark.xaml", UriKind.Relative)));
        resources["BoolVis"] = new BooleanToVisibilityConverter();
        resources["ShowIf"] = new NonEmptyToVisibilityConverter();
        var data = new
        {
            BridgeLabel = "Codex 1", WorkingText = "working… 4m 50s", ShowWorkingText = true,
            ShowSupervisionStatus = false, SupervisionStatus = "", SupervisionAlert = false,
            TokensText = "7.8M (7.6M/113.4k)", TokenRatesText = "113.4k tok/s · 7.8M tok/min",
            TokenRatesToolTip = "Average over the last 60 seconds", HasTokens = true, HasTokenRates = true, CostText = "~$17.5991",
        };

        foreach (var label in labels)
        {
            var view = (TextBlock)XamlReader.Parse(label.ToString());
            view.DataContext = data;
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            view.GetBindingExpression(TextBlock.TextProperty)?.UpdateTarget();
            view.GetBindingExpression(UIElement.VisibilityProperty)?.UpdateTarget();
            view.Measure(new Size(747, 28));
            if (view.Text != data.TokenRatesText || view.Visibility != Visibility.Visible)
                throw new InvalidOperationException($"Header token-rate binding: '{view.Text}', {view.Visibility}, {view.GetBindingExpression(TextBlock.TextProperty)?.Status}.");
        }

        // Lay out the actual bridge title/stat controls at the width of the user's screenshot, allowing
        // 140 px for the right-side buttons and 12 px for the status dot. Use large rates to catch trimming.
        var bridge = labels.Single(e => (string?)e.Attribute("FontSize") == "9.5").Parent!;
        var header = new DockPanel { Width = 747, Height = 28, Background = new SolidColorBrush(Color.FromRgb(16, 16, 16)), DataContext = data };
        TextElement.SetFontFamily(header, (FontFamily)resources["Ui"]);
        TextElement.SetForeground(header, (Brush)resources["Text"]);
        var actions = new Border { Width = 140 };
        DockPanel.SetDock(actions, Dock.Right);
        header.Children.Add(actions);
        header.Children.Add(new Border { Width = 12 });
        var textBlocks = bridge.Elements(wpf + "TextBlock")
            .Select(e => (TextBlock)XamlReader.Parse(e.ToString())).ToArray();
        foreach (var block in textBlocks) header.Children.Add(block);
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        header.Measure(new Size(747, 28));
        header.Arrange(new Rect(0, 0, 747, 28));
        header.UpdateLayout();
        foreach (var text in new[] { data.TokenRatesText, data.CostText })
        {
            var block = textBlocks.Single(b => b.Text == text);
            var natural = new TextBlock { Text = block.Text, FontFamily = block.FontFamily, FontSize = block.FontSize, FontWeight = block.FontWeight };
            natural.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            if (block.ActualWidth + 0.5 < natural.DesiredSize.Width)
                throw new InvalidOperationException($"Header trims {text}: {block.ActualWidth:0.0} px available, {natural.DesiredSize.Width:0.0} px needed.");
        }

        var bitmap = new RenderTargetBitmap(747, 28, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(header);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(Environment.CurrentDirectory, "token-rate-header.png"));
        encoder.Save(output);
        Console.WriteLine("PASS: both header bindings and 747 px bridge layout; token-rate-header.png rendered");
        RenderFreshGrok(labels, resources);
    }

    private static void RenderFreshGrok(XElement[] labels, ResourceDictionary resources)
    {
        var chat = new ChatViewModel(Environment.CurrentDirectory, provider: "grok", accountId: "token-rate-simulation");
        try
        {
            chat.Status = "running";
            typeof(ChatViewModel).GetMethod("EstimateGrokTokenRate", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(chat, new object[] { new string('x', 320) });
            if (chat.HasTokens || !chat.HasTokenRates) throw new InvalidOperationException("Fresh Grok fixture must have rate estimates before any usage total.");
            var panel = new StackPanel { Width = 660, Background = new SolidColorBrush(Color.FromRgb(16, 16, 16)) };
            foreach (var label in labels)
            {
                var row = new DockPanel { Height = 30, DataContext = chat, LastChildFill = false };
                row.Children.Add(new TextBlock
                {
                    Text = (string?)label.Attribute("FontSize") == "9.5" ? "Bridge · working…" : "Grok · working…",
                    Width = 150, FontFamily = (FontFamily)resources["Mono"], FontSize = 11,
                    Foreground = (Brush)resources["Muted"], VerticalAlignment = VerticalAlignment.Center,
                });
                var rate = (TextBlock)XamlReader.Parse(label.ToString());
                rate.DataContext = chat;
                row.Children.Add(rate);
                row.Children.Add(new TextBlock
                {
                    Text = "Grok 26% weekly", Margin = new Thickness(12, 0, 0, 0),
                    FontFamily = (FontFamily)resources["Mono"], FontSize = 11,
                    Foreground = (Brush)resources["Muted"], VerticalAlignment = VerticalAlignment.Center,
                });
                panel.Children.Add(row);
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                rate.GetBindingExpression(TextBlock.TextProperty)?.UpdateTarget();
                rate.GetBindingExpression(UIElement.VisibilityProperty)?.UpdateTarget();
                if (rate.Visibility != Visibility.Visible || rate.Text != chat.TokenRatesText)
                    throw new InvalidOperationException($"Fresh Grok rate label: {rate.Visibility}, '{rate.Text}', expected '{chat.TokenRatesText}'.");
            }
            panel.Measure(new Size(660, 60));
            panel.Arrange(new Rect(0, 0, 660, 60));
            panel.UpdateLayout();
            foreach (var row in panel.Children.OfType<DockPanel>())
            {
                var rate = row.Children.OfType<TextBlock>().Single(b => b.Text == chat.TokenRatesText);
                var natural = new TextBlock { Text = rate.Text, FontFamily = rate.FontFamily, FontSize = rate.FontSize };
                natural.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                if (rate.ActualWidth + 0.5 < natural.DesiredSize.Width)
                    throw new InvalidOperationException("Fresh Grok rates are trimmed in a 660px footer.");
            }
            var bitmap = new RenderTargetBitmap(660, 60, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(panel);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(Environment.CurrentDirectory, "token-rate-grok-fresh-660.png"));
            encoder.Save(output);
            Console.WriteLine("PASS: fresh Grok rate bindings visible before usage; both controls rendered in 660px footer harness");
        }
        finally { chat.Close(); }
    }
}
