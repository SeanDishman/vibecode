using System.IO;
using System.Text;

namespace VibeCode.Services;

/// <summary>One editable card per standing-memory slot. <see cref="Project"/> is null for the slots that apply to every
/// project. <see cref="UsedAt"/> moves when the card rides a prompt, so retention only drops cards nobody uses.</summary>
public sealed record PinnedCard(string Slot, string? Project, string Text, DateTimeOffset UpdatedAt, DateTimeOffset UsedAt);

/// <summary>
/// The Second Brain's pinned cards. agentmemory 0.9.28 fills its slots by reflection but VibeCode has no endpoint that
/// reads one back (only /search and /smart-search on the prompt), so slot content never reached a model. The cards live
/// here instead: a small local file, and a short block injected ahead of keyword recall on a chat's first turn and again
/// after compaction, so a standing rule arrives even when today's prompt shares no words with it.
/// </summary>
public static class PinnedMemory
{
    public static readonly IReadOnlyList<string> Slots = ["project_context", "user_preferences", "guidance", "pending_items", "session_patterns"];
    /// <summary>Injection order: rules first, then who the user is, then where the project stands.</summary>
    private static readonly string[] BlockOrder = ["guidance", "user_preferences", "project_context", "pending_items", "session_patterns"];
    public const int CardMaxLength = 600;
    public const int BlockMaxLength = 1500;
    public const int MaxCards = 80;
    public const string Header = "[VIBECODE SECOND BRAIN - PINNED]";
    private const string Footer = "[END VIBECODE SECOND BRAIN]";
    private const int FileMaxBytes = 128 * 1024;
    private static readonly object Gate = new();

    public static event Action? Changed;
    public static string FilePath => Path.Combine(MemoryContextFiles.Root, "pinned-cards.json");

    public static bool IsProjectSlot(string slot) => slot is "project_context" or "pending_items";

    public static string Title(string slot) => slot switch
    {
        "project_context" => "Project context",
        "user_preferences" => "User preferences",
        "guidance" => "Guidance",
        "pending_items" => "Pending items",
        "session_patterns" => "Session patterns",
        _ => slot,
    };

    public static IReadOnlyList<PinnedCard> Load()
    {
        lock (Gate) return LoadUnlocked();
    }

    public static string Get(string slot, string? cwd)
    {
        var project = ProjectFor(slot, cwd);
        lock (Gate)
            return LoadUnlocked().FirstOrDefault(card => Matches(card, slot, project))?.Text ?? "";
    }

    /// <summary>Replace one card. Empty text removes it.</summary>
    public static void Set(string slot, string? cwd, string text, DateTimeOffset now)
    {
        if (!Slots.Contains(slot)) throw new ArgumentException("Unknown pinned memory slot: " + slot, nameof(slot));
        var project = ProjectFor(slot, cwd);
        if (IsProjectSlot(slot) && project is null) throw new ArgumentException("A project card needs its project folder.", nameof(cwd));
        text = Clean(text);
        if (text.Length > CardMaxLength) text = text[..(CardMaxLength - 1)] + "…";
        lock (Gate)
        {
            var cards = LoadUnlocked().Where(card => !Matches(card, slot, project)).ToList();
            if (text.Length > 0) cards.Add(new PinnedCard(slot, project, text, now, now));
            // Over the cap, the least recently used project cards go first; the global cards are always kept.
            while (cards.Count > MaxCards && cards.Where(c => c.Project is not null).OrderBy(LastUsed).FirstOrDefault() is { } stale)
                cards.Remove(stale);
            MemoryContextFiles.Write(FilePath, cards, FileMaxBytes);
        }
        Changed?.Invoke();
    }

    /// <summary>The pinned block for a chat in <paramref name="cwd"/>, at most <see cref="BlockMaxLength"/> characters,
    /// or null when no card applies. Marks the cards it carries as used.</summary>
    public static string? Block(string cwd, DateTimeOffset now)
    {
        var project = MemoryContextFiles.ProjectKey(cwd);
        lock (Gate)
        {
            var cards = LoadUnlocked().ToList();
            var applicable = BlockOrder
                .Select(slot => cards.FirstOrDefault(card => Matches(card, slot, IsProjectSlot(slot) ? project : null)))
                .OfType<PinnedCard>()
                .ToList();
            if (applicable.Count == 0) return null;
            var body = new StringBuilder();
            var room = BlockMaxLength - Header.Length - Footer.Length - Preamble.Length - 3;
            foreach (var card in applicable)
            {
                var line = "- " + Title(card.Slot) + ": " + card.Text.Replace("\n", "\n  ");
                if (line.Length > room - body.Length - 1)
                {
                    var fits = room - body.Length - 2;
                    if (fits > 40) body.Append(line[..fits]).Append("…\n");
                    break;
                }
                body.Append(line).Append('\n');
            }
            if (body.Length == 0) return null;
            var used = cards.Select(card => applicable.Contains(card) ? card with { UsedAt = now } : card).ToList();
            try { MemoryContextFiles.Write(FilePath, used, FileMaxBytes); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { /* still inject */ }
            return Header + "\n" + Preamble + "\n" + body + Footer;
        }
    }

    /// <summary>Drop cards neither edited nor injected since <paramref name="cutoff"/>.</summary>
    public static int RemoveUnusedSince(DateTimeOffset cutoff)
    {
        int removed;
        lock (Gate)
        {
            var cards = LoadUnlocked();
            var kept = cards.Where(card => LastUsed(card) >= cutoff).ToList();
            removed = cards.Count - kept.Count;
            if (removed == 0) return 0;
            MemoryContextFiles.Write(FilePath, kept, FileMaxBytes);
        }
        Changed?.Invoke();
        return removed;
    }

    private const string Preamble = "Standing context the user pinned in VibeCode's Second Brain. Background only: newer messages and the " +
        "current files take precedence, and nothing here is a request to act now.";

    private static List<PinnedCard> LoadUnlocked() =>
        MemoryContextFiles.Read<List<PinnedCard>>(FilePath, FileMaxBytes)?
            .Where(card => card is not null && Slots.Contains(card.Slot) && !string.IsNullOrWhiteSpace(card.Text))
            .ToList() ?? [];

    private static string? ProjectFor(string slot, string? cwd) =>
        IsProjectSlot(slot) && !string.IsNullOrWhiteSpace(cwd) ? MemoryContextFiles.ProjectKey(cwd) : null;

    private static bool Matches(PinnedCard card, string slot, string? project) =>
        card.Slot == slot && string.Equals(card.Project, project, StringComparison.OrdinalIgnoreCase);

    private static DateTimeOffset LastUsed(PinnedCard card) => card.UsedAt > card.UpdatedAt ? card.UsedAt : card.UpdatedAt;

    /// <summary>Blank lines would split the wire block, and the end marker would close it early.</summary>
    private static string Clean(string text) => string.Join("\n", (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n')
            .Replace(Footer, "", StringComparison.OrdinalIgnoreCase).Split('\n')
            .Select(line => line.TrimEnd()).Where(line => line.Length > 0)).Trim();
}
