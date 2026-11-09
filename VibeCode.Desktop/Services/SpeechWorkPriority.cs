namespace VibeCode.Services;

/// <summary>Prioritize active transcription over new session startup; recording and downloads can overlap startup.</summary>
internal static class SpeechWorkPriority
{
    private static readonly object Gate = new();
    private static TaskCompletionSource? _idle;

    public static bool IsBusy { get { lock (Gate) return _idle is not null; } }

    public static void SetBusy(bool busy)
    {
        TaskCompletionSource? release = null;
        lock (Gate)
        {
            if (busy) _idle ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            else { release = _idle; _idle = null; }
        }
        release?.TrySetResult();
    }

    public static async Task WaitUntilIdleAsync()
    {
        while (true)
        {
            Task? wait;
            lock (Gate) wait = _idle?.Task;
            if (wait is null) return;
            await wait.ConfigureAwait(false);
        }
    }
}

/// <summary>A short-lived, dedicated worker keeps cloud dictation off the UI and shared worker queue.</summary>
internal static class SpeechRequestWorker
{
    public static Task<string> Run(Func<CancellationToken, string> request, CancellationToken cancel)
    {
        if (cancel.IsCancellationRequested) return Task.FromCanceled<string>(cancel);
        var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try
            {
                cancel.ThrowIfCancellationRequested();
                var text = request(cancel);
                cancel.ThrowIfCancellationRequested();
                result.TrySetResult(text);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { result.TrySetCanceled(cancel); }
            catch (Exception ex) { result.TrySetException(ex); }
        }) { IsBackground = true, Name = "VibeCode speech upload", Priority = ThreadPriority.AboveNormal };
        try { worker.Start(); }
        catch (Exception ex) { result.TrySetException(ex); }
        return result.Task;
    }
}
