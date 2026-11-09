using System.Windows;
using System.Windows.Media;

namespace VibeCode.UI;

// The office floor is furnished by the work done on it, like the pixel office (tiers and thresholds: WorkflowTown). A
// project's pod starts on bare concrete and gets a better rug as its directory's tokens grow; each agent's desk starts
// as a cardboard box with a CRT and a stool and ends as a black-glass battlestation with two screens and a racing chair
// as its own chat's tokens grow, so a new chat's agents start at junk desks even in a well-kept pod. Tier 2 is the floor
// as it always looked, and what every pod and desk shows while town improvements are off.
public sealed partial class WorkflowFloorView
{
    private static readonly Color Gold = PixelArt.C("#D9B45A");
    // The town stamp the floor and desks were last baked for, so an upgrade refurnishes without a new layout.
    private int _bakedTown = -1;

    private static int DeskTierOf(AgentActivityNode? agent) =>
        agent is null ? WorkflowTown.StandardTier : WorkflowTown.Instance.DeskTier(agent.Chat.SessionId);

    /// <summary>The look a desk is drawn with: its chat's tier, once any technician on the way has installed it.</summary>
    private int DeskTierFor(Desk desk) => ShownTier(desk.AgentId, _byId.GetValueOrDefault(desk.AgentId));

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

    // ---- the pod ----------------------------------------------------------------------------------------------------

    /// <summary>What a pod stands on: bare concrete, a thin cheap rug, the standard rug, a rich rug with a double border,
    /// a plush one edged in gold.</summary>
    private void PodRug(DrawingContext dc, Pod pod)
    {
        var rug = Inflate(pod.Area, 3, 3);
        var line = Math.Max(1, _p * 0.6);
        switch (WorkflowTown.Instance.RoomTier(pod.Project.Key))
        {
            case 0:
                dc.DrawRoundedRectangle(Fill(Alpha(PixelArt.C("#6B6A66"), 0.6)), null, R(rug), _p, _p);
                var stain = Alpha(PixelArt.C("#4E4D4A"), 0.55);
                Box(dc, stain, rug.Left + rug.Width * 0.2, rug.Top + rug.Height * 0.3, 7, 4);
                Box(dc, stain, rug.Left + rug.Width * 0.62, rug.Top + rug.Height * 0.7, 5, 3);
                for (var i = 0; i < 5; i++)
                    Box(dc, Alpha(PixelArt.C("#3E3D3A"), 0.6), rug.Left + rug.Width * 0.45 + (i % 2), rug.Top + 6 + i * 3, 1, 3);
                break;
            case 1:
                dc.DrawRoundedRectangle(Fill(Alpha(Mix(pod.Rug, PixelArt.C("#8A8478"), 0.55), 0.35)), null, R(rug), 2 * _p, 2 * _p);
                break;
            case 2:
                dc.DrawRoundedRectangle(Fill(Alpha(pod.Rug, 0.42)), Stroke(Alpha(pod.Rug, 0.8), line), R(rug), 3 * _p, 3 * _p);
                break;
            case 3:
                dc.DrawRoundedRectangle(Fill(Alpha(pod.Rug, 0.6)), Stroke(Alpha(pod.Rug, 0.95), line), R(rug), 3 * _p, 3 * _p);
                dc.DrawRoundedRectangle(null, Stroke(Alpha(Mix(pod.Rug, Colors.White, 0.35), 0.8), line), R(Inflate(rug, -2, -2)), 2 * _p, 2 * _p);
                break;
            default:
                dc.DrawRoundedRectangle(Fill(Alpha(Mix(pod.Rug, PixelArt.C("#2A1E3A"), 0.25), 0.7)), Stroke(Gold, Math.Max(1, _p)), R(rug), 3 * _p, 3 * _p);
                dc.DrawRoundedRectangle(null, Stroke(Alpha(Gold, 0.7), line), R(Inflate(rug, -2, -2)), 2 * _p, 2 * _p);
                for (var x = rug.Left + 6; x < rug.Right - 4; x += 10)
                    Box(dc, Alpha(Gold, 0.18), x, rug.Top + 3, 1, rug.Height - 6);
                break;
        }
    }

    // ---- the desk -----------------------------------------------------------------------------------------------------

    /// <summary>Where the main screen sits on a desk of this tier.</summary>
    private static Rect ScreenOf(Desk desk, int tier) => tier switch
    {
        0 => new Rect(desk.X + 12, desk.Y + 3, 10, 7),
        1 => new Rect(desk.X + 12, desk.Y + 3, 10, 8),
        3 => new Rect(desk.X + 8, desk.Y + 2, 18, 9),
        WorkflowTown.TopTier => new Rect(desk.X + 5, desk.Y + 2, 13, 9),
        _ => new Rect(desk.X + 11, desk.Y + 2, 12, 9),
    };

    /// <summary>The desk, computer and keyboard of a non-standard tier, baked with the furniture.</summary>
    private void DeskArtTier(DrawingContext dc, Desk desk, int tier)
    {
        var (x, y) = (desk.X, desk.Y);
        switch (tier)
        {
            case 0:
                // A taped cardboard box with its label still on, a beige CRT, a keyboard missing a key, a crumpled note.
                Box(dc, Shade, x + 6, y + 25, 22, 2);
                Box(dc, PixelArt.C("#C49A6C"), x + 6, y + 11, 22, 11);
                Box(dc, PixelArt.C("#A87F52"), x + 6, y + 16, 22, 1);
                Box(dc, PixelArt.C("#D8C59A"), x + 16, y + 11, 2, 11);
                Box(dc, PixelArt.C("#8E6A44"), x + 6, y + 22, 22, 4);
                Box(dc, PixelArt.C("#EDEAE0"), x + 8, y + 23, 5, 2);
                Box(dc, PixelArt.C("#9E9479"), x + 11, y - 1, 12, 3);
                Box(dc, PixelArt.C("#B9AF95"), x + 10, y + 1, 14, 11);
                Box(dc, PixelArt.C("#5FD38D"), x + 21, y + 10, 1, 1);
                Box(dc, PixelArt.C("#C8BFA6"), x + 12, y + 16, 10, 3);
                Box(dc, PixelArt.C("#6B6F78"), x + 15, y + 17, 1, 1);
                Box(dc, PixelArt.C("#C8BFA6"), x + 24, y + 16, 2, 3);
                Box(dc, Paper, x + 25, y + 12, 2, 2);
                break;
            case 1:
                // Chipped particle board, an old beige screen with a sticky note.
                Box(dc, Shade, x + 5, y + 26, 26, 2);
                Box(dc, PixelArt.C("#CDBB9E"), x + 4, y + 10, 26, 12);
                Box(dc, PixelArt.C("#DCCDB2"), x + 4, y + 10, 26, 1);
                Box(dc, PixelArt.C("#8E7B5E"), x + 20, y + 21, 2, 1);
                Box(dc, PixelArt.C("#B39F80"), x + 4, y + 22, 26, 4);
                Box(dc, PixelArt.C("#8E7B5E"), x + 4, y + 25, 26, 1);
                Box(dc, PixelArt.C("#A89E86"), x + 15, y + 11, 4, 2);
                Box(dc, PixelArt.C("#A89E86"), x + 13, y + 13, 8, 1);
                Box(dc, PixelArt.C("#C2B8A0"), x + 10, y + 1, 14, 11);
                Box(dc, PixelArt.C("#F5E36C"), x + 22, y + 1, 3, 3);
                Box(dc, PixelArt.C("#D8D4C8"), x + 12, y + 16, 10, 3);
                for (var k = 0; k < 4; k++) Box(dc, KeyDot, x + 13 + k * 2, y + 17, 1, 1);
                Box(dc, PixelArt.C("#D8D4C8"), x + 24, y + 16, 2, 3);
                break;
            case 3:
                // Walnut with brass handles, a widescreen, a lamp and a mug.
                Box(dc, Shade, x + 5, y + 26, 26, 2);
                Box(dc, PixelArt.C("#6E4A2E"), x + 4, y + 10, 26, 12);
                Box(dc, PixelArt.C("#86603E"), x + 4, y + 10, 26, 1);
                Box(dc, PixelArt.C("#5A3B24"), x + 4, y + 22, 26, 4);
                Box(dc, PixelArt.C("#3E2817"), x + 20, y + 22, 1, 4);
                Box(dc, Gold, x + 9, y + 23, 2, 1);
                Box(dc, Gold, x + 24, y + 23, 2, 1);
                Box(dc, PixelArt.C("#2A2E36"), x + 16, y + 11, 3, 2);
                Box(dc, PixelArt.C("#2A2E36"), x + 13, y + 13, 9, 1);
                Box(dc, PixelArt.C("#15171C"), x + 7, y + 1, 20, 11);
                Box(dc, PixelArt.C("#2A2E36"), x + 12, y + 16, 10, 3);
                for (var k = 0; k < 4; k++) Box(dc, PixelArt.C("#5A5E66"), x + 13 + k * 2, y + 17, 1, 1);
                Box(dc, PixelArt.C("#2A2E36"), x + 24, y + 16, 2, 3);
                Box(dc, Color.FromArgb(90, 0xFF, 0xE3, 0xA0), x + 4, y + 13, 6, 6);
                Box(dc, PixelArt.C("#2A2A30"), x + 5, y + 14, 3, 3);
                Box(dc, PixelArt.C("#E8E4D0"), x + 27, y + 12, 2, 2);
                break;
            default:
                // Black glass edged in gold, two screens on an arm, a succulent; the RGB light is drawn live.
                Box(dc, Shade, x + 4, y + 26, 28, 2);
                Box(dc, PixelArt.C("#1A1E28"), x + 3, y + 10, 28, 12);
                Box(dc, Gold, x + 3, y + 10, 28, 1);
                Box(dc, PixelArt.C("#232836"), x + 3, y + 22, 28, 4);
                Box(dc, Gold, x + 3, y + 25, 28, 1);
                Box(dc, PixelArt.C("#2A2E36"), x + 18, y + 11, 2, 2);
                Box(dc, PixelArt.C("#111318"), x + 4, y + 1, 15, 11);
                Box(dc, PixelArt.C("#111318"), x + 19, y + 1, 11, 11);
                Box(dc, PixelArt.C("#1A1E28"), x + 12, y + 16, 10, 3);
                Box(dc, PixelArt.C("#1A1E28"), x + 24, y + 16, 2, 3);
                Box(dc, PixelArt.C("#E8E4D0"), x + 5, y + 15, 3, 2);
                Box(dc, PixelArt.C("#4E9A4A"), x + 5, y + 13, 3, 2);
                break;
        }
        // Something of its owner's on every desk past the cardboard box.
        if (tier is 1 or 3)
        {
            var colour = _byId.TryGetValue(desk.AgentId, out var owner) ? WorkflowGuilds.ShirtOf(owner) : PixelArt.ShirtOf(desk.AgentId);
            Box(dc, colour, x + 26, y + 18, 2, 2);
        }
    }

    /// <summary>What moves on a desk of this tier: the CRT's scanlines, the battlestation's RGB light and dashboard.</summary>
    private void DeskLiveTier(DrawingContext dc, Desk desk, int tier, AgentActivityNode? agent)
    {
        var (x, y) = (desk.X, desk.Y);
        if (tier == 0)
        {
            var screen = ScreenOf(desk, 0);
            for (var row = screen.Top; row < screen.Bottom; row += 1)
                Box(dc, Color.FromArgb(55, 0, 0, 0), screen.Left, row, screen.Width, 0.4);
            return;
        }
        if (tier != WorkflowTown.TopTier) return;
        var h = Hash(desk.AgentId);
        var glow = Hue(_time * 40 + h * 360);
        Box(dc, Color.FromArgb(200, glow.R, glow.G, glow.B), x + 3, y + 22, 28, 1);
        for (var i = 0; i < 4; i++) Box(dc, Hue(_time * 60 + i * 70 + h * 360), x + 13 + i * 2, y + 17, 1, 1);
        // The second screen: bars that move while the agent works, a flat graph otherwise.
        var dash = new Rect(x + 20, y + 2, 9, 9);
        var working = agent?.State == AgentActivityState.Working;
        Box(dc, PixelArt.C(working ? "#0E1A24" : "#141A26"), dash.Left, dash.Top, dash.Width, dash.Height);
        string[] inks = ["#7FD1FF", "#A8F0BE", "#F5CB5C", "#E8574F"];
        for (var i = 0; i < 4; i++)
        {
            var level = working ? 0.25 + 0.7 * Math.Abs(Math.Sin(_time * (1.3 + i * 0.4) + h * 10 + i)) : 0.2 + 0.1 * (i % 3);
            var height = Math.Max(1, Math.Round((dash.Height - 2) * level));
            Box(dc, PixelArt.C(inks[i]), dash.Left + 1 + i * 2, dash.Bottom - 1 - height, 1, height);
        }
    }

    // ---- the chair --------------------------------------------------------------------------------------------------

    /// <summary>An empty desk chair of this tier: a stool, a plastic chair, the office chair, a mesh chair, a racing seat.</summary>
    private void EmptyChair(DrawingContext dc, Point seat, int tier)
    {
        switch (tier)
        {
            case 0:
                Box(dc, PixelArt.C("#8A6A4A"), seat.X - 3, seat.Y - 6, 6, 4);
                Box(dc, PixelArt.C("#5E4228"), seat.X - 3, seat.Y - 2, 1, 2);
                Box(dc, PixelArt.C("#5E4228"), seat.X + 2, seat.Y - 2, 1, 2);
                return;
            case 1: Box(dc, PixelArt.C("#9DA2AA"), seat.X - 5, seat.Y - 8, 10, 6); break;
            case 3: Box(dc, PixelArt.C("#2C2C34"), seat.X - 5, seat.Y - 8, 10, 6); break;
            case WorkflowTown.TopTier:
                Box(dc, PixelArt.C("#1E1E24"), seat.X - 5, seat.Y - 8, 10, 6);
                Box(dc, PixelArt.C("#C8324A"), seat.X - 1, seat.Y - 8, 2, 6);
                break;
            default: Box(dc, ChairSeat, seat.X - 5, seat.Y - 8, 10, 6); break;
        }
        ChairFront(dc, seat, tier);
    }

    /// <summary>The back of a desk chair of this tier seen from behind, in front of whoever sits in it. A stool has none.</summary>
    private void ChairFront(DrawingContext dc, Point seat, int tier)
    {
        switch (tier)
        {
            case 0:
                Box(dc, PixelArt.C("#6E5034"), seat.X - 3, seat.Y - 1, 6, 1);
                break;
            case 1:
                Box(dc, PixelArt.C("#C9CDD3"), seat.X - 4, seat.Y - 3, 8, 4);
                Box(dc, PixelArt.C("#DDE1E6"), seat.X - 4, seat.Y - 3, 8, 1);
                Box(dc, PixelArt.C("#9DA2AA"), seat.X - 4, seat.Y + 2, 8, 1);
                break;
            case 3:
                Box(dc, PixelArt.C("#3A3E46"), seat.X - 5, seat.Y - 4, 10, 5);
                for (var row = seat.Y - 3; row < seat.Y + 1; row += 1.5) Box(dc, PixelArt.C("#4E535C"), seat.X - 4, row, 8, 0.5);
                Box(dc, PixelArt.C("#2C2C34"), seat.X - 4, seat.Y + 2, 8, 1);
                break;
            case WorkflowTown.TopTier:
                Box(dc, PixelArt.C("#1E1E24"), seat.X - 5, seat.Y - 4, 10, 5);
                Box(dc, PixelArt.C("#C8324A"), seat.X - 5, seat.Y - 4, 2, 5);
                Box(dc, PixelArt.C("#C8324A"), seat.X + 3, seat.Y - 4, 2, 5);
                Box(dc, PixelArt.C("#C8324A"), seat.X - 2, seat.Y - 4, 4, 1);
                Box(dc, PixelArt.C("#1E1E24"), seat.X - 4, seat.Y + 2, 8, 1);
                break;
            default:
                ChairFront(dc, seat);
                break;
        }
    }
}
