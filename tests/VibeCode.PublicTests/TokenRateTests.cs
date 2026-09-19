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
        ChatUsageAndIdleTimer();
        ResumedSession();
        GrokStreaming();
        GrokBatched50k();
        ReplayLiveGrokCaptures();
        Console.WriteLine($"PASS: {_checks} token-rate checks (50k bursts, elapsed averages, Grok ACP streaming, reconciliation, idle expiry and pane isolation)");
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

    private static void ChatUsageAndIdleTimer()
    {
        var clock = new TestClock();
        var chat = Chat(clock, "codex");
        var sibling = Chat(new TestClock(), "codex");
        try
        {
            Check(!chat.HasTokens && chat.TokenRatesText == "0 tok/s · 0 tok/min", "empty chat stays empty");
            Usage(chat, 100, 200, 40);
            Check(chat.TokenRatesText == "340 tok/s · 340 tok/min", "rates include fresh, cached and output tokens");
            Check(chat.HasTokens && chat.TokensText == "340 (300/40)", "existing total remains unchanged");
            clock.Milliseconds = 900;
            Usage(chat, 100, 200, 60);
            clock.Milliseconds = 1000;
            Result(chat, 100, 200, 60);
            Check(chat.TokenRatesText == "360 tok/s · 360 tok/min", "final result counts no live token twice");
            Check(chat.TotalTokens == 360 && !chat.IsWorking, "result commits the original total");

            clock.Milliseconds = 1100;
            Usage(chat, 5, 0, 5);
            Result(chat, 5, 0, 10);
            Check(chat.TokenRatesText == "341 tok/s · 375 tok/min", "next turn contributes its final delta");
            Usage(chat, 10, 0, 10);
            Call(chat, "IngestSdk", new JsonObject
            {
                ["type"] = "system", ["subtype"] = "codex_usage_checkpoint",
                ["usage"] = Bucket(10, 0, 15),
            });
            Usage(chat, 5, 0, 2);
            Result(chat, 5, 0, 5);
            Check(chat.TotalTokens == 410, "checkpoint and final totals remain intact");
            Check(chat.TokenRatesText == "373 tok/s · 410 tok/min", "fallback checkpoint does not duplicate usage");
            Result(chat, 20, 0, 10);
            Check(chat.TokenRatesText == "400 tok/s · 440 tok/min", "final-only provider usage is counted");
            Check(sibling.TokenRatesText == "0 tok/s · 0 tok/min", "each pane has its own rates");

            clock.Milliseconds = 2100;
            PumpTimer();
            Check(chat.TokenRatesText == "210 tok/s · 440 tok/min", "idle dispatcher tick gradually reduces the average");
            Check(Timer(chat).IsEnabled, "timer keeps aging the idle minute");
            clock.Milliseconds = 61100;
            PumpTimer();
            Check(chat.TokenRatesText == "0 tok/s · 0 tok/min", "idle dispatcher tick expires the minute");
            Check(!Timer(chat).IsEnabled, "empty windows stop the timer");
            Usage(chat, 1, 0, 1);
            Check(Timer(chat).IsEnabled, "new usage restarts the timer");
        }
        finally { chat.Close(); sibling.Close(); }
        Check(!Timer(chat).IsEnabled, "closing a pane releases its timer");
    }

    private static void ResumedSession()
    {
        var chat = Chat(new TestClock(), "kimi");
        try
        {
            var result = new JsonObject
            {
                ["type"] = "result", ["subtype"] = "success", ["usage"] = Bucket(10, 0, 10),
                ["session_usage"] = Bucket(900000, 0, 100000),
            };
            Call(chat, "IngestSdk", result);
            Check(chat.TotalTokens == 1000000, "resumed session still shows its historical total");
            Check(chat.TokenRatesText == "20 tok/s · 20 tok/min", "historical session total is excluded from recent rates");
        }
        finally { chat.Close(); }
    }

    private static void GrokStreaming()
    {
        var clock = new TestClock();
        var chat = Chat(clock, "grok");
        var options = new KimiSessionOptions { Cwd = Environment.CurrentDirectory };
        typeof(KimiSessionOptions).GetProperty("UseGrokProtocol", Hidden)!.SetValue(options, true);
        using var protocol = new KimiSession(options);
        protocol.MessageReceived += message => Call(chat, "IngestSdk", message);
        try
        {
            Chunk(protocol, "agent_thought_chunk", 40);
            Check(chat.TokenRatesText == "0 tok/s · 0 tok/min", "Grok history replay while starting contributes no live rates");
            chat.Status = "running";
            Chunk(protocol, "agent_thought_chunk", 40);
            Check(chat.TokenRatesText == "0 tok/s · 0 tok/min", "queued early prompt does not turn uninitialized Grok history replay into live rates");
            Check(chat.Items.OfType<ThinkingItem>().Any(item => item.Text.Length > 0), "Grok history remains visible when its rate estimate is excluded");
            typeof(KimiSession).GetField("_initialized", Hidden)!.SetValue(protocol, true);
            Chunk(protocol, "agent_thought_chunk", 40);
            Check(chat.TokenRatesText == "~10 tok/s · ~10 tok/min", "Grok thinking stream shows provisional token rates before final usage");
            Check(chat.HasTokenRates && !chat.HasTokens && chat.TotalTokens == 0, "fresh Grok rate is visible without inventing session usage");
            var cost = chat.CostText;
            clock.Milliseconds = 900;
            Chunk(protocol, "agent_message_chunk", 20);
            Check(chat.TokenRatesText == "~15 tok/s · ~15 tok/min", "Grok text and thinking both contribute");
            Call(protocol, "TranslateSessionUpdate", new JsonObject
            {
                ["sessionUpdate"] = "usage_update", ["used"] = 900000, ["size"] = 1000000,
            });
            Check(chat.TokenRatesText == "~15 tok/s · ~15 tok/min" && chat.TotalTokens == 0 && chat.CostText == cost,
                "Grok context-only update does not clear rates or invent billed tokens/cost");
            clock.Milliseconds = 2000;
            Call(chat, "RefreshTokenRates");
            Check(chat.TokenRatesText == "~8 tok/s · ~15 tok/min", "Grok tool-only gap gradually reduces the average");
            Chunk(protocol, "agent_message_chunk", 20);
            Check(chat.TokenRatesText == "~10 tok/s · ~20 tok/min", "stream resumes from its cumulative baseline after a gap");
            clock.Milliseconds = 2200;
            Result(chat, 100, 20, 25);
            Check(chat.TokenRatesText == "66 tok/s · 145 tok/min", "delayed final usage reconciles streamed output without counting it twice");
            Check(chat.TotalTokens == 145 && chat.TotalIn == 120 && chat.TotalOut == 25, "Grok final usage preserves exact disjoint buckets");

            chat.Status = "running";
            Chunk(protocol, "agent_message_chunk", 400);
            Result(chat, 0, 0, 2);
            Check(chat.TokenRatesText == "67 tok/s · 147 tok/min", "Grok overestimate corrects downward without subtracting a preceding turn");
            chat.Status = "running";
            Chunk(protocol, "agent_message_chunk", 40);
            Result(chat, 0, 0, 0);
            Check(chat.TokenRatesText == "67 tok/s · 147 tok/min", "explicit zero final usage removes only the current estimate");

            clock.Milliseconds = 63000;
            chat.Status = "running";
            Chunk(protocol, "agent_message_chunk", 8);
            Call(chat, "IngestSdk", new JsonObject { ["type"] = "result", ["subtype"] = "error", ["result"] = "test stop" });
            Check(chat.TokenRatesText == "~2 tok/s · ~2 tok/min", "missing final usage leaves an estimate marked provisional");
            clock.Milliseconds = 123000;
            Call(chat, "RefreshTokenRates");
            Check(chat.TokenRatesText == "0 tok/s · 0 tok/min" && !Timer(chat).IsEnabled, "Grok estimates expire at the exact minute boundary");
        }
        finally { chat.Close(); }
    }

    private static void GrokBatched50k()
    {
        var clock = new TestClock();
        var chat = Chat(clock, "grok");
        var options = new KimiSessionOptions { Cwd = Environment.CurrentDirectory };
        typeof(KimiSessionOptions).GetProperty("UseGrokProtocol", Hidden)!.SetValue(options, true);
        using var protocol = new KimiSession(options);
        typeof(KimiSession).GetField("_initialized", Hidden)!.SetValue(protocol, true);
        protocol.MessageReceived += message => Call(chat, "IngestSdk", message);
        try
        {
            // A chat can sit open for minutes before dispatch. Its clock starts with the actual prompt.
            clock.Milliseconds = 120000;
            chat.Status = "running";
            clock.Milliseconds = 140000;
            Chunk(protocol, "agent_thought_chunk", 80000);
            Check(chat.TokenRatesText == "~1.0k tok/s · ~20.0k tok/min", $"Grok 20k burst is averaged from prompt dispatch: {chat.TokenRatesText}");
            clock.Milliseconds = 141000;
            PumpTimer();
            Check(chat.TokenRatesText == "~952 tok/s · ~20.0k tok/min", "dispatcher keeps the buffered Grok rate visible");
            clock.Milliseconds = 160000;
            Chunk(protocol, "agent_message_chunk", 80000);
            Check(chat.TokenRatesText == "~1.0k tok/s · ~40.0k tok/min", "second Grok 20k burst is spread over elapsed time");
            clock.Milliseconds = 170000;
            Chunk(protocol, "agent_message_chunk", 40000);
            Result(chat, 0, 0, 50000);
            Check(chat.TokenRatesText == "1.0k tok/s · 50.0k tok/min", "authoritative 50k completion confirms the averaged Grok rate");
            Check(chat.TotalTokens == 50000 && chat.TotalOut == 50000, "50k burst simulation preserves exact session totals");
            clock.Milliseconds = 230000;
            PumpTimer();
            Check(chat.TokenRatesText == "0 tok/s · 0 tok/min" && !Timer(chat).IsEnabled, "finished Grok rate expires and releases its timer");
        }
        finally { chat.Close(); }
    }

    private static void Chunk(KimiSession protocol, string kind, int characters) =>
        Call(protocol, "TranslateSessionUpdate", new JsonObject
        {
            ["sessionUpdate"] = kind,
            ["content"] = new JsonObject { ["type"] = "text", ["text"] = new string('x', characters) },
        });

    private static void ReplayLiveGrokCaptures()
    {
        var directory = Environment.GetEnvironmentVariable("VIBECODE_GROK_CAPTURE_DIR");
        if (string.IsNullOrEmpty(directory)) return;
        foreach (var file in System.IO.Directory.GetFiles(directory, "live-*-all.json"))
        {
            var capture = JsonNode.Parse(System.IO.File.ReadAllText(file))!;
            foreach (var scenario in capture["scenarios"]!.AsArray())
            {
                var phase = scenario!["name"]!.GetValue<string>();
                var clock = new TestClock();
                var chat = Chat(clock, "grok");
                chat.Status = "running";
                var options = new KimiSessionOptions { Cwd = Environment.CurrentDirectory };
                typeof(KimiSessionOptions).GetProperty("UseGrokProtocol", Hidden)!.SetValue(options, true);
                using var protocol = new KimiSession(options);
                typeof(KimiSession).GetField("_initialized", Hidden)!.SetValue(protocol, true);
                protocol.MessageReceived += message => Call(chat, "IngestSdk", message);
                try
                {
                    var chunks = 0;
                    foreach (var ev in capture["events"]!.AsArray().OfType<JsonObject>())
                    {
                        if (ev["phase"]?.GetValue<string>() != phase || ev["generated_text"] is null) continue;
                        clock.Milliseconds = (long)(ev["seconds"]!.GetValue<double>() * 1000);
                        Call(protocol, "TranslateSessionUpdate", new JsonObject
                        {
                            ["sessionUpdate"] = ev["update_kind"]!.DeepClone(),
                            ["content"] = new JsonObject { ["type"] = "text", ["text"] = ev["generated_text"]!.DeepClone() },
                        });
                        chunks++;
                    }
                    Check(chunks > 0 && chat.TokenRatesText.Contains('~') && chat.HasTokenRates,
                        $"{System.IO.Path.GetFileName(file)} {phase}: captured live stream has visible provisional rates");
                    var report = (JsonObject)typeof(KimiSession).GetMethod("UsageFromGrokResponse", BindingFlags.Static | BindingFlags.NonPublic)!
                        .Invoke(null, new object?[] { scenario["response_numeric"] })!;
                    var usage = report["usage"]!;
                    var expected = new[] { "input_tokens", "cache_read_input_tokens", "cache_creation_input_tokens", "output_tokens" }
                        .Sum(key => usage[key]?.GetValue<long>() ?? 0);
                    clock.Milliseconds = (long)(scenario["seconds"]!.GetValue<double>() * 1000);
                    Call(chat, "IngestSdk", new JsonObject { ["type"] = "result", ["subtype"] = "success", ["usage"] = usage.DeepClone() });
                    Check(chat.TotalTokens == expected && !chat.TokenRatesText.Contains('~'), "captured Grok final report confirms all estimates and commits exact usage");
                    var actual = ((double, double))Call(typeof(ChatViewModel).GetField("_tokenUsageRates", Hidden)!.GetValue(chat)!, "Read")!;
                    Check(actual.Item2 == expected, "captured Grok stream and final report contribute each token once");
                    Check(Math.Abs(actual.Item1 - expected / Math.Max(1, clock.Milliseconds / 1000d)) < 0.000001,
                        "captured final batch is averaged across the prompt duration");
                    Console.WriteLine($"PASS: live Grok replay {System.IO.Path.GetFileName(file)} {phase}: {chunks} chunks, {chat.TokenRatesText}, exact {expected} tokens");
                    clock.Milliseconds += 1000;
                    Call(chat, "RefreshTokenRates");
                    Check(!chat.TokenRatesText.StartsWith("0 tok/s"), "captured final batch does not disappear a second after arrival");
                }
                finally { chat.Close(); }
            }
        }
    }

    private static object Tracker(TestClock clock) => Activator.CreateInstance(
        typeof(ChatViewModel).Assembly.GetType("VibeCode.Services.RollingTokenUsage", throwOnError: true)!,
        new object[] { clock })!;

    private static ChatViewModel Chat(TestClock clock, string provider)
    {
        var chat = new ChatViewModel(Environment.CurrentDirectory, provider: provider, accountId: "token-rate-simulation");
        typeof(ChatViewModel).GetField("_tokenUsageRates", Hidden)!.SetValue(chat, Tracker(clock));
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
