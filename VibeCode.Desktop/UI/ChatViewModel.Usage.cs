namespace VibeCode.UI;

public sealed partial class ChatViewModel
{
    private bool _unfinishedUsageRecorded;

    /// <summary>Preserve observed consumption if a provider exits or a pane closes before a final usage report.
    /// Clearing the snapshot after recording prevents a later close/error from counting the same work again.</summary>
    private void CommitUnfinishedUsage()
    {
        if (!_liveTurnUsage.HasTokens) return;
        var usage = _liveTurnUsage;
        var cost = LiveEstimatedCost;
        TotalIn = _totalIn + usage.TotalIn;
        TotalOut = _totalOut + usage.Output;
        TotalTokens = _totalTokens + usage.Total;
        if (!IsGrok) _estCost += cost;
        LogTurnUsage(usage.Input, usage.CacheWrite, usage.CacheRead, usage.Output, cost, reported: false, model: UsageModel);
        // Kimi's next authoritative session snapshot contains this consumption too. Advance its history
        // baseline so that snapshot records only the later work.
        if (IsKimi)
        {
            _loggedKimiUsage = new(_loggedKimiUsage.Input + usage.Input, _loggedKimiUsage.CacheWrite + usage.CacheWrite,
                _loggedKimiUsage.CacheRead + usage.CacheRead, _loggedKimiUsage.Output + usage.Output);
            _loggedKimiCost += cost;
        }
        _unfinishedUsageRecorded = true;
        ResetLiveUsage();
    }
}
