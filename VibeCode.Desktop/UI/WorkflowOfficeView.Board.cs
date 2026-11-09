using System.Windows;
using System.Windows.Media;

namespace VibeCode.UI;

// Each room's whiteboard: the project at a glance. Magnets count who is working, who needs you and who failed, beside
// what the room has spent; a sticky note pins every agent that needs a look; every agent's own todo list is a strip of
// checkboxes with the item in progress written out; and the rest of the team fills what is left: who is on which task
// and for how long, or what an idle agent last reported. Hovering the board opens all of it as a card; clicking it
// filters the wall to the room, as clicking the room does.
public sealed partial class WorkflowOfficeView
{
    private static readonly Color BoardInk = PixelArt.C("#2A3240");
    private static readonly Color BoardMuted = PixelArt.C("#6A7486");
    private static readonly Color BoardRule = PixelArt.C("#C9D0DA");
    private static readonly Color MarkerGreen = PixelArt.C("#2E9E5B");
    private static readonly Color MarkerAmber = PixelArt.C("#D08A16");
    private static readonly Color MarkerRed = PixelArt.C("#D04848");
    private static readonly Color MarkerBlue = PixelArt.C("#2F62C8");
    private static readonly Color NoteYellow = PixelArt.C("#FFE27A");
    private static readonly Color NotePink = PixelArt.C("#FFB8B8");
    private const string PendingGlyph = "";   // circle ring

    private sealed record BoardPlan(AgentActivityNode Agent, IReadOnlyList<TodoEntry> Items, int Done);

    /// <summary>A teammate not already pinned: its task, or for an idle agent what it last reported.</summary>
    private sealed record BoardEntry(AgentActivityNode Agent, string Text, bool Report);

    private sealed record BoardFacts(int Working, int Waiting, int Failed, IReadOnlyList<AgentActivityNode> Alerts,
        IReadOnlyList<BoardPlan> Plans, IReadOnlyList<BoardEntry> Team,
        double Spend, bool Estimated, AgentActivityNode? Fullest, double FullestPercent);

    private static string BoardKey(string projectKey) => "board:" + projectKey;

    private Room? HoveredBoard => HoverKey is { } key && key.StartsWith("board:", StringComparison.Ordinal)
        ? _rooms.FirstOrDefault(room => BoardKey(room.Project.Key) == key) : null;

    /// <summary>Where a room's board hangs: right of the clock and every certificate the room has earned, clear of the
    /// desks, up to the lounge wall; from under the ceiling lights to just above a standing agent's head. A wide room
    /// gets a wide board; a room with no free wall gets none.</summary>
    private Rect BoardRect(Room room)
    {
        var p = _px;
        var cell = room.Bounds;
        var right = Math.Round(room.Lounge.Left - 8 * p);
        // Certificates start 9p right of the clock, one every 11p (Certificates).
        var earned = WorkflowTown.Instance.For(room.Project.Key).Count;
        var wall = cell.Left + cell.Width * 0.42 + 9 * p + earned * 11 * p + 2 * p;
        var desks = room.Desks.Count > 0 ? room.Desks[^1].Desk.Right + 4 * p : cell.Left + 8 * p;
        // Never smaller than the 36p board this wall always had: certificates give way first, the desks never do.
        var left = Math.Round(Math.Max(Math.Min(Math.Max(wall, right - 84 * p), right - 36 * p), desks));
        var top = Math.Round(cell.Top + 12 * p);
        var bottom = Math.Round(room.Floor - 18 * p);
        return right - left < 32 * p || bottom - top < 18 * p ? Rect.Empty : new Rect(left, top, right - left, bottom - top);
    }

    private void HitBoard(Room room)
    {
        if (!room.Board.IsEmpty) Hit(Inflate(room.Board, _px, _px), BoardKey(room.Project.Key), room.Project);
    }

    private static BoardFacts Facts(AgentActivityProject project)
    {
        var agents = project.Agents;
        var alerts = agents.Where(a => a.State == AgentActivityState.Waiting)
            .Concat(agents.Where(a => a.State == AgentActivityState.Error)).ToList();
        var plans = agents.Where(a => a.PlanTotal > 0 && !a.IsCentralPlanner)
            .Select(a => new BoardPlan(a, a.Chat.Todos.ToList(), a.PlanDone))
            .OrderByDescending(plan => plan.Agent.State == AgentActivityState.Working)
            .ThenBy(plan => plan.Done == plan.Items.Count)
            .ToList();
        var pinned = alerts.Concat(plans.Select(plan => plan.Agent)).ToHashSet();
        var team = agents.Where(a => !pinned.Contains(a))
            .Select(a => a.State == AgentActivityState.Idle && (a.Reported.Length > 0 || a.Last is { Failed: false })
                ? new BoardEntry(a, a.Reported.Length > 0 ? a.Reported : a.Last!.Text, true)
                : new BoardEntry(a, a.Task, false))
            .OrderBy(entry => entry.Agent.State == AgentActivityState.Working ? 0 : entry.Report ? 1 : 2)
            .ThenByDescending(entry => entry.Agent.LastActiveAt ?? DateTimeOffset.MinValue)
            .ToList();
        double spend = 0;
        var estimated = false;
        foreach (var chat in agents.Select(a => a.Chat).Distinct())
        {
            var (amount, guess) = chat.DisplayedCost;
            spend += amount;
            estimated |= guess && amount > 0;
        }
        var fullest = agents.Where(a => !a.IsCentralPlanner).OrderByDescending(a => a.Chat.CtxPercentValue).FirstOrDefault();
        return new(project.Working, agents.Count(a => a.State == AgentActivityState.Waiting),
            agents.Count(a => a.State == AgentActivityState.Error), alerts, plans, team, spend, estimated,
            fullest, fullest?.Chat.CtxPercentValue ?? 0);
    }

    private static string AlertText(AgentActivityNode agent) =>
        agent.State == AgentActivityState.Error && agent.Last is { Failed: true } failed ? "Failed: " + failed.Text
        : agent.State == AgentActivityState.Error ? "Failed" : agent.Status;

    private static string Money(double usd) => usd >= 100 ? $"${usd:0}" : usd >= 10 ? $"${usd:0.0}" : $"${usd:0.00}";

    // ---- the board on the wall ------------------------------------------------------------------------------------

    private void DrawWhiteboard(DrawingContext dc, Room room, double time, DateTime nowUtc)
    {
        if (room.Board.IsEmpty) return;
        var b = room.Board;
        var p = _px;
        var facts = Facts(room.Project);
        DrawMarkerTray(dc, b);
        if (HoverKey == BoardKey(room.Project.Key))
            dc.DrawRectangle(null, Stroke(Ink("Accent", Color.FromRgb(0x7C, 0xB7, 0xFF)), 2), Inflate(b, 2 * p, 2 * p));
        var size = Math.Clamp(3.2 * p, 9.5, 12.5);
        var small = Math.Clamp(2.8 * p, 9, 11);
        var line = Text("Ag", small, BoardInk).Height;
        var x = b.Left + 2 * p;
        var width = b.Width - 4 * p;
        var bottom = b.Bottom - 1.5 * p;
        var y = BoardHeader(dc, facts, x, b.Top + 1.5 * p, width, size, time);
        dc.DrawRectangle(Fill(BoardRule), null, new Rect(Math.Round(x), Math.Round(y), Math.Round(width), Math.Max(1, Math.Round(p * 0.5))));
        y += 1.5 * p;

        // A nearly full context keeps the bottom line, so everything above stops short of it.
        var warning = facts.FullestPercent >= 80 && facts.Fullest is { } fullest ? $"{fullest.Label} context {facts.FullestPercent:0}% full" : null;
        if (warning is not null) bottom -= line + p;
        var wrote = false;

        // Sticky notes: whoever needs you, then whoever failed. The last one counts any that do not fit.
        var notes = Math.Min(facts.Alerts.Count, facts.Plans.Count > 0 ? 2 : 3);
        for (var i = 0; i < notes && y + line + p <= bottom; i++)
        {
            var agent = facts.Alerts[i];
            var rest = facts.Alerts.Count - i;
            var text = i == notes - 1 && rest > 1 ? $"+{rest} need a look" : $"{agent.Label} · {AlertText(agent)}";
            y = StickyNote(dc, x, y, width, line, text, agent.State == AgentActivityState.Error ? NotePink : NoteYellow, i, small);
            wrote = true;
        }
        foreach (var plan in facts.Plans)
        {
            if (y + line + 2.5 * p > bottom) break;
            y = PlanLane(dc, plan, x, y, width, bottom, small, line, time);
            wrote = true;
        }
        foreach (var entry in facts.Team)
        {
            if (y + line > bottom) break;
            TeamLine(dc, entry, x, y, width, line, small, nowUtc);
            y += line;
            wrote = true;
        }
        if (!wrote)
        {
            // Nothing pinned yet: a doodle where the work will go.
            var ink = Stroke(PixelArt.C("#7C8CF0"), Math.Max(1, p * 0.6));
            var mid = Math.Min(b.Bottom - 6 * p, y + 10 * p);
            dc.DrawLine(ink, new Point(x + 2 * p, mid + 2 * p), new Point(x + 10 * p, mid - 5 * p));
            dc.DrawLine(ink, new Point(x + 10 * p, mid - 5 * p), new Point(x + 18 * p, mid));
            dc.DrawLine(ink, new Point(x + 18 * p, mid), new Point(Math.Min(x + width, x + 30 * p), mid - 7 * p));
        }
        if (warning is not null)
            dc.DrawText(Text(warning, small, MarkerAmber, bold: true, maxWidth: width), new Point(Math.Round(x), Math.Round(b.Bottom - 1.5 * p - line)));
    }

    /// <summary>The magnets along the top, and the room's spend on the right. Returns where the rule goes.</summary>
    private double BoardHeader(DrawingContext dc, BoardFacts facts, double x, double y, double width, double size, double time)
    {
        var p = _px;
        var spend = facts.Spend >= 0.005 ? Text((facts.Estimated ? "~" : "") + Money(facts.Spend), size, BoardMuted, bold: true) : null;
        var room = width - (spend is null ? 0 : spend.Width + 2 * p);
        var height = Text("Ag", size, BoardInk, bold: true).Height;
        var counts = new List<(int Count, string Word, Color Ink)>();
        if (facts.Working > 0) counts.Add((facts.Working, "working", MarkerGreen));
        if (facts.Waiting > 0) counts.Add((facts.Waiting, facts.Waiting == 1 ? "needs you" : "need you", MarkerAmber));
        if (facts.Failed > 0) counts.Add((facts.Failed, "failed", MarkerRed));
        if (counts.Count == 0)
            dc.DrawText(Text("All quiet", size, BoardMuted, bold: true, maxWidth: room), new Point(Math.Round(x), Math.Round(y)));
        var magnet = Math.Max(2, Math.Round(2 * p));
        // Words while they all fit, bare numbers on a narrow board.
        var words = counts.Sum(c => magnet + p + Text($"{c.Count} {c.Word}", size, c.Ink, bold: true).Width + 2 * p) <= room + 2 * p;
        var cx = x;
        foreach (var (count, word, ink) in counts)
        {
            var label = Text(words ? $"{count} {word}" : count.ToString(), size, ink, bold: true);
            if (cx + magnet + p + label.Width > x + room) break;
            var glow = ink == MarkerGreen ? 0.7 + 0.3 * Math.Sin(time * 3) : 1;
            dc.DrawRectangle(Fill(Alpha(ink, glow)), null, new Rect(Math.Round(cx), Math.Round(y + (height - magnet) / 2), magnet, magnet));
            dc.DrawText(label, new Point(Math.Round(cx + magnet + p), Math.Round(y)));
            cx += magnet + p + label.Width + 2 * p;
        }
        if (spend is not null) dc.DrawText(spend, new Point(Math.Round(x + width - spend.Width), Math.Round(y)));
        return y + height + 0.5 * p;
    }

    private double StickyNote(DrawingContext dc, double x, double y, double width, double line, string text, Color paper, int index, double small)
    {
        var p = _px;
        // Pinned by hand: every other note sits a pixel in, and each casts a pixel of shadow.
        var inset = index % 2 == 1 ? p : 0;
        var note = new Rect(Math.Round(x + inset), Math.Round(y), Math.Round(width - inset - p), Math.Round(line + p));
        dc.DrawRectangle(Fill(PixelArt.Shade(paper, 0.72)), null, new Rect(note.Left + p, note.Top + p, note.Width, note.Height));
        dc.DrawRectangle(Fill(paper), null, note);
        var pin = Math.Max(2, Math.Round(1.5 * p));
        dc.DrawRectangle(Fill(paper == NotePink ? MarkerBlue : MarkerRed), null, new Rect(note.Left + p, note.Top - Math.Round(p * 0.5), pin, pin));
        dc.DrawText(Text(text, small, PixelArt.Shade(paper, 0.3), bold: true, maxWidth: note.Width - pin - 3 * p),
            new Point(Math.Round(note.Left + pin + 2 * p), Math.Round(note.Top + p * 0.5)));
        return note.Bottom + 1.5 * p;
    }

    /// <summary>One agent's todo list: its name and count, a checkbox per item (a bar when they do not fit), and the item
    /// in progress, or the next one, written out.</summary>
    private double PlanLane(DrawingContext dc, BoardPlan plan, double x, double y, double width, double bottom, double small, double line, double time)
    {
        var p = _px;
        var total = plan.Items.Count;
        var count = Text($"{plan.Done}/{total}", small, BoardMuted, bold: true);
        dc.DrawText(count, new Point(Math.Round(x + width - count.Width), Math.Round(y)));
        dc.DrawText(Text(plan.Agent.Label, small, BoardInk, bold: true, maxWidth: width - count.Width - 2 * p), new Point(Math.Round(x), Math.Round(y)));
        y += line;
        var box = Math.Max(2, Math.Round(2 * p));
        var gap = Math.Max(1, Math.Round(0.75 * p));
        var blink = 0.55 + 0.45 * Math.Sin(time * 4);
        if (total * (box + gap) - gap <= width)
            for (var i = 0; i < total; i++)
            {
                var status = plan.Items[i].Status;
                var fill = status == "completed" ? MarkerGreen : status == "in_progress" ? Alpha(MarkerAmber, blink) : BoardRule;
                dc.DrawRectangle(Fill(fill), null, new Rect(Math.Round(x + i * (box + gap)), Math.Round(y), box, box));
            }
        else
        {
            var done = Math.Round(width * plan.Done / total);
            var active = plan.Items.Count(t => t.Status == "in_progress");
            dc.DrawRectangle(Fill(BoardRule), null, new Rect(Math.Round(x), Math.Round(y), Math.Round(width), box));
            dc.DrawRectangle(Fill(MarkerGreen), null, new Rect(Math.Round(x), Math.Round(y), done, box));
            if (active > 0)
                dc.DrawRectangle(Fill(Alpha(MarkerAmber, blink)), null, new Rect(Math.Round(x + done), Math.Round(y), Math.Max(box, Math.Round(width * active / total)), box));
        }
        y += box + 0.75 * p;
        if (y + line <= bottom)
        {
            var current = plan.Items.FirstOrDefault(t => t.Status == "in_progress") ?? plan.Items.FirstOrDefault(t => t.Status != "completed");
            var (text, ink) = current is null ? ("All done", MarkerGreen)
                : current.Status == "in_progress" ? ("▸ " + current.Text, BoardInk) : ("Next: " + current.Text, BoardMuted);
            dc.DrawText(Text(text, small, ink, bold: current is null, maxWidth: width), new Point(Math.Round(x), Math.Round(y)));
            y += line;
        }
        return y + p;
    }

    /// <summary>One teammate: a magnet in its state's colour (a tick for a report), its name, how long it has been
    /// working, and its task or report.</summary>
    private void TeamLine(DrawingContext dc, BoardEntry entry, double x, double y, double width, double line, double small, DateTime nowUtc)
    {
        var p = _px;
        var agent = entry.Agent;
        if (entry.Report) Tick(dc, x, y + line / 2, MarkerGreen);
        else
        {
            var dot = Math.Max(2, Math.Round(1.5 * p));
            var ink = agent.State switch
            {
                AgentActivityState.Working => MarkerGreen,
                AgentActivityState.Waiting or AgentActivityState.Paused => MarkerAmber,
                AgentActivityState.Error => MarkerRed,
                _ => PixelArt.C("#9AA4B4"),
            };
            dc.DrawRectangle(Fill(ink), null, new Rect(Math.Round(x + p * 0.5), Math.Round(y + (line - dot) / 2), dot, dot));
        }
        var name = Text(agent.Label, small, BoardInk, bold: true, maxWidth: width * 0.45);
        dc.DrawText(name, new Point(Math.Round(x + 4 * p), Math.Round(y)));
        var since = Since(agent, nowUtc);
        var rest = (since.Length > 0 ? since + " · " : "") + entry.Text;
        var left = x + 4 * p + name.Width + p;
        if (rest.Length > 0 && x + width - left > 6 * p)
            dc.DrawText(Text(rest, small, entry.Report ? BoardInk : BoardMuted, maxWidth: x + width - left), new Point(Math.Round(left), Math.Round(y)));
    }

    private void Tick(DrawingContext dc, double x, double middle, Color color)
    {
        var u = Math.Max(1, Math.Round(_px * 0.6));
        var ink = Fill(color);
        foreach (var (dx, dy) in new[] { (0, 0), (1, 1), (2, 0), (3, -1) })
            dc.DrawRectangle(ink, null, new Rect(Math.Round(x + dx * u), Math.Round(middle + dy * u - u / 2), u, u));
    }

    /// <summary>The ledge under the board, with its three markers.</summary>
    private void DrawMarkerTray(DrawingContext dc, Rect b)
    {
        var p = _px;
        var ledge = new Rect(Math.Round(b.Left + 3 * p), Math.Round(b.Bottom + p), Math.Round(b.Width - 6 * p), Math.Max(1, Math.Round(1.5 * p)));
        dc.DrawRectangle(Fill(PixelArt.C("#7E858E")), null, ledge);
        var caps = new[] { MarkerRed, MarkerBlue, MarkerGreen };
        for (var i = 0; i < caps.Length; i++)
        {
            var marker = new Rect(Math.Round(ledge.Left + 2 * p + i * 6 * p), ledge.Top, Math.Round(4 * p), Math.Max(1, Math.Round(p)));
            dc.DrawRectangle(Fill(PixelArt.C("#E8EAEE")), null, marker);
            dc.DrawRectangle(Fill(caps[i]), null, new Rect(marker.Left, marker.Top, Math.Max(1, Math.Round(1.2 * p)), marker.Height));
        }
    }

    // ---- the board as a card ----------------------------------------------------------------------------------------

    private void DrawBoardCard(DrawingContext dc, Rect bounds, Room room, DateTime nowUtc)
    {
        const double Row = 19;
        var facts = Facts(room.Project);
        var (bg, border, text, muted) = CardColors();
        var rows = new List<(string Glyph, Color GlyphInk, string Text, Color Ink, bool Heading)>();
        if (facts.Alerts.Count > 0)
        {
            rows.Add(("", muted, "NEEDS A LOOK", muted, true));
            foreach (var agent in facts.Alerts)
                rows.Add((agent.State == AgentActivityState.Error ? WorkflowActivity.ErrorGlyph : WorkflowActivity.WaitingGlyph,
                    StateColor(agent.State), $"{agent.Label} · {AlertText(agent)}", text, false));
        }
        foreach (var plan in facts.Plans)
        {
            rows.Add(("", muted, $"{plan.Agent.Label.ToUpperInvariant()} · {plan.Done}/{plan.Items.Count} · {plan.Agent.Task}", muted, true));
            foreach (var item in plan.Items.Take(8))
                rows.Add(item.Status == "completed" ? (WorkflowActivity.DoneGlyph, StateColor(AgentActivityState.Working), item.Text, muted, false)
                    : item.Status == "in_progress" ? (WorkflowActivity.WorkingGlyph, StateColor(AgentActivityState.Waiting), item.Text, text, false)
                    : (PendingGlyph, muted, item.Text, text, false));
            if (plan.Items.Count > 8) rows.Add(("", muted, $"+{plan.Items.Count - 8} more", muted, false));
        }
        if (facts.Team.Count > 0)
        {
            rows.Add(("", muted, "TEAM", muted, true));
            foreach (var entry in facts.Team.Take(8))
            {
                var since = Since(entry.Agent, nowUtc);
                var glyph = entry.Report ? WorkflowActivity.ReportGlyph : entry.Agent.State == AgentActivityState.Working ? WorkflowActivity.WorkingGlyph
                    : entry.Agent.State == AgentActivityState.Paused ? WorkflowActivity.StoppedGlyph : WorkflowActivity.IdleGlyph;
                var what = since.Length > 0 ? since + " · " : entry.Report ? "" : entry.Agent.Status + " · ";
                rows.Add((glyph, entry.Report ? StateColor(AgentActivityState.Working) : StateColor(entry.Agent.State),
                    $"{entry.Agent.Label} · {what}{entry.Text}", text, false));
            }
        }
        if (rows.Count == 0) rows.Add(("", muted, "Nothing pinned yet: no one in this room needs a look or keeps a todo list.", muted, false));
        var maxRows = Math.Max(3, (int)((bounds.Height - 24 - 110) / Row));
        if (rows.Count > maxRows) rows = [.. rows.Take(maxRows - 1), ("", muted, "…", muted, false)];

        var width = Math.Min(400, bounds.Width - 24);
        var height = 64 + rows.Count * Row + 30;
        var card = Place(bounds, room.Board, width, height);
        dc.DrawRoundedRectangle(Fill(Color.FromArgb(100, 0, 0, 0)), null, new Rect(card.Left + 3, card.Top + 5, card.Width, card.Height), 10, 10);
        dc.DrawRoundedRectangle(Fill(bg), Stroke(border, 1), card, 10, 10);
        var x = card.Left + 16; var inner = card.Width - 32; var y = card.Top + 12;
        var summary = new List<string>();
        if (facts.Working > 0) summary.Add($"{facts.Working} working");
        if (facts.Waiting > 0) summary.Add(facts.Waiting == 1 ? "1 needs you" : $"{facts.Waiting} need you");
        if (facts.Failed > 0) summary.Add($"{facts.Failed} failed");
        var state = Text(summary.Count == 0 ? "all quiet" : string.Join(" · ", summary), 11.5,
            facts.Failed > 0 ? StateColor(AgentActivityState.Error) : facts.Waiting > 0 ? StateColor(AgentActivityState.Waiting)
            : facts.Working > 0 ? StateColor(AgentActivityState.Working) : muted, bold: true);
        dc.DrawText(state, new Point(card.Right - 16 - state.Width, y + 3));
        dc.DrawText(Text(room.Project.Name + " · board", 16, text, bold: true, maxWidth: inner - state.Width - 12), new Point(x, y));
        y += 24;
        var money = facts.Spend >= 0.005 ? $"{(facts.Estimated ? "About " : "")}{Money(facts.Spend)} spent so far" : "No spend recorded yet";
        var context = facts.Fullest is { } fullest && facts.FullestPercent > 0 ? $"  ·  fullest context {fullest.Label} {facts.FullestPercent:0}%" : "";
        dc.DrawText(Text(money + context, 11, facts.FullestPercent >= 80 ? StateColor(AgentActivityState.Waiting) : muted, maxWidth: inner), new Point(x, y));
        y += 22;
        dc.DrawRectangle(Fill(border), null, new Rect(x, y, inner, 1));
        y += 8;
        foreach (var (glyph, glyphInk, line, ink, heading) in rows)
        {
            if (heading) dc.DrawText(Text(line, 10.5, ink, bold: true, maxWidth: inner), new Point(x, y + 2));
            else
            {
                if (glyph.Length > 0) dc.DrawText(Glyph(glyph, 11, glyphInk), new Point(x + 2, y + 3));
                dc.DrawText(Text(line, 12, ink, maxWidth: inner - 22), new Point(x + 22, y + 1));
            }
            y += Row;
        }
        dc.DrawText(Text("Click the board to filter the wall to this room", 10.5, muted, maxWidth: inner), new Point(x, y + 6));
    }
}
