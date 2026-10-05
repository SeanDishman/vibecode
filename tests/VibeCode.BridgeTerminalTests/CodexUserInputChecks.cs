using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using VibeCode.Protocol;
using VibeCode.UI;

internal static partial class Program
{
    /// <summary>The request a live Codex 0.159.2 turn sent (artifacts/codex-user-input/probe_request_user_input.py).</summary>
    private static JsonObject CodexColorQuestion() => JsonNode.Parse("""
        {"threadId":"t","turnId":"u","itemId":"call_1","isBlocking":false,
         "questions":[{"id":"preferred_color","header":"Color","question":"Which color do you prefer?","isOther":true,"isSecret":false,
           "options":[{"label":"Red","description":"Choose red."},{"label":"Blue","description":"Choose blue."}]}]}
        """)!.AsObject();

    /// <summary>Codex request_user_input without a model: the tool is enabled for interactive sessions, the shared
    /// question card answers by Codex question id, and the session replies in Codex's schema shape
    /// ({answers: {id: {answers: [..]}}}), the exact reply a live Codex turn accepted.</summary>
    private static JsonObject BuildUserInputAnswers(JsonObject request, JsonNode? answers) =>
        (JsonObject)typeof(CodexSession).GetMethod("BuildUserInputAnswers", Flags)!.Invoke(null, [request, answers])!;

    private static void VerifyCodexUserInput()
    {
        var addFlags = typeof(CodexSession).GetMethod("AddSessionFeatureFlags", Flags)!;
        var interactive = new ProcessStartInfo();
        addFlags.Invoke(null, [interactive, false]);
        var dialogue = new ProcessStartInfo();
        addFlags.Invoke(null, [dialogue, true]);
        Check("interactive Codex sessions offer request_user_input in Default mode",
            interactive.ArgumentList.Contains("features.default_mode_request_user_input=true"));
        Check("dialogue-only Codex sessions keep it off", !dialogue.ArgumentList.Any(a => a.Contains("request_user_input")));

        var (_, codexTeam) = Team("codex-question", 1);
        var codex = codexTeam[0];
        Call(codex, "OnPermissionRequested", new PermissionRequest
            { RequestId = "codex:7", ToolName = "AskUserQuestion", ToolUseId = "call_1", Input = CodexColorQuestion() });
        var card = codex.Items.OfType<PermItem>().Last();
        Check("a Codex question card keeps the question id and options", card.Kind == "question"
            && card.Questions.Single().Id == "preferred_color"
            && card.Questions.Single().Options.Select(o => o.Label).SequenceEqual(["Red", "Blue"]));
        card.Questions.Single().Options.Single(o => o.Label == "Blue").Selected = true;
        codex.AnswerQuestion(card);
        var cardAnswers = Session(codex).PermissionResponses.Last()["updatedInput"]!["answers"]!;
        Check("the Codex card answers by question id with a list", cardAnswers.ToJsonString() == """{"preferred_color":["Blue"]}""");
        Check("CodexSession turns the card's answer into Codex's response shape",
            BuildUserInputAnswers(CodexColorQuestion(), cardAnswers).ToJsonString()
            == """{"preferred_color":{"answers":["Blue"]}}""");
        Check("an answer keyed by question text (Claude's shape) still reaches the right Codex question",
            BuildUserInputAnswers(CodexColorQuestion(), new JsonObject { ["Which color do you prefer?"] = "Red" }).ToJsonString()
            == """{"preferred_color":{"answers":["Red"]}}""");

        var (_, claudeTeam) = Team("claude-question", 1, "claude");
        var claude = claudeTeam[0];
        Call(claude, "OnPermissionRequested", new PermissionRequest
        {
            RequestId = "claude:1", ToolName = "AskUserQuestion", ToolUseId = "toolu_1",
            Input = JsonNode.Parse("""{"questions":[{"question":"Which color do you prefer?","header":"Color","multiSelect":false,"options":[{"label":"Red","description":"r"},{"label":"Blue","description":"b"}]}]}"""),
        });
        var claudeCard = claude.Items.OfType<PermItem>().Last();
        claudeCard.Questions.Single().Options.Single(o => o.Label == "Blue").Selected = true;
        claude.AnswerQuestion(claudeCard);
        Check("Claude's AskUserQuestion answer format is unchanged",
            Session(claude).PermissionResponses.Last()["updatedInput"]!["answers"]!.ToJsonString() == """{"Which color do you prefer?":"Blue"}""");

        using var session = new CodexSession(new CodexSessionOptions { Cwd = _root });
        var handle = typeof(CodexSession).GetMethod("HandleServerMessage", Flags)!;
        var wire = (Channel<string>)typeof(CodexSession).GetField("_writeQueue", Flags)!.GetValue(session)!;
        handle.Invoke(session, ["item/tool/requestUserInput", CodexColorQuestion(), JsonValue.Create(7)]);
        Check("a headless Codex session answers at once instead of stalling the turn",
            wire.Reader.TryRead(out var headless) && headless == """{"id":7,"result":{"answers":{}}}""");
        PermissionRequest? raised = null;
        session.PermissionRequested += req => raised = req;
        handle.Invoke(session, ["item/tool/requestUserInput", CodexColorQuestion(), JsonValue.Create(8)]);
        Check("an interactive Codex session raises the question card and waits", raised?.ToolName == "AskUserQuestion" && !wire.Reader.TryRead(out _));
        session.RespondPermission(raised!.RequestId, new JsonObject
            { ["behavior"] = "allow", ["updatedInput"] = new JsonObject { ["answers"] = cardAnswers.DeepClone() } }, raised.ToolUseId);
        Check("the reply on the wire is exactly what live Codex accepted",
            wire.Reader.TryRead(out var reply) && reply == """{"id":8,"result":{"answers":{"preferred_color":{"answers":["Blue"]}}}}""");
    }

    /// <summary>One real Codex turn through VibeCode's CodexSession: the model must be able to ask, and must receive the
    /// answer the question card produces. Needs VIBECODE_TEST_CODEX_HOME (a signed-in Codex home); spends one short turn.</summary>
    private static void VerifyCodexUserInputLive()
    {
        var home = Environment.GetEnvironmentVariable("VIBECODE_TEST_CODEX_HOME");
        if (string.IsNullOrWhiteSpace(home) || !File.Exists(Path.Combine(home, "auth.json")))
            throw new InvalidOperationException("Set VIBECODE_TEST_CODEX_HOME to a signed-in Codex home to run this live check.");
        var cwd = Path.Combine(_root, "codex-live-question-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(cwd);
        using var session = new CodexSession(new CodexSessionOptions { Cwd = cwd, HomeDirectory = home });
        PermissionRequest? asked = null;
        var replies = new List<string>();
        var finished = false;
        session.PermissionRequested += req =>
        {
            asked = req;
            var id = req.Input?["questions"]?[0]?["id"]?.ToString() ?? "";
            // The answer exactly as the Codex question card now produces it.
            session.RespondPermission(req.RequestId, new JsonObject
            {
                ["behavior"] = "allow",
                ["updatedInput"] = new JsonObject { ["answers"] = new JsonObject { [id] = new JsonArray(JsonValue.Create("Blue")) } },
            }, req.ToolUseId);
        };
        session.MessageReceived += m =>
        {
            var type = m["type"]?.ToString();
            if (type == "assistant" && m["message"]?["content"] is JsonArray blocks)
                lock (replies) replies.AddRange(blocks.Select(b => b?["text"]?.ToString()).OfType<string>());
            if (type == "result") finished = true;
        };
        session.Start();
        session.SendUser(JsonValue.Create("Use your request_user_input tool exactly once to ask me which color I prefer, with the options " +
            "Red and Blue. After I answer, reply with only the color I picked, in capitals. Do not use any other tool."));
        PumpUntil(() => finished, 240_000);
        var text = string.Join(" ", replies);
        Console.WriteLine("Live Codex reply: " + text);
        Check("live Codex asks through request_user_input in VibeCode's Default mode", asked?.ToolName == "AskUserQuestion");
        Check("live Codex receives the question card's answer", text.Contains("BLUE", StringComparison.Ordinal));
    }
}
