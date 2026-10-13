using System.Text.Json.Nodes;
using VibeCode.Services;

namespace VibeCode.Protocol;


// Gemini model selection and usage reconciliation for the ACP session.
public sealed partial class GeminiAcpSession
{
    private IReadOnlyDictionary<string, GeminiTokenRecord>? _geminiUsageBefore;
    private bool _geminiPromptSeen;
    private bool _geminiUsageActive;
    private readonly object _geminiUsageGate = new();
    private bool _geminiTranscriptBaselineKnown;

    private void BeginGeminiUsage()
    {
        lock (_geminiUsageGate)
        {
            _geminiUsageBefore = GeminiSessionUsage.Read(SessionId);
            _geminiTranscriptBaselineKnown = _geminiUsageBefore is not null || (!_geminiPromptSeen && _options.Resume is null);
            _geminiPromptSeen = true;
            _geminiUsageActive = true;
        }
    }

    private JsonObject EndGeminiUsage(JsonNode? response)
    {
        lock (_geminiUsageGate) _geminiUsageActive = false;
        var after = GeminiSessionUsage.Read(SessionId);
        if (GeminiSessionUsage.ContextReport(after) is { } context) Emit(context);
        if (SessionId is { } id) GeminiSessionUsage.RememberSession(id, _options.Cwd);
        return GeminiSessionUsage.TurnReport(_geminiUsageBefore, _geminiTranscriptBaselineKnown ? after : null, response);
    }

    internal JsonObject? GeminiUsageSnapshot()
    {
        lock (_geminiUsageGate)
        {
            if (!_geminiUsageActive || !_geminiTranscriptBaselineKnown) return null;
            var after = GeminiSessionUsage.Read(SessionId);
            if (after is null) return null;
            var report = GeminiSessionUsage.TurnReport(_geminiUsageBefore, after, null);
            var usage = report["usage"]!.AsObject();
            usage["_vibecode_aggregate_prompt"] = true;
            if (GeminiSessionUsage.ContextReport(after)?["usage"] is JsonObject context)
                foreach (var (key, value) in context) usage[key] = value?.DeepClone();
            return report;
        }
    }

    private void EmitGeminiProgress()
    {
        lock (_geminiUsageGate)
            if (GeminiUsageSnapshot() is { } report)
            {
                report["type"] = "system"; report["subtype"] = "usage_update";
                Emit(report);
            }
    }

    private void ApplyGeminiModelState(JsonObject? state)
    {
        if (state?["availableModels"] is not JsonArray models) return;
        var current = state["currentModelId"]?.ToString();
        Models = new JsonArray();
        foreach (var model in models.OfType<JsonObject>())
        {
            if (model["modelId"]?.ToString() is not { Length: > 0 } id) continue;
            Models.Add(new JsonObject
            {
                ["value"] = id,
                ["displayName"] = model["name"]?.ToString() ?? id,
                ["description"] = model["description"]?.ToString() ?? "",
                ["isDefault"] = id == current,
                ["supportsEffort"] = false,
                ["supportsFastMode"] = false,
                ["supportsAutoMode"] = false,
            });
        }
        _model = current ?? _model;
    }

    private async Task ApplyGeminiSelectionsAsync()
    {
        if (_requestedModel is { Length: > 0 } requested && Models.OfType<JsonObject>().Any(m => m["value"]?.ToString() == requested))
            await SetGeminiModelAsync(requested);
        await SetGeminiModeAsync(_requestedPermissionMode);
        GeminiSessionUsage.RememberSession(SessionId!, _options.Cwd);
        GeminiUsageService.Instance.Refresh(force: true);
    }

    private async Task SetGeminiModelAsync(string? model)
    {
        if (string.IsNullOrWhiteSpace(model) || model == "default") return;
        _requestedModel = model;
        if (SessionId is null) return;
        await RequestAsync("session/set_model", new JsonObject { ["sessionId"] = SessionId, ["modelId"] = model });
        _model = model;
        foreach (var row in Models.OfType<JsonObject>()) row["isDefault"] = row["value"]?.ToString() == model;
    }

    private Task SetGeminiModeAsync(string mode) => RequestAsync("session/set_mode", new JsonObject
    {
        ["sessionId"] = SessionId,
        // VibeCode's auto/don't-ask policies answer individual requests. They do not grant Gemini blanket yolo.
        ["modeId"] = mode switch { "plan" => "plan", "bypassPermissions" => "yolo", "acceptEdits" => "auto_edit", _ => "default" },
    });
}
