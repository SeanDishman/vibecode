using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VibeCode;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    private static void VerifyToolMemory(Grid host, bool measureOnly)
    {
        AppSettings.Current.AgentMemoryEnabled = false;
        var resources = Application.Current.Resources;
        resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(
            new Uri("/VibeCode;component/Themes/Dark.xaml", UriKind.Relative)));
        resources["BoolVis"] = new BooleanToVisibilityConverter();
        resources["ShowIf"] = new NonEmptyToVisibilityConverter();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var shell = (MainWindow)typeof(MainWindow).GetConstructors(flags)
            .Single(ctor => ctor.GetParameters().Length == 3).Invoke([new MainViewModel(), null, true]);
        host.Resources.MergedDictionaries.Add(shell.Resources);
        try
        {
            var group = new CompactToolGroupItem(CompactToolGroupItem.EditKind, true);
            for (var i = 0; i < 100; i++)
            {
                var tool = new ToolItem { Id = "memory-" + i, Name = "Edit", Status = "done" };
                for (var line = 0; line < 100; line++)
                    tool.DiffLines.Add(new DiffLine { Kind = "add", Text = $"synthetic diff {i}:{line}" });
                group.Add(tool);
            }
            var before = GC.GetTotalMemory(true);
            var presenter = new ContentPresenter
            {
                Content = group,
                ContentTemplate = (DataTemplate)shell.FindResource(new DataTemplateKey(typeof(CompactToolGroupItem))),
            };
            host.Children.Add(presenter);
            Pump("collapsed tool group loads");
            Measure("initial collapsed");
            group.Expanded = true;
            Pump("tool group expands");
            Measure("expanded group, closed tools");
            foreach (var tool in group.Tools) tool.Expanded = true;
            Pump("tool details expand", 30000);
            Measure("expanded details");
            foreach (var tool in group.Tools) tool.Expanded = false;
            group.Expanded = false;
            Pump("tool group collapses again", 30000);
            Measure("collapsed after viewing");
            group.CompactEnabled = false;
            Pump("full tool cards load", 30000);
            Measure("full mode, closed tools");
            foreach (var tool in group.Tools) tool.Expanded = true;
            Pump("full tool details expand", 30000);
            Measure("full mode, expanded details");
            foreach (var tool in group.Tools) tool.Expanded = false;
            Pump("full tool details collapse", 30000);
            Measure("full mode, collapsed after viewing");
            group.CompactEnabled = true;
            Pump("switch back to compact mode", 30000);
            Measure("compact after mode switch");

            // Updates can arrive while the controls do not exist. Reopening must use the current model,
            // including a previously expanded child, without replaying or discarding transcript data.
            var first = group.Tools[0];
            first.Result = "Result received while collapsed";
            first.DiffLines.Add(new DiffLine { Kind = "del", Text = "Change received while collapsed" });
            first.Expanded = true;
            group.Expanded = true;
            Pump("tool group reopens with updated data", 30000);
            var renderedLines = Visuals(presenter).OfType<TextBlock>().Select(v => v.DataContext).OfType<DiffLine>().ToArray();
            if (!measureOnly) Check(renderedLines.SequenceEqual(first.DiffLines), "reopening restores every diff line, including hidden updates");
            Check(Visuals(presenter).OfType<TextBox>().Any(v => v.Text == first.Result), "reopening restores the latest tool output");
            Check(group.Tools.Count == 100 && group.Tools.Sum(t => t.DiffLines.Count) == 10001,
                "mode switches and collapse preserve the complete underlying transcript");
            group.Expanded = false;
            Pump("hide a group while its child stays expanded", 30000);
            Measure("collapsed with expanded child");

            var agent = new ToolItem { Id = "parent", Name = "Task", IsAgent = true, Status = "done" };
            agent.Children.Add(first);
            presenter.ContentTemplate = (DataTemplate)shell.FindResource(new DataTemplateKey(typeof(ToolItem)));
            presenter.Content = agent;
            Pump("collapsed subagent card loads");
            if (!measureOnly) Check(!Visuals(presenter).OfType<MarkdownView>().Any(), "collapsed subagent does not create transcript or body viewers");
            agent.Expanded = true;
            Pump("subagent transcript expands", 30000);
            if (!measureOnly) Check(Visuals(presenter).OfType<TextBlock>().Count(v => v.DataContext is DiffLine) == first.DiffLines.Count,
                "expanded subagent still renders the complete child transcript");
            presenter.Visibility = Visibility.Collapsed;
            Pump("hide an expanded transcript", 30000);
            if (!measureOnly) Check(!Visuals(presenter).OfType<TextBlock>().Any(v => v.DataContext is DiffLine),
                "hiding the transcript releases details without changing expansion state");
            Check(agent.Expanded && first.Expanded, "hiding a view preserves expansion state in its model");
            first.Result = "Output received while the chat was hidden";
            presenter.Visibility = Visibility.Visible;
            Pump("show the expanded transcript again", 30000);
            Check(Visuals(presenter).OfType<TextBox>().Any(v => v.Text == first.Result),
                "showing a hidden transcript restores expanded details with current output");
            agent.Expanded = false;
            Pump("subagent transcript collapses", 30000);
            if (!measureOnly) Check(!Visuals(presenter).OfType<MarkdownView>().Any() &&
                !Visuals(presenter).OfType<TextBlock>().Any(v => v.DataContext is DiffLine),
                "collapsing a subagent releases the child controls");
            presenter.Content = new ToolItem
            {
                Id = "recycled",
                Name = "Bash",
                Status = "done",
                Expanded = true,
                Result = "Output from the replacement tool"
            };
            Pump("recycle the presenter for another tool", 30000);
            Check(Visuals(presenter).OfType<TextBox>().Any(v => v.Text == "Output from the replacement tool") &&
                !Visuals(presenter).OfType<TextBlock>().Any(v => v.DataContext is DiffLine),
                "recycled tool controls show the new model without retaining the previous transcript");

            void Measure(string phase)
            {
                var views = Visuals(presenter).ToArray();
                var markdown = views.OfType<MarkdownView>().Count();
                var texts = views.OfType<TextBlock>().Count();
                var retained = GC.GetTotalMemory(true) - before;
                Console.WriteLine($"MEMORY {phase}: retained delta={retained / 1048576d:F2} MiB; visuals={views.Length}; text blocks={texts}; markdown={markdown}");
                if (!measureOnly && group.UseCompactDisplay && !group.Expanded)
                {
                    Check(texts < 40 && markdown == 0, "collapsed group releases its tool detail controls");
                    Check(retained < 32 * 1048576L, "collapsed group stays below the retained-memory budget after viewing 10,000 diff lines");
                }
                if (!measureOnly && group.Tools.All(t => !t.Expanded))
                    Check(!views.OfType<TextBlock>().Any(v => v.DataContext is DiffLine) && markdown == 0,
                        "closed tools have no diff or markdown controls in either display mode");
                if (!measureOnly && group.Tools.All(t => t.Expanded))
                    Check(views.OfType<TextBlock>().Count(v => v.DataContext is DiffLine) == 10000,
                        "expanded details render all 10,000 lines without duplicate hidden tool cards");
            }
        }
        finally
        {
            host.Children.Clear();
            host.Resources.MergedDictionaries.Remove(shell.Resources);
            shell.Close();
        }
    }

    private static IEnumerable<DependencyObject> Visuals(DependencyObject parent)
    {
        yield return parent;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var child in Visuals(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }
}
