using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;
using VibeCode.UI;

internal static class ShellOutcomeLayout
{
    public static void Run(XDocument document, string output, string theme)
    {
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var source = document.Descendants(wpf + "StackPanel")
            .Single(e => (string?)e.Attribute("Visibility") == "{Binding HasShellOutcomeCounts, Converter={StaticResource BoolVis}}");
        var host = new StackPanel { Width = 320, Background = (Brush)Application.Current.Resources["Bg0"] };
        var row = QueuedMessageLayout.Parse<StackPanel>(source);
        var group = new CompactToolGroupItem(CompactToolGroupItem.BashKind, true);
        row.DataContext = group;
        host.Children.Add(row);
        var labels = row.Children.OfType<TextBlock>().ToArray();
        var checks = 0;

        CheckState("", "", false, false, "running only");
        var first = new ToolItem { Id = "first", Name = "Bash", Status = "running" };
        group.Add(first);
        CheckState("", "", false, false, "pending result");
        first.Status = "done";
        CheckState("1 success", "", false, true, "single success");
        var second = new ToolItem { Id = "second", Name = "Bash", Status = "done" };
        group.Add(second);
        CheckState("2 successes", "", false, true, "multiple successes");
        second.Status = "error";
        CheckState("1 success", "1 failure", true, true, "actual failure");
        first.Status = "error";
        CheckState("", "2 failures", false, true, "failures only");
        first.Status = second.Status = "done";
        CheckState("2 successes", "", false, true, "corrected results hide failures again");

        var examples = new StackPanel { Width = 320, Background = (Brush)Application.Current.Resources["Bg0"] };
        foreach (var (successes, failures) in new[] { (1, 0), (6, 0), (5, 1), (0, 1) })
        {
            var example = new CompactToolGroupItem(CompactToolGroupItem.BashKind, true);
            for (var i = 0; i < successes + failures; i++)
                example.Add(new ToolItem { Id = "example-" + i, Name = "Bash", Status = i < successes ? "done" : "error" });
            var line = QueuedMessageLayout.Parse<StackPanel>(source);
            line.Margin = new Thickness(12, 8, 12, 8);
            line.DataContext = example;
            examples.Children.Add(line);
        }
        QueuedMessageLayout.Layout(examples);
        QueuedMessageLayout.Save(examples, Path.Combine(output, $"shell-outcomes-{theme.ToLowerInvariant()}.png"), 2);
        Console.WriteLine($"PASS: {checks} {theme} shell outcome checks; zero failures and their separator are hidden, singular/plural labels update live");

        void CheckState(string success, string failure, bool separator, bool visible, string label)
        {
            QueuedMessageLayout.Layout(host);
            checks++;
            if (labels[0].Text != success || labels[2].Text != failure
                || (labels[0].Visibility == Visibility.Visible) != (success.Length > 0)
                || (labels[2].Visibility == Visibility.Visible) != (failure.Length > 0)
                || (labels[1].Visibility == Visibility.Visible) != separator
                || (row.Visibility == Visibility.Visible) != visible)
                throw new InvalidOperationException($"{label}: unexpected visible Bash outcome counts");
        }
    }
}
