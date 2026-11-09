using System.Net.NetworkInformation;
using System.Windows.Threading;

namespace VibeCode.Services;

/// <summary>A Bridge pane waiting on a <see cref="ProviderHealthGate"/>.</summary>
public interface IHealthGateMember
{
    /// <summary>The gate's shared tick: refresh the countdown, and continue once the gate lets this member.</summary>
    void OnHealthGateTick(ProviderHealthGate gate);

    /// <summary>True once this member's probe has real model output. A probe that hears back has shown the provider is up
    /// before its whole turn ends.</summary>
    bool ProbeAnswered { get; }
}

/// <summary>
/// One recovery gate per provider and account. A Bridge pane whose turn failed with a transient provider error (overloaded,
/// unavailable, network, timeout) waits here instead of on a one-minute timer of its own. Exactly one waiting pane probes -
/// its ordinary continuation is the probe - after exponential backoff with full jitter, and a probe that hears back
/// releases every other pane at once. Before this each pane retried on its own fixed 60 s cycle and spent a model turn every
/// time, so an outage cost one turn per pane per minute, with every pane hitting the provider in step.
/// Rate and usage limits never reach a gate: <see cref="UsageLimitRecovery"/> owns those.
/// </summary>
public sealed class ProviderHealthGate
{
    public static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaximumDelay = TimeSpan.FromMinutes(10);
    /// <summary>Full jitter can draw almost nothing, but the CLI has already retried by the time a turn fails.</summary>
    public static readonly TimeSpan MinimumDelay = TimeSpan.FromSeconds(5);
    /// <summary>A probe that has heard nothing for this long counts as failed, so one hung turn cannot hold every pane.</summary>
    public static readonly TimeSpan ProbeTimeout = MaximumDelay;
    private const int MaximumAttempt = 16;

    private readonly Func<DateTimeOffset> _now;
    private readonly Func<double> _random;
    private readonly List<IHealthGateMember> _waiting = new();
    private readonly List<IHealthGateMember> _released = new();

    internal ProviderHealthGate(string provider, string account, Func<DateTimeOffset> now, Func<double> random)
    {
        Provider = provider;
        Account = account;
        _now = now;
        _random = random;
    }

    public string Provider { get; }
    public string Account { get; }
    /// <summary>Failed probes since the last success; each one doubles the ceiling of the next wait.</summary>
    public int Attempt { get; private set; }
    public DateTimeOffset NextProbeAt { get; private set; }
    /// <summary>The member elected to probe. Its continuation is in flight once <see cref="ProbeStartedAt"/> is set.</summary>
    public IHealthGateMember? Prober { get; private set; }
    public DateTimeOffset? ProbeStartedAt { get; private set; }
    public bool Offline { get; private set; }
    /// <summary>The provider status page's open incident while this gate waits; null when there is none or it is unreadable.</summary>
    public string? Incident { get; internal set; }
    public TimeSpan UntilNextProbe => NextProbeAt - _now();

    internal bool IsEmpty => Prober is null && _waiting.Count == 0 && _released.Count == 0;

    /// <summary>The prober first, so its verdict is in before the others check in on the same tick.</summary>
    internal IReadOnlyList<IHealthGateMember> Members =>
        (Prober is { } prober ? [prober] : Array.Empty<IHealthGateMember>())
        .Concat(_waiting.Where(member => !ReferenceEquals(member, Prober))).Concat(_released).ToArray();

    public bool Holds(IHealthGateMember member) =>
        ReferenceEquals(Prober, member) || _waiting.Contains(member) || _released.Contains(member);

    /// <summary>Released by a successful probe and about to continue.</summary>
    public bool Releases(IHealthGateMember member) => _released.Contains(member);

    /// <summary>Full jitter: a uniform draw below a ceiling that doubles with each failed probe up to the maximum, and never
    /// under the minimum.</summary>
    public static TimeSpan Backoff(int attempt, double unit)
    {
        var ceiling = Math.Min(MaximumDelay.TotalSeconds, BaseDelay.TotalSeconds * Math.Pow(2, Math.Clamp(attempt, 0, MaximumAttempt)));
        return TimeSpan.FromSeconds(Math.Max(MinimumDelay.TotalSeconds, ceiling * Math.Clamp(unit, 0, 1)));
    }

    public void Join(IHealthGateMember member)
    {
        if (Holds(member)) return;
        // The first member of an outage starts the clock; later arrivals wait for the same probe.
        if (Prober is null && _waiting.Count == 0) Schedule();
        _waiting.Add(member);
    }

    /// <summary>True when <paramref name="member"/> may continue now: a successful probe released it, or it is the first
    /// waiting member to ask once the probe is due, which elects it. Every other member keeps waiting.</summary>
    public bool TryProceed(IHealthGateMember member)
    {
        if (Offline) return false;
        if (_released.Contains(member)) return true;
        if (Prober is not null) return ReferenceEquals(Prober, member) && ProbeStartedAt is null;
        if (_now() < NextProbeAt || !_waiting.Contains(member)) return false;
        Prober = member;
        return true;
    }

    /// <summary>The member is sending its continuation and leaves the queue. True when that turn is this gate's probe.</summary>
    public bool BeginTurn(IHealthGateMember member)
    {
        if (_released.Remove(member)) return false;
        _waiting.Remove(member);
        if (!ReferenceEquals(Prober, member) || ProbeStartedAt is not null) return false;
        ProbeStartedAt = _now();
        return true;
    }

    /// <summary>The provider answered: every waiting member continues, and the next outage starts from the first step.</summary>
    public void ProbeSucceeded()
    {
        Prober = null;
        ProbeStartedAt = null;
        Attempt = 0;
        _released.AddRange(_waiting);
        _waiting.Clear();
    }

    /// <summary>The probe failed the same way: wait longer, and let the next probe come from the back of the queue.</summary>
    public void ProbeFailed(IHealthGateMember member)
    {
        if (!ReferenceEquals(Prober, member)) return;   // a late verdict from a probe the gate already gave up on
        Prober = null;
        ProbeStartedAt = null;
        Attempt = Math.Min(Attempt + 1, MaximumAttempt);
        Schedule();
        _waiting.Remove(member);
        _waiting.Add(member);
    }

    /// <summary>A probe that never heard back counts as failed, even while its turn is still running.</summary>
    internal void ProbeTimedOut()
    {
        Prober = null;
        ProbeStartedAt = null;
        Attempt = Math.Min(Attempt + 1, MaximumAttempt);
        Schedule();
    }

    public void Leave(IHealthGateMember member)
    {
        _waiting.Remove(member);
        _released.Remove(member);
        if (!ReferenceEquals(Prober, member)) return;
        var unanswered = ProbeStartedAt is not null;
        Prober = null;
        ProbeStartedAt = null;
        // A probe abandoned mid-flight proved nothing, so the next one waits a fresh draw at the same step.
        if (unanswered) Schedule();
    }

    /// <summary>Make the probe due now, as when the network has just come back.</summary>
    public void ProbeNow() => NextProbeAt = _now();

    internal void SetOnline(bool online)
    {
        if (online && Offline) ProbeNow();
        Offline = !online;
    }

    private void Schedule() => NextProbeAt = _now() + Backoff(Attempt, _random());
}

/// <summary>
/// Every open <see cref="ProviderHealthGate"/>, ticked by one shared timer that runs only while a gate is open. While it runs
/// it also follows Windows' network availability - offline pauses every gate, reconnecting probes at once - and reads the
/// provider's public status page so a waiting pane can name the incident.
/// </summary>
public static class ProviderHealthGates
{
    private static readonly Dictionary<string, ProviderHealthGate> Open = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, DateTimeOffset> StatusDue = new(StringComparer.Ordinal);
    private static readonly HashSet<string> StatusReading = new(StringComparer.Ordinal);
    private static DispatcherTimer? _timer;
    private static Dispatcher? _dispatcher;
    private static bool _online = true;

    internal static Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;
    internal static Func<double> Random { get; set; } = System.Random.Shared.NextDouble;

    /// <summary>Wait on the gate for this provider and account, opening it if this is the first failure of an outage.</summary>
    public static ProviderHealthGate Join(string provider, string? account, IHealthGateMember member)
    {
        var key = provider + "\n" + account;
        if (!Open.TryGetValue(key, out var gate))
        {
            if (Open.Count == 0) StartTicking();
            Open[key] = gate = new ProviderHealthGate(provider, account ?? "", () => Clock(), () => Random());
            gate.SetOnline(_online);
        }
        gate.Join(member);
        return gate;
    }

    /// <summary>One pass over every gate: settle a probe that heard back or ran out of time, let each member check in, and
    /// drop gates nobody waits on.</summary>
    internal static void Tick()
    {
        var now = Clock();
        foreach (var (key, gate) in Open.ToArray())
        {
            if (gate.Prober is { } prober && gate.ProbeStartedAt is { } started)
            {
                if (prober.ProbeAnswered) gate.ProbeSucceeded();
                else if (now - started >= ProviderHealthGate.ProbeTimeout) gate.ProbeTimedOut();
            }
            foreach (var member in gate.Members) member.OnHealthGateTick(gate);
            if (gate.IsEmpty) Open.Remove(key);
        }
        ReadStatusPages(now);
        if (Open.Count == 0) StopTicking();
    }

    /// <summary>Offline pauses every gate; coming back probes at once instead of waiting out the backoff.</summary>
    internal static void NetworkChanged(bool available)
    {
        _online = available;
        foreach (var gate in Open.Values) gate.SetOnline(available);
    }

    private static void StartTicking()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        try { _online = NetworkInterface.GetIsNetworkAvailable(); }
        catch (NetworkInformationException) { _online = true; }
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        _timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTimerTick;
        _timer.Start();
    }

    private static void StopTicking()
    {
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        if (_timer is { } timer)
        {
            timer.Stop();
            timer.Tick -= OnTimerTick;
        }
        _timer = null;
    }

    private static void OnTimerTick(object? sender, EventArgs e) => Tick();

    // Raised on a thread-pool thread.
    private static void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) =>
        _dispatcher?.InvokeAsync(() => NetworkChanged(e.IsAvailable));

    /// <summary>Read each waiting provider's status page now and then. A provider with no open gate is never polled.</summary>
    private static void ReadStatusPages(DateTimeOffset now)
    {
        foreach (var provider in StatusDue.Keys.Where(p => Open.Values.All(gate => gate.Provider != p)).ToArray())
            StatusDue.Remove(provider);   // the next outage reads the page straight away
        // Hidden smoke runs and test fixtures contact nothing outside the machine on a provider's behalf.
        if (Environment.GetEnvironmentVariable("VIBECODE_HIDDEN") == "1") return;
        foreach (var provider in Open.Values.Select(gate => gate.Provider).Distinct().ToArray())
        {
            if (ProviderStatusPage.SummaryUrl(provider) is not { } url || StatusReading.Contains(provider)
                || StatusDue.TryGetValue(provider, out var due) && now < due) continue;
            StatusDue[provider] = now + ProviderStatusPage.PollInterval;
            StatusReading.Add(provider);
            _ = ReadStatusPageAsync(provider, url);
        }
    }

    private static async Task ReadStatusPageAsync(string provider, Uri url)
    {
        var (read, incident) = await ProviderStatusPage.TryReadAsync(url, provider);
        StatusReading.Remove(provider);
        if (!read) return;   // unreachable or unreadable: keep whatever the panes already show
        foreach (var gate in Open.Values.Where(gate => gate.Provider == provider)) gate.Incident = incident;
    }
}
