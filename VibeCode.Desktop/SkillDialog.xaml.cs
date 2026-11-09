using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using VibeCode.Services;
using VibeCode.UI;

namespace VibeCode;

/// <summary>
/// Adds a skill: the AI (optionally) finds where it is published, the user reviews the source, and VibeCode downloads,
/// checks and installs it into the selected CLIs' skills folders.
/// </summary>
public partial class SkillDialog : Window
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private CancellationTokenSource? _aiGeneration;
    private CancellationTokenSource? _install;
    // The AI draft's skill name: only a hint that helps find the right SKILL.md when the folder is slightly off.
    private string _suggestedName = "";

    public SkillInstallResult? Result { get; private set; }

    public SkillDialog()
    {
        InitializeComponent();
        AiProviderBox.SelectedValue = ProviderModelCatalog.Normalize(AppSettings.Current.DefaultProvider);
        if (AiProviderBox.SelectedIndex < 0) AiProviderBox.SelectedIndex = 0;
        RefreshAiModels();
        TargetsText.Text = $"Claude: {SkillCatalog.ClaudeSkillsRoot}, and each Claude account VibeCode runs (copied when its next chat starts). "
                           + $"Codex: {SkillCatalog.CodexSkillsRoot}, which every Codex account shares. "
                           + "A skill with the same name installed some other way is never replaced.";
        Loaded += (_, _) => EnableDarkTitleBar();
        Closed += (_, _) =>
        {
            _aiGeneration?.Cancel();
            _install?.Cancel();
        };
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

    private void OnAiProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AiModelBox is null) return;
        RefreshAiModels();
    }

    private void RefreshAiModels()
    {
        var provider = AiProviderBox.SelectedValue as string ?? "claude";
        var previous = AiModelBox.SelectedValue as string;
        AiModelBox.ItemsSource = ProviderModelCatalog.For(provider).ToList();
        AiModelBox.SelectedValue = previous;
        if (AiModelBox.SelectedIndex < 0) AiModelBox.SelectedIndex = 0;
    }

    private async void OnGenerateWithAi(object sender, RoutedEventArgs e)
    {
        var request = AiPromptBox.Text.Trim();
        if (request.Length == 0)
        {
            ShowAiStatus("Name or describe the skill you want the AI to find.", isError: true);
            AiPromptBox.Focus();
            return;
        }
        if (_aiGeneration is not null || _install is not null) return;

        var provider = AiProviderBox.SelectedValue as string ?? "claude";
        var model = AiModelBox.SelectedValue as string;
        var generation = new CancellationTokenSource();
        _aiGeneration = generation;
        var logLines = new List<string>();
        SetAiBusy(true, "Starting AI assistant…");
        try
        {
            var progress = new Progress<string>(status =>
            {
                if (!ReferenceEquals(_aiGeneration, generation) || generation.IsCancellationRequested) return;
                AppendAiLog(logLines, status);
            });
            var suggestion = await SkillAssistantService.GenerateAsync(provider, model, request, AppSettings.Dir, generation.Token, progress);
            if (!ReferenceEquals(_aiGeneration, generation) || generation.IsCancellationRequested) return;
            AppendAiLog(logLines, "Skill source ready.");
            var value = suggestion.Definition;
            _suggestedName = SkillCatalog.IsValidName(value.Name) ? value.Name : "";
            SourceBox.Text = value.Source;
            FolderBox.Text = value.Folder;
            RefBox.Text = value.Ref;
            ClaudeCheck.IsChecked = value.UseClaude;
            CodexCheck.IsChecked = value.UseCodex;
            HideValidation();
            var problems = SkillCatalog.Validate(value);
            if (problems.Count > 0) ShowValidation(problems);
            var detail = string.IsNullOrWhiteSpace(suggestion.Notes) ? "" : "\n\n" + suggestion.Notes;
            ShowAiStatus("Source filled in. Check that it is the skill's official source, then choose Install skill." + detail, isError: false);
        }
        catch (OperationCanceledException) when (generation.IsCancellationRequested)
        {
            // Closing the dialog cancels the private provider turn; there is no stale result to display.
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_aiGeneration, generation))
            {
                AppendAiLog(logLines, "Failed: " + ex.Message);
                ShowAiStatus(ex.Message, isError: true);
            }
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

    private void AppendAiLog(List<string> logLines, string status)
    {
        if (string.IsNullOrWhiteSpace(status)) return;
        AiWorkingText.Text = status;
        var entry = $"[{DateTime.Now:HH:mm:ss}] {status}";
        if (logLines.Count > 0 && logLines[^1].EndsWith("] " + status, StringComparison.Ordinal)) return;
        logLines.Add(entry);
        AiLogText.Text = string.Join(Environment.NewLine, logLines);
        AiLogPanel.Visibility = Visibility.Visible;
        AiLogScroll.ScrollToEnd();
    }

    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        if (_install is not null || _aiGeneration is not null) return;
        var request = new SkillDefinition
        {
            Name = _suggestedName,
            Source = SourceBox.Text.Trim(),
            Folder = FolderBox.Text.Trim(),
            Ref = RefBox.Text.Trim(),
            UseClaude = ClaudeCheck.IsChecked == true,
            UseCodex = CodexCheck.IsChecked == true,
        };
        var problems = SkillCatalog.Validate(request);
        if (problems.Count > 0)
        {
            ShowValidation(problems);
            return;
        }
        HideValidation();
        var install = new CancellationTokenSource();
        _install = install;
        SetInstallBusy(true, "Starting…");
        try
        {
            var progress = new Progress<string>(message => InstallStatusText.Text = message);
            Result = await SkillCatalog.InstallAsync(request, Http, progress, install.Token);
            DialogResult = true;
        }
        catch (OperationCanceledException) when (install.IsCancellationRequested)
        {
            // The dialog closed during the download; nothing was installed.
        }
        catch (HttpRequestException ex)
        {
            ShowValidation(new[] { "The download failed: " + ex.Message });
        }
        catch (Exception ex)
        {
            ShowValidation(new[] { ex.Message });
        }
        finally
        {
            if (ReferenceEquals(_install, install))
            {
                _install = null;
                if (IsLoaded) SetInstallBusy(false, "");
            }
            install.Dispose();
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _install?.Cancel();
        DialogResult = false;
    }

    private void SetAiBusy(bool busy, string? status = null)
    {
        AiGenerateButton.IsEnabled = !busy;
        AiProviderBox.IsEnabled = !busy;
        AiModelBox.IsEnabled = !busy;
        AiPromptBox.IsEnabled = !busy;
        InstallButton.IsEnabled = !busy;
        if (status is not null) AiWorkingText.Text = status;
        AiWorkingText.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        AiProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy)
        {
            AiLogText.Text = "";
            AiLogPanel.Visibility = Visibility.Collapsed;
            AiStatusPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void SetInstallBusy(bool busy, string status)
    {
        InstallButton.IsEnabled = !busy;
        AiGenerateButton.IsEnabled = !busy;
        SourceBox.IsEnabled = FolderBox.IsEnabled = RefBox.IsEnabled = !busy;
        ClaudeCheck.IsEnabled = CodexCheck.IsEnabled = !busy;
        InstallProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        InstallStatusText.Text = status;
        InstallStatusText.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowAiStatus(string message, bool isError)
    {
        AiStatusText.Text = message;
        AiStatusPanel.SetResourceReference(Border.BackgroundProperty, isError ? "RedSoft" : "GreenSoft");
        AiStatusText.SetResourceReference(TextBlock.ForegroundProperty, isError ? "Red" : "Green");
        AiStatusPanel.Visibility = Visibility.Visible;
    }

    private void ShowValidation(IEnumerable<string> errors)
    {
        ValidationText.Text = string.Join(Environment.NewLine, errors.Select(error => "• " + error));
        ValidationPanel.Visibility = Visibility.Visible;
    }

    private void HideValidation() => ValidationPanel.Visibility = Visibility.Collapsed;
}
