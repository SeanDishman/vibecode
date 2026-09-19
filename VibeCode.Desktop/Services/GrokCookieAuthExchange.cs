using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace VibeCode.Services;

public sealed class GrokCookieExchangeResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Completes Grok's OAuth handshake using pasted cookies, without a browser engine.
///
/// The WebView2 window in <see cref="VibeCode.UI.GrokCookieLoginWindow"/> does one job: load the session cookies,
/// open xAI's authorize URL, and let the redirect chain land on the loopback address the Grok CLI is listening on.
/// That is an HTTP conversation, not a rendering problem - so it also runs here, with no WebView2 at all. Microsoft
/// ships no WebView2 for Linux, which is precisely why cookie sign-in was impossible on the Wine build.
///
/// Redirects are followed by hand because the last hop is an https -> http://127.0.0.1 downgrade, and HttpClient
/// refuses to follow those automatically. Cookie values are never logged, and are dropped as soon as the exchange
/// finishes.
/// </summary>
public static class GrokCookieAuthExchange
{
    private const int MaximumRedirects = 20;

    /// <summary>Cloudflare rejects a bare .NET agent outright; this is a plain desktop-browser identity.</summary>
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/131.0.0.0 Safari/537.36";

    public static async Task<GrokCookieExchangeResult> RunAsync(
        IReadOnlyList<GrokBrowserCookie> cookies, string loginUrl, CancellationToken cancellationToken)
    {
        if (!GrokLoginUrl.TryGetTrusted(loginUrl, out var trusted))
            return Failure("Grok returned an untrusted sign-in address.");

        var jar = new CookieContainer();
        var loaded = Seed(jar, cookies);
        if (loaded == 0)
            return Failure("No compatible Grok SSO cookie could be loaded.");

        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = true,
            CookieContainer = jar,
            AutomaticDecompression = DecompressionMethods.All,
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        return await FollowAsync(client, new Uri(trusted), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Walk the authorize redirect chain until it lands on the address the Grok CLI is listening on.
    /// Separated from <see cref="RunAsync"/> so the hop handling can be driven against a local server in tests,
    /// which is also why the callback test is a parameter: in production it is simply "this is loopback".
    /// </summary>
    internal static async Task<GrokCookieExchangeResult> FollowAsync(
        HttpClient client, Uri start, CancellationToken cancellationToken, Func<Uri, bool>? isCallback = null)
    {
        var reachedCallback = isCallback ?? (uri => uri.IsLoopback);
        var current = start;
        for (var hop = 0; hop < MaximumRedirects; hop++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.UserAgent.ParseAdd(UserAgent);
            request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            request.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.9");

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                // Reaching the callback is the goal; the CLI closing the socket the moment it has the
                // authorization code is a completed handshake, not a transport failure.
                return reachedCallback(current)
                    ? new GrokCookieExchangeResult { Success = true }
                    : Failure("The xAI sign-in request failed: " + ex.Message);
            }

            using (response)
            {
                if (reachedCallback(current)) return new GrokCookieExchangeResult { Success = true };

                if (!TryGetRedirect(response, current, out var next, out var redirectError))
                    return Failure(redirectError ?? DescribeDeadEnd(response));

                current = next;
            }
        }

        return Failure("xAI redirected more times than the sign-in exchange allows.");
    }

    /// <summary>Load the pasted cookies against their own origin, so CookieContainer scopes them exactly as a browser would.</summary>
    private static int Seed(CookieContainer jar, IReadOnlyList<GrokBrowserCookie> cookies)
    {
        var sessionCookies = 0;
        foreach (var imported in cookies)
        {
            try
            {
                var domain = imported.Domain.TrimStart('.');
                if (domain.Length == 0) continue;
                var origin = new Uri("https://" + domain + "/");
                jar.Add(origin, new Cookie(imported.Name, imported.Value,
                    string.IsNullOrWhiteSpace(imported.Path) ? "/" : imported.Path)
                {
                    Domain = imported.Domain,
                    Secure = imported.IsSecure,
                    HttpOnly = imported.IsHttpOnly,
                });
                if (imported.Name.Equals("sso", StringComparison.OrdinalIgnoreCase)
                    || imported.Name.Equals("sso-rw", StringComparison.OrdinalIgnoreCase))
                    sessionCookies++;
            }
            catch
            {
                // One malformed entry must not expose its value or discard the rest of the export.
            }
        }
        return sessionCookies;
    }

    /// <summary>
    /// Resolve one redirect. https is allowed anywhere - xAI's own flow crosses x.ai, grok.com and their identity
    /// hosts - but plain http only ever to loopback, which is the CLI's own callback listener.
    /// </summary>
    internal static bool TryGetRedirect(HttpResponseMessage response, Uri current, out Uri next, out string? error)
    {
        next = current;
        error = null;
        var status = (int)response.StatusCode;
        if (status is not (301 or 302 or 303 or 307 or 308)) return false;

        var location = response.Headers.Location;
        if (location is null)
        {
            error = "xAI sent a redirect with no destination.";
            return false;
        }

        var resolved = location.IsAbsoluteUri ? location : new Uri(current, location);
        if (resolved.Scheme == Uri.UriSchemeHttps
            || (resolved.Scheme == Uri.UriSchemeHttp && resolved.IsLoopback))
        {
            next = resolved;
            return true;
        }

        error = "xAI redirected to an address VibeCode will not follow.";
        return false;
    }

    private static string DescribeDeadEnd(HttpResponseMessage response) =>
        (int)response.StatusCode switch
        {
            401 or 403 => "xAI rejected the pasted cookies. They are usually expired, or tied to a different browser.",
            429 => "xAI is rate-limiting this sign-in. Wait a moment and try again.",
            >= 500 => $"xAI returned a server error (HTTP {(int)response.StatusCode}).",
            _ => "The pasted session did not complete xAI's sign-in without a browser. "
                 + "This normally means the cookies are stale or the account needs an interactive challenge.",
        };

    private static GrokCookieExchangeResult Failure(string error) => new() { Error = error };
}
