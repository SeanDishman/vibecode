using System.Text.Json.Nodes;
using VibeCode.AgentStatus.Mcp.Contracts;
using VibeCode.AgentStatus.Mcp.Reporting;

namespace VibeCode.AgentStatus.Mcp.Tools;

public interface IStatusTool
{
    string Name { get; }
    JsonObject Definition { get; }
    JsonObject Invoke(JsonObject arguments);
}

public sealed class ReportStatusTool(IStatusReportSink sink) : IStatusTool
{
    public const string ToolName = "report_status";
    public string Name => ToolName;
    public JsonObject Definition => new()
    {
        ["name"] = Name,
        ["title"] = "Report current AI activity",
        ["inputSchema"] = InputSchema(),
        ["outputSchema"] = OutputSchema(),
        ["annotations"] = new JsonObject { ["readOnlyHint"] = false, ["destructiveHint"] = false, ["idempotentHint"] = false, ["openWorldHint"] = false },
    };
    public JsonObject Invoke(JsonObject arguments)
    {
        var report = AgentStatusReport.Create(arguments);
        sink.Publish(report);
        return report.ToJson();
    }
    public static JsonObject InputSchema() => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["properties"] = new JsonObject
        {
            ["stage"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(WorkStages.All.Select(t => (JsonNode?)JsonValue.Create(t.Id)).Append(JsonValue.Create("unknown")).ToArray()) },
            ["activity"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = AgentStatusReport.MaxActivityLength },
            ["step"] = new JsonObject { ["type"] = new JsonArray("string", "null"), ["minLength"] = 1, ["maxLength"] = AgentStatusReport.MaxStepLength },
            ["task_title"] = new JsonObject
            {
                ["type"] = new JsonArray("string", "null"),
                ["minLength"] = 1,
                ["maxLength"] = AgentStatusReport.MaxTaskTitleLength,
                ["description"] = "Summarize your task in 3–7 words. Keep this heading stable while reporting changing activity and steps."
            },
            ["tool"] = new JsonObject { ["type"] = new JsonArray("string", "null"), ["minLength"] = 1, ["maxLength"] = AgentStatusReport.MaxToolLength },
            ["tool_action"] = new JsonObject
            {
                ["type"] = new JsonArray("string", "null"),
                ["minLength"] = 1,
                ["maxLength"] = AgentStatusReport.MaxToolActionLength,
                ["description"] = "What that tool is doing in one short sentence. No commands, paths, credentials or raw output."
            },
            ["state"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(AgentStatusReport.States.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()), ["default"] = "working" },
        },
        ["required"] = new JsonArray("stage", "activity"),
    };
    public static JsonObject OutputSchema()
    {
        var schema = InputSchema();
        var properties = (JsonObject)schema["properties"]!;
        properties["schema_version"] = new JsonObject { ["type"] = "integer", ["const"] = AgentStatusReport.SchemaVersion };
        properties["kind"] = new JsonObject { ["type"] = "string", ["const"] = "agent_status" };
        properties["report_id"] = new JsonObject { ["type"] = "string", ["pattern"] = "^[a-f0-9]{32}$" };
        properties["reported_at"] = new JsonObject { ["type"] = "string", ["format"] = "date-time" };
        schema["required"] = new JsonArray(properties.Select(p => (JsonNode?)JsonValue.Create(p.Key)).ToArray());
        return schema;
    }
}

public sealed class ListStagesTool : IStatusTool
{
    public string Name => "list_stages";
    public JsonObject Definition => new()
    {
        ["name"] = Name,
        ["title"] = "List reporting stage labels",
        ["inputSchema"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = false },
        ["annotations"] = new JsonObject { ["readOnlyHint"] = true, ["destructiveHint"] = false, ["idempotentHint"] = true, ["openWorldHint"] = false },
    };
    public JsonObject Invoke(JsonObject arguments)
    {
        if (arguments.Count > 0) throw new StatusValidationException("list_stages takes no arguments.");
        return new JsonObject
        {
            ["schema_version"] = AgentStatusReport.SchemaVersion,
            ["catalog"] = WorkStages.Version,
            ["stages"] = new JsonArray(WorkStages.All.Select(t => (JsonNode?)new JsonObject { ["id"] = t.Id, ["name"] = t.Name })
                .Append(new JsonObject { ["id"] = "unknown", ["name"] = "Unmapped" }).ToArray()),
        };
    }
}
