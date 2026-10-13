namespace VibeCode.UI;

public sealed partial class ChatViewModel
{
    private bool _unfinishedUsageRecorded;

    /// <summary>Preserve observed consumption if a provider exits or a pane closes before a final usage report.
    /// Clearing the snapshot after recording prevents a later close/error from counting the same work again.</summary>
    private void CommitUnfinishedUsage()
    {
        if (_unfinishedUsageRecorded) return;
        var geminiReport = (_session as VibeCode.Protocol.GeminiSession)?.UsageSnapshot();
        if (geminiReport is not null) SetLiveUsage(UsageOf(geminiReport["usage"]));
        if (!_liveTurnUsage.HasTokens) return;
        var usage = _liveTurnUsage;
        var cost = LiveEstimatedCost;
        TotalIn = _totalIn + usage.TotalIn;
        TotalOut = _totalOut + usage.Output;
        TotalTokens = _totalTokens + usage.Total;
        _estCost += cost;
        if (geminiReport?["modelUsage"] is System.Text.Json.Nodes.JsonObject geminiModels && geminiModels.Count > 0)
        {
            foreach (var (model, tokens) in geminiModels)
                LogTurnUsage(DoubleOrZero(tokens?["inputTokens"]), 0, DoubleOrZero(tokens?["cacheReadInputTokens"]),
                    DoubleOrZero(tokens?["outputTokens"]), 0, reported: true, model: model);
        }
        else LogTurnUsage(usage.Input, usage.CacheWrite, usage.CacheRead, usage.Output, cost, reported: false, model: UsageModel);
        _unfinishedUsageRecorded = true;
        ResetLiveUsage();
    }
}
