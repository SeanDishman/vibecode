using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VibeCode.Services;

namespace VibeCode;

public partial class SettingsWindow
{
    /// <summary>One row of the About page's startup waterfall: bars on the shared time axis, in milliseconds since the
    /// process started.</summary>
    internal sealed record WaterfallRow(string Label, string Value, string BrushKey,
        IReadOnlyList<(double Start, double Duration)> Bars, string? ToolTip = null);

    /// <summary>Settings > About: the last startup as a small waterfall. Each timed step and Bridge reconnect is a bar on
    /// one time axis, the transcript replays fold into one row plus the slowest three, every UI stall over 250 ms is a
    /// red tick, and a green line marks when the workspace was ready.</summary>
    private void RenderStartupWaterfall()
    {
        StartupWaterfall.Children.Clear();
        if (StartupTimeline.Latest() is not { Entries.Count: > 0 } timeline)
        {
            StartupWaterfallStatus.Text = "Not recorded yet";
            return;
        }
        var end = Math.Max(timeline.ReadyMs, timeline.Entries.Max(e => e.StartMs + e.DurationMs));
        StartupWaterfallStatus.Text = timeline.ReadyMs >= 0 ? $"Ready in {timeline.ReadyMs / 1000:0.0} s" : "Never reported ready";
        foreach (var row in BuildStartupWaterfallRows(timeline)) StartupWaterfall.Children.Add(WaterfallRowView(row, end, timeline.ReadyMs));
        var axis = new DockPanel { Margin = new Thickness(170, 3, 64, 0) };
        var last = WaterfallAxisLabel($"{end / 1000:0.0} s");
        DockPanel.SetDock(last, Dock.Right);
        axis.Children.Add(last);
        axis.Children.Add(WaterfallAxisLabel($"0 s · process start, {timeline.StartedAt:HH:mm}"));
        StartupWaterfall.Children.Add(axis);
    }

    /// <summary>The rows, kept to a glance: steps under 10 ms fold into one row, replays into one plus the slowest three.</summary>
    internal static List<WaterfallRow> BuildStartupWaterfallRows(StartupTimeline.Timeline timeline)
    {
        static string Ms(double ms) => ms >= 1000 ? $"{ms / 1000:0.0} s" : $"{ms:0} ms";
        var rows = new List<WaterfallRow>();
        var steps = timeline.Entries.Where(e => e.Kind is "phase" or "bridge").ToList();
        foreach (var step in steps.Where(s => s.DurationMs >= 10))
            rows.Add(new(step.Name, Ms(step.DurationMs), step.Kind == "bridge" ? "Amber" : "Accent",
                [(step.StartMs, step.DurationMs)], step.Detail));
        var quick = steps.Where(s => s.DurationMs < 10).ToList();
        if (quick.Count > 0)
            rows.Add(new($"{quick.Count} quick steps", Ms(quick.Sum(s => s.DurationMs)), "Accent",
                quick.Select(s => (s.StartMs, s.DurationMs)).ToList(), string.Join(", ", quick.Select(s => s.Name))));

        var replays = timeline.Entries.Where(e => e.Kind == "replay").ToList();
        if (replays.Count > 0)
        {
            var first = replays.Min(r => r.StartMs);
            rows.Add(new($"History replays ({replays.Count})", Ms(replays.Max(r => r.StartMs + r.DurationMs) - first), "AccentDim",
                replays.Select(r => (r.StartMs, r.DurationMs)).ToList()));
            foreach (var replay in replays.OrderByDescending(r => r.DurationMs).Take(3))
                rows.Add(new("   " + replay.Name, Ms(replay.DurationMs), "AccentDim", [(replay.StartMs, replay.DurationMs)], replay.Detail));
        }

        var stalls = timeline.Entries.Where(e => e.Kind == "stall").ToList();
        rows.Add(new(stalls.Count == 0 ? "No UI stalls over 250 ms" : $"UI stalls over 250 ms ({stalls.Count})",
            stalls.Count == 0 ? "" : Ms(stalls.Sum(s => s.DurationMs)), "Red", stalls.Select(s => (s.StartMs, s.DurationMs)).ToList(),
            stalls.Count == 0 ? null : string.Join("\n", stalls.OrderByDescending(s => s.DurationMs).Take(8)
                .Select(s => $"{Ms(s.DurationMs)} at {s.StartMs / 1000:0.0} s, {s.Name}"))));
        return rows;
    }

    private FrameworkElement WaterfallRowView(WaterfallRow row, double end, double readyMs)
    {
        var view = new Grid { Margin = new Thickness(0, 1, 0, 1), ToolTip = row.ToolTip, Background = Brushes.Transparent };
        view.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        view.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        view.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        view.Children.Add(new TextBlock
        {
            Text = row.Label, FontSize = 11, Foreground = WaterfallBrush("Muted"), TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
        });
        var track = new Grid { Height = 8, VerticalAlignment = VerticalAlignment.Center, Background = WaterfallBrush("BorderHairline") };
        Grid.SetColumn(track, 1);
        foreach (var (start, duration) in row.Bars) track.Children.Add(WaterfallBar(start, duration, end, WaterfallBrush(row.BrushKey), minWidth: 2));
        if (readyMs >= 0) track.Children.Add(WaterfallBar(readyMs, 0, end, WaterfallBrush("Green"), minWidth: 1));
        view.Children.Add(track);
        var value = new TextBlock
        {
            Text = row.Value, FontSize = 11, Foreground = WaterfallBrush("Faint"), HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap,
        };
        if (TryFindResource("Mono") is FontFamily mono) value.FontFamily = mono;
        Grid.SetColumn(value, 2);
        view.Children.Add(value);
        return view;
    }

    /// <summary>A bar placed by three proportional star columns, so it scales with the card and needs no layout code.</summary>
    private static Grid WaterfallBar(double start, double duration, double end, Brush brush, double minWidth)
    {
        var bar = new Grid();
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0, start), GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0, duration), GridUnitType.Star), MinWidth = minWidth });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0, end - start - duration), GridUnitType.Star) });
        var fill = new Border { Background = brush, CornerRadius = new CornerRadius(1) };
        Grid.SetColumn(fill, 1);
        bar.Children.Add(fill);
        return bar;
    }

    private TextBlock WaterfallAxisLabel(string text) => new() { Text = text, FontSize = 10, Foreground = WaterfallBrush("Faint") };

    private Brush WaterfallBrush(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;
}
