using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using VibeCode.AgentStatus.Mcp;
using VibeCode.AgentStatus.Mcp.Bridge;
using VibeCode.AgentStatus.Mcp.Contracts;
using VibeCode.Services;

/// <summary>
/// I72 (Bridge tools served in-process over loopback MCP Streamable HTTP) and I73 (one shared process per opted-in
/// stdio MCP server). Real HTTP clients talk to the real endpoint; the Bridge results are compared with the stdio
/// relay's over the real per-session pipe. This executable doubles as the fake stdio MCP server for I73. Everything
/// runs against a temporary data folder; the optional Claude check uses an isolated CLAUDE_CONFIG_DIR and only runs
/// `claude mcp add/list`, which never starts a model turn.
/// </summary>
internal static partial class Program
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static int Checks;
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "VibeCode-mcp-host-" + Guid.NewGuid().ToString("N"));
    private static readonly Assembly App = typeof(McpCatalog).Assembly;
    private static readonly Type ConnectionType = App.GetType("VibeCode.Services.BridgeMcpConnection", throwOnError: true)!;
    private static readonly Type HostType = App.GetType("VibeCode.Services.McpLoopbackHost", throwOnError: true)!;
    private static readonly HttpClient Http = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(120) };

    [STAThread]
    private static int Main(string[] args)
    {
        // Child role first, before anything touches settings.
        if (Environment.GetEnvironmentVariable(FakeServerVariable) == "1") return RunFakeServer();

        Directory.CreateDirectory(Root);
        Environment.SetEnvironmentVariable("VIBECODE_DATA_DIR", Path.Combine(Root, "data"));
        Environment.SetEnvironmentVariable("VIBECODE_HIDDEN", "1");
        var dispatcher = StartDispatcher();
        try
        {
            var only = args.FirstOrDefault(a => a.StartsWith("--only=", StringComparison.Ordinal))?["--only=".Length..];
            if (only == "readers") VerifyProcessReaders(dispatcher, baseline: args.Contains("--baseline"));
            if (only is null or "bridge") VerifyBridgeOverHttp(dispatcher, includeTimeout: !args.Contains("--quick")).GetAwaiter().GetResult();
            if (only is null or "fallback") VerifyFallbacks(dispatcher);
            if (only is null or "claude") VerifyClaudeInterop(dispatcher);
            if (only is null or "codex") VerifyCodexInterop(dispatcher);
            if (only is null or "shared") VerifySharedServer().GetAwaiter().GetResult();
            if (only is null or "ui") VerifyRenderedUi();
            Console.WriteLine($"PASS: {Checks} MCP host checks. No provider calls.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL " + ex);
            return 1;
        }
        finally
        {
            dispatcher.InvokeShutdown();
            try { Directory.Delete(Root, recursive: true); } catch { /* temp folder; best effort */ }
        }
    }

    private static void Check(bool value, string name)
    {
        if (!value) throw new InvalidOperationException(name);
        Checks++;
        Console.WriteLine("PASS " + name);
    }

    private static Dispatcher StartDispatcher()
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        }) { IsBackground = true, Name = "test UI dispatcher" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        return dispatcher!;
    }

    // ───────────────────────────── I72: Bridge tools over HTTP ─────────────────────────────

    private sealed record Call(string Tool, JsonObject Arguments, int Thread);

    private static (IDisposable Connection, ConcurrentQueue<Call> Calls) NewConnection(Dispatcher dispatcher, string caller,
        Func<string, JsonObject, JsonObject?>? special = null)
    {
        var calls = new ConcurrentQueue<Call>();
        Func<string, JsonObject, JsonObject> handler = (tool, args) =>
        {
            calls.Enqueue(new Call(tool, (JsonObject)args.DeepClone(), Environment.CurrentManagedThreadId));
            if (special?.Invoke(tool, args) is { } answer) return answer;
            return tool switch
            {
                "bridge_send_message" => throw new BridgeMcpException("recipient_unknown", "No agent is called that.",
                    "Refresh bridge_list_agents and copy message_recipient exactly.", "not_started"),
                "chat_set_title" => throw new StatusValidationException("This chat already has a title."),
                "bridge_agent_status" => throw new InvalidOperationException("handler exploded"),
                _ => new JsonObject { ["caller"] = caller, ["tool"] = tool, ["joined"] = true, ["agents"] = new JsonArray() },
            };
        };
        var connection = (IDisposable)Activator.CreateInstance(ConnectionType, Any, null, [dispatcher, handler], null)!;
        return (connection, calls);
    }

    private static McpServerDefinition Registration(IDisposable connection) =>
        (McpServerDefinition)ConnectionType.GetMethod("Registration", Any)!.Invoke(connection, null)!;

    private static string PipeName(IDisposable connection) => (string)ConnectionType.GetProperty("PipeName", Any)!.GetValue(connection)!;

    private static string TokenOf(McpServerDefinition http) => http.Headers["Authorization"]["Bearer ".Length..];

    private static JsonObject Rpc(int id, string method, JsonObject? parameters = null)
    {
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (parameters is not null) message["params"] = parameters;
        return message;
    }

    private static JsonObject Notification(string method) => new() { ["jsonrpc"] = "2.0", ["method"] = method };

    private static JsonObject InitializeParams(string client) => new()
    {
        ["protocolVersion"] = "2025-06-18",
        ["capabilities"] = new JsonObject { ["roots"] = new JsonObject { ["listChanged"] = true }, ["sampling"] = new JsonObject() },
        ["clientInfo"] = new JsonObject { ["name"] = client, ["version"] = "1.0.0" },
    };

    private static JsonObject ToolCall(int id, string tool, JsonObject? arguments = null) =>
        Rpc(id, "tools/call", new JsonObject { ["name"] = tool, ["arguments"] = arguments ?? new JsonObject() });

    /// <summary>One MCP client session over HTTP, using the Streamable HTTP transport.</summary>
    private sealed class HttpMcpClient(string url, string token, string name)
    {
        public string Url { get; } = url;
        public string Token { get; } = token;
        public string? Session { get; private set; }
        public JsonObject? InitializeResult { get; private set; }

        public async Task<HttpReply> InitializeAsync()
        {
            var reply = await PostAsync(Url, Token, Rpc(1, "initialize", InitializeParams(name)));
            Session = reply.Session;
            InitializeResult = reply.Body?["result"] as JsonObject;
            var initialized = await PostAsync(Url, Token, Notification("notifications/initialized"), Session);
            Check(initialized.Status == HttpStatusCode.Accepted, $"{name}: notifications/initialized is accepted with 202");
            return reply;
        }

        public Task<HttpReply> SendAsync(JsonNode message) => PostAsync(Url, Token, message, Session);
    }

    private sealed record HttpReply(HttpStatusCode Status, JsonNode? Body, string? Session, string? ContentType, List<JsonObject> Events);

    private static async Task<HttpReply> PostAsync(string url, string? token, JsonNode body, string? session = null,
        Action<HttpRequestMessage>? tweak = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (session is not null) request.Headers.Add("Mcp-Session-Id", session);
        tweak?.Invoke(request);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        var sessionId = response.Headers.TryGetValues("Mcp-Session-Id", out var values) ? values.First() : null;
        var type = response.Content.Headers.ContentType?.MediaType;
        var events = new List<JsonObject>();
        JsonNode? parsed;
        if (type == "text/event-stream")
        {
            await foreach (var message in ReadEventsAsync(await response.Content.ReadAsStreamAsync())) events.Add(message);
            parsed = events.LastOrDefault(message => !message.ContainsKey("method"));
        }
        else
        {
            var text = await response.Content.ReadAsStringAsync();
            parsed = text.Length == 0 ? null : JsonNode.Parse(text);
        }
        return new HttpReply(response.StatusCode, parsed, sessionId, type, events);
    }

    private static async Task<HttpStatusCode> SendAsync(HttpMethod method, string url, string token, string? session)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (session is not null) request.Headers.Add("Mcp-Session-Id", session);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        return response.StatusCode;
    }

    private static async IAsyncEnumerable<JsonObject> ReadEventsAsync(Stream stream, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0) yield return (JsonObject)JsonNode.Parse(data.ToString())!;
                data.Clear();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal)) data.Append(line[5..].TrimStart());
        }
    }

    /// <summary>A tool result as the agent sees it, minus the per-failure diagnostic id that never repeats.</summary>
    private static string Comparable(JsonNode? result)
    {
        var copy = result?.DeepClone() as JsonObject ?? new JsonObject();
        if (copy["structuredContent"] is JsonObject structured && structured["error"] is JsonObject error)
        {
            error.Remove("diagnostic_id");
            copy["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = structured.ToJsonString() });
        }
        return copy.ToJsonString();
    }

    private static async Task VerifyBridgeOverHttp(Dispatcher dispatcher, bool includeTimeout)
    {
        Check(AppSettings.Current.BridgeToolsInProcess, "Serve Bridge tools in-process defaults to on");
        var block = new ManualResetEventSlim();
        var (connection, calls) = NewConnection(dispatcher, "A", (tool, _) =>
        {
            if (tool != "bridge_read_messages") return null;
            block.Wait(TimeSpan.FromSeconds(40));   // holds the UI thread past the relay's 30 s budget
            return new JsonObject { ["messages"] = new JsonArray() };
        });
        var (other, otherCalls) = NewConnection(dispatcher, "B");
        try
        {
            var http = Registration(connection);
            var otherHttp = Registration(other);
            Check(http.Transport == McpCatalog.HttpTransport && http.Url.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)
                  && http.Url.EndsWith("/bridge", StringComparison.Ordinal), "the Bridge registration is a loopback Streamable HTTP URL at /bridge");
            Check(http.Id == "vibecode-bridge-v1" && http.Name == "bridge" && http.RuntimeName == McpCatalog.RuntimeServerName(
                      new McpServerDefinition { Id = "vibecode-bridge-v1", Name = "bridge" }), "the HTTP registration keeps the Bridge's id and runtime name, so tool names do not change");
            Check(http.UseClaude && http.UseCodex && http.Command.Length == 0
                  && http.Environment.Count == 0 && http.StartupTimeoutSeconds == 30 && http.ToolTimeoutSeconds == 45,
                "the HTTP registration targets every CLI with the relay's timeouts and no helper command");
            Check(http.Headers.TryGetValue("Authorization", out var authorization) && authorization.StartsWith("Bearer ", StringComparison.Ordinal)
                  && TokenOf(http).Length >= 40, "the registration carries a random per-chat bearer token");
            Check(TokenOf(http) != TokenOf(otherHttp) && http.Url == otherHttp.Url, "two chats share the endpoint but not the token");
            Check(TokenOf(Registration(connection)) == TokenOf(http), "a chat relaunching keeps its token");

            var client = new HttpMcpClient(http.Url, TokenOf(http), "test-claude");
            var init = await client.InitializeAsync();
            Check(init.Status == HttpStatusCode.OK && init.ContentType == "application/json" && init.Session is { Length: 32 },
                "initialize answers JSON and opens an Mcp-Session-Id");

            // The stdio relay exactly as `VibeCode.exe --bridge-mcp` runs it, over this chat's real pipe.
            var pipe = PipeName(connection);
            var relay = new AgentStatusMcpHost(BridgeMcpTools.Create((tool, args) => BridgeMcpClient.Invoke(pipe, tool, args)),
                serverName: "vibecode-bridge", instructions: BridgeMcpTools.Instructions, maxMessageCharacters: BridgeMcpClient.MaxRequestCharacters);
            var relayInit = relay.Dispatch(Rpc(1, "initialize", InitializeParams("test-relay")))!["result"];
            relay.Dispatch(Notification("notifications/initialized"));
            Check(JsonNode.DeepEquals(client.InitializeResult, relayInit), "initialize returns what the stdio relay returns (server info, instructions, capabilities, version)");
            Check(AgentStatusReport.Text(client.InitializeResult?["serverInfo"]?["name"]) == "vibecode-bridge"
                  && AgentStatusReport.Text(client.InitializeResult?["instructions"]) == BridgeMcpTools.Instructions, "the in-app server announces itself as vibecode-bridge with the Bridge instructions");

            var list = await client.SendAsync(Rpc(2, "tools/list"));
            var tools = (list.Body?["result"]?["tools"] as JsonArray)?.Select(tool => AgentStatusReport.Text(tool?["name"])).ToArray() ?? [];
            Check(JsonNode.DeepEquals(list.Body?["result"], relay.Dispatch(Rpc(2, "tools/list"))!["result"]), "tools/list is identical to the stdio relay's");
            Check(tools.Length == BridgeMcpTools.Create((_, _) => new JsonObject()).Count && tools.Contains(BridgeMcpTools.AgentNameToolName)
                  && tools.Contains("bridge_read_chat"), $"tools/list serves the whole registry ({tools.Length} tools, including bridge_set_name)");

            async Task<(JsonNode? Http, JsonNode? Relay)> Both(int id, string tool, JsonObject arguments)
            {
                var overHttp = await client.SendAsync(ToolCall(id, tool, (JsonObject)arguments.DeepClone()));
                Check(overHttp.Status == HttpStatusCode.OK && AgentStatusReport.Text(overHttp.Body?["jsonrpc"]) == "2.0"
                      && overHttp.Body?["id"]?.GetValue<int>() == id, $"{tool}: a JSON-RPC reply with the caller's id");
                var overRelay = relay.Dispatch(ToolCall(id, tool, (JsonObject)arguments.DeepClone()))!["result"];
                return (overHttp.Body?["result"], overRelay);
            }

            calls.Clear();
            var (listed, listedRelay) = await Both(3, "bridge_list_agents", new JsonObject());
            Check(Comparable(listed) == Comparable(listedRelay) && listed?["isError"]?.GetValue<bool>() == false
                  && AgentStatusReport.Text(listed?["structuredContent"]?["caller"]) == "A", "a successful call returns the handler's receipt exactly as the relay does");
            Check(calls.Count == 2 && calls.All(call => call.Thread == dispatcher.Thread.ManagedThreadId),
                "the handler runs on the UI dispatcher for both transports");

            var (rejected, rejectedRelay) = await Both(4, "bridge_send_message", new JsonObject { ["recipient"] = "nobody", ["message"] = "hello" });
            Check(Comparable(rejected) == Comparable(rejectedRelay) && rejected?["isError"]?.GetValue<bool>() == true
                  && AgentStatusReport.Text(rejected?["structuredContent"]?["error"]?["code"]) == "recipient_unknown"
                  && AgentStatusReport.Text(rejected?["structuredContent"]?["error"]?["phase"]) == "execution"
                  && AgentStatusReport.Text(rejected?["structuredContent"]?["error"]?["tool"]) == "bridge_send_message",
                "a Bridge error keeps its code, phase, tool and request_status exactly as the relay reports it");

            calls.Clear();
            var (invalid, invalidRelay) = await Both(5, "bridge_send_message", new JsonObject { ["recipient"] = "x", ["message"] = "m", ["sender_id"] = "forged" });
            Check(Comparable(invalid) == Comparable(invalidRelay) && calls.IsEmpty
                  && AgentStatusReport.Text(invalid?["structuredContent"]?["error"]?["phase"]) == "validation",
                "an argument that tries to pick the sender is refused in validation, before any handler runs");

            calls.Clear();
            await Both(6, "bridge_send_message", new JsonObject { ["agent_id"] = "peer-7", ["message"] = "alias" });
            Check(calls.Count == 2 && calls.All(call => AgentStatusReport.Text(call.Arguments["recipient"]) == "peer-7" && !call.Arguments.ContainsKey("agent_id")),
                "the legacy agent_id alias still reaches the handler as recipient");

            var (refused, refusedRelay) = await Both(7, "chat_set_title", new JsonObject { ["title"] = "Fix login redirect" });
            Check(Comparable(refused) == Comparable(refusedRelay) && AgentStatusReport.Text(refused?["structuredContent"]?["error"]?["code"]) == "request_rejected",
                "a handler's validation refusal is the relay's request_rejected receipt");

            var (failed, failedRelay) = await Both(8, "bridge_agent_status", new JsonObject());
            Check(Comparable(failed) == Comparable(failedRelay) && AgentStatusReport.Text(failed?["structuredContent"]?["error"]?["code"]) == "internal_error"
                  && AgentStatusReport.Text(failed?["structuredContent"]?["error"]?["diagnostic_id"]) is { Length: 32 },
                "an unexpected handler failure is the relay's internal_error with a diagnostic_id");

            var unknown = await client.SendAsync(ToolCall(9, "bridge_does_not_exist"));
            Check(JsonNode.DeepEquals(unknown.Body, relay.Dispatch(ToolCall(9, "bridge_does_not_exist"))), "an unknown tool gets the relay's JSON-RPC error");

            otherCalls.Clear();
            var otherClient = new HttpMcpClient(otherHttp.Url, TokenOf(otherHttp), "test-codex");
            await otherClient.InitializeAsync();
            var fromOther = await otherClient.SendAsync(ToolCall(1, "bridge_list_agents"));
            Check(AgentStatusReport.Text(fromOther.Body?["result"]?["structuredContent"]?["caller"]) == "B" && otherCalls.Count == 1,
                "each bearer token reaches only its own chat's handler (caller identity)");
            var crossed = await PostAsync(http.Url, TokenOf(otherHttp), ToolCall(10, "bridge_list_agents"), client.Session);
            Check(crossed.Status == HttpStatusCode.NotFound, "another chat's token cannot use this chat's MCP session");

            // Transport rejections.
            Check((await PostAsync(http.Url, null, Rpc(1, "initialize", InitializeParams("x")))).Status == HttpStatusCode.Unauthorized,
                "a request without a bearer token is rejected with 401");
            Check((await PostAsync(http.Url, "wrong-token", Rpc(1, "initialize", InitializeParams("x")))).Status == HttpStatusCode.Unauthorized,
                "a wrong bearer token is rejected with 401");
            Check((await PostAsync(http.Url.Replace("/bridge", "/mcp/other"), TokenOf(http), Rpc(1, "initialize", InitializeParams("x")))).Status == HttpStatusCode.NotFound,
                "a token opens only its own path");
            Check((await PostAsync(http.Url, TokenOf(http), Rpc(1, "initialize", InitializeParams("x")),
                      tweak: request => request.Headers.TryAddWithoutValidation("Origin", "http://evil.example"))).Status == HttpStatusCode.Forbidden,
                "a foreign Origin is rejected with 403");
            var port = new Uri(http.Url).Port;
            Check((await PostAsync(http.Url, TokenOf(http), Rpc(1, "initialize", InitializeParams("x")),
                      tweak: request => request.Headers.Host = "rebind.evil.example:" + port)).Status == HttpStatusCode.Forbidden,
                "a foreign Host header (DNS rebinding) is rejected with 403");
            Check((await PostAsync(http.Url, TokenOf(http), Rpc(1, "initialize", InitializeParams("x")),
                      tweak: request => request.Headers.TryAddWithoutValidation("Origin", "http://127.0.0.1:" + port))).Status == HttpStatusCode.OK,
                "the endpoint's own origin is allowed");
            Check((await PostAsync(http.Url, TokenOf(http), Rpc(11, "tools/list"))).Status == HttpStatusCode.BadRequest,
                "a request without Mcp-Session-Id is rejected with 400");
            Check((await PostAsync(http.Url, TokenOf(http), Rpc(11, "tools/list"), "0123456789abcdef0123456789abcdef")).Status == HttpStatusCode.NotFound,
                "an unknown Mcp-Session-Id is rejected with 404");
            Check(await SendAsync(HttpMethod.Get, http.Url, TokenOf(http), client.Session) == HttpStatusCode.MethodNotAllowed,
                "GET is 405: the Bridge sends no server-initiated messages");
            var oversized = await client.SendAsync(ToolCall(12, "bridge_send_message",
                new JsonObject { ["recipient"] = "x", ["message"] = new string('a', BridgeMcpClient.MaxRequestCharacters) }));
            Check(oversized.Status == (HttpStatusCode)413, "a request over the relay's 65,536-character frame limit is rejected with 413");
            using (var raw = new TcpClient())
            {
                await raw.ConnectAsync(IPAddress.Loopback, port);
                var stream = raw.GetStream();
                var body = Encoding.UTF8.GetBytes(Rpc(13, "tools/list").ToJsonString());
                var head = $"POST /bridge HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nAuthorization: Bearer {TokenOf(http)}\r\nMcp-Session-Id: {client.Session}\r\n" +
                           "Content-Type: application/json\r\nTransfer-Encoding: chunked\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                await stream.WriteAsync(Encoding.ASCII.GetBytes(body.Length.ToString("X") + "\r\n"));
                await stream.WriteAsync(body);
                await stream.WriteAsync("\r\n0\r\n\r\n"u8.ToArray());
                var answer = await new StreamReader(stream).ReadToEndAsync();
                Check(answer.StartsWith("HTTP/1.1 200", StringComparison.Ordinal) && answer.Contains("\"tools\":["), "a chunked request body is accepted");
            }

            if (includeTimeout)
            {
                var started = Stopwatch.StartNew();
                var late = await client.SendAsync(ToolCall(14, "bridge_read_messages"));
                started.Stop();
                block.Set();
                Check(AgentStatusReport.Text(late.Body?["result"]?["structuredContent"]?["error"]?["code"]) == "connection_timeout"
                      && AgentStatusReport.Text(late.Body?["result"]?["structuredContent"]?["error"]?["request_status"]) == "unknown"
                      && started.Elapsed >= TimeSpan.FromSeconds(29) && started.Elapsed < TimeSpan.FromSeconds(40),
                    $"a call the UI does not answer within 30 s returns the relay's connection_timeout receipt ({started.Elapsed.TotalSeconds:0.0} s)");
                await dispatcher.InvokeAsync(() => { });   // the blocked call has finished
            }

            Check(await SendAsync(HttpMethod.Delete, http.Url, TokenOf(http), client.Session) == HttpStatusCode.OK
                  && (await client.SendAsync(Rpc(15, "tools/list"))).Status == HttpStatusCode.NotFound, "DELETE ends the session");
            other.Dispose();
            Check((await otherClient.SendAsync(Rpc(2, "tools/list"))).Status == HttpStatusCode.Unauthorized,
                "a closed chat's token stops working at once");
        }
        finally
        {
            block.Set();
            connection.Dispose();
            other.Dispose();
        }
    }

    // ───────────────────────────── I72: fallbacks and projections ─────────────────────────────

    private static void VerifyFallbacks(Dispatcher dispatcher)
    {
        var (connection, _) = NewConnection(dispatcher, "F");
        try
        {
            var http = Registration(connection);
            var pipe = PipeName(connection);
            bool IsRelay(McpServerDefinition definition) => definition.IsStdio && definition.Arguments.LastOrDefault() == "--bridge-mcp"
                && definition.Environment.TryGetValue(BridgeMcpClient.PipeEnvironment, out var name) && name == pipe && definition.Id == http.Id;

            // ACP: HTTP only for an agent that declares mcpCapabilities.http.
            JsonNode Capabilities(bool http) => new JsonObject { ["agentCapabilities"] = new JsonObject { ["mcpCapabilities"] = new JsonObject { ["http"] = http } } };
            var forAcp = (Func<IEnumerable<McpServerDefinition>?, JsonNode?, IEnumerable<McpServerDefinition>?>)Delegate.CreateDelegate(
                typeof(Func<IEnumerable<McpServerDefinition>?, JsonNode?, IEnumerable<McpServerDefinition>?>), HostType.GetMethod("ForAcpAgent", Any)!);
            Check(forAcp([http], Capabilities(true))!.Single() is var kept && kept.Transport == McpCatalog.HttpTransport && kept.Url == http.Url,
                "an ACP agent declaring mcpCapabilities.http keeps the HTTP Bridge");
            Check(IsRelay(forAcp([http], Capabilities(false))!.Single()) && IsRelay(forAcp([http], new JsonObject())!.Single()),
                "an ACP agent without HTTP MCP support gets the stdio relay for this chat's pipe");
            // The CLI projections.
            var claude = McpCatalog.BuildClaudeConfig([http])["mcpServers"]?[http.RuntimeName];
            Check(AgentStatusReport.Text(claude?["type"]) == "http" && AgentStatusReport.Text(claude?["url"]) == http.Url
                  && AgentStatusReport.Text(claude?["headers"]?["Authorization"]) == "Bearer " + TokenOf(http) && claude?["command"] is null,
                "Claude's --mcp-config gets the HTTP entry with its token header and no helper command");
            var codex = McpCatalog.BuildCodexProjection([http]);
            var line = codex.ConfigOverrides.Single();
            Check(line.Contains("url = \"" + http.Url + "\"", StringComparison.Ordinal) && line.Contains("env_http_headers", StringComparison.Ordinal)
                  && !line.Contains(TokenOf(http), StringComparison.Ordinal) && codex.Environment.Values.Contains("Bearer " + TokenOf(http)),
                "Codex gets the URL on its command line and the token only through an environment variable");
            Check(line.Contains("\"bridge_list_agents\" = { approval_mode = \"approve\" }", StringComparison.Ordinal)
                  && line.Contains("\"" + BridgeMcpTools.AgentNameToolName + "\" = { approval_mode = \"approve\" }", StringComparison.Ordinal),
                "Codex still preapproves every Bridge tool over HTTP");
            foreach (var provider in new[] { "claude", "codex" }) McpCatalog.EnsureLaunchReady([http], provider);
            Check(true, "the HTTP registration passes every CLI's launch validation");

            // The user's switch, and an endpoint that cannot listen.
            AppSettings.Current.BridgeToolsInProcess = false;
            try { Check(IsRelay(Registration(connection)), "with 'Serve Bridge tools in-process' off a chat launches the stdio relay"); }
            finally { AppSettings.Current.BridgeToolsInProcess = true; }

            using var blocker = new TcpListener(IPAddress.Loopback, 0);
            blocker.Start();
            var taken = ((IPEndPoint)blocker.LocalEndpoint).Port;
            Check(HostType.GetMethod("TryStart", Any)!.Invoke(null, [taken]) is null, $"the endpoint reports failure when port {taken} cannot be bound");
            var shared = HostType.GetField("_shared", Any)!;
            var failedAt = HostType.GetField("_failedAt", Any)!;
            var running = shared.GetValue(null);
            shared.SetValue(null, null);
            failedAt.SetValue(null, DateTime.UtcNow);   // as if the start just failed
            var (fresh, _) = NewConnection(dispatcher, "G");
            try
            {
                var fallback = Registration(fresh);
                Check(fallback.IsStdio && fallback.Arguments.LastOrDefault() == "--bridge-mcp"
                      && fallback.Environment[BridgeMcpClient.PipeEnvironment] == PipeName(fresh),
                    "a chat launched while the endpoint cannot listen gets the stdio relay automatically");
            }
            finally
            {
                fresh.Dispose();
                shared.SetValue(null, running);
                failedAt.SetValue(null, DateTime.MinValue);
            }
        }
        finally { connection.Dispose(); }
    }

    // ───────────────────────────── interop with the real Claude and Codex CLIs ─────────────────────────────

    private static string? ClaudeExe() => new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe"),
        }.FirstOrDefault(File.Exists);

    private static string? CodexExe() => new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules", "@openai", "codex",
                "node_modules", "@openai", "codex-win32-x64", "vendor", "x86_64-pc-windows-msvc", "bin", "codex.exe"),
        }.FirstOrDefault(File.Exists);

    /// <summary>`claude mcp list` (its health check connects to every server) for VibeCode's exact --mcp-config JSON,
    /// placed as the project's .mcp.json, in a fresh isolated config folder: no account and no API key, so nothing can
    /// start a model turn or touch the user's own config.</summary>
    private static string ClaudeMcpList(string claude, string name, McpServerDefinition http)
    {
        var home = Path.Combine(Root, "claude-config-" + name);
        var work = Path.Combine(Root, "claude-work-" + name);
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(work);
        File.WriteAllText(Path.Combine(work, ".mcp.json"), McpCatalog.BuildClaudeConfig([http]).ToJsonString());
        File.WriteAllText(Path.Combine(home, "settings.json"), new JsonObject { ["enableAllProjectMcpServers"] = true }.ToJsonString());
        string Run(params string[] arguments)
        {
            var psi = new ProcessStartInfo(claude) { WorkingDirectory = work, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
            foreach (var argument in arguments) psi.ArgumentList.Add(argument);
            psi.Environment["CLAUDE_CONFIG_DIR"] = home;
            foreach (var variable in new[] { "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "CLAUDE_CODE_OAUTH_TOKEN", "ANTHROPIC_BASE_URL" })
                psi.Environment.Remove(variable);
            psi.Environment["DISABLE_AUTOUPDATER"] = "1";
            psi.Environment["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1";
            using var process = Process.Start(psi)!;
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(90_000)) { try { process.Kill(true); } catch { } }
            return output.Result + error.Result;
        }
        var listed = Run("mcp", "list");
        Console.WriteLine("  claude mcp list: " + listed.Replace("\r", "").Replace("\n", " | ").Trim());
        Check(File.Exists(Path.Combine(home, ".claude.json")), $"{name}: the Claude check used its own isolated config folder");
        return listed;
    }

    /// <summary>VibeCode's exact Codex projection (-c override plus header environment variable) handed to the real
    /// app-server, which lists the server's tools over HTTP. No account, no thread and no turn are involved.</summary>
    private static string CodexMcpStatus(string codex, McpServerDefinition http)
    {
        var home = Path.Combine(Root, "codex-home-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(home);
        var projection = McpCatalog.BuildCodexProjection([http]);
        var psi = new ProcessStartInfo(codex) { WorkingDirectory = Root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false) };
        psi.ArgumentList.Add("app-server");
        foreach (var value in projection.ConfigOverrides)
        {
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(value);
        }
        foreach (var pair in projection.Environment) psi.Environment[pair.Key] = pair.Value;
        psi.Environment["CODEX_HOME"] = home;   // isolated: no account, no user config
        foreach (var variable in new[] { "OPENAI_API_KEY", "CODEX_API_KEY" }) psi.Environment.Remove(variable);
        using var process = Process.Start(psi)!;
        try
        {
            _ = process.StandardError.ReadToEndAsync();
            var replies = new ConcurrentDictionary<int, JsonObject>();
            _ = Task.Run(async () =>
            {
                while (await process.StandardOutput.ReadLineAsync() is { } line)
                    if (JsonNode.Parse(line) is JsonObject message && message["id"] is JsonValue id && id.TryGetValue<int>(out var number) && message["method"] is null)
                        replies[number] = message;
            });
            JsonObject? Ask(int id, string method, JsonObject parameters)
            {
                process.StandardInput.Write(new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters }.ToJsonString() + "\n");
                process.StandardInput.Flush();
                for (var waited = 0; waited < 900 && !replies.ContainsKey(id); waited++) Thread.Sleep(100);
                return replies.GetValueOrDefault(id);
            }
            Check(Ask(1, "initialize", new JsonObject { ["clientInfo"] = new JsonObject { ["name"] = "vibecode-test", ["version"] = "1.0.0" } })?["result"] is not null,
                $"{http.Name}: the real Codex app-server starts with VibeCode's HTTP projection");
            process.StandardInput.Write(new JsonObject { ["method"] = "initialized", ["params"] = new JsonObject() }.ToJsonString() + "\n");
            process.StandardInput.Flush();
            var text = Ask(2, "mcpServerStatus/list", new JsonObject { ["serverName"] = http.RuntimeName })?.ToJsonString() ?? "(no reply)";
            Console.WriteLine("  codex mcpServerStatus/list: " + (text.Length > 300 ? text[..300] + "…" : text));
            return text;
        }
        finally
        {
            try { process.Kill(entireProcessTree: true); } catch { }
        }
    }

    private static void VerifyClaudeInterop(Dispatcher dispatcher)
    {
        if (ClaudeExe() is not { } claude)
        {
            Console.WriteLine("SKIP Claude interop: no claude.exe found");
            return;
        }
        var (connection, calls) = NewConnection(dispatcher, "C");
        try
        {
            var http = Registration(connection);
            var listed = ClaudeMcpList(claude, "bridge", http);
            Check(listed.Contains(http.RuntimeName + ": " + http.Url, StringComparison.Ordinal) && listed.Contains("Connected", StringComparison.OrdinalIgnoreCase),
                "the real Claude CLI connects to the in-app Bridge endpoint from VibeCode's own --mcp-config JSON (claude mcp list)");
            Check(calls.IsEmpty, "the health check called no Bridge tool");
        }
        finally { connection.Dispose(); }
    }

    private static void VerifyCodexInterop(Dispatcher dispatcher)
    {
        if (CodexExe() is not { } codex)
        {
            Console.WriteLine("SKIP Codex interop: no codex.exe found");
            return;
        }
        var (connection, calls) = NewConnection(dispatcher, "X");
        try
        {
            var http = Registration(connection);
            var text = CodexMcpStatus(codex, http);
            Check(text.Contains(http.RuntimeName, StringComparison.Ordinal) && text.Contains("bridge_list_agents", StringComparison.Ordinal)
                  && text.Contains(BridgeMcpTools.AgentNameToolName, StringComparison.Ordinal),
                "the real Codex MCP client lists every Bridge tool over HTTP from the projected token header");
            Check(calls.IsEmpty, "listing tools called no Bridge tool");
        }
        finally { connection.Dispose(); }
    }
}
