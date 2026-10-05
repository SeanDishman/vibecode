using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using VibeCode.UI;

namespace VibeCode.Services;

public sealed record BridgeOrchestratorGroup(string AgentId, int WorkerCount, string Objective, string? ExistingScope = null);

public sealed record BridgeOrchestratorAssignment(string AgentId, string TaskName, string Scope,
    string Exclusions, string Verification, string Handoffs)
{
    public string Instructions => $"Your responsibility: {Scope}\nOutside your scope: {Exclusions}\n" +
        $"Required checks: {Verification}\nDependencies and handoffs: {Handoffs}";
}

/// <summary>The short-lived planner's result survives; its provider process never joins the worker roster.</summary>
public sealed class BridgeCentralPlan
{
    public string State { get; set; } = "planning";
    public string Provider { get; set; } = "";
    public string? Model { get; set; }
    public string? Effort { get; set; }
    public string Error { get; set; } = "";
    public List<BridgeOrchestratorAssignment> Assignments { get; set; } = new();
    public HashSet<string> PublishedPlans { get; set; } = new();
    [JsonIgnore] public CancellationTokenSource? Cancellation { get; set; }
}

public static class BridgeCentralOrchestratorService
{
    private const string Instructions = """
        You are the temporary CENTRAL ORCHESTRATOR. You have no worker group and do not own any implementation.
        Read the user's complete objective, decompose it, and assign complementary responsibilities to the permanent orchestrators.
        Balance the useful work according to each group's worker count; with equal counts aim for roughly equal effort.
        Cover every requested outcome exactly once, including integration and final verification ownership. Do not give every group the whole objective.
        Separate scope ownership, not file access: groups can edit shared files while coordinating concrete dependencies.
        Preserve an existing_scope exactly when one is provided. Divide a new objective only among the groups that share it;
        coordinate it with existing scopes without taking their work. Give concrete boundaries, required checks and handoffs.
        Do not perform the task, inspect the workspace, call tools, spawn agents or communicate with any workers.
        Your only output is the handoff. After it is validated and delivered, your session is disposed and you have no further role.
        Return only JSON: {"assignments":[{"agent_id":"exact supplied ID","task_name":"two to five words",
        "scope":"specific responsibility, at most 1200 characters","exclusions":"work owned by other groups",
        "verification":"concrete checks this group owns","handoffs":"dependencies and who integrates the result"}]}.
        Include exactly one assignment per supplied group. Use distinct scopes and task names. Never invent agent IDs.
        """;

    /// <summary>How long the planner may go without any sign of life - start-up, thinking progress, text - before it
    /// counts as stalled. Every event resets it, so a planner that is still thinking is never cut off by this.</summary>
    internal static readonly TimeSpan InactivityLimit = TimeSpan.FromMinutes(5);

    /// <summary>The ceiling for a planner that keeps working without ever answering. It used to be a flat 5 minutes
    /// for everything, which cut off max-effort planners in the middle of their reasoning.</summary>
    internal static readonly TimeSpan WorkLimit = TimeSpan.FromMinutes(30);

    public static async Task<IReadOnlyList<BridgeOrchestratorAssignment>> DivideAsync(ChatViewModel host,
        BridgeAgentConfiguration configuration, IReadOnlyList<BridgeOrchestratorGroup> groups, CancellationToken cancellationToken,
        Action<bool>? waitingForLimit = null)
    {
        if (groups.Count < 2) throw new ArgumentException("Central orchestration needs at least two groups.", nameof(groups));
        cancellationToken.ThrowIfCancellationRequested();
        var accountId = BridgeTeamSuggestionService.PlanningAccountId(host, configuration.Provider);
        using var session = BridgeTeamSuggestionService.CreateSession(host, Instructions, configuration, dialogueOnly: true,
            outputSchema: CreateOutputSchema(), capturedAccountId: accountId);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var done = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        var chunks = new List<string>();
        var gate = new object();
        var usage = new BridgeSuggestionUsage(configuration.Provider, configuration.Model, host.Cwd);
        var planner = PlannerName(host, configuration);
        var progress = new CentralPlannerProgress();
        session.Initialized += () =>
        {
            progress.Ready();
            ready.TrySetResult();
        };
        session.Exited += (code, stderr) =>
        {
            var error = new CentralPlannerException(progress.DescribeExit(planner, code, stderr), "the planner's CLI exited");
            ready.TrySetException(error);
            done.TrySetException(error);
        };
        session.PermissionRequested += r => session.RespondPermission(r.RequestId,
            new JsonObject { ["behavior"] = "deny", ["message"] = "The central orchestrator only divides the task." }, r.ToolUseId);
        session.MessageReceived += node =>
        {
            progress.Observe(node);
            if (node["type"]?.ToString() == "assistant" && node["message"]?["content"] is JsonArray content)
                lock (gate)
                    foreach (var block in content.OfType<JsonObject>().Where(b => b["type"]?.ToString() == "text"))
                        chunks.Add(block["text"]?.ToString() ?? "");
            if (node["type"]?.ToString() != "result") return;
            usage.Record(node, session.SessionId, session.Models);
            done.TrySetResult(node.DeepClone());
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var waiting = false;
        var watchdog = WatchAsync(() => progress, () => waiting, timeout);
        try
        {
            await Task.Run(session.Start, timeout.Token);
            await ready.Task.WaitAsync(timeout.Token);
            session.SendUser(JsonValue.Create("[CENTRAL ORCHESTRATOR INPUT]\n" + JsonSerializer.Serialize(new
            {
                groups = groups.Select(g => new { agent_id = g.AgentId, worker_count = g.WorkerCount,
                    objective = g.Objective, existing_scope = g.ExistingScope })
            })));
            progress.Sent();
            var attempts = 0;
            JsonNode response;
            while (true)
            {
                response = await done.Task.WaitAsync(timeout.Token);
                if (!UsageLimitRecovery.IsLimitError(response) || !AppSettings.Current.ContinueAfterLimitResets) break;
                waiting = true;
                waitingForLimit?.Invoke(true);
                bool recovered;
                try { recovered = await UsageLimitRecovery.WaitForResetAsync(response, configuration.Provider, configuration.Model,
                    accountId, session, attempts++, timeout.Token); }
                finally { waiting = false; waitingForLimit?.Invoke(false); }
                if (!recovered) break;
                lock (gate) chunks.Clear();
                done = new(TaskCreationOptions.RunContinuationsAsynchronously);
                progress = new CentralPlannerProgress();
                progress.Ready(); progress.Sent();
                session.SendUser(JsonValue.Create("Continue the central assignment after the usage limit. Return the complete JSON handoff for the supplied groups."));
            }
            if (response["is_error"]?.ToString() == "true" || (response["subtype"]?.ToString() ?? "").Contains("error", StringComparison.OrdinalIgnoreCase))
                throw new CentralPlannerException(progress.DescribeError(planner, response), "the provider returned an error");
            var result = response["result"]?.ToString() ?? "";
            string? why = null;
            lock (gate)
            {
                foreach (var candidate in new[] { result, string.Concat(chunks) }.Concat(chunks.AsEnumerable().Reverse()))
                {
                    if (TryParse(candidate, groups, out var assignments, out var reason)) return assignments;
                    if (why is null || why == NoHandoffReason) why = reason;
                }
            }
            throw new CentralPlannerException($"{planner} returned assignments VibeCode could not use: {why}. " +
                "No worker tasks were started.", "its assignments were unusable");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CentralPlannerException(progress.DescribeTimeout(planner), progress.ShortTimeoutReason());
        }
        finally
        {
            timeout.Cancel();
            await watchdog.ConfigureAwait(false);
        }
    }

    /// <summary>Ends the planner when it has been silent for <see cref="InactivityLimit"/> or busy for
    /// <see cref="WorkLimit"/>; <see cref="CentralPlannerProgress.Limit"/> records which.</summary>
    private static async Task WatchAsync(Func<CentralPlannerProgress> progress, Func<bool> waiting, CancellationTokenSource timeout)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), timeout.Token).ConfigureAwait(false);
                if (waiting() || progress().CheckLimits(InactivityLimit, WorkLimit) is null) continue;
                timeout.Cancel();
                return;
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>"The central orchestrator (Claude Code · Opus 5.5 · max effort)" - which planner, so the advice in a
    /// failure (check that account, lower that effort) has something concrete to point at.</summary>
    private static string PlannerName(ChatViewModel host, BridgeAgentConfiguration configuration)
    {
        var model = string.IsNullOrWhiteSpace(configuration.Model) || configuration.Model == "default"
            ? "default model"
              : ProviderModelCatalog.For(configuration.Provider).FirstOrDefault(m => m.Value == configuration.Model || m.ResolvedModel == configuration.Model)
                  ?.ShortName ?? configuration.Model;
        var effort = string.IsNullOrWhiteSpace(configuration.Effort) ? "default effort" : configuration.Effort + " effort";
        return $"The central orchestrator ({ProviderModelCatalog.DisplayName(configuration.Provider)} · {model} · {effort})";
    }

    private static JsonObject CreateOutputSchema() => JsonNode.Parse("""
        {"type":"object","additionalProperties":false,"required":["assignments"],"properties":{"assignments":{
          "type":"array","items":{"type":"object","additionalProperties":false,
            "required":["agent_id","task_name","scope","exclusions","verification","handoffs"],
            "properties":{"agent_id":{"type":"string"},"task_name":{"type":"string"},"scope":{"type":"string"},
              "exclusions":{"type":"string"},"verification":{"type":"string"},"handoffs":{"type":"string"}}}}}}
        """)!.AsObject();

    public static bool TryParse(string text, IReadOnlyList<BridgeOrchestratorGroup> groups,
        out IReadOnlyList<BridgeOrchestratorAssignment> assignments) => TryParse(text, groups, out assignments, out _);

    private const string NoHandoffReason = "its answer contained no JSON handoff";

    /// <summary>As <see cref="TryParse(string, IReadOnlyList{BridgeOrchestratorGroup}, out IReadOnlyList{BridgeOrchestratorAssignment})"/>,
    /// with the first rule the handoff broke in words a user can act on.</summary>
    public static bool TryParse(string text, IReadOnlyList<BridgeOrchestratorGroup> groups,
        out IReadOnlyList<BridgeOrchestratorAssignment> assignments, out string reason)
    {
        assignments = Array.Empty<BridgeOrchestratorAssignment>();
        reason = "";
        if (groups.Count < 2 || groups.Select(g => g.AgentId).Distinct(StringComparer.Ordinal).Count() != groups.Count)
        {
            reason = "the team does not have two distinct orchestrators";
            return false;
        }
        var first = text.IndexOf('{');
        var last = text.LastIndexOf('}');
        if (first < 0 || last < first)
        {
            reason = NoHandoffReason;
            return false;
        }
        try
        {
            if (JsonNode.Parse(text[first..(last + 1)])?["assignments"] is not JsonArray rows)
            {
                reason = "its JSON had no \"assignments\" list";
                return false;
            }
            if (rows.Count != groups.Count)
            {
                reason = $"it returned {rows.Count} assignment(s) for {groups.Count} orchestrators";
                return false;
            }
            var result = new List<BridgeOrchestratorAssignment>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var scopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                string Read(string key) => row?[key] is JsonValue value && value.TryGetValue<string>(out var valueText) ? valueText.Trim() : "";
                var id = Read("agent_id");
                var group = groups.FirstOrDefault(g => g.AgentId == id);
                var name = Read("task_name");
                var scope = Read("scope");
                var exclusions = Read("exclusions");
                var verification = Read("verification");
                var handoffs = Read("handoffs");
                var normalizedScope = string.Join(" ", scope.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                reason =
                    group is null ? $"it named an orchestrator that is not on this team (\"{id}\")" :
                    !ids.Add(id) ? $"it gave {id} two assignments" :
                    !scopes.Add(normalizedScope) ? "it gave two orchestrators the same scope" :
                    !names.Add(name) ? $"it gave two orchestrators the same task name (\"{name}\")" :
                    name.Length > 60 || name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length is < 2 or > 5
                        ? $"the task name \"{name}\" is not two to five words" :
                    scope.Length is < 1 or > 1200 ? $"the scope for {id} is empty or longer than 1,200 characters" :
                    group.ExistingScope is null && scope == group.Objective.Trim()
                        ? $"it handed {id} the whole objective instead of a share of it" :
                    group.ExistingScope is { Length: > 0 } existing && scope != existing ? $"it rewrote {id}'s existing scope" :
                    new[] { exclusions, verification, handoffs }.Any(s => s.Length is < 1 or > 2000)
                        ? $"the exclusions, checks or handoffs for {id} are missing or longer than 2,000 characters" : "";
                if (reason.Length > 0) return false;
                result.Add(new(id, name, scope, exclusions, verification, handoffs));
            }
            assignments = result;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            reason = "its JSON was malformed";
            return false;
        }
    }
}

/// <summary>A central planning failure, with the full diagnosis for the transcript and a few words for the roster.</summary>
public sealed class CentralPlannerException(string message, string shortReason) : InvalidOperationException(message)
{
    public string ShortReason { get; } = shortReason;
}

/// <summary>
/// What the temporary central orchestrator did and when. A failure used to say only "timed out before finishing the
/// split", which reads the same whether the CLI never started, the account was rate limited and nothing came back,
/// or the model was still reasoning when a flat five-minute timer fired. Recording the timeline lets each of those be
/// told apart - and lets the watchdog end only a planner that has actually gone quiet.
/// </summary>
internal sealed class CentralPlannerProgress
{
    private readonly object _gate = new();
    private readonly Func<DateTime> _clock;

    public CentralPlannerProgress(Func<DateTime>? clock = null)
    {
        _clock = clock ?? (() => DateTime.UtcNow);
        StartedAt = LastActivityAt = _clock();
    }

    public DateTime StartedAt { get; }
    public DateTime? ReadyAt { get; private set; }
    public DateTime? SentAt { get; private set; }
    public DateTime? FirstOutputAt { get; private set; }
    public DateTime LastActivityAt { get; private set; }
    public long ThinkingTokens { get; private set; }
    public int TextCharacters { get; private set; }
    /// <summary>"inactive" or "work" once a limit has ended the planner.</summary>
    public string? Limit { get; private set; }

    public void Ready() { lock (_gate) ReadyAt ??= LastActivityAt = _clock(); }
    public void Sent() { lock (_gate) SentAt ??= LastActivityAt = _clock(); }

    /// <summary>Any event is a sign of life. After the task is sent, thinking progress and written text are output.</summary>
    public void Observe(JsonNode node)
    {
        lock (_gate)
        {
            var now = _clock();
            LastActivityAt = now;
            if (SentAt is null) return;   // start-up chatter (init, model catalogs) is not an answer yet
            var message = node["message"] as JsonObject;
            switch (node["type"]?.ToString())
            {
                case "assistant":
                    if (message?["content"] is JsonArray content)
                        foreach (var block in content.OfType<JsonObject>())
                            TextCharacters += (block["text"]?.ToString() ?? block["thinking"]?.ToString() ?? "").Length;
                    FirstOutputAt ??= now;
                    break;
                case "system" when node["subtype"]?.ToString() == "thinking_tokens":
                    if (node["estimated_tokens"] is JsonValue value && value.TryGetValue<long>(out var tokens))
                        ThinkingTokens = Math.Max(ThinkingTokens, tokens);
                    FirstOutputAt ??= now;
                    break;
                case "stream_event":
                    FirstOutputAt ??= now;
                    break;
            }
        }
    }

    /// <summary>Which limit has been reached, if any. Sticky: the first one to trip is the one reported.</summary>
    public string? CheckLimits(TimeSpan inactivity, TimeSpan work)
    {
        lock (_gate)
        {
            var now = _clock();
            Limit ??= now - StartedAt >= work ? "work" : now - LastActivityAt >= inactivity ? "inactive" : null;
            return Limit;
        }
    }

    public string DescribeTimeout(string planner)
    {
        lock (_gate)
        {
            var now = _clock();
            if (Limit == "work")
                return $"{planner} was still working after {Span(now - StartedAt)} ({Work()}; its last output was {Span(now - LastActivityAt)} ago) " +
                       "and was stopped before it returned the assignments. A large objective at a high thinking effort can take that long: " +
                       "lower the central orchestrator's effort or give the team a smaller objective.";
            if (ReadyAt is null)
                return $"{planner} never finished starting: its CLI sent no ready signal in {Span(now - StartedAt)}. " +
                       "Check that this account is signed in and that its CLI starts on its own.";
            if (SentAt is null || FirstOutputAt is null)
                return $"{planner} started{(SentAt is { } sent ? $" and received the task {Span(sent - StartedAt)} in" : "")}, then sent nothing back " +
                       $"for {Span(now - LastActivityAt)} - no thinking and no text. The account is most likely at a usage or rate limit, " +
                       "or the provider stalled. Check this account's usage.";
            return $"{planner} worked for {Span(LastActivityAt - SentAt.Value)} ({Work()}), then went silent for {Span(now - LastActivityAt)} " +
                   "without returning the assignments. The provider stalled in the middle of its answer.";
        }
    }

    public string ShortTimeoutReason()
    {
        lock (_gate)
            return Limit == "work" ? "the planner was still working at the 30-minute limit"
                : ReadyAt is null ? "the planner's CLI never started"
                : FirstOutputAt is null ? "no response from the provider"
                : "the planner stalled mid-answer";
    }

    public string DescribeExit(string planner, int code, string? stderr)
    {
        lock (_gate)
        {
            var tail = (stderr ?? "").Trim();
            if (tail.Length > 300) tail = "…" + tail[^300..];
            return $"{planner} exited (code {code}) {Span(_clock() - StartedAt)} in, before returning the assignments." +
                   (tail.Length > 0 ? $" Its last error output: {tail}" : " It printed no error output.");
        }
    }

    public string DescribeError(string planner, JsonNode result)
    {
        var detail = (result["result"]?.ToString() ?? "").Trim();
        if (detail.Length > 400) detail = detail[..400] + "…";
        return detail.Length > 0
            ? $"{planner} returned an error instead of the assignments: {detail}"
            : $"{planner} ended with \"{result["subtype"]}\" instead of the assignments. Check this account's usage limit.";
    }

    private string Work()
    {
        var parts = new List<string>();
        if (ThinkingTokens > 0) parts.Add($"about {ThinkingTokens:N0} thinking tokens");
        if (TextCharacters > 0) parts.Add($"{TextCharacters:N0} characters written");
        return parts.Count > 0 ? string.Join(" and ", parts) : "no visible output";
    }

    internal static string Span(TimeSpan span)
    {
        var seconds = (int)Math.Round(Math.Max(0, span.TotalSeconds));
        if (seconds < 90) return seconds == 1 ? "1 second" : $"{seconds} seconds";
        var minutes = seconds / 60;
        var rest = seconds % 60;
        return rest == 0 ? $"{minutes} min" : $"{minutes} min {rest} s";
    }
}
