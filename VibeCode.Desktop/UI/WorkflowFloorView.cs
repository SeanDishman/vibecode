using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;

namespace VibeCode.UI;

/// <summary>
/// The office floor, after Pixel Agents' top-down office: one open-plan floor seen from above, where every project is a
/// pod of desks and every agent a little worker who gets up and walks to whatever its current step needs.
///
/// Editing a file or writing a reply, a worker types at its own desk; reading, it leans in to a page on its monitor;
/// thinking, it sits with a hand on its head. A shell command sends it to the standing terminals in the server room, a
/// code search to the library shelves, a web search to the kiosk, planning to the whiteboard, delegating to the meeting
/// table (its subagents come along as interns), and a Bridge message to the desk of the teammate it is writing to. When
/// it needs you - an approval, a sign-in, a question - it comes to the front door and waves. Stopped, it naps at its
/// desk; failed, it holds its head before a red screen; idle, it drifts between the coffee machine, the water cooler and
/// the windows; once its work is done it sits on the couch with a mug.
///
/// Workers walk the aisles on a grid route that prefers straight runs, so a trip across the floor takes a few seconds.
/// A worker that has just reached a station stays a moment before it sets off again, so a burst of short calls reads as
/// one errand rather than pacing. With motion off every worker simply stands where its step puts it.
///
/// The server room is for running commands: after one, a worker keeps its terminal for only a few seconds of thinking in
/// case the next follows, then goes back to its desk. With every terminal taken - or both kiosks, for a web search - it
/// runs the command from its own desk instead of standing in line.
///
/// The windows show the same sky and weather as the tavern and the office tower. Hover a worker or a pod for the
/// details; click a pod to filter the wall, a worker or its desk to open the chat.
/// </summary>
public sealed partial class WorkflowFloorView : WorkflowScene
{
    /// <summary>What a worker is doing on the floor, which decides where it goes, how it stands and what its screen shows.</summary>
    public enum Activity { Typing, Replying, Reading, Thinking, Shell, Searching, Browsing, Planning, Delegating, Messaging, NeedsYou, Failed, Asleep, Resting, Waiting, Idle, Leaving }

    private enum Zone { Desk, Visit, Terminal, Library, Kiosk, Board, Table, Door, Couch, Lounge }
    private enum Facing { Down, Up, Left, Right }

    /// <summary>A place to stand or sit, in floor units (one art pixel each): where the feet go, which way the worker
    /// faces there, whether it sits, and for a lounge spot what it does there.</summary>
    private sealed record Spot(string Key, Zone Zone, Point Foot, Facing Facing, bool Seated = false, string Look = "");

    private sealed class Walker
    {
        public required string Id;
        public string Label = "";
        public AgentActivityNode? LastNode;
        public Point Foot;
        public readonly List<Point> Path = new();
        public Facing Facing = Facing.Up;
        public Spot? Goal;
        public Activity Doing = Activity.Idle;
        /// <summary>Scene time it last arrived somewhere or set off.</summary>
        public double Since;
        /// <summary>Scene time it was last running a command.</summary>
        public double LastShell = double.NegativeInfinity;
        /// <summary>Distance walked, which keeps the step cycle in time with the ground it covers.</summary>
        public double Stride;
        public int Errand;
        public double NextErrand = double.NaN;
        public bool Arrived => Path.Count == 0;
        public bool At(string key) => Arrived && Goal?.Key == key;
    }

    private sealed class Pod
    {
        public required AgentActivityProject Project;
        public Rect Area;
        public Color Rug;
    }

    /// <summary>A desk slot: the monitor and desktop at the top, the chair below, the aisle under that.</summary>
    private sealed record Desk(string AgentId, double X, double Y)
    {
        public Point Seat => new(X + 17, Y + 33);
    }

    /// <summary>Furniture that workers can pass in front of or behind, drawn in depth order with them.</summary>
    private sealed record Prop(double Base, Drawing Art, Action<DrawingContext>? Live = null);

    // Floor units are art pixels; the floor is scaled by _p DIPs per unit to fit the wall.
    private const double WallH = 30, Side = 3, BottomWall = 6, WingW = 78, SlotW = 34, SlotH = 40, PlateH = 12, Cell = 4;
    private const double WalkSpeed = 30, MinDwell = 1.5;
    /// <summary>Seconds a worker thinking after a command keeps its terminal, in case another command follows.</summary>
    private const double TerminalGrace = 4;
    private const int MaxWorkers = 24;

    private readonly List<Pod> _pods = new();
    private readonly Dictionary<string, Desk> _desks = new(StringComparer.Ordinal);
    private readonly Dictionary<Zone, List<Spot>> _spots = new();
    private readonly Dictionary<string, Spot> _seats = new(StringComparer.Ordinal), _visits = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Walker> _walkers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (Activity Doing, string? Visit)> _wants = new(StringComparer.Ordinal);
    private readonly List<Prop> _props = new();
    private readonly List<Rect> _windows = new();
    private readonly List<Point> _terminals = new(), _racks = new(), _shelves = new(), _cabinets = new();
    private List<AgentActivityNode> _shown = new();
    private Dictionary<string, AgentActivityNode> _byId = new(StringComparer.Ordinal);
    private bool[,] _blocked = new bool[0, 0];
    private double _fw, _fh, _p = 3, _time;
    private Point _origin, _kiosk, _counter, _cooler, _couch, _table, _clock, _armchair, _printer;
    private Rect _center, _library, _kitchen, _server, _meeting, _board;
    private Spot _entrance = new("entrance", Zone.Door, new Point(0, 0), Facing.Up);
    private string _layoutKey = "";
    private DrawingGroup? _static;
    private double _last = double.NaN;
    private int _more;
    private bool _placed;

    public string SkyMode { get; set; } = "auto";
    public string WeatherMode { get; set; } = "auto";
    public SceneSky Sky { get; private set; }

    /// <summary>Pods on the floor, one per project. Exposed for verification.</summary>
    public int PodCount => _pods.Count;
    /// <summary>What an agent is doing on the floor now. Exposed for verification.</summary>
    public Activity? ActivityOf(string agentId) => _walkers.TryGetValue(agentId, out var w) ? w.Doing : null;
    /// <summary>The station an agent is at or walking to, such as "desk:id", "terminal:0" or "door:1". Exposed for verification.</summary>
    public string? StationOf(string agentId) => _walkers.TryGetValue(agentId, out var w) ? w.Goal?.Key : null;
    /// <summary>Whether an agent is on its way somewhere. Exposed for verification.</summary>
    public bool IsWalking(string agentId) => _walkers.TryGetValue(agentId, out var w) && !w.Arrived;
    /// <summary>Where an agent's feet are, in this element's DIPs. Exposed for verification.</summary>
    public Point? FootOf(string agentId) => _walkers.TryGetValue(agentId, out var w) ? Dip(w.Foot) : null;

    public WorkflowFloorView()
    {
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        AutomationProperties.SetName(this, "Office floor: projects are desk pods, agents walk to the station their current step needs");
    }

    // ---- what each agent is doing -------------------------------------------------------------------------------

    /// <summary>The floor activity for an agent's state and the step it is on.</summary>
    public static Activity Classify(AgentActivityNode agent)
    {
        switch (agent.State)
        {
            case AgentActivityState.Paused: return Activity.Asleep;
            case AgentActivityState.Error: return Activity.Failed;
            // Approvals and sign-ins wait on you; a usage limit or a Bridge retry only waits on the clock.
            case AgentActivityState.Waiting: return agent.Status is "Approval needed" or "Sign-in needed" ? Activity.NeedsYou : Activity.Waiting;
            case AgentActivityState.Idle: return agent.Status == "Completed" ? Activity.Resting : Activity.Idle;
        }
        if (agent.IsCentralPlanner) return Activity.Planning;
        var now = agent.Now;
        if (now is null) return Activity.Typing;
        if (now.Glyph == WorkflowActivity.ApprovalGlyph) return Activity.NeedsYou;
        if (now.Tool.Length > 0) return ForTool(now.Tool);
        return now.Glyph switch
        {
            WorkflowActivity.ThinkingGlyph or WorkflowActivity.PlanGlyph => Activity.Thinking,
            WorkflowActivity.WritingGlyph => Activity.Replying,
            WorkflowActivity.DelegateGlyph => Activity.Delegating,
            _ => Activity.Typing,
        };
    }

    /// <summary>Every provider's tools arrive under Claude Code's names (Codex and Gemini are translated on the way in),
    /// so the canonical names cover them all. MCP tools are desk work unless they drive a browser.</summary>
    private static Activity ForTool(string name)
    {
        if (ToolItem.IsShellTool(name)) return Activity.Shell;
        switch (name)
        {
            case "Read" or "NotebookRead": return Activity.Reading;
            case "Grep" or "Glob" or "LS": return Activity.Searching;
            case "WebSearch" or "WebFetch": return Activity.Browsing;
            case "Task" or "Agent" or "spawn_subagent" or "Workflow": return Activity.Delegating;
            case "AskUserQuestion": return Activity.NeedsYou;
            case "ExitPlanMode" or "EnterPlanMode": return Activity.Planning;
        }
        var tool = name[(name.LastIndexOf("__", StringComparison.Ordinal) is var index && index >= 0 ? index + 2 : 0)..];
        switch (tool)
        {
            case "bridge_send_message" or "bridge_broadcast" or "bridge_dispatch_task": return Activity.Messaging;
            case "bridge_read_chat" or "project_read_chat" or "bridge_file_edits": return Activity.Reading;
            case "bridge_set_plan" or "bridge_update_task" or "bridge_agree_scope": return Activity.Planning;
        }
        if (name.StartsWith("mcp__", StringComparison.Ordinal)
            && (name.Contains("browser", StringComparison.OrdinalIgnoreCase) || name.Contains("chrome", StringComparison.OrdinalIgnoreCase)
                || name.Contains("fetch", StringComparison.OrdinalIgnoreCase) || name.Contains("web", StringComparison.OrdinalIgnoreCase)))
            return Activity.Browsing;
        return Activity.Typing;
    }

    private static Zone ZoneOf(Activity doing) => doing switch
    {
        Activity.Shell => Zone.Terminal,
        Activity.Searching => Zone.Library,
        Activity.Browsing => Zone.Kiosk,
        Activity.Planning => Zone.Board,
        Activity.Delegating => Zone.Table,
        Activity.Messaging => Zone.Visit,
        Activity.NeedsYou => Zone.Door,
        Activity.Resting or Activity.Waiting => Zone.Couch,
        Activity.Idle => Zone.Lounge,
        _ => Zone.Desk,
    };

    /// <summary>Where a worker is and what it is up to there, for its card. <paramref name="zone"/> is the station it is
    /// at or walking to, which can differ from the one its step normally takes it to.</summary>
    private static string Where(Activity doing, Zone? zone = null) => doing switch
    {
        Activity.Shell when zone == Zone.Desk => "At its desk, running a command: every terminal is taken",
        Activity.Browsing when zone == Zone.Desk => "At its desk, on the web: both kiosks are taken",
        Activity.Thinking when zone is { } at && StationName(at) is { } station => "At " + station + ", thinking",
        Activity.Typing => "At its desk, typing",
        Activity.Replying => "At its desk, writing the reply",
        Activity.Reading => "At its desk, reading",
        Activity.Thinking => "At its desk, thinking",
        Activity.Shell => "At a server-room terminal",
        Activity.Searching => "Searching the library shelves",
        Activity.Browsing => "At the web kiosk",
        Activity.Planning => "At the whiteboard",
        Activity.Delegating => "At the meeting table with its subagents",
        Activity.Messaging => "Over at a teammate's desk",
        Activity.NeedsYou => "At the front door, waiting for you",
        Activity.Failed => "At its desk, stuck on an error",
        Activity.Asleep => "Napping at its desk",
        Activity.Resting => "On the couch, work done",
        Activity.Waiting => "On the couch, waiting",
        Activity.Leaving => "Heading out the front door",
        _ => "Taking a break",
    };

    /// <summary>A station a worker can stay at to think between two calls.</summary>
    private static string? StationName(Zone zone) => zone switch
    {
        Zone.Terminal => "a server-room terminal",
        Zone.Library => "the library shelves",
        Zone.Kiosk => "the web kiosk",
        Zone.Board => "the whiteboard",
        Zone.Table => "the meeting table",
        Zone.Visit => "a teammate's desk",
        _ => null,
    };

    protected override void OnProjectsChanged()
    {
        // Classified once per snapshot rather than per frame: a message's addressee needs a look at the chat's rows.
        _wants.Clear();
        var roster = Projects.SelectMany(p => p.Agents).ToList();
        foreach (var agent in roster)
        {
            var doing = Classify(agent);
            _wants[agent.Id] = (doing, doing == Activity.Messaging ? Addressee(agent, roster) : null);
        }
    }

    private (Activity Doing, string? Visit) Want(AgentActivityNode agent) =>
        _wants.TryGetValue(agent.Id, out var want) ? want : (Classify(agent), null);

    /// <summary>The teammate a running Bridge message is addressed to, when it is on the floor too.</summary>
    private static string? Addressee(AgentActivityNode agent, IReadOnlyList<AgentActivityNode> roster)
    {
        var items = agent.Chat.Items;
        ToolItem? running = null;
        // The current turn only: rows above the newest prompt belong to an earlier one.
        for (int i = items.Count - 1, seen = 0; i >= 0 && seen < 80 && running is null && items[i] is not UserItem; i--, seen++)
        {
            running = items[i] switch
            {
                ToolItem { IsRunning: true } tool when tool.Name == agent.Now?.Tool => tool,
                CompactToolGroupItem group => group.Tools.LastOrDefault(t => t.IsRunning && t.Name == agent.Now?.Tool),
                _ => null,
            };
        }
        var to = Str(running?.Input?["recipient"]) ?? Str(running?.Input?["agent"]);
        if (string.IsNullOrWhiteSpace(to)) return null;
        var id = to.StartsWith("peer:", StringComparison.OrdinalIgnoreCase) ? to[5..] : to;
        return roster.FirstOrDefault(a => a.Id != agent.Id && (id.Length >= 6 && a.Id.StartsWith(id, StringComparison.OrdinalIgnoreCase)
            || string.Equals(a.Label, to, StringComparison.OrdinalIgnoreCase)))?.Id;
    }

    private static string? Str(JsonNode? node)
    {
        try { return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null; }
        catch (InvalidOperationException) { return null; }
    }

    // ---- frame --------------------------------------------------------------------------------------------------

    protected override void Render(DrawingContext dc, Rect bounds)
    {
        Sky = SceneSky.Resolve(SkyMode, WeatherMode, DateTime.Now);
        var time = _time = Time;
        var moving = IsAnimating;
        Layout(bounds);
        // A pod or desk reached its next look (or town improvements were switched): refurnish, keeping the layout.
        if (_static is not null && _bakedTown != WorkflowTown.Instance.Stamp)
        {
            _bakedTown = WorkflowTown.Instance.Stamp;
            _static = BuildStatic();
            BuildProps();
        }
        var dt = double.IsNaN(_last) ? 0 : Math.Clamp(time - _last, 0, 0.25);
        _last = time;
        // A desk that just earned its next look waits for its technician (WorkflowFloorView.Deliveries.cs).
        if (UpdateDeskTiers(time, moving)) BuildProps();
        Advance(time, dt, moving);
        AdvanceDeliveries(time, dt, moving);

        var nowUtc = DateTime.UtcNow;
        var agentHover = HoveredAgent;
        var podHover = agentHover is null ? HoveredProject : null;

        if (_static is not null) dc.DrawDrawing(_static);
        DrawWall(dc, time);
        foreach (var pod in _pods) Hit(R(Inflate(pod.Area, 3, 3)), pod.Project);
        foreach (var (id, desk) in _desks)
            if (_byId.TryGetValue(id, out var owner)) Hit(R(desk.X + 3, desk.Y, 28, 37), owner);
        DrawZoneSigns(dc);
        DrawSpeaker(dc, time);

        // Furniture and workers in depth order: whoever stands lower on the floor is nearer, so drawn later.
        var drawn = new List<(AgentActivityNode Agent, Walker Walker, Rect Body)>();
        var interns = new List<(AgentActivityNode Lead, int Index, Rect Body)>();
        var scene = new List<(double Base, int Order, Action Draw)>();
        foreach (var prop in _props) scene.Add((prop.Base, 0, () => { dc.DrawDrawing(prop.Art); prop.Live?.Invoke(dc); }));
        foreach (var agent in _shown)
        {
            var walker = _walkers[agent.Id];
            var focused = Hovered(agent);
            scene.Add((walker.Foot.Y, 1, () => drawn.Add((agent, walker, DrawWorker(dc, agent, walker, time, focused)))));
            foreach (var intern in agent.ActiveChildren > 0 ? Interns(agent, walker, time) : [])
                scene.Add((intern.Foot.Y, 2, () => interns.Add((agent, intern.Index, DrawIntern(dc, intern, HoverKey == InternKey(agent, intern.Index))))));
        }
        foreach (var walker in _walkers.Values)
        {
            if (_byId.ContainsKey(walker.Id) || walker.LastNode is not { } leaving) continue;
            var w = walker;
            scene.Add((w.Foot.Y, 1, () => DrawWorker(dc, leaving, w, time, false)));
        }
        StageDeliveries(dc, scene, time);

        foreach (var (_, _, draw) in scene.OrderBy(s => s.Base).ThenBy(s => s.Order)) draw();
        DrawDeliveryEffects(dc, time);

        foreach (var (agent, _, body) in drawn) Hit(Inflate(body, 2, 2), agent);
        // After their leads, so an intern standing at its lead's elbow is the one pointed at. A click opens the lead's chat.
        foreach (var (lead, index, body) in interns) Hit(Inflate(body, 2, 2), InternKey(lead, index), lead);
        if (podHover is not null && _pods.FirstOrDefault(p => p.Project.Key == podHover.Key) is { } focus)
            dc.DrawRoundedRectangle(null, Stroke(Ink("Accent", Color.FromRgb(0x7C, 0xB7, 0xFF)), 3), R(Inflate(focus.Area, 4, 4)), 6, 6);
        DrawPlates(dc, podHover);
        DrawNames(dc, drawn);
        DrawBubbles(dc, bounds, drawn, time);
        DrawFinishers(dc);
        var flash = SceneWeatherFx.Lightning(Sky, time, out _);
        if (flash > 0 && _fw > 0) dc.DrawRectangle(Fill(Color.FromArgb((byte)(flash * 26), 0xDD, 0xE6, 0xFF)), null, R(0, 0, _fw, _fh));
        if (_more > 0)
        {
            var more = Text($"+{_more} more agents", 11.5, Color.FromRgb(0xE8, 0xEC, 0xF2), bold: true);
            var at = Dip(new Point(_fw / 2 + 50, _fh - BottomWall - 8));
            dc.DrawRoundedRectangle(Fill(Color.FromArgb(200, 0x16, 0x1A, 0x22)), null, new Rect(at.X, at.Y - more.Height / 2 - 3, more.Width + 14, more.Height + 6), 5, 5);
            dc.DrawText(more, new Point(at.X + 7, at.Y - more.Height / 2));
        }

        if (HoveredIntern() is { } hovered) DrawInternCard(dc, bounds, hovered.Lead, hovered.Index);
        else if (agentHover is not null) DrawAgentCard(dc, bounds, agentHover, nowUtc);
        else if (podHover is not null && _pods.FirstOrDefault(p => p.Project.Key == podHover.Key) is { } pod) DrawPodCard(dc, bounds, pod, nowUtc);
        DrawHud(dc);
    }

    // ---- layout -------------------------------------------------------------------------------------------------

    private void Layout(Rect bounds)
    {
        _shown = Projects.SelectMany(p => p.Agents).Take(MaxWorkers).ToList();
        _byId = _shown.GroupBy(a => a.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        _more = Projects.Sum(p => p.Agents.Count) - _shown.Count;
        var groups = Projects.Select(p => (Project: p, Agents: p.Agents.Where(a => _byId.ContainsKey(a.Id)).ToList()))
            .Where(g => g.Agents.Count > 0).ToList();
        // Guild colours are part of the plan: each desk carries a keepsake in its owner's colour, baked with the furniture.
        var key = $"{bounds.Width:0}x{bounds.Height:0}|{WorkflowGuilds.Enabled}|" + string.Join(";", groups.Select(g => g.Project.Key + ":" + string.Join(",", g.Agents.Select(a => a.Id))));
        if (key == _layoutKey)
        {
            foreach (var pod in _pods) pod.Project = Projects.FirstOrDefault(p => p.Key == pod.Project.Key) ?? pod.Project;
            return;
        }
        _layoutKey = key;
        // Paths and spots are about to move: whatever was being delivered is simply there.
        FinishDeliveries(relayout: true);

        // Pods pack into lines across the middle of the floor. A few shapes are tried and the one that draws the
        // whole floor largest wins, so a wide wall gets a wide office and a tall one a deep office.
        var availW = Math.Max(100, bounds.Width - 24);
        var availH = Math.Max(100, bounds.Height - 64);
        (double Scale, double CenterW, double Interior, List<Rect> Places, double Width)? best = null;
        foreach (var podCols in new[] { 2, 3, 4, 5, 6 })
            for (var lineSlots = 2; lineSlots <= 12; lineSlots++)
            {
                var places = new List<Rect>();
                double x = 0, y = 0, lineH = 0, width = 0;
                foreach (var (_, agents) in groups)
                {
                    var cols = Math.Min(agents.Count, podCols);
                    var rows = (int)Math.Ceiling(agents.Count / (double)cols);
                    var w = cols * SlotW + 4;
                    var h = PlateH + rows * SlotH;
                    if (x > 0 && x + w > lineSlots * SlotW + 4) { x = 0; y += lineH + 10; lineH = 0; }
                    places.Add(new Rect(x, y, w, h));
                    x += w + 10;
                    lineH = Math.Max(lineH, h);
                    width = Math.Max(width, x - 10);
                }
                var podsH = places.Count == 0 ? 0 : y + lineH;
                var centerW = Math.Max(160, width + 24);
                var interior = Math.Max(150, 18 + podsH + 36);
                var scale = Math.Min(availW / (Side * 2 + WingW * 2 + centerW), availH / (WallH + interior + BottomWall));
                if (best is null || scale > best.Value.Scale + 0.001) best = (scale, centerW, interior, places, width);
            }
        var (fit, centerWidth, interiorH, podPlaces, podsWidth) = best!.Value;
        // Whole DIPs per art pixel once there is room for them, half steps below that, so the pixels stay square.
        _p = fit >= 3 ? Math.Floor(fit) : Math.Max(1, Math.Floor(fit * 2) / 2);
        // Room left over at that scale widens the aisles and deepens the wings instead of leaving bare wall.
        var podsHeight = podPlaces.Count == 0 ? 0 : podPlaces.Max(r => r.Bottom);
        var spareH = Math.Floor(availH / _p - (WallH + interiorH + BottomWall));
        if (spareH > 0) interiorH += Math.Min(spareH, Math.Round(interiorH * 0.3));
        var spareW = Math.Floor(availW / _p - (Side * 2 + WingW * 2 + centerWidth));
        if (spareW > 0) centerWidth += Math.Min(spareW, Math.Round(centerWidth * 0.3));
        _fw = Side * 2 + WingW * 2 + centerWidth;
        _fh = WallH + interiorH + BottomWall;
        _origin = new Point(Math.Round((bounds.Width - _fw * _p) / 2), Math.Round(52 + Math.Max(0, (bounds.Height - 64 - _fh * _p) / 2)));

        var cx0 = Side + WingW;
        var cx1 = cx0 + centerWidth;
        var bottom = _fh - BottomWall;
        var split = Math.Round(interiorH * 0.46);
        _center = new Rect(cx0, WallH, centerWidth, interiorH);
        _library = new Rect(Side, WallH, WingW, split);
        _kitchen = new Rect(Side, WallH + split, WingW, interiorH - split);
        _server = new Rect(cx1, WallH, WingW, split);
        _meeting = new Rect(cx1, WallH + split, WingW, interiorH - split);

        _pods.Clear(); _desks.Clear(); _seats.Clear(); _visits.Clear();
        _spots.Clear();
        foreach (var zone in Enum.GetValues<Zone>()) _spots[zone] = new List<Spot>();
        var left = cx0 + Math.Round((centerWidth - podsWidth) / 2);
        var top = WallH + 18 + Math.Max(0, Math.Round((interiorH - (18 + podsHeight + 36)) / 2));
        for (var g = 0; g < groups.Count; g++)
        {
            var (project, agents) = groups[g];
            var place = podPlaces[g];
            var pod = new Pod
            {
                Project = project, Area = new Rect(left + place.X, top + place.Y, place.Width, place.Height),
                Rug = Rugs[(int)(Hash(project.Key, 3) * Rugs.Length)],
            };
            _pods.Add(pod);
            var cols = (int)Math.Round((place.Width - 4) / SlotW);
            for (var i = 0; i < agents.Count; i++)
            {
                var desk = new Desk(agents[i].Id, pod.Area.Left + 2 + i % cols * SlotW, pod.Area.Top + PlateH + i / cols * SlotH);
                _desks[desk.AgentId] = desk;
                _seats[desk.AgentId] = new Spot("desk:" + desk.AgentId, Zone.Desk, desk.Seat, Facing.Up, Seated: true);
                _visits[desk.AgentId] = new Spot("visit:" + desk.AgentId, Zone.Visit, new Point(desk.Seat.X + 14, desk.Seat.Y + 4), Facing.Left);
            }
        }

        // Library: two bookshelves against the wall, a row of filing cabinets, the web kiosk.
        _shelves.Clear(); _cabinets.Clear(); _terminals.Clear(); _racks.Clear(); _windows.Clear();
        for (var i = 0; i < 2; i++)
        {
            var x = _library.Left + 5 + i * 36;
            _shelves.Add(new Point(x, WallH - 21));
            Add(Zone.Library, new Spot($"shelf:{i * 2}", Zone.Library, new Point(x + 8, WallH + 14), Facing.Up));
            Add(Zone.Library, new Spot($"shelf:{i * 2 + 1}", Zone.Library, new Point(x + 22, WallH + 14), Facing.Up));
        }
        for (var i = 0; i < 3; i++)
        {
            var x = _library.Left + 4 + i * 12;
            _cabinets.Add(new Point(x, WallH + 22));
            Add(Zone.Library, new Spot($"cabinet:{i}", Zone.Library, new Point(x + 5, WallH + 44), Facing.Up));
        }
        _kiosk = new Point(_library.Left + 44, WallH + 20);
        Add(Zone.Kiosk, new Spot("kiosk:0", Zone.Kiosk, new Point(_kiosk.X + 7, _kiosk.Y + 24), Facing.Up));
        Add(Zone.Kiosk, new Spot("kiosk:1", Zone.Kiosk, new Point(_kiosk.X + 19, _kiosk.Y + 24), Facing.Up));
        // A reading chair under the shelves, somewhere for an idle worker to sit with a book.
        _armchair = new Point(_library.Left + 8, _library.Bottom - 20);
        Add(Zone.Lounge, new Spot("armchair:0", Zone.Lounge, new Point(_armchair.X + 7, _armchair.Y + 15), Facing.Down, Seated: true, Look: "book"));

        // Server room: racks against the wall, a row of standing terminals in front of them.
        for (var i = 0; i < 3; i++) _racks.Add(new Point(_server.Left + 8 + i * 22, WallH - 23));
        for (var i = 0; i < 3; i++)
        {
            var t = new Point(_server.Left + 4 + i * 24, WallH + 16);
            _terminals.Add(t);
            Add(Zone.Terminal, new Spot($"terminal:{i}", Zone.Terminal, new Point(t.X + 10, t.Y + 18), Facing.Up));
        }
        _printer = new Point(_server.Left + 6, _server.Bottom - 24);
        Add(Zone.Lounge, new Spot("printer:0", Zone.Lounge, new Point(_printer.X + 9, _printer.Y + 19), Facing.Up, Look: "printer"));

        // The middle wall: whiteboard, clock and windows; the windows double as places to stand and look out.
        _board = new Rect(cx0 + 10, 4, 48, 20);
        Add(Zone.Board, new Spot("board:0", Zone.Board, new Point(_board.Left + 12, WallH + 9), Facing.Up));
        Add(Zone.Board, new Spot("board:1", Zone.Board, new Point(_board.Left + 36, WallH + 9), Facing.Up));
        _clock = new Point(_board.Right + 12, 14);
        for (var x = _board.Right + 24; x + 30 <= cx1 - 8; x += 42)
        {
            var window = new Rect(x, 5, 30, 19);
            _windows.Add(window);
            Add(Zone.Lounge, new Spot($"window:{_windows.Count - 1}", Zone.Lounge, new Point(window.Left + 15, WallH + 9), Facing.Up, Look: "window"));
        }

        // Meeting table: two chairs behind it, two in front, room to stand at either end.
        _table = new Point(_meeting.Left + 15, _meeting.Top + 26);
        Add(Zone.Table, new Spot("table:0", Zone.Table, new Point(_table.X + 13, _table.Y + 31), Facing.Up, Seated: true));
        Add(Zone.Table, new Spot("table:1", Zone.Table, new Point(_table.X + 35, _table.Y + 31), Facing.Up, Seated: true));
        Add(Zone.Table, new Spot("table:2", Zone.Table, new Point(_table.X + 13, _table.Y + 1), Facing.Down, Seated: true));
        Add(Zone.Table, new Spot("table:3", Zone.Table, new Point(_table.X + 35, _table.Y + 1), Facing.Down, Seated: true));
        Add(Zone.Table, new Spot("table:4", Zone.Table, new Point(_table.X - 7, _table.Y + 10), Facing.Right));
        Add(Zone.Table, new Spot("table:5", Zone.Table, new Point(_table.X + 55, _table.Y + 10), Facing.Left));

        // Kitchen and lounge: counter with the coffee machine, the water cooler, the couch.
        _counter = new Point(_kitchen.Left + 5, _kitchen.Top + 8);
        _cooler = new Point(_kitchen.Left + 52, _kitchen.Top + 4);
        _couch = new Point(_kitchen.Left + 6, bottom - 34);
        Add(Zone.Lounge, new Spot("coffee:0", Zone.Lounge, new Point(_counter.X + 9, _counter.Y + 22), Facing.Down, Look: "mug"));
        Add(Zone.Lounge, new Spot("coffee:1", Zone.Lounge, new Point(_counter.X + 24, _counter.Y + 22), Facing.Down, Look: "mug"));
        Add(Zone.Lounge, new Spot("cooler:0", Zone.Lounge, new Point(_cooler.X + 4, _cooler.Y + 30), Facing.Down));
        Add(Zone.Lounge, new Spot("lounge:0", Zone.Lounge, new Point(_kitchen.Right - 10, bottom - 12), Facing.Left));
        for (var i = 0; i < 3; i++)
            Add(Zone.Couch, new Spot($"couch:{i}", Zone.Couch, new Point(_couch.X + 9 + i * 13, _couch.Y + 18), Facing.Down, Seated: true));

        // The front door: whoever needs you lines up just inside it.
        var door = _fw / 2;
        foreach (var k in new[] { 0, -1, 1, -2, 2 })
            Add(Zone.Door, new Spot($"door:{Math.Abs(k) * 2 - (k < 0 ? 1 : 0)}", Zone.Door, new Point(door + k * 14, bottom - 10), Facing.Down));
        _entrance = new Spot("entrance", Zone.Door, new Point(door, _fh - 2), Facing.Up);

        BuildGrid(cx0, cx1);
        PlaceCouches();
        _bakedTown = WorkflowTown.Instance.Stamp;
        _static = BuildStatic();
        BuildProps();
        // Everyone still in the roster is placed again where the new plan puts them; departing workers walk out the front door.
        foreach (var walker in _walkers.Values)
            if (_byId.ContainsKey(walker.Id)) { walker.Goal = null; walker.Path.Clear(); }
    }

    private void Add(Zone zone, Spot spot) => _spots[zone].Add(spot);

    private void BuildGrid(double cx0, double cx1)
    {
        var cols = (int)Math.Ceiling(_fw / Cell);
        var rows = (int)Math.Ceiling(_fh / Cell);
        _blocked = new bool[cols, rows];
        var door = _fw / 2;
        Block(new Rect(0, 0, _fw, WallH + 3));
        Block(new Rect(0, 0, Side + 1, _fh));
        Block(new Rect(_fw - Side - 1, 0, Side + 1, _fh));
        Block(new Rect(0, _fh - BottomWall - 1, door - 12, BottomWall + 1));
        Block(new Rect(door + 12, _fh - BottomWall - 1, _fw - door - 12, BottomWall + 1));
        foreach (var desk in _desks.Values) Block(new Rect(desk.X + 4, desk.Y + 8, 26, 18));
        foreach (var shelf in _shelves) Block(new Rect(shelf.X, WallH, 30, 6));
        foreach (var cabinet in _cabinets) Block(new Rect(cabinet.X, cabinet.Y + 6, 10, 8));
        Block(new Rect(_kiosk.X, _kiosk.Y + 8, 26, 8));
        foreach (var rack in _racks) Block(new Rect(rack.X, WallH, 14, 6));
        foreach (var t in _terminals) Block(new Rect(t.X, t.Y + 6, 20, 7));
        Block(new Rect(_table.X, _table.Y + 4, 48, 14));
        Block(new Rect(_counter.X, _counter.Y, 38, 12));
        Block(new Rect(_cooler.X, _cooler.Y + 12, 9, 8));
        Block(new Rect(_couch.X, _couch.Y, 44, 15));
        Block(new Rect(_armchair.X, _armchair.Y, 24, 12));
        Block(new Rect(_printer.X, _printer.Y + 4, 18, 8));
        foreach (var plant in Plants()) Block(new Rect(plant.X - 1, plant.Y + 1, 10, 7));
    }

    private void Block(Rect area)
    {
        area.Inflate(1, 1);
        var cols = _blocked.GetLength(0);
        var rows = _blocked.GetLength(1);
        for (var cx = Math.Max(0, (int)(area.Left / Cell)); cx < cols && cx * Cell <= area.Right; cx++)
            for (var cy = Math.Max(0, (int)(area.Top / Cell)); cy < rows && cy * Cell <= area.Bottom; cy++)
                if (area.Contains(new Point((cx + 0.5) * Cell, (cy + 0.5) * Cell))) _blocked[cx, cy] = true;
    }

    /// <summary>Potted plants: one by the couch, one either side of the front door.</summary>
    private IEnumerable<Point> Plants()
    {
        var bottom = _fh - BottomWall;
        yield return new Point(_kitchen.Right - 14, bottom - 30);
        yield return new Point(_fw / 2 - 46, bottom - 12);
        yield return new Point(_fw / 2 + 38, bottom - 12);
        yield return new Point(_meeting.Right - 13, bottom - 12);
    }

    // ---- who goes where -------------------------------------------------------------------------------------------

    private void Advance(double time, double dt, bool moving)
    {
        foreach (var agent in _shown)
        {
            if (_walkers.TryGetValue(agent.Id, out var existing))
            {
                existing.Label = agent.Label;
                existing.LastNode = agent;
                if (existing.Doing == Activity.Leaving) { existing.Goal = null; existing.Path.Clear(); }
                continue;
            }
            var walker = new Walker { Id = agent.Id, Label = agent.Label, LastNode = agent };
            // A worker that turns up while the floor is running comes in through the front door.
            if (_placed && moving && !_seen.Contains(agent.Id))
            {
                walker.Foot = _entrance.Foot;
                walker.Goal = _entrance;
                walker.Since = double.NegativeInfinity;
            }
            _walkers[agent.Id] = walker;
            _seen.Add(agent.Id);
        }

        // Workers that have been idle for over a minute and left the roster get up and walk out the front door.
        foreach (var id in _walkers.Keys.Where(k => !_byId.ContainsKey(k)).ToList())
        {
            var walker = _walkers[id];
            if (!moving || walker.LastNode?.Chat.Status == "closed")
            {
                _walkers.Remove(id);
                _seen.Remove(id);
                continue;
            }
            if (walker.Doing != Activity.Leaving)
            {
                var exit = _entrance with { Key = "exit", Facing = Facing.Down };
                walker.Goal = exit;
                walker.Doing = Activity.Leaving;
                walker.Since = time;
                Route(walker, exit.Foot);
            }
            Step(walker, dt, time);
            if (walker.Arrived)
            {
                _walkers.Remove(id);
                _seen.Remove(id);
            }
        }

        // Shared stations stay with whoever already holds one; the rest are handed out in roster order. Thinking between
        // two calls, a worker stays at the station it is working at instead of trooping back to its desk and out again -
        // at a terminal only for a few seconds, though: the server room is for running commands, not thinking them over.
        var holders = new Dictionary<string, string>(StringComparer.Ordinal);
        var plan = new Dictionary<string, (Activity Doing, Spot Spot)>(StringComparer.Ordinal);
        foreach (var agent in _shown)
        {
            var (doing, _) = Want(agent);
            var zone = ZoneOf(doing);
            var walker = _walkers[agent.Id];
            if (doing == Activity.Shell) walker.LastShell = time;
            if (walker.Goal is not { } held) continue;
            var keep = held.Zone == zone && zone is not (Zone.Desk or Zone.Visit or Zone.Lounge)
                || doing == Activity.Thinking && held.Zone is Zone.Library or Zone.Kiosk or Zone.Board or Zone.Table or Zone.Visit
                || doing == Activity.Thinking && held.Zone == Zone.Terminal && moving
                    && (!walker.Arrived || time - Math.Max(walker.LastShell, walker.Since) < TerminalGrace)
                // Running a command or a search from its own desk because the stations were full, it finishes it there.
                || doing == walker.Doing && held.Zone == Zone.Desk && zone is Zone.Terminal or Zone.Kiosk;
            if (keep && holders.TryAdd(held.Key, agent.Id)) plan[agent.Id] = (doing, held);
        }
        foreach (var agent in _shown)
        {
            if (plan.ContainsKey(agent.Id)) continue;
            var (doing, visit) = Want(agent);
            var walker = _walkers[agent.Id];
            var zone = ZoneOf(doing);
            var spot = zone switch
            {
                Zone.Desk => _seats[agent.Id],
                Zone.Visit when visit is not null && _visits.TryGetValue(visit, out var beside) && !holders.ContainsKey(beside.Key) => beside,
                // A broadcast, or a message to someone off the floor, is announced at the whiteboard.
                Zone.Visit => Free(Zone.Board, holders),
                Zone.Couch => FreeSeat(Zone.Couch, holders) ?? Wander(walker, time, moving, holders),
                Zone.Lounge => Wander(walker, time, moving, holders),
                // Every terminal taken, or both kiosks: run it from its own desk rather than stand in line doing nothing.
                Zone.Terminal or Zone.Kiosk => FreeSeat(zone, holders) ?? _seats[agent.Id],
                _ => Free(zone, holders),
            };
            holders[spot.Key] = agent.Id;
            plan[agent.Id] = (doing, spot);
        }
        // Every couch seat spoken for: send for another (WorkflowFloorView.Deliveries.cs).
        OrderCouchIfFull(plan.Values.Count(p => ZoneOf(p.Doing) == Zone.Couch), time, moving);

        foreach (var agent in _shown)
        {
            var walker = _walkers[agent.Id];
            var (doing, spot) = plan[agent.Id];
            if (!moving || walker.Goal is null)
            {
                Place(walker, spot, time);
                walker.Doing = doing;
                continue;
            }
            if (walker.Goal.Key == spot.Key) walker.Doing = doing;
            // Just arrived somewhere: finish the moment before setting off, so short steps do not send it pacing.
            else if (!walker.Arrived || time - walker.Since >= MinDwell)
            {
                walker.Goal = spot;
                walker.Doing = doing;
                walker.Since = time;
                Route(walker, spot.Foot);
            }
            Step(walker, dt, time);
        }
        _placed = true;
    }

    private Spot Free(Zone zone, Dictionary<string, string> holders)
    {
        if (FreeSeat(zone, holders) is { } free) return free;
        var spots = _spots[zone];
        // Every station is taken: queue behind the front row, a step further back each lap.
        for (var lap = 1; lap <= 3; lap++)
            foreach (var spot in spots)
            {
                var key = spot.Key + "+" + lap;
                if (holders.ContainsKey(key)) continue;
                var back = (spot.Facing == Facing.Down ? -11 : 11) * lap;
                return spot with { Key = key, Foot = new Point(spot.Foot.X, spot.Foot.Y + back), Seated = false };
            }
        return spots[0];
    }

    private Spot? FreeSeat(Zone zone, Dictionary<string, string> holders) =>
        _spots[zone].FirstOrDefault(spot => !holders.ContainsKey(spot.Key));

    /// <summary>Idle workers drift between the coffee machine, the water cooler and the windows, staying a while at each.</summary>
    private Spot Wander(Walker walker, double time, bool moving, Dictionary<string, string> holders)
    {
        var spots = _spots[Zone.Lounge];
        var first = (int)(Hash(walker.Id, 7) * spots.Count);
        if (!moving)
        {
            for (var i = 0; i < spots.Count; i++)
                if (!holders.ContainsKey(spots[(first + i) % spots.Count].Key)) return spots[(first + i) % spots.Count];
            return spots[first];
        }
        if (walker.Goal is { Zone: Zone.Lounge } current && !holders.ContainsKey(current.Key) && (!walker.Arrived || !(time >= walker.NextErrand)))
            return current;
        walker.Errand++;
        var start = (int)(Hash(walker.Id, walker.Errand) * spots.Count);
        for (var i = 0; i < spots.Count; i++)
        {
            var spot = spots[(start + i) % spots.Count];
            if (spot.Key != walker.Goal?.Key && !holders.ContainsKey(spot.Key)
                && !_walkers.Values.Any(other => other != walker && other.Goal?.Key == spot.Key)) return spot;
        }
        return walker.Goal is { Zone: Zone.Lounge } stay ? stay : spots[start];
    }

    private void Place(Walker walker, Spot spot, double time)
    {
        var moved = walker.Goal?.Key != spot.Key;
        walker.Goal = spot;
        walker.Path.Clear();
        walker.Foot = spot.Foot;
        walker.Facing = spot.Facing;
        if (moved)
        {
            walker.Since = time;
            walker.NextErrand = time + 4 + Hash(walker.Id, walker.Errand) * 8;
        }
    }

    private void Step(Walker walker, double dt, double time)
    {
        if (walker.Arrived) return;
        var budget = WalkSpeed * dt;
        while (budget > 1e-6 && walker.Path.Count > 0)
        {
            var next = walker.Path[0];
            var dx = next.X - walker.Foot.X;
            var dy = next.Y - walker.Foot.Y;
            var distance = Math.Sqrt(dx * dx + dy * dy);
            if (distance > 0.05)
                walker.Facing = Math.Abs(dx) >= Math.Abs(dy) ? dx < 0 ? Facing.Left : Facing.Right : dy < 0 ? Facing.Up : Facing.Down;
            if (distance <= budget)
            {
                walker.Foot = next;
                walker.Path.RemoveAt(0);
                walker.Stride += distance;
                budget -= distance;
            }
            else
            {
                walker.Foot = new Point(walker.Foot.X + dx / distance * budget, walker.Foot.Y + dy / distance * budget);
                walker.Stride += budget;
                budget = 0;
            }
        }
        if (walker.Arrived && walker.Goal is { } goal)
        {
            walker.Foot = goal.Foot;
            walker.Facing = goal.Facing;
            walker.Since = time;
            walker.NextErrand = time + 6 + Hash(walker.Id, walker.Errand) * 9;
        }
    }

    private void Route(Walker walker, Point to)
    {
        walker.Path.Clear();
        walker.Path.AddRange(Waypoints(walker.Foot, to));
    }

    /// <summary>A walking route along the aisles: A* over the floor grid, 4-way, with a small cost for every turn so a
    /// worker walks straight down an aisle instead of zig-zagging across the carpet. Kept to its turning points.</summary>
    private List<Point> Waypoints(Point origin, Point to)
    {
        var cols = _blocked.GetLength(0);
        var rows = _blocked.GetLength(1);
        var start = Open(CellOf(origin));
        var goal = Open(CellOf(to));
        if (cols == 0 || start < 0 || goal < 0 || start == goal) return [to];
        const int Dirs = 4;
        int[] stepX = [0, 0, -1, 1], stepY = [-1, 1, 0, 0];
        var cost = new double[cols * rows * Dirs];
        Array.Fill(cost, double.MaxValue);
        var from = new int[cost.Length];
        var done = new bool[cost.Length];
        var open = new PriorityQueue<int, double>();
        var (gx, gy) = (goal % cols, goal / cols);
        for (var d = 0; d < Dirs; d++)
        {
            cost[start * Dirs + d] = 0;
            from[start * Dirs + d] = -1;
            open.Enqueue(start * Dirs + d, 0);
        }
        var end = -1;
        while (open.TryDequeue(out var node, out _))
        {
            if (done[node]) continue;
            done[node] = true;
            var cell = node / Dirs;
            if (cell == goal) { end = node; break; }
            var (x, y) = (cell % cols, cell / cols);
            for (var d = 0; d < Dirs; d++)
            {
                var nx = x + stepX[d];
                var ny = y + stepY[d];
                if (nx < 0 || ny < 0 || nx >= cols || ny >= rows || _blocked[nx, ny]) continue;
                var next = (ny * cols + nx) * Dirs + d;
                var g = cost[node] + 1 + (d == node % Dirs ? 0 : 0.6);
                if (g >= cost[next]) continue;
                cost[next] = g;
                from[next] = node;
                open.Enqueue(next, g + Math.Abs(nx - gx) + Math.Abs(ny - gy));
            }
        }
        if (end < 0) return [to];
        var cells = new List<int>();
        for (var node = end; node >= 0; node = from[node]) cells.Add(node / Dirs);
        cells.Reverse();
        Point Center(int c) => new((c % cols + 0.5) * Cell, (c / cols + 0.5) * Cell);
        var path = new List<Point> { Center(cells[0]) };
        for (var i = 1; i < cells.Count - 1; i++)
        {
            var turn = cells[i] - cells[i - 1] != cells[i + 1] - cells[i];
            if (turn) path.Add(Center(cells[i]));
        }
        path.Add(Center(cells[^1]));
        path.Add(to);
        return path;
    }

    private bool OpenFloor(Point at)
    {
        // The whole sprite has to fit, not just the feet.
        var cell = CellOf(at);
        return cell >= 0 && at.X > Side + 6 && at.X < _fw - Side - 6 && at.Y > WallH + 10 && at.Y < _fh - BottomWall
            && !_blocked[cell % _blocked.GetLength(0), cell / _blocked.GetLength(0)];
    }

    private int CellOf(Point at)
    {
        var cols = _blocked.GetLength(0);
        var rows = _blocked.GetLength(1);
        if (cols == 0) return -1;
        return Math.Clamp((int)(at.Y / Cell), 0, rows - 1) * cols + Math.Clamp((int)(at.X / Cell), 0, cols - 1);
    }

    /// <summary>The cell itself when it is open floor, else the nearest open one (a seat tucked against a desk).</summary>
    private int Open(int cell)
    {
        if (cell < 0) return -1;
        var cols = _blocked.GetLength(0);
        var rows = _blocked.GetLength(1);
        var (x, y) = (cell % cols, cell / cols);
        for (var r = 0; r <= 6; r++)
            for (var dy = -r; dy <= r; dy++)
                for (var dx = -r; dx <= r; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
                    var (nx, ny) = (x + dx, y + dy);
                    if (nx >= 0 && ny >= 0 && nx < cols && ny < rows && !_blocked[nx, ny]) return ny * cols + nx;
                }
        return -1;
    }

    // ---- workers ------------------------------------------------------------------------------------------------

    private Rect DrawWorker(DrawingContext dc, AgentActivityNode agent, Walker walker, double time, bool focused)
    {
        var (art, flip, drop) = PoseOf(walker, time);
        var spot = walker.Arrived ? walker.Goal : null;
        if (spot is not { Seated: true })
            dc.DrawEllipse(Fill(Color.FromArgb(70, 0, 0, 0)), null, Dip(new Point(walker.Foot.X, walker.Foot.Y - 0.5)), 4.5 * _p, 1.4 * _p);
        var body = Sprite(dc, agent.Id, art, flip, walker.Foot, drop, _p, WorkflowGuilds.Shirt(agent));
        DrawProp(dc, agent, walker.Foot, art, drop);
        if (spot is { Seated: true, Facing: Facing.Up })
        {
            // At its own desk the chair is as good as that desk; every other seat is the standard chair.
            if (spot.Key == "desk:" + agent.Id) ChairFront(dc, spot.Foot, ShownTier(agent.Id, agent));
            else ChairFront(dc, spot.Foot);
        }
        if (agent.Chat.IsBridgeManager || agent.IsCentralPlanner)
        {
            var gold = PixelArt.C("#F5CB5C");
            var top = walker.Foot.Y - 16 + drop;
            Box(dc, gold, walker.Foot.X - 3, top - 1, 6, 1);
            for (var i = -1; i <= 1; i++) Box(dc, gold, walker.Foot.X + i * 2.5 - 0.5, top - 2, 1, 1);
        }
        if (walker.Arrived && walker.Doing == Activity.Asleep && spot?.Zone == Zone.Desk) Snore(dc, Dip(new Point(walker.Foot.X + 5, walker.Foot.Y - 12)), time);
        if (focused) dc.DrawRectangle(null, Stroke(Color.FromRgb(0xF5, 0xCB, 0x5C), 1.5), Inflate(body, 2, 2));
        return body;
    }

    private (string Art, bool Flip, double Drop) PoseOf(Walker walker, double time)
    {
        var h = Hash(walker.Id);
        if (!walker.Arrived || walker.Goal is null)
        {
            var (walk, flip) = WalkPose(walker.Facing, walker.Stride);
            return (walk, flip, 0);
        }
        if (Cheering(walker, time) is { } cheer) return cheer;
        var spot = walker.Goal;
        bool Beat(double rate) => (int)Math.Floor(time * rate + h * 10) % 2 == 0;
        // Thinking away from its desk, a worker stands where it is with a hand to its head.
        if (walker.Doing == Activity.Thinking && spot.Zone != Zone.Desk && !spot.Seated)
            return spot.Facing switch { Facing.Left => ("side", false, 0), Facing.Right => ("side", true, 0), Facing.Down => ("front", false, 0), _ => ("think", false, 0) };
        switch (spot.Zone)
        {
            case Zone.Desk:
                return (walker.Doing switch
                {
                    Activity.Typing or Activity.Replying or Activity.Shell => Beat(7) ? "sit-type1" : "sit-type2",
                    Activity.Thinking => "sit-think",
                    Activity.Asleep => "sit-sleep",
                    Activity.Failed => "sit-slump",
                    _ => "sit-read",
                }, false, 3);
            case Zone.Terminal:
                return (Beat(6) ? "term1" : "term2", false, 0);
            case Zone.Kiosk:
                return (Beat(1.6) ? "term1" : "back", false, 0);
            case Zone.Library:
                return ("reach", Beat(0.7), 0);
            case Zone.Board:
                return ("reach", Beat(2.5), 0);
            case Zone.Door:
                return (Beat(2.5) ? "wave1" : "wave2", false, 0);
            case Zone.Couch:
                return (walker.Doing == Activity.Resting ? "sit-mug" : "sit-front", false, 2);
            case Zone.Table when spot.Seated:
                return spot.Facing == Facing.Down ? ("sit-front", false, 2) : ("sit-read", false, 3);
        }
        if (spot.Look == "book") return ("sit-book", false, 2);
        if (spot.Look == "printer") return (Beat(1.2) ? "term1" : "back", false, 0);
        return spot.Facing switch
        {
            Facing.Up => ("back", false, 0),
            Facing.Left => ("side", false, 0),
            Facing.Right => ("side", true, 0),
            _ => (spot.Look == "mug" ? "mug" : "front", false, 0),
        };
    }

    private Rect Sprite(DrawingContext dc, string id, string art, bool flip, Point foot, double drop, double scale, Color? shirt = null)
    {
        var drawing = FloorSprite(art, flip, id, shirt, scale);
        var x = Math.Round(_origin.X + foot.X * _p - 6 * scale);
        var y = Math.Round(_origin.Y + foot.Y * _p - (16 - drop) * scale);
        dc.PushTransform(new TranslateTransform(x, y));
        dc.DrawDrawing(drawing);
        dc.Pop();
        return new Rect(x, y, 12 * scale, (16 - drop) * scale);
    }

    /// <summary>A subagent on the floor. <see cref="Index"/> is its place in its lead's <see cref="AgentActivityNode.Subagents"/>.</summary>
    private sealed record Intern(string Id, int Index, Point Foot, string Art, bool Flip, double Drop, bool Seated, Facing Facing, Color? Shirt = null);

    private const string InternPrefix = "intern:";

    /// <summary>The hover key of a lead's <paramref name="index"/>th intern.</summary>
    private static string InternKey(AgentActivityNode lead, int index) => InternPrefix + lead.Id + ":" + index;

    /// <summary>The intern under the pointer: its lead, and which of the lead's subagents it is.</summary>
    private (AgentActivityNode Lead, int Index)? HoveredIntern()
    {
        if (HoverKey is not { } key || !key.StartsWith(InternPrefix, StringComparison.Ordinal)) return null;
        var split = key.LastIndexOf(':');
        return split > InternPrefix.Length && int.TryParse(key[(split + 1)..], out var index)
            && _byId.TryGetValue(key[InternPrefix.Length..split], out var lead) ? (lead, index) : null;
    }

    /// <summary>Subagents tag along as interns: round the table while their lead delegates, at its elbow otherwise.
    /// Each is placed on its own, so one sitting behind the table is drawn behind it.</summary>
    private IEnumerable<Intern> Interns(AgentActivityNode agent, Walker lead, double time)
    {
        var count = Math.Min(3, agent.ActiveChildren);
        var free = lead.Arrived && lead.Goal?.Zone == Zone.Table
            ? _spots[Zone.Table].Where(s => !Occupied(s.Key)).Take(count).ToList() : [];
        // Otherwise at its elbow, wherever there is floor to stand on.
        (double X, double Y)[] around = [(-11, 4), (11, 4), (0, 10), (-11, 11), (11, 11), (-6, 16), (6, 16)];
        var beside = lead.Arrived ? around.Where(o => OpenFloor(new Point(lead.Foot.X + o.X, lead.Foot.Y + o.Y))).ToList() : [];
        if (beside.Count < count) beside = around.ToList();
        for (var i = 0; i < count; i++)
        {
            var id = agent.Id + ":intern" + i;
            Point foot;
            string art;
            var flip = false;
            if (i < free.Count)
            {
                foot = free[i].Foot;
                art = free[i].Seated ? free[i].Facing == Facing.Down ? "sit-front" : "sit-read" : "side";
                flip = free[i].Facing == Facing.Right;
            }
            else
            {
                var (dx, dy) = beside[i - free.Count];
                foot = new Point(lead.Foot.X + dx, lead.Foot.Y + dy);
                if (!lead.Arrived)
                {
                    var second = (int)((lead.Stride + i * 2) / 3) % 2 == 1;
                    (art, flip) = lead.Facing switch
                    {
                        Facing.Up => (second ? "back-walk2" : "back-walk1", false),
                        Facing.Down => (second ? "front-walk2" : "front-walk1", false),
                        var side => (second ? "side" : "side-walk", side == Facing.Right),
                    };
                }
                else
                {
                    // Turned toward their lead, bobbing as they chat.
                    art = i == 2 ? "back" : "side";
                    flip = i == 0;
                    foot.Y -= Math.Abs(Math.Sin(time * 4 + i)) * 0.8;
                }
            }
            var seated = i < free.Count && free[i].Seated;
            // Interns wear their lead's colours.
            yield return new Intern(id, i, foot, art, flip, seated ? free[i].Facing == Facing.Down ? 2 : 3 : 0, seated, i < free.Count ? free[i].Facing : lead.Facing,
                WorkflowGuilds.ShirtOf(agent));
        }
    }

    private Rect DrawIntern(DrawingContext dc, Intern intern, bool focused)
    {
        var scale = Math.Max(1, Math.Round(_p * 0.7 * 2) / 2);
        if (!intern.Seated) dc.DrawEllipse(Fill(Color.FromArgb(60, 0, 0, 0)), null, Dip(new Point(intern.Foot.X, intern.Foot.Y - 0.5)), 3.2 * _p, 1 * _p);
        var body = Sprite(dc, intern.Id, intern.Art, intern.Flip, intern.Foot, intern.Drop, scale, intern.Shirt);
        // The back of a chair in front of an intern sitting with its back to us, as for anyone else.
        if (intern.Seated && intern.Facing == Facing.Up) ChairFront(dc, intern.Foot);
        if (focused) dc.DrawRectangle(null, Stroke(Color.FromRgb(0xF5, 0xCB, 0x5C), 1.5), Inflate(body, 2, 2));
        return body;
    }

    private void Snore(DrawingContext dc, Point at, double time)
    {
        for (var i = 0; i < 3; i++)
        {
            var t = (time * 0.4 + i / 3.0) % 1;
            dc.PushOpacity(0.9 * (1 - t));
            dc.DrawText(Text("z", 10 + i * 2, Color.FromRgb(0xE8, 0xEE, 0xFF), bold: true), new Point(at.X + t * 12, at.Y - t * 24));
            dc.Pop();
        }
    }

    // ---- names and speech bubbles ---------------------------------------------------------------------------------

    private void DrawNames(DrawingContext dc, List<(AgentActivityNode Agent, Walker Walker, Rect Body)> drawn)
    {
        if (_p < 2) return;
        var size = Math.Clamp(2.9 * _p, 9, 11.5);
        var desks = new List<Rect>();
        // A name under every desk, and under a worker while it is away from its own.
        foreach (var (id, desk) in _desks)
        {
            if (!_byId.TryGetValue(id, out var agent)) continue;
            desks.Add(NameTag(dc, agent.Label, Dip(new Point(desk.Seat.X, desk.Seat.Y + 3)), size, Color.FromArgb(200, 0x12, 0x15, 0x1C), WorkflowGuilds.ShirtOf(agent)));
        }
        foreach (var (agent, walker, body) in drawn)
        {
            if (walker.At("desk:" + agent.Id)) continue;
            NameTag(dc, agent.Label, new Point(body.Left + body.Width / 2, body.Bottom + 1), size, Color.FromArgb(215, 0x24, 0x2A, 0x38), WorkflowGuilds.ShirtOf(agent), desks);
        }
    }

    /// <summary>A name plate with a stripe in the colour its owner wears.</summary>
    private Rect NameTag(DrawingContext dc, string label, Point center, double size, Color plate, Color stripe, IReadOnlyList<Rect>? avoid = null)
    {
        var text = Text(label, size, Color.FromRgb(0xF2, 0xF4, 0xF8), bold: true, maxWidth: 30 * _p);
        var box = new Rect(Math.Round(center.X - text.Width / 2 - 5), Math.Round(center.Y), text.Width + 10, text.Height + 2);
        // A worker visiting someone's desk keeps its name beside the desk's own name, not on top of it.
        if (avoid?.FirstOrDefault(other => other.IntersectsWith(box), Rect.Empty) is { IsEmpty: false } taken)
            box.X = box.Left + box.Width / 2 < taken.Left + taken.Width / 2 ? taken.Left - box.Width - 2 : taken.Right + 2;
        dc.DrawRoundedRectangle(Fill(plate), null, box, 3, 3);
        dc.DrawRectangle(Fill(stripe), null, new Rect(box.Left + 2, box.Top + 3, 2, box.Height - 6));
        dc.DrawText(text, new Point(box.Left + 6, box.Top + 1));
        return box;
    }

    private void DrawBubbles(DrawingContext dc, Rect bounds, List<(AgentActivityNode Agent, Walker Walker, Rect Body)> drawn, double time)
    {
        var placed = new List<Rect>();
        var size = Math.Clamp(3.3 * _p, 10, 12.5);
        // Whoever needs you speaks first, then failures, then the working; nearer the top first so lower bubbles stack under them.
        foreach (var (agent, walker, body) in drawn.OrderBy(d => d.Walker.Doing switch { Activity.NeedsYou => 0, Activity.Failed => 1, _ => 2 }).ThenBy(d => d.Body.Top))
        {
            var anchor = new Point(Math.Round(body.Left + body.Width / 2), Math.Round(body.Top));
            if (walker.Doing == Activity.Thinking && walker.Arrived)
            {
                ThoughtDots(dc, anchor, time, Hash(agent.Id));
                continue;
            }
            var (text, glyph, edge) = Speech(agent, walker);
            if (text is null) continue;
            var label = Text(text, size, Color.FromRgb(0x2A, 0x32, 0x40), maxWidth: Math.Clamp(46 * _p, 120, 250));
            var icon = Glyph(glyph, size, edge == Colors.White ? StateColor(agent.State) : edge);
            var width = label.Width + icon.Width + 18;
            var height = Math.Max(label.Height, icon.Height) + 6;
            Rect? spot = null;
            // A floor this small has no room for words over every head; the badges below carry the icon and hover the rest.
            foreach (var (dx, dy) in _p < 2 ? [] : new[] { (0.0, 0.0), (0.0, -1.0), (-0.6, 0.0), (0.6, 0.0), (0.0, -2.0), (-0.6, -1.0), (0.6, -1.0) })
            {
                var box = new Rect(anchor.X - width / 2 + dx * width, anchor.Y - height - 6 + dy * (height + 4), width, height);
                box.X = Math.Clamp(box.X, 4, Math.Max(4, bounds.Width - box.Width - 4));
                box.Y = Math.Max(4, box.Y);
                if (placed.Any(r => r.IntersectsWith(box))) continue;
                spot = box;
                break;
            }
            if (spot is not { } bubble)
            {
                // No room for words: a small badge with the step's icon instead.
                var radius = Math.Clamp(2.7 * _p, 5.5, 8);
                var center = new Point(anchor.X, anchor.Y - radius - 3);
                var badge = new Rect(center.X - radius, center.Y - radius, radius * 2, radius * 2);
                if (placed.Any(r => r.IntersectsWith(badge))) continue;
                dc.DrawEllipse(Fill(Color.FromRgb(0xFB, 0xFC, 0xFE)), Stroke(edge == Colors.White ? StateColor(agent.State) : edge, 1.2), center, radius, radius);
                var small = Glyph(glyph, radius * 1.15, edge == Colors.White ? StateColor(agent.State) : edge);
                dc.DrawText(small, new Point(center.X - small.Width / 2, center.Y - small.Height / 2));
                placed.Add(badge);
                continue;
            }
            placed.Add(bubble);
            var tail = Math.Clamp(anchor.X, bubble.Left + 6, bubble.Right - 6);
            dc.DrawLine(Stroke(edge == Colors.White ? Color.FromRgb(0xC8, 0xCE, 0xDA) : edge, 1.5), new Point(tail, bubble.Bottom), new Point(anchor.X, anchor.Y - 1));
            dc.DrawRoundedRectangle(Fill(Color.FromRgb(0xFB, 0xFC, 0xFE)), Stroke(edge == Colors.White ? Color.FromRgb(0xC8, 0xCE, 0xDA) : edge, 1.5), bubble, 5, 5);
            dc.DrawText(icon, new Point(bubble.Left + 6, bubble.Top + (bubble.Height - icon.Height) / 2));
            dc.DrawText(label, new Point(bubble.Left + 12 + icon.Width, bubble.Top + (bubble.Height - label.Height) / 2));
        }
    }

    /// <summary>What a worker says over its head: its live step while working, what it needs while it waits.</summary>
    private (string? Text, string Glyph, Color Edge) Speech(AgentActivityNode agent, Walker walker)
    {
        var amber = Color.FromRgb(0xF2, 0xB8, 0x4B);
        var red = Color.FromRgb(0xF2, 0x6D, 0x6D);
        switch (walker.Doing)
        {
            // "To Claude 1 · Can you rerun the wall checks?" rather than the tool's own name.
            case Activity.Messaging when Want(agent).Visit is { } to && _byId.TryGetValue(to, out var peer) && agent.Now is { } message:
                var split = message.Text.IndexOf(" · ", StringComparison.Ordinal);
                return ("To " + peer.Label + (split >= 0 ? message.Text[split..] : ""), message.Glyph, Colors.White);
            case Activity.NeedsYou:
                return (agent.Now?.Text is { Length: > 0 } ask && agent.State == AgentActivityState.Working ? ask : agent.Status, WorkflowActivity.ApprovalGlyph, amber);
            case Activity.Failed:
                return ("Failed: " + (agent.Last?.Failed == true ? agent.Last.Text : agent.Status), WorkflowActivity.ErrorGlyph, red);
            case Activity.Waiting:
                return (agent.Status, WorkflowActivity.WaitingGlyph, amber);
            case Activity.Asleep or Activity.Resting or Activity.Idle:
                return (null, "", Colors.White);
        }
        var now = agent.Now ?? WorkflowAgentCard.Resting(agent, DateTime.UtcNow);
        return (now.Text, now.Glyph.Length > 0 ? now.Glyph : WorkflowActivity.WorkingGlyph, Colors.White);
    }

    private void ThoughtDots(DrawingContext dc, Point anchor, double time, double h)
    {
        var k = Math.Clamp(_p / 3, 0.55, 1);
        var ink = Fill(Color.FromRgb(0xFB, 0xFC, 0xFE));
        var edge = Stroke(Color.FromRgb(0xC8, 0xCE, 0xDA), 1.2);
        dc.DrawEllipse(ink, edge, new Point(anchor.X + 4 * k, anchor.Y - 4 * k), 2 * k, 2 * k);
        var cloud = new Rect(anchor.X - 2 * k, anchor.Y - 24 * k, 34 * k, 16 * k);
        dc.DrawRoundedRectangle(ink, edge, cloud, 8 * k, 8 * k);
        for (var i = 0; i < 3; i++)
        {
            var lit = (int)Math.Floor(time * 3 + h * 10) % 3 == i;
            dc.DrawEllipse(Fill(lit ? Color.FromRgb(0x4F, 0x8F, 0xE8) : Color.FromRgb(0xA8, 0xB0, 0xBE)), null,
                new Point(cloud.Left + (9 + i * 8) * k, cloud.Top + 8 * k), 2.2 * k, 2.2 * k);
        }
    }

    // ---- cards ------------------------------------------------------------------------------------------------------

    private void DrawPodCard(DrawingContext dc, Rect bounds, Pod pod, DateTime nowUtc)
    {
        var project = pod.Project;
        var rows = project.Agents.Take(6).ToList();
        var width = Math.Min(380, bounds.Width - 24);
        // Where the pod stands: its look, its directory's tokens and what the next look takes.
        var furnished = WorkflowTown.Enabled && project.Key.Length > 0
            ? "Office: " + WorkflowTown.Progress(WorkflowTown.RoomTierName(WorkflowTown.Instance.RoomTier(project.Key)),
                WorkflowTown.Instance.ProjectTokens(project.Key), WorkflowTown.RoomTierTokens)
            : null;
        var height = 70 + rows.Count * 60 + (project.Agents.Count > rows.Count ? 18 : 0) + 22 + (furnished is null ? 0 : 18);
        var card = Place(bounds, R(pod.Area), width, height);
        var (bg, border, text, muted) = CardColors();
        dc.DrawRoundedRectangle(Fill(Color.FromArgb(100, 0, 0, 0)), null, new Rect(card.Left + 3, card.Top + 5, card.Width, card.Height), 10, 10);
        dc.DrawRoundedRectangle(Fill(bg), Stroke(border, 1), card, 10, 10);
        var x = card.Left + 16; var y = card.Top + 12; var inner = card.Width - 32;
        dc.DrawText(Text(project.Name, 16, text, bold: true, maxWidth: inner - 100), new Point(x, y));
        var working = Text(project.Working > 0 ? $"● {project.Working} working" : "no one working", 11.5,
            project.Working > 0 ? StateColor(AgentActivityState.Working) : muted, bold: true);
        dc.DrawText(working, new Point(card.Right - 16 - working.Width, y + 3));
        y += 24;
        dc.DrawText(Text(project.Directory.Length == 0 ? "Chats without a project directory" : project.Directory, 11, muted, maxWidth: inner), new Point(x, y));
        y += 22;
        if (furnished is not null)
        {
            dc.DrawText(Text(furnished, 11, muted, maxWidth: inner), new Point(x, y - 4));
            y += 18;
        }
        dc.DrawRectangle(Fill(border), null, new Rect(x, y, inner, 1));
        y += 8;
        foreach (var agent in rows) { AgentLines(dc, agent, x, y, inner, nowUtc, text, muted); y += 60; }
        if (project.Agents.Count > rows.Count) { dc.DrawText(Text($"+{project.Agents.Count - rows.Count} more", 11, muted), new Point(x, y)); y += 18; }
        dc.DrawText(Text("Click the pod to filter · click a worker to open its chat", 10.5, muted, maxWidth: inner), new Point(x, y + 2));
    }

    private void DrawAgentCard(DrawingContext dc, Rect bounds, AgentActivityNode agent, DateTime nowUtc)
    {
        var anchor = Targets.LastOrDefault(t => t.Key == AgentKey(agent)).Bounds;
        if (anchor.Width <= 0) anchor = new Rect(bounds.Width / 2, bounds.Height / 2, 1, 1);
        var (bg, border, text, muted) = CardColors();
        // Where its desk stands: its look, its own chat's tokens and what the next look takes.
        var desk = WorkflowTown.Enabled
            ? "Desk: " + WorkflowTown.Progress(WorkflowTown.DeskTierName(DeskTierOf(agent)), WorkflowTown.Instance.SessionTokens(agent.Chat.SessionId), WorkflowTown.DeskTierTokens)
              + (_deliveries.Any(d => d.Errand == Errand.Desk && d.DeskId == agent.Id && d.Leg != Leg.Out) ? " · being installed" : "")
            : null;
        var team = agent.Subagents.Take(MaxTeamLines).ToList();
        var card = Place(bounds, anchor, Math.Min(350, bounds.Width - 24), (desk is null ? 128 : 146) + TeamHeight(agent, team.Count));
        dc.DrawRoundedRectangle(Fill(Color.FromArgb(100, 0, 0, 0)), null, new Rect(card.Left + 3, card.Top + 5, card.Width, card.Height), 10, 10);
        dc.DrawRoundedRectangle(Fill(bg), Stroke(border, 1), card, 10, 10);
        AgentLines(dc, agent, card.Left + 16, card.Top + 12, card.Width - 32, nowUtc, text, muted);
        var doing = _walkers.TryGetValue(agent.Id, out var walker) ? walker.Doing : Classify(agent);
        var at = walker?.Goal?.Zone;
        var where = walker is { Arrived: false } ? "On the way: " + Where(doing, at).ToLowerInvariant() : Where(doing, at);
        dc.DrawText(Text(where, 11.5, text, maxWidth: card.Width - 32), new Point(card.Left + 16, card.Top + 72));
        var model = agent.Account.Length > 0 ? agent.Model + " · " + agent.Account : agent.Model;
        dc.DrawText(Text(model + "  ·  " + agent.ProjectName, 11, muted, maxWidth: card.Width - 32), new Point(card.Left + 16, card.Top + 90));
        if (desk is not null) dc.DrawText(Text(desk, 11, muted, maxWidth: card.Width - 32), new Point(card.Left + 16, card.Top + 107));
        var y = card.Top + (desk is null ? 107 : 125);
        if (team.Count > 0) y = TeamLines(dc, agent, team, card.Left + 16, y, card.Width - 32, border, text, muted);
        dc.DrawText(Text("Click to open the chat", 10.5, muted), new Point(card.Left + 16, y));
    }

    /// <summary>Subagents listed by name on a lead's card; the rest are counted.</summary>
    private const int MaxTeamLines = 4;

    private static double TeamHeight(AgentActivityNode agent, int lines) =>
        lines == 0 ? 0 : 26 + lines * 19 + (agent.ActiveChildren > lines ? 17 : 0);

    /// <summary>A lead's working subagents on its card, each by name with the step it is on. Returns where the next
    /// line goes.</summary>
    private double TeamLines(DrawingContext dc, AgentActivityNode agent, IReadOnlyList<AgentActivitySubagent> team, double x, double y,
        double width, Color border, Color text, Color muted)
    {
        dc.DrawRectangle(Fill(border), null, new Rect(x, y, width, 1));
        var heading = agent.ActiveChildren == 1 ? "1 SUBAGENT WORKING" : $"{agent.ActiveChildren} SUBAGENTS WORKING";
        dc.DrawText(Text(heading, 9.5, muted, bold: true), new Point(x, y + 6));
        y += 26;
        var working = StateColor(AgentActivityState.Working);
        foreach (var sub in team)
        {
            dc.DrawEllipse(Fill(working), null, new Point(x + 4, y + 8), 3, 3);
            var name = Text(sub.Label, 11.5, text, bold: true, maxWidth: Math.Round(width * 0.38));
            dc.DrawText(name, new Point(x + 14, y));
            dc.DrawText(Text(sub.Now.Text, 11.5, muted, maxWidth: width - 22 - name.Width), new Point(x + 22 + name.Width, y));
            y += 19;
        }
        if (agent.ActiveChildren > team.Count)
        {
            dc.DrawText(Text($"+{agent.ActiveChildren - team.Count} more", 10.5, muted), new Point(x + 14, y));
            y += 17;
        }
        return y;
    }

    /// <summary>An intern's card: which of its lead's subagents it is, what it was asked for, the step it is on and
    /// the one it finished last.</summary>
    private void DrawInternCard(DrawingContext dc, Rect bounds, AgentActivityNode lead, int index)
    {
        var anchor = Targets.LastOrDefault(t => t.Key == InternKey(lead, index)).Bounds;
        if (anchor.Width <= 0) anchor = new Rect(bounds.Width / 2, bounds.Height / 2, 1, 1);
        var sub = index < lead.Subagents.Count ? lead.Subagents[index] : null;
        var (bg, border, text, muted) = CardColors();
        var card = Place(bounds, anchor, Math.Min(350, bounds.Width - 24), sub?.Last is null ? 118 : 136);
        dc.DrawRoundedRectangle(Fill(Color.FromArgb(100, 0, 0, 0)), null, new Rect(card.Left + 3, card.Top + 5, card.Width, card.Height), 10, 10);
        dc.DrawRoundedRectangle(Fill(bg), Stroke(border, 1), card, 10, 10);
        var x = card.Left + 16; var y = card.Top + 12; var width = card.Width - 32;
        var working = StateColor(AgentActivityState.Working);
        dc.DrawEllipse(Fill(working), null, new Point(x + 4, y + 9), 4, 4);
        var role = Text("Subagent", 11, working, bold: true);
        dc.DrawText(role, new Point(x + width - role.Width, y + 1));
        dc.DrawText(Text(sub?.Label ?? $"Subagent {index + 1}", 12.5, text, bold: true, maxWidth: width - role.Width - 24), new Point(x + 14, y));
        dc.DrawText(Text(sub is { Task.Length: > 0 } ? sub.Task : "Helping " + lead.Label, 12, text, maxWidth: width - 14), new Point(x + 14, y + 18));
        var now = sub?.Now ?? new AgentActivityStep(WorkflowActivity.WorkingGlyph, "Working");
        if (now.Glyph.Length > 0) dc.DrawText(Glyph(now.Glyph, 11, working), new Point(x + 14, y + 38));
        dc.DrawText(Text(now.Text, 11.5, text, maxWidth: width - 34), new Point(x + 32, y + 36));
        y += 56;
        if (sub?.Last is { } last)
        {
            var ink = last.Failed ? StateColor(AgentActivityState.Error) : muted;
            if (last.Glyph.Length > 0) dc.DrawText(Glyph(last.Glyph, 11, ink), new Point(x + 14, y + 2));
            dc.DrawText(Text("Last: " + last.Text, 11, ink, maxWidth: width - 34), new Point(x + 32, y + 1));
            y += 18;
        }
        var helping = lead.ActiveChildren > 1 ? $"Working for {lead.Label} · {index + 1} of {lead.ActiveChildren} subagents" : $"Working for {lead.Label}";
        dc.DrawText(Text(helping, 11, muted, maxWidth: width), new Point(x, y + 4));
        dc.DrawText(Text($"Click to open {lead.Label}'s chat", 10.5, muted, maxWidth: width), new Point(x, y + 22));
    }

    /// <summary>An intern is described as the subagent it is, not as its lead.</summary>
    protected override string DescribeHit(string key, object target)
    {
        if (!key.StartsWith(InternPrefix, StringComparison.Ordinal) || target is not AgentActivityNode lead) return base.DescribeHit(key, target);
        var index = int.TryParse(key[(key.LastIndexOf(':') + 1)..], out var i) ? i : 0;
        var sub = index < lead.Subagents.Count ? lead.Subagents[index] : null;
        return $"{sub?.Label ?? "Subagent"}, a subagent of {lead.Label}"
            + (sub is { Task.Length: > 0 } ? ": " + sub.Task : "")
            + $". Now {sub?.Now.Text ?? "working"}. Press Enter to open {lead.Label}'s chat.";
    }

    private (Color Bg, Color Border, Color Text, Color Muted) CardColors() =>
        (Alpha(Ink("Bg1", Color.FromRgb(0x16, 0x1A, 0x22)), 0.97), Ink("Border", Color.FromRgb(0x2E, 0x36, 0x44)),
         Ink("Text", Color.FromRgb(0xE7, 0xED, 0xF7)), Ink("Muted", Color.FromRgb(0xAA, 0xBA, 0xD0)));

    private void AgentLines(DrawingContext dc, AgentActivityNode agent, double x, double y, double width, DateTime nowUtc, Color text, Color muted)
    {
        var state = StateColor(agent.State);
        dc.DrawEllipse(Fill(state), null, new Point(x + 4, y + 9), 4, 4);
        var since = Since(agent, nowUtc);
        var status = Text(since.Length > 0 ? $"{agent.Status} · {since}" : agent.Status, 11, state, bold: true);
        dc.DrawText(status, new Point(x + width - status.Width, y + 1));
        dc.DrawText(Text(agent.Role.Length > 0 ? $"{agent.Label} · {agent.Role}" : agent.Label, 12.5, text, bold: true, maxWidth: width - status.Width - 24), new Point(x + 14, y));
        dc.DrawText(Text(agent.Task, 12, text, maxWidth: width - 14), new Point(x + 14, y + 18));
        var now = agent.Now ?? WorkflowAgentCard.Resting(agent, nowUtc);
        if (now.Glyph.Length > 0) dc.DrawText(Glyph(now.Glyph, 11, agent.State == AgentActivityState.Working ? state : muted), new Point(x + 14, y + 38));
        dc.DrawText(Text(now.Text, 11.5, agent.State == AgentActivityState.Working ? text : muted, maxWidth: width - 34), new Point(x + 32, y + 36));
    }

    private static Rect Place(Rect bounds, Rect anchor, double width, double height)
    {
        var x = anchor.Right + 12 + width <= bounds.Width ? anchor.Right + 12 : anchor.Left - 12 - width;
        if (x < 12) x = Math.Clamp(anchor.Left + anchor.Width / 2 - width / 2, 12, Math.Max(12, bounds.Width - width - 12));
        var y = Math.Clamp(anchor.Top, 12, Math.Max(12, bounds.Height - height - 12));
        if (x + width > anchor.Left && x < anchor.Right)
            y = anchor.Top - height - 12 >= 12 ? anchor.Top - height - 12 : Math.Min(bounds.Height - height - 12, anchor.Bottom + 12);
        return new Rect(Math.Round(x), Math.Round(y), width, height);
    }

    private void DrawHud(DrawingContext dc)
    {
        var label = Text(Sky.Label + "  ·  " + DateTime.Now.ToString("HH:mm"), 12.5, Color.FromRgb(0xF2, 0xF4, 0xF8), bold: true);
        var box = new Rect(14, 14, label.Width + 24, 28);
        dc.DrawRoundedRectangle(Fill(Color.FromArgb(150, 10, 12, 20)), null, box, 8, 8);
        dc.DrawText(label, new Point(26, 19));
        DrawGuildLegend(dc, new Point(box.Right + 8, 14));
    }

    // ---- geometry -------------------------------------------------------------------------------------------------

    /// <summary>Floor units to this element's DIPs, snapped to whole DIPs so neighbouring pixels meet.</summary>
    private Rect R(double x, double y, double w, double h)
    {
        var left = Math.Round(_origin.X + x * _p);
        var top = Math.Round(_origin.Y + y * _p);
        return new Rect(left, top, Math.Max(1, Math.Round(_origin.X + (x + w) * _p) - left), Math.Max(1, Math.Round(_origin.Y + (y + h) * _p) - top));
    }

    private Rect R(Rect area) => R(area.X, area.Y, area.Width, area.Height);
    private Point Dip(Point at) => new(Math.Round(_origin.X + at.X * _p), Math.Round(_origin.Y + at.Y * _p));
    private void Box(DrawingContext dc, Color color, double x, double y, double w, double h) => dc.DrawRectangle(Fill(color), null, R(x, y, w, h));

    private static DrawingGroup Bake(Action<DrawingContext> draw)
    {
        var group = new DrawingGroup();
        using (var dc = group.Open()) draw(dc);
        group.Freeze();
        return group;
    }

    private static Rect Inflate(Rect rect, double x, double y)
    {
        rect.Inflate(x, y);
        return rect;
    }

    private static Rect Offset(Rect rect, double x, double y)
    {
        rect.Offset(x, y);
        return rect;
    }

    private static double Bounce(double t, double range)
    {
        var m = t % 2;
        return (m < 1 ? m : 2 - m) * Math.Max(0, range);
    }
}
