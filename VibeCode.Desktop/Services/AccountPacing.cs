using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using VibeCode.UI;

namespace VibeCode.Services;

/// <summary>
/// One usage meter per provider account, fed by every Claude pane's rate_limit_event: how much of a limit window the
/// account has used, whether the CLI already warns or refuses, and when that window resets. Past the pacing threshold a
/// Bridge gives that account no new assignments and the robot summons newcomers on another usable account, while agents
/// already working finish normally. Before this, every pane on one account ran into the limit together, mid-task.
/// Which fields arrive depends on the CLI version and the login type, so each one is optional: without a utilization the
/// status decides, and a reading without a reset time is forgotten after a while instead of holding work forever.
/// </summary>
public sealed class AccountPacing
{
    /// <summary>How long a reading without a reset time keeps holding work; a newer event refreshes it.</summary>
    public static readonly TimeSpan UndatedReadingLifetime = TimeSpan.FromMinutes(30);
    public static AccountPacing Instance { get; } = new(() => DateTimeOffset.UtcNow);

    private readonly Func<DateTimeOffset> _now;
    private readonly Dictionary<string, Dictionary<string, Reading>> _accounts = new(StringComparer.Ordinal);
    private DispatcherTimer? _releaseTimer;

    internal AccountPacing(Func<DateTimeOffset> now) => _now = now;

    /// <summary>Raised when an account may have started or stopped holding new work: a new reading, or a window resetting.</summary>
    public event Action? Changed;

    /// <summary>One limit window of one account as last reported.</summary>
    public sealed record Reading(string Window, string Status, double? Utilization, DateTimeOffset? ResetsAt, DateTimeOffset SeenAt);

    private static double Threshold => AppSettings.Current.PacingThresholdPercent / 100.0;

    /// <summary>Record one rate_limit_event's rate_limit_info for this account.</summary>
    public void Note(string provider, string? accountId, JsonObject info)
    {
        var status = Text(info, "status") ?? "allowed";
        var window = Text(info, "rateLimitType") ?? Text(info, "rate_limit_type") ?? "unknown";
        var utilization = Fraction(info["utilization"])
            ?? (status == "rejected" ? 1.0 : Fraction(info["surpassedThreshold"] ?? info["surpassed_threshold"]));
        var key = Key(provider, accountId);
        if (!_accounts.TryGetValue(key, out var windows)) _accounts[key] = windows = new(StringComparer.Ordinal);
        windows[window] = new Reading(window, status, utilization, Unix(info["resetsAt"] ?? info["resets_at"]), _now());
        ScheduleRelease();
        Changed?.Invoke();
    }

    /// <summary>The account's most used window that has not reset yet; null when nothing current is known.</summary>
    public Reading? Hottest(string provider, string? accountId)
    {
        if (!_accounts.TryGetValue(Key(provider, accountId), out var windows)) return null;
        var now = _now();
        return windows.Values.Where(reading => IsCurrent(reading, now))
            .OrderByDescending(reading => reading.Utilization ?? 0).FirstOrDefault();
    }

    /// <summary>True while new work for this account should wait or go elsewhere.</summary>
    public bool Holds(string provider, string? accountId) => Holding(provider, accountId) is not null;

    /// <summary>The window holding this account's new work: one the provider already refuses, or a short window past the
    /// threshold. A weekly window paces nothing on its own - holding new work for days because a week is 85% spent would
    /// stall a Bridge far longer than the limit it guards against - so it holds only once the provider refuses.</summary>
    private Reading? Holding(string provider, string? accountId)
    {
        if (!_accounts.TryGetValue(Key(provider, accountId), out var windows)) return null;
        var now = _now();
        return windows.Values.Where(reading => IsCurrent(reading, now)
                && (reading.Status == "rejected" || !IsLongWindow(reading.Window) && reading.Utilization >= Threshold))
            .OrderByDescending(reading => reading.Status == "rejected").ThenByDescending(reading => reading.Utilization ?? 0)
            .FirstOrDefault();
    }

    private static bool IsLongWindow(string window) =>
        window.StartsWith("seven_day", StringComparison.OrdinalIgnoreCase) || window.Contains("week", StringComparison.OrdinalIgnoreCase);

    /// <summary>"88% · resets 3:40 PM" for an account that holds new work; null for one that does not.</summary>
    public string? Describe(string provider, string? accountId)
    {
        if (Holding(provider, accountId) is not { } hot) return null;
        var used = hot.Utilization is { } fraction ? $"{Math.Round(fraction * 100):0}%" : "at its limit";
        return hot.ResetsAt is { } reset ? $"{used} · resets {reset.ToLocalTime():t}" : used;
    }

    /// <summary>Where new work goes instead of <paramref name="accountId"/>: another usable account of the same provider that
    /// does not hold, the one with the most room first (an account with no reading counts as half used). Null when none.</summary>
    public BridgeAccountChoice? SteerAway(string provider, string? accountId, IEnumerable<BridgeAccountChoice> choices) => choices
        .Where(choice => choice.Provider == provider && choice.IsUsable
                         && !string.Equals(choice.AccountId, accountId ?? "", StringComparison.Ordinal)
                         && !Holds(provider, choice.AccountId))
        .OrderBy(choice => Hottest(provider, choice.AccountId)?.Utilization ?? 0.5)
        .FirstOrDefault();

    /// <summary>Forget readings whose window has reset or gone stale, so the work they held can start.</summary>
    internal void Prune()
    {
        var now = _now();
        foreach (var windows in _accounts.Values)
            foreach (var window in windows.Where(pair => !IsCurrent(pair.Value, now)).Select(pair => pair.Key).ToArray())
                windows.Remove(window);
    }

    private static bool IsCurrent(Reading reading, DateTimeOffset now) =>
        reading.ResetsAt is { } reset ? now < reset : now - reading.SeenAt < UndatedReadingLifetime;

    /// <summary>Wake when the next current reading expires: that is when held work may start without any new event.</summary>
    private void ScheduleRelease()
    {
        var now = _now();
        var expiries = _accounts.Values.SelectMany(windows => windows.Values).Where(reading => IsCurrent(reading, now))
            .Select(reading => reading.ResetsAt ?? reading.SeenAt + UndatedReadingLifetime).ToArray();
        if (expiries.Length == 0)
        {
            _releaseTimer?.Stop();
            return;
        }
        if (_releaseTimer is null)
        {
            _releaseTimer = new DispatcherTimer(DispatcherPriority.Background);
            _releaseTimer.Tick += OnReleaseTick;
        }
        _releaseTimer.Stop();
        _releaseTimer.Interval = TimeSpan.FromSeconds(Math.Clamp((expiries.Min() - now).TotalSeconds + 1, 1, int.MaxValue / 1000.0));
        _releaseTimer.Start();
    }

    private void OnReleaseTick(object? sender, EventArgs e)
    {
        Prune();
        ScheduleRelease();
        Changed?.Invoke();
    }

    private static string Key(string provider, string? accountId) => provider + "\n" + accountId;

    private static string? Text(JsonObject info, string name) => info[name] is JsonValue value ? value.ToString() : null;

    // Claude reports utilization as a fraction of the window; a value above 1.5 can only be a percentage.
    private static double? Fraction(JsonNode? node) =>
        node is JsonValue value && double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
        && number >= 0 ? number > 1.5 ? number / 100 : number : null;

    // Unix seconds; a value this large can only be milliseconds.
    private static DateTimeOffset? Unix(JsonNode? node) =>
        node is JsonValue value && long.TryParse(value.ToString(), out var number) && number > 0
            ? number > 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(number) : DateTimeOffset.FromUnixTimeSeconds(number)
            : null;
}
