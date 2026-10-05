using System.Windows;
using System.Windows.Controls;
using VibeCode.Services;

namespace VibeCode.UI;

public partial class BridgeAgentOptionsPanel : UserControl
{
    private ChatViewModel? _host;
    private string _orchestratorProvider = "claude";
    private string _workerProvider = "claude";
    private string _centralProvider = "claude";
    private bool _workerFastMode;
    private bool _updating;
    private readonly Dictionary<string, BridgeAgentConfiguration> _orchestratorChoices = new();
    private readonly Dictionary<string, BridgeAgentConfiguration> _workerChoices = new();
    private readonly Dictionary<string, BridgeAgentConfiguration> _centralChoices = new();
    public event EventHandler? ConfigurationChanged;
    private sealed record ProviderChoice(string Id, string Label);

    public BridgeAgentOptionsPanel()
    {
        InitializeComponent();
        OrchestratorReviewBox.ItemsSource = WorkerReviewBox.ItemsSource = BridgeReviewPolicy.Choices;
        OrchestratorReviewBox.SelectedValue = WorkerReviewBox.SelectedValue = BridgeReviewPolicy.Normal;
        WorkerProviderBox.ItemsSource = CentralProviderBox.ItemsSource = BridgeAgentConfigurationPolicy.Providers
            .Select(provider => new ProviderChoice(provider, ProviderModelCatalog.DisplayName(provider))).ToArray();
    }

    public BridgeAgentConfiguration OrchestratorConfiguration => Read(_orchestratorProvider, OrchestratorModelBox, OrchestratorEffortBox, OrchestratorReviewBox,
        OrchestratorFastModeToggle.IsChecked == true);
    public BridgeAgentConfiguration WorkerConfiguration => Read(_workerProvider, WorkerModelBox, WorkerEffortBox, WorkerReviewBox, _workerFastMode);
    public BridgeAgentConfiguration CentralConfiguration => new(_centralProvider,
        (CentralModelBox.SelectedItem as ModelChoice)?.Value, (CentralEffortBox.SelectedItem as EffortChoice)?.Value);
    public void ShowCentralPlanner(bool visible) => CentralFields.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    public void Configure(ChatViewModel host, string orchestratorProvider)
    {
        if (ReferenceEquals(host, _host)) { SetOrchestratorProvider(orchestratorProvider); return; }
        _host = host;
        if (host.Models.Count > 0) ProviderModelCatalog.Remember(host.Provider, host.Models);
        _orchestratorChoices.Clear();
        _workerChoices.Clear();
        _centralChoices.Clear();
        _centralProvider = ProviderModelCatalog.Normalize(orchestratorProvider);
        _orchestratorProvider = ProviderModelCatalog.Normalize(orchestratorProvider);
        var worker = host.BridgeWorkerConfiguration ?? BridgeAgentConfigurationPolicy.Defaults(host.Provider, host);
        _workerProvider = worker.Provider;
        _workerChoices[_workerProvider] = worker;
        _updating = true;
        try
        {
            OrchestratorReviewBox.SelectedValue = BridgeReviewPolicy.Normalize(host.BridgeReviewLevel);
            WorkerReviewBox.SelectedValue = BridgeReviewPolicy.Normalize(worker.ReviewLevel);
            PopulateOrchestrator(BridgeAgentConfigurationPolicy.Defaults(_orchestratorProvider, host));
            WorkerProviderBox.SelectedValue = _workerProvider;
            Populate(_workerProvider, worker, WorkerModelBox, WorkerEffortBox, WorkerEffortNote);
            _workerFastMode = worker.FastMode ?? host.FastMode;
            CentralProviderBox.SelectedValue = _centralProvider;
            Populate(_centralProvider, BridgeAgentConfigurationPolicy.Defaults(_centralProvider, host),
                CentralModelBox, CentralEffortBox, CentralEffortNote);
        }
        finally { _updating = false; }
    }

    public void SetOrchestratorProvider(string provider)
    {
        provider = ProviderModelCatalog.Normalize(provider);
        if (provider == _orchestratorProvider) return;
        _orchestratorChoices[_orchestratorProvider] = OrchestratorConfiguration;
        _orchestratorProvider = provider;
        _updating = true;
        try
        {
            PopulateOrchestrator(_orchestratorChoices.GetValueOrDefault(provider) ?? BridgeAgentConfigurationPolicy.Defaults(provider, _host));
        }
        finally { _updating = false; }
        ConfigurationChanged?.Invoke(this, EventArgs.Empty);
    }

    private static BridgeAgentConfiguration Read(string provider, ComboBox models, ComboBox efforts, ComboBox reviews, bool fastMode) =>
        new(provider, (models.SelectedItem as ModelChoice)?.Value, (efforts.SelectedItem as EffortChoice)?.Value,
            reviews.SelectedValue as string ?? BridgeReviewPolicy.Normal,
            fastMode && BridgeAgentConfigurationPolicy.SupportsFastMode(provider, models.SelectedItem as ModelChoice));

    private void PopulateOrchestrator(BridgeAgentConfiguration configuration)
    {
        Populate(_orchestratorProvider, configuration, OrchestratorModelBox, OrchestratorEffortBox, OrchestratorEffortNote);
        OrchestratorFastModeToggle.IsChecked = configuration.FastMode == true;
        RefreshFastMode();
    }

    private void RefreshFastMode()
    {
        var visible = _orchestratorProvider is "claude" or "codex";
        var supported = BridgeAgentConfigurationPolicy.SupportsFastMode(_orchestratorProvider, OrchestratorModelBox.SelectedItem as ModelChoice);
        OrchestratorFastModeToggle.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        OrchestratorFastModeToggle.IsEnabled = supported;
        if (!supported) OrchestratorFastModeToggle.IsChecked = false;
        OrchestratorFastModeToggle.ToolTip = supported
            ? "Faster output for the orchestrator only. Increased usage."
            : "Fast mode is unavailable for this model.";
    }

    private void OnFastModeChanged(object sender, RoutedEventArgs e)
    {
        if (!_updating) ConfigurationChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void Populate(string provider, BridgeAgentConfiguration configuration, ComboBox models, ComboBox efforts, TextBlock note)
    {
        configuration = BridgeAgentConfigurationPolicy.Normalize(configuration);
        models.ItemsSource = BridgeAgentConfigurationPolicy.ModelsFor(provider);
        models.SelectedItem = models.Items.OfType<ModelChoice>().FirstOrDefault(model => model.Value == configuration.Model);
        PopulateEfforts(provider, models, efforts, note, configuration.Effort);
    }

    private static void PopulateEfforts(string provider, ComboBox models, ComboBox efforts, TextBlock note, string? effort)
    {
        var choices = BridgeAgentConfigurationPolicy.EffortsFor(provider, models.SelectedItem as ModelChoice, effort);
        var supported = choices.Count > 0;
        if (!supported) choices.Add(new EffortChoice { Value = null, Label = "Default" });
        efforts.ItemsSource = choices;
        efforts.SelectedItem = choices.FirstOrDefault(choice => string.Equals(choice.Value, effort, StringComparison.OrdinalIgnoreCase)) ?? choices[0];
        efforts.IsEnabled = supported;
        note.Text = "This model has no configurable effort in its current catalog. It uses the provider default.";
        note.Visibility = supported ? Visibility.Collapsed : Visibility.Visible;
        efforts.ToolTip = supported ? "Reasoning effort for this role" : note.Text;
    }

    private void OnWorkerProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || WorkerProviderBox.SelectedValue is not string provider) return;
        _workerChoices[_workerProvider] = WorkerConfiguration;
        _workerProvider = provider;
        _updating = true;
        try
        {
            var configuration = _workerChoices.GetValueOrDefault(provider) ?? BridgeAgentConfigurationPolicy.Defaults(provider, _host);
            Populate(provider, configuration, WorkerModelBox, WorkerEffortBox, WorkerEffortNote);
            _workerFastMode = configuration.FastMode ?? _host?.FastMode == true;
        }
        finally { _updating = false; }
        ConfigurationChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnOrchestratorModelChanged(object sender, SelectionChangedEventArgs e) =>
        ModelChanged(_orchestratorProvider, OrchestratorModelBox, OrchestratorEffortBox, OrchestratorEffortNote);
    private void OnWorkerModelChanged(object sender, SelectionChangedEventArgs e) =>
        ModelChanged(_workerProvider, WorkerModelBox, WorkerEffortBox, WorkerEffortNote);
    private void OnCentralModelChanged(object sender, SelectionChangedEventArgs e) =>
        ModelChanged(_centralProvider, CentralModelBox, CentralEffortBox, CentralEffortNote);

    private void OnCentralProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || CentralProviderBox.SelectedValue is not string provider) return;
        _centralChoices[_centralProvider] = CentralConfiguration;
        _centralProvider = provider;
        _updating = true;
        try
        {
            Populate(provider, _centralChoices.GetValueOrDefault(provider) ?? BridgeAgentConfigurationPolicy.Defaults(provider, _host),
                CentralModelBox, CentralEffortBox, CentralEffortNote);
        }
        finally { _updating = false; }
        ConfigurationChanged?.Invoke(this, EventArgs.Empty);
    }
    private void ModelChanged(string provider, ComboBox models, ComboBox efforts, TextBlock note)
    {
        if (_updating) return;
        var previousEffort = (efforts.SelectedItem as EffortChoice)?.Value;
        _updating = true;
        try
        {
            PopulateEfforts(provider, models, efforts, note, previousEffort);
            if (ReferenceEquals(models, OrchestratorModelBox)) RefreshFastMode();
        }
        finally { _updating = false; }
        ConfigurationChanged?.Invoke(this, EventArgs.Empty);
    }
    private void OnChoiceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updating) ConfigurationChanged?.Invoke(this, EventArgs.Empty);
    }
}
