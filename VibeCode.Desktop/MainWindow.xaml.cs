using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Data;
using VibeCode.Protocol;
using VibeCode.Services;
using VibeCode.UI;
using WpfAnimatedGif;

namespace VibeCode;

public partial class MainWindow : Window
{
    public static readonly DependencyProperty SurfaceActiveChatProperty = DependencyProperty.Register(
        nameof(SurfaceActiveChat), typeof(ChatViewModel), typeof(MainWindow));
    public static readonly DependencyProperty SurfaceShowHomeProperty = DependencyProperty.Register(
        nameof(SurfaceShowHome), typeof(bool), typeof(MainWindow));
    public static readonly DependencyProperty SurfaceShowBridgeProperty = DependencyProperty.Register(
        nameof(SurfaceShowBridge), typeof(bool), typeof(MainWindow));
    public static readonly DependencyProperty SurfacePanelChatProperty = DependencyProperty.Register(
        nameof(SurfacePanelChat), typeof(ChatViewModel), typeof(MainWindow));

    public ChatViewModel? SurfaceActiveChat
    {
        get => (ChatViewModel?)GetValue(SurfaceActiveChatProperty);
        private set => SetValue(SurfaceActiveChatProperty, value);
    }
    public bool SurfaceShowHome
    {
        get => (bool)GetValue(SurfaceShowHomeProperty);
        private set => SetValue(SurfaceShowHomeProperty, value);
    }
    public bool SurfaceShowBridge
    {
        get => (bool)GetValue(SurfaceShowBridgeProperty);
        private set => SetValue(SurfaceShowBridgeProperty, value);
    }
    public ChatViewModel? SurfacePanelChat
    {
        get => (ChatViewModel?)GetValue(SurfacePanelChatProperty);
        private set => SetValue(SurfacePanelChatProperty, value);
    }

    private readonly MainViewModel _vm;
    private readonly bool _isBridgeMonitor;
    private readonly bool _isDoubleSessionCompanionMode;
    private readonly MainWindow? _primaryWindow;
    private bool _stickToBottom = true;
    private ChatViewModel? _wired;
    private string _appliedBackground = "";
    private string? _randomChoice;       // per-launch pick; "<builtin>" sentinel for the bundled gif
    private string _hiddenSnapshot = "";
    private bool _showOnlyOwnedSnapshot = AppSettings.Current.ShowOnlyOwnedSessions;
    private bool _hideEmailsSnapshot = AppSettings.Current.HideEmails;
    private bool _compactModeSnapshot = AppSettings.Current.CompactMode;
    private bool _dualMonitorBridgeSnapshot = AppSettings.Current.DualMonitorBridge;
    private bool _dualMonitorDoubleSessionsSnapshot = AppSettings.Current.DualMonitorDoubleSessions;
    private ListBox? _bridgeScrollList;
    private ChatViewModel? _bridgeScrollPane;
    private ListCollectionView? _bridgePaneView;
    private ListCollectionView? _allAccountManagerView;
    private ListCollectionView? _claudeAccountManagerView;
    private ListCollectionView? _codexAccountManagerView;
    private ListCollectionView? _grokAccountManagerView;
    private bool _accountManagerRefreshQueued;
    private MainWindow? _bridgeMonitorWindow;
    private MonitorWorkArea? _bridgeMonitorTarget;
    private HwndSource? _windowSource;
    private nint _lastMainMonitor;
    private bool _startupComplete;
    private bool _internalBridgeMonitorClose;
    private int _settingsDialogDepth;
    private bool _isClosing;
    private bool _bridgeCollectionRefreshQueued;

    private bool IsDoubleSessionCompanion => _isDoubleSessionCompanionMode;
    private ChatViewModel? ActiveChatForWindow => IsDoubleSessionCompanion ? _vm.SecondaryActiveChat : _vm.ActiveChat;
    private ChatViewModel? PanelChatForWindow => SurfaceShowBridge ? _vm.BridgePanelChat : ActiveChatForWindow;

    public MainWindow() : this(new MainViewModel(), null)
    {
    }

    private MainWindow(MainViewModel vm, MainWindow? primaryWindow)
    {
        _vm = vm;
        _primaryWindow = primaryWindow;
        _isBridgeMonitor = primaryWindow is not null;
        _isDoubleSessionCompanionMode = _isBridgeMonitor && AppSettings.Current.DualMonitorDoubleSessions;
        InitializeComponent();
        DataContext = _vm;
        RefreshSurfaceState();

        // WPF otherwise scrolls an expanded tool/thinking card into view. Each full chat shell owns this guard.
        MsgList.AddHandler(FrameworkElement.RequestBringIntoViewEvent,
            new RequestBringIntoViewEventHandler((_, e) => e.Handled = true), true);

        _vm.PropertyChanged += OnMainViewModelPropertyChanged;
        _vm.BridgePanes.CollectionChanged += OnBridgePanesChanged;
        Closing += OnWindowClosing;
        Closed += OnWindowClosed;

        if (_isBridgeMonitor)
        {
            ConfigureBridgeMonitorShell();
            Loaded += OnBridgeMonitorLoaded;
            Deactivated += OnShellDeactivated;
            return;
        }

        EnsureAccountManagerViews();
        _vm.AllAccounts.CollectionChanged += OnAccountManagerCollectionChanged;
        _vm.Accounts.CollectionChanged += OnAccountManagerCollectionChanged;
        _vm.CodexAccounts.CollectionChanged += OnAccountManagerCollectionChanged;
        _vm.GrokAccounts.CollectionChanged += OnAccountManagerCollectionChanged;
        RestoreWindowPlacement();
        if (IsWeatherMapsSmoke)
        {
            Title = "VibeCode Weather Maps Smoke";
            StartupOverlay.Visibility = Visibility.Collapsed;
            _startupComplete = true;
            var weatherMapsTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(750),
            };
            weatherMapsTimer.Tick += (_, _) =>
            {
                weatherMapsTimer.Stop();
                RadarWindow.Open(this);
            };
            weatherMapsTimer.Start();
        }
        else if (IsMcpSettingsSmoke)
        {
            Title = "VibeCode MCP Settings Smoke";
            StartupOverlay.Visibility = Visibility.Collapsed;
            _startupComplete = true;
            var settingsTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(750),
            };
            settingsTimer.Tick += (_, _) =>
            {
                settingsTimer.Stop();
                ShowSettingsDialog();
            };
            settingsTimer.Start();
        }
        else Loaded += OnLoaded;
        SourceInitialized += OnMainWindowSourceInitialized;
        LocationChanged += OnMainWindowLocationChanged;

        // Persistence must not depend on OnWindowClosing: a Task Manager kill, a crash or a power loss never runs it.
        SizeChanged += OnShellPlacementChanged;
        StateChanged += OnShellPlacementChanged;
        Deactivated += OnShellDeactivated;   // alt-tabbing away is a free, natural moment to flush
        if (Application.Current is { } app) app.SessionEnding += OnSessionEnding;
        if (!_isBridgeMonitor) App.PersistOnExit = PersistShellState;
    }

    private void OnShellDeactivated(object? sender, EventArgs e)
    {
        if ((_isBridgeMonitor && !IsDoubleSessionCompanion) || _isClosing) return;
        if (Environment.GetEnvironmentVariable("VIBECODE_HIDDEN") == "1") return;
        CaptureComposerDraft();
        _vm.FlushPendingSave();
    }

    /// <summary>Flush everything worth keeping. Idempotent, so every shutdown route can call it.</summary>
    private void PersistShellState()
    {
        if (_isBridgeMonitor) return;
        if (Environment.GetEnvironmentVariable("VIBECODE_HIDDEN") == "1") return;
        try
        {
            CaptureComposerDraft();
            if (_bridgeMonitorWindow is { IsDoubleSessionCompanion: true } companion)
                companion.CaptureComposerDraft();
            SaveWindowPlacement();
            _vm.SaveEverything();
        }
        catch { /* a failed save must never block or crash the shutdown */ }
    }

    private System.Windows.Threading.DispatcherTimer? _placementSaveTimer;

    /// <summary>Window moved/resized/maximized: remember it now, coalesced, instead of only on a polite close.</summary>
    private void OnShellPlacementChanged(object? sender, EventArgs e)
    {
        if (_isBridgeMonitor || _isClosing || !_startupComplete) return;
        if (Environment.GetEnvironmentVariable("VIBECODE_HIDDEN") == "1") return;   // never pollute the real settings

        if (_placementSaveTimer is null)
        {
            _placementSaveTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(900),   // one write per drag, not one per pixel
            };
            _placementSaveTimer.Tick += (_, _) =>
            {
                _placementSaveTimer!.Stop();
                if (_isClosing) return;
                try { SaveWindowPlacement(); AppSettings.Current.Save(); }
                catch { /* transient IO - the next change retries */ }
            };
        }
        _placementSaveTimer.Stop();
        _placementSaveTimer.Start();
    }

    /// <summary>Windows is shutting down or logging the user off. This is the last moment we are guaranteed to run.</summary>
    private void OnSessionEnding(object? sender, SessionEndingCancelEventArgs e)
    {
        if (_isClosing) return;
        PersistShellState();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_isClosing) return;
        _isClosing = true;
        if (_isBridgeMonitor)
        {
            if (IsDoubleSessionCompanion)
            {
                CaptureComposerDraft();
                _vm.FlushPendingSave();
            }
            return; // this view never owns or disposes provider sessions
        }

        AppSettings.Changed -= OnSettingsChanged;   // unsubscribe before the final Save so it doesn't reload
        AppSettings.ActiveAccountAdopted -= OnActiveAccountAdopted;
        _vm.Chats.CollectionChanged -= OnChatsChanged;
        _vm.AllAccounts.CollectionChanged -= OnAccountManagerCollectionChanged;
        _vm.Accounts.CollectionChanged -= OnAccountManagerCollectionChanged;
        _vm.CodexAccounts.CollectionChanged -= OnAccountManagerCollectionChanged;
        _vm.GrokAccounts.CollectionChanged -= OnAccountManagerCollectionChanged;
        _codexRefreshTimer?.Stop();
        _kimiRefreshTimer?.Stop();
        _grokRefreshTimer?.Stop();
        _placementSaveTimer?.Stop();
        if (Application.Current is { } closingApp) closingApp.SessionEnding -= OnSessionEnding;
        PersistShellState();   // no-ops under the off-screen test hook, which must not touch the user's real settings
        foreach (var p in _vm.LiveBridgePeers.ToList()) p.Close();   // includes peers parked behind other chats
        foreach (var c in _vm.Chats.ToList()) { UnhookChat(c); c.Close(); }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _vm.PropertyChanged -= OnMainViewModelPropertyChanged;
        _vm.BridgePanes.CollectionChanged -= OnBridgePanesChanged;
        if (_wired is not null)
        {
            _wired.ItemsChanged -= OnChatItemsChanged;
            _wired = null;
        }

        if (_isBridgeMonitor)
        {
            if (_primaryWindow is { } primary)
            {
                if (ReferenceEquals(primary._bridgeMonitorWindow, this)) primary._bridgeMonitorWindow = null;
                // Closing the second monitor is a LAYOUT choice, not a quit: fold every agent back into the one
                // remaining window. (This used to close the primary window too, which killed the whole app and took
                // every running agent with it.) An internal collapse already did the fold, so it skips this.
                if (!_internalBridgeMonitorClose && !primary._isClosing)
                {
                    var doubleSession = IsDoubleSessionCompanion;
                    primary.Dispatcher.BeginInvoke(new Action(() => primary.CollapseDualMonitorCompanion(doubleSession)));
                }
            }
            return;
        }

        if (_windowSource is not null)
        {
            _windowSource.RemoveHook(OnMainWindowMessage);
            _windowSource = null;
        }

        if (_bridgeMonitorWindow is { _isClosing: false } companion)
        {
            companion._internalBridgeMonitorClose = true;
            companion.Close();
        }
        _bridgeMonitorWindow = null;
    }

    private void ConfigureBridgeMonitorShell()
    {
        Title = "VibeCode — Bridge monitor";
        Title = IsDoubleSessionCompanion ? "VibeCode - Session 2" : Title;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowActivated = false; // adding agent 3 should not pull keyboard focus away from the composer the user clicked
        MinWidth = IsDoubleSessionCompanion ? 960 : 640;
        MinHeight = 480;
        StartupOverlay.Visibility = Visibility.Collapsed;
        // App-wide identity, settings, and extension controls stay in the primary titlebar in both companion modes.
        TitleBarBrand.Visibility = Visibility.Collapsed;
        GamesButton.Visibility = Visibility.Collapsed;
        WeatherChip.Visibility = Visibility.Collapsed;
        SettingsButton.Visibility = Visibility.Collapsed;
        BridgeMonitorTitle.Visibility = Visibility.Visible;
        BridgeMonitorTitle.Text = IsDoubleSessionCompanion ? "VibeCode - Session 2" : "Bridge - second display";
        if (IsDoubleSessionCompanion)
        {
            // Both shells keep chat/project navigation. Identity and app-wide configuration remain in the primary.
            AccountFooter.Visibility = Visibility.Collapsed;
        }
        else
        {
            SidebarSurface.Visibility = Visibility.Collapsed;
            ChatSurface.Visibility = Visibility.Collapsed;
            RightPanelSurface.Visibility = Visibility.Collapsed;
        }
        SetBridgePanePartition(companion: true);
    }

    private void RefreshSurfaceState()
    {
        var active = ActiveChatForWindow;
        var showBridge = IsDoubleSessionCompanion
            ? _vm.SecondaryShowBridge
            : _vm.ShowBridge;
        if (showBridge && (!_vm.IsBridge
                           || (IsDoubleSessionCompanion
                               && !ReferenceEquals(active, _vm.BridgePanes.FirstOrDefault()))))
            showBridge = false;

        SurfaceActiveChat = active;
        SurfaceShowBridge = showBridge;
        SurfaceShowHome = active is null && !showBridge;
        SurfacePanelChat = showBridge ? _vm.BridgePanelChat : active;

        // Popup content is hosted in a separate visual tree, so it cannot use an ancestor-window binding.
        MorePopup.DataContext = active;
        ModePopup.DataContext = active;
        ThinkPopup.DataContext = active;
        ExtendedQueuePopup.DataContext = active;
        ModelPopup.DataContext = active;
        UsagePopup.DataContext = active;
        GrokUsagePopup.DataContext = active;
        if (showBridge || active?.IsClaude != true) UsagePopup.IsOpen = false;
        if (showBridge || active?.IsGrok != true) GrokUsagePopup.IsOpen = false;
        if (!showBridge) BridgeGrokUsagePopup.IsOpen = false;
        if (!showBridge) BridgeKimiUsagePopup.IsOpen = false;
    }

    private void GoHomeForWindow()
    {
        // Capture the chat we are leaving so the folder box reopens on that project, not the user profile.
        var leavingCwd = ActiveChatForWindow?.Cwd;
        if (IsDoubleSessionCompanion) _vm.GoSecondaryHome();
        else _vm.GoHome();
        PrefillCwdBox(leavingCwd);
    }

    /// <summary>Put the most useful project path in the new-chat folder field (last chat, else MRU, else profile).</summary>
    private void PrefillCwdBox(string? preferred = null)
    {
        if (ShellDirectory(preferred) is { } fromChat)
        {
            CwdBox.Text = fromChat;
            return;
        }
        CwdBox.Text = ShellDirectory(_vm.PreferredNewChatCwd) ?? _vm.DefaultCwd;
    }

    private void OpenChatForWindow(ChatViewModel chat)
    {
        if (IsDoubleSessionCompanion) _vm.OpenSecondaryChat(chat);
        else _vm.OpenChat(chat);
    }

    private ChatViewModel NewChatForWindow(string cwd, string? resume = null, bool fork = false,
        string? title = null, string? provider = null)
    {
        var chat = _vm.NewChat(cwd, resume, fork, title, provider, activatePrimary: !IsDoubleSessionCompanion);
        if (IsDoubleSessionCompanion) _vm.OpenSecondaryChat(chat);
        return chat;
    }

    private ChatViewModel ResumeSessionForWindow(SessionEntry session)
    {
        var chat = _vm.ResumeSession(session, activatePrimary: !IsDoubleSessionCompanion);
        if (IsDoubleSessionCompanion) _vm.OpenSecondaryChat(chat);
        return chat;
    }

    private void ActivateBridgeForWindow(string? peerProvider = null)
    {
        if (IsDoubleSessionCompanion)
        {
            if (ActiveChatForWindow is { } chat) _vm.ActivateBridgeOnSecondary(chat, peerProvider);
        }
        else
        {
            _vm.ActivateBridge(peerProvider);
        }
        RefreshSurfaceState();
        RefreshBridgePartitions();
    }

    private void OnBridgeMonitorLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnBridgeMonitorLoaded;
        EnableDarkTitleBar();
        ApplyBackground();
        if (IsDoubleSessionCompanion)
        {
            PrefillCwdBox();
            SetNewChatProvider(AppSettings.Current.DefaultProvider, persist: false);
            _vm.EnsureSecondarySelection((_vm.ShowBridge || _vm.SecondaryShowBridge)
                                         && _vm.BridgePanes.Any(pane => pane.OnSecondMonitor));
            RefreshSurfaceState();
            WireActiveChat();
        }
        ScheduleBridgeGridRows();
    }

    private void OnMainViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        var surfaceChanged = e.PropertyName is nameof(MainViewModel.ActiveChat)
            or nameof(MainViewModel.SecondaryActiveChat)
            or nameof(MainViewModel.ShowBridge)
            or nameof(MainViewModel.SecondaryShowBridge)
            or nameof(MainViewModel.PanelChat)
            or nameof(MainViewModel.BridgePanelChat);
        if (surfaceChanged) RefreshSurfaceState();

        if ((!_isBridgeMonitor && e.PropertyName == nameof(MainViewModel.ActiveChat))
            || (IsDoubleSessionCompanion && e.PropertyName == nameof(MainViewModel.SecondaryActiveChat)))
            WireActiveChat();
        if (e.PropertyName == nameof(MainViewModel.BridgeGridRows)) ScheduleBridgeGridRows();
        if (!_isBridgeMonitor && e.PropertyName is nameof(MainViewModel.ShowBridge)
            or nameof(MainViewModel.SecondaryShowBridge))
            ReconcileDualMonitorBridge();
    }

    private void OnBridgePanesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // A roster swap clears one collection and adds up to eight panes synchronously. Coalesce that burst into one
        // layout/monitor reconciliation; otherwise every Add queues another full Bridge layout after navigation.
        if (_bridgeCollectionRefreshQueued) return;
        _bridgeCollectionRefreshQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _bridgeCollectionRefreshQueued = false;
            if (_isClosing) return;
            _bridgePaneView?.Refresh(); // index-based split: a host removal can shift every remaining pane
            RefreshSurfaceState();
            ScheduleBridgeGridRows();
            if (!_isBridgeMonitor) ReconcileDualMonitorBridge();
        }));
    }

    private void SetBridgePanePartition(bool companion)
    {
        // Filter on the pane's OWN surface flag, never on its index: index-based ownership silently dragged agents
        // across displays whenever a peer was added or removed.
        var view = new ListCollectionView((IList)_vm.BridgePanes)
        {
            Filter = item => item is ChatViewModel pane && pane.OnSecondMonitor == companion,
        };
        _bridgePaneView = view;
        BridgePaneList.ItemsSource = view;
        ScheduleBridgeGridRows();
    }

    /// <summary>The one-time spill applied as the split opens: the roster is divided evenly, half staying here and half
    /// moving across (the odd one out stays here). Afterwards each pane keeps whatever surface it is on, so an agent
    /// added on a display stays on that display.</summary>
    private void AssignInitialBridgeSurfaces()
    {
        var total = _vm.BridgePanes.Count;
        for (var i = 0; i < total; i++)
            _vm.BridgePanes[i].OnSecondMonitor = DualMonitorBridgePolicy.IsCompanionPane(i, total);
    }

    /// <summary>True while the Bridge is actually spread over two windows, asked from either window.</summary>
    private bool SplitActive => (_isBridgeMonitor
            ? _primaryWindow?._bridgeMonitorWindow is not null
            : _bridgeMonitorWindow is not null)
        && _vm.BridgePanes.Any(pane => pane.OnSecondMonitor);

    /// <summary>Re-evaluate both windows' filters and grid density after the pane set or its ownership changed.</summary>
    private void RefreshBridgePartitions()
    {
        var primary = _isBridgeMonitor ? _primaryWindow : this;
        if (primary is null) return;
        primary._bridgePaneView?.Refresh();
        primary._bridgeMonitorWindow?._bridgePaneView?.Refresh();
        primary.ScheduleBridgeGridRows();
        primary._bridgeMonitorWindow?.ScheduleBridgeGridRows();
    }

    private void RestoreSingleMonitorBridge()
    {
        _bridgePaneView = null;
        BindingOperations.SetBinding(BridgePaneList, ItemsControl.ItemsSourceProperty,
            new Binding(nameof(MainViewModel.BridgePanes)));
        ScheduleBridgeGridRows();
    }

    private bool PaneBelongsToThisBridgeSurface(ChatViewModel pane) =>
        _vm.BridgePanes.Contains(pane) && pane.OnSecondMonitor == _isBridgeMonitor;

    private void ScheduleBridgeGridRows()
    {
        // Loaded priority: after a partition change the ItemsControl still has to regenerate its containers, and at
        // Normal priority we would measure (and size) the panel against the OUTGOING pane set.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            if (_isClosing || !BridgePaneList.IsLoaded) return;
            BridgePaneList.UpdateLayout();
            if (FindVisualDescendant<UniformGrid>(BridgePaneList) is not { } grid) return;
            var panes = _bridgePaneView is null
                ? _vm.BridgePanes.AsEnumerable()
                : _bridgePaneView.Cast<ChatViewModel>();
            var visible = panes.Count(p => p.BridgePaneShown);
            grid.Rows = DualMonitorBridgePolicy.RowsForVisiblePaneCount(visible);
        }));
    }

    private void ReconcileDualMonitorBridge(bool reposition = false)
    {
        if (_isBridgeMonitor || !_startupComplete || _isClosing) return;
        if (Environment.GetEnvironmentVariable("VIBECODE_HIDDEN") == "1")
        {
            CloseBridgeMonitorInternal();
            return;
        }

        var settings = AppSettings.Current;
        var monitorCount = DisplayMonitorService.ActiveMonitorCount;
        var bridgeVisible = _vm.ShowBridge || _vm.SecondaryShowBridge;
        var shouldSplit = DualMonitorBridgePolicy.ShouldSplit(
            settings.DualMonitorBridge,
            bridgeVisible,
            _vm.BridgePanes.Count,
            monitorCount);
        var shouldOpen = DualMonitorBridgePolicy.ShouldOpenCompanion(
            settings.DualMonitorBridge,
            settings.DualMonitorDoubleSessions,
            bridgeVisible,
            _vm.BridgePanes.Count,
            monitorCount);
        var shouldPartition = DualMonitorBridgePolicy.ShouldPartition(
            shouldSplit, settings.DualMonitorDoubleSessions, _vm.ShowBridge, _vm.SecondaryShowBridge);
        if (!shouldOpen || !DisplayMonitorService.TryGetCompanionWorkArea(this, out var target))
        {
            CloseBridgeMonitorInternal();
            return;
        }

        if (_bridgeMonitorWindow is { } existing
            && existing.IsDoubleSessionCompanion != settings.DualMonitorDoubleSessions)
        {
            CloseBridgeMonitorInternal();
        }

        if (_bridgeMonitorWindow is null)
        {
            // ShowDialog runs a nested dispatcher. Defer creation until it returns so a brand-new maximized window
            // cannot activate behind (or remain enabled beside) a modal Settings window.
            if (_settingsDialogDepth > 0) return;
            OpenBridgeMonitor(target);
            return;
        }

        if (shouldPartition)
        {
            // Seed a new roster once, then preserve explicit per-pane ownership as agents are added or removed.
            if (!_vm.BridgePanes.Any(pane => pane.OnSecondMonitor)) AssignInitialBridgeSurfaces();
            SetBridgePanePartition(companion: false);
            _bridgeMonitorWindow.SetBridgePanePartition(companion: true);
        }
        else
        {
            foreach (var pane in _vm.BridgePanes) pane.OnSecondMonitor = false;
            RestoreSingleMonitorBridge();
            _bridgeMonitorWindow.RestoreSingleMonitorBridge();
        }
        ScheduleBridgeGridRows();
        _bridgeMonitorWindow.ScheduleBridgeGridRows();

        if (reposition || _bridgeMonitorTarget?.Handle != target.Handle)
        {
            try
            {
                DisplayMonitorService.PlaceOnMonitor(_bridgeMonitorWindow, target, maximize: true);
                _bridgeMonitorTarget = target;
            }
            catch (Exception ex)
            {
                CloseBridgeMonitorInternal();
                ShowBridgeHint("Dual-monitor Bridge returned to one window: " + ex.Message);
            }
        }
    }

    private void OpenBridgeMonitor(MonitorWorkArea target)
    {
        MainWindow? companion = null;
        try
        {
            _vm.ResetBridgeExpand(); // a single-window focus must not strand hidden panes on the new display
            var splitEligible = DualMonitorBridgePolicy.ShouldSplit(
                AppSettings.Current.DualMonitorBridge,
                _vm.ShowBridge || _vm.SecondaryShowBridge,
                _vm.BridgePanes.Count,
                DisplayMonitorService.ActiveMonitorCount);
            var splitBridge = DualMonitorBridgePolicy.ShouldPartition(
                splitEligible, AppSettings.Current.DualMonitorDoubleSessions,
                _vm.ShowBridge, _vm.SecondaryShowBridge);
            if (splitBridge) AssignInitialBridgeSurfaces();
            else foreach (var pane in _vm.BridgePanes) pane.OnSecondMonitor = false;
            companion = new MainWindow(_vm, this)
            {
                _randomChoice = _randomChoice, // random backgrounds remain visually identical across both views
            };
            _bridgeMonitorWindow = companion;
            _bridgeMonitorTarget = target;
            _lastMainMonitor = DisplayMonitorService.MonitorFor(this);
            if (splitBridge)
            {
                SetBridgePanePartition(companion: false);
                companion.SetBridgePanePartition(companion: true);
            }
            else
            {
                RestoreSingleMonitorBridge();
                companion.RestoreSingleMonitorBridge();
            }
            // WPF refuses to Show a non-activating window that is already Maximized. Place its HWND on the target
            // while normal, show it without stealing focus, and only then maximize it on that display.
            var maximizeAfterShow = DualMonitorBridgePolicy.RequiresPostShowMaximize(
                companion.ShowActivated, companion.IsVisible);
            DisplayMonitorService.PlaceOnMonitor(companion, target, maximize: !maximizeAfterShow);
            companion.Show();
            if (maximizeAfterShow) companion.WindowState = WindowState.Maximized;
            if (DisplayMonitorService.MonitorFor(companion) != target.Handle)
                throw new InvalidOperationException("Windows moved the Bridge companion away from the selected display.");
        }
        catch (Exception ex)
        {
            _bridgeMonitorWindow = null;
            _bridgeMonitorTarget = null;
            RestoreSingleMonitorBridge();
            if (companion is { _isClosing: false })
            {
                companion._internalBridgeMonitorClose = true;
                companion.Close();
            }
            ShowBridgeHint("Dual-monitor Bridge stayed in one window: " + ex.Message);
        }
    }

    private void CloseBridgeMonitorInternal()
    {
        if (_isBridgeMonitor) return;
        var companion = _bridgeMonitorWindow;
        _bridgeMonitorWindow = null;
        _bridgeMonitorTarget = null;

        // Reattach the authoritative collection BEFORE removing the second view, so there is never a state in which a
        // draft/session exists but is unreachable. Every agent comes home to this window, whichever display it was on
        // - clearing the surface flags is what makes the restored list contain all of them again.
        if (companion is not null || _bridgePaneView is not null)
        {
            _vm.ResetBridgeExpand();
            foreach (var pane in _vm.BridgePanes) pane.OnSecondMonitor = false;
            RestoreSingleMonitorBridge();
        }

        if (companion is null) return;
        companion._internalBridgeMonitorClose = true;
        if (!companion._isClosing) companion.Close();
    }

    /// <summary>
    /// Fold the Bridge back into this single window and stop re-splitting. This is what closing the second-monitor
    /// window does: the agents all keep running and simply return here. It must never close the app - the agent cap
    /// is unchanged, so the returning agents still fit under the same limit they were created within.
    /// </summary>
    private void CollapseDualMonitorCompanion(bool doubleSession)
    {
        if (_isBridgeMonitor || _isClosing) return;
        var settings = AppSettings.Current;
        var changed = false;
        if (doubleSession && settings.DualMonitorDoubleSessions)
        {
            settings.DualMonitorDoubleSessions = false;
            changed = true;
        }
        // Closing the companion is an explicit return-to-one-window choice. If Bridge splitting is also enabled,
        // disable it too so the just-closed companion does not immediately reopen as a pane-only window.
        if (settings.DualMonitorBridge)
        {
            settings.DualMonitorBridge = false;
            changed = true;
        }
        if (changed)
        {
            settings.Save();   // remember the choice; also re-enters here via OnSettingsChanged
        }
        CloseBridgeMonitorInternal();     // idempotent, and covers the setting already being off
    }

    private void OnMainWindowSourceInitialized(object? sender, EventArgs e)
    {
        _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _windowSource?.AddHook(OnMainWindowMessage);
        _lastMainMonitor = DisplayMonitorService.MonitorFor(this);
    }

    private void OnMainWindowLocationChanged(object? sender, EventArgs e)
    {
        if (!_startupComplete || _isClosing) return;
        var current = DisplayMonitorService.MonitorFor(this);
        if (current == nint.Zero || current == _lastMainMonitor) return;
        _lastMainMonitor = current;
        if (_bridgeMonitorWindow is not null) ReconcileDualMonitorBridge(reposition: true);
    }

    private nint OnMainWindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        const int wmDisplayChange = 0x007E;
        if (message == wmDisplayChange)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _lastMainMonitor = DisplayMonitorService.MonitorFor(this);
                ReconcileDualMonitorBridge(reposition: true);
            }));
        }
        return nint.Zero;
    }

    private static bool IsWeatherMapsSmoke => Environment.GetEnvironmentVariable("VIBECODE_HIDDEN") == "1"
        && Environment.GetEnvironmentVariable("VIBECODE_OPEN_WEATHER_MAPS") == "1";

    private static bool IsMcpSettingsSmoke => Environment.GetEnvironmentVariable("VIBECODE_HIDDEN") == "1"
        && (Environment.GetEnvironmentVariable("VIBECODE_OPEN_SETTINGS") == "1"
            || Environment.GetCommandLineArgs().Contains("--mcp-settings-smoke", StringComparer.OrdinalIgnoreCase));

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        var startupTimer = Stopwatch.StartNew();
        try
        {
            // Yield before doing first-launch work so the loading surface is painted immediately.
            await ShowStartupStepAsync("Preparing your workspace…", 16, 1);
            EnableDarkTitleBar();
            PrefillCwdBox();
            SetNewChatProvider(AppSettings.Current.DefaultProvider, persist: false);
            _hiddenSnapshot = HiddenSnapshot();

            await ShowStartupStepAsync("Loading projects and appearance…", 38, 2);
            _vm.LoadProjects();
            ApplyBackground();
            AppSettings.Changed += OnSettingsChanged;
            AppSettings.ActiveAccountAdopted += OnActiveAccountAdopted;   // another window's newer pick won - re-render
            _vm.Chats.CollectionChanged += OnChatsChanged;
            foreach (var c in _vm.Chats) HookChat(c);

            await ShowStartupStepAsync("Restoring accounts and conversations…", 68, 3);
            _vm.RefreshAccounts();  // load the account chip + snapshot the live login FIRST, so restored chats can resolve their per-account token
            _vm.ApplyCodexAccounts(CodexAccountService.Instance.List()); // local metadata first; network refresh happens after restore
            _vm.ApplyGrokAccounts(GrokAccountService.Instance.List()); // adopt the legacy login and bind restored Grok chats to it
            var savedCodexBridges = AppSettings.Current.SavedBridges
                .Where(x => x.Provider == "codex")
                .ToList();
            var ownedCodexSessions = AppSettings.Current.OpenChats
                .Where(x => x.Provider == "codex")
                .Select(x => x.SessionId)
                .Concat(savedCodexBridges.Select(x => (string?)x.HostSessionId))
                .Concat(savedCodexBridges.SelectMany(x => x.Peers.Select(p => p.SessionId)));
            Protocol.CodexEnvironment.ImportOwnedSessions(ownedCodexSessions);
            _vm.RestoreSession();   // reopen last session's chats (restored chats get hooked via CollectionChanged)

            await ShowStartupStepAsync("Starting background services…", 88, 4);
            StartTokenRefresh();    // keep every account's OAuth token fresh so per-account chats don't hit the ~8h expiry
            StartCodexAccountRefresh(); // VibeCode owns this OpenAI session and refreshes it even when no Codex chat is open
            StartKimiAccountRefresh();  // authenticate is a token-free status check and does not create a Kimi session
            StartGrokAccountRefresh();  // authenticate every isolated Grok profile and refresh its real xAI billing
            StartAccountUsagePolling(); // pre-warm + periodically refresh each account's session/week usage so changes surface
            StartSpotifyPolling();  // keep the Spotify mini-menu's now-playing fresh (no-op while the extension is off)
            StartWeatherPolling();  // keep the titlebar weather chip current (no-op while the extension is off)
        }
        finally
        {
            // Avoid a distracting one-frame flash on unusually fast launches.
            var minimumVisible = TimeSpan.FromMilliseconds(650);
            if (startupTimer.Elapsed < minimumVisible)
                await Task.Delay(minimumVisible - startupTimer.Elapsed);
            await HideStartupOverlayAsync();
            _startupComplete = true;
            ReconcileDualMonitorBridge();
        }
    }

    private Task ShowStartupStepAsync(string text, double progress, int step) =>
        StartupOverlay.SetStageAsync(text, progress, step);

    private Task HideStartupOverlayAsync() => StartupOverlay.CompleteAsync();

    // ---------- settings ----------

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        ShowSettingsDialog();
    }

    private void ShowSettingsDialog()
    {
        var coordinator = _isBridgeMonitor ? _primaryWindow : this;
        if (coordinator is null) return;
        coordinator._settingsDialogDepth++;
        try
        {
            new SettingsWindow { Owner = this }.ShowDialog();
        }
        finally
        {
            coordinator._settingsDialogDepth--;
            coordinator.ReconcileDualMonitorBridge();
        }
    }

    private void OnHideProject(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (Ctx<ProjectVm>(sender) is { } p) _vm.HideProject(p);
    }

    /// <summary>Clear the whole projects list in one click. This asks first, unlike the per-row ✕: one project
    /// back is one click, and thirty back was thirty until Settings grew a Restore all — so the dialog names the
    /// number and says where the undo is instead of assuming the user knows.</summary>
    private void OnHideAllProjects(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var count = _vm.Projects.Count;
        if (count == 0) return;
        if (MessageBox.Show(this,
                $"Hide all {count} project{(count == 1 ? "" : "s")} from the sidebar?\n\n"
                + "Nothing is deleted — open chats keep running, and Settings ▸ Projects restores them "
                + "individually or all at once.",
                "Hide all projects?", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        _vm.HideAllProjects();
    }

    private static string HiddenSnapshot() =>
        string.Join("|", AppSettings.Current.HiddenProjects.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));

    // A routine save here (background toggle, chat activity…) found a newer account selection on disk and kept it
    // instead of reverting it. The checkmark, footer and every chat chip must follow the account that actually won.
    private void OnActiveAccountAdopted() => Dispatcher.BeginInvoke(() => _vm.RefreshAccounts());

    private void OnSettingsChanged()
    {
        Dispatcher.BeginInvoke(() =>
        {
            ApplyBackground();
            if (_bridgeMonitorWindow is { _isClosing: false } bridgeMonitor)
            {
                bridgeMonitor._randomChoice = _randomChoice;
                bridgeMonitor.ApplyBackground();
            }
            SpotifyService.Instance.NotifyEnabledChanged();   // show/hide the titlebar chip when the toggle changes
            WeatherService.Instance.NotifyEnabledChanged();   // same for the weather chip next to the gear
            var snap = HiddenSnapshot();
            var onlyOwned = AppSettings.Current.ShowOnlyOwnedSessions;
            if (snap != _hiddenSnapshot || onlyOwned != _showOnlyOwnedSnapshot)
            {
                _hiddenSnapshot = snap;
                _showOnlyOwnedSnapshot = onlyOwned;
                _vm.LoadProjects();
            }
            if (AppSettings.Current.HideEmails != _hideEmailsSnapshot)
            {
                _hideEmailsSnapshot = AppSettings.Current.HideEmails;
                _vm.RefreshAccounts();   // refresh any detailed views whose privacy setting changed
            }
            if (AppSettings.Current.CompactMode != _compactModeSnapshot)
            {
                _compactModeSnapshot = AppSettings.Current.CompactMode;
                foreach (var chat in _vm.Chats.Concat(_vm.LiveBridgePeers).Distinct())
                    chat.SetCompactMode(_compactModeSnapshot);
            }
            foreach (var chat in _vm.Chats.Concat(_vm.LiveBridgePeers).Distinct())
                chat.RefreshSwarmSettings();
            _vm.RefreshBridgeLimit();
            _vm.RefreshBridgeRealtimeSharing();
            // Only the two dual-monitor toggles change what a reconcile would do, but EVERY settings write raises
            // AppSettings.Changed (autosave, window-placement saves on each move/resize, account refreshes…). Running
            // ReconcileDualMonitorBridge on all of them rebuilt both bridge partitions each time — tearing down and
            // recreating every pane view on the second monitor, which is the "second monitor keeps refreshing" bug.
            // Monitor-count, bridge-visibility and pane-set changes each arrive on their own events (WM_DISPLAYCHANGE,
            // ShowBridge/SecondaryShowBridge, BridgePanes), so gate this reconcile on the settings that actually feed it.
            if (AppSettings.Current.DualMonitorBridge != _dualMonitorBridgeSnapshot
                || AppSettings.Current.DualMonitorDoubleSessions != _dualMonitorDoubleSessionsSnapshot)
            {
                _dualMonitorBridgeSnapshot = AppSettings.Current.DualMonitorBridge;
                _dualMonitorDoubleSessionsSnapshot = AppSettings.Current.DualMonitorDoubleSessions;
                ReconcileDualMonitorBridge();
            }
        });
    }

    private void ApplyBackground()
    {
        var s = AppSettings.Current;

        // CLI mode: a terminal is flat black - no art, no vignette. The theme's own opaque scrim
        // brushes already cover the backdrop; just make sure the image never shows and bail out.
        if (AppSettings.IsCliMode)
        {
            BgImage.Visibility = Visibility.Collapsed;
            var flat = new SolidColorBrush(Color.FromRgb(0x0C, 0x0C, 0x0C));
            flat.Freeze();
            Application.Current.Resources["ScrimChat"] = flat;
            Application.Current.Resources["ScrimHome"] = flat;
            Application.Current.Resources["ScrimBridge"] = flat;
            return;
        }

        var pool = s.Backgrounds.Where(File.Exists).ToList();

        string? target = null; // null → built-in
        if (s.RandomBackground && pool.Count > 0)
        {
            if (_randomChoice is null)
            {
                var choices = new List<string?> { null };
                choices.AddRange(pool);
                _randomChoice = choices[Random.Shared.Next(choices.Count)] ?? "<builtin>";
            }
            target = _randomChoice == "<builtin>" ? null : _randomChoice;
        }
        else
        {
            _randomChoice = null;
            if (s.ActiveBackground is { } active && File.Exists(active)) target = active;
        }

        var key = target ?? "<builtin>";
        if (key != _appliedBackground)
        {
            try
            {
                var uri = target is null
                    ? new Uri("pack://application:,,,/Assets/background.gif")
                    : new Uri(target);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = uri;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                if (key.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) || target is null)
                {
                    ImageBehavior.SetAnimatedSource(BgImage, bmp);
                }
                else
                {
                    ImageBehavior.SetAnimatedSource(BgImage, null);
                    BgImage.Source = bmp;
                }
                _appliedBackground = key;
            }
            catch { /* unreadable image - keep the previous backdrop */ }
        }

        // Vertical-gradient vignette instead of a flat tint: anchor the header and composer
        // zones dark, let the mid reading-band breathe. Reads as depth, not wallpaper.
        var visibility = Math.Clamp(s.BackgroundVisibility, 0, 100);
        // Chat keeps a heavy floor (text must stay readable) - shows roughly half the slider's art.
        byte midChat = (byte)Math.Round(255 * (1 - visibility / 100.0 * 0.5));
        Application.Current.Resources["ScrimChat"] =
            MakeScrimGradient((byte)Math.Min(255, midChat + 32), (byte)Math.Min(255, midChat + 8), midChat);
        // Home shows a bit more art than chat (it's the hero), but this scene is busy - keep a firm floor.
        byte midHome = (byte)Math.Round(255 * (1 - visibility / 100.0 * 0.9));
        Application.Current.Resources["ScrimHome"] =
            MakeScrimGradient((byte)Math.Min(255, midHome + 40), (byte)Math.Min(255, midHome + 14), midHome);
        // Bridge sits between the two: the translucent glass panes carry their own text floor, so the
        // backdrop can breathe through the panes and the gaps between them without hurting readability.
        byte midBridge = (byte)Math.Round(255 * (1 - visibility / 100.0 * 0.8));
        Application.Current.Resources["ScrimBridge"] =
            MakeScrimGradient((byte)Math.Min(255, midBridge + 30), (byte)Math.Min(255, midBridge + 10), midBridge);
    }

    // Builds a top-to-bottom scrim over the neutral base (#1A1A1D): opaque at the edges, lighter mid.
    private static LinearGradientBrush MakeScrimGradient(byte edge, byte near, byte mid)
    {
        static Color C(byte a) => Color.FromArgb(a, 0x1A, 0x1A, 0x1D);
        var b = new LinearGradientBrush { StartPoint = new System.Windows.Point(0, 0), EndPoint = new System.Windows.Point(0, 1) };
        b.GradientStops.Add(new GradientStop(C(edge), 0.0));
        b.GradientStops.Add(new GradientStop(C(near), 0.12));
        b.GradientStops.Add(new GradientStop(C(mid), 0.5));
        b.GradientStops.Add(new GradientStop(C(near), 0.86));
        b.GradientStops.Add(new GradientStop(C(edge), 1.0));
        b.Freeze();
        return b;
    }

    // ---------- window placement (remember size/position across launches) ----------

    private void RestoreWindowPlacement()
    {
        // Test hook: VIBECODE_HIDDEN=1 launches the window off every monitor (still rendered, so it's
        // drivable/capturable) so automated verification never pops a window onto the user's screen.
        if (Environment.GetEnvironmentVariable("VIBECODE_HIDDEN") == "1")
        {
            // Off every monitor but still a normal (taskbar-visible, non-minimized) window so it renders
            // and its handle is findable for automation. It never appears on the user's screen.
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = 6000; Top = 200; Width = 1500; Height = 950;
            return;
        }
        var s = AppSettings.Current;
        if (s.WindowWidth is > 300 && s.WindowHeight is > 300)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            if (s.WindowLeft is { } l) Left = l;
            if (s.WindowTop is { } t) Top = t;
            Width = s.WindowWidth.Value;
            Height = s.WindowHeight.Value;
            EnsureOnScreen();   // a saved spot on a now-disconnected monitor would hide the window
        }
        if (s.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private void EnsureOnScreen()
    {
        double vl = SystemParameters.VirtualScreenLeft, vt = SystemParameters.VirtualScreenTop;
        double vw = SystemParameters.VirtualScreenWidth, vh = SystemParameters.VirtualScreenHeight;
        if (Left < vl || Left > vl + vw - 100) Left = vl + 40;
        if (Top < vt || Top > vt + vh - 100) Top = vt + 40;
    }

    private void SaveWindowPlacement()
    {
        var s = AppSettings.Current;
        s.WindowMaximized = WindowState == WindowState.Maximized;
        // save the *normal* bounds even when maximized, so un-maximizing next launch is correct
        var b = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (b.Width > 300 && b.Height > 300)
        {
            s.WindowLeft = b.Left; s.WindowTop = b.Top; s.WindowWidth = b.Width; s.WindowHeight = b.Height;
        }
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

    // ---------- attention / notifications ----------

    private void OnChatsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null) foreach (ChatViewModel c in e.NewItems) HookChat(c);
        if (e.OldItems is not null) foreach (ChatViewModel c in e.OldItems) UnhookChat(c);
    }

    private void HookChat(ChatViewModel c)
    {
        c.TurnCompleted += OnChatWantsAttention;
        c.AttentionNeeded += OnChatWantsAttention;
    }

    private void UnhookChat(ChatViewModel c)
    {
        c.TurnCompleted -= OnChatWantsAttention;
        c.AttentionNeeded -= OnChatWantsAttention;
    }

    // Flash the taskbar button when a turn finishes or Claude needs the user, but only if the
    // window isn't already focused (no point nagging someone who's watching).
    private void OnChatWantsAttention()
    {
        if (!IsActive) Dispatcher.BeginInvoke(FlashTaskbar);
    }

    /// <summary>True when this shell window is currently rendering the given chat: the selected chat of its surface,
    /// or - with the Bridge overlay up - a pane on THIS window's surface that isn't collapsed behind an expanded
    /// peer. Used by <see cref="NotificationService"/> to decide whether the user can already see the event.</summary>
    public bool DisplaysChat(ChatViewModel chat)
    {
        if (!IsLoaded || !IsVisible) return false;
        if (SurfaceShowBridge)
        {
            var onThisSurface = _bridgePaneView is null
                ? _vm.BridgePanes.Contains(chat)
                : PaneBelongsToThisBridgeSurface(chat);
            return onThisSurface && chat.BridgePaneShown;
        }
        return ReferenceEquals(ActiveChatForWindow, chat);
    }

    /// <summary>Bring this shell to the front and select the given chat - re-entering its live bridge when the chat
    /// is a pane of one. A parked bridge peer has no sidebar row of its own; focusing the shell is the safe fallback
    /// (its roster swaps back in when the user clicks the host).</summary>
    public void NavigateToChat(ChatViewModel chat)
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
        if (_vm.BridgePanes.Contains(chat) && _vm.BridgePanes.FirstOrDefault() is { } host) { _vm.OpenChat(host); return; }
        if (_vm.Chats.Contains(chat)) _vm.OpenChat(chat);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public nint hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    private const uint FLASHW_ALL = 0x3, FLASHW_TIMERNOFG = 0xC;

    private void FlashTaskbar()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == nint.Zero) return;
            var fi = new FLASHWINFO
            {
                cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
                hwnd = hwnd,
                dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG,   // flash until the window is brought to the foreground
                uCount = uint.MaxValue,
                dwTimeout = 0,
            };
            FlashWindowEx(ref fi);
        }
        catch { /* flashing is best-effort */ }
    }

    // ---------- window chrome ----------

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaxRestoreClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    // ---------- active chat wiring ----------

    private void WireActiveChat()
    {
        if (_wired is not null)
        {
            _wired.ItemsChanged -= OnChatItemsChanged;
            CaptureComposerDraft(_wired);   // leaving a chat: keep whatever was half-typed in it
        }
        _wired = ActiveChatForWindow;
        if (_wired is not null)
        {
            _wired.ItemsChanged += OnChatItemsChanged;
            _wired.ResetPromptHistoryNavigation();
        }
        // Each chat owns its composer text, so switching chats (or relaunching) shows that chat's unsent prompt back.
        _restoringComposerDraft = true;
        try { if (InputBox is not null) InputBox.Text = _wired?.Draft ?? ""; }
        finally { _restoringComposerDraft = false; }
        _stickToBottom = true;
        Dispatcher.BeginInvoke(() => { ScrollMsgToBottom(); InputBox?.Focus(); });
    }

    private bool _restoringComposerDraft;

    /// <summary>The main composer is a plain TextBox rather than a bound control, so its text has to be folded into
    /// the chat before anything is persisted - otherwise a force quit throws away the prompt being written.</summary>
    private void CaptureComposerDraft(ChatViewModel? chat = null)
    {
        if ((_isBridgeMonitor && !IsDoubleSessionCompanion) || InputBox is null) return;
        if ((chat ?? ActiveChatForWindow) is { } target) target.Draft = InputBox.Text;
    }

    private void OnChatItemsChanged()
    {
        if (_stickToBottom) Dispatcher.BeginInvoke(ScrollMsgToBottom, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OnMsgScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // The message ScrollViewer now lives inside the ListBox control template, so there's no MsgScroll field -
        // read the offsets straight off the event args. Only re-evaluate stickiness on real scrolls (not when the
        // extent grew because a new message was appended), otherwise appending would flip us off "stick to bottom".
        if (e.ExtentHeightChange == 0)
            _stickToBottom = e.VerticalOffset >= e.ExtentHeight - e.ViewportHeight - 120;
    }

    // Scroll the virtualized message list to the very bottom. The ScrollViewer is inside MsgList's template, so we
    // walk the visual tree for it; if the template isn't realized yet (first layout), fall back to bringing the last
    // item into view so a freshly-opened chat still lands at the newest message.
    private void ScrollMsgToBottom()
    {
        var sv = FindScrollViewer(MsgList);
        if (sv is not null) sv.ScrollToBottom();
        else if (MsgList.Items.Count > 0) MsgList.ScrollIntoView(MsgList.Items[MsgList.Items.Count - 1]);
    }

    // Depth-first search of the visual tree for the first ScrollViewer under d (the one baked into MsgList's template).
    private static ScrollViewer? FindScrollViewer(DependencyObject d)
    {
        if (d is ScrollViewer sv) return sv;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var found = FindScrollViewer(VisualTreeHelper.GetChild(d, i));
            if (found is not null) return found;
        }
        return null;
    }

    // ---------- "jump to your messages" navigator ----------

    /// <summary>Focus the message list when the navigator opens so wheel/keyboard stay on the popup instead of the
    /// transcript underneath (WPF can route wheel to the focused chat even while the cursor is over a Popup).</summary>
    private void OnUserMessagesPopupOpened(object sender, EventArgs e)
    {
        if (sender is not Popup popup) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var list = FindVisualDescendant<ListBox>(popup.Child);
            if (list is null) return;
            list.Focus();
            Keyboard.Focus(list);
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    // A row in the navigator popup was clicked: fly the owning transcript to that prompt and flash it. The prompt's
    // Owner identifies its chat/pane; NavHost maps that VM to its realized (virtualized) message ListBox.
    private void OnNavigateToMessage(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: UserItem item }) return;
        ClosePopupAround(sender as DependencyObject);
        var list = NavHost.ForChat(item.Owner) ?? MsgList;
        if (list is null) return;
        // Defer past the popup close so the scroll animation isn't competing with the fade-out this same tick.
        Dispatcher.BeginInvoke(new Action(() => NavigateToUserMessage(list, item)), System.Windows.Threading.DispatcherPriority.Background);
    }

    // Smoothly scroll `list` so `item` sits comfortably in view, then pulse it. Works with UI virtualization: the
    // target offset is measured by briefly bringing the item into view and reading the offset within the SAME layout
    // pass (no frame is painted between measure and reset, so there is no visible pre-jump), then we animate to it.
    private void NavigateToUserMessage(ListBox list, UserItem item)
    {
        var sv = FindScrollViewer(list);
        if (sv is null) { list.ScrollIntoView(item); FlashItem(list, item); return; }

        var start = sv.VerticalOffset;
        list.ScrollIntoView(item);
        sv.UpdateLayout();                       // realize + position the target in this pass
        var measured = sv.VerticalOffset;
        // Leave breathing room above the message instead of gluing it to the very top edge.
        var target = Math.Max(0, Math.Min(measured - sv.ViewportHeight * 0.28, sv.ScrollableHeight));
        sv.ScrollToVerticalOffset(start);        // snap back before anything paints
        sv.UpdateLayout();

        AnimateScroll(sv, start, target, () => FlashItem(list, item));
    }

    private static void AnimateScroll(ScrollViewer sv, double from, double to, Action onDone)
    {
        if (Math.Abs(from - to) < 1.5) { onDone(); return; }
        // Duration scales with distance: a tiny hop is quick, a long fly still feels controlled.
        var ms = Math.Clamp(Math.Abs(from - to) * 0.55, 260, 680);
        var anim = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        };
        anim.Completed += (_, _) =>
        {
            // Hand control back to the user: drop the animation hold and pin the final offset as the base value.
            sv.BeginAnimation(ScrollAnimationBehavior.VerticalOffsetProperty, null);
            ScrollAnimationBehavior.SetVerticalOffset(sv, to);
            sv.ScrollToVerticalOffset(to);
            onDone();
        };
        ScrollAnimationBehavior.SetVerticalOffset(sv, from);
        sv.BeginAnimation(ScrollAnimationBehavior.VerticalOffsetProperty, anim);
    }

    // Pulse the accent overlay on the target row. After the scroll the row is on-screen, so its container is realized;
    // if virtualization estimation drifted and it isn't, bring it in and retry once on the next layout pass.
    private void FlashItem(ListBox list, object item)
    {
        list.UpdateLayout();
        if (TryFlash(list, item)) return;
        list.ScrollIntoView(item);
        Dispatcher.BeginInvoke(new Action(() => TryFlash(list, item)), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static bool TryFlash(ListBox list, object item)
    {
        if (list.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement container) return false;
        if (FindDescendantByName(container, "NavFlash") is not Border overlay) return false;
        var pulse = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        pulse.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(0.30, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(150)),
            new CubicEase { EasingMode = EasingMode.EaseOut }));
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1150)),
            new CubicEase { EasingMode = EasingMode.EaseIn }));
        overlay.BeginAnimation(UIElement.OpacityProperty, pulse);
        return true;
    }

    // Set IsOpen=false on the Popup that contains `d`: navigator rows live in the popup's own (logical) tree, so a
    // combined logical-then-visual walk reaches the Popup where a visual-only walk would stop at the PopupRoot.
    private static void ClosePopupAround(DependencyObject? d)
    {
        for (var hops = 0; d is not null && hops < 64; hops++)
        {
            if (d is System.Windows.Controls.Primitives.Popup popup) { popup.IsOpen = false; return; }
            d = LogicalTreeHelper.GetParent(d) ?? VisualTreeHelper.GetParent(d);
        }
    }

    // First descendant FrameworkElement with the given Name (reaches a template-scoped element inside a container).
    private static FrameworkElement? FindDescendantByName(DependencyObject root, string name)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement fe && fe.Name == name) return fe;
            if (FindDescendantByName(child, name) is { } match) return match;
        }
        return null;
    }

    // ---------- copy / selection ----------

    private void OnOpenSubagentInspector(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not SubagentItem subagent) return;
        FindChatContext(sender as DependencyObject)?.OpenSubagent(subagent);
    }

    private void OnCloseSubagentInspector(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        FindChatContext(sender as DependencyObject)?.CloseSubagentInspector();
    }

    /// <summary>The roster lives inside a Popup, so its row carries the child as DataContext. Walk only as far as
    /// the shared roster template to recover the owning normal chat or exact Bridge pane.</summary>
    private static ChatViewModel? FindChatContext(DependencyObject? source)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is FrameworkElement { DataContext: ChatViewModel chat }) return chat;
        return null;
    }

    // Double-clicking a sent prompt selects the whole message (not just a word) so it's easy to copy.
    private void OnPromptDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && sender is TextBox tb)
        {
            tb.Focus();
            tb.SelectAll();
            e.Handled = true;
        }
    }

    // Stop this exact turn (including provider child agents), wait for its checkpoint to seal, rewind it, then put
    // the original text and attachments back into the composer that owns the row. UserItem.Owner is authoritative;
    // the ancestor list is used only to distinguish the normal composer from a Bridge pane's composer.
    private async void OnUndoPrompt(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button button || button.DataContext is not UserItem item) return;
        var list = FindParent<ListBox>(button);
        var owner = item.Owner ?? list?.DataContext as ChatViewModel;
        if (owner is null) return;
        var isMainComposer = ReferenceEquals(list, MsgList);
        var currentText = isMainComposer ? InputBox.Text : owner.Draft;
        var originalAttachments = item.Attachments ?? Array.Empty<Attachment>();
        var composerMatches = string.Equals(currentText, item.Text, StringComparison.Ordinal)
                              && owner.Attachments.Count == originalAttachments.Count
                              && owner.Attachments.Zip(originalAttachments).All(pair => ReferenceEquals(pair.First, pair.Second));
        var hasDifferentDraft = !composerMatches
                                && (!string.IsNullOrWhiteSpace(currentText) || owner.Attachments.Count > 0);

        if (hasDifferentDraft)
        {
            var fileText = item.UndoChangedFileCount == 0
                ? "No recorded files need restoring."
                : $"This will restore {item.UndoChangedFileCount} file{(item.UndoChangedFileCount == 1 ? "" : "s")} to their pre-prompt contents.";
            var answer = MessageBox.Show(this,
                $"{fileText}\n\nYour current unsent message and staged attachments will be replaced by this prompt. Continue?",
                "Undo prompt changes", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
        }

        if (owner.IsWorking || item.RollbackCheckpoint?.IsCompleted == false)
        {
            var pending = $"Stopping {owner.AgentDisplay} and its subagents, then rewinding…";
            if (isMainComposer) ShowComposerHint(pending);
            else ShowBridgeHint(pending);
        }

        var result = await owner.StopAndUndoPromptAsync(item);
        if (!result.Success)
        {
            MessageBox.Show(this, result.Message, "Couldn't undo prompt", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        owner.Attachments.Clear();
        foreach (var attachment in originalAttachments) owner.Attachments.Add(attachment);
        owner.ResetPromptHistoryNavigation();

        if (isMainComposer)
        {
            InputBox.Text = item.Text;
            InputBox.CaretIndex = InputBox.Text.Length;
            SlashPopup.IsOpen = false;
            InputBox.Focus();
            ShowComposerHint(result.Message);
            return;
        }

        owner.Draft = item.Text;
        _vm.SelectBridgePane(owner);
        _vm.NoteBridgeActivity();
        ShowBridgeHint(result.Message);
        _ = Dispatcher.BeginInvoke(() =>
        {
            var input = FindVisualDescendant<TextBox>(this, candidate =>
                ReferenceEquals(candidate.DataContext, owner)
                && System.Windows.Automation.AutomationProperties.GetName(candidate) == "Bridge message input");
            if (input is null) return;
            input.CaretIndex = input.Text.Length;
            input.Focus();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    // Right-click "Copy": copy just this message/tool block's text.
    private void OnCopyBlock(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ItemVm item)
            SetClipboard(BlockText(item));
    }

    // "View full code" on an edit/write card: open the Sublime-style viewer for that file (works in the main
    // chat and in bridge panes - the button's DataContext is the ToolItem either way).
    private void OnViewFullCode(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ToolItem tool)
            CodeViewerWindow.Open(tool, this);
    }

    // "Open in editor" on an edit/write card: hand the real file to whichever IDE the user picks, jumping
    // to the first edited line where that editor supports it. The picker only lists editors detected on
    // this machine, and remembers the last choice so it is one click thereafter.
    private void OnOpenInEditor(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ToolItem tool) return;
        var file = tool.FilePath;
        if (string.IsNullOrWhiteSpace(file) || !System.IO.File.Exists(file))
        {
            MessageBox.Show(this, "That file no longer exists on disk.", "Open in editor",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new UI.OpenInEditorDialog(file, tool.FirstEditedLine) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Chosen is null) return;

        if (!Services.ExternalEditorService.Open(dialog.Chosen, file, tool.FirstEditedLine))
        {
            MessageBox.Show(this, $"Couldn't launch {dialog.Chosen.Name}.", "Open in editor",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // Right-click "Copy whole conversation": the reliable way to grab text that spans a tool card,
    // since WPF can't drag-select across the separate message controls (a Read/Edit card in the middle
    // breaks a single selection). We copy the transcript wholesale instead.
    private void OnCopyConversation(object sender, RoutedEventArgs e)
    {
        var chat = ChatFrom(sender);
        if (chat is null) return;
        var parts = new List<string>();
        foreach (var it in chat.Items)
        {
            var line = TranscriptLine(it, chat.AgentDisplay);
            if (!string.IsNullOrEmpty(line)) parts.Add(line);
        }
        SetClipboard(string.Join("\n\n", parts));
    }

    // Walk up from the right-clicked block to the ChatViewModel that owns it (main chat or a bridge pane).
    private ChatViewModel? ChatFrom(object sender)
    {
        var mi = sender as FrameworkElement;
        var menu = mi?.Parent as ContextMenu
                   ?? (mi is not null ? ItemsControl.ItemsControlFromItemContainer(mi) as ContextMenu : null);
        for (DependencyObject? d = menu?.PlacementTarget; d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is FrameworkElement fe && fe.DataContext is ChatViewModel chat) return chat;
        return ActiveChatForWindow;
    }

    private static string BlockText(ItemVm item) => item switch
    {
        UserItem u => u.Text,
        TextItem t => t.Text,
        ThinkingItem th => th.Text,
        ToolItem tool => ToolText(tool),
        CompactToolGroupItem group => GroupText(group),
        DividerItem d => d.Label,
        BannerItem b => b.Text,
        _ => "",
    };

    // One transcript entry per top-level item; thinking/dividers/banners are dropped as noise.
    private static string TranscriptLine(ItemVm item, string agentDisplay) => item switch
    {
        UserItem u when !string.IsNullOrWhiteSpace(u.Text) => "You:\n" + u.Text.Trim(),
        TextItem t when !string.IsNullOrWhiteSpace(t.Text) => agentDisplay + ":\n" + t.Text.Trim(),
        ToolItem tool => ToolText(tool),
        CompactToolGroupItem group => GroupText(group),
        _ => "",
    };

    private static string GroupText(CompactToolGroupItem group) =>
        string.Join("\n\n", group.Tools.Select(ToolText));

    private static string ToolText(ToolItem t)
    {
        var head = string.IsNullOrWhiteSpace(t.Summary) ? t.Name : $"{t.Name}: {t.Summary}";
        return string.IsNullOrWhiteSpace(t.Result) ? $"[{head}]" : $"[{head}]\n{t.Result!.Trim()}";
    }

    private static void SetClipboard(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        // Clipboard access can throw COMException when another process briefly holds it - never let a copy crash.
        try { Clipboard.SetText(text); }
        catch { /* ignore transient clipboard lock */ }
    }

    private static T? Ctx<T>(object sender) where T : class => (sender as FrameworkElement)?.DataContext as T;

    // ---------- home / sidebar ----------

    private void OnNewChatHome(object sender, RoutedEventArgs e) => GoHomeForWindow();

    private void OnExpandSidebar(object sender, RoutedEventArgs e) => _vm.SidebarCollapsed = false;

    private void OnCollapseSidebar(object sender, RoutedEventArgs e) => _vm.SidebarCollapsed = true;

    // ---------- sidebar chat right-click menu (pin / fork / rename / copy / delete) ----------

    private void OnPinChat(object sender, RoutedEventArgs e)
    {
        if (Ctx<ChatViewModel>(sender) is { } chat) _vm.TogglePin(chat);
    }

    private void OnForkChatMenu(object sender, RoutedEventArgs e)
    {
        if (Ctx<ChatViewModel>(sender) is { CanFork: true, SessionId: { } sid } chat)
            NewChatForWindow(chat.Cwd, sid, fork: true, title: chat.Title + " (fork)", provider: chat.Provider);
    }

    private void OnRenameChat(object sender, RoutedEventArgs e)
    {
        if (Ctx<ChatViewModel>(sender) is not { } chat) return;
        var name = PromptForText("Rename chat", chat.Title);
        if (!string.IsNullOrWhiteSpace(name)) { chat.Title = name.Trim(); _vm.SaveSession(); }
    }

    private void OnCopyChatTranscript(object sender, RoutedEventArgs e)
    {
        if (Ctx<ChatViewModel>(sender) is not { } chat) return;
        var parts = new List<string>();
        foreach (var it in chat.Items)
        {
            var line = TranscriptLine(it, chat.AgentDisplay);
            if (!string.IsNullOrEmpty(line)) parts.Add(line);
        }
        SetClipboard(string.Join("\n\n", parts));
    }

    private void OnDeleteChatMenu(object sender, RoutedEventArgs e)
    {
        if (Ctx<ChatViewModel>(sender) is { } chat) _vm.CloseChat(chat);
    }

    /// <summary>VibeCode chrome for small owned dialogs: dark, rounded, draggable and consistent with the main shell.</summary>
    private Window CreateThemedDialog(string title, UIElement content, double cardWidth)
    {
        var win = new Window
        {
            Title = title,
            Owner = this,
            Width = cardWidth + 24, // PopupCard carries a 12px shadow margin on each side
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            ShowInTaskbar = false,
            Background = Brushes.Transparent,
            UseLayoutRounding = true,
            SnapsToDevicePixels = true,
        };

        var close = new Button
        {
            Content = "\uE711",
            Width = 42,
            Height = 38,
            ToolTip = "Close",
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        close.SetResourceReference(StyleProperty, "TitlebarClose");
        close.Click += (_, _) => win.Close();

        var titleRow = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(close, Dock.Right);
        titleRow.Children.Add(close);
        var brand = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(13, 0, 0, 0),
        };
        brand.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = (Brush)FindResource("Accent"),
            Margin = new Thickness(0, 0, 9, 0),
        });
        brand.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = (Brush)FindResource("Text"),
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        titleRow.Children.Add(brand);

        var header = new Border
        {
            Height = 39,
            Background = (Brush)FindResource("Bg1"),
            BorderBrush = (Brush)FindResource("BorderSoft"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(12, 12, 0, 0),
            Child = titleRow,
        };
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Left) win.DragMove();
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(header, 0);
        Grid.SetRow(content, 1);
        grid.Children.Add(header);
        grid.Children.Add(content);

        var shell = new Border { Child = grid };
        shell.SetResourceReference(StyleProperty, "PopupCard");
        win.Content = shell;
        win.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            win.Close();
        };
        return win;
    }

    /// <summary>Small themed single-line input dialog (used for Rename). Returns null on cancel.</summary>
    private string? PromptForText(string title, string initial)
    {
        var tb = new TextBox
        {
            Text = initial ?? "",
            Margin = new Thickness(16, 16, 16, 10),
            Padding = new Thickness(9, 7, 9, 7),
            FontSize = 13,
            Background = (Brush)FindResource("Bg2"),
            Foreground = (Brush)FindResource("Text"),
            BorderBrush = (Brush)FindResource("Border"),
            CaretBrush = (Brush)FindResource("Text"),
        };
        var ok = new Button { Content = "Rename", IsDefault = true, MinWidth = 92, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(14, 6, 14, 6) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 82, Padding = new Thickness(14, 6, 14, 6) };
        if (TryFindResource("PrimaryButton") is Style ps) ok.Style = ps;
        if (TryFindResource("GhostButton") is Style gs) cancel.Style = gs;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(16, 0, 16, 16) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel();
        panel.Children.Add(tb);
        panel.Children.Add(buttons);
        var win = CreateThemedDialog(title, panel, 400);
        ok.Click += (_, _) => { win.DialogResult = true; };
        win.Loaded += (_, _) => { tb.Focus(); tb.SelectAll(); };
        return win.ShowDialog() == true ? tb.Text : null;
    }

    private void OnStartChat(object sender, RoutedEventArgs e) => StartFromCwdBox();

    private void OnCwdKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) StartFromCwdBox(); }

    private void StartFromCwdBox()
    {
        // Canonicalise before anything downstream sees it. A pasted "C:/proj" or "C:\\proj" would otherwise become
        // this chat's cwd verbatim and then key its saved state, its project row and its hidden-projects entry by a
        // string no other code path ever produces - the same folder showing up twice, under two spellings.
        if (ShellDirectory(CwdBox.Text) is not { } path)
        {
            CwdHint.Text = "That folder doesn't exist - double-check the path.";
            CwdHint.Foreground = (System.Windows.Media.Brush)FindResource("Red");
            return;
        }
        CwdBox.Text = path;   // show the path actually being used, not the one that was pasted
        // Starting a chat also unhides the folder if it was previously "Hide this project"ed.
        NewChatForWindow(path, provider: AppSettings.Current.DefaultProvider);
    }

    private void OnProviderPick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string provider)
        {
            SetNewChatProvider(provider, persist: true);
            MaybeOfferProviderChat(provider);
        }
    }

    /// <summary>After the new-chat provider changes, the focused chat keeps its own AI — a Codex thread can't
    /// continue as Claude, so its model pill still shows GPT models, which reads as "the switch didn't work".
    /// Offer a fresh chat in the same project so the switch immediately yields the new provider's models.</summary>
    private void MaybeOfferProviderChat(string provider)
    {
        provider = provider?.Trim().ToLowerInvariant() switch
        {
            "codex" => "codex", "kimi" => "kimi", "grok" => "grok", _ => "claude",
        };
        if (ActiveChatForWindow is not { } chat
            || string.Equals(chat.Provider, provider, StringComparison.OrdinalIgnoreCase)) return;
        var name = provider switch { "codex" => "Codex", "kimi" => "Kimi", "grok" => "Grok", _ => "Claude" };
        if (MessageBox.Show(this,
                $"The open chat keeps running on {chat.AgentDisplay} with its own models. Start a new {name} chat in this project so your next message uses {name} models?",
                $"Start a {name} chat?", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            NewChatForWindow(chat.Cwd, provider: provider);
    }

    /// <summary>After the active AI changes, chats that are already open keep running the AI they started on — which
    /// is exactly wrong when the user switched BECAUSE that account was exhausted. Offer to bring them across.
    /// A conversation can't move between AIs, so unlike the same-provider account move this always asks first.</summary>
    private void OfferMoveOpenChatsToProvider(string provider)
    {
        var movable = _vm.ChatsMovableToProvider(provider);
        if (movable.Count == 0) return;
        var name = ProviderModelCatalog.DisplayName(provider);
        var one = movable.Count == 1;
        var subject = one
            ? $"1 open chat still runs on {movable[0].ProviderDisplay}."
            : $"{movable.Count} open chats still run on another AI.";
        if (MessageBox.Show(this,
                $"{subject}\n\nMove {(one ? "it" : "them")} to {name} now? Each chat restarts fresh in the same " +
                $"project — a conversation can't move between AIs, so the old thread stays in the project's history.",
                $"Move to {name}?", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        var (moved, failed) = _vm.MoveAllChatsToProvider(provider);
        if (failed > 0)
            MessageBox.Show(this,
                $"Moved {moved} chat{(moved == 1 ? "" : "s")} to {name}. {failed} couldn't be moved and still run the old AI.",
                "Some chats kept their AI", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>A model row belonging to another AI can never be sent to this session, so clicking it used to do
    /// nothing at all — the pill just kept showing the old AI's model, which reads as "switching didn't work".
    /// Offer the only thing that actually changes it: move this chat onto that AI.</summary>
    private void OfferMoveChatToProvider(ChatViewModel chat, string provider)
    {
        if (string.Equals(ProviderModelCatalog.Normalize(chat.Provider), ProviderModelCatalog.Normalize(provider),
                StringComparison.OrdinalIgnoreCase)) return;
        var name = ProviderModelCatalog.DisplayName(provider);
        if (MessageBox.Show(this,
                $"This chat runs on {chat.ProviderDisplay}, so {name} models can't be applied to it.\n\n" +
                $"Move it to {name}? The chat restarts fresh in the same project — a conversation can't move between " +
                $"AIs, so the {chat.ProviderDisplay} thread stays in the project's history.",
                $"Move this chat to {name}?", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            _vm.MoveChatToProvider(chat, provider);
    }

    private void SetNewChatProvider(string provider, bool persist)
    {
        provider = provider?.Trim().ToLowerInvariant() switch
        {
            "codex" => "codex", "kimi" => "kimi", "grok" => "grok", _ => "claude",
        };
        ClaudeProviderToggle.IsChecked = provider == "claude";
        CodexProviderToggle.IsChecked = provider == "codex";
        KimiProviderToggle.IsChecked = provider == "kimi";
        GrokProviderToggle.IsChecked = provider == "grok";
        CwdHint.Text = provider switch
        {
            "codex" => "AGENTS.md, skills, plugins and MCP servers load through VibeCode's managed Codex runtime.",
            "kimi" => "AGENTS.md, skills, MCP servers and Kimi's project context load through the installed Kimi Code CLI.",
            "grok" => "AGENTS.md, skills, MCP servers and Grok's project context load through the reviewed Grok ACP runtime.",
            _ => "CLAUDE.md, skills and MCP servers load exactly like the CLI.",
        };
        CwdHint.Foreground = (Brush)FindResource("Faint");
        if (persist && AppSettings.Current.DefaultProvider != provider)
        {
            AppSettings.Current.DefaultProvider = provider;
            AppSettings.Current.Save();
        }
        _vm.RefreshProviderPresentation();
    }

    private void OnBrowseFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a project folder" };
        if (ShellDirectory(CwdBox.Text) is { } start) dlg.InitialDirectory = start;

        bool? picked;
        try
        {
            picked = dlg.ShowDialog(this);
        }
        catch (ArgumentException)
        {
            // The shell parses InitialDirectory itself and can still reject something ShellDirectory let through;
            // failing to open the picker at all is far worse than starting it in the default folder.
            dlg.InitialDirectory = "";
            picked = dlg.ShowDialog(this);
        }

        if (picked != true) return;
        CwdBox.Text = dlg.FolderName;
        // Persist immediately so the chip appears even if the user leaves home without clicking New Chat yet.
        _vm.RememberDirectorySelection(dlg.FolderName);
        SetNewChatProvider(AppSettings.Current.DefaultProvider, persist: false);
    }

    /// <summary>
    /// A directory path the shell can actually parse, or null if there is no usable starting folder.
    /// <para>
    /// <see cref="Directory.Exists"/> is not a strong enough guard on its own: it happily accepts
    /// <c>C:/Users/me</c>, <c>.\src</c>, <c>C:\\Users\\me</c> and <c>\\?\C:\Users\me</c>, all of which make
    /// <c>OpenFolderDialog.ShowDialog</c> throw "Value does not fall within the expected range" (the shell's
    /// E_INVALIDARG). Paths in those shapes arrive by paste all the time - from a bash prompt, a JSON file,
    /// a log line. Normalising first turns every one of them into a path the picker opens on.
    /// </para>
    /// </summary>
    private static string? ShellDirectory(string? raw)
    {
        var text = raw?.Trim().Trim('"') ?? "";
        if (text.Length == 0) return null;
        try
        {
            var full = Path.GetFullPath(text);          // fixes / separators, ".\", and doubled separators
            if (full.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) full = @"\\" + full[8..];
            else if (full.StartsWith(@"\\?\", StringComparison.Ordinal)) full = full[4..];
            return Directory.Exists(full) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException or IOException or System.Security.SecurityException)
        {
            return null;                               // malformed or unreachable: just start in the default folder
        }
    }

    private void OnChipClick(object sender, RoutedEventArgs e)
    {
        if (Ctx<ProjectVm>(sender) is not { } p) return;
        CwdBox.Text = p.Cwd;
        _vm.RememberDirectorySelection(p.Cwd);
    }

    private void OnOpenChat(object sender, RoutedEventArgs e)
    {
        if (Ctx<ChatViewModel>(sender) is { } chat) OpenChatForWindow(chat);
    }

    private void OnCloseChat(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (Ctx<ChatViewModel>(sender) is { } chat) _vm.CloseChat(chat);
    }

    private void OnCloseActiveChat(object sender, RoutedEventArgs e)
    {
        if (ActiveChatForWindow is { } chat) _vm.CloseChat(chat);
    }

    private void OnResumeSession(object sender, RoutedEventArgs e)
    {
        if (Ctx<SessionEntry>(sender) is { } session) ResumeSessionForWindow(session);
    }

    /// <summary>Home screen: bring a dormant bridge back with every agent it had.</summary>
    private void OnResumeSavedBridge(object sender, RoutedEventArgs e)
    {
        if (Ctx<SavedBridgeVm>(sender) is not { } entry) return;
        if (!_vm.ResumeSavedBridge(entry, activatePrimary: !IsDoubleSessionCompanion))
            ShowComposerHint("That bridge's agents could not be resumed - their sessions may no longer exist.");
    }

    private void OnNewChatInProject(object sender, RoutedEventArgs e)
    {
        if (Ctx<ProjectVm>(sender) is { } p && Directory.Exists(p.Cwd)) NewChatForWindow(p.Cwd);
    }

    private void OnForkChat(object sender, RoutedEventArgs e)
    {
        if (ActiveChatForWindow is { CanFork: true, SessionId: { } sid } chat)
            NewChatForWindow(chat.Cwd, sid, fork: true, title: chat.Title + " (fork)", provider: chat.Provider);
    }

    private async void OnSignIn(object sender, RoutedEventArgs e)
    {
        if (ActiveChatForWindow is { IsCodex: true } codexChat)
        {
            await SignInToCodexAsync();
            if (_vm.IsCodexSignedIn) codexChat.RetryAfterSignIn();
            return;
        }
        if (ActiveChatForWindow is { IsKimi: true } kimiChat)
        {
            await ManageKimiAccountAsync(selectIfReady: true);
            if (_vm.IsKimiSignedIn) kimiChat.RetryAfterSignIn();
            return;
        }
        if (ActiveChatForWindow is { IsGrok: true } grokChat)
        {
            await ManageGrokAccountAsync(selectIfReady: true);
            if (_vm.IsGrokSignedIn) grokChat.RetryAfterSignIn();
            return;
        }
        try
        {
            var cli = Protocol.ClaudeSession.ResolveCliPath();
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/k \"\"{cli}\" /login\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Open a terminal and run:  claude /login\n\n{ex.Message}", "Sign in to Claude");
        }
    }

    // ---------- composer ----------

    private void OnSendClick(object sender, RoutedEventArgs e)
    {
        // One button: it acts as Stop/Interrupt while a turn is running, and as Send otherwise (incl. during startup).
        if (ActiveChatForWindow?.CanInterrupt == true) ActiveChatForWindow.Interrupt();
        else SendCurrent();
    }

    private void SendCurrent()
    {
        var chat = ActiveChatForWindow;
        if (chat is null) return;
        var text = InputBox.Text.Trim();
        var atts = chat.Attachments.ToList();
        if (text.Length == 0 && atts.Count == 0) return;
        if (!chat.Send(text, atts)) return;   // session gone / nothing sent - keep the input + staged attachments
        chat.Attachments.Clear();
        InputBox.Clear();
        SlashPopup.IsOpen = false;
        _stickToBottom = true;
        ScrollMsgToBottom();
    }

    // ---------- dictation (offline speech-to-text, push-to-talk) ----------

    // Who owns the running capture. Identified by the OWNING VIEW MODEL, not by Button reference: the bridge
    // ItemsControl regenerates containers when a Claude is added/removed, which used to strand a recording mic with no
    // way to stop it. The main composer gets an explicit sentinel so that null unambiguously means "owner unknown"
    // (encoding the main composer as null made it indistinguishable from an orphan). Only one capture runs at a time.
    //
    // OWNERSHIP RULE (the other half of the one written at the top of SpeechService's capture region — keep them
    // identical): SpeechService issues a token per capture. _micToken is the token of the capture THIS window's
    // bookkeeping (_micOwner/_micActive/_micButton) describes. Every mutation of that bookkeeping, and every mic glyph
    // recolor, must go through ReleaseMicIfOwned(token) / a `_micToken == token` check. A continuation that was
    // disowned (ForceReset, or simply a newer recording) must touch NOTHING — otherwise it wipes the ownership of a
    // LIVE recording and the next click silently discards the user's audio.
    private static readonly object MainComposerOwner = new();
    private object? _micOwner;
    private bool _micActive;
    private int _micToken;        // 0 = we own no capture
    private Button? _micButton;   // the recording button, for the glyph recolor only
    private DateTime _micBusySince;
    // How long a non-Recording busy state may last before a click is allowed to force it back to Idle. Without this,
    // one hung transcription makes every mic in the app permanently unusable. The first-use model download gets its
    // own, far larger budget: ~142 MB over a slow link legitimately exceeds the transcription budget, and force-
    // resetting it would disown a download that is still running perfectly well.
    private static readonly TimeSpan MicWedgeTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan MicDownloadTimeout = TimeSpan.FromMinutes(30);

    private void ClearMicOwner() { _micButton = null; _micActive = false; _micOwner = null; _micToken = 0; }

    /// <summary>Release this window's mic bookkeeping and reset the glyph — but ONLY if <paramref name="token"/> is
    /// still the capture we describe. Returns false when a newer capture has taken over, in which case the caller is a
    /// stale continuation and must do nothing at all (no visual, no hint, no insert).</summary>
    private bool ReleaseMicIfOwned(int token, Button? btn)
    {
        if (token == 0 || _micToken != token) return false;
        SetMicVisual(btn, SpeechState.Idle);
        if (!ReferenceEquals(btn, _micButton)) SetMicVisual(_micButton, SpeechState.Idle);
        ClearMicOwner();
        return true;
    }

    private void OnMicClick(object sender, RoutedEventArgs e)
        => ToggleMic(sender as Button, null, text => InsertAtCaret(InputBox, text), ShowComposerHint);

    private void OnBridgeMicClick(object sender, RoutedEventArgs e)
    {
        // SpeechService is process-global. Keep exactly one token/owner ledger even though the Bridge has two visual
        // trees; this also lets a recording started on the companion finish after a hot-unplug moves its pane home.
        if (_isBridgeMonitor && _primaryWindow is { } primary)
        {
            primary.OnBridgeMicClick(sender, e);
            return;
        }
        if (Ctx<ChatViewModel>(sender) is { } vm)
        {
            _vm.SelectBridgePane(vm);
            ToggleMic(sender as Button, vm, text => AppendDraft(vm, text), ShowBridgeHint);
        }
    }

    // Click once to start recording; click the same button again to stop, transcribe, and drop the text into the
    // composer. A click on any OTHER mic while one is busy is ignored (single capture at a time). `hint` surfaces
    // status/errors where the user is actually looking — the composer hint for the main mic, the bridge header for a
    // pane. Without a bridge-visible hint, a pane dictation error (or the one-time ~142 MB model download) is silent,
    // which reads as "the mic does nothing". Every failure/edge now leaves a visible message.
    private async void ToggleMic(Button? btn, ChatViewModel? owner, Action<string> insert, Action<string> hint)
    {
        if (btn is null) return;
        var svc = SpeechService.Instance;
        var key = (object?)owner ?? MainComposerOwner;   // never null: null is reserved for "unknown owner"

        if (_micActive && ReferenceEquals(key, _micOwner) && svc.State == SpeechState.Recording && svc.Owns(_micToken))
        {
            var token = _micToken;                       // the capture we are stopping; everything below is gated on it
            _micButton = btn;   // the container may have been regenerated since we started; recolor the CURRENT button
            _micBusySince = DateTime.UtcNow;             // restart the wedge clock for the transcription phase
            SetMicVisual(btn, SpeechState.Transcribing);
            hint(svc.ModelReady
                ? "Transcribing…"
                : "Downloading the speech model — ~142 MB, one time. Transcribing right after…");
            var text = "";
            try { text = await svc.StopAndTranscribeAsync(token); }
            catch (Exception ex)
            {
                if (ReleaseMicIfOwned(token, btn)) hint("Speech-to-text failed: " + ex.Message);
                return;
            }
            // A stale continuation (ForceReset + a newer recording already running) owns nothing: releasing here would
            // wipe the LIVE capture's ownership and kill its red glyph while the mic is still hot. Bail silently.
            if (!ReleaseMicIfOwned(token, btn)) return;
            // Don't blank the hint on success: a click that arrived while this capture was transcribing has already
            // posted its own "Still transcribing…", and clearing here would wipe it, making that click look ignored.
            // The hint's own auto-clear timer retires whichever message is actually current.
            if (!string.IsNullOrWhiteSpace(text)) insert(text);
            else hint("No speech detected — try again, a bit closer to the mic.");
            return;
        }

        if (svc.IsBusy)
        {
            // The owner is gone (unknown, or its pane was removed mid-recording) => the capture can never be stopped by
            // a click. Recover rather than leaving the mic stuck forever. A live owner gets a visible explanation
            // instead of the old silent return, which read as "the mic button does nothing".
            var orphaned = !_micActive
                           || !svc.Owns(_micToken)                                       // our bookkeeping is stale
                           || _micOwner is null                                          // owner truly unknown
                           || (_micOwner is ChatViewModel o && !_vm.BridgePanes.Contains(o));   // its pane was removed

            if (svc.State == SpeechState.Recording)
            {
                // Only a live capture may be cancelled. Cancelling a running transcription would let that call's own
                // completion reset the state of the recording we are about to start (and leak its WaveInEvent).
                if (!orphaned)
                {
                    hint(ReferenceEquals(key, _micOwner) ? "Still transcribing…" : "Another pane is already listening.");
                    return;
                }
                svc.CancelRecording();
            }
            else
            {
                // The first-use model download is legitimately slow — judge it against its own budget, not the
                // transcription one, or a perfectly healthy ~142 MB download gets force-reset at 90 s (and its
                // still-running successor collides with it).
                var downloading = svc.State == SpeechState.Downloading;
                var budget = downloading ? MicDownloadTimeout : MicWedgeTimeout;
                if (DateTime.UtcNow - _micBusySince > budget)
                {
                    svc.ForceReset();   // never finished — the only escape from a dead mic
                }
                else
                {
                    // Busy and not wedged yet: never restart on top of it, just say so.
                    hint(downloading
                        ? "Still downloading the speech model — ~142 MB, one time. Transcribing right after…"
                        : "Still transcribing…");
                    return;
                }
            }
            SetMicVisual(_micButton, SpeechState.Idle);
            ClearMicOwner();
        }

        if (!svc.StartRecording(out var err, out var newToken))
        {
            hint(err ?? "Microphone unavailable.");
            return;
        }
        _micButton = btn;
        _micOwner = key;
        _micActive = true;
        _micToken = newToken;          // from now on, only a continuation holding THIS token may touch the fields above
        _micBusySince = DateTime.UtcNow;
        SetMicVisual(btn, SpeechState.Recording);
        hint("Listening — click the mic again to insert.");
    }

    // A transient status line for the bridge (dictation feedback). The bridge overlay has no composer hint of its own,
    // so mic status/errors are shown in the bridge header instead. An empty message clears it immediately.
    private System.Windows.Threading.DispatcherTimer? _bridgeHintTimer;
    private void ShowBridgeHint(string message)
    {
        _vm.BridgeHint = message;
        _bridgeHintTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _bridgeHintTimer.Stop();
        if (string.IsNullOrEmpty(message)) return;                    // explicit clear — nothing to schedule
        _bridgeHintTimer.Tick -= OnBridgeHintTick;
        _bridgeHintTimer.Tick += OnBridgeHintTick;                    // (subscribe once; the -= above prevents stacking)
        _bridgeHintTimer.Start();
    }
    private void OnBridgeHintTick(object? s, EventArgs e) { _bridgeHintTimer?.Stop(); _vm.BridgeHint = ""; }

    // Recolor the mic glyph for the current state (red while listening, accent while working, neutral when idle).
    private void SetMicVisual(Button? btn, SpeechState state)
    {
        if (btn?.Content is not TextBlock tb) return;
        switch (state)
        {
            case SpeechState.Recording:
                tb.Foreground = (Brush)FindResource("Red");
                btn.ToolTip = "Listening — click to stop & insert";
                break;
            case SpeechState.Transcribing:
            case SpeechState.Downloading:
                tb.Foreground = (Brush)FindResource("Accent");
                btn.ToolTip = state == SpeechState.Downloading ? "Downloading speech model…" : "Transcribing…";
                break;
            default:
                tb.ClearValue(TextBlock.ForegroundProperty);
                btn.ToolTip = "Dictate (speech to text)";
                break;
        }
    }

    // Insert transcribed text at the composer caret (replacing any selection), spacing it off an adjacent word.
    private static void InsertAtCaret(TextBox box, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var at = box.SelectionLength > 0 ? box.SelectionStart : box.CaretIndex;
        var needLead = at > 0 && at <= box.Text.Length && !char.IsWhiteSpace(box.Text[at - 1]);
        var ins = (needLead ? " " : "") + text;
        box.SelectedText = ins;
        box.CaretIndex = at + ins.Length;
        box.Focus();
    }

    // Append transcribed text to a bridge pane's draft (its composer is bound to ChatViewModel.Draft).
    private static void AppendDraft(ChatViewModel vm, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var cur = vm.Draft ?? "";
        var needLead = cur.Length > 0 && !char.IsWhiteSpace(cur[^1]);
        vm.Draft = cur + (needLead ? " " : "") + text;
    }

    // ---------- centered, high-density account manager ----------

    private void OnAccountChecked(object sender, RoutedEventArgs e)
    {
        EnsureAccountManagerViews();
        _vm.RefreshAccounts();
        _vm.ApplyCodexAccounts(CodexAccountService.Instance.List());
        _vm.ApplyGrokAccounts(GrokAccountService.Instance.List());
        KickAccountUsage();
        KickCodexAccountRefresh(forceRefresh: false); // refresh the OpenAI row's plan + rolling usage on every open
        KickKimiAccountRefresh();                     // refresh installed/login state without creating a chat
        KickGrokAccountRefresh();                     // refresh bundled CLI and xAI login state
        SelectAccountManagerProvider(AppSettings.Current.DefaultProvider);
        RefreshAccountManagerViews();
        AccountManagerOverlay.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!IsAccountManagerOpen) return;
            AccountSearchBox.Focus();
            AccountSearchBox.SelectAll();
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private bool IsAccountManagerOpen => AccountManagerOverlay.Visibility == Visibility.Visible;

    /// <summary>Hides the overlay and releases the sidebar toggle that opened it. Collapsing before unchecking
    /// keeps the toggle's Unchecked handler from recursing back in here.</summary>
    private void CloseAccountManager()
    {
        if (!IsAccountManagerOpen) return;
        AccountManagerOverlay.Visibility = Visibility.Collapsed;
        AccountToggle.IsChecked = false;
    }

    private void OnAccountUnchecked(object sender, RoutedEventArgs e) => CloseAccountManager();

    private void OnCloseAccountManager(object sender, RoutedEventArgs e) => CloseAccountManager();

    private void OnAccountManagerBackdropClick(object sender, MouseButtonEventArgs e)
    {
        CloseAccountManager();
        e.Handled = true;
    }

    private void OnAccountProviderChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateAccountManagerProvider();

    private void OnAccountSearchChanged(object sender, TextChangedEventArgs e)
    {
        // TextChanged can fire during InitializeComponent before the controls below the search box exist.
        // The placeholder is NOT touched here - it binds to text + focus in XAML, and assigning Visibility
        // in code would blow that binding away and strand the hint under the caret again.
        if (AccountSearchClearButton is null) return;
        var hasText = !string.IsNullOrWhiteSpace(AccountSearchBox.Text);
        AccountSearchClearButton.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;
        RefreshAccountManagerViews();
    }

    private void OnClearAccountSearch(object sender, RoutedEventArgs e)
    {
        AccountSearchBox.Clear();
        AccountSearchBox.Focus();
    }

    private void OnAccountStatusChanged(object sender, SelectionChangedEventArgs e) =>
        RefreshAccountManagerViews();

    private void OnAccountSortChanged(object sender, SelectionChangedEventArgs e) =>
        RefreshAccountManagerViews();

    private void OnResetAccountFilters(object sender, RoutedEventArgs e)
    {
        AccountSearchBox.Clear();
        AccountStatusCombo.SelectedIndex = 0;
        AccountSortCombo.SelectedIndex = 0;
        RefreshAccountManagerViews();
        AccountSearchBox.Focus();
    }

    /// <summary>The provider picker is a filter first: choosing a provider reveals its accounts, and choosing an
    /// account is what changes the provider used by new chats. That keeps browsing the manager side-effect free.</summary>
    private string SelectedAccountManagerProvider =>
        (AccountProviderCombo.SelectedItem as ComboBoxItem)?.Tag as string
        ?? AppSettings.Current.DefaultProvider;

    private void SelectAccountManagerProvider(string? provider)
    {
        var index = provider?.ToLowerInvariant() switch
        {
            "claude" => 1,
            "codex" => 2,
            "kimi" => 3,
            "grok" => 4,
            _ => 0,
        };
        if (AccountProviderCombo.SelectedIndex != index)
            AccountProviderCombo.SelectedIndex = index;
        else
            UpdateAccountManagerProvider();
    }

    private void UpdateAccountManagerProvider()
    {
        // SelectionChanged can fire while InitializeComponent is still constructing the controls below the picker.
        if (AllAccountsPanel is null || ClaudeAccountsPanel is null || CodexAccountsPanel is null || KimiAccountsPanel is null
            || GrokAccountsPanel is null
            || AccountManagerAddButton is null || AccountManagerAddLabel is null
            || AccountManagerFooterHint is null || AccountSortCombo is null)
            return;

        var provider = SelectedAccountManagerProvider;
        AllAccountsPanel.Visibility = provider == "all" ? Visibility.Visible : Visibility.Collapsed;
        ClaudeAccountsPanel.Visibility = provider == "claude" ? Visibility.Visible : Visibility.Collapsed;
        CodexAccountsPanel.Visibility = provider == "codex" ? Visibility.Visible : Visibility.Collapsed;
        KimiAccountsPanel.Visibility = provider == "kimi" ? Visibility.Visible : Visibility.Collapsed;
        GrokAccountsPanel.Visibility = provider == "grok" ? Visibility.Visible : Visibility.Collapsed;
        AccountSortCombo.IsEnabled = provider != "kimi";
        AccountManagerAddButton.IsEnabled = provider != "all";

        (AccountManagerAddLabel.Text, AccountManagerFooterHint.Text) = provider switch
        {
            "all" => ("Choose a provider to add", "Showing every account. Select a provider above before adding another."),
            "codex" => ("Add Codex account", "Click to switch · right-click to test or remove · saved date appears at right."),
            "kimi" => ("Manage Kimi account", "Kimi keeps one shared OAuth login in the Kimi Code CLI."),
            "grok" => ("Add Grok account", "Click to switch · right-click to test, sign out, or remove · each login stays isolated."),
            _ => ("Add Claude account", "Click to switch · right-click to test or remove · saved date appears at right."),
        };
        RefreshAccountManagerViews();
    }

    private void EnsureAccountManagerViews()
    {
        if (_allAccountManagerView is not null || AllAccountList is null || ClaudeAccountList is null
            || CodexAccountList is null || GrokAccountList is null) return;

        _allAccountManagerView = new ListCollectionView((IList)_vm.AllAccounts);
        _claudeAccountManagerView = new ListCollectionView((IList)_vm.Accounts);
        _codexAccountManagerView = new ListCollectionView((IList)_vm.CodexAccounts);
        _grokAccountManagerView = new ListCollectionView((IList)_vm.GrokAccounts);
        AllAccountList.ItemsSource = _allAccountManagerView;
        ClaudeAccountList.ItemsSource = _claudeAccountManagerView;
        CodexAccountList.ItemsSource = _codexAccountManagerView;
        GrokAccountList.ItemsSource = _grokAccountManagerView;
    }

    private void OnAccountManagerCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        ScheduleAccountManagerRefresh();

    private void ScheduleAccountManagerRefresh()
    {
        if (_isBridgeMonitor || _isClosing || _accountManagerRefreshQueued || Dispatcher.HasShutdownStarted) return;
        _accountManagerRefreshQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _accountManagerRefreshQueued = false;
            if (!_isClosing) RefreshAccountManagerViews();
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private string SelectedAccountManagerStatus =>
        (AccountStatusCombo?.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";

    private string SelectedAccountManagerSort =>
        (AccountSortCombo?.SelectedItem as ComboBoxItem)?.Tag as string ?? "active";

    private void RefreshAccountManagerViews()
    {
        EnsureAccountManagerViews();
        if (_allAccountManagerView is null || _claudeAccountManagerView is null
            || _codexAccountManagerView is null || _grokAccountManagerView is null
            || AccountSearchBox is null || AccountStatusCombo is null || AccountSortCombo is null)
            return;

        var comparer = new AccountManagerComparer(SelectedAccountManagerSort);
        foreach (var view in new[]
                 {
                     _allAccountManagerView, _claudeAccountManagerView, _codexAccountManagerView, _grokAccountManagerView,
                 })
        {
            using (view.DeferRefresh())
            {
                view.Filter = AccountMatchesManagerFilters;
                view.CustomSort = comparer;
            }
        }

        UpdateAccountManagerResultState();
    }

    private bool AccountMatchesManagerFilters(object item)
    {
        if (!TryDescribeAccount(item, out var account) || !MatchesAccountStatus(account)) return false;
        var query = AccountSearchBox?.Text?.Trim();
        if (string.IsNullOrEmpty(query)) return true;

        // Hide-emails is a privacy boundary, not only a presentation preference: hidden addresses are deliberately
        // excluded from the searchable corpus so the result set cannot reveal them by inference.
        var searchableEmail = AppSettings.Current.HideEmails ? "" : account.Email;
        var haystack = string.Join('\n', account.Label, account.ProviderLine, account.Plan,
            account.UsageDisplay, account.Id, searchableEmail);
        return query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(term => haystack.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private bool MatchesAccountStatus(AccountManagerDescriptor account) => SelectedAccountManagerStatus switch
    {
        "ready" => account.Usable && !account.AtLimit,
        "at-limit" => account.AtLimit,
        "sign-in" => !account.Usable,
        "selected" => account.IsSelected,
        _ => true,
    };

    private bool KimiMatchesManagerFilters()
    {
        var account = new AccountManagerDescriptor(
            "kimi", _vm.KimiAccountLabel, _vm.KimiAccountSub, "Kimi Code", _vm.KimiAccountUsage, "",
            _vm.IsKimiInstalled && _vm.IsKimiSignedIn, false, _vm.IsKimiSelected, _vm.IsKimiSelected,
            null, DateTime.MinValue);
        if (!MatchesAccountStatus(account)) return false;

        var query = AccountSearchBox?.Text?.Trim();
        if (string.IsNullOrEmpty(query)) return true;
        var haystack = string.Join('\n', account.Label, account.ProviderLine, account.Plan, account.UsageDisplay);
        return query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(term => haystack.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private bool GrokMatchesManagerFilters()
    {
        var account = new AccountManagerDescriptor(
            "grok", _vm.GrokAccountLabel, _vm.GrokAccountSub, "Grok", _vm.GrokAccountUsage, "",
            _vm.IsGrokInstalled && _vm.IsGrokSignedIn, false, _vm.IsGrokSelected, _vm.IsGrokSelected,
            null, DateTime.MinValue);
        if (!MatchesAccountStatus(account)) return false;

        var query = AccountSearchBox?.Text?.Trim();
        if (string.IsNullOrEmpty(query)) return true;
        var haystack = string.Join('\n', account.Label, account.ProviderLine, account.Plan, account.UsageDisplay,
            Grok45Preset.NormalDisplayName);
        return query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(term => haystack.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private void UpdateAccountManagerResultState()
    {
        if (AccountManagerResultsText is null || AccountResetFiltersButton is null
            || AllAccountEmptyText is null || ClaudeAccountEmptyText is null || CodexAccountEmptyText is null
            || KimiAccountRow is null || KimiAccountEmptyText is null
            || GrokAccountEmptyText is null)
            return;

        var provider = SelectedAccountManagerProvider;
        var allVisible = _allAccountManagerView?.Cast<object>().Count() ?? 0;
        var claudeVisible = _claudeAccountManagerView?.Cast<object>().Count() ?? 0;
        var codexVisible = _codexAccountManagerView?.Cast<object>().Count() ?? 0;
        var kimiVisible = KimiMatchesManagerFilters();
        var grokVisible = _grokAccountManagerView?.Cast<object>().Count() ?? 0;
        var (visible, total) = provider switch
        {
            "all" => (allVisible, _vm.AllAccounts.Count),
            "codex" => (codexVisible, _vm.CodexAccounts.Count),
            "kimi" => (kimiVisible ? 1 : 0, 1),
            "grok" => (grokVisible, _vm.GrokAccounts.Count),
            _ => (claudeVisible, _vm.Accounts.Count),
        };

        AccountManagerResultsText.Text = visible == total
            ? $"{total:N0} account{(total == 1 ? "" : "s")}"
            : $"{visible:N0} of {total:N0}";

        var hasSearchOrStatusFilter = !string.IsNullOrWhiteSpace(AccountSearchBox.Text)
                                      || SelectedAccountManagerStatus != "all";
        AllAccountEmptyText.Text = _vm.AllAccounts.Count == 0
            ? "No accounts are available yet. Choose a provider below to add one."
            : "No accounts match the current search or status filter.";
        ClaudeAccountEmptyText.Text = _vm.Accounts.Count == 0
            ? "No Claude accounts are saved yet. Add one below to get started."
            : "No Claude accounts match the current search or status filter.";
        CodexAccountEmptyText.Text = _vm.CodexAccounts.Count == 0
            ? "No Codex accounts are saved yet. Add one below to get started."
            : "No Codex accounts match the current search or status filter.";
        AllAccountEmptyText.Visibility = allVisible == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClaudeAccountEmptyText.Visibility = claudeVisible == 0 ? Visibility.Visible : Visibility.Collapsed;
        CodexAccountEmptyText.Visibility = codexVisible == 0 ? Visibility.Visible : Visibility.Collapsed;
        KimiAccountRow.Visibility = kimiVisible ? Visibility.Visible : Visibility.Collapsed;
        KimiAccountEmptyText.Visibility = kimiVisible ? Visibility.Collapsed : Visibility.Visible;
        GrokAccountEmptyText.Text = _vm.GrokAccounts.Count == 0
            ? "No Grok accounts are saved yet. Add one below to get started."
            : "No Grok accounts match the current search or status filter.";
        GrokAccountEmptyText.Visibility = grokVisible == 0 ? Visibility.Visible : Visibility.Collapsed;
        AccountResetFiltersButton.Visibility = hasSearchOrStatusFilter || SelectedAccountManagerSort != "active"
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static bool TryDescribeAccount(object? item, out AccountManagerDescriptor account)
    {
        switch (item)
        {
            case AccountInfo claude:
                account = new AccountManagerDescriptor(
                    claude.Id, claude.Label, claude.ProviderLine, claude.PlanDisplay, claude.UsageDisplay,
                    claude.Email, claude.Usable, claude.AtLimit, claude.IsVibeCodeSelected, claude.IsCurrent,
                    AccountUsageScore(claude.SessionPercent, claude.WeekPercent, claude.AtLimit), claude.SavedAt);
                return true;
            case CodexAccountInfo codex:
                account = new AccountManagerDescriptor(
                    codex.Id, codex.Label, codex.ProviderLine, codex.PlanDisplay, codex.UsageDisplay,
                    codex.Email, codex.Usable, codex.AtLimit, codex.IsVibeCodeSelected, codex.IsCurrent,
                    AccountUsageScore(codex.SessionPercent, codex.WeekPercent, codex.AtLimit), codex.SavedAt);
                return true;
            case GrokAccountInfo grok:
                account = new AccountManagerDescriptor(
                    grok.Id, grok.Label, grok.ProviderLine, grok.PlanDisplay, grok.UsageDisplay,
                    grok.Email, grok.Usable, grok.AtLimit, grok.IsVibeCodeSelected, grok.IsCurrent,
                    grok.UsagePercent ?? (grok.AtLimit ? 100 : null), grok.SavedAt);
                return true;
            case KimiAccountEntry kimi:
                account = new AccountManagerDescriptor(
                    "kimi", kimi.Label, kimi.ProviderLine, "Kimi Code", kimi.UsageDisplay,
                    "", kimi.Usable, false, kimi.IsVibeCodeSelected, kimi.IsVibeCodeSelected,
                    null, DateTime.MinValue);
                return true;
            default:
                account = default;
                return false;
        }
    }

    private static int? MaxUsage(int? session, int? week) => (session, week) switch
    {
        ({ } s, { } w) => Math.Max(s, w),
        ({ } s, null) => s,
        (null, { } w) => w,
        _ => null,
    };

    private static int? AccountUsageScore(int? session, int? week, bool atLimit) =>
        MaxUsage(session, week) ?? (atLimit ? 100 : null);

    private readonly record struct AccountManagerDescriptor(
        string Id,
        string Label,
        string ProviderLine,
        string Plan,
        string UsageDisplay,
        string Email,
        bool Usable,
        bool AtLimit,
        bool IsSelected,
        bool IsCurrent,
        int? UsagePercent,
        DateTime SavedAt);

    private sealed class AccountManagerComparer : IComparer
    {
        private readonly string _mode;
        public AccountManagerComparer(string mode) => _mode = mode;

        public int Compare(object? x, object? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (!TryDescribeAccount(x, out var left)) return 1;
            if (!TryDescribeAccount(y, out var right)) return -1;

            var result = _mode switch
            {
                "usage-low" => CompareUsageAccounts(left, right, descending: false),
                "usage-high" => CompareUsageAccounts(left, right, descending: true),
                "name" => StringComparer.CurrentCultureIgnoreCase.Compare(left.Label, right.Label),
                "newest" => right.SavedAt.CompareTo(left.SavedAt),
                "oldest" => left.SavedAt.CompareTo(right.SavedAt),
                "attention" => CompareAttention(left, right),
                _ => CompareActive(left, right),
            };
            if (result != 0) return result;
            result = StringComparer.CurrentCultureIgnoreCase.Compare(left.Label, right.Label);
            return result != 0 ? result : StringComparer.OrdinalIgnoreCase.Compare(left.Id, right.Id);
        }

        private static int CompareActive(AccountManagerDescriptor left, AccountManagerDescriptor right)
        {
            var selected = right.IsSelected.CompareTo(left.IsSelected);
            return selected != 0 ? selected : right.IsCurrent.CompareTo(left.IsCurrent);
        }

        private static int CompareAttention(AccountManagerDescriptor left, AccountManagerDescriptor right)
        {
            static int Rank(AccountManagerDescriptor value) => !value.Usable ? 0 : value.AtLimit ? 1 : 2;
            var rank = Rank(left).CompareTo(Rank(right));
            return rank != 0 ? rank : CompareUsage(left.UsagePercent, right.UsagePercent, descending: true);
        }

        private static int CompareUsageAccounts(AccountManagerDescriptor left, AccountManagerDescriptor right, bool descending)
        {
            // A signed-out account with an old low percentage is not actually "available". Keep usable accounts first
            // in either usage direction, then compare their measured utilization.
            var usable = right.Usable.CompareTo(left.Usable);
            return usable != 0 ? usable : CompareUsage(left.UsagePercent, right.UsagePercent, descending);
        }

        private static int CompareUsage(int? left, int? right, bool descending)
        {
            if (left is null && right is null) return 0;
            if (left is null) return 1;  // unknown usage always follows measured accounts
            if (right is null) return -1;
            return descending ? right.Value.CompareTo(left.Value) : left.Value.CompareTo(right.Value);
        }
    }

    private void OnSwitchAccount(object sender, RoutedEventArgs e)
    {
        CloseAccountManager();
        // Accept the already-current row too: bailing there was the one path that ended in ZERO feedback, which is
        // indistinguishable from "the click didn't register". Every click now ends in a dialog.
        if (Ctx<AccountInfo>(sender) is not { } a) return;

        // A broken/stub saved login can't be used - refuse before even trying.
        if (a.NeedsRelogin) { ShowReloginWarning(a.Label); return; }

        // Switching is now safe even with chats open: each chat carries its own account's OAuth token, so changing the
        // active account never touches a running chat's login. No "close chats first" warning needed anymore.
        switch (_vm.SwitchAccount(a))
        {
            case Services.SwitchOutcome.Ok:
                SetNewChatProvider("claude", persist: true);
                MessageBox.Show(this,
                    $"Claude Code is now active as {a.Label}.\n\n" +
                    "New chats use this account. Chats from other Claude logins stay with those logins " +
                    "(sidebar shows only this account's threads).",
                    "Account switched", MessageBoxButton.OK, MessageBoxImage.Information);
                // Optional: re-home a chat that was left on another *provider* (not another Claude account).
                OfferMoveOpenChatsToProvider("claude");
                break;
            case Services.SwitchOutcome.NeedsRelogin:
                ShowReloginWarning(a.Label);
                break;
            case Services.SwitchOutcome.Missing:
                MessageBox.Show(this,
                    $"{a.Label}'s saved login is no longer available, so nothing changed. Re-add it with the account menu → \"Add another account\".",
                    "Account unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
                break;
            case Services.SwitchOutcome.SaveFailed:
                MessageBox.Show(this,
                    $"Couldn't save the switch to {a.Label} — your selection may not survive restarting VibeCode.\n\n" +
                    (Services.AccountService.Instance.LastSelectError ?? "Unknown error writing settings.json."),
                    "Switch not saved", MessageBoxButton.OK, MessageBoxImage.Warning);
                break;
        }
    }

    private void OnSwitchCodexAccount(object sender, RoutedEventArgs e)
    {
        CloseAccountManager();
        if (Ctx<CodexAccountInfo>(sender) is not { } account) return;
        switch (_vm.SwitchCodexAccount(account))
        {
            case CodexAccountOutcome.Ok:
                SetNewChatProvider("codex", persist: true);
                // Validation on the way in: switching to an account that is ITSELF maxed out just trades one dead
                // account for another - say so up front instead of letting the next prompt fail.
                if (account.AtLimit)
                    MessageBox.Show(this,
                        $"Heads up: {account.Label} is also at its usage limit ({account.UsageDisplay}). Prompts will likely fail until it resets.",
                        "That account is at its limit too", MessageBoxButton.OK, MessageBoxImage.Warning);
                // Per-account workspaces: chats stay under the login that created them (sidebar filters by active
                // account). Optional bulk move still available when the previous login is exhausted.
                var movable = _vm.CodexChatsMovableTo(account.Id);
                if (movable.Count > 0)
                {
                    var ask = MessageBox.Show(this,
                        $"OpenAI Codex is now active as {account.Label}.\n\n" +
                        $"{movable.Count} open Codex chat{(movable.Count == 1 ? "" : "s")}/pane{(movable.Count == 1 ? "" : "s")} " +
                        "still belong to other saved logins and are hidden while this account is active " +
                        "(switch back to see them).\n\n" +
                        "Move those chats onto this account too? (Use Yes if the old login is maxed out.)",
                        "Account switched", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (ask == MessageBoxResult.Yes)
                    {
                        var (movedCount, failedCount) = _vm.MoveAllCodexChatsToAccount(account.Id, account.Label);
                        var summary = $"Moved {movedCount} Codex chat{(movedCount == 1 ? "" : "s")}/pane{(movedCount == 1 ? "" : "s")} to {account.Label}.";
                        if (failedCount > 0) summary += $"\n{failedCount} couldn't be moved and keep their old account.";
                        MessageBox.Show(this, summary, "Chats updated",
                            MessageBoxButton.OK, failedCount > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
                    }
                }
                else
                    MessageBox.Show(this,
                        $"OpenAI Codex is now active as {account.Label}.\n\n" +
                        "New chats use this account. Chats from other Codex logins stay with those logins " +
                        "(sidebar shows only this account's threads).",
                        "Account switched", MessageBoxButton.OK, MessageBoxImage.Information);
                MaybeOfferProviderChat("codex");   // e.g. a Claude chat is focused: offer a Codex chat so the models flip
                break;
            case CodexAccountOutcome.NeedsRelogin:
                MessageBox.Show(this,
                    $"{account.Label}'s saved OpenAI login is unavailable. Add that account once more to refresh its private login; the other saved accounts will remain untouched.",
                    "Account needs sign-in", MessageBoxButton.OK, MessageBoxImage.Warning);
                break;
            case CodexAccountOutcome.Missing:
                MessageBox.Show(this, "That saved OpenAI account is no longer available, so nothing changed.",
                    "Account unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
                break;
            case CodexAccountOutcome.SaveFailed:
                MessageBox.Show(this,
                    "VibeCode couldn't save the OpenAI account selection, so nothing changed.\n\n" +
                    (CodexAccountService.Instance.LastError ?? "Unknown account-store error."),
                    "Switch not saved", MessageBoxButton.OK, MessageBoxImage.Warning);
                break;
        }
    }

    private void ShowReloginWarning(string label) =>
        MessageBox.Show(this,
            $"{label}'s saved login is incomplete, so I didn't switch (that would have logged you out).\n\n" +
            "Re-add it: open the account menu → \"Add another account\" and sign in as this account once. " +
            "VibeCode will save a fresh, working login and you'll be able to switch freely after that.",
            "That account needs a fresh sign-in", MessageBoxButton.OK, MessageBoxImage.Warning);

    // Chats authenticate with each account's STORED OAuth token (so switching can't corrupt them), but a static token
    // can't self-refresh. This keeps the stored tokens fresh (best-effort) so per-account chats keep working long-term.
    private System.Windows.Threading.DispatcherTimer? _tokenRefreshTimer;
    // Task.Run so nothing (not even the fast file I/O before the probe's first await) runs on the UI thread.
    private static void KickTokenRefresh() => _ = Task.Run(() => Services.AccountService.Instance.RefreshAllTokensAsync());
    private void StartTokenRefresh()
    {
        KickTokenRefresh();   // once now
        _tokenRefreshTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(90) };
        _tokenRefreshTimer.Tick -= OnTokenRefreshTick;
        _tokenRefreshTimer.Tick += OnTokenRefreshTick;
        _tokenRefreshTimer.Start();
    }
    private void OnTokenRefreshTick(object? sender, EventArgs e) => KickTokenRefresh();

    // VibeCode's OpenAI login is a managed app-server session in its own CODEX_HOME. Codex refreshes managed tokens
    // automatically while chats are active; this timer also refreshes them while VibeCode is merely sitting open.
    private System.Windows.Threading.DispatcherTimer? _codexRefreshTimer;
    private int _codexRefreshRunning;
    private void StartCodexAccountRefresh()
    {
        KickCodexAccountRefresh(forceRefresh: true);
        _codexRefreshTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
        _codexRefreshTimer.Tick -= OnCodexRefreshTick;
        _codexRefreshTimer.Tick += OnCodexRefreshTick;
        _codexRefreshTimer.Start();
    }
    private void OnCodexRefreshTick(object? sender, EventArgs e) => KickCodexAccountRefresh(forceRefresh: true);
    private void KickCodexAccountRefresh(bool forceRefresh)
    {
        if (Interlocked.Exchange(ref _codexRefreshRunning, 1) != 0) return;
        _ = RefreshCodexAccountAsync(forceRefresh);
    }
    private async Task RefreshCodexAccountAsync(bool forceRefresh)
    {
        try
        {
            var accounts = await CodexAccountService.Instance.RefreshAllAsync(forceRefresh);
            _vm.ApplyCodexAccounts(accounts);
            ScheduleAccountManagerRefresh();
        }
        catch { /* preserve the last known-good account rows; the next timer tick retries */ }
        finally { Interlocked.Exchange(ref _codexRefreshRunning, 0); }
    }

    // Kimi's ACP authenticate method only validates the cached token; it creates no session and makes no model call.
    private System.Windows.Threading.DispatcherTimer? _kimiRefreshTimer;
    private int _kimiRefreshRunning;
    private void StartKimiAccountRefresh()
    {
        KickKimiAccountRefresh();
        _kimiRefreshTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
        _kimiRefreshTimer.Tick -= OnKimiRefreshTick;
        _kimiRefreshTimer.Tick += OnKimiRefreshTick;
        _kimiRefreshTimer.Start();
    }
    private void OnKimiRefreshTick(object? sender, EventArgs e) => KickKimiAccountRefresh();
    private void KickKimiAccountRefresh()
    {
        if (Interlocked.Exchange(ref _kimiRefreshRunning, 1) != 0) return;
        _ = RefreshKimiAccountAsync();
    }
    private async Task<KimiAccountState> RefreshKimiAccountAsync()
    {
        try
        {
            var state = await KimiAccountLoginService.ReadAsync();
            _vm.ApplyKimiAccount(state);
            ScheduleAccountManagerRefresh();
            return state;
        }
        finally { Interlocked.Exchange(ref _kimiRefreshRunning, 0); }
    }

    // Grok accounts are independently authenticated and billed through ACP using their own GROK_AUTH_PATH.
    private System.Windows.Threading.DispatcherTimer? _grokRefreshTimer;
    private int _grokRefreshRunning;
    private void StartGrokAccountRefresh()
    {
        KickGrokAccountRefresh();
        _grokRefreshTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
        _grokRefreshTimer.Tick -= OnGrokRefreshTick;
        _grokRefreshTimer.Tick += OnGrokRefreshTick;
        _grokRefreshTimer.Start();
    }
    private void OnGrokRefreshTick(object? sender, EventArgs e) => KickGrokAccountRefresh();
    private void KickGrokAccountRefresh()
    {
        if (Interlocked.Exchange(ref _grokRefreshRunning, 1) != 0) return;
        _ = RefreshGrokAccountAsync();
    }
    private async Task<GrokAccountState> RefreshGrokAccountAsync()
    {
        try
        {
            var accounts = await GrokAccountService.Instance.RefreshAllAsync();
            _vm.ApplyGrokAccounts(accounts);
            var state = await GrokAccountLoginService.ReadAsync(
                GrokAccountService.Instance.AuthPathFor(GrokAccountService.Instance.ActiveId),
                includeUsage: false);
            _vm.ApplyGrokAccount(state);
            ScheduleAccountManagerRefresh();
            return state;
        }
        finally { Interlocked.Exchange(ref _grokRefreshRunning, 0); }
    }

    // Per-account usage polling: probe every saved account's session/week /usage in the background and flag changes on
    // its row. Task.Run keeps the probe's file I/O off the UI thread (RefreshAccountUsageAsync marshals its UI updates).
    private System.Windows.Threading.DispatcherTimer? _accountUsageTimer;
    private void KickAccountUsage() => _ = Task.Run(async () =>
    {
        await _vm.RefreshAccountUsageAsync();
        // Usage is itself a sort/filter key, so a completed background probe must re-place rows that are currently
        // ordered by availability or attention instead of leaving a stale order until the manager is reopened.
        ScheduleAccountManagerRefresh();
    });
    private void StartAccountUsagePolling()
    {
        KickAccountUsage();   // pre-warm so the numbers are ready when the account menu is opened
        _accountUsageTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(4) };
        _accountUsageTimer.Tick -= OnAccountUsageTick;
        _accountUsageTimer.Tick += OnAccountUsageTick;
        _accountUsageTimer.Start();
    }
    private void OnAccountUsageTick(object? sender, EventArgs e) => KickAccountUsage();

    /// <summary>
    /// "Add API key" - sign a provider in with a raw key instead of its subscription login. The key is
    /// validated against the vendor's free model-list endpoint before it is stored (DPAPI-sealed), so a
    /// typo fails here rather than as a confusing CLI error on the next message.
    /// </summary>
    private async void OnAddApiKeyAccount(object sender, RoutedEventArgs e)
    {
        var provider = SelectedAccountManagerProvider;
        if (!Services.ApiKeyAccountService.Providers.Contains(provider)) provider = "claude";
        CloseAccountManager();

        var dialog = new UI.ApiKeyDialog(provider) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        Services.ApiKeyAccountService.Instance.Add(provider, dialog.ApiKey, dialog.AccountLabel);
        RefreshAccountManagerViews();
        await Task.CompletedTask;
    }

    private async void OnAddAccount(object sender, RoutedEventArgs e)
    {
        var provider = SelectedAccountManagerProvider;
        CloseAccountManager();
        if (provider == "codex")
        {
            await SignInToCodexAsync();
            return;
        }
        if (provider == "kimi")
        {
            await ManageKimiAccountAsync(selectIfReady: true);
            return;
        }
        if (provider == "grok")
        {
            await SignInToGrokAsync();
            return;
        }

        Services.AccountService.Instance.SaveCurrent();
        MessageBox.Show(this,
            "Claude Code's official account login will open in a terminal. Complete that sign-in there.\n\n" +
            "VibeCode gives this account its own permanent Claude home, so Claude Code can refresh OAuth normally. " +
            "No browser cookies or short-lived copied token will be saved.",
            "Add account", MessageBoxButton.OK, MessageBoxImage.Information);
        var result = await Services.AccountService.Instance.LoginNewAccountAsync();
        if (!result.Success || result.AccountId is not { } privateAccountId)
        {
            MessageBox.Show(this, result.Error ?? "Claude Code did not finish signing in.",
                "Claude account not added", MessageBoxButton.OK, MessageBoxImage.Warning);
            _vm.RefreshAccounts();
            return;
        }
        Services.AccountService.Instance.SelectAccount(privateAccountId);
        SetNewChatProvider("claude", persist: true);
        _vm.RefreshAccounts();
        var privateAccountLabel = result.Label ?? "Claude account";
        MessageBox.Show(this,
            privateAccountLabel + " is saved in its own native Claude Code home. New chats use this account, and Claude Code will refresh the login automatically.",
            "Claude account added", MessageBoxButton.OK, MessageBoxImage.Information);
        return;
#if false
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/k \"" + Protocol.ClaudeSession.ResolveCliPath() + "\" /login",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Open a terminal and run:  claude /login\n\n" + ex.Message, "Add account");
            return;
        }
        MessageBox.Show(this,
            "A terminal opened running `claude /login`. Sign in to the OTHER Claude account there, then click OK — VibeCode will save it so you can switch between both. Your current account is already saved.",
            "Add account", MessageBoxButton.OK, MessageBoxImage.Information);
        _vm.RefreshAccounts();   // snapshot the just-added login into the store
        // Make the account you just signed into the active one - but never SILENTLY. Adopting whatever the live
        // ~/.claude.json holds used to overwrite a deliberate selection, which reads as "I picked A and it went back to B".
        if (Services.AccountService.Instance.Current()?.Id is { } newId)
        {
            var sel = Services.AppSettings.Current.ActiveAccountId;
            var adopt = string.IsNullOrEmpty(sel) || sel == newId
                || MessageBox.Show(this,
                       "Use the account you just signed into for new chats?\n\nChoosing No keeps your current selection.",
                       "Switch to the new account?", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
            if (adopt)
            {
                Services.AccountService.Instance.SelectAccount(newId);
                SetNewChatProvider("claude", persist: true);
            }
        }
        _vm.RefreshAccounts();
#endif
    }

    private async void OnManageKimiAccount(object sender, RoutedEventArgs e)
    {
        CloseAccountManager();
        await ManageKimiAccountAsync(selectIfReady: true);
    }

    private async Task ManageKimiAccountAsync(bool selectIfReady)
    {
        var state = await RefreshKimiAccountAsync();
        if (ShowKimiAccountProbeError(state)) return;
        if (state.IsSignedIn)
        {
            if (selectIfReady) SetNewChatProvider("kimi", persist: true);
            return;
        }

        if (!state.IsInstalled)
        {
            var install = MessageBox.Show(this,
                "Kimi Code CLI is not installed. Install Kimi's official Windows build now?\n\n" +
                "The official installer opens in PowerShell. Kimi also requires Git for Windows.",
                "Install Kimi Code CLI", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (install != MessageBoxResult.Yes) return;
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoExit -NoProfile -ExecutionPolicy Bypass -Command \"irm 'https://code.kimi.com/kimi-code/install.ps1' | iex\"",
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Normal,
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    "Run this in PowerShell:\n\nirm https://code.kimi.com/kimi-code/install.ps1 | iex\n\n" + ex.Message,
                    "Install Kimi Code CLI", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            MessageBox.Show(this,
                "Finish the official installer in the PowerShell window, then close it and click OK here. " +
                "VibeCode will detect the new CLI without needing an app restart.",
                "Installing Kimi Code CLI", MessageBoxButton.OK, MessageBoxImage.Information);
            state = await RefreshKimiAccountAsync();
            if (!state.IsInstalled)
            {
                MessageBox.Show(this,
                    state.Error ?? "Kimi Code CLI still was not found. Finish the installer, then reopen the account menu and choose Kimi Code.",
                    "Kimi CLI not detected", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (ShowKimiAccountProbeError(state)) return;
        }

        await SignInToKimiAsync();
    }

    private bool ShowKimiAccountProbeError(KimiAccountState state)
    {
        if (string.IsNullOrWhiteSpace(state.Error)) return false;
        MessageBox.Show(this,
            "VibeCode found the Kimi Code CLI but could not verify its account.\n\n" + state.Error +
            "\n\nRun `kimi doctor` in a terminal for the CLI's own diagnostics.",
            "Couldn't check Kimi Code", MessageBoxButton.OK, MessageBoxImage.Warning);
        return true;
    }

    private async Task SignInToKimiAsync()
    {
        using var cancellation = new CancellationTokenSource();
        var status = new TextBlock
        {
            Text = "Starting Kimi's device-code sign-in…\nYour browser will open when Kimi returns the verification link.",
            Foreground = (Brush)FindResource("Muted"),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
        };
        var cancel = new Button { Content = "Cancel", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0), Padding = new Thickness(14, 6, 14, 6) };
        cancel.SetResourceReference(StyleProperty, "GhostButton");
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(status);
        panel.Children.Add(cancel);
        var progress = CreateThemedDialog("Sign in to Kimi", panel, 430);
        var closingForResult = false;
        cancel.Click += (_, _) => cancellation.Cancel();
        progress.Closing += (_, _) => { if (!closingForResult) cancellation.Cancel(); };
        progress.Show();

        var result = await KimiAccountLoginService.LoginAsync(
            url => Dispatcher.BeginInvoke(() =>
            {
                status.Text = "Finish signing in to Kimi in your browser.\nVibeCode will detect it automatically.";
                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                catch { /* the verification URL remains visible in Kimi's status output */ }
            }),
            line => Dispatcher.BeginInvoke(() =>
            {
                if (!line.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    status.Text = line.Length > 240 ? line[..240] + "…" : line;
            }),
            cancellation.Token);

        closingForResult = true;
        if (progress.IsVisible) progress.Close();
        if (!result.Success)
        {
            if (!cancellation.IsCancellationRequested)
                MessageBox.Show(this, result.Error ?? "Kimi sign-in failed.", "Kimi account not added",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        // LoginAsync already performs the supported ACP authenticate check before returning success. Apply that
        // authoritative result directly instead of spawning a second ACP process that could transiently disagree.
        _vm.ApplyKimiAccount(new KimiAccountState
        {
            IsInstalled = true,
            IsSignedIn = true,
            Version = result.Version,
        });
        SetNewChatProvider("kimi", persist: true);
        MessageBox.Show(this,
            "Kimi Code is ready. New Kimi chats and Kimi Bridge agents use the shared login stored by the official CLI under ~/.kimi-code.",
            "Kimi account ready", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void OnManageGrokAccount(object sender, RoutedEventArgs e)
    {
        CloseAccountManager();
        await ManageGrokAccountAsync(selectIfReady: true);
    }

    private async Task ManageGrokAccountAsync(bool selectIfReady)
    {
        var state = await RefreshGrokAccountAsync();
        if (ShowGrokAccountProbeError(state)) return;
        if (state.IsSignedIn)
        {
            if (selectIfReady) SetNewChatProvider("grok", persist: true);
            return;
        }

        if (!state.IsInstalled)
        {
            MessageBox.Show(this,
                "The Grok CLI is not available. Install it so grok.exe is on PATH, place it beside VibeCode, " +
                "or set VIBECODE_GROK_PATH.",
                "Grok runtime not found", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        await SignInToGrokAsync();
    }

    private bool ShowGrokAccountProbeError(GrokAccountState state)
    {
        if (string.IsNullOrWhiteSpace(state.Error)) return false;
        MessageBox.Show(this,
            "VibeCode found the Grok runtime but could not verify its xAI account.\n\n" + state.Error +
            "\n\nYou can verify the runtime directly with `grok agent --no-leader stdio`.",
            "Couldn't check Grok", MessageBoxButton.OK, MessageBoxImage.Warning);
        return true;
    }

    private enum GrokSignInChoice
    {
        Browser,
        DeviceCode,
        Cookies,
    }

    private GrokSignInChoice? PromptForGrokSignInChoice()
    {
        GrokSignInChoice? choice = null;
        var intro = new TextBlock
        {
            Text = "Choose how xAI should connect this Grok account.",
            Foreground = (Brush)FindResource("Muted"),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        };

        Button ChoiceButton(string title, string detail, bool primary = false)
        {
            var button = new Button
            {
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(13, 10, 13, 10),
                Margin = new Thickness(0, 0, 0, 8),
                Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = title, FontWeight = FontWeights.SemiBold },
                        new TextBlock
                        {
                            Text = detail, Foreground = (Brush)FindResource("Faint"),
                            FontSize = 10.5, Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap,
                        },
                    },
                },
            };
            button.SetResourceReference(StyleProperty, primary ? "PrimaryButton" : "GhostButton");
            return button;
        }

        var device = ChoiceButton("Use device code (recommended)",
            "Shows a short code you can approve from any browser or device.", primary: true);
        var browser = ChoiceButton("Use existing browser session",
            "xAI reuses the login already present in your default browser.");
        var cookies = ChoiceButton("Paste Grok cookies",
            "Uses your own Cookie-Editor JSON or Netscape export in a temporary private sign-in window.");
        var cancel = new Button
        {
            Content = "Cancel", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 4, 0, 0),
        };
        cancel.SetResourceReference(StyleProperty, "GhostButton");

        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(intro);
        panel.Children.Add(device);
        panel.Children.Add(browser);
        panel.Children.Add(cookies);
        panel.Children.Add(cancel);
        var dialog = CreateThemedDialog("Connect Grok account", panel, 470);
        browser.Click += (_, _) => { choice = GrokSignInChoice.Browser; dialog.DialogResult = true; };
        device.Click += (_, _) => { choice = GrokSignInChoice.DeviceCode; dialog.DialogResult = true; };
        cookies.Click += (_, _) => { choice = GrokSignInChoice.Cookies; dialog.DialogResult = true; };
        return dialog.ShowDialog() == true ? choice : null;
    }

    private string? PromptForGrokCookies()
    {
        string? secret = null;
        var explanation = new TextBlock
        {
            Text = "Only paste cookies from your own Grok/xAI account. Cookie-Editor JSON, Netscape export, and a Cookie header are supported.",
            Foreground = (Brush)FindResource("Muted"), FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
        };
        var warning = new TextBlock
        {
            Text = "Cookies are live credentials. VibeCode masks them, never logs them, and clears the temporary browser profile after sign-in.",
            Foreground = (Brush)FindResource("Accent"), FontSize = 10.5,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
        };
        var box = new PasswordBox
        {
            MaxLength = 256 * 1024, Padding = new Thickness(9, 8, 9, 8),
            Background = (Brush)FindResource("Bg2"), Foreground = (Brush)FindResource("Text"),
            BorderBrush = (Brush)FindResource("Border"),
        };
        var status = new TextBlock
        {
            Text = "Press Ctrl+V here, or use Paste from clipboard.",
            Foreground = (Brush)FindResource("Faint"), FontSize = 10.5,
            Margin = new Thickness(1, 6, 0, 12),
        };
        var paste = new Button { Content = "Paste from clipboard", Padding = new Thickness(12, 7, 12, 7) };
        var import = new Button
        {
            Content = "Sign in", IsDefault = true, Padding = new Thickness(14, 7, 14, 7),
            Margin = new Thickness(8, 0, 0, 0),
        };
        var cancel = new Button
        {
            Content = "Cancel", IsCancel = true, Padding = new Thickness(14, 7, 14, 7),
            Margin = new Thickness(8, 0, 0, 0),
        };
        paste.SetResourceReference(StyleProperty, "GhostButton");
        import.SetResourceReference(StyleProperty, "PrimaryButton");
        cancel.SetResourceReference(StyleProperty, "GhostButton");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(paste);
        buttons.Children.Add(import);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(explanation);
        panel.Children.Add(warning);
        panel.Children.Add(box);
        panel.Children.Add(status);
        panel.Children.Add(buttons);
        var dialog = CreateThemedDialog("Paste Grok cookies", panel, 510);

        paste.Click += (_, _) =>
        {
            try
            {
                var clipboardText = Clipboard.ContainsText() ? Clipboard.GetText() : "";
                box.Password = clipboardText;
                clipboardText = string.Empty;
                status.Text = box.Password.Length > 0
                    ? "Cookie text loaded and hidden."
                    : "The clipboard does not contain text.";
            }
            catch
            {
                status.Text = "VibeCode could not read text from the clipboard.";
            }
        };
        import.Click += (_, _) =>
        {
            if (box.Password.Length == 0)
            {
                status.Text = "Paste a cookie export first.";
                return;
            }
            secret = box.Password;
            box.Clear();
            dialog.DialogResult = true;
        };
        dialog.Loaded += (_, _) => box.Focus();
        var accepted = dialog.ShowDialog() == true;
        box.Clear();
        return accepted ? secret : null;
    }

    private async void OnImportGrokCookies(object sender, RoutedEventArgs e)
    {
        CloseAccountManager();
        await ImportGrokCookiesAsync();
    }

    private async Task ImportGrokCookiesAsync()
    {
        var state = await RefreshGrokAccountAsync();
        if (ShowGrokAccountProbeError(state)) return;
        if (!state.IsInstalled)
        {
            MessageBox.Show(this, "The reviewed Grok runtime is not available.", "Grok runtime not found",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var cookieText = PromptForGrokCookies();
        if (cookieText is null) return;
        using var imported = GrokCookieImportService.Parse(cookieText);
        cookieText = string.Empty;
        if (!imported.Success)
        {
            MessageBox.Show(this, imported.Error ?? "The Grok cookie export is not usable.",
                "Grok cookies not imported", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        await RunGrokLoginAsync(GrokLoginMode.ExistingBrowserSession, imported);
    }

    private async Task SignInToGrokAsync()
    {
        switch (PromptForGrokSignInChoice())
        {
            case GrokSignInChoice.Browser:
                await RunGrokLoginAsync(GrokLoginMode.ExistingBrowserSession);
                break;
            case GrokSignInChoice.DeviceCode:
                await RunGrokLoginAsync(GrokLoginMode.DeviceCode);
                break;
            case GrokSignInChoice.Cookies:
                await ImportGrokCookiesAsync();
                break;
        }
    }

    private async Task RunGrokLoginAsync(GrokLoginMode mode, GrokCookieImportResult? cookieImport = null)
    {
        using var cancellation = new CancellationTokenSource();
        var status = new TextBlock
        {
            Text = "Starting Grok's device-code sign-in…\nYour browser will open when xAI returns the verification link.",
            Foreground = (Brush)FindResource("Muted"),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
        };
        status.Text = cookieImport is not null
            ? "Preparing a private Grok sign-in window for the pasted cookies..."
            : mode == GrokLoginMode.ExistingBrowserSession
                ? "Starting Grok's browser sign-in...\nxAI can reuse the session already present in your default browser."
                : "Starting Grok's device-code sign-in...\nYour browser will open when xAI returns the verification link.";
        var cancel = new Button
        {
            Content = "Cancel", HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0), Padding = new Thickness(14, 6, 14, 6),
        };
        cancel.SetResourceReference(StyleProperty, "GhostButton");
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(status);
        panel.Children.Add(cancel);
        var progress = CreateThemedDialog("Sign in to Grok", panel, 430);
        var closingForResult = false;
        cancel.Click += (_, _) => cancellation.Cancel();
        progress.Closing += (_, _) => { if (!closingForResult) cancellation.Cancel(); };
        progress.Show();

        GrokCookieLoginWindow? cookieWindow = null;
        string? cookieBrowserError = null;
        var result = await GrokAccountService.Instance.AddAsync(
            mode,
            url => Dispatcher.BeginInvoke(async () =>
            {
                if (cookieImport is null)
                {
                    status.Text = mode == GrokLoginMode.DeviceCode
                        ? "Approve the device code with xAI in your browser.\nVibeCode will detect it automatically."
                        : "Finish signing in to Grok with xAI in your browser.\nYour existing browser session should be reused.";
                    try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                    catch { /* the trusted verification URL remains available to the CLI */ }
                    return;
                }

                try
                {
                    status.Text = "Loading your Grok session into a temporary private browser...";
                    cookieWindow = new GrokCookieLoginWindow { Owner = this };
                    cookieWindow.UserCancelled += (_, _) => cancellation.Cancel();
                    cookieWindow.Show();
                    await cookieWindow.InitializeAsync(cookieImport.Cookies, url);
                    status.Text = "Waiting for xAI to finish the official OAuth exchange...";
                }
                catch (Exception ex)
                {
                    cookieBrowserError = "The temporary Grok sign-in window could not be prepared: " + ex.Message;
                    cancellation.Cancel();
                }
            }),
            line => Dispatcher.BeginInvoke(() =>
            {
                if (!line.Contains("[xAI sign-in link]", StringComparison.Ordinal))
                    status.Text = line.Length > 240 ? line[..240] + "…" : line;
            }),
            cancellation.Token);

        closingForResult = true;
        if (progress.IsVisible) progress.Close();
        if (cookieWindow is { IsLoaded: true })
            await cookieWindow.ClearAndCloseAsync();
        if (!result.Success)
        {
            var userCancelled = cancellation.IsCancellationRequested && cookieBrowserError is null;
            if (cookieImport is not null && !userCancelled)
            {
                var detail = cookieBrowserError ?? result.Error ?? "The pasted Grok session was not accepted.";
                if (detail.Length > 600) detail = detail[..600] + "...";
                var retry = MessageBox.Show(this,
                    detail + "\n\nThe pasted cookies may be stale or tied to another browser. Try device-code sign-in instead?",
                    "Grok cookie sign-in did not finish", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (retry == MessageBoxResult.Yes)
                    await RunGrokLoginAsync(GrokLoginMode.DeviceCode);
            }
            else if (!userCancelled)
                MessageBox.Show(this, result.Error ?? "Grok sign-in failed.", "Grok account not added",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _vm.ApplyGrokAccount(new GrokAccountState
        {
            IsInstalled = true,
            IsSignedIn = true,
            Version = result.Version,
        });
        _vm.ApplyGrokAccounts(GrokAccountService.Instance.List());
        SetNewChatProvider("grok", persist: true);
        var identity = !string.IsNullOrWhiteSpace(result.Name)
            ? result.Name
            : !string.IsNullOrWhiteSpace(result.Email) ? result.Email : "Grok account";
        MessageBox.Show(this,
            cookieImport is null
                ? $"{identity} is ready. New Grok chats and Bridge agents use this isolated xAI login."
                : $"{identity} is ready. The temporary cookie browser was cleared; the isolated CLI login was saved.",
            "Grok account ready", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async Task SignInToCodexAsync()
    {
        using var cancellation = new CancellationTokenSource();
        var status = new TextBlock
        {
            Text = "Starting VibeCode's secure OpenAI sign-in…\nYour browser will open in a moment.",
            Foreground = (Brush)FindResource("Muted"),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
        };
        var cancel = new Button { Content = "Cancel", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0), Padding = new Thickness(14, 6, 14, 6) };
        cancel.SetResourceReference(StyleProperty, "GhostButton");
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(status);
        panel.Children.Add(cancel);
        var progress = CreateThemedDialog("Sign in to VibeCode", panel, 390);
        var closingForResult = false;
        cancel.Click += (_, _) => cancellation.Cancel();
        progress.Closing += (_, _) => { if (!closingForResult) cancellation.Cancel(); };
        progress.Show();

        var result = await CodexAccountService.Instance.AddAsync(url =>
        {
            status.Text = "Finish the OpenAI sign-in in your browser.\nVibeCode will detect it and keep the session refreshed.";
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }, cancellation.Token);
        closingForResult = true;
        if (progress.IsVisible) progress.Close();

        if (!result.Success)
        {
            if (!cancellation.IsCancellationRequested)
                MessageBox.Show(this, result.Error ?? "Codex sign-in failed.", "Codex account not added", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _vm.ApplyCodexAccounts(CodexAccountService.Instance.List());
        SetNewChatProvider("codex", persist: true);
        var identity = !string.IsNullOrWhiteSpace(result.Name) && !result.Name.Contains('@')
            ? result.Name.Trim()
            : "your OpenAI account";
        var plan = !string.IsNullOrWhiteSpace(result.Plan) ? $" ({result.Plan})" : "";
        MessageBox.Show(this,
            $"VibeCode saved {identity}{plan} as a separate OpenAI account.\n\nYou can switch between every saved Codex account from the account manager. Existing chats stay on their original account, and your standalone Codex app and CLI login were not changed.",
            "VibeCode account ready", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ---------- account right-click: test / remove ----------

    // Capture which account was right-clicked BEFORE its context menu opens. The account popup is StaysOpen=False
    // so opening the menu can close it and drop the menu items' inherited DataContext - this field is the reliable source.
    private AccountInfo? _ctxAccount;
    private void OnAccountRightDown(object sender, MouseButtonEventArgs e) => _ctxAccount = Ctx<AccountInfo>(sender);

    private async void OnTestAccount(object sender, RoutedEventArgs e)
    {
        CloseAccountManager();
        if (_ctxAccount is not { } a) return;
        // The probe runs a headless `claude -p /usage` against an isolated copy of this account's creds; give a heads-up.
        var wait = CreateThemedDialog("Testing account", new TextBlock
        {
            Text = $"Testing {a.Label}…\nRunning claude against this account's saved login.",
            Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(18), FontSize = 13, TextWrapping = TextWrapping.Wrap,
        }, 300);
        wait.Show();
        AccountTestResult result;
        try { result = await Services.AccountService.Instance.TestAccountAsync(a.Id); }
        finally { wait.Close(); }

        var raw = result.Raw.Length > 1600 ? result.Raw[..1600] + "\n… (truncated)" : result.Raw;
        var body = result.Detail;
        if (!string.IsNullOrWhiteSpace(raw)) body += "\n\n──── raw claude output ────\n" + raw;
        MessageBox.Show(this, body, $"Test: {a.Label} — {result.Verdict}",
            MessageBoxButton.OK, result.Ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void OnRemoveAccount(object sender, RoutedEventArgs e)
    {
        CloseAccountManager();
        if (_ctxAccount is not { } a) return;
        var mainNote = a.IsCurrent
            ? "\n\nThis is your current main account. VibeCode will fall back to another saved login if one exists, otherwise new chats won't prefer this account."
            : "";
        var r = MessageBox.Show(this,
            $"Remove {a.Label} from VibeCode?{mainNote}\n\nThis only forgets VibeCode's saved copy of the login — it doesn't sign the account out anywhere else. You can add it back later with \"Add another account\".",
            "Remove account", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes) return;
        // Same feedback rule as the switch path: a refused settings.json write means nothing was removed, so say so
        // instead of leaving the row on screen with no explanation.
        switch (_vm.ForgetAccount(a))
        {
            case Services.SwitchOutcome.SaveFailed:
                MessageBox.Show(this,
                    $"Couldn't remove {a.Label} — updating the saved account selection failed, so nothing was deleted.\n\n" +
                    (Services.AccountService.Instance.LastSelectError ?? "Unknown error writing settings.json."),
                    "Account not removed", MessageBoxButton.OK, MessageBoxImage.Warning);
                break;
        }
    }

    // ---------- Codex account test/remove (mirrors the Claude account menu) ----------

    private CodexAccountInfo? _ctxCodexAccount;
    private void OnCodexAccountRightDown(object sender, MouseButtonEventArgs e) => _ctxCodexAccount = Ctx<CodexAccountInfo>(sender);

    private async void OnTestCodexAccount(object sender, RoutedEventArgs e)
    {
        CloseAccountManager();
        if (_ctxCodexAccount is not { } a) return;
        // Reads run against the account's OWN private CODEX_HOME (refresh happens in place - testing can't corrupt it).
        var wait = CreateThemedDialog("Testing account", new TextBlock
        {
            Text = $"Testing {a.Label}…\nAsking Codex to validate this account's saved login.",
            Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(18), FontSize = 13, TextWrapping = TextWrapping.Wrap,
        }, 300);
        wait.Show();
        AccountTestResult result;
        try { result = await CodexAccountService.Instance.TestAccountAsync(a.Id); }
        finally { wait.Close(); }
        _vm.ApplyCodexAccounts(CodexAccountService.Instance.List());   // the test refreshed identity/usage - show it

        var raw = result.Raw.Length > 1600 ? result.Raw[..1600] + "\n… (truncated)" : result.Raw;
        var body = result.Detail;
        if (!string.IsNullOrWhiteSpace(raw)) body += "\n\n──── raw codex output ────\n" + raw;
        MessageBox.Show(this, body, $"Test: {a.Label} — {result.Verdict}",
            MessageBoxButton.OK, result.Ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void OnRemoveCodexAccount(object sender, RoutedEventArgs e)
    {
        CloseAccountManager();
        if (_ctxCodexAccount is not { } a) return;
        var mainNote = a.IsCurrent
            ? "\n\nThis is the active Codex account. VibeCode will fall back to another saved Codex login if one exists, otherwise nothing."
            : "";
        var r = MessageBox.Show(this,
            $"Remove {a.Label} from VibeCode?{mainNote}\n\nThis only forgets VibeCode's saved copy of the login — it doesn't sign the account out anywhere else. You can add it back later with \"Add account\".",
            "Remove account", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes) return;
        switch (_vm.ForgetCodexAccount(a))
        {
            case CodexAccountOutcome.SaveFailed:
                MessageBox.Show(this,
                    $"Couldn't remove {a.Label}.\n\n" + (CodexAccountService.Instance.LastError ?? "Unknown account-store error."),
                    "Account not removed", MessageBoxButton.OK, MessageBoxImage.Warning);
                break;
        }
    }

    // ---------- Grok account switch/test/logout/remove ----------

    private GrokAccountInfo? _ctxGrokAccount;
    private void OnGrokAccountRightDown(object sender, MouseButtonEventArgs e) =>
        _ctxGrokAccount = Ctx<GrokAccountInfo>(sender);

    private void OnSwitchGrokAccount(object sender, RoutedEventArgs e)
    {
        CloseAccountManager();
        if (Ctx<GrokAccountInfo>(sender) is not { } account) return;
        switch (_vm.SwitchGrokAccount(account))
        {
            case GrokAccountOutcome.Ok:
                SetNewChatProvider("grok", persist: true);
                MessageBox.Show(this,
                    $"Grok is now active as {account.Label}.\n\n" +
                    "New chats use this account. Chats from other Grok logins stay with those logins " +
                    "(sidebar shows only this account's threads — switch back to see the others).",
                    "Account switched", MessageBoxButton.OK,
                    account.AtLimit ? MessageBoxImage.Warning : MessageBoxImage.Information);
                break;
            case GrokAccountOutcome.NeedsRelogin:
                MessageBox.Show(this,
                    $"{account.Label} is signed out. Add the account again to refresh only its private Grok login.",
                    "Account needs sign-in", MessageBoxButton.OK, MessageBoxImage.Warning);
                break;
            case GrokAccountOutcome.Missing:
                MessageBox.Show(this, "That Grok account is no longer saved.",
                    "Account unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
                break;
            case GrokAccountOutcome.SaveFailed:
                MessageBox.Show(this,
                    "VibeCode couldn't save the Grok account selection.\n\n" +
                    (GrokAccountService.Instance.LastError ?? "Unknown account-store error."),
                    "Switch not saved", MessageBoxButton.OK, MessageBoxImage.Warning);
                break;
        }
    }

    private async void OnTestGrokAccount(object sender, RoutedEventArgs e)
    {
        CloseAccountManager();
        if (_ctxGrokAccount is not { } account) return;
        var wait = CreateThemedDialog("Testing Grok account", new TextBlock
        {
            Text = $"Testing {account.Label}…\nAuthenticating its isolated CLI login and reading xAI billing.",
            Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(18),
            FontSize = 13, TextWrapping = TextWrapping.Wrap,
        }, 320);
        wait.Show();
        AccountTestResult result;
        try { result = await GrokAccountService.Instance.TestAccountAsync(account.Id); }
        finally { wait.Close(); }
        _vm.ApplyGrokAccounts(GrokAccountService.Instance.List());
        var body = result.Detail;
        if (!string.IsNullOrWhiteSpace(result.Raw)) body += "\n\n" + result.Raw;
        MessageBox.Show(this, body, $"Test: {account.Label} — {result.Verdict}",
            MessageBoxButton.OK, result.Ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private async void OnLogoutGrokAccount(object sender, RoutedEventArgs e)
    {
        CloseAccountManager();
        if (_ctxGrokAccount is not { } account) return;
        if (MessageBox.Show(this,
                $"Sign out {account.Label}?\n\nVibeCode will run Grok's supported logout command against only this saved profile. Other Grok accounts stay signed in.",
                "Sign out Grok account", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        var result = await GrokAccountService.Instance.LogoutAsync(account.Id);
        _vm.ApplyGrokAccounts(GrokAccountService.Instance.List());
        if (!result.Success)
            MessageBox.Show(this, result.Error ?? "Grok logout failed.", "Could not sign out",
                MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OnRemoveGrokAccount(object sender, RoutedEventArgs e)
    {
        CloseAccountManager();
        if (_ctxGrokAccount is not { } account) return;
        var mainNote = account.IsCurrent
            ? "\n\nThis is the active Grok account. VibeCode will fall back to another saved Grok login if one exists, otherwise nothing."
            : "";
        if (MessageBox.Show(this,
                $"Remove {account.Label} from VibeCode?{mainNote}\n\nSign out first if you want Grok to revoke its cached CLI session.",
                "Remove Grok account", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        switch (_vm.ForgetGrokAccount(account))
        {
            case GrokAccountOutcome.SaveFailed:
                MessageBox.Show(this,
                    $"Couldn't remove {account.Label}.\n\n" +
                    (GrokAccountService.Instance.LastError ?? "Unknown account-store error."),
                    "Account not removed", MessageBoxButton.OK, MessageBoxImage.Warning);
                break;
        }
    }

    // ---------- Spotify inline player ----------

    private System.Windows.Threading.DispatcherTimer? _spotifyTimer;

    private void StartSpotifyPolling()
    {
        // 1s heartbeat: advances the progress bar smoothly + re-syncs with Spotify every few seconds.
        _spotifyTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _spotifyTimer.Tick -= OnSpotifyTick;
        _spotifyTimer.Tick += OnSpotifyTick;
        _spotifyTimer.Start();
        if (SpotifyService.Instance.Enabled) _ = SpotifyService.Instance.PollAsync();
    }

    private void OnSpotifyTick(object? sender, EventArgs e) => SpotifyService.Instance.Heartbeat();

    private void OnSpotifyPlayPause(object sender, RoutedEventArgs e) => _ = SpotifyService.Instance.ToggleAsync();
    private void OnSpotifyNextTrack(object sender, RoutedEventArgs e) => _ = SpotifyService.Instance.NextAsync();
    private void OnSpotifyPrevTrack(object sender, RoutedEventArgs e) => _ = SpotifyService.Instance.PreviousAsync();
    private void OnSpotifyShuffle(object sender, RoutedEventArgs e) => _ = SpotifyService.Instance.ToggleShuffleAsync();
    private void OnSpotifyRepeat(object sender, RoutedEventArgs e) => _ = SpotifyService.Instance.CycleRepeatAsync();

    private void OnSpotifyOpenSettings(object sender, RoutedEventArgs e) => ShowSettingsDialog();

    // ---------- games extension ----------

    private void OnOpenSurviveTheShapes(object sender, RoutedEventArgs e)
    {
        GamesPopup.IsOpen = false;
        GameWindow.Open(this, GameCatalog.SurviveTheShapes);
    }

    // ---------- weather extension ----------

    private System.Windows.Threading.DispatcherTimer? _weatherTimer;

    private void StartWeatherPolling()
    {
        // NWS observations update roughly hourly, so a 10 minute poll is plenty to keep the chip honest without
        // hammering a free public API. No-op while the extension is off or no place has been picked.
        _weatherTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
        _weatherTimer.Tick -= OnWeatherTick;
        _weatherTimer.Tick += OnWeatherTick;
        _weatherTimer.Start();
        OnWeatherTick(null, EventArgs.Empty);   // once now, so the chip isn't stuck on "--" after a launch
    }

    private void OnWeatherTick(object? sender, EventArgs e)
    {
        if (WeatherService.Instance.Enabled) _ = WeatherService.Instance.RefreshAsync();
    }

    private void OnWeatherChipClick(object sender, RoutedEventArgs e) => RadarWindow.Open(this);

    // ---------- chat header: Bridge button + ⋮ menu (fork / swarm / subagents) ----------

    private void OnMoreChecked(object sender, RoutedEventArgs e) => MorePopup.IsOpen = true;
    private void OnMoreUnchecked(object sender, RoutedEventArgs e) => MorePopup.IsOpen = false;
    private void OnMorePopupClosed(object? sender, EventArgs e) => MoreToggle.IsChecked = false;

    /// <summary>Header Bridge button. Bridge is the headline action, so it is NOT buried in the ⋮ menu.</summary>
    private void OnMenuBridge(object sender, RoutedEventArgs e)
    {
        MorePopup.IsOpen = false;
        var chat = ActiveChatForWindow ?? _vm.ActiveChat;
        // Resume / reopen paths stay one-click. Only a brand-new roster needs a peer provider pick.
        if (!_vm.WouldStartFreshBridge(chat))
        {
            ActivateBridgeForWindow();
            return;
        }
        // Open on the next input tick so the click that pressed the button doesn't immediately
        // dismiss this StaysOpen=False popup.
        Dispatcher.BeginInvoke(new Action(() => BridgePeerPopup.IsOpen = true),
            System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OnStartBridgeWithProvider(object sender, RoutedEventArgs e)
    {
        BridgePeerPopup.IsOpen = false;
        if ((sender as FrameworkElement)?.Tag is not string provider) return;
        ActivateBridgeForWindow(provider);
    }

    private void OnMenuFork(object sender, RoutedEventArgs e) { MorePopup.IsOpen = false; OnForkChat(sender, e); }


    // ---------- bridge (multiple providers/agents on one project) ----------

    // Each bridge pane's toolbar buttons (Todos, Subagents, per-agent usage, mode/model/effort) are
    // ToggleButtons whose StaysOpen=False popup is bound TwoWay to IsChecked. That combo has a classic
    // WPF bounce: clicking the button while its popup is open first dismisses the popup (the click lands
    // outside it, on mouse-down) which unchecks the button, and then the button's own click (mouse-up)
    // re-checks it and the popup springs straight back open - so it looks like it never closes. We record
    // when a toggle was just dismissed and, if its click re-opened it microseconds later, force it shut so
    // the button behaves as a real toggle. Keyed to the exact toggle so opening pane B's popup while pane
    // A's is open still works (that dismisses A and opens B, not close-then-reopen of the same one).
    private ToggleButton? _lastDismissedPaneToggle;
    private long _lastDismissedPaneToggleAt;

    private void OnPaneToggleUnchecked(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton tb)
        {
            _lastDismissedPaneToggle = tb;
            _lastDismissedPaneToggleAt = Environment.TickCount64;
        }
    }

    private void OnPaneToggleClick(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { IsChecked: true } tb
            && ReferenceEquals(tb, _lastDismissedPaneToggle)
            && Environment.TickCount64 - _lastDismissedPaneToggleAt < 250)
        {
            tb.IsChecked = false;
        }
    }

    private void OnActivateBridge(object sender, RoutedEventArgs e) => ActivateBridgeForWindow();
    private void OnAddBridgeAgent(object sender, RoutedEventArgs e)
    {
        // At the Settings cap the button stays clickable on purpose — silent disable left people thinking
        // Add agent was broken. Tell them the limit and offer to open Settings to raise it.
        if (!_vm.CanAddBridgeAgent)
        {
            ShowBridgeAgentLimitReached();
            return;
        }
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OnAddBridgeProvider(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string provider) return;
        if (!_vm.CanAddBridgeAgent)
        {
            ShowBridgeAgentLimitReached();
            return;
        }
        var before = _vm.BridgePanes.Count;
        _vm.AddBridgeAgent(provider);
        if (_vm.BridgePanes.Count <= before) return;   // refused: already at the agent cap
        // An agent added from the second-monitor window belongs to that display and stays there.
        _vm.BridgePanes[^1].OnSecondMonitor = _isBridgeMonitor && SplitActive;
        RefreshBridgePartitions();
    }

    /// <summary>Explain the Bridge agent cap and offer to open Settings so the user can raise it.</summary>
    private void ShowBridgeAgentLimitReached()
    {
        var count = _vm.BridgePanes.Count;
        var limit = AppSettings.Current.BridgeAgentLimit;
        var open = MessageBox.Show(this,
            $"This Bridge already has {count} agent{(count == 1 ? "" : "s")} — that's the maximum set in Settings ({limit}).\n\n" +
            "If you seriously want more, open Settings → Bridge and raise \"Maximum agents per Bridge\".\n\n" +
            "Open Settings now?",
            "Bridge is full",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);
        if (open == MessageBoxResult.Yes) ShowSettingsDialog();
    }

    /// <summary>A click anywhere in a pane makes that exact conversation the Bridge selection and keyboard-scroll target.</summary>
    private void OnBridgePanePointerDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement root && root.DataContext is ChatViewModel pane)
            ActivateBridgeScrollTarget(root, pane);
    }

    /// <summary>
    /// Route the wheel explicitly to the pane under the pointer. Markdown/read-only text can otherwise consume the
    /// event before the ListBox template's ScrollViewer sees it. A nested code block or composer keeps the wheel while
    /// it still has room; at its boundary, scrolling naturally continues through the pane transcript.
    /// </summary>
    private void OnBridgePaneMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta == 0 || sender is not FrameworkElement root || root.DataContext is not ChatViewModel pane) return;
        var host = ActivateBridgeScrollTarget(root, pane);
        if (host is null || host.ScrollableHeight <= 0) return;
        if (NestedScrollerCanConsume(e.OriginalSource as DependencyObject, root, host, e.Delta)) return;

        var notches = Math.Abs(e.Delta) / 120.0;
        var lines = SystemParameters.WheelScrollLines;
        var distance = lines < 0
            ? host.ViewportHeight * notches
            : Math.Max(1, lines) * 18.0 * notches;
        var target = host.VerticalOffset - Math.Sign(e.Delta) * distance;
        host.ScrollToVerticalOffset(Math.Clamp(target, 0, host.ScrollableHeight));
        e.Handled = true;
    }

    private ScrollViewer? ActivateBridgeScrollTarget(DependencyObject root, ChatViewModel pane)
    {
        _vm.SelectBridgePane(pane);
        _vm.NoteBridgeActivity();
        _bridgeScrollPane = pane;
        _bridgeScrollList = FindVisualDescendant<ListBox>(root);
        return _bridgeScrollList is null ? null : FindScrollViewer(_bridgeScrollList);
    }

    private ScrollViewer? CurrentBridgeScrollViewer()
    {
        if (_bridgeScrollList is null || _bridgeScrollPane is null || !_bridgeScrollList.IsLoaded
            || !_vm.BridgePanes.Contains(_bridgeScrollPane)
            || !ReferenceEquals(_bridgeScrollList.DataContext, _bridgeScrollPane))
            return null;
        return FindScrollViewer(_bridgeScrollList);
    }

    private static T? FindVisualDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindVisualDescendant<T>(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }

    private static T? FindVisualDescendant<T>(DependencyObject root, Func<T, bool> predicate) where T : DependencyObject
    {
        if (root is T match && predicate(match)) return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindVisualDescendant(VisualTreeHelper.GetChild(root, i), predicate) is { } found) return found;
        return null;
    }

    private static bool NestedScrollerCanConsume(DependencyObject? source, DependencyObject paneRoot,
        ScrollViewer transcript, int delta)
    {
        for (var current = source; current is not null && !ReferenceEquals(current, paneRoot); current = EventParent(current))
        {
            ScrollViewer? nested = current as ScrollViewer;
            if (nested is null && current is TextBoxBase textBox) nested = FindScrollViewer(textBox);
            if (nested is null || ReferenceEquals(nested, transcript)) continue;
            if (delta > 0 && nested.VerticalOffset > 0.5) return true;
            if (delta < 0 && nested.VerticalOffset < nested.ScrollableHeight - 0.5) return true;
        }
        return false;
    }

    private static DependencyObject? EventParent(DependencyObject child)
    {
        if (child is ContentElement content)
            return ContentOperations.GetParent(content) ?? (content as FrameworkContentElement)?.Parent;
        if (child is Visual visual)
            return VisualTreeHelper.GetParent(visual) ?? LogicalTreeHelper.GetParent(child);
        return LogicalTreeHelper.GetParent(child);
    }

    private void OnRemoveBridgePane(object sender, RoutedEventArgs e)
    {
        if (Ctx<ChatViewModel>(sender) is { } vm) _vm.RemoveBridgePane(vm);
    }

    // A bridge pane is a real provider conversation, so it can branch independently without stopping its peers.
    private void OnForkBridgePane(object sender, RoutedEventArgs e)
    {
        if (Ctx<ChatViewModel>(sender) is not { CanFork: true, SessionId: { } sid } pane) return;
        _vm.NoteBridgeActivity();
            NewChatForWindow(pane.Cwd, sid, fork: true, title: pane.Title + " (fork)", provider: pane.Provider);
    }

    // Crown this pane as the bridge MANAGER (the brain that dispatches its peers), or step it down again.
    private void OnToggleBridgeManager(object sender, RoutedEventArgs e)
    {
        if (Ctx<ChatViewModel>(sender) is { } vm) _vm.ToggleBridgeManager(vm);
    }

    // Expand one bridge agent to fill the whole bridge (focus mode), or restore the grid if it's already expanded.
    private void OnToggleBridgePaneExpand(object sender, RoutedEventArgs e)
    {
        if (Ctx<ChatViewModel>(sender) is not { } vm) return;
        _vm.ToggleBridgeExpand(vm, SplitActive ? PaneBelongsToThisBridgeSurface : null);
    }

    // ---------- bridge pane ⋮ overflow (Manager / Fork / Todos / Subagents / Messages) ----------
    // Expand + Close stay as standalone header buttons; everything else opens from this menu.
    // Feature panels (todos / subagents / messages) open after the menu dismisses so the same click
    // that chose the row doesn't also kill the follow-up popup (same pattern as OnMenuBridge).

    private void OnPaneMenuManager(object sender, RoutedEventArgs e)
    {
        ClosePaneMoreMenu(sender);
        OnToggleBridgeManager(sender, e);
    }

    private void OnPaneMenuFork(object sender, RoutedEventArgs e)
    {
        ClosePaneMoreMenu(sender);
        OnForkBridgePane(sender, e);
    }

    private void OnPaneMenuMinimize(object sender, RoutedEventArgs e)
    {
        ClosePaneMoreMenu(sender);
        if (Ctx<ChatViewModel>(sender) is { } vm) _vm.MinimizeBridgePane(vm);
    }

    private void OnRestoreBridgePane(object sender, RoutedEventArgs e)
    {
        if (Ctx<ChatViewModel>(sender) is { } vm) _vm.RestoreBridgePane(vm);
    }

    private void OnPaneMenuTodos(object sender, RoutedEventArgs e)
    {
        ClosePaneMoreMenu(sender);
        OpenPaneFeaturePopup(sender, "PaneTodosPopup");
    }

    private void OnPaneMenuMessages(object sender, RoutedEventArgs e)
    {
        ClosePaneMoreMenu(sender);
        OpenPaneFeaturePopup(sender, "PaneMessagesPopup");
    }

    private void ClosePaneMoreMenu(object sender)
    {
        if (FindNamedInVisualTree<ToggleButton>(sender as DependencyObject, "PaneMoreToggle") is { } more)
            more.IsChecked = false;
    }

    private void OpenPaneFeaturePopup(object sender, string popupName)
    {
        var origin = sender as DependencyObject;
        // Open after the ⋮ menu finishes closing so its dismiss click doesn't also kill this popup.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (FindNamedInVisualTree<Popup>(origin, popupName) is { } popup)
                popup.IsOpen = true;
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>Find a named element registered in a DataTemplate namescope near <paramref name="start"/>.</summary>
    private static T? FindNamedInVisualTree<T>(DependencyObject? start, string name) where T : class
    {
        for (var d = start; d is not null; d = VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d))
        {
            if (d is not FrameworkElement fe) continue;
            if (fe.FindName(name) is T hit) return hit;
            if (NameScope.GetNameScope(fe) is INameScope scope && scope.FindName(name) is T scoped)
                return scoped;
        }
        return null;
    }

    private void OnBridgePaneSend(object sender, RoutedEventArgs e)
    {
        // One button per pane: Stop/Interrupt while a turn is running, Send otherwise (incl. during startup).
        if (Ctx<ChatViewModel>(sender) is not { } vm) return;
        _vm.SelectBridgePane(vm);
        if (vm.CanInterrupt) vm.Interrupt();
        else SendBridgePane(vm);
    }

    private void OnBridgeInputKeyDown(object sender, KeyEventArgs e)
    {
        if (Ctx<ChatViewModel>(sender) is not { } vm) return;
        _vm.SelectBridgePane(vm);
        // Bridge uses the same Send/Stop button and advertises the same Esc shortcut as a normal chat. The normal
        // composer handled it, but this pane-local handler did not, so Esc was silently ignored instead of
        // interrupting the pane whose editor has focus.
        if (e.Key == Key.Escape && vm.CanInterrupt)
        {
            vm.Interrupt();
            e.Handled = true;
            return;
        }
        // Ctrl+V with an image on the clipboard stages it on this pane (text paste still works normally).
        if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control && TryPasteImage(vm))
        {
            e.Handled = true;
            return;
        }
        if (sender is TextBox bridgeInput && TryNavigatePromptHistory(bridgeInput, vm, e.Key, text => vm.Draft = text))
        {
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            SendBridgePane(vm);
            e.Handled = true;
        }
    }

    private void SendBridgePane(ChatViewModel vm)
    {
        var text = vm.Draft.Trim();
        var atts = vm.Attachments.ToList();
        if (text.Length == 0 && atts.Count == 0) return;
        if (vm.Send(text, atts)) { vm.Draft = ""; vm.Attachments.Clear(); _vm.NoteBridgeActivity(); }   // sending resets the bridge idle timeout
    }

    // Bridge panes reuse the shared attachment helpers, targeting the pane's own ChatViewModel
    // (resolved from the sender's DataContext) instead of the active chat.
    private void OnBridgeAttachClick(object sender, RoutedEventArgs e)
    {
        if (Ctx<ChatViewModel>(sender) is not { } vm) return;
        _vm.SelectBridgePane(vm);
        var dlg = MakeAttachDialog();
        if (dlg.ShowDialog(this) == true)
            foreach (var f in dlg.FileNames) AddAttachmentFromFile(vm, f);
    }

    private void OnBridgeComposerDragOver(object sender, DragEventArgs e)
    {
        if (Ctx<ChatViewModel>(sender) is not null && e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void OnBridgeComposerDrop(object sender, DragEventArgs e)
    {
        if (Ctx<ChatViewModel>(sender) is not { } vm || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        _vm.SelectBridgePane(vm);
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            foreach (var f in files) AddAttachmentFromFile(vm, f);
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+V with an image on the clipboard becomes an attachment (text paste still works normally).
        if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control
            && ActiveChatForWindow is { } chat && TryPasteImage(chat))
        {
            e.Handled = true;
            return;
        }
        if (SlashPopup.IsOpen && SlashList.Items.Count > 0)
        {
            if (e.Key is Key.Down or Key.Up)
            {
                var delta = e.Key == Key.Down ? 1 : -1;
                SlashList.SelectedIndex = ((SlashList.SelectedIndex + delta) % SlashList.Items.Count + SlashList.Items.Count) % SlashList.Items.Count;
                SlashList.ScrollIntoView(SlashList.SelectedItem);
                e.Handled = true;
                return;
            }
            if (e.Key is Key.Tab or Key.Enter)
            {
                AcceptSlash();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Escape)
            {
                SlashPopup.IsOpen = false;
                e.Handled = true;
                return;
            }
        }
        if (ActiveChatForWindow is { } activeChat
            && sender is TextBox input
            && TryNavigatePromptHistory(input, activeChat, e.Key, text => input.Text = text))
        {
            // A recalled slash command should remain history-navigation text; do not let TextChanged reopen the
            // command chooser and make the next Up/Down unexpectedly select a slash command instead.
            SlashPopup.IsOpen = false;
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            SendCurrent();
            e.Handled = true;
        }
        else if (e.Key == Key.Tab && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            ActiveChatForWindow?.CycleMode();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && ActiveChatForWindow?.CanInterrupt == true)
        {
            ActiveChatForWindow.Interrupt();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Recall one prompt at a composer's vertical boundary. Once a prompt has been recalled, repeated Up/Down at its
    /// ending caret continues through history so all ten entries are reachable without walking through multiline text.
    /// </summary>
    private static bool TryNavigatePromptHistory(
        TextBox box, ChatViewModel chat, Key key, Action<string> applyText)
    {
        if (Keyboard.Modifiers != ModifierKeys.None || key is not (Key.Up or Key.Down) || box.SelectionLength > 0)
            return false;

        var browsingAtEnd = box.CaretIndex == box.Text.Length && chat.IsBrowsingPromptHistory(box.Text);
        if (!browsingAtEnd)
        {
            var line = box.GetLineIndexFromCharacterIndex(box.CaretIndex);
            var lastLine = Math.Max(0, box.LineCount - 1);
            if (key == Key.Up && line > 0) return false;
            if (key == Key.Down && line < lastLine) return false;
        }

        if (!chat.TryNavigatePromptHistory(key == Key.Up ? -1 : 1, box.Text, out var recalled))
            return false;

        applyText(recalled);
        // Bridge composers are bound to Draft. SetCurrentValue is a no-op when binding already refreshed the target,
        // and preserves that binding if target refresh is deferred until after this key event.
        if (!string.Equals(box.Text, recalled, StringComparison.Ordinal))
            box.SetCurrentValue(TextBox.TextProperty, recalled);
        box.CaretIndex = recalled.Length;
        return true;
    }

    /// <summary>After a pane click, common navigation keys scroll that pane without stealing keys from an editor/input.</summary>
    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!SurfaceShowBridge || Keyboard.Modifiers != ModifierKeys.None) return;
        var scroll = CurrentBridgeScrollViewer();
        if (scroll is null) return;

        var focused = Keyboard.FocusedElement;
        if (focused is TextBoxBase { IsReadOnly: false } or PasswordBox or ButtonBase or Slider) return;
        if (focused is Selector selector && !ReferenceEquals(selector, _bridgeScrollList)) return;

        switch (e.Key)
        {
            case Key.Up: scroll.LineUp(); break;
            case Key.Down: scroll.LineDown(); break;
            case Key.PageUp: scroll.PageUp(); break;
            case Key.PageDown: scroll.PageDown(); break;
            case Key.Home: scroll.ScrollToTop(); break;
            case Key.End: scroll.ScrollToBottom(); break;
            default: return;
        }
        _vm.NoteBridgeActivity();
        e.Handled = true;
    }

    /// <summary>Esc leaves a per-pane bridge expand. Wired as a BUBBLING KeyDown on the window so it is the LAST
    /// resort: any descendant that already handles Esc (the image lightbox, the slash popup, the interrupt handler)
    /// marks the event handled and we never run. It also only acts when the bridge is showing AND a pane is actually
    /// expanded.</summary>
    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (IsAccountManagerOpen)
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            {
                AccountSearchBox.Focus();
                AccountSearchBox.SelectAll();
                e.Handled = true;
                return;
            }
            if (e.Key != Key.Escape) return;
            CloseAccountManager();
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Escape) return;
        if (!SurfaceShowBridge) return;
        if (!_vm.BridgePanes.Any(p => p.BridgeExpanded)) return;
        _vm.ResetBridgeExpand();
        e.Handled = true;   // only swallowed when we really did restore the grid
    }

    private void OnInputChanged(object sender, TextChangedEventArgs e)
    {
        var text = InputBox.Text;
        var chat = ActiveChatForWindow;
        if (!_restoringComposerDraft && chat is not null)
        {
            chat.Draft = text;    // mirrored into the chat so autosave can keep an unsent prompt across a force quit
            _vm.RequestSave();
        }
        if (chat is null || !text.StartsWith('/') || text.Contains(' ') || text.Length < 1)
        {
            SlashPopup.IsOpen = false;
            return;
        }
        var filter = text[1..];
        var matches = chat.Commands
            .Where(c => c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c.Name.StartsWith(filter, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(c => c.Name)
            .Take(12)
            .ToList();
        SlashList.ItemsSource = matches;
        SlashList.SelectedIndex = matches.Count > 0 ? 0 : -1;
        SlashPopup.IsOpen = matches.Count > 0;
    }

    private void OnSlashPick(object sender, MouseButtonEventArgs e) => AcceptSlash();

    private void OnInputLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Close the slash popup unless focus moved into it (i.e. the user is clicking a command).
        if (e.NewFocus is System.Windows.Media.Visual v && SlashList.IsAncestorOf(v)) return;
        SlashPopup.IsOpen = false;
    }

    private void OnDismissAuth(object sender, RoutedEventArgs e)
    {
        if (ActiveChatForWindow is { } chat) chat.AuthNeeded = false;
    }

    private void OnUsageChecked(object sender, RoutedEventArgs e)
    {
        if (ActiveChatForWindow?.IsClaude != true)
        {
            UsageToggle.IsChecked = false;
            return;
        }
        BridgeCodexUsagePopup.IsOpen = false;
        BridgeGrokUsagePopup.IsOpen = false;
        BridgeKimiUsagePopup.IsOpen = false;
        GrokUsagePopup.IsOpen = false;
        UsagePopup.IsOpen = true;
        UsageService.Instance.Refresh(force: true);   // always fetch fresh numbers when the panel opens
    }

    private void OnUsageUnchecked(object sender, RoutedEventArgs e) => UsagePopup.IsOpen = false;
    private void OnUsagePopupClosed(object? sender, EventArgs e) => UsageToggle.IsChecked = false;

    private void OnGrokUsageChecked(object sender, RoutedEventArgs e)
    {
        if (ActiveChatForWindow?.IsGrok != true)
        {
            GrokAccountUsageToggle.IsChecked = false;
            return;
        }
        UsagePopup.IsOpen = false;
        BridgeUsagePopup.IsOpen = false;
        BridgeCodexUsagePopup.IsOpen = false;
        BridgeGrokUsagePopup.IsOpen = false;
        BridgeKimiUsagePopup.IsOpen = false;
        GrokUsagePopup.IsOpen = true;
        KickGrokAccountRefresh();
    }
    private void OnGrokUsageUnchecked(object sender, RoutedEventArgs e) => GrokUsagePopup.IsOpen = false;
    private void OnGrokUsagePopupClosed(object? sender, EventArgs e) => GrokAccountUsageToggle.IsChecked = false;
    private void OnGrokUsageRefresh(object sender, RoutedEventArgs e) => KickGrokAccountRefresh();
    // Normal Codex chats share the same live account/rate-limit card as Bridge. Move the one popup to the
    // active surface's pill so its data and refresh behavior cannot drift between chat modes.
    private void OnCodexUsageChecked(object sender, RoutedEventArgs e)
    {
        if (ActiveChatForWindow?.IsCodex != true)
        {
            CodexUsageToggle.IsChecked = false;
            return;
        }
        UsagePopup.IsOpen = false;
        GrokUsagePopup.IsOpen = false;
        BridgeUsagePopup.IsOpen = false;
        BridgeGrokUsagePopup.IsOpen = false;
        BridgeKimiUsagePopup.IsOpen = false;
        BridgeCodexUsagePopup.IsOpen = false;
        BridgeCodexUsagePopup.PlacementTarget = CodexUsageToggle;
        BridgeCodexUsagePopup.Placement = PlacementMode.Top;
        BridgeCodexUsagePopup.VerticalOffset = -6;
        BridgeCodexUsagePopup.IsOpen = true;
        KickCodexAccountRefresh(forceRefresh: true);
    }
    private void OnCodexUsageUnchecked(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(BridgeCodexUsagePopup.PlacementTarget, CodexUsageToggle))
            BridgeCodexUsagePopup.IsOpen = false;
    }

    // bridge header: the session · week usage pill is clickable → open the full breakdown (and refresh).
    private void OnBridgeUsageChecked(object sender, RoutedEventArgs e)
    {
        BridgeCodexUsagePopup.IsOpen = false;
        BridgeGrokUsagePopup.IsOpen = false;
        BridgeKimiUsagePopup.IsOpen = false;
        BridgeUsagePopup.IsOpen = true;
        UsageService.Instance.Refresh(force: true);
    }
    private void OnBridgeUsageUnchecked(object sender, RoutedEventArgs e) => BridgeUsagePopup.IsOpen = false;
    private void OnBridgeUsagePopupClosed(object? sender, EventArgs e) => BridgeUsageToggle.IsChecked = false;
    private void OnBridgeUsageRefresh(object sender, RoutedEventArgs e) => UsageService.Instance.Refresh(force: true);

    // bridge header: Announce — pause every agent on the bridge and hand them one broadcast message.
    private void OnBridgeAnnounceChecked(object sender, RoutedEventArgs e)
    {
        BridgeUsagePopup.IsOpen = false;
        BridgeCodexUsagePopup.IsOpen = false;
        BridgeGrokUsagePopup.IsOpen = false;
        BridgeKimiUsagePopup.IsOpen = false;
        BridgeAnnouncePopup.IsOpen = true;
        // Focus the field once the popup has realized its content.
        Dispatcher.BeginInvoke(new Action(() => AnnounceInput.Focus()),
            System.Windows.Threading.DispatcherPriority.Input);
    }
    private void OnBridgeAnnounceUnchecked(object sender, RoutedEventArgs e) => BridgeAnnouncePopup.IsOpen = false;
    private void OnBridgeAnnouncePopupClosed(object? sender, EventArgs e) => BridgeAnnounceToggle.IsChecked = false;
    private void OnCloseBridgeAnnounce(object sender, RoutedEventArgs e) => BridgeAnnouncePopup.IsOpen = false;

    // Enter sends the announcement; Shift+Enter inserts a newline; Esc dismisses without sending.
    private void OnAnnounceInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            SendBridgeAnnouncement();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            BridgeAnnouncePopup.IsOpen = false;
        }
    }

    private void OnSendBridgeAnnouncement(object sender, RoutedEventArgs e) => SendBridgeAnnouncement();

    private void SendBridgeAnnouncement()
    {
        var text = AnnounceInput.Text;
        if (string.IsNullOrWhiteSpace(text)) { AnnounceInput.Focus(); return; }
        _vm.AnnounceToBridge(text);
        AnnounceInput.Clear();
        BridgeAnnouncePopup.IsOpen = false;
    }

    // Codex has its own app-server rate-limit payload (including reset timestamps and optional extra buckets).
    private void OnBridgeCodexUsageChecked(object sender, RoutedEventArgs e)
    {
        BridgeUsagePopup.IsOpen = false;
        BridgeGrokUsagePopup.IsOpen = false;
        BridgeKimiUsagePopup.IsOpen = false;
        BridgeCodexUsagePopup.IsOpen = false;
        BridgeCodexUsagePopup.PlacementTarget = BridgeCodexUsageToggle;
        BridgeCodexUsagePopup.Placement = PlacementMode.Bottom;
        BridgeCodexUsagePopup.VerticalOffset = 6;
        BridgeCodexUsagePopup.IsOpen = true;
        KickCodexAccountRefresh(forceRefresh: true);
    }
    private void OnBridgeCodexUsageUnchecked(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(BridgeCodexUsagePopup.PlacementTarget, BridgeCodexUsageToggle))
            BridgeCodexUsagePopup.IsOpen = false;
    }
    private void OnBridgeCodexUsagePopupClosed(object? sender, EventArgs e)
    {
        if (ReferenceEquals(BridgeCodexUsagePopup.PlacementTarget, CodexUsageToggle))
            CodexUsageToggle.IsChecked = false;
        else if (ReferenceEquals(BridgeCodexUsagePopup.PlacementTarget, BridgeCodexUsageToggle))
            BridgeCodexUsageToggle.IsChecked = false;
    }
    private void OnBridgeCodexUsageRefresh(object sender, RoutedEventArgs e) => KickCodexAccountRefresh(forceRefresh: true);

    private void OnBridgeGrokUsageChecked(object sender, RoutedEventArgs e)
    {
        if (!_vm.BridgeUsesGrok)
        {
            BridgeHeaderGrokUsageToggle.IsChecked = false;
            return;
        }
        UsagePopup.IsOpen = false;
        GrokUsagePopup.IsOpen = false;
        BridgeUsagePopup.IsOpen = false;
        BridgeCodexUsagePopup.IsOpen = false;
        BridgeGrokUsagePopup.IsOpen = true;
        KickGrokAccountRefresh();
    }
    private void OnBridgeGrokUsageUnchecked(object sender, RoutedEventArgs e) => BridgeGrokUsagePopup.IsOpen = false;
    private void OnBridgeGrokUsagePopupClosed(object? sender, EventArgs e) => BridgeHeaderGrokUsageToggle.IsChecked = false;
    private void OnBridgeGrokUsageRefresh(object sender, RoutedEventArgs e) => KickGrokAccountRefresh();

    private void OnBridgeKimiUsageChecked(object sender, RoutedEventArgs e)
    {
        if (!_vm.BridgeUsesKimi)
        {
            BridgeKimiUsageToggle.IsChecked = false;
            return;
        }
        UsagePopup.IsOpen = false;
        GrokUsagePopup.IsOpen = false;
        BridgeUsagePopup.IsOpen = false;
        BridgeCodexUsagePopup.IsOpen = false;
        BridgeGrokUsagePopup.IsOpen = false;
        BridgeKimiUsagePopup.IsOpen = true;
        KimiUsageService.Instance.Refresh(force: true);
    }
    private void OnBridgeKimiUsageUnchecked(object sender, RoutedEventArgs e) => BridgeKimiUsagePopup.IsOpen = false;
    private void OnBridgeKimiUsagePopupClosed(object? sender, EventArgs e) => BridgeKimiUsageToggle.IsChecked = false;
    private void OnBridgeKimiUsageRefresh(object sender, RoutedEventArgs e) => KimiUsageService.Instance.Refresh(force: true);
    private void OnClosePreview(object sender, RoutedEventArgs e)
    {
        if (PanelChatForWindow is { } chat) chat.SelectedFile = null;
    }

    private void OnCloseAllArtifacts(object sender, RoutedEventArgs e) => PanelChatForWindow?.ClearFiles();

    private void AcceptSlash()
    {
        if (SlashList.SelectedItem is CommandChoice cmd)
        {
            InputBox.Text = "/" + cmd.Name + " ";
            InputBox.CaretIndex = InputBox.Text.Length;
        }
        SlashPopup.IsOpen = false;
        InputBox.Focus();
    }

    // ---------- attachments ----------

    private const int LongPasteAttachmentThreshold = 2_000;
    private const string PastedTextFilePrefix = "Message ";

    private static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" };

    /// <summary>
    /// Keep a large paste out of the editable prompt while staging that pasted portion as a readable text attachment.
    /// Text already typed in the composer is deliberately left alone. The routed paste event also covers the context
    /// menu, so main and Bridge composers behave the same regardless of how Paste was invoked.
    /// </summary>
    private void OnComposerPasting(object sender, DataObjectPastingEventArgs e)
    {
        if (sender is not TextBox box || !TryGetPastedText(e.SourceDataObject, out var pastedText)
            || pastedText.Length <= LongPasteAttachmentThreshold)
            return;

        var chat = ReferenceEquals(box, InputBox) ? ActiveChatForWindow : box.DataContext as ChatViewModel;
        if (chat is null) return;

        var fileName = NextPastedTextFileName(chat.Attachments);
        chat.Attachments.Add(new Attachment
        {
            Kind = "text",
            FileName = fileName,
            Text = pastedText,
        });

        // Cancel only this paste. Existing composer text (including a selection) remains untouched.
        e.CancelCommand();
        e.Handled = true;
        ShowComposerHint($"Attached the {pastedText.Length:N0}-character paste as {fileName}.");
    }

    private static bool TryGetPastedText(IDataObject data, out string text)
    {
        text = "";
        try
        {
            text = data.GetData(DataFormats.UnicodeText, true) as string
                   ?? data.GetData(DataFormats.Text, true) as string
                   ?? "";
            return text.Length > 0;
        }
        catch
        {
            // Clipboard/data-object ownership can disappear during a paste. Let WPF handle it normally in that case.
            return false;
        }
    }

    private static string NextPastedTextFileName(IEnumerable<Attachment> attachments)
    {
        var next = 1;
        foreach (var attachment in attachments)
        {
            var name = attachment.FileName;
            if (!name.StartsWith(PastedTextFilePrefix, StringComparison.OrdinalIgnoreCase)
                || !name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                continue;

            var numberLength = name.Length - PastedTextFilePrefix.Length - ".txt".Length;
            if (numberLength > 0
                && int.TryParse(name.AsSpan(PastedTextFilePrefix.Length, numberLength), out var number)
                && number >= next)
                next = number + 1;
        }
        return $"{PastedTextFilePrefix}{next}.txt";
    }

    private static Microsoft.Win32.OpenFileDialog MakeAttachDialog() => new()
    {
        Title = "Attach images or files",
        Multiselect = true,
        Filter = "Attachable files|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp;*.pdf;*.txt;*.md;*.csv;*.json;*.log;*.xml;*.yml;*.yaml|Images|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp|All files|*.*",
    };

    private void OnAttachClick(object sender, RoutedEventArgs e)
    {
        if (ActiveChatForWindow is not { } chat) return;
        var dlg = MakeAttachDialog();
        if (dlg.ShowDialog(this) == true)
            foreach (var f in dlg.FileNames) AddAttachmentFromFile(chat, f);
    }

    private void OnRemoveAttachment(object sender, RoutedEventArgs e)
    {
        // The chip's DataContext is the Attachment, not its owning chat, so remove it from whichever
        // collection actually holds it (the active chat OR any bridge pane).
        if (Ctx<Attachment>(sender) is not { } a) return;
        if (ActiveChatForWindow?.Attachments.Remove(a) == true) return;
        foreach (var p in _vm.BridgePanes)
            if (p.Attachments.Remove(a)) return;
    }

    private void OnCancelQueued(object sender, RoutedEventArgs e)
    {
        if (Ctx<QueuedItem>(sender) is not { } q) return;
        q.Owner.CancelQueued(q);
    }

    private void OnSendQueuedNow(object sender, RoutedEventArgs e)
    {
        if (Ctx<QueuedItem>(sender) is not { } queued) return;
        var owner = queued.Owner;
        if (!owner.SendQueuedNow(queued)) return;

        if (owner.IsBridgeAgent)
        {
            _vm.SelectBridgePane(owner);
            _vm.NoteBridgeActivity();
        }
    }

    private void OnComposerDragOver(object sender, DragEventArgs e)
    {
        if (ActiveChatForWindow is not null && e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void OnComposerDrop(object sender, DragEventArgs e)
    {
        if (ActiveChatForWindow is not { } chat || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            foreach (var f in files) AddAttachmentFromFile(chat, f);
    }

    /// <summary>Pull an image off the clipboard into the given chat's attachments; returns false so a plain text paste falls through.</summary>
    private bool TryPasteImage(ChatViewModel chat)
    {
        try
        {
            // Clipboard access can throw COMException when another process briefly holds the
            // clipboard; keep every access inside the try so a normal Ctrl+V never errors out.
            if (!Clipboard.ContainsImage()) return false;
            var src = Clipboard.GetImage();
            if (src is null) return false;
            var png = PngFromBitmap(src);
            chat.Attachments.Add(new Attachment
            {
                Kind = "image", FileName = "pasted-image.png", MediaType = "image/png",
                Data = png, Preview = LoadThumb(png),
            });
            return true;
        }
        catch { return false; }
    }

    private void AddAttachmentFromFile(ChatViewModel chat, string path)
    {
        try
        {
            var name = Path.GetFileName(path);
            var ext = Path.GetExtension(path);
            if (ImageExts.Contains(ext))
            {
                var bytes = File.ReadAllBytes(path);
                // png/jpeg/gif/webp are sent as-is; bmp isn't an accepted media type, so re-encode to png.
                var media = ext.ToLowerInvariant() switch
                {
                    ".png" => "image/png", ".gif" => "image/gif", ".webp" => "image/webp",
                    ".jpg" or ".jpeg" => "image/jpeg", _ => "image/png",
                };
                if (ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase))
                {
                    var bmp = new BitmapImage();
                    using var ms = new MemoryStream(bytes);
                    bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.StreamSource = ms; bmp.EndInit();
                    bytes = PngFromBitmap(bmp);
                }
                chat.Attachments.Add(new Attachment
                {
                    Kind = "image", FileName = name, MediaType = media, Data = bytes, Preview = LoadThumb(bytes),
                });
            }
            else if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                chat.Attachments.Add(new Attachment
                {
                    Kind = "document", FileName = name, MediaType = "application/pdf", Data = File.ReadAllBytes(path),
                });
            }
            else
            {
                // treat everything else as text; cap absurdly large files
                var info = new FileInfo(path);
                if (info.Length > 1_000_000)
                {
                    ShowComposerHint($"{name} is too large to attach as text ({info.Length / 1024}KB).");
                    return;
                }
                chat.Attachments.Add(new Attachment { Kind = "text", FileName = name, Text = File.ReadAllText(path) });
            }
        }
        catch (Exception ex)
        {
            ShowComposerHint($"Couldn't attach {Path.GetFileName(path)}: {ex.Message}");
        }
    }

    // A transient status line above the main composer (dictation feedback, attachment errors). This used to write to
    // CwdHint, which lives on the HOME card — invisible once a chat is open, so every message was lost. An empty
    // message clears it immediately; anything else self-clears so a stale error can't sit there forever.
    private System.Windows.Threading.DispatcherTimer? _composerHintTimer;
    private void ShowComposerHint(string message)
    {
        ComposerHintText.Text = message;
        _composerHintTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _composerHintTimer.Stop();
        if (string.IsNullOrEmpty(message)) return;                    // explicit clear — nothing to schedule
        _composerHintTimer.Tick -= OnComposerHintTick;
        _composerHintTimer.Tick += OnComposerHintTick;                // (subscribe once; the -= above prevents stacking)
        _composerHintTimer.Start();
    }
    private void OnComposerHintTick(object? s, EventArgs e) { _composerHintTimer?.Stop(); ComposerHintText.Text = ""; }

    /// <summary>Decode a small thumbnail from image bytes (returns null if the format can't be decoded, e.g. webp without a codec).</summary>
    private static BitmapSource? LoadThumb(byte[] bytes, int decodeWidth = 240)
    {
        try
        {
            var bmp = new BitmapImage();
            using var ms = new MemoryStream(bytes);
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = decodeWidth;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    private static byte[] PngFromBitmap(BitmapSource src)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    // ---------- image lightbox (double-click a chat image to expand it) ----------

    private void OnImageDblClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;                       // single clicks pass through (text selection etc.)
        if ((sender as FrameworkElement)?.DataContext is not Attachment a) return;
        var full = FullImage(a.Data) ?? a.Preview;           // full-res from bytes, fall back to the thumbnail
        if (full is null) return;
        LightboxImage.Source = full;
        LightboxImage.Tag = a;                               // so "Copy image" can re-encode full-res bytes
        Lightbox.Visibility = Visibility.Visible;
        Lightbox.Focus();                                    // so Esc reaches OnLightboxKey
        e.Handled = true;
    }

    private void OnCloseLightbox(object sender, RoutedEventArgs e)
    {
        Lightbox.Visibility = Visibility.Collapsed;
        LightboxImage.Source = null;                         // release the full-res bitmap
        LightboxImage.Tag = null;
        if (e is MouseButtonEventArgs me) me.Handled = true;
    }

    private void OnLightboxKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { OnCloseLightbox(sender, e); e.Handled = true; }
    }

    /// <summary>
    /// Right-click "Copy image" on a chat attachment, composer chip, or the fullscreen lightbox.
    /// Puts a bitmap on the Windows clipboard (paste into Paint / Discord / etc.).
    /// </summary>
    private void OnCopyImage(object sender, RoutedEventArgs e)
    {
        try
        {
            var bmp = ResolveImageForCopy(sender);
            if (bmp is null) return;
            // Clipboard.SetImage wants a freezable bitmap; clone+freeze so we don't pin UI sources.
            var copy = bmp.Clone();
            copy.Freeze();
            Clipboard.SetImage(copy);
        }
        catch
        {
            // Clipboard can be locked by another app — fail quietly like SetClipboard does for text.
        }
    }

    /// <summary>Find the BitmapSource behind an ImageMenu click (attachment DataContext or lightbox Source).</summary>
    private static BitmapSource? ResolveImageForCopy(object sender)
    {
        var mi = sender as FrameworkElement;
        var menu = mi?.Parent as ContextMenu
                   ?? (mi is not null ? ItemsControl.ItemsControlFromItemContainer(mi) as ContextMenu : null);
        var target = menu?.PlacementTarget as FrameworkElement ?? mi;

        // Prefer full-res bytes when the image is an Attachment (chat bubble or composer chip).
        if (target?.DataContext is Attachment att)
            return FullImage(att.Data) ?? att.Preview;
        if (target is Image img)
        {
            if (img.Tag is Attachment tagged)
                return FullImage(tagged.Data) ?? tagged.Preview ?? img.Source as BitmapSource;
            return img.Source as BitmapSource;
        }
        return null;
    }

    /// <summary>Decode an image at full resolution from its bytes (null if the bytes can't be decoded).</summary>
    private static BitmapSource? FullImage(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0) return null;
        try
        {
            var bmp = new BitmapImage();
            using var ms = new MemoryStream(bytes);
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    // ---------- mode / model ----------

    private void OnModeChecked(object sender, RoutedEventArgs e) => ModePopup.IsOpen = true;
    private void OnModeUnchecked(object sender, RoutedEventArgs e) => ModePopup.IsOpen = false;
    private void OnModelChecked(object sender, RoutedEventArgs e)
    {
        ActiveChatForWindow?.RefreshModelPicker(AppSettings.Current.DefaultProvider);
        ModelPopup.IsOpen = true;
    }
    private void OnModelUnchecked(object sender, RoutedEventArgs e) => ModelPopup.IsOpen = false;

    private void OnModePick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string mode) ActiveChatForWindow?.SetMode(mode);
        ModePopup.IsOpen = false;
    }

    private void OnModelPick(object sender, RoutedEventArgs e)
    {
        var m = Ctx<ModelPickerChoice>(sender);
        ModelPopup.IsOpen = false;   // close before any dialog, so the popup can't sit on top of it
        if (m is null || ActiveChatForWindow is not { } chat) return;
        if (m.CanApply) chat.SetPickerModel(m);
        else OfferMoveChatToProvider(chat, m.Model.Provider);   // picking another AI's model used to silently do nothing
    }

    private void OnModePopupClosed(object? sender, EventArgs e) => ModeToggle.IsChecked = false;
    private void OnModelPopupClosed(object? sender, EventArgs e) => ModelToggle.IsChecked = false;

    // ---- per-pane pickers (Bridge). Each pane is its own ChatViewModel; the pick target is the
    //      button's DataContext (or, for the list popups, its Tag, since list items rebind DataContext). ----

    private void OnPaneModePick(object sender, RoutedEventArgs e)
    {
        if (Ctx<ChatViewModel>(sender) is { } pane && (sender as FrameworkElement)?.Tag is string mode)
            pane.SetMode(mode);
        CloseAncestorPopup(sender as DependencyObject);
    }

    private void OnPaneModelPick(object sender, RoutedEventArgs e)
    {
        var m = Ctx<ModelPickerChoice>(sender);
        var pane = (sender as FrameworkElement)?.Tag as ChatViewModel;
        CloseAncestorPopup(sender as DependencyObject);   // close before any dialog, so the popup can't sit on top of it
        if (m is null || pane is null) return;
        if (m.CanApply) pane.SetPickerModel(m);
        else OfferMoveChatToProvider(pane, m.Model.Provider);
    }

    private void OnPaneEffortPick(object sender, RoutedEventArgs e)
    {
        if (Ctx<EffortChoice>(sender) is { } c && (sender as FrameworkElement)?.Tag is ChatViewModel pane)
            pane.SetEffort(c.Value);
        CloseAncestorPopup(sender as DependencyObject);
    }

    /// <summary>Walk up (logical, falling back to visual) to the enclosing Popup and close it. Its IsOpen is
    /// two-way bound to the pill's ToggleButton.IsChecked, so closing also unchecks the pill.</summary>
    private static void CloseAncestorPopup(DependencyObject? d)
    {
        while (d is not null)
        {
            if (d is Popup p) { p.IsOpen = false; return; }
            var next = LogicalTreeHelper.GetParent(d);
            if (next is null && d is Visual) next = VisualTreeHelper.GetParent(d);
            d = next;
        }
    }

    private void OnThinkChecked(object sender, RoutedEventArgs e) => ThinkPopup.IsOpen = true;
    private void OnThinkUnchecked(object sender, RoutedEventArgs e) => ThinkPopup.IsOpen = false;
    private void OnThinkPopupClosed(object? sender, EventArgs e) => ThinkToggle.IsChecked = false;

    private void OnExtendedQueueChecked(object sender, RoutedEventArgs e) => ExtendedQueuePopup.IsOpen = true;
    private void OnExtendedQueueUnchecked(object sender, RoutedEventArgs e) => ExtendedQueuePopup.IsOpen = false;
    private void OnExtendedQueuePopupClosed(object? sender, EventArgs e) => ExtendedQueueToggle.IsChecked = false;

    private ChatViewModel? ExtendedQueueOwner(object sender) =>
        Ctx<ChatViewModel>(sender) ?? ActiveChatForWindow;

    private void OnExtendedQueueEnabledPick(object sender, RoutedEventArgs e)
    {
        if (ExtendedQueueOwner(sender) is { } chat)
            chat.SetExtendedQueueEnabled(!chat.ExtendedQueueEnabled);
    }

    private void OnExtendedQueueChunkPick(object sender, RoutedEventArgs e)
    {
        if (ExtendedQueueOwner(sender) is { } chat
            && int.TryParse((sender as FrameworkElement)?.Tag?.ToString(), out var size))
            chat.SetExtendedQueueChunkSize(size);
        CloseAncestorPopup(sender as DependencyObject);
    }

    private void OnExtendedQueueResume(object sender, RoutedEventArgs e)
    {
        ExtendedQueueOwner(sender)?.ResumeExtendedQueue();
        CloseAncestorPopup(sender as DependencyObject);
    }

    private void OnEffortPick(object sender, RoutedEventArgs e)
    {
        if (Ctx<EffortChoice>(sender) is { } c) ActiveChatForWindow?.SetEffort(c.Value);
        ThinkPopup.IsOpen = false;
    }

    // Fast-mode toggle in the effort dropdown. Keep the popup open so the check flips in place; it's a global
    // setting applied to a chat's next start (a session --settings flag), so no per-turn effect on the current chat.
    private void OnFastModePick(object sender, RoutedEventArgs e)
    {
        if (ActiveChatForWindow is { } chat) chat.SetFastMode(!chat.FastMode);
    }

    // ---------- permissions ----------

    // Respond through the chat that OWNS the card (p.Owner), not the globally-active chat - otherwise a permission
    // card inside a bridge pane answers the wrong VM, whose pending table doesn't have the request, so it silently
    // no-ops and the Allow/Deny buttons look dead. ActiveChat is only a fallback for legacy items with no Owner.
    private ChatViewModel? PermOwner(object sender) => Ctx<PermItem>(sender)?.Owner ?? ActiveChatForWindow;

    private void OnPermAllow(object sender, RoutedEventArgs e)
    {
        if (Ctx<PermItem>(sender) is { } p) PermOwner(sender)?.RespondPermission(p, allow: true);
    }

    private void OnPermAllowAlways(object sender, RoutedEventArgs e)
    {
        if (Ctx<PermItem>(sender) is { } p) PermOwner(sender)?.RespondPermission(p, allow: true, always: true);
    }

    private void OnPermDeny(object sender, RoutedEventArgs e)
    {
        if (Ctx<PermItem>(sender) is { } p) PermOwner(sender)?.RespondPermission(p, allow: false, denyMessage: p.DenyNote);
    }

    private void OnQuestionAnswer(object sender, RoutedEventArgs e)
    {
        if (Ctx<PermItem>(sender) is { } p) PermOwner(sender)?.AnswerQuestion(p);
    }

    private void OnQuestionOptionClicked(object sender, RoutedEventArgs e)
    {
        // radio behavior for single-select questions
        if ((sender as FrameworkElement)?.DataContext is not QuestionOption opt) return;
        var itemsControl = FindParent<ItemsControl>(sender as DependencyObject);
        if (itemsControl?.DataContext is QuestionEntry { MultiSelect: false } entry && opt.Selected)
            foreach (var other in entry.Options.Where(o => o != opt)) other.Selected = false;
    }

    private static T? FindParent<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null && d is not T) d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        return d as T;
    }

    private void OnPlanApproveAuto(object sender, RoutedEventArgs e)
    {
        if (Ctx<PermItem>(sender) is { } p) PermOwner(sender)?.DecidePlan(p, approve: true, autoAccept: true, null);
    }

    private void OnPlanApproveManual(object sender, RoutedEventArgs e)
    {
        if (Ctx<PermItem>(sender) is { } p) PermOwner(sender)?.DecidePlan(p, approve: true, autoAccept: false, null);
    }

    private void OnPlanKeepPlanning(object sender, RoutedEventArgs e)
    {
        if (Ctx<PermItem>(sender) is { } p) PermOwner(sender)?.DecidePlan(p, approve: false, autoAccept: false, p.DenyNote);
    }

    // ---------- panel ----------

    private void OnPanelTab(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, ArtTab)) { ArtTab.IsChecked = true; TodoTab.IsChecked = false; }
        else { TodoTab.IsChecked = true; ArtTab.IsChecked = false; }
    }

    private void OnOpenArtifact(object sender, RoutedEventArgs e)
    {
        var path = PanelChatForWindow?.SelectedFile?.Path;
        if (path is null || !File.Exists(path)) return;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Open file"); }
    }

    private void OnRefreshArtifact(object sender, RoutedEventArgs e) => PanelChatForWindow?.RefreshPreview();
}
