namespace VibeCode.UI;

/// <summary>
/// Hand-crafted terminal banners for the empty chat surface in CLI mode only.
/// These are display-only chrome — never inserted into the transcript / Items list.
/// </summary>
public static class ProviderAsciiArt
{
    public static string For(string? provider) => (provider ?? "").Trim().ToLowerInvariant() switch
    {
        "codex" => Codex,
        "kimi" => Kimi,
        "gemini" => "G E M I N I",
        _ => Claude,
    };

    public static string TaglineFor(string? provider) => (provider ?? "").Trim().ToLowerInvariant() switch
    {
        "codex" => "OpenAI Codex · ready when you are",
        "kimi" => "Kimi Code · ready when you are",
        "gemini" => "Google Gemini · ready when you are",
        _ => "Claude Code · ready when you are",
    };

    // Fixed-width box-drawing frames, 54 columns inside the borders, in Cascadia Mono / Consolas.
    // Every banner is the SAME width so switching provider does not resize the empty-chat surface.
    //

    // center with equal left/right pad. Odd-width words (CLAUDE/CODEX/KIMI) sit at most 1 column

    // left of center, which read as "the logo isn't centered".
    //
    // Letterforms are FIGlet ansi_shadow. The host TextBlocks sit in a DownOnly Viewbox so a
    // narrow Bridge pane scales this down instead of cropping it.

    public static readonly string Claude = """
        ╔══════════════════════════════════════════════════════╗
        ║                                                      ║
        ║   ██████╗██╗      █████╗ ██╗   ██╗██████╗ ███████╗   ║
        ║  ██╔════╝██║     ██╔══██╗██║   ██║██╔══██╗██╔════╝   ║
        ║  ██║     ██║     ███████║██║   ██║██║  ██║█████╗     ║
        ║  ██║     ██║     ██╔══██║██║   ██║██║  ██║██╔══╝     ║
        ║  ╚██████╗███████╗██║  ██║╚██████╔╝██████╔╝███████╗   ║
        ║   ╚═════╝╚══════╝╚═╝  ╚═╝ ╚═════╝ ╚═════╝ ╚══════╝   ║
        ║                                                      ║
        ║               ✦  C L A U D E   C O D E               ║
        ║                                                      ║
        ╚══════════════════════════════════════════════════════╝
        """;


    public static readonly string Codex = """
        ╔══════════════════════════════════════════════════════╗
        ║                                                      ║
        ║       ██████╗ ██████╗ ██████╗ ███████╗██╗  ██╗       ║
        ║      ██╔════╝██╔═══██╗██╔══██╗██╔════╝╚██╗██╔╝       ║
        ║      ██║     ██║   ██║██║  ██║█████╗   ╚███╔╝        ║
        ║      ██║     ██║   ██║██║  ██║██╔══╝   ██╔██╗        ║
        ║      ╚██████╗╚██████╔╝██████╔╝███████╗██╔╝ ██╗       ║
        ║       ╚═════╝ ╚═════╝ ╚═════╝ ╚══════╝╚═╝  ╚═╝       ║
        ║                                                      ║
        ║              ◎  O P E N A I   C O D E X              ║
        ║                                                      ║
        ╚══════════════════════════════════════════════════════╝
        """;


    public static readonly string Kimi = """
        ╔══════════════════════════════════════════════════════╗
        ║                                                      ║
        ║              ██╗  ██╗██╗███╗   ███╗██╗               ║
        ║              ██║ ██╔╝██║████╗ ████║██║               ║
        ║              █████╔╝ ██║██╔████╔██║██║               ║
        ║              ██╔═██╗ ██║██║╚██╔╝██║██║               ║
        ║              ██║  ██╗██║██║ ╚═╝ ██║██║               ║
        ║              ╚═╝  ╚═╝╚═╝╚═╝     ╚═╝╚═╝               ║
        ║                                                      ║
        ║                 ◐  K I M I   C O D E                 ║
        ║                                                      ║
        ╚══════════════════════════════════════════════════════╝
        """;
}
