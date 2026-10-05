using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using VibeCode.UI;

namespace VibeCode.Services;

/// <summary>User preferences, separate from credentials, transcripts and session-restoration bookkeeping.</summary>
public static class JarvisSettingsCatalog
{
    public static IReadOnlyList<string> Categories { get; } =
        ["General", "Appearance", "Notifications", "Bridge", "Usage", "Projects", "MCP servers", "Extensions", "Jarvis", "Privacy", "About"];

    private static readonly IReadOnlyDictionary<string, Setting> Entries = Build();
    private sealed record Setting(PropertyInfo Property, string Category);

    private static IReadOnlyDictionary<string, Setting> Build()
    {
        var result = new Dictionary<string, Setting>(StringComparer.OrdinalIgnoreCase);
        Add("General", "RunInBackground ContinueAfterLimitResets DefaultProvider DefaultMode DefaultModel DefaultEffort DefaultCodexModel DefaultCodexEffort DefaultKimiModel DefaultKimiEffort DefaultGrokModel DefaultGrokEffort DefaultGlmModel DefaultGlmEffort FastMode");
        Add("Appearance", "UiMode Borderless ThinkingOrbStyle CompactMode Backgrounds ActiveBackground RandomBackground BackgroundVisibility");
        Add("Notifications", "NotifyOnTurnEnd NotifyOnAwaitingInput NotificationSound");
        Add("Bridge", "BridgeOrchestratorCount BridgeOrchestratorWorkerCounts DualMonitorBridge DualMonitorDoubleSessions BridgeAgentLimit BridgeRealtimeSharing BridgePeerMessaging ShowAgentMessageDividers AgentSwarmsEnabled AgentSwarmsInBridge SwarmMaxWorkers AgentSupervisionEnabled SupervisionStaleSeconds SupervisionIdleSeconds SupervisionInterventionGraceSeconds SupervisionMaxTaskSeconds SupervisionMaxInterventions SupervisionMaxRestarts SupervisionLoopRepeatThreshold");
        Add("Usage", "TelemetryOnCompanionDisplay TelemetryLiveAnimation TelemetryWallRangeHours");
        Add("Projects", "ShowOnlyOwnedSessions HiddenProjects");
        Add("Extensions", "SecondBrainEnabled AgentMemoryEnabled AgentMemoryAutoRecall AgentMemoryAutoRemember AgentMemoryEndpoint SpotifyEnabled SpotifyClientId WeatherEnabled WeatherPlace WeatherLat WeatherLon WeatherCountryCode GroqSpeechEnabled PhoneEnabled PhoneBridgeEnabled PhoneBridgePort");
        Add("Jarvis", "JarvisProvider JarvisModel JarvisEffort JarvisVoiceEnabled JarvisVoiceId JarvisSpeechRate JarvisSpeechVolume");
        Add("Privacy", "HideEmails IsolateChatsByAccount GrokDeleteProxy");
        return result;

        void Add(string category, string names)
        {
            foreach (var name in names.Split(' '))
                result.Add(name, new(typeof(AppSettings).GetProperty(name)
                    ?? throw new InvalidOperationException("Unknown Jarvis setting: " + name), category));
        }
    }

    public static JsonObject Snapshot(AppSettings settings)
    {
        var values = new JsonArray();
        foreach (var (name, entry) in Entries)
        {
            var range = Range(name);
            var type = Nullable.GetUnderlyingType(entry.Property.PropertyType) ?? entry.Property.PropertyType;
            values.Add(new JsonObject
            {
                ["name"] = name, ["category"] = entry.Category,
                ["type"] = type == typeof(bool) ? "boolean" : type == typeof(int) || type == typeof(double) ? "number"
                    : type == typeof(string) ? "string" : "JSON array",
                ["value"] = name == nameof(AppSettings.GrokDeleteProxy)
                    ? JsonValue.Create(entry.Property.GetValue(settings) is null ? "default" : "[redacted]")
                    : JsonSerializer.SerializeToNode(entry.Property.GetValue(settings)),
                ["choices"] = new JsonArray(Choices(name).Select(choice => (JsonNode?)JsonValue.Create(choice)).ToArray()),
                ["minimum"] = range?.Minimum, ["maximum"] = range?.Maximum,
            });
        }
        return new()
        {
            ["preferences"] = values,
            ["categories"] = new JsonArray(Categories.Select(category => (JsonNode?)JsonValue.Create(category)).ToArray()),
            ["mcp_servers"] = new JsonArray(settings.McpServers.Select(server => (JsonNode?)DescribeServer(server)).ToArray()),
            ["models"] = new JsonObject(BridgeAgentConfigurationPolicy.Providers.Select(provider =>
                new KeyValuePair<string, JsonNode?>(provider, new JsonArray(BridgeAgentConfigurationPolicy.ModelsFor(provider)
                    .Select(model => (JsonNode?)new JsonObject
                    {
                        ["id"] = model.Value, ["label"] = model.Display,
                        ["efforts"] = new JsonArray(model.EffortLevels.Select(effort => (JsonNode?)JsonValue.Create(effort)).ToArray()),
                    }).ToArray())))),
        };
    }

    public static JsonObject DescribeServer(McpServerDefinition server) => new()
    {
        ["id"] = server.Id, ["name"] = server.Name, ["transport"] = server.Transport, ["enabled"] = server.Enabled,
        ["command"] = server.Command, ["argument_count"] = server.Arguments.Count,
        ["url"] = PublicUrl(server.Url),
        ["environment_variables"] = new JsonArray(server.Environment.Keys.Select(key => (JsonNode?)JsonValue.Create(key)).ToArray()),
        ["headers"] = new JsonArray(server.Headers.Keys.Select(key => (JsonNode?)JsonValue.Create(key)).ToArray()),
        ["bearerTokenEnvironmentVariable"] = server.BearerTokenEnvironmentVariable,
        ["startupTimeoutSeconds"] = server.StartupTimeoutSeconds, ["toolTimeoutSeconds"] = server.ToolTimeoutSeconds,
        ["useClaude"] = server.UseClaude, ["useCodex"] = server.UseCodex,
        ["useKimi"] = server.UseKimi, ["useGrok"] = server.UseGrok,
    };

    private static string PublicUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
        ? new UriBuilder(uri) { UserName = "", Password = "", Query = "", Fragment = "" }.Uri.AbsoluteUri : "";

    public static string Category(string name) => Entry(name).Category;
    public static bool HasSetting(string name) => Entries.ContainsKey(name);
    public static PropertyInfo Property(string name) => Entry(name).Property;
    private static Setting Entry(string name) => Entries.TryGetValue(name, out var entry) ? entry
        : throw new ArgumentException($"'{name}' is not an editable preference. Use a name from the settings catalog.");

    public static object? ParseValue(JarvisSettingChange change)
    {
        var entry = Entry(change.Name);
        var name = entry.Property.Name;
        var declared = entry.Property.PropertyType;
        var type = Nullable.GetUnderlyingType(declared) ?? declared;
        var text = change.Value.Trim();
        object? value;
        if (text is "" or "null" or "default" && (Nullable.GetUnderlyingType(declared) is not null
                || type == typeof(string) && name is not (nameof(AppSettings.UiMode) or nameof(AppSettings.DefaultMode)
                    or nameof(AppSettings.DefaultProvider) or nameof(AppSettings.JarvisProvider) or nameof(AppSettings.JarvisVoiceId)
                    or nameof(AppSettings.NotificationSound) or nameof(AppSettings.ThinkingOrbStyle) or nameof(AppSettings.AgentMemoryEndpoint))))
            value = null;
        else if (type == typeof(bool)) value = bool.TryParse(text, out var flag) ? flag : throw Invalid(name, "true or false");
        else if (type == typeof(int)) value = int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)
            ? integer : throw Invalid(name, "a whole number");
        else if (type == typeof(double)) value = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            && double.IsFinite(number) ? number : throw Invalid(name, "a finite number");
        else if (type == typeof(string)) value = text;
        else
        {
            try { value = JsonSerializer.Deserialize(text, declared) ?? throw Invalid(name, "a JSON array"); }
            catch (JsonException) { throw Invalid(name, "a JSON array"); }
        }
        if (value is string selected && Choices(name) is { Count: > 0 } choices)
            value = choices.FirstOrDefault(choice => choice.Equals(selected, StringComparison.OrdinalIgnoreCase))
                ?? throw Invalid(name, string.Join(", ", choices));
        if (value is not null && Range(name) is { } range && value is int or double)
        {
            var number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (number < range.Minimum || number > range.Maximum)
                throw Invalid(name, $"a value from {range.Minimum} to {range.Maximum}");
        }
        if (name is nameof(AppSettings.ActiveBackground) or nameof(AppSettings.Backgrounds) or nameof(AppSettings.HiddenProjects))
        {
            var paths = value is string selectedPath ? new[] { selectedPath } : value is IEnumerable<string> list ? list : [];
            foreach (var path in paths)
            {
                JarvisPathPolicy.Normalize(path);
                if (name != nameof(AppSettings.HiddenProjects) && (!File.Exists(path)
                    || Path.GetExtension(path).ToLowerInvariant() is not (".gif" or ".png" or ".jpg" or ".jpeg" or ".bmp" or ".webp")))
                    throw Invalid(name, "an existing image file");
            }
        }
        if (name == nameof(AppSettings.AgentMemoryEndpoint) && value is string endpoint
            && (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
            throw Invalid(name, "an absolute http or https URL");
        if (name == nameof(AppSettings.BridgeOrchestratorWorkerCounts) && value is int[] counts
            && (counts.Length > 4 || counts.Any(count => count is < 0 or > SwarmPolicy.HardMaxWorkers)))
            throw Invalid(name, "up to four worker counts from 0 to 16");
        return value;
    }

    private static ArgumentException Invalid(string name, string expected) => new($"{name} requires {expected}.");
    private static (double Minimum, double Maximum)? Range(string name) => name switch
    {
        nameof(AppSettings.BackgroundVisibility) or nameof(AppSettings.JarvisSpeechVolume) => (0, 100),
        nameof(AppSettings.JarvisSpeechRate) => (0.6, 1.5),
        nameof(AppSettings.BridgeAgentLimit) => (BridgeAgentPolicy.MinimumAgentLimit, BridgeAgentPolicy.MaximumAgentLimit),
        nameof(AppSettings.SwarmMaxWorkers) => (SwarmPolicy.MinMaxWorkers, SwarmPolicy.HardMaxWorkers),
        nameof(AppSettings.BridgeOrchestratorCount) => (1, 4),
        nameof(AppSettings.SupervisionStaleSeconds) or nameof(AppSettings.SupervisionIdleSeconds)
            or nameof(AppSettings.SupervisionInterventionGraceSeconds) => (1, 86_400),
        nameof(AppSettings.SupervisionMaxTaskSeconds) => (0, 86_400),
        nameof(AppSettings.SupervisionMaxInterventions) or nameof(AppSettings.SupervisionMaxRestarts) => (0, 100),
        nameof(AppSettings.SupervisionLoopRepeatThreshold) => (2, 100),
        nameof(AppSettings.TelemetryWallRangeHours) => (1, 720),
        nameof(AppSettings.PhoneBridgePort) => (0, 65535),
        nameof(AppSettings.WeatherLat) => (-90, 90), nameof(AppSettings.WeatherLon) => (-180, 180),
        _ => null,
    };

    private static IReadOnlyList<string> Choices(string name) => name switch
    {
        nameof(AppSettings.UiMode) => ["background", "cli"],
        nameof(AppSettings.DefaultProvider) or nameof(AppSettings.JarvisProvider) => ["claude", "codex", "kimi", "grok", "glm"],
        nameof(AppSettings.DefaultMode) => ["default", "auto", "plan", "acceptEdits", "bypassPermissions", "dontAsk"],
        nameof(AppSettings.ThinkingOrbStyle) => ThinkingOrbStyles.All.Select(orb => orb.Id).ToArray(),
        nameof(AppSettings.NotificationSound) => NotificationSounds.All.Select(sound => sound.Id).ToArray(),
        nameof(AppSettings.JarvisVoiceId) => JarvisSpeechService.GetVoices().Select(voice => voice.Id).ToArray(),
        nameof(AppSettings.JarvisEffort) or nameof(AppSettings.DefaultEffort) or nameof(AppSettings.DefaultCodexEffort)
            or nameof(AppSettings.DefaultGrokEffort) or nameof(AppSettings.DefaultGlmEffort)
            => ["none", "minimal", "low", "medium", "high", "xhigh", "max", "ultra", "ultracode"],
        nameof(AppSettings.DefaultKimiEffort) => ["on", "off", "low", "high", "max"],
        _ => [],
    };
}
