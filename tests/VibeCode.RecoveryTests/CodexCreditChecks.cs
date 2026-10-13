using System.IO;
using System.Text.Json.Nodes;
using VibeCode.Protocol;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    private static JsonObject CreditUsage(bool? hasCredits = true, bool unlimited = false, string? status = null)
    {
        var credits = new JsonObject { ["unlimited"] = unlimited, ["balance"] = "62500" };
        if (hasCredits is { } available) credits["hasCredits"] = available;
        return new JsonObject
        {
            ["rateLimits"] = new JsonObject
            {
                ["limitId"] = "codex",
                ["credits"] = credits,
                ["rateLimitReachedType"] = status,
                ["primary"] = new JsonObject { ["usedPercent"] = 100, ["resetsAt"] = 2000000000 },
                ["secondary"] = new JsonObject { ["usedPercent"] = 100, ["resetsAt"] = 2100000000 },
            },
            ["rateLimitsByLimitId"] = new JsonObject
            {
                ["base_model_inference"] = new JsonObject { ["primary"] = new JsonObject { ["usedPercent"] = 25 } },
            },
        };
    }

    private static JsonObject CreditFailure(string error = "usageLimitExceeded") => new()
    {
        ["id"] = "failed-turn",
        ["status"] = "failed",
        ["error"] = new JsonObject { ["message"] = "Usage limit reached", ["codexErrorInfo"] = error },
    };

    private static void CodexCreditChecks()
    {
        foreach (var status in new string?[] { null, "rate_limit_reached" })
        {
            var snapshot = UsageLimitRecovery.CodexSnapshot(CreditUsage(status: status), "gpt-6-astra");
            Check("credits extend exhausted session and weekly allowances: " + status, snapshot is { Available: true, ResetsAt: null });
        }
        Check("unlimited credits extend included usage", UsageLimitRecovery.CodexSnapshot(CreditUsage(false, true), "gpt-6-astra").Available == true);
        Check("no credits preserves exhausted allowance", UsageLimitRecovery.CodexSnapshot(CreditUsage(false), "gpt-6-astra").Available == false);
        Check("balance text alone cannot authorize credit recovery", UsageLimitRecovery.CodexSnapshot(CreditUsage(null), "gpt-6-astra").Available == false);
        var malformed = CreditUsage();
        malformed["rateLimits"]!["credits"]!["hasCredits"] = "true";
        Check("malformed credit flags do not grant allowance", UsageLimitRecovery.CodexSnapshot(malformed, "gpt-6-astra").Available == false);
        malformed["rateLimits"]!["credits"] = "62500";
        Check("malformed credit objects do not crash recovery", UsageLimitRecovery.CodexSnapshot(malformed, "gpt-6-astra").Available == false);

        var mapped = CreditUsage();
        mapped["rateLimitsByLimitId"]!["codex"] = mapped["rateLimits"]!.DeepClone();
        mapped.Remove("rateLimits");
        Check("credits work with the multi-bucket response alone", UsageLimitRecovery.CodexSnapshot(mapped, "gpt-6-astra").Available == true);
        var creditOnly = CreditUsage();
        creditOnly["rateLimits"]!.AsObject().Remove("primary");
        creditOnly["rateLimits"]!.AsObject().Remove("secondary");
        Check("credit-only plans do not require subscription windows", UsageLimitRecovery.CodexSnapshot(creditOnly, "gpt-6-astra").Available == true);
        var reserveFull = CreditUsage();
        reserveFull["rateLimitsByLimitId"]!["base_model_inference"]!["primary"]!["usedPercent"] = 100;
        Check("purchased credits do not invent Reserve allowance", UsageLimitRecovery.CodexSnapshot(reserveFull, "gpt-reserve").Available == false);

        foreach (var status in new[] { "workspace_owner_credits_depleted", "workspace_member_credits_depleted",
                     "workspace_owner_usage_limit_reached", "workspace_member_usage_limit_reached", "future_spending_restriction" })
        {
            var usage = CreditUsage(status: status);
            Check("credits respect account restriction: " + status, UsageLimitRecovery.CodexSnapshot(usage, "gpt-6-astra").Available == false);
            using var blocked = new CreditFixture(usage, reserve: true);
            blocked.Fail();
            Check("restricted accounts never submit a recovery turn: " + status, blocked.Starts.Length == 0 && blocked.Results.Length == 1);
        }
        var capped = CreditUsage();
        capped["rateLimits"]!["individualLimit"] = new JsonObject { ["remainingPercent"] = 0 };
        Check("credits respect the individual spending cap", UsageLimitRecovery.CodexSnapshot(capped, "gpt-6-astra").Available == false);
        using (var blocked = new CreditFixture(capped, reserve: true))
        {
            blocked.Fail();
            Check("spending caps prevent both credit and Reserve retries", blocked.Starts.Length == 0);
        }

        var paidAccount = new CodexAccountInfo { Id = "fixture", SessionPercent = 100, WeekPercent = 100, HasCredits = true, CreditBalance = "62500" };
        Check("account stays ready at 100 percent with credits", !paidAccount.AtLimit && paidAccount.CanUseCredits);
        Check("account explains credit availability at 100 percent", paidAccount.UsageDisplay.Contains("credits available"));
        Check("account remains exhausted without credits", new CodexAccountInfo { Id = "fixture", WeekPercent = 100, HasCredits = false }.AtLimit);
        Check("account spending cap outranks its credits", new CodexAccountInfo
        {
            Id = "fixture",
            HasCredits = true,
            SpendLimit = new CodexSpendLimit { RemainingPercent = 0 }
        }.AtLimit);
        Check("account depletion status outranks cached credits", new CodexAccountInfo
        {
            Id = "fixture",
            HasCredits = true,
            RateLimitStatus = "workspace_owner_credits_depleted"
        }.AtLimit);

        foreach (var reserve in new[] { false, true })
        {
            using var live = new CreditFixture(CreditUsage(), reserve);
            var rejected = new JsonObject
            {
                ["threadId"] = "credit-thread",
                ["cwd"] = Root,
                ["input"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Finish the existing task" },
                    new JsonObject { ["type"] = "localImage", ["path"] = "fixture.png" }),
                ["outputSchema"] = new JsonObject { ["type"] = "object" },
            };
            var input = rejected["input"]!.ToJsonString();
            live.Fail(rejected);
            Check("credits retry once even without a Reserve model: " + reserve, live.Starts.Length == 1);
            var start = live.Starts.Single();
            Check("credit retry preserves the thread, model, effort and speed", start["threadId"]?.ToString() == "credit-thread"
                && start["model"]?.ToString() == "gpt-6-astra" && start["effort"]?.ToString() == "ultra" && start["serviceTier"]?.ToString() == "priority");
            Check("a rejected turn retains its original text and attachments", start["input"]!.ToJsonString() == input);
            Check("credit continuation preserves the response schema", start["outputSchema"]?["type"]?.ToString() == "object");
            Check("credits take priority over available Reserve", live.Events.All(e => e["subtype"]?.ToString() != "codex_reserve_activated")
                && ReadField<string>(live.Session, "_model") == "gpt-6-astra");
            Check("credit recovery keeps the host turn open", live.Results.Length == 0);
            Check("credit recovery only reads usage and starts a turn", live.Requests.SequenceEqual(new[] { "account/rateLimits/read", "turn/start" }));
            live.Fail();
            Check("a second limit does not loop or switch models", live.Starts.Length == 1 && live.Results.Length == 1);
        }

        using (var live = new CreditFixture(CreditUsage()))
        {
            Field(live.Session, "_lastUsage", new JsonObject { ["input_tokens"] = 12, ["output_tokens"] = 7 });
            live.Fail();
            Check("mid-turn credit recovery checkpoints previous usage once", live.Events.Count(e => e["subtype"]?.ToString() == "codex_usage_checkpoint") == 1);
            Check("mid-turn recovery continues without replaying completed actions", live.Starts.Single()["input"]?[0]?["text"]?.ToString().Contains("do not repeat actions that already succeeded") == true);
            Check("accepted credit retry reports its turn as active", ReadField<string>(live.Session, "_turnId") == "credit-retry");
        }
        using (var reserve = new CreditFixture(CreditUsage(false), reserve: true))
        {
            reserve.Fail();
            Check("without credits existing Reserve fallback still works", reserve.Starts.Single()["model"]?.ToString() == "gpt-reserve");
        }
        using (var none = new CreditFixture(CreditUsage(false)))
        {
            none.Fail();
            Check("without credits or Reserve the original error remains", none.Starts.Length == 0 && none.Results.Single()["is_error"]?.ToString() == "true");
        }
        using (var transient = new CreditFixture(CreditUsage(), reserve: true))
        {
            transient.Fail(error: "httpConnectionFailed");
            Check("ordinary transport errors never trigger paid recovery", transient.Requests.Count == 0 && transient.Results.Length == 1);
        }
        using (var disconnected = new CreditFixture(CreditUsage()) { StartError = new IOException("fixture disconnected") })
        {
            disconnected.Fail();
            Check("uncertain retry acceptance is never replayed", disconnected.Starts.Length == 1 && disconnected.Results.Length == 1);
        }
        foreach (var cancel in new[] { "stop", "model", "close" })
        {
            var pending = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var live = new CreditFixture(CreditUsage()) { ReadUsage = () => pending.Task };
            live.Fail(wait: false);
            Check("usage check keeps the logical turn active", ReadField<bool>(live.Session, "_reserveRecoveryPending"));
            if (cancel == "stop") live.Session.InterruptAsync().GetAwaiter().GetResult();
            else if (cancel == "model") live.Session.SetModelAsync("gpt-6.1-sol").GetAwaiter().GetResult();
            else live.Session.Dispose();
            pending.SetResult(new JsonObject { ["result"] = CreditUsage() });
            Pump(() => !ReadField<bool>(live.Session, "_reserveRecoveryPending"));
            Check("cancelled usage check cannot submit paid work: " + cancel, live.Starts.Length == 0);
        }

        var (chat, _) = NewChat("credit-banner");
        chat.Model = "gpt-6-astra";
        Call(chat, "ApplySystem", new JsonObject { ["subtype"] = "codex_credits_available", ["message"] = "Retrying with credits." }, null);
        Check("credit notice leaves the chosen model unchanged", chat.Model == "gpt-6-astra"
            && chat.Items.OfType<BannerItem>().Any(b => b.Text == "Retrying with credits."));
    }

    private sealed class CreditFixture : IDisposable
    {
        internal CodexSession Session { get; }
        internal List<JsonNode> Events { get; } = [];
        internal List<string> Requests { get; } = [];
        private readonly List<JsonObject> _starts = [];
        internal JsonObject[] Starts => _starts.ToArray();
        internal JsonNode[] Results => Events.Where(e => e["type"]?.ToString() == "result").ToArray();
        internal Func<Task<JsonNode?>>? ReadUsage;
        internal Exception? StartError;

        internal CreditFixture(JsonObject usage, bool reserve = false)
        {
            Session = new CodexSession(new CodexSessionOptions { Cwd = Root, Model = "gpt-6-astra", Effort = "ultra", FastMode = true });
            Field(Session, "_launchPending", true);
            Property(Session, "SessionId", "credit-thread");
            if (reserve) Field(Session, "_reserveModel", JsonNode.Parse("""{"id":"gpt-reserve","supportedReasoningEfforts":[{"reasoningEffort":"high"}]}"""));
            Session.MessageReceived += message => Events.Add(message.DeepClone());
            Property(Session, "RequestFixture", new Func<string, JsonObject?, Task<JsonNode?>>((method, payload) =>
            {
                Requests.Add(method);
                if (method == "account/rateLimits/read") return ReadUsage?.Invoke()
                    ?? Task.FromResult<JsonNode?>(new JsonObject { ["result"] = usage.DeepClone() });
                if (method == "turn/start")
                {
                    _starts.Add(payload!.DeepClone().AsObject());
                    if (StartError is { } failure) return Task.FromException<JsonNode?>(failure);
                    return Task.FromResult<JsonNode?>(JsonNode.Parse("""{"result":{"turn":{"id":"credit-retry"}}}"""));
                }
                throw new Exception("Unexpected credit recovery request: " + method);
            }));
        }

        internal void Fail(JsonObject? rejected = null, string error = "usageLimitExceeded", bool wait = true)
        {
            Call(Session, "CompleteTurn", CreditFailure(error), rejected);
            if (wait) Pump(() => !ReadField<bool>(Session, "_reserveRecoveryPending"));
        }

        public void Dispose() => Session.Dispose();
    }
}
