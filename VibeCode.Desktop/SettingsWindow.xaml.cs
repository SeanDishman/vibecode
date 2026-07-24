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
            VisSlider.Value = Math.Clamp(s.BackgroundVisibility, 0, 80);
            ClientIdBox.Text = s.SpotifyClientId ?? "";
            HideEmailsToggle.IsChecked = s.HideEmails;
            CompactModeToggle.IsChecked = s.CompactMode;
            NotifyTurnEndToggle.IsChecked = s.NotifyOnTurnEnd;
            NotifyAwaitingInputToggle.IsChecked = s.NotifyOnAwaitingInput;
            SwarmsEnabledToggle.IsChecked = s.AgentSwarmsEnabled;
            BridgeSwarmsToggle.IsChecked = s.AgentSwarmsInBridge;
            SwarmMaxSlider.Value = SwarmPolicy.ClampMaxWorkers(s.SwarmMaxWorkers);
            DualMonitorBridgeToggle.IsChecked = s.DualMonitorBridge;
            DualMonitorDoubleSessionsToggle.IsChecked = s.DualMonitorDoubleSessions;
            BridgeRealtimeSharingToggle.IsChecked = s.BridgeRealtimeSharing;
            BridgeAgentLimitSlider.Value = s.BridgeAgentLimit;
            TelemetryCompanionDisplayToggle.IsChecked = s.TelemetryOnCompanionDisplay;
            TelemetryAnimationToggle.IsChecked = s.TelemetryLiveAnimation;
            RebuildNotificationSounds();
            RebuildBackgrounds();
            RefreshHidden();
            RefreshMcpServers();
            RefreshModeCards();
            _ready = true;
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
    }

    private void OnPickMode(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        var target = ReferenceEquals(sender, ModeCardCli) ? "cli" : "background";
        if (string.Equals(AppSettings.Current.UiMode, target, StringComparison.OrdinalIgnoreCase)) return;
        var label = target == "cli" ? "CLI mode" : "Custom background mode";
        if (MessageBox.Show(this,
                $"Switch to {label}?\n\nVibeCode restarts to apply the new look. Open chats are saved and reopened automatically.",
                "Change mode", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        AppSettings.Current.UiMode = target;
        if (AppSettings.Current.TrySave() is { } err)
        {
            // don't restart into a mode that never landed on disk - the relaunch would come back unchanged
            AppSettings.Current.UiMode = target == "cli" ? "background" : "cli";
            MessageBox.Show(this, $"Couldn't save the mode change:\n{err.Message}", "Change mode",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        App.RestartToApplyTheme();
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

    private void OnCompactModeChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        AppSettings.Current.CompactMode = CompactModeToggle.IsChecked == true;
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

    private void OnBridgeAgentLimitChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        AppSettings.Current.BridgeAgentLimit = BridgeAgentPolicy.ClampLimit((int)Math.Round(e.NewValue));
        AppSettings.Current.Save();
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
        PaneAppearance, PaneNotifications, PaneBridge, PaneUsage,
        PaneProjects, PaneMcp, PaneExtensions, PanePrivacy, PaneAbout,
    };

    private void OnRailChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (PaneAppearance is null) return;   // during InitializeComponent
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

    private async void OnWeatherSearchTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
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
