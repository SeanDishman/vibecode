using System.Windows.Media;
using VibeCode.Services;

namespace VibeCode.UI;

/// <summary>Public model performance beside, but never counted as, this machine's token usage.</summary>
public sealed record WorkflowModelSpeedRow(string Model, string Display, string Provider, string? OpenRouterId,
    string Throughput, string Latency, string Detail, DateTime? CheckedAt, bool HasData)
{
    public Brush Swatch => UsagePalette.BrushFor(Model);
}

public static class WorkflowModelSpeeds
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);
    public const int MaxRows = 8;

    public static WorkflowModelSpeedRow[] Build(IEnumerable<ModelUsage> models,
        Func<string, ModelSpeed?> reading, Func<string, DateTime?> checkedAt)
    {
        return models.Where(m => double.IsFinite(m.Total) && m.Total > 0)
            .Select(m => (Usage: m, Id: Id(m)))
            .GroupBy(m => m.Id ?? m.Usage.Provider + ":" + m.Usage.Model, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Sum(m => m.Usage.Total)).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Take(MaxRows).Select(g =>
            {
                var (model, id) = g.First();
                var speed = id is null ? null : reading(id);
                var at = id is null ? null : checkedAt(id);
                var valid = speed is { } s && double.IsFinite(s.TokensPerSecond) && s.TokensPerSecond > 0
                    && double.IsFinite(s.LatencySeconds) && s.LatencySeconds > 0;
                var detail = id is null ? "Not listed on OpenRouter"
                    : at is null ? "Fetching public model stats"
                    : valid ? $"OpenRouter provider traffic · last checked {at.Value.ToLocalTime():HH:mm}"
                    : "No public stats available";
                return new WorkflowModelSpeedRow(model.Model, model.Display, ProviderModelCatalog.DisplayName(model.Provider), id,
                    valid ? speed!.Value.TokensPerSecond.ToString("0") : "—",
                    valid ? $"{speed!.Value.LatencySeconds:0.0}s" : "—", detail, at, valid);
            }).ToArray();
    }

    private static string? Id(ModelUsage model)
    {
        if (string.IsNullOrWhiteSpace(model.Model) || model.Model.ToLowerInvariant() is "unknown" or "auto" or "default") return null;
        return ModelSpeedService.OpenRouterId(new ModelChoice { Value = model.Model, Display = model.Display, Provider = model.Provider });
    }

    public static string Caption(IReadOnlyList<WorkflowModelSpeedRow> rows)
    {
        if (rows.Count == 0) return "Stats appear when a model uses tokens.";
        if (!rows.Any(r => r.HasData)) return rows.Any(r => r.OpenRouterId is not null && r.CheckedAt is null)
            ? "Fetching public model stats…" : "No public readings available.";
        var oldest = rows.Where(r => r.HasData).Select(r => r.CheckedAt).Min();
        return "30-min provider stats" + (oldest is { } at ? $" · checked {at.ToLocalTime():HH:mm}" : "");
    }
}
