using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    private static void VerifyLayoutRestoration()
    {
        var (vm, regular) = Team("regular-layout-navigation", 2);
        foreach (var chat in regular) chat.Status = "running";
        regular[0].Draft = "Keep the host draft";
        regular[1].Draft = "Keep the peer draft";
        vm.SetBridgeTerminalMode(false);
        vm.OpenChat(regular[0]);
        var panel = vm.SharedBridgeTerminal;
        Check("creating the shared view model preserves regular split terminals", Split(regular) && !vm.IsSingleTerminalBridge);
        Check("view initialization does not overwrite the saved split layout", !Saved(regular).SingleTerminal);
        vm.SelectBridgePane(regular[1]);

        // Keep the same two-way selection binding as the mounted (possibly hidden) shared view.
        var roster = new ListBox { DataContext = panel, ItemsSource = panel.Agents };
        roster.SetBinding(Selector.SelectedItemProperty, new Binding(nameof(BridgeSharedTerminalViewModel.Target)) { Mode = BindingMode.TwoWay });
        var (_, other) = Team("single-layout-navigation", 2);
        foreach (var chat in other) { vm.Chats.Add(chat); Call(vm, "Track", chat); }
        Call(vm, "ParkActiveBridge", true);
        vm.BridgePanes.ReplaceAll(other);
        vm.SetBridgeTerminalMode(true);
        vm.OpenChat(other[0]);
        panel.Target = other[1];
        Check("explicit shared recipient selection still selects the agent", other[1].BridgeTerminalSelected && vm.BridgePanelChat == other[1]);

        for (var visit = 1; visit <= 3; visit++)
        {
            vm.OpenChat(regular[0]);
            Check($"return {visit} restores both regular split terminals", vm.ShowBridge && Split(regular) && panel.Target == regular[0]);
            Check($"return {visit} preserves the split bridge's focused side panel", vm.BridgePanelChat == regular[1]);
            Check($"return {visit} keeps regular agents independent", regular.All(p => !p.IsBridgeManager && !p.BridgeCoordinatesOnly));
            vm.SaveBridge();
            Check($"return {visit} saves the original split layout", !Saved(regular).SingleTerminal);
            vm.OpenChat(other[0]);
            Check($"return {visit} preserves the other bridge's single terminal and recipient", vm.IsSingleTerminalBridge &&
                other.Count(p => p.BridgePaneShown) == 1 && other[1].BridgeTerminalSelected && panel.Target == other[1]);
        }

        vm.OpenSecondaryChat(regular[0]);
        var secondaryPanel = vm.SecondarySharedBridgeTerminal;
        Check("opening a regular bridge on the second window preserves split terminals", vm.SecondaryShowBridge &&
            !vm.SecondaryIsSingleTerminalBridge && Split(regular) && secondaryPanel.Target == regular[0]);
        Check("second-window refresh leaves the primary bridge's selection intact", vm.IsSingleTerminalBridge && panel.Target == other[1]);
        vm.OpenChat(regular[0]);
        var plainChat = new ChatViewModel(regular[0].Cwd, provider: "codex");
        Chats.Add(plainChat); vm.Chats.Add(plainChat);
        vm.OpenChat(plainChat);
        Check("opening a plain chat hides the bridge while retaining its roster", !vm.ShowBridge && vm.ActiveChat == plainChat &&
            vm.BridgePanes.SequenceEqual(regular));
        vm.OpenChat(regular[0]);
        Check("returning from a plain chat keeps both regular sessions visible", vm.ShowBridge && Split(regular));
        Check("navigation preserves running sessions and unsent drafts", regular.All(p => p.Status == "running" && !Session(p).HasExited) &&
            regular[0].Draft == "Keep the host draft" && regular[1].Draft == "Keep the peer draft");

        foreach (var original in new[] { regular, other })
        {
            // Round-trip the actual snapshot and resume with fake live sessions, without invoking a provider.
            var saved = JsonSerializer.Deserialize<SavedBridgeState>(JsonSerializer.Serialize(Saved(original)))!;
            AppSettings.Current.UpsertSavedBridge(saved);
            var resumedVm = new MainViewModel();
            var resumedPanel = resumedVm.SharedBridgeTerminal;
            var resumed = original.Select(source =>
            {
                var chat = new ChatViewModel(source.Cwd, provider: source.Provider) { Status = "idle" };
                typeof(ChatViewModel).GetField("_session", Flags)!.SetValue(chat, new FakeSession());
                Property(chat, "SessionId", source.SessionId!);
                Chats.Add(chat); resumedVm.Chats.Add(chat); Call(resumedVm, "Track", chat);
                return chat;
            }).ToArray();
            var layout = saved.SingleTerminal ? "single" : "split";
            Check($"saved {layout} bridge resumes with its own layout", resumedVm.RestoreBridge(resumed[0]) &&
                resumedVm.IsSingleTerminalBridge == saved.SingleTerminal &&
                resumed.Count(p => p.BridgePaneShown) == (saved.SingleTerminal ? 1 : 2));
            Check($"saved {layout} bridge restores its selected recipient", resumedPanel.Target?.SessionId == saved.SelectedTerminalSessionId);
            resumedVm.SaveBridge();
            Check($"restoring {layout} layout does not change the next snapshot", Saved(resumed).SingleTerminal == saved.SingleTerminal);
        }
        BindingOperations.ClearAllBindings(roster);
        vm.CloseBridge();

        static bool Split(ChatViewModel[] panes) => panes.All(p => !p.BridgeSingleTerminal && p.BridgePaneShown);
        static SavedBridgeState Saved(ChatViewModel[] panes) => AppSettings.Current.SavedBridges.Single(s => s.HostSessionId == panes[0].SessionId);
    }
}
