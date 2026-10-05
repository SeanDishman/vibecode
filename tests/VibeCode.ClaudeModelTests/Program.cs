using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Threading;
using VibeCode.Protocol;
using VibeCode.Services;
using VibeCode.UI;

internal static class Program
{
    private const BindingFlags Flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Assembly Desktop = typeof(ChatViewModel).Assembly;
    private static readonly string[] Current = ["claude-opus-5-5", "claude-fable-5-1", "claude-sonnet-5-5", "claude-haiku-4-5"];
    private static readonly string[] Names = ["Opus 5.5", "Fable 5.1", "Sonnet 5.5", "Haiku 4.5"];
    private static readonly string[] Efforts = ["low", "medium", "high", "xhigh", "max"];
    private static string Root = "";
    private static int Checks;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--output-format")) return Fixture();
        if (args.Contains("--version")) { Console.WriteLine("Claude catalog fixture"); return 0; }
        Root = Path.Combine(Environment.CurrentDirectory, "artifacts", "claude-model-refresh",
            "checks-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff"));
        Directory.CreateDirectory(Root);
        var actualCli = ClaudeSession.ResolveCliPath();
        var actualVersion = FileVersionInfo.GetVersionInfo(actualCli);
        Check("VibeCode resolves a Claude runtime compatible with new models", Version.TryParse(actualVersion.ProductVersion, out var runtimeVersion) && runtimeVersion >= new Version(2, 1, 284));
        File.WriteAllText(Path.Combine(Root, "resolved-runtime.txt"), actualCli + "\n" + actualVersion.ProductVersion);
        Environment.SetEnvironmentVariable("VIBECODE_DATA_DIR", Path.Combine(Root, "data"));
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", Path.Combine(Root, "empty-claude-home"));
        Environment.SetEnvironmentVariable("VIBECODE_CLAUDE_ACCOUNT_STORE", Path.Combine(Root, "empty-claude-accounts"));
        Environment.SetEnvironmentVariable("KIMI_CODE_HOME", Path.Combine(Root, "empty-kimi-home"));
        Environment.SetEnvironmentVariable("KIMI_SHARE_DIR", Path.Combine(Root, "empty-kimi-share"));
        Environment.SetEnvironmentVariable("VIBECODE_CODEX_LEGACY_HOME", Path.Combine(Root, "empty-codex-home"));
        Environment.SetEnvironmentVariable("CLAUDE_MODEL_FIXTURE", Path.Combine(Root, "wire.jsonl"));
        Environment.SetEnvironmentVariable("VIBECODE_CLAUDE_PATH", Environment.ProcessPath);
        Directory.CreateDirectory(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")!);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        AppSettings.Current.AgentMemoryEnabled = AppSettings.Current.AgentSwarmsEnabled = false;
        AppSettings.Current.NotifyOnTurnEnd = AppSettings.Current.NotifyOnAwaitingInput = false;

        AppSettings.Current.McpServers.Clear();
        try
        {
            CatalogChecks();
            PickerAndTransportChecks();
            if (args.Contains("--local-catalog"))
                foreach (var id in Current.Take(3)) InstalledCatalogCheck(actualCli, id);
            Console.WriteLine($"PASS: {Checks} Claude catalog checks. No model generations. Evidence: {Root}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { app.Shutdown(); }
    }

    private static object? Call(string type, string method, params object?[] args) =>
        Desktop.GetType(type, true)!.GetMethod(method, Flags)!.Invoke(null, args);

    private static List<ModelChoice> Preview() =>
        ((IEnumerable<ModelChoice>)Call("VibeCode.UI.ProviderModelCatalog", "For", "claude")!).ToList();

    private static void Check(string name, bool value)
    {
        if (!value) throw new InvalidOperationException("FAIL: " + name);
        Checks++;
        Console.WriteLine("PASS: " + name);
    }

    private static void CatalogChecks()
    {
        var preview = Preview();
        for (var i = 0; i < Current.Length; i++)
        {
            var row = preview.Single(m => m.Value == Current[i]);
            Check("offline choice uses explicit " + Names[i], row.Display == Names[i] && row.ShortName == Names[i] && row.ResolvedModel == Current[i]);
            Check(Names[i] + " effort matches its model", i == 3 ? !row.SupportsEffort && row.EffortLevels.Count == 0 : row.SupportsEffort && row.EffortLevels.SequenceEqual(Efforts));
        }
        Check("current coding choices precede legacy choices", preview.Take(3).Select(m => m.Value).SequenceEqual(Current.Take(3)));
        Check("default follows model choices", preview.Last().Value == "default");
        Check("invite-only Mythos is absent from the offline menu", !preview.Any(m => m.Value.Contains("mythos")));
        Check("fast mode switches to current Opus", ((ModelChoice?)Call("VibeCode.UI.ProviderModelCatalog", "FastModeSwitchTarget", preview))?.Value == Current[0]);
        Check("Fable and Sonnet do not advertise Opus fast mode", preview.Where(m => Current.Skip(1).Contains(m.Value)).All(m => !m.SupportsFastMode));
        Check("Opus 4.6 does not gain xhigh", !preview.Single(m => m.Value == "claude-opus-4-6").EffortLevels.Contains("xhigh"));

        foreach (var (alias, id) in new[] { ("opus", Current[0]), ("fable", Current[1]), ("sonnet", Current[2]), ("haiku", Current[3]) })
        {
            var normalized = (BridgeAgentConfiguration)Call("VibeCode.UI.BridgeAgentConfigurationPolicy", "Normalize", new BridgeAgentConfiguration("claude", alias, "max"))!;
            Check("offline bridge understands saved " + alias + " alias", normalized.Model == id && (alias != "haiku" || normalized.Effort is null));
        }

        var live = new JsonArray(Row("default", "Default", "claude-opus-4-8"), Row("opus", "Opus", "claude-opus-4-8[1m]"),
            Row("sonnet", "Sonnet", "claude-sonnet-5"), Row("haiku", "Haiku", "claude-haiku-4-5-20251001"),
            Row("custom", "Enterprise model", "enterprise-model"), Row("mythos", "Mythos", "claude-mythos-5-1"));
        Call("VibeCode.Protocol.ClaudeSession", "EnsureKnownModels", live);
        Check("old live catalog gains Opus 5.5", live.OfType<JsonObject>().Any(m => m["value"]!.ToString() == Current[0]));
        Check("old live catalog gains explicit Fable 5.1", live.OfType<JsonObject>().Any(m => m["value"]!.ToString() == Current[1]));
        Check("old Sonnet alias does not suppress new Sonnet", live.OfType<JsonObject>().Any(m => m["value"]!.ToString() == Current[2]));
        Check("snapshot and context variants do not duplicate existing models", live.OfType<JsonObject>().Count(m => m["value"]!.ToString() is "claude-haiku-4-5" or "claude-opus-4-8") == 0);
        Check("custom deployment is retained", live.OfType<JsonObject>().Any(m => m["resolvedModel"]!.ToString() == "enterprise-model"));
        Check("eligible live Mythos retains its native metadata", live.OfType<JsonObject>().Single(m => m["value"]!.ToString() == "mythos")["supportsFastMode"]!.GetValue<bool>() == false);
        var oldMythos = new JsonArray(Row("mythos", "Mythos", "claude-mythos-5"));
        Call("VibeCode.Protocol.ClaudeSession", "EnsureKnownModels", oldMythos);
        Check("eligible older Mythos catalog gains 5.1", oldMythos.OfType<JsonObject>().Any(m => m["value"]?.ToString() == "claude-mythos-5-1" && m["supportsFastMode"]?.GetValue<bool>() == false));
        var mythosPreview = new List<ModelChoice> { new() { Provider = "claude", Value = "mythos", Display = "Mythos", ResolvedModel = "claude-mythos-5" } };
        Call("VibeCode.UI.ProviderModelCatalog", "EnsureClaudeModelsVisible", mythosPreview);
        Check("eligible Mythos picker gains current version with effort", mythosPreview.Any(m => m.Value == "claude-mythos-5-1" && m.EffortLevels.SequenceEqual(Efforts)));
        Check("older Mythos row keeps its actual version", mythosPreview.Single(m => m.Value == "mythos").Display == "Mythos 5");
        var serialized = live.ToJsonString();
        Call("VibeCode.Protocol.ClaudeSession", "EnsureKnownModels", live);
        Check("repeated catalog merge is stable", live.ToJsonString() == serialized);
        var empty = new JsonArray();
        Call("VibeCode.Protocol.ClaudeSession", "EnsureKnownModels", empty);
        Check("empty provider model list still gains the public catalog", Current.All(id => empty.OfType<JsonObject>().Any(m => m["value"]!.ToString() == id)));
        var defaultOnly = new JsonArray(Row("default", "Default", Current[0]));
        Call("VibeCode.Protocol.ClaudeSession", "EnsureKnownModels", defaultOnly);
        Check("default resolution does not hide explicit current Opus", defaultOnly.OfType<JsonObject>().Any(m => m["value"]?.ToString() == Current[0]));
        var defaultPreview = new List<ModelChoice> { new() { Provider = "claude", Value = "default", Display = "Default", ResolvedModel = Current[0], SupportsFastMode = true } };
        Call("VibeCode.UI.ProviderModelCatalog", "EnsureClaudeModelsVisible", defaultPreview);
        Check("default-only menu puts explicit current Opus first", defaultPreview.First().Value == Current[0]);
        Check("default-only menu fast target stays on current Opus", ((ModelChoice?)Call("VibeCode.UI.ProviderModelCatalog", "FastModeSwitchTarget", defaultPreview))?.Value == Current[0]);

        var aliased = Current.Take(3).Select((id, i) => new ModelChoice { Provider = "claude", Value = new[] { "opus", "fable", "sonnet" }[i], Display = new[] { "Opus", "Fable", "Sonnet" }[i], ResolvedModel = id + "[1m]", SupportsEffort = true, EffortLevels = Efforts }).ToList();
        Call("VibeCode.UI.ProviderModelCatalog", "EnsureClaudeModelsVisible", aliased);
        Check("current resolved aliases get correct version labels", aliased.Take(3).Select(m => m.Display).SequenceEqual(Names.Take(3)));
        Check("current resolved aliases do not gain duplicate native rows", !aliased.Any(m => Current.Take(3).Contains(m.Value)));
        var raw = new List<ModelChoice> { new() { Provider = "claude", Value = Current[0], Display = Current[0], ResolvedModel = Current[0], Description = Current[0] + " · CLI custom model" } };
        Call("VibeCode.UI.ProviderModelCatalog", "EnsureClaudeModelsVisible", raw);
        Check("raw CLI model ID gets a friendly picker and pill label", raw[0].Display == Names[0] && raw[0].ShortName == Names[0]);
        var staleDescription = new List<ModelChoice> { new() { Provider = "claude", Value = "opus", Display = "Opus (1M context)", ResolvedModel = Current[0], Description = "Opus 5 with 1M context · CLI tagline" } };
        Call("VibeCode.UI.ProviderModelCatalog", "EnsureClaudeModelsVisible", staleDescription);
        Check("alias pill matches its resolved version", staleDescription[0].Display == Names[0] && staleDescription[0].ShortName == Names[0] && staleDescription[0].Description!.EndsWith("CLI tagline"));

        Check("Opus 5.5 base pricing", ModelPricing.IsPriced(Current[0]) && ModelPricing.For(Current[0]) == new ModelPricing.Price(4, 20));
        Check("Opus 5.5 cache reads use the lower rate", Math.Abs(ModelPricing.TurnCost(Current[0] + "[1m]", 1_000_000, 1_000_000, 1_000_000, 1_000_000) - 29.2) < 0.00001);
        Check("Sonnet 5.5 base pricing", ModelPricing.For(Current[2]) == new ModelPricing.Price(2, 10));
        Check("Sonnet 5 price reflects current list price", ModelPricing.For("claude-sonnet-5") == new ModelPricing.Price(2, 10));
        Check("previous Opus keeps its price", ModelPricing.For("claude-opus-5") == new ModelPricing.Price(5, 25));
        Check("CLI's dated Haiku snapshot uses Haiku pricing", ModelPricing.IsPriced("claude-haiku-4-5-20251001") && ModelPricing.For("claude-haiku-4-5-20251001") == new ModelPricing.Price(1, 5));
        Check("CLI's dated Haiku snapshot keeps its usage name", UsagePalette.DisplayName("claude-haiku-4-5-20251001") == Names[3]);
        Check("new models have known usage names", Current.Select(UsagePalette.DisplayName).SequenceEqual(Names));
        Check("new Opus keeps its family color", UsagePalette.BrushFor(Current[0]).Color == UsagePalette.BrushFor("claude-opus-5").Color);
        Check("new Sonnet keeps its family color", UsagePalette.BrushFor(Current[2]).Color == UsagePalette.BrushFor("claude-sonnet-5").Color);
        Check("new models have family energy estimates", Current.All(ModelEnergy.IsRated) && ModelEnergy.ActiveParamsBillions(Current[2]) == ModelEnergy.ActiveParamsBillions("claude-sonnet-5"));
        Check("speed mapping uses real OpenRouter IDs", preview.Where(m => Current.Take(3).Contains(m.Value)).Select(ModelSpeedService.OpenRouterId).SequenceEqual(new[] { "anthropic/claude-opus-5.5", "anthropic/claude-fable-5.1", "anthropic/claude-sonnet-5.5" }));
        var board = (ModelSpeedRow[])Call("VibeCode.Services.ModelSpeedBoard", "CreateRows")!;
        Check("speed board names current Claude models", board.Take(3).Select(m => m.DisplayName).SequenceEqual(new[] { Names[0], Names[2], Names[1] }));
    }

    private static void PickerAndTransportChecks()
    {
        Check("custom fixture override retains priority", ClaudeSession.ResolveCliPath() == Environment.ProcessPath);
        using var session = new ClaudeSession(new ClaudeSessionOptions { Cwd = Root, Model = Current[0], Effort = "max" });
        session.Start();
        PumpUntil(() => session.WhenInitialized.IsCompleted && session.Models.Count > 0);
        Check("adapter initialization merges the new lineup", Current.All(id => session.Models.OfType<JsonObject>().Any(m => m["value"]!.ToString() == id || m["resolvedModel"]!.ToString().StartsWith(id))));
        var startup = Wire().First(m => m["launch"] is not null)["launch"]!.AsArray().Select(m => m!.ToString()).ToArray();
        Check("launch passes exact Opus 5.5 ID and effort", startup[Array.IndexOf(startup, "--model") + 1] == Current[0] && startup[Array.IndexOf(startup, "--effort") + 1] == "max");
        foreach (var id in Current.Take(3))
        {
            var switching = session.SetModelAsync(id, "xhigh");
            PumpUntil(() => switching.IsCompleted);
            switching.GetAwaiter().GetResult();
            Check("runtime switch passes exact " + id, Wire().Any(m => m["request"]?["subtype"]?.ToString() == "set_model" && m["request"]?["model"]?.ToString() == id && m["request"]?["effort"]?.ToString() == "xhigh"));
        }

        var chat = new ChatViewModel(Root, provider: "claude") { ExcludeFromMemory = true };
        try
        {
            chat.Start();
            PumpUntil(() => chat.Models.Count > 0 && chat.Status == "idle");
            AppSettings.Current.DefaultProvider = "codex";
            chat.RefreshModelPicker();
            Check("normal chat picker has the current lineup", Current.All(id => chat.PickerModels.Any(m => m.Value == id || m.Model.ResolvedModel?.StartsWith(id) == true)));
            Check("normal Claude chat keeps selectable Claude models when New Chat uses Codex", chat.PickerModels.All(m => m.Model.Provider == "claude" && m.CanApply));
            Check("old live Opus alias keeps its actual version label", chat.Models.Single(m => m.Value == "opus").Display == "Opus 4.8");
            foreach (var id in Current.Take(3))
            {
                chat.SetPickerModel(chat.PickerModels.Single(m => m.Value == id));
                Check("normal chat selects " + id, chat.Model == id && chat.EffortOptions.Any(m => m.Value == "xhigh") && chat.ModelDisplay == Names[Array.IndexOf(Current, id)]);
            }
            chat.BridgeLabel = "Claude catalog test";
            typeof(ChatViewModel).GetProperty("BridgeSingleTerminal", Flags)!.SetValue(chat, true);
            chat.RefreshModelPicker();
            Check("bridge keeps its Claude catalog when global provider differs", chat.PickerModels.All(m => m.Model.Provider == "claude" && m.CanApply));
            var bridgeModels = (IEnumerable<ModelChoice>)Call("VibeCode.UI.BridgeAgentConfigurationPolicy", "ModelsFor", "claude")!;
            Check("orchestrator setup has current models and effort", Current.Take(3).All(id => bridgeModels.Any(m => m.Value == id && m.EffortLevels.SequenceEqual(Efforts))));
            chat.SetFastMode(true);
            Check("bridge fast-mode switch selects new Opus", chat.Model == Current[0] && chat.FastMode);
            Check("provider model update keeps goal and native compaction in the IDE slash menu", chat.Commands.Select(c => c.Name).ToHashSet().SetEquals(["goal", "compact"]));
        }
        finally { chat.Close(); }
    }

    private static void InstalledCatalogCheck(string executable, string selectedModel)
    {
        var psi = (ProcessStartInfo)Call("VibeCode.Protocol.ClaudeSession", "CreateStartInfo",
            new ClaudeSessionOptions { Cwd = Root, Model = selectedModel, ConfigDirectory = Path.Combine(Root, "empty-claude-home") })!;
        psi.FileName = executable;
        psi.ArgumentList[psi.ArgumentList.IndexOf("--setting-sources=user,project,local")] = "--setting-sources=";
        var emptyMcp = Path.Combine(Root, "empty-mcp.json");
        File.WriteAllText(emptyMcp, "{\"mcpServers\":{}}");
        psi.ArgumentList.Add("--strict-mcp-config");
        psi.ArgumentList.Add("--mcp-config");
        psi.ArgumentList.Add(emptyMcp);
        using var process = Process.Start(psi)!;
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            process.StandardInput.WriteLine(new JsonObject { ["type"] = "control_request", ["request_id"] = "catalog-only", ["request"] = new JsonObject { ["subtype"] = "initialize" } }.ToJsonString());
            process.StandardInput.Flush();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            JsonArray? models = null;
            while (awaitLine() is { } line)
            {
                if (JsonNode.Parse(line) is not JsonObject message || message["type"]?.ToString() != "control_response") continue;
                models = message["response"]?["response"]?["models"]?.AsArray();
                if (models is not null) break;
            }
            Check("installed CLI initializes " + selectedModel + " without generation", models is { Count: > 0 });
            File.WriteAllText(Path.Combine(Root, "installed-cli-" + selectedModel + ".json"), models!.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            var selected = models.OfType<JsonObject>().FirstOrDefault(m => m["value"]?.ToString() == selectedModel
                || ModelPricing.CanonicalId(m["resolvedModel"]?.ToString()) == selectedModel);
            Check("installed CLI recognizes selected " + selectedModel, selected is not null && selected["supportsEffort"]?.GetValue<bool>() == true);
            Call("VibeCode.Protocol.ClaudeSession", "EnsureKnownModels", models);
            Check("real CLI catalog gains all current public choices", Current.All(id => models.OfType<JsonObject>().Any(m => m["value"]?.ToString() == id || m["resolvedModel"]?.ToString().StartsWith(id) == true)));
            process.StandardInput.WriteLine(new JsonObject { ["type"] = "control_request", ["request_id"] = "switch-only", ["request"] = new JsonObject { ["subtype"] = "set_model", ["model"] = selectedModel, ["effort"] = "max" } }.ToJsonString());
            process.StandardInput.Flush();
            JsonObject? switched = null;
            while (awaitLine() is { } switchLine)
            {
                var reply = JsonNode.Parse(switchLine);
                if (reply?["type"]?.ToString() != "control_response" || reply["response"]?["request_id"]?.ToString() != "switch-only") continue;
                switched = reply["response"]?.AsObject();
                break;
            }
            File.WriteAllText(Path.Combine(Root, "installed-switch-" + selectedModel + ".json"), switched?.ToJsonString() ?? "null");
            var needsAuthentication = switched?["subtype"]?.ToString() == "error" && switched["error_code"]?.ToString() == "check_failed"
                && switched["error"]?.ToString().Contains("Could not resolve authentication method", StringComparison.Ordinal) == true;
            Check(needsAuthentication ? "real CLI checks account access for " + selectedModel + " in the empty profile"
                : "real CLI accepts model and max effort for " + selectedModel,
                needsAuthentication || switched?["subtype"]?.ToString() == "success");
            string? awaitLine() => process.StandardOutput.ReadLineAsync(timeout.Token).AsTask().GetAwaiter().GetResult();
        }
        finally
        {
            process.StandardInput.Close();
            if (!process.WaitForExit(3000)) { process.Kill(true); process.WaitForExit(); }
            errors.GetAwaiter().GetResult();
        }
    }

    private static JsonObject Row(string value, string display, string resolved) => new()
    {
        ["value"] = value, ["displayName"] = display, ["resolvedModel"] = resolved,
        ["supportedEffortLevels"] = new JsonArray(value == "haiku" ? Array.Empty<JsonNode?>() : Efforts.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
        ["supportsEffort"] = value != "haiku", ["supportsAutoMode"] = value != "haiku",
        ["supportsFastMode"] = value is "opus" or "default", ["isDefault"] = value == "default",
    };

    private static List<JsonObject> Wire() => File.Exists(Environment.GetEnvironmentVariable("CLAUDE_MODEL_FIXTURE"))
        ? File.ReadAllLines(Environment.GetEnvironmentVariable("CLAUDE_MODEL_FIXTURE")!).Select(line => JsonNode.Parse(line)!.AsObject()).ToList() : [];

    private static void PumpUntil(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Provider fixture did not initialize.");
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(10);
        }
    }

    private static int Fixture()
    {
        void Log(JsonObject row) => File.AppendAllText(Environment.GetEnvironmentVariable("CLAUDE_MODEL_FIXTURE")!, row.ToJsonString() + "\n");
        Log(new JsonObject { ["launch"] = new JsonArray(Environment.GetCommandLineArgs().Skip(1).Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()) });
        while (Console.ReadLine() is { } line)
        {
            var request = JsonNode.Parse(line)!.AsObject();
            Log(request);
            if (request["type"]?.ToString() != "control_request") throw new InvalidOperationException("Catalog checks must never generate a model reply.");
            var payload = new JsonObject();
            if (request["request"]?["subtype"]?.ToString() == "initialize")
            {
                payload["commands"] = new JsonArray(new JsonObject { ["name"] = "init", ["description"] = "native command" });
                payload["models"] = new JsonArray(Row("default", "Default", "claude-opus-4-8"), Row("opus", "Opus", "claude-opus-4-8[1m]"),
                    Row("fable", "Fable", "claude-fable-5"), Row("sonnet", "Sonnet", "claude-sonnet-5"), Row("haiku", "Haiku", "claude-haiku-4-5-20251001"));
            }
            Console.WriteLine(new JsonObject { ["type"] = "control_response", ["response"] = new JsonObject { ["subtype"] = "success", ["request_id"] = request["request_id"]!.DeepClone(), ["response"] = payload } }.ToJsonString());
            Console.Out.Flush();
        }
        return 0;
    }
}
