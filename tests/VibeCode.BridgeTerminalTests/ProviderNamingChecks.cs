using System.Text.Json.Nodes;
using VibeCode.AgentStatus.Mcp.Bridge;
using VibeCode.Protocol;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    private static void VerifyAllProviderNaming(MainViewModel vm, string directory)
    {
        ChatViewModel Chat(string provider, string? id = null)
        {
            var chat = new ChatViewModel(directory, provider: provider) { Status = "idle", SessionId = id ?? Guid.NewGuid().ToString("N") };
            typeof(ChatViewModel).GetField("_session", Flags)!.SetValue(chat, new FakeSession());
            typeof(ChatViewModel).GetField("_bridgeSessionInitialized", Flags)!.SetValue(chat, true);
            Chats.Add(chat); vm.Chats.Add(chat); Call(vm, "Track", chat);
            return chat;
        }

        var providerNames = new[] { "claude", "codex", "kimi", "glm" };
        foreach (var provider in providerNames)
        {
            var chat = Chat(provider);
            var endpoint = Call(chat, "EnsureBridgeMcp")!;
            var registration = (McpServerDefinition)Call(endpoint, "Registration")!;
            var name = provider switch
            {
                "glm" => "chat_set_title",
                _ => "mcp__" + registration.RuntimeName + "__chat_set_title",
            };
            var sessionRules = (string)Call(chat, "ChatNamingSessionInstructions")!;
            Check(provider + ": session instructions grant the correct naming tool", sessionRules.Contains(name)
                && sessionRules.Contains("already authorized in every chat mode") && sessionRules.Contains("Never rename it later"));
            Check(provider + ": first request dispatches", chat.Send("Repair login validation."));
            PumpUntil(() => Session(chat).Sent.Count == 1);
            var prompt = Session(chat).Sent[0];
            Check(provider + ": first-turn instructions name the actual tool", prompt.Contains(name) && prompt.Contains("FIRST tool call"));
            FinishWork(chat, "Login request received.");

            foreach (var mode in new[] { "default", "plan", "acceptEdits", "auto", "bypassPermissions" })
            {
                chat.SetMode(mode);
                var prior = Session(chat).PermissionResponses.Count;
                Call(chat, "OnPermissionRequested", new PermissionRequest
                { RequestId = provider + mode, ToolName = name, Input = new JsonObject { ["title"] = "Repair login validation" } });
                Check(provider + ": naming is preapproved in " + mode, Session(chat).PermissionResponses.Count == prior + 1
                    && Session(chat).PermissionResponses.Last()["behavior"]!.ToString() == "allow" && !chat.Items.OfType<PermItem>().Any());
            }

            if (provider is "kimi")
            {
                foreach (var kind in new[] { "other", "edit", "execute" })
                    Check(provider + ": ACP retains exact identity with kind=" + kind,
                        (string)typeof(KimiSession).GetMethod("NormalizeToolName", Flags)!.Invoke(null, [name, kind, new JsonObject()])! == name);
                var servers = McpCatalog.BuildKimiAcpServers([registration]);
                Check(provider + ": ACP registers the naming server", servers.OfType<JsonObject>().Any(s => s["name"]!.ToString() == registration.RuntimeName));
            }
            if (provider == "codex")
                Check("Codex launch preapproves chat_set_title", McpCatalog.BuildCodexProjection([registration]).ConfigOverrides.Any(c => c.Contains("\"chat_set_title\" = { approval_mode = \"approve\" }")));
            if (provider == "glm") VerifyGlmNamingAdapter(endpoint);

            var named = BridgeMcpClient.InvokeAsync((string)Property(endpoint, "PipeName")!, "chat_set_title", new() { ["title"] = "Repair login validation" });
            PumpUntil(() => named.IsCompleted);
            Check(provider + ": ordinary chat can be named through real MCP transport", named.GetAwaiter().GetResult()["applied"]!.GetValue<bool>() && chat.Title == "Repair login validation");
            Check(provider + ": later AI rename is refused", !Tool(chat, "chat_set_title", new() { ["title"] = "Unrelated new task" })["applied"]!.GetValue<bool>());
            chat.Send("Now work on checkout.");
            PumpUntil(() => Session(chat).Sent.Count == 2);
            Check(provider + ": changed tasks preserve the first AI title", Session(chat).Sent[1].Contains("Do not call chat_set_title")
                && !Session(chat).Sent[1].Contains("This chat still needs"));
            FinishWork(chat);

            var manual = Chat(provider);
            var stale = Chat(provider, manual.SessionId);
            const string humanTitle = "My exact human title can exceed five words";
            vm.RenameChat(manual, humanTitle);
            Check(provider + ": human title suppresses session naming instructions", ((string)Call(manual, "ChatNamingSessionInstructions")!).Contains("named by the user")
                && !((string)Call(manual, "ChatNamingSessionInstructions")!).Contains("BEFORE starting"));
            stale.Send("First actual request after the user named the chat.");
            PumpUntil(() => Session(stale).Sent.Count == 1);
            Check(provider + ": an already-open view sees the human title before dispatch", stale.Title == humanTitle && Session(stale).Sent[0].Contains("named by the user")
                && !Session(stale).Sent[0].Contains("This chat still needs"));
            FinishWork(stale);
            Check(provider + ": AI cannot overwrite a title named by a human first", !Tool(manual, "chat_set_title", new() { ["title"] = "AI replacement" })["applied"]!.GetValue<bool>()
                && !Tool(stale, "chat_set_title", new() { ["title"] = "Stale replacement" })["applied"]!.GetValue<bool>() && manual.Title == humanTitle);
            var resumed = new ChatViewModel(directory, resume: manual.SessionId, provider: provider);
            Chats.Add(resumed); vm.Chats.Add(resumed); Call(vm, "Track", resumed);
            Check(provider + ": reopening retains the human title", resumed.Title == humanTitle && !Tool(resumed, "chat_set_title", new() { ["title"] = "Resumed replacement" })["applied"]!.GetValue<bool>());
            vm.RenameChat(manual, "Another human title");
            Check(provider + ": the human can still rename freely", manual.Title == "Another human title");
        }
    }

    private static void VerifyGlmNamingAdapter(object endpoint)
    {
        using var glm = new GlmSession(new GlmSessionOptions
        {
            Cwd = Environment.CurrentDirectory,
            ApiKeys = ["offline-fixture"],
            PermissionMode = "plan",
            BridgeMcpPipe = (string)Property(endpoint, "PipeName")!,
        });
        var schema = (JsonArray)Call(glm, "ToolSchema")!;
        Check("GLM advertises its direct naming function", schema.Any(tool => tool?["function"]?["name"]?.ToString() == "chat_set_title"));
        var permissions = 0;
        glm.PermissionRequested += _ => permissions++;
        var decision = (Task)Call(glm, "DecideAsync", "chat_set_title", "glm-title", new JsonObject { ["title"] = "Name fixture" }, CancellationToken.None)!;
        PumpUntil(() => decision.IsCompleted);
        var result = decision.GetType().GetProperty("Result")!.GetValue(decision)!;
        Check("GLM's native approval path permits naming in Plan mode", (bool)Property(result, "Allowed")! && permissions == 0);
    }
}
