using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using VibeCode.Services;

namespace VibeCode.UI;

public sealed record JarvisMessage(string Role, string Text)
{
    public string Speaker => Role == "user" ? "You" : Role == "system" ? "Status" : "Jarvis";
}

public sealed class JarvisViewModel : Observable, IDisposable
{
    private readonly JarvisRuntime _runtime;
    private readonly JarvisSpeechService _speech;
    private readonly IJarvisMicrophone _microphone;
    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _audioTimer;
    private double _microphoneLevel;
    private long _lastAudioTick;
    private CancellationTokenSource? _operationStop;
    private long _operation;
    private int _micToken;
    private bool _dictationSubmits = true;
    private bool _disposed;
    private string _inputText = "";
    private string _state = "idle";
    private string _statusText = "Ready when you are.";
    private bool _busy;
    private bool _listening;
    private bool _speaking;

    public JarvisViewModel(IJarvisActionHost host, IJarvisPlanner? planner = null, JarvisSpeechService? speech = null,
        IJarvisMicrophone? microphone = null)
    {
        _runtime = new(planner ?? new JarvisDialogueService(), host);
        _speech = speech ?? JarvisSpeechService.Instance;
        _microphone = microphone ?? new JarvisMicrophone();
        _ui = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _audioTimer = new DispatcherTimer(DispatcherPriority.Render, _ui) { Interval = TimeSpan.FromMilliseconds(40) };
        _audioTimer.Tick += OnAudioTick;
        _speech.VoiceChanged += OnVoiceChanged;
        _microphone.StateChanged += OnSpeechStateChanged;
        AppSettings.Changed += OnSettingsChanged;
    }

    public ObservableCollection<JarvisMessage> Messages { get; } = new();
    public string InputText { get => _inputText; set { if (Set(ref _inputText, value)) Raise(nameof(CanSubmit)); } }
    public bool IsBusy { get => _busy; private set { if (Set(ref _busy, value)) RefreshAvailability(); } }
    public bool IsListening { get => _listening; private set { if (Set(ref _listening, value))
    {
        if (value) { _lastAudioTick = Stopwatch.GetTimestamp(); _audioTimer.Start(); }
        else { _audioTimer.Stop(); MicrophoneLevel = 0; }
        RefreshAvailability();
    } } }
    public bool IsSpeaking { get => _speaking; private set { if (Set(ref _speaking, value)) RefreshAvailability(); } }
    public string State { get => _state; private set { if (Set(ref _state, value)) { Raise(nameof(HasError)); Raise(nameof(PresenceText)); } } }
    public bool HasError => State == "error";
    public string StatusText { get => _statusText; private set { if (Set(ref _statusText, value)) Raise(nameof(PresenceText)); } }
    public double MicrophoneLevel { get => _microphoneLevel; private set => Set(ref _microphoneLevel, value); }
    public string PresenceText => State switch
    {
        "listening" => "Listening…", "thinking" => "Thinking…",
        "speaking" or "transcribing" or "notice" or "error" => StatusText,
        _ => HasMicrophone ? "" : "Connect a microphone, or type a message.",
    };
    public string TalkButtonText => IsBusy || IsSpeaking ? "Stop" : IsListening ? "Stop and send" : "Click to talk";
    public bool CanUseTalkButton => IsBusy || IsSpeaking || CanListen;
    public string VoiceDescription => _speech.VoiceDescription;
    public string ProviderDescription => $"{ProviderModelCatalog.DisplayName(AppSettings.Current.JarvisProvider)} · {AppSettings.Current.JarvisModel ?? "provider default"}";
    public bool CanSubmit => !_disposed && !IsBusy && !IsListening && !string.IsNullOrWhiteSpace(InputText);
    public bool CanListen => !_disposed && !IsBusy && (IsListening || (!_microphone.IsBusy && HasMicrophone));
    public bool HasMicrophone => _microphone.HasDevice;

    public async Task SubmitAsync()
    {
        if (!CanSubmit) return;
        var request = InputText.Trim();
        InputText = "";
        Messages.Add(new("user", request));
        var operation = ++_operation;
        using var stop = new CancellationTokenSource();
        _operationStop = stop;
        IsBusy = true;
        State = "thinking";
        StatusText = "Thinking with " + ProviderDescription;
        WarmUpVoice();
        try
        {
            var answer = await _runtime.SubmitAsync(request, JarvisSelection.FromSettings(), stop.Token,
                receipt => { if (Current(operation)) StatusText = receipt.Message; });
            if (!Current(operation)) return;
            Messages.Add(new("assistant", answer));
            StatusText = "Ready when you are.";
            if (AppSettings.Current.JarvisVoiceEnabled)
                await SpeakAsync(answer, stop.Token, operation);
            if (Current(operation)) State = "idle";
        }
        catch (OperationCanceledException ex)
        {
            if (Current(operation)) { StatusText = ex.Message; State = "idle"; }
        }
        catch (Exception ex)
        {
            if (!Current(operation)) return;
            State = "error";
            StatusText = ex.Message;
            Messages.Add(new("system", ex.Message));
        }
        finally
        {
            if (Current(operation)) { _operationStop = null; IsBusy = false; IsSpeaking = false; }
        }
    }

    public Task ToggleListeningAsync() => ToggleListeningCoreAsync(submit: true);
    public Task TestMicrophoneAsync() => ToggleListeningCoreAsync(submit: false);

    private async Task ToggleListeningCoreAsync(bool submit)
    {
        if (_disposed) return;
        var service = _microphone;
        if (IsListening && service.Owns(_micToken))
        {
            var token = _micToken;
            var operation = ++_operation;
            using var stop = new CancellationTokenSource();
            _operationStop = stop;
            IsListening = false;
            IsBusy = true;
            State = "transcribing";
            StatusText = service.ReadyWithoutDownload ? "Transcribing your request…" : "Preparing offline speech · first-use model download is about 1.5 GB";
            try
            {
                var work = service.StopAndTranscribeAsync(token);
                _ = work.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                var text = await work.WaitAsync(stop.Token);
                if (!Current(operation) || !service.Owns(token)) return;
                _micToken = 0;
                InputText = string.IsNullOrWhiteSpace(InputText) ? text : InputText.TrimEnd() + " " + text;
                StatusText = text.Length == 0 ? "I didn’t catch any speech. Try again." : _dictationSubmits ? "Heard you." : "Microphone test transcribed into the text box.";
                State = text.Length == 0 || !_dictationSubmits ? "notice" : "idle";
                IsBusy = false;
                if (_dictationSubmits && text.Length > 0) await SubmitAsync();
            }
            catch (OperationCanceledException) { if (Current(operation)) { State = "idle"; StatusText = "Dictation cancelled."; } }
            catch (Exception ex) { if (Current(operation)) { State = "error"; StatusText = "Speech-to-text failed: " + ex.Message; } }
            finally { if (Current(operation)) { _operationStop = null; IsBusy = false; RefreshAvailability(); } }
            return;
        }
        if (IsBusy) return;
        if (service.IsBusy)
        {
            StatusText = "Another composer owns the microphone. Finish its dictation first.";
            State = "notice";
            return;
        }
        _speech.Stop();
        var listeningOperation = ++_operation;
        // Start capture synchronously so the next activation always sees the current recording state.
        if (!Current(listeningOperation) || IsBusy || service.IsBusy) return;
        if (!service.StartRecording(out var error, out var tokenStarted, this))
        {
            State = "error";
            StatusText = error ?? "The microphone could not start.";
            RefreshAvailability();
            return;
        }
        _micToken = tokenStarted;
        _dictationSubmits = submit;
        IsListening = true;
        State = "listening";
        StatusText = submit ? "Listening… click Stop and send when you're finished." : "Microphone test… click again to transcribe.";
        if (submit) WarmUpVoice();
    }

    /// <summary>Load the reply voice in the background while the user is still talking or the model is thinking, so
    /// a reply only waits for its own first sentence.</summary>
    public void WarmUpVoice()
    {
        if (!_disposed && AppSettings.Current.JarvisVoiceEnabled) _speech.BeginWarmUp(AppSettings.Current.JarvisVoiceId);
    }

    public async Task TestVoiceAsync()
    {
        if (_disposed || IsBusy || _microphone.IsBusy)
        {
            StatusText = "Finish the current request or dictation before testing the voice.";
            return;
        }
        var operation = ++_operation;
        using var stop = new CancellationTokenSource();
        _operationStop = stop;
        IsBusy = true;
        try
        {
            await SpeakAsync("Hello. I’m Jarvis, your desktop assistant. Ready when you are.", stop.Token, operation);
            if (Current(operation)) { State = "idle"; StatusText = VoiceDescription; }
        }
        catch (OperationCanceledException) { if (Current(operation)) { State = "idle"; StatusText = "Voice test stopped."; } }
        catch (Exception ex) { if (Current(operation)) { State = "error"; StatusText = "Voice test failed: " + ex.Message; } }
        finally { if (Current(operation)) { IsBusy = false; IsSpeaking = false; _operationStop = null; } }
    }

    private async Task SpeakAsync(string text, CancellationToken token, long operation)
    {
        IsSpeaking = true;
        StatusText = "Speaking";
        State = "speaking";
        await _speech.SpeakAsync(text, AppSettings.Current.JarvisVoiceId, AppSettings.Current.JarvisSpeechRate,
            AppSettings.Current.JarvisSpeechVolume, token, progress => OnUi(() =>
            {
                if (Current(operation)) { StatusText = progress; Raise(nameof(VoiceDescription)); }
            }));
        if (Current(operation)) { IsSpeaking = false; StatusText = VoiceDescription; }
    }

    public void Cancel()
    {
        ++_operation;
        _operationStop?.Cancel();
        _operationStop = null;
        _speech.Stop();
        if (_micToken != 0 && _microphone.Owns(_micToken)) _microphone.ForceReset();
        _micToken = 0;
        IsListening = false;
        IsSpeaking = false;
        IsBusy = false;
        State = "idle";
        StatusText = "Stopped.";
    }

    public void ClearConversation()
    {
        Cancel();
        _runtime.ClearConversation();
        Messages.Clear();
        InputText = "";
        StatusText = "Ready for a fresh conversation.";
    }

    public void RefreshSettings()
    {
        RefreshMemoryPermission();
        Raise(nameof(ProviderDescription));
        Raise(nameof(VoiceDescription));
        RefreshAvailability();
    }

    public void RefreshMemoryPermission() => OnUi(RefreshMemoryPermissionCore);

    private void RefreshMemoryPermissionCore()
    {
        if (_disposed) return;
        _runtime.RefreshMemoryAccess();
        if (!_runtime.CurrentMemoryRevoked) return;
        if (_runtime.IsPlanning || IsSpeaking)
        {
            _operationStop?.Cancel();
            _speech.Stop();
            StatusText = "Second Brain access changed. Stale memory was discarded.";
        }
    }

    private void OnSettingsChanged() => OnUi(() => { if (!_disposed) RefreshSettings(); });

    private bool Current(long operation) => !_disposed && operation == _operation;
    private void RefreshAvailability()
    {
        Raise(nameof(CanListen)); Raise(nameof(CanSubmit)); Raise(nameof(HasMicrophone));
        Raise(nameof(CanUseTalkButton)); Raise(nameof(TalkButtonText)); Raise(nameof(PresenceText));
    }
    private void OnAudioTick(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        var frame = _microphone.LevelFrame;
        var target = IsListening && ReferenceEquals(_microphone.RecordingOwner, this)
            && _microphone.Owns(_micToken) && frame?.CaptureToken == _micToken
            && Stopwatch.GetElapsedTime(frame.CapturedAt, now).TotalMilliseconds < 250 ? frame.Amplitude.Level : 0;
        MicrophoneLevel = MicAmplitudePolicy.Smooth(MicrophoneLevel, target, Stopwatch.GetElapsedTime(_lastAudioTick, now).TotalSeconds);
        _lastAudioTick = now;
    }
    private void OnVoiceChanged() => OnUi(() => Raise(nameof(VoiceDescription)));
    private void OnSpeechStateChanged(object? sender, EventArgs e) => OnUi(() =>
    {
        if (_disposed) return;
        if (IsListening && !_microphone.Owns(_micToken))
        { IsListening = false; _micToken = 0; State = "idle"; StatusText = "Dictation ended."; }
        if (_microphone.RecordingOwner == this && _microphone.State == SpeechState.Downloading)
            StatusText = _microphone.DownloadProgressText ?? "Preparing offline speech…";
        RefreshAvailability();
    });
    private void OnUi(Action action)
    {
        if (_ui.CheckAccess()) action();
        else if (!_ui.HasShutdownStarted) _ui.BeginInvoke(action);
    }

    public void Dispose()
    {
        if (_disposed) return;
        Cancel();
        _disposed = true;
        _audioTimer.Stop();
        _audioTimer.Tick -= OnAudioTick;
        _speech.VoiceChanged -= OnVoiceChanged;
        _microphone.StateChanged -= OnSpeechStateChanged;
        AppSettings.Changed -= OnSettingsChanged;
    }
}
