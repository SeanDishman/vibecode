using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace VibeCode.Services;

/// <summary>
/// One VibeCode-owned MCP definition. Provider configuration formats are deliberately kept out of this model;
/// <see cref="McpCatalog"/> projects it to Claude JSON, Codex TOML overrides, or ACP at the process boundary.
/// </summary>
public sealed class McpServerDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Transport { get; set; } = McpCatalog.StdioTransport;
    public bool Enabled { get; set; } = true;
    public bool UseClaude { get; set; } = true;
    public bool UseCodex { get; set; } = true;
    public bool UseKimi { get; set; } = true;
    public bool UseGrok { get; set; } = true;
    public string Command { get; set; } = "";
    public List<string> Arguments { get; set; } = new();
    public string Url { get; set; } = "";
    public Dictionary<string, string> Environment { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? BearerTokenEnvironmentVariable { get; set; }
    public int StartupTimeoutSeconds { get; set; } = McpCatalog.DefaultStartupTimeoutSeconds;
    public int ToolTimeoutSeconds { get; set; } = McpCatalog.DefaultToolTimeoutSeconds;

    [JsonIgnore] public bool IsStdio => Transport == McpCatalog.StdioTransport;
    [JsonIgnore] public bool IsSse => Transport == McpCatalog.SseTransport;
    [JsonIgnore] public string TransportLabel => IsStdio ? "Local · stdio" : IsSse ? "Remote · legacy SSE" : "Remote · Streamable HTTP";
    [JsonIgnore] public string EndpointSummary => IsStdio
        ? (Arguments?.Count ?? 0) == 0
            ? Command
            : $"{Command} | {Arguments?.Count ?? 0} argument{((Arguments?.Count ?? 0) == 1 ? "" : "s")}"
        : Url;
    [JsonIgnore] public string RuntimeName => McpCatalog.RuntimeServerName(this);
    [JsonIgnore] public string RuntimeNameLabel => "Runtime: " + RuntimeName;
    [JsonIgnore] public string TargetsSummary
    {
        get
        {
            var targets = new List<string>();
            if (UseClaude) targets.Add("Claude");
            if (UseCodex && !IsSse) targets.Add("Codex");
            if (UseKimi) targets.Add("Kimi");
            if (UseGrok) targets.Add("Grok");
            return targets.Count == 0 ? "No compatible CLI selected" : string.Join(" · ", targets);
        }
    }
    [JsonIgnore] public string CompatibilityNote => IsSse
        ? "Legacy SSE is sent to Claude, Kimi, and Grok; current Codex releases do not support it."
        : "Applied to each selected CLI when a chat next starts or reconnects.";

    public McpServerDefinition Clone() => new()
    {
        Id = Id,
        Name = Name,
        Transport = Transport,
        Enabled = Enabled,
        UseClaude = UseClaude,
        UseCodex = UseCodex,
        UseKimi = UseKimi,
        UseGrok = UseGrok,
        Command = Command,
        Arguments = new List<string>(Arguments ?? new List<string>()),
        Url = Url,
        Environment = new Dictionary<string, string>(Environment ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase),
        Headers = new Dictionary<string, string>(Headers ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase),
        BearerTokenEnvironmentVariable = BearerTokenEnvironmentVariable,
        StartupTimeoutSeconds = StartupTimeoutSeconds,
        ToolTimeoutSeconds = ToolTimeoutSeconds,
    };
}

public enum McpValidationSeverity { Warning, Error }

public sealed record McpValidationMessage(McpValidationSeverity Severity, string Message);

public sealed class CodexMcpProjection
{
    public List<string> ConfigOverrides { get; } = new();
    public Dictionary<string, string> Environment { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Canonical MCP catalog plus loss-aware adapters for the four CLI integration surfaces used by VibeCode.
/// This is a configuration adapter, not an MCP proxy: every CLI remains the MCP client and owns tool approvals.
/// </summary>
public static partial class McpCatalog
{
    public const string StdioTransport = "stdio";
    public const string HttpTransport = "http";
    public const string SseTransport = "sse";
    public const int DefaultStartupTimeoutSeconds = 30;
    public const int DefaultToolTimeoutSeconds = 60;
    public const int MinTimeoutSeconds = 1;
    public const int MaxTimeoutSeconds = 86_400;
    public const int MaxManagedServers = 64;
    public const int MaxCodexProjectionCharacters = 24_000;
    public const int MaxCodexProjectionEnvironmentCharacters = 24_000;

    // ClaudeSession removes these inherited values so a machine-level API key or endpoint cannot silently replace
    // VibeCode's selected subscription account. A Claude-targeted MCP definition therefore cannot use one of them
    // as a parent-process ${VAR} reference (literal values inside a server's own env map remain valid).
    internal static IReadOnlyList<string> ClaudeSuppressedEnvironmentVariables { get; } = Array.AsReadOnly(new[]
    {
        "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL", "ANTHROPIC_CUSTOM_HEADERS",
        "CLAUDE_CODE_OAUTH_TOKEN",
        "CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT",
    });

    [GeneratedRegex("^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ServerNamePattern();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex EnvironmentNamePattern();

    [GeneratedRegex("^[A-Za-z0-9!#$%&'*+.^_`|~-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex HeaderNamePattern();

    [GeneratedRegex(@"\$\{(?<name>[A-Za-z_][A-Za-z0-9_]*)(?::-(?<fallback>[^}]*))?\}", RegexOptions.CultureInvariant)]
    private static partial Regex EnvironmentTemplatePattern();

    public static bool NormalizeDefinitions(List<McpServerDefinition> definitions)
    {
        var changed = definitions.RemoveAll(definition => definition is null) > 0;
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in definitions)
        {
            var normalizedId = definition.Id?.Trim() ?? "";
            if (normalizedId.Length == 0 || !ids.Add(normalizedId))
            {
                definition.Id = Guid.NewGuid().ToString("N");
                ids.Add(definition.Id);
                changed = true;
            }
            else if (definition.Id != normalizedId)
            {
                definition.Id = normalizedId;
                changed = true;
            }

            var name = (definition.Name ?? "").Trim();
            var command = (definition.Command ?? "").Trim();
            var url = (definition.Url ?? "").Trim();
            if (definition.Name != name) { definition.Name = name; changed = true; }
            if (definition.Command != command) { definition.Command = command; changed = true; }
            if (definition.Url != url) { definition.Url = url; changed = true; }
            var bearer = definition.BearerTokenEnvironmentVariable?.Trim();
            if (bearer?.Length == 0) bearer = null;
            if (definition.BearerTokenEnvironmentVariable != bearer)
            {
                definition.BearerTokenEnvironmentVariable = bearer;
                changed = true;
            }

            var transport = (definition.Transport ?? "").Trim().ToLowerInvariant();
            if (transport == "streamable-http") transport = HttpTransport;
            if (transport is not (StdioTransport or HttpTransport or SseTransport)) transport = StdioTransport;
            if (definition.Transport != transport)
            {
                definition.Transport = transport;
                changed = true;
            }

            definition.Arguments ??= new List<string>();
            for (var index = 0; index < definition.Arguments.Count; index++)
            {
                if (definition.Arguments[index] is not null) continue;
                definition.Arguments[index] = "";
                changed = true;
            }
            definition.Environment = NormalizeMap(definition.Environment, out var environmentChanged);
            definition.Headers = NormalizeMap(definition.Headers, out var headersChanged);
            changed |= environmentChanged || headersChanged;

            var startup = ClampTimeout(definition.StartupTimeoutSeconds, DefaultStartupTimeoutSeconds);
            var tool = ClampTimeout(definition.ToolTimeoutSeconds, DefaultToolTimeoutSeconds);
            if (definition.StartupTimeoutSeconds != startup) { definition.StartupTimeoutSeconds = startup; changed = true; }
            if (definition.ToolTimeoutSeconds != tool) { definition.ToolTimeoutSeconds = tool; changed = true; }
        }
        return changed;
    }

    public static IReadOnlyList<McpValidationMessage> Validate(
        McpServerDefinition definition, IEnumerable<McpServerDefinition>? catalog = null)
    {
        var messages = new List<McpValidationMessage>();
        var name = (definition.Name ?? "").Trim();
        var arguments = definition.Arguments ?? new List<string>();
        if (name.Length == 0)
            messages.Add(Error("Server name is required."));
        else if (!ServerNamePattern().IsMatch(name))
            messages.Add(Error("Server name may contain only letters, numbers, hyphens, and underscores."));
        else if (name.Length > 64)
            messages.Add(Error("Server name cannot exceed 64 characters."));
        if (catalog?.Any(other => !ReferenceEquals(other, definition)
                                  && !string.Equals(other.Id, definition.Id, StringComparison.OrdinalIgnoreCase)
                                  && string.Equals(other.Name?.Trim() ?? "", name, StringComparison.OrdinalIgnoreCase)) == true)
            messages.Add(Error($"Another MCP server is already named '{name}'."));

        if (definition.Transport is not (StdioTransport or HttpTransport or SseTransport))
            messages.Add(Error("Transport must be stdio, Streamable HTTP, or legacy SSE."));
        if (!definition.UseClaude && !definition.UseCodex && !definition.UseKimi && !definition.UseGrok)
            messages.Add(Error("Select at least one CLI."));
        if (definition.IsSse && definition.UseCodex)
            messages.Add(new McpValidationMessage(McpValidationSeverity.Warning,
                "Codex has no legacy SSE transport, so this server will be skipped for Codex."));

        if (definition.IsStdio)
        {
            if (string.IsNullOrWhiteSpace(definition.Command))
                messages.Add(Error("A command is required for a stdio server."));
            else if (definition.Command.Length > 4096)
                messages.Add(Error("Command cannot exceed 4,096 characters."));
            else if (definition.Command.Contains('\0'))
                messages.Add(Error("Command contains a null character."));
            else if (definition.Command.Length >= 2 && definition.Command[0] == '"' && definition.Command[^1] == '"')
                messages.Add(Error("Do not wrap the command in quotes. VibeCode passes the executable and each argument separately."));
            else
            {
                var expandedCommand = ExpandEnvironmentTemplates(definition.Command ?? "");
                if (!expandedCommand.Contains("${", StringComparison.Ordinal)
                    && Path.IsPathRooted(expandedCommand) && !File.Exists(expandedCommand))
                    messages.Add(new McpValidationMessage(McpValidationSeverity.Warning,
                        "The selected MCP server executable does not currently exist."));
                else if ((definition.UseKimi || definition.UseGrok) && !Path.IsPathRooted(expandedCommand)
                                               && ResolveExecutable(definition.Command ?? "") is null)
                    messages.Add(new McpValidationMessage(McpValidationSeverity.Warning,
                        "ACP asks for an absolute executable path, and this command is not currently on PATH."));
            }
            ValidateMap(definition.Environment, "environment variable", EnvironmentNamePattern(), messages);
            if (arguments.Count > 128 || arguments.Sum(argument => (long)(argument?.Length ?? 0)) > 16_384)
                messages.Add(Error("A stdio server may use at most 128 arguments and 16,384 argument characters."));
            if (arguments.Any(argument => argument is null || argument.Contains('\0')))
                messages.Add(Error("Command arguments cannot be null or contain a null character."));
            if (ExpandEnvironmentTemplates(definition.Command ?? "").Length > 4096)
                messages.Add(Error("Expanded command cannot exceed 4,096 characters."));
            if (arguments.Sum(argument => (long)ExpandEnvironmentTemplates(argument ?? "").Length) > 16_384)
                messages.Add(Error("Expanded command arguments cannot exceed 16,384 characters."));
            if ((definition.Environment?.Values ?? Enumerable.Empty<string>())
                .Select(value => ExpandEnvironmentTemplates(value ?? "")).Any(value => value.Length > 8192))
                messages.Add(Error("An expanded environment value exceeds 8,192 characters."));
        }
        else
        {
            var expandedUrl = ExpandEnvironmentTemplates(definition.Url ?? "");
            if (string.IsNullOrWhiteSpace(definition.Url))
                messages.Add(Error("A URL is required for a remote server."));
            else if (definition.Url.Length > 4096)
                messages.Add(Error("Remote MCP URL cannot exceed 4,096 characters."));
            else if (!expandedUrl.Contains("${", StringComparison.Ordinal)
                     && (!Uri.TryCreate(expandedUrl, UriKind.Absolute, out var uri)
                         || uri.Scheme is not ("http" or "https")))
                messages.Add(Error("Remote MCP URL must be an absolute http:// or https:// address."));
            ValidateMap(definition.Headers, "HTTP header", HeaderNamePattern(), messages, forbidNewLines: true);
            if (expandedUrl.Length > 4096)
                messages.Add(Error("Expanded remote MCP URL cannot exceed 4,096 characters."));
            var expandedHeaders = (definition.Headers?.Values ?? Enumerable.Empty<string>())
                .Select(value => ExpandEnvironmentTemplates(value ?? "")).ToArray();
            if (expandedHeaders.Any(value => value.Length > 8192))
                messages.Add(Error("An expanded HTTP header value exceeds 8,192 characters."));
            if (expandedHeaders.Any(value => value.Contains('\r') || value.Contains('\n')))
                messages.Add(Error("Expanded HTTP header values cannot contain line breaks."));
            if (definition.BearerTokenEnvironmentVariable is { Length: > 0 } bearer
                && !EnvironmentNamePattern().IsMatch(bearer))
                messages.Add(Error("Bearer token environment variable has an invalid name."));
            if (definition.BearerTokenEnvironmentVariable is { Length: > 0 }
                && definition.Headers?.Keys.Any(key => key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) == true)
                messages.Add(Error("Use either an Authorization header or a bearer-token environment variable, not both."));
            if (definition.BearerTokenEnvironmentVariable is { Length: > 0 } configuredBearer
                && Environment.GetEnvironmentVariable(configuredBearer) is { } configuredToken)
            {
                if (configuredToken.Length > 8192)
                    messages.Add(Error("Bearer token cannot exceed 8,192 characters."));
                if (configuredToken.Contains('\r') || configuredToken.Contains('\n'))
                    messages.Add(Error("Bearer token cannot contain a line break."));
            }
        }

        if (definition.StartupTimeoutSeconds is < MinTimeoutSeconds or > MaxTimeoutSeconds)
            messages.Add(Error($"Startup timeout must be between {MinTimeoutSeconds} and {MaxTimeoutSeconds} seconds."));
        if (definition.ToolTimeoutSeconds is < MinTimeoutSeconds or > MaxTimeoutSeconds)
            messages.Add(Error($"Tool timeout must be between {MinTimeoutSeconds} and {MaxTimeoutSeconds} seconds."));

        var templateValues = TemplateBearingValues(definition).ToArray();
        var referenced = templateValues
            .SelectMany(MissingEnvironmentVariables)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (referenced.Length > 0)
            messages.Add(new McpValidationMessage(McpValidationSeverity.Warning,
                "Not set in VibeCode's environment: " + string.Join(", ", referenced) + "."));
        if (definition.BearerTokenEnvironmentVariable is { Length: > 0 } tokenName
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable(tokenName)))
            messages.Add(new McpValidationMessage(McpValidationSeverity.Warning,
                $"Bearer token environment variable {tokenName} is not set in VibeCode's environment."));
        if (definition.UseClaude)
        {
            var suppressed = templateValues.SelectMany(ReferencedEnvironmentVariables)
                .Concat(new[] { definition.BearerTokenEnvironmentVariable })
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Intersect(ClaudeSuppressedEnvironmentVariables, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (suppressed.Length > 0)
                messages.Add(Error("Claude-targeted MCP references cannot use environment variables that VibeCode removes to protect account selection: "
                                   + string.Join(", ", suppressed) + ". Use an MCP-specific variable name."));
        }
        if (definition.UseCodex && new[] { definition.Command, definition.Url }.Concat(arguments)
                .Any(value => EnvironmentTemplatePattern().IsMatch(value ?? "")))
            messages.Add(new McpValidationMessage(McpValidationSeverity.Warning,
                "Codex expands variables used in commands, arguments, or URLs into its launch command. Keep secrets in environment, header, or bearer-token fields."));
        return messages;
    }

    /// <summary>
    /// Fail loudly at the process boundary when an enabled definition cannot be honored. Settings validation keeps
    /// missing variables as warnings so a portable catalog can be saved before its environment is installed; a live
    /// CLI must never receive the literal text ${TOKEN} or silently lose an invalid server.
    /// </summary>
    public static void EnsureLaunchReady(IEnumerable<McpServerDefinition>? source, string provider,
        string? sessionDirectory = null)
    {
        var definitions = Snapshot(source);
        var selected = definitions.Where(definition => definition.Enabled && IsSelectedForProvider(definition, provider)).ToList();
        var problems = new List<string>();
        if (selected.Count > MaxManagedServers)
            problems.Add($"at most {MaxManagedServers} managed MCP servers may target one CLI");

        foreach (var definition in selected)
        {
            foreach (var validation in Validate(definition, definitions)
                         .Where(message => message.Severity == McpValidationSeverity.Error))
                problems.Add($"{definition.Name}: {validation.Message}");

            var missing = TemplateBearingValues(definition).SelectMany(MissingEnvironmentVariables)
                .Concat(definition.BearerTokenEnvironmentVariable is { Length: > 0 } bearer
                        && string.IsNullOrEmpty(Environment.GetEnvironmentVariable(bearer))
                    ? new[] { bearer }
                    : Array.Empty<string>())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (missing.Length > 0)
                problems.Add($"{definition.Name}: set {string.Join(", ", missing)} in VibeCode's environment");

            if (missing.Length == 0)
            {
                if (definition.IsStdio)
                {
                    if (ExpandEnvironmentTemplates(definition.Command ?? "").Length > 4096)
                        problems.Add($"{definition.Name}: expanded command exceeds 4,096 characters");
                    if (definition.Arguments.Sum(argument => (long)ExpandEnvironmentTemplates(argument).Length) > 16_384)
                        problems.Add($"{definition.Name}: expanded command arguments exceed 16,384 characters");
                    if (definition.Environment.Values.Select(ExpandEnvironmentTemplates).Any(value => value.Length > 8192))
                        problems.Add($"{definition.Name}: an expanded environment value exceeds 8,192 characters");

                    if (provider is "kimi" or "grok")
                    {
                        var executable = ResolveExecutable(definition.Command ?? "", sessionDirectory);
                        if (executable is null || !Path.IsPathRooted(executable) || !File.Exists(executable))
                            problems.Add($"{definition.Name}: {provider} requires an existing executable path; install the command or select its executable");
                    }
                }
                else
                {
                    if (ExpandEnvironmentTemplates(definition.Url).Length > 4096)
                        problems.Add($"{definition.Name}: expanded URL exceeds 4,096 characters");
                    var expandedHeaders = definition.Headers.Values.Select(ExpandEnvironmentTemplates).ToArray();
                    if (expandedHeaders.Any(value => value.Length > 8192))
                        problems.Add($"{definition.Name}: an expanded HTTP header value exceeds 8,192 characters");
                    if (expandedHeaders.Any(value => value.Contains('\r') || value.Contains('\n')))
                        problems.Add($"{definition.Name}: expanded HTTP header values cannot contain line breaks");
                    if (definition.BearerTokenEnvironmentVariable is { Length: > 0 } bearerName
                        && Environment.GetEnvironmentVariable(bearerName) is { } token)
                    {
                        if (token.Length > 8192)
                            problems.Add($"{definition.Name}: bearer token environment variable {bearerName} exceeds 8,192 characters");
                        if (token.Contains('\r') || token.Contains('\n'))
                            problems.Add($"{definition.Name}: bearer token environment variable {bearerName} contains a line break");
                    }
                }
            }
        }

        var distinct = problems.Distinct(StringComparer.Ordinal).ToArray();
        if (distinct.Length > 0)
        {
            var providerName = provider switch { "claude" => "Claude", "codex" => "Codex", "kimi" => "Kimi", "grok" => "Grok", _ => provider };
            throw new InvalidOperationException($"Cannot launch {providerName} with the selected MCP catalog: "
                                                + string.Join("; ", distinct) + ".");
        }
    }

    public static List<McpServerDefinition> Snapshot(IEnumerable<McpServerDefinition>? definitions)
    {
        var snapshot = definitions?.Where(value => value is not null).Select(value => value.Clone()).ToList()
                       ?? new List<McpServerDefinition>();
        NormalizeDefinitions(snapshot);
        return snapshot;
    }

    public static JsonObject BuildClaudeConfig(IEnumerable<McpServerDefinition>? definitions)
    {
        var servers = new JsonObject();
        foreach (var definition in Selected(definitions, "claude"))
        {
            var entry = new JsonObject { ["type"] = definition.Transport };
            if (definition.IsStdio)
            {
                entry["command"] = definition.Command;
                entry["args"] = new JsonArray(definition.Arguments
                    .Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
                entry["env"] = ToJsonObject(definition.Environment);
            }
            else
            {
                entry["url"] = definition.Url;
                var headers = new Dictionary<string, string>(definition.Headers, StringComparer.OrdinalIgnoreCase);
                if (definition.BearerTokenEnvironmentVariable is { Length: > 0 } bearer)
                    headers["Authorization"] = $"Bearer ${{{bearer}}}";
                if (headers.Count > 0) entry["headers"] = ToJsonObject(headers);
            }
            entry["timeout"] = checked(definition.ToolTimeoutSeconds * 1000);
            servers[RuntimeServerName(definition)] = entry;
        }
        return new JsonObject { ["mcpServers"] = servers };
    }

    public static int? MaximumStartupTimeoutSeconds(IEnumerable<McpServerDefinition>? definitions, string provider)
    {
        var values = Selected(definitions, provider).Select(definition => definition.StartupTimeoutSeconds).ToArray();
        return values.Length == 0 ? null : values.Max();
    }

    /// <summary>Write an immutable, content-addressed Claude launch file and return null when no Claude server applies.</summary>
    public static string? WriteClaudeLaunchConfig(IEnumerable<McpServerDefinition>? definitions, string? directory = null)
    {
        var config = BuildClaudeConfig(definitions);
        if (config["mcpServers"] is not JsonObject { Count: > 0 }) return null;
        var json = config.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant()[..16];
        directory ??= Path.Combine(AppSettings.Dir, "mcp");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"claude-{hash}.json");
        if (File.Exists(path)) return path;
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            try { File.Move(temp, path, overwrite: false); }
            catch (IOException) when (File.Exists(path)) { File.Delete(temp); }
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            throw;
        }
        return path;
    }

    public static CodexMcpProjection BuildCodexProjection(IEnumerable<McpServerDefinition>? definitions)
    {
        var projection = new CodexMcpProjection();
        var environmentOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in Selected(definitions, "codex").ToList())
        {
            var fields = new List<string>();
            if (definition.IsStdio)
            {
                fields.Add("command = " + TomlString(ExpandEnvironmentTemplates(definition.Command)));
                fields.Add("args = " + TomlArray(definition.Arguments.Select(ExpandEnvironmentTemplates)));
                if (definition.Environment.Count > 0)
                {
                    foreach (var pair in definition.Environment)
                    {
                        var expanded = ExpandEnvironmentTemplates(pair.Value);
                        if (projection.Environment.TryGetValue(pair.Key, out var existing) && existing != expanded)
                            throw new InvalidOperationException(
                                $"Codex MCP servers '{environmentOwners[pair.Key]}' and '{definition.Name}' assign different values to {pair.Key}. Use the same value for that variable or disable Codex for one definition.");
                        projection.Environment[pair.Key] = expanded;
                        environmentOwners[pair.Key] = definition.Name;
                    }
                    fields.Add("env_vars = " + TomlArray(definition.Environment.Keys.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)));
                }
            }
            else
            {
                fields.Add("url = " + TomlString(ExpandEnvironmentTemplates(definition.Url)));
                if (definition.BearerTokenEnvironmentVariable is { Length: > 0 } bearer)
                    fields.Add("bearer_token_env_var = " + TomlString(bearer));
                if (definition.Headers.Count > 0)
                {
                    var headerEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var index = 0;
                    foreach (var pair in definition.Headers.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
                    {
                        var variable = HeaderEnvironmentName(definition.Name, pair.Key, index++);
                        projection.Environment[variable] = ExpandEnvironmentTemplates(pair.Value);
                        headerEnvironment[pair.Key] = variable;
                    }
                    fields.Add("env_http_headers = " + TomlMap(headerEnvironment));
                }
            }
            fields.Add($"startup_timeout_sec = {definition.StartupTimeoutSeconds}");
            fields.Add($"tool_timeout_sec = {definition.ToolTimeoutSeconds}");
            fields.Add("enabled = true");
            // Codex's -c dotted-path parser does not apply TOML quoted-key semantics to path segments: quoting a
            // safe name makes the quote characters part of the server name. Validation deliberately limits names
            // to bare TOML-key characters, so use the segment verbatim here.
            projection.ConfigOverrides.Add($"mcp_servers.{RuntimeServerName(definition)}={{ {string.Join(", ", fields)} }}");
        }
        return projection;
    }

    public static JsonArray BuildKimiAcpServers(IEnumerable<McpServerDefinition>? definitions, string? sessionDirectory = null)
        => BuildAcpServers(definitions, "kimi", sessionDirectory);

    public static JsonArray BuildGrokAcpServers(IEnumerable<McpServerDefinition>? definitions, string? sessionDirectory = null)
        => BuildAcpServers(definitions, "grok", sessionDirectory);

    private static JsonArray BuildAcpServers(IEnumerable<McpServerDefinition>? definitions, string provider,
        string? sessionDirectory)
    {
        var servers = new JsonArray();
        foreach (var definition in Selected(definitions, provider))
        {
            var entry = new JsonObject
            {
                ["name"] = RuntimeServerName(definition),
            };
            if (definition.IsStdio)
            {
                // ACP v1's stdio union branch is the only branch without a `type` discriminator. Kimi's
                // adapter therefore identifies stdio by the absence of `type`; sending `type: "stdio"`
                // makes current Kimi releases treat the otherwise-valid server as unsupported and drop it.
                var command = ExpandEnvironmentTemplates(definition.Command);
                entry["command"] = ResolveExecutable(command, sessionDirectory) ?? command;
                entry["args"] = new JsonArray(definition.Arguments.Select(value => JsonValue.Create(ExpandEnvironmentTemplates(value))).ToArray());
                var environment = new JsonArray();
                foreach (var pair in definition.Environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
                    environment.Add(new JsonObject { ["name"] = pair.Key, ["value"] = ExpandEnvironmentTemplates(pair.Value) });
                entry["env"] = environment;
            }
            else
            {
                entry["type"] = definition.Transport;
                entry["url"] = ExpandEnvironmentTemplates(definition.Url);
                var headers = new Dictionary<string, string>(definition.Headers, StringComparer.OrdinalIgnoreCase);
                if (definition.BearerTokenEnvironmentVariable is { Length: > 0 } bearer)
                    headers["Authorization"] = "Bearer " + (Environment.GetEnvironmentVariable(bearer) ?? $"${{{bearer}}}");
                var headerArray = new JsonArray();
                foreach (var pair in headers.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
                    headerArray.Add(new JsonObject { ["name"] = pair.Key, ["value"] = ExpandEnvironmentTemplates(pair.Value) });
                entry["headers"] = headerArray;
            }
            servers.Add(entry);
        }
        return servers;
    }

    public static string ExpandEnvironmentTemplates(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return EnvironmentTemplatePattern().Replace(value, match =>
        {
            var name = match.Groups["name"].Value;
            var current = Environment.GetEnvironmentVariable(name);
            if (current is not null) return current;
            return match.Groups["fallback"].Success ? match.Groups["fallback"].Value : match.Value;
        });
    }

    /// <summary>
    /// Keep VibeCode-managed definitions out of provider-native namespaces. Codex merges same-named TOML tables
    /// field-by-field, so an HTTP native entry plus a stdio launch override would otherwise become invalid. The
    /// stable suffix is shared across all adapters and keeps the human portion recognizable in MCP tool names.
    /// </summary>
    public static string RuntimeServerName(McpServerDefinition definition)
    {
        var display = string.IsNullOrWhiteSpace(definition.Name) ? "server" : definition.Name.Trim();
        var safe = new string(display.Select(character =>
            character is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_'
                ? character
                : '_').ToArray()).Trim('_');
        if (safe.Length == 0) safe = "server";
        const int maximumDisplayCharacters = 48; // vc_ + display + _ + 12-hex suffix = at most 64 characters
        if (safe.Length > maximumDisplayCharacters) safe = safe[..maximumDisplayCharacters];
        var identity = string.IsNullOrWhiteSpace(definition.Id) ? display : definition.Id;
        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant()[..12];
        return $"vc_{safe}_{suffix}";
    }

    public static IEnumerable<string> MissingEnvironmentVariables(string value)
    {
        foreach (Match match in EnvironmentTemplatePattern().Matches(value ?? ""))
        {
            var name = match.Groups["name"].Value;
            if (!match.Groups["fallback"].Success && Environment.GetEnvironmentVariable(name) is null)
                yield return name;
        }
    }

    private static IEnumerable<string> ReferencedEnvironmentVariables(string value)
    {
        foreach (Match match in EnvironmentTemplatePattern().Matches(value ?? ""))
            yield return match.Groups["name"].Value;
    }

    /// <summary>Resolve a bare command for ACP, whose stdio schema asks clients to send an absolute executable path.</summary>
    public static string? ResolveExecutable(string command, string? workingDirectory = null)
    {
        command = ExpandEnvironmentTemplates(command).Trim().Trim('"');
        if (command.Length == 0) return null;
        try
        {
            if (Path.IsPathRooted(command)) return Path.GetFullPath(command);
            if (command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar))
                return Path.GetFullPath(command, string.IsNullOrWhiteSpace(workingDirectory) ? Environment.CurrentDirectory : workingDirectory);

            var extensions = OperatingSystem.IsWindows()
                ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Prepend("")
                : new[] { "" };
            foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "")
                         .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory.Trim('"'), command + extension);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
        }
        catch { }
        return null;
    }

    private static IEnumerable<McpServerDefinition> Selected(IEnumerable<McpServerDefinition>? source, string provider)
    {
        var definitions = Snapshot(source);
        foreach (var definition in definitions)
        {
            if (!definition.Enabled || Validate(definition, definitions).Any(message => message.Severity == McpValidationSeverity.Error))
                continue;
            if (IsSelectedForProvider(definition, provider)) yield return definition;
        }
    }

    private static bool IsSelectedForProvider(McpServerDefinition definition, string provider) => provider switch
    {
        "claude" => definition.UseClaude,
        "codex" => definition.UseCodex && !definition.IsSse,
        "kimi" => definition.UseKimi,
        "grok" => definition.UseGrok,
        _ => false,
    };

    private static IEnumerable<string> TemplateBearingValues(McpServerDefinition definition)
    {
        foreach (var value in definition.Environment?.Values ?? Enumerable.Empty<string>()) yield return value ?? "";
        foreach (var value in definition.Headers?.Values ?? Enumerable.Empty<string>()) yield return value ?? "";
        yield return definition.Command ?? "";
        yield return definition.Url ?? "";
        foreach (var value in definition.Arguments ?? new List<string>()) yield return value ?? "";
    }

    private static JsonObject ToJsonObject(IEnumerable<KeyValuePair<string, string>> values)
    {
        var result = new JsonObject();
        foreach (var pair in values.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)) result[pair.Key] = pair.Value;
        return result;
    }

    private static string HeaderEnvironmentName(string server, string header, int index)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(server + "\n" + header)))[..12];
        return $"VIBECODE_MCP_HEADER_{hash}_{index}";
    }

    private static string TomlString(string value) => JsonSerializer.Serialize(value);
    private static string TomlArray(IEnumerable<string> values) => "[" + string.Join(", ", values.Select(TomlString)) + "]";
    private static string TomlMap(IEnumerable<KeyValuePair<string, string>> values) =>
        "{ " + string.Join(", ", values.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => $"{TomlString(pair.Key)} = {TomlString(pair.Value)}")) + " }";

    private static int ClampTimeout(int value, int fallback) => value <= 0 ? fallback : Math.Clamp(value, MinTimeoutSeconds, MaxTimeoutSeconds);

    private static Dictionary<string, string> NormalizeMap(Dictionary<string, string>? map, out bool changed)
    {
        map ??= new Dictionary<string, string>();
        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in map)
        {
            var key = pair.Key?.Trim() ?? "";
            if (key.Length > 0) normalized[key] = pair.Value ?? "";
        }
        changed = map.Comparer != StringComparer.OrdinalIgnoreCase || map.Count != normalized.Count
                  || map.Any(pair => !normalized.TryGetValue(pair.Key, out var value) || value != pair.Value);
        return changed ? normalized : map;
    }

    private static void ValidateMap(Dictionary<string, string>? map, string label, Regex keyPattern,
        ICollection<McpValidationMessage> messages, bool forbidNewLines = false)
    {
        foreach (var pair in map ?? new Dictionary<string, string>())
        {
            if (!keyPattern.IsMatch(pair.Key)) messages.Add(Error($"Invalid {label} name: '{pair.Key}'."));
            if (pair.Key.Length > 128) messages.Add(Error($"{label} name cannot exceed 128 characters."));
            if (pair.Value.Length > 8192) messages.Add(Error($"{label} '{pair.Key}' value cannot exceed 8,192 characters."));
            if (pair.Value.Contains('\0')) messages.Add(Error($"{label} '{pair.Key}' contains a null character."));
            if (forbidNewLines && (pair.Value.Contains('\r') || pair.Value.Contains('\n')))
                messages.Add(Error($"{label} '{pair.Key}' cannot contain a line break."));
        }
        if ((map?.Count ?? 0) > 64) messages.Add(Error($"A server may define at most 64 {label}s."));
    }

    private static McpValidationMessage Error(string message) => new(McpValidationSeverity.Error, message);
}
