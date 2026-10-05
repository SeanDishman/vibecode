using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using VibeCode.Services;

namespace VibeCode.UI;

/// <summary>Chronological real-audio history, shown only inside the recording composer.</summary>
public sealed class MicLevelMeter : Control
{
    public static readonly DependencyProperty RecordingOwnerProperty = DependencyProperty.Register(
        nameof(RecordingOwner), typeof(object), typeof(MicLevelMeter),
        new PropertyMetadata(null, (d, _) => ((MicLevelMeter)d).SyncRecording()));

    /// <summary>Match the object passed to StartRecording. Null observes any live recording.</summary>
    public object? RecordingOwner
    {
        get => GetValue(RecordingOwnerProperty);
        set => SetValue(RecordingOwnerProperty, value);
    }

    private readonly DispatcherTimer _timer;
    private MicHistorySnapshot? _history;
    private bool _subscribed;
    private int _token;
    private long _announcedSecond = -1;

    public MicLevelMeter()
    {
        Height = 32;
        FontSize = 11;
        Visibility = Visibility.Collapsed;
        Focusable = false;
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
        ClipToBounds = true;
        // Blue is cyan in the CLI palette; dictation keeps the user's requested blue in both themes.
        SetResourceReference(ForegroundProperty, "WeatherBlue");
        AutomationProperties.SetName(this, "Microphone recording timeline");
        AutomationProperties.SetHelpText(this,
            "Captured audio from left to right. Taller blue bars show louder input; low bars show silence. Click the microphone again to stop and insert text.");
        ToolTip = "Recorded audio · latest 60 seconds · click the microphone again to stop and insert";
        _timer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher)
            { Interval = TimeSpan.FromMilliseconds(50) };
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += (_, _) => SyncRecording();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribed)
        {
            _subscribed = true;
            SpeechService.Instance.StateChanged += OnSpeechStateChanged;
            _timer.Tick += OnTick;
        }
        SyncRecording();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _subscribed = false;
        SpeechService.Instance.StateChanged -= OnSpeechStateChanged;
        _timer.Tick -= OnTick;
        Reset();
        SetCurrentValue(VisibilityProperty, Visibility.Collapsed);
    }

    private void OnSpeechStateChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        if (Dispatcher.CheckAccess()) SyncRecording();
        else
        {
            try { Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(SyncRecording)); }
            catch (InvalidOperationException) { /* dispatcher shut down between checks */ }
        }
    }

    private bool OwnsRecording => _subscribed && SpeechService.Instance.State == SpeechState.Recording
        && (RecordingOwner is null || ReferenceEquals(RecordingOwner, SpeechService.Instance.RecordingOwner));

    private void SyncRecording()
    {
        var active = OwnsRecording;
        var token = active ? SpeechService.Instance.CurrentToken : 0;
        if (_token != token) { Reset(); _token = token; }
        SetCurrentValue(VisibilityProperty, active ? Visibility.Visible : Visibility.Collapsed);
        if (!active || !IsVisible) { Reset(); return; }
        ReadHistory();
        if (!_timer.IsEnabled) _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (!OwnsRecording || !IsVisible) { SyncRecording(); return; }
        ReadHistory();
    }

    private void ReadHistory()
    {
        var history = SpeechService.Instance.LevelHistory;
        if (history?.CaptureToken != _token) history = null;
        if (_history?.SampleCount == history?.SampleCount) return;
        _history = history;
        var second = (long)(history?.DurationSeconds ?? 0);
        if (second != _announcedSecond)
        {
            _announcedSecond = second;
            AutomationProperties.SetName(this, $"Microphone recording timeline, {second} seconds captured");
        }
        InvalidateVisual();
    }

    private void Reset()
    {
        _timer.Stop();
        _history = null;
        _token = 0;
        _announcedSecond = -1;
        AutomationProperties.SetName(this, "Microphone recording timeline");
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size constraint)
        => new(Math.Min(180, constraint.Width), Math.Min(Height, constraint.Height));

    protected override AutomationPeer OnCreateAutomationPeer() => new TimelineAutomationPeer(this);

    private sealed class TimelineAutomationPeer(MicLevelMeter owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(MicLevelMeter);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (!OwnsRecording || Foreground is null || ActualWidth <= 0 || ActualHeight <= 0) return;
        var duration = TimeSpan.FromSeconds(_history?.DurationSeconds ?? 0);
        var elapsed = duration.TotalHours >= 1 ? duration.ToString(@"h\:mm\:ss") : duration.ToString(@"m\:ss");
        var text = new FormattedText(elapsed, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontSize, Foreground,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var showTime = ActualWidth >= text.Width + 60;
        var graphWidth = Math.Max(0, ActualWidth - (showTime ? text.Width + 12 : 0));
        var columns = Math.Max(1, (int)(graphWidth / 4));
        var step = graphWidth / columns;
        var width = Math.Min(2.5, step * 0.65);
        var minimum = Math.Min(2, ActualHeight);
        var future = Foreground.CloneCurrentValue();
        future.Opacity *= 0.22;
        for (var i = 0; i < columns; i++)
        {
            var level = _history?.GetDisplayLevel(i, columns) ?? double.NaN;
            var height = minimum + Math.Max(0, ActualHeight - minimum - 4) * (double.IsNaN(level) ? 0 : level);
            dc.DrawRoundedRectangle(double.IsNaN(level) ? future : Foreground, null,
                new Rect(step * (i + 0.5) - width / 2, (ActualHeight - height) / 2, width, height),
                width / 2, width / 2);
        }
        if (showTime) dc.DrawText(text, new Point(ActualWidth - text.Width, (ActualHeight - text.Height) / 2));
    }
}
