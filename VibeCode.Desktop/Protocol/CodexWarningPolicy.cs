namespace VibeCode.Protocol;

internal static class CodexWarningPolicy
{
    // Match only Codex's known startup advisory, never a generic warning, error or approval denial.
    internal static bool IsUnstableFeatureNotice(string? message) => message is not null
        && message.TrimStart().StartsWith("Under-development features enabled:", StringComparison.Ordinal)
        && message.Contains("suppress_unstable_features_warning", StringComparison.Ordinal);
}
