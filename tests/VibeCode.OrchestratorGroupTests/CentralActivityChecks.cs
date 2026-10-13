using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VibeCode;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    const string PlannerThinking = "Separate implementation from integration checks. Each orchestrator needs a clear scope and handoff.";

    static void StreamCentralFixture(JsonNode request, string provider, string id, string turnId, Action<JsonObject> emit)
    {
        if (CentralInput(request) is null || Environment.GetEnvironmentVariable("REVIEW_CENTRAL_RELEASE") is not { } release) return;
        if (provider == "codex")
        {
            emit(new()
            {
                ["method"] = "item/reasoning/summaryTextDelta",
                ["params"] = new JsonObject
                { ["threadId"] = id, ["turnId"] = turnId, ["itemId"] = "central-thinking", ["delta"] = PlannerThinking }
            });
            emit(new()
            {
                ["method"] = "item/agentMessage/delta",
                ["params"] = new JsonObject
                { ["threadId"] = id, ["turnId"] = turnId, ["itemId"] = "central-assignment", ["delta"] = "{\"assignments\":[" }
            });
        }
        else
        {
            emit(Stream("message_start"));
            emit(Stream("content_block_start", 0, new() { ["type"] = "thinking", ["thinking"] = "" }));
            emit(Stream("content_block_delta", 0, new() { ["type"] = "thinking_delta", ["thinking"] = PlannerThinking }));
            // This provider fixture returns output in result; the test above separately covers partial text.
        }
        var started = DateTime.UtcNow;
        while (!File.Exists(release) && DateTime.UtcNow - started < TimeSpan.FromSeconds(35)) Thread.Sleep(20);
    }

    static JsonObject Stream(string type, int index = 0, JsonObject? block = null) => new()
    {
        ["type"] = "stream_event",
        ["event"] = new JsonObject
        {
            ["type"] = type,
            ["index"] = index,
            [type == "content_block_start" ? "content_block" : "delta"] = block
        }
    };

    static void VerifyCentralActivity()
    {
        VerifyCentralStreamReconciliation();
        foreach (var secondary in new[] { false, true })
        {
            var release = Path.Combine(Root, "central-release-" + secondary);
            Environment.SetEnvironmentVariable("REVIEW_CENTRAL_RELEASE", release);
            CentralPlannerActivity? activity = null;
            try
            {
                var team = FreshGroups(secondary, secondary ? "claude" : "codex", secondary ? "codex" : "claude", [1, 1], false, (vm, host, shell) =>
                {
                    activity = secondary ? vm.SecondaryCentralPlanner : vm.CentralPlanner;
                    Check("temporary planner is visible before any output " + secondary, activity is not null && !activity.IsDisposed);
                    var pane = (CentralPlannerPane)shell.FindName("CentralPlannerPanel");
                    Pump(() => activity!.Thinking.Contains(PlannerThinking));
                    shell.Width = 1440; shell.Height = 900;
                    var surface = (FrameworkElement)shell.Content;
                    surface.Measure(new Size(1440, 900)); surface.Arrange(new Rect(0, 0, 1440, 900)); surface.UpdateLayout();
                    Check("live provider thinking reaches the correct window " + secondary,
                        ReferenceEquals(pane.DataContext, activity) && pane.Visibility == Visibility.Visible);
                    Check("planner has space alongside existing agents " + secondary, pane.ActualWidth > 250 && pane.ActualHeight > 200);
                    Check("planner remains outside permanent agent capacity " + secondary,
                        (secondary ? vm.SecondaryBridgePanes : vm.BridgePanes).Count == 4);
                    if (secondary)
                    {
                        Check("streamed assignment text is visible before handoff", activity!.Output == "{\"assignments\":[");
                        Check("secondary planner does not appear in primary bridge", vm.CentralPlanner is null);
                    }
                    else
                    {
                        RenderCentralPane(activity!);
                        RenderCentralSurface(shell, "central-live-single.png");
                        vm.SetBridgeTerminalMode(false, secondary);
                        surface.UpdateLayout();
                        Check("central pane also appears beside separate terminals", !vm.IsSingleTerminalBridge && pane.Visibility == Visibility.Visible);
                        RenderCentralSurface(shell, "central-live-separate.png");
                        vm.SetBridgeTerminalMode(true, secondary);
                    }
                    Check("temporary stream is excluded from saved work state", !JsonSerializer.Serialize(Property(host, "BridgeWork"))
                        .Contains(PlannerThinking));
                    File.WriteAllText(release, "continue");
                });
                var panes = secondary ? team.Vm.SecondaryBridgePanes : team.Vm.BridgePanes;
                var log = panes.SelectMany(p => p.Items.OfType<ToolItem>()).Single(t => t.Name == "Central orchestrator");
                Check("successful handoff removes pane and stops refresh timer " + secondary,
                    activity!.IsDisposed && (secondary ? team.Vm.SecondaryCentralPlanner : team.Vm.CentralPlanner) is null);
                Check("handoff preserves copyable provider transcript " + secondary,
                    log.Status == "done" && log.Result!.Contains(PlannerThinking) && log.Result.Contains("Assignments delivered"));
                Check("final assignment replaces partial streamed text " + secondary,
                    activity.Output.StartsWith("{\"assignments\":[") && activity.Output.IndexOf("{\"assignments\":[", 1, StringComparison.Ordinal) == -1);
            }
            finally { Environment.SetEnvironmentVariable("REVIEW_CENTRAL_RELEASE", null); File.WriteAllText(release, "continue"); }
        }
    }

    static void VerifyCentralStreamReconciliation()
    {
        using var activity = new CentralPlannerActivity(new("codex", "gpt-6.1-sol", "high", FastMode: true));
        activity.Observe(Stream("message_start"));
        activity.Observe(Stream("content_block_start", 0, new() { ["type"] = "thinking", ["thinking"] = "" }));
        activity.Observe(Stream("content_block_delta", 0, new() { ["type"] = "thinking_delta", ["thinking"] = "Plan" }));
        activity.Observe(Stream("content_block_start", 1, new() { ["type"] = "text", ["text"] = "" }));
        activity.Observe(Stream("content_block_delta", 1, new() { ["type"] = "text_delta", ["text"] = "{\"assignments\":" }));
        activity.Observe(JsonNode.Parse("""{"type":"assistant","message":{"content":[{"type":"thinking","thinking":"Plan carefully"},{"type":"text","text":"{\"assignments\":[]}"}]}}""")!);
        activity.Flush();
        Check("final snapshots reconcile both block types without duplication", activity.Thinking == "Plan carefully" && activity.Output == "{\"assignments\":[]}");
        Check("central activity identifies its own fast mode", activity.Configuration.Contains("Fast mode"));
        activity.SetStage("Waiting for usage limit reset…"); activity.Flush();
        Check("usage wait remains visible with earlier progress", activity.Status.Contains("usage limit") && activity.Events.Contains("Writing assignments"));
        activity.Observe(Stream("content_block_delta", 1, new() { ["type"] = "text_delta", ["text"] = new string('x', 600_000) }));
        activity.Flush();
        Check("stream display has bounded memory", activity.Output.Length <= 262_144 && activity.Events.Contains("Display limit"));
        activity.Dispose();
        var after = activity.Transcript(); activity.Observe(Stream("content_block_delta", 1, new() { ["text"] = "late" }));
        Check("late provider callbacks cannot revive disposed activity", activity.IsDisposed && activity.Transcript() == after);
        using var retry = new CentralPlannerActivity(new("codex", "gpt-6.1-sol", "high"));
        foreach (var text in new[] { "Interrupted draft", "Retry after reset" })
        {
            retry.Observe(Stream("message_start"));
            retry.Observe(Stream("content_block_start", 0, new() { ["type"] = "text", ["text"] = "" }));
            retry.Observe(Stream("content_block_delta", 0, new() { ["type"] = "text_delta", ["text"] = text }));
        }
        retry.Observe(JsonNode.Parse("""{"type":"assistant","message":{"content":[{"type":"text","text":"Retry after reset completed"}]}}""")!);
        retry.Flush();
        Check("a retry preserves prior progress and finalizes only its own stream", retry.Output == "Interrupted draft\n\nRetry after reset completed");
    }

    static void RenderCentralPane(CentralPlannerActivity activity)
    {
        var resources = Application.Current.Resources.MergedDictionaries;
        var original = resources.ToArray();
        try
        {
            foreach (var theme in new[] { "Dark", "Borderless", "Cli" })
            {
                resources.Clear();
                resources.Add((ResourceDictionary)Application.LoadComponent(new Uri($"/VibeCode;component/Themes/{(theme == "Cli" ? "Cli" : "Dark")}.xaml", UriKind.Relative)));
                if (theme == "Borderless") resources.Add((ResourceDictionary)Application.LoadComponent(new Uri("/VibeCode;component/Themes/Borderless.xaml", UriKind.Relative)));
                foreach (var width in new[] { 320, 540 })
                {
                    var pane = new CentralPlannerPane { DataContext = activity };
                    var border = new Border { Child = pane, Background = (Brush)Application.Current.FindResource("Bg0") };
                    border.Measure(new Size(width, 640)); border.Arrange(new Rect(0, 0, width, 640)); border.UpdateLayout();
                    SaveCentralImage(border, width, 640, $"central-pane-{theme}-{width}.png");
                    Check($"{theme} pane wraps and exposes selectable thinking at {width}px", Descendants<TextBox>(pane)
                        .Any(t => t.Text == PlannerThinking && t.ActualWidth <= width - 24 && t.ActualHeight > 0 && t.IsReadOnly));
                }
            }
        }
        finally { resources.Clear(); foreach (var resource in original) resources.Add(resource); }
    }

    static void RenderCentralSurface(MainWindow shell, string name)
    {
        shell.UpdateLayout();
        var surface = (FrameworkElement)shell.Content;
        SaveCentralImage(surface, (int)Math.Ceiling(surface.ActualWidth), (int)Math.Ceiling(surface.ActualHeight), name);
    }

    static void SaveCentralImage(FrameworkElement surface, int width, int height, string name)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(surface);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(Root, name)); encoder.Save(stream);
        Console.WriteLine("Rendered " + name);
    }
}
