using System.Text.Json.Nodes;

namespace VibeCode.Services;

/// <summary>Accounts for every turn of an isolated planner, including retries and interrupted usage.</summary>
internal sealed class BridgeSuggestionUsage : IDisposable
{
    private readonly string _provider, _project;
    private readonly string? _selectedModel, _tier;
    private readonly Action<BridgeUsageSummary>? _changed;
    private readonly object _gate = new();
    private readonly Dictionary<string, JsonObject> _messages = new();
    private readonly Timer _pulse;
    private UsageEntry? _live;
    private BridgeUsageSummary _completed = new();
    private JsonArray _models = new();
    private string? _sessionId, _observedModel, _streamMessage;
    private double _reportedCost;
    private Tokens _sessionTokens;
    private bool _recorded, _disposed;

    public BridgeSuggestionUsage(string provider, string? selectedModel, string project,
        string? tier = null, Action<BridgeUsageSummary>? changed = null)
    {
        _provider = provider; _selectedModel = selectedModel; _project = project; _tier = tier; _changed = changed;
        // A Usage window may open halfway through planning, or the provider may be silent for a long tool call.
        _pulse = new Timer(_ => { lock (_gate) { if (!_disposed) PublishTelemetry(); } }, null, 2000, 2000);
    }

    public BridgeUsageSummary Summary { get { lock (_gate) return _completed + Summarize(_live); } }

    public void BeginTurn()
    {
        lock (_gate)
        {
            if (_disposed) return;
            CommitUnfinished();
            _recorded = false;
            _messages.Clear(); _streamMessage = null;
        }
    }

    public void Observe(JsonNode node, string? sessionId, JsonArray models)
    {
        lock (_gate)
        {
            if (_disposed || _recorded) return;
            _sessionId = sessionId; _models = models;
            var type = UsagePricing.Text(node["type"]);
            var subtype = UsagePricing.Text(node["subtype"]);
            if (type == "system" && subtype is "init" or "codex_reserve_activated" or "usage_update" or "codex_usage_checkpoint"
                && UsagePricing.Text(node["model"]) is { Length: > 0 } model && model != "default")
                _observedModel = model;
            if (type == "system" && subtype == "codex_usage_checkpoint")
            {
                _live = Entry(node["usage"], ResolveModel(node), UsagePricing.Tier(node) ?? _tier);
                CommitUnfinished();
                _messages.Clear();
                Changed();
                return;
            }
            if (type == "system" && subtype == "usage_update" && HasBuckets(node["usage"]))
                _live = Entry(node["usage"], ResolveModel(node), UsagePricing.Tier(node) ?? _tier);
            else if (type == "assistant" && node["message"]?["usage"] is not null)
                CaptureMessage(node["message"]!);
            else if (type == "stream_event" && node["event"] is { } ev)
            {
                if (UsagePricing.Text(ev["type"]) == "message_start" && ev["message"] is { } message)
                {
                    _streamMessage = UsagePricing.Text(message["id"]) ?? "planning-stream";
                    if (message["usage"] is null) return;
                    CaptureMessage(message, _streamMessage);
                }
                else if (UsagePricing.Text(ev["type"]) == "message_delta" && ev["usage"] is { } delta)
                    CaptureMessage(new JsonObject { ["usage"] = delta.DeepClone() }, _streamMessage ?? "planning-stream");
                else return;
            }
            else return;
            Changed();
        }
    }

    private void CaptureMessage(JsonNode message, string? id = null)
    {
        id ??= UsagePricing.Text(message["id"]) ?? _streamMessage ?? "planning-message";
        if (!_messages.TryGetValue(id, out var saved)) _messages[id] = saved = new JsonObject();
        if (message["model"] is { } model)
        {
            saved["model"] = model.DeepClone();
            _observedModel = UsagePricing.Text(model);
        }
        if (message["usage"] is JsonObject usage)
        {
            var merged = saved["usage"] as JsonObject ?? new JsonObject();
            foreach (var (key, value) in usage)
                merged[key] = key == "output_tokens"
                    ? JsonValue.Create(Math.Max(Number(merged, key), UsagePricing.Number(value) ?? 0))
                    : value?.DeepClone();
            if (saved["usage"] is null) saved["usage"] = merged;
        }
        var entries = _messages.Values.Select(m => Entry(m["usage"], ResolveModel(m), UsagePricing.Tier(m["usage"]) ?? _tier)).ToArray();
        _live = new UsageEntry
        {
            Provider = _provider,
            Model = ResolveModel(null),
            SessionId = _sessionId,
            Project = _project,
            Input = entries.Sum(e => e.Input),
            CacheWrite = entries.Sum(e => e.CacheWrite),
            CacheRead = entries.Sum(e => e.CacheRead),
            Output = entries.Sum(e => e.Output),
            CostUsd = entries.Sum(e => e.CostUsd),
        };
    }

    public void Record(JsonNode result, string? sessionId, JsonArray models)
    {
        lock (_gate)
        {
            if (_disposed || _recorded) return;
            _sessionId = sessionId; _models = models;
            var usage = result["usage"];
            var session = Tokens.Read(result["session_usage"]);
            UsageEntry entry;
            if (session.Total > 0 && (_provider == "kimi" || !HasBuckets(usage)))
                entry = Entry((session - _sessionTokens).Json(), ResolveModel(result), UsagePricing.Tier(result) ?? _tier);
            else if (HasBuckets(usage))
            {
                entry = Entry(usage, ResolveModel(result), UsagePricing.Tier(result) ?? _tier);
                // Per-message estimates preserve mixed request tiers and cache TTL when the final totals agree.
                if (_messages.Count > 0 && _live is { } live && Tokens.Read(usage) == Tokens.From(live)) entry.CostUsd = live.CostUsd;
            }
            else entry = _live ?? Entry(null, ResolveModel(result), _tier);
            var reportedCost = UsagePricing.Number(result["total_cost_usd"]);
            if (reportedCost is > 0 || ((_provider is "gemini") && reportedCost is not null))
            {
                entry.CostUsd = Math.Max(0, reportedCost!.Value - _reportedCost);
                entry.CostReported = true;
                _reportedCost = reportedCost.Value;
            }
            Commit(entry);
            _recorded = true;
            _live = null;
            _messages.Clear();
            Changed();
        }
    }

    private void Commit(UsageEntry entry)
    {
        if (entry.Total <= 0) return;
        entry.At = DateTimeOffset.Now;
        entry.SessionId = _sessionId;
        UsageLog.Instance.Record(entry);
        _sessionTokens += Tokens.From(entry);
        _completed += Summarize(entry);
    }

    private void CommitUnfinished()
    {
        if (_live is { } live) Commit(live);
        _live = null;
        LiveTurnTelemetry.Instance.Clear(this);
    }

    private UsageEntry Entry(JsonNode? usage, string model, string? tier) => new()
    {
        Provider = _provider,
        Model = model,
        SessionId = _sessionId,
        Project = _project,
        Input = Number(usage, "input_tokens"),
        CacheWrite = Number(usage, "cache_creation_input_tokens"),
        CacheRead = Number(usage, "cache_read_input_tokens"),
        Output = Number(usage, "output_tokens"),
        CostUsd = _provider is "gemini" ? 0 : UsagePricing.Estimate(model, usage, tier),
    };

    private string ResolveModel(JsonNode? envelope)
    {
        var model = UsagePricing.Text(envelope?["model"]);
        if (string.IsNullOrWhiteSpace(model) && envelope?["modelUsage"] is JsonObject { Count: 1 } byModel) model = byModel.First().Key;
        if (string.IsNullOrWhiteSpace(model)) model = _observedModel ?? _selectedModel;
        if (string.IsNullOrWhiteSpace(model) || model == "default")
            model = _models.OfType<JsonObject>().Where(m => UsagePricing.Text(m["value"]) == "default")
                .Select(m => UsagePricing.Text(m["resolvedModel"])).FirstOrDefault(m => !string.IsNullOrWhiteSpace(m) && m != "default");
        return string.IsNullOrWhiteSpace(model) ? "unknown" : ModelPricing.CanonicalId(model);
    }

    private void Changed()
    {
        PublishTelemetry();
        _changed?.Invoke(_completed + Summarize(_live));
    }

    private void PublishTelemetry()
    {
        if (_live is not { Total: > 0 } live) { LiveTurnTelemetry.Instance.Clear(this); return; }
        LiveTurnTelemetry.Instance.Report(this, _provider, live.Model, live.Input, live.CacheWrite, live.CacheRead,
            live.Output, _sessionId, _project, live.CostUsd);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            CommitUnfinished();
            _disposed = true;
            _pulse.Dispose();
            Changed();
        }
    }

    private static BridgeUsageSummary Summarize(UsageEntry? entry) => entry is null ? new()
        : new(entry.TotalIn, entry.Output, entry.CostUsd, !entry.CostReported && entry.CostUsd > 0);
    private static double Number(JsonNode? node, string field) => UsagePricing.Number(node?[field]) ?? 0;
    private static bool HasBuckets(JsonNode? node) => node is JsonObject obj &&
        new[] { "input_tokens", "cache_creation_input_tokens", "cache_read_input_tokens", "output_tokens" }.Any(obj.ContainsKey);

    private readonly record struct Tokens(double Input, double CacheWrite, double CacheRead, double Output)
    {
        public double Total => Input + CacheWrite + CacheRead + Output;
        public static Tokens Read(JsonNode? usage) => new(Number(usage, "input_tokens"), Number(usage, "cache_creation_input_tokens"),
            Number(usage, "cache_read_input_tokens"), Number(usage, "output_tokens"));
        public static Tokens From(UsageEntry entry) => new(entry.Input, entry.CacheWrite, entry.CacheRead, entry.Output);
        public static Tokens operator +(Tokens a, Tokens b) => new(a.Input + b.Input, a.CacheWrite + b.CacheWrite, a.CacheRead + b.CacheRead, a.Output + b.Output);
        public static Tokens operator -(Tokens a, Tokens b) => new(Math.Max(0, a.Input - b.Input), Math.Max(0, a.CacheWrite - b.CacheWrite),
            Math.Max(0, a.CacheRead - b.CacheRead), Math.Max(0, a.Output - b.Output));
        public JsonObject Json() => new()
        {
            ["input_tokens"] = Input,
            ["cache_creation_input_tokens"] = CacheWrite,
            ["cache_read_input_tokens"] = CacheRead,
            ["output_tokens"] = Output
        };
    }
}
