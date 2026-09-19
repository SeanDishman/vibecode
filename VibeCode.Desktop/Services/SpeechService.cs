using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using NAudio.Wave;
using Whisper.net;

namespace VibeCode.Services;

public enum SpeechState { Idle, Recording, Transcribing, Downloading }

/// <summary>
/// Offline microphone dictation (push-to-talk). Records the default mic at 16 kHz mono 16-bit — exactly the format
/// Whisper wants, so no resampling — then transcribes locally with Whisper.net (whisper.cpp). The ~1.5 GB English
/// model is downloaded once to %APPDATA%\VibeCode\whisper on first use; everything after is fully offline, no API key.
/// Single instance: only one capture runs at a time (the composer that started it owns it).
/// </summary>
public sealed class SpeechService
{
    public static SpeechService Instance { get; } = new();
    private SpeechService() { }

    private static readonly string ModelDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VibeCode", "whisper");

    // ================================ WHICH MODEL THE OFFLINE PATH RUNS ================================
    // medium.en — OpenAI's 769 M-parameter English model, the second-largest whisper there is. It replaced base.en
    // (74 M, ~142 MB) so that the offline path is a real alternative to the Groq one rather than the cheap fallback:
    // base.en mangles proper nouns and technical jargon, which is most of what gets dictated into a coding agent.
    // On one 10.5 s test clip base.en produced "the Grock speech service ... the Kabir nets deployment manifest"
    // where medium.en produced "the Grok speech service ... the Kubernetes deployment manifest".
    //
    // All figures below are measured, on a 6c/12t Ryzen 3600 + GTX 1660 SUPER, same 10.5 s clip, warm file cache:
    //
    //   * DOWNLOAD. 1.46 GiB instead of 142 MB — a ten-minute wait on an ordinary connection, not a ten-second one.
    //     It therefore reports progress (DownloadedBytes/DownloadTotalBytes) and is policed by a STALL clock rather
    //     than a fixed deadline; a slow-but-moving download must never be force-reset. See DownloadModelAsync.
    //   * MEMORY. On the GPU path the weights sit in VRAM and this process peaks at ~300 MB of host RAM; on the CPU
    //     fallback everything is host-side and it peaks at ~2.1 GB (base.en: ~400 MB). Note that the "~5 GB VRAM" in
    //     OpenAI's own table is the PyTorch implementation — ggml f16 is far leaner, and this fits the 6 GB card it
    //     was measured on with room to spare.
    //   * TIME ON THE GPU. Load 1.9 s -> 2.6 s, inference 0.31 s -> 1.10 s. Still ~10x faster than realtime; the
    //     user cannot feel this.
    //   * TIME ON THE CPU FALLBACK. Load 0.3 s -> 2.3 s, inference 1.66 s -> 16.0 s. This is the one real cost of
    //     the swap, and it is what TranscribeTimeout is sized against. It is close to flat per 30 s of audio rather
    //     than proportional to the clip, because whisper's encoder runs on padded 30 s windows either way — so a
    //     two-second "yes, do that" on a GPU-less machine costs about what a twenty-second one does. If that ever
    //     needs fixing, the fix is to pick the model from WhisperNativeRuntime.UsesGpu rather than to shrink this
    //     one back for everybody.
    // ==================================================================================================
    private const string ModelFile = "ggml-medium.en.bin";
    private static string ModelPath => Path.Combine(ModelDir, ModelFile);
    private const string ModelUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/" + ModelFile;

    /// <summary>Exact byte length of <see cref="ModelUrl"/>. A truncated download answers 200 and then simply stops,
    /// leaving a plausible-looking file that fails to load for ever; at 1.46 GiB that is a long wait to be told
    /// nothing useful. Checked before the file is moved into place, so a short read is retried rather than kept.</summary>
    private const long ModelBytes = 1_533_774_781;

    /// <summary>Model files this app used to download and no longer loads. Deleted once the current model is safely
    /// in place, so an upgrade does not silently leave a superseded model behind for ever. Re-downloadable, and
    /// nothing but this app writes to <see cref="ModelDir"/>.</summary>
    private static readonly string[] SupersededModelFiles = { "ggml-base.en.bin" };

    /// <summary>Human-readable download size, for the one-time-fetch warnings the composer shows.</summary>
    public const string ModelDownloadSize = "~1.5 GB";

    // ONE budget for everything between "the user clicked stop" and "text appears", not just the inference.
    //
    // This used to bound the inference alone, which is only the last of three waits — and on a machine under heavy
    // load it is not the one that stalls. The other two had no limit at all:
    //
    //   * finalising the WAV, which waits on NAudio's RecordingStopped callback. That callback runs on NAudio's own
    //     thread, and a saturated box can delay it indefinitely.
    //   * loading the model: a 1.46 GiB read into RAM (and, on the GPU build, a pipeline compile that runs a whole
    //     warm-up inference). Normally this is already done — it starts when the mic goes live — but if the machine
    //     is busy enough that the user stops speaking before the warm-up finishes, the transcription waits on it.
    //
    // Either one hanging pinned the UI on "transcribing…" for ever, because the await never returned and the
    // finally that resets State never ran. Everything is now measured against this single deadline.
    //
    // 180 s, up from the 60 s that fitted base.en. The GPU path did not need it — a 10.5 s clip measured 1.10 s of
    // inference on top of a 2.6 s load. The CPU FALLBACK did: the same clip takes 16.0 s there, ~9.7x what base.en
    // took, and that is on an IDLE machine. Scaled up, a minute-long dictation on a GPU-less box is around a minute
    // of inference before any contention at all, which walks straight through a 60 s deadline while working
    // perfectly. A budget that expires on a transcription that would have succeeded is the worse failure: it throws
    // away what the user just said, and no retry can get it back.
    private static readonly TimeSpan TranscribeTimeout = TimeSpan.FromSeconds(180);

    /// <summary>How long to wait for NAudio to hand back the finished WAV before salvaging the buffer by hand.
    /// Short, because it is only a header flush — anything longer means the callback is not coming.</summary>
    private static readonly TimeSpan WavFinalizeTimeout = TimeSpan.FromSeconds(5);

    // Matters on the CPU fallback build; harmless on the GPU one. whisper.cpp's own default is min(4, cores), which
    // leaves most of the machine idle — and, worse, asks for a small share of a CPU that is already being fought
    // over. More threads means a bigger slice of a contended box: measured on a 6c/12t Ryzen under a saturating
    // 12-thread load, a 4.6 s clip took 2.5-3.0 s with 4 threads and 1.3 s with 12. Clamped at 16 so a very wide CPU
    // doesn't spend more on thread sync than it wins back.
    private static readonly int InferenceThreads = Math.Clamp(Environment.ProcessorCount, 4, 16);

    public SpeechState State { get; private set; } = SpeechState.Idle;
    public bool IsBusy => State is not SpeechState.Idle;
    public bool ModelReady => File.Exists(ModelPath);

    /// <summary>True when this transcription will be done by Groq rather than by the local model. Read once at the
    /// top of the transcription and again by the composer's hint, so a mid-recording settings change cannot land
    /// half in one backend and half in the other.</summary>
    public static bool UsesGroq => GroqSpeechService.Instance.Enabled;

    /// <summary>True when the next dictation needs no one-time model download — either the local model is already on
    /// disk, or Groq is doing the transcribing and the local model is never touched at all. The composer uses this
    /// to decide whether to warn about the <see cref="ModelDownloadSize"/> fetch.</summary>
    public bool ReadyWithoutDownload => UsesGroq || ModelReady;

    // ===================================== THE ONE-TIME DOWNLOAD, WATCHED =====================================
    // At 142 MB a silent download was survivable. At 1.46 GiB it is not: a progress-free "downloading…" that sits
    // there for ten minutes is indistinguishable from a hang, and the fixed 30-minute budget the UI policed it with
    // was itself too short for a modest connection (1.46 GiB needs ~1.4 Mbit/s just to make that deadline).
    //
    // Both are answered by the same two counters, written by DownloadModelAsync as the bytes land:
    //   * DownloadedBytes/DownloadTotalBytes give the composer a real percentage every second.
    //   * DownloadStalledFor lets the watchdog police a download that has STOPPED MOVING rather than one that is
    //     merely slow — so a 40-minute download on a bad hotel connection finishes, and a dead socket is still
    //     caught in minutes instead of half an hour.
    // Written from the download task and read from the UI thread: long/volatile, no lock. Neither is ever consulted
    // outside SpeechState.Downloading, so a stale pair from a finished download cannot be misread as a live one.
    private long _downloadedBytes;
    private long _downloadTotalBytes;
    private long _lastProgressTicks;      // DateTime.UtcNow.Ticks at the last byte received

    /// <summary>Bytes of the model fetched so far by the download in progress.</summary>
    public long DownloadedBytes => Interlocked.Read(ref _downloadedBytes);

    /// <summary>Total bytes the download in progress expects, or 0 before the server has said.</summary>
    public long DownloadTotalBytes => Interlocked.Read(ref _downloadTotalBytes);

    /// <summary>How long the download in progress has gone without receiving a byte. <see cref="TimeSpan.Zero"/>
    /// when no download has started.</summary>
    public TimeSpan DownloadStalledFor
    {
        get
        {
            var at = Interlocked.Read(ref _lastProgressTicks);
            return at == 0 ? TimeSpan.Zero : DateTime.UtcNow - new DateTime(at, DateTimeKind.Utc);
        }
    }

    /// <summary>The download's progress as a sentence — "412 MB of 1.5 GB (27%)" — or null before enough is known
    /// to say anything true. The composer appends this to its own hint, so it stays one line.</summary>
    public string? DownloadProgressText
    {
        get
        {
            long done = DownloadedBytes, total = DownloadTotalBytes;
            if (done <= 0) return null;
            static string Mb(long b) => b >= 1_000_000_000 ? $"{b / 1e9:0.0} GB" : $"{b / 1e6:0} MB";
            return total > 0 ? $"{Mb(done)} of {Mb(total)} ({done * 100 / total}%)" : Mb(done);
        }
    }

    // ================================ CAPTURE OWNERSHIP RULE — read before editing ================================
    // There is exactly ONE authoritative notion of "which capture is current": the monotonically increasing token
    // handed out by StartRecording (== _captureGeneration). Every mutation of State, of _current, and of the CALLER's
    // own mic bookkeeping must be a no-op unless the mutator still holds the current token (Owns(token)).
    //
    //   * StartRecording issues a NEW token; ForceReset/CancelRecording BURN the current one (nobody owns it after).
    //   * StopAndTranscribeAsync takes the token as an argument. It performs the whole Recording -> Transcribing
    //     transition SYNCHRONOUSLY, before its first await: it flips State and detaches _current on entry. A second
    //     concurrent call therefore fails the entry guard and can never transcribe/insert the same audio twice.
    //   * All per-capture objects (buffer/writer/TCS/handler) live on one immutable Capture instance and the mic's
    //     DataAvailable handler closes over THAT instance — never over fields — so a late callback from an abandoned
    //     capture can never write into, or complete, a newer one. The DEVICE is the one shared thing: it is armed
    //     ahead of the click and outlives any single capture, which is why StopCapture detaches the handler rather
    //     than disposing it.
    //   * A disowned continuation must touch NOTHING: not State, not the caller's visuals, not the caller's ownership.
    // MainWindow.ToggleMic mirrors this with _micToken; keep the two rules identical.
    // ============================================================================================================

    private sealed class Capture
    {
        public required int Gen;
        public required MicCapture Mic;
        public required MemoryStream Buffer;
        public required WaveFileWriter Writer;
        public required TaskCompletionSource<byte[]> Tcs;
        public Action<byte[], int>? OnData;
    }

    private Capture? _current;
    private int _captureGeneration;          // 0 is never issued, so token 0 always means "owns nothing"

    // ===================================== ARMING THE MICROPHONE =====================================
    // waveInOpen measured 38-184 ms depending on how busy the machine is, and it used to be paid on the click
    // itself — time the user spends already talking. It is now paid when the user PRESSES the mic button, which
    // is 80-150 ms before the click completes, so the device is normally open by the time they commit.
    //
    // The device is closed again as soon as the dictation ends, and an arm that never turns into a recording is
    // dropped by ArmWindow. Opening is not recording — nothing is captured until MicCapture.Start — but an
    // indefinitely open capture handle is not something a settings pane with a Privacy section should leave
    // lying around.
    private static readonly TimeSpan ArmWindow = TimeSpan.FromSeconds(10);
    private MicCapture? _mic;
    private System.Threading.Timer? _unarm;
    private readonly object _micGate = new();

    private MicCapture Mic()
    {
        lock (_micGate) return _mic ??= new MicCapture();
    }

    /// <summary>Open the microphone before it is needed, so the click that starts dictation does not wait on
    /// waveInOpen. Best effort: a device that will not open reports its error from <see cref="StartRecording"/>
    /// exactly as it did before.</summary>
    public void ArmMic()
    {
        if (IsBusy) return;
        try
        {
            var mic = Mic();
            lock (_micGate)
            {
                _unarm ??= new System.Threading.Timer(_ => ReleaseMicIfIdle(), null, Timeout.Infinite, Timeout.Infinite);
                _unarm.Change(ArmWindow, Timeout.InfiniteTimeSpan);
            }
            mic.Arm();
        }
        catch { /* arming is only ever an optimisation */ }
    }

    /// <summary>Close the device once it is neither armed nor recording. Called when a dictation finishes and by
    /// the arm timer, so a press that never became a click cannot leave the mic open.</summary>
    private void ReleaseMicIfIdle()
    {
        MicCapture? mic;
        lock (_micGate)
        {
            if (IsBusy || _current is not null) return;
            mic = _mic;
            _mic = null;
            _unarm?.Change(Timeout.Infinite, Timeout.Infinite);
        }
        try { mic?.Dispose(); } catch { /* closing a device that already went away */ }
    }

    // ================================ WHY DICTATION USED TO TAKE HALF A MINUTE ==================================
    // Three costs used to sit between "user stops talking" and "text appears", all of them avoidable. Timings below
    // are a 4.6 s clip on a 6c/12t Ryzen 3600 + GTX 1660 SUPER, "busy" = a saturating 12-thread load:
    //
    //   1. RUNNING ON THE CPU AT ALL. Inference now runs on the GPU (Vulkan) rather than the CPU, worth ~6-7x on
    //      its own and for the same transcript: 1.33 s -> 0.19 s idle, and 1.4 s -> 0.22 s with all 12 hardware
    //      threads saturated by other processes — which is this app's normal condition, a wall of agents and a
    //      build. Backend choice, and what happens on a machine with no usable GPU, live in WhisperNativeRuntime.
    //
    //   2. LOADING THE MODEL (2.3-2.7 s warm, more on a cold cache). WhisperFactory.FromPath pulls the ~1.46 GiB
    //      model off disk and into RAM
    //      (and, on the GPU build, uploads it to VRAM — which is why the load itself got slightly slower while
    //      inference got 6x faster). It ran on the transcription path, so the user paid for it while staring at
    //      "transcribing…". It now starts the moment the mic goes live and overlaps with the user actually speaking
    //      — by the time they stop, it is almost always already done. Still built exactly once per process, shared.
    //
    //   3. LOSING THE CPU RACE. whisper.cpp runs inference on native worker threads that inherit the PROCESS
    //      priority class, so on a saturated machine they compete on equal footing with everything else and get
    //      starved: against a dozen busy Normal-priority processes the same 4.6 s clip takes 14-28 s at Normal and
    //      1.4 s at AboveNormal. Managed thread priorities can't help (ggml creates the threads itself), so the
    //      process class is the only lever that reaches them. It earns its keep on the CPU fallback; the GPU build
    //      barely notices, though the boost also covers the model load and the pipeline compile below.
    //      Worth knowing before trusting a benchmark: the boost lifts EVERY thread in this process, so it buys
    //      nothing against load generated inside VibeCode itself — only against the other processes on the box.
    //      See CpuBoost.
    // ============================================================================================================

    private Task<WhisperFactory>? _factoryLoad;   // the one shared model load (expensive), started as early as possible
    private readonly object _factoryGate = new();

    /// <summary>The shared <see cref="WhisperFactory"/>, loading it on first call and leaving the GPU ready to run.
    /// Concurrent callers share one load; a FAILED load is not cached, so a later attempt (a re-download, a freed-up
    /// machine) gets a real retry instead of a permanently broken mic.</summary>
    private Task<WhisperFactory> LoadFactoryAsync()
    {
        lock (_factoryGate)
        {
            if (_factoryLoad is { IsCompleted: true, IsCompletedSuccessfully: false }) _factoryLoad = null;
            return _factoryLoad ??= Task.Run(async () =>
            {
                WhisperNativeRuntime.Configure();
                // Covers the upgrade that does NOT go through a download — a machine that already had the current
                // model would otherwise keep the superseded one for ever, since nothing else ever looks.
                PruneSupersededModels();
                using var boost = CpuBoost.Acquire();     // reading 1.46 GiB + allocating loses the same race inference does
                var factory = WhisperFactory.FromPath(ModelPath);
                await CompileGpuPipelinesAsync(factory);  // part of "ready", so the real transcription never waits on it
                return factory;
            });
        }
    }

    /// <summary>Runs one throwaway inference over a second of silence, purely to make the GPU driver compile
    /// whisper's compute pipelines.
    ///
    /// The driver caches those compiled pipelines on disk PER EXECUTABLE, so a freshly installed VibeCode pays for
    /// them once — and it is not small: measured on this box, the first inference in a never-before-run exe took
    /// 6.3 s, and every one after it 0.18 s. Without this, that 6.3 s lands squarely on the user's first dictation
    /// after every update, which is exactly the "why is this so slow" moment we are fixing. Compiling on a silent
    /// clip produces the same pipelines a real utterance needs (verified: the real clip that follows runs at the
    /// full 0.18 s), and this sits inside the warm-up, so it overlaps with the user actually speaking.
    ///
    /// Skipped on the CPU build, which has no pipelines to compile and would just burn a second of a busy CPU.</summary>
    private static async Task CompileGpuPipelinesAsync(WhisperFactory factory)
    {
        if (!WhisperNativeRuntime.UsesGpu) return;
        try
        {
            using var processor = factory.CreateBuilder().WithLanguage("en").WithThreads(InferenceThreads).Build();
            using var silence = new MemoryStream(SilenceWav(seconds: 1));
            await foreach (var _ in processor.ProcessAsync(silence)) { }
        }
        catch { /* warm-up is an optimisation, never a failure mode */ }
    }

    /// <summary>A valid WAV of pure silence in the same 16 kHz mono 16-bit format <see cref="StartRecording"/>
    /// captures, so the warm-up exercises the real decode path rather than a special case.</summary>
    private static byte[] SilenceWav(double seconds)
    {
        var data = (int)(16000 * seconds) * 2;
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + data); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(16000); w.Write(32000); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(data);
        w.Write(new byte[data]);
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>Start getting whisper ready in the background — model into RAM, GPU pipelines compiled — so that by
    /// the time the user stops speaking there is nothing left to do but the inference itself. Best effort: no-op
    /// before the one-time download has happened (that path loads it itself), and a failure here is swallowed so the
    /// real transcription can report it properly.</summary>
    private void BeginWarmUp()
    {
        // Nothing to warm when Groq is doing the transcribing: the local model is never loaded, so paying for a
        // 1.46 GiB read and a GPU pipeline compile on every mic click would be pure waste.
        if (UsesGroq) return;
        if (!ModelReady || _factoryLoad is not null) return;
        try { _ = LoadFactoryAsync().ContinueWith(t => _ = t.Exception, TaskScheduler.Default); }
        catch { /* warm-up is an optimisation, never a failure mode */ }
    }

    /// <summary>Temporarily raises the process priority so whisper.cpp's native worker threads stop being starved by
    /// whatever else is loading the machine. AboveNormal rather than High: enough to beat ordinary work, not enough to
    /// make the rest of the system stutter. Refcounted (warm-up and transcription can overlap) and always restored to
    /// whatever the class was before — a failure to change priority is ignored, since it only costs speed.</summary>
    private sealed class CpuBoost : IDisposable
    {
        private static readonly object Gate = new();
        private static int _depth;
        private static ProcessPriorityClass _saved = ProcessPriorityClass.Normal;
        private bool _released;

        public static IDisposable Acquire()
        {
            lock (Gate)
            {
                if (_depth++ == 0)
                {
                    try
                    {
                        var me = Process.GetCurrentProcess();
                        _saved = me.PriorityClass;
                        // Never lower an app the user (or a launcher) has already put above us.
                        if (_saved is ProcessPriorityClass.Normal or ProcessPriorityClass.BelowNormal or ProcessPriorityClass.Idle)
                            me.PriorityClass = ProcessPriorityClass.AboveNormal;
                    }
                    catch { /* denied / process exiting — dictation still works, just slower */ }
                }
            }
            return new CpuBoost();
        }

        public void Dispose()
        {
            lock (Gate)
            {
                if (_released) return;                    // double-dispose must not unbalance the refcount
                _released = true;
                if (--_depth > 0) return;
                try { Process.GetCurrentProcess().PriorityClass = _saved; } catch { /* see above */ }
            }
        }
    }

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
            if (!MicCapture.AnyDevice) { error = "No microphone found."; return false; }
            var gen = ++_captureGeneration;      // this capture now owns State; everyone older is disowned
            var buffer = new MemoryStream();
            var mic = Mic();
            var writer = new WaveFileWriter(buffer, new WaveFormat(MicCapture.SampleRate, MicCapture.Bits, MicCapture.Channels));
            var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            cap = new Capture { Gen = gen, Mic = mic, Buffer = buffer, Writer = writer, Tcs = tcs };

            // The handler closes over THIS capture's locals — never over fields — so a callback still in flight when
            // a newer capture starts writes into its OWN wav. Unsubscribed by StopCapture, which returns only once
            // the capture thread is done, so there is no window where a stale handler can fire.
            void OnData(byte[] buf, int count) { try { writer.Write(buf, 0, count); } catch { /* stopped */ } }
            cap.OnData = OnData;
            mic.DataAvailable += OnData;

            _current = cap;
            mic.Start();
            State = SpeechState.Recording;
            token = gen;
            // Mute the competition: dictating over music transcribes the music too. Fire-and-forget so a slow Spotify
            // round trip can't delay the mic going live; it self-serializes and no-ops when the extension is off.
            // Every path that ends this capture (transcribe/silence/failure/cancel/force-reset) undoes it.
            _ = SpotifyService.Instance.DuckAsync();
            // Get whisper ready WHILE the user talks, not after. This is pure overlap: by the time they click stop the
            // model is normally already in RAM and the GPU pipelines are built, so "transcribing…" starts on the
            // inference itself instead of on a 1.46 GiB disk read and a shader compile. It matters far more with
            // medium.en than it did with base.en — the read it hides is ten times the size.
            BeginWarmUp();
            return true;
        }
        catch (Exception ex)
        {
            error = "Microphone error: " + ex.Message;
            if (cap is not null)
            {
                StopCapture(cap);                // Start() may have got as far as queueing buffers
                DisposeCapture(cap);
                if (ReferenceEquals(_current, cap)) _current = null;
            }
            State = SpeechState.Idle;            // nothing is live, so State must say so
            ReleaseMicIfIdle();                  // a device that cannot record must not stay open
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
        StopCapture(cap);                        // synchronous: returns with the WAV finished and cap.Tcs completed
        // ---- end synchronous section ----

        // The whole pipeline runs against one clock, started the moment the user asked for their text.
        var budget = Stopwatch.StartNew();
        TimeSpan Remaining() => TranscribeTimeout - budget.Elapsed;

        byte[] wav;
        try { wav = await cap.Tcs.Task.WaitAsync(WavFinalizeTimeout); }
        catch (TimeoutException) { wav = SalvageWav(cap); }   // the stop callback never came back — keep the audio
        finally { DisposeCapture(cap); }         // always OUR capture object — never a newer one's

        if (wav.Length <= 44)                    // 44-byte WAV header only = no audio captured
        {
            FinishIfStillOwned();
            return "";
        }

        try
        {
            // The cloud backend, when the user has switched it on. Everything above this line — the capture, the
            // ownership token, the WAV, the Spotify duck — is identical either way; only who turns bytes into text
            // changes. Read ONCE into a local so a settings change mid-call cannot split this transcription across
            // both backends. It shares the same budget as the local path, so a hung upload cannot outlast it.
            if (UsesGroq) return await TranscribeWithGroqAsync(wav, Remaining());

            if (!ModelReady)
            {
                SetIfStillOwned(SpeechState.Downloading);
                await DownloadModelAsync();
            }
            SetIfStillOwned(SpeechState.Transcribing);

            // Normally instant — the warm-up started when the mic went live — but on a loaded machine it can still
            // be running, so it is bounded like everything else. The load is NOT cancelled on timeout: it is shared
            // and cached, so letting it finish in the background means the next attempt is the warm one.
            Task<WhisperFactory> factoryLoad = LoadFactoryAsync();
            if (!factoryLoad.IsCompleted)
            {
                var wait = Remaining();
                if (wait <= TimeSpan.Zero) throw Stalled("preparing the speech model");
                try { await factoryLoad.WaitAsync(wait); }
                catch (TimeoutException)
                {
                    _ = factoryLoad.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);  // never unobserved
                    throw Stalled("preparing the speech model");
                }
            }
            var factory = await factoryLoad;

            // Bound the inference by TranscribeTimeout so a stalled native call (or an over-long clip) can't leave the
            // UI stuck on "transcribing…" forever. Cancel the pipeline cooperatively AND stop awaiting on the wall
            // clock, so this returns within the budget even if native processing ignores cancellation. On timeout we
            // throw; the finally below flips State back to Idle and the caller shows the failure.
            var timeoutCts = new CancellationTokenSource();
            // Held for the whole inference — this is what keeps a busy machine from stretching a two-second
            // transcription into a two-minute one. Released on every exit path, including the timeout throw below.
            using var boost = CpuBoost.Acquire();
            var work = Task.Run(async () =>
            {
                using var processor = factory.CreateBuilder()
                                             .WithLanguage("en")
                                             .WithThreads(InferenceThreads)
                                             .Build();
                using var ms = new MemoryStream(wav);
                var sb = new StringBuilder();
                await foreach (var seg in processor.ProcessAsync(ms).WithCancellation(timeoutCts.Token))
                    if (!IsNonSpeechMarker(seg.Text)) sb.Append(seg.Text);
                return sb.ToString().Trim();
            }, timeoutCts.Token);

            // Whatever is left of the one budget, so a slow model load cannot push the total past it.
            if (await Task.WhenAny(work, Task.Delay(Positive(Remaining()))) != work && !work.IsCompletedSuccessfully)
            {
                timeoutCts.Cancel();                                          // best-effort cooperative stop
                _ = work.ContinueWith(t => { _ = t.Exception; timeoutCts.Dispose(); }, // observe + dispose on its own time
                                      TaskScheduler.Default);
                throw Stalled("transcribing");
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
            ReleaseMicIfIdle();                  // the dictation is over; don't leave the device open
            _ = SpotifyService.Instance.UnduckAsync();
        }
    }

    /// <summary>Hand the finished WAV to Groq instead of the local model, bounded by whatever is left of the one
    /// transcription budget — so a stalled upload can no more wedge the mic on "transcribing…" than a stalled
    /// native inference can. The timeout message is its own rather than <see cref="Stalled"/>'s: this one waits on
    /// a network, not on a contended CPU, and telling the user their machine is busy would send them looking in
    /// the wrong place.</summary>
    private static async Task<string> TranscribeWithGroqAsync(byte[] wav, TimeSpan budget)
    {
        using var cts = new CancellationTokenSource(Positive(budget));
        string text;
        try { text = await GroqSpeechService.Instance.TranscribeAsync(wav, cts.Token); }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Groq didn't answer within {TranscribeTimeout.TotalSeconds:0}s — the network or the API is slow right now. "
                + "The mic is free again; try once more, or record a shorter clip.");
        }
        // Whisper annotates non-speech the same way whichever machine runs it ("[BLANK_AUDIO]"), but here it arrives
        // as the WHOLE response rather than as one segment among many — so it needs the same guard, or a moment of
        // silence types a literal marker into the composer instead of reporting that nothing was heard.
        return IsNonSpeechMarker(text) ? "" : text.Trim();
    }

    /// <summary>The one message for "the budget ran out", naming the stage that ate it so a report says something
    /// useful instead of just "it failed". Every stage times out into the same shape.</summary>
    private static TimeoutException Stalled(string stage) => new(
        $"Speech-to-text gave up after {TranscribeTimeout.TotalSeconds:0}s while {stage} — the machine is likely busy. "
        + "The mic is free again; try once more, or record a shorter clip.");

    private static TimeSpan Positive(TimeSpan value) => value > TimeSpan.Zero ? value : TimeSpan.Zero;

    /// <summary>The audio captured so far, with its RIFF header flushed by hand. Used when NAudio's
    /// RecordingStopped callback does not come back in time: on a loaded machine that callback can be delayed
    /// indefinitely, but the samples already written are perfectly good — so this recovers what the user said
    /// instead of throwing the whole recording away.</summary>
    private static byte[] SalvageWav(Capture cap)
    {
        try { cap.Writer.Dispose(); } catch { /* may already be disposed by a late callback */ }
        try { return cap.Buffer.ToArray(); } catch { return Array.Empty<byte>(); }   // valid after dispose
    }

    /// <summary>True for whisper's non-speech annotations — "[BLANK_AUDIO]", "[ Silence ]", "(upbeat music)" and the
    /// like. They are DESCRIPTIONS of the audio, not things the user said, and whisper emits them as segments of their
    /// own; without this, recording a moment of quiet typed the literal text "[BLANK_AUDIO]" into the composer instead
    /// of reporting that nothing was heard. Only whole-segment annotations are dropped, so a genuine aside like
    /// "call foo (the new one)" survives untouched.</summary>
    private static bool IsNonSpeechMarker(string? segment)
    {
        var s = segment?.Trim();
        if (string.IsNullOrEmpty(s)) return true;
        return (s[0] == '[' && s[^1] == ']') || (s[0] == '(' && s[^1] == ')');
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
            StopCapture(cap);                    // the device is stopped and detached, so two captures can never overlap
            DisposeCapture(cap);
        }
        State = SpeechState.Idle;
        ReleaseMicIfIdle();
        // Unconditional: this call just burned the token, so the transcription that would otherwise unduck is now
        // disowned and will deliberately do nothing. If we skip it here, paused music never comes back.
        _ = SpotifyService.Instance.UnduckAsync();
    }

    /// <summary>Stop the device and finish this capture's WAV. Synchronous by design: MicCapture.Stop returns only
    /// once its capture thread is done, so by the time this returns no further callback can write into the writer
    /// and the RIFF header can be flushed on the spot. That is what makes the old "wait for RecordingStopped, and
    /// salvage the buffer by hand if it never comes" dance unnecessary — the timeout below is now belt and braces
    /// rather than a routine escape.
    ///
    /// Safe to call twice: Stop is idempotent, the handler is detached only once, and TrySetResult ignores the
    /// second attempt.</summary>
    private static void StopCapture(Capture cap)
    {
        try { cap.Mic.Stop(); } catch { /* a device that already went away is still stopped */ }
        var handler = cap.OnData;
        cap.OnData = null;
        if (handler is not null) { try { cap.Mic.DataAvailable -= handler; } catch { /* ignore */ } }
        try { cap.Writer.Dispose(); } catch { /* header finalize best-effort */ }   // flushes the RIFF header
        cap.Tcs.TrySetResult(cap.Buffer.ToArray());                                 // valid after writer dispose
    }

    /// <summary>Release this capture's own objects. The DEVICE is deliberately not disposed here: it is shared
    /// with the arming path and outlives any one capture — <see cref="ReleaseMicIfIdle"/> closes it.</summary>
    private static void DisposeCapture(Capture cap)
    {
        var handler = cap.OnData;
        cap.OnData = null;
        if (handler is not null) { try { cap.Mic.DataAvailable -= handler; } catch { /* ignore */ } }
        try { cap.Writer.Dispose(); } catch { /* already disposed by StopCapture */ }
        try { cap.Buffer.Dispose(); } catch { /* ignore */ }
    }

    // Download the model to a temp file then move it into place, so a cancelled/failed download can't leave a
    // corrupt model that would fail to load forever. The temp name is UNIQUE PER ATTEMPT: a disowned-but-still-running
    // first attempt and a second attempt would otherwise write the same fixed path and die on a sharing violation.
    //
    // No HttpClient.Timeout: it is a deadline for the WHOLE response, body included, and any value that does not cut
    // off a legitimately slow 1.46 GiB download is far too long to catch a dead one. The stall clock the counters
    // below feed does that job properly, and it is the UI's watchdog that acts on it.
    private async Task DownloadModelAsync()
    {
        Directory.CreateDirectory(ModelDir);
        var tmp = ModelPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Interlocked.Exchange(ref _downloadedBytes, 0);
        Interlocked.Exchange(ref _downloadTotalBytes, 0);
        Interlocked.Exchange(ref _lastProgressTicks, DateTime.UtcNow.Ticks);
        try
        {
            long written;
            using (var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan })
            using (var resp = await http.GetAsync(ModelUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                // Prefer what this response actually promises; fall back to the constant so the percentage still
                // reads sensibly on a server (or proxy) that sends no length.
                Interlocked.Exchange(ref _downloadTotalBytes, resp.Content.Headers.ContentLength ?? ModelBytes);
                await using var src = await resp.Content.ReadAsStreamAsync();
                await using var dst = File.Create(tmp);
                written = await CopyWithProgressAsync(src, dst);
            }

            // A truncated body arrives as a clean end-of-stream, not as an error. Without this the half-model is
            // moved into place and every future dictation fails on a load that can never succeed — and, because
            // ModelReady only asks whether the file exists, it would never be re-fetched either.
            var expected = DownloadTotalBytes;
            if (expected > 0 && written != expected)
                throw new IOException(
                    $"The speech model download ended early ({written / 1_000_000} MB of {expected / 1_000_000} MB). "
                    + "Check the connection and try again.");

            // Another attempt may have finished first — its file is just as good, so keep it rather than racing a delete.
            if (!File.Exists(ModelPath)) File.Move(tmp, ModelPath);
            PruneSupersededModels();
        }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort */ } }
    }

    /// <summary>Stream.CopyToAsync with the counters kept up to date, so the UI can show a percentage and tell a slow
    /// download apart from a dead one. Returns the bytes written. The buffer is 1 MB rather than the framework's
    /// 81 KB: at this size the syscall count is worth caring about, and it updates the counters ~1500 times over the
    /// whole download — often enough for a per-second readout, rarely enough to cost nothing.</summary>
    private async Task<long> CopyWithProgressAsync(Stream src, Stream dst)
    {
        var buffer = new byte[1 << 20];
        long total = 0;
        int read;
        while ((read = await src.ReadAsync(buffer)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read));
            total += read;
            Interlocked.Exchange(ref _downloadedBytes, total);
            Interlocked.Exchange(ref _lastProgressTicks, DateTime.UtcNow.Ticks);
        }
        return total;
    }

    /// <summary>Delete model files an older build downloaded and this one no longer loads. Called only once the
    /// current model is on disk, so a machine is never left with no model at all; every one of them is a public
    /// file this app re-downloads on demand, and nothing but this app writes to <see cref="ModelDir"/>. Best
    /// effort — a locked file (an old instance still holding it) simply stays until the next successful download.</summary>
    private static void PruneSupersededModels()
    {
        if (!File.Exists(ModelPath)) return;
        foreach (var name in SupersededModelFiles)
        {
            try
            {
                var path = Path.Combine(ModelDir, name);
                if (File.Exists(path)) File.Delete(path);
            }
            catch { /* still in use, or not ours to remove — it only costs disk */ }
        }
    }
}
