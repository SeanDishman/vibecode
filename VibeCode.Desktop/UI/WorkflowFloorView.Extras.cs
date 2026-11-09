using System.Windows;
using System.Windows.Media;

namespace VibeCode.UI;

// The office floor's extras: sprites in provider-guild colours with role props, neighbours clapping when a worker
// finishes, and a speaker on the library shelf for the music. The floor shows its town progress in its furniture
// (WorkflowFloorView.Upgrades.cs) rather than in keepsakes on the pods.
public sealed partial class WorkflowFloorView
{
    // Far enough to take in a pod's diagonal neighbour, not the next pod over.
    private const double ClapReach = 60;
    private static readonly Color Leather = PixelArt.C("#5C3A1E"), Brass = PixelArt.C("#E0B954"), Scroll = PixelArt.C("#F3E9D0"),
        Ribbon = PixelArt.C("#C8402E"),
        SpeakerBox = PixelArt.C("#2A2E36"), SpeakerCone = PixelArt.C("#11141A"), SpeakerTrim = PixelArt.C("#6A707A"),
        Lit = PixelArt.C("#5FD38D"), Unlit = PixelArt.C("#2E6A46");
    // Looked up for every worker every frame, so keyed by value: a cached sprite costs no allocation.
    private readonly Dictionary<(string Art, bool Flip, string Id, Color? Shirt, double Scale), Drawing> _sprites = new();

    /// <summary>A floor sprite in an agent's colours: its guild tabard (or a parent's, for an intern) over its own
    /// hashed hair and skin.</summary>
    private Drawing FloorSprite(string art, bool flip, string id, Color? shirt, double scale)
    {
        if (_sprites.TryGetValue((art, flip, id, shirt, scale), out var cached)) return cached;
        if (_sprites.Count > 3000) _sprites.Clear();
        var palette = PixelArt.Palette(id, shirt);
        palette['o'] = Outline;
        palette['w'] = Paper;
        var key = flip ? art + ":f" : art;
        return _sprites[(art, flip, id, shirt, scale)] = PixelArt.Sprite("floor:" + key + ":" + id + ":" + shirt, Sprites[key], palette, scale);
    }

    /// <summary>A Bridge worker's tool belt at the waist, the central planner's scroll in hand. Seated, the chair hides
    /// the belt.</summary>
    private void DrawProp(DrawingContext dc, AgentActivityNode agent, Point foot, string art, double drop)
    {
        switch (WorkflowGuilds.PropOf(agent))
        {
            case PixelArt.Prop.Belt when !art.StartsWith("sit", StringComparison.Ordinal):
                var half = art.StartsWith("side", StringComparison.Ordinal) ? 2 : 3;
                Box(dc, Leather, foot.X - half, foot.Y - 5 + drop, half * 2, 1);
                Box(dc, Brass, foot.X - 0.5, foot.Y - 5 + drop, 1, 1);
                break;
            case PixelArt.Prop.Scroll:
                Box(dc, Scroll, foot.X + 3, foot.Y - 9 + drop, 2, 4);
                Box(dc, Ribbon, foot.X + 3, foot.Y - 7 + drop, 2, 1);
                break;
        }
    }

    /// <summary>A worker near one that just finished claps: standing, it turns round to clap; at its desk it throws its
    /// arms up. Null for everyone else.</summary>
    private (string Art, bool Flip, double Drop)? Cheering(Walker walker, double time)
    {
        if (!walker.Arrived || walker.Goal is not { } spot) return null;
        foreach (var moment in Moments)
        {
            if (moment.AgentId == walker.Id || !_walkers.TryGetValue(moment.AgentId, out var star)) continue;
            var dx = star.Foot.X - walker.Foot.X;
            var dy = star.Foot.Y - walker.Foot.Y;
            if (dx * dx + dy * dy > ClapReach * ClapReach) continue;
            var beat = (int)Math.Floor(time * 5 + Hash(walker.Id) * 3) % 2 == 0;
            if (!spot.Seated) return (beat ? "clap1" : "clap2", false, 0);
            return spot.Facing == Facing.Up ? (beat ? "sit-cheer1" : "sit-cheer2", false, 3) : null;
        }
        return null;
    }

    /// <summary>Gold sparkles round the head of every worker finishing now, drawn over the speech bubbles.</summary>
    private void DrawFinishers(DrawingContext dc)
    {
        foreach (var moment in Moments)
            if (_walkers.TryGetValue(moment.AgentId, out var walker))
                Sparkles(dc, Dip(new Point(walker.Foot.X, walker.Foot.Y - 12)), Time - moment.Start, _p);
    }

    /// <summary>A speaker on top of the library's second bookshelf while the Spotify extension has a track: notes while
    /// it plays, a dim light while paused. Clicking it plays or pauses.</summary>
    private void DrawSpeaker(DrawingContext dc, double time)
    {
        if (_shelves.Count < 2 || MusicSource() is not { } music) return;
        var x = _shelves[1].X + 22;
        var y = _shelves[1].Y - 7;
        Box(dc, SpeakerBox, x, y, 6, 7);
        Box(dc, SpeakerCone, x + 1.5, y + 3, 3, 3);
        Box(dc, SpeakerTrim, x + 2.5, y + 4, 1, 1);
        Box(dc, SpeakerTrim, x + 1, y + 1, 2, 1);
        Box(dc, music.Playing ? Lit : Unlit, x + 4, y + 1, 1, 1);
        // The shelf stands against the top of the floor, so the notes drift sideways more than up.
        if (music.Playing) Notes(dc, Dip(new Point(x + 6, y)), time, _p * 0.7, Sky.Night ? 0.16 : 0.3, Color.FromRgb(0xF2, 0xF4, 0xF8), rise: 4);
        Hit(R(x - 1, y - 1, 8, 9), MusicKey);
    }

    protected override void OnCommand(string key)
    {
        if (key != MusicKey) return;
        ToggleMusic();
        InvalidateVisual();
    }
}
