using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using VibeCode.Services;
using VibeCode.UI;

namespace VibeCode;

public partial class App : Application
{
    private BrowserBridgeService? _browserBridge;
    private bool _showingDispatcherError;
    private string? _lastDispatcherError;
    private DateTime _lastDispatcherErrorAt;
    private static bool _restartRequested;
    private static string? _restartTarget;
    private static string? _shutdownOrigin;

    /// <summary>True when this build is running under Wine, which is how the Linux package ships VibeCode.</summary>
    internal static bool IsWineCompatibility => ExternalBrowser.IsWine;

    /// <summary>
    /// Hand a trusted http(s) address to the user's real browser. Wine has no dependable Windows URL association,
    /// so <see cref="ExternalBrowser"/> reaches the Linux opener directly; callers treat false as "show the link".
    /// </summary>
    internal static bool TryOpenExternalUri(string address) => ExternalBrowser.TryOpen(address, out _);

    /// <summary>The theme dictionary for a UI mode. Chosen at startup and again on every live appearance change.
    /// Assembly-qualified rather than the shorter relative form: the relative one resolves against whatever the
    /// ENTRY assembly is, which is VibeCode when the app runs but the harness when a test drives these types.</summary>
    private static Uri ThemeSource(bool cliMode) =>
        new($"pack://application:,,,/VibeCode;component/Themes/{(cliMode ? "Cli" : "Dark")}.xaml");

    /// <summary>Held for the life of the process; releasing it hands this data directory to the next launch.</summary>
    private static IDisposable? _instanceLease;

    /// <summary>Answer a second launch: put the running shell back on screen. It may be closed to the notification
    /// area, which is exactly the case a second launch is trying to undo — RestoreFromBackground is the same path
    /// the tray icon's Open uses, so a hidden window, a hidden Bridge companion and the icon itself all come back
    /// together.</summary>
    private static void BringPrimaryShellForward()
    {
        try
        {
            if (Current?.Windows.OfType<MainWindow>().FirstOrDefault(w => w.IsPrimaryShell) is not { } shell) return;
            shell.RestoreFromBackground();
            if (shell.WindowState == WindowState.Minimized) shell.WindowState = WindowState.Normal;
            shell.Show();
            shell.Activate();
        }
        catch (Exception ex) { CrashLog.Write(ex, "App.BringPrimaryShellForward"); }
    }

    /// <summary>The borderless surfaces, merged ON TOP of the base theme rather than replacing it: it redefines
    /// only the handful of SurfaceX keys the app's chrome is painted with, so everything else still comes from
    /// the theme underneath and the two settings compose instead of forking into a third full theme.</summary>
    private static readonly Uri BorderlessOverlaySource =
        new("pack://application:,,,/VibeCode;component/Themes/Borderless.xaml");

    /// <summary>Load the dictionaries the current settings ask for. Clear() drops only the merged theme;
    /// converters declared directly in App.xaml survive it.</summary>
    private static void ApplyThemeDictionaries(ResourceDictionary resources)
    {
        resources.MergedDictionaries.Clear();
        resources.MergedDictionaries.Add(new ResourceDictionary { Source = ThemeSource(AppSettings.IsCliMode) });
        // Added last on purpose - a merged dictionary later in the list wins the key.
        if (AppSettings.IsBorderless)
            resources.MergedDictionaries.Add(new ResourceDictionary { Source = BorderlessOverlaySource });
    }

    /// <summary>
    /// Apply the UI mode the user just picked. Windows resolve theme brushes, styles and control templates with
    /// StaticResource when their content is parsed, so swapping the dictionary is only half the job - the shell
    /// has to be rebuilt for it to be read. It is rebuilt over the SAME view model, which is the whole point:
    /// this used to relaunch the process, and every running agent died with it.
    /// </summary>
    public static void ApplyAppearanceChange(bool reopenSettings)
    {
        if (!CodeViewerWindow.ConfirmCloseEditors()) return;
        if (TryReloadAppearanceInPlace(reopenSettings)) return;
        // Nothing salvageable happened in there, but the mode is already saved to disk - relaunching at least
        // leaves the user with the look they asked for rather than a half-swapped one.
        RestartToApplyTheme();
    }

    private static bool TryReloadAppearanceInPlace(bool reopenSettings)
    {
        if (Current is not { } app) return false;
        if (app.Windows.OfType<MainWindow>().FirstOrDefault(w => w.IsPrimaryShell) is not { } shell) return false;

        var previousShutdownMode = app.ShutdownMode;
        try
        {
            // The outgoing shell is closed while the new one is up, but nothing else may end the app in between -
            // OnLastWindowClose would treat a one-frame gap as "the user quit" and take every session with it.
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            ApplyThemeDictionaries(app.Resources);

            shell.ReloadShellForAppearanceChange(reopenSettings);
            return true;
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "AppearanceReload");
            return false;
        }
        finally
        {
            app.ShutdownMode = previousShutdownMode;
        }
    }

    /// <summary>
    /// The process is on its way out for a reason VibeCode itself decided: the notification icon's Quit, a theme or
    /// update restart, or Windows ending the session. "Run in background" turns the close button into a hide, so
    /// every one of those routes has to be able to say <em>this one is real</em> - otherwise the shell would hide
    /// from a shutdown it cannot cancel (WPF ignores Cancel during Application.Shutdown) and the app would exit
    /// having skipped the teardown that ends the provider processes.
    /// </summary>
    internal static bool IsExiting { get; private set; }

    /// <summary>Declare the exit before triggering it, so the shell's Closing handler tears sessions down instead of
    /// hiding. Safe to call more than once; the first reason recorded is the one the log keeps.</summary>
    internal static void BeginExit(string origin)
    {
        IsExiting = true;
        _shutdownOrigin ??= origin;
    }

    /// <summary>Restart the app to apply a theme change. The fallback for <see cref="ApplyAppearanceChange"/> when
    /// the in-place reload cannot run (no shell yet, or it threw): the normal Closing path persists open chats and
    /// session restore brings them back under the new theme, at the cost of every live agent.</summary>
    public static void RestartToApplyTheme()
    {
        if (!CodeViewerWindow.ConfirmCloseEditors()) return;
        _restartRequested = true;
        BeginExit("App.RestartToApplyTheme (mode/theme switch confirmed in Settings)");
        Current.Shutdown();
    }

    /// <summary>Close and come back up as a *different* executable - the freshly installed build from
    /// Settings &gt; About. The theme restart above relaunches Environment.ProcessPath, which is exactly the copy
    /// an update has just superseded, so the new path has to be carried explicitly. Everything else is identical:
    /// the normal Closing path persists open chats and session restore brings them back.</summary>
    public static void RestartInto(string executablePath)
    {
        if (!CodeViewerWindow.ConfirmCloseEditors()) return;
        _restartRequested = true;
        _restartTarget = executablePath;
        BeginExit($"App.RestartInto (update installed: {executablePath})");
        Current.Shutdown();
    }

    /// <summary>Last-chance state flush, registered by the main window. Application.Shutdown() (a mode change restarts
    /// the app that way) does not necessarily take the same path as closing the window, and this also covers an exit
    /// raised from anywhere else - persistence must not depend on which route the process took out.</summary>
    internal static Action? PersistOnExit;

    /// <summary>Run the state flush from anywhere, including a thread that is in the middle of crashing. Marshals to
    /// the UI thread (the view models are not thread-safe) but never waits forever - a wedged UI thread must not turn
    /// a crash into a hang.</summary>
    internal static void FlushStateSafely()
    {
        try
        {
            if (PersistOnExit is not { } flush) return;
            var dispatcher = Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess()) { flush(); return; }
            dispatcher.Invoke(flush, TimeSpan.FromSeconds(3));
        }
        catch { /* last-gasp save: whatever happens here, it must not make the crash worse */ }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Say WHY we are exiting. When nothing in VibeCode asked for it, WPF closed the app on its own - which under
        // the default ShutdownMode means the last window just closed - and that is worth seeing in the log.
        CrashLog.Note("Exit",
            $"exit code:        {e.ApplicationExitCode}{Environment.NewLine}" +
            $"restart queued:   {_restartRequested}{Environment.NewLine}" +
            $"shutdown mode:    {ShutdownMode}{Environment.NewLine}" +
            $"windows open:     {DescribeOpenWindows()}{Environment.NewLine}" +
            $"requested by:     {_shutdownOrigin ?? "(nothing in VibeCode called Shutdown - WPF ended the app itself)"}");

        _browserBridge?.Dispose();
        // remember: false — this is a shutdown, not the user switching phone access off, so the setting must survive.
        try { Services.PhoneBridgeService.Instance.Stop(remember: false); } catch { /* the socket dies with us anyway */ }
        try { PersistOnExit?.Invoke(); }
        catch { /* never turn a save failure into a crash on the way out */ }

        // _restartTarget is set only by an update; everything else comes back as the same executable.
        if (_restartRequested && (_restartTarget ?? Environment.ProcessPath) is { Length: > 0 } exe)
        {
            try { Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true }); }
            catch { /* the app still exits cleanly; the user can relaunch by hand */ }
        }
        CrashLog.ReleaseRunMarker();   // we got here, so the next start must not report this run as killed
        base.OnExit(e);
    }

    private static string DescribeOpenWindows()
    {
        try
        {
            var open = Current?.Windows.Cast<Window>().Select(w => w.GetType().Name).ToList();
            return open is { Count: > 0 } ? string.Join(", ", open) : "(none)";
        }
        catch { return "(unavailable)"; }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // A stdio server is a separate, headless role: never acquire the shell lease, load settings,
        // create a window, start memory/browser services, or disturb the running desktop instance.
        if (e.Args.Contains("--agent-status-mcp", StringComparer.Ordinal))
        {
            Environment.Exit(Task.Run(() => AgentStatus.Mcp.AgentStatusMcpHost.RunConsoleAsync()).GetAwaiter().GetResult());
            return;
        }
        PortableEnvironment.Configure();
        base.OnStartup(e);

        // Lifecycle breadcrumbs, written before anything else can go wrong. A run that is killed from outside
        // (Task Manager, a script running Stop-Process/taskkill) throws nothing, gets no Windows Error Reporting
        // entry and never reaches OnExit - so without the marker it left the log completely silent.
        foreach (var abandoned in CrashLog.ClaimRunMarker())
        {
            CrashLog.Note("PreviousRunWasKilled",
                "The previous run never reached its exit handler, so it did not close itself: it was killed from" +
                Environment.NewLine +
                "outside (Task Manager, or a script running Stop-Process / taskkill against VibeCode) or died hard." +
                Environment.NewLine + abandoned);
        }
        CrashLog.Note("Startup",
            $"exe {Environment.ProcessPath ?? "(unknown)"}{Environment.NewLine}args [{string.Join(' ', e.Args)}]");

        // One VibeCode per data directory, decided before anything can read or write settings.json. Two builds
        // running at once is how a setting the user changed once — Borderless, Groq speech-to-text — got quietly
        // republished from the other instance's stale copy of the whole document. See SingleInstance.
        //
        // Environment.Exit rather than Shutdown(): StartupUri creates MainWindow AFTER OnStartup returns, and a
        // second instance must not build a window, attach a tray icon or save anything on its way out.
        if (!SingleInstance.TryAcquire(out _instanceLease))
        {
            CrashLog.Note("SecondInstance",
                $"Another VibeCode already owns {AppSettings.Dir}. Asked it to come forward and exited without" +
                Environment.NewLine + "loading settings, so the two cannot overwrite each other's preferences.");
            Environment.Exit(0);
            return;
        }
        SingleInstance.ShowRequested += () => Dispatcher.BeginInvoke(new Action(BringPrimaryShellForward));

        AppDomain.CurrentDomain.ProcessExit += (_, _) => CrashLog.ReleaseRunMarker();
        SessionEnding += (_, args) =>
        {
            // Registered before the shell's own handler, so "run in background" cannot answer a logoff by hiding.
            BeginExit($"Windows session ending: {args.ReasonSessionEnding}");
            CrashLog.Note("SessionEnding", $"Windows is ending the session: {args.ReasonSessionEnding}");
        };

        // Swap the theme BEFORE StartupUri creates MainWindow - every window resolves theme brushes with
        // StaticResource at load, so the merged dictionary must already hold the right theme (and, when the
        // borderless look is on, the overlay that empties out the chrome's surfaces).
        if (AppSettings.IsCliMode || AppSettings.IsBorderless) ApplyThemeDictionaries(Resources);

        ApplyWineCompatibility();

        // Hand every text control a VibeCode right-click menu. Must run before the first window is created:
        // it works by class handler on Loaded, and a control that comes up without one falls back to WPF's
        // stock light-themed editing popup (white text on white, on every surface in this app).
        TextEditMenu.Install();

        // The Games menu owns a richer session-aware presenter than the original inline placeholder. Attach it
        // when the StartupUri-created main window finishes loading; class handling also covers a recreated window.
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnMainWindowLoaded));

        // Background-thread fatal: flush chats AND dump a crash log before the runtime kills the process.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            try
            {
                CrashLog.Write(args.ExceptionObject as Exception, "AppDomain.UnhandledException",
                    isTerminating: args.IsTerminating);
            }
            catch { /* never throw from a crash handler */ }
            FlushStateSafely();
        };

        // Unobserved task faults (async work that nobody awaited) — log without taking the process down.
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            try { CrashLog.Write(args.Exception, "TaskScheduler.UnobservedTaskException"); }
            catch { /* ignore */ }
            args.SetObserved();
        };

        ReportSettingsRecovery();

        // The bundled Browser plugin discovers native backends before it asks for a tab. Publishing the bridge at
        // application startup makes browser capability deterministic for every Codex chat, while WebView2 itself is
        // still created lazily only when an agent or the user opens the first browser tab.
        if (Environment.GetEnvironmentVariable("VIBECODE_DISABLE_BROWSER_BRIDGE") != "1")
        {
            _browserBridge = new BrowserBridgeService(Dispatcher);
            _browserBridge.Start();
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    /// <summary>
    /// Everything the WPF interface needs that a Wine prefix does not provide. No-op on Windows.
    /// </summary>
    private void ApplyWineCompatibility()
    {
        if (!IsWineCompatibility) return;

        // Wine may expose Windows bitmap-font registry entries whose .fon files are unavailable to WPF's
        // DirectWrite path. If every requested family is unresolved, WPF's text mapper falls back to Arial and
        // terminates the process when that family is absent too. These app-scoped physical families keep every
        // text run on a real OpenType font installed by install-kali.sh. VibeCode Linux Icons is an MIT-licensed
        // Fluent System Icons build carrying every Segoe PUA codepoint this app draws - and the emoji it writes
        // straight into strings, which is why it trails each text family rather than only backing the icon one.
        Resources["Ui"] = new FontFamily("DejaVu Sans, VibeCode Linux Icons");
        Resources["Display"] = new FontFamily("DejaVu Sans, VibeCode Linux Icons");
        Resources["Mono"] = new FontFamily("DejaVu Sans Mono, VibeCode Linux Icons");
        Resources["Icons"] = new FontFamily("VibeCode Linux Icons, DejaVu Sans");

        // install-kali.sh places the official Windows provider runtimes and PortableGit in one prefix-owned
        // directory. Wine does not merge Linux PATH entries into the Windows search path consistently, so make
        // that deterministic before any account probe or CLI process starts.
        if (Environment.GetEnvironmentVariable("VIBECODE_WINE_TOOL_PATH") is { Length: > 0 } toolPath)
        {
            var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            Environment.SetEnvironmentVariable("PATH",
                toolPath.TrimEnd(';') + (currentPath.Length > 0 ? ";" + currentPath : ""));
        }

        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        CrashLog.Note("WineCompatibility",
            "Using DejaVu text, the packaged Fluent-compatible icon and emoji font, external-browser fallbacks, " +
            "and WPF software rendering.");
    }

    private static void OnMainWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MainWindow owner) return;
        GamesPopupCard.AttachTo(owner);
        GameWindow.MaybeAutoOpenForSmoke(owner);
        UsageDashboardWindow.MaybeAutoOpenForSmoke(owner);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs args)
    {
        args.Handled = true;
        // Log + save BEFORE showing the dialog: the app is in an unknown state and the user may kill it from the
        // error box rather than continue, which would otherwise discard everything since the last autosave.
        var logPath = CrashLog.Write(args.Exception, "DispatcherUnhandledException");
        FlushStateSafely();

        var message = DescribeException(args.Exception);
        var now = DateTime.UtcNow;
        if (_showingDispatcherError ||
            (string.Equals(message, _lastDispatcherError, StringComparison.Ordinal) &&
             now - _lastDispatcherErrorAt < TimeSpan.FromSeconds(5)))
        {
            return;
        }

        _lastDispatcherError = message;
        _lastDispatcherErrorAt = now;
        _showingDispatcherError = true;
        try
        {
            var body = message;
            if (logPath is not null)
                body += Environment.NewLine + Environment.NewLine + "Crash log written to:" + Environment.NewLine + logPath;
            MessageBox.Show(body, "VibeCode error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _showingDispatcherError = false;
        }
    }

    /// <summary>Say so when settings could not be read normally. Silence here is what made a lost settings.json look
    /// like "the app reset itself for no reason" - and the unreadable file is kept, so it can still be inspected.</summary>
    private static void ReportSettingsRecovery()
    {
        if (Environment.GetEnvironmentVariable("VIBECODE_HIDDEN") == "1") return;   // automated runs get no modal
        var quarantined = AppSettings.QuarantinedPath;
        var message = AppSettings.StartupOutcome switch
        {
            AppSettings.LoadOutcome.RecoveredHistory =>
                "VibeCode recovered saved chats and projects from settings files that an earlier version incorrectly marked as damaged.\n\n" +
                "Your current preferences were kept, and the original recovery files are still available in your VibeCode data folder.",
            AppSettings.LoadOutcome.RecoveredFromBackup =>
                "VibeCode's settings file could not be read, so your previous saved copy was restored.\n\n" +
                "Your chats, bridges and preferences are back as of the last successful save." +
                (quarantined is null ? "" : $"\n\nThe damaged file was kept at:\n{quarantined}"),
            AppSettings.LoadOutcome.Quarantined =>
                "VibeCode's settings file was damaged and no usable backup was found, so it started with defaults.\n\n" +
                "Nothing was deleted - the damaged file was moved aside rather than overwritten" +
                (quarantined is null ? "." : $":\n{quarantined}"),
            _ => null,
        };
        if (message is null) return;
        MessageBox.Show(message, "VibeCode settings", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static string DescribeException(Exception exception)
    {
        var messages = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (!string.IsNullOrWhiteSpace(current.Message) &&
                !messages.Contains(current.Message, StringComparer.Ordinal))
            {
                messages.Add(current.Message);
            }
        }

        return string.Join(Environment.NewLine, messages);
    }
}
