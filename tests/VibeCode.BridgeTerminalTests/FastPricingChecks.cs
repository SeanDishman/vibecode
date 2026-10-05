using System.Text.Json.Nodes;
using VibeCode.Protocol;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    private static void VerifyFastPricing()
    {
        void Near(string label, double actual, double expected) => Check(label, Math.Abs(actual - expected) < 1e-9);
        double Price(string model, string? tier = null) => ModelPricing.TurnCost(model, 1000, 200, 300, 100, tier);
        Near("Opus 5.5 standard includes its discounted cache reads", Price("claude-opus-5-5"), 0.00706);
        Near("Opus 5.5 fast doubles input, output and both cache tiers", Price("claude-opus-5-5", "fast"), 0.01412);
        foreach (var model in new[] { "claude-opus-5", "claude-opus-4-8", "gpt-6-astra", "gpt-6.1-sol", "gpt-6-sol", "gpt-6-luna", "gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna" })
            Near(model + " fast is twice its own standard rate", Price(model, "fast"), Price(model) * 2);
        Near("priority and fast aliases price identically", Price("gpt-6.1-sol", "priority"), Price("gpt-6.1-sol", "fast"));
        Near("GPT-5.5 retains its documented API fast exception", Price("gpt-5.5", "fast"), Price("gpt-5.5") * 2.5);
        Near("Astra ultrafast metadata uses its own dollar rate", Price("gpt-6-astra", "ultrafast"), Price("gpt-6-astra") * 6);
        foreach (var model in new[] { "claude-sonnet-5-5", "claude-opus-4-6", "claude-opus-4-7", "kimi-k2.7-code-highspeed", "zai-org/glm-5.2-fast" })
            Near(model + " receives no unrelated fast surcharge", Price(model, "fast"), Price(model));
        Near("Claude one-hour cache writes use the higher write rate", ModelPricing.TurnCost("claude-opus-5-5", 0, 1000, 0, 0, "fast", cacheWrite1h: 1000), 0.016);
        Near("long cache writes cannot exceed the write total", ModelPricing.TurnCost("claude-opus-5-5", 0, 1000, 0, 0, "fast", cacheWrite1h: 5000), 0.016);
        Near("272K GPT input remains short context", ModelPricing.TurnCost("gpt-6-sol", 272000, 0, 0, 1000, "fast", 272000), 1.108);
        Near("over 272K applies long-context and fast rates together", ModelPricing.TurnCost("gpt-6-sol", 272001, 0, 0, 1000, "fast", 272001), 2.206008);
        Near("aggregate turn totals do not imply one long request", ModelPricing.TurnCost("gpt-6-sol", 500000, 0, 0, 1000, "fast"), 2.02);

        VerifyCodexRequestPricing(Near);
        VerifyChatPriceAccounting(Near);
        VerifyLivePriceAccounting(Near);
    }

    private static JsonObject PriceUsage(double input = 1000, double read = 0, double output = 100, string? speed = null) => new()
    {
        ["input_tokens"] = input, ["cache_read_input_tokens"] = read, ["output_tokens"] = output, ["speed"] = speed,
    };

    private static void VerifyCodexRequestPricing(Action<string, double, double> near)
    {
        using var session = new CodexSession(new() { Cwd = _root, Model = "gpt-6-luna", FastMode = true });
        Property(session, "SessionId", "price-root");
        Call(session, "RememberTurnPricing", new JsonObject { ["threadId"] = "price-root", ["model"] = "gpt-6-luna", ["serviceTier"] = "priority" });
        var messages = new List<JsonNode>();
        session.MessageReceived += m => messages.Add(m.DeepClone());
        void Request(double input, double totalInput, double totalOutput, string? tier = null, string thread = "price-root", string? model = null)
        {
            var last = new JsonObject { ["inputTokens"] = input, ["outputTokens"] = 100, ["cachedInputTokens"] = 0 };
            Call(session, "CaptureUsage", new JsonObject { ["model"] = model, ["serviceTier"] = tier,
                ["tokenUsage"] = new JsonObject { ["last"] = last,
                    ["total"] = new JsonObject { ["inputTokens"] = totalInput, ["outputTokens"] = totalOutput, ["cachedInputTokens"] = 0 } } }, thread);
        }
        double Cost() => messages.Last()["usage"]!["estimated_cost_usd"]!.GetValue<double>();
        Request(150000, 150000, 100);
        near("Codex adapter prices the sent fast request", Cost(), 0.0301);
        Request(150000, 150000, 100);
        near("repeated cumulative usage does not duplicate money", Cost(), 0.0301);
        session.SetFastModeAsync(false).GetAwaiter().GetResult();
        session.SetModelAsync("gpt-6-astra").GetAwaiter().GetResult();
        Request(150000, 300000, 200);
        near("mid-turn picker changes do not reprice a dispatched request", Cost(), 0.0602);
        Request(150000, 450000, 300, "default");
        near("reported standard fallback overrides requested fast", Cost(), 0.07525);
        Request(300000, 750000, 400, "priority");
        near("request-size surcharge applies only to the long request", Cost(), 0.1954);
        Request(1000, 1000, 100, "default", "child", "gpt-6-sol");
        near("reported child model and tier use their own rates", Cost(), 0.1984);
        var usage = messages.Last()["usage"]!.DeepClone();
        var planner = Activator.CreateInstance(typeof(ModelPricing).Assembly.GetType("VibeCode.Services.BridgeSuggestionUsage")!, Flags, null,
            ["codex", "gpt-6-luna", "pricing-fixture"], null)!;
        Call(planner, "Record", new JsonObject { ["usage"] = usage }, "price-planner", new JsonArray());
        near("background planning preserves adapter request pricing", UsageLog.Instance.Entries().Last().CostUsd, 0.1984);
    }

    private static void VerifyChatPriceAccounting(Action<string, double, double> near)
    {
        var (_, team) = Team("pricing-chats", 1);
        var codex = team[0]; codex.Status = "idle"; codex.Model = "gpt-6-luna"; codex.FastMode = true;
        Check("pricing fixture sends through normal dispatch", codex.Send("Calculate a price."));
        PumpUntil(() => Session(codex).Sent.Count == 1);
        var usage = PriceUsage();
        Call(codex, "ApplySystem", new JsonObject { ["subtype"] = "usage_update", ["usage"] = usage.DeepClone() }, null);
        near("chat live estimate uses the dispatched fast mode", ((ValueTuple<double, bool>)Property(codex, "DisplayedCost")!).Item1, 0.0003);
        codex.FastMode = false; codex.Model = "gpt-6-astra";
        near("changing mode and model leaves current chat cost alone", ((ValueTuple<double, bool>)Property(codex, "DisplayedCost")!).Item1, 0.0003);
        Call(codex, "ApplyResult", new JsonObject { ["usage"] = usage.DeepClone(), ["total_cost_usd"] = 0 });
        near("zero subscription bill falls back to the fast estimate", UsageLog.Instance.Entries().Last().CostUsd, 0.0003);
        Check("committed turn retains its actual model", UsageLog.Instance.Entries().Last().Model == "gpt-6-luna");
        near("live usage is not counted again after completion", ((ValueTuple<double, bool>)Property(codex, "DisplayedCost")!).Item1, 0.0003);

        var claude = new ChatViewModel(_root, provider: "claude") { Model = "claude-opus-5-5", FastMode = true };
        Chats.Add(claude); Call(claude, "BeginTurnPricing");
        var fastUsage = PriceUsage(speed: "fast");
        fastUsage["cache_creation_input_tokens"] = 1000;
        fastUsage["cache_creation"] = new JsonObject { ["ephemeral_1h_input_tokens"] = 1000 };
        var first = new JsonObject { ["id"] = "msg-fast", ["model"] = "claude-opus-5-5", ["usage"] = fastUsage };
        Call(claude, "CaptureClaudeAssistantUsage", first, true);
        Call(claude, "CaptureClaudeAssistantUsage", first, true);
        var standard = new JsonObject { ["id"] = "msg-standard", ["model"] = "claude-sonnet-5-5", ["usage"] = PriceUsage(speed: "standard") };
        Call(claude, "CaptureClaudeAssistantUsage", standard, false);
        near("Claude de-duplicates messages and prices mixed speeds, models and cache TTL", ((ValueTuple<double, bool>)Property(claude, "DisplayedCost")!).Item1, 0.031);
        var total = PriceUsage(input: 2000, output: 200); total["cache_creation_input_tokens"] = 1000;
        Call(claude, "ApplyResult", new JsonObject { ["usage"] = total, ["total_cost_usd"] = 0 });
        near("Claude completion preserves per-message modifiers", UsageLog.Instance.Entries().Last().CostUsd, 0.031);
        Call(claude, "ApplyResult", new JsonObject { ["usage"] = PriceUsage(), ["total_cost_usd"] = 0.04 });
        near("provider-reported spend is not multiplied again", UsageLog.Instance.Entries().Last().CostUsd, 0.04);
        Check("provider-reported Claude cost is marked as reported", UsageLog.Instance.Entries().Last().CostReported);
        Call(claude, "ApplyResult", new JsonObject { ["usage"] = PriceUsage(), ["total_cost_usd"] = 0.07 });
        near("cumulative provider totals log only the new spend", UsageLog.Instance.Entries().Last().CostUsd, 0.03);

        var grok = new ChatViewModel(_root, provider: "grok") { Model = "grok-4.7-fast", FastMode = true };
        Chats.Add(grok); Call(grok, "BeginTurnPricing");
        Call(grok, "ApplyResult", new JsonObject { ["usage"] = PriceUsage(), ["total_cost_usd"] = 0.123 });
        near("Grok fast keeps the provider's actual cost", UsageLog.Instance.Entries().Last().CostUsd, 0.123);
        Call(grok, "ApplyResult", new JsonObject { ["usage"] = PriceUsage() });
        Check("missing Grok prices are not invented or marked reported", UsageLog.Instance.Entries().Last() is { CostUsd: 0, CostReported: false });
    }

    private static void VerifyLivePriceAccounting(Action<string, double, double> near)
    {
        var telemetry = LiveTurnTelemetry.Instance;
        using var watcher = telemetry.Watch();
        var owner = new object();
        telemetry.Report(owner, "codex", "gpt-6-luna", 1000, 0, 0, 100, "price-flow", _root, 0.0003);
        telemetry.Report(owner, "codex", "gpt-6-luna", 2000, 0, 0, 200, "price-flow", _root, 0.00045);
        telemetry.Report(owner, "codex", "gpt-6-luna", 2000, 0, 0, 200, "price-flow", _root, 0.00045);
        near("live dashboard uses request-aware costs", telemetry.Merge([]).Single().CostUsd, 0.00045);
        near("live flow sums price deltas without repeated snapshots", telemetry.Flow().Where(e => e.SessionId == "price-flow").Sum(e => e.CostUsd), 0.00045);
        telemetry.Report(owner, "codex", "gpt-6-luna", 2000, 0, 0, 200, "price-flow", _root, 0.0004);
        near("same-token price corrections reach the flow", telemetry.Flow().Where(e => e.SessionId == "price-flow").Sum(e => e.CostUsd), 0.0004);
        telemetry.Clear(owner);
        telemetry.Report(owner, "grok", "grok-4.7-fast", 1000, 0, 0, 100, "price-grok", _root);
        near("live Grok cannot borrow another provider's fallback price", telemetry.Merge([]).Single().CostUsd, 0);
        telemetry.Clear(owner);
    }
}
