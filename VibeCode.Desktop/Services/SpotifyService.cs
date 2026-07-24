using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VibeCode.UI;

namespace VibeCode.Services;

/// <summary>
/// Optional Spotify mini-player. Uses the Spotify Web API with OAuth Authorization-Code + PKCE (no client secret -
/// the Client ID is the user's own registered app id, which is public). Tokens live in %APPDATA%\VibeCode\spotify-auth.json.
/// Playback control (play/pause/skip) drives whatever Spotify device the user has active and needs Spotify Premium.
/// </summary>
public sealed class SpotifyService : Observable
{
    public static SpotifyService Instance { get; } = new();

    // The user registers their own free app at developer.spotify.com and adds EXACTLY this redirect URI.
    public const int CallbackPort = 8888;
    public static string RedirectUri => $"http://127.0.0.1:{CallbackPort}/callback";
    private const string Scopes = "user-read-playback-state user-modify-playback-state user-read-currently-playing";

    private static readonly HttpClient Http = new();
    private static string AuthFile => Path.Combine(AppSettings.Dir, "spotify-auth.json");

    private string? _accessToken;
    private string? _refreshToken;
    private DateTime _expiresAt;
    private string? _clientId;

    private SpotifyService() => LoadTokens();

    // ---- bindable state ----
    private bool _connected;
    public bool IsConnected { get => _connected; private set { if (Set(ref _connected, value)) Raise(nameof(StatusText)); } }
    private bool _isPlaying;
    public bool IsPlaying { get => _isPlaying; private set { if (Set(ref _isPlaying, value)) Raise(nameof(PlayPauseGlyph)); } }
    private string _track = "";
    public string Track { get => _track; private set { if (Set(ref _track, value)) { Raise(nameof(HasTrack)); Raise(nameof(StatusText)); Raise(nameof(NowPlayingTooltip)); } } }
    private string _artist = "";
    public string Artist { get => _artist; private set { if (Set(ref _artist, value)) Raise(nameof(NowPlayingTooltip)); } }
    private string? _albumArt;
    public string? AlbumArt { get => _albumArt; private set => Set(ref _albumArt, value); }

    // ---- progress / shuffle / repeat / volume (for the inline player bar) ----
    private int _progressMs;
    public int ProgressMs { get => _progressMs; private set { if (Set(ref _progressMs, value)) { Raise(nameof(ElapsedText)); Raise(nameof(ProgressPercent)); } } }
    private int _durationMs;
    public int DurationMs { get => _durationMs; private set { if (Set(ref _durationMs, value)) { Raise(nameof(TotalText)); Raise(nameof(ProgressPercent)); } } }
    private bool _shuffle;
    public bool ShuffleOn { get => _shuffle; private set => Set(ref _shuffle, value); }
    private string _repeat = "off";   // off | context | track
    public string RepeatState { get => _repeat; private set { if (Set(ref _repeat, value)) { Raise(nameof(RepeatGlyph)); Raise(nameof(RepeatActive)); } } }
    private int _volumePercent = 100;
    private int _lastAudibleVolume = 50;
    public int VolumePercent
    {
        get => _volumePercent;
        private set
        {
            value = Math.Clamp(value, 0, 100);
            if (!Set(ref _volumePercent, value)) return;
            if (value > 0) _lastAudibleVolume = value;
            Raise(nameof(IsMuted));
            Raise(nameof(VolumeGlyph));
            Raise(nameof(VolumeText));
            Raise(nameof(VolumeTooltip));
        }
    }
    private bool _supportsVolume = true;
    public bool SupportsVolume
    {
        get => _supportsVolume;
        private set
        {
            if (!Set(ref _supportsVolume, value)) return;
            Raise(nameof(VolumeText));
            Raise(nameof(VolumeTooltip));
        }
    }

    public string ElapsedText => Fmt(_progressMs);
    public string TotalText => Fmt(_durationMs);
    public double ProgressPercent => _durationMs > 0 ? Math.Min(100.0, 100.0 * _progressMs / _durationMs) : 0;
    public bool RepeatActive => _repeat != "off";
    // Segoe MDL2 / Fluent: E8ED = RepeatOne, E8EE = RepeatAll (titlebar used to hardcode E8EE and never flip).
    public string RepeatGlyph => _repeat == "track" ? "\uE8ED" : "\uE8EE";
    public bool IsMuted => _volumePercent == 0;
    // Segoe Fluent Icons: mute / low / medium / high speaker states.
    public string VolumeGlyph => _volumePercent switch
    {
        0 => "\uE74F",
        < 34 => "\uE993",
        < 67 => "\uE994",
        _ => "\uE995",
    };
    public string VolumeText => SupportsVolume ? $"{_volumePercent}%" : "Unavailable";
    public string VolumeTooltip => !SupportsVolume
        ? "This Spotify device does not support remote volume control"
        : IsMuted
            ? "Muted - click to adjust, double-click to restore"
            : $"Volume {_volumePercent}% - click to adjust, double-click to mute";
    private static string Fmt(int ms) { var t = TimeSpan.FromMilliseconds(Math.Max(0, ms)); return $"{(int)t.TotalMinutes}:{t.Seconds:00}"; }

    public bool HasTrack => !string.IsNullOrEmpty(_track);
    public string PlayPauseGlyph => _isPlaying ? "" : "";   // pause / play (Segoe Fluent)
    public string StatusText => !IsConnected ? "Not connected" : HasTrack ? _track : "Nothing playing";
    /// <summary>Full "Track — Artist" for titlebar tooltips when the visible labels are ellipsized.</summary>
    public string NowPlayingTooltip =>
        !HasTrack ? "Nothing playing"
        : string.IsNullOrEmpty(_artist) ? _track
        : $"{_track} — {_artist}";

    /// <summary>Whether the extension is turned on (mirrors AppSettings so the titlebar menu can bind to it).</summary>
    public bool Enabled
    {
        get => AppSettings.Current.SpotifyEnabled;
        set
        {
            if (AppSettings.Current.SpotifyEnabled == value) return;
            AppSettings.Current.SpotifyEnabled = value;
            AppSettings.Current.Save();
            Raise(nameof(Enabled));
        }
    }
    /// <summary>Re-raise Enabled (call after settings change elsewhere so bindings refresh).</summary>
    public void NotifyEnabledChanged() => Raise(nameof(Enabled));

    // =============== auth ===============

    public bool HasClientId => !string.IsNullOrWhiteSpace(AppSettings.Current.SpotifyClientId);

    /// <summary>Run the browser OAuth (PKCE) flow. Returns null on success, else a human-readable error.</summary>
    public async Task<string?> ConnectAsync(string clientId)
    {
        clientId = clientId.Trim();
        if (clientId.Length == 0) return "Enter your Spotify Client ID first.";
        _clientId = clientId;

        var verifier = RandBase64Url(64);
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = RandBase64Url(16);

        var authUrl = "https://accounts.spotify.com/authorize?" + Form(new()
        {
            ["client_id"] = clientId,
            ["response_type"] = "code",
            ["redirect_uri"] = RedirectUri,
            ["code_challenge_method"] = "S256",
            ["code_challenge"] = challenge,
            ["scope"] = Scopes,
            ["state"] = state,
        });

        Task<(string? code, string? error, string? state)> callback;
        try { callback = WaitForCallbackAsync(); }
        catch (Exception ex) { return $"Couldn't open the local callback on port {CallbackPort}: {ex.Message}"; }

        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = authUrl, UseShellExecute = true }); }
        catch (Exception ex) { return "Couldn't open the browser: " + ex.Message; }

        var (code, error, gotState) = await callback;
        if (error is not null) return "Spotify denied the request: " + error;
        if (code is null) return "No authorization code came back.";
        if (gotState != state) return "State mismatch - possible interference; aborted.";

        var body = await PostTokenAsync(new()
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = RedirectUri,
            ["client_id"] = clientId,
            ["code_verifier"] = verifier,
        });
        if (body is null) return "Spotify rejected the token exchange (check the Client ID and that the redirect URI is registered).";
        StoreTokens(body, clientId);
        await PollAsync();
        return null;
    }

    public void Disconnect()
    {
        _accessToken = _refreshToken = null;
        _expiresAt = default;
        try { if (File.Exists(AuthFile)) File.Delete(AuthFile); } catch { /* best effort */ }
        IsConnected = false; IsPlaying = false; Track = ""; Artist = ""; AlbumArt = null;
        SupportsVolume = true; VolumePercent = 100;
    }

    private async Task<bool> EnsureTokenAsync()
    {
        if (_refreshToken is null) return false;
        if (_accessToken is not null && DateTime.UtcNow < _expiresAt.AddSeconds(-30)) return true;
        var body = await PostTokenAsync(new()
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = _refreshToken,
            ["client_id"] = _clientId ?? AppSettings.Current.SpotifyClientId ?? "",
        });
        if (body is null) return false;
        StoreTokens(body, _clientId ?? AppSettings.Current.SpotifyClientId ?? "");
        return _accessToken is not null;
    }

    // =============== playback ===============

    /// <summary>Refresh now-playing state. Safe to call on a timer.</summary>
    public async Task PollAsync()
    {
        if (!await EnsureTokenAsync()) { IsConnected = false; return; }
        IsConnected = true;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.spotify.com/v1/me/player?additional_types=track");
            req.Headers.Add("Authorization", "Bearer " + _accessToken);
            using var resp = await Http.SendAsync(req);
            if (resp.StatusCode == HttpStatusCode.NoContent) { IsPlaying = false; Track = ""; Artist = ""; AlbumArt = null; ProgressMs = 0; DurationMs = 0; return; }
            if (!resp.IsSuccessStatusCode) return;
            var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync());
            IsPlaying = node?["is_playing"]?.GetValue<bool>() ?? false;
            var item = node?["item"];
            Track = item?["name"]?.GetValue<string>() ?? "";
            Artist = string.Join(", ", (item?["artists"] as JsonArray)?.Select(a => a?["name"]?.GetValue<string>()).Where(n => n is not null) ?? Array.Empty<string>());
            // Prefer ~64px art for the 30px titlebar thumb (API returns largest→smallest). Fall back through the list.
            AlbumArt = PickAlbumArt(item?["album"]?["images"] as JsonArray);
            DurationMs = item?["duration_ms"]?.GetValue<int>() ?? 0;
            ProgressMs = node?["progress_ms"]?.GetValue<int>() ?? 0;
            ShuffleOn = node?["shuffle_state"]?.GetValue<bool>() ?? false;
            RepeatState = node?["repeat_state"]?.GetValue<string>() ?? "off";
            var device = node?["device"];
            SupportsVolume = device?["supports_volume"]?.GetValue<bool>() ?? true;
            VolumePercent = device?["volume_percent"]?.GetValue<int>() ?? _volumePercent;
        }
        catch { /* transient network - keep last state */ }
    }

    /// <summary>Called ~1×/sec by the UI: advances the progress bar locally while playing and re-syncs with Spotify
    /// every few seconds, so the bar moves smoothly instead of jumping only on each poll.</summary>
    public async void Heartbeat()
    {
        if (!Enabled) return;
        if (_isPlaying && _durationMs > 0 && _progressMs < _durationMs) ProgressMs = Math.Min(_durationMs, _progressMs + 1000);
        if (++_sinceSync >= 5) { _sinceSync = 0; await PollAsync(); }
    }
    private int _sinceSync;

    public Task ToggleAsync() => TransportAsync(IsPlaying ? "pause" : "play", HttpMethod.Put);
    public Task NextAsync() => TransportAsync("next", HttpMethod.Post);
    public Task PreviousAsync() => TransportAsync("previous", HttpMethod.Post);
    public Task ToggleShuffleAsync() => PutStateAsync($"shuffle?state={(!_shuffle).ToString().ToLowerInvariant()}");
    public Task CycleRepeatAsync() => PutStateAsync($"repeat?state={(_repeat switch { "off" => "context", "context" => "track", _ => "off" })}");

    /// <summary>Updates the bound volume immediately while the slider is moving; the UI debounces the network write.</summary>
    public void PreviewVolume(int volumePercent) => VolumePercent = volumePercent;

    /// <summary>Set the active Spotify device volume. The optimistic local update keeps the slider and icon responsive.</summary>
    public async Task SetVolumeAsync(int volumePercent)
    {
        if (!SupportsVolume) return;
        volumePercent = Math.Clamp(volumePercent, 0, 100);
        VolumePercent = volumePercent;
        await PutStateAsync($"volume?volume_percent={volumePercent}");
    }

    /// <summary>Mute to zero or restore the most recent non-zero volume.</summary>
    public Task ToggleMuteAsync() => SetVolumeAsync(IsMuted ? Math.Max(1, _lastAudibleVolume) : 0);

    // =============== ducking (auto-pause while the user dictates) ===============
    //
    // Mic dictation pauses playback and restores it afterwards. The one rule that matters: only music WE paused is
    // resumed — if it was already paused when dictation started, it stays paused. `_ducked` is that latch.
    //
    // Duck/Unduck are serialized by `_duckLock` rather than being plain fire-and-forget. Dictation can be shorter than
    // a Spotify round trip, so Unduck routinely lands while Duck is still in flight; without the lock it would read the
    // latch before Duck set it, and the user's music would just never come back.
    private readonly SemaphoreSlim _duckLock = new(1, 1);
    private bool _ducked;

    /// <summary>Pause playback for the duration of a dictation. No-op when the extension is off, disconnected, or
    /// nothing was playing (that last case is what makes the resume conditional). Idempotent: a second Duck keeps the
    /// original latch, so overlapping captures can't lose the pending resume.</summary>
    public async Task DuckAsync()
    {
        if (!Enabled || !IsConnected) return;
        await _duckLock.WaitAsync();
        try
        {
            if (_ducked) return;
            // Decide on FRESH state: the heartbeat only syncs every 5 s, and acting on a stale "playing" would resume
            // music the user had paused themselves moments earlier — exactly what must not happen.
            await PollAsync();
            if (!IsPlaying) return;              // paused before dictation => still paused after it
            _ducked = true;
            await TransportAsync("pause", HttpMethod.Put);
        }
        finally { _duckLock.Release(); }
    }

    /// <summary>Dictation is over: resume playback, but only if this service is the one that paused it.</summary>
    public async Task UnduckAsync()
    {
        await _duckLock.WaitAsync();
        try
        {
            if (!_ducked) return;
            _ducked = false;                     // cleared even if the resume below fails - we no longer owe one
            if (!IsConnected) return;
            await PollAsync();
            if (IsPlaying) return;               // the user hit play themselves mid-dictation - don't fight them
            await TransportAsync("play", HttpMethod.Put);
        }
        finally { _duckLock.Release(); }
    }

    private async Task PutStateAsync(string pathAndQuery)
    {
        if (!await EnsureTokenAsync()) return;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Put, $"https://api.spotify.com/v1/me/player/{pathAndQuery}");
            req.Headers.Add("Authorization", "Bearer " + _accessToken);
            req.Content = new StringContent("", Encoding.UTF8, "application/json");
            await Http.SendAsync(req);
        }
        catch { /* no active device / not premium - ignore */ }
        await Task.Delay(200);
        await PollAsync();
    }

    private async Task TransportAsync(string action, HttpMethod method)
    {
        if (!await EnsureTokenAsync()) return;
        try
        {
            using var req = new HttpRequestMessage(method, $"https://api.spotify.com/v1/me/player/{action}");
            req.Headers.Add("Authorization", "Bearer " + _accessToken);
            if (method == HttpMethod.Put) req.Content = new StringContent("", Encoding.UTF8, "application/json");
            await Http.SendAsync(req);
        }
        catch { /* no active device / not premium / offline - ignore */ }
        await Task.Delay(250);
        await PollAsync();
    }

    // =============== token plumbing ===============

    private async Task<JsonNode?> PostTokenAsync(Dictionary<string, string> form)
    {
        try
        {
            using var content = new FormUrlEncodedContent(form);
            using var resp = await Http.PostAsync("https://accounts.spotify.com/api/token", content);
            if (!resp.IsSuccessStatusCode) return null;
            return JsonNode.Parse(await resp.Content.ReadAsStringAsync());
        }
        catch { return null; }
    }

    private void StoreTokens(JsonNode body, string clientId)
    {
        _accessToken = body["access_token"]?.GetValue<string>();
        if (body["refresh_token"]?.GetValue<string>() is { } rt) _refreshToken = rt;   // refresh omitted on refreshes
        var expiresIn = body["expires_in"]?.GetValue<int>() ?? 3600;
        _expiresAt = DateTime.UtcNow.AddSeconds(expiresIn);
        _clientId = clientId;
        try
        {
            Directory.CreateDirectory(AppSettings.Dir);
            var json = new JsonObject
            {
                ["refresh_token"] = _refreshToken,
                ["client_id"] = clientId,
            };
            File.WriteAllText(AuthFile, json.ToJsonString());
        }
        catch { /* disk trouble - stays in memory this run */ }
        IsConnected = _refreshToken is not null;
    }

    private void LoadTokens()
    {
        try
        {
            if (!File.Exists(AuthFile)) return;
            var node = JsonNode.Parse(File.ReadAllText(AuthFile));
            _refreshToken = node?["refresh_token"]?.GetValue<string>();
            _clientId = node?["client_id"]?.GetValue<string>();
            _connected = _refreshToken is not null;
        }
        catch { /* ignore */ }
    }

    // =============== loopback callback (TcpListener - no admin/urlacl needed) ===============

    private static async Task<(string? code, string? error, string? state)> WaitForCallbackAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, CallbackPort);
        listener.Start();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            using var client = await listener.AcceptTcpClientAsync(cts.Token);
            using var stream = client.GetStream();
            var buf = new byte[4096];
            var n = await stream.ReadAsync(buf, cts.Token);
            var request = Encoding.ASCII.GetString(buf, 0, n);
            var firstLine = request.Split('\n').FirstOrDefault() ?? "";           // "GET /callback?code=... HTTP/1.1"
            var url = firstLine.Split(' ').Skip(1).FirstOrDefault() ?? "";
            var q = url.Contains('?') ? url[(url.IndexOf('?') + 1)..] : "";
            var pairs = ParseQuery(q);
            var body = "<html><body style='font-family:sans-serif;background:#191712;color:#eee;text-align:center;padding-top:80px'>"
                     + "<h2>Spotify connected ✓</h2><p>You can close this tab and return to VibeCode.</p></body></html>";
            var resp = "HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: " + Encoding.UTF8.GetByteCount(body) + "\r\nConnection: close\r\n\r\n" + body;
            await stream.WriteAsync(Encoding.UTF8.GetBytes(resp), cts.Token);
            return (pairs.GetValueOrDefault("code"), pairs.GetValueOrDefault("error"), pairs.GetValueOrDefault("state"));
        }
        finally { listener.Stop(); }
    }

    // =============== helpers ===============

    /// <summary>Pick the album image closest to 64px (titlebar thumb is 30px). Spotify returns largest first.</summary>
    private static string? PickAlbumArt(JsonArray? images)
    {
        if (images is null || images.Count == 0) return null;
        string? best = null;
        var bestDist = int.MaxValue;
        foreach (var img in images)
        {
            var url = img?["url"]?.GetValue<string>();
            if (string.IsNullOrEmpty(url)) continue;
            var h = img?["height"]?.GetValue<int>() ?? 0;
            var dist = Math.Abs(h - 64);
            if (dist < bestDist) { bestDist = dist; best = url; }
        }
        return best ?? images.LastOrDefault()?["url"]?.GetValue<string>();
    }

    private static string Form(Dictionary<string, string> d) =>
        string.Join("&", d.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

    private static Dictionary<string, string> ParseQuery(string q)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = part.IndexOf('=');
            if (i < 0) d[Uri.UnescapeDataString(part)] = "";
            else d[Uri.UnescapeDataString(part[..i])] = Uri.UnescapeDataString(part[(i + 1)..]);
        }
        return d;
    }

    private static string RandBase64Url(int bytes)
    {
        var b = new byte[bytes];
        RandomNumberGenerator.Fill(b);
        return Base64Url(b);
    }

    private static string Base64Url(byte[] b) =>
        Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
