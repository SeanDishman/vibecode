using VibeCode.UI;

namespace VibeCode.Services;

/// <summary>
/// The Games extension. Unlike Spotify and Weather it ships turned on for a fresh install, so the arcade shows up
/// in the titlebar out of the box - but it can still be switched off in Settings &gt; Extensions. The titlebar
/// controller binds its visibility to <see cref="Enabled"/>, while this service also publishes the current parked
/// game session while its WebView remains alive off-screen.
/// </summary>
public sealed class GamesService : Observable
{
    public static GamesService Instance { get; } = new();

    private string? _pausedGameId;
    private string? _pausedGameName;

    private GamesService() { }

    /// <summary>Whether the Games extension is turned on (mirrors AppSettings so the titlebar can bind to it).</summary>
    public bool Enabled
    {
        get => AppSettings.Current.GamesEnabled;
        set
        {
            if (AppSettings.Current.GamesEnabled == value) return;
            AppSettings.Current.GamesEnabled = value;
            AppSettings.Current.Save();
            Raise(nameof(Enabled));
        }
    }

    /// <summary>Re-read the settings-backed flag after something else wrote settings.json (the titlebar controller
    /// and the Extensions card both bind straight to <see cref="Enabled"/>).</summary>
    public void NotifyEnabledChanged() => Raise(nameof(Enabled));

    /// <summary>True while a game window is hidden rather than closed. Its WebView and JavaScript heap are still
    /// alive, so the current turn can resume without reloading the game.</summary>
    public bool HasPausedSession => _pausedGameId is not null;

    public string PausedGameName => _pausedGameName ?? string.Empty;
    public string SessionLabel => HasPausedSession ? "PAUSED · PROGRESS SAVED" : "READY TO PLAY";

    /// <summary>Per-game badge for the Games popup: empty unless that specific game is the parked one, so the
    /// menu stays quiet (just the game's name) until there is actually a run waiting to be resumed.</summary>
    public string SurviveTheShapesBadge => BadgeFor("survive-the-shapes");
    public string TowerDefenseBadge => BadgeFor("tower-defense");
    public string TinyEmpiresBadge => BadgeFor("tiny-empires");
    public string FishingTycoonBadge => BadgeFor("fishing-tycoon");

    private string BadgeFor(string gameId) =>
        string.Equals(_pausedGameId, gameId, StringComparison.Ordinal) ? "Resume" : string.Empty;

    public string SessionSummary => HasPausedSession
        ? "Your current run is frozen safely in place."
        : "Dodge the swarm · upgrade · fight bosses.";

    internal void MarkSessionPaused(string gameId, string gameName)
    {
        if (string.Equals(_pausedGameId, gameId, StringComparison.Ordinal) &&
            string.Equals(_pausedGameName, gameName, StringComparison.Ordinal))
            return;

        _pausedGameId = gameId;
        _pausedGameName = gameName;
        RaiseSessionState();
    }

    internal void ClearPausedSession(string? gameId = null)
    {
        if (_pausedGameId is null) return;
        if (gameId is not null && !string.Equals(_pausedGameId, gameId, StringComparison.Ordinal)) return;

        _pausedGameId = null;
        _pausedGameName = null;
        RaiseSessionState();
    }

    private void RaiseSessionState()
    {
        Raise(nameof(HasPausedSession));
        Raise(nameof(PausedGameName));
        Raise(nameof(SessionLabel));
        Raise(nameof(SurviveTheShapesBadge));
        Raise(nameof(TowerDefenseBadge));
        Raise(nameof(TinyEmpiresBadge));
        Raise(nameof(FishingTycoonBadge));
        Raise(nameof(SessionSummary));
    }
}
