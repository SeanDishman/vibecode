using System.IO;
using System.Text;

namespace VibeCode.Protocol;

/// <summary>VibeCode's Grok 4.5 preset. It routes to xAI's real Grok 4.5 model with a clean coding-agent prompt.</summary>
public static class Grok45Preset
{
    public const string NormalModelId = "grok-4.5";
    public const string NormalDisplayName = "Grok 4.5";
    public const string NormalDescription = "Grok 4.5 with VibeCode's clean coding-agent system prompt.";

    private const string NormalPromptResource = "VibeCode.Assets.Grok45NormalPrompt.txt";
    private static readonly Lazy<string> NormalPromptValue = new(() => Load(NormalPromptResource, NormalDisplayName));

    public static string NormalSystemPrompt => NormalPromptValue.Value;

    public static bool IsGrok45(string? model, string? displayName = null)
    {
        var id = model?.Trim();
        if (string.Equals(id, NormalModelId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(id, "grok-4-5", StringComparison.OrdinalIgnoreCase)
            || string.Equals(id, "grok45", StringComparison.OrdinalIgnoreCase))
            return true;
        return string.Equals(displayName?.Trim(), NormalDisplayName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>VibeCode exposes the provider's real model ids directly, so the backend id is the requested id.</summary>
    public static string? BackendModel(string? model) => model;

    /// <summary>The CLI's compiled default prompt is replaced with VibeCode's clean coding-agent prompt.</summary>
    public static GrokSessionPrompt SessionPrompt(string? requestedModel, string? appendSystemPrompt) =>
        new(Join(NormalSystemPrompt, appendSystemPrompt), null);

    private static string Join(params string?[] values) =>
        string.Join("\n\n", values.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!.Trim()));

    private static string Load(string resource, string name)
    {
        using var stream = typeof(Grok45Preset).Assembly.GetManifestResourceStream(resource)
                           ?? throw new InvalidOperationException($"The embedded {name} prompt is missing ({resource}).");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var value = reader.ReadToEnd().Trim();
        return value.Length > 0 ? value : throw new InvalidOperationException($"The embedded {name} prompt is empty.");
    }
}

public readonly record struct GrokSessionPrompt(string? SystemPromptOverride, string? Rules);
