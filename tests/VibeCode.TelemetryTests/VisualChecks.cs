using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    static void PumpFor(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }

    static void Invoke(Button button)
    {
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
        PumpFor(30);
    }

    static byte[] Pixels(FrameworkElement element)
    {
        var width = (int)Math.Ceiling(element.ActualWidth);
        var height = (int)Math.Ceiling(element.ActualHeight);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var bytes = new byte[width * height * 4];
        bitmap.CopyPixels(bytes, width * 4, 0);
        return bytes;
    }

    static void VerifyVisualInteractions()
    {
        var source = new MainViewModel();
        var directory = @"C:\Projects\Customer platforms\VibeCode enterprise workflow automation";
        var fixture = Enumerable.Range(1, 8).Select(i => Chat(directory, "Claude " + i,
            i == 1 ? "Verify long project names and keyboard navigation without clipped controls" : "Review workflow step " + i,
            i <= 2 ? "running" : "idle", "claude")).ToArray();
        fixture[2].AuthNeeded = true;
        foreach (var chat in fixture) source.Chats.Add(chat);
        var wall = new WorkflowTelemetryWindow(source) { Left = 6200, Top = 200, WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false };
        wall.Show(); wall.Refresh();
        var graph = (WorkflowGraph)wall.FindName("Graph");
        var scroll = (ScrollViewer)wall.FindName("MapScroll");
        var filter = (ComboBox)wall.FindName("ProjectFilter");
        var search = (TextBox)wall.FindName("Search");
        Capture(wall, 760, 480, "workflow-long-compact.png");
        CheckLayout(graph);
        var hub = graph.Children.OfType<Button>().Single(b => b.Tag is AgentActivityProject);
        var first = graph.Children.OfType<Button>().First(b => b.Tag is AgentActivityNode);
        Check("Compact projects are visible beside their first agent", Math.Abs(Canvas.GetTop(hub) - Canvas.GetTop(first)) < 1);
        Check("Full task and path remain available despite visual truncation", first.ToolTip.ToString()!.Contains(fixture[0].Title)
            && hub.ToolTip.ToString() == directory);
        Check("Long node text fits vertically without inheriting wrapping", graph.Children.OfType<Button>().All(b =>
            ((FrameworkElement)b.Content).ActualHeight <= b.ActualHeight - b.Padding.Top - b.Padding.Bottom));
        Invoke(hub);
        Check("Activating a project node applies its directory filter", (string)filter.SelectedValue == directory);
        search.Text = "workflow step 8";
        var target = graph.Children.OfType<Button>().Single(b => b.Tag is AgentActivityNode);
        Invoke(target);
        Check("Activating an agent node opens that existing chat", ReferenceEquals(source.ActiveChat, fixture[7]));
        Check("Node activation does not start a provider session", typeof(ChatViewModel).GetField("_session", Flags)!.GetValue(fixture[7]) is null);
        search.Clear(); filter.SelectedValue = "*";
        wall.UpdateLayout();
        var last = graph.Children.OfType<Button>().Single(b => b.Tag is AgentActivityNode n && n.Id == fixture[7].BridgeAgentId);
        last.Focus(); last.BringIntoView(); PumpFor(80);
        Check("Keyboard focus reaches an offscreen agent", last.IsKeyboardFocused);
        Check("Focused offscreen agents scroll into view", scroll.VerticalOffset > 0
            && last.TransformToAncestor(scroll).TransformBounds(new Rect(last.RenderSize)).Bottom <= scroll.ActualHeight + 1);
        Capture(wall, 760, 480, "workflow-keyboard-scroll.png");
        first = graph.Children.OfType<Button>().Single(b => b.Tag is AgentActivityNode n && n.Id == fixture[0].BridgeAgentId);
        first.Focus(); first.BringIntoView(); PumpFor(80);
        Check("Keyboard tab moves between graph controls", first.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)));
        Check("Filtering preserves visual keyboard order", Keyboard.FocusedElement is Button { Tag: AgentActivityNode focused } && focused.Id == fixture[1].BridgeAgentId);
        scroll.ScrollToTop();
        Capture(wall, 1280, 800, "workflow-scaled-150.png", 144);
        CheckLayout(graph);
        Capture(wall, 1280, 800, "workflow-long-desktop.png");
        AppSettings.Current.TelemetryLiveAnimation = true; graph.UpdateMotion(); PumpFor(100);
        if (SystemParameters.ClientAreaAnimation)
        {
            var before = Pixels(graph);
            Capture(wall, 1280, 800, "workflow-motion-a.png");
            PumpFor(400);
            var after = Pixels(graph);
            Capture(wall, 1280, 800, "workflow-motion-b.png");
            Check("Working connections produce different rendered animation frames", !before.SequenceEqual(after));
        }
        AppSettings.Current.TelemetryLiveAnimation = false; graph.UpdateMotion(); PumpFor(60);
        var still = Pixels(graph); PumpFor(180);
        Check("Reduced-motion graph pixels remain stationary", still.SequenceEqual(Pixels(graph)));
        fixture[0].Status = "error";
        PumpFor(1200);
        Check("The real refresh timer updates visible agent states", graph.Children.OfType<Button>()
            .Any(b => b.Tag is AgentActivityNode n && n.Id == fixture[0].BridgeAgentId && n.State == AgentActivityState.Error));
        wall.WindowState = WindowState.Minimized;
        Check("Minimizing the wall stops connection animation", !graph.IsAnimating);
        wall.WindowState = WindowState.Normal;
        Check("Pin button changes the real window's topmost state", !wall.Topmost);
        ((System.Windows.Controls.Primitives.ToggleButton)wall.FindName("Pin")).IsChecked = true;
        Check("Pin action keeps the wall on top", wall.Topmost);
        wall.Close();
        Check("Closing the wall preserves open chats", fixture.All(c => c.Status != "closed"));
        foreach (var chat in fixture) chat.Close();
    }
}
