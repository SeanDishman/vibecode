using System.ComponentModel;
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
/// Everything security-relevant is deliberately explicit here rather than buried in Settings: the listener is off
/// until this window turns it on, the pairing code only exists while this window is showing it, and the safety
/// code the phone must match is on screen next to it. A user who never opens this window never has an open port.
/// </summary>
public partial class PhoneBridgeWindow : Window
{
    private static PhoneBridgeWindow? _open;
    private readonly DispatcherTimer _tick;
    private readonly PhoneBridgeService _bridge = PhoneBridgeService.Instance;

    public PhoneBridgeWindow()
    {
        InitializeComponent();
        PortBox.Text = _bridge.Port.ToString();
        _bridge.PropertyChanged += OnBridgeChanged;
        _bridge.Devices.CollectionChanged += (_, _) => Refresh();

        // Only runs while this window is open — the countdown is the only thing that needs a per-second tick and
        // there is no reason for it to exist when nobody is watching a code expire.
        _tick = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) => _bridge.TickPairingCountdown();
        _tick.Start();

        Refresh();
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
        _tick.Stop();
        _bridge.PropertyChanged -= OnBridgeChanged;
        // Leaving pairing open behind a closed window would mean a code nobody can read is still accepted.
        _bridge.ClosePairing();
        _open = null;
        base.OnClosed(e);
    }

    private void OnBridgeChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        PowerButton.Content = _bridge.Running ? "Turn off" : "Turn on";
        PairIdlePanel.Visibility = _bridge.Running && !_bridge.PairingOpen ? Visibility.Visible : Visibility.Collapsed;
        NoDevices.Visibility = _bridge.Devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ErrorLine.Text = _bridge.Error;
        ErrorLine.Visibility = _bridge.HasError ? Visibility.Visible : Visibility.Collapsed;
        RefreshApk();
        RefreshDiagnostics();
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
        List<PhoneProblem> problems;
        try
        {
            problems = PhoneReachability.Check();
        }
        catch (Exception ex)
        {
            CrashLog.Note("PhoneBridge", $"reachability check failed - {ex.Message}");
            DiagnosticsCard.Visibility = Visibility.Collapsed;
            return;
        }

        DiagnosticsList.Children.Clear();
        if (problems.Count == 0)
        {
            DiagnosticsCard.Visibility = Visibility.Collapsed;
            return;
        }

        DiagnosticsCard.Visibility = Visibility.Visible;
        DiagnosticsTitle.Text = problems.Any(p => p.Blocking)
            ? "Your phone will not be able to reach this PC"
            : "Worth knowing";

        foreach (var problem in problems) DiagnosticsList.Children.Add(BuildProblemRow(problem));
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
                    Refresh();
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

    private void OnRecheck(object sender, RoutedEventArgs e) => Refresh();

    private void RefreshApk()
    {
        var enrolment = _bridge.Enrolment;
        var haveFile = enrolment is not null && enrolment.ApkPath.Length > 0 && File.Exists(enrolment.ApkPath);

        ApkReadyPanel.Visibility = haveFile ? Visibility.Visible : Visibility.Collapsed;
        BuildApkButton.Content = haveFile ? "Generate a new one" : "Generate the app";
        BuildApkButton.IsEnabled = PhoneApkBuilder.TemplateAvailable;

        if (!PhoneApkBuilder.TemplateAvailable)
        {
            ApkHintText.Text = "This build of VibeCode does not include the phone app, so there is nothing to "
                               + "generate. Pair by hand below instead.";
            return;
        }

        if (haveFile)
        {
            ApkPathText.Text = enrolment!.ApkPath;
            // The distinction that matters: an app nobody has installed yet is a live credential sitting in a
            // file, and one that has been claimed is inert. Say which.
            ApkStateText.Text = enrolment.Pending
                ? "Not set up yet. The FIRST phone to open it claims it — after that the same file cannot set up "
                  + "any other phone, ever. Until then, treat it like a key."
                : $"Claimed by \"{enrolment.DeviceName}\" on {enrolment.UsedAt:g}. That is the only phone this file "
                  + "will ever work on — anyone else who opens it is refused, even with the file in hand.";
            ApkStateText.Foreground = enrolment.Pending
                ? (System.Windows.Media.Brush)FindResource("Amber")
                : (System.Windows.Media.Brush)FindResource("Faint");
            RevokeApkButton.Visibility = enrolment.Pending ? Visibility.Visible : Visibility.Collapsed;
            ApkHintText.Text = "Copy the file to your phone and open it. Generating a new one immediately stops "
                               + "the previous file from being able to set up a phone.";
        }
    }

    private void OnBuildApk(object sender, RoutedEventArgs e)
    {
        if (_bridge.Enrolment?.Pending == true)
        {
            var confirm = MessageBox.Show(this,
                "The app you generated last time has not been set up on a phone yet. Generating a new one stops "
                + "that file from working.\r\n\r\nContinue?",
                "Phone", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.OK) return;
        }

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
        var path = _bridge.Enrolment?.ApkPath;
        if (!string.IsNullOrEmpty(path) && File.Exists(path)) Reveal(path);
    }

    private void OnRevokeApk(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            "Cancel the generated app? The file stays on disk but can no longer set up a phone.",
            "Phone", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK) return;
        _bridge.RevokeEnrolment();
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
