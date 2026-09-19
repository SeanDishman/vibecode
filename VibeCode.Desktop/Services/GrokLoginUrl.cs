using System.Text.RegularExpressions;

namespace VibeCode.Services;

/// <summary>
/// Which sign-in addresses VibeCode will act on, and how they are written to a log or a status line.
///
/// This is the trust boundary for Grok sign-in: the CLI's output is parsed for a URL that VibeCode then opens, or
/// drives a cookie exchange against, so only xAI's own hosts may ever come back from here. It is deliberately free
/// of process, protocol and UI dependencies so the policy can be exercised on its own.
/// </summary>
public static partial class GrokLoginUrl
{
    /// <summary>Find the first xAI sign-in URL in a line of CLI output.</summary>
    public static bool TryGetTrusted(string? text, out string url)
    {
        foreach (Match match in UrlRegex().Matches(text ?? string.Empty))
        {
            var candidate = match.Value.TrimEnd('.', ',', ';', ')', ']', '}', '>', '"');
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var parsed)
                || parsed.Scheme != Uri.UriSchemeHttps
                || parsed.UserInfo.Length != 0
                || !IsTrustedHost(parsed.Host))
                continue;
            url = parsed.AbsoluteUri;
            return true;
        }
        url = string.Empty;
        return false;
    }

    /// <summary>Replace every URL in CLI output with a label. Sign-in links are single-use credentials.</summary>
    public static string Redact(string text) =>
        UrlRegex().Replace(text, match =>
        {
            var candidate = match.Value.TrimEnd('.', ',', ';', ')', ']', '}', '>', '"');
            return TryGetTrusted(candidate, out _) ? "[xAI sign-in link]" : "[link omitted]";
        });

    public static bool IsTrustedHost(string host) =>
        host.Equals("x.ai", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".x.ai", StringComparison.OrdinalIgnoreCase)
        || host.Equals("grok.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".grok.com", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"https://[^\s\x1b]+", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex UrlRegex();
}
