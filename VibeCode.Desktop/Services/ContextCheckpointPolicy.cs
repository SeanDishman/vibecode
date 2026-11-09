using System.Text;

namespace VibeCode.Services;

/// <summary>
/// Prompting a checkpoint before the context window fills. Every turn already asks the model to call chat_checkpoint
/// "before compaction", but the CLI compacts on its own without warning the model, so the Constraints and Decisions the
/// post-compaction recovery block is meant to restore usually stayed empty. VibeCode knows the exact fill, so it asks once
/// per crossing of <see cref="AppSettings.ContextCheckpointNudgePercent"/>.
/// </summary>
public static class ContextCheckpointPolicy
{
    public const string Header = "[VIBECODE CONTEXT]";
    public const int DefaultPercent = 75;
    /// <summary>The meter must fall this far below the threshold (a compaction, a fresh session) before it re-arms, so a
    /// reading that wobbles around the line cannot nudge twice.</summary>
    public const double RearmMargin = 10;

    /// <summary>True exactly when <paramref name="percent"/> crosses the threshold while armed; updates the arm.</summary>
    public static bool Crossed(double percent, int threshold, ref bool armed)
    {
        if (threshold is <= 0 or > 100) return false;
        if (percent < threshold - RearmMargin) armed = true;
        if (!armed || percent < threshold) return false;
        armed = false;
        return true;
    }

    public static string Note(double percent) =>
        $"{Header} {percent:0}% used - call chat_checkpoint now with current constraints and decisions";

    public static string AgeText(DateTimeOffset? savedAt, DateTimeOffset now)
    {
        if (savedAt is not { } saved || saved == default) return "checkpoint none";
        var age = now - saved;
        return age < TimeSpan.FromMinutes(1) ? "checkpoint just now"
            : age < TimeSpan.FromHours(1) ? $"checkpoint {(int)age.TotalMinutes} min ago"
            : age < TimeSpan.FromDays(1) ? $"checkpoint {(int)age.TotalHours} h ago"
            : $"checkpoint {(int)age.TotalDays} d ago";
    }

    /// <summary>An editable first prompt for a fresh chat that carries the work on (an Amp-style handoff), for providers
    /// that cannot compact or fork through VibeCode.</summary>
    public static string HandoffDraft(TaskCheckpoint? checkpoint, string title, double percent)
    {
        var text = new StringBuilder()
            .Append("Continuing work from an earlier chat")
            .Append(string.IsNullOrWhiteSpace(title) ? "" : $" (\"{title.Trim()}\")")
            .Append($" whose context was {percent:0}% full. Its saved checkpoint, as reported there (not verified):\n");
        if (checkpoint is null || string.IsNullOrWhiteSpace(checkpoint.Objective))
            text.Append("\nNo checkpoint was saved. Objective: <describe the task to continue>\n");
        else
        {
            text.Append("\nObjective: ").Append(checkpoint.Objective.Trim()).Append('\n');
            Section(text, "Constraints", checkpoint.Constraints);
            Section(text, "Decisions", checkpoint.Decisions);
            Section(text, "Done so far", checkpoint.Completed);
            Section(text, "Remaining", checkpoint.Remaining);
            Section(text, "Evidence", checkpoint.Evidence);
            if (checkpoint.RecentRequests.LastOrDefault() is { Length: > 0 } last)
                text.Append("\nLast request there:\n").Append(last.Trim()).Append('\n');
        }
        return text.Append("\nCheck the current files before redoing anything, then continue with the remaining work.").ToString();
    }

    private static void Section(StringBuilder text, string name, IReadOnlyList<string> rows)
    {
        if (rows.Count == 0) return;
        text.Append('\n').Append(name).Append(":\n");
        foreach (var row in rows) text.Append("- ").Append(row.Trim()).Append('\n');
    }
}
