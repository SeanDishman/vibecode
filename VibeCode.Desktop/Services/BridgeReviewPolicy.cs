namespace VibeCode.Services;

public sealed record BridgeReviewChoice(string Value, string Label, int PassLimit, string Description);

public static class BridgeReviewPolicy
{
    public const string Normal = "normal";
    public static IReadOnlyList<BridgeReviewChoice> Choices { get; } = Array.AsReadOnly(new[]
    {
        new BridgeReviewChoice("none", "None", 0, "No extra review passes. Complete the task and its requested checks."),
        new BridgeReviewChoice("low", "Low", 1, "One quick pass for obvious problems in the changed work."),
        new BridgeReviewChoice(Normal, "Normal", 1, "One focused pass over the changed work and relevant integration."),
        new BridgeReviewChoice("high", "High", 2, "One thorough pass, then at most one follow-up pass for corrections."),
    });

    public static bool IsKnown(string? value) => Choices.Any(choice =>
        string.Equals(choice.Value, value?.Trim(), StringComparison.OrdinalIgnoreCase));
    public static string Normalize(string? value) => Choice(value).Value;
    public static BridgeReviewChoice Choice(string? value) => Choices.FirstOrDefault(choice =>
        string.Equals(choice.Value, value?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? Choices[2];

    public static string TurnInstructions(string? level, bool orchestrator) =>
        "[BRIDGE REVIEW SETTINGS]" + Instructions(level, orchestrator);

    public static string Instructions(string? level, bool orchestrator)
    {
        var choice = Choice(level);
        var scope = orchestrator ? "goal" : "assignment";
        var direction = choice.Value switch
        {
            "none" => "Skip optional self-review, audits and review-only assignments. Do not start a final inspection pass.",
            "low" => "Make at most one quick review pass for obvious defects in the changed work. Avoid broad audits and independent reviewer assignments.",
            "high" => "Make at most two review passes: one thorough review of the actual output and relevant integration, then one focused follow-up on corrections if needed.",
            _ => "Make at most one focused review pass over the changed work and relevant integration. Avoid repeated audits and extra independent reviews.",
        };
        return $"\n[REVIEW LEVEL: {choice.Label.ToUpperInvariant()} — USER SELECTED]\n" +
            $"Your optional review budget is {choice.PassLimit} pass(es) for this {scope}. {direction}\n" +
            "Required work and checks explicitly requested by the user still apply. Do not reset this review budget when a correction arrives. " +
            "A delegated review counts toward the orchestrator's budget; respect the selected worker's review level too. " +
            "Fix concrete defects within your assigned role, then report remaining limitations rather than starting more review cycles.\n" +
            (orchestrator ? "When work and requested checks finish, record scope completion once with bridge_review_scope. " +
                (choice.Value == "none" ? "Use approved to record completion without a review; the app labels it skipped, not passed. Disclose that extra review was disabled.\n"
                    : "Use approved only for a satisfactory result; use changes_requested for concrete remaining defects. Cite the evidence you actually inspected.\n") : "");
    }
}
