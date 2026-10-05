using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VibeCode;
using VibeCode.Services;
using VibeCode.UI;

internal static class OrbPreview
{
    internal static void RenderStyleSheet(string theme)
    {
        var resources = Application.Current.Resources;
        var host = new StackPanel { Width = 700, Background = (Brush)resources["Bg0"] };
        foreach (var option in ThinkingOrbStyles.All)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 7, 14, 7) };
            row.Children.Add(new TextBlock
            {
                Text = option.Name, Width = 98, FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)resources["Text"], FontFamily = (FontFamily)resources["Ui"],
            });
            foreach (var phase in new[] { 0.0, 0.12, 0.25, 0.37, 0.5, 0.62, 0.75, 0.87 })
            {
                row.Children.Add(new Image
                {
                    Source = Program.Frame(option.Style, 44, (Brush)resources["Accent"], phase),
                    Width = 44, Height = 44, Margin = new Thickness(9, 0, 9, 0),
                });
            }
            row.Children.Add(new Image
            {
                Source = Program.Frame(option.Style, 22, (Brush)resources["Accent"], 0.137), Width = 22, Height = 22,
                Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            });
            host.Children.Add(row);
        }
        Render(host, 700, $"orbs-{theme.ToLowerInvariant()}.png");
    }

    internal static void RenderAppearance(SettingsWindow window, string theme, int width)
    {
        // Reparse the shipped setting card with its actual templates and palette at each width/theme.
        // A new window is necessary because this app's window-local styles resolve StaticResource on load.
        var captureWindow = new SettingsWindow { ShowActivated = false, Width = width, Height = 1100 };
        captureWindow.Show();
        ((ListBox)captureWindow.FindName("Rail")).SelectedIndex = 1;
        Program.Drain();
        captureWindow.UpdateLayout();
        var card = (SettingCard)captureWindow.FindName("ThinkingOrbSetting");
        var picker = (ListBox)captureWindow.FindName("ThinkingOrbPicker");
        foreach (ListBoxItem item in Program.Descendants<ListBoxItem>(picker))
        {
            Program.Check(item.ActualWidth > 110 && item.ActualHeight >= 48, $"orb choice is usable at {width}px in {theme}");
            foreach (var text in Program.Descendants<TextBlock>(item).Where(text => text.Visibility == Visibility.Visible && text.Text.Any(char.IsLetter)))
                Program.Check(text.ActualWidth > 50 && text.ActualHeight > 10, $"orb label is readable at {width}px in {theme}");
        }
        Render(card, card.ActualWidth, $"appearance-{theme.ToLowerInvariant()}-{width}.png", laidOut: true);
        if (width == 740) Render(card, card.ActualWidth, $"appearance-{theme.ToLowerInvariant()}-{width}-200pct.png", laidOut: true, scale: 2);
        captureWindow.Close();
    }

    private static void Render(FrameworkElement target, double width, string file, bool laidOut = false, int scale = 1)
    {
        if (!laidOut)
        {
            target.Measure(new Size(width, double.PositiveInfinity));
            target.Arrange(new Rect(0, 0, width, Math.Ceiling(target.DesiredSize.Height)));
            target.UpdateLayout();
        }
        var bounds = new Rect(0, 0, width, target.ActualHeight);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle((Brush)Application.Current.Resources["Bg1"], null, new Rect(0, 0, width, target.ActualHeight));
            drawing.DrawRectangle(new VisualBrush(target), null, bounds);
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(target.ActualHeight * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(Program.OutputDirectory, file));
        encoder.Save(output);
    }
}
