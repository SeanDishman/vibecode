using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;

namespace VibeCode.UI;

/// <summary>
/// The tavern town. Every project is a building on one cobbled street and every agent is a villager: a lit window
/// while it works, a figure in the street while it rests, waves at the door when it needs you, stands under a red
/// mark when it failed, dozes on the bench when stopped, and heads to the tavern for a mug once its work is done.
/// Chimneys smoke while anyone inside is working, and subagents run errands in front of their building.
///
/// The sky follows the local clock - day, dusk, night with stars and a moon - and the weather follows the Weather
/// extension's live conditions when it has them, otherwise drifting between clear, cloud, rain and storms on its own.
/// Both can be pinned from the wall. There is no sound.
///
/// Hovering a building lifts it out of the street, rings everyone who belongs to it - inside or out - and opens a
/// card of who is in there and what each of them is doing right now. A click on a building filters the wall to it;
/// a click on a window or villager opens that agent's chat.
/// </summary>
public sealed partial class WorkflowTavernView : WorkflowScene
{
    private sealed class Building
    {
        public required AgentActivityProject Project;
        public Rect Body, Door, Chimney;
        public Point RoofPeak;
        public double Scale, Overhang;
        public int Style, Columns;
        public readonly List<(string Id, Rect Window)> Windows = new();
        public Rect Outline => new(Body.Left - Overhang, RoofPeak.Y, Body.Width + Overhang * 2, Body.Bottom - RoofPeak.Y);
    }

    private enum Look { Tavern, Stone, Brick, Forge, Hall, Shed }

    private readonly List<Building> _buildings = new();
    private string _layoutKey = "";
    private DrawingGroup? _static;
    private string _staticKey = "";
    private double _ground;
    private double _scale = 1;
    private static readonly RadialGradientBrush Glow = MakeGlow(Color.FromRgb(0xFF, 0xC8, 0x6A));
    private static readonly RadialGradientBrush LampGlow = MakeGlow(Color.FromRgb(0xFF, 0xDD, 0x8C));
    private static readonly RadialGradientBrush ForgeGlow = MakeGlow(Color.FromRgb(0xFF, 0x7A, 0x2E));

    /// <summary>"auto" follows the clock; "dawn", "day", "dusk" or "night" pins it.</summary>
    public string SkyMode { get; set; } = "auto";
    /// <summary>"auto" follows the Weather extension or drifts; otherwise one of clear, cloudy, rain, storm, snow, fog.</summary>
    public string WeatherMode { get; set; } = "auto";
    public SceneSky Sky { get; private set; }
    public int BuildingCount => _buildings.Count;

    public WorkflowTavernView()
    {
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        AutomationProperties.SetName(this, "Tavern town: projects are buildings, agents are villagers");
    }

    protected override void Render(DrawingContext dc, Rect bounds)
    {
        Sky = SceneSky.Resolve(SkyMode, WeatherMode, DateTime.Now);
        Layout(bounds);
        var time = Time;
        // Hovering a villager or window rings that one agent; hovering the building rings everyone who lives there.
        var agentHover = HoveredAgent;
        var hovered = HoveredProject;
        var ring = agentHover is null ? hovered : null;
        var nowUtc = DateTime.UtcNow;

        DrawSky(dc, bounds, time);
        var key = $"{_layoutKey}|{bounds.Width:0}x{bounds.Height:0}|{Sky.Darkness:0.00}|{Sky.Weather}|{WorkflowTown.Instance.Stamp}";
        if (_static is null || key != _staticKey) { _static = BuildStatic(bounds); _staticKey = key; }
        dc.DrawDrawing(_static);

        foreach (var building in _buildings) Hit(building.Outline, building.Project);
        foreach (var building in _buildings) HitPlaque(building);
        foreach (var building in _buildings) DrawLights(dc, building, time, ring);
        DrawSmoke(dc, time);
        DrawLamps(dc, bounds, time);
        DrawBard(dc, bounds, time);
        DrawVillagers(dc, bounds, time, nowUtc, ring);
        SceneWeatherFx.Draw(dc, bounds, Sky, time, _ground, Fill, Stroke);
        DrawLightning(dc, bounds, time);
        foreach (var building in _buildings) DrawSign(dc, building, ring);
        DrawFireworks(dc);

        if (agentHover is not null) DrawAgentCard(dc, bounds, agentHover, nowUtc);
        else if (HoveredPlaque is { } plaque && _buildings.FirstOrDefault(b => b.Project.Key == plaque.Key) is { } home)
            DrawImprovementsCard(dc, bounds, PlaqueRect(home), plaque, WorkflowTown.Instance.For(plaque.Key),
                Color.FromArgb(244, 0x22, 0x19, 0x12), Color.FromRgb(0xC9, 0xA2, 0x27), CardInk, CardMuted);
        else if (hovered is not null && _buildings.FirstOrDefault(b => b.Project.Key == hovered.Key) is { } focus)
        {
            // Everything else steps back so the hovered building and its people read at once.
            foreach (var other in _buildings.Where(b => b != focus))
                dc.DrawRectangle(Fill(Color.FromArgb(70, 6, 8, 16)), null, Inflate(other.Outline, 6, 4));
            dc.DrawRectangle(null, Stroke(Color.FromArgb(90, 0xF5, 0xCB, 0x5C), 7), Inflate(focus.Outline, 4, 4));
            dc.DrawRectangle(null, Stroke(Color.FromRgb(0xF5, 0xCB, 0x5C), 2), Inflate(focus.Outline, 4, 4));
            DrawProjectCard(dc, bounds, focus, nowUtc);
        }
        DrawHud(dc, bounds);
    }

    // ---- layout -------------------------------------------------------------------------------------------------

    private void Layout(Rect bounds)
    {
        var key = bounds.Width.ToString("0") + "x" + bounds.Height.ToString("0") + "|" + string.Join(";", Projects.Select(p =>
            p.Key + ":" + string.Join(",", p.Agents.Take(12).Select(a => a.Id)) + ":" + p.Agents.Count));
        // Rebind to the newest snapshot every frame; positions only move when the shape of the town changes.
        if (key == _layoutKey)
        {
            foreach (var building in _buildings)
                building.Project = Projects.FirstOrDefault(p => p.Key == building.Project.Key) ?? building.Project;
            return;
        }
        _layoutKey = key;
        _buildings.Clear();
        _ground = Math.Round(bounds.Height * 0.76);
        if (Projects.Count == 0) return;

        var specs = Projects.Select(p =>
        {
            var shown = Math.Min(12, p.Agents.Count);
            var columns = shown <= 2 ? 2 : shown <= 6 ? 3 : 4;
            var floors = Math.Max(1, (int)Math.Ceiling(shown / (double)columns));
            var width = 120 + columns * 30.0;
            var height = 96 + floors * 46.0;
            return (Project: p, Columns: columns, Floors: floors, Width: width, Height: height, Roof: Math.Min(70, width * 0.36));
        }).ToArray();
        const double Gap = 46, Margin = 40;
        var natural = specs.Sum(s => s.Width + 20) + Gap * (specs.Length - 1);
        var tallest = specs.Max(s => s.Height + s.Roof + 30);
        var scale = Math.Min(1.7, Math.Min((bounds.Width - Margin * 2) / natural, (_ground - 64) / tallest));
        scale = Math.Max(0.4, scale);
        _scale = scale;
        var x = (bounds.Width - natural * scale) / 2 + 10 * scale;

        // The busiest project is the tavern; the rest take the other looks in a stable order.
        var tavern = Projects.Where(p => p.Key.Length > 0).OrderByDescending(p => p.Agents.Count).ThenBy(p => p.Name).FirstOrDefault();
        var looks = new[] { Look.Stone, Look.Brick, Look.Forge, Look.Hall };
        var next = (int)(Hash(string.Join("", Projects.Select(p => p.Key))) * looks.Length);
        foreach (var spec in specs)
        {
            var s = scale;
            var body = new Rect(Math.Round(x), Math.Round(_ground - spec.Height * s), Math.Round(spec.Width * s), Math.Round(spec.Height * s));
            var look = spec.Project.Key.Length == 0 ? Look.Shed : ReferenceEquals(spec.Project, tavern) ? Look.Tavern : looks[next++ % looks.Length];
            var b = new Building
            {
                Project = spec.Project, Body = body, Scale = s, Style = (int)look, Columns = spec.Columns, Overhang = Math.Round(10 * s),
                RoofPeak = new Point(body.Left + body.Width / 2, Math.Round(body.Top - spec.Roof * s)),
            };
            var doorWidth = Math.Round(30 * s);
            b.Door = new Rect(Math.Round(body.Left + body.Width * 0.62 - doorWidth / 2), Math.Round(_ground - 46 * s), doorWidth, Math.Round(46 * s));
            var chimneyX = body.Left + body.Width * 0.72;
            b.Chimney = new Rect(Math.Round(chimneyX), Math.Round(RoofY(b, chimneyX) - 22 * s), Math.Round(16 * s), Math.Round(30 * s));
            // Windows fill the upper floors left to right, top down; the ground floor keeps the door and the sign.
            var winW = Math.Round(20 * s); var winH = Math.Round(24 * s);
            var pad = (body.Width - spec.Columns * winW) / (spec.Columns + 1);
            for (var i = 0; i < Math.Min(12, spec.Project.Agents.Count); i++)
            {
                var row = i / spec.Columns; var col = i % spec.Columns;
                var wx = body.Left + pad + col * (winW + pad);
                var wy = body.Top + 14 * s + row * 46 * s;
                b.Windows.Add((spec.Project.Agents[i].Id, new Rect(Math.Round(wx), Math.Round(wy), winW, winH)));
            }
            _buildings.Add(b);
            x += (spec.Width + 20 + Gap) * s;
        }
    }

    private static double RoofY(Building b, double x)
    {
        var half = b.Body.Width / 2 + b.Overhang;
        var t = Math.Clamp(1 - Math.Abs(x - b.RoofPeak.X) / half, 0, 1);
        return b.Body.Top - (b.Body.Top - b.RoofPeak.Y) * t;
    }

    // ---- sky ----------------------------------------------------------------------------------------------------

    private void DrawSky(DrawingContext dc, Rect bounds, double time) =>
        SceneWeatherFx.DrawSky(dc, bounds.Width, _ground, Sky, time, Fill, Stroke);

    // ---- the street and the buildings (cached until the town, the light or the weather changes) -----------------

    private Color Shade(Color c) => Mix(c, Color.FromRgb(10, 14, 30), Sky.Darkness * 1.1);

    private DrawingGroup BuildStatic(Rect bounds)
    {
        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            // Two ranges of hills, the far one bluer.
            DrawHills(dc, bounds, _ground - bounds.Height * 0.2, Shade(Color.FromRgb(0x5E, 0x7F, 0x8C)), 0.7, 1);
            DrawHills(dc, bounds, _ground - bounds.Height * 0.1, Shade(Color.FromRgb(0x4F, 0x8A, 0x55)), 1.3, 2);
            // Grass verge, kerb, cobbles.
            dc.DrawRectangle(Fill(Shade(Color.FromRgb(0x5C, 0x8F, 0x46))), null, new Rect(0, _ground - 6, bounds.Width, 10));
            var street = new Rect(0, _ground + 4, bounds.Width, bounds.Height - _ground - 4);
            dc.DrawRectangle(Fill(Shade(Color.FromRgb(0x6E, 0x66, 0x5E))), null, street);
            var mortar = Fill(Shade(Color.FromRgb(0x58, 0x51, 0x4A)));
            var light = Fill(Shade(Color.FromRgb(0x7C, 0x74, 0x6B)));
            for (var row = 0; street.Top + row * 12 < street.Bottom; row++)
            {
                var y = street.Top + row * 12;
                dc.DrawRectangle(mortar, null, new Rect(0, y, bounds.Width, 2));
                for (var cx = (row % 2) * 13.0; cx < bounds.Width; cx += 26)
                {
                    dc.DrawRectangle(mortar, null, new Rect(cx, y, 2, 12));
                    dc.DrawRectangle(light, null, new Rect(cx + 4, y + 3, 8, 2));
                }
            }
            dc.DrawRectangle(Fill(Shade(Color.FromRgb(0x8E, 0x86, 0x7C))), null, new Rect(0, _ground + 2, bounds.Width, 4));
            if (Sky.Weather is SceneWeather.Rain or SceneWeather.Storm)
            {
                // Puddles catch the sky.
                for (var i = 0; i < 7; i++)
                {
                    var px = SceneWeatherFx.Noise(i * 17 + 5) * bounds.Width;
                    var py = _ground + 24 + SceneWeatherFx.Noise(i * 17 + 6) * Math.Max(10, street.Height - 40);
                    var pw = 40 + SceneWeatherFx.Noise(i * 17 + 7) * 70;
                    dc.DrawEllipse(Fill(Alpha(Mix(Sky.Bottom, Color.FromRgb(0x9F, 0xB8, 0xD8), 0.4), 0.55)), null, new Point(px, py), pw / 2, pw / 9);
                }
            }
            if (Sky.Weather == SceneWeather.Snow)
                dc.DrawRectangle(Fill(Color.FromArgb(150, 0xEE, 0xF2, 0xFA)), null, new Rect(0, _ground - 2, bounds.Width, 9));
            foreach (var building in _buildings) DrawBuilding(dc, building);
            foreach (var building in _buildings) DrawImprovements(dc, building);
            foreach (var lamp in LampPositions(bounds)) DrawLampPost(dc, lamp);
        }
        group.Freeze();
        return group;
    }

    private void DrawHills(DrawingContext dc, Rect bounds, double baseline, Color color, double frequency, int seed)
    {
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(0, _ground), true, true);
            for (var x = 0.0; x <= bounds.Width + 16; x += 16)
            {
                var y = baseline - (Math.Sin(x / bounds.Width * Math.PI * 2 * frequency + seed) * 0.5 + 0.5) * bounds.Height * 0.09
                        - Math.Sin(x / 97.0 + seed * 3) * 6;
                g.LineTo(new Point(x, Math.Round(y / 4) * 4), true, false);
            }
            g.LineTo(new Point(bounds.Width, _ground), true, false);
        }
        geometry.Freeze();
        dc.DrawGeometry(Fill(color), null, geometry);
    }

    private static (Color Wall, Color Trim, Color Roof, Color Detail) Colours(Look look) => look switch
    {
        Look.Tavern => (PixelArt.C("#E9DCC0"), PixelArt.C("#5B3A22"), PixelArt.C("#A4472E"), PixelArt.C("#7A3422")),
        Look.Stone => (PixelArt.C("#9EA3A8"), PixelArt.C("#5E6368"), PixelArt.C("#46607E"), PixelArt.C("#7D8388")),
        Look.Brick => (PixelArt.C("#B4583E"), PixelArt.C("#6B2F22"), PixelArt.C("#3F6B4A"), PixelArt.C("#93452F")),
        Look.Forge => (PixelArt.C("#6A6260"), PixelArt.C("#3A3434"), PixelArt.C("#3A3238"), PixelArt.C("#544C4A")),
        Look.Hall => (PixelArt.C("#D9C28E"), PixelArt.C("#7A5A2A"), PixelArt.C("#6A4A8C"), PixelArt.C("#BFA56E")),
        _ => (PixelArt.C("#9C7A52"), PixelArt.C("#5C4026"), PixelArt.C("#6E5A3E"), PixelArt.C("#83643F")),
    };

    private void DrawBuilding(DrawingContext dc, Building b)
    {
        var s = b.Scale;
        var look = (Look)b.Style;
        var (wall, trim, roof, detail) = Colours(look);
        var body = b.Body;
        // Foundation and walls.
        dc.DrawRectangle(Fill(Shade(Color.FromRgb(0x5A, 0x55, 0x50))), null, new Rect(body.Left - 3 * s, body.Bottom - 8 * s, body.Width + 6 * s, 10 * s));
        dc.DrawRectangle(Fill(Shade(wall)), null, body);
        var trimInk = Fill(Shade(trim));
        var detailInk = Fill(Shade(detail));
        switch (look)
        {
            case Look.Tavern or Look.Shed:
                // Timber frame: posts at the corners and between bays, a beam at every floor.
                foreach (var px in new[] { body.Left, body.Right - 5 * s }.Concat(Enumerable.Range(1, b.Columns - 1).Select(c => body.Left + body.Width * c / b.Columns - 2 * s)))
                    dc.DrawRectangle(trimInk, null, new Rect(Math.Round(px), body.Top, Math.Round(5 * s), body.Height));
                for (var y = body.Top; y < body.Bottom - 50 * s; y += 46 * s)
                    dc.DrawRectangle(trimInk, null, new Rect(body.Left, Math.Round(y), body.Width, Math.Round(5 * s)));
                if (look == Look.Shed)
                    for (var px = body.Left + 8 * s; px < body.Right; px += 10 * s)
                        dc.DrawRectangle(detailInk, null, new Rect(Math.Round(px), body.Top, Math.Max(1, Math.Round(2 * s)), body.Height));
                break;
            case Look.Stone or Look.Forge or Look.Hall:
                for (var row = 0; body.Top + row * 9 * s < body.Bottom; row++)
                {
                    var y = Math.Round(body.Top + row * 9 * s);
                    dc.DrawRectangle(detailInk, null, new Rect(body.Left, y, body.Width, Math.Max(1, Math.Round(1.5 * s))));
                    for (var px = body.Left + (row % 2) * 9 * s; px < body.Right; px += 18 * s)
                        dc.DrawRectangle(detailInk, null, new Rect(Math.Round(px), y, Math.Max(1, Math.Round(1.5 * s)), Math.Round(9 * s)));
                }
                break;
            case Look.Brick:
                for (var row = 0; body.Top + row * 6 * s < body.Bottom; row++)
                {
                    var y = Math.Round(body.Top + row * 6 * s);
                    dc.DrawRectangle(detailInk, null, new Rect(body.Left, y, body.Width, Math.Max(1, Math.Round(1 * s))));
                    for (var px = body.Left + (row % 2) * 7 * s; px < body.Right; px += 14 * s)
                        dc.DrawRectangle(detailInk, null, new Rect(Math.Round(px), y, Math.Max(1, Math.Round(1 * s)), Math.Round(6 * s)));
                }
                break;
        }
        // Roof: a pixel-stepped gable, lighter on the sunlit side; the forge gets a flat roof with battlements.
        var roofInk = Fill(Shade(roof));
        var roofShade = Fill(Shade(PixelArt.Shade(roof, 0.78)));
        if (look == Look.Forge)
        {
            var top = new Rect(body.Left - b.Overhang / 2, body.Top - 12 * s, body.Width + b.Overhang, 12 * s);
            dc.DrawRectangle(roofInk, null, top);
            for (var px = top.Left; px < top.Right - 6 * s; px += 14 * s)
                dc.DrawRectangle(roofInk, null, new Rect(Math.Round(px), Math.Round(top.Top - 8 * s), Math.Round(8 * s), Math.Round(8 * s)));
        }
        else
        {
            var step = Math.Max(2, Math.Round(4 * s));
            for (var y = b.RoofPeak.Y; y < body.Top; y += step)
            {
                var t = (y - b.RoofPeak.Y) / (body.Top - b.RoofPeak.Y);
                var half = (body.Width / 2 + b.Overhang) * t;
                dc.DrawRectangle(roofInk, null, new Rect(Math.Round(b.RoofPeak.X - half), Math.Round(y), Math.Round(half), step));
                dc.DrawRectangle(roofShade, null, new Rect(Math.Round(b.RoofPeak.X), Math.Round(y), Math.Round(half), step));
            }
            dc.DrawRectangle(Fill(Shade(PixelArt.Shade(roof, 0.6))), null, new Rect(body.Left - b.Overhang, body.Top - step, body.Width + b.Overhang * 2, step));
            if (look == Look.Hall)
            {
                // A little bell tower over the guild hall.
                var tower = new Rect(b.RoofPeak.X - 9 * s, b.RoofPeak.Y - 26 * s, 18 * s, 30 * s);
                dc.DrawRectangle(Fill(Shade(wall)), null, tower);
                dc.DrawRectangle(Fill(Shade(Color.FromRgb(0x2A, 0x24, 0x30))), null, new Rect(tower.Left + 5 * s, tower.Top + 8 * s, 8 * s, 10 * s));
                dc.DrawRectangle(roofInk, null, new Rect(tower.Left - 3 * s, tower.Top - 6 * s, tower.Width + 6 * s, 6 * s));
                dc.DrawRectangle(roofInk, null, new Rect(tower.Left + 3 * s, tower.Top - 12 * s, tower.Width - 6 * s, 6 * s));
            }
        }
        // Chimney.
        dc.DrawRectangle(Fill(Shade(Color.FromRgb(0x7A, 0x5A, 0x4A))), null, b.Chimney);
        dc.DrawRectangle(Fill(Shade(Color.FromRgb(0x4E, 0x3A, 0x30))), null, new Rect(b.Chimney.Left - 2 * s, b.Chimney.Top, b.Chimney.Width + 4 * s, 4 * s));
        // Window frames and sills; the glass is drawn live.
        foreach (var (_, window) in b.Windows)
        {
            dc.DrawRectangle(trimInk, null, Inflate(window, 3 * s, 3 * s));
            dc.DrawRectangle(Fill(Shade(PixelArt.Shade(trim, 0.8))), null, new Rect(window.Left - 5 * s, window.Bottom + 3 * s, window.Width + 10 * s, 3 * s));
        }
        // Door: planks, an arch, a ring.
        var door = b.Door;
        dc.DrawRectangle(trimInk, null, Inflate(door, 3 * s, 3 * s));
        dc.DrawRectangle(Fill(Shade(Color.FromRgb(0x6B, 0x45, 0x2A))), null, door);
        for (var px = door.Left + 7 * s; px < door.Right - 2; px += 7 * s)
            dc.DrawRectangle(Fill(Shade(Color.FromRgb(0x55, 0x36, 0x20))), null, new Rect(Math.Round(px), door.Top, Math.Max(1, Math.Round(1.5 * s)), door.Height));
        dc.DrawRectangle(Fill(Shade(Color.FromRgb(0xC9, 0xA2, 0x27))), null, new Rect(door.Right - 8 * s, door.Top + door.Height / 2, 3 * s, 3 * s));
        // Bench beside the house for anyone dozing.
        var bench = new Rect(body.Left + 6 * s, _ground + 8 * s, 34 * s, 4 * s);
        dc.DrawRectangle(Fill(Shade(Color.FromRgb(0x7A, 0x55, 0x32))), null, bench);
        dc.DrawRectangle(Fill(Shade(Color.FromRgb(0x5A, 0x3D, 0x22))), null, new Rect(bench.Left + 2 * s, bench.Bottom, 3 * s, 8 * s));
        dc.DrawRectangle(Fill(Shade(Color.FromRgb(0x5A, 0x3D, 0x22))), null, new Rect(bench.Right - 5 * s, bench.Bottom, 3 * s, 8 * s));
    }

    private static double SignSize(Building b) => Math.Clamp(13 * b.Scale, 10.5, 19);
    private static double CountsSize(Building b) => Math.Clamp(9.5 * b.Scale, 9, 13);

    private static string Counts(AgentActivityProject project) =>
        project.Working > 0 ? $"{project.Working} working" : project.Agents.Count == 1 ? "1 agent" : $"{project.Agents.Count} agents";

    /// <summary>The name board: centred on the front wall just above the door, as wide as the name needs.</summary>
    private Rect Sign(Building b)
    {
        var s = b.Scale;
        var size = SignSize(b);
        var room = b.Body.Width - 34 * s;
        var text = Text(b.Project.Name, size, Colors.White, bold: true, maxWidth: room);
        var counts = Text(Counts(b.Project), CountsSize(b), Colors.White);
        var width = Math.Round(Math.Min(Math.Max(text.Width, counts.Width), room) + 18 * s);
        var height = Math.Round(text.Height + counts.Height + 8 * s);
        return new Rect(Math.Round(b.Body.Left + (b.Body.Width - width) / 2), Math.Round(b.Door.Top - 8 * s - height), width, height);
    }

    // ---- live parts: lights, smoke, lamps, people, signs --------------------------------------------------------

    private void DrawLights(DrawingContext dc, Building b, double time, AgentActivityProject? hovered)
    {
        var s = b.Scale;
        var night = Sky.Darkness;
        foreach (var (id, window) in b.Windows)
        {
            var agent = b.Project.Agents.FirstOrDefault(a => a.Id == id);
            if (agent is null) continue;
            Hit(Inflate(window, 4, 4), agent);
            var flicker = 0.86 + 0.1 * Math.Sin(time * 7.3 + Hash(id) * 20) + 0.04 * Math.Sin(time * 17 + Hash(id, 2) * 9);
            Color glass;
            switch (agent.State)
            {
                case AgentActivityState.Working:
                    glass = Mix(Color.FromRgb(0xFF, 0xE0, 0x96), Color.FromRgb(0xFF, 0xC4, 0x5C), 0.5 + 0.5 * Math.Sin(time * 3 + Hash(id) * 6));
                    if (night > 0.05)
                    {
                        dc.PushOpacity(Math.Min(1, night * 1.6) * flicker);
                        dc.DrawEllipse(Glow, null, new Point(window.Left + window.Width / 2, window.Top + window.Height / 2), window.Width * 2.2, window.Height * 1.8);
                        dc.Pop();
                    }
                    dc.DrawRectangle(Fill(Alpha(glass, 0.75 + 0.25 * flicker)), null, window);
                    // Someone at work inside: a silhouette nodding over the desk.
                    var bob = Math.Round(Math.Sin(time * 11 + Hash(id) * 30) * 0.8 * s);
                    var shadow = Fill(Color.FromArgb(150, 0x5A, 0x32, 0x18));
                    dc.DrawEllipse(shadow, null, new Point(window.Left + window.Width * 0.5, window.Bottom - 12 * s + bob), 4 * s, 4 * s);
                    dc.DrawRectangle(shadow, null, new Rect(window.Left + window.Width * 0.18, window.Bottom - 7 * s + bob, window.Width * 0.64, 7 * s - bob));
                    break;
                case AgentActivityState.Waiting or AgentActivityState.Paused:
                    glass = Mix(Shade(Color.FromRgb(0x3A, 0x45, 0x5A)), Color.FromRgb(0xF2, 0xB8, 0x4B), agent.State == AgentActivityState.Waiting ? 0.25 + 0.2 * Math.Sin(time * 4) : 0.1);
                    dc.DrawRectangle(Fill(glass), null, window);
                    break;
                case AgentActivityState.Error:
                    dc.DrawRectangle(Fill(Mix(Shade(Color.FromRgb(0x3A, 0x30, 0x38)), Color.FromRgb(0xF2, 0x6D, 0x6D), 0.45)), null, window);
                    var crack = Stroke(Color.FromRgb(0x1A, 0x12, 0x14), Math.Max(1, s));
                    dc.DrawLine(crack, new Point(window.Left + 3 * s, window.Top + 4 * s), new Point(window.Left + window.Width * 0.55, window.Top + window.Height * 0.5));
                    dc.DrawLine(crack, new Point(window.Left + window.Width * 0.55, window.Top + window.Height * 0.5), new Point(window.Right - 4 * s, window.Bottom - 3 * s));
                    break;
                default:
                    // Nobody home: glass that reflects the sky by day and goes dark at night.
                    glass = night > 0.3 ? Color.FromRgb(0x1C, 0x22, 0x33) : Shade(Mix(Sky.Bottom, Color.FromRgb(0x8A, 0xA4, 0xBC), 0.5));
                    dc.DrawRectangle(Fill(glass), null, window);
                    dc.DrawRectangle(Fill(Color.FromArgb((byte)(night > 0.3 ? 30 : 90), 255, 255, 255)), null, new Rect(window.Left + 2 * s, window.Top + 2 * s, 3 * s, window.Height * 0.4));
                    break;
            }
            // Muntins.
            var bar = Fill(Shade(Colours((Look)b.Style).Trim));
            dc.DrawRectangle(bar, null, new Rect(window.Left + window.Width / 2 - s / 2, window.Top, Math.Max(1, s), window.Height));
            dc.DrawRectangle(bar, null, new Rect(window.Left, window.Top + window.Height / 2 - s / 2, window.Width, Math.Max(1, s)));
            if (agent.Chat.IsBridgeManager || agent.IsCentralPlanner)
                DrawCrown(dc, new Point(window.Left + window.Width / 2, window.Top - 7 * s), s);
            if (agent.State == AgentActivityState.Paused) Snore(dc, new Point(window.Right, window.Top), time, s);
            if (Hovered(agent) || hovered?.Key == b.Project.Key)
                dc.DrawRectangle(null, Stroke(Color.FromRgb(0xF5, 0xCB, 0x5C), Hovered(agent) ? 2.5 : 1.5), Inflate(window, 4, 4));
        }
        var more = b.Project.Agents.Count - b.Windows.Count;
        if (more > 0)
        {
            var label = Text("+" + more, Math.Clamp(11 * s, 10, 15), Color.FromRgb(0xF6, 0xE7, 0xC1), bold: true);
            dc.DrawText(label, new Point(b.Body.Right - label.Width - 6 * s, b.Body.Top + 2 * s));
        }
        // Light spilling from the door at night, and the forge's fire.
        if (night > 0.12 && b.Project.Working > 0)
        {
            dc.PushOpacity(Math.Min(1, night * 1.5) * 0.8);
            dc.DrawEllipse(Glow, null, new Point(b.Door.Left + b.Door.Width / 2, b.Door.Bottom), b.Door.Width * 1.6, 16 * s);
            dc.Pop();
        }
        if ((Look)b.Style == Look.Forge && b.Project.Working > 0)
        {
            var fire = new Rect(b.Body.Left + 12 * s, b.Body.Bottom - 30 * s, 22 * s, 18 * s);
            dc.PushOpacity(0.7 + 0.3 * Math.Sin(time * 9));
            dc.DrawEllipse(ForgeGlow, null, new Point(fire.Left + fire.Width / 2, fire.Top + fire.Height / 2), 34 * s, 26 * s);
            dc.Pop();
            dc.DrawRectangle(Fill(Color.FromRgb(0x2A, 0x1A, 0x14)), null, fire);
            dc.DrawRectangle(Fill(Color.FromRgb(0xFF, 0x8A, 0x2A)), null, new Rect(fire.Left + 4 * s, fire.Top + 6 * s + Math.Sin(time * 12) * s, fire.Width - 8 * s, fire.Height - 8 * s));
        }
    }

    private void DrawSmoke(DrawingContext dc, double time)
    {
        foreach (var b in _buildings)
        {
            var working = b.Project.Working;
            if (working == 0) continue;
            var s = b.Scale;
            var puffs = Math.Min(8, 3 + working * 2);
            var origin = new Point(b.Chimney.Left + b.Chimney.Width / 2, b.Chimney.Top);
            var ink = Sky.Night ? Color.FromRgb(0x8E, 0x92, 0xA0) : Color.FromRgb(0xE4, 0xE4, 0xE8);
            for (var i = 0; i < puffs; i++)
            {
                var t = (time * 0.32 + i / (double)puffs + Hash(b.Project.Key)) % 1;
                var rise = t * 90 * s;
                var drift = t * t * 46 * s + Math.Sin(time * 1.4 + i) * 3 * s;
                var size = (5 + t * 12) * s;
                dc.DrawRectangle(Fill(Alpha(ink, 0.55 * (1 - t))), null,
                    new Rect(Math.Round(origin.X + drift - size / 2), Math.Round(origin.Y - rise - size / 2), Math.Round(size), Math.Round(size)));
            }
        }
    }

    private IEnumerable<Point> LampPositions(Rect bounds)
    {
        if (_buildings.Count == 0) { yield return new Point(bounds.Width * 0.3, _ground); yield return new Point(bounds.Width * 0.7, _ground); yield break; }
        yield return new Point(Math.Max(20, _buildings[0].Body.Left - 34), _ground);
        for (var i = 1; i < _buildings.Count; i++)
            yield return new Point((_buildings[i - 1].Body.Right + _buildings[i].Body.Left) / 2, _ground);
        yield return new Point(Math.Min(bounds.Width - 20, _buildings[^1].Body.Right + 34), _ground);
    }

    /// <summary>Lamps are sized with the town so a crowded street does not get lamp posts taller than its houses.</summary>
    private double LampScale => Math.Clamp(_scale, 0.45, 1.2);

    private void DrawLampPost(DrawingContext dc, Point foot)
    {
        var k = LampScale;
        var post = Fill(Shade(Color.FromRgb(0x2E, 0x2D, 0x33)));
        dc.DrawRectangle(post, null, new Rect(Math.Round(foot.X - 2 * k), Math.Round(foot.Y - 74 * k), Math.Max(2, Math.Round(4 * k)), Math.Round(78 * k)));
        dc.DrawRectangle(post, null, new Rect(Math.Round(foot.X - 6 * k), Math.Round(foot.Y - 2 * k), Math.Round(12 * k), Math.Round(6 * k)));
        dc.DrawRectangle(post, null, new Rect(Math.Round(foot.X - 8 * k), Math.Round(foot.Y - 88 * k), Math.Round(16 * k), Math.Max(2, Math.Round(4 * k))));
        dc.DrawRectangle(Fill(Shade(Color.FromRgb(0x6A, 0x6E, 0x78))), null, new Rect(Math.Round(foot.X - 6 * k), Math.Round(foot.Y - 84 * k), Math.Round(12 * k), Math.Round(12 * k)));
    }

    private void DrawLamps(DrawingContext dc, Rect bounds, double time)
    {
        if (!Sky.LampsOn) return;
        var k = LampScale;
        foreach (var foot in LampPositions(bounds))
        {
            var head = new Point(foot.X, foot.Y - 78 * k);
            dc.PushOpacity(Math.Min(1, Sky.Darkness * 1.8) * (0.9 + 0.1 * Math.Sin(time * 5 + foot.X)));
            dc.DrawEllipse(LampGlow, null, head, 60 * k, 60 * k);
            dc.DrawEllipse(LampGlow, null, new Point(foot.X, foot.Y + 10 * k), 46 * k, 12 * k);
            dc.Pop();
            dc.DrawRectangle(Fill(Color.FromRgb(0xFF, 0xE6, 0x9A)), null, new Rect(Math.Round(head.X - 4 * k), Math.Round(head.Y - 4 * k), Math.Round(8 * k), Math.Round(8 * k)));
        }
    }

    private void DrawVillagers(DrawingContext dc, Rect bounds, double time, DateTime nowUtc, AgentActivityProject? hovered)
    {
        var tavern = _buildings.FirstOrDefault(b => (Look)b.Style == Look.Tavern) ?? _buildings.FirstOrDefault();
        var people = new List<(double Y, Action Draw)>();
        foreach (var b in _buildings)
        {
            var s = b.Scale;
            var px = Math.Clamp(Math.Round(2.4 * s * 2) / 2, 2, 4);
            var height = PixelArt.Height * px;
            var width = PixelArt.Width * px;
            var outside = b.Project.Agents.Where(a => a.State != AgentActivityState.Working).ToList();
            for (var i = 0; i < outside.Count; i++)
            {
                var agent = outside[i];
                var h = Hash(agent.Id);
                var lane = _ground + 12 + Math.Round(h * Math.Max(4, Math.Min(40, bounds.Height - _ground - height - 16)));
                double x; var pose = PixelArt.Pose.Stand; var flip = false; string? bubble = null; var bubbleInk = Colors.White;
                switch (agent.State)
                {
                    case AgentActivityState.Waiting:
                        x = b.Door.Left - width - 4 - i * (width + 4);
                        pose = (time * 1.6 + h) % 1 < 0.5 ? PixelArt.Pose.Wave : PixelArt.Pose.Stand;
                        bubble = agent.Status is "Sign-in needed" or "Usage limit" ? "!" : "?";
                        bubbleInk = Color.FromRgb(0xF2, 0xB8, 0x4B);
                        lane = _ground + 10;
                        break;
                    case AgentActivityState.Error:
                        x = b.Door.Right + 6 + i * (width + 4) + Math.Round(Math.Sin(time * 30) * (IsAnimating ? 1 : 0));
                        bubble = "!";
                        bubbleInk = Color.FromRgb(0xF2, 0x6D, 0x6D);
                        lane = _ground + 10;
                        break;
                    case AgentActivityState.Paused:
                        x = b.Body.Left + 8 * s + i * (width - 2);
                        pose = PixelArt.Pose.Sleep;
                        lane = _ground + 4;
                        break;
                    default:
                        if (agent.Status == "Completed" && tavern is not null)
                        {
                            // Done for the day: off to the tavern for a mug.
                            x = tavern.Door.Right + 10 + (i % 4) * (width + 6) + Math.Sin(time * 0.7 + h * 9) * 3;
                            pose = PixelArt.Pose.Mug;
                            flip = Hash(agent.Id, 5) > 0.5;
                        }
                        else
                        {
                            // Stroll in front of home: walk, pause, walk back.
                            var span = Math.Max(30, b.Body.Width + 30);
                            var cycle = 12 + h * 10;
                            var phase = (time / cycle + h) % 1;
                            double along; bool moving;
                            if (phase < 0.4) { along = Ease(phase / 0.4); moving = true; flip = false; }
                            else if (phase < 0.5) { along = 1; moving = false; }
                            else if (phase < 0.9) { along = 1 - Ease((phase - 0.5) / 0.4); moving = true; flip = true; }
                            else { along = 0; moving = false; }
                            x = b.Body.Left - 15 + along * span - width / 2;
                            pose = moving && Math.Floor(time * 6 + h * 4) % 2 == 0 ? PixelArt.Pose.Walk : PixelArt.Pose.Stand;
                        }
                        // Quest complete: the mug goes up, and up again.
                        if (MomentAge(agent.Id) is var age and >= 0) pose = (int)(age * 3) % 2 == 0 ? PixelArt.Pose.Cheer : PixelArt.Pose.Mug;
                        break;
                }
                x = Math.Clamp(x, 2, bounds.Width - width - 2);
                var feet = Math.Min(bounds.Height - 4, lane + height);
                var rect = new Rect(Math.Round(x), Math.Round(feet - height), width, height);
                var mine = hovered?.Key == b.Project.Key;
                var focused = Hovered(agent);
                var captured = (agent, rect, pose, flip, bubble, bubbleInk, mine, focused, px);
                people.Add((feet, () => DrawPerson(dc, captured.agent, captured.rect, captured.pose, captured.flip, captured.bubble,
                    captured.bubbleInk, captured.mine, captured.focused, captured.px, time)));
            }
            // Subagents run errands between the door and the street.
            var errands = b.Project.Agents.Where(a => a.State == AgentActivityState.Working).Sum(a => a.ActiveChildren);
            for (var i = 0; i < Math.Min(4, errands); i++)
            {
                var small = Math.Max(1.5, px * 0.62);
                var t = (time * 0.45 + i * 0.29 + Hash(b.Project.Key, i)) % 1;
                var go = t < 0.5;
                var along = go ? t * 2 : (1 - t) * 2;
                var ex = b.Door.Left + b.Door.Width / 2 + along * (60 + i * 18) * s * (i % 2 == 0 ? 1 : -1);
                var ey = _ground + 26 + i * 6;
                var id = b.Project.Key + ":errand" + i;
                // Errand runners wear their parent's colours.
                var sprite = PixelArt.Character(id, Math.Floor(time * 10) % 2 == 0 ? PixelArt.Pose.Walk : PixelArt.Pose.Stand, small,
                    flip: !go ^ (i % 2 == 1), night: Sky.Darkness * 0.55, shirt: ErrandParent(b.Project, i) is { } parent ? WorkflowGuilds.ShirtOf(parent) : null);
                people.Add((ey + PixelArt.Height * small, () =>
                {
                    dc.PushTransform(new TranslateTransform(Math.Round(ex), Math.Round(ey)));
                    dc.DrawDrawing(sprite);
                    dc.Pop();
                }));
            }
        }
        foreach (var (_, draw) in people.OrderBy(p => p.Y)) draw();
    }

    private void DrawPerson(DrawingContext dc, AgentActivityNode agent, Rect rect, PixelArt.Pose pose, bool flip, string? bubble,
        Color bubbleInk, bool mine, bool focused, double px, double time)
    {
        Hit(Inflate(rect, 4, 6), agent);
        if (mine || focused)
        {
            dc.DrawEllipse(Fill(Color.FromArgb(90, 0xF5, 0xCB, 0x5C)), Stroke(Color.FromRgb(0xF5, 0xCB, 0x5C), focused ? 2 : 1.2),
                new Point(rect.Left + rect.Width / 2, rect.Bottom), rect.Width * 0.75, rect.Width * 0.25);
        }
        else dc.DrawEllipse(Fill(Color.FromArgb(60, 0, 0, 0)), null, new Point(rect.Left + rect.Width / 2, rect.Bottom), rect.Width * 0.55, rect.Width * 0.16);
        // Villagers stand in the same night as the street around them, unless the pointer is on them.
        var sprite = PixelArt.Character(agent.Id, pose, px, flip, mine || focused ? 0 : Sky.Darkness * 0.55,
            WorkflowGuilds.Shirt(agent), WorkflowGuilds.PropOf(agent));
        dc.PushTransform(new TranslateTransform(rect.Left, rect.Top));
        dc.DrawDrawing(sprite);
        dc.Pop();
        if (agent.Chat.IsBridgeManager || agent.IsCentralPlanner) DrawCrown(dc, new Point(rect.Left + rect.Width / 2, rect.Top - px), px / 2.4);
        if (pose == PixelArt.Pose.Sleep) Snore(dc, new Point(rect.Right - px * 2, rect.Top), time, px / 2.4);
        if (bubble is not null)
        {
            var hop = Math.Round(Math.Abs(Math.Sin(time * 3 + Hash(agent.Id) * 7)) * 3);
            var box = new Rect(rect.Left + rect.Width / 2 - 9, rect.Top - 26 - hop, 18, 18);
            dc.DrawRoundedRectangle(Fill(Color.FromRgb(0xFB, 0xF6, 0xE8)), Stroke(bubbleInk, 2), box, 4, 4);
            var mark = Text(bubble, 13, bubbleInk, bold: true);
            dc.DrawText(mark, new Point(box.Left + (box.Width - mark.Width) / 2, box.Top + (box.Height - mark.Height) / 2));
        }
        if (mine || focused || Projects.Sum(p => p.Agents.Count) <= 10)
        {
            var tag = Text(agent.Label, 10.5, Color.FromRgb(0xF6, 0xEF, 0xDC), bold: focused, maxWidth: 110);
            var at = new Point(Math.Round(rect.Left + rect.Width / 2 - tag.Width / 2), Math.Round(rect.Bottom + 2));
            dc.DrawRoundedRectangle(Fill(Color.FromArgb(150, 12, 10, 16)), null, new Rect(at.X - 4, at.Y - 1, tag.Width + 8, tag.Height + 2), 3, 3);
            dc.DrawText(tag, at);
        }
    }

    private void DrawSign(DrawingContext dc, Building b, AgentActivityProject? hovered)
    {
        var s = b.Scale;
        var sign = Sign(b);
        var size = SignSize(b);
        var lit = Sky.LampsOn;
        var board = lit ? Color.FromRgb(0x6B, 0x45, 0x22) : Color.FromRgb(0x7E, 0x55, 0x2E);
        // The board is lit from below at night, so the name reads from across the room.
        if (lit)
        {
            dc.PushOpacity(0.55);
            dc.DrawEllipse(LampGlow, null, new Point(sign.Left + sign.Width / 2, sign.Top + sign.Height / 2), sign.Width * 0.75, sign.Height * 1.4);
            dc.Pop();
        }
        dc.DrawRectangle(Fill(PixelArt.Shade(board, 0.7)), null, Inflate(sign, 2 * s, 2 * s));
        dc.DrawRectangle(Fill(board), null, sign);
        var name = Text(b.Project.Name, size, Color.FromRgb(0xFB, 0xEB, 0xC4), bold: true, maxWidth: sign.Width - 14 * s);
        var counts = Text(Counts(b.Project), CountsSize(b), b.Project.Working > 0 ? Color.FromRgb(0xA8, 0xF0, 0xBE) : Color.FromRgb(0xE6, 0xDC, 0xC6));
        var top = sign.Top + (sign.Height - name.Height - counts.Height) / 2;
        dc.DrawText(name, new Point(Math.Round(sign.Left + (sign.Width - name.Width) / 2), Math.Round(top)));
        dc.DrawText(counts, new Point(Math.Round(sign.Left + (sign.Width - counts.Width) / 2), Math.Round(top + name.Height)));
        if ((Look)b.Style == Look.Tavern)
        {
            // The tavern's mug, hanging beside its board.
            var mug = new Rect(sign.Right + 4 * s, sign.Top + 2 * s, 10 * s, 12 * s);
            dc.DrawRectangle(Fill(Color.FromRgb(0xE2, 0xA3, 0x3B)), null, mug);
            dc.DrawRectangle(Fill(Color.FromRgb(0xFB, 0xF6, 0xE8)), null, new Rect(mug.Left, mug.Top, mug.Width, 3 * s));
            dc.DrawRectangle(null, Stroke(Color.FromRgb(0xE2, 0xA3, 0x3B), Math.Max(1, 1.5 * s)), new Rect(mug.Right, mug.Top + 3 * s, 3 * s, 6 * s));
        }
        if (hovered?.Key == b.Project.Key)
            dc.DrawRectangle(null, Stroke(Color.FromRgb(0xF5, 0xCB, 0x5C), 1.5), Inflate(sign, 3 * s + 1, 3 * s + 1));
    }

    private void DrawLightning(DrawingContext dc, Rect bounds, double time)
    {
        var flash = SceneWeatherFx.Lightning(Sky, time, out var boltX);
        if (flash <= 0) return;
        dc.DrawRectangle(Fill(Color.FromArgb((byte)(flash * 80), 0xDD, 0xE6, 0xFF)), null, bounds);
        if (flash < 0.5) return;
        var x = bounds.Width * boltX;
        var y = 0.0;
        var seed = (int)(time / 7.3);
        var bolt = new StreamGeometry();
        using (var g = bolt.Open())
        {
            g.BeginFigure(new Point(x, y), false, false);
            for (var i = 0; y < _ground - 60; i++)
            {
                y += 18 + SceneWeatherFx.Noise(seed * 50 + i) * 30;
                x += (SceneWeatherFx.Noise(seed * 50 + i + 25) - 0.5) * 46;
                g.LineTo(new Point(x, Math.Min(y, _ground - 60)), true, false);
            }
        }
        bolt.Freeze();
        dc.DrawGeometry(null, Stroke(Color.FromArgb((byte)(flash * 120), 0xC8, 0xD8, 0xFF), 7), bolt);
        dc.DrawGeometry(null, Stroke(Color.FromArgb((byte)(flash * 255), 0xFF, 0xFF, 0xFF), 2.2), bolt);
    }

    private void DrawCrown(DrawingContext dc, Point top, double s)
    {
        s = Math.Max(1, s);
        var gold = Fill(Color.FromRgb(0xF5, 0xCB, 0x5C));
        dc.DrawRectangle(gold, null, new Rect(Math.Round(top.X - 6 * s), Math.Round(top.Y - 3 * s), Math.Round(12 * s), Math.Round(3 * s)));
        for (var i = -1; i <= 1; i++)
            dc.DrawRectangle(gold, null, new Rect(Math.Round(top.X + i * 5 * s - 1 * s), Math.Round(top.Y - 6 * s), Math.Round(2 * s), Math.Round(3 * s)));
    }

    private void Snore(DrawingContext dc, Point at, double time, double s)
    {
        for (var i = 0; i < 3; i++)
        {
            var t = (time * 0.4 + i / 3.0) % 1;
            var z = Text("z", Math.Max(9, (9 + i * 2) * Math.Max(1, s)), Color.FromRgb(0xE8, 0xEE, 0xFF), bold: true);
            dc.PushOpacity(0.9 * (1 - t));
            dc.DrawText(z, new Point(at.X + t * 12 * Math.Max(1, s), at.Y - t * 26 * Math.Max(1, s)));
            dc.Pop();
        }
    }

    // ---- cards and HUD ------------------------------------------------------------------------------------------

    private static readonly Color CardInk = Color.FromRgb(0xF6, 0xE7, 0xC1);
    private static readonly Color CardMuted = Color.FromRgb(0xC2, 0xB0, 0x8C);

    private void DrawProjectCard(DrawingContext dc, Rect bounds, Building b, DateTime nowUtc)
    {
        var project = b.Project;
        const int MaxRows = 6;
        var rows = project.Agents.Take(MaxRows).ToList();
        var width = Math.Min(380, bounds.Width - 24);
        var height = 70 + rows.Count * 60 + (project.Agents.Count > MaxRows ? 18 : 0) + 22;
        var card = PlaceCard(bounds, b.Outline, width, height);
        Panel(dc, card);
        var x = card.Left + 16; var y = card.Top + 12; var inner = card.Width - 32;
        dc.DrawText(Text(project.Name, 16, CardInk, bold: true, maxWidth: inner - 90), new Point(x, y));
        var counts = Text(project.Working > 0 ? $"● {project.Working} working" : "no one working", 11.5,
            project.Working > 0 ? Color.FromRgb(0x8C, 0xE8, 0xA8) : CardMuted, bold: true);
        dc.DrawText(counts, new Point(card.Right - 16 - counts.Width, y + 3));
        y += 24;
        dc.DrawText(Text(project.Directory.Length == 0 ? "Chats without a project directory" : project.Directory, 11, CardMuted, maxWidth: inner), new Point(x, y));
        y += 22;
        dc.DrawRectangle(Fill(Color.FromArgb(90, 0xC9, 0xA2, 0x27)), null, new Rect(x, y, inner, 1));
        y += 8;
        foreach (var agent in rows) { AgentRows(dc, agent, x, y, inner, nowUtc); y += 60; }
        if (project.Agents.Count > MaxRows)
        {
            dc.DrawText(Text($"+{project.Agents.Count - MaxRows} more in this building", 11, CardMuted), new Point(x, y));
            y += 18;
        }
        dc.DrawText(Text("Click to filter the wall · click a window or villager to open its chat", 10.5, Color.FromRgb(0x9C, 0x8C, 0x6C), maxWidth: inner), new Point(x, y + 2));
    }

    private void DrawAgentCard(DrawingContext dc, Rect bounds, AgentActivityNode agent, DateTime nowUtc)
    {
        var anchor = AnchorFor(agent);
        var width = Math.Min(340, bounds.Width - 24);
        var card = PlaceCard(bounds, anchor, width, 108);
        Panel(dc, card);
        AgentRows(dc, agent, card.Left + 16, card.Top + 12, card.Width - 32, nowUtc);
        var model = agent.Account.Length > 0 ? agent.Model + " · " + agent.Account : agent.Model;
        dc.DrawText(Text(model + "  ·  " + agent.ProjectName, 11, CardMuted, maxWidth: card.Width - 32), new Point(card.Left + 16, card.Top + 72));
        dc.DrawText(Text("Click to open the chat", 10.5, Color.FromRgb(0x9C, 0x8C, 0x6C)), new Point(card.Left + 16, card.Top + 88));
    }

    private Rect AnchorFor(AgentActivityNode agent) =>
        Targets.LastOrDefault(t => t.Key == AgentKey(agent)).Bounds is { IsEmpty: false } r && r.Width > 0 ? r : new Rect(RenderSize.Width / 2, _ground, 1, 1);

    private void AgentRows(DrawingContext dc, AgentActivityNode agent, double x, double y, double width, DateTime nowUtc)
    {
        var state = StateColor(agent.State);
        dc.DrawEllipse(Fill(state), null, new Point(x + 4, y + 9), 4, 4);
        var since = Since(agent, nowUtc);
        var status = Text(since.Length > 0 ? $"{agent.Status} · {since}" : agent.Status, 11, state, bold: true);
        dc.DrawText(status, new Point(x + width - status.Width, y + 1));
        dc.DrawText(Text(agent.Role.Length > 0 ? $"{agent.Label} · {agent.Role}" : agent.Label, 12.5, CardInk, bold: true, maxWidth: width - status.Width - 24), new Point(x + 14, y));
        dc.DrawText(Text(agent.Task, 12, CardInk, maxWidth: width - 14), new Point(x + 14, y + 18));
        var now = agent.Now ?? WorkflowAgentCard.Resting(agent, nowUtc);
        if (now.Glyph.Length > 0) dc.DrawText(Glyph(now.Glyph, 11, agent.State == AgentActivityState.Working ? Color.FromRgb(0x8C, 0xE8, 0xA8) : CardMuted), new Point(x + 14, y + 38));
        dc.DrawText(Text(now.Text, 11.5, agent.State == AgentActivityState.Working ? Color.FromRgb(0xE8, 0xF6, 0xEC) : CardMuted, maxWidth: width - 34),
            new Point(x + 32, y + 36));
    }

    private static Rect PlaceCard(Rect bounds, Rect anchor, double width, double height)
    {
        var x = Math.Clamp(anchor.Left + anchor.Width / 2 - width / 2, 12, Math.Max(12, bounds.Width - width - 12));
        var y = anchor.Top - height - 14;
        if (y < 12) y = Math.Min(anchor.Bottom + 14, bounds.Height - height - 12);
        // Beside the anchor rather than over it when neither above nor below fits.
        if (y < 12 || new Rect(x, y, width, height).IntersectsWith(Inflate(anchor, -anchor.Width * 0.2, -anchor.Height * 0.2)))
        {
            y = Math.Clamp(anchor.Top, 12, Math.Max(12, bounds.Height - height - 12));
            x = anchor.Right + 14 + width < bounds.Width ? anchor.Right + 14 : Math.Max(12, anchor.Left - 14 - width);
        }
        return new Rect(Math.Round(x), Math.Round(y), width, height);
    }

    private void Panel(DrawingContext dc, Rect card)
    {
        dc.DrawRoundedRectangle(Fill(Color.FromArgb(110, 0, 0, 0)), null, new Rect(card.Left + 3, card.Top + 5, card.Width, card.Height), 9, 9);
        dc.DrawRoundedRectangle(Fill(Color.FromArgb(244, 0x22, 0x19, 0x12)), Stroke(Color.FromRgb(0xC9, 0xA2, 0x27), 1.5), card, 8, 8);
        dc.DrawRoundedRectangle(null, Stroke(Color.FromArgb(70, 0xF5, 0xCB, 0x5C), 1), Inflate(card, -4, -4), 5, 5);
    }

    private void DrawHud(DrawingContext dc, Rect bounds)
    {
        var clock = DateTime.Now.ToString("HH:mm");
        var label = Text(Sky.Label + "  ·  " + clock, 12.5, Color.FromRgb(0xF6, 0xEF, 0xDC), bold: true);
        var source = Text(SkyMode == "auto" && WeatherMode == "auto" ? "sky follows your clock · weather " + Sky.Source
            : (SkyMode == "auto" ? "sky follows your clock" : "sky pinned") + " · weather " + Sky.Source, 10.5, Color.FromRgb(0xC9, 0xC2, 0xB0));
        var width = Math.Max(label.Width, source.Width) + 24;
        var box = new Rect(14, 14, width, 44);
        dc.DrawRoundedRectangle(Fill(Color.FromArgb(150, 10, 12, 20)), null, box, 8, 8);
        dc.DrawText(label, new Point(26, 20));
        dc.DrawText(source, new Point(26, 38));
        DrawGuildLegend(dc, new Point(14, box.Bottom + 6));
    }

    private static Rect Inflate(Rect rect, double x, double y)
    {
        rect.Inflate(x, y);
        return rect;
    }

    private static double Ease(double t) => t * t * (3 - 2 * t);

    private static RadialGradientBrush MakeGlow(Color color)
    {
        var brush = new RadialGradientBrush(Color.FromArgb(150, color.R, color.G, color.B), Color.FromArgb(0, color.R, color.G, color.B));
        brush.GradientStops.Insert(1, new GradientStop(Color.FromArgb(60, color.R, color.G, color.B), 0.45));
        brush.Freeze();
        return brush;
    }
}
