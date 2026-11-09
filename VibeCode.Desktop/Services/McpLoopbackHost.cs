using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VibeCode.AgentStatus.Mcp.Contracts;

namespace VibeCode.Services;

/// <summary>A JSON-RPC service behind loopback routes: one chat's Bridge tools, or one shared stdio MCP server.</summary>
internal interface IMcpHttpEndpoint
{
    /// <summary>Largest JSON-RPC body accepted from a client, in characters.</summary>
    int MaxRequestCharacters { get; }

    /// <summary>Answer each request on a text/event-stream response. A long call can then report progress, and the
    /// stream's keep-alives stop an HTTP client's header timeout (five minutes in Node's fetch) from cutting it off.</summary>
    bool StreamsReplies { get; }

    /// <summary>Accept a client's GET event stream for server-initiated notifications.</summary>
    bool OffersEventStream { get; }

    /// <summary>One message from <paramref name="session"/>: the reply to a request, or null for a notification or a
    /// response. A streaming endpoint may first send messages related to the request through <paramref name="events"/>,
    /// whose <see cref="McpEventStream.Closed"/> also tells it when the client has gone.</summary>
    Task<JsonObject?> HandleAsync(McpHttpSession session, JsonObject message, McpEventStream? events);

    void SessionClosed(McpHttpSession session);
}

/// <summary>One registered caller: the only URL path its token opens, the service behind it, and the stdio launch a
/// client that cannot speak Streamable HTTP receives instead.</summary>
internal sealed record McpLoopbackRoute(string Path, IMcpHttpEndpoint Endpoint, McpServerDefinition StdioFallback);

/// <summary>An MCP session (Mcp-Session-Id), opened by one initialize under one bearer token.</summary>
internal sealed class McpHttpSession
{
    private static long _sequence;
    private McpEventStream? _events;

    internal McpHttpSession(string token, McpLoopbackRoute route)
    {
        Token = token;
        Route = route;
    }

    public string Id { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    public string Token { get; }
    public McpLoopbackRoute Route { get; }
    internal long Sequence { get; } = Interlocked.Increment(ref _sequence);

    /// <summary>The endpoint's own per-session state.</summary>
    public object? State { get; set; }

    /// <summary>Send a server-initiated message on the client's GET stream. False when the client has none open.</summary>
    public bool Notify(JsonObject message)
    {
        if (Volatile.Read(ref _events) is not { } events) return false;
        _ = events.SendAsync(message);
        return true;
    }

    internal McpEventStream? Attach(McpEventStream events) => Interlocked.Exchange(ref _events, events);
    internal void Detach(McpEventStream events) => Interlocked.CompareExchange(ref _events, null, events);
    internal void Close() => Interlocked.Exchange(ref _events, null)?.Close();
}

/// <summary>A chunked text/event-stream response body. Replies, notifications and keep-alives share it, one write at a time.</summary>
internal sealed class McpEventStream
{
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(30);
    private readonly Stream _stream;
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _finished;   // the closing chunk is written, or the connection failed

    internal McpEventStream(Stream stream) => _stream = stream;

    /// <summary>Completes when the client has gone or the stream is closed or ended.</summary>
    public Task Closed => _closed.Task;

    public Task<bool> SendAsync(JsonNode message) => WriteAsync("event: message\ndata: " + message.ToJsonString() + "\n\n");

    internal Task<bool> KeepAliveAsync() => WriteAsync(": keep-alive\n\n");

    /// <summary>Write the closing chunk; nothing is written after it.</summary>
    internal Task CompleteAsync() => WriteAsync(null);

    /// <summary>No further events; the stream's handler stops waiting and ends the response.</summary>
    internal void Close() => _closed.TrySetResult();

    private async Task<bool> WriteAsync(string? text)
    {
        byte[] chunk;
        if (text is null) chunk = "0\r\n\r\n"u8.ToArray();
        else
        {
            var payload = Encoding.UTF8.GetBytes(text);
            chunk = [.. Encoding.ASCII.GetBytes(payload.Length.ToString("X", CultureInfo.InvariantCulture) + "\r\n"), .. payload, (byte)'\r', (byte)'\n'];
        }
        await _write.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_finished || text is not null && _closed.Task.IsCompleted) return false;
            // A client that stopped reading must not hold every later writer forever.
            using var timeout = new CancellationTokenSource(WriteTimeout);
            await _stream.WriteAsync(chunk, timeout.Token).ConfigureAwait(false);
            await _stream.FlushAsync(timeout.Token).ConfigureAwait(false);
            _finished = text is null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or SocketException)
        {
            _finished = true;
            return false;
        }
        finally
        {
            if (_finished) _closed.TrySetResult();
            _write.Release();
        }
    }
}

internal static class McpJsonRpc
{
    public static string? Method(JsonObject message) => AgentStatusReport.Text(message["method"]);
    public static bool IsRequest(JsonObject message) => Method(message) is not null && message["id"] is not null;
    public static bool IsResponse(JsonObject message) => !message.ContainsKey("method") && message["id"] is not null
        && (message.ContainsKey("result") || message.ContainsKey("error"));

    public static JsonObject Result(JsonNode? id, JsonNode result) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result };

    public static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };
}

/// <summary>
/// VibeCode's own MCP Streamable HTTP endpoint (spec 2025-03-26/2025-06-18). It serves the in-app Bridge tools and the
/// shared stdio servers, so an agent no longer needs a helper process for either: the 2026-10 census counted 69
/// `--bridge-mcp` relays at about 30 MB and 1.6 s startup each. A route's bearer token is its caller's identity, as the
/// per-session pipe name is for the stdio relay; models never choose it.
/// Bound to 127.0.0.1 only, on a random free port. A web page can point a DNS name of its own at 127.0.0.1, but it
/// cannot make the Host header, or an Origin it sends, name this listener, so both are checked on every request.
/// Every response closes its connection: on loopback a fresh connection costs nothing, and no keep-alive framing
/// can go wrong.
/// </summary>
internal sealed class McpLoopbackHost
{
    public const string SessionHeader = "Mcp-Session-Id";
    private const int MaxHeaderBytes = 16 * 1024;
    private const int MaxSessionsPerToken = 32;   // a CLI may hold several at once (Codex: discovery plus each thread)
    private static readonly TimeSpan RequestReadTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(1);
    private static readonly object Gate = new();
    private static McpLoopbackHost? _shared;
    private static DateTime _failedAt = DateTime.MinValue;

    private readonly TcpListener _listener;
    private readonly ConcurrentDictionary<string, McpLoopbackRoute> _routes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, McpHttpSession> _sessions = new(StringComparer.Ordinal);

    private McpLoopbackHost(int port)
    {
        // Exclusive use: no other process can bind this port with SO_REUSEADDR and take requests meant for us.
        _listener = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
        try { _listener.Start(); }
        catch
        {
            _listener.Stop();
            throw;
        }
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        BaseUrl = "http://127.0.0.1:" + Port.ToString(CultureInfo.InvariantCulture);
        _ = Task.Run(AcceptAsync);
    }

    public int Port { get; }
    public string BaseUrl { get; }

    /// <summary>The app's endpoint, started on first use. Null while it cannot listen (retried after a minute): callers
    /// then launch their stdio form, exactly as before this endpoint existed.</summary>
    public static McpLoopbackHost? Shared
    {
        get
        {
            lock (Gate)
            {
                if (_shared is null && DateTime.UtcNow - _failedAt >= RetryAfterFailure)
                {
                    _shared = TryStart(0);
                    if (_shared is null) _failedAt = DateTime.UtcNow;
                }
                return _shared;
            }
        }
    }

    internal static McpLoopbackHost? TryStart(int port)
    {
        try { return new McpLoopbackHost(port); }
        catch (SocketException ex)
        {
            CrashLog.Note("mcp-loopback", $"Cannot listen on 127.0.0.1:{port} ({ex.SocketErrorCode}); MCP servers keep their stdio launch.");
            return null;
        }
    }

    /// <summary>Register a caller. The returned token is its identity; <see cref="HttpDefinition"/> hands it to the CLI.</summary>
    public string Register(string path, IMcpHttpEndpoint endpoint, McpServerDefinition stdioFallback)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        _routes[token] = new McpLoopbackRoute(path, endpoint, stdioFallback.Clone());
        return token;
    }

    /// <summary>What a CLI launches for <paramref name="token"/>: <paramref name="stdio"/>'s Id and name, so its tool
    /// names stay the same, served at this endpoint. <paramref name="stdio"/> also becomes the route's fallback.</summary>
    public McpServerDefinition HttpDefinition(string token, McpServerDefinition stdio)
    {
        var route = _routes[token] = _routes[token] with { StdioFallback = stdio.Clone() };
        var definition = stdio.Clone();
        definition.Transport = McpCatalog.HttpTransport;
        definition.Url = BaseUrl + route.Path;
        definition.Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Authorization"] = "Bearer " + token };
        definition.BearerTokenEnvironmentVariable = null;
        definition.Command = "";
        definition.Arguments = new List<string>();
        definition.Environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        definition.ShareAcrossAgents = false;
        return definition;
    }

    /// <summary>Retire a caller: its token stops working and its sessions end.</summary>
    public void Unregister(string token)
    {
        if (!_routes.TryRemove(token, out _)) return;
        foreach (var session in _sessions.Values)
            if (session.Token == token) EndSession(session);
    }

    /// <summary>The launch list for an ACP agent. ACP lets a client send HTTP MCP servers only to an agent that declares
    /// mcpCapabilities.http, so for any other agent each route of this endpoint goes back to its stdio launch.</summary>
    public static IEnumerable<McpServerDefinition>? ForAcpAgent(IEnumerable<McpServerDefinition>? servers, JsonNode? initializeResult) =>
        servers is null || (initializeResult as JsonObject)?["agentCapabilities"] is JsonObject capabilities
            && capabilities["mcpCapabilities"] is JsonObject mcp && mcp["http"] is JsonValue http
            && http.TryGetValue<bool>(out var supported) && supported
            ? servers
            : WithStdioFallbacks(servers);

    /// <summary><paramref name="servers"/> with every route of this endpoint replaced by its stdio launch.</summary>
    public static List<McpServerDefinition> WithStdioFallbacks(IEnumerable<McpServerDefinition> servers)
    {
        McpLoopbackHost? host;
        lock (Gate) host = _shared;
        return servers.Select(server => host?.FallbackFor(server) ?? server).ToList();
    }

    private McpServerDefinition? FallbackFor(McpServerDefinition server) =>
        server.Transport == McpCatalog.HttpTransport && server.Url.StartsWith(BaseUrl + "/", StringComparison.Ordinal)
        && server.Headers.TryGetValue("Authorization", out var authorization)
        && BearerToken(authorization) is { } token && _routes.TryGetValue(token, out var route)
            ? route.StdioFallback.Clone()
            : null;

    private async Task AcceptAsync()
    {
        while (true)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { return; }
            catch (SocketException)
            {
                // A connection reset before it was accepted; never spin if the socket itself has failed.
                await Task.Delay(50).ConfigureAwait(false);
                continue;
            }
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var connection = client;
        try
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            using var reading = new CancellationTokenSource(RequestReadTimeout);
            if (await ReadHeadAsync(stream, reading.Token).ConfigureAwait(false) is not { } head)
            {
                await RespondAsync(stream, 400, "Malformed HTTP request.").ConfigureAwait(false);
                return;
            }
            if (!head.Headers.TryGetValue("Host", out var authority) || !IsOwn(authority, ""))
            {
                await RespondAsync(stream, 403, "This endpoint only answers requests addressed to 127.0.0.1:" + Port + ".").ConfigureAwait(false);
                return;
            }
            if (head.Headers.TryGetValue("Origin", out var origin) && !IsOwn(origin, "http://"))
            {
                await RespondAsync(stream, 403, "Cross-origin requests are not allowed.").ConfigureAwait(false);
                return;
            }
            if (!head.Headers.TryGetValue("Authorization", out var authorization) || BearerToken(authorization) is not { } token
                || !_routes.TryGetValue(token, out var route))
            {
                await RespondAsync(stream, 401, "A valid bearer token is required.", "WWW-Authenticate: Bearer").ConfigureAwait(false);
                return;
            }
            if (head.Path != route.Path)
            {
                await RespondAsync(stream, 404, "No MCP endpoint at this path for this token.").ConfigureAwait(false);
                return;
            }
            switch (head.Method)
            {
                case "POST":
                    await PostAsync(stream, head, token, route, reading.Token).ConfigureAwait(false);
                    break;
                case "GET":
                    await GetAsync(stream, head, token, route).ConfigureAwait(false);
                    break;
                case "DELETE":
                    var ended = FindSession(head, token);
                    if (ended is not null) EndSession(ended);
                    await RespondAsync(stream, ended is null ? 404 : 200, ended is null ? "Unknown MCP session." : null).ConfigureAwait(false);
                    break;
                default:
                    await RespondAsync(stream, 405, "Use POST, GET or DELETE.", "Allow: POST, GET, DELETE").ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException) { }
        catch (Exception ex)
        {
            // One malformed exchange must never reach the app's unhandled-exception path.
            CrashLog.Note("mcp-loopback", "Request failed: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private async Task PostAsync(Stream stream, HttpHead head, string token, McpLoopbackRoute route, CancellationToken reading)
    {
        var endpoint = route.Endpoint;
        string? text;
        try { text = await ReadBodyAsync(stream, head, endpoint.MaxRequestCharacters, reading).ConfigureAwait(false); }
        catch (InvalidDataException)
        {
            await RespondAsync(stream, 413, $"A request may be at most {endpoint.MaxRequestCharacters:N0} characters.").ConfigureAwait(false);
            return;
        }
        if (text is null)
        {
            await RespondAsync(stream, 400, "Malformed request body.").ConfigureAwait(false);
            return;
        }
        JsonNode? body;
        try { body = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { MaxDepth = 64 }); }
        catch (JsonException)
        {
            await RespondJsonAsync(stream, 400, McpJsonRpc.Error(null, -32700, "Parse error: send one JSON-RPC message.")).ConfigureAwait(false);
            return;
        }
        var messages = body is JsonArray batch ? batch.ToArray() : [body];
        if (messages.Length == 0 || messages.Any(message => message is not JsonObject))
        {
            await RespondJsonAsync(stream, 400, McpJsonRpc.Error(null, -32600, "Invalid JSON-RPC message.")).ConfigureAwait(false);
            return;
        }
        var objects = messages.Cast<JsonObject>().ToArray();
        // initialize always opens a new session; every other message names the one it belongs to.
        var initialize = objects.Length == 1 && McpJsonRpc.IsRequest(objects[0]) && McpJsonRpc.Method(objects[0]) == "initialize";
        McpHttpSession session;
        if (initialize) session = new McpHttpSession(token, route);
        else if (FindSession(head, token) is { } existing) session = existing;
        else
        {
            var named = head.Headers.ContainsKey(SessionHeader);
            await RespondAsync(stream, named ? 404 : 400, named ? "Unknown or expired MCP session; initialize again." : SessionHeader + " is required.").ConfigureAwait(false);
            return;
        }

        if (!objects.Any(McpJsonRpc.IsRequest))
        {
            foreach (var message in objects) await endpoint.HandleAsync(session, message, null).ConfigureAwait(false);
            await RespondAsync(stream, 202, null).ConfigureAwait(false);
            return;
        }
        if (endpoint.StreamsReplies && objects.Length == 1 && !initialize)
        {
            await StreamReplyAsync(stream, session, objects[0]).ConfigureAwait(false);
            return;
        }
        var replies = new List<JsonObject>();
        foreach (var message in objects)
            if (await endpoint.HandleAsync(session, message, null).ConfigureAwait(false) is { } reply)
                replies.Add(reply);
        var headers = new List<string>();
        if (initialize)
        {
            if (replies.Count == 1 && replies[0]["result"] is not null)
            {
                AddSession(session);
                headers.Add(SessionHeader + ": " + session.Id);
            }
            else endpoint.SessionClosed(session);
        }
        JsonNode payload = body is JsonArray ? new JsonArray(replies.Select(reply => (JsonNode?)reply).ToArray()) : replies[0];
        await RespondJsonAsync(stream, 200, payload, headers.ToArray()).ConfigureAwait(false);
    }

    private static async Task StreamReplyAsync(Stream stream, McpHttpSession session, JsonObject request)
    {
        await WriteHeadAsync(stream, 200, "text/event-stream", null, chunked: true).ConfigureAwait(false);
        var events = new McpEventStream(stream);
        using var done = new CancellationTokenSource();
        var keepAlive = KeepAliveAsync(events, done.Token);
        // The body was read in full, so the connection only becomes readable again when the client gives up.
        _ = ClientClosedAsync(stream, done.Token).ContinueWith(_ => events.Close(), TaskScheduler.Default);
        try
        {
            if (await session.Route.Endpoint.HandleAsync(session, request, events).ConfigureAwait(false) is { } reply)
                await events.SendAsync(reply).ConfigureAwait(false);
        }
        finally
        {
            done.Cancel();
            await keepAlive.ConfigureAwait(false);
            await events.CompleteAsync().ConfigureAwait(false);
        }
    }

    private async Task GetAsync(Stream stream, HttpHead head, string token, McpLoopbackRoute route)
    {
        if (!route.Endpoint.OffersEventStream)
        {
            await RespondAsync(stream, 405, "This MCP endpoint sends no server-initiated messages.", "Allow: POST, DELETE").ConfigureAwait(false);
            return;
        }
        if (FindSession(head, token) is not { } session)
        {
            var named = head.Headers.ContainsKey(SessionHeader);
            await RespondAsync(stream, named ? 404 : 400, named ? "Unknown or expired MCP session." : SessionHeader + " is required.").ConfigureAwait(false);
            return;
        }
        await WriteHeadAsync(stream, 200, "text/event-stream", null, chunked: true).ConfigureAwait(false);
        var events = new McpEventStream(stream);
        session.Attach(events)?.Close();   // a reconnecting client replaces its earlier stream
        using var done = new CancellationTokenSource();
        var keepAlive = KeepAliveAsync(events, done.Token);
        try { await Task.WhenAny(events.Closed, ClientClosedAsync(stream, done.Token)).ConfigureAwait(false); }
        finally
        {
            done.Cancel();
            await keepAlive.ConfigureAwait(false);
            session.Detach(events);
            await events.CompleteAsync().ConfigureAwait(false);
        }
    }

    private static async Task KeepAliveAsync(McpEventStream events, CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.WhenAny(events.Closed, Task.Delay(KeepAliveInterval, cancellationToken)).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested || events.Closed.IsCompleted || !await events.KeepAliveAsync().ConfigureAwait(false))
                return;
        }
    }

    /// <summary>Completes when the client closes its end of a connection whose request was read in full.</summary>
    private static async Task ClientClosedAsync(Stream stream, CancellationToken cancellationToken)
    {
        try { await stream.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException) { }
    }

    private McpHttpSession? FindSession(HttpHead head, string token) =>
        head.Headers.TryGetValue(SessionHeader, out var id) && _sessions.TryGetValue(id, out var session) && session.Token == token
            ? session
            : null;

    private void AddSession(McpHttpSession session)
    {
        _sessions[session.Id] = session;
        // A CLI that keeps reconnecting must not grow the table without bound: keep its newest sessions only.
        foreach (var stale in _sessions.Values.Where(other => other.Token == session.Token)
                     .OrderByDescending(other => other.Sequence).Skip(MaxSessionsPerToken).ToArray())
            EndSession(stale);
    }

    private void EndSession(McpHttpSession session)
    {
        if (!_sessions.TryRemove(session.Id, out _)) return;
        session.Close();
        session.Route.Endpoint.SessionClosed(session);
    }

    private bool IsOwn(string value, string scheme)
    {
        var port = ":" + Port.ToString(CultureInfo.InvariantCulture);
        return value.Equals(scheme + "127.0.0.1" + port, StringComparison.OrdinalIgnoreCase)
               || value.Equals(scheme + "localhost" + port, StringComparison.OrdinalIgnoreCase);
    }

    private static string? BearerToken(string authorization) =>
        authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && authorization.Length > 7
            ? authorization[7..].Trim()
            : null;

    // ---------------- HTTP/1.1 ----------------

    private sealed record HttpHead(string Method, string Path, Dictionary<string, string> Headers, byte[] Leftover);

    private static async Task<HttpHead?> ReadHeadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxHeaderBytes];
        var length = 0;
        var end = -1;
        while (end < 0)
        {
            if (length == buffer.Length) return null;
            var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (read == 0) return null;
            var scanFrom = Math.Max(0, length - 3);
            length += read;
            for (var i = scanFrom; i + 3 < length && end < 0; i++)
                if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n') end = i;
        }
        var lines = Encoding.ASCII.GetString(buffer, 0, end).Split("\r\n");
        var parts = lines[0].Split(' ');
        if (parts.Length != 3 || parts[2] is not ("HTTP/1.1" or "HTTP/1.0") || !parts[1].StartsWith('/')) return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) return null;
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (headers.TryAdd(name, value)) continue;
            // A repeated header that decides routing, identity or framing is ambiguous; any other is a list.
            if (name.Equals("Host", StringComparison.OrdinalIgnoreCase) || name.Equals("Origin", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) || name.Equals(SessionHeader, StringComparison.OrdinalIgnoreCase)
                || name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) || name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                return null;
            headers[name] += ", " + value;
        }
        var target = parts[1];
        var query = target.IndexOf('?');
        return new HttpHead(parts[0], query < 0 ? target : target[..query], headers, buffer[(end + 4)..length]);
    }

    /// <summary>The body as text; null when it is malformed. Throws <see cref="InvalidDataException"/> when it is too large.</summary>
    private static async Task<string?> ReadBodyAsync(Stream stream, HttpHead head, int maxCharacters, CancellationToken cancellationToken)
    {
        if (head.Headers.TryGetValue("Expect", out var expect) && expect.Equals("100-continue", StringComparison.OrdinalIgnoreCase))
            await stream.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        var maxBytes = (long)maxCharacters * 3;   // UTF-8 spends at most three bytes per UTF-16 character
        var reader = new BodyReader(stream, head.Leftover, cancellationToken);
        var body = new MemoryStream();
        if (head.Headers.TryGetValue("Transfer-Encoding", out var encoding))
        {
            // Chunked is not optional: a client that serialises JSON straight to the socket cannot know its length.
            if (!encoding.Equals("chunked", StringComparison.OrdinalIgnoreCase) || head.Headers.ContainsKey("Content-Length")) return null;
            while (true)
            {
                var line = await reader.ReadLineAsync(1024).ConfigureAwait(false);
                if (line is null) return null;
                var semicolon = line.IndexOf(';');
                if (!int.TryParse((semicolon < 0 ? line : line[..semicolon]).Trim(), NumberStyles.AllowHexSpecifier,
                        CultureInfo.InvariantCulture, out var size) || size < 0) return null;
                if (size == 0)
                {
                    string? trailer;
                    do
                    {
                        trailer = await reader.ReadLineAsync(MaxHeaderBytes).ConfigureAwait(false);
                        if (trailer is null) return null;
                    } while (trailer.Length > 0);
                    break;
                }
                if (body.Length + size > maxBytes) throw new InvalidDataException();
                if (!await reader.CopyAsync(size, body).ConfigureAwait(false)
                    || await reader.ReadLineAsync(2).ConfigureAwait(false) is not "") return null;
            }
        }
        else
        {
            if (!head.Headers.TryGetValue("Content-Length", out var declared)
                || !long.TryParse(declared, NumberStyles.None, CultureInfo.InvariantCulture, out var length)) return null;
            if (length > maxBytes) throw new InvalidDataException();
            if (!await reader.CopyAsync((int)length, body).ConfigureAwait(false)) return null;
        }
        string text;
        try { text = new UTF8Encoding(false, true).GetString(body.GetBuffer(), 0, (int)body.Length); }
        catch (DecoderFallbackException) { return null; }
        if (text.Length > maxCharacters) throw new InvalidDataException();
        return text;
    }

    /// <summary>Reads lines and exact byte counts, starting with whatever arrived together with the header.</summary>
    private sealed class BodyReader(Stream stream, byte[] leftover, CancellationToken cancellationToken)
    {
        private byte[] _buffer = leftover.Length > 0 ? leftover : new byte[8192];
        private int _start;
        private int _end = leftover.Length;

        public async Task<string?> ReadLineAsync(int maximum)
        {
            while (true)
            {
                var newline = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
                if (newline >= 0)
                {
                    var line = Encoding.ASCII.GetString(_buffer, _start, newline - _start).TrimEnd('\r');
                    _start = newline + 1;
                    return line;
                }
                if (_end - _start > maximum || !await FillAsync().ConfigureAwait(false)) return null;
            }
        }

        public async Task<bool> CopyAsync(int count, MemoryStream output)
        {
            while (count > 0)
            {
                if (_start == _end && !await FillAsync().ConfigureAwait(false)) return false;
                var take = Math.Min(count, _end - _start);
                output.Write(_buffer, _start, take);
                _start += take;
                count -= take;
            }
            return true;
        }

        private async Task<bool> FillAsync()
        {
            if (_start > 0)
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }
            if (_end == _buffer.Length) Array.Resize(ref _buffer, Math.Max(8192, _buffer.Length * 2));
            var read = await stream.ReadAsync(_buffer.AsMemory(_end), cancellationToken).ConfigureAwait(false);
            _end += read;
            return read > 0;
        }
    }

    private static Task RespondAsync(Stream stream, int status, string? error, params string[] headers) =>
        error is null
            ? WriteAsync(stream, status, null, [], headers)
            : RespondJsonAsync(stream, status, McpJsonRpc.Error(null, -32000, error), headers);

    private static Task RespondJsonAsync(Stream stream, int status, JsonNode payload, params string[] headers) =>
        WriteAsync(stream, status, "application/json", Encoding.UTF8.GetBytes(payload.ToJsonString()), headers);

    private static async Task WriteAsync(Stream stream, int status, string? contentType, byte[] body, string[] headers)
    {
        await WriteHeadAsync(stream, status, contentType, headers, chunked: false, length: body.Length).ConfigureAwait(false);
        await stream.WriteAsync(body).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    private static async Task WriteHeadAsync(Stream stream, int status, string? contentType, string[]? headers, bool chunked, int length = 0)
    {
        var reason = status switch
        {
            200 => "OK", 202 => "Accepted", 400 => "Bad Request", 401 => "Unauthorized", 403 => "Forbidden",
            404 => "Not Found", 405 => "Method Not Allowed", 413 => "Content Too Large", _ => "Error",
        };
        var head = new StringBuilder("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n");
        if (contentType is not null) head.Append("Content-Type: ").Append(contentType).Append("\r\n");
        head.Append(chunked ? "Transfer-Encoding: chunked\r\n" : "Content-Length: " + length + "\r\n")
            // Nothing here is meant for a browser, and none of it should ever sit in a cache.
            .Append("Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\n");
        foreach (var header in headers ?? []) head.Append(header).Append("\r\n");
        head.Append("Connection: close\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString())).ConfigureAwait(false);
        if (chunked) await stream.FlushAsync().ConfigureAwait(false);
    }
}
