using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows.Data;
using VibeCode.Services;

namespace VibeCode.UI;

/// <summary>One row in the unified account list for Kimi's single shared CLI login. Kimi has no per-account store,
/// so this is a lightweight snapshot rebuilt whenever Kimi state changes; the merged list renders it beside the
/// Claude/Codex/Grok account objects with its own "Kimi" badge. Property names mirror the other account types
/// (Label / ProviderLine / UsageDisplay / Initial / IsVibeCodeSelected) so one row template and the shared
/// filter/sort code treat every provider the same.</summary>
public sealed class KimiAccountEntry
{
    public string Label { get; init; } = "";
    public string ProviderLine { get; init; } = "";
    public string UsageDisplay { get; init; } = "";
    public string Initial { get; init; } = "K";
    public bool IsVibeCodeSelected { get; init; }
    public bool Usable { get; init; }
}

/// <summary>An ObservableCollection with one-reset roster replacement for fast Bridge host switching.</summary>
public sealed class BridgePaneCollection : ObservableCollection<ChatViewModel>
{
    public BridgePaneCollection() { }
    public BridgePaneCollection(IEnumerable<ChatViewModel> panes) : base(panes.ToList()) { }

    public void ReplaceAll(IEnumerable<ChatViewModel> panes)
    {
        var replacement = panes.ToList();
        Items.Clear();
        foreach (var pane in replacement) Items.Add(pane);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

public sealed class ProjectVm : Observable
{
    private bool _open;
    public required string Cwd { get; init; }
    public required string Name { get; init; }
    public required DateTime LastModified { get; init; }
    public ObservableCollection<SessionEntry> Sessions { get; } = new();
    public bool Open { get => _open; set => Set(ref _open, value); }
    public string CountText => Sessions.Count.ToString();
}

/// <summary>A dormant bridge on the home screen: enough to describe it, plus the snapshot needed to resume it.</summary>
public sealed class SavedBridgeVm
{
    public SavedBridgeVm(SavedBridgeState state) => State = state;

    public SavedBridgeState State { get; }
    public DateTime SavedAt => State.SavedAt;
    public string Project => Path.GetFileName(State.Cwd.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } name
        ? name
        : State.Cwd;

    public string Title => string.IsNullOrWhiteSpace(State.HostTitle) ? $"{Project} bridge" : State.HostTitle!;

    /// <summary>The host counts as an agent too - the roster the user saw was peers + 1.</summary>
    public int AgentCount => State.Peers.Count(p => !string.IsNullOrWhiteSpace(p.SessionId)) + 1;

    public string Detail => $"{AgentCount} agents · {Project}";
}

public sealed partial class MainViewModel : Observable
{
    public ObservableCollection<ChatViewModel> Chats { get; } = new();
    /// <summary>The sidebar's grouped projection: pinned chats first, then regular chats, without duplicating state.</summary>
    public ICollectionView ChatGroups { get; }
    public ObservableCollection<ProjectVm> Projects { get; } = new();
    public ObservableCollection<ProjectVm> RecentProjects { get; } = new();   // home-screen chips, five newest usable folders
    public ObservableCollection<SessionEntry> RecentSessions { get; } = new();

    public MainViewModel()
    {
        ChatListDragBehavior.Register();
        var chats = new ListCollectionView(Chats);
        chats.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ChatViewModel.SidebarSection)));
        // Chats are NOT hidden by account any more: switching logins used to empty the sidebar, which reads as
        // "it forgot all my chats" even though nothing was ever deleted. Every row stays, carries a chip naming its
        // login, and keeps running under that login. Per-account workspaces are opt-in via IsolateChatsByAccount.
        chats.Filter = ChatVisibleInSidebar;
        ChatGroups = chats;
        _sidebarCollapsed = AppSettings.Current.SidebarCollapsed;   // field, not property: restoring is not a change
        // Quota reads land on the dispatcher after each fetch; the account rows mirror them without polling.
        KimiUsageService.Instance.PropertyChanged += OnKimiUsageChanged;
    }

    private void OnKimiUsageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(KimiUsageService.Summary) or nameof(KimiUsageService.Status)
            or nameof(KimiUsageService.UpdatedText))) return;
        Raise(nameof(KimiAccountUsage));
        RebuildAllAccounts();   // the merged roster snapshots UsageDisplay, so it must be re-taken
    }

    /// <summary>
    /// True when <paramref name="item"/> belongs to the currently selected login for its provider.
    /// Kimi has a single shared login (always shown). Legacy rows with no AccountId stay visible so old
    /// restores aren't orphaned; every new chat captures ActiveId at creation.
    /// </summary>
    private bool ChatMatchesActiveAccount(object item)
    {
        if (item is not ChatViewModel chat) return false;
        if (string.IsNullOrWhiteSpace(chat.AccountId)) return true;   // pre-isolation snapshots
        if (chat.IsKimi) return true;
        if (chat.IsClaude) return Visible(chat.AccountId, AccountService.Instance.ActiveId, Accounts.Select(a => a.Id));
        if (chat.IsCodex) return Visible(chat.AccountId, CodexAccountService.Instance.ActiveId, CodexAccounts.Select(a => a.Id));
        if (chat.IsGrok) return Visible(chat.AccountId, GrokAccountService.Instance.ActiveId, GrokAccounts.Select(a => a.Id));
        return true;

        // "Belongs to another login" is only a sane reason to hide a chat while that other login still EXISTS -
        // switching back is what makes it reachable again. Once the account is removed there is nothing to switch
        // back to, so the same rule silently hid those chats forever, in a sidebar that offered no way to see them.
        // An orphan is shown instead: it is the user's conversation, and this is the only surface that lists it.
        static bool Visible(string? chatAccount, string? activeAccount, IEnumerable<string> knownAccounts)
        {
            if (activeAccount is null) return true;                                    // no selection: show everything
            if (string.Equals(chatAccount, activeAccount, StringComparison.OrdinalIgnoreCase)) return true;
            return !knownAccounts.Any(id => string.Equals(id, chatAccount, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>The sidebar's actual predicate. Account isolation is opt-in, so by default a chat NEVER leaves the
    /// sidebar because the user switched logins - the row carries an account chip instead, and the chat still spawns
    /// under its own account (see <see cref="ChatViewModel"/>'s per-provider config dir).</summary>
    private bool ChatVisibleInSidebar(object item)
    {
        if (item is not ChatViewModel chat) return false;
        return !AppSettings.Current.IsolateChatsByAccount || ChatMatchesActiveAccount(chat);
    }

    /// <summary>Re-apply the sidebar filter after a Claude/Codex/Grok switch. With isolation off this is a pure
    /// refresh (the rows all stay and the focused chat is left alone); with isolation on it also re-homes the
    /// selection when the focused chat belongs to a different login.</summary>
    public void RefreshChatAccountFilter()
    {
        ChatGroups.Refresh();
        // If the focused chat is now hidden, pick another visible one (or home) so the UI matches the filter.
        if (ActiveChat is { } active && !ChatVisibleInSidebar(active))
        {
            if (ShowBridge) HideBridge();
            ActiveChat = FirstVisibleChat(preferNot: null);
        }
        if (SecondaryActiveChat is { } secondary && !ChatVisibleInSidebar(secondary))
            SecondaryActiveChat = FirstVisibleChat(preferNot: ActiveChat) ?? ActiveChat;
    }

    private ChatViewModel? FirstVisibleChat(ChatViewModel? preferNot)
    {
        foreach (var chat in Chats)
        {
            if (preferNot is not null && ReferenceEquals(chat, preferNot)) continue;
            if (ChatVisibleInSidebar(chat)) return chat;
        }
        return null;
    }

    private ChatViewModel? _activeChat;
    public ChatViewModel? ActiveChat
    {
        get => _activeChat;
        set { if (Set(ref _activeChat, value)) { Raise(nameof(ShowHome)); Raise(nameof(PanelChat)); RequestSave(); } }
    }
    public bool ShowHome => _activeChat is null;

    private ChatViewModel? _secondaryActiveChat;
    /// <summary>The independently selected chat in the optional full-shell second-monitor window.</summary>
    public ChatViewModel? SecondaryActiveChat
    {
        get => _secondaryActiveChat;
        set
        {
            if (!Set(ref _secondaryActiveChat, value)) return;
            Raise(nameof(SecondaryShowHome));
            RequestSave();
        }
    }

    private bool _secondaryShowBridge;
    /// <summary>Whether the second full shell is showing its assigned Bridge panes instead of its selected chat.</summary>
    public bool SecondaryShowBridge
    {
        get => _secondaryShowBridge;
        set
        {
            if (!Set(ref _secondaryShowBridge, value)) return;
            Raise(nameof(SecondaryShowHome));
            RequestSave();
        }
    }

    public bool SecondaryShowHome => _secondaryActiveChat is null && !_secondaryShowBridge;

    private bool _sidebarCollapsed;
    /// <summary>Full-screen focus: hide the left sidebar (chats + projects) so the chat gets the whole width.</summary>
    public bool SidebarCollapsed
    {
        get => _sidebarCollapsed;
        set { if (Set(ref _sidebarCollapsed, value)) RequestSave(); }
    }

    public string DefaultCwd { get; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Path to prefill in the new-chat folder box: last used project if it still exists, else the user profile.</summary>
    public string PreferredNewChatCwd =>
        AppSettings.Current.MostRecentExistingDirectory() is { } recent ? recent : DefaultCwd;

    public int HiddenCount => AppSettings.Current.HiddenProjects.Count;

    // ================= Accounts (Claude profiles + OpenAI + shared Kimi/Grok CLI logins) =================
    public ObservableCollection<AccountInfo> Accounts { get; } = new();
    public ObservableCollection<CodexAccountInfo> CodexAccounts { get; } = new();
    public ObservableCollection<GrokAccountInfo> GrokAccounts { get; } = new();
    /// <summary>The single unified roster the account manager shows: every saved Claude, Codex and Grok login plus
    /// Kimi's shared CLI row, mixed into one list so the user picks any AI from one place instead of switching
    /// provider "categories". Rebuilt by <see cref="RebuildAllAccounts"/> whenever any provider's accounts change.</summary>
    public ObservableCollection<object> AllAccounts { get; } = new();
    public CodexAccountInfo? CurrentCodexAccount => CodexAccounts.FirstOrDefault(x => x.IsCurrent)
                                                   ?? CodexAccounts.FirstOrDefault(x => x.Usable);
    public GrokAccountInfo? CurrentGrokAccount => GrokAccounts.FirstOrDefault(x => x.IsCurrent)
                                                 ?? GrokAccounts.FirstOrDefault(x => x.Usable);
    private AccountInfo? _currentAccount;
    public AccountInfo? CurrentAccount
    {
        get => _currentAccount;
        private set { if (Set(ref _currentAccount, value)) { Raise(nameof(AccountInitial)); Raise(nameof(AccountLabel)); Raise(nameof(AccountSub)); Raise(nameof(IsSignedIn)); Raise(nameof(HasMultipleAccounts)); } }
    }
    private bool CodexProviderSelected => string.Equals(AppSettings.Current.DefaultProvider, "codex", StringComparison.OrdinalIgnoreCase);
    private bool KimiProviderSelected => string.Equals(AppSettings.Current.DefaultProvider, "kimi", StringComparison.OrdinalIgnoreCase);
    private bool GrokProviderSelected => string.Equals(AppSettings.Current.DefaultProvider, "grok", StringComparison.OrdinalIgnoreCase);
    public string AccountInitial => CodexProviderSelected
        ? CodexAccountInitial
        : KimiProviderSelected ? KimiAccountInitial : GrokProviderSelected ? GrokAccountInitial : _currentAccount?.Initial ?? "?";
    public string AccountLabel => CodexProviderSelected
        ? CodexAccountLabel
        : KimiProviderSelected ? KimiAccountLabel : GrokProviderSelected ? GrokAccountLabel : _currentAccount?.Label ?? "Sign in";
    public string AccountSub => CodexProviderSelected
        ? CodexAccountSub
        : KimiProviderSelected
            ? KimiAccountSub
            : GrokProviderSelected
                ? GrokAccountSub
            : _currentAccount is { } a
                ? a.ProviderLine
                : "Claude Code · Not signed in";
    public bool IsSignedIn => CodexProviderSelected
        ? IsCodexSignedIn
        : KimiProviderSelected ? IsKimiSignedIn : GrokProviderSelected ? IsGrokSignedIn : _currentAccount is not null;
    public bool HasMultipleAccounts => Accounts.Count > 1;
    /// <summary>True when the shown account is just the live ~/.claude login because no selection was stored - i.e. a
    /// guess, not the user's choice. Shown as a hint so a lost selection can't pass for correct state.</summary>
    public bool AccountIsFallback => !CodexProviderSelected && !KimiProviderSelected && !GrokProviderSelected && AccountService.Instance.ActiveIsFallback;

    private string? _codexAccountEmail;
    private string? _codexAccountName;
    private string? _codexAccountPlan;
    private int? _codexSessionPercent;
    private int? _codexWeekPercent;
    private bool _isCodexSignedIn;
    public bool IsCodexSignedIn => _isCodexSignedIn;
    public bool IsCodexSelected => CodexProviderSelected && IsCodexSignedIn;
    public string CodexAccountLabel => IsCodexSignedIn
        ? !string.IsNullOrWhiteSpace(_codexAccountName) && !LooksLikeEmail(_codexAccountName)
            ? _codexAccountName.Trim()
            : CurrentCodexAccount?.Label ?? "OpenAI account"
        : "Sign in with OpenAI";
    public string CodexAccountSub => IsCodexSignedIn
        ? $"OpenAI Codex · {FormatPlan(_codexAccountPlan)}"
        : "OpenAI Codex · Not signed in";
    public string CodexAccountEmail => IsCodexSignedIn && !string.IsNullOrWhiteSpace(_codexAccountEmail)
                                      && !AppSettings.Current.HideEmails
        ? _codexAccountEmail
        : "";
    public string CodexAccountUsage
    {
        get
        {
            var parts = new List<string>();
            if (_codexSessionPercent is { } session) parts.Add($"{session}% session");
            if (_codexWeekPercent is { } week) parts.Add($"{week}% week");
            return string.Join(" · ", parts) + (parts.Count > 0 && CodexAtLimit ? " · at limit" : "");
        }
    }
    public bool HasCodexAccountUsage => _codexSessionPercent is not null || _codexWeekPercent is not null;
    public bool CodexAtLimit => _codexSessionPercent >= 100 || _codexWeekPercent >= 100;
    public string CodexAccountInitial => IsCodexSignedIn
        ? (!string.IsNullOrWhiteSpace(_codexAccountName) && !LooksLikeEmail(_codexAccountName) ? _codexAccountName : "O") is { Length: > 0 } identity
            ? identity[..1].ToUpperInvariant()
            : "O"
        : "O";

    private bool _isKimiInstalled;
    private bool _isKimiSignedIn;
    private string? _kimiVersion;
    public bool IsKimiInstalled => _isKimiInstalled;
    public bool IsKimiSignedIn => _isKimiSignedIn;
    public bool IsKimiSelected => KimiProviderSelected && IsKimiSignedIn;
    public string KimiAccountLabel => !IsKimiInstalled
        ? "Install Kimi Code CLI"
        : IsKimiSignedIn ? "Kimi account" : "Sign in to Kimi";
    public string KimiAccountSub
    {
        get
        {
            return !IsKimiInstalled
                ? "Kimi Code · Not installed"
                : IsKimiSignedIn ? "Kimi Code · Connected" : "Kimi Code · Not signed in";
        }
    }
    /// <summary>Live quota from <see cref="KimiUsageService"/> ("12% quota · 3% 5h"), with the nearest reset when
    /// Kimi reports one. Falls back to the service's status line ("Loading Kimi quota…", token-expired note, …)
    /// until the first successful read.</summary>
    public string KimiAccountUsage
    {
        get
        {
            if (!IsKimiSignedIn) return "";
            var usage = KimiUsageService.Instance;
            if (!usage.HasData) return usage.Status;
            var reset = usage.Limits.FirstOrDefault(limit => limit.HasReset);
            return reset is null ? usage.Summary : $"{usage.Summary} · resets {reset.ResetDisplay}";
        }
    }
    public string KimiAccountInitial => "K";

    public void ApplyKimiAccount(KimiAccountState state)
    {
        // Preserve a known-good row through transient process/network failures. Missing and signed-out states with
        // no Error are authoritative and update immediately. A failed ACP handshake still proves that an executable
        // was found, so retain that installation fact without incorrectly flipping a connected account to signed out.
        if (!string.IsNullOrWhiteSpace(state.Error))
        {
            if (state.IsInstalled && !_isKimiInstalled)
            {
                _isKimiInstalled = true;
                Raise(nameof(IsKimiInstalled));
                Raise(nameof(KimiAccountLabel));
                Raise(nameof(KimiAccountSub));
                RefreshProviderPresentation();
            }
            return;
        }
        _isKimiInstalled = state.IsInstalled;
        _isKimiSignedIn = state.IsSignedIn;
        _kimiVersion = state.Version;
        Raise(nameof(IsKimiInstalled));
        Raise(nameof(IsKimiSignedIn));
        Raise(nameof(IsKimiSelected));
        Raise(nameof(KimiAccountLabel));
        Raise(nameof(KimiAccountSub));
        Raise(nameof(KimiAccountUsage));
        Raise(nameof(KimiAccountInitial));
        RefreshProviderPresentation();
    }

    private bool _isGrokInstalled;
    private bool _isGrokSignedIn;
    private string? _grokVersion;
    public bool IsGrokInstalled => _isGrokInstalled;
    public bool IsGrokSignedIn => _isGrokSignedIn;
    public bool IsGrokSelected => GrokProviderSelected && IsGrokSignedIn;
    public string GrokAccountLabel => !IsGrokInstalled
        ? "Build or install Grok CLI"
        : CurrentGrokAccount?.Label ?? "Sign in to Grok";
    public string GrokAccountSub => !IsGrokInstalled
        ? "Grok · Not installed"
        : CurrentGrokAccount?.ProviderLine ?? "Grok · Not signed in";
    public string GrokAccountUsage => CurrentGrokAccount?.UsageDisplay ?? "";
    public string GrokAccountInitial => CurrentGrokAccount?.Initial ?? "G";

    public void ApplyGrokAccount(GrokAccountState state)
    {
        if (!string.IsNullOrWhiteSpace(state.Error))
        {
            if (state.IsInstalled && !_isGrokInstalled)
            {
                _isGrokInstalled = true;
                Raise(nameof(IsGrokInstalled));
                Raise(nameof(GrokAccountLabel));
                Raise(nameof(GrokAccountSub));
                RefreshProviderPresentation();
            }
            return;
        }
        _isGrokInstalled = state.IsInstalled;
        _isGrokSignedIn = state.IsSignedIn;
        _grokVersion = state.Version;
        Raise(nameof(IsGrokInstalled));
        Raise(nameof(IsGrokSignedIn));
        Raise(nameof(IsGrokSelected));
        Raise(nameof(GrokAccountLabel));
        Raise(nameof(GrokAccountSub));
        Raise(nameof(GrokAccountUsage));
        Raise(nameof(GrokAccountInitial));
        RefreshProviderPresentation();
    }

    public void ApplyGrokAccounts(IEnumerable<GrokAccountInfo> accounts)
    {
        GrokAccounts.Clear();
        foreach (var account in accounts) GrokAccounts.Add(account);
        _isGrokSignedIn = CurrentGrokAccount?.Usable == true;
        Raise(nameof(GrokAccounts));
        Raise(nameof(CurrentGrokAccount));
        Raise(nameof(IsGrokSignedIn));
        RefreshGrokAccountPresentation();
        RefreshChatAccountFilter();   // re-chip every row for the new Grok login (only isolation mode hides any)
    }

    public void RefreshGrokAccountPresentation()
    {
        foreach (var account in GrokAccounts) account.RefreshProviderSelection();
        Raise(nameof(GrokAccountLabel));
        Raise(nameof(GrokAccountSub));
        Raise(nameof(GrokAccountUsage));
        Raise(nameof(GrokAccountInitial));
        RefreshProviderPresentation();
    }

    public void ApplyCodexAccount(CodexAccountState state)
    {
        // A transient offline/CLI error must not flash a valid account to "signed out". Only an authoritative
        // account/read response changes the visible state; the next background tick will retry failures.
        if (!string.IsNullOrWhiteSpace(state.Error)) return;
        _isCodexSignedIn = state.IsSignedIn;
        _codexAccountName = state.Name;
        _codexAccountEmail = state.Email;
        _codexAccountPlan = state.Plan;
        _codexSessionPercent = state.SessionPercent;
        _codexWeekPercent = state.WeekPercent;
        Raise(nameof(IsCodexSignedIn));
        Raise(nameof(CodexAccountLabel));
        Raise(nameof(CodexAccountSub));
        Raise(nameof(CodexAccountEmail));
        Raise(nameof(CodexAccountUsage));
        Raise(nameof(HasCodexAccountUsage));
        Raise(nameof(CodexAtLimit));
        Raise(nameof(CodexAccountInitial));
        RefreshProviderPresentation();
    }

    /// <summary>Replace the OpenAI rows from the durable per-account store and project the selected row into the
    /// compact footer properties retained for the rest of the UI.</summary>
    public void ApplyCodexAccounts(IEnumerable<CodexAccountInfo> accounts)
    {
        CodexAccounts.Clear();
        foreach (var account in accounts) CodexAccounts.Add(account);
        var current = CodexAccounts.FirstOrDefault(x => x.IsCurrent)
                      ?? CodexAccounts.FirstOrDefault(x => x.Usable);
        _isCodexSignedIn = current?.Usable == true;
        _codexAccountName = current?.Name;
        _codexAccountEmail = current?.Email;
        _codexAccountPlan = current?.Plan;
        _codexSessionPercent = current?.SessionPercent;
        _codexWeekPercent = current?.WeekPercent;
        Raise(nameof(CodexAccounts));
        Raise(nameof(CurrentCodexAccount));
        Raise(nameof(IsCodexSignedIn));
        RefreshCodexAccountPresentation();
        RefreshChatAccountFilter();   // re-chip every row for the new Codex login (only isolation mode hides any)
    }

    public void RefreshCodexAccountPresentation()
    {
        foreach (var account in CodexAccounts) account.RefreshProviderSelection();
        Raise(nameof(CodexAccountLabel));
        Raise(nameof(CodexAccountSub));
        Raise(nameof(CodexAccountEmail));
        Raise(nameof(CodexAccountUsage));
        Raise(nameof(HasCodexAccountUsage));
        Raise(nameof(CodexAtLimit));
        Raise(nameof(CodexAccountInitial));
        RefreshProviderPresentation();
    }

    /// <summary>Re-render the one active provider across the footer and every row's single checkmark.</summary>
    public void RefreshProviderPresentation()
    {
        foreach (var account in Accounts) account.RefreshProviderSelection();
        foreach (var account in CodexAccounts) account.RefreshProviderSelection();
        foreach (var account in GrokAccounts) account.RefreshProviderSelection();
        // Account/provider selection is presentation state for already-open chats. Their pills and processes remain
        // session-owned; only the rows revealed by opening the model popup follow the newly selected provider.
        foreach (var chat in Chats.Concat(LiveBridgePeers).Distinct())
            chat.RefreshModelPicker(AppSettings.Current.DefaultProvider);
        Raise(nameof(IsCodexSelected));
        Raise(nameof(IsKimiSelected));
        Raise(nameof(IsGrokSelected));
        Raise(nameof(AccountInitial));
        Raise(nameof(AccountLabel));
        Raise(nameof(AccountSub));
        Raise(nameof(IsSignedIn));
        Raise(nameof(AccountIsFallback));
        RebuildAllAccounts();   // keep the merged account-manager roster in step with every provider's rows
    }

    /// <summary>Rebuild the merged <see cref="AllAccounts"/> roster from the per-provider collections plus Kimi's
    /// single shared login. Ordered Claude → Codex → Kimi → Grok; the account manager re-sorts and filters this
    /// view, so ordering here only sets a stable default. Cheap (a handful of rows) and idempotent.</summary>
    public void RebuildAllAccounts()
    {
        AllAccounts.Clear();
        foreach (var a in Accounts) AllAccounts.Add(a);
        foreach (var a in CodexAccounts) AllAccounts.Add(a);
        AllAccounts.Add(new KimiAccountEntry
        {
            Label = KimiAccountLabel,
            ProviderLine = KimiAccountSub,
            UsageDisplay = KimiAccountUsage,
            Initial = KimiAccountInitial,
            IsVibeCodeSelected = IsKimiSelected,
            Usable = IsKimiInstalled && IsKimiSignedIn,
        });
        foreach (var a in GrokAccounts) AllAccounts.Add(a);
    }

    private static string FormatPlan(string? plan)
    {
        var value = plan?.Trim();
        return string.IsNullOrWhiteSpace(value)
            ? "Subscription"
            : char.ToUpperInvariant(value[0]) + value[1..];
    }

    private static bool LooksLikeEmail(string value)
    {
        var at = value.IndexOf('@');
        return at > 0 && at < value.Length - 1;
    }

    /// <summary>Reload the account list and snapshot the live login so it's always switch-back-able (non-destructive).</summary>
    public void RefreshAccounts()
    {
        AccountService.Instance.SaveCurrent();   // keep the live account preserved in the store
        var list = AccountService.Instance.List();
        Accounts.Clear();
        foreach (var a in list) Accounts.Add(a);
        CurrentAccount = list.FirstOrDefault(a => a.IsCurrent);
        Raise(nameof(HasMultipleAccounts));
        Raise(nameof(AccountIsFallback));
        RefreshCodexAccountPresentation();
        AccountService.NotifyAccountsChanged();   // open chats re-resolve their "running as …" chip
        RefreshChatAccountFilter();   // re-chip every row for the new Claude login (only isolation mode hides any)
    }

    /// <summary>Switch the live login to a saved account (the current one is preserved first, so it stays logged in).
    /// Returns the outcome so the UI can warn if the target's saved login is broken instead of logging the user out.</summary>
    public SwitchOutcome SwitchAccount(AccountInfo a)
    {
        // No early-out for the already-current row: IsCurrent can be a FALLBACK derived from the live ~/.claude login
        // rather than a stored choice, so skipping the write there turned a real switch request into a silent no-op.
        // SelectAccount is idempotent and cheap, so re-running it just re-confirms the preference on disk.
        // Just change which account NEW chats run under - no file swap, so running chats (each already carries its own
        // OAuth token) can't be corrupted. This is the account-switching-corrupts-login fix.
        var before = AccountService.Instance.ActiveId;
        var outcome = AccountService.Instance.SelectAccount(a.Id);
        RefreshAccounts();
        // Tie the forced usage refresh to what the app is ACTUALLY running as now: a failed save rolls the in-memory
        // selection back, so re-probing then would just re-fetch the numbers already on screen.
        if (AccountService.Instance.ActiveId != before) UsageService.Instance.Refresh(force: true);
        return outcome;
    }

    /// <summary>Remove a saved profile. Returns the outcome so the UI can surface a settings-save failure the same way
    /// the switch path does - on <see cref="SwitchOutcome.SaveFailed"/> nothing was deleted.</summary>
    public SwitchOutcome ForgetAccount(AccountInfo a)
    {
        var outcome = AccountService.Instance.Forget(a.Id);
        RefreshAccounts();
        return outcome;
    }

    public CodexAccountOutcome SwitchCodexAccount(CodexAccountInfo account)
    {
        var outcome = CodexAccountService.Instance.Select(account.Id);
        ApplyCodexAccounts(CodexAccountService.Instance.List());
        return outcome;
    }

    public CodexAccountOutcome ForgetCodexAccount(CodexAccountInfo account)
    {
        var outcome = CodexAccountService.Instance.Forget(account.Id);
        ApplyCodexAccounts(CodexAccountService.Instance.List());
        return outcome;
    }

    public GrokAccountOutcome SwitchGrokAccount(GrokAccountInfo account)
    {
        var outcome = GrokAccountService.Instance.Select(account.Id);
        ApplyGrokAccounts(GrokAccountService.Instance.List());
        return outcome;
    }

    public GrokAccountOutcome ForgetGrokAccount(GrokAccountInfo account)
    {
        var outcome = GrokAccountService.Instance.Forget(account.Id);
        ApplyGrokAccounts(GrokAccountService.Instance.List());
        return outcome;
    }

    /// <summary>Re-home an open CLAUDE chat onto another saved account: copy its transcript into that account's private
    /// claude-home, then respawn the SAME conversation there. Claude had no equivalent of the Codex move below, so a
    /// chat whose account hit its usage limit was stuck - switching accounts only ever helped NEW chats while the open
    /// one kept answering "You've hit your session limit" under the login that created it.
    /// Works for normal chats AND bridge panes (the pane keeps its number, role prompt and provider settings).</summary>
    public bool MoveClaudeChatToAccount(ChatViewModel chat, string accountId, string? accountLabel = null)
    {
        if (!chat.IsClaude || chat.SessionId is not { } sid) return false;
        if (string.Equals(chat.AccountId, accountId, StringComparison.OrdinalIgnoreCase)) return true;   // already there
        // Must happen BEFORE the respawn: the new session resolves its id inside the target account's home only.
        if (!AccountService.Instance.MigrateTranscript(sid, chat.AccountId, accountId)) return false;

        var moved = new ChatViewModel(chat.Cwd, resume: sid, fork: false, title: chat.Title,
            accountId: accountId, provider: "claude")
            { Pinned = chat.Pinned, ExcludeFromMemory = chat.ExcludeFromMemory };
        moved.Items.Add(new DividerItem { Label = $"→ moved to {accountLabel ?? "the active Claude account"}" });
        // Bridge identity must survive the respawn or a moved pane loses its role in the roster.
        moved.BridgeLabel = chat.BridgeLabel;
        moved.IsBridgeHost = chat.IsBridgeHost;
        moved.IsBridgeManager = chat.IsBridgeManager;   // the crown moves with the conversation
        moved.Prelude = chat.Prelude;
        moved.AppendSystemPrompt = chat.AppendSystemPrompt;
        if (chat.Mode is { } mode) moved.SetMode(mode);
        moved.Model = chat.Model;
        moved.Effort = chat.Effort;

        var wasActive = ReferenceEquals(ActiveChat, chat);
        var wasSecondaryActive = ReferenceEquals(SecondaryActiveChat, chat);
        var chatIndex = Chats.IndexOf(chat);          // host + normal chats live here; pure peers don't
        chat.Close();
        if (chatIndex >= 0)
        {
            Chats.Remove(chat);
            Chats.Insert(Math.Min(chatIndex, Chats.Count), moved);
        }
        var roster = ReplaceLivePane(chat, moved);    // non-null when this is a live pane, parked or on the surface
        if (roster is not null)
        {
            // Peers were told this agent "hit an error and stopped" while its account was maxed out. Tell them it's
            // back so nobody permanently writes its number off the roster.
            var n = BridgeNumberOf(moved);
            foreach (var peer in roster.Panes.Where(p => !ReferenceEquals(p, moved)))
            {
                var note = $"[BRIDGE] {moved.AgentDisplay} agent #{n} is back (moved to a fresh account) and owns its board claims again.";
                peer.Prelude = string.IsNullOrEmpty(peer.Prelude) ? note : peer.Prelude + "\n" + note;
                peer.Items.Add(new DividerItem { Label = $"🔗 {moved.AgentDisplay} #{n} back on a fresh account" });
            }
        }
        Track(moved);
        MarkOwned(sid);
        if (wasActive && chatIndex >= 0) ActiveChat = moved;
        if (wasSecondaryActive && chatIndex >= 0) SecondaryActiveChat = moved;
        moved.Start();
        SaveSession();
        // Persist the roster that actually changed. The parameterless SaveBridge() only ever snapshots the primary
        // surface, so a parked roster would come back from disk still pointing at the account it just escaped.
        if (roster is not null) SaveBridge(roster.Panes, roster.Board);
        return true;
    }

    /// <summary>Every open Claude chat/pane pinned to a DIFFERENT account than <paramref name="accountId"/> that can
    /// be moved (has a resumable session). Parked rosters are included: they are live conversations the user can see
    /// (shell 2 shows one), and leaving them out is what let a bridge stay stuck on a maxed-out login.</summary>
    public List<ChatViewModel> ClaudeChatsMovableTo(string accountId) =>
        Chats.Concat(AllLivePanes()).Distinct()
            .Where(c => c.IsClaude && c.SessionId is not null
                        && !string.Equals(c.AccountId, accountId, StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>Move every eligible open Claude chat/pane onto <paramref name="accountId"/>.
    /// Returns the count moved plus the NAME of everything that did not, because a chat left behind on an exhausted
    /// account is only recoverable if the user is told which one it was.</summary>
    public (int Moved, List<string> Failed) MoveAllClaudeChatsToAccount(string accountId, string? accountLabel = null)
    {
        var moved = 0;
        var failed = new List<string>();
        foreach (var chat in ClaudeChatsMovableTo(accountId))   // snapshot - the move mutates Chats/BridgePanes
        {
            if (MoveClaudeChatToAccount(chat, accountId, accountLabel)) moved++;
            else failed.Add(DescribeForMoveReport(chat));
        }
        return (moved, failed);
    }

    /// <summary>How a chat is named in a "couldn't be moved" report: its bridge role if it has one, else its title.</summary>
    private string DescribeForMoveReport(ChatViewModel chat)
    {
        var name = !string.IsNullOrWhiteSpace(chat.Title) ? chat.Title!.Trim() : chat.Cwd;
        if (name.Length > 60) name = name[..60].TrimEnd() + "…";
        var number = BridgeNumberOf(chat);
        return number > 0 ? $"{name} (bridge agent #{number})" : name;
    }

    /// <summary>Re-home an open Codex chat onto another saved account: copy its rollout into that account's private
    /// CODEX_HOME, then respawn the SAME thread there. This is how a conversation escapes an exhausted account —
    /// without it, "switch account" only helps new chats while the open chat keeps erroring on the old login.
    /// Works for normal chats AND bridge panes (the pane keeps its number, role prompt and provider settings).</summary>
    public bool MoveCodexChatToAccount(ChatViewModel chat, string accountId, string? accountLabel = null)
    {
        if (!chat.IsCodex || chat.SessionId is not { } sid) return false;
        if (string.Equals(chat.AccountId, accountId, StringComparison.OrdinalIgnoreCase)) return true;   // already there
        CodexAccountService.Instance.MigrateThread(sid, chat.AccountId, accountId);   // best-effort; resume shows an error if the rollout is missing

        var moved = new ChatViewModel(chat.Cwd, resume: sid, fork: false, title: chat.Title,
            accountId: accountId, provider: "codex")
            { Pinned = chat.Pinned, ExcludeFromMemory = chat.ExcludeFromMemory };
        moved.Items.Add(new DividerItem { Label = $"→ moved to {accountLabel ?? "the active Codex account"}" });
        // Bridge identity must survive the respawn or a moved pane loses its role in the roster.
        moved.BridgeLabel = chat.BridgeLabel;
        moved.IsBridgeHost = chat.IsBridgeHost;
        moved.IsBridgeManager = chat.IsBridgeManager;   // the crown moves with the conversation
        moved.Prelude = chat.Prelude;
        moved.AppendSystemPrompt = chat.AppendSystemPrompt;
        if (chat.Mode is { } mode) moved.SetMode(mode);
        moved.Model = chat.Model;
        moved.Effort = chat.Effort;

        var wasActive = ReferenceEquals(ActiveChat, chat);
        var wasSecondaryActive = ReferenceEquals(SecondaryActiveChat, chat);
        var chatIndex = Chats.IndexOf(chat);          // host + normal chats live here; pure peers don't
        chat.Close();
        if (chatIndex >= 0)
        {
            Chats.Remove(chat);
            Chats.Insert(Math.Min(chatIndex, Chats.Count), moved);
        }
        var roster = ReplaceLivePane(chat, moved);    // non-null when this is a live pane, parked or on the surface
        if (roster is not null)
        {
            // Peers may have been told this agent "hit an error and stopped" while its account was maxed out.
            // Tell them it's back so nobody permanently writes its number off the roster.
            var n = BridgeNumberOf(moved);
            foreach (var peer in roster.Panes.Where(p => !ReferenceEquals(p, moved)))
            {
                var note = $"[BRIDGE] {moved.AgentDisplay} agent #{n} is back (moved to a fresh account) and owns its board claims again.";
                peer.Prelude = string.IsNullOrEmpty(peer.Prelude) ? note : peer.Prelude + "\n" + note;
                peer.Items.Add(new DividerItem { Label = $"🔗 {moved.AgentDisplay} #{n} back on a fresh account" });
            }
        }
        Track(moved);
        MarkOwned(sid);
        if (wasActive && chatIndex >= 0) ActiveChat = moved;
        if (wasSecondaryActive && chatIndex >= 0) SecondaryActiveChat = moved;
        moved.Start();
        SaveSession();
        if (roster is not null) SaveBridge(roster.Panes, roster.Board);
        return true;
    }

    /// <summary>Every open Codex chat/pane that is pinned to a DIFFERENT account than <paramref name="accountId"/>
    /// and can be moved (has a resumable session). Parked rosters included - see
    /// <see cref="ClaudeChatsMovableTo"/>.</summary>
    public List<ChatViewModel> CodexChatsMovableTo(string accountId) =>
        Chats.Concat(AllLivePanes()).Distinct()
            .Where(c => c.IsCodex && c.SessionId is not null
                        && !string.Equals(c.AccountId, accountId, StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>Move every eligible open Codex chat/pane onto <paramref name="accountId"/>.
    /// Returns the count moved plus the NAME of everything that did not - see
    /// <see cref="MoveAllClaudeChatsToAccount"/>.</summary>
    public (int Moved, List<string> Failed) MoveAllCodexChatsToAccount(string accountId, string? accountLabel = null)
    {
        var moved = 0;
        var failed = new List<string>();
        foreach (var chat in CodexChatsMovableTo(accountId))   // snapshot - the move mutates Chats/BridgePanes
        {
            if (MoveCodexChatToAccount(chat, accountId, accountLabel)) moved++;
            else failed.Add(DescribeForMoveReport(chat));
        }
        return (moved, failed);
    }

    /// <summary>Every open chat/pane still running on a provider other than <paramref name="provider"/>.</summary>
    public List<ChatViewModel> ChatsMovableToProvider(string provider)
    {
        var target = ProviderModelCatalog.Normalize(provider);
        return Chats.Concat(BridgePanes).Distinct()
            .Where(c => !IsParkedBridgePane(c)
                        && !string.Equals(ProviderModelCatalog.Normalize(c.Provider), target,
                StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>Re-home an OPEN chat/pane onto a different PROVIDER. Unlike the Codex account move, the conversation
    /// cannot travel — Claude, Codex, Kimi, and Grok keep separate transcripts — so the thread restarts fresh under the new
    /// AI while keeping its project, sidebar slot, pinned state, permission mode and bridge role. Without this,
    /// switching to another AI only ever helped NEW chats, which left the open chat stranded on the exhausted account
    /// it was switched away from (its model pill still reading e.g. a GPT model, so the switch looked broken).</summary>
    public bool MoveChatToProvider(ChatViewModel chat, string provider)
    {
        provider = ProviderModelCatalog.Normalize(provider);
        if (IsParkedBridgePane(chat)) return false;   // provider changes respawn; parked live rosters must stay uninterrupted
        if (string.Equals(ProviderModelCatalog.Normalize(chat.Provider), provider, StringComparison.OrdinalIgnoreCase))
            return true;   // already there
        var chatIndex = Chats.IndexOf(chat);          // host + normal chats live here; pure peers don't
        var paneIndex = BridgePanes.IndexOf(chat);    // >= 0 when this is a live bridge pane
        if (chatIndex < 0 && paneIndex < 0) return false;   // not an open chat: nothing to replace it in

        // Model and effort are provider-specific ids (a Codex model id means nothing to Claude), so the replacement
        // keeps the defaults its own constructor loaded. Only provider-neutral state travels.
        var moved = new ChatViewModel(chat.Cwd, title: chat.Title, provider: provider) { Pinned = chat.Pinned };
        moved.Items.Add(new DividerItem
        {
            Label = $"→ switched to {moved.ProviderDisplay} — new conversation " +
                    $"(the {chat.ProviderDisplay} thread stays in this project's history)",
        });
        moved.Prelude = chat.Prelude;
        if (chat.Mode is { } mode) moved.SetMode(mode);

        var wasActive = ReferenceEquals(ActiveChat, chat);
        var wasSecondaryActive = ReferenceEquals(SecondaryActiveChat, chat);
        var number = BridgeNumberOf(chat);   // read the roster identity off the OLD label before it goes away
        MarkOwned(chat.SessionId);           // keep the old thread listed so its conversation stays reachable
        chat.Close();
        if (chatIndex >= 0)
        {
            Chats.Remove(chat);
            Chats.Insert(Math.Min(chatIndex, Chats.Count), moved);
        }
        if (paneIndex >= 0)
        {
            // The pane keeps its roster number; only the agent behind it changes, so re-label and re-brief it.
            moved.IsBridgeHost = chat.IsBridgeHost;
            moved.IsBridgeManager = chat.IsBridgeManager;   // a provider swap must not silently fire the manager
            moved.BridgeLabel = number > 0 ? AgentLabel(moved, number) : chat.BridgeLabel;
            if (number > 0) moved.Title = $"Bridge · {moved.AgentDisplay} {number}";
            BridgePanes[paneIndex] = moved;
            _bridgeErrored.Remove(chat);   // the old pane is gone; the replacement announces its own errors
            if (number > 0) moved.AppendSystemPrompt = BridgePrompt(moved, number, BridgeNumbers(), ManagerNumberIn(BridgePanes));
            RaiseBridgeUi();
            // Peers were briefed on this agent by its old provider name; correct the roster so nobody keeps
            // addressing it as the AI it no longer runs, or assumes its board claims were abandoned.
            foreach (var peer in BridgePanes.Where(p => !ReferenceEquals(p, moved)))
            {
                var note = $"[BRIDGE] agent #{number} now runs on {moved.ProviderDisplay} (it was {chat.ProviderDisplay}) " +
                           "and has started a fresh conversation. It still owns the same area on the status board.";
                peer.Prelude = string.IsNullOrEmpty(peer.Prelude) ? note : peer.Prelude + "\n" + note;
                peer.Items.Add(new DividerItem { Label = $"🔗 agent #{number} switched to {moved.AgentDisplay}" });
            }
        }
        else moved.AppendSystemPrompt = chat.AppendSystemPrompt;
        Track(moved);
        if (wasActive) ActiveChat = moved;
        if (wasSecondaryActive) SecondaryActiveChat = moved;
        moved.Start();
        SaveSession();
        if (paneIndex >= 0) SaveBridge();
        return true;
    }

    /// <summary>Move every open chat/pane off its current AI and onto <paramref name="provider"/>. Returns (moved, failed).</summary>
    public (int Moved, int Failed) MoveAllChatsToProvider(string provider)
    {
        int moved = 0, failed = 0;
        foreach (var chat in ChatsMovableToProvider(provider))   // snapshot - the move mutates Chats/BridgePanes
        {
            if (MoveChatToProvider(chat, provider)) moved++;
            else failed++;
        }
        return (moved, failed);
    }

    private bool _usagePolling;
    /// <summary>Probe every saved account's <c>/usage</c> in the background and push the numbers onto its row, flagging any
    /// whose session/week % moved since the last poll (so you can watch an account's usage change over time). Sequential
    /// + guarded so overlapping ticks don't pile up claude processes. Marshals UI updates itself - safe to call via
    /// <c>Task.Run</c> so none of the probe's file I/O touches the UI thread.</summary>
    public async Task RefreshAccountUsageAsync()
    {
        if (_usagePolling) return;
        _usagePolling = true;
        try
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            var accts = dispatcher is not null ? dispatcher.Invoke(() => Accounts.ToList()) : Accounts.ToList();
            foreach (var a in accts)
            {
                dispatcher?.Invoke(a.MarkUsageChecking);
                var u = await AccountService.Instance.RefreshUsageAsync(a.Id);
                if (u is not null) dispatcher?.Invoke(() => a.ApplyUsage(u));
            }
        }
        finally { _usagePolling = false; }
    }

    private int _projectLoadVersion;

    public void LoadProjects()
    {
        // Opening Home can request a fresh scan while an older startup/settings scan is still running. Only the last
        // request may publish, or a slower stale result can put an older folder back above the chat just created.
        var version = Interlocked.Increment(ref _projectLoadVersion);
        Task.Run(() =>
        {
            var projects = SessionCatalog.ListProjects();
            System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
            {
                if (version != Volatile.Read(ref _projectLoadVersion)) return;
                var hidden = AppSettings.Current.HiddenProjects;
                var owned = AppSettings.Current.OwnedSessions;
                var deleted = AppSettings.Current.DeletedSessions;
                var onlyOwned = AppSettings.Current.ShowOnlyOwnedSessions;
                // A deleted session is gone from every list, owned-filter on or off. Its transcript may well still be
                // on disk - the provider owns that file - which is exactly why the tombstone has to be checked here.
                bool Keep(SessionEntry s) => !deleted.Contains(s.SessionId) && (!onlyOwned || owned.Contains(s.SessionId));
                Projects.Clear();
                RecentSessions.Clear();
                foreach (var p in projects.Where(p => !hidden.Contains(p.Cwd)))
                {
                    var sessions = p.Sessions.Where(Keep).ToList();
                    if (onlyOwned && sessions.Count == 0) continue;   // hide projects with no VibeCode chats
                    var vm = new ProjectVm { Cwd = p.Cwd, Name = p.Name, LastModified = p.LastModified };
                    foreach (var sess in sessions) vm.Sessions.Add(sess);
                    Projects.Add(vm);
                }
                foreach (var sess in projects.Where(p => !hidden.Contains(p.Cwd)).SelectMany(p => p.Sessions)
                             .Where(Keep).OrderByDescending(s => s.LastModified).Take(10))
                    RecentSessions.Add(sess);
                RefreshRecentProjects();
                Raise(nameof(HiddenCount));
            });
        });
    }

    /// <summary>Combine the provider-independent MRU with transcript history, dedupe path spellings, and show the five
    /// most recently used existing folders. Open chats no longer suppress a chip — users often start another chat in
    /// the same project and need one-click access to that path.</summary>
    private void RefreshRecentProjects()
    {
        var settings = AppSettings.Current;
        bool Hidden(string path) => settings.HiddenProjects.Any(hidden => RecentDirectoryHistory.PathsEqual(hidden, path));
        var remembered = (settings.RecentDirectories ?? new List<RecentDirectoryState>())
            .Where(entry => !Hidden(entry.Cwd) && Directory.Exists(entry.Cwd))
            .Select(entry => new RecentDirectoryCandidate(
                entry.Cwd, RecentDirectoryHistory.DisplayName(entry.Cwd), entry.LastUsed));
        var catalog = Projects
            .Where(project => !Hidden(project.Cwd) && Directory.Exists(project.Cwd))
            .Select(project => new RecentDirectoryCandidate(
                project.Cwd, project.Name, new DateTimeOffset(project.LastModified)));
        var suggestions = RecentDirectoryHistory.SelectSuggestions(remembered.Concat(catalog));

        RecentProjects.Clear();
        foreach (var suggestion in suggestions)
        {
            var project = Projects.FirstOrDefault(candidate =>
                              RecentDirectoryHistory.PathsEqual(candidate.Cwd, suggestion.Cwd))
                          ?? new ProjectVm
                          {
                              Cwd = suggestion.Cwd,
                              Name = suggestion.Name,
                              LastModified = suggestion.LastUsed.LocalDateTime,
                          };
            RecentProjects.Add(project);
        }
    }

    private static bool IsUntitledOpenChat(ChatViewModel chat)
    {
        if (chat.ResumeSessionId is not null) return false;
        if (chat.Items.Any(item => item is UserItem or QueuedItem)) return false;
        return string.IsNullOrWhiteSpace(chat.Title)
               || string.Equals(chat.Title, RecentDirectoryHistory.DisplayName(chat.Cwd),
                   StringComparison.OrdinalIgnoreCase);
    }

    private void RememberRecentDirectory(string cwd)
    {
        if (!AppSettings.Current.RememberRecentDirectory(cwd)) return;
        Raise(nameof(HiddenCount));
        Raise(nameof(PreferredNewChatCwd));
        RefreshRecentProjects();
        RequestSave();
    }

    /// <summary>Remember a folder the user just browsed/typed without starting a chat yet, so the chip is ready next time.</summary>
    public void RememberDirectorySelection(string cwd) => RememberRecentDirectory(cwd);

    public void HideProject(ProjectVm project)
    {
        AppSettings.Current.HiddenProjects.Add(project.Cwd);
        Projects.Remove(project);
        RefreshRecentProjects();
        Raise(nameof(HiddenCount));
        AppSettings.Current.Save();
    }

    /// <summary>Hide every project the sidebar is currently listing, in one pass. Not a loop over
    /// <see cref="HideProject"/>: that removes from the collection it is walking, and writes settings once per
    /// project. Returns how many were hidden so the caller can say so rather than guess.</summary>
    public int HideAllProjects()
    {
        if (Projects.Count == 0) return 0;
        var count = Projects.Count;
        foreach (var project in Projects) AppSettings.Current.HiddenProjects.Add(project.Cwd);
        Projects.Clear();
        RefreshRecentProjects();
        Raise(nameof(HiddenCount));
        AppSettings.Current.Save();
        return count;
    }

    /// <param name="configure">Runs on the new chat immediately BEFORE it spawns. The CLI is handed the model and
    /// effort at spawn time, so anything that must apply to the very first turn has to be set here — afterwards it
    /// would take a restart (or a push that also rewrites the app-wide default).</param>
    /// <param name="accountId">Which saved login the session signs in as. Null means "whatever this provider has
    /// selected", which is what every ordinary new chat wants; a caller that lets the user pick an account up front
    /// (Demon Mode) passes one, and so does <see cref="ResumeSession"/> - a transcript can only be resumed by the
    /// account whose home stores it. <c>""</c> means the shared <c>~/.claude</c> home explicitly.</param>
    public ChatViewModel NewChat(string cwd, string? resume = null, bool fork = false, string? title = null,
        string? provider = null, string? accountId = null, bool activatePrimary = true,
        Action<ChatViewModel>? configure = null)
    {
        if (activatePrimary) HideBridge();   // a running bridge keeps going in the background; just leave its overlay
        provider ??= AppSettings.Current.DefaultProvider;
        var chat = new ChatViewModel(cwd, resume, fork, title, accountId: accountId, provider: provider);
        // Every conversation opened here - fresh, forked, or a transcript picked out of history - starts in the mode
        // the user last deliberately picked, Auto until they pick something else. A resumed one used to be left out,
        // and the constructor's own default is "default", so reopening an old chat landed on Ask: the one mode nobody
        // chose. Chats that were already OPEN keep their own remembered mode instead - they come back through
        // RestoreSessionCore, which falls back to this same seed only when it has nothing saved for them.
        // SetMode also records the choice so a provider's initialize event cannot reset the pill to Ask.
        // remember: false — this is reading the seed, not setting it.
        chat.SetMode(AppSettings.Current.DefaultMode);
        Track(chat);
        MarkOwned(chat.SessionId);   // a resumed chat already has its id (set in the ctor, no PropertyChanged)
        Chats.Insert(0, chat);
        RememberRecentDirectory(cwd);   // immediate and provider-neutral; no transcript file needs to exist yet
        if (Chats.Any(c => c.Pinned)) ReorderPinned();   // keep pinned chats above a freshly created one
        if (activatePrimary) ActiveChat = chat;
        if (chat.SessionId is not null)
            SaveSession();   // resumed chats already have an id in the ctor, so no later SessionId change would save them
        configure?.Invoke(chat);   // last thing before spawn: the seed applied above is a default this may override
        chat.Start();
        return chat;
    }

    /// <summary>Toggle a chat's pinned state and move it between the dedicated pinned and regular sections.</summary>
    public void TogglePin(ChatViewModel chat)
    {
        chat.Pinned = !chat.Pinned;
        ReorderPinned();
        SaveSession();
    }

    /// <summary>Float the conversation that owns this activity to the top of its sidebar section. A Bridge peer has
    /// no row of its own, so its host row is promoted instead. Pinned chats remain in their explicit pinned section.</summary>
    public void BumpChatToTop(ChatViewModel activitySource)
    {
        var chat = SidebarChatFor(activitySource);
        if (chat is null) return;
        var from = Chats.IndexOf(chat);
        // Unpinned chats land just under the pinned block; a pinned chat floats to the very top.
        var target = chat.Pinned ? 0 : Chats.Count(c => c.Pinned && !ReferenceEquals(c, chat));
        if (from == target) return;
        Chats.Move(from, target);
        RequestSave();
    }

    private ChatViewModel? SidebarChatFor(ChatViewModel activitySource)
    {
        if (Chats.Contains(activitySource)) return activitySource;
        if (!TryGetLiveBridge(activitySource, out var bridge)) return null;
        return bridge.Panes.FirstOrDefault(Chats.Contains);
    }

    /// <summary>Apply one manual drag inside the chat's current pinned/unpinned section. This changes order only:
    /// it never pins the row, and the next MessageSent promotion is free to move another conversation above it.</summary>
    public bool MoveChat(ChatViewModel chat, ChatViewModel? target, bool insertAfter)
    {
        if (!Chats.Contains(chat) || target is null || !Chats.Contains(target)
            || ReferenceEquals(chat, target) || chat.Pinned != target.Pinned)
            return false;

        var section = Chats.Where(candidate => candidate.Pinned == chat.Pinned).ToList();
        section.Remove(chat);
        var insertAt = section.IndexOf(target) + (insertAfter ? 1 : 0);
        section.Insert(Math.Clamp(insertAt, 0, section.Count), chat);

        var ordered = chat.Pinned
            ? section.Concat(Chats.Where(candidate => !candidate.Pinned)).ToList()
            : Chats.Where(candidate => candidate.Pinned).Concat(section).ToList();

        var changed = false;
        for (var i = 0; i < ordered.Count; i++)
        {
            var current = Chats.IndexOf(ordered[i]);
            if (current == i) continue;
            Chats.Move(current, i);
            changed = true;
        }
        if (changed) RequestSave();
        return changed;
    }

    /// <summary>Stable reorder: pinned chats first (keeping their relative order), then the rest.</summary>
    private void ReorderPinned()
    {
        var ordered = Chats.OrderByDescending(c => c.Pinned).ToList();   // LINQ OrderBy is a stable sort
        for (int i = 0; i < ordered.Count; i++)
        {
            var cur = Chats.IndexOf(ordered[i]);
            if (cur != i) Chats.Move(cur, i);
        }
        ChatGroups.Refresh();   // Pinned changed on the item; force the grouping boundary to update immediately
    }

    /// <summary>Tag a session id as one of ours (shown even when the owned-only filter is on).</summary>
    private static void MarkOwned(string? sessionId)
    {
        if (sessionId is { } id && AppSettings.Current.OwnedSessions.Add(id)) AppSettings.Current.Save();
    }

    /// <summary>Persist state whenever a chat gets (or changes) its resumable session id, and mark it "ours".</summary>
    private void Track(ChatViewModel c)
    {
        c.RefreshPeerChatAccess = () => RefreshBridgeChatAccess(c);
        c.MessageSent += () =>
        {
            BumpChatToTop(c);   // sending in a chat floats it to the top of the sidebar list
            RememberRecentDirectory(c.Cwd);
        };
        c.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChatViewModel.SessionId))
            {
                // Any chat opened/created inside VibeCode is one of "our" chats and stays visible.
                if (c.SessionId is { } id) AppSettings.Current.OwnedSessions.Add(id);
                SaveSession();       // persists OwnedSessions + OpenChats
                // Capture the peer's resumable id in whichever live bridge owns it. A parked bridge is deliberately
                // absent from BridgePanes while another chat is on screen, but its provider process is still alive.
                if (TryGetLiveBridge(c, out var bridge)) SaveBridge(bridge.Panes, bridge.Board);
                // A session id is exactly what makes a pane forkable, so it is also what makes the WHOLE roster
                // forkable. Without this, CanForkBridge kept the value it had when the roster was built - false,
                // because no agent has an id yet at that point - and the header's Fork stayed dead for the life of
                // the bridge no matter how many turns the agents took.
                if (BridgePanes.Contains(c)) RaiseBridgeFork();
            }
            else if (e.PropertyName == nameof(ChatViewModel.Status))
            {
                OnBridgePaneStatusChanged(c);   // surface a crashed bridge peer to its still-running peers
                OnBridgeManagerStatusChanged(c); // manager loop: route dispatches / relay worker reports
            }
            else if (e.PropertyName is nameof(ChatViewModel.Draft) or nameof(ChatViewModel.Title)
                     or nameof(ChatViewModel.Mode))
            {
                // An unsent prompt / a renamed chat / a permission mode must not need a polite shutdown to survive.
                // Mode is in here rather than in SetMode so the pill and the file agree however the mode moved -
                // including the plan-approval hand-off and the re-assert on a CLI re-init.
                RequestSave();
            }
            else if (e.PropertyName == nameof(ChatViewModel.GrokAccount) && c.IsGrok && BridgePanes.Contains(c))
            {
                RaiseBridgeGrokUsage();
            }
        };
    }

    /// <summary>Snapshot the open chats (and which is "active") so the next launch reopens them.</summary>
    public void SaveSession()
    {
        SnapshotSession();
        SaveSnapshot();
    }

    /// <summary>False until <see cref="RestoreSession"/> has run. Before that point <see cref="Chats"/> is empty
    /// because nothing has been reopened yet - it is NOT "the user has no chats" - so a snapshot taken then would
    /// publish an empty list over a full one.</summary>
    private bool _sessionRestored;

    /// <summary>Saved chats this launch could not rebuild. Written back untouched so a transient failure costs the
    /// user a restart, not the conversation.</summary>
    private readonly List<OpenChatState> _unrestoredChats = new();

    private void SnapshotSession()
    {
        var s = AppSettings.Current;

        // THE data-loss guard. A save can be triggered from anywhere at any time - the 1.2s autosave, losing focus,
        // and above all App.OnDispatcherUnhandledException, which flushes state on its way through *any* unhandled
        // exception. A WPF layout pass throwing while startup is still on "Restoring accounts and conversations…"
        // therefore used to run SaveEverything() with an empty Chats collection: settings.json got OpenChats: [],
        // and because File.Replace rotates the old file into settings.bak.json, the very next autosave (1.2s later)
        // overwrote the backup too. Every chat, gone, with no error and nothing to recover from.
        // Everything else here is still persisted; only the chat list waits until it means something.
        if (!_sessionRestored)
        {
            s.SidebarCollapsed = SidebarCollapsed;
            return;
        }

        s.OpenChats = Chats
            // A provider/auth/rate-limit error is transient. If a resumable id exists, keep the chat: filtering errors
            // here made a perfectly intact on-disk transcript disappear from the sidebar on the next restart.
            // Also keep a chat that has an unsent draft but no id yet: a first prompt typed into a brand-new chat has
            // no session_id until a turn runs, and dropping it here is exactly how an unsent draft "just disappears"
            // after a restart. A chat with neither an id nor a draft is still discarded.
            .Where(c => c.SessionId is not null || !string.IsNullOrWhiteSpace(c.Draft))
            .Select(c => new OpenChatState
            {
                Cwd = c.Cwd,
                SessionId = c.SessionId,
                Provider = c.Provider,
                Title = c.Title,
                Active = c == ActiveChat,
                SecondaryActive = c == SecondaryActiveChat,
                Pinned = c.Pinned,
                AccountId = c.AccountId,
                Mode = c.Mode,
                Draft = NullIfEmpty(c.Draft),
                ExcludeFromMemory = c.ExcludeFromMemory,
            })
            .ToList();
        // Chats this launch couldn't rebuild are not on screen, but they are still the user's - re-persist them
        // rather than let one bad launch drop them out of the file forever.
        if (_unrestoredChats.Count > 0)
            s.OpenChats.AddRange(_unrestoredChats.Where(o => s.OpenChats.All(k => k.SessionId != o.SessionId)));
        s.SidebarCollapsed = SidebarCollapsed;
        s.BridgeVisible = ShowBridge;
        s.SecondaryBridgeVisible = SecondaryShowBridge;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    // ---- autosave: nothing may depend on the window's Closing handler, which a force quit never runs ----

    private System.Windows.Threading.DispatcherTimer? _autoSaveTimer;
    private bool _saveDirty;

    /// <summary>Persist everything worth keeping. Safe to call at any moment (including from a shutdown hook).</summary>
    public void SaveEverything()
    {
        // Build every snapshot first and perform one atomic settings write. With several four-agent bridges alive,
        // saving each roster separately made the dispatcher stutter immediately after a chat switch.
        SnapshotSession();
        foreach (var bridge in LiveBridges()) SnapshotBridge(bridge.Panes, bridge.Board);
        SaveSnapshot();
    }

    private void SaveSnapshot()
    {
        if (AppSettings.Current.TrySave() is not { } error) { _saveDirty = false; return; }
        // Save() intentionally discards IO errors. Treating that as success cancelled autosave retries and could
        // leave an entire background session unsaved after one transient sharing violation or disk failure.
        RequestSave();
        CrashLog.Write(error, "SessionSave");
    }

    /// <summary>Write a pending autosave right now instead of waiting for its timer. Called at the natural "the user
    /// stopped touching the app" moments (losing focus, shutting down, crashing) so the debounce window is not the
    /// thing standing between a crash and the user's work.</summary>
    public void FlushPendingSave()
    {
        _autoSaveTimer?.Stop();
        if (!_saveDirty) return;
        try { SaveEverything(); }
        catch { /* keep it dirty; the next mutation reschedules */ }
    }

    /// <summary>Mark state dirty and schedule a coalesced save. Called from the many small mutations that used to be
    /// persisted only when the app was closed politely - a Task Manager kill, a crash, or a power loss skips Closing
    /// entirely, which is exactly how a working bridge used to disappear.</summary>
    public void RequestSave()
    {
        _saveDirty = true;
        if (_autoSaveTimer is null)
        {
            _autoSaveTimer = new System.Windows.Threading.DispatcherTimer
            {
                // Coalesce bursts (typing, streaming) into one write. This interval IS the worst-case data loss for a
                // hard kill or power cut, where no shutdown hook can run at all - so keep it short.
                Interval = TimeSpan.FromMilliseconds(1200),
            };
            _autoSaveTimer.Tick += (_, _) =>
            {
                _autoSaveTimer!.Stop();   // idle again until the next mutation asks for a save
                if (!_saveDirty) return;
                try { SaveEverything(); }
                catch { _saveDirty = true; _autoSaveTimer.Start(); }   // transient IO: retry, never take the UI down
            };
        }
        _autoSaveTimer.Start();
    }

    /// <summary>Reopen the chats saved from last session (call once at startup).</summary>
    public void RestoreSession()
    {
        try { RestoreSessionCore(); }
        finally
        {
            // Restore is over, however it went. Only now may a snapshot speak for the whole chat list - see
            // _sessionRestored. Anything that could NOT be rebuilt stays in _unrestoredChats and is written back
            // verbatim, so a folder that briefly went missing (an unplugged drive, a synced project) does not
            // quietly delete the conversation that lived in it.
            _sessionRestored = true;
        }
    }

    private void RestoreSessionCore()
    {
        ImportPendingBridgeRecoveries();
        // Remove only malformed snapshots. The five-hour timeout belongs to live idle processes, not conversation
        // history: Claude/Codex transcripts remain resumable after an overnight shutdown.
        PruneSavedBridges();
        // Restore chats that either have a resumable id OR carry an unsent draft (a never-sent first prompt): the
        // latter has no SessionId, so gating restore on the id alone silently threw the draft away on every launch.
        var saved = AppSettings.Current.OpenChats
            .Where(o => o.SessionId is not null || !string.IsNullOrWhiteSpace(o.Draft))
            .Where(o => o.SessionId is null || !AppSettings.Current.DeletedSessions.Contains(o.SessionId))
            .ToList();
        ChatViewModel? active = null;
        ChatViewModel? secondaryActive = null;
        // saved is newest-first (same order as Chats); Add() appends so the order is preserved
        foreach (var oc in saved)
        {
            // One chat that refuses to rebuild must not abort the loop. It used to: the throw escaped to the
            // dispatcher, every LATER chat was never added, and the next autosave wrote that shorter list back.
            try
            {
                var cwd = Directory.Exists(oc.Cwd) ? oc.Cwd : DefaultCwd;
                var chat = new ChatViewModel(cwd, oc.SessionId, fork: false, title: oc.Title, accountId: oc.AccountId, provider: oc.Provider)
                { Pinned = oc.Pinned, Draft = oc.Draft ?? "", ExcludeFromMemory = oc.ExcludeFromMemory };
                // Put the chat back in the mode it was left in. SetMode (not the property) because the provider's
                // init event reports the CLI's own mode and overwrites anything the pane hasn't registered as a
                // deliberate choice - which is exactly how every restored chat used to come back on "ask".
                // remember: false - restoring is not the user picking a mode for future chats.
                // A snapshot written before modes were remembered (or hand-edited to something no provider knows)
                // falls back to the seed rather than to "ask", which is the mode nobody chose.
                chat.SetMode(AppSettings.IsKnownPermissionMode(oc.Mode)
                    ? oc.Mode!
                    : AppSettings.Current.DefaultMode);
                Track(chat);
                MarkOwned(oc.SessionId);   // restored chats stay "ours" so their project shows in the list
                Chats.Add(chat);
                chat.Start();
                if (oc.Active) active = chat;
                if (oc.SecondaryActive) secondaryActive = chat;
            }
            catch (Exception ex)
            {
                _unrestoredChats.Add(oc);   // keep the pointer; losing it is how a transcript becomes unreachable
                CrashLog.Note("ChatRestoreFailed",
                    $"session {oc.SessionId ?? "(none)"} in {oc.Cwd}: {ex.Message}{Environment.NewLine}" +
                    "The saved entry was kept so the chat is not dropped from settings.json.");
            }
        }

        // Flag the sidebar rows that anchor a saved bridge so they keep their "click to return" cue. A snapshot whose
        // host row is NOT open stays dormant and is offered on the home screen instead: bridges are now kept
        // indefinitely, so resurrecting a chat (and spawning a provider process) for every snapshot ever taken would
        // start a small fleet of agents on every launch.
        foreach (var sb in AppSettings.Current.SavedBridges)
            if (Chats.FirstOrDefault(c => c.SessionId == sb.HostSessionId && c.Provider == sb.Provider) is { } anchor)
                anchor.IsBridgeHost = true;

        if (Chats.Any(c => c.Pinned)) ReorderPinned();   // pinned chats float to the top on restore too
        if (Chats.Count > 0)
        {
            // Restore the last-focused chat as saved. Only per-account isolation (opt-in) can veto it.
            ActiveChat = active is not null && ChatVisibleInSidebar(active)
                ? active
                : FirstVisibleChat(preferNot: null) ?? Chats[0];
            SecondaryActiveChat = secondaryActive is not null && ChatVisibleInSidebar(secondaryActive)
                                  && !ReferenceEquals(secondaryActive, ActiveChat)
                ? secondaryActive
                : FirstVisibleChat(preferNot: ActiveChat) ?? ActiveChat;
        }

        // Put the bridge back on screen if the user was looking at it (its host was the active chat, or the overlay was
        // up when the app died). A bridge that was running in the BACKGROUND stays dormant behind its host cue.
        var bridgeAnchor = ActiveChat is not null && ChatVisibleInSidebar(ActiveChat) && HasSavedBridgeFor(ActiveChat)
            ? ActiveChat
            : AppSettings.Current.BridgeVisible
                ? Chats.FirstOrDefault(c => ChatVisibleInSidebar(c) && HasSavedBridgeFor(c))
                : null;
        if (bridgeAnchor is not null) RestoreBridge(bridgeAnchor);
        SecondaryShowBridge = AppSettings.Current.SecondaryBridgeVisible
                              && SecondaryActiveChat is not null
                              && ReferenceEquals(SecondaryActiveChat, BridgePanes.FirstOrDefault());
        // Shell 2 can be showing a bridge of its OWN rather than the primary's. Its selected chat is already
        // persisted, so a saved roster hanging off that chat is all the anchor this needs — without it, a restart
        // brought back one of the two displays and left the other on a bare transcript.
        if (!SecondaryShowBridge
            && AppSettings.Current.SecondaryBridgeVisible
            && AppSettings.Current.DualMonitorDoubleSessions
            && SecondaryActiveChat is { } secondaryAnchor
            && !ReferenceEquals(secondaryAnchor, ActiveChat)
            && ChatVisibleInSidebar(secondaryAnchor)
            && HasSavedBridgeFor(secondaryAnchor))
        {
            RestoreBridge(secondaryAnchor, showPrimary: false);
        }
        RefreshResumableBridges();
        RefreshChatAccountFilter();   // no-op unless per-account isolation is on; then it hides other-account rows
    }

    /// <summary>Support/recovery hook: a project-local manifest can re-index bridge session ids whose old single-slot
    /// pointer was already lost. Import is atomic with settings persistence, then the manifest is retained as a dated
    /// .imported receipt. Normal app use never creates this file.</summary>
    private static void ImportPendingBridgeRecoveries()
    {
        const string fileName = ".vibecode-bridge-recovery.json";
        if (Environment.GetEnvironmentVariable("VIBECODE_HIDDEN") == "1") return;   // UI smoke tests never mutate real state
        var settings = AppSettings.Current;
        var roots = settings.OpenChats.Select(x => x.Cwd)
            .Concat(settings.SavedBridges.Select(x => x.Cwd))
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var root in roots)
        {
            var path = Path.Combine(root, fileName);
            if (!File.Exists(path)) continue;
            try
            {
                var recovered = JsonSerializer.Deserialize<List<SavedBridgeState>>(File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<SavedBridgeState>();
                recovered = recovered
                    .Where(x => !string.IsNullOrWhiteSpace(x.HostSessionId)
                                && x.Peers.Any(p => !string.IsNullOrWhiteSpace(p.SessionId)))
                    .ToList();
                if (recovered.Count == 0) continue;

                foreach (var bridge in recovered)
                {
                    // The manifest is project-local support input, not authority to launch an agent in another folder.
                    bridge.Cwd = root;
                    if (string.IsNullOrWhiteSpace(bridge.Provider)) bridge.Provider = "claude";
                    foreach (var peer in bridge.Peers) peer.Cwd = root;
                    settings.UpsertSavedBridge(bridge);
                    settings.OwnedSessions.Add(bridge.HostSessionId);
                    foreach (var peer in bridge.Peers)
                        if (peer.SessionId is { Length: > 0 } id) settings.OwnedSessions.Add(id);
                }

                if (settings.TrySave() is not null) continue;   // leave the request in place so the next launch retries
                var receipt = path + $".imported-{DateTime.Now:yyyyMMdd-HHmmss}";
                File.Move(path, receipt, overwrite: true);
            }
            catch { /* malformed/locked support file: leave it untouched for inspection or retry */ }
        }
    }

    public ChatViewModel ResumeSession(SessionEntry session, bool activatePrimary = true)
    {
        var existing = Chats.FirstOrDefault(c => c.SessionId == session.SessionId && c.Provider == session.Provider);
        if (existing is not null && existing.Status is not ("closed" or "error"))
        {
            if (activatePrimary) OpenChat(existing);
            return existing;
        }
        var replacePrimarySelection = existing is not null && ReferenceEquals(ActiveChat, existing);
        var replaceSecondarySelection = existing is not null && ReferenceEquals(SecondaryActiveChat, existing);
        if (existing is not null)
        {
            existing.Close();
            Chats.Remove(existing);   // replace an exited/error row instead of showing two copies of one transcript
            if (ReferenceEquals(SecondaryActiveChat, existing)) SecondaryActiveChat = null;
        }
        var cwd = Directory.Exists(session.Cwd)
            ? session.Cwd
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // Resume under the login whose home actually holds this transcript, NOT the currently selected one. The CLI
        // resolves --resume inside its own CLAUDE_CONFIG_DIR, so opening a chat from a previous account while another
        // account was selected got "No conversation found with session ID" - switching accounts read as losing every
        // conversation the old one had, though all of them were still on disk the whole time.
        var replacement = NewChat(cwd, session.SessionId, title: session.Title, provider: session.Provider,
            accountId: session.AccountId, activatePrimary: activatePrimary || replacePrimarySelection);
        if (replaceSecondarySelection) SecondaryActiveChat = replacement;
        return replacement;
    }

    public void CloseChat(ChatViewModel chat)
    {
        // Closing a live host snapshots and disposes its peers first. The snapshot is deliberately KEPT: closing one
        // sidebar row must never destroy a multi-agent bridge. Every peer conversation stays resumable from the saved
        // bridge list (and a relaunch rebuilds the host row from it), which is what "close" used to silently delete.
        if (IsBridge && ReferenceEquals(BridgePanes.FirstOrDefault(), chat)) CloseBridge();
        else CloseParkedBridge(chat);
        chat.Close();
        Chats.Remove(chat);
        RefreshRecentProjects();   // a closed untouched chat no longer suppresses its folder suggestion
        if (ActiveChat == chat) ActiveChat = Chats.FirstOrDefault();
        if (SecondaryActiveChat == chat)
            SecondaryActiveChat = Chats.FirstOrDefault(candidate => !ReferenceEquals(candidate, ActiveChat))
                                  ?? Chats.FirstOrDefault();
        if (!IsBridge || !ReferenceEquals(SecondaryActiveChat, BridgePanes.FirstOrDefault()))
            SecondaryShowBridge = false;
        SaveSession();
        RefreshResumableBridges();
    }

    /// <summary>
    /// What the red "Delete" row in the chat menu actually promises: the chat goes away and stays away.
    ///
    /// It used to be a plain <see cref="CloseChat"/>, which removes the sidebar row and nothing else - the session id
    /// stayed in OwnedSessions and the transcript stayed on disk, so the chat reappeared under Projects / Recent the
    /// moment the home screen refreshed. Deleting now also tombstones the session so no VibeCode surface offers it
    /// again. The provider's own transcript file is deliberately left alone: it belongs to the CLI, VibeCode is not
    /// the only thing that reads it, and this app has quite enough ways to lose a conversation already.
    /// </summary>
    public void DeleteChat(ChatViewModel chat)
    {
        var settings = AppSettings.Current;
        // Only this chat's own session. Deleting one sidebar row must not tombstone the peers of a bridge it hosts -
        // CloseChat deliberately keeps that roster resumable, and silently burying it here would be the same class of
        // over-reach that made removing an account delete a year of transcripts.
        if (chat.SessionId is { Length: > 0 } id)
        {
            settings.DeletedSessions.Add(id);
            settings.OwnedSessions.Remove(id);
        }
        CloseChat(chat);          // closes the provider session, drops the row, and persists
        LoadProjects();           // the browser is a background scan; re-run it so the row disappears there too
    }

    // ================= Bridge: multiple coding agents/providers on one project =================

    /// <summary>The roster currently assigned to the Bridge surface. [0] is its host. It stays populated when the
    /// overlay is hidden; other chats' live rosters reside in _parkedBridges until their host is selected.</summary>
    public BridgePaneCollection BridgePanes { get; } = new();

    /// <summary>
    /// The other live panes sharing <paramref name="chat"/>'s bridge, including a roster that is currently parked
    /// rather than shown. Used to apply a host-level decision - muting the Second Brain - across the whole roster,
    /// since every peer is an independent chat that would otherwise keep its own setting.
    /// </summary>
    public IEnumerable<ChatViewModel> PeersOf(ChatViewModel chat)
    {
        var roster = BridgePanes.Contains(chat)
            ? BridgePanes.AsEnumerable()
            : _parkedBridges.Values.FirstOrDefault(bridge => bridge.Panes.Contains(chat))?.Panes.AsEnumerable();
        return roster?.Where(pane => !ReferenceEquals(pane, chat)) ?? Enumerable.Empty<ChatViewModel>();
    }

    /// <summary>
    /// A live roster that is not currently bound to the Bridge surface. Parking is intentionally a view-model-only
    /// operation: its ChatViewModels and provider sessions stay untouched, so returning to the host is a cheap
    /// collection swap instead of three process disposals followed by three transcript replays and CLI launches.
    /// </summary>
    private sealed class LiveBridge
    {
        /// <summary>Observable and mutable, not a snapshot: the SECOND working shell binds straight to a parked
        /// roster's panes, so adding or closing an agent over there has to reach the surface showing it.</summary>
        public required BridgePaneCollection Panes { get; init; }
        public ChatViewModel? PanelChat { get; set; }
        public DateTime Activity { get; set; }
        public required HashSet<ChatViewModel> Errored { get; init; }
        /// <summary>This roster's coordination board (see <see cref="_bridgeBoard"/>).</summary>
        public string Board { get; set; } = DefaultBridgeBoard;
        /// <summary>THIS roster's peer-message budget (see <see cref="_peerTraffic"/>). Per roster, because the
        /// ledger's keys are bare agent numbers: a shared one lets a dead — or merely parked — roster's traffic
        /// decide whether an unrelated agent #2 is allowed to hear from an unrelated agent #1.</summary>
        public required PeerTrafficLedger Peers { get; init; }
    }

    /// <summary>The coordination board every bridge uses unless it is a fork.</summary>
    private const string DefaultBridgeBoard = ".vibecode-bridge.md";

    /// <summary>
    /// The status-board file name of the roster currently on the surface. Normally the one board in the project
    /// root - but a FORKED bridge runs in the same project as the roster it came from, and two rosters sharing one
    /// board would overwrite each other's claims and collide on agent numbers. A fork therefore gets its own
    /// (".vibecode-bridge-2.md", "-3", …), and every prompt, seed and rewrite goes through this name.
    /// </summary>
    private string _bridgeBoard = DefaultBridgeBoard;

    private readonly Dictionary<ChatViewModel, LiveBridge> _parkedBridges = new();

    // ---------------- the second working shell's own Bridge ----------------

    /// <summary>
    /// The host of the roster the SECOND working shell is showing, when that is a DIFFERENT bridge from the one on
    /// the primary window. Null means the two shells share a roster (or shell 2 has no bridge), which is the older
    /// "one bridge spread over two displays" arrangement and still works exactly as before.
    /// <para>
    /// The roster itself stays in <see cref="_parkedBridges"/> rather than becoming a second authoritative surface.
    /// That is the whole trick: a parked roster is already fully live — its processes run, and every "for each live
    /// bridge" sweep (idle timeout, real-time-sharing re-brief, peer muting, Demon bookkeeping) already walks the
    /// park list. Pointing a window at one costs nothing but the binding.
    /// </para>
    /// </summary>
    private ChatViewModel? _secondaryBridgeHost;

    /// <summary>The roster bound to the second shell's Bridge surface. Falls back to the shared roster so that
    /// showing the SAME bridge on both displays keeps behaving as it always has.</summary>
    public BridgePaneCollection SecondaryBridgePanes =>
        SecondaryBridge is { } bridge ? bridge.Panes : BridgePanes;

    private LiveBridge? SecondaryBridge =>
        _secondaryBridgeHost is { } host && _parkedBridges.TryGetValue(host, out var bridge) ? bridge : null;

    /// <summary>True while the two shells are showing two different rosters — the state that used to be impossible.</summary>
    public bool HasSeparateSecondaryBridge => SecondaryBridge is not null;

    /// <summary>Point shell 2 at a live roster that is parked behind its host, without disturbing the primary.</summary>
    private void SetSecondaryBridgeHost(ChatViewModel? host)
    {
        var resolved = host is not null && _parkedBridges.ContainsKey(host) ? host : null;
        if (ReferenceEquals(_secondaryBridgeHost, resolved)) return;
        _secondaryBridgeHost = resolved;
        RaiseSecondaryBridgeUi();
    }

    /// <summary>Drop shell 2's separate roster when it is no longer parked (closed, or pulled onto the primary).</summary>
    private void ReconcileSecondaryBridgeHost()
    {
        if (_secondaryBridgeHost is null) return;
        if (_parkedBridges.ContainsKey(_secondaryBridgeHost)) return;
        _secondaryBridgeHost = null;
        RaiseSecondaryBridgeUi();
    }

    private bool IsParkedBridgePane(ChatViewModel pane) =>
        _parkedBridges.Values.Any(bridge => bridge.Panes.Contains(pane));

    /// <summary>The live roster a pane belongs to, or null. Covers the primary surface and every parked roster
    /// (which includes whatever shell 2 is showing).</summary>
    private LiveBridge? RosterOf(ChatViewModel? pane) =>
        pane is null ? null : TryGetLiveBridge(pane, out var bridge) ? bridge : null;

    // Roster-scoped accessors. Throughout the Bridge code a LiveBridge of null means "the primary surface", whose
    // state lives in the plain fields (_bridgeBoard and friends). Deliberately NOT a LiveBridge copy of the primary:
    // that would take writes to Board/PanelChat/Activity and quietly throw them away.

    /// <summary>The roster a surface-scoped action acts on: shell 2's own roster when it has one, else the primary
    /// (null). Pass the result to the <c>…For</c> accessors below.</summary>
    private LiveBridge? SurfaceRoster(bool secondary) => secondary ? SecondaryBridge : null;

    private BridgePaneCollection PanesFor(LiveBridge? roster) => roster?.Panes ?? BridgePanes;

    private string BoardFor(LiveBridge? roster) => roster?.Board ?? _bridgeBoard;

    private void SetBoardFor(LiveBridge? roster, string board)
    {
        if (roster is null) _bridgeBoard = board;
        else roster.Board = board;
    }

    private HashSet<ChatViewModel> ErroredFor(LiveBridge? roster) => roster?.Errored ?? _bridgeErrored;

    private PeerTrafficLedger PeerTrafficFor(LiveBridge? roster) => roster?.Peers ?? _peerTraffic;

    private void NoteBridgeActivity(LiveBridge? roster)
    {
        if (roster is null) NoteBridgeActivity();
        else roster.Activity = DateTime.Now;
    }

    /// <summary>Announce whichever surface the roster belongs to, so a change made in one shell never repaints
    /// the other one's header with its own roster's numbers.</summary>
    private void RaiseRosterUi(LiveBridge? roster)
    {
        if (roster is null) RaiseBridgeUi();
        else RaiseSecondaryBridgeUi();
    }

    private IEnumerable<LiveBridge> LiveBridges()
    {
        if (BridgePanes.Count > 0)
            yield return new LiveBridge
            {
                Panes = BridgePanes,
                PanelChat = _bridgePanelChat,
                Activity = _bridgeActivity,
                Errored = _bridgeErrored,
                Board = _bridgeBoard,
                Peers = _peerTraffic,
            };
        foreach (var bridge in _parkedBridges.Values) yield return bridge;
    }

    /// <summary>Every live bridge pane, on whichever surface it sits: the roster bound to the Bridge overlay PLUS
    /// every parked roster. A parked roster is not dormant - its provider sessions are running, and it is where the
    /// second working shell's bridge lives - so anything that rescues open chats has to be able to see it.</summary>
    private IEnumerable<ChatViewModel> AllLivePanes() =>
        BridgePanes.Concat(_parkedBridges.Values.SelectMany(bridge => bridge.Panes));

    /// <summary>
    /// Put <paramref name="moved"/> in <paramref name="old"/>'s slot in whichever live roster holds it - the one on
    /// the Bridge surface or a parked one - and return that roster so the caller can brief its peers and persist it.
    /// Null means the chat was not a bridge pane at all.
    ///
    /// Handling the PARKED case is the point. A roster that is merely off the primary surface (which is exactly where
    /// the second working shell's bridge lives) used to be skipped by every account move, so the one bridge the user
    /// was actually watching could stay stranded on an exhausted login while everything else moved across - silently,
    /// because a skipped roster was never counted or reported.
    /// </summary>
    private LiveBridge? ReplaceLivePane(ChatViewModel old, ChatViewModel moved)
    {
        var paneIndex = BridgePanes.IndexOf(old);
        if (paneIndex >= 0)
        {
            BridgePanes[paneIndex] = moved;
            _bridgeErrored.Remove(old);   // the old pane is gone; the replacement announces its own errors
            if (ReferenceEquals(_bridgePanelChat, old)) _bridgePanelChat = moved;
            RaiseBridgeUi();
            return new LiveBridge
            {
                Panes = BridgePanes, PanelChat = _bridgePanelChat, Activity = _bridgeActivity,
                Errored = _bridgeErrored, Board = _bridgeBoard, Peers = _peerTraffic,
            };
        }

        foreach (var (host, bridge) in _parkedBridges.ToList())   // re-keying below mutates the dictionary
        {
            var parkedIndex = bridge.Panes.IndexOf(old);
            if (parkedIndex < 0) continue;
            bridge.Panes[parkedIndex] = moved;
            bridge.Errored.Remove(old);
            if (ReferenceEquals(bridge.PanelChat, old)) bridge.PanelChat = moved;
            // The park list is keyed by the roster's HOST chat, and shell 2 points at it by that same key. Moving the
            // host builds a NEW ChatViewModel, so both have to be re-pointed or the roster becomes unreachable: the
            // second shell would keep rendering a closed pane, and re-selecting the host would spawn a second bridge.
            if (ReferenceEquals(host, old))
            {
                var wasSecondaryRoster = ReferenceEquals(_secondaryBridgeHost, old);
                _parkedBridges.Remove(host);
                _parkedBridges[moved] = bridge;
                // Assigned directly, not through SetSecondaryBridgeHost: that resolves the key against _parkedBridges
                // and would quietly null the pointer if it ran before the re-key above.
                if (wasSecondaryRoster) _secondaryBridgeHost = moved;
            }
            RaiseSecondaryBridgeUi();
            return bridge;
        }

        return null;
    }

    private bool TryGetLiveBridge(ChatViewModel pane, out LiveBridge bridge)
    {
        if (BridgePanes.Contains(pane))
        {
            bridge = new LiveBridge
            {
                Panes = BridgePanes,
                PanelChat = _bridgePanelChat,
                Activity = _bridgeActivity,
                Errored = _bridgeErrored,
                Board = _bridgeBoard,
                Peers = _peerTraffic,
            };
            return true;
        }

        foreach (var parked in _parkedBridges.Values)
        {
            if (!parked.Panes.Contains(pane)) continue;
            bridge = parked;
            return true;
        }

        bridge = null!;
        return false;
    }

    /// <summary>Every non-host provider session owned by every live roster, including rosters parked behind chats.</summary>
    public IEnumerable<ChatViewModel> LiveBridgePeers =>
        LiveBridges().SelectMany(bridge => bridge.Panes.Skip(1)).Distinct();

    private ChatViewModel? _bridgePanelChat;
    /// <summary>The chat whose artifacts/todos fill the right panel. In Bridge this follows the pane the user most
    /// recently interacted with; outside Bridge it is simply ActiveChat.</summary>
    public ChatViewModel? BridgePanelChat =>
        _bridgePanelChat is not null && BridgePanes.Contains(_bridgePanelChat)
            ? _bridgePanelChat
            : BridgePanes.FirstOrDefault();

    public ChatViewModel? PanelChat => ShowBridge ? BridgePanelChat : ActiveChat;

    /// <summary>The right-panel chat for shell 2. Falls back to the shared roster's choice while both shells show
    /// the same bridge, so the older arrangement is untouched.</summary>
    public ChatViewModel? SecondaryBridgePanelChat => SecondaryBridge is { } bridge
        ? bridge.PanelChat is not null && bridge.Panes.Contains(bridge.PanelChat)
            ? bridge.PanelChat
            : bridge.Panes.FirstOrDefault()
        : BridgePanelChat;

    public void SelectBridgePane(ChatViewModel pane)
    {
        // A pane clicked in shell 2's own roster selects within THAT roster; the primary's panel must not follow it.
        if (SecondaryBridge is { } secondary && secondary.Panes.Contains(pane))
        {
            if (ReferenceEquals(secondary.PanelChat, pane)) return;
            secondary.PanelChat = pane;
            Raise(nameof(SecondaryBridgePanelChat));
            return;
        }
        if (!BridgePanes.Contains(pane) || ReferenceEquals(_bridgePanelChat, pane)) return;
        _bridgePanelChat = pane;
        Raise(nameof(PanelChat));
        Raise(nameof(BridgePanelChat));
    }
    /// <summary>A roster currently owns the Bridge surface (the overlay itself may be hidden).</summary>
    public bool IsBridge => BridgePanes.Count > 0;
    public bool CanAddBridgeAgent =>
        !IsDemonMode   // a Demon team is a fixed roster; a seventeenth agent would have no role and no lane
        && BridgeAgentPolicy.CanAdd(BridgePanes.Count, AppSettings.Current.BridgeAgentLimit);
    public string BridgeAgentName => BridgePanes.FirstOrDefault()?.AgentDisplay ?? ActiveChat?.AgentDisplay ?? "Agent";
    public string AddBridgeAgentText => "Add agent";
    /// <summary>Tooltip for Add agent. When the roster is at the Settings cap, spell out the limit so the
    /// button never looks "broken" — a click also opens a dialog that points at Settings.</summary>
    public string AddBridgeAgentToolTip =>
        CanAddBridgeAgent
            ? $"Choose Claude Code, OpenAI Codex, Kimi Code, or Grok (up to {AppSettings.Current.BridgeAgentLimit} agents)"
            : $"Bridge is full ({BridgePanes.Count} / {AppSettings.Current.BridgeAgentLimit}). Raise the limit in Settings → Bridge.";
    /// <summary>The Announce affordance is live whenever a Bridge roster owns the surface — except in Demon Mode,
    /// where broadcasting to all sixteen would be a second way for the user to talk to the read-only workers.</summary>
    public bool CanAnnounceToBridge => IsBridge && !IsDemonMode;
    public string BridgeSummary => SummaryOf(BridgePanes);

    /// <summary>The full working directory shared by every pane in the active Bridge.</summary>
    public string BridgeProjectPath => BridgePanes.FirstOrDefault()?.Cwd ?? "";

    public bool BridgeUsesClaude => BridgePanes.Any(p => p.Provider == "claude");
    public bool BridgeUsesCodex => BridgePanes.Any(p => p.Provider == "codex");
    public bool BridgeUsesKimi => BridgePanes.Any(p => p.Provider == "kimi");
    public bool BridgeUsesGrok => BridgePanes.Any(p => p.Provider == "grok");
    /// <summary>Unique xAI accounts backing the Grok panes in this Bridge. Multiple agents can share one account,
    /// so quota rows are grouped by account id instead of pretending per-session token totals are account usage.</summary>
    public IEnumerable<GrokAccountInfo> BridgeGrokAccounts => GrokAccountsOf(BridgePanes);
    public string BridgeGrokAccountCountText => GrokAccountCountTextOf(BridgePanes);
    public string BridgeGrokUsageSummary => GrokUsageSummaryOf(BridgePanes);

    // ---- the same header, computed for whichever roster a surface is showing ----

    private static string SummaryOf(IReadOnlyList<ChatViewModel> panes)
    {
        if (panes.Count == 0) return "";
        var groups = panes.GroupBy(p => p.AgentDisplay)
            .Select(g => (Name: g.Key, Count: g.Count()))
            .ToList();
        var roster = groups.Count == 1
            ? $"{groups[0].Count} {groups[0].Name} agent{(groups[0].Count == 1 ? "" : "s")}"
            : $"{panes.Count} agents ({string.Join(", ", groups.Select(g => $"{g.Count} {g.Name}"))})";
        return $"{roster} on this project — each knows the others are working";
    }

    private static IEnumerable<GrokAccountInfo> GrokAccountsOf(IReadOnlyList<ChatViewModel> panes) => panes
        .Where(p => p.IsGrok)
        .Select(p => p.GrokAccount)
        .OfType<GrokAccountInfo>()
        .GroupBy(account => account.Id, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.First());

    private static string GrokAccountCountTextOf(IReadOnlyList<ChatViewModel> panes)
    {
        var count = GrokAccountsOf(panes).Count();
        return $"{count} Grok account{(count == 1 ? "" : "s")}";
    }

    private static string GrokUsageSummaryOf(IReadOnlyList<ChatViewModel> panes)
    {
        var accounts = GrokAccountsOf(panes).ToList();
        if (accounts.Count == 0) return "usage unavailable";
        if (accounts.Count == 1) return accounts[0].UsageSummary;
        var known = accounts.Where(account => account.UsagePercent is not null).ToList();
        return known.Count == 0
            ? $"{accounts.Count} accounts · usage unavailable"
            : $"{accounts.Count} accounts · max {known.Max(account => account.UsagePercent)}%";
    }

    // ---- shell 2's header, when it is showing a roster of its own ----

    public bool SecondaryIsBridge => SecondaryBridgePanes.Count > 0;
    public string SecondaryBridgeSummary => SummaryOf(SecondaryBridgePanes);
    public string SecondaryBridgeProjectPath => SecondaryBridgePanes.FirstOrDefault()?.Cwd ?? "";
    public bool SecondaryBridgeUsesClaude => SecondaryBridgePanes.Any(p => p.Provider == "claude");
    public bool SecondaryBridgeUsesCodex => SecondaryBridgePanes.Any(p => p.Provider == "codex");
    public bool SecondaryBridgeUsesKimi => SecondaryBridgePanes.Any(p => p.Provider == "kimi");
    public bool SecondaryBridgeUsesGrok => SecondaryBridgePanes.Any(p => p.Provider == "grok");
    public IEnumerable<GrokAccountInfo> SecondaryBridgeGrokAccounts => GrokAccountsOf(SecondaryBridgePanes);
    public string SecondaryBridgeGrokAccountCountText => GrokAccountCountTextOf(SecondaryBridgePanes);
    public string SecondaryBridgeGrokUsageSummary => GrokUsageSummaryOf(SecondaryBridgePanes);
    public bool SecondaryHasMinimizedBridgePanes => SecondaryBridgePanes.Any(p => p.BridgeMinimized);

    /// <summary>Demon Mode refuses the second display outright, so a roster shown over there is never a Demon wall
    /// and its Announce/Add/Fork affordances need no Demon caveat of their own.</summary>
    public bool SecondaryCanAnnounceToBridge => SecondaryIsBridge && !SecondaryShowsDemonRoster;
    public bool SecondaryCanAddBridgeAgent => !SecondaryShowsDemonRoster
        && BridgeAgentPolicy.CanAdd(SecondaryBridgePanes.Count, AppSettings.Current.BridgeAgentLimit);
    public string SecondaryAddBridgeAgentToolTip =>
        SecondaryCanAddBridgeAgent
            ? $"Choose Claude Code, OpenAI Codex, Kimi Code, or Grok (up to {AppSettings.Current.BridgeAgentLimit} agents)"
            : $"Bridge is full ({SecondaryBridgePanes.Count} / {AppSettings.Current.BridgeAgentLimit}). Raise the limit in Settings → Bridge.";
    public bool SecondaryCanForkBridge =>
        SecondaryIsBridge && !SecondaryShowsDemonRoster && SecondaryBridgePanes.Any(p => p.CanFork);
    public string SecondaryForkBridgeToolTip => SecondaryCanForkBridge
        ? "Fork this bridge — a second roster that starts with everything these agents already know"
        : ForkBridgeUnavailableReason(SecondaryBridgePanes, SecondaryShowsDemonRoster);

    private bool SecondaryShowsDemonRoster => SecondaryBridgePanes.Any(p => p.IsDemonOrchestrator);

    private void RaiseBridgeGrokUsage()
    {
        Raise(nameof(BridgeGrokAccounts));
        Raise(nameof(BridgeGrokAccountCountText));
        Raise(nameof(BridgeGrokUsageSummary));
        Raise(nameof(SecondaryBridgeGrokAccounts));
        Raise(nameof(SecondaryBridgeGrokAccountCountText));
        Raise(nameof(SecondaryBridgeGrokUsageSummary));
    }

    /// <summary>Announce shell 2's whole Bridge header. Cheap enough to fire wholesale — it is only ever raised on a
    /// roster change, and every one of these is a trivial projection of the roster.</summary>
    private void RaiseSecondaryBridgeUi()
    {
        Raise(nameof(SecondaryBridgePanes));
        Raise(nameof(HasSeparateSecondaryBridge));
        Raise(nameof(SecondaryIsBridge));
        Raise(nameof(SecondaryBridgeSummary));
        Raise(nameof(SecondaryBridgeProjectPath));
        Raise(nameof(SecondaryBridgeUsesClaude));
        Raise(nameof(SecondaryBridgeUsesCodex));
        Raise(nameof(SecondaryBridgeUsesKimi));
        Raise(nameof(SecondaryBridgeUsesGrok));
        Raise(nameof(SecondaryBridgeGrokAccounts));
        Raise(nameof(SecondaryBridgeGrokAccountCountText));
        Raise(nameof(SecondaryBridgeGrokUsageSummary));
        Raise(nameof(SecondaryHasMinimizedBridgePanes));
        Raise(nameof(SecondaryCanAnnounceToBridge));
        Raise(nameof(SecondaryCanAddBridgeAgent));
        Raise(nameof(SecondaryAddBridgeAgentToolTip));
        Raise(nameof(SecondaryCanForkBridge));
        Raise(nameof(SecondaryForkBridgeToolTip));
        Raise(nameof(SecondaryBridgePanelChat));
    }
    /// <summary>Rows used by the bridge's UniformGrid. WPF's fully automatic sizing treats two panes as a 2x2
    /// square, leaving the bottom half empty. Pick the row count from the number of panes that are actually visible
    /// so two panes form one full-height row and a focused pane still fills the entire bridge.</summary>
    public int BridgeGridRows
    {
        get
        {
            var visible = BridgePanes.Count(p => p.BridgePaneShown);
            return visible switch { <= 2 => 1, <= 6 => 2, _ => 3 };
        }
    }

    /// <summary>True when at least one bridge agent is user-minimized (restore chips in the bridge header).</summary>
    public bool HasMinimizedBridgePanes => BridgePanes.Any(p => p.BridgeMinimized);

    /// <summary>Re-read whether this roster can be forked. Fired on every roster change AND whenever a pane earns a
    /// session id, which is the moment an unforkable bridge becomes a forkable one.</summary>
    private void RaiseBridgeFork()
    {
        Raise(nameof(CanForkBridge));
        Raise(nameof(ForkBridgeToolTip));
    }

    private void RaiseBridgeUi()
    {
        Raise(nameof(IsBridge));
        Raise(nameof(CanAnnounceToBridge));
        RaiseBridgeFork();
        RefreshBridgeLimit();
        Raise(nameof(BridgeAgentName));
        Raise(nameof(AddBridgeAgentText));
        Raise(nameof(BridgeSummary));
        Raise(nameof(BridgeProjectPath));
        Raise(nameof(BridgeUsesClaude));
        Raise(nameof(BridgeUsesCodex));
        Raise(nameof(BridgeUsesKimi));
        Raise(nameof(BridgeUsesGrok));
        // The Kimi pill is hidden until a quota read succeeds, so a resumed bridge whose Kimi agent is sitting idle
        // would never show one. Kick a (throttled) read whenever the roster changes.
        if (BridgeUsesKimi) KimiUsageService.Instance.Refresh();
        RaiseBridgeGrokUsage();
        Raise(nameof(BridgeGridRows));
        Raise(nameof(HasMinimizedBridgePanes));
        // Every roster change can also change whether the DEMON roster is the one on screen (park / un-park), which
        // is what the demon wall and its badge are drawn from.
        RaiseDemonUi();
        Raise(nameof(PanelChat));
        Raise(nameof(BridgePanelChat));
        // Shell 2 shares this roster whenever it has none of its own, and its Grok/limit numbers are read from the
        // same settings — so a primary roster change is also a second-shell header change.
        ReconcileSecondaryBridgeHost();
        RaiseSecondaryBridgeUi();
        RefreshResumableBridges();   // the live bridge is excluded from the dormant list, so it moves with every change
    }

    /// <summary>Refresh the add-agent affordance after Settings changes the live Bridge ceiling.</summary>
    public void RefreshBridgeLimit()
    {
        Raise(nameof(CanAddBridgeAgent));
        Raise(nameof(AddBridgeAgentToolTip));
    }

    private bool _realtimeSharingSnapshot = AppSettings.Current.BridgeRealtimeSharing;

    /// <summary>Re-brief every live bridge pane after Settings flips real-time sharing, so the change reaches bridges
    /// already running: the refreshed system prompt covers any later restart, a prelude note rides the next message of
    /// the live session, and turning it on adds the "## Live activity" board to each bridge's coordination file.</summary>
    public void RefreshBridgeRealtimeSharing()
    {
        var enabled = AppSettings.Current.BridgeRealtimeSharing;
        if (enabled == _realtimeSharingSnapshot) return;
        _realtimeSharingSnapshot = enabled;
        foreach (var bridge in LiveBridges())
        {
            if (enabled && bridge.Panes.FirstOrDefault()?.Cwd is { Length: > 0 } cwd) EnsureLiveActivitySection(cwd, bridge.Board);
            var roster = bridge.Panes.Select(BridgeNumberOf).Where(n => n > 0).ToList();
            var managerNumber = ManagerNumberIn(bridge.Panes);
            foreach (var pane in bridge.Panes)
            {
                var n = BridgeNumberOf(pane);
                if (n <= 0) continue;
                pane.AppendSystemPrompt = BridgePrompt(bridge.Board, pane, n, roster, managerNumber);
                var note = enabled
                    ? "[BRIDGE] Real-time sharing was just turned ON. From now on also keep exactly ONE compact block under " +
                      "\"## Live activity\" in `" + bridge.Board + "`: an `Agent #" + n + " » <file path(s)>` line plus 1-2 short " +
                      "lines on what you're adding/changing there. Rewrite it in place at checkpoints (start/switch/finish a " +
                      "file) — never append history. Write your first block before your next edit, and glance at peers' lines " +
                      "before touching a file they list."
                    : "[BRIDGE] Real-time sharing was just turned OFF. Stop updating the \"## Live activity\" board and go back " +
                      "to high-level coordination only via your \"## Active\" block. Don't log individual edits anywhere.";
                pane.Prelude = string.IsNullOrEmpty(pane.Prelude) ? note : pane.Prelude + "\n" + note;
            }
        }
    }

    /// <summary>Project the live roster's activity onto its host so the single sidebar row represents every pane.</summary>
    private void RefreshBridgeWorkingCue()
        => RefreshBridgeWorkingCue(BridgePanes);

    private static void RefreshBridgeWorkingCue(IReadOnlyList<ChatViewModel> panes)
    {
        if (panes.FirstOrDefault() is { } host)
            host.BridgeHasWorkingPane = panes.Any(p => p.IsWorking);
    }

    private string _bridgeHint = "";
    /// <summary>Transient status line shown in the bridge header (dictation download/errors etc.). The bridge has no
    /// composer hint of its own, so mic feedback would otherwise be invisible here. Auto-cleared by the code-behind.</summary>
    public string BridgeHint { get => _bridgeHint; set => Set(ref _bridgeHint, value); }

    private bool _showBridge;
    /// <summary>Whether the split-pane overlay is currently on screen. Independent of IsBridge: a bridge can be
    /// running in the background (IsBridge true) while the user looks at another chat or home (ShowBridge false).</summary>
    public bool ShowBridge
    {
        get => _showBridge;
        set { if (Set(ref _showBridge, value)) { Raise(nameof(PanelChat)); RequestSave(); } }
    }

    // Live-process idle auto-close: a backgrounded bridge disposes its processes after this long with no activity
    // (no messages sent, no pane working, not reopened) so forgotten bridges don't run agents forever. Its saved
    // conversation does NOT expire. Default 5h; the
    // VIBECODE_BRIDGE_TIMEOUT_SECONDS env var overrides it (used by automated verification to test the auto-close).
    private static TimeSpan BridgeIdleTimeout =>
        int.TryParse(Environment.GetEnvironmentVariable("VIBECODE_BRIDGE_TIMEOUT_SECONDS"), out var n) && n > 0
            ? TimeSpan.FromSeconds(n)
            : TimeSpan.FromHours(5);
    private DateTime _bridgeActivity;
    private System.Windows.Threading.DispatcherTimer? _bridgeIdleTimer;

    /// <summary>Mark the bridge as "just used" so the idle timeout restarts.</summary>
    public void NoteBridgeActivity() => _bridgeActivity = DateTime.Now;

    /// <summary>
    /// Remove the displayed roster from the bound collection without closing a single provider session. The roster is
    /// retained by host identity and can be restored synchronously when that chat is selected again.
    /// </summary>
    private void ParkActiveBridge(bool clearSurface = true)
    {
        if (BridgePanes.FirstOrDefault() is not { } host) return;
        // Shell 2 was showing this roster. Parking it used to blank that window; now the roster simply keeps the
        // second display and only leaves the PRIMARY surface, which is all the caller ever meant.
        var keepOnSecondary = SecondaryShowBridge && ReferenceEquals(SecondaryActiveChat, host);
        _parkedBridges[host] = new LiveBridge
        {
            Panes = new BridgePaneCollection(BridgePanes),
            PanelChat = _bridgePanelChat,
            Activity = _bridgeActivity == default ? DateTime.Now : _bridgeActivity,
            Errored = new HashSet<ChatViewModel>(_bridgeErrored),
            Board = _bridgeBoard,
            Peers = _peerTraffic,   // its own message budget travels with it, exactly like its board
        };
        if (keepOnSecondary) SetSecondaryBridgeHost(host);
        if (clearSurface) BridgePanes.ReplaceAll(Array.Empty<ChatViewModel>());
        _bridgePanelChat = null;
        _bridgeErrored.Clear();
        _bridgeActivity = default;
        _bridgeBoard = DefaultBridgeBoard;   // the parked roster took its board with it; whatever comes next starts on the default
        _peerTraffic = new PeerTrafficLedger();   // …and so did its traffic; the next roster here starts on a clean budget
        // Parking a Demon roster does not end the team, but it does take it off the surface — and the demon wall
        // must go with it, whatever the caller does next.
        RaiseDemonUi();
    }

    /// <summary>Swap a parked roster back into the bound collection; no transcript IO or CLI launch occurs.</summary>
    private bool ActivateParkedBridge(ChatViewModel host, bool showPrimary = true)
    {
        // Shell 2 shows a parked roster WHERE IT IS. Hauling it onto the primary surface first is what used to evict
        // the bridge the user had on the other display; there is nothing to move, only a window to point at it.
        if (!showPrimary)
        {
            if (!_parkedBridges.ContainsKey(host)) return false;
            SetSecondaryBridgeHost(host);
            SecondaryActiveChat = host;
            SecondaryShowBridge = true;
            NoteBridgeActivity(_parkedBridges[host]);
            StartBridgeIdleTimer();
            RaiseSecondaryBridgeUi();
            return true;
        }

        if (!_parkedBridges.Remove(host, out var bridge)) return false;
        BridgePanes.ReplaceAll(bridge.Panes);
        _bridgeBoard = bridge.Board;   // a fork keeps writing to its own board, not the project's default one
        _peerTraffic = bridge.Peers;   // and it comes back with the traffic it accrued while parked
        _bridgePanelChat = bridge.PanelChat is not null && BridgePanes.Contains(bridge.PanelChat)
            ? bridge.PanelChat
            : host;
        _bridgeErrored.UnionWith(bridge.Errored);
        _bridgeActivity = bridge.Activity == default ? DateTime.Now : bridge.Activity;
        RefreshBridgeWorkingCue();
        ShowBridge = true;   // the !showPrimary case returned above, having left the roster parked
        NoteBridgeActivity();
        StartBridgeIdleTimer();
        RaiseBridgeUi();
        return true;
    }

    /// <summary>Hide the overlay but KEEP the bridge running in the background (peers stay alive). Used when the
    /// user navigates away - the bridge is reachable again by opening its host chat.</summary>
    public void HideBridge() { if (ShowBridge) ShowBridge = false; }

    /// <summary>Go to the home screen; a running bridge is left alive in the background.</summary>
    public void GoHome()
    {
        HideBridge();
        ActiveChat = null;
        RefreshRecentProjects();   // instant MRU result from memory
        LoadProjects();            // then reconcile sessions/folders written since the last scan
    }

    /// <summary>Choose a sensible chat for the optional second full shell without changing the primary surface.</summary>
    public void EnsureSecondarySelection(bool preferBridge = false)
    {
        if (SecondaryActiveChat is null || !Chats.Contains(SecondaryActiveChat))
        {
            SecondaryActiveChat = preferBridge && BridgePanes.FirstOrDefault() is { } host
                ? host
                : Chats.FirstOrDefault(chat => !ReferenceEquals(chat, ActiveChat)) ?? ActiveChat;
        }

        if (preferBridge && ReferenceEquals(SecondaryActiveChat, BridgePanes.FirstOrDefault()))
            SecondaryShowBridge = true;
        else if (!ReferenceEquals(SecondaryActiveChat, BridgePanes.FirstOrDefault()))
            SecondaryShowBridge = false;
    }

    public void GoSecondaryHome()
    {
        SecondaryShowBridge = false;
        SecondaryActiveChat = null;
        RefreshRecentProjects();
        LoadProjects();
    }

    /// <summary>Navigate only the second full shell. Live Bridge panes remain shared across both windows.</summary>
    public void OpenSecondaryChat(ChatViewModel chat)
    {
        if (!Chats.Contains(chat)) return;
        SecondaryActiveChat = chat;
        if (IsBridge && ReferenceEquals(chat, BridgePanes.FirstOrDefault()))
        {
            SecondaryShowBridge = true;
            NoteBridgeActivity();
            return;
        }

        if (_parkedBridges.ContainsKey(chat) || HasSavedBridgeFor(chat))
        {
            ActivateBridgeOnSecondary(chat);
            return;
        }

        SecondaryShowBridge = false;
    }

    /// <summary>Open an existing chat. Opening a live bridge host re-enters the bridge overlay;
    /// opening anything else hides the overlay (bridge keeps running in the background).</summary>
    public void OpenChat(ChatViewModel chat)
    {
        if (IsBridge && ReferenceEquals(chat, BridgePanes[0]))
        {
            NoteBridgeActivity();
            ActiveChat = chat;
            ShowBridge = true;      // returned to a live bridge
            return;
        }

        // A roster already visited during this run is parked, not dormant. Swap it into view without touching any
        // provider process; this is the hot path when moving between chats that each own a four-agent bridge.
        if (_parkedBridges.ContainsKey(chat))
        {
            if (IsBridge) ParkActiveBridge(clearSurface: false);
            ActiveChat = chat;
            if (ActivateParkedBridge(chat)) return;
        }

        // A saved-only host still needs its one initial resume. Park any current roster first so its agents continue
        // working while the saved roster reconnects.
        if (HasSavedBridgeFor(chat))
        {
            if (IsBridge) ParkActiveBridge();
            ActiveChat = chat;
            if (RestoreBridge(chat)) return;
        }
        HideBridge();
        ActiveChat = chat;
    }

    /// <summary>
    /// True when Bridge on this chat would spawn a brand-new two-agent roster (so the UI should pick agent 2's
    /// provider first). False for resume/reopen/show-existing paths that never create a peer.
    /// </summary>
    public bool WouldStartFreshBridge(ChatViewModel? chat)
    {
        if (chat is null) return false;
        if (_parkedBridges.ContainsKey(chat)) return false;
        if (IsBridge && ReferenceEquals(chat, BridgePanes.FirstOrDefault())) return false;
        if (!IsBridge && HasSavedBridgeFor(chat)) return false;
        // Fresh start (or park-current-then-start on a different host).
        return true;
    }

    /// <summary>Turn the current chat into a two-agent bridge. Agent 1 is the host; agent 2 uses
    /// <paramref name="peerProvider"/> (null = same provider as the host). Additional peers can still be
    /// mixed later via Add agent while sharing the same project and coordination roster.</summary>
    public void ActivateBridge(string? peerProvider = null)
    {
        if (ActiveChat is null) return;

        if (_parkedBridges.ContainsKey(ActiveChat))
        {
            if (IsBridge) ParkActiveBridge(clearSurface: false);
            ActivateParkedBridge(ActiveChat);
            return;
        }

        // "Bridge" on a host with a dormant snapshot means resume it, not replace its saved peers with a fresh agent.
        if (!IsBridge && HasSavedBridgeFor(ActiveChat))
        {
            RestoreBridge(ActiveChat);
            return;
        }

        // A hidden bridge keeps running while the user navigates to another chat. If they explicitly choose
        // Bridge from that new chat (often after switching accounts), park the old roster instead of silently
        // returning because the surface can display only one roster. Parking leaves every provider process running.
        if (IsBridge)
        {
            if (ReferenceEquals(ActiveChat, BridgePanes[0]))
            {
                ShowBridge = true;
                NoteBridgeActivity();
                return;
            }

            ParkActiveBridge();
        }

        var host = ActiveChat;
        _bridgeBoard = DefaultBridgeBoard;                 // only a fork takes a numbered board
        _peerTraffic.Clear();                              // a brand-new roster starts on a brand-new message budget
        WriteBridgeFile(host.Cwd);                         // fresh coordination file for this bridge
        AppendPeerToBridgeFile(host.Cwd, 1);              // seed agent 1 so the roster always lists everyone
        host.BridgeLabel = AgentLabel(host, 1);
        host.IsBridgeHost = true;
        host.IsBridgeManager = false;                     // a fresh bridge starts flat; the user crowns a manager
        host.Prelude = BridgeJoinPrelude(host, 1, new[] { 1, 2 }, 0);   // host learns about its peer on the next message
        host.Items.Add(new DividerItem { Label = $"🔗 Bridge activated — you are {host.BridgeLabel}" });
        BridgePanes.Add(host);
        AddBridgeAgent(peerProvider);                     // spawn agent 2 (any supported provider)
        ShowBridge = true;
        NoteBridgeActivity();
        StartBridgeIdleTimer();
        RaiseBridgeUi();
    }

    /// <summary>Activate or resume a Bridge from the second full shell without changing the primary active chat.
    /// <paramref name="peerProvider"/> is used only when spawning a fresh agent 2 (same rules as <see cref="ActivateBridge"/>).</summary>
    public bool ActivateBridgeOnSecondary(ChatViewModel host, string? peerProvider = null)
    {
        if (!Chats.Contains(host)) return false;
        SecondaryActiveChat = host;

        // Already running and parked: just point this window at it. The primary keeps whatever it was showing.
        if (_parkedBridges.ContainsKey(host)) return ActivateParkedBridge(host, showPrimary: false);

        // The roster the PRIMARY is showing. Both shells on one bridge stays supported — that is the older
        // "spread one Bridge across two displays" arrangement, and it is not what this branch changes.
        if (IsBridge && ReferenceEquals(host, BridgePanes.FirstOrDefault()))
        {
            SetSecondaryBridgeHost(null);
            SecondaryShowBridge = true;
            NoteBridgeActivity();
            return true;
        }

        if (HasSavedBridgeFor(host)) return RestoreBridge(host, showPrimary: false);

        // A brand-new roster for THIS window. It is built straight into a parked slot, so the bridge on the primary
        // display is never touched: two live rosters, one per shell, which is the whole point.
        var bridge = new LiveBridge
        {
            Panes = new BridgePaneCollection(),
            Errored = new HashSet<ChatViewModel>(),
            Activity = DateTime.Now,
            Board = FreeBridgeBoard(host.Cwd),   // its own board only if this project already has a roster
            Peers = new PeerTrafficLedger(),
        };
        _parkedBridges[host] = bridge;
        WriteBridgeFile(host.Cwd, bridge.Board);
        AppendPeerToBridgeFile(host.Cwd, bridge.Board, 1);
        host.BridgeLabel = AgentLabel(host, 1);
        host.IsBridgeHost = true;
        host.IsBridgeManager = false;
        host.Prelude = BridgeJoinPrelude(bridge.Board, host, 1, new[] { 1, 2 }, 0);
        host.Items.Add(new DividerItem { Label = $"🔗 Bridge activated — you are {host.BridgeLabel}" });
        bridge.Panes.Add(host);
        bridge.PanelChat = host;
        AddBridgeAgent(peerProvider, bridge);
        SetSecondaryBridgeHost(host);
        SecondaryShowBridge = true;
        NoteBridgeActivity(bridge);
        StartBridgeIdleTimer();
        RaiseSecondaryBridgeUi();
        RefreshResumableBridges();
        return true;
    }

    /// <summary>Add another agent to the bridge up to the configured limit. A null provider matches
    /// the host; the header picker can explicitly add Claude, Codex, Kimi, or Grok to an existing bridge.</summary>
    public void AddBridgeAgent(string? provider = null) => AddBridgeAgent(provider, null);

    /// <summary>Add an agent to whatever Bridge the SECOND working shell is showing. Falls through to the shared
    /// roster when both shells are on the same bridge, so the button means the same thing in either window.</summary>
    public void AddBridgeAgentOnSecondary(string? provider = null) => AddBridgeAgent(provider, SecondaryBridge);

    /// <summary><paramref name="target"/> null means the primary surface; pass shell 2's roster to grow the bridge
    /// on the other display without disturbing this one.</summary>
    private void AddBridgeAgent(string? provider, LiveBridge? target)
    {
        var panes = PanesFor(target);
        var board = BoardFor(target);
        if (panes.Count == 0) return;
        if (!BridgeAgentPolicy.CanAdd(panes.Count, AppSettings.Current.BridgeAgentLimit)) return;
        ResetBridgeExpand(target);         // a new pane must not land hidden behind a focused peer - back to the grid
        var host = panes[0];
        var cwd = host.Cwd;
        provider = provider?.Trim().ToLowerInvariant() switch
        {
            "claude" => "claude",
            "codex" => "codex",
            "kimi" => "kimi",
            "grok" => "grok",
            _ => host.Provider,
        };
        var k = panes.Count + 1;          // compact roster => the new pane is always the next contiguous number
        var roster = panes.Select(BridgeNumberOf).Where(n => n > 0).ToList();   // the live roster the new peer joins…
        roster.Add(k);                    // …plus itself
        AppendPeerToBridgeFile(cwd, board, k);   // seed the new peer's identity so everyone sees it in the roster
        var sameProvider = string.Equals(provider, host.Provider, StringComparison.OrdinalIgnoreCase);
        var managerNumber = ManagerNumberIn(panes);
        var chat = new ChatViewModel(cwd, accountId: sameProvider ? host.AccountId : null, provider: provider);
        chat.Title = $"Bridge · {chat.AgentDisplay} {k}";
        // A peer works on the host's task, so muting the host has to mute everything it brings along - otherwise
        // the conversation is excluded from the brain while its bridge agents keep writing the same work into it.
        chat.ExcludeFromMemory = host.ExcludeFromMemory;
        chat.BridgeLabel = AgentLabel(chat, k);
        chat.AppendSystemPrompt = BridgePrompt(board, chat, k, roster, managerNumber);
        chat.SetMode(host.Mode);       // new peers inherit the host's permission mode instead of defaulting to "ask"
        if (sameProvider)
        {
            chat.Model = host.Model;   // same-provider peers inherit the host's model + reasoning effort
            chat.Effort = host.Effort; // (plain setters: never rewrite the global defaults while cloning a live pane)
            chat.FastMode = host.FastMode;   // …and its fast mode, which is per chat rather than one shared setting
        }
        // A cross-provider peer keeps the defaults loaded by its own constructor; a Codex model id is invalid for Claude.
        Track(chat);
        // Actively tell already-running peers that another agent joined (their system prompt is frozen at spawn,
        // so we push it via the one-shot Prelude that rides their next message) + a visible divider
        var activeCount = panes.Count + 1;   // compact numbering means the new identity and active count match
        foreach (var peer in panes)
        {
            var self = BridgeNumberOf(peer);
            var activePeers = string.Join(", ", roster.Where(n => n != self).Select(n => $"agent #{n}"));
            var note = $"[BRIDGE] {chat.AgentDisplay} agent #{k} joined — {activeCount} agents now active. You are " +
                       $"still {peer.AgentDisplay} agent #{self}; your active peers are {activePeers}. No action needed; " +
                       "just don't pick up their area.";
            peer.Prelude = string.IsNullOrEmpty(peer.Prelude) ? note : peer.Prelude + "\n" + note;
            peer.AppendSystemPrompt = BridgePrompt(board, peer, self, roster, managerNumber); // current session gets Prelude; any restart gets the same full roster
            peer.Items.Add(new DividerItem { Label = $"🔗 {chat.AgentDisplay} #{k} joined the bridge" });
        }
        panes.Add(chat);
        RefreshBridgeWorkingCue(panes);
        chat.Start();
        // Only while a crown is live: if the user stepped the manager down, new peers just join as equals — no
        // "fold it into the plan" update, and no auto-dispatch chain. Re-read the crown (not a stale managerNumber).
        if (ManagerOf(panes) is { IsBridgeManager: true } mgr && !ReferenceEquals(mgr, chat))
            SendManagerUpdate(mgr,
                $"{chat.AgentDisplay} agent #{k} just JOINED the bridge and is idle ({activeCount} agents now). " +
                $"Fold it into the plan and put it to work: reply with a @@DISPATCH agent={k} block giving it an " +
                "unclaimed lane (or rebalance lanes if that helps the project finish sooner).");
        NoteBridgeActivity(target);
        SaveBridge(panes, board);  // keep the temp-save current
        RaiseRosterUi(target);
    }

    // ---------------- forking a whole bridge ----------------

    /// <summary>Whether Fork bridge can run: a live roster, not a Demon team (a fixed, deliberately unresumable
    /// roster), and at least one pane whose provider session can actually be branched.</summary>
    public bool CanForkBridge => IsBridge && !IsDemonMode && BridgePanes.Any(p => p.CanFork);

    public string ForkBridgeToolTip => CanForkBridge
        ? "Fork this bridge — a second roster that starts with everything these agents already know"
        : ForkBridgeUnavailableReason(BridgePanes, IsDemonMode);

    private static string ForkBridgeUnavailableReason(IReadOnlyList<ChatViewModel> panes, bool demon) =>
        demon
            ? "A Demon team cannot be forked"
            : "Nothing to fork yet — agents can be branched once they've had a turn";

    /// <summary>
    /// Branch the WHOLE roster into a second bridge that starts with the first one's knowledge. Every pane is forked
    /// the way a single chat is (<c>--fork-session</c>: the new session replays the original's transcript and then
    /// diverges), so agent #2 of the fork remembers everything agent #2 of the original did. The original roster is
    /// parked, NOT closed — it keeps running in the background and is one click away from the sidebar.
    ///
    /// Two rosters now share one project, so the fork also gets its own coordination board (see
    /// <see cref="_bridgeBoard"/>); without that the two teams would overwrite each other's claims and both think
    /// they were agents #1..#N of the same bridge.
    ///
    /// A pane that cannot be branched (Kimi and Grok have no fork, and neither does an agent that has never had a
    /// turn) still gets a seat in the new roster - as a fresh agent of the same provider and model, with no memory.
    /// Returns null when there is nothing to fork.
    /// </summary>
    public ChatViewModel? ForkBridge()
    {
        if (!CanForkBridge) return null;
        var source = BridgePanes.ToList();
        var sourceHost = source[0];
        var cwd = sourceHost.Cwd;
        var sourceBoard = _bridgeBoard;
        var board = NextBridgeBoard(cwd);
        var roster = Enumerable.Range(1, source.Count).ToList();
        var managerNumber = ManagerNumberIn(source);
        var blank = 0;   // panes that had to start fresh instead of inheriting a transcript

        // Build every fork BEFORE touching the surface: if a constructor throws, the user still has their bridge.
        var forks = new List<ChatViewModel>();
        foreach (var pane in source)
        {
            var branch = pane.CanFork;
            if (!branch) blank++;
            var chat = new ChatViewModel(pane.Cwd,
                resume: branch ? pane.SessionId : null,
                fork: branch,
                title: ForkTitle(pane.Title),
                accountId: pane.AccountId,
                provider: pane.Provider);
            chat.Model = pane.Model;       // plain setters: cloning a roster must not rewrite the app-wide defaults
            chat.Effort = pane.Effort;
            chat.FastMode = pane.FastMode;
            chat.SetMode(pane.Mode);
            chat.ExcludeFromMemory = pane.ExcludeFromMemory;
            chat.IsBridgeManager = pane.IsBridgeManager;   // the crown is part of the roster's shape
            forks.Add(chat);
        }

        // The original keeps every provider process it had; parking is purely a view-model move.
        ParkActiveBridge();
        _bridgeBoard = board;
        WriteBridgeFile(cwd);

        var host = forks[0];
        host.IsBridgeHost = true;
        for (var i = 0; i < forks.Count; i++)
        {
            var chat = forks[i];
            var num = i + 1;
            AppendPeerToBridgeFile(cwd, num);
            chat.BridgeLabel = AgentLabel(chat, num);
            chat.AppendSystemPrompt = BridgePrompt(chat, num, roster, managerNumber);
            // The fork's transcript still ends inside the OLD bridge, so the system prompt alone is not enough:
            // say out loud that this is a new roster on a new board before it acts on a stale hand-off.
            chat.Prelude = BridgeForkPrelude(board, sourceBoard, chat, num, roster, managerNumber);
            chat.Items.Add(new DividerItem
            {
                Label = source[i].CanFork
                    ? $"🔱 Forked from {source[i].BridgeLabel ?? source[i].AgentDisplay} — you are {chat.BridgeLabel} on a new bridge"
                    : $"🔱 New bridge — {chat.BridgeLabel} started fresh ({chat.AgentDisplay} sessions cannot be forked)",
            });
            Track(chat);
        }

        // Only the host earns a sidebar row; peers live in the roster exactly like Add agent's peers do.
        MarkOwned(host.SessionId);
        Chats.Insert(0, host);
        if (Chats.Any(c => c.Pinned)) ReorderPinned();

        BridgePanes.ReplaceAll(forks);
        foreach (var chat in forks) chat.Start();
        ResetBridgeExpand();
        RefreshBridgeWorkingCue();
        ActiveChat = host;
        ShowBridge = true;
        NoteBridgeActivity();
        StartBridgeIdleTimer();
        SaveSession();
        RaiseBridgeUi();
        if (blank > 0)
            host.Items.Add(new BannerItem
            {
                Level = "info",
                Text = $"{blank} of {forks.Count} agents started fresh: only Claude and Codex sessions can be forked, " +
                       "and only after they've had a turn.",
            });
        return host;
    }

    /// <summary>"Bridge · Claude 2" → "Bridge · Claude 2 (fork)", and forking a fork does not stack the suffix.</summary>
    private static string ForkTitle(string? title)
    {
        var name = string.IsNullOrWhiteSpace(title) ? "Bridge" : title!.Trim();
        return name.EndsWith("(fork)", StringComparison.OrdinalIgnoreCase) ? name : name + " (fork)";
    }

    /// <summary>The board a NEW roster should take in this project: the project's default one when nothing else is
    /// using it (the ordinary case — the two shells are usually on two different projects), and only otherwise a
    /// numbered one. Starting a second bridge must not gratuitously rename the board of the first project to open.</summary>
    private string FreeBridgeBoard(string cwd) =>
        BridgeBoardsTakenIn(cwd).Contains(DefaultBridgeBoard) ? NextBridgeBoard(cwd) : DefaultBridgeBoard;

    /// <summary>Every coordination board already spoken for in this project — by a live roster (which is actively
    /// writing to it) or by a dormant saved one (whose claims are still on disk waiting to be resumed).</summary>
    private HashSet<string> BridgeBoardsTakenIn(string cwd) => LiveBridges()
        .Where(b => string.Equals(b.Panes.FirstOrDefault()?.Cwd, cwd, StringComparison.OrdinalIgnoreCase))
        .Select(b => b.Board)
        .Concat(AppSettings.Current.SavedBridges
            .Where(sb => string.Equals(sb.Cwd, cwd, StringComparison.OrdinalIgnoreCase))
            .Select(sb => string.IsNullOrWhiteSpace(sb.Board) ? DefaultBridgeBoard : sb.Board!))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A coordination board no other roster in this project is using. Only forks and a second shell's roster ever
    /// take a numbered board - an ordinary bridge keeps using the project's default one.
    /// </summary>
    private string NextBridgeBoard(string cwd)
    {
        var taken = BridgeBoardsTakenIn(cwd);
        for (var n = 2; n <= 99; n++)
        {
            var name = $".vibecode-bridge-{n}.md";
            if (!taken.Contains(name)) return name;
        }
        return DefaultBridgeBoard;
    }

    /// <summary>
    /// Broadcast one message to every agent on the live Bridge. Each running pane is interrupted first so the
    /// announcement preempts whatever it's doing, then the text is delivered as that pane's next user turn — sent
    /// immediately when the pane is idle, or queued behind the interrupt and auto-flushed the moment its turn ends.
    /// Agents pick up their own work again after reading it. Returns how many panes the announcement reached.
    /// </summary>
    public int AnnounceToBridge(string message) => AnnounceToBridge(message, null);

    /// <summary>Broadcast to the roster the SECOND working shell is showing — its own bridge, or the shared one when
    /// both shells are on the same bridge.</summary>
    public int AnnounceToSecondaryBridge(string message) => AnnounceToBridge(message, SecondaryBridge);

    private int AnnounceToBridge(string message, LiveBridge? target)
    {
        var body = (message ?? string.Empty).Trim();
        var panes = PanesFor(target);
        if (body.Length == 0 || panes.Count == 0) return 0;
        var wire = "📢 ANNOUNCEMENT (broadcast to every agent on this bridge):\n\n" + body +
                   "\n\n— Note this, then carry on with your work.";
        var reached = 0;
        foreach (var pane in panes.ToList())
        {
            if (pane.CanInterrupt) pane.Interrupt();   // stop the current turn so the notice lands now
            if (pane.Send(wire)) reached++;            // idle → sends now; busy → queues, auto-flushes post-interrupt
        }
        if (reached > 0) NoteBridgeActivity(target);
        return reached;
    }

    /// <summary>Close one pane via its X without turning the departed agent into a standalone chat. The other agents
    /// keep running; if the host is closed, the next pane is promoted. One survivor collapses to a normal chat.</summary>
    public void RemoveBridgePane(ChatViewModel pane)
    {
        // Which surface owns this pane? Shell 2's own roster is a full bridge with its own host, board and manager,
        // so closing a pane over there must renumber THAT roster and leave the primary's alone.
        var target = SecondaryBridge is { } secondary && secondary.Panes.Contains(pane) ? secondary : null;
        var panes = PanesFor(target);
        if (!panes.Contains(pane)) return;
        // Closing the orchestrator ends the whole Demon team — without it the fifteen workers are unreachable by
        // anyone, which is exactly the orphaned-process state Demon Mode promises never to leave behind.
        if (target is null && IsDemonMode && pane.IsDemonOrchestrator) { CloseDemonTeam("the orchestrator was closed"); return; }
        if (target is null && IsDemonMode && pane.IsDemonWorker) RetireSupervisedPane(pane, "its pane was closed by the user");
        var originalHost = panes[0];
        var wasHost = ReferenceEquals(pane, panes[0]);
        var wasActive = ReferenceEquals(pane, ActiveChat);
        var wasSecondaryActive = ReferenceEquals(pane, SecondaryActiveChat);
        var leftNum = BridgeNumberOf(pane);
        var wasManager = pane.IsBridgeManager;

        panes.Remove(pane);
        Chats.Remove(pane);                // the host is a sidebar chat; closing its pane must close that row too
        ErroredFor(target).Remove(pane);   // drop any error-announce bookkeeping for the departing pane
        ForgetPeerState(pane);             // …and any peer-chain state, which is keyed by pane and outlives it
        pane.Close();                      // provider transcripts remain resumable through the existing history/recovery path
        RefreshBridgeWorkingCue(panes);
        NoteBridgeActivity(target);

        if (panes.Count >= 2)
        {
            ResetBridgeExpand(target);   // if the removed (or any) pane was focused, restore the grid so none stays hidden
            // The bridge continues with the remaining agents. If we removed the host, promote the new first pane to
            // be the host anchor (a real chat you can click to return to the bridge) so it isn't left unreachable.
            if (wasHost)
            {
                RemoveSavedBridgeFor(originalHost, save: false);   // host key changed; do not strand a duplicate snapshot
                PromoteToHost(panes[0], target);
            }
            // Compact every surviving identity (2,3,4 -> 1,2,3), rewrite the status-board ownership headers, and
            // queue an explicit self+peer roster correction into every already-running provider session.
            CompactBridgeRosterAfterDeparture(pane.Cwd, leftNum, pane.AgentDisplay, "left the bridge", "🔗", target);
            if (wasManager)
            {
                // The brain left. Tell the survivors plainly so nobody keeps waiting for dispatches that will never come.
                foreach (var peer in panes)
                {
                    var note = "[BRIDGE] The MANAGER left the bridge. No manager is assigned now — coordinate as equal " +
                               "peers via the status board until the user crowns a new one.";
                    peer.Prelude = string.IsNullOrEmpty(peer.Prelude) ? note : peer.Prelude + "\n" + note;
                    peer.Items.Add(new DividerItem { Label = "👑 the manager left — no manager assigned" });
                }
            }
            else if (ManagerOf(panes) is { } mgr)
            {
                // A worker left: the manager adapts in real time — its unfinished lane is reassignable immediately.
                SendManagerUpdate(mgr,
                    $"{pane.AgentDisplay} agent #{leftNum} LEFT the bridge and the roster was renumbered contiguously " +
                    $"(you are {mgr.AgentDisplay} agent #{BridgeNumberOf(mgr)}; {panes.Count} agents remain). " +
                    "Anything it was working on is now unowned — update the plan and re-dispatch its unfinished lane " +
                    "to a free worker (or take it yourself if none are free).");
            }
            if (wasActive) ActiveChat = panes[0];
            if (wasSecondaryActive) SecondaryActiveChat = panes[0];
            SaveBridge(panes, BoardFor(target));
            SaveSession();   // Chats changed (host removed / promoted) - persist OpenChats now so a crash can't lose it
            RaiseRosterUi(target);
            return;
        }

        // Only one Claude left → not a bridge anymore: keep the survivor as an ordinary chat and leave bridge mode.
        var survivor = panes.FirstOrDefault();
        panes.Clear();
        ErroredFor(target).Clear();
        PeerTrafficFor(target).Clear();   // this roster is over; the next one on this surface starts on a clean budget
        SetBoardFor(target, DefaultBridgeBoard);
        RemoveSavedBridgeFor(originalHost, save: false);
        if (target is not null)
        {
            // Shell 2's bridge just collapsed to a single chat. Retire the parked slot and leave that window on the
            // survivor as an ordinary chat — the primary's bridge is not involved and must not be disturbed.
            _parkedBridges.Remove(originalHost);
            SetSecondaryBridgeHost(null);
            RetireCollapsedBridgeSurvivor(survivor);
            SecondaryShowBridge = false;
            SecondaryActiveChat = survivor ?? SecondaryActiveChat;
            if (!IsBridge && _parkedBridges.Count == 0) StopBridgeIdleTimer();
            RaiseBridgeUi();
            SaveSession();
            return;
        }
        if (_parkedBridges.Count == 0) StopBridgeIdleTimer();
        RetireCollapsedBridgeSurvivor(survivor);
        if (wasActive || ShowBridge) ActiveChat = survivor ?? Chats.FirstOrDefault();
        // Shell 2 keeps its own bridge (and its own selection) when it has one; only follow the collapse otherwise.
        if (!HasSeparateSecondaryBridge && (wasSecondaryActive || SecondaryShowBridge))
            SecondaryActiveChat = survivor
                                  ?? Chats.FirstOrDefault(candidate => !ReferenceEquals(candidate, ActiveChat))
                                  ?? ActiveChat;
        ShowBridge = false;
        if (!HasSeparateSecondaryBridge) SecondaryShowBridge = false;
        RaiseBridgeUi();
        SaveSession();
    }

    /// <summary>Toggle "focus one Claude": expand the given pane to fill the whole bridge (collapsing its peers), or
    /// restore the grid if it was already expanded. Collapsed peers keep running in the background - only hidden.
    /// User-minimized panes stay minimized; expand only reflows agents that are still on the grid.</summary>
    public void ToggleBridgeExpand(ChatViewModel pane, Func<ChatViewModel, bool>? surfaceContains = null)
    {
        // Resolve the roster from the PANE, not from the primary surface: the same button exists on shell 2's own
        // bridge, and focusing an agent there must reflow that roster rather than silently doing nothing.
        if (pane is null || pane.BridgeMinimized || RosterPanesOf(pane) is not { } panes) return;
        SelectBridgePane(pane);
        surfaceContains ??= _ => true;
        var expand = !pane.BridgeExpanded;                       // clicking the expanded pane's button restores the grid
        foreach (var p in panes)
        {
            p.BridgeExpanded = expand && ReferenceEquals(p, pane);
            // In dual-monitor mode focus is local to the surface that owns the clicked pane. The other monitor stays
            // populated instead of going blank; single-monitor callers use the default predicate and retain the old behavior.
            // Minimized panes keep BridgeVisible true so unminimizing them later lands them back in the grid correctly.
            if (p.BridgeMinimized) { p.BridgeExpanded = false; continue; }
            p.BridgeVisible = !expand || !surfaceContains(p) || ReferenceEquals(p, pane);
        }
        RaiseGridRowsFor(pane);
    }

    /// <summary>The panes of the live roster a pane belongs to — the primary surface's or shell 2's — or null when it
    /// belongs to neither (a roster parked behind a chat nobody is looking at).</summary>
    private BridgePaneCollection? RosterPanesOf(ChatViewModel pane)
    {
        if (BridgePanes.Contains(pane)) return BridgePanes;
        if (SecondaryBridge is { } secondary && secondary.Panes.Contains(pane)) return secondary.Panes;
        return null;
    }

    /// <summary>Re-announce the grid density of the surface that owns this pane.</summary>
    private void RaiseGridRowsFor(ChatViewModel pane)
    {
        if (SecondaryBridge is { } secondary && secondary.Panes.Contains(pane))
        {
            Raise(nameof(SecondaryHasMinimizedBridgePanes));
            return;
        }
        Raise(nameof(BridgeGridRows));
        Raise(nameof(HasMinimizedBridgePanes));
    }

    /// <summary>Restore the full grid (no pane expanded, expand-focus collapse cleared). User-minimized panes stay
    /// minimized — this only undoes focus mode, not a deliberate hide.</summary>
    public void ResetBridgeExpand() => ResetBridgeExpand(null);

    private void ResetBridgeExpand(LiveBridge? target)
    {
        foreach (var p in PanesFor(target)) { p.BridgeExpanded = false; p.BridgeVisible = true; }
        Raise(nameof(BridgeGridRows));
    }

    /// <summary>Hide a bridge agent from the grid without removing it. The agent keeps running; a restore chip in the
    /// bridge header (next to usage) brings it back. If it was focused, the grid is restored for remaining panes.</summary>
    public void MinimizeBridgePane(ChatViewModel pane)
    {
        if (pane is null || pane.BridgeMinimized || RosterPanesOf(pane) is not { } panes) return;
        var wasExpanded = pane.BridgeExpanded;
        pane.BridgeMinimized = true;
        pane.BridgeExpanded = false;
        if (wasExpanded)
        {
            // Peers that were expand-collapsed should reappear (unless they themselves are minimized).
            foreach (var p in panes)
            {
                if (!p.BridgeMinimized) p.BridgeVisible = true;
                p.BridgeExpanded = false;
            }
        }
        RaiseGridRowsFor(pane);
        NoteBridgeActivity(RosterOf(pane));
    }

    /// <summary>Bring a user-minimized bridge agent back onto the grid. Always shows the pane (clears expand-focus
    /// collapse if needed) so a restore chip never "succeeds" while leaving the agent still hidden.</summary>
    public void RestoreBridgePane(ChatViewModel pane)
    {
        if (pane is null || !pane.BridgeMinimized || RosterPanesOf(pane) is not { } panes) return;
        pane.BridgeMinimized = false;
        // Expand-focus had collapsed peers via BridgeVisible; clearing it guarantees the restored pane is actually on
        // screen, and any other still-minimized agents stay hidden via BridgeMinimized alone.
        foreach (var p in panes)
        {
            p.BridgeExpanded = false;
            p.BridgeVisible = true;
        }
        SelectBridgePane(pane);
        RaiseGridRowsFor(pane);
        NoteBridgeActivity(RosterOf(pane));
    }

    /// <summary>Make a (formerly peer) pane the bridge's host anchor: a real chat in the sidebar carrying the
    /// "return to bridge" cue, and re-key the temp-save to it.</summary>
    private void PromoteToHost(ChatViewModel newHost, LiveBridge? target = null)
    {
        if (!Chats.Contains(newHost))
        {
            Chats.Insert(0, newHost);
            if (Chats.Any(c => c.Pinned)) ReorderPinned();
        }
        newHost.IsBridgeHost = true;
        MarkOwned(newHost.SessionId);
        // A parked roster is keyed on its host, and shell 2 points at it by that key — both have to follow the crown.
        if (target is not null)
        {
            var oldKey = _parkedBridges.FirstOrDefault(entry => ReferenceEquals(entry.Value, target)).Key;
            if (oldKey is not null && !ReferenceEquals(oldKey, newHost))
            {
                _parkedBridges.Remove(oldKey);
                _parkedBridges[newHost] = target;
                if (ReferenceEquals(_secondaryBridgeHost, oldKey)) _secondaryBridgeHost = newHost;
            }
        }
        SaveBridge(PanesFor(target), BoardFor(target));   // SavedBridge is keyed on the host's SessionId - re-point it
    }

    /// <summary>A roster that has shrunk to one agent is not a bridge any more: turn the survivor back into an
    /// ordinary, reachable chat. Shared by the primary surface and shell 2's own roster.</summary>
    private void RetireCollapsedBridgeSurvivor(ChatViewModel? survivor)
    {
        if (survivor is null) return;
        survivor.BridgeHasWorkingPane = false;
        survivor.BridgeLabel = "";
        survivor.IsBridgeHost = false;
        survivor.IsBridgeManager = false;   // no bridge left to manage
        RetireBridgeAgentContext(survivor, CollapsedBridgeNotice);
        if (!Chats.Contains(survivor))
        {
            Chats.Insert(0, survivor);   // ensure it's a normal, reachable chat
            if (Chats.Any(c => c.Pinned)) ReorderPinned();
        }
        MarkOwned(survivor.SessionId);
    }

    /// <summary>
    /// Take the bridge back out of an agent that is no longer in one.
    ///
    /// The four flags above are what the WINDOW reads; none of them is what the agent reads. Its session was launched
    /// with the "[BRIDGE MODE] you are agent #N of M alongside …" appendix, and whatever roster note was staged last
    /// is still sitting on its Prelude waiting to ride the user's next message. Clearing only the flags is how a chat
    /// with no bridge ends up being told, on its very next ordinary turn, that its "active peers are agent #2,
    /// agent #3" — and how restarting it launches a bridge agent for a bridge that no longer exists.
    ///
    /// Every other roster change tells the live sessions what happened (a join, a departure, a lost manager, a fork).
    /// The bridge ENDING was the one transition that told them nothing.
    /// </summary>
    /// <param name="divider">Null for a caller that writes its own transcript marker (Demon Mode does).</param>
    private static void RetireBridgeAgentContext(ChatViewModel pane, string notice,
        string? divider = "🔗 Bridge ended — this is an ordinary chat again")
    {
        pane.ClearPeerChatAccess();
        pane.ClearPeerMailbox();
        pane.AppendSystemPrompt = null;   // a restart must not relaunch it as somebody's peer
        pane.Prelude = StripStaleBridgeNotes(pane.Prelude, notice);
        if (divider is not null) pane.Items.Add(new DividerItem { Label = divider });
    }

    /// <summary>
    /// Drop the bridge notes staged on a Prelude and stage <paramref name="notice"/> instead, keeping anything that
    /// did not come from the bridge — a rewind note is the one that matters, and silently eating it would leave the
    /// agent editing a workspace it does not know was rolled back.
    ///
    /// A bridge note begins with "[BRIDGE" and runs to the end of its paragraph: they are appended a single newline
    /// apart and may be several lines long (a bounced-message notice lists one line per undelivered message), while
    /// anything from outside is separated by a blank line.
    /// </summary>
    private static string StripStaleBridgeNotes(string? prelude, string notice)
    {
        var kept = new List<string>();
        var dropping = false;
        foreach (var line in (prelude ?? "").Split('\n'))
        {
            if (line.StartsWith("[BRIDGE", StringComparison.Ordinal)) dropping = true;
            else if (line.Trim().Length == 0) dropping = false;
            if (!dropping) kept.Add(line);
        }
        kept.Add(notice);
        return string.Join("\n", kept).TrimStart('\n');
    }

    /// <summary>What the last agent standing is told when the others leave. It has been running under bridge rules
    /// for its whole session, so the correction has to be explicit about which of them no longer apply — an agent
    /// that merely stops hearing about peers keeps writing to the board and waiting on answers.</summary>
    private const string CollapsedBridgeNotice =
        "[BRIDGE] The bridge has ENDED. Every other agent left and you are on your own in an ordinary chat — this " +
        "REPLACES the bridge rules you were given earlier in this session. There is no roster and no agent numbers " +
        "(you are not \"agent #1\" of anything), there is nobody to message (a @@MSG block now reaches no one), and " +
        "the status board is no longer in use — stop reading and rewriting it. Do not wait on anyone and do not hand " +
        "work over: anything that was still in flight is yours. Carry on with the user's request directly.";

    /// <summary>The same correction for a bridge the user closed outright. The peers are gone from this session's
    /// point of view either way; the difference is that this one can be brought back, so it does not say the roster
    /// is finished for good.</summary>
    /// <summary>The same correction for a Demon orchestrator whose team has been stopped. Worded around dispatch
    /// rather than peers, because that is the channel it will otherwise keep using.</summary>
    private const string DemonTeamEndedNotice =
        "[BRIDGE] Demon Mode has ENDED and every worker has been stopped. This REPLACES the orchestration rules you " +
        "were given earlier in this session: there is no team, a @@DISPATCH or @@MSG block now reaches no one, and " +
        "nothing you assigned is still being worked on. Do not wait on any worker and do not dispatch. You are an " +
        "ordinary chat again — if work remains, do it yourself in this session.";

    private const string ClosedBridgeNotice =
        "[BRIDGE] The bridge has been CLOSED and the other agents are no longer running. This REPLACES the bridge " +
        "rules you were given earlier in this session: there is no roster to coordinate with, a @@MSG block now " +
        "reaches no one, and the status board is no longer in use. Do not wait on anyone. You are an ordinary chat " +
        "again — carry on with the user's request directly.";

    /// <summary>End the LIVE bridge but save it durably: the peers' sessions are persisted before
    /// their processes are disposed, and the host keeps a "resume bridge" cue. Called by the host pane's X and the
    /// idle timeout. Reopening the host (<see cref="OpenChat"/>) resumes the provider-specific peer threads.</summary>
    public void CloseBridge()
    {
        // A Demon team is never left resumable: its lanes only mean anything for the objective it was spun up for.
        if (IsDemonMode) { CloseDemonTeam("the team was closed"); return; }
        var host = BridgePanes.FirstOrDefault();
        var wasShowing = ShowBridge;
        SaveBridge();   // persist peers FIRST so nothing is lost when their live processes are disposed
        foreach (var p in BridgePanes.Skip(1).ToList()) { ForgetPeerState(p); p.Close(); }
        BridgePanes.Clear();
        _bridgeErrored.Clear();
        _bridgeBoard = DefaultBridgeBoard;
        _peerTraffic.Clear();   // the roster is gone; its agent numbers must not budget the next one's
        if (host is not null)
        {
            host.BridgeHasWorkingPane = false;
            host.BridgeLabel = "";
            host.IsBridgeHost = HasSavedBridgeFor(host);   // keep the cue iff there's a resumable saved bridge
            // Resuming re-briefs every pane from scratch (see RestoreBridge), so dropping the frozen appendix here
            // costs the saved bridge nothing and stops the host talking to peers that are no longer running.
            ForgetPeerState(host);
            RetireBridgeAgentContext(host, ClosedBridgeNotice);
        }
        ShowBridge = false;
        // Only blank shell 2 when it was showing THIS roster. A bridge of its own on the other display is a separate
        // bridge and survives closing this one.
        if (!HasSeparateSecondaryBridge) SecondaryShowBridge = false;
        if (_parkedBridges.Count == 0) StopBridgeIdleTimer();
        // Only pull the user to the host if they were actually looking at the bridge; a background timeout
        // shouldn't yank them out of whatever chat they're in.
        if (wasShowing && host is not null) ActiveChat = host;
        RaiseBridgeUi();
        SaveSession();
    }

    /// <summary>Close the bridge the SECOND working shell is showing, leaving the primary's bridge running. When the
    /// two shells share one roster there is only one bridge to close, so this is the ordinary close.</summary>
    public void CloseSecondaryBridge()
    {
        if (_secondaryBridgeHost is not { } host || SecondaryBridge is null) { CloseBridge(); return; }
        SetSecondaryBridgeHost(null);
        SecondaryShowBridge = false;
        SecondaryActiveChat = host;
        CloseParkedBridge(host);   // saves the roster, disposes its peers, keeps the host as a resumable chat
        RaiseBridgeUi();
        SaveSession();
    }

    /// <summary>Dispose one parked roster while leaving every other live bridge untouched.</summary>
    private bool CloseParkedBridge(ChatViewModel host)
    {
        if (!_parkedBridges.Remove(host, out var bridge)) return false;
        SaveBridge(bridge.Panes, bridge.Board);
        foreach (var pane in bridge.Panes) ForgetPeerState(pane);
        foreach (var pane in bridge.Panes.Skip(1)) pane.Close();
        RetireBridgeAgentContext(host, ClosedBridgeNotice);
        host.BridgeHasWorkingPane = false;
        host.BridgeLabel = "";
        host.IsBridgeHost = HasSavedBridgeFor(host);
        if (!IsBridge && _parkedBridges.Count == 0) StopBridgeIdleTimer();
        RefreshResumableBridges();
        return true;
    }

    // ---- persist bridge peers so a Close / navigate-away / restart can resume them later ----

    private static SavedBridgeState? SavedBridgeFor(ChatViewModel host) =>
        AppSettings.Current.FindSavedBridge(host.SessionId, host.Provider);

    private static bool HasSavedBridgeFor(ChatViewModel host) =>
        SavedBridgeFor(host) is { } s
        && s.Peers.Any(p => !string.IsNullOrWhiteSpace(p.SessionId));

    private static void PruneSavedBridges()
    {
        var settings = AppSettings.Current;
        var removed = settings.RemoveMalformedSavedBridges();
        if (removed > 0) settings.Save();
    }

    private static bool RemoveSavedBridgeFor(ChatViewModel host, bool save = true) =>
        RemoveSavedBridgeFor(host.SessionId, host.Provider, save);

    private static bool RemoveSavedBridgeFor(string? hostId, string provider, bool save = true)
    {
        if (hostId is null) return false;
        var settings = AppSettings.Current;
        var removed = settings.RemoveSavedBridge(hostId, provider);
        if (removed && save) settings.Save();
        return removed;
    }

    /// <summary>Dormant bridges the user can put back on screen: everything saved except the one that is live right
    /// now. Surfaced on the home screen so a closed - or force-quit - bridge is always one click from returning,
    /// instead of being reachable only by remembering which sidebar row happened to be its host.</summary>
    public ObservableCollection<SavedBridgeVm> ResumableBridges { get; } = new();

    public bool HasResumableBridges => ResumableBridges.Count > 0;

    public void RefreshResumableBridges()
    {
        var liveHosts = LiveBridges()
            .Select(bridge => bridge.Panes.FirstOrDefault())
            .Where(host => host is not null)
            .Cast<ChatViewModel>()
            .ToList();
        var dormant = AppSettings.Current.SavedBridges
            .Where(b => b.Peers.Any(p => !string.IsNullOrWhiteSpace(p.SessionId)))
            .Where(saved => !liveHosts.Any(host =>
                string.Equals(saved.HostSessionId, host.SessionId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(saved.Provider, host.Provider, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(b => b.SavedAt)
            .Select(b => new SavedBridgeVm(b))
            .ToList();

        ResumableBridges.Clear();
        foreach (var vm in dormant) ResumableBridges.Add(vm);
        Raise(nameof(HasResumableBridges));
    }

    /// <summary>Put a dormant bridge back on screen: re-open (or rebuild) its host chat, then resume every peer.</summary>
    public bool ResumeSavedBridge(SavedBridgeVm entry, bool activatePrimary = true)
    {
        var saved = entry.State;
        var host = Chats.FirstOrDefault(c =>
            string.Equals(c.SessionId, saved.HostSessionId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(c.Provider, saved.Provider, StringComparison.OrdinalIgnoreCase));

        if (host is not null && IsBridge && ReferenceEquals(BridgePanes[0], host))
        {
            if (activatePrimary)
            {
                ActiveChat = host;
                ShowBridge = true;
            }
            else
            {
                SecondaryActiveChat = host;
                SecondaryShowBridge = true;
            }
            NoteBridgeActivity();
            return true;
        }

        if (host is not null && _parkedBridges.ContainsKey(host))
        {
            if (IsBridge)
            {
                if (!activatePrimary) ShowBridge = false;
                ParkActiveBridge(clearSurface: false);
            }
            if (activatePrimary) ActiveChat = host;
            else SecondaryActiveChat = host;
            var activated = ActivateParkedBridge(host, showPrimary: activatePrimary);
            RefreshResumableBridges();
            return activated;
        }

        // A saved-only roster reconnects once; the roster currently on screen is parked and keeps working.
        if (IsBridge)
        {
            if (!activatePrimary) ShowBridge = false;
            ParkActiveBridge();
        }

        var replacePrimarySelection = host is not null && ReferenceEquals(ActiveChat, host);
        var replaceSecondarySelection = host is not null && ReferenceEquals(SecondaryActiveChat, host);
        if (host is null || host.Status is "error" or "closed")
        {
            if (host is not null)
            {
                host.Close();
                Chats.Remove(host);
                if (ReferenceEquals(SecondaryActiveChat, host)) SecondaryActiveChat = null;
            }
            var cwd = Directory.Exists(saved.Cwd) ? saved.Cwd : DefaultCwd;
            host = new ChatViewModel(cwd, resume: saved.HostSessionId, fork: false,
                title: saved.HostTitle, accountId: saved.HostAccountId, provider: saved.Provider)
            { Pinned = saved.HostPinned };
            Track(host);
            MarkOwned(saved.HostSessionId);
            Chats.Insert(0, host);
            if (Chats.Any(c => c.Pinned)) ReorderPinned();
            // SnapshotBridge saved the host's mode alongside the peers', and every peer gets its own back in
            // RestoreBridge - a host rebuilt here was the one pane nothing read it for, so it came back on Ask while
            // the rest of the roster came back on bypass. Same clamp as the chat restore: a snapshot from before the
            // mode was saved (or one edited by hand) falls back to the seed rather than to the mode nobody chose.
            host.SetMode(AppSettings.IsKnownPermissionMode(saved.Mode) ? saved.Mode! : AppSettings.Current.DefaultMode);
            host.Start();
        }

        if (activatePrimary || replacePrimarySelection) ActiveChat = host;
        if (!activatePrimary || replaceSecondarySelection) SecondaryActiveChat = host;
        var restored = RestoreBridge(host, showPrimary: activatePrimary);
        if (restored) SaveSession();
        RefreshResumableBridges();
        return restored;
    }

    /// <summary>Snapshot the live bridge's peer thread ids to disk. No-op if there's nothing resumable yet.</summary>
    public void SaveBridge()
    {
        SaveBridge(BridgePanes, _bridgeBoard);
    }

    private static void SaveBridge(IReadOnlyList<ChatViewModel> panes, string board)
    {
        if (!SnapshotBridge(panes, board)) return;
        AppSettings.Current.Save();
    }

    private static bool SnapshotBridge(IReadOnlyList<ChatViewModel> panes, string board)
    {
        var host = panes.FirstOrDefault();
        // A Demon team is deliberately not resumable. Persisting one would offer to bring fifteen workers back days
        // later, each holding a lane from an objective that has long since been finished or abandoned.
        if (host is not null && (host.IsDemonOrchestrator || panes.Any(p => p.IsDemonWorker))) return false;
        if (host?.SessionId is not { } hostId || panes.Count < 2) return false;
        var peers = panes.Skip(1)
            .Where(p => p.SessionId is not null)
            .Select(p => new SavedBridgePane
            {
                Cwd = p.Cwd,
                SessionId = p.SessionId,
                Label = p.BridgeLabel,
                Title = p.Title,
                Provider = p.Provider,
                AccountId = p.AccountId,
                Mode = p.Mode,
                Model = p.Model,
                Effort = p.Effort,
                FastMode = p.ShowFastMode ? p.FastMode : null,
                Draft = NullIfEmpty(p.Draft),
                ExcludeFromMemory = p.ExcludeFromMemory,
                IsManager = p.IsBridgeManager,
            })
            .ToList();
        if (peers.Count == 0) return false;   // Track() saves again as soon as a brand-new peer receives its session id

        var snapshot = new SavedBridgeState
        {
            Cwd = host.Cwd,
            HostSessionId = hostId,
            Provider = host.Provider,
            HostTitle = host.Title,
            HostAccountId = host.AccountId,
            HostPinned = host.Pinned,
            HostIsManager = host.IsBridgeManager,
            Mode = host.Mode,
            Board = board,
            SavedAt = DateTime.Now,
            Peers = peers,
        };
        var settings = AppSettings.Current;
        settings.UpsertSavedBridge(snapshot);
        return true;
    }

    /// <summary>Re-spawn a temp-saved bridge's peers (with --resume) and re-enter the overlay. Returns false if there
    /// is no valid saved bridge for this host (missing / different host / expired).</summary>
    public bool RestoreBridge(ChatViewModel host, bool showPrimary = true)
    {
        // Only the PRIMARY surface can hold one roster at a time. Shell 2 resumes into a parked slot of its own, so a
        // bridge already running on the main window is no reason to refuse it.
        if (showPrimary && IsBridge) return false;
        if (!showPrimary && _parkedBridges.ContainsKey(host)) return false;
        var s = SavedBridgeFor(host);
        var savedPeers = s?.Peers.Where(p => !string.IsNullOrWhiteSpace(p.SessionId)).ToList();
        if (s is null || savedPeers is null || savedPeers.Count == 0) return false;
        // Saved bridges from older builds may contain gaps (1,3,4). Always restore a compact 1..N roster so labels,
        // the status board, and every provider-specific peer prompt agree before any resumed agent receives work.
        var peerNums = Enumerable.Range(2, savedPeers.Count).ToList();
        var roster = Enumerable.Range(1, savedPeers.Count + 1).ToList();
        // The crown survives the restart: the manager keeps its role (numbers are compacted the same way labels are).
        var managerNumber = s.HostIsManager ? 1
            : savedPeers.FindIndex(p => p.IsManager) is var mi and >= 0 ? peerNums[mi] : 0;
        // A forked roster resumes onto ITS board. Older snapshots predate forking and all shared the default one.
        var board = string.IsNullOrWhiteSpace(s.Board) ? DefaultBridgeBoard : s.Board!;
        if (showPrimary) _bridgeBoard = board;   // shell 2's board rides on its own roster record, set below
        WriteBridgeFile(host.Cwd, board, preserveManagerPlan: managerNumber > 0);
        AppendPeerToBridgeFile(host.Cwd, board, 1);
        host.BridgeLabel = AgentLabel(host, 1);
        host.IsBridgeHost = true;
        host.IsBridgeManager = s.HostIsManager;
        // Every resumed PEER is re-briefed below; the host used to be the one pane that was not, silently inheriting
        // whatever appendix it happened to still be carrying. That was already wrong whenever the roster came back a
        // different size ("agent #1 of 4" resuming into a roster of three), and it is the only reason closing a
        // bridge could leave the appendix in place at all.
        host.AppendSystemPrompt = BridgePrompt(board, host, 1, roster, managerNumber);
        host.Prelude = BridgeJoinPrelude(board, host, 1, roster, managerNumber);
        host.Items.Add(new DividerItem { Label = $"🔗 Bridge resumed — you are {host.BridgeLabel}" });
        var restoredPanes = new List<ChatViewModel> { host };
        var panesToStart = new List<ChatViewModel>();
        for (int i = 0; i < savedPeers.Count; i++)
        {
            var pane = savedPeers[i];
            var num = peerNums[i];
            var cwd = Directory.Exists(pane.Cwd) ? pane.Cwd : host.Cwd;
            var provider = string.IsNullOrWhiteSpace(pane.Provider) ? host.Provider : pane.Provider!;
            AppendPeerToBridgeFile(cwd, board, num);   // seed the file with the SAME number as the label (no mismatch)

            // A manually resumed peer may already be a normal sidebar chat. Reuse its live process instead of
            // spawning two writers for the same provider session; errored/closed rows are rebuilt from transcript.
            var chat = Chats.FirstOrDefault(c => c.SessionId == pane.SessionId
                                                  && string.Equals(c.Provider, provider, StringComparison.OrdinalIgnoreCase)
                                                  && !ReferenceEquals(c, host));
            var start = chat is null || chat.Status is "error" or "closed";
            if (chat is not null)
            {
                var wasPrimarySelection = ReferenceEquals(ActiveChat, chat);
                var wasSecondarySelection = ReferenceEquals(SecondaryActiveChat, chat);
                Chats.Remove(chat);
                if (wasPrimarySelection) ActiveChat = host;
                if (wasSecondarySelection) SecondaryActiveChat = host;
            }
            if (start)
            {
                chat?.Close();
                chat = new ChatViewModel(cwd, resume: pane.SessionId, fork: false,
                    title: pane.Title,
                    accountId: pane.AccountId ?? (string.Equals(provider, host.Provider, StringComparison.OrdinalIgnoreCase)
                        ? host.AccountId
                        : null),
                    provider: provider);
                if (string.IsNullOrWhiteSpace(pane.Title)) chat.Title = $"Bridge · {chat.AgentDisplay} {num}";
                Track(chat);
            }

            chat!.BridgeLabel = AgentLabel(chat, num);
            // Older snapshots have no flag of their own; fall back to the host's so a restored bridge cannot start
            // recording a conversation the user had muted before the restart.
            chat.ExcludeFromMemory = pane.ExcludeFromMemory || host.ExcludeFromMemory;
            chat.IsBridgeManager = pane.IsManager;
            chat.AppendSystemPrompt = BridgePrompt(board, chat, num, roster, managerNumber);
            chat.Prelude = BridgeJoinPrelude(board, chat, num, roster, managerNumber);
            UpdateGeneratedBridgeTitle(chat, num); // normalize an older saved title such as "Bridge · Codex 4" to its new compact number
            // Same clamp + fallback the chat restore uses: a peer from a snapshot written before modes were saved has
            // neither value, and a hand-edited one must not be handed to a provider that has never heard of it.
            chat.SetMode(AppSettings.IsKnownPermissionMode(pane.Mode ?? s.Mode)
                ? (pane.Mode ?? s.Mode)!
                : AppSettings.Current.DefaultMode);
            var sameProvider = string.Equals(chat.Provider, host.Provider, StringComparison.OrdinalIgnoreCase);
            if (pane.Model is not null) chat.Model = pane.Model;
            else if (sameProvider) chat.Model = host.Model; // legacy snapshots inherited the host's provider settings
            if (pane.Effort is not null) chat.Effort = pane.Effort;
            else if (sameProvider) chat.Effort = host.Effort;
            // Fast mode is per pane, so a restored bridge must not flatten four panes back onto one shared answer.
            if (pane.FastMode is { } fast) chat.FastMode = fast;
            else if (sameProvider) chat.FastMode = host.FastMode;   // pre-per-chat snapshots knew only the host's
            if (!string.IsNullOrEmpty(pane.Draft)) chat.Draft = pane.Draft;   // an unsent prompt survives the restart
            MarkOwned(chat.SessionId);
            restoredPanes.Add(chat);
            if (start) panesToStart.Add(chat);
        }
        // Shell 2 resumes into a parked roster of its own, which is what leaves the primary's bridge where it is.
        var target = showPrimary
            ? null
            : new LiveBridge
            {
                Panes = new BridgePaneCollection(restoredPanes),
                Errored = new HashSet<ChatViewModel>(),
                Activity = DateTime.Now,
                Board = board,
                PanelChat = host,
                Peers = new PeerTrafficLedger(),
            };
        if (target is null) BridgePanes.ReplaceAll(restoredPanes);
        else _parkedBridges[host] = target;
        foreach (var pane in panesToStart) pane.Start();
        // After ALL panes exist (it iterates the roster, so it's a no-op before they're added): the host can carry a
        // stale BridgeVisible == false from a peer that was expanded when the bridge closed, which would render the
        // host invisible on resume. A resumed bridge always comes back as the full grid.
        ResetBridgeExpand(target);
        RefreshBridgeWorkingCue(PanesFor(target));
        if (showPrimary) ShowBridge = true;
        else
        {
            SetSecondaryBridgeHost(host);
            SecondaryActiveChat = host;
            SecondaryShowBridge = true;
        }
        NoteBridgeActivity(target);
        StartBridgeIdleTimer();
        RequestSave();  // refresh compact labels and host metadata in the coalesced autosave, off the click path
        RaiseBridgeUi();
        return true;
    }

    private void StartBridgeIdleTimer()
    {
        // Poll frequently enough to notice the timeout; for a short (test) timeout, poll proportionally faster.
        var timeout = BridgeIdleTimeout;
        var interval = timeout < TimeSpan.FromMinutes(1)
            ? TimeSpan.FromSeconds(Math.Max(1, timeout.TotalSeconds / 3))
            : TimeSpan.FromMinutes(5);
        _bridgeIdleTimer ??= new System.Windows.Threading.DispatcherTimer();
        _bridgeIdleTimer.Interval = interval;
        _bridgeIdleTimer.Tick -= OnBridgeIdleTick;
        _bridgeIdleTimer.Tick += OnBridgeIdleTick;
        _bridgeIdleTimer.Start();
    }

    private void StopBridgeIdleTimer()
    {
        if (_bridgeIdleTimer is null) return;
        _bridgeIdleTimer.Stop();
        _bridgeIdleTimer.Tick -= OnBridgeIdleTick;
    }

    private void OnBridgeIdleTick(object? sender, EventArgs e)
    {
        if (!IsBridge && _parkedBridges.Count == 0) { StopBridgeIdleTimer(); return; }
        var now = DateTime.Now;

        // Parked rosters remain genuine live processes. Give each one its own idle clock so a working background
        // agent is never stopped merely because another chat happens to own the visible Bridge surface.
        foreach (var (host, bridge) in _parkedBridges.ToList())
        {
            if (bridge.Panes.Any(p => p.IsWorking)) bridge.Activity = now;
            else if (now - bridge.Activity >= BridgeIdleTimeout) CloseParkedBridge(host);
        }

        if (!IsBridge) return;
        if (BridgePanes.Any(p => p.IsWorking)) { NoteBridgeActivity(); return; }
        if (now - _bridgeActivity >= BridgeIdleTimeout) CloseBridge();
    }

    // ---- roster numbering: live bridge identities are always compact and ordered (1..N). Removing a pane renumbers
    // every survivor in display order, rewrites its status-board header, and queues the corrected roster into the
    // already-running provider sessions. This keeps the UI, coordination file, Claude/Codex context, and next join
    // on the same numbering scheme.

    /// <summary>Parse the trailing number out of a provider label such as "Claude 2" or "Codex 2".</summary>
    private static int? LabelNumber(string? label)
    {
        if (string.IsNullOrEmpty(label)) return null;
        var digits = new string(label.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) ? n : null;
    }

    /// <summary>The current identity number in a pane's BridgeLabel (0 if unlabeled).</summary>
    private static int BridgeNumberOf(ChatViewModel pane) => LabelNumber(pane.BridgeLabel) ?? 0;

    /// <summary>Every live pane's bridge number (skips any unlabeled).</summary>
    private List<int> BridgeNumbers() => BridgePanes.Select(BridgeNumberOf).Where(n => n > 0).ToList();

    /// <summary>The compact roster invariant makes the next identity exactly one past the live pane count.</summary>
    private int NextBridgeNumber() => BridgePanes.Count + 1;

    private static string AgentLabel(ChatViewModel pane, int number) => $"{pane.AgentDisplay} {number}";

    /// <summary>
    /// Renumber every survivor after a pane leaves, then tell each live provider session both its new identity and
    /// the complete peer roster. <see cref="ChatViewModel.AppendSystemPrompt"/> is refreshed for any later restart;
    /// <see cref="ChatViewModel.Prelude"/> carries the correction into the session that is already running.
    /// </summary>
    private void CompactBridgeRosterAfterDeparture(string cwd, int departedNumber, string departedAgent, string what,
        string glyph, LiveBridge? target = null)
    {
        var panes = PanesFor(target);
        var board = BoardFor(target);
        if (panes.Count == 0) return;
        var renumbered = panes
            .Select((pane, i) => (Pane: pane, OldNumber: BridgeNumberOf(pane), NewNumber: i + 1))
            .ToList();
        var roster = Enumerable.Range(1, renumbered.Count).ToArray();
        // The manager's number can shift with everyone else's; every rebuilt brief must point at its NEW number.
        var managerNumber = renumbered.FirstOrDefault(x => x.Pane.IsBridgeManager).NewNumber;   // 0 when no manager

        RewriteBridgeFileRoster(cwd, board, renumbered.Select(x => (x.OldNumber, x.NewNumber)).ToList());

        foreach (var group in renumbered.Where(e => e.Pane.PeerMailbox is { Closed: false })
                     .GroupBy(e => e.Pane.PeerMailbox!.Store))
        {
            try { group.Key.Renumber(group.ToDictionary(e => e.Pane.PeerMailbox!, e => e.NewNumber)); }
            catch (Exception ex)
            {
                foreach (var entry in group)
                    entry.Pane.Items.Add(new BannerItem { Level = "warning", Text = "Could not renumber bridge mailboxes: " + ex.Message });
            }
        }

        foreach (var entry in renumbered)
        {
            entry.Pane.BridgeLabel = AgentLabel(entry.Pane, entry.NewNumber);
            entry.Pane.IsBridgeHost = entry.NewNumber == 1;
            UpdateGeneratedBridgeTitle(entry.Pane, entry.NewNumber);
            entry.Pane.AppendSystemPrompt = BridgePrompt(board, entry.Pane, entry.NewNumber, roster, managerNumber);

            var peers = string.Join(", ", roster.Where(n => n != entry.NewNumber).Select(n => $"agent #{n}"));
            var oldIdentity = entry.OldNumber > 0 ? entry.OldNumber : entry.NewNumber;
            var note = $"[BRIDGE] {departedAgent} agent #{departedNumber} {what}. The live bridge was renumbered " +
                       $"contiguously. You were agent #{oldIdentity}; you are now {entry.Pane.AgentDisplay} agent #{entry.NewNumber} " +
                       $"of {roster.Length}. Your active peers are {(peers.Length > 0 ? peers : "none")}. Use Agent " +
                       $"#{entry.NewNumber} for your block in {board} from now on. The departed agent's claimed area is free.";
            entry.Pane.Prelude = string.IsNullOrEmpty(entry.Pane.Prelude) ? note : entry.Pane.Prelude + "\n" + note;

            var identityChange = oldIdentity == entry.NewNumber
                ? ""
                : $" · you are now {entry.Pane.AgentDisplay} #{entry.NewNumber}";
            entry.Pane.Items.Add(new DividerItem
            {
                Label = $"{glyph} {departedAgent} #{departedNumber} {what}{identityChange} · {roster.Length} active",
            });
        }
    }

    /// <summary>Keep the generated bridge title aligned with a compacted identity; preserve user/original titles.</summary>
    private static void UpdateGeneratedBridgeTitle(ChatViewModel pane, int number)
    {
        var prefix = $"Bridge · {pane.AgentDisplay} ";
        if (pane.Title.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(pane.Title[prefix.Length..], out _))
            pane.Title = prefix + number;
    }

    /// <summary>
    /// Re-key the live two-line status-board blocks without losing their task notes. Blocks that no longer belong to
    /// a live pane are removed from Active, preventing the departed old #1 from colliding with the new compact #1.
    /// </summary>
    private static void RewriteBridgeFileRoster(string cwd, string board, IReadOnlyList<(int OldNumber, int NewNumber)> renumbered)
    {
        try
        {
            var path = Path.Combine(cwd, board);
            if (!File.Exists(path)) return;
            var lines = File.ReadAllLines(path).ToList();
            var activeStart = lines.FindIndex(line => line.Trim().Equals("## Active", StringComparison.OrdinalIgnoreCase));
            if (activeStart < 0) return;
            var activeEnd = lines.FindIndex(activeStart + 1,
                line => line.StartsWith("## ", StringComparison.Ordinal));
            if (activeEnd < 0) activeEnd = lines.Count;

            var header = new System.Text.RegularExpressions.Regex(
                @"^\s*-?\s*Agent #(?<number>\d+)\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var blocks = new Dictionary<int, List<string>>();
            for (var i = activeStart + 1; i < activeEnd;)
            {
                var match = header.Match(lines[i]);
                if (!match.Success) { i++; continue; }
                var number = int.Parse(match.Groups["number"].Value);
                var block = new List<string> { lines[i++] };
                while (i < activeEnd && !header.IsMatch(lines[i])) block.Add(lines[i++]);
                while (block.Count > 1 && string.IsNullOrWhiteSpace(block[^1])) block.RemoveAt(block.Count - 1);
                blocks.TryAdd(number, block); // malformed duplicate headers must not create duplicate live identities
            }

            var rewritten = lines.Take(activeStart + 1).ToList();
            rewritten.Add("");
            foreach (var (oldNumber, newNumber) in renumbered)
            {
                if (oldNumber > 0 && blocks.TryGetValue(oldNumber, out var saved))
                {
                    var block = saved.ToList();
                    block[0] = header.Replace(block[0], m =>
                    {
                        var offset = m.Groups["number"].Index - m.Index;
                        return m.Value[..offset] + newNumber + m.Value[(offset + m.Groups["number"].Length)..];
                    }, 1);
                    for (var i = 1; i < block.Count; i++)
                    {
                        // Only the generated seed note owns this identity token; leave human-authored task notes intact.
                        if (block[i].StartsWith("_(Agent #", StringComparison.Ordinal)
                            && block[i].Contains("just joined", StringComparison.OrdinalIgnoreCase))
                            block[i] = block[i].Replace($"Agent #{oldNumber}", $"Agent #{newNumber}", StringComparison.Ordinal);
                    }
                    rewritten.AddRange(block);
                }
                else
                {
                    rewritten.Add($"Agent #{newNumber}: Working on — (figuring out what to do…)");
                    rewritten.Add($"_(Agent #{newNumber} was renumbered; this updates once it picks up a task.)_");
                }
                rewritten.Add("");
            }
            rewritten.AddRange(lines.Skip(activeEnd));
            RewriteLiveActivityRoster(rewritten, renumbered, header);
            File.WriteAllLines(path, rewritten);
        }
        catch { /* best-effort coordination file */ }
    }

    /// <summary>Re-key the real-time "## Live activity" blocks with the same old→new mapping the Active section got,
    /// and drop blocks that belonged to departed agents so a stale snapshot never masquerades as a live peer's.</summary>
    private static void RewriteLiveActivityRoster(List<string> lines,
        IReadOnlyList<(int OldNumber, int NewNumber)> renumbered, System.Text.RegularExpressions.Regex header)
    {
        var start = lines.FindIndex(line => line.Trim().Equals("## Live activity", StringComparison.OrdinalIgnoreCase));
        if (start < 0) return;
        var end = lines.FindIndex(start + 1, line => line.StartsWith("## ", StringComparison.Ordinal));
        if (end < 0) end = lines.Count;
        var map = new Dictionary<int, int>();
        foreach (var (oldNumber, newNumber) in renumbered)
            if (oldNumber > 0) map.TryAdd(oldNumber, newNumber);

        var kept = new List<string>();
        var i = start + 1;
        while (i < end && !header.IsMatch(lines[i])) kept.Add(lines[i++]);   // seed note / spacing stays put
        while (i < end)
        {
            var number = int.Parse(header.Match(lines[i]).Groups["number"].Value);
            var block = new List<string> { lines[i++] };
            while (i < end && !header.IsMatch(lines[i])) block.Add(lines[i++]);
            if (!map.TryGetValue(number, out var renewed)) continue;   // departed agent's snapshot
            block[0] = header.Replace(block[0], m =>
            {
                var offset = m.Groups["number"].Index - m.Index;
                return m.Value[..offset] + renewed + m.Value[(offset + m.Groups["number"].Length)..];
            }, 1);
            kept.AddRange(block);
        }
        lines.RemoveRange(start + 1, end - start - 1);
        lines.InsertRange(start + 1, kept);
    }

    /// <summary>Best-effort: mark agent #n's block on the status board as gone (left/errored) so a peer that
    /// re-skims the file knows that area is now unowned. Only rewrites the "Agent #n:" header line - it leaves any
    /// plain-English note a peer wrote underneath alone, and no-ops if the block isn't there.</summary>
    private static void MarkBridgeFilePeerGone(string cwd, string board, int n, string reason)
    {
        if (n <= 0) return;
        try
        {
            var path = Path.Combine(cwd, board);
            if (!File.Exists(path)) return;
            var lines = File.ReadAllLines(path);
            var rx = new System.Text.RegularExpressions.Regex(@"^\s*-?\s*Agent #" + n + @"\b");
            for (int i = 0; i < lines.Length; i++)
                if (rx.IsMatch(lines[i]))
                {
                    lines[i] = $"Agent #{n}: — {reason} (area now unowned)";
                    File.WriteAllLines(path, lines);
                    return;
                }
        }
        catch { /* best-effort coordination file */ }
    }

    /// <summary>Panes already announced as errored, so a flapping process doesn't spam the survivors with dividers.</summary>
    private readonly HashSet<ChatViewModel> _bridgeErrored = new();

    /// <summary>Watch a bridge pane's status: when a live peer crashes (Status → error) announce it ONCE so the others
    /// know its claimed area is unowned and free its block on the board. Re-arms if the pane recovers (e.g. resumes).</summary>
    private void OnBridgePaneStatusChanged(ChatViewModel c)
    {
        if (!TryGetLiveBridge(c, out var bridge)) return;
        RefreshBridgeWorkingCue(bridge.Panes);
        if (c.Status == "error")
        {
            if (!bridge.Errored.Add(c)) return;   // already announced this error
            var n = BridgeNumberOf(c);
            MarkBridgeFilePeerGone(c.Cwd, bridge.Board, n, "hit an error and stopped");
            var active = bridge.Panes.Count(p => !bridge.Errored.Contains(p));
            var agent = c.AgentDisplay;
            foreach (var peer in bridge.Panes.Where(p => !ReferenceEquals(p, c)))
            {
                var note = $"[BRIDGE] {agent} agent #{n} hit an error and stopped — {active} agent(s) still active. Anything it " +
                           $"had claimed on the status board ({bridge.Board}) is now unowned.";
                peer.Prelude = string.IsNullOrEmpty(peer.Prelude) ? note : peer.Prelude + "\n" + note;
                peer.Items.Add(new DividerItem { Label = $"⚠ {agent} #{n} errored" });
            }
        }
        else if (c.Status is "running" or "idle")
        {
            bridge.Errored.Remove(c);
            _bridgeErrored.Remove(c);   // recovered → a future error can announce again
        }
    }

    /// <summary>System-prompt appendix telling each bridge agent who its peers are and how tightly to coordinate.
    /// Default: LIGHTLY - at the level of "who owns what area", NOT by tracking every edit, deliberately terse to keep
    /// bridge token use low. With <see cref="AppSettings.BridgeRealtimeSharing"/> on, each agent additionally keeps one
    /// compact rewritten-in-place block on a "## Live activity" board (current file(s) + what it's adding), updated at
    /// checkpoints rather than per edit so the richer awareness stays cheap.
    /// <paramref name="roster"/> is the actual compact set of live agent numbers, so every provider receives the same
    /// roster while <paramref name="pane"/> gets its own provider identity.</summary>
    private string BridgePrompt(ChatViewModel pane, int index, IReadOnlyCollection<int> roster, int managerNumber) =>
        BridgePrompt(_bridgeBoard, pane, index, roster, managerNumber);

    private static string BridgePrompt(string board, ChatViewModel pane, int index, IReadOnlyCollection<int> roster, int managerNumber)
    {
        var peers = string.Join(", ", roster.Where(i => i != index).OrderBy(i => i).Select(i => "agent #" + i));
        var swarmRule = SwarmPolicy.BridgeRuntimeRule(
            AppSettings.Current.AgentSwarmsEnabled && AppSettings.Current.AgentSwarmsInBridge,
            AppSettings.Current.SwarmMaxWorkers)
            // The peer channel sits between the swarm rule and the role brief: it is addressed to every agent,
            // whereas ManagerClause differs per role and always closes the appendix.
            + (AppSettings.Current.BridgePeerMessaging ? PeerMessagePolicy.BridgeClause(index) : "")
            + ManagerClause(pane, index, managerNumber, roster.Count);
        // The board is still where standing claims live — it is durable and everyone can re-read it. A peer message is
        // for the thing a board cannot do: reach one specific agent about one specific thing, right now.
        var boardRole = AppSettings.Current.BridgePeerMessaging
            ? "the board is for standing claims; use a peer message when you need one specific agent's attention:\n"
            : "the file is the only channel, don't message peers directly:\n";
        if (AppSettings.Current.BridgeRealtimeSharing)
            return "[BRIDGE MODE] You are " + pane.ProviderDisplay + " agent #" + index + " of " + roster.Count + " working in this same project " +
                "alongside " + (peers.Length > 0 ? peers : "(peers joining)") + " (more may join). Real-time sharing is ON: peers stay aware of " +
                "each other through `" + board + "` (project root) — an area-claims board plus a live-activity board. Keep both current; " +
                boardRole +
                "- Under \"## Active\" keep your two-line block: an `Agent #" + index + ": Working on <the thing>` line, then a plain-English " +
                "note (≤100 words) on what you're doing and how. Refresh it if your task changes.\n" +
                "- Under \"## Live activity\" keep exactly ONE compact block for yourself: an `Agent #" + index + " » <file path(s)>` line plus " +
                "1-2 short lines naming what you're adding or changing there right now (function/class names and a few words — never diffs or " +
                "code). REWRITE that block in place whenever you start, switch to, or finish a file, or after several consecutive edits to the " +
                "same file. Never append history — the board is a snapshot, and per-edit logging is a bug (it burns everyone's tokens).\n" +
                "- Before editing a file, glance at peers' \"## Live activity\" lines. If a peer lists the file you're about to touch, hold off " +
                "or pick different work, and say so in your block.\n" +
                "- FIRST ACTION, before any other work: READ `" + board + "`, pick an area no peer claimed, then write BOTH your " +
                "\"## Active\" block AND your first \"## Live activity\" block. Yours is seeded as \"figuring out what to do\", and leaving it " +
                "that way is a bug. Do the read and the writes before you report back, then continue with the request.\n" +
                "- Only touch a peer's files after glancing at their blocks first. Otherwise just work; assume peers own their areas.\n" +
                swarmRule;
        return "[BRIDGE MODE] You are " + pane.ProviderDisplay + " agent #" + index + " of " + roster.Count + " working in this same project " +
            "alongside " + (peers.Length > 0 ? peers : "(peers joining)") + " (more may join). Coordinate at a HIGH LEVEL only — " +
            "you do NOT need to know or track what the others are editing line-by-line, and you should NOT narrate your own edits to them. " +
            "Just avoid working on the same thing:\n" +
            "- `" + board + "` (project root) is a STATUS BOARD. Under \"## Active\" each agent has a two-line block:\n" +
            "  an `Agent #" + index + ": Working on <the thing>` line, then a plain-English note (≤100 words) on what you're doing and how.\n" +
            "- FIRST ACTION, before any other work: READ `" + board + "` to see what your peers claimed, and pick a " +
            "DIFFERENT area. Then, as soon as you know your task, REWRITE your own block (the \"Working on\" line AND the note) " +
            "to say plainly what you're building — it is seeded as \"figuring out what to do\", and leaving it that way is a bug. " +
            "Do the read and the write before you report back, then continue with the request. Refresh it if your task changes; " +
            "it's a status, not a per-edit changelog.\n" +
            "- Only touch a peer's files after glancing at their block first. Otherwise just work; assume peers own their areas.\n" +
            swarmRule;
    }

    /// <summary>Prelude for a pane that was ALREADY mid-conversation when the bridge formed. Its system prompt is frozen,
    /// so the rules have to ride the next user message — and appended that way an agent reads them as background and
    /// carries on with the user's task without ever claiming its block. Lead with the state change and one explicit
    /// first action so the board actually gets read and written before the agent continues.</summary>
    private string BridgeJoinPrelude(ChatViewModel pane, int index, IReadOnlyCollection<int> roster, int managerNumber) =>
        BridgeJoinPrelude(_bridgeBoard, pane, index, roster, managerNumber);

    private static string BridgeJoinPrelude(string board, ChatViewModel pane, int index, IReadOnlyCollection<int> roster, int managerNumber) =>
        "[BRIDGE] You have just been put into a VibeCode Bridge. This is NEW state — it was not true earlier in this " +
        "conversation, so don't assume anything below was already handled. Before you continue with anything else: read " +
        "`" + board + "` in the project root, then rewrite your own `Agent #" + index + "` block there to say what " +
        "you are working on. Then carry on with the request.\n\n" + BridgePrompt(board, pane, index, roster, managerNumber);

    /// <summary>
    /// Prelude for a pane that arrived by FORKING another bridge. Its transcript is the original agent's, right down
    /// to that bridge's rules and board, so the first thing it has to be told is that the world changed: this is a
    /// new roster, on a new board, and the agents it remembers are still working elsewhere.
    /// </summary>
    private static string BridgeForkPrelude(string board, string sourceBoard, ChatViewModel pane, int index,
        IReadOnlyCollection<int> roster, int managerNumber) =>
        "[BRIDGE FORK] This conversation was just FORKED into a new bridge. Everything above happened in the previous " +
        "bridge and is yours to keep using - but that roster is still running separately, so do not assume your old " +
        "peers are picking anything up here, and do not act on work you had handed to them. You are now agent #" + index +
        " of " + roster.Count + " in a fresh roster, and your status board is `" + board + "` (NOT `" + sourceBoard +
        "`, which belongs to the bridge you came from). Before anything else: read `" + board + "` and write your own " +
        "`Agent #" + index + "` block. Then carry on.\n\n" + BridgePrompt(board, pane, index, roster, managerNumber);

    /// <summary>The "## Live activity" board seeded when real-time sharing is on. It sits BEFORE "## Active", which
    /// must stay the last section because <see cref="AppendPeerToBridgeFile"/> seeds identities by appending at EOF.</summary>
    private const string LiveActivitySection =
        "## Live activity\n" +
        "_(Real-time sharing is ON: each agent keeps exactly ONE compact block here — an `Agent #N » <file path(s)>` line\n" +
        "plus 1-2 short lines on what it's adding/changing there. Rewrite your block in place at checkpoints; never\n" +
        "append history.)_\n\n";

    /// <summary>Reset the coordination file to a fresh scaffold at the start of a new bridge (clears stale entries).
    /// With <paramref name="preserveManagerPlan"/> (a managed bridge resuming), the manager's "## Manager plan"
    /// section is carried over so the lane assignments survive the restart instead of being re-derived from scratch.</summary>
    private void WriteBridgeFile(string cwd, bool preserveManagerPlan = false) =>
        WriteBridgeFile(cwd, _bridgeBoard, preserveManagerPlan);

    private static void WriteBridgeFile(string cwd, string board, bool preserveManagerPlan = false)
    {
        try
        {
            var path = Path.Combine(cwd, board);
            var plan = preserveManagerPlan ? ReadManagerPlanSection(path) : "";
            var realtime = AppSettings.Current.BridgeRealtimeSharing;
            File.WriteAllText(path,
                "# Bridge coordination\n\n" +
                "Several coding agents share this project (VibeCode Bridge). They may use different providers. This is a lightweight STATUS BOARD:\n" +
                "under \"## Active\" each instance keeps a two-line block — an `Agent #N: Working on <thing>` line, then a\n" +
                "plain-English note (≤100 words) on what it's doing and how. It is NOT a changelog — don't log individual\n" +
                "edits. Read it BEFORE your first edit, pick a different area than the others, then rewrite your OWN block —\n" +
                "leaving it as \"figuring out what to do\" means your peers cannot see what you took.\n\n" +
                (plan.Length > 0 ? plan + "\n" : "") +
                (realtime ? LiveActivitySection : "") +
                "## Active\n");
        }
        catch { /* best-effort coordination file */ }
    }

    /// <summary>The manager's "## Manager plan" section of an existing coordination file (empty when absent).</summary>
    private static string ReadManagerPlanSection(string path)
    {
        if (!File.Exists(path)) return "";
        var lines = File.ReadAllLines(path).ToList();
        var start = lines.FindIndex(line => line.Trim().Equals("## Manager plan", StringComparison.OrdinalIgnoreCase));
        if (start < 0) return "";
        var end = lines.FindIndex(start + 1, line => line.StartsWith("## ", StringComparison.Ordinal));
        if (end < 0) end = lines.Count;
        return string.Join("\n", lines.Skip(start).Take(end - start)).TrimEnd() + "\n";
    }

    /// <summary>Insert the "## Live activity" board into an existing coordination file when real-time sharing turns on
    /// mid-bridge. Placed before "## Active" so peer seeding can keep appending at EOF. No-op if already present.</summary>
    private static void EnsureLiveActivitySection(string cwd, string board)
    {
        try
        {
            var path = Path.Combine(cwd, board);
            if (!File.Exists(path)) return;
            var lines = File.ReadAllLines(path).ToList();
            if (lines.Any(line => line.Trim().Equals("## Live activity", StringComparison.OrdinalIgnoreCase))) return;
            var insert = LiveActivitySection.TrimEnd('\n').Split('\n').Append("").ToList();
            var activeStart = lines.FindIndex(line => line.Trim().Equals("## Active", StringComparison.OrdinalIgnoreCase));
            if (activeStart < 0) lines.AddRange(insert);
            else lines.InsertRange(activeStart, insert);
            File.WriteAllLines(path, lines);
        }
        catch { /* best-effort coordination file */ }
    }

    /// <summary>Seed a peer's identity into the coordination file so it ALWAYS lists every live agent, even ones
    /// that never write to it themselves (this is what makes peers aware of each other).</summary>
    private void AppendPeerToBridgeFile(string cwd, int index) => AppendPeerToBridgeFile(cwd, _bridgeBoard, index);

    private static void AppendPeerToBridgeFile(string cwd, string board, int index)
    {
        try { File.AppendAllText(Path.Combine(cwd, board), $"Agent #{index}: Working on — (figuring out what to do…)\n_(Agent #{index} just joined; this updates once it picks up a task.)_\n\n"); }
        catch { /* best-effort */ }
    }

    // ================= Bridge manager: one crowned pane is the "brain" that runs the others =================
    // The user crowns one pane as MANAGER. From then on the user directs the project through that pane; the manager
    // assigns lanes to the other panes by writing @@DISPATCH blocks in its replies, which the app extracts and
    // delivers into the target panes as their next user turn. The app closes the loop by auto-reporting worker
    // turn results / errors / roster changes back to the manager, so dispatching continues — hands-free — until the
    // manager decides the project is done. Nothing here interrupts a running pane: dispatches queue behind live
    // turns, which is also how the user can keep talking to the manager while every worker stays busy.

    /// <summary>The crowned pane of a roster, if any.</summary>
    private static ChatViewModel? ManagerOf(IEnumerable<ChatViewModel> panes) =>
        panes.FirstOrDefault(p => p.IsBridgeManager);

    /// <summary>The crowned pane's roster number (0 when the roster has no manager).</summary>
    private static int ManagerNumberIn(IEnumerable<ChatViewModel> panes) =>
        ManagerOf(panes) is { } m ? BridgeNumberOf(m) : 0;

    /// <summary>Crown a pane as the bridge MANAGER (moving the crown if another pane holds it), or step the current
    /// manager down when it is clicked again. Crowning re-briefs every live session and immediately sends the new
    /// manager an activation message so it takes charge without the user having to prompt it.</summary>
    public void ToggleBridgeManager(ChatViewModel pane)
    {
        if (!BridgePanes.Contains(pane)) return;
        // In Demon Mode the crown is structural, not a user choice: stepping the orchestrator down would leave fifteen
        // read-only workers with nobody able to dispatch to them and nobody able to type into them either.
        if (IsDemonMode) return;

        if (pane.IsBridgeManager)
        {
            // Step down: back to a flat bridge of equal peers. Must also kill in-flight manager traffic —
            // work orders and MANAGER UPDATEs are ordinary queued Sends, so without a purge they still
            // land on workers (and the former manager) after the crown is gone.
            foreach (var p in BridgePanes) p.IsBridgeManager = false;   // belt-and-suspenders: only one should be true
            RefreshBridgeManagerBriefs();
            CancelAllManagerTraffic("manager stepped down — pending dispatches cancelled");
            foreach (var p in BridgePanes)
            {
                var note = "[BRIDGE] The manager role was removed by the user — no manager is assigned now. " +
                           "Coordinate as equal peers via the status board again; nobody is dispatching work.";
                // Replace any staged manager-loop prelude with the step-down notice only (don't stack "fold into plan").
                p.ClearManagerPreludes();
                p.Prelude = string.IsNullOrEmpty(p.Prelude) ? note : p.Prelude + "\n" + note;
                p.Items.Add(new DividerItem { Label = "👑 manager stepped down — no manager assigned" });
            }
            NoteBridgeActivity();
            SaveBridge();
            RaiseBridgeUi();
            return;
        }

        var previous = ManagerOf(BridgePanes);
        if (previous is not null) previous.IsBridgeManager = false;
        pane.IsBridgeManager = true;
        RefreshBridgeManagerBriefs();
        // Seed the status ledger so a worker that is ALREADY mid-turn still gets its finish reported to the new manager.
        foreach (var p in BridgePanes) _bridgeSeenStatus[p] = p.Status;

        var m = BridgeNumberOf(pane);
        foreach (var p in BridgePanes.Where(p => !ReferenceEquals(p, pane)))
        {
            var demoted = ReferenceEquals(p, previous) ? "You are NO LONGER the manager. " : "";
            var note = $"[BRIDGE] {demoted}{pane.AgentDisplay} agent #{m} was just made this bridge's MANAGER by the " +
                       "user. From now on messages starting \"👑 [FROM MANAGER\" are your work orders. End every " +
                       "finished task with a short factual report (what you did / verified / anything blocking) — the " +
                       "end of your reply is relayed to the manager automatically. Let the manager assign lanes " +
                       "instead of picking up new areas on your own.";
            p.Prelude = string.IsNullOrEmpty(p.Prelude) ? note : p.Prelude + "\n" + note;
            p.Items.Add(new DividerItem { Label = $"👑 {pane.AgentDisplay} #{m} is now the manager" });
        }

        pane.Items.Add(new DividerItem { Label = $"👑 {pane.BridgeLabel} is now the bridge manager" });
        pane.Send(ManagerKickoff(pane, m, BridgePanes.Where(p => !ReferenceEquals(p, pane)).ToList()));
        NoteBridgeActivity();
        SaveBridge();
        RaiseBridgeUi();
    }

    /// <summary>Rebuild every live pane's system-prompt appendix so restarts see the same manager arrangement the
    /// running sessions were told about via preludes.</summary>
    private void RefreshBridgeManagerBriefs()
    {
        var roster = BridgeNumbers();
        var managerNumber = ManagerNumberIn(BridgePanes);
        foreach (var p in BridgePanes)
        {
            var n = BridgeNumberOf(p);
            if (n > 0) p.AppendSystemPrompt = BridgePrompt(p, n, roster, managerNumber);
        }
    }

    /// <summary>The activation message a freshly crowned manager receives (as its next user turn, so it acts NOW).</summary>
    private static string ManagerKickoff(ChatViewModel pane, int m, IReadOnlyList<ChatViewModel> workers)
    {
        var roster = workers.Count == 0
            ? "You have NO workers right now — until agents join, you do the work yourself."
            : "Your workers right now: " + string.Join(", ",
                  workers.Select(w => $"{w.AgentDisplay} agent #{BridgeNumberOf(w)} ({(w.IsWorking ? "working" : w.Status)})")) +
              ". Roster changes are reported to you automatically.";
        return $"👑 [MANAGER] You are now the MANAGER of this bridge — {pane.AgentDisplay} agent #{m}. {roster}\n\n" +
               "The user runs the project through you, and the app relays every worker's turn result to you. To put " +
               "a worker to work, include in your reply (one block per assignment):\n" +
               "@@DISPATCH agent=<number, or all>\n<that worker's complete, self-contained prompt>\n@@END\n\n" +
               "First: read `.vibecode-bridge.md` and skim the project. If the mission is already clear from this " +
               "conversation or the board, write your lane plan under a \"## Manager plan\" section in that file and " +
               "dispatch every idle worker NOW. If the mission is not clear yet, ask the user for it — don't invent one.";
    }

    /// <summary>System-prompt appendix per role. Workers learn who the brain is and how orders arrive; the manager
    /// gets the dispatch grammar and the run-the-project-to-completion loop. Empty when the roster has no manager.</summary>
    private static string ManagerClause(ChatViewModel pane, int index, int managerNumber, int rosterCount)
    {
        // Demon Mode replaces the brief entirely rather than layering onto it: its orchestrator has a narrower job
        // (plan + dispatch, no review) and its workers are unreachable by the user, so the ordinary "coordinate as
        // peers" framing would be actively wrong for both.
        if (pane.IsDemonOrchestrator)
            return DemonModePolicy.OrchestratorBrief(Math.Max(1, rosterCount - 1),
                AppSettings.Current.DemonOrchestratorReviewsWork);
        if (pane.IsDemonWorker) return DemonModePolicy.WorkerBrief(index, Math.Max(1, rosterCount - 1));

        if (managerNumber <= 0) return "";
        if (index != managerNumber)
            return "\n[BRIDGE MANAGER] Agent #" + managerNumber + " is this bridge's MANAGER (the brain): the user " +
                   "directs the project through it and it assigns the work. Messages beginning \"👑 [FROM MANAGER\" " +
                   "are your work orders — do them inside the lane they define, honoring any \"don't touch\" " +
                   "constraints. End every finished task with a short factual report (what you did / verified / " +
                   "anything blocking); the end of your reply is relayed to the manager automatically. Stay in your " +
                   "lane: the manager reassigns lanes, so don't grab unclaimed work on your own.";
        return "\n[BRIDGE MANAGER — THIS IS YOU] You are the MANAGER (the brain) of this bridge. The user runs the " +
               "project through YOU; the other agents are your workers, each a live session of its own.\n" +
               "- To assign work, put one or more dispatch blocks in your reply, each formatted EXACTLY:\n" +
               "@@DISPATCH agent=<worker number, or all>\n" +
               "<that worker's complete, self-contained prompt: goal, files/areas, constraints, what NOT to touch>\n" +
               "@@END\n" +
               "When your reply finishes, the app extracts each block and delivers it INTO that worker's session " +
               "(workers never see the rest of your reply; text outside blocks is yours to the user). No blocks = " +
               "nothing dispatched. Never write a literal line starting with @@DISPATCH unless you mean it to fire — " +
               "when merely explaining the syntax, describe it in prose. If a lane cannot start until another " +
               "finishes, record it on the header line: `@@DISPATCH agent=7 depends=3,4`.\n" +
               "- Decompose the project into NON-OVERLAPPING lanes (disjoint files/areas), one per worker, and keep " +
               "the current assignments under a \"## Manager plan\" section you maintain in `.vibecode-bridge.md` " +
               "(rewrite it in place — it is your memory if anything restarts).\n" +
               "- The app AUTOMATICALLY messages you \"👑 [MANAGER UPDATE]\" whenever a worker finishes (with its " +
               "report), errors, joins, or leaves. React every time: verify/accept the work, update the plan, then " +
               "IMMEDIATELY dispatch the freed or new worker its next unclaimed lane — never a lane someone else is " +
               "on. An idle worker is wasted capacity; keep every worker busy until the project is genuinely done. " +
               "The user should never have to say \"continue\".\n" +
               "- Dispatching to a busy worker is fine: it queues and arrives the moment that worker's current turn " +
               "ends — use this to steer or extend workers without interrupting them.\n" +
               "- When every lane is done, dispatch a final verification pass (build/tests/run), then tell the user " +
               "the project is complete and STOP dispatching. Do not invent filler work.\n" +
               "- The user may talk to you at ANY time while workers run (questions, ideas, new requirements): " +
               "answer directly, fold their input into the plan, and dispatch accordingly.\n" +
               "- If you have no workers right now, do the work yourself in this session.";
    }

    /// <summary>Last seen Status per live bridge pane, for edge-detecting real turn completions ("running" → "idle")
    /// against boot transitions and repeats. Entries drop out when a pane closes or leaves every roster.</summary>
    private readonly Dictionary<ChatViewModel, string> _bridgeSeenStatus = new();

    /// <summary>The manager loop's event source, fed by <see cref="Track"/> on every pane status change: when the
    /// crowned pane finishes a turn its reply is scanned for dispatch blocks; when a worker finishes (or errors),
    /// the result is relayed to the crowned pane so it can assign the next lane. Covers parked rosters too — a
    /// managed bridge keeps driving itself in the background.</summary>
    private void OnBridgeManagerStatusChanged(ChatViewModel c)
    {
        var now = c.Status;
        _bridgeSeenStatus.TryGetValue(c, out var prev);
        if (now == "closed" || !TryGetLiveBridge(c, out var bridge))
        {
            _bridgeSeenStatus.Remove(c);
            ForgetPeerChain(c);
            OnSupervisionStatusChanged(c, prev ?? "");   // a closed pane must still be struck from the ledger
            return;
        }
        _bridgeSeenStatus[c] = now;
        // Supervision runs BEFORE the crown check: it owns the ledger whether or not a dispatch loop is live, and a
        // team whose manager stepped down still has assignments that must not be left in an unknown state.
        OnSupervisionStatusChanged(c, prev ?? "");

        var turnEnded = prev == "running" && now == "idle";

        // Peer messaging is NOT a manager feature: agents address each other on a flat roster too, which is exactly
        // the case the status board served worst. So it is routed for EVERY pane, before the crown is even consulted.
        if (turnEnded) RoutePeerMessages(c, bridge);

        // A mailbox control turn is not evidence that an assigned lane finished. Still route its peer replies
        // and receipts above, but do not wake the manager's work-completion loop for it.
        if (c.IsPeerNotificationTurn) return;

        // No crown ⇒ no dispatch loop, no worker→manager relays. Flat peers only.
        var manager = ManagerOf(bridge.Panes);
        if (manager is null || !manager.IsBridgeManager) return;

        if (ReferenceEquals(c, manager))
        {
            if (turnEnded) RouteManagerDispatches(manager, bridge.Panes);
            return;
        }

        if (turnEnded)
        {
            var report = Tail(AgentDirectiveParser.Strip(c.LastTurnReplyText(), new[]
                { AgentDirectiveParser.MessageVerb, AgentDirectiveParser.ReadVerb, AgentDirectiveParser.AnsweredVerb }), 1800);
            if (report.Length == 0) return;   // an interrupted/empty turn carries nothing worth relaying
            // Demon Mode's orchestrator plans and dispatches; it does NOT sit in judgement on worker output unless the
            // user explicitly asked for that. Telling it to "check/accept the work" here would quietly reintroduce the
            // review step the mode exists to leave out.
            var reaction = IsDemonMode && !AppSettings.Current.DemonOrchestratorReviewsWork
                ? "Do NOT review or re-do this work. Just record the lane as done, update \"## Manager plan\", then " +
                  $"either dispatch this worker its next unclaimed lane (@@DISPATCH agent={BridgeNumberOf(c)}) or, if " +
                  "every lane is done, tell the user the project is complete."
                : "React per your manager brief: check/accept the work, update \"## Manager plan\", then either " +
                  $"dispatch this worker its next unclaimed lane (@@DISPATCH agent={BridgeNumberOf(c)}) or, if every " +
                  "lane is done, run final verification and tell the user the project is complete.";
            SendManagerUpdate(manager,
                $"{c.AgentDisplay} agent #{BridgeNumberOf(c)} finished its turn and is now idle. The end of its report:\n" +
                "───\n" + report + "\n───\n" + reaction);
        }
        else if (now == "error" && prev != "error")
        {
            SendManagerUpdate(manager,
                $"{c.AgentDisplay} agent #{BridgeNumberOf(c)} hit an ERROR and stopped mid-work. Treat its lane as " +
                "unowned: reassign the remainder to a free worker, or hold it if you expect a recovery (you'll get " +
                "another update when it next finishes anything).");
        }
    }

    /// <summary>Deliver an app-generated event to the crowned pane as its next user turn (queued behind a live one).
    /// Visible in the manager's transcript on purpose: the user can always see what the app told the brain.</summary>
    private void SendManagerUpdate(ChatViewModel manager, string body)
    {
        // Crown may have been removed between the status edge and this call (or the pane was never manager).
        if (!manager.IsBridgeManager) return;
        if (manager.Status is "error" or "closed") return;   // never queue events into a broken session
        if (manager.Send("👑 [MANAGER UPDATE] " + body)) NoteBridgeActivity();
    }

    private static List<DispatchBlock> ParseDispatchBlocks(string reply) => DispatchBlockParser.Parse(reply);

    /// <summary>Extract every @@DISPATCH block from the manager's finished reply and deliver each into its target
    /// pane(s). A busy target simply queues the order; an unknown number is skipped (the roster may have changed
    /// while the manager was thinking — the departure update it also received sorts that out).</summary>
    private void RouteManagerDispatches(ChatViewModel manager, IReadOnlyList<ChatViewModel> panes)
    {
        // Re-check the crown: the manager can step down while its last turn is finishing, and a queued
        // worker.Send would otherwise still land as "FROM MANAGER" after the user turned management off.
        if (!manager.IsBridgeManager) return;
        var reply = manager.LastTurnReplyText();
        // A reply with no @@DISPATCH text at all is normal for an ordinary bridge and for an orchestrator that is
        // still talking to the user — but in Demon Mode it is also exactly what a team that never started looks like.
        if (!DispatchBlockParser.MentionsDispatch(reply)) { NoteDemonTurnWithoutDispatch(manager, panes); return; }
        // This pane really is the manager and its work orders are about to be delivered, so the blocks come off
        // screen: each worker gets the order in its own pane and the manager keeps a "dispatched #N" divider.
        HideRoutedDirectives(manager, AgentDirectiveParser.DispatchVerb);
        var m = BridgeNumberOf(manager);
        var delivered = new List<string>();
        foreach (var (target, attributes, body) in ParseDispatchBlocks(reply))
        {
            if (!manager.IsBridgeManager) break;   // stepped down mid-route
            var dependencies = ParseDependencies(attributes);
            var targets = target.Equals("all", StringComparison.OrdinalIgnoreCase)
                ? panes.Where(p => !ReferenceEquals(p, manager)).ToList()
                : int.TryParse(target, out var num)
                    ? panes.Where(p => !ReferenceEquals(p, manager) && BridgeNumberOf(p) == num).ToList()
                    : new List<ChatViewModel>();
            foreach (var worker in targets)
            {
                if (!manager.IsBridgeManager) break;
                // Under supervision the closing line is a contract, not a nicety: the completion marker is what the
                // ledger reads to tell "the agent finished" from "the agent said something".
                var closing = IsSupervising
                    ? "— Work within this order's scope and constraints. " + DemonModePolicy.ReportingContract
                    : "— Work within this order's scope and constraints. When done, end your reply with a short " +
                      "factual report (what you did / verified / anything blocking); it is relayed to the " +
                      "manager automatically.";
                var wire = $"👑 [FROM MANAGER — {manager.AgentDisplay} agent #{m}] Work order:\n\n{body}\n\n{closing}";
                // A Demon worker may still be waiting its turn in the staggered launch queue. Send is a no-op on a
                // pane with no session yet, and this loop reads that as "not delivered", so the lane would be dropped
                // in silence. A work order is reason enough to jump the queue; no-op for every other kind of pane.
                StartQueuedDemonWorker(worker);
                if (!worker.Send(wire)) continue;
                ResetPeerChain(worker);   // a work order is a fresh start, not a continuation of a peer conversation
                delivered.Add("#" + BridgeNumberOf(worker));
                _demonDispatched = true;   // the team has started; the never-started guard stands down for good
                // The supervisor's ledger is written from the SAME delivery that reached the session, so the two can
                // never disagree about which agent holds which task.
                NoteDispatch(worker, body, dependencies);
            }
        }
        if (delivered.Count == 0)
        {
            // The manager wrote "@@DISPATCH" and believes it just assigned work, but nothing reached a session — a
            // malformed header, or a number nobody on the roster answers to. Saying nothing here is how a lane dies
            // in silence: the manager waits for a report that can never come, and the user watches an idle team.
            if (!manager.IsBridgeManager) return;
            var roster = string.Join(", ", panes.Where(p => !ReferenceEquals(p, manager))
                .Select(p => "#" + BridgeNumberOf(p)).Where(s => s != "#0"));
            manager.Items.Add(new DividerItem { Label = "👑 dispatch not delivered — no valid target in that reply" });
            SupervisionLog.Write(manager.BridgeLabel, "DISPATCH-FAILED", "reply contained @@DISPATCH but matched no worker");
            SendManagerUpdate(manager,
                "Your last reply contained @@DISPATCH but NOTHING was delivered — no work order reached any worker, " +
                "so nobody is working on it. Re-send it now with the header on its own line and a real roster number:\n" +
                "@@DISPATCH agent=<number>\n<the complete self-contained prompt>\n@@END\n" +
                $"Workers available right now: {(roster.Length > 0 ? roster : "none")}.");
            return;
        }
        if (manager.IsBridgeManager)   // only label if the crown still holds (step-down may have purged mid-route)
            manager.Items.Add(new DividerItem { Label = $"👑 dispatched work to agent {string.Join(", ", delivered.Distinct())}" });
        NoteBridgeActivity();
    }

    /// <summary>When the crown drops, drop every still-queued manager-loop prompt on every live pane so workers
    /// (and the former manager) do not keep consuming work orders / join-updates that were staged under management.</summary>
    private void CancelAllManagerTraffic(string dividerLabel)
    {
        var purged = 0;
        foreach (var p in BridgePanes)
        {
            p.ClearManagerPreludes();
            purged += p.PurgeManagerInjectedQueue();
        }
        if (purged > 0 && BridgePanes.Count > 0)
        {
            // One divider on the host is enough signal; per-pane purge already removed the grey queue rows.
            BridgePanes[0].Items.Add(new DividerItem { Label = $"👑 {dividerLabel} ({purged} queued)" });
        }
    }

    /// <summary>The last <paramref name="max"/> chars of a report (whole string when it fits).</summary>
    private static string Tail(string s, int max) => s.Length <= max ? s : "…" + s[^max..];
}
