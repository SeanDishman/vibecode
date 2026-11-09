using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VibeCode;
using VibeCode.Services;

/// <summary>The new controls laid out and rendered for real with the app's Dark theme, never shown on screen: the
/// Settings › Bridge switch, the MCP list's sharing state and the MCP dialog's checkbox. Renders land in
/// %TEMP%\agent5-mcp-host-ui for review.</summary>
internal static partial class Program
{
    private static readonly string Shots = Path.Combine(Path.GetTempPath(), "agent5-mcp-host-ui");

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static void Snap(FrameworkElement element, string name, double width, double height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Shots);
        using var output = File.Create(Path.Combine(Shots, name));
        encoder.Save(output);
        Console.WriteLine("  rendered " + Path.Combine(Shots, name));
    }

    private static bool SavedInProcess() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "data", "settings.json"))).RootElement
            .GetProperty(nameof(AppSettings.BridgeToolsInProcess)).GetBoolean();

    private static void VerifyRenderedUi()
    {
        if (Application.Current is null) _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var resources = Application.Current!.Resources.MergedDictionaries;
        if (resources.Count == 0)
            resources.Add((ResourceDictionary)Application.LoadComponent(new Uri("/VibeCode;component/Themes/Dark.xaml", UriKind.Relative)));
        var shared = new McpServerDefinition { Name = "Blender_MCP", Command = Environment.ProcessPath!, ShareAcrossAgents = true };
        AppSettings.Current.McpServers.Add(shared);

        // Settings › Bridge: the switch shows the setting and saves it.
        var settings = new SettingsWindow();
        var toggle = (ToggleButton)settings.FindName("BridgeToolsInProcessToggle");
        toggle.IsChecked = AppSettings.Current.BridgeToolsInProcess;   // what the window's Loaded handler does
        typeof(SettingsWindow).GetField("_ready", Any)!.SetValue(settings, true);
        toggle.IsChecked = false;
        Check(!AppSettings.Current.BridgeToolsInProcess && !SavedInProcess(), "turning the switch off saves BridgeToolsInProcess=false");
        toggle.IsChecked = true;
        Check(AppSettings.Current.BridgeToolsInProcess && SavedInProcess(), "turning it back on saves BridgeToolsInProcess=true");
        settings.SelectCategory("Bridge");
        var bridge = (FrameworkElement)settings.FindName("PaneBridge");
        Snap(bridge, "settings-bridge.png", 760, 1500);
        Check(Descendants<TextBlock>(bridge).Any(text => text.Text == "Serve Bridge tools in-process") && toggle.ActualWidth > 0,
            "the 'Serve Bridge tools in-process' card renders in Settings › Bridge");

        // Settings › MCP servers: a shared server says so in the list.
        typeof(SettingsWindow).GetMethod("RefreshMcpServers", Any)!.Invoke(settings, null);
        var card = settings.FindName("McpServersCard")!;
        card.GetType().GetProperty("IsExpanded")!.SetValue(card, true);
        settings.SelectCategory("Extensions");
        var extensions = (FrameworkElement)settings.FindName("PaneExtensions");
        Snap(extensions, "settings-mcp-list.png", 760, 1100);
        var rows = Descendants<TextBlock>(extensions).Select(text => string.Concat(text.Inlines.OfType<System.Windows.Documents.Run>().Select(run => run.Text)));
        Check(rows.Any(row => row.StartsWith("Local · stdio", StringComparison.Ordinal) && row.EndsWith("  ·  shared · 0 processes · 0 agents", StringComparison.Ordinal)),
            "the MCP list shows a shared server's state (shared · 0 processes · 0 agents)");
        settings.Close();

        // The MCP server dialog: the checkbox round-trips, and only a stdio server can be shared.
        var dialog = new McpServerDialog(AppSettings.Current.McpServers, shared);
        var share = (CheckBox)dialog.FindName("ShareCheck");
        Check(share.IsChecked == true, "an opted-in server opens with 'Share one instance across agents' ticked");
        McpServerDefinition Build()
        {
            object?[] arguments = [null, null, null];
            typeof(McpServerDialog).GetMethod("TryBuild", Any)!.Invoke(dialog, arguments);
            return (McpServerDefinition)arguments[0]!;
        }
        Check(Build().ShareAcrossAgents, "saving keeps the opt-in");
        share.IsChecked = false;
        Check(!Build().ShareAcrossAgents, "unticking it saves a per-agent server");
        share.IsChecked = true;
        Snap((FrameworkElement)dialog.Content, "mcp-dialog-stdio.png", 640, 1250);
        var transport = (ComboBox)dialog.FindName("TransportBox");
        transport.SelectedValue = McpCatalog.HttpTransport;
        Check(!Build().ShareAcrossAgents && ((FrameworkElement)dialog.FindName("StdioPanel")).Visibility == Visibility.Collapsed,
            "a remote server cannot be marked shared, and the checkbox is hidden with the stdio fields");
        dialog.Close();
        AppSettings.Current.McpServers.Remove(shared);
    }
}
