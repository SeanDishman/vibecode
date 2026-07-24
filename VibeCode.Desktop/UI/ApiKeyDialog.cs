using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VibeCode.Services;

namespace VibeCode.UI;

/// <summary>
/// Paste-an-API-key dialog. Built in code rather than XAML so adding it touches no shared markup.
/// The key is checked against the provider before the dialog will close: every one of these vendors
/// exposes a free GET /v1/models, so a bad key is caught here instead of surfacing later as an opaque
/// CLI failure on the user's next message.
/// </summary>
public sealed class ApiKeyDialog : Window
{
    private readonly string _provider;
    private readonly PasswordBox _key = new();
    private readonly TextBox _label = new();
    private readonly TextBlock _status = new();
    private readonly Button _save = new();

    public string ApiKey { get; private set; } = "";
    public string AccountLabel { get; private set; } = "";

    private static readonly Brush Bg = new SolidColorBrush(Color.FromRgb(0x12, 0x14, 0x1b));
    private static readonly Brush Fg = new SolidColorBrush(Color.FromRgb(0xf0, 0xf2, 0xf7));
    private static readonly Brush Faint = new SolidColorBrush(Color.FromRgb(0x8b, 0x93, 0xa6));
    private static readonly Brush Bad = new SolidColorBrush(Color.FromRgb(0xff, 0x8f, 0xa3));
    private static readonly Brush Good = new SolidColorBrush(Color.FromRgb(0x7e, 0xe7, 0x87));

    public ApiKeyDialog(string provider)
    {
        _provider = provider;
        var name = ApiKeyAccountService.ProviderName(provider);

        Title = $"Add {name} API key";
        Width = 460; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = Bg; Foreground = Fg;
        FontFamily = new FontFamily("Segoe UI");

        var root = new StackPanel { Margin = new Thickness(20) };

        root.Children.Add(new TextBlock
        {
            Text = $"Sign in to {name} with an API key",
            FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Fg,
            Margin = new Thickness(0, 0, 0, 4),
        });
        root.Children.Add(new TextBlock
        {
            Text = HintFor(provider),
            FontSize = 11.5, Foreground = Faint, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        root.Children.Add(Caption("API key"));
        _key.Padding = new Thickness(8, 7, 8, 7);
        _key.Margin = new Thickness(0, 0, 0, 12);
        _key.FontFamily = new FontFamily("Cascadia Code, Consolas, monospace");
        root.Children.Add(_key);

        root.Children.Add(Caption("Label (optional)"));
        _label.Padding = new Thickness(8, 7, 8, 7);
        _label.Margin = new Thickness(0, 0, 0, 12);
        root.Children.Add(_label);

        // Spending warning: a key bills per token, unlike the subscription logins this app normally uses.
        root.Children.Add(new TextBlock
        {
            Text = "An API key is billed per token by the provider, separately from any subscription. "
                 + "Selecting this account makes it the credential used for new sessions.",
            FontSize = 11, Foreground = Faint, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        });

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

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        var key = _key.Password?.Trim() ?? "";
        if (key.Length == 0) { Fail("Paste a key first."); return; }

        _save.IsEnabled = false;
        _status.Foreground = Faint;
        _status.Text = "Checking the key with the provider…";

        var result = await ApiKeyAccountService.ValidateAsync(_provider, key);
        _save.IsEnabled = true;

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
