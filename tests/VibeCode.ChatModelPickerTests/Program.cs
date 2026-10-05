using System.IO;
using System.Windows;
using System.Windows.Threading;
using VibeCode.Services;
using VibeCode.UI;

internal static class Program
{
    private static readonly List<ChatViewModel> Chats = [];
    private static string Root = "";
    private static int Checks;

    [STAThread]
    private static int Main()
    {
        Root = Path.Combine(Environment.CurrentDirectory, "artifacts", "chat-model-picker",
            "checks-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff"));
        Directory.CreateDirectory(Root);
        Environment.SetEnvironmentVariable("VIBECODE_DATA_DIR", Path.Combine(Root, "data"));
        Environment.SetEnvironmentVariable("VIBECODE_CLAUDE_ACCOUNT_STORE", Path.Combine(Root, "claude-accounts"));
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", Path.Combine(Root, "claude-home"));
        Environment.SetEnvironmentVariable("VIBECODE_CODEX_ACCOUNT_STORE", Path.Combine(Root, "codex-accounts"));
        Environment.SetEnvironmentVariable("VIBECODE_CODEX_LEGACY_HOME", Path.Combine(Root, "codex-home"));
        Environment.SetEnvironmentVariable("VIBECODE_GROK_ACCOUNT_STORE", Path.Combine(Root, "grok-accounts"));
        Environment.SetEnvironmentVariable("VIBECODE_GROK_LEGACY_AUTH_PATH", Path.Combine(Root, "grok-home", "auth.json"));
        Environment.SetEnvironmentVariable("KIMI_CODE_HOME", Path.Combine(Root, "kimi-home"));
        Environment.SetEnvironmentVariable("KIMI_SHARE_DIR", Path.Combine(Root, "kimi-share"));
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        AppSettings.Current.AgentMemoryEnabled = AppSettings.Current.AgentSwarmsEnabled = false;
        AppSettings.Current.NotifyOnTurnEnd = AppSettings.Current.NotifyOnAwaitingInput = false;

        try
        {
            VerifyChatIsolation();
            VerifyLiveCatalogAndSelections();
            Console.WriteLine($"PASS: {Checks} chat model picker checks. No provider sessions started. Evidence: {Root}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            foreach (var chat in Chats) chat.Close();
            app.Shutdown();
        }
    }

    private static ChatViewModel Chat(string provider, bool bridge = false, string? resume = null)
    {
        var chat = new ChatViewModel(Root, resume: resume, accountId: provider + "-original", provider: provider)
        {
            ExcludeFromMemory = true,
        };
        Chats.Add(chat);
        if (bridge) chat.BridgeLabel = provider + " 1";
        return chat;
    }

    private static void CheckCatalog(ChatViewModel chat, string scenario)
    {
        Check(scenario + ": only the chat's own models are selectable",
            chat.PickerModels.Count > 0 && chat.PickerModels.All(m => m.Model.Provider == chat.Provider && m.CanApply));
    }

    private static void VerifyChatIsolation()
    {
        var vm = new MainViewModel();
        foreach (var provider in new[] { "codex", "claude", "kimi", "grok", "glm" })
        {
            AppSettings.Current.DefaultProvider = provider == "claude" ? "codex" : "claude";
            foreach (var bridge in new[] { false, true })
            {
                var chat = Chat(provider, bridge);
                vm.Chats.Add(chat);
                CheckCatalog(chat, provider + (bridge ? " bridge" : " chat") + " opened with another New Chat provider");
            }
        }
        vm.ActiveChat = vm.Chats.First(c => c.IsCodex && !c.IsBridgeAgent);
        vm.SecondaryActiveChat = vm.Chats.First(c => c.IsClaude && !c.IsBridgeAgent);
        foreach (var selectedProvider in new[] { "claude", "codex", "grok", "kimi", "glm" })
        {
            AppSettings.Current.DefaultProvider = selectedProvider;
            AppSettings.Current.ActiveAccountId = "claude-other";
            vm.RefreshProviderPresentation();
            Check("switching New Chat to " + selectedProvider + " leaves every chat's provider and models intact",
                vm.Chats.All(c => c.PickerModels.Count > 0 && c.PickerModels.All(m => m.Model.Provider == c.Provider && m.CanApply)));
            Check("switching New Chat to " + selectedProvider + " keeps original chat accounts and both window selections",
                vm.Chats.All(c => c.AccountId == (c.IsKimi ? null : c.Provider + "-original"))
                && vm.ActiveChat!.IsCodex && vm.SecondaryActiveChat!.IsClaude);
        }
        AppSettings.Current.DefaultProvider = "claude";
        var restored = Chat("codex", resume: "12345678-codex-restored");
        CheckCatalog(restored, "restored Codex chat while New Chat uses Claude");
        restored.SyncCodexAccountWithActive();
        Check("restored Codex chat keeps its captured account and session",
            restored.AccountId == "codex-original" && restored.SessionId == "12345678-codex-restored");
    }

    private static void VerifyLiveCatalogAndSelections()
    {
        var vm = new MainViewModel();
        AppSettings.Current.DefaultProvider = "claude";
        var codex = Chat("codex");
        vm.Chats.Add(codex);
        // An account-specific choice is absent from the shared fallback. A live chat must keep its own catalog.
        codex.Models.Add(new ModelChoice { Provider = "codex", Value = "codex-account-model", Display = "Account model" });
        vm.RefreshProviderPresentation();
        Check("Codex account's live catalog survives a global Claude selection",
            codex.PickerModels.Count == 1 && ReferenceEquals(codex.PickerModels[0].Model, codex.Models[0]));
        AppSettings.Current.DefaultModel = "claude-kept";
        codex.SetPickerModel(codex.PickerModels.Single());
        Check("selecting a Codex model changes that chat and only the Codex default",
            codex.Model == "codex-account-model" && AppSettings.Current.DefaultCodexModel == codex.Model
            && AppSettings.Current.DefaultModel == "claude-kept" && AppSettings.Current.DefaultProvider == "claude"
            && codex.AccountId == "codex-original");
        codex.SetPickerModel(new ModelPickerChoice
        {
            Model = new ModelChoice { Provider = "claude", Value = "claude-opus-5-5", Display = "Opus" }, CanApply = true,
        });
        Check("a stale foreign model row cannot change a Codex chat", codex.Model == "codex-account-model");

        AppSettings.Current.DefaultProvider = "codex";
        var claude = Chat("claude");
        vm.Chats.Add(claude);
        var claudeModel = new ModelChoice { Provider = "claude", Value = "claude-account-model", Display = "Account model" };
        claude.Models.Add(claudeModel);
        vm.RefreshProviderPresentation();
        Check("Claude account's live model remains available with a global Codex selection",
            claude.PickerModels.Any(m => ReferenceEquals(m.Model, claudeModel)) && claude.PickerModels.All(m => m.Model.Provider == "claude" && m.CanApply));
        claude.SetPickerModel(claude.PickerModels.Single(m => ReferenceEquals(m.Model, claudeModel)));
        Check("selecting a Claude model changes that chat and only the Claude default",
            claude.Model == claudeModel.Value && AppSettings.Current.DefaultModel == claude.Model
            && AppSettings.Current.DefaultCodexModel == codex.Model && AppSettings.Current.DefaultProvider == "codex"
            && claude.AccountId == "claude-original");
    }

    private static void Check(string name, bool value)
    {
        if (!value) throw new InvalidOperationException("FAIL: " + name);
        Checks++;
        Console.WriteLine("PASS: " + name);
    }
}
