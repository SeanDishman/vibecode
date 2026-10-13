using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using VibeCode.Services;

namespace VibeCode.UI;

/// <summary>A display of the existing planning session, never a second session or a roster member.</summary>
public sealed class CentralPlannerActivity : Observable, IDisposable
{
    private const int TextLimit = 262_144;
    private readonly object _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _timer;
    private readonly List<Block> _blocks = [];
    private readonly Dictionary<int, Block> _stream = [];
    private readonly Queue<string> _log = [];
    private string _stage = "Connecting to provider…";
    private bool _dirty = true, _disposed;
    private long _thinkingTokens;
    private int _characters;
    private string _status = "Connecting to provider…", _thinking = "", _output = "", _events = "", _elapsed = "0s";

    public CentralPlannerActivity(BridgeAgentConfiguration configuration)
    {
        var model = ProviderModelCatalog.For(configuration.Provider).FirstOrDefault(m =>
            m.Value == configuration.Model || m.ResolvedModel == configuration.Model)?.ShortName ?? configuration.Model ?? "Default model";
        Configuration = $"{ProviderModelCatalog.DisplayName(configuration.Provider)} · {model} · {configuration.Effort ?? "default"} effort" +
            (configuration.UltrafastMode ? " · Ultrafast mode" : configuration.FastMode == true ? " · Fast mode" : "");
        _log.Enqueue("0s  Connecting to provider");
        // Provider callbacks only update bounded buffers. One UI timer publishes snapshots; no per-token
        // dispatcher work or retained JSON event queue, even while this bridge is hidden.
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    public string Configuration { get; }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string Thinking { get => _thinking; private set { if (Set(ref _thinking, value)) Raise(nameof(HasThinking)); } }
    public string Output { get => _output; private set { if (Set(ref _output, value)) Raise(nameof(HasOutput)); } }
    public string Events { get => _events; private set => Set(ref _events, value); }
    public string Elapsed { get => _elapsed; private set => Set(ref _elapsed, value); }
    public bool HasThinking => Thinking.Length > 0;
    public bool HasOutput => Output.Length > 0;
    public bool IsDisposed => _disposed;

    public void SetStage(string stage)
    {
        lock (_gate)
        {
            if (_disposed || stage == _stage) return;
            _stage = stage;
            _log.Enqueue($"{Age()}  {stage}");
            while (_log.Count > 64) _log.Dequeue();
            _dirty = true;
        }
    }

    public void Observe(JsonNode node)
    {
        lock (_gate)
        {
            if (_disposed) return;
            var type = node["type"]?.ToString();
            if (type == "stream_event" && node["event"] is { } ev)
            {
                var index = ev["index"] is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;
                switch (ev["type"]?.ToString())
                {
                    case "message_start":
                        foreach (var earlier in _blocks) earlier.Final = true;
                        _stream.Clear();
                        break;
                    case "content_block_start":
                        var kind = ev["content_block"]?["type"]?.ToString();
                        if (kind is "thinking" or "text" && NewBlock(kind) is { } block)
                        {
                            _stream[index] = block;
                            Write(block, ev["content_block"]?[kind]?.ToString() ?? "", false);
                        }
                        break;
                    case "content_block_delta":
                        if (_stream.TryGetValue(index, out var active))
                            Write(active, ev["delta"]?[active.Kind]?.ToString() ?? "", false);
                        break;
                }
            }
            else if (type == "assistant" && node["message"]?["content"] is JsonArray content)
            {
                foreach (var block in content.OfType<JsonObject>())
                {
                    var kind = block["type"]?.ToString();
                    if (kind is not ("text" or "thinking")) continue;
                    var text = block[kind]?.ToString() ?? "";
                    // Claude sends a full message snapshot; Codex finalizes items one at a time. Match each
                    // unfinalized block of the same kind so neither protocol repeats streamed text.
                    var target = _blocks.FirstOrDefault(b => b.Kind == kind && !b.Final) ?? NewBlock(kind);
                    if (target is null) continue;
                    if (text.Length > 0) Write(target, text, true);
                    target.Final = true;
                }
            }
            else if (type == "system" && node["subtype"]?.ToString() == "thinking_tokens")
            {
                if (node["estimated_tokens"] is JsonValue value && value.TryGetValue<long>(out var tokens))
                    _thinkingTokens = tokens;
                SetStage("Thinking…");
                _dirty = true;
            }
            else if (type == "result" && node["result"]?.ToString() is { Length: > 0 } result &&
                     !_blocks.Any(b => b.Kind == "text" && b.Text.Length > 0))
            {
                if (NewBlock("text") is { } block) Write(block, result, false);
            }
        }
    }

    private Block? NewBlock(string kind)
    {
        if (_blocks.Count >= 128) return null;
        var block = new Block(kind);
        _blocks.Add(block);
        return block;
    }

    private void Write(Block block, string text, bool replace)
    {
        if (text.Length == 0) return;
        if (replace) { _characters -= block.Text.Length; block.Text.Clear(); }
        var remaining = TextLimit - _characters;
        if (remaining <= 0) return;
        var length = Math.Min(remaining, text.Length);
        block.Text.Append(text.AsSpan(0, length));
        _characters += length;
        SetStage(block.Kind == "thinking" ? "Thinking…" : "Writing assignments…");
        _dirty = true;
    }

    private string Age() => _clock.Elapsed.TotalMinutes < 1 ? $"{(int)_clock.Elapsed.TotalSeconds}s"
        : $"{(int)_clock.Elapsed.TotalMinutes}m {_clock.Elapsed.Seconds:00}s";

    private void OnTick(object? sender, EventArgs e) => Flush();

    public void Flush()
    {
        _timer.Dispatcher.VerifyAccess();
        lock (_gate)
        {
            Elapsed = Age();
            if (!_dirty) return;
            _dirty = false;
            Status = _stage + (_stage == "Thinking…" && _thinkingTokens > 0 ? $"  ~{_thinkingTokens:N0} tokens" : "");
            Thinking = string.Join("\n\n", _blocks.Where(b => b.Kind == "thinking").Select(b => b.Text.ToString()));
            Output = string.Join("\n\n", _blocks.Where(b => b.Kind == "text").Select(b => b.Text.ToString()));
            Events = string.Join("\n", _log) + (_characters >= TextLimit ? "\nDisplay limit reached; planning continues." : "");
        }
    }

    public string Transcript()
    {
        Flush();
        return Configuration + "\n\n" + Events + (HasThinking ? "\n\nThinking\n" + Thinking : "") +
            (HasOutput ? "\n\nOutput\n" + Output : "");
    }

    public void Dispose()
    {
        _timer.Dispatcher.VerifyAccess();
        lock (_gate) { if (_disposed) return; _disposed = true; }
        _timer.Stop();
        _timer.Tick -= OnTick;
        _clock.Stop();
        Flush();
        lock (_gate) { _blocks.Clear(); _stream.Clear(); _log.Clear(); }
    }

    private sealed class Block(string kind)
    {
        public string Kind { get; } = kind;
        public StringBuilder Text { get; } = new();
        public bool Final { get; set; }
    }
}
