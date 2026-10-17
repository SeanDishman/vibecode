using VibeCode.Services;

namespace VibeCode.UI;

/// <summary>One exact rolling hour shared by the totals and both workflow-wall usage charts.</summary>
public sealed record WorkflowUsageSnapshot(UsageReport Report, IReadOnlyList<HourUsage> Buckets)
{
    public static readonly TimeSpan UsageWindow = TimeSpan.FromHours(1);
    public double TokensPerMinute => Report.Total / UsageWindow.TotalMinutes;
    public double TokensPerTurn => Report.Turns > 0 ? Report.Total / Report.Turns : 0;
    public double RecentTokensPerMinute { get; init; }

    public static WorkflowUsageSnapshot Build(IReadOnlyList<UsageEntry> entries, DateTimeOffset now)
    {
        var since = now - UsageWindow;
        static double Tokens(double value) => double.IsFinite(value) && value > 0 ? value : 0;
        var recent = entries.Where(e => e.At >= since && e.At <= now).Select(e => new UsageEntry
        {
            At = e.At, Provider = e.Provider, Model = ModelPricing.CanonicalId(e.Model),
            Input = Tokens(e.Input), CacheRead = Tokens(e.CacheRead), CacheWrite = Tokens(e.CacheWrite),
            Output = Tokens(e.Output), CostUsd = Tokens(e.CostUsd), CostReported = e.CostReported,
            Project = e.Project, SessionId = e.SessionId,
        }).Where(e => e.Total > 0).ToArray();
        var report = UsageAnalytics.BuildSince(recent, since, now);
        // Absolute-time buckets survive clock-offset/DST changes. Both partial edge buckets are retained;
        // filtering above prevents the first partial bucket from including anything older than one hour.
        var size = TimeSpan.FromMinutes(5).Ticks;
        var first = now.UtcTicks / size * size - 12 * size;
        var buckets = Enumerable.Range(0, 13).Select(i => new HourUsage
        { Hour = new DateTimeOffset(first + i * size, TimeSpan.Zero).LocalDateTime }).ToArray();
        foreach (var entry in recent)
        {
            var bucket = buckets[(int)((entry.At.UtcTicks - first) / size)];
            bucket.TokensIn += entry.TotalIn;
            bucket.TokensOut += entry.Output;
            bucket.CacheRead += entry.CacheRead;
            bucket.Turns++;
            if (!bucket.ByModel.TryGetValue(entry.Model, out var model))
                bucket.ByModel[entry.Model] = model = new HourSlice();
            model.Tokens += entry.Total;
        }
        return new(report, buckets)
        {
            RecentTokensPerMinute = recent.Where(e => e.At > now.AddMinutes(-5)).Sum(e => e.Total) / 5,
        };
    }
}
