using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Automation;
using VibeCode.Services;
using VibeCode.Protocol;

namespace VibeCode.UI;

/// <summary>
/// Paste-an-API-key dialog. Built in code rather than XAML so adding it touches no shared markup.
/// The key is checked against the selected service before the dialog closes.
/// </summary>
public sealed class ApiKeyDialog : Window
{
    private readonly string _provider;
    private readonly PasswordBox _key = new();
    private readonly TextBox _label = new();
    private readonly TextBlock _status = new();
    private readonly Button _save = new();
    private readonly ComboBox _backend = new();
    private readonly TextBlock _hint = new();
    private readonly TextBlock _billing = new();
    private readonly Button _openAccount = new();
    private readonly Button _copyAccountLink = new();
    private readonly CancellationTokenSource _closed = new();

    public string ApiKey { get; private set; } = "";
    public string AccountLabel { get; private set; } = "";
    public string GlmBackend => _backend.SelectedValue as string ?? GlmPreset.Baseten;
    private string AccountUrl => GlmPreset.IsZai(GlmBackend) ? GlmPreset.ZaiAccountUrl : GlmPreset.BasetenAccountUrl;

    private static readonly Brush Bg = new SolidColorBrush(Color.FromRgb(0x12, 0x14, 0x1b));
    private static readonly Brush Fg = new SolidColorBrush(Color.FromRgb(0xf0, 0xf2, 0xf7));
    private static readonly Brush Faint = new SolidColorBrush(Color.FromRgb(0x8b, 0x93, 0xa6));
    private static readonly Brush Bad = new SolidColorBrush(Color.FromRgb(0xff, 0x8f, 0xa3));
    private static readonly Brush Good = new SolidColorBrush(Color.FromRgb(0x7e, 0xe7, 0x87));

    public ApiKeyDialog(string provider)
    {
        _provider = provider;
        var name = ApiKeyAccountService.ProviderName(provider);

        var isGlm = GlmPreset.Is(provider);
        Title = isGlm ? "Add GLM account" : $"Add {name} API key";
        Width = 460; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = Bg; Foreground = Fg;
        FontFamily = new FontFamily("Segoe UI");

        var root = new StackPanel { Margin = new Thickness(20) };

        root.Children.Add(new TextBlock
        {
            Text = isGlm ? "Connect a GLM account" : $"Sign in to {name} with an API key",
            FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Fg,
            Margin = new Thickness(0, 0, 0, 4),
        });
        _hint.Text = HintFor(provider);
        _hint.FontSize = 12;
        _hint.Foreground = Faint;
        _hint.TextWrapping = TextWrapping.Wrap;
        _hint.Margin = new Thickness(0, 0, 0, 14);

        if (isGlm)
        {
            root.Children.Add(Caption("Service"));
            _backend.SelectedValuePath = "Tag";
            _backend.Margin = new Thickness(0, 0, 0, 12);
            _backend.Padding = new Thickness(8, 7, 8, 7);
            AutomationProperties.SetName(_backend, "GLM service");
            foreach (var backend in new[] { GlmPreset.ZaiCodingPlan, GlmPreset.ZaiApi, GlmPreset.Baseten })
                _backend.Items.Add(new ComboBoxItem { Tag = backend, Content = GlmPreset.BackendName(backend) });
            _backend.SelectionChanged += (_, _) => RefreshGlmService();
            _backend.SelectedValue = GlmPreset.ZaiCodingPlan;
            root.Children.Add(_backend);
        }
        root.Children.Add(_hint);

        if (isGlm)
        {
            var accountActions = new WrapPanel { Margin = new Thickness(0, 0, 0, 14) };
            _openAccount.Padding = new Thickness(10, 6, 10, 6);
            _openAccount.Margin = new Thickness(0, 0, 8, 0);
            _openAccount.HorizontalAlignment = HorizontalAlignment.Left;
            _openAccount.Click += (_, _) =>
            {
                try
                {
                    if (!App.TryOpenExternalUri(AccountUrl))
                        Fail("Couldn't open your browser. Open the account website and go to API Keys.");
                }
                catch { Fail("Couldn't open your browser. Open the account website and go to API Keys."); }
            };
            _copyAccountLink.Content = "Copy link";
            _copyAccountLink.Padding = new Thickness(10, 6, 10, 6);
            _copyAccountLink.Click += (_, _) =>
            {
                try
                {
                    Clipboard.SetText(AccountUrl);
                    _copyAccountLink.Content = "Copied";
                }
                catch { Fail($"Couldn't copy the link. Try again, or open {AccountUrl} manually."); }
            };
            accountActions.Children.Add(_openAccount);
            accountActions.Children.Add(_copyAccountLink);
            root.Children.Add(accountActions);
        }

        root.Children.Add(Caption("API key"));
        _key.Padding = new Thickness(8, 7, 8, 7);
        _key.Margin = new Thickness(0, 0, 0, 12);
        _key.FontFamily = new FontFamily("Cascadia Code, Consolas, monospace");
        _key.Background = TryFindResource("Bg2") as Brush ?? Bg;
        _key.Foreground = Fg;
        _key.BorderBrush = TryFindResource("Border") as Brush ?? Faint;
        _key.CaretBrush = TryFindResource("Accent") as Brush ?? Fg;
        AutomationProperties.SetName(_key, "API key");
        root.Children.Add(_key);

        root.Children.Add(Caption("Label (optional)"));
        _label.Padding = new Thickness(8, 7, 8, 7);
        _label.Margin = new Thickness(0, 0, 0, 12);
        AutomationProperties.SetName(_label, "Account label (optional)");
        root.Children.Add(_label);

        _billing.Text = "An API key is billed per token by the provider, separately from any subscription. "
            + "Selecting this account makes it the credential used for new sessions.";
        _billing.FontSize = 12;
        _billing.Foreground = Faint;
        _billing.TextWrapping = TextWrapping.Wrap;
        _billing.Margin = new Thickness(0, 0, 0, 10);
        root.Children.Add(_billing);
        if (isGlm) RefreshGlmService();

        _status.FontSize = 11.5;
        _status.TextWrapping = TextWrapping.Wrap;
        _status.Margin = new Thickness(0, 0, 0, 10);
        root.Children.Add(_status);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        _save.Content = "Verify and save";
        _save.Padding = new Thickness(14, 7, 14, 7);
        _save.IsDefault = true;
        _save.Click += OnSave;
        buttons.Children.Add(cancel);
        buttons.Children.Add(_save);
        root.Children.Add(buttons);

        Content = root;
        Loaded += (_, _) => _key.Focus();
        Closed += (_, _) => _closed.Cancel();
    }

    private static TextBlock Caption(string text) => new()
    {
        Text = text, FontSize = 11, Foreground = Faint, Margin = new Thickness(0, 0, 0, 4),
    };

    private static string HintFor(string provider) => provider switch
    {
        "claude" => "Create one at console.anthropic.com → API keys. Starts with \"sk-ant-\".",
        "codex" => "Create one at platform.openai.com → API keys. Starts with \"sk-\".",
        "grok" => "Create one in the xAI console → API keys. Starts with \"xai-\".",
        "kimi" => "Create one at platform.moonshot.ai → API keys. Kimi is used through its Anthropic-compatible endpoint.",
        _ => "Paste the provider's API key.",
    };

    private void RefreshGlmService()
    {
        _hint.Text = GlmBackend switch
        {
            GlmPreset.ZaiCodingPlan => "Sign in at z.ai, then copy your key from Individual Coding Plan → Plan Overview. "
                + "For a Team Plan, use the key from Team Coding Plan → My Plan.",
            GlmPreset.ZaiApi => "Sign in at z.ai and copy your standard API key. This connects to the pay-per-token API.",
            _ => "Create a key at app.baseten.co → API keys. Your existing Baseten keys continue to work here.",
        };
        _billing.Text = GlmBackend == GlmPreset.ZaiCodingPlan
            ? "Uses your GLM Coding Plan quota. Verification makes a one-output-token request. "
                + "New chats use this service; fallback stays within Coding Plan accounts."
            : GlmBackend == GlmPreset.ZaiApi
                ? "Billed per token to your Z.ai API account. Verification makes a one-output-token request. "
                    + "Coding Plan quota is used only when you choose Z.ai Coding Plan."
                : "Billed by Baseten. Verification checks the model list without generating tokens. "
                    + "Fallback stays within your Baseten accounts.";
        _openAccount.Content = GlmPreset.IsZai(GlmBackend) ? "Open Z.ai account" : "Open Baseten account";
        _copyAccountLink.Content = "Copy link";
        _copyAccountLink.ToolTip = AccountUrl;
        AutomationProperties.SetName(_copyAccountLink, $"Copy {GlmPreset.BackendName(GlmBackend)} account link");
        _status.Text = "";
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        var key = _key.Password?.Trim() ?? "";
        if (key.Length == 0) { Fail("Paste a key first."); return; }

        _save.IsEnabled = false;
        _backend.IsEnabled = false;
        _key.IsEnabled = false;
        _status.Foreground = Faint;
        _status.Text = "Checking the key with the provider…";

        ApiKeyValidation result;
        try
        {
            result = await ApiKeyAccountService.ValidateAsync(_provider, key, _closed.Token, GlmBackend);
        }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested) { return; }
        finally
        {
            _save.IsEnabled = true;
            _backend.IsEnabled = true;
            _key.IsEnabled = true;
        }
        if (_closed.IsCancellationRequested) return;

        if (!result.Ok) { Fail(result.Message); return; }

        _status.Foreground = Good;
        _status.Text = result.Message;
        ApiKey = key;
        AccountLabel = _label.Text?.Trim() ?? "";
        DialogResult = true;
    }

    private void Fail(string message)
    {
        _status.Foreground = Bad;
        _status.Text = message;
    }
}
