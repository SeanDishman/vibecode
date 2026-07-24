using System.Text;

namespace VibeCode.Services;

public static class CompactToolPresentation
{
    public static bool ShouldUseCompactDisplay(bool enabled, string groupKind, int toolCount) =>
        enabled && toolCount > 0 && (groupKind == "bash" || toolCount > 1);

    public static string ToSingleLine(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var result = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = result.Length > 0;
                continue;
            }
            if (pendingSpace)
            {
                result.Append(' ');
                pendingSpace = false;
            }
            result.Append(ch);
        }
        return result.ToString();
    }
}
