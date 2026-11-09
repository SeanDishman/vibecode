using System.Windows;
using System.Windows.Media;

namespace VibeCode.UI;

// The office's extras: framed certificates for a project's town improvements, confetti in the room of an agent that
// just finished, and a radio on the first lounge's shelf playing whatever the Spotify extension plays.
public sealed partial class WorkflowOfficeView
{
    /// <summary>Where a room's certificates hang: a row on the back wall right of the clock, as many as fit.</summary>
    private (Rect Area, int Count) Certificates(Room room)
    {
        var earned = WorkflowTown.Instance.For(room.Project.Key).Count;
        if (earned == 0) return (Rect.Empty, 0);
        var p = _px;
        var left = Math.Round(room.Bounds.Left + room.Bounds.Width * 0.42 + 9 * p);
        var right = (room.Board.IsEmpty ? room.Lounge.Left : room.Board.Left) - 3 * p;
        var count = Math.Min(earned, (int)Math.Floor((right - left + 3 * p) / (11 * p)));
        return count <= 0 ? (Rect.Empty, 0) : (new Rect(left, Math.Round(room.Bounds.Top + 11 * p), count * 11 * p - 3 * p, 7 * p), count);
    }

    /// <summary>One framed certificate per earned improvement, baked into the room with its furniture.</summary>
    private void DrawCertificates(DrawingContext dc, Room room)
    {
        var (area, count) = Certificates(room);
        var p = _px;
        for (var i = 0; i < count; i++)
        {
            var frame = new Rect(Math.Round(area.Left + i * 11 * p), area.Top, Math.Round(8 * p), Math.Round(7 * p));
            dc.DrawRectangle(Fill(PixelArt.C("#5C4026")), null, frame);
            var paper = Inflate(frame, -p, -p);
            dc.DrawRectangle(Fill(PixelArt.C("#F3E9D0")), null, paper);
            dc.DrawRectangle(Fill(PixelArt.C("#9AA4B4")), null, new Rect(paper.Left + p, paper.Top + p, paper.Width - 2 * p, Math.Max(1, p * 0.6)));
            dc.DrawRectangle(Fill(PixelArt.C("#9AA4B4")), null, new Rect(paper.Left + p, paper.Top + 2.4 * p, paper.Width - 3 * p, Math.Max(1, p * 0.6)));
            dc.DrawRectangle(Fill(PixelArt.C("#E0B954")), null, new Rect(paper.Right - 2 * p, paper.Bottom - 2 * p, Math.Max(1, 1.4 * p), Math.Max(1, 1.4 * p)));
        }
    }

    private void HitCertificates(Room room)
    {
        if (Certificates(room) is { Count: > 0 } wall) Hit(Inflate(wall.Area, 3, 3), PlaqueKey(room.Project.Key));
    }

    private Room? RoomOf(string projectKey)
    {
        foreach (var room in _rooms) if (room.Project.Key == projectKey) return room;
        return null;
    }

    /// <summary>Confetti through the room of every agent finishing now.</summary>
    private void DrawConfetti(DrawingContext dc)
    {
        foreach (var moment in Moments)
            if (RoomOf(moment.ProjectKey) is { } room)
                Confetti(dc, room.Bounds, Time - moment.Start, _px, (int)(Hash(moment.AgentId) * 997) + (moment.WholeRun ? 500 : 0));
    }

    /// <summary>The radio on a shelf under the first lounge's window: notes while the music plays, a dim dial while it
    /// is paused. Clicking it plays or pauses.</summary>
    private void DrawRadio(DrawingContext dc, double time)
    {
        if (_rooms.Count == 0 || MusicSource() is not { } music) return;
        var room = _rooms[0];
        var p = _px;
        var shelf = Math.Round(Math.Min(room.Window.Bottom + 10 * p, room.Floor - 23 * p));
        var radio = new Rect(Math.Round(room.Window.Left + 4 * p), shelf - Math.Round(5 * p), Math.Round(9 * p), Math.Round(5 * p));
        if (radio.Top < room.Window.Bottom + 2 * p) return;
        dc.DrawRectangle(Fill(PixelArt.C("#5C4A3A")), null, new Rect(Math.Round(room.Window.Left + 2 * p), shelf, Math.Round(13 * p), Math.Max(1, Math.Round(p))));
        dc.DrawRectangle(Fill(PixelArt.C("#8A4E2E")), null, radio);
        dc.DrawRectangle(Fill(PixelArt.C("#2A2A30")), null, new Rect(radio.Left + p, radio.Top + p, 4 * p, radio.Height - 2 * p));
        for (var y = radio.Top + 1.5 * p; y < radio.Bottom - p; y += p)
            dc.DrawRectangle(Fill(PixelArt.C("#5A5A62")), null, new Rect(radio.Left + 1.5 * p, y, 3 * p, Math.Max(1, p * 0.4)));
        dc.DrawRectangle(Fill(music.Playing ? PixelArt.C("#F5CB5C") : PixelArt.C("#7A6A4A")), null, new Rect(radio.Right - 3 * p, radio.Top + 1.5 * p, 2 * p, 2 * p));
        dc.DrawRectangle(Fill(PixelArt.C("#2A2A30")), null, new Rect(radio.Right - 2 * p, radio.Top - 4 * p, Math.Max(1, p * 0.5), 4 * p));
        if (music.Playing) Notes(dc, new Point(radio.Left + 3 * p, radio.Top - p), time, p * 0.6, Sky.Night ? 0.16 : 0.3, Color.FromRgb(0xF2, 0xF4, 0xF8));
        Hit(Inflate(radio, 4, 6), MusicKey);
    }

    protected override void OnCommand(string key)
    {
        if (key != MusicKey) return;
        ToggleMusic();
        InvalidateVisual();
    }
}
