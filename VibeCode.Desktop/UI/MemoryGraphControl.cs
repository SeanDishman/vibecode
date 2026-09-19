using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using VibeCode.Services;

namespace VibeCode.UI;

/// <summary>Lightweight force-directed renderer for AgentMemory's knowledge graph. No browser or WebView required.</summary>
public sealed class MemoryGraphControl : FrameworkElement
{
    private const int MaxNodes = 340;
    private static readonly Typeface LabelFace = new("Segoe UI Semibold");
    private static readonly Typeface MetaFace = new("Segoe UI");

    private static readonly DependencyPropertyKey MatchCountKey = DependencyProperty.RegisterReadOnly(
        nameof(MatchCount), typeof(int), typeof(MemoryGraphControl), new PropertyMetadata(0));

    public static readonly DependencyProperty MatchCountProperty = MatchCountKey.DependencyProperty;

    /// <summary>How many drawn nodes the current query matches. Zero with no query means the map itself is empty.</summary>
    public int MatchCount => (int)GetValue(MatchCountProperty);

    public static readonly DependencyProperty GraphProperty = DependencyProperty.Register(
        nameof(Graph), typeof(AgentMemoryGraphSnapshot), typeof(MemoryGraphControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnGraphChanged));

    public static readonly DependencyProperty SelectedNodeProperty = DependencyProperty.Register(
        nameof(SelectedNode), typeof(AgentMemoryGraphNode), typeof(MemoryGraphControl),
        new FrameworkPropertyMetadata(null,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty SearchTextProperty = DependencyProperty.Register(
        nameof(SearchText), typeof(string), typeof(MemoryGraphControl),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender, OnSearchChanged));

    public AgentMemoryGraphSnapshot? Graph
    {
        get => (AgentMemoryGraphSnapshot?)GetValue(GraphProperty);
        set => SetValue(GraphProperty, value);
    }

    public AgentMemoryGraphNode? SelectedNode
    {
        get => (AgentMemoryGraphNode?)GetValue(SelectedNodeProperty);
        set => SetValue(SelectedNodeProperty, value);
    }

    public string SearchText
    {
        get => (string)GetValue(SearchTextProperty);
        set => SetValue(SearchTextProperty, value);
    }

    private List<AgentMemoryGraphNode> _nodes = new();
    private List<AgentMemoryGraphEdge> _edges = new();
    private readonly Dictionary<string, Point> _positions = new(StringComparer.Ordinal);
    // The floor used to be 0.18, which stopped the wheel while a large corpus still ran off every edge - there was
    // no way to see the whole shape at once. Nodes bottom out at a 3px dot well before this, so going further out
    // keeps shrinking the layout rather than the circles.
    private const double MinZoom = 0.05;
    private const double MaxZoom = 4.5;
    private double _zoom = 1;
    private Vector _pan;
    private Point _dragStart;
    private Vector _dragOrigin;
    private bool _panning;
    private bool _moved;
    private bool _hasFitted;
    private bool _userAdjusted;

    public MemoryGraphControl()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = Cursors.Hand;
        Loaded += (_, _) =>
        {
            if (!_hasFitted) FitToView();
        };
        SizeChanged += (_, _) =>
        {
            if (!_hasFitted && _nodes.Count > 0) FitToView();
        };
    }

    private static void OnGraphChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        var map = (MemoryGraphControl)target;
        map.BuildLayout();
        if (map.SelectedNode is { } selected && map._nodes.All(node => node.Id != selected.Id))
            map.SelectedNode = null;
        // The map refreshes itself while it is open. Yanking the view back to fit every time would throw away the
        // zoom or pan the user just set, so only auto-fit while they have not taken the view over.
        if (map._userAdjusted) return;
        map._hasFitted = false;
        map.Dispatcher.BeginInvoke(map.FitToView, DispatcherPriority.Loaded);
    }

    private static void OnSearchChanged(DependencyObject target, DependencyPropertyChangedEventArgs args) =>
        ((MemoryGraphControl)target).UpdateMatchCount();

    private void UpdateMatchCount() =>
        SetValue(MatchCountKey, _nodes.Count(IsMatch));

    private void BuildLayout()
    {
        // A refresh mostly returns the same nodes. Re-seeding from where they already are keeps the map recognisable
        // instead of reshuffling the whole picture every time it polls.
        var previous = new Dictionary<string, Point>(_positions, StringComparer.Ordinal);
        _positions.Clear();
        var sourceNodes = Graph?.Nodes ?? Array.Empty<AgentMemoryGraphNode>();
        _nodes = sourceNodes
            .OrderByDescending(node => node.Strength)
            .ThenByDescending(node => node.UpdatedAt ?? DateTimeOffset.MinValue)
            .Take(MaxNodes).ToList();
        UpdateMatchCount();
        var visibleIds = _nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        _edges = (Graph?.Edges ?? Array.Empty<AgentMemoryGraphEdge>())
            .Where(edge => visibleIds.Contains(edge.SourceId) && visibleIds.Contains(edge.TargetId))
            .ToList();
        if (_nodes.Count == 0)
        {
            InvalidateVisual();
            return;
        }

        var groups = _nodes.GroupBy(node => NormalizeType(node.Type), StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase).ToList();
        for (var groupIndex = 0; groupIndex < groups.Count; groupIndex++)
        {
            var group = groups[groupIndex].ToList();
            var clusterAngle = Math.PI * 2 * groupIndex / Math.Max(1, groups.Count) - Math.PI / 2;
            var anchor = new Vector(Math.Cos(clusterAngle) * 210, Math.Sin(clusterAngle) * 210);
            for (var itemIndex = 0; itemIndex < group.Count; itemIndex++)
            {
                var angle = itemIndex * 2.399963229728653 + clusterAngle;
                var radius = 25 + Math.Sqrt(itemIndex) * 35;
                _positions[group[itemIndex].Id] = previous.TryGetValue(group[itemIndex].Id, out var settled)
                    ? settled
                    : new Point(anchor.X + Math.Cos(angle) * radius, anchor.Y + Math.Sin(angle) * radius);
            }
        }
        // Nodes that survived a refresh are already relaxed; only a genuinely new layout needs the full solve.
        RelaxLayout(_nodes.Count(node => !previous.ContainsKey(node.Id)) > _nodes.Count / 4);
        InvalidateVisual();
    }

    private void RelaxLayout(bool fullSolve = true)
    {
        var count = _nodes.Count;
        var points = _nodes.Select(node => _positions[node.Id]).ToArray();
        var index = _nodes.Select((node, i) => (node.Id, i)).ToDictionary(pair => pair.Id, pair => pair.i, StringComparer.Ordinal);
        var links = _edges.Where(edge => index.ContainsKey(edge.SourceId) && index.ContainsKey(edge.TargetId))
            .Select(edge => (A: index[edge.SourceId], B: index[edge.TargetId])).ToArray();
        var typeOrder = _nodes.Select(node => NormalizeType(node.Type)).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(type => type, StringComparer.OrdinalIgnoreCase).ToList();
        var anchors = typeOrder.Select((type, i) =>
        {
            var angle = Math.PI * 2 * i / Math.Max(1, typeOrder.Count) - Math.PI / 2;
            return (type, point: new Point(Math.Cos(angle) * 210, Math.Sin(angle) * 210));
        }).ToDictionary(pair => pair.type, pair => pair.point, StringComparer.OrdinalIgnoreCase);

        // Sessions are hubs, not a category: anchoring them all to one "session" point stacks every chat on top of
        // its neighbours. Give each its own slot on a wider ring so its own turns gather around it.
        var hubs = _nodes.Where(node => NormalizeType(node.Type) == "session").Select(node => node.Id).ToList();
        // The ring has to grow with the corpus: mutual repulsion pushes 240 observations into a wide disc, and a
        // fixed radius would leave every hub buried in the middle of it.
        var hubRadius = Math.Max(300.0, 180 + count * 1.6);
        var hubAnchors = hubs.Select((id, i) =>
        {
            var angle = Math.PI * 2 * i / Math.Max(1, hubs.Count) - Math.PI / 2;
            return (id, point: new Point(Math.Cos(angle) * hubRadius, Math.Sin(angle) * hubRadius));
        }).ToDictionary(pair => pair.id, pair => pair.point, StringComparer.Ordinal);

        // Relaxation is O(n²) per pass. Keep a big observation map responsive rather than pixel-perfect.
        var passes = fullSolve ? count > 220 ? 44 : 72 : 10;
        for (var iteration = 0; iteration < passes; iteration++)
        {
            var forces = new Vector[count];
            for (var a = 0; a < count; a++)
            for (var b = a + 1; b < count; b++)
            {
                var delta = points[a] - points[b];
                var distanceSquared = Math.Max(100, delta.LengthSquared);
                if (delta.LengthSquared < 0.01) delta = new Vector(1, 0);
                var repulsion = delta * (11000 / distanceSquared / Math.Sqrt(distanceSquared));
                forces[a] += repulsion;
                forces[b] -= repulsion;
            }

            foreach (var (a, b) in links)
            {
                var delta = points[b] - points[a];
                var distance = Math.Max(1, delta.Length);
                var spring = delta * ((distance - 132) * 0.012 / distance);
                forces[a] += spring;
                forces[b] -= spring;
            }

            for (var i = 0; i < count; i++)
            {
                var isHub = hubAnchors.TryGetValue(_nodes[i].Id, out var hub);
                var anchor = isHub ? hub : anchors[NormalizeType(_nodes[i].Type)];
                forces[i] += (anchor - points[i]) * (isHub ? 0.03 : 0.006);
                if (!isHub) forces[i] += new Vector(-points[i].X, -points[i].Y) * 0.0015;
                var movement = forces[i] * (0.62 - iteration * (0.36 / passes));
                if (movement.Length > 13) movement *= 13 / movement.Length;
                points[i] += movement;
            }
        }

        for (var i = 0; i < count; i++) _positions[_nodes[i].Id] = points[i];
    }

    public void FitToView()
    {
        FitTo(_positions.Values);
        _userAdjusted = false;
    }

    /// <summary>Zoom to what the query hit. Dimming alone is useless once the map holds hundreds of nodes.</summary>
    public void FitToMatches()
    {
        var matches = _nodes.Where(IsMatch)
            .Select(node => _positions.TryGetValue(node.Id, out var point) ? point : (Point?)null)
            .OfType<Point>().ToArray();
        if (matches.Length == 0) FitToView();
        else FitTo(matches);
        // A deliberate zoom to results is a view the next refresh must not throw away.
        _userAdjusted = matches.Length > 0;
    }

    private void FitTo(IReadOnlyCollection<Point> points)
    {
        if (points.Count == 0 || ActualWidth < 40 || ActualHeight < 40) return;
        var minX = points.Min(point => point.X);
        var maxX = points.Max(point => point.X);
        var minY = points.Min(point => point.Y);
        var maxY = points.Max(point => point.Y);
        // Leave room for labels and for the interaction hint overlaid at the bottom of the graph card. Fitting only
        // the node centers made the lowest file label technically visible but crowded by the hint.
        var spanX = Math.Max(160, maxX - minX + 120);
        var spanY = Math.Max(160, maxY - minY + 170);
        _zoom = Math.Clamp(Math.Min(ActualWidth / spanX, ActualHeight / spanY), 0.25, 2.2);
        _pan = new Vector(-(minX + maxX) * 0.5 * _zoom, -(minY + maxY) * 0.5 * _zoom);
        _hasFitted = true;
        InvalidateVisual();
    }

    public void ZoomBy(double factor) => ZoomAt(factor, new Point(ActualWidth / 2, ActualHeight / 2));

    private void ZoomAt(double factor, Point screenPoint)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var world = ScreenToWorld(screenPoint);
        _zoom = Math.Clamp(_zoom * factor, MinZoom, MaxZoom);
        _pan = new Vector(
            screenPoint.X - ActualWidth / 2 - world.X * _zoom,
            screenPoint.Y - ActualHeight / 2 - world.Y * _zoom);
        _hasFitted = true;
        _userAdjusted = true;
        InvalidateVisual();
    }

    private Point WorldToScreen(Point point) => new(
        point.X * _zoom + ActualWidth / 2 + _pan.X,
        point.Y * _zoom + ActualHeight / 2 + _pan.Y);

    private Point ScreenToWorld(Point point) => new(
        (point.X - ActualWidth / 2 - _pan.X) / _zoom,
        (point.Y - ActualHeight / 2 - _pan.Y) / _zoom);

    internal static string NormalizeType(string? type)
    {
        var value = (type ?? "fact").Trim().ToLowerInvariant();
        if (value == "session") return "session";
        if (value is "prompt" or "conversation") return "prompt";
        if (value is "reply" or "answer") return "reply";
        if (value is "command" or "command_run") return "command";
        if (value.Contains("decision")) return "decision";
        if (value.Contains("architect") || value is "library" or "project") return "architecture";
        if (value.Contains("preference") || value.Contains("person")) return "preference";
        if (value.Contains("bug") || value.Contains("error")) return "bug";
        if (value.Contains("pattern") || value.Contains("workflow")) return "pattern";
        if (value.Contains("file") || value.Contains("function")) return "file";
        return "fact";
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(BrushFor(Color.FromRgb(23, 24, 28)), null, new Rect(RenderSize));
        DrawGrid(dc);
        if (_nodes.Count == 0)
        {
            DrawCenteredText(dc, "No memories mapped yet", ActualHeight / 2 - 10, 13, Color.FromRgb(108, 110, 119));
            DrawCenteredText(dc, "Keep chatting — useful decisions and patterns will appear here.", ActualHeight / 2 + 14, 10.5, Color.FromRgb(84, 86, 94));
            return;
        }

        var selectedId = SelectedNode?.Id;
        var queryActive = !string.IsNullOrWhiteSpace(SearchText);
        foreach (var edge in _edges)
        {
            if (!_positions.TryGetValue(edge.SourceId, out var sourceWorld) ||
                !_positions.TryGetValue(edge.TargetId, out var targetWorld)) continue;
            var sourceNode = _nodes.FirstOrDefault(node => node.Id == edge.SourceId);
            var targetNode = _nodes.FirstOrDefault(node => node.Id == edge.TargetId);
            if (sourceNode is null || targetNode is null) continue;
            var source = WorldToScreen(sourceWorld);
            var target = WorldToScreen(targetWorld);
            if (!Visible(source) && !Visible(target)) continue;
            var selected = selectedId == edge.SourceId || selectedId == edge.TargetId;
            var matched = !queryActive || IsMatch(sourceNode) || IsMatch(targetNode);
            var color = selected ? Color.FromArgb(190, 105, 150, 247)
                : Color.FromArgb(matched ? (byte)72 : (byte)18, 102, 106, 122);
            var pen = new Pen(BrushFor(color), selected ? 1.65 : 0.85);
            pen.Freeze();
            dc.DrawLine(pen, source, target);
            if (selected && _zoom > 0.62 && !string.IsNullOrWhiteSpace(edge.Label))
                DrawEdgeLabel(dc, edge.Label, source, target);
        }

        foreach (var node in _nodes)
        {
            if (_positions.TryGetValue(node.Id, out var position)) DrawNode(dc, node, WorldToScreen(position));
        }

        if (queryActive && MatchCount == 0)
        {
            DrawCenteredText(dc, $"Nothing matches “{SearchText.Trim()}”", ActualHeight / 2 - 10, 13,
                Color.FromRgb(108, 110, 119));
            DrawCenteredText(dc, "Try a project name, a file, an error, or a phrase you used.",
                ActualHeight / 2 + 14, 10.5, Color.FromRgb(84, 86, 94));
        }
    }

    private void DrawGrid(DrawingContext dc)
    {
        const double spacing = 32;
        var offsetX = (ActualWidth / 2 + _pan.X) % spacing;
        var offsetY = (ActualHeight / 2 + _pan.Y) % spacing;
        var dot = BrushFor(Color.FromArgb(38, 100, 104, 118));
        for (var x = offsetX; x < ActualWidth; x += spacing)
        for (var y = offsetY; y < ActualHeight; y += spacing)
            dc.DrawEllipse(dot, null, new Point(x, y), 0.75, 0.75);
    }

    private void DrawEdgeLabel(DrawingContext dc, string label, Point source, Point target)
    {
        var text = label.Replace('_', ' ');
        if (text.Length > 22) text = text[..21] + "…";
        var formatted = MakeText(text, 8.5, Color.FromRgb(133, 137, 151), MetaFace);
        var center = new Point((source.X + target.X) / 2, (source.Y + target.Y) / 2);
        var box = new Rect(center.X - formatted.Width / 2 - 5, center.Y - formatted.Height / 2 - 2,
            formatted.Width + 10, formatted.Height + 4);
        dc.DrawRoundedRectangle(BrushFor(Color.FromArgb(224, 32, 33, 38)), null, box, 5, 5);
        dc.DrawText(formatted, new Point(center.X - formatted.Width / 2, center.Y - formatted.Height / 2));
    }

    private void DrawNode(DrawingContext dc, AgentMemoryGraphNode node, Point center)
    {
        if (!Visible(center, 70)) return;
        var matched = IsMatch(node);
        var selected = SelectedNode?.Id == node.Id;
        // The floor used to be 7px, which the whole map hits below roughly 0.35 zoom - so every node pinned to the
        // same size just as the layout was pulling them together, and the map turned into one undifferentiated
        // clump. Letting them keep shrinking preserves the strength differences that make the shape readable.
        var radius = NodeRadius(node, _zoom, 3);
        // Zoomed far out the hairline outline stops reading as depth and hundreds of them merge into a smear.
        // Below this size a node is just a dot.
        var detailed = radius >= 6.5;
        var baseColor = ColorForType(node.Type);
        if (!matched) baseColor.A = 40;

        // A halo belongs to the selected node alone. Drawing a soft one behind every node ringed the entire map in
        // glow and more than doubled each node's footprint, so neighbours bled into each other at any real density.
        if (selected)
        {
            dc.DrawEllipse(BrushFor(Color.FromArgb(40, 76, 141, 245)), null, center, radius + 13, radius + 13);
            dc.DrawEllipse(null, new Pen(BrushFor(Color.FromArgb(230, 236, 237, 241)), 1.5), center, radius + 4, radius + 4);
        }

        var outline = detailed
            ? new Pen(BrushFor(Color.FromArgb(matched ? (byte)120 : (byte)25, 236, 237, 241)), 0.75)
            : null;
        dc.DrawEllipse(BrushFor(baseColor), outline, center, radius, radius);

        if (radius >= 9)
        {
            var glyph = GlyphFor(node.Type);
            var glyphText = MakeText(glyph, Math.Clamp(radius * 0.72, 7, 11),
                matched ? Color.FromRgb(248, 249, 252) : Color.FromArgb(70, 248, 249, 252), LabelFace);
            dc.DrawText(glyphText, new Point(center.X - glyphText.Width / 2, center.Y - glyphText.Height / 2));
        }

        if (_zoom < 0.42 || (!matched && !selected) || !ShouldLabel(node, selected)) return;
        var label = string.IsNullOrWhiteSpace(node.Label) ? "Memory" : node.Label.Trim();
        if (label.Length > 24) label = label[..23] + "…";
        var labelText = MakeText(label, selected ? 11 : 10,
            selected ? Color.FromRgb(236, 237, 241) : Color.FromRgb(166, 168, 177), LabelFace);
        var labelPoint = new Point(center.X - labelText.Width / 2, center.Y + radius + 5);
        if (selected)
        {
            var box = new Rect(labelPoint.X - 5, labelPoint.Y - 2, labelText.Width + 10, labelText.Height + 4);
            dc.DrawRoundedRectangle(BrushFor(Color.FromArgb(225, 32, 33, 38)), null, box, 5, 5);
        }
        dc.DrawText(labelText, labelPoint);
    }

    /// <summary>Strength drives the size, and the spread has to be wide enough to actually read. At the old
    /// 10.5 + strength * 5 a half-strength memory came out 84% the width of a full-strength one - close enough to
    /// look the same in a crowd. Widening the slope pins the top end exactly where it was and only brings the
    /// middle and low end down, so strong memories are unchanged and weaker ones read as visibly smaller.</summary>
    private static double NodeRadius(AgentMemoryGraphNode node, double zoom, double floor)
    {
        var strength = node.Strength <= 0 ? 0.25 : Math.Clamp(node.Strength, 0, 1);
        return Math.Clamp((8 + strength * 7.5) * Math.Sqrt(zoom), floor, 23);
    }

    /// <summary>Labelling 200 nodes at fit-zoom is a wall of overlapping text. Anchors and durable memories are few
    /// enough to always carry one; conversation earns one as you zoom in, tool traffic last. A selection or a
    /// search hit always wins - that is how you find the node you were looking for.</summary>
    private bool ShouldLabel(AgentMemoryGraphNode node, bool selected)
    {
        if (selected || !string.IsNullOrWhiteSpace(SearchText)) return true;
        return NormalizeType(node.Type) switch
        {
            "session" or "concept" or "decision" or "preference" or "architecture" or "pattern" => true,
            "prompt" or "reply" or "bug" => _zoom > 0.62,
            _ => _zoom > 0.92,
        };
    }

    private bool IsMatch(AgentMemoryGraphNode node) => Matches(node, SearchText);

    /// <summary>Every whitespace-separated term must appear somewhere on the node, so "grok error" narrows
    /// instead of matching everything that mentions either word.</summary>
    internal static bool Matches(AgentMemoryGraphNode node, string? search)
    {
        var terms = (search ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) return true;
        return terms.All(term => Contains(node.Label, term) || Contains(node.Type, term) || Contains(node.Content, term)
                                 || node.Concepts.Any(value => Contains(value, term))
                                 || node.Files.Any(value => Contains(value, term)));
    }

    private static bool Contains(string? value, string query) =>
        value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;

    private static string GlyphFor(string? type) => NormalizeType(type) switch
    {
        "session" => "S",
        "prompt" => "?",
        "reply" => "A",
        "command" => ">",
        "decision" => "D",
        "architecture" => "A",
        "preference" => "P",
        "bug" => "!",
        "pattern" => "↻",
        "file" => "F",
        _ => "·",
    };

    internal static Color ColorForType(string? type) => NormalizeType(type) switch
    {
        "session" => Color.FromRgb(242, 166, 90),
        "prompt" => Color.FromRgb(139, 124, 246),
        "reply" => Color.FromRgb(76, 141, 245),
        "command" => Color.FromRgb(126, 208, 166),
        "decision" => Color.FromRgb(139, 124, 246),
        "architecture" => Color.FromRgb(76, 141, 245),
        "preference" => Color.FromRgb(242, 166, 90),
        "bug" => Color.FromRgb(232, 106, 120),
        "pattern" => Color.FromRgb(126, 208, 166),
        "file" => Color.FromRgb(143, 174, 230),
        _ => Color.FromRgb(112, 149, 184),
    };

    private FormattedText MakeText(string text, double size, Color color, Typeface face) => new(
        text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, BrushFor(color),
        VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private void DrawCenteredText(DrawingContext dc, string text, double y, double size, Color color)
    {
        var formatted = MakeText(text, size, color, MetaFace);
        dc.DrawText(formatted, new Point((ActualWidth - formatted.Width) / 2, y));
    }

    private bool Visible(Point point, double margin = 20) => point.X >= -margin && point.X <= ActualWidth + margin
                                                            && point.Y >= -margin && point.Y <= ActualHeight + margin;

    private static SolidColorBrush BrushFor(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        ZoomAt(e.Delta > 0 ? 1.13 : 1 / 1.13, e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        var point = e.GetPosition(this);
        var hit = HitNode(point);
        if (hit is not null)
        {
            SelectedNode = hit;
            if (e.ClickCount == 2 && _positions.TryGetValue(hit.Id, out var world))
            {
                _zoom = Math.Clamp(_zoom * 1.35, MinZoom, MaxZoom);
                _pan = new Vector(-world.X * _zoom, -world.Y * _zoom);
                InvalidateVisual();
            }
            e.Handled = true;
            return;
        }
        BeginPan(point);
        e.Handled = true;
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        Focus();
        BeginPan(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = e.GetPosition(this);
        if (_panning)
        {
            var delta = point - _dragStart;
            if (delta.Length > 2) { _moved = true; _userAdjusted = true; }
            _pan = _dragOrigin + delta;
            _hasFitted = true;
            InvalidateVisual();
            return;
        }

        var hit = HitNode(point);
        Cursor = hit is null ? Cursors.Hand : Cursors.Arrow;
        ToolTip = hit is null ? null : $"{hit.Label}\n{hit.Type}";
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        EndPan(clearSelection: !_moved);
        e.Handled = true;
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        EndPan(clearSelection: false);
        e.Handled = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (!_panning) ToolTip = null;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _panning = false;
        Cursor = Cursors.Hand;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is Key.Add or Key.OemPlus) { ZoomBy(1.2); e.Handled = true; }
        else if (e.Key is Key.Subtract or Key.OemMinus) { ZoomBy(1 / 1.2); e.Handled = true; }
        else if (e.Key is Key.D0 or Key.NumPad0) { FitToView(); e.Handled = true; }
        else if (e.Key == Key.Escape) { SelectedNode = null; e.Handled = true; }
    }

    private void BeginPan(Point point)
    {
        _dragStart = point;
        _dragOrigin = _pan;
        _moved = false;
        _panning = true;
        Cursor = Cursors.SizeAll;
        CaptureMouse();
    }

    private void EndPan(bool clearSelection)
    {
        if (!_panning) return;
        _panning = false;
        Cursor = Cursors.Hand;
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (clearSelection) SelectedNode = null;
    }

    private AgentMemoryGraphNode? HitNode(Point screen)
    {
        for (var i = _nodes.Count - 1; i >= 0; i--)
        {
            var node = _nodes[i];
            if (!IsMatch(node) || !_positions.TryGetValue(node.Id, out var world)) continue;
            var center = WorldToScreen(world);
            var radius = NodeRadius(node, _zoom, 7) + 5;
            if ((screen - center).Length <= radius) return node;
        }
        return null;
    }
}
