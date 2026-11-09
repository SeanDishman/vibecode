using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace VibeCode.UI;

public partial class StartupOverlay : UserControl
{
    public StartupOverlay()
    {
        InitializeComponent();
        if (VibeCode.Services.AppSettings.IsCliMode)
            LogoGlow.Visibility = Visibility.Collapsed;
    }

    public async Task SetStageAsync(string status, double progress, int step)
    {
        var nextProgress = Math.Clamp(progress, 0, 100);
        var currentProgress = StartupProgress.Value;

        StartupStatusText.Text = status;
        StartupStepText.Text = $"{Math.Clamp(step, 0, 4):00} / 04";
        StartupPercentText.Text = $"{nextProgress:0}%";

        StartupStatusText.BeginAnimation(OpacityProperty, null);
        if (SystemParameters.ClientAreaAnimation)
        {
            StartupProgress.BeginAnimation(RangeBase.ValueProperty, null);
            StartupProgress.Value = nextProgress;
            StartupProgress.BeginAnimation(RangeBase.ValueProperty, new DoubleAnimation
            {
                From = currentProgress,
                To = nextProgress,
                Duration = TimeSpan.FromMilliseconds(280),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop,
            });
            StartupStatusText.BeginAnimation(OpacityProperty, new DoubleAnimation
            {
                From = 0.45,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(160),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
        }
        else
        {
            StartupProgress.BeginAnimation(RangeBase.ValueProperty, null);
            StartupProgress.Value = nextProgress;
        }

        // Give WPF a render pass before the next synchronous startup stage begins.
        await Dispatcher.Yield(DispatcherPriority.Render);
    }

    public async Task CompleteAsync()
    {
        await SetStageAsync("Workspace ready", 100, 4);

        // Readiness is not gated on a decorative minimum time or fade-out.
        Visibility = Visibility.Collapsed;
        BeginAnimation(OpacityProperty, null);
    }
}
