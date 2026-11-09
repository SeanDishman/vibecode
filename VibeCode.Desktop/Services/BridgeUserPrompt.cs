using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace VibeCode.Services;

/// <summary>
/// A Bridge's prompt: text the user writes once, from the Bridge's ⋮ menu, that VibeCode puts at the top of every
/// message each agent in that Bridge receives. One per Bridge, kept with it.
/// </summary>
public sealed record BridgeUserPrompt
{
    public const int MaxCharacters = 8000;
    public const string Header = "[BRIDGE USER PROMPT]";
    public const string Footer = "[END BRIDGE USER PROMPT]";

    public string Text { get; init; } = "";

    [JsonIgnore] public bool IsEmpty => string.IsNullOrWhiteSpace(Text);

    /// <summary>The user's text, tidied for the wire: one line-ending style, no end marker of its own, within the cap.</summary>
    public static BridgeUserPrompt From(string? text)
    {
        var clean = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Replace(Footer, "", StringComparison.OrdinalIgnoreCase).Trim();
        return new BridgeUserPrompt { Text = clean.Length > MaxCharacters ? clean[..MaxCharacters].TrimEnd() : clean };
    }

    /// <summary>The wire block, empty when there is no prompt. It keeps the user's blank lines, so the transcript cuts it at
    /// <see cref="Footer"/> rather than block by block (ChatViewModel.StripInjectedPrelude).</summary>
    public string Block() => IsEmpty ? "" : Header + " The user's prompt for every agent in this Bridge:\n" + Text.Trim() + "\n" + Footer;
}

/// <summary>One small file per Bridge, keyed by its host conversation, so the prompt is restored with the Bridge. A
/// prompt no agent has received for the retention period is deleted by <see cref="MemoryRetention"/>.</summary>
public static class BridgeUserPromptStore
{
    private const int FileMaxBytes = 64 * 1024;
    private static readonly object Gate = new();

    public static string DirectoryPath => Path.Combine(AppSettings.Dir, "bridge-prompts");

    public static string Key(string provider, string hostSessionId) => provider + "\n" + hostSessionId;

    public static string PathFor(string key) => Path.Combine(DirectoryPath,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".json");

    public static BridgeUserPrompt Load(string key)
    {
        lock (Gate) return MemoryContextFiles.Read<BridgeUserPrompt>(PathFor(key), FileMaxBytes) ?? new BridgeUserPrompt();
    }

    /// <summary>An empty prompt removes the file.</summary>
    public static void Save(string key, BridgeUserPrompt prompt)
    {
        lock (Gate)
        {
            if (prompt.IsEmpty)
            {
                if (File.Exists(PathFor(key))) File.Delete(PathFor(key));
            }
            else MemoryContextFiles.Write(PathFor(key), prompt, FileMaxBytes);
        }
    }

    /// <summary>Sending the prompt counts as use: retention measures idleness from the file's time.</summary>
    public static void Touch(string key, DateTimeOffset now)
    {
        lock (Gate)
        {
            try { if (File.Exists(PathFor(key))) File.SetLastWriteTimeUtc(PathFor(key), now.UtcDateTime); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
