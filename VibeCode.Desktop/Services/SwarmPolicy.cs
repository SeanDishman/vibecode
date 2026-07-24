namespace VibeCode.Services;

/// <summary>
/// Product-level guardrails for provider-native child agents. A Bridge peer is a separate root CLI session;
/// a swarm worker is a child owned by one of those roots. Keeping that distinction here prevents UI labels,
/// prompts, and provider launch limits from drifting into different definitions of "agent".
/// </summary>
public static class SwarmPolicy
{
    /// <summary>Conservative default: Codex itself defaults to six and nearby products generally ship four to ten.</summary>
    public const int DefaultMaxWorkers = 6;

    /// <summary>Absolute VibeCode ceiling, even if settings.json is edited by hand.</summary>
    public const int HardMaxWorkers = 16;

    public const int MinMaxWorkers = 2;

    public static bool SupportsProvider(string? provider) =>
        string.Equals(provider, "claude", StringComparison.OrdinalIgnoreCase)
        || string.Equals(provider, "codex", StringComparison.OrdinalIgnoreCase)
        || string.Equals(provider, "grok", StringComparison.OrdinalIgnoreCase);

    public static int ClampMaxWorkers(int value) => Math.Clamp(value, MinMaxWorkers, HardMaxWorkers);

    /// <summary>
    /// This directive is intentionally attached only to a user-selected swarm turn. Merely making native child-agent
    /// tools available must not cause every ordinary prompt to fan out and spend several times the expected tokens.
    /// </summary>
    public static string BuildTurnDirective(string provider, int maxWorkers, bool isBridge)
    {
        if (!SupportsProvider(provider)) return "";
        // A shared app budget can leave one final slot even though the user-facing ceiling slider starts at two.
        maxWorkers = Math.Clamp(maxWorkers, 1, HardMaxWorkers);
        var nativeName = provider.ToLowerInvariant() switch
        {
            "codex" => "Codex collaboration subagents",
            "grok" => "Grok subagents",
            _ => "Claude Agent subagents",
        };
        var bridgeRule = isBridge
            ? " Bridge peers are separate root sessions; do not count, message, or commandeer them as swarm workers."
            : "";

        return "[VIBECODE SWARM REQUEST]\n" +
               $"Use {nativeName} only if this task has genuinely independent work. Choose the smallest useful " +
               $"swarm, from 1 to at most {maxWorkers} child workers; using none is valid when decomposition would " +
               "add overhead. Keep the swarm flat: child workers must not spawn more agents. Give workers disjoint " +
               "scopes, avoid duplicate edits, wait for every child, then verify and integrate their results before " +
               $"answering. Never exceed {maxWorkers} concurrent or total child workers for this request.{bridgeRule}\n" +
               "[END VIBECODE SWARM REQUEST]";
    }

    public static string BridgeRuntimeRule(bool enabled, int maxWorkers) => enabled
        ? $"- Child swarms are opt-in inside Bridge. Spawn them only when a user message contains " +
          $"[VIBECODE SWARM REQUEST], and obey its flat {ClampMaxWorkers(maxWorkers)}-worker ceiling. Bridge peers " +
          "remain separate roots, not swarm children."
        : "- Do not spawn child/subagents while in Bridge. Bridge peers already provide parallel roots; the user can " +
          "explicitly enable per-pane swarms in VibeCode Settings.";

    public static string SessionRuntimeRule(bool enabled) => enabled
        ? "[VIBECODE CHILD-AGENT POLICY] Native child-agent tools are available, but do not spawn children for an " +
          "ordinary message. Use them only when that user turn contains [VIBECODE SWARM REQUEST], and obey that " +
          "request's flat worker ceiling."
        : "[VIBECODE CHILD-AGENT POLICY] Child agents are disabled for this session.";
}

/// <summary>
/// App-wide reservation of worst-case child concurrency. Provider processes cannot share an internal scheduler, so
/// VibeCode leases the maximum each explicit request is allowed to create and writes the granted value into that
/// turn's directive. This makes the advertised 16-worker ceiling true across normal chats and every Bridge pane.
/// </summary>
public static class SwarmBudget
{
    public const int AppWorkerCeiling = SwarmPolicy.HardMaxWorkers;
    private static readonly object Gate = new();
    private static readonly Dictionary<long, int> Reservations = new();
    private static long _nextId;

    public static event Action? CapacityChanged;

    public static int ReservedWorkers
    {
        get { lock (Gate) return Reservations.Values.Sum(); }
    }

    public static int AvailableWorkers => Math.Max(0, AppWorkerCeiling - ReservedWorkers);

    /// <summary>Returns null only when every app-wide slot is already leased. A final single slot is still useful.</summary>
    public static SwarmLease? TryAcquire(int requestedWorkers)
    {
        SwarmLease? lease;
        lock (Gate)
        {
            var available = AppWorkerCeiling - Reservations.Values.Sum();
            if (available <= 0) return null;
            var granted = Math.Min(SwarmPolicy.ClampMaxWorkers(requestedWorkers), available);
            var id = ++_nextId;
            Reservations[id] = granted;
            lease = new SwarmLease(id, granted);
        }
        NotifyCapacityChanged();
        return lease;
    }

    internal static void Release(long id)
    {
        var changed = false;
        lock (Gate) changed = Reservations.Remove(id);
        if (changed) NotifyCapacityChanged();
    }

    /// <summary>Regression-harness isolation; production never resets live leases.</summary>
    internal static void ResetForTests()
    {
        lock (Gate) Reservations.Clear();
        NotifyCapacityChanged();
    }

    private static void NotifyCapacityChanged()
    {
        foreach (var handler in CapacityChanged?.GetInvocationList().Cast<Action>() ?? Array.Empty<Action>())
            try { handler(); } catch { /* one closing window must not strand every other chat's lease */ }
    }
}

public sealed class SwarmLease : IDisposable
{
    private readonly long _id;
    private int _disposed;
    internal SwarmLease(long id, int grantedWorkers) { _id = id; GrantedWorkers = grantedWorkers; }
    public int GrantedWorkers { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) SwarmBudget.Release(_id);
    }
}
