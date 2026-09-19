using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace VibeCode.Services;

/// <summary>
/// One model's live figures on the telemetry wall's speed board.
/// </summary>
public sealed class ModelSpeedRow : INotifyPropertyChanged
{
    public ModelSpeedRow(string displayName, string vendor, string openRouterId, string paletteId)
    {
        DisplayName = displayName;
        Vendor = vendor;
        OpenRouterId = openRouterId;
        Swatch = UsagePalette.BrushFor(paletteId);
    }

    public string DisplayName { get; }
    /// <summary>Who makes it — the board groups visually by this, and two vendors ship a "5" model each.</summary>
    public string Vendor { get; }
    public string OpenRouterId { get; }
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
/// The telemetry wall's model speed board: throughput and latency for a fixed set of frontier models, polled from
/// OpenRouter's public endpoint stats on a slow cycle.
///
/// This is deliberately NOT scoped by the wall's range strip. Every other panel there is a window over THIS
/// machine's own history; these figures are the wider world's — OpenRouter's median over its last 30 minutes of
/// real traffic across every provider endpoint serving that model. Nothing local would change them, so a "last
/// 7 days" chip would be meaningless against them.
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

    /// <summary>
    /// The models on the board, in reading order. Ids verified against OpenRouter's catalogue rather than guessed —
    /// an id it does not carry silently yields no figures at all.
    ///
    /// Each row uses its provider's base model ID for measurements.
    ///
    /// A method rather than a static field on purpose. As a field it would have to be declared above
    /// <see cref="Instance"/> to be initialised first, and a later tidy-up that reordered the two would leave the
    /// singleton's constructor reading a null array — which is exactly what it did before this became a method.
    /// </summary>
    private static ModelSpeedRow[] CreateRows() =>
    [
        new("Opus 5",        "Anthropic", "anthropic/claude-opus-5",   "claude-opus-5"),
        new("Sonnet 5",      "Anthropic", "anthropic/claude-sonnet-5", "claude-sonnet-5"),
        new("Fable 5.1",     "Anthropic", "anthropic/claude-fable-5-1", "claude-fable-5-1"),
        new("Grok 4.5",      "xAI",       "x-ai/grok-4.5",             "grok-4.5"),
        // Astra shipped 2026-09-03 and OpenRouter had not listed openai/gpt-6-astra yet when this row was added,
        // so it reads "no data" until they do — Apply() keeps that tidy rather than printing a fake zero.
        new("GPT-6 Astra",   "OpenAI",    "openai/gpt-6-astra",        "gpt-6-astra"),
        new("GPT 5.6 Sol",   "OpenAI",    "openai/gpt-5.6-sol",        "gpt-5.6-sol"),
        new("GPT 5.6 Terra", "OpenAI",    "openai/gpt-5.6-terra",      "gpt-5.6-terra"),
        new("GPT 5.6 Luna",  "OpenAI",    "openai/gpt-5.6-luna",       "gpt-5.6-luna"),
    ];

    private readonly ModelSpeedRow[] _board;

    private ModelSpeedBoard()
    {
        _board = CreateRows();
        Rows = new ReadOnlyObservableCollection<ModelSpeedRow>(new ObservableCollection<ModelSpeedRow>(_board));
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
    }

    private void OnTick(object? sender, EventArgs e) => Refresh();

    /// <summary>Poll every model on the board. Cheap when nothing has expired — the underlying cache skips those.</summary>
    public void Refresh()
    {
        // Just under the cycle: a cache entry written by the previous tick must be considered stale by this one, or
        // every poll after the first would be skipped and the board would freeze on its opening numbers.
        var maxAge = RefreshInterval - TimeSpan.FromSeconds(30);
        ModelSpeedService.Instance.PrefetchIds(_board.Select(r => r.OpenRouterId), maxAge);
        Apply();
    }

    private void OnSpeedsUpdated()
    {
        Apply();
        Application.Current?.Dispatcher.BeginInvoke(() => Updated?.Invoke());
    }

    private void Apply()
    {
        var newest = LastUpdated;
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
