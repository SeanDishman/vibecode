using System.Text.Json.Nodes;
using VibeCode.Services;

namespace VibeCode.Protocol;

public sealed partial class CodexSession
{
    private JsonObject? _reserveModel;
    private JsonNode? _lastTurnError;
    private string? _lastCompletedRootTurnId;
    private bool _reserveRetryAttempted, _reserveRecoveryPending;
    private int _modelSelectionVersion;
    private CancellationTokenSource? _reserveRecoveryCancellation;

    // Offline regression transport. Production requests always use the process's JSON-RPC connection.
    internal Func<string, JsonObject?, Task<JsonNode?>>? RequestFixture { get; set; }

    internal bool? UsageAvailable(CodexAccountInfo account)
    {
        if (account.RateLimitStatus is not (null or "" or "rate_limit_reached") || account.SpendLimit?.RemainingPercent is <= 0) return false;
        var reserve = account.UsageLimits.Where(w => w.LimitId == CodexReserveFallback.LimitId).ToArray();
        var reserveAvailable = reserve.Length > 0 && reserve.All(w => w.Percent is >= 0 and < 100 && string.IsNullOrEmpty(w.ReachedType));
        if (ApiModel(_model) == CodexReserveFallback.ModelId)
            return reserve.Length == 0 ? null : reserveAvailable;
        return !account.AtLimit || (_reserveModel is not null && reserveAvailable);
    }

    private void CancelReserveRecovery()
    {
        try { _reserveRecoveryCancellation?.Cancel(); }
        catch (ObjectDisposedException) { /* The quota check completed concurrently. */ }
    }

    private sealed class CodexRpcException(JsonNode error)
        : InvalidOperationException(error["message"]?.GetValue<string>() ?? "Codex request failed.")
    {
        internal JsonNode Error { get; } = error.DeepClone();
    }

    private void TrackAcceptedTurn(JsonNode? response)
    {
        var id = response?["result"]?["turn"]?["id"]?.GetValue<string>();
        // A fast failure/completion can precede its turn/start response; do not resurrect that old turn id.
        if (id is not null && id != _lastCompletedRootTurnId) _turnId = id;
    }

    private bool BeginReserveRecovery(JsonObject? turn, JsonObject? rejectedRequest)
    {
        if (_disposed || _interruptRequested || _reserveRetryAttempted || _reserveRecoveryPending
            || HasActiveSubagents || SessionId is null || _reserveModel is null
            || ApiModel(_model) == CodexReserveFallback.ModelId || turn?["status"]?.GetValue<string>() != "failed"
            || !CodexReserveFallback.IsUsageLimitError(turn?["error"] ?? _lastTurnError)) return false;

        _reserveRetryAttempted = true;
        _reserveRecoveryPending = true;
        _reserveRecoveryCancellation = new CancellationTokenSource();
        _ = RecoverWithReserveAsync(turn!.DeepClone().AsObject(), rejectedRequest?.DeepClone().AsObject(),
            _reserveRecoveryCancellation, Volatile.Read(ref _modelSelectionVersion));
        EmitTurnActivity();
        return true;
    }

    private async Task RecoverWithReserveAsync(JsonObject failedTurn, JsonObject? rejectedRequest,
        CancellationTokenSource cancellation, int selectionVersion)
    {
        var submitted = false;
        try
        {
            // Read on THIS conversation's authenticated connection. Never use the globally selected account,
            // consume earned resets, switch credentials, buy credits, or reinterpret a generic 429 as quota.
            var response = await RequestAsync("account/rateLimits/read")
                .WaitAsync(TimeSpan.FromSeconds(10), cancellation.Token).ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_disposed || selectionVersion != Volatile.Read(ref _modelSelectionVersion)
                || !CodexReserveFallback.CanActivate(response?["result"])) return;

            var previous = ApiModel(_model);
            var effort = CodexReserveFallback.Effort(_reserveModel!, _effort);
            var p = rejectedRequest ?? new JsonObject
            {
                ["threadId"] = SessionId,
                ["cwd"] = _options.Cwd,
                ["input"] = new JsonArray(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = "Continue the interrupted task from the existing conversation after the usage-limit interruption. "
                               + "Keep completed work and do not repeat actions that already succeeded.",
                }),
            };
            p["model"] = CodexReserveFallback.ModelId;
            p["effort"] = effort;
            p["serviceTierForTurn"] = "default";
            p["serviceTier"] = "default";
            ApplySecurity(p, legacyThreadShape: false);

            cancellation.Token.ThrowIfCancellationRequested();
            // Commit the first model's usage without ending the host's logical turn or releasing its queue/rollback.
            // The continuation reports its own usage normally; no tokens are lost or counted twice at the switch.
            if (_lastUsage is not null)
                Emit(new JsonObject { ["type"] = "system", ["subtype"] = "codex_usage_checkpoint",
                    ["model"] = previous, ["usage"] = _lastUsage.DeepClone() });
            _turnUsage = default;
            _lastUsage = null;
            _lastError = null;
            _lastTurnError = null;
            cancellation.Token.ThrowIfCancellationRequested();
            _model = CodexReserveFallback.ModelId;
            _effort = effort;
            Emit(new JsonObject
            {
                ["type"] = "system", ["subtype"] = "codex_reserve_activated",
                ["model"] = _model, ["previous_model"] = previous, ["effort"] = effort,
                ["supportedEffortLevels"] = new JsonArray((_reserveModel!["supportedReasoningEfforts"] as JsonArray)?
                    .OfType<JsonObject>().Select(e => e["reasoningEffort"]?.DeepClone()).ToArray() ?? []),
                ["message"] = "Codex usage limit reached. Switching this chat to GPT Reserve to continue the interrupted task.",
            });

            // Once sent, do not cancel the RPC wait: its acceptance is uncertain until it replies. Stop stays
            // latched and interrupts the replacement as soon as turn/started supplies the new turn id.
            cancellation.Token.ThrowIfCancellationRequested();
            var started = await RequestAsync("turn/start", p).ConfigureAwait(false);
            submitted = true;
            TrackAcceptedTurn(started);
            if (_interruptRequested && _turnId is not null)
                await SafeRequest("turn/interrupt", new JsonObject { ["threadId"] = SessionId, ["turnId"] = _turnId });
        }
        catch (OperationCanceledException) { /* Stop, model change, or close during the quota check. */ }
        catch (Exception ex)
        {
            // A failed quota read leaves the original error visible. A rejected Reserve submission is terminal:
            // never bounce between models or replay a request after an uncertain transport failure.
            if (_model == CodexReserveFallback.ModelId)
                failedTurn = new JsonObject { ["status"] = "failed", ["error"] = new JsonObject
                    { ["message"] = "GPT Reserve could not continue: " + ex.Message } };
        }
        finally
        {
            _reserveRecoveryPending = false;
            if (ReferenceEquals(_reserveRecoveryCancellation, cancellation)) _reserveRecoveryCancellation = null;
            cancellation.Dispose();
            if (!_disposed)
            {
                if (!submitted)
                {
                    if (_interruptRequested) failedTurn = new JsonObject { ["status"] = "interrupted" };
                    FinishTurn(failedTurn);
                }
                EmitTurnActivity();
            }
        }
    }
}
