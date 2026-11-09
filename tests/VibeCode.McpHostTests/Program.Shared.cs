using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using VibeCode.AgentStatus.Mcp.Contracts;
using VibeCode.Services;

internal static partial class Program
{
    private const string FakeServerVariable = "VIBECODE_TEST_FAKE_MCP";
    private static readonly Type HubType = App.GetType("VibeCode.Services.SharedMcpHub", throwOnError: true)!;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);

    // ───────────────────────────── the fake stdio MCP server (child role) ─────────────────────────────

    /// <summary>A tiny MCP server over stdio. Its tool results report what it saw: its process id, how often it was
    /// initialized, the capabilities and request ids it received, and the progress token it was given.</summary>
    private static int RunFakeServer()
    {
        var writer = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        var gate = new object();
        void Send(JsonObject message) { lock (gate) writer.Write(message.ToJsonString() + "\n"); }
        JsonObject Result(JsonNode? id, JsonObject result) => new() { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result };
        var initializes = 0;
        var initialized = 0;
        JsonNode? capabilities = null;
        var answers = new ConcurrentDictionary<string, TaskCompletionSource<JsonObject>>();
        var reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        while (reader.ReadLine() is { } line)
        {
            if (JsonNode.Parse(line) is not JsonObject message) continue;
            var id = message["id"]?.DeepClone();
            var method = AgentStatusReport.Text(message["method"]);
            if (method is null)
            {
                if (AgentStatusReport.Text(message["id"]) is { } key && answers.TryRemove(key, out var waiter)) waiter.TrySetResult(message);
                continue;
            }
            switch (method)
            {
                case "initialize":
                    Interlocked.Increment(ref initializes);
                    capabilities = (message["params"] as JsonObject)?["capabilities"]?.DeepClone();
                    Send(Result(id, new JsonObject
                    {
                        ["protocolVersion"] = (message["params"] as JsonObject)?["protocolVersion"]?.DeepClone(),
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = true } },
                        ["serverInfo"] = new JsonObject { ["name"] = "fake-shared", ["version"] = "1.0.0" },
                    }));
                    break;
                case "notifications/initialized":
                    Interlocked.Increment(ref initialized);
                    break;
                case "tools/list":
                    Send(Result(id, new JsonObject
                    {
                        ["tools"] = new JsonArray(new[] { "echo", "notify", "progress", "ask_roots", "crash" }
                            .Select(name => (JsonNode?)new JsonObject { ["name"] = name, ["inputSchema"] = new JsonObject { ["type"] = "object" } }).ToArray()),
                    }));
                    break;
                case "tools/call":
                    var parameters = (JsonObject)message["params"]!.DeepClone();
                    _ = Task.Run(async () =>
                    {
                        var tool = AgentStatusReport.Text(parameters["name"]);
                        var arguments = parameters["arguments"] as JsonObject ?? new JsonObject();
                        var seen = new JsonObject
                        {
                            ["pid"] = Environment.ProcessId, ["initializes"] = Volatile.Read(ref initializes),
                            ["initialized"] = Volatile.Read(ref initialized), ["capabilities"] = capabilities?.DeepClone(),
                            ["server_id"] = id?.DeepClone(), ["args"] = arguments.DeepClone(),
                            ["progress_token"] = (parameters["_meta"] as JsonObject)?["progressToken"]?.DeepClone(),
                        };
                        switch (tool)
                        {
                            case "echo" when arguments["delay_ms"] is JsonValue delay:
                                await Task.Delay(delay.GetValue<int>());
                                break;
                            case "notify":
                                Send(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/tools/list_changed" });
                                break;
                            case "progress":
                                Send(new JsonObject
                                {
                                    ["jsonrpc"] = "2.0", ["method"] = "notifications/progress",
                                    ["params"] = new JsonObject { ["progressToken"] = seen["progress_token"]?.DeepClone(), ["progress"] = 1, ["total"] = 2 },
                                });
                                break;
                            case "ask_roots":
                                var answer = answers["roots-1"] = new TaskCompletionSource<JsonObject>();
                                Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = "roots-1", ["method"] = "roots/list" });
                                seen["answer"] = (await answer.Task.WaitAsync(TimeSpan.FromSeconds(10))).DeepClone();
                                break;
                            case "crash":
                                Environment.Exit(3);
                                break;
                        }
                        Send(Result(id, new JsonObject
                        {
                            ["structuredContent"] = seen,
                            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "ok" }),
                        }));
                    });
                    break;
                default:
                    if (id is not null)
                        Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "unknown" } });
                    break;
            }
        }
        return 0;
    }

    // ───────────────────────────── I73: one shared process, many agents ─────────────────────────────

    /// <summary>A client's GET event stream, collecting every server-initiated message.</summary>
    private sealed class EventStream : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private HttpResponseMessage? _response;
        public ConcurrentQueue<JsonObject> Received { get; } = new();
        public HttpStatusCode Status { get; private set; }

        public static async Task<EventStream> OpenAsync(HttpMcpClient client)
        {
            var stream = new EventStream();
            var request = new HttpRequestMessage(HttpMethod.Get, client.Url);
            request.Headers.Accept.ParseAdd("text/event-stream");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", client.Token);
            request.Headers.Add("Mcp-Session-Id", client.Session);
            stream._response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            stream.Status = stream._response.StatusCode;
            var body = await stream._response.Content.ReadAsStreamAsync();
            _ = Task.Run(async () =>
            {
                try
                {
                    await foreach (var message in ReadEventsAsync(body, stream._stop.Token)) stream.Received.Enqueue(message);
                }
                catch (Exception) { /* the stream ends with the test */ }
            });
            await Task.Delay(500);   // the server attaches the stream just after sending its headers
            return stream;
        }

        public async Task<bool> WaitForAsync(Func<JsonObject, bool> match)
        {
            for (var attempt = 0; attempt < 50; attempt++)
            {
                if (Received.Any(match)) return true;
                await Task.Delay(100);
            }
            return false;
        }

        public void Dispose()
        {
            _stop.Cancel();
            _response?.Dispose();
        }
    }

    private static JsonNode? Seen(HttpReply reply) => reply.Body?["result"]?["structuredContent"];

    private static async Task VerifySharedServer()
    {
        var hub = HubType.GetProperty("Instance", Any)!.GetValue(null)!;
        void Project(List<McpServerDefinition> servers, object owner) => HubType.GetMethod("Project", Any)!.Invoke(hub, [servers, owner]);
        void Release(object owner) => HubType.GetMethod("Release", Any)!.Invoke(hub, [owner]);
        var definition = new McpServerDefinition
        {
            Name = "fake-shared", Command = Environment.ProcessPath!, ShareAcrossAgents = true,
            StartupTimeoutSeconds = 20, ToolTimeoutSeconds = 30,
        };
        definition.Environment[FakeServerVariable] = "1";
        string Summary() => (string)HubType.GetMethod("Summary", Any)!.Invoke(hub, [definition.Id])!;

        // Projection: opted-in, valid stdio servers only.
        var notShared = definition.Clone();
        notShared.ShareAcrossAgents = false;
        var broken = definition.Clone();
        broken.Id = Guid.NewGuid().ToString("N");
        broken.Name = "broken-shared";
        broken.Command = "";
        var unrelated = new List<McpServerDefinition> { notShared, broken };
        Project(unrelated, new object());
        Check(unrelated.All(server => server.IsStdio), "a server without the opt-in, or one that is invalid, keeps its own stdio launch");

        var ownerA = new object();
        var ownerB = new object();
        var listA = new List<McpServerDefinition> { definition.Clone() };
        var listB = new List<McpServerDefinition> { definition.Clone() };
        Project(listA, ownerA);
        Project(listB, ownerB);
        var a = listA[0];
        var b = listB[0];
        Check(a.Transport == McpCatalog.HttpTransport && a.Url == b.Url && a.Url.Contains("/mcp/", StringComparison.Ordinal)
              && TokenOf(a) != TokenOf(b) && a.RuntimeName == definition.RuntimeName && a.Command.Length == 0,
            "each agent gets its own token on the shared server's one loopback URL, under the server's usual runtime name");
        var relaunch = new List<McpServerDefinition> { definition.Clone() };   // every launch projects a fresh catalog copy
        Project(relaunch, ownerA);
        Check(TokenOf(relaunch[0]) == TokenOf(a), "an agent relaunching keeps its lease");
        Check(Summary() == "shared · 0 processes · 2 agents", $"no process runs before an agent connects ({Summary()})");
        var fallback = ((List<McpServerDefinition>)HostType.GetMethod("WithStdioFallbacks", Any)!.Invoke(null, [new[] { a }])!).Single();
        Check(fallback.IsStdio && fallback.Command == definition.Command && fallback.Environment[FakeServerVariable] == "1",
            "an ACP agent without HTTP MCP gets the server's own stdio launch back");

        var clientA = new HttpMcpClient(a.Url, TokenOf(a), "agent-a");
        var initA = await clientA.InitializeAsync();
        Check(initA.Status == HttpStatusCode.OK && initA.Session is not null
              && AgentStatusReport.Text(clientA.InitializeResult?["serverInfo"]?["name"]) == "fake-shared", "the first agent's initialize starts the server");
        var clientB = new HttpMcpClient(b.Url, TokenOf(b), "agent-b");
        await clientB.InitializeAsync();
        Check(JsonNode.DeepEquals(clientA.InitializeResult, clientB.InitializeResult), "a later agent gets the cached initialize result");

        var first = await clientB.SendAsync(ToolCall(1, "echo"));
        Check(first.ContentType == "text/event-stream" && first.Body?["id"]?.GetValue<int>() == 1, "a shared server answers over an event stream, with the caller's id");
        Check(Seen(first)?["initializes"]?.GetValue<int>() == 1 && Seen(first)?["initialized"]?.GetValue<int>() == 1,
            "the server was initialized exactly once for both agents");
        Check(Seen(first)?["capabilities"] is JsonObject { Count: 0 }, "the shared server is offered no sampling, roots or elicitation");
        var pid = Seen(first)!["pid"]!.GetValue<int>();
        Check(Summary() == "shared · 1 process · 2 agents", $"Settings shows one process for two agents ({Summary()})");
        var job = (IntPtr)(nint)App.GetType("VibeCode.Protocol.ProcessJob", throwOnError: true)!.GetField("_job", Any)!.GetValue(null)!;
        using (var child = Process.GetProcessById(pid))
            Check(job != IntPtr.Zero && IsProcessInJob(child.Handle, job, out var inJob) && inJob, "the shared child is owned by VibeCode's ProcessJob");

        var listed = await clientA.SendAsync(Rpc(2, "tools/list"));
        Check((listed.Body?["result"]?["tools"] as JsonArray)?.Count == 5, "tools/list passes through");

        // The real CLIs against a shared server, whose replies are event streams rather than JSON bodies.
        if (ClaudeExe() is { } claude)
        {
            var byClaude = ClaudeMcpList(claude, "shared", a);
            Check(byClaude.Contains(a.RuntimeName + ": " + a.Url, StringComparison.Ordinal) && byClaude.Contains("Connected", StringComparison.OrdinalIgnoreCase),
                "the real Claude CLI connects to a shared server's loopback route from VibeCode's own --mcp-config JSON (claude mcp list)");
        }
        else Console.WriteLine("SKIP Claude interop with a shared server: no claude.exe found");
        if (CodexExe() is { } codex)
        {
            var byCodex = CodexMcpStatus(codex, a);
            Check(byCodex.Contains("\"echo\"", StringComparison.Ordinal) && byCodex.Contains("\"ask_roots\"", StringComparison.Ordinal),
                "the real Codex MCP client lists a shared server's tools through its event-stream replies");
        }
        else Console.WriteLine("SKIP Codex interop with a shared server: no codex.exe found");

        // The same request id from two agents at once.
        var slow = clientA.SendAsync(ToolCall(7, "echo", new JsonObject { ["who"] = "A", ["delay_ms"] = 400 }));
        var fast = clientB.SendAsync(ToolCall(7, "echo", new JsonObject { ["who"] = "B", ["delay_ms"] = 50 }));
        await Task.WhenAll(slow, fast);
        Check(slow.Result.Body?["id"]?.GetValue<int>() == 7 && fast.Result.Body?["id"]?.GetValue<int>() == 7
              && AgentStatusReport.Text(Seen(slow.Result)?["args"]?["who"]) == "A" && AgentStatusReport.Text(Seen(fast.Result)?["args"]?["who"]) == "B",
            "two agents using the same request id each get their own reply");
        Check(!JsonNode.DeepEquals(Seen(slow.Result)?["server_id"], Seen(fast.Result)?["server_id"]), "the server saw two different request ids");

        // Broadcast, and progress for the caller only.
        using var streamA = await EventStream.OpenAsync(clientA);
        using var streamB = await EventStream.OpenAsync(clientB);
        Check(streamA.Status == HttpStatusCode.OK && streamB.Status == HttpStatusCode.OK, "agents can open a GET event stream on a shared server");
        await clientA.SendAsync(ToolCall(8, "notify"));
        static bool ListChanged(JsonObject message) => AgentStatusReport.Text(message["method"]) == "notifications/tools/list_changed";
        Check(await streamA.WaitForAsync(ListChanged) && await streamB.WaitForAsync(ListChanged), "a server notification reaches every agent");
        var progress = await clientA.SendAsync(ToolCall(9, "progress"));
        Check(progress.Body?["error"] is null, "a call without a progress token still works");
        var withToken = ToolCall(10, "progress");
        withToken["params"]!["_meta"] = new JsonObject { ["progressToken"] = "agent-a-token" };
        var tracked = await clientA.SendAsync(withToken);
        Check(tracked.Events.Any(message => AgentStatusReport.Text(message["method"]) == "notifications/progress"
                                            && AgentStatusReport.Text(message["params"]?["progressToken"]) == "agent-a-token"),
            "progress reaches the calling agent on its own reply stream, under its own token");
        Check(AgentStatusReport.Text(Seen(tracked)?["progress_token"]) is { } serverToken && serverToken != "agent-a-token",
            "the server saw a progress token of VibeCode's, so two agents' tokens cannot collide");
        Check(!streamB.Received.Any(message => AgentStatusReport.Text(message["method"]) == "notifications/progress"), "the other agent gets no progress for that call");

        // A server-to-client request.
        var roots = await clientA.SendAsync(ToolCall(11, "ask_roots"));
        Check(Seen(roots)?["answer"]?["error"]?["code"]?.GetValue<int>() == -32601, "the server's roots/list request is refused, never put to an arbitrary agent");
        Check(Summary().Contains("not shareable", StringComparison.Ordinal), $"Settings says the server needs a copy per agent ({Summary()})");

        // Crash and restart.
        var crashed = await clientA.SendAsync(ToolCall(12, "crash"));
        Check(crashed.Body?["error"] is JsonObject && crashed.Body?["id"]?.GetValue<int>() == 12, "a call in flight when the server crashes gets an error with its own id");
        JsonNode? restarted = null;
        var clock = Stopwatch.StartNew();
        for (var attempt = 0; attempt < 60 && restarted is null; attempt++)
        {
            var reply = await clientB.SendAsync(ToolCall(20 + attempt, "echo"));
            if (Seen(reply) is { } seen) restarted = seen;
            else await Task.Delay(250);
        }
        Check(restarted?["pid"]?.GetValue<int>() is { } newPid && newPid != pid && restarted["initializes"]?.GetValue<int>() == 1
              && restarted["initialized"]?.GetValue<int>() == 1, $"the crashed server restarts and is initialized again ({clock.Elapsed.TotalSeconds:0.0} s)");
        Check(clock.Elapsed >= TimeSpan.FromSeconds(0.9), "the restart waits for its backoff");
        Check((await clientA.SendAsync(ToolCall(90, "echo"))).Body?["result"] is not null, "agents keep their MCP sessions across the restart");

        // Leases end with their chats.
        Release(ownerA);
        Check(Summary().StartsWith("shared · 1 process · 1 agent", StringComparison.Ordinal), $"a closed chat releases its lease ({Summary()})");
        Check((await clientA.SendAsync(ToolCall(91, "echo"))).Status == HttpStatusCode.Unauthorized, "a released lease's token stops working");
        var lastPid = Seen(await clientB.SendAsync(ToolCall(92, "echo")))!["pid"]!.GetValue<int>();
        using var last = Process.GetProcessById(lastPid);
        Release(ownerB);
        Check(last.WaitForExit(10_000), "the last lease ending stops the shared process");
        Check(Summary().StartsWith("shared · 0 processes · 0 agents", StringComparison.Ordinal), $"nothing is left running ({Summary()})");
    }
}
