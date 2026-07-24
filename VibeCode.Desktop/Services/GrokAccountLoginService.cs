using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using VibeCode.Protocol;

namespace VibeCode.Services;

public enum GrokLoginMode
{
    DeviceCode,
    ExistingBrowserSession,
}

public sealed class GrokStoredIdentity
{
    public string? AccountId { get; init; }
    public string? Name { get; init; }
    public string? Email { get; init; }
    public string? AuthMode { get; init; }
    public string? TeamName { get; init; }
}

public sealed class GrokAccountState
{
    public bool IsInstalled { get; init; }
    public bool IsSignedIn { get; init; }
    public string? Version { get; init; }
    public string? Error { get; init; }
    public string? AccountId { get; init; }
    public string? Name { get; init; }
    public string? Email { get; init; }
    public string? Plan { get; init; }
    public string? AuthMode { get; init; }
    public int? UsagePercent { get; init; }
    public string? UsagePeriod { get; init; }
    public DateTimeOffset? UsageResetsAt { get; init; }
    public long? PrepaidBalanceCents { get; init; }
    public bool UsageReadSucceeded { get; init; }
    public string? UsageError { get; init; }
}

public sealed class GrokLoginResult
{
    public bool Success { get; init; }
    public string? Version { get; init; }
    public string? Error { get; init; }
    public string? AccountId { get; init; }
    public string? Name { get; init; }
    public string? Email { get; init; }
    public string? Plan { get; init; }
    public string? AuthMode { get; init; }
    public int? UsagePercent { get; init; }
    public string? UsagePeriod { get; init; }
    public DateTimeOffset? UsageResetsAt { get; init; }
    public long? PrepaidBalanceCents { get; init; }
}

public sealed class GrokLogoutResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Authenticates Grok through ACP. Every managed account gets its own GROK_AUTH_PATH, which is the native
/// Grok Build override for auth.json; GROK_HOME stays shared so configuration, plugins, and sessions remain shared.
/// </summary>
public static partial class GrokAccountLoginService
{
    private static readonly SemaphoreSlim AccountGate = new(1, 1);
    private static readonly HttpClient BillingClient = new() { Timeout = TimeSpan.FromSeconds(18) };
    internal static string DefaultHome
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("GROK_HOME");
            return string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".grok")
                : Path.GetFullPath(configured.Trim('"'));
        }
    }

    public static Task<GrokAccountState> ReadAsync(CancellationToken cancellationToken = default) =>
        ReadAsync(null, true, cancellationToken);

    public static async Task<GrokAccountState> ReadAsync(string? authFilePath, bool includeUsage = true,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        var held = false;
        Process? process = null;
        Task<string>? stderr = null;
        try
        {
            await AccountGate.WaitAsync(token).ConfigureAwait(false);
            held = true;
            process = Start(authFilePath, "agent", "--no-leader", "stdio");
            stderr = process.StandardError.ReadToEndAsync(token);
            await WriteAsync(process, 1, "initialize", new JsonObject
            {
                ["protocolVersion"] = 1,
                ["clientCapabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject
                {
                    ["name"] = "vibecode-account-check",
                    ["title"] = "VibeCode",
                    ["version"] = "1.0.0",
                },
            }, token).ConfigureAwait(false);
            var initialize = await ReadResponseAsync(process, 1, token).ConfigureAwait(false);
            ThrowIfError(initialize);
            var meta = initialize["result"]?["_meta"];
            var version = StringValue(meta?["agentVersion"])
                          ?? StringValue(initialize["result"]?["agentInfo"]?["version"]);
            var authMethod = StringValue(meta?["defaultAuthMethodId"]);
            if (authMethod is null)
                return new GrokAccountState { IsInstalled = true, IsSignedIn = false, Version = version };

            await WriteAsync(process, 2, "authenticate", new JsonObject { ["methodId"] = authMethod }, token)
                .ConfigureAwait(false);
            var authenticate = await ReadResponseAsync(process, 2, token).ConfigureAwait(false);
            if (authenticate["error"] is JsonObject authError)
            {
                return new GrokAccountState
                {
                    IsInstalled = true,
                    IsSignedIn = false,
                    Version = version,
                    Error = ErrorMessage(authError),
                };
            }

            var stored = InspectStoredAccount(authFilePath);
            var authMeta = authenticate["result"]?["_meta"];
            JsonObject? authInfo = null;
            await WriteAsync(process, 3, "x.ai/auth/info", new JsonObject(), token).ConfigureAwait(false);
            var infoResponse = await ReadResponseAsync(process, 3, token).ConfigureAwait(false);
            if (infoResponse["error"] is null) authInfo = infoResponse["result"] as JsonObject;

            JsonObject? billing = null;
            string? usageError = null;
            if (includeUsage)
            {
                await WriteAsync(process, 4, "x.ai/billing", new JsonObject(), token).ConfigureAwait(false);
                var billingResponse = await ReadResponseAsync(process, 4, token).ConfigureAwait(false);
                if (billingResponse["error"] is JsonObject billingError) usageError = ErrorMessage(billingError);
                else billing = billingResponse["result"] as JsonObject;
                if (billing is null)
                {
                    var fallback = await FetchBillingFallbackAsync(authFilePath, version, token).ConfigureAwait(false);
                    billing = fallback.Billing;
                    usageError = fallback.Error ?? usageError;
                }
            }

            var email = StringValue(authInfo?["email"]) ?? StringValue(authMeta?["email"]) ?? stored.Email;
            var firstName = StringValue(authInfo?["firstName"]);
            var lastName = StringValue(authInfo?["lastName"]);
            var name = JoinName(firstName, lastName)
                       ?? stored.Name
                       ?? StringValue(authInfo?["teamName"])
                       ?? StringValue(authMeta?["team_name"]);
            var accountId = stored.AccountId
                            ?? StringValue(authInfo?["principalId"])
                            ?? email;
            var plan = StringValue(billing?["subscription_tier"])
                       ?? StringValue(billing?["subscriptionTier"])
                       ?? StringValue(authMeta?["subscription_tier"]);
            var config = billing?["config"];
            var usagePercent = ProductUsagePercent(config) ?? PercentValue(config?["creditUsagePercent"]);
            if (usagePercent is null)
            {
                var used = DecimalValue(billing?["used"]?["val"]);
                var limit = DecimalValue(billing?["monthlyLimit"]?["val"]);
                if (used is not null && limit > 0) usagePercent = (int)Math.Clamp(Math.Round(used.Value / limit.Value * 100m), 0, 100);
            }
            var period = NormalizeUsagePeriod(StringValue(config?["currentPeriod"]?["type"]));
            var resetsAt = DateValue(config?["currentPeriod"]?["end"]) ?? DateValue(billing?["billingPeriodEnd"]);
            var prepaid = MoneyValue(config?["prepaidBalance"]?["val"]) ?? MoneyValue(billing?["prepaidBalance"]?["val"]);
            return new GrokAccountState
            {
                IsInstalled = true,
                IsSignedIn = true,
                Version = version,
                AccountId = accountId,
                Name = name,
                Email = email,
                Plan = plan,
                AuthMode = StringValue(authInfo?["methodId"]) ?? StringValue(authMeta?["auth_mode"]) ?? stored.AuthMode,
                UsagePercent = usagePercent,
                UsagePeriod = period,
                UsageResetsAt = resetsAt,
                PrepaidBalanceCents = prepaid,
                UsageReadSucceeded = includeUsage && billing is not null,
                UsageError = usageError,
            };
        }
        catch (FileNotFoundException)
        {
            return new GrokAccountState { IsInstalled = false, IsSignedIn = false };
        }
        catch (OperationCanceledException)
        {
            return new GrokAccountState
            {
                IsInstalled = true,
                Error = cancellationToken.IsCancellationRequested
                    ? "Grok account check cancelled."
                    : "Grok account check timed out.",
            };
        }
        catch (Exception ex)
        {
            return new GrokAccountState { IsInstalled = true, Error = ex.Message };
        }
        finally
        {
            if (process is not null) Stop(process);
            if (stderr is not null)
                try { _ = await stderr.ConfigureAwait(false); } catch { }
            if (held) AccountGate.Release();
        }
    }

    public static Task<GrokLoginResult> LoginAsync(Action<string> openBrowser,
        Action<string>? statusChanged = null, CancellationToken cancellationToken = default) =>
        LoginAsync(null, GrokLoginMode.DeviceCode, openBrowser, statusChanged, cancellationToken);

    public static Task<GrokLoginResult> LoginAsync(GrokLoginMode mode, Action<string> openBrowser,
        Action<string>? statusChanged = null, CancellationToken cancellationToken = default) =>
        LoginAsync(null, mode, openBrowser, statusChanged, cancellationToken);

    public static async Task<GrokLoginResult> LoginAsync(string? authFilePath, GrokLoginMode mode,
        Action<string> openBrowser, Action<string>? statusChanged = null,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(8));
        var token = timeout.Token;
        var held = false;
        Process? process = null;
        try
        {
            await AccountGate.WaitAsync(token).ConfigureAwait(false);
            held = true;
            process = mode == GrokLoginMode.ExistingBrowserSession
                ? Start(authFilePath, "login", "--oauth")
                : Start(authFilePath, "login", "--device-auth");
            var recent = new Queue<string>();
            var outputGate = new object();
            var opened = 0;

            void Observe(string raw)
            {
                var line = AnsiRegex().Replace(raw, "").Trim();
                if (line.Length == 0) return;
                var displayLine = RedactLoginOutput(line);
                lock (outputGate)
                {
                    recent.Enqueue(displayLine);
                    while (recent.Count > 10) recent.Dequeue();
                }
                statusChanged?.Invoke(displayLine);
                if (TryGetTrustedLoginUrl(line, out var loginUrl)
                    && Interlocked.CompareExchange(ref opened, 1, 0) == 0)
                    openBrowser(loginUrl);
            }

            async Task PumpAsync(StreamReader reader)
            {
                while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } line) Observe(line);
            }

            var stdout = PumpAsync(process.StandardOutput);
            var stderr = PumpAsync(process.StandardError);
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                string detail;
                lock (outputGate) detail = recent.Count > 0
                    ? string.Join(Environment.NewLine, recent)
                    : $"Grok login exited with code {process.ExitCode}.";
                return new GrokLoginResult { Error = detail };
            }

            process.Dispose();
            process = null;
            AccountGate.Release();
            held = false;
            var state = await ReadAsync(authFilePath, true, token).ConfigureAwait(false);
            return state.IsSignedIn
                ? LoginResult(state)
                : new GrokLoginResult
                {
                    Error = state.Error ?? "Grok finished login but the account is still unavailable.",
                };
        }
        catch (FileNotFoundException ex) { return new GrokLoginResult { Error = ex.Message }; }
        catch (OperationCanceledException)
        {
            return new GrokLoginResult
            {
                Error = cancellationToken.IsCancellationRequested ? "Grok sign-in cancelled." : "Grok sign-in timed out.",
            };
        }
        catch (Exception ex) { return new GrokLoginResult { Error = ex.Message }; }
        finally
        {
            if (process is not null) Stop(process);
            if (held) AccountGate.Release();
        }
    }

    public static async Task<GrokLogoutResult> LogoutAsync(string authFilePath,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        var token = timeout.Token;
        var held = false;
        Process? process = null;
        try
        {
            await AccountGate.WaitAsync(token).ConfigureAwait(false);
            held = true;
            process = Start(authFilePath, "logout");
            var stdout = process.StandardOutput.ReadToEndAsync(token);
            var stderr = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            var detail = string.Join(Environment.NewLine, new[]
            {
                await stdout.ConfigureAwait(false),
                await stderr.ConfigureAwait(false),
            }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
            if (process.ExitCode != 0)
                return new GrokLogoutResult
                {
                    Error = detail.Length > 0 ? RedactLoginOutput(detail) : $"Grok logout exited with code {process.ExitCode}.",
                };
            return new GrokLogoutResult { Success = true };
        }
        catch (OperationCanceledException)
        {
            return new GrokLogoutResult
            {
                Error = cancellationToken.IsCancellationRequested ? "Grok logout cancelled." : "Grok logout timed out.",
            };
        }
        catch (Exception ex) { return new GrokLogoutResult { Error = ex.Message }; }
        finally
        {
            if (process is not null) Stop(process);
            if (held) AccountGate.Release();
        }
    }

    public static GrokStoredIdentity InspectStoredAccount(string? authFilePath)
    {
        var path = string.IsNullOrWhiteSpace(authFilePath)
            ? Path.Combine(DefaultHome, "auth.json")
            : Path.GetFullPath(authFilePath);
        try
        {
            if (!File.Exists(path)) return new GrokStoredIdentity();
            var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            if (root is null) return new GrokStoredIdentity();
            var entries = new List<JsonObject>();
            if (root["user_id"] is not null || root["principal_id"] is not null) entries.Add(root);
            foreach (var item in root)
                if (item.Value is JsonObject candidate
                    && (candidate["user_id"] is not null || candidate["principal_id"] is not null
                        || candidate["email"] is not null))
                    entries.Add(candidate);
            var selected = entries
                .OrderByDescending(e => DateValue(e["create_time"]) ?? DateTimeOffset.MinValue)
                .FirstOrDefault();
            if (selected is null) return new GrokStoredIdentity();
            return new GrokStoredIdentity
            {
                AccountId = StringValue(selected["user_id"]) ?? StringValue(selected["principal_id"])
                            ?? StringValue(selected["email"]),
                Name = JoinName(StringValue(selected["first_name"]), StringValue(selected["last_name"])),
                Email = StringValue(selected["email"]),
                AuthMode = StringValue(selected["auth_mode"]),
                TeamName = StringValue(selected["team_name"]),
            };
        }
        catch { return new GrokStoredIdentity(); }
    }

    /// <summary>
    /// Older reviewed runtimes authenticate correctly but predate the x.ai/billing ACP extension. Use the same
    /// first-party request implemented by current Grok Build as a compatibility fallback; credentials never leave
    /// the Authorization header and no response body is copied into diagnostics.
    /// </summary>
    private static async Task<(JsonObject? Billing, string? Error)> FetchBillingFallbackAsync(
        string? authFilePath, string? version, CancellationToken token)
    {
        var credential = ReadStoredCredential(authFilePath);
        if (credential is null) return (null, "Grok billing is unavailable for this login.");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://cli-chat-proxy.grok.com/v1/billing?format=credits");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Key);
            request.Headers.TryAddWithoutValidation("X-XAI-Token-Auth", "xai-grok-cli");
            request.Headers.TryAddWithoutValidation("x-userid", credential.UserId);
            request.Headers.TryAddWithoutValidation("x-grok-client-version", version ?? "vibecode-1.0");
            request.Headers.TryAddWithoutValidation("x-grok-client-mode", "headless");
            using var response = await BillingClient.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return (null, $"Grok billing returned HTTP {(int)response.StatusCode}.");
            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            var billing = await JsonNode.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false)
                          as JsonObject;
            return billing is null
                ? (null, "Grok billing returned an unreadable response.")
                : (billing, null);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return (null, "Grok billing timed out.");
        }
        catch (HttpRequestException ex)
        {
            return (null, "Grok billing request failed: " + ex.Message);
        }
    }

    private sealed record StoredCredential(string Key, string UserId);

    private static StoredCredential? ReadStoredCredential(string? authFilePath)
    {
        var path = string.IsNullOrWhiteSpace(authFilePath)
            ? Path.Combine(DefaultHome, "auth.json")
            : Path.GetFullPath(authFilePath);
        try
        {
            if (!File.Exists(path)) return null;
            var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            if (root is null) return null;
            var entries = new List<JsonObject>();
            if (root["key"] is not null) entries.Add(root);
            entries.AddRange(root.Select(x => x.Value).OfType<JsonObject>().Where(x => x["key"] is not null));
            foreach (var entry in entries.OrderByDescending(x => DateValue(x["create_time"])
                                                                  ?? DateTimeOffset.MinValue))
            {
                var key = StringValue(entry["key"]);
                var userId = StringValue(entry["user_id"]);
                if (key is not null && userId is not null) return new StoredCredential(key, userId);
            }
        }
        catch { }
        return null;
    }

    private static GrokLoginResult LoginResult(GrokAccountState state) => new()
    {
        Success = true,
        Version = state.Version,
        AccountId = state.AccountId,
        Name = state.Name,
        Email = state.Email,
        Plan = state.Plan,
        AuthMode = state.AuthMode,
        UsagePercent = state.UsagePercent,
        UsagePeriod = state.UsagePeriod,
        UsageResetsAt = state.UsageResetsAt,
        PrepaidBalanceCents = state.PrepaidBalanceCents,
    };

    private static Process Start(string? authFilePath, params string[] args)
    {
        var process = new Process
        {
            StartInfo = GrokSession.CreateCliStartInfoForAuth(null, authFilePath, args),
        };
        process.Start();
        ProcessJob.Assign(process);
        return process;
    }

    private static async Task WriteAsync(Process process, int id, string method, JsonObject parameters,
        CancellationToken token)
    {
        var json = new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters,
        }.ToJsonString(new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        await process.StandardInput.WriteLineAsync(json.AsMemory(), token).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(token).ConfigureAwait(false);
    }

    private static async Task<JsonObject> ReadResponseAsync(Process process, int id, CancellationToken token)
    {
        while (await process.StandardOutput.ReadLineAsync(token).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonObject? message;
            try { message = JsonNode.Parse(line) as JsonObject; } catch { continue; }
            if (JsonInt(message?["id"]) == id) return message!;
        }
        throw new InvalidOperationException("Grok ACP closed before it answered the account check.");
    }

    private static void ThrowIfError(JsonObject message)
    {
        if (message["error"] is JsonObject error) throw new InvalidOperationException(ErrorMessage(error));
    }

    private static string ErrorMessage(JsonObject error) =>
        StringValue(error["message"]) ?? error.ToJsonString();

    private static string? JoinName(string? first, string? last)
    {
        var result = string.Join(" ", new[] { first, last }.Where(v => !string.IsNullOrWhiteSpace(v))).Trim();
        return result.Length == 0 ? null : result;
    }

    private static string? NormalizeUsagePeriod(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.Contains("weekly", StringComparison.OrdinalIgnoreCase)) return "weekly";
        if (value.Contains("monthly", StringComparison.OrdinalIgnoreCase)) return "monthly";
        return value.Trim().ToLowerInvariant().Replace('_', ' ');
    }

    private static string? StringValue(JsonNode? node)
    {
        try { return string.IsNullOrWhiteSpace(node?.GetValue<string>()) ? null : node!.GetValue<string>().Trim(); }
        catch { return null; }
    }

    private static decimal? DecimalValue(JsonNode? node)
    {
        try { return node?.GetValue<decimal>(); } catch { }
        return decimal.TryParse(StringValue(node), NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static int? PercentValue(JsonNode? node)
    {
        var value = DecimalValue(node);
        return value is null ? null : (int)Math.Clamp(Math.Round(value.Value), 0, 100);
    }

    private static int? ProductUsagePercent(JsonNode? config)
    {
        if (config?["productUsage"] is not JsonArray products) return null;
        foreach (var row in products.OfType<JsonObject>())
            if (string.Equals(StringValue(row["product"]), "GrokBuild", StringComparison.OrdinalIgnoreCase))
                return PercentValue(row["usagePercent"]);
        return null;
    }

    private static long? MoneyValue(JsonNode? node)
    {
        var value = DecimalValue(node);
        return value is null ? null : (long)Math.Round(value.Value);
    }

    private static DateTimeOffset? DateValue(JsonNode? node)
    {
        var value = StringValue(node);
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
    }

    private static int JsonInt(JsonNode? node)
    {
        try { return node?.GetValue<int>() ?? 0; } catch { }
        try { return checked((int)(node?.GetValue<long>() ?? 0)); } catch { return 0; }
    }

    private static void Stop(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                try { process.StandardInput.Close(); } catch { }
                if (!process.WaitForExit(1200)) process.Kill(entireProcessTree: true);
            }
        }
        catch { }
        process.Dispose();
    }

    public static bool TryGetTrustedLoginUrl(string text, out string url)
    {
        foreach (Match match in UrlRegex().Matches(text))
        {
            var candidate = match.Value.TrimEnd('.', ',', ';', ')', ']', '}', '>', '"');
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var parsed)
                || parsed.Scheme != Uri.UriSchemeHttps
                || parsed.UserInfo.Length != 0
                || !IsTrustedLoginHost(parsed.Host))
                continue;
            url = parsed.AbsoluteUri;
            return true;
        }
        url = string.Empty;
        return false;
    }

    public static string RedactLoginOutput(string text) =>
        UrlRegex().Replace(text, match =>
        {
            var candidate = match.Value.TrimEnd('.', ',', ';', ')', ']', '}', '>', '"');
            return TryGetTrustedLoginUrl(candidate, out _) ? "[xAI sign-in link]" : "[link omitted]";
        });

    private static bool IsTrustedLoginHost(string host) =>
        host.Equals("x.ai", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".x.ai", StringComparison.OrdinalIgnoreCase)
        || host.Equals("grok.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".grok.com", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"\x1B(?:[@-Z\\-_]|\[[0-?]*[ -/]*[@-~])", RegexOptions.Compiled)]
    private static partial Regex AnsiRegex();

    [GeneratedRegex(@"https://[^\s\x1b]+", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex UrlRegex();
}
