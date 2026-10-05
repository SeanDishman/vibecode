using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using VibeCode.Protocol;
using VibeCode.UI;

internal static class TokenRateTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int _checks;
public static void Run()
    {
        RollingWindows();
        Batched50k();
        LongBufferedReport();
        CorrectionsAndTurns();
        DirectionalRates();
        Console.WriteLine($"PASS: {_checks} rolling token-rate, 50k burst, separate read/write, reconciliation, expiry and pane isolation checks");
    }
    private static void RollingWindows()
    {
        var clock = new TestClock();
        var tracker = Tracker(clock);
        Expect(tracker, 0, 0, "new window");
        Call(tracker, "ObserveTurn", 100d);
        clock.Milliseconds = 900;
        Call(tracker, "ObserveTurn", 150d);
        clock.Milliseconds = 999;
        Expect(tracker, 150, 150, "both increments within a second");
        clock.Milliseconds = 1000;
        Expect(tracker, 150, 150, "one-second boundary does not erase the rate");
        Call(tracker, "ObserveTurn", 150d);
        clock.Milliseconds = 1900;
        Expect(tracker, 150 / 1.9, 150, "duplicate snapshot does not renew a sample");
        clock.Milliseconds = 60000;
        Expect(tracker, 50d / 60, 50, "exact minute boundary");
        clock.Milliseconds = 60900;
        Expect(tracker, 0, 0, "idle minute expires");
        Call(tracker, "ObserveTurn", 170d);
        Expect(tracker, 20d / 60, 20, "long turn retains its baseline after samples expire");
        foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, -1 })
            Call(tracker, "ObserveTurn", invalid);
        Expect(tracker, 20d / 60, 20, "invalid values cannot poison rates");
    }

    private static void Batched50k()
    {
        var clock = new TestClock();
        var tracker = Tracker(clock);
        Call(tracker, "StartTurn");
        clock.Milliseconds = 20000;
        Call(tracker, "ObserveTurn", 20000d);
        Expect(tracker, 1000, 20000, "20k tokens over 20 seconds is 1k tok/s");
        clock.Milliseconds = 21000;
        Expect(tracker, 20000d / 21, 20000, "rate remains visible between buffered reports");
        clock.Milliseconds = 40000;
        Call(tracker, "ObserveTurn", 40000d);
        Expect(tracker, 1000, 40000, "second 20k batch has no arrival-second spike");
        clock.Milliseconds = 50000;
        Call(tracker, "ObserveTurn", 50000d);
        Expect(tracker, 1000, 50000, "50k-token simulation ends at 1k tok/s");
        Call(tracker, "ObserveTurn", 50000d);
        Expect(tracker, 1000, 50000, "duplicate completion adds no tokens");
        clock.Milliseconds = 70000;
        Expect(tracker, 40000d / 60, 40000, "first batch ages proportionally across its 20-second interval");
        clock.Milliseconds = 110000;
        Expect(tracker, 0, 0, "completed simulation expires after a full idle minute");
        Call(tracker, "StartTurn");
        clock.Milliseconds = 130000;
        Call(tracker, "ObserveTurn", 20000d);
        Expect(tracker, 1000, 20000, "new prompt excludes preceding idle time");
    }

    private static void LongBufferedReport()
    {
        var clock = new TestClock();
        var buffered = Tracker(clock);
        var streamed = Tracker(clock);
        for (var second = 1; second <= 120; second++)
        {
            clock.Milliseconds = second * 1000;
            Call(streamed, "ObserveEstimatedTurn", second * 1000d);
        }
        Call(buffered, "ObserveTurn", 120000d);
        Call(streamed, "ObserveTurn", 120000d);
        Expect(buffered, 1000, 60000, "two-minute final-only report is spread across its full duration");
        Expect(streamed, 1000, 60000, "streamed and buffered deliveries agree for the same generation rate");
        clock.Milliseconds = 150000;
        Expect(buffered, 500, 30000, "long buffered interval ages smoothly");
        Expect(streamed, 500, 30000, "streamed interval ages at the same speed");

        var delayedClock = new TestClock();
        var delayed = Tracker(delayedClock);
        for (var second = 1; second <= 20; second++)
        {
            delayedClock.Milliseconds = second * 1000;
            Call(delayed, "ObserveEstimatedTurn", second * 10d);
        }
        Call(delayed, "ObserveTurn", 20000d);
        Expect(delayed, 1000, 20000, "final input usage uses the report clock instead of the latest text chunk");
        delayedClock.Milliseconds = 70000;
        Expect(delayed, 10000d / 60, 10000, "final input and streamed output age over their generation intervals");
    }

    private static void CorrectionsAndTurns()
    {
        var clock = new TestClock();
        var tracker = Tracker(clock);
        Call(tracker, "ObserveTurn", 100d);
        Call(tracker, "StartTurn");
        clock.Milliseconds = 1000;
        Call(tracker, "ObserveTurn", 50d);
        Call(tracker, "ObserveTurn", 30d);
        Expect(tracker, 130, 130, "authoritative correction removes only the current turn's excess");
        Call(tracker, "ObserveTurn", 0d);
        Expect(tracker, 100, 100, "correction cannot remove a preceding turn");
        Call(tracker, "ObserveTurn", 40d);
        Expect(tracker, 140, 140, "growth after correction uses the corrected baseline");
        Call(tracker, "StartTurn");
        Call(tracker, "ObserveTurn", 7d);
        Expect(tracker, 147, 147, "new turns preserve the trailing minute");
    }

    private static void Chunk(KimiSession protocol, string kind, int characters) =>
        Call(protocol, "TranslateSessionUpdate", new JsonObject
        {
            ["sessionUpdate"] = kind,
            ["content"] = new JsonObject { ["type"] = "text", ["text"] = new string('x', characters) },
        });

    private static object Tracker(TestClock clock) => Activator.CreateInstance(
        typeof(ChatViewModel).Assembly.GetType("VibeCode.Services.RollingTokenUsage", throwOnError: true)!,
        new object[] { clock })!;

    private static void DirectionalRates()
    {
        var clock = new TestClock();
        var chat = Chat(clock, "codex");
        var sibling = Chat(new TestClock(), "codex");
        try
        {
            Check(chat.TokenRatesText.Length == 0 && !chat.HasTokenRates, "empty rate rows stay hidden");
            chat.Status = "running";
            Call(chat, "BeginTokenUsageTiming");
            clock.Milliseconds = 10000;
            Usage(chat, 100, 200, 40);
            Check(chat.TokenRatesText == "read 30/s · 300/min\nwrite 4/s · 40/min", "input, cache and output are displayed in separate rows");
            Check(sibling.TokenRatesText.Length == 0, "usage remains isolated to its own pane");

            Call(chat, "EstimateStreamingTokenRate", new string('x', 40));
            Check(chat.TokenRatesText.Contains("write ~") && !chat.TokenRatesText.Contains("read ~"), "streaming estimates affect only generated output");
            clock.Milliseconds = 10500;
            Usage(chat, 100, 200, 50);
            Check(!chat.TokenRatesText.Contains('~'), "an authoritative report reconciles output estimates");
            Result(chat, 100, 200, 50);
            Check(chat.TotalTokens == 350 && !chat.HasTokenRates, "completed totals are exact and finished rate rows are hidden");
            Check(Rates(chat, "_readTokenUsageRates").Item2 == 300 && Rates(chat, "_writeTokenUsageRates").Item2 == 50,
                "the final report does not count provisional or reported usage twice");

            chat.Status = "running";
            Call(chat, "ResetTokenUsageTurn");
            clock.Milliseconds = 11000;
            Usage(chat, 5, 0, 10);
            Result(chat, 5, 0, 15);
            Check(chat.TotalTokens == 370, "a later turn commits only its own final consumption");
            Check(Rates(chat, "_readTokenUsageRates").Item2 == 305 && Rates(chat, "_writeTokenUsageRates").Item2 == 65,
                "both directions retain independent trailing-minute totals across turns");

            chat.Status = "running";
            clock.Milliseconds = 12000;
            Usage(chat, 1, 0, 1);
            Check(Timer(chat).IsEnabled, "active usage starts the display timer");
            clock.Milliseconds = 73000;
            PumpTimer();
            Check(chat.TokenRatesText.Length == 0 && !Timer(chat).IsEnabled, "expired usage hides both rows and releases the timer");
        }
        finally { chat.Close(); sibling.Close(); }
        Check(!Timer(chat).IsEnabled, "closing a pane releases the rate timer");
    }

    private static (double, double) Rates(ChatViewModel chat, string field) =>
        ((double, double))Call(typeof(ChatViewModel).GetField(field, Hidden)!.GetValue(chat)!, "Read")!;

    private static ChatViewModel Chat(TestClock clock, string provider)
    {
        var chat = new ChatViewModel(Environment.CurrentDirectory, provider: provider, accountId: "token-rate-simulation");
        typeof(ChatViewModel).GetField("_readTokenUsageRates", Hidden)!.SetValue(chat, Tracker(clock));
        typeof(ChatViewModel).GetField("_writeTokenUsageRates", Hidden)!.SetValue(chat, Tracker(clock));
        return chat;
    }

    private static JsonObject Bucket(long input, long cached, long output) => new()
    {
        ["input_tokens"] = input, ["cache_read_input_tokens"] = cached, ["output_tokens"] = output,
    };

    private static void Usage(ChatViewModel chat, long input, long cached, long output) =>
        Call(chat, "IngestSdk", new JsonObject
        {
            ["type"] = "system", ["subtype"] = "usage_update", ["usage"] = Bucket(input, cached, output),
        });

    private static void Result(ChatViewModel chat, long input, long cached, long output) =>
        Call(chat, "IngestSdk", new JsonObject
        {
            ["type"] = "result", ["subtype"] = "success", ["usage"] = Bucket(input, cached, output),
        });

    private static DispatcherTimer Timer(ChatViewModel chat) =>
        (DispatcherTimer)typeof(ChatViewModel).GetField("_tokenRateTimer", Hidden)!.GetValue(chat)!;

    private static void PumpTimer()
    {
        var frame = new DispatcherFrame();
        var stop = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMilliseconds(350) };
        stop.Tick += (_, _) => { stop.Stop(); frame.Continue = false; };
        stop.Start();
        Dispatcher.PushFrame(frame);
    }

    private static object? Call(object target, string method, params object?[] arguments) =>
        target.GetType().GetMethod(method, Hidden | BindingFlags.Public)!.Invoke(target, arguments);

    private static void Expect(object tracker, double second, double minute, string label)
    {
        var actual = ((double, double))Call(tracker, "Read")!;
        Check(Math.Abs(actual.Item1 - second) < 0.000001 && Math.Abs(actual.Item2 - minute) < 0.000001,
            $"{label}: expected {second}/{minute}, received {actual}");
    }

    private static void Check(bool condition, string message)
    {
        _checks++;
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class TestClock : TimeProvider
    {
        public long Milliseconds { get; set; }
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Milliseconds;
    }
}
