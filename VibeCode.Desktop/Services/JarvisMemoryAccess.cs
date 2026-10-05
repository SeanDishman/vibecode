namespace VibeCode.Services;

/// <summary>Global opt-in and the active chat's permission are distinct facts.</summary>
public sealed record JarvisMemoryPermission(bool ExtensionEnabled, bool ChatAllowed, string? SourceId)
{
    public bool EffectiveAllowed => ExtensionEnabled && ChatAllowed && SourceId is not null;
}

/// <summary>A recalled-memory epoch. Revocation is permanent even if the same chat is enabled again.</summary>
public sealed class JarvisMemoryAccess(string sourceId, CancellationToken revoked,
    Func<JarvisMemoryPermission> readPermission)
{
    public string SourceId { get; } = sourceId;
    public CancellationToken Revoked { get; } = revoked;
    public bool IsAllowed
    {
        get
        {
            var permission = readPermission();
            return !Revoked.IsCancellationRequested && permission.EffectiveAllowed
                && string.Equals(SourceId, permission.SourceId, StringComparison.Ordinal);
        }
    }
}

public static class JarvisMemoryPolicy
{
    public static JarvisDialogueLine[] SafeHistory(IEnumerable<JarvisDialogueLine> history) =>
        history.Where(line => line.MemorySources.All(source => source.IsAllowed)).ToArray();

    public static JarvisMemoryAccess[] Sources(JarvisContext context, IEnumerable<JarvisDialogueLine> history) =>
        history.SelectMany(line => line.MemorySources)
            .Concat(context.MemoryContext.Length > 0 && context.MemoryAccess is { IsAllowed: true } access
                ? [access] : Array.Empty<JarvisMemoryAccess>()).Distinct().ToArray();
}
