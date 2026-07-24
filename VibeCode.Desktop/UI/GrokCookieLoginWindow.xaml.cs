using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using VibeCode.Services;

namespace VibeCode.UI;

/// <summary>
/// Isolated WebView2 surface used only to exchange pasted Grok cookies for the official CLI's OAuth token.
/// The temporary profile is cleared and removed after each attempt.
/// </summary>
public partial class GrokCookieLoginWindow : Window
{
    private const string ProfilePrefix = "VibeCode-GrokAuth-";
    private string? _profileDirectory;
    private bool _closeIsProgrammatic;
    private bool _cleanupScheduled;

    public GrokCookieLoginWindow()
    {
        InitializeComponent();
        Closing += (_, _) =>
        {
            if (!_closeIsProgrammatic) UserCancelled?.Invoke(this, EventArgs.Empty);
        };
        Closed += (_, _) =>
        {
            BrowserView.Dispose();
            ScheduleProfileDeletion();
        };
    }

    public event EventHandler? UserCancelled;

    internal async Task InitializeAsync(IReadOnlyList<GrokBrowserCookie> cookies, string loginUrl)
    {
        if (!GrokAccountLoginService.TryGetTrustedLoginUrl(loginUrl, out var trustedUrl))
            throw new InvalidOperationException("Grok returned an untrusted sign-in address.");

        _profileDirectory = Path.Combine(Path.GetTempPath(), ProfilePrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_profileDirectory);
        var environment = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: _profileDirectory);
        await BrowserView.EnsureCoreWebView2Async(environment);

        var core = BrowserView.CoreWebView2
                   ?? throw new InvalidOperationException("The private sign-in browser did not start.");
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = true;
        core.Settings.IsStatusBarEnabled = true;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;

        core.NavigationStarting += (_, args) =>
        {
            if (!IsAllowedNavigation(args.Uri)) args.Cancel = true;
        };
        core.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            if (IsAllowedNavigation(args.Uri)) core.Navigate(args.Uri);
        };
        core.NavigationCompleted += (_, _) => LoadingOverlay.Visibility = Visibility.Collapsed;
        core.ProcessFailed += (_, _) =>
        {
            LoadingText.Text = "The private sign-in browser stopped. Close it and try again.";
            LoadingOverlay.Visibility = Visibility.Visible;
        };

        var acceptedSso = 0;
        foreach (var imported in cookies)
        {
            try
            {
                var browserCookie = core.CookieManager.CreateCookie(
                    imported.Name, imported.Value, imported.Domain, imported.Path);
                browserCookie.IsSecure = true;
                browserCookie.IsHttpOnly = imported.IsHttpOnly;
                if (imported.Expires is { } expires) browserCookie.Expires = expires.UtcDateTime;
                browserCookie.SameSite = imported.SameSite switch
                {
                    "strict" => CoreWebView2CookieSameSiteKind.Strict,
                    "none" => CoreWebView2CookieSameSiteKind.None,
                    _ => CoreWebView2CookieSameSiteKind.Lax,
                };
                core.CookieManager.AddOrUpdateCookie(browserCookie);
                if (imported.Name.Equals("sso", StringComparison.OrdinalIgnoreCase)
                    || imported.Name.Equals("sso-rw", StringComparison.OrdinalIgnoreCase))
                    acceptedSso++;
            }
            catch
            {
                // One stale or browser-specific cookie must not expose its value or abort other valid SSO entries.
            }
            finally
            {
                imported.ClearSecret();
            }
        }

        if (acceptedSso == 0)
            throw new InvalidOperationException("No compatible Grok SSO cookie could be loaded.");

        LoadingText.Text = "Opening the official xAI authorization page...";
        core.Navigate(trustedUrl);
    }

    internal async Task ClearAndCloseAsync()
    {
        if (_closeIsProgrammatic) return;
        _closeIsProgrammatic = true;
        try
        {
            if (BrowserView.CoreWebView2 is { } core)
                await core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile);
        }
        catch
        {
            // The profile directory is also removed after the WebView process releases it.
        }
        BrowserView.Dispose();
        Close();
    }

    private static bool IsAllowedNavigation(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme == Uri.UriSchemeHttps) return true;
        return uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void ScheduleProfileDeletion()
    {
        if (_cleanupScheduled || _profileDirectory is not { Length: > 0 } profile) return;
        _cleanupScheduled = true;
        _profileDirectory = null;
        _ = Task.Run(async () =>
        {
            if (!IsOwnedTemporaryProfile(profile)) return;
            for (var attempt = 0; attempt < 8; attempt++)
            {
                try
                {
                    if (Directory.Exists(profile)) Directory.Delete(profile, recursive: true);
                    return;
                }
                catch
                {
                    await Task.Delay(250 * (attempt + 1));
                }
            }
        });
    }

    private static bool IsOwnedTemporaryProfile(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            return string.Equals(Path.GetDirectoryName(fullPath), tempRoot, StringComparison.OrdinalIgnoreCase)
                   && Path.GetFileName(fullPath).StartsWith(ProfilePrefix, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }
}
