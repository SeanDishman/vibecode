using System.Globalization;
using System.Text.Json.Nodes;

namespace VibeCode.Services;

/// <summary>Facts from the running host, rebuilt at the model boundary instead of embedded in a prompt.</summary>
public static class JarvisAppKnowledge
{
    public static JsonObject Snapshot()
    {
        var now = DateTimeOffset.Now;
        return new()
        {
            ["name"] = "VibeCode",
            ["running_version"] = AppVersion.Current.IsKnown ? AppVersion.Current.ToString() : null,
            ["version_source"] = "Running application assembly; same source as the About section in Settings",
            // No release feed is consulted by this snapshot. Knowing the installed version does not establish
            // that it is the newest published release, or that a newer executable on disk is already running.
            ["latest_release_version"] = null,
            ["update_status"] = "not_checked",
            ["local_time"] = now.ToString("O", CultureInfo.InvariantCulture),
            ["utc_time"] = now.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            ["time_zone"] = TimeZoneInfo.Local.Id,
            ["supported_actions"] = new JsonArray(JarvisPlanParser.CreateOutputSchema()["properties"]!["actions"]!["items"]!["anyOf"]!
                .AsArray().SelectMany(shape => shape!["properties"]!["kind"]!["enum"]!.AsArray())
                .Select(kind => kind!.GetValue<string>()).Distinct(StringComparer.Ordinal)
                .Select(kind => (JsonNode?)JsonValue.Create(kind)).ToArray()),
        };
    }
}
