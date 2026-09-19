using System.Collections.ObjectModel;
using VibeCode.Services;

namespace VibeCode.UI;

/// <summary>What a session should run as. Deliberately three plain values rather than a ChatViewModel: the Demon
/// setup dialog runs BEFORE any session exists, and model + effort have to be decided by then — the CLI is handed
/// both at spawn time (see ChatViewModel.StartCore), so choosing them afterwards would mean restarting sixteen
/// processes to apply them.</summary>
public sealed record SessionSetup(string? Model, string? Effort, string Mode)
{
    /// <summary>Apply to a pane that has NOT started yet. Model and effort are direct property assignments on purpose:
    /// SetModel would push to a session that does not exist and would rewrite the app-wide default model as a side
    /// effect — sixteen times over, for a choice the user made about one team.
    /// <para>The mode does NOT get that treatment, because SetMode has neither problem: it pushes nothing while the
    /// pane has no session, and only writes the app-wide seed when asked to remember. Assigning the property instead
    /// set <c>_mode</c> and left <c>_userMode</c> pointing at the seed <see cref="MainViewModel.NewChat"/> had just
    /// applied — so when the provider's init event arrived a moment later and re-asserted the remembered choice, the
    /// orchestrator silently reverted to the seed while its workers (copied through SetMode) kept the dialog's pick.</para></summary>
    public void ApplyToUnstarted(ChatViewModel chat)
    {
        chat.SetMode(Mode);   // remember: false - ApplyToLive is where a deliberate pick becomes the seed
        chat.Model = Model;
        chat.Effort = Effort;
    }

    /// <summary>Apply to a pane that is already running, pushing each change down to its live session.</summary>
    public void ApplyToLive(ChatViewModel chat)
    {
        chat.SetMode(Mode, remember: true);   // an explicit answer to "what should this run as", same as SetModel below
        chat.Effort = Effort;    // set first: SetModel sends model and effort in one set_model call
        chat.SetModel(Model);
    }
}

/// <summary>What a whole TEAM should start as: how many sessions it has, which AI account signs them in, and what
/// they run as once they are up. Separate from <see cref="SessionSetup"/> because size and account are decided once
/// for the roster and can never be re-applied afterwards — a session authenticates at spawn, and a team is not
/// resized while it runs — whereas model/thinking/permission are also asked for one pane at a time from its ⋮ menu,
/// where neither is up for discussion.
/// <para><paramref name="SessionCount"/> counts the orchestrator, so a team of 4 is one orchestrator and three
/// workers. It defaults to a full team for the automation hook and any older caller.</para></summary>
public sealed record TeamSetup(string Provider, string? AccountId, string AccountLabel, SessionSetup Session,
    int SessionCount = DemonModePolicy.SessionCount);

/// <summary>One offer in the team-size row. A plain number rather than a slider: the range is short enough to show
/// whole, and picking "9" in one click cannot land on 8 the way dragging can.</summary>
public sealed class TeamSizeChoice : Observable
{
    private bool _isSelected;
    public required int Count { get; init; }
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}

/// <summary>
/// One AI account the team can run on, flattened out of the four per-provider account stores into one row so the
/// user picks an AI from a single list instead of choosing a provider first and an account second — the same shape
/// the app's own account manager uses.
/// </summary>
public sealed class TeamAccountRow : Observable
{
    private bool _isSelected;
    public required string Provider { get; init; }
    /// <summary>Null for a provider with a single shared CLI login (Kimi), which has no account store to pick from.</summary>
    public string? AccountId { get; init; }
    public required string Label { get; init; }
    public required string Sub { get; init; }
    public required string Initial { get; init; }
    /// <summary>False when the saved login is broken/signed out. Still listed — hiding it would look like the
    /// account had been deleted — but it says so, and starting sixteen sessions on it would fail sixteen times.</summary>
    public bool Usable { get; init; } = true;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    /// <summary>Every account VibeCode could sign a team in as. Kimi is included only when the caller can vouch for
    /// its shared login: its state comes from an async CLI probe that lives on the main view-model, and a dialog
    /// that guessed would offer a team on a CLI that is not installed.</summary>
    public static List<TeamAccountRow> Discover(bool includeKimi)
    {
        var rows = new List<TeamAccountRow>();
        foreach (var a in AccountService.Instance.List())
            rows.Add(new TeamAccountRow
            {
                Provider = "claude", AccountId = a.Id, Label = a.Label, Sub = a.ProviderLine,
                Initial = a.Initial, Usable = a.Usable,
            });
        foreach (var a in CodexAccountService.Instance.List())
            rows.Add(new TeamAccountRow
            {
                Provider = "codex", AccountId = a.Id, Label = a.Label, Sub = a.ProviderLine,
                Initial = a.Initial, Usable = a.Usable,
            });
        if (includeKimi)
            rows.Add(new TeamAccountRow
            {
                Provider = "kimi", AccountId = null, Label = "Kimi account", Sub = "Kimi Code · Connected",
                Initial = "K",
            });
        foreach (var a in GrokAccountService.Instance.List())
            rows.Add(new TeamAccountRow
            {
                Provider = "grok", AccountId = a.Id, Label = a.Label, Sub = a.ProviderLine,
                Initial = a.Initial, Usable = a.Usable,
            });
        return rows;
    }

    /// <summary>The account a provider would have used anyway, so opening the dialog and pressing Start changes
    /// nothing.</summary>
    public static string? ActiveIdFor(string provider) => ProviderModelCatalog.Normalize(provider) switch
    {
        "codex" => CodexAccountService.Instance.ActiveId,
        "grok" => GrokAccountService.Instance.ActiveId,
        "kimi" => null,
        _ => AccountService.Instance.ActiveId,
    };
}

/// <summary>One permission-mode row. The four modes are a fixed product decision rather than a catalog, so they are
/// listed rather than discovered — but listed ONCE, so this dialog and the composer pill cannot disagree.</summary>
public sealed class PermissionModeChoice : Observable
{
    private bool _isSelected;
    public required string Value { get; init; }
    public required string Label { get; init; }
    public required string Description { get; init; }
    public required string Glyph { get; init; }
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}

/// <summary>One model row. <see cref="ModelChoice"/> is a plain catalog record with no selection state, and the
/// dialog needs a check mark that moves.</summary>
public sealed class SetupModelRow : Observable
{
    private bool _isSelected;
    public required ModelChoice Model { get; init; }
    public string Value => Model.Value;
    public string Display => Model.Display;
    public string? Description => Model.Description;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}

/// <summary>
/// Backs the "what should these sessions run as" dialog: the AI account, then model, thinking level and permission
/// mode.
///
/// The account comes FIRST because everything under it depends on which AI is answering — the model list, the
/// thinking tiers, even whether thinking exists — so picking one rebuilds the rest. It is offered only when a whole
/// team is being started; a single pane's ⋮ menu reopens this dialog to change how that session runs, and a running
/// session cannot change the login it authenticated with.
///
/// The thinking list is REBUILT whenever the model changes, because effort tiers are per-model — Haiku has none, and
/// ultracode exists only where xhigh does. A fixed list would offer levels the chosen model cannot honour.
/// </summary>
public sealed class SessionSetupViewModel : Observable
{
    private bool _isClaude;
    private string _agentDisplay;
    private string _provider;

    /// <param name="accounts">The AI accounts to choose from, or null for the single-pane case where the account is
    /// already settled and must not be presented as a choice. Non-null also means "this is a team", which is what
    /// puts the size row on screen — a machine with no saved logins still gets to choose how big its team is.</param>
    /// <param name="sessionCount">How many sessions the team should start with, orchestrator included. Ignored for
    /// the single-pane case.</param>
    public SessionSetupViewModel(string provider, SessionSetup current, string agentDisplay,
        IReadOnlyList<TeamAccountRow>? accounts = null, string? accountId = null,
        int sessionCount = DemonModePolicy.SessionCount)
    {
        _provider = ProviderModelCatalog.Normalize(provider);
        _isClaude = _provider == "claude";
        _agentDisplay = agentDisplay;
        _mode = current.Mode;
        _effort = current.Effort;
        _accountId = accountId;
        _accountLabel = agentDisplay;

        IsTeam = accounts is not null;
        _sessionCount = DemonModePolicy.ClampSessionCount(sessionCount);
        if (IsTeam)
            for (var n = DemonModePolicy.MinimumSessionCount; n <= DemonModePolicy.MaximumSessionCount; n++)
                TeamSizes.Add(new TeamSizeChoice { Count = n });

        if (accounts is not null)
            foreach (var a in accounts) Accounts.Add(a);
        // Match the incoming provider+account to a row so the list opens on the account the team would have used
        // anyway. Falling back to the provider alone keeps a preselection when the id is unknown (Kimi has none).
        var preselected = Accounts.FirstOrDefault(
                              a => a.Provider == _provider
                                   && string.Equals(a.AccountId, accountId, StringComparison.OrdinalIgnoreCase))
                          ?? Accounts.FirstOrDefault(a => a.Provider == _provider);
        if (preselected is not null)
        {
            _accountId = preselected.AccountId;
            _accountLabel = preselected.Label;
        }

        RebuildModels(current.Model);
        RefreshSelections();
    }

    public ObservableCollection<TeamAccountRow> Accounts { get; } = new();
    public ObservableCollection<TeamSizeChoice> TeamSizes { get; } = new();
    public ObservableCollection<SetupModelRow> Models { get; } = new();
    public ObservableCollection<EffortChoice> Efforts { get; } = new();

    /// <summary>True when this dialog is standing a whole team up rather than reconfiguring one pane. Size is a
    /// team-only question: a running roster is never resized, so a pane's ⋮ menu must not offer one.</summary>
    public bool IsTeam { get; }

    /// <summary>False for the single-pane case, and for a team on a machine with no saved logins at all — where
    /// offering an empty list would read as a broken dialog rather than "sign in first".</summary>
    public bool HasAccounts => Accounts.Count > 0;

    private int _sessionCount;
    /// <summary>Sessions in the team, orchestrator included.</summary>
    public int SessionCount
    {
        get => _sessionCount;
        set
        {
            if (!Set(ref _sessionCount, DemonModePolicy.ClampSessionCount(value))) return;
            RefreshSelections();
            Raise(nameof(TeamSizeNote));
            Raise(nameof(Summary));
        }
    }

    /// <summary>The line under the size row. Spells the split out — the number on the chip counts the orchestrator,
    /// and "17" meaning sixteen workers is exactly the kind of off-by-one a user should not have to infer — and says
    /// plainly what a bigger team costs, since every session is another CLI process on one account's quota.</summary>
    public string TeamSizeNote => $"1 orchestrator + {_sessionCount - 1} worker" + (_sessionCount == 2 ? "" : "s") +
                                  ". Each one is a separate CLI process signed in as the same account, so a bigger " +
                                  "team means more parallel lanes, more memory, and quota spent faster.";

    private string? _accountId;
    private string _accountLabel;

    /// <summary>Point the whole dialog at another AI. The model list, its thinking tiers and the default model all
    /// belong to the provider, so they are rebuilt rather than carried across — a Claude model id means nothing to
    /// Codex, and handing one over is how a team spawns sixteen sessions on a model that does not exist.</summary>
    public void SelectAccount(TeamAccountRow account)
    {
        var provider = ProviderModelCatalog.Normalize(account.Provider);
        var providerChanged = provider != _provider;
        _provider = provider;
        _isClaude = provider == "claude";
        _accountId = account.AccountId;
        _accountLabel = account.Label;
        if (providerChanged)
        {
            _agentDisplay = ProviderModelCatalog.DisplayName(provider);
            _effort = DefaultEffortFor(provider);
            RebuildModels(DefaultModelFor(provider));
        }
        RefreshSelections();
        Raise(nameof(Summary));
    }

    /// <summary>The remembered model/thinking level for a provider — what a new chat on it would start as.</summary>
    public static string? DefaultModelFor(string provider) => ProviderModelCatalog.Normalize(provider) switch
    {
        "codex" => AppSettings.Current.DefaultCodexModel,
        "kimi" => AppSettings.Current.DefaultKimiModel,
        "grok" => AppSettings.Current.DefaultGrokModel,
        _ => AppSettings.Current.DefaultModel,
    };

    public static string? DefaultEffortFor(string provider) => ProviderModelCatalog.Normalize(provider) switch
    {
        "codex" => AppSettings.Current.DefaultCodexEffort,
        "kimi" => AppSettings.Current.DefaultKimiEffort,
        "grok" => AppSettings.Current.DefaultGrokEffort,
        _ => AppSettings.Current.DefaultEffort,
    };

    private void RebuildModels(string? preferred)
    {
        Models.Clear();
        foreach (var m in ProviderModelCatalog.For(_provider)) Models.Add(new SetupModelRow { Model = m });
        // The remembered model may not belong to this provider's list; fall back to whatever it offers first.
        _model = Models.Any(m => string.Equals(m.Value, preferred, StringComparison.OrdinalIgnoreCase))
            ? preferred
            : Models.FirstOrDefault()?.Value;
        RebuildEfforts();
        Raise(nameof(HasModels));
        Raise(nameof(Model));
    }

    public IReadOnlyList<PermissionModeChoice> PermissionModes { get; } = new[]
    {
        new PermissionModeChoice { Value = "default", Glyph = "", Label = "Ask before acting",
            Description = "Every edit and command waits for you" },
        new PermissionModeChoice { Value = "auto", Glyph = "", Label = "Auto",
            Description = "Edits and commands run; only dangerous ones ask" },
        new PermissionModeChoice { Value = "plan", Glyph = "", Label = "Plan mode",
            Description = "Reads and plans, changes nothing" },
        new PermissionModeChoice { Value = "bypassPermissions", Glyph = "", Label = "Bypass permissions",
            Description = "Nothing is ever asked — fastest, and the one that can do damage" },
    };

    /// <summary>False when this provider's catalog has never been seen (nothing has run yet), so there is nothing
    /// honest to offer. The dialog says so rather than showing an empty list that reads as a bug.</summary>
    public bool HasModels => Models.Count > 0;
    public bool HasEfforts => Efforts.Count > 0;
    public string EffortEmptyText => HasModels
        ? "This model has no thinking levels — it always runs the same way."
        : "No model catalog yet. Start any chat once and VibeCode will remember what this provider offers.";

    private string? _model;
    public string? Model
    {
        get => _model;
        set
        {
            if (!Set(ref _model, value)) return;
            RebuildEfforts();
            RefreshSelections();
            Raise(nameof(Summary));
        }
    }

    private string? _effort;
    public string? Effort
    {
        get => _effort;
        set { if (Set(ref _effort, value)) { RefreshSelections(); Raise(nameof(Summary)); } }
    }

    private string _mode;
    public string Mode
    {
        get => _mode;
        set { if (Set(ref _mode, value)) { RefreshSelections(); Raise(nameof(Summary)); } }
    }

    /// <summary>One line restating the whole choice, so confirming is never a leap of faith.</summary>
    public string Summary
    {
        get
        {
            var model = Models.FirstOrDefault(m => string.Equals(m.Value, _model, StringComparison.OrdinalIgnoreCase));
            var effort = Efforts.FirstOrDefault(e => string.Equals(e.Value, _effort, StringComparison.OrdinalIgnoreCase));
            var mode = PermissionModes.FirstOrDefault(p => p.Value == _mode);
            var parts = new List<string>();
            if (IsTeam) parts.Add($"{_sessionCount} sessions");
            if (HasAccounts) parts.Add(_accountLabel);
            parts.Add(model?.Display ?? "default model");
            if (effort is not null) parts.Add(effort.Label.ToLowerInvariant() + " thinking");
            if (mode is not null) parts.Add(mode.Label.ToLowerInvariant());
            return string.Join("  ·  ", parts);
        }
    }

    /// <summary>The choice, with the effort suppressed when the selected model has no thinking tiers at all.
    /// <para>
    /// The remembered level is kept in <see cref="Effort"/> on purpose — picking Haiku and changing your mind should
    /// bring "X-High" back rather than silently reset you to Auto — but it must not be HANDED to a model that has no
    /// such level. This mirrors what SetModel does for a live pane (<c>HasEffort ? _effort : null</c>); without it a
    /// Haiku team spawned with an effort argument its CLI never advertised.
    /// </para></summary>
    public SessionSetup Result => new(_model, HasEfforts ? _effort : null, _mode);

    /// <summary>The team answer: how many sessions, the chosen account, and everything those sessions run as.</summary>
    public TeamSetup TeamResult => new(_provider, _accountId, _accountLabel, Result, _sessionCount);

    private void RebuildEfforts()
    {
        var row = Models.FirstOrDefault(m => string.Equals(m.Value, _model, StringComparison.OrdinalIgnoreCase));
        Efforts.Clear();
        foreach (var e in ChatViewModel.EffortChoicesFor(row?.Model, _isClaude, _effort, _agentDisplay)) Efforts.Add(e);
        // Same rule the live picker applies: an effort this model does not offer falls back to Auto rather than
        // being sent anyway and silently ignored.
        if (Efforts.Count > 0 && _effort is not null
            && !Efforts.Any(e => string.Equals(e.Value, _effort, StringComparison.OrdinalIgnoreCase)))
            _effort = null;
        Raise(nameof(HasEfforts));
        Raise(nameof(EffortEmptyText));
    }

    private void RefreshSelections()
    {
        foreach (var t in TeamSizes) t.IsSelected = t.Count == _sessionCount;
        foreach (var a in Accounts)
            a.IsSelected = a.Provider == _provider
                           && string.Equals(a.AccountId, _accountId, StringComparison.OrdinalIgnoreCase);
        foreach (var m in Models) m.IsSelected = string.Equals(m.Value, _model, StringComparison.OrdinalIgnoreCase);
        foreach (var e in Efforts) e.IsSelected = string.Equals(e.Value, _effort, StringComparison.OrdinalIgnoreCase);
        foreach (var p in PermissionModes) p.IsSelected = p.Value == _mode;
    }
}
