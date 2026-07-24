using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using VibeCode.Services;
using VibeCode.UI;

namespace VibeCode;

public partial class McpServerDialog : Window
{
    private readonly List<McpServerDefinition> _catalog;
    private readonly string _id;
    private CancellationTokenSource? _aiGeneration;

    public McpServerDefinition? Result { get; private set; }

    public McpServerDialog(IEnumerable<McpServerDefinition> catalog, McpServerDefinition? existing = null)
    {
        InitializeComponent();
        _catalog = McpCatalog.Snapshot(catalog);
        var value = existing?.Clone() ?? new McpServerDefinition();
        _id = value.Id;
        if (existing is not null)
        {
            Title = Heading.Text = "Edit MCP server";
            NameBox.Text = value.Name;
            CommandBox.Text = value.Command;
            ArgumentsBox.Text = string.Join(Environment.NewLine, value.Arguments);
            EnvironmentBox.Text = FormatPairs(value.Environment);
            UrlBox.Text = value.Url;
            HeadersBox.Text = FormatPairs(value.Headers);
            BearerBox.Text = value.BearerTokenEnvironmentVariable ?? "";
            ClaudeCheck.IsChecked = value.UseClaude;
            CodexCheck.IsChecked = value.UseCodex;
            KimiCheck.IsChecked = value.UseKimi;
            GrokCheck.IsChecked = value.UseGrok;
            EnabledCheck.IsChecked = value.Enabled;
            StartupTimeoutBox.Text = value.StartupTimeoutSeconds.ToString();
            ToolTimeoutBox.Text = value.ToolTimeoutSeconds.ToString();
        }
        TransportBox.SelectedValue = value.Transport;
        if (TransportBox.SelectedIndex < 0) TransportBox.SelectedIndex = 0;
        AiProviderBox.SelectedValue = ProviderModelCatalog.Normalize(AppSettings.Current.DefaultProvider);
        if (AiProviderBox.SelectedIndex < 0) AiProviderBox.SelectedIndex = 0;
        RefreshAiModels();
        Loaded += (_, _) => EnableDarkTitleBar();
        Closed += (_, _) => _aiGeneration?.Cancel();
    }

    private void EnableDarkTitleBar()
    {
        try
        {
            var enabled = 1;
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref enabled, sizeof(int));
        }
        catch { }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int attrValue, int attrSize);

    private void OnTransportChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StdioPanel is null) return;
        var transport = TransportBox.SelectedValue as string ?? McpCatalog.StdioTransport;
        var stdio = transport == McpCatalog.StdioTransport;
        StdioPanel.Visibility = stdio ? Visibility.Visible : Visibility.Collapsed;
        RemotePanel.Visibility = stdio ? Visibility.Collapsed : Visibility.Visible;
        var sse = transport == McpCatalog.SseTransport;
        if (sse) CodexCheck.IsChecked = false;
        CodexCheck.IsEnabled = !sse;
        TransportCompatibility.Text = sse
            ? "Current Codex releases do not support legacy SSE. Claude, Kimi, and Grok remain available; use Streamable HTTP when the server offers it."
            : "";
        TransportCompatibility.Visibility = sse ? Visibility.Visible : Visibility.Collapsed;
        HideValidation();
    }

    private void OnAiProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AiModelBox is null) return;
        RefreshAiModels();
    }

    private void RefreshAiModels()
    {
        var provider = AiProviderBox.SelectedValue as string ?? "claude";
        var models = ProviderModelCatalog.For(provider).ToList();
        var previous = AiModelBox.SelectedValue as string;
        AiModelBox.ItemsSource = models;
        AiModelBox.SelectedValue = previous;
        if (AiModelBox.SelectedIndex < 0) AiModelBox.SelectedIndex = 0;
    }

    private async void OnGenerateWithAi(object sender, RoutedEventArgs e)
    {
        var request = AiPromptBox.Text.Trim();
        if (request.Length == 0)
        {
            ShowAiStatus("Describe the MCP server you want the AI to configure.", isError: true);
            AiPromptBox.Focus();
            return;
        }
        if (_aiGeneration is not null) return;

        var provider = AiProviderBox.SelectedValue as string ?? "claude";
        var model = AiModelBox.SelectedValue as string;
        var generation = new CancellationTokenSource();
        _aiGeneration = generation;
        SetAiBusy(true);
        try
        {
            var suggestion = await McpConfigAssistantService.GenerateAsync(provider, model, request,
                AppSettings.Dir, generation.Token);
            if (!ReferenceEquals(_aiGeneration, generation) || generation.IsCancellationRequested) return;
            ApplyAiSuggestion(suggestion.Definition);
            var valid = TryBuild(out _, out var errors, out var warnings);
            ShowValidation(errors, warnings, showSuccess: valid);
            var detail = string.IsNullOrWhiteSpace(suggestion.Notes) ? "" : "\n\n" + suggestion.Notes;
            ShowAiStatus("Draft filled in. Review the command, package or URL, prerequisites, and any warnings before saving."
                         + detail, isError: false);
        }
        catch (OperationCanceledException) when (generation.IsCancellationRequested)
        {
            // Closing the dialog cancels the private provider turn; there is no stale result to display.
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_aiGeneration, generation)) ShowAiStatus(ex.Message, isError: true);
        }
        finally
        {
            if (ReferenceEquals(_aiGeneration, generation))
            {
                _aiGeneration = null;
                SetAiBusy(false);
            }
            generation.Dispose();
        }
    }

    private void ApplyAiSuggestion(McpServerDefinition value)
    {
        NameBox.Text = value.Name;
        TransportBox.SelectedValue = value.Transport;
        if (TransportBox.SelectedIndex < 0) TransportBox.SelectedValue = McpCatalog.StdioTransport;
        CommandBox.Text = value.Command;
        ArgumentsBox.Text = string.Join(Environment.NewLine, value.Arguments);
        EnvironmentBox.Text = FormatPairs(value.Environment);
        UrlBox.Text = value.Url;
        HeadersBox.Text = FormatPairs(value.Headers);
        BearerBox.Text = value.BearerTokenEnvironmentVariable ?? "";
        ClaudeCheck.IsChecked = value.UseClaude;
        CodexCheck.IsChecked = value.UseCodex && value.Transport != McpCatalog.SseTransport;
        KimiCheck.IsChecked = value.UseKimi;
        GrokCheck.IsChecked = value.UseGrok;
        EnabledCheck.IsChecked = true;
        StartupTimeoutBox.Text = value.StartupTimeoutSeconds.ToString();
        ToolTimeoutBox.Text = value.ToolTimeoutSeconds.ToString();
        HideValidation();
    }

    private void SetAiBusy(bool busy)
    {
        AiGenerateButton.IsEnabled = !busy;
        AiProviderBox.IsEnabled = !busy;
        AiModelBox.IsEnabled = !busy;
        AiPromptBox.IsEnabled = !busy;
        AiWorkingText.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        AiProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy) AiStatusPanel.Visibility = Visibility.Collapsed;
    }

    private void ShowAiStatus(string message, bool isError)
    {
        AiStatusText.Text = message;
        AiStatusPanel.SetResourceReference(Border.BackgroundProperty, isError ? "RedSoft" : "GreenSoft");
        AiStatusText.SetResourceReference(TextBlock.ForegroundProperty, isError ? "Red" : "Green");
        AiStatusPanel.Visibility = Visibility.Visible;
    }
    private void OnBrowseCommand(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose MCP server executable",
            Filter = "Executables (*.exe;*.cmd;*.bat)|*.exe;*.cmd;*.bat|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) == true) CommandBox.Text = dialog.FileName;
    }

    private void OnValidate(object sender, RoutedEventArgs e)
    {
        if (TryBuild(out _, out var errors, out var warnings)) ShowValidation(errors, warnings, showSuccess: true);
        else ShowValidation(errors, warnings, showSuccess: false);
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!TryBuild(out var value, out var errors, out var warnings))
        {
            ShowValidation(errors, warnings, showSuccess: false);
            return;
        }
        if (warnings.Count > 0)
        {
            ShowValidation(errors, warnings, showSuccess: false);
            if (MessageBox.Show(this, string.Join(Environment.NewLine, warnings.Select(warning => "• " + warning))
                                      + "\n\nSave this server anyway?",
                    "MCP configuration warnings", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
        }
        Result = value;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private bool TryBuild(out McpServerDefinition value, out List<string> errors, out List<string> warnings)
    {
        errors = new List<string>();
        warnings = new List<string>();
        var environment = ParsePairs(EnvironmentBox.Text, "Environment", errors);
        var headers = ParsePairs(HeadersBox.Text, "Header", errors);
        if (!int.TryParse(StartupTimeoutBox.Text.Trim(), out var startup))
            errors.Add("Startup timeout must be a whole number of seconds.");
        if (!int.TryParse(ToolTimeoutBox.Text.Trim(), out var tool))
            errors.Add("Tool timeout must be a whole number of seconds.");

        var transport = TransportBox.SelectedValue as string ?? McpCatalog.StdioTransport;
        value = new McpServerDefinition
        {
            Id = _id,
            Name = NameBox.Text.Trim(),
            Transport = transport,
            Enabled = EnabledCheck.IsChecked == true,
            UseClaude = ClaudeCheck.IsChecked == true,
            UseCodex = CodexCheck.IsChecked == true && transport != McpCatalog.SseTransport,
            UseKimi = KimiCheck.IsChecked == true,
            UseGrok = GrokCheck.IsChecked == true,
            Command = CommandBox.Text.Trim(),
            Arguments = ArgumentsBox.Text.Replace("\r\n", "\n").Split('\n')
                .Where(argument => argument.Length > 0).ToList(),
            Environment = environment,
            Url = UrlBox.Text.Trim(),
            Headers = headers,
            BearerTokenEnvironmentVariable = NullIfWhiteSpace(BearerBox.Text),
            StartupTimeoutSeconds = startup,
            ToolTimeoutSeconds = tool,
        };

        foreach (var message in McpCatalog.Validate(value, _catalog))
            (message.Severity == McpValidationSeverity.Error ? errors : warnings).Add(message.Message);

        var prospective = _catalog.Where(server => !string.Equals(server.Id, _id, StringComparison.OrdinalIgnoreCase))
            .Select(server => server.Clone()).Append(value).ToList();
        try
        {
            var codexProjection = McpCatalog.BuildCodexProjection(prospective);
            var codexCharacters = codexProjection.ConfigOverrides.Sum(text => text.Length + 3);
            if (codexCharacters > McpCatalog.MaxCodexProjectionCharacters)
                errors.Add("The selected Codex definitions are too large for a safe Windows launch command. Shorten values or disable Codex on some servers.");
            var codexEnvironmentCharacters = codexProjection.Environment.Sum(pair => pair.Key.Length + pair.Value.Length + 2);
            if (codexEnvironmentCharacters > McpCatalog.MaxCodexProjectionEnvironmentCharacters)
                errors.Add("The selected Codex environment and headers are too large for a safe Windows launch. Shorten values or disable Codex on some servers.");
        }
        catch (InvalidOperationException ex) { errors.Add(ex.Message); }
        return errors.Count == 0;
    }

    private void ShowValidation(IReadOnlyCollection<string> errors, IReadOnlyCollection<string> warnings, bool showSuccess)
    {
        ValidationText.Text = string.Join(Environment.NewLine, errors.Select(error => "• " + error));
        WarningText.Text = string.Join(Environment.NewLine, warnings.Select(warning => "• " + warning));
        ValidationPanel.Visibility = errors.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        WarningPanel.Visibility = warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ValidPanel.Visibility = showSuccess && errors.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void HideValidation()
    {
        ValidationPanel.Visibility = Visibility.Collapsed;
        WarningPanel.Visibility = Visibility.Collapsed;
        ValidPanel.Visibility = Visibility.Collapsed;
    }

    private static Dictionary<string, string> ParsePairs(string text, string label, ICollection<string> errors)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (line.Length == 0) continue;
            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                errors.Add($"{label} line {index + 1} must use NAME=value.");
                continue;
            }
            var name = line[..separator].Trim();
            if (result.ContainsKey(name)) errors.Add($"{label} '{name}' is listed more than once.");
            else result[name] = line[(separator + 1)..];
        }
        return result;
    }

    private static string FormatPairs(IEnumerable<KeyValuePair<string, string>> pairs) =>
        string.Join(Environment.NewLine, pairs.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => $"{pair.Key}={pair.Value}"));

    private static string? NullIfWhiteSpace(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
