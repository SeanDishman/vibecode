using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using VibeCode.Services;

namespace VibeCode.UI;

/// <summary>
/// A persistent, user-visible WebView owned by VibeCode. The browser bridge controls the same surface through
/// Chromium's DevTools protocol, so agent actions and the page the user sees can never drift apart.
/// </summary>
public partial class BrowserWindow : Window
{
    /// <summary>Far outside any physical monitor, matching the bridge's own hidden-window test hook.</summary>
    private const double OffScreenPosition = -32000;

    private string _sessionName = "Agent browser";
    private double _restoreLeft = double.NaN;
    private double _restoreTop = double.NaN;
    private readonly HashSet<string> _allowedDownloads = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Pre-authorize one download the user has already approved through the agent's download flow. Chromium's
    /// Browser.setDownloadBehavior is rejected outright by WebView2, so the grant is recorded here and applied
    /// in the native DownloadStarting event instead.
    /// </summary>
    internal void AllowDownload(string url) => _allowedDownloads.Add(url);

    private void OnDownloadStarting(CoreWebView2DownloadStartingEventArgs args)
    {
        // Only redirect downloads the agent flow pre-authorized. A download the user started themselves in this
        // window keeps WebView2's normal behavior and its normal destination.
        if (!_allowedDownloads.Remove(args.DownloadOperation.Uri)) return;

        var directory = Path.Combine(AppSettings.Dir, "browser", "downloads");
        Directory.CreateDirectory(directory);
        var name = Path.GetFileName(args.ResultFilePath);
        if (string.IsNullOrWhiteSpace(name)) name = "download";
        args.ResultFilePath = Path.Combine(directory, name);
        // Suppress the download UI: this file was requested by an agent, not by someone watching the window.
        args.Handled = true;
    }

    /// <summary>Whether the user can actually see this browser, as opposed to it merely existing.</summary>
    internal bool IsOnScreen => IsVisible && Left > OffScreenPosition / 2;

    /// <summary>
    /// Backs the plugin's browser "visibility" capability. Hiding parks the window off every monitor instead of
    /// calling Hide(): WebView2 stops rendering an actually-hidden window, which would silently turn the agent's
    /// screenshots blank while every call still reported success.
    /// </summary>
    internal void SetOnScreen(bool onScreen)
    {
        if (onScreen)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            if (!double.IsNaN(_restoreLeft)) Left = _restoreLeft;
            if (!double.IsNaN(_restoreTop)) Top = _restoreTop;
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Show();
            Activate();
            return;
        }

        if (IsOnScreen)
        {
            _restoreLeft = Left;
            _restoreTop = Top;
        }
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = OffScreenPosition;
        Top = OffScreenPosition;
    }

    internal BrowserWindow()
    {
        InitializeComponent();
        Closed += (_, _) => BrowserView.Dispose();
    }

    internal CoreWebView2 Core => BrowserView.CoreWebView2
        ?? throw new InvalidOperationException("The browser is still starting.");

    internal string CurrentUrl => BrowserView.CoreWebView2?.Source ?? "about:blank";
    internal string CurrentTitle => BrowserView.CoreWebView2?.DocumentTitle ?? "New tab";

    internal async Task InitializeAsync(CoreWebView2Environment environment, string? initialUrl = null)
    {
        try
        {
            await BrowserView.EnsureCoreWebView2Async(environment);
            var core = Core;
            core.Settings.AreDevToolsEnabled = true;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.AreBrowserAcceleratorKeysEnabled = true;
            core.Settings.IsStatusBarEnabled = true;

            core.SourceChanged += (_, _) => RefreshChrome();
            core.DocumentTitleChanged += (_, _) => RefreshChrome();
            core.HistoryChanged += (_, _) => RefreshChrome();
            core.NavigationCompleted += (_, _) => RefreshChrome();
            core.NewWindowRequested += (_, args) =>
            {
                // Keep target=_blank links on the controlled surface. Agent-created tabs still use the bridge's
                // explicit createTab operation and therefore remain independently addressable.
                args.Handled = true;
                if (!string.IsNullOrWhiteSpace(args.Uri)) core.Navigate(args.Uri);
            };
            core.DownloadStarting += (_, args) => OnDownloadStarting(args);
            core.ProcessFailed += (_, args) =>
            {
                LoadingDetail.Text = $"Browser process stopped ({args.ProcessFailedKind}). Close this tab and retry.";
                LoadingOverlay.Visibility = Visibility.Visible;
            };

            LoadingOverlay.Visibility = Visibility.Collapsed;
            core.Navigate(string.IsNullOrWhiteSpace(initialUrl) ? "about:blank" : initialUrl);
            RefreshChrome();
        }
        catch (Exception ex)
        {
            LoadingDetail.Text = "Couldn't start WebView2: " + ex.Message;
            LoadingOverlay.Visibility = Visibility.Visible;
            throw;
        }
    }

    internal void SetSessionName(string? name)
    {
        _sessionName = string.IsNullOrWhiteSpace(name) ? "Agent browser" : name.Trim();
        RefreshChrome();
    }

    internal void Navigate(string address)
    {
        var value = address.Trim();
        if (value.Length == 0) return;
        if (!Uri.TryCreate(value, UriKind.Absolute, out _)) value = "https://" + value;
        Core.Navigate(value);
    }

    private void RefreshChrome()
    {
        if (BrowserView.CoreWebView2 is not { } core) return;
        if (!AddressBox.IsKeyboardFocusWithin) AddressBox.Text = core.Source;
        BackButton.IsEnabled = core.CanGoBack;
        ForwardButton.IsEnabled = core.CanGoForward;
        var pageTitle = string.IsNullOrWhiteSpace(core.DocumentTitle) ? "New tab" : core.DocumentTitle;
        Title = $"{pageTitle} — {_sessionName} — VibeCode Browser";
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (BrowserView.CoreWebView2?.CanGoBack == true) BrowserView.CoreWebView2.GoBack();
    }

    private void OnForward(object sender, RoutedEventArgs e)
    {
        if (BrowserView.CoreWebView2?.CanGoForward == true) BrowserView.CoreWebView2.GoForward();
    }

    private void OnReload(object sender, RoutedEventArgs e) => BrowserView.CoreWebView2?.Reload();

    private void OnAddressKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        Navigate(AddressBox.Text);
        BrowserView.Focus();
    }
}
