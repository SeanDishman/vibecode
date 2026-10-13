using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VibeCode;
using VibeCode.Protocol;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    private static void SwarmControlsChecks()
    {
        var settings = AppSettings.Current;
        var originalLimit = settings.SwarmMaxWorkers;
        try
        {
            // Old saved opt-outs must not keep tools disabled once those settings have been removed.
            var legacy = JsonSerializer.Deserialize<AppSettings>(
                """{"AgentSwarmsEnabled":false,"AgentSwarmsInBridge":false,"SwarmMaxWorkers":4,"NotifyOnTurnEnd":false}""")!;
            var saved = JsonSerializer.SerializeToNode(legacy)!;
            Check("retired swarm switches are dropped without losing the limit or unrelated preferences",
                legacy.SwarmMaxWorkers == 4 && !legacy.NotifyOnTurnEnd
                && saved["AgentSwarmsEnabled"] is null && saved["AgentSwarmsInBridge"] is null);
            var catalog = JarvisSettingsCatalog.Snapshot(legacy).ToJsonString();
            Check("settings tools retain only the worker limit", catalog.Contains("SwarmMaxWorkers")
                && !catalog.Contains("AgentSwarmsEnabled") && !catalog.Contains("AgentSwarmsInBridge"));

            foreach (var provider in new[] { "claude", "codex" })
                foreach (var bridge in new[] { false, true })
                {
                    settings.SwarmMaxWorkers = 4;
                    var (chat, session) = NewChat("natural-swarm-" + provider + "-" + bridge, provider);
                    if (bridge) chat.BridgeLabel = provider + " 1";
                    chat.SetExtendedQueueEnabled(false);
                    const string request = "Use two subagents to check the parser and the UI in parallel.";
                    Check("normal-language request accepted: " + provider + "/" + bridge, chat.Send(request));
                    Pump(() => session.Sent.Count == 1);
                    var wire = session.Sent[0];
                    Check("dispatch carries request and current bounded policy: " + provider + "/" + bridge,
                        wire.Contains(request) && wire.Contains("at most 4 concurrent or total child workers")
                        && wire.Contains("No special request marker, button, or enable setting is required")
                        && !wire.Contains("VIBECODE SWARM REQUEST") && chat.ShowSubagentsControl);
                    Check("resumed transcript hides child policy: " + provider + "/" + bridge,
                        (string)typeof(ChatViewModel).GetMethod("StripInjectedPrelude", Flags)!.Invoke(null, [wire])! == request);

                    settings.SwarmMaxWorkers = 2;
                    chat.RefreshSwarmSettings();
                    chat.Send("Continue using the workers for verification.");
                    Check("follow-up queues without a separate swarm mode: " + provider + "/" + bridge,
                        chat.HasQueued && chat.Items.OfType<QueuedItem>().Single().QueueStatusText == "Queued · sends when the agent finishes");
                    End(chat, new JsonObject { ["is_error"] = false });
                    Pump(() => session.Sent.Count == 2);
                    Check("queued follow-up receives lowered limit: " + provider + "/" + bridge,
                        session.Sent[1].Contains("at most 2 concurrent or total child workers")
                        && session.Sent[1].Contains("Continue using the workers"));
                    End(chat, new JsonObject { ["is_error"] = false });
                    chat.Close();
                }

            var (capped, _) = NewChat("native-worker-cap");
            Field(capped, "_sessionSwarmWorkerCap", 4);
            settings.SwarmMaxWorkers = 12;
            Check("existing session cannot advertise more workers than its launch cap", capped.SwarmMaxWorkers == 4);
            settings.SwarmMaxWorkers = 2;
            Check("lowered limit applies to an existing session", capped.SwarmMaxWorkers == 2);
            Check("bridge rules accept ordinary delegation without a settings opt-in",
                SwarmPolicy.BridgeRuntimeRule(4).Contains("normal language")
                && SwarmPolicy.BridgeRuntimeRule(4).Contains("within 4 child workers")
                && !SwarmPolicy.BridgeRuntimeRule(4).Contains("VIBECODE SWARM REQUEST"));
            Check("advanced orchestrators can honor an explicit native-child request",
                BridgeOrchestratorPolicy.Instructions.Contains("If the user explicitly asks you to create native subagents")
                && !BridgeOrchestratorPolicy.Instructions.Contains("Do not create native subagents"));
            Check("unsupported providers are not promised native child tools", SwarmPolicy.SessionRuntimeRule("kimi", 6) == "");
            Check("worker slider range remains bounded", SwarmPolicy.ClampMaxWorkers(0) == 2
                && SwarmPolicy.ClampMaxWorkers(int.MaxValue) == 16);

            var claude = (ProcessStartInfo)typeof(ClaudeSession).GetMethod("CreateStartInfo", Flags)!.Invoke(null,
                [new ClaudeSessionOptions { Cwd = Root, SwarmsEnabled = true, SwarmMaxWorkers = 4, McpServers = [] }])!;
            Check("Claude retains native child visibility and configured concurrency",
                claude.ArgumentList.Contains("--forward-subagent-text") && !claude.ArgumentList.Contains("--disallowedTools")
                && claude.Environment["CLAUDE_CODE_MAX_TOOL_USE_CONCURRENCY"] == "4");
            var codex = CodexEnvironment.CreateAppServerStartInfo(Root, Path.Combine(Root, "swarm-codex-home"),
                swarmsEnabled: true, swarmMaxWorkers: 4, mcpServers: []);
            Check("Codex retains native worker and nesting limits", codex.ArgumentList.Contains("features.multi_agent=true")
                && codex.ArgumentList.Contains("agents.max_threads=4") && codex.ArgumentList.Contains("agents.max_depth=1"));
        }
        finally { settings.SwarmMaxWorkers = originalLimit; }
    }

    private static void SwarmSettingsControls(SettingsWindow window)
    {
        Check("retired swarm controls are absent", window.FindName("SwarmsEnabledToggle") is null
            && window.FindName("BridgeSwarmsToggle") is null);
        var slider = (Slider)window.FindName("SwarmMaxSlider");
        var original = slider.Value;
        try
        {
            Check("worker limit stays enabled and adjustable", slider.IsEnabled && slider.Minimum == 2 && slider.Maximum == 16);
            slider.Value = 4;
            var reloaded = (AppSettings)typeof(AppSettings).GetMethod("Load", Flags)!.Invoke(null, null)!;
            Check("worker limit survives settings save/reload", reloaded.SwarmMaxWorkers == 4);
            ((FrameworkElement)window.FindName("PaneGeneral")).Visibility = Visibility.Collapsed;
            var panel = (FrameworkElement)window.FindName("PaneBridge");
            panel.Visibility = Visibility.Visible;
            window.UpdateLayout();
            var group = ParentOf<SettingGroup>(slider)!;
            group.BringIntoView();
            var rendering = Stopwatch.StartNew();
            Pump(() => rendering.Elapsed > TimeSpan.FromMilliseconds(300));
            window.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(group.ActualWidth),
                (int)Math.Ceiling(group.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            // Render in local coordinates instead of keeping the group's offset inside the scrolled page.
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
                drawing.DrawRectangle(new VisualBrush(group), null, new Rect(0, 0, group.ActualWidth, group.ActualHeight));
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(Root, "swarm-limit-settings.png")); encoder.Save(output);
        }
        finally { slider.Value = original; }
    }

    private static T? ParentOf<T>(DependencyObject value) where T : DependencyObject
    {
        for (var parent = VisualTreeHelper.GetParent(value); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is T result) return result;
        return null;
    }
}
