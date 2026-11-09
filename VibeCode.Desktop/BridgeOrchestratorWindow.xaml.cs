using System.Windows;
using VibeCode.UI;

namespace VibeCode;

public partial class BridgeOrchestratorWindow : Window
{
    public int WorkerCount => SetupPanel.WorkerCount;
    public int OrchestratorCount => SetupPanel.OrchestratorCount;
    public IReadOnlyList<int> WorkerAllocation => SetupPanel.WorkerAllocation;
    public string Objective => SetupPanel.Objective;
    public string Provider => SetupPanel.Provider;
    public bool SingleTerminal => SetupPanel.SingleTerminal;
    public bool FactCheck => SetupPanel.FactCheck;
    public bool PlanSteps => SetupPanel.PlanSteps;
    public BridgeAgentConfiguration OrchestratorConfiguration => SetupPanel.OrchestratorConfiguration;
    public BridgeAgentConfiguration WorkerConfiguration => SetupPanel.WorkerConfiguration;
    public BridgeAgentConfiguration CentralConfiguration => SetupPanel.CentralConfiguration;

    public BridgeOrchestratorWindow(ChatViewModel host, int? maximumWorkers = null, BridgeSetupPanel? retainedSetup = null,
        Func<string, int>? workerCapacity = null, bool hasOtherOrchestrators = false,
        Func<int, int, int>? projectedAgentCount = null)
    {
        InitializeComponent();
        if (retainedSetup is not null)
        {
            SetupPanel = retainedSetup;
            Content = SetupPanel;
        }
        SetupPanel.StartRequested += OnStart;
        SetupPanel.CancelRequested += OnCancel;
        MaxHeight = Math.Max(320, SystemParameters.WorkArea.Height - 60);
        Width = Math.Min(980, SystemParameters.WorkArea.Width - 60);
        SetupPanel.MaxHeight = MaxHeight - 40;
        SetupPanel.Configure(host, maximumWorkers ?? Services.BridgeAgentPolicy.ClampLimit(Services.AppSettings.Current.BridgeAgentLimit) - 1,
            advancedOnly: true, singleTerminal: host.BridgeSingleTerminal, workerCapacity: workerCapacity,
            hasOtherOrchestrators: hasOtherOrchestrators, projectedAgentCount: projectedAgentCount);
        Loaded += (_, _) => SetupPanel.FocusFirstControl();
        Closed += (_, _) =>
        {
            SetupPanel.Suspend();
            SetupPanel.StartRequested -= OnStart;
            SetupPanel.CancelRequested -= OnCancel;
            Content = null; // A cancelled draft can be hosted by the next setup window.
        };
    }

    private void OnStart(object? sender, EventArgs e) => DialogResult = true;
    private void OnCancel(object? sender, EventArgs e) => DialogResult = false;
}
