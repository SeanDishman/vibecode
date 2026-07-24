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

public sealed class MainViewModel : Observable
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
        // Per-account workspace: each Claude/Codex/Grok login only sees the chats that started under it.
        // Switching accounts never deletes the other side's threads — they reappear when you switch back.
        chats.Filter = ChatMatchesActiveAccount;
        ChatGroups = chats;
        _sidebarCollapsed = AppSettings.Current.SidebarCollapsed;   // field, not property: restoring is not a change
    }

    /// <summary>
    /// True when <paramref name="item"/> belongs to the currently selected login for its provider.
    /// Kimi has a single shared login (always shown). Legacy rows with no AccountId stay visible so old
    /// restores aren't orphaned; every new chat captures ActiveId at creation.
    /// </summary>
    private static bool ChatMatchesActiveAccount(object item)
    {
        if (item is not ChatViewModel chat) return false;
        if (string.IsNullOrWhiteSpace(chat.AccountId)) return true;   // pre-isolation snapshots
        if (chat.IsKimi) return true;
        if (chat.IsClaude)
        {
            var active = AccountService.Instance.ActiveId;
            return active is null
                   || string.Equals(chat.AccountId, active, StringComparison.OrdinalIgnoreCase);
        }
        if (chat.IsCodex)
        {
            var active = CodexAccountService.Instance.ActiveId;
            return active is null
                   || string.Equals(chat.AccountId, active, StringComparison.OrdinalIgnoreCase);
        }
        if (chat.IsGrok)
        {
            var active = GrokAccountService.Instance.ActiveId;
            return active is null
                   || string.Equals(chat.AccountId, active, StringComparison.OrdinalIgnoreCase);
        }
        return true;
    }

    /// <summary>Re-apply the account filter after a Claude/Codex/Grok switch and re-home selection if the
    /// focused chat belongs to a different login (it stays open in the background under its own account).</summary>
    public void RefreshChatAccountFilter()
    {
        ChatGroups.Refresh();
        // If the focused chat is now hidden, pick another visible one (or home) so the UI matches the filter.
        if (ActiveChat is { } active && !ChatMatchesActiveAccount(active))
        {
            if (ShowBridge) HideBridge();
            ActiveChat = FirstVisibleChat(preferNot: null);
        }
        if (SecondaryActiveChat is { } secondary && !ChatMatchesActiveAccount(secondary))
            SecondaryActiveChat = FirstVisibleChat(preferNot: ActiveChat) ?? ActiveChat;
    }

    private ChatViewModel? FirstVisibleChat(ChatViewModel? preferNot)
    {
        foreach (var chat in Chats)
        {
            if (preferNot is not null && ReferenceEquals(chat, preferNot)) continue;
            if (ChatMatchesActiveAccount(chat)) return chat;
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
    public string KimiAccountUsage => IsKimiSignedIn ? "usage unavailable" : "";
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
        RefreshChatAccountFilter();   // switch Grok login → only that login's chats in the sidebar
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
        RefreshChatAccountFilter();   // switch Codex login → only that login's chats in the sidebar
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
        RefreshChatAccountFilter();   // switch Claude login → only that login's chats in the sidebar
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

    /// <summary>Re-home an open Codex chat onto another saved account: copy its rollout into that account's private
    /// CODEX_HOME, then respawn the SAME thread there. This is how a conversation escapes an exhausted account —
    /// without it, "switch account" only helps new chats while the open chat keeps erroring on the old login.
    /// Works for normal chats AND bridge panes (the pane keeps its number, role prompt and provider settings).</summary>
    public bool MoveCodexChatToAccount(ChatViewModel chat, string accountId, string? accountLabel = null)
    {
        if (!chat.IsCodex || chat.SessionId is not { } sid) return false;
        if (IsParkedBridgePane(chat)) return false;   // never stop a hidden roster merely because a bulk account move ran
        if (string.Equals(chat.AccountId, accountId, StringComparison.OrdinalIgnoreCase)) return true;   // already there
        CodexAccountService.Instance.MigrateThread(sid, chat.AccountId, accountId);   // best-effort; resume shows an error if the rollout is missing

        var moved = new ChatViewModel(chat.Cwd, resume: sid, fork: false, title: chat.Title,
            accountId: accountId, provider: "codex") { Pinned = chat.Pinned };
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
        var paneIndex = BridgePanes.IndexOf(chat);    // >= 0 when this is a live bridge pane
        chat.Close();
        if (chatIndex >= 0)
        {
            Chats.Remove(chat);
            Chats.Insert(Math.Min(chatIndex, Chats.Count), moved);
        }
        if (paneIndex >= 0)
        {
            BridgePanes[paneIndex] = moved;
            _bridgeErrored.Remove(chat);   // the old errored pane is gone; the moved one announces its own errors
            RaiseBridgeUi();
            // Peers may have been told this agent "hit an error and stopped" while its account was maxed out.
            // Tell them it's back so nobody permanently writes its number off the roster.
            var n = BridgeNumberOf(moved);
            foreach (var peer in BridgePanes.Where(p => !ReferenceEquals(p, moved)))
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
        if (paneIndex >= 0) SaveBridge();
        return true;
    }

    /// <summary>Every open Codex chat/pane that is pinned to a DIFFERENT account than <paramref name="accountId"/>
    /// and can be moved (has a resumable session).</summary>
    public List<ChatViewModel> CodexChatsMovableTo(string accountId) =>
        Chats.Concat(BridgePanes).Distinct()
            .Where(c => c.IsCodex && c.SessionId is not null
                        && !IsParkedBridgePane(c)
                        && !string.Equals(c.AccountId, accountId, StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>Move every eligible open Codex chat/pane onto <paramref name="accountId"/>. Returns (moved, failed).</summary>
    public (int Moved, int Failed) MoveAllCodexChatsToAccount(string accountId, string? accountLabel = null)
    {
        int moved = 0, failed = 0;
        foreach (var chat in CodexChatsMovableTo(accountId))   // snapshot - the move mutates Chats/BridgePanes
        {
            if (MoveCodexChatToAccount(chat, accountId, accountLabel)) moved++;
            else failed++;
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
                var onlyOwned = AppSettings.Current.ShowOnlyOwnedSessions;
                bool Keep(SessionEntry s) => !onlyOwned || owned.Contains(s.SessionId);
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

    public ChatViewModel NewChat(string cwd, string? resume = null, bool fork = false, string? title = null,
        string? provider = null, bool activatePrimary = true)
    {
        if (activatePrimary) HideBridge();   // a running bridge keeps going in the background; just leave its overlay
        provider ??= AppSettings.Current.DefaultProvider;
        var chat = new ChatViewModel(cwd, resume, fork, title, provider: provider);
        // Fresh conversations (including forks) start in VibeCode's Auto policy for every provider.
        // SetMode also records the choice so a provider's initialize event cannot reset the pill to Ask.
        if (resume is null || fork) chat.SetMode("auto");
        Track(chat);
        MarkOwned(chat.SessionId);   // a resumed chat already has its id (set in the ctor, no PropertyChanged)
        Chats.Insert(0, chat);
        RememberRecentDirectory(cwd);   // immediate and provider-neutral; no transcript file needs to exist yet
        if (Chats.Any(c => c.Pinned)) ReorderPinned();   // keep pinned chats above a freshly created one
        if (activatePrimary) ActiveChat = chat;
        if (chat.SessionId is not null)
            SaveSession();   // resumed chats already have an id in the ctor, so no later SessionId change would save them
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
                if (TryGetLiveBridge(c, out var bridge)) SaveBridge(bridge.Panes);
            }
            else if (e.PropertyName == nameof(ChatViewModel.Status))
            {
                OnBridgePaneStatusChanged(c);   // surface a crashed bridge peer to its still-running peers
                OnBridgeManagerStatusChanged(c); // manager loop: route dispatches / relay worker reports
            }
            else if (e.PropertyName is nameof(ChatViewModel.Draft) or nameof(ChatViewModel.Title))
            {
                RequestSave();   // an unsent prompt / a renamed chat must not need a polite shutdown to survive
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
        AppSettings.Current.Save();
        _saveDirty = false;
    }

    private void SnapshotSession()
    {
        var s = AppSettings.Current;
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
                Draft = NullIfEmpty(c.Draft),
            })
            .ToList();
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
        foreach (var bridge in LiveBridges()) SnapshotBridge(bridge.Panes);
        AppSettings.Current.Save();
        _saveDirty = false;
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
        ImportPendingBridgeRecoveries();
        // Remove only malformed snapshots. The five-hour timeout belongs to live idle processes, not conversation
        // history: Claude/Codex transcripts remain resumable after an overnight shutdown.
        PruneSavedBridges();
        // Restore chats that either have a resumable id OR carry an unsent draft (a never-sent first prompt): the
        // latter has no SessionId, so gating restore on the id alone silently threw the draft away on every launch.
        var saved = AppSettings.Current.OpenChats.Where(o => o.SessionId is not null || !string.IsNullOrWhiteSpace(o.Draft)).ToList();
        ChatViewModel? active = null;
        ChatViewModel? secondaryActive = null;
        // saved is newest-first (same order as Chats); Add() appends so the order is preserved
        foreach (var oc in saved)
        {
            var cwd = Directory.Exists(oc.Cwd) ? oc.Cwd : DefaultCwd;
            var chat = new ChatViewModel(cwd, oc.SessionId, fork: false, title: oc.Title, accountId: oc.AccountId, provider: oc.Provider)
            { Pinned = oc.Pinned, Draft = oc.Draft ?? "" };
            Track(chat);
            MarkOwned(oc.SessionId);   // restored chats stay "ours" so their project shows in the list
            Chats.Add(chat);
            chat.Start();
            if (oc.Active) active = chat;
            if (oc.SecondaryActive) secondaryActive = chat;
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
            // Prefer the last-focused chat only when it still belongs to the active login for its provider.
            ActiveChat = active is not null && ChatMatchesActiveAccount(active)
                ? active
                : FirstVisibleChat(preferNot: null) ?? Chats[0];
            SecondaryActiveChat = secondaryActive is not null && ChatMatchesActiveAccount(secondaryActive)
                                  && !ReferenceEquals(secondaryActive, ActiveChat)
                ? secondaryActive
                : FirstVisibleChat(preferNot: ActiveChat) ?? ActiveChat;
        }

        // Put the bridge back on screen if the user was looking at it (its host was the active chat, or the overlay was
        // up when the app died). A bridge that was running in the BACKGROUND stays dormant behind its host cue.
        var bridgeAnchor = ActiveChat is not null && ChatMatchesActiveAccount(ActiveChat) && HasSavedBridgeFor(ActiveChat)
            ? ActiveChat
            : AppSettings.Current.BridgeVisible
                ? Chats.FirstOrDefault(c => ChatMatchesActiveAccount(c) && HasSavedBridgeFor(c))
                : null;
        if (bridgeAnchor is not null) RestoreBridge(bridgeAnchor);
        SecondaryShowBridge = AppSettings.Current.SecondaryBridgeVisible
                              && SecondaryActiveChat is not null
                              && ReferenceEquals(SecondaryActiveChat, BridgePanes.FirstOrDefault());
        RefreshResumableBridges();
        RefreshChatAccountFilter();   // hide other-account rows after restore
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
        var replacement = NewChat(cwd, session.SessionId, title: session.Title, provider: session.Provider,
            activatePrimary: activatePrimary || replacePrimarySelection);
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

    // ================= Bridge: multiple coding agents/providers on one project =================

    /// <summary>The roster currently assigned to the Bridge surface. [0] is its host. It stays populated when the
    /// overlay is hidden; other chats' live rosters reside in _parkedBridges until their host is selected.</summary>
    public BridgePaneCollection BridgePanes { get; } = new();

    /// <summary>
    /// A live roster that is not currently bound to the Bridge surface. Parking is intentionally a view-model-only
    /// operation: its ChatViewModels and provider sessions stay untouched, so returning to the host is a cheap
    /// collection swap instead of three process disposals followed by three transcript replays and CLI launches.
    /// </summary>
    private sealed class LiveBridge
    {
        public required IReadOnlyList<ChatViewModel> Panes { get; init; }
        public ChatViewModel? PanelChat { get; init; }
        public DateTime Activity { get; set; }
        public required HashSet<ChatViewModel> Errored { get; init; }
    }

    private readonly Dictionary<ChatViewModel, LiveBridge> _parkedBridges = new();

    private bool IsParkedBridgePane(ChatViewModel pane) =>
        _parkedBridges.Values.Any(bridge => bridge.Panes.Contains(pane));

    private IEnumerable<LiveBridge> LiveBridges()
    {
        if (BridgePanes.Count > 0)
            yield return new LiveBridge
            {
                Panes = BridgePanes,
                PanelChat = _bridgePanelChat,
                Activity = _bridgeActivity,
                Errored = _bridgeErrored,
            };
        foreach (var bridge in _parkedBridges.Values) yield return bridge;
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

    public void SelectBridgePane(ChatViewModel pane)
    {
        if (!BridgePanes.Contains(pane) || ReferenceEquals(_bridgePanelChat, pane)) return;
        _bridgePanelChat = pane;
        Raise(nameof(PanelChat));
        Raise(nameof(BridgePanelChat));
    }
    /// <summary>A roster currently owns the Bridge surface (the overlay itself may be hidden).</summary>
    public bool IsBridge => BridgePanes.Count > 0;
    public bool CanAddBridgeAgent =>
        BridgeAgentPolicy.CanAdd(BridgePanes.Count, AppSettings.Current.BridgeAgentLimit);
    public string BridgeAgentName => BridgePanes.FirstOrDefault()?.AgentDisplay ?? ActiveChat?.AgentDisplay ?? "Agent";
    public string AddBridgeAgentText => "Add agent";
    /// <summary>Tooltip for Add agent. When the roster is at the Settings cap, spell out the limit so the
    /// button never looks "broken" — a click also opens a dialog that points at Settings.</summary>
    public string AddBridgeAgentToolTip =>
        CanAddBridgeAgent
            ? $"Choose Claude Code, OpenAI Codex, Kimi Code, or Grok (up to {AppSettings.Current.BridgeAgentLimit} agents)"
            : $"Bridge is full ({BridgePanes.Count} / {AppSettings.Current.BridgeAgentLimit}). Raise the limit in Settings → Bridge.";
    /// <summary>The Announce affordance is live whenever a Bridge roster owns the surface.</summary>
    public bool CanAnnounceToBridge => IsBridge;
    public string BridgeSummary
    {
        get
        {
            if (!IsBridge) return "";
            var groups = BridgePanes.GroupBy(p => p.AgentDisplay)
                .Select(g => (Name: g.Key, Count: g.Count()))
                .ToList();
            var roster = groups.Count == 1
                ? $"{groups[0].Count} {groups[0].Name} agent{(groups[0].Count == 1 ? "" : "s")}"
                : $"{BridgePanes.Count} agents ({string.Join(", ", groups.Select(g => $"{g.Count} {g.Name}"))})";
            return $"{roster} on this project — each knows the others are working";
        }
    }
    /// <summary>The full working directory shared by every pane in the active Bridge.</summary>
    public string BridgeProjectPath => BridgePanes.FirstOrDefault()?.Cwd ?? "";

    public bool BridgeUsesClaude => BridgePanes.Any(p => p.Provider == "claude");
    public bool BridgeUsesCodex => BridgePanes.Any(p => p.Provider == "codex");
    public bool BridgeUsesKimi => BridgePanes.Any(p => p.Provider == "kimi");
    public bool BridgeUsesGrok => BridgePanes.Any(p => p.Provider == "grok");
    /// <summary>Unique xAI accounts backing the Grok panes in this Bridge. Multiple agents can share one account,
    /// so quota rows are grouped by account id instead of pretending per-session token totals are account usage.</summary>
    public IEnumerable<GrokAccountInfo> BridgeGrokAccounts => BridgePanes
        .Where(p => p.IsGrok)
        .Select(p => p.GrokAccount)
        .OfType<GrokAccountInfo>()
        .GroupBy(account => account.Id, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.First());
    public string BridgeGrokAccountCountText
    {
        get
        {
            var count = BridgeGrokAccounts.Count();
            return $"{count} Grok account{(count == 1 ? "" : "s")}";
        }
    }
    public string BridgeGrokUsageSummary
    {
        get
        {
            var accounts = BridgeGrokAccounts.ToList();
            if (accounts.Count == 0) return "usage unavailable";
            if (accounts.Count == 1) return accounts[0].UsageSummary;
            var known = accounts.Where(account => account.UsagePercent is not null).ToList();
            return known.Count == 0
                ? $"{accounts.Count} accounts · usage unavailable"
                : $"{accounts.Count} accounts · max {known.Max(account => account.UsagePercent)}%";
        }
    }

    private void RaiseBridgeGrokUsage()
    {
        Raise(nameof(BridgeGrokAccounts));
        Raise(nameof(BridgeGrokAccountCountText));
        Raise(nameof(BridgeGrokUsageSummary));
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

    private void RaiseBridgeUi()
    {
        Raise(nameof(IsBridge));
        Raise(nameof(CanAnnounceToBridge));
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
        Raise(nameof(PanelChat));
        Raise(nameof(BridgePanelChat));
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
            if (enabled && bridge.Panes.FirstOrDefault()?.Cwd is { Length: > 0 } cwd) EnsureLiveActivitySection(cwd);
            var roster = bridge.Panes.Select(BridgeNumberOf).Where(n => n > 0).ToList();
            var managerNumber = ManagerNumberIn(bridge.Panes);
            foreach (var pane in bridge.Panes)
            {
                var n = BridgeNumberOf(pane);
                if (n <= 0) continue;
                pane.AppendSystemPrompt = BridgePrompt(pane, n, roster, managerNumber);
                var note = enabled
                    ? "[BRIDGE] Real-time sharing was just turned ON. From now on also keep exactly ONE compact block under " +
                      "\"## Live activity\" in `.vibecode-bridge.md`: an `Agent #" + n + " » <file path(s)>` line plus 1-2 short " +
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
        if (SecondaryShowBridge && ReferenceEquals(SecondaryActiveChat, host)) SecondaryShowBridge = false;
        _parkedBridges[host] = new LiveBridge
        {
            Panes = BridgePanes.ToList(),
            PanelChat = _bridgePanelChat,
            Activity = _bridgeActivity == default ? DateTime.Now : _bridgeActivity,
            Errored = new HashSet<ChatViewModel>(_bridgeErrored),
        };
        if (clearSurface) BridgePanes.ReplaceAll(Array.Empty<ChatViewModel>());
        _bridgePanelChat = null;
        _bridgeErrored.Clear();
        _bridgeActivity = default;
    }

    /// <summary>Swap a parked roster back into the bound collection; no transcript IO or CLI launch occurs.</summary>
    private bool ActivateParkedBridge(ChatViewModel host, bool showPrimary = true)
    {
        if (!_parkedBridges.Remove(host, out var bridge)) return false;
        BridgePanes.ReplaceAll(bridge.Panes);
        _bridgePanelChat = bridge.PanelChat is not null && BridgePanes.Contains(bridge.PanelChat)
            ? bridge.PanelChat
            : host;
        _bridgeErrored.UnionWith(bridge.Errored);
        _bridgeActivity = bridge.Activity == default ? DateTime.Now : bridge.Activity;
        RefreshBridgeWorkingCue();
        if (showPrimary) ShowBridge = true;
        else
        {
            SecondaryActiveChat = host;
            SecondaryShowBridge = true;
        }
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

        if (_parkedBridges.ContainsKey(host))
        {
            if (IsBridge)
            {
                ShowBridge = false;
                ParkActiveBridge(clearSurface: false);
            }
            return ActivateParkedBridge(host, showPrimary: false);
        }

        if (IsBridge)
        {
            if (ReferenceEquals(host, BridgePanes.FirstOrDefault()))
            {
                SecondaryShowBridge = true;
                NoteBridgeActivity();
                return true;
            }

            // There is one authoritative live roster. Switching it from session 2 leaves the primary on its normal
            // host transcript instead of unexpectedly showing a different project's Bridge.
            ShowBridge = false;
            ParkActiveBridge();
        }

        if (HasSavedBridgeFor(host)) return RestoreBridge(host, showPrimary: false);

        WriteBridgeFile(host.Cwd);
        AppendPeerToBridgeFile(host.Cwd, 1);
        host.BridgeLabel = AgentLabel(host, 1);
        host.IsBridgeHost = true;
        host.IsBridgeManager = false;
        host.Prelude = BridgeJoinPrelude(host, 1, new[] { 1, 2 }, 0);
        host.Items.Add(new DividerItem { Label = $"Bridge activated - you are {host.BridgeLabel}" });
        BridgePanes.Add(host);
        AddBridgeAgent(peerProvider);
        SecondaryShowBridge = true;
        NoteBridgeActivity();
        StartBridgeIdleTimer();
        RaiseBridgeUi();
        return true;
    }

    /// <summary>Add another agent to the bridge up to the configured limit. A null provider matches
    /// the host; the header picker can explicitly add Claude, Codex, Kimi, or Grok to an existing bridge.</summary>
    public void AddBridgeAgent(string? provider = null)
    {
        if (!BridgeAgentPolicy.CanAdd(BridgePanes.Count, AppSettings.Current.BridgeAgentLimit)) return;
        ResetBridgeExpand();               // a new pane must not land hidden behind a focused peer - back to the grid
        var host = BridgePanes[0];
        var cwd = host.Cwd;
        provider = provider?.Trim().ToLowerInvariant() switch
        {
            "claude" => "claude",
            "codex" => "codex",
            "kimi" => "kimi",
            "grok" => "grok",
            _ => host.Provider,
        };
        var k = NextBridgeNumber();       // compact roster => the new pane is always the next contiguous number
        var roster = BridgeNumbers();     // the live roster the new peer joins…
        roster.Add(k);                    // …plus itself
        AppendPeerToBridgeFile(cwd, k);   // seed the new peer's identity so everyone sees it in the roster
        var sameProvider = string.Equals(provider, host.Provider, StringComparison.OrdinalIgnoreCase);
        var managerNumber = ManagerNumberIn(BridgePanes);
        var chat = new ChatViewModel(cwd, accountId: sameProvider ? host.AccountId : null, provider: provider);
        chat.Title = $"Bridge · {chat.AgentDisplay} {k}";
        chat.BridgeLabel = AgentLabel(chat, k);
        chat.AppendSystemPrompt = BridgePrompt(chat, k, roster, managerNumber);
        chat.SetMode(host.Mode);       // new peers inherit the host's permission mode instead of defaulting to "ask"
        if (sameProvider)
        {
            chat.Model = host.Model;   // same-provider peers inherit the host's model + reasoning effort
            chat.Effort = host.Effort; // (plain setters: never rewrite the global defaults while cloning a live pane)
        }
        // A cross-provider peer keeps the defaults loaded by its own constructor; a Codex model id is invalid for Claude.
        Track(chat);
        // Actively tell already-running peers that another agent joined (their system prompt is frozen at spawn,
        // so we push it via the one-shot Prelude that rides their next message) + a visible divider
        var activeCount = BridgePanes.Count + 1;   // compact numbering means the new identity and active count match
        foreach (var peer in BridgePanes)
        {
            var self = BridgeNumberOf(peer);
            var activePeers = string.Join(", ", roster.Where(n => n != self).Select(n => $"agent #{n}"));
            var note = $"[BRIDGE] {chat.AgentDisplay} agent #{k} joined — {activeCount} agents now active. You are " +
                       $"still {peer.AgentDisplay} agent #{self}; your active peers are {activePeers}. No action needed; " +
                       "just don't pick up their area.";
            peer.Prelude = string.IsNullOrEmpty(peer.Prelude) ? note : peer.Prelude + "\n" + note;
            peer.AppendSystemPrompt = BridgePrompt(peer, self, roster, managerNumber); // current session gets Prelude; any restart gets the same full roster
            peer.Items.Add(new DividerItem { Label = $"🔗 {chat.AgentDisplay} #{k} joined the bridge" });
        }
        BridgePanes.Add(chat);
        RefreshBridgeWorkingCue();
        chat.Start();
        // Only while a crown is live: if the user stepped the manager down, new peers just join as equals — no
        // "fold it into the plan" update, and no auto-dispatch chain. Re-read the crown (not a stale managerNumber).
        if (ManagerOf(BridgePanes) is { IsBridgeManager: true } mgr && !ReferenceEquals(mgr, chat))
            SendManagerUpdate(mgr,
                $"{chat.AgentDisplay} agent #{k} just JOINED the bridge and is idle ({activeCount} agents now). " +
                $"Fold it into the plan and put it to work: reply with a @@DISPATCH agent={k} block giving it an " +
                "unclaimed lane (or rebalance lanes if that helps the project finish sooner).");
        NoteBridgeActivity();
        SaveBridge();              // keep the temp-save current
        RaiseBridgeUi();
    }

    /// <summary>
    /// Broadcast one message to every agent on the live Bridge. Each running pane is interrupted first so the
    /// announcement preempts whatever it's doing, then the text is delivered as that pane's next user turn — sent
    /// immediately when the pane is idle, or queued behind the interrupt and auto-flushed the moment its turn ends.
    /// Agents pick up their own work again after reading it. Returns how many panes the announcement reached.
    /// </summary>
    public int AnnounceToBridge(string message)
    {
        var body = (message ?? string.Empty).Trim();
        if (body.Length == 0 || BridgePanes.Count == 0) return 0;
        var wire = "📢 ANNOUNCEMENT (broadcast to every agent on this bridge):\n\n" + body +
                   "\n\n— Note this, then carry on with your work.";
        var reached = 0;
        foreach (var pane in BridgePanes.ToList())
        {
            if (pane.CanInterrupt) pane.Interrupt();   // stop the current turn so the notice lands now
            if (pane.Send(wire)) reached++;            // idle → sends now; busy → queues, auto-flushes post-interrupt
        }
        if (reached > 0) NoteBridgeActivity();
        return reached;
    }

    /// <summary>Close one pane via its X without turning the departed agent into a standalone chat. The other agents
    /// keep running; if the host is closed, the next pane is promoted. One survivor collapses to a normal chat.</summary>
    public void RemoveBridgePane(ChatViewModel pane)
    {
        if (!BridgePanes.Contains(pane)) return;
        var originalHost = BridgePanes[0];
        var wasHost = ReferenceEquals(pane, BridgePanes[0]);
        var wasActive = ReferenceEquals(pane, ActiveChat);
        var wasSecondaryActive = ReferenceEquals(pane, SecondaryActiveChat);
        var leftNum = BridgeNumberOf(pane);
        var wasManager = pane.IsBridgeManager;

        BridgePanes.Remove(pane);
        Chats.Remove(pane);                // the host is a sidebar chat; closing its pane must close that row too
        _bridgeErrored.Remove(pane);       // drop any error-announce bookkeeping for the departing pane
        pane.Close();                      // provider transcripts remain resumable through the existing history/recovery path
        RefreshBridgeWorkingCue();
        NoteBridgeActivity();

        if (BridgePanes.Count >= 2)
        {
            ResetBridgeExpand();   // if the removed (or any) pane was focused, restore the grid so none stays hidden
            // The bridge continues with the remaining agents. If we removed the host, promote the new first pane to
            // be the host anchor (a real chat you can click to return to the bridge) so it isn't left unreachable.
            if (wasHost)
            {
                RemoveSavedBridgeFor(originalHost, save: false);   // host key changed; do not strand a duplicate snapshot
                PromoteToHost(BridgePanes[0]);
            }
            // Compact every surviving identity (2,3,4 -> 1,2,3), rewrite the status-board ownership headers, and
            // queue an explicit self+peer roster correction into every already-running provider session.
            CompactBridgeRosterAfterDeparture(pane.Cwd, leftNum, pane.AgentDisplay, "left the bridge", "🔗");
            if (wasManager)
            {
                // The brain left. Tell the survivors plainly so nobody keeps waiting for dispatches that will never come.
                foreach (var peer in BridgePanes)
                {
                    var note = "[BRIDGE] The MANAGER left the bridge. No manager is assigned now — coordinate as equal " +
                               "peers via the status board until the user crowns a new one.";
                    peer.Prelude = string.IsNullOrEmpty(peer.Prelude) ? note : peer.Prelude + "\n" + note;
                    peer.Items.Add(new DividerItem { Label = "👑 the manager left — no manager assigned" });
                }
            }
            else if (ManagerOf(BridgePanes) is { } mgr)
            {
                // A worker left: the manager adapts in real time — its unfinished lane is reassignable immediately.
                SendManagerUpdate(mgr,
                    $"{pane.AgentDisplay} agent #{leftNum} LEFT the bridge and the roster was renumbered contiguously " +
                    $"(you are {mgr.AgentDisplay} agent #{BridgeNumberOf(mgr)}; {BridgePanes.Count} agents remain). " +
                    "Anything it was working on is now unowned — update the plan and re-dispatch its unfinished lane " +
                    "to a free worker (or take it yourself if none are free).");
            }
            if (wasActive) ActiveChat = BridgePanes[0];
            if (wasSecondaryActive) SecondaryActiveChat = BridgePanes[0];
            SaveBridge();
            SaveSession();   // Chats changed (host removed / promoted) - persist OpenChats now so a crash can't lose it
            RaiseBridgeUi();
            return;
        }

        // Only one Claude left → not a bridge anymore: keep the survivor as an ordinary chat and leave bridge mode.
        var survivor = BridgePanes.FirstOrDefault();
        BridgePanes.Clear();
        _bridgeErrored.Clear();
        RemoveSavedBridgeFor(originalHost, save: false);
        if (_parkedBridges.Count == 0) StopBridgeIdleTimer();
        if (survivor is not null)
        {
            survivor.BridgeHasWorkingPane = false;
            survivor.BridgeLabel = "";
            survivor.IsBridgeHost = false;
            survivor.IsBridgeManager = false;   // no bridge left to manage
            if (!Chats.Contains(survivor))
            {
                Chats.Insert(0, survivor);   // ensure it's a normal, reachable chat
                if (Chats.Any(c => c.Pinned)) ReorderPinned();
            }
            MarkOwned(survivor.SessionId);
        }
        if (wasActive || ShowBridge) ActiveChat = survivor ?? Chats.FirstOrDefault();
        if (wasSecondaryActive || SecondaryShowBridge)
            SecondaryActiveChat = survivor
                                  ?? Chats.FirstOrDefault(candidate => !ReferenceEquals(candidate, ActiveChat))
                                  ?? ActiveChat;
        ShowBridge = false;
        SecondaryShowBridge = false;
        RaiseBridgeUi();
        SaveSession();
    }

    /// <summary>Toggle "focus one Claude": expand the given pane to fill the whole bridge (collapsing its peers), or
    /// restore the grid if it was already expanded. Collapsed peers keep running in the background - only hidden.
    /// User-minimized panes stay minimized; expand only reflows agents that are still on the grid.</summary>
    public void ToggleBridgeExpand(ChatViewModel pane, Func<ChatViewModel, bool>? surfaceContains = null)
    {
        if (pane is null || !BridgePanes.Contains(pane) || pane.BridgeMinimized) return;
        SelectBridgePane(pane);
        surfaceContains ??= _ => true;
        var expand = !pane.BridgeExpanded;                       // clicking the expanded pane's button restores the grid
        foreach (var p in BridgePanes)
        {
            p.BridgeExpanded = expand && ReferenceEquals(p, pane);
            // In dual-monitor mode focus is local to the surface that owns the clicked pane. The other monitor stays
            // populated instead of going blank; single-monitor callers use the default predicate and retain the old behavior.
            // Minimized panes keep BridgeVisible true so unminimizing them later lands them back in the grid correctly.
            if (p.BridgeMinimized) { p.BridgeExpanded = false; continue; }
            p.BridgeVisible = !expand || !surfaceContains(p) || ReferenceEquals(p, pane);
        }
        Raise(nameof(BridgeGridRows));
    }

    /// <summary>Restore the full grid (no pane expanded, expand-focus collapse cleared). User-minimized panes stay
    /// minimized — this only undoes focus mode, not a deliberate hide.</summary>
    public void ResetBridgeExpand()
    {
        foreach (var p in BridgePanes) { p.BridgeExpanded = false; p.BridgeVisible = true; }
        Raise(nameof(BridgeGridRows));
    }

    /// <summary>Hide a bridge agent from the grid without removing it. The agent keeps running; a restore chip in the
    /// bridge header (next to usage) brings it back. If it was focused, the grid is restored for remaining panes.</summary>
    public void MinimizeBridgePane(ChatViewModel pane)
    {
        if (pane is null || !BridgePanes.Contains(pane) || pane.BridgeMinimized) return;
        var wasExpanded = pane.BridgeExpanded;
        pane.BridgeMinimized = true;
        pane.BridgeExpanded = false;
        if (wasExpanded)
        {
            // Peers that were expand-collapsed should reappear (unless they themselves are minimized).
            foreach (var p in BridgePanes)
            {
                if (!p.BridgeMinimized) p.BridgeVisible = true;
                p.BridgeExpanded = false;
            }
        }
        Raise(nameof(BridgeGridRows));
        Raise(nameof(HasMinimizedBridgePanes));
        NoteBridgeActivity();
    }

    /// <summary>Bring a user-minimized bridge agent back onto the grid. Always shows the pane (clears expand-focus
    /// collapse if needed) so a restore chip never "succeeds" while leaving the agent still hidden.</summary>
    public void RestoreBridgePane(ChatViewModel pane)
    {
        if (pane is null || !BridgePanes.Contains(pane) || !pane.BridgeMinimized) return;
        pane.BridgeMinimized = false;
        // Expand-focus had collapsed peers via BridgeVisible; clearing it guarantees the restored pane is actually on
        // screen, and any other still-minimized agents stay hidden via BridgeMinimized alone.
        foreach (var p in BridgePanes)
        {
            p.BridgeExpanded = false;
            p.BridgeVisible = true;
        }
        SelectBridgePane(pane);
        Raise(nameof(BridgeGridRows));
        Raise(nameof(HasMinimizedBridgePanes));
        NoteBridgeActivity();
    }

    /// <summary>Make a (formerly peer) pane the bridge's host anchor: a real chat in the sidebar carrying the
    /// "return to bridge" cue, and re-key the temp-save to it.</summary>
    private void PromoteToHost(ChatViewModel newHost)
    {
        if (!Chats.Contains(newHost))
        {
            Chats.Insert(0, newHost);
            if (Chats.Any(c => c.Pinned)) ReorderPinned();
        }
        newHost.IsBridgeHost = true;
        MarkOwned(newHost.SessionId);
        SaveBridge();   // SavedBridge is keyed on the host's SessionId - re-point it at the new host
    }

    /// <summary>End the LIVE bridge but save it durably: the peers' sessions are persisted before
    /// their processes are disposed, and the host keeps a "resume bridge" cue. Called by the host pane's X and the
    /// idle timeout. Reopening the host (<see cref="OpenChat"/>) resumes the provider-specific peer threads.</summary>
    public void CloseBridge()
    {
        var host = BridgePanes.FirstOrDefault();
        var wasShowing = ShowBridge;
        SaveBridge();   // persist peers FIRST so nothing is lost when their live processes are disposed
        foreach (var p in BridgePanes.Skip(1).ToList()) p.Close();
        BridgePanes.Clear();
        _bridgeErrored.Clear();
        if (host is not null)
        {
            host.BridgeHasWorkingPane = false;
            host.BridgeLabel = "";
            host.IsBridgeHost = HasSavedBridgeFor(host);   // keep the cue iff there's a resumable saved bridge
        }
        ShowBridge = false;
        SecondaryShowBridge = false;
        if (_parkedBridges.Count == 0) StopBridgeIdleTimer();
        // Only pull the user to the host if they were actually looking at the bridge; a background timeout
        // shouldn't yank them out of whatever chat they're in.
        if (wasShowing && host is not null) ActiveChat = host;
        RaiseBridgeUi();
        SaveSession();
    }

    /// <summary>Dispose one parked roster while leaving every other live bridge untouched.</summary>
    private bool CloseParkedBridge(ChatViewModel host)
    {
        if (!_parkedBridges.Remove(host, out var bridge)) return false;
        SaveBridge(bridge.Panes);
        foreach (var pane in bridge.Panes.Skip(1)) pane.Close();
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
        SaveBridge(BridgePanes);
    }

    private static void SaveBridge(IReadOnlyList<ChatViewModel> panes)
    {
        if (!SnapshotBridge(panes)) return;
        AppSettings.Current.Save();
    }

    private static bool SnapshotBridge(IReadOnlyList<ChatViewModel> panes)
    {
        var host = panes.FirstOrDefault();
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
                Draft = NullIfEmpty(p.Draft),
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
        if (IsBridge) return false;
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
        WriteBridgeFile(host.Cwd, preserveManagerPlan: managerNumber > 0);
        AppendPeerToBridgeFile(host.Cwd, 1);
        host.BridgeLabel = AgentLabel(host, 1);
        host.IsBridgeHost = true;
        host.IsBridgeManager = s.HostIsManager;
        host.Prelude = BridgeJoinPrelude(host, 1, roster, managerNumber);
        host.Items.Add(new DividerItem { Label = $"🔗 Bridge resumed — you are {host.BridgeLabel}" });
        var restoredPanes = new List<ChatViewModel> { host };
        var panesToStart = new List<ChatViewModel>();
        for (int i = 0; i < savedPeers.Count; i++)
        {
            var pane = savedPeers[i];
            var num = peerNums[i];
            var cwd = Directory.Exists(pane.Cwd) ? pane.Cwd : host.Cwd;
            var provider = string.IsNullOrWhiteSpace(pane.Provider) ? host.Provider : pane.Provider!;
            AppendPeerToBridgeFile(cwd, num);   // seed the file with the SAME number as the label (no mismatch)

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
            chat.IsBridgeManager = pane.IsManager;
            chat.AppendSystemPrompt = BridgePrompt(chat, num, roster, managerNumber);
            chat.Prelude = BridgeJoinPrelude(chat, num, roster, managerNumber);
            UpdateGeneratedBridgeTitle(chat, num); // normalize an older saved title such as "Bridge · Codex 4" to its new compact number
            if ((pane.Mode ?? s.Mode) is { } m) chat.SetMode(m);
            var sameProvider = string.Equals(chat.Provider, host.Provider, StringComparison.OrdinalIgnoreCase);
            if (pane.Model is not null) chat.Model = pane.Model;
            else if (sameProvider) chat.Model = host.Model; // legacy snapshots inherited the host's provider settings
            if (pane.Effort is not null) chat.Effort = pane.Effort;
            else if (sameProvider) chat.Effort = host.Effort;
            if (!string.IsNullOrEmpty(pane.Draft)) chat.Draft = pane.Draft;   // an unsent prompt survives the restart
            MarkOwned(chat.SessionId);
            restoredPanes.Add(chat);
            if (start) panesToStart.Add(chat);
        }
        BridgePanes.ReplaceAll(restoredPanes);
        foreach (var pane in panesToStart) pane.Start();
        // After ALL panes exist (it iterates BridgePanes, so it's a no-op before they're added): the host can carry a
        // stale BridgeVisible == false from a peer that was expanded when the bridge closed, which would render the
        // host invisible on resume. A resumed bridge always comes back as the full grid.
        ResetBridgeExpand();
        RefreshBridgeWorkingCue();
        if (showPrimary) ShowBridge = true;
        else
        {
            SecondaryActiveChat = host;
            SecondaryShowBridge = true;
        }
        NoteBridgeActivity();
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
    private void CompactBridgeRosterAfterDeparture(string cwd, int departedNumber, string departedAgent, string what, string glyph)
    {
        if (BridgePanes.Count == 0) return;
        var renumbered = BridgePanes
            .Select((pane, i) => (Pane: pane, OldNumber: BridgeNumberOf(pane), NewNumber: i + 1))
            .ToList();
        var roster = Enumerable.Range(1, renumbered.Count).ToArray();
        // The manager's number can shift with everyone else's; every rebuilt brief must point at its NEW number.
        var managerNumber = renumbered.FirstOrDefault(x => x.Pane.IsBridgeManager).NewNumber;   // 0 when no manager

        RewriteBridgeFileRoster(cwd, renumbered.Select(x => (x.OldNumber, x.NewNumber)).ToList());

        foreach (var entry in renumbered)
        {
            entry.Pane.BridgeLabel = AgentLabel(entry.Pane, entry.NewNumber);
            entry.Pane.IsBridgeHost = entry.NewNumber == 1;
            UpdateGeneratedBridgeTitle(entry.Pane, entry.NewNumber);
            entry.Pane.AppendSystemPrompt = BridgePrompt(entry.Pane, entry.NewNumber, roster, managerNumber);

            var peers = string.Join(", ", roster.Where(n => n != entry.NewNumber).Select(n => $"agent #{n}"));
            var oldIdentity = entry.OldNumber > 0 ? entry.OldNumber : entry.NewNumber;
            var note = $"[BRIDGE] {departedAgent} agent #{departedNumber} {what}. The live bridge was renumbered " +
                       $"contiguously. You were agent #{oldIdentity}; you are now {entry.Pane.AgentDisplay} agent #{entry.NewNumber} " +
                       $"of {roster.Length}. Your active peers are {(peers.Length > 0 ? peers : "none")}. Use Agent " +
                       $"#{entry.NewNumber} for your block in .vibecode-bridge.md from now on. The departed agent's claimed area is free.";
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
    private static void RewriteBridgeFileRoster(string cwd, IReadOnlyList<(int OldNumber, int NewNumber)> renumbered)
    {
        try
        {
            var path = Path.Combine(cwd, ".vibecode-bridge.md");
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
    private static void MarkBridgeFilePeerGone(string cwd, int n, string reason)
    {
        if (n <= 0) return;
        try
        {
            var path = Path.Combine(cwd, ".vibecode-bridge.md");
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
            MarkBridgeFilePeerGone(c.Cwd, n, "hit an error and stopped");
            var active = bridge.Panes.Count(p => !bridge.Errored.Contains(p));
            var agent = c.AgentDisplay;
            foreach (var peer in bridge.Panes.Where(p => !ReferenceEquals(p, c)))
            {
                var note = $"[BRIDGE] {agent} agent #{n} hit an error and stopped — {active} agent(s) still active. Anything it " +
                           "had claimed on the status board (.vibecode-bridge.md) is now unowned.";
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
    private static string BridgePrompt(ChatViewModel pane, int index, IReadOnlyCollection<int> roster, int managerNumber)
    {
        var peers = string.Join(", ", roster.Where(i => i != index).OrderBy(i => i).Select(i => "agent #" + i));
        var swarmRule = SwarmPolicy.BridgeRuntimeRule(
            AppSettings.Current.AgentSwarmsEnabled && AppSettings.Current.AgentSwarmsInBridge,
            AppSettings.Current.SwarmMaxWorkers)
            + ManagerClause(index, managerNumber);   // the manager brief always closes the appendix, both sharing modes
        if (AppSettings.Current.BridgeRealtimeSharing)
            return "[BRIDGE MODE] You are " + pane.ProviderDisplay + " agent #" + index + " of " + roster.Count + " working in this same project " +
                "alongside " + (peers.Length > 0 ? peers : "(peers joining)") + " (more may join). Real-time sharing is ON: peers stay aware of " +
                "each other through `.vibecode-bridge.md` (project root) — an area-claims board plus a live-activity board. Keep both current; " +
                "the file is the only channel, don't message peers directly:\n" +
                "- Under \"## Active\" keep your two-line block: an `Agent #" + index + ": Working on <the thing>` line, then a plain-English " +
                "note (≤100 words) on what you're doing and how. Refresh it if your task changes.\n" +
                "- Under \"## Live activity\" keep exactly ONE compact block for yourself: an `Agent #" + index + " » <file path(s)>` line plus " +
                "1-2 short lines naming what you're adding or changing there right now (function/class names and a few words — never diffs or " +
                "code). REWRITE that block in place whenever you start, switch to, or finish a file, or after several consecutive edits to the " +
                "same file. Never append history — the board is a snapshot, and per-edit logging is a bug (it burns everyone's tokens).\n" +
                "- Before editing a file, glance at peers' \"## Live activity\" lines. If a peer lists the file you're about to touch, hold off " +
                "or pick different work, and say so in your block.\n" +
                "- FIRST ACTION, before any other work: READ `.vibecode-bridge.md`, pick an area no peer claimed, then write BOTH your " +
                "\"## Active\" block AND your first \"## Live activity\" block. Yours is seeded as \"figuring out what to do\", and leaving it " +
                "that way is a bug. Do the read and the writes before you report back, then continue with the request.\n" +
                "- Only touch a peer's files after glancing at their blocks first. Otherwise just work; assume peers own their areas.\n" +
                swarmRule;
        return "[BRIDGE MODE] You are " + pane.ProviderDisplay + " agent #" + index + " of " + roster.Count + " working in this same project " +
            "alongside " + (peers.Length > 0 ? peers : "(peers joining)") + " (more may join). Coordinate at a HIGH LEVEL only — " +
            "you do NOT need to know or track what the others are editing line-by-line, and you should NOT narrate your own edits to them. " +
            "Just avoid working on the same thing:\n" +
            "- `.vibecode-bridge.md` (project root) is a STATUS BOARD. Under \"## Active\" each agent has a two-line block:\n" +
            "  an `Agent #" + index + ": Working on <the thing>` line, then a plain-English note (≤100 words) on what you're doing and how.\n" +
            "- FIRST ACTION, before any other work: READ `.vibecode-bridge.md` to see what your peers claimed, and pick a " +
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
    private static string BridgeJoinPrelude(ChatViewModel pane, int index, IReadOnlyCollection<int> roster, int managerNumber) =>
        "[BRIDGE] You have just been put into a VibeCode Bridge. This is NEW state — it was not true earlier in this " +
        "conversation, so don't assume anything below was already handled. Before you continue with anything else: read " +
        "`.vibecode-bridge.md` in the project root, then rewrite your own `Agent #" + index + "` block there to say what " +
        "you are working on. Then carry on with the request.\n\n" + BridgePrompt(pane, index, roster, managerNumber);

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
    private static void WriteBridgeFile(string cwd, bool preserveManagerPlan = false)
    {
        try
        {
            var path = Path.Combine(cwd, ".vibecode-bridge.md");
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
    private static void EnsureLiveActivitySection(string cwd)
    {
        try
        {
            var path = Path.Combine(cwd, ".vibecode-bridge.md");
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
    private static void AppendPeerToBridgeFile(string cwd, int index)
    {
        try { File.AppendAllText(Path.Combine(cwd, ".vibecode-bridge.md"), $"Agent #{index}: Working on — (figuring out what to do…)\n_(Agent #{index} just joined; this updates once it picks up a task.)_\n\n"); }
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
    private static string ManagerClause(int index, int managerNumber)
    {
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
               "when merely explaining the syntax, describe it in prose.\n" +
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
        if (now == "closed" || !TryGetLiveBridge(c, out var bridge)) { _bridgeSeenStatus.Remove(c); return; }
        _bridgeSeenStatus[c] = now;

        // No crown ⇒ no dispatch loop, no worker→manager relays. Flat peers only.
        var manager = ManagerOf(bridge.Panes);
        if (manager is null || !manager.IsBridgeManager) return;

        if (ReferenceEquals(c, manager))
        {
            if (prev == "running" && now == "idle") RouteManagerDispatches(manager, bridge.Panes);
            return;
        }

        if (prev == "running" && now == "idle")
        {
            var report = Tail(c.LastTurnReplyText(), 1800);
            if (report.Length == 0) return;   // an interrupted/empty turn carries nothing worth relaying
            SendManagerUpdate(manager,
                $"{c.AgentDisplay} agent #{BridgeNumberOf(c)} finished its turn and is now idle. The end of its report:\n" +
                "───\n" + report + "\n───\n" +
                "React per your manager brief: check/accept the work, update \"## Manager plan\", then either " +
                $"dispatch this worker its next unclaimed lane (@@DISPATCH agent={BridgeNumberOf(c)}) or, if every " +
                "lane is done, run final verification and tell the user the project is complete.");
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

    /// <summary>One dispatch block in the manager's reply. Multiline body, tolerant of surrounding markdown/fences.</summary>
    private static readonly System.Text.RegularExpressions.Regex ManagerDispatchBlock = new(
        @"^[ \t]*@@DISPATCH[ \t]+agent[ \t]*=[ \t]*(?<target>\d+|all)[ \t]*\r?$(?<body>[\s\S]*?)^[ \t]*@@END[ \t]*\r?$",
        System.Text.RegularExpressions.RegexOptions.Multiline
        | System.Text.RegularExpressions.RegexOptions.IgnoreCase
        | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Extract every @@DISPATCH block from the manager's finished reply and deliver each into its target
    /// pane(s). A busy target simply queues the order; an unknown number is skipped (the roster may have changed
    /// while the manager was thinking — the departure update it also received sorts that out).</summary>
    private void RouteManagerDispatches(ChatViewModel manager, IReadOnlyList<ChatViewModel> panes)
    {
        // Re-check the crown: the manager can step down while its last turn is finishing, and a queued
        // worker.Send would otherwise still land as "FROM MANAGER" after the user turned management off.
        if (!manager.IsBridgeManager) return;
        var reply = manager.LastTurnReplyText();
        if (reply.Length == 0 || !reply.Contains("@@DISPATCH", StringComparison.OrdinalIgnoreCase)) return;
        var m = BridgeNumberOf(manager);
        var delivered = new List<string>();
        foreach (System.Text.RegularExpressions.Match match in ManagerDispatchBlock.Matches(reply))
        {
            if (!manager.IsBridgeManager) break;   // stepped down mid-route
            var body = match.Groups["body"].Value.Trim();
            if (body.Length == 0) continue;
            var target = match.Groups["target"].Value;
            var targets = target.Equals("all", StringComparison.OrdinalIgnoreCase)
                ? panes.Where(p => !ReferenceEquals(p, manager)).ToList()
                : int.TryParse(target, out var num)
                    ? panes.Where(p => !ReferenceEquals(p, manager) && BridgeNumberOf(p) == num).ToList()
                    : new List<ChatViewModel>();
            foreach (var worker in targets)
            {
                if (!manager.IsBridgeManager) break;
                var wire = $"👑 [FROM MANAGER — {manager.AgentDisplay} agent #{m}] Work order:\n\n{body}\n\n" +
                           "— Work within this order's scope and constraints. When done, end your reply with a short " +
                           "factual report (what you did / verified / anything blocking); it is relayed to the " +
                           "manager automatically.";
                if (worker.Send(wire)) delivered.Add("#" + BridgeNumberOf(worker));
            }
        }
        if (delivered.Count == 0) return;
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
