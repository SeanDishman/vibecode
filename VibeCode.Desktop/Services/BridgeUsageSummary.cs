using System.Text.Json.Serialization;

namespace VibeCode.Services;

/// <summary>Usage of temporary planning sessions retained with the bridge after their processes leave.</summary>
public sealed record BridgeUsageSummary(double Read = 0, double Write = 0, double Cost = 0, bool Estimated = false)
{
    [JsonIgnore] public bool HasUsage => Read + Write > 0 || Cost > 0;
    public static BridgeUsageSummary operator +(BridgeUsageSummary left, BridgeUsageSummary right) =>
        new(left.Read + right.Read, left.Write + right.Write, left.Cost + right.Cost, left.Estimated || right.Estimated);
}
