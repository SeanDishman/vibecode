using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using VibeCode.Services;

namespace VibeCode.UI;

public partial class SpotifyVolumeControl : UserControl
{
    private readonly DispatcherTimer _commitTimer;
    private int _pendingVolume;

    public SpotifyVolumeControl()
    {
        InitializeComponent();
        _commitTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(140),
        };
        _commitTimer.Tick += OnCommitTimerTick;
        Unloaded += (_, _) => _commitTimer.Stop();
    }

    private void OnVolumeButtonClick(object sender, RoutedEventArgs e)
    {
        VolumePopup.IsOpen = true;
        Dispatcher.BeginInvoke(() => VolumeSlider.Focus(), DispatcherPriority.Input);
    }

    private void OnVolumeButtonPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;

        // The first click opens the slider; consume the second click so it toggles mute instead
        // of being interpreted as another ordinary button activation.
        e.Handled = true;
        _commitTimer.Stop();
        VolumePopup.IsOpen = true;
        _ = SpotifyService.Instance.ToggleMuteAsync();
    }

    private void OnVolumeSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || !VolumePopup.IsOpen) return;

        var next = Math.Clamp((int)Math.Round(e.NewValue), 0, 100);
        if (next == SpotifyService.Instance.VolumePercent) return;

        _pendingVolume = next;
        SpotifyService.Instance.PreviewVolume(next);
        _commitTimer.Stop();
        _commitTimer.Start();
    }

    private async void OnCommitTimerTick(object? sender, EventArgs e)
    {
        _commitTimer.Stop();
        await SpotifyService.Instance.SetVolumeAsync(_pendingVolume);
    }
}
