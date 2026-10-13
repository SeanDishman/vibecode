using System.Text.Json;

namespace VibeCode.Services;

/// <summary>The user's persistent objective for one chat, independent of its provider.</summary>
public sealed record ChatGoal(string Id, string Text, bool Completed = false, bool Paused = false);

public static class GoalPolicy
{
    public const string CheckHeader = "[VIBECODE GOAL CHECK]";
    private const string CheckGoalPrefix = CheckHeader + "\nGoal: ";

    public static bool TryParseCommand(string text, out string goal)
    {
        var command = text.AsSpan().TrimStart();
        goal = "";
        if (!command.StartsWith("/goal", StringComparison.OrdinalIgnoreCase)
            || (command.Length > 5 && !char.IsWhiteSpace(command[5]))) return false;
        goal = command[5..].Trim().ToString();
        return true;
    }

    public static ChatGoal? Normalize(ChatGoal? goal) => goal is not null
        && Guid.TryParseExact(goal.Id, "N", out _) && !string.IsNullOrWhiteSpace(goal.Text)
            ? goal with { Text = goal.Text.Trim() } : null;

    public static string CompleteMarker(ChatGoal goal) => $"[VIBECODE_GOAL_COMPLETE:{goal.Id}]";
    public static string WaitingMarker(ChatGoal goal) => $"[VIBECODE_GOAL_WAITING:{goal.Id}]";

    public static string TurnContext(ChatGoal goal) =>
        "[VIBECODE ACTIVE GOAL]\n"
        + "The user set this goal for this chat (JSON string): " + JsonSerializer.Serialize(goal.Text)
        + "\nWork toward it while respecting the user's latest instructions. Report the goal's status "
        + "in this turn when it is finished or needs user input; do not wait for a separate goal check. "
        + "Do not treat a turn ending as proof that the goal is complete."
        + StatusInstructions(goal)
        + "\n[/VIBECODE ACTIVE GOAL]";

    public static string BuildCheck(ChatGoal goal) => CheckGoalPrefix + JsonSerializer.Serialize(goal.Text)
        + "\nDid you finish this user's goal?"
        + "\nReview the actual work and verification before answering. If the goal is unfinished, "
        + "continue doing the remaining work now; another normal stop will trigger this check again. "
        + StatusInstructions(goal);

    private static string StatusInstructions(ChatGoal goal) =>
        "\nIf the goal is fully achieved, briefly report the result and end with this exact standalone line "
        + "(plain text, without Markdown formatting):\n"
        + CompleteMarker(goal)
        + "\nIf you cannot continue without a user answer, approval, credentials, or an external change, "
        + "explain what is needed and end with this exact standalone line instead:\n" + WaitingMarker(goal)
        + "\nNever claim completion merely to stop the reminders. Follow all permission and safety requirements.";

    public static bool TryReadCheckGoal(string text, out string goal)
    {
        goal = "";
        if (!text.StartsWith(CheckGoalPrefix, StringComparison.Ordinal)) return false;
        var end = text.IndexOf('\n', CheckGoalPrefix.Length);
        if (end < 0) return false;
        try { goal = JsonSerializer.Deserialize<string>(text[CheckGoalPrefix.Length..end]) ?? ""; }
        catch (JsonException) { return false; }
        return !string.IsNullOrWhiteSpace(goal);
    }

    public static bool EndsWithMarker(string reply, string marker) => TrailingMarkerStart(reply, marker) >= 0;

    public static string StripTrailingMarker(string reply, string marker) =>
        TrailingMarkerStart(reply, marker) is var start && start >= 0 ? reply[..start].TrimEnd() : reply;

    private static int TrailingMarkerStart(string reply, string marker)
    {
        var text = reply.AsSpan().TrimEnd();
        var lastLine = text.LastIndexOf('\n') + 1;
        if (IsMarkerLine(text[lastLine..], marker)) return lastLine;

        // Models sometimes format the protocol as code. Only accept a terminal, single-line fenced block;
        // a marker quoted earlier in an explanation must not settle the goal.
        if (!text[lastLine..].Trim().SequenceEqual("```")) return -1;
        text = text[..lastLine].TrimEnd();
        var markerLine = text.LastIndexOf('\n') + 1;
        if (!IsMarkerLine(text[markerLine..], marker)) return -1;
        text = text[..markerLine].TrimEnd();
        var fenceLine = text.LastIndexOf('\n') + 1;
        var fence = text[fenceLine..].Trim();
        return fence.SequenceEqual("```") || fence.SequenceEqual("```text") || fence.SequenceEqual("```plaintext")
            ? fenceLine : -1;
    }

    private static bool IsMarkerLine(ReadOnlySpan<char> line, string marker)
    {
        line = line.Trim();
        while (line.Length >= 2 && (line[0] is '`' or '*' or '_') && line[^1] == line[0])
            line = line[1..^1].Trim();
        return line.SequenceEqual(marker);
    }
}
