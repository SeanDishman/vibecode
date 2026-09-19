namespace VibeCode.Services;

/// <summary>Usage averaged over up to 60 seconds. Each batch is spread over its reporting interval instead
/// of being charged to its arrival second. Accessed on the chat's UI thread.</summary>
internal sealed class RollingTokenUsage
{
    private readonly TimeProvider _clock;
    private readonly LinkedList<Sample> _samples = new();
    private double _turnTotal;
    private long _turn;
    private long _windowStarted;
    private long _reportedAt;
    private long _estimatedAt;

    private readonly record struct Sample(long Started, long Ended, double Tokens, long Turn, bool Estimated);

    public RollingTokenUsage(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _windowStarted = _reportedAt = _estimatedAt = _clock.GetTimestamp();
    }

    public void ObserveTurn(double total) => Observe(total, estimated: false);

    public void ObserveEstimatedTurn(double total) => Observe(total, estimated: true);

    private void Observe(double total, bool estimated)
    {
        if (!double.IsFinite(total) || total < 0) return;
        var now = _clock.GetTimestamp();
        Prune(now);
        // A real cumulative snapshot confirms the earlier provisional samples without moving their
        // timestamps. Otherwise a delayed final report would bill the streamed output a second time.
        if (!estimated)
            for (var node = _samples.Last; node is not null && node.Value.Turn == _turn; node = node.Previous)
                node.Value = node.Value with { Estimated = false };
        var delta = total - _turnTotal;
        _turnTotal = total;
        if (delta > 0)
        {
            // Streaming estimates and real usage have separate clocks. A late final report includes input
            // tokens used throughout the request; the last text chunk must not squeeze them into a few ms.
            var started = estimated ? _estimatedAt : _reportedAt;
            _samples.AddLast(new Sample(started, now, delta, _turn, estimated));
        }
        else if (delta < 0)
        {
            // An authoritative result can correct a streaming estimate downward. Remove the excess from
            // this turn's newest samples, without subtracting any tokens from an earlier turn.
            var excess = -delta;
            while (excess > 0 && _samples.Last is { } node && node.Value.Turn == _turn)
            {
                var removed = Math.Min(excess, node.Value.Tokens);
                excess -= removed;
                if (removed == node.Value.Tokens) _samples.RemoveLast();
                else node.Value = node.Value with { Tokens = node.Value.Tokens - removed };
            }
        }
        if (!estimated) _reportedAt = now;
        _estimatedAt = now;
    }

    public void StartTurn()
    {
        _turnTotal = 0;
        _turn++;
        BeginTiming();
    }

    // Called when the prompt is dispatched, including after a long idle/startup. Moving to "running" again
    // inside a turn must not reset its cumulative baseline or shorten an in-flight reporting interval.
    public void BeginTiming()
    {
        if (_turnTotal != 0) return;
        var now = _clock.GetTimestamp();
        Prune(now);
        _reportedAt = _estimatedAt = now;
        if (_samples.Count == 0) _windowStarted = now;
    }

    public (double PerSecond, double LastMinute) Read()
    {
        var (second, minute, _, _) = ReadWithEstimates();
        return (second, minute);
    }

    public (double PerSecond, double LastMinute, bool SecondEstimated, bool MinuteEstimated) ReadWithEstimates()
    {
        var now = _clock.GetTimestamp();
        Prune(now);
        double minute = 0;
        bool estimated = false;
        foreach (var sample in _samples)
        {
            var duration = _clock.GetElapsedTime(sample.Started, sample.Ended).TotalSeconds;
            var age = _clock.GetElapsedTime(sample.Ended, now).TotalSeconds;
            // Only the part of an interval overlapping the last minute belongs in this window. Long
            // buffered responses then have the same rate whether delivered in one batch or many chunks.
            var fraction = duration > 0 ? Math.Clamp((60 - age) / duration, 0, 1) : 1;
            minute += sample.Tokens * fraction;
            estimated |= sample.Estimated && fraction > 0;
        }
        // Warm up from dispatch instead of dividing the first batch by a full minute. Use at least one
        // second so same-tick updates cannot produce an unbounded rate. Idle time naturally ages the rate.
        var seconds = Math.Clamp(_clock.GetElapsedTime(_windowStarted, now).TotalSeconds, 1, 60);
        return (minute / seconds, minute, estimated, estimated);
    }

    private void Prune(long now)
    {
        while (_samples.First is { } node
               && _clock.GetElapsedTime(node.Value.Ended, now) >= TimeSpan.FromMinutes(1))
            _samples.RemoveFirst();
    }
}
