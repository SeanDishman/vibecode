using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using VibeCode.UI;

namespace VibeCode.Services;

/// <summary>Validated preference changes use the same merge-aware store and MCP catalog as Settings.</summary>
public static class JarvisSettingsActions
{
    private static readonly string[] McpFields =
        ["Name", "Transport", "Enabled", "UseClaude", "UseCodex", "UseKimi", "UseGrok", "Command", "Arguments", "Url",
         "Environment", "Headers", "BearerTokenEnvironmentVariable", "StartupTimeoutSeconds", "ToolTimeoutSeconds"];

    public static bool IsSettingsAction(string kind) => kind is
        "read_settings" or "open_settings" or "update_settings" or "configure_mcp" or "remove_mcp";

    public static void Validate(JarvisAction action)
    {
        switch (action.Kind)
        {
            case "read_settings":
            case "open_settings":
                if (action.Changes.Count > 0) throw new ArgumentException("Only update_settings can change preferences.");
                if (action.Target.Length > 0 && !JarvisSettingsCatalog.Categories.Contains(action.Target, StringComparer.OrdinalIgnoreCase)
                    && !(action.Kind == "read_settings" && JarvisSettingsCatalog.HasSetting(action.Target)))
                    throw new ArgumentException("Use a settings category from the catalog.");
                break;
            case "update_settings":
                if (action.Changes.Count is < 1 or > 20) throw new ArgumentException("Specify between 1 and 20 settings to change.");
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var change in action.Changes)
                {
                    if (!names.Add(change.Name)) throw new ArgumentException("Specify each setting only once.");
                    JarvisSettingsCatalog.ParseValue(change);
                    if (change.Name.Equals(nameof(AppSettings.SecondBrainEnabled), StringComparison.OrdinalIgnoreCase)
                        && !action.RequestQuote.Contains("Second Brain", StringComparison.OrdinalIgnoreCase)
                        && !action.RequestQuote.Contains(nameof(AppSettings.SecondBrainEnabled), StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException("Changing Second Brain requires a request that specifically names Second Brain.");
                }
                break;
            case "configure_mcp":
                if (string.IsNullOrWhiteSpace(action.Target) || action.McpConfiguration is not { Count: > 0 } config)
                    throw new ArgumentException("Specify an MCP server name and the fields to configure.");
                if (config.Any(field => !McpFields.Contains(field.Key, StringComparer.OrdinalIgnoreCase))
                    || config.Select(field => field.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() != config.Count)
                    throw new ArgumentException("The MCP configuration contained unsupported or duplicate fields.");
                break;
            case "remove_mcp":
                if (string.IsNullOrWhiteSpace(action.Target) || action.McpConfiguration is not null)
                    throw new ArgumentException("Specify only the name or ID of the MCP server to remove.");
                break;
            default: throw new ArgumentException("Unsupported settings action.");
        }
    }

    public static JarvisActionReceipt Execute(JarvisAction action, CancellationToken cancellationToken,
        Action<string>? openSettings = null, Func<Exception?>? save = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validate(action);
        var settings = AppSettings.Current;
        if (action.Kind == "open_settings")
        {
            if (openSettings is null) throw new InvalidOperationException("The settings window is unavailable.");
            var category = action.Target.Length == 0 ? "General" : action.Target;
            openSettings(category);
            return Receipt($"Opened VibeCode settings > {category}.");
        }
        if (action.Kind == "read_settings")
        {
            var snapshot = JarvisSettingsCatalog.Snapshot(settings);
            if (action.Target.Length > 0)
                snapshot["preferences"] = new JsonArray(snapshot["preferences"]!.AsArray()
                    .Where(node => node!["category"]!.ToString().Equals(action.Target, StringComparison.OrdinalIgnoreCase)
                        || node["name"]!.ToString().Equals(action.Target, StringComparison.OrdinalIgnoreCase))
                    .Select(node => node!.DeepClone()).ToArray());
            return Receipt("Current VibeCode settings" + (action.Target.Length > 0 ? " > " + action.Target : "")
                + ":\n" + string.Join("\n", snapshot["preferences"]!.AsArray().Select(node =>
                    "- " + node!["name"] + " = " + (node["value"]?.ToJsonString() ?? "default")))
                + (action.Target is "" or "MCP servers" ? "\n\nMCP servers:\n" + (settings.McpServers.Count == 0 ? "None configured."
                    : string.Join("\n", settings.McpServers.Select(server => $"- {server.Name}: {server.Transport}, {(server.Enabled ? "enabled" : "disabled")}, {server.TargetsSummary}"))) : ""));
        }
        save ??= () => settings.TrySave(defaultModeIsDeliberate: action.Changes.Any(change =>
            change.Name.Equals(nameof(AppSettings.DefaultMode), StringComparison.OrdinalIgnoreCase)));
        if (action.Kind == "update_settings") return UpdatePreferences();
        return UpdateMcp();

        JarvisActionReceipt UpdatePreferences()
        {
            var changes = action.Changes.Select(change =>
                (Property: JarvisSettingsCatalog.Property(change.Name), Value: JarvisSettingsCatalog.ParseValue(change))).ToArray();
            // Validate the complete proposed combination before touching the shared instance.
            var candidate = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
            foreach (var change in changes) change.Property.SetValue(candidate, change.Value);
            if (changes.Any(change => change.Property.Name is nameof(AppSettings.JarvisProvider) or nameof(AppSettings.JarvisModel)))
            {
                JarvisDialogueService.ValidateSelection(new(candidate.JarvisProvider, candidate.JarvisModel, candidate.JarvisEffort));
                RequireCompatibleModel(candidate.JarvisModel, candidate.JarvisProvider);
            }
            foreach (var (property, provider) in new[]
            {
                (nameof(AppSettings.DefaultModel), "claude"), (nameof(AppSettings.DefaultCodexModel), "codex"),
                (nameof(AppSettings.DefaultKimiModel), "kimi"), (nameof(AppSettings.DefaultGrokModel), "grok"),
                (nameof(AppSettings.DefaultGlmModel), "glm"),
            })
                if (changes.Any(change => change.Property.Name == property))
                    RequireCompatibleModel((string?)typeof(AppSettings).GetProperty(property)!.GetValue(candidate), provider);
            var previous = changes.Select(change => (change.Property, Value: change.Property.GetValue(settings))).ToArray();
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                foreach (var change in changes) change.Property.SetValue(settings, change.Value);
                if (save() is { } error) throw new IOException("The settings change could not be saved. " + error.Message, error);
            }
            catch
            {
                foreach (var original in previous) original.Property.SetValue(settings, original.Value);
                throw;
            }
            var saved = ReadSavedSettings();
            foreach (var change in changes)
                if (!Equivalent(change.Property.GetValue(settings), change.Property.GetValue(saved)))
                    throw new IOException($"Could not verify the saved value of {change.Property.Name}.");
            return Receipt("Updated VibeCode settings:\n" + string.Join("\n", changes.Select(change =>
                "- " + change.Property.Name + " = " + (change.Property.Name == nameof(AppSettings.GrokDeleteProxy) ? "[redacted]"
                    : JsonSerializer.Serialize(change.Property.GetValue(saved))))));
        }

        JarvisActionReceipt UpdateMcp()
        {
            var matches = settings.McpServers.Where(server => server.Id.Equals(action.Target, StringComparison.OrdinalIgnoreCase)
                || server.Name.Equals(action.Target, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length > 1) throw new ArgumentException("More than one MCP server matches. Use its ID from settings.");
            var existing = matches.SingleOrDefault();
            if (existing is null && action.Kind == "remove_mcp")
                return Receipt($"No MCP server named '{action.Target}' is configured.");
            var previous = settings.McpServers.ToList();
            McpServerDefinition? definition = null;
            IReadOnlyList<McpValidationMessage> messages = [];
            if (action.Kind == "configure_mcp")
            {
                if (existing is null && settings.McpServers.Count >= McpCatalog.MaxManagedServers)
                    throw new InvalidOperationException("The MCP server catalog is full.");
                definition = existing?.Clone() ?? new McpServerDefinition { Name = action.Target };
                foreach (var (key, node) in action.McpConfiguration!)
                {
                    var property = typeof(McpServerDefinition).GetProperty(McpFields.Single(field => field.Equals(key, StringComparison.OrdinalIgnoreCase)))!;
                    if (node is null) throw new ArgumentException("MCP fields cannot be null. Use an empty string, array or object to clear a field.");
                    property.SetValue(definition, node.Deserialize(property.PropertyType));
                }
                messages = McpCatalog.Validate(definition, settings.McpServers);
                if (messages.Any(message => message.Severity == McpValidationSeverity.Error))
                    throw new ArgumentException("The MCP configuration is invalid: "
                        + string.Join(" ", messages.Where(message => message.Severity == McpValidationSeverity.Error).Select(message => message.Message)));
            }
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (existing is not null) settings.McpServers.Remove(existing);
                if (definition is not null) settings.McpServers.Add(definition);
                if (save() is { } error) throw new IOException("The MCP change could not be saved. " + error.Message, error);
            }
            catch { settings.McpServers = previous; throw; }
            var saved = ReadSavedSettings();
            if (definition is not null)
            {
                if (saved.McpServers.SingleOrDefault(server => server.Id == definition.Id) is not { } persisted
                    || !Equivalent(definition, persisted)) throw new IOException("Could not verify the saved MCP configuration.");
            }
            else if (saved.McpServers.Any(server => server.Id == existing!.Id))
                throw new IOException("Could not verify removal of the MCP definition.");
            var message = definition is null ? $"Removed MCP definition '{existing!.Name}'."
                : $"{(existing is null ? "Added" : "Updated")} MCP server '{definition.Name}'. "
                    + "The configuration applies when selected chats next start or reconnect.";
            if (messages.Any(validation => validation.Severity == McpValidationSeverity.Warning))
                message += "\n" + string.Join("\n", messages.Where(validation => validation.Severity == McpValidationSeverity.Warning).Select(validation => validation.Message));
            return Receipt(message);
        }
        JarvisActionReceipt Receipt(string message) => new(action.Kind, "", message);
    }

    private static AppSettings ReadSavedSettings() => JsonSerializer.Deserialize<AppSettings>(
        File.ReadAllText(Path.Combine(AppSettings.Dir, "settings.json")))
        ?? throw new IOException("The saved settings could not be verified.");
    private static bool Equivalent(object? left, object? right) => JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);

    private static void RequireCompatibleModel(string? model, string provider)
    {
        if (string.IsNullOrWhiteSpace(model) || model == "default") return;
        if (!ProviderModelCatalog.ModelBelongsTo(model, provider))
            throw new InvalidOperationException($"'{model}' is not a compatible model for {provider}.");
        var knownOwners = BridgeAgentConfigurationPolicy.Providers.Where(owner => ProviderModelCatalog.For(owner)
            .Any(choice => choice.Value.Equals(model, StringComparison.OrdinalIgnoreCase)
                || string.Equals(choice.ResolvedModel, model, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (knownOwners.Length > 0 && !knownOwners.Contains(provider, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"'{model}' belongs to {string.Join(" or ", knownOwners)}. Choose a {provider} model or reset the model to default.");
    }
}
