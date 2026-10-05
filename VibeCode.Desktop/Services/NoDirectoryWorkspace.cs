using System.IO;

namespace VibeCode.Services;

/// <summary>
/// Where a "No directory" chat runs. Every provider process still needs a working folder, so a chat that is not about
/// any project gets VibeCode's own folder instead of the user's code: none of a project's files, CLAUDE.md/AGENTS.md or
/// project MCP servers come along. It is one shared, stable folder rather than one per chat so the providers' own
/// history (keyed by cwd) keeps resume and the sidebar working, and every such chat groups under one entry. Being
/// shared, it is a scratch space, not a guaranteed-empty one: anything an agent writes here (a script, even a
/// CLAUDE.md) is still here for the next no-directory chat, exactly as files persist inside any project.
/// <para>
/// The folder is literally named <see cref="DisplayName"/>, so everything that labels a chat by its folder name - the
/// sidebar's history group, saved bridges, an untitled chat's title - already reads "No directory". It sits in
/// LocalAppData beside Jarvis's planning workspace, deliberately not in <see cref="AppSettings.Dir"/>, which holds
/// settings.json and the sealed API keys.
/// </para>
/// </summary>
public static class NoDirectoryWorkspace
{
    public const string DisplayName = "No directory";

    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VibeCode", DisplayName);

    /// <summary>Create the folder if it is missing (first use, or the user cleared it) and return it.</summary>
    public static string Ensure()
    {
        Directory.CreateDirectory(Folder);
        return Folder;
    }

    public static bool IsNoDirectory(string? cwd) => RecentDirectoryHistory.PathsEqual(cwd, Folder);

    /// <summary><see cref="Directory.Exists"/> for a chat's saved cwd, except that this folder is recreated rather
    /// than reported missing. Callers fall back to another folder when a cwd is gone; for a no-directory chat that
    /// fallback would quietly move it into the user's profile or a project, while the folder itself is ours to remake.</summary>
    public static bool DirectoryExists(string? cwd)
    {
        if (Directory.Exists(cwd)) return true;
        if (!IsNoDirectory(cwd)) return false;
        try
        {
            Ensure();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
