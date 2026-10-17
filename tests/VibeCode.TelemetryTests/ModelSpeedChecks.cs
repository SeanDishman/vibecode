using System.Windows.Media;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    static Dictionary<string, (ModelSpeed? Speed, DateTime At)> SpeedCache =>
        (Dictionary<string, (ModelSpeed? Speed, DateTime At)>)typeof(ModelSpeedService).GetField("_speeds", Flags)!.GetValue(ModelSpeedService.Instance)!;

    static int SpeedSubscribers => ((Delegate?)typeof(ModelSpeedService).GetField("Updated", Flags)!.GetValue(ModelSpeedService.Instance))?.GetInvocationList().Length ?? 0;

    static void SeedSpeedFixtures()
    {
        // The production cache is prefilled in this isolated process, so native captures make no network requests.
        SpeedCache["openai/gpt-6.1"] = (new ModelSpeed(97.4, 8.3), DateTime.UtcNow);
        SpeedCache["anthropic/claude-opus-5"] = (new ModelSpeed(56.8, 2.4), DateTime.UtcNow);
    }

    static void VerifyModelSpeeds()
    {
        static ModelUsage Model(string model, string provider, double tokens) => new()
        { Model = model, Provider = provider, Display = model, Color = Colors.CornflowerBlue, Input = tokens };
        var models = new[] { Model("gpt-6.1", "codex", 20), Model("claude-opus-5", "claude", 30),
            Model("openai/gpt-6.1", "codex", 15), Model("old-unused-model", "codex", 0) };
        var service = ModelSpeedService.Instance;
        var rows = WorkflowModelSpeeds.Build(models, service.ById, service.FetchedAt);
        Check("OpenRouter rows follow actual token usage and merge aliases", rows.Length == 2
            && rows[0].OpenRouterId == "openai/gpt-6.1" && rows[1].OpenRouterId == "anthropic/claude-opus-5");
        Check("Performance rows keep throughput and latency units separate", rows[0].Throughput == "97" && rows[0].Latency == "8.3s" && rows[0].HasData);
        Check("Public performance has a source and check time", WorkflowModelSpeeds.Caption(rows).Contains("30-min provider stats")
            && WorkflowModelSpeeds.Caption(rows).Contains("checked") && rows.All(r => r.Detail.Contains("OpenRouter")));
        var loading = WorkflowModelSpeeds.Build(models, _ => null, _ => null);
        Check("Unfetched model stats show loading without fabricated values", loading.All(r => r.Throughput == "—" && r.Latency == "—" && !r.HasData)
            && WorkflowModelSpeeds.Caption(loading).Contains("Fetching"));
        var unavailable = WorkflowModelSpeeds.Build(models, _ => null, _ => DateTime.UtcNow);
        Check("Failed lookups finish loading and report unavailable stats", WorkflowModelSpeeds.Caption(unavailable) == "No public readings available."
            && unavailable.All(r => r.Detail == "No public stats available"));
        var invalid = WorkflowModelSpeeds.Build(models, _ => new ModelSpeed(double.NaN, -1), _ => DateTime.UtcNow);
        Check("Non-finite or negative provider readings cannot reach the UI", invalid.All(r => !r.HasData && r.Throughput == "—"));
        var unknown = WorkflowModelSpeeds.Build([Model("unknown", "codex", 1)], _ => throw new Exception("Unexpected lookup"), _ => null);
        Check("Unknown model labels never generate a provider lookup", unknown.Single().OpenRouterId is null && unknown.Single().Detail == "Not listed on OpenRouter");
        Check("Empty hourly activity has a useful empty state", WorkflowModelSpeeds.Caption([]) == "Stats appear when a model uses tokens.");
        var many = WorkflowModelSpeeds.Build(Enumerable.Range(0, 12).Select(i => Model("model-" + i, "codex", i + 1)), _ => null, _ => null);
        Check("The model board is bounded and prioritizes the most-used models", many.Length == 8 && many[0].Model == "model-11" && many[^1].Model == "model-4");
    }
}
