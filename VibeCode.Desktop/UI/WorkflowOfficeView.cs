using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;

namespace VibeCode.UI;

/// <summary>
/// The pixel office, after the pixel-art agent offices (Pixel Agents, PixelHQ, Clawffice): a cutaway tower where every
/// project is a room and every agent has a desk. A working agent sits typing with code scrolling on its monitor and
/// says what it is doing in a speech bubble; one that needs you stands and waves; a failed one sits before a red
/// screen; a stopped one naps on the desk; an idle one wanders to the coffee machine, and a finished one relaxes in
/// the lounge with a mug. Subagents are interns running between the desk and the lounge. Orchestrators wear a crown,
/// and each room's whiteboard shows who needs you, what failed, every todo list and what got done (see .Board.cs).
///
/// Outside, the same sky and weather as the tavern pass the windows. Hover a room or a desk for the details; click
/// a room to filter the wall, a desk to open the chat.
/// </summary>
public sealed partial class WorkflowOfficeView : WorkflowScene
{
    private sealed class Room
    {
        public required AgentActivityProject Project;
        public Rect Bounds, Window, Lounge, Board;
        public double Floor;
        public Color Paint;
        /// <summary>How furnished the room is (see <see cref="WorkflowTown.RoomTier"/>), as of the cached scenery.</summary>
        public int Tier = WorkflowTown.StandardTier;
        public readonly List<(string Id, Rect Desk)> Desks = new();
    }

    private const int MaxDesks = 8;
    private readonly List<Room> _rooms = new();
    private Rect _tower;
    /// <summary>The sun or moon in the sky behind the tower, this frame; every window shows that one.</summary>
    private Point _orb;
    private double _px = 3;
    private string _layoutKey = "";
    private DrawingGroup? _static;
    private string _staticKey = "";
    private static readonly Color[] Paints =
    [
        PixelArt.C("#34465C"), PixelArt.C("#46395C"), PixelArt.C("#2F5248"), PixelArt.C("#5C4636"), PixelArt.C("#3C4F6E"),
        PixelArt.C("#5A3C4A"), PixelArt.C("#45553A"),
    ];

    public string SkyMode { get; set; } = "auto";
    public string WeatherMode { get; set; } = "auto";
    public SceneSky Sky { get; private set; }
    public int RoomCount => _rooms.Count;

    public WorkflowOfficeView()
    {
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        AutomationProperties.SetName(this, "Pixel office: projects are rooms, agents sit at desks");
    }

    protected override void Render(DrawingContext dc, Rect bounds)
    {
        Sky = SceneSky.Resolve(SkyMode, WeatherMode, DateTime.Now);
        Layout(bounds);
        var time = Time;
        var nowUtc = DateTime.UtcNow;
        var agentHover = HoveredAgent;
        var roomHover = agentHover is null ? HoveredProject : null;

        SceneWeatherFx.DrawSky(dc, bounds.Width, bounds.Height - 26, Sky, time, Fill, Stroke);
        _orb = SceneWeatherFx.OrbAt(Sky, bounds.Width, bounds.Height - 26);
        var key = $"{_layoutKey}|{Sky.Darkness:0.00}|{WorkflowTown.Instance.Stamp}";
        if (_static is null || key != _staticKey) { _static = BuildStatic(bounds); _staticKey = key; }
        SceneWeatherFx.Draw(dc, bounds, Sky, time, bounds.Bottom - 26, Fill, Stroke);
        dc.DrawDrawing(_static);

        foreach (var room in _rooms) Hit(room.Bounds, room.Project);
        foreach (var room in _rooms) HitCertificates(room);
        foreach (var room in _rooms) HitBoard(room);
        foreach (var room in _rooms) DrawRoom(dc, room, time, nowUtc, roomHover);
        DrawRadio(dc, time);
        DrawConfetti(dc);
        var flash = SceneWeatherFx.Lightning(Sky, time, out _);
        if (flash > 0) dc.DrawRectangle(Fill(Color.FromArgb((byte)(flash * 50), 0xDD, 0xE6, 0xFF)), null, bounds);

        if (agentHover is not null) DrawAgentCard(dc, bounds, agentHover, nowUtc);
        else if (HoveredBoard is { } board) DrawBoardCard(dc, bounds, board, nowUtc);
        else if (HoveredPlaque is { } plaque && RoomOf(plaque.Key) is { } wall)
        {
            var (bg, border, text, muted) = CardColors();
            DrawImprovementsCard(dc, bounds, Certificates(wall).Area, plaque, WorkflowTown.Instance.For(plaque.Key), bg, border, text, muted);
        }
        else if (roomHover is not null && _rooms.FirstOrDefault(r => r.Project.Key == roomHover.Key) is { } focus)
        {
            dc.DrawRectangle(null, Stroke(Ink("Accent", Color.FromRgb(0x7C, 0xB7, 0xFF)), 3), Inflate(focus.Bounds, 1, 1));
            DrawRoomCard(dc, bounds, focus, nowUtc);
        }
        DrawHud(dc);
    }

    // ---- layout -------------------------------------------------------------------------------------------------

    private void Layout(Rect bounds)
    {
        var key = $"{bounds.Width:0}x{bounds.Height:0}|" + string.Join(";", Projects.Select(p =>
            p.Key + ":" + string.Join(",", p.Agents.Take(MaxDesks).Select(a => a.Id)) + ":" + p.Agents.Count));
        if (key == _layoutKey)
        {
            foreach (var room in _rooms) room.Project = Projects.FirstOrDefault(p => p.Key == room.Project.Key) ?? room.Project;
            return;
        }
        _layoutKey = key;
        _rooms.Clear();
        _tower = new Rect(Math.Round(bounds.Width * 0.04), 64, Math.Round(bounds.Width * 0.92), Math.Round(bounds.Height - 64 - 30));
        if (Projects.Count == 0) { _px = 3; return; }
        var columns = Projects.Count <= 2 ? Projects.Count : Projects.Count <= 4 ? 2 : Projects.Count <= 9 ? 3 : 4;
        var rows = (int)Math.Ceiling(Projects.Count / (double)columns);
        const double Wall = 8;
        var cellW = (_tower.Width - Wall * (columns + 1)) / columns;
        // One art scale for the whole tower, as large as the most crowded room allows; a room is then only as tall
        // as its furniture and bubbles need, so the tower does not fill with bare wall.
        var busiest = Projects.Max(p => Math.Min(MaxDesks, Math.Max(1, p.Agents.Count)));
        var fit = Math.Min(cellW / (busiest * 36 + 52), (_tower.Height - Wall * (rows + 1)) / rows / 64);
        _px = Math.Clamp(Math.Floor(fit * 2) / 2, 1.5, 4);
        var cellH = Math.Min((_tower.Height - Wall * (rows + 1)) / rows, Math.Max(64 * _px, 230));
        // Shrink the shell to the rooms it actually holds, standing on the street.
        var towerH = rows * cellH + Wall * (rows + 1);
        _tower = new Rect(_tower.Left, Math.Round(bounds.Height - 30 - towerH), _tower.Width, Math.Round(towerH));
        for (var i = 0; i < Projects.Count; i++)
        {
            var project = Projects[i];
            var row = i / columns; var col = i % columns;
            // A short last floor stretches its rooms across the tower instead of leaving an empty bay.
            var inRow = Math.Min(columns, Projects.Count - row * columns);
            var width = (_tower.Width - Wall * (inRow + 1)) / inRow;
            var cell = new Rect(Math.Round(_tower.Left + Wall + col * (width + Wall)), Math.Round(_tower.Top + Wall + row * (cellH + Wall)),
                Math.Round(width), Math.Round(cellH));
            var p = _px;
            var room = new Room
            {
                Project = project, Bounds = cell, Floor = Math.Round(cell.Bottom - 5 * p),
                Paint = Paints[(int)(Hash(project.Key, 3) * Paints.Length)],
            };
            room.Lounge = new Rect(cell.Right - 46 * p, cell.Top, 46 * p, cell.Height);
            room.Window = new Rect(Math.Round(room.Lounge.Left + 8 * p), Math.Round(cell.Top + 16 * p), Math.Round(30 * p), Math.Round(Math.Min(22 * p, cell.Height * 0.3)));
            var shown = Math.Min(MaxDesks, project.Agents.Count);
            var slot = 36 * p;
            var start = cell.Left + 8 * p;
            for (var d = 0; d < shown; d++)
                room.Desks.Add((project.Agents[d].Id, new Rect(Math.Round(start + d * slot), Math.Round(room.Floor - 13 * p), Math.Round(30 * p), Math.Round(13 * p))));
            room.Board = BoardRect(room);
            _rooms.Add(room);
        }
    }

    // ---- the building, cached until the layout or the light changes -------------------------------------------

    private DrawingGroup BuildStatic(Rect bounds)
    {
        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            // A city skyline behind the tower, windows lit after dark.
            var night = Sky.Darkness;
            for (var i = 0; i < 16; i++)
            {
                var w = 40 + SceneWeatherFx.Noise(i * 7 + 1) * 70;
                var x = i * bounds.Width / 15 - 30 + SceneWeatherFx.Noise(i * 7 + 2) * 20;
                var h = 90 + SceneWeatherFx.Noise(i * 7 + 3) * bounds.Height * 0.45;
                var rect = new Rect(Math.Round(x), Math.Round(bounds.Height - 26 - h), Math.Round(w), Math.Round(h));
                dc.DrawRectangle(Fill(PixelArt.Night(PixelArt.C("#5E6E86"), 0.35 + night)), null, rect);
                if (night > 0.3)
                    for (var wy = rect.Top + 8; wy < rect.Bottom - 8; wy += 12)
                        for (var wx = rect.Left + 6; wx < rect.Right - 8; wx += 10)
                            if (SceneWeatherFx.Noise((int)(wx * 31 + wy * 17)) > 0.72)
                                dc.DrawRectangle(Fill(Color.FromArgb(200, 0xFF, 0xD9, 0x8A)), null, new Rect(Math.Round(wx), Math.Round(wy), 4, 5));
            }
            dc.DrawRectangle(Fill(PixelArt.Night(PixelArt.C("#4A4A52"), night * 0.8)), null, new Rect(0, bounds.Height - 26, bounds.Width, 26));
            dc.DrawRectangle(Fill(PixelArt.Night(PixelArt.C("#6A6A72"), night * 0.8)), null, new Rect(0, bounds.Height - 26, bounds.Width, 4));
            // No rooms means an empty street. Freezing inside this block crashed the first frame: the context closes
            // the group as the block ends, and a frozen group cannot be closed.
            if (_rooms.Count > 0)
            {
                // The shell: concrete, a roof slab, an antenna.
                var shell = PixelArt.Night(PixelArt.C("#3A3F4A"), night * 0.5);
                dc.DrawRectangle(Fill(shell), null, _tower);
                dc.DrawRectangle(Fill(PixelArt.Shade(shell, 0.75)), null, new Rect(_tower.Left - 8, _tower.Top - 12, _tower.Width + 16, 14));
                dc.DrawRectangle(Fill(PixelArt.Shade(shell, 0.75)), null, new Rect(_tower.Left + _tower.Width * 0.8, _tower.Top - 52, 4, 40));
                foreach (var room in _rooms) DrawRoomStatic(dc, room);
            }
        }
        group.Freeze();
        return group;
    }

    private void DrawRoomStatic(DrawingContext dc, Room room)
    {
        var p = _px;
        var cell = room.Bounds;
        // How furnished the room is follows its directory's tokens (WorkflowTown); tier 2 is the standard office.
        var tier = room.Tier = WorkflowTown.Instance.RoomTier(room.Project.Key);
        var paint = WallPaint(room.Paint, tier);
        DrawShell(dc, room, paint, tier);
        DrawCeilingLights(dc, room, tier);
        DrawLounge(dc, room, paint, tier);
        DrawRoomDecor(dc, room, tier);
        // Chairs, each as good as its own agent's desk; monitors, desks and people are drawn live.
        foreach (var (id, desk) in room.Desks)
            DrawChair(dc, desk, room.Project.Agents.FirstOrDefault(a => a.Id == id) is { } seated ? DeskTier(seated) : WorkflowTown.StandardTier);
        // A wall clock and a framed poster over the desks.
        var clock = new Point(Math.Round(cell.Left + cell.Width * 0.42), Math.Round(cell.Top + 16 * p));
        dc.DrawEllipse(Fill(PixelArt.C("#E8E4D0")), Stroke(tier == WorkflowTown.TopTier ? Gold : PixelArt.C("#2A2A30"), Math.Max(1, p)), clock, 6 * p, 6 * p);
        var poster = new Rect(Math.Round(cell.Left + cell.Width * 0.25), Math.Round(cell.Top + 12 * p), 14 * p, 18 * p);
        if (room.Desks.Count == 0 || poster.Right < (room.Board.IsEmpty ? room.Lounge.Left : room.Board.Left) - 4 * p)
            DrawPoster(dc, room, paint, tier, poster);
        // Whiteboard frame.
        if (!room.Board.IsEmpty)
        {
            dc.DrawRectangle(Fill(PixelArt.C("#9AA0A8")), null, Inflate(room.Board, p, p));
            dc.DrawRectangle(Fill(PixelArt.C("#F4F6F8")), null, room.Board);
        }
        // Window frame on the lounge wall.
        dc.DrawRectangle(Fill(PixelArt.C("#C8CCD4")), null, Inflate(room.Window, 2 * p, 2 * p));
        DrawCertificates(dc, room);
    }

    // ---- live -----------------------------------------------------------------------------------------------------

    private void DrawRoom(DrawingContext dc, Room room, double time, DateTime nowUtc, AgentActivityProject? hovered)
    {
        var p = _px;
        DrawWindowView(dc, room, time);
        if (room.Tier == 0) DrawCrackedPane(dc, room);
        var face = new Point(Math.Round(room.Bounds.Left + room.Bounds.Width * 0.42), Math.Round(room.Bounds.Top + 16 * p));
        var now = DateTime.Now;
        var hand = Stroke(PixelArt.C("#2A2A30"), Math.Max(1, p * 0.7));
        var hourAngle = (now.Hour % 12 + now.Minute / 60.0) / 12 * Math.PI * 2;
        var minuteAngle = now.Minute / 60.0 * Math.PI * 2;
        dc.DrawLine(hand, face, new Point(face.X + Math.Sin(hourAngle) * 3 * p, face.Y - Math.Cos(hourAngle) * 3 * p));
        dc.DrawLine(hand, face, new Point(face.X + Math.Sin(minuteAngle) * 4.6 * p, face.Y - Math.Cos(minuteAngle) * 4.6 * p));
        // Nameplate: project name and who is in.
        var plate = Text(room.Project.Name, Math.Clamp(5 * p, 11, 16), Color.FromRgb(0xF2, 0xF4, 0xF8), bold: true, maxWidth: room.Bounds.Width * 0.5);
        var counts = Text(room.Project.Working > 0 ? $"{room.Project.Working} working · {room.Project.Agents.Count} agents"
            : room.Project.Agents.Count == 1 ? "1 agent" : $"{room.Project.Agents.Count} agents", Math.Clamp(3.8 * p, 10, 12.5),
            room.Project.Working > 0 ? Color.FromRgb(0xA8, 0xF0, 0xBE) : Color.FromRgb(0xC8, 0xCE, 0xDA));
        var plateBox = new Rect(room.Bounds.Left + 8, room.Bounds.Top + 8, Math.Max(plate.Width, counts.Width) + 16, plate.Height + counts.Height + 8);
        dc.DrawRectangle(Fill(Color.FromArgb(200, 0x16, 0x1A, 0x22)), null, plateBox);
        dc.DrawText(plate, new Point(plateBox.Left + 8, plateBox.Top + 3));
        dc.DrawText(counts, new Point(plateBox.Left + 8, plateBox.Top + 4 + plate.Height));
        DrawWhiteboard(dc, room, time, nowUtc);

        var people = new List<(double Z, Action Draw)>();
        for (var d = 0; d < room.Desks.Count; d++)
        {
            var (id, desk) = room.Desks[d];
            var agent = room.Project.Agents.FirstOrDefault(a => a.Id == id);
            if (agent is null) continue;
            var mine = hovered?.Key == room.Project.Key;
            var index = d;
            people.Add((desk.Top, () => DrawDesk(dc, room, agent, desk, index, time, nowUtc, mine)));
        }
        foreach (var (_, draw) in people) draw();
        var more = room.Project.Agents.Count - room.Desks.Count;
        if (more > 0)
        {
            var label = Text($"+{more} more", 11, Color.FromRgb(0xE8, 0xEC, 0xF2), bold: true);
            dc.DrawText(label, new Point(room.Lounge.Left - label.Width - 8, room.Bounds.Top + 10));
        }
    }

    private void DrawWindowView(DrawingContext dc, Room room, double time)
    {
        var w = room.Window;
        var sky = new LinearGradientBrush(Sky.Top, Sky.Bottom, 90);
        sky.Freeze();
        dc.PushClip(new RectangleGeometry(w));
        dc.DrawRectangle(sky, null, w);
        // The windows look out on the sky behind the tower, so the sun or moon is that sky's own and shows only in a
        // window in front of it. Placed per window, every room had one in the same corner of its glass.
        SceneWeatherFx.DrawOrb(dc, _orb, Sky, Fill);
        var variant = _rooms.IndexOf(room) + 1;   // each window its own stars and drops
        if (Sky.Night && Sky.Weather is SceneWeather.Clear or SceneWeather.Cloudy)
            for (var i = 0; i < 6; i++)
                dc.DrawRectangle(Fill(Color.FromArgb(200, 0xF4, 0xF1, 0xDC)), null,
                    new Rect(w.Left + SceneWeatherFx.Noise(i + 40 + variant * 97) * w.Width,
                        w.Top + SceneWeatherFx.Noise(i + 50 + variant * 97) * w.Height * 0.7, 2, 2));
        SceneWeatherFx.Draw(dc, w, Sky, time, w.Bottom, Fill, Stroke, variant);
        dc.Pop();
        var bar = Fill(PixelArt.C("#C8CCD4"));
        dc.DrawRectangle(bar, null, new Rect(w.Left + w.Width / 2 - _px / 2, w.Top, _px, w.Height));
        dc.DrawRectangle(bar, null, new Rect(w.Left, w.Top + w.Height / 2 - _px / 2, w.Width, _px));
    }

    private void DrawDesk(DrawingContext dc, Room room, AgentActivityNode agent, Rect desk, int index, double time, DateTime nowUtc, bool mine)
    {
        var p = _px;
        var focused = Hovered(agent);
        var h = Hash(agent.Id);
        var top = desk.Top;
        var seat = new Point(desk.Left + 17 * p, top - 9 * p);
        // Desk, computer and chair are as good as this agent's own chat has earned (WorkflowTown).
        var tier = DeskTier(agent);
        var computerTop = ComputerTop(desk, tier);
        var standing = new Point(desk.Right + p, room.Floor - PixelArt.Height * p);
        Hit(new Rect(desk.Left - 2 * p, computerTop - 4 * p, desk.Width + 13 * p, room.Floor - computerTop + 4 * p), agent);
        if (focused)
            dc.DrawRectangle(Fill(Color.FromArgb(56, 0xF5, 0xCB, 0x5C)), Stroke(Color.FromRgb(0xF5, 0xCB, 0x5C), 1.5),
                new Rect(desk.Left - 3 * p, computerTop - 5 * p, desk.Width + 15 * p, room.Floor - computerTop + 5 * p));

        // Who sits where, by state.
        string? bubble = null; var bubbleInk = Color.FromRgb(0x2A, 0x32, 0x40); var bubbleEdge = Colors.White;
        var person = PixelArt.Pose.Sit; var at = seat; var flip = false; var atDesk = true;
        switch (agent.State)
        {
            case AgentActivityState.Working:
                at = new Point(seat.X, seat.Y + Math.Round(Math.Abs(Math.Sin(time * 9 + h * 20)) * 0.6 * p));
                bubble = (agent.Now ?? WorkflowAgentCard.Resting(agent, nowUtc)).Text;
                break;
            case AgentActivityState.Waiting:
                atDesk = false; person = (time * 1.5 + h) % 1 < 0.5 ? PixelArt.Pose.Wave : PixelArt.Pose.Stand; at = standing;
                bubble = agent.Status; bubbleEdge = Color.FromRgb(0xF2, 0xB8, 0x4B);
                break;
            case AgentActivityState.Error:
                bubble = "Failed: " + (agent.Last?.Failed == true ? agent.Last.Text : agent.Status); bubbleEdge = Color.FromRgb(0xF2, 0x6D, 0x6D);
                break;
            case AgentActivityState.Paused:
                person = PixelArt.Pose.Sleep; at = new Point(seat.X, seat.Y + 2 * p);
                break;
            default:
                atDesk = false;
                var lounge = room.Lounge.Left + 4 * p + (index % 3) * 10 * p;
                if (agent.Status == "Completed") { person = PixelArt.Pose.Mug; at = new Point(lounge, room.Floor - PixelArt.Height * p); flip = index % 2 == 1; }
                else
                {
                    // Desk, coffee, desk: a slow round trip with a pause at each end.
                    var cycle = 16 + h * 10;
                    var phase = (time / cycle + h) % 1;
                    double t; bool moving;
                    if (phase < 0.35) { t = Smooth(phase / 0.35); moving = true; flip = false; }
                    else if (phase < 0.55) { t = 1; moving = false; }
                    else if (phase < 0.9) { t = 1 - Smooth((phase - 0.55) / 0.35); moving = true; flip = true; }
                    else { t = 0; moving = false; }
                    var x = desk.Right + p + (lounge - desk.Right - p) * t;
                    at = new Point(x, room.Floor - PixelArt.Height * p);
                    person = moving && Math.Floor(time * 6 + h * 3) % 2 == 0 ? PixelArt.Pose.Walk : PixelArt.Pose.Stand;
                }
                break;
        }

        // Computer, then the person behind the desk, then the desk in front of their legs.
        DrawComputer(dc, desk, agent, time, h, tier);
        if (atDesk) Person(dc, agent, person, at, flip, mine, focused);
        DrawDeskFront(dc, desk, tier, time, h);
        if (!atDesk) Person(dc, agent, person, at, flip, mine, focused);
        if (agent.Chat.IsBridgeManager || agent.IsCentralPlanner) Crown(dc, new Point(at.X + 4.5 * p, at.Y - p), p);
        if (person == PixelArt.Pose.Sleep) Snore(dc, new Point(at.X + 8 * p, at.Y), time);
        // Interns for subagents, scurrying to the lounge and back.
        for (var i = 0; i < Math.Min(3, agent.ActiveChildren); i++)
        {
            var small = Math.Max(1, p * 0.6);
            var t = (time * 0.5 + i * 0.33 + h) % 1;
            var along = t < 0.5 ? t * 2 : (1 - t) * 2;
            var x = desk.Right + 2 * p + along * Math.Max(10, room.Lounge.Left - desk.Right - 10 * p);
            var sprite = PixelArt.Character(agent.Id + ":intern" + i, Math.Floor(time * 10) % 2 == 0 ? PixelArt.Pose.Walk : PixelArt.Pose.Stand, small, flip: t >= 0.5,
                shirt: WorkflowGuilds.ShirtOf(agent));
            dc.PushTransform(new TranslateTransform(Math.Round(x), Math.Round(room.Floor - PixelArt.Height * small)));
            dc.DrawDrawing(sprite);
            dc.Pop();
        }
        // Name under the desk, and what they are saying above it.
        var tag = Text(agent.Label, Math.Clamp(3.6 * p, 9.5, 12), Color.FromRgb(0xF2, 0xF4, 0xF8), bold: focused, maxWidth: desk.Width + 10 * p);
        var tagAt = new Point(Math.Round(desk.Left + desk.Width / 2 - tag.Width / 2), Math.Round(room.Floor + 1));
        if (tagAt.Y + tag.Height <= room.Bounds.Bottom + 2) dc.DrawText(tag, tagAt);
        if (bubble is not null) Bubble(dc, room, new Point(at.X + 4.5 * p, at.Y - (agent.Chat.IsBridgeManager || agent.IsCentralPlanner ? 4 : 2) * p),
            bubble, bubbleInk, bubbleEdge, index, desk.Width + 12 * p);
    }

    private void DrawScreen(DrawingContext dc, Rect screen, AgentActivityNode agent, double time, double h)
    {
        var p = _px;
        switch (agent.State)
        {
            case AgentActivityState.Working:
                dc.DrawRectangle(Fill(PixelArt.C("#0E1A24")), null, screen);
                dc.PushClip(new RectangleGeometry(screen));
                var scroll = (time * 6 * p + h * 100) % (2 * p);
                var line = 0;
                for (var y = screen.Top + p - scroll; y < screen.Bottom; y += 2 * p, line++)
                {
                    var seed = (int)Math.Floor((time * 3 + h * 50)) + line;
                    var indent = (SceneWeatherFx.Noise(seed * 3) * 3) * p;
                    var length = (2 + SceneWeatherFx.Noise(seed * 3 + 1) * 7) * p;
                    var ink = SceneWeatherFx.Noise(seed * 3 + 2) switch { < 0.3 => "#7FD1FF", < 0.55 => "#A8F0BE", < 0.75 => "#F5CB5C", _ => "#E8ECF2" };
                    dc.DrawRectangle(Fill(PixelArt.C(ink)), null, new Rect(screen.Left + p + indent, y, Math.Min(length, screen.Width - 2 * p - indent), p));
                }
                dc.Pop();
                break;
            case AgentActivityState.Waiting:
                dc.DrawRectangle(Fill(PixelArt.C("#2A2A1E")), null, screen);
                dc.DrawRectangle(Fill(PixelArt.C("#F2B84B")), null, new Rect(screen.Left + 3 * p, screen.Top + 2 * p, screen.Width - 6 * p, screen.Height - 4 * p));
                dc.DrawRectangle(Fill(PixelArt.C("#2A2A1E")), null, new Rect(screen.Left + screen.Width / 2 - p / 2, screen.Top + 3 * p, p, 2 * p));
                break;
            case AgentActivityState.Error:
                dc.DrawRectangle(Fill(PixelArt.C("#B83A3A")), null, screen);
                dc.DrawRectangle(Fill(Colors.White), null, new Rect(screen.Left + screen.Width / 2 - p / 2, screen.Top + p, p, 3 * p));
                dc.DrawRectangle(Fill(Colors.White), null, new Rect(screen.Left + screen.Width / 2 - p / 2, screen.Top + 5 * p, p, p));
                break;
            case AgentActivityState.Paused:
                dc.DrawRectangle(Fill(PixelArt.C("#0A0C10")), null, screen);
                break;
            default:
                // A screensaver: one dot bouncing round the screen; a tick once the work is done.
                dc.DrawRectangle(Fill(PixelArt.C("#141A26")), null, screen);
                if (agent.Status == "Completed")
                {
                    var g = Fill(PixelArt.C("#5FD38D"));
                    dc.DrawRectangle(g, null, new Rect(screen.Left + 3 * p, screen.Top + 4 * p, p, p));
                    dc.DrawRectangle(g, null, new Rect(screen.Left + 4 * p, screen.Top + 5 * p, p, p));
                    dc.DrawRectangle(g, null, new Rect(screen.Left + 5 * p, screen.Top + 4 * p, p, p));
                    dc.DrawRectangle(g, null, new Rect(screen.Left + 6 * p, screen.Top + 3 * p, p, p));
                    dc.DrawRectangle(g, null, new Rect(screen.Left + 7 * p, screen.Top + 2 * p, p, p));
                }
                else
                {
                    var bx = Bounce(time * 3 + h * 10, screen.Width - p);
                    var by = Bounce(time * 2.2 + h * 7, screen.Height - p);
                    dc.DrawRectangle(Fill(PixelArt.C("#7C8CF0")), null, new Rect(Math.Round(screen.Left + bx), Math.Round(screen.Top + by), p, p));
                }
                break;
        }
    }

    private void Person(DrawingContext dc, AgentActivityNode agent, PixelArt.Pose pose, Point at, bool flip, bool mine, bool focused)
    {
        var sprite = PixelArt.Character(agent.Id, pose, _px, flip, shirt: WorkflowGuilds.Shirt(agent), prop: WorkflowGuilds.PropOf(agent));
        dc.PushTransform(new TranslateTransform(Math.Round(at.X), Math.Round(at.Y)));
        dc.DrawDrawing(sprite);
        dc.Pop();
        var body = new Rect(at.X, at.Y, PixelArt.Width * _px, PixelArt.Height * _px);
        Hit(Inflate(body, 2, 2), agent);
        if (focused) dc.DrawRectangle(null, Stroke(Color.FromRgb(0xF5, 0xCB, 0x5C), 1.5), Inflate(body, 2, 2));
    }

    private void Bubble(DrawingContext dc, Room room, Point anchor, string text, Color ink, Color edge, int index, double maxWidth)
    {
        var size = Math.Clamp(3.6 * _px, 10, 12.5);
        var label = Text(text, size, ink, maxWidth: Math.Max(60, maxWidth));
        // Alternate heights so neighbouring bubbles do not sit on one another.
        var lift = index % 2 == 0 ? 0 : label.Height + 8;
        var box = new Rect(Math.Round(anchor.X - label.Width / 2 - 6), Math.Round(anchor.Y - label.Height - 12 - lift), label.Width + 12, label.Height + 6);
        box.X = Math.Clamp(box.X, room.Bounds.Left + 2, Math.Max(room.Bounds.Left + 2, room.Bounds.Right - box.Width - 2));
        if (box.Top < room.Bounds.Top + 2) box.Y = room.Bounds.Top + 2;
        dc.DrawLine(Stroke(edge, 1.5), new Point(anchor.X, box.Bottom), new Point(anchor.X, anchor.Y - 2));
        dc.DrawRoundedRectangle(Fill(Color.FromRgb(0xFB, 0xFC, 0xFE)), Stroke(edge, 1.5), box, 4, 4);
        dc.DrawText(label, new Point(box.Left + 6, box.Top + 3));
    }

    private void Crown(DrawingContext dc, Point top, double p)
    {
        var gold = Fill(Color.FromRgb(0xF5, 0xCB, 0x5C));
        dc.DrawRectangle(gold, null, new Rect(Math.Round(top.X - 3 * p), Math.Round(top.Y - p), 7 * p, p));
        for (var i = -1; i <= 1; i++) dc.DrawRectangle(gold, null, new Rect(Math.Round(top.X + i * 3 * p), Math.Round(top.Y - 2 * p), p, p));
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

    // ---- cards ------------------------------------------------------------------------------------------------

    private void DrawRoomCard(DrawingContext dc, Rect bounds, Room room, DateTime nowUtc)
    {
        var project = room.Project;
        var rows = project.Agents.Take(6).ToList();
        var width = Math.Min(380, bounds.Width - 24);
        // Where the room stands: its look, its directory's tokens and what the next look takes.
        var office = WorkflowTown.Enabled && project.Key.Length > 0
            ? "Office: " + WorkflowTown.Progress(WorkflowTown.RoomTierName(room.Tier), WorkflowTown.Instance.ProjectTokens(project.Key), WorkflowTown.RoomTierTokens)
            : null;
        var height = 70 + rows.Count * 60 + (project.Agents.Count > rows.Count ? 18 : 0) + 22 + (office is null ? 0 : 18);
        var card = Place(bounds, room.Bounds, width, height);
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
        if (office is not null)
        {
            dc.DrawText(Text(office, 11, muted, maxWidth: inner), new Point(x, y - 4));
            y += 18;
        }
        dc.DrawRectangle(Fill(border), null, new Rect(x, y, inner, 1));
        y += 8;
        foreach (var agent in rows) { AgentLines(dc, agent, x, y, inner, nowUtc, text, muted); y += 60; }
        if (project.Agents.Count > rows.Count) { dc.DrawText(Text($"+{project.Agents.Count - rows.Count} more", 11, muted), new Point(x, y)); y += 18; }
        dc.DrawText(Text("Click the room to filter · click a desk to open its chat", 10.5, muted, maxWidth: inner), new Point(x, y + 2));
    }

    private void DrawAgentCard(DrawingContext dc, Rect bounds, AgentActivityNode agent, DateTime nowUtc)
    {
        var anchor = Targets.LastOrDefault(t => t.Key == AgentKey(agent)).Bounds;
        if (anchor.Width <= 0) anchor = new Rect(bounds.Width / 2, bounds.Height / 2, 1, 1);
        var (bg, border, text, muted) = CardColors();
        // Where its desk stands: its look, its own chat's tokens and what the next look takes.
        var desk = WorkflowTown.Enabled
            ? "Desk: " + WorkflowTown.Progress(WorkflowTown.DeskTierName(DeskTier(agent)), WorkflowTown.Instance.SessionTokens(agent.Chat.SessionId), WorkflowTown.DeskTierTokens)
            : null;
        var card = Place(bounds, anchor, Math.Min(350, bounds.Width - 24), desk is null ? 110 : 128);
        dc.DrawRoundedRectangle(Fill(Color.FromArgb(100, 0, 0, 0)), null, new Rect(card.Left + 3, card.Top + 5, card.Width, card.Height), 10, 10);
        dc.DrawRoundedRectangle(Fill(bg), Stroke(border, 1), card, 10, 10);
        AgentLines(dc, agent, card.Left + 16, card.Top + 12, card.Width - 32, nowUtc, text, muted);
        var model = agent.Account.Length > 0 ? agent.Model + " · " + agent.Account : agent.Model;
        dc.DrawText(Text(model + "  ·  " + agent.ProjectName, 11, muted, maxWidth: card.Width - 32), new Point(card.Left + 16, card.Top + 72));
        if (desk is not null) dc.DrawText(Text(desk, 11, muted, maxWidth: card.Width - 32), new Point(card.Left + 16, card.Top + 89));
        dc.DrawText(Text("Click to open the chat", 10.5, muted), new Point(card.Left + 16, card.Top + (desk is null ? 89 : 107)));
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

    private static Rect Inflate(Rect rect, double x, double y)
    {
        rect.Inflate(x, y);
        return rect;
    }

    private static double Smooth(double t) => t * t * (3 - 2 * t);

    private static double Bounce(double t, double range)
    {
        var m = t % 2;
        return (m < 1 ? m : 2 - m) * Math.Max(0, range);
    }
}
