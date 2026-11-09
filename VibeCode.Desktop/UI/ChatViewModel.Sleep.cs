using System.IO;
using VibeCode.Protocol;
using VibeCode.Services;

namespace VibeCode.UI;

/// <summary>
/// A restored chat that is not on screen sleeps instead of starting its provider. Every provider launches each

/// many CLIs and over a thousand helper processes. A sleeping chat still loads its usage and task recovery, but not
/// its history: that replays when the chat is first needed - opened, sent a prompt, or woken by a peer - and its
/// provider starts right after, as an asleep Codex chat (whose CLI replays its own history) always worked.
/// </summary>
public sealed partial class ChatViewModel
{
    private bool _providerAsleep;
    private int? _asleepAtVersion;

    /// <summary>History not replayed yet because the chat restored asleep and off screen. One such Claude chat measured
    /// 17,358 messages: 2.3 s of parsing and 5 s of UI-thread replay, a 3.8 s freeze among it, on every launch.</summary>
    private bool _historyDeferred;

    /// <summary>Restored without its provider. <see cref="Wake"/> starts it.</summary>
    public bool IsAsleep => _providerAsleep;

    /// <summary>Restore this chat without launching its provider.</summary>
    internal void StartAsleep() => Start(asleep: true);

    /// <summary>Start a sleeping chat's provider. Does nothing for a chat that is awake.</summary>
    public void Wake()
    {
        if (!_providerAsleep || Status == "closed") return;
        _providerAsleep = false;
        Raise(nameof(IsAsleep));
        if (_asleepAtVersion is not { } version) return;   // its history is still loading; that start carries on
        _asleepAtVersion = null;
        Status = "starting";
        if (_idleSleep) ResumeFromIdleSleep(version);
        else if (_historyDeferred) StartWithTranscript(version);   // replay the history first; the provider starts after it
        else ContinueStart(version);
    }

    /// <summary>Called where a start would launch the provider. True when this chat sleeps there instead.</summary>
    private bool StayAsleep(int version)
    {
        if (!_providerAsleep) return false;
        _asleepAtVersion = version;
        if (Status == "starting") Status = "idle";

        return true;
    }

    /// <summary>
    /// Open a Gemini chat's transcript without launching Antigravity. The ACP server starts on the next prompt.
    /// </summary>
    internal void RevealGeminiWhileAsleep()
    {
        if (!IsGemini || !_providerAsleep || Status == "closed") return;

        if (!_historyDeferred || ResumeSessionId is null) return;
        var version = _asleepAtVersion ?? Volatile.Read(ref _startVersion);
        StartWithTranscript(version);
    }

    /// <summary>A prompt for a sleeping chat wakes it. The prompt waits in the queue until the session is ready.</summary>
    private bool SendWhileAsleep(string text, IReadOnlyList<Attachment>? attachments)
    {
        Wake();
        if (_session is { HasExited: false }) return Send(text, attachments);
        if (Status is "closed" or "error" || string.IsNullOrWhiteSpace(text) && attachments is not { Count: > 0 }) return false;
        QueuePrompt(text, attachments is { Count: > 0 } ? attachments.ToList() : null, extended: false);
        MessageSent?.Invoke();
        return true;
    }
}
