using System.IO;
using NAudio.Wave;
using VibeCode.Services;

internal static partial class Program
{
    private static void VerifySpeechProviderSwitch()
    {
        var service = SpeechService.Instance;
        var groq = GroqSpeechService.Instance;
        var enabled = groq.Enabled;
        var type = typeof(SpeechService).Assembly.GetType("VibeCode.Services.SpeechTranscription")!;
        var pending = typeof(SpeechService).GetField("_transcription", Flags)!;
        var generation = typeof(SpeechService).GetField("_captureGeneration", Flags)!;
        var wav = (byte[])Call(service, "SilenceWav", 1.0)!;
        var owner = new object();
        var token = 8100;

        (object Run, Task<string> Result, List<(bool Groq, byte[] Audio, CancellationToken Cancel, TaskCompletionSource<string> Reply)> Calls)
            Begin(bool cloud, SpeechState state = SpeechState.Transcribing)
        {
            service.ForceReset();
            groq.Enabled = cloud;
            var run = Activator.CreateInstance(type, Flags, null, [cloud], null)!;
            pending.SetValue(service, run);
            generation.SetValue(service, ++token);
            Property(service, "RecordingOwner", owner);
            Property(service, "State", state);
            var calls = new List<(bool Groq, byte[] Audio, CancellationToken Cancel, TaskCompletionSource<string> Reply)>();
            Func<bool, byte[], CancellationToken, Task<string>> transcribe = (useGroq, audio, cancel) =>
            {
                if (useGroq) Property(service, "State", SpeechState.Transcribing);
                var reply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                calls.Add((useGroq, audio, cancel, reply));
                return reply.Task; // deliberately ignores cancellation, like a stalled native call
            };
            var result = (Task<string>)Call(run, "RunAsync", wav, transcribe)!;
            return (run, result, calls);
        }

        string Finish(Task<string> result)
        {
            PumpUntil(() => result.IsCompleted);
            return result.GetAwaiter().GetResult();
        }

        try
        {
            foreach (var state in new[] { SpeechState.Transcribing, SpeechState.Downloading })
            {
                var active = Begin(false, state);
                var firstStarted = service.TranscriptionStartedAtUtc;
                Check($"{state}: local attempt started", active.Calls.Count == 1 && !active.Calls[0].Groq);
                groq.Enabled = true; // the real Settings binding and AppSettings.Changed notification
                PumpUntil(() => active.Calls.Count == 2);
                Check($"{state}: enabling Groq cancels local work immediately", active.Calls[0].Cancel.IsCancellationRequested);
                Check($"{state}: Groq receives the exact retained WAV", active.Calls[1].Groq
                    && ReferenceEquals(active.Calls[0].Audio, active.Calls[1].Audio) && active.Calls[1].Audio.SequenceEqual(wav));
                Check($"{state}: switching preserves the destination and capture token", service.Owns(token)
                    && ReferenceEquals(service.RecordingOwner, owner) && service.State == SpeechState.Transcribing);
                Check($"{state}: the replacement has a fresh watchdog start", service.TranscriptionStartedAtUtc > firstStarted);
                AppSettings.Current.Save();
                groq.Enabled = true;
                Settle();
                Check($"{state}: unrelated saves and repeated enable do not upload twice", active.Calls.Count == 2);
                active.Calls[0].Reply.SetResult("obsolete local text");
                Settle();
                Check($"{state}: late local text cannot win or release the microphone", !active.Result.IsCompleted && service.IsBusy && service.Owns(token));
                active.Calls[1].Reply.SetResult("the Groq transcript");
                Check($"{state}: the caller receives only the Groq result", Finish(active.Result) == "the Groq transcript");
                groq.Enabled = false;
                Settle();
                Check($"{state}: a completed recording cannot restart", active.Calls.Count == 2);
            }

            var reverse = Begin(true);
            groq.Enabled = false;
            PumpUntil(() => reverse.Calls.Count == 2);
            Check("switching off cancels the upload and retains its WAV for offline transcription",
                reverse.Calls[0].Cancel.IsCancellationRequested && !reverse.Calls[1].Groq
                && ReferenceEquals(reverse.Calls[0].Audio, reverse.Calls[1].Audio));
            reverse.Calls[0].Reply.SetException(new IOException("late cancelled upload"));
            reverse.Calls[1].Reply.SetResult("offline result");
            Check("an obsolete provider failure cannot replace the selected result", Finish(reverse.Result) == "offline result");

            var rapid = Begin(false);
            groq.Enabled = true;
            groq.Enabled = false;
            groq.Enabled = true;
            PumpUntil(() => rapid.Calls.Count >= 2 && rapid.Calls[^1].Groq && !rapid.Calls[^1].Cancel.IsCancellationRequested);
            Check("rapid toggles leave only the latest requested provider active", rapid.Calls[^1].Groq
                && rapid.Calls.SkipLast(1).All(c => c.Cancel.IsCancellationRequested)
                && rapid.Calls.All(c => ReferenceEquals(c.Audio, wav)));
            rapid.Calls[^1].Reply.SetResult("latest selection");
            Check("rapid switches deliver one transcript", Finish(rapid.Result) == "latest selection");
            foreach (var superseded in rapid.Calls.SkipLast(1)) superseded.Reply.SetResult("stale");

            var failure = Begin(false);
            groq.Enabled = true;
            PumpUntil(() => failure.Calls.Count == 2);
            failure.Calls[1].Reply.SetException(new InvalidOperationException("Groq key is missing"));
            try { _ = Finish(failure.Result); throw new Exception("Selected provider failure was swallowed"); }
            catch (InvalidOperationException ex) { Check("a current Groq error surfaces without falling back or looping", ex.Message == "Groq key is missing" && failure.Calls.Count == 2); }
            failure.Calls[0].Reply.SetResult("stale");

            var abandoned = Begin(false);
            service.ForceReset();
            Check("force reset cancels the actual pending transcription", abandoned.Calls[0].Cancel.IsCancellationRequested
                && !service.Owns(token) && service.State == SpeechState.Idle);
            var newerToken = ++token;
            generation.SetValue(service, newerToken);
            var newerOwner = new object();
            Property(service, "RecordingOwner", newerOwner);
            Property(service, "State", SpeechState.Recording);
            groq.Enabled = true;
            Check("changing provider during recording does not stop the microphone", service.State == SpeechState.Recording
                && service.Owns(newerToken) && ReferenceEquals(service.RecordingOwner, newerOwner));
            Check("abandoned work returns no text", Finish(abandoned.Result) == "");
            abandoned.Calls[0].Reply.SetResult("late abandoned text");
            Settle();
            Check("late abandoned work cannot change a newer recording", service.State == SpeechState.Recording && service.Owns(newerToken));

            // Run the actual capture-finalization path with synthetic PCM and the real missing-key error. No
            // hardware or HTTP is touched, and both failure and silence must release the original composer.
            service.ForceReset();
            pending.SetValue(service, null);
            Check("the isolated fixture has no Groq credential", !groq.HasKey);
            foreach (var sampleBytes in new[] { 32000, 0 })
            {
                var captureType = typeof(SpeechService).GetNestedType("Capture", Flags)!;
                var capture = Activator.CreateInstance(captureType, nonPublic: true)!;
                var buffer = new MemoryStream();
                var writer = new WaveFileWriter(buffer, new WaveFormat(16000, 16, 1));
                writer.Write(new byte[sampleBytes], 0, sampleBytes);
                captureType.GetField("Gen")!.SetValue(capture, ++token);
                captureType.GetField("Buffer")!.SetValue(capture, buffer);
                captureType.GetField("Writer")!.SetValue(capture, writer);
                captureType.GetField("Tcs")!.SetValue(capture, new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously));
                generation.SetValue(service, token);
                typeof(SpeechService).GetField("_current", Flags)!.SetValue(service, capture);
                Property(service, "RecordingOwner", owner);
                Property(service, "State", SpeechState.Recording);
                var actual = service.StopAndTranscribeAsync(token);
                if (sampleBytes == 0) Check("an empty clip does not call either provider", Finish(actual) == "");
                else
                {
                    try { _ = Finish(actual); throw new Exception("Missing Groq credential should be reported"); }
                    catch (InvalidOperationException ex) { Check("the real Groq path reports a missing credential", ex.Message.Contains("no API key is saved")); }
                }
                Check("the real pipeline releases its composer after completion or failure", service.State == SpeechState.Idle
                    && service.RecordingOwner is null && service.TranscriptionStartedAtUtc is null && service.Owns(token));
            }
        }
        finally
        {
            service.ForceReset();
            groq.Enabled = enabled;
        }
    }
}
