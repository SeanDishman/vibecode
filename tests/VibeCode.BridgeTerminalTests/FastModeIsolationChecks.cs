using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using VibeCode;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    private static void VerifyFastModeVisibility()
    {
        AppSettings.Current.FastMode = AppSettings.Current.CodexUltrafastMode = false;
        var resources = Application.Current.Resources.MergedDictionaries;
        var original = resources.ToArray();
        try
        {
            foreach (var provider in new[] { "claude", "codex" })
            {
                var (vm, chats) = Team("fast-visibility-" + provider, 2, provider);
                foreach (var chat in chats) Load(chat);
                var selected = chats[0];
                selected.Model = "fixture-no-fast";
                selected.FastMode = true;
                Check(provider + ": saved speed and a fast-capable sibling model do not show lightning",
                    !selected.CanFastMode && !selected.ShowFastMode && selected.Model == "fixture-no-fast");
                selected.Model = "fixture-missing";
                Check(provider + ": an unlisted model cannot inherit the default's speed capability",
                    !selected.CanFastMode && !selected.ShowFastMode && !selected.CanUltrafastMode);
                selected.Model = null;
                Check(provider + ": implicit default uses its own capability record", selected.CanFastMode && selected.ShowFastMode);
                selected.Model = "fixture-fast[1m]";
                Check(provider + ": context variant retains the selected model's capability", selected.CanFastMode && selected.ShowFastMode);

                foreach (var theme in new[] { "Dark", "Borderless", "Cli" })
                {
                    resources.Clear();
                    resources.Add((ResourceDictionary)Application.LoadComponent(new Uri(
                        $"/VibeCode;component/Themes/{(theme == "Cli" ? "Cli" : "Dark")}.xaml", UriKind.Relative)));
                    if (theme == "Borderless") resources.Add((ResourceDictionary)Application.LoadComponent(
                        new Uri("/VibeCode;component/Themes/Borderless.xaml", UriKind.Relative)));
                    selected.Model = "fixture-fast";
                    Property(vm, "ActiveChat", selected);
                    var shell = (MainWindow)typeof(MainWindow).GetConstructors(Flags)
                        .Single(ctor => ctor.GetParameters().Length == 3).Invoke([vm, null, true]);
                    shell.Left = shell.Top = -30000;
                    shell.ShowActivated = shell.ShowInTaskbar = false;
                    try
                    {
                        shell.Show(); shell.UpdateLayout();
                        var normal = (ToggleButton)shell.FindName("FastModeToggle");
                        var pane = (FrameworkElement)((DataTemplate)shell.FindResource("BridgeComposerTemplate")).LoadContent();
                        pane.DataContext = chats[1];
                        pane.Measure(new Size(400, double.PositiveInfinity)); pane.Arrange(new Rect(pane.DesiredSize)); pane.UpdateLayout();
                        var bridge = Descendants<ToggleButton>(pane).Single(button => button.Name == "PaneFastModeToggle");
                        VerifyControl(normal, selected, "normal");
                        VerifyControl(bridge, chats[1], "Bridge");

                        void VerifyControl(ToggleButton button, ChatViewModel owner, string layout)
                        {
                            var label = provider + " " + theme + " " + layout;
                            // Keep an empty captured row visible in evidence when all optional controls collapse.
                            ((FrameworkElement)button.Parent).MinHeight = 44;
                            owner.Model = "fixture-fast";
                            PumpBindings();
                            Check(label + ": supported model shows lightning", button.Visibility == Visibility.Visible && button.IsEnabled);
                            if (provider == "codex") CaptureUltrafastElement((FrameworkElement)button.Parent, 640,
                                "fast-supported-" + layout + "-" + theme + ".png");
                            owner.Model = "fixture-no-fast";
                            owner.FastMode = true;
                            PumpBindings();
                            Check(label + ": switching to an unsupported model removes lightning", button.Visibility == Visibility.Collapsed
                                && owner.Model == "fixture-no-fast");
                            if (provider == "codex") CaptureUltrafastElement((FrameworkElement)button.Parent, 640,
                                "fast-unsupported-" + layout + "-" + theme + ".png");
                            owner.Model = "fixture-fast";
                            PumpBindings();
                            Check(label + ": switching back restores lightning", button.Visibility == Visibility.Visible);
                            Load(owner, supported: false);
                            PumpBindings();
                            Check(label + ": refreshed catalog can withdraw support without a model change", button.Visibility == Visibility.Collapsed);
                            Load(owner);
                            PumpBindings();
                        }
                    }
                    finally { shell.Close(); }
                }
                selected.Models.Clear(); selected.RefreshModelPicker();
                Check(provider + ": missing catalog never grants Fast", !selected.CanFastMode && !selected.ShowFastMode);
            }
            foreach (var provider in new[] { "glm" })
            {
                var (_, chats) = Team("fast-unsupported-" + provider, 1, provider);
                Load(chats[0]);
                Check(provider + ": provider without a Fast adapter hides lightning", !chats[0].CanFastMode && !chats[0].ShowFastMode);
            }
        }
        finally { resources.Clear(); foreach (var resource in original) resources.Add(resource); }

        static void Load(ChatViewModel chat, bool supported = true)
        {
            chat.Models.Clear();
            chat.Models.Add(new ModelChoice { Provider = chat.Provider, Value = "fixture-no-fast", Display = "Standard only" });
            chat.Models.Add(new ModelChoice { Provider = chat.Provider, Value = "fixture-fast", Display = "Fast capable", SupportsFastMode = supported });
            chat.Models.Add(new ModelChoice { Provider = chat.Provider, Value = "default", Display = "Default", ResolvedModel = "fixture-fast", SupportsFastMode = supported });
            chat.RefreshModelPicker();
        }
        static void PumpBindings() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }

    /// <summary>Fast mode belongs to each chat. Turning it on in one Bridge pane must not reach a sibling, and a chat
    /// saved and rebuilt must come back with its own setting rather than the new-chat seed (whatever any chat toggled
    /// last), which is how one pane's toggle used to turn every Claude chat fast after the next launch.</summary>
    private static void VerifyFastModeIsolation()
    {
        AppSettings.Current.FastMode = false;
        var (vm, team) = Team("fast-mode-isolation", 3, "claude");
        team[1].SetFastMode(true);
        Check("turning fast mode on in Claude 2 leaves Claude 1 and Claude 3 alone",
            team[1].FastMode && !team[0].FastMode && !team[2].FastMode);
        Check("the toggle only seeds chats created later", AppSettings.Current.FastMode);

        // SnapshotSession refuses to write the chat list until startup restore has run (its data-loss guard).
        typeof(MainViewModel).GetField("_sessionRestored", Flags)!.SetValue(vm, true);
        Call(vm, "SnapshotSession");
        var saved = AppSettings.Current.OpenChats.ToDictionary(o => o.SessionId!);
        Check("each chat's own fast mode is saved with it",
            saved[team[0].SessionId!].FastMode == false && saved[team[1].SessionId!].FastMode == true
            && saved[team[2].SessionId!].FastMode == false);
        var reloaded = JsonSerializer.Deserialize<OpenChatState>(JsonSerializer.Serialize(saved[team[0].SessionId!]))!;
        Check("a saved fast-mode-off chat survives a settings reload as off, not as the seed",
            reloaded.FastMode == false && AppSettings.Current.FastMode);
        var legacy = JsonSerializer.Deserialize<OpenChatState>("""{"Cwd":"C:\\x","SessionId":"old","Provider":"claude"}""")!;
        Check("snapshots from before per-chat saving have no value and keep using the seed", legacy.FastMode is null);
        AppSettings.Current.FastMode = false;
    }
}
