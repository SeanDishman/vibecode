using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using VibeCode.UI;

namespace VibeCode.Services;

/// <summary>
/// One model's live figures on the telemetry wall's speed board.
/// </summary>
public sealed class ModelSpeedRow : INotifyPropertyChanged
{
    public ModelSpeedRow(string displayName, string vendor, string? openRouterId, string paletteId)
    {
        DisplayName = displayName;
        Vendor = vendor;
        OpenRouterId = openRouterId;
        Key = openRouterId ?? vendor + ":" + paletteId;
        Swatch = UsagePalette.BrushFor(paletteId);
    }

    public string DisplayName { get; }
    /// <summary>Who makes it — the board groups visually by this, and two vendors ship a "5" model each.</summary>
    public string Vendor { get; }
    public string? OpenRouterId { get; }
    internal string Key { get; }
    /// <summary>The same colour this model gets in the donut and the by-model list, so one model reads as one
    /// colour everywhere on the wall.</summary>
    public Brush Swatch { get; }

    private string _throughput = "—";
    public string Throughput { get => _throughput; private set => Set(ref _throughput, value); }

    private string _latency = "";
    public string Latency { get => _latency; private set => Set(ref _latency, value); }

    private bool _hasData;
    /// <summary>False until a reading lands. Drives the "no data" styling rather than printing a fake zero.</summary>
    public bool HasData { get => _hasData; private set => Set(ref _hasData, value); }

    /// <summary>Fold a reading (or the absence of one) into this row.</summary>
    internal void Apply(ModelSpeed? speed)
    {
        if (speed is not { } s)
        {
            // Keep the last good reading rather than blanking the tile on one failed poll: a wall that flickers to
            // "—" every time OpenRouter rate-limits reads as broken.
            if (!HasData) { Throughput = "—"; Latency = "no data"; }
            return;
        }
        Throughput = s.TokensPerSecond.ToString("0");
        Latency = $"{s.LatencySeconds:0.0}s latency";
        HasData = true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set(ref string field, string value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (field == value) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private void Set(ref bool field, bool value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (field == value) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>
/// The telemetry wall's model speed board: the user's most-used models over the last 30 days, with throughput
/// and latency polled from OpenRouter's public endpoint stats on a slow cycle.
///
/// This is deliberately NOT scoped by the wall's range strip. Every other panel there is a window over THIS
/// machine's own history; selection here always uses 30 days of local tokens, while the speeds describe
/// OpenRouter's last 30 minutes of traffic. Local usage selects the models, not their measured speeds.
///
/// The fetch itself is <see cref="ModelSpeedService"/>, the same code that fills the model picker's speed line, so
/// the wall and the picker can never quote different numbers for the same model.
/// </summary>
public sealed class ModelSpeedBoard
{
    public static ModelSpeedBoard Instance { get; } = new();

    /// <summary>How often the board re-polls. Slow on purpose: these are 30-minute medians over other people's
    /// traffic, so a faster cycle would spend requests to redraw the same numbers.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);

    public const int MaxRows = 8;
    public static readonly TimeSpan UsagePeriod = TimeSpan.FromDays(30);

    /// <summary>Rank actual, completed usage by all input/cache/output tokens. Collapse aliases and prompt
    /// wrappers onto the same backend, without substituting a newer model for an older model actually used.</summary>
    public static ModelSpeedRow[] SelectRows(IEnumerable<UsageEntry> entries, DateTimeOffset now)
    {
        var groups = new Dictionary<string, (ModelSpeedRow Row, double Tokens, DateTimeOffset LastUsed)>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (entry.At < now - UsagePeriod || entry.At > now || !double.IsFinite(entry.Total) || entry.Total <= 0) continue;
            var raw = ModelPricing.CanonicalId(entry.Model);
            if (raw.ToLowerInvariant() is "unknown" or "auto" or "default") continue;
            var id = ModelSpeedService.OpenRouterId(raw, entry.Provider);
            var paletteId = id is null ? raw : id[(id.IndexOf('/') + 1)..];
            if (paletteId.StartsWith("claude-", StringComparison.Ordinal))
                paletteId = Regex.Replace(paletteId, @"(?<=\d)\.(?=\d)", "-");
            var vendor = (id?.Split('/')[0]) switch
            {
                "anthropic" => "Anthropic", "openai" => "OpenAI", "x-ai" => "xAI",
                "z-ai" => "Z.ai", "moonshotai" => "Moonshot", "google" => "Google",
                _ => ProviderModelCatalog.DisplayName(entry.Provider),
            };
            var key = id ?? vendor + ":" + paletteId;
            if (groups.TryGetValue(key, out var group))
                groups[key] = (group.Row, group.Tokens + entry.Total, entry.At > group.LastUsed ? entry.At : group.LastUsed);
            else
                groups[key] = (new ModelSpeedRow(UsagePalette.DisplayName(paletteId), vendor, id, paletteId), entry.Total, entry.At);
        }
        return groups.Values.OrderByDescending(g => g.Tokens).ThenByDescending(g => g.LastUsed)
            .ThenBy(g => g.Row.Key, StringComparer.OrdinalIgnoreCase).Take(MaxRows).Select(g => g.Row).ToArray();
    }

    private readonly ObservableCollection<ModelSpeedRow> _board = new();

    private ModelSpeedBoard()
    {
        Rows = new ReadOnlyObservableCollection<ModelSpeedRow>(_board);
    }

    public ReadOnlyObservableCollection<ModelSpeedRow> Rows { get; }

    /// <summary>When the last poll landed, for the board's own caption. Null until the first one does.</summary>
    public DateTime? LastUpdated { get; private set; }

    /// <summary>Raised on the UI thread whenever readings change.</summary>
    public event Action? Updated;

    private DispatcherTimer? _timer;
    private int _watchers;
    private bool _hooked;

    /// <summary>
    /// Start polling while the returned handle lives. Polling is tied to a visible board rather than running for
    /// the life of the app: nothing else reads these figures, so a closed wall polling OpenRouter every five
    /// minutes forever would be pure waste.
    /// </summary>
    public IDisposable Watch()
    {
        if (++_watchers == 1)
        {
            if (!_hooked)
            {
                ModelSpeedService.Instance.Updated += OnSpeedsUpdated;
                UsageLog.Instance.Changed += OnUsageChanged;
                _hooked = true;
            }
            _timer ??= new DispatcherTimer { Interval = RefreshInterval };
            _timer.Tick -= OnTick;
            _timer.Tick += OnTick;
            _timer.Start();
            Refresh();   // don't make the first board wait five minutes for its first figures
        }
        return new Watcher(this);
    }

    private void Release()
    {
        if (--_watchers > 0) return;
        _watchers = 0;
        _timer?.Stop();
        ModelSpeedService.Instance.Updated -= OnSpeedsUpdated;
        UsageLog.Instance.Changed -= OnUsageChanged;
        _hooked = false;
    }

    private void OnTick(object? sender, EventArgs e) => Refresh();

    private void OnUsageChanged() => Application.Current?.Dispatcher.BeginInvoke(() =>
    {
        if (_watchers > 0) Refresh();
    });

    private void UpdateSelection()
    {
        var selected = SelectRows(UsageLog.Instance.Entries(), DateTimeOffset.Now);
        var existing = _board.ToDictionary(r => r.Key, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < selected.Length; i++)
        {
            if (existing.TryGetValue(selected[i].Key, out var row))
            {
                var oldIndex = _board.IndexOf(row);
                if (oldIndex != i) _board.Move(oldIndex, i);
            }
            else _board.Insert(i, selected[i]);
        }
        while (_board.Count > selected.Length) _board.RemoveAt(_board.Count - 1);
    }

    /// <summary>Poll every model on the board. Cheap when nothing has expired — the underlying cache skips those.</summary>
    public void Refresh()
    {
        UpdateSelection();
        // Just under the cycle: a cache entry written by the previous tick must be considered stale by this one, or
        // every poll after the first would be skipped and the board would freeze on its opening numbers.
        var maxAge = RefreshInterval - TimeSpan.FromSeconds(30);
        ModelSpeedService.Instance.PrefetchIds(_board.Select(r => r.OpenRouterId).OfType<string>(), maxAge);
        Apply();
        Updated?.Invoke();
    }

    private void OnSpeedsUpdated()
    {
        Apply();
        Updated?.Invoke();
    }

    private void Apply()
    {
        DateTime? newest = null;
        foreach (var row in _board)
        {
            row.Apply(ModelSpeedService.Instance.ById(row.OpenRouterId));
            if (ModelSpeedService.Instance.FetchedAt(row.OpenRouterId) is { } at
                && (newest is null || at > newest)) newest = at;
        }
        LastUpdated = newest;
    }

    private sealed class Watcher : IDisposable
    {
        private ModelSpeedBoard? _board;
        public Watcher(ModelSpeedBoard board) => _board = board;
        public void Dispose()
        {
            var board = Interlocked.Exchange(ref _board, null);
            board?.Release();
        }
    }
}
