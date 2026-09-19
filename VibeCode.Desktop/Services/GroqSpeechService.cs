using System.IO;
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
    private GroqSpeechService() { Load(); }

    /// <summary>Whisper large-v3: Groq's most accurate transcription model. There is also a <c>-turbo</c> variant
    /// that is cheaper and faster but measurably worse on hard audio — this path exists precisely because the user
    /// asked for the best one, and a dictation clip is a few seconds either way.</summary>
    public const string Model = "whisper-large-v3";

    private const string TranscribeUrl = "https://api.groq.com/openai/v1/audio/transcriptions";
    private const string ModelsUrl = "https://api.groq.com/openai/v1/models";

    /// <summary>Groq's free tier rejects uploads over 25 MB. At the 16 kHz mono 16-bit
    /// <see cref="SpeechService"/> records, that is a little over 13 minutes — far past any dictation clip — but
    /// checking it here turns an opaque HTTP 413 into a sentence that says what to do.</summary>
    private const int MaxUploadBytes = 25 * 1024 * 1024;

    // Its own client, not ApiKeyAccountService's: that one is a 20 s validation client, and an upload plus a
    // full-size-model inference legitimately takes longer. The real deadline is SpeechService's single budget,
    // which cancels this through the token; this timeout is only a backstop for a connection that never answers.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("VibeCode.GroqSpeech.v1");
    private static string FilePath => Path.Combine(AppSettings.Dir, "groq-speech.json");

    private string _secret = "";   // base64 DPAPI ciphertext, never the key itself
    private string _masked = "";

    /// <summary>Use Groq instead of the offline model for dictation. Mirrors AppSettings so the Settings card can
    /// bind straight to it, exactly like <see cref="GamesService.Enabled"/>.</summary>
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

    public string StatusText => !Enabled ? "Off" : HasKey ? $"Whisper large-v3 · {_masked}" : "No API key saved";

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
        if (Key() is not { Length: > 0 } key)
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
        try { res = await Http.SendAsync(req, ct); }
        // DNS/TLS/offline. Unwrapped, this surfaces as a bare "No such host is known", which reads like a bug in
        // the app rather than a machine that is not on the internet.
        catch (HttpRequestException ex) { throw new HttpRequestException("Couldn't reach Groq: " + ex.Message, ex); }

        using (res)
        {
            var body = await res.Content.ReadAsStringAsync(ct);
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
            return JsonDocument.Parse(body).RootElement.TryGetProperty("text", out var text)
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
            401 or 403 => "Groq rejected the API key. Check it in Settings ▸ Extensions ▸ Speech to text.",
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
            if (JsonDocument.Parse(body).RootElement.TryGetProperty("error", out var error)
                && error.TryGetProperty("message", out var message)
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
    }
}
