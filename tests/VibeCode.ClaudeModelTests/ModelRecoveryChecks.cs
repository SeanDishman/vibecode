using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using VibeCode.Protocol;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    private static void Feed(ClaudeSession session, JsonObject envelope) =>
        typeof(ClaudeSession).GetMethod("HandleLineForTest", Flags)!.Invoke(session, [envelope.ToJsonString()]);

    private static JsonObject Fallback(string uuid = "fallback-1", string original = "claude-opus-5-5",
        string direction = "retry", string? parent = null) => new()
        {
            ["type"] = "system",
            ["subtype"] = "model_refusal_fallback",
            ["trigger"] = "refusal",
            ["original_model"] = original,
            ["fallback_model"] = "claude-opus-4-8",
            ["direction"] = direction,
            ["uuid"] = uuid,
            ["parent_tool_use_id"] = parent,
            ["session_id"] = "recovery-session",
        };
    private static JsonObject ResponseStart(string model = "claude-opus-4-8", string? parent = null) => new()
    {
        ["type"] = "stream_event",
        ["parent_tool_use_id"] = parent,
        ["session_id"] = "recovery-session",
        ["event"] = new JsonObject
        {
            ["type"] = "message_start",
            ["message"] = new JsonObject
            { ["id"] = "fallback-response", ["model"] = model, ["role"] = "assistant", ["content"] = new JsonArray() }
        },
    };
    private static JsonObject Assistant(string model = "claude-opus-4-8", string? parent = null) => new()
    {
        ["type"] = "assistant",
        ["parent_tool_use_id"] = parent,
        ["session_id"] = "recovery-session",
        ["message"] = new JsonObject
        {
            ["id"] = "fallback-response",
            ["model"] = model,
            ["role"] = "assistant",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Research continues." })
        },
    };
    private static JsonObject Stopped() => new() { ["type"] = "result", ["subtype"] = "error_during_execution", ["is_error"] = true, ["errors"] = new JsonArray("Interrupted by user") };
    private static int Controls(string subtype) => Wire().Count(m => m["request"]?["subtype"]?.ToString() == subtype);
    private static int Restores() => Wire().Count(m => m["request"]?["subtype"]?.ToString() == "set_model"
        && m["request"]?["model"]?.ToString().StartsWith(Current[0]) == true);
    private static bool Phase(ConcurrentQueue<JsonNode> messages, string phase) => messages.Any(m =>
        m["subtype"]?.ToString() == "claude_model_restore" && m["phase"]?.ToString() == phase);
    private static bool IsFollowup(JsonNode m) => m["type"]?.ToString() == "assistant" && m["message"]?["id"]?.ToString() == "followup";
    private static void FixtureMode(string mode) => Environment.SetEnvironmentVariable("CLAUDE_MODEL_RECOVERY_FIXTURE", mode);
    private static void RecoveryDelay(ClaudeSession session, int milliseconds = 75) =>
        typeof(ClaudeSession).GetProperty("OpusRecoveryDelay", Flags)!.SetValue(session, TimeSpan.FromMilliseconds(milliseconds));

    private static void ModelRecoveryChecks()
    {
        var oldWire = Environment.GetEnvironmentVariable("CLAUDE_MODEL_FIXTURE");
        try
        {
            using (var defaults = new ClaudeSession(new ClaudeSessionOptions { Cwd = Root, Model = Current[0] }))
                Check("production recovery gives Opus 4.8 thirty seconds before retrying",
                    (TimeSpan)typeof(ClaudeSession).GetProperty("OpusRecoveryDelay", Flags)!.GetValue(defaults)! == TimeSpan.FromSeconds(30));
            FixtureMode("interrupt-only");
            RecoveryScenario("stop-before-switch", Current[0] + "[1m]", (session, messages) =>
            {
                RecoveryDelay(session, 400);
                Feed(session, Assistant(Current[0])); Feed(session, Fallback(original: Current[0] + "-20261001"));
                Feed(session, ResponseStart(parent: "child-tool"));
                Check("a child response cannot trigger the parent's recovery", Controls("interrupt") == 0);
                var elapsed = Stopwatch.StartNew();
                Feed(session, ResponseStart());
                Check("the fallback keeps running during the cooldown", Phase(messages, "waiting") && session.CanSteer
                    && Controls("interrupt") == 0 && Restores() == 0);
                PumpUntil(() => elapsed.ElapsedMilliseconds >= 100);
                Feed(session, Fallback()); Feed(session, ResponseStart());
                Check("repeated frames do not bypass the cooldown", Controls("interrupt") == 0 && Restores() == 0);
                PumpUntil(() => Controls("interrupt") == 1);
                Check("the interrupt waits for the complete cooldown", elapsed.ElapsedMilliseconds >= 400);
                Check("fallback is interrupted before any model change", Restores() == 0 && Phase(messages, "stopping"));
                Feed(session, Fallback()); Feed(session, ResponseStart());
                Check("duplicate frames do not interrupt twice", Controls("interrupt") == 1);
                var effort = session.SetFlagSettingsAsync("xhigh", true); PumpUntil(() => effort.IsCompleted);
                Feed(session, Stopped()); PumpUntil(() => Phase(messages, "ready"));
                var restore = Wire().Single(m => m["request"]?["subtype"]?.ToString() == "set_model")["request"]!;
                Check("restart keeps the selected context variant and latest effort", restore["model"]!.ToString() == Current[0] + "[1m]" && restore["effort"]!.ToString() == "xhigh");
                Check("ready is emitted only after the old turn stopped", !session.CanSteer && messages.Any(m => m["vibecode_model_recovery_interrupted"]?.GetValue<bool>() == true));
                Check("the host controls continuation and the original prompt is not replayed", Wire().All(m => m["type"]?.ToString() != "user"));
                session.SendUser(JsonValue.Create("followup")!);
                PumpUntil(() => messages.Any(IsFollowup));
                Check("the restarted turn uses Opus 5.5 in the same conversation", messages.Single(IsFollowup)["message"]?["model"]?.ToString() == Current[0] + "[1m]" && Wire().Count(m => m["launch"] is not null) == 1);
            });
            FixtureMode("success");
            RecoveryScenario("result-only", "opus", (session, messages) =>
            {
                Feed(session, Fallback()); Feed(session, Stopped()); PumpUntil(() => Phase(messages, "selected"));
                Check("an already-ended fallback selects Opus without interrupting an idle turn", Restores() == 1 && Controls("interrupt") == 0);
            });
            RecoveryScenario("natural-completion", Current[0], (session, messages) =>
            {
                RecoveryDelay(session, 250);
                Feed(session, Assistant(Current[0])); Feed(session, Fallback()); Feed(session, ResponseStart());
                Feed(session, new JsonObject { ["type"] = "result", ["subtype"] = "success", ["is_error"] = false });
                PumpUntil(() => Phase(messages, "selected"));
                Check("a completed fallback changes the next model without interrupting or replaying the task",
                    Controls("interrupt") == 0 && Restores() == 1 && !Phase(messages, "ready")
                    && !messages.Any(m => m["vibecode_model_recovery_interrupted"]?.GetValue<bool>() == true));
            });
            RecoveryScenario("completion-forwarding-during-cooldown", Current[0], (session, messages) =>
            {
                RecoveryDelay(session, 250);
                using var forwarding = new ManualResetEventSlim();
                using var release = new ManualResetEventSlim();
                session.MessageReceived += message =>
                {
                    if (message["type"]?.ToString() != "result") return;
                    forwarding.Set();
                    release.Wait(TimeSpan.FromSeconds(5));
                };
                Feed(session, Assistant(Current[0])); Feed(session, Fallback()); Feed(session, ResponseStart());
                var delivery = Task.Run(() => Feed(session,
                    new JsonObject { ["type"] = "result", ["subtype"] = "success", ["is_error"] = false }));
                try
                {
                    PumpUntil(() => forwarding.IsSet);
                    var elapsed = Stopwatch.StartNew(); PumpUntil(() => elapsed.ElapsedMilliseconds >= 300);
                    Check("a result still being forwarded prevents interrupting an already-finished fallback",
                        Controls("interrupt") == 0 && Restores() == 0 && !Phase(messages, "stopping"));
                }
                finally { release.Set(); delivery.GetAwaiter().GetResult(); }
                PumpUntil(() => Phase(messages, "selected"));
                Check("the completed result is delivered before the next model is selected", Restores() == 1 && !Phase(messages, "ready"));
            });
            RecoveryScenario("manual-during-cooldown", Current[0], (session, messages) =>
            {
                RecoveryDelay(session, 250);
                Feed(session, Assistant(Current[0])); Feed(session, Fallback()); Feed(session, ResponseStart());
                var change = session.SetModelAsync(Current[2], "high"); PumpUntil(() => change.IsCompleted);
                PumpUntil(() => Phase(messages, "cancelled"));
                var elapsed = Stopwatch.StartNew(); PumpUntil(() => elapsed.ElapsedMilliseconds >= 300);
                Check("manual model selection cancels the cooldown before any interrupt", Controls("interrupt") == 0 && Restores() == 0);
            });
            RecoveryScenario("stop-during-cooldown", Current[0], (session, messages) =>
            {
                RecoveryDelay(session, 250);
                Feed(session, Assistant(Current[0])); Feed(session, Fallback()); Feed(session, ResponseStart());
                var stop = session.InterruptAsync(); PumpUntil(() => stop.IsCompleted && Phase(messages, "cancelled"));
                var elapsed = Stopwatch.StartNew(); PumpUntil(() => elapsed.ElapsedMilliseconds >= 300);
                Check("manual Stop cancels the waiting retry without a second interrupt", Controls("interrupt") == 1 && Restores() == 0 && !Phase(messages, "ready"));
            });
            RecoveryScenario("manual-before-fallback", Current[0], (session, messages) =>
            {
                Feed(session, Fallback());
                var change = session.SetModelAsync(Current[2], "high"); PumpUntil(() => change.IsCompleted);
                Feed(session, ResponseStart()); Feed(session, Stopped());
                Check("manual model selection cancels pending recovery", Restores() == 0 && Controls("interrupt") == 0);
            });
            foreach (var selected in new[] { "claude-opus-4-8", Current[1], Current[2] })
                RecoveryScenario("selected-" + selected, selected, (session, messages) =>
                {
                    Feed(session, Fallback()); Feed(session, ResponseStart()); Feed(session, Stopped());
                    Check("selected " + selected + " is respected", Restores() == 0 && Controls("interrupt") == 0);
                });
            RecoveryScenario("unrelated-events", Current[0], (session, messages) =>
            {
                Feed(session, Fallback(parent: "child-tool")); Feed(session, Fallback(original: Current[1]));
                Feed(session, Fallback(direction: "revert")); Feed(session, ResponseStart()); Feed(session, Stopped());
                Check("unrelated fallback events do not interrupt the root", Controls("interrupt") == 0 && Restores() == 0);
            });
            FixtureMode("delayed");
            RecoveryScenario("manual-during-switch", Current[0], (session, messages) =>
            {
                Feed(session, Assistant(Current[0])); Feed(session, Fallback()); Feed(session, ResponseStart()); PumpUntil(() => Restores() == 1);
                var change = session.SetModelAsync("claude-opus-4-8", "high"); PumpUntil(() => change.IsCompleted);
                PumpUntil(() => Phase(messages, "cancelled"));
                Check("a manual choice during the switch cancels automatic continuation", !Phase(messages, "ready"));
                session.SendUser(JsonValue.Create("followup")!);
                PumpUntil(() => messages.Any(IsFollowup));
                Check("the newer manual model reaches the CLI last", messages.Single(IsFollowup)["message"]?["model"]?.ToString() == "claude-opus-4-8");
            });
            foreach (var mode in new[] { "reject", "interrupt-reject", "interrupt-only", "ignore-model" })
            {
                FixtureMode(mode);
                RecoveryScenario("failure-" + mode, Current[0], (session, messages) =>
                {
                    typeof(ClaudeSession).GetProperty("OpusRecoveryTimeout", Flags)!.SetValue(session, TimeSpan.FromMilliseconds(250));
                    Feed(session, Assistant(Current[0])); Feed(session, Fallback()); Feed(session, ResponseStart()); PumpUntil(() => Phase(messages, "failed"));
                    Check(mode + " never starts an unconfirmed replacement turn", !Phase(messages, "ready") && Wire().All(m => m["type"]?.ToString() != "user"));
                    Check(mode + " consumes one bounded interrupt attempt", Controls("interrupt") == 1 && Restores() <= 1);
                });
            }
            FixtureMode("success");
            RecoveryScenario("loop-bound", Current[0], (session, messages) =>
            {
                for (var i = 1; i <= 4; i++)
                {
                    Feed(session, Assistant(Current[0])); Feed(session, Fallback("fallback-" + i)); Feed(session, ResponseStart());
                    var count = i;
                    PumpUntil(() => messages.Count(m => m["subtype"]?.ToString() == "claude_model_restore" && m["phase"]?.ToString() is "ready" or "fallback") == count);
                }
                Check("repeated fallback continues on Opus 4.8 without a fourth interrupt or terminal error",
                    Controls("interrupt") == 3 && Restores() == 3 && Phase(messages, "fallback") && !Phase(messages, "failed") && session.CanSteer);
                var change = session.SetModelAsync(Current[0], "max"); PumpUntil(() => change.IsCompleted);
                Feed(session, Fallback("manual-retry")); Feed(session, ResponseStart());
                PumpUntil(() => messages.Count(m => m["phase"]?.ToString() == "ready") == 4);
                Check("explicitly choosing Opus 5.5 permits a fresh delayed retry", Controls("interrupt") == 4 && Restores() == 5);
            });
            RecoveryChatCheck("success"); RecoveryChatCheck("reject"); RecoveryChatCancellation();
            RecoveryChatNaturalCompletion(); RecoveryChatRepeatedFallback(); RecoveryChatCloseDuringCooldown();
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLAUDE_MODEL_RECOVERY_FIXTURE", null);
            Environment.SetEnvironmentVariable("CLAUDE_MODEL_FIXTURE", oldWire);
        }
    }

    private static void RecoveryScenario(string name, string model, Action<ClaudeSession, ConcurrentQueue<JsonNode>> action)
    {
        Environment.SetEnvironmentVariable("CLAUDE_MODEL_FIXTURE", Path.Combine(Root, name + ".jsonl"));
        using var session = new ClaudeSession(new ClaudeSessionOptions { Cwd = Root, Model = model, Effort = "max" });
        RecoveryDelay(session);
        var messages = new ConcurrentQueue<JsonNode>(); session.MessageReceived += messages.Enqueue;
        session.Start(); PumpUntil(() => session.WhenInitialized.IsCompleted && session.Models.Count > 0);
        Feed(session, new JsonObject { ["type"] = "system", ["subtype"] = "init", ["session_id"] = "recovery-session", ["model"] = model });
        try { action(session, messages); }
        catch (Exception ex) { throw new InvalidOperationException($"Recovery fixture '{name}' failed. CLI stderr: {session.StderrTail}", ex); }
    }

    private static void RecoveryChatCheck(string mode)
    {
        FixtureMode(mode);
        Environment.SetEnvironmentVariable("CLAUDE_MODEL_FIXTURE", Path.Combine(Root, "chat-restart-" + mode + ".jsonl"));
        AppSettings.Current.DefaultModel = Current[0];
        var chat = new ChatViewModel(Root, provider: "claude") { ExcludeFromMemory = true };
        try
        {
            chat.Start(); PumpUntil(() => chat.Models.Count > 0 && chat.Status == "idle"); AppSettings.Current.DefaultModel = Current[2];
            var session = (ClaudeSession)typeof(ChatViewModel).GetField("_session", Flags)!.GetValue(chat)!;
            RecoveryDelay(session);
            var completions = 0; chat.TurnCompleted += () => completions++;
            Check("research starts normally before " + mode + " recovery", chat.Send("Research fixture"));
            PumpUntil(() => Wire().Any(m => m["type"]?.ToString() == "user")); chat.Send("Queued follow-up");
            Feed(session, Fallback()); Feed(session, ResponseStart());
            if (mode == "success")
            {
                PumpUntil(() => Wire().Count(m => m["type"]?.ToString() == "user") == 2);
                var users = Wire().Where(m => m["type"]?.ToString() == "user").ToArray();
                Check("automatic restart sends a continuation after interrupt and model acknowledgment", Controls("interrupt") == 1
                    && users[1].ToJsonString().Contains("VIBECODE CLAUDE MODEL RECOVERY") && chat.Model == Current[0] && chat.Status == "running");
                Check("recovery does not replay the task or consume queued follow-ups", users[1].ToJsonString().Contains("do not repeat completed actions")
                    && !users[1].ToJsonString().Contains("Queued follow-up") && chat.Items.OfType<QueuedItem>().Any());
                Check("the intermediate stop is not reported as task completion", completions == 0);
                Feed(session, new JsonObject { ["type"] = "result", ["subtype"] = "success", ["is_error"] = false });
                PumpUntil(() => Wire().Count(m => m["type"]?.ToString() == "user") == 3);
                Check("queued follow-ups run only after the restarted task finishes", completions == 1
                    && Wire().Last(m => m["type"]?.ToString() == "user").ToJsonString().Contains("Queued follow-up"));
            }
            else
            {
                PumpUntil(() => chat.Items.OfType<BannerItem>().Any(b => b.Text.StartsWith("Could not restart automatically")));
                Check("failed model selection leaves the chat stopped with queued prompts retained", chat.Status == "error" && chat.Model == "claude-opus-4-8"
                    && Wire().Count(m => m["type"]?.ToString() == "user") == 1 && chat.Items.OfType<QueuedItem>().Any());
            }
            Check(mode + " recovery preserves the user's original prompt and saved default", chat.Items.OfType<UserItem>().Any(u => u.Text == "Research fixture") && AppSettings.Current.DefaultModel == Current[2]);
            Check(mode + " recovery retains the same conversation process", Wire().Count(m => m["launch"] is not null) == 1);
        }
        finally { chat.Close(); }
    }

    private static void RecoveryChatCancellation()
    {
        FixtureMode("interrupt-only");
        Environment.SetEnvironmentVariable("CLAUDE_MODEL_FIXTURE", Path.Combine(Root, "chat-manual-stop.jsonl"));
        AppSettings.Current.DefaultModel = Current[0];
        var chat = new ChatViewModel(Root, provider: "claude") { ExcludeFromMemory = true };
        try
        {
            chat.Start(); PumpUntil(() => chat.Models.Count > 0 && chat.Status == "idle");
            chat.Send("Stop cancellation fixture"); PumpUntil(() => Wire().Any(m => m["type"]?.ToString() == "user"));
            var session = (ClaudeSession)typeof(ChatViewModel).GetField("_session", Flags)!.GetValue(chat)!;
            RecoveryDelay(session);
            Feed(session, Fallback()); Feed(session, ResponseStart()); PumpUntil(() => Controls("interrupt") == 1);
            PumpUntil(() => typeof(ChatViewModel).GetField("_claudeModelRestartId", Flags)!.GetValue(chat) is not null);
            chat.Interrupt(); Feed(session, Stopped()); PumpUntil(() => chat.Status == "idle");
            Check("manual Stop cancels recovery and prevents a delayed restart", Restores() == 0 && Wire().Count(m => m["type"]?.ToString() == "user") == 1);
        }
        finally { chat.Close(); }
    }

    private static void RecoveryChatNaturalCompletion()
    {
        FixtureMode("success");
        Environment.SetEnvironmentVariable("CLAUDE_MODEL_FIXTURE", Path.Combine(Root, "chat-cooldown-completion.jsonl"));
        AppSettings.Current.DefaultModel = Current[0];
        var chat = new ChatViewModel(Root, provider: "claude") { ExcludeFromMemory = true };
        try
        {
            chat.Start(); PumpUntil(() => chat.Models.Count > 0 && chat.Status == "idle");
            var session = (ClaudeSession)typeof(ChatViewModel).GetField("_session", Flags)!.GetValue(chat)!;
            RecoveryDelay(session, 750);
            var completions = 0; chat.TurnCompleted += () => completions++;
            chat.Send("Finish during cooldown"); PumpUntil(() => Wire().Count(m => m["type"]?.ToString() == "user") == 1);
            chat.Send("Queued next task");
            Feed(session, Fallback()); Feed(session, ResponseStart());
            PumpUntil(() => chat.Items.OfType<BannerItem>().Any(b => b.Text.Contains("for 30 seconds")));
            Check("the cooldown leaves the chat running on Opus 4.8 with steering available", chat.Status == "running"
                && chat.Model == "claude-opus-4-8" && chat.CanSteer && chat.CanSendQueuedNow && Controls("interrupt") == 0);
            Feed(session, new JsonObject { ["type"] = "result", ["subtype"] = "success", ["is_error"] = false });
            PumpUntil(() => Wire().Count(m => m["type"]?.ToString() == "user") == 2);
            PumpUntil(() => chat.Items.OfType<BannerItem>().Any(b => b.Text == "Opus 5.5 selected for the next turn."));
            Check("natural completion during cooldown drains the queue without replaying or interrupting the next task",
                completions == 1 && Controls("interrupt") == 0 && Restores() == 1 && chat.Status == "running"
                && Wire().Count(m => m["type"]?.ToString() == "user") == 2
                && Wire().Last(m => m["type"]?.ToString() == "user").ToJsonString().Contains("Queued next task"));
        }
        finally { chat.Close(); }
    }

    private static void RecoveryChatRepeatedFallback()
    {
        FixtureMode("success");
        Environment.SetEnvironmentVariable("CLAUDE_MODEL_FIXTURE", Path.Combine(Root, "chat-repeated-fallback.jsonl"));
        AppSettings.Current.DefaultModel = Current[0];
        var chat = new ChatViewModel(Root, provider: "claude") { ExcludeFromMemory = true };
        try
        {
            chat.Start(); PumpUntil(() => chat.Models.Count > 0 && chat.Status == "idle");
            var session = (ClaudeSession)typeof(ChatViewModel).GetField("_session", Flags)!.GetValue(chat)!;
            RecoveryDelay(session);
            var completions = 0; chat.TurnCompleted += () => completions++;
            chat.Send("Research despite repeated fallback"); PumpUntil(() => Wire().Count(m => m["type"]?.ToString() == "user") == 1);
            chat.Send("Queued after research");
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                Feed(session, Fallback("chat-fallback-" + attempt)); Feed(session, ResponseStart());
                var expected = attempt + 1;
                PumpUntil(() => Wire().Count(m => m["type"]?.ToString() == "user") == expected && chat.Model == Current[0]);
            }
            Feed(session, Fallback("chat-fallback-4")); Feed(session, ResponseStart());
            PumpUntil(() => chat.Items.OfType<BannerItem>().Any(b => b.Text == "Claude keeps switching back. Continuing on Opus 4.8."));
            Check("exhausted retries leave the active chat running and its queued request intact", Controls("interrupt") == 3
                && Restores() == 3 && chat.Status == "running" && chat.Model == "claude-opus-4-8" && chat.CanSteer
                && completions == 0 && chat.Items.OfType<QueuedItem>().Any()
                && !chat.Items.OfType<BannerItem>().Any(b => b.Text.StartsWith("Could not restart automatically")));
            Feed(session, new JsonObject { ["type"] = "result", ["subtype"] = "success", ["is_error"] = false });
            PumpUntil(() => Wire().Count(m => m["type"]?.ToString() == "user") == 5);
            Check("the fallback finishes normally and the queued request continues", completions == 1
                && chat.Status == "running" && chat.Model == "claude-opus-4-8"
                && Wire().Last(m => m["type"]?.ToString() == "user").ToJsonString().Contains("Queued after research"));
        }
        finally { chat.Close(); }
    }

    private static void RecoveryChatCloseDuringCooldown()
    {
        FixtureMode("success");
        Environment.SetEnvironmentVariable("CLAUDE_MODEL_FIXTURE", Path.Combine(Root, "chat-close-cooldown.jsonl"));
        AppSettings.Current.DefaultModel = Current[0];
        var chat = new ChatViewModel(Root, provider: "claude") { ExcludeFromMemory = true };
        try
        {
            chat.Start(); PumpUntil(() => chat.Models.Count > 0 && chat.Status == "idle");
            var session = (ClaudeSession)typeof(ChatViewModel).GetField("_session", Flags)!.GetValue(chat)!;
            RecoveryDelay(session, 250);
            chat.Send("Close during cooldown"); PumpUntil(() => Wire().Any(m => m["type"]?.ToString() == "user"));
            Feed(session, Fallback()); Feed(session, ResponseStart());
            PumpUntil(() => chat.Items.OfType<BannerItem>().Any(b => b.Text.Contains("for 30 seconds")));
            chat.Close();
            var elapsed = Stopwatch.StartNew(); PumpUntil(() => elapsed.ElapsedMilliseconds >= 300);
            Check("closing the chat cancels the cooldown without restarting its process", chat.Status == "closed"
                && Controls("interrupt") == 0 && Restores() == 0 && Wire().Count(m => m["launch"] is not null) == 1);
        }
        finally { if (chat.Status != "closed") chat.Close(); }
    }

    private static int ModelRecoveryFixture()
    {
        var args = Environment.GetCommandLineArgs(); var modelIndex = Array.IndexOf(args, "--model");
        var model = modelIndex >= 0 ? args[modelIndex + 1] : Current[0];
        var mode = Environment.GetEnvironmentVariable("CLAUDE_MODEL_RECOVERY_FIXTURE");
        void Emit(JsonObject row) { Console.WriteLine(row.ToJsonString()); Console.Out.Flush(); }
        void Log(JsonObject row) => File.AppendAllText(Environment.GetEnvironmentVariable("CLAUDE_MODEL_FIXTURE")!, row.ToJsonString() + "\n");
        Log(new JsonObject { ["launch"] = new JsonArray(args.Skip(1).Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()) });
        while (Console.ReadLine() is { } line)
        {
            var envelope = JsonNode.Parse(line)!.AsObject(); Log(envelope);
            if (envelope["type"]?.ToString() == "user")
            {
                if (envelope["message"]?["content"]?.ToString() == "followup")
                {
                    var reply = Assistant(model); reply["message"]!["id"] = "followup"; Emit(reply);
                    Emit(new JsonObject { ["type"] = "result", ["is_error"] = false });
                }
                continue;
            }
            var request = envelope["request"]!; var subtype = request["subtype"]?.ToString();
            if (subtype == "set_model" && mode == "ignore-model") continue;
            if (subtype == "set_model" && mode == "delayed") Thread.Sleep(150);
            var reject = subtype == "set_model" && mode == "reject" || subtype == "interrupt" && mode == "interrupt-reject";
            var payload = new JsonObject();
            if (subtype == "initialize") payload["models"] = new JsonArray(Row(Current[0], Names[0], Current[0]));
            if (!reject && subtype == "set_model") model = request["model"]?.ToString() ?? model;
            Emit(new JsonObject
            {
                ["type"] = "control_response",
                ["response"] = new JsonObject
                {
                    ["subtype"] = reject ? "error" : "success",
                    ["request_id"] = envelope["request_id"]!.DeepClone(),
                    ["response"] = payload,
                    ["error"] = reject ? "Control rejected by fixture" : null,
                }
            });
            if (subtype == "interrupt" && !reject && mode != "interrupt-only") Emit(Stopped());
        }
        return 0;
    }
}
