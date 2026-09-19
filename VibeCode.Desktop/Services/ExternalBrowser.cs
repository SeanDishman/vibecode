using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace VibeCode.Services;

/// <summary>
/// Opens a provider's sign-in page in the user's real browser.
///
/// On Windows this is ShellExecute and always has been. Under Wine it cannot be: the Linux package builds a bare
/// prefix, so the Windows URL association points at winebrowser and nothing guarantees it reaches a real browser.
/// Every device-code and OAuth sign-in in VibeCode ends with "finish this in your browser", so when the launch
/// quietly failed the account simply never arrived - which is why Kimi, Codex and Grok could not be signed in on
/// Linux while Claude, whose CLI prints a code to paste back, worked.
///
/// Callers must treat a false result as normal and show the URL instead. <see cref="Describe"/> gives the wording.
/// </summary>
public static class ExternalBrowser
{
    /// <summary>Unix directories searched for an opener, expressed as Wine sees them under the Z: drive root.</summary>
    private static readonly string[] UnixBinaryDirectories =
        { @"Z:\usr\bin", @"Z:\bin", @"Z:\usr\local\bin", @"Z:\snap\bin" };

    /// <summary>xdg-open first: it honours the desktop's own default-browser setting instead of guessing.</summary>
    private static readonly string[] UnixOpeners =
        { "xdg-open", "x-www-browser", "sensible-browser", "firefox", "chromium", "google-chrome" };

    private static readonly Lazy<bool> WineDetected = new(DetectWine);

    /// <summary>True when this process is running on Wine rather than real Windows.</summary>
    public static bool IsWine => WineDetected.Value;

    /// <summary>
    /// Try to show <paramref name="url"/> in a browser. Returns false - never throws - when no launcher worked,
    /// so a sign-in dialog can fall back to displaying the link.
    /// </summary>
    public static bool TryOpen(string? url, out string? error)
    {
        error = null;
        if (!IsSafeUrl(url, out var safe))
        {
            error = "That sign-in address is not a valid https link.";
            return false;
        }

        foreach (var attempt in Launchers(safe))
        {
            try
            {
                using var started = Process.Start(attempt);
                // A null Process means the shell handed the URL to an already-running browser, which is a success.
                return true;
            }
            catch (Exception ex)
            {
                error ??= ex.Message;
            }
        }

        error = IsWine
            ? "Wine could not hand the link to a Linux browser."
            : error ?? "No application is registered to open web links.";
        return false;
    }

    /// <summary>One line for a sign-in dialog: what happened, and what the user should do about it.</summary>
    public static string Describe(bool opened) => opened
        ? "Finish signing in in the browser window that just opened."
        : "VibeCode could not open a browser. Copy the link below and open it yourself to finish signing in.";

    /// <summary>
    /// Ordered launch attempts. Wine gets the Linux openers first because they are the ones that actually reach a
    /// browser; ShellExecute stays last so a prefix with a working association is still used rather than rejected.
    /// </summary>
    private static IEnumerable<ProcessStartInfo> Launchers(string url)
    {
        if (IsWine)
        {
            foreach (var opener in ResolveUnixOpeners())
            {
                var psi = new ProcessStartInfo { FileName = opener, UseShellExecute = false, CreateNoWindow = true };
                psi.ArgumentList.Add(url);
                yield return psi;
            }

            // Wine's own URL dispatcher. It re-reads $BROWSER and the desktop settings itself, so it is a genuine
            // second chance rather than a repeat of the attempts above.
            var winebrowser = new ProcessStartInfo
            {
                FileName = "winebrowser.exe", UseShellExecute = false, CreateNoWindow = true,
            };
            winebrowser.ArgumentList.Add(url);
            yield return winebrowser;
        }

        yield return new ProcessStartInfo(url) { UseShellExecute = true };
    }

    /// <summary>
    /// Openers that exist right now, most specific first: an explicit $BROWSER, then the usual Debian/Kali names.
    /// Wine maps the Linux root onto Z:, so a Unix path is reachable as a normal Windows path from here.
    /// </summary>
    private static IEnumerable<string> ResolveUnixOpeners()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in PreferredOpeners())
        foreach (var path in CandidatePaths(candidate))
        {
            if (!seen.Add(path)) continue;
            var exists = false;
            try { exists = File.Exists(path); }
            catch { /* an unreadable mount point is simply not a launcher */ }
            if (exists) yield return path;
        }
    }

    private static IEnumerable<string> PreferredOpeners()
    {
        // start.sh exports BROWSER=xdg-open, and a user may point it somewhere else entirely.
        var configured = Environment.GetEnvironmentVariable("BROWSER");
        if (!string.IsNullOrWhiteSpace(configured))
            foreach (var entry in configured.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                yield return entry;

        foreach (var opener in UnixOpeners) yield return opener;
    }

    private static IEnumerable<string> CandidatePaths(string opener)
    {
        // An absolute Unix path from $BROWSER needs translating onto Wine's Z: drive; a bare name gets searched for.
        if (opener.StartsWith('/'))
        {
            yield return @"Z:" + opener.Replace('/', '\\');
            yield break;
        }
        if (opener.Contains('\\') || opener.Contains(':'))
        {
            yield return opener;
            yield break;
        }
        foreach (var directory in UnixBinaryDirectories)
            yield return Path.Combine(directory, opener);
    }

    /// <summary>Only ever hand a real https link (or a loopback callback) to a launcher.</summary>
    private static bool IsSafeUrl(string? value, out string url)
    {
        url = string.Empty;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed)) return false;
        if (parsed.UserInfo.Length != 0) return false;
        if (parsed.Scheme == Uri.UriSchemeHttps || (parsed.Scheme == Uri.UriSchemeHttp && parsed.IsLoopback))
        {
            url = parsed.AbsoluteUri;
            return true;
        }
        return false;
    }

    private static bool DetectWine()
    {
        // start.sh sets this, and it also lets the behaviour be exercised without a Wine prefix.
        if (string.Equals(Environment.GetEnvironmentVariable("VIBECODE_LINUX_COMPAT"), "wine",
                StringComparison.OrdinalIgnoreCase))
            return true;

        // The canonical probe: Wine's ntdll exports wine_get_version, a real Windows ntdll never does.
        try
        {
            var ntdll = GetModuleHandleW("ntdll.dll");
            return ntdll != IntPtr.Zero && GetProcAddress(ntdll, "wine_get_version") != IntPtr.Zero;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern IntPtr GetModuleHandleW(string moduleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = false)]
    private static extern IntPtr GetProcAddress(IntPtr module, string procedureName);
}
