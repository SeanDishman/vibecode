using System.Windows.Threading;
using VibeCode.Services;

namespace VibeCode.UI;

public sealed partial class ChatViewModel
{
    private bool _unfinishedUsageRecorded;
    private bool _sessionUsageRestored;
    private double _costBeforeResume;
    private bool _costBeforeResumeEstimated;

    /// <summary>A running turn has not yet reported tokens, so its totals are pending.</summary>
    internal bool TurnUsagePending => _status == "running" && !_liveTurnUsage.HasTokens;

    /// <summary>The provider supplied only part of this chat's usage, so <see cref="TokensText"/> is a minimum.</summary>
    internal bool TokenUsageIncomplete => _tokenUsageIncomplete;

    private async Task RestoreSessionUsageAndStartAsync(int version, string sessionId)
    {
        // Loading the durable ledger can involve thousands of turns. Keep it off the dispatcher,
        // and finish before launching the provider so a new result cannot be included twice.
        var entries = await Task.Run(() => UsageLog.Instance.Entries()
            .Where(entry => entry.Provider == Provider && entry.SessionId == sessionId).ToArray()).ConfigureAwait(false);
        await _ui.InvokeAsync(() =>
        {
            if (version != Volatile.Read(ref _startVersion) || Status == "closed") return;
            RestoreRecordedUsage(entries);
            StartWithTranscript(version);
        }, DispatcherPriority.Background);
    }

    private void RestoreRecordedUsage(IReadOnlyList<UsageEntry> entries)
    {
        if (_sessionUsageRestored) return;
        _sessionUsageRestored = true;
        var input = entries.Sum(entry => entry.Input);
        var cacheWrite = entries.Sum(entry => entry.CacheWrite);
        var cacheRead = entries.Sum(entry => entry.CacheRead);
        var output = entries.Sum(entry => entry.Output);
        TotalIn = input + cacheWrite + cacheRead;
        TotalOut = output;
        TotalTokens = TotalIn + TotalOut;
        var cost = entries.Sum(entry => entry.CostUsd);
        _costBeforeResume = cost;
        _costBeforeResumeEstimated = entries.Any(entry => entry.CostUsd > 0 && !entry.CostReported);
        Raise(nameof(CostText));
    }

    private void RestoreCodexUsageBaseline(System.Text.Json.Nodes.JsonNode? snapshot)
    {
        if (!IsCodex) return;
        var usage = UsageOf(snapshot);
        // The root thread's native lifetime total can fill gaps in the app ledger. The ledger
        // may already include native children, so use it as a floor rather than adding it again.
        TotalIn = Math.Max(_totalIn, usage.TotalIn);
        TotalOut = Math.Max(_totalOut, usage.Output);
        TotalTokens = TotalIn + TotalOut;
    }

    private void CommitLateCodexUsage(System.Text.Json.Nodes.JsonNode message)
    {
        var usage = UsageOf(message["usage"]);
        if (!usage.HasTokens) return;
        // The adapter has already subtracted this thread's previously reported consumption. A late
        // child report must not reset a newer turn's live usage or change the parent's lifecycle.
        var model = NodeString(message["model"]) ?? UsageModel;
        var cost = UsagePricing.Estimate(model, message["usage"], _costTierForTurn);
        TotalIn += usage.TotalIn;
        TotalOut += usage.Output;
        TotalTokens += usage.Total;
        _estCost += cost;
        LogTurnUsage(usage.Input, usage.CacheWrite, usage.CacheRead, usage.Output, cost, reported: false, model: model);
        Raise(nameof(CostText));
    }

    /// <summary>Preserve observed consumption if a provider exits or a pane closes before a final usage report.
    /// Clearing the snapshot after recording prevents a later close/error from counting the same work again.</summary>
    private void CommitUnfinishedUsage()
    {
        if (_unfinishedUsageRecorded) return;
        if (!_liveTurnUsage.HasTokens) return;
        var usage = _liveTurnUsage;
        var cost = LiveEstimatedCost;
        TotalIn = _totalIn + usage.TotalIn;
        TotalOut = _totalOut + usage.Output;
        TotalTokens = _totalTokens + usage.Total;
        _estCost += cost;
        LogTurnUsage(usage.Input, usage.CacheWrite, usage.CacheRead, usage.Output, cost, reported: false, model: UsageModel);
        _unfinishedUsageRecorded = true;
        ResetLiveUsage();
    }
}
