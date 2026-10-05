using System.Text.Json.Nodes;
using System.Windows.Threading;
using VibeCode.Services;

namespace VibeCode.UI;

public sealed partial class ChatViewModel
{
    private readonly RollingTokenUsage _readTokenUsageRates = new();
    private readonly RollingTokenUsage _writeTokenUsageRates = new();
    private DispatcherTimer? _tokenRateTimer;
    private string _tokenRatesText = "";
    private bool _hasRecentTokenUsage;
    private bool _hasTokenUsageThisTurn;
    private double _streamedRateCharacters;
    private double _streamedRateConfirmedCharacters;
    private double _streamedRateConfirmedOutputTokens;

    public string TokenRatesText => _tokenRatesText;
    public bool HasTokenRates => IsWorking && _hasRecentTokenUsage;
    public string TokenRatesToolTip =>
        "Read = input tokens, including cache reads and cache creation. Write = generated output tokens. "
        + "Each row shows tokens reported in the last 60 seconds. The per-second average appears only when new tokens "
        + "were reported or streamed in the last second. A working agent can be waiting on a tool or a usage report. "
        + "Batched reports are spread over the time since the previous "
        + "report or prompt start. Codex and Grok streaming output is estimated at one token per four characters; "
        + "~ marks unconfirmed token estimates. Rates gradually fall during tool-only periods. "
        + "Zero rows are hidden, and rates disappear when the turn finishes.";

    private void TrackTokenUsage(LiveUsage usage)
    {
        if (Status == "closed" || !double.IsFinite(usage.Total) || usage.Total <= 0) return;
        ObserveReportedTokenUsage(usage);
    }

    private void ObserveReportedTokenUsage(LiveUsage usage)
    {
        _hasTokenUsageThisTurn = true;
        _streamedRateConfirmedOutputTokens = usage.Output;
        _streamedRateConfirmedCharacters = _streamedRateCharacters;
        _readTokenUsageRates.ObserveTurn(usage.TotalIn);
        _writeTokenUsageRates.ObserveTurn(usage.Output);
        UpdateTokenRateTimer();
    }

    private void CompleteTokenUsage(JsonNode? usage)
    {
        // An absent report leaves provisional samples provisional. An explicit zero is a correction.
        if (Status == "closed" || usage is not JsonObject bucket
            || !new[] { "input_tokens", "output_tokens", "cache_read_input_tokens", "cache_creation_input_tokens" }
                .Any(bucket.ContainsKey)) return;
        ObserveReportedTokenUsage(UsageOf(usage));
    }

    private void EstimateStreamingTokenRate(string? chunk)
    {
        // session/load can replay old ACP chunks while starting. Only an active prompt generates usage.
        if ((!IsGrok && !IsCodex) || Status != "running" || string.IsNullOrEmpty(chunk)) return;
        _streamedRateCharacters += chunk.Length;
        var total = _streamedRateConfirmedOutputTokens
            + Math.Floor((_streamedRateCharacters - _streamedRateConfirmedCharacters) / OutputCharsPerToken);
        if (total <= 0) return;
        // Codex/ACP send output text/thinking before their aggregate usage arrives. Only the rate
        // samples use this estimate; session totals, context occupancy and billed cost stay authoritative.
        _hasTokenUsageThisTurn = true;
        _writeTokenUsageRates.ObserveEstimatedTurn(total);
        UpdateTokenRateTimer();
    }

    private void ResetTokenUsageTurn()
    {
        _hasTokenUsageThisTurn = false;
        _streamedRateCharacters = _streamedRateConfirmedCharacters = _streamedRateConfirmedOutputTokens = 0;
        _readTokenUsageRates.StartTurn();
        _writeTokenUsageRates.StartTurn();
    }

    private void BeginTokenUsageTiming()
    {
        // If only one side has reported, resuming this turn must not restart the other side's clock.
        if (_hasTokenUsageThisTurn) return;
        _readTokenUsageRates.BeginTiming();
        _writeTokenUsageRates.BeginTiming();
    }

    private void UpdateTokenRateTimer()
    {
        RefreshTokenRates();
        if (!HasTokenRates) return;
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
        var read = _readTokenUsageRates.ReadWithEstimates();
        var write = _writeTokenUsageRates.ReadWithEstimates();
        var readText = Math.Round(read.LastMinute) > 0
            ? $"read {FormatRate(read, _readTokenUsageRates.HasRecentActivity)}" : "";
        var writeText = Math.Round(write.LastMinute) > 0
            ? $"write {FormatRate(write, _writeTokenUsageRates.HasRecentActivity)}" : "";
        var text = readText.Length == 0 ? writeText : writeText.Length == 0 ? readText : $"{readText}\n{writeText}";
        if (_tokenRatesText != text)
        {
            _tokenRatesText = text;
            Raise(nameof(TokenRatesText));
        }
        var hasRecentUsage = text.Length > 0;
        if (_hasRecentTokenUsage != hasRecentUsage)
        {
            _hasRecentTokenUsage = hasRecentUsage;
            Raise(nameof(HasTokenRates));
        }
        // Finished chats need no display ticks. Recalculate the retained minute window when work resumes.
        if (!HasTokenRates) _tokenRateTimer?.Stop();
    }

    private static string FormatRate((double PerSecond, double LastMinute, bool SecondEstimated, bool MinuteEstimated) rate, bool hasRecentActivity)
    {
        var minute = $"{(rate.MinuteEstimated ? "~" : "")}{FmtTokens(rate.LastMinute)}/min";
        return hasRecentActivity && rate.PerSecond >= .5
            ? $"{(rate.SecondEstimated ? "~" : "")}{FmtTokens(rate.PerSecond)}/s · {minute}"
            : minute;
    }
}
