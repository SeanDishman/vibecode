using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VibeCode.UI;   // Observable (INotifyPropertyChanged base), as used by the other extension services

namespace VibeCode.Services;

/// <summary>
/// Optional cloud backend for mic dictation: Groq's hosted Whisper large-v3 instead of the bundled offline model.
///
/// The whole capture side stays exactly where it was — <see cref="SpeechService"/> still owns the microphone, the
/// WAV, the ownership tokens and the Spotify duck. This service replaces one step: turning those bytes into text.
///
/// The trade it offers is accuracy and speed for privacy. large-v3 is the full-size 1550 M model; the offline path
/// runs <c>medium.en</c> (769 M), which is close behind it on ordinary English dictation but still loses ground on
/// strong accents and unusual proper nouns — and, on a machine with no usable GPU, takes seconds where this takes
/// one. The cost is that the clip LEAVES THE MACHINE, which the offline path never does, and it needs the user's own
/// Groq key. That is why it ships off and why the Settings card says so in as many words. It is now a preference
/// rather than a rescue: the offline model was <c>base.en</c> (74 M) when this was written, and the honest reason to
/// reach for Groq back then was that the local transcript was often wrong.
///
/// The key is sealed with DPAPI in its own file, never in settings.json — the same rule
/// <see cref="ApiKeyAccountService"/> follows, and the reason settings.json holds only the on/off flag.
/// </summary>
public sealed class GroqSpeechService : Observable
{
    public static GroqSpeechService Instance { get; } = new();
    private GroqSpeechService()
    {
        Load();
        LoadRefusals();
        // A VPN going on or off changes which address Groq sees, and whether it answers at all.
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += (_, _) => OnNetworkChanged();
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += (_, _) => OnNetworkChanged();
    }

    /// <summary>Whisper large-v3: Groq's most accurate transcription model. There is also a <c>-turbo</c> variant
    /// that is cheaper and faster but measurably worse on hard audio — this path exists precisely because the user
    /// asked for the best one, and a dictation clip is a few seconds either way.</summary>
    public const string Model = "whisper-large-v3";

    private const string TranscribeUrl = "https://api.groq.com/openai/v1/audio/transcriptions";
    private const string ModelsUrl = "https://api.groq.com/openai/v1/models";
    /// <summary>Groq's edge reports the public address it sees here, and still answers while the API refuses it.</summary>
    private const string TraceUrl = "https://api.groq.com/cdn-cgi/trace";

    /// <summary>Groq's free tier rejects uploads over 25 MB. At the 16 kHz mono 16-bit
    /// <see cref="SpeechService"/> records, that is a little over 13 minutes — far past any dictation clip — but
    /// checking it here turns an opaque HTTP 413 into a sentence that says what to do.</summary>
    private const int MaxUploadBytes = 25 * 1024 * 1024;

    // Its own client, not ApiKeyAccountService's: that one is a 20 s validation client, and an upload plus a
    // full-size-model inference legitimately takes longer. The real deadline is SpeechService's single budget,
    // which cancels this through the token; this timeout is only a backstop for a connection that never answers.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };
    private static readonly HttpClient UploadHttp = new(new SpeechHttpHandler()) { Timeout = TimeSpan.FromMinutes(2) };

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("VibeCode.GroqSpeech.v1");
    private static string FilePath => Path.Combine(AppSettings.Dir, "groq-speech.json");

    private string _secret = "";   // base64 DPAPI ciphertext, never the key itself
    private string _masked = "";

    /// <summary>Use Groq instead of the offline model for dictation. Mirrors AppSettings so the Settings card can
    /// bind straight to it, exactly like <see cref="WeatherService.Enabled"/>.</summary>
    public bool Enabled
    {
        get => AppSettings.Current.GroqSpeechEnabled;
        set
        {
            if (AppSettings.Current.GroqSpeechEnabled == value) return;
            AppSettings.Current.GroqSpeechEnabled = value;
            AppSettings.Current.Save();
            RaiseState();
        }
    }

    /// <summary>Re-read the settings-backed flag after something else wrote settings.json.</summary>
    public void NotifyEnabledChanged() => RaiseState();

    public bool HasKey => _secret.Length > 0;

    /// <summary>"gsk_abc…7T4a" — enough to recognise a key without revealing it.</summary>
    public string MaskedKey => _masked;

    /// <summary>True when dictation will actually reach Groq. <see cref="Enabled"/> without a key is a deliberately
    /// LOUD failure rather than a quiet fall back to the offline model: silently ignoring a switch the user just
    /// turned on is how a setting comes to look broken. The card warns, and the mic reports it on first use.</summary>
    public bool Active => Enabled && HasKey;

    public string StatusText => !Enabled ? "Off" : !HasKey ? "No API key saved"
        : _reachable == false ? $"Not reachable from this connection · dictation uses the offline model ({_problem})"
        : $"Whisper large-v3 · {_masked}";

    /* ── reachability ─────────────────────────────────────────────────────── */
    // Groq refuses some connections outright - a VPN exit in a region it doesn't serve gets turned away before any
    // audio is read - and that has nothing to do with the clip or the key. So dictation checks, with the free models
    // call and no audio, whether Groq answers from this connection, and uses the offline model while it doesn't. The
    // check starts with each recording when the last one is a couple of minutes old, and again whenever the network
    // changes; a real upload that fails or succeeds updates it too.
    //
    // A refusal is also remembered by the public address Groq refused (groq-refused-addresses.json), across network changes
    // and restarts. Back on that address - the same VPN exit - dictation goes straight to the local model without
    // asking Groq or saying anything; the address is asked about again once a day in case Groq lets it in by then.

    /// <summary>How long a verdict stands before a new recording checks again.</summary>
    public static readonly TimeSpan ReachabilityFreshFor = TimeSpan.FromMinutes(2);
    /// <summary>How long a remembered refusal stands before that address is asked about again.</summary>
    public static readonly TimeSpan RefusalRecheckAfter = TimeSpan.FromDays(1);
    private const int MaxRememberedRefusals = 64;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan TraceTimeout = TimeSpan.FromSeconds(3);
    private readonly object _probeGate = new();
    private Task<bool>? _probe;
    private volatile int _verdict;   // 0 not checked, 1 answers from here, 2 out of reach
    private volatile string _problem = "";
    private volatile string? _address;   // the public address the last check ran from; null when unknown
    private DateTime _checkedUtc = DateTime.MinValue;
    private CancellationTokenSource? _networkSettle;
    private readonly object _refusalGate = new();
    private readonly Dictionary<string, DateTimeOffset> _refusals = new(StringComparer.OrdinalIgnoreCase);
    private string? _refusalNotice;   // an address refused for the first time, until a dictation says so once
    private static string RefusalsPath => Path.Combine(AppSettings.Dir, "groq-refused-addresses.json");

    private bool? _reachable => _verdict switch { 1 => true, 2 => false, _ => null };

    /// <summary>Whether Groq answered from this connection at the last check; null until something checked.</summary>
    public bool? Reachable => _reachable;
    /// <summary>Why Groq is out of reach, in words the composer can show; empty while it answers.</summary>
    public string UnreachableReason => _reachable == false ? _problem : "";

    /// <summary>Whether Groq answers from this connection, checked at most every <see cref="ReachabilityFreshFor"/>
    /// unless <paramref name="force"/>d. Concurrent callers share one check.</summary>
    public Task<bool> CheckReachabilityAsync(bool force = false) => CheckReachabilityAsync(Http, force);

    internal Task<bool> CheckReachabilityAsync(HttpClient client, bool force)
    {
        lock (_probeGate)
        {
            if (!force && _reachable is { } known && DateTime.UtcNow - _checkedUtc < ReachabilityFreshFor) return Task.FromResult(known);
            if (_probe is { IsCompleted: false } running) return running;
            return _probe = RunProbeAsync(client);
        }
    }

    private async Task<bool> RunProbeAsync(HttpClient client)
    {
        var address = await LookUpAddressAsync(client).ConfigureAwait(false);
        _address = address;
        if (address is not null && IsRecentlyRefused(address))
        {
            Record(false, RememberedRefusalText(address));
            return false;
        }
        var (reachable, problem, refused) = await ProbeAsync(client, Key(), CancellationToken.None).ConfigureAwait(false);
        if (address is not null)
        {
            if (refused) RememberRefusal(address);
            else if (reachable) ForgetRefusal(address);
        }
        Record(reachable, refused && address is not null ? RefusedAddressText(address) : problem);
        return reachable;
    }

    /// <summary>A real upload got through: Groq answers from this connection.</summary>
    public void MarkReachable()
    {
        if (_address is { } address) ForgetRefusal(address);
        Record(true, null);
    }

    /// <summary>A real upload failed: treat Groq as out of reach until the next check.</summary>
    public void MarkUnreachable(string problem) => Record(false, problem);

    /// <summary>A real upload was refused although the check let it through: remember the address like a refused check.</summary>
    internal void MarkConnectionRefused()
    {
        if (_address is { } address)
        {
            RememberRefusal(address);
            Record(false, RefusedAddressText(address));
        }
        else Record(false, RefusedConnectionText);
    }

    /// <summary>True when Groq is out of reach because it refused this address before: the local model answers quietly.</summary>
    internal bool RefusalRemembered => _reachable == false && _address is { } address && IsRemembered(address);

    /// <summary>The address Groq has just refused for the first time, once, so one dictation can say why it went
    /// local; null afterwards, and for a refusal that belonged to an earlier connection.</summary>
    internal string? TakeRefusalNotice()
    {
        string? notice;
        lock (_refusalGate)
        {
            notice = _refusalNotice;
            _refusalNotice = null;
        }
        return notice is not null && string.Equals(notice, _address, StringComparison.OrdinalIgnoreCase) ? notice : null;
    }

    private void Record(bool reachable, string? problem)
    {
        // Problems are shown inside a sentence, so they carry no closing full stop of their own.
        var text = reachable ? "" : (problem ?? "Groq didn't answer").TrimEnd().TrimEnd('.');
        var changed = _reachable != reachable || _problem != text;
        _problem = text;
        _checkedUtc = DateTime.UtcNow;
        _verdict = reachable ? 1 : 2;
        if (changed) RaiseState();
    }

    /// <summary>The network changed: forget the verdict and, once it settles (a VPN reconnect fires several changes),
    /// check again so the next dictation already knows.</summary>
    private void OnNetworkChanged()
    {
        _verdict = 0;
        _address = null;   // a VPN switching on or off changes the address Groq sees; the next check looks again
        CancellationTokenSource settle;
        lock (_probeGate)
        {
            _networkSettle?.Cancel();
            _networkSettle = settle = new CancellationTokenSource();
        }
        if (!Active) return;
        _ = Task.Delay(TimeSpan.FromSeconds(3), settle.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled && Active) _ = CheckReachabilityAsync(force: true);
        }, TaskScheduler.Default);
    }

    /// <summary>One check: the free models call with the key. <c>Refused</c> means Groq turned this connection away
    /// itself (see <see cref="IsConnectionRefusal"/>) rather than the key. Pure apart from the request, for the tests.</summary>
    internal static async Task<(bool Reachable, string? Problem, bool Refused)> ProbeAsync(HttpClient client, string key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key)) return (false, "Groq is on but no API key is saved", false);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, ModelsUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var res = await client.SendAsync(req, ct).WaitAsync(ProbeTimeout, ct).ConfigureAwait(false);
            var status = (int)res.StatusCode;
            if (status is 403 or 451)
            {
                var body = await res.Content.ReadAsStringAsync(ct).WaitAsync(ProbeTimeout, ct).ConfigureAwait(false);
                return IsConnectionRefusal(status, body)
                    ? (false, RefusedConnectionText, true)
                    : (false, "Groq denied this key access" + (ErrorMessage(body) is { } detail ? ": " + detail : ""), false);
            }
            return status switch
            {
                >= 200 and < 300 or 429 => (true, null, false),   // rate limited still means it answers from here
                401 => (false, "Groq rejected the API key", false),
                >= 500 => (false, $"Groq is having trouble right now (HTTP {status})", false),
                _ => (false, $"Groq answered HTTP {status}", false),
            };
        }
        catch (TimeoutException) { return (false, $"Groq didn't answer within {ProbeTimeout.TotalSeconds:0}s from this connection", false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return (false, "Groq didn't answer from this connection", false); }
        catch (HttpRequestException ex) { return (false, "Couldn't reach Groq from this connection: " + ex.Message, false); }
    }

    internal const string RefusedConnectionText = "Groq refuses this connection, likely a VPN or a region it doesn't serve";

    private static string RefusedAddressText(string address) =>
        $"Groq refuses this connection's IP address {address}, likely a VPN or a region it doesn't serve";

    private static string RememberedRefusalText(string address) =>
        $"Groq refused this connection's IP address {address} before, likely a VPN or a region it doesn't serve";

    /// <summary>
    /// True when Groq turned the CONNECTION away rather than the key or the request. Measured from a VPN exit Groq
    /// refuses (2026-11-05, NL): the models and transcription paths answer 403 <c>{"error":{"message":"Access denied.
    /// Please check your network settings."}}</c> even with no key sent, so the edge decides before a key matters. A 403 that names
    /// anything else (a key's project permissions) is about the key, and is never remembered against the address.
    /// A 403 with no Groq error at all is an edge block page; 451 is a legal/region block.
    /// </summary>
    internal static bool IsConnectionRefusal(int status, string? body)
    {
        if (status == 451) return true;
        if (status != 403) return false;
        if (ErrorMessage(body ?? "") is not { } message) return true;
        return message.Contains("network", StringComparison.OrdinalIgnoreCase)
               || message.Contains("region", StringComparison.OrdinalIgnoreCase)
               || message.Contains("country", StringComparison.OrdinalIgnoreCase)
               || message.Contains("location", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The public address this connection reaches Groq from, as Groq's own edge reports it (the same host the
    /// API lives on, so no other service learns anything). Null when the edge doesn't say; the check then runs exactly
    /// as it did before addresses were remembered.</summary>
    private static async Task<string?> LookUpAddressAsync(HttpClient client)
    {
        try
        {
            using var stop = new CancellationTokenSource(TraceTimeout);
            using var res = await client.GetAsync(TraceUrl, stop.Token).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return null;
            return ParseTraceAddress(await res.Content.ReadAsStringAsync(stop.Token).ConfigureAwait(false));
        }
        catch (Exception) { return null; }   // unknown address: never a reason for the dictation itself to fail
    }

    /// <summary>The <c>ip=</c> line of a trace answer, normalised so one address is always written one way.</summary>
    internal static string? ParseTraceAddress(string? trace)
    {
        foreach (var line in (trace ?? "").Split('\n'))
        {
            var entry = line.Trim();
            if (entry.StartsWith("ip=", StringComparison.Ordinal) && IPAddress.TryParse(entry[3..], out var address))
                return address.ToString();
        }
        return null;
    }

    /* ── remembered refusals ──────────────────────────────────────────────── */

    private bool IsRecentlyRefused(string address)
    {
        lock (_refusalGate)
            return _refusals.TryGetValue(address, out var at) && DateTimeOffset.UtcNow - at < RefusalRecheckAfter;
    }

    private bool IsRemembered(string address)
    {
        lock (_refusalGate) return _refusals.ContainsKey(address);
    }

    private void RememberRefusal(string address)
    {
        lock (_refusalGate)
        {
            var known = _refusals.ContainsKey(address);
            _refusals[address] = DateTimeOffset.UtcNow;
            if (!known) _refusalNotice = address;   // say why once; a re-confirmed refusal stays quiet
            while (_refusals.Count > MaxRememberedRefusals)
                _refusals.Remove(_refusals.MinBy(entry => entry.Value).Key);
            PersistRefusals();
        }
    }

    private void ForgetRefusal(string address)
    {
        lock (_refusalGate)
        {
            if (!_refusals.Remove(address)) return;
            if (string.Equals(_refusalNotice, address, StringComparison.OrdinalIgnoreCase)) _refusalNotice = null;
            PersistRefusals();
        }
    }

    private void LoadRefusals()
    {
        try
        {
            if (!File.Exists(RefusalsPath)) return;
            if (JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllText(RefusalsPath)) is not { } saved) return;
            lock (_refusalGate)
                foreach (var (address, at) in saved)
                    if (IPAddress.TryParse(address, out var parsed)) _refusals[parsed.ToString()] = at;
        }
        catch { /* unreadable: nothing remembered, so every address is simply checked again */ }
    }

    /// <summary>Called under <see cref="_refusalGate"/>, so two checks finishing together can't write an older list last.</summary>
    private void PersistRefusals()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Dir);
            File.WriteAllText(RefusalsPath, JsonSerializer.Serialize(_refusals, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* still remembered for this run */ }
    }

    /* ── key storage ──────────────────────────────────────────────────────── */

    /// <summary>Seal a verified key to this Windows user and persist it. Refuses to store anything in the clear:
    /// if DPAPI fails there is no key rather than a readable one.</summary>
    public bool SaveKey(string key)
    {
        key = (key ?? "").Trim();
        if (key.Length == 0) return false;
        if (Protect(key) is not { Length: > 0 } sealedKey) return false;
        _secret = sealedKey;
        _masked = Mask(key);
        Persist();
        RaiseState();
        return true;
    }

    public void ClearKey()
    {
        if (!HasKey) return;
        _secret = "";
        _masked = "";
        Persist();
        RaiseState();
    }

    /// <summary>The plaintext key, unsealed on demand. Empty when there is none, or when the blob was written by a
    /// different Windows user/machine and can no longer be unsealed.</summary>
    private string Key()
    {
        if (_secret.Length == 0) return "";
        try
        {
            var blob = Convert.FromBase64String(_secret);
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser));
        }
        catch { return ""; }
    }

    /* ── transcription ────────────────────────────────────────────────────── */

    /// <summary>Transcribe one finished WAV. Throws with a sentence the composer can show verbatim — every failure
    /// here is something the user can act on (a bad key, a rate limit, no network), so none of them are swallowed.
    /// </summary>
    public async Task<string> TranscribeAsync(byte[] wav, CancellationToken ct)
    {
        try { return await TranscribeOnWorkerAsync(wav, Key, UploadHttp, ct).ConfigureAwait(false); }
        catch (GroqConnectionRefusedException)
        {
            MarkConnectionRefused();
            throw;
        }
    }

    internal static Task<string> TranscribeOnWorkerAsync(byte[] wav, Func<string> readKey, HttpClient client, CancellationToken ct) =>
        SpeechRequestWorker.Run(cancel => Transcribe(wav, readKey, client, cancel), ct);

    private static string Transcribe(byte[] wav, Func<string> readKey, HttpClient client, CancellationToken ct)
    {
        if (readKey() is not { Length: > 0 } key)
            throw new InvalidOperationException(
                "Groq speech-to-text is on but no API key is saved. Add one in Settings ▸ Extensions ▸ Speech to text, "
                + "or switch the extension off to go back to the offline model.");

        if (wav.Length > MaxUploadBytes)
            throw new InvalidOperationException(
                $"That clip is {wav.Length / (1024.0 * 1024.0):0.#} MB and Groq accepts {MaxUploadBytes / (1024 * 1024)} MB. "
                + "Record a shorter one.");

        using var form = new MultipartFormDataContent();
        var audio = new ByteArrayContent(wav);
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        // The filename matters: Groq picks the decoder off the extension, and a nameless part is rejected outright.
        form.Add(audio, "file", "dictation.wav");
        form.Add(new StringContent(Model), "model");
        form.Add(new StringContent("json"), "response_format");
        form.Add(new StringContent("en"), "language");        // same language pin as the offline path's WithLanguage("en")
        form.Add(new StringContent("0"), "temperature");      // dictation wants the literal words, not a fluent paraphrase

        using var req = new HttpRequestMessage(HttpMethod.Post, TranscribeUrl) { Content = form };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        HttpResponseMessage res;
        // Synchronous HTTP on our dedicated worker: neither request preparation nor response processing needs
        // the WPF dispatcher or a free Task.Run worker. The cancellation token still stops an abandoned upload.
        try { res = client.Send(req, HttpCompletionOption.ResponseContentRead, ct); }
        // DNS/TLS/offline. Unwrapped, this surfaces as a bare "No such host is known", which reads like a bug in
        // the app rather than a machine that is not on the internet.
        catch (HttpRequestException ex) { throw new HttpRequestException("Couldn't reach Groq: " + ex.Message, ex); }

        using (res)
        {
            // ResponseContentRead has already buffered the body; this only decodes those bytes.
            var body = res.Content.ReadAsStringAsync(ct).GetAwaiter().GetResult();
            if (IsConnectionRefusal((int)res.StatusCode, body)) throw new GroqConnectionRefusedException();
            if (!res.IsSuccessStatusCode) throw new HttpRequestException(Explain((int)res.StatusCode, body));
            return ParseText(body);
        }
    }

    /// <summary>Pull the transcript out of <c>{"text":"…"}</c>. A success with no text field is silence, not a
    /// failure — the caller treats "" as "nothing was heard".</summary>
    private static string ParseText(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("text", out var text)
                ? text.GetString()?.Trim() ?? ""
                : "";
        }
        catch (JsonException)
        {
            throw new HttpRequestException("Groq returned a response this build could not read.");
        }
    }

    /// <summary>Turn an HTTP failure into something worth reading. Groq reports the real cause in
    /// <c>{"error":{"message":…}}</c>, which is far more useful than the status code on the 400s.</summary>
    private static string Explain(int status, string body)
    {
        var detail = ErrorMessage(body);
        return status switch
        {
            401 => "Groq rejected the API key. Check it in Settings ▸ Extensions ▸ Speech to text.",
            403 => "Groq denied access to speech transcription. Check this key's permissions and project access.",
            413 => "Groq refused the clip as too large. Record a shorter one.",
            429 => "Groq is rate limiting this key" + (detail is null ? "." : $": {detail}"),
            >= 500 => $"Groq is having trouble right now (HTTP {status}). Try again in a moment.",
            _ => detail is null ? $"Groq returned HTTP {status}." : $"Groq returned HTTP {status}: {detail}",
        };
    }

    private static string? ErrorMessage(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            // Any other JSON shape (an array, a bare string) throws from TryGetProperty, so check each level's kind.
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String
                && message.GetString() is { Length: > 0 } text)
                return text;
        }
        catch (JsonException) { /* not JSON - the status code alone will have to do */ }
        return null;
    }

    /// <summary>Confirm a key before saving it, so a typo is caught in the dialog instead of surfacing later as a
    /// failed dictation. Uses the free GET /v1/models, so it costs nothing — the same trick
    /// <see cref="ApiKeyAccountService.ValidateAsync"/> uses for the coding providers.</summary>
    public static async Task<ApiKeyValidation> ValidateKeyAsync(string key, CancellationToken ct = default)
    {
        key = (key ?? "").Trim();
        if (key.Length < 8) return ApiKeyValidation.Bad("That key looks too short.");

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, ModelsUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var res = await Http.SendAsync(req, ct).WaitAsync(TimeSpan.FromSeconds(20), ct);

            if (res.IsSuccessStatusCode) return ApiKeyValidation.Good("Key accepted — dictation now runs on Groq.");
            return (int)res.StatusCode switch
            {
                401 or 403 => ApiKeyValidation.Bad("Groq rejected that key."),
                429 => ApiKeyValidation.Good("Key is valid (rate limited right now)."),
                _ => ApiKeyValidation.Bad($"Groq returned HTTP {(int)res.StatusCode}."),
            };
        }
        catch (OperationCanceledException) { return ApiKeyValidation.Bad("Timed out reaching Groq."); }
        catch (Exception ex) { return ApiKeyValidation.Bad("Couldn't reach Groq: " + ex.Message); }
    }

    /* ── storage ──────────────────────────────────────────────────────────── */

    private static string Mask(string key)
    {
        if (string.IsNullOrEmpty(key)) return "";
        if (key.Length <= 10) return new string('•', key.Length);
        return key[..Math.Min(7, key.Length)] + "…" + key[^4..];
    }

    /// <summary>DPAPI, current-user scope: the file is useless on any other account or machine.</summary>
    private static string Protect(string plain)
    {
        try
        {
            var blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(blob);
        }
        catch
        {
            return "";   // never silently fall back to plaintext for a credential
        }
    }

    private sealed class Persisted
    {
        public string Secret { get; set; } = "";
        public string Masked { get; set; } = "";
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            if (JsonSerializer.Deserialize<Persisted>(File.ReadAllText(FilePath)) is not { } saved) return;
            _secret = saved.Secret ?? "";
            _masked = saved.Masked ?? "";
        }
        catch { /* unreadable: behave as "no key saved" rather than failing the app's startup */ }
    }

    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Dir);
            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(new Persisted { Secret = _secret, Masked = _masked },
                                         new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* the in-memory key still works for this run; the card shows what is actually stored next launch */ }
    }

    private void RaiseState()
    {
        Raise(nameof(Enabled));
        Raise(nameof(HasKey));
        Raise(nameof(MaskedKey));
        Raise(nameof(Active));
        Raise(nameof(StatusText));
        Raise(nameof(Reachable));
        Raise(nameof(UnreachableReason));
    }
}

/// <summary>Groq turned the connection away (a VPN exit or a region it doesn't serve) before reading the clip or the
/// key. Still an <see cref="HttpRequestException"/>, so every caller treats it as the connection failure it is.</summary>
internal sealed class GroqConnectionRefusedException() : HttpRequestException(GroqSpeechService.RefusedConnectionText + ".");
