using System.Text.Json.Nodes;
using VibeCode.Protocol;
using VibeCode.Services;

namespace VibeCode.UI;

/// <summary>
/// The pane's side of <see cref="ProviderHealthGate"/>. A recognized outage waits on its provider and account's shared gate
/// instead of a timer of its own; the gate's one shared tick drives <see cref="CheckBridgeErrorRecovery"/>; and when this
/// pane is elected, its ordinary continuation is the probe whose outcome releases every other pane or holds them back longer.
/// </summary>
public sealed partial class ChatViewModel : IHealthGateMember
{
    private ProviderHealthGate? _bridgeRetryGate;   // waiting on the shared gate; such a wait has no _bridgeRetryTimer
    private ProviderHealthGate? _healthProbe;       // this pane's continuation in flight is that gate's probe
    private int _healthProbeTools;                  // tool calls seen when that probe was sent

    // Only a new tool call proves the model answered: Claude Code reports an outage as a synthetic assistant message too.
    private bool HealthProbeAnswered => _toolById.Count > _healthProbeTools;

    // One gate per login: Bridge siblings on one account share an outage, other accounts keep their own.
    private string HealthGateAccount => _session is GlmSession glm ? glm.ActiveAccountId ?? AccountId ?? "" : AccountId ?? "";

    private void JoinHealthGate() => _bridgeRetryGate = ProviderHealthGates.Join(Provider, HealthGateAccount, this);

    private void LeaveHealthGate()
    {
        if (_bridgeRetryGate is not { } gate) return;
        _bridgeRetryGate = null;
        gate.Leave(this);
    }

    private bool BridgeRetryDue => _bridgeRetryGate is { } gate ? gate.TryProceed(this) : DateTimeOffset.Now >= _bridgeRetryAt;

    /// <summary>Leave the gate as this pane continues; the elected pane's continuation becomes the gate's probe.</summary>
    private void TakeHealthGateTurn()
    {
        if (_bridgeRetryGate is not { } gate) return;
        _bridgeRetryGate = null;
        if (!gate.BeginTurn(this)) return;
        _healthProbe = gate;
        _healthProbeTools = _toolById.Count;
    }

    /// <summary>The probe's turn ended. The same outage again backs the gate off further; an answer from the provider releases
    /// every waiting pane; anything else (a stop, a usage limit, an unrelated failure) proves nothing either way.</summary>
    private void SettleHealthProbe(JsonNode result, bool eligible)
    {
        if (_healthProbe is not { } gate) return;
        _healthProbe = null;
        if (eligible && BridgeErrorRecovery.IsTransient(result)) gate.ProbeFailed(this);
        else if (result["is_error"]?.ToString() != "true" || HealthProbeAnswered) gate.ProbeSucceeded();
        else gate.Leave(this);
    }

    /// <summary>A closed pane's probe never reports back, so the gate hands the next probe to another pane.</summary>
    private void AbandonHealthProbe()
    {
        if (_healthProbe is not { } gate) return;
        _healthProbe = null;
        gate.Leave(this);
    }

    void IHealthGateMember.OnHealthGateTick(ProviderHealthGate gate)
    {
        if (ReferenceEquals(_bridgeRetryGate, gate)) CheckBridgeErrorRecovery();
        else if (!ReferenceEquals(_healthProbe, gate)) gate.Leave(this);   // a wait this pane no longer tracks
    }

    bool IHealthGateMember.ProbeAnswered => _healthProbe is not null && HealthProbeAnswered;

    private string HealthGateText(ProviderHealthGate gate)
    {
        var state = gate.Offline ? $"Offline · {AgentDisplay} resumes when the network is back"
            : gate.Releases(this) ? $"{AgentDisplay} is back · continuing"
            : ReferenceEquals(gate.Prober, this) ? $"Checking whether {AgentDisplay} has recovered"
            : gate.Prober is not null ? $"Waiting for {AgentDisplay} to recover · checking now"
            : $"Waiting for {AgentDisplay} to recover · next check {Countdown(gate.UntilNextProbe)}";
        return gate.Incident is { Length: > 0 } incident ? $"{state} · {incident}" : state;
    }

    private static string Countdown(TimeSpan left)
    {
        var seconds = Math.Max(0, (int)Math.Ceiling(left.TotalSeconds));
        return $"{seconds / 60}:{seconds % 60:00}";
    }
}
