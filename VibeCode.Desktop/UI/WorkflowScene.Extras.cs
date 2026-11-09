using System.Windows;
using System.Windows.Media;
using VibeCode.Services;

namespace VibeCode.UI;

/// <summary>What the Spotify extension is playing, for the tavern bard, the office radio and the floor's speaker.</summary>
public readonly record struct WallMusic(bool Playing, string Track, string Artist)
{
    /// <summary>"Track – Artist" for the bard's pennant.</summary>
    public string Caption => Artist.Length > 0 ? Track + " – " + Artist : Track;
}

// The drawn views' shared extras: finish moments, the pixel effects they use, music from the Spotify extension, the
// provider-guild legend and the town-improvements card.
public abstract partial class WorkflowScene : IWorkflowCelebrating
{
    /// <summary>A finish playing over one agent, in scene time.</summary>
    protected readonly record struct Moment(string AgentId, string ProjectKey, double Start, bool WholeRun);

    private readonly List<Moment> _moments = new();
    private IReadOnlyList<AgentActivityProject>? _guildsFor;
    private bool _guildsOn;
    private IReadOnlyList<(string Label, Color Tabard)> _guilds = [];
    private static readonly Color[] Sparks =
        [PixelArt.C("#F5CB5C"), PixelArt.C("#F27BA8"), PixelArt.C("#7FD8F0"), PixelArt.C("#FFF6DC"), PixelArt.C("#A8F0BE")];
    // Saturated and warm, so a burst reads against a pale day sky as well as against the stars.
    private static readonly Color[] Fireworks =
        [PixelArt.C("#FFC93C"), PixelArt.C("#FF5C8A"), PixelArt.C("#FF8C2E"), PixelArt.C("#B47CFF"), PixelArt.C("#FF4D4D")];

    /// <summary>The scene's click target for the bard, the radio and the speaker: play or pause the music.</summary>
    protected const string MusicKey = "music";

    /// <summary>The playing track, or null while the Spotify extension is off, not connected or has nothing loaded.
    /// Read when a frame is drawn; tests swap it for a fixture.</summary>
    internal static Func<WallMusic?> MusicSource = () =>
    {
        // The main window keeps this state fresh while the extension is on (a one-second heartbeat).
        if (!AppSettings.Current.SpotifyEnabled) return null;
        var spotify = SpotifyService.Instance;
        return spotify.IsConnected && spotify.HasTrack ? new WallMusic(spotify.IsPlaying, spotify.Track, spotify.Artist) : null;
    };

    /// <summary>Play or pause from the scene. Tests swap it so a click never reaches Spotify.</summary>
    internal static Action ToggleMusic = () => _ = SpotifyService.Instance.ToggleAsync();

    public void Celebrate(AgentActivityNode agent, bool wholeRun)
    {
        if (!IsAnimating) return;
        for (var i = _moments.Count - 1; i >= 0; i--) if (_moments[i].AgentId == agent.Id) _moments.RemoveAt(i);
        if (_moments.Count >= 12) _moments.RemoveAt(0);
        _moments.Add(new(agent.Id, agent.ProjectKey, Time, wholeRun));
        InvalidateVisual();
    }

    /// <summary>The moments still playing. Motion stopping ends them all: with the clock stopped a firework would
    /// hang in the sky.</summary>
    protected List<Moment> Moments
    {
        get
        {
            if (!IsAnimating) { _moments.Clear(); return _moments; }
            var now = Time;
            for (var i = _moments.Count - 1; i >= 0; i--)
                if (now - _moments[i].Start > WorkflowCelebrations.Seconds) _moments.RemoveAt(i);
            return _moments;
        }
    }

    /// <summary>Seconds into the moment playing over <paramref name="agentId"/>, or -1 when there is none.</summary>
    protected double MomentAge(string agentId)
    {
        foreach (var moment in Moments) if (moment.AgentId == agentId) return Time - moment.Start;
        return -1;
    }

    // ---- pixel effects --------------------------------------------------------------------------------------------

    /// <summary>A pixel firework: a rocket climbs from <paramref name="from"/>, a white flash where it bursts at
    /// <paramref name="burst"/>, then eighteen sparks streak out, drop a little and fade out by <paramref name="life"/>.</summary>
    protected void Firework(DrawingContext dc, Point from, Point burst, double age, double unit, int seed, double life = WorkflowCelebrations.Seconds)
    {
        var px = Math.Max(3, Math.Round(unit * 2.2));
        const double Rise = 0.45;
        if (age < 0) return;
        if (age < Rise)
        {
            var t = age / Rise;
            var y = from.Y + (burst.Y - from.Y) * (1 - (1 - t) * (1 - t));
            for (var k = 3; k >= 0; k--)
                dc.DrawRectangle(Fill(k == 0 ? Colors.White : Alpha(Fireworks[0], 0.7 / k)), null,
                    new Rect(Math.Round(burst.X - px / 2), Math.Round(y + k * px * 1.3), px, px));
            return;
        }
        var s = age - Rise;
        if (s < 0.2)
        {
            dc.PushOpacity(1 - s / 0.2);
            var arm = px * 3;
            dc.DrawRectangle(Fill(Colors.White), null, new Rect(Math.Round(burst.X - arm), Math.Round(burst.Y - px / 2), arm * 2, px));
            dc.DrawRectangle(Fill(Colors.White), null, new Rect(Math.Round(burst.X - px / 2), Math.Round(burst.Y - arm), px, arm * 2));
            dc.Pop();
        }
        var fade = Math.Clamp(1 - s / (life - Rise), 0, 1);
        // Each spark is a bright head with a dimmer pixel behind it, so it reads as a streak rather than a dot.
        for (var pass = 0; pass < 2; pass++)
        {
            dc.PushOpacity(pass == 0 ? fade * 0.45 : fade);
            for (var i = 0; i < 18; i++)
            {
                var angle = (i + SceneWeatherFx.Noise(seed + i) * 0.5) / 18 * Math.PI * 2;
                var reach = (20 + 12 * SceneWeatherFx.Noise(seed + i + 40)) * unit * (1 - Math.Exp(-s * 3)) * (pass == 0 ? 0.78 : 1);
                var x = burst.X + Math.Cos(angle) * reach;
                var y = burst.Y + Math.Sin(angle) * reach + 6 * unit * s * s;
                dc.DrawRectangle(Fill(Fireworks[(i + seed) % Fireworks.Length]), null, new Rect(Math.Round(x - px / 2), Math.Round(y - px / 2), px, px));
            }
            dc.Pop();
        }
    }

    /// <summary>Paper confetti tumbling down through <paramref name="area"/> from its top edge.</summary>
    protected void Confetti(DrawingContext dc, Rect area, double age, double unit, int seed)
    {
        if (age < 0) return;
        var u = Math.Max(2, unit);
        dc.PushOpacity(Math.Clamp((WorkflowCelebrations.Seconds - age) / 0.6, 0, 1));
        for (var i = 0; i < 44; i++)
        {
            var t = age - SceneWeatherFx.Noise(seed + i * 7 + 1) * 0.5;
            if (t < 0) continue;
            var x = area.Left + SceneWeatherFx.Noise(seed + i * 7) * area.Width + Math.Sin(t * 3.4 + i) * 2.5 * u;
            var y = area.Top + t * area.Height * (0.3 + 0.2 * SceneWeatherFx.Noise(seed + i * 7 + 2));
            if (y > area.Bottom - 2 * u || x < area.Left || x > area.Right - 2 * u) continue;
            // Tumbling: each slip narrows and widens as it turns over.
            var width = Math.Max(2, Math.Round(u * (0.5 + Math.Abs(Math.Sin(t * 7 + i)))));
            var ink = i % 2 == 0 ? Fireworks[(i / 2) % Fireworks.Length] : Sparks[(i / 2) % Sparks.Length];
            dc.DrawRectangle(Fill(ink), null, new Rect(Math.Round(x), Math.Round(y), width, Math.Max(2, Math.Round(u * 1.4))));
        }
        dc.Pop();
    }

    /// <summary>Gold sparkles circling round a head that just finished.</summary>
    protected void Sparkles(DrawingContext dc, Point around, double age, double unit)
    {
        if (age < 0) return;
        var px = Math.Max(2, Math.Round(unit * 0.8));
        dc.PushOpacity(Math.Clamp((WorkflowCelebrations.Seconds - age) / 0.6, 0, 1));
        for (var i = 0; i < 6; i++)
        {
            var angle = i / 6.0 * Math.PI * 2 + age * 1.8;
            var reach = (7 + 2 * age) * unit;
            var center = new Point(Math.Round(around.X + Math.Cos(angle) * reach), Math.Round(around.Y + Math.Sin(angle) * reach * 0.5 - age * unit));
            var ink = Fill(i % 2 == 0 ? Sparks[0] : Sparks[3]);
            var arm = Math.Abs(Math.Sin(age * 6 + i)) > 0.4 ? px : 0;
            dc.DrawRectangle(ink, null, new Rect(center.X, center.Y, px, px));
            if (arm <= 0) continue;
            dc.DrawRectangle(ink, null, new Rect(center.X - arm, center.Y, arm, px));
            dc.DrawRectangle(ink, null, new Rect(center.X + px, center.Y, arm, px));
            dc.DrawRectangle(ink, null, new Rect(center.X, center.Y - arm, px, arm));
            dc.DrawRectangle(ink, null, new Rect(center.X, center.Y + px, px, arm));
        }
        dc.Pop();
    }

    /// <summary>Pixel quavers drifting up and away from <paramref name="from"/>, <paramref name="rise"/> units high;
    /// <paramref name="rate"/> is loops per second, slower at night.</summary>
    protected void Notes(DrawingContext dc, Point from, double time, double unit, double rate, Color ink, int count = 3, double rise = 16)
    {
        var u = Math.Max(1, Math.Round(unit));
        var brush = Fill(ink);
        for (var i = 0; i < count; i++)
        {
            var t = (time * rate + i / (double)count) % 1;
            var x = Math.Round(from.X + Math.Sin(t * 5 + i * 2) * 2.5 * u + t * 7 * u);
            var y = Math.Round(from.Y - t * rise * u);
            dc.PushOpacity(Math.Min(1, (1 - t) * 1.6));
            dc.DrawRectangle(brush, null, new Rect(x, y + 3 * u, 2 * u, 2 * u));
            dc.DrawRectangle(brush, null, new Rect(x + u, y, u, 4 * u));
            dc.DrawRectangle(brush, null, new Rect(x + 2 * u, y, u, 2 * u));
            dc.Pop();
        }
    }

    // ---- guild legend -----------------------------------------------------------------------------------------------

    /// <summary>A chip under a scene's sky label naming the provider colours on the wall: one swatch per guild present.
    /// Nothing while guild colours are off.</summary>
    protected void DrawGuildLegend(DrawingContext dc, Point at)
    {
        if (!ReferenceEquals(_guildsFor, Projects) || _guildsOn != WorkflowGuilds.Enabled)
        {
            _guildsFor = Projects;
            _guildsOn = WorkflowGuilds.Enabled;
            _guilds = WorkflowGuilds.Present(Projects);
        }
        if (_guilds.Count == 0) return;
        var ink = Color.FromRgb(0xF2, 0xF4, 0xF8);
        var width = 12.0;
        foreach (var (label, _) in _guilds) width += 14 + Text(label, 10.5, ink).Width + 10;
        var height = Text("Ag", 10.5, ink).Height + 10;
        dc.DrawRoundedRectangle(Fill(Color.FromArgb(150, 10, 12, 20)), null, new Rect(at.X, at.Y, width - 4, height), 7, 7);
        var x = at.X + 10;
        foreach (var (label, tabard) in _guilds)
        {
            var text = Text(label, 10.5, ink);
            dc.DrawRectangle(Fill(tabard), null, new Rect(Math.Round(x), Math.Round(at.Y + (height - 9) / 2), 9, 9));
            dc.DrawText(text, new Point(Math.Round(x + 13), Math.Round(at.Y + (height - text.Height) / 2)));
            x += 14 + text.Width + 10;
        }
    }

    // ---- town improvements ----------------------------------------------------------------------------------------

    /// <summary>The hover key of a project's plaque or certificates.</summary>
    protected static string PlaqueKey(string projectKey) => "plaque:" + projectKey;

    /// <summary>The project whose plaque is under the pointer, if any.</summary>
    protected AgentActivityProject? HoveredPlaque => HoverKey is { } key && key.StartsWith("plaque:", StringComparison.Ordinal)
        ? Projects.FirstOrDefault(p => PlaqueKey(p.Key) == key) : null;

    /// <summary>The card a plaque opens: each improvement, why it was earned and the day it was.</summary>
    protected void DrawImprovementsCard(DrawingContext dc, Rect bounds, Rect anchor, AgentActivityProject project,
        IReadOnlyList<TownImprovement> earned, Color background, Color border, Color ink, Color muted)
    {
        var width = Math.Min(330, bounds.Width - 24);
        var height = 58 + earned.Count * 22;
        var x = Math.Clamp(anchor.Left + anchor.Width / 2 - width / 2, 12, Math.Max(12, bounds.Width - width - 12));
        var y = anchor.Top - height - 12 >= 12 ? anchor.Top - height - 12 : Math.Min(bounds.Height - height - 12, anchor.Bottom + 12);
        var card = new Rect(Math.Round(x), Math.Round(y), width, height);
        dc.DrawRoundedRectangle(Fill(Color.FromArgb(100, 0, 0, 0)), null, new Rect(card.Left + 3, card.Top + 5, card.Width, card.Height), 9, 9);
        dc.DrawRoundedRectangle(Fill(background), Stroke(border, 1.5), card, 8, 8);
        dc.DrawText(Text(project.Name + " · town improvements", 13.5, ink, bold: true, maxWidth: width - 28), new Point(card.Left + 14, card.Top + 10));
        dc.DrawText(Text("Earned from this project's own history", 10.5, muted, maxWidth: width - 28), new Point(card.Left + 14, card.Top + 30));
        var row = card.Top + 50;
        foreach (var item in earned)
        {
            var date = Text(item.EarnedLocal.ToString("d MMM yyyy"), 11, muted);
            dc.DrawRectangle(Fill(Sparks[0]), null, new Rect(card.Left + 15, row + 6, 5, 5));
            dc.DrawText(Text(item.Title + " – " + item.Reason, 11.5, ink, maxWidth: width - 46 - date.Width), new Point(card.Left + 26, row));
            dc.DrawText(date, new Point(card.Right - 14 - date.Width, row + 1));
            row += 22;
        }
    }
}
