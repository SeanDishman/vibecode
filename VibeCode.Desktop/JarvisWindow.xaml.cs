using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using VibeCode.UI;

namespace VibeCode;

public partial class JarvisWindow : Window
{
    private readonly JarvisViewModel _jarvis;
    private readonly Action _openSettings;
    private readonly Action _openAppSettings;

    public JarvisWindow(JarvisViewModel jarvis, Action openSettings, Action? openAppSettings = null)
    {
        _jarvis = jarvis;
        _openSettings = openSettings;
        _openAppSettings = openAppSettings ?? openSettings;
        InitializeComponent();
        DataContext = jarvis;
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        MinWidth = Math.Min(MinWidth, SystemParameters.WorkArea.Width);
        MinHeight = Math.Min(MinHeight, SystemParameters.WorkArea.Height);
        if (Environment.GetEnvironmentVariable("VIBECODE_HIDDEN") == "1")
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = 6100; Top = 300;
        }
        Loaded += (_, _) =>
        {
            var dark = 1;
            try { DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int)); }
            catch (DllNotFoundException) { }
            if (IsActive) MessageBox.Focus();
        };
        _jarvis.Messages.CollectionChanged += OnMessagesChanged;
        Closing += OnClosing;
        _jarvis.WarmUpVoice();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (IsLoaded && ConversationList.Items.Count > 0) ConversationList.ScrollIntoView(ConversationList.Items[ConversationList.Items.Count - 1]);
        }));

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _jarvis.Messages.CollectionChanged -= OnMessagesChanged;
        _jarvis.Cancel();
    }
    private async void OnSubmit(object sender, RoutedEventArgs e)
    {
        if (SendButton.IsEnabled) await _jarvis.SubmitAsync();
        if (IsActive) MessageBox.Focus();
    }
    private async void OnTalkClick(object sender, RoutedEventArgs e)
    {
        // Mouse, keyboard and accessibility activation share the same recording toggle.
        if (_jarvis.IsBusy || _jarvis.IsSpeaking) _jarvis.Cancel();
        else await _jarvis.ToggleListeningAsync();
    }
    private void OnClear(object sender, RoutedEventArgs e) => _jarvis.ClearConversation();
    private void OnSettings(object sender, RoutedEventArgs e) => _openSettings();
    private void OnAppSettings(object sender, RoutedEventArgs e) => _openAppSettings();
    private void OnOptions(object sender, RoutedEventArgs e)
    {
        OptionsButton.ContextMenu.PlacementTarget = OptionsButton;
        OptionsButton.ContextMenu.IsOpen = !OptionsButton.ContextMenu.IsOpen;
    }
    private async void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        e.Handled = true;
        if (SendButton.IsEnabled) await _jarvis.SubmitAsync();
    }
    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        if (_jarvis.IsBusy || _jarvis.IsListening || _jarvis.IsSpeaking) _jarvis.Cancel();
        else Close();
    }
}
