using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VibeCode.Services;

/// <summary>One reopenable chat tab: its folder, resumable session id, and title.</summary>
public sealed class OpenChatState
{
    public string Cwd { get; set; } = "";
    public string? SessionId { get; set; }
    public string Provider { get; set; } = "claude";
    public string? Title { get; set; }
    public bool Active { get; set; }
    /// <summary>The chat selected in the optional full-shell second-monitor window.</summary>
    public bool SecondaryActive { get; set; }
    public bool Pinned { get; set; }
    public string? AccountId { get; set; }   // which Claude account this chat runs under (for per-session auth on restore)
    /// <summary>This chat's permission mode - ask (<c>default</c>) / auto / plan / bypassPermissions. Persisted per
    /// chat because it is a per-conversation decision: one chat parked in plan mode while another runs on bypass is
    /// normal, and a restart that quietly put both back on "ask" meant re-picking the mode on every launch. Null in
    /// snapshots written before this was remembered; those fall back to <see cref="AppSettings.DefaultMode"/>.
    /// <para>Restored through <c>ChatViewModel.SetMode</c>, never by assigning the property: a provider's
    /// <c>init</c> event reports the CLI's own mode and would overwrite anything the pane had not registered as a
    /// deliberate choice.</para></summary>
    public string? Mode { get; set; }
    /// <summary>Unsent composer text. A force quit must not throw away a prompt the user was still writing.</summary>
    public string? Draft { get; set; }
    /// <summary>Chat is held back from the Second Brain. Persisted so a restart cannot quietly start recording a
    /// chat the user deliberately muted.</summary>
    public bool ExcludeFromMemory { get; set; }
}

/// <summary>One project folder remembered by the new-chat picker, ordered by actual use rather than transcript scan time.</summary>
public sealed class RecentDirectoryState
{
    public string Cwd { get; set; } = "";
    public DateTimeOffset LastUsed { get; set; }
}

/// <summary>One saved bridge peer - enough to re-spawn its provider thread later.</summary>
public sealed class SavedBridgePane
{
    public string Cwd { get; set; } = "";
    public string? SessionId { get; set; }   // resumable id; null = never started, skip
    public string? Label { get; set; }       // e.g. "Claude 2" or "Codex 2"
    public string? Title { get; set; }
    public string? Provider { get; set; }    // null only in legacy snapshots; inherit the host provider on migration
    public string? AccountId { get; set; }
    public string? Mode { get; set; }
    public string? Model { get; set; }
    public string? Effort { get; set; }
    /// <summary>This pane's own fast-mode choice. Null in snapshots written before fast mode became per-chat, which
    /// is why it is nullable: those fall back to the host's, not to a hard false.</summary>
    public bool? FastMode { get; set; }
    /// <summary>Unsent composer text for this pane.</summary>
    public string? Draft { get; set; }
    /// <summary>Peer is held back from the Second Brain, normally inherited from the host chat it was started from.</summary>
    public bool ExcludeFromMemory { get; set; }
    /// <summary>True when this pane was the bridge's designated manager (the "brain" that dispatches the others).</summary>
    public bool IsManager { get; set; }
}

/// <summary>A saved bridge so a Close / navigate-away / app-restart can resume the peers.
/// The first agent is a normal chat in OpenChats; this only stores the peers + a link to the host.</summary>
public sealed class SavedBridgeState
{
    public string Cwd { get; set; } = "";
    public string HostSessionId { get; set; } = "";   // matches the host's OpenChats entry
    public string Provider { get; set; } = "claude";  // host provider; each peer persists its own provider
    public string? HostTitle { get; set; }             // lets startup recover the anchor even if OpenChats was stale
    public string? HostAccountId { get; set; }
    public bool HostPinned { get; set; }
    /// <summary>True when the HOST pane was the bridge's designated manager (peers store their own flag).</summary>
    public bool HostIsManager { get; set; }
    public string? Mode { get; set; }                 // permission mode to reapply to resumed peers
    /// <summary>This roster's coordination board file name. Null in snapshots written before bridges could be forked;
    /// those all shared the project's single ".vibecode-bridge.md". A forked bridge owns a numbered one instead, and
    /// resuming it has to land on the same file or it starts claiming areas on the original roster's board.</summary>
    public string? Board { get; set; }
    public List<SavedBridgePane> Peers { get; set; } = new();
    public DateTime SavedAt { get; set; }
}

/// <summary>Persistent user settings - %APPDATA%\VibeCode\settings.json.</summary>
public sealed class AppSettings
{
    public HashSet<string> HiddenProjects { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Session ids created or opened inside VibeCode - the only ones shown when ShowOnlyOwnedSessions is on.</summary>
    public HashSet<string> OwnedSessions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Sessions the user deleted. Closing a chat only takes its sidebar row away; the transcript stays in the
    /// provider's own store and the id stays in <see cref="OwnedSessions"/>, so a "deleted" chat used to walk straight
    /// back into the project browser and the recent list. A tombstone is what keeps it gone - and it must outlive the
    /// file, because the transcript belongs to the CLI, not to VibeCode.</summary>
    public HashSet<string> DeletedSessions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>When true, the sidebar/home show only VibeCode's own chats, not the whole Claude Code history.</summary>
    public bool ShowOnlyOwnedSessions { get; set; } = true;
    /// <summary>Opt back in to per-account workspaces, where the sidebar lists only the active login's chats.
    /// Default false: switching Claude/Codex/Grok accounts must never make a conversation disappear - that reads as
    /// data loss even though nothing was deleted. Off-account rows stay put, carry a chip naming their login, and keep
    /// spawning under their own account (ChatViewModel passes its own AccountId to the provider's config dir).</summary>
    public bool IsolateChatsByAccount { get; set; }
    public List<string> Backgrounds { get; set; } = new();
    public string? ActiveBackground { get; set; }       // null → built-in gif
    public bool RandomBackground { get; set; }
    public int BackgroundVisibility { get; set; } = 22; // % of the art visible on the home screen
    /// <summary>Overall UI mode: "background" (default art-backed look) or "cli" (terminal-style theme).
    /// Applied at startup by App.OnStartup swapping the theme dictionary; changing it requires a restart
    /// because the app resolves theme brushes with StaticResource at window load.</summary>
    public string UiMode { get; set; } = "background";
    /// <summary>True when the terminal-style theme is active for this process.</summary>
    public static bool IsCliMode => string.Equals(Current.UiMode, "cli", StringComparison.OrdinalIgnoreCase);
    /// <summary>Borderless look: the app's own chrome - titlebar, sidebar, right panel, chat header and the Bridge
    /// panes - stops painting its grey surfaces and keeps only a thin black outline, and the background art is
    /// stretched behind the whole window instead of the chat column alone. So the wallpaper IS the background.
    /// Cards *inside* a transcript (your prompts, tool cards, code blocks) keep their fill - text has to stay
    /// readable over the art. Applied by merging Themes/Borderless.xaml over the base theme, through the same
    /// in-place shell reload <see cref="UiMode"/> uses; nothing restarts.</summary>
    public bool Borderless { get; set; }
    /// <summary>True when this process loaded the borderless surfaces. CLI mode wins: a flat terminal canvas has
    /// no art to reveal, so there is nothing for borderless to do there.</summary>
    public static bool IsBorderless => Current.Borderless && !IsCliMode;
    // null = auto | low | medium | high | xhigh | max | ultracode (Claude only: xhigh + dynamic workflows)
    public string? DefaultEffort { get; set; }
    public string? DefaultModel { get; set; }           // null = CLI default; else a model alias/value key (opus, sonnet, …)
    /// <summary>Provider used when the home screen starts a new chat.</summary>
    public string DefaultProvider { get; set; } = "claude"; // claude | codex | kimi | grok

    private string _defaultMode = DefaultPermissionMode;
    /// <summary>The permission mode a NEW chat starts in, rewritten every time the user deliberately picks one -
    /// exactly like <see cref="DefaultModel"/> and <see cref="DefaultEffort"/> follow the model and thinking pickers.
    /// Without it, picking bypass (or plan) was a per-chat choice that had to be made again for every conversation and
    /// every launch. Chats that are still open carry their OWN mode in <see cref="OpenChatState.Mode"/>; this is only
    /// the seed for one that has no remembered mode of its own.
    /// <para>Inherit paths - a bridge peer taking the host's mode, a supervised restart, a chat moved to another
    /// provider - deliberately do NOT write here: they are the app copying a mode across, not the user choosing one.</para>
    /// <para>The setter clamps, so a hand-edited settings.json cannot seed a chat with a mode no provider accepts.</para></summary>
    public string DefaultMode
    {
        get => _defaultMode;
        set => _defaultMode = NormalizePermissionMode(value);
    }

    /// <summary>What a fresh install starts on: VibeCode's client-side Auto policy (edits and commands run, dangerous
    /// ones still ask), which is the mode every new chat used before any of this was remembered.</summary>
    public const string DefaultPermissionMode = "auto";

    /// <summary>Every mode a chat can actually be in. "auto" is VibeCode's own client-side policy, "dontAsk" is Kimi's;
    /// the rest are CLI modes. Anything else is a hand-edited or future value that no provider would accept.</summary>
    public static bool IsKnownPermissionMode(string? mode) => mode?.Trim() is
        "default" or "auto" or "plan" or "acceptEdits" or "bypassPermissions" or "dontAsk";

    /// <summary>The mode as given, or <see cref="DefaultPermissionMode"/> when it is not one a provider has heard of.</summary>
    public static string NormalizePermissionMode(string? mode) =>
        IsKnownPermissionMode(mode) ? mode!.Trim() : DefaultPermissionMode;
    public string? DefaultCodexEffort { get; set; }     // null = model default
    public string? DefaultCodexModel { get; set; }      // null = Codex CLI default
    public string? DefaultKimiEffort { get; set; }      // null = CLI default; on/off or K3 low/high/max when advertised
    public string? DefaultKimiModel { get; set; }       // null = Kimi CLI default
    public string? DefaultGrokEffort { get; set; }      // null = Grok model default
    public string? DefaultGrokModel { get; set; } = Protocol.Grok45Preset.NormalModelId;
    /// <summary>GLM's own model/effort seeds. They exist for the same reason every other provider has a pair:
    /// without them GLM falls through to <see cref="DefaultModel"/>/<see cref="DefaultEffort"/> — Claude's slots —
    /// and the two providers write over each other. Picking a GLM model would put a <c>zai-org/…</c> id in Claude's
    /// picker, where Claude Code accepts it, caches it in its own <c>.claude.json</c> and lists it back as a
    /// "custom model" on every launch afterwards. The effort slot is kept even though GLM ignores effort on the
    /// wire, so that a shared slot can never become the path by which Claude's <c>ultracode</c> level leaks into
    /// another provider's request.</summary>
    public string? DefaultGlmModel { get; set; }        // null = GlmPreset.DefaultModelId
    public string? DefaultGlmEffort { get; set; }       // null = no effort sent (GLM has no effort knob)
    /// <summary>Optional proxy for the account manager's "Delete all chats" action. grok.com's chat REST API sits
    /// behind Cloudflare, which challenges most home IPs (a plain request gets a 403 "Just a moment" page); routing
    /// through a clean-IP proxy is what lets the stored CLI token reach and delete conversations. Format
    /// <c>host:port</c> or <c>host:port@user:pass</c>. Never a credential to a VibeCode service - it's the user's own proxy.</summary>
    public string? GrokDeleteProxy { get; set; }
    /// <summary>Claude Code "fast mode" (faster output on supported models), applied per session via the CLI's
    /// <c>--settings {"fastMode":true}</c>. This is only the SEED for the next chat created - the live flag belongs
    /// to each chat (<see cref="UI.ChatViewModel.FastMode"/>), so toggling one Bridge pane cannot drag its siblings
    /// along. Written by whichever chat toggled it last, exactly like DefaultModel / DefaultEffort.</summary>
    public bool FastMode { get; set; }
    /// <summary>Which saved Claude account new chats run under. Each chat spawns with THIS account's OAuth token in
    /// its env, so switching accounts (changing this) never touches a running chat's login. Null = the live ~/.claude login.</summary>
    public string? ActiveAccountId { get; set; }

    /// <summary>Privacy: remove account emails from detailed account views so they are safe to screen-share.
    /// Primary account surfaces always use a screen-safe name plus the provider and plan.</summary>
    public bool HideEmails { get; set; }

    /// <summary>Condense consecutive Bash, edit, web-search, and MCP/browser tool calls into one expandable activity
    /// card. The group opens to a one-line list, and every row can then be opened independently for full details.</summary>
    public bool CompactMode { get; set; }

    // Desktop notifications (both off by default). A toast is shown only when the chat it concerns is NOT fully
    // visible on screen - covered by another app, minimized, on another virtual desktop, or simply not the
    // selected chat/pane - so the user is never nagged about something already in front of them.
    /// <summary>Toast when any chat or Bridge agent finishes its turn (agent name + a snippet of the reply).</summary>
    public bool NotifyOnTurnEnd { get; set; }
    /// <summary>Toast when an agent stops to wait on the user - a question, a plan review, or a permission request.</summary>
    public bool NotifyOnAwaitingInput { get; set; }
    /// <summary>Which chime a toast plays. A <see cref="NotificationSounds"/> id: "silent", "classic", a bundled
    /// stem such as "06-marimba", or "custom:&lt;filename&gt;" for a file in %APPDATA%\VibeCode\Sounds.</summary>
    public string NotificationSound { get; set; } = NotificationSounds.DefaultId;

    /// <summary>When a visible Bridge has at least three agents and Windows reports another active display, keep the
    /// first two panes in the main shell and show the remaining panes in a coordinated companion window.</summary>
    public bool DualMonitorBridge { get; set; }
    /// <summary>Keep a second full chat shell open on another display. Both windows share live chats and Bridge
    /// processes, but each remembers a different selected chat; account and settings controls remain primary-only.</summary>
    public bool DualMonitorDoubleSessions { get; set; }
    private int _bridgeAgentLimit = BridgeAgentPolicy.DefaultAgentLimit;
    /// <summary>Maximum root agents a Bridge may grow to. The setter clamps hand-edited settings on deserialize too.</summary>
    public int BridgeAgentLimit
    {
        get => _bridgeAgentLimit;
        set => _bridgeAgentLimit = BridgeAgentPolicy.ClampLimit(value);
    }

    /// <summary>Bridge agents keep a "## Live activity" board in .vibecode-bridge.md alongside the area claims: one
    /// compact block per agent naming the file(s) it is in right now and what it is adding there, rewritten in place
    /// at checkpoints (start/switch/finish a file) rather than per edit, so the extra awareness stays cheap.
    /// Off = classic high-level coordination only.</summary>
    public bool BridgeRealtimeSharing { get; set; }

    /// <summary>Let bridge agents send prompts straight to each other with <c>@@MSG agent=N</c>, instead of only the
    /// manager being able to reach anyone. Off means the status board is the only sideways channel again — an agent
    /// that hits something outside its lane can then only do the work in someone else's area or drop it.
    /// Loop-bounded either way (see <see cref="PeerMessageLimits"/>); the switch exists for rosters where the extra
    /// turns are not wanted.</summary>
    public bool BridgePeerMessaging { get; set; } = true;

    /// <summary>Expose provider-native child-agent swarms in Claude, Codex, and Grok chats. Kimi is excluded.</summary>
    public bool AgentSwarmsEnabled { get; set; } = true;
    /// <summary>Bridge peers are already parallel root CLIs, so child swarms inside each pane require a separate opt-in.</summary>
    public bool AgentSwarmsInBridge { get; set; }
    /// <summary>Maximum child workers one explicitly requested swarm turn may create.</summary>
    public int SwarmMaxWorkers { get; set; } = SwarmPolicy.DefaultMaxWorkers;

    // ---- agent supervision: the orchestrator actively watches every delegated agent and escalates on a bounded
    // ladder (intervene -> restart on the same task -> take the task over itself). Off means a delegated agent can
    // stall forever with nobody noticing, which is exactly the failure these thresholds exist to end.

    /// <summary>Watch delegated agents and intervene when one stalls. Applies to any managed Bridge, and is always on
    /// in Demon Mode (a locked read-only worker has no other way to be rescued).</summary>
    public bool AgentSupervisionEnabled { get; set; } = true;
    /// <summary>Seconds of mid-turn silence before a working agent counts as stale.</summary>
    public int SupervisionStaleSeconds { get; set; } = 300;
    /// <summary>Seconds an agent may hold an assignment while not running before it counts as stuck.</summary>
    public int SupervisionIdleSeconds { get; set; } = 90;
    /// <summary>Seconds an intervention has to be answered before the supervisor escalates past it.</summary>
    public int SupervisionInterventionGraceSeconds { get; set; } = 120;
    /// <summary>Hard ceiling on one assignment however healthy it looks. 0 disables the cap.</summary>
    public int SupervisionMaxTaskSeconds { get; set; } = 3600;
    /// <summary>Interventions per assignment before the supervisor stops asking and restarts the agent.</summary>
    public int SupervisionMaxInterventions { get; set; } = 1;
    /// <summary>Restarts per assignment before the orchestrator completes the task itself.</summary>
    public int SupervisionMaxRestarts { get; set; } = 1;
    /// <summary>Identical consecutive turn outputs that count as a loop.</summary>
    public int SupervisionLoopRepeatThreshold { get; set; } = 3;

    // ---- Demon Mode: a preset Bridge of 4-17 sessions with one locked orchestrator and the rest read-only workers.

    // There is deliberately no "Demon Mode enabled" preference. The Settings switch starts and stops a team directly,
    // and a team is never resumed across restarts — so a persisted "on" could only ever describe a team that no longer
    // exists. (An older build stored one; unknown keys in settings.json are ignored, so it simply falls away.)

    /// <summary>Demon Mode's orchestrator plans and dispatches only. Turn this on to also have it review, correct and
    /// validate worker output — the one behaviour the mode otherwise deliberately withholds.</summary>
    public bool DemonOrchestratorReviewsWork { get; set; }

    private int _demonSessionCount = DemonModePolicy.SessionCount;
    /// <summary>How many sessions the next Demon team stands up, orchestrator included. Unlike the switch itself this
    /// IS remembered: the size is a standing preference about how much of the machine and the account's quota a team
    /// may take, and re-picking it on every start would be the dialog asking a question it already knows the answer
    /// to. The setter clamps, so a hand-edited settings.json cannot ask for a roster the wall cannot lay out.</summary>
    public int DemonSessionCount
    {
        get => _demonSessionCount;
        set => _demonSessionCount = DemonModePolicy.ClampSessionCount(value);
    }

    /// <summary>VibeCode's provider-neutral MCP catalog. Definitions are projected at launch instead of overwriting
    /// Claude, Codex, Kimi, or Grok's own configuration files.</summary>
    public List<McpServerDefinition> McpServers { get; set; } = new();

    // Second Brain (agentmemory). The daemon is a native Windows sidecar; VibeCode talks to its REST API directly
    // so capture/recall works the same way for Claude, Codex, Kimi, Grok, and Bridge agents. Authentication remains
    // in AGENTMEMORY_SECRET - never serialize bearer tokens into settings.json.
    public bool AgentMemoryEnabled { get; set; } = true;
    public bool AgentMemoryAutoRecall { get; set; } = true;
    // Promote corrections, decisions and stated habits without being asked. Waiting for an explicit "remember this"
    // leaves the durable store empty through exactly the turns worth keeping.
    public bool AgentMemoryAutoRemember { get; set; } = true;
    public string AgentMemoryEndpoint { get; set; } = "http://127.0.0.1:3111";

    // Spotify extension (optional; off by default). The Client ID is the user's own registered Spotify app id
    // (public, PKCE - no secret). OAuth tokens live in a separate spotify-auth.json, never here.
    public bool SpotifyEnabled { get; set; }
    public string? SpotifyClientId { get; set; }

    // Weather extension (optional; off by default). Reads public NWS/Open-Meteo endpoints - no account or key.
    // The saved point is deliberately city-level, including when it came from approximate network location.
    public bool WeatherEnabled { get; set; }
    public string? WeatherPlace { get; set; }
    public double? WeatherLat { get; set; }
    public double? WeatherLon { get; set; }
    public string? WeatherCountryCode { get; set; }
    /// <summary>Remembered radar window bounds so it reopens where the user last dragged it.</summary>
    public double? RadarLeft { get; set; }
    public double? RadarTop { get; set; }
    public double? RadarWidth { get; set; }
    public double? RadarHeight { get; set; }

    // Games extension. Unlike Spotify and Weather, this one ships ON for a fresh install - it's the friendly
    // default that shows off the extension surface - but it can still be turned off in Settings > Extensions.
    public bool GamesEnabled { get; set; } = true;

    /// <summary>Route mic dictation through Groq's hosted Whisper large-v3 instead of the offline medium.en model.
    /// Off by default, and deliberately so: the offline path never sends audio anywhere, and this one uploads the
    /// clip. Only the flag lives here - the Groq API key is DPAPI-sealed in its own groq-speech.json, because a
    /// credential is never serialized into settings.json (see <see cref="GroqSpeechService"/>).</summary>
    public bool GroqSpeechEnabled { get; set; }

    // Telemetry HUD and wall. Both ship ON: this is a window whose entire purpose is to be parked on a spare
    // screen and watched, so landing there and reading as live is the behaviour that should need no discovery.
    /// <summary>Open the telemetry windows on the display VibeCode is NOT on, when there is more than one.
    /// A HUD the user has already dragged onto some other display is left exactly where they put it.</summary>
    public bool TelemetryOnCompanionDisplay { get; set; } = true;
    /// <summary>Travel the charts to each new reading instead of snapping, and keep a slow ripple on the
    /// sparklines. Presentation only - every figure still settles on the real one.</summary>
    public bool TelemetryLiveAnimation { get; set; } = true;
    /// <summary>How far back the telemetry wall looks, in hours - 1 to 720. Remembered because the wall is a
    /// window you set up once and leave running for weeks; re-picking the range at every launch would be a
    /// chore. An unrecognised value falls back to the same 1 h default rather than failing the window.
    ///
    /// This value IS the wall's default, not merely its memory: the window restores whatever is here and only
    /// falls back to its own constant when nothing matches, so leaving this at 24 while the window defaulted to
    /// 1 h would have shipped a "default" no fresh install ever saw.</summary>
    public double TelemetryWallRangeHours { get; set; } = 1;

    // session restore: reopen the chats (and window) from last time
    /// <summary>Provider-neutral MRU folders for the five new-chat suggestions. This is updated immediately when a
    /// chat is created or used, so the picker does not have to wait for a provider transcript to appear on disk.</summary>
    public List<RecentDirectoryState> RecentDirectories { get; set; } = new();
    public List<OpenChatState> OpenChats { get; set; } = new();
    /// <summary>Every closed/backgrounded bridge, keyed by provider + host session. Keeping more than one
    /// matters: opening a new bridge must not silently orphan the previous bridge's peer conversations.</summary>
    public List<SavedBridgeState> SavedBridges { get; set; } = new();
    /// <summary>Legacy single-slot bridge state. Read once and migrated into <see cref="SavedBridges"/>; retained only
    /// so settings written by older builds deserialize without losing the last bridge.</summary>
    public SavedBridgeState? SavedBridge { get; set; }
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

    // ---- shell layout, so a relaunch (or a force quit) comes back to the view the user left ----
    /// <summary>The left sidebar was collapsed for full-width chat.</summary>
    public bool SidebarCollapsed { get; set; }
    /// <summary>The bridge overlay was on screen. A background bridge stays dormant behind its host cue instead.</summary>
    public bool BridgeVisible { get; set; }
    /// <summary>The second full-shell window was showing its assigned Bridge panes instead of its selected chat.</summary>
    public bool SecondaryBridgeVisible { get; set; }

    // ---- run in background: closing the window is not the same thing as quitting ----

    /// <summary>Closing the shell leaves VibeCode running in the notification area instead of ending the process,
    /// so every chat, Bridge peer and Demon worker keeps coding while the window is gone. ON by default: the app's
    /// whole point is agents that work on their own, and a close button that killed a nine-agent Bridge mid-task
    /// was destroying real work on a click people make absent-mindedly. Off restores the literal reading - the
    /// close button ends the process, and every running agent with it.
    /// <para>
    /// Ignored while <c>VIBECODE_HIDDEN=1</c>: an automated run closes the window to end the app, and a harness
    /// that instead left a trayed process behind would hang and then pile up.
    /// </para></summary>
    public bool RunInBackground { get; set; } = true;

    /// <summary>The "it is still running down here" balloon has been shown once. A window that vanishes into an icon
    /// has to explain itself the first time or it reads as a crash - and has to stop explaining itself after that.</summary>
    public bool BackgroundNoticeShown { get; set; }

    // Phone extension (optional; off by default, like Spotify and Weather). Until this is switched on in
    // Settings > Extensions the titlebar has no phone button at all and the bridge never auto-starts. This is a
    // separate flag from PhoneBridgeEnabled on purpose: this one is "does the feature exist in the UI", that one
    // is "was the listening socket left on". Hiding the door must also close it, so switching this off stops the
    // bridge - but switching it back on only restores the button, not the socket.
    public bool PhoneEnabled { get; set; }

    /// <summary>Serve chats to paired phones on the local network (Settings ▸ the phone button in the titlebar).
    /// Off unless the user has switched it on - a chat app should not open a listening socket by default.</summary>
    public bool PhoneBridgeEnabled { get; set; }
    /// <summary>TCP port the phone bridge binds. 0 means "use the default".</summary>
    public int PhoneBridgePort { get; set; }

    public static string Dir => Environment.GetEnvironmentVariable("VIBECODE_DATA_DIR") is { Length: > 0 } overrideDirectory
        ? Path.GetFullPath(overrideDirectory.Trim('"'))
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VibeCode");
    public static string BackgroundsDir => Path.Combine(Dir, "backgrounds");
    private static string FilePath => Path.Combine(Dir, "settings.json");

    // Load uses SupportedProviders and the other static helpers below. A property initializer runs before
    // those fields, causing every non-Claude settings file to be misclassified as corrupt on a fresh process.
    // Lazy evaluates only after ALL field initializers have finished. Reading Dir for the single-instance gate
    // must also remain independent of loading/recovering settings in a second process.
    private static readonly Lazy<AppSettings> CurrentSettings = new(Load);
    public static AppSettings Current => CurrentSettings.Value;

    /// <summary>One-time recovery of valid files quarantined by the old startup initialization bug.</summary>
    public int SettingsStartupRecoveryVersion { get; set; }
    public static event Action? Changed;
    /// <summary>Raised when a save adopted another VibeCode window's newer <see cref="ActiveAccountId"/> instead of
    /// reverting it. The account UI listens so it can re-render under the account that actually won.</summary>
    public static event Action? ActiveAccountAdopted;

    /// <summary>Settings exactly as this instance last read or wrote them. Every save is a three-way merge against it:
    /// a field THIS instance changed wins, a field only DISK changed is adopted, so one window can never revert another
    /// window's (or its own earlier run's) unrelated settings by rewriting the whole file from stale memory.</summary>
    private AppSettings? _baseline;

    /// <summary>Bridges this instance deliberately dropped, so the merge doesn't resurrect them from disk.</summary>
    private readonly HashSet<string> _droppedBridgeKeys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Serializes saves within the process - background token/usage refreshes save from worker threads.</summary>
    private static readonly object SaveLock = new();

    /// <summary>The <see cref="ActiveAccountId"/> this instance last saw on disk - what to roll memory back to when a
    /// deliberate selection didn't land, so the app never behaves as an account the user was told wasn't saved.</summary>
    [JsonIgnore]
    public string? LastPersistedActiveAccountId => _baseline?.ActiveAccountId;

    /// <summary>Previous good copy, rotated in by every successful save. A crash while writing, a half-flushed file
    /// after a power loss, or a torn read leaves this intact.</summary>
    private static string BackupPath => Path.Combine(Dir, "settings.bak.json");

    /// <summary>How the current settings were obtained. Anything other than <c>Loaded</c> means the user is one save
    /// away from losing real state, so the UI can say so instead of silently presenting a blank app.</summary>
    public enum LoadOutcome { Loaded, FirstRun, RecoveredFromBackup, RecoveredHistory, Quarantined }

    /// <summary>Set once at startup. Read by the shell to warn when settings had to be recovered or quarantined.
    /// <c>Loaded</c> is the zero value.</summary>
    public static LoadOutcome StartupOutcome { get; private set; }

    /// <summary>Where an unreadable settings.json was moved, if it was. Never deleted - it may be hand-recoverable.</summary>
    public static string? QuarantinedPath { get; private set; }

    private static AppSettings Load()
    {
        SweepStaleTemps();

        // The main file first, then the rotated backup. A single bad read used to mean "start fresh", and because the
        // very next save rewrites the whole document, that silently destroyed every chat, bridge and preference.
        if (TryLoadFrom(FilePath, out var loaded))
        {
            StartupOutcome = LoadOutcome.Loaded;
            RecoverMisclassifiedHistory(loaded!);
            return loaded!;
        }

        var mainFileExisted = File.Exists(FilePath);
        if (TryLoadFrom(BackupPath, out var recovered))
        {
            StartupOutcome = LoadOutcome.RecoveredFromBackup;
            if (mainFileExisted) QuarantinedPath = Quarantine();
            RecoverMisclassifiedHistory(recovered!);
            return recovered!;
        }

        // Nothing readable. If a file IS there but neither it nor the backup parsed, move it aside rather than let the
        // next save overwrite it - a file we cannot read is not the same thing as a file that has nothing in it.
        if (mainFileExisted)
        {
            QuarantinedPath = Quarantine();
            StartupOutcome = LoadOutcome.Quarantined;
        }
        else StartupOutcome = LoadOutcome.FirstRun;

        // Baseline the defaults so the first save can still tell "the user changed this" from "another instance wrote
        // it while we were starting up".
        var fresh = new AppSettings();
        fresh._baseline = fresh.Clone();
        RecoverMisclassifiedHistory(fresh);
        return fresh;
    }

    private static bool TryLoadFrom(string path, out AppSettings? settings)
    {
        settings = null;
        AppSettings? loaded;
        try
        {
            if (!File.Exists(path)) return false;
            loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
        if (loaded is null) return false;
        // A programming error in a migration is not evidence that the user's file is corrupt. Keep migrations
        // outside the parse-error catch so such an error can never quarantine valid data and replace it with defaults.
        loaded.HiddenProjects = new HashSet<string>(loaded.HiddenProjects ?? [], StringComparer.OrdinalIgnoreCase);
        loaded.OwnedSessions = new HashSet<string>(loaded.OwnedSessions ?? [], StringComparer.OrdinalIgnoreCase);
        loaded.DeletedSessions = new HashSet<string>(loaded.DeletedSessions ?? [], StringComparer.OrdinalIgnoreCase);
        loaded.OpenChats ??= new();
        loaded.OpenChats.RemoveAll(chat => chat is null);
        loaded.Backgrounds ??= new();
        loaded.NormalizeBridgeState();
        loaded.NormalizeRecentDirectories();
        // Folders still in the MRU must not stay on the hide list — that combo made heavily-used projects
        // (e.g. WpfApp3) vanish from the five new-chat chips even though RecentDirectories still listed them.
        loaded.UnhideRecentlyUsedProjects();
        loaded.NormalizeSwarmSettings();
        loaded.NormalizeProviderModelSlots();
        loaded.NormalizeMcpServers();
        loaded._baseline = loaded.Clone();
        settings = loaded;
        return true;
    }

    private static void RecoverMisclassifiedHistory(AppSettings settings)
    {
        if (settings.SettingsStartupRecoveryVersion >= 1 || !Directory.Exists(Dir)) return;
        List<AppSettings> snapshots = new();
        try
        {
            foreach (var path in Directory.GetFiles(Dir, "settings.corrupt-*.json").OrderByDescending(Path.GetFileName, StringComparer.Ordinal))
            {
                if (TryLoadFrom(path, out var snapshot)
                    && !string.Equals(snapshot!.DefaultProvider, "claude", StringComparison.OrdinalIgnoreCase))
                    snapshots.Add(snapshot);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        if (snapshots.Count == 0) return;

        // Preserve current preferences and current tabs. Only the newest lost layout is reopened; older copies
        // restore history/ownership and dormant bridges without spawning every old conversation at startup.
        foreach (var snapshot in snapshots)
        {
            settings.DeletedSessions.UnionWith(snapshot.DeletedSessions);
            settings.OwnedSessions.UnionWith(snapshot.OwnedSessions);
        }
        settings.OwnedSessions.ExceptWith(settings.DeletedSessions);
        settings.RecentDirectories = RecentDirectoryHistory.NormalizeRemembered(settings.RecentDirectories.Concat(
            snapshots.SelectMany(snapshot => snapshot.RecentDirectories)
                .Where(entry => !settings.HiddenProjects.Contains(entry.Cwd))));

        static string ChatKey(OpenChatState chat) => $"{chat.Provider}\n{chat.SessionId ?? chat.Cwd}";
        var keys = settings.OpenChats.Select(ChatKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hadTabs = settings.OpenChats.Count > 0;
        foreach (var chat in snapshots[0].OpenChats)
        {
            if (chat.SessionId is { } id && settings.DeletedSessions.Contains(id)) continue;
            if (!keys.Add(ChatKey(chat))) continue;
            if (hadTabs) { chat.Active = false; chat.SecondaryActive = false; }
            settings.OpenChats.Add(chat);
        }
        var bridgeKeys = settings.SavedBridges.Select(bridge => BridgeKey(bridge.HostSessionId, bridge.Provider))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var bridge in snapshots.SelectMany(snapshot => snapshot.SavedBridges))
        {
            if (settings.DeletedSessions.Contains(bridge.HostSessionId)
                || !bridgeKeys.Add(BridgeKey(bridge.HostSessionId, bridge.Provider))) continue;
            bridge.Peers.RemoveAll(peer => peer.SessionId is { } id && settings.DeletedSessions.Contains(id));
            if (bridge.Peers.Count > 0) settings.SavedBridges.Add(bridge);
        }
        settings.TrimSavedBridges();
        // Keep the original baseline: these recovered entries are local changes that must survive MergeWithDisk.
        // Persist the migration marker with the same atomic write as the recovered history, so later closes/deletes
        // are never undone by re-importing these files on every launch. The source files remain untouched.
        settings.SettingsStartupRecoveryVersion = 1;
        StartupOutcome = LoadOutcome.RecoveredHistory;
    }

    private static string? Quarantine()
    {
        try
        {
            var dest = Path.Combine(Dir, $"settings.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.Move(FilePath, dest, overwrite: true);
            return dest;
        }
        catch { return null; }   // locked by another instance; it still owns a good copy, so leave it alone
    }

    /// <summary>Migrate the old one-bridge slot and collapse duplicate snapshots after an interrupted/concurrent save.
    /// Public so the protocol regression harness can verify settings compatibility without touching the real file.</summary>
    public bool NormalizeBridgeState()
    {
        SavedBridges ??= new List<SavedBridgeState>();
        var candidates = SavedBridges.Where(x => x is not null).ToList();
        if (SavedBridge is not null) candidates.Add(SavedBridge);
        var filledLegacyPeerProviders = false;

        foreach (var bridge in candidates)
        {
            if (string.IsNullOrWhiteSpace(bridge.Provider)) bridge.Provider = "claude";
            bridge.Peers ??= new List<SavedBridgePane>();
            bridge.Peers.RemoveAll(peer => peer is null);
            foreach (var peer in bridge.Peers.Where(peer => string.IsNullOrWhiteSpace(peer.Provider)))
            {
                // Before mixed-provider bridges, every peer implicitly used the host provider. Materialize that old
                // contract once so subsequent saves can restore each pane independently.
                peer.Provider = bridge.Provider;
                filledLegacyPeerProviders = true;
            }
        }

        var normalized = candidates
            .Where(x => !string.IsNullOrWhiteSpace(x.HostSessionId))
            .GroupBy(x => $"{x.Provider}\n{x.HostSessionId}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(x => x.SavedAt).First())
            .OrderByDescending(x => x.SavedAt)
            .ToList();

        var changed = filledLegacyPeerProviders
                      || SavedBridge is not null
                      || normalized.Count != SavedBridges.Count
                      || !normalized.SequenceEqual(SavedBridges);
        SavedBridges = normalized;
        SavedBridge = null;
        return changed;
    }

    /// <summary>
    /// Retire settings left behind by the removed in-app provider.
    ///
    /// Two things outlive it in a settings.json written by an older build. Its provider id can still be sitting in
    /// <see cref="DefaultProvider"/>, which would have every new chat ask for a provider this build no longer has;
    /// and one of its model ids can still be in <see cref="DefaultModel"/> — Claude's slot, which it used to write
    /// into before it had seeds of its own. That second one is not inert: Claude Code accepts an unknown id, caches
    /// it in its own <c>.claude.json</c>, and lists it back as a "custom model" on every launch afterwards.
    /// </summary>
    public bool NormalizeProviderModelSlots()
    {
        var changed = false;
        if (!string.Equals(DefaultProvider, "claude", StringComparison.OrdinalIgnoreCase)
            && !SupportedProviders.Contains(DefaultProvider))
        {
            DefaultProvider = "claude";
            changed = true;
        }
        if (IsRetiredModelId(DefaultModel) || Protocol.CodexSession.IsRetiredModel(DefaultModel))
        {
            DefaultModel = null;   // back to "whatever the Claude CLI recommends"
            changed = true;
        }
        if (Protocol.CodexSession.IsRetiredModel(DefaultCodexModel))
        {
            DefaultCodexModel = null;   // let Codex's live catalog choose its current recommended model
            changed = true;
        }
        // Bridge peers remember their own model. Clear a retired id here as well as at the launch boundary so the
        // repaired snapshot stays repaired after the next save instead of requesting GPT-5.5 on every restart.
        if (SavedBridges is null)
        {
            SavedBridges = new List<SavedBridgeState>();
            changed = true;
        }
        foreach (var bridge in SavedBridges)
        {
            if (bridge.Peers is null)
            {
                bridge.Peers = new List<SavedBridgePane>();
                changed = true;
            }
            foreach (var peer in bridge.Peers)
            {
                if (!Protocol.CodexSession.IsRetiredModel(peer.Model)) continue;
                peer.Model = null;
                changed = true;
            }
        }
        // A GLM id in Claude's slot is the same failure with a live provider instead of a retired one, so it is
        // carried across rather than dropped: the GLM model the user picked stays their GLM default.
        if (Protocol.GlmPreset.IsGlmModelId(DefaultModel))
        {
            DefaultGlmModel ??= DefaultModel;
            DefaultModel = null;
            changed = true;
        }
        return changed;
    }

    private static readonly HashSet<string> SupportedProviders =
        new(["claude", "codex", "kimi", "grok", Protocol.GlmPreset.ProviderId], StringComparer.OrdinalIgnoreCase);

    /// <summary>No Claude, Codex, Kimi or Grok model id contains "deepseek", so the substring safely catches the
    /// retired provider's ids — including preset ids this build has never heard of.</summary>
    private static bool IsRetiredModelId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.Contains("deepseek", StringComparison.OrdinalIgnoreCase);

    /// <summary>Clamp hand-edited or older settings before they reach provider launch arguments.</summary>
    public bool NormalizeSwarmSettings()
    {
        var normalized = SwarmPolicy.ClampMaxWorkers(SwarmMaxWorkers);
        if (normalized == SwarmMaxWorkers) return false;
        SwarmMaxWorkers = normalized;
        return true;
    }

    /// <summary>Repair hand-edited/older MCP settings without ever importing or mutating provider-native configs.</summary>
    public bool NormalizeMcpServers()
    {
        if (McpServers is null)
        {
            McpServers = new List<McpServerDefinition>();
            return true;
        }
        var dropped = DropRetiredCodeGraphServers();
        return McpCatalog.NormalizeDefinitions(McpServers) || dropped;
    }

    /// <summary>
    /// Drop the code-graph server every earlier build registered here. The feature is gone, but its definition was
    /// written into settings.json - and nothing left in the app strips it at launch, so without this every chat
    /// would still start a graphify-mcp process pointed at no graph. A graphify server the user added by hand
    /// carries a different id and is deliberately left alone.
    /// </summary>
    private bool DropRetiredCodeGraphServers() => McpServers.RemoveAll(server =>
        server?.Id?.StartsWith("vibecode-second-brain-graphify", StringComparison.OrdinalIgnoreCase) == true) > 0;

    /// <summary>Move a valid folder to the front of the persistent MRU list.
    /// Also un-hides the folder: deliberately opening/using a project means the user wants it again,
    /// so a prior "Hide this project" must not keep wiping it from the new-chat chips.</summary>
    public bool RememberRecentDirectory(string? cwd, DateTimeOffset? lastUsed = null)
    {
        if (RecentDirectoryHistory.NormalizePath(cwd) is not { } normalized) return false;
        var beforeDirs = Json(RecentDirectories ?? new List<RecentDirectoryState>());
        var beforeHidden = Json(HiddenProjects.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList());
        RecentDirectories ??= new List<RecentDirectoryState>();
        RecentDirectories.Add(new RecentDirectoryState
        {
            Cwd = normalized,
            LastUsed = (lastUsed ?? DateTimeOffset.UtcNow).ToUniversalTime(),
        });
        NormalizeRecentDirectories();
        // Drop every spelling of this path from the hide-list (forward/back slashes both show up historically).
        var removedHidden = HiddenProjects.RemoveWhere(h => RecentDirectoryHistory.PathsEqual(h, normalized)) > 0;
        return removedHidden || beforeDirs != Json(RecentDirectories)
            || beforeHidden != Json(HiddenProjects.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>Most recently used project folder that still exists on disk (ignores hide list — text field needs it).</summary>
    public string? MostRecentExistingDirectory()
    {
        foreach (var entry in RecentDirectories ?? Enumerable.Empty<RecentDirectoryState>())
        {
            if (RecentDirectoryHistory.NormalizePath(entry.Cwd) is { } cwd && Directory.Exists(cwd))
                return cwd;
        }
        return null;
    }

    /// <summary>Clear hide-list entries that still appear in the MRU (any path spelling).</summary>
    public bool UnhideRecentlyUsedProjects()
    {
        if (HiddenProjects.Count == 0 || RecentDirectories is not { Count: > 0 }) return false;
        var before = HiddenProjects.Count;
        HiddenProjects.RemoveWhere(hidden =>
            RecentDirectories.Any(entry => RecentDirectoryHistory.PathsEqual(hidden, entry.Cwd)));
        return HiddenProjects.Count != before;
    }

    /// <summary>Repair hand-edited/legacy history, collapse path spelling duplicates, and keep the file bounded.</summary>
    public bool NormalizeRecentDirectories()
    {
        var before = Json(RecentDirectories ?? new List<RecentDirectoryState>());
        RecentDirectories = RecentDirectoryHistory.NormalizeRemembered(RecentDirectories);
        return before != Json(RecentDirectories);
    }

    public SavedBridgeState? FindSavedBridge(string? hostSessionId, string provider)
    {
        if (hostSessionId is null) return null;
        return SavedBridges.FirstOrDefault(x =>
            string.Equals(x.HostSessionId, hostSessionId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Provider, provider, StringComparison.OrdinalIgnoreCase));
    }

    internal static string BridgeKey(string? hostSessionId, string? provider) =>
        $"{(string.IsNullOrWhiteSpace(provider) ? "claude" : provider)}\n{hostSessionId}";

    /// <summary>Insert or replace one host's snapshot without disturbing dormant bridges belonging to other hosts.</summary>
    public void UpsertSavedBridge(SavedBridgeState snapshot)
    {
        RemoveSavedBridge(snapshot.HostSessionId, snapshot.Provider);
        _droppedBridgeKeys.Remove(BridgeKey(snapshot.HostSessionId, snapshot.Provider));
        SavedBridges.Insert(0, snapshot);
        TrimSavedBridges();
    }

    public bool RemoveSavedBridge(string? hostSessionId, string provider)
    {
        if (hostSessionId is null) return false;
        // Remember the removal: the save-time merge must not restore it from another window's copy of the file.
        _droppedBridgeKeys.Add(BridgeKey(hostSessionId, provider));
        return SavedBridges.RemoveAll(x =>
            string.Equals(x.HostSessionId, hostSessionId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Provider, provider, StringComparison.OrdinalIgnoreCase)) > 0;
    }

    /// <summary>Bridge snapshots are small pointers, but they must not grow without bound across months of use.</summary>
    public const int MaxSavedBridges = 40;

    private void TrimSavedBridges()
    {
        if (SavedBridges.Count <= MaxSavedBridges) return;
        SavedBridges = SavedBridges.OrderByDescending(x => x.SavedAt).Take(MaxSavedBridges).ToList();
    }

    /// <summary>Drop only unusable pointers. SavedAt is intentionally ignored: provider transcripts outlive the
    /// app's live-process idle timeout, so an overnight/longer shutdown must not erase bridge history.</summary>
    public int RemoveMalformedSavedBridges()
    {
        var doomed = SavedBridges.Where(IsMalformed).ToList();
        foreach (var bridge in doomed) _droppedBridgeKeys.Add(BridgeKey(bridge.HostSessionId, bridge.Provider));
        return SavedBridges.RemoveAll(IsMalformed);

        static bool IsMalformed(SavedBridgeState x) =>
            string.IsNullOrWhiteSpace(x.HostSessionId)
            || x.Peers is null
            || !x.Peers.Any(p => !string.IsNullOrWhiteSpace(p.SessionId));
    }

    // A crash between the temp write and the Move strands a uniquely-named temp that nothing else would ever remove,
    // so they accumulate forever. Only sweep old ones - a fresh temp may belong to another instance mid-save.
    private static void SweepStaleTemps()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(Dir, "settings.json.*.tmp"))
                if (DateTime.Now - File.GetLastWriteTime(f) > TimeSpan.FromHours(1))
                    try { File.Delete(f); } catch { /* best effort */ }
        }
        catch { /* no folder yet / unreadable - nothing to sweep */ }
    }

    /// <summary>Fire-and-forget save. Kept so the ~20 existing call sites that can't act on a failure stay unchanged;
    /// anything that must NOT report unverified success (account switching) calls <see cref="TrySave"/> instead.</summary>
    public void Save() => TrySave();

    /// <summary>Writes settings atomically (temp file + move) and reports why it failed instead of swallowing it.
    /// Returns null on success. A half-written settings.json would fail Load()'s parse and get silently replaced by
    /// defaults - wiping ActiveAccountId, OpenChats and the window bounds - so the write is never done in place.</summary>
    public Exception? TrySave(bool activeAccountIsDeliberate = false, bool defaultModeIsDeliberate = false)
    {
        lock (SaveLock) return TrySaveCore(activeAccountIsDeliberate, defaultModeIsDeliberate);
    }

    private Exception? TrySaveCore(bool activeAccountIsDeliberate, bool defaultModeIsDeliberate)
    {
        NormalizeSwarmSettings();
        NormalizeProviderModelSlots();
        NormalizeMcpServers();
        NormalizeRecentDirectories();
        var adopted = MergeWithDisk(activeAccountIsDeliberate, defaultModeIsDeliberate);
        // Again after the merge: the three-way merge can adopt a retired model or server still sitting in a settings
        // file written by another running instance. Do not publish that stale value back to disk.
        NormalizeProviderModelSlots();
        DropRetiredCodeGraphServers();
        Exception? verifyError = null;
        var moved = false;   // once the Move lands the save HAS succeeded - nothing after it may report a failure
        try
        {
            Directory.CreateDirectory(Dir);
            // Unique temp name per write: a fixed FilePath + ".tmp" is SHARED across instances, so two windows saving
            // at once either cross-write (one publishes the other's whole settings object) or race the Move.
            var tmp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, this, new JsonSerializerOptions { WriteIndented = true });
                    stream.Flush(flushToDisk: true);
                }
                // File.Replace publishes the new file AND rotates the old one into settings.bak.json in a single
                // operation, so there is always a previous good copy to recover from and never a moment with neither.
                if (File.Exists(FilePath))
                {
                    try { File.Replace(tmp, FilePath, BackupPath, ignoreMetadataErrors: true); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
                    {
                        File.Move(tmp, FilePath, overwrite: true);   // Replace needs both files intact; Move always works
                    }
                }
                else File.Move(tmp, FilePath, overwrite: true);
                moved = true;
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* don't let stray temps pile up */ }
                throw;
            }
            _baseline = Clone();   // disk now matches memory: this is the new merge base

            // Only meaningful for a deliberate account change: catch a clobber landing inside the move→read window, so
            // "Switched to X." is never reported for a write that didn't survive. (The far commoner stale overwrite -
            // another window saving minutes later - is invisible to a read-back by construction; MergeWithDisk
            // is what handles that one.)
            if (activeAccountIsDeliberate)
            {
                // The read-back is only ever a check on someone else's clobber. If the read itself fails (a sharing
                // violation right after another instance's move is a real Windows condition), our write still landed -
                // so swallow it rather than reporting "Couldn't save the switch" for a save that fully succeeded.
                try
                {
                    if (JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath))?.ActiveAccountId is var onDisk
                        && onDisk != ActiveAccountId)
                    {
                        if (_baseline is not null) _baseline.ActiveAccountId = onDisk;
                        verifyError = new InvalidOperationException(
                            $"settings.json came back with account '{onDisk ?? "(none)"}' instead of '{ActiveAccountId ?? "(none)"}' - another VibeCode window may have overwritten it.");
                    }
                }
                catch { /* unverifiable, not failed */ }
            }
        }
        catch (Exception ex)
        {
            // A throw after a successful Move can't unmake the write; only a pre-Move failure is a real save failure.
            if (!moved)
            {
                // The adopt already mutated memory (it has to, to be serialized). Announce it even on the failure path,
                // or this instance silently runs as a different account with nothing in the UI saying so.
                if (adopted) ActiveAccountAdopted?.Invoke();
                return ex;   // caller decides whether the user needs to know
            }
        }
        Changed?.Invoke();   // the write itself landed, even on the verify-mismatch path - subscribers must not miss it
        if (adopted) ActiveAccountAdopted?.Invoke();
        return verifyError;
    }

    /// <summary>Deliberately change which account new chats run under and persist it. Marks the change as this
    /// instance's own so the pre-write reconcile treats it as the newest choice instead of a stale overwrite.
    /// Returns null on success.</summary>
    public Exception? SetActiveAccount(string? id)
    {
        ActiveAccountId = id;
        return TrySave(activeAccountIsDeliberate: true);
    }

    /// <summary>Deliberately choose what mode new chats start in, and persist it as this instance's own newest intent.
    /// Two VibeCode builds share one settings.json with nothing stopping them from running at once, so a plain Save
    /// is not enough on either count: the write has to happen even when this process's copy already reads as the
    /// picked mode (the file may not), and the pre-write merge must not adopt the other window's older value on the
    /// way past. Skipping either one loses the click silently - the pill moves, and the next launch is back on Ask.</summary>
    public void SetDefaultMode(string mode)
    {
        DefaultMode = mode;
        TrySave(defaultModeIsDeliberate: true);
    }

    private AppSettings Clone() =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this))!;

    private static string Json(object? value) => JsonSerializer.Serialize(value);

    /// <summary>Every settable scalar on this type. Reflection rather than a hand-list so a future setting is covered
    /// by the merge the day it is added instead of silently reverting between windows.</summary>
    private static readonly PropertyInfo[] ScalarProperties = typeof(AppSettings)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && p.CanWrite && IsScalar(p.PropertyType))
        .ToArray();

    private static bool IsScalar(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal)
               || type == typeof(DateTime) || type == typeof(TimeSpan);
    }

    /// <summary>Three-way merge against the file before overwriting it. <see cref="Current"/> is a load-once static that
    /// rewrites the WHOLE document, so without this any save - toggling a background, a background token refresh - would
    /// republish this instance's stale copy of every OTHER setting and silently revert another window's work (or this
    /// window's own, when a second instance loaded the file first). The rule per field: if THIS instance changed it away
    /// from the baseline we keep ours; otherwise, if disk changed it, we adopt disk's. Returns true when the adopted
    /// field was <see cref="ActiveAccountId"/>, which the UI has to announce.</summary>
    private bool MergeWithDisk(bool activeAccountIsDeliberate, bool defaultModeIsDeliberate)
    {
        AppSettings? disk = null;
        try
        {
            if (File.Exists(FilePath))
                disk = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
        }
        catch { /* unreadable or half-written by another instance: keep what we have rather than lose it */ }
        if (disk is null) return false;
        disk.NormalizeBridgeState();
        disk.NormalizeRecentDirectories();

        var baseline = _baseline;
        var accountAdopted = false;

        foreach (var property in ScalarProperties)
        {
            var mine = property.GetValue(this);
            var theirs = property.GetValue(disk);
            if (Equals(mine, theirs)) continue;

            // A deliberate account switch is by definition the newest intent, so it never yields to disk.
            if (activeAccountIsDeliberate && property.Name == nameof(ActiveAccountId)) continue;
            // Same rule for the mode the user just picked from a pill. The generic test below cannot see it: a window
            // that launched while the file already said "bypassPermissions" holds it as BOTH its value and its
            // baseline, so re-picking bypass after another window wrote "auto" reads as "unchanged here, changed on
            // disk" and would adopt the other window's older answer over the click that just happened.
            if (defaultModeIsDeliberate && property.Name == nameof(DefaultMode)) continue;

            var wasMineChanged = baseline is not null && !Equals(mine, property.GetValue(baseline));
            if (wasMineChanged) continue;   // this instance owns the newer value

            property.SetValue(this, theirs);
            if (property.Name == nameof(ActiveAccountId)) accountAdopted = true;
        }

        // Ownership marks are purely additive - a union can never be wrong, and losing one hides a real chat.
        foreach (var id in disk.OwnedSessions) OwnedSessions.Add(id);
        // Tombstones are additive for the mirror-image reason: dropping one resurrects a chat the user deleted.
        foreach (var id in disk.DeletedSessions ?? []) DeletedSessions.Add(id);
        OwnedSessions.ExceptWith(DeletedSessions);   // deleted wins; the union above must not undo a deletion

        HiddenProjects = MergeCollection(HiddenProjects, disk.HiddenProjects, baseline?.HiddenProjects);
        Backgrounds = MergeCollection(Backgrounds, disk.Backgrounds, baseline?.Backgrounds);
        McpServers = MergeCollection(McpServers, disk.McpServers, baseline?.McpServers);
        OpenChats = MergeCollection(OpenChats, disk.OpenChats, baseline?.OpenChats);
        // Folder recency is timestamped and additive: unioning avoids one app window erasing another window's newest chat.
        RecentDirectories = RecentDirectoryHistory.NormalizeRemembered(
            (RecentDirectories ?? new List<RecentDirectoryState>())
            .Concat(disk.RecentDirectories ?? new List<RecentDirectoryState>()));
        SavedBridges = MergeSavedBridges(disk.SavedBridges);

        return accountAdopted;
    }

    /// <summary>Keep ours when this instance edited the list, otherwise take the file's newer copy.</summary>
    private static T MergeCollection<T>(T mine, T theirs, T? baseline) where T : class =>
        baseline is not null && Json(mine) != Json(baseline) ? mine : theirs;

    /// <summary>Bridges are unioned by host, never last-writer-wins: two windows each holding a different live bridge
    /// must both survive, and a dormant bridge belonging to neither must not be dropped just because it is unknown here.
    /// Only a bridge THIS instance deliberately removed stays removed.</summary>
    private List<SavedBridgeState> MergeSavedBridges(List<SavedBridgeState> theirs)
    {
        var merged = new Dictionary<string, SavedBridgeState>(StringComparer.OrdinalIgnoreCase);
        foreach (var bridge in theirs.Concat(SavedBridges))
        {
            if (string.IsNullOrWhiteSpace(bridge.HostSessionId)) continue;
            var key = BridgeKey(bridge.HostSessionId, bridge.Provider);
            if (_droppedBridgeKeys.Contains(key)) continue;
            // Same host seen twice (ours and the file's): the more recent snapshot has the fuller peer roster.
            if (!merged.TryGetValue(key, out var existing) || bridge.SavedAt > existing.SavedAt)
                merged[key] = bridge;
        }
        return merged.Values.OrderByDescending(x => x.SavedAt).Take(MaxSavedBridges).ToList();
    }

    /// <summary>Copies an image into the backgrounds folder so it survives the source moving; returns the stored path.</summary>
    public static string ImportBackground(string source)
    {
        Directory.CreateDirectory(BackgroundsDir);
        var dest = Path.Combine(BackgroundsDir, Path.GetFileName(source));
        if (File.Exists(dest) && !string.Equals(dest, source, StringComparison.OrdinalIgnoreCase))
            dest = Path.Combine(BackgroundsDir,
                $"{Path.GetFileNameWithoutExtension(source)}-{DateTime.Now:HHmmss}{Path.GetExtension(source)}");
        if (!string.Equals(dest, source, StringComparison.OrdinalIgnoreCase))
            File.Copy(source, dest, overwrite: false);
        return dest;
    }
}
