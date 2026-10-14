using System.Collections;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    private const string CapacityError = "Selected model is at capacity. Please try a different model.";

    private static (ChatViewModel Chat, FakeSession Session) AdvancedRetryChat(string name, bool manager = false, string provider = "codex")
    {
        var pair = NewChat(name, provider);
        pair.Chat.BridgeLabel = manager ? "Agent 1" : "Agent 2";
        pair.Chat.IsBridgeManager = manager;
        Property(pair.Chat, "BridgeCoordinatesOnly", manager);
        Property(pair.Chat, "BridgeCoordinatorAgentId", manager ? null : "manager-id");
        pair.Chat.Model = provider == "codex" ? "gpt-6-astra" : null;
        return pair;
    }

    private static void RetryNow(ChatViewModel chat)
    {
        Field(chat, "_bridgeRetryAt", DateTimeOffset.MinValue);
        Call(chat, "CheckBridgeErrorRecovery");
    }

    private static void BridgeErrorRecoveryChecks()
    {
        foreach (var text in new[] { CapacityError, "HTTP 503 Service Unavailable", "API Error: 529 overloaded_error", "Internal server error",
            "Bad gateway", "Gateway timeout", "stream disconnected before completion", "connection reset by peer", "request timed out" })
            Check("temporary provider failure is retryable: " + text, BridgeErrorRecovery.IsTransient(Error(text)));
        foreach (var code in new[] { "httpConnectionFailed", "serverOverloaded", "responseStreamDisconnected" })
            Check("structured provider failure is retryable: " + code, BridgeErrorRecovery.IsTransient(new JsonObject
            { ["is_error"] = true, ["provider_error"] = new JsonObject { ["codexErrorInfo"] = code } }));
        foreach (var field in new[] { "status", "status_code", "http_status", "httpStatusCode" })
            Check("structured HTTP failure is retryable: " + field, BridgeErrorRecovery.IsTransient(new JsonObject
            { ["is_error"] = true, ["provider_error"] = new JsonObject { [field] = 503 } }));
        foreach (var text in new[] { "HTTP 401: connection reset", "HTTP 403: service unavailable", "Invalid API key", "usage limit",
            "429 rate limit", "invalid request", "context window exceeded", "model not found", "permission denied",
            "user interrupted", "request cancelled", "Tool failed with exit code 1"})
            Check("non-transient failure does not retry: " + text, !BridgeErrorRecovery.IsTransient(Error(text)));
        Check("successful text and transport warnings do not trigger retries", !BridgeErrorRecovery.IsTransient(new JsonObject
        { ["is_error"] = false, ["result"] = "Falling back from WebSockets to HTTPS: 503 Service Unavailable" }));

        AppSettings.Current.ContinueAfterLimitResets = false;
        foreach (var provider in new[] { "codex", "claude", "glm" })
            foreach (var manager in new[] { false, true })
            {
                var (chat, session) = AdvancedRetryChat(provider + "-retry-" + manager, manager, provider);
                chat.Status = "running";
                var before = DateTimeOffset.Now;
                End(chat, Error(CapacityError));
                var deadline = ReadField<DateTimeOffset>(chat, "_bridgeRetryAt");
                Check(provider + " advanced role schedules a full minute independently of quota settings: " + manager,
                    chat.WaitingForBridgeRetry && deadline >= before.AddMinutes(1) && deadline <= DateTimeOffset.Now.AddMinutes(1));
                Check("waiting role remains visibly pending and stoppable", chat.BridgeTaskState == "waiting" && chat.CanInterrupt
                    && chat.ShowWorkingText && chat.WorkingText.StartsWith("Retrying in ") && chat.BridgeAvailabilityText.StartsWith("Retrying in "));
                Call(chat, "CheckBridgeErrorRecovery");
                Check("no prompt before the deadline", session.Sent.Count == 0);
                var selectedModel = chat.Model;
                RetryNow(chat); Pump(() => session.Sent.Count == 1);
                Call(chat, "CheckBridgeErrorRecovery");
                Check("one continuation keeps the same model and task", session.Sent.Count == 1 && !chat.WaitingForBridgeRetry
                    && session.Sent[0].Contains(BridgeErrorRecovery.ContinuePrompt) && chat.Model == selectedModel && chat.BridgeTaskState == "working");
                chat.Close();
            }
        foreach (var ordinaryBridge in new[] { false, true })
        {
            var (chat, session) = NewChat("ordinary-capacity-" + ordinaryBridge);
            if (ordinaryBridge) chat.BridgeLabel = "Agent 1";
            End(chat, Error(CapacityError)); RetryNow(chat);
            Check("ordinary chats/bridges retain manual retry behavior " + ordinaryBridge, !chat.WaitingForBridgeRetry && session.Sent.Count == 0);
        }

        var (repeat, repeatedSession) = AdvancedRetryChat("repeat-capacity");
        for (var i = 0; i < 3; i++)
        {
            var before = DateTimeOffset.Now; End(repeat, Error(CapacityError));
            Check("repeated capacity errors get a new minute, never an immediate loop " + i, ReadField<DateTimeOffset>(repeat, "_bridgeRetryAt") >= before.AddMinutes(1));
            RetryNow(repeat); var count = i + 1; Pump(() => repeatedSession.Sent.Count == count);
        }
        End(repeat, new JsonObject { ["is_error"] = false });
        Check("success leaves no scheduled recovery", !repeat.WaitingForBridgeRetry && ReadField<object?>(repeat, "_bridgeRetryTimer") is null);

        foreach (var cancel in new[] { "stop", "send", "close", "model", "membership", "session", "account", "work", "reassignment" })
        {
            var (chat, session) = AdvancedRetryChat("retry-cancel-" + cancel);
            End(chat, Error(CapacityError));
            var timer = ReadField<DispatcherTimer>(chat, "_bridgeRetryTimer");
            switch (cancel)
            {
                case "stop": chat.Interrupt(); break;
                case "send": chat.Send("Use my updated task."); Pump(() => session.Sent.Count == 1); break;
                case "close": chat.Close(); break;
                case "model": chat.SetModel("gpt-6.1-sol"); break;
                case "membership": chat.BridgeLabel = ""; break;
                case "session": Field(chat, "_session", new FakeSession()); break;
                case "account": Property(chat, "AccountId", "changed-account"); break;
                case "work": Property(chat, "BridgeWork", new BridgeWorkState()); break;
                case "reassignment": Property(chat, "BridgeCoordinatorAgentId", "new-manager"); break;
            }
            RetryNow(chat);
            Check(cancel + " invalidates the pending retry and stops its timer", !chat.WaitingForBridgeRetry && !timer.IsEnabled
                && session.Sent.Count == (cancel == "send" ? 1 : 0));
            chat.Close();
        }
        var (stopped, stoppedSession) = AdvancedRetryChat("manual-stop-error");
        stopped.Status = "running"; stopped.Interrupt(); End(stopped, Error(CapacityError)); RetryNow(stopped);
        Check("an intentional stop never schedules a retry", !stopped.WaitingForBridgeRetry && stoppedSession.Sent.Count == 0);

        var (queued, queueSession) = AdvancedRetryChat("retry-extended-queue");
        queued.ExtendedQueueEnabled = true; queued.Send("Finish the assigned area."); Pump(() => queueSession.Sent.Count == 1);
        End(queued, Error(CapacityError));
        Check("failed extended chunk is preserved while waiting", queued.WaitingForBridgeRetry && queued.ExtendedQueuePaused && queued.HasQueued);
        RetryNow(queued); Pump(() => queueSession.Sent.Count == 2);
        Check("retry resumes the preserved queue once", !queued.ExtendedQueuePaused && queueSession.Sent[1].Contains("Finish the assigned area."));
        queued.Close();

        var (pending, pendingSession) = AdvancedRetryChat("retry-before-later-queue");
        pending.Send("Finish the current task."); Pump(() => pendingSession.Sent.Count == 1);
        pending.ExtendedQueueEnabled = true; pending.Send("Then verify the second area.");
        End(pending, Error(CapacityError)); RetryNow(pending); Pump(() => pendingSession.Sent.Count == 2);
        Check("later queued work cannot replace recovery of the current task", pending.HasQueued && !pending.ExtendedQueuePaused
            && pendingSession.Sent[1].Contains(BridgeErrorRecovery.ContinuePrompt) && !pendingSession.Sent[1].Contains("Then verify"));
        End(pending, new JsonObject { ["is_error"] = false }); Pump(() => pendingSession.Sent.Count == 3);
        Check("later queued work runs after the recovered task succeeds", pendingSession.Sent[2].Contains("Then verify the second area."));
        pending.Close();

        var (quota, _) = AdvancedRetryChat("retry-quota-priority");
        AppSettings.Current.ContinueAfterLimitResets = true;
        End(quota, Error("usage limit"));
        Check("quota recovery retains its own schedule", quota.WaitingForLimitReset && !quota.WaitingForBridgeRetry);
        quota.Interrupt();
        VerifyRetryBridgeBookkeeping();
    }

    private static void VerifyRetryBridgeBookkeeping()
    {
        var (worker, session) = AdvancedRetryChat("retry-journal");
        var task = new BridgeWorkTask { OwnerId = worker.BridgeAgentId, Title = "Preserve this assignment", State = "running" };
        var work = new BridgeWorkState { Tasks = [task] }; Property(worker, "BridgeWork", work);
        var vm = new MainViewModel();
        var live = Activator.CreateInstance(typeof(MainViewModel).GetNestedType("LiveBridge", Flags)!, nonPublic: true)!;
        var errors = new HashSet<ChatViewModel>();
        Property(live, "Panes", new BridgePaneCollection { worker }); Property(live, "Errored", errors);
        Property(live, "Peers", new PeerTrafficLedger());
        ((IDictionary)typeof(MainViewModel).GetField("_parkedBridges", Flags)!.GetValue(vm)!)[worker] = live;
        worker.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(ChatViewModel.Status)) return;
            Call(vm, "OnBridgePaneStatusChanged", worker); Call(vm, "OnBridgeManagerStatusChanged", worker);
        };
        worker.Status = "running"; End(worker, Error(CapacityError));
        Check("capacity wait retains the durable assignment and its ownership", task.State == "running" && errors.Count == 0);
        var state = typeof(MainViewModel).GetMethod("AgentRunState", Flags)!.Invoke(null, [worker]);
        Check("peers see a pending retry as busy", (string)state! == "waiting_to_retry"
            && (bool)typeof(MainViewModel).GetMethod("IsBusyState", Flags)!.Invoke(null, [state])!);
        Check("peer guidance preserves the recovering worker's area", ((string)typeof(MainViewModel).GetMethod("AgentVerdict", Flags)!
            .Invoke(null, [worker, state, "Preserve this assignment", null, null])!).Contains("Treat its area as taken"));
        RetryNow(worker); Pump(() => session.Sent.Count == 1);
        Check("continuation keeps the same running journal entry", task.State == "running" && work.Tasks.Count == 1 && errors.Count == 0);
        End(worker, Error(CapacityError)); worker.Interrupt();
        Check("cancelling retry releases the task for an explicit retry", task.State == "failed" && !worker.WaitingForBridgeRetry);
        worker.Close();
    }

    private static void BridgeErrorRetryTimerChecks()
    {
        var (chat, session) = AdvancedRetryChat("real-one-minute-retry");
        chat.Status = "running"; var clock = Stopwatch.StartNew(); End(chat, Error(CapacityError));
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) => { if (session.Sent.Count > 0 || clock.Elapsed > TimeSpan.FromSeconds(75)) frame.Continue = false; };
        timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        Check("production dispatcher waits a real minute before continuing", clock.Elapsed >= TimeSpan.FromMinutes(1)
            && clock.Elapsed < TimeSpan.FromSeconds(75) && session.Sent.Count == 1);
        Check("production retry releases its timer after one continuation", !chat.WaitingForBridgeRetry
            && ReadField<object?>(chat, "_bridgeRetryTimer") is null && session.Sent.Single().Contains(BridgeErrorRecovery.ContinuePrompt));
        Console.WriteLine($"Observed delay: {clock.Elapsed.TotalSeconds:F2} seconds.");
    }
}
