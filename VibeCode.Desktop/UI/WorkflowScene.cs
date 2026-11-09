using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using VibeCode.Services;

namespace VibeCode.UI;

/// <summary>One way of drawing the workflow wall. The window hands every view the same filtered roster and listens
/// for the same requests: open this agent's chat, show this agent's account menu (a right-click), or filter to this
/// project.</summary>
public interface IWorkflowWallView
{
    event Action<AgentActivityNode>? AgentSelected;
    event Action<AgentActivityNode>? AgentMenuRequested;
    event Action<string>? ProjectSelected;
    bool MotionEnabled { get; set; }
    void SetProjects(IReadOnlyList<AgentActivityProject> projects);
    void UpdateMotion();
}

/// <summary>An entry in the wall's View dropdown and in Settings › Appearance. <see cref="Key"/> is what
/// settings.json remembers.</summary>
public sealed record WorkflowViewChoice(string Key, string Label, string Description, string Legend)
{
    /// <summary>A picture of this view with a team at work, for the Appearance picker. Rendered offscreen from the
    /// view itself by the telemetry tests (--wall-thumbnails).</summary>
    public string Thumbnail => $"pack://application:,,,/VibeCode;component/Assets/WallStyles/{Key}.png";
    public string AutomationName => Label + " wall style";
}

public static class WorkflowViews
{
    public const string Graph = "graph", Tavern = "tavern", Office = "office", Floor = "floor", Lanes = "lanes", Board = "board";

    public static readonly WorkflowViewChoice[] All =
    [
        new(Graph, "Flow graph", "Projects as hubs, agents as cards, packets streaming at each model's speed (after Agent Flow and Synapse).",
            "Packets stream from working agents into their project or team at each model's OpenRouter speed (1 packet ≈ 20 tokens of public median throughput, not this machine's traffic). Dashed lines are idle."),
        new(Floor, "Office floor", "A top-down open-plan office (after Pixel Agents): agents get up and walk to whatever their current step needs - desk, terminals, library, whiteboard or front door.",
            "Agents type at their desks to edit, lean in to read, stand at the server-room terminals for commands, browse the library to search and come to the front door when they need you. Idle ones wander to the coffee. Hover for details; click to open the chat."),
    ];

    private static readonly WorkflowViewChoice[] Retired =
    [
        new(Tavern, "Tavern town", "Every project is a building on one street, every agent a villager. Day follows your clock; weather follows the Weather extension or drifts on its own.",
            "Lit windows are agents at work; villagers outside are idle, waiting or stuck. Hover a building to see who is inside and what they are doing; click it to filter, click a window or villager to open the chat."),
        new(Office, "Pixel office", "A cutaway office tower: each project a room, each agent at a desk (after Pixel Agents, PixelHQ and Clawffice).",
            "Agents type at their desks while they work, raise a hand when they need you, nap when stopped and head for the coffee when idle. Hover for details; click a desk to open the chat."),
        new(Lanes, "Swimlanes", "One lane per agent on a shared clock, coloured by state, with a mark for every step (after Kansh and multi-agent observability dashboards).",
            "Lanes record what each agent did since this wall opened, plus each working agent's current turn. Green works, amber waits, red failed; dots are steps. Click a lane to open the chat."),
        new(Board, "Kanban board", "Agents as cards in In progress, Needs you, Idle and Done columns (after Vibe Kanban and Cline's agent board).",
            "Cards move between columns as agents start, block, rest and finish. Click a card to open its chat."),
    ];

    public static WorkflowViewChoice For(string? key) =>
        All.FirstOrDefault(v => string.Equals(v.Key, key, StringComparison.OrdinalIgnoreCase))
        ?? Retired.FirstOrDefault(v => string.Equals(v.Key, key, StringComparison.OrdinalIgnoreCase))
        ?? All[0];

    /// <summary>The view was chosen in Settings or on the wall itself. An open wall redraws in it and Settings moves
    /// its tick, so the two pickers never disagree.</summary>
    public static event Action<string>? Chosen;

    /// <summary>Remember a view and tell every picker. Choosing the view already saved does nothing.</summary>
    public static void Choose(string? key)
    {
        var choice = For(key);
        var settings = AppSettings.Current;
        if (string.Equals(settings.WorkflowWallView, choice.Key, StringComparison.Ordinal)) return;
        settings.WorkflowWallView = choice.Key;
        settings.Save();
        Chosen?.Invoke(choice.Key);
    }
}

/// <summary>
/// Shared plumbing for the wall's drawn views - the tavern, the office and the swimlanes: the roster, hover, click
/// and keyboard hit-testing, the motion clock with the same off switches as the flow graph, and cached text.
///
/// Each frame is drawn by <see cref="Render"/> into WPF's retained drawing, then shown until the next frame. While
/// motion is on that is a modest fixed rate on a dispatcher timer; while it is off the scene redraws only when the
/// roster or the hover changes, and <see cref="Time"/> stands still, so a still wall really is still. Nothing here
/// polls, fetches or reads anything beyond the snapshot the window passes in.
/// </summary>
public abstract partial class WorkflowScene : FrameworkElement, IWorkflowWallView
{
    protected sealed record SceneHit(Rect Bounds, string Key, object Target);

    private readonly DispatcherTimer _frame;
    private readonly Stopwatch _clock = new();
    private double _elapsed;
    private bool _animating;
    private string? _hoverKey;
    private int _keyboardIndex = -1;
    private List<SceneHit> _hits = new();
    private List<SceneHit> _drawing = new();
    private readonly Dictionary<(string, double, Color, bool, bool, double), FormattedText> _texts = new();
    private static readonly Dictionary<Color, SolidColorBrush> BrushCache = new();
    private Typeface? _ui, _uiBold, _mono;

    public event Action<AgentActivityNode>? AgentSelected;
    public event Action<AgentActivityNode>? AgentMenuRequested;
    public event Action<string>? ProjectSelected;
    public bool MotionEnabled { get; set; } = true;
    public bool IsAnimating => _animating;
    /// <summary>Clickable regions from the last frame, topmost last. Exposed for verification.</summary>
    public IReadOnlyList<(Rect Bounds, string Key)> Targets => _hits.Select(h => (h.Bounds, h.Key)).ToArray();

    /// <summary>Show the hover state for a target key ("agent:id" or "project:key"), exactly as the pointer or the
    /// arrow keys would. Null clears it.</summary>
    public void Hover(string? key) => SetHover(key);

    /// <summary>Activate a target by key, as a click or Enter on it would.</summary>
    public bool Activate(string key)
    {
        var hit = _hits.LastOrDefault(h => h.Key == key);
        if (hit is null) return false;
        Activate(hit);
        return true;
    }

    /// <summary>Right-click a target by key, as the mouse would. Only agents have a menu.</summary>
    public bool RequestMenu(string key)
    {
        if (_hits.LastOrDefault(h => h.Key == key)?.Target is not AgentActivityNode agent) return false;
        AgentMenuRequested?.Invoke(agent);
        return true;
    }
    protected IReadOnlyList<AgentActivityProject> Projects { get; private set; } = [];
    protected string? HoverKey => _hoverKey;
    protected Point? Pointer { get; private set; }

    protected WorkflowScene()
    {
        Focusable = true;
        ClipToBounds = true;
        _frame = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromSeconds(1 / FramesPerSecond) };
        _frame.Tick += (_, _) => InvalidateVisual();
        Loaded += (_, _) => UpdateMotion();
        Unloaded += (_, _) => SetAnimating(false);
        IsVisibleChanged += (_, _) => UpdateMotion();
    }

    /// <summary>Redraw rate while moving. Ambient scenes do not need the display's refresh rate.</summary>
    protected virtual double FramesPerSecond => 30;

    /// <summary>Scene time in seconds. Advances only while motion runs.</summary>
    protected double Time => _elapsed + (_clock.IsRunning ? _clock.Elapsed.TotalSeconds : 0);

    public void SetProjects(IReadOnlyList<AgentActivityProject> projects)
    {
        Projects = projects;
        OnProjectsChanged();
        InvalidateMeasure();
        InvalidateVisual();
        UpdateMotion();
    }

    protected virtual void OnProjectsChanged() { }

    public void UpdateMotion() => SetAnimating(IsLoaded && IsVisible && MotionEnabled && HudMotion.Enabled
        && SystemParameters.ClientAreaAnimation && Window.GetWindow(this)?.WindowState != WindowState.Minimized);

    private void SetAnimating(bool running)
    {
        if (running == _animating) return;
        _animating = running;
        if (running) { _clock.Restart(); _frame.Start(); }
        else
        {
            _elapsed += _clock.Elapsed.TotalSeconds;
            _clock.Reset();
            _frame.Stop();
        }
        InvalidateVisual();
    }

    protected sealed override void OnRender(DrawingContext dc)
    {
        _drawing = new List<SceneHit>();
        var bounds = new Rect(RenderSize);
        if (bounds.Width < 40 || bounds.Height < 40) { _hits = _drawing; return; }
        Render(dc, bounds);
        _hits = _drawing;
        if (_hoverKey is not null && _hits.All(h => h.Key != _hoverKey)) _hoverKey = null;
    }

    protected abstract void Render(DrawingContext dc, Rect bounds);

    // ---- hit testing --------------------------------------------------------------------------------------------

    protected void Hit(Rect bounds, AgentActivityNode agent) => _drawing.Add(new(bounds, AgentKey(agent), agent));
    protected void Hit(Rect bounds, AgentActivityProject project) => _drawing.Add(new(bounds, ProjectKey(project), project));
    protected void Hit(Rect bounds, string key) => _drawing.Add(new(bounds, key, key));
    /// <summary>A part of an agent or project with its own hover (a room's whiteboard, say); a click still opens or
    /// filters to <paramref name="target"/>.</summary>
    protected void Hit(Rect bounds, string key, object target) => _drawing.Add(new(bounds, key, target));

    protected static string AgentKey(AgentActivityNode agent) => "agent:" + agent.Id;
    protected static string ProjectKey(AgentActivityProject project) => "project:" + project.Key;
    protected bool Hovered(AgentActivityNode agent) => _hoverKey == AgentKey(agent);
    protected bool Hovered(AgentActivityProject project) => _hoverKey == ProjectKey(project);

    /// <summary>The project under the pointer, or the project of the agent under it.</summary>
    protected AgentActivityProject? HoveredProject => _hoverKey is null ? null
        : Projects.FirstOrDefault(p => Hovered(p) || p.Agents.Any(Hovered));

    protected AgentActivityNode? HoveredAgent => _hoverKey is null ? null
        : Projects.SelectMany(p => p.Agents).FirstOrDefault(Hovered);

    private SceneHit? HitAt(Point point)
    {
        for (var i = _hits.Count - 1; i >= 0; i--) if (_hits[i].Bounds.Contains(point)) return _hits[i];
        return null;
    }

    private void SetHover(string? key)
    {
        if (key == _hoverKey) return;
        _hoverKey = key;
        Cursor = key is null ? null : Cursors.Hand;
        var hit = _hits.FirstOrDefault(h => h.Key == key);
        AutomationProperties.SetHelpText(this, hit is null ? "" : DescribeHit(hit.Key, hit.Target));
        if (!_animating) InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Pointer = e.GetPosition(this);
        SetHover(HitAt(Pointer.Value)?.Key);
        if (!_animating && WantsPointerRedraw) InvalidateVisual();
    }

    /// <summary>Views that draw something that follows the pointer (a tooltip) redraw on every move.</summary>
    protected virtual bool WantsPointerRedraw => false;

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        Pointer = null;
        SetHover(null);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        if (HitAt(e.GetPosition(this)) is { } hit) { Activate(hit); e.Handled = true; }
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (HitAt(e.GetPosition(this))?.Target is AgentActivityNode agent) { AgentMenuRequested?.Invoke(agent); e.Handled = true; }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_hits.Count == 0) return;
        var order = _hits.Where(h => h.Target is AgentActivityNode or AgentActivityProject).ToList();
        if (order.Count == 0) return;
        var current = order.FindIndex(h => h.Key == _hoverKey);
        switch (e.Key)
        {
            case Key.Right or Key.Down:
                _keyboardIndex = (current + 1) % order.Count;
                break;
            case Key.Left or Key.Up:
                _keyboardIndex = current <= 0 ? order.Count - 1 : current - 1;
                break;
            case Key.Enter or Key.Space when current >= 0:
                Activate(order[current]);
                e.Handled = true;
                return;
            case Key.Escape when _hoverKey is not null:
                SetHover(null);
                e.Handled = true;
                return;
            default:
                return;
        }
        SetHover(order[_keyboardIndex].Key);
        e.Handled = true;
    }

    private void Activate(SceneHit hit)
    {
        switch (hit.Target)
        {
            case AgentActivityNode agent: AgentSelected?.Invoke(agent); break;
            case AgentActivityProject project: ProjectSelected?.Invoke(project.Key); break;
            case string key: OnCommand(key); break;
        }
    }

    /// <summary>A scene's own clickable controls (a range chip, say) arrive here by the key they were drawn with.</summary>
    protected virtual void OnCommand(string key) { }

    /// <summary>What a screen reader hears for the target under the pointer. A view whose parts have their own hover
    /// (the floor's interns) describes them itself.</summary>
    protected virtual string DescribeHit(string key, object target) => Describe(target);

    internal static string Describe(object target) => target switch
    {
        AgentActivityNode a => $"{a.Label}, {a.Role}: {a.Task}. {a.Status}. Now {(a.Now ?? WorkflowAgentCard.Resting(a, DateTime.UtcNow)).Text}. Press Enter to open the chat.",
        AgentActivityProject p => $"{p.Name}: {p.Agents.Count} agents, {p.Working} working. Press Enter to filter the wall to it.",
        _ => "",
    };

    // ---- ink and text -------------------------------------------------------------------------------------------

    protected static SolidColorBrush Fill(Color color)
    {
        if (BrushCache.TryGetValue(color, out var brush)) return brush;
        if (BrushCache.Count > 4096) BrushCache.Clear();
        brush = new SolidColorBrush(color);
        brush.Freeze();
        return BrushCache[color] = brush;
    }

    protected static Pen Stroke(Color color, double thickness)
    {
        var pen = new Pen(Fill(color), thickness);
        pen.Freeze();
        return pen;
    }

    protected static Color Alpha(Color color, double alpha) =>
        Color.FromArgb((byte)Math.Clamp(Math.Round(color.A * alpha), 0, 255), color.R, color.G, color.B);

    protected static Color Mix(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb((byte)(a.A + (b.A - a.A) * t), (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
    }

    /// <summary>A theme colour by resource name, so the data views follow the Dark and CLI themes.</summary>
    protected Color Ink(string resource, Color fallback) => TryFindResource(resource) is SolidColorBrush brush ? brush.Color : fallback;

    protected static Color StateColor(AgentActivityState state) => state switch
    {
        AgentActivityState.Working => Color.FromRgb(0x5F, 0xD3, 0x8D),
        AgentActivityState.Waiting or AgentActivityState.Paused => Color.FromRgb(0xF2, 0xB8, 0x4B),
        AgentActivityState.Error => Color.FromRgb(0xF2, 0x6D, 0x6D),
        _ => Color.FromRgb(0x8A, 0x94, 0xA6),
    };

    /// <summary>Single-line text, trimmed with an ellipsis to <paramref name="maxWidth"/> when it is positive.</summary>
    protected FormattedText Text(string text, double size, Color color, bool bold = false, bool mono = false, double maxWidth = 0)
    {
        var key = (text, size, color, bold, mono, Math.Round(maxWidth));
        if (_texts.TryGetValue(key, out var cached)) return cached;
        if (_texts.Count > 1200) _texts.Clear();
        _ui ??= new Typeface(TryFindResource("Ui") as FontFamily ?? new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        _uiBold ??= new Typeface(_ui.FontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        _mono ??= new Typeface(TryFindResource("Mono") as FontFamily ?? new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            mono ? _mono : bold ? _uiBold : _ui, size, Fill(color), VisualTreeHelper.GetDpi(this).PixelsPerDip)
        { MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
        if (maxWidth > 0) formatted.MaxTextWidth = Math.Max(1, maxWidth);
        return _texts[key] = formatted;
    }

    /// <summary>A Segoe Fluent glyph as text, in the theme's icon font.</summary>
    protected FormattedText Glyph(string glyph, double size, Color color)
    {
        var key = ("glyph:" + glyph, size, color, false, false, 0.0);
        if (_texts.TryGetValue(key, out var cached)) return cached;
        var family = TryFindResource("Icons") as FontFamily ?? new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        var formatted = new FormattedText(glyph, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), size, Fill(color),
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        return _texts[key] = formatted;
    }

    /// <summary>Stable 0..1 noise per string, so an agent keeps its colours and gait from frame to frame.</summary>
    protected static double Hash(string text, int salt = 0)
    {
        unchecked
        {
            var h = (uint)(2166136261 + salt * 16777619);
            foreach (var c in text) { h ^= c; h *= 16777619; }
            h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
            return (h & 0xFFFFFF) / (double)0x1000000;
        }
    }

    /// <summary>Elapsed working time for a card line: "12m", "1h 04m".</summary>
    protected static string Since(AgentActivityNode agent, DateTime nowUtc) =>
        agent.State == AgentActivityState.Working && agent.WorkingSinceUtc is { } since
            ? WorkflowAgentCard.Duration(nowUtc - since, seconds: false) : "";
}

/// <summary>
/// What each agent on the wall has been doing, for the swimlanes: state segments and one mark per new step, kept for
/// <see cref="Span"/>. It is fed the window's own snapshots - nothing is read from disk or a provider - so it knows
/// only what happened while the wall was open, plus the one fact every snapshot carries about the past: when a
/// working agent's current turn began. Gaps where the wall was hidden or minimized are left as gaps.
/// </summary>
public sealed class WorkflowHistory
{
    public static readonly TimeSpan Span = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan Gap = TimeSpan.FromSeconds(6);
    private readonly Dictionary<string, Lane> _lanes = new(StringComparer.Ordinal);

    public readonly record struct Segment(AgentActivityState State, DateTime StartUtc, DateTime EndUtc);
    public sealed record Mark(DateTime AtUtc, string Glyph, string Text, bool Failed);

    public sealed class Lane
    {
        public List<Segment> Segments { get; } = new();
        public List<Mark> Marks { get; } = new();
        internal string Step = "";
        internal DateTime SeenUtc;
        internal bool AtDoor;
    }

    // Times an agent came to the door for you, per local day, for the notice board's recap.
    private readonly Dictionary<DateTime, int> _doorVisits = new();

    public DateTime OpenedUtc { get; } = DateTime.UtcNow;
    public int Count => _lanes.Count;
    public Lane? For(string agentId) => _lanes.GetValueOrDefault(agentId);

    /// <summary>How many times an agent came to the door for you (an approval, a sign-in or a question) on the local
    /// days from <paramref name="fromLocal"/> to <paramref name="toLocal"/>, while this wall was open.</summary>
    public int DoorVisits(DateTime fromLocal, DateTime toLocal) =>
        _doorVisits.Where(d => d.Key >= fromLocal.Date && d.Key <= toLocal.Date).Sum(d => d.Value);

    public void Record(IReadOnlyList<AgentActivityProject> projects, DateTime nowUtc)
    {
        var cutoff = nowUtc - Span;
        foreach (var agent in projects.SelectMany(p => p.Agents))
        {
            if (!_lanes.TryGetValue(agent.Id, out var lane))
            {
                _lanes[agent.Id] = lane = new Lane();
                if (agent.State == AgentActivityState.Working && agent.WorkingSinceUtc is { } since && since < nowUtc)
                    lane.Segments.Add(new(AgentActivityState.Working, since < cutoff ? cutoff : since, nowUtc));
                else if (agent.LastActiveAt?.UtcDateTime is { } active && active > cutoff && active < nowUtc && agent.Last is { } done)
                    lane.Marks.Add(new(active, done.Glyph, done.Text, done.Failed));
            }
            var open = lane.Segments.Count > 0 && nowUtc - lane.Segments[^1].EndUtc <= Gap;
            if (open && lane.Segments[^1].State == agent.State)
                lane.Segments[^1] = lane.Segments[^1] with { EndUtc = nowUtc };
            else
                lane.Segments.Add(new(agent.State, open ? lane.Segments[^1].EndUtc : nowUtc, nowUtc));
            var step = agent.Now?.Text ?? "";
            if (step.Length > 0 && step != lane.Step) lane.Marks.Add(new(nowUtc, agent.Now!.Glyph, step, agent.Now.Failed));
            lane.Step = step;
            lane.SeenUtc = nowUtc;
            var door = WorkflowWallEvents.NeedsYou(agent);
            if (door && !lane.AtDoor) _doorVisits[nowUtc.ToLocalTime().Date] = _doorVisits.GetValueOrDefault(nowUtc.ToLocalTime().Date) + 1;
            lane.AtDoor = door;
        }
        foreach (var (id, lane) in _lanes.ToArray())
        {
            lane.Segments.RemoveAll(s => s.EndUtc < cutoff);
            lane.Marks.RemoveAll(m => m.AtUtc < cutoff);
            if (lane.SeenUtc < cutoff) _lanes.Remove(id);
        }
    }
}
