using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace VibeCode.UI;

/// <summary>A stable directory-to-agent graph. Nodes remain native keyboard-accessible buttons;
/// only the connection layer repaints while working. No physics, provider requests or background polling.</summary>
public sealed class WorkflowGraph : Canvas
{
    private readonly ConnectionLayer _connections = new();
    private readonly Dictionary<string, Button> _nodes = new(StringComparer.Ordinal);
    private IReadOnlyList<AgentActivityProject> _projects = [];
    private readonly DispatcherTimer _motion;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    public event Action<AgentActivityNode>? AgentSelected;
    public event Action<string>? ProjectSelected;
    public bool MotionEnabled { get; set; } = true;
    public int NodeCount => _nodes.Count;
    public bool IsAnimating => _motion.IsEnabled;

    public WorkflowGraph()
    {
        _motion = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
        Children.Add(_connections);
        ClipToBounds = true;
        SizeChanged += (_, e) => { if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > .5) LayoutGraph(); };
        Loaded += (_, _) => UpdateMotion();
        Unloaded += (_, _) => _motion.Stop();
        IsVisibleChanged += (_, _) => UpdateMotion();
        _motion.Tick += (_, _) =>
        {
            UpdateMotion();
            _connections.Phase = _clock.Elapsed.TotalSeconds / 2.8;
            _connections.InvalidateVisual();
        };
    }

    public void SetProjects(IReadOnlyList<AgentActivityProject> projects)
    {
        // A running chat can emit many snapshots a second. An unchanged roster costs no layout or new controls.
        var unchanged = _projects.Count == projects.Count && _projects.Zip(projects).All(pair =>
            string.Equals(pair.First.Key, pair.Second.Key, StringComparison.OrdinalIgnoreCase)
            && pair.First.Agents.SequenceEqual(pair.Second.Agents));
        _projects = projects;
        if (!unchanged) LayoutGraph();
        UpdateMotion();
    }

    public void UpdateMotion()
    {
        var running = IsLoaded && IsVisible && MotionEnabled && HudMotion.Enabled
            && SystemParameters.ClientAreaAnimation && Window.GetWindow(this)?.WindowState != WindowState.Minimized
            && _connections.Links.Any(link => link.Working);
        if (running) _motion.Start(); else _motion.Stop();
        _connections.Animate = running;
        _connections.InvalidateVisual();
    }

    public static string StateResource(AgentActivityState state) => state switch
    {
        AgentActivityState.Working => "Green",
        AgentActivityState.Waiting => "Amber",
        AgentActivityState.Error => "Red",
        AgentActivityState.Paused => "Amber",
        _ => "Muted",
    };

    private void LayoutGraph()
    {
        var width = ActualWidth;
        if (width < 100) return;
        var present = new HashSet<string>(StringComparer.Ordinal);
        var links = new List<Connection>();
        // Wide displays show independent project clusters beside each other. Compact windows become a
        // single directional tree; node text never shrinks to make a graph fit.
        var columns = Math.Min(Math.Max(1, _projects.Count), Math.Max(1, (int)(width / 900)));
        var cellWidth = width / columns;
        var y = 16.0;
        var tabIndex = 0;
        for (var first = 0; first < _projects.Count; first += columns)
        {
            var rowHeight = 0.0;
            for (var column = 0; column < columns && first + column < _projects.Count; column++)
            {
                var project = _projects[first + column];
                var x = cellWidth * column;
                var compact = cellWidth < 760;
                var agentWidth = compact ? Math.Max(190, cellWidth - 254) : Math.Min(360, (cellWidth - 300) / 2);
                var projectWidth = compact ? 180 : 214;
                var rows = compact ? project.Agents.Count : (project.Agents.Count + 1) / 2;
                var height = Math.Max(144, rows * 120 + 20);
                rowHeight = Math.Max(rowHeight, height);
                var hub = new Rect(x + (compact ? 8 : (cellWidth - projectWidth) / 2), y + (compact ? 10 : (height - 112) / 2), projectWidth, 112);
                var projectId = "project:" + project.Key;
                var projectButton = Node(projectId, present);
                KeyboardNavigation.SetTabIndex(projectButton, tabIndex++);
                projectButton.SetResourceReference(Control.BackgroundProperty, "Bg2");
                projectButton.SetResourceReference(Control.BorderBrushProperty, "AccentDim");
                projectButton.Tag = project;
                projectButton.ToolTip = project.Directory.Length == 0 ? "Chats without a project directory" : project.Directory;
                AutomationProperties.SetName(projectButton, $"Filter to {project.Name}, {project.Agents.Count} agents");
                projectButton.Content = ProjectContent(project);
                Place(projectButton, hub);
                for (var i = 0; i < project.Agents.Count; i++)
                {
                    var agent = project.Agents[i];
                    var left = !compact && i % 2 == 0;
                    var index = compact ? i : i / 2;
                    var rect = new Rect(x + (left ? 8 : cellWidth - agentWidth - 8), y + 10 + index * 120, agentWidth, 100);
                    var button = Node("agent:" + agent.Id, present);
                    KeyboardNavigation.SetTabIndex(button, tabIndex++);
                    button.Tag = agent;
                    button.ToolTip = $"{agent.Label} · {agent.Role}\n{agent.Task}\n{agent.Model}\n{agent.Directory}\n{agent.Status}"
                        + (agent.Account.Length > 0 ? "\n" + agent.Account : "")
                        + "\nOpen this chat";
                    AutomationProperties.SetName(button, $"Open {agent.Label}: {agent.Task}. {agent.Status}. {agent.Role}.");
                    button.Content = AgentContent(agent);
                    Place(button, rect);
                    var start = new Point(left ? hub.Left : hub.Right, hub.Top + hub.Height / 2);
                    var end = new Point(left ? rect.Right : rect.Left, rect.Top + rect.Height / 2);
                    links.Add(new(start, end, agent.State == AgentActivityState.Working));
                }
            }
            y += rowHeight + 44;
        }
        foreach (var id in _nodes.Keys.Where(k => !present.Contains(k)).ToArray())
        {
            Children.Remove(_nodes[id]);
            _nodes.Remove(id);
        }
        Height = Math.Max(180, y);
        _connections.Width = width;
        _connections.Height = Height;
        _connections.Links = links;
        _connections.InvalidateVisual();
        UpdateMotion();
    }

    private Button Node(string id, HashSet<string> present)
    {
        present.Add(id);
        if (_nodes.TryGetValue(id, out var node)) return node;
        node = new Button { Cursor = Cursors.Hand, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(14, 12, 14, 12) };
        node.SetResourceReference(StyleProperty, "WorkflowNode");
        node.Click += (_, _) =>
        {
            if (node.Tag is AgentActivityNode agent) AgentSelected?.Invoke(agent);
            else if (node.Tag is AgentActivityProject project) ProjectSelected?.Invoke(project.Key);
        };
        _nodes[id] = node;
        Children.Add(node);
        return node;
    }

    private static void Place(FrameworkElement element, Rect rect)
    { SetLeft(element, rect.Left); SetTop(element, rect.Top); element.Width = rect.Width; element.Height = rect.Height; }

    private static TextBlock Text(string text, double size, string resource = "Text")
    {
        var block = new TextBlock { Text = text, FontSize = size, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
        block.SetResourceReference(TextBlock.ForegroundProperty, resource);
        return block;
    }

    private static UIElement ProjectContent(AgentActivityProject project)
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var heading = new DockPanel();
        var icon = Text("\uE8B7", 17, "Accent");
        icon.SetResourceReference(TextBlock.FontFamilyProperty, "Icons");
        icon.Margin = new Thickness(0, 0, 9, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;
        heading.Children.Add(icon);
        var title = Text(project.Name, 16);
        title.FontWeight = FontWeights.SemiBold;
        heading.Children.Add(title);
        stack.Children.Add(heading);
        var path = project.Directory.Length == 0 ? "Independent chats" : project.Directory;
        var directory = Text(path, 11.5, "Muted");
        directory.Margin = new Thickness(0, 8, 0, 10);
        stack.Children.Add(directory);
        stack.Children.Add(Text($"{project.Agents.Count} agent{(project.Agents.Count == 1 ? "" : "s")} · {project.Working} working", 12, "Muted"));
        return stack;
    }

    private static UIElement AgentContent(AgentActivityNode agent)
    {
        var stack = new StackPanel();
        var header = new DockPanel();
        var status = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0) };
        var dot = new System.Windows.Shapes.Ellipse { Width = 5, Height = 5, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
        dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, StateResource(agent.State));
        status.Children.Add(dot);
        status.Children.Add(Text(agent.Status, 11, StateResource(agent.State)));
        DockPanel.SetDock(status, Dock.Right);
        header.Children.Add(status);
        var label = Text(agent.Label, 12, "Muted");
        header.Children.Add(label);
        stack.Children.Add(header);
        var task = Text(agent.Task, 14);
        task.FontWeight = FontWeights.SemiBold;
        task.Margin = new Thickness(0, 7, 0, 6);
        stack.Children.Add(task);
        var detail = agent.Role + " · " + agent.Model;
        if (agent.Account.Length > 0) detail += " · " + agent.Account;
        if (agent.ActiveChildren > 0) detail += $" · {agent.ActiveChildren} subagents working";
        stack.Children.Add(Text(detail, 11.5, "Muted"));
        return stack;
    }

    private sealed record Connection(Point Start, Point End, bool Working)
    {
        // Geometry belongs to the layout, not the animation frame.
        public StreamGeometry Geometry { get; } = Build(Start, End);
        private static StreamGeometry Build(Point start, Point end)
        {
            var middle = (start.X + end.X) / 2;
            var geometry = new StreamGeometry();
            using (var path = geometry.Open())
            { path.BeginFigure(start, false, false); path.BezierTo(new(middle, start.Y), new(middle, end.Y), end, true, false); }
            geometry.Freeze();
            return geometry;
        }
    }
    private sealed class ConnectionLayer : FrameworkElement
    {
        public IReadOnlyList<Connection> Links { get; set; } = [];
        public double Phase { get; set; }
        public bool Animate { get; set; }
        public ConnectionLayer() => IsHitTestVisible = false;
        protected override void OnRender(DrawingContext dc)
        {
            var idle = TryFindResource("Border") as Brush ?? Brushes.DimGray;
            var active = TryFindResource("Accent") as Brush ?? Brushes.CornflowerBlue;
            var green = TryFindResource("Green") as Brush ?? Brushes.LightGreen;
            foreach (var link in Links)
            {
                var midpoint = (link.Start.X + link.End.X) / 2;
                var c1 = new Point(midpoint, link.Start.Y);
                var c2 = new Point(midpoint, link.End.Y);
                dc.DrawGeometry(null, new Pen(link.Working ? active : idle, link.Working ? 1.5 : 1), link.Geometry);
                dc.DrawEllipse(link.Working ? active : idle, null, link.End, 3, 3);
                if (!link.Working || !Animate) continue;
                // This is a working-state indicator, not a message/traffic counter.
                var t = Phase % 1;
                var u = 1 - t;
                var point = new Point(u*u*u*link.Start.X + 3*u*u*t*c1.X + 3*u*t*t*c2.X + t*t*t*link.End.X,
                    u*u*u*link.Start.Y + 3*u*u*t*c1.Y + 3*u*t*t*c2.Y + t*t*t*link.End.Y);
                dc.DrawEllipse(green, null, point, 3.5, 3.5);
            }
        }
    }
}
