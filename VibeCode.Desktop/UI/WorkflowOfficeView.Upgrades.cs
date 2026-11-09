using System.Windows;
using System.Windows.Media;

namespace VibeCode.UI;

// The office is furnished by the work done in it (tiers and thresholds: WorkflowTown). A project's room starts with
// grimy walls, a concrete floor, one bare bulb and a kettle on a cardboard box, and ends as a penthouse with marble,
// a chandelier and an aquarium as its directory's tokens grow. Each agent's own desk starts as a cardboard box with a
// CRT and a stool, and ends as a battlestation with two screens and a gaming chair as its own chat's tokens grow, so a
// new chat's agents start at junk desks even in a well-kept room. Tier 2 is the office as it always looked, and what
// every room and desk shows while town improvements are off.
public sealed partial class WorkflowOfficeView
{
    private static readonly Color Gold = PixelArt.C("#D9B45A");

    private static int DeskTier(AgentActivityNode agent) => WorkflowTown.Instance.DeskTier(agent.Chat.SessionId);

    private new static Color Mix(Color a, Color b, double t) => Color.FromRgb((byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));

    /// <summary>A fully saturated colour around the hue wheel, for the battlestation's RGB lighting.</summary>
    private static Color Hue(double degrees)
    {
        var h = (degrees % 360 + 360) % 360 / 60;
        var x = 1 - Math.Abs(h % 2 - 1);
        var (r, g, b) = h switch
        {
            < 1 => (1.0, x, 0.0), < 2 => (x, 1.0, 0.0), < 3 => (0.0, 1.0, x), < 4 => (0.0, x, 1.0), < 5 => (x, 0.0, 1.0), _ => (1.0, 0.0, x),
        };
        return Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }

    /// <summary>The room's walls: grimy at first, its own colour once it is looked after.</summary>
    private static Color WallPaint(Color paint, int tier) => tier switch
    {
        0 => Mix(paint, PixelArt.C("#4A4742"), 0.55),
        1 => Mix(paint, PixelArt.C("#55524C"), 0.25),
        _ => paint,
    };

    // ---- the room ---------------------------------------------------------------------------------------------------

    /// <summary>Walls, wainscot, floor and skirting.</summary>
    private void DrawShell(DrawingContext dc, Room room, Color paint, int tier)
    {
        var p = _px;
        var cell = room.Bounds;
        var floor = new Rect(cell.Left, room.Floor, cell.Width, cell.Bottom - room.Floor);
        dc.DrawRectangle(Fill(paint), null, cell);
        switch (tier)
        {
            case 0:
                // Water stains in the lounge's ceiling corner and a crack down the wall beside its window, where
                // the nameplate, the posters and the whiteboard never cover them.
                var stain = Fill(Color.FromArgb(90, 0x2E, 0x24, 0x16));
                var sx = Math.Round(room.Lounge.Left + 6 * p);
                dc.DrawRectangle(stain, null, new Rect(sx, cell.Top + 2 * p, 13 * p, 4 * p));
                dc.DrawRectangle(stain, null, new Rect(sx + 3 * p, cell.Top + 6 * p, 7 * p, 3 * p));
                dc.DrawRectangle(stain, null, new Rect(sx + 5 * p, cell.Top + 9 * p, 2 * p, 3 * p));
                var crack = Fill(PixelArt.Shade(paint, 0.5));
                var cx = Math.Round(room.Window.Right + 4 * p);
                for (var i = 0; i < 8 && cx < room.Lounge.Right - p; i++)
                    dc.DrawRectangle(crack, null, new Rect(cx + (i % 2 == 0 ? 0 : p), room.Window.Top + i * 2 * p, Math.Max(1, 0.7 * p), 2 * p));
                break;
            case WorkflowTown.TopTier:
                // Pinstriped wallpaper under a gilt cornice.
                var stripe = Fill(Color.FromArgb(26, 0xFF, 0xF4, 0xD8));
                for (var x = cell.Left + 3 * p; x < cell.Right; x += 7 * p)
                    dc.DrawRectangle(stripe, null, new Rect(Math.Round(x), cell.Top, Math.Max(1, p), Math.Max(0, room.Floor - 16 * p - cell.Top)));
                dc.DrawRectangle(Fill(Gold), null, new Rect(cell.Left, cell.Top, cell.Width, Math.Max(1, 1.5 * p)));
                dc.DrawRectangle(Fill(PixelArt.Shade(Gold, 0.7)), null, new Rect(cell.Left, cell.Top + 1.5 * p, cell.Width, Math.Max(1, 0.6 * p)));
                break;
        }
        // Wainscot: none in a bare room, a plain skirting board next, panelling from the standard look up.
        if (tier >= WorkflowTown.StandardTier)
        {
            dc.DrawRectangle(Fill(PixelArt.Shade(paint, 0.82)), null, new Rect(cell.Left, room.Floor - 16 * p, cell.Width, 16 * p));
            if (tier > WorkflowTown.StandardTier)
                dc.DrawRectangle(Fill(tier == WorkflowTown.TopTier ? Gold : PixelArt.C("#B89A70")), null, new Rect(cell.Left, room.Floor - 16 * p, cell.Width, Math.Max(1, p)));
        }
        else if (tier == 1) dc.DrawRectangle(Fill(PixelArt.C("#5A4A3A")), null, new Rect(cell.Left, room.Floor - 3 * p, cell.Width, 2 * p));
        switch (tier)
        {
            case 0:
                // Bare concrete, patched and cracked.
                dc.DrawRectangle(Fill(PixelArt.C("#6B6A66")), null, floor);
                for (var i = 0; i < floor.Width / (9 * p); i++)
                    if (SceneWeatherFx.Noise(i * 13 + (int)(Hash(room.Project.Key, 5) * 97)) > 0.55)
                        dc.DrawRectangle(Fill(PixelArt.C("#5C5B57")), null, new Rect(Math.Round(floor.Left + i * 9 * p), floor.Top + p, 5 * p, Math.Max(1, p)));
                dc.DrawRectangle(Fill(PixelArt.C("#4E4D4A")), null, new Rect(Math.Round(floor.Left + floor.Width * 0.3), floor.Top + 2 * p, 7 * p, Math.Max(1, 0.6 * p)));
                break;
            case 1:
                // Lino tiles.
                dc.DrawRectangle(Fill(PixelArt.C("#9A9488")), null, floor);
                for (var x = floor.Left; x < floor.Right; x += 14 * p)
                    dc.DrawRectangle(Fill(PixelArt.C("#8A8478")), null, new Rect(Math.Round(x), floor.Top, Math.Min(7 * p, floor.Right - x), floor.Height));
                break;
            case 2:
                dc.DrawRectangle(Fill(PixelArt.C("#8A6A4A")), null, floor);
                for (var x = cell.Left; x < cell.Right; x += 14 * p)
                    dc.DrawRectangle(Fill(PixelArt.C("#735638")), null, new Rect(Math.Round(x), room.Floor, Math.Max(1, p), floor.Height));
                break;
            case 3:
                // Dark oak, freshly polished.
                dc.DrawRectangle(Fill(PixelArt.C("#7A5534")), null, floor);
                for (var x = cell.Left; x < cell.Right; x += 10 * p)
                    dc.DrawRectangle(Fill(PixelArt.C("#5E3F24")), null, new Rect(Math.Round(x), room.Floor, Math.Max(1, p), floor.Height));
                dc.DrawRectangle(Fill(PixelArt.C("#93683F")), null, new Rect(floor.Left, floor.Top, floor.Width, Math.Max(1, 0.6 * p)));
                break;
            default:
                // Veined marble.
                dc.DrawRectangle(Fill(PixelArt.C("#3C4250")), null, floor);
                for (var x = cell.Left; x < cell.Right; x += 12 * p)
                {
                    dc.DrawRectangle(Fill(PixelArt.C("#5A6274")), null, new Rect(Math.Round(x), room.Floor, 1, floor.Height));
                    dc.DrawRectangle(Fill(Color.FromArgb(120, 0x8A, 0x93, 0xA8)), null, new Rect(Math.Round(x + 3 * p), floor.Top + 2 * p, 4 * p, Math.Max(1, 0.5 * p)));
                }
                dc.DrawRectangle(Fill(PixelArt.C("#6E7890")), null, new Rect(floor.Left, floor.Top, floor.Width, Math.Max(1, 0.6 * p)));
                break;
        }
        dc.DrawRectangle(Fill(PixelArt.C("#2A2A30")), null, new Rect(cell.Left, room.Floor - p, cell.Width, p));
    }

    private void DrawCeilingLights(DrawingContext dc, Room room, int tier)
    {
        var p = _px;
        var cell = room.Bounds;
        var cord = Fill(PixelArt.C("#2A2A30"));
        if (tier == 0)
        {
            // One bare bulb on a flex.
            var x = Math.Round(cell.Left + cell.Width * 0.3);
            dc.DrawEllipse(Fill(Color.FromArgb(34, 0xFF, 0xF0, 0xB0)), null, new Point(x, cell.Top + 10 * p), 12 * p, 9 * p);
            dc.DrawRectangle(cord, null, new Rect(x, cell.Top, Math.Max(1, 0.6 * p), 7 * p));
            dc.DrawRectangle(Fill(PixelArt.C("#5A5A62")), null, new Rect(x - p, cell.Top + 7 * p, 2.6 * p, 1.5 * p));
            dc.DrawRectangle(Fill(PixelArt.C("#F7E7A0")), null, new Rect(x - 1.2 * p, cell.Top + 8.5 * p, 3 * p, 3 * p));
            return;
        }
        if (tier == WorkflowTown.TopTier)
        {
            // Recessed spots, and a chandelier over the room.
            for (var x = cell.Left + 15 * p; x < cell.Right - 10 * p; x += 30 * p)
                dc.DrawRectangle(Fill(PixelArt.C("#FFF4D0")), null, new Rect(Math.Round(x), cell.Top + 2 * p, 3 * p, Math.Max(1, 0.8 * p)));
            var c = new Point(Math.Round(cell.Left + cell.Width * 0.6), cell.Top + 7 * p);
            dc.DrawEllipse(Fill(Color.FromArgb(30, 0xFF, 0xE8, 0xA8)), null, c, 16 * p, 10 * p);
            dc.DrawRectangle(Fill(Gold), null, new Rect(c.X, cell.Top + 2 * p, Math.Max(1, 0.6 * p), 3 * p));
            dc.DrawRectangle(Fill(Gold), null, new Rect(c.X - 7 * p, c.Y, 14.6 * p, Math.Max(1, 0.8 * p)));
            for (var i = -3; i <= 3; i++)
            {
                dc.DrawRectangle(Fill(Gold), null, new Rect(c.X + i * 2 * p, c.Y - 2 * p, Math.Max(1, 0.6 * p), 2 * p));
                dc.DrawRectangle(Fill(PixelArt.C("#FFF6D8")), null, new Rect(c.X + i * 2 * p - 0.2 * p, c.Y - 3 * p, p, p));
                dc.DrawRectangle(Fill(PixelArt.C("#CFE6FF")), null, new Rect(c.X + i * 2 * p, c.Y + p + Math.Abs(i) % 2 * p, Math.Max(1, 0.6 * p), Math.Max(1, 1.2 * p)));
            }
            return;
        }
        for (var x = cell.Left + 30 * p; x < cell.Right - 20 * p; x += 60 * p)
        {
            var at = Math.Round(x);
            switch (tier)
            {
                case 1:
                    // A fluorescent tube on two chains.
                    dc.DrawRectangle(Fill(PixelArt.C("#5A5A62")), null, new Rect(at - 6 * p, cell.Top, Math.Max(1, 0.6 * p), 3 * p));
                    dc.DrawRectangle(Fill(PixelArt.C("#5A5A62")), null, new Rect(at + 6 * p, cell.Top, Math.Max(1, 0.6 * p), 3 * p));
                    dc.DrawRectangle(Fill(PixelArt.C("#B8BCC4")), null, new Rect(at - 7 * p, cell.Top + 3 * p, 14 * p, Math.Max(1, 0.8 * p)));
                    dc.DrawRectangle(Fill(PixelArt.C("#F0F2EA")), null, new Rect(at - 7 * p, cell.Top + 3.8 * p, 14 * p, Math.Max(1, 1.2 * p)));
                    break;
                case 2:
                    dc.DrawRectangle(cord, null, new Rect(at, cell.Top, Math.Max(1, p), 4 * p));
                    dc.DrawRectangle(Fill(PixelArt.C("#E8E4D0")), null, new Rect(at - 5 * p, cell.Top + 4 * p, 11 * p, 2 * p));
                    break;
                default:
                    // A pendant dome with warm light under it.
                    dc.DrawEllipse(Fill(Color.FromArgb(30, 0xFF, 0xE3, 0xA0)), null, new Point(at, cell.Top + 10 * p), 10 * p, 6 * p);
                    dc.DrawRectangle(cord, null, new Rect(at, cell.Top, Math.Max(1, 0.6 * p), 5 * p));
                    dc.DrawRectangle(Fill(PixelArt.C("#2F3640")), null, new Rect(at - 4 * p, cell.Top + 5 * p, 9 * p, 3 * p));
                    dc.DrawRectangle(Fill(PixelArt.C("#FFE3A0")), null, new Rect(at - 2.5 * p, cell.Top + 8 * p, 6 * p, Math.Max(1, p)));
                    break;
            }
        }
    }

    /// <summary>The lounge: what the coffee comes from, the water, the plant.</summary>
    private void DrawLounge(DrawingContext dc, Room room, Color paint, int tier)
    {
        var p = _px;
        var cell = room.Bounds;
        var lounge = room.Lounge;
        var floor = room.Floor;
        dc.DrawRectangle(Fill(PixelArt.Shade(paint, 0.7)), null, new Rect(lounge.Left, cell.Top, Math.Max(1, p), cell.Height));
        var rug = new Rect(lounge.Left + 4 * p, floor, lounge.Width - 8 * p, 2 * p);
        switch (tier)
        {
            case 0:
            {
                // A kettle on a cardboard box, a water jug on the floor, and a plant nobody watered.
                var box = new Rect(lounge.Left + 4 * p, floor - 8 * p, 12 * p, 8 * p);
                dc.DrawRectangle(Fill(PixelArt.C("#B08458")), null, box);
                dc.DrawRectangle(Fill(PixelArt.C("#D8C59A")), null, new Rect(box.Left + 5 * p, box.Top, 2 * p, box.Height));
                dc.DrawRectangle(Fill(PixelArt.C("#C49A6C")), null, new Rect(box.Left, box.Top, box.Width, Math.Max(1, p)));
                var kettle = new Rect(box.Left + 3 * p, box.Top - 4 * p, 5 * p, 4 * p);
                dc.DrawRectangle(Fill(PixelArt.C("#8E949C")), null, kettle);
                dc.DrawRectangle(Fill(PixelArt.C("#8E949C")), null, new Rect(kettle.Left - 1.5 * p, kettle.Top + p, 1.5 * p, p));
                dc.DrawRectangle(Fill(PixelArt.C("#3A3E46")), null, new Rect(kettle.Left + p, kettle.Top - p, 3 * p, Math.Max(1, 0.7 * p)));
                DrawJug(dc, new Rect(lounge.Right - 12 * p, floor - 8 * p, 6 * p, 8 * p));
                var pot = new Rect(lounge.Left + 24 * p, floor - 5 * p, 6 * p, 5 * p);
                dc.DrawRectangle(Fill(PixelArt.C("#A0522D")), null, pot);
                var stalk = Fill(PixelArt.C("#6B4F2A"));
                dc.DrawRectangle(stalk, null, new Rect(pot.Left + 2 * p, floor - 11 * p, Math.Max(1, 0.7 * p), 6 * p));
                dc.DrawRectangle(stalk, null, new Rect(pot.Left + 4 * p, floor - 9 * p, Math.Max(1, 0.7 * p), 4 * p));
                dc.DrawRectangle(stalk, null, new Rect(pot.Left + 4 * p, floor - 9 * p, 2 * p, Math.Max(1, 0.7 * p)));
                dc.DrawRectangle(Fill(PixelArt.C("#7A6A3A")), null, new Rect(pot.Right + p, floor - p, 2 * p, Math.Max(1, 0.7 * p)));
                return;
            }
            case 1:
            {
                // A folding table with a kettle and a mug, the water jug, a small plant.
                var table = new Rect(lounge.Left + 4 * p, floor - 9 * p, 14 * p, 1.5 * p);
                dc.DrawRectangle(Fill(PixelArt.C("#8A7A6A")), null, table);
                dc.DrawRectangle(Fill(PixelArt.C("#5A5A62")), null, new Rect(table.Left + p, table.Bottom, Math.Max(1, 0.8 * p), floor - table.Bottom));
                dc.DrawRectangle(Fill(PixelArt.C("#5A5A62")), null, new Rect(table.Right - 2 * p, table.Bottom, Math.Max(1, 0.8 * p), floor - table.Bottom));
                dc.DrawRectangle(Fill(PixelArt.C("#C8CCD4")), null, new Rect(table.Left + 2 * p, table.Top - 5 * p, 5 * p, 5 * p));
                dc.DrawRectangle(Fill(PixelArt.C("#3A3E46")), null, new Rect(table.Left + 3 * p, table.Top - 6 * p, 3 * p, Math.Max(1, 0.7 * p)));
                dc.DrawRectangle(Fill(PixelArt.C("#E8E4D0")), null, new Rect(table.Left + 9 * p, table.Top - 2 * p, 2 * p, 2 * p));
                DrawJug(dc, new Rect(lounge.Right - 12 * p, floor - 8 * p, 6 * p, 8 * p));
                var pot = new Rect(lounge.Left + 24 * p, floor - 5 * p, 6 * p, 5 * p);
                dc.DrawRectangle(Fill(PixelArt.C("#A0522D")), null, pot);
                foreach (var (lx, ly, lw) in new[] { (-1.0, -10.0, 4.0), (2.0, -9.0, 4.0), (0.0, -13.0, 3.0) })
                    dc.DrawRectangle(Fill(PixelArt.C("#4E9A4A")), null, new Rect(pot.Left + (2 + lx) * p, floor + ly * p, lw * p, 4 * p));
                return;
            }
        }
        // From the standard look up: a counter with a coffee machine, a water cooler (an aquarium at the top) and a plant.
        dc.DrawRectangle(Fill(tier == WorkflowTown.TopTier ? PixelArt.C("#4A2F5C") : PixelArt.C("#7A3E4A")), null,
            tier > WorkflowTown.StandardTier ? new Rect(rug.Left - 2 * p, rug.Top, rug.Width + 4 * p, rug.Height + p) : rug);
        if (tier > WorkflowTown.StandardTier)
            dc.DrawRectangle(Fill(tier == WorkflowTown.TopTier ? Gold : PixelArt.C("#9A5E6A")), null, new Rect(rug.Left - 2 * p, rug.Top, rug.Width + 4 * p, Math.Max(1, 0.6 * p)));
        var counter = new Rect(lounge.Left + 4 * p, floor - 12 * p, 16 * p, 12 * p);
        switch (tier)
        {
            case 2:
                dc.DrawRectangle(Fill(PixelArt.C("#5C4A3A")), null, counter);
                dc.DrawRectangle(Fill(PixelArt.C("#B8BCC4")), null, new Rect(counter.Left + 3 * p, counter.Top - 9 * p, 9 * p, 9 * p));
                dc.DrawRectangle(Fill(PixelArt.C("#3A3E46")), null, new Rect(counter.Left + 5 * p, counter.Top - 5 * p, 5 * p, 3 * p));
                dc.DrawRectangle(Fill(PixelArt.C("#E8574F")), null, new Rect(counter.Left + 10 * p, counter.Top - 8 * p, p, p));
                break;
            case 3:
                // An espresso machine on a hardwood counter.
                dc.DrawRectangle(Fill(PixelArt.C("#4A3A2E")), null, counter);
                dc.DrawRectangle(Fill(PixelArt.C("#6A5644")), null, new Rect(counter.Left, counter.Top, counter.Width, Math.Max(1, p)));
                dc.DrawRectangle(Fill(PixelArt.C("#2A2A30")), null, new Rect(counter.Left + 3 * p, counter.Top - 9 * p, 9 * p, 9 * p));
                dc.DrawRectangle(Fill(PixelArt.C("#D8DCE4")), null, new Rect(counter.Left + 4 * p, counter.Top - 8 * p, 7 * p, 3 * p));
                dc.DrawRectangle(Fill(PixelArt.C("#E8E4D0")), null, new Rect(counter.Left + 6 * p, counter.Top - 2 * p, 2 * p, 2 * p));
                dc.DrawRectangle(Fill(PixelArt.C("#E8574F")), null, new Rect(counter.Left + 10 * p, counter.Top - 4 * p, p, p));
                break;
            default:
                // A marble bar with a chrome machine and two cups.
                dc.DrawRectangle(Fill(PixelArt.C("#262B36")), null, counter);
                dc.DrawRectangle(Fill(PixelArt.C("#5A6274")), null, new Rect(counter.Left, counter.Top, counter.Width, Math.Max(1, p)));
                dc.DrawRectangle(Fill(Gold), null, new Rect(counter.Left, counter.Top + p, counter.Width, Math.Max(1, 0.5 * p)));
                dc.DrawRectangle(Fill(PixelArt.C("#D8DCE4")), null, new Rect(counter.Left + 2 * p, counter.Top - 10 * p, 10 * p, 10 * p));
                dc.DrawRectangle(Fill(PixelArt.C("#2A2A30")), null, new Rect(counter.Left + 4 * p, counter.Top - 6 * p, 6 * p, 2 * p));
                dc.DrawRectangle(Fill(Gold), null, new Rect(counter.Left + 2 * p, counter.Top - 10 * p, 10 * p, Math.Max(1, 0.6 * p)));
                dc.DrawRectangle(Fill(PixelArt.C("#E8E4D0")), null, new Rect(counter.Left + 5 * p, counter.Top - 2 * p, 2 * p, 2 * p));
                dc.DrawRectangle(Fill(PixelArt.C("#E8E4D0")), null, new Rect(counter.Left + 13 * p, counter.Top - 2 * p, 2 * p, 2 * p));
                break;
        }
        if (tier == WorkflowTown.TopTier)
        {
            // An aquarium on a stand where the cooler stood.
            var stand = new Rect(lounge.Right - 13 * p, floor - 8 * p, 10 * p, 8 * p);
            var tank = new Rect(stand.Left, stand.Top - 8 * p, stand.Width, 8 * p);
            dc.DrawRectangle(Fill(PixelArt.C("#2A2A30")), null, stand);
            dc.DrawRectangle(Fill(PixelArt.C("#3E86C4")), null, tank);
            dc.DrawRectangle(Fill(PixelArt.C("#D9C49A")), null, new Rect(tank.Left, tank.Bottom - p, tank.Width, p));
            dc.DrawRectangle(Fill(PixelArt.C("#4E9A4A")), null, new Rect(tank.Left + p, tank.Bottom - 5 * p, Math.Max(1, 0.8 * p), 4 * p));
            dc.DrawRectangle(Fill(PixelArt.C("#F59E3C")), null, new Rect(tank.Left + 4 * p, tank.Top + 3 * p, 2 * p, p));
            dc.DrawRectangle(Fill(PixelArt.C("#F59E3C")), null, new Rect(tank.Left + 3.4 * p, tank.Top + 3 * p, Math.Max(1, 0.6 * p), Math.Max(1, 0.6 * p)));
            dc.DrawRectangle(Fill(Color.FromArgb(160, 0xE8, 0xF4, 0xFF)), null, new Rect(tank.Left + 7 * p, tank.Top + 2 * p, Math.Max(1, 0.6 * p), Math.Max(1, 0.6 * p)));
            dc.DrawRectangle(Fill(PixelArt.C("#1A1E28")), null, new Rect(tank.Left, tank.Top - p, tank.Width, p));
        }
        else
        {
            var cooler = new Rect(lounge.Right - 14 * p, floor - 20 * p, 8 * p, 20 * p);
            dc.DrawRectangle(Fill(PixelArt.C("#D8DCE4")), null, new Rect(cooler.Left, cooler.Top + 8 * p, cooler.Width, 12 * p));
            dc.DrawRectangle(Fill(PixelArt.C("#7FB8E8")), null, new Rect(cooler.Left + p, cooler.Top, cooler.Width - 2 * p, 8 * p));
        }
        var plant = new Rect(lounge.Left + 24 * p, floor - 6 * p, 7 * p, 6 * p);
        dc.DrawRectangle(Fill(tier == WorkflowTown.TopTier ? Gold : PixelArt.C("#A0522D")), null, plant);
        foreach (var (lx, ly, lw) in new[] { (-2.0, -14.0, 4.0), (2.0, -16.0, 4.0), (-4.0, -10.0, 5.0), (3.0, -11.0, 5.0), (0.0, -19.0, 3.0) })
            dc.DrawRectangle(Fill(PixelArt.C("#4E9A4A")), null, new Rect(plant.Left + (3 + lx) * p, floor + ly * p, lw * p, 5 * p));
        if (tier > WorkflowTown.StandardTier)
            foreach (var (lx, ly, lw) in new[] { (-3.0, -22.0, 4.0), (3.0, -21.0, 4.0), (0.0, -25.0, 3.0) })
                dc.DrawRectangle(Fill(PixelArt.C("#3E8A44")), null, new Rect(plant.Left + (3 + lx) * p, floor + ly * p, lw * p, 5 * p));
    }

    private void DrawJug(DrawingContext dc, Rect jug)
    {
        var p = _px;
        dc.DrawRectangle(Fill(Color.FromArgb(210, 0xA9, 0xD3, 0xEE)), null, jug);
        dc.DrawRectangle(Fill(PixelArt.C("#3C7FC0")), null, new Rect(jug.Left + 2 * p, jug.Top - p, 2 * p, p));
        dc.DrawRectangle(Fill(Color.FromArgb(120, 0xFF, 0xFF, 0xFF)), null, new Rect(jug.Left + p, jug.Top + p, Math.Max(1, 0.7 * p), jug.Height - 2 * p));
    }

    /// <summary>A crack across a bare room's window, drawn over the live view through it.</summary>
    private void DrawCrackedPane(DrawingContext dc, Room room)
    {
        var p = _px;
        var w = room.Window;
        var glass = Fill(Color.FromArgb(150, 0xF4, 0xF6, 0xF8));
        for (var i = 0; i < 4; i++)
            dc.DrawRectangle(glass, null, new Rect(Math.Round(w.Left + (3 + i * 1.5) * p), Math.Round(w.Top + (2 + i * 1.6) * p), Math.Max(1, 0.6 * p), Math.Max(1, 1.6 * p)));
    }

    /// <summary>A rug under the desks and a tall plant in the corner once a room is comfortable; curtains at the top.</summary>
    private void DrawRoomDecor(DrawingContext dc, Room room, int tier)
    {
        var p = _px;
        var cell = room.Bounds;
        if (tier <= WorkflowTown.StandardTier) return;
        if (room.Desks.Count > 0)
        {
            var under = new Rect(room.Desks[0].Desk.Left - 3 * p, room.Floor, room.Desks[^1].Desk.Right + 3 * p - (room.Desks[0].Desk.Left - 3 * p), 2 * p);
            dc.DrawRectangle(Fill(tier == WorkflowTown.TopTier ? PixelArt.C("#5C3A4A") : PixelArt.C("#4A5C6A")), null, under);
            dc.DrawRectangle(Fill(tier == WorkflowTown.TopTier ? Gold : PixelArt.C("#6A7E8E")), null, new Rect(under.Left, under.Top, under.Width, Math.Max(1, 0.6 * p)));
        }
        // A tall fern in the corner by the door.
        var pot = new Rect(cell.Left + p, room.Floor - 6 * p, 5 * p, 6 * p);
        dc.DrawRectangle(Fill(tier == WorkflowTown.TopTier ? Gold : PixelArt.C("#5A6478")), null, pot);
        foreach (var (lx, ly, lw) in new[] { (0.0, -12.0, 3.0), (2.0, -16.0, 3.0), (-1.0, -20.0, 3.0), (1.0, -24.0, 2.0) })
            dc.DrawRectangle(Fill(PixelArt.C("#3E8A44")), null, new Rect(pot.Left + (1 + lx) * p, room.Floor + ly * p, lw * p, 5 * p));
        if (tier == WorkflowTown.TopTier)
        {
            // Velvet curtains either side of the window.
            var w = Inflate(room.Window, 2 * p, 2 * p);
            var velvet = Fill(PixelArt.C("#8A2E3A"));
            dc.DrawRectangle(velvet, null, new Rect(w.Left - 3 * p, w.Top - 2 * p, 3 * p, w.Height + 6 * p));
            dc.DrawRectangle(velvet, null, new Rect(w.Right, w.Top - 2 * p, 3 * p, w.Height + 6 * p));
            dc.DrawRectangle(Fill(Gold), null, new Rect(w.Left - 4 * p, w.Top - 3 * p, w.Width + 8 * p, Math.Max(1, p)));
        }
    }

    /// <summary>The poster over the desks: a taped-up sign in a bare room, a gilt-framed painting at the top.</summary>
    private void DrawPoster(DrawingContext dc, Room room, Color paint, int tier, Rect poster)
    {
        var p = _px;
        switch (tier)
        {
            case 0:
            {
                var sign = new Rect(poster.Left + p, poster.Top + 4 * p, 12 * p, 8 * p);
                dc.DrawRectangle(Fill(PixelArt.C("#EDEAE0")), null, sign);
                dc.DrawRectangle(Fill(PixelArt.C("#3A3E46")), null, new Rect(sign.Left + 2 * p, sign.Top + 2 * p, 7 * p, Math.Max(1, 0.7 * p)));
                dc.DrawRectangle(Fill(PixelArt.C("#3A3E46")), null, new Rect(sign.Left + 2 * p, sign.Top + 4 * p, 5 * p, Math.Max(1, 0.7 * p)));
                dc.DrawRectangle(Fill(Color.FromArgb(200, 0xE8, 0xD9, 0xA0)), null, new Rect(sign.Left - p, sign.Top - p, 3 * p, 1.5 * p));
                dc.DrawRectangle(Fill(Color.FromArgb(200, 0xE8, 0xD9, 0xA0)), null, new Rect(sign.Right - 2 * p, sign.Top - p, 3 * p, 1.5 * p));
                return;
            }
            case WorkflowTown.TopTier:
            {
                // A landscape in a gilt frame.
                dc.DrawRectangle(Fill(Gold), null, Inflate(poster, 1.5 * p, 1.5 * p));
                dc.DrawRectangle(Fill(PixelArt.C("#8EC5E8")), null, poster);
                dc.DrawRectangle(Fill(PixelArt.C("#4E9A4A")), null, new Rect(poster.Left, poster.Bottom - 7 * p, poster.Width, 7 * p));
                dc.DrawRectangle(Fill(PixelArt.C("#3E7A3E")), null, new Rect(poster.Left + 3 * p, poster.Bottom - 10 * p, 6 * p, 4 * p));
                dc.DrawRectangle(Fill(PixelArt.C("#F5CB5C")), null, new Rect(poster.Right - 5 * p, poster.Top + 3 * p, 3 * p, 3 * p));
                return;
            }
        }
        dc.DrawRectangle(Fill(tier == 3 ? PixelArt.C("#5C4026") : PixelArt.C("#2A2A30")), null, Inflate(poster, p, p));
        dc.DrawRectangle(Fill(PixelArt.Shade(paint, 1.35)), null, poster);
        dc.DrawRectangle(Fill(PixelArt.C("#F5CB5C")), null, new Rect(poster.Left + 3 * p, poster.Top + 4 * p, 8 * p, 8 * p));
        dc.DrawRectangle(Fill(PixelArt.C("#E8E4D0")), null, new Rect(poster.Left + 3 * p, poster.Bottom - 4 * p, 8 * p, p));
    }

    /// <summary>The chair behind a desk: a stool, a plastic chair, the office chair, a mesh chair, a gaming chair.</summary>
    private void DrawChair(DrawingContext dc, Rect desk, int tier)
    {
        var p = _px;
        switch (tier)
        {
            case 0:
                dc.DrawRectangle(Fill(PixelArt.C("#8A6A4A")), null, new Rect(desk.Left + 24 * p, desk.Top - 2 * p, 5 * p, 1.5 * p));
                break;
            case 1:
                dc.DrawRectangle(Fill(PixelArt.C("#C9CDD3")), null, new Rect(desk.Left + 16 * p, desk.Top - 6 * p, 12 * p, 6 * p));
                dc.DrawRectangle(Fill(PixelArt.C("#9DA2AA")), null, new Rect(desk.Left + 18 * p, desk.Top - 4 * p, 8 * p, Math.Max(1, 0.7 * p)));
                break;
            case 2:
                var chair = new Rect(desk.Left + 16 * p, desk.Top - 7 * p, 12 * p, 8 * p);
                dc.DrawRectangle(Fill(PixelArt.C("#2C2C34")), null, chair);
                dc.DrawRectangle(Fill(PixelArt.C("#2C2C34")), null, new Rect(chair.Left + 5 * p, desk.Bottom - 4 * p, 2 * p, 4 * p));
                break;
            case 3:
                // Mesh back with a headrest.
                var mesh = new Rect(desk.Left + 16 * p, desk.Top - 10 * p, 12 * p, 11 * p);
                dc.DrawRectangle(Fill(PixelArt.C("#3A3E46")), null, mesh);
                for (var y = mesh.Top + p; y < mesh.Bottom - p; y += 1.5 * p)
                    dc.DrawRectangle(Fill(PixelArt.C("#4E535C")), null, new Rect(mesh.Left + p, y, mesh.Width - 2 * p, Math.Max(1, 0.5 * p)));
                dc.DrawRectangle(Fill(PixelArt.C("#2C2C34")), null, new Rect(desk.Left + 21 * p, desk.Top - 12 * p, 2 * p, 2 * p));
                dc.DrawRectangle(Fill(PixelArt.C("#2C2C34")), null, new Rect(desk.Left + 18 * p, desk.Top - 14 * p, 8 * p, 3 * p));
                break;
            default:
                // A tall racing seat, black with red bolsters.
                var seat = new Rect(desk.Left + 16 * p, desk.Top - 15 * p, 12 * p, 16 * p);
                dc.DrawRectangle(Fill(PixelArt.C("#1E1E24")), null, seat);
                dc.DrawRectangle(Fill(PixelArt.C("#C8324A")), null, new Rect(seat.Left, seat.Top + 2 * p, 2 * p, seat.Height - 2 * p));
                dc.DrawRectangle(Fill(PixelArt.C("#C8324A")), null, new Rect(seat.Right - 2 * p, seat.Top + 2 * p, 2 * p, seat.Height - 2 * p));
                dc.DrawRectangle(Fill(PixelArt.C("#C8324A")), null, new Rect(seat.Left + 3 * p, seat.Top + p, 6 * p, 2 * p));
                dc.DrawRectangle(Fill(PixelArt.C("#1E1E24")), null, new Rect(seat.Left - p, seat.Top + 4 * p, p, 3 * p));
                dc.DrawRectangle(Fill(PixelArt.C("#1E1E24")), null, new Rect(seat.Right, seat.Top + 4 * p, p, 3 * p));
                break;
        }
    }

    // ---- the desk -------------------------------------------------------------------------------------------------

    /// <summary>The top of an agent's computer, for its hit area: the battlestation's second screen sits above.</summary>
    private double ComputerTop(Rect desk, int tier) => tier >= WorkflowTown.TopTier ? desk.Top - 20 * _px : desk.Top - 11 * _px;

    /// <summary>The computer on the desk, its screen showing what the agent is doing: a beige CRT, an old beige flat
    /// screen, the standard monitor, a widescreen, and two screens on an arm with RGB light behind them.</summary>
    private void DrawComputer(DrawingContext dc, Rect desk, AgentActivityNode agent, double time, double h, int tier)
    {
        var p = _px;
        var top = desk.Top;
        switch (tier)
        {
            case 0:
            {
                var crt = new Rect(desk.Left + p, top - 11 * p, 13 * p, 11 * p);
                dc.DrawRectangle(Fill(PixelArt.C("#B9AF95")), null, crt);
                dc.DrawRectangle(Fill(PixelArt.C("#9E9479")), null, new Rect(crt.Right - 3 * p, crt.Top, 3 * p, crt.Height));
                var screen = new Rect(crt.Left + 1.5 * p, crt.Top + 1.5 * p, 8 * p, 7 * p);
                DrawScreen(dc, screen, agent, time, h);
                // Scanlines, a crack in the case and a power light.
                for (var y = screen.Top; y < screen.Bottom; y += p)
                    dc.DrawRectangle(Fill(Color.FromArgb(55, 0, 0, 0)), null, new Rect(screen.Left, Math.Round(y), screen.Width, Math.Max(1, 0.4 * p)));
                dc.DrawRectangle(Fill(PixelArt.C("#7A7262")), null, new Rect(crt.Right - 2.6 * p, crt.Top + 2 * p, Math.Max(1, 0.5 * p), 2 * p));
                dc.DrawRectangle(Fill(PixelArt.C("#7A7262")), null, new Rect(crt.Right - 2.1 * p, crt.Top + 4 * p, Math.Max(1, 0.5 * p), 2 * p));
                dc.DrawRectangle(Fill(PixelArt.C("#5FD38D")), null, new Rect(crt.Left + 2 * p, crt.Bottom - 1.5 * p, Math.Max(1, 0.8 * p), Math.Max(1, 0.8 * p)));
                break;
            }
            case 1:
            {
                var monitor = new Rect(desk.Left + 2 * p, top - 11 * p, 12 * p, 9 * p);
                dc.DrawRectangle(Fill(PixelArt.C("#C2B8A0")), null, monitor);
                DrawScreen(dc, Inflate(monitor, -1.5 * p, -1.5 * p), agent, time, h);
                dc.DrawRectangle(Fill(PixelArt.C("#A89E86")), null, new Rect(monitor.Left + 4.5 * p, monitor.Bottom, 3 * p, 2 * p));
                dc.DrawRectangle(Fill(PixelArt.C("#F5E36C")), null, new Rect(monitor.Right - 2 * p, monitor.Top - 0.5 * p, 2.5 * p, 2.5 * p));
                break;
            }
            case 2:
            {
                var monitor = new Rect(desk.Left + 2 * p, top - 11 * p, 13 * p, 9 * p);
                dc.DrawRectangle(Fill(PixelArt.C("#1C1E24")), null, monitor);
                DrawScreen(dc, Inflate(monitor, -p, -p), agent, time, h);
                dc.DrawRectangle(Fill(PixelArt.C("#1C1E24")), null, new Rect(monitor.Left + 5 * p, monitor.Bottom, 3 * p, 2 * p));
                break;
            }
            case 3:
            {
                var monitor = new Rect(desk.Left, top - 11 * p, 16 * p, 9 * p);
                dc.DrawRectangle(Fill(PixelArt.C("#15171C")), null, monitor);
                DrawScreen(dc, Inflate(monitor, -0.75 * p, -0.75 * p), agent, time, h);
                dc.DrawRectangle(Fill(PixelArt.C("#5A5E66")), null, new Rect(monitor.Left + 7.6 * p, monitor.Top - 0.6 * p, Math.Max(1, 0.8 * p), Math.Max(1, 0.6 * p)));
                dc.DrawRectangle(Fill(PixelArt.C("#2A2E36")), null, new Rect(monitor.Left + 6.5 * p, monitor.Bottom, 3 * p, 2 * p));
                dc.DrawRectangle(Fill(PixelArt.C("#2A2E36")), null, new Rect(monitor.Left + 5 * p, top - 0.6 * p, 6 * p, Math.Max(1, 0.6 * p)));
                break;
            }
            default:
            {
                // RGB light haloing two screens on an arm: the work below, a live dashboard above.
                var glow = Hue(time * 40 + h * 360);
                var screens = new Rect(desk.Left, top - 20 * p, 16 * p, 18 * p);
                for (var ring = 3; ring >= 1; ring--)
                    dc.DrawRectangle(Fill(Color.FromArgb((byte)(30 + (3 - ring) * 25), glow.R, glow.G, glow.B)), null, Inflate(screens, ring * p, ring * p));
                dc.DrawRectangle(Fill(PixelArt.C("#2A2E36")), null, new Rect(desk.Left + 15 * p, top - 18 * p, Math.Max(1, p), 18 * p));
                var main = new Rect(desk.Left, top - 11 * p, 16 * p, 9 * p);
                var upper = new Rect(desk.Left + p, top - 20 * p, 14 * p, 8 * p);
                foreach (var screen in new[] { upper, main }) dc.DrawRectangle(Fill(PixelArt.C("#111318")), null, screen);
                DrawScreen(dc, Inflate(main, -0.75 * p, -0.75 * p), agent, time, h);
                DrawDashboard(dc, Inflate(upper, -0.75 * p, -0.75 * p), agent, time, h);
                dc.DrawRectangle(Fill(PixelArt.C("#2A2E36")), null, new Rect(desk.Left + 6.5 * p, main.Bottom, 3 * p, 2 * p));
                break;
            }
        }
    }

    /// <summary>The battlestation's second screen: bars that move while the agent works, a flat graph otherwise.</summary>
    private void DrawDashboard(DrawingContext dc, Rect screen, AgentActivityNode agent, double time, double h)
    {
        var p = _px;
        var working = agent.State == AgentActivityState.Working;
        dc.DrawRectangle(Fill(PixelArt.C(working ? "#0E1A24" : "#141A26")), null, screen);
        string[] inks = ["#7FD1FF", "#A8F0BE", "#F5CB5C", "#E8574F", "#7C8CF0"];
        var count = Math.Max(1, (int)((screen.Width - p) / (2 * p)));
        for (var i = 0; i < count; i++)
        {
            var level = working ? 0.25 + 0.7 * Math.Abs(Math.Sin(time * (1.3 + i * 0.4) + h * 10 + i)) : 0.2 + 0.1 * (i % 3);
            var height = Math.Max(p, Math.Round((screen.Height - 2 * p) * level));
            dc.DrawRectangle(Fill(PixelArt.C(inks[i % inks.Length])), null,
                new Rect(screen.Left + p + i * 2 * p, screen.Bottom - p - height, Math.Max(1, 1.2 * p), height));
        }
    }

    /// <summary>The desk in front of the agent's legs, with its keyboard and whatever sits on it.</summary>
    private void DrawDeskFront(DrawingContext dc, Rect desk, int tier, double time, double h)
    {
        var p = _px;
        var top = desk.Top;
        switch (tier)
        {
            case 0:
                // A cardboard box, taped up, with a beige keyboard missing a key.
                dc.DrawRectangle(Fill(PixelArt.C("#B08458")), null, desk);
                dc.DrawRectangle(Fill(PixelArt.C("#C49A6C")), null, new Rect(desk.Left, top, desk.Width, 2 * p));
                dc.DrawRectangle(Fill(PixelArt.C("#D8C59A")), null, new Rect(desk.Left + 13 * p, top, 2 * p, desk.Height));
                // Its shipping label is still on it.
                var label = new Rect(desk.Left + 3 * p, top + 4 * p, 7 * p, 5 * p);
                dc.DrawRectangle(Fill(PixelArt.C("#EDEAE0")), null, label);
                dc.DrawRectangle(Fill(PixelArt.C("#6B6F78")), null, new Rect(label.Left + p, label.Top + p, 4 * p, Math.Max(1, 0.6 * p)));
                dc.DrawRectangle(Fill(PixelArt.C("#6B6F78")), null, new Rect(label.Left + p, label.Top + 2.4 * p, 5 * p, Math.Max(1, 0.6 * p)));
                dc.DrawRectangle(Fill(PixelArt.C("#2A2A30")), null, new Rect(label.Left + p, label.Bottom - 1.4 * p, 3 * p, Math.Max(1, 0.6 * p)));
                dc.DrawRectangle(Fill(PixelArt.C("#C8BFA6")), null, new Rect(desk.Left + 15 * p, top - p, 10 * p, p));
                dc.DrawRectangle(Fill(PixelArt.C("#3A3E46")), null, new Rect(desk.Left + 19 * p, top - p, Math.Max(1, 0.8 * p), Math.Max(1, 0.5 * p)));
                break;
            case 1:
                // Particle board with a chipped edge.
                dc.DrawRectangle(Fill(PixelArt.C("#CDBB9E")), null, new Rect(desk.Left, top, desk.Width, 2 * p));
                dc.DrawRectangle(Fill(PixelArt.C("#B39F80")), null, new Rect(desk.Left + p, top + 2 * p, desk.Width - 2 * p, desk.Height - 2 * p));
                dc.DrawRectangle(Fill(PixelArt.C("#8E7B5E")), null, new Rect(desk.Left + 8 * p, top, 2 * p, p));
                dc.DrawRectangle(Fill(PixelArt.C("#5A5E66")), null, new Rect(desk.Left + 15 * p, top - p, 10 * p, p));
                break;
            case 2:
                dc.DrawRectangle(Fill(PixelArt.C("#9C7046")), null, new Rect(desk.Left, top, desk.Width, 2 * p));
                dc.DrawRectangle(Fill(PixelArt.C("#7E5A36")), null, new Rect(desk.Left + p, top + 2 * p, desk.Width - 2 * p, desk.Height - 2 * p));
                dc.DrawRectangle(Fill(PixelArt.C("#3A3E46")), null, new Rect(desk.Left + 15 * p, top - p, 10 * p, p));
                break;
            case 3:
                // Walnut with drawers, a lamp and a mug.
                dc.DrawRectangle(Fill(PixelArt.C("#6E4A2E")), null, new Rect(desk.Left, top, desk.Width, 2 * p));
                dc.DrawRectangle(Fill(PixelArt.C("#86603E")), null, new Rect(desk.Left, top, desk.Width, Math.Max(1, 0.5 * p)));
                dc.DrawRectangle(Fill(PixelArt.C("#5A3B24")), null, new Rect(desk.Left + p, top + 2 * p, desk.Width - 2 * p, desk.Height - 2 * p));
                dc.DrawRectangle(Fill(PixelArt.C("#3E2817")), null, new Rect(desk.Left + 20 * p, top + 2 * p, Math.Max(1, 0.6 * p), desk.Height - 2 * p));
                dc.DrawRectangle(Fill(Gold), null, new Rect(desk.Left + 24 * p, top + 5 * p, 2 * p, Math.Max(1, 0.6 * p)));
                dc.DrawRectangle(Fill(Gold), null, new Rect(desk.Left + 24 * p, top + 9 * p, 2 * p, Math.Max(1, 0.6 * p)));
                dc.DrawRectangle(Fill(PixelArt.C("#2A2E36")), null, new Rect(desk.Left + 15 * p, top - p, 10 * p, p));
                dc.DrawRectangle(Fill(PixelArt.C("#E8E4D0")), null, new Rect(desk.Left + 12.5 * p, top - 2 * p, 2 * p, 2 * p));
                dc.DrawRectangle(Fill(PixelArt.C("#2A2A30")), null, new Rect(desk.Right - 4 * p, top - p, 3 * p, p));
                dc.DrawRectangle(Fill(PixelArt.C("#2A2A30")), null, new Rect(desk.Right - 3 * p, top - 6 * p, Math.Max(1, 0.7 * p), 5 * p));
                dc.DrawRectangle(Fill(PixelArt.C("#2A2A30")), null, new Rect(desk.Right - 5 * p, top - 7 * p, 4 * p, 2 * p));
                dc.DrawRectangle(Fill(Color.FromArgb(120, 0xFF, 0xE3, 0xA0)), null, new Rect(desk.Right - 5 * p, top - 5 * p, 4 * p, 4 * p));
                break;
            default:
                // Black glass and gold, RGB light under the edge and on the keys, a succulent and a mug.
                dc.DrawRectangle(Fill(PixelArt.C("#1A1E28")), null, new Rect(desk.Left, top, desk.Width, 2 * p));
                dc.DrawRectangle(Fill(Gold), null, new Rect(desk.Left, top, desk.Width, Math.Max(1, 0.6 * p)));
                dc.DrawRectangle(Fill(PixelArt.C("#232836")), null, new Rect(desk.Left + p, top + 2 * p, desk.Width - 2 * p, desk.Height - 2 * p));
                var glow = Hue(time * 40 + h * 360 + 120);
                dc.DrawRectangle(Fill(Color.FromArgb(200, glow.R, glow.G, glow.B)), null, new Rect(desk.Left + p, top + 2 * p, desk.Width - 2 * p, Math.Max(1, 0.8 * p)));
                dc.DrawRectangle(Fill(Gold), null, new Rect(desk.Left + p, desk.Bottom - 2 * p, desk.Width - 2 * p, Math.Max(1, 0.5 * p)));
                dc.DrawRectangle(Fill(PixelArt.C("#1A1E28")), null, new Rect(desk.Left + 15 * p, top - p, 10 * p, p));
                for (var i = 0; i < 4; i++)
                {
                    var key = Hue(time * 60 + i * 70 + h * 360);
                    dc.DrawRectangle(Fill(key), null, new Rect(desk.Left + (16 + i * 2.2) * p, top - 0.8 * p, Math.Max(1, 0.8 * p), Math.Max(1, 0.5 * p)));
                }
                dc.DrawRectangle(Fill(PixelArt.C("#E8E4D0")), null, new Rect(desk.Left + 12.5 * p, top - 2 * p, 2 * p, 2 * p));
                dc.DrawRectangle(Fill(PixelArt.C("#E8E4D0")), null, new Rect(desk.Right - 4 * p, top - 2 * p, 3 * p, 2 * p));
                dc.DrawRectangle(Fill(PixelArt.C("#4E9A4A")), null, new Rect(desk.Right - 4 * p, top - 4 * p, p, 2 * p));
                dc.DrawRectangle(Fill(PixelArt.C("#5FB85A")), null, new Rect(desk.Right - 2.6 * p, top - 5 * p, p, 3 * p));
                break;
        }
    }
}
