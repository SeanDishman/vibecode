using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using VibeCode.Protocol;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    private static T ReadField<T>(object target, string name) => (T)target.GetType().GetField(name, Flags)!.GetValue(target)!;

    private static void RecoveryLifecycleChecks()
    {
        AppSettings.Current.ContinueAfterLimitResets = true;
        var (timerChat, timerSession) = NewChat("real-recovery-timer");
        var probes = 0;
        SetProbe(timerChat, () => { probes++; return Task.FromResult(new UsageRecoverySnapshot(true)); });
        var elapsed = Stopwatch.StartNew();
        End(timerChat, Error("usage limit"));
        Check("quota wait starts a live dispatcher timer", ReadField<DispatcherTimer>(timerChat, "_limitRecoveryTimer").IsEnabled);
        // Do not call the recovery method or alter its deadlines: the production timer must drive this retry.
        Pump(() => timerSession.Sent.Count != 0);
        Check("real timer waits for the initial delay and sends exactly one continuation",
            elapsed.Elapsed >= TimeSpan.FromSeconds(5) && probes == 1 && timerSession.Sent.Count == 1
            && timerSession.Sent[0].Contains("Continue the interrupted task"));
        Check("successful retry stops its recovery timer", !ReadField<DispatcherTimer>(timerChat, "_limitRecoveryTimer").IsEnabled);
        timerChat.Close();

        var (backoff, backoffSession) = NewChat("repeat-recovery-backoff");
        SetProbe(backoff, () => Task.FromResult(UsageRecoverySnapshot.Unknown));
        int[] earliestSeconds = [5, 60, 120, 240, 300, 300];
        int[] fallbackMinutes = [3, 6, 12, 24, 30, 30];
        for (var attempt = 0; attempt < earliestSeconds.Length; attempt++)
        {
            var before = DateTimeOffset.Now;
            End(backoff, Error("usage limit"));
            var after = DateTimeOffset.Now;
            bool Scheduled(string field, TimeSpan delay) => ReadField<DateTimeOffset>(backoff, field) >= before + delay
                && ReadField<DateTimeOffset>(backoff, field) <= after + delay;
            Check($"retry {attempt + 1} has the expected bounded backoff", Scheduled("_limitRecoveryEarliestResume", TimeSpan.FromSeconds(earliestSeconds[attempt]))
                && Scheduled("_limitRecoveryRetryAt", TimeSpan.FromMinutes(fallbackMinutes[attempt])));
            CheckRecovery(backoff);
            Check($"retry {attempt + 1} cannot bypass the fallback deadline", backoffSession.Sent.Count == attempt);
            // Advance only the stored deadlines, keeping the state transitions and attempt accounting real.
            Field(backoff, "_limitRecoveryEarliestResume", DateTimeOffset.MinValue);
            Field(backoff, "_limitRecoveryRetryAt", DateTimeOffset.MinValue);
            CheckRecovery(backoff);
            Pump(() => backoffSession.Sent.Count == attempt + 1);
            Check($"retry {attempt + 1} preserves its attempt count", ReadField<int>(backoff, "_limitRecoveryAttempts") == attempt + 1);
        }
        backoff.Close();

        foreach (var change in new[] { "stop", "close", "setting", "model", "account", "session" })
        {
            var (chat, session) = NewChat("late-allowance-" + change);
            var reading = new TaskCompletionSource<UsageRecoverySnapshot>();
            SetProbe(chat, () => reading.Task);
            End(chat, Error("usage limit"));
            Field(chat, "_limitRecoveryEarliestResume", DateTimeOffset.MinValue);
            var checking = (Task)Call(chat, "CheckLimitRecoveryAsync")!;
            Check(change + " race really has an outstanding usage request", !checking.IsCompleted);
            switch (change)
            {
                case "stop": chat.Interrupt(); break;
                case "close": chat.Close(); break;
                case "setting":
                    AppSettings.Current.ContinueAfterLimitResets = false;
                    Check("saving disabled recovery succeeds", AppSettings.Current.TrySave() is null);
                    Check("disabling the preference stops the actual waiting timer", !ReadField<DispatcherTimer>(chat, "_limitRecoveryTimer").IsEnabled);
                    break;
                case "model": chat.Model = "gpt-6-luna"; break;
                case "account": Property(chat, "AccountId", "different-test-account"); break;
                case "session": Field(chat, "_session", new FakeSession()); break;
            }
            reading.SetResult(new UsageRecoverySnapshot(true));
            Pump(() => checking.IsCompleted);
            checking.GetAwaiter().GetResult();
            Check(change + " change rejects a late available reading", session.Sent.Count == 0
                && (ReadField<object?>(chat, "_session") is not FakeSession current || current.Sent.Count == 0));
            chat.Close();
            AppSettings.Current.ContinueAfterLimitResets = true;
        }

        var (resumable, resumeSession) = NewChat("setting-reenable");
        End(resumable, Error("usage limit"));
        AppSettings.Current.ContinueAfterLimitResets = false;
        Check("disabling recovery saves successfully", AppSettings.Current.TrySave() is null);
        Check("disabled waiting timer is stopped", !resumable.WaitingForLimitReset && !ReadField<DispatcherTimer>(resumable, "_limitRecoveryTimer").IsEnabled);
        AppSettings.Current.ContinueAfterLimitResets = true;
        Check("enabling recovery saves successfully", AppSettings.Current.TrySave() is null);
        Check("reenabling recovery restarts the parked wait", resumable.WaitingForLimitReset && ReadField<DispatcherTimer>(resumable, "_limitRecoveryTimer").IsEnabled);
        resumable.Interrupt();
        Check("Stop clears the parked wait and stops its timer", !resumable.WaitingForLimitReset && resumeSession.Sent.Count == 0
            && !ReadField<DispatcherTimer>(resumable, "_limitRecoveryTimer").IsEnabled);
        resumable.Close();
    }

    private static void CompactionGuardChecks()
    {
        var (claude, cs) = NewChat("compact-focus", "claude");
        Check("Claude accepts case-insensitive compaction with focus instructions", claude.Send("  /COMPACT Keep the plan and unresolved work  "));
        Check("focus instructions reach the native compact interface exactly", cs.CompactInstructions.SequenceEqual(new[] { "Keep the plan and unresolved work" }) && cs.Sent.Count == 0);
        End(claude, new JsonObject { ["type"] = "result", ["is_error"] = false });
        Check("completed compaction releases its banner and working state", !claude.IsCompactingContext && !claude.IsWorking
            && !claude.Items.OfType<BannerItem>().Any(b => b.Text == "Compacting conversation context…"));
        claude.Close();

        var (codex, cds) = NewChat("compact-rejections");
        Check("Codex rejects unsupported focus instructions without dispatch", !codex.Send("/compact Keep the plan") && cds.Compactions == 0 && cds.Sent.Count == 0);
        Check("compaction rejects attachments without dispatch", !codex.Send("/compact", [new Attachment { Kind = "text", FileName = "notes.txt", Text = "notes" }])
            && cds.Compactions == 0 && cds.Sent.Count == 0);
        Check("similarly named commands are ordinary prompts", codex.Send("/compacter explain this command"));
        Pump(() => cds.Sent.Count == 1);
        Check("command boundary prevents accidental native compaction", cds.Compactions == 0 && cds.Sent.Single().Contains("/compacter"));
        Check("compaction cannot run during an ordinary turn", !codex.Send("/compact") && cds.Compactions == 0);
        codex.Close();

        var (failed, fs) = NewChat("compact-native-failure");
        fs.CompactError = new InvalidOperationException("Native compaction request failed");
        Check("compaction request starts even when the native adapter subsequently fails", failed.Send("/compact"));
        Pump(() => !failed.IsCompactingContext);
        Check("native compaction failure releases working state and reports the error", !failed.IsWorking && !failed.WaitingForLimitReset
            && failed.Items.OfType<BannerItem>().Any(b => b.Text.Contains("Native compaction request failed")));
        failed.Close();
        foreach (var provider in new[] { "glm" })
        {
            var (chat, _) = NewChat("unsupported-compact-" + provider, provider);
            Field(chat, "_session", new NonCompactSession());
            Check(provider + " does not advertise unsupported compaction", chat.Commands.All(c => c.Name != "compact"));
            Check(provider + " reports unsupported compaction without a model request", !chat.Send("/compact")
                && chat.Items.OfType<BannerItem>().Any(b => b.Text.Contains("not supported")));
            chat.Close();
        }
    }

    private static void ProviderAllowanceChecks()
    {
        var snapshotMethod = typeof(UsageService).GetMethod("RecoverySnapshot", Flags)!;
        UsageRecoverySnapshot Claude(string text, string? model) => (UsageRecoverySnapshot)snapshotMethod.Invoke(null, [text, model])!;
        const string report = "Current session: 20% used\nCurrent week (all models): 30% used\nCurrent week (Fable): 100% used · resets Oct 5, 4pm (America/Chicago)\nCurrent week (Opus): 40% used";
        Check("Claude usage checks the selected model's weekly pool", Claude(report, "claude-fable-5-1").Available == false);
        Check("Claude ignores a different model's exhausted pool", Claude(report, "claude-opus-5").Available == true);
        Check("Claude shared session cap blocks every model", Claude(report.Replace("session: 20%", "session: 100%"), "claude-opus-5").Available == false);
        Check("Claude unreadable usage is unknown", Claude("Signed out. Please login.", "claude-fable-5-1").Available is null);
        Check("exhausted windows without reset stamps never invent a reset", UsageRecoverySnapshot.FromWindows([(100, null), (100, DateTimeOffset.Now.AddHours(1))]) is { Available: false, ResetsAt: null });
        foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, -1d })
            Check("invalid quota percentage is unknown: " + invalid, UsageRecoverySnapshot.FromWindows([(invalid, null)]).Available is null);
        Check("quota words in a successful reply cannot schedule recovery", !UsageLimitRecovery.IsLimitError(new JsonObject { ["is_error"] = false, ["result"] = "The quota is exhausted" }));

        using var codex = new CodexSession(new CodexSessionOptions { Cwd = Root, Model = "gpt-6-astra" });
        Field(codex, "_launchPending", true);
        var reads = 0;
        Property(codex, "RequestFixture", new Func<string, JsonObject?, Task<JsonNode?>>((method, _) =>
        {
            Check("Codex recovery reads allowance through its own session", method == "account/rateLimits/read");
            var used = ++reads == 1 ? 25 : 100;
            return Task.FromResult<JsonNode?>(new JsonObject
            {
                ["result"] = new JsonObject
                {
                    ["rateLimits"] = new JsonObject { ["limitId"] = "codex", ["primary"] = new JsonObject { ["usedPercent"] = used } },
                }
            });
        }));
        var first = (Task<UsageRecoverySnapshot>)Call(codex, "ReadRecoveryUsageAsync")!;
        Pump(() => first.IsCompleted);
        Check("fresh Codex allowance can permit recovery", first.GetAwaiter().GetResult().Available == true);
        var second = (Task<UsageRecoverySnapshot>)Call(codex, "ReadRecoveryUsageAsync")!;
        Pump(() => second.IsCompleted);
        Check("Codex does not reuse a cached available reading", second.GetAwaiter().GetResult().Available == false && reads == 2);
        GlmAllowanceChecks();
    }

    private static void GlmAllowanceChecks()
    {
        var accounts = ApiKeyAccountService.Instance;
        var first = accounts.Add("glm", "coding-fixture-key-one", "Test quota exhausted", GlmPreset.ZaiCodingPlan);
        var rotated = accounts.Add("glm", "coding-fixture-key-two", "Test quota available", GlmPreset.ZaiCodingPlan);
        accounts.Select("glm", first.Id);
        var requests = new List<string>();
        using var http = new HttpClient(new UsageHttpFixture(request =>
        {
            var key = request.Headers.GetValues("Authorization").Single(); requests.Add(key);
            Check("GLM recovery queries only the quota monitor", request.Method == HttpMethod.Get && request.RequestUri!.ToString() == GlmUsageService.QuotaUrl);
            if (requests.Count == 3) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            var percent = key == "coding-fixture-key-one" ? 100 : 20;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new JsonObject
                {
                    ["success"] = true,
                    ["code"] = 200,
                    ["data"] = new JsonObject
                    {
                        ["limits"] = new JsonArray(
                    new JsonObject { ["type"] = "CREDIT_LIMIT", ["unit"] = 3, ["number"] = 5, ["percentage"] = percent },
                    new JsonObject { ["type"] = "TIME_LIMIT", ["percentage"] = 100 })
                    },
                }.ToJsonString())
            };
        }));
        var service = GlmUsageService.Instance;
        var originalHttp = ReadField<HttpClient>(service, "_http"); Field(service, "_http", http);
        using var session = new GlmSession(new GlmSessionOptions
        {
            Cwd = Root,
            Backend = GlmPreset.ZaiCodingPlan,
            ApiKeys = ["coding-fixture-key-one", "coding-fixture-key-two"],
            ApiKeyAccountIds = [first.Id, rotated.Id]
        });
        UsageRecoverySnapshot Read(string? account = null)
        {
            var method = typeof(UsageLimitRecovery).GetMethod("ProbeAsync", Flags)!;
            var task = (Task<UsageRecoverySnapshot>)method.Invoke(null, ["glm", "glm-5.3", account ?? first.Id, session])!;
            Pump(() => task.IsCompleted); return task.GetAwaiter().GetResult();
        }
        try
        {
            Check("GLM selected exhausted key remains blocked", Read().Available == false);
            Field(session, "_keyIndex", 1);
            Check("GLM recovery follows the active rotated key and ignores tool-only quota", Read().Available == true
                && requests.SequenceEqual(new[] { "coding-fixture-key-one", "coding-fixture-key-two" }));
            Check("failed GLM refresh cannot reuse healthy cached usage", Read().Available is null && rotated.GlmUsage.IsStale && requests.Count == 3);
        }
        finally
        {
            Field(service, "_http", originalHttp);
            // Keep the isolated Coding Plan fixture selected for the subsequent mixed-provider setup checks.
            accounts.Select("glm", rotated.Id);
        }
    }

    private sealed class UsageHttpFixture(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
    }


    private sealed class NonCompactSession : ICodingSession
    {
#pragma warning disable CS0067
        public event Action<JsonNode>? MessageReceived;
        public event Action<PermissionRequest>? PermissionRequested;
        public event Action<string>? PermissionCancelled;
        public event Action<int, string>? Exited;
        public event Action? Initialized;
#pragma warning restore CS0067
        public JsonArray Commands { get; } = [];
        public JsonArray Models { get; } = [];
        public string? SessionId => "unsupported-compact-fixture";
        public bool HasExited => false;
        public void Start() { }
        public void SendUser(JsonNode content) => throw new Exception("Unsupported compaction must not reach SendUser");
        public Task InterruptAsync() => Task.CompletedTask;
        public Task SetPermissionModeAsync(string mode) => Task.CompletedTask;
        public Task SetModelAsync(string? model, string? effort = null) => Task.CompletedTask;
        public void RespondPermission(string requestId, JsonObject result, string? toolUseId) { }
        public void Dispose() { }
    }

}
