using System.Globalization;
using System.IO;
using System.Text.Json;
using VibeCode.Services;

namespace VibeCode.UI;

/// <summary>One cosmetic upgrade a project has earned from its own history, and the local date it was earned.</summary>
public sealed record TownImprovement(string Id, string Title, string Reason, DateTime EarnedLocal);

/// <summary>
/// Town improvements: quiet, cosmetic upgrades a project's building (and its office room) earns from real milestones -
/// window flower boxes after 100 turns, a string of lanterns after a million tokens, a weathervane once a Bridge has
/// run there, a stone plaque after 1,000 lines changed by Bridge agents, a banner once five agents worked there at once.
/// No badges, streaks, scores or notifications: a long-lived project simply looks more like home.
///
/// Everything is derived from history VibeCode already keeps - the usage log (every turn, 400 days) and each project's
/// Bridge edit ledger - plus what the wall itself sees (a Bridge on the wall, five agents in one project). Reading that
/// history is not free, so it happens off the UI thread, at most every few minutes or when a new project appears, and
/// the views bake the result into their cached scenery; nothing is computed per frame. The edit ledger only keeps a
/// week, so each earned date is remembered in town-improvements.json beside the usage log: an upgrade, once earned,
/// stays, and the remembered date is the first one ever seen.
/// </summary>
public sealed class WorkflowTown
{
    public static WorkflowTown Instance { get; } = new();
    public static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(4);
    public const string Flowers = "flowers", Lanterns = "lanterns", Weathervane = "weathervane", Plaque = "plaque", Banner = "banner";
    public const int FlowerTurns = 100, PlaqueLines = 1_000, BannerAgents = 5;
    public const double LanternTokens = 1_000_000;
    private static readonly TimeSpan CrowdWindow = TimeSpan.FromMinutes(10);

    private static readonly (string Id, string Title, string Reason)[] Kinds =
    [
        (Flowers, "Window flower boxes", "100 turns"), (Lanterns, "String of lanterns", "1M tokens"),
        (Weathervane, "Weathervane", "First Bridge run"), (Plaque, "Stone plaque", "1,000 lines changed"),
        (Banner, "Banner", "5 agents at once"),
    ];

    /// <summary>
    /// The pixel office starts shabby and is furnished by the work done in it. A project's room (walls, floor, lights,
    /// lounge) follows every token its directory has used; each agent's own desk, computer and chair follow its own
    /// chat's tokens, so a new chat's agents start at junk desks even in a well-kept room. Thresholds sit about five
    /// times apart, from a year of real use: half of all chats pass 8M tokens and half of all projects 60M.
    /// With town improvements off, every room and desk keeps the standard look (<see cref="StandardTier"/>).
    /// </summary>
    public static readonly double[] RoomTierTokens = [0, 25e6, 150e6, 750e6, 3e9];
    public static readonly double[] DeskTierTokens = [0, 2e6, 10e6, 50e6, 200e6];
    public const int StandardTier = 2, TopTier = 4;
    private static readonly string[] RoomTierNames = ["Bare walls", "Getting by", "Furnished", "Comfortable", "Penthouse"];
    private static readonly string[] DeskTierNames = ["Junk", "Hand-me-down", "Standard", "Pro", "Battlestation"];

    // Not a shared static empty map: Instance is created before any static declared below it is initialized.
    private volatile IReadOnlyDictionary<string, IReadOnlyList<TownImprovement>> _byProject =
        new Dictionary<string, IReadOnlyList<TownImprovement>>(StringComparer.OrdinalIgnoreCase);
    private volatile IReadOnlyDictionary<string, double> _projectTokens = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
    private volatile IReadOnlyDictionary<string, double> _sessionTokens = new Dictionary<string, double>(StringComparer.Ordinal);
    // Sessions a finished refresh has counted, replaced whole so the UI thread can read it while one runs.
    private volatile IReadOnlySet<string> _measured = new HashSet<string>(StringComparer.Ordinal);
    private readonly HashSet<string> _trackedSessions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _tracked = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, DateTime>> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _memoGate = new();
    private DateTime _refreshedUtc = DateTime.MinValue;
    private volatile bool _refreshing;
    private int _version;

    public static string MemoPath => Path.Combine(AppSettings.Dir, "town-improvements.json");
    public static bool Enabled => AppSettings.Current.WorkflowTownImprovements;
    /// <summary>Bumped whenever a refresh lands, so a view knows to rebuild its cached scenery.</summary>
    public int Version => Volatile.Read(ref _version);
    /// <summary>Part of a view's static cache key: changes when the improvements or the toggle change.</summary>
    public int Stamp => Enabled ? Version * 2 + 1 : 0;

    /// <summary>The improvements a project has earned, in a fixed order. Empty while the toggle is off. Called every
    /// frame, so it takes the project's key as the wall already has it (normalized, see <see cref="Key"/>) and only
    /// looks it up.</summary>
    public IReadOnlyList<TownImprovement> For(string projectKey) =>
        Enabled && _byProject.TryGetValue(projectKey, out var list) ? list : [];

    /// <summary>Every token a project's directory has used, from the usage log as of the last refresh.</summary>
    public double ProjectTokens(string projectKey) => _projectTokens.GetValueOrDefault(Key(projectKey));
    /// <summary>Every token one chat session has used, as of the last refresh.</summary>
    public double SessionTokens(string? sessionId) =>
        sessionId is { Length: > 0 } && _sessionTokens.TryGetValue(sessionId, out var tokens) ? tokens : 0;
    /// <summary>How furnished a project's office room is, 0 (bare walls) to <see cref="TopTier"/>. The shared room of
    /// chats without a directory is no project of its own and keeps the standard look.</summary>
    public int RoomTier(string projectKey) =>
        Enabled && projectKey.Length > 0 ? Tier(ProjectTokens(projectKey), RoomTierTokens) : StandardTier;
    /// <summary>How good one agent's desk, computer and chair are, 0 (junk) to <see cref="TopTier"/>.</summary>
    public int DeskTier(string? sessionId) => Enabled ? Tier(SessionTokens(sessionId), DeskTierTokens) : StandardTier;
    /// <summary>Whether a refresh has counted this chat's tokens yet. Until one has, its desk tier is a placeholder, so the
    /// first count is a measurement and not an upgrade.</summary>
    public bool Measured(string? sessionId) => sessionId is { Length: > 0 } && _measured.Contains(sessionId);

    public static int Tier(double tokens, IReadOnlyList<double> steps)
    {
        var tier = 0;
        for (var i = 1; i < steps.Count; i++) if (tokens >= steps[i]) tier = i;
        return tier;
    }

    public static string RoomTierName(int tier) => RoomTierNames[Math.Clamp(tier, 0, TopTier)];
    public static string DeskTierName(int tier) => DeskTierNames[Math.Clamp(tier, 0, TopTier)];

    /// <summary>"Comfortable · 812M tokens · next look at 3B": where a room or desk stands and what its next look takes.</summary>
    public static string Progress(string name, double tokens, IReadOnlyList<double> steps)
    {
        var tier = Tier(tokens, steps);
        return tier >= steps.Count - 1 ? $"{name} · {Short(tokens)} tokens · fully upgraded"
            : $"{name} · {Short(tokens)} tokens · next look at {Short(steps[tier + 1])}";
    }

    /// <summary>"450k", "8.2M", "3B".</summary>
    public static string Short(double tokens) => tokens switch
    {
        >= 1e9 => (tokens / 1e9).ToString(tokens < 1e10 ? "0.#" : "0", CultureInfo.InvariantCulture) + "B",
        >= 1e6 => (tokens / 1e6).ToString(tokens < 1e7 ? "0.#" : "0", CultureInfo.InvariantCulture) + "M",
        >= 1e3 => (tokens / 1e3).ToString("0", CultureInfo.InvariantCulture) + "k",
        _ => tokens.ToString("0", CultureInfo.InvariantCulture),
    };

    /// <summary>Hand the town the wall's newest snapshot (UI thread). Notes what the wall itself sees - a Bridge in a
    /// project, five agents there at once - and starts a background refresh when a new project appears or the last one
    /// is a few minutes old. Never blocks.</summary>
    public void Track(IReadOnlyList<AgentActivityProject> projects, DateTime nowUtc)
    {
        if (!Enabled) return;
        var changed = false;
        foreach (var project in projects)
        {
            // A chat that appears (or comes back) on the wall gets its desk from its own history at once.
            foreach (var agent in project.Agents)
                if (agent.Chat.SessionId is { Length: > 0 } session) changed |= _trackedSessions.Add(session);
            if (project.Directory.Length == 0) continue;
            var key = Key(project.Key);
            changed |= _tracked.Add(key);
            if (project.Agents.Any(a => a.Chat.IsBridgeAgent || a.IsCentralPlanner)) changed |= See(key, Weathervane, nowUtc);
            if (project.Agents.Count >= BannerAgents) changed |= See(key, Banner, nowUtc);
        }
        if (_refreshing || !changed && nowUtc - _refreshedUtc < RefreshEvery) return;
        _refreshing = true;
        _refreshedUtc = nowUtc;
        var keys = _tracked.ToArray();
        var sessions = _trackedSessions.ToArray();
        var seen = _seen.ToDictionary(p => p.Key, p => (IReadOnlyDictionary<string, DateTime>)new Dictionary<string, DateTime>(p.Value), StringComparer.OrdinalIgnoreCase);
        _ = Task.Run(() =>
        {
            try
            {
                var (fresh, projectTokens, sessionTokens) = Compute(keys, seen, DateTimeOffset.Now);
                // Token counts always move; the views rebuild their scenery when the version moves, so it only moves
                // when an improvement was earned or a room or desk reached its next look.
                var furnished = keys.Any(k => Tier(projectTokens.GetValueOrDefault(k), RoomTierTokens) != Tier(_projectTokens.GetValueOrDefault(k), RoomTierTokens))
                    || sessions.Any(s => Tier(sessionTokens.GetValueOrDefault(s), DeskTierTokens) != Tier(_sessionTokens.GetValueOrDefault(s), DeskTierTokens));
                _projectTokens = projectTokens;
                _sessionTokens = sessionTokens;
                _measured = new HashSet<string>(_measured.Concat(sessions), StringComparer.Ordinal);
                if (Same(fresh, _byProject) && !furnished) return;
                _byProject = fresh;
                Interlocked.Increment(ref _version);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { /* cosmetic: try again next time */ }
            finally { _refreshing = false; }
        });
    }

    private static bool Same(IReadOnlyDictionary<string, IReadOnlyList<TownImprovement>> a, IReadOnlyDictionary<string, IReadOnlyList<TownImprovement>> b) =>
        a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var other) && p.Value.SequenceEqual(other));

    private bool See(string key, string id, DateTime nowUtc)
    {
        if (!_seen.TryGetValue(key, out var ids)) _seen[key] = ids = new(StringComparer.Ordinal);
        return ids.TryAdd(id, nowUtc.ToLocalTime());
    }

    private (IReadOnlyDictionary<string, IReadOnlyList<TownImprovement>> Improvements, Dictionary<string, double> ProjectTokens,
        Dictionary<string, double> SessionTokens) Compute(string[] keys,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, DateTime>> seen, DateTimeOffset now)
    {
        var wanted = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
        var usage = new Dictionary<string, List<UsageEntry>>(StringComparer.OrdinalIgnoreCase);
        var (projectTokens, sessionTokens) = TokenTotals(UsageLog.Instance.Entries());
        // A year of turns names only a handful of directories; each is normalized once, not once per turn.
        var keyOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in UsageLog.Instance.Entries())
        {
            if (entry.Project is not { Length: > 0 } project) continue;
            if (!keyOf.TryGetValue(project, out var key)) keyOf[project] = key = Key(project);
            if (!wanted.Contains(key)) continue;
            if (!usage.TryGetValue(key, out var rows)) usage[key] = rows = new();
            rows.Add(entry);
        }
        lock (_memoGate)
        {
            var memo = LoadMemo();
            var changed = false;
            foreach (var key in keys)
            {
                var earned = Derive(usage.GetValueOrDefault(key) ?? [], BridgeEditLedger.Read(key, now), seen.GetValueOrDefault(key));
                if (!memo.TryGetValue(key, out var remembered)) memo[key] = remembered = new(StringComparer.Ordinal);
                foreach (var (id, at) in earned)
                    if (!remembered.TryGetValue(id, out var known) || at < known) { remembered[id] = at; changed = true; }
            }
            if (changed) SaveMemo(memo);
            return (memo.Where(p => wanted.Contains(p.Key)).ToDictionary(p => p.Key, p => List(p.Value), StringComparer.OrdinalIgnoreCase),
                projectTokens, sessionTokens);
        }
    }

    /// <summary>Every token each project directory (normalized, see <see cref="Key"/>) and each chat session has used.
    /// Pure, for the tests.</summary>
    public static (Dictionary<string, double> Projects, Dictionary<string, double> Sessions) TokenTotals(IEnumerable<UsageEntry> usage)
    {
        var projects = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var sessions = new Dictionary<string, double>(StringComparer.Ordinal);
        var keyOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in usage)
        {
            var total = entry.Total;
            if (entry.SessionId is { Length: > 0 } session) sessions[session] = sessions.GetValueOrDefault(session) + total;
            if (entry.Project is not { Length: > 0 } project) continue;
            if (!keyOf.TryGetValue(project, out var key)) keyOf[project] = key = Key(project);
            projects[key] = projects.GetValueOrDefault(key) + total;
        }
        return (projects, sessions);
    }

    /// <summary>The improvements, in display order, from a map of id to earned date.</summary>
    public static IReadOnlyList<TownImprovement> List(IReadOnlyDictionary<string, DateTime> earned) =>
        Kinds.Where(k => earned.ContainsKey(k.Id)).Select(k => new TownImprovement(k.Id, k.Title, k.Reason, earned[k.Id])).ToArray();

    /// <summary>What a project has earned from its turns (<paramref name="usage"/>), its Bridge edits
    /// (<paramref name="edits"/>) and what the wall saw (<paramref name="seen"/>), each with the local date it was
    /// first true. Pure, for the tests.</summary>
    public static IReadOnlyDictionary<string, DateTime> Derive(IEnumerable<UsageEntry> usage, IEnumerable<BridgeEditEntry> edits,
        IReadOnlyDictionary<string, DateTime>? seen = null)
    {
        var earned = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        var turns = 0;
        var tokens = 0.0;
        // Five different sessions with a turn inside ten minutes is five agents working there at once.
        var window = new Queue<(DateTimeOffset At, string Session)>();
        var sessions = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in usage.OrderBy(e => e.At))
        {
            var at = entry.At.LocalDateTime;
            if (++turns == FlowerTurns) earned[Flowers] = at;
            tokens += entry.Total;
            if (tokens >= LanternTokens) earned.TryAdd(Lanterns, at);
            if (earned.ContainsKey(Banner) || entry.SessionId is not { Length: > 0 } session) continue;
            window.Enqueue((entry.At, session));
            sessions[session] = sessions.GetValueOrDefault(session) + 1;
            while (window.Peek().At < entry.At - CrowdWindow)
            {
                var old = window.Dequeue();
                if (--sessions[old.Session] == 0) sessions.Remove(old.Session);
            }
            if (sessions.Count >= BannerAgents) earned[Banner] = at;
        }
        var lines = 0;
        foreach (var edit in edits.OrderBy(e => e.At))
        {
            var at = edit.At.LocalDateTime;
            earned.TryAdd(Weathervane, at);
            lines += edit.Added + edit.Removed;
            if (lines >= PlaqueLines) earned.TryAdd(Plaque, at);
        }
        foreach (var (id, at) in seen ?? new Dictionary<string, DateTime>())
            if (!earned.TryGetValue(id, out var known) || at < known) earned[id] = at;
        return earned;
    }

    private static Dictionary<string, Dictionary<string, DateTime>> LoadMemo()
    {
        var memo = new Dictionary<string, Dictionary<string, DateTime>>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(MemoPath)) return memo;
        // An unreadable memo only costs remembered dates; the history rebuilds what it still covers.
        try
        {
            if (JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, DateTime>>>(File.ReadAllText(MemoPath)) is { } stored)
                foreach (var (key, ids) in stored) memo[Key(key)] = new(ids, StringComparer.Ordinal);
        }
        catch (JsonException) { }
        return memo;
    }

    private static void SaveMemo(Dictionary<string, Dictionary<string, DateTime>> memo)
    {
        Directory.CreateDirectory(AppSettings.Dir);
        var temp = MemoPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(memo, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, MemoPath, overwrite: true);
    }

    /// <summary>A project directory as the wall keys it: full path, no trailing separator.</summary>
    public static string Key(string path)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return path.TrimEnd('\\', '/'); }
    }
}
