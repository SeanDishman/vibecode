using System.Reflection;

namespace VibeCode.Services;

/// <summary>What "version" means in VibeCode: the &lt;Version&gt; element of VibeCode.Desktop.csproj, which MSBuild
/// stamps onto the assembly and which Settings &gt; About reads back through <see cref="Current"/>.
///
/// Deliberately lenient about shape. It accepts "v1.2", "1.2.3" and "1.2.3.4" so a hand-edited csproj cannot
/// break the About page with a parse failure, and it ignores any "-prerelease" or "+build" suffix - which does
/// mean 1.2.0-beta and 1.2.0 compare equal.</summary>
public readonly record struct AppVersion : IComparable<AppVersion>
{
    public int Major { get; init; }
    public int Minor { get; init; }
    public int Patch { get; init; }
    public int Revision { get; init; }

    /// <summary>Zero means "we could not tell" - it is below every real version, so an unknown running version
    /// never suppresses an update and never claims to be newer than one.</summary>
    public bool IsKnown => Major > 0 || Minor > 0 || Patch > 0 || Revision > 0;

    public static bool TryParse(string? text, out AppVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var trimmed = text.Trim();
        if (trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed[1..];
        // Cut a semver suffix before splitting, so "1.4.0-rc.2" does not parse its way into the revision field.
        var cut = trimmed.IndexOfAny(new[] { '-', '+', ' ' });
        if (cut >= 0) trimmed = trimmed[..cut];

        var parts = trimmed.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is 0 or > 4) return false;

        var numbers = new int[4];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out var value) || value < 0) return false;
            numbers[i] = value;
        }

        version = new AppVersion { Major = numbers[0], Minor = numbers[1], Patch = numbers[2], Revision = numbers[3] };
        return true;
    }

    public static AppVersion Parse(string? text) => TryParse(text, out var v) ? v : default;

    /// <summary>The version of the build that is running right now.</summary>
    public static AppVersion Current { get; } = ReadCurrent();

    private static AppVersion ReadCurrent()
    {
        // InformationalVersion first: it carries <Version> verbatim, where AssemblyVersion has already been
        // widened to four parts and would report "1.2" as "1.2.0.0".
        var assembly = Assembly.GetEntryAssembly() ?? typeof(AppVersion).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (TryParse(informational, out var parsed)) return parsed;
        return TryParse(assembly.GetName().Version?.ToString(), out var fallback) ? fallback : default;
    }

    public int CompareTo(AppVersion other)
    {
        if (Major != other.Major) return Major.CompareTo(other.Major);
        if (Minor != other.Minor) return Minor.CompareTo(other.Minor);
        if (Patch != other.Patch) return Patch.CompareTo(other.Patch);
        return Revision.CompareTo(other.Revision);
    }

    public static bool operator <(AppVersion a, AppVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(AppVersion a, AppVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(AppVersion a, AppVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(AppVersion a, AppVersion b) => a.CompareTo(b) >= 0;

    /// <summary>Three parts normally, four only when a revision was actually specified - so a version reads back
    /// the way it was written in the csproj rather than growing a ".0" nobody typed.</summary>
    public override string ToString() =>
        Revision > 0 ? $"{Major}.{Minor}.{Patch}.{Revision}" : $"{Major}.{Minor}.{Patch}";
}
