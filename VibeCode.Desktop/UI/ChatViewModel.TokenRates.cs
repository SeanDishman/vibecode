using System.Text.Json.Nodes;
using System.Windows.Threading;
using VibeCode.Services;

namespace VibeCode.UI;

public sealed partial class ChatViewModel
{
    private readonly RollingTokenUsage _tokenUsageRates = new();
    private DispatcherTimer? _tokenRateTimer;
    private string _tokenRatesText = "0 tok/s · 0 tok/min";
    private bool _hasRecentTokenUsage;
    private double _grokRateCharacters;
    private double _grokRateConfirmedCharacters;
    private double _grokRateConfirmedTokens;

    public string TokenRatesText => _tokenRatesText;
    public bool HasTokenRates => HasTokens || _hasRecentTokenUsage;
    public string TokenRatesToolTip =>
        "Average tokens per second over up to the last 60 seconds, and tokens used in that window "
        + "(input + cached input + output). Batched reports are spread over the time since the previous "
        + "report or prompt start. Grok streaming output is estimated at one token per four characters; "
        + "~ marks unconfirmed token estimates. Rates gradually fall during idle or tool-only periods.";

    private void TrackTokenUsage(double turnTotal)
    {
        if (Status == "closed" || !double.IsFinite(turnTotal) || turnTotal <= 0) return;
        ObserveReportedTokenUsage(turnTotal);
    }

    private void ObserveReportedTokenUsage(double turnTotal)
    {
        _grokRateConfirmedTokens = turnTotal;
        _grokRateConfirmedCharacters = _grokRateCharacters;
        _tokenUsageRates.ObserveTurn(turnTotal);
        UpdateTokenRateTimer();
    }

    private void CompleteTokenUsage(JsonNode? usage)
    {
        // An absent report leaves provisional samples provisional. An explicit zero is a correction.
        if (Status == "closed" || usage is not JsonObject bucket
            || !new[] { "input_tokens", "output_tokens", "cache_read_input_tokens", "cache_creation_input_tokens" }
                .Any(bucket.ContainsKey)) return;
        ObserveReportedTokenUsage(UsageOf(usage).Total);
    }

    private void EstimateGrokTokenRate(string? chunk)
    {
        // session/load can replay old ACP chunks while starting. Only an active prompt generates usage.
        if (!IsGrok || Status != "running" || string.IsNullOrEmpty(chunk)) return;
        _grokRateCharacters += chunk.Length;
        var total = _grokRateConfirmedTokens
            + Math.Floor((_grokRateCharacters - _grokRateConfirmedCharacters) / OutputCharsPerToken);
        if (total <= 0) return;
        // ACP sends output text/thinking while its aggregate usage arrives at turn end. Only the rate
        // samples use this estimate; session totals, context occupancy and billed cost stay authoritative.
        _tokenUsageRates.ObserveEstimatedTurn(total);
        UpdateTokenRateTimer();
    }

    private void ResetTokenUsageTurn()
    {
        _grokRateCharacters = _grokRateConfirmedCharacters = _grokRateConfirmedTokens = 0;
        _tokenUsageRates.StartTurn();
    }

    private void UpdateTokenRateTimer()
    {
        RefreshTokenRates();
        if (!_hasRecentTokenUsage) return;
        _tokenRateTimer ??= new DispatcherTimer(DispatcherPriority.Background, _ui)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _tokenRateTimer.Tick -= OnTokenRateTick;
        _tokenRateTimer.Tick += OnTokenRateTick;
        _tokenRateTimer.Start();
    }

    private void OnTokenRateTick(object? sender, EventArgs e) => RefreshTokenRates();

    private void RefreshTokenRates()
    {
        var (second, minute, secondEstimated, minuteEstimated) = _tokenUsageRates.ReadWithEstimates();
        var text = $"{(secondEstimated ? "~" : "")}{FmtTokens(second)} tok/s · {(minuteEstimated ? "~" : "")}{FmtTokens(minute)} tok/min";
        if (_tokenRatesText != text)
        {
            _tokenRatesText = text;
            Raise(nameof(TokenRatesText));
        }
        if (_hasRecentTokenUsage != (minute > 0))
        {
            _hasRecentTokenUsage = minute > 0;
            Raise(nameof(HasTokenRates));
        }
        // Keep aging samples while idle, then release the dispatcher timer until new usage arrives.
        if (minute == 0) _tokenRateTimer?.Stop();
    }
}
