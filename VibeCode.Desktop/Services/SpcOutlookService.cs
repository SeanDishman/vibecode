using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;

namespace VibeCode.Services;

/// <summary>A latitude/longitude vertex from an SPC GeoJSON outlook.</summary>
public readonly record struct SpcCoordinate(double Lat, double Lon);

/// <summary>One GeoJSON polygon. Ring zero is the exterior; later rings are holes.</summary>
public sealed class SpcPolygon
{
    public IReadOnlyList<IReadOnlyList<SpcCoordinate>> Rings { get; init; } = Array.Empty<IReadOnlyList<SpcCoordinate>>();

    public bool Contains(double lat, double lon)
    {
        if (Rings.Count == 0 || !RingContains(Rings[0], lat, lon)) return false;
        for (var i = 1; i < Rings.Count; i++)
            if (RingContains(Rings[i], lat, lon)) return false;
        return true;
    }

    private static bool RingContains(IReadOnlyList<SpcCoordinate> ring, double lat, double lon)
    {
        if (ring.Count < 3) return false;
        var inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            var a = ring[j];
            var b = ring[i];
            if (OnSegment(a, b, lat, lon)) return true;
            if ((b.Lat > lat) == (a.Lat > lat)) continue;
            var crossingLon = (a.Lon - b.Lon) * (lat - b.Lat) / (a.Lat - b.Lat) + b.Lon;
            if (lon < crossingLon) inside = !inside;
        }
        return inside;
    }

    private static bool OnSegment(SpcCoordinate a, SpcCoordinate b, double lat, double lon)
    {
        const double epsilon = 1e-8;
        var cross = (lon - a.Lon) * (b.Lat - a.Lat) - (lat - a.Lat) * (b.Lon - a.Lon);
        if (Math.Abs(cross) > epsilon) return false;
        return lon >= Math.Min(a.Lon, b.Lon) - epsilon && lon <= Math.Max(a.Lon, b.Lon) + epsilon
            && lat >= Math.Min(a.Lat, b.Lat) - epsilon && lat <= Math.Max(a.Lat, b.Lat) + epsilon;
    }
}

/// <summary>One categorical SPC risk contour such as Marginal, Slight, or Enhanced.</summary>
public sealed class SpcRiskArea
{
    public string Code { get; init; } = "";
    public string Label { get; init; } = "";
    public int Rank { get; init; }
    public string Fill { get; init; } = "#808080";
    public string Stroke { get; init; } = "#FFFFFF";
    public IReadOnlyList<SpcPolygon> Polygons { get; init; } = Array.Empty<SpcPolygon>();

    public bool Contains(double lat, double lon) => Polygons.Any(polygon => polygon.Contains(lat, lon));
}

/// <summary>A current SPC Day 1, 2, or 3 categorical convective outlook.</summary>
public sealed class SpcOutlook
{
    public int Day { get; init; }
    public DateTimeOffset? Issued { get; init; }
    public DateTimeOffset? ValidFrom { get; init; }
    public DateTimeOffset? ValidTo { get; init; }
    public string Forecaster { get; init; } = "";
    public IReadOnlyList<SpcRiskArea> Areas { get; init; } = Array.Empty<SpcRiskArea>();

    /// <summary>The highest categorical contour containing the supplied point.</summary>
    public SpcRiskArea? RiskAt(double lat, double lon) => Areas
        .Where(area => area.Contains(lat, lon))
        .OrderByDescending(area => area.Rank)
        .FirstOrDefault();

    public string ValidWindowUtc
    {
        get
        {
            if (ValidFrom is not { } start || ValidTo is not { } end) return "Current outlook";
            return $"Valid {start.UtcDateTime:ddd HH}Z – {end.UtcDateTime:ddd HH}Z";
        }
    }
}

/// <summary>Lossless parser for the Polygon and MultiPolygon shapes in SPC's categorical GeoJSON products.</summary>
public static class SpcOutlookParser
{
    public static SpcOutlook Parse(string json, int day)
    {
        if (day is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(day), "SPC outlook day must be 1, 2, or 3.");
        var root = JsonNode.Parse(json) as JsonObject
                   ?? throw new InvalidDataException("The SPC outlook was not a GeoJSON object.");
        if (!string.Equals(Text(root["type"]), "FeatureCollection", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The SPC outlook was not a GeoJSON FeatureCollection.");

        var areas = new List<SpcRiskArea>();
        DateTimeOffset? issued = null, validFrom = null, validTo = null;
        var forecaster = "";
        if (root["features"] is JsonArray features)
        foreach (var feature in features.OfType<JsonObject>())
        {
            var props = feature["properties"] as JsonObject ?? new JsonObject();
            var geometry = feature["geometry"] as JsonObject;
            var polygons = ParseGeometry(geometry);
            if (polygons.Count == 0) continue;

            var code = Text(props["LABEL"]).Trim().ToUpperInvariant();
            if (code.Length == 0) code = "OUTLOOK";
            var dn = Number(props["DN"]);
            var colors = DefaultColors(code);
            areas.Add(new SpcRiskArea
            {
                Code = code,
                Label = Text(props["LABEL2"]) is { Length: > 0 } label ? label : FriendlyLabel(code),
                Rank = RiskRank(code, dn),
                Fill = Color(Text(props["fill"]), colors.fill),
                Stroke = Color(Text(props["stroke"]), colors.stroke),
                Polygons = polygons,
            });

            issued ??= Timestamp(props["ISSUE_ISO"] ?? props["ISSUE"]);
            validFrom ??= Timestamp(props["VALID_ISO"] ?? props["VALID"]);
            validTo ??= Timestamp(props["EXPIRE_ISO"] ?? props["EXPIRE"]);
            if (forecaster.Length == 0) forecaster = Text(props["FORECASTER"]);
        }

        return new SpcOutlook
        {
            Day = day,
            Issued = issued,
            ValidFrom = validFrom,
            ValidTo = validTo,
            Forecaster = forecaster,
            Areas = areas.OrderBy(area => area.Rank).ToList(),
        };
    }

    private static List<SpcPolygon> ParseGeometry(JsonObject? geometry)
    {
        var result = new List<SpcPolygon>();
        if (geometry?["coordinates"] is not JsonArray coordinates) return result;
        switch (Text(geometry["type"]))
        {
            case "Polygon":
                if (ParsePolygon(coordinates) is { } polygon) result.Add(polygon);
                break;
            case "MultiPolygon":
                foreach (var node in coordinates.OfType<JsonArray>())
                    if (ParsePolygon(node) is { } part) result.Add(part);
                break;
        }
        return result;
    }

    private static SpcPolygon? ParsePolygon(JsonArray coordinates)
    {
        var rings = new List<IReadOnlyList<SpcCoordinate>>();
        foreach (var ringNode in coordinates.OfType<JsonArray>())
        {
            var ring = new List<SpcCoordinate>();
            foreach (var point in ringNode.OfType<JsonArray>())
            {
                if (point.Count < 2 || ReadDouble(point[0]) is not { } lon || ReadDouble(point[1]) is not { } lat) continue;
                ring.Add(new SpcCoordinate(lat, lon));
            }
            if (ring.Count >= 3) rings.Add(ring);
        }
        return rings.Count == 0 ? null : new SpcPolygon { Rings = rings };
    }

    private static string Text(JsonNode? node)
    {
        try { return node?.GetValue<string>() ?? ""; }
        catch { return node?.ToString() ?? ""; }
    }

    private static int Number(JsonNode? node) => int.TryParse(node?.ToString(), NumberStyles.Integer,
        CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static double? ReadDouble(JsonNode? node) => double.TryParse(node?.ToString(), NumberStyles.Float,
        CultureInfo.InvariantCulture, out var value) ? value : null;

    private static DateTimeOffset? Timestamp(JsonNode? node)
    {
        var value = Text(node);
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var iso)) return iso;
        return DateTimeOffset.TryParseExact(value, "yyyyMMddHHmm", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var compact) ? compact : null;
    }

    private static int RiskRank(string code, int fallback) => code switch
    {
        "TSTM" => 2, "MRGL" => 3, "SLGT" => 4, "ENH" => 5, "MDT" => 6, "HIGH" => 8,
        _ => fallback,
    };

    private static string FriendlyLabel(string code) => code switch
    {
        "TSTM" => "General Thunderstorms Risk", "MRGL" => "Marginal Risk", "SLGT" => "Slight Risk",
        "ENH" => "Enhanced Risk", "MDT" => "Moderate Risk", "HIGH" => "High Risk", _ => "SPC Outlook",
    };

    private static (string fill, string stroke) DefaultColors(string code) => code switch
    {
        "TSTM" => ("#C1E9C1", "#55BB55"), "MRGL" => ("#66A366", "#005500"),
        "SLGT" => ("#FFE066", "#DDAA00"), "ENH" => ("#FFA366", "#FF6600"),
        "MDT" => ("#FF6666", "#FF0000"), "HIGH" => ("#FF66FF", "#FF00FF"),
        _ => ("#808080", "#FFFFFF"),
    };

    private static string Color(string value, string fallback)
    {
        value = value.Trim();
        if (value.Length is 4 or 7 or 9 && value[0] == '#' && value.Skip(1).All(Uri.IsHexDigit)) return value;
        return fallback;
    }
}

/// <summary>Downloads and briefly caches the official current SPC Day 1–3 categorical outlooks.</summary>
public sealed class SpcOutlookService
{
    public static SpcOutlookService Instance { get; } = new();
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);
    private static readonly HttpClient Http = CreateHttp();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<int, (SpcOutlook outlook, DateTimeOffset fetched)> _cache = new();

    private SpcOutlookService() { }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.Add("User-Agent", "VibeCode/1.0 (desktop app; SPC outlook map)");
        return http;
    }

    public static string UrlForDay(int day)
    {
        if (day is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(day));
        return $"https://www.spc.noaa.gov/products/outlook/day{day}otlk_cat.nolyr.geojson";
    }

    public async Task<SpcOutlook> GetAsync(int day, bool forceRefresh = false, CancellationToken ct = default)
    {
        if (day is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(day));
        if (!forceRefresh && Fresh(day) is { } cached) return cached;

        await _gate.WaitAsync(ct);
        try
        {
            if (!forceRefresh && Fresh(day) is { } afterWait) return afterWait;
            var fixturePath = Environment.GetEnvironmentVariable("VIBECODE_SPC_OUTLOOK_FIXTURE");
            var json = Environment.GetEnvironmentVariable("VIBECODE_HIDDEN") == "1"
                       && fixturePath is { Length: > 0 }
                ? await File.ReadAllTextAsync(Path.GetFullPath(fixturePath), ct)
                : await Http.GetStringAsync(UrlForDay(day), ct);
            var outlook = SpcOutlookParser.Parse(json, day);
            _cache[day] = (outlook, DateTimeOffset.UtcNow);
            return outlook;
        }
        finally { _gate.Release(); }
    }

    private SpcOutlook? Fresh(int day) => _cache.TryGetValue(day, out var entry)
        && DateTimeOffset.UtcNow - entry.fetched < CacheLifetime ? entry.outlook : null;
}
