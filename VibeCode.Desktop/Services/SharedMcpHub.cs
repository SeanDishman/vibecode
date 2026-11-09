using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VibeCode.AgentStatus.Mcp.Contracts;
using VibeCode.Protocol;

namespace VibeCode.Services;

/// <summary>
/// One process for each stdio MCP server marked "Share one instance across agents", instead of one per agent: the
/// 2026-10 census found 13 Blender MCP stacks (uvx plus two pythons each) all driving the same add-on socket. A chat
/// leases the server and launches a loopback Streamable HTTP route in place of its own child. Opt-in per server,
/// because sharing is only right for a server that keeps no per-agent state.
/// </summary>
internal sealed class SharedMcpHub
{
    public static SharedMcpHub Instance { get; } = new();

    private readonly object _gate = new();
    private readonly Dictionary<string, SharedMcpServer> _servers = new(StringComparer.Ordinal);
    private readonly List<Lease> _leases = new();

    private sealed class Lease(object owner, SharedMcpServer server, McpLoopbackHost host, string token)
    {
        public object Owner { get; } = owner;
        public SharedMcpServer Server { get; } = server;
        public McpLoopbackHost Host { get; } = host;
        public string Token { get; } = token;
    }

    /// <summary>Replace each shareable stdio server in a chat's launch list with the chat's lease on its one shared
    /// instance, and end the chat's leases on servers it no longer launches. A server that cannot be shared right now
    /// (invalid, missing a variable, or no loopback endpoint) keeps its own stdio launch, as before.</summary>
    public void Project(List<McpServerDefinition> servers, object owner)
    {
        lock (_gate)
        {
            var kept = new HashSet<Lease>();
            for (var index = 0; index < servers.Count; index++)
            {
                if (!Shareable(servers[index], servers) || McpLoopbackHost.Shared is not { } host) continue;
                var lease = LeaseFor(owner, servers[index], host);
                kept.Add(lease);
                servers[index] = host.HttpDefinition(lease.Token, servers[index]);
            }
            foreach (var stale in _leases.Where(lease => lease.Owner == owner && !kept.Contains(lease)).ToArray()) End(stale);
        }
    }

    /// <summary>End every lease <paramref name="owner"/> holds. A server nobody holds any more stops.</summary>
    public void Release(object owner)
    {
        lock (_gate)
            foreach (var lease in _leases.Where(lease => lease.Owner == owner).ToArray()) End(lease);
    }

    /// <summary>The MCP list's sharing state, e.g. "shared · 1 process · 13 agents".</summary>
    public string Summary(string id)
    {
        lock (_gate)
        {
            var servers = _servers.Values.Where(server => string.Equals(server.Definition.Id, id, StringComparison.OrdinalIgnoreCase)).ToArray();
            var processes = servers.Count(server => server.Running);
            var agents = servers.Sum(server => server.Agents);
            var summary = $"shared · {processes} process{(processes == 1 ? "" : "es")} · {agents} agent{(agents == 1 ? "" : "s")}";
            return servers.Any(server => server.AsksClients)
                ? summary + " · not shareable: it sends agents sampling, roots or input requests"
                : summary;
        }
    }

    private static bool Shareable(McpServerDefinition definition, IEnumerable<McpServerDefinition> catalog) =>
        definition is { ShareAcrossAgents: true, IsStdio: true, Enabled: true }
        && !AgentMemoryService.IsMemoryServer(definition)
        && !McpCatalog.Validate(definition, catalog).Any(message => message.Severity == McpValidationSeverity.Error)
        && !definition.Arguments.Prepend(definition.Command).Concat(definition.Environment.Values)
            .SelectMany(McpCatalog.MissingEnvironmentVariables).Any();

    private Lease LeaseFor(object owner, McpServerDefinition definition, McpLoopbackHost host)
    {
        var key = LaunchKey(definition);
        if (_leases.FirstOrDefault(lease => lease.Owner == owner && lease.Server.Key == key && lease.Host == host) is { } existing)
            return existing;
        if (!_servers.TryGetValue(key, out var server)) _servers[key] = server = new SharedMcpServer(key, definition);
        var created = new Lease(owner, server, host, host.Register("/mcp/" + key, server, definition));
        _leases.Add(created);
        server.Agents = _leases.Count(lease => lease.Server == server);
        return created;
    }

    private void End(Lease lease)
    {
        _leases.Remove(lease);
        lease.Host.Unregister(lease.Token);
        lease.Server.Agents = _leases.Count(other => other.Server == lease.Server);
        if (lease.Server.Agents > 0) return;
        _servers.Remove(lease.Server.Key);
        lease.Server.Stop();
    }

    /// <summary>Same server and same expanded launch, same process. An edited server starts afresh for new chats while
    /// chats still holding the old launch keep theirs until they close.</summary>
    private static string LaunchKey(McpServerDefinition definition)
    {
        var launch = new JsonObject
        {
            ["id"] = definition.Id,
            ["command"] = McpCatalog.ExpandEnvironmentTemplates(definition.Command),
            ["args"] = new JsonArray(definition.Arguments.Select(argument => (JsonNode?)McpCatalog.ExpandEnvironmentTemplates(argument)).ToArray()),
            ["env"] = new JsonObject(definition.Environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => KeyValuePair.Create(pair.Key.ToUpperInvariant(), (JsonNode?)McpCatalog.ExpandEnvironmentTemplates(pair.Value)))),
        };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(launch.ToJsonString())))[..16].ToLowerInvariant();
    }
}

/// <summary>
/// One shared stdio MCP server and the agents multiplexed onto it, after rmcp-mux. Each client request gets an id of
/// ours (and a progress token of ours), so two agents' "id 1" never collide; the first initialize is sent once and its
/// result is cached for every later agent; server notifications go to every agent, progress only to the caller; a
/// crashed server restarts with 1-30 s backoff and is initialized again with the same parameters.
/// The server is initialized without client capabilities, so it has no reason to ask an agent for sampling, roots or
/// elicitation: one shared copy cannot know which agent to ask. One that asks anyway gets an error, and Settings
/// marks it as needing a copy per agent.
/// </summary>
internal sealed class SharedMcpServer : IMcpHttpEndpoint
{
    // The newest MCP revision both Claude's TypeScript SDK and Codex's rmcp client speak; a later client asking for a
    // newer one must not lock out an older one sharing the same process.
    private const string NewestSharedProtocol = "2025-06-18";
    private static readonly TimeSpan MinimumBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StableUptime = TimeSpan.FromMinutes(1);

    private readonly object _gate = new();
    private readonly SemaphoreSlim _start = new(1, 1);
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly ConcurrentDictionary<long, Pending> _pending = new();
    private readonly ConcurrentDictionary<string, Pending> _progress = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<McpHttpSession, byte> _sessions = new();
    private readonly StringBuilder _stderr = new();
    private Process? _process;
    private string? _initializeResult;   // JSON text: a parsed node is not safe to read from several requests at once
    private JsonObject? _initializeParams;
    private long _nextId;
    private TimeSpan _backoff = MinimumBackoff;
    private DateTime _notBefore = DateTime.MinValue;
    private DateTime _startedAt;
    private bool _stopped;

    public SharedMcpServer(string key, McpServerDefinition definition)
    {
        Key = key;
        Definition = definition.Clone();
    }

    public string Key { get; }
    public McpServerDefinition Definition { get; }

    /// <summary>Chats holding a lease on this server.</summary>
    public int Agents { get; set; }

    public bool Running
    {
        get { lock (_gate) return _process is { } process && !process.HasExited; }
    }

    /// <summary>The server sent an agent a request (sampling, roots, elicitation) that a shared copy cannot route.</summary>
    public bool AsksClients { get; private set; }

    public string? LastError { get; private set; }

    public int MaxRequestCharacters => 8 * 1024 * 1024;
    public bool StreamsReplies => true;
    public bool OffersEventStream => true;

    private sealed class Pending(McpHttpSession? session, JsonNode? clientId, McpEventStream? events)
    {
        public McpHttpSession? Session { get; } = session;
        public JsonNode? ClientId { get; } = clientId;
        public McpEventStream? Events { get; } = events;
        public JsonNode? ClientProgressToken { get; set; }
        public TaskCompletionSource<JsonObject> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public async Task<JsonObject?> HandleAsync(McpHttpSession session, JsonObject message, McpEventStream? events)
    {
        _sessions.TryAdd(session, 0);
        if (McpJsonRpc.Method(message) is not { } method) return null;   // a response: requests to agents are never forwarded
        if (!McpJsonRpc.IsRequest(message))
        {
            await ForwardNotificationAsync(session, method, message).ConfigureAwait(false);
            return null;
        }
        var id = message["id"];
        if (method == "ping") return McpJsonRpc.Result(id, new JsonObject());
        var ready = await EnsureRunningAsync(method == "initialize" ? message["params"] as JsonObject : null).ConfigureAwait(false);
        if (ready is null)
            return McpJsonRpc.Error(id, -32603, $"The shared MCP server '{Definition.Name}' is not available: {LastError}.");
        if (method == "initialize") return McpJsonRpc.Result(id, JsonNode.Parse(ready)!);
        return await CallAsync(session, message, events).ConfigureAwait(false);
    }

    public void SessionClosed(McpHttpSession session) => _sessions.TryRemove(session, out _);

    /// <summary>Kill the server for good: nobody holds it any more.</summary>
    public void Stop()
    {
        Process? process;
        lock (_gate)
        {
            _stopped = true;
            process = _process;
            _process = null;
            _initializeResult = null;
        }
        FailPending("The shared MCP server '" + Definition.Name + "' was stopped.");
        if (process is null) return;
        try { process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    /// <summary>The cached initialize result of a running, initialized server, starting or restarting it when needed.
    /// Null when it cannot run now (see <see cref="LastError"/>).</summary>
    private async Task<string?> EnsureRunningAsync(JsonObject? clientParams)
    {
        lock (_gate)
            if (_initializeResult is { } ready && _process is { HasExited: false }) return ready;
        await _start.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_initializeResult is { } ready && _process is { HasExited: false }) return ready;
                if (_stopped) return null;
                if (_initializeParams is null && clientParams is not null) _initializeParams = SharedInitializeParams(clientParams);
                if (_initializeParams is null)
                {
                    LastError = "no agent has initialized it yet";
                    return null;
                }
            }
            var wait = _notBefore - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait).ConfigureAwait(false);
            return await StartAsync().ConfigureAwait(false);
        }
        finally { _start.Release(); }
    }

    private static JsonObject SharedInitializeParams(JsonObject clientParams)
    {
        var requested = AgentStatusReport.Text(clientParams["protocolVersion"]) ?? NewestSharedProtocol;
        return new JsonObject
        {
            ["protocolVersion"] = string.CompareOrdinal(requested, NewestSharedProtocol) > 0 ? NewestSharedProtocol : requested,
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "vibecode-shared-mcp", ["title"] = "VibeCode (shared by its agents)", ["version"] = "1.0.0" },
        };
    }

    private async Task<string?> StartAsync()
    {
        Process process;
        try { process = Launch(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            lock (_gate)
            {
                LastError = "it could not start (" + ex.Message + ")";
                _notBefore = DateTime.UtcNow + _backoff;
                _backoff = Next(_backoff);
            }
            CrashLog.Note("shared-mcp", $"{Definition.Name}: {LastError}");
            return null;
        }
        var id = Interlocked.Increment(ref _nextId);
        var pending = _pending[id] = new Pending(null, null, null);
        try
        {
            JsonObject initialize;
            lock (_gate) initialize = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = "initialize", ["params"] = _initializeParams!.DeepClone() };
            JsonObject? reply = null;
            if (await WriteAsync(process, initialize).ConfigureAwait(false))
            {
                try { reply = await pending.Reply.Task.WaitAsync(TimeSpan.FromSeconds(Definition.StartupTimeoutSeconds)).ConfigureAwait(false); }
                catch (TimeoutException) { }
            }
            if (reply?["result"] is not JsonObject result)
            {
                var reason = reply is null
                    ? $"it did not answer initialize within {Definition.StartupTimeoutSeconds} s"
                    : "it refused initialize: " + (AgentStatusReport.Text((reply["error"] as JsonObject)?["message"]) ?? "no reason given");
                lock (_gate) LastError = reason + StderrTail();
                Kill(process);   // its exit schedules the next attempt with backoff
                return null;
            }
            await WriteAsync(process, new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }).ConfigureAwait(false);
            lock (_gate)
            {
                if (_process != process) return null;   // it died meanwhile
                _initializeResult = result.ToJsonString();
                _startedAt = DateTime.UtcNow;
                LastError = null;
                return _initializeResult;
            }
        }
        finally { _pending.TryRemove(id, out _); }
    }

    private Process Launch()
    {
        // No chat's project folder: the one copy serves agents working in many.
        var directory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var command = McpCatalog.ExpandEnvironmentTemplates(Definition.Command);
        var executable = McpCatalog.ResolveExecutable(command, directory) ?? command;
        var arguments = Definition.Arguments.Select(McpCatalog.ExpandEnvironmentTemplates).ToList();
        var psi = new ProcessStartInfo
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        if (Path.GetExtension(executable).ToLowerInvariant() is ".cmd" or ".bat")
        {
            // A batch shim (npx.cmd) needs cmd.exe, which re-parses its line: quote every part, and refuse what quoting
            // cannot protect there (cmd expands %VAR% even inside quotes).
            var parts = arguments.Prepend(executable).ToList();
            if (parts.Any(part => part.IndexOfAny(['%', '"', '\r', '\n', '\0']) >= 0))
                throw new InvalidOperationException("a batch-file command cannot safely pass %, quotes or line breaks; use the program's own executable");
            psi.FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            psi.Arguments = "/d /v:off /s /c \"" + string.Join(" ", parts.Select(part => "\"" + part + "\"")) + "\"";
        }
        else
        {
            psi.FileName = executable;
            foreach (var argument in arguments) psi.ArgumentList.Add(argument);
        }
        // A Claude-launched copy never inherits the account-selection variables VibeCode strips from its CLIs; sharing
        // must not widen what the server sees. Its own environment entries still apply.
        foreach (var name in McpCatalog.ClaudeSuppressedEnvironmentVariables) psi.Environment.Remove(name);
        foreach (var pair in Definition.Environment) psi.Environment[pair.Key] = McpCatalog.ExpandEnvironmentTemplates(pair.Value);
        var process = new Process { StartInfo = psi };
        process.Start();
        ProcessJob.Assign(process);   // dies with VibeCode, like every CLI it starts
        lock (_gate)
        {
            _process = process;
            _stderr.Clear();
        }
        _ = Task.Run(() => ReadAsync(process));
        _ = Task.Run(() => DrainErrorsAsync(process));
        return process;
    }

    private async Task<JsonObject> CallAsync(McpHttpSession session, JsonObject message, McpEventStream? events)
    {
        var clientId = message["id"];
        var id = Interlocked.Increment(ref _nextId);
        var pending = new Pending(session, clientId?.DeepClone(), events);
        var forwarded = (JsonObject)message.DeepClone();
        forwarded["id"] = id;
        string? progressToken = null;
        if (forwarded["params"] is JsonObject parameters && parameters["_meta"] is JsonObject meta && meta["progressToken"] is { } token)
        {
            // Agents choose their own progress tokens too: give each call one of ours and translate back.
            progressToken = "vibecode-" + id;
            pending.ClientProgressToken = token.DeepClone();
            meta["progressToken"] = progressToken;
            _progress[progressToken] = pending;
        }
        _pending[id] = pending;
        try
        {
            Process? process;
            lock (_gate) process = _process;
            if (process is null || !await WriteAsync(process, forwarded).ConfigureAwait(false))
                return McpJsonRpc.Error(clientId, -32603, $"The shared MCP server '{Definition.Name}' is not running; try again.");
            // The CLI enforces its own tool timeout; this only keeps an abandoned call from waiting forever.
            var deadline = Task.Delay(TimeSpan.FromSeconds(Definition.ToolTimeoutSeconds + 30));
            var finished = await Task.WhenAny(pending.Reply.Task, deadline, events?.Closed ?? deadline).ConfigureAwait(false);
            if (finished != pending.Reply.Task)
            {
                // The agent gave up, or the call outlived its tool timeout: let the server stop working on it.
                await WriteAsync(process, new JsonObject
                {
                    ["jsonrpc"] = "2.0", ["method"] = "notifications/cancelled",
                    ["params"] = new JsonObject { ["requestId"] = id, ["reason"] = "The agent stopped waiting for this request." },
                }).ConfigureAwait(false);
                return McpJsonRpc.Error(clientId, -32001, $"The shared MCP server '{Definition.Name}' did not answer in time.");
            }
            var reply = await pending.Reply.Task.ConfigureAwait(false);
            reply["id"] = clientId?.DeepClone();
            return reply;
        }
        finally
        {
            _pending.TryRemove(id, out _);
            if (progressToken is not null) _progress.TryRemove(progressToken, out _);
        }
    }

    private async Task ForwardNotificationAsync(McpHttpSession session, string method, JsonObject message)
    {
        // initialized: VibeCode initialized the server itself. roots/list_changed: it was offered no roots. progress:
        // only for requests to agents, which are never forwarded. That leaves a cancellation, in the server's ids.
        if (method != "notifications/cancelled" || (message["params"] as JsonObject)?["requestId"] is not { } requestId) return;
        foreach (var (id, pending) in _pending)
        {
            if (pending.Session != session || !JsonNode.DeepEquals(pending.ClientId, requestId)) continue;
            var forwarded = (JsonObject)message.DeepClone();
            ((JsonObject)forwarded["params"]!)["requestId"] = id;
            Process? process;
            lock (_gate) process = _process;
            if (process is not null) await WriteAsync(process, forwarded).ConfigureAwait(false);
            return;
        }
    }

    private async Task ReadAsync(Process process)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                JsonObject? message;
                try { message = JsonNode.Parse(line) as JsonObject; }
                catch (JsonException) { continue; }   // a stray log line on stdout
                if (message is not null) await RouteAsync(process, message).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        Exited(process);
    }

    /// <summary>A message from the server: a reply for one waiting caller, its own request, or a notification.</summary>
    private async Task RouteAsync(Process process, JsonObject message)
    {
        if (McpJsonRpc.IsResponse(message))
        {
            if (message["id"] is JsonValue value && value.TryGetValue<long>(out var id) && _pending.TryRemove(id, out var pending))
                pending.Reply.TrySetResult(message);
            return;
        }
        if (McpJsonRpc.Method(message) is not { } method) return;
        if (McpJsonRpc.IsRequest(message))
        {
            if (method != "ping")
            {
                AsksClients = true;
                CrashLog.Note("shared-mcp", $"{Definition.Name} sent a shared instance a {method} request; it needs a copy per agent.");
            }
            await WriteAsync(process, method == "ping"
                ? McpJsonRpc.Result(message["id"], new JsonObject())
                : McpJsonRpc.Error(message["id"], -32601, "VibeCode shares this MCP server across agents, so it cannot route " + method
                    + " to one of them. Turn off sharing for this server to give each agent its own copy.")).ConfigureAwait(false);
            return;
        }
        if (method == "notifications/progress" && AgentStatusReport.Text((message["params"] as JsonObject)?["progressToken"]) is { } token
            && _progress.TryGetValue(token, out var caller))
        {
            var progress = (JsonObject)message.DeepClone();
            ((JsonObject)progress["params"]!)["progressToken"] = caller.ClientProgressToken?.DeepClone();
            // Never awaited: an agent that stops reading must not stall every other agent's replies behind it.
            if (caller.Events is { } events) _ = events.SendAsync(progress);
            else caller.Session?.Notify(progress);
            return;
        }
        if (method is "notifications/progress" or "notifications/cancelled") return;   // for calls that have already ended
        foreach (var session in _sessions.Keys) session.Notify(message);   // list changes, log messages, resource updates
    }

    private void Exited(Process process)
    {
        int? code = null;
        try
        {
            if (!process.WaitForExit(2000)) process.Kill(entireProcessTree: true);
            code = process.ExitCode;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        bool current, restart = false;
        lock (_gate)
        {
            current = _process == process;   // not after Stop(), which already failed every waiting call
            if (current)
            {
                _process = null;
                _initializeResult = null;
                // Backoff doubles from 1 s to 30 s; a server that stayed up for a minute counts as healthy again.
                if (_startedAt != default && DateTime.UtcNow - _startedAt >= StableUptime) _backoff = MinimumBackoff;
                _notBefore = DateTime.UtcNow + _backoff;
                _backoff = Next(_backoff);
                _startedAt = default;
                LastError ??= $"it exited with code {code?.ToString() ?? "unknown"}" + StderrTail();
                restart = !_stopped && Agents > 0;
            }
        }
        process.Dispose();
        if (!current) return;
        CrashLog.Note("shared-mcp", $"{Definition.Name} exited (code {code?.ToString() ?? "unknown"}); restart {(restart ? "scheduled" : "not needed")}.");
        FailPending($"The shared MCP server '{Definition.Name}' exited before answering. VibeCode is restarting it; try again.");
        if (restart) _ = Task.Run(() => EnsureRunningAsync(null));
    }

    private void FailPending(string reason)
    {
        foreach (var id in _pending.Keys)
            if (_pending.TryRemove(id, out var pending)) pending.Reply.TrySetResult(McpJsonRpc.Error(null, -32603, reason));
        _progress.Clear();
    }

    private async Task<bool> WriteAsync(Process process, JsonObject message)
    {
        var line = message.ToJsonString() + "\n";
        await _write.WaitAsync().ConfigureAwait(false);
        try
        {
            await process.StandardInput.WriteAsync(line).ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { return false; }
        finally { _write.Release(); }
    }

    private async Task DrainErrorsAsync(Process process)
    {
        var buffer = new char[1024];
        try
        {
            int read;
            while ((read = await process.StandardError.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                lock (_gate)
                {
                    if (_process != process) continue;
                    _stderr.Append(buffer, 0, read);
                    if (_stderr.Length > 2048) _stderr.Remove(0, _stderr.Length - 2048);
                }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    private string StderrTail()
    {
        var tail = _stderr.ToString().Trim();
        if (tail.Length > 300) tail = "…" + tail[^300..];
        return tail.Length == 0 ? "" : "; stderr: " + tail.Replace('\r', ' ').Replace('\n', ' ');
    }

    private static void Kill(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    private static TimeSpan Next(TimeSpan backoff) => TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaximumBackoff.Ticks));
}
