using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;

namespace VibeCode.UI;

/// <summary>
/// Swimlanes, after the timeline views of agent observability dashboards (Kansh, the Claude Code hooks multi-agent
/// observability board): every agent gets a lane on one shared clock, grouped by project, coloured by what it was
/// doing - working, waiting, stopped, failed, idle - with a dot for every new step it took.
///
/// The lanes come from <see cref="WorkflowHistory"/>, which only knows what happened while the wall was open (plus
/// each working agent's current turn); the point where the wall opened is marked so an empty stretch before it is
/// never mistaken for idleness. Hover a lane for the step or state under the pointer; click it to open the chat.
/// </summary>
public sealed class WorkflowLanesView : WorkflowScene
{
    private const double Header = 44, ProjectRow = 34, LaneRow = 42, LabelWidth = 250;
    private static readonly int[] Ranges = [5, 15, 60];
    private int _minutes = 15;

    public WorkflowHistory? History { get; set; }
    public int Minutes => _minutes;

    public WorkflowLanesView() => AutomationProperties.SetName(this, "Swimlanes: one lane per agent on a shared clock");

    protected override double FramesPerSecond => 4;
    protected override bool WantsPointerRedraw => true;

    protected override Size MeasureOverride(Size available)
    {
        var height = Header + Projects.Count * ProjectRow + Projects.Sum(p => p.Agents.Count) * LaneRow + 24;
        return new Size(double.IsInfinity(available.Width) ? 800 : available.Width, height);
    }

    protected override void OnCommand(string key)
    {
        if (key.StartsWith("range:", StringComparison.Ordinal) && int.TryParse(key[6..], out var minutes)) { _minutes = minutes; InvalidateVisual(); }
    }

    protected override void Render(DrawingContext dc, Rect bounds)
    {
        var text = Ink("Text", Color.FromRgb(0xE7, 0xED, 0xF7));
        var muted = Ink("Muted", Color.FromRgb(0xAA, 0xBA, 0xD0));
        var faint = Ink("Faint", Color.FromRgb(0x6E, 0x7A, 0x8C));
        var border = Ink("Border", Color.FromRgb(0x2E, 0x36, 0x44));
        var track = Ink("Bg2", Color.FromRgb(0x1C, 0x22, 0x2C));
        var accent = Ink("Accent", Color.FromRgb(0x7C, 0xB7, 0xFF));
        var nowUtc = DateTime.UtcNow;
        var span = TimeSpan.FromMinutes(_minutes);
        var x0 = Math.Min(LabelWidth, bounds.Width * 0.32) + 16;
        var x1 = bounds.Width - 28;
        if (x1 - x0 < 80) return;
        double X(DateTime t) => x1 - (nowUtc - t).TotalSeconds / span.TotalSeconds * (x1 - x0);

        // Header: range chips, then the clock along the top.
        var chipX = x1 - 34 - Ranges.Length * 52;
        var chipStart = chipX;
        foreach (var minutes in Ranges)
        {
            var chip = new Rect(chipX, 10, 46, 24);
            var on = minutes == _minutes;
            dc.DrawRoundedRectangle(Fill(on ? Alpha(accent, 0.18) : Colors.Transparent), Stroke(on ? accent : border, 1), chip, 6, 6);
            var label = Text(minutes == 60 ? "1 h" : minutes + " m", 11.5, on ? accent : muted, bold: on);
            dc.DrawText(label, new Point(chip.Left + (chip.Width - label.Width) / 2, chip.Top + (chip.Height - label.Height) / 2));
            Hit(chip, "range:" + minutes);
            chipX += 52;
        }
        dc.DrawText(Text("Agent", 11, muted, bold: true), new Point(16, 16));
        var step = _minutes <= 5 ? 1 : _minutes <= 15 ? 3 : 10;
        var localNow = DateTime.Now;
        var first = new DateTime(localNow.Year, localNow.Month, localNow.Day, localNow.Hour, localNow.Minute - localNow.Minute % step, 0, DateTimeKind.Local);
        var gridPen = Stroke(Alpha(border, 0.8), 1);
        var bottom = Math.Max(bounds.Height - 8, Header + 10);
        for (var tick = first; tick > localNow - span; tick = tick.AddMinutes(-step))
        {
            var x = X(tick.ToUniversalTime());
            if (x < x0 + 4) break;
            dc.DrawLine(gridPen, new Point(Math.Round(x) + 0.5, Header - 4), new Point(Math.Round(x) + 0.5, bottom));
            var stamp = Text(tick.ToString("HH:mm"), 10.5, faint);
            if (x - stamp.Width / 2 > x0 && x + stamp.Width / 2 < chipStart - 8) dc.DrawText(stamp, new Point(x - stamp.Width / 2, 20));
        }
        dc.DrawLine(Stroke(accent, 1.5), new Point(x1 + 0.5, Header - 8), new Point(x1 + 0.5, bottom));
        var nowLabel = Text("now", 10.5, accent, bold: true);
        dc.DrawText(nowLabel, new Point(x1 - nowLabel.Width / 2, Header - 8 - nowLabel.Height));
        if (History is { } history && X(history.OpenedUtc) > x0)
        {
            // Nothing before this line was observed; it is not idle time.
            var opened = X(history.OpenedUtc);
            var dashed = new Pen(Fill(faint), 1) { DashStyle = new DashStyle([3, 3], 0) };
            dashed.Freeze();
            dc.DrawLine(dashed, new Point(opened, Header - 4), new Point(opened, bottom));
            var caption = Text("wall opened", 10, faint);
            if (opened - caption.Width - 6 > x0) dc.DrawText(caption, new Point(opened - caption.Width - 6, Header - 2));
        }

        var y = Header;
        var hoverAgent = HoveredAgent;
        string? tip = null;
        foreach (var project in Projects)
        {
            var row = new Rect(0, y, bounds.Width, ProjectRow);
            Hit(new Rect(0, y, x0, ProjectRow), project);
            if (Hovered(project)) dc.DrawRectangle(Fill(Alpha(accent, 0.08)), null, row);
            dc.DrawText(Glyph("", 13, muted), new Point(16, y + 10));
            var name = Text(project.Name, 13, text, bold: true, maxWidth: x0 - 60);
            dc.DrawText(name, new Point(36, y + 8));
            var meta = Text(project.Working > 0 ? $"{project.Working} of {project.Agents.Count} working"
                : project.Agents.Count == 1 ? "1 agent" : $"{project.Agents.Count} agents", 11, project.Working > 0 ? StateColor(AgentActivityState.Working) : faint);
            dc.DrawText(meta, new Point(Math.Min(x0 - meta.Width - 6, 36 + name.Width + 10), y + 10));
            dc.DrawRectangle(Fill(border), null, new Rect(0, y + ProjectRow - 1, bounds.Width, 1));
            y += ProjectRow;
            foreach (var agent in project.Agents)
            {
                var lane = new Rect(0, y, bounds.Width, LaneRow);
                Hit(lane, agent);
                if (ReferenceEquals(hoverAgent, agent) || hoverAgent?.Id == agent.Id) dc.DrawRectangle(Fill(Alpha(accent, 0.1)), null, lane);
                var state = StateColor(agent.State);
                dc.DrawEllipse(Fill(state), null, new Point(22, y + 14), 4, 4);
                dc.DrawText(Text(agent.Role.Length > 0 ? $"{agent.Label} · {agent.Role}" : agent.Label, 12, text, bold: true, maxWidth: x0 - 44), new Point(34, y + 5));
                var since = Since(agent, nowUtc);
                dc.DrawText(Text(since.Length > 0 ? $"{agent.Status} · {since}" : agent.Status, 11, state, maxWidth: x0 - 44), new Point(34, y + 22));
                var trackRect = new Rect(x0, y + 12, x1 - x0, 18);
                dc.DrawRoundedRectangle(Fill(track), null, trackRect, 4, 4);
                if (History?.For(agent.Id) is { } record)
                {
                    foreach (var segment in record.Segments)
                    {
                        var left = Math.Max(x0, X(segment.StartUtc));
                        var right = Math.Min(x1, X(segment.EndUtc));
                        if (right <= x0 || left >= x1) continue;
                        right = Math.Max(right, left + 2);
                        var ink = StateColor(segment.State);
                        var bar = segment.State switch
                        {
                            AgentActivityState.Idle => new Rect(left, y + 19, right - left, 4),
                            AgentActivityState.Paused => new Rect(left, y + 15, right - left, 12),
                            _ => new Rect(left, y + 13, right - left, 16),
                        };
                        dc.DrawRoundedRectangle(Fill(segment.State == AgentActivityState.Idle ? Alpha(ink, 0.55) : Alpha(ink, 0.85)), null, bar, 3, 3);
                        if (Pointer is { } pointer && lane.Contains(pointer) && pointer.X >= left && pointer.X <= right)
                            tip = $"{Label(segment.State)} · {Clock(segment.StartUtc)}–{Clock(segment.EndUtc)} ({WorkflowAgentCard.Duration(segment.EndUtc - segment.StartUtc, seconds: true)})";
                    }
                    foreach (var mark in record.Marks)
                    {
                        var x = X(mark.AtUtc);
                        if (x < x0 || x > x1) continue;
                        var ink = mark.Failed ? StateColor(AgentActivityState.Error) : text;
                        dc.DrawEllipse(Fill(ink), Stroke(track, 1.5), new Point(x, y + 21), 3.5, 3.5);
                        if (Pointer is { } pointer && lane.Contains(pointer) && Math.Abs(pointer.X - x) <= 5)
                            tip = $"{Clock(mark.AtUtc)} · {mark.Text}";
                    }
                }
                if (agent.State == AgentActivityState.Working)
                {
                    var pulse = IsAnimating ? 0.5 + 0.5 * Math.Sin(Time * 4) : 0.6;
                    dc.DrawEllipse(Fill(Alpha(state, 0.25 * pulse + 0.1)), null, new Point(x1, y + 21), 9, 9);
                    dc.DrawEllipse(Fill(state), null, new Point(x1, y + 21), 4, 4);
                }
                var now = agent.Now ?? WorkflowAgentCard.Resting(agent, nowUtc);
                if (Pointer is { } p && lane.Contains(p) && p.X < x0) tip = now.Text;
                y += LaneRow;
                dc.DrawRectangle(Fill(Alpha(border, 0.45)), null, new Rect(16, y - 1, bounds.Width - 32, 1));
            }
        }
        if (tip is not null && Pointer is { } at) Tooltip(dc, bounds, at, tip, text, border);
    }

    private void Tooltip(DrawingContext dc, Rect bounds, Point at, string tip, Color text, Color border)
    {
        var label = Text(tip, 12, text, maxWidth: 420);
        var box = new Rect(at.X + 14, at.Y + 16, label.Width + 16, label.Height + 10);
        if (box.Right > bounds.Width - 8) box.X = at.X - box.Width - 10;
        if (box.Bottom > bounds.Height - 4) box.Y = at.Y - box.Height - 8;
        dc.DrawRoundedRectangle(Fill(Ink("Bg3", Color.FromRgb(0x22, 0x2A, 0x36))), Stroke(border, 1), box, 6, 6);
        dc.DrawText(label, new Point(box.Left + 8, box.Top + 5));
    }

    private static string Label(AgentActivityState state) => state switch
    {
        AgentActivityState.Working => "Working", AgentActivityState.Waiting => "Waiting", AgentActivityState.Paused => "Stopped",
        AgentActivityState.Error => "Error", _ => "Idle",
    };

    private static string Clock(DateTime utc) => utc.ToLocalTime().ToString("HH:mm:ss");
}
