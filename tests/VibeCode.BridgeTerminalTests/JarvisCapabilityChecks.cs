using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    private static void VerifyJarvisSettingsAndNewChats()
    {
        var settings = AppSettings.Current;
        settings.SecondBrainEnabled = false;
        settings.McpServers.Clear();
        settings.JarvisProvider = "codex";
        settings.JarvisModel = "gpt-6-luna";
        settings.DefaultProvider = "codex";
        settings.DefaultCodexModel = null;
        settings.AgentSwarmsEnabled = false;

        settings.GrokDeleteProxy = "private-proxy-credential";
        settings.OwnedSessions.Add("preserved-session");
        settings.TrySave();
        Check("invalid legacy MCP URLs cannot prevent settings inspection", JarvisSettingsCatalog.DescribeServer(
            new McpServerDefinition { Name = "invalid-endpoint", Url = "javascript:invalid" })["url"]!.ToString() == "");
        var vm = new MainViewModel();
        string? category = null;
        vm.JarvisSettingsRequested += value => category = value;

        var update = SettingsPlan("Turn off your voice and set your volume to 50.",
            new("JarvisVoiceEnabled", "false"), new("JarvisSpeechVolume", "50"));
        var reply = Simulate(update.Request, update.Json, vm);
        var persisted = LoadJarvisSettings();
        Check("settings requests reach the real host and persist both requested values", !settings.JarvisVoiceEnabled
            && settings.JarvisSpeechVolume == 50 && !persisted.JarvisVoiceEnabled && persisted.JarvisSpeechVolume == 50);
        Check("settings receipts use stored values and preserve unrelated state", reply.Contains("JarvisSpeechVolume = 50")
            && persisted.OwnedSessions.Contains("preserved-session") && persisted.GrokDeleteProxy == "private-proxy-credential"
            && !persisted.SecondBrainEnabled && !reply.Contains("unverified"));
        Check("setting names are canonical before dispatching live UI changes", JarvisPlanParser.Parse(
            SettingsPlan("Change theme", new JarvisSettingChange("uimode", "cli")).Json, "Change theme")
            .Actions.Single().Changes.Single().Name == "UiMode");
        RejectJarvis("settings cannot address transcript-restoration state", () => JarvisPlanParser.Parse(
            SettingsPlan("Change settings", new JarvisSettingChange("OpenChats", "[]")).Json, "Change settings"));
        RejectJarvis("unknown setting names do not silently succeed", () => JarvisPlanParser.Parse(
            SettingsPlan("Change settings", new JarvisSettingChange("ImaginarySwitch", "true")).Json, "Change settings"));
        RejectJarvis("out-of-range volume is rejected instead of clamped", () => JarvisPlanParser.Parse(
            SettingsPlan("Change settings", new JarvisSettingChange("JarvisSpeechVolume", "101")).Json, "Change settings"));
        RejectJarvis("blank booleans cannot become defaults", () => JarvisPlanParser.Parse(
            SettingsPlan("Change settings", new JarvisSettingChange("JarvisVoiceEnabled", "")).Json, "Change settings"));
        RejectJarvis("nonfinite numbers cannot reach settings", () => JarvisPlanParser.Parse(
            SettingsPlan("Change settings", new JarvisSettingChange("JarvisSpeechRate", "NaN")).Json, "Change settings"));
        RejectJarvis("an unrelated request cannot opt in to Second Brain", () => JarvisPlanParser.Parse(
            SettingsPlan("Enable all extensions", new JarvisSettingChange("SecondBrainEnabled", "true")).Json, "Enable all extensions"));
        var invalidBatch = SettingsPlan("Change settings", new("JarvisSpeechVolume", "61"), new("JarvisProvider", "claude"));
        try { Simulate(invalidBatch.Request, invalidBatch.Json, vm); throw new Exception("Invalid combination accepted"); }
        catch (InvalidOperationException) { }
        Check("a provider/model mismatch leaves every setting in the batch unchanged", settings.JarvisProvider == "codex"
            && settings.JarvisSpeechVolume == 50 && LoadJarvisSettings().JarvisSpeechVolume == 50);
        var fail = JarvisPlanParser.Parse(SettingsPlan("Volume to 61", new JarvisSettingChange("JarvisSpeechVolume", "61")).Json, "Volume to 61").Actions.Single();
        try { JarvisSettingsActions.Execute(fail, CancellationToken.None, save: () => new IOException("fixture write failure")); }
        catch (IOException) { }
        Check("failed settings writes roll back and never claim success", settings.JarvisSpeechVolume == 50 && LoadJarvisSettings().JarvisSpeechVolume == 50);

        const string addRequest = "Add a docs MCP server at https://example.com/mcp for Codex only.";
        var addJson = PlanJson("configure_mcp", addRequest, new()
        {
            ["target"] = "docs", ["config_json"] = new JsonObject
            {
                ["transport"] = "http", ["url"] = "https://example.com/mcp", ["useClaude"] = false,
                ["useCodex"] = true, ["useKimi"] = false, ["useGrok"] = false,
                ["headers"] = new JsonObject { ["Authorization"] = "Bearer private-existing-secret" },
            }.ToJsonString(),
        });
        var added = Simulate(addRequest, addJson, vm);
        var original = settings.McpServers.Single();
        Check("MCP requests persist the validated shared catalog definition", added.Contains("Added MCP server 'docs'")
            && original.Transport == "http" && original.UseCodex && !original.UseClaude && LoadJarvisSettings().McpServers.Single().Url == original.Url);
        var snapshot = ((IJarvisActionHost)vm).GetContext().Settings!;
        Check("settings context exposes real preferences and a redacted server inventory", snapshot["preferences"]!.AsArray().Count >= 70
            && snapshot["mcp_servers"]![0]!["name"]!.ToString() == "docs"
            && !snapshot.ToJsonString().Contains("private-existing-secret") && !snapshot.ToJsonString().Contains("private-proxy-credential"));
        const string patchRequest = "Disable docs and set its tool timeout to 90 seconds.";
        var patchJson = PlanJson("configure_mcp", patchRequest, new()
        { ["target"] = "docs", ["config_json"] = "{\"enabled\":false,\"toolTimeoutSeconds\":90}" });
        var patched = Simulate(patchRequest, patchJson, vm);
        var changed = settings.McpServers.Single();
        Check("partial MCP edits preserve identity, endpoint, provider targets and credentials", patched.Contains("Updated")
            && changed.Id == original.Id && !changed.Enabled && changed.ToolTimeoutSeconds == 90 && changed.Url == original.Url
            && changed.Headers["Authorization"] == "Bearer private-existing-secret" && changed.UseCodex && !changed.UseClaude);
        var invalidMcp = PlanJson("configure_mcp", "Change MCP", new()
        { ["target"] = "docs", ["config_json"] = "{\"url\":\"javascript:bad\",\"enabled\":true}" });
        try { Simulate("Change MCP", invalidMcp, vm); throw new Exception("Invalid endpoint accepted"); }
        catch (ArgumentException) { }
        Check("invalid MCP batches leave saved and live definitions untouched", !settings.McpServers.Single().Enabled
            && LoadJarvisSettings().McpServers.Single().Url == "https://example.com/mcp");
        RejectJarvis("MCP changes cannot include arbitrary command actions", () => JarvisPlanParser.Parse(
            PlanJson("configure_mcp", "Change MCP", new() { ["target"] = "docs", ["config_json"] = "{\"run_script\":\"bad\"}" }), "Change MCP"));
        var mcpFail = JarvisPlanParser.Parse(patchJson, patchRequest).Actions.Single() with
        { McpConfiguration = new() { ["enabled"] = true } };
        try { JarvisSettingsActions.Execute(mcpFail, CancellationToken.None, save: () => new IOException("fixture write failure")); }
        catch (IOException) { }
        Check("failed MCP writes restore the previous catalog", ReferenceEquals(settings.McpServers.Single(), changed) && !changed.Enabled);
        using (var stop = new CancellationTokenSource())
        {
            stop.Cancel();
            try { JarvisSettingsActions.Execute(fail, stop.Token); } catch (OperationCanceledException) { }
            Check("cancellation before settings execution has no effects", settings.JarvisSpeechVolume == 50);
        }
        var read = Simulate("Read my settings", PlanJson("read_settings", "Read my settings", new() { ["target"] = "Jarvis" }), vm);
        Check("read settings reports persisted preferences without credentials", read.Contains("JarvisSpeechVolume = 50")
            && !read.Contains("private-existing-secret") && !read.Contains("private-proxy-credential"));
        Simulate("Open MCP settings", PlanJson("open_settings", "Open MCP settings", new() { ["target"] = "MCP servers" }), vm);
        Check("settings actions reach the requested category in the shell", category == "MCP servers");

        var previousRuntime = Environment.GetEnvironmentVariable("VIBECODE_CODEX_PATH");
        Environment.SetEnvironmentVariable("VIBECODE_CODEX_PATH", Path.ChangeExtension(Assembly.GetExecutingAssembly().Location, ".exe"));
        var directory = Path.Combine(_root, "jarvis-three-chats-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            const string createRequest = "Can you open three chats in this directory?";
            var json = PlanJson("create_chats", createRequest, new() { ["path"] = directory, ["count"] = 3 });
            var opened = Simulate(createRequest, json, vm);
            Chats.AddRange(vm.Chats);
            Check("three-chat simulation opens exactly three real sidebar chats", vm.Chats.Count == 3
                && vm.Chats.All(chat => chat.Cwd == directory && chat.Provider == "codex" && chat.SessionId is not null && chat.Status == "idle")
                && opened.Contains("Opened 3 new chats"));
            Check("empty-chat creation sends no coding tasks and selects a created chat", vm.Chats.All(chat => !chat.Items.OfType<UserItem>().Any())
                && vm.Chats.Contains(vm.ActiveChat!));
            RejectJarvis("zero-chat requests are invalid", () => JarvisPlanParser.Parse(
                PlanJson("create_chats", createRequest, new() { ["path"] = directory, ["count"] = 0 }), createRequest));
            RejectJarvis("oversized chat batches are rejected", () => JarvisPlanParser.Parse(
                PlanJson("create_chats", createRequest, new() { ["path"] = directory, ["count"] = 13 }), createRequest));
            using var stopped = new CancellationTokenSource(); stopped.Cancel();
            var pending = ((IJarvisNewChatsHost)vm).CreateChatsAsync(directory, 3, stopped.Token);
            try { pending.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
            Check("cancelled creation cannot leave extra chats", vm.Chats.Count == 3);
        }
        finally { Environment.SetEnvironmentVariable("VIBECODE_CODEX_PATH", previousRuntime); }
        var removed = Simulate("Remove docs MCP server", PlanJson("remove_mcp", "Remove docs MCP server", new() { ["target"] = "docs" }), vm);
        Check("MCP removal is verified and preserves unrelated settings", removed.Contains("Removed")
            && settings.McpServers.Count == 0 && LoadJarvisSettings().McpServers.Count == 0 && settings.JarvisSpeechVolume == 50);
        vm.ShutdownJarvis();
    }

    private static (string Request, string Json) SettingsPlan(string request, params JarvisSettingChange[] changes) =>
        (request, PlanJson("update_settings", request, new()
        {
            ["target"] = "", ["changes"] = new JsonArray(changes.Select(change => (JsonNode?)new JsonObject
                { ["name"] = change.Name, ["value"] = change.Value }).ToArray()),
        }));

    private static AppSettings LoadJarvisSettings() => JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path.Combine(AppSettings.Dir, "settings.json")))!;

    private static string Simulate(string request, string json, MainViewModel host, IJarvisDesktopActions? desktop = null)
    {
        var plannerSession = new JarvisPlanningSession(json);
        var planner = new JarvisDialogueService(Path.Combine(_root, "jarvis-planner-fixture"), (_, _) => plannerSession);
        var runtime = new JarvisRuntime(planner, host, desktop);
        var turn = runtime.SubmitAsync(request, new("codex", null, null), CancellationToken.None);
        PumpUntil(() => turn.IsCompleted);
        return turn.GetAwaiter().GetResult();
    }
}
