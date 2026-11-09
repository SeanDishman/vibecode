using System.Windows;
using System.Windows.Media;

namespace VibeCode.UI;

// The office floor's building, furniture, screens and sprites; WorkflowFloorView.cs places and moves the workers.
public sealed partial class WorkflowFloorView
{
    // ---- the building, cached until the layout changes ---------------------------------------------------------

    private static readonly Color[] Rugs =
    [
        PixelArt.C("#5B6FA0"), PixelArt.C("#7A5F9A"), PixelArt.C("#4F8A7A"), PixelArt.C("#9A6A4F"), PixelArt.C("#5F8F5A"),
        PixelArt.C("#9A5A6A"), PixelArt.C("#4F7F9A"),
    ];
    private static readonly Color[] Books =
    [
        PixelArt.C("#E8574F"), PixelArt.C("#4F8FE8"), PixelArt.C("#5DBB63"), PixelArt.C("#F0A030"), PixelArt.C("#B05FD6"),
        PixelArt.C("#2FB5B0"), PixelArt.C("#E86FA4"), PixelArt.C("#C9A227"), PixelArt.C("#E8E4D0"),
    ];
    private static readonly Color
        WallFace = PixelArt.C("#3E4C66"), WallTrim = PixelArt.C("#56688A"), WallBase = PixelArt.C("#2A3346"), WallEdge = PixelArt.C("#20263A"),
        Carpet = PixelArt.C("#4C5672"), CarpetAlt = PixelArt.C("#48526D"), Plank = PixelArt.C("#8A6A4A"), PlankSeam = PixelArt.C("#76593B"),
        Tile = PixelArt.C("#B9B4A6"), TileAlt = PixelArt.C("#ADA89A"), ServerTile = PixelArt.C("#3A4049"), ServerSeam = PixelArt.C("#30353D"),
        MeetCarpet = PixelArt.C("#55496E"), MeetAlt = PixelArt.C("#504468"), ZoneLine = Color.FromArgb(90, 0x10, 0x12, 0x1A),
        DeskTop = PixelArt.C("#B08458"), DeskEdge = PixelArt.C("#C99D6E"), DeskFace = PixelArt.C("#7E5A36"), DeskDark = PixelArt.C("#5E4228"),
        Bezel = PixelArt.C("#1C1E24"), Metal = PixelArt.C("#9AA0A8"), MetalHi = PixelArt.C("#BCC2CA"), MetalDark = PixelArt.C("#5E646E"),
        KeyCap = PixelArt.C("#D8DCE4"), KeyDot = PixelArt.C("#9EA4AE"), Paper = PixelArt.C("#EEF0F4"), PaperLine = PixelArt.C("#A8B0BE"),
        ChairBack = PixelArt.C("#363645"), ChairSeat = PixelArt.C("#26262F"), ChairHi = PixelArt.C("#50505F"),
        Shade = Color.FromArgb(60, 0, 0, 0), Outline = PixelArt.C("#15161D"),
        Wood = PixelArt.C("#6B4A2E"), WoodDark = PixelArt.C("#4E3520"), WoodBack = PixelArt.C("#3A2818"),
        Steel = PixelArt.C("#8A96A8"), SteelHi = PixelArt.C("#A8B4C4"), SteelLine = PixelArt.C("#68768A"),
        Rack = PixelArt.C("#20242C"), RackFace = PixelArt.C("#2C313B"), RackSlot = PixelArt.C("#171A20"),
        Counter = PixelArt.C("#D8D4C8"), CounterFace = PixelArt.C("#9A968A"), CounterLine = PixelArt.C("#7E7A70"),
        Machine = PixelArt.C("#33373F"), Couch = PixelArt.C("#4F6FA8"), CouchDark = PixelArt.C("#3B5890"), CouchHi = PixelArt.C("#6A8AC2"),
        Leaf = PixelArt.C("#4E9A4A"), LeafHi = PixelArt.C("#6BBF5E"), Pot = PixelArt.C("#A0522D"), PotDark = PixelArt.C("#7E3E20"),
        Table = PixelArt.C("#A0754A"), TableHi = PixelArt.C("#B88A5C"), TableFace = PixelArt.C("#6E4C2C"),
        Board = PixelArt.C("#F4F6F8"), Glass = Color.FromArgb(150, 0x9F, 0xD0, 0xF0), Mat = PixelArt.C("#6A4A3A"), MatLine = PixelArt.C("#7E5C48"),
        Rug = PixelArt.C("#7A3E4A"), RugEdge = PixelArt.C("#94505E");

    private DrawingGroup BuildStatic() => Bake(dc =>
    {
        // A shadow under the whole floor, so it sits on the wall like a diorama.
        dc.DrawRoundedRectangle(Fill(Color.FromArgb(80, 0, 0, 0)), null, Offset(R(0, 0, _fw, _fh), 6, 8), 6, 6);
        Checker(dc, _center, Carpet, CarpetAlt, 8);
        Planks(dc, _library);
        Checker(dc, _kitchen, Tile, TileAlt, 6);
        Checker(dc, _server, ServerTile, ServerSeam, 8);
        Checker(dc, _meeting, MeetCarpet, MeetAlt, 8);
        foreach (var zone in new[] { _library, _kitchen, _server, _meeting })
        {
            Box(dc, ZoneLine, zone.Left, zone.Top, zone.Width, 1);
            Box(dc, ZoneLine, zone.Right - 1, zone.Top, 1, zone.Height);
            Box(dc, ZoneLine, zone.Left, zone.Top, 1, zone.Height);
        }
        // What each project's pod stands on, as furnished as its directory's tokens have made it, and a rug under each couch.
        foreach (var pod in _pods) PodRug(dc, pod);
        foreach (var couch in _couches)
        {
            Box(dc, RugEdge, couch.X - 4, couch.Y + 6, 52, 24);
            Box(dc, Rug, couch.X - 3, couch.Y + 7, 50, 22);
            for (var x = couch.X; x < couch.X + 44; x += 6) Box(dc, RugEdge, x, couch.Y + 18, 3, 1);
        }

        // Walls: the top wall seen face-on, thin side walls, the front wall with its glass door.
        Box(dc, WallFace, 0, 0, _fw, WallH);
        for (var x = 12.0; x < _fw; x += 24) Box(dc, Alpha(WallTrim, 0.25), x, 3, 1, WallH - 6);
        Box(dc, WallTrim, 0, 0, _fw, 2);
        Box(dc, WallBase, 0, WallH - 3, _fw, 3);
        Box(dc, WallEdge, 0, 0, Side, _fh);
        Box(dc, WallEdge, _fw - Side, 0, Side, _fh);
        var bottom = _fh - BottomWall;
        var door = _fw / 2;
        Box(dc, WallEdge, 0, bottom, _fw, BottomWall);
        Box(dc, WallTrim, 0, bottom, _fw, 1);
        Box(dc, Metal, door - 13, bottom, 26, BottomWall);
        Box(dc, Glass, door - 12, bottom + 1, 11, BottomWall - 2);
        Box(dc, Glass, door + 1, bottom + 1, 11, BottomWall - 2);
        Box(dc, MetalDark, door - 2, bottom + 2, 1, 2);
        Box(dc, MetalDark, door + 1, bottom + 2, 1, 2);
        // A lobby rug inside the door, where anyone who needs you waits, and the doormat on it.
        dc.DrawRoundedRectangle(Fill(Alpha(Rugs[0], 0.35)), Stroke(Alpha(Rugs[0], 0.7), Math.Max(1, _p * 0.6)), R(door - 38, bottom - 24, 76, 22), 3 * _p, 3 * _p);
        Box(dc, Mat, door - 12, bottom - 7, 24, 6);
        for (var x = door - 10; x < door + 10; x += 4) Box(dc, MatLine, x, bottom - 6, 2, 4);

        // On the wall: bookshelves in the library, the whiteboard frame, the clock face, a poster in the kitchen.
        foreach (var shelf in _shelves) Shelf(dc, shelf);
        Box(dc, Metal, _board.Left - 1, _board.Top - 1, _board.Width + 2, _board.Height + 2);
        Box(dc, Board, _board.Left, _board.Top, _board.Width, _board.Height);
        Box(dc, MetalDark, _board.Left + 6, _board.Bottom + 1, _board.Width - 12, 1);
        Box(dc, PixelArt.C("#E8574F"), _board.Left + 10, _board.Bottom, 3, 1);
        Box(dc, PixelArt.C("#4F8FE8"), _board.Left + 15, _board.Bottom, 3, 1);
        dc.DrawEllipse(Fill(PixelArt.C("#E8E4D0")), Stroke(PixelArt.C("#2A2A30"), Math.Max(1, _p)), Dip(_clock), 5.5 * _p, 5.5 * _p);
    });

    private void Checker(DrawingContext dc, Rect area, Color a, Color b, double size)
    {
        Box(dc, a, area.Left, area.Top, area.Width, area.Height);
        for (var y = area.Top; y < area.Bottom; y += size)
            for (var x = area.Left; x < area.Right; x += size)
                if (((int)((x - area.Left) / size) + (int)((y - area.Top) / size)) % 2 == 1)
                    Box(dc, b, x, y, Math.Min(size, area.Right - x), Math.Min(size, area.Bottom - y));
    }

    private void Planks(DrawingContext dc, Rect area)
    {
        Box(dc, Plank, area.Left, area.Top, area.Width, area.Height);
        var row = 0;
        for (var y = area.Top; y < area.Bottom; y += 5, row++)
        {
            Box(dc, PlankSeam, area.Left, y, area.Width, Math.Min(1, area.Bottom - y));
            for (var x = area.Left + (row % 3) * 9; x < area.Right; x += 27) Box(dc, PlankSeam, x, y, 1, Math.Min(5, area.Bottom - y));
        }
    }

    private void Shelf(DrawingContext dc, Point at)
    {
        var (x, top) = (at.X, at.Y);
        Box(dc, Shade, x + 1, WallH + 5, 30, 2);
        Box(dc, Wood, x, top, 30, 26);
        Box(dc, WoodBack, x + 2, top + 2, 26, 19);
        var seed = (int)(x * 13);
        for (var row = 0; row < 3; row++)
        {
            var floor = top + 7 + row * 7;
            var bx = x + 3;
            while (bx < x + 26)
            {
                var width = SceneWeatherFx.Noise(seed++) > 0.6 ? 3 : 2;
                var height = 4 + (SceneWeatherFx.Noise(seed++) > 0.5 ? 1 : 0);
                if (bx + width > x + 27) break;
                Box(dc, Books[(int)(SceneWeatherFx.Noise(seed++) * Books.Length)], bx, floor - height, width, height);
                bx += width + (SceneWeatherFx.Noise(seed++) > 0.8 ? 2 : 0);
            }
            Box(dc, WoodDark, x + 2, floor, 26, 1);
        }
        Box(dc, WoodDark, x, top + 21, 30, 5);
        Box(dc, Wood, x + 1, top + 22, 28, 1);
    }

    // ---- furniture that stands on the floor ------------------------------------------------------------------------

    private void BuildProps()
    {
        _props.Clear();
        foreach (var desk in _desks.Values) _props.Add(new Prop(desk.Y + 26, Bake(dc => DeskArt(dc, desk)), dc => DeskLive(dc, desk)));
        for (var i = 0; i < _terminals.Count; i++)
        {
            var (t, index) = (_terminals[i], i);
            _props.Add(new Prop(t.Y + 13, Bake(dc => TerminalArt(dc, t)), dc => TerminalLive(dc, t, index)));
        }
        for (var i = 0; i < _racks.Count; i++)
        {
            var (rack, index) = (_racks[i], i);
            _props.Add(new Prop(WallH + 5, Bake(dc => RackArt(dc, rack)), dc => RackLive(dc, rack, index)));
        }
        foreach (var cabinet in _cabinets) _props.Add(new Prop(cabinet.Y + 14, Bake(dc => CabinetArt(dc, cabinet))));
        _props.Add(new Prop(_kiosk.Y + 16, Bake(KioskArt), KioskLive));
        _props.Add(new Prop(_counter.Y + 12, Bake(CounterArt), CounterLive));
        _props.Add(new Prop(_cooler.Y + 20, Bake(CoolerArt)));
        foreach (var couch in _couches) { var at = couch; _props.Add(new Prop(at.Y + 15, Bake(dc => CouchArt(dc, at)))); }
        _props.Add(new Prop(_armchair.Y + 12, Bake(ArmchairArt)));
        _props.Add(new Prop(_printer.Y + 12, Bake(PrinterArt), PrinterLive));
        _props.Add(new Prop(_table.Y + 17, Bake(TableArt)));
        // The chairs behind the table are behind whoever sits in them; the ones in front are drawn with their sitter.
        foreach (var spot in _spots[Zone.Table].Where(s => s.Seated))
        {
            var seat = spot;
            if (seat.Facing == Facing.Down) _props.Add(new Prop(seat.Foot.Y - 3, Bake(dc => FacingChair(dc, seat.Foot))));
            else _props.Add(new Prop(seat.Foot.Y, Bake(_ => { }), dc => { if (!Occupied(seat.Key)) EmptyChair(dc, seat.Foot); }));
        }
        foreach (var plant in Plants()) { var at = plant; _props.Add(new Prop(at.Y + 8, Bake(dc => PlantArt(dc, at)))); }
    }

    private bool Occupied(string key) => _walkers.Values.Any(w => w.At(key));

    private void DeskArt(DrawingContext dc, Desk desk)
    {
        // As good as its owner's own chat has earned (WorkflowFloorView.Upgrades.cs); tier 2 is the desk below.
        if (DeskTierFor(desk) is var tier && tier != WorkflowTown.StandardTier) { DeskArtTier(dc, desk, tier); return; }
        var (x, y) = (desk.X, desk.Y);
        Box(dc, Shade, x + 5, y + 26, 26, 2);
        Box(dc, DeskTop, x + 4, y + 10, 26, 12);
        Box(dc, DeskEdge, x + 4, y + 10, 26, 1);
        Box(dc, DeskFace, x + 4, y + 22, 26, 4);
        Box(dc, DeskDark, x + 4, y + 25, 26, 1);
        Box(dc, DeskDark, x + 6, y + 23, 6, 1);
        Box(dc, DeskDark, x + 22, y + 23, 6, 1);
        // The monitor on its stand; the screen itself is drawn live.
        Box(dc, MetalDark, x + 15, y + 11, 4, 2);
        Box(dc, MetalDark, x + 13, y + 13, 8, 1);
        Box(dc, Bezel, x + 10, y + 1, 14, 11);
        Box(dc, KeyCap, x + 12, y + 16, 10, 3);
        for (var k = 0; k < 4; k++) Box(dc, KeyDot, x + 13 + k * 2, y + 17, 1, 1);
        Box(dc, KeyCap, x + 24, y + 16, 2, 3);
        // Something of its owner's on every desk.
        var h = Hash(desk.AgentId, 5);
        if (h < 0.34)
        {
            var colour = _byId.TryGetValue(desk.AgentId, out var owner) ? WorkflowGuilds.ShirtOf(owner) : PixelArt.ShirtOf(desk.AgentId);
            Box(dc, colour, x + 6, y + 13, 3, 3);
            Box(dc, PixelArt.Shade(colour, 0.7), x + 9, y + 14, 1, 1);
        }
        else if (h < 0.67)
        {
            Box(dc, Pot, x + 5, y + 14, 4, 3);
            Box(dc, Leaf, x + 4, y + 10, 6, 4);
            Box(dc, LeafHi, x + 6, y + 9, 2, 2);
        }
        else
        {
            Box(dc, Paper, x + 5, y + 12, 5, 7);
            Box(dc, PaperLine, x + 6, y + 14, 3, 1);
            Box(dc, PaperLine, x + 6, y + 16, 3, 1);
        }
    }

    private void DeskLive(DrawingContext dc, Desk desk)
    {
        _byId.TryGetValue(desk.AgentId, out var agent);
        var walker = agent is null ? null : _walkers.GetValueOrDefault(agent.Id);
        var seated = walker?.At("desk:" + desk.AgentId) == true;
        var tier = ShownTier(desk.AgentId, agent);
        Screen(dc, R(ScreenOf(desk, tier)), agent, walker, seated);
        DeskLiveTier(dc, desk, tier, agent);
        if (!seated) EmptyChair(dc, desk.Seat, tier);
        if (seated && walker!.Doing == Activity.Failed) Smoke(dc, new Point(desk.X + 22, desk.Y + 1));
    }

    private void EmptyChair(DrawingContext dc, Point seat)
    {
        Box(dc, ChairSeat, seat.X - 5, seat.Y - 8, 10, 6);
        ChairFront(dc, seat);
    }

    /// <summary>The back of a desk chair, seen from behind: in front of whoever sits in it.</summary>
    private void ChairFront(DrawingContext dc, Point seat)
    {
        Box(dc, ChairBack, seat.X - 4, seat.Y - 3, 8, 4);
        Box(dc, ChairHi, seat.X - 4, seat.Y - 3, 8, 1);
        Box(dc, ChairSeat, seat.X - 1, seat.Y + 1, 2, 1);
        Box(dc, ChairSeat, seat.X - 4, seat.Y + 2, 8, 1);
    }

    /// <summary>A chair facing the viewer, its back behind the sitter.</summary>
    private void FacingChair(DrawingContext dc, Point seat)
    {
        Box(dc, ChairBack, seat.X - 5, seat.Y - 15, 10, 9);
        Box(dc, ChairHi, seat.X - 5, seat.Y - 15, 10, 1);
        Box(dc, ChairSeat, seat.X - 5, seat.Y - 6, 10, 4);
    }

    private void TerminalArt(DrawingContext dc, Point t)
    {
        var (x, y) = (t.X, t.Y);
        Box(dc, Shade, x + 1, y + 13, 19, 1);
        Box(dc, MetalDark, x + 2, y + 4, 1, 9);
        Box(dc, MetalDark, x + 17, y + 4, 1, 9);
        Box(dc, MetalDark, x + 2, y + 9, 16, 1);
        Box(dc, Metal, x, y, 20, 4);
        Box(dc, MetalHi, x, y, 20, 1);
        Box(dc, MetalDark, x, y + 4, 20, 1);
        Box(dc, Bezel, x + 3, y - 11, 14, 11);
        Box(dc, MetalDark, x + 9, y - 1, 2, 1);
        Box(dc, KeyCap, x + 5, y + 1, 10, 2);
    }

    private void TerminalLive(DrawingContext dc, Point t, int index)
    {
        var screen = R(t.X + 4, t.Y - 10, 12, 9);
        var busy = Occupied($"terminal:{index}");
        dc.DrawRectangle(Fill(PixelArt.C("#06090C")), null, screen);
        var p = _p;
        dc.PushClip(new RectangleGeometry(screen));
        if (busy) Output(dc, screen, index);
        else
        {
            dc.DrawRectangle(Fill(Color.FromRgb(0x7F, 0xE0, 0x9A)), null, new Rect(screen.Left + p, screen.Bottom - 2.5 * p, 2 * p, Math.Max(1, p * 0.8)));
            if ((int)(_time * 2) % 2 == 0)
                dc.DrawRectangle(Fill(Color.FromRgb(0xE8, 0xEC, 0xF2)), null, new Rect(screen.Left + 4 * p, screen.Bottom - 3 * p, p, 1.6 * p));
        }
        dc.Pop();
    }

    /// <summary>Command output scrolling past on a dark screen, a line every few frames.</summary>
    private void Output(DrawingContext dc, Rect screen, int seed)
    {
        var p = _p;
        var scroll = Math.Floor(_time * 5 + seed * 3);
        for (var row = 0; row < (int)(screen.Height / (1.7 * p)); row++)
        {
            var line = (int)scroll + row + seed * 50;
            var length = (2 + Math.Floor(SceneWeatherFx.Noise(line) * 8)) * p;
            var ink = SceneWeatherFx.Noise(line + 7) < 0.7 ? Color.FromRgb(0x7F, 0xE0, 0x9A) : Color.FromRgb(0xE8, 0xEC, 0xF2);
            dc.DrawRectangle(Fill(ink), null, new Rect(screen.Left + p, screen.Top + p * 0.5 + row * 1.7 * p, Math.Min(length, screen.Width - 2 * p), Math.Max(1, p * 0.8)));
        }
    }

    private void RackArt(DrawingContext dc, Point at)
    {
        var (x, y) = (at.X, at.Y);
        Box(dc, Shade, x + 1, WallH + 5, 15, 2);
        Box(dc, Rack, x, y, 14, 28);
        Box(dc, RackFace, x + 1, y + 1, 12, 24);
        for (var slot = 0; slot < 6; slot++) Box(dc, RackSlot, x + 1, y + 4 + slot * 4, 12, 1);
        Box(dc, MetalDark, x + 1, y + 26, 12, 2);
    }

    private void RackLive(DrawingContext dc, Point at, int index)
    {
        // Lights blink faster while someone is at a terminal.
        var busy = _spots[Zone.Terminal].Any(s => Occupied(s.Key));
        var rate = busy ? 7 : 1.5;
        for (var slot = 0; slot < 6; slot++)
            for (var led = 0; led < 3; led++)
            {
                var seed = index * 97 + slot * 13 + led * 5;
                var on = SceneWeatherFx.Noise(seed + (int)Math.Floor(_time * rate + SceneWeatherFx.Noise(seed) * 3)) > 0.35;
                var color = led == 2 ? Color.FromRgb(0x7F, 0xB8, 0xE8) : led == 1 && slot % 3 == 2 ? Color.FromRgb(0xF2, 0xB8, 0x4B) : Color.FromRgb(0x5F, 0xD3, 0x8D);
                Box(dc, on ? color : Alpha(color, 0.25), at.X + 3 + led * 3, at.Y + 2 + slot * 4, 1, 1);
            }
    }

    private void CabinetArt(DrawingContext dc, Point at)
    {
        var (x, y) = (at.X, at.Y);
        Box(dc, Shade, x + 1, y + 14, 10, 1);
        Box(dc, SteelHi, x, y, 10, 3);
        Box(dc, Steel, x, y + 3, 10, 11);
        for (var drawer = 0; drawer < 3; drawer++)
        {
            Box(dc, SteelLine, x, y + 6 + drawer * 4, 10, 1);
            Box(dc, KeyCap, x + 4, y + 4 + drawer * 4, 2, 1);
        }
    }

    private void KioskArt(DrawingContext dc)
    {
        // Screens held up on posts, above the head of whoever stands at them.
        var (x, y) = (_kiosk.X, _kiosk.Y);
        Box(dc, Shade, x + 3, y + 16, 22, 1);
        Box(dc, MetalDark, x + 6, y + 4, 2, 12);
        Box(dc, MetalDark, x + 18, y + 4, 2, 12);
        Box(dc, MetalDark, x + 4, y + 15, 6, 1);
        Box(dc, MetalDark, x + 16, y + 15, 6, 1);
        Box(dc, Bezel, x, y - 5, 26, 9);
        Box(dc, MetalDark, x, y + 3, 26, 1);
    }

    private void KioskLive(DrawingContext dc)
    {
        for (var i = 0; i < 2; i++)
        {
            var screen = R(_kiosk.X + 2 + i * 12, _kiosk.Y - 4, 10, 7);
            var busy = Occupied($"kiosk:{i}");
            dc.DrawRectangle(Fill(busy ? PixelArt.C("#1E4C8A") : PixelArt.C("#16243A")), null, screen);
            // A globe, turning while someone browses.
            var center = new Point(screen.Left + screen.Width / 2, screen.Top + screen.Height / 2);
            var radius = Math.Min(screen.Width, screen.Height) * 0.36;
            var ink = busy ? Color.FromRgb(0x9F, 0xD8, 0xFF) : Color.FromRgb(0x4A, 0x62, 0x86);
            dc.DrawEllipse(null, Stroke(ink, Math.Max(1, _p * 0.5)), center, radius, radius);
            var turn = busy ? Math.Sin(_time * 2.2 + i) : 0.4;
            dc.DrawEllipse(null, Stroke(ink, Math.Max(1, _p * 0.4)), center, Math.Abs(turn) * radius, radius);
            dc.DrawLine(Stroke(ink, Math.Max(1, _p * 0.4)), new Point(center.X - radius, center.Y), new Point(center.X + radius, center.Y));
        }
    }

    private void CounterArt(DrawingContext dc)
    {
        var (x, y) = (_counter.X, _counter.Y);
        Box(dc, Shade, x + 1, y + 12, 38, 2);
        Box(dc, Counter, x, y, 38, 6);
        Box(dc, PixelArt.C("#ECE8DC"), x, y, 38, 1);
        Box(dc, CounterFace, x, y + 6, 38, 6);
        for (var door = 1; door < 4; door++) Box(dc, CounterLine, x + door * 9.5, y + 6, 1, 6);
        for (var door = 0; door < 4; door++) Box(dc, KeyCap, x + 4 + door * 9.5, y + 8, 2, 1);
        // The coffee machine, a stack of mugs and the microwave.
        Box(dc, Machine, x + 4, y - 8, 10, 11);
        Box(dc, PixelArt.C("#B8BCC4"), x + 5, y - 7, 8, 3);
        Box(dc, PixelArt.C("#E8574F"), x + 12, y - 7, 1, 1);
        Box(dc, PixelArt.C("#1A1C20"), x + 7, y - 2, 4, 3);
        Box(dc, Paper, x + 8, y, 2, 2);
        Box(dc, PixelArt.C("#4F8FE8"), x + 17, y + 1, 2, 2);
        Box(dc, PixelArt.C("#E8574F"), x + 17, y - 1, 2, 2);
        Box(dc, PixelArt.C("#F0A030"), x + 20, y + 1, 2, 2);
        Box(dc, PixelArt.C("#E8E4D8"), x + 25, y - 6, 11, 8);
        Box(dc, PixelArt.C("#2A2E36"), x + 26, y - 5, 6, 5);
        Box(dc, PixelArt.C("#9AA0A8"), x + 33, y - 5, 2, 1);
        Box(dc, PixelArt.C("#9AA0A8"), x + 33, y - 3, 2, 1);
    }

    private void CounterLive(DrawingContext dc)
    {
        // Steam off the coffee while someone waits for a cup.
        if (!_spots[Zone.Lounge].Any(s => s.Look == "mug" && Occupied(s.Key))) return;
        for (var i = 0; i < 3; i++)
        {
            var t = (_time * 0.7 + i / 3.0) % 1;
            var alpha = (byte)(150 * (1 - t));
            Box(dc, Color.FromArgb(alpha, 0xF4, 0xF6, 0xFA), _counter.X + 8 + Math.Sin(_time * 3 + i * 2) * 1.5, _counter.Y - 9 - t * 9, 1.5, 1.5);
        }
    }

    private void CoolerArt(DrawingContext dc)
    {
        var (x, y) = (_cooler.X, _cooler.Y);
        Box(dc, Shade, x + 1, y + 20, 9, 1);
        Box(dc, PixelArt.C("#7FB8E8"), x + 1, y, 7, 7);
        Box(dc, PixelArt.C("#B8DCF8"), x + 2, y + 1, 2, 4);
        Box(dc, PixelArt.C("#D8DCE4"), x, y + 7, 9, 13);
        Box(dc, PixelArt.C("#B8BCC4"), x, y + 7, 9, 1);
        Box(dc, PixelArt.C("#4F8FE8"), x + 2, y + 10, 2, 2);
        Box(dc, PixelArt.C("#E8574F"), x + 5, y + 10, 2, 2);
    }

    private void CouchArt(DrawingContext dc, Point at)
    {
        var (x, y) = (at.X, at.Y);
        Box(dc, Shade, x + 1, y + 15, 44, 2);
        Box(dc, CouchDark, x, y, 44, 7);
        Box(dc, CouchHi, x + 1, y, 42, 1);
        Box(dc, Couch, x + 3, y + 7, 38, 5);
        Box(dc, CouchDark, x + 15.5, y + 7, 1, 5);
        Box(dc, CouchDark, x + 28.5, y + 7, 1, 5);
        Box(dc, CouchDark, x, y + 3, 4, 12);
        Box(dc, CouchDark, x + 40, y + 3, 4, 12);
        Box(dc, CouchHi, x, y + 3, 4, 1);
        Box(dc, CouchHi, x + 40, y + 3, 4, 1);
        Box(dc, PixelArt.C("#2F4A7E"), x + 3, y + 12, 38, 3);
        Box(dc, PixelArt.C("#F0A030"), x + 5, y + 3, 5, 4);
        Box(dc, PixelArt.C("#E86FA4"), x + 34, y + 3, 5, 4);
    }

    private void ArmchairArt(DrawingContext dc)
    {
        var (x, y) = (_armchair.X, _armchair.Y);
        Box(dc, Shade, x + 1, y + 12, 14, 2);
        Box(dc, PixelArt.C("#7A3E4A"), x, y, 14, 7);
        Box(dc, PixelArt.C("#94505E"), x + 1, y, 12, 1);
        Box(dc, PixelArt.C("#8E4A58"), x + 2, y + 6, 10, 4);
        Box(dc, PixelArt.C("#6A3440"), x, y + 3, 3, 9);
        Box(dc, PixelArt.C("#6A3440"), x + 11, y + 3, 3, 9);
        Box(dc, PixelArt.C("#5A2C36"), x + 2, y + 10, 10, 2);
        // A side table with a reading lamp.
        Box(dc, Shade, x + 17, y + 12, 7, 1);
        Box(dc, Wood, x + 17, y + 5, 7, 4);
        Box(dc, WoodDark, x + 17, y + 9, 7, 3);
        Box(dc, MetalDark, x + 20, y - 2, 1, 7);
        Box(dc, PixelArt.C("#F5E2A8"), x + 18, y - 5, 5, 3);
        Box(dc, PixelArt.C("#E8574F"), x + 18, y + 6, 3, 1);
    }

    private void PrinterArt(DrawingContext dc)
    {
        var (x, y) = (_printer.X, _printer.Y);
        Box(dc, Shade, x + 1, y + 12, 18, 1);
        Box(dc, PixelArt.C("#D8DCE4"), x, y, 18, 5);
        Box(dc, PixelArt.C("#B8BCC4"), x, y + 5, 18, 7);
        Box(dc, PixelArt.C("#8A9098"), x + 2, y + 7, 14, 1);
        Box(dc, PixelArt.C("#8A9098"), x + 2, y + 9, 14, 1);
        Box(dc, PixelArt.C("#3A3E46"), x + 12, y + 1, 4, 2);
    }

    private void PrinterLive(DrawingContext dc)
    {
        // Pages roll out while someone waits at the printer; the light blinks either way.
        var (x, y) = (_printer.X, _printer.Y);
        var busy = Occupied("printer:0");
        Box(dc, (int)(_time * (busy ? 4 : 1)) % 2 == 0 ? PixelArt.C("#5FD38D") : PixelArt.C("#2E6A46"), x + 13, y + 1, 1, 1);
        var page = busy ? 1 + (_time * 2 % 1) * 4 : 1;
        Box(dc, Paper, x + 3, y + 1, 7, page);
        Box(dc, PaperLine, x + 4, y + 1, 5, Math.Min(1, page));
    }

    private void TableArt(DrawingContext dc)
    {
        var (x, y) = (_table.X, _table.Y);
        dc.DrawRoundedRectangle(Fill(Shade), null, R(x + 2, y + 15, 46, 3), 2 * _p, 2 * _p);
        dc.DrawRoundedRectangle(Fill(TableFace), null, R(x, y + 3, 48, 14), 5 * _p, 5 * _p);
        dc.DrawRoundedRectangle(Fill(Table), null, R(x, y, 48, 14), 5 * _p, 5 * _p);
        dc.DrawRoundedRectangle(Fill(TableHi), null, R(x + 3, y + 1, 42, 2), _p, _p);
        // A laptop, some papers and a jug of water.
        Box(dc, Paper, x + 9, y + 5, 6, 4);
        Box(dc, PaperLine, x + 10, y + 6, 4, 1);
        Box(dc, MetalDark, x + 30, y + 4, 9, 5);
        Box(dc, PixelArt.C("#7FB8E8"), x + 31, y + 5, 7, 3);
        Box(dc, PixelArt.C("#B8DCF8"), x + 22, y + 4, 3, 5);
    }

    private void PlantArt(DrawingContext dc, Point at)
    {
        var (x, y) = (at.X, at.Y);
        Box(dc, Shade, x, y + 7, 9, 1);
        Box(dc, Leaf, x - 2, y - 7, 12, 8);
        Box(dc, Leaf, x + 1, y - 11, 6, 5);
        Box(dc, LeafHi, x - 1, y - 6, 3, 3);
        Box(dc, LeafHi, x + 5, y - 9, 3, 3);
        Box(dc, LeafHi, x + 2, y - 3, 2, 2);
        Box(dc, Pot, x, y, 8, 7);
        Box(dc, PotDark, x, y, 8, 1);
        Box(dc, PotDark, x + 1, y + 6, 6, 1);
    }

    // ---- screens --------------------------------------------------------------------------------------------------

    private void Screen(DrawingContext dc, Rect screen, AgentActivityNode? agent, Walker? walker, bool seated)
    {
        var p = _p;
        var h = agent is null ? 0 : Hash(agent.Id);
        if (agent is null || walker is null) { dc.DrawRectangle(Fill(PixelArt.C("#0A0C10")), null, screen); return; }
        dc.PushClip(new RectangleGeometry(screen));
        if (!seated)
        {
            if (agent.Status == "Completed")
            {
                dc.DrawRectangle(Fill(PixelArt.C("#12261A")), null, screen);
                var g = Fill(PixelArt.C("#5FD38D"));
                var (cx, cy) = (screen.Left + screen.Width / 2 - 2.5 * p, screen.Top + screen.Height / 2 - p);
                for (var i = 0; i < 5; i++) dc.DrawRectangle(g, null, new Rect(cx + i * p, cy + (i < 2 ? i : 4 - i) * p - (i >= 2 ? p : 0), p, p));
            }
            else
            {
                // Locked while its owner is away: a dot drifting round the screen.
                dc.DrawRectangle(Fill(PixelArt.C("#18233A")), null, screen);
                var bx = Bounce(_time * 3 + h * 10, screen.Width - p);
                var by = Bounce(_time * 2.2 + h * 7, screen.Height - p);
                dc.DrawRectangle(Fill(PixelArt.C("#7C8CF0")), null, new Rect(Math.Round(screen.Left + bx), Math.Round(screen.Top + by), p, p));
            }
            dc.Pop();
            return;
        }
        switch (walker.Doing)
        {
            case Activity.Shell:
                // A command run from the desk because every server-room terminal was taken.
                dc.DrawRectangle(Fill(PixelArt.C("#06090C")), null, screen);
                Output(dc, screen, (int)(h * 50));
                break;
            case Activity.Reading or Activity.Browsing:
            {
                // A page scrolling slowly past, the scrollbar creeping down: a file, or the web with the kiosks taken.
                dc.DrawRectangle(Fill(PixelArt.C("#E8ECF2")), null, screen);
                var scroll = (_time * 1.5 * p) % (2 * p);
                var top = (int)Math.Floor(_time * 0.75);
                for (var row = 0; row < 6; row++)
                {
                    var length = (3 + Math.Floor(SceneWeatherFx.Noise(top + row + (int)(h * 500)) * 6)) * p;
                    dc.DrawRectangle(Fill(PixelArt.C("#8A94A6")), null, new Rect(screen.Left + p, screen.Top + p - scroll + row * 2 * p, Math.Min(length, screen.Width - 3.5 * p), Math.Max(1, p * 0.7)));
                }
                var thumb = (_time * 0.08 + h) % 1 * (screen.Height - 3 * p);
                dc.DrawRectangle(Fill(PixelArt.C("#A8B0BE")), null, new Rect(screen.Right - 1.5 * p, screen.Top + thumb, p, 3 * p));
                break;
            }
            case Activity.Replying:
            {
                // Chat bubbles rising as the reply is written.
                dc.DrawRectangle(Fill(PixelArt.C("#141B26")), null, screen);
                var rise = (_time * 2.4 * p + h * 50) % (3 * p);
                for (var row = 0; row < 4; row++)
                {
                    var mine = ((int)Math.Floor(_time * 0.8 + h * 9) + row) % 2 == 0;
                    var width = (4 + Math.Floor(SceneWeatherFx.Noise(row + (int)(_time * 0.8) + (int)(h * 300)) * 4)) * p;
                    var y = screen.Bottom - 2 * p - row * 3 * p + rise - 3 * p;
                    var x = mine ? screen.Right - p - width : screen.Left + p;
                    dc.DrawRectangle(Fill(mine ? PixelArt.C("#4F8FE8") : PixelArt.C("#3A4456")), null, new Rect(x, y, width, 2 * p));
                }
                break;
            }
            case Activity.Failed:
                dc.DrawRectangle(Fill(PixelArt.C("#B83A3A")), null, screen);
                dc.DrawRectangle(Fill(Colors.White), null, new Rect(screen.Left + screen.Width / 2 - p / 2, screen.Top + p, p, 4 * p));
                dc.DrawRectangle(Fill(Colors.White), null, new Rect(screen.Left + screen.Width / 2 - p / 2, screen.Top + 6 * p, p, p));
                break;
            case Activity.Asleep:
                dc.DrawRectangle(Fill(PixelArt.C("#06080B")), null, screen);
                if ((int)(_time * 0.8) % 2 == 0) dc.DrawRectangle(Fill(PixelArt.C("#5FD38D")), null, new Rect(screen.Right - 2 * p, screen.Bottom - 2 * p, p, p));
                break;
            default:
                Code(dc, screen, h, live: walker.Doing != Activity.Thinking);
                break;
        }
        dc.Pop();
    }

    /// <summary>An editor: coloured lines of code, the newest growing as it is typed and the file scrolling up line by line.</summary>
    private void Code(DrawingContext dc, Rect screen, double h, bool live)
    {
        var p = _p;
        dc.DrawRectangle(Fill(PixelArt.C("#0E1A24")), null, screen);
        var tick = live ? Math.Floor(_time * 1.3 + h * 40) : Math.Floor(h * 40);
        var rows = (int)(screen.Height / (2 * p));
        for (var row = 0; row < rows; row++)
        {
            var seed = (int)tick + row + (int)(h * 997);
            var indent = Math.Floor(SceneWeatherFx.Noise(seed * 3) * 3) * p;
            var length = (2 + Math.Floor(SceneWeatherFx.Noise(seed * 3 + 1) * 6)) * p;
            if (live && row == rows - 1) length = Math.Max(p, Math.Floor((_time * 1.3 + h * 40) % 1 * 7) * p);
            var ink = SceneWeatherFx.Noise(seed * 3 + 2) switch
            {
                < 0.3 => Color.FromRgb(0x7F, 0xD1, 0xFF), < 0.55 => Color.FromRgb(0xA8, 0xF0, 0xBE), < 0.75 => Color.FromRgb(0xF5, 0xCB, 0x5C), _ => Color.FromRgb(0xE8, 0xEC, 0xF2),
            };
            var y = screen.Top + p * 0.6 + row * 2 * p;
            dc.DrawRectangle(Fill(ink), null, new Rect(screen.Left + p + indent, y, Math.Min(length, screen.Width - 2 * p - indent), Math.Max(1, p * 0.8)));
            if (live && row == rows - 1 && (int)(_time * 3) % 2 == 0)
                dc.DrawRectangle(Fill(Colors.White), null, new Rect(Math.Min(screen.Right - 2 * p, screen.Left + p + indent + length + p * 0.5), y - p * 0.3, Math.Max(1, p * 0.5), 1.4 * p));
        }
        if (!live) dc.DrawRectangle(Fill(Color.FromArgb(70, 0, 0, 0)), null, screen);
    }

    private void Smoke(DrawingContext dc, Point at)
    {
        for (var i = 0; i < 3; i++)
        {
            var t = (_time * 0.5 + i / 3.0) % 1;
            var size = 2 + t * 3;
            Box(dc, Color.FromArgb((byte)(130 * (1 - t)), 0x8A, 0x8E, 0x96), at.X - size / 2 + Math.Sin(_time * 2 + i) * 2, at.Y - t * 12, size, size);
        }
    }

    // ---- the wall, live: windows, whiteboard, clock --------------------------------------------------------------

    private void DrawWall(DrawingContext dc, double time)
    {
        for (var i = 0; i < _windows.Count; i++) DrawWindow(dc, _windows[i], i, time);
        // Clock hands follow your local time.
        var face = Dip(_clock);
        var now = DateTime.Now;
        var hand = Stroke(PixelArt.C("#2A2A30"), Math.Max(1, _p * 0.7));
        var hour = (now.Hour % 12 + now.Minute / 60.0) / 12 * Math.PI * 2;
        var minute = now.Minute / 60.0 * Math.PI * 2;
        dc.DrawLine(hand, face, new Point(face.X + Math.Sin(hour) * 2.8 * _p, face.Y - Math.Cos(hour) * 2.8 * _p));
        dc.DrawLine(hand, face, new Point(face.X + Math.Sin(minute) * 4.2 * _p, face.Y - Math.Cos(minute) * 4.2 * _p));
        DrawBoard(dc);
    }

    private void DrawWindow(DrawingContext dc, Rect window, int index, double time)
    {
        var w = R(window);
        var sky = new LinearGradientBrush(Sky.Top, Sky.Bottom, 90);
        sky.Freeze();
        dc.PushClip(new RectangleGeometry(w));
        dc.DrawRectangle(sky, null, w);
        // The row of windows looks out on one sky: the sun or moon crosses the whole row and shows in the window it is
        // behind, or behind the wall between two. Placed per window, every window had one in the same spot.
        var (ox, oy, sun) = Sky.Orb();
        var row = R(new Rect(_windows[0].Left, window.Top, _windows[^1].Right - _windows[0].Left, window.Height));
        dc.PushOpacity(SceneWeatherFx.OrbVeil(Sky));
        dc.DrawEllipse(Fill(sun ? Color.FromRgb(0xFF, 0xE2, 0x7A) : Color.FromRgb(0xEE, 0xEC, 0xDC)), null,
            new Point(row.Left + row.Width * ox, w.Top + w.Height * oy), 2.5 * _p, 2.5 * _p);
        dc.Pop();
        if (Sky.Night && Sky.Weather is SceneWeather.Clear or SceneWeather.Cloudy)
            for (var i = 0; i < 6; i++)
                dc.DrawRectangle(Fill(Color.FromArgb(200, 0xF4, 0xF1, 0xDC)), null,
                    new Rect(w.Left + SceneWeatherFx.Noise(i + 40 + (int)window.Left) * w.Width, w.Top + SceneWeatherFx.Noise(i + 50 + (int)window.Left) * w.Height * 0.7, 2, 2));
        SceneWeatherFx.Draw(dc, w, Sky, time, w.Bottom, Fill, Stroke, index + 1);
        var flash = SceneWeatherFx.Lightning(Sky, time, out _);
        if (flash > 0) dc.DrawRectangle(Fill(Color.FromArgb((byte)(flash * 200), 0xEE, 0xF2, 0xFF)), null, w);
        dc.Pop();
        var frame = Fill(PixelArt.C("#C8CCD4"));
        dc.DrawRectangle(null, new Pen(frame, Math.Max(1, _p)), Offset(w, 0, 0));
        dc.DrawRectangle(frame, null, new Rect(w.Left + w.Width / 2 - _p / 2, w.Top, _p, w.Height));
        dc.DrawRectangle(frame, null, new Rect(w.Left, w.Top + w.Height / 2 - _p / 2, w.Width, _p));
        Box(dc, PixelArt.C("#D8DCE4"), window.Left - 1, window.Bottom, window.Width + 2, 2);
    }

    /// <summary>The whiteboard carries the busiest agent's todo list, or sticky notes for who is working, waiting and done.</summary>
    private void DrawBoard(DrawingContext dc)
    {
        var b = _board;
        var p = _p;
        var planner = _shown.Where(a => a.PlanTotal > 0).OrderByDescending(a => a.State == AgentActivityState.Working).FirstOrDefault();
        if (planner is not null)
        {
            var title = Text($"{planner.Label} · {planner.PlanDone}/{planner.PlanTotal}", Math.Clamp(3.2 * p, 9, 12), PixelArt.C("#2A3240"), bold: true, maxWidth: b.Width * p - 4 * p);
            var at = Dip(new Point(b.Left + 2, b.Top + 1));
            dc.DrawText(title, at);
            var top = b.Top + 1 + title.Height / p + 1;
            var rows = Math.Min(planner.PlanTotal, Math.Max(1, (int)((b.Bottom - top - 1) / 3)));
            for (var i = 0; i < rows; i++)
            {
                var done = i < planner.PlanDone;
                Box(dc, done ? PixelArt.C("#3FA45B") : PixelArt.C("#9AA4B4"), b.Left + 2, top + i * 3, 2, 2);
                Box(dc, done ? PixelArt.C("#B8C4B8") : PixelArt.C("#5A6478"), b.Left + 6, top + i * 3 + 0.5, (b.Width - 9) * (0.45 + 0.5 * Hash(planner.Id, i)), 1);
            }
            return;
        }
        // Three columns of stickies: working, needs you, done.
        var columns = new[]
        {
            (_shown.Count(a => a.State == AgentActivityState.Working), PixelArt.C("#A8F0BE")),
            (_shown.Count(a => a.State == AgentActivityState.Waiting), PixelArt.C("#F5CB5C")),
            (_shown.Count(a => a.Status == "Completed"), PixelArt.C("#9FD0F0")),
        };
        for (var c = 0; c < 3; c++)
        {
            var x = b.Left + 2 + c * 16;
            Box(dc, PixelArt.C("#C8CCD4"), x + 14, b.Top + 2, 0.5, b.Height - 4);
            Box(dc, PixelArt.C("#5A6478"), x + 2, b.Top + 2, 10, 1);
            var (count, color) = columns[c];
            for (var i = 0; i < Math.Min(count, 6); i++)
                Box(dc, color, x + 1 + i % 2 * 6, b.Top + 5 + i / 2 * 5, 5, 4);
        }
    }

    /// <summary>Signs on the walls name the rooms: over the shelves and racks at the back, along the front wall below.</summary>
    private void DrawZoneSigns(DrawingContext dc)
    {
        if (_p < 2) return;
        var size = Math.Clamp(2.7 * _p, 8.5, 11.5);
        void Sign(string text, double centerX, double y)
        {
            var label = Text(text, size, Color.FromRgb(0xE8, 0xEC, 0xF4), bold: true);
            var at = Dip(new Point(centerX, y));
            var plate = new Rect(Math.Round(at.X - label.Width / 2 - 6), at.Y, Math.Round(label.Width + 12), Math.Round(label.Height + 1));
            dc.DrawRoundedRectangle(Fill(Color.FromArgb(170, 0x12, 0x16, 0x20)), null, plate, 3, 3);
            dc.DrawText(label, new Point(plate.Left + 6, plate.Top));
        }
        Sign("LIBRARY", _library.Left + _library.Width / 2, 2);
        Sign("SERVER ROOM", _server.Left + _server.Width / 2, 1);
        Sign("KITCHEN", _kitchen.Left + _kitchen.Width / 2, _fh - BottomWall + 0.5);
        Sign("MEETING ROOM", _meeting.Left + _meeting.Width / 2, _fh - BottomWall + 0.5);
    }

    private void DrawPlates(DrawingContext dc, AgentActivityProject? hovered)
    {
        foreach (var pod in _pods)
        {
            var size = Math.Clamp(3.6 * _p, 10, 14);
            // The plate stays inside its pod: the count goes first when the name needs the room.
            var room = (pod.Area.Width - 4) * _p;
            var name = Text(pod.Project.Name, size, Color.FromRgb(0xF2, 0xF4, 0xF8), bold: true, maxWidth: room - 16);
            var working = pod.Project.Working;
            var count = Text(working > 0 ? $"{working} working" : pod.Project.Agents.Count == 1 ? "1 agent" : $"{pod.Project.Agents.Count} agents",
                Math.Clamp(3 * _p, 9, 12), working > 0 ? Color.FromRgb(0xA8, 0xF0, 0xBE) : Color.FromRgb(0xC8, 0xCE, 0xDA));
            var both = name.Width + count.Width + 26 <= room;
            var at = Dip(new Point(pod.Area.Left + 2, pod.Area.Top));
            var box = new Rect(at.X, at.Y, both ? name.Width + count.Width + 26 : name.Width + 16, Math.Max(name.Height, count.Height) + 4);
            var mine = hovered?.Key == pod.Project.Key;
            dc.DrawRoundedRectangle(Fill(Color.FromArgb(mine ? (byte)235 : (byte)205, 0x16, 0x1A, 0x22)), mine ? Stroke(Ink("Accent", Color.FromRgb(0x7C, 0xB7, 0xFF)), 1.5) : null, box, 4, 4);
            dc.DrawText(name, new Point(box.Left + 8, box.Top + 2));
            if (both) dc.DrawText(count, new Point(box.Left + 18 + name.Width, box.Top + 2 + (name.Height - count.Height) / 2));
        }
    }

    // ---- sprites ------------------------------------------------------------------------------------------------
    // Ten art pixels wide and fourteen tall, drawn with a one-pixel outline, so twelve by sixteen on the floor with the
    // feet at the bottom centre. h hair, s skin, e eye, c shirt, d sleeve, p trousers, b shoes, m mug. The body keeps to
    // columns 1-8; columns 0 and 9 are for arms. Seated poses leave the legs out: the chair hides them.

    private static readonly string[] FrontRows =
    [
        "...hhhh...",
        "..hhhhhh..",
        ".hhhhhhhh.",
        ".hhsssshh.",
        "..sesses..",
        "..ssssss..",
        "...ssss...",
        "..dccccd..",
        ".dccccccd.",
        ".sccccccs.",
        "..pppppp..",
        "..pp..pp..",
        "..pp..pp..",
        "..bb..bb..",
    ];

    private static readonly string[] BackRows =
    [
        "...hhhh...",
        "..hhhhhh..",
        ".hhhhhhhh.",
        ".hhhhhhhh.",
        "..hhhhhh..",
        "..hhhhhh..",
        "...ssss...",
        "..dccccd..",
        ".dccccccd.",
        ".sccccccs.",
        "..pppppp..",
        "..pp..pp..",
        "..pp..pp..",
        "..bb..bb..",
    ];

    private static readonly string[] SideRows =
    [
        "...hhhh...",
        "..hhhhhh..",
        ".hhhhhhhh.",
        ".sshhhhhh.",
        ".sesshhhh.",
        ".ssssshh..",
        "..ssss....",
        "...dccc...",
        "...dccc...",
        "...sccc...",
        "...pppp...",
        "...pp.p...",
        "...pp.p...",
        "..bbb.bb..",
    ];

    private static string[] With(string[] rows, params (int Row, string Text)[] changes)
    {
        var copy = (string[])rows.Clone();
        foreach (var (row, text) in changes) copy[row] = text;
        return copy;
    }

    private static readonly Dictionary<string, string[]> Sprites = BuildSprites();

    private static Dictionary<string, string[]> BuildSprites()
    {
        const string none = "..........";
        var raw = new Dictionary<string, string[]>
        {
            ["front"] = FrontRows,
            ["front-walk1"] = With(FrontRows, (12, "..pp..bb.."), (13, "..bb......")),
            ["front-walk2"] = With(FrontRows, (12, "..bb..pp.."), (13, "......bb..")),
            ["back"] = BackRows,
            ["back-walk1"] = With(BackRows, (12, "..pp..bb.."), (13, "..bb......")),
            ["back-walk2"] = With(BackRows, (12, "..bb..pp.."), (13, "......bb..")),
            ["side"] = SideRows,
            ["side-walk"] = With(SideRows, (10, "...pppp..."), (11, "..pp..pp.."), (12, ".pp....pp."), (13, "bb.....bb.")),
            ["mug"] = With(FrontRows, (8, ".dcccccmm."), (9, ".scccccmm.")),
            ["wave1"] = With(FrontRows, (0, "...hhhh..s"), (1, "..hhhhhh.s"), (2, ".hhhhhhhhd"), (3, ".hhsssshhd"), (4, "..sesses.d"),
                (5, "..ssssss.d"), (6, "...ssss..d"), (7, "..dccccdd."), (8, ".dcccccc.."), (9, ".scccccc..")),
            ["wave2"] = With(FrontRows, (1, "..hhhhhh.."), (2, ".hhhhhhhhs"), (3, ".hhsssshhs"), (4, "..sesses.d"),
                (5, "..ssssss.d"), (6, "...ssss..d"), (7, "..dccccdd."), (8, ".dcccccc.."), (9, ".scccccc..")),
            ["term1"] = With(BackRows, (7, "s.dccccd.."), (8, ".dccccccd."), (9, "..cccccc.s")),
            ["term2"] = With(BackRows, (7, "..dccccd.s"), (8, ".dccccccd."), (9, "s.cccccc..")),
            ["think"] = With(BackRows, (2, ".hhhhhhhhs"), (3, ".hhhhhhhhs"), (4, "..hhhhhhd."), (5, "..hhhhhhd."), (6, "...ssss.d."),
                (7, "..dccccdd."), (8, ".dcccccc.."), (9, ".scccccc..")),
            ["reach"] = With(BackRows, (0, "...hhhh..s"), (1, "..hhhhhh.s"), (2, ".hhhhhhhhd"), (3, ".hhhhhhhhd"), (4, "..hhhhhh.d"),
                (5, "..hhhhhh.d"), (6, "...ssss..d"), (7, "..dccccdd."), (8, ".dcccccc.."), (9, ".scccccc..")),
            ["sit-type1"] = With(BackRows, (7, "s.dccccd.."), (8, ".dccccccd."), (9, "..cccccc.s"), (10, "..cccccc.."), (11, none), (12, none), (13, none)),
            ["sit-type2"] = With(BackRows, (7, "..dccccd.s"), (8, ".dccccccd."), (9, "s.cccccc.."), (10, "..cccccc.."), (11, none), (12, none), (13, none)),
            ["sit-read"] = With(BackRows, (7, "..dccccd.."), (8, ".dccccccds"), (9, ".scccccc.."), (10, "..cccccc.."), (11, none), (12, none), (13, none)),
            ["sit-think"] = With(BackRows, (2, ".hhhhhhhhs"), (3, ".hhhhhhhhs"), (4, "..hhhhhhd."), (5, "..hhhhhhd."), (6, "...ssss.d."),
                (7, "..dccccdd."), (8, ".dcccccc.."), (9, ".scccccc.."), (10, "..cccccc.."), (11, none), (12, none), (13, none)),
            ["sit-sleep"] = [none, none, none, "...hhhh...", "..hhhhhh..", ".shhhhhhs.", "sdhhhhhhds", ".dccccccd.", "..cccccc..", "..cccccc..", "..cccccc..", none, none, none],
            ["sit-slump"] = With(BackRows, (1, ".shhhhhhs."), (2, ".dhhhhhhd."), (3, ".dhhhhhhd."), (4, "..dhhhhd.."), (7, "..cccccc.."),
                (8, ".cccccccc."), (9, ".cccccccc."), (10, "..cccccc.."), (11, none), (12, none), (13, none)),
            ["sit-front"] = With(FrontRows, (10, ".pppppppp."), (11, "..pp..pp.."), (12, "..bb..bb.."), (13, none)),
            ["sit-mug"] = With(FrontRows, (8, ".dcccccmm."), (9, ".scccccmm."), (10, ".pppppppp."), (11, "..pp..pp.."), (12, "..bb..bb.."), (13, none)),
            ["sit-book"] = With(FrontRows, (8, ".dwwwwwwd."), (9, ".swwwwwws."), (10, ".pppppppp."), (11, "..pp..pp.."), (12, "..bb..bb.."), (13, none)),
            // A teammate finished: hands wide, then together; seated ones throw both arms up behind their monitors.
            ["clap1"] = With(FrontRows, (7, ".sdccccds."), (9, "..cccccc..")),
            ["clap2"] = With(FrontRows, (8, "..dcsscd.."), (9, "..cccccc..")),
            ["sit-cheer1"] = With(BackRows, (0, "s..hhhh..s"), (1, "d.hhhhhh.d"), (2, "dhhhhhhhhd"), (3, "dhhhhhhhhd"), (4, "d.hhhhhh.d"),
                (5, "d.hhhhhh.d"), (6, "d..ssss..d"), (7, ".ddccccdd."), (8, ".cccccccc."), (9, ".cccccccc."), (10, "..cccccc.."), (11, none), (12, none), (13, none)),
            ["sit-cheer2"] = With(BackRows, (2, "shhhhhhhhs"), (3, "dhhhhhhhhd"), (4, "d.hhhhhh.d"), (5, "d.hhhhhh.d"), (6, "d..ssss..d"),
                (7, ".ddccccdd."), (8, ".cccccccc."), (9, ".cccccccc."), (10, "..cccccc.."), (11, none), (12, none), (13, none)),
        };
        var art = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var (name, rows) in raw)
        {
            var outlined = Outlined(rows);
            art[name] = outlined;
            art[name + ":f"] = outlined.Select(r => new string(r.Reverse().ToArray())).ToArray();
        }
        return art;
    }

    /// <summary>The template with a dark one-pixel outline round its silhouette, so workers read on any floor.</summary>
    private static string[] Outlined(string[] rows)
    {
        var height = rows.Length + 2;
        var width = rows.Max(r => r.Length) + 2;
        var grid = new char[height, width];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                grid[y, x] = y > 0 && y <= rows.Length && x > 0 && x <= rows[y - 1].Length ? rows[y - 1][x - 1] : '.';
        var result = new string[height];
        for (var y = 0; y < height; y++)
        {
            var line = new char[width];
            for (var x = 0; x < width; x++)
            {
                var c = grid[y, x];
                if (c == '.')
                {
                    bool Solid(int yy, int xx) => yy >= 0 && xx >= 0 && yy < height && xx < width && grid[yy, xx] is not '.';
                    if (Solid(y - 1, x) || Solid(y + 1, x) || Solid(y, x - 1) || Solid(y, x + 1)) c = 'o';
                }
                line[x] = c;
            }
            result[y] = new string(line);
        }
        return result;
    }
}
