using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VibeCode.Protocol;

public sealed class GlmSessionOptions
{
    public required string Cwd { get; init; }
    /// <summary>
    /// Every key this session may use, most-preferred first.
    ///
    /// A list rather than a single key so a turn can move to the next credential instead of dying when one is
    /// rate limited or revoked. The first entry is the account the user actually selected, so a single-key setup
    /// behaves exactly as if this were one key.
    /// </summary>
    public required IReadOnlyList<string> ApiKeys { get; init; }
    public string BaseUrl { get; init; } = GlmPreset.DefaultBaseUrl;
    public string? Model { get; init; }
    public string PermissionMode { get; init; } = "default";
    public string? AppendSystemPrompt { get; init; }
}

/// <summary>
/// A coding session spoken natively against GLM on Baseten's OpenAI-compatible API.
///
/// Every other provider in VibeCode is an external CLI that already contains an agent loop; this one is the agent
/// loop. That is forced rather than preferred - see <see cref="GlmPreset"/> - because the transport with working
/// tool calls here is OpenAI chat-completions, and none of the four CLIs VibeCode drives can speak it.
///
/// The class earns its keep by translating in both directions at once. Outbound it keeps an OpenAI message array
/// and advertises <see cref="GlmTools"/>. Inbound it re-shapes the OpenAI SSE stream into the Anthropic
/// stream-json envelopes <c>ChatViewModel</c> already understands, so the transcript renders GLM's thinking,
/// text, tool calls and diffs with exactly the same UI as Claude - no provider-specific presentation code.
/// </summary>
public sealed class GlmSession : ICodingSession
{
    public event Action<JsonNode>? MessageReceived;
    public event Action<PermissionRequest>? PermissionRequested;
    public event Action<string>? PermissionCancelled;
    public event Action<int, string>? Exited;
    public event Action? Initialized;

    public JsonArray Commands { get; } = new();
    public JsonArray Models { get; private set; } = new();
    public string? SessionId { get; private set; }
    public bool HasExited => _disposed;

    /// <summary>Tool round-trips allowed in a single turn before the loop is cut short. High enough for real work,
    /// finite so a model that loops on a failing edit cannot bill forever.</summary>
    private const int MaxToolRoundTrips = 60;

    private readonly GlmSessionOptions _options;
    private readonly HttpClient _http;
    /// <summary>The keys to try, in order. Never empty once <see cref="Start"/> has run.</summary>
    private readonly List<string> _keys;
    /// <summary>Which key requests are currently signed with. Advances only when one is refused for quota.</summary>
    private int _keyIndex;
    /// <summary>The OpenAI-format conversation. Index 0 is always the system prompt.</summary>
    private readonly List<JsonObject> _history = new();
    private readonly Dictionary<string, TaskCompletionSource<JsonObject>> _permissions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _turnGate = new(1, 1);
    private CancellationTokenSource _turn = new();
    private string _model;
    private string _permissionMode;
    private bool _disposed;
    /// <summary>Tools the user chose "always allow" for, for the life of this session.</summary>
    private readonly HashSet<string> _alwaysAllowed = new(StringComparer.OrdinalIgnoreCase);

    public GlmSession(GlmSessionOptions options)
    {
        _options = options;
        // Normalized, not merely defaulted: the caller's model can be another provider's (a shared settings slot
        // did exactly that), and an id Baseten does not serve comes back as a 404 that kills the whole turn.
        _model = GlmPreset.NormalizeModel(options.Model);
        _permissionMode = options.PermissionMode;

        _keys = options.ApiKeys.Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.Ordinal).ToList();

        _http = new HttpClient
        {
            // Deliberately long: GLM thinks for a long time before it answers, and a turn that is merely slow
            // must not be reported as a failure.
            Timeout = TimeSpan.FromMinutes(10),
        };
        // Authorization is set per request rather than on the client, because the key can change mid-turn.
    }

    /// <summary>How many keys this session can fall back through. Surfaced so the UI can say "3 keys".</summary>
    public int KeyCount => _keys.Count;

    private string CurrentKey => _keys.Count == 0 ? "" : _keys[Math.Min(_keyIndex, _keys.Count - 1)];

    // ---------------- lifecycle ----------------

    public void Start()
    {
        SessionId = Guid.NewGuid().ToString("n");
        Models = BuildModels();
        _history.Add(new JsonObject
        {
            // Must be "system": a "developer" message is accepted with a 200 and then silently ignored.
            ["role"] = GlmPreset.DeveloperRole,
            ["content"] = SystemPrompt(),
        });

        Emit(new JsonObject
        {
            ["type"] = "system",
            ["subtype"] = "init",
            ["session_id"] = SessionId,
            ["model"] = _model,
            ["permissionMode"] = _permissionMode,
        });
        Initialized?.Invoke();
    }

    private string SystemPrompt()
    {
        var builder = new StringBuilder();
        builder.Append("You are a coding agent running inside VibeCode on Windows. You are working in the folder ")
            .Append(_options.Cwd).Append(".\n\n")
            .Append("Use the provided tools to do real work rather than describing what you would do. ")
            .Append("Read a file before you edit it, and prefer Edit over Write when changing part of a file. ")
            .Append("Paths may be given relative to the working folder. Never guess a file's contents - read it.\n\n")
            .Append("Do not write tool calls as text or XML in your reply. Emit real tool calls; anything you type ")
            .Append("as prose is shown to the user and does not run.\n\n")
            .Append("When you have finished, say briefly what you changed.");
        if (!string.IsNullOrWhiteSpace(_options.AppendSystemPrompt))
            builder.Append("\n\n").Append(_options.AppendSystemPrompt);
        return builder.ToString();
    }

    private static JsonArray BuildModels()
    {
        var array = new JsonArray();
        foreach (var (value, display, description) in GlmPreset.Models)
        {
            array.Add(new JsonObject
            {
                ["value"] = value,
                ["displayName"] = display,
                ["description"] = description,
                // No effort picker: Baseten does not list reasoning_effort among GLM's supported features, and
                // the endpoint accepts one anyway without honouring it. See GlmPreset.SupportsEffort.
                ["supportsEffort"] = GlmPreset.SupportsEffort,
                ["effortLevels"] = new JsonArray(),
            });
        }
        return array;
    }

    // ---------------- the turn ----------------

    public void SendUser(JsonNode content)
    {
        if (_disposed) return;
        var text = TextOf(content);
        if (string.IsNullOrWhiteSpace(text)) return;
        _history.Add(new JsonObject { ["role"] = "user", ["content"] = text });
        _ = Task.Run(RunTurnAsync);
    }

    private async Task RunTurnAsync()
    {
        // One turn at a time. A second prompt arriving mid-turn queues behind this one rather than interleaving
        // two tool loops over the same working directory.
        await _turnGate.WaitAsync().ConfigureAwait(false);
        var cancel = ResetTurnCancellation();
        // One user prompt can cost many requests once tools start looping, and the usage page records ONE row per
        // turn - so the whole loop's tokens are summed and reported together at the end.
        var turnUsage = new Usage();
        try
        {
            for (var round = 0; round < MaxToolRoundTrips; round++)
            {
                cancel.ThrowIfCancellationRequested();
                var reply = await StreamOnceAsync(cancel).ConfigureAwait(false);
                turnUsage.Add(reply.Usage);

                // Record exactly what the model said, tool calls included, or the next request loses the thread.
                _history.Add(reply.ToHistory());

                if (reply.ToolCalls.Count == 0)
                {
                    EmitResult(success: true, null, usage: turnUsage);
                    return;
                }

                foreach (var call in reply.ToolCalls)
                {
                    cancel.ThrowIfCancellationRequested();
                    var outcome = await RunToolAsync(call, cancel).ConfigureAwait(false);
                    _history.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = call.Id,
                        ["content"] = outcome.Text,
                    });
                }
            }
            EmitResult(success: false, $"Stopped after {MaxToolRoundTrips} tool calls in one turn.", turnUsage);
        }
        catch (OperationCanceledException)
        {
            // A user interrupt is a normal way for a turn to end, not a provider failure. Tokens already spent
            // are still reported - the user was billed for them whether or not they waited for the answer.
            EmitResult(success: true, null, turnUsage, interrupted: true);
        }
        catch (Exception ex)
        {
            EmitResult(success: false, Explain(ex), turnUsage);
        }
        finally
        {
            _turnGate.Release();
        }
    }

    private CancellationToken ResetTurnCancellation()
    {
        var fresh = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _turn, fresh);
        previous.Dispose();
        return fresh.Token;
    }

    /// <summary>Turns a provider exception into something worth showing a user.</summary>
    private static string Explain(Exception ex) => ex switch
    {
        HttpRequestException http => $"Could not reach the GLM endpoint: {http.Message}",
        TaskCanceledException => "The GLM endpoint did not answer in time.",
        _ => ex.Message,
    };

    private void EmitResult(bool success, string? error, Usage usage = default, bool interrupted = false)
    {
        var result = new JsonObject
        {
            ["type"] = "result",
            ["subtype"] = success ? "success" : "error",
            ["session_id"] = SessionId,
        };
        // Anthropic's field names, because that is the envelope the whole app reads. Without this block the turn
        // never reaches the usage log and GLM is absent from every chart.
        if (usage.Any)
            result["usage"] = new JsonObject
            {
                ["input_tokens"] = usage.Input,
                ["cache_creation_input_tokens"] = 0,
                ["cache_read_input_tokens"] = usage.CacheRead,
                ["output_tokens"] = usage.Output,
            };
        if (!success)
        {
            result["is_error"] = true;
            result["result"] = error ?? "The turn failed.";
        }
        if (interrupted) result["terminal_reason"] = "aborted_streaming";
        Emit(result);
    }

    // ---------------- streaming ----------------

    /// <summary>Tokens billed by one request, in the app's own three-bucket input split.</summary>
    private struct Usage
    {
        public double Input;
        public double CacheRead;
        public double Output;

        public void Add(Usage other)
        {
            Input += other.Input;
            CacheRead += other.CacheRead;
            Output += other.Output;
        }

        public readonly bool Any => Input + CacheRead + Output > 0;
    }

    /// <summary>What one assistant message came back as, once its stream has finished.</summary>
    private sealed class Reply
    {
        public string Text = "";
        public string Reasoning = "";
        public Usage Usage;
        public readonly List<ToolCall> ToolCalls = new();

        /// <summary>The OpenAI-shaped assistant message to append to history.</summary>
        public JsonObject ToHistory()
        {
            var message = new JsonObject { ["role"] = "assistant", ["content"] = Text };
            if (ToolCalls.Count == 0) return message;
            var calls = new JsonArray();
            foreach (var call in ToolCalls)
                calls.Add(new JsonObject
                {
                    ["id"] = call.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = call.Name,
                        ["arguments"] = call.Arguments.Length == 0 ? "{}" : call.Arguments,
                    },
                });
            message["tool_calls"] = calls;
            return message;
        }
    }

    private sealed class ToolCall
    {
        public string Id = "";
        public string Name = "";
        public string Arguments = "";
    }

    /// <summary>How many times a request that failed BEFORE streaming anything is retried.</summary>
    private const int MaxTransientRetries = 3;

    /// <summary>
    /// Send one request and stream its answer.
    ///
    /// Transient upstream failures are retried, but only while the failure is still a bare HTTP status - once a
    /// single delta has been emitted to the transcript, retrying would replay text the user has already seen, so
    /// from that point a failure is final.
    /// </summary>
    private async Task<Reply> StreamOnceAsync(CancellationToken cancel)
    {
        var delay = TimeSpan.FromSeconds(3);
        for (var attempt = 0; ; attempt++)
        {
            cancel.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"{_options.BaseUrl.TrimEnd('/')}/chat/completions");
            var body = new JsonObject
            {
                ["model"] = _model,
                ["stream"] = true,
                // Generous on purpose: thinking is billed against this budget, so a small cap yields an EMPTY
                // reply rather than a short one. See GlmPreset.DefaultMaxTokensPerTurn.
                ["max_tokens"] = GlmPreset.DefaultMaxTokensPerTurn,
                ["messages"] = History(),
                ["tools"] = GlmTools.Schema(),
                ["tool_choice"] = "auto",
                // Without this a streamed response reports no token counts at all, and the usage page would show
                // GLM turns as free of charge AND free of tokens - i.e. invisible.
                ["stream_options"] = new JsonObject { ["include_usage"] = true },
            };
            // Deliberately no reasoning_effort: Baseten answers 200 to one and ignores it for GLM.
            request.Content = new StringContent(body.ToJsonString(), new UTF8Encoding(false), "application/json");
            request.Headers.Authorization = new AuthenticationHeaderValue(GlmPreset.AuthScheme, CurrentKey);

            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                // Defensive: Baseten's 403 for a bad key advertises a Content-Length it then does not send, so
                // reading the body throws. The STATUS is what DescribeFailure needs, and losing the body to an
                // exception here would turn "Baseten rejected this API key" into a generic network error.
                string text;
                try { text = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested) { throw; }
                catch { text = ""; }

                // These refusals are about the KEY, not the request: 429 means this key is out of allowance,
                // 401/403 mean it was revoked or mistyped (Baseten answers 403 for a bad key, 401 for none).
                // Either way another saved key may still work, so move to it and retry immediately - waiting out
                // a backoff is pointless when a different credential is sitting right there.
                if (IsKeyProblem(status) && _keyIndex + 1 < _keys.Count)
                {
                    _keyIndex++;
                    Emit(new JsonObject
                    {
                        ["type"] = "system",
                        ["subtype"] = "retry",
                        ["message"] = status == 429
                            ? $"That API key is rate limited — switching to key {_keyIndex + 1} of {_keys.Count}."
                            : $"That API key was rejected — switching to key {_keyIndex + 1} of {_keys.Count}.",
                    });
                    attempt--;   // keep the transient budget for genuinely transient failures
                    continue;
                }

                if (attempt < MaxTransientRetries && IsTransient(status))
                {
                    Emit(new JsonObject
                    {
                        ["type"] = "system",
                        ["subtype"] = "retry",
                        ["message"] = $"{DescribeFailure(status, text)} Retrying…",
                    });
                    await Task.Delay(delay, cancel).ConfigureAwait(false);
                    delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
                    continue;
                }
                throw new HttpRequestException(DescribeFailure(status, text));
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return await ReadStreamAsync(reader, cancel).ConfigureAwait(false);
        }
    }

    /// <summary>429 is the endpoint catching its breath; 5xx is an upstream failure worth one more try.</summary>
    private static bool IsTransient(int status) => status == 429 || status >= 500;

    /// <summary>A refusal that another saved key might not share: out of allowance, revoked, or mistyped.</summary>
    private static bool IsKeyProblem(int status) => status is 401 or 403 or 429;

    /// <summary>
    /// Consume the SSE body, emitting Anthropic-shaped stream events as it goes.
    ///
    /// Block indices are assigned in the order blocks actually open, because the transcript addresses live blocks
    /// by index: thinking is block 0 only if the model thinks before it speaks, which it usually but not always does.
    /// </summary>
    private async Task<Reply> ReadStreamAsync(StreamReader reader, CancellationToken cancel)
    {
        var reply = new Reply();
        var byIndex = new Dictionary<int, ToolCall>();
        var nextBlock = 0;
        int? thinkingBlock = null;
        int? textBlock = null;

        Emit(new JsonObject { ["type"] = "stream_event", ["event"] = new JsonObject { ["type"] = "message_start" } });

        while (await reader.ReadLineAsync(cancel).ConfigureAwait(false) is { } line)
        {
            cancel.ThrowIfCancellationRequested();
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var payload = line[5..].Trim();
            if (payload.Length == 0 || payload == "[DONE]") continue;

            JsonNode? node;
            try { node = JsonNode.Parse(payload); }
            catch (JsonException) { continue; }   // a keep-alive or partial frame

            // Some gateways report a mid-stream failure as a data frame rather than an HTTP status.
            if (node?["error"] is { } streamError)
                throw new HttpRequestException(ErrorMessage(streamError) ?? "The endpoint reported an error.");

            // Baseten repeats a running usage object on EVERY chunk and sends a final choice-less frame carrying
            // the totals. Read before the choice-less frames are skipped below; last write wins, which is the
            // final total.
            if (node?["usage"] is JsonObject usage) reply.Usage = ReadUsage(usage);

            // Not every frame carries a choice: the usage/keep-alive frames send "choices":[], and indexing an
            // empty JsonArray throws rather than returning null - which surfaces as a bare "Index was out of
            // range" killing the whole turn.
            if (node?["choices"] is not JsonArray choices || choices.Count == 0) continue;
            var delta = choices[0]?["delta"];
            if (delta is null) continue;

            if (StringOf(delta["reasoning_content"]) is { Length: > 0 } thinking)
            {
                if (thinkingBlock is null)
                {
                    thinkingBlock = nextBlock++;
                    EmitBlockStart(thinkingBlock.Value, new JsonObject { ["type"] = "thinking", ["thinking"] = "" });
                }
                reply.Reasoning += thinking;
                EmitBlockDelta(thinkingBlock.Value,
                    new JsonObject { ["type"] = "thinking_delta", ["thinking"] = thinking });
            }

            if (StringOf(delta["content"]) is { Length: > 0 } content)
            {
                // Thinking is finished the moment prose starts; close it so the UI stops the spinner on it.
                if (thinkingBlock is not null && textBlock is null) EmitBlockStop(thinkingBlock.Value);
                if (textBlock is null)
                {
                    textBlock = nextBlock++;
                    EmitBlockStart(textBlock.Value, new JsonObject { ["type"] = "text", ["text"] = "" });
                }
                reply.Text += content;
                EmitBlockDelta(textBlock.Value, new JsonObject { ["type"] = "text_delta", ["text"] = content });
            }

            if (delta["tool_calls"] is JsonArray calls) AccumulateToolCalls(calls, byIndex);
        }

        if (textBlock is not null) EmitBlockStop(textBlock.Value);
        else if (thinkingBlock is not null) EmitBlockStop(thinkingBlock.Value);

        reply.ToolCalls.AddRange(byIndex.OrderBy(kv => kv.Key).Select(kv => kv.Value)
            .Where(call => call.Name.Length > 0));
        return reply;
    }

    /// <summary>
    /// Translate OpenAI's token counts into the app's three input buckets.
    ///
    /// <c>prompt_tokens</c> is the whole input side INCLUDING anything served from cache, so the cached part is
    /// subtracted out rather than added on - counting it twice would inflate every GLM row on the usage page.
    /// There is no cache-write concept here, so that bucket stays zero.
    /// </summary>
    private static Usage ReadUsage(JsonObject usage)
    {
        var prompt = DoubleOf(usage["prompt_tokens"]);
        var cached = DoubleOf(usage["prompt_tokens_details"]?["cached_tokens"]);
        cached = Math.Clamp(cached, 0, prompt);
        return new Usage
        {
            Input = prompt - cached,
            CacheRead = cached,
            Output = DoubleOf(usage["completion_tokens"]),
        };
    }

    private static double DoubleOf(JsonNode? node)
    {
        if (node is not JsonValue value) return 0;
        if (value.TryGetValue<double>(out var number)) return number;
        return double.TryParse(value.ToString(), out var parsed) ? parsed : 0;
    }

    /// <summary>
    /// Tool calls arrive spread across frames: the first carries id and name, later ones append argument text a
    /// few characters at a time, all keyed by <c>index</c>.
    /// </summary>
    private static void AccumulateToolCalls(JsonArray calls, Dictionary<int, ToolCall> byIndex)
    {
        foreach (var item in calls.OfType<JsonObject>())
        {
            var index = item["index"]?.GetValue<int>() ?? 0;
            if (!byIndex.TryGetValue(index, out var call)) byIndex[index] = call = new ToolCall();
            if (StringOf(item["id"]) is { Length: > 0 } id) call.Id = id;
            if (item["function"] is JsonObject function)
            {
                if (StringOf(function["name"]) is { Length: > 0 } name) call.Name = name;
                if (StringOf(function["arguments"]) is { Length: > 0 } arguments) call.Arguments += arguments;
            }
        }
    }

    private void EmitBlockStart(int index, JsonObject block) => Emit(new JsonObject
    {
        ["type"] = "stream_event",
        ["event"] = new JsonObject
        {
            ["type"] = "content_block_start", ["index"] = index, ["content_block"] = block,
        },
    });

    private void EmitBlockDelta(int index, JsonObject delta) => Emit(new JsonObject
    {
        ["type"] = "stream_event",
        ["event"] = new JsonObject { ["type"] = "content_block_delta", ["index"] = index, ["delta"] = delta },
    });

    private void EmitBlockStop(int index) => Emit(new JsonObject
    {
        ["type"] = "stream_event",
        ["event"] = new JsonObject { ["type"] = "content_block_stop", ["index"] = index },
    });

    // ---------------- tools ----------------

    private async Task<ToolOutcome> RunToolAsync(ToolCall call, CancellationToken cancel)
    {
        var input = ParseArguments(call.Arguments);
        var id = string.IsNullOrEmpty(call.Id) ? Guid.NewGuid().ToString("n") : call.Id;

        Emit(new JsonObject
        {
            ["type"] = "assistant",
            ["message"] = new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "tool_use", ["id"] = id, ["name"] = call.Name, ["input"] = input.DeepClone(),
                }),
            },
        });

        ToolOutcome outcome;
        var decision = await DecideAsync(call.Name, id, input, cancel).ConfigureAwait(false);
        if (!decision.Allowed)
        {
            outcome = ToolOutcome.Fail(decision.Reason ?? "The user did not allow this.");
        }
        else
        {
            outcome = await GlmTools.RunAsync(call.Name, input, _options.Cwd, cancel).ConfigureAwait(false);
        }

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
                    ["content"] = outcome.Text,
                    ["is_error"] = outcome.IsError,
                }),
            },
        });
        return outcome;
    }

    private sealed record Decision(bool Allowed, string? Reason);

    /// <summary>
    /// Whether a tool may run: the chat's permission mode first, then the user, via the same approval card every
    /// other provider raises.
    /// </summary>
    private async Task<Decision> DecideAsync(string tool, string toolUseId, JsonObject input, CancellationToken cancel)
    {
        if (!GlmTools.NeedsApproval(tool)) return new Decision(true, null);
        if (_alwaysAllowed.Contains(tool)) return new Decision(true, null);

        switch (_permissionMode)
        {
            case "bypassPermissions":
                return new Decision(true, null);
            case "acceptEdits" when tool is "Write" or "Edit":
                return new Decision(true, null);
            case "plan":
                return new Decision(false,
                    "Plan mode is on, so nothing may be changed yet. Describe the plan instead and wait for approval.");
        }

        var requestId = Guid.NewGuid().ToString("n");
        var pending = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_permissions) _permissions[requestId] = pending;

        PermissionRequested?.Invoke(new PermissionRequest
        {
            RequestId = requestId,
            ToolName = tool,
            Input = input.DeepClone(),
            ToolUseId = toolUseId,
            // This session runs in-process against an endpoint VibeCode dialled itself; there is no external
            // runtime whose identity could be spoofed, so the envelope is trustworthy by construction.
            IntegrityVerified = true,
        });

        JsonObject answer;
        using (cancel.Register(() => pending.TrySetCanceled()))
        {
            try { answer = await pending.Task.ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                lock (_permissions) _permissions.Remove(requestId);
                PermissionCancelled?.Invoke(requestId);
                throw;
            }
        }
        lock (_permissions) _permissions.Remove(requestId);

        var behavior = StringOf(answer["behavior"]);
        if (string.Equals(behavior, "allow", StringComparison.OrdinalIgnoreCase))
        {
            if (answer["updatedPermissions"] is JsonArray || answer["always"]?.GetValue<bool>() == true)
                _alwaysAllowed.Add(tool);
            return new Decision(true, null);
        }
        return new Decision(false,
            StringOf(answer["message"]) is { Length: > 0 } message
                ? message
                : "The user declined this tool call. Do not retry it; ask what to do instead.");
    }

    public void RespondPermission(string requestId, JsonObject result, string? toolUseId)
    {
        TaskCompletionSource<JsonObject>? pending;
        lock (_permissions) _permissions.TryGetValue(requestId, out pending);
        pending?.TrySetResult(result);
    }

    private static JsonObject ParseArguments(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return new JsonObject();
        try { return JsonNode.Parse(arguments) as JsonObject ?? new JsonObject(); }
        catch (JsonException)
        {
            // Malformed JSON is the model's mistake, and the tool layer turns this into a readable failure
            // rather than a crashed turn.
            return new JsonObject { ["__malformed_arguments"] = arguments };
        }
    }

    // ---------------- controls ----------------

    public Task InterruptAsync()
    {
        try { _turn.Cancel(); } catch (ObjectDisposedException) { /* no turn running */ }
        return Task.CompletedTask;
    }

    public Task SetPermissionModeAsync(string mode)
    {
        _permissionMode = mode;
        return Task.CompletedTask;
    }

    /// <summary><paramref name="effort"/> is accepted and ignored: GLM has no effort knob here, and the session
    /// deliberately never puts one on the wire. See <see cref="GlmPreset.SupportsEffort"/>.</summary>
    public Task SetModelAsync(string? model, string? effort = null)
    {
        if (!string.IsNullOrWhiteSpace(model) && model != "default")
        {
            _model = GlmPreset.NormalizeModel(model);
            if (_history.Count > 0) _history[0] = new JsonObject
            {
                ["role"] = GlmPreset.DeveloperRole,
                ["content"] = SystemPrompt(),
            };
        }
        // Mirrors the CLIs: a model change re-announces the session so the header and picker follow it.
        Emit(new JsonObject
        {
            ["type"] = "system",
            ["subtype"] = "init",
            ["session_id"] = SessionId,
            ["model"] = _model,
            ["permissionMode"] = _permissionMode,
        });
        return Task.CompletedTask;
    }

    // ---------------- plumbing ----------------

    private JsonArray History()
    {
        var array = new JsonArray();
        foreach (var message in _history) array.Add(message.DeepClone());
        return array;
    }

    private string DescribeFailure(int status, string body)
    {
        var reason = TryErrorMessage(body);
        return status switch
        {
            401 => "No API key was sent to the GLM endpoint.",
            403 => _keys.Count > 1
                ? $"All {_keys.Count} of your Baseten API keys were rejected."
                : "Baseten rejected this API key.",
            404 => $"Baseten has no model called \"{_model}\".",
            429 => _keys.Count > 1
                ? $"All {_keys.Count} of your Baseten keys are rate limited. Wait a moment and try again."
                : "Rate limited by Baseten. Wait a moment, or add a second API key so VibeCode can fall back to it.",
            >= 500 => $"The GLM endpoint failed ({status}). {reason}".TrimEnd(),
            _ => reason is { Length: > 0 } ? reason : $"The endpoint returned {status}.",
        };
    }

    private static string? TryErrorMessage(string body)
    {
        try { return ErrorMessage(JsonNode.Parse(body)?["error"]); }
        catch (JsonException) { return null; }
    }

    private static string? ErrorMessage(JsonNode? error) => error switch
    {
        null => null,
        JsonValue value => value.ToString(),
        _ => StringOf(error["message"]) ?? error.ToString(),
    };

    private static string? StringOf(JsonNode? node) =>
        node is JsonValue value
            ? value.TryGetValue<string>(out var text) ? text : null
            : null;

    private static string TextOf(JsonNode content)
    {
        if (content is JsonValue value) return value.TryGetValue<string>(out var text) ? text : content.ToString();
        if (content is JsonArray array)
            return string.Join("\n", array.OfType<JsonObject>()
                .Where(block => StringOf(block["type"]) == "text")
                .Select(block => StringOf(block["text"]) ?? ""));
        if (content is JsonObject obj && StringOf(obj["text"]) is { } single) return single;
        return content.ToString();
    }

    private void Emit(JsonNode node) => MessageReceived?.Invoke(node);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _turn.Cancel(); } catch (ObjectDisposedException) { /* already gone */ }
        lock (_permissions)
        {
            foreach (var (id, pending) in _permissions.ToList())
            {
                pending.TrySetCanceled();
                PermissionCancelled?.Invoke(id);
            }
            _permissions.Clear();
        }
        _http.Dispose();
        _turnGate.Dispose();
        try { _turn.Dispose(); } catch { /* already disposed */ }
        Exited?.Invoke(0, "");
    }
}
