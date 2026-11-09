namespace VibeCode.Services;

/// <summary>Keep one finished recording while its provider changes. Only the latest attempt may return text.</summary>
internal sealed class SpeechTranscription(bool useGroq)
{
    private readonly object _gate = new();
    private bool _useGroq = useGroq;
    private bool _cancelled;
    private bool _finished;
    private bool _running;
    private DateTime? _startedAtUtc;
    private DateTime? _fallbackAtUtc;
    private CancellationTokenSource? _attempt;

    public DateTime? StartedAtUtc { get { lock (_gate) return _startedAtUtc; } }

    /// <summary>When the current attempt handed its recording to Windows' recognizer as well; null until then.</summary>
    public DateTime? FallbackStartedAtUtc { get { lock (_gate) return _fallbackAtUtc; } }

    /// <summary>Restart the current attempt's clock once its one-time model download is done, so the download's
    /// minutes never count against the transcription deadline or the composers' watchdogs.</summary>
    public void RestartClock() { lock (_gate) if (_attempt is not null) _startedAtUtc = DateTime.UtcNow; }

    public void MarkFallback() { lock (_gate) if (_attempt is not null) _fallbackAtUtc = DateTime.UtcNow; }

    public void SwitchProvider(bool useGroq)
    {
        CancellationTokenSource? attempt;
        lock (_gate)
        {
            if (_finished || _cancelled || _useGroq == useGroq) return;
            _useGroq = useGroq;
            attempt = _attempt;
        }
        CancelAttempt(attempt);
    }

    public void Cancel()
    {
        CancellationTokenSource? attempt;
        lock (_gate)
        {
            _cancelled = true;
            attempt = _attempt;
        }
        CancelAttempt(attempt);
    }

    public async Task<string> RunAsync(byte[] wav, Func<bool, byte[], CancellationToken, Task<string>> transcribe)
    {
        lock (_gate)
        {
            if (_running) throw new InvalidOperationException("This recording is already being transcribed.");
            _running = true;
        }
        try
        {
            while (true)
            {
                CancellationTokenSource stop;
                bool groq;
                lock (_gate)
                {
                    if (_cancelled) return "";
                    groq = _useGroq;
                    _attempt = stop = new CancellationTokenSource();
                    _startedAtUtc = DateTime.UtcNow;
                    _fallbackAtUtc = null;
                }
                Task<string>? work = null;
                try
                {
                    work = transcribe(groq, wav, stop.Token);
                    // Native inference may ignore cancellation. Stop waiting immediately and observe its eventual
                    // completion separately; it must never insert stale text or delay the newly selected provider.
                    var text = await work.WaitAsync(stop.Token);
                    lock (_gate)
                    {
                        if (_cancelled) return "";
                        if (!stop.IsCancellationRequested && groq == _useGroq)
                        {
                            _finished = true;
                            return text;
                        }
                    }
                }
                catch (Exception) when (Superseded(stop, groq)) { }
                finally
                {
                    lock (_gate) { if (ReferenceEquals(_attempt, stop)) _attempt = null; }
                    if (work is null) stop.Dispose();
                    else _ = work.ContinueWith(t => { _ = t.Exception; stop.Dispose(); },
                        CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
            }
        }
        finally { lock (_gate) _finished = true; }
    }

    private bool Superseded(CancellationTokenSource stop, bool groq)
    {
        lock (_gate) return _cancelled || stop.IsCancellationRequested || groq != _useGroq;
    }

    private static void CancelAttempt(CancellationTokenSource? attempt)
    {
        try { attempt?.Cancel(); }
        catch (ObjectDisposedException) { /* the prior attempt finished while the provider was changing */ }
    }
}
