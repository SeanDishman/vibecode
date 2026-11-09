using System.Windows;
using System.Windows.Media;

namespace VibeCode.UI;

// The tavern's extras: town improvements baked into the street, fireworks over a building whose agent just finished,
// and the bard by the tavern who plays whatever the Spotify extension is playing.
public sealed partial class WorkflowTavernView
{
    private static readonly Color[] Blooms = [PixelArt.C("#E8574F"), PixelArt.C("#F5CB5C"), PixelArt.C("#E86FA4"), PixelArt.C("#F4F1E8")];
    private static readonly Color Iron = PixelArt.C("#2E2D33");
    // The pennant's words, rebuilt only when the track or play state changes rather than every frame.
    private (bool Playing, string Track, string Artist) _pennantFor;
    private string _pennant = "";

    /// <summary>A project's earned upgrades, drawn into the cached street with the building itself.</summary>
    private void DrawImprovements(DrawingContext dc, Building b)
    {
        var earned = WorkflowTown.Instance.For(b.Project.Key);
        if (earned.Count == 0) return;
        var s = b.Scale;
        var dot = Math.Max(2, Math.Round(2 * s));
        if (Has(earned, WorkflowTown.Flowers))
            foreach (var (_, window) in b.Windows)
            {
                // A box hung under each sill, blooms showing over its lip.
                var box = new Rect(Math.Round(window.Left - 3 * s), Math.Round(window.Bottom + 6 * s), Math.Round(window.Width + 6 * s), Math.Max(2, Math.Round(4 * s)));
                dc.DrawRectangle(Fill(Shade(PixelArt.C("#6B4A2E"))), null, box);
                dc.DrawRectangle(Fill(Shade(PixelArt.C("#4E9A4A"))), null, new Rect(box.Left, box.Top - dot / 2, box.Width, dot / 2 + 1));
                var i = 0;
                for (var fx = box.Left + s; fx + dot <= box.Right; fx += dot + 2 * s, i++)
                    dc.DrawRectangle(Fill(Shade(Blooms[i % Blooms.Length])), null, new Rect(Math.Round(fx), Math.Round(box.Top - dot - s), dot, dot));
            }
        if (Has(earned, WorkflowTown.Lanterns))
        {
            // A string of lanterns along the eaves, glowing once the lamps are lit.
            var left = b.Body.Left - b.Overhang * 0.6;
            var right = b.Body.Right + b.Overhang * 0.6;
            var top = b.Body.Top + 3 * s;
            var sag = 6 * s;
            var cord = Fill(Shade(PixelArt.C("#3A2A20")));
            for (var x = left; x < right; x += Math.Max(1, s))
            {
                var t = (x - left) / (right - left);
                dc.DrawRectangle(cord, null, new Rect(Math.Round(x), Math.Round(top + sag * 4 * t * (1 - t)), Math.Max(1, Math.Round(s)), Math.Max(1, Math.Round(s))));
            }
            for (var k = 1; k <= 5; k++)
            {
                var t = k / 6.0;
                var at = new Point(left + (right - left) * t, top + sag * 4 * t * (1 - t));
                if (Sky.LampsOn)
                {
                    dc.PushOpacity(Math.Min(1, Sky.Darkness * 1.6) * 0.7);
                    dc.DrawEllipse(LampGlow, null, new Point(at.X, at.Y + 4 * s), 9 * s, 9 * s);
                    dc.Pop();
                }
                var paper = k % 2 == 0 ? PixelArt.C("#E8A33B") : PixelArt.C("#D9453A");
                dc.DrawRectangle(Fill(Sky.LampsOn ? paper : Shade(paper)), null, new Rect(Math.Round(at.X - 2 * s), Math.Round(at.Y + s), Math.Max(2, Math.Round(4 * s)), Math.Max(3, Math.Round(5 * s))));
                dc.DrawRectangle(Fill(Shade(Iron)), null, new Rect(Math.Round(at.X - s), Math.Round(at.Y), Math.Max(1, Math.Round(2 * s)), Math.Max(1, Math.Round(s))));
            }
        }
        if (Has(earned, WorkflowTown.Weathervane))
        {
            // On the ridge, or on the bell tower, or the forge's flat roof.
            var look = (Look)b.Style;
            var foot = look == Look.Forge ? new Point(b.Body.Left + b.Body.Width * 0.4, b.Body.Top - 20 * s)
                : look == Look.Hall ? new Point(b.RoofPeak.X, b.RoofPeak.Y - 38 * s) : b.RoofPeak;
            var ink = Fill(Shade(Iron));
            var w = Math.Max(1, Math.Round(1.5 * s));
            dc.DrawRectangle(ink, null, new Rect(Math.Round(foot.X - w / 2), Math.Round(foot.Y - 18 * s), w, Math.Round(18 * s)));
            var arm = Math.Round(foot.Y - 14 * s);
            dc.DrawRectangle(ink, null, new Rect(Math.Round(foot.X - 8 * s), arm, Math.Round(16 * s), w));
            dc.DrawRectangle(ink, null, new Rect(Math.Round(foot.X + 6 * s), arm - w, Math.Round(2 * s), w * 3));
            dc.DrawRectangle(ink, null, new Rect(Math.Round(foot.X - 9 * s), arm - 2 * w, w * 2, w * 5));
            // The cockerel on top.
            var bird = Fill(Shade(PixelArt.C("#C9A227")));
            dc.DrawRectangle(bird, null, new Rect(Math.Round(foot.X - 3 * s), Math.Round(foot.Y - 22 * s), Math.Round(6 * s), Math.Round(4 * s)));
            dc.DrawRectangle(bird, null, new Rect(Math.Round(foot.X + 2 * s), Math.Round(foot.Y - 25 * s), Math.Round(2 * s), Math.Round(3 * s)));
            dc.DrawRectangle(bird, null, new Rect(Math.Round(foot.X - 5 * s), Math.Round(foot.Y - 24 * s), Math.Round(2 * s), Math.Round(3 * s)));
        }
        if (Has(earned, WorkflowTown.Banner))
        {
            // A pole out from the corner with the town's banner hanging from it.
            var root = new Point(b.Body.Right, b.Body.Top + 14 * s);
            var pole = Fill(Shade(PixelArt.C("#5C4026")));
            for (var k = 0; k < 8; k++)
                dc.DrawRectangle(pole, null, new Rect(Math.Round(root.X + k * 2 * s), Math.Round(root.Y - k * s), Math.Max(1, Math.Round(2 * s)), Math.Max(1, Math.Round(1.5 * s))));
            var cloth = new Rect(Math.Round(root.X + 8 * s), Math.Round(root.Y - 6 * s), Math.Round(9 * s), Math.Round(18 * s));
            dc.DrawRectangle(Fill(Shade(PixelArt.C("#9E2B25"))), null, cloth);
            dc.DrawRectangle(Fill(Shade(PixelArt.C("#E0B954"))), null, new Rect(cloth.Left, cloth.Top, cloth.Width, Math.Max(1, Math.Round(1.5 * s))));
            dc.DrawRectangle(Fill(Shade(PixelArt.C("#E0B954"))), null, new Rect(Math.Round(cloth.Left + 3 * s), Math.Round(cloth.Top + 5 * s), Math.Round(3 * s), Math.Round(4 * s)));
            // Swallowtail.
            dc.DrawRectangle(Fill(Shade(PixelArt.C("#9E2B25"))), null, new Rect(cloth.Left, cloth.Bottom, Math.Round(3 * s), Math.Round(3 * s)));
            dc.DrawRectangle(Fill(Shade(PixelArt.C("#9E2B25"))), null, new Rect(cloth.Right - Math.Round(3 * s), cloth.Bottom, Math.Round(3 * s), Math.Round(3 * s)));
        }
        // A plate by the door for any upgrade at all, a carved stone plaque once 1,000 lines have changed.
        var plate = PlaqueRect(b);
        if (Has(earned, WorkflowTown.Plaque))
        {
            dc.DrawRectangle(Fill(Shade(PixelArt.C("#6E6A64"))), null, Inflate(plate, Math.Max(1, s), Math.Max(1, s)));
            dc.DrawRectangle(Fill(Shade(PixelArt.C("#A9A49B"))), null, plate);
            for (var k = 0; k < 3; k++)
                dc.DrawRectangle(Fill(Shade(PixelArt.C("#6E6A64"))), null, new Rect(Math.Round(plate.Left + 2 * s), Math.Round(plate.Top + (2 + k * 2.5) * s), Math.Round(plate.Width - (4 + k * 2) * s), Math.Max(1, Math.Round(s))));
        }
        else
        {
            dc.DrawRectangle(Fill(Shade(PixelArt.C("#7A5A2A"))), null, Inflate(plate, Math.Max(1, s * 0.5), Math.Max(1, s * 0.5)));
            dc.DrawRectangle(Fill(Shade(PixelArt.C("#D9B44A"))), null, plate);
        }
    }

    private static bool Has(IReadOnlyList<TownImprovement> earned, string id)
    {
        foreach (var item in earned) if (item.Id == id) return true;
        return false;
    }

    /// <summary>Where a building's plaque hangs: on the front wall beside the door.</summary>
    private Rect PlaqueRect(Building b)
    {
        var s = b.Scale;
        var stone = Has(WorkflowTown.Instance.For(b.Project.Key), WorkflowTown.Plaque);
        var size = stone ? new Size(Math.Round(11 * s), Math.Round(9 * s)) : new Size(Math.Round(7 * s), Math.Round(5 * s));
        return new Rect(Math.Round(b.Door.Left - 6 * s - size.Width), Math.Round(b.Door.Top + 12 * s), size.Width, size.Height);
    }

    /// <summary>The plaque is hoverable once the project has earned anything: it lists what and when.</summary>
    private void HitPlaque(Building b)
    {
        if (WorkflowTown.Instance.For(b.Project.Key).Count > 0) Hit(Inflate(PlaqueRect(b), 4, 4), PlaqueKey(b.Project.Key));
    }

    private Building? BuildingOf(string projectKey)
    {
        foreach (var b in _buildings) if (b.Project.Key == projectKey) return b;
        return null;
    }

    /// <summary>The subagent an errand runner stands for: the parent whose children it counts among.</summary>
    private static AgentActivityNode? ErrandParent(AgentActivityProject project, int index)
    {
        foreach (var agent in project.Agents)
        {
            if (agent.State != AgentActivityState.Working) continue;
            if (index < agent.ActiveChildren) return agent;
            index -= agent.ActiveChildren;
        }
        return null;
    }

    /// <summary>A firework over the building of every agent finishing now; a whole Bridge run gets three.</summary>
    private void DrawFireworks(DrawingContext dc)
    {
        foreach (var moment in Moments)
        {
            if (BuildingOf(moment.ProjectKey) is not { } b) continue;
            var age = Time - moment.Start;
            var s = b.Scale;
            var unit = Math.Max(1, s * 1.2);
            var top = (Look)b.Style == Look.Hall ? b.RoofPeak.Y - 38 * s : (Look)b.Style == Look.Forge ? b.Body.Top - 20 * s : b.RoofPeak.Y;
            var spread = (Hash(moment.AgentId, 9) - 0.5) * b.Body.Width * 0.35;
            var from = new Point(b.RoofPeak.X + spread * 0.3, top);
            // Close over its own roof, and never up among the sky label and the legend.
            var burst = new Point(b.RoofPeak.X + spread, Math.Max(110, top - (30 + 12 * Hash(moment.AgentId, 4)) * s));
            var seed = (int)(Hash(moment.AgentId) * 997);
            Firework(dc, from, burst, age, unit, seed);
            if (!moment.WholeRun) continue;
            Firework(dc, new Point(from.X - 24 * s, top), new Point(burst.X - 44 * s, burst.Y + 14 * s), age - 0.3, unit, seed + 101,
                WorkflowCelebrations.Seconds - 0.3);
            Firework(dc, new Point(from.X + 24 * s, top), new Point(burst.X + 44 * s, burst.Y + 6 * s), age - 0.6, unit, seed + 202,
                WorkflowCelebrations.Seconds - 0.6);
        }
    }

    /// <summary>The bard: while the Spotify extension has a track loaded he stands at the tavern's corner with a lute,
    /// strumming while it plays (a slower loop at night) and resting the lute while it is paused, under a pennant naming
    /// the track. Clicking him plays or pauses.</summary>
    private void DrawBard(DrawingContext dc, Rect bounds, double time)
    {
        if (MusicSource() is not { } music) return;
        Building? tavern = null;
        foreach (var b in _buildings) if ((Look)b.Style == Look.Tavern) { tavern = b; break; }
        if (tavern is null) return;
        var s = tavern.Scale;
        var px = Math.Clamp(Math.Round(2.4 * s * 2) / 2, 2, 4);
        var width = PixelArt.Width * px;
        var height = PixelArt.Height * px;
        var body = new Rect(Math.Max(2, Math.Round(tavern.Body.Left - width * 0.6)), Math.Round(_ground + 2), width, height);
        var rate = Sky.Night ? 2.2 : 4.5;
        var pose = !music.Playing ? PixelArt.Pose.LuteRest : (int)(time * rate) % 2 == 0 ? PixelArt.Pose.Lute : PixelArt.Pose.Strum;
        dc.DrawEllipse(Fill(Color.FromArgb(60, 0, 0, 0)), null, new Point(body.Left + width / 2, body.Bottom), width * 0.55, width * 0.16);
        var sprite = PixelArt.Character("tavern-bard", pose, px, night: Sky.Darkness * 0.55, shirt: PixelArt.C("#3E8E5A"));
        dc.PushTransform(new TranslateTransform(body.Left, body.Top));
        dc.DrawDrawing(sprite);
        dc.Pop();
        // A feathered cap, so he reads as the bard and not one of the agents.
        dc.DrawRectangle(Fill(PixelArt.C("#2F6B45")), null, new Rect(body.Left + 2 * px, body.Top - px, 5 * px, px));
        dc.DrawRectangle(Fill(PixelArt.C("#E8574F")), null, new Rect(body.Left + 6 * px, body.Top - 3 * px, px, 2 * px));
        if (music.Playing) Notes(dc, new Point(body.Left + 4 * px, body.Top - 2 * px), time, px / 2, Sky.Night ? 0.16 : 0.3, Color.FromRgb(0xFB, 0xEB, 0xC4));
        // The pennant overhead.
        if (_pennantFor != (music.Playing, music.Track, music.Artist))
        {
            _pennantFor = (music.Playing, music.Track, music.Artist);
            _pennant = (music.Playing ? "♪ " : "Paused · ") + music.Caption;
        }
        var caption = Text(_pennant, 10.5, Color.FromRgb(0xFB, 0xEB, 0xC4), bold: true, maxWidth: 220);
        var pennant = new Rect(Math.Round(Math.Clamp(body.Left + width / 2 - caption.Width / 2 - 8, 4, Math.Max(4, bounds.Width - caption.Width - 30))),
            Math.Round(body.Top - 28 - caption.Height), Math.Round(caption.Width + 16), Math.Round(caption.Height + 6));
        var cloth = Fill(music.Playing ? Color.FromRgb(0x8E, 0x2F, 0x2A) : Color.FromRgb(0x5A, 0x4A, 0x40));
        dc.DrawRectangle(Fill(Color.FromRgb(0x4A, 0x32, 0x1E)), null, new Rect(Math.Round(body.Left + width / 2 - 1), pennant.Bottom, 2, body.Top - pennant.Bottom - px));
        dc.DrawRectangle(cloth, null, pennant);
        dc.DrawRectangle(cloth, null, new Rect(pennant.Right, pennant.Top + 3, 6, pennant.Height - 6));
        dc.DrawRectangle(cloth, null, new Rect(pennant.Right + 6, pennant.Top + 6, 4, pennant.Height - 12));
        dc.DrawText(caption, new Point(pennant.Left + 8, pennant.Top + 3));
        Hit(Rect.Union(Inflate(body, 4, 4), pennant), MusicKey);
    }

    protected override void OnCommand(string key)
    {
        if (key != MusicKey) return;
        ToggleMusic();
        InvalidateVisual();
    }
}
