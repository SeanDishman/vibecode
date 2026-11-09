using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using VibeCode.Services;

namespace VibeCode.UI;

/// <summary>
/// The wall as a kanban board, after the agent boards of Vibe Kanban and Cline: In progress, Needs you, Idle and
/// Done, each agent a card in the column its state puts it in, grouped by project inside the column. The card is the
/// flow graph's own agent card, so the two views say the same things the same way.
///
/// Cards are native buttons built once per agent and updated in place; a card only moves when its agent changes
/// column, so the board does not churn under the pointer while it refreshes every second.
/// </summary>
public sealed partial class WorkflowBoardView : Grid, IWorkflowWallView
{
    private sealed record Lane(string Title, string Ink, string Empty, string Hint);

    private static readonly Lane[] Lanes =
    [
        new("In progress", "Green", "No one is mid-turn.", "Agents running a turn right now"),
        new("Needs you", "Amber", "Nothing is waiting on you.", "Waiting for an approval, a sign-in or a usage reset, or stopped with an error"),
        new("Idle", "Muted", "No one is resting.", "Between turns, or stopped by their orchestrator"),
        new("Done", "Accent", "Nothing reported done yet.", "Bridge agents that reported their task complete"),
    ];

    private readonly StackPanel[] _lists = new StackPanel[Lanes.Length];
    private readonly TextBlock[] _counts = new TextBlock[Lanes.Length];
    private readonly TextBlock[] _empty = new TextBlock[Lanes.Length];
    private readonly Dictionary<string, Button> _cards = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextBlock> _groups = new(StringComparer.Ordinal);
    private bool _ticking;

    public event Action<AgentActivityNode>? AgentSelected;
    public event Action<AgentActivityNode>? AgentMenuRequested;
    public event Action<string>? ProjectSelected;
    public bool MotionEnabled { get; set; } = true;
    /// <summary>The cached public OpenRouter reading for a model id, as the graph uses. Must never fetch.</summary>
    public Func<string, ModelSpeed?>? SpeedLookup { get; set; }
    public int CardCount => _cards.Count;
    public int CountIn(int lane) => _lists[lane].Children.OfType<Button>().Count();

    public WorkflowBoardView()
    {
        Margin = new Thickness(12);
        for (var i = 0; i < Lanes.Length; i++)
        {
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var lane = Lanes[i];
            var frame = new Border { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Padding = new Thickness(10, 10, 6, 4),
                Margin = new Thickness(i == 0 ? 0 : 6, 0, i == Lanes.Length - 1 ? 0 : 6, 0) };
            frame.SetResourceReference(Border.BackgroundProperty, "Bg1");
            frame.SetResourceReference(Border.BorderBrushProperty, "Border");
            var dock = new DockPanel();
            var header = new DockPanel { Margin = new Thickness(2, 0, 6, 10), ToolTip = lane.Hint };
            var bar = new Rectangle { Width = 4, Height = 16, RadiusX = 2, RadiusY = 2, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            bar.SetResourceReference(Shape.FillProperty, lane.Ink);
            header.Children.Add(bar);
            var count = WorkflowAgentCard.Text(12, "Muted");
            count.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(count, Dock.Right);
            header.Children.Add(count);
            var title = WorkflowAgentCard.Text(14, "Text");
            title.Text = lane.Title;
            title.FontWeight = FontWeights.SemiBold;
            header.Children.Add(title);
            DockPanel.SetDock(header, Dock.Top);
            dock.Children.Add(header);
            var empty = WorkflowAgentCard.Text(12, "Faint");
            empty.Text = lane.Empty;
            empty.Margin = new Thickness(4, 6, 0, 0);
            DockPanel.SetDock(empty, Dock.Top);
            dock.Children.Add(empty);
            var list = new StackPanel { Margin = new Thickness(0, 0, 4, 0) };
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                CanContentScroll = false, Content = list, Focusable = false };
            AutomationProperties.SetName(scroll, lane.Title);
            dock.Children.Add(scroll);
            frame.Child = dock;
            SetColumn(frame, i);
            Children.Add(frame);
            _lists[i] = list; _counts[i] = count; _empty[i] = empty;
        }
        IsVisibleChanged += (_, _) => UpdateMotion();
    }

    /// <summary>Which column an agent belongs in.</summary>
    public static int LaneOf(AgentActivityNode agent) => agent.State switch
    {
        AgentActivityState.Working => 0,
        AgentActivityState.Waiting or AgentActivityState.Error => 1,
        _ when agent.Status == "Completed" => 3,
        _ => 2,
    };

    public void SetProjects(IReadOnlyList<AgentActivityProject> projects)
    {
        var nowUtc = DateTime.UtcNow;
        var present = new HashSet<string>(StringComparer.Ordinal);
        var multipleProjects = projects.Count > 1;
        for (var lane = 0; lane < Lanes.Length; lane++)
        {
            var wanted = new List<UIElement>();
            var count = 0;
            foreach (var project in projects)
            {
                var agents = project.Agents.Where(a => LaneOf(a) == lane).ToList();
                if (agents.Count == 0) continue;
                if (multipleProjects) wanted.Add(Group(lane, project));
                foreach (var agent in agents)
                {
                    wanted.Add(Card(agent, nowUtc));
                    present.Add(agent.Id);
                    count++;
                }
            }
            var list = _lists[lane];
            if (!list.Children.Cast<UIElement>().SequenceEqual(wanted))
            {
                // A card moving between columns has to leave the old one first; WPF allows one parent at a time.
                foreach (var element in wanted)
                    if (VisualTreeHelper.GetParent(element) is Panel parent && parent != list) parent.Children.Remove(element);
                list.Children.Clear();
                foreach (var element in wanted) list.Children.Add(element);
            }
            _counts[lane].Text = count.ToString();
            _empty[lane].Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        foreach (var id in _cards.Keys.Where(id => !present.Contains(id)).ToArray())
        {
            if (VisualTreeHelper.GetParent(_cards[id]) is Panel parent) parent.Children.Remove(_cards[id]);
            _cards.Remove(id);
        }
    }

    private TextBlock Group(int lane, AgentActivityProject project)
    {
        var key = lane + "|" + project.Key;
        if (!_groups.TryGetValue(key, out var header))
        {
            header = WorkflowAgentCard.Text(11.5, "Muted");
            header.FontWeight = FontWeights.SemiBold;
            header.Margin = new Thickness(4, 4, 0, 6);
            header.Cursor = Cursors.Hand;
            header.MouseLeftButtonUp += (_, e) => { if (header.Tag is string projectKey) ProjectSelected?.Invoke(projectKey); e.Handled = true; };
            _groups[key] = header;
        }
        header.Text = project.Name;
        header.Tag = project.Key;
        header.ToolTip = (project.Directory.Length == 0 ? "Chats without a project directory" : project.Directory) + "\nClick to filter the wall to this project";
        return header;
    }

    private Button Card(AgentActivityNode agent, DateTime nowUtc)
    {
        if (!_cards.TryGetValue(agent.Id, out var button))
        {
            button = new Button
            {
                Cursor = Cursors.Hand, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(14, 11, 14, 11),
                Height = WorkflowGraph.AgentHeight, Margin = new Thickness(0, 0, 0, 10), Content = new WorkflowAgentCard(),
            };
            button.SetResourceReference(StyleProperty, "WorkflowNode");
            button.SetResourceReference(Control.BorderBrushProperty, "Border");
            button.Click += (_, _) => { if (button.Tag is AgentActivityNode node) AgentSelected?.Invoke(node); };
            button.MouseRightButtonUp += (_, e) =>
            {
                if (button.Tag is not AgentActivityNode node) return;
                AgentMenuRequested?.Invoke(node);
                e.Handled = true;
            };
            _cards[agent.Id] = button;
        }
        if (button.Tag is not AgentActivityNode previous || previous.State != agent.State)
            button.SetResourceReference(WorkflowGraph.RailProperty, WorkflowGraph.StateResource(agent.State));
        button.Tag = agent;
        var speed = agent.OpenRouterId is { } id ? SpeedLookup?.Invoke(id) : null;
        ((WorkflowAgentCard)button.Content).Update(agent, WorkflowFlow.For(speed?.TokensPerSecond), speed, nowUtc, _ticking);
        var now = agent.Now ?? WorkflowAgentCard.Resting(agent, nowUtc);
        button.ToolTip = $"{agent.Label} · {agent.Role}\n{agent.Task}\nNow: {now.Text}" + (agent.Last is { } last ? $"\nLast: {last.Text}" : "")
            + $"\n{agent.Model}\n{agent.ProjectName} · {agent.Directory}\n{agent.Status}\nOpen this chat";
        AutomationProperties.SetName(button, $"Open {agent.Label}: {agent.Task}. {agent.Status}. Now {now.Text}. {agent.ProjectName}.");
        return button;
    }

    public void UpdateMotion() => _ticking = IsVisible && MotionEnabled && HudMotion.Enabled && SystemParameters.ClientAreaAnimation;
}
