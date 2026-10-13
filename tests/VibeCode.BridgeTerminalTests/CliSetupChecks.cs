using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using VibeCode;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    private static void VerifyCliSetup()
    {
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var originalRuntime = Environment.GetEnvironmentVariable("VIBECODE_RUNTIME_DIR");
        var resources = Application.Current.Resources.MergedDictionaries;
        var originalResources = resources.ToArray();
        try
        {
            // Even if a global Gemini script exists, Node is absent in this fresh-machine scenario.
            Environment.SetEnvironmentVariable("PATH", Environment.GetFolderPath(Environment.SpecialFolder.System));
            Environment.SetEnvironmentVariable("VIBECODE_RUNTIME_DIR", Path.Combine(_root, "missing-cli-tools"));
            Check("a missing prerequisite offers Gemini setup", !CliInstallationService.IsReady("gemini"));
            var chat = new ChatViewModel(_root, provider: "gemini"); Chats.Add(chat);
            chat.AuthNeeded = true;
            Check("chat's recovery button offers installation directly", chat.SignInButtonText == "Install Gemini CLI"
                && chat.AuthMessage.Contains("official CLI"));
            foreach (var theme in new[] { "Dark", "Borderless", "Cli" })
            {
                resources.Clear();
                resources.Add((ResourceDictionary)Application.LoadComponent(new Uri($"/VibeCode;component/Themes/{(theme == "Cli" ? "Cli" : "Dark")}.xaml", UriKind.Relative)));
                if (theme == "Borderless") resources.Add((ResourceDictionary)Application.LoadComponent(new Uri("/VibeCode;component/Themes/Borderless.xaml", UriKind.Relative)));
                var vm = new MainViewModel();
                var shell = (MainWindow)typeof(MainWindow).GetConstructors(Flags).Single(c => c.GetParameters().Length == 3).Invoke([vm, null, true]);
                shell.Left = shell.Top = -30000; shell.ShowActivated = shell.ShowInTaskbar = false;
                try
                {
                    shell.Show(); shell.UpdateLayout();
                    foreach (var provider in new[] { "claude", "codex", "gemini", "gemini" })
                    {
                        Call(shell, "SelectAccountManagerProvider", provider);
                        var label = ((TextBlock)shell.FindName("AccountManagerAddLabel")).Text;
                        var button = (Button)shell.FindName("AccountManagerAddButton");
                        var missing = !CliInstallationService.IsReady(provider);
                        Check(theme + " " + provider + " presents the correct setup action", button.IsEnabled
                            && label.StartsWith("Install ") == missing && AutomationProperties.GetName(button) == label);
                    }
                    var footer = (FrameworkElement)((Button)shell.FindName("AccountManagerAddButton")).Parent;
                    CaptureUltrafastElement(footer, 580, "cli-setup-" + theme.ToLowerInvariant() + ".png");
                    Call(shell, "SelectAccountManagerProvider", "glm");
                    Check(theme + " API-only provider retains account setup", ((TextBlock)shell.FindName("AccountManagerAddLabel")).Text == "Add GLM account");
                }
                finally { shell.Close(); }
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Environment.SetEnvironmentVariable("VIBECODE_RUNTIME_DIR", originalRuntime);
            resources.Clear(); foreach (var resource in originalResources) resources.Add(resource);
        }
    }
}
