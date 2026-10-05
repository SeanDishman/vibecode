using System.Collections.ObjectModel;

namespace VibeCode.UI;

public sealed class BridgeSetupGroup(int number, int workers) : Observable
{
    public string Label => $"Orchestrator {number}";
    private int _workerCount = workers;
    public int WorkerCount { get => _workerCount; set => Set(ref _workerCount, value); }
    public ObservableCollection<int> WorkerChoices { get; } = new();
    internal void SetMaximum(int maximum)
    {
        // Keep the collection stable: rebuilding the ItemsSource clears a live ComboBox selection.
        while (WorkerChoices.Count > maximum) WorkerChoices.RemoveAt(WorkerChoices.Count - 1);
        while (WorkerChoices.Count < maximum) WorkerChoices.Add(WorkerChoices.Count + 1);
    }
}
