using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using VibeCode.Services;

namespace VibeCode.UI;

/// <summary>A stable directory-to-agent graph. Nodes remain native keyboard-accessible buttons whose content is
/// updated in place; positions change only when the roster's shape or the canvas width does.
///
/// Working connections stream packets from the agent into its parent at the agent's model speed (see
/// <see cref="WorkflowFlow"/>). The stream is WPF animation of each path's dash offset, so it runs on WPF's own
/// animation clock at display rate with no timer or per-frame drawing code here, and stops entirely when the wall is
/// hidden, minimized or motion is turned off. Static lines are drawn once per layout or state change. No physics, provider requests or
/// background polling.</summary>
public sealed partial class WorkflowGraph : Canvas, IWorkflowWallView
{
    internal const double AgentHeight = 132, AgentGap = 16, HubWidth = 220, HubHeight = 122;
    private const double AgentPitch = AgentHeight + AgentGap;
    private const int LayerZ = -3, FlowZ = -2, HaloZ = -1;

    /// <summary>The status rail along a node's left edge. Read by the WorkflowNode template.</summary>
    public static readonly DependencyProperty RailProperty = DependencyProperty.RegisterAttached(
        "Rail", typeof(Brush), typeof(WorkflowGraph), new FrameworkPropertyMetadata(null));
    public static Brush? GetRail(DependencyObject element) => (Brush?)element.GetValue(RailProperty);
    public static void SetRail(DependencyObject element, Brush? value) => element.SetValue(RailProperty, value);

    private readonly ConnectionLayer _connections = new();
    private readonly Dictionary<string, Button> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Rect> _cards = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FlowStream> _flows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Halo> _halos = new(StringComparer.Ordinal);
    private IReadOnlyList<AgentActivityProject> _projects = [];
    private string _structure = "";
    private bool _animating;
    public event Action<AgentActivityNode>? AgentSelected;
    public event Action<AgentActivityNode>? AgentMenuRequested;
    public event Action<string>? ProjectSelected;
    public bool MotionEnabled { get; set; } = true;
    /// <summary>The cached public OpenRouter reading for a model id. Supplied by the wall; must never fetch.</summary>
    public Func<string, ModelSpeed?>? SpeedLookup { get; set; }
    public int NodeCount => _nodes.Count;
    public int ManagedConnectionCount => _connections.Links.Count(link => link.Managed);
    public bool IsAnimating => _animating;
    /// <summary>Working connections with a packet stream, animated or not.</summary>
    public int FlowCount => _flows.Count;

    public WorkflowGraph()
    {
        SetZIndex(_connections, LayerZ);
        Children.Add(_connections);
        ClipToBounds = true;
        SizeChanged += (_, e) => { if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > .5) LayoutGraph(); };
        Loaded += (_, _) => UpdateMotion();
        Unloaded += (_, _) => SetAnimating(false);
        IsVisibleChanged += (_, _) => UpdateMotion();
    }

    public void SetProjects(IReadOnlyList<AgentActivityProject> projects)
    {
        // A running chat can emit many snapshots a second. Only a change in WHO is on the wall moves anything;
        // what each agent is doing updates the existing cards and lines in place.
        _projects = projects;
        var structure = Structure(projects);
        if (structure != _structure) LayoutGraph();
        else UpdateContent();
        UpdateMotion();
    }

    /// <summary>Re-read model speeds after the shared speed cache changes, without waiting for the next refresh.</summary>
    public void RefreshSpeeds()
    {
        UpdateContent();
        UpdateMotion();
    }

    public void UpdateMotion()
    {
        var running = IsLoaded && IsVisible && MotionEnabled && HudMotion.Enabled
            && SystemParameters.ClientAreaAnimation && Window.GetWindow(this)?.WindowState != WindowState.Minimized
            && _flows.Count > 0;
        SetAnimating(running);
    }

    private void SetAnimating(bool running)
    {
        if (running == _animating) return;
        _animating = running;
        _connections.Streaming = running;
        _connections.InvalidateVisual();
        foreach (var flow in _flows.Values) flow.SetRunning(running);
        foreach (var halo in _halos.Values) halo.SetRunning(running);
        // Working-time counters switch between seconds and whole minutes with motion, now rather than next refresh.
        UpdateContent();
    }

    public static string StateResource(AgentActivityState state) => state switch
    {
        AgentActivityState.Working => "Green",
        AgentActivityState.Waiting => "Amber",
        AgentActivityState.Error => "Red",
        AgentActivityState.Paused => "Amber",
        _ => "Muted",
    };

    /// <summary>Everything that decides a position: projects, agents, and who reports to whom.</summary>
    private static string Structure(IReadOnlyList<AgentActivityProject> projects) => string.Join("\n", projects.Select(project =>
        project.Key + "|" + string.Join(",", project.Agents.Select(a =>
            a.Id + ":" + a.ParentAgentId + ":" + (a.IsCentralPlanner ? "c" : "") + (a.Chat.IsBridgeManager ? "m" : "")))));

    private void LayoutGraph()
    {
        var width = ActualWidth;
        if (width < 100) return;
        _structure = Structure(_projects);
        var present = new HashSet<string>(StringComparer.Ordinal);
        var links = new List<Connection>();
        _cards.Clear();
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
                if (project.Agents.Any(a => a.IsCentralPlanner || a.Chat.IsBridgeManager || a.ParentAgentId is not null))
                {
                    rowHeight = Math.Max(rowHeight, LayoutAdvanced(project, x, y, cellWidth, present, links, ref tabIndex));
                    continue;
                }
                var compact = cellWidth < 760;
                // The gap between a card and its hub is where the stream runs, so it is kept long enough to read.
                var agentWidth = compact ? Math.Max(190, cellWidth - 254) : Math.Min(340, (cellWidth - 344) / 2);
                var projectWidth = compact ? 180 : HubWidth;
                var rows = compact ? project.Agents.Count : (project.Agents.Count + 1) / 2;
                var height = Math.Max(HubHeight + 32, rows * AgentPitch + 20);
                rowHeight = Math.Max(rowHeight, height);
                var hub = new Rect(x + (compact ? 8 : (cellWidth - projectWidth) / 2), y + (compact ? 10 : (height - HubHeight) / 2), projectWidth, HubHeight);
                PlaceProject(project, hub, present, ref tabIndex);
                for (var i = 0; i < project.Agents.Count; i++)
                {
                    var agent = project.Agents[i];
                    var left = !compact && i % 2 == 0;
                    var index = compact ? i : i / 2;
                    var rect = new Rect(x + (left ? 8 : cellWidth - agentWidth - 8), y + 10 + index * AgentPitch, agentWidth, AgentHeight);
                    PlaceAgent(agent, rect, present, ref tabIndex);
                    var start = new Point(left ? hub.Left : hub.Right, hub.Top + hub.Height / 2);
                    var end = new Point(left ? rect.Right : rect.Left, rect.Top + rect.Height / 2);
                    links.Add(new(agent.Id, start, end));
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
        // Streams and halos follow the geometry they were built on; the content pass below rebuilds them.
        foreach (var id in _flows.Keys.ToArray()) RemoveFlow(id);
        foreach (var id in _halos.Keys.ToArray()) RemoveHalo(id);
        UpdateContent();
        _connections.InvalidateVisual();
        UpdateMotion();
    }

    private void PlaceProject(AgentActivityProject project, Rect hub, HashSet<string> present, ref int tabIndex)
    {
        var button = Node("project:" + project.Key, present);
        KeyboardNavigation.SetTabIndex(button, tabIndex++);
        button.SetResourceReference(Control.BackgroundProperty, "Bg2");
        button.SetResourceReference(Control.BorderBrushProperty, "AccentDim");
        button.ToolTip = project.Directory.Length == 0 ? "Chats without a project directory" : project.Directory;
        if (button.Content is not WorkflowProjectCard) button.Content = new WorkflowProjectCard();
        Place(button, hub);
    }

    private void PlaceAgent(AgentActivityNode agent, Rect rect, HashSet<string> present, ref int tabIndex)
    {
        var button = Node("agent:" + agent.Id, present);
        KeyboardNavigation.SetTabIndex(button, tabIndex++);
        button.SetResourceReference(Control.BorderBrushProperty, agent.IsCentralPlanner ? "Accent" : agent.Chat.IsBridgeManager ? "AccentDim" : "Border");
        if (button.Content is not WorkflowAgentCard) button.Content = new WorkflowAgentCard();
        Place(button, rect);
        _cards[agent.Id] = rect;
    }

    private double LayoutAdvanced(AgentActivityProject project, double x, double y, double width,
        HashSet<string> present, List<Connection> links, ref int tabIndex)
    {
        var hub = new Rect(x + (width - HubWidth) / 2, y + 10, HubWidth, HubHeight);
        PlaceProject(project, hub, present, ref tabIndex);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var nextY = hub.Bottom + 36;
        var index = tabIndex;

        void Groups(AgentActivityNode[] roots, Rect parent, bool managed)
        {
            var columns = Math.Max(1, Math.Min(roots.Length, Math.Min(3, (int)(width / 280))));
            var cell = width / columns;
            for (var first = 0; first < roots.Length; first += columns)
            {
                var bottom = nextY;
                for (var column = 0; column < columns && first + column < roots.Length; column++)
                {
                    var manager = roots[first + column];
                    if (!used.Add(manager.Id)) continue;
                    var cardWidth = Math.Min(320, cell - 24);
                    var rect = new Rect(x + column * cell + (cell - cardWidth) / 2, nextY, cardWidth, AgentHeight);
                    PlaceAgent(manager, rect, present, ref index);
                    links.Add(new(manager.Id, new(parent.Left + parent.Width / 2, parent.Bottom), new(rect.Left + rect.Width / 2, rect.Top),
                        vertical: true, managed: managed));
                    var workerY = rect.Bottom + 20;
                    foreach (var worker in project.Agents.Where(a => a.ParentAgentId == manager.Id && !a.IsCentralPlanner && !a.Chat.IsBridgeManager))
                    {
                        if (!used.Add(worker.Id)) continue;
                        var workerRect = new Rect(rect.Left + 18, workerY, rect.Width - 18, AgentHeight);
                        PlaceAgent(worker, workerRect, present, ref index);
                        // Route down the team's left gutter, never through earlier workers' cards.
                        links.Add(new(worker.Id, new(rect.Left + 7, rect.Bottom), new(workerRect.Left, workerRect.Top + AgentHeight / 2),
                            laneX: rect.Left + 7, managed: true));
                        workerY += AgentHeight + AgentGap;
                    }
                    bottom = Math.Max(bottom, workerY);
                }
                nextY = bottom + 32;
            }
        }

        foreach (var central in project.Agents.Where(a => a.IsCentralPlanner))
        {
            used.Add(central.Id);
            var rect = new Rect(x + (width - 320) / 2, nextY, 320, AgentHeight);
            PlaceAgent(central, rect, present, ref index);
            links.Add(new(central.Id, new(hub.Left + hub.Width / 2, hub.Bottom), new(rect.Left + rect.Width / 2, rect.Top), vertical: true));
            nextY = rect.Bottom + 36;
            Groups(project.Agents.Where(a => a.ParentAgentId == central.Id && a.Chat.IsBridgeManager && !a.IsCentralPlanner).ToArray(), rect, true);
        }
        Groups(project.Agents.Where(a => !used.Contains(a.Id) && a.Chat.IsBridgeManager).ToArray(), hub, false);
        Groups(project.Agents.Where(a => !used.Contains(a.Id)).ToArray(), hub, false);
        tabIndex = index;
        return nextY - y;
    }

    /// <summary>Bring every card, line, stream and halo up to date with the current snapshot. Positions are untouched.</summary>
    private void UpdateContent()
    {
        var nowUtc = DateTime.UtcNow;
        var linkStates = false;
        var working = new HashSet<string>(StringComparer.Ordinal);
        var glow = (TryFindResource("Green") as SolidColorBrush)?.Color ?? Colors.LightGreen;
        foreach (var project in _projects)
        {
            if (_nodes.TryGetValue("project:" + project.Key, out var hub) && hub.Content is WorkflowProjectCard face)
            {
                hub.Tag = project;
                face.Update(project);
                AutomationProperties.SetName(hub, $"Filter to {project.Name}, {project.Agents.Count} agents, {project.Working} working");
            }
            foreach (var agent in project.Agents)
            {
                if (!_nodes.TryGetValue("agent:" + agent.Id, out var button)) continue;
                var speed = agent.OpenRouterId is { } id ? SpeedLookup?.Invoke(id) : null;
                var flow = WorkflowFlow.For(speed?.TokensPerSecond);
                UpdateAgent(button, agent, flow, speed, nowUtc);
                if (_connections.Find(agent.Id) is not { } link) continue;
                if (link.State != agent.State) { link.State = agent.State; linkStates = true; }
                if (agent.State != AgentActivityState.Working) continue;
                working.Add(agent.Id);
                if (!_flows.TryGetValue(agent.Id, out var stream))
                {
                    _flows[agent.Id] = stream = new FlowStream(link.Geometry);
                    foreach (var path in stream.Paths) { SetZIndex(path, FlowZ); Children.Add(path); }
                }
                stream.Show(flow, _animating);
                if (!_halos.TryGetValue(agent.Id, out var halo) && _cards.TryGetValue(agent.Id, out var card))
                {
                    _halos[agent.Id] = halo = new Halo(card);
                    SetZIndex(halo.Element, HaloZ);
                    Children.Add(halo.Element);
                    halo.SetRunning(_animating);
                }
                if (halo is not null) halo.Ink = glow;
            }
        }
        foreach (var id in _flows.Keys.Where(id => !working.Contains(id)).ToArray()) RemoveFlow(id);
        foreach (var id in _halos.Keys.Where(id => !working.Contains(id)).ToArray()) RemoveHalo(id);
        if (linkStates) _connections.InvalidateVisual();
    }

    private void UpdateAgent(Button button, AgentActivityNode agent, WorkflowFlow flow, ModelSpeed? speed, DateTime nowUtc)
    {
        if (button.Tag is not AgentActivityNode previous || previous.State != agent.State)
            button.SetResourceReference(RailProperty, StateResource(agent.State));
        button.Tag = agent;
        if (button.Content is WorkflowAgentCard card) card.Update(agent, flow, speed, nowUtc, ticking: _animating);
        var now = agent.Now ?? WorkflowAgentCard.Resting(agent, nowUtc);
        var pace = flow.Measured ? $"{flow.TokensPerSecond:0} tok/s on OpenRouter" : "no OpenRouter speed";
        button.ToolTip = $"{agent.Label} · {agent.Role}\n{agent.Task}\nNow: {now.Text}"
            + (agent.Last is { } last ? $"\nLast: {last.Text}" : "")
            + (agent.ActiveChildren > 0 ? "\n\n" + WorkflowActivity.DescribeSubagents(agent) + "\n" : "")
            + $"\n{agent.Model} · {pace}\n{agent.Directory}\n{agent.Status}"
            + (agent.Reported.Length > 0 && agent.Last?.Text != agent.Reported ? "\nReported: " + agent.Reported : "")
            + (agent.Account.Length > 0 ? "\n" + agent.Account : "")
            + (agent.IsCentralPlanner ? "\nOpen this planner's Bridge" : "\nOpen this chat");
        AutomationProperties.SetName(button, $"Open {agent.Label}: {agent.Task}. {agent.Status}. Now {now.Text}. {agent.Role}.");
    }

    private void RemoveFlow(string id)
    {
        if (!_flows.Remove(id, out var stream)) return;
        stream.SetRunning(false);
        foreach (var path in stream.Paths) Children.Remove(path);
    }

    private void RemoveHalo(string id)
    {
        if (!_halos.Remove(id, out var halo)) return;
        halo.SetRunning(false);
        Children.Remove(halo.Element);
    }

    private Button Node(string id, HashSet<string> present)
    {
        present.Add(id);
        if (_nodes.TryGetValue(id, out var node)) return node;
        node = new Button { Cursor = Cursors.Hand, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(14, 11, 14, 11) };
        node.SetResourceReference(StyleProperty, "WorkflowNode");
        node.Click += (_, _) =>
        {
            if (node.Tag is AgentActivityNode agent) AgentSelected?.Invoke(agent);
            else if (node.Tag is AgentActivityProject project) ProjectSelected?.Invoke(project.Key);
        };
        node.MouseRightButtonUp += (_, e) =>
        {
            if (node.Tag is not AgentActivityNode agent) return;
            AgentMenuRequested?.Invoke(agent);
            e.Handled = true;
        };
        _nodes[id] = node;
        Children.Add(node);
        return node;
    }

    private static void Place(FrameworkElement element, Rect rect)
    { SetLeft(element, rect.Left); SetTop(element, rect.Top); element.Width = rect.Width; element.Height = rect.Height; }

    /// <summary>A line from a parent (project, planner or orchestrator) down to one agent, keyed by that agent.</summary>
    private sealed class Connection(string id, Point start, Point end, bool vertical = false, double? laneX = null, bool managed = false)
    {
        public string Id { get; } = id;
        public Point Start { get; } = start;
        public Point End { get; } = end;
        public bool Managed { get; } = managed;
        public AgentActivityState State { get; set; } = AgentActivityState.Idle;
        // Geometry belongs to the layout, not to a frame or a state change.
        public StreamGeometry Geometry { get; } = Build(start, end, vertical, laneX);

        private static StreamGeometry Build(Point start, Point end, bool vertical, double? lane)
        {
            var c1 = vertical ? new Point(start.X, (start.Y + end.Y) / 2) : new Point(lane ?? (start.X + end.X) / 2, start.Y);
            var c2 = vertical ? new Point(end.X, (start.Y + end.Y) / 2) : new Point(lane ?? (start.X + end.X) / 2, end.Y);
            var geometry = new StreamGeometry();
            using (var path = geometry.Open())
            { path.BeginFigure(start, false, false); path.BezierTo(c1, c2, end, true, false); }
            geometry.Freeze();
            return geometry;
        }
    }

    /// <summary>
    /// Packets travelling one working connection: a soft glow, a trailing streak that brightens toward the packet,
    /// and a bright head, each a dashed stroke of the same geometry. Advancing every dash offset by exactly one
    /// pattern per period moves the whole stream one packet along with nothing to wrap, so the loop is seamless.
    /// Offsets grow, which moves the pattern toward the path's start - the parent - so packets run from the agent
    /// into its project or manager.
    /// </summary>
    private sealed class FlowStream
    {
        // The head is the near-white of the text ink so it reads as light travelling on the green line, not as more
        // line. Two streak layers of different lengths give each packet a tail that fades away from it.
        private readonly Path _glow = Stroke(12, "Green"), _trail = Stroke(2.4, "Green"), _tail = Stroke(3, "Green"), _head = Stroke(4.4, "Text");
        private WorkflowFlow? _flow;
        private bool _running;

        public FlowStream(Geometry geometry)
        {
            foreach (var path in Paths) path.Data = geometry;
            _head.StrokeDashCap = _glow.StrokeDashCap = PenLineCap.Round;
            _trail.StrokeDashCap = _tail.StrokeDashCap = PenLineCap.Flat;
        }

        public IEnumerable<Path> Paths => [_glow, _trail, _tail, _head];

        public void Show(WorkflowFlow flow, bool running)
        {
            if (_flow == flow && _running == running) return;
            var restart = _flow != flow;
            _flow = flow;
            // A zero-length dash with round caps is a dot of the stroke's diameter; one per Spacing pixels.
            _glow.StrokeDashArray = [0, flow.Spacing / _glow.StrokeThickness];
            _head.StrokeDashArray = [0, flow.Spacing / _head.StrokeThickness];
            // The streak starts under the head and trails back toward the agent, longer when the model is faster.
            var streak = Math.Clamp(flow.PixelsPerSecond * 0.2, 10, flow.Spacing * 0.75);
            Dashes(_trail, streak, flow.Spacing);
            Dashes(_tail, streak * 0.45, flow.Spacing);
            // An unmeasured model streams at a neutral pace, drawn dimmer so it never passes for a measured one.
            _head.Opacity = flow.Measured ? 0.95 : 0.5;
            _tail.Opacity = flow.Measured ? 0.6 : 0.28;
            _trail.Opacity = flow.Measured ? 0.3 : 0.14;
            _glow.Opacity = flow.Measured ? 0.26 : 0.12;
            if (restart) _running = !running;
            SetRunning(running);
        }

        public void SetRunning(bool running)
        {
            if (_running == running || _flow is not { } flow) { _running = running; return; }
            _running = running;
            foreach (var path in Paths)
            {
                path.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
                if (!running) { path.BeginAnimation(Shape.StrokeDashOffsetProperty, null); continue; }
                var pattern = flow.Spacing / path.StrokeThickness;
                path.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation(0, pattern, new Duration(TimeSpan.FromSeconds(flow.Period)))
                { RepeatBehavior = RepeatBehavior.Forever });
            }
        }

        /// <summary>One <paramref name="length"/>-pixel dash per packet, starting at the packet. Dash arrays count in
        /// stroke widths, so pixels are divided by the thickness.</summary>
        private static void Dashes(Path path, double length, double spacing) =>
            path.StrokeDashArray = [length / path.StrokeThickness, (spacing - length) / path.StrokeThickness];

        private static Path Stroke(double thickness, string ink)
        {
            var path = new Path { StrokeThickness = thickness, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
            path.SetResourceReference(Shape.StrokeProperty, ink);
            return path;
        }
    }

    /// <summary>A soft glow behind a working agent's card. The card covers the shape itself, so only the blurred
    /// rim shows. It breathes slowly while motion is on and rests at a fixed glow otherwise, so a still wall still
    /// shows which agents are working.</summary>
    private sealed class Halo
    {
        private readonly System.Windows.Media.Effects.DropShadowEffect _glow = new() { ShadowDepth = 0, BlurRadius = 22, Opacity = 0.55 };
        private bool? _running;
        public Border Element { get; } = new() { CornerRadius = new CornerRadius(12), IsHitTestVisible = false };

        public Halo(Rect card)
        {
            Element.SetResourceReference(Border.BackgroundProperty, "Bg1");
            Element.Effect = _glow;
            // Breathing changes only opacity, so the blurred glow is rasterised once and composited each frame.
            Element.CacheMode = new BitmapCache();
            Place(Element, card);
        }

        /// <summary>The glow follows the theme's working ink. An effect colour cannot be a resource reference.</summary>
        public Color Ink { set { if (_glow.Color != value) _glow.Color = value; } }

        public void SetRunning(bool running)
        {
            if (_running == running) return;
            _running = running;
            if (!running)
            {
                Element.BeginAnimation(OpacityProperty, null);
                Element.Opacity = 0.8;
                return;
            }
            Element.BeginAnimation(OpacityProperty, new DoubleAnimation(0.35, 1, new Duration(TimeSpan.FromSeconds(1.8)))
            {
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            });
        }
    }

    /// <summary>
    /// The still part of every connection, drawn in one pass: working lines glow and fade from the agent's green
    /// into the project's accent; waiting and stopped lines are amber, errors red, idle lines a quiet dash. Each
    /// ink is a resource reference, so a theme switch repaints the lines with nothing else changing.
    /// </summary>
    private sealed class ConnectionLayer : FrameworkElement
    {
        private static readonly DependencyProperty AccentInkProperty = Ink("AccentInk");
        private static readonly DependencyProperty GreenInkProperty = Ink("GreenInk");
        private static readonly DependencyProperty AmberInkProperty = Ink("AmberInk");
        private static readonly DependencyProperty RedInkProperty = Ink("RedInk");
        private static readonly DependencyProperty IdleInkProperty = Ink("IdleInk");
        private static readonly DependencyProperty SurfaceInkProperty = Ink("SurfaceInk");
        private IReadOnlyList<Connection> _links = [];
        private Dictionary<string, Connection> _byId = new(StringComparer.Ordinal);

        /// <summary>While packets are moving the working line steps back so they stand out; a still wall keeps the
        /// line at full strength, because then the line is the whole signal.</summary>
        public bool Streaming { get; set; }

        public ConnectionLayer()
        {
            IsHitTestVisible = false;
            SetResourceReference(AccentInkProperty, "Accent");
            SetResourceReference(GreenInkProperty, "Green");
            SetResourceReference(AmberInkProperty, "Amber");
            SetResourceReference(RedInkProperty, "Red");
            SetResourceReference(IdleInkProperty, "Border");
            SetResourceReference(SurfaceInkProperty, "CodeBg");
        }

        public IReadOnlyList<Connection> Links
        {
            get => _links;
            set
            {
                _links = value;
                _byId = value.GroupBy(link => link.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
            }
        }

        public Connection? Find(string id) => _byId.GetValueOrDefault(id);

        private static DependencyProperty Ink(string name) => DependencyProperty.Register(name, typeof(Brush), typeof(ConnectionLayer),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        private Color InkColor(DependencyProperty property, Color fallback) => GetValue(property) is SolidColorBrush brush ? brush.Color : fallback;

        protected override void OnRender(DrawingContext dc)
        {
            var accent = InkColor(AccentInkProperty, Colors.CornflowerBlue);
            var green = InkColor(GreenInkProperty, Colors.LightGreen);
            var amber = InkColor(AmberInkProperty, Colors.Goldenrod);
            var red = InkColor(RedInkProperty, Colors.IndianRed);
            var idle = InkColor(IdleInkProperty, Colors.DimGray);
            var surface = Frozen(new SolidColorBrush(InkColor(SurfaceInkProperty, Colors.Black)));
            var glow = FrozenPen(Tint(green, Streaming ? 0.08 : 0.13), 7);
            var line = Streaming ? 0.55 : 1;
            var idlePen = FrozenPen(new SolidColorBrush(idle), 1, [4, 4]);
            var waitingPen = FrozenPen(Tint(amber, 0.75), 1.2, [3, 3]);
            var stoppedPen = FrozenPen(Tint(amber, 0.6), 1.2, [1, 3]);
            var errorPen = FrozenPen(Tint(red, 0.75), 1.3);
            var accentRing = FrozenPen(new SolidColorBrush(accent), 1.2);
            var idleRing = FrozenPen(new SolidColorBrush(idle), 1);
            foreach (var link in _links)
            {
                Color port;
                switch (link.State)
                {
                    case AgentActivityState.Working:
                        dc.DrawGeometry(null, glow, link.Geometry);
                        // The line itself carries the direction of travel: agent green running into project accent.
                        var fade = new LinearGradientBrush(green, accent, 0)
                        { MappingMode = BrushMappingMode.Absolute, StartPoint = link.End, EndPoint = link.Start, Opacity = line };
                        dc.DrawGeometry(null, FrozenPen(Frozen(fade), 1.6), link.Geometry);
                        port = green;
                        break;
                    case AgentActivityState.Waiting:
                        dc.DrawGeometry(null, waitingPen, link.Geometry);
                        port = amber;
                        break;
                    case AgentActivityState.Paused:
                        dc.DrawGeometry(null, stoppedPen, link.Geometry);
                        port = amber;
                        break;
                    case AgentActivityState.Error:
                        dc.DrawGeometry(null, errorPen, link.Geometry);
                        port = red;
                        break;
                    default:
                        dc.DrawGeometry(null, idlePen, link.Geometry);
                        port = idle;
                        break;
                }
                // A filled port on the agent, a hollow one on the parent it reports to.
                dc.DrawEllipse(Frozen(new SolidColorBrush(port)), null, link.End, 3, 3);
                dc.DrawEllipse(surface, link.State == AgentActivityState.Working ? accentRing : idleRing, link.Start, 3.2, 3.2);
            }
        }

        private static Brush Tint(Color color, double alpha) => Frozen(new SolidColorBrush(Color.FromArgb((byte)Math.Round(color.A * alpha), color.R, color.G, color.B)));

        private static T Frozen<T>(T freezable) where T : Freezable { freezable.Freeze(); return freezable; }

        private static Pen FrozenPen(Brush brush, double thickness, double[]? dashes = null)
        {
            var pen = new Pen(brush, thickness);
            if (dashes is not null) pen.DashStyle = new DashStyle(dashes, 0);
            pen.Freeze();
            return pen;
        }
    }
}
