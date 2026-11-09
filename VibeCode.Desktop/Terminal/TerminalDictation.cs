using VibeCode.Services;

namespace VibeCode.Terminal;

/// <summary>The desktop's shared microphone. Kept behind a small adapter so terminal tests never record audio.</summary>
public interface ITerminalSpeechSource
{
    event EventHandler? StateChanged;
    SpeechState State { get; }
    bool ReadyWithoutDownload { get; }
    string? DownloadProgressText { get; }
    bool Owns(int token);
    bool StartRecording(out string? error, out int token, object owner);
    Task<string> StopAndTranscribeAsync(int token);
    string? TakeTranscriptionNote();
    void ForceReset();
}

internal sealed class DesktopTerminalSpeech : ITerminalSpeechSource
{
    private SpeechService Service => SpeechService.Instance;
    public event EventHandler? StateChanged { add => Service.StateChanged += value; remove => Service.StateChanged -= value; }
    public SpeechState State => Service.State;
    public bool ReadyWithoutDownload => Service.ReadyWithoutDownload;
    public string? DownloadProgressText => Service.DownloadProgressText;
    public bool Owns(int token) => Service.Owns(token);
    public bool StartRecording(out string? error, out int token, object owner) => Service.StartRecording(out error, out token, owner);
    public Task<string> StopAndTranscribeAsync(int token) => Service.StopAndTranscribeAsync(token);
    public string? TakeTranscriptionNote() => Service.TakeTranscriptionNote();
    public void ForceReset() => Service.ForceReset();
}

/// <summary>One terminal's dictation ownership. All commands run on the desktop dispatcher, just like its mic button.</summary>
public sealed class TerminalDictation : IDisposable
{
    private readonly ITerminalSpeechSource _source;
    private readonly Action<Action> _dispatch;
    private int _token, _generation;
    private bool _finishing, _disposed;
    public string ContextId { get; private set; }
    public event Action<TerminalPacket>? PacketReady;

    public TerminalDictation(ITerminalSpeechSource source, string contextId, Action<Action> dispatch)
    {
        _source = source; ContextId = contextId; _dispatch = dispatch;
        _source.StateChanged += OnStateChanged;
    }

    public bool IsActive => _token != 0 && _source.Owns(_token);
    public bool NeedsProgressUpdates => IsActive && _source.State == SpeechState.Downloading;

    public TerminalSpeech Snapshot()
    {
        if (IsActive)
        {
            if (_source.State == SpeechState.Recording)
                return new("recording", "Listening · F5 stops and inserts · Ctrl+G cancels" +
                    (_source.ReadyWithoutDownload ? "" : $" · First transcription downloads {SpeechService.ModelDownloadSize}"));
            if (_source.State == SpeechState.Downloading)
                return new("downloading", "Downloading speech model · " + (_source.DownloadProgressText ?? SpeechService.ModelDownloadSize) + " · F5 cancels");
            if (_finishing || _source.State == SpeechState.Transcribing)
                return new("transcribing", "Transcribing · F5 or Ctrl+G cancels · Your draft is kept");
        }
        return _source.State != SpeechState.Idle
            ? new("busy", "Microphone is in use by another composer.")
            : new("idle", "F5 or click Mic to dictate · Enter sends after transcription");
    }

    public void Handle(string command)
    {
        if (_disposed) return;
        if (command == "cancel") { Cancel(); return; }
        if (command != "toggle") { Publish("dictation-state", "Unknown microphone command.", false); return; }
        if (IsActive)
        {
            if (_source.State == SpeechState.Recording && !_finishing)
            {
                _finishing = true;
                _ = FinishAsync(_token, _generation, ContextId);
            }
            else Cancel();
            return;
        }
        if (_source.State != SpeechState.Idle) { Publish("dictation-state", Snapshot().Hint, false); return; }
        try
        {
            if (!_source.StartRecording(out var error, out var token, this))
            { Publish("dictation-state", error ?? "Microphone unavailable. Check the desktop microphone settings.", false); return; }
            _token = token; _generation++; _finishing = false;
            Publish("dictation-state", Snapshot().Hint);
        }
        catch (Exception ex) { Publish("dictation-state", "Could not start microphone: " + ex.Message, false); }
    }

    private async Task FinishAsync(int token, int generation, string context)
    {
        try
        {
            var transcription = _source.StopAndTranscribeAsync(token);
            Publish("dictation-state", Snapshot().Hint);
            var text = await transcription;
            if (!Current(token, generation, context)) return;
            var note = _source.TakeTranscriptionNote();
            _token = 0; _finishing = false;
            Publish("dictation-result", string.IsNullOrWhiteSpace(text) ? "" : text.Trim(), true, note);
        }
        catch (Exception ex)
        {
            if (!Current(token, generation, context)) return;
            // Only our capture can be released. A newer desktop/terminal owner is never reset here.
            _token = 0; _finishing = false;
            if (_source.Owns(token)) _source.ForceReset();
            Publish("dictation-state", "Speech-to-text failed: " + ex.Message + " Your draft is kept.", false);
        }
    }

    private bool Current(int token, int generation, string context) =>
        !_disposed && _token == token && _generation == generation && ContextId == context && _source.Owns(token);

    public void Cancel()
    {
        var token = _token;
        _token = 0; _generation++; _finishing = false;
        if (token != 0 && _source.Owns(token)) _source.ForceReset();
        if (!_disposed) Publish("dictation-state", token != 0 ? "Dictation cancelled. Your draft is kept." : Snapshot().Hint);
    }

    public void SwitchContext(string contextId)
    {
        if (ContextId == contextId) return;
        Cancel(); ContextId = contextId;
    }

    public void RefreshProgress() { if (NeedsProgressUpdates) Publish("dictation-state", Snapshot().Hint); }

    private void OnStateChanged(object? sender, EventArgs e) => _dispatch(() =>
    {
        if (!_disposed) Publish("dictation-state", Snapshot().Hint);
    });

    private void Publish(string type, string text, bool accepted = true, string? note = null)
    {
        if (_disposed) return;
        var state = Snapshot();
        if (note is not null) state = state with { Hint = note };
        PacketReady?.Invoke(new(type, text, accepted, Speech: state, ContextId: ContextId));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _source.StateChanged -= OnStateChanged;
        Cancel();
    }
}
