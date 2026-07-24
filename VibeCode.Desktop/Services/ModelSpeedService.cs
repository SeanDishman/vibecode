using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using VibeCode.UI;

namespace VibeCode.Services;

/// <summary>How fast one model is running right now, aggregated over the providers serving it.</summary>
public readonly record struct ModelSpeed(double TokensPerSecond, double LatencySeconds)
{
    /// <summary>The one line the model picker prints under a model's description.</summary>
    public string Text => $"{TokensPerSecond:0} tok/s · {LatencySeconds:0.0}s latency";
}

/// <summary>
/// Live output speed per model, the pair openrouter.ai puts on a model page: median throughput in tokens per second
/// and median end-to-end latency over the last 30 minutes of real traffic, request-weighted across every provider
/// endpoint OpenRouter routes that model to. Only a model slug ever leaves the app — nothing about this app's chats.
///
/// Lookups are lazy (the first time a model picker opens) and cached for <see cref="CacheLifetime"/>. A model that
/// can't be mapped onto an OpenRouter id, or that OpenRouter has no traffic for, simply shows no speed line rather
/// than a guess.
/// </summary>
public sealed class ModelSpeedService
{
    public static ModelSpeedService Instance { get; } = new();

    private const string UserAgent = "VibeCode/1.0 (desktop app; github.com/vibecode)";
    private const string ModelUrl = "https://openrouter.ai/api/v1/model/";
    private const string StatsUrl = "https://openrouter.ai/api/frontend/v1/stats/endpoint?permaslug={0}&variant=standard";
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);
    private const int MaxParallelFetches = 4;

    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.Add("User-Agent", UserAgent);
        return http;
    }

    private ModelSpeedService() { }

    /// <summary>Raised on the UI thread after a lookup lands, so an already-open picker can fill itself in.</summary>
    public event Action? Updated;

    private readonly object _gate = new();
    private readonly Dictionary<string, (ModelSpeed? Speed, DateTime At)> _speeds = new(StringComparer.OrdinalIgnoreCase);
    // OpenRouter id -> dated "permaslug" its stats are keyed by (null = it 404s, i.e. we guessed an id it doesn't have).
    private readonly Dictionary<string, string?> _slugs = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The cached speed for a model, or null while it is unknown. Never blocks.</summary>
    public ModelSpeed? For(ModelChoice? model)
    {
        var id = model is null ? null : OpenRouterId(model);
        if (id is null) return null;
        lock (_gate) return _speeds.TryGetValue(id, out var entry) ? entry.Speed : null;
    }

    /// <summary>
    /// Start (or skip, when cached) lookups for a picker's worth of models. Returns immediately; callers learn
    /// about results through <see cref="Updated"/>.
    /// </summary>
    public void Prefetch(IEnumerable<ModelChoice> models)
    {
        var ids = new List<string>();
        foreach (var model in models)
        {
            var id = OpenRouterId(model);
            if (id is not null && !ids.Contains(id, StringComparer.OrdinalIgnoreCase)) ids.Add(id);
        }
        if (ids.Count == 0) return;

        lock (_gate)
        {
            var now = DateTime.UtcNow;
            ids.RemoveAll(id => _inFlight.Contains(id)
                                || (_speeds.TryGetValue(id, out var entry) && now - entry.At < CacheLifetime));
            if (ids.Count == 0) return;
            foreach (var id in ids) _inFlight.Add(id);
        }
        _ = Task.Run(() => FetchAsync(ids));
    }

    private async Task FetchAsync(List<string> ids)
    {
        var landed = false;
        try
        {
            using var limit = new SemaphoreSlim(MaxParallelFetches);
            var results = await Task.WhenAll(ids.Select(async id =>
            {
                await limit.WaitAsync().ConfigureAwait(false);
                try { return (Id: id, Speed: await FetchOneAsync(id).ConfigureAwait(false)); }
                finally { limit.Release(); }
            })).ConfigureAwait(false);

            lock (_gate)
            {
                var now = DateTime.UtcNow;
                foreach (var (id, speed) in results)
                {
                    // Cache misses too, so a model OpenRouter has no numbers for isn't re-asked on every popup.
                    _speeds[id] = (speed, now);
                    landed |= speed is not null;
                }
            }
        }
        catch
        {
            // Speed is decoration. A failed lookup leaves the line off; it retries the next time a picker opens.
        }
        finally
        {
            lock (_gate) foreach (var id in ids) _inFlight.Remove(id);
        }
        if (landed) Application.Current?.Dispatcher.BeginInvoke(() => Updated?.Invoke());
    }

    private async Task<ModelSpeed?> FetchOneAsync(string openRouterId)
    {
        try
        {
            var permaslug = await PermaslugAsync(openRouterId).ConfigureAwait(false);
            if (permaslug is null) return null;
            var url = string.Format(StatsUrl, Uri.EscapeDataString(permaslug));
            var json = JsonNode.Parse(await Http.GetStringAsync(url).ConfigureAwait(false));
            return Aggregate(json?["data"] as JsonArray);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// One number per model out of the per-provider rows: each endpoint's median weighted by how many requests it
    /// actually served, so a barely-used endpoint can't drag the headline figure around.
    /// </summary>
    private static ModelSpeed? Aggregate(JsonArray? endpoints)
    {
        if (endpoints is null) return null;
        double throughput = 0, latencyMs = 0, requests = 0;
        foreach (var endpoint in endpoints)
        {
            if (endpoint is null) continue;
            // Skip anything a normal request would never be routed to.
            if (Flag(endpoint["is_disabled"]) || Flag(endpoint["is_hidden"])
                || Flag(endpoint["is_byok_only"]) || Flag(endpoint["is_deranked"])) continue;
            if (Num(endpoint["status"]) is < 0) continue;

            var stats = endpoint["stats"];
            if (stats is null) continue;
            var tps = Num(stats["p50_throughput"]);
            var latency = Num(stats["p50_latency"]);
            var count = Num(stats["request_count"]);
            if (tps is not > 0 || latency is not > 0 || count is not > 0) continue;

            throughput += tps.Value * count.Value;
            latencyMs += latency.Value * count.Value;
            requests += count.Value;
        }
        return requests > 0 ? new ModelSpeed(throughput / requests, latencyMs / requests / 1000.0) : null;
    }

    /// <summary>Resolve an OpenRouter id to the dated slug its stats are filed under, remembering the answer.</summary>
    private async Task<string?> PermaslugAsync(string openRouterId)
    {
        lock (_gate)
            if (_slugs.TryGetValue(openRouterId, out var cached)) return cached;

        string? slug = null;
        var missing = false;
        try
        {
            var json = JsonNode.Parse(await Http.GetStringAsync(ModelUrl + openRouterId).ConfigureAwait(false));
            slug = json?["data"]?["canonical_slug"]?.GetValue<string>();
        }
        catch (HttpRequestException e)
        {
            // A 404 means the id we derived isn't a model OpenRouter carries — worth remembering. Anything else
            // (offline, rate limited) is transient and must NOT poison the cache for the rest of the session.
            missing = e.StatusCode == HttpStatusCode.NotFound;
            if (!missing) return null;
        }
        catch
        {
            return null;
        }
        if (slug is not null || missing) lock (_gate) _slugs[openRouterId] = slug;
        return slug;
    }

    // ---- mapping this app's model ids onto OpenRouter's ----

    /// <summary>Claude Code ships tier aliases rather than ids, and Kimi's ids drop the vendor.</summary>
    private static readonly IReadOnlyDictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["opus"] = "anthropic/claude-opus-5",
        ["opusplan"] = "anthropic/claude-opus-5",
        ["sonnet"] = "anthropic/claude-sonnet-5",
        ["haiku"] = "anthropic/claude-haiku-4.5",
        ["fable"] = "anthropic/claude-fable-5",
        ["k3"] = "moonshotai/kimi-k3",
        ["kimi-for-coding"] = "moonshotai/kimi-k2.7-code",
        ["kimi-for-coding-highspeed"] = "moonshotai/kimi-k2.7-code",
    };

    private static readonly IReadOnlyDictionary<string, string> VendorPrefixes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["claude"] = "anthropic/",
        ["codex"] = "openai/",
        ["kimi"] = "moonshotai/",
        ["grok"] = "x-ai/",
    };

    // Claude Code writes versions with dashes (claude-opus-4-8) where OpenRouter writes dots (claude-opus-4.8).
    private static readonly Regex VersionDots = new(@"(?<=\d)-(?=\d)", RegexOptions.Compiled);
    private static readonly Regex DateSuffix = new(@"-\d{6,8}$", RegexOptions.Compiled);

    /// <summary>The OpenRouter model id for one picker row, or null when we can't map it onto one.</summary>
    public static string? OpenRouterId(ModelChoice model)
    {
        // ResolvedModel first: it's what a tier alias like "sonnet" actually points at today.
        return Map(model.ResolvedModel, model.Provider) ?? Map(model.Value, model.Provider);
    }

    private static string? Map(string? raw, string? provider)
    {
        var key = Canonical(raw);
        if (key.Length == 0 || key is "default" or "auto") return null;
        if (Aliases.TryGetValue(key, out var mapped)) return mapped;
        if (key.Contains('/')) return key;
        return VendorPrefixes.TryGetValue(ProviderModelCatalog.Normalize(provider), out var prefix) ? prefix + key : null;
    }

    private static string Canonical(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var id = raw.Trim().ToLowerInvariant();
        var bracket = id.IndexOf('[');
        if (bracket >= 0) id = id[..bracket];                   // "claude-opus-5[1m]" is the same model, longer context
        if (id.StartsWith("vibecode:", StringComparison.Ordinal)) id = id["vibecode:".Length..];
        if (id.StartsWith("kimi-code/", StringComparison.Ordinal)) id = id["kimi-code/".Length..];
        id = DateSuffix.Replace(id, "");                        // strip the snapshot date before dashes become dots
        return VersionDots.Replace(id, ".").Trim();
    }

    private static double? Num(JsonNode? node)
    {
        try { return node?.GetValue<double>(); }
        catch { return null; }
    }

    private static bool Flag(JsonNode? node)
    {
        try { return node?.GetValue<bool>() ?? false; }
        catch { return false; }
    }
}
