using System.Windows.Threading;
using VibeCode.AgentStatus.Mcp.Contracts;
using VibeCode.Services;

namespace VibeCode.UI;

/// <summary>
/// The Bridge side of <see cref="AccountPacing"/>. An account past the pacing threshold gets no new assignments - they
/// start after its window resets or once its usage drops - a teammate the robot summons joins on another usable account,
/// and the Bridge header says which account is held and where new work goes instead.
/// </summary>
public sealed partial class MainViewModel
{
    private bool _pacingRefreshQueued;

    public string BridgePacingText => PacingTextOf(BridgePanes);
    public bool HasBridgePacing => BridgePacingText.Length > 0;
    public string SecondaryBridgePacingText => PacingTextOf(SecondaryBridgePanes);
    public bool SecondaryHasBridgePacing => SecondaryBridgePacingText.Length > 0;

    private void StartAccountPacing() => AccountPacing.Instance.Changed += OnAccountPacingChanged;

    private void OnAccountPacingChanged()
    {
        if (_pacingRefreshQueued) return;
        _pacingRefreshQueued = true;
        // Readings arrive inside one pane's event handling, which must not start other panes' work re-entrantly.
        ApplicationDispatcher.InvokeAsync(ApplyAccountPacing, DispatcherPriority.Background);
    }

    /// <summary>An account may have crossed the threshold either way: held assignments that can start now do, and both
    /// Bridge headers repaint.</summary>
    private void ApplyAccountPacing()
    {
        _pacingRefreshQueued = false;
        foreach (var bridge in LiveBridges().ToList())
        {
            try { StartReadyBridgeTasks(bridge); }
            catch (StatusValidationException ex)
            { bridge.Panes.FirstOrDefault()?.Items.Add(new BannerItem { Level = "warning", Text = ex.Message }); }
        }
        Raise(nameof(BridgePacingText));
        Raise(nameof(HasBridgePacing));
        Raise(nameof(SecondaryBridgePacingText));
        Raise(nameof(SecondaryHasBridgePacing));
    }

    /// <summary>"Account A 88% · resets 3:40 PM · new work goes to Account B" for each held account in the roster.</summary>
    private static string PacingTextOf(IReadOnlyList<ChatViewModel> panes)
    {
        var pacing = AccountPacing.Instance;
        var held = panes.Where(pane => pacing.Holds(pane.Provider, pane.AccountId))
            .GroupBy(pane => (pane.Provider, pane.AccountId)).Select(group => group.First()).ToList();
        return string.Join("  ·  ", held.Select(pane =>
        {
            var elsewhere = pacing.SteerAway(pane.Provider, pane.AccountId, BridgeAccountChoice.For(pane.Provider));
            return $"{pane.AccountLabel} {pacing.Describe(pane.Provider, pane.AccountId)} · "
                   + (elsewhere is null ? "new work waits for the reset" : $"new work goes to {elsewhere.Label}");
        }));
    }

    /// <summary>The account a teammate summoned from <paramref name="origin"/> joins on: another usable account of the same
    /// provider while the origin's is past the pacing threshold, otherwise the origin's own.</summary>
    private static string? PacedAccountFor(ChatViewModel origin)
    {
        var choices = BridgeAccountChoice.For(origin.Provider);
        var exhausted = choices.Any(a => SameAccount(a.AccountId, origin.AccountId) && a.AtUsageLimit);
        return (exhausted || AccountPacing.Instance.Holds(origin.Provider, origin.AccountId))
            && AccountPacing.Instance.SteerAway(origin.Provider, origin.AccountId, choices) is { } other
                ? other.AccountId : origin.AccountId;
    }
}
