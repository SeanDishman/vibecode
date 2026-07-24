using System.Globalization;
using System.Text.Json;

namespace VibeCode.Services;

/// <summary>A browser cookie held only for one Grok sign-in attempt. Values are always redacted.</summary>
public sealed class GrokBrowserCookie
{
    internal GrokBrowserCookie(string name, string value, string domain, string path, bool isSecure,
        bool isHttpOnly, string? sameSite, DateTimeOffset? expires)
    {
        Name = name;
        Value = value;
        Domain = domain;
        Path = path;
        IsSecure = isSecure;
        IsHttpOnly = isHttpOnly;
        SameSite = sameSite;
        Expires = expires;
    }

    public string Name { get; }
    public string Value { get; private set; }
    public string Domain { get; }
    public string Path { get; }
    public bool IsSecure { get; }
    public bool IsHttpOnly { get; }
    public string? SameSite { get; }
    public DateTimeOffset? Expires { get; }

    internal GrokBrowserCookie ForDomain(string domain) =>
        new(Name, Value, domain, Path, IsSecure, IsHttpOnly, SameSite, Expires);

    internal void ClearSecret() => Value = string.Empty;
    public override string ToString() => $"{Name} ({Domain}) [value redacted]";
}

public sealed class GrokCookieImportResult : IDisposable
{
    private readonly List<GrokBrowserCookie> _cookies;

    internal GrokCookieImportResult(List<GrokBrowserCookie> cookies, string? error)
    {
        _cookies = cookies;
        Error = error;
    }

    public bool Success => Error is null && _cookies.Count > 0;
    public string? Error { get; }
    public IReadOnlyList<GrokBrowserCookie> Cookies => _cookies;
    public int CookieCount => _cookies.Count;

    public void Dispose()
    {
        foreach (var cookie in _cookies) cookie.ClearSecret();
        _cookies.Clear();
    }
}

/// <summary>
/// Reads Cookie-Editor JSON, Netscape exports, and Cookie headers without persisting or logging secrets.
/// Only xAI/Grok authentication cookies are retained.
/// </summary>
public static class GrokCookieImportService
{
    private const int MaximumInputLength = 256 * 1024;
    private const int MaximumCookieCount = 64;
    private const int MaximumCookieValueLength = 32 * 1024;

    private static readonly HashSet<string> RetainedCookieNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "sso", "sso-rw", "oauth", "redirect", "cf_clearance", "__cf_bm",
    };

    private static readonly HashSet<string> SessionCookieNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "sso", "sso-rw",
    };

    public static GrokCookieImportResult Parse(string? input, DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(input))
            return Failure("Paste a Cookie-Editor JSON export, Netscape cookie export, or Cookie header.");
        if (input.Length > MaximumInputLength)
            return Failure("The cookie export is too large. Export only cookies for grok.com and x.ai.");

        var parsed = new List<GrokBrowserCookie>();
        var instant = now ?? DateTimeOffset.UtcNow;
        try
        {
            var trimmed = input.TrimStart();
            if (trimmed.StartsWith("[", StringComparison.Ordinal)
                || trimmed.StartsWith("{", StringComparison.Ordinal))
                ParseJson(input, instant, parsed);
            else if (input.Contains('\t'))
                ParseNetscape(input, instant, parsed);
            else
                ParseCookieHeader(input, parsed);
        }
        catch (JsonException)
        {
            Clear(parsed);
            return Failure("The JSON cookie export is invalid or incomplete.");
        }
        catch
        {
            Clear(parsed);
            return Failure("The cookie export could not be read safely.");
        }

        var retained = Dedupe(parsed);
        Clear(parsed);
        if (retained.Count == 0)
            return Failure("No current Grok/xAI authentication cookies were found.");
        if (!retained.Any(cookie => SessionCookieNames.Contains(cookie.Name)))
        {
            Clear(retained);
            return Failure("The export is missing Grok's sso or sso-rw authentication cookie.");
        }

        // Some grok.com-only exports omit the parent-domain cookie used by accounts.x.ai/auth.x.ai.
        // Mirror only the two SSO cookies, never analytics or Cloudflare state.
        foreach (var cookie in retained
                     .Where(cookie => cookie.Domain == ".grok.com" && SessionCookieNames.Contains(cookie.Name))
                     .ToArray())
        {
            if (!retained.Any(existing => existing.Domain == ".x.ai"
                                          && string.Equals(existing.Name, cookie.Name, StringComparison.Ordinal)))
                retained.Add(cookie.ForDomain(".x.ai"));
        }

        if (retained.Count > MaximumCookieCount)
        {
            Clear(retained);
            return Failure("The cookie export contains too many authentication entries.");
        }
        return new GrokCookieImportResult(retained, null);
    }

    private static void ParseJson(string input, DateTimeOffset now, List<GrokBrowserCookie> output)
    {
        using var document = JsonDocument.Parse(input, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
            MaxDepth = 16,
        });
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray()) ParseJsonCookie(item, now, output);
            return;
        }
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("Cookie export root must be an object or array.");
        if (TryProperty(root, "cookies", out var cookies) && cookies.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in cookies.EnumerateArray()) ParseJsonCookie(item, now, output);
            return;
        }
        ParseJsonCookie(root, now, output);
    }

    private static void ParseJsonCookie(JsonElement item, DateTimeOffset now, List<GrokBrowserCookie> output)
    {
        if (item.ValueKind != JsonValueKind.Object) return;
        var name = StringProperty(item, "name");
        var value = StringProperty(item, "value");
        var domain = StringProperty(item, "domain") ?? StringProperty(item, "host") ?? ".grok.com";
        var path = StringProperty(item, "path") ?? "/";
        var secure = BoolProperty(item, "secure") ?? true;
        var httpOnly = BoolProperty(item, "httpOnly") ?? true;
        var sameSite = StringProperty(item, "sameSite");
        var session = BoolProperty(item, "session") ?? false;
        var expires = session ? null : ExpiryProperty(item);
        AddCookie(output, name, value, domain, path, secure, httpOnly, sameSite, expires, now);
    }

    private static void ParseNetscape(string input, DateTimeOffset now, List<GrokBrowserCookie> output)
    {
        foreach (var rawLine in input.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            var httpOnly = line.StartsWith("#HttpOnly_", StringComparison.OrdinalIgnoreCase);
            if (httpOnly) line = line["#HttpOnly_".Length..];
            else if (line.StartsWith('#')) continue;

            var fields = line.Split('\t');
            if (fields.Length < 7) continue;
            DateTimeOffset? expires = null;
            if (long.TryParse(fields[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
                && seconds > 0)
            {
                try { expires = DateTimeOffset.FromUnixTimeSeconds(seconds); }
                catch (ArgumentOutOfRangeException) { continue; }
            }
            AddCookie(output, fields[5], string.Join("\t", fields.Skip(6)), fields[0], fields[2],
                string.Equals(fields[3], "TRUE", StringComparison.OrdinalIgnoreCase),
                httpOnly, null, expires, now);
        }
    }

    private static void ParseCookieHeader(string input, List<GrokBrowserCookie> output)
    {
        var header = input.Trim();
        if (header.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase))
            header = header["Cookie:".Length..];
        foreach (var part in header.Split(new[] { ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0) continue;
            AddCookie(output, part[..separator], part[(separator + 1)..], ".grok.com", "/",
                true, true, null, null, DateTimeOffset.UtcNow);
        }
    }

    private static void AddCookie(List<GrokBrowserCookie> output, string? rawName, string? rawValue,
        string rawDomain, string rawPath, bool isSecure, bool isHttpOnly, string? sameSite,
        DateTimeOffset? expires, DateTimeOffset now)
    {
        var name = rawName?.Trim();
        if (string.IsNullOrEmpty(name) || !RetainedCookieNames.Contains(name)) return;
        if (name.Length > 256 || name.Any(ch => char.IsControl(ch) || ch is ';' or '=')) return;
        if (string.IsNullOrEmpty(rawValue) || rawValue.Length > MaximumCookieValueLength
                                             || rawValue.Any(ch => ch is '\r' or '\n')) return;
        var domain = NormalizeDomain(rawDomain);
        if (domain is null || expires is { } expiration && expiration <= now) return;
        var path = string.IsNullOrWhiteSpace(rawPath) || !rawPath.StartsWith('/')
            ? "/"
            : rawPath.Trim();
        output.Add(new GrokBrowserCookie(name, rawValue, domain, path, isSecure, isHttpOnly,
            NormalizeSameSite(sameSite), expires));
    }

    private static string? NormalizeDomain(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var domain = raw.Trim().TrimEnd('.').ToLowerInvariant();
        if (domain.StartsWith("#httponly_", StringComparison.OrdinalIgnoreCase))
            domain = domain["#httponly_".Length..];
        domain = domain.TrimStart('.');
        if (domain == "grok.com" || domain.EndsWith(".grok.com", StringComparison.Ordinal))
            return ".grok.com";
        if (domain == "x.ai" || domain.EndsWith(".x.ai", StringComparison.Ordinal))
            return ".x.ai";
        return null;
    }

    private static string? NormalizeSameSite(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.Trim().ToLowerInvariant() switch
        {
            "strict" => "strict",
            "lax" => "lax",
            "none" or "no_restriction" or "no-restriction" => "none",
            _ => null,
        };
    }

    private static List<GrokBrowserCookie> Dedupe(IEnumerable<GrokBrowserCookie> input)
    {
        var byKey = new Dictionary<string, GrokBrowserCookie>(StringComparer.Ordinal);
        foreach (var cookie in input)
        {
            var key = cookie.Domain + "\n" + cookie.Path + "\n" + cookie.Name;
            if (byKey.Remove(key, out var previous)) previous.ClearSecret();
            byKey[key] = cookie.ForDomain(cookie.Domain);
        }
        return byKey.Values.ToList();
    }

    private static DateTimeOffset? ExpiryProperty(JsonElement item)
    {
        foreach (var name in new[] { "expirationDate", "expiration", "expires" })
        {
            if (!TryProperty(item, name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
                return FromUnix(number);
            if (value.ValueKind != JsonValueKind.String) continue;
            var text = value.GetString();
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                return FromUnix(number);
            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
                return parsed;
        }
        return null;
    }

    private static DateTimeOffset? FromUnix(double value)
    {
        if (!double.IsFinite(value) || value <= 0) return null;
        if (value > 100_000_000_000d) value /= 1000d;
        try { return DateTimeOffset.FromUnixTimeSeconds(checked((long)value)); }
        catch (Exception) when (value > 0) { return null; }
    }

    private static bool TryProperty(JsonElement item, string name, out JsonElement value)
    {
        foreach (var property in item.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            value = property.Value;
            return true;
        }
        value = default;
        return false;
    }

    private static string? StringProperty(JsonElement item, string name)
    {
        if (!TryProperty(item, name, out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static bool? BoolProperty(JsonElement item, string name)
    {
        if (!TryProperty(item, name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.True) return true;
        if (value.ValueKind == JsonValueKind.False) return false;
        return value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed)
            ? parsed
            : null;
    }

    private static GrokCookieImportResult Failure(string error) => new(new List<GrokBrowserCookie>(), error);

    private static void Clear(IEnumerable<GrokBrowserCookie> cookies)
    {
        foreach (var cookie in cookies) cookie.ClearSecret();
    }
}
