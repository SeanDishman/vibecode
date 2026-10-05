using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using KokoroSharp.Core;
using KokoroSharp.Processing;
using Microsoft.ML.OnnxRuntime;
using NAudio.Wave;

namespace VibeCode.Services;

public sealed record JarvisVoiceChoice(string Id, string Display)
{
    public string Name => Display;
}

/// <summary>Free local neural British stock voices, with a named installed Windows voice fallback.</summary>
public sealed class JarvisSpeechService : IDisposable
{
    public static JarvisSpeechService Instance { get; } = new();
    public const string DefaultVoiceId = "bm_george";
    public const string ModelUrl = "https://github.com/Lyrcaxis/KokoroSharpBinaries/releases/download/v2.0.0/kokoro-fp16.onnx";
    public const long ModelSize = 163_636_560;
    public const string ModelSha256 = "027a25b14aef7d3ae57fd09301ebefbec868e79d55213d07e4f3af442f5ba352";
    // A reply's voice is checked against this list, never GetVoices(): enumerating every installed Windows voice over
    // COM cost ~140 ms on each reply.
    private static readonly JarvisVoiceChoice[] NeuralVoices =
    [
        new("bm_george", "George · British male · Kokoro"),
        new("bm_daniel", "Daniel · British male · Kokoro"),
        new("bm_lewis", "Lewis · British male · Kokoro"),
        new("bf_emma", "Emma · British female · Kokoro"),
        new("bf_alice", "Alice · British female · Kokoro"),
        new("bf_isabella", "Isabella · British female · Kokoro"),
        new("bf_lily", "Lily · British female · Kokoro"),
    ];
    private const int FirstChunkLimit = 100, ChunkLimit = 220;
    private static readonly byte[] ChunkPause = new byte[4800]; // a short natural pause between segments
    private static readonly string[] ClauseMarks = [", ", "; ", ": ", " — ", " – "];
    // A sentence ends at . ! or ? (and any closing quote or bracket) before a space and then a capital, a digit or an
    // opening quote or bracket - so "e.g. this" and "v1.2" stay whole.
    private static readonly Regex SentenceBreak =
        new(@"(?<=[.!?][""')\]’”]*)\s+(?=[A-Z0-9""'(‘“])", RegexOptions.Compiled);
    private readonly string _cacheDirectory;
    private readonly SemaphoreSlim _synthesisGate = new(1, 1);
    private readonly object _stopGate = new();
    private CancellationTokenSource? _speakingStop;
    private KokoroModel? _model;
    private KokoroVoice? _voice;
    private string? _voiceId;
    private bool _phonemizerStarted;
    private int _warming;
    private bool _disposed;
    private bool _modelVerified;
    public string VoiceDescription { get; private set; } = "Kokoro George · British English · licensed stock voice";
    public event Action? VoiceChanged;
    public string ModelPath => Path.Combine(_cacheDirectory, "kokoro-fp16.onnx");

    public JarvisSpeechService(string? cacheDirectory = null)
    {
        _cacheDirectory = cacheDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VibeCode", "Jarvis", "Speech");
        SpeechService.Instance.StateChanged += OnCaptureStateChanged;
    }

    public static IReadOnlyList<JarvisVoiceChoice> GetVoices()
    {
        var choices = new List<JarvisVoiceChoice>(NeuralVoices);
        try
        {
            dynamic? sapi = null;
            dynamic? tokens = null;
            try
            {
                sapi = Activator.CreateInstance(Type.GetTypeFromProgID("SAPI.SpVoice")!);
                tokens = sapi!.GetVoices();
                for (var i = 0; i < (int)tokens!.Count; i++)
                {
                    dynamic token = tokens.Item(i);
                    try { choices.Add(new("sapi:" + (string)token.Id, (string)token.GetDescription() + " · Windows")); }
                    finally { Marshal.ReleaseComObject(token); }
                }
            }
            finally
            {
                if (tokens is not null) Marshal.ReleaseComObject(tokens);
                if (sapi is not null) Marshal.ReleaseComObject(sapi);
            }
        }
        catch { /* Neural voices remain usable on a machine without SAPI. */ }
        return choices;
    }

    private static bool IsNeuralVoice(string? voiceId) => NeuralVoices.Any(voice => voice.Id == voiceId);

    /// <summary>Get the British voice ready in the background - the model's integrity check and load, the voice and
    /// the phonemizer's start-up, ~3.5 s on a 6-core CPU that the first reply after launch used to sit through. Best
    /// effort, like the whisper warm-up: a no-op until a first reply has downloaded the model, and a failure here is
    /// swallowed so the reply itself can report it.</summary>
    public void BeginWarmUp(string? voiceId)
    {
        voiceId ??= DefaultVoiceId;
        if (_disposed || !IsNeuralVoice(voiceId) || !File.Exists(ModelPath)) return;
        if (_model is not null && _voiceId == voiceId && _phonemizerStarted) return;
        if (Interlocked.Exchange(ref _warming, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await _synthesisGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed) return;
                await EnsureVoiceAsync(voiceId, CancellationToken.None, progress: null).ConfigureAwait(false);
                if (_phonemizerStarted) return;
                Tokenizer.Tokenize("Ready.", "en-gb", preprocess: true);
                _phonemizerStarted = true;
            }
            finally
            {
                _synthesisGate.Release();
                Volatile.Write(ref _warming, 0);
            }
        }).ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    public Task SpeakAsync(string text, CancellationToken cancellationToken = default) =>
        SpeakAsync(text, AppSettings.Current.JarvisVoiceId, AppSettings.Current.JarvisSpeechRate,
            AppSettings.Current.JarvisSpeechVolume, cancellationToken);

    public async Task SpeakAsync(string text, string? voiceId, double rate, int volume,
        CancellationToken cancellationToken = default, Action<string>? progress = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Stop();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_stopGate) _speakingStop = stop;
        var spoken = PrepareSpeechText(text);
        try
        {
            if (spoken.Length == 0) return;
            if (SpeechService.Instance.IsBusy) throw new InvalidOperationException("Finish dictation before playing a voice reply.");
            if (voiceId?.StartsWith("sapi:", StringComparison.Ordinal) == true)
            {
                await SpeakWindowsAsync(spoken, voiceId, rate, volume, stop.Token, progress);
                return;
            }
            string? unspoken;
            try
            {
                unspoken = await SpeakNeuralAsync(spoken, voiceId ?? DefaultVoiceId, rate, volume, stop.Token, progress);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                stop.Token.ThrowIfCancellationRequested();
                if (SpeechService.Instance.IsBusy) throw;
                unspoken = spoken;
            }
            if (unspoken is null) return;
            stop.Token.ThrowIfCancellationRequested();
            progress?.Invoke("British voice unavailable; using the named Windows fallback.");
            await SpeakWindowsAsync(unspoken, null, rate, volume, stop.Token, progress, fallback: true);
        }
        finally
        {
            lock (_stopGate) if (ReferenceEquals(_speakingStop, stop)) _speakingStop = null;
        }
    }

    public Task<byte[]> SynthesizeWavAsync(string text, string voiceId = DefaultVoiceId, double rate = 1,
        CancellationToken cancellationToken = default, Action<string>? progress = null) =>
        Detach(SynthesizeWavCoreAsync(PrepareSpeechText(text), voiceId, rate, cancellationToken, progress), cancellationToken);

    private async Task<byte[]> SynthesizeWavCoreAsync(string text, string voiceId, double rate,
        CancellationToken cancellationToken, Action<string>? progress)
    {
        using var buffer = new MemoryStream();
        using (var wav = new WaveFileWriter(new NonDisposingStream(buffer), new WaveFormat(24_000, 16, 1)))
        {
            foreach (var chunk in SpeechChunks(text))
            {
                var pcm = await SynthesizeChunkAsync(chunk, voiceId, rate, cancellationToken, progress).ConfigureAwait(false);
                if (pcm.Length == 0) continue;
                wav.Write(pcm, 0, pcm.Length);
                wav.Write(ChunkPause, 0, ChunkPause.Length);
            }
        }
        SetVoiceDescription(NeuralDescription(voiceId));
        return buffer.ToArray();
    }

    /// <summary>
    /// Speak with the neural voice while the reply is still being synthesized. Only the opening sentence is waited
    /// for; the rest is synthesized behind it, ~3x faster than it plays on a 6-core CPU. Synthesizing the whole reply
    /// before playing any of it left a typical answer silent for ~5 s under "Preparing British voice reply".
    /// Returns null once everything has played, or the text still unspoken if synthesis failed partway - what was
    /// already synthesized plays out first, so the fallback voice only picks up from there.
    /// </summary>
    private async Task<string?> SpeakNeuralAsync(string text, string voiceId, double rate, int volume,
        CancellationToken cancellationToken, Action<string>? progress)
    {
        var chunks = SpeechChunks(text).ToArray();
        var queue = new BufferedWaveProvider(new WaveFormat(24_000, 16, 1))
        {
            // Room for the whole reply even at the slowest rate (~0.07 s a character at 1x), so synthesis never waits.
            BufferDuration = TimeSpan.FromSeconds(Math.Max(30, text.Length * 0.1 / ClampRate(rate))),
        };
        using var output = new WaveOutEvent { Volume = Math.Clamp(volume, 0, 100) / 100f };
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        output.PlaybackStopped += (_, e) => { if (e.Exception is not null) ended.TrySetException(e.Exception); else ended.TrySetResult(); };
        output.Init(queue);
        using var stop = cancellationToken.Register(() => { try { output.Stop(); } catch { } });
        var playing = false;
        string? unspoken = null;
        for (var i = 0; i < chunks.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ended.Task.IsFaulted) await ended.Task.ConfigureAwait(false);   // the audio device failed mid-reply
            byte[] pcm;
            try
            {
                pcm = await Detach(SynthesizeChunkAsync(chunks[i], voiceId, rate, cancellationToken, progress),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (playing && !cancellationToken.IsCancellationRequested)
            {
                unspoken = string.Join(' ', chunks[i..]);
                break;
            }
            if (SpeechService.Instance.IsBusy) throw new InvalidOperationException("Another composer started dictation; voice output was stopped.");
            if (pcm.Length == 0) continue;
            queue.AddSamples(pcm, 0, pcm.Length);
            queue.AddSamples(ChunkPause, 0, ChunkPause.Length);
            if (playing) continue;
            playing = true;
            SetVoiceDescription(NeuralDescription(voiceId));
            output.Play();
            progress?.Invoke("Speaking");
        }
        if (!playing) return unspoken;
        queue.ReadFully = false;   // nothing more is coming: play out what is queued, then stop
        await ended.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return unspoken;
    }

    /// <summary>One chunk as 16-bit 24 kHz PCM. The gate is held per chunk, so a cancelled reply lets the next one in
    /// after at most one chunk of inference.</summary>
    private async Task<byte[]> SynthesizeChunkAsync(string chunk, string voiceId, double rate,
        CancellationToken cancellationToken, Action<string>? progress)
    {
        await _synthesisGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var voice = await EnsureVoiceAsync(voiceId, cancellationToken, progress).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var tokens = await Task.Run(() => Tokenizer.Tokenize(chunk, "en-gb", preprocess: true)).ConfigureAwait(false);
            _phonemizerStarted = true;
            if (tokens.Length == 0) return [];
            if (tokens.Length > KokoroModel.maxTokens) throw new InvalidOperationException("A speech segment is too long.");
            var samples = await Task.Run(() => _model!.Infer(tokens, voice.Features, ClampRate(rate))).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var pcm = new byte[samples.Length * 2];
            for (var i = 0; i < samples.Length; i++)
            {
                var value = (short)(Math.Clamp(samples[i], -1f, 1f) * short.MaxValue);
                pcm[i * 2] = (byte)value;
                pcm[i * 2 + 1] = (byte)(value >> 8);
            }
            return pcm;
        }
        finally { _synthesisGate.Release(); }
    }

    /// <summary>The model and the requested stock voice, loaded once and kept for every later reply. Call inside the
    /// gate.</summary>
    private async Task<KokoroVoice> EnsureVoiceAsync(string voiceId, CancellationToken cancellationToken, Action<string>? progress)
    {
        if (!IsNeuralVoice(voiceId)) throw new InvalidOperationException("Choose an available British voice in Settings > Jarvis.");
        if (_model is null)
        {
            if (File.Exists(ModelPath)) progress?.Invoke("Loading the British voice…");
            await EnsureModelAsync(cancellationToken, progress).ConfigureAwait(false);
            _model = await Task.Run(() => new KokoroModel(ModelPath,
                new SessionOptions { InterOpNumThreads = 1, IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount, 2, 6) })).ConfigureAwait(false);
        }
        if (_voice is not null && _voiceId == voiceId) return _voice;
        cancellationToken.ThrowIfCancellationRequested();
        // Load only the requested stock embedding, from the trusted installed package assets.
        var voicePath = Path.Combine(_cacheDirectory, voiceId + ".npy");
        if (!File.Exists(voicePath))
        {
            using var embedding = typeof(JarvisSpeechService).Assembly.GetManifestResourceStream("VibeCode.JarvisVoices." + voiceId + ".npy")
                ?? throw new FileNotFoundException("The installed British voice embedding is unavailable.");
            var temporaryVoice = voicePath + "." + Guid.NewGuid().ToString("N") + ".part";
            try
            {
                await using (var target = new FileStream(temporaryVoice, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await embedding.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporaryVoice, voicePath, overwrite: false);
            }
            finally { if (File.Exists(temporaryVoice)) File.Delete(temporaryVoice); }
        }
        _voice = KokoroVoice.FromPath(voicePath);
        _voiceId = voiceId;
        return _voice;
    }

    // ONNX inference is bounded by the short chunks. A cancelled caller returns immediately; the native operation
    // keeps its model alive and gate held until it finishes, and its output is never played.
    private static Task<T> Detach<T>(Task<T> work, CancellationToken cancellationToken)
    {
        _ = work.ContinueWith(t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        return work.WaitAsync(cancellationToken);
    }

    private static float ClampRate(double rate) => (float)(double.IsFinite(rate) ? Math.Clamp(rate, 0.6, 1.5) : 1);
    private static string NeuralDescription(string voiceId) =>
        $"Kokoro {voiceId[3..]} · British English · licensed stock voice ({voiceId})";

    private async Task EnsureModelAsync(CancellationToken cancellationToken, Action<string>? progress)
    {
        Directory.CreateDirectory(_cacheDirectory);
        if (!_modelVerified && File.Exists(ModelPath))
        {
            await ValidateModelAsync(ModelPath, cancellationToken).ConfigureAwait(false);
            _modelVerified = true;
        }
        if (_modelVerified) return;
        var temporary = Path.Combine(_cacheDirectory, "kokoro-download-" + Guid.NewGuid().ToString("N") + ".part");
        try
        {
            progress?.Invoke("Downloading free British voice model · 156 MB · first use only");
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(8) };
            using var response = await client.GetAsync(ModelUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous))
            {
                var bytes = new byte[81920];
                long downloaded = 0;
                var reported = -1;
                int count;
                while ((count = await input.ReadAsync(bytes, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    downloaded += count;
                    if (downloaded > ModelSize) throw new IOException("The speech model download exceeded its expected size.");
                    await output.WriteAsync(bytes.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    var percent = (int)(downloaded * 100 / ModelSize);
                    if (percent != reported) { reported = percent; progress?.Invoke($"Downloading British voice · {percent}% of 156 MB"); }
                }
            }
            await ValidateModelAsync(temporary, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, ModelPath, overwrite: false);
            _modelVerified = true;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task ValidateModelAsync(string path, CancellationToken cancellationToken)
    {
        if (new FileInfo(path).Length != ModelSize) throw new IOException("The British voice model is incomplete. Remove the incomplete speech cache and try again.");
        await using var input = File.OpenRead(path);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false));
        if (!hash.Equals(ModelSha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The British voice model failed its integrity check.");
    }

    private async Task SpeakWindowsAsync(string text, string? voiceId, double rate, int volume,
        CancellationToken cancellationToken, Action<string>? progress, bool fallback = false)
    {
        await Task.Run(() =>
        {
            dynamic? sapi = null;
            dynamic? tokens = null;
            dynamic? selected = null;
            try
            {
                sapi = Activator.CreateInstance(Type.GetTypeFromProgID("SAPI.SpVoice")!);
                tokens = sapi!.GetVoices();
                if ((int)tokens!.Count == 0) throw new InvalidOperationException("No Windows text-to-speech voice is installed.");
                for (var i = 0; i < (int)tokens.Count; i++)
                {
                    dynamic candidate = tokens.Item(i);
                    if (voiceId == "sapi:" + (string)candidate.Id
                        || (voiceId is null && ((string)candidate.GetAttribute("Language")).Split(';').Contains("809")))
                    { selected = candidate; break; }
                    Marshal.ReleaseComObject(candidate);
                }
                if (selected is null && voiceId is not null) throw new InvalidOperationException("That Windows voice is unavailable.");
                selected ??= tokens.Item(0);
                sapi.Voice = selected;
                sapi.Rate = (int)Math.Clamp(Math.Round(((double.IsFinite(rate) ? Math.Clamp(rate, 0.6, 1.5) : 1) - 1) * 10), -4, 5);
                sapi.Volume = Math.Clamp(volume, 0, 100);
                var description = (string)selected.GetDescription() + (fallback ? " · Windows fallback (British model unavailable)" : " · Windows stock voice");
                SetVoiceDescription(description);
                progress?.Invoke(description);
                cancellationToken.ThrowIfCancellationRequested();
                sapi.Speak(text, 1 | 16); // async, plain text; user text cannot become speech XML
                while (!(bool)sapi.WaitUntilDone(100))
                    if (cancellationToken.IsCancellationRequested || SpeechService.Instance.IsBusy)
                    {
                        sapi.Speak("", 2); // purge queued speech
                        cancellationToken.ThrowIfCancellationRequested();
                        throw new OperationCanceledException("Voice playback stopped for microphone capture.");
                    }
            }
            finally
            {
                if (selected is not null) Marshal.ReleaseComObject(selected);
                if (tokens is not null) Marshal.ReleaseComObject(tokens);
                if (sapi is not null) Marshal.ReleaseComObject(sapi);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public static string PrepareSpeechText(string text)
    {
        text = Regex.Replace(text, @"```[\s\S]*?```", " Code is in the chat. ");
        text = Regex.Replace(text, @"\[([^\]]+)\]\([^)]+\)", "$1");
        text = Regex.Replace(text, @"[*#`]", "");
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length > 2400 ? text[..2400] + ". The remaining detail is in the conversation." : text;
    }

    /// <summary>
    /// The pieces a reply is synthesized and played in. Only the first is waited for, so it is the opening sentence
    /// alone, cut at a clause if it runs past 100 characters. Each later chunk is whole sentences, up to twice what is
    /// already queued and at most 220 characters: synthesis runs ~3x faster than playback, so every chunk is ready
    /// before the audio ahead of it runs out.
    /// </summary>
    private static IEnumerable<string> SpeechChunks(string text)
    {
        var pieces = new List<string>();
        foreach (var sentence in SentenceBreak.Split(text))
            for (var rest = sentence.Trim(); rest.Length > 0;)
            {
                var limit = pieces.Count == 0 ? FirstChunkLimit : ChunkLimit;
                var length = rest.Length <= limit ? rest.Length : CutPoint(rest, limit);
                pieces.Add(rest[..length].Trim());
                rest = rest[length..].Trim();
            }
        var queued = 0;
        for (var i = 0; i < pieces.Count;)
        {
            var chunk = pieces[i++];
            var budget = Math.Min(ChunkLimit, 2 * queued);
            while (i < pieces.Count && chunk.Length + 1 + pieces[i].Length <= budget) chunk += " " + pieces[i++];
            queued += chunk.Length;
            yield return chunk;
        }
    }

    /// <summary>Where to cut a sentence longer than <paramref name="limit"/>: after its last clause break that keeps at
    /// least a third of the limit, else at its last space, else hard at the limit (a path or address with no spaces).</summary>
    private static int CutPoint(string sentence, int limit)
    {
        var window = sentence[..(limit + 1)];
        var clause = -1;
        foreach (var mark in ClauseMarks)
        {
            var at = window.LastIndexOf(mark, StringComparison.Ordinal);
            if (at >= 0) clause = Math.Max(clause, at + mark.TrimEnd().Length);
        }
        if (clause >= limit / 3) return clause;
        var space = window.LastIndexOf(' ');
        return space > 0 ? space : limit;
    }

    private void SetVoiceDescription(string description) { VoiceDescription = description; VoiceChanged?.Invoke(); }
    private void OnCaptureStateChanged(object? sender, EventArgs e) { if (SpeechService.Instance.IsBusy) Stop(); }
    public void Stop() { lock (_stopGate) try { _speakingStop?.Cancel(); } catch (ObjectDisposedException) { } }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        SpeechService.Instance.StateChanged -= OnCaptureStateChanged;
        // Dispose the native model only after the last in-flight synthesis has released its lease.
        _ = Task.Run(async () => { await _synthesisGate.WaitAsync(); try { _model?.Dispose(); } finally { _synthesisGate.Release(); } });
    }

    private sealed class NonDisposingStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    }
}
