using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using VibeCode;
using VibeCode.Protocol;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    private static void VerifyGeminiUi()
    {
        Environment.SetEnvironmentVariable("GEMINI_CLI_HOME", Path.Combine(_root, "gemini-ui-home"));
        Directory.CreateDirectory(GeminiRuntime.ConfigDirectory);
        var vm = new MainViewModel();
        var chat = new ChatViewModel(_root, provider: "gemini"); Chats.Add(chat);
        chat.Model = "gemini-fixture-pro";
        chat.Models.Add(new ModelChoice { Provider = "gemini", Value = chat.Model!, Display = "Gemini Pro", SupportsEffort = false });
        chat.RefreshModelPicker();
        vm.Chats.Add(chat); vm.ActiveChat = chat;
        Check("Gemini stays Gemini when creating a chat", chat.Provider == "gemini" && chat.AgentDisplay == "Gemini" && chat.SignInButtonText == (chat.NeedsCliInstall ? "Install Gemini CLI" : "Sign in with Google"));
        Check("Gemini does not advertise unsupported fork, effort, or speed", !chat.CanFork && !chat.HasEffort && !chat.ShowUltrafastMode);
        var result = new JsonObject
        {
            ["usage"] = new JsonObject { ["input_tokens"] = 900, ["cache_read_input_tokens"] = 600, ["output_tokens"] = 140 },
            ["total_cost_usd"] = 0,
            ["modelUsage"] = new JsonObject
            {
                ["gemini-fixture-pro"] = new JsonObject { ["inputTokens"] = 600, ["cacheReadInputTokens"] = 400, ["outputTokens"] = 100 },
                ["gemini-fixture-flash"] = new JsonObject { ["inputTokens"] = 300, ["cacheReadInputTokens"] = 200, ["outputTokens"] = 40 },
            },
        };
        Call(chat, "ApplyResult", result);
        Check("Gemini UI uses complete token totals", chat.TotalIn == 1500 && chat.TotalOut == 140 && chat.TotalTokens == 1640);
        Check("Gemini UI labels subscription usage", chat.CostText == "Included in plan");
        result["usage_is_incomplete"] = true;
        Call(chat, "ApplyResult", result);
        Check("Partial token counts are visibly qualified", chat.TokensText.StartsWith("≥") && chat.TokensToolTip.Contains("thinking"));
        Check("Gemini is offered to every bridge role", BridgeSetupPanel.Providers.Any(p => p.Id == "gemini"));
        var resources = Application.Current.Resources.MergedDictionaries;
        var original = resources.ToArray();
        try
        {
            foreach (var theme in new[] { "Dark", "Borderless", "Cli" })
            {
                resources.Clear();
                resources.Add((ResourceDictionary)Application.LoadComponent(new Uri($"/VibeCode;component/Themes/{(theme == "Cli" ? "Cli" : "Dark")}.xaml", UriKind.Relative)));
                if (theme == "Borderless") resources.Add((ResourceDictionary)Application.LoadComponent(new Uri("/VibeCode;component/Themes/Borderless.xaml", UriKind.Relative)));
                var shell = (MainWindow)typeof(MainWindow).GetConstructors(Flags).Single(c => c.GetParameters().Length == 3).Invoke([vm, null, true]);
                shell.Left = shell.Top = -30000; shell.ShowActivated = shell.ShowInTaskbar = false;
                try
                {
                    shell.Show(); shell.UpdateLayout();
                    var mcp = new McpServerDialog([]);
                    Check(theme + " MCP configuration exposes a Gemini destination", mcp.FindName("GeminiCheck") is CheckBox { IsChecked: true });
                    mcp.Close();
                    Call(shell, "SetNewChatProvider", "gemini", false);
                    Check(theme + " new chat picker preserves Gemini", ((ToggleButton)shell.FindName("GeminiProviderToggle")).IsChecked == true);
                    Call(shell, "SelectAccountManagerProvider", "gemini");
                    Call(GeminiUsageService.Instance, "Publish", "Google AI Pro",
                        new GeminiQuotaWindow[] { new("gemini-3.8-flash", 25, 1125, DateTimeOffset.UtcNow.AddHours(8)) }, "");
                    System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    Check(theme + " account roster refreshes with Google quota", vm.AllAccounts.OfType<GeminiAccountEntry>().Single().UsageDisplay == "25% used");
                    var panel = (FrameworkElement)shell.FindName("GeminiAccountsPanel");
                    Check(theme + " Gemini account panel can be selected", panel.Visibility == Visibility.Visible);
                    CaptureUltrafastElement(panel, 500, "gemini-account-" + theme.ToLowerInvariant() + ".png");
                    Check(theme + " duplicate titlebar settings button is removed", shell.FindName("SettingsButton") is null);
                    var template = shell.Resources[new DataTemplateKey(typeof(GeminiAccountEntry))] as DataTemplate;
                    Check(theme + " unified account list has a Gemini template", template is not null);
                    var row = (FrameworkElement)template!.LoadContent();
                    row.DataContext = new GeminiAccountEntry { Label = "Google account", ProviderLine = "Gemini · Google AI Pro", UsageDisplay = "25% used", Usable = true, IsVibeCodeSelected = true };
                    CaptureUltrafastElement(row, 560, "gemini-account-row-" + theme.ToLowerInvariant() + ".png");
                }
                finally { shell.Close(); }
            }
        }
        finally { resources.Clear(); foreach (var resource in original) resources.Add(resource); }
        var previousCli = Environment.GetEnvironmentVariable("VIBECODE_GEMINI_PATH");
        try
        {
            // Model a present CLI explicitly: CI runners do not have Gemini installed. This fixture never launches it.
            Environment.SetEnvironmentVariable("VIBECODE_GEMINI_PATH", Environment.ProcessPath);
            File.WriteAllText(Path.Combine(GeminiRuntime.ConfigDirectory, "oauth_creds.json"), "{\"expiry_date\":1}");
            Check("Google CLI login is usable by Bridge", BridgeAccountChoice.For("gemini").Any(a => a.IsUsable));
        }
        finally { Environment.SetEnvironmentVariable("VIBECODE_GEMINI_PATH", previousCli); }

    }
}
