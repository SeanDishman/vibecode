using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using VibeCode.Services;

namespace VibeCode.UI;

/// <param name="ResourceName">Embedded original, used to seed the editable on-disk copy the first time.</param>
/// <param name="RelativePath">Where the file lives under <see cref="GameWindow.GamesDir"/>, using '/' separators.</param>
internal sealed record GameAsset(string ResourceName, string RelativePath);

/// <param name="EntryPath">The file to load, relative to <see cref="GameWindow.GamesDir"/>.</param>
/// <param name="Assets">Every file the game is made of, seeded on disk so it stays editable.</param>
/// <param name="Controls">Short control hint shown in the game window's titlebar.</param>
internal sealed record GameDefinition(
    string Id, string Name, string Description, string EntryPath,
    IReadOnlyList<GameAsset> Assets, string Controls)
{
    /// <summary>A game built from more than one file has to be served off disk over a virtual host:
    /// NavigateToString gives the page no origin, so relative &lt;script&gt;/&lt;link&gt; URLs - and ES module
    /// imports - have nothing to resolve against. Single-file games keep the simpler string load.</summary>
    internal bool ServeFromFolder => Assets.Count > 1;
}

internal static class GameCatalog
{
    internal static readonly GameDefinition SurviveTheShapes = new(
        "survive-the-shapes",
        "Survive the Shapes",
        "A stickman-with-a-gun survival shooter: dodge swarming shapes, upgrade your arsenal, and beat the bosses.",
        "SurviveTheShapes.html",
        [new GameAsset("VibeCode.Assets.Games.SurviveTheShapes.html", "SurviveTheShapes.html")],
        "WASD  ·  MOUSE  ·  P PAUSE");

    internal static readonly GameDefinition TowerDefense = new(
        "tower-defense",
        "Tower Defense",
        "Fifteen turrets (with upgrades, sell, and soft SFX), a very long road, and an endless swarm of circles. Do not let one through.",
        "TowerDefense/index.html",
        // FolderAssets enumerates every embedded file under TowerDefense/ so new modules
        // (audio.js, etc.) seed into %APPDATA%\VibeCode\Games without a hand-maintained list.
        FolderAssets("TowerDefense"),
        "CLICK BUILD  ·  CLICK UPGRADE/SELL  ·  RIGHT-CLICK INFO  ·  P PAUSE");

    internal static readonly GameDefinition TinyEmpires = new(
        "tiny-empires",
        "Tiny Empires",
        "A pixel world map you settle, improve and conquer: grow cities, research your way from the stone age to the space race, then march tiny armies over the border.",
        "TinyEmpires/index.html",
        FolderAssets("TinyEmpires"),
        "DRAG SELECT  ·  R-CLICK ORDER  ·  T RESEARCH  ·  P PAUSE");

    internal static readonly GameDefinition FishingTycoon = new(
        "fishing-tycoon",
        "Fishing Tycoon",
        "One little boat, one big ocean. Haul in fish, sell them at the dock, and upgrade your way down to the deep water where the money swims.",
        "FishingTycoon/index.html",
        FolderAssets("FishingTycoon"),
        "WASD / CLICK SAIL  ·  E UPGRADES  ·  P PAUSE");

    /// <summary>Embedded resource names mirror the folder layout, so a game file's path is also its key:
    /// <c>src/hud.js</c> in folder <c>TowerDefense</c> is <c>VibeCode.Assets.Games.TowerDefense.src.hud.js</c>.</summary>
    internal static GameAsset Asset(string folder, string relativePath) => new(
        $"VibeCode.Assets.Games.{folder}.{relativePath.Replace('/', '.')}",
        $"{folder}/{relativePath}");

    /// <summary>
    /// Every embedded file under a game folder, discovered at runtime. The csproj already embeds these by
    /// wildcard, so a hand-written asset list only exists to be forgotten: adding one JavaScript module and
    /// not listing it here means the module is never seeded on a fresh install and the whole game fails to
    /// boot on its first import. Enumerating the manifest keeps the two in step by construction.
    /// </summary>
    internal static IReadOnlyList<GameAsset> FolderAssets(string folder)
    {
        var prefix = $"VibeCode.Assets.Games.{folder}.";
        var assets = new List<GameAsset>();

        foreach (var name in Assembly.GetExecutingAssembly().GetManifestResourceNames())
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;

            // "src.construction.js" -> "src/construction.js". Only the final dot is the
            // extension; every earlier one was a directory separator before embedding.
            var tail = name[prefix.Length..];
            var lastDot = tail.LastIndexOf('.');
            var relative = lastDot <= 0
                ? tail
                : string.Concat(tail[..lastDot].Replace('.', '/'), tail[lastDot..]);

            assets.Add(new GameAsset(name, $"{folder}/{relative}"));
        }

        // index.html first so a partial seed still leaves the entry point present.
        assets.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        return assets;
    }
}

/// <summary>Hosts local JavaScript games in an owned WebView2 window that remains slightly inset from the IDE.</summary>
public partial class GameWindow : Window
{
    private static GameWindow? _open;
    private static Task<CoreWebView2Environment>? _environmentTask;

    private readonly GameDefinition _game;
    private string? _html;
    private bool _initialized;
    private bool _loading;
    private bool _parked;
    private bool _closed;

    private GameWindow(GameDefinition game)
    {
        _game = game;
        InitializeComponent();
        Title = $"{game.Name} — Games — VibeCode";
        GameTitle.Text = game.Name;
        GameSubtitle.Text = "Arcade session · progress saves when minimized";
        GameControls.Text = game.Controls;
    }

    /// <summary>Open one game window at a time; selecting the current game simply brings it forward.</summary>
    internal static void Open(Window owner, GameDefinition game)
    {
        if (_open is { IsLoaded: true } existing)
        {
            if (existing._game.Id.Equals(game.Id, StringComparison.OrdinalIgnoreCase))
            {
                if (existing._parked)
                {
                    existing.RestoreParkedSession();
                    return;
                }

                if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
                existing.Activate();
                existing.GameView.Focus();
                return;
            }

            existing.Close();
        }

        var window = new GameWindow(game) { Owner = owner };
        SizeJustInsideOwner(window, owner);
        _open = window;
        window.Show();
    }

    /// <summary>Automated-verification hook, mirroring VIBECODE_OPEN_WEATHER_MAPS: with VIBECODE_HIDDEN=1 set,
    /// VIBECODE_OPEN_GAME=&lt;game id&gt; opens that game off-screen, and VIBECODE_GAME_SCREENSHOT=&lt;path&gt; writes a
    /// PNG of the rendered page once it has had a moment to draw. Does nothing during a normal run.</summary>
    internal static void MaybeAutoOpenForSmoke(Window owner)
    {
        if (Environment.GetEnvironmentVariable("VIBECODE_HIDDEN") != "1") return;
        if (Environment.GetEnvironmentVariable("VIBECODE_OPEN_GAME") is not { Length: > 0 } id) return;

        var game = new[]
            {
                GameCatalog.SurviveTheShapes, GameCatalog.TowerDefense,
                GameCatalog.TinyEmpires, GameCatalog.FishingTycoon,
            }
            .FirstOrDefault(g => g.Id.Equals(id.Trim(), StringComparison.OrdinalIgnoreCase));
        if (game is not null) Open(owner, game);
    }

    /// <summary>Capture the WebView's own page bitmap. A WPF RenderTargetBitmap cannot see WebView2 content,
    /// so verifying that a game actually drew something has to go through CapturePreviewAsync.</summary>
    private async Task CaptureSmokeScreenshotAsync()
    {
        if (Environment.GetEnvironmentVariable("VIBECODE_HIDDEN") != "1") return;
        if (Environment.GetEnvironmentVariable("VIBECODE_GAME_SCREENSHOT") is not { Length: > 0 } path) return;

        try
        {
            // Give the page a beat to lay out, build its sprite cache, and paint the first frame.
            await Task.Delay(1500);
            if (_closed || GameView.CoreWebView2 is not { } core) return;

            var script = Environment.GetEnvironmentVariable("VIBECODE_GAME_SCRIPT");
            if (!string.IsNullOrWhiteSpace(script))
            {
                // Record what the page said back: a silent no-op here is otherwise
                // indistinguishable from a game that simply drew nothing.
                var result = await core.ExecuteScriptAsync(script);
                try { await File.WriteAllTextAsync(Path.GetFullPath(path) + ".txt", result); }
                catch { /* diagnostics only */ }

                await Task.Delay(900);
                if (_closed || GameView.CoreWebView2 is null) return;
            }

            await using var output = File.Create(Path.GetFullPath(path));
            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, output);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("game screenshot failed: " + ex.Message);
        }
    }

    private static void SizeJustInsideOwner(GameWindow window, Window owner)
    {
        // Leave a deliberate rim of the IDE visible: 48 DIPs per side and 40 DIPs above/below.
        var ownerWidth = owner.ActualWidth > 0 ? owner.ActualWidth : owner.Width;
        var ownerHeight = owner.ActualHeight > 0 ? owner.ActualHeight : owner.Height;
        if (double.IsFinite(ownerWidth)) window.Width = Math.Max(window.MinWidth, ownerWidth - 96);
        if (double.IsFinite(ownerHeight)) window.Height = Math.Max(window.MinHeight, ownerHeight - 80);

        // Off-screen verification must never flash a game over the user's real desktop.
        if (Environment.GetEnvironmentVariable("VIBECODE_HIDDEN") != "1") return;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = owner.Left + Math.Max(0, (ownerWidth - window.Width) / 2);
        window.Top = owner.Top + Math.Max(0, (ownerHeight - window.Height) / 2);
        window.ShowActivated = false;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e) => await LoadGameAsync();

    private async Task LoadGameAsync()
    {
        if (_closed || _loading) return;
        _loading = true;
        ShowLoading("Loading game…", "Starting the JavaScript game surface");

        try
        {
            SeedGameFiles(_game);
            if (!_initialized)
            {
                var environment = await GetEnvironmentAsync();
                if (_closed) return;
                await GameView.EnsureCoreWebView2Async(environment);
                if (_closed || GameView.CoreWebView2 is not { } core) return;

                core.Settings.AreDevToolsEnabled = true;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.AreBrowserAcceleratorKeysEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.NavigationCompleted += OnNavigationCompleted;
                core.NewWindowRequested += (_, args) => args.Handled = true;
                core.ProcessFailed += (_, args) =>
                    ShowError($"The game process stopped ({args.ProcessFailedKind}).");

                // Multi-file games are fetched from the editable games folder as if it were a website,
                // which is what makes relative imports (and therefore ES modules) resolve at all.
                if (_game.ServeFromFolder)
                {
                    Directory.CreateDirectory(GamesDir);
                    core.SetVirtualHostNameToFolderMapping(
                        VirtualHost, GamesDir, CoreWebView2HostResourceAccessKind.Allow);
                }

                // ES module imports (./render.js etc.) do NOT inherit the ?v= stamp on
                // index.html, so WebView's HTTP cache would keep serving the old module
                // after an AppData edit + Restart. Kill the cache so disk is always truth.
                try
                {
                    await core.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
                    await core.CallDevToolsProtocolMethodAsync(
                        "Network.setCacheDisabled", "{\"cacheDisabled\":true}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("game cache-disable failed: " + ex.Message);
                }

                _initialized = true;
            }

            await BustGameModuleCacheAsync();
            NavigateToGame();
        }
        catch (Exception ex)
        {
            _environmentTask = null; // a retry should be able to recreate a failed WebView environment
            ShowError("Couldn't start the game: " + ex.Message);
        }
        finally
        {
            _loading = false;
        }
    }

    private static Task<CoreWebView2Environment> GetEnvironmentAsync()
    {
        if (_environmentTask is not null) return _environmentTask;
        var userDataFolder = Path.Combine(AppSettings.Dir, "games-webview");
        Directory.CreateDirectory(userDataFolder);
        return _environmentTask = CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: userDataFolder);
    }

    /// <summary>Editable on-disk home for game files: <c>%APPDATA%\VibeCode\Games</c> by default, or whatever
    /// <c>VIBECODE_GAMES_DIR</c> points at (handy for editing straight out of a source tree). Files here shadow the
    /// embedded originals, so tweaking a game's HTML/JS and restarting it shows up without rebuilding the exe.</summary>
    internal static string GamesDir =>
        Environment.GetEnvironmentVariable("VIBECODE_GAMES_DIR") is { Length: > 0 } overrideDir
            ? Path.GetFullPath(overrideDir.Trim('"'))
            : Path.Combine(AppSettings.Dir, "Games");

    internal static string GameFilePath(GameDefinition game) =>
        Path.Combine(GamesDir, game.EntryPath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Hostname the games folder is published under. Uses the reserved <c>.invalid</c> TLD so it can
    /// never collide with a real site the WebView might otherwise reach.</summary>
    private const string VirtualHost = "vibecode-games.invalid";

    /// <summary>Write any missing game files out of the assembly so the on-disk copy is complete and editable.
    /// Existing files are left alone: that is what lets someone tweak a game, hit Restart, and see it change.
    /// Binary assets (wav/png/…) are written as raw bytes — StreamReader would corrupt them.</summary>
    private static void SeedGameFiles(GameDefinition game)
    {
        Directory.CreateDirectory(GamesDir);
        foreach (var asset in game.Assets)
        {
            var path = Path.Combine(GamesDir, asset.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path)) continue;

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            if (IsBinaryGameAsset(asset.RelativePath))
                File.WriteAllBytes(path, ReadEmbeddedGameBytes(asset.ResourceName));
            else
                File.WriteAllText(path, ReadEmbeddedGame(asset.ResourceName));
        }
    }

    private static bool IsBinaryGameAsset(string relativePath)
    {
        var ext = Path.GetExtension(relativePath);
        return ext.Equals(".wav", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".mp3", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".ogg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".gif", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".webp", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".bin", StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] ReadEmbeddedGameBytes(string resourceName)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"The embedded game '{resourceName}' was not found.");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Drop cached JS modules for the virtual-host games origin. The entry HTML is already
    /// stamped with ?v=, but every <c>import './foo.js'</c> resolves to a bare URL that the
    /// WebView happily serves from disk cache — which is why "I edited AppData and Restarted"
    /// used to do nothing visible.
    /// </summary>
    private async Task BustGameModuleCacheAsync()
    {
        if (GameView.CoreWebView2 is not { } core) return;
        try
        {
            // Prefer the profile API when available; fall back to CDP Network.clearBrowserCache.
            await core.Profile.ClearBrowsingDataAsync(
                CoreWebView2BrowsingDataKinds.DiskCache | CoreWebView2BrowsingDataKinds.CacheStorage);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("game Profile.ClearBrowsingDataAsync failed: " + ex.Message);
            try
            {
                await core.CallDevToolsProtocolMethodAsync("Network.clearBrowserCache", "{}");
            }
            catch (Exception ex2)
            {
                Debug.WriteLine("game Network.clearBrowserCache failed: " + ex2.Message);
            }
        }
    }

    /// <summary>Point the WebView at the game. Folder games load over the virtual host with a cache-busting
    /// stamp taken from the files themselves, so editing one and hitting Restart really does reload it; a
    /// single-file game is still injected directly, and falls back to the embedded copy if the disk copy
    /// cannot be read (locked file, read-only volume) so it at least still runs.</summary>
    private void NavigateToGame()
    {
        if (_game.ServeFromFolder)
        {
            // Unique path segment + query so neither the document nor any intermediate
            // service worker can reuse a previous module graph for this boot.
            var stamp = GameFilesStamp(_game);
            GameView.CoreWebView2.Navigate(
                $"https://{VirtualHost}/{_game.EntryPath}?v={stamp}&_={DateTime.UtcNow.Ticks}");
            return;
        }

        try
        {
            _html = File.ReadAllText(GameFilePath(_game));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _html = ReadEmbeddedGame(_game.Assets[0].ResourceName);
        }

        GameView.NavigateToString(_html);
    }

    /// <summary>Newest write time across a game's files, used purely to bust the WebView's HTTP cache.</summary>
    private static long GameFilesStamp(GameDefinition game)
    {
        long stamp = 0;
        foreach (var asset in game.Assets)
        {
            var path = Path.Combine(GamesDir, asset.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                if (File.Exists(path)) stamp = Math.Max(stamp, File.GetLastWriteTimeUtc(path).Ticks);
            }
            catch (IOException) { /* a stale stamp only costs a cached reload */ }
        }
        return stamp;
    }

    private static string ReadEmbeddedGame(string resourceName)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"The embedded game '{resourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_closed) return;
        if (!e.IsSuccess)
        {
            ShowError($"The game could not load ({e.WebErrorStatus}).");
            return;
        }

        LoadingOverlay.Visibility = Visibility.Collapsed;
        GameView.Focus();
        try
        {
            await GameView.CoreWebView2.ExecuteScriptAsync(
                "window.focus(); if (document.body) { document.body.tabIndex = 0; document.body.focus(); }");
        }
        catch { /* focus is a convenience; the game remains playable after a click */ }

        await CaptureSmokeScreenshotAsync();
    }

    private void ShowLoading(string title, string detail)
    {
        LoadingTitle.Text = title;
        LoadingDetail.Text = detail;
        LoadingProgress.Visibility = Visibility.Visible;
        RetryButton.Visibility = Visibility.Collapsed;
        LoadingOverlay.Visibility = Visibility.Visible;
    }

    private void ShowError(string detail)
    {
        if (_closed) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_closed) return;
            LoadingTitle.Text = "Game unavailable";
            LoadingDetail.Text = detail;
            LoadingProgress.Visibility = Visibility.Collapsed;
            RetryButton.Visibility = Visibility.Visible;
            LoadingOverlay.Visibility = Visibility.Visible;
        });
    }

    private async void OnReload(object sender, RoutedEventArgs e)
    {
        _parked = false;
        GamesService.Instance.ClearPausedSession(_game.Id);

        if (_initialized && GameView.CoreWebView2 is not null)
        {
            ShowLoading("Restarting game…", _game.Name);
            try
            {
                // Re-seed anything that went missing, drop the module cache, then reload so
                // "Restart game" actually reflects on-disk edits (including AppData tweaks).
                SeedGameFiles(_game);
                await BustGameModuleCacheAsync();
                NavigateToGame();
            }
            catch (Exception ex)
            {
                ShowError("Couldn't reload the game: " + ex.Message);
            }
            return;
        }

        await LoadGameAsync();
    }

    /// <summary>
    /// Freeze the run inside the existing page, then hide this window without disposing its WebView. Keeping the
    /// JavaScript heap alive is the save: every enemy, upgrade, projectile, score, and timer resumes exactly in place.
    /// </summary>
    private async void OnPauseAndMinimize(object sender, RoutedEventArgs e)
    {
        if (_closed || _parked) return;
        _parked = true;

        if (_initialized && GameView.CoreWebView2 is { } core)
        {
            try
            {
                // The editable game file may predate this host button, so use the public DOM/input surface instead
                // of depending on a version-specific JavaScript API. Dispatch P only while gameplay is actually live.
                await core.ExecuteScriptAsync(
                    """
                    (() => {
                      const stage = document.getElementById('stage');
                      const pause = document.getElementById('pause');
                      const isPlaying = stage?.classList.contains('playing') &&
                                        (!pause || pause.classList.contains('hidden'));
                      if (isPlaying) {
                        document.dispatchEvent(new KeyboardEvent('keydown', {
                          key: 'p', code: 'KeyP', bubbles: true, cancelable: true
                        }));
                      }
                    })();
                    """);
            }
            catch
            {
                // Hiding the WebView also raises document.visibilitychange, which is the game's fallback auto-pause.
            }
        }

        // Closing the window is still allowed while WebView2 is answering the pause script.
        if (_closed)
        {
            _parked = false;
            return;
        }

        GamesService.Instance.MarkSessionPaused(_game.Id, _game.Name);
        Hide();

        if (Owner is { } owner)
        {
            if (owner.WindowState == WindowState.Minimized) owner.WindowState = WindowState.Normal;
            owner.Activate();
        }
    }

    /// <summary>Show a parked WebView and continue through the game's own Resume action without navigating.</summary>
    private async void RestoreParkedSession()
    {
        if (_closed) return;

        _parked = false;
        GamesService.Instance.ClearPausedSession(_game.Id);
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();

        // Let WPF reconnect the hidden WebView to the visual tree before asking the page to continue.
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
        if (_initialized && GameView.CoreWebView2 is { } core)
        {
            try
            {
                await core.ExecuteScriptAsync(
                    """
                    (() => {
                      const pause = document.getElementById('pause');
                      const resume = document.getElementById('resumeBtn');
                      if (pause && !pause.classList.contains('hidden') && resume) resume.click();
                      window.focus();
                      document.body?.focus();
                    })();
                    """);
            }
            catch
            {
                // A visible game remains usable after a click even if its convenience resume script fails.
            }
        }

        if (_closed) return;
        GameView.Focus();
    }

    /// <summary>Reveal the editable game file in Explorer so the user can tweak it and Restart — no rebuild needed.</summary>
    private void OnOpenGamesFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            // Seed this game's files so the folder isn't empty the first time the user goes looking for it.
            try { SeedGameFiles(_game); }
            catch { /* seeding is best-effort; still open the folder below */ }

            var path = GameFilePath(_game);

            // Highlight the game file when it exists; otherwise just open the folder itself.
            var args = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{GamesDir}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", args));
        }
        catch (Exception ex)
        {
            ShowError("Couldn't open the games folder: " + ex.Message);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        GamesService.Instance.ClearPausedSession(_game.Id);
        if (ReferenceEquals(_open, this)) _open = null;
        if (GameView.CoreWebView2 is { } core) core.NavigationCompleted -= OnNavigationCompleted;
        GameView.Dispose();
    }
}
