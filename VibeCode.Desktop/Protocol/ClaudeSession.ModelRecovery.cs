using System.Text.Json.Nodes;
using VibeCode.Services;

namespace VibeCode.Protocol;

public sealed partial class ClaudeSession
{
    private readonly object _modelRecoveryGate = new();
    private string? _selectedModel;
    private string? _selectedEffort;
    private long _modelSelectionVersion;
    private bool _pendingOpusRestore;
    private string? _lastOpusFallbackUuid;
    private CancellationTokenSource? _opusRecoveryCancellation;
    private TaskCompletionSource? _opusRecoveryResult;
    private bool _opusRecoveryTurnEnded;
    private bool _opusInterruptRequested;
    private readonly Queue<DateTime> _opusRestartTimes = new();
    internal TimeSpan OpusRecoveryDelay { get; set; } = TimeSpan.FromSeconds(30);
    internal TimeSpan OpusRecoveryTimeout { get; set; } = TimeSpan.FromSeconds(30);

    internal bool IsCurrentModelSelection(long version)
    {
        lock (_modelRecoveryGate) return version == _modelSelectionVersion;
    }

    /// <summary>
    /// Let a committed fallback work for 30 seconds before interrupting and selecting Opus. Exhausting the retry
    /// budget leaves the fallback running. Only an interrupted turn needs a continuation in the same conversation.
    /// Anthropic's model-config docs were rechecked on 2026-10-07; the session stays on its fallback until switched:
    /// https://code.claude.com/docs/en/model-config#automatic-model-fallback
    /// https://unpkg.com/@anthropic-ai/claude-agent-sdk@0.3.220/sdk.d.ts
    /// </summary>
    private void ObserveOpusFallback(JsonNode message, string? type)
    {
        if (message["parent_tool_use_id"] is not null || message["subagent_thread_id"] is not null) return;
        if (SessionId is { } sessionId && message["session_id"]?.ToString() is { } eventSession
            && sessionId != eventSession) return;

        lock (_modelRecoveryGate)
        {
            var selected = ClaudeModelCatalog.CanonicalId(_selectedModel);
            if (selected is not ("" or "default" or "opus") && selected != ClaudeModelCatalog.Opus) return;

            var subtype = message["subtype"]?.ToString();
            if (type == "system" && subtype == "model_refusal_fallback"
                && message["direction"]?.ToString() is "retry" or "sticky"
                && ClaudeModelCatalog.CanonicalId(message["original_model"]?.ToString()) == ClaudeModelCatalog.Opus
                && ClaudeModelCatalog.CanonicalId(message["fallback_model"]?.ToString()) == "claude-opus-4-8")
            {
                var uuid = message["uuid"]?.ToString();
                if (uuid is not null && uuid == _lastOpusFallbackUuid) return;
                _lastOpusFallbackUuid = uuid;
                _pendingOpusRestore = true;
            }

            if (!_pendingOpusRestore) return;
            if (_opusRecoveryCancellation is not null) return;
            var responseModel = type switch
            {
                "stream_event" when message["event"]?["type"]?.ToString() == "message_start"
                    => message["event"]?["message"]?["model"]?.ToString(),
                "assistant" => message["message"]?["model"]?.ToString(),
                _ => null,
            };
            // message_start confirms that the CLI has committed its fallback and the retry is in flight.
            // Older/non-streaming runtimes may only report an assistant or result.
            if (ClaudeModelCatalog.CanonicalId(responseModel) != "claude-opus-4-8"
                && type != "result" && !(type == "system" && subtype == "model_refusal_fallback"
                    && message["direction"]?.ToString() == "sticky")) return;

            _pendingOpusRestore = false; // one control per fallback, regardless of repeated stream/assistant frames
            if (_lifetime.IsCancellationRequested || HasExited) return;
            var target = selected == ClaudeModelCatalog.Opus ? _selectedModel! : ClaudeModelCatalog.Opus;
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _opusRecoveryCancellation = cancellation;
            _opusRecoveryTurnEnded = false;
            var finished = _opusRecoveryResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = RestartOnOpusAsync(target, _modelSelectionVersion, type != "result", finished, cancellation);
        }
    }

    // Called before forwarding a root result, then completed AFTER forwarding it. The host must settle the old
    // turn before seeing the ready event, even if the CLI acknowledges controls immediately.
    private TaskCompletionSource? MarkOpusRecoveryResult(JsonNode message)
    {
        lock (_modelRecoveryGate)
        {
            _opusRecoveryTurnEnded = true;
            // A natural completion during the cooldown is a real completion, not an interrupted turn to replay.
            if (_opusInterruptRequested)
                message["vibecode_model_recovery_interrupted"] = true;
            _opusInterruptRequested = false;
            return _opusRecoveryResult;
        }
    }

    internal void CancelOpusRecovery()
    {
        lock (_modelRecoveryGate)
        {
            _modelSelectionVersion++; // also invalidates a ready event already queued for the UI
            _pendingOpusRestore = false;
            _opusRecoveryCancellation?.Cancel();
        }
    }

    private async Task RestartOnOpusAsync(string model, long selectionVersion, bool interrupt,
        TaskCompletionSource finished, CancellationTokenSource cancellation)
    {
        var recoveryId = Guid.NewGuid().ToString("N");
        var phase = "cancelled";
        var detail = "Automatic Opus restart cancelled.";
        void Notify(string phase, string message) => MessageReceived?.Invoke(new JsonObject
        {
            ["type"] = "system",
            ["subtype"] = "claude_model_restore",
            ["phase"] = phase,
            ["recovery_id"] = recoveryId,
            ["model"] = phase == "fallback" ? "claude-opus-4-8" : model,
            ["selection_version"] = selectionVersion,
            ["success"] = phase == "ready",
            ["message"] = message,
        });
        try
        {
            bool retry;
            lock (_modelRecoveryGate)
            {
                var now = DateTime.UtcNow;
                while (_opusRestartTimes.TryPeek(out var at) && now - at > TimeSpan.FromMinutes(5)) _opusRestartTimes.Dequeue();
                retry = _opusRestartTimes.Count < 3;
            }
            if (!retry)
            {
                // Decide before sending interrupt. The fallback already owns this turn and can finish it normally.
                phase = "fallback";
                detail = "Claude keeps switching back. Continuing on Opus 4.8.";
            }
            else
            {
                Notify("waiting", "Continuing on Opus 4.8 for 30 seconds before retrying Opus 5.5.");
                await Task.Delay(OpusRecoveryDelay, cancellation.Token).ConfigureAwait(false);
                Task<JsonNode?>? stop = null;
                bool restart;
                lock (_modelRecoveryGate)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (!IsCurrentModelSelection(selectionVersion)) return;
                    // The result can be received before its forwarding barrier completes. It has still ended
                    // the fallback, even if the UI or another subscriber is busy delivering that result.
                    restart = interrupt && !_opusRecoveryTurnEnded;
                    _opusRestartTimes.Enqueue(DateTime.UtcNow);
                    if (restart)
                    {
                        Notify("stopping", "Stopping Opus 4.8 to restart with Opus 5.5.");
                        _opusInterruptRequested = true;
                        stop = RequestAsync(new JsonObject { ["subtype"] = "interrupt" },
                            OpusRecoveryTimeout, cancellation.Token);
                    }
                }
                if (stop is not null) await stop.ConfigureAwait(false);
                await finished.Task.WaitAsync(OpusRecoveryTimeout, cancellation.Token).ConfigureAwait(false);
                Task<JsonNode?> change;
                lock (_modelRecoveryGate)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (!IsCurrentModelSelection(selectionVersion)) return;
                    // Serialize the write with manual selection, so a later user choice reaches the CLI last.
                    change = RequestAsync(new JsonObject { ["subtype"] = "set_model", ["model"] = model, ["effort"] = _selectedEffort },
                        OpusRecoveryTimeout, cancellation.Token);
                }
                await change.ConfigureAwait(false);
                cancellation.Token.ThrowIfCancellationRequested();
                if (IsCurrentModelSelection(selectionVersion))
                {
                    phase = restart ? "ready" : "selected";
                    detail = restart ? "Opus 5.5 selected. Restarting the interrupted task."
                        : "Opus 5.5 selected for the next turn.";
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The terminal notification is sent after state is cleared, so it may safely start another turn.
        }
        catch (Exception ex)
        {
            CrashLog.Note("ClaudeModelRecovery", $"Could not restart on {model}: {ex.Message}");
            phase = "failed";
            detail = $"Could not restart automatically with Opus 5.5: {ex.Message}";
        }
        finally
        {
            lock (_modelRecoveryGate)
            {
                if (ReferenceEquals(_opusRecoveryCancellation, cancellation))
                {
                    _opusRecoveryCancellation = null;
                    _opusRecoveryResult = null;
                    _pendingOpusRestore = false;
                }
            }
            cancellation.Dispose();
        }
        if (!_lifetime.IsCancellationRequested) Notify(phase, detail);
    }
}
