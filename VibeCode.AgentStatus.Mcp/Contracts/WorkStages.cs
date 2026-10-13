namespace VibeCode.AgentStatus.Mcp.Contracts;

public static class WorkStages
{
    public const string Version = "coding-v1";
    public sealed record Stage(string Id, string Name);
    public static IReadOnlyList<Stage> All { get; } = Array.AsReadOnly(new[]
    {
        new Stage("planning", "Planning"), new Stage("reading", "Reading"),
        new Stage("implementing", "Implementing"), new Stage("testing", "Testing"),
        new Stage("reviewing", "Reviewing"), new Stage("documenting", "Documenting"),
    });
}
