using System.Text.Json.Nodes;

namespace VibeCode.Services;

public sealed partial class AgentMemoryService
{
    /// <summary>Save one memory the user approved in the Second Brain's "To review" list. Queued like any other write
    /// when the brain is offline; nothing when the extension is off.</summary>
    public Task RememberApprovedAsync(string cwd, string provider, string content, CancellationToken cancellationToken = default)
    {
        if (!MemoryEnabled || string.IsNullOrWhiteSpace(content)) return Task.CompletedTask;
        var project = ProjectKey(cwd);
        return PostDurablyAsync("/agentmemory/remember", new JsonObject
        {
            ["content"] = Redact(Trim(content, 4_000)),
            ["type"] = "fact",
            ["concepts"] = new JsonArray("vibecode", "reviewed", provider, ProjectName(project)),
            ["files"] = new JsonArray(),
            ["project"] = project,
        }, cancellationToken);
    }

    /// <summary>Retention for the brain itself: forget memories and sessions last updated before <paramref name="cutoff"/>.
    /// Only talks to a brain that is already on and online; it never starts the engine. Returns (memories, sessions).</summary>
    public async Task<(int Memories, int Sessions)> ForgetOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        if (!MemoryEnabled || !IsOnline || IsBusy) return (0, 0);
        using var access = AccessScope(cancellationToken);
        cancellationToken = access.Token;
        const int page = 200, maxPages = 10;
        var oldMemories = new List<string>();
        for (var offset = 0; offset < page * maxPages; offset += page)
        {
            var root = await GetJsonAsync($"/agentmemory/memories?latest=true&limit={page}&offset={offset}", TimeSpan.FromSeconds(15),
                cancellationToken).ConfigureAwait(false);
            if (root?["memories"] is not JsonArray memories || memories.Count == 0) break;
            oldMemories.AddRange(memories.OfType<JsonObject>()
                .Where(memory => DateOf(memory["updatedAt"] ?? memory["createdAt"]) is { } at && at < cutoff)
                .Select(memory => TextOf(memory["id"])).OfType<string>());
            if (memories.Count < page) break;
        }
        var sessions = await GetJsonAsync("/agentmemory/sessions", TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        var oldSessions = ReadSessions(sessions).Where(session => session.UpdatedAt is { } at && at < cutoff).Select(session => session.Id).ToList();
        var forgotten = (Memories: 0, Sessions: 0);
        foreach (var id in oldMemories.Distinct(StringComparer.Ordinal))
            if (await PostJsonAsync("/agentmemory/forget", new JsonObject { ["memoryId"] = id }, TimeSpan.FromSeconds(20), cancellationToken)
                    .ConfigureAwait(false) is not null) forgotten.Memories++;
        // As in ForgetEverythingAsync: forgetting a session drops its summary and every observation beneath it.
        foreach (var id in oldSessions)
        {
            if (await PostJsonAsync("/agentmemory/forget", new JsonObject { ["sessionId"] = id }, TimeSpan.FromSeconds(20), cancellationToken)
                    .ConfigureAwait(false) is null) continue;
            forgotten.Sessions++;
            _startedSessions.TryRemove(id, out _);   // a chat resumed later must start its session row again
        }
        return forgotten;
    }
}
