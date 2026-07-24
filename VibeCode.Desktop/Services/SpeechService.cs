using System.IO;
using System.Net.Http;
using System.Text;
using NAudio.Wave;
using Whisper.net;

namespace VibeCode.Services;

public enum SpeechState { Idle, Recording, Transcribing, Downloading }

/// <summary>
/// Offline microphone dictation (push-to-talk). Records the default mic at 16 kHz mono 16-bit — exactly the format
/// Whisper wants, so no resampling — then transcribes locally with Whisper.net (whisper.cpp). The ~142 MB English
/// model is downloaded once to %APPDATA%\VibeCode\whisper on first use; everything after is fully offline, no API key.
/// Single instance: only one capture runs at a time (the composer that started it owns it).
/// </summary>
public sealed class SpeechService
{
    public static SpeechService Instance { get; } = new();
    private SpeechService() { }

    private static readonly string ModelDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VibeCode", "whisper");
    private static string ModelPath => Path.Combine(ModelDir, "ggml-base.en.bin");
    // ggml base.en — good dictation accuracy with punctuation/casing, ~142 MB, comfortably faster-than-realtime on CPU.
    private const string ModelUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.en.bin";

    // Bound a single transcription: a wedged native inference used to leave the UI pinned on "transcribing…"
    // indefinitely (until the user manually poked the mic). After this budget the attempt is abandoned, State
    // returns to Idle, and the caller surfaces a failure. Recording/downloading have their own separate limits.
    private static readonly TimeSpan TranscribeTimeout = TimeSpan.FromSeconds(60);

    public SpeechState State { get; private set; } = SpeechState.Idle;
    public bool IsBusy => State is not SpeechState.Idle;
    public bool ModelReady => File.Exists(ModelPath);

    private WhisperFactory? _factory;       // expensive (loads the model into RAM) — built once, reused

    // ================================ CAPTURE OWNERSHIP RULE — read before editing ================================
    // There is exactly ONE authoritative notion of "which capture is current": the monotonically increasing token
    // handed out by StartRecording (== _captureGeneration). Every mutation of State, of _current, and of the CALLER's
    // own mic bookkeeping must be a no-op unless the mutator still holds the current token (Owns(token)).
    //
    //   * StartRecording issues a NEW token; ForceReset/CancelRecording BURN the current one (nobody owns it after).
    //   * StopAndTranscribeAsync takes the token as an argument. It performs the whole Recording -> Transcribing
    //     transition SYNCHRONOUSLY, before its first await: it flips State and detaches _current on entry. A second
    //     concurrent call therefore fails the entry guard and can never transcribe/insert the same audio twice.
    //   * All per-capture objects (WaveInEvent/buffer/writer/TCS) live on one immutable Capture instance and the NAudio
    //     event handlers close over THAT instance — never over fields — so a late callback from an abandoned capture
    //     can never write into, or complete, a newer one.
    //   * A disowned continuation must touch NOTHING: not State, not the caller's visuals, not the caller's ownership.
    // MainWindow.ToggleMic mirrors this with _micToken; keep the two rules identical.
    // ============================================================================================================

    private sealed class Capture
    {
        public required int Gen;
        public required WaveInEvent WaveIn;
        public required MemoryStream Buffer;
        public required WaveFileWriter Writer;
        public required TaskCompletionSource<byte[]> Tcs;
    }

    private Capture? _current;
    private int _captureGeneration;          // 0 is never issued, so token 0 always means "owns nothing"

    /// <summary>The token of the capture that is currently authoritative (0 = none).</summary>
    public int CurrentToken => _captureGeneration;

    /// <summary>True while <paramref name="token"/> is still the current capture. Callers must gate every state or
    /// visual mutation on this — see the ownership rule above.</summary>
    public bool Owns(int token) => token != 0 && token == _captureGeneration;

    /// <summary>Begin capturing the default microphone. Returns false with a human message if it can't start
    /// (no device, access denied). Call on the UI thread. <paramref name="token"/> is the capture's ownership token;
    /// pass it back to <see cref="StopAndTranscribeAsync"/> and use it with <see cref="Owns"/>.</summary>
    public bool StartRecording(out string? error, out int token)
    {
        error = null;
        token = 0;
        if (IsBusy) { error = "Already listening."; return false; }
        Capture? cap = null;
        try
        {
            if (WaveInEvent.DeviceCount == 0) { error = "No microphone found."; return false; }
            var gen = ++_captureGeneration;      // this capture now owns State; everyone older is disowned
            var buffer = new MemoryStream();
            var waveIn = new WaveInEvent { WaveFormat = new WaveFormat(16000, 16, 1), BufferMilliseconds = 50 };
            var writer = new WaveFileWriter(buffer, waveIn.WaveFormat);
            var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            cap = new Capture { Gen = gen, WaveIn = waveIn, Buffer = buffer, Writer = writer, Tcs = tcs };

            // Handlers close over THIS capture's locals — never over fields. A callback already queued on the NAudio
            // thread when a newer capture starts therefore finalizes its OWN wav and completes its OWN TCS.
            waveIn.DataAvailable += (_, e) => { try { writer.Write(e.Buffer, 0, e.BytesRecorded); } catch { /* stopped */ } };
            waveIn.RecordingStopped += (_, _) =>
            {
                try { writer.Dispose(); } catch { /* header finalize best-effort */ }   // flushes the RIFF header
                tcs.TrySetResult(buffer.ToArray());                                     // valid after writer dispose
            };

            _current = cap;
            waveIn.StartRecording();
            State = SpeechState.Recording;
            token = gen;
            // Mute the competition: dictating over music transcribes the music too. Fire-and-forget so a slow Spotify
            // round trip can't delay the mic going live; it self-serializes and no-ops when the extension is off.
            // Every path that ends this capture (transcribe/silence/failure/cancel/force-reset) undoes it.
            _ = SpotifyService.Instance.DuckAsync();
            return true;
        }
        catch (Exception ex)
        {
            error = "Microphone error: " + ex.Message;
            if (cap is not null)
            {
                cap.Tcs.TrySetResult(Array.Empty<byte>());
                DisposeCapture(cap);
                if (ReferenceEquals(_current, cap)) _current = null;
            }
            State = SpeechState.Idle;            // nothing is live, so State must say so
            return false;
        }
    }

    /// <summary>Stop the mic, (download the model on first ever use), transcribe, and return the recognized text
    /// ("" if silence). Transcription runs off the UI thread. Awaited from an async UI handler, it resumes on the
    /// UI thread. Throws on a genuine transcription failure so the caller can surface it.
    /// Returns "" immediately (touching nothing) unless <paramref name="token"/> still owns the current capture.</summary>
    public async Task<string> StopAndTranscribeAsync(int token)
    {
        // ---- synchronous section: no await may appear before _current is detached ----
        var cap = _current;
        if (cap is null || cap.Gen != token || !Owns(token) || State != SpeechState.Recording) return "";
        State = SpeechState.Transcribing;        // off Recording BEFORE the first await => re-entrancy impossible
        _current = null;                         // this call now solely owns cap; nobody else can stop or dispose it
        try { cap.WaveIn.StopRecording(); }      // fires RecordingStopped -> completes cap.Tcs with the WAV bytes
        catch { cap.Tcs.TrySetResult(Array.Empty<byte>()); }
        // ---- end synchronous section ----

        byte[] wav;
        try { wav = await cap.Tcs.Task; }
        finally { DisposeCapture(cap); }         // always OUR capture object — never a newer one's

        if (wav.Length <= 44)                    // 44-byte WAV header only = no audio captured
        {
            FinishIfStillOwned();
            return "";
        }

        try
        {
            if (!ModelReady)
            {
                SetIfStillOwned(SpeechState.Downloading);
                await DownloadModelAsync();
            }
            SetIfStillOwned(SpeechState.Transcribing);
            WhisperNativeRuntime.Configure();
            var factory = _factory ??= WhisperFactory.FromPath(ModelPath);

            // Bound the inference by TranscribeTimeout so a stalled native call (or an over-long clip) can't leave the
            // UI stuck on "transcribing…" forever. Cancel the pipeline cooperatively AND stop awaiting on the wall
            // clock, so this returns within the budget even if native processing ignores cancellation. On timeout we
            // throw; the finally below flips State back to Idle and the caller shows the failure.
            var timeoutCts = new CancellationTokenSource();
            var work = Task.Run(async () =>
            {
                using var processor = factory.CreateBuilder().WithLanguage("en").Build();
                using var ms = new MemoryStream(wav);
                var sb = new StringBuilder();
                await foreach (var seg in processor.ProcessAsync(ms).WithCancellation(timeoutCts.Token))
                    sb.Append(seg.Text);
                return sb.ToString().Trim();
            }, timeoutCts.Token);

            if (await Task.WhenAny(work, Task.Delay(TranscribeTimeout)) != work && !work.IsCompletedSuccessfully)
            {
                timeoutCts.Cancel();                                          // best-effort cooperative stop
                _ = work.ContinueWith(t => { _ = t.Exception; timeoutCts.Dispose(); }, // observe + dispose on its own time
                                      TaskScheduler.Default);
                throw new TimeoutException(
                    $"Transcription timed out after {TranscribeTimeout.TotalSeconds:0}s. Try again, or record a shorter clip.");
            }
            timeoutCts.Dispose();
            return await work;                                               // finished in time: real text or a real failure
        }
        finally { FinishIfStillOwned(); }

        // Only touch State while this call still owns the capture — a CancelRecording/ForceReset (and the newer
        // recording that usually follows) bumps the generation and takes ownership away from us.
        void SetIfStillOwned(SpeechState s) { if (Owns(token)) State = s; }

        // The capture is over (text, silence, or failure): go Idle and let Spotify resume. Gated on ownership for the
        // same reason as SetIfStillOwned — a disowned continuation must not resume music over the newer, LIVE capture
        // that took the duck from it. Whoever burned our token (AbandonCapture) owns the unduck instead.
        void FinishIfStillOwned()
        {
            if (!Owns(token)) return;
            State = SpeechState.Idle;
            _ = SpotifyService.Instance.UnduckAsync();
        }
    }

    /// <summary>Abandon an in-progress recording without transcribing. No-op unless a capture is actually recording —
    /// yanking State to Idle during a running transcription would corrupt the state machine.
    /// Returns true if a capture was dropped.</summary>
    public bool CancelRecording()
    {
        if (State != SpeechState.Recording || _current is null) return false;
        AbandonCapture();
        return true;
    }

    /// <summary>Last-resort escape from a state that never completed (a hung download/transcription). Forces Idle and
    /// disowns the in-flight call so its completion can't reset the state of whatever starts next.</summary>
    public void ForceReset() => AbandonCapture();

    private void AbandonCapture()
    {
        _captureGeneration++;                    // burn the token: any in-flight call is now disowned
        var cap = _current;
        _current = null;
        if (cap is not null)
        {
            try { cap.WaveIn.StopRecording(); } catch { /* ignore */ }
            cap.Tcs.TrySetResult(Array.Empty<byte>());
            DisposeCapture(cap);                 // disposes the WaveInEvent, so two captures can never be live at once
        }
        State = SpeechState.Idle;
        // Unconditional: this call just burned the token, so the transcription that would otherwise unduck is now
        // disowned and will deliberately do nothing. If we skip it here, paused music never comes back.
        _ = SpotifyService.Instance.UnduckAsync();
    }

    private static void DisposeCapture(Capture cap)
    {
        try { cap.WaveIn.Dispose(); } catch { /* ignore */ }
        try { cap.Writer.Dispose(); } catch { /* already disposed by RecordingStopped */ }
        try { cap.Buffer.Dispose(); } catch { /* ignore */ }
    }

    // Download the model to a temp file then move it into place, so a cancelled/failed download can't leave a
    // corrupt model that would fail to load forever. The temp name is UNIQUE PER ATTEMPT: a disowned-but-still-running
    // first attempt and a second attempt would otherwise write the same fixed path and die on a sharing violation.
    private static async Task DownloadModelAsync()
    {
        Directory.CreateDirectory(ModelDir);
        var tmp = ModelPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
            using (var resp = await http.GetAsync(ModelUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                await using var src = await resp.Content.ReadAsStreamAsync();
                await using var dst = File.Create(tmp);
                await src.CopyToAsync(dst);
            }
            // Another attempt may have finished first — its file is just as good, so keep it rather than racing a delete.
            if (File.Exists(ModelPath)) return;
            File.Move(tmp, ModelPath);
        }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort */ } }
    }
}
