using System.IO;
using System.Text.Json.Nodes;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    private static void VerifyRemovedGemini()
    {
        var assembly = typeof(ChatViewModel).Assembly;
        Check("Gemini has no CLI installer", !CliInstallationService.Supports("gemini"));
        Check("Gemini is absent from bridge creation", !BridgeSetupPanel.Providers.Any(p => p.Id == "gemini"));
        Check("Gemini is absent from role configuration", !((string[])assembly.GetType("VibeCode.UI.BridgeAgentConfigurationPolicy")!.GetField("Providers", Flags)!.GetValue(null)!).Contains("gemini"));
        foreach (var type in new[] { "VibeCode.Protocol.GeminiSession", "VibeCode.Services.GeminiRuntime", "VibeCode.Services.GeminiUsageService" })
            Check(type + " is removed from the app", assembly.GetType(type) is null);
        Check("MCP has no Gemini destination", typeof(McpServerDefinition).GetProperty("UseGemini") is null);
        try
        {
            CliInstallationService.InstallAsync("gemini").GetAwaiter().GetResult();
            Check("retired CLI installation is rejected before download", false);
        }
        catch (ArgumentException) { Check("retired CLI installation is rejected before download", true); }

        var history = assembly.GetType("VibeCode.Services.LegacyGeminiHistory")!;
        List<TranscriptMessage>? Parse(string text, string id) =>
            (List<TranscriptMessage>?)history.GetMethod("Parse", Flags)!.Invoke(null, [text, id]);
        const string json = """
            {"sessionId":"saved-session","messages":[
              {"id":"one","type":"user","content":[{"text":"Earlier question"}]},
              {"id":"two","type":"gemini","content":"Earlier answer"}]}
            """;
        var messages = Parse(json, "saved-session")!;
        Check("native JSON conversations remain readable", messages.Count == 2 && messages[0].Type == "user"
            && messages[1].Message["content"]![0]!["text"]!.ToString() == "Earlier answer");
        Check("history never loads another session", Parse(json, "wrong-session") is null);
        const string jsonl = """
            {"sessionId":"saved-session"}
            {"id":"one","type":"user","content":"Earlier question"}
            {"id":"two","type":"gemini","content":"Partial answer"}
            {"id":"two","content":{"parts":[{"text":"Completed answer"}]}}
            {"id":"three"
            """;
        messages = Parse(jsonl, "saved-session")!;
        Check("JSONL upserts do not duplicate messages and tolerate interrupted writes", messages.Count == 2
            && messages[1].Message["content"]![0]!["text"]!.ToString() == "Completed answer");
        var settings = new AppSettings { DefaultProvider = "gemini" };
        Check("new chats cannot default to removed Gemini", settings.NormalizeProviderModelSlots() && settings.DefaultProvider == "claude");

        var originalHome = Environment.GetEnvironmentVariable("GEMINI_CLI_HOME");
        try
        {
            var home = Path.Combine(_root, "legacy-history");
            var directory = Path.Combine(home, ".gemini", "tmp", "fixture", "chats");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "session-2026-saved-se.jsonl"), jsonl);
            Environment.SetEnvironmentVariable("GEMINI_CLI_HOME", home);
            var chat = new ChatViewModel(_root, resume: "saved-session", provider: "gemini");
            Chats.Add(chat);
            chat.Start();
            PumpUntil(() => chat.Items.OfType<BannerItem>().Any(item => item.Text.Contains("read-only")));
            Check("saved Gemini chats preserve their provider and cannot send", chat.IsGemini && !chat.CanCompose && !chat.Send("must not run"));
            Check("opening history launches no session or sign-in prompt", typeof(ChatViewModel).GetField("_session", Flags)!.GetValue(chat) is null && !chat.AuthNeeded);
            Check("opening history replays saved messages", chat.Items.OfType<UserItem>().Any() && chat.Items.OfType<TextItem>().Any());
            var defaults = (AppSettings.Current.DefaultModel, AppSettings.Current.DefaultEffort);
            chat.SetModel("retired-gemini-model"); chat.SetEffort("high");
            Check("archive controls cannot change another provider's defaults", defaults == (AppSettings.Current.DefaultModel, AppSettings.Current.DefaultEffort));
            var count = chat.Items.Count;
            chat.Start();
            Check("reopening does not duplicate transcript or removal notice", chat.Items.Count == count);
        }
        finally { Environment.SetEnvironmentVariable("GEMINI_CLI_HOME", originalHome); }
    }
}
