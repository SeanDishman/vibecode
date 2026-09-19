using System.IO;
using System.Text.RegularExpressions;

namespace VibeCode.Services;

public enum TranscriptLinkKind { LocalPath, Web, Anchor }

/// <summary>A transcript destination, with editor line notation kept separate from the file name.</summary>
public sealed record TranscriptLink(TranscriptLinkKind Kind, string Address, int? Line = null)
{
    private static readonly Regex LineSuffix = new(@":(?<line>[1-9]\d*)(?::[1-9]\d*)?(?:-[1-9]\d*(?::[1-9]\d*)?)?$",
        RegexOptions.CultureInvariant);
    private static readonly Regex LineFragment = new(@"^L(?<line>[1-9]\d*)(?:C[1-9]\d*)?(?:-L?[1-9]\d*(?:C[1-9]\d*)?)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Scheme = new(@"^[a-z][a-z0-9+.-]*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool TryResolve(string? destination, string? workingDirectory,
        out TranscriptLink? target, out string? error)
    {
        target = null;
        error = null;
        var value = destination?.Trim() ?? "";
        if (value.StartsWith('<') && value.EndsWith('>')) value = value[1..^1].Trim();
        if (value.Length == 0 || value.Any(char.IsControl))
        {
            error = "This link has an empty or invalid address.";
            return false;
        }

        // Do this before parsing file line numbers: URL ports, query strings and fragments are part of the URL.
        if (Uri.TryCreate(value, UriKind.Absolute, out var web)
            && web.Scheme is "https" or "http" or "mailto")
        {
            if (web.Scheme != "mailto" && string.IsNullOrEmpty(web.Host))
            {
                error = "This web link has an invalid address.";
                return false;
            }
            target = new(TranscriptLinkKind.Web, web.AbsoluteUri);
            return true;
        }

        if (value.StartsWith('#'))
        {
            target = new(TranscriptLinkKind.Anchor, Uri.UnescapeDataString(value));
            return true;
        }

        try
        {
            int? line = null;
            // Split before unescaping, so a literal %23 in a filename stays part of its name.
            var hash = value.IndexOf('#');
            if (hash >= 0)
            {
                var fragment = LineFragment.Match(value[(hash + 1)..]);
                if (fragment.Success && int.TryParse(fragment.Groups["line"].Value, out var fragmentLine))
                    line = fragmentLine;
                value = value[..hash];
            }

            if (value.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                if (!Uri.TryCreate(value, UriKind.Absolute, out var file) || !file.IsFile || file.IsUnc)
                {
                    error = "This file link must point to a local file.";
                    return false;
                }
                value = file.LocalPath; // Uri.LocalPath already unescapes exactly once.
            }
            else value = Uri.UnescapeDataString(value);

            var suffix = LineSuffix.Match(value);
            if (suffix.Success)
            {
                if (!int.TryParse(suffix.Groups["line"].Value, out var suffixLine))
                {
                    error = "The line number in this file link is too large.";
                    return false;
                }
                line ??= suffixLine;
                value = value[..suffix.Index];
            }

            // Codex also emits /C:/Users/... links. The first slash is URI notation, not part of a Windows path.
            if (value.Length > 3 && value[0] == '/' && IsDrivePath(value[1..])) value = value[1..];
            if (Scheme.IsMatch(value) && !IsDrivePath(value))
            {
                error = "This link type is not supported. Use a web address or a local file path.";
                return false;
            }
            if (value.StartsWith(@"\\") || value.StartsWith("//", StringComparison.Ordinal))
            {
                error = "This file link must point to a local file.";
                return false;
            }
            if (value.Length == 0 || value.Any(char.IsControl) || value.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0
                || value[(IsDrivePath(value) ? 2 : 0)..].Contains(':'))
            {
                error = "This file link has an invalid path.";
                return false;
            }

            if (!Path.IsPathFullyQualified(value))
            {
                if (string.IsNullOrWhiteSpace(workingDirectory) || !Path.IsPathFullyQualified(workingDirectory))
                {
                    error = "VibeCode could not determine which project this relative file link belongs to.";
                    return false;
                }
                value = Path.GetFullPath(value, workingDirectory);
            }
            else value = Path.GetFullPath(value);

            target = new(TranscriptLinkKind.LocalPath, value, line);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UriFormatException)
        {
            error = "This file link has an invalid path: " + ex.Message;
            return false;
        }
    }

    private static bool IsDrivePath(string value) => value.Length >= 3 && char.IsAsciiLetter(value[0])
        && value[1] == ':' && value[2] is '/' or '\\';
}
