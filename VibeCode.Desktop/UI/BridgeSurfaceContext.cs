using System.ComponentModel;
using VibeCode.Services;

namespace VibeCode.UI;

/// <summary>
/// The Bridge header as ONE window sees it.
///
/// Two Bridges can now be open at once — one per display — and the header describes a roster: how many agents, which
/// project, which providers, whether another agent can join, whether it can be forked. Every one of those answers is
/// different in the two windows, but the header XAML is a single template shared by both.
///
/// So the second working shell hands that header this object instead of the MainViewModel. It exposes the same
/// property NAMES the header already binds, resolved against <see cref="MainViewModel.SecondaryBridgePanes"/>, plus
/// straight pass-throughs for the handful of non-roster values the header also reads. No binding in the header had
/// to change; the primary window still gets the view model itself and is untouched.
///
/// Deliberately read-only: actions stay in the code-behind, which routes them by window (see
/// <c>OwnsSecondaryBridge</c>). A context that could also mutate would be a second, competing path to the roster.
/// </summary>
public sealed class BridgeSurfaceContext : INotifyPropertyChanged
{
    private readonly MainViewModel _vm;

    public BridgeSurfaceContext(MainViewModel vm)
    {
        _vm = vm;
        _vm.PropertyChanged += OnViewModelChanged;
    }

    /// <summary>Detach from the view model, which outlives this window.</summary>
    public void Dispose() => _vm.PropertyChanged -= OnViewModelChanged;

    // ---- the roster this window is showing ----
    public BridgePaneCollection BridgePanes => _vm.SecondaryBridgePanes;
    public bool IsBridge => _vm.SecondaryIsBridge;
    public string BridgeSummary => _vm.SecondaryBridgeSummary;
    public string BridgeProjectPath => _vm.SecondaryBridgeProjectPath;
    public bool BridgeUsesClaude => _vm.SecondaryBridgeUsesClaude;
    public bool BridgeUsesCodex => _vm.SecondaryBridgeUsesCodex;
    public bool BridgeUsesKimi => _vm.SecondaryBridgeUsesKimi;
    public bool BridgeUsesGrok => _vm.SecondaryBridgeUsesGrok;
    public IEnumerable<GrokAccountInfo> BridgeGrokAccounts => _vm.SecondaryBridgeGrokAccounts;
    public string BridgeGrokAccountCountText => _vm.SecondaryBridgeGrokAccountCountText;
    public string BridgeGrokUsageSummary => _vm.SecondaryBridgeGrokUsageSummary;
    public bool HasMinimizedBridgePanes => _vm.SecondaryHasMinimizedBridgePanes;
    public bool CanAnnounceToBridge => _vm.SecondaryCanAnnounceToBridge;
    public bool CanAddBridgeAgent => _vm.SecondaryCanAddBridgeAgent;
    public string AddBridgeAgentToolTip => _vm.SecondaryAddBridgeAgentToolTip;
    public string AddBridgeAgentText => _vm.AddBridgeAgentText;   // a constant label, same in both shells

    /// <summary>Fork puts the new roster on the PRIMARY surface, so it is not offered for this window's bridge —
    /// taking it would silently move the bridge the user is looking at onto the other display.</summary>
    public bool CanForkBridge => false;
    public string ForkBridgeToolTip =>
        "Fork runs on the main window — open this bridge there to branch it";

    // ---- not roster-scoped: the same answer in either window ----
    public bool IsDemonSurface => _vm.IsDemonSurface;
    public string DemonSummary => _vm.DemonSummary;
    public bool IsCodexSignedIn => _vm.IsCodexSignedIn;
    public bool HasCodexAccountUsage => _vm.HasCodexAccountUsage;
    public bool CodexAtLimit => _vm.CodexAtLimit;
    public string CodexAccountUsage => _vm.CodexAccountUsage;
    public string BridgeHint => _vm.BridgeHint;

    /// <summary>
    /// Every name above is a projection of the view model, so a change there is a change here. The map keeps the
    /// two in step without this class having to know which view-model property feeds which header field: anything
    /// prefixed "Secondary" is re-announced under its unprefixed name, and the rest pass straight through.
    /// </summary>
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        var name = e.PropertyName;
        if (string.IsNullOrEmpty(name)) { Raise(null); return; }

        if (name.StartsWith("Secondary", StringComparison.Ordinal))
        {
            var mapped = name["Secondary".Length..];
            // SecondaryIsBridge -> IsBridge, SecondaryBridgeSummary -> BridgeSummary, and so on.
            Raise(mapped);
            // SecondaryHasMinimizedBridgePanes and friends already read correctly under the mapped name; the
            // roster collection itself is also worth re-announcing so the minimized chips rebind.
            if (mapped == "BridgePanes") Raise(nameof(IsBridge));
            return;
        }

        switch (name)
        {
            case nameof(MainViewModel.IsDemonSurface):
            case nameof(MainViewModel.DemonSummary):
            case nameof(MainViewModel.IsCodexSignedIn):
            case nameof(MainViewModel.HasCodexAccountUsage):
            case nameof(MainViewModel.CodexAtLimit):
            case nameof(MainViewModel.CodexAccountUsage):
            case nameof(MainViewModel.BridgeHint):
                Raise(name);
                break;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
