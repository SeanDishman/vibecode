using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using VibeCode.Protocol;

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
        foreach (var variable in new[] { "VIBECODE_DATA_DIR", "VIBECODE_CODEX_ACCOUNT_STORE", "VIBECODE_CODEX_LEGACY_HOME",
            "VIBECODE_CLAUDE_ACCOUNT_STORE", "CLAUDE_CONFIG_DIR", "VIBECODE_GROK_ACCOUNT_STORE", "VIBECODE_GROK_LEGACY_AUTH_PATH",
            "KIMI_CODE_HOME", "GROK_HOME" })
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
        Environment.SetEnvironmentVariable("VIBECODE_GROK_PATH", Environment.ProcessPath);
        Environment.SetEnvironmentVariable("VIBECODE_CODEX_PATH", Environment.ProcessPath);
        Environment.CurrentDirectory = testDirectory;
        _ = new Application();
        try
        {
            CatalogsFollowRuntime();
            ApkPackageTests.Run(mobileTemplate);
            TokenRateTests.Run();
            TokenRateLayoutTests.Run();
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
        var state = JsonNode.Parse("""
            {"currentModelId":"grok-4.6","availableModels":[
              {"modelId":"grok-4.6","name":"Grok 4.6","_meta":{"reasoningEfforts":["low","high","xhigh"]}},
              {"modelId":"grok-4.5","name":"Grok 4.5"}]}
            """)!.AsObject();
        var grok = (JsonArray)typeof(KimiSession).GetMethod("GrokModelsFromState", Hidden)!.Invoke(null, new object?[] { state, "grok-4.6" })!;
        Require(grok.Count == 2, "Grok catalog keeps exactly the two provider rows");
        Require(grok[0]!["supportedEffortLevels"]!.AsArray().Count == 3, "Grok preserves runtime reasoning levels");
        Require(grok[0]!["isDefault"]!.GetValue<bool>(), "Grok keeps its current model selected");
        using var codex = new CodexSession(new CodexSessionOptions { Cwd = Environment.CurrentDirectory });
        var rows = JsonNode.Parse("""
            [{"id":"gpt-5.6-sol","model":"gpt-5.6-sol","displayName":"GPT Sol","isDefault":true,
              "supportedReasoningEfforts":[{"reasoningEffort":"high"}],"serviceTiers":[{"id":"priority"}]},
             {"id":"internal-model","hidden":true}]
            """)!.AsArray();
        typeof(CodexSession).GetMethod("BuildModels", Hidden)!.Invoke(codex, new object[] { rows });
        Require(codex.Models.Count == 5 && codex.Models[0]!["value"]!.GetValue<string>() == "gpt-5.6-sol",
            "Codex keeps known normal models when the runtime returns a partial catalog");
        Require(codex.Models[0]!["supportsFastMode"]!.GetValue<bool>(), "Codex keeps runtime speed-tier support");
        Require(codex.Models[0]!["displayName"]!.GetValue<string>() == "GPT Sol 5.6", "Codex keeps the Sol product label");
        Require(codex.Models[0]!["supportedEffortLevels"]!.AsArray().Count == 1, "Live reasoning options override fallback options");
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
        Require(codex.Models.Count == 5, "Retired and internal models do not add menu entries");
        typeof(CodexSession).GetMethod("BuildModels", Hidden)!.Invoke(codex, new object?[] { null });
        Require(codex.Models.Any(row => row!["value"]!.GetValue<string>() == "default"), "Empty catalogs retain the provider default");
        var luna = codex.Models.OfType<JsonObject>().Single(row => row["value"]!.GetValue<string>() == "gpt-5.6-luna");
        Require(!luna["supportedEffortLevels"]!.AsArray().Any(level => level!.GetValue<string>() == "ultra"), "Fallback reasoning remains model-specific");
        var spark = codex.Models.OfType<JsonObject>().Single(row => row["value"]!.GetValue<string>() == "gpt-5.3-codex-spark");
        Require(!spark["supportsFastMode"]!.GetValue<bool>(), "Spark fallback does not advertise a separate priority tier");
        Console.WriteLine("PASS: live and fallback provider catalogs, reasoning levels and speed tiers");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
