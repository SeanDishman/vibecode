using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using VibeCode.Services;

namespace VibeCode.Protocol;

public sealed class KimiSessionOptions
{
    public required string Cwd { get; init; }
    public string? Resume { get; init; }
    public bool ForkSession { get; init; }
    public string? Model { get; init; }
    /// <summary>
    /// Kimi ACP exposes provider-specific thinking values. Older models use <c>on</c>/<c>off</c>; K3 also exposes
    /// <c>low</c>/<c>high</c>/<c>max</c>. Null keeps the CLI default.
    /// </summary>
    public string? Effort { get; init; }
    public string PermissionMode { get; init; } = "default";
    public string? AppendSystemPrompt { get; init; }
    /// <summary>Immutable VibeCode MCP snapshot forwarded through ACP for this session.</summary>
    public IReadOnlyList<McpServerDefinition>? McpServers { get; init; }
    /// <summary>Internal ACP flavor switch used by the first-class <see cref="GrokSession"/> facade.</summary>
    internal bool UseGrokProtocol { get; init; }
    internal string? GrokAuthFilePath { get; init; }
}

/// <summary>
/// A Kimi Code conversation hosted by <c>kimi acp</c>. Kimi's official ACP adapter speaks
/// JSON-RPC 2.0 over newline-delimited stdio. This class translates ACP's session updates and
/// reverse permission requests to the Claude-shaped stream consumed by <see cref="UI.ChatViewModel"/>.
/// </summary>
public sealed class KimiSession : ICodingSession
{
    private const string SystemContextOpen = "<vibecode-system-context>";
    private const string SystemContextClose = "</vibecode-system-context>";
    private static readonly Regex UsageLinePattern = new(
        @"^\s*-\s*(?<label>.+?):\s*input\s+(?<input>[\d\s,._]+),\s*output\s+(?<output>[\d\s,._]+),\s*cache\s+read\s+(?<read>[\d\s,._]+),\s*cache\s+creation\s+(?<write>[\d\s,._]+)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ContextLinePattern = new(
        @"^\s*-\s*Context:\s*(?<used>[\d\s,._]+)\s*/\s*(?<window>[\d\s,._]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    public event Action<JsonNode>? MessageReceived;
    public event Action<PermissionRequest>? PermissionRequested;
    public event Action<string>? PermissionCancelled;
    public event Action<int, string>? Exited;
    public event Action? Initialized;

    /// <summary>Raw ACP notifications for protocol smoke tests and diagnostics.</summary>
    internal event Action<string, JsonObject?>? NotificationReceived;

    public JsonArray Commands { get; private set; } = BuiltInCommands();
    public JsonArray Models { get; private set; } = new();
    public string? SessionId { get; private set; }
    public bool HasExited => _disposed || _initializationFailed || _proc is null || _proc.HasExited;

    private sealed class PendingPermission
    {
        public required JsonNode RpcId { get; init; }
        public required JsonArray Options { get; init; }
        public required string ToolName { get; init; }
        public required string? ToolUseId { get; init; }
        public string? Question { get; init; }
    }

    private sealed class KimiRpcException(int code, string message, JsonNode? data = null) : Exception(message)
    {
        public int Code { get; } = code;
        public JsonNode? RpcData { get; } = data;
    }

    private sealed record TurnFailure(string Title, string Detail, string Subtype);

    private readonly KimiSessionOptions _options;
    private readonly bool _isGrok;
    private readonly object _writeLock = new();
    private readonly StringBuilder _stderr = new();
    private readonly Queue<(long Sequence, string Text)> _stderrLines = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly ConcurrentDictionary<string, PendingPermission> _permissions = new();
    private readonly Dictionary<string, JsonObject> _toolInputs = new();
    private readonly Dictionary<string, string> _toolNames = new();
    private readonly HashSet<string> _announcedTools = new();
    private readonly Queue<JsonNode> _earlyMessages = new();
    private readonly List<string> _tempAttachments = new();
    private readonly SemaphoreSlim _turnGate = new(1, 1);
    private readonly object _usageProbeLock = new();
    private readonly object _grokUsageLock = new();
    private Process? _proc;
    private StreamWriter? _stdin;
    private int _requestSeq;
    private string? _model;
    private string? _effort;
    private string _permissionMode;
    private string? _requestedModel;
    private string? _requestedEffort;
    private string _requestedPermissionMode;
    private bool _basePromptPending;
    private bool _initialized;
    private bool _initializationFailed;
    private bool _disposed;
    private bool _streamStarted;
    private int _nextStreamIndex;
    private int? _activeStreamIndex;
    private string? _activeStreamBlock;
    private long _stderrSequence;
    private int _turnActivity;
    private StringBuilder? _usageProbeText;
    private JsonObject? _grokTurnUsage;
    private bool _grokTurnInFlight;
    private string ProviderName => _isGrok ? "Grok" : "Kimi";
    private string ProviderSlug => _isGrok ? "grok" : "kimi";

    public KimiSession(KimiSessionOptions options)
    {
        _ = options.UseGrokProtocol ? GrokSession.ResolveCliPath() : ResolveCliPath();
        _options = options;
        _isGrok = options.UseGrokProtocol;
        _requestedModel = string.IsNullOrWhiteSpace(options.Model)
                          || string.Equals(options.Model, "default", StringComparison.OrdinalIgnoreCase)
            ? null
            : options.Model;
        _model = _isGrok
            ? Grok45Preset.BackendModel(_requestedModel ?? Grok45Preset.NormalModelId)
            : _requestedModel;
        _effort = _requestedEffort = options.Effort;
        _permissionMode = _requestedPermissionMode = options.PermissionMode;
        _basePromptPending = !_isGrok && !string.IsNullOrWhiteSpace(options.AppendSystemPrompt);
        if (_isGrok) Commands = GrokBuiltInCommands();
    }

    /// <summary>Find either Kimi's recommended native executable or the npm shim.</summary>
    public static string ResolveCliPath()
    {
        foreach (var candidate in CliCandidates())
        {
            try { if (File.Exists(candidate)) return Path.GetFullPath(candidate); }
            catch { /* inaccessible or malformed PATH entry */ }
        }

        throw new FileNotFoundException(
            "Could not find Kimi Code CLI. Install the official Windows build with " +
            "`irm https://code.kimi.com/kimi-code/install.ps1 | iex`, then reopen the account menu. " +
            "Kimi also requires Git for Windows. You can point VibeCode at an existing runtime with VIBECODE_KIMI_PATH.");
    }

    /// <summary>Ordered CLI search paths, exposed internally for a no-process regression test.</summary>
    internal static IReadOnlyList<string> CliCandidates()
    {
        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable("VIBECODE_KIMI_PATH") is { Length: > 0 } configured)
            candidates.Add(configured.Trim('"'));

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var kimiHome = Environment.GetEnvironmentVariable("KIMI_CODE_HOME");
        if (string.IsNullOrWhiteSpace(kimiHome)) kimiHome = Path.Combine(profile, ".kimi-code");
        else kimiHome = kimiHome.Trim('"');

        // Kimi's native Windows installer puts the standalone executable here. Check it directly so an install
        // completed while VibeCode is open works even before the parent process receives an updated PATH.
        candidates.Add(Path.Combine(kimiHome, "bin", "kimi.exe"));
        candidates.Add(Path.Combine(kimiHome, "bin", "kimi"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "kimi.exe"));
        candidates.Add(Path.Combine(profile, ".local", "bin", "kimi.exe"));
        candidates.Add(Path.Combine(profile, "bin", "kimi.exe"));
        candidates.Add(Path.Combine(appData, "npm", "kimi.cmd"));
        candidates.Add(Path.Combine(appData, "npm", "kimi.exe"));
        candidates.Add(Path.Combine(local, "Microsoft", "WinGet", "Links", "kimi.exe"));

        // Include the process PATH plus current user/machine PATH. The latter two matter immediately after the
        // account manager runs Kimi's installer: Windows does not retroactively update this process's environment.
        var pathValues = new[]
        {
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine),
        };
        var extensions = new[] { ".exe", ".cmd", ".bat", ".ps1", "" };
        foreach (var value in pathValues)
        foreach (var dir in (value ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        foreach (var ext in extensions)
            candidates.Add(Path.Combine(dir.Trim('"'), "kimi" + ext));

        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>Create a redirected process for native, npm, PowerShell, or direct JS Kimi installs.</summary>
    public static ProcessStartInfo CreateCliStartInfo(string? workingDirectory, params string[] args)
    {
        var cli = ResolveCliPath();
        var extension = Path.GetExtension(cli).ToLowerInvariant();
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        if (!string.IsNullOrWhiteSpace(workingDirectory)) psi.WorkingDirectory = workingDirectory;
        // A Moonshot key also repoints the Anthropic-compatible base URL, since that is how Kimi
        // authenticates an Anthropic-shaped client.
        VibeCode.Services.ApiKeyAccountService.Instance.ApplyTo(psi, "kimi");

        if (extension == ".cmd" || extension == ".bat")
        {
            // npm's standard Windows shim is a .cmd file. Prefer its JS entry so JSON-RPC owns stdio directly;
            // retain cmd.exe as a compatibility fallback for other package-manager layouts.
            var npmEntry = Path.Combine(Path.GetDirectoryName(cli)!, "node_modules", "@moonshot-ai", "kimi-code", "dist", "main.mjs");
            if (File.Exists(npmEntry))
            {
                psi.FileName = "node.exe";
                psi.ArgumentList.Add(npmEntry);
                foreach (var arg in args) psi.ArgumentList.Add(arg);
            }
            else
            {
                psi.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
                psi.ArgumentList.Add("/d");
                psi.ArgumentList.Add("/s");
                psi.ArgumentList.Add("/c");
                psi.ArgumentList.Add(QuoteCmd(cli, args));
            }
        }
        else if (extension == ".ps1")
        {
            psi.FileName = "powershell.exe";
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(cli);
            foreach (var arg in args) psi.ArgumentList.Add(arg);
        }
        else if (extension is ".mjs" or ".js")
        {
            psi.FileName = "node.exe";
            psi.ArgumentList.Add(cli);
            foreach (var arg in args) psi.ArgumentList.Add(arg);
        }
        else
        {
            psi.FileName = cli;
            foreach (var arg in args) psi.ArgumentList.Add(arg);
        }
        return psi;
    }

    private static string QuoteCmd(string executable, IEnumerable<string> args)
    {
        static string Q(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
        return Q(executable) + " " + string.Join(" ", args.Select(Q));
    }

    public void Start()
    {
        var provider = _isGrok ? "grok" : "kimi";
        McpCatalog.EnsureLaunchReady(_options.McpServers, provider, _options.Cwd);
        var psi = _isGrok
            ? GrokSession.CreateCliStartInfoForAuth(_options.Cwd, _options.GrokAuthFilePath,
                "agent", "--no-leader", "stdio")
            : CreateCliStartInfo(_options.Cwd, "acp");
        _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _proc.Exited += (_, _) =>
        {
            foreach (var pending in _pending) pending.Value.TrySetCanceled();
            if (!_disposed) Exited?.Invoke(_proc!.ExitCode, StderrTail);
        };
        _proc.Start();
        ProcessJob.Assign(_proc);
        _stdin = _proc.StandardInput;
        _ = Task.Run(ReadStdoutLoop);
        _ = Task.Run(ReadStderrLoop);
        _ = Task.Run(InitializeAsync);
    }

    private string StderrTail { get { lock (_stderr) return _stderr.ToString(); } }

    private async Task InitializeAsync()
    {
        try
        {
            var initializeResult = await RequestAsync("initialize", new JsonObject
            {
                ["protocolVersion"] = 1,
                ["clientCapabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject
                {
                    ["name"] = "vibecode",
                    ["title"] = "VibeCode",
                    ["version"] = "1.0.0",
                },
            });
            if (_isGrok)
            {
                ApplyGrokModelState(initializeResult?["_meta"]?["modelState"] as JsonObject, notify: false);
                ApplyCommands(initializeResult?["_meta"]?["availableCommands"] as JsonArray);
            }

            JsonNode? sessionResult;
            var mcpServers = _isGrok
                ? McpCatalog.BuildGrokAcpServers(_options.McpServers, _options.Cwd)
                : McpCatalog.BuildKimiAcpServers(_options.McpServers, _options.Cwd);
            if (_options.Resume is not null && !_options.ForkSession)
            {
                SessionId = _options.Resume;
                var loadParams = new JsonObject
                {
                    ["sessionId"] = SessionId,
                    ["cwd"] = _options.Cwd,
                    ["mcpServers"] = mcpServers.DeepClone(),
                };
                if (_isGrok) loadParams["_meta"] = BuildGrokSessionMeta();
                sessionResult = await RequestAsync("session/load", loadParams);
                EndStream();
            }
            else
            {
                var newParams = new JsonObject
                {
                    ["cwd"] = _options.Cwd,
                    ["mcpServers"] = mcpServers.DeepClone(),
                };
                if (_isGrok) newParams["_meta"] = BuildGrokSessionMeta();
                sessionResult = await RequestAsync("session/new", newParams);
                SessionId = sessionResult?["sessionId"]?.GetValue<string>();
            }

            if (SessionId is null) throw new InvalidOperationException($"{ProviderName} ACP did not return a session id.");
            // Keep the user's requested values separate from the CLI's freshly reported defaults. Applying the
            // config first used to overwrite these fields, so a saved model (including a newly advertised K3
            // model), thinking choice, and permission mode were silently ignored on every new session.
            if (_isGrok)
                ApplyGrokModelState(sessionResult?["models"] as JsonObject, notify: false);
            else
                ApplyConfigOptions(sessionResult?["configOptions"] as JsonArray, notify: false);
            await ApplyInitialSelectionsAsync();
            EmitInit();
            if (_options.Resume is not null && !_options.ForkSession)
                Emit(new JsonObject { ["type"] = "system", ["subtype"] = "resume_boundary" });
            if (_options.ForkSession && _options.Resume is not null)
                Emit(new JsonObject
                {
                    ["type"] = "system", ["subtype"] = "permission_denied", ["tool_name"] = $"{ProviderName} fork",
                    ["message"] = $"{ProviderName} ACP did not fork this transcript; this is a new {ProviderName} session.",
                });

            _initialized = true;
            Initialized?.Invoke();

            List<JsonNode> queued;
            lock (_earlyMessages)
            {
                queued = _earlyMessages.Select(x => x.DeepClone()).ToList();
                _earlyMessages.Clear();
            }
            foreach (var content in queued) _ = PromptAsync(content);
        }
        catch (Exception ex)
        {
            _initializationFailed = true;
            var detail = FriendlyError(ex);
            EmitFailure($"{ProviderName} failed to initialize", detail);
            Initialized?.Invoke();
            Dispose(); // ACP remains idle after an init/auth failure; release the subprocess immediately.
        }
    }

    private async Task ApplyInitialSelectionsAsync()
    {
        if (SessionId is null) return;
        if (_isGrok)
        {
            await ApplyInitialGrokSelectionsAsync();
            return;
        }
        var requestedModel = ResolveBackendModel(_requestedModel);
        if (!string.IsNullOrWhiteSpace(requestedModel)
            && !string.Equals(requestedModel, "default", StringComparison.OrdinalIgnoreCase))
        {
            // Managed Kimi accounts return their model catalog dynamically. A model saved under an older account or
            // CLI release must not make all future chats fail; use the current CLI default when it is no longer listed.
            var advertised = Models.OfType<JsonObject>()
                .Select(x => x["value"]?.GetValue<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x) && x != "default")
                .OfType<string>()
                .ToArray();
            if (advertised.Length == 0 || advertised.Contains(requestedModel, StringComparer.Ordinal))
            {
                if (await TrySetInitialConfigOptionAsync("model", requestedModel))
                {
                    _model = requestedModel;
                    foreach (var row in Models.OfType<JsonObject>())
                        row["isDefault"] = string.Equals(row["value"]?.GetValue<string>(), requestedModel,
                            StringComparison.Ordinal);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(_requestedEffort))
        {
            var thinking = ThinkingValue(_requestedEffort);
            if (await TrySetInitialConfigOptionAsync("thinking", thinking)) _effort = thinking;
        }

        var requestedMode = _requestedPermissionMode;
        var mode = ToAcpMode(requestedMode);
        if (mode == "default" || await TrySetInitialConfigOptionAsync("mode", mode))
            _permissionMode = requestedMode;
    }

    private JsonObject BuildGrokSessionMeta()
    {
        var prompt = Grok45Preset.SessionPrompt(_requestedModel, _options.AppendSystemPrompt);
        var meta = new JsonObject
        {
            ["clientIdentifier"] = "vibecode",
            ["modelId"] = Grok45Preset.BackendModel(_requestedModel ?? Grok45Preset.NormalModelId),
            ["yoloMode"] = _requestedPermissionMode == "bypassPermissions",
            ["autoMode"] = _requestedPermissionMode == "dontAsk",
        };
        if (!string.IsNullOrWhiteSpace(prompt.SystemPromptOverride))
            meta["systemPromptOverride"] = prompt.SystemPromptOverride;
        if (!string.IsNullOrWhiteSpace(prompt.Rules)) meta["rules"] = prompt.Rules;
        return meta;
    }

    private async Task ApplyInitialGrokSelectionsAsync()
    {
        if (SessionId is null) return;
        var backend = Grok45Preset.BackendModel(_requestedModel ?? Grok45Preset.NormalModelId);
        if (!string.IsNullOrWhiteSpace(backend))
            await SafeSetGrokModelAsync(backend, _requestedEffort);
        if (ToAcpMode(_requestedPermissionMode) == "plan")
            await SafeSetGrokModeAsync("plan");
        _permissionMode = _requestedPermissionMode;
    }

    private async Task<bool> TrySetInitialConfigOptionAsync(string id, string value)
    {
        try
        {
            await SetConfigOptionAsync(id, value);
            return true;
        }
        catch (KimiRpcException ex)
        {
            // Configuration choices are preferences, not a reason to lose access to the conversation. This also
            // handles catalogs changing as Kimi rolls out or retires models for an account.
            Debug.WriteLine($"vibecode: Kimi ignored unavailable initial {id}={value}: {ex.Message}");
            return false;
        }
    }

    private void EmitInit()
    {
        var selectedModel = CurrentModelId() ?? _model ?? "default";
        var publicModel = _isGrok && Grok45Preset.IsGrok45(selectedModel)
            ? Grok45Preset.NormalModelId
            : selectedModel;
        Emit(new JsonObject
        {
            ["type"] = "system",
            ["subtype"] = "init",
            ["session_id"] = SessionId,
            ["model"] = publicModel,
            ["permissionMode"] = _permissionMode,
        });
    }

    public void SendUser(JsonNode content)
    {
        if (_disposed || _initializationFailed) return;
        if (!_initialized || SessionId is null)
        {
            lock (_earlyMessages) _earlyMessages.Enqueue(content.DeepClone());
            return;
        }
        _ = PromptAsync(content.DeepClone());
    }

    private async Task PromptAsync(JsonNode content)
    {
        await _turnGate.WaitAsync();
        var started = Stopwatch.StartNew();
        try
        {
            if (SessionId is null || _disposed) return;
            if (_isGrok) BeginGrokUsageCapture();
            var stderrMarker = StderrMarker;
            Interlocked.Exchange(ref _turnActivity, 0);
            BeginStream();
            var response = await RequestAsync("session/prompt", new JsonObject
            {
                ["sessionId"] = SessionId,
                ["prompt"] = BuildPrompt(content),
            });

            // Kimi Code 0.27.x logs non-auth provider failures (including provider.rate_limit) to stderr, but
            // resolves the ACP request as a clean, empty end_turn. Give the stderr reader a brief chance to consume
            // that terminal log line before deciding this was a successful turn. Without this bridge, exhausted
            // accounts look exactly like the agent ignored the user's message.
            if (!_isGrok) await Task.Delay(75);
            var hiddenFailure = _isGrok ? null : FailureFromDiagnostics(StderrSince(stderrMarker));
            EndStream();
            if (hiddenFailure is not null)
            {
                EmitFailure(hiddenFailure.Title, hiddenFailure.Detail, started.Elapsed.TotalMilliseconds,
                    hiddenFailure.Subtype);
                return;
            }

            var stopReason = response?["stopReason"]?.GetValue<string>() ?? "end_turn";
            if (stopReason is not ("end_turn" or "cancelled"))
            {
                EmitFailure($"{ProviderName} stopped without an answer",
                    $"{ProviderName} ended the turn with `{stopReason}` before returning a response.",
                    started.Elapsed.TotalMilliseconds, stopReason == "refusal" ? "refusal" : "error");
                return;
            }
            if (stopReason == "end_turn" && Volatile.Read(ref _turnActivity) == 0)
            {
                EmitFailure($"{ProviderName} returned no response",
                    _isGrok
                        ? "Grok completed the turn without assistant output. Check the Grok account and model, then retry."
                        : "Kimi Code completed the turn without any assistant output. Its ACP mode can hide provider and " +
                          "quota failures this way. Run `/usage` in this chat or check the Kimi Code Console, then retry.",
                    started.Elapsed.TotalMilliseconds, "empty_response");
                return;
            }

            JsonObject? usageReport = null;
            if (_isGrok)
                usageReport = MergeGrokUsageReports(UsageFromGrokResponse(response), EndGrokUsageCapture());
            else if (stopReason == "end_turn")
                usageReport = await QueryUsageAsync();
            var result = new JsonObject
            {
                ["type"] = "result",
                ["subtype"] = stopReason == "cancelled" ? "interrupted" : "success",
                ["is_error"] = false,
                ["result"] = "",
                ["duration_ms"] = started.Elapsed.TotalMilliseconds,
                ["usage"] = usageReport?["usage"]?.DeepClone() ?? new JsonObject(),
            };
            if (usageReport?["session_usage"] is { } sessionUsage)
                result["session_usage"] = sessionUsage.DeepClone();
            if (usageReport?["session_model_usage"] is { } modelUsage)
                result["session_model_usage"] = modelUsage.DeepClone();
            if (usageReport?["modelUsage"] is { } turnModelUsage)
                result["modelUsage"] = turnModelUsage.DeepClone();
            if (usageReport?["num_turns"] is { } numTurns)
                result["num_turns"] = numTurns.DeepClone();
            if (usageReport?["usage_is_incomplete"] is { } incomplete)
                result["usage_is_incomplete"] = incomplete.DeepClone();
            if (usageReport?["total_cost_usd"] is { } totalCost)
                result["total_cost_usd"] = totalCost.DeepClone();
            Emit(result);
        }
        catch (Exception ex)
        {
            EndStream();
            EmitFailure($"{ProviderName} turn failed", FriendlyError(ex), started.Elapsed.TotalMilliseconds);
        }
        finally
        {
            if (_isGrok) EndGrokUsageCapture();
            _turnGate.Release();
        }
    }

    private JsonArray BuildPrompt(JsonNode content)
    {
        var prompt = new JsonArray();
        if (content is JsonValue scalar)
        {
            AddText(scalar.GetValue<string>());
            return prompt;
        }

        if (content is JsonArray blocks)
        {
            foreach (var block in blocks.OfType<JsonObject>())
            {
                switch (block["type"]?.GetValue<string>())
                {
                    case "text": AddText(block["text"]?.GetValue<string>() ?? ""); break;
                    case "image":
                    {
                        var source = block["source"] as JsonObject;
                        if (source?["data"]?.GetValue<string>() is { Length: > 0 } data)
                            prompt.Add(new JsonObject
                            {
                                ["type"] = "image",
                                ["mimeType"] = source["media_type"]?.GetValue<string>() ?? "image/png",
                                ["data"] = data,
                            });
                        break;
                    }
                    case "document":
                    {
                        var source = block["source"] as JsonObject;
                        if (source?["data"]?.GetValue<string>() is { Length: > 0 } data)
                        {
                            var media = source["media_type"]?.GetValue<string>() ?? "application/pdf";
                            var path = SaveAttachment(Convert.FromBase64String(data), ExtensionFor(media));
                            AddText($"A document is attached at `{path}`. Read it as part of this request.");
                        }
                        break;
                    }
                }
            }
        }

        if (_basePromptPending)
            AddText("");
        return prompt;

        void AddText(string text)
        {
            if (_basePromptPending)
            {
                _basePromptPending = false;
                text = SystemContextOpen + "\n" + _options.AppendSystemPrompt! + "\n" + SystemContextClose
                       + (text.Length == 0 ? "" : "\n\n" + text);
            }
            if (text.Length > 0) prompt.Add(new JsonObject { ["type"] = "text", ["text"] = text });
        }
    }

    private string SaveAttachment(byte[] data, string extension)
    {
        var dir = Path.Combine(Path.GetTempPath(), "VibeCode", ProviderSlug + "-attachments");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, Guid.NewGuid().ToString("N") + extension);
        File.WriteAllBytes(path, data);
        _tempAttachments.Add(path);
        return path;
    }

    private static string ExtensionFor(string media) => media.ToLowerInvariant() switch
    {
        "application/pdf" => ".pdf",
        "image/jpeg" => ".jpg",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        "text/plain" => ".txt",
        _ => ".bin",
    };

    private async Task ReadStdoutLoop()
    {
        try
        {
            string? line;
            while ((line = await _proc!.StandardOutput.ReadLineAsync()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try { HandleLine(JsonNode.Parse(line) as JsonObject); }
                catch (Exception ex) { Debug.WriteLine($"vibecode: bad {ProviderName} ACP line: {ex.Message}"); }
            }
        }
        catch (Exception ex) { Debug.WriteLine($"vibecode: {ProviderName} ACP stdout ended: {ex.Message}"); }
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
                    _stderrLines.Enqueue((++_stderrSequence, line));
                    while (_stderrLines.Count > 240) _stderrLines.Dequeue();
                }
            }
        }
        catch { /* process gone */ }
    }

    private long StderrMarker
    {
        get { lock (_stderr) return _stderrSequence; }
    }

    private IReadOnlyList<string> StderrSince(long marker)
    {
        lock (_stderr)
            return _stderrLines.Where(x => x.Sequence > marker).Select(x => x.Text).ToArray();
    }

    private static TurnFailure? FailureFromDiagnostics(IReadOnlyList<string> lines)
    {
        var terminalIndex = -1;
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            if (!lines[i].Contains("acp: turn ended with failed reason", StringComparison.OrdinalIgnoreCase))
                continue;
            terminalIndex = i;
            break;
        }
        if (terminalIndex < 0) return null;

        // Structured loggers may render the attached error object on following lines, so retain a small bounded
        // window. Never send an entire diagnostic log (or stack trace) through the chat UI.
        var diagnostic = string.Join(" ", lines.Skip(terminalIndex).Take(8));
        var lower = diagnostic.ToLowerInvariant();
        if (lower.Contains("engine is currently overloaded") || lower.Contains("engine_overloaded"))
            return new TurnFailure("Kimi is temporarily overloaded",
                "Kimi's inference service is busy. Wait a moment and retry this turn.", "rate_limit");
        if (lower.Contains("too many concurrent") || lower.Contains("too many requests"))
            return new TurnFailure("Kimi rate limit reached",
                "Too many Kimi requests are active right now. Wait a moment, then retry.", "rate_limit");
        if (lower.Contains("monthly usage limit"))
            return new TurnFailure("Kimi monthly usage limit reached",
                "The account's shared monthly Kimi quota is exhausted. Check `/usage` or the Kimi Code Console " +
                "for its reset or Extra Usage options.", "rate_limit");
        if (lower.Contains("usage limit") || lower.Contains("billing cycle") || lower.Contains("quota"))
            return new TurnFailure("Kimi usage limit reached",
                "The account's Kimi quota is exhausted for the current window or billing cycle. Check `/usage` " +
                "or the Kimi Code Console for the reset time.", "rate_limit");
        if (lower.Contains("provider.rate_limit") || lower.Contains("rate_limit") ||
            lower.Contains("rate limit") || Regex.IsMatch(lower, @"(?:status(?: code)?\D{0,6}|http\s*)429\b"))
            return new TurnFailure("Kimi rate limit reached",
                "Kimi rejected the request because of a provider rate limit. Check `/usage` for account limits; " +
                "if quota remains, wait a moment and retry.", "rate_limit");

        return new TurnFailure("Kimi provider request failed",
            "Kimi Code reported a provider failure but did not expose its details through ACP. Retry the turn; " +
            "if it repeats, run `/usage` and `kimi doctor` for the account and CLI status.", "provider_error");
    }

    /// <summary>
    /// Ask Kimi's built-in local command for its authoritative counters. The command is handled inside Kimi Code,
    /// so it does not make another model request or consume quota. ACP exposes the report as ordinary assistant
    /// chunks; while this probe is active those chunks are captured instead of appearing as a second chat reply.
    /// </summary>
    private async Task<JsonObject?> QueryUsageAsync()
    {
        if (SessionId is null || _disposed) return null;
        lock (_usageProbeLock) _usageProbeText = new StringBuilder();
        try
        {
            await RequestAsync("session/prompt", new JsonObject
            {
                ["sessionId"] = SessionId,
                ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "/usage" }),
            });
            string report;
            lock (_usageProbeLock) report = _usageProbeText?.ToString() ?? "";
            return ParseUsageReport(report);
        }
        catch (Exception ex)
        {
            // Usage is supplemental. Never turn an otherwise successful answer into an error if an older or
            // customized Kimi ACP runtime does not implement the local command.
            Debug.WriteLine($"vibecode: Kimi usage probe failed: {ex.Message}");
            return null;
        }
        finally
        {
            lock (_usageProbeLock) _usageProbeText = null;
        }
    }

    /// <summary>Translate Kimi's human-readable /usage report into the shared Claude-shaped usage envelope.</summary>
    internal static JsonObject? ParseUsageReport(string report)
    {
        JsonObject? total = null;
        JsonObject? currentTurn = null;
        var byModel = new JsonObject();
        long contextUsed = 0;
        long contextWindow = 0;

        foreach (var line in report.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var context = ContextLinePattern.Match(line);
            if (context.Success)
            {
                contextUsed = TokenNumber(context.Groups["used"].Value);
                contextWindow = TokenNumber(context.Groups["window"].Value);
                continue;
            }

            var usage = UsageLinePattern.Match(line);
            if (!usage.Success) continue;
            var bucket = UsageBucket(
                TokenNumber(usage.Groups["input"].Value),
                TokenNumber(usage.Groups["output"].Value),
                TokenNumber(usage.Groups["read"].Value),
                TokenNumber(usage.Groups["write"].Value));
            var label = usage.Groups["label"].Value.Trim();
            if (label.Equals("Total", StringComparison.OrdinalIgnoreCase)) total = bucket;
            else if (label.Equals("Current turn", StringComparison.OrdinalIgnoreCase)) currentTurn = bucket;
            else byModel[label] = bucket;
        }

        if (total is null && currentTurn is null && byModel.Count == 0) return null;
        currentTurn ??= new JsonObject();
        if (contextUsed > 0) currentTurn["context_input_tokens"] = contextUsed;
        if (contextWindow > 0) currentTurn["context_window"] = contextWindow;
        return new JsonObject
        {
            ["usage"] = currentTurn,
            ["session_usage"] = total ?? new JsonObject(),
            ["session_model_usage"] = byModel,
        };
    }

    private static JsonObject UsageBucket(long input, long output, long cacheRead, long cacheWrite) => new()
    {
        ["input_tokens"] = input,
        ["output_tokens"] = output,
        ["cache_read_input_tokens"] = cacheRead,
        ["cache_creation_input_tokens"] = cacheWrite,
    };

    /// <summary>Map Grok's documented prompt metadata or rich turn-completed update to the shared usage envelope.</summary>
    internal static JsonObject? UsageFromGrokResponse(JsonNode? response)
    {
        if (response is not JsonObject payload) return null;

        // The standard ACP response uses `_meta.usage`; the bundled runtime's authoritative persisted/live event
        // uses `_x.ai/session/update -> turn_completed -> usage`. Accept both plus the early flat fixture shape.
        // ACP inputTokens is the FULL prompt count (cache included); the shared UI expects disjoint buckets.
        var meta = payload["_meta"] as JsonObject ?? payload;
        var raw = meta["usage"] as JsonObject ?? meta["promptUsage"] as JsonObject ?? meta;
        var fullInput = FirstJsonLong(raw, "inputTokens", "input_tokens");
        var output = FirstJsonLong(raw, "outputTokens", "output_tokens");
        var cacheRead = FirstJsonLong(raw, "cachedReadTokens", "cacheReadInputTokens",
            "cache_read_input_tokens");
        var cacheWrite = FirstJsonLong(raw, "cachedWriteTokens", "cacheWriteInputTokens",
            "cache_creation_input_tokens");
        var total = FirstJsonLong(raw, "totalTokens", "total_tokens");
        var usesCamelPromptShape = raw["inputTokens"] is not null;
        var input = usesCamelPromptShape
            ? Math.Max(0, fullInput - cacheRead - cacheWrite)
            : fullInput;
        if (input + cacheRead + cacheWrite + output == 0 && total > 0) input = total;
        if (input + cacheRead + cacheWrite + output == 0) return null;

        var usage = UsageBucket(input, output, cacheRead, cacheWrite);
        if (total > 0) usage["total_tokens"] = total;
        if (FirstJsonLong(raw, "thoughtTokens", "reasoningTokens", "reasoning_tokens") is var reasoning
            && reasoning > 0)
            usage["reasoning_tokens"] = reasoning;

        // PromptUsage is an aggregate across the model rounds in this prompt, not the latest context snapshot.
        // The separate ACP usage_update above owns context occupancy; mark this so ChatViewModel never mistakes
        // the aggregate prompt sum for the amount currently in the model's context window.
        usage["_vibecode_aggregate_prompt"] = true;

        var report = new JsonObject { ["usage"] = usage };
        if ((meta["modelUsage"] ?? raw["modelUsage"]) is { } modelUsage)
            report["modelUsage"] = modelUsage.DeepClone();
        if ((meta["numTurns"] ?? raw["numTurns"]) is { } turns)
            report["num_turns"] = turns.DeepClone();
        if ((meta["usageIsIncomplete"] ?? raw["usageIsIncomplete"]) is { } incomplete)
            report["usage_is_incomplete"] = incomplete.DeepClone();
        if (GrokCostUsd(meta, raw) is { } cost) report["total_cost_usd"] = cost;
        return report;
    }

    private void BeginGrokUsageCapture()
    {
        lock (_grokUsageLock)
        {
            _grokTurnUsage = null;
            _grokTurnInFlight = true;
        }
    }

    private void CaptureGrokTurnUsage(JsonObject update)
    {
        var report = UsageFromGrokResponse(update);
        if (report is null) return;
        lock (_grokUsageLock)
        {
            if (_grokTurnInFlight) _grokTurnUsage = MergeGrokUsageReports(report, _grokTurnUsage);
        }
    }

    private JsonObject? EndGrokUsageCapture()
    {
        lock (_grokUsageLock)
        {
            _grokTurnInFlight = false;
            var report = _grokTurnUsage;
            _grokTurnUsage = null;
            return report;
        }
    }

    private static JsonObject? MergeGrokUsageReports(JsonObject? primary, JsonObject? fallback)
    {
        if (primary is null) return fallback?.DeepClone().AsObject();
        if (fallback is null) return primary.DeepClone().AsObject();
        var merged = fallback.DeepClone().AsObject();
        foreach (var (key, value) in primary) merged[key] = value?.DeepClone();
        return merged;
    }

    private static long FirstJsonLong(JsonObject source, params string[] keys)
    {
        foreach (var key in keys)
            if (source[key] is { } node && JsonLong(node) is var value && value != 0) return value;
        return 0;
    }

    private static double? GrokCostUsd(JsonObject meta, JsonObject usage)
    {
        if (JsonTrue(meta["costIsPartial"] ?? usage["costIsPartial"])
            || JsonTrue(meta["usageIsIncomplete"] ?? usage["usageIsIncomplete"])) return null;

        foreach (var node in new[] { meta["totalCostUsd"], meta["total_cost_usd"], usage["totalCostUsd"] })
            if (JsonDouble(node) is { } direct && direct > 0) return direct;
        foreach (var node in new[] { meta["totalCostUsdTicks"], meta["total_cost_usd_ticks"], usage["costUsdTicks"] })
            if (JsonLong(node) is var ticks && ticks > 0) return ticks / 10_000_000_000d;

        if ((meta["modelUsage"] ?? usage["modelUsage"]) is not JsonObject byModel) return null;
        long tickTotal = 0;
        double usdTotal = 0;
        var sawTicks = false;
        var sawUsd = false;
        foreach (var row in byModel.Select(x => x.Value).OfType<JsonObject>())
        {
            if (JsonTrue(row["costIsPartial"])) return null;
            var ticks = FirstJsonLong(row, "costUsdTicks", "cost_usd_ticks");
            if (ticks > 0) { tickTotal += ticks; sawTicks = true; }
            if (JsonDouble(row["costUSD"] ?? row["costUsd"]) is { } usd && usd > 0)
            { usdTotal += usd; sawUsd = true; }
        }
        return sawTicks ? tickTotal / 10_000_000_000d : sawUsd ? usdTotal : null;
    }

    private static bool JsonTrue(JsonNode? node)
    {
        try { return node?.GetValue<bool>() == true; } catch { return false; }
    }

    private static double? JsonDouble(JsonNode? node)
    {
        if (node is null) return null;
        try { return node.GetValue<double>(); } catch { /* integer */ }
        try { return node.GetValue<long>(); } catch { return null; }
    }

    private static long JsonLong(JsonNode? node)
    {
        if (node is null) return 0;
        try { return node.GetValue<long>(); } catch { /* narrower or floating number */ }
        try { return node.GetValue<int>(); } catch { /* floating number */ }
        try { return checked((long)Math.Round(node.GetValue<double>())); } catch { return 0; }
    }

    private static long TokenNumber(string value)
    {
        var digits = new string(value.Where(char.IsDigit).ToArray());
        return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }

    private void HandleLine(JsonObject? message)
    {
        if (message is null) return;
        var method = message["method"]?.GetValue<string>();
        if (method is not null)
        {
            var p = message["params"] as JsonObject;
            if (message["id"] is { } requestId) HandleServerRequest(method, p, requestId);
            else HandleNotification(method, p);
            return;
        }

        if (message["id"] is not { } id) return;
        if (!_pending.TryRemove(RpcKey(id), out var pending)) return;
        if (message["error"] is JsonObject error)
        {
            var code = JsonInt(error["code"]);
            pending.TrySetException(new KimiRpcException(code,
                error["message"]?.GetValue<string>() ?? error.ToJsonString(), error["data"]?.DeepClone()));
        }
        else pending.TrySetResult(message["result"]?.DeepClone());
    }

    private void HandleNotification(string method, JsonObject? p)
    {
        NotificationReceived?.Invoke(method, p?.DeepClone().AsObject());
        if (_isGrok && method == "_x.ai/models/update")
        {
            ApplyGrokModelState(p, notify: _initialized);
            return;
        }
        if (_isGrok && (method is "x.ai/session_notification" or "_x.ai/session/update")
                    && p?["update"] is JsonObject grokUpdate)
        {
            switch (grokUpdate["sessionUpdate"]?.GetValue<string>())
            {
                case "model_changed":
                    ApplyGrokModelChanged(grokUpdate);
                    return;
                case "turn_completed":
                    CaptureGrokTurnUsage(grokUpdate);
                    return;
            }
        }
        if (method == "session/update" && p?["update"] is JsonObject update)
            TranslateSessionUpdate(update);
    }

    private void TranslateSessionUpdate(JsonObject update)
    {
        lock (_usageProbeLock)
        {
            if (_usageProbeText is not null)
            {
                if (update["sessionUpdate"]?.GetValue<string>() == "agent_message_chunk")
                    _usageProbeText.Append(ContentText(update["content"]));
                // Suppress the probe's user echo and assistant report, plus any incidental status updates.
                return;
            }
        }
        switch (update["sessionUpdate"]?.GetValue<string>())
        {
            case "agent_message_chunk":
                StreamChunk("text", ContentText(update["content"]));
                break;
            case "agent_thought_chunk":
                StreamChunk("thinking", ContentText(update["content"]));
                break;
            case "user_message_chunk":
            {
                EndStream();
                var text = StripSystemContext(ContentText(update["content"]));
                if (text.Length > 0) Emit(new JsonObject
                {
                    ["type"] = "user",
                    ["message"] = new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
                    },
                });
                break;
            }
            case "tool_call":
                CaptureTool(update, isUpdate: false);
                break;
            case "tool_call_update":
                CaptureTool(update, isUpdate: true);
                break;
            case "plan":
                EmitPlan(update["entries"] as JsonArray);
                break;
            case "available_commands_update":
                ApplyCommands(update["availableCommands"] as JsonArray);
                if (_initialized) Initialized?.Invoke();
                break;
            case "config_option_update":
                if (!_isGrok) ApplyConfigOptions(update["configOptions"] as JsonArray, notify: _initialized);
                break;
            case "usage_update" when _isGrok:
            {
                // Grok publishes context occupancy as ACP's UsageUpdate { used, size }. This is a context
                // snapshot, not an OpenAI/Claude account quota and not a turn-token delta. Project only those
                // two values so the active Grok chat gets an accurate context bar without touching any other
                // provider's account service or token counters.
                var hasUsed = update["used"] is not null || update["usedTokens"] is not null;
                var hasSize = update["size"] is not null || update["contextWindow"] is not null;
                var used = FirstJsonLong(update, "used", "usedTokens");
                var size = FirstJsonLong(update, "size", "contextWindow");
                if (hasUsed || hasSize)
                {
                    var context = new JsonObject();
                    // Preserve an explicit zero: ACP uses it to clear the previous context snapshot.
                    if (hasUsed) context["context_input_tokens"] = Math.Max(0, used);
                    if (size > 0) context["context_window"] = size;
                    Emit(new JsonObject
                    {
                        ["type"] = "system",
                        ["subtype"] = "usage_update",
                        ["usage"] = context,
                    });
                }
                break;
            }
        }
    }

    private static string StripSystemContext(string text)
    {
        if (!text.StartsWith(SystemContextOpen, StringComparison.Ordinal)) return text;
        var end = text.IndexOf(SystemContextClose, StringComparison.Ordinal);
        if (end < 0) return "";
        return text[(end + SystemContextClose.Length)..].TrimStart('\r', '\n');
    }

    private void BeginStream()
    {
        if (_streamStarted) return;
        _streamStarted = true;
        _nextStreamIndex = 0;
        _activeStreamIndex = null;
        _activeStreamBlock = null;
        EmitStreamEvent(new JsonObject { ["type"] = "message_start" });
    }

    private void StreamChunk(string blockType, string text)
    {
        if (text.Length == 0) return;
        Interlocked.Exchange(ref _turnActivity, 1);
        BeginStream();
        if (_activeStreamBlock != blockType)
        {
            StopActiveStreamBlock();
            _activeStreamIndex = _nextStreamIndex++;
            _activeStreamBlock = blockType;
            EmitStreamEvent(new JsonObject
            {
                ["type"] = "content_block_start",
                ["index"] = _activeStreamIndex.Value,
                ["content_block"] = new JsonObject
                {
                    ["type"] = blockType,
                    [blockType] = "",
                },
            });
        }
        EmitStreamEvent(new JsonObject
        {
            ["type"] = "content_block_delta",
            ["index"] = _activeStreamIndex!.Value,
            ["delta"] = new JsonObject
            {
                ["type"] = blockType == "thinking" ? "thinking_delta" : "text_delta",
                [blockType == "thinking" ? "thinking" : "text"] = text,
            },
        });
    }

    private void StopActiveStreamBlock()
    {
        if (_activeStreamIndex is not { } index) return;
        EmitStreamEvent(new JsonObject { ["type"] = "content_block_stop", ["index"] = index });
        _activeStreamIndex = null;
        _activeStreamBlock = null;
    }

    private void EndStream()
    {
        if (!_streamStarted) return;
        StopActiveStreamBlock();
        EmitStreamEvent(new JsonObject { ["type"] = "message_stop" });
        _streamStarted = false;
    }

    private void EmitStreamEvent(JsonObject ev) => Emit(new JsonObject
    {
        ["type"] = "stream_event",
        ["event"] = ev,
    });

    private void CaptureTool(JsonObject update, bool isUpdate)
    {
        var id = update["toolCallId"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(id)) return;
        Interlocked.Exchange(ref _turnActivity, 1);
        StopActiveStreamBlock();

        var previousInput = _toolInputs.TryGetValue(id, out var prior) ? prior : null;
        var input = BuildToolInput(update, previousInput);
        var title = update["title"]?.GetValue<string>();
        var kind = update["kind"]?.GetValue<string>();
        var name = NormalizeToolName(title, kind, input);
        _toolInputs[id] = input;
        _toolNames[id] = name;

        var status = update["status"]?.GetValue<string>();
        var pendingOnly = !isUpdate && status == "pending" && update["rawInput"] is null;
        if (!pendingOnly && (!_announcedTools.Contains(id) || isUpdate && update["rawInput"] is not null))
        {
            Emit(new JsonObject
            {
                ["type"] = "assistant",
                ["message"] = new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = id,
                        ["name"] = name,
                        ["input"] = input.DeepClone(),
                    }),
                },
            });
            _announcedTools.Add(id);
        }

        if (status is not ("completed" or "failed")) return;
        if (!_announcedTools.Contains(id))
        {
            Emit(new JsonObject
            {
                ["type"] = "assistant",
                ["message"] = new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "tool_use", ["id"] = id, ["name"] = name, ["input"] = input.DeepClone(),
                    }),
                },
            });
            _announcedTools.Add(id);
        }
        var resultText = update["rawOutput"] is { } raw ? NodeText(raw) : ToolContentText(update["content"] as JsonArray);
        Emit(new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "tool_result",
                    ["tool_use_id"] = id,
                    ["content"] = resultText,
                    ["is_error"] = status == "failed",
                }),
            },
        });
        _toolInputs.Remove(id);
        _toolNames.Remove(id);
    }

    private static JsonObject BuildToolInput(JsonObject update, JsonObject? previous)
    {
        JsonObject input;
        if (update["rawInput"] is JsonObject rawObject) input = rawObject.DeepClone().AsObject();
        else if (update["rawInput"] is { } raw) input = new JsonObject { ["input"] = raw.DeepClone() };
        else input = previous?.DeepClone().AsObject() ?? TryParseObject(ToolContentText(update["content"] as JsonArray)) ?? new JsonObject();

        if (update["content"] is JsonArray content)
        foreach (var item in content.OfType<JsonObject>())
        {
            if (item["type"]?.GetValue<string>() != "diff") continue;
            input["file_path"] = item["path"]?.GetValue<string>();
            input["old_string"] = item["oldText"]?.GetValue<string>() ?? "";
            input["new_string"] = item["newText"]?.GetValue<string>() ?? "";
            break;
        }
        return input;
    }

    private static JsonObject? TryParseObject(string text)
    {
        try { return JsonNode.Parse(text) as JsonObject; }
        catch { return null; }
    }

    internal static string NormalizeToolName(string? title, string? kind, JsonObject input)
    {
        if (string.Equals(title, "AskUserQuestion", StringComparison.OrdinalIgnoreCase)) return "AskUserQuestion";
        if (string.Equals(title, "ExitPlanMode", StringComparison.OrdinalIgnoreCase)) return "ExitPlanMode";

        // ACP only standardizes broad tool kinds. Grok's native runtime currently reports several file tools as
        // kind="other" and puts their stable identifiers in title (read_file, grep, search_replace, ...). Translate
        // those identifiers into the same names Claude/Codex use so summaries, icons, edit diffs, and compact
        // stacking all travel through one presentation path instead of producing a wall of generic gear rows.
        var titleKey = string.Concat((title ?? "").Trim().ToLowerInvariant()
            .Select(ch => ch == '-' || char.IsWhiteSpace(ch) ? '_' : ch));
        var canonicalTitle = titleKey switch
        {
            "bash" or "shell" or "run_command" or "execute_command" => "Bash",
            "powershell" => "PowerShell",
            "read" or "read_file" => "Read",
            "write" or "write_file" or "create_file" => "Write",
            "edit" or "edit_file" or "search_replace" or "apply_patch" or "patch" => "Edit",
            "multi_edit" or "multiedit" => "MultiEdit",
            "notebook_edit" => "NotebookEdit",
            "grep" or "grep_files" or "search_files" => "Grep",
            "glob" => "Glob",
            "list_dir" or "list_directory" or "ls" => "LS",
            "web_search" or "search_web" => "WebSearch",
            "web_fetch" or "fetch_url" => "WebFetch",
            "todo_write" or "todowrite" => "TodoWrite",
            // Grok Build task tool — keep stable name so ChatViewModel Subagents roster can attach
            "spawn_subagent" or "spawn_agent" or "spawnsubagent" => "spawn_subagent",
            "get_command_or_subagent_output" or "get_subagent_output" or "get_command_output" => "get_command_or_subagent_output",
            "skill" => "Skill",
            "think" => "Think",
            "ask_user_question" => "AskUserQuestion",
            "exit_plan_mode" => "ExitPlanMode",
            _ => null,
        };
        if (canonicalTitle is not null) return canonicalTitle;

        return kind?.ToLowerInvariant() switch
        {
            "execute" => "Bash",
            "edit" => input["old_string"] is not null ? "Edit" : "Write",
            "read" => "Read",
            "fetch" => "WebFetch",
            "think" => "Think",
            _ => string.IsNullOrWhiteSpace(title) ? "KimiTool" : title!,
        };
    }

    private void EmitPlan(JsonArray? entries)
    {
        if (entries is null) return;
        var todos = new JsonArray();
        foreach (var entry in entries.OfType<JsonObject>())
            todos.Add(new JsonObject
            {
                ["content"] = entry["content"]?.GetValue<string>() ?? "",
                ["status"] = entry["status"]?.GetValue<string>() ?? "pending",
            });
        if (todos.Count > 0) Interlocked.Exchange(ref _turnActivity, 1);
        Emit(new JsonObject { ["type"] = "system", ["subtype"] = "todo_update", ["todos"] = todos });
    }

    private void ApplyCommands(JsonArray? commands)
    {
        if (commands is null || commands.Count == 0) return;
        var result = new JsonArray();
        foreach (var command in commands.OfType<JsonObject>())
            result.Add(new JsonObject
            {
                ["name"] = command["name"]?.GetValue<string>() ?? "",
                ["description"] = command["description"]?.GetValue<string>(),
                ["argumentHint"] = command["input"]?["hint"]?.GetValue<string>(),
            });
        if (result.Count > 0) Commands = result;
    }

    private void ApplyConfigOptions(JsonArray? options, bool notify)
    {
        if (options is null) return;
        Models = ModelsFromConfig(options);
        var model = options.OfType<JsonObject>().FirstOrDefault(x => x["id"]?.GetValue<string>() == "model");
        var providerModel = model?["currentValue"]?.GetValue<string>();
        if (_initialized && providerModel is not null) _requestedModel = providerModel;
        _model = providerModel ?? _model;
        var thinking = options.OfType<JsonObject>().FirstOrDefault(x => x["id"]?.GetValue<string>() == "thinking");
        if (thinking?["currentValue"]?.GetValue<string>() is { } effort) _effort = effort;
        var mode = options.OfType<JsonObject>().FirstOrDefault(x => x["id"]?.GetValue<string>() == "mode")?["currentValue"]?.GetValue<string>();
        if (mode is not null) _permissionMode = FromAcpMode(mode);
        if (!notify) return;
        EmitInit();
        Initialized?.Invoke();
    }

    internal static JsonArray ModelsFromConfig(JsonArray options)
    {
        var result = new JsonArray();
        var modelOption = options.OfType<JsonObject>().FirstOrDefault(x => x["id"]?.GetValue<string>() == "model");
        var current = modelOption?["currentValue"]?.GetValue<string>();
        var thinking = options.OfType<JsonObject>().FirstOrDefault(x => x["id"]?.GetValue<string>() == "thinking");
        var effortLevels = new JsonArray();
        if (thinking?["options"] is JsonArray thinkingOptions)
            foreach (var option in thinkingOptions.OfType<JsonObject>())
                if (option["value"]?.GetValue<string>() is { } value) effortLevels.Add(value);

        if (modelOption?["options"] is JsonArray modelOptions)
        foreach (var option in modelOptions.OfType<JsonObject>())
        {
            var value = option["value"]?.GetValue<string>() ?? "";
            var row = new JsonObject
            {
                ["value"] = value,
                ["displayName"] = option["name"]?.GetValue<string>() ?? value,
                ["description"] = option["description"]?.GetValue<string>(),
                ["resolvedModel"] = value,
                ["supportedEffortLevels"] = value == current ? effortLevels.DeepClone() : new JsonArray(),
                ["supportsEffort"] = value == current && effortLevels.Count > 0,
                ["supportsAutoMode"] = true,
                ["supportsFastMode"] = false,
                ["isDefault"] = value == current,
            };
            result.Add(row);
        }
        if (result.Count == 0)
            result.Add(new JsonObject
            {
                ["value"] = "default", ["displayName"] = "Kimi Code default", ["resolvedModel"] = "default",
                ["supportedEffortLevels"] = new JsonArray(), ["supportsEffort"] = false,
                ["supportsAutoMode"] = true, ["supportsFastMode"] = false, ["isDefault"] = true,
            });
        return result;
    }

    private void ApplyGrokModelState(JsonObject? state, bool notify)
    {
        if (state is null) return;
        var current = state["currentModelId"]?.GetValue<string>();
        Models = GrokModelsFromState(state, _requestedModel);
        _model = current ?? _model;
        var currentInfo = (state["availableModels"] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(row => string.Equals(row["modelId"]?.GetValue<string>(), current,
                StringComparison.OrdinalIgnoreCase));
        if (currentInfo?["_meta"]?["reasoningEffort"]?.GetValue<string>() is { } effort)
            _effort = effort;
        if (_initialized && current is not null && !Grok45Preset.IsGrok45(current))
            _requestedModel = current;
        if (!notify) return;
        EmitInit();
        Initialized?.Invoke();
    }

    private void ApplyGrokModelChanged(JsonObject update)
    {
        var current = update["model_id"]?.GetValue<string>() ?? update["modelId"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(current)) return;
        _model = current;
        if (!Grok45Preset.IsGrok45(current)) _requestedModel = current;
        foreach (var row in Models.OfType<JsonObject>())
        {
            var value = row["value"]?.GetValue<string>();
            row["isDefault"] = string.Equals(Grok45Preset.BackendModel(value), current,
                StringComparison.OrdinalIgnoreCase);
        }
        if (update["reasoning_effort"]?.GetValue<string>() is { } effort) _effort = effort;
        EmitInit();
        Initialized?.Invoke();
    }

    internal static JsonArray GrokModelsFromState(JsonObject? state, string? requestedModel = null)
    {
        var result = new JsonArray();
        var current = state?["currentModelId"]?.GetValue<string>();
        var foundGrok45 = false;
        if (state?["availableModels"] is JsonArray available)
        foreach (var info in available.OfType<JsonObject>())
        {
            var value = info["modelId"]?.GetValue<string>()
                        ?? info["id"]?.GetValue<string>()
                        ?? "";
            if (value.Length == 0) continue;
            var display = info["name"]?.GetValue<string>() ?? value;
            var meta = info["_meta"] as JsonObject ?? info["meta"] as JsonObject;
            var efforts = new JsonArray();
            if (meta?["reasoningEfforts"] is JsonArray effortRows)
            foreach (var effort in effortRows)
            {
                var token = effort is JsonObject effortObject
                    ? effortObject["value"]?.GetValue<string>()
                    : effort is JsonValue effortValue ? SafeString(effortValue) : null;
                if (!string.IsNullOrWhiteSpace(token)) efforts.Add(token);
            }
            var isGrok45 = Grok45Preset.IsGrok45(value, display);
            foundGrok45 |= isGrok45;
            result.Add(new JsonObject
            {
                ["value"] = isGrok45 ? Grok45Preset.NormalModelId : value,
                ["displayName"] = isGrok45 ? Grok45Preset.NormalDisplayName : display,
                ["description"] = isGrok45 ? Grok45Preset.NormalDescription : info["description"]?.GetValue<string>(),
                ["resolvedModel"] = value,
                ["supportedEffortLevels"] = efforts,
                ["supportsEffort"] = meta?["supportsReasoningEffort"]?.GetValue<bool>() == true || efforts.Count > 0,
                ["supportsAutoMode"] = true,
                ["supportsFastMode"] = false,
                ["isDefault"] = string.Equals(value, current, StringComparison.OrdinalIgnoreCase),
            });
        }

        if (!foundGrok45)
        {
            var efforts = new JsonArray("low", "medium", "high");
            result.Insert(0, new JsonObject
            {
                ["value"] = Grok45Preset.NormalModelId,
                ["displayName"] = Grok45Preset.NormalDisplayName,
                ["description"] = Grok45Preset.NormalDescription,
                ["resolvedModel"] = Grok45Preset.NormalModelId,
                ["supportedEffortLevels"] = efforts.DeepClone(),
                ["supportsEffort"] = true,
                ["supportsAutoMode"] = true,
                ["supportsFastMode"] = false,
                ["isDefault"] = true,
            });
        }
        return result;
    }

    private string? ResolveBackendModel(string? requestedModel)
    {
        if (_isGrok) return Grok45Preset.BackendModel(requestedModel ?? Grok45Preset.NormalModelId);
        return requestedModel;
    }

    private string? CurrentModelId() => Models.OfType<JsonObject>()
        .FirstOrDefault(x => x["isDefault"]?.GetValue<bool>() == true)?["value"]?.GetValue<string>()
        ?? Models.OfType<JsonObject>().FirstOrDefault()?["value"]?.GetValue<string>();

    private void HandleServerRequest(string method, JsonObject? p, JsonNode rpcId)
    {
        if (method != "session/request_permission")
        {
            WriteJson(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = rpcId.DeepClone(),
                ["error"] = new JsonObject { ["code"] = -32601, ["message"] = $"Unsupported ACP client method: {method}" },
            });
            return;
        }

        var toolCall = p?["toolCall"] as JsonObject ?? new JsonObject();
        var toolUseId = toolCall["toolCallId"]?.GetValue<string>();
        var title = toolCall["title"]?.GetValue<string>() ?? $"{ProviderName}Tool";
        var kind = toolCall["kind"]?.GetValue<string>();
        var input = toolUseId is not null && _toolInputs.TryGetValue(toolUseId, out var saved)
            ? saved.DeepClone().AsObject()
            : BuildToolInput(toolCall, null);
        var name = toolUseId is not null && _toolNames.TryGetValue(toolUseId, out var savedName)
            ? savedName
            : NormalizeToolName(title, kind, input);
        var options = (p?["options"] as JsonArray)?.DeepClone().AsArray() ?? new JsonArray();
        var contentText = ToolContentText(toolCall["content"] as JsonArray);
        string? question = null;

        if (title == "AskUserQuestion")
        {
            name = "AskUserQuestion";
            question = contentText;
            var choices = new JsonArray();
            foreach (var option in options.OfType<JsonObject>())
            {
                if (option["kind"]?.GetValue<string>()?.StartsWith("reject", StringComparison.OrdinalIgnoreCase) == true) continue;
                choices.Add(new JsonObject
                {
                    ["label"] = option["name"]?.GetValue<string>() ?? option["optionId"]?.GetValue<string>() ?? "Option",
                    ["description"] = option["description"]?.GetValue<string>(),
                });
            }
            input["questions"] = new JsonArray(new JsonObject
            {
                ["question"] = question,
                ["header"] = ProviderName,
                ["multiSelect"] = false,
                ["options"] = choices,
            });
        }
        else if (title == "ExitPlanMode" || options.OfType<JsonObject>().Any(x =>
                     x["optionId"]?.GetValue<string>()?.StartsWith("plan_", StringComparison.Ordinal) == true))
        {
            name = "ExitPlanMode";
            input["plan"] = contentText.Replace("Requesting approval to ", "", StringComparison.OrdinalIgnoreCase);
        }
        else if (!string.IsNullOrWhiteSpace(contentText))
        {
            input["description"] ??= contentText;
        }

        var key = ProviderSlug + ":" + RpcKey(rpcId);
        _permissions[key] = new PendingPermission
        {
            RpcId = rpcId.DeepClone(),
            Options = options,
            ToolName = name,
            ToolUseId = toolUseId,
            Question = question,
        };
        var suggestions = options.OfType<JsonObject>().Any(x => x["kind"]?.GetValue<string>() == "allow_always")
            ? new JsonArray("approve_always")
            : null;
        PermissionRequested?.Invoke(new PermissionRequest
        {
            RequestId = key,
            ToolName = name,
            ToolUseId = toolUseId,
            Input = input,
            Suggestions = suggestions,
        });
    }

    public void RespondPermission(string requestId, JsonObject result, string? toolUseId)
    {
        if (!_permissions.TryRemove(requestId, out var pending)) return;
        var allow = result["behavior"]?.GetValue<string>() == "allow";
        var always = result["updatedPermissions"] is not null;
        var answer = pending.Question is null
            ? null
            : result["updatedInput"]?["answers"]?[pending.Question]?.GetValue<string>();
        var optionId = ChoosePermissionOption(pending.Options, pending.ToolName, allow, always, answer,
            result["message"]?.GetValue<string>());
        JsonObject outcome = optionId is null
            ? new JsonObject { ["outcome"] = "cancelled" }
            : new JsonObject { ["outcome"] = "selected", ["optionId"] = optionId };
        WriteJson(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = pending.RpcId.DeepClone(),
            ["result"] = new JsonObject { ["outcome"] = outcome },
        });
    }

    internal static string? ChoosePermissionOption(JsonArray options, string toolName, bool allow, bool always,
        string? answer, string? denyMessage)
    {
        var rows = options.OfType<JsonObject>().ToList();
        if (toolName == "AskUserQuestion")
        {
            if (!allow) return rows.FirstOrDefault(x => x["optionId"]?.GetValue<string>()?.EndsWith("_skip", StringComparison.Ordinal) == true)?["optionId"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(answer))
            {
                var exact = rows.FirstOrDefault(x => string.Equals(x["name"]?.GetValue<string>(), answer, StringComparison.OrdinalIgnoreCase));
                if (exact is not null) return exact["optionId"]?.GetValue<string>();
                var contained = rows.FirstOrDefault(x => answer.Split(',', StringSplitOptions.TrimEntries)
                    .Any(a => string.Equals(a, x["name"]?.GetValue<string>(), StringComparison.OrdinalIgnoreCase)));
                if (contained is not null) return contained["optionId"]?.GetValue<string>();
            }
            return rows.FirstOrDefault(x => x["kind"]?.GetValue<string>() == "allow_once")?["optionId"]?.GetValue<string>();
        }

        var plan = toolName == "ExitPlanMode" || rows.Any(x => x["optionId"]?.GetValue<string>()?.StartsWith("plan_", StringComparison.Ordinal) == true);
        if (plan)
        {
            if (allow)
                return rows.FirstOrDefault(x => x["optionId"]?.GetValue<string>() == "plan_approve")?["optionId"]?.GetValue<string>()
                       ?? rows.FirstOrDefault(x => x["optionId"]?.GetValue<string>()?.StartsWith("plan_opt_", StringComparison.Ordinal) == true)?["optionId"]?.GetValue<string>()
                       ?? rows.FirstOrDefault(x => x["kind"]?.GetValue<string>()?.StartsWith("allow", StringComparison.Ordinal) == true)?["optionId"]?.GetValue<string>();
            var revise = !string.IsNullOrWhiteSpace(denyMessage);
            return rows.FirstOrDefault(x => x["optionId"]?.GetValue<string>() == (revise ? "plan_revise" : "plan_reject_and_exit"))?["optionId"]?.GetValue<string>()
                   ?? rows.FirstOrDefault(x => x["kind"]?.GetValue<string>()?.StartsWith("reject", StringComparison.Ordinal) == true)?["optionId"]?.GetValue<string>();
        }

        if (allow && always)
            return rows.FirstOrDefault(x => x["kind"]?.GetValue<string>() == "allow_always")?["optionId"]?.GetValue<string>()
                   ?? rows.FirstOrDefault(x => x["optionId"]?.GetValue<string>() == "approve_always")?["optionId"]?.GetValue<string>();
        if (allow)
            return rows.FirstOrDefault(x => x["kind"]?.GetValue<string>()?.StartsWith("allow", StringComparison.Ordinal) == true)?["optionId"]?.GetValue<string>();
        return rows.FirstOrDefault(x => x["kind"]?.GetValue<string>()?.StartsWith("reject", StringComparison.Ordinal) == true)?["optionId"]?.GetValue<string>();
    }

    public Task InterruptAsync()
    {
        if (SessionId is not null)
            Notify("session/cancel", new JsonObject { ["sessionId"] = SessionId });
        return Task.CompletedTask;
    }

    public Task SetPermissionModeAsync(string mode)
    {
        _permissionMode = _requestedPermissionMode = mode;
        if (!_initialized || SessionId is null) return Task.CompletedTask;
        return _isGrok
            // Grok ACP session modes are prompt modes (default/plan), not permission modes. Yolo/auto are
            // creation-time metadata; VibeCode still applies later permission changes when answering requests.
            ? SafeSetGrokModeAsync(mode == "plan" ? "plan" : "default")
            : SafeSetConfigOptionAsync("mode", ToAcpMode(mode));
    }

    public async Task SetModelAsync(string? model, string? effort = null)
    {
        if (_isGrok)
        {
            _requestedModel = string.IsNullOrWhiteSpace(model)
                              || string.Equals(model, "default", StringComparison.OrdinalIgnoreCase)
                ? Grok45Preset.NormalModelId
                : model;
            _model = ResolveBackendModel(_requestedModel);
            _effort = _requestedEffort = effort;
            foreach (var row in Models.OfType<JsonObject>())
                row["isDefault"] = string.Equals(row["value"]?.GetValue<string>(), _requestedModel,
                    StringComparison.OrdinalIgnoreCase);
            if (_initialized && SessionId is not null && _model is not null)
                await SafeSetGrokModelAsync(_model, effort);
            return;
        }

        _requestedModel = string.IsNullOrWhiteSpace(model)
                          || string.Equals(model, "default", StringComparison.OrdinalIgnoreCase)
            ? null
            : model;
        _model = ResolveBackendModel(_requestedModel);
        _effort = _requestedEffort = effort;
        if (!_initialized || SessionId is null) return;
        if (_model is not null) await SafeSetConfigOptionAsync("model", _model);
        if (!string.IsNullOrWhiteSpace(effort)) await SafeSetConfigOptionAsync("thinking", ThinkingValue(effort));
    }

    private async Task SafeSetConfigOptionAsync(string id, string value)
    {
        try { await SetConfigOptionAsync(id, value); }
        catch (Exception ex) { Debug.WriteLine($"vibecode: Kimi set {id} failed: {ex.Message}"); }
    }

    private Task<JsonNode?> SetConfigOptionAsync(string id, string value) => RequestAsync(
        "session/set_config_option", new JsonObject
        {
            ["sessionId"] = SessionId,
            ["configId"] = id,
            ["value"] = value,
        });

    private async Task SafeSetGrokModelAsync(string model, string? effort)
    {
        try
        {
            var parameters = new JsonObject { ["sessionId"] = SessionId, ["modelId"] = model };
            if (!string.IsNullOrWhiteSpace(effort))
                parameters["_meta"] = new JsonObject { ["reasoningEffort"] = effort };
            await RequestAsync("session/set_model", parameters);
            _model = model;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"vibecode: Grok set model failed: {ex.Message}");
        }
    }

    private async Task SafeSetGrokModeAsync(string mode)
    {
        try
        {
            await RequestAsync("session/set_mode", new JsonObject
            {
                ["sessionId"] = SessionId,
                ["modeId"] = mode,
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"vibecode: Grok set mode failed: {ex.Message}");
        }
    }

    private static string ThinkingValue(string? effort)
    {
        if (string.IsNullOrWhiteSpace(effort)) return "on";
        var value = effort.Trim().ToLowerInvariant();
        return value == "auto" ? "on" : value;
    }

    internal static string ToAcpMode(string mode) => mode switch
    {
        "plan" => "plan",
        "bypassPermissions" => "yolo",
        "dontAsk" => "auto",
        // Kimi ACP has no "accept edits only" mode. Both it and VibeCode Auto remain manual at the agent,
        // then ChatViewModel answers only the matching safe requests; mapping acceptEdits to yolo would also
        // approve arbitrary shell commands.
        "acceptEdits" or "auto" => "default",
        _ => "default",
    };

    private static string FromAcpMode(string mode) => mode switch
    {
        "plan" => "plan",
        "yolo" => "bypassPermissions",
        "auto" => "dontAsk",
        _ => "default",
    };

    private Task<JsonNode?> RequestAsync(string method, JsonObject? p = null)
    {
        var id = Interlocked.Increment(ref _requestSeq).ToString();
        var pending = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = pending;
        WriteJson(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = int.Parse(id),
            ["method"] = method,
            ["params"] = p ?? new JsonObject(),
        });
        return pending.Task;
    }

    private void Notify(string method, JsonObject? p) => WriteJson(new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["method"] = method,
        ["params"] = p ?? new JsonObject(),
    });

    private void WriteJson(JsonNode node)
    {
        var json = node.ToJsonString(new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        lock (_writeLock)
        {
            if (_stdin is null) return;
            _stdin.Write(json);
            _stdin.Write('\n');
            _stdin.Flush();
        }
    }

    private void EmitFailure(string title, string detail, double durationMs = 0, string subtype = "error")
    {
        Emit(new JsonObject
        {
            ["type"] = "result",
            ["subtype"] = subtype,
            ["is_error"] = true,
            ["result"] = $"{title}: {detail}",
            ["duration_ms"] = durationMs,
            ["usage"] = new JsonObject(),
        });
    }

    private string FriendlyError(Exception ex)
    {
        if (ex is KimiRpcException { Code: -32000 }
            || ex.Message.Contains("not authenticated", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase))
            return _isGrok
                ? "Grok is not signed in. Open VibeCode's account menu and choose Grok, or run `grok login`."
                : "Kimi Code is not signed in. Open VibeCode's account menu and choose Kimi Code, or run `kimi login`.";
        return ex is TaskCanceledException
            ? $"{ProviderName} closed before the request completed."
            : ex.Message;
    }

    private static int JsonInt(JsonNode? node)
    {
        if (node is null) return 0;
        try { return node.GetValue<int>(); } catch { /* wider number */ }
        try { return checked((int)node.GetValue<long>()); } catch { return 0; }
    }

    private static string RpcKey(JsonNode id) => id.ToJsonString().Trim('"');

    private static string ContentText(JsonNode? content) => content switch
    {
        JsonObject o when o["type"]?.GetValue<string>() == "text" => o["text"]?.GetValue<string>() ?? "",
        JsonValue v => SafeString(v),
        _ => "",
    };

    private static string SafeString(JsonValue value)
    {
        try { return value.GetValue<string>(); }
        catch { return value.ToJsonString(); }
    }

    private static string ToolContentText(JsonArray? content)
    {
        if (content is null) return "";
        return string.Join("\n", content.OfType<JsonObject>().Select(item =>
        {
            if (item["type"]?.GetValue<string>() == "content") return ContentText(item["content"]);
            if (item["type"]?.GetValue<string>() == "diff")
                return $"{item["path"]}\n{item["newText"]}";
            return "";
        }).Where(x => x.Length > 0));
    }

    private static string NodeText(JsonNode node)
    {
        if (node is JsonValue value) return SafeString(value);
        if (node is JsonArray array) return string.Join("\n", array.Select(x => x is null ? "" : NodeText(x)));
        if (node is JsonObject obj)
        {
            foreach (var key in new[] { "text", "content", "message", "output", "stdout", "stderr" })
                if (obj[key] is { } child) return NodeText(child);
        }
        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonArray BuiltInCommands() => new(
        Command("compact", "Compact conversation context"),
        Command("status", "Show session status"),
        Command("usage", "Show Kimi session token usage"),
        Command("mcp", "Show configured MCP servers"),
        Command("tasks", "Show background tasks"),
        Command("help", "Show available Kimi commands"));

    private static JsonArray GrokBuiltInCommands() => new(
        Command("compact", "Compact Grok conversation context"),
        Command("status", "Show Grok session status"),
        Command("usage", "Show Grok session token usage"),
        Command("mcp", "Show configured MCP servers"),
        Command("tasks", "Show Grok background tasks"),
        Command("help", "Show available Grok commands"));

    private static JsonObject Command(string name, string description) =>
        new() { ["name"] = name, ["description"] = description };

    private void Emit(JsonNode node) => MessageReceived?.Invoke(node);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var (key, permission) in _permissions.ToList())
        {
            WriteJson(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = permission.RpcId.DeepClone(),
                ["result"] = new JsonObject { ["outcome"] = new JsonObject { ["outcome"] = "cancelled" } },
            });
            PermissionCancelled?.Invoke(key);
        }
        _permissions.Clear();
        try { _stdin?.Close(); } catch { /* broken pipe */ }   // EOF → the CLI exits on its own
        // Reap off the UI thread so closing a pane/bridge never freezes the app; delete temp attachments only
        // once the process is actually gone (it may still hold them open until then).
        ProcessJob.ReapDetached(_proc, afterExit: () =>
        {
            foreach (var path in _tempAttachments)
                try { File.Delete(path); } catch { /* best-effort temp cleanup */ }
        });
        _turnGate.Dispose();
    }
}
