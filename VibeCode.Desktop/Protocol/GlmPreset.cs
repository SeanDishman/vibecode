namespace VibeCode.Protocol;

/// <summary>
/// GLM served through Baseten's OpenAI-compatible inference API, as an in-app provider.
///
/// Everything here was measured against the live endpoint rather than taken from documentation:
///
///   * <c>POST /v1/chat/completions</c> does real tool calling. It returns <c>finish_reason: "tool_calls"</c>
///     with a proper <c>tool_calls[]</c> array (streamed as incremental <c>arguments</c> deltas keyed by
///     <c>index</c>), and puts the model's thinking in <c>reasoning_content</c>.
///   * <c>GET /v1/models</c> is free, needs the key, and answers 403 for a bad one / 401 for none - which is what
///     makes it a usable key check in the account manager.
///   * Authorization takes BOTH <c>Api-Key {key}</c> (Baseten's documented scheme) and <c>Bearer {key}</c>. The
///     documented one is used.
///   * The gateway is permissive, not strict: an unknown <c>reasoning_effort</c>, an unsupported <c>top_k</c> and
///     a <c>developer</c> role are all answered 200 rather than 400. That is a trap rather than a convenience -
///     see <see cref="DeveloperRole"/> and <see cref="SupportsEffort"/>, because "accepted" here does not mean
///     "honoured".
///
/// None of the four CLIs VibeCode drives can carry this transport, so the provider is spoken natively by
/// <see cref="GlmSession"/>.
/// </summary>
public static class GlmPreset
{
    /// <summary>Internal provider id. Matches AppSettings.DefaultProvider and the api-key store's provider key.</summary>
    public const string ProviderId = "glm";

    /// <summary>What the user sees. The menu calls it GLM; Baseten is the host, not the model.</summary>
    public const string DisplayName = "GLM";

    public const string DefaultBaseUrl = "https://inference.baseten.co/v1";

    /// <summary>Baseten's auth scheme. Verified: <c>Bearer</c> also works, but this is the documented one.</summary>
    public const string AuthScheme = "Api-Key";

    public const string DefaultModelId = "zai-org/GLM-5.3-Flash";

    /// <summary>Verified from GET /v1/models: context_length 1048576, max_completion_tokens 131072.</summary>
    public const int ContextWindow = 1_048_576;
    public const int MaxOutputTokens = 131_072;

    /// <summary>
    /// Kept well under <see cref="MaxOutputTokens"/> per request, but deliberately generous.
    ///
    /// Thinking is billed against this same budget and GLM thinks before almost every reply, so a small cap does
    /// not produce a short answer - it produces an EMPTY one. Measured: <c>max_tokens: 64</c> came back with
    /// <c>content: null</c>, <c>finish_reason: "length"</c> and all 64 tokens spent in
    /// <c>reasoning_tokens</c>, i.e. the model never reached the part the user would have seen.
    /// </summary>
    public const int DefaultMaxTokensPerTurn = 32_768;

    /// <summary>
    /// System-level instruction goes as <c>system</c>, never <c>developer</c>.
    ///
    /// The gateway ACCEPTS a <c>developer</c> message - HTTP 200, no complaint - and then ignores it. Measured
    /// side by side with one instruction ("be terse"): as <c>system</c> the model obeyed and answered tersely; as
    /// <c>developer</c> the identical request behaved exactly as if no instruction had been sent at all. A silently
    /// dropped system prompt is worse than a rejected one, because nothing anywhere reports it.
    /// </summary>
    public const string DeveloperRole = "system";

    /// <summary>
    /// GLM has no reasoning-effort knob here, so none is advertised and none is sent.
    ///
    /// Baseten publishes per-model <c>supported_features</c>, and GLM's list is
    /// <c>[tools, json_mode, structured_outputs, reasoning]</c> - <c>reasoning_effort</c> appears only on
    /// <c>openai/gpt-oss-120b</c>. The endpoint still answers 200 to a <c>reasoning_effort</c> it does not
    /// implement (even a made-up <c>"xhigh"</c>), which is exactly why this is pinned to false rather than left
    /// to a runtime probe: shipping the effort picker on a model that ignores it would put a control in the UI
    /// that silently does nothing.
    /// </summary>
    public const bool SupportsEffort = false;

    /// <summary>
    /// The GLM family Baseten serves, strongest-cheapest first. Ids, context and pricing are read from the live
    /// <c>GET /v1/models</c> catalog, so they are what the endpoint will actually accept as <c>model</c>.
    /// </summary>
    public static readonly (string Value, string Display, string Description)[] Models =
    [
        (DefaultModelId, "GLM 5.3 Flash",
            "1M context, 128K output. Fast and cheap ($0.15/$0.50 per Mtok) - the default."),
        ("zai-org/GLM-5.2", "GLM 5.2",
            "1M context, 256K output. Stronger and slower than Flash ($1.40/$4.40 per Mtok)."),
        ("zai-org/GLM-5.2-Fast", "GLM 5.2 Fast",
            "GLM 5.2 on faster hardware, same 1M context ($2.10/$6.60 per Mtok)."),
        ("zai-org/GLM-4.7", "GLM 4.7",
            "Previous generation, 200K context ($0.60/$2.20 per Mtok)."),
    ];

    /// <summary>True when this provider id is ours, whatever casing it arrived in.</summary>
    public static bool Is(string? provider) =>
        string.Equals(provider, ProviderId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The model id to put on the wire. This provider has a fixed catalog, and another provider's id arriving here
    /// - which is exactly what a shared settings default used to do - has to become something Baseten can serve,
    /// or the turn dies on a 404.
    /// </summary>
    public static string NormalizeModel(string? model)
    {
        var value = model?.Trim();
        if (string.IsNullOrEmpty(value) || string.Equals(value, "default", StringComparison.OrdinalIgnoreCase))
            return DefaultModelId;
        foreach (var known in Models)
            if (string.Equals(known.Value, value, StringComparison.OrdinalIgnoreCase)) return known.Value;
        return DefaultModelId;
    }

    /// <summary>True when a model id belongs to this provider. Used to keep GLM ids out of Claude's slots.</summary>
    public static bool IsGlmModelId(string? id) =>
        !string.IsNullOrWhiteSpace(id)
        && (id.StartsWith("zai-org/", StringComparison.OrdinalIgnoreCase)
            || id.Contains("glm", StringComparison.OrdinalIgnoreCase));
}
