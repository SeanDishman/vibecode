namespace VibeCode.Services;

/// <summary>Uses the same persistent tombstones as Delete chat; never deletes a working directory or CLI files.</summary>
public static class ChatHistoryRetention
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    public static DateTimeOffset LastActivity(SessionEntry session, AppSettings settings)
    {
        var fileTime = new DateTimeOffset(session.LastModified).ToUniversalTime();
        if (session.LastAssistantAt is { } reply && reply > fileTime) fileTime = reply;
        var recorded = settings.ChatMetadata.GetValueOrDefault(ChatMetadata.Key(session.Provider, session.SessionId))?.LastActivityAt;
        return recorded is { } recent && recent > fileTime ? recent : fileTime;
    }

    public static DateTimeOffset LastReply(SessionEntry session, AppSettings settings)
    {
        var recorded = settings.ChatMetadata.GetValueOrDefault(ChatMetadata.Key(session.Provider, session.SessionId))?.LastAssistantAt;
        var transcript = session.LastAssistantAt;
        return recorded is { } reply && (transcript is null || reply >= transcript) ? reply
            : transcript ?? LastActivity(session, settings);
    }

    public static bool Expired(DateTimeOffset? lastActivity, DateTimeOffset now) =>
        lastActivity is { } timestamp && timestamp != DateTimeOffset.MinValue && timestamp < now - Retention;

    public static HashSet<string> Apply(AppSettings settings, IEnumerable<SessionEntry> sessions,
        DateTimeOffset now, IReadOnlySet<string>? protectedSessionIds = null)
    {
        var protectedIds = new HashSet<string>(protectedSessionIds ?? new HashSet<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var open in settings.OpenChats.Where(c => c.Metadata?.IsLocked == true || !string.IsNullOrWhiteSpace(c.Draft)))
            if (open.SessionId is { } id) protectedIds.Add(id);
        foreach (var bridge in settings.SavedBridges)
        {
            var members = bridge.Peers.Select(p => (p.SessionId, p.Provider ?? bridge.Provider, p.Draft))
                .Append((bridge.HostSessionId, bridge.Provider, (string?)null)).ToList();
            if (members.Any(p => p.Item1 is { } id && (protectedIds.Contains(id)
                    || settings.ChatMetadata.GetValueOrDefault(ChatMetadata.Key(p.Item2, id))?.IsLocked == true
                    || !string.IsNullOrWhiteSpace(p.Item3))))
                foreach (var member in members) if (member.Item1 is { } id) protectedIds.Add(id);
        }

        var activity = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        void Remember(string? id, string provider, DateTimeOffset? when)
        {
            if (id is null) return;
            var metadata = settings.ChatMetadata.GetValueOrDefault(ChatMetadata.Key(provider, id));
            if (metadata?.IsLocked == true) protectedIds.Add(id);
            if (metadata?.LastActivityAt is { } recorded && (when is null || recorded > when)) when = recorded;
            if (when is { } known && (!activity.TryGetValue(id, out var prior) || known > prior)) activity[id] = known;
        }
        foreach (var session in sessions) Remember(session.SessionId, session.Provider, LastActivity(session, settings));
        foreach (var chat in settings.OpenChats) Remember(chat.SessionId, chat.Provider, chat.Metadata?.LastActivityAt);
        foreach (var bridge in settings.SavedBridges)
        {
            // A saved roster is a fallback only: autosaves must not make an inactive conversation younger.
            if (!activity.ContainsKey(bridge.HostSessionId)) Remember(bridge.HostSessionId, bridge.Provider, new DateTimeOffset(bridge.SavedAt));
            foreach (var peer in bridge.Peers)
                if (peer.SessionId is { } id && !activity.ContainsKey(id)) Remember(id, peer.Provider ?? bridge.Provider, new DateTimeOffset(bridge.SavedAt));
        }
        var expired = activity.Where(pair => Expired(pair.Value, now) && !protectedIds.Contains(pair.Key))
            .Select(pair => pair.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Keep a saved bridge intact while any member has recent activity.
        foreach (var bridge in settings.SavedBridges)
        {
            var ids = bridge.Peers.Select(p => p.SessionId).Append(bridge.HostSessionId).OfType<string>().ToArray();
            if (ids.Any(id => !expired.Contains(id))) expired.ExceptWith(ids);
        }
        settings.DeletedSessions.UnionWith(expired);
        settings.OwnedSessions.ExceptWith(settings.DeletedSessions);
        settings.OpenChats.RemoveAll(c => c.SessionId is { } id && settings.DeletedSessions.Contains(id));
        foreach (var bridge in settings.SavedBridges.Where(b => expired.Contains(b.HostSessionId)).ToList())
            settings.RemoveSavedBridge(bridge.HostSessionId, bridge.Provider);
        foreach (var key in settings.ChatMetadata.Keys.Where(k => k.IndexOf(':') is var separator && separator >= 0
                     && expired.Contains(k[(separator + 1)..])).ToList())
            settings.ChatMetadata.Remove(key);
        settings.RecentDirectories.RemoveAll(d => Expired(d.LastUsed, now));
        return expired;
    }
}
