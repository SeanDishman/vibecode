using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using VibeCode.Protocol;

namespace VibeCode.Services;

public sealed class CodexLoginResult
{
    public bool Success { get; init; }
    public string? AccountId { get; init; }
    public string? Name { get; init; }
    public string? Email { get; init; }
    public string? Plan { get; init; }
    public string? Error { get; init; }
}

public sealed class CodexAccountState
{
    public bool IsSignedIn { get; init; }
    public string? AccountId { get; init; }
    public string? Name { get; init; }
    public string? Email { get; init; }
    public string? Plan { get; init; }
    public int? SessionPercent { get; init; }
    public int? WeekPercent { get; init; }
    public bool UsageReadSucceeded { get; init; }
    public List<CodexUsageLimit> UsageLimits { get; init; } = new();
    public DateTimeOffset? UsageUpdatedAt { get; init; }
    public int? AvailableResetCredits { get; init; }
    public bool? HasCredits { get; init; }
    public bool? UnlimitedCredits { get; init; }
    public string? CreditBalance { get; init; }
    public CodexSpendLimit? SpendLimit { get; init; }
    public string? RateLimitStatus { get; init; }
    public string? Error { get; init; }
}

/// <summary>One subscription window returned by Codex app-server's account/rateLimits/read endpoint.</summary>
public sealed class CodexUsageLimit
{
    public string LimitId { get; set; } = "";
    public string Label { get; set; } = "";
    public int Percent { get; set; }
    public int? WindowDurationMinutes { get; set; }
    public long? ResetsAtUnixSeconds { get; set; }
    public string? ReachedType { get; set; }

    [JsonIgnore] public int RemainingPercent => Math.Clamp(100 - Percent, 0, 100);
    [JsonIgnore] public bool HasReset => ResetAt is not null;
    [JsonIgnore] public bool HasStatus => !string.IsNullOrWhiteSpace(StatusDisplay);

    [JsonIgnore]
    public string WindowDisplay
    {
        get
        {
            if (WindowDurationMinutes is not { } minutes || minutes <= 0) return "";
            if (minutes % 1440 == 0) return Unit(minutes / 1440, "day");
            if (minutes % 60 == 0) return Unit(minutes / 60, "hour");
            return Unit(minutes, "minute");
        }
    }

    [JsonIgnore]
    public string DetailDisplay
    {
        get
        {
            var remaining = $"{RemainingPercent}% remaining";
            return string.IsNullOrWhiteSpace(WindowDisplay) ? remaining : $"{remaining} · {WindowDisplay}";
        }
    }

    [JsonIgnore]
    public string ResetDisplay
    {
        get
        {
            if (ResetAt is not { } reset) return "";
            var local = reset.ToLocalTime();
            var span = reset - DateTimeOffset.Now;
            var relative = span <= TimeSpan.Zero
                ? "soon"
                : span.TotalHours < 1
                    ? $"in {Math.Max(1, (int)Math.Round(span.TotalMinutes))}m"
                    : span.TotalDays < 2
                        ? $"in {(int)Math.Floor(span.TotalHours)}h {span.Minutes}m"
                        : $"in {(int)Math.Floor(span.TotalDays)}d {span.Hours}h";
            var absolute = local.Date == DateTimeOffset.Now.Date
                ? local.ToString("h:mm tt")
                : local.Year == DateTimeOffset.Now.Year
                    ? local.ToString("MMM d, h:mm tt")
                    : local.ToString("MMM d yyyy, h:mm tt");
            return $"{relative} · {absolute}";
        }
    }

    [JsonIgnore]
    public string StatusDisplay => ReachedType switch
    {
        "rate_limit_reached" => "Rate limit reached",
        "workspace_owner_credits_depleted" or "workspace_member_credits_depleted" => "Workspace credits depleted",
        "workspace_owner_usage_limit_reached" or "workspace_member_usage_limit_reached" => "Workspace usage limit reached",
        _ => "",
    };

    [JsonIgnore]
    private DateTimeOffset? ResetAt
    {
        get
        {
            if (ResetsAtUnixSeconds is not { } value) return null;
            try { return DateTimeOffset.FromUnixTimeSeconds(value); }
            catch (ArgumentOutOfRangeException) { return null; }
        }
    }

    private static string Unit(int value, string name) => $"{value}-{name} window";
}

/// <summary>Optional workspace spend-control information included alongside Codex rate limits.</summary>
public sealed class CodexSpendLimit
{
    public string Limit { get; set; } = "";
    public string Used { get; set; } = "";
    public int RemainingPercent { get; set; }
    public long ResetsAtUnixSeconds { get; set; }

    [JsonIgnore] public string Summary => $"{Used} of {Limit} used · {Math.Clamp(RemainingPercent, 0, 100)}% remaining";
    [JsonIgnore]
    public string ResetDisplay
    {
        get
        {
            try
            {
                var reset = DateTimeOffset.FromUnixTimeSeconds(ResetsAtUnixSeconds).ToLocalTime();
                return reset.Year == DateTimeOffset.Now.Year
                    ? reset.ToString("MMM d, h:mm tt")
                    : reset.ToString("MMM d yyyy, h:mm tt");
            }
            catch (ArgumentOutOfRangeException) { return ""; }
        }
    }
}

/// <summary>
/// Owns VibeCode's OpenAI account lifecycle through Codex app-server's supported managed ChatGPT flow.
/// The app-server runs with VibeCode's isolated CODEX_HOME, so login/refresh never changes the standalone
/// Codex CLI or desktop app account.
/// </summary>
public static class CodexAccountLoginService
{
    private static readonly SemaphoreSlim AccountGate = new(1, 1);

    public static Task<CodexLoginResult> LoginAsync(Action<string> openBrowser, CancellationToken cancellationToken) =>
        LoginAsync(CodexEnvironment.HomeDirectory, openBrowser, cancellationToken);

    /// <summary>Run managed ChatGPT sign-in inside one isolated account home.</summary>
    public static async Task<CodexLoginResult> LoginAsync(
        string homeDirectory, Action<string> openBrowser, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var token = timeout.Token;
        var gateHeld = false;
        Process? process = null;
        try
        {
            await AccountGate.WaitAsync(token);
            gateHeld = true;
            process = StartServer(homeDirectory);
            await InitializeAsync(process, token);

            await WriteAsync(process, new JsonObject
            {
                ["id"] = 2,
                ["method"] = "account/login/start",
                ["params"] = new JsonObject
                {
                    ["type"] = "chatgpt",
                    ["useHostedLoginSuccessPage"] = true,
                    ["appBrand"] = "chatgpt",
                },
            }, token);
            var loginStart = await ReadResponseAsync(process, 2, token);
            var login = loginStart["result"];
            var loginId = login?["loginId"]?.GetValue<string>();
            var authUrl = login?["authUrl"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(loginId) || string.IsNullOrWhiteSpace(authUrl))
                throw new InvalidOperationException("Codex did not return a browser sign-in URL.");

            openBrowser(authUrl);
            while (true)
            {
                var message = await ReadMessageAsync(process, token);
                if (message["method"]?.GetValue<string>() != "account/login/completed") continue;
                var p = message["params"];
                if (p?["loginId"]?.GetValue<string>() != loginId) continue;
                if (p["success"]?.GetValue<bool>() != true)
                    throw new InvalidOperationException(p["error"]?.GetValue<string>() ?? "Codex sign-in did not complete.");
                break;
            }

            await WriteAsync(process, new JsonObject
            {
                ["id"] = 3,
                ["method"] = "account/read",
                ["params"] = new JsonObject { ["refreshToken"] = true },
            }, token);
            var accountResponse = await ReadResponseAsync(process, 3, token);
            var account = accountResponse["result"]?["account"];
            var stored = ReadStoredIdentity(homeDirectory);
            return new CodexLoginResult
            {
                Success = true,
                AccountId = stored.AccountId,
                Name = stored.Name,
                Email = account?["email"]?.GetValue<string>() ?? stored.Email,
                Plan = account?["planType"]?.GetValue<string>(),
            };
        }
        catch (OperationCanceledException)
        {
            return new CodexLoginResult { Error = cancellationToken.IsCancellationRequested ? "Sign-in cancelled." : "Sign-in timed out." };
        }
        catch (Exception ex)
        {
            return new CodexLoginResult { Error = ex.Message };
        }
        finally
        {
            if (process is not null) StopServer(process);
            if (gateHeld) AccountGate.Release();
        }
    }

    /// <summary>
    /// Read VibeCode's OpenAI account and optionally force the official managed-token refresh. This is safe to call
    /// periodically; app-server serializes token persistence and returns the account identity after refresh.
    /// </summary>
    public static Task<CodexAccountState> ReadAsync(bool forceRefresh, CancellationToken cancellationToken = default) =>
        ReadAsync(CodexEnvironment.HomeDirectory, forceRefresh, cancellationToken);

    public static async Task<CodexAccountState> ReadAsync(
        string homeDirectory, bool forceRefresh, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        var gateHeld = false;
        Process? process = null;
        try
        {
            await AccountGate.WaitAsync(token);
            gateHeld = true;
            process = StartServer(homeDirectory);
            await InitializeAsync(process, token);
            await WriteAsync(process, new JsonObject
            {
                ["id"] = 2,
                ["method"] = "account/read",
                ["params"] = new JsonObject { ["refreshToken"] = forceRefresh },
            }, token);
            var response = await ReadResponseAsync(process, 2, token);
            var account = response["result"]?["account"];
            var usage = account is null ? new RateLimitUsage() : await TryReadRateLimitsAsync(process, 3, token);
            var stored = ReadStoredIdentity(homeDirectory);
            return new CodexAccountState
            {
                IsSignedIn = account is not null,
                AccountId = account is null ? null : stored.AccountId,
                Name = account is null ? null : stored.Name,
                Email = account?["email"]?.GetValue<string>() ?? (account is null ? null : stored.Email),
                Plan = account?["planType"]?.GetValue<string>() ?? usage.Plan,
                SessionPercent = usage.SessionPercent,
                WeekPercent = usage.WeekPercent,
                UsageReadSucceeded = usage.ReadSucceeded,
                UsageLimits = usage.Limits,
                UsageUpdatedAt = usage.UpdatedAt,
                AvailableResetCredits = usage.AvailableResetCredits,
                HasCredits = usage.HasCredits,
                UnlimitedCredits = usage.UnlimitedCredits,
                CreditBalance = usage.CreditBalance,
                SpendLimit = usage.SpendLimit,
                RateLimitStatus = usage.RateLimitStatus,
            };
        }
        catch (OperationCanceledException)
        {
            return new CodexAccountState { Error = cancellationToken.IsCancellationRequested ? "Account check cancelled." : "Account refresh timed out." };
        }
        catch (Exception ex)
        {
            return new CodexAccountState { Error = ex.Message };
        }
        finally
        {
            if (process is not null) StopServer(process);
            if (gateHeld) AccountGate.Release();
        }
    }

    /// <summary>
    /// Inspect the non-secret identity fields already present in an auth file without starting Codex or refreshing a
    /// token. Used to adopt the pre-multi-account VibeCode home before a second login can replace it.
    /// </summary>
    public static CodexAccountState InspectStoredAccount(string homeDirectory)
    {
        var stored = ReadStoredIdentity(homeDirectory);
        return new CodexAccountState
        {
            IsSignedIn = !string.IsNullOrWhiteSpace(stored.AccountId),
            AccountId = stored.AccountId,
            Name = stored.Name,
            Email = stored.Email,
        };
    }

    private static Process StartServer(string homeDirectory)
    {
        var process = new Process { StartInfo = CodexEnvironment.CreateAppServerStartInfo(homeDirectory: homeDirectory) };
        process.Start();
        ProcessJob.Assign(process);
        _ = process.StandardError.ReadToEndAsync(); // drain stderr so a noisy CLI can never block on a full pipe
        return process;
    }

    private sealed class RateLimitUsage
    {
        public int? SessionPercent { get; init; }
        public int? WeekPercent { get; init; }
        public string? Plan { get; init; }
        public bool ReadSucceeded { get; init; }
        public List<CodexUsageLimit> Limits { get; init; } = new();
        public DateTimeOffset? UpdatedAt { get; init; }
        public int? AvailableResetCredits { get; init; }
        public bool? HasCredits { get; init; }
        public bool? UnlimitedCredits { get; init; }
        public string? CreditBalance { get; init; }
        public CodexSpendLimit? SpendLimit { get; init; }
        public string? RateLimitStatus { get; init; }
    }

    /// <summary>Read Codex's official subscription windows. A usage failure is deliberately non-fatal: account/read
    /// still supplied an authoritative signed-in identity, and the next background refresh can retry the numbers.</summary>
    private static async Task<RateLimitUsage> TryReadRateLimitsAsync(Process process, int id, CancellationToken token)
    {
        try
        {
            await WriteAsync(process, new JsonObject
            {
                ["id"] = id,
                ["method"] = "account/rateLimits/read",
                ["params"] = null,
            }, token);
            var response = await ReadResponseAsync(process, id, token);
            var result = response["result"];
            var snapshot = result?["rateLimits"];
            int? sessionPercent = null, weekPercent = null;
            var limits = new List<CodexUsageLimit>();
            var mainLimitId = StringValue(snapshot?["limitId"]) ?? "codex";
            ApplyBucket(snapshot, mainLimitId, isMain: true);
            if (result?["rateLimitsByLimitId"] is JsonObject buckets)
            {
                foreach (var (key, bucket) in buckets)
                {
                    if (bucket is null) continue;
                    var bucketId = StringValue(bucket["limitId"]) ?? key;
                    if (string.Equals(bucketId, mainLimitId, StringComparison.OrdinalIgnoreCase)) continue;
                    ApplyBucket(bucket, bucketId, isMain: false);
                }
            }

            var credits = snapshot?["credits"];
            var spend = snapshot?["individualLimit"];
            return new RateLimitUsage
            {
                SessionPercent = sessionPercent,
                WeekPercent = weekPercent,
                Plan = snapshot?["planType"]?.GetValue<string>(),
                ReadSucceeded = true,
                Limits = limits,
                UpdatedAt = DateTimeOffset.Now,
                AvailableResetCredits = JsonInt(result?["rateLimitResetCredits"]?["availableCount"]),
                HasCredits = JsonBool(credits?["hasCredits"]),
                UnlimitedCredits = JsonBool(credits?["unlimited"]),
                CreditBalance = StringValue(credits?["balance"]),
                SpendLimit = ParseSpendLimit(spend),
                RateLimitStatus = StringValue(snapshot?["rateLimitReachedType"]),
            };

            void ApplyBucket(JsonNode? bucket, string fallbackId, bool isMain)
            {
                if (bucket is null) return;
                var limitId = StringValue(bucket["limitId"]) ?? fallbackId;
                var bucketName = isMain ? "" : FriendlyBucketName(StringValue(bucket["limitName"]) ?? limitId);
                var reachedType = StringValue(bucket["rateLimitReachedType"]);
                ApplyWindow(bucket["primary"], primaryFallback: true);
                ApplyWindow(bucket["secondary"], primaryFallback: false);

                void ApplyWindow(JsonNode? window, bool primaryFallback)
                {
                    var percent = JsonInt(window?["usedPercent"]);
                    if (percent is null) return;
                    var durationMinutes = JsonInt(window?["windowDurationMins"]);
                    var isWeek = durationMinutes is >= 2880 || ((durationMinutes is null or <= 0) && !primaryFallback);
                    var windowLabel = isWeek ? "This week" : "Session";
                    var clampedPercent = Math.Clamp(percent.Value, 0, 100);
                    if (isMain)
                    {
                        if (isWeek) weekPercent ??= clampedPercent;
                        else sessionPercent ??= clampedPercent;
                    }
                    else
                    {
                        windowLabel = $"{bucketName} · {windowLabel.ToLowerInvariant()}";
                    }

                    limits.Add(new CodexUsageLimit
                    {
                        LimitId = limitId,
                        Label = windowLabel,
                        Percent = clampedPercent,
                        WindowDurationMinutes = durationMinutes,
                        ResetsAtUnixSeconds = JsonLong(window?["resetsAt"]),
                        ReachedType = reachedType,
                    });
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { return new RateLimitUsage(); }
    }

    private static int? JsonInt(JsonNode? node)
    {
        if (node is null) return null;
        try { return node.GetValue<int>(); } catch { /* wider numeric representation */ }
        try { return checked((int)node.GetValue<long>()); } catch { /* JSON number may be floating-point */ }
        try { return checked((int)Math.Round(node.GetValue<double>())); } catch { return null; }
    }

    private static long? JsonLong(JsonNode? node)
    {
        if (node is null) return null;
        try { return node.GetValue<long>(); } catch { /* narrower numeric representation */ }
        try { return node.GetValue<int>(); } catch { /* JSON number may be floating-point */ }
        try { return checked((long)Math.Round(node.GetValue<double>())); } catch { return null; }
    }

    private static bool? JsonBool(JsonNode? node)
    {
        if (node is null) return null;
        try { return node.GetValue<bool>(); } catch { return null; }
    }

    private static CodexSpendLimit? ParseSpendLimit(JsonNode? node)
    {
        if (node is null
            || StringValue(node["limit"]) is not { } limit
            || StringValue(node["used"]) is not { } used
            || JsonInt(node["remainingPercent"]) is not { } remaining
            || JsonLong(node["resetsAt"]) is not { } resetsAt)
            return null;
        return new CodexSpendLimit
        {
            Limit = limit,
            Used = used,
            RemainingPercent = remaining,
            ResetsAtUnixSeconds = resetsAt,
        };
    }

    private static string FriendlyBucketName(string value)
    {
        var words = value.Replace('_', ' ').Replace('-', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 1 && words[0].Equals("codex", StringComparison.OrdinalIgnoreCase)) words = words[1..];
        if (words.Length == 0) return "Additional limit";
        return string.Join(" ", words.Select(word =>
            word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..]));
    }

    private sealed record StoredIdentity(string? AccountId, string? Name, string? Email);

    /// <summary>Extract identity claims only. Token strings never leave this method and are never logged.</summary>
    private static StoredIdentity ReadStoredIdentity(string homeDirectory)
    {
        try
        {
            var authPath = Path.Combine(homeDirectory, "auth.json");
            var tokens = JsonNode.Parse(File.ReadAllText(authPath))?["tokens"];
            var idClaims = ClaimsFromJwt(StringValue(tokens?["id_token"]));
            var accessClaims = ClaimsFromJwt(StringValue(tokens?["access_token"]));
            var authClaims = idClaims?["https://api.openai.com/auth"];
            var profileClaims = accessClaims?["https://api.openai.com/profile"];
            var email = StringValue(idClaims?["email"])
                        ?? StringValue(profileClaims?["email"])
                        ?? StringValue(accessClaims?["email"]);
            var name = StringValue(idClaims?["name"])
                       ?? StringValue(profileClaims?["name"])
                       ?? StringValue(accessClaims?["name"]);
            var id = StringValue(tokens?["account_id"])
                     ?? StringValue(authClaims?["chatgpt_account_id"])
                     ?? StringValue(authClaims?["chatgpt_user_id"])
                     ?? StringValue(idClaims?["sub"])
                     ?? StringValue(accessClaims?["sub"]);
            if (string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(email))
                id = "email-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(email.Trim().ToLowerInvariant()))).ToLowerInvariant();
            return new StoredIdentity(id, name, email);
        }
        catch { return new StoredIdentity(null, null, null); }
    }

    private static JsonNode? ClaimsFromJwt(string? jwt)
    {
        if (string.IsNullOrWhiteSpace(jwt)) return null;
        var pieces = jwt.Split('.');
        if (pieces.Length < 2) return null;
        try
        {
            var payload = pieces[1].Replace('-', '+').Replace('_', '/');
            payload += new string('=', (4 - payload.Length % 4) % 4);
            return JsonNode.Parse(Convert.FromBase64String(payload));
        }
        catch { return null; }
    }

    private static string? StringValue(JsonNode? node)
    {
        try
        {
            var value = node?.GetValue<string>();
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch { return null; }
    }

    private static async Task InitializeAsync(Process process, CancellationToken token)
    {
        await WriteAsync(process, new JsonObject
        {
            ["id"] = 1,
            ["method"] = "initialize",
            ["params"] = new JsonObject
            {
                ["clientInfo"] = new JsonObject
                {
                    ["name"] = "vibecode",
                    ["title"] = "VibeCode",
                    ["version"] = "1.0.0",
                },
            },
        }, token);
        await ReadResponseAsync(process, 1, token);
        await WriteAsync(process, new JsonObject { ["method"] = "initialized", ["params"] = new JsonObject() }, token);
    }

    private static void StopServer(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.StandardInput.Close();
                if (!process.WaitForExit(1000)) process.Kill(entireProcessTree: true);
            }
        }
        catch { /* process already gone */ }
        finally { process.Dispose(); }
    }

    private static async Task<JsonObject> ReadResponseAsync(Process process, int id, CancellationToken token)
    {
        while (true)
        {
            var message = await ReadMessageAsync(process, token);
            if (message["id"]?.GetValue<int>() != id) continue;
            if (message["error"] is { } error)
                throw new InvalidOperationException(error["message"]?.GetValue<string>() ?? error.ToJsonString());
            return message;
        }
    }

    private static async Task<JsonObject> ReadMessageAsync(Process process, CancellationToken token)
    {
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(token);
            if (line is null) throw new InvalidOperationException("Codex app-server closed during sign-in.");
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (JsonNode.Parse(line) is JsonObject message) return message;
        }
    }

    private static async Task WriteAsync(Process process, JsonObject message, CancellationToken token)
    {
        var json = message.ToJsonString(new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        await process.StandardInput.WriteLineAsync(json.AsMemory(), token);
        await process.StandardInput.FlushAsync(token);
    }
}
