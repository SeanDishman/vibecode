using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VibeCode.Services;
using VibeCode.UI;

namespace VibeCode;

public sealed class BgEntry : Observable
{
    private bool _isActive;
    public string? Path { get; init; }              // null → built-in
    public required string Name { get; init; }
    public bool IsBuiltin => Path is null;
    public ImageSource? Thumb { get; init; }
    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }
    public string AutomationName => $"Background {Name}";
    public string RemoveAutomationName => $"Remove background {Name}";
}

public sealed record HiddenEntry(string Path, string Name)
{
    public string RestoreAutomationName => $"Restore {Name}";
}

/// <summary>One row of the usage table - the plain-text twin of the charts above it, so every figure on the
/// page is readable without hovering a mark. Pre-formatted here rather than in converters because the table and
/// the charts must format identically.</summary>
public sealed class UsageRow
{
    public required string Display { get; init; }
    public required Brush Swatch { get; init; }
    public required string Turns { get; init; }
    public required string TokensIn { get; init; }
    public required string TokensOut { get; init; }
    public required string Cost { get; init; }
    public required string Energy { get; init; }
    public required string Water { get; init; }
    public required Visibility EstimateFlagVisibility { get; init; }

    /// <summary><paramref name="costScale"/> is the largest cost in the table: a column of numbers has to share
    /// one format, or the rows read as "$18.26" beside "$4.431" and stop lining up.</summary>
    public static UsageRow From(ModelUsage model, double costScale) => new()
    {
        Display = model.Display,
        Swatch = UsagePalette.BrushFor(model.Model),
        Turns = model.Turns.ToString("N0"),
        TokensIn = UsageAnalytics.Tokens(model.TotalIn),
        TokensOut = UsageAnalytics.Tokens(model.Output),
        Cost = UsageAnalytics.MoneyScaled(model.CostUsd, costScale),
        Energy = UsageAnalytics.Energy(model.EnergyWh),
        Water = UsageAnalytics.Water(model.WaterLitres),
        EstimateFlagVisibility = model.FullyPriced ? Visibility.Collapsed : Visibility.Visible,
    };
}

public partial class SettingsWindow : Window
{
    private readonly ObservableCollection<BgEntry> _backgrounds = new();
    private readonly ObservableCollection<HiddenEntry> _hidden = new();
    private readonly ObservableCollection<McpServerDefinition> _mcpServers = new();
    private bool _ready;
    private bool _refreshingMcp;

    public SettingsWindow()
    {
        InitializeComponent();
        // Test hook: keep the dialog off every monitor when the app is launched hidden, so automated
        // verification never pops it onto the user's screen (CenterOwner would otherwise fall back on-screen).
        if (Environment.GetEnvironmentVariable("VIBECODE_HIDDEN") == "1")
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = 6100; Top = 300;
        }
        BgList.ItemsSource = _backgrounds;
        HiddenList.ItemsSource = _hidden;
        McpList.ItemsSource = _mcpServers;
        Loaded += (_, _) =>
        {
            EnableDarkTitleBar();
            var s = AppSettings.Current;
            RandomCheck.IsChecked = s.RandomBackground;
            ShowAllCheck.IsChecked = !s.ShowOnlyOwnedSessions;
            IsolateChatsCheck.IsChecked = s.IsolateChatsByAccount;
            VisSlider.Value = Math.Clamp(s.BackgroundVisibility, 0, 80);
            ClientIdBox.Text = s.SpotifyClientId ?? "";
            HideEmailsToggle.IsChecked = s.HideEmails;
            RunInBackgroundToggle.IsChecked = s.RunInBackground;
            BorderlessToggle.IsChecked = s.Borderless;
            CompactModeToggle.IsChecked = s.CompactMode;
            NotifyTurnEndToggle.IsChecked = s.NotifyOnTurnEnd;
            NotifyAwaitingInputToggle.IsChecked = s.NotifyOnAwaitingInput;
            SwarmsEnabledToggle.IsChecked = s.AgentSwarmsEnabled;
            BridgeSwarmsToggle.IsChecked = s.AgentSwarmsInBridge;
            SwarmMaxSlider.Value = SwarmPolicy.ClampMaxWorkers(s.SwarmMaxWorkers);
            DualMonitorBridgeToggle.IsChecked = s.DualMonitorBridge;
            DualMonitorDoubleSessionsToggle.IsChecked = s.DualMonitorDoubleSessions;
            BridgeRealtimeSharingToggle.IsChecked = s.BridgeRealtimeSharing;
            BridgePeerMessagingToggle.IsChecked = s.BridgePeerMessaging;
            BridgeAgentLimitSlider.Value = s.BridgeAgentLimit;
            SupervisionEnabledToggle.IsChecked = s.AgentSupervisionEnabled;
            SupervisionStaleSlider.Value = Math.Clamp(s.SupervisionStaleSeconds, 30, 1800);
            SupervisionGraceSlider.Value = Math.Clamp(s.SupervisionInterventionGraceSeconds, 30, 600);
            SupervisionRestartsSlider.Value = Math.Clamp(s.SupervisionMaxRestarts, 0, 3);
            DemonModeToggle.IsChecked = DemonTeamLive;   // live state, not a stored preference — see DemonStartFolder
            DemonReviewToggle.IsChecked = s.DemonOrchestratorReviewsWork;
            RefreshDualMonitorAvailability();
            TelemetryCompanionDisplayToggle.IsChecked = s.TelemetryOnCompanionDisplay;
            TelemetryAnimationToggle.IsChecked = s.TelemetryLiveAnimation;
            AboutVersionText.Text = AppVersion.Current.IsKnown
                ? $"Version {AppVersion.Current}"
                : "Version unknown";
            RebuildNotificationSounds();
            RebuildBackgrounds();
            RefreshHidden();
            RefreshMcpServers();
            RefreshModeCards();
            _ready = true;
            InitializeSettingsSearch();
        };
    }

    // ---------- UI mode ----------

    private void RefreshModeCards()
    {
        var cli = AppSettings.IsCliMode;
        ModeCardBackground.BorderBrush = (Brush)FindResource(cli ? "BorderSoft" : "Accent");
        ModeCardCli.BorderBrush = (Brush)FindResource(cli ? "Accent" : "BorderSoft");
        ModeCheckBackground.Visibility = cli ? Visibility.Collapsed : Visibility.Visible;
        ModeCheckCli.Visibility = cli ? Visibility.Visible : Visibility.Collapsed;
        // backgrounds are meaningless on the flat terminal canvas - grey the whole section out
        BackgroundSection.IsEnabled = !cli;
        BackgroundSection.Opacity = cli ? 0.45 : 1.0;
        CliModeHint.Visibility = cli ? Visibility.Visible : Visibility.Collapsed;
        // Borderless uncovers the background art, and CLI mode has none. Hidden outright rather than greyed out:
        // a switch that is still ON while doing nothing is worse than no switch at all. The stored preference is
        // left alone, so it comes back exactly as it was when the user picks Custom background again.
        BorderlessSection.Visibility = cli ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Set when the user picked a different UI mode, or flipped Borderless - both are theme-dictionary
    /// changes, and both need the same rebuild. The shell re-skins itself once this dialog is gone - Settings is
    /// modal and owned by that shell, and swapping a modal dialog's owner out from under it is not safe.
    /// Same shape as <see cref="DemonStartFolder"/>: decided here, acted on out there.</summary>
    public bool UiModeChanged { get; private set; }

    private void OnBorderlessChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        var previous = AppSettings.Current.Borderless;
        var target = BorderlessToggle.IsChecked == true;
        if (previous == target) return;
        AppSettings.Current.Borderless = target;
        if (AppSettings.Current.TrySave() is { } err)
        {
            // Same rule the mode cards follow: never re-skin into a look that never landed on disk, or the next
            // launch comes back without it and the switch has silently lied.
            AppSettings.Current.Borderless = previous;
            BorderlessToggle.IsChecked = previous;
            MessageBox.Show(this, $"Couldn't save the borderless setting:\n{err.Message}", "Borderless",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        UiModeChanged = true;
        Close();
    }

    private void OnPickMode(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        var target = ReferenceEquals(sender, ModeCardCli) ? "cli" : "background";
        var previous = AppSettings.Current.UiMode;
        if (string.Equals(previous, target, StringComparison.OrdinalIgnoreCase)) return;
        AppSettings.Current.UiMode = target;
        if (AppSettings.Current.TrySave() is { } err)
        {
            // don't re-skin into a mode that never landed on disk - the next launch would come back unchanged
            AppSettings.Current.UiMode = previous;
            MessageBox.Show(this, $"Couldn't save the mode change:\n{err.Message}", "Change mode",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        // No confirmation prompt: this no longer restarts anything. The look changes in place, chats keep running,
        // and clicking the other card puts it back.
        UiModeChanged = true;
        Close();
    }

    private void EnableDarkTitleBar()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            var enabled = 1;
            DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int));
        }
        catch { /* pre-Win10 1809 */ }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int attrValue, int attrSize);

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    // ---------- backgrounds ----------

    private void RebuildBackgrounds()
    {
        var s = AppSettings.Current;
        _backgrounds.Clear();
        _backgrounds.Add(new BgEntry
        {
            Path = null,
            Name = "Ramen shop · built-in",
            Thumb = LoadThumb(new Uri("pack://application:,,,/Assets/background.gif")),
            IsActive = !s.RandomBackground && s.ActiveBackground is null,
        });
        foreach (var path in s.Backgrounds.Where(File.Exists))
            _backgrounds.Add(new BgEntry
            {
                Path = path,
                Name = System.IO.Path.GetFileNameWithoutExtension(path),
                Thumb = LoadThumb(new Uri(path)),
                IsActive = !s.RandomBackground && string.Equals(s.ActiveBackground, path, StringComparison.OrdinalIgnoreCase),
            });
    }

    private static ImageSource? LoadThumb(Uri uri)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = uri;
            bmp.DecodePixelWidth = 300;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    private void OnAddBackground(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Add background image",
            Filter = "Images (*.gif;*.png;*.jpg;*.jpeg;*.bmp)|*.gif;*.png;*.jpg;*.jpeg;*.bmp",
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) != true) return;
        var s = AppSettings.Current;
        string? lastAdded = null;
        foreach (var file in dlg.FileNames)
        {
            try
            {
                var stored = AppSettings.ImportBackground(file);
                if (!s.Backgrounds.Contains(stored, StringComparer.OrdinalIgnoreCase))
                    s.Backgrounds.Add(stored);
                lastAdded = stored;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Couldn't add {System.IO.Path.GetFileName(file)}:\n{ex.Message}", "Add background");
            }
        }
        if (lastAdded is not null && !s.RandomBackground) s.ActiveBackground = lastAdded;
        s.Save();
        RebuildBackgrounds();
    }

    private void OnPickBackground(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not BgEntry entry) return;
        var s = AppSettings.Current;
        s.ActiveBackground = entry.Path;
        s.RandomBackground = false;
        RandomCheck.IsChecked = false;
        s.Save();
        foreach (var b in _backgrounds) b.IsActive = ReferenceEquals(b, entry);
    }

    private void OnRemoveBackground(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not BgEntry { Path: { } path }) return;
        var s = AppSettings.Current;
        s.Backgrounds.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        if (string.Equals(s.ActiveBackground, path, StringComparison.OrdinalIgnoreCase)) s.ActiveBackground = null;
        s.Save();
        try { if (path.StartsWith(AppSettings.BackgroundsDir, StringComparison.OrdinalIgnoreCase)) File.Delete(path); }
        catch { /* in use - orphan is harmless */ }
        RebuildBackgrounds();
    }

    private void OnRandomChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        var s = AppSettings.Current;
        s.RandomBackground = RandomCheck.IsChecked == true;
        s.Save();
        RebuildBackgrounds();
    }

    private void OnVisibilityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        var s = AppSettings.Current;
        s.BackgroundVisibility = (int)Math.Round(e.NewValue);
        s.Save();
    }

    private void OnShowAllChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        AppSettings.Current.ShowOnlyOwnedSessions = ShowAllCheck.IsChecked != true;
        AppSettings.Current.Save();   // fires Changed -> the main window reloads the project list
    }

    private void OnIsolateChatsChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        AppSettings.Current.IsolateChatsByAccount = IsolateChatsCheck.IsChecked == true;
        AppSettings.Current.Save();   // fires Changed -> the main window re-applies the sidebar filter live
    }

    private void OnCompactModeChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        AppSettings.Current.CompactMode = CompactModeToggle.IsChecked == true;
        AppSettings.Current.Save();
    }

    /// <summary>Takes effect on the next close - there is nothing to apply now, and the shell re-reads it off the
    /// Changed event to relabel its close button.</summary>
    private void OnRunInBackgroundChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        AppSettings.Current.RunInBackground = RunInBackgroundToggle.IsChecked == true;
        AppSettings.Current.Save();
    }

    private void OnNotifyTurnEndChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        AppSettings.Current.NotifyOnTurnEnd = NotifyTurnEndToggle.IsChecked == true;
        AppSettings.Current.Save();
    }

    private void OnNotifyAwaitingInputChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        AppSettings.Current.NotifyOnAwaitingInput = NotifyAwaitingInputToggle.IsChecked == true;
        AppSettings.Current.Save();
    }

    // ---------------- notification sound ----------------

    /// <summary>Fill the picker and select whatever is saved. Also called after a rescan, so it has to
    /// cope with the saved sound having been deleted from the folder in the meantime.</summary>
    private void RebuildNotificationSounds()
    {
        NotificationSoundPicker.ItemsSource = NotificationSounds.All;
        NotificationSoundPicker.SelectedItem = NotificationSounds.Resolve(AppSettings.Current.NotificationSound);
    }

    /// <summary>Selecting plays it. Hearing each one as you arrow down the open dropdown is the whole point —
    /// nobody can pick a notification chime off a name alone.</summary>
    private void OnNotificationSoundChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || NotificationSoundPicker.SelectedItem is not NotificationSound sound) return;
        AppSettings.Current.NotificationSound = sound.Id;
        AppSettings.Current.Save();
        NotificationSounds.Play(sound);
    }

    /// <summary>The play button on a dropdown row: previews without changing what is selected.</summary>
    private void OnPlayNotificationSoundRow(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: NotificationSound sound }) NotificationSounds.Play(sound);
        e.Handled = true;   // don't let the click fall through and re-select the row
    }

    private void OnPreviewNotificationSound(object sender, RoutedEventArgs e)
    {
        if (NotificationSoundPicker.SelectedItem is NotificationSound sound) NotificationSounds.Play(sound);
    }

    private void OnOpenSoundsFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(NotificationSounds.UserDir);
            // Explorer opening on an empty folder tells the user nothing, so leave a note in it.
            var readme = Path.Combine(NotificationSounds.UserDir, "README.txt");
            if (!File.Exists(readme))
            {
                File.WriteAllText(readme,
                    """
                    Drop notification sounds in this folder.

                    They show up in Settings > Notifications alongside the built-in ones, tagged
                    "yours". Hit Rescan there after adding files - the list is read once when the
                    settings window opens.

                    - MP3 and WAV both work (WMA, AIFF and M4A usually do too).
                    - Keep them short. Anything past about four seconds is annoying as a toast.
                    - The filename becomes the name in the list, so "soft ping.mp3" reads as
                      "Soft Ping". Dashes and underscores are treated as spaces, and a leading
                      number like "03-" is dropped so you can force an ordering.

                    Pixabay, Mixkit and freesound.org are all reasonable places to get them.
                    Check the licence on anything you plan to ship rather than just use yourself.
                    """);
            }
            Process.Start(new ProcessStartInfo(NotificationSounds.UserDir) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            // Explorer refusing to open is not worth taking the settings window down over.
        }
    }

    private void OnRescanSounds(object sender, RoutedEventArgs e)
    {
        NotificationSounds.Refresh();
        var wasReady = _ready;
        _ready = false;                 // re-selecting during a rebuild must not re-save or replay
        try { RebuildNotificationSounds(); }
        finally { _ready = wasReady; }
    }

    private void OnDualMonitorBridgeChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        AppSettings.Current.DualMonitorBridge = DualMonitorBridgeToggle.IsChecked == true;
        AppSettings.Current.Save();
    }

    private void OnDualMonitorDoubleSessionsChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        AppSettings.Current.DualMonitorDoubleSessions = DualMonitorDoubleSessionsToggle.IsChecked == true;
        AppSettings.Current.Save();
    }

    private void OnBridgeRealtimeSharingChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        AppSettings.Current.BridgeRealtimeSharing = BridgeRealtimeSharingToggle.IsChecked == true;
        AppSettings.Current.Save();   // fires Changed -> the main window re-briefs any live bridge panes
    }

    private void OnBridgePeerMessagingChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        AppSettings.Current.BridgePeerMessaging = BridgePeerMessagingToggle.IsChecked == true;
        AppSettings.Current.Save();   // fires Changed -> live bridge panes are re-briefed with (or without) the channel
    }

    private void OnBridgeAgentLimitChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        AppSettings.Current.BridgeAgentLimit = BridgeAgentPolicy.ClampLimit((int)Math.Round(e.NewValue));
        AppSettings.Current.Save();
    }

    // ---------- supervision + Demon Mode ----------

    private void OnSupervisionEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        AppSettings.Current.AgentSupervisionEnabled = SupervisionEnabledToggle.IsChecked == true;
        AppSettings.Current.Save();
    }

    private void OnSupervisionStaleChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        AppSettings.Current.SupervisionStaleSeconds = (int)Math.Round(e.NewValue);
        AppSettings.Current.Save();   // a running team picks the new threshold up on its next scan
    }

    private void OnSupervisionGraceChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        AppSettings.Current.SupervisionInterventionGraceSeconds = (int)Math.Round(e.NewValue);
        AppSettings.Current.Save();
    }

    private void OnSupervisionRestartsChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        AppSettings.Current.SupervisionMaxRestarts = (int)Math.Round(e.NewValue);
        AppSettings.Current.Save();
    }

    // ---------- Demon Mode ----------
    //
    // Settings is the ONLY way in and out of Demon Mode. It used to be a stored "enabled" preference that merely added
    // a Normal/Demon question to New chat, which meant the decision was made in one place, armed in a second, and
    // spent in a third when a folder was finally picked. Now the switch IS the team: on asks for the folder and starts
    // it, off stops it. Nothing is persisted — a Demon team is deliberately never resumed, so a remembered "on" could
    // only ever be a lie the next time VibeCode starts.

    /// <summary>True when a Demon team is already running, so the switch opens showing reality.</summary>
    public bool DemonTeamLive { get; init; }

    /// <summary>Whether Kimi's shared CLI login is usable, so the account picker can offer it. Handed in by the
    /// owner: it is the answer to an async CLI probe the main view-model owns, and Settings must not guess it.</summary>
    public bool KimiAvailable { get; init; }

    /// <summary>The folder the user picked for a new team, or null if they did not start one. Read by the owner once
    /// this dialog closes: the view-model lives out there, and the Bridge has to be on screen and unobstructed by the
    /// time sixteen sessions start appearing in it.</summary>
    public string? DemonStartFolder { get; private set; }

    /// <summary>Set when the user switched a live team off.</summary>
    public bool DemonStopRequested { get; private set; }

    /// <summary>AI account, model, thinking level and permission mode for the whole roster, asked for right after
    /// the folder. Non-null whenever <see cref="DemonStartFolder"/> is.</summary>
    public TeamSetup? DemonSetup { get; private set; }

    private void OnDemonModeChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        if (DemonModeToggle.IsChecked == true)
        {
            if (DemonTeamLive) return;                    // already running — the switch is just showing that
            // Folder, then which account and what the sessions run as. Both are asked BEFORE anything spawns, because
            // the CLI takes the login, model and effort at launch: choosing them afterwards would mean restarting the
            // whole team. Cancelling either one cancels the start. Re-entrancy on the reset is fine — it fires
            // Unchecked, which falls through to the DemonTeamLive check below and does nothing.
            if (PickDemonFolder() is not { } folder)
            {
                DemonModeToggle.IsChecked = false;
                return;
            }
            if (SessionSetupWindow.AskForTeam(this, AppSettings.Current.DefaultProvider, KimiAvailable)
                is not { } setup)
            {
                DemonModeToggle.IsChecked = false;
                return;
            }
            DemonStartFolder = folder;
            DemonSetup = setup;
            Close();
            return;
        }
        if (DemonTeamLive) { DemonStopRequested = true; Close(); }
    }

    private string? PickDemonFolder()
    {
        // Automated/off-screen runs get no shell dialog — an unanswered modal would hang every smoke test — so the
        // folder comes from the same env var that starts a team at launch. Empty there still means "cancelled",
        // which is what keeps the cancel path exercisable too.
        if (Environment.GetEnvironmentVariable("VIBECODE_HIDDEN") == "1")
        {
            var fromEnv = Environment.GetEnvironmentVariable("VIBECODE_DEMON_START");
            return Directory.Exists(fromEnv) ? fromEnv : null;
        }

        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            // The size is asked for in the step AFTER this one, so the title names the range rather than a number
            // the user has not chosen yet.
            Title = $"Choose the project for a Demon team " +
                    $"({DemonModePolicy.MinimumSessionCount}-{DemonModePolicy.MaximumSessionCount} sessions)",
        };
        try
        {
            return dlg.ShowDialog(this) == true ? dlg.FolderName : null;
        }
        catch (ArgumentException)
        {
            return null;   // the shell refused to open; treat it as a cancel rather than taking the window down
        }
    }

    private void OnDemonReviewChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        AppSettings.Current.DemonOrchestratorReviewsWork = DemonReviewToggle.IsChecked == true;
        AppSettings.Current.Save();
    }

    /// <summary>
    /// A Demon team and the second display cannot both be had, so while one is running the dual-monitor switches are
    /// disabled and say why.
    ///
    /// Disabled rather than accepted-and-ignored on purpose: the setting is persistent, so accepting it would leave a
    /// switch reading "on" for a window that is never going to open, and the user would go looking for the display
    /// bug instead of the sentence explaining it. The refusal text is the same one the Bridge header shows when a
    /// team starts with the setting already on — one rule, one wording.
    /// </summary>
    private void RefreshDualMonitorAvailability()
    {
        DualMonitorBridgeToggle.IsEnabled = !DemonTeamLive;
        DualMonitorDoubleSessionsToggle.IsEnabled = !DemonTeamLive;
        DemonBlocksDualMonitorNote.Text = DualMonitorBridgePolicy.DemonRefusal +
                                          " End the team above to use a second display.";
        DemonBlocksDualMonitorNote.Visibility = DemonTeamLive ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- hidden projects ----------

    private void RefreshHidden()
    {
        var filter = HiddenSearch.Text.Trim();
        _hidden.Clear();
        foreach (var path in AppSettings.Current.HiddenProjects.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(name)) name = path;
            if (filter.Length > 0
                && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                && !path.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            _hidden.Add(new HiddenEntry(path, name));
        }
        HiddenEmpty.Visibility = _hidden.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HiddenEmptyText.Text = AppSettings.Current.HiddenProjects.Count == 0
            ? "Nothing hidden yet"
            : "No hidden project matches that search.";

        // Offered only on the unfiltered list. With a search active, "Restore all" sitting above a filtered
        // list would read as "restore these", and it does not - it restores every hidden project.
        var total = AppSettings.Current.HiddenProjects.Count;
        RestoreAllButton.Visibility = total > 1 && filter.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        RestoreAllLabel.Text = $"Restore all {total}";
    }

    // ---------- category rail ----------

    /// <summary>Panes in rail order. Kept as one list so adding a category is a single edit here plus the
    /// matching ListBoxItem, instead of renumbering a column of index comparisons.</summary>
    private UIElement[] Panes => new UIElement[]
    {
        PaneGeneral, PaneAppearance, PaneNotifications, PaneBridge, PaneUsage,
        PaneProjects, PaneMcp, PaneExtensions, PanePrivacy, PaneAbout,
    };

    private void OnRailChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (PaneGeneral is null) return;   // during InitializeComponent
        if (_settingsSearchReady && SettingsSearchBox.Text.Length > 0) SettingsSearchBox.Clear();
        var selected = Rail.SelectedIndex;
        var panes = Panes;
        for (var i = 0; i < panes.Length; i++)
            panes[i].Visibility = i == selected ? Visibility.Visible : Visibility.Collapsed;
        // The usage page reads a file and re-aggregates it, so it only does that work once it is on screen.
        if (ReferenceEquals(panes.ElementAtOrDefault(selected), PaneUsage)) RefreshUsage();
    }

    // ---------- usage ----------

    private bool _usageHooked;

    private UsageWindow SelectedUsageWindow =>
        RangeToday.IsChecked == true ? UsageWindow.Today
        : RangeMonth.IsChecked == true ? UsageWindow.Month
        : RangeAll.IsChecked == true ? UsageWindow.All
        : UsageWindow.Week;

    private void OnUsageRangeChanged(object sender, RoutedEventArgs e)
    {
        if (_ready) RefreshUsage();
    }

    // Owner rather than this: Settings is modal, and a telemetry window owned by a dialog would be dismissed
    // along with it. The same idiom the radar button uses.
    private void OnOpenTelemetryHud(object sender, RoutedEventArgs e) => UsageHudWindow.Open(Owner ?? this);

    private void OnOpenTelemetryWall(object sender, RoutedEventArgs e) => UsageDashboardWindow.Open(Owner ?? this);

    private void OnTelemetryCompanionDisplayChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        AppSettings.Current.TelemetryOnCompanionDisplay = TelemetryCompanionDisplayToggle.IsChecked == true;
        AppSettings.Current.Save();
    }

    private void OnTelemetryAnimationChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        AppSettings.Current.TelemetryLiveAnimation = TelemetryAnimationToggle.IsChecked == true;
        AppSettings.Current.Save();   // fires Changed -> an open HUD picks the ripple up without being reopened
    }

    private void OnUsageShareMetricChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        UsageDonut.Metric =
            ShareByTokens.IsChecked == true ? UsageMetric.Tokens
            : ShareByEnergy.IsChecked == true ? UsageMetric.Energy
            : ShareByWater.IsChecked == true ? UsageMetric.Water
            : UsageMetric.Cost;
    }

    private void OnUsageTrendMetricChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        var byCost = TrendByCost.IsChecked == true;
        UsageTrend.Metric = byCost ? UsageMetric.Cost : UsageMetric.Tokens;
        UsageTrendCaption.Text = byCost ? "DAILY SPEND" : "DAILY TOKENS";
    }

    private void OnUsageFootprintMetricChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        var byWater = FootprintByWater.IsChecked == true;
        UsageFootprintTrend.Metric = byWater ? UsageMetric.Water : UsageMetric.Energy;
        UsageFootprintTrendCaption.Text = byWater ? "WATER PER DAY" : "ELECTRICITY PER DAY";
    }

    private void RefreshUsage()
    {
        if (!_usageHooked)
        {
            // Live-update while the page is open: a turn finishing in another window should show up here.
            UsageLog.Instance.Changed += OnUsageLogChanged;
            Closed += (_, _) => UsageLog.Instance.Changed -= OnUsageLogChanged;
            _usageHooked = true;
        }

        var window = SelectedUsageWindow;
        var report = UsageAnalytics.Build(UsageLog.Instance.Entries(), window);

        UsageHeroLabel.Text = $"ESTIMATED SPEND · {UsageAnalytics.Label(window).ToUpperInvariant()}";
        UsageHero.Text = UsageAnalytics.Money(report.CostUsd);
        UsageHeroSub.Text = Describe(report);

        UsageTileIn.Text = UsageAnalytics.Tokens(report.TotalIn);
        UsageTileOut.Text = UsageAnalytics.Tokens(report.Output);
        UsageTileCache.Text = UsageAnalytics.Percent(report.CacheHitRatio);
        UsageTileTurns.Text = report.Turns.ToString("N0");

        UsageEnergy.Text = UsageAnalytics.Energy(report.EnergyWh);
        UsageWater.Text = UsageAnalytics.Water(report.WaterLitres);
        UsageEnergySub.Text = report.HasData ? UsageAnalytics.EnergyEquivalent(report.EnergyWh) : "";
        UsageWaterSub.Text = report.HasData
            ? $"{UsageAnalytics.WaterEquivalent(report.WaterLitres)} of cooling water"
            : "";
        UsageFootprintNote.Text = DescribeFootprint(report);

        UsageDonut.Report = report;
        UsageBars.Report = report;
        UsageTrend.Report = report;
        UsageFootprintTrend.Report = report;

        var costScale = report.Models.Count == 0 ? 0 : report.Models.Max(m => m.CostUsd);
        var rows = report.Models.Select(m => UsageRow.From(m, costScale)).ToList();
        UsageTable.ItemsSource = rows;
        UsageTable.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        UsageTableEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var total = UsageLog.Instance.Count;
        UsageStorageNote.Text = total == 0
            ? "Nothing logged yet."
            : $"{total:N0} calls logged locally, kept for {UsageLog.RetentionDays} days.";
    }

    private static string Describe(UsageReport report)
    {
        if (!report.HasData)
            return report.FirstSeen is null
                ? "No model calls recorded yet — finish a turn in any chat and it will appear here."
                : "No model calls in this range.";
        var turns = report.Turns == 1 ? "1 turn" : $"{report.Turns:N0} turns";
        var models = report.Models.Count == 1 ? "1 model" : $"{report.Models.Count} models";
        var line = $"{UsageAnalytics.Tokens(report.Total)} tokens across {turns} and {models}.";
        // Never quote a confident number for a model we have no published price for.
        return report.FullyPriced ? line : line + " Some models have no published price and are estimated at the Opus tier.";
    }

    /// <summary>The method note under the energy and water figures. It says what the number counts and how wrong
    /// it can be, because a physical quantity printed to three digits invites being believed to three digits.</summary>
    private static string DescribeFootprint(UsageReport report)
    {
        if (!report.HasData)
            return "Electricity and water are estimated from each model's size and the tokens it processed. "
                   + "Nothing recorded in this range yet.";

        var line = "Estimated from each model's active size and its token counts — datacenter electricity "
                   + "including cooling and idle capacity, and the water evaporated cooling it. These are "
                   + "modelled, not metered, and are good to roughly a factor of three. Water counts on-site "
                   + "cooling only; including the water used to generate the electricity it is about "
                   + UsageAnalytics.Water(report.WaterLitresWithGeneration) + ". "
                   + "Most of the total here is output tokens and cache writes — a cache hit skips almost all "
                   + "of the work, which is why " + UsageAnalytics.Percent(report.CacheHitRatio)
                   + " of the input costs so little.";

        // Same honesty rule the cost column follows: never quote a confident number for an unrated model.
        return report.FullyRated
            ? line
            : line + " Some models here have no published size and are estimated at the Opus tier.";
    }

    private void OnUsageLogChanged()
    {
        // Raised from whichever thread committed the turn.
        Dispatcher.BeginInvoke(() =>
        {
            if (_ready && PaneUsage.Visibility == Visibility.Visible) RefreshUsage();
        });
    }

    private void OnExportUsage(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export usage history",
            Filter = "CSV (*.csv)|*.csv",
            FileName = $"vibecode-usage-{DateTime.Now:yyyy-MM-dd}.csv",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var rows = UsageLog.Instance.ExportCsv(dlg.FileName);
            MessageBox.Show(this, $"Wrote {rows:N0} rows to\n{dlg.FileName}", "Export usage",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't write that file:\n{ex.Message}", "Export usage",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnClearUsage(object sender, RoutedEventArgs e)
    {
        var total = UsageLog.Instance.Count;
        if (total == 0) return;
        if (MessageBox.Show(this,
                $"Delete all {total:N0} logged model calls?\n\nThis only erases VibeCode's local usage history — it does not affect your provider accounts, and it cannot be undone.",
                "Clear usage history", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        UsageLog.Instance.Clear();
        RefreshUsage();
    }

    // ---------- MCP catalog ----------

    private void RefreshMcpServers()
    {
        _refreshingMcp = true;
        try
        {
            _mcpServers.Clear();
            foreach (var server in AppSettings.Current.McpServers.OrderBy(server => server.Name, StringComparer.OrdinalIgnoreCase))
                _mcpServers.Add(server);
            McpEmpty.Visibility = _mcpServers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            McpList.Visibility = _mcpServers.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            McpCountText.Text = _mcpServers.Count switch
            {
                0 => "No VibeCode servers",
                1 => "1 VibeCode server",
                _ => $"{_mcpServers.Count} VibeCode servers",
            };
        }
        finally { _refreshingMcp = false; }
    }

    private void OnAddMcpServer(object sender, RoutedEventArgs e)
    {
        if (AppSettings.Current.McpServers.Count >= McpCatalog.MaxManagedServers)
        {
            MessageBox.Show(this, $"VibeCode supports up to {McpCatalog.MaxManagedServers} managed MCP servers. Remove an unused definition first.",
                "MCP server limit", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new McpServerDialog(AppSettings.Current.McpServers) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is null) return;
        AppSettings.Current.McpServers.Add(dialog.Result);
        AppSettings.Current.Save();
        RefreshMcpServers();
    }

    private void OnEditMcpServer(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not McpServerDefinition existing) return;
        var dialog = new McpServerDialog(AppSettings.Current.McpServers, existing) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is null) return;
        var index = AppSettings.Current.McpServers.FindIndex(server =>
            string.Equals(server.Id, existing.Id, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return;
        AppSettings.Current.McpServers[index] = dialog.Result;
        AppSettings.Current.Save();
        RefreshMcpServers();
    }

    private void OnRemoveMcpServer(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not McpServerDefinition existing) return;
        if (MessageBox.Show(this,
                $"Remove the VibeCode MCP definition '{existing.Name}'?\n\nThis does not alter MCP servers configured directly in Claude, Codex, Kimi, or Grok.",
                "Remove MCP server", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        AppSettings.Current.McpServers.RemoveAll(server =>
            string.Equals(server.Id, existing.Id, StringComparison.OrdinalIgnoreCase));
        AppSettings.Current.Save();
        RefreshMcpServers();
    }

    private void OnMcpEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready || _refreshingMcp || (sender as FrameworkElement)?.DataContext is not McpServerDefinition server) return;
        server.Enabled = (sender as System.Windows.Controls.Primitives.ToggleButton)?.IsChecked == true;
        AppSettings.Current.Save();
    }

    // ---------- agent swarms ----------

    private void OnSwarmsEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        AppSettings.Current.AgentSwarmsEnabled = SwarmsEnabledToggle.IsChecked == true;
        AppSettings.Current.Save();
    }

    private void OnBridgeSwarmsChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        AppSettings.Current.AgentSwarmsInBridge = BridgeSwarmsToggle.IsChecked == true;
        AppSettings.Current.Save();
    }

    private void OnSwarmMaxChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        AppSettings.Current.SwarmMaxWorkers = SwarmPolicy.ClampMaxWorkers((int)Math.Round(e.NewValue));
        AppSettings.Current.Save();
    }

    // ---------- privacy ----------

    private void OnHideEmailsChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        AppSettings.Current.HideEmails = HideEmailsToggle.IsChecked == true;
        AppSettings.Current.Save();   // fires Changed -> the main window re-masks the account email
    }

    // ---------- Spotify extension ----------

    private void OnSpotifyEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        // the switch's TwoWay binding already saved AppSettings; poll immediately if we're already connected
        if (SpotifyService.Instance.Enabled && SpotifyService.Instance.IsConnected)
            _ = SpotifyService.Instance.PollAsync();
    }

    // ---------- games extension ----------

    private void OnGamesEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        // the switch's TwoWay binding already saved AppSettings; nudge the titlebar controller to re-read the flag
        GamesService.Instance.NotifyEnabledChanged();
    }

    private async void OnSpotifyConnect(object sender, RoutedEventArgs e)
    {
        var id = ClientIdBox.Text.Trim();
        AppSettings.Current.SpotifyClientId = id;
        AppSettings.Current.Save();
        ConnectStatus.Text = "Connecting… finish in your browser";
        ConnectBtn.IsEnabled = false;
        var err = await SpotifyService.Instance.ConnectAsync(id);
        ConnectBtn.IsEnabled = true;
        ConnectStatus.Text = err is null ? "Connected" : "Not connected";
        if (err is not null) MessageBox.Show(this, err, "Connect Spotify", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private System.Windows.Threading.DispatcherTimer? _copyTimer;

    private void OnCopyDevLink(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText("https://developer.spotify.com/dashboard"); }
        catch { /* clipboard briefly locked by another app */ }
        DevCopied.Visibility = Visibility.Visible;
        _copyTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _copyTimer.Tick -= OnCopyTimer;
        _copyTimer.Tick += OnCopyTimer;
        _copyTimer.Stop();
        _copyTimer.Start();
    }

    private void OnCopyTimer(object? sender, EventArgs e)
    {
        DevCopied.Visibility = Visibility.Collapsed;
        _copyTimer?.Stop();
    }

    // ---------- speech to text: Groq ----------

    private void OnGroqSpeechEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        // The switch's TwoWay binding already saved AppSettings. Nothing else to start or stop: SpeechService reads
        // the flag at the moment it transcribes, so the next mic click simply lands on the other backend.
        GroqSpeechStatus();
    }

    /// <summary>Keep the not-yet-saved status line honest when the card is reopened or the switch is flipped back.
    /// It is a warning, not a result, so it must not linger as "Key accepted" from a previous visit.</summary>
    private void GroqSpeechStatus()
    {
        if (GroqSpeechService.Instance.HasKey) return;   // the saved-key half of the card is showing instead
        GroqKeyStatus.Foreground = (Brush)FindResource("Amber");
        GroqKeyStatus.Text = "Dictation still runs offline until a key is saved.";
    }

    private void OnCopyGroqKeyLink(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText("https://console.groq.com/keys"); }
        catch { /* clipboard briefly locked by another app */ }
        GroqLinkCopied.Visibility = Visibility.Visible;
        _groqCopyTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _groqCopyTimer.Tick -= OnGroqCopyTimer;
        _groqCopyTimer.Tick += OnGroqCopyTimer;
        _groqCopyTimer.Stop();
        _groqCopyTimer.Start();
    }

    private System.Windows.Threading.DispatcherTimer? _groqCopyTimer;

    private void OnGroqCopyTimer(object? sender, EventArgs e)
    {
        GroqLinkCopied.Visibility = Visibility.Collapsed;
        _groqCopyTimer?.Stop();
    }

    private async void OnSaveGroqKey(object sender, RoutedEventArgs e)
    {
        var key = GroqKeyBox.Password?.Trim() ?? "";
        if (key.Length == 0)
        {
            GroqKeyStatus.Foreground = (Brush)FindResource("Red");
            GroqKeyStatus.Text = "Paste a key first.";
            return;
        }

        GroqSaveBtn.IsEnabled = false;
        GroqKeyStatus.Foreground = (Brush)FindResource("Muted");
        GroqKeyStatus.Text = "Checking the key with Groq…";

        // Verified before it is stored, exactly like ApiKeyDialog: a typo caught here beats one that surfaces later
        // as a mic click that just fails.
        var result = await GroqSpeechService.ValidateKeyAsync(key);
        GroqSaveBtn.IsEnabled = true;

        if (!result.Ok)
        {
            GroqKeyStatus.Foreground = (Brush)FindResource("Red");
            GroqKeyStatus.Text = result.Message;
            return;
        }

        if (!GroqSpeechService.Instance.SaveKey(key))
        {
            GroqKeyStatus.Foreground = (Brush)FindResource("Red");
            GroqKeyStatus.Text = "Windows wouldn't encrypt the key, so it was not saved.";
            return;
        }
        GroqKeyBox.Clear();   // the saved-key half of the card takes over; nothing keeps the plaintext on screen
    }

    private void OnRemoveGroqKey(object sender, RoutedEventArgs e)
    {
        GroqSpeechService.Instance.ClearKey();
        GroqKeyBox.Clear();
        GroqSpeechStatus();
    }

    // ---------- phone extension ----------

    private void OnPhoneEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        // the switch's TwoWay binding already saved AppSettings and, when switching off, stopped the bridge;
        // nudge the titlebar button to re-read the flag
        PhoneBridgeService.Instance.NotifyEnabledChanged();
    }

    // ---------- weather extension ----------

    private CancellationTokenSource? _placeSearch;
    private bool _suppressWeatherAutocomplete;

    private void OnWeatherEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        // the switch's TwoWay binding already saved AppSettings; the service refreshes itself when switched on
        WeatherService.Instance.NotifyEnabledChanged();
    }

    private void OnWeatherSearchKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        e.Handled = true;                       // otherwise Enter also rings the default-button bell
        OnWeatherSearch(sender, e);
    }

    private void OnWeatherSearchFocusChanged(object sender, RoutedEventArgs e) => RefreshWeatherSearchHint();

    /// <summary>The grey "Search a U.S. or Canadian city" sitting on top of the empty field. Driven from here
    /// rather than by HintVisibilityConverter: the field lives inside an ExtensionCard's Body, so its logical
    /// parent is a ContentPresenter in the card TEMPLATE, and an ElementName binding would look for
    /// WeatherSearchBox in the template's namescope and silently never find it.</summary>
    private void RefreshWeatherSearchHint() =>
        WeatherSearchHint.Visibility =
            !WeatherSearchBox.IsKeyboardFocusWithin && string.IsNullOrWhiteSpace(WeatherSearchBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;

    private async void OnWeatherSearchTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        // Before the guards: picking a place writes the box's text with autocomplete suppressed, and the hint
        // still has to get out of the way for it.
        RefreshWeatherSearchHint();
        if (!_ready || _suppressWeatherAutocomplete) return;

        _placeSearch?.Cancel();
        WeatherResults.ItemsSource = null;
        var query = WeatherSearchBox.Text.Trim();
        if (query.Length < 3)
        {
            SetWeatherStatus("");
            return;
        }

        var cts = new CancellationTokenSource();
        _placeSearch = cts;
        try
        {
            // Wait until the user pauses so typing a city does not issue a request for every character.
            await Task.Delay(350, cts.Token);
            await RunWeatherSearchAsync(query, cts, automatic: true);
        }
        catch (OperationCanceledException) { }
    }

    private async void OnWeatherSearch(object sender, RoutedEventArgs e)
    {
        var query = WeatherSearchBox.Text.Trim();
        WeatherResults.ItemsSource = null;
        if (query.Length < 2)
        {
            SetWeatherStatus(string.IsNullOrEmpty(query) ? "" : "Type at least 2 characters.");
            return;
        }

        // A second search while the first is still in flight must not have its (older, slower) results win.
        _placeSearch?.Cancel();
        var cts = new CancellationTokenSource();
        _placeSearch = cts;

        await RunWeatherSearchAsync(query, cts, automatic: false);
    }

    private async Task RunWeatherSearchAsync(string query, CancellationTokenSource cts, bool automatic)
    {
        SetWeatherStatus(automatic ? "Finding suggestions…" : "Searching…");
        try
        {
            var hits = await WeatherService.Instance.SearchPlacesAsync(query, cts.Token);
            if (cts.IsCancellationRequested || !ReferenceEquals(_placeSearch, cts)) return;
            WeatherResults.ItemsSource = hits;
            SetWeatherStatus(hits.Count == 0 ? $"No U.S. or Canadian place matched “{query}”." : "");
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (ReferenceEquals(_placeSearch, cts))
                SetWeatherStatus("Place suggestions are unavailable. Check your connection and try again.");
        }
    }

    private void SetWeatherStatus(string text)
    {
        WeatherStatus.Text = text;
        WeatherStatus.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnWeatherPickPlace(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PlaceHit hit) return;
        _placeSearch?.Cancel();
        SetWeatherSearchText(hit.Display);
        WeatherService.Instance.SetLocation(hit.Display, hit.Lat, hit.Lon, hit.CountryCode);
        WeatherResults.ItemsSource = null;
        SetWeatherStatus($"Location set to {hit.Display}.");
    }

    private async void OnWeatherGetLocation(object sender, RoutedEventArgs e)
    {
        _placeSearch?.Cancel();
        var cts = new CancellationTokenSource();
        _placeSearch = cts;
        WeatherResults.ItemsSource = null;
        WeatherLocationButton.IsEnabled = false;
        SetWeatherStatus("Finding your approximate location…");

        try
        {
            var hit = await WeatherService.Instance.GetMyLocationAsync(cts.Token);
            if (cts.IsCancellationRequested || !ReferenceEquals(_placeSearch, cts)) return;

            SetWeatherSearchText(hit.Display);
            WeatherService.Instance.SetLocation(hit.Display, hit.Lat, hit.Lon, hit.CountryCode);
            SetWeatherStatus($"Using approximate location: {hit.Display}.");
        }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException ex)
        {
            if (ReferenceEquals(_placeSearch, cts)) SetWeatherStatus(ex.Message);
        }
        catch (Exception)
        {
            if (ReferenceEquals(_placeSearch, cts))
                SetWeatherStatus("Your location is unavailable. Check your connection and try again.");
        }
        finally
        {
            WeatherLocationButton.IsEnabled = true;
        }
    }

    private void SetWeatherSearchText(string text)
    {
        _suppressWeatherAutocomplete = true;
        try
        {
            WeatherSearchBox.Text = text;
            WeatherSearchBox.CaretIndex = text.Length;
        }
        finally { _suppressWeatherAutocomplete = false; }
    }

    private void OnOpenRadar(object sender, RoutedEventArgs e) => RadarWindow.Open(Owner ?? this);

    private void OnSpotifyDisconnect(object sender, RoutedEventArgs e) => SpotifyService.Instance.Disconnect();
    private void OnSpotifyToggle(object sender, RoutedEventArgs e) => _ = SpotifyService.Instance.ToggleAsync();
    private void OnSpotifyNext(object sender, RoutedEventArgs e) => _ = SpotifyService.Instance.NextAsync();
    private void OnSpotifyPrev(object sender, RoutedEventArgs e) => _ = SpotifyService.Instance.PreviousAsync();

    private void OnHiddenSearch(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_ready) RefreshHidden();
    }

    private void OnRestoreProject(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not HiddenEntry entry) return;
        AppSettings.Current.HiddenProjects.Remove(entry.Path);
        AppSettings.Current.Save();
        RefreshHidden();
    }

    /// <summary>Put every hidden project back at once - the undo for the sidebar's Hide all. No confirmation:
    /// this only ever ADDS rows back to a list, and the way to undo it is the button that got you here.</summary>
    private void OnRestoreAllProjects(object sender, RoutedEventArgs e)
    {
        if (AppSettings.Current.HiddenProjects.Count == 0) return;
        AppSettings.Current.HiddenProjects.Clear();
        AppSettings.Current.Save();
        RefreshHidden();
    }
}
