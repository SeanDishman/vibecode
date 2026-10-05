using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VibeCode.Services;
using VibeCode.UI;

/// <summary>Actual renderer frames, a complete live WPF clock cycle, and fractional-DPI evidence.</summary>
internal static class MotionReview
{
    private static readonly DependencyProperty Phase = (DependencyProperty)typeof(OrbitSpinner)
        .GetField("PhaseProperty", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;

    internal static void Run()
    {
        var report = new StringBuilder("# Thinking orb rendering review\n\n");
        report.AppendLine("One full six-second cycle sampled at 30 frames per second, at the shipped 22px size and 100%, 125%, 150%, and 200% display scales.\n");
        report.AppendLine("| Theme | Style | DPI scale | Visible pixels (min/max) | Brightness change between adjacent frames (max) | Clipped frames |\n|---|---|---|---|---|---|");
        foreach (var theme in new[] { "Dark", "Cli" })
        {
            Program.Theme(theme);
            var ink = (Brush)Application.Current.Resources["Accent"];
            foreach (var option in ThinkingOrbStyles.All)
            {
                foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
                {
                    var orb = Orb(option.Style, ink);
                    var minimum = int.MaxValue;
                    var maximum = 0;
                    var clipped = 0;
                    var energy = new List<double>();
                    // Reuse the native render surface; thousands of tiny temporary surfaces can exhaust
                    // WPF's graphics handles before their managed wrappers trigger a collection.
                    var buffer = new RenderTargetBitmap((int)Math.Ceiling(22 * scale), (int)Math.Ceiling(22 * scale),
                        96 * scale, 96 * scale, PixelFormats.Pbgra32);
                    for (var frame = 0; frame < 180; frame++)
                    {
                        var bitmap = Frame(orb, frame / 180.0, scale, buffer);
                        var pixels = Pixels(bitmap);
                        var visible = 0;
                        var sum = 0.0;
                        var touchesEdge = false;
                        for (var y = 0; y < bitmap.PixelHeight; y++)
                            for (var x = 0; x < bitmap.PixelWidth; x++)
                            {
                                var alpha = pixels[(y * bitmap.PixelWidth + x) * 4 + 3];
                                sum += alpha;
                                if (alpha <= 15) continue;
                                visible++;
                                if (x == 0 || x == bitmap.PixelWidth - 1 || y == 0 || y == bitmap.PixelHeight - 1)
                                    touchesEdge = true;
                            }
                        minimum = Math.Min(minimum, visible);
                        maximum = Math.Max(maximum, visible);
                        clipped += touchesEdge ? 1 : 0;
                        energy.Add(sum);
                    }
                    Program.Check(minimum >= 20, $"{option.Name} never disappears in {theme} at {scale:P0}");
                    Program.Check(clipped == 0, $"{option.Name} is never clipped in {theme} at {scale:P0}");
                    var largestStep = Enumerable.Range(0, energy.Count).Max(index =>
                        Math.Abs(energy[(index + 1) % energy.Count] - energy[index]) / energy.Average());
                    report.AppendLine(FormattableString.Invariant($"| {theme} | {option.Name} | {scale:P0} | {minimum}/{maximum} | {largestStep:P1} | {clipped} |"));
                }
            }
            ActualSizeSheet(theme, ink);
            DpiSheet(theme, ink);
            LiveCycle(theme, ink);
        }
        Program.Theme("Dark");
        File.WriteAllText(Path.Combine(Program.OutputDirectory, "motion-review.md"), report.ToString());
    }

    private static OrbitSpinner Orb(ThinkingOrbStyle style, Brush ink, bool animate = false)
    {
        var orb = new OrbitSpinner { Width = 22, Height = 22, StyleKind = style, Ink = ink, Spin = animate };
        orb.Measure(new Size(22, 22));
        orb.Arrange(new Rect(0, 0, 22, 22));
        orb.UpdateLayout();
        return orb;
    }

    private static RenderTargetBitmap Frame(OrbitSpinner orb, double? phase, double scale = 1, RenderTargetBitmap? buffer = null)
    {
        if (phase is { } value) orb.SetValue(Phase, value);
        orb.UpdateLayout();
        var bitmap = buffer ?? new RenderTargetBitmap((int)Math.Ceiling(22 * scale), (int)Math.Ceiling(22 * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        if (buffer is not null) bitmap.Clear();
        if (orb.IsLoaded)
        {
            // Copy the control's local drawing so the parent's layout offset and viewport clip do
            // not turn a perfectly rendered, off-screen live control into an empty 22px snapshot.
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
                drawing.DrawDrawing(VisualTreeHelper.GetDrawing(orb));
            bitmap.Render(visual);
        }
        else bitmap.Render(orb);
        return bitmap;
    }

    private static void ActualSizeSheet(string theme, Brush ink)
    {
        var host = Host();
        host.Children.Add(Label("22px chat size · samples across the full animation", 15));
        foreach (var option in ThinkingOrbStyles.All)
        {
            var row = Row(option.Name);
            var orb = Orb(option.Style, ink);
            for (var frame = 0; frame < 16; frame++)
                row.Children.Add(Picture(Frame(orb, frame / 16.0), 22));
            row.Children.Add(Label("thinking…", 12));
            host.Children.Add(row);
        }
        Save(host, $"chat-size-{theme.ToLowerInvariant()}.png");
    }

    private static void DpiSheet(string theme, Brush ink)
    {
        var host = Host();
        host.Children.Add(Label("100% / 125% / 150% / 200% display scaling · native device pixels", 15));
        foreach (var option in ThinkingOrbStyles.All)
        {
            var row = Row(option.Name);
            var orb = Orb(option.Style, ink);
            foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
                for (var frame = 0; frame < 4; frame++)
                {
                    var bitmap = Frame(orb, 0.07 + frame / 4.0, scale);
                    row.Children.Add(Picture(bitmap, bitmap.PixelWidth));
                }
            host.Children.Add(row);
        }
        Save(host, $"display-scales-{theme.ToLowerInvariant()}.png");
    }

    private static void LiveCycle(string theme, Brush ink)
    {
        var host = Host();
        var orbs = ThinkingOrbStyles.All.Select(option => Orb(option.Style, ink, animate: true)).ToArray();
        foreach (var orb in orbs) host.Children.Add(orb);
        var window = new Window
        {
            Left = 6100, Top = 300, Width = 150, Height = 320,
            ShowActivated = false, ShowInTaskbar = false, Content = host,
        };
        var captures = orbs.Select(_ => new List<BitmapSource>()).ToArray();
        var phases = orbs.Select(_ => new List<double>()).ToArray();
        // Keep WPF's render clock awake while the review window is off-screen; the compositor normally
        // suspends frame ticks for occluded windows, which is useful for the app but defeats this probe.
        EventHandler keepRendering = (_, _) => { };
        CompositionTarget.Rendering += keepRendering;
        window.Show();
        Program.Drain();
        Program.Check(orbs.All(orb => orb.HasAnimatedProperties), "live review clocks start when loaded");
        var dispatcherFrame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMilliseconds(333) };
        timer.Tick += (_, _) =>
        {
            for (var index = 0; index < orbs.Length; index++)
            {
                var capture = Frame(orbs[index], null);
                Program.Check(Pixels(capture).Where((_, channel) => channel % 4 == 3).Count(alpha => alpha > 15) >= 20,
                    $"{ThinkingOrbStyles.All[index].Name} is actually rendered in each live {theme} capture: "
                    + $"size {orbs[index].ActualWidth}/{orbs[index].ActualHeight}, visible {orbs[index].IsVisible}, "
                    + $"drawing {VisualTreeHelper.GetDrawing(orbs[index])?.Bounds}");
                captures[index].Add(capture);
                phases[index].Add((double)orbs[index].GetValue(Phase));
            }
            if (captures[0].Count < 19) return;
            timer.Stop();
            dispatcherFrame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(dispatcherFrame);
        CompositionTarget.Rendering -= keepRendering;
        window.Close();
        var sheet = Host();
        sheet.Children.Add(Label("Live WPF clock · one complete six-second cycle", 15));
        for (var index = 0; index < orbs.Length; index++)
        {
            Program.Check(phases[index].Zip(phases[index].Skip(1), (a, b) => Math.Abs(a - b) > 0.001).All(advanced => advanced),
                $"{ThinkingOrbStyles.All[index].Name} live clock advances in {theme}: {string.Join(", ", phases[index].Select(value => value.ToString("0.000")))}");
            Program.Check(phases[index].Zip(phases[index].Skip(1), (a, b) => b < a).Any(wrapped => wrapped),
                $"{ThinkingOrbStyles.All[index].Name} completes a live loop in {theme}");
            Program.Check(!orbs[index].HasAnimatedProperties, "closing the live review releases every animation");
            var row = Row(ThinkingOrbStyles.All[index].Name);
            foreach (var capture in captures[index]) row.Children.Add(Picture(capture, 22));
            sheet.Children.Add(row);
        }
        Save(sheet, $"live-cycle-{theme.ToLowerInvariant()}.png");
    }

    private static StackPanel Host() => new() { Background = (Brush)Application.Current.Resources["Bg0"] };
    private static StackPanel Row(string name)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 11, 14, 11) };
        var nameLabel = Label(name, 13);
        nameLabel.Width = 96;
        row.Children.Add(nameLabel);
        return row;
    }
    private static TextBlock Label(string text, int fontSize) => new()
    {
        Text = text, FontSize = fontSize, FontFamily = (FontFamily)Application.Current.Resources["Ui"],
        Foreground = (Brush)Application.Current.Resources["Text"], VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 4, 0, 4),
    };
    private static Image Picture(BitmapSource frame, int size) => new()
    {
        Source = frame, Width = size, Height = size, Margin = new Thickness(7, 0, 7, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static void Save(FrameworkElement visual, string file)
    {
        visual.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        visual.Arrange(new Rect(new Point(), visual.DesiredSize));
        visual.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth), (int)Math.Ceiling(visual.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(Program.OutputDirectory, file));
        encoder.Save(output);
    }
    private static byte[] Pixels(BitmapSource frame)
    {
        var pixels = new byte[frame.PixelWidth * frame.PixelHeight * 4];
        frame.CopyPixels(pixels, frame.PixelWidth * 4, 0);
        return pixels;
    }
}
