using System.Diagnostics;
using System.Windows;
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
    private static string? _shutdownOrigin;

    /// <summary>Restart the app to apply a theme change. The theme dictionary is chosen once at startup
    /// (windows resolve it with StaticResource), so a mode switch saves settings and relaunches: the normal
    /// Closing path persists open chats and session restore brings them back under the new theme.</summary>
    public static void RestartToApplyTheme()
    {
        _restartRequested = true;
        _shutdownOrigin = "App.RestartToApplyTheme (mode/theme switch confirmed in Settings)";
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
        try { PersistOnExit?.Invoke(); }
        catch { /* never turn a save failure into a crash on the way out */ }

        if (_restartRequested && Environment.ProcessPath is { Length: > 0 } exe)
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

        AppDomain.CurrentDomain.ProcessExit += (_, _) => CrashLog.ReleaseRunMarker();
        SessionEnding += (_, args) =>
            CrashLog.Note("SessionEnding", $"Windows is ending the session: {args.ReasonSessionEnding}");

        // Swap the theme BEFORE StartupUri creates MainWindow - every window resolves theme brushes with
        // StaticResource at load, so the merged dictionary must already hold the right theme.
        if (AppSettings.IsCliMode)
        {
            Resources.MergedDictionaries.Clear();
            Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/Themes/Cli.xaml")
            });
        }

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

    private static void OnMainWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MainWindow owner) return;
        GamesPopupCard.AttachTo(owner);
        GameWindow.MaybeAutoOpenForSmoke(owner);
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
