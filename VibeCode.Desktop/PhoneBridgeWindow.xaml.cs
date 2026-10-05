using System.ComponentModel;
using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using VibeCode.Services;

namespace VibeCode;

/// <summary>
/// The pairing and status surface for <see cref="PhoneBridgeService"/>.
///
/// Shows listener state, local setup checks and pairing. Phone access starts only after it is enabled; that
/// preference is restored on app launch. Local checks do not establish reachability from a phone or private VPN.
/// </summary>
public partial class PhoneBridgeWindow : Window
{
    private static PhoneBridgeWindow? _open;
    private readonly DispatcherTimer _tick;
    private readonly PhoneBridgeService _bridge = PhoneBridgeService.Instance;
    private bool _closed;
    private bool _diagnosticsBusy;
    private bool _diagnosticsAgain;

    public PhoneBridgeWindow()
    {
        InitializeComponent();
        PortBox.Text = _bridge.Port.ToString();
        _bridge.PropertyChanged += OnBridgeChanged;
        _bridge.Devices.CollectionChanged += OnDevicesChanged;

        // Only runs while this window is open — the countdown is the only thing that needs a per-second tick and
        // there is no reason for it to exist when nobody is watching a code expire.
        _tick = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) => _bridge.TickPairingCountdown();
        _tick.Start();

        Refresh(recheck: true);
    }

    /// <summary>Shows the window, or brings the existing one forward. One bridge, one window.</summary>
    public static void Open(Window owner)
    {
        if (_open is not null)
        {
            if (_open.WindowState == WindowState.Minimized) _open.WindowState = WindowState.Normal;
            _open.Activate();
            return;
        }
        _open = new PhoneBridgeWindow { Owner = owner };
        _open.Show();
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _tick.Stop();
        _bridge.PropertyChanged -= OnBridgeChanged;
        _bridge.Devices.CollectionChanged -= OnDevicesChanged;
        // Leaving pairing open behind a closed window would mean a code nobody can read is still accepted.
        _bridge.ClosePairing();
        _open = null;
        base.OnClosed(e);
    }

    private void OnBridgeChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Countdown bindings update themselves. Do not run COM and VPN processes every second.
        if (e.PropertyName == nameof(PhoneBridgeService.PairingCountdown)) return;
        Refresh(recheck: e.PropertyName is nameof(PhoneBridgeService.Running) or nameof(PhoneBridgeService.Port));
    }

    private void OnDevicesChanged(object? sender, NotifyCollectionChangedEventArgs e) => Refresh();

    private void Refresh(bool recheck = false)
    {
        PowerButton.Content = _bridge.Running ? "Turn off" : "Turn on";
        PairIdlePanel.Visibility = _bridge.Running && !_bridge.PairingOpen ? Visibility.Visible : Visibility.Collapsed;
        NoDevices.Visibility = _bridge.Devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ErrorLine.Text = _bridge.Error;
        ErrorLine.Visibility = _bridge.HasError ? Visibility.Visible : Visibility.Collapsed;
        RefreshApk();
        if (recheck)
        {
            _bridge.RefreshAddresses();
            RefreshDiagnostics();
        }
    }

    /// <summary>
    /// Rebuilds the "why can't my phone see this PC" list.
    ///
    /// This is the panel that exists because the failure it describes is invisible: a VPN blocking the LAN, a
    /// missing firewall rule and a healthy-but-idle bridge all look exactly the same from the handset, which shows
    /// a spinner and nothing else. Anything with a fix gets a button, because telling a user to go and edit
    /// firewall rules by hand is not an answer.
    /// </summary>
    private void RefreshDiagnostics()
    {
        // A window can be constructed before the normal application dispatcher loop installs its context.
        // Enter through the dispatcher so every continuation below returns to the window's owning thread.
        Dispatcher.BeginInvoke(new Action(RefreshDiagnosticsAsync));
    }

    private async void RefreshDiagnosticsAsync()
    {
        if (_closed) return;
        if (_diagnosticsBusy)
        {
            _diagnosticsAgain = true;
            return;
        }
        _diagnosticsBusy = true;
        DiagnosticsCard.Visibility = Visibility.Visible;
        DiagnosticsCheckButton.IsEnabled = false;
        try
        {
            do
            {
                _diagnosticsAgain = false;
                DiagnosticsTitle.Text = "Checking this PC…";
                List<PhoneProblem> problems;
                try
                {
                    problems = await Task.Run(PhoneReachability.Check);
                }
                catch (Exception ex)
                {
                    CrashLog.Note("PhoneBridge", $"reachability check failed - {ex.Message}");
                    problems = new List<PhoneProblem>
                    {
                        new("Local checks could not finish", "Try Check again. Phone and remote connectivity have not been verified.", Blocking: false),
                    };
                }
                if (_closed) return;

                DiagnosticsList.Children.Clear();
                DiagnosticsTitle.Text = problems.Any(p => p.Blocking)
                    ? "Connection setup needs attention"
                    : "Local connection checks";
                if (problems.Count == 0)
                    problems.Add(new PhoneProblem("No local setup issue detected",
                        "This check does not test your phone's route or remote access. Connect from the phone to verify it.", Blocking: false));
                foreach (var problem in problems) DiagnosticsList.Children.Add(BuildProblemRow(problem));
            } while (_diagnosticsAgain);
        }
        finally
        {
            _diagnosticsBusy = false;
            if (!_closed) DiagnosticsCheckButton.IsEnabled = true;
        }
    }

    private UIElement BuildProblemRow(PhoneProblem problem)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = problem.Title,
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (System.Windows.Media.Brush)FindResource(problem.Blocking ? "Red" : "Amber"),
        });
        stack.Children.Add(new TextBlock
        {
            Text = problem.Detail,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 0, 0),
            Foreground = (System.Windows.Media.Brush)FindResource("Muted"),
        });

        if (problem is { FixLabel: { } label, Fix: { } fix })
        {
            var status = new TextBlock
            {
                FontSize = 10.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0),
                Visibility = Visibility.Collapsed,
                Foreground = (System.Windows.Media.Brush)FindResource("Red"),
            };
            var button = new Button
            {
                Content = label,
                Style = (Style)FindResource("PrimaryButton"),
                Padding = new Thickness(14, 6, 14, 6),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 9, 0, 0),
            };
            button.Click += (_, _) =>
            {
                button.IsEnabled = false;
                string result;
                try
                {
                    Mouse.OverrideCursor = Cursors.Wait;
                    result = fix();
                }
                catch (Exception ex)
                {
                    result = ex.Message;
                }
                finally
                {
                    Mouse.OverrideCursor = null;
                }

                if (result.Length == 0)
                {
                    // Believe the system, not the exit code: re-run every check and let the panel redraw itself.
                    Refresh(recheck: true);
                    return;
                }
                status.Text = result;
                status.Visibility = Visibility.Visible;
                button.IsEnabled = true;
            };
            stack.Children.Add(button);
            stack.Children.Add(status);
        }

        return new Border
        {
            Background = (System.Windows.Media.Brush)FindResource("Bg2"),
            CornerRadius = (CornerRadius)FindResource("Rad8"),
            Padding = new Thickness(12, 10, 12, 11),
            Margin = new Thickness(0, 0, 0, 8),
            Child = stack,
        };
    }

    private void OnRecheck(object sender, RoutedEventArgs e) => Refresh(recheck: true);

    /// <summary>One generated app as the window lists it.</summary>
    public sealed record ApkRow(string Id, string Path, string State, System.Windows.Media.Brush StateBrush,
        bool CanCancel, bool HasFile)
    {
        public Visibility CancelVisibility => CanCancel ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ShowVisibility => HasFile ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Recently claimed or expired apps listed under the waiting ones, so "which phone used which file"
    /// stays answerable without the list growing forever.</summary>
    private const int ShownFinishedApps = 3;

    private void RefreshApk()
    {
        BuildApkButton.IsEnabled = PhoneApkBuilder.TemplateAvailable;
        if (!PhoneApkBuilder.TemplateAvailable)
        {
            ApkList.ItemsSource = null;
            BuildApkButton.Content = "Generate the app";
            ApkHintText.Text = "This build of VibeCode does not include the phone app, so there is nothing to "
                               + "generate. Pair by hand below instead.";
            return;
        }

        var enrolments = _bridge.Enrolments.Where(e => e.ApkPath.Length > 0).ToList();
        var shown = enrolments.Where(e => e.Pending)
            .Concat(enrolments.Where(e => !e.Pending).Take(ShownFinishedApps))
            .Select(e => new ApkRow(e.Id, e.ApkPath, ApkState(e),
                (System.Windows.Media.Brush)FindResource(e.Pending ? "Amber" : "Faint"),
                e.Pending, File.Exists(e.ApkPath)))
            .ToList();
        ApkList.ItemsSource = shown;
        BuildApkButton.Content = shown.Count > 0 || _bridge.Devices.Count > 0 ? "Generate an app for another phone" : "Generate the app";
        ApkHintText.Text = shown.Count > 0 || _bridge.Devices.Count > 0
            ? "Copy a file to the phone it is for and open it there. Each file sets up exactly one phone, so make one per "
              + "phone — every phone you pair keeps working alongside the others."
            : "Copy the file to your phone and open it. Android will ask you to allow installing from this source — that "
              + "is expected for an app that did not come from the Play Store.";
    }

    /// <summary>The distinction that matters: an app nobody has installed yet is a live credential sitting in a file,
    /// and one that has been claimed is inert. Say which.</summary>
    private static string ApkState(PhoneEnrolment enrolment) => enrolment.Pending
        ? $"Not set up yet. Expires {enrolment.ExpiresAt.ToLocalTime():g}. The FIRST phone to open it claims it. "
          + "Until then, treat the file like a key."
        : enrolment.UsedAt is null
            ? "This enrollment has expired. Generate a new app on this PC."
        : $"Claimed by \"{enrolment.DeviceName}\" on {enrolment.UsedAt:g}. That is the only phone this file will ever "
          + "work on — anyone else who opens it is refused, even with the file in hand.";

    private void OnBuildApk(object sender, RoutedEventArgs e)
    {
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            var path = PhoneApkBuilder.Build();
            Refresh();
            Reveal(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Phone", MessageBoxButton.OK, MessageBoxImage.Warning);
            CrashLog.Note("PhoneBridge", $"APK generation failed - {ex}");
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    private void OnShowApk(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ApkRow row } && File.Exists(row.Path)) Reveal(row.Path);
    }

    private void OnRevokeApk(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ApkRow row }) return;
        var answer = MessageBox.Show(this,
            $"Cancel {System.IO.Path.GetFileName(row.Path)}? The file stays on disk but can no longer set up a phone. "
            + "Other generated apps and paired phones are not affected.",
            "Phone", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK) return;
        _bridge.RevokeEnrolment(row.Id);
        Refresh();
    }

    private static void Reveal(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe",
                $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            CrashLog.Note("PhoneBridge", $"could not reveal the generated APK - {ex.Message}");
        }
    }

    private void OnTogglePower(object sender, RoutedEventArgs e)
    {
        if (_bridge.Running) _bridge.Stop();
        else _bridge.Start();
        PortBox.Text = _bridge.Port.ToString();
        Refresh();
    }

    private void OnStartPairing(object sender, RoutedEventArgs e)
    {
        _bridge.OpenPairing();
        Refresh();
    }

    private void OnCancelPairing(object sender, RoutedEventArgs e)
    {
        _bridge.ClosePairing();
        Refresh();
    }

    private void OnApplyPort(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1024 or > 65535)
        {
            MessageBox.Show(this, "Pick a port between 1024 and 65535.", "Phone",
                MessageBoxButton.OK, MessageBoxImage.Information);
            PortBox.Text = _bridge.Port.ToString();
            return;
        }
        _bridge.SetPort(port);
        PortBox.Text = _bridge.Port.ToString();
        Refresh();
    }

    private void OnRevoke(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: PhoneDevice device }) return;
        var answer = MessageBox.Show(this,
            $"Revoke \"{device.Name}\"? It loses access immediately and has to pair again to get it back.",
            "Phone", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK) return;
        _bridge.Revoke(device);
        Refresh();
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            "This throws away the security key this PC identifies itself with and unpairs every phone. "
            + "Each one will have to pair again from scratch.\r\n\r\nContinue?",
            "Phone", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK) return;
        _bridge.ResetEverything();
        Refresh();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
