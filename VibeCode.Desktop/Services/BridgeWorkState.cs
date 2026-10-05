using System.Text.Json.Serialization;

namespace VibeCode.Services;

/// <summary>Small, event-driven journal saved with the bridge. IDs survive process and pane replacement.</summary>
public sealed class BridgeWorkState
{
    public string RunId { get; set; } = Guid.NewGuid().ToString("N");
    public int PlanVersion { get; set; } = 1;
    public bool DispatchStarted { get; set; }
    public string OrchestratorRoster { get; set; } = "";
    public Dictionary<string, int> ConfirmedVersions { get; set; } = new();
    public Dictionary<string, string> Scopes { get; set; } = new();
    public HashSet<string> SetupMessages { get; set; } = new();
    public HashSet<string> SetupReads { get; set; } = new();
    public List<BridgeWorkTask> Tasks { get; set; } = new();
    public Dictionary<string, string> OrchestratorObjectives { get; set; } = new();
    public BridgeCentralPlan? CentralPlan { get; set; }

    public static string PeerKey(string from, string to) => from + ":" + to;
    public bool Confirmed(string agentId) => ConfirmedVersions.GetValueOrDefault(agentId) == PlanVersion;
    public bool Ready(BridgeWorkTask task) => task.Dependencies.All(id => Tasks.Any(t => t.Id == id && t.State == "completed"));

    public void Recover()
    {
        if (CentralPlan is { State: "planning" or "handing_off" } central)
        {
            central.State = "interrupted";
            central.Error = "Central orchestration was interrupted. Start a new team to divide the task again.";
            ConfirmedVersions.Clear();
        }
        // A crash may happen after a side effect but before its result is saved. Never replay it implicitly.
        foreach (var task in Tasks.Where(t => t.State is "running" or "dispatching" or "queued"))
        {
            task.State = "interrupted";
            task.Summary = "Session ended before completion was confirmed. Check existing work before retrying this task.";
        }
    }

    public static void ValidateGraph(IReadOnlyList<BridgeWorkTask> tasks)
    {
        if (tasks.Count > 500) throw new InvalidOperationException("A bridge plan can retain at most 500 tasks.");
        var index = new Dictionary<string, BridgeWorkTask>(StringComparer.Ordinal);
        foreach (var task in tasks)
        {
            if (string.IsNullOrWhiteSpace(task.Id) || task.Id.Length > 64 || !index.TryAdd(task.Id, task))
                throw new InvalidOperationException("Task IDs must be nonempty, unique, and at most 64 characters.");
        }
        var visited = new HashSet<string>();
        var visiting = new HashSet<string>();
        void Visit(string id)
        {
            if (visited.Contains(id)) return;
            if (!index.TryGetValue(id, out var task)) throw new InvalidOperationException($"Unknown dependency: {id}.");
            if (!visiting.Add(id)) throw new InvalidOperationException("Task dependencies cannot contain a cycle.");
            foreach (var dependency in task.Dependencies) Visit(dependency);
            visiting.Remove(id);
            visited.Add(id);
        }
        foreach (var task in tasks) Visit(task.Id);
    }
}

public sealed class BridgeWorkTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string CoordinatorId { get; set; } = "";
    public string OwnerId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Instruction { get; set; } = "";
    public List<string> Dependencies { get; set; } = new();
    public List<string> Files { get; set; } = new();
    public string State { get; set; } = "planned";
    public string Summary { get; set; } = "";
    public string Evidence { get; set; } = "";
    public int Attempt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    [JsonIgnore] public bool Active => State is "dispatching" or "running";
}
