using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using VibeCode.UI;

internal static class QueuedMessageLayout
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static int _checks;

    public static void Run()
    {
        var document = XDocument.Load(Path.Combine(Environment.CurrentDirectory, "VibeCode.Desktop", "MainWindow.xaml"));
        var input = document.Descendants(Wpf + "TextBox").Single(e => (string?)e.Attribute(X + "Name") == "InputBox");
        var paneInput = document.Descendants(Wpf + "TextBox").Single(e => (string?)e.Attribute(X + "Name") == "PaneInput");
        var queuedTemplate = document.Descendants(Wpf + "DataTemplate")
            .Single(e => (string?)e.Attribute("DataType") == "{x:Type ui:QueuedItem}");
        var steerSource = document.Descendants(Wpf + "Button").Single(e => (string?)e.Attribute("Content") == "Steer");
        Check(steerSource.Ancestors().Contains(queuedTemplate), "Steer must appear on a submitted queued message");
        Check(!document.Descendants(Wpf + "Button").Any(e => (string?)e.Attribute("Content") == "Queue"),
            "composer mode buttons returned");
        Check(queuedTemplate.Descendants(Wpf + "Button").Any(e => (string?)e.Attribute("Click") == "OnSendQueuedNow"),
            "queued messages lost their Send message now action");

        var output = Path.Combine(Environment.CurrentDirectory, "artifacts", "agent1-codex-message-actions", "verification");
        Directory.CreateDirectory(output);
        foreach (var theme in new[] { "Dark", "Cli" })
        {
            var resources = Application.Current.Resources;
            resources.MergedDictionaries.Clear();
            resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(
                new Uri($"/VibeCode;component/Themes/{theme}.xaml", UriKind.Relative)));
            resources["BoolVis"] = new BooleanToVisibilityConverter();
            resources["ShowIf"] = new NonEmptyToVisibilityConverter();
            resources["HintVis"] = new HintVisibilityConverter();
            var style = new XElement(Wpf + "ResourceDictionary", new XAttribute(XNamespace.Xmlns + "x", X),
                new XElement(document.Descendants(Wpf + "Style").Single(e => (string?)e.Attribute(X + "Key") == "SendButton")));
            resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(style.ToString()));

            foreach (var width in new[] { 600, 240 })
            {
                var host = new StackPanel
                {
                    Width = width, Background = (Brush)resources["Bg0"],
                    DataContext = new ComposerFixture(),
                };
                host.SetValue(TextBlock.FontFamilyProperty, resources["Ui"]);
                var card = Parse<Grid>(queuedTemplate.Elements().Single());
                card.Style = null; // Capture the settled message without its entrance transition.
                card.DataContext = Queued(codex: true);
                host.Children.Add(card);
                host.Children.Add(new TextBlock { Text = "Chat composer", Foreground = (Brush)resources["Muted"], Margin = new Thickness(4, 12, 4, 4) });
                var mainComposer = Parse<Grid>(input.Parent!);
                host.Children.Add(mainComposer);
                host.Children.Add(new TextBlock { Text = "Bridge composer", Foreground = (Brush)resources["Muted"], Margin = new Thickness(4, 12, 4, 4) });
                var bridgeComposer = Parse<DockPanel>(paneInput.Ancestors(Wpf + "DockPanel").First());
                host.Children.Add(bridgeComposer);
                Layout(host);

                Check(Descendants<Button>(mainComposer).Count() == 1, "chat composer has extra action buttons");
                Check(Descendants<Button>(mainComposer).All(b => b.Content is not ("Queue" or "Steer")), "chat composer exposes follow-up modes");
                Check(Descendants<Button>(bridgeComposer).All(b => b.Content is not ("Queue" or "Steer")), "Bridge composer exposes follow-up modes");
                var actions = Descendants<Button>(card).Where(b => b.Content is "Steer" or "Send message now").ToArray();
                Check(actions.Length == 2 && actions.All(b => b.IsEnabled && b.Visibility == Visibility.Visible), "submitted Codex message must offer both actions");
                foreach (var action in actions)
                {
                    action.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    Check(action.ActualWidth + action.Margin.Left + action.Margin.Right + .5 >= action.DesiredSize.Width
                        && action.ActualHeight + action.Margin.Top + action.Margin.Bottom + .5 >= action.DesiredSize.Height
                        && action.ActualHeight >= 20,
                        $"{width}px message action clips {action.Content}: {action.ActualWidth:0.0} × {action.ActualHeight:0.0}, needs {action.DesiredSize}");
                    Check(action.TransformToAncestor(host).Transform(new Point(action.ActualWidth, 0)).X <= width + .5,
                        $"{action.Content} extends outside its message pane");
                }
                Check(AutomationProperties.GetName(actions.Single(b => b.Content is "Send message now")) == "Send message now", "accessible send label differs from the visible label");
                Save(host, Path.Combine(output, $"sent-message-{theme.ToLowerInvariant()}-{width}.png"), width == 240 ? 2 : 1);

                var steer = actions.Single(b => b.Content is "Steer");
                foreach (var state in new[] { Queued(codex: false), Queued(codex: true, extended: true), Queued(codex: true, swarm: true), Queued(codex: true, head: false) })
                {
                    card.DataContext = state;
                    Layout(host);
                    Check(steer.Visibility == Visibility.Collapsed, "steering is exposed for an incompatible queue entry");
                }
                card.DataContext = Queued(codex: true, active: false);
                Layout(host);
                Check(steer.Visibility == Visibility.Visible && !steer.IsEnabled, "an inactive turn still allows steering");
            }
            ShellOutcomeLayout.Run(document, output, theme);
        }
        Console.WriteLine($"PASS: {_checks} message layout checks; actions live under sent messages, with clean chat and Bridge composers in both themes");
    }

    private static object Queued(bool codex, bool extended = false, bool swarm = false, bool head = true, bool active = true) => new
    {
        // Steer is offered by SupportsSteer (Codex, Claude, GLM, Grok); "codex: false" stands for a provider that only
        // queues, such as Kimi, whose ACP adapter refuses a prompt while a turn runs.
        Owner = new { IsCodex = codex, SupportsSteer = codex, CanSteer = active, CanSendQueuedNow = true },
        IsQueueHead = head, Extended = extended, UseSwarm = swarm, HasText = true, IsGoalCommand = false,
        Text = "Focus on the failing tests first.", QueueStatusText = "Queued · sends when the agent finishes",
        QueueActionText = codex ? "Send message now" : "Send prompt now",
        QueueActionToolTip = "Stop the active turn if needed, then send all queued prompts together",
    };

    private sealed class ComposerFixture
    {
        public bool CanInterrupt => true;
        public bool CanType => true;
        public bool InputLocked => false;
        public string Draft { get; set; } = "";
        public string ComposerPlaceholder => "Message";
    }

    internal static T Parse<T>(XElement source) where T : FrameworkElement
    {
        var clone = new XElement(source);
        clone.SetAttributeValue("xmlns", Wpf.NamespaceName);
        clone.SetAttributeValue(XNamespace.Xmlns + "x", X);
        clone.SetAttributeValue(XNamespace.Xmlns + "ui", "clr-namespace:VibeCode.UI");
        foreach (var element in clone.DescendantsAndSelf())
            foreach (var attribute in element.Attributes().Where(a => a.Value.StartsWith("On", StringComparison.Ordinal)).ToArray())
                attribute.Remove();
        return (T)XamlReader.Parse(clone.ToString().Replace("clr-namespace:VibeCode.UI\"",
            "clr-namespace:VibeCode.UI;assembly=VibeCode\"", StringComparison.Ordinal));
    }

    internal static void Layout(FrameworkElement element)
    {
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        element.Measure(new Size(element.Width, double.PositiveInfinity));
        element.Arrange(new Rect(0, 0, element.Width, Math.Ceiling(element.DesiredSize.Height)));
        element.UpdateLayout();
    }

    internal static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    internal static void Save(FrameworkElement element, string path, double scale = 1)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * scale), (int)Math.Ceiling(element.ActualHeight * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path);
        encoder.Save(output);
    }

    private static void Check(bool condition, string message)
    {
        _checks++;
        if (!condition) throw new InvalidOperationException(message);
    }
}
