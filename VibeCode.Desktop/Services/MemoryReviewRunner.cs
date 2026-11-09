using System.IO;
using System.Text.Json.Nodes;
using VibeCode.Protocol;
using VibeCode.UI;

namespace VibeCode.Services;

/// <summary>
/// The production review runner: one isolated, tools-less session through the same signed-in adapters Jarvis uses, on
/// the reviewed chat's own provider and account, with the cheapest model that provider lists. Its tokens are recorded
/// like any other planner turn, so they appear in the usage dashboard. Never run by the tests.
/// </summary>
internal sealed class SubscriptionMemoryReviewRunner : IMemoryReviewRunner
{
    /// <summary>Providers whose adapters can drop every native tool (DialogueOnly).</summary>
    public static bool Supports(string provider) => provider is "claude" or "codex" or GlmPreset.ProviderId;

    public async Task<string> RunAsync(MemoryReviewRequest request, string instructions, string prompt,
        CancellationToken cancellationToken)
    {
        if (!Supports(request.Provider))
            throw new NotSupportedException($"{ProviderModelCatalog.DisplayName(request.Provider)} cannot run a tools-less review.");
        // An empty folder of its own, like Jarvis: the review never opens the project or its instructions files.
        var workspace = Path.Combine(MemoryContextFiles.Root, "review-workspace");
        Directory.CreateDirectory(workspace);
        var model = CheapestModel(request.Provider) ?? request.Model;
        using var session = CreateSession(request.Provider, request.AccountId, model, instructions, workspace);
        using var usage = new BridgeSuggestionUsage(request.Provider, model, request.Cwd);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var done = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        var texts = new List<string>();
        session.Initialized += () => ready.TrySetResult();
        session.Exited += (_, _) =>
        {
            var error = new InvalidOperationException("The review session closed before answering.");
            ready.TrySetException(error);
            done.TrySetException(error);
        };
        session.PermissionRequested += permission => session.RespondPermission(permission.RequestId,
            new JsonObject { ["behavior"] = "deny", ["message"] = "Memory review cannot use tools." }, permission.ToolUseId);
        session.MessageReceived += node =>
        {
            usage.Observe(node, session.SessionId, session.Models);
            if (node["type"]?.ToString() == "assistant" && node["message"]?["content"] is JsonArray content)
                lock (texts)
                    texts.AddRange(content.OfType<JsonObject>().Where(b => b["type"]?.ToString() == "text").Select(b => b["text"]?.ToString() ?? ""));
            if (node["type"]?.ToString() != "result") return;
            usage.Record(node, session.SessionId, session.Models);
            done.TrySetResult(node.DeepClone());
        };
        try
        {
            await Task.Run(session.Start, cancellationToken).ConfigureAwait(false);
            await ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            session.SendUser(JsonValue.Create(prompt));
            var result = await done.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (result["is_error"]?.ToString() == "true" || (result["subtype"]?.ToString() ?? "").Contains("error", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The review model returned an error: " + result["result"]);
            var answer = result["result"]?.ToString() ?? "";
            if (answer.Contains('{')) return answer;
            lock (texts) return texts.LastOrDefault(text => text.Contains('{')) ?? answer;
        }
        finally
        {
            if (!done.Task.IsCompleted)
                try { await session.InterruptAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        }
    }

    /// <summary>The lowest list-priced model the provider currently offers, or null to keep the chat's own.</summary>
    internal static string? CheapestModel(string provider) => ProviderModelCatalog.For(provider)
        .Where(m => !string.Equals(m.Value, "default", StringComparison.OrdinalIgnoreCase)
                    && ModelPricing.IsPriced(m.ResolvedModel ?? m.Value))
        .OrderBy(m => ModelPricing.For(m.ResolvedModel ?? m.Value) is var price ? price.InputPerMTok + price.OutputPerMTok : 0)
        .Select(m => m.Value)
        .FirstOrDefault();

    private static ICodingSession CreateSession(string provider, string? accountId, string? model, string instructions, string workspace)
    {
        model = string.Equals(model, "default", StringComparison.OrdinalIgnoreCase) ? null : model;
        return provider switch
        {
            "codex" => new CodexSession(new CodexSessionOptions
            {
                AccountId = accountId ?? "", Cwd = workspace, HomeDirectory = CodexAccountService.Instance.HomeFor(accountId),
                Model = model, PermissionMode = "plan", AppendSystemPrompt = instructions, McpServers = [], DialogueOnly = true,
            }),
            "claude" => new ClaudeSession(new ClaudeSessionOptions
            {
                AccountId = accountId ?? "", Cwd = workspace, ConfigDirectory = AccountService.Instance.ConfigDirectory(accountId),
                Model = model, PermissionMode = "plan", AppendSystemPrompt = instructions, McpServers = [], DialogueOnly = true,
                NativeMemoryDisabled = true,
            }),
            _ => new GlmSession(new GlmSessionOptions
            {
                Cwd = workspace, Backend = ApiKeyAccountService.Instance.SelectedFor(GlmPreset.ProviderId)?.GlmBackend ?? GlmPreset.Baseten,
                ApiKeys = ApiKeyAccountService.Instance.CredentialsFor(GlmPreset.ProviderId, accountId).Select(c => c.Key).ToList(),
                Model = model, PermissionMode = "plan", AppendSystemPrompt = instructions, DialogueOnly = true,
            }),
        };
    }
}
