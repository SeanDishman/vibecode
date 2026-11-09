using System.Globalization;
using System.IO;
using System.Speech.AudioFormat;
using System.Speech.Recognition;
using NAudio.Wave;

namespace VibeCode.Services;

/// <summary>
/// Dictation's second engine: Windows' own speech recognizer (SAPI, through System.Speech), run over the same
/// recording when local Whisper has kept the user waiting too long (see SpeechService.WhisperPatience). It is part of
/// Windows, so there is no model to download, no GPU and no API key, and it runs well ahead of real time: a 6.2 s
/// clip measured 2.5 s on this box, engine start included. The price is accuracy — it is much weaker than Whisper
/// with names, jargon and punctuation — which is why it only ever finishes a dictation Whisper has already had its
/// chance at, and never replaces Whisper outright.
/// </summary>
internal static class WindowsSpeechFallback
{
    /// <summary>How long a cancelled recognition gets to report back before its engine is disposed anyway.</summary>
    private static readonly TimeSpan CancelGrace = TimeSpan.FromSeconds(2);

    /// <summary>Transcribe a 16-bit PCM WAV — the format SpeechService records. Returns "" when nothing was
    /// recognised. Throws <see cref="NotSupportedException"/> synchronously when Windows has no English recognizer,
    /// so a caller can tell "there is no fallback here" from "the fallback ran and heard nothing".</summary>
    public static Task<string> TranscribeAsync(byte[] wav, CancellationToken cancel)
    {
        var recognizer = PickRecognizer();
        // On the pool, never the caller's thread: System.Speech raises its events through the SynchronizationContext
        // it was created on, so an engine built on the UI thread would queue every phrase behind the dispatcher.
        return Task.Run(() => TranscribeAsync(wav, recognizer, cancel), cancel);
    }

    /// <summary>The installed English recognizer: the user's own variety when Windows is set to one (en-GB,
    /// en-AU…), otherwise US English, otherwise any. English because Whisper is told the speech is English too.</summary>
    private static RecognizerInfo PickRecognizer()
    {
        IReadOnlyList<RecognizerInfo> installed;
        try { installed = SpeechRecognitionEngine.InstalledRecognizers(); }
        catch (Exception ex) { throw new NotSupportedException("Windows speech recognition is unavailable: " + ex.Message, ex); }
        var english = installed.Where(r => r.Culture.TwoLetterISOLanguageName == "en").ToList();
        return english.FirstOrDefault(r => r.Culture.Name == CultureInfo.CurrentUICulture.Name)
               ?? english.FirstOrDefault(r => r.Culture.Name == "en-US")
               ?? english.FirstOrDefault()
               ?? throw new NotSupportedException("Windows has no English speech recognizer installed.");
    }

    private static async Task<string> TranscribeAsync(byte[] wav, RecognizerInfo recognizer, CancellationToken cancel)
    {
        // The samples alone, with their format stated outright: NAudio can write header chunks of its own, and this
        // way the recognizer never has to parse them.
        byte[] pcm;
        WaveFormat format;
        using (var reader = new WaveFileReader(new MemoryStream(wav, writable: false)))
        {
            format = reader.WaveFormat;
            using var samples = new MemoryStream();
            reader.CopyTo(samples);
            pcm = samples.ToArray();
        }
        if (format.Encoding != WaveFormatEncoding.Pcm || format.BitsPerSample != 16 || format.Channels is not (1 or 2))
            throw new NotSupportedException($"Windows speech recognition can't read {format} audio.");

        // Declared first so it is disposed last: an engine that is slow to report a cancellation never reads a
        // disposed stream.
        using var audio = new MemoryStream(pcm, writable: false);
        using var engine = new SpeechRecognitionEngine(recognizer);
        engine.LoadGrammar(new DictationGrammar());
        engine.SetInputToAudioStream(audio, new SpeechAudioFormatInfo(format.SampleRate, AudioBitsPerSample.Sixteen,
            format.Channels == 1 ? AudioChannel.Mono : AudioChannel.Stereo));

        // Phrases arrive in order on one serialized worker, and all of them before RecognizeCompleted.
        var phrases = new List<string>();
        var done = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.SpeechRecognized += (_, e) => { lock (phrases) phrases.Add(e.Result.Text); };
        engine.RecognizeCompleted += (_, e) => done.TrySetResult(e.Error);
        // Multiple, not a Recognize() loop: Recognize() returns null for a phrase it rejects as well as at the end of
        // the audio, so a loop on it stops at the first mumble and drops everything said after it.
        engine.RecognizeAsync(RecognizeMode.Multiple);
        Exception? error;
        using (cancel.Register(() => { try { engine.RecognizeAsyncCancel(); } catch { /* finished already */ } }))
        {
            try { error = await done.Task.WaitAsync(cancel); }
            catch (OperationCanceledException)
            {
                // RecognizeAsyncCancel ends the recognition and reports back; the bound only covers an engine that
                // never does, so a cancelled fallback can't keep its engine alive for ever.
                await Task.WhenAny(done.Task, Task.Delay(CancelGrace));
                throw;
            }
        }
        if (error is not null) throw new InvalidOperationException("Windows speech recognition failed: " + error.Message, error);
        lock (phrases) return string.Join(" ", phrases.Select(p => p.Trim()).Where(p => p.Length > 0));
    }
}
