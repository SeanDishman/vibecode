using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace VibeCode.AgentStatus.Mcp.Contracts;

public sealed class StatusValidationException(string message) : Exception(message);

// The AI owns only these descriptive fields. Identity, turn, timestamps and effective permissions
// are deliberately not writable arguments. A host binds a receipt to its authenticated call context.
public sealed record AgentStatusReport(string ReportId, DateTimeOffset ReportedAt,
    string Stage, string Activity, string? Step, string State, string? TaskTitle = null,
    string? Tool = null, string? ToolAction = null)
{
    public const int SchemaVersion = 1;
    public const int MaxActivityLength = 240;
    public const int MaxStepLength = 100;
    public const int MaxTaskTitleLength = 80;
    public const int MaxToolLength = 80;
    public const int MaxToolActionLength = 160;
    public static IReadOnlyList<string> States { get; } = Array.AsReadOnly(new[] { "working", "waiting", "blocked", "completed" });
    private static readonly HashSet<string> Fields = new(StringComparer.Ordinal)
    {
        "stage", "activity", "step", "state", "task_title", "tool", "tool_action",
    };
    private static readonly Regex Sensitive = new(@"(?i)\b(?:bearer\s+\S+|(?:password|api[_ -]?key|access[_ -]?token|refresh[_ -]?token)\s*[:=]\s*\S+)|\b(?:sk-[A-Za-z0-9_-]{12,}|eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+)\b",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static AgentStatusReport Create(JsonObject args)
    {
        if (args.Any(pair => !Fields.Contains(pair.Key))) throw new StatusValidationException("Unknown field. Identity and access permissions cannot be set by this tool.");
        var stage = Text(args["stage"]);
        if (stage is null || (stage != "unknown" && !AttackTactics.All.Any(t => t.Id == stage)))
            throw new StatusValidationException("stage must be a supported TA identifier or 'unknown'; use list_stages for the catalog.");
        var activity = ValidateText(args["activity"], MaxActivityLength, required: true)!;
        var step = ValidateText(args["step"], MaxStepLength, required: false);
        var taskTitle = ValidateText(args["task_title"], MaxTaskTitleLength, required: false);
        var tool = ValidateText(args["tool"], MaxToolLength, required: false);
        var toolAction = ValidateText(args["tool_action"], MaxToolActionLength, required: false);
        var state = Text(args["state"]) ?? (args.ContainsKey("state") ? null : "working");
        if (state is null || !States.Contains(state)) throw new StatusValidationException("state must be working, waiting, blocked or completed.");
        return new(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, stage, activity, step, state, taskTitle, tool, toolAction);
    }

    public JsonObject ToJson() => new()
    {
        ["schema_version"] = SchemaVersion, ["kind"] = "agent_status", ["report_id"] = ReportId,
        ["reported_at"] = ReportedAt.ToString("O"), ["stage"] = Stage,
        ["activity"] = Activity, ["step"] = Step, ["state"] = State, ["task_title"] = TaskTitle,
        ["tool"] = Tool, ["tool_action"] = ToolAction,
    };

    public static AgentStatusReport? FromReceipt(JsonNode? node)
    {
        try
        {
            if (node is not JsonObject value || value["schema_version"] is not JsonValue version
                || !version.TryGetValue<int>(out var number) || number != SchemaVersion
                || Text(value["kind"]) != "agent_status"
                || !Guid.TryParseExact(Text(value["report_id"]), "N", out var id)
                || !DateTimeOffset.TryParseExact(Text(value["reported_at"]), "O", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var time)) return null;
            if (!value.ContainsKey("state")) return null;
            var report = Create(new JsonObject
            {
                ["stage"] = value["stage"]?.DeepClone(), ["activity"] = value["activity"]?.DeepClone(),
                ["step"] = value["step"]?.DeepClone(), ["state"] = value["state"]?.DeepClone(),
                ["task_title"] = value["task_title"]?.DeepClone(),
                ["tool"] = value["tool"]?.DeepClone(), ["tool_action"] = value["tool_action"]?.DeepClone(),
            });
            return report with { ReportId = id.ToString("N"), ReportedAt = time };
        }
        catch (StatusValidationException) { return null; }
    }

    public static string? Text(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : null;
    private static string? ValidateText(JsonNode? value, int maximum, bool required)
    {
        if (value is null && !required) return null;
        var text = Text(value)?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > maximum || text.Any(char.IsControl))
            throw new StatusValidationException($"Descriptions must be nonempty single-line text within their {maximum}-character limit.");
        if (Sensitive.IsMatch(text)) throw new StatusValidationException("Do not include credentials or tokens in status reports.");
        return text;
    }
}
