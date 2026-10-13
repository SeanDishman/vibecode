using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using VibeCode.Protocol;
using VibeCode.Services;

internal static partial class Program
{
    private static readonly BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static int _checks;
    private static string _root = "";
    private static object? Call(string type, string method, params object?[] arguments) => typeof(GeminiRuntime).Assembly
        .GetType("VibeCode.Services." + type)!.GetMethod(method, Flags)!.Invoke(null, arguments);
    private static void Check(string name, bool condition)
    {
        if (!condition) throw new InvalidOperationException(name);
        _checks++;
    }

    private static async Task<int> Main(string[] args)
    {
        _root = Path.Combine(Environment.CurrentDirectory, "artifacts", "gemini-integration", "tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("VIBECODE_DATA_DIR", Path.Combine(_root, "data"));
        Environment.SetEnvironmentVariable("GEMINI_CLI_HOME", Path.Combine(_root, "google-home"));
        try
        {
            if (!args.Contains("--fixtures-only")) await OfficialHandshake();
            Environment.SetEnvironmentVariable("VIBECODE_GEMINI_PATH", Path.Combine(AppContext.BaseDirectory, "fake-gemini.mjs"));
            Directory.CreateDirectory(GeminiRuntime.ConfigDirectory);
            File.WriteAllText(Path.Combine(GeminiRuntime.ConfigDirectory, "oauth_creds.json"), "{\"access_token\":\"fixture\",\"expiry_date\":1}");
            VerifyUsage();
            VerifyQuota();
            await VerifyQuotaRefresh();
            VerifyAuthIsolation();
            await VerifySession(resume: false);
            await VerifySession(resume: true);
            await VerifyNativeSpeechLease();
            Console.WriteLine($"PASS: {_checks} Gemini CLI, ACP, subscription, MCP, usage, resume, cancellation and native-speech lifetime checks. Evidence: {_root}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static async Task OfficialHandshake()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        using var process = new Process { StartInfo = GeminiRuntime.CreateStartInfo(_root, "--acp") };
        process.Start();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":1,\"clientCapabilities\":{},\"clientInfo\":{\"name\":\"vibecode-test\",\"version\":\"1\"}}}");
            await process.StandardInput.FlushAsync();
            JsonObject? result = null;
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                JsonNode? response;
                try { response = JsonNode.Parse(line); } catch { continue; }
                if (response?["id"]?.ToString() == "1") { result = response["result"]?.AsObject(); break; }
            }
            Check("official CLI supports ACP v1", result?["protocolVersion"]?.ToString() == "1");
            Check("official CLI advertises Google sign-in", (result?["authMethods"] as JsonArray)?.Any(a => a?["id"]?.ToString() == "oauth-personal") == true);
            Check("official CLI supports resume", result?["agentCapabilities"]?["loadSession"]?.GetValue<bool>() == true);
            Check("official CLI supports HTTP MCP", result?["agentCapabilities"]?["mcpCapabilities"]?["http"]?.GetValue<bool>() == true);
            Check("official CLI supports images", result?["agentCapabilities"]?["promptCapabilities"]?["image"]?.GetValue<bool>() == true);
            File.WriteAllText(Path.Combine(_root, "official-initialize.json"), result!.ToJsonString());
        }
        finally { if (!process.HasExited) process.Kill(true); await stderr; }
    }

    private static void VerifyAuthIsolation()
    {
        Environment.SetEnvironmentVariable("GEMINI_API_KEY", "fixture-api-key");
        Environment.SetEnvironmentVariable("GOOGLE_API_KEY", "fixture-api-key");
        Environment.SetEnvironmentVariable("GOOGLE_GENAI_USE_VERTEXAI", "true");
        var psi = GeminiRuntime.CreateStartInfo(_root, "--acp");
        Check("Google plan does not inherit API billing", !psi.Environment.ContainsKey("GEMINI_API_KEY") && !psi.Environment.ContainsKey("GOOGLE_API_KEY") && !psi.Environment.ContainsKey("GOOGLE_GENAI_USE_VERTEXAI"));
        var settings = JsonNode.Parse(File.ReadAllText(psi.Environment["GEMINI_CLI_SYSTEM_SETTINGS_PATH"]!));
        Check("project settings cannot silently select API auth", settings?["security"]?["auth"]?["selectedType"]?.ToString() == "oauth-personal"
            && settings?["security"]?["auth"]?["enforcedType"]?.ToString() == "oauth-personal");
        Check("no shell concatenation in CLI launch", !psi.UseShellExecute && psi.ArgumentList.Last() == "--acp");
    }

    private static void VerifyUsage()
    {
        const string source = """
            {"sessionId":"usage-test"}
            {"id":"a","type":"gemini","model":"pro","tokens":null}
            {"id":"a","type":"gemini","model":"pro","tokens":{"input":1000,"cached":400,"output":60,"thoughts":40}}
            {"id":"a","type":"gemini","model":"pro","tokens":{"input":1000,"cached":400,"output":60,"thoughts":40}}
            {"id":"b","type":"gemini","model":"flash","tokens":{"input":500,"cached":200,"output":30,"thoughts":10}}
            {"unfinished":
            """;
        var snapshot = Call("GeminiSessionUsage", "Parse", source, "usage-test");
        var report = (JsonObject)Call("GeminiSessionUsage", "TurnReport", null, snapshot, null)!;
        Check("cached input is not counted twice", report["usage"]?["input_tokens"]?.ToString() == "900" && report["usage"]?["cache_read_input_tokens"]?.ToString() == "600");
        Check("thinking is included in generated tokens", report["usage"]?["output_tokens"]?.ToString() == "140");
        Check("actual model split survives auto routing", report["modelUsage"]?.AsObject().Count == 2 && report["modelUsage"]?["pro"]?["outputTokens"]?.ToString() == "100");
        Check("included subscription usage has no API cost", report["total_cost_usd"]?.ToString() == "0");
        var repeat = (JsonObject)Call("GeminiSessionUsage", "TurnReport", snapshot, snapshot, null)!;
        Check("resumed history and repeated upserts do not get charged again", repeat["usage"]?["input_tokens"]?.ToString() == "0" && repeat["usage"]?["output_tokens"]?.ToString() == "0");
        Check("a different session cannot contaminate usage", Call("GeminiSessionUsage", "Parse", source, "another-session") is null);
        var fallback = (JsonObject)Call("GeminiSessionUsage", "TurnReport", null, null, JsonNode.Parse("{\"_meta\":{\"quota\":{\"token_count\":{\"input_tokens\":32,\"output_tokens\":4}}}}"))!;
        Check("ACP-only counts are explicitly incomplete", fallback["usage_is_incomplete"]?.GetValue<bool>() == true && fallback["usage"]?["input_tokens"]?.ToString() == "32");
        var partial = Call("GeminiSessionUsage", "Parse", "{\"sessionId\":\"partial\",\"messages\":[{\"id\":\"a\",\"type\":\"gemini\",\"model\":\"pro\",\"tokens\":{\"input\":1000,\"cached\":400,\"output\":60,\"thoughts\":40}}]}", "partial");
        var reconciled = (JsonObject)Call("GeminiSessionUsage", "TurnReport", null, partial, JsonNode.Parse("""{"_meta":{"quota":{"token_count":{"input_tokens":1500,"output_tokens":90},"model_usage":[{"model":"pro","token_count":{"input_tokens":1000,"output_tokens":60}},{"model":"flash","token_count":{"input_tokens":500,"output_tokens":30}}]}}}"""))!;
        Check("partly flushed transcripts retain all ACP input", reconciled["usage"]?["input_tokens"]?.ToString() == "1100" && reconciled["usage"]?["cache_read_input_tokens"]?.ToString() == "400");
        Check("partial usage preserves known thinking and model attribution", reconciled["usage"]?["output_tokens"]?.ToString() == "130" && reconciled["modelUsage"]?["flash"]?["inputTokens"]?.ToString() == "500" && reconciled["usage_is_incomplete"]?.GetValue<bool>() == true);
        var legacy = Call("GeminiSessionUsage", "Parse", "{\"sessionId\":\"old\",\"messages\":[{\"id\":\"a\",\"type\":\"gemini\",\"tokens\":{\"input\":20,\"cached\":5,\"thoughts\":2,\"output\":3}}]}", "old");
        Check("legacy JSON transcripts remain readable", legacy is not null);
    }

    private static void VerifyQuota()
    {
        var quota = JsonNode.Parse("""{"buckets":[{"modelId":"pro","remainingFraction":0.25,"remainingAmount":"375","resetTime":"2026-10-14T07:00:00Z"},{"modelId":"flash","remainingFraction":0,"remainingAmount":"0"},{"modelId":"unknown"}]}""");
        var windows = (IReadOnlyList<GeminiQuotaWindow>)Call("GeminiUsageService", "Parse", quota)!;
        Check("remaining fraction is converted to consumed percent", windows[0].Percent == 75 && windows[0].Remaining == 375);
        Check("provider reset time survives", windows[0].ResetsAt?.Hour == 7);
        Check("exhausted buckets retain zero remaining", windows[1].Percent == 100 && windows[1].Remaining == 0);
        Check("unknown quota is not fabricated", windows.Count == 2);
        Check("model pools are displayed separately", windows.Select(w => w.Model).SequenceEqual(new[] { "pro", "flash" }));
    }

    private static async Task VerifySession(bool resume)
    {
        var node = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe");
        var servers = new[]
        {
            new McpServerDefinition { Id = "stdio", Name = "local", Command = node, Arguments = ["fixture.js"], Environment = new() { ["TEST_ENV"] = "yes" } },
            new McpServerDefinition { Id = "http", Name = "remote", Transport = "http", Url = "https://example.invalid/mcp", Headers = new() { ["X-Test"] = "fixture" } },
            new McpServerDefinition { Id = "sse", Name = "events", Transport = "sse", Url = "https://example.invalid/sse" },
            new McpServerDefinition { Id = "excluded", Name = "excluded", UseGemini = false, Command = node },
        };
        using var session = new GeminiSession(new()
        {
            Cwd = _root,
            Model = "gemini-test-pro",
            PermissionMode = "plan",
            AppendSystemPrompt = "VibeCode project and MCP instructions",
            Resume = resume ? "f50bd21a-867d-4548-b551-000000000001" : null,
            McpServers = servers
        });
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var results = Channel.CreateUnbounded<JsonNode>();
        var messages = new ConcurrentQueue<JsonNode>();
        var permissions = 0;
        JsonObject? pendingUsage = null;
        session.Initialized += () => ready.TrySetResult();
        session.MessageReceived += m => { messages.Enqueue(m.DeepClone()); if (m["type"]?.ToString() == "result") results.Writer.TryWrite(m.DeepClone()); };
        var permissionInputs = new ConcurrentQueue<PermissionRequest>();
        session.PermissionRequested += p =>
        {
            Interlocked.Increment(ref permissions); permissionInputs.Enqueue(p);
            pendingUsage = (JsonObject?)typeof(GeminiSession).GetMethod("UsageSnapshot", Flags)!.Invoke(session, null);
            session.RespondPermission(p.RequestId, new JsonObject { ["behavior"] = "allow", ["updatedInput"] = p.Input?.DeepClone() }, p.ToolUseId);
        };
        session.Start();
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Check("Gemini starts with its own live catalog", !session.HasExited && session.Models.Count == 5 && session.Models.OfType<JsonObject>().All(m => m["supportsEffort"]?.GetValue<bool>() == false));
        Check("Gemini 4 and new Flash IDs pass through when the runtime advertises them", session.Models.OfType<JsonObject>().Any(m => m["value"]?.ToString() == "gemini-4-argon")
            && session.Models.OfType<JsonObject>().Any(m => m["value"]?.ToString() == "gemini-3.8-flash"));
        await session.SetModelAsync("gemini-4-argon");
        Check("new runtime models can be selected without a VibeCode release", session.Models.OfType<JsonObject>().Single(m => m["value"]?.ToString() == "gemini-4-argon")["isDefault"]?.GetValue<bool>() == true);
        async Task<JsonNode> Turn(string prompt)
        {
            session.SendUser(JsonValue.Create(prompt)!);
            return await results.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
        }
        var result = await Turn("permission");
        Check("ACP permissions round-trip", permissions == 1 && result["is_error"]?.GetValue<bool>() == false);
        Check("native Gemini permissions show the command without rawInput", permissionInputs.First().Input?["command"]?.ToString() == "echo Gemini");
        Check("Gemini publishes incurred usage while awaiting tools", messages.Any(m => m["subtype"]?.ToString() == "usage_update" && m["usage"]?["output_tokens"]?.ToString() == "100"));
        Check("closing mid-turn can capture the current native usage and model", pendingUsage?["usage"]?["input_tokens"]?.ToString() == "600"
            && pendingUsage?["modelUsage"]?["gemini-test-pro"]?["outputTokens"]?.ToString() == "100");
        Check("each turn reads full usage despite shorter ACP totals", result["usage"]?["output_tokens"]?.ToString() == "140" && result["usage"]?["input_tokens"]?.ToString() == "900");
        result = await Turn("next message");
        Check("second turn excludes previous transcript consumption", result["usage"]?["output_tokens"]?.ToString() == "140");
        result = await Turn("mcp-permission");
        Check("MCP display titles round-trip without being mistaken for trusted built-ins", permissions == 2 && permissionInputs.Last().ToolName == "chat_set_title(title: \"Example\")" && result["is_error"]?.GetValue<bool>() == false);
        result = await Turn("empty-with-usage");
        Check("empty answers still retain incurred usage", result["is_error"]?.GetValue<bool>() == true && result["usage"]?["output_tokens"]?.ToString() == "140");
        result = await Turn("fail-with-usage");
        Check("failed model calls retain observed usage", result["is_error"]?.GetValue<bool>() == true && result["usage"]?["output_tokens"]?.ToString() == "5");
        session.SendUser(JsonValue.Create("wait-for-cancel")!);
        await Task.Delay(100);
        await session.InterruptAsync();
        result = await results.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Check("cancellation keeps incurred tokens", result["subtype"]?.ToString() == "interrupted" && result["usage"]?["output_tokens"]?.ToString() == "140");
        Check("streaming answers and thoughts reach the UI protocol", messages.Any(m => m["type"]?.ToString() == "stream_event"));
        if (resume) Check("resume replays old messages without treating them as a new turn", messages.Any(m => m["is_replay"]?.GetValue<bool>() == true)
            && messages.Any(m => m["subtype"]?.ToString() == "resume_boundary"));
        var requests = File.ReadLines(Path.Combine(GeminiRuntime.Home, "wire.jsonl")).Select(l => JsonNode.Parse(l)!).ToArray();
        var creation = requests.Last(r => r["method"]?.ToString() == (resume ? "session/load" : "session/new"));
        var sent = creation["params"]!["mcpServers"]!.AsArray();
        Check("stdio, HTTP and SSE MCP reach Gemini; excluded servers stay out", sent.Count == 3 && sent.Count(s => s?["type"] is null) == 1);
        Check("MCP headers and environment survive", sent.Any(s => s?["headers"]?.ToJsonString().Contains("X-Test") == true) && sent.Any(s => s?["env"]?.ToJsonString().Contains("TEST_ENV") == true));
        Check("saved model and plan mode use Gemini methods", requests.Any(r => r["method"]?.ToString() == "session/set_model" && r["params"]?["modelId"]?.ToString() == "gemini-test-pro")
            && requests.Any(r => r["method"]?.ToString() == "session/set_mode" && r["params"]?["modeId"]?.ToString() == "plan"));
        Check("Gemini uses native model selection and transcript usage without extra probes", !requests.Any(r => r["method"]?.ToString() == "session/set_config_option" || r["params"]?["prompt"]?.ToJsonString().Contains("/usage") == true));
    }

    private static async Task VerifyNativeSpeechLease()
    {
        var type = typeof(SpeechService).Assembly.GetType("VibeCode.Services.NativeSpeechWorkQueue")!;
        var queue = Activator.CreateInstance(type)!;
        var run = type.GetMethod("RunAsync")!.MakeGenericMethod(typeof(string));
        Task<string> Start(Func<Task<string>> work, CancellationToken token) => (Task<string>)run.Invoke(queue, [work, token])!;
        var native = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource();
        var first = Start(() => native.Task, stop.Token);
        stop.Cancel();
        var secondStarted = false;
        var second = Start(() => { secondStarted = true; return Task.FromResult("second"); }, CancellationToken.None);
        Check("abandoning speech does not release the active native model", !secondStarted && !first.IsCompleted);
        using var queuedStop = new CancellationTokenSource();
        var cancelledWorkRan = false;
        var third = Start(() => { cancelledWorkRan = true; return Task.FromResult("third"); }, queuedStop.Token);
        queuedStop.Cancel();
        try { await third; } catch (OperationCanceledException) { }
        Check("cancelled queued dictation never enters the native runtime", !cancelledWorkRan && third.IsCanceled);
        native.SetResult("late first result");
        await first; await second;
        Check("next dictation proceeds after native cleanup", secondStarted && second.Result == "second");
        try { await Start(() => Task.FromException<string>(new IOException("fixture")), CancellationToken.None); } catch (IOException) { }
        Check("native failure releases the lease", await Start(() => Task.FromResult("recovered"), CancellationToken.None) == "recovered");
    }
}
