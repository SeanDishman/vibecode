namespace VibeCode.Services;

/// <summary>Jarvis uses the shared, owned capture session. The adapter also allows hardware-free voice checks.</summary>
public interface IJarvisMicrophone
{
    event EventHandler? StateChanged;
    bool HasDevice { get; }
    bool IsBusy { get; }
    bool ReadyWithoutDownload { get; }
    SpeechState State { get; }
    object? RecordingOwner { get; }
    string? DownloadProgressText { get; }
    MicLevelFrame? LevelFrame { get; }
    bool Owns(int token);
    bool StartRecording(out string? error, out int token, object owner);
    Task<string> StopAndTranscribeAsync(int token);
    void ForceReset();
}

public sealed class JarvisMicrophone : IJarvisMicrophone
{
    private SpeechService Service => SpeechService.Instance;
    public event EventHandler? StateChanged { add => Service.StateChanged += value; remove => Service.StateChanged -= value; }
    public bool HasDevice => MicCapture.AnyDevice;
    public bool IsBusy => Service.IsBusy;
    public bool ReadyWithoutDownload => Service.ReadyWithoutDownload;
    public SpeechState State => Service.State;
    public object? RecordingOwner => Service.RecordingOwner;
    public string? DownloadProgressText => Service.DownloadProgressText;
    public MicLevelFrame? LevelFrame => Service.LevelFrame;
    public bool Owns(int token) => Service.Owns(token);
    public bool StartRecording(out string? error, out int token, object owner) => Service.StartRecording(out error, out token, owner);
    public Task<string> StopAndTranscribeAsync(int token) => Service.StopAndTranscribeAsync(token);
    public void ForceReset() => Service.ForceReset();
}
