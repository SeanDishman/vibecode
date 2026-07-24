using System.Windows.Media;

namespace VibeCode.Services;

/// <summary>The time slice the usage page is showing. One filter for every chart on the page.</summary>
public enum UsageWindow { Today, Week, Month, All }

/// <summary>Everything one model burned inside the selected window.</summary>
public sealed class ModelUsage
{
    public required string Model { get; init; }
    public required string Provider { get; init; }
    public required string Display { get; init; }
    /// <summary>Fixed categorical slot for this model - see <see cref="UsagePalette"/>. Never rank-derived.</summary>
    public required Color Color { get; init; }
    public double Input { get; set; }
    public double CacheWrite { get; set; }
    public double CacheRead { get; set; }
    public double Output { get; set; }
    public double CostUsd { get; set; }
    /// <summary>Estimated watt-hours behind this row - see <see cref="ModelEnergy"/>.</summary>
    public double EnergyWh { get; set; }
    public int Turns { get; set; }
    /// <summary>False when any turn in this row was priced by the Opus-tier fallback rather than a listed rate.</summary>
    public bool FullyPriced { get; set; } = true;
    /// <summary>False when any turn in this row fell back to the Opus tier for its energy estimate too.</summary>
    public bool FullyRated { get; set; } = true;

    public double TotalIn => Input + CacheWrite + CacheRead;
    public double Total => TotalIn + Output;
    /// <summary>Litres of cooling water behind <see cref="EnergyWh"/>.</summary>
    public double WaterLitres => ModelEnergy.OnSiteWaterLitres(EnergyWh);
}

/// <summary>One calendar day of the selected window, for the trend chart.</summary>
public sealed class DayUsage
{
    public required DateTime Day { get; init; }
    public double TokensIn { get; set; }
    public double TokensOut { get; set; }
    public double CostUsd { get; set; }
    /// <summary>Estimated watt-hours behind this day - see <see cref="ModelEnergy"/>.</summary>
    public double EnergyWh { get; set; }
    public double Total => TokensIn + TokensOut;
    /// <summary>Litres of cooling water behind <see cref="EnergyWh"/>. On-site only, the same boundary the rest
    /// of the page leads with.</summary>
    public double WaterLitres => ModelEnergy.OnSiteWaterLitres(EnergyWh);
}

/// <summary>One clock hour, for the live HUD's hourly graph. Daily buckets are too coarse to show a working
/// session taking shape, which is the whole point of a monitor you leave running.</summary>
public sealed class HourUsage
{
    public required DateTime Hour { get; init; }
    public double TokensIn { get; set; }
    public double TokensOut { get; set; }
    /// <summary>Cache-served share of <see cref="TokensIn"/>, so the HUD can show a cache rate over the same
    /// rolling 24 hours the rest of its panels use rather than over a calendar day.</summary>
    public double CacheRead { get; set; }
    public double CostUsd { get; set; }
    public double EnergyWh { get; set; }
    public int Turns { get; set; }
    public double Total => TokensIn + TokensOut;

    /// <summary>This hour split by model, so the HUD can stack one bar by model instead of showing a flat
    /// total. Keyed by model id - the colour comes from <see cref="UsagePalette"/> like everywhere else.</summary>
    public Dictionary<string, HourSlice> ByModel { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>One model's share of one hour.</summary>
public sealed class HourSlice
{
    public double Tokens { get; set; }
    public double CostUsd { get; set; }
    public double EnergyWh { get; set; }
}

/// <summary>The aggregated answer to "what have I used" for one window.</summary>
public sealed class UsageReport
{
    public required UsageWindow Window { get; init; }
    public IReadOnlyList<ModelUsage> Models { get; init; } = Array.Empty<ModelUsage>();
    public IReadOnlyList<DayUsage> Days { get; init; } = Array.Empty<DayUsage>();
    public double Input { get; init; }
    public double CacheWrite { get; init; }
    public double CacheRead { get; init; }
    public double Output { get; init; }
    public double CostUsd { get; init; }
    /// <summary>Estimated watt-hours across the window - see <see cref="ModelEnergy"/>.</summary>
    public double EnergyWh { get; init; }
    public int Turns { get; init; }
    public DateTimeOffset? FirstSeen { get; init; }
    /// <summary>True when every turn in the window was priced from a listed rate.</summary>
    public bool FullyPriced { get; init; } = true;
    /// <summary>True when every turn in the window had its own energy rating rather than the Opus fallback.</summary>
    public bool FullyRated { get; init; } = true;

    public double TotalIn => Input + CacheWrite + CacheRead;
    public double Total => TotalIn + Output;
    public bool HasData => Turns > 0 && Total > 0;

    /// <summary>Litres of cooling water behind <see cref="EnergyWh"/>, on-site only.</summary>
    public double WaterLitres => ModelEnergy.OnSiteWaterLitres(EnergyWh);

    /// <summary>Litres including the water consumed generating the electricity.</summary>
    public double WaterLitresWithGeneration => ModelEnergy.TotalWaterLitres(EnergyWh);

    /// <summary>Share of input tokens that were served from cache - the single number that explains why a huge
    /// token count can cost very little.</summary>
    public double CacheHitRatio => TotalIn > 0 ? CacheRead / TotalIn : 0;
}

/// <summary>Rolls the raw <see cref="UsageLog"/> into the shapes the usage page draws.</summary>
public static class UsageAnalytics
{
    public static string Label(UsageWindow window) => window switch
    {
        UsageWindow.Today => "Today",
        UsageWindow.Week => "Last 7 days",
        UsageWindow.Month => "Last 30 days",
        _ => "All time",
    };

    /// <summary>Inclusive lower bound of a window, or null for all-time. Day windows start at local midnight so
    /// "Today" means today, not "the last 24 hours".</summary>
    public static DateTimeOffset? Since(UsageWindow window, DateTimeOffset now)
    {
        var midnight = new DateTimeOffset(now.Date, now.Offset);
        return window switch
        {
            UsageWindow.Today => midnight,
            UsageWindow.Week => midnight.AddDays(-6),
            UsageWindow.Month => midnight.AddDays(-29),
            _ => null,
        };
    }

    public static UsageReport Build(IReadOnlyList<UsageEntry> entries, UsageWindow window) =>
        Build(entries, window, DateTimeOffset.Now);

    /// <summary>
    /// The same roll-up from an ARBITRARY cutoff rather than one of the four calendar windows - what the
    /// telemetry wall needs, because its range chips are durations (1 h … 30 d) counted back from now, not days
    /// counted back from midnight. Callers pass the exact instant their charts start at, so the totals and the
    /// bars can never be summing different sets of turns. The report is labelled <see cref="UsageWindow.All"/>
    /// because none of the four names it; the only thing that reads that label is <see cref="Label"/>, which
    /// the wall does not call.
    /// </summary>
    public static UsageReport BuildSince(IReadOnlyList<UsageEntry> entries, DateTimeOffset? since, DateTimeOffset now) =>
        Build(entries, UsageWindow.All, now, since);

    /// <summary><paramref name="now"/> is a parameter so the aggregation is testable and so every chart on one
    /// render shares a single "now" instead of each re-reading the clock.</summary>
    public static UsageReport Build(IReadOnlyList<UsageEntry> entries, UsageWindow window, DateTimeOffset now) =>
        Build(entries, window, now, Since(window, now));

    private static UsageReport Build(IReadOnlyList<UsageEntry> entries, UsageWindow window, DateTimeOffset now,
        DateTimeOffset? since)
    {
        var models = new Dictionary<string, ModelUsage>(StringComparer.OrdinalIgnoreCase);
        var days = new Dictionary<DateTime, DayUsage>();
        double input = 0, cacheWrite = 0, cacheRead = 0, output = 0, cost = 0, energyWh = 0;
        var turns = 0;
        var fullyPriced = true;
        var fullyRated = true;
        DateTimeOffset? firstSeen = null;

        foreach (var e in entries)
        {
            if (firstSeen is null || e.At < firstSeen) firstSeen = e.At;
            if (since is not null && e.At < since) continue;

            if (!models.TryGetValue(e.Model, out var model))
            {
                models[e.Model] = model = new ModelUsage
                {
                    Model = e.Model,
                    Provider = e.Provider,
                    Display = UsagePalette.DisplayName(e.Model),
                    Color = UsagePalette.ColorFor(e.Model),
                };
            }
            model.Input += e.Input;
            model.CacheWrite += e.CacheWrite;
            model.CacheRead += e.CacheRead;
            model.Output += e.Output;
            model.CostUsd += e.CostUsd;
            model.Turns++;
            // A model with no listed rate is priced at the Opus fallback. Say so rather than quoting it flatly.
            if (!e.CostReported && !ModelPricing.IsPriced(e.Model))
            {
                model.FullyPriced = false;
                fullyPriced = false;
            }
            // Energy is always our own estimate - no provider reports it - so the only question is whether this
            // model has its own rating or borrowed the Opus tier's.
            var turnWh = ModelEnergy.TurnEnergyWh(e.Model, e.Input, e.CacheWrite, e.CacheRead, e.Output);
            model.EnergyWh += turnWh;
            if (!ModelEnergy.IsRated(e.Model))
            {
                model.FullyRated = false;
                fullyRated = false;
            }

            var day = e.At.LocalDateTime.Date;
            if (!days.TryGetValue(day, out var bucket)) days[day] = bucket = new DayUsage { Day = day };
            bucket.TokensIn += e.TotalIn;
            bucket.TokensOut += e.Output;
            bucket.CostUsd += e.CostUsd;
            bucket.EnergyWh += turnWh;

            input += e.Input;
            cacheWrite += e.CacheWrite;
            cacheRead += e.CacheRead;
            output += e.Output;
            cost += e.CostUsd;
            energyWh += turnWh;
            turns++;
        }

        return new UsageReport
        {
            Window = window,
            // Ordered by spend for the table and the legend. This is presentation order only - a model's COLOR
            // comes from its own identity, so reordering or filtering never repaints the survivors.
            Models = models.Values.OrderByDescending(m => m.CostUsd).ThenByDescending(m => m.Total).ToList(),
            Days = FillDays(days, window, now),
            Input = input,
            CacheWrite = cacheWrite,
            CacheRead = cacheRead,
            Output = output,
            CostUsd = cost,
            EnergyWh = energyWh,
            Turns = turns,
            FirstSeen = firstSeen,
            FullyPriced = fullyPriced,
            FullyRated = fullyRated,
        };
    }

    /// <summary>A day with no turns is a real zero, not a gap: without it the trend chart would join Monday
    /// straight to Friday and hide the quiet days entirely.</summary>
    private static List<DayUsage> FillDays(Dictionary<DateTime, DayUsage> byDay, UsageWindow window, DateTimeOffset now)
    {
        if (byDay.Count == 0) return new List<DayUsage>();
        var last = now.Date;
        var first = window switch
        {
            UsageWindow.Today => last,
            UsageWindow.Week => last.AddDays(-6),
            UsageWindow.Month => last.AddDays(-29),
            _ => byDay.Keys.Min(),
        };
        if (first > last) first = last;

        // All-time can span years. Past ~120 points a daily line is unreadable noise on a card this size, so
        // fall back to only the days that actually have data.
        if ((last - first).TotalDays > 120)
            return byDay.Values.OrderBy(d => d.Day).ToList();

        var result = new List<DayUsage>();
        for (var day = first; day <= last; day = day.AddDays(1))
            result.Add(byDay.TryGetValue(day, out var found) ? found : new DayUsage { Day = day });
        return result;
    }

    /// <summary>The last <paramref name="hours"/> clock hours, oldest first, zero-filled. Quiet hours have to be
    /// real zeroes or the HUD's graph would compress a whole idle night into one flat step and read as busy.</summary>
    public static IReadOnlyList<HourUsage> BuildHours(IReadOnlyList<UsageEntry> entries, int hours, DateTimeOffset now) =>
        BuildBuckets(entries, TimeSpan.FromHours(1), hours, now);

    /// <summary>
    /// The last <paramref name="count"/> buckets of <paramref name="size"/>, oldest first, zero-filled - the
    /// general form of <see cref="BuildHours"/>, so one dashboard can show the same telemetry at 30-minute,
    /// hourly and daily resolution without three near-identical aggregations drifting apart.
    ///
    /// The grid is anchored to the CURRENT day's midnight and stepped uniformly back from there, so a 30-minute
    /// bucket always starts on :00 or :30 and the bars do not shuffle sideways as the clock moves.
    /// <see cref="HourUsage.Hour"/> carries the bucket's start whatever the size is.
    /// </summary>
    public static IReadOnlyList<HourUsage> BuildBuckets(IReadOnlyList<UsageEntry> entries, TimeSpan size, int count,
        DateTimeOffset now)
    {
        if (count <= 0 || size <= TimeSpan.Zero) return Array.Empty<HourUsage>();

        // Build the grid ONCE, then index entries straight into it. Deriving each entry's bucket independently -
        // by flooring it against its own day's midnight - looked equivalent and was not: a size that does not
        // divide a day evenly restarts the grid at every midnight, so entries on an earlier day produced keys
        // this method never generated and their tokens silently vanished. It also lost an hour across a DST
        // spring-forward, because stepping a local DateTime by size walks over wall-clock times that real
        // timestamps still land in. Indexing into the generated grid makes both failures impossible by
        // construction rather than by comment.
        var localNow = now.LocalDateTime;
        var anchor = localNow.Date;
        var elapsed = (localNow - anchor).Ticks / size.Ticks;
        var last = DateTime.SpecifyKind(anchor.AddTicks(elapsed * size.Ticks), DateTimeKind.Unspecified);
        var first = last - TimeSpan.FromTicks(size.Ticks * (count - 1));

        var result = new List<HourUsage>(count);
        for (var i = 0; i < count; i++)
            result.Add(new HourUsage { Hour = first + TimeSpan.FromTicks(size.Ticks * i) });

        foreach (var e in entries)
        {
            var at = e.At.LocalDateTime;
            // A future-stamped entry - clock skew, or a log copied from another machine - is excluded at EVERY
            // resolution. The bound has to be NOW, not the end of the current bucket: at daily size the bucket
            // ends at tomorrow's midnight, so testing against it still counted an entry stamped later today in
            // the daily panel while the hourly one dropped it - the same quiet disagreement between two panels,
            // just moved rather than fixed.
            if (at < first || at > localNow) continue;
            var index = (int)((at - first).Ticks / size.Ticks);
            if (index < 0 || index >= result.Count) continue;
            var bucket = result[index];
            var wh = ModelEnergy.TurnEnergyWh(e.Model, e.Input, e.CacheWrite, e.CacheRead, e.Output);
            bucket.TokensIn += e.TotalIn;
            bucket.TokensOut += e.Output;
            bucket.CacheRead += e.CacheRead;
            bucket.CostUsd += e.CostUsd;
            bucket.EnergyWh += wh;
            bucket.Turns++;

            var key = ModelPricing.CanonicalId(e.Model);
            if (!bucket.ByModel.TryGetValue(key, out var slice)) bucket.ByModel[key] = slice = new HourSlice();
            slice.Tokens += e.Total;
            slice.CostUsd += e.CostUsd;
            slice.EnergyWh += wh;
        }

        return result;
    }

    // ---------- formatting ----------

    /// <summary>Electricity, picking its unit: 840 Wh / 10.2 kWh. One significant-ish figure, because the
    /// estimate behind it is worth about that much - see <see cref="ModelEnergy"/>.</summary>
    public static string Energy(double wh)
    {
        var v = Math.Abs(wh);
        if (v >= 1000) return (wh / 1000).ToString("0.##") + " kWh";
        if (v >= 10) return wh.ToString("0.#") + " Wh";
        if (v <= 0) return "0 Wh";
        return wh.ToString("0.##") + " Wh";
    }

    /// <summary>Electricity formatted for an AXIS or a COLUMN, where every value must share one unit. The
    /// per-value <see cref="Energy"/> picks its own, which is right for a standalone figure and wrong for a
    /// series: it rendered one axis as "0 Wh / 1 kWh / 2 kWh". The scale's largest value chooses the unit.</summary>
    public static string EnergyScaled(double value, double scaleMax)
    {
        if (Math.Abs(scaleMax) >= 1000) return (value / 1000).ToString("0.##") + " kWh";
        return value.ToString("0.##") + " Wh";
    }

    /// <summary>Water, picking its unit: 260 mL / 10.3 L.</summary>
    public static string Water(double litres)
    {
        var v = Math.Abs(litres);
        if (v >= 1) return litres.ToString("0.##") + " L";
        if (v <= 0) return "0 mL";
        var ml = litres * 1000;
        return ml >= 10 ? ml.ToString("0") + " mL" : ml.ToString("0.#") + " mL";
    }

    /// <summary>Water formatted for an AXIS or a COLUMN, where every value must share one unit - the twin of
    /// <see cref="EnergyScaled"/> and for the same reason: <see cref="Water"/> picks its unit per value, which
    /// would render one axis as "0 mL / 1 L / 2 L". The scale's largest value chooses the unit.</summary>
    public static string WaterScaled(double value, double scaleMax)
    {
        if (Math.Abs(scaleMax) >= 1) return value.ToString("0.##") + " L";
        return (value * 1000).ToString("0.#") + " mL";
    }

    /// <summary>Something to measure the kilowatt-hours against. Picked so the comparison is always a household
    /// object rather than an abstraction - "0.1% of a household's annual use" tells nobody anything.</summary>
    public static string EnergyEquivalent(double wh)
    {
        if (wh <= 0) return "";
        if (wh < 20) return $"about {wh / 0.01:0} seconds of a 36 W laptop charger";
        if (wh < 200) return $"about {wh / 10:0.#} hours of a 10 W LED bulb";
        if (wh < 2000) return $"about {wh / 900:0.#} loads through a washing machine";
        if (wh < 40_000) return $"about {wh / 1400:0.#} days of running a fridge";
        return $"about {wh / 1000 / 30:0.#} months of electricity for one home";
    }

    /// <summary>Water measured in things you can picture holding.</summary>
    public static string WaterEquivalent(double litres)
    {
        if (litres <= 0) return "";
        if (litres < 0.25) return $"about {litres * 1000 / 5:0} teaspoons";
        if (litres < 2) return $"about {litres / 0.25:0.#} cups";
        if (litres < 100) return $"about {litres / 0.5:0} small water bottles";
        return $"about {litres / 150:0.#} baths";
    }

    /// <summary>Compact token count: 987 / 12.4K / 3.28M / 1.05B.</summary>
    public static string Tokens(double value)
    {
        var v = Math.Abs(value);
        if (v >= 1_000_000_000) return (value / 1_000_000_000).ToString("0.##") + "B";
        if (v >= 1_000_000) return (value / 1_000_000).ToString("0.##") + "M";
        if (v >= 1_000) return (value / 1_000).ToString("0.#") + "K";
        return value.ToString("0");
    }

    /// <summary>Money, with enough precision that a fraction of a cent still reads as a number rather than $0.00.</summary>
    public static string Money(double usd)
    {
        var v = Math.Abs(usd);
        if (v >= 1000) return "$" + usd.ToString("#,##0");
        if (v >= 10) return "$" + usd.ToString("0.00");
        if (v >= 0.01) return "$" + usd.ToString("0.000");
        if (v <= 0) return "$0";
        return "$" + usd.ToString("0.00000");
    }

    /// <summary>
    /// Money formatted for a COLUMN or an AXIS, where every value must share one format. <see cref="Money"/>
    /// picks its precision per value, which is right for a standalone figure but wrong for a series: it rendered
    /// one axis as "$0 / $5.000 / $10.00" and one table column as "$18.26 / $4.431". The scale's largest value
    /// chooses the format, and every value in the group is then written the same way.
    /// </summary>
    public static string MoneyScaled(double value, double scaleMax)
    {
        var m = Math.Abs(scaleMax);
        if (m >= 1000) return "$" + value.ToString("#,##0");
        if (m >= 1) return "$" + value.ToString("0.00");
        if (m >= 0.01) return "$" + value.ToString("0.000");
        return "$" + value.ToString("0.00000");
    }

    public static string Percent(double ratio) => (ratio * 100).ToString("0.#") + "%";
}
