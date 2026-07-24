using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VibeCode.Services;

/// <summary>
/// One committed model turn: which model burned how many tokens, split by cache tier, and what those tokens
/// are worth at list price. Property names are deliberately short - this is a line in an append-only file that
/// grows with every turn the user ever takes.
/// </summary>
public sealed class UsageEntry
{
    [JsonPropertyName("t")] public DateTimeOffset At { get; set; }
    [JsonPropertyName("p")] public string Provider { get; set; } = "";
    /// <summary>Resolved model id (e.g. "claude-opus-5"), already stripped of any "[1m]" variant tag.</summary>
    [JsonPropertyName("m")] public string Model { get; set; } = "";
    [JsonPropertyName("i")] public double Input { get; set; }
    [JsonPropertyName("cw")] public double CacheWrite { get; set; }
    [JsonPropertyName("cr")] public double CacheRead { get; set; }
    [JsonPropertyName("o")] public double Output { get; set; }
    /// <summary>USD this turn is worth. Provider-reported when the provider bills per call, otherwise the
    /// <see cref="ModelPricing"/> list-price estimate.</summary>
    [JsonPropertyName("u")] public double CostUsd { get; set; }
    /// <summary>True when <see cref="CostUsd"/> came from the provider rather than the list-price estimate.</summary>
    [JsonPropertyName("r")] public bool CostReported { get; set; }
    [JsonPropertyName("s")] public string? SessionId { get; set; }
    [JsonPropertyName("d")] public string? Project { get; set; }

    /// <summary>Every input-side token, whatever cache tier it was billed at.</summary>
    [JsonIgnore] public double TotalIn => Input + CacheWrite + CacheRead;
    [JsonIgnore] public double Total => TotalIn + Output;
}

/// <summary>
/// The permanent record of every model call VibeCode has made: one JSON line per committed turn in
/// <c>%APPDATA%\VibeCode\usage-log.jsonl</c>.
///
/// Deliberately NOT part of settings.json. That file is rewritten whole on every save and three-way merged
/// against disk, so appending thousands of turn rows to it would make each background token refresh
/// re-serialize the entire history. An append-only sidecar file costs one short line per turn instead, and a
/// corrupt line can be skipped without taking the user's settings down with it.
/// </summary>
public sealed class UsageLog
{
    public static UsageLog Instance { get; } = new();

    /// <summary>Turns older than this are dropped the next time the log is loaded. Roughly a year of history:
    /// long enough for "what did I spend this year", short enough that the file never becomes unbounded.</summary>
    public const int RetentionDays = 400;

    /// <summary>Hard ceiling on rows held in memory (and rewritten to disk), even inside the retention window.
    /// A very heavy user with a bridge full of agents can log a lot of turns; the oldest go first.</summary>
    public const int MaxEntries = 250_000;

    public static string FilePath => Path.Combine(AppSettings.Dir, "usage-log.jsonl");

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly object _gate = new();
    private readonly List<UsageEntry> _entries = new();
    private readonly Queue<string> _pending = new();
    private bool _loaded;
    private bool _flushing;

    /// <summary>Raised (on whatever thread committed the turn) after a turn is recorded or the log is cleared,
    /// so an open Usage page can refresh itself.</summary>
    public event Action? Changed;

    private UsageLog() { }

    /// <summary>Every retained turn, oldest first. A snapshot - safe to aggregate off the UI thread.</summary>
    public IReadOnlyList<UsageEntry> Entries()
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _entries.ToArray();
        }
    }

    public int Count
    {
        get { lock (_gate) { EnsureLoaded(); return _entries.Count; } }
    }

    /// <summary>Record one committed turn. Called once per turn from the chat that owns it; a turn with no
    /// tokens at all (an interrupt before the first request) is dropped rather than logged as a zero row.</summary>
    public void Record(UsageEntry entry)
    {
        if (entry.Total <= 0) return;
        if (string.IsNullOrWhiteSpace(entry.Model)) entry.Model = "unknown";
        if (string.IsNullOrWhiteSpace(entry.Provider)) entry.Provider = "claude";

        lock (_gate)
        {
            EnsureLoaded();
            _entries.Add(entry);
            TrimLocked();
            _pending.Enqueue(JsonSerializer.Serialize(entry, Json));
        }
        StartFlush();
        Changed?.Invoke();
    }

    /// <summary>Convenience overload for the call site that already has the raw usage buckets.</summary>
    public void Record(string provider, string? model, double input, double cacheWrite, double cacheRead,
        double output, double costUsd, bool costReported, string? sessionId, string? project) =>
        Record(new UsageEntry
        {
            At = DateTimeOffset.Now,
            Provider = provider,
            Model = ModelPricing.CanonicalId(model),
            Input = input,
            CacheWrite = cacheWrite,
            CacheRead = cacheRead,
            Output = output,
            CostUsd = costUsd,
            CostReported = costReported,
            SessionId = sessionId,
            Project = project,
        });

    /// <summary>Erase the whole history. Used by the "Clear usage history" button, which confirms first.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _pending.Clear();
            _loaded = true;   // nothing left to read back in
            try { if (File.Exists(FilePath)) File.Delete(FilePath); }
            catch { /* locked by a virus scanner or another window: the rewrite below still truncates it */ }
        }
        Changed?.Invoke();
    }

    /// <summary>Write the whole history out as CSV for a spreadsheet. Returns the row count written.</summary>
    public int ExportCsv(string path)
    {
        var rows = Entries();
        var sb = new StringBuilder();
        // energy_wh / water_litres are derived, not logged - see ModelEnergy. They are exported anyway so a
        // spreadsheet gets the same figures the usage page shows rather than having to re-derive them.
        sb.AppendLine("timestamp,provider,model,input_tokens,cache_write_tokens,cache_read_tokens,output_tokens,total_tokens,cost_usd,cost_source,energy_wh,water_litres,session_id,project");
        foreach (var e in rows)
            sb.Append(e.At.ToString("o")).Append(',')
              .Append(Csv(e.Provider)).Append(',')
              .Append(Csv(e.Model)).Append(',')
              .Append(e.Input.ToString("0")).Append(',')
              .Append(e.CacheWrite.ToString("0")).Append(',')
              .Append(e.CacheRead.ToString("0")).Append(',')
              .Append(e.Output.ToString("0")).Append(',')
              .Append(e.Total.ToString("0")).Append(',')
              .Append(e.CostUsd.ToString("0.########")).Append(',')
              .Append(e.CostReported ? "reported" : "estimated").Append(',')
              .Append(EnergyWh(e).ToString("0.######")).Append(',')
              .Append(ModelEnergy.OnSiteWaterLitres(EnergyWh(e)).ToString("0.########")).Append(',')
              .Append(Csv(e.SessionId)).Append(',')
              .Append(Csv(e.Project)).AppendLine();
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return rows.Count;
    }

    private static double EnergyWh(UsageEntry e) =>
        ModelEnergy.TurnEnergyWh(e.Model, e.Input, e.CacheWrite, e.CacheRead, e.Output);

    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0
            ? value
            : '"' + value.Replace("\"", "\"\"") + '"';
    }

    // ---------- persistence ----------

    /// <summary>Read the file once, on the first call that needs it. Always called under <see cref="_gate"/>.</summary>
    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;   // set first: a failed read must not retry on every single turn
        var cutoff = DateTimeOffset.Now.AddDays(-RetentionDays);
        var dropped = false;
        try
        {
            if (!File.Exists(FilePath)) return;
            foreach (var line in File.ReadLines(FilePath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                UsageEntry? entry;
                // One torn line (a crash mid-append) must cost one turn of history, not all of it.
                try { entry = JsonSerializer.Deserialize<UsageEntry>(line); }
                catch { dropped = true; continue; }
                if (entry is null || entry.Total <= 0) { dropped = true; continue; }
                if (entry.At < cutoff) { dropped = true; continue; }
                _entries.Add(entry);
            }
        }
        catch { /* unreadable: start from what we got rather than losing the ability to log new turns */ }

        _entries.Sort(static (a, b) => a.At.CompareTo(b.At));
        if (TrimLocked() || dropped) RewriteLocked();
    }

    /// <summary>Enforce <see cref="MaxEntries"/>. Returns true when rows were removed.</summary>
    private bool TrimLocked()
    {
        if (_entries.Count <= MaxEntries) return false;
        _entries.RemoveRange(0, _entries.Count - MaxEntries);
        return true;
    }

    /// <summary>Republish the retained rows, compacting away pruned/corrupt lines. Always called under the gate.</summary>
    private void RewriteLocked()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Dir);
            var tmp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var sb = new StringBuilder();
            foreach (var entry in _entries) sb.AppendLine(JsonSerializer.Serialize(entry, Json));
            File.WriteAllText(tmp, sb.ToString());
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { /* best effort - the in-memory history is still correct for this session */ }
    }

    /// <summary>Drain queued lines on a background thread so a turn commit never waits on disk.</summary>
    private void StartFlush()
    {
        lock (_gate)
        {
            if (_flushing || _pending.Count == 0) return;
            _flushing = true;
        }
        _ = Task.Run(Flush);
    }

    private void Flush()
    {
        while (true)
        {
            string batch;
            lock (_gate)
            {
                if (_pending.Count == 0) { _flushing = false; return; }
                var sb = new StringBuilder();
                while (_pending.Count > 0) sb.AppendLine(_pending.Dequeue());
                batch = sb.ToString();
            }
            try
            {
                Directory.CreateDirectory(AppSettings.Dir);
                File.AppendAllText(FilePath, batch);
            }
            catch
            {
                // Disk full, file locked, folder gone. The rows stay in memory for this session; dropping the
                // batch is better than spinning on a permanent failure and pinning a thread-pool thread.
                lock (_gate) { _flushing = false; }
                return;
            }
        }
    }
}
