using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using VibeCode.Protocol;
using VibeCode.Services;
using VibeCode.UI;

internal static class Program
{
    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    [STAThread]
    private static int Main()
    {
        var source = Path.Combine(Environment.CurrentDirectory, "VibeCode.Desktop");
        if (!File.Exists(Path.Combine(source, "MainWindow.xaml")))
        {
            Console.Error.WriteLine("Run these tests from the repository root.");
            return 1;
        }
        var testDirectory = Path.Combine(Path.GetTempPath(), "vibecode-public-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDirectory);
        foreach (var variable in new[] { "VIBECODE_DATA_DIR", "VIBECODE_CODEX_ACCOUNT_STORE", "VIBECODE_CODEX_LEGACY_HOME", "VIBECODE_CLAUDE_ACCOUNT_STORE", "CLAUDE_CONFIG_DIR", "GEMINI_CLI_HOME" })
        {
            var path = Path.Combine(testDirectory, variable.ToLowerInvariant());
            Directory.CreateDirectory(path);
            Environment.SetEnvironmentVariable(variable, path);
        }
        Environment.SetEnvironmentVariable("VIBECODE_TEST_SOURCE", source);
        var mobileTemplate = Environment.GetEnvironmentVariable("VIBECODE_TEST_MOBILE_TEMPLATE")
            ?? Path.Combine(source, "Assets", "vibecode-mobile.apk");
        Environment.SetEnvironmentVariable("VIBECODE_DISABLE_BROWSER_BRIDGE", "1");
        Environment.SetEnvironmentVariable("VIBECODE_HIDDEN", "1");
        // These tests translate recorded protocol shapes; they never start a provider process.
        Environment.SetEnvironmentVariable("VIBECODE_CODEX_PATH", Environment.ProcessPath);
        Environment.CurrentDirectory = testDirectory;
        _ = new Application();
        try
        {
            CatalogsFollowRuntime();
            RetiredProviderSettings();
            ProtocolBoundaryTests.Run();
            ApkPackageTests.Run(mobileTemplate);
            TokenRateTests.Run();
            Console.WriteLine("PASS: public distribution regression suite");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error is TargetInvocationException { InnerException: not null } wrapped
                ? wrapped.InnerException : error);
            return 1;
        }
    }

    private static void CatalogsFollowRuntime()
    {
        using var codex = new CodexSession(new CodexSessionOptions { Cwd = Environment.CurrentDirectory });
        var rows = JsonNode.Parse("""
            [{"id":"gpt-5.6-sol","model":"gpt-5.6-sol","displayName":"GPT Sol","isDefault":true,
              "supportedReasoningEfforts":[{"reasoningEffort":"high"}],"serviceTiers":[{"id":"priority"}]},
             {"id":"internal-model","hidden":true}]
            """)!.AsArray();
        typeof(CodexSession).GetMethod("BuildModels", Hidden)!.Invoke(codex, new object[] { rows });
        var sol = codex.Models.OfType<JsonObject>().Single(row => row["value"]!.GetValue<string>() == "gpt-5.6-sol");
        Require(codex.Models.Count == CodexModelCatalog.Current.Length && codex.Models.Any(row => row!["value"]!.GetValue<string>() == "gpt-6.1-sol"),
            "Codex keeps known normal models when the runtime returns a partial catalog");
        Require(sol["supportsFastMode"]!.GetValue<bool>(), "Codex keeps runtime speed-tier support");
        Require(sol["displayName"]!.GetValue<string>() == "GPT Sol 5.6", "Codex keeps the Sol product label");
        Require(sol["supportedEffortLevels"]!.AsArray().Count == 1, "Live reasoning options override fallback options");
        Require(codex.Models.All(row => row!["value"]!.GetValue<string>() != "internal-model"), "Hidden internal rows stay out of the menu");
        var partial = JsonNode.Parse("""
            [{"id":"gpt-6-astra","hidden":true,"isDefault":true,
              "supportedReasoningEfforts":[{"reasoningEffort":"high"},{"reasoningEffort":"ultra"}],"serviceTiers":[]},
             {"id":"gpt-5.5","hidden":false},{"id":"internal-model","hidden":true}]
            """)!.AsArray();
        typeof(CodexSession).GetMethod("BuildModels", Hidden)!.Invoke(codex, new object[] { partial });
        var astra = codex.Models.OfType<JsonObject>().Single(row => row["value"]!.GetValue<string>() == "gpt-6-astra");
        Require(astra["supportedEffortLevels"]!.AsArray().Count == 2, "Known hidden normal models keep the runtime's options");
        Require(!astra["supportsFastMode"]!.GetValue<bool>(), "An explicit empty speed tier list stays disabled");
        Require(codex.Models.Count == CodexModelCatalog.Current.Length, "Retired and internal models do not add menu entries");
        typeof(CodexSession).GetMethod("BuildModels", Hidden)!.Invoke(codex, new object?[] { null });
        Require(codex.Models.Any(row => row!["value"]!.GetValue<string>() == "default"), "Empty catalogs retain the provider default");
        var luna = codex.Models.OfType<JsonObject>().Single(row => row["value"]!.GetValue<string>() == "gpt-5.6-luna");
        Require(!luna["supportedEffortLevels"]!.AsArray().Any(level => level!.GetValue<string>() == "ultra"), "Fallback reasoning remains model-specific");
        Require(codex.Models.All(row => row!["value"]!.GetValue<string>() != "gpt-5.3-codex-spark"), "Retired models stay out of the fallback catalog");
        Console.WriteLine("PASS: live and fallback provider catalogs, reasoning levels and speed tiers");
    }

    private static void RetiredProviderSettings()
    {
        string[] providers = ["claude", "codex", "gemini", "glm"];
        Require(BridgeSetupPanel.Providers.Select(provider => provider.Id).SequenceEqual(providers),
            "Bridge setup lists exactly the supported providers");
        Require(ApiKeyAccountService.Providers.SequenceEqual(["claude", "codex", "glm"]),
            "API accounts list only supported key-based providers");
        var settings = new AppSettings
        {
            DefaultProvider = "retired-provider",
            JarvisProvider = "retired-provider", JarvisModel = "retired-model", JarvisEffort = "retired-effort",
            OpenChats = providers.Select(provider => new OpenChatState { Provider = provider, SessionId = provider }).ToList(),
            SavedBridges = [new SavedBridgeState
            {
                Provider = "codex", HostSessionId = "supported-host",
                HostConfiguration = new("codex", "gpt-6-luna", "medium"),
                HostWorkerConfiguration = new("retired-provider", "retired-model", null),
                Peers = [new SavedBridgePane
                         {
                             Provider = "gemini", SessionId = "supported-peer",
                             Configuration = new("gemini", "default", null),
                             WorkerConfiguration = new("retired-provider", "retired-model", null),
                         },
                         new SavedBridgePane { Provider = "retired-provider", SessionId = "retired-peer" }],
            }, new SavedBridgeState { Provider = "retired-provider", HostSessionId = "retired-host" }],
        };
        settings.OpenChats.Add(new OpenChatState { Provider = "retired-provider", SessionId = "retired-chat" });
        Require(settings.NormalizeProviderModelSlots(), "Removed providers trigger settings migration");
        Require(settings.DefaultProvider == "claude" && settings.OpenChats.Select(chat => chat.SessionId).SequenceEqual(providers),
            "Migration resets a retired default and preserves supported conversations");
        Require(settings.SavedBridges.Single().HostSessionId == "supported-host"
                && settings.SavedBridges.Single().Peers.Single().SessionId == "supported-peer",
            "Migration preserves supported bridges and removes retired hosts and peers");
        Require(settings.JarvisProvider == "codex" && settings.JarvisModel == new AppSettings().JarvisModel,
            "A retired Jarvis selection returns to the supported assistant defaults");
        Require(settings.SavedBridges.Single().HostConfiguration?.Provider == "codex"
                && settings.SavedBridges.Single().HostWorkerConfiguration is null
                && settings.SavedBridges.Single().Peers.Single().Configuration?.Provider == "gemini"
                && settings.SavedBridges.Single().Peers.Single().WorkerConfiguration is null,
            "Saved roles preserve supported configurations and release retired launch defaults");
        Require(!settings.NormalizeProviderModelSlots(), "Provider migration is idempotent");
        Console.WriteLine("PASS: supported providers and retired-provider settings migration");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
