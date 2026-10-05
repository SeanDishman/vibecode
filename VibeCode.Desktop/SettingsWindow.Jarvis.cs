using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.ComponentModel;
using VibeCode.Services;
using VibeCode.UI;

namespace VibeCode;

internal sealed record JarvisProviderOption(string Id, string Name);

public partial class SettingsWindow
{
    private bool _refreshingJarvisSettings;
    private CancellationTokenSource? _jarvisVoicePreview;
    private JarvisViewModel? _jarvisSettingsVm;
    private bool _jarvisMicTesting;
    private string _jarvisMicDraft = "";

    /// <summary>Opens a category by name. Notifications, Privacy, Projects, About and MCP servers were categories
    /// of their own until the rail was consolidated; those names are still what Jarvis's settings catalog, the
    /// rest of the app and people use, so each one lands on the section that replaced it rather than nowhere.</summary>
    public void SelectCategory(string category)
    {
        var name = category.Trim();
        (string Rail, FrameworkElement? Section) destination = name.ToLowerInvariant() switch
        {
            "notifications" => ("General", SectionNotifications),
            "privacy" => ("General", SectionPrivacy),
            "projects" => ("General", SectionProjects),
            "hidden projects" => ("General", HiddenProjectsCard),   // the sidebar's "N hidden" link
            "about" => ("General", SectionAbout),
            "mcp servers" or "mcp" => ("MCP and extensions", McpServersCard),
            "extensions" => ("MCP and extensions", SectionExtensions),
            "mcp & extensions" => ("MCP and extensions", null),
            "bridge" or "bridge & agents" => ("Bridge and agents", null),
            _ => (name, null),
        };
        for (var index = 0; index < Rail.Items.Count; index++)
        {
            if (Rail.Items[index] is not ListBoxItem item
                || !AutomationProperties.GetName(item).Equals(destination.Rail, StringComparison.OrdinalIgnoreCase)) continue;
            Rail.SelectedIndex = index;
            if (destination.Section is { } section) Reveal(section, focus: false);
            return;
        }
    }

    private void InitializeJarvisSettings()
    {
        _refreshingJarvisSettings = true;
        try
        {
            var settings = AppSettings.Current;
            JarvisProviderPicker.ItemsSource = BridgeAgentConfigurationPolicy.Providers
                .Select(id => new JarvisProviderOption(id, ProviderModelCatalog.DisplayName(id))).ToArray();
            JarvisProviderPicker.SelectedValue = settings.JarvisProvider;
            RebuildJarvisModels(settings.JarvisModel);
            JarvisVoiceToggle.IsChecked = settings.JarvisVoiceEnabled;
            var voices = JarvisSpeechService.GetVoices().ToList();
            if (voices.All(voice => voice.Id != settings.JarvisVoiceId))
                voices.Add(new JarvisVoiceChoice(settings.JarvisVoiceId, settings.JarvisVoiceId + " (unavailable saved voice)"));
            JarvisVoicePicker.ItemsSource = voices;
            JarvisVoicePicker.SelectedValue = settings.JarvisVoiceId;
            if (JarvisVoicePicker.SelectedItem is null) JarvisVoicePicker.SelectedIndex = 0;
            JarvisSpeedSlider.Value = settings.JarvisSpeechRate;
            JarvisVolumeSlider.Value = settings.JarvisSpeechVolume;
            // Both status lines are for live feedback only (a preview playing, a transcript, a fault). What they
            // used to say while idle is the Voice and Microphone test rows' info text now.
            JarvisVoiceStatus.Text = "";
            if (_jarvisSettingsVm is not null) _jarvisSettingsVm.PropertyChanged -= OnJarvisSettingsStateChanged;
            _jarvisSettingsVm = (Owner as MainWindow)?.JarvisAssistant;
            if (_jarvisSettingsVm is not null) _jarvisSettingsVm.PropertyChanged += OnJarvisSettingsStateChanged;
            JarvisMicrophoneTestButton.IsEnabled = _jarvisSettingsVm?.CanListen == true;
            JarvisMicrophoneStatus.Text = _jarvisSettingsVm?.HasMicrophone == true
                ? "" : "Connect a microphone to test voice input.";
            Closed -= OnJarvisSettingsClosed;
            Closed += OnJarvisSettingsClosed;
        }
        finally { _refreshingJarvisSettings = false; }
    }

    private void RebuildJarvisModels(string? selectedModel)
    {
        var provider = JarvisProviderPicker.SelectedValue as string ?? AppSettings.Current.JarvisProvider;
        var models = BridgeAgentConfigurationPolicy.ModelsFor(provider).ToList();
        if (!string.IsNullOrWhiteSpace(selectedModel) && models.All(m => !string.Equals(m.Value, selectedModel, StringComparison.OrdinalIgnoreCase)))
            models.Insert(0, new ModelChoice { Provider = provider, Value = selectedModel, Display = selectedModel + " (saved)", Description = "Saved choice; not listed in the current catalog." });
        JarvisModelPicker.ItemsSource = models;
        JarvisModelPicker.SelectedValue = selectedModel;
        if (JarvisModelPicker.SelectedItem is null)
            JarvisModelPicker.SelectedItem = models.FirstOrDefault(m => provider == "codex" && m.Value == "gpt-6-luna")
                ?? models.FirstOrDefault(m => m.Value == "default") ?? models.FirstOrDefault();
        RebuildJarvisEfforts();
    }

    private void RebuildJarvisEfforts()
    {
        var provider = JarvisProviderPicker.SelectedValue as string ?? "codex";
        var efforts = BridgeAgentConfigurationPolicy.EffortsFor(provider, JarvisModelPicker.SelectedItem as ModelChoice, AppSettings.Current.JarvisEffort);
        JarvisEffortPicker.ItemsSource = efforts;
        JarvisEffortPicker.SelectedItem = efforts.FirstOrDefault(e => string.Equals(e.Value, AppSettings.Current.JarvisEffort, StringComparison.OrdinalIgnoreCase))
            ?? efforts.FirstOrDefault(e => e.Value is null) ?? efforts.FirstOrDefault();
        JarvisEffortPicker.IsEnabled = efforts.Count > 1;
    }

    private bool CanEditJarvisSettings => _ready && !_refreshingJarvisSettings;

    private void OnJarvisProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!CanEditJarvisSettings || JarvisProviderPicker.SelectedValue is not string provider) return;
        var settings = AppSettings.Current;
        var previous = (settings.JarvisProvider, settings.JarvisModel, settings.JarvisEffort);
        _refreshingJarvisSettings = true;
        try
        {
            settings.JarvisProvider = provider;
            RebuildJarvisModels(null);
            settings.JarvisModel = (JarvisModelPicker.SelectedItem as ModelChoice)?.Value;
            settings.JarvisEffort = (JarvisEffortPicker.SelectedItem as EffortChoice)?.Value;
        }
        finally { _refreshingJarvisSettings = false; }
        SaveJarvisSetting(() => (settings.JarvisProvider, settings.JarvisModel, settings.JarvisEffort) = previous);
    }

    private void OnJarvisModelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!CanEditJarvisSettings || JarvisModelPicker.SelectedItem is not ModelChoice model) return;
        var settings = AppSettings.Current;
        var previous = (settings.JarvisModel, settings.JarvisEffort);
        settings.JarvisModel = model.Value;
        _refreshingJarvisSettings = true;
        try { RebuildJarvisEfforts(); settings.JarvisEffort = (JarvisEffortPicker.SelectedItem as EffortChoice)?.Value; }
        finally { _refreshingJarvisSettings = false; }
        SaveJarvisSetting(() => (settings.JarvisModel, settings.JarvisEffort) = previous);
    }

    private void OnJarvisEffortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!CanEditJarvisSettings || JarvisEffortPicker.SelectedItem is not EffortChoice effort) return;
        var previous = AppSettings.Current.JarvisEffort;
        AppSettings.Current.JarvisEffort = effort.Value;
        SaveJarvisSetting(() => AppSettings.Current.JarvisEffort = previous);
    }

    private void OnJarvisVoiceEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (!CanEditJarvisSettings) return;
        var previous = AppSettings.Current.JarvisVoiceEnabled;
        AppSettings.Current.JarvisVoiceEnabled = JarvisVoiceToggle.IsChecked == true;
        if (!AppSettings.Current.JarvisVoiceEnabled) _jarvisVoicePreview?.Cancel();
        SaveJarvisSetting(() => AppSettings.Current.JarvisVoiceEnabled = previous);
    }

    private void OnJarvisVoiceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!CanEditJarvisSettings || JarvisVoicePicker.SelectedValue is not string voice) return;
        var previous = AppSettings.Current.JarvisVoiceId;
        AppSettings.Current.JarvisVoiceId = voice;
        SaveJarvisSetting(() => AppSettings.Current.JarvisVoiceId = previous);
    }

    // The readouts beside both sliders are the rows' own ValueText bindings; nothing to refresh here.
    private void OnJarvisSpeedChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!CanEditJarvisSettings) return;
        var previous = AppSettings.Current.JarvisSpeechRate;
        AppSettings.Current.JarvisSpeechRate = e.NewValue;
        SaveJarvisSetting(() => AppSettings.Current.JarvisSpeechRate = previous);
    }

    private void OnJarvisVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!CanEditJarvisSettings) return;
        var previous = AppSettings.Current.JarvisSpeechVolume;
        AppSettings.Current.JarvisSpeechVolume = (int)e.NewValue;
        SaveJarvisSetting(() => AppSettings.Current.JarvisSpeechVolume = previous);
    }

    private void SaveJarvisSetting(Action rollback)
    {
        if (AppSettings.Current.TrySave() is not { } error)
        { JarvisSettingsError.Visibility = Visibility.Collapsed; return; }
        rollback();
        InitializeJarvisSettings();
        JarvisSettingsError.Text = "Could not save Jarvis settings. " + error.Message;
        JarvisSettingsError.Visibility = Visibility.Visible;
    }

    private void OnSecondBrainEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        var previous = AppSettings.Current.SecondBrainEnabled;
        AppSettings.Current.SecondBrainEnabled = SecondBrainToggle.IsChecked == true;
        if (AppSettings.Current.TrySave() is not { } error) return;
        AppSettings.Current.SecondBrainEnabled = previous;
        _ready = false;
        SecondBrainToggle.IsChecked = previous;
        _ready = true;
        System.Windows.MessageBox.Show(this, "Could not save the Second Brain setting. " + error.Message,
            "Settings not saved", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OnOpenSecondBrainSettings(object sender, RoutedEventArgs e)
    {
        if (AppSettings.Current.SecondBrainEnabled) MemoryMapWindow.Open(Owner ?? this);
    }

    private async void OnPreviewJarvisVoice(object sender, RoutedEventArgs e)
    {
        _jarvisVoicePreview?.Cancel();
        using var preview = new CancellationTokenSource();
        _jarvisVoicePreview = preview;
        JarvisPreviewButton.IsEnabled = false;
        JarvisStopPreviewButton.IsEnabled = true;
        JarvisVoiceStatus.Text = "Playing voice preview…";
        try
        {
            var settings = AppSettings.Current;
            await JarvisSpeechService.Instance.SpeakAsync("Good afternoon. I'm Jarvis, ready to help with your projects.",
                settings.JarvisVoiceId, settings.JarvisSpeechRate, settings.JarvisSpeechVolume, preview.Token,
                progress => Dispatcher.BeginInvoke(new Action(() => JarvisVoiceStatus.Text = progress)));
            JarvisVoiceStatus.Text = JarvisSpeechService.Instance.VoiceDescription;
        }
        catch (OperationCanceledException) { JarvisVoiceStatus.Text = "Voice preview stopped."; }
        catch (Exception error) { JarvisVoiceStatus.Text = "Voice preview unavailable. " + error.Message; }
        finally
        {
            if (ReferenceEquals(_jarvisVoicePreview, preview)) _jarvisVoicePreview = null;
            JarvisPreviewButton.IsEnabled = true;
            JarvisStopPreviewButton.IsEnabled = false;
        }
    }

    private void OnStopJarvisVoice(object sender, RoutedEventArgs e) => _jarvisVoicePreview?.Cancel();
    private async void OnTestJarvisMicrophone(object sender, RoutedEventArgs e)
    {
        if (_jarvisSettingsVm is null) return;
        if (!_jarvisMicTesting)
        {
            _jarvisMicDraft = _jarvisSettingsVm.InputText;
            _jarvisSettingsVm.InputText = "";
            _jarvisMicTesting = true;
        }
        await _jarvisSettingsVm.TestMicrophoneAsync();
        if (!_jarvisSettingsVm.IsListening && !_jarvisSettingsVm.IsBusy)
        {
            if (!string.IsNullOrWhiteSpace(_jarvisSettingsVm.InputText))
                JarvisMicrophoneStatus.Text = "Heard: " + _jarvisSettingsVm.InputText;
            _jarvisSettingsVm.InputText = _jarvisMicDraft;
            _jarvisMicTesting = false;
        }
        RefreshJarvisMicrophoneControls();
    }

    private void OnStopJarvisMicrophoneTest(object sender, RoutedEventArgs e)
    {
        if (!_jarvisMicTesting || _jarvisSettingsVm is null) return;
        _jarvisSettingsVm.Cancel();
        _jarvisSettingsVm.InputText = _jarvisMicDraft;
        _jarvisMicTesting = false;
        JarvisMicrophoneStatus.Text = "Microphone test stopped.";
        RefreshJarvisMicrophoneControls();
    }

    private void OnJarvisSettingsStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_jarvisSettingsVm is null || !IsLoaded) return;
        if (_jarvisMicTesting) JarvisMicrophoneStatus.Text = _jarvisSettingsVm.StatusText;
        RefreshJarvisMicrophoneControls();
    }

    private void RefreshJarvisMicrophoneControls()
    {
        JarvisMicrophoneTestButton.Content = _jarvisSettingsVm?.IsListening == true && _jarvisMicTesting ? "Finish test" : "Test microphone";
        JarvisMicrophoneTestButton.IsEnabled = _jarvisSettingsVm?.CanListen == true;
        JarvisStopMicrophoneTestButton.IsEnabled = _jarvisMicTesting;
    }

    private void OnJarvisSettingsClosed(object? sender, EventArgs e)
    {
        _jarvisVoicePreview?.Cancel();
        if (_jarvisSettingsVm is null) return;
        _jarvisSettingsVm.PropertyChanged -= OnJarvisSettingsStateChanged;
        if (_jarvisMicTesting) { _jarvisSettingsVm.Cancel(); _jarvisSettingsVm.InputText = _jarvisMicDraft; _jarvisMicTesting = false; }
    }
}
