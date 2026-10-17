using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    private static void VerifyLinkMenus(Window window, ProbeMarkdownView viewer, string root)
    {
        const string url = "http://localhost:5173/play?mode=jungle&sound=1#scene";
        var project = Path.Combine(root, "Project with spaces");
        var directory = Path.Combine(project, "src");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "main.cs");
        File.WriteAllText(file, "// A local test file.\n");
        viewer.LinkBaseDirectory = project;
        viewer.Markdown = $"It's running: **[Play Sol Jungle]({url})**. Click the scene for mouse look and sound.\n\n" +
            "[Source file](src/main.cs:42) and [Project directory](src/).\n\n" +
            "| Resource | Location |\n| --- | --- |\n| docs | [Documentation](https://example.com/docs) |";
        window.Content = viewer;
        Pump();
        var links = Links(viewer.Document!).ToArray();
        string? copied = null;
        viewer.ContextMenu = BuildMenu(viewer, value => copied = value);

        // Routed input reaches the same class handlers as a normal right click, including a nested text Run.
        RightClick(viewer, links[0].Inlines.FirstInline!);
        Check(Headers(viewer).Take(2).SequenceEqual(["Open link", "Copy URL"]), "friendly labels have web actions first");
        Check(Headers(viewer).Contains("Copy") && Headers(viewer).Contains("Select all"), "link actions retain text commands");
        InvokeMenu(viewer, "Copy URL");
        Check(copied == url, "Copy URL copies destination, including port, query and fragment, not friendly label");
        InvokeMenu(viewer, "Open link");
        Check(viewer.Opened == new TranscriptLink(TranscriptLinkKind.Web, url), "menu Open uses normal navigation");

        viewer.Selection.Select(links[0].ContentStart, links[0].ContentEnd);
        var selected = viewer.Selection.Text;
        RightClick(viewer, links[0].Inlines.FirstInline!);
        InvokeMenu(viewer, "Copy URL");
        Check(copied == url && viewer.Selection.Text == selected, "right click on a selected label preserves selection and URL");
        Copy(viewer, "normal Copy still copies selected link text, not its URL");
        Check(_copied == "Play Sol Jungle", "plain Copy retains the friendly label");

        // Selection overlays can make the event originate on the document host: hit-test the rendered text.
        var hitPoint = LinkPoint(viewer, links[0]);
        RefreshLinkMenu(viewer, null, hitPoint);
        InvokeMenu(viewer, "Copy URL");
        Check(copied == url, "selected link resolves from rendered pointer position without an event-source Hyperlink");
        KeyboardMenu(viewer);
        InvokeMenu(viewer, "Copy URL");
        Check(copied == url, "Menu key on a selected link exposes the same destination");
        viewer.Selection.Select(links[0].ContentStart.GetPositionAtOffset(2)!, links[0].ContentStart.GetPositionAtOffset(7)!);
        KeyboardMenu(viewer);
        InvokeMenu(viewer, "Copy URL");
        Check(copied == url, "partial link-label selection resolves the full destination");

        RightClick(viewer, links[1]);
        Check(Headers(viewer).Take(3).SequenceEqual(["Open file", "Copy path", "Copy directory"]), "local files have path and directory actions");
        InvokeMenu(viewer, "Copy path");
        Check(copied == file, "relative file path resolves against the chat project and excludes editor line notation");
        InvokeMenu(viewer, "Copy directory");
        Check(copied == directory, "Copy directory resolves the file's containing folder");
        InvokeMenu(viewer, "Open file");
        Check(viewer.Opened == new TranscriptLink(TranscriptLinkKind.LocalPath, file, 42), "opening from the menu preserves line numbers");
        Check(!Headers(viewer).Contains("Copy URL"), "opening another link removes stale web actions");

        RightClick(viewer, links[2]);
        Check(Headers(viewer).Contains("Open directory"), "directory links are identified as directories");
        InvokeMenu(viewer, "Copy directory");
        Check(Path.TrimEndingDirectorySeparator(copied!) == directory, "Copy directory copies the linked folder itself");
        InvokeMenu(viewer, "Open directory");
        Check(Path.TrimEndingDirectorySeparator(viewer.Opened!.Address) == directory, "directory navigation retains its target");

        RightClick(viewer, links[3]);
        InvokeMenu(viewer, "Copy URL");
        Check(copied == "https://example.com/docs", "links nested in tables get the same menu");
        // No link menu should leak into plain text, a broad selection, or a replacement document.
        viewer.Selection.Select(viewer.Document!.ContentStart, viewer.Document.ContentEnd);
        KeyboardMenu(viewer);
        Check(!Headers(viewer).Contains("Copy URL") && !Headers(viewer).Contains("Copy path"), "a multiple-link selection does not choose an arbitrary URL");
        RefreshLinkMenu(viewer, viewer, new Point(viewer.ActualWidth - 2, viewer.ActualHeight - 2));
        Check(!Headers(viewer).Contains("Open link"), "right-clicking blank space has no stale link actions");

        var oldLink = links[0];
        viewer.Markdown = "[New streamed link](https://example.com/new)";
        Pump();
        RightClick(viewer, Links(viewer.Document!).Single());
        var snapshot = viewer.ContextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Copy URL"));
        viewer.Markdown = "[Later streamed link](https://example.com/later)";
        Pump();
        snapshot.Command.Execute(null);
        Check(copied == "https://example.com/new", "an open menu keeps the destination it showed despite streamed replacements");
        RightClick(viewer, Links(viewer.Document!).Single());
        InvokeMenu(viewer, "Copy URL");
        Check(copied == "https://example.com/later", "reopening the menu resolves the new document");
        RefreshLinkMenu(viewer, oldLink, new Point(viewer.ActualWidth - 2, viewer.ActualHeight - 2));
        Check(!Headers(viewer).Contains("Copy URL"), "a detached link cannot become the current destination");

        viewer.Document = new FlowDocument(new Paragraph(new Hyperlink(new Run("File URI"))
        {
            CommandParameter = new Uri(file).AbsoluteUri + "#L7",
        }));
        Pump();
        RightClick(viewer, Links(viewer.Document).Single());
        InvokeMenu(viewer, "Copy path");
        Check(copied == file, "file URI unescapes spaces and removes the line fragment for Copy path");
        InvokeMenu(viewer, "Open file");
        Check(viewer.Opened!.Line == 7, "file URI line navigation survives the menu");
        viewer.Document = new FlowDocument(new Paragraph(new Hyperlink(new Run("Unsupported link"))
        {
            CommandParameter = "javascript:alert(1)",
        }));
        Pump();
        RightClick(viewer, Links(viewer.Document).Single());
        Check(!Headers(viewer).Contains("Open link") && !Headers(viewer).Contains("Open file"), "unsupported schemes are never opened from the menu");
        InvokeMenu(viewer, "Copy link address");
        Check(copied == "javascript:alert(1)", "unsupported destinations can be copied as inert text");

        viewer.Markdown = "[Missing source](src/missing.cs:99)";
        Pump();
        RightClick(viewer, Links(viewer.Document!).Single());
        InvokeMenu(viewer, "Copy path");
        Check(copied == Path.Combine(directory, "missing.cs"), "copying a missing file path does not require opening it");

        var custom = new ContextMenu { Style = (Style)Application.Current.FindResource("DarkContextMenu") };
        custom.Items.Add(new MenuItem { Header = "Custom owner action" });
        viewer.ContextMenu = custom;
        RightClick(viewer, Links(viewer.Document!).Single());
        Check(ReferenceEquals(viewer.ContextMenu, custom) && custom.Items.Count == 1, "explicit custom menus are not replaced");
        var editor = new TextBox();
        var editorMenu = BuildMenu(editor, _ => { });
        Check(editorMenu.Items.OfType<MenuItem>().Any(item => Equals(item.Header, "Paste")), "editable controls retain their editing menu");
        Check(!editorMenu.Items.OfType<MenuItem>().Any(item => Equals(item.Header, "Copy URL")), "link commands stay confined to link surfaces");

        CaptureLinkMenus(window, viewer, project);
    }

    private static ContextMenu BuildMenu(FrameworkElement target, Action<string> copy) =>
        (ContextMenu)typeof(TextEditMenu).GetMethod("BuildFor", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [target, copy])!;

    private static string[] Headers(MarkdownView viewer) => viewer.ContextMenu.Items.OfType<MenuItem>()
        .Select(item => item.Header?.ToString() ?? "").ToArray();

    private static void InvokeMenu(MarkdownView viewer, string header)
    {
        var item = viewer.ContextMenu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, header));
        Check(item.Command.CanExecute(item.CommandParameter), header + " is enabled");
        item.Command.Execute(item.CommandParameter);
    }

    private static void RightClick(MarkdownView viewer, ContentElement source)
    {
        source.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
        {
            RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent,
        });
        ContextOpening(source, 20, 20);
        Check(viewer.ContextMenu is not null, "routed right click supplies a menu");
    }

    private static void KeyboardMenu(MarkdownView viewer) => ContextOpening(viewer, -1, -1);

    private static void ContextOpening(object source, double x, double y)
    {
        var args = (ContextMenuEventArgs)Activator.CreateInstance(typeof(ContextMenuEventArgs),
            BindingFlags.NonPublic | BindingFlags.Instance, null, [source, true, x, y], null)!;
        if (source is ContentElement content) content.RaiseEvent(args);
        else ((UIElement)source).RaiseEvent(args);
    }

    private static void RefreshLinkMenu(MarkdownView viewer, DependencyObject? source, Point? point) =>
        typeof(TextEditMenu).GetMethod("UpdateLinkActions", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [viewer.ContextMenu, viewer, source, point]);

    private static Point LinkPoint(MarkdownView viewer, Hyperlink link)
    {
        foreach (var visual in Visuals(viewer))
        {
            if (visual is not IContentHost host) continue;
            var bounds = host.GetRectangles(link);
            if (bounds.Count == 0) continue;
            var rect = bounds[0];
            return visual.TransformToAncestor(viewer).Transform(new Point(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2));
        }
        throw new InvalidOperationException("No rendered link rectangle");
    }

    private static IEnumerable<Visual> Visuals(Visual root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (VisualTreeHelper.GetChild(root, i) is Visual child)
                foreach (var nested in Visuals(child)) yield return nested;
    }

    private static void CaptureLinkMenus(Window window, ProbeMarkdownView viewer, string project)
    {
        var output = Environment.GetEnvironmentVariable("VIBECODE_TEST_OUTPUT");
        if (string.IsNullOrEmpty(output)) return;
        Directory.CreateDirectory(output);
        window.Width = 760;
        window.Height = 350;
        window.Left = 40;
        window.Top = 40;
        window.Content = null;
        var surface = new Border { Padding = new Thickness(24), Child = viewer };
        window.Content = surface;
        viewer.FontFamily = new FontFamily("Segoe UI");
        viewer.FontSize = 14;
        foreach (var theme in new[] { "Dark", "Borderless", "Cli" })
        {
            Application.Current.Resources.MergedDictionaries.Clear();
            Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"/VibeCode;component/Themes/{(theme == "Borderless" ? "Dark" : theme)}.xaml", UriKind.Relative),
            });
            if (theme == "Borderless") Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("/VibeCode;component/Themes/Borderless.xaml", UriKind.Relative),
            });
            surface.Background = (Brush)Application.Current.FindResource("Bg0");
            viewer.Background = Brushes.Transparent;
            viewer.Foreground = (Brush)Application.Current.FindResource("Text");
            viewer.LinkBaseDirectory = project;
            viewer.Markdown = "It's running: **[Play Sol Jungle](http://localhost:5173/)**. Click the scene for mouse look and sound.\n\n" +
                "The source is [main.cs](src/main.cs:42). Open the [project directory](src/).";
            Pump();
            for (var i = 0; i < 3; i++)
            {
                var link = Links(viewer.Document!).ElementAt(i);
                viewer.Selection.Select(link.ContentStart, link.ContentEnd);
                viewer.Focus();
                viewer.ContextMenu = BuildMenu(viewer, _ => { });
                var menu = viewer.ContextMenu;
                menu.PlacementTarget = viewer;
                menu.Placement = PlacementMode.Relative;
                menu.HorizontalOffset = i == 0 ? 112 : 28;
                menu.VerticalOffset = i == 0 ? 48 : 105;
                menu.IsOpen = true;
                CommandManager.InvalidateRequerySuggested();
                Pump();
                menu.UpdateLayout();
                Check(menu.IsOpen && menu.ActualHeight > 60 && menu.ActualWidth >= 196, "themed link menu renders at a readable size");
                Check(menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Copy")).IsEnabled,
                    "native Copy remains enabled for selected link text while the menu is open");
                var scene = new DrawingVisual();
                using (var drawing = scene.RenderOpen())
                {
                    drawing.DrawRectangle(new VisualBrush(surface), null, new Rect(0, 0, surface.ActualWidth, surface.ActualHeight));
                    drawing.DrawRectangle(new VisualBrush(menu), null,
                        new Rect(menu.HorizontalOffset + 24, menu.VerticalOffset + 24, menu.ActualWidth, menu.ActualHeight));
                }
                var bitmap = new RenderTargetBitmap((int)surface.ActualWidth, (int)surface.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(scene);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(output, $"{theme.ToLowerInvariant()}-{new[] { "url", "file", "directory" }[i]}.png"));
                encoder.Save(stream);
                menu.IsOpen = false;
                Pump();
            }
        }
    }
}
