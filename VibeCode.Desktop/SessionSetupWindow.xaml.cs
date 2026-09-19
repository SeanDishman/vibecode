using System.Windows;
using System.Windows.Controls;
using VibeCode.Services;
using VibeCode.UI;

namespace VibeCode;

/// <summary>
/// Asks what a session should run as: model, then thinking level, then permission mode — and, for a whole team, how
/// many sessions there are and which account signs them in.
///
/// Used in two places, which is the whole reason it is a window rather than three popups: once when a Demon team
/// starts (the answer is applied to every session before any of them spawn), and again from a single pane's ⋮ menu
/// afterwards. A Demon worker has no composer, so its ⋮ menu is the only place those three controls exist — which is
/// exactly the "you can still change them one by one" half of the feature.
/// </summary>
public partial class SessionSetupWindow : Window
{
    private readonly SessionSetupViewModel _vm;

    private SessionSetupWindow(SessionSetupViewModel vm, string heading, string subheading, string confirm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Heading.Text = heading;
        Subheading.Text = subheading;
        ConfirmButton.Content = confirm;
        // Keep the harness (and any off-screen verification run) from throwing this onto the user's display.
        if (Environment.GetEnvironmentVariable("VIBECODE_HIDDEN") == "1")
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = 6100; Top = 300;
        }
    }

    /// <summary>Ask how big a Demon team should be, which AI account it should run on, and what its sessions should
    /// start as. Returns null if the user cancelled — which must cancel the team too, since none of this can be
    /// applied after the sessions spawn.</summary>
    /// <param name="kimiAvailable">Whether Kimi's shared CLI login is usable. Passed in because that answer comes
    /// from an async probe the main view-model owns; this dialog must not guess it.</param>
    public static TeamSetup? AskForTeam(Window? owner, string provider, bool kimiAvailable = false)
    {
        // Preselected to what a chat would have started as anyway (see ChatViewModel's constructor), so opening this
        // dialog and pressing Start changes nothing. Defaulting the thinking level to Auto instead would quietly
        // downgrade anyone whose remembered default is xhigh.
        var current = new SessionSetup(SessionSetupViewModel.DefaultModelFor(provider),
            SessionSetupViewModel.DefaultEffortFor(provider), AppSettings.Current.DefaultMode);
        var accountId = TeamAccountRow.ActiveIdFor(provider);
        var size = DemonModePolicy.ClampSessionCount(AppSettings.Current.DemonSessionCount);
        // Automated/off-screen runs take the defaults rather than a modal nobody can answer, matching how the folder
        // picker and the old new-chat chooser behave. Returning null here would cancel the team outright.
        if (Environment.GetEnvironmentVariable("VIBECODE_HIDDEN") == "1")
            return new TeamSetup(ProviderModelCatalog.Normalize(provider), accountId,
                ProviderModelCatalog.DisplayName(provider), current, size);
        var vm = new SessionSetupViewModel(provider, current, ProviderModelCatalog.DisplayName(provider),
            TeamAccountRow.Discover(kimiAvailable), accountId, size);
        if (Show(owner, vm,
                "Demon Mode setup",
                $"A team of {DemonModePolicy.MinimumSessionCount}-{DemonModePolicy.MaximumSessionCount} sessions, " +
                "all signed in as one account and started with these. You can still change any pane on its own " +
                "afterwards, from its ⋮ menu.",
                "Start the team") is null) return null;
        // Remembered here rather than by the caller: this is where the user chose it, and the next team should open
        // on the size they last ran — including the automation path above, which reads the same stored value.
        if (AppSettings.Current.DemonSessionCount != vm.SessionCount)
        {
            AppSettings.Current.DemonSessionCount = vm.SessionCount;
            AppSettings.Current.Save();
        }
        return vm.TeamResult;
    }

    /// <summary>Ask for one pane's settings. Pre-selected to what that pane is running now.</summary>
    public static SessionSetup? AskForPane(Window? owner, ChatViewModel pane)
    {
        var vm = new SessionSetupViewModel(pane.Provider, new SessionSetup(pane.Model, pane.Effort, pane.Mode),
            pane.AgentDisplay);
        return Show(owner, vm, $"{pane.BridgeLabel} setup",
            "Applies to this session only — the rest of the team is left alone.", "Apply");
    }

    private static SessionSetup? Show(Window? owner, SessionSetupViewModel vm, string heading, string subheading,
        string confirm)
    {
        var dialog = new SessionSetupWindow(vm, heading, subheading, confirm);
        if (owner is not null && owner.IsLoaded) dialog.Owner = owner;
        return dialog.ShowDialog() == true ? vm.Result : null;
    }

    private void OnPickTeamSize(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is int count) _vm.SessionCount = count;
    }

    private void OnPickAccount(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TeamAccountRow row) _vm.SelectAccount(row);
    }

    private void OnPickModel(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string value) _vm.Model = value;
    }

    private void OnPickEffort(object sender, RoutedEventArgs e)
    {
        // Tag is null for the "Auto" row, which is exactly the value Auto means - so read Tag, do not require it.
        _vm.Effort = (sender as FrameworkElement)?.Tag as string;
    }

    private void OnPickMode(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string value) _vm.Mode = value;
    }

    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
