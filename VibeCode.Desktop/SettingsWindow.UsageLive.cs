using System.Windows;
using System.Windows.Threading;
using VibeCode.Services;

namespace VibeCode;

public partial class SettingsWindow
{
    private IDisposable? _usageLiveWatch;
    private DispatcherTimer? _usageLiveRefresh;
    private bool _usageHadLiveTurns;
    private DateTime _usageReportDay;

    private void StartUsageLiveRefresh()
    {
        _usageLiveWatch ??= LiveTurnTelemetry.Instance.Watch();
        _usageLiveRefresh = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _usageLiveRefresh.Tick += (_, _) =>
        {
            if (!_ready || PaneUsage.Visibility != Visibility.Visible) return;
            if (LiveTurnTelemetry.Instance.StreamingCount > 0 || _usageHadLiveTurns || _usageReportDay != DateTime.Today)
                RefreshUsage();
        };
        _usageLiveRefresh.Start();
    }

    private void StopUsageLiveRefresh()
    {
        _usageLiveRefresh?.Stop();
        _usageLiveRefresh = null;
        _usageLiveWatch?.Dispose();
        _usageLiveWatch = null;
    }
}
