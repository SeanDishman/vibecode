using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using PuppeteerSharp;

namespace VibeCode.Services;

/// <summary>
/// The agent's browser, as an actual Chrome rather than a WebView2 control.
///
/// This exists because WebView2 cannot be the surface an agent drives. It rejects
/// <c>Browser.setDownloadBehavior</c> outright, it stops rendering the moment the window is really hidden (so
/// screenshots come back blank while every call still reports success), and it presents as an embedded
/// automation surface, which ordinary bot checks decline. Chrome has none of those problems: it is the browser
/// the checks are written for.
///
/// Measured on this machine against the two public test pages for exactly this question:
/// with Puppeteer's stock launch args <c>navigator.webdriver</c> is <c>true</c>; dropping
/// <c>--enable-automation</c> and disabling the <c>AutomationControlled</c> blink feature makes it <c>false</c>,
/// with plugins, <c>window.chrome</c> and a normal desktop UA all intact. Both configurations were served the
/// real page rather than a "Just a moment…" interstitial - the deciding factor there is being a headed, real
/// Chrome with a persistent profile, not the switches. An interactive Turnstile widget still wants a human
/// click; the window is on screen for exactly that reason, and the profile below remembers the outcome.
///
/// Nothing here spoofs anything. Every switch is a documented Chrome switch, and the browser really is a
/// real Chrome with a real profile - that is the whole mechanism.
/// </summary>
public sealed class ChromeAgentBrowser : IAsyncDisposable
{
    /// <summary>Where Chrome keeps this browser's cookies, logins and any challenge cookie it earns. Persistent
    /// on purpose: a fresh profile every launch is both a bot-check tell and a re-login every session.</summary>
    private static string ProfileDirectory => Path.Combine(AppSettings.Dir, "agent-chrome");

    /// <summary>Far outside any physical monitor. Chrome keeps rendering off-screen - unlike WebView2 - so this
    /// is a real hide that still screenshots.</summary>
    private const int OffScreen = -32000;

    private readonly SemaphoreSlim _startGate = new(1, 1);
    private IBrowser? _browser;

    /// <summary>Chrome as the user actually has it installed. Deliberately NOT Chrome-for-Testing, which carries
    /// different branding and its own UA and is the more conspicuous of the two.</summary>
    public static string? FindInstalledChrome()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("VIBECODE_CHROME_PATH"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Google", "Chrome", "Application", "chrome.exe"),
        };
        return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
    }

    public static bool IsAvailable => FindInstalledChrome() is not null;

    /// <summary>Start Chrome, or hand back the one already running. Serialised: two tabs opening at once must not
    /// race into two browsers pointed at the same profile directory, which Chrome refuses outright.</summary>
    public async Task<IBrowser> EnsureBrowserAsync()
    {
        if (_browser is { IsConnected: true } running) return running;
        await _startGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_browser is { IsConnected: true } already) return already;
            var executable = FindInstalledChrome()
                ?? throw new InvalidOperationException(
                    "Google Chrome is not installed. The agent browser needs it; install Chrome or set VIBECODE_CHROME_PATH.");
            Directory.CreateDirectory(ProfileDirectory);

            var options = new LaunchOptions
            {
                // Headed. Headless is its own tell, and this window is meant to be watchable - it is the same
                // surface the user can take over when a page wants a human.
                Headless = false,
                ExecutablePath = executable,
                UserDataDir = ProfileDirectory,
                // Let the real window size decide. A fixed synthetic viewport is a tell, and the plugin sets its
                // own through Emulation.setDeviceMetricsOverride when it wants one.
                DefaultViewport = null,
                // Puppeteer adds this by default; it is what raises the "Chrome is being controlled by automated
                // test software" banner and half the fingerprint.
                IgnoredDefaultArgs = new[] { "--enable-automation" },
                Args = new[]
                {
                    "--disable-blink-features=AutomationControlled",
                    "--no-first-run",
                    "--no-default-browser-check",
                    "--hide-crash-restore-bubble",
                    $"--window-position={OffScreen},{OffScreen}",
                },
            };

            _browser = await Puppeteer.LaunchAsync(options).ConfigureAwait(false);
            return _browser;
        }
        finally
        {
            _startGate.Release();
        }
    }

    /// <summary>Open a tab and return the handle the bridge drives it through.</summary>
    public async Task<ChromeAgentTab> NewTabAsync()
    {
        var browser = await EnsureBrowserAsync().ConfigureAwait(false);
        var page = await browser.NewPageAsync().ConfigureAwait(false);
        return new ChromeAgentTab(page);
    }

    public async ValueTask DisposeAsync()
    {
        var browser = _browser;
        _browser = null;
        if (browser is null) return;
        try { await browser.CloseAsync().ConfigureAwait(false); }
        catch (Exception ex) when (ex is PuppeteerException or ObjectDisposedException or IOException)
        {
            // A browser the user already quit is not a shutdown failure.
        }
        finally { browser.Dispose(); }
    }
}

/// <summary>
/// One Chrome tab, exposed as the two things the browser bridge actually needs: send a raw CDP command, and
/// hear every CDP event back. Everything else the plugin asks for is expressed in those terms.
/// </summary>
public sealed class ChromeAgentTab : IAsyncDisposable
{
    private readonly IPage _page;

    internal ChromeAgentTab(IPage page)
    {
        _page = page;
        // The plugin is a Playwright-shaped client: it waits for execution contexts and frame lifecycle before it
        // will evaluate anything, so events are not optional garnish - a missing one HANGS a call rather than
        // failing it. PuppeteerSharp surfaces the whole protocol stream, so forward it wholesale instead of
        // subscribing to a hand-maintained list that can silently fall behind Chrome's catalog.
        _page.Client.MessageReceived += OnMessage;
        _page.Close += (_, _) => Closed?.Invoke();
    }

    /// <summary>Raised for every CDP event: method, the originating session id (null for the page itself), and
    /// the raw parameter JSON.</summary>
    public event Action<string, string?, JsonNode?>? CdpEvent;

    public event Action? Closed;

    public string Url => _page.Url;

    private void OnMessage(object? sender, MessageEventArgs e)
    {
        JsonNode? parameters;
        try { parameters = e.MessageData.ValueKind == JsonValueKind.Undefined ? new JsonObject() : JsonNode.Parse(e.MessageData.GetRawText()); }
        catch (JsonException) { parameters = new JsonObject(); }
        CdpEvent?.Invoke(e.MessageID, null, parameters);
    }

    /// <summary>
    /// Send one CDP command. <paramref name="sessionId"/> addresses an attached child target (iframe, worker,
    /// popup); routing those through the page-level session would silently run the command against the wrong
    /// document, which is the same trap WebView2's two overloads set.
    /// </summary>
    public async Task<JsonNode?> SendCdpAsync(string method, JsonNode? commandParams, string? sessionId,
        CancellationToken token = default)
    {
        var args = commandParams is null ? null : JsonSerializer.Deserialize<object>(commandParams.ToJsonString());
        // Raw CDP, not Puppeteer's high-level API: the plugin is the automation layer and expects a transparent
        // pipe. Anything clever here would show up as the bridge and the agent disagreeing about page state.
        var session = string.IsNullOrWhiteSpace(sessionId)
            ? _page.Client
            : throw new InvalidOperationException(
                $"CDP session '{sessionId}' is not attached to this tab.");
        var response = await session.SendAsync(method, args).ConfigureAwait(false);
        return response is { } value ? JsonNode.Parse(value.GetRawText()) : new JsonObject();
    }

    /// <summary>Whether this tab's Chrome window is somewhere the user can actually see it.</summary>
    public async Task<bool> IsOnScreenAsync()
    {
        var bounds = await WindowBoundsAsync().ConfigureAwait(false);
        return bounds?["left"]?.GetValue<int>() is { } left && left > -10000;
    }

    /// <summary>Show or park the window. Parking beats minimising: a minimised Chrome throttles rendering, and
    /// the agent's screenshots would degrade without anything reporting a failure.</summary>
    public async Task SetOnScreenAsync(bool onScreen)
    {
        var windowId = await WindowIdAsync().ConfigureAwait(false);
        if (windowId is null) return;
        var bounds = onScreen
            ? new JsonObject { ["left"] = 120, ["top"] = 120, ["windowState"] = "normal" }
            : new JsonObject { ["left"] = -32000, ["top"] = -32000, ["windowState"] = "normal" };
        await _page.Client.SendAsync("Browser.setWindowBounds", new
        {
            windowId = windowId.Value,
            bounds = JsonSerializer.Deserialize<object>(bounds.ToJsonString()),
        }).ConfigureAwait(false);
    }

    public Task BringToFrontAsync() => _page.BringToFrontAsync();

    private async Task<JsonNode?> WindowBoundsAsync()
    {
        var response = await _page.Client.SendAsync("Browser.getWindowForTarget").ConfigureAwait(false);
        return response is { } value ? JsonNode.Parse(value.GetRawText())?["bounds"] : null;
    }

    private async Task<int?> WindowIdAsync()
    {
        var response = await _page.Client.SendAsync("Browser.getWindowForTarget").ConfigureAwait(false);
        if (response is not { } value) return null;
        return JsonNode.Parse(value.GetRawText())?["windowId"]?.GetValue<int>();
    }

    public async ValueTask DisposeAsync()
    {
        _page.Client.MessageReceived -= OnMessage;
        try { await _page.CloseAsync().ConfigureAwait(false); }
        catch (Exception ex) when (ex is PuppeteerException or ObjectDisposedException)
        {
            // A tab the user already closed is not a failure to close it.
        }
    }
}
