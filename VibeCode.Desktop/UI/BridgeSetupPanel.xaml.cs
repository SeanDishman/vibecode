using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using VibeCode.Services;

namespace VibeCode.UI;

public partial class BridgeSetupPanel : UserControl
{
    public sealed record ProviderChoice(string Id, string Label);
    public static IReadOnlyList<ProviderChoice> Providers { get; } =
    [new("claude", "Claude Code"), new("codex", "OpenAI Codex"), new("glm", "GLM")];
    private const string AutomaticCount = "Let AI choose";
    private ChatViewModel? _host;
    private int _maximum = 8;
    private int _singleGroupMaximum = 8;
    private Func<string, int>? _workerCapacity;
    private Func<int, int, int>? _projectedAgentCount;
    private CancellationTokenSource? _suggestion;
    private bool _configuring;
    private bool _editingCount;
    private bool _closed;
    private bool _starting;
    private bool _advancedOnly;
    private bool _hasOtherOrchestrators;
    private IReadOnlyList<BridgeAccountChoice> _accounts = [];
    private int _micToken;
    private DateTime _transcribingSince;
    private readonly DispatcherTimer _speechTimer;

    public event EventHandler? StartRequested;
    public event EventHandler? CancelRequested;
    public ChatViewModel? Host => _host;
    public BridgeAgentConfiguration OrchestratorConfiguration => AgentOptions.OrchestratorConfiguration;
    public BridgeAgentConfiguration WorkerConfiguration => AgentOptions.WorkerConfiguration;
    public BridgeAgentConfiguration CentralConfiguration => AgentOptions.CentralConfiguration;
    public bool IsAdvanced => AdvancedChoice.IsChecked == true;
    public bool SingleTerminal => IsAdvanced && (RequiresSingleTerminal || SingleTerminalChoice.IsChecked == true);
    /// <summary>Fact check orchestrator: once every agent stops, the orchestrator must confirm the task is really done.</summary>
    public bool FactCheck => IsAdvanced && FactCheckChoice.IsChecked == true;
    /// <summary>Plan steps: orchestrators publish a step plan before dispatch and its progress shows above the chat.
    /// Off, they assign work directly. Only an Advanced team has orchestrators to plan.</summary>
    public bool PlanSteps => IsAdvanced && PlanStepsChoice.IsChecked == true;
    private bool RequiresSingleTerminal => IsAdvanced && BridgeAgentPolicy.RequiresSingleTerminal(
        _projectedAgentCount?.Invoke(OrchestratorCount, AutomaticWorkers ? OrchestratorCount : WorkerCount)
            ?? OrchestratorCount + (AutomaticWorkers ? OrchestratorCount : WorkerCount));
    public bool AutomaticWorkers => WorkerCountBox.SelectedItem is string;
    public int WorkerCount => WorkerCountBox.SelectedItem is int count ? count : 1;
    public int OrchestratorCount => OrchestratorCountBox.SelectedItem is int count ? count : 1;
    public IReadOnlyList<int> WorkerAllocation => Groups.Select(group => group.WorkerCount).ToArray();
    public ObservableCollection<BridgeSetupGroup> Groups { get; } = new();
    public string Objective => ObjectiveBox.Text.Trim();
    public BridgeAccountChoice? PeerAccount => PeerAccountBox.SelectedItem as BridgeAccountChoice;
    public BridgeProviderChoice? PeerProvider => PeerProviderBox.SelectedItem as BridgeProviderChoice;
    public string Provider => (IsAdvanced ? OrchestratorProviderBox.SelectedValue as string : PeerAccount?.Provider ?? PeerProvider?.Id)
        ?? _host?.Provider ?? "claude";

    public BridgeSetupPanel()
    {
        InitializeComponent();
        OrchestratorCountBox.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler(OnCountTextChanged));
        WorkerCountBox.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler(OnCountTextChanged));
        GroupAllocation.ItemsSource = Groups;
        _speechTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _speechTimer.Tick += (_, _) => RefreshSpeech();
        Unloaded += (_, _) => Suspend();
    }

    public void Configure(ChatViewModel host, int maximumWorkers, bool advancedOnly = false,
        bool singleTerminal = true, Func<string, int>? workerCapacity = null, bool hasOtherOrchestrators = false,
        Func<int, int, int>? projectedAgentCount = null)
    {
        var retainDraft = ReferenceEquals(_host, host) && _advancedOnly == advancedOnly;
        Suspend();
        _closed = false;
        _configuring = true;
        _host = host;
        _singleGroupMaximum = Math.Max(0, maximumWorkers);
        _workerCapacity = workerCapacity;
        _projectedAgentCount = projectedAgentCount;
        _advancedOnly = advancedOnly;
        _hasOtherOrchestrators = hasOtherOrchestrators;
        ModeChoices.Visibility = advancedOnly ? Visibility.Collapsed : Visibility.Visible;
        Heading.Text = advancedOnly ? "Add an orchestrator" : "Start a bridge";
        if (!retainDraft)
        {
            OrchestratorProviderBox.ItemsSource = Providers;
            OrchestratorProviderBox.SelectedValue = host.Provider;
            ObjectiveBox.Text = host.Draft;
            SingleTerminalChoice.IsChecked = singleTerminal;
            SeparateTerminalsChoice.IsChecked = !singleTerminal;
            // Another orchestrator joins this Bridge's own setting; a new team starts from the last one chosen.
            FactCheckChoice.IsChecked = advancedOnly && host.BridgeWork is { } current
                ? current.FactCheckOrchestrators : AppSettings.Current.BridgeFactCheckOrchestrators;
            PlanStepsChoice.IsChecked = advancedOnly && host.BridgeWork is { } bridgeWork
                ? bridgeWork.PlanSteps : AppSettings.Current.BridgePlanSteps;
            AdvancedChoice.IsChecked = advancedOnly;
            RegularChoice.IsChecked = !advancedOnly;
            AgentOptions.Configure(host, host.Provider);
        }
        var previousAccount = retainDraft ? PeerAccount : null;
        var previousProvider = retainDraft ? PeerProvider?.Id : null;
        _accounts = BridgeAccountChoice.Load();
        var providers = BridgeProviderChoice.From(_accounts);
        PeerProviderBox.ItemsSource = providers;
        // Provider first, then only its accounts. A reopened draft keeps its provider; a fresh one starts on the host's.
        PeerProviderBox.SelectedItem = providers.FirstOrDefault(p => p.IsUsable && p.Id == previousProvider)
            ?? providers.FirstOrDefault(p => p.IsUsable && p.Id == host.Provider)
            ?? providers.FirstOrDefault(p => p.IsUsable);
        ShowPeerAccounts(previousAccount);
        if (_workerCapacity is not null) _singleGroupMaximum = Math.Max(0, _workerCapacity(OrchestratorProviderBox.SelectedValue as string ?? host.Provider));
        if (!retainDraft) OrchestratorCountBox.SelectedItem = null;
        SetWorkerChoices();
        if (!retainDraft)
        {
            var saved = AppSettings.Current.BridgeOrchestratorWorkerCounts;
            WorkerCountBox.SelectedItem = saved is { Length: > 0 } && saved.Length == OrchestratorCount && saved.All(n => n > 0)
                ? (int)Math.Clamp(saved.Sum(n => (long)n), OrchestratorCount, Math.Max(OrchestratorCount, _maximum)) : AutomaticCount;
            RebuildAllocation(saved);
        }
        SetSuggestion("");
        ShowError("");
        _configuring = false;
        UpdateControls();
    }

    public void FocusFirstControl()
    {
        if (IsAdvanced) ObjectiveBox.Focus();
        else RegularChoice.Focus();
    }

    public void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    public BridgeAccountChoice RequirePeerAccount()
    {
        var selected = PeerAccount;
        if (selected is null || !BridgeAccountChoice.Load().Any(a => a.IsUsable &&
                a.Provider == selected.Provider && a.AccountId == selected.AccountId))
            throw new InvalidOperationException("This account is unavailable. Reopen bridge setup to choose an account, or sign in again in AI accounts.");
        return selected;
    }

    /// <summary>List the chosen provider's accounts only, keeping <paramref name="preferred"/> while it is still usable.</summary>
    private void ShowPeerAccounts(BridgeAccountChoice? preferred = null)
    {
        var host = _host;
        var accounts = _accounts.Where(a => a.Provider == PeerProvider?.Id).ToArray();
        PeerAccountBox.ItemsSource = accounts;
        PeerAccountBox.SelectedItem = accounts.FirstOrDefault(a => a.IsUsable && preferred is not null &&
                a.Provider == preferred.Provider && a.AccountId == preferred.AccountId)
            ?? accounts.FirstOrDefault(a => a.IsUsable && host is not null && a.Provider == host.Provider && a.AccountId == (host.AccountId ?? ""))
            ?? accounts.FirstOrDefault(a => a.IsUsable && a.IsDefault)
            ?? accounts.FirstOrDefault(a => a.IsUsable);
    }

    private void OnPeerProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_configuring || StartButton is null) return;
        _configuring = true;
        ShowPeerAccounts();
        _configuring = false;
        ShowError("");
        UpdateControls();
    }

    public void Suspend()
    {
        _closed = true;
        _suggestion?.Cancel();
        _suggestion = null;
        _speechTimer?.Stop();
        if (_micToken != 0 && SpeechService.Instance.Owns(_micToken)) SpeechService.Instance.ForceReset();
        _micToken = 0;
        _starting = false;
        if (SpeechText is not null) ResetSpeechVisual();
    }

    private void SetWorkerChoices()
    {
        var count = OrchestratorCountBox.SelectedItem is int existing ? existing : AppSettings.Current.BridgeOrchestratorCount;
        var maxGroups = BridgeTeamAllocationPolicy.MaximumOrchestrators(_singleGroupMaximum);
        if (OrchestratorCountBox.Items.Count != maxGroups)
            OrchestratorCountBox.ItemsSource = Enumerable.Range(1, maxGroups);
        OrchestratorCountBox.SelectedItem = maxGroups > 0 ? Math.Clamp(count, 1, maxGroups) : null;
        _maximum = BridgeTeamAllocationPolicy.MaximumWorkers(_singleGroupMaximum, OrchestratorCount);
        var selected = WorkerCountBox.SelectedItem;
        WorkerCountBox.ItemsSource = new object[] { AutomaticCount }.Concat(Enumerable.Range(OrchestratorCount,
            Math.Max(0, _maximum - OrchestratorCount + 1)).Cast<object>());
        WorkerCountBox.SelectedItem = selected is int workers && _maximum >= OrchestratorCount
            ? Math.Clamp(workers, OrchestratorCount, _maximum) : AutomaticCount;
        RebuildAllocation(WorkerAllocation);
    }

    private void OnCountTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_configuring || _editingCount || StartButton is null || sender is not ComboBox box
            || e.OriginalSource is not TextBox editor) return;
        var caret = editor.SelectionStart;
        var selectionLength = editor.SelectionLength;
        _editingCount = true;
        try
        {
            // Keep invalid/unfinished text visible. Only a valid choice changes the allocation;
            // validation below disables Start rather than silently launching the previous count.
            var text = editor.Text.Trim();
            var isNumber = int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var count);
            var choice = box.Items.Cast<object>().FirstOrDefault(item => isNumber ? item is int number && number == count : item.ToString() == text);
            if (choice is not null && !Equals(box.SelectedItem, choice)) box.SelectedItem = choice;
            UpdateControls();
            editor.Select(Math.Min(caret, editor.Text.Length), Math.Min(selectionLength, editor.Text.Length - Math.Min(caret, editor.Text.Length)));
        }
        finally { _editingCount = false; }
    }

    private string CountInputError()
    {
        if (_singleGroupMaximum < 1) return "No room for workers. Raise the bridge agent limit in Settings.";
        var maxGroups = BridgeTeamAllocationPolicy.MaximumOrchestrators(_singleGroupMaximum);
        if (!int.TryParse(OrchestratorCountBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var groups)
            || groups < 1 || groups > maxGroups)
            return $"Enter 1–{maxGroups} orchestrators. Each needs at least one worker.";
        if (WorkerCountBox.Text.Trim() == AutomaticCount) return "";
        if (!int.TryParse(WorkerCountBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var workers)
            || workers < groups || workers > _maximum)
            return $"Enter {groups}–{_maximum} total workers, or select ‘{AutomaticCount}’. Orchestrators count toward the agent limit.";
        return "";
    }

    private void RebuildAllocation(IReadOnlyList<int>? preferred = null)
    {
        var total = AutomaticWorkers ? OrchestratorCount : WorkerCount;
        var values = preferred is not null && preferred.Count == OrchestratorCount && preferred.All(n => n > 0)
            && preferred.Sum(n => (long)n) == total ? preferred.ToArray() : BridgeTeamAllocationPolicy.Distribute(total, OrchestratorCount);
        foreach (var group in Groups) group.PropertyChanged -= OnAllocationChanged;
        Groups.Clear();
        for (var index = 0; index < values.Length; index++)
        {
            var group = new BridgeSetupGroup(index + 1, values[index]);
            group.PropertyChanged += OnAllocationChanged;
            Groups.Add(group);
        }
        RefreshAllocationChoices();
    }

    private void RefreshAllocationChoices()
    {
        var total = Groups.Sum(group => group.WorkerCount);
        foreach (var group in Groups)
            group.SetMaximum(Math.Max(group.WorkerCount, _maximum - total + group.WorkerCount));
    }

    private void OnAllocationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_configuring || e.PropertyName != nameof(BridgeSetupGroup.WorkerCount)) return;
        _configuring = true;
        WorkerCountBox.SelectedItem = Groups.Sum(group => group.WorkerCount);
        RefreshAllocationChoices();
        _configuring = false;
        ShowError("");
        UpdateControls();
    }

    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (_configuring || StartButton is null) return;
        if (IsAdvanced && _workerCapacity is not null)
        {
            _singleGroupMaximum = Math.Max(0, _workerCapacity(Provider));
            _configuring = true;
            SetWorkerChoices();
            _configuring = false;
        }
        if (!IsAdvanced)
        {
            _suggestion?.Cancel();
            if (_micToken != 0 && SpeechService.Instance.Owns(_micToken)) SpeechService.Instance.ForceReset();
            _micToken = 0;
            _speechTimer?.Stop();
            ResetSpeechVisual();
        }
        ShowError("");
        UpdateControls();
    }

    private void OnSettingsChanged(object sender, RoutedEventArgs e)
    {
        if (_configuring || StartButton is null) return;
        if (!_editingCount && (ReferenceEquals(sender, OrchestratorCountBox) || ReferenceEquals(sender, WorkerCountBox)))
        {
            // SelectionChanged can precede ComboBox's internal text update, including in an unrendered draft.
            var box = (ComboBox)sender;
            _configuring = true;
            box.Text = box.SelectedItem?.ToString() ?? "";
            _configuring = false;
        }
        if (ReferenceEquals(sender, OrchestratorProviderBox))
        {
            AgentOptions.SetOrchestratorProvider(Provider);
            if (_workerCapacity is not null) _singleGroupMaximum = Math.Max(0, _workerCapacity(Provider));
            _configuring = true;
            SetWorkerChoices();
            _configuring = false;
            SetSuggestion("");
        }
        else if (ReferenceEquals(sender, OrchestratorCountBox))
        {
            _configuring = true;
            SetWorkerChoices();
            _configuring = false;
            SetSuggestion("");
        }
        else if (ReferenceEquals(sender, WorkerCountBox)) RebuildAllocation();
        ShowError("");
        UpdateControls();
    }

    private void OnAgentConfigurationChanged(object? sender, EventArgs e)
    {
        if (_configuring || StartButton is null) return;
        if (_workerCapacity is not null)
        {
            _singleGroupMaximum = Math.Max(0, _workerCapacity(Provider));
            _configuring = true;
            SetWorkerChoices();
            _configuring = false;
        }
        ShowError("");
        UpdateControls();
    }

    private void OnObjectiveChanged(object sender, TextChangedEventArgs e)
    {
        if (_configuring || StartButton is null) return;
        SetSuggestion("");
        ShowError("");
        UpdateControls();
    }

    private void UpdateControls()
    {
        if (StartButton is null || _configuring) return;
        var busy = _suggestion is not null || _starting;
        var countError = CountInputError();
        CountError.Text = countError;
        CountError.Visibility = IsAdvanced && countError.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!_advancedOnly) Width = IsAdvanced ? 960 : 480;
        RegularFields.Visibility = IsAdvanced ? Visibility.Collapsed : Visibility.Visible;
        AdvancedFields.Visibility = LayoutFields.Visibility = IsAdvanced ? Visibility.Visible : Visibility.Collapsed;
        var requiresSingle = RequiresSingleTerminal;
        SeparateTerminalsChoice.Visibility = requiresSingle ? Visibility.Collapsed : Visibility.Visible;
        TerminalLayoutChoices.Columns = requiresSingle ? 1 : 2;
        if (requiresSingle && SingleTerminalChoice.IsChecked != true)
        {
            _configuring = true;
            try { SingleTerminalChoice.IsChecked = true; }
            finally { _configuring = false; }
        }
        AgentOptions.ShowCentralPlanner(OrchestratorCount > 1 || _hasOtherOrchestrators);
        Heading.Text = _advancedOnly ? OrchestratorCount > 1 ? "Add orchestrators" : "Add an orchestrator" : "Start a bridge";
        ModeDescription.Text = IsAdvanced
            ? OrchestratorCount > 1 || _hasOtherOrchestrators ? "A central planner divides the task. Each orchestrator leads its own workers."
                : "An orchestrator leads your workers. Mix providers and choose a model for each role."
            : "Connect another AI to this chat. Both agents can message each other and work together.";
        var hostName = _host?.AgentDisplay ?? "This chat";
        var peerName = Providers.FirstOrDefault(p => p.Id == Provider)?.Label ?? Provider;
        RegularSummary.Text = $"{hostName} + {peerName}. Chats open side by side.";
        RegularSummary.Visibility = PeerAccount is null ? Visibility.Collapsed : Visibility.Visible;
        AccountNote.Text = "Add or reconnect an account in AI accounts to start a bridge.";
        AccountNote.Visibility = PeerAccountBox.Items.OfType<BridgeAccountChoice>().Any(a => a.IsUsable)
            ? Visibility.Collapsed : Visibility.Visible;
        if (!AppSettings.Current.BridgePeerMessaging)
            RegularSummary.Text += " Messaging is paused in Bridge settings.";
        ObjectiveBox.IsReadOnly = busy;
        ModeChoices.IsEnabled = !busy;
        OrchestratorProviderBox.IsEnabled = WorkerCountBox.IsEnabled = !busy;
        PeerProviderBox.IsEnabled = !busy && PeerProviderBox.Items.OfType<BridgeProviderChoice>().Any(p => p.IsUsable);
        PeerAccountBox.IsEnabled = !busy && PeerAccountBox.HasItems;
        LayoutFields.IsEnabled = PlanStepsFields.IsEnabled = FactCheckFields.IsEnabled = !busy;
        AgentOptions.IsEnabled = !busy;
        OrchestratorCountBox.IsEnabled = GroupAllocation.IsEnabled = !busy;
        MicButton.IsEnabled = !busy;
        StartButton.Content = _starting ? "Starting…" : busy ? "Choosing team…" : IsAdvanced ? "Start team" : "Start bridge";
        StartButton.IsEnabled = _host is not null && !busy && _micToken == 0 &&
            (IsAdvanced ? Objective.Length > 0 && countError.Length == 0 && _maximum >= OrchestratorCount : PeerAccount?.IsUsable == true);
        TeamSizeText.Text = !IsAdvanced ? "2 connected agents" : countError.Length > 0 ? "Enter a valid team size" : AutomaticWorkers
            ? $"{OrchestratorCount} orchestrator{(OrchestratorCount == 1 ? "" : "s")} · AI chooses workers"
            : OrchestratorCount == 1 ? $"1 orchestrator + {WorkerCount} worker{(WorkerCount == 1 ? "" : "s")} · {1 + WorkerCount} agents"
            : $"{OrchestratorCount} orchestrators + {WorkerCount} workers · {OrchestratorCount + WorkerCount} agents";
        AllocationFields.Visibility = OrchestratorCount > 1 && !AutomaticWorkers ? Visibility.Visible : Visibility.Collapsed;
        AllocationNote.Text = AutomaticWorkers
            ? "Workers are shared evenly to start. Each worker belongs to one orchestrator."
            : $"{OrchestratorCount + WorkerCount} agents in {OrchestratorCount} group{(OrchestratorCount == 1 ? "" : "s")}. Each worker belongs to one orchestrator.";
        CountNote.Text = _maximum < 1 ? "No room for workers. Raise the bridge agent limit in Settings."
            : AutomaticWorkers ? $"AI chooses {OrchestratorCount}–{_maximum} total workers when you start. Orchestrators are extra."
            : $"Type a count or choose from the list. Up to {_maximum} workers with {OrchestratorCount} orchestrator{(OrchestratorCount == 1 ? "" : "s")}.";
        LargeTeamNote.Text = $"Teams of {BridgeAgentPolicy.DefaultAgentLimit} or fewer agents are recommended. Larger teams use more memory and provider capacity.";
        LargeTeamNote.Visibility = !AutomaticWorkers && countError.Length == 0 && OrchestratorCount + WorkerCount > BridgeAgentPolicy.DefaultAgentLimit
            ? Visibility.Visible : Visibility.Collapsed;
        OrchestratorProviderBox.ToolTip = Provider == _host?.Provider
            ? "Uses this chat’s account and permissions with the model selected below."
            : "Uses the selected provider’s default account, the model selected below, and this chat’s permissions.";
        LayoutNote.Text = requiresSingle
            ? $"Teams above {BridgeAgentPolicy.MaximumSeparateTerminalAgents} agents use a single terminal. Switch agents while everyone keeps working."
            : AutomaticWorkers
            ? $"Teams above {BridgeAgentPolicy.MaximumSeparateTerminalAgents} agents automatically use a single terminal."
            : SingleTerminalChoice.IsChecked == true
            ? "One shared view. Switch agents while everyone keeps working."
            : "Each agent has its own chat, shown side by side.";
    }

    private void SetSuggestion(string text)
    {
        SuggestionText.Text = text;
        SuggestionText.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnPanelSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var stacked = ActualWidth < 760;
        Grid.SetColumnSpan(TaskFields, stacked ? 3 : 1);
        Grid.SetColumn(ModelFields, stacked ? 0 : 2);
        Grid.SetRow(ModelFields, stacked ? 1 : 0);
        Grid.SetColumnSpan(ModelFields, stacked ? 3 : 1);
        ModelFields.Margin = new Thickness(0, stacked ? 24 : 0, 0, 0);
    }

    private async Task<bool> SuggestWorkersAsync()
    {
        if (_host is null || Objective.Length == 0 || _maximum < 1 || _suggestion is not null) return false;
        using var request = new CancellationTokenSource();
        _suggestion = request;
        ShowError("");
        SetSuggestion("Choosing a team size for your task…");
        UpdateControls();
        try
        {
            var suggestion = await BridgeTeamSuggestionService.SuggestAsync(_host, Objective, _maximum, request.Token, OrchestratorCount,
                OrchestratorCount > 1 || _hasOtherOrchestrators ? CentralConfiguration : OrchestratorConfiguration,
                waiting => SetSuggestion(waiting ? "Waiting for the planning model's usage limit to reset. Team sizing will continue automatically."
                    : "Choosing a team size for your task…"));
            if (_closed || request.IsCancellationRequested || !ReferenceEquals(_suggestion, request)) return false;
            WorkerCountBox.SelectedItem = Math.Max(OrchestratorCount, suggestion.WorkerCount);
            SetSuggestion(suggestion.Reason);
            return true;
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { return false; }
        catch (Exception ex)
        {
            if (!_closed && ReferenceEquals(_suggestion, request))
            {
                SetSuggestion("");
                ShowError(ex.Message + " Choose a worker count above or try again.");
            }
            return false;
        }
        finally
        {
            if (ReferenceEquals(_suggestion, request))
            {
                _suggestion = null;
                UpdateControls();
            }
        }
    }

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        if (!StartButton.IsEnabled) return;
        if (IsAdvanced && AutomaticWorkers && !await SuggestWorkersAsync()) return;
        if (_closed) return;
        _starting = true;
        UpdateControls();
        try { StartRequested?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { _starting = false; UpdateControls(); }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => CancelRequested?.Invoke(this, EventArgs.Empty);

    private void OnPanelKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (HasOpenDropDown(this)) return;
            CancelRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            OnStart(sender, e);
            e.Handled = true;
        }
    }

    private static bool HasOpenDropDown(DependencyObject root)
    {
        if (root is ComboBox { IsDropDownOpen: true }) return true;
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++)
            if (HasOpenDropDown(System.Windows.Media.VisualTreeHelper.GetChild(root, index))) return true;
        return false;
    }

    private void OnMicArm(object sender, MouseButtonEventArgs e) => SpeechService.Instance.ArmMic();

    private async void OnMic(object sender, RoutedEventArgs e)
    {
        var speech = SpeechService.Instance;
        ShowError("");
        if (_micToken != 0 && speech.Owns(_micToken) && speech.State == SpeechState.Recording)
        {
            var token = _micToken;
            _transcribingSince = DateTime.UtcNow;
            SpeechText.Text = "Transcribing…";
            try
            {
                var text = await speech.StopAndTranscribeAsync(token);
                if (_closed || _micToken != token || !speech.Owns(token)) return;
                if (string.IsNullOrWhiteSpace(text)) ShowError("No speech detected. Try again closer to the microphone.");
                else
                {
                    var at = ObjectiveBox.SelectionStart;
                    var lead = at > 0 && !char.IsWhiteSpace(ObjectiveBox.Text[at - 1]) ? " " : "";
                    var end = at + ObjectiveBox.SelectionLength;
                    var trail = end < ObjectiveBox.Text.Length && !char.IsWhiteSpace(ObjectiveBox.Text[end]) ? " " : "";
                    ObjectiveBox.SelectedText = lead + text.Trim() + trail;
                    ObjectiveBox.CaretIndex = at + lead.Length + text.Trim().Length + trail.Length;
                    ObjectiveBox.Focus();
                }
            }
            catch (Exception ex)
            {
                if (!_closed && _micToken == token) ShowError("Speech-to-text failed: " + ex.Message);
            }
            finally
            {
                if (_micToken == token)
                {
                    _micToken = 0;
                    _speechTimer.Stop();
                    ResetSpeechVisual();
                    UpdateControls();
                }
            }
            return;
        }
        if (speech.IsBusy)
        {
            ShowError(_micToken != 0 ? "Speech-to-text is still working. Your task will appear here." : "Another microphone is in use. Finish that recording, then try here.");
            return;
        }
        if (!speech.StartRecording(out var error, out _micToken, this))
        {
            ShowError(error ?? "The microphone is unavailable. Check your Windows microphone settings.");
            return;
        }
        _speechTimer.Start();
        RefreshSpeech();
        UpdateControls();
    }

    private void RefreshSpeech()
    {
        var speech = SpeechService.Instance;
        if (_micToken == 0 || !speech.Owns(_micToken))
        {
            _micToken = 0;
            _speechTimer.Stop();
            ResetSpeechVisual();
            UpdateControls();
            return;
        }
        if (speech.State == SpeechState.Downloading && speech.DownloadStalledFor > TimeSpan.FromMinutes(2) ||
            speech.State == SpeechState.Transcribing && DateTime.UtcNow - (speech.TranscriptionStartedAtUtc ?? _transcribingSince) > TimeSpan.FromSeconds(183))
        {
            speech.ForceReset();
            ShowError("Speech-to-text stopped responding. Try the microphone again.");
            RefreshSpeech();
            return;
        }
        var recording = speech.State == SpeechState.Recording;
        MicGlyph.SetResourceReference(TextBlock.ForegroundProperty, recording ? "WeatherBlue" : "Accent");
        SpeechText.Text = recording ? "Listening · click to finish" : speech.State == SpeechState.Downloading
            ? "Downloading speech model " + speech.DownloadProgressText
            : SpeechService.UsesGroq && GroqSpeechService.Instance.Reachable != false ? "Transcribing with Groq…"
            : speech.FallbackStartedAtUtc is not null ? "Finishing with Windows speech recognition…" : "Transcribing…";
        SpeechText.ToolTip = SpeechText.Text;
        MicButton.ToolTip = recording ? "Stop dictation and insert text" : "Transcribing your task";
        System.Windows.Automation.AutomationProperties.SetName(MicButton, recording ? "Stop dictating team task" : "Dictate team task");
    }

    private void ResetSpeechVisual()
    {
        MicGlyph.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        SpeechText.Text = "Type or dictate";
        SpeechText.ToolTip = null;
        MicButton.ToolTip = SpeechService.UsesGroq ? "Dictate task with Groq" : "Dictate task";
        System.Windows.Automation.AutomationProperties.SetName(MicButton, "Dictate team task");
    }
}
