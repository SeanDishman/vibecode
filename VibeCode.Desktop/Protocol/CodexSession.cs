using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VibeCode.Services;

namespace VibeCode.Protocol;

public sealed class CodexSessionOptions
{
    public required string Cwd { get; init; }
    /// <summary>The saved OpenAI account's isolated CODEX_HOME. Null keeps the original VibeCode home for backwards compatibility.</summary>
    public string? HomeDirectory { get; init; }
    public string? Resume { get; init; }
    public bool ForkSession { get; init; }
    public string? Model { get; init; }
    public string? Effort { get; init; }
    public string? Title { get; init; }
    public string PermissionMode { get; init; } = "default";
    public string? AppendSystemPrompt { get; init; }
    /// <summary>Whether the stable Codex collaboration tool family is available to this root session.</summary>
    public bool SwarmsEnabled { get; init; }
    /// <summary>Provider-enforced ceiling for open child threads. The launch boundary clamps this again.</summary>
    public int SwarmMaxWorkers { get; init; } = 6;
    /// <summary>Immutable VibeCode MCP snapshot for this launch; VibeCode never edits Codex's native config.</summary>
    public IReadOnlyList<McpServerDefinition>? McpServers { get; init; }
}

/// <summary>
/// A Codex conversation hosted by <c>codex app-server</c>. App-server is the supported
/// integration surface for rich clients: it keeps one resumable thread alive, streams
/// tool/file events, and sends approval requests over JSON-RPC on stdin/stdout.
///
/// This class translates those events to the small Claude-shaped stream already consumed
/// by ChatViewModel. Keeping the translation at the protocol edge lets every existing
/// message, tool, diff, permission, queue, interrupt, and artifact view work for both CLIs.
/// </summary>
public sealed class CodexSession : ICodingSession
{
    private static readonly HashSet<string> IgnoredArtifactDirectories = new(StringComparer.OrdinalIgnoreCase)
        { ".git", ".idea", ".vs", ".agents", ".codex", "bin", "obj", "node_modules" };

    public event Action<JsonNode>? MessageReceived;
    public event Action<PermissionRequest>? PermissionRequested;
    public event Action<string>? PermissionCancelled;
    public event Action<int, string>? Exited;
    public event Action? Initialized;
    /// <summary>Raw app-server notifications for protocol smoke tests and diagnostics.</summary>
    internal event Action<string, JsonObject?>? NotificationReceived;

    public JsonArray Commands { get; private set; } = new();
    public JsonArray Models { get; private set; } = new();
    public string? SessionId { get; private set; }
    public bool HasExited => _disposed || _proc is null || _proc.HasExited;

    private readonly CodexSessionOptions _options;
    private Process? _proc;
    private StreamWriter? _stdin;
    // Stdin writes go through a single-consumer queue drained by a background task. Writing the pipe directly on
    // the caller's thread froze the whole app ("Not Responding", 0% CPU): the send button, stop button, and
    // approval clicks all wrote on the UI thread, and when the app-server stalled mid-turn the tiny pipe buffer
    // filled and Write blocked inside the lock — with Codex sometimes waiting on the very approval reply that was
    // stuck behind it. The queue keeps caller threads free and preserves write order.
    private readonly System.Threading.Channels.Channel<string> _writeQueue =
        System.Threading.Channels.Channel.CreateUnbounded<string>(
            new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true });
    private readonly StringBuilder _stderr = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly Dictionary<string, JsonNode> _approvalRpcIds = new();
    private readonly Dictionary<string, string> _approvalMethods = new();
    private readonly Dictionary<string, JsonObject> _approvalParams = new();
    private readonly Dictionary<string, JsonObject> _startedItems = new();
    private readonly HashSet<string> _announcedTools = new();
    private readonly Dictionary<string, int> _streamIndexByItem = new();
    private readonly Queue<JsonNode> _earlyMessages = new();
    private readonly List<string> _tempAttachments = new();
    private int _requestSeq;
    private string? _turnId;
    private bool _rootThreadReportedActive;
    private bool _interruptRequested;
    // app-server multiplexes the parent thread and every spawned subagent over this one connection. Keep their turn
    // identities separate: treating a child's turn/completed as the parent's result is what made the composer morph
    // back to Send while the parent (or a sibling) was still spending tokens, and also made Stop lose its turn id.
    private readonly ConcurrentDictionary<string, string> _activeSubagentTurns = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SubagentInfo> _subagents = new(StringComparer.Ordinal);
    // Stop can arrive after spawnAgent reports a child thread but before that thread has a turn id. There is no
    // turn/interrupt target yet, so latch the intent by thread id. If app-server announces that turn later we cancel
    // it immediately without resurrecting the already-stopped parent UI.
    private readonly ConcurrentDictionary<string, byte> _subagentsCancelledBeforeStart = new(StringComparer.Ordinal);
    private int _nextSubagentOrder;
    private JsonObject? _pendingRootCompletion;
    private string? _model;
    private string? _effort;
    private string _permissionMode;
    private string? _lastError;
    private JsonObject? _lastUsage;
    /// <summary>Bridge append text injected once for a fresh thread (developer item or first-turn prepend).</summary>
    private readonly string? _effectiveAppendPrompt;
    // turn/diff/updated is a latest aggregated snapshot, not a delta. Keep one snapshot per multiplexed thread so
    // shell-driven edits can become real CodexEdit transcript cards without a child turn overwriting the parent.
    private readonly Dictionary<string, string> _turnDiffByThread = new(StringComparer.Ordinal);
    private readonly HashSet<string> _turnFileChangePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _turnCommandFilePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _activeCommandItems = new(StringComparer.Ordinal);
    private readonly object _artifactWatchGate = new();
    private FileSystemWatcher? _artifactWatcher;
    private DateTime _commandCaptureUntilUtc;
    private TokenUsage _turnUsage;
    // Token totals are cumulative PER THREAD. A single baseline shared by parent and children makes each switch look
    // like a counter rollback and double-counts the latest request, so retain one baseline per app-server thread.
    private readonly Dictionary<string, TokenUsage> _previousTotalUsageByThread = new(StringComparer.Ordinal);
    private bool _appendPromptPending;
    private int _nextStreamIndex;
    private bool _streamStarted;
    private bool _disposed;
    private bool _hydratingHistory;
    private bool _runtimeIntegrityVerified;
    private string? _runtimeIntegrityReason;

    private sealed class SubagentInfo
    {
        public required string ThreadId { get; init; }
        public required int Order { get; init; }
        public string Label { get; set; } = "";
        public string Task { get; set; } = "";
        public string Activity { get; set; } = "Starting";
        public string Status { get; set; } = "pendingInit";
        public bool SawTurn { get; set; }
        public bool IsActive => Status is "pendingInit" or "running";
    }

    /// <summary>Codex reports cached input as a subset of input, unlike the disjoint Claude usage buckets.</summary>
    private readonly record struct TokenUsage(double Input, double CachedInput, double Output, double ReasoningOutput)
    {
        public static TokenUsage From(JsonNode? node) => new(
            Number(node, "inputTokens", "input_tokens"),
            Number(node, "cachedInputTokens", "cached_input_tokens"),
            Number(node, "outputTokens", "output_tokens"),
            Number(node, "reasoningOutputTokens", "reasoning_output_tokens"));

        public static TokenUsage operator +(TokenUsage left, TokenUsage right) => new(
            left.Input + right.Input,
            left.CachedInput + right.CachedInput,
            left.Output + right.Output,
            left.ReasoningOutput + right.ReasoningOutput);

        public static TokenUsage operator -(TokenUsage left, TokenUsage right) => new(
            left.Input - right.Input,
            left.CachedInput - right.CachedInput,
            left.Output - right.Output,
            left.ReasoningOutput - right.ReasoningOutput);

        public bool HasNegativeValue => Input < 0 || CachedInput < 0 || Output < 0 || ReasoningOutput < 0;
        public bool HasTokens => Input > 0 || CachedInput > 0 || Output > 0 || ReasoningOutput > 0;
    }

    public CodexSession(CodexSessionOptions options)
    {
        _options = options;
        _model = options.Model;
        _effort = options.Effort;
        _permissionMode = options.PermissionMode;
        _effectiveAppendPrompt = JoinPrompts(options.AppendSystemPrompt);
        _appendPromptPending = !string.IsNullOrWhiteSpace(_effectiveAppendPrompt);
    }

    private static string? JoinPrompts(params string?[] values)
    {
        var parts = values.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!.Trim()).ToArray();
        return parts.Length == 0 ? null : string.Join("\n\n", parts);
    }

    /// <summary>VibeCode exposes app-server's real model ids directly, so no alias mapping is needed.</summary>
    private string? ApiModel(string? model) => model;

    public static string ResolveCliPath()
    {
        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable("VIBECODE_CODEX_PATH") is { Length: > 0 } configured)
            candidates.Add(configured);

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "codex.exe"));
        candidates.Add(Path.Combine(appData, "npm", "codex.exe"));

        // npm's Windows package keeps the native binary below the JS shim. Cover both
        // layouts used by recent @openai/codex releases.
        var npmRoot = Path.Combine(appData, "npm", "node_modules", "@openai", "codex");
        candidates.Add(Path.Combine(npmRoot, "node_modules", "@openai", "codex-win32-x64", "vendor",
            "x86_64-pc-windows-msvc", "bin", "codex.exe"));
        candidates.Add(Path.Combine(npmRoot, "vendor", "x86_64-pc-windows-msvc", "codex", "codex.exe"));
        candidates.Add(Path.Combine(npmRoot, "node_modules", "@openai", "codex-win32-x64", "vendor",
            "x86_64-pc-windows-msvc", "codex", "codex.exe"));

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            candidates.Add(Path.Combine(dir.Trim(), "codex.exe"));
        }
        // The Microsoft Store Codex desktop package exposes a WindowsApps binary on PATH, but normal unpackaged
        // desktop apps cannot CreateProcess it (Win32 error 5). Keep it out of CLI discovery; users need the actual
        // @openai/codex CLI, a sibling binary, or VIBECODE_CODEX_PATH.
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (candidate.Contains("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase)) continue;
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* malformed/inaccessible PATH entry */ }
        }
        throw new FileNotFoundException(
            "Could not find the OpenAI Codex runtime (codex.exe). Install it with `npm install -g @openai/codex`, " +
            "then sign in from VibeCode's account menu. You can also point VibeCode at an existing binary with VIBECODE_CODEX_PATH.");
    }

    public void Start()
    {
        var psi = CodexEnvironment.CreateAppServerStartInfo(_options.Cwd, _options.HomeDirectory,
            _options.SwarmsEnabled, _options.SwarmMaxWorkers, _options.McpServers);
        var integrity = CodexEnvironment.VerifyRuntimeIntegrity(psi.FileName);
        _runtimeIntegrityVerified = integrity.Trusted;
        _runtimeIntegrityReason = integrity.Reason;
        StartArtifactWatcher();

        _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _proc.Exited += (_, _) =>
        {
            foreach (var pending in _pending) pending.Value.TrySetCanceled();
            if (!_disposed) Exited?.Invoke(_proc!.ExitCode, StderrTail);
        };
        _proc.Start();
        ProcessJob.Assign(_proc);
        _stdin = _proc.StandardInput;
        _ = Task.Run(WriteStdinLoop);
        _ = Task.Run(ReadStdoutLoop);
        _ = Task.Run(ReadStderrLoop);
        _ = Task.Run(InitializeAsync);
    }

    private string StderrTail { get { lock (_stderr) return _stderr.ToString(); } }

    private async Task InitializeAsync()
    {
        try
        {
            await RequestAsync("initialize", new JsonObject
            {
                ["clientInfo"] = new JsonObject
                {
                    ["name"] = "vibecode",
                    ["title"] = "VibeCode",
                    ["version"] = "1.0.0",
                },
            });
            Notify("initialized", new JsonObject());

            // Fail early with a useful sign-in state. Otherwise model/list or thread/start can surface a vague
            // upstream Unauthorized error and the host has no reliable way to show its Codex sign-in action.
            // Managed ChatGPT auth refreshes during normal app-server use. Force a refresh at session startup too,
            // so a VibeCode chat reopened after days asleep begins with a current token rather than failing its first turn.
            var accountResponse = await RequestAsync("account/read", new JsonObject { ["refreshToken"] = true });
            var accountResult = accountResponse?["result"];
            if (accountResult?["requiresOpenaiAuth"]?.GetValue<bool>() == true && accountResult["account"] is null)
                throw new InvalidOperationException("VibeCode is not signed in to OpenAI. Open the account menu, choose Add account, then OpenAI for VibeCode.");

            var modelResponse = await RequestAsync("model/list", new JsonObject
            {
                ["limit"] = 100,
                ["includeHidden"] = false,
            });
            BuildModels(modelResponse?["result"]?["data"] as JsonArray);
            BuildCommands();

            var method = _options.ForkSession && _options.Resume is not null ? "thread/fork"
                : _options.Resume is not null ? "thread/resume"
                : "thread/start";
            var p = new JsonObject();
            if (_options.Resume is not null) p["threadId"] = _options.Resume;
            p["cwd"] = _options.Cwd;
            var startModel = ApiModel(_model);
            if (!string.IsNullOrWhiteSpace(startModel) && startModel != "default") p["model"] = startModel;
            ApplySecurity(p, legacyThreadShape: true);
            var threadResponse = await RequestAsync(method, p);
            SessionId = threadResponse?["result"]?["thread"]?["id"]?.GetValue<string>()
                        ?? threadResponse?["result"]?["thread"]?["sessionId"]?.GetValue<string>();
            if (SessionId is null) throw new InvalidOperationException("Codex app-server did not return a thread id.");

            // Naming a fresh thread creates durable user-facing metadata without spending a model turn. It also
            // keeps an empty host chat resumable if the user activates a Bridge before sending its first message.
            if (_options.Resume is null && !string.IsNullOrWhiteSpace(_options.Title))
            {
                try
                {
                    await RequestAsync("thread/name/set", new JsonObject
                    {
                        ["threadId"] = SessionId,
                        ["name"] = _options.Title,
                    });
                }
                catch { /* naming is useful metadata, never a reason to fail an otherwise valid thread */ }
            }

            // A brand-new app-server thread is not written to a rollout until it has model-visible history. Bridge
            // peers can be closed before their first user turn, so inject their coordination instructions now. This
            // both gives the peer its role immediately and makes the empty peer thread genuinely resumable.
            if (_options.Resume is null && _appendPromptPending)
            {
                try
                {
                    await RequestAsync("thread/inject_items", new JsonObject
                    {
                        ["threadId"] = SessionId,
                        ["items"] = new JsonArray(new JsonObject
                        {
                            ["type"] = "message",
                            ["role"] = "developer",
                            ["content"] = new JsonArray(new JsonObject
                            {
                                ["type"] = "input_text",
                                ["text"] = _effectiveAppendPrompt,
                            }),
                        }),
                    });
                    _appendPromptPending = false;
                }
                catch (Exception ex)
                {
                    // Older app-server builds can reject injected developer items. Keep the original first-turn
                    // prepend as a compatibility fallback; the visible warning explains why an untouched peer may
                    // not survive an app restart yet.
                    const string label = "Codex Bridge";
                    Emit(new JsonObject
                    {
                        ["type"] = "system", ["subtype"] = "permission_denied", ["tool_name"] = label,
                        ["message"] = "Session instructions will be applied on the first message; early thread persistence was unavailable: " + ex.Message,
                    });
                }
            }

            Emit(new JsonObject
            {
                ["type"] = "system",
                ["subtype"] = "init",
                ["session_id"] = SessionId,
                ["model"] = _model ?? DefaultModelId(),
                ["permissionMode"] = _permissionMode,
            });
            if (_options.Resume is not null)
            {
                _hydratingHistory = true;
                try { await HydrateHistoryAsync(); }
                finally
                {
                    _hydratingHistory = false;
                    // Persisted collab items describe the old process. They remain useful in the Done list, but a new
                    // app-server process has no live child turns to interrupt until this session spawns/resumes one.
                    foreach (var agent in _subagents.Values.Where(a => a.IsActive))
                        ApplySubagentState(agent, "completed", "Previous run", notify: false);
                    if (_subagents.Count > 0) EmitSubagentUpdate();
                }
            }
            Initialized?.Invoke();

            List<JsonNode> queued;
            lock (_earlyMessages)
            {
                queued = _earlyMessages.Select(x => x.DeepClone()).ToList();
                _earlyMessages.Clear();
            }
            foreach (var content in queued) _ = StartTurnAsync(content);
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            EmitFailure("Codex failed to initialize", ex.Message);
            Initialized?.Invoke();
        }
    }

    /// <summary>Render persisted Codex turns when a normal chat or Bridge peer is resumed/forked.</summary>
    private async Task HydrateHistoryAsync()
    {
        if (SessionId is null) return;
        try
        {
            var response = await RequestAsync("thread/read", new JsonObject
            {
                ["threadId"] = SessionId,
                ["includeTurns"] = true,
            });
            if (response?["result"]?["thread"]?["turns"] is not JsonArray turns) return;
            foreach (var turn in turns.OfType<JsonObject>())
            {
                if (turn["items"] is not JsonArray items) continue;
                foreach (var item in items.OfType<JsonObject>())
                {
                    if (item["type"]?.GetValue<string>() == "userMessage") EmitHistoricalUser(item);
                    else TranslateCompleted(item);
                }
            }
            _announcedTools.Clear();
            _startedItems.Clear();
            Emit(new JsonObject { ["type"] = "system", ["subtype"] = "resume_boundary" });
        }
        catch (Exception ex)
        {
            // A history-render failure must not make a valid resumed thread unusable. The server already resumed the
            // model context; show a warning and let the user continue in the live thread.
            Emit(new JsonObject
            {
                ["type"] = "system", ["subtype"] = "permission_denied", ["tool_name"] = "Codex history",
                ["message"] = "The thread resumed, but its previous messages could not be displayed: " + ex.Message,
            });
        }
    }

    private void EmitHistoricalUser(JsonObject item)
    {
        var blocks = new JsonArray();
        if (item["content"] is JsonArray content)
        {
            foreach (var input in content.OfType<JsonObject>())
            {
                var type = input["type"]?.GetValue<string>();
                if (type == "text" && input["text"]?.GetValue<string>() is { Length: > 0 } text)
                    blocks.Add(new JsonObject { ["type"] = "text", ["text"] = text });
                else if (type is "image" or "localImage")
                    blocks.Add(new JsonObject { ["type"] = "text", ["text"] = "[Attached image]" });
            }
        }
        if (blocks.Count == 0) return;
        Emit(new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = blocks },
        });
    }

    private void BuildModels(JsonArray? data)
    {
        Models = new JsonArray();
        var seenBackends = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (data is not null)
        {
            foreach (var entry in data.OfType<JsonObject>())
            {
                var levels = new JsonArray();
                if (entry["supportedReasoningEfforts"] is JsonArray efforts)
                    foreach (var e in efforts.OfType<JsonObject>())
                        if (e["reasoningEffort"]?.GetValue<string>() is { } level) levels.Add(level);
                var id = entry["id"]?.GetValue<string>() ?? entry["model"]?.GetValue<string>() ?? "";
                var displayName = entry["displayName"]?.GetValue<string>() ?? id;
                var description = entry["description"]?.GetValue<string>();
                // Keep the versioned catalog id for app-server calls, but use the product name for the
                // flagship Sol model. The catalog description remains separate picker text; it must not
                // replace the model name in the compact composer pill.
                if (id.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase)
                    && id.EndsWith("-sol", StringComparison.OrdinalIgnoreCase))
                {
                    displayName = "GPT Sol 5.6";
                    if (string.IsNullOrWhiteSpace(description))
                        description = "Fast and affordable agentic coding model.";
                }
                var resolved = entry["model"]?.GetValue<string>() ?? id;
                seenBackends.Add(id);
                Models.Add(new JsonObject
                {
                    ["value"] = id,
                    ["displayName"] = displayName,
                    ["description"] = description,
                    ["resolvedModel"] = resolved,
                    ["supportedEffortLevels"] = levels,
                    ["supportsEffort"] = levels.Count > 0,
                    ["supportsAutoMode"] = true,
                    ["supportsFastMode"] = false,
                    ["isDefault"] = entry["isDefault"]?.GetValue<bool>() ?? false,
                });
            }
        }
        if (Models.Count == 0)
        {
            Models.Add(new JsonObject
            {
                ["value"] = "default", ["displayName"] = "Codex default", ["resolvedModel"] = "default",
                ["supportedEffortLevels"] = new JsonArray("low", "medium", "high", "xhigh"),
                ["supportsEffort"] = true, ["supportsAutoMode"] = true,
            });
        }

        // If app-server's live catalog omitted a backend (rare/offline), still surface the GPT-5.6 rows so the
        // picker matches the fallback menu and a later model switch can retry.
        foreach (var entry in CodexModelPreset.All)
        {
            if (!seenBackends.Add(entry.ModelId)) continue;
            if (Models.OfType<JsonObject>().Any(row =>
                    string.Equals(row["value"]?.GetValue<string>(), entry.ModelId, StringComparison.OrdinalIgnoreCase)))
                continue;
            Models.Add(new JsonObject
            {
                ["value"] = entry.ModelId,
                ["displayName"] = entry.DisplayName,
                ["description"] = entry.Description,
                ["resolvedModel"] = entry.ModelId,
                ["supportedEffortLevels"] = new JsonArray("low", "medium", "high", "xhigh"),
                ["supportsEffort"] = true,
                ["supportsAutoMode"] = true,
                ["supportsFastMode"] = false,
                ["isDefault"] = string.Equals(_model, entry.ModelId, StringComparison.OrdinalIgnoreCase),
            });
        }

        if (string.IsNullOrWhiteSpace(_model)) _model = DefaultModelId();
    }

    private string? DefaultModelId() => Models.OfType<JsonObject>()
        .FirstOrDefault(x => x["isDefault"]?.GetValue<bool>() == true)?["value"]?.GetValue<string>()
        ?? Models.OfType<JsonObject>().FirstOrDefault()?["value"]?.GetValue<string>();

    private void BuildCommands()
    {
        Commands = new JsonArray(
            Command("review", "Review the current changes"),
            Command("plan", "Switch to plan mode"),
            Command("status", "Show session status"),
            Command("init", "Create repository instructions"),
            Command("compact", "Compact conversation context"));
        static JsonObject Command(string name, string description) =>
            new() { ["name"] = name, ["description"] = description };
    }

    public void SendUser(JsonNode content)
    {
        if (_disposed) return;
        if (SessionId is null)
        {
            lock (_earlyMessages) _earlyMessages.Enqueue(content.DeepClone());
            return;
        }
        _ = StartTurnAsync(content.DeepClone());
    }

    private async Task StartTurnAsync(JsonNode content)
    {
        if (SessionId is null) return;
        try
        {
            var input = BuildInput(content);
            var p = new JsonObject
            {
                ["threadId"] = SessionId,
                ["input"] = input,
                ["cwd"] = _options.Cwd,
            };
            var turnModel = ApiModel(_model);
            if (!string.IsNullOrWhiteSpace(turnModel) && turnModel != "default") p["model"] = turnModel;
            if (!string.IsNullOrWhiteSpace(_effort)) p["effort"] = _effort;
            ApplySecurity(p, legacyThreadShape: false);
            var response = await RequestAsync("turn/start", p);
            _turnId = response?["result"]?["turn"]?["id"]?.GetValue<string>() ?? _turnId;
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            EmitFailure("Codex turn failed to start", ex.Message);
        }
    }

    private JsonArray BuildInput(JsonNode content)
    {
        var input = new JsonArray();
        if (content is JsonValue scalar)
        {
            AddText(scalar.GetValue<string>());
            return input;
        }
        if (content is not JsonArray blocks) return input;
        foreach (var block in blocks.OfType<JsonObject>())
        {
            switch (block["type"]?.GetValue<string>())
            {
                case "text": AddText(block["text"]?.GetValue<string>() ?? ""); break;
                case "image":
                {
                    var source = block["source"] as JsonObject;
                    if (source?["data"]?.GetValue<string>() is { Length: > 0 } b64)
                    {
                        var media = source["media_type"]?.GetValue<string>() ?? "image/png";
                        var path = SaveAttachment(Convert.FromBase64String(b64), ExtensionFor(media));
                        input.Add(new JsonObject { ["type"] = "localImage", ["path"] = path });
                    }
                    break;
                }
                case "document":
                {
                    var source = block["source"] as JsonObject;
                    if (source?["data"]?.GetValue<string>() is { Length: > 0 } b64)
                    {
                        var media = source["media_type"]?.GetValue<string>() ?? "application/pdf";
                        var path = SaveAttachment(Convert.FromBase64String(b64), ExtensionFor(media));
                        AddText($"A document is attached at `{path}`. Read it as part of this request.");
                    }
                    break;
                }
            }
        }
        return input;

        void AddText(string text)
        {
            if (_appendPromptPending)
            {
                text = _effectiveAppendPrompt + "\n\n" + text;
                _appendPromptPending = false;
            }
            if (!string.IsNullOrWhiteSpace(text)) input.Add(new JsonObject { ["type"] = "text", ["text"] = text });
        }
    }

    private string SaveAttachment(byte[] bytes, string extension)
    {
        var dir = Path.Combine(Path.GetTempPath(), "VibeCode", "codex-attachments");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, Guid.NewGuid().ToString("N") + extension);
        File.WriteAllBytes(path, bytes);
        _tempAttachments.Add(path);
        return path;
    }

    private static string ExtensionFor(string media) => media.ToLowerInvariant() switch
    {
        "image/jpeg" => ".jpg", "image/gif" => ".gif", "image/webp" => ".webp",
        "application/pdf" => ".pdf", _ => ".png",
    };

    private void ApplySecurity(JsonObject p, bool legacyThreadShape)
    {
        var (approval, legacySandbox, policyType) = _permissionMode switch
        {
            "plan" => ("never", "read-only", "readOnly"),
            "bypassPermissions" => ("never", "danger-full-access", "dangerFullAccess"),
            "acceptEdits" or "auto" => ("never", "workspace-write", "workspaceWrite"),
            _ => ("on-request", "workspace-write", "workspaceWrite"),
        };
        p["approvalPolicy"] = approval;
        if (legacyThreadShape)
        {
            p["sandbox"] = legacySandbox;
        }
        else
        {
            var policy = new JsonObject { ["type"] = policyType };
            if (policyType == "workspaceWrite")
            {
                policy["writableRoots"] = new JsonArray(_options.Cwd);
                policy["networkAccess"] = true;
            }
            p["sandboxPolicy"] = policy;
        }
    }

    private async Task ReadStdoutLoop()
    {
        try
        {
            string? line;
            while ((line = await _proc!.StandardOutput.ReadLineAsync()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try { HandleLine(JsonNode.Parse(line) as JsonObject); }
                catch (Exception ex) { Debug.WriteLine($"vibecode: bad Codex line: {ex.Message}"); }
            }
        }
        catch (Exception ex) { Debug.WriteLine($"vibecode: Codex stdout ended: {ex.Message}"); }
    }

    private async Task ReadStderrLoop()
    {
        try
        {
            string? line;
            while ((line = await _proc!.StandardError.ReadLineAsync()) is not null)
            {
                lock (_stderr)
                {
                    _stderr.AppendLine(line);
                    if (_stderr.Length > 16000) _stderr.Remove(0, _stderr.Length - 16000);
                }
            }
        }
        catch { /* process gone */ }
    }

    private void HandleLine(JsonObject? message)
    {
        if (message is null) return;
        var method = message["method"]?.GetValue<string>();
        if (method is not null)
        {
            if (message["id"] is { } requestId) HandleServerMessage(method, message["params"] as JsonObject, requestId);
            else HandleNotification(method, message["params"] as JsonObject);
            return;
        }
        if (message["id"] is not { } id) return;
        var key = RpcKey(id);
        if (!_pending.TryRemove(key, out var pending)) return;
        if (message["error"] is { } error) pending.TrySetException(new InvalidOperationException(
            error["message"]?.GetValue<string>() ?? error.ToJsonString()));
        else pending.TrySetResult(message);
    }

    private void HandleNotification(string method, JsonObject? p)
    {
        NotificationReceived?.Invoke(method, p?.DeepClone().AsObject());
        var threadId = NotificationThreadId(p);
        var isRootThread = IsRootThread(threadId);
        switch (method)
        {
            case "turn/started":
                if (isRootThread) BeginRootTurn(p);
                else BeginSubagentTurn(threadId!, p);
                EmitTurnActivity();
                break;
            case "item/started":
                if (p?["item"] is JsonObject started)
                {
                    if (!isRootThread) ObserveSubagentItem(threadId!, started, completed: false);
                    TranslateStarted(started, isRootThread ? null : threadId);
                }
                break;
            case "item/completed":
                if (p?["item"] is JsonObject completed)
                {
                    var subagentThreadId = isRootThread ? null : threadId;
                    StopStreamItem(completed["id"]?.GetValue<string>(), subagentThreadId);
                    TranslateCompleted(completed, subagentThreadId);
                    if (!isRootThread) ObserveSubagentItem(threadId!, completed, completed: true);
                }
                break;
            case "item/agentMessage/delta": StreamItemDelta(p, "text", "text_delta", "text"); break;
            case "item/plan/delta": StreamItemDelta(p, "text", "text_delta", "text"); break;
            case "item/reasoning/summaryTextDelta": StreamItemDelta(p, "thinking", "thinking_delta", "thinking"); break;
            case "item/reasoning/textDelta": StreamItemDelta(p, "thinking", "thinking_delta", "thinking"); break;
            case "turn/plan/updated":
                if (isRootThread) TranslatePlanUpdate(p);
                else SetSubagentActivity(threadId!, "Updating its plan");
                break;
            case "turn/diff/updated": CaptureTurnDiff(threadId, p?["diff"]?.GetValue<string>()); break;
            case "turn/completed":
                if (isRootThread) RootTurnCompleted(p?["turn"] as JsonObject);
                else SubagentTurnCompleted(threadId!, p?["turn"] as JsonObject);
                break;
            case "thread/tokenUsage/updated": CaptureUsage(p, threadId); break;
            case "thread/status/changed": ObserveThreadStatus(threadId, p?["status"]); break;
            case "error":
                if (isRootThread)
                    _lastError = p?["error"]?["message"]?.GetValue<string>() ?? p?["message"]?.GetValue<string>();
                else
                    SetSubagentState(threadId!, "errored",
                        p?["error"]?["message"]?.GetValue<string>() ?? p?["message"]?.GetValue<string>() ?? "Error");
                break;
            case "warning" or "configWarning":
                if ((p?["message"]?.GetValue<string>() ?? p?["summary"]?.GetValue<string>()) is { Length: > 0 } warning)
                    Emit(new JsonObject { ["type"] = "system", ["subtype"] = "permission_denied", ["tool_name"] = "Codex", ["message"] = warning });
                break;
            case "serverRequest/resolved":
                if (p?["requestId"] is { } resolved)
                {
                    var key = "codex:" + RpcKey(resolved);
                    _approvalRpcIds.Remove(key);
                    _approvalMethods.Remove(key);
                    _approvalParams.Remove(key);
                    PermissionCancelled?.Invoke(key);
                }
                break;
        }
    }

    // Narrow offline-regression seam used by VibeCode.ProtocolTest. Production notifications still enter only through
    // HandleLine; this avoids launching a model turn just to verify parent/child event scoping.
    internal void PrimeNotificationFixture(string sessionId) => SessionId = sessionId;
    internal void HandleNotificationFixture(string method, JsonObject? p) => HandleNotification(method, p);

    private static string? NotificationThreadId(JsonObject? p)
    {
        try { return p?["threadId"]?.GetValue<string>(); }
        catch { return null; }
    }

    // Older app-server builds omitted threadId on a few notifications. Preserve those as parent events, but once an
    // id is present it must match exactly; child threads share this connection and are not parent lifecycle signals.
    private bool IsRootThread(string? threadId) => string.IsNullOrWhiteSpace(threadId)
        || SessionId is null
        || string.Equals(threadId, SessionId, StringComparison.Ordinal);

    private void BeginRootTurn(JsonObject? p)
    {
        _interruptRequested = false;
        // A new parent turn may legitimately resume a previously stopped child thread. Keep latches only for late
        // child turns that are already in flight; dormant children are safe to use again in the new turn.
        foreach (var threadId in _subagentsCancelledBeforeStart.Keys)
            if (!_activeSubagentTurns.ContainsKey(threadId))
                _subagentsCancelledBeforeStart.TryRemove(threadId, out _);
        _turnId = p?["turn"]?["id"]?.GetValue<string>() ?? _turnId;
        _rootThreadReportedActive = true;
        _turnUsage = default;
        _lastUsage = null;
        _turnDiffByThread.Clear();
        _turnFileChangePaths.Clear();
        lock (_artifactWatchGate)
        {
            _turnCommandFilePaths.Clear();
            _activeCommandItems.Clear();
            _commandCaptureUntilUtc = DateTime.MinValue;
        }
        BeginMessageStream();
    }

    private void BeginSubagentTurn(string threadId, JsonObject? p)
    {
        var cancelledBeforeStart = _subagentsCancelledBeforeStart.ContainsKey(threadId);
        if (p?["turn"]?["id"]?.GetValue<string>() is { Length: > 0 } turnId)
        {
            _activeSubagentTurns[threadId] = turnId;
            // Stop may land while a spawned child is still pending initialization and has no turn id yet. Honor that
            // click as soon as app-server announces the child turn instead of silently letting it run.
            if (_interruptRequested || cancelledBeforeStart)
                _ = SafeRequest("turn/interrupt", new JsonObject { ["threadId"] = threadId, ["turnId"] = turnId });
        }
        var agent = EnsureSubagent(threadId);
        agent.SawTurn = true;
        ApplySubagentState(agent,
            cancelledBeforeStart ? "interrupted" : "running",
            cancelledBeforeStart ? "Stopping" : string.IsNullOrWhiteSpace(agent.Activity) ? "Working" : agent.Activity,
            notify: true,
            preserveCancellationLatch: cancelledBeforeStart);
    }

    private void RootTurnCompleted(JsonObject? turn)
    {
        _turnId = null; // the parent itself is no longer interruptible; any still-running children remain targets
        _rootThreadReportedActive = false;
        _pendingRootCompletion = turn?.DeepClone().AsObject() ?? new JsonObject { ["status"] = "completed" };
        TryCompletePendingRootTurn();
        EmitTurnActivity();
    }

    private void SubagentTurnCompleted(string threadId, JsonObject? turn)
    {
        EmitTurnDiffTools(threadId, threadId);
        _activeSubagentTurns.TryRemove(threadId, out _);
        _subagentsCancelledBeforeStart.TryRemove(threadId, out _);
        var status = turn?["status"]?.GetValue<string>() switch
        {
            "failed" => "errored",
            "interrupted" => "interrupted",
            _ => "completed",
        };
        var error = turn?["error"]?["message"]?.GetValue<string>();
        SetSubagentState(threadId, status, string.IsNullOrWhiteSpace(error) ? "Finished" : error!);
        TryCompletePendingRootTurn();
        EmitTurnActivity();
    }

    private bool HasActiveSubagents =>
        _activeSubagentTurns.Keys.Any(threadId => !_subagentsCancelledBeforeStart.ContainsKey(threadId))
        || _subagents.Values.Any(a => a.IsActive);

    private void TryCompletePendingRootTurn()
    {
        if (_pendingRootCompletion is null || HasActiveSubagents) return;
        var completed = _pendingRootCompletion;
        _pendingRootCompletion = null;
        CompleteTurn(completed);
    }

    private void EmitTurnActivity()
    {
        if (_hydratingHistory) return;
        Emit(new JsonObject
        {
            ["type"] = "system",
            ["subtype"] = "turn_activity",
            ["active"] = _turnId is not null || _rootThreadReportedActive || HasActiveSubagents,
        });
    }

    private string TurnDiffKey(string? threadId) => string.IsNullOrWhiteSpace(threadId)
        ? SessionId ?? "root"
        : threadId;

    private void CaptureTurnDiff(string? threadId, string? diff)
    {
        var key = TurnDiffKey(threadId);
        if (string.IsNullOrWhiteSpace(diff)) _turnDiffByThread.Remove(key);
        else _turnDiffByThread[key] = diff;
    }

    private void BeginMessageStream(string? subagentThreadId = null)
    {
        _streamIndexByItem.Clear();
        _nextStreamIndex = 0;
        _streamStarted = true;
        EmitStreamEvent(new JsonObject { ["type"] = "message_start" }, subagentThreadId);
    }

    private void StreamItemDelta(JsonObject? p, string blockType, string deltaType, string deltaField)
    {
        var itemId = p?["itemId"]?.GetValue<string>() ?? p?["id"]?.GetValue<string>();
        if (itemId is null) return;
        var threadId = NotificationThreadId(p);
        var subagentThreadId = IsRootThread(threadId) ? null : threadId;
        if (!_streamStarted) BeginMessageStream(subagentThreadId);
        if (!_streamIndexByItem.TryGetValue(itemId, out var index))
        {
            index = _nextStreamIndex++;
            _streamIndexByItem[itemId] = index;
            EmitStreamEvent(new JsonObject
            {
                ["type"] = "content_block_start", ["index"] = index,
                ["content_block"] = new JsonObject { ["type"] = blockType, [blockType] = "" },
            }, subagentThreadId);
        }
        var delta = p?["delta"]?.GetValue<string>() ?? p?["text"]?.GetValue<string>() ?? "";
        if (delta.Length == 0) return;
        EmitStreamEvent(new JsonObject
        {
            ["type"] = "content_block_delta", ["index"] = index,
            ["delta"] = new JsonObject { ["type"] = deltaType, [deltaField] = delta },
        }, subagentThreadId);
    }

    private void StopStreamItem(string? itemId, string? subagentThreadId = null)
    {
        if (itemId is null || !_streamIndexByItem.Remove(itemId, out var index)) return;
        EmitStreamEvent(new JsonObject { ["type"] = "content_block_stop", ["index"] = index }, subagentThreadId);
    }

    private void EmitStreamEvent(JsonObject ev, string? subagentThreadId = null)
    {
        var envelope = new JsonObject
        {
            ["type"] = "stream_event",
            ["event"] = ev,
        };
        if (!string.IsNullOrWhiteSpace(subagentThreadId)) envelope["subagent_thread_id"] = subagentThreadId;
        Emit(envelope);
    }

    private void HandleServerMessage(string method, JsonObject? p, JsonNode rpcId)
    {
        var key = "codex:" + RpcKey(rpcId);
        _approvalRpcIds[key] = rpcId.DeepClone();
        _approvalMethods[key] = method;
        _approvalParams[key] = p?.DeepClone().AsObject() ?? new JsonObject();
        if (method == "item/commandExecution/requestApproval")
        {
            var integrity = VerifyApprovalEnvelope(method, p);
            var network = p?["networkApprovalContext"] as JsonObject;
            var networkReason = network is null ? null
                : $"Network access to {network["host"]}{(network["protocol"] is null ? "" : " via " + network["protocol"])}";
            PermissionRequested?.Invoke(new PermissionRequest
            {
                RequestId = key,
                ToolName = "Bash",
                ToolUseId = p?["itemId"]?.GetValue<string>(),
                Input = new JsonObject
                {
                    ["command"] = p?["command"]?.GetValue<string>() ?? "",
                    ["cwd"] = p?["cwd"]?.GetValue<string>(),
                    ["description"] = networkReason ?? p?["reason"]?.GetValue<string>(),
                },
                IntegrityVerified = integrity.Verified,
                DecisionReason = integrity.Reason,
            });
            return;
        }
        if (method == "item/fileChange/requestApproval")
        {
            var integrity = VerifyApprovalEnvelope(method, p);
            var itemId = p?["itemId"]?.GetValue<string>();
            var started = itemId is not null && _startedItems.TryGetValue(itemId, out var item) ? item : null;
            var first = (started?["changes"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
            PermissionRequested?.Invoke(new PermissionRequest
            {
                RequestId = key,
                ToolName = "Edit",
                ToolUseId = itemId,
                Input = new JsonObject
                {
                    ["file_path"] = first?["path"]?.GetValue<string>() ?? p?["grantRoot"]?.GetValue<string>() ?? "files",
                    ["old_string"] = "",
                    ["new_string"] = first?["diff"]?.GetValue<string>() ?? "",
                    ["reason"] = p?["reason"]?.GetValue<string>(),
                },
                IntegrityVerified = integrity.Verified,
                DecisionReason = integrity.Reason,
            });
            return;
        }
        if (method is "tool/requestUserInput" or "item/tool/requestUserInput")
        {
            PermissionRequested?.Invoke(new PermissionRequest
            {
                RequestId = key,
                ToolName = "AskUserQuestion",
                ToolUseId = p?["itemId"]?.GetValue<string>(),
                Input = p?.DeepClone(),
            });
            return;
        }
        if (method == "item/permissions/requestApproval")
        {
            var integrity = VerifyApprovalEnvelope(method, p);
            PermissionRequested?.Invoke(new PermissionRequest
            {
                RequestId = key,
                ToolName = "RequestPermissions",
                ToolUseId = p?["itemId"]?.GetValue<string>(),
                Input = p?.DeepClone(),
                IntegrityVerified = integrity.Verified,
                DecisionReason = integrity.Reason,
            });
            return;
        }

        // Unknown server-initiated requests must always receive a response or app-server
        // will wait forever. A conservative cancellation keeps the thread usable.
        WriteJson(new JsonObject { ["id"] = rpcId.DeepClone(), ["result"] = new JsonObject { ["decision"] = "cancel" } });
        _approvalRpcIds.Remove(key);
        _approvalMethods.Remove(key);
        _approvalParams.Remove(key);
    }

    internal void TranslateStarted(JsonObject item, string? subagentThreadId = null)
    {
        var id = item["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N");
        _startedItems[id] = item.DeepClone().AsObject();
        switch (item["type"]?.GetValue<string>())
        {
            case "commandExecution":
            {
                var command = SafeString(item["command"]) ?? "";
                var patchChanges = ShellPatchChanges(command);
                if (patchChanges.Count > 0)
                {
                    AnnounceShellPatchTools(id, patchChanges, subagentThreadId);
                }
                else
                {
                    BeginCommandCapture(id);
                    AnnounceTool(id, "Bash", new JsonObject
                    {
                        ["command"] = command,
                        ["cwd"] = SafeString(item["cwd"]),
                    }, subagentThreadId: subagentThreadId);
                }
                break;
            }
            case "mcpToolCall":
                AnnounceTool(id, McpName(item), item["arguments"]?.DeepClone(), subagentThreadId: subagentThreadId);
                break;
            case "webSearch":
                AnnounceTool(id, "WebSearch", WebSearchInput(item), subagentThreadId: subagentThreadId);
                break;
            case "collabAgentToolCall": ObserveCollabAgentTool(item); break;
            case "subAgentActivity": ObserveSubagentActivity(item); break;
            case "contextCompaction":
                EmitScoped(new JsonObject { ["type"] = "system", ["subtype"] = "compact_boundary" }, subagentThreadId);
                break;
        }
    }

    internal void TranslateCompleted(JsonObject item, string? subagentThreadId = null)
    {
        var id = item["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N");
        var type = item["type"]?.GetValue<string>();
        switch (type)
        {
            case "agentMessage": EmitAssistantText(item["text"]?.GetValue<string>() ?? "", subagentThreadId); break;
            case "plan": EmitAssistantText(item["text"]?.GetValue<string>() ?? "", subagentThreadId); break;
            case "reasoning":
            {
                var text = JoinText(item["summary"] ?? item["content"]);
                if (!string.IsNullOrWhiteSpace(text))
                    EmitAssistantBlock(new JsonObject { ["type"] = "thinking", ["thinking"] = text }, subagentThreadId);
                break;
            }
            case "commandExecution":
            {
                _startedItems.TryGetValue(id, out var startedCommand);
                var command = SafeString(item["command"]) ?? SafeString(startedCommand?["command"]) ?? "";
                var patchChanges = ShellPatchChanges(command);
                var failed = Failed(item);
                var output = SafeString(item["aggregatedOutput"]) ?? "";
                if (patchChanges.Count > 0)
                {
                    AnnounceShellPatchTools(id, patchChanges, subagentThreadId);
                    for (var i = 0; i < patchChanges.Count; i++)
                    {
                        var change = patchChanges[i];
                        if (!failed) _turnFileChangePaths.Add(AbsoluteArtifactPath(change.Path));
                        SettleTool(ShellPatchToolId(id, i),
                            output.Length > 0 ? output : failed ? "Patch failed" : "File updated",
                            failed, subagentThreadId);
                    }
                }
                else
                {
                    AnnounceTool(id, "Bash",
                        new JsonObject { ["command"] = command, ["cwd"] = SafeString(item["cwd"] ?? startedCommand?["cwd"]) },
                        subagentThreadId: subagentThreadId);
                    SettleTool(id, output, failed, subagentThreadId);
                }
                EndCommandCapture(id);
                break;
            }
            case "fileChange": TranslateFileChanges(id, item, subagentThreadId); break;
            case "mcpToolCall":
                AnnounceTool(id, McpName(item), item["arguments"]?.DeepClone(), subagentThreadId: subagentThreadId);
                SettleTool(id, JoinText(item["result"] ?? item["error"]), Failed(item), subagentThreadId);
                break;
            case "webSearch":
                // item/completed is authoritative and commonly contains the useful action/query fields that were
                // absent from item/started. Re-emit the same tool id so ChatViewModel refreshes the existing card.
                // A successful webSearch item has no result body of its own (citations arrive in the agent message),
                // so do not manufacture the unhelpful output text "Search completed".
                _startedItems.TryGetValue(id, out var startedSearch);
                AnnounceTool(id, "WebSearch", WebSearchInput(item, startedSearch), refreshExisting: true,
                    subagentThreadId: subagentThreadId);
                var searchError = JoinText(item["error"]);
                SettleTool(id, searchError, Failed(item) || !string.IsNullOrWhiteSpace(searchError), subagentThreadId);
                break;
            case "collabAgentToolCall": ObserveCollabAgentTool(item); break;
            case "subAgentActivity": ObserveSubagentActivity(item); break;
            case "exitedReviewMode": EmitAssistantText(item["review"]?.GetValue<string>() ?? "", subagentThreadId); break;
        }
        _startedItems.Remove(id);
    }

    private SubagentInfo EnsureSubagent(string threadId, string? task = null, string? label = null)
    {
        var agent = _subagents.GetOrAdd(threadId, id =>
        {
            var order = Interlocked.Increment(ref _nextSubagentOrder);
            return new SubagentInfo
            {
                ThreadId = id,
                Order = order,
                Label = $"Subagent {order}",
            };
        });
        if (!string.IsNullOrWhiteSpace(task)) agent.Task = TruncateDisplay(task!, 260);
        if (!string.IsNullOrWhiteSpace(label)) agent.Label = TruncateDisplay(label!, 48);
        return agent;
    }

    /// <summary>Consume the stable collabAgentToolCall item emitted for spawn/send/wait/resume/close operations.</summary>
    private void ObserveCollabAgentTool(JsonObject item)
    {
        var tool = SafeString(item["tool"]) ?? "";
        var callStatus = SafeString(item["status"]) ?? "inProgress";
        var prompt = SafeString(item["prompt"]);
        var states = item["agentsStates"] as JsonObject;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (item["receiverThreadIds"] is JsonArray receivers)
            foreach (var id in receivers.Select(SafeString).Where(x => !string.IsNullOrWhiteSpace(x))) ids.Add(id!);
        if (states is not null)
            foreach (var entry in states) ids.Add(entry.Key);

        foreach (var id in ids)
        {
            var agent = EnsureSubagent(id, tool == "spawnAgent" ? prompt : null);
            var reported = states?[id] as JsonObject;
            var reportedStatus = SafeString(reported?["status"]);
            var reportedMessage = SafeString(reported?["message"]);
            var status = !string.IsNullOrWhiteSpace(reportedStatus) ? reportedStatus! : tool switch
            {
                "spawnAgent" when callStatus == "failed" => "errored",
                "spawnAgent" => agent.Status is "completed" or "interrupted" or "errored" ? agent.Status : "pendingInit",
                "sendInput" or "resumeAgent" when callStatus != "failed" => "running",
                "closeAgent" when callStatus == "completed" => "shutdown",
                _ => agent.Status,
            };
            var activity = !string.IsNullOrWhiteSpace(reportedMessage) ? reportedMessage! : tool switch
            {
                "spawnAgent" => callStatus == "failed" ? "Could not start" : "Starting",
                "sendInput" => callStatus == "failed" ? "Follow-up failed" : "Receiving follow-up",
                "resumeAgent" => callStatus == "failed" ? "Resume failed" : "Resuming",
                "wait" => status is "completed" or "interrupted" or "errored" ? "Finished" : "Working",
                "closeAgent" => callStatus == "completed" ? "Closed" : "Closing",
                _ => agent.Activity,
            };
            ApplySubagentState(agent, status, activity, notify: false);
        }

        if (ids.Count > 0) NotifySubagentStateChanged();
    }

    private void ObserveSubagentActivity(JsonObject item)
    {
        var threadId = SafeString(item["agentThreadId"]);
        if (string.IsNullOrWhiteSpace(threadId)) return;
        var path = SafeString(item["agentPath"]);
        var label = string.IsNullOrWhiteSpace(path) ? null : path!.TrimEnd('/', '\\').Split('/', '\\').LastOrDefault();
        var kind = SafeString(item["kind"]);
        var agent = EnsureSubagent(threadId!, label: label);
        ApplySubagentState(agent, kind == "interrupted" ? "interrupted" : "running", kind switch
        {
            "started" => "Starting",
            "interacted" => "Working",
            "interrupted" => "Interrupted",
            _ => agent.Activity,
        }, notify: false);
        NotifySubagentStateChanged();
    }

    private void ObserveSubagentItem(string threadId, JsonObject item, bool completed)
    {
        var agent = EnsureSubagent(threadId);
        if (!completed || agent.IsActive) agent.Status = "running";
        agent.Activity = TruncateDisplay(SubagentItemActivity(item, completed), 180);
        NotifySubagentStateChanged();
    }

    private static string SubagentItemActivity(JsonObject item, bool completed)
    {
        var prefix = completed ? "Finished · " : "";
        var type = SafeString(item["type"]) ?? "work";
        var detail = type switch
        {
            "commandExecution" => "Command · " + (SafeString(item["command"]) ?? "shell"),
            "fileChange" => "Editing · " + ((item["changes"] as JsonArray)?.OfType<JsonObject>()
                .Select(x => SafeString(x["path"])).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "files"),
            "mcpToolCall" => "Using · " + McpName(item),
            "webSearch" => "Searching · " + (SafeString(item["query"]) ?? "web"),
            "reasoning" => "Reasoning",
            "plan" => "Planning",
            "agentMessage" => "Writing its report",
            "collabAgentToolCall" => "Coordinating subagents",
            "dynamicToolCall" => "Using · " + (SafeString(item["tool"]) ?? "tool"),
            "contextCompaction" => "Compacting context",
            _ => type,
        };
        return prefix + detail;
    }

    private void ObserveThreadStatus(string? threadId, JsonNode? statusNode)
    {
        if (string.IsNullOrWhiteSpace(threadId)) return;
        var type = statusNode is JsonObject status ? SafeString(status["type"]) : SafeString(statusNode);
        if (IsRootThread(threadId))
        {
            if (type == "active") _rootThreadReportedActive = true;
            else if (type is "idle" or "notLoaded" or "systemError") _rootThreadReportedActive = false;
            EmitTurnActivity();
            return;
        }

        var agent = EnsureSubagent(threadId!);
        switch (type)
        {
            case "active": ApplySubagentState(agent, "running", ThreadWaitActivity(statusNode), notify: false); break;
            case "idle" when agent.SawTurn: ApplySubagentState(agent, "completed", "Finished", notify: false); break;
            case "systemError": ApplySubagentState(agent, "errored", "System error", notify: false); break;
            case "notLoaded": ApplySubagentState(agent, "shutdown", "Closed", notify: false); break;
            default: return;
        }
        NotifySubagentStateChanged();
    }

    private static string ThreadWaitActivity(JsonNode? statusNode)
    {
        if (statusNode?["activeFlags"] is not JsonArray flags) return "Working";
        var values = flags.Select(SafeString).OfType<string>().ToHashSet(StringComparer.Ordinal);
        if (values.Contains("waitingOnUserInput")) return "Waiting for input";
        if (values.Contains("waitingOnApproval")) return "Waiting for approval";
        return "Working";
    }

    private void SetSubagentActivity(string threadId, string activity)
    {
        var agent = EnsureSubagent(threadId);
        ApplySubagentState(agent, "running", activity, notify: false);
        NotifySubagentStateChanged();
    }

    private void SetSubagentState(string threadId, string status, string activity)
    {
        ApplySubagentState(EnsureSubagent(threadId), status, activity, notify: false);
        NotifySubagentStateChanged();
    }

    private void SetSubagentState(SubagentInfo agent, string status, string activity) =>
        ApplySubagentState(agent, status, activity, notify: true);

    private void ApplySubagentState(SubagentInfo agent, string status, string? activity, bool notify,
        bool preserveCancellationLatch = false)
    {
        // A late spawn/activity notification must not undo an explicit Stop while the child is still waiting for a
        // turn id. BeginSubagentTurn remains responsible for cancelling a real late turn if one appears.
        if (_subagentsCancelledBeforeStart.ContainsKey(agent.ThreadId)
            && status is "pendingInit" or "running")
        {
            status = "interrupted";
            activity = "Stop requested";
        }
        else if (!preserveCancellationLatch && status is not ("pendingInit" or "running"))
        {
            _subagentsCancelledBeforeStart.TryRemove(agent.ThreadId, out _);
        }
        if (!string.IsNullOrWhiteSpace(status)) agent.Status = status;
        if (!string.IsNullOrWhiteSpace(activity)) agent.Activity = TruncateDisplay(activity!, 180);
        if (notify) NotifySubagentStateChanged();
    }

    private void NotifySubagentStateChanged()
    {
        EmitSubagentUpdate();
        TryCompletePendingRootTurn();
        EmitTurnActivity();
    }

    private void EmitSubagentUpdate()
    {
        var agents = new JsonArray();
        foreach (var agent in _subagents.Values.OrderBy(a => a.Order))
            agents.Add(new JsonObject
            {
                ["thread_id"] = agent.ThreadId,
                ["label"] = agent.Label,
                ["task"] = agent.Task,
                ["activity"] = agent.Activity,
                ["status"] = agent.Status,
            });
        Emit(new JsonObject { ["type"] = "system", ["subtype"] = "subagent_update", ["agents"] = agents });
    }

    private static string? SafeString(JsonNode? node)
    {
        try { return node?.GetValue<string>(); }
        catch { return null; }
    }

    private static string TruncateDisplay(string value, int max)
    {
        var normalized = string.Join(" ", value.Replace("\r", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim()).Where(x => x.Length > 0));
        return normalized.Length <= max ? normalized : normalized[..Math.Max(1, max - 1)] + "…";
    }

    private void TranslateFileChanges(string itemId, JsonObject item, string? subagentThreadId = null)
    {
        var changes = item["changes"] as JsonArray ?? new JsonArray();
        var index = 0;
        foreach (var change in changes.OfType<JsonObject>())
        {
            var id = $"{itemId}:{index++}";
            var path = change["path"]?.GetValue<string>() ?? "file";
            _turnFileChangePaths.Add(AbsoluteArtifactPath(path));
            AnnounceTool(id, "CodexEdit", new JsonObject
            {
                ["file_path"] = path,
                // Current app-server schemas encode this as { "type": "add|delete|update" }.
                // Older builds used a bare string, so accept both without letting a shape change drop the
                // entire completed file-change notification (and therefore the Artifacts entry).
                ["kind"] = PatchKind(change["kind"]),
                ["diff"] = change["diff"]?.GetValue<string>() ?? "",
            }, subagentThreadId: subagentThreadId);
            SettleTool(id, change["diff"]?.GetValue<string>() ?? "File updated", Failed(item), subagentThreadId);
        }
    }

    private void AnnounceShellPatchTools(string commandId, IReadOnlyList<ShellPatchChange> changes,
        string? subagentThreadId)
    {
        for (var i = 0; i < changes.Count; i++)
        {
            var change = changes[i];
            AnnounceTool(ShellPatchToolId(commandId, i), "CodexEdit", new JsonObject
            {
                ["file_path"] = change.Path,
                ["kind"] = change.Kind,
                ["diff"] = change.Diff,
            }, subagentThreadId: subagentThreadId);
        }
    }

    private static string ShellPatchToolId(string commandId, int index) => $"{commandId}:patch:{index}";

    /// <summary>Translate Codex's live turn plan into the shared checklist shape consumed by ChatViewModel.</summary>
    private void TranslatePlanUpdate(JsonObject? p) => Emit(new JsonObject
    {
        ["type"] = "system",
        ["subtype"] = "todo_update",
        ["todos"] = PlanTodos(p),
    });

    internal static JsonArray PlanTodos(JsonObject? p)
    {
        var todos = new JsonArray();
        if (p?["plan"] is not JsonArray plan) return todos;
        foreach (var entry in plan.OfType<JsonObject>())
        {
            var step = entry["step"]?.GetValue<string>() ?? "";
            if (string.IsNullOrWhiteSpace(step)) continue;
            var status = entry["status"]?.GetValue<string>() switch
            {
                "inProgress" or "in_progress" => "in_progress",
                "completed" => "completed",
                _ => "pending",
            };
            todos.Add(new JsonObject
            {
                ["content"] = step,
                ["activeForm"] = step,
                ["status"] = status,
            });
        }
        return todos;
    }

    /// <summary>
    /// Preserve the complete app-server web-search description for the activity card. Current schemas keep a
    /// backwards-compatible top-level query and add a typed action: search(query/queries), openPage(url), or
    /// findInPage(url/pattern). Keeping the action is what lets the expanded UI show the exact operation instead of
    /// the unhelpful result-only text "Search completed".
    /// </summary>
    internal static JsonObject WebSearchInput(JsonObject item, JsonObject? startedItem = null)
    {
        var finalAction = item["action"] as JsonObject;
        var startedAction = startedItem?["action"] as JsonObject;
        var action = MergeActions(finalAction, startedAction);
        var query = StringValue(item["query"]);
        if (string.IsNullOrWhiteSpace(query)) query = ActionQuery(finalAction);
        if (string.IsNullOrWhiteSpace(query)) query = StringValue(startedItem?["query"]);
        if (string.IsNullOrWhiteSpace(query)) query = ActionQuery(startedAction);

        var input = new JsonObject { ["query"] = query ?? "" };
        if (action is not null) input["action"] = action;
        return input;

        static string? ActionQuery(JsonObject? action)
        {
            if (action is null) return null;
            var query = StringValue(action["query"]);
            if (!string.IsNullOrWhiteSpace(query)) return query;
            return action["queries"] is JsonArray queries
                ? queries.Select(StringValue).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))
                : null;
        }

        static JsonObject? MergeActions(JsonObject? finalAction, JsonObject? startedAction)
        {
            if (finalAction is null && startedAction is null) return null;
            var merged = startedAction?.DeepClone().AsObject() ?? new JsonObject();
            if (finalAction is not null)
                foreach (var property in finalAction)
                    merged[property.Key] = property.Value?.DeepClone();
            return merged;
        }

        static string? StringValue(JsonNode? node)
        {
            try { return node?.GetValue<string>(); }
            catch { return null; }
        }
    }

    internal static string? PatchKind(JsonNode? kind)
    {
        if (kind is JsonObject obj) return obj["type"]?.GetValue<string>();
        try { return kind?.GetValue<string>(); }
        catch { return null; }
    }

    internal sealed record ShellPatchChange(string Path, string Kind, string Diff);

    /// <summary>
    /// A patch invoked through exec/shell is reported by app-server as commandExecution, not fileChange. Recover the
    /// native patch payload while the command starts so the UI can show per-file Edit cards immediately and capture
    /// the pre-edit files for undo. The turn-level git diff remains a fallback for opaque shell mutations.
    /// </summary>
    internal static IReadOnlyList<ShellPatchChange> ShellPatchChanges(string? command)
    {
        var result = new List<ShellPatchChange>();
        if (string.IsNullOrWhiteSpace(command)
            || (!command.Contains("apply_patch", StringComparison.OrdinalIgnoreCase)
                && !command.Contains("--codex-run-as-apply-patch", StringComparison.OrdinalIgnoreCase)))
            return result;

        const string patchWord = "Patch";
        var beginMarker = "*** Begin " + patchWord;
        var endMarker = "*** End " + patchWord;
        var lines = command.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (!string.Equals(lines[i], beginMarker, StringComparison.Ordinal)) continue;
            var end = i + 1;
            while (end < lines.Length && !string.Equals(lines[end], endMarker, StringComparison.Ordinal)) end++;
            ParseBlock(i + 1, end);
            i = end;
        }
        return result;

        void ParseBlock(int start, int end)
        {
            string? originalPath = null;
            string? targetPath = null;
            string? kind = null;
            var body = new List<string>();

            void Flush()
            {
                if (string.IsNullOrWhiteSpace(originalPath) || string.IsNullOrWhiteSpace(targetPath) || kind is null) return;
                result.Add(new ShellPatchChange(targetPath, kind, BuildShellPatchDiff(originalPath, targetPath, kind, body)));
                originalPath = targetPath = kind = null;
                body.Clear();
            }

            for (var lineIndex = start; lineIndex < end; lineIndex++)
            {
                var line = lines[lineIndex];
                if (TryShellPatchHeader(line, out var nextKind, out var nextPath))
                {
                    Flush();
                    originalPath = targetPath = nextPath;
                    kind = nextKind;
                    continue;
                }
                if (kind is null) continue;
                const string movePrefix = "*** Move to: ";
                if (line.StartsWith(movePrefix, StringComparison.Ordinal))
                {
                    targetPath = NormalizeShellPatchPath(line[movePrefix.Length..]);
                    continue;
                }
                if (!string.Equals(line, "*** End of File", StringComparison.Ordinal)) body.Add(line);
            }
            Flush();
        }
    }

    private static bool TryShellPatchHeader(string line, out string kind, out string path)
    {
        foreach (var candidate in new[]
                 {
                     (Prefix: "*** Update File: ", Kind: "update"),
                     (Prefix: "*** Add File: ", Kind: "add"),
                     (Prefix: "*** Delete File: ", Kind: "delete"),
                 })
        {
            if (!line.StartsWith(candidate.Prefix, StringComparison.Ordinal)) continue;
            kind = candidate.Kind;
            path = NormalizeShellPatchPath(line[candidate.Prefix.Length..]);
            return path.Length > 0;
        }
        kind = path = "";
        return false;
    }

    private static string NormalizeShellPatchPath(string value)
    {
        var path = value.Trim();
        if (path.Length >= 2 && path[0] == '"' && path[^1] == '"')
        {
            try { return JsonSerializer.Deserialize<string>(path) ?? path[1..^1]; }
            catch { return path[1..^1]; }
        }
        if (path.Length >= 2 && path[0] == '\'' && path[^1] == '\'') return path[1..^1];
        return path;
    }

    private static string BuildShellPatchDiff(string originalPath, string targetPath, string kind, IEnumerable<string> body)
    {
        var diff = new StringBuilder();
        diff.Append("--- ").Append(kind == "add" ? "/dev/null" : "a/" + originalPath).Append('\n');
        diff.Append("+++ ").Append(kind == "delete" ? "/dev/null" : "b/" + targetPath).Append('\n');
        foreach (var line in body)
        {
            if (line.StartsWith("@@", StringComparison.Ordinal) || line.StartsWith('+') || line.StartsWith('-')
                || line.StartsWith(' '))
                diff.Append(line).Append('\n');
            else if (line.Length == 0)
                diff.Append(' ').Append('\n');
        }
        return diff.ToString().TrimEnd('\n');
    }

    /// <summary>Split app-server's aggregated turn diff into the per-file payloads consumed by CodexEdit cards.
    /// This is the only edit signal for changes made inside a shell/dynamic tool instead of a native fileChange item.</summary>
    internal static IEnumerable<(string Path, string Diff)> DiffFileChanges(string? diff)
    {
        if (string.IsNullOrWhiteSpace(diff)) yield break;
        string? path = null;
        var section = new StringBuilder();
        foreach (var line in diff.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                if (!string.IsNullOrWhiteSpace(path) && section.Length > 0)
                    yield return (path, section.ToString().TrimEnd('\r', '\n'));
                path = DiffPath(line);
                section.Clear();
            }
            if (!string.IsNullOrWhiteSpace(path)) section.Append(line).Append('\n');
        }
        if (!string.IsNullOrWhiteSpace(path) && section.Length > 0)
            yield return (path, section.ToString().TrimEnd('\r', '\n'));
    }

    /// <summary>Paths named by a turn-level unified diff. This catches files created/edited through shell commands,
    /// which have no fileChange item of their own.</summary>
    internal static IEnumerable<string> DiffArtifactPaths(string? diff) => DiffFileChanges(diff).Select(x => x.Path);

    private static string? DiffPath(string header)
    {
        var pair = header[11..];
        if (pair.StartsWith("\"a/", StringComparison.Ordinal))
        {
            var split = pair.LastIndexOf(" \"b/", StringComparison.Ordinal);
            if (split >= 0 && pair.EndsWith('"')) return GitUnescape(pair[(split + 4)..^1]);
            return null;
        }
        var unquotedSplit = pair.LastIndexOf(" b/", StringComparison.Ordinal);
        return unquotedSplit >= 0 ? pair[(unquotedSplit + 3)..] : null;
    }

    private static string GitUnescape(string path)
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<string>($"\"{path}\"") ?? path; }
        catch { return path; }
    }

    private string AbsoluteArtifactPath(string path)
    {
        try { return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(_options.Cwd, path)); }
        catch { return path; }
    }

    private void EmitTurnDiffTools(string? threadId, string? subagentThreadId)
    {
        if (!_turnDiffByThread.Remove(TurnDiffKey(threadId), out var diff)) return;
        var index = 0;
        foreach (var change in DiffFileChanges(diff))
        {
            var full = AbsoluteArtifactPath(change.Path);
            if (_turnFileChangePaths.Contains(full)) continue; // a native fileChange item already showed this path
            _turnFileChangePaths.Add(full);
            var id = $"turn-diff:{TurnDiffKey(threadId)}:{index++}";
            AnnounceTool(id, "CodexEdit", new JsonObject
            {
                ["file_path"] = change.Path,
                ["kind"] = DiffPatchKind(change.Diff),
                ["diff"] = change.Diff,
            }, subagentThreadId: subagentThreadId);
            SettleTool(id, "File updated", failed: false, subagentThreadId);
        }
    }

    private static string DiffPatchKind(string diff)
    {
        var normalized = diff.Replace("\r\n", "\n");
        if (normalized.Contains("\n--- /dev/null\n", StringComparison.Ordinal)
            || normalized.Contains("\nnew file mode ", StringComparison.Ordinal)) return "add";
        if (normalized.Contains("\n+++ /dev/null\n", StringComparison.Ordinal)
            || normalized.Contains("\ndeleted file mode ", StringComparison.Ordinal)) return "delete";
        return "update";
    }

    private void EmitDiffArtifacts()
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var diff in _turnDiffByThread.Values)
            foreach (var relative in DiffArtifactPaths(diff).Distinct(StringComparer.OrdinalIgnoreCase))
                candidates.Add(AbsoluteArtifactPath(relative));
        lock (_artifactWatchGate)
            foreach (var full in _turnCommandFilePaths) candidates.Add(full);

        var paths = new JsonArray();
        foreach (var full in candidates)
            if (!_turnFileChangePaths.Contains(full) && File.Exists(full)) paths.Add(full);
        if (paths.Count > 0) Emit(new JsonObject
        {
            ["type"] = "system",
            ["subtype"] = "artifact_update",
            ["paths"] = paths,
        });
    }

    private void StartArtifactWatcher()
    {
        try
        {
            if (!Directory.Exists(_options.Cwd)) return;
            _artifactWatcher = new FileSystemWatcher(_options.Cwd)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
                EnableRaisingEvents = true,
            };
            _artifactWatcher.Created += OnArtifactFileChanged;
            _artifactWatcher.Changed += OnArtifactFileChanged;
            _artifactWatcher.Renamed += OnArtifactFileChanged;
        }
        catch { _artifactWatcher?.Dispose(); _artifactWatcher = null; }
    }

    private void BeginCommandCapture(string id)
    {
        lock (_artifactWatchGate) _activeCommandItems.Add(id);
    }

    private void EndCommandCapture(string id)
    {
        lock (_artifactWatchGate)
            if (_activeCommandItems.Remove(id)) _commandCaptureUntilUtc = DateTime.UtcNow.AddMilliseconds(750);
    }

    private void OnArtifactFileChanged(object sender, FileSystemEventArgs e)
    {
        try
        {
            var full = Path.GetFullPath(e.FullPath);
            if (!ShouldTrackArtifact(full)) return;
            lock (_artifactWatchGate)
            {
                if (_activeCommandItems.Count == 0 && DateTime.UtcNow > _commandCaptureUntilUtc) return;
                _turnCommandFilePaths.Add(full);
            }
        }
        catch { /* transient rename/delete or inaccessible path */ }
    }

    private bool ShouldTrackArtifact(string full)
    {
        var relative = Path.GetRelativePath(_options.Cwd, full);
        if (relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(relative)
            || string.Equals(relative, ".vibecode-bridge.md", StringComparison.OrdinalIgnoreCase)) return false;
        return !relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(IgnoredArtifactDirectories.Contains);
    }

    private void AnnounceTool(string id, string name, JsonNode? input, bool refreshExisting = false,
        string? subagentThreadId = null)
    {
        var firstAnnouncement = _announcedTools.Add(id);
        if (!firstAnnouncement && !refreshExisting) return;
        EmitAssistantBlock(new JsonObject
        {
            ["type"] = "tool_use",
            ["id"] = id,
            ["name"] = name,
            ["input"] = input?.DeepClone() ?? new JsonObject(),
        }, subagentThreadId);
    }

    private void SettleTool(string id, string result, bool failed, string? subagentThreadId = null)
    {
        EmitScoped(new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "tool_result", ["tool_use_id"] = id,
                    ["content"] = result, ["is_error"] = failed,
                }),
            },
        }, subagentThreadId);
    }

    private void EmitAssistantText(string text, string? subagentThreadId = null)
    {
        if (!string.IsNullOrWhiteSpace(text))
            EmitAssistantBlock(new JsonObject { ["type"] = "text", ["text"] = text }, subagentThreadId);
    }

    private void EmitAssistantBlock(JsonObject block, string? subagentThreadId = null) => EmitScoped(new JsonObject
    {
        ["type"] = "assistant",
        ["message"] = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = new JsonArray(block),
        },
    }, subagentThreadId);

    private void CompleteTurn(JsonObject? turn)
    {
        var status = turn?["status"]?.GetValue<string>() ?? "completed";
        var error = turn?["error"]?["message"]?.GetValue<string>() ?? _lastError;
        EmitTurnDiffTools(SessionId, subagentThreadId: null);
        EmitDiffArtifacts();
        Emit(new JsonObject
        {
            ["type"] = "result",
            ["subtype"] = status,
            ["is_error"] = status == "failed",
            ["result"] = error,
            ["usage"] = _lastUsage?.DeepClone() ?? new JsonObject(),
        });
        _turnId = null;
        _rootThreadReportedActive = false;
        _interruptRequested = false;
        _lastError = null;
        _lastUsage = null;
        _turnDiffByThread.Clear();
        _turnFileChangePaths.Clear();
        lock (_artifactWatchGate)
        {
            _turnCommandFilePaths.Clear();
            _activeCommandItems.Clear();
            _commandCaptureUntilUtc = DateTime.MinValue;
        }
        _turnUsage = default;
        _announcedTools.Clear();
        _streamIndexByItem.Clear();
        _streamStarted = false;
        _nextStreamIndex = 0;
    }

    private void CaptureUsage(JsonObject? p, string? threadId)
    {
        var u = p?["tokenUsage"] ?? p?["usage"] ?? p;
        if (u is null) return;
        var usageThread = string.IsNullOrWhiteSpace(threadId) ? SessionId ?? "root" : threadId!;

        // App-server exposes both `last` (the latest model request) and `total` (the cumulative thread
        // snapshot). Derive increments from total after the first event so tool-heavy turns with several
        // model requests are fully counted, while a resumed thread does not import all of its old usage.
        var latest = TokenUsage.From(u["last"] ?? u);
        TokenUsage increment;
        if (u["total"] is { } totalNode)
        {
            var total = TokenUsage.From(totalNode);
            increment = _previousTotalUsageByThread.TryGetValue(usageThread, out var previous) ? total - previous : latest;
            if (increment.HasNegativeValue) increment = latest; // counter reset/rollback compatibility
            _previousTotalUsageByThread[usageThread] = total;
        }
        else
        {
            increment = latest;
        }
        if (increment.HasTokens) _turnUsage += increment;

        // OpenAI inputTokens already INCLUDES cachedInputTokens. Convert it to the disjoint buckets the
        // shared Claude-shaped UI expects, otherwise cached tokens inflate both totals and estimated cost.
        var freshInput = Math.Max(0, _turnUsage.Input - _turnUsage.CachedInput);
        _lastUsage = new JsonObject
        {
            ["input_tokens"] = freshInput,
            ["cache_read_input_tokens"] = _turnUsage.CachedInput,
            ["output_tokens"] = _turnUsage.Output,
            ["reasoning_output_tokens"] = _turnUsage.ReasoningOutput,
            // Context is the latest request's prompt, not the sum of every request made during this turn.
            ["context_input_tokens"] = latest.Input,
            ["context_window"] = Number(u, "modelContextWindow", "model_context_window"),
        };

        // Unlike Claude's partial-message stream, Codex reports usage on a dedicated app-server
        // notification. Forward the current turn snapshot immediately so Bridge panes can update
        // their token and cost readouts while the agent is still running. The same snapshot remains
        // on the final result, where the view model commits it exactly once to session totals.
        Emit(new JsonObject
        {
            ["type"] = "system",
            ["subtype"] = "usage_update",
            ["usage"] = _lastUsage.DeepClone(),
        });

        // Usage is also a useful liveness confirmation, but only while app-server still records that child's turn as
        // active. Final usage commonly arrives next to completion and must not resurrect a finished agent.
        if (!IsRootThread(threadId) && _activeSubagentTurns.ContainsKey(usageThread))
            SetSubagentActivity(usageThread, "Using model tokens");
    }

    private static double Number(JsonNode? node, params string[] names)
    {
        foreach (var name in names)
        {
            var value = node?[name];
            if (value is null) continue;
            try { return value.GetValue<double>(); } catch { if (double.TryParse(value.ToString(), out var n)) return n; }
        }
        return 0;
    }

    private static bool Failed(JsonObject item)
    {
        var status = item["status"]?.GetValue<string>();
        return status is "failed" or "declined" || (item["exitCode"] is not null && Number(item, "exitCode") != 0);
    }

    private static string McpName(JsonObject item)
    {
        var server = item["server"]?.GetValue<string>() ?? "mcp";
        var tool = item["tool"]?.GetValue<string>() ?? "tool";
        return $"mcp__{server}__{tool}";
    }

    private static string JoinText(JsonNode? node)
    {
        if (node is null) return "";
        if (node is JsonValue value) return value.ToString();
        if (node is JsonArray array) return string.Join("\n", array.Select(JoinText).Where(x => x.Length > 0));
        if (node is JsonObject obj)
        {
            foreach (var key in new[] { "text", "content", "message", "output" })
                if (obj[key] is { } child) return JoinText(child);
            return obj.ToJsonString();
        }
        return node.ToString();
    }

    private void EmitFailure(string title, string detail)
    {
        Emit(new JsonObject
        {
            ["type"] = "result", ["subtype"] = "error", ["is_error"] = true,
            ["result"] = $"{title}: {detail}", ["usage"] = new JsonObject(),
        });
    }

    private void EmitScoped(JsonObject envelope, string? subagentThreadId)
    {
        if (!string.IsNullOrWhiteSpace(subagentThreadId))
            envelope["subagent_thread_id"] = subagentThreadId;
        Emit(envelope);
    }

    private void Emit(JsonNode node) => MessageReceived?.Invoke(node);

    private Task<JsonNode?> RequestAsync(string method, JsonObject? p = null)
    {
        var id = Interlocked.Increment(ref _requestSeq).ToString();
        var pending = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = pending;
        WriteJson(new JsonObject { ["id"] = int.Parse(id), ["method"] = method, ["params"] = p ?? new JsonObject() });
        return pending.Task;
    }

    private void Notify(string method, JsonObject? p) =>
        WriteJson(new JsonObject { ["method"] = method, ["params"] = p ?? new JsonObject() });

    private void WriteJson(JsonNode node)
    {
        var json = node.ToJsonString(new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        _writeQueue.Writer.TryWrite(json);
    }

    private async Task WriteStdinLoop()
    {
        try
        {
            await foreach (var json in _writeQueue.Reader.ReadAllAsync())
            {
                var stdin = _stdin;
                if (stdin is null) break;
                await stdin.WriteAsync(json);
                await stdin.WriteAsync('\n');
                await stdin.FlushAsync();
            }
        }
        catch (Exception ex) { Debug.WriteLine($"vibecode: Codex stdin ended: {ex.Message}"); }
    }

    private static string RpcKey(JsonNode id) => id.ToJsonString().Trim('"');

    private KeyValuePair<string, string>[] PrepareInterrupt()
    {
        _interruptRequested = _turnId is not null || HasActiveSubagents;
        var activeChildTurns = _activeSubagentTurns.ToArray();
        if (_interruptRequested)
        {
            var changed = false;
            foreach (var agent in _subagents.Values)
            {
                if (!agent.IsActive || _activeSubagentTurns.ContainsKey(agent.ThreadId)) continue;
                _subagentsCancelledBeforeStart[agent.ThreadId] = 0;
                ApplySubagentState(agent, "interrupted", "Stop requested", notify: false,
                    preserveCancellationLatch: true);
                changed = true;
            }
            if (changed) NotifySubagentStateChanged();
        }
        return activeChildTurns;
    }

    // Offline regression seam: request selection still stays inside InterruptAsync; the fixture only exercises the
    // state transition that used to leave a parent result pinned behind a child with no turn id.
    internal void PrepareInterruptFixture() => PrepareInterrupt();

    public Task InterruptAsync()
    {
        var activeChildTurns = PrepareInterrupt();
        var requests = new List<Task>();
        if (SessionId is not null && _turnId is not null)
            requests.Add(SafeRequest("turn/interrupt", new JsonObject { ["threadId"] = SessionId, ["turnId"] = _turnId }));
        foreach (var child in activeChildTurns)
            requests.Add(SafeRequest("turn/interrupt", new JsonObject { ["threadId"] = child.Key, ["turnId"] = child.Value }));
        return requests.Count == 0 ? Task.CompletedTask : Task.WhenAll(requests);
    }

    public Task SetPermissionModeAsync(string mode)
    {
        _permissionMode = mode;
        return Task.CompletedTask;
    }

    public Task SetModelAsync(string? model, string? effort = null)
    {
        _model = string.IsNullOrWhiteSpace(model) || model == "default" ? DefaultModelId() : model;
        _effort = effort;
        return Task.CompletedTask;
    }

    private async Task SafeRequest(string method, JsonObject p)
    {
        try { await RequestAsync(method, p); }
        catch (Exception ex) { Debug.WriteLine($"vibecode: Codex {method} failed: {ex.Message}"); }
    }

    private (bool Verified, string? Reason) VerifyApprovalEnvelope(string method, JsonObject? p)
    {
        if (!_runtimeIntegrityVerified)
            return (false, _runtimeIntegrityReason ?? "The Codex runtime signature was not verified.");
        if (p is null) return (false, "The Codex approval request had no parameter object.");
        if (!TryString(p["itemId"], out var itemId)
            || !TryString(p["threadId"], out var threadId)
            || !TryString(p["turnId"], out var turnId)
            || !TryInteger(p["startedAtMs"], out _))
            return (false, "The Codex approval request was missing required identity fields.");
        var matchesRoot = string.Equals(threadId, SessionId, StringComparison.Ordinal)
                          && string.Equals(turnId, _turnId, StringComparison.Ordinal);
        var matchesSubagent = _activeSubagentTurns.TryGetValue(threadId, out var childTurn)
                              && string.Equals(turnId, childTurn, StringComparison.Ordinal);
        if (!matchesRoot && !matchesSubagent)
            return (false, "The Codex approval request did not belong to the active thread and turn.");
        if (!_startedItems.ContainsKey(itemId))
            return (false, "The Codex approval request did not match a live tool item.");

        if (method == "item/commandExecution/requestApproval")
        {
            var network = p["networkApprovalContext"];
            if (network is not null && !IsWellFormedNetworkApproval(network))
                return (false, "The Codex network approval context was malformed.");
            var hasCommand = TryString(p["command"], out var command) && !string.IsNullOrWhiteSpace(command);
            var hasActions = p["commandActions"] is JsonArray { Count: > 0 };
            if (!hasCommand && network is null && !hasActions)
                return (false, "The Codex command approval did not identify an action.");
        }
        else if (method == "item/permissions/requestApproval")
        {
            if (!TryString(p["cwd"], out var cwd) || !Path.IsPathFullyQualified(cwd))
                return (false, "The Codex permission request did not contain an absolute working directory.");
            if (!IsWellFormedPermissionProfile(p["permissions"]))
                return (false, "The Codex permission profile did not match the supported filesystem/network schema.");
        }

        return (true, null);
    }

    private static bool IsWellFormedNetworkApproval(JsonNode node)
    {
        if (node is not JsonObject network
            || !TryString(network["host"], out var host)
            || string.IsNullOrWhiteSpace(host)
            || !TryString(network["protocol"], out var protocol)) return false;
        return protocol is "http" or "https" or "socks5Tcp" or "socks5Udp";
    }

    /// <summary>Validate the current app-server RequestPermissionProfile before it can be granted automatically.</summary>
    internal static bool IsWellFormedPermissionProfile(JsonNode? node)
    {
        if (node is not JsonObject profile || !HasOnlyKeys(profile, "fileSystem", "network")) return false;
        var hasPermission = false;

        if (profile.TryGetPropertyValue("network", out var networkNode) && networkNode is not null)
        {
            hasPermission = true;
            if (networkNode is not JsonObject network || !HasOnlyKeys(network, "enabled")) return false;
            if (network.TryGetPropertyValue("enabled", out var enabled) && enabled is not null && !TryBoolean(enabled, out _))
                return false;
        }

        if (profile.TryGetPropertyValue("fileSystem", out var fileSystemNode) && fileSystemNode is not null)
        {
            hasPermission = true;
            if (fileSystemNode is not JsonObject fileSystem
                || !HasOnlyKeys(fileSystem, "entries", "globScanMaxDepth", "read", "write")) return false;
            if (fileSystem.TryGetPropertyValue("globScanMaxDepth", out var depth) && depth is not null
                && (!TryInteger(depth, out var value) || value < 1 || value > uint.MaxValue)) return false;
            if (fileSystem.TryGetPropertyValue("read", out var read) && read is not null && !IsStringArray(read)) return false;
            if (fileSystem.TryGetPropertyValue("write", out var write) && write is not null && !IsStringArray(write)) return false;
            if (fileSystem.TryGetPropertyValue("entries", out var entries) && entries is not null)
            {
                if (entries is not JsonArray rows || rows.Any(row => !IsWellFormedFileSystemEntry(row))) return false;
            }
        }

        return hasPermission;
    }

    private static bool IsWellFormedFileSystemEntry(JsonNode? node)
    {
        if (node is not JsonObject entry
            || !TryString(entry["access"], out var access)
            || access is not ("read" or "write" or "deny")
            || entry["path"] is not JsonObject path
            || !TryString(path["type"], out var type)) return false;

        return type switch
        {
            "path" => TryString(path["path"], out var value) && !string.IsNullOrWhiteSpace(value),
            "glob_pattern" => TryString(path["pattern"], out var pattern) && !string.IsNullOrWhiteSpace(pattern),
            "special" => IsWellFormedSpecialPath(path["value"]),
            _ => false,
        };
    }

    private static bool IsWellFormedSpecialPath(JsonNode? node)
    {
        if (node is not JsonObject value || !TryString(value["kind"], out var kind)) return false;
        return kind switch
        {
            "root" or "minimal" or "tmpdir" or "slash_tmp" => true,
            "project_roots" => value["subpath"] is null || TryString(value["subpath"], out _),
            "unknown" => TryString(value["path"], out var path) && !string.IsNullOrWhiteSpace(path)
                         && (value["subpath"] is null || TryString(value["subpath"], out _)),
            _ => false,
        };
    }

    private static bool IsStringArray(JsonNode node) =>
        node is JsonArray array && array.All(value => TryString(value, out _));

    private static bool HasOnlyKeys(JsonObject value, params string[] allowed) =>
        value.All(property => allowed.Contains(property.Key, StringComparer.Ordinal));

    private static bool TryString(JsonNode? node, out string value)
    {
        try { value = node?.GetValue<string>() ?? ""; return node is not null; }
        catch { value = ""; return false; }
    }

    private static bool TryInteger(JsonNode? node, out long value)
    {
        try { value = node?.GetValue<long>() ?? 0; return node is not null; }
        catch { value = 0; return false; }
    }

    private static bool TryBoolean(JsonNode? node, out bool value)
    {
        try { value = node?.GetValue<bool>() ?? false; return node is not null; }
        catch { value = false; return false; }
    }

    internal static JsonObject BuildPermissionsApprovalResponse(JsonObject? original, bool allow, bool forSession)
    {
        var requested = original?["permissions"] ?? original?["requestedPermissions"];
        var granted = allow && IsWellFormedPermissionProfile(requested)
            ? requested!.DeepClone()
            : new JsonObject();
        return new JsonObject
        {
            ["permissions"] = granted,
            ["scope"] = allow && forSession ? "session" : "turn",
        };
    }

    public void RespondPermission(string requestId, JsonObject result, string? toolUseId)
    {
        if (!_approvalRpcIds.Remove(requestId, out var rpcId)) return;
        _approvalMethods.Remove(requestId, out var method);
        _approvalParams.Remove(requestId, out var original);
        var allow = result["behavior"]?.GetValue<string>() == "allow";
        JsonObject response;
        if (method is "tool/requestUserInput" or "item/tool/requestUserInput")
        {
            response = new JsonObject { ["answers"] = result["updatedInput"]?["answers"]?.DeepClone() ?? new JsonObject() };
        }
        else if (method == "item/permissions/requestApproval")
        {
            response = BuildPermissionsApprovalResponse(original, allow,
                allow && result["updatedPermissions"] is not null);
        }
        else
        {
            var always = result["updatedPermissions"] is not null;
            response = new JsonObject { ["decision"] = allow ? (always ? "acceptForSession" : "accept") : "decline" };
        }
        WriteJson(new JsonObject { ["id"] = rpcId, ["result"] = response });
    }

    public void Dispose()
    {
        _disposed = true;
        _writeQueue.Writer.TryComplete();
        try { _artifactWatcher?.Dispose(); } catch { /* watcher already gone */ }
        try { _stdin?.Close(); } catch { /* broken pipe */ }   // EOF → the CLI exits on its own
        // Reap off the UI thread so closing a pane/bridge never freezes the app; delete temp attachments only
        // once the process is actually gone (it may still hold them open until then).
        ProcessJob.ReapDetached(_proc, afterExit: () =>
        {
            foreach (var path in _tempAttachments)
                try { File.Delete(path); } catch { /* best-effort temp cleanup */ }
        });
    }
}
