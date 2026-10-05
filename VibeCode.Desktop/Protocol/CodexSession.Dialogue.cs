using System.Diagnostics;
using System.Text.Json.Nodes;
using VibeCode.Services;

namespace VibeCode.Protocol;

public sealed partial class CodexSession
{
    /// <summary>The model actually resolved by app-server's thread/start response, including provider aliases.</summary>
    public string? ResolvedModel { get; private set; }
    private void CaptureResolvedModel(JsonNode? response) => ResolvedModel = response?["model"]?.ToString();
    private void ConfigureDialogueOnlyThread(JsonObject request)
    {
        if (!_options.DialogueOnly) return;
        // Replace the coding CLI persona only for this isolated structured assistant thread.
        request["baseInstructions"] = _effectiveAppendPrompt;
        request["developerInstructions"] = _options.DialogueInstructions ?? "Return the desktop action plan as JSON. The host executes supported actions after validation.";
    }

    private void ConfigureDialogueOnlyTurn(JsonObject request)
    {
        if (!_options.DialogueOnly) return;
        request["outputSchema"] = _options.DialogueOutputSchema?.DeepClone() ?? JarvisPlanParser.CreateOutputSchema();
    }
    private void ConfigureDialogueOnlyProcess(ProcessStartInfo process)
    {
        if (!_options.DialogueOnly) return;
        foreach (var setting in new[]
        {
            "features.shell_tool=false", "features.unified_exec=false", "features.apply_patch_freeform=false",
            "features.code_mode=false", "features.multi_agent=false", "features.multi_agent_v2=false",
            "features.js_repl=false", "web_search=\"disabled\"", "project_doc_max_bytes=0",
            "mcp_servers={}", "plugins={}",
        })
        {
            process.ArgumentList.Add("-c");
            process.ArgumentList.Add(setting);
        }
    }
}
