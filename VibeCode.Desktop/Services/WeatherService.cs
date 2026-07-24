using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using VibeCode.UI;

namespace VibeCode.Services;

/// <summary>One U.S. or Canadian place returned by <see cref="WeatherService.SearchPlacesAsync"/>.</summary>
public sealed class PlaceHit
{
    public string Name { get; init; } = "";
    public string Region { get; init; } = "";
    public string Country { get; init; } = "";
    public string CountryCode { get; init; } = "";
    public double Lat { get; init; }
    public double Lon { get; init; }
    internal long Population { get; init; }
    public string Display => string.IsNullOrEmpty(Region) ? Name : $"{Name}, {Region}";
}

/// <summary>
/// Optional weather extension for the U.S. and Canada: NWS observations in the U.S., Open-Meteo conditions in
/// Canada, the NWS radar mosaic, and SPC Day 1–3 outlooks. All endpoints are public and require no account or API
/// key. The saved location is city-level; "Get my location" uses an approximate network location rather than
/// retaining a device GPS fix.
/// </summary>
public sealed class WeatherService : Observable
{
    public static WeatherService Instance { get; } = new();

    // NWS asks every client to identify itself in User-Agent; requests without one get thrown away.
    private const string UserAgent = "VibeCode/1.0 (desktop app; github.com/vibecode)";
    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.Add("User-Agent", UserAgent);
        return http;
    }

    private WeatherService() { }

    // ---- location (persisted) ----
    public double? Lat => AppSettings.Current.WeatherLat;
    public double? Lon => AppSettings.Current.WeatherLon;
    public bool HasLocation => Lat is not null && Lon is not null;
    public string Place => AppSettings.Current.WeatherPlace ?? "";
    public string CountryCode => AppSettings.Current.WeatherCountryCode ?? "";

    // ---- bindable conditions ----
    private double? _tempF;
    private string _conditions = "";
    public string Conditions
    {
        get => _conditions;
        private set
        {
            if (!Set(ref _conditions, value)) return;
            Raise(nameof(ChipText));
            Raise(nameof(ChipTooltip));
        }
    }
    private string _station = "";
    public string Station { get => _station; private set { if (Set(ref _station, value)) Raise(nameof(ChipTooltip)); } }
    private bool _loading;
    public bool IsLoading { get => _loading; private set => Set(ref _loading, value); }
    private string _error = "";
    public string Error { get => _error; private set { if (Set(ref _error, value)) Raise(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrEmpty(_error);

    /// <summary>Temperature rounded to whole degrees F, or "--" before the first successful fetch.</summary>
    public string TempText => _tempF is null ? "--" : $"{Math.Round(_tempF.Value)}°";

    /// <summary>Compact titlebar label: current temperature and conditions, or a prompt to pick a place.</summary>
    public string ChipText
    {
        get
        {
            if (!HasLocation) return "Set location";
            if (_tempF is null) return "--\u00B0";
            return string.IsNullOrWhiteSpace(_conditions) ? TempText : $"{TempText} {_conditions}";
        }
    }

    public string ChipTooltip
    {
        get
        {
            if (!HasLocation) return "Weather - pick a location in Settings > Extensions";
            var parts = new List<string> { Place };
            if (!string.IsNullOrEmpty(_conditions)) parts.Add(_conditions);
            if (!string.IsNullOrEmpty(_station))
                parts.Add(_station == "Open-Meteo" ? "conditions by Open-Meteo" : $"station {_station}");
            parts.Add("Click to open NWS radar and SPC outlook maps");
            return string.Join("\n", parts);
        }
    }

    /// <summary>Condition glyph (Segoe Fluent Icons). Only codepoints verified present in the shipped font are used.</summary>
    public string ConditionGlyph
    {
        get
        {
            var c = _conditions.ToLowerInvariant();
            if (c.Contains("rain") || c.Contains("shower") || c.Contains("drizzle") || c.Contains("storm") ||
                c.Contains("snow") || c.Contains("sleet")) return "\uEB42";   // water drop
            if (c.Contains("cloud") || c.Contains("overcast") || c.Contains("fog") || c.Contains("haze")) return "\uE753";  // cloud
            if (c.Contains("clear") || c.Contains("sunny") || c.Contains("fair")) return "\uE706";   // sun
            return "\uE9CA";   // thermometer - unknown/no reading yet
        }
    }

    /// <summary>Whether the extension is turned on (mirrors AppSettings so the titlebar can bind to it).</summary>
    public bool Enabled
    {
        get => AppSettings.Current.WeatherEnabled;
        set
        {
            if (AppSettings.Current.WeatherEnabled == value) return;
            AppSettings.Current.WeatherEnabled = value;
            AppSettings.Current.Save();
            Raise(nameof(Enabled));
            if (value) _ = RefreshAsync();
        }
    }

    /// <summary>Re-read the settings-backed state after something else wrote settings.json (the titlebar chip and the
    /// Extensions card both bind straight to these).</summary>
    public void NotifyEnabledChanged()
    {
        Raise(nameof(Enabled)); Raise(nameof(HasLocation)); Raise(nameof(Place)); Raise(nameof(CountryCode));
        Raise(nameof(ChipText)); Raise(nameof(ChipTooltip));
    }

    public void SetLocation(string place, double lat, double lon, string countryCode)
    {
        countryCode = countryCode.Trim().ToUpperInvariant();
        if (countryCode != "US" && countryCode != "CA")
            throw new ArgumentException("Weather locations must be in the U.S. or Canada.", nameof(countryCode));

        var s = AppSettings.Current;
        s.WeatherPlace = place;
        s.WeatherLat = lat;
        s.WeatherLon = lon;
        s.WeatherCountryCode = countryCode;
        s.Save();
        _pointCache = null;

        // Do not briefly show the previous city's reading next to the newly selected place.
        _tempF = null;
        Conditions = "";
        Station = "";
        Error = "";

        Raise(nameof(Place)); Raise(nameof(Lat)); Raise(nameof(Lon)); Raise(nameof(CountryCode));
        Raise(nameof(HasLocation)); Raise(nameof(ChipText)); Raise(nameof(ChipTooltip));
        Raise(nameof(TempText)); Raise(nameof(ConditionGlyph));
        LocationChanged?.Invoke(this, EventArgs.Empty);
        _ = RefreshAsync();
    }

    /// <summary>Raised after <see cref="SetLocation"/> so an open radar window can recentre on the new point.</summary>
    public event EventHandler? LocationChanged;

    // ---- geocoding (Open-Meteo's free search API; no key) ----
    public async Task<List<PlaceHit>> SearchPlacesAsync(string query, CancellationToken ct = default)
    {
        query = query.Trim();
        if (query.Length < 2) return new List<PlaceHit>();

        var direct = await SearchCountriesAsync(query, ct);
        if (direct.Count > 0) return RankPlaces(direct, query);

        // The API searches a place name or postal code, not a free-form "city state" string. If the direct lookup
        // misses, separate a trailing state/province hint. This handles "McKinney Texas" and "Ottawa ON" while
        // preserving genuine multi-word city names on the direct path above.
        var words = query.Replace(',', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var split = words.Length - 1; split >= 1; split--)
        {
            var placeName = string.Join(' ', words[..split]);
            var regionHint = string.Join(' ', words[split..]);
            if (!IsPlausibleRegionHint(regionHint)) continue;
            var matches = (await SearchCountriesAsync(placeName, ct))
                .Where(hit => MatchesRegionHint(hit, regionHint))
                .ToList();
            if (matches.Count > 0) return RankPlaces(matches, placeName);
        }

        return new List<PlaceHit>();
    }

    private static async Task<List<PlaceHit>> SearchCountriesAsync(string query, CancellationToken ct)
    {
        var resultSets = await Task.WhenAll(
            FetchPlacesAsync(query, "US", ct),
            FetchPlacesAsync(query, "CA", ct));
        return resultSets.SelectMany(result => result)
            .DistinctBy(hit => $"{hit.CountryCode}|{hit.Name}|{hit.Region}", StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task<List<PlaceHit>> FetchPlacesAsync(string query, string countryCode, CancellationToken ct)
    {
        var hits = new List<PlaceHit>();
        var url = "https://geocoding-api.open-meteo.com/v1/search?count=10&language=en&format=json" +
                  $"&countryCode={countryCode}&name={Uri.EscapeDataString(query)}";
        var json = JsonNode.Parse(await Http.GetStringAsync(url, ct));
        if (json?["results"] is not JsonArray results) return hits;

        foreach (var result in results)
        {
            if (result is null) continue;
            var lat = ReadDouble(result["latitude"]);
            var lon = ReadDouble(result["longitude"]);
            var code = result["country_code"]?.GetValue<string>()?.ToUpperInvariant() ?? "";
            if (lat is null || lon is null || (code != "US" && code != "CA")) continue;

            hits.Add(new PlaceHit
            {
                Name = result["name"]?.GetValue<string>() ?? query,
                Region = result["admin1"]?.GetValue<string>() ?? "",
                Country = result["country"]?.GetValue<string>() ?? (code == "CA" ? "Canada" : "United States"),
                CountryCode = code,
                Lat = lat.Value,
                Lon = lon.Value,
                Population = result["population"] is { } population && long.TryParse(population.ToString(), out var value)
                    ? value
                    : 0,
            });
        }

        return hits;
    }

    private static List<PlaceHit> RankPlaces(IEnumerable<PlaceHit> hits, string query)
    {
        var normalizedQuery = NormalizePlaceText(query);
        return hits.OrderBy(hit => NormalizePlaceText(hit.Name) == normalizedQuery ? 0 : 1)
            .ThenBy(hit => NormalizePlaceText(hit.Name).StartsWith(normalizedQuery, StringComparison.Ordinal) ? 0 : 1)
            .ThenByDescending(hit => hit.Population)
            .ThenBy(hit => hit.Display, StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();
    }

    private static bool MatchesRegionHint(PlaceHit hit, string hint)
    {
        var normalizedHint = NormalizePlaceText(hint);
        if (normalizedHint.Length == 0) return true;

        var region = NormalizePlaceText(hit.Region);
        var country = NormalizePlaceText(hit.Country);
        if (region.StartsWith(normalizedHint, StringComparison.Ordinal) ||
            country.StartsWith(normalizedHint, StringComparison.Ordinal) ||
            NormalizePlaceText(hit.CountryCode).StartsWith(normalizedHint, StringComparison.Ordinal)) return true;

        return RegionCodes.TryGetValue(region, out var code) &&
               NormalizePlaceText(code).StartsWith(normalizedHint, StringComparison.Ordinal);
    }

    private static bool IsPlausibleRegionHint(string hint)
    {
        var normalizedHint = NormalizePlaceText(hint);
        if (normalizedHint.Length < 2) return false;
        if ("unitedstates".StartsWith(normalizedHint, StringComparison.Ordinal) ||
            "usa".StartsWith(normalizedHint, StringComparison.Ordinal) ||
            "canada".StartsWith(normalizedHint, StringComparison.Ordinal)) return true;
        return RegionCodes.Any(pair => pair.Key.StartsWith(normalizedHint, StringComparison.Ordinal) ||
                                       NormalizePlaceText(pair.Value).StartsWith(normalizedHint, StringComparison.Ordinal));
    }

    private static string NormalizePlaceText(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    // Abbreviations let the structured fallback understand both "Dallas TX" and "Toronto ON".
    private static readonly IReadOnlyDictionary<string, string> RegionCodes = new Dictionary<string, string>
    {
        ["alabama"] = "AL", ["alaska"] = "AK", ["arizona"] = "AZ", ["arkansas"] = "AR",
        ["california"] = "CA", ["colorado"] = "CO", ["connecticut"] = "CT", ["delaware"] = "DE",
        ["districtofcolumbia"] = "DC", ["florida"] = "FL", ["georgia"] = "GA", ["hawaii"] = "HI",
        ["idaho"] = "ID", ["illinois"] = "IL", ["indiana"] = "IN", ["iowa"] = "IA", ["kansas"] = "KS",
        ["kentucky"] = "KY", ["louisiana"] = "LA", ["maine"] = "ME", ["maryland"] = "MD",
        ["massachusetts"] = "MA", ["michigan"] = "MI", ["minnesota"] = "MN", ["mississippi"] = "MS",
        ["missouri"] = "MO", ["montana"] = "MT", ["nebraska"] = "NE", ["nevada"] = "NV",
        ["newhampshire"] = "NH", ["newjersey"] = "NJ", ["newmexico"] = "NM", ["newyork"] = "NY",
        ["northcarolina"] = "NC", ["northdakota"] = "ND", ["ohio"] = "OH", ["oklahoma"] = "OK",
        ["oregon"] = "OR", ["pennsylvania"] = "PA", ["rhodeisland"] = "RI", ["southcarolina"] = "SC",
        ["southdakota"] = "SD", ["tennessee"] = "TN", ["texas"] = "TX", ["utah"] = "UT",
        ["vermont"] = "VT", ["virginia"] = "VA", ["washington"] = "WA", ["westvirginia"] = "WV",
        ["wisconsin"] = "WI", ["wyoming"] = "WY", ["alberta"] = "AB", ["britishcolumbia"] = "BC",
        ["manitoba"] = "MB", ["newbrunswick"] = "NB", ["newfoundlandandlabrador"] = "NL",
        ["northwestterritories"] = "NT", ["novascotia"] = "NS", ["nunavut"] = "NU", ["ontario"] = "ON",
        ["princeedwardisland"] = "PE", ["quebec"] = "QC", ["saskatchewan"] = "SK", ["yukon"] = "YT",
    };

    /// <summary>
    /// Gets a city-level estimate from the public network address. This works in an unpackaged WPF app without
    /// requesting device GPS permission; callers should describe the result as approximate.
    /// </summary>
    public async Task<PlaceHit> GetMyLocationAsync(CancellationToken ct = default)
    {
        var json = JsonNode.Parse(await Http.GetStringAsync("https://get.geojs.io/v1/ip/geo.json", ct));
        var countryCode = json?["country_code"]?.GetValue<string>()?.ToUpperInvariant() ?? "";
        if (countryCode != "US" && countryCode != "CA")
            throw new InvalidOperationException("Your network location appears to be outside the U.S. and Canada.");

        var lat = ReadDouble(json?["latitude"]);
        var lon = ReadDouble(json?["longitude"]);
        if (lat is null || lon is null)
            throw new InvalidOperationException("Your network location could not be determined.");

        var region = json?["region"]?.GetValue<string>()?.Trim() ?? "";
        var city = json?["city"]?.GetValue<string>()?.Trim() ?? "";
        var country = json?["country"]?.GetValue<string>()?.Trim() ??
                      (countryCode == "CA" ? "Canada" : "United States");
        if (string.IsNullOrEmpty(city)) city = !string.IsNullOrEmpty(region) ? region : country;
        if (string.Equals(city, region, StringComparison.OrdinalIgnoreCase)) region = "";

        return new PlaceHit
        {
            Name = city,
            Region = region,
            Country = country,
            CountryCode = countryCode,
            Lat = lat.Value,
            Lon = lon.Value,
        };
    }

    private static double? ReadDouble(JsonNode? value) =>
        double.TryParse(value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;

    // ---- current conditions (NWS observations in the U.S.; Open-Meteo model data in Canada) ----
    private (string stations, string city)? _pointCache;

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (!HasLocation) return;
        IsLoading = true;
        try
        {
            var lat = Lat!.Value.ToString("0.####", CultureInfo.InvariantCulture);
            var lon = Lon!.Value.ToString("0.####", CultureInfo.InvariantCulture);

            if (string.Equals(CountryCode, "CA", StringComparison.OrdinalIgnoreCase))
            {
                await RefreshCanadianConditionsAsync(lat, lon, ct);
                return;
            }

            if (_pointCache is null)
            {
                var pt = JsonNode.Parse(await Http.GetStringAsync($"https://api.weather.gov/points/{lat},{lon}", ct));
                var props = pt?["properties"];
                var stations = props?["observationStations"]?.GetValue<string>();
                var city = props?["relativeLocation"]?["properties"]?["city"]?.GetValue<string>() ?? "";
                if (string.IsNullOrEmpty(stations))
                {
                    Error = "No NWS coverage for that location (US only).";
                    return;
                }
                _pointCache = (stations, city);
            }

            var list = JsonNode.Parse(await Http.GetStringAsync(_pointCache.Value.stations, ct));
            var first = (list?["features"] as JsonArray)?.FirstOrDefault();
            var id = first?["properties"]?["stationIdentifier"]?.GetValue<string>();
            if (string.IsNullOrEmpty(id)) { Error = "No nearby weather station."; return; }

            var obs = JsonNode.Parse(await Http.GetStringAsync(
                $"https://api.weather.gov/stations/{id}/observations/latest", ct));
            var op = obs?["properties"];
            var celsius = op?["temperature"]?["value"];

            Station = id;
            Conditions = op?["textDescription"]?.GetValue<string>() ?? "";
            _tempF = celsius is null ? null : celsius.GetValue<double>() * 9.0 / 5.0 + 32.0;
            Error = "";
            Raise(nameof(TempText)); Raise(nameof(ChipText)); Raise(nameof(ConditionGlyph));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Error = ex is HttpRequestException ? "Could not reach the weather service." : ex.Message;
        }
        finally { IsLoading = false; }
    }

    private async Task RefreshCanadianConditionsAsync(string lat, string lon, CancellationToken ct)
    {
        var url = $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}" +
                  "&current=temperature_2m,weather_code&temperature_unit=fahrenheit&forecast_days=1";
        var json = JsonNode.Parse(await Http.GetStringAsync(url, ct));
        var current = json?["current"];
        var temperature = ReadDouble(current?["temperature_2m"]);
        if (temperature is null) throw new InvalidDataException("No current weather was returned for that location.");

        _tempF = temperature;
        var code = current?["weather_code"] is { } codeNode && int.TryParse(codeNode.ToString(), out var parsed)
            ? parsed
            : -1;
        Conditions = DescribeWeatherCode(code);
        Station = "Open-Meteo";
        Error = "";
        Raise(nameof(TempText)); Raise(nameof(ChipText)); Raise(nameof(ConditionGlyph));
    }

    private static string DescribeWeatherCode(int code) => code switch
    {
        0 => "Clear",
        1 => "Mostly clear",
        2 => "Partly cloudy",
        3 => "Overcast",
        45 or 48 => "Fog",
        51 or 53 or 55 => "Drizzle",
        56 or 57 => "Freezing drizzle",
        61 or 63 or 65 => "Rain",
        66 or 67 => "Freezing rain",
        71 or 73 or 75 => "Snow",
        77 => "Snow grains",
        80 or 81 or 82 => "Rain showers",
        85 or 86 => "Snow showers",
        95 => "Thunderstorms",
        96 or 99 => "Thunderstorms with hail",
        _ => "Current conditions",
    };

    // ---- radar frame times ----
    // The NOAA GeoServer advertises the timestamps it holds for the reflectivity mosaic in its WMS capabilities
    // document, as a comma-separated ISO list on the "time" dimension. Those values feed the animation timeline.
    public static async Task<List<string>> RadarFrameTimesAsync(int max = 8, CancellationToken ct = default)
    {
        var frames = new List<string>();
        try
        {
            var xml = await Http.GetStringAsync(RadarTiles.CapabilitiesUrl, ct);
            var i = xml.IndexOf("<Dimension name=\"time\"", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return frames;
            var open = xml.IndexOf('>', i);
            var close = xml.IndexOf("</Dimension>", i, StringComparison.OrdinalIgnoreCase);
            if (open < 0 || close < 0 || close <= open) return frames;
            var times = xml[(open + 1)..close].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            frames.AddRange(times.TakeLast(Math.Max(1, max)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* animation falls back to the live frame */ }
        return frames;
    }

    /// <summary>Renders an ISO radar timestamp as a short local-time label for the timeline ("3:42 PM").</summary>
    public static string FrameLabel(string? iso)
    {
        if (string.IsNullOrEmpty(iso)) return "Latest";
        return DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
            ? t.ToLocalTime().ToString("h:mm tt")
            : "Latest";
    }
}
