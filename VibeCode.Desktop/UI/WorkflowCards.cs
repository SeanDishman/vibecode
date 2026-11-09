using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using VibeCode.Services;

namespace VibeCode.UI;

/// <summary>
/// The face of one agent node on the workflow wall: who it is, what it was asked for, what it is doing NOW, the
/// LAST thing it finished, and the model it is running on with that model's public speed.
///
/// Built once per agent and updated in place. The wall refreshes every second and a working agent changes what it
/// is doing every few seconds; rebuilding the visual tree on each change would churn controls under the keyboard
/// focus and the mouse, while setting a TextBlock to the text it already shows costs nothing.
///
/// Every brush is a resource reference, so a theme switch restyles the card without a rebuild, and every line is a
/// single trimmed line: the node has a fixed height and a long path must shorten rather than push the footer out.
/// </summary>
internal sealed class WorkflowAgentCard : Grid
{
    private readonly TextBlock _label = Text(11.5, "Muted");
    private readonly Border _pill = new() { CornerRadius = new CornerRadius(999), Padding = new Thickness(7, 1, 8, 2), VerticalAlignment = VerticalAlignment.Center };
    private readonly Ellipse _dot = new() { Width = 6, Height = 6, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = Text(10.5, "Muted");
    private readonly TextBlock _task = Text(14, "Text");
    private readonly ActivityRow _now = new("NOW");
    private readonly ActivityRow _last = new("LAST");
    private readonly Border _modelChip = new() { CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 2), MaxWidth = 128, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _model = Text(10.5, "Muted");
    private readonly Meter _speedMeter = new(30, "Accent");
    private readonly TextBlock _speed = Text(10.5, "Text");
    private readonly StackPanel _planPanel = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _plan = Text(10.5, "Muted");
    private readonly Meter _planMeter = new(32, "Green");
    private readonly StackPanel _childrenPanel = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
    private readonly TextBlock _children = Text(10.5, "Muted");
    private string _state = "";
    private bool? _measured;

    public WorkflowAgentCard()
    {
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new DockPanel();
        var pill = new StackPanel { Orientation = Orientation.Horizontal };
        pill.Children.Add(_dot);
        pill.Children.Add(_status);
        _pill.Child = pill;
        _pill.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(_pill, Dock.Right);
        header.Children.Add(_pill);
        _label.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(_label);
        Add(header, 0);

        _task.FontWeight = FontWeights.SemiBold;
        _task.Margin = new Thickness(0, 5, 0, 5);
        Add(_task, 1);
        Add(_now, 2);
        _last.Margin = new Thickness(0, 2, 0, 0);
        Add(_last, 3);

        var footer = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        _planPanel.Children.Add(_plan);
        _planMeter.Margin = new Thickness(6, 0, 0, 0);
        _planPanel.Children.Add(_planMeter);
        DockPanel.SetDock(_planPanel, Dock.Right);
        footer.Children.Add(_planPanel);
        var people = Glyph(WorkflowActivity.DelegateGlyph, 10.5, "Muted");
        people.Margin = new Thickness(0, 1, 4, 0);
        _childrenPanel.Children.Add(people);
        _childrenPanel.Children.Add(_children);
        _childrenPanel.ToolTip = "Provider subagents working for this agent";
        DockPanel.SetDock(_childrenPanel, Dock.Right);
        footer.Children.Add(_childrenPanel);
        _modelChip.SetResourceReference(Border.BackgroundProperty, "Bg3");
        _modelChip.Child = _model;
        footer.Children.Add(_modelChip);
        var speed = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        speed.Children.Add(_speedMeter);
        _speed.Margin = new Thickness(6, 0, 0, 0);
        _speed.SetResourceReference(TextBlock.FontFamilyProperty, "Mono");
        speed.Children.Add(_speed);
        footer.Children.Add(speed);
        Add(footer, 5);
    }

    /// <summary>Show <paramref name="agent"/> as of <paramref name="nowUtc"/>. Cheap when nothing changed.
    /// <paramref name="ticking"/> is false when motion is off: a still wall then counts working time in whole
    /// minutes instead of redrawing a seconds counter on every card every second.</summary>
    public void Update(AgentActivityNode agent, WorkflowFlow flow, ModelSpeed? speed, DateTime nowUtc, bool ticking)
    {
        _label.Text = agent.Role.Length == 0 ? agent.Label : agent.Label + " · " + agent.Role;
        _task.Text = agent.Task;

        var state = WorkflowGraph.StateResource(agent.State);
        if (_state != state)
        {
            _state = state;
            _dot.SetResourceReference(Shape.FillProperty, state);
            _status.SetResourceReference(TextBlock.ForegroundProperty, state);
            _pill.SetResourceReference(Border.BackgroundProperty, SoftResource(agent.State));
        }
        _status.Text = agent.State == AgentActivityState.Working && agent.WorkingSinceUtc is { } since
            ? $"{agent.Status} · {Duration(nowUtc - since, ticking)}" : agent.Status;

        var now = agent.Now ?? Resting(agent, nowUtc);
        _now.Show(now, agent.State == AgentActivityState.Working ? "Text" : agent.State == AgentActivityState.Error ? "Red"
            : agent.State is AgentActivityState.Waiting or AgentActivityState.Paused ? "Amber" : "Muted");
        if (agent.Last is { } last) _last.Show(last, last.Failed ? "Red" : "Muted");
        else _last.Show(new AgentActivityStep("", "Nothing finished yet"), "Faint");

        _model.Text = agent.Account.Length > 0 ? agent.Model + " · " + agent.Account : agent.Model;
        _model.ToolTip = _model.Text;
        _speedMeter.Fill = flow.Meter;
        _speed.Text = flow.Measured ? $"{flow.TokensPerSecond:0} tok/s" : "— tok/s";
        if (_measured != flow.Measured)
        {
            _measured = flow.Measured;
            _speed.SetResourceReference(TextBlock.ForegroundProperty, flow.Measured ? "Text" : "Faint");
        }
        var tip = speed is { } reading && flow.Measured
            ? $"OpenRouter: {reading.TokensPerSecond:0} tok/s median throughput, {reading.LatencySeconds:0.0}s latency (30-minute public stats). "
              + "Sets this agent's flow speed; it is not a measurement of this chat."
            : agent.OpenRouterId is null ? "This model is not listed on OpenRouter, so the flow uses a neutral pace."
            : "No OpenRouter reading yet, so the flow uses a neutral pace.";
        _speed.ToolTip = _speedMeter.ToolTip = tip;

        _planPanel.Visibility = agent.PlanTotal > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (agent.PlanTotal > 0)
        {
            _plan.Text = $"{agent.PlanDone}/{agent.PlanTotal}";
            _planMeter.Fill = (double)agent.PlanDone / agent.PlanTotal;
            _planPanel.ToolTip = $"{agent.PlanDone} of {agent.PlanTotal} todo items done";
        }
        _childrenPanel.Visibility = agent.ActiveChildren > 0 ? Visibility.Visible : Visibility.Collapsed;
        _children.Text = agent.ActiveChildren.ToString();
    }

    /// <summary>The NOW line for an agent that has nothing in flight: why it is stopped, or how long it has rested.</summary>
    internal static AgentActivityStep Resting(AgentActivityNode agent, DateTime nowUtc) => agent.State switch
    {
        AgentActivityState.Waiting => new(WorkflowActivity.WaitingGlyph, agent.Status),
        AgentActivityState.Error => new(WorkflowActivity.ErrorGlyph, "Stopped with an error"),
        AgentActivityState.Paused => new(WorkflowActivity.StoppedGlyph, agent.Status == "Stopping" ? "Stopping on request" : "Stopped by its orchestrator"),
        AgentActivityState.Working => new(WorkflowActivity.WorkingGlyph, "Working"),
        // Resting time is only ever shown to the minute: an idle card has no reason to change every second.
        _ => new(agent.Status == "Completed" ? WorkflowActivity.DoneGlyph : WorkflowActivity.IdleGlyph,
            agent.LastActiveAt is { } at && nowUtc - at.UtcDateTime is { TotalSeconds: >= 0 } rest
                ? rest.TotalMinutes < 1 ? $"{agent.Status} · just now" : $"{agent.Status} · {Duration(rest, seconds: false)} ago"
                : agent.Status),
    };

    /// <summary>"2m 14s" with <paramref name="seconds"/>, "2m" without; under a minute "42s" or "&lt;1m".</summary>
    internal static string Duration(TimeSpan span, bool seconds) =>
        span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes:00}m"
        : span.TotalMinutes >= 1 ? seconds ? $"{(int)span.TotalMinutes}m {span.Seconds:00}s" : $"{(int)span.TotalMinutes}m"
        : seconds ? $"{Math.Max(0, (int)span.TotalSeconds)}s" : "<1m";

    private static string SoftResource(AgentActivityState state) => state switch
    {
        AgentActivityState.Working => "GreenSoft",
        AgentActivityState.Waiting or AgentActivityState.Paused => "AmberSoft",
        AgentActivityState.Error => "RedSoft",
        _ => "Bg3",
    };

    private void Add(UIElement element, int row)
    {
        SetRow(element, row);
        Children.Add(element);
    }

    internal static TextBlock Text(double size, string resource)
    {
        var block = new TextBlock { FontSize = size, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
        block.SetResourceReference(TextBlock.ForegroundProperty, resource);
        return block;
    }

    internal static TextBlock Glyph(string glyph, double size, string resource)
    {
        var block = Text(size, resource);
        block.Text = glyph;
        block.SetResourceReference(TextBlock.FontFamilyProperty, "Icons");
        block.VerticalAlignment = VerticalAlignment.Center;
        return block;
    }

    /// <summary>"NOW  ✎ Editing WorkflowGraph.cs": a small fixed-width caption, the tool's own glyph, the sentence.</summary>
    private sealed class ActivityRow : Grid
    {
        private readonly TextBlock _glyph = Glyph("", 11, "Muted");
        private readonly TextBlock _text = Text(12, "Text");
        private string _ink = "";

        public ActivityRow(string caption)
        {
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(17) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var label = Text(9.5, "Faint");
            label.Text = caption;
            label.FontWeight = FontWeights.SemiBold;
            label.VerticalAlignment = VerticalAlignment.Center;
            Children.Add(label);
            SetColumn(_glyph, 1);
            Children.Add(_glyph);
            _text.VerticalAlignment = VerticalAlignment.Center;
            SetColumn(_text, 2);
            Children.Add(_text);
        }

        public void Show(AgentActivityStep step, string ink)
        {
            _glyph.Text = step.Glyph;
            _text.Text = step.Text;
            ToolTip = step.Text;
            if (_ink == ink) return;
            _ink = ink;
            _glyph.SetResourceReference(TextBlock.ForegroundProperty, ink == "Text" ? "Green" : ink);
            _text.SetResourceReference(TextBlock.ForegroundProperty, ink);
        }
    }

    /// <summary>A 4px rounded track with a proportional fill: speed on a log scale, or plan progress.</summary>
    internal sealed class Meter : Grid
    {
        private readonly Border _fill = new() { CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left };
        private readonly double _width;

        public Meter(double width, string ink)
        {
            _width = width;
            Width = width;
            Height = 4;
            VerticalAlignment = VerticalAlignment.Center;
            var track = new Border { CornerRadius = new CornerRadius(2) };
            track.SetResourceReference(Border.BackgroundProperty, "Bg3");
            Children.Add(track);
            _fill.SetResourceReference(Border.BackgroundProperty, ink);
            Children.Add(_fill);
        }

        public double Fill
        {
            set
            {
                var width = double.IsFinite(value) ? Math.Clamp(value, 0, 1) * _width : 0;
                _fill.Width = width;
                _fill.Visibility = width > 0.5 ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }
}

/// <summary>
/// The face of a project hub: folder, name and path, the agent counts, and a segmented bar of their states so a
/// project's health reads at a glance - mostly green is moving, any red needs a look.
/// </summary>
internal sealed class WorkflowProjectCard : StackPanel
{
    private static readonly AgentActivityState[] Order =
        [AgentActivityState.Working, AgentActivityState.Waiting, AgentActivityState.Paused, AgentActivityState.Error, AgentActivityState.Idle];

    private readonly TextBlock _title = WorkflowAgentCard.Text(16, "Text");
    private readonly TextBlock _path = WorkflowAgentCard.Text(11.5, "Muted");
    private readonly TextBlock _counts = WorkflowAgentCard.Text(12, "Muted");
    private readonly Grid _bar = new() { Height = 4, Margin = new Thickness(0, 9, 0, 0) };
    private string _shape = "";

    public WorkflowProjectCard()
    {
        VerticalAlignment = VerticalAlignment.Center;
        var heading = new DockPanel();
        var icon = WorkflowAgentCard.Glyph("", 17, "Accent");
        icon.Margin = new Thickness(0, 0, 9, 0);
        heading.Children.Add(icon);
        _title.FontWeight = FontWeights.SemiBold;
        heading.Children.Add(_title);
        Children.Add(heading);
        _path.Margin = new Thickness(0, 7, 0, 8);
        Children.Add(_path);
        Children.Add(_counts);
        Children.Add(_bar);
    }

    public void Update(AgentActivityProject project)
    {
        _title.Text = project.Name;
        _path.Text = project.Directory.Length == 0 ? "Independent chats" : project.Directory;
        var counts = Order.Select(state => project.Agents.Count(a => a.State == state)).ToArray();
        var waiting = counts[1] + counts[2];
        // Short enough for the compact hub: "2 of 4 working · 1 waiting" rather than three separate counts.
        _counts.Text = (waiting + counts[3] == 0
                ? $"{project.Agents.Count} agent{(project.Agents.Count == 1 ? "" : "s")} · {counts[0]} working"
                : $"{counts[0]} of {project.Agents.Count} working")
            + (waiting > 0 ? $" · {waiting} waiting" : "") + (counts[3] > 0 ? $" · {counts[3]} error{(counts[3] == 1 ? "" : "s")}" : "");
        _counts.ToolTip = _counts.Text;
        var shape = string.Join(",", counts);
        if (shape == _shape) return;
        _shape = shape;
        _bar.Children.Clear();
        _bar.ColumnDefinitions.Clear();
        for (var i = 0; i < Order.Length; i++)
        {
            if (counts[i] == 0) continue;
            _bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(counts[i], GridUnitType.Star) });
            var segment = new Border { CornerRadius = new CornerRadius(2), Margin = new Thickness(_bar.Children.Count == 0 ? 0 : 2, 0, 0, 0) };
            segment.SetResourceReference(Border.BackgroundProperty, Order[i] == AgentActivityState.Idle ? "Border" : WorkflowGraph.StateResource(Order[i]));
            SetColumnOf(segment, _bar.ColumnDefinitions.Count - 1);
            _bar.Children.Add(segment);
        }
        _bar.ToolTip = string.Join(" · ", Order.Select((state, i) => (state, count: counts[i])).Where(x => x.count > 0)
            .Select(x => $"{x.count} {x.state.ToString().ToLowerInvariant()}"));
    }

    private static void SetColumnOf(UIElement element, int column) => Grid.SetColumn(element, column);
}
