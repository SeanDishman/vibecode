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
    public IReadOnlyList<string> ApiKeyAccountIds { get; init; } = [];
    public string Backend { get; init; } = GlmPreset.Baseten;
    /// <summary>Optional endpoint override for local protocol tests.</summary>
    public string? BaseUrl { get; init; }
    public string? Model { get; init; }
    public string? Effort { get; init; }
    public string PermissionMode { get; init; } = "default";
    public string? AppendSystemPrompt { get; init; }
    public string? BridgeMcpPipe { get; init; }
    /// <summary>The session to continue. Its saved conversation is loaded from <see cref="HistoryDirectory"/>.</summary>
    public string? Resume { get; init; }
    /// <summary>Where the conversation is saved after every step. Null keeps it in memory only.</summary>
    public string? HistoryDirectory { get; init; }
}

/// <summary>
/// A coding session spoken natively against GLM on Baseten or Z.ai's official API.
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
public sealed class GlmSession : ICodingSession, ISteerableSession
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

    /// <summary>
    /// Consecutive copies of the exact same failing tool batch allowed before the turn is stopped.
    ///
    /// GLM is trained for long-horizon work and Z.ai does not impose a 60-tool boundary. The old fixed ceiling
    /// therefore killed healthy turns solely because they were large. Keep the runaway protection targeted at the
    /// failure mode it was meant for instead: a model retrying an unchanged failed edit/shell call forever.
    /// </summary>
    private const int MaxRepeatedFailedToolBatches = 8;

    private readonly GlmSessionOptions _options;
    private readonly HttpClient _http;
    private readonly string _backend;
    private readonly string _baseUrl;
    /// <summary>The keys to try, in order. Never empty once <see cref="Start"/> has run.</summary>
    private readonly List<string> _keys;
    private readonly List<string?> _accountIds = [];
    /// <summary>Which key requests are currently signed with. Advances only when one is refused for quota.</summary>
    private int _keyIndex;
    /// <summary>The OpenAI-format conversation. Index 0 is always the system prompt.</summary>
    private readonly List<JsonObject> _history = new();
    /// <summary>Tool calls that failed, saved with the history so a replayed card is still marked as an error.</summary>
    private readonly HashSet<string> _failedToolCalls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskCompletionSource<JsonObject>> _permissions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _turnGate = new(1, 1);
    /// <summary>Guards <see cref="_turnLive"/> and <see cref="_steers"/>, so a steer either lands in the running turn
    /// or is refused - never left stranded by a turn that finished in between.</summary>
    private readonly object _steerGate = new();
    private readonly Queue<string> _steers = new();
    private bool _turnLive;
    private CancellationTokenSource _turn = new();
    private string _model;
    private string _effort;
    private string _permissionMode;
    private bool _disposed;
    /// <summary>Tools the user chose "always allow" for, for the life of this session.</summary>
    private readonly HashSet<string> _alwaysAllowed = new(StringComparer.OrdinalIgnoreCase);

    public GlmSession(GlmSessionOptions options, HttpMessageHandler? handler = null)
    {
        _options = options;
        _backend = GlmPreset.NormalizeBackend(options.Backend);
        _baseUrl = options.BaseUrl ?? GlmPreset.BaseUrlFor(_backend);
        _model = GlmPreset.NormalizeModel(options.Model, _backend);
        _effort = GlmPreset.NormalizeEffort(options.Effort);
        _permissionMode = options.PermissionMode;

        _keys = [];
        for (var i = 0; i < options.ApiKeys.Count; i++)
        {
            var key = options.ApiKeys[i];
            if (string.IsNullOrWhiteSpace(key) || _keys.Contains(key, StringComparer.Ordinal)) continue;
            _keys.Add(key);
            _accountIds.Add(i < options.ApiKeyAccountIds.Count ? options.ApiKeyAccountIds[i] : null);
        }

        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        // GLM may think for a long time before it answers.
        _http.Timeout = TimeSpan.FromMinutes(10);
        // Authorization is set per request rather than on the client, because the key can change mid-turn.
    }

    /// <summary>How many keys this session can fall back through. Surfaced so the UI can say "3 keys".</summary>
    public int KeyCount => _keys.Count;
    public string? ActiveAccountId => _accountIds.Count == 0 ? null : _accountIds[Math.Min(_keyIndex, _accountIds.Count - 1)];

    private string CurrentKey => _keys.Count == 0 ? "" : _keys[Math.Min(_keyIndex, _keys.Count - 1)];

    // ---------------- lifecycle ----------------

    public void Start()
    {
        // A resumed chat keeps its id even when nothing was saved for it (chats from before history was kept), so
        // the sidebar row, its title and its metadata stay attached to the same conversation.
        var resume = Services.GlmTranscriptStore.IsValidId(_options.Resume) ? _options.Resume : null;
        SessionId = resume ?? Guid.NewGuid().ToString("n");
        Models = BuildModels();
        _history.Add(new JsonObject
        {
            // Must be "system": a "developer" message is accepted with a 200 and then silently ignored.
            ["role"] = GlmPreset.DeveloperRole,
            ["content"] = SystemPrompt(),
        });
        // The saved system prompt is not restored: the one just built reflects this launch's settings.
        if (resume is not null && _options.HistoryDirectory is { } directory
            && Services.GlmTranscriptStore.Load(directory, resume) is { } saved)
        {
            _history.AddRange(saved.Messages);
            _failedToolCalls.UnionWith(saved.FailedToolCalls);
        }

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

    private JsonArray BuildModels()
    {
        var array = new JsonArray();
        foreach (var (value, display, description) in GlmPreset.ModelsFor(_backend))
        {
            array.Add(new JsonObject
            {
                ["value"] = value,
                ["displayName"] = display,
                ["description"] = description,
                ["supportsEffort"] = GlmPreset.IsZai(_backend),
                ["supportedEffortLevels"] = new JsonArray(GlmPreset.EffortsFor(_backend).Select(e => (JsonNode?)JsonValue.Create(e)).ToArray()),
                ["resolvedModel"] = value,
            });
        }
        return array;
    }

    private string WireModel => _model;

    // ---------------- the turn ----------------

    public void SendUser(JsonNode content)
    {
        if (_disposed) return;
        var text = TextOf(content);
        if (string.IsNullOrWhiteSpace(text)) return;
        lock (_steerGate)
        {
            // Steers left behind by a turn that stopped before taking them (see RunTurnAsync's finally).
            if (_steers.Count > 0) text = string.Join("\n\n", _steers) + "\n\n" + text;
            _steers.Clear();
        }
        _history.Add(new JsonObject { ["role"] = "user", ["content"] = text });
        _ = Task.Run(RunTurnAsync);
    }

    /// <summary>This session IS the agent loop, so a running turn can always take more guidance: it joins the
    /// conversation at the loop's next step - after the current tool results, or in place of ending the turn.</summary>
    public bool CanSteer
    {
        get { lock (_steerGate) return !_disposed && _turnLive; }
    }

    public Task SteerAsync(JsonNode content)
    {
        var text = TextOf(content);
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("The steering message is empty.");
        lock (_steerGate)
        {
            if (_disposed || !_turnLive) throw new InvalidOperationException("GLM has no active turn to steer.");
            _steers.Enqueue(text);
        }
        return Task.CompletedTask;
    }

    /// <summary>Move pending steers into the conversation. Only between whole steps: after a reply's tool results, or
    /// after a reply with none, which is where a user message is valid in the chat-completions format.</summary>
    private bool TakeSteers(bool endTurnIfNone)
    {
        lock (_steerGate)
        {
            if (_steers.Count == 0)
            {
                if (endTurnIfNone) _turnLive = false;
                return false;
            }
            while (_steers.TryDequeue(out var text))
                _history.Add(new JsonObject { ["role"] = "user", ["content"] = text });
            return true;
        }
    }

    private async Task RunTurnAsync()
    {
        // One turn at a time. A second prompt arriving mid-turn queues behind this one rather than interleaving
        // two tool loops over the same working directory.
        await _turnGate.WaitAsync().ConfigureAwait(false);
        lock (_steerGate) _turnLive = true;
        // The same liveness signal Claude and Codex send: the chat re-reads CanSteer, so Steer enables only once this
        // loop can actually take guidance.
        Emit(new JsonObject { ["type"] = "system", ["subtype"] = "turn_activity", ["active"] = true });
        var cancel = ResetTurnCancellation();
        // Show each request's reported usage while tools run, then commit the whole loop once at turn end.
        // Keep the current request in this total too so an interrupted stream retains its reported tokens.
        var turnUsage = new Usage();
        try
        {
            if (_keys.Count == 0)
                throw new InvalidOperationException($"No {GlmPreset.BackendName(_backend)} key is saved. Add a GLM account in the account manager.");
            string? lastFailedToolBatch = null;
            var repeatedFailedToolBatches = 0;
            while (true)
            {
                cancel.ThrowIfCancellationRequested();
                var completedUsage = turnUsage;
                var reply = await StreamOnceAsync(cancel, usage =>
                {
                    turnUsage = completedUsage;
                    turnUsage.Add(usage);
                    var snapshot = turnUsage.ToJson();
                    // Context occupancy belongs to this request, not the sum of every tool round-trip.
                    snapshot["context_input_tokens"] = usage.Input + usage.CacheRead;
                    Emit(new JsonObject
                    {
                        ["type"] = "system", ["subtype"] = "usage_update", ["usage"] = snapshot,
                    });
                }).ConfigureAwait(false);

                // Record exactly what the model said, tool calls included, or the next request loses the thread.
                _history.Add(reply.ToHistory(GlmPreset.IsZai(_backend)));

                if (reply.ToolCalls.Count == 0)
                {
                    // Guidance that arrived while this final answer streamed keeps the turn going, so it is answered
                    // here rather than left for a turn that would never start.
                    if (TakeSteers(endTurnIfNone: true)) continue;
                    EmitResult(success: true, null, usage: turnUsage);
                    return;
                }

                var toolBatch = ToolBatchKey(reply.ToolCalls);
                var everyToolFailed = true;
                foreach (var call in reply.ToolCalls)
                {
                    cancel.ThrowIfCancellationRequested();
                    var outcome = await RunToolAsync(call, cancel).ConfigureAwait(false);
                    everyToolFailed &= outcome.IsError;
                    if (outcome.IsError) _failedToolCalls.Add(call.Id);
                    _history.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = call.Id,
                        ["content"] = outcome.Text,
                    });
                }
                TakeSteers(endTurnIfNone: false);
                // Saved per round, not only at turn end, so a crash or force quit mid-task keeps the work done so far.
                SaveHistory();

                if (!everyToolFailed)
                {
                    lastFailedToolBatch = null;
                    repeatedFailedToolBatches = 0;
                    continue;
                }

                if (string.Equals(lastFailedToolBatch, toolBatch, StringComparison.Ordinal))
                    repeatedFailedToolBatches++;
                else
                {
                    lastFailedToolBatch = toolBatch;
                    repeatedFailedToolBatches = 1;
                }

                if (repeatedFailedToolBatches < MaxRepeatedFailedToolBatches) continue;
                EmitResult(success: false,
                    $"GLM repeated the same failing tool request {MaxRepeatedFailedToolBatches} times. " +
                    "The turn was stopped to prevent a runaway loop; send another message with different instructions to continue.",
                    turnUsage);
                return;
            }
        }
        catch (OperationCanceledException)
        {
            // A user interrupt is a normal way for a turn to end, not a provider failure. Tokens already spent
            // are still reported - the user was billed for them whether or not they waited for the answer.
            EmitResult(success: true, null, turnUsage, interrupted: true);
        }
        catch (Exception ex)
        {
            EmitResult(success: false, Explain(ex), turnUsage, providerError: (ex as GlmRequestException)?.Error);
        }
        finally
        {
            // A turn stopped early (interrupt, error) may still hold steers. They stay queued and lead the next
            // prompt, which keeps the user's words without wedging them between a tool call and its results.
            lock (_steerGate) _turnLive = false;
            SaveHistory();
            _turnGate.Release();
        }
    }

    /// <summary>
    /// Write the conversation to disk so a reopened chat can continue it. Runs on the turn thread only, which is the
    /// only writer of the messages after the system prompt.
    /// </summary>
    private void SaveHistory()
    {
        if (_options.HistoryDirectory is not { } directory || SessionId is null) return;
        // Index loop rather than LINQ: SetModelAsync may replace the system prompt at index 0 from the UI thread, and
        // that bumps the list version an enumerator would throw on.
        var messages = new List<JsonObject>();
        for (var i = 1; i < _history.Count; i++) messages.Add(_history[i]);
        if (messages.Count == 0) return;
        try
        {
            Services.GlmTranscriptStore.Save(directory, new Services.GlmTranscriptStore.Snapshot(
                SessionId, _options.Cwd, _accountIds.FirstOrDefault(), _model, DateTime.Now, messages,
                _failedToolCalls.ToList()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A locked or full disk must not fail the turn; the next save writes the whole conversation again.
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

    private sealed class GlmRequestException(string message, JsonObject error) : HttpRequestException(message)
    {
        public JsonObject Error { get; } = error;
    }

    private void EmitResult(bool success, string? error, Usage usage = default, bool interrupted = false, JsonObject? providerError = null)
    {
        var result = new JsonObject
        {
            ["type"] = "result",
            ["subtype"] = success ? "success" : "error",
            ["session_id"] = SessionId,
        };
        // Anthropic's field names, because that is the envelope the whole app reads. Without this block the turn
        // never reaches the usage log and GLM is absent from every chart.
        if (usage.Any) result["usage"] = usage.ToJson();
        if (!success)
        {
            result["is_error"] = true;
            result["result"] = error ?? "The turn failed.";
            result["provider_error"] = providerError?.DeepClone();
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

        public readonly JsonObject ToJson() => new()
        {
            ["input_tokens"] = Input,
            ["cache_creation_input_tokens"] = 0,
            ["cache_read_input_tokens"] = CacheRead,
            ["output_tokens"] = Output,
        };
    }

    /// <summary>What one assistant message came back as, once its stream has finished.</summary>
    private sealed class Reply
    {
        public string Text = "";
        public string Reasoning = "";
        public Usage Usage;
        public readonly List<ToolCall> ToolCalls = new();

        /// <summary>The OpenAI-shaped assistant message to append to history.</summary>
        public JsonObject ToHistory(bool preserveThinking)
        {
            var message = new JsonObject { ["role"] = "assistant", ["content"] = Text };
            // Required for Z.ai's interleaved/preserved thinking, including assistant tool-call messages.
            if (preserveThinking && Reasoning.Length > 0) message["reasoning_content"] = Reasoning;
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

    /// <summary>
    /// Exact identity of one assistant tool batch, excluding provider-generated call ids. A changed argument is
    /// progress and resets the failed-loop guard; replaying the same broken request is not.
    /// </summary>
    private static string ToolBatchKey(IEnumerable<ToolCall> calls)
    {
        var key = new StringBuilder();
        foreach (var call in calls)
            key.Append(call.Name).Append('\u001f').Append(call.Arguments).Append('\u001e');
        return key.ToString();
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
    private async Task<Reply> StreamOnceAsync(CancellationToken cancel, Action<Usage> reportUsage)
    {
        var delay = TimeSpan.FromSeconds(3);
        for (var attempt = 0; ; attempt++)
        {
            cancel.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"{_baseUrl.TrimEnd('/')}/chat/completions");
            var body = new JsonObject
            {
                ["model"] = WireModel,
                ["stream"] = true,
                // Generous on purpose: thinking is billed against this budget, so a small cap yields an EMPTY
                // reply rather than a short one. See GlmPreset.DefaultMaxTokensPerTurn.
                ["max_tokens"] = GlmPreset.DefaultMaxTokensPerTurn,
                ["messages"] = History(),
                ["tools"] = ToolSchema(),
                ["tool_choice"] = "auto",
            };
            if (GlmPreset.IsZai(_backend))
            {
                body["thinking"] = new JsonObject { ["type"] = "enabled", ["clear_thinking"] = false };
                body["reasoning_effort"] = _effort;
                body["tool_stream"] = true;
            }
            else
            {
                // Baseten needs this flag; Z.ai includes usage in its final streaming frame by default.
                body["stream_options"] = new JsonObject { ["include_usage"] = true };
            }
            // Baseten does not implement GLM reasoning_effort; only the official API receives it.
            request.Content = new StringContent(body.ToJsonString(), new UTF8Encoding(false), "application/json");
            request.Headers.Authorization = new AuthenticationHeaderValue(GlmPreset.AuthSchemeFor(_backend), CurrentKey);

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
                var accountLimit = GlmPreset.IsZai(_backend) && GlmApiError.Parse(text).IsAccountLimit;

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

                if (!accountLimit && attempt < MaxTransientRetries && IsTransient(status))
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
                var retry = response.Headers.RetryAfter;
                throw new GlmRequestException(DescribeFailure(status, text), new JsonObject
                {
                    ["status"] = status,
                    ["code"] = GlmApiError.Parse(text).Code,
                    ["usage_limit"] = accountLimit,
                    ["retry_after_seconds"] = retry?.Delta?.TotalSeconds,
                    ["retry_at"] = retry?.Date?.ToString("O"),
                });
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return await ReadStreamAsync(reader, cancel, reportUsage).ConfigureAwait(false);
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
    private async Task<Reply> ReadStreamAsync(StreamReader reader, CancellationToken cancel, Action<Usage> reportUsage)
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
            if (node?["usage"] is JsonObject usage)
            {
                var latestUsage = ReadUsage(usage);
                if (!latestUsage.Equals(reply.Usage))
                {
                    reply.Usage = latestUsage;
                    reportUsage(latestUsage);
                }
            }

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

    private JsonArray ToolSchema()
    {
        var schema = GlmTools.Schema();
        if (_options.BridgeMcpPipe is null) return schema;
        foreach (var tool in AgentStatus.Mcp.Bridge.BridgeMcpTools.Create((_, _) => new JsonObject()))
        {
            var definition = tool.Definition;
            schema.Add(new JsonObject { ["type"] = "function", ["function"] = new JsonObject
            {
                ["name"] = tool.Name, ["description"] = definition["description"]!.DeepClone(),
                ["parameters"] = definition["inputSchema"]!.DeepClone(),
            }});
        }
        return schema;
    }

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
        else if (_options.BridgeMcpPipe is { } pipe && (call.Name.StartsWith("bridge_", StringComparison.Ordinal) || call.Name == "chat_set_title"))
        {
            try
            {
                var receipt = await AgentStatus.Mcp.Bridge.BridgeMcpClient.InvokeAsync(pipe, call.Name, input, cancel).ConfigureAwait(false);
                outcome = ToolOutcome.Ok(receipt.ToJsonString());
            }
            catch (AgentStatus.Mcp.Contracts.StatusValidationException ex) { outcome = ToolOutcome.Fail(ex.Message); }
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

    /// <summary>Official Z.ai supports effort; Baseten retains its existing behavior.</summary>
    public Task SetModelAsync(string? model, string? effort = null)
    {
        _effort = GlmPreset.NormalizeEffort(effort);
        if (!string.IsNullOrWhiteSpace(model) && model != "default")
        {
            _model = GlmPreset.NormalizeModel(model, _backend);
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
        var name = GlmPreset.BackendName(_backend);
        if (GlmPreset.IsZai(_backend) && GlmApiError.Parse(body) is { } error
            && (error.Code is not null || error.Message is not null))
            return error.Describe(_backend);
        var reason = TryErrorMessage(body);
        return status switch
        {
            401 or 403 => _keys.Count > 1
                ? $"All {_keys.Count} of your {name} API keys were rejected."
                : $"{name} rejected this API key.",
            404 => $"{name} has no model or endpoint for \"{_model}\".",
            429 => _keys.Count > 1
                ? $"All {_keys.Count} of your {name} keys are rate limited. Wait a moment and try again."
                : $"Rate limited by {name}. Wait a moment and try again.",
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

    private void Emit(JsonNode node)
    {
        // Metadata belongs to this session, not to the account selected globally for the next chat.
        if (node is JsonObject obj && obj["type"]?.ToString() is "system" or "result")
        {
            if (_accountIds.Count > _keyIndex) obj["glm_account_id"] = _accountIds[_keyIndex];
            if (GlmPreset.IsZai(_backend) && obj["subtype"]?.ToString() == "init") obj["effort"] = _effort;
        }
        MessageReceived?.Invoke(node);
    }

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
