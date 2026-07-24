using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using VibeCode.UI;

namespace VibeCode.Services;

/// <summary>One rate-limit bucket parsed from `/usage` (session / weekly / per-model week).</summary>
public sealed class UsageLimit
{
    public required string Label { get; init; }
    public required int Percent { get; init; }
    public string? ResetText { get; init; }   // raw from the CLI, e.g. "Jul 19, 3:59pm (America/Chicago)"
    public bool HasReset => !string.IsNullOrWhiteSpace(ResetDisplay);

    /// <summary>
    /// Reset shown as a relative "in 5h" / "in 3d" instead of an absolute date - the session limit is a
    /// rolling ~6h window, so the wall-clock reset time is noise. Falls back to the raw text if unparseable.
    /// </summary>
    public string? ResetDisplay
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ResetText)) return null;
            if (!TryParseReset(ResetText, out var reset)) return ResetText.Trim();
            var span = reset - DateTime.Now;
            if (span <= TimeSpan.Zero) return "soon";
            if (span.TotalHours < 1) return $"in {Math.Max(1, (int)Math.Round(span.TotalMinutes))}m";
            if (span.TotalHours < 48) return $"in {(int)Math.Round(span.TotalHours)}h";
            return $"in {(int)Math.Round(span.TotalDays)}d";
        }
    }

    // The CLI prints the reset in the machine's own timezone (e.g. "Jul 19, 3:59pm (America/Chicago)"),
    // so we drop the "(tz)" note and parse it as local time. No year is given - assume the current one.
    private static bool TryParseReset(string raw, out DateTime reset)
    {
        var s = Regex.Replace(raw, @"\s*\([^)]*\)\s*$", "").Trim();
        var withYear = $"{s} {DateTime.Now.Year}";
        // On the hour the CLI drops the minutes ("Jul 22, 4pm"), so the minute-less shapes need their own formats.
        // Without them this fell through to the loose parse below, which reads that "4" as the YEAR - every
        // on-the-hour reset then landed in the year 4 and rendered as "resets soon".
        string[] formats =
        {
            "MMM d, h:mmtt yyyy", "MMM d, h:mm tt yyyy", "MMM d, htt yyyy", "MMM d, h tt yyyy",
            "MMMM d, h:mmtt yyyy", "MMMM d, h:mm tt yyyy", "MMMM d, htt yyyy", "MMMM d, h tt yyyy",
        };
        if (!DateTime.TryParseExact(withYear, formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out reset)
            && !DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out reset))
            return false;
        if (reset < DateTime.Now.AddDays(-1)) reset = reset.AddYears(1);   // Dec -> Jan wrap
        // Still in the past means we misread it. Fall back to showing the CLI's own text rather than confidently
        // announcing a reset that already happened.
        return reset >= DateTime.Now.AddDays(-1);
    }
}

/// <summary>
/// Fetches subscription rate-limit usage by running `claude -p /usage` headlessly and
/// parses it into structured limits (plus reset times and plan). Account-global, so a
/// single shared instance backs every chat's usage panel.
/// </summary>
public sealed class UsageService : Observable
{
    public static UsageService Instance { get; } = new();

    private static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(3);
    private DateTime _lastFetch = DateTime.MinValue;
    // Guarded together: Refresh() runs on the UI thread while FetchAsync's finally runs on a thread-pool thread, so
    // unsynchronized flags let a single account switch spawn several concurrent 45s `claude -p /usage` probes.
    private readonly object _gate = new();
    private bool _fetching;
    private bool _pendingForce;

    private string _summary = "";
    private string _detail = "Loading subscription usage…";
    private string _plan = "";
    private string _updatedText = "";
    private bool _loading = true;

    /// <summary>Compact "4% session · 46% week" for the status-strip pill.</summary>
    public string Summary { get => _summary; private set => Set(ref _summary, value); }
    /// <summary>Full text (tooltip / fallback).</summary>
    public string Detail { get => _detail; private set => Set(ref _detail, value); }
    /// <summary>Plan name, e.g. "Subscription".</summary>
    public string Plan { get => _plan; private set => Set(ref _plan, value); }
    public string UpdatedText { get => _updatedText; private set => Set(ref _updatedText, value); }
    public bool Loading { get => _loading; private set => Set(ref _loading, value); }

    public ObservableCollection<UsageLimit> Limits { get; } = new();
    public bool HasData => Limits.Count > 0;

    /// <summary>Why the panel is empty, in one sentence, when there is nothing to draw. The usage popups render only
    /// <see cref="Limits"/> + <see cref="UpdatedText"/>, so without this EVERY failure looked identical: a blank card
    /// with a Refresh button and no hint that anything had gone wrong.</summary>
    public string Status => Limits.Count > 0 ? "" : _detail;
    public bool HasStatus => Limits.Count == 0 && !string.IsNullOrWhiteSpace(_detail);

    public void Refresh(bool force = false)
    {
        // A forced refresh that lands mid-probe (very common: switching accounts right after chat activity kicked one)
        // used to be dropped entirely, leaving the usage pill on the OLD account's numbers. Queue it instead.
        lock (_gate)
        {
            if (_fetching) { if (force) _pendingForce = true; return; }
            if (!force && DateTime.UtcNow - _lastFetch < MinInterval) return;
            _fetching = true;
        }
        Application.Current?.Dispatcher.BeginInvoke(() => { Loading = true; UpdatedText = "refreshing…"; });
        _ = Task.Run(FetchAsync);
    }

    private async Task FetchAsync()
    {
        try
        {
            // Report the ACTIVE account's usage, not whatever's in the shared ~/.claude login (switching accounts no
            // longer swaps that file). It has to be probed as a real LOGIN though: handing the CLI a bare
            // CLAUDE_CODE_OAUTH_TOKEN makes `/usage` print the "Total cost: $0.00" run summary with no buckets at
            // all - which is why the panel went blank for good as soon as an account was explicitly picked, and why
            // it looked like switching accounts "rate limited" the check. AccountService probes through a throwaway
            // CLAUDE_CONFIG_DIR instead, which reports normally.
            var activeId = AccountService.Instance.ActiveId;
            string text;
            if (activeId is not null && !AccountService.Instance.ActiveIsFallback)
            {
                // No pre-refresh spawn: the probe's own CLI refreshes a near-expiry token inside the temp dir and
                // AccountService persists it back, so one process does both jobs.
                if (await AccountService.Instance.ProbeUsageTextAsync(activeId) is not { } probed)
                {
                    Post(new Parsed { Detail = "This account's saved login is empty or broken — re-add it from the account menu." });
                    return;
                }
                text = probed;
            }
            else text = await ProbeSharedLoginAsync();

            Post(Parse(text));
        }
        catch (Exception ex)
        {
            Post(new Parsed { Detail = $"Couldn't load usage: {ex.Message}" });
        }
        finally
        {
            bool rerun;
            lock (_gate)
            {
                _lastFetch = DateTime.UtcNow;
                _fetching = false;
                rerun = _pendingForce;
                _pendingForce = false;
            }
            if (rerun) Refresh(force: true);   // re-run for whoever we were busy for
        }
    }

    /// <summary>Probe the live ~/.claude login (no account explicitly selected) and return the CLI's report text.</summary>
    private static async Task<string> ProbeSharedLoginAsync()
    {
        var psi = new ProcessStartInfo
        {
            FileName = Protocol.ClaudeSession.ResolveCliPath(),
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add("/usage");
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("json");
        Protocol.ClaudeSession.StripInheritedEnv(psi);

        using var proc = Process.Start(psi);
        if (proc is null) throw new InvalidOperationException("couldn't start the claude CLI");
        // Close stdin (the CLI otherwise stalls ~3s waiting for piped input) and drain BOTH pipes before waiting:
        // stderr was previously left unread, so a chatty run could fill that pipe and hang the read forever - with
        // _fetching stuck true, which silently dead-ends every later refresh, including the popup's button.
        try { proc.StandardInput.Close(); } catch { /* already gone */ }
        var so = proc.StandardOutput.ReadToEndAsync();
        var se = proc.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await proc.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { proc.Kill(entireProcessTree: true); } catch { /* gone */ } }
        var stdout = (await so).Trim();
        await se;   // let the drain finish so the pipe closes cleanly

        try { return JsonNode.Parse(stdout)?["result"]?.GetValue<string>() ?? stdout; }
        catch { return stdout; }
    }

    private sealed class Parsed
    {
        public string Summary = "";
        public string Detail = "";
        public string Plan = "";
        public List<UsageLimit> Limits = new();
    }

    private static Parsed Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new Parsed { Detail = "No usage info - sign in to Claude Code with a subscription." };

        var p = new Parsed { Detail = text };

        // Plan: "You are currently using your subscription to power your Claude Code usage"
        var planM = Regex.Match(text, @"using your (.+?) to power", RegexOptions.IgnoreCase);
        if (planM.Success)
        {
            var raw = planM.Groups[1].Value.Trim();
            p.Plan = raw.Length == 0 ? "" : char.ToUpper(raw[0]) + raw[1..];
        }

        // "Current session: 4% used · resets Jul 15, 8:19pm (America/Chicago)"
        // "Current week (all models): 46% used · resets ..."
        // "Current week (Fable): 40% used · resets ..."
        foreach (Match m in Regex.Matches(
                     text,
                     @"Current (?:session|week \((?<bucket>[^)]+)\)):\s*(?<pct>\d+)% used(?:[^\n]*?resets\s*(?<reset>[^\n]+))?"))
        {
            var bucket = m.Groups["bucket"].Value.Trim();
            var label = bucket.Length == 0
                ? "Session"
                : string.Equals(bucket, "all models", StringComparison.OrdinalIgnoreCase)
                    ? "This week"
                    : $"This week - {bucket}";
            p.Limits.Add(new UsageLimit
            {
                Label = label,
                Percent = int.TryParse(m.Groups["pct"].Value, out var pct) ? Math.Clamp(pct, 0, 100) : 0,
                ResetText = m.Groups["reset"].Success ? m.Groups["reset"].Value.Trim() : null,
            });
        }

        var session = p.Limits.FirstOrDefault(l => l.Label == "Session");
        var week = p.Limits.FirstOrDefault(l => l.Label == "This week");
        var parts = new List<string>();
        if (session is not null) parts.Add($"{session.Percent}% session");
        if (week is not null) parts.Add($"{week.Percent}% week");
        p.Summary = string.Join(" · ", parts);
        if (p.Limits.Count == 0) p.Detail = DescribeEmpty(text);   // never leave the panel with nothing to say
        return p;
    }

    /// <summary>Turn a report that yielded no buckets into one honest sentence. The CLI is not consistent about
    /// failing loudly here - an unauthenticated <c>/usage</c> exits 0 and prints the generic run summary - so the
    /// shape of the output has to carry the verdict.</summary>
    private static string DescribeEmpty(string text)
    {
        var hay = text.ToLowerInvariant();
        if (hay.Contains("not logged in") || hay.Contains("please log in") || hay.Contains("please sign in")
            || hay.Contains("/login") || hay.Contains("unauthor") || hay.Contains("invalid_grant")
            || hay.Contains("authentication_error"))
            return "Claude rejected this account's login — re-add it from the account menu.";
        if (Regex.IsMatch(text, @"^\s*Total cost:", RegexOptions.Multiline))
            return "No subscription usage came back for this account — its saved login looks signed out. Re-add it from the account menu.";
        return "Couldn't read usage from the Claude CLI — try Refresh.";
    }

    private void Post(Parsed p) => Application.Current?.Dispatcher.BeginInvoke(() =>
    {
        Summary = p.Summary;
        Detail = string.IsNullOrWhiteSpace(p.Detail) ? _detail : p.Detail;
        Plan = p.Plan;
        Limits.Clear();
        foreach (var l in p.Limits) Limits.Add(l);
        Raise(nameof(HasData));
        Raise(nameof(Status));
        Raise(nameof(HasStatus));
        // A failed check still HAPPENED - stamp it, or the card reads as "never ran" and hides that Refresh did work.
        UpdatedText = $"updated {DateTime.Now:h:mm tt}";
        Loading = false;
    });
}
