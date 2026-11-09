using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Windows.Threading;

namespace VibeCode.UI;

/// <summary>Ordered provider events, with bounded work between opportunities for input and speech delivery.</summary>
internal sealed class SessionEventPump(Dispatcher ui, Func<bool> isCurrent, Action<List<JsonNode>> ingest)
{
    private readonly ConcurrentQueue<(JsonNode? Message, Action? Action)> _pending = new();
    private int _scheduled;
    private const int BatchSize = 32;

    public void Message(JsonNode message) { _pending.Enqueue((message, null)); Schedule(); }
    public void Action(Action action) { _pending.Enqueue((null, action)); Schedule(); }

    private void Schedule()
    {
        if (Interlocked.CompareExchange(ref _scheduled, 1, 0) == 0)
            ui.BeginInvoke(DispatcherPriority.Background, new Action(Drain));
    }

    private void Drain()
    {
        try
        {
            var timer = Stopwatch.StartNew();
            do
            {
                if (!isCurrent()) { _pending.Clear(); return; }
                var batch = new List<JsonNode>(BatchSize);
                while (batch.Count < BatchSize && _pending.TryPeek(out var next) && next.Message is not null)
                {
                    if (_pending.TryDequeue(out next)) batch.Add(next.Message!);
                }
                if (batch.Count > 0) ingest(batch);
                // Initialization, permission and exit notifications stay behind earlier messages, even when a
                // large history spans multiple dispatcher turns. Otherwise an exit could settle a partial turn.
                if (_pending.TryPeek(out var control) && control.Action is not null && _pending.TryDequeue(out control))
                    control.Action!();
            } while (!_pending.IsEmpty && timer.ElapsedMilliseconds < 8);
        }
        finally
        {
            Volatile.Write(ref _scheduled, 0);
            if (!_pending.IsEmpty) Schedule();
        }
    }
}
