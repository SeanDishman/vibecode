using System.Windows;
using System.Windows.Threading;
using VibeCode.Services;

namespace VibeCode.UI;

/// <summary>Only an explicit Open creates the display and its timer. Reporting has an independent lifetime.</summary>
internal sealed class MitreMonitorController : IDisposable
{
    private readonly Window _owner;
    private readonly DispatcherTimer _tick;
    private readonly MitreMonitorViewModel _model;
    private MitreMonitorWindow? _window;
    private bool _disposed;

    public MitreMonitorController(Window owner, MainViewModel main)
    {
        _owner = owner;
        _model = new MitreMonitorViewModel(main);
        _tick = new DispatcherTimer(DispatcherPriority.Background, owner.Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += OnTick;
        owner.Closed += OnOwnerClosed;
        AppSettings.Changed += OnSettingsChanged;
    }

    private void OnTick(object? sender, EventArgs e) => Refresh();
    private void OnOwnerClosed(object? sender, EventArgs e) => Dispose();
    private void OnSettingsChanged() => _owner.Dispatcher.BeginInvoke(new Action(() =>
    {
        if (!_disposed && !AppSettings.Current.MitreMonitorEnabled) _window?.Close();
    }));
    private void Refresh()
    {
        if (!_disposed && _window is not null) _model.Refresh();
    }

    public void Open()
    {
        if (_disposed) return;
        if (_window is not null)
        {
            if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
            _window.Activate();
            return;
        }
        if (!AppSettings.Current.MitreMonitorEnabled)
        {
            AppSettings.Current.MitreMonitorEnabled = true;
            AppSettings.Current.Save();
        }
        _model.Refresh();
        _window = new MitreMonitorWindow(_model) { Owner = _owner };
        _window.Closed += OnMonitorClosed;
        try
        {
            _window.PlaceForOwner(_owner);
            _window.Show();
            _tick.Start();
        }
        catch
        {
            _window?.Close();
            throw;
        }
    }

    private void OnMonitorClosed(object? sender, EventArgs e)
    {
        if (sender is MitreMonitorWindow window) window.Closed -= OnMonitorClosed;
        _window = null;
        _tick.Stop();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tick.Stop();
        _tick.Tick -= OnTick;
        _owner.Closed -= OnOwnerClosed;
        AppSettings.Changed -= OnSettingsChanged;
        _window?.Close();
    }
}
