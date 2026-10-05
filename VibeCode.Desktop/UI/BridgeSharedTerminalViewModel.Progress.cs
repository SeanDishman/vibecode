using VibeCode.Services;

namespace VibeCode.UI;

public sealed partial class BridgeSharedTerminalViewModel
{
    private IReadOnlyList<BridgePlanStep> _planSteps = Array.Empty<BridgePlanStep>();
    private string _shownPlanAgreement = "";
    public IReadOnlyList<BridgePlanStep> PlanSteps => _planSteps;
    public bool HasPlan => PlanSteps.Count > 0;
    public int CompletedSteps => PlanSteps.Count(s => s.State == "completed");
    public int TotalSteps => PlanSteps.Count;
    public string PlanProgressText => $"{CompletedSteps} of {TotalSteps} step{(TotalSteps == 1 ? "" : "s")} complete";
    public string CurrentPlanStep => string.Join(" · ", PlanSteps.Where(s => s.State is "running" or "dispatching").Select(s => s.Title)) is { Length: > 0 } current
        ? "Now: " + current : PlanSteps.Any(s => s.State is "blocked" or "failed" or "interrupted")
            ? "Needs attention: " + string.Join(" · ", PlanSteps.Where(s => s.State is "blocked" or "failed" or "interrupted").Select(s => s.Title))
            : HasPlan && CompletedSteps == TotalSteps ? "All planned steps are complete" : "Waiting for the next step";
    public string PlanAgreementText => Agents.FirstOrDefault()?.BridgeWork is { } work
        ? $"Plan v{work.PlanVersion}" + (work.DispatchStarted ? " · Setup closed" : " · Planning") : "";

    private void RefreshProgress()
    {
        if (_shownPlanAgreement != PlanAgreementText)
        {
            _shownPlanAgreement = PlanAgreementText;
            Raise(nameof(PlanAgreementText));
        }
        var work = Agents.FirstOrDefault()?.BridgeWork;
        var steps = work?.Tasks.Select((task, index) => new BridgePlanStep(task.Id, task.Title, task.State,
            $"{index + 1}. {task.Title}",
            (Agents.FirstOrDefault(a => a.BridgeAgentId == task.OwnerId)?.BridgeTerminalIdentity ?? "Departed agent") + " · " +
            (task.State == "queued" && !work.Ready(task) ? "Waiting for dependencies" : task.State switch
            {
                "planned" => "Planned", "queued" => "Queued", "dispatching" => "Starting", "running" => "In progress",
                "completed" => "Complete", "interrupted" => "Interrupted — check before retrying", "failed" => "Failed", "blocked" => "Blocked", _ => task.State,
            }),
            task.Summary + (task.Evidence.Length > 0 ? "\nEvidence: " + task.Evidence : "") +
            (task.Dependencies.Count > 0 ? "\nDepends on: " + string.Join(", ", task.Dependencies) : ""),
            task.Files.Count > 0 ? "File activity: " + string.Join(", ", task.Files) + " (shared editing allowed)" : "")).ToArray()
            ?? Array.Empty<BridgePlanStep>();
        if (_planSteps.SequenceEqual(steps)) return;
        _planSteps = steps;
        foreach (var name in new[] { nameof(PlanSteps), nameof(HasPlan), nameof(CompletedSteps), nameof(TotalSteps),
                     nameof(PlanProgressText), nameof(CurrentPlanStep) }) Raise(name);
    }
}

public sealed record BridgePlanStep(string Id, string Title, string State, string Label, string StatusText, string Detail, string Files);
