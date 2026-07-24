using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VibeCode.Services;

namespace VibeCode.Protocol;

/// <summary>
/// A single Windows Job Object that every claude.exe we spawn is assigned to. It's configured with
/// KILL_ON_JOB_CLOSE, so when VibeCode exits - cleanly OR by a crash - Windows terminates every child
/// (and their whole process trees) automatically. This is what guarantees "closing VibeCode stops the
/// coding, nothing keeps running in the background." It only ever affects processes WE started (they're
/// explicitly assigned to the job); other claude.exe instances on the machine are never touched.
/// </summary>
internal static class ProcessJob
{
    private static readonly nint _job = Create();

    private static nint Create()
    {
        try
        {
            var h = CreateJobObject(nint.Zero, null);
            if (h == nint.Zero) return nint.Zero;
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
                },
            };
            int len = Marshal.SizeOf(info);
            nint ptr = Marshal.AllocHGlobal(len);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                SetInformationJobObject(h, JobObjectExtendedLimitInformation, ptr, (uint)len);
            }
            finally { Marshal.FreeHGlobal(ptr); }
            return h;
        }
        catch { return nint.Zero; }   // job objects unavailable - fall back to per-session Dispose kills
    }

    public static void Assign(Process proc)
    {
        if (_job == nint.Zero) return;
        try { AssignProcessToJobObject(_job, proc.Handle); }
        catch { /* process may already be gone, or nested-job restriction on very old Windows */ }
    }

    /// <summary>Reap a child process without blocking the caller. Session Dispose() runs on the UI thread when a
    /// pane or a whole bridge is closed; a synchronous <c>WaitForExit(3000)</c> there froze the app for up to ~3s
    /// per pane while the CLI flushed and exited. The caller closes stdin first (graceful EOF); this then waits,
    /// force-kills on timeout, disposes, and runs any post-exit cleanup on a background thread. Losing the wait is
    /// safe: every child is assigned to this KILL_ON_JOB_CLOSE job, so it still dies with VibeCode on exit/crash.</summary>
    public static void ReapDetached(Process? proc, Action? afterExit = null)
    {
        if (proc is null) { try { afterExit?.Invoke(); } catch { /* best-effort */ } return; }
        _ = Task.Run(() =>
        {
            try { if (!proc.HasExited && !proc.WaitForExit(3000)) proc.Kill(entireProcessTree: true); }
            catch { /* already gone */ }
            finally
            {
                try { proc.Dispose(); } catch { /* already disposed */ }
                try { afterExit?.Invoke(); } catch { /* best-effort cleanup */ }
            }
        });
    }

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateJobObject(nint lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll")]
    private static extern bool SetInformationJobObject(nint hJob, int infoClass, nint lpInfo, uint cbInfo);

    [DllImport("kernel32.dll")]
    private static extern bool AssignProcessToJobObject(nint hJob, nint hProcess);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }
}

public sealed class ClaudeSessionOptions
{
    public required string Cwd { get; init; }
    public string? Resume { get; init; }
    public bool ForkSession { get; init; }
    public string? Model { get; init; }
    public string? Effort { get; init; }   // low | medium | high | xhigh | max (null = CLI default)
    public string PermissionMode { get; init; } = "default";
    public bool IncludePartialMessages { get; init; } = true;
    /// <summary>Extra text appended to the system prompt (used by Bridge to tell each Claude about its peers).</summary>
    public string? AppendSystemPrompt { get; init; }
    /// <summary>This account's persistent native Claude home. Claude Code owns OAuth storage and refresh here, so
    /// sessions never depend on browser cookies or a short-lived CLAUDE_CODE_OAUTH_TOKEN override.</summary>
    public string? ConfigDirectory { get; init; }
    /// <summary>Claude Code "fast mode" - faster output on supported models. Merged into the session-scoped
    /// <c>--settings</c> JSON alongside <c>showThinkingSummaries</c> (does not persist to the user's real settings).</summary>
    public bool FastMode { get; init; }
    /// <summary>Expose Claude's native Agent tool and forward child output/lifecycle events to the host UI.</summary>
    public bool SwarmsEnabled { get; init; }
    /// <summary>Maximum read-only tools/subagents Claude may run concurrently in this session.</summary>
    public int SwarmMaxWorkers { get; init; } = SwarmPolicy.DefaultMaxWorkers;
    /// <summary>Immutable VibeCode MCP snapshot for this launch; VibeCode never edits Claude's native config.</summary>
    public IReadOnlyList<McpServerDefinition>? McpServers { get; init; }
    /// <summary>Protocol-harness override; production uses VibeCode's private AppData MCP directory.</summary>
    internal string? McpConfigDirectory { get; init; }
}

public sealed class PermissionRequest
{
    public required string RequestId { get; init; }
    public required string ToolName { get; init; }
    public JsonNode? Input { get; init; }
    public JsonNode? Suggestions { get; init; }
    public string? ToolUseId { get; init; }
    public string? AgentId { get; init; }
    public string? DecisionReason { get; init; }
    /// <summary>True only when the provider runtime identity and this live approval envelope passed validation.</summary>
    public bool IntegrityVerified { get; init; }
}

/// <summary>
/// One live Claude Code conversation, driven over the CLI's stream-json protocol
/// (the same wire protocol the official Agent SDK speaks). Spawns `claude` with
/// --input-format/--output-format stream-json and exchanges NDJSON on stdio.
/// </summary>
public sealed class ClaudeSession : ICodingSession
{
    public event Action<JsonNode>? MessageReceived;       // SDK messages: system/assistant/user/stream_event/result/...
    public event Action<PermissionRequest>? PermissionRequested;
    public event Action<string>? PermissionCancelled;     // requestId
    public event Action<int, string>? Exited;             // exit code, stderr tail
    public event Action? Initialized;

    public JsonArray Commands { get; private set; } = new();
    public JsonArray Models { get; private set; } = new();
    public string? SessionId { get; private set; }
    public bool HasExited => _proc is null || _proc.HasExited;

    private readonly ClaudeSessionOptions _options;
    private Process? _proc;
    private StreamWriter? _stdin;
    // Same stall-proofing as CodexSession: stdin writes are queued and drained by a background task instead of
    // blocking the caller. SendUser / RespondPermission / InterruptAsync all run on the UI thread, and a wedged
    // CLI that stops draining its stdin pipe would otherwise freeze the whole app inside Write/Flush.
    private readonly System.Threading.Channels.Channel<string> _writeQueue =
        System.Threading.Channels.Channel.CreateUnbounded<string>(
            new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true });
    private int _reqSeq;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly ConcurrentDictionary<string, byte> _incomingPermissions = new();
    private readonly StringBuilder _stderr = new();
    private readonly TaskCompletionSource _initTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<string, ClaudeSubagentInfo> _subagents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _subagentIdByToolUse = new(StringComparer.Ordinal);
    private int _nextSubagentOrder;
    private bool _rootTurnActive;

    private sealed class ClaudeSubagentInfo
    {
        public required string Id { get; set; }
        public required int Order { get; init; }
        public string? ToolUseId { get; set; }
        public string Label { get; set; } = "Subagent";
        public string Task { get; set; } = "";
        public string Activity { get; set; } = "Starting";
        public string Status { get; set; } = "pendingInit";
        public bool BackgroundExpected { get; set; }
        public bool IsActive => Status is "pendingInit" or "running";
    }

    public Task WhenInitialized => _initTcs.Task;
    public string StderrTail { get { lock (_stderr) return _stderr.ToString(); } }

    public ClaudeSession(ClaudeSessionOptions options) => _options = options;

    public static string ResolveCliPath()
    {
        var candidates = new List<string>();
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        candidates.Add(Path.Combine(appData, "npm", "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe"));
        candidates.Add(Path.Combine(home, ".local", "bin", "claude.exe"));
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            candidates.Add(Path.Combine(dir.Trim(), "claude.exe"));
        }
        foreach (var c in candidates)
        {
            try { if (File.Exists(c)) return c; } catch { /* bad PATH entry */ }
        }
        throw new FileNotFoundException(
            "Could not find the Claude Code CLI (claude.exe). Install it with `npm install -g @anthropic-ai/claude-code` and sign in with /login.");
    }

    /// <summary>
    /// Remove inherited env vars that would make the CLI use a raw API key / custom endpoint instead
    /// of the user's Claude subscription login, plus the entrypoint markers. This is why a machine
    /// with ANTHROPIC_API_KEY set was hitting "402 API key budget exhausted" - the key overrode the sub.
    /// </summary>
    public static void StripInheritedEnv(ProcessStartInfo psi)
    {
        foreach (var name in McpCatalog.ClaudeSuppressedEnvironmentVariables)
            psi.Environment.Remove(name);

        // Scrub first, then deliberately put back a key the user actually chose. Order matters: the
        // strip above exists so an ambient machine-level key can never hijack a subscription run, and
        // this only fires when the selected Claude account IS an API-key account.
        ApiKeyAccountService.Instance.ApplyTo(psi, "claude");
    }

    public void Start()
    {
        var psi = CreateStartInfo(_options);

        _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _proc.Exited += (_, _) =>
        {
            _initTcs.TrySetCanceled();
            foreach (var kv in _pending) kv.Value.TrySetCanceled();
            Exited?.Invoke(_proc!.ExitCode, StderrTail);
        };
        _proc.Start();
        ProcessJob.Assign(_proc);   // so this claude.exe (and its whole tree) dies when VibeCode exits
        _stdin = _proc.StandardInput;

        _ = Task.Run(WriteStdinLoop);
        _ = Task.Run(ReadStdoutLoop);
        _ = Task.Run(ReadStderrLoop);
        _ = Task.Run(InitializeAsync);
    }

    /// <summary>Build separately from Start so the regression harness can verify every safety flag without logging in.</summary>
    internal static ProcessStartInfo CreateStartInfo(ClaudeSessionOptions options)
    {
        McpCatalog.EnsureLaunchReady(options.McpServers, "claude");
        var args = new List<string>
        {
            "--output-format", "stream-json",
            "--input-format", "stream-json",
            "--verbose",
            "--permission-prompt-tool", "stdio",
            "--setting-sources=user,project,local",
            "--permission-mode", options.PermissionMode,
            // Makes "bypassPermissions" *available* to switch into at runtime (without enabling it by
            // default - we still start in the given mode). Without this, the CLI silently ignores a
            // runtime set_permission_mode:bypassPermissions, so Bypass never actually took effect.
            "--allow-dangerously-skip-permissions",
        };
        if (options.IncludePartialMessages) args.Add("--include-partial-messages");
        if (!string.IsNullOrEmpty(options.Model)) { args.Add("--model"); args.Add(options.Model!); }
        if (!string.IsNullOrEmpty(options.Effort)) { args.Add("--effort"); args.Add(options.Effort!); }
        // Newer Claude models (Opus 4.7+/Opus 5/Sonnet 5+/Fable/Mythos) default thinking.display to "omitted":
        // the stream still emits thinking blocks, but thinking / thinking_delta are empty and only the
        // encrypted signature remains. Interactive Claude Code shows summaries when the user opts in;
        // without this flag VibeCode's "Thought process" cards expand to blank. Hidden CLI flag (accepted
        // by claude.exe even when not listed in --help; Agent SDK maps thinking.display → this flag).
        args.Add("--thinking-display");
        args.Add("summarized");
        // Session-scoped settings (do not write the user's real settings.json). Always request thinking
        // summaries for interactive-parity; merge fast mode into the same object when enabled.
        var settings = new JsonObject { ["showThinkingSummaries"] = true };
        if (options.FastMode)
        {
            settings["fastMode"] = true;
            settings["fastModePerSessionOptIn"] = true;
        }
        args.Add("--settings");
        args.Add(settings.ToJsonString());
        var mcpConfigPath = McpCatalog.WriteClaudeLaunchConfig(options.McpServers, options.McpConfigDirectory);
        if (mcpConfigPath is not null) { args.Add("--mcp-config"); args.Add(mcpConfigPath); }
        if (options.SwarmsEnabled)
        {
            // Without this flag Claude emits child tool calls but not the child's own text/thinking, leaving the
            // roster looking stalled. The environment limit is the provider-side backstop for prompt disobedience.
            args.Add("--forward-subagent-text");
        }
        else
        {
            // A bare disallowed tool name removes it from the model context in every permission mode.
            args.Add("--disallowedTools");
            args.Add("Agent");
            args.Add("Task"); // legacy Claude Code releases exposed the same child-agent tool under this alias
        }
        if (!string.IsNullOrEmpty(options.AppendSystemPrompt)) { args.Add("--append-system-prompt"); args.Add(options.AppendSystemPrompt!); }
        if (!string.IsNullOrEmpty(options.Resume)) args.Add($"--resume={options.Resume}");
        if (options.ForkSession) args.Add("--fork-session");

        var psi = new ProcessStartInfo
        {
            FileName = ResolveCliPath(),
            WorkingDirectory = options.Cwd,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        StripInheritedEnv(psi);
        if (options.SwarmsEnabled)
        {
            var max = SwarmPolicy.ClampMaxWorkers(options.SwarmMaxWorkers).ToString();
            psi.Environment["CLAUDE_CODE_FORWARD_SUBAGENT_TEXT"] = "1";
            psi.Environment["CLAUDE_CODE_MAX_TOOL_USE_CONCURRENCY"] = max;
        }
        if (McpCatalog.MaximumStartupTimeoutSeconds(options.McpServers, "claude") is { } mcpStartupSeconds)
        {
            var requestedMilliseconds = checked(mcpStartupSeconds * 1000);
            if (psi.Environment.TryGetValue("MCP_TIMEOUT", out var inherited)
                && int.TryParse(inherited, out var inheritedMilliseconds))
                requestedMilliseconds = Math.Max(requestedMilliseconds, inheritedMilliseconds);
            psi.Environment["MCP_TIMEOUT"] = requestedMilliseconds.ToString();
        }
        psi.Environment.Remove("CLAUDE_CODE_OAUTH_TOKEN");
        if (!string.IsNullOrWhiteSpace(options.ConfigDirectory))
            psi.Environment["CLAUDE_CONFIG_DIR"] = Path.GetFullPath(options.ConfigDirectory);
        return psi;
    }

    private async Task ReadStdoutLoop()
    {
        try
        {
            var reader = _proc!.StandardOutput;
            string? line;
            while ((line = await reader.ReadLineAsync()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try { HandleLine(line); }
                catch (Exception ex) { Debug.WriteLine($"vibecode: bad line: {ex.Message}"); }
            }
        }
        catch (Exception ex) { Debug.WriteLine($"vibecode: stdout loop ended: {ex.Message}"); }
    }

    private async Task ReadStderrLoop()
    {
        try
        {
            var reader = _proc!.StandardError;
            string? line;
            while ((line = await reader.ReadLineAsync()) is not null)
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

    private void HandleLine(string line)
    {
        var node = JsonNode.Parse(line);
        if (node is null) return;
        var type = node["type"]?.GetValue<string>();
        switch (type)
        {
            case "control_response":
            {
                var resp = node["response"];
                var id = resp?["request_id"]?.GetValue<string>();
                if (id is not null && _pending.TryRemove(id, out var tcs))
                {
                    if (resp!["subtype"]?.GetValue<string>() == "success") tcs.TrySetResult(resp);
                    else tcs.TrySetException(new InvalidOperationException(resp["error"]?.ToString() ?? "CLI error"));
                }
                break;
            }
            case "control_request":
            {
                var requestId = node["request_id"]!.GetValue<string>();
                var request = node["request"]!;
                var subtype = request["subtype"]?.GetValue<string>();
                if (subtype == "can_use_tool")
                {
                    _incomingPermissions[requestId] = 1;
                    PermissionRequested?.Invoke(new PermissionRequest
                    {
                        RequestId = requestId,
                        ToolName = request["tool_name"]?.GetValue<string>() ?? "?",
                        Input = request["input"],
                        Suggestions = request["permission_suggestions"],
                        ToolUseId = request["tool_use_id"]?.GetValue<string>(),
                        AgentId = request["agent_id"]?.GetValue<string>(),
                        DecisionReason = request["decision_reason"]?.ToString(),
                    });
                }
                else
                {
                    // Subtypes we don't serve (hooks, SDK MCP) - reply with an error so the CLI isn't left waiting.
                    WriteJson(new JsonObject
                    {
                        ["type"] = "control_response",
                        ["response"] = new JsonObject
                        {
                            ["subtype"] = "error",
                            ["request_id"] = requestId,
                            ["error"] = $"Unsupported control request: {subtype}",
                        },
                    });
                }
                break;
            }
            case "control_cancel_request":
            {
                var id = node["request_id"]?.GetValue<string>();
                if (id is not null && _incomingPermissions.TryRemove(id, out _))
                    PermissionCancelled?.Invoke(id);
                break;
            }
            default:
            {
                if (type == "system" && node["subtype"]?.GetValue<string>() == "init")
                    SessionId = node["session_id"]?.GetValue<string>();

                // Claude can finish the root response while background Agent tasks (run_in_background) keep working.
                // The result is forwarded straight away so the chat goes idle and queued prompts flush - the same
                // feel as the CLI, where you keep talking while background work continues. This used to be held back
                // until every child reached a terminal event, which pinned the pane to "running" for the whole swarm:
                // Stop stayed armed and send-now went through Interrupt(), killing the children the user was waiting on.
                if (type == "result") _rootTurnActive = false;

                ObserveClaudeSubagents(node, type);
                MessageReceived?.Invoke(node);
                if (type == "result") EmitTurnActivity();
                break;
            }
        }
    }

    /// <summary>Test seam for recorded SDK envelopes; production input still arrives exclusively through stdout.</summary>
    internal void HandleLineForTest(string line) => HandleLine(line);

    private void ObserveClaudeSubagents(JsonNode node, string? type)
    {
        if (type == "assistant")
        {
            if (node["parent_tool_use_id"] is null) _rootTurnActive = true;
            ObserveAssistantAgents(node);
            if (node["parent_tool_use_id"]?.GetValue<string>() is { Length: > 0 } parent
                && _subagentIdByToolUse.TryGetValue(parent, out var childId)
                && _subagents.TryGetValue(childId, out var child))
            {
                child.Status = "running";
                child.Activity = AssistantActivity(node["message"]);
                EmitSubagentUpdate();
            }
            EmitTurnActivity();
            return;
        }

        if (type == "user")
        {
            ObserveAgentToolResults(node);
            return;
        }

        if (type != "system") return;
        switch (node["subtype"]?.GetValue<string>())
        {
            case "task_started": ObserveTaskStarted(node); break;
            case "task_progress": ObserveTaskProgress(node); break;
            case "task_updated": ObserveTaskUpdated(node); break;
            case "task_notification": ObserveTaskNotification(node); break;
        }
    }

    private void ObserveAssistantAgents(JsonNode node)
    {
        if (node["message"]?["content"] is not JsonArray blocks) return;
        foreach (var block in blocks.OfType<JsonObject>())
        {
            if (block["type"]?.GetValue<string>() != "tool_use") continue;
            var name = block["name"]?.GetValue<string>();
            if (name is not ("Agent" or "Task")) continue; // Task remains a supported alias for Agent.
            var toolUseId = block["id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(toolUseId)) continue;
            var input = block["input"] as JsonObject;
            var task = NodeText(input?["description"] ?? input?["prompt"]);
            var kind = NodeText(input?["subagent_type"]);
            var agent = EnsureSubagent(toolUseId!, task, kind);
            agent.ToolUseId = toolUseId;
            agent.Status = "running";
            agent.Activity = "Starting";
            agent.BackgroundExpected = input?["run_in_background"]?.GetValue<bool>() == true;
            _subagentIdByToolUse[toolUseId!] = agent.Id;
        }
        EmitSubagentUpdate();
    }

    private void ObserveAgentToolResults(JsonNode node)
    {
        if (node["message"]?["content"] is not JsonArray blocks) return;
        foreach (var block in blocks.OfType<JsonObject>())
        {
            if (block["type"]?.GetValue<string>() != "tool_result") continue;
            var toolUseId = block["tool_use_id"]?.GetValue<string>();
            if (toolUseId is null || !_subagentIdByToolUse.TryGetValue(toolUseId, out var id)
                                  || !_subagents.TryGetValue(id, out var agent)) continue;

            var structured = node["tool_use_result"] as JsonObject;
            var status = structured?["status"]?.GetValue<string>();
            if (status is "async_launched" or "sub_agent_entered")
            {
                agent.Status = "running";
                agent.Activity = status == "async_launched" ? "Working in background" : "Working";
            }
            else if (block["is_error"]?.GetValue<bool>() == true)
            {
                agent.Status = "errored";
                agent.Activity = "Agent failed";
            }
            else if (status == "completed" || status is null && !agent.BackgroundExpected)
            {
                agent.Status = "completed";
                agent.Activity = "Finished";
            }
        }
        EmitSubagentUpdate();
    }

    private void ObserveTaskStarted(JsonNode node)
    {
        var taskType = node["task_type"]?.GetValue<string>();
        if (taskType is not ("local_agent" or "remote_agent")) return; // ignore background Bash/Monitor tasks
        var taskId = node["task_id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(taskId)) return;
        var toolUseId = node["tool_use_id"]?.GetValue<string>();
        var description = NodeText(node["description"]);
        var agent = ReidentifySubagent(taskId!, toolUseId, description);
        agent.Status = "running";
        agent.Activity = "Working";
        EmitSubagentUpdate();
    }

    private void ObserveTaskProgress(JsonNode node)
    {
        var taskId = node["task_id"]?.GetValue<string>();
        if (taskId is null || !_subagents.TryGetValue(taskId, out var agent)) return;
        agent.Status = "running";
        var description = NodeText(node["description"]);
        if (description.Length > 0) agent.Task = description;
        var kind = NodeText(node["subagent_type"]);
        if (kind.Length > 0) agent.Label = $"{kind} {agent.Order}";
        var summary = NodeText(node["summary"]);
        var lastTool = NodeText(node["last_tool_name"]);
        agent.Activity = summary.Length > 0 ? summary
            : lastTool.Length > 0 ? $"Using {lastTool}"
            : "Working";
        EmitSubagentUpdate();
    }

    private void ObserveTaskUpdated(JsonNode node)
    {
        var taskId = node["task_id"]?.GetValue<string>();
        if (taskId is null || !_subagents.TryGetValue(taskId, out var agent)) return;
        var patch = node["patch"] as JsonObject;
        var description = NodeText(patch?["description"]);
        if (description.Length > 0) agent.Task = description;
        var status = patch?["status"]?.GetValue<string>();
        if (status is not null) agent.Status = status switch
        {
            "pending" => "pendingInit",
            "running" => "running",
            "completed" => "completed",
            "failed" => "errored",
            "killed" => "interrupted",
            _ => agent.Status,
        };
        var error = NodeText(patch?["error"]);
        agent.Activity = error.Length > 0 ? error : agent.Status switch
        {
            "completed" => "Finished",
            "errored" => "Failed",
            "interrupted" => "Stopped",
            "running" => agent.Activity,
            _ => agent.Activity,
        };
        EmitSubagentUpdate();
    }

    private void ObserveTaskNotification(JsonNode node)
    {
        var taskId = node["task_id"]?.GetValue<string>();
        if (taskId is null || !_subagents.TryGetValue(taskId, out var agent)) return;
        agent.Status = node["status"]?.GetValue<string>() switch
        {
            "completed" => "completed",
            "failed" => "errored",
            "stopped" => "interrupted",
            _ => agent.Status,
        };
        var summary = NodeText(node["summary"]);
        agent.Activity = summary.Length > 0 ? summary : agent.Status switch
        {
            "completed" => "Finished",
            "errored" => "Failed",
            "interrupted" => "Stopped",
            _ => agent.Activity,
        };
        EmitSubagentUpdate();
    }

    private ClaudeSubagentInfo ReidentifySubagent(string taskId, string? toolUseId, string task)
    {
        if (_subagents.TryGetValue(taskId, out var exact))
        {
            if (!string.IsNullOrWhiteSpace(toolUseId)) exact.ToolUseId = toolUseId;
            return exact;
        }
        if (toolUseId is not null && _subagentIdByToolUse.TryGetValue(toolUseId, out var oldId)
                                 && _subagents.Remove(oldId, out var provisional))
        {
            provisional.Id = taskId;
            provisional.ToolUseId = toolUseId;
            if (task.Length > 0) provisional.Task = task;
            _subagents[taskId] = provisional;
            _subagentIdByToolUse[toolUseId] = taskId;
            return provisional;
        }
        var created = EnsureSubagent(taskId, task, null);
        if (toolUseId is not null)
        {
            created.ToolUseId = toolUseId;
            _subagentIdByToolUse[toolUseId] = taskId;
        }
        return created;
    }

    private ClaudeSubagentInfo EnsureSubagent(string id, string task, string? kind)
    {
        if (_subagents.TryGetValue(id, out var existing))
        {
            if (task.Length > 0) existing.Task = task;
            return existing;
        }
        var order = ++_nextSubagentOrder;
        var agent = new ClaudeSubagentInfo
        {
            Id = id,
            Order = order,
            Label = string.IsNullOrWhiteSpace(kind) ? $"Subagent {order}" : $"{kind} {order}",
            Task = task,
        };
        _subagents[id] = agent;
        return agent;
    }

    private void EmitSubagentUpdate()
    {
        if (_subagents.Count == 0) return;
        var agents = new JsonArray(_subagents.Values.OrderBy(agent => agent.Order).Select(agent => (JsonNode)new JsonObject
        {
            ["thread_id"] = agent.Id,
            ["parent_tool_use_id"] = agent.ToolUseId,
            ["label"] = agent.Label,
            ["task"] = agent.Task,
            ["activity"] = Truncate(agent.Activity, 180),
            ["status"] = agent.Status,
        }).ToArray());
        MessageReceived?.Invoke(new JsonObject
        {
            ["type"] = "system",
            ["subtype"] = "subagent_update",
            ["agents"] = agents,
        });
        EmitTurnActivity();
    }

    /// <summary>Liveness of the ROOT turn, which is what arms Stop and holds queued prompts back. Background children
    /// deliberately do NOT count: they outlive the root result, and reporting them here would flip the chat back to
    /// "running" on every task_progress, so the user could only reach the composer by interrupting them. They stay
    /// visible through <see cref="EmitSubagentUpdate"/>, which is what keeps the host's subagent count live.</summary>
    private void EmitTurnActivity() => MessageReceived?.Invoke(new JsonObject
    {
        ["type"] = "system",
        ["subtype"] = "turn_activity",
        ["active"] = _rootTurnActive,
    });

    private static string AssistantActivity(JsonNode? message)
    {
        if (message?["content"] is not JsonArray blocks) return "Working";
        foreach (var block in blocks.OfType<JsonObject>().Reverse())
        {
            if (block["type"]?.GetValue<string>() == "tool_use")
                return $"Using {NodeText(block["name"])}";
            if (block["type"]?.GetValue<string>() == "text" && NodeText(block["text"]).Length > 0)
                return "Reporting progress";
        }
        return "Working";
    }

    private static string NodeText(JsonNode? node)
    {
        if (node is null) return "";
        try { return node.GetValue<string>().Trim(); }
        catch { return node.ToJsonString(); }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "â€¦";

    /// <summary>
    /// Claude models Anthropic has shipped that the local Claude Code CLI may not yet advertise in
    /// <c>initialize.models</c>. Kept in sync with <see cref="VibeCode.UI.ProviderModelCatalog"/> fallbacks.
    /// </summary>
    private static readonly (string Value, string Display, string Description)[] KnownModelExtras =
    [
        ("claude-opus-5", "Claude Opus 5",
            "Near-Fable intelligence for complex agentic coding and enterprise work, at half Fable price."),
        ("claude-opus-4-8", "Opus 4.8",
            "Previous flagship Opus. Highly autonomous on long-horizon agentic and knowledge work."),
        ("claude-opus-4-6", "Opus 4.6",
            "Older Opus. Still accepts temperature and a fixed thinking budget that 4.7+ dropped."),
    ];

    /// <summary>
    /// Insert known model IDs the CLI omitted. Placed after the default/alias rows when possible so
    /// "Claude Opus 5" appears near the top of the picker instead of buried under Haiku.
    /// </summary>
    internal static void EnsureKnownModels(JsonArray models)
    {
        if (models.Count == 0) return;
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in models.OfType<JsonObject>())
        {
            if (row["value"]?.GetValue<string>() is { Length: > 0 } v) existing.Add(v);
            if (row["resolvedModel"]?.GetValue<string>() is { Length: > 0 } r)
            {
                existing.Add(r);
                // Strip "[1m]"-style variant tags so claude-opus-5 matches claude-opus-5[1m].
                var bracket = r.IndexOf('[');
                if (bracket > 0) existing.Add(r[..bracket]);
            }
        }

        // Clone effort tiers from the live "opus" alias (or any opus-tier row) so the new row has real levels.
        JsonArray? effortTemplate = null;
        bool supportsEffort = false, supportsAuto = false;
        foreach (var row in models.OfType<JsonObject>())
        {
            var value = row["value"]?.GetValue<string>() ?? "";
            var resolved = row["resolvedModel"]?.GetValue<string>() ?? "";
            if (value.Equals("opus", StringComparison.OrdinalIgnoreCase)
                || value.Contains("opus", StringComparison.OrdinalIgnoreCase)
                || resolved.Contains("opus", StringComparison.OrdinalIgnoreCase))
            {
                if (row["supportedEffortLevels"] is JsonArray levels) effortTemplate = levels;
                supportsEffort = row["supportsEffort"]?.GetValue<bool>() ?? (effortTemplate?.Count > 0);
                supportsAuto = row["supportsAutoMode"]?.GetValue<bool>() ?? false;
                break;
            }
        }

        // Insert right after "default" (or at index 0) so it sits above the legacy Opus 4.8 alias.
        int insertAt = 0;
        for (int i = 0; i < models.Count; i++)
        {
            if (models[i] is JsonObject row
                && string.Equals(row["value"]?.GetValue<string>(), "default", StringComparison.OrdinalIgnoreCase))
            {
                insertAt = i + 1;
                break;
            }
        }

        foreach (var (value, display, description) in KnownModelExtras)
        {
            if (existing.Contains(value)) continue;
            var levels = effortTemplate is null
                ? new JsonArray("low", "medium", "high", "xhigh", "max")
                : (JsonArray)effortTemplate.DeepClone();
            models.Insert(insertAt++, new JsonObject
            {
                ["value"] = value,
                ["displayName"] = display,
                ["description"] = description,
                ["resolvedModel"] = value,
                ["supportedEffortLevels"] = levels,
                ["supportsEffort"] = supportsEffort || levels.Count > 0,
                ["supportsAutoMode"] = supportsAuto,
                // Opus 5 ships Fast mode on Claude Platform / Claude Code usage credits.
                ["supportsFastMode"] = true,
                ["isDefault"] = false,
            });
            existing.Add(value);
        }
    }

    private async Task InitializeAsync()
    {
        try
        {
            var resp = await RequestAsync(new JsonObject { ["subtype"] = "initialize" });
            if (resp?["response"] is JsonObject r)
            {
                Commands = r["commands"] as JsonArray ?? new JsonArray();
                Models = r["models"] as JsonArray ?? new JsonArray();
                // Claude Code's initialize catalog lags new Anthropic IDs (e.g. still shows Opus 4.8 for
                // the "opus" alias). Inject known models so the picker can select them explicitly.
                EnsureKnownModels(Models);
            }
            _initTcs.TrySetResult();
            Initialized?.Invoke();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"vibecode: initialize failed: {ex.Message}");
            _initTcs.TrySetResult(); // don't block sends; CLI works without capabilities
            Initialized?.Invoke();
        }
    }

    private Task<JsonNode?> RequestAsync(JsonObject request)
    {
        var id = $"vcr_{Interlocked.Increment(ref _reqSeq)}";
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        WriteJson(new JsonObject
        {
            ["type"] = "control_request",
            ["request_id"] = id,
            ["request"] = request,
        });
        return tcs.Task;
    }

    private void WriteJson(JsonNode node)
    {
        var json = node.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
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
        catch (Exception ex) { Debug.WriteLine($"vibecode: stdin loop ended: {ex.Message}"); }
    }

    /// <summary>content: string or array of content blocks (text/image).</summary>
    public void SendUser(JsonNode content)
    {
        _rootTurnActive = true;
        var msg = new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = content },
            ["parent_tool_use_id"] = null,
        };
        if (SessionId is not null) msg["session_id"] = SessionId;
        WriteJson(msg);
        EmitTurnActivity();
    }

    public void RespondPermission(string requestId, JsonObject result, string? toolUseId)
    {
        if (!_incomingPermissions.TryRemove(requestId, out _)) return;
        if (toolUseId is not null) result["toolUseID"] = toolUseId;
        WriteJson(new JsonObject
        {
            ["type"] = "control_response",
            ["response"] = new JsonObject
            {
                ["subtype"] = "success",
                ["request_id"] = requestId,
                ["response"] = result,
            },
        });
    }

    public Task InterruptAsync() => SafeRequest(new JsonObject { ["subtype"] = "interrupt" });
    public Task SetPermissionModeAsync(string mode) => SafeRequest(new JsonObject { ["subtype"] = "set_permission_mode", ["mode"] = mode });
    public Task SetModelAsync(string? model, string? effort = null)
    {
        var req = new JsonObject { ["subtype"] = "set_model" };
        if (model is not null) req["model"] = model;
        req["effort"] = effort;   // always sent; explicit null reverts effort to the CLI default (runtime effort rides on set_model)
        return SafeRequest(req);
    }

    private async Task SafeRequest(JsonObject request)
    {
        try { await RequestAsync(request); }
        catch (Exception ex) { Debug.WriteLine($"vibecode: control {request["subtype"]} failed: {ex.Message}"); }
    }

    public void Dispose()
    {
        _writeQueue.Writer.TryComplete();
        try { _stdin?.Close(); } catch { /* broken pipe */ }   // EOF → the CLI exits on its own
        ProcessJob.ReapDetached(_proc);   // wait/kill/dispose off the UI thread so closing a pane never freezes it
    }
}
