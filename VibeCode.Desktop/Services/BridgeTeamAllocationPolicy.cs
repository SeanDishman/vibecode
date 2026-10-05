namespace VibeCode.Services;

/// <summary>Worker counts are ordered by orchestrator; each worker has exactly one owner.</summary>
public static class BridgeTeamAllocationPolicy
{
    public static int MaximumOrchestrators(int singleGroupWorkerCapacity) =>
        Math.Max(0, (singleGroupWorkerCapacity + 1) / 2);

    public static int MaximumWorkers(int singleGroupWorkerCapacity, int orchestrators) =>
        Math.Max(0, singleGroupWorkerCapacity - Math.Max(0, orchestrators - 1));

    public static int[] Distribute(int workers, int orchestrators)
    {
        if (orchestrators < 1 || workers < orchestrators)
            throw new ArgumentException("Choose at least one worker for every orchestrator.");
        return Enumerable.Range(0, orchestrators)
            .Select(index => workers / orchestrators + (index < workers % orchestrators ? 1 : 0)).ToArray();
    }

    public static void Validate(IReadOnlyList<int> allocation, int singleGroupWorkerCapacity)
    {
        if (allocation.Count < 1 || allocation.Any(count => count < 1))
            throw new ArgumentException("Choose at least one orchestrator and one worker per group.");
        if (allocation.Count > MaximumOrchestrators(singleGroupWorkerCapacity) ||
            allocation.Sum(count => (long)count) > MaximumWorkers(singleGroupWorkerCapacity, allocation.Count))
            throw new ArgumentException("These groups exceed the bridge's agent limit. Choose fewer agents or raise the limit in Settings.");
    }
}
