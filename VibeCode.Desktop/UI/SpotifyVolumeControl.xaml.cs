using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using VibeCode.Services;

namespace VibeCode.UI;

public partial class SpotifyVolumeControl : UserControl
{
    /// <summary>One wheel notch, in percent. Matches the slider's LargeChange so wheel and page-up agree.</summary>
    private const int WheelStep = 10;

    private readonly DispatcherTimer _commitTimer;
    private int _pendingVolume;
    private bool _hasPending;

    public SpotifyVolumeControl()
    {
        InitializeComponent();
        // Dragging the slider raises ValueChanged for every pixel; each one is a PUT to Spotify if sent
        // straight through. Coalesce them and send the value the user actually landed on.
        _commitTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(140),
        };
        _commitTimer.Tick += OnCommitTimerTick;
        // Unloaded with a commit still pending would drop the user's last adjustment on the floor -
        // the local value would show the new volume while the device stayed where it was.
        Unloaded += (_, _) => Flush();
    }

    private void OnVolumeButtonClick(object sender, RoutedEventArgs e)
    {
        // Mute is the whole job of the speaker now; the slider next to it handles everything else.
        _commitTimer.Stop();
        _hasPending = false;
        _ = SpotifyService.Instance.ToggleMuteAsync();
    }

    private void OnVolumeSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;

        var next = Math.Clamp((int)Math.Round(e.NewValue), 0, 100);
        // ValueChanged also fires when the OneWay binding pushes a poll result in. Those are echoes of
        // the device's own state, not the user moving anything, and sending them back would be noise.
        if (next == SpotifyService.Instance.VolumePercent) return;

        Queue(next);
    }

    private void OnVolumeWheel(object sender, MouseWheelEventArgs e)
    {
        if (!SpotifyService.Instance.SupportsVolume) return;

        var next = Math.Clamp(SpotifyService.Instance.VolumePercent + (e.Delta > 0 ? WheelStep : -WheelStep), 0, 100);
        e.Handled = true;   // don't let the titlebar or anything under it scroll instead
        if (next == SpotifyService.Instance.VolumePercent) return;

        Queue(next);
    }

    /// <summary>Show the new value immediately, and schedule the single network write that follows it.</summary>
    private void Queue(int volumePercent)
    {
        _pendingVolume = volumePercent;
        _hasPending = true;
        SpotifyService.Instance.PreviewVolume(volumePercent);
        _commitTimer.Stop();
        _commitTimer.Start();
    }

    private void OnCommitTimerTick(object? sender, EventArgs e) => Flush();

    private void Flush()
    {
        _commitTimer.Stop();
        if (!_hasPending) return;
        _hasPending = false;
        _ = SpotifyService.Instance.SetVolumeAsync(_pendingVolume);
    }
}
