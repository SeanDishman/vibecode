using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VibeCode.Services;

public sealed class GrokChatBulkDeleteResult
{
    public bool Success { get; init; }
    public int Deleted { get; init; }
    /// <summary>grok.com answered the Cloudflare challenge instead of the API - the caller needs a clean-IP proxy.</summary>
    public bool NeedsProxy { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Deletes every top-level chat on one Grok account by calling the same grok.com REST endpoints the web app uses,
/// authenticated with the account's stored CLI bearer token. The token IS accepted on /rest/app-chat/conversations,
/// but grok.com is fronted by Cloudflare, which 403-challenges most home IPs; an optional proxy routes the calls
/// through a clean IP so the challenge never fires. Projects/workspaces are intentionally out of scope - the CLI
/// token lacks the workspaces scope, and those need the account's full web session.
/// </summary>
public static class GrokChatBulkDeleteService
{
    private const string ConversationsBase = "https://grok.com/rest/app-chat/conversations";
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    public static async Task<GrokChatBulkDeleteResult> DeleteAllAsync(
        string authFilePath, string? proxy, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        if (!TryReadCredential(authFilePath, out var key, out var userId))
            return new GrokChatBulkDeleteResult { Error = "Couldn't read this account's saved Grok login." };

        var handler = new HttpClientHandler { UseProxy = false, AutomaticDecompression = DecompressionMethods.All };
        if (!string.IsNullOrWhiteSpace(proxy))
        {
            if (!TryBuildProxy(proxy!, out var webProxy, out var proxyError))
                return new GrokChatBulkDeleteResult { Error = proxyError };
            handler.Proxy = webProxy;
            handler.UseProxy = true;
        }

        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        try
        {
            var deleted = 0;
            for (var round = 0; round < 5000; round++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (ids, listError, cloudflare) = await ListPageAsync(http, key, userId, cancellationToken);
                if (cloudflare)
                    return new GrokChatBulkDeleteResult
                    {
                        NeedsProxy = string.IsNullOrWhiteSpace(proxy),
                        Error = string.IsNullOrWhiteSpace(proxy)
                            ? "grok.com blocked the request behind Cloudflare (your connection's IP is challenged). Add a proxy to route through a clean IP."
                            : "grok.com blocked the request behind Cloudflare even through the proxy - the proxy IP is also challenged. Try a different proxy.",
                    };
                if (listError is not null)
                    return new GrokChatBulkDeleteResult { Deleted = deleted, Error = listError };
                if (ids.Count == 0) break;

                var progressedThisRound = false;
                foreach (var id in ids)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (await DeleteOneAsync(http, key, userId, id, cancellationToken))
                    {
                        deleted++;
                        progressedThisRound = true;
                        if (deleted % 10 == 0) progress?.Report(deleted);
                    }
                }
                // A full page that refused to delete would otherwise spin forever.
                if (!progressedThisRound)
                    return new GrokChatBulkDeleteResult
                    {
                        Deleted = deleted,
                        Error = $"Stopped after deleting {deleted}: grok.com refused to delete the remaining chats.",
                    };
            }
            progress?.Report(deleted);
            return new GrokChatBulkDeleteResult { Success = true, Deleted = deleted };
        }
        catch (OperationCanceledException)
        {
            return new GrokChatBulkDeleteResult { Error = "Deletion cancelled or timed out." };
        }
        catch (HttpRequestException ex)
        {
            return new GrokChatBulkDeleteResult { Error = "Network error talking to grok.com: " + ex.Message };
        }
    }

    private static void AddHeaders(HttpRequestMessage request, string key, string userId)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Headers.TryAddWithoutValidation("X-XAI-Token-Auth", "xai-grok-cli");
        request.Headers.TryAddWithoutValidation("x-userid", userId);
        request.Headers.TryAddWithoutValidation("x-grok-client-version", "vibecode-1.0");
        request.Headers.TryAddWithoutValidation("x-grok-client-mode", "headless");
        request.Headers.UserAgent.ParseAdd(UserAgent);
        request.Headers.Accept.ParseAdd("application/json");
    }

    private static async Task<(List<string> Ids, string? Error, bool Cloudflare)> ListPageAsync(
        HttpClient http, string key, string userId, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ConversationsBase + "?pageSize=100");
        AddHeaders(request, key, userId);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, token)
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Forbidden && LooksLikeCloudflare(body))
            return (new List<string>(), null, true);
        if (!response.IsSuccessStatusCode)
            return (new List<string>(), $"grok.com returned HTTP {(int)response.StatusCode} when listing chats.", false);

        var ids = new List<string>();
        try
        {
            if (JsonNode.Parse(body)?["conversations"] is JsonArray conversations)
                foreach (var conversation in conversations)
                    if ((string?)conversation?["conversationId"] is { Length: > 0 } id)
                        ids.Add(id);
        }
        catch (JsonException)
        {
            return (ids, "grok.com returned an unreadable chat list.", false);
        }
        return (ids, null, false);
    }

    private static async Task<bool> DeleteOneAsync(
        HttpClient http, string key, string userId, string conversationId, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, ConversationsBase + "/" + Uri.EscapeDataString(conversationId));
        AddHeaders(request, key, userId);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }

    private static bool LooksLikeCloudflare(string body) =>
        body.Contains("Just a moment", StringComparison.OrdinalIgnoreCase)
        || body.Contains("cf-browser-verification", StringComparison.OrdinalIgnoreCase)
        || body.Contains("challenge-platform", StringComparison.OrdinalIgnoreCase);

    /// <summary>Structured proxy: scheme kind ("http" or "socks5"), host, port, and optional credentials.</summary>
    public sealed record ProxyParts(string Kind, string Host, int Port, string? User, string? Pass);

    /// <summary>
    /// Parses a saved proxy in either a full URI (<c>http://user:pass@host:port</c>, <c>socks5://host:port</c>) or the
    /// legacy <c>host:port</c> / <c>host:port@user:pass</c> shorthand. URI credentials are percent-decoded, so a
    /// password containing '@' or ':' round-trips safely. Returns null when the string is blank or unparseable.
    /// </summary>
    public static ProxyParts? ParseProxy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        if (text.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host)) return null;
            string? user = null, pass = null;
            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                var split = uri.UserInfo.IndexOf(':');
                if (split >= 0)
                {
                    user = Uri.UnescapeDataString(uri.UserInfo[..split]);
                    pass = Uri.UnescapeDataString(uri.UserInfo[(split + 1)..]);
                }
                else user = Uri.UnescapeDataString(uri.UserInfo);
            }
            var kind = uri.Scheme.StartsWith("socks", StringComparison.OrdinalIgnoreCase) ? "socks5" : "http";
            return new ProxyParts(kind, uri.Host, uri.Port <= 0 ? 0 : uri.Port, NullIfEmpty(user), NullIfEmpty(pass));
        }
        string? legacyUser = null, legacyPass = null;
        var hostPort = text;
        var at = text.IndexOf('@');
        if (at >= 0)
        {
            var creds = text[(at + 1)..];
            hostPort = text[..at];
            var colon = creds.IndexOf(':');
            if (colon >= 0) { legacyUser = creds[..colon]; legacyPass = creds[(colon + 1)..]; }
            else legacyUser = creds;
        }
        var lastColon = hostPort.LastIndexOf(':');
        if (lastColon <= 0 || !int.TryParse(hostPort[(lastColon + 1)..], out var legacyPort)) return null;
        return new ProxyParts("http", hostPort[..lastColon], legacyPort, NullIfEmpty(legacyUser), NullIfEmpty(legacyPass));
    }

    /// <summary>Builds the canonical stored form (a URI with percent-encoded credentials) from structured fields.</summary>
    public static string BuildProxyString(ProxyParts parts)
    {
        var scheme = parts.Kind.Equals("socks5", StringComparison.OrdinalIgnoreCase) ? "socks5" : "http";
        var userInfo = "";
        if (!string.IsNullOrEmpty(parts.User))
        {
            userInfo = Uri.EscapeDataString(parts.User);
            if (!string.IsNullOrEmpty(parts.Pass)) userInfo += ":" + Uri.EscapeDataString(parts.Pass);
            userInfo += "@";
        }
        return $"{scheme}://{userInfo}{parts.Host}:{parts.Port}";
    }

    /// <summary>Turns a saved proxy string into an authenticated HTTP or SOCKS5 proxy for the handler.</summary>
    internal static bool TryBuildProxy(string value, out IWebProxy proxy, out string? error)
    {
        proxy = null!;
        error = null;
        var parts = ParseProxy(value);
        if (parts is null || string.IsNullOrWhiteSpace(parts.Host))
        {
            error = "Proxy must be host:port (optionally with a username and password).";
            return false;
        }
        if (parts.Port is < 1 or > 65535)
        {
            error = "Proxy port is not valid.";
            return false;
        }
        var scheme = parts.Kind.Equals("socks5", StringComparison.OrdinalIgnoreCase) ? "socks5" : "http";
        var web = new WebProxy($"{scheme}://{parts.Host}:{parts.Port}");
        if (!string.IsNullOrEmpty(parts.User)) web.Credentials = new NetworkCredential(parts.User, parts.Pass ?? "");
        proxy = web;
        return true;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>Reads the (key, user_id) bearer credential from a Grok auth.json, newest entry first.</summary>
    public static bool TryReadCredential(string authFilePath, out string bearerKey, out string userId)
    {
        bearerKey = "";
        userId = "";
        try
        {
            if (!File.Exists(authFilePath)) return false;
            if (JsonNode.Parse(File.ReadAllText(authFilePath)) is not JsonObject root) return false;
            var entries = new List<JsonObject>();
            if (root["key"] is not null) entries.Add(root);
            entries.AddRange(root.Select(x => x.Value).OfType<JsonObject>().Where(x => x["key"] is not null));
            foreach (var entry in entries.OrderByDescending(x => (string?)x["create_time"] ?? ""))
            {
                var key = (string?)entry["key"];
                var uid = (string?)entry["user_id"];
                if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(uid))
                {
                    bearerKey = key;
                    userId = uid;
                    return true;
                }
            }
        }
        catch { /* fall through to false */ }
        return false;
    }
}
