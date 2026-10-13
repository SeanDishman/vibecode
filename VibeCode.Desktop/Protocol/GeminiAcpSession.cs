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

public sealed class GeminiSessionOptions
{
    public required string Cwd { get; init; }
    public string? Resume { get; init; }
    public string? Model { get; init; }
    public string PermissionMode { get; init; } = "default";
    public string? AppendSystemPrompt { get; init; }
    /// <summary>Immutable VibeCode MCP snapshot forwarded through ACP for this session.</summary>
    public IReadOnlyList<McpServerDefinition>? McpServers { get; init; }
}

/// <summary>Translates Gemini CLI's ACP session updates and permission requests into the chat stream.</summary>
public sealed partial class GeminiAcpSession : ICodingSession
{
    private const string SystemContextOpen = "<vibecode-system-context>";
    private const string SystemContextClose = "</vibecode-system-context>";
    public event Action<JsonNode>? MessageReceived;
    public event Action<PermissionRequest>? PermissionRequested;
    public event Action<string>? PermissionCancelled;
    public event Action<int, string>? Exited;
    public event Action? Initialized;

    /// <summary>Raw ACP notifications for protocol smoke tests and diagnostics.</summary>
    internal event Action<string, JsonObject?>? NotificationReceived;

    public JsonArray Commands { get; private set; } = new();
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

    private sealed class AcpRpcException(int code, string message, JsonNode? data = null) : Exception(message)
    {
        public int Code { get; } = code;
        public JsonNode? RpcData { get; } = data;
    }


    private readonly GeminiSessionOptions _options;
    private readonly object _writeLock = new();
    private readonly StringBuilder _stderr = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly ConcurrentDictionary<string, PendingPermission> _permissions = new();
    private readonly Dictionary<string, JsonObject> _toolInputs = new();
    private readonly Dictionary<string, string> _toolNames = new();
    private readonly HashSet<string> _announcedTools = new();
    private readonly Queue<JsonNode> _earlyMessages = new();
    private readonly List<string> _tempAttachments = new();
    private readonly SemaphoreSlim _turnGate = new(1, 1);

    private Process? _proc;
    private StreamWriter? _stdin;
    private int _requestSeq;
    private string? _model;
    private string _permissionMode;
    private string? _requestedModel;
    private string _requestedPermissionMode;
    private bool _basePromptPending;
    private bool _initialized;
    private bool _initializationFailed;
    private bool _disposed;
    private bool _streamStarted;
    private int _nextStreamIndex;
    private int? _activeStreamIndex;
    private string? _activeStreamBlock;
    private int _turnActivity;
    private const string ProviderName = "Gemini";
    private const string ProviderSlug = "gemini";

    public GeminiAcpSession(GeminiSessionOptions options)
    {
        _ = GeminiRuntime.ResolveCliPath();
        _options = options;
        _requestedModel = string.IsNullOrWhiteSpace(options.Model)
                          || string.Equals(options.Model, "default", StringComparison.OrdinalIgnoreCase)
            ? null
            : options.Model;
        _model = _requestedModel;
        _permissionMode = _requestedPermissionMode = options.PermissionMode;
        _basePromptPending = !string.IsNullOrWhiteSpace(options.AppendSystemPrompt);
    }

    public void Start()
    {
        var provider = ProviderSlug;
        if (!GeminiRuntime.HasLogin) throw new InvalidOperationException("Sign in with Google in AI accounts → Gemini to use your subscription.");
        McpCatalog.EnsureLaunchReady(_options.McpServers, provider, _options.Cwd);
        var psi = GeminiRuntime.CreateStartInfo(_options.Cwd, "--acp");
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

            await RequestAsync("authenticate", new JsonObject { ["methodId"] = "oauth-personal" });
            JsonNode? sessionResult;
            var mcpServers = McpCatalog.BuildGeminiAcpServers(_options.McpServers, _options.Cwd);
            if (_options.Resume is not null)
            {
                SessionId = _options.Resume;
                var loadParams = new JsonObject
                {
                    ["sessionId"] = SessionId,
                    ["cwd"] = _options.Cwd,
                    ["mcpServers"] = mcpServers.DeepClone(),
                };
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
                sessionResult = await RequestAsync("session/new", newParams);
                SessionId = sessionResult?["sessionId"]?.GetValue<string>();
            }

            if (SessionId is null) throw new InvalidOperationException($"{ProviderName} ACP did not return a session id.");
            ApplyGeminiModelState(sessionResult?["models"] as JsonObject);
            await ApplyGeminiSelectionsAsync();
            EmitInit();
            if (_options.Resume is not null)
                Emit(new JsonObject { ["type"] = "system", ["subtype"] = "resume_boundary" });

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

    private void EmitInit()
    {
        var selectedModel = CurrentModelId() ?? _model ?? "default";
        var publicModel = selectedModel;
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
        JsonNode? geminiResponse = null;
        try
        {
            if (SessionId is null || _disposed) return;
            BeginGeminiUsage();
            Interlocked.Exchange(ref _turnActivity, 0);
            BeginStream();
            var response = await RequestAsync("session/prompt", new JsonObject
            {
                ["sessionId"] = SessionId,
                ["prompt"] = BuildPrompt(content),
            });

            EndStream();
            geminiResponse = response;
            var stopReason = response?["stopReason"]?.GetValue<string>() ?? "end_turn";
            if (stopReason is not ("end_turn" or "cancelled"))
            {
                EmitFailure($"{ProviderName} stopped without an answer",
                    $"{ProviderName} ended the turn with `{stopReason}` before returning a response.",
                    started.Elapsed.TotalMilliseconds, stopReason == "refusal" ? "refusal" : "error", geminiResponse);
                return;
            }
            if (stopReason == "end_turn" && Volatile.Read(ref _turnActivity) == 0)
            {
                EmitFailure($"{ProviderName} returned no response",
                    "Gemini completed the turn without assistant output. Check the Google account and selected model, then retry.",
                    started.Elapsed.TotalMilliseconds, "empty_response", geminiResponse);
                return;
            }

            var usageReport = EndGeminiUsage(response);
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
            lock (_geminiUsageGate) _geminiUsageActive = false;
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

        if ((_basePromptPending) && prompt.Count == 0)
            AddText("");
        return prompt;

        void AddText(string text)
        {
            if (text.TrimStart().StartsWith('/'))
            {
                prompt.Add(new JsonObject { ["type"] = "text", ["text"] = text });
                return; // Native slash commands must stay first; context is retained for the next real prompt.
            }
            if (_basePromptPending)
            {
                var contexts = new List<string>(2);
                if (_basePromptPending)
                {
                    contexts.Add(_options.AppendSystemPrompt!);
                    _basePromptPending = false;
                }
                text = SystemContextOpen + "\n" + string.Join("\n\n", contexts) + "\n" + SystemContextClose
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
                }
            }
        }
        catch { /* process gone */ }
    }

    private static long FirstJsonLong(JsonObject source, params string[] keys)
    {
        foreach (var key in keys)
            if (source[key] is { } node && JsonLong(node) is var value && value != 0) return value;
        return 0;
    }



    private static long JsonLong(JsonNode? node)
    {
        if (node is null) return 0;
        try { return node.GetValue<long>(); } catch { /* narrower or floating number */ }
        try { return node.GetValue<int>(); } catch { /* floating number */ }
        try { return checked((long)Math.Round(node.GetValue<double>())); } catch { return 0; }
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
            pending.TrySetException(new AcpRpcException(code,
                error["message"]?.GetValue<string>() ?? error.ToJsonString(), error["data"]?.DeepClone()));
        }
        else pending.TrySetResult(message["result"]?.DeepClone());
    }

    private void HandleNotification(string method, JsonObject? p)
    {
        NotificationReceived?.Invoke(method, p?.DeepClone().AsObject());
        if (method == "session/update" && p?["update"] is JsonObject update)
            TranslateSessionUpdate(update);
    }

    private void TranslateSessionUpdate(JsonObject update)
    {
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
                EmitGeminiProgress();
                CaptureTool(update, isUpdate: false);
                break;
            case "tool_call_update":
                EmitGeminiProgress();
                CaptureTool(update, isUpdate: true);
                break;
            case "plan":
                EmitPlan(update["entries"] as JsonArray);
                break;
            case "available_commands_update":
                ApplyCommands(update["availableCommands"] as JsonArray);
                if (_initialized) Initialized?.Invoke();
                break;
            case "usage_update":
                {

                    // snapshot, not an OpenAI/Claude account quota and not a turn-token delta. Project only those

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
        // session/load emits transcript chunks before initialization finishes. Capture their origin here:
        // the UI can already say running when the user queues Send now during a slow resume.
        ["is_replay"] = !_initialized,
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
                        ["type"] = "tool_use",
                        ["id"] = id,
                        ["name"] = name,
                        ["input"] = input.DeepClone(),
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

    private JsonObject BuildToolInput(JsonObject update, JsonObject? previous)
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
        if (update["rawInput"] is null)
        {
            // Gemini ACP supplies human display titles, locations and diffs, not raw arguments.
            // Preserve that evidence for approval without treating a display title as a trusted MCP identity.
            if (update["title"]?.ToString() is { Length: > 0 } title)
            {
                input["description"] ??= title;
                if (update["kind"]?.ToString() == "execute") input["command"] ??= title;
            }
            if ((update["locations"] as JsonArray)?.FirstOrDefault()?["path"]?.ToString() is { Length: > 0 } path)
                input["file_path"] ??= path;
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
        // An ACP kind is broad presentation metadata. Keep the exact built-in MCP identity so a runtime
        // labelling it as "edit" or "execute" cannot turn the naming permission into Write or Bash.
        if (title is not null && BridgeMcpConnection.IsChatTitleMcpName(title)) return title;
        if (string.Equals(title, "AskUserQuestion", StringComparison.OrdinalIgnoreCase)) return "AskUserQuestion";
        if (string.Equals(title, "ExitPlanMode", StringComparison.OrdinalIgnoreCase)) return "ExitPlanMode";


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
            _ => string.IsNullOrWhiteSpace(title) ? "GeminiTool" : title!,
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
        return SetGeminiModeAsync(mode);
    }

    public Task SetModelAsync(string? model, string? effort = null) => SetGeminiModelAsync(model);

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

    private void EmitFailure(string title, string detail, double durationMs = 0, string subtype = "error", JsonNode? geminiResponse = null)
    {
        var failure = new JsonObject
        {
            ["type"] = "result",
            ["subtype"] = subtype,
            ["is_error"] = true,
            ["result"] = $"{title}: {detail}",
            ["duration_ms"] = durationMs,
            ["usage"] = new JsonObject(),
        };
        if (_geminiUsageActive)
            foreach (var (key, value) in EndGeminiUsage(geminiResponse)) failure[key] = value?.DeepClone();
        Emit(failure);
    }

    private static string FriendlyError(Exception ex) => ex switch
    {
        AcpRpcException { Code: -32000 } => "Sign in with Google in AI accounts → Gemini, then retry. " + ex.Message,
        TaskCanceledException => "Gemini closed before the request completed.",
        _ => ex.Message,
    };

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
