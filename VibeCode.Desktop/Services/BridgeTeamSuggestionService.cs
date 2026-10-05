using System.Text.Json.Nodes;
using VibeCode.Protocol;
using VibeCode.UI;

namespace VibeCode.Services;

public sealed record BridgeTeamSuggestion(int WorkerCount, string Reason);

/// <summary>A separate planning turn; never injects its answer into the user's running conversation.</summary>
public static class BridgeTeamSuggestionService
{
    private const string Instructions = "Suggest the smallest useful parallel team for the supplied task. " +
        "Count worker AIs only, excluding orchestrators. Honor the user's orchestrator count and minimum workers. Prefer 1 for a narrow change, 2-3 for independent implementation and verification, " +
        "and larger teams only when there are genuinely independent areas. Do not perform the task, call tools, edit files, or spawn agents. " +
        "Return only JSON with worker_count (integer) and reason (one short sentence, up to 300 characters).";

    public static async Task<BridgeTeamSuggestion> SuggestAsync(ChatViewModel host, string objective, int maximum,
        CancellationToken cancellationToken = default, int orchestratorCount = 1,
        BridgeAgentConfiguration? configuration = null, Action<bool>? waitingForLimit = null)
    {
        if (string.IsNullOrWhiteSpace(objective))
            throw new ArgumentException("Type or dictate a task before asking AI to size the team.", nameof(objective));
        if (maximum < 1) throw new ArgumentOutOfRangeException(nameof(maximum), "The bridge has no room for a worker.");
        if (orchestratorCount < 1 || orchestratorCount > maximum)
            throw new ArgumentOutOfRangeException(nameof(orchestratorCount), "Every orchestrator needs at least one worker.");
        cancellationToken.ThrowIfCancellationRequested();
        configuration ??= BridgeAgentConfigurationPolicy.From(host);
        var accountId = PlanningAccountId(host, configuration.Provider);
        using var session = CreateSession(host, configuration: configuration, capturedAccountId: accountId);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var done = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        var candidates = new List<string>();
        var gate = new object();
        var usage = new BridgeSuggestionUsage(configuration.Provider, configuration.Model, host.Cwd);
        session.Initialized += () => ready.TrySetResult();
        session.Exited += (_, _) =>
        {
            var error = new InvalidOperationException("The planning session closed before returning a count.");
            ready.TrySetException(error);
            done.TrySetException(error);
        };
        session.PermissionRequested += r => session.RespondPermission(r.RequestId,
            new JsonObject { ["behavior"] = "deny", ["message"] = "Team-size planning cannot execute tools." }, r.ToolUseId);
        session.MessageReceived += node =>
        {
            if (node["type"]?.ToString() == "assistant" && node["message"]?["content"] is JsonArray content)
                lock (gate)
                    foreach (var block in content.OfType<JsonObject>().Where(b => b["type"]?.ToString() == "text"))
                        candidates.Add(block["text"]?.ToString() ?? "");
            if (node["type"]?.ToString() != "result") return;
            usage.Record(node, session.SessionId, session.Models);
            done.TrySetResult(node.DeepClone());
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            await Task.Run(session.Start, timeout.Token);
            await ready.Task.WaitAsync(timeout.Token);
            session.SendUser(JsonValue.Create($"The user selected {orchestratorCount} orchestrator(s). Suggest between {orchestratorCount} and {maximum} total worker AIs, " +
                $"split among these orchestrators with at least one worker in each group, for this task:\n\n{objective}"));
            var attempts = 0;
            JsonNode response;
            while (true)
            {
                response = await done.Task.WaitAsync(timeout.Token);
                if (!UsageLimitRecovery.IsLimitError(response) || !AppSettings.Current.ContinueAfterLimitResets) break;
                timeout.CancelAfter(Timeout.InfiniteTimeSpan);
                waitingForLimit?.Invoke(true);
                bool recovered;
                try { recovered = await UsageLimitRecovery.WaitForResetAsync(response, configuration.Provider, configuration.Model,
                    accountId, session, attempts++, timeout.Token); }
                finally { waitingForLimit?.Invoke(false); }
                if (!recovered) break;
                lock (gate) candidates.Clear();
                done = new(TaskCreationOptions.RunContinuationsAsynchronously);
                timeout.CancelAfter(TimeSpan.FromMinutes(2));
                session.SendUser(JsonValue.Create("Continue after the usage limit and return the JSON team-size suggestion for the task above."));
            }
            if (response["is_error"]?.ToString() == "true" || (response["subtype"]?.ToString() ?? "").Contains("error", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The AI could not suggest a count. " + response["result"]);
            var result = response["result"]?.ToString() ?? "";
            lock (gate)
            {
                foreach (var candidate in new[] { result }.Concat(candidates.AsEnumerable().Reverse()))
                    if (TryParse(candidate, maximum, out var suggestion) && suggestion!.WorkerCount >= orchestratorCount) return suggestion;
            }
            throw new InvalidOperationException("The AI returned an invalid team size.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The count suggestion timed out.");
        }
    }

    public static bool TryParse(string text, int maximum, out BridgeTeamSuggestion? suggestion)
    {
        suggestion = null;
        var first = text.IndexOf('{');
        var last = text.LastIndexOf('}');
        if (first < 0 || last < first) return false;
        try
        {
            var json = JsonNode.Parse(text[first..(last + 1)]);
            if (json?["worker_count"] is not JsonValue value || !value.TryGetValue<int>(out var count)
                || count < 1 || count > maximum || json["reason"] is not JsonValue reasonValue
                || !reasonValue.TryGetValue<string>(out var reason) || string.IsNullOrWhiteSpace(reason) || reason.Length > 300) return false;
            suggestion = new(count, reason.Trim());
            return true;
        }
        catch (System.Text.Json.JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    internal static ICodingSession CreateSession(ChatViewModel host, string? instructions = null,
        BridgeAgentConfiguration? configuration = null, bool dialogueOnly = false, JsonObject? outputSchema = null,
        string? capturedAccountId = null)
    {
        configuration = BridgeAgentConfigurationPolicy.Normalize(configuration ?? BridgeAgentConfigurationPolicy.From(host));
        var accountId = capturedAccountId ?? PlanningAccountId(host, configuration.Provider);
        var glmAccount = accountId is null ? ApiKeyAccountService.Instance.SelectedFor(GlmPreset.ProviderId)
            : ApiKeyAccountService.Instance.For(GlmPreset.ProviderId).FirstOrDefault(a => a.Id == accountId);
        var glmCredentials = configuration.Provider == "glm"
            ? ApiKeyAccountService.Instance.CredentialsFor(GlmPreset.ProviderId, accountId) : [];
        var model = string.Equals(configuration.Model, "default", StringComparison.OrdinalIgnoreCase) ? null : configuration.Model;
        var effort = configuration.Effort;
        instructions ??= Instructions;
        return configuration.Provider switch
    {
        "codex" => new CodexSession(new CodexSessionOptions
        {
            Cwd = host.Cwd, HomeDirectory = CodexAccountService.Instance.HomeFor(accountId), Model = model,
            Effort = effort, PermissionMode = "plan", AppendSystemPrompt = instructions, McpServers = [], DialogueOnly = dialogueOnly,
            DialogueOutputSchema = outputSchema, DialogueInstructions = outputSchema is null ? null : "Return only the JSON handoff matching the supplied output schema. Do not execute the task.",
        }),
        "claude" => new ClaudeSession(new ClaudeSessionOptions
        {
            Cwd = host.Cwd, ConfigDirectory = AccountService.Instance.ConfigDirectory(accountId), Model = model,
            Effort = effort, PermissionMode = "plan", AppendSystemPrompt = instructions, McpServers = [], DialogueOnly = dialogueOnly,
        }),
        "kimi" => new KimiSession(new KimiSessionOptions
        {
            Cwd = host.Cwd, Model = model, Effort = effort,
            PermissionMode = "plan", AppendSystemPrompt = instructions, McpServers = [],
        }),
        "grok" => new GrokSession(new GrokSessionOptions
        {
            Cwd = host.Cwd, AuthFilePath = GrokAccountService.Instance.AuthPathFor(accountId), Model = model,
            Effort = effort, PermissionMode = "plan", AppendSystemPrompt = instructions, McpServers = [],
        }),
        "glm" => new GlmSession(new GlmSessionOptions
        {
            Cwd = host.Cwd, Backend = glmAccount?.GlmBackend ?? GlmPreset.Baseten,
            ApiKeys = glmCredentials.Select(c => c.Key).ToList(), ApiKeyAccountIds = glmCredentials.Select(c => c.AccountId).ToList(),
            Model = model, Effort = effort, PermissionMode = "plan", AppendSystemPrompt = instructions,
        }),
        _ => throw new InvalidOperationException("This provider cannot suggest a team size."),
        };
    }

    internal static string? PlanningAccountId(ChatViewModel host, string provider) => provider == host.Provider ? host.AccountId : provider switch
    {
        "claude" => AccountService.Instance.ActiveId,
        "codex" => CodexAccountService.Instance.ActiveId,
        "grok" => GrokAccountService.Instance.ActiveId,
        "glm" => ApiKeyAccountService.Instance.SelectedFor(GlmPreset.ProviderId)?.Id,
        _ => null,
    };
}
