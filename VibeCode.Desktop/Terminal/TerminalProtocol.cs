using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json;

namespace VibeCode.Terminal;

// Shared by the desktop host and the console-only entry point. No WPF objects cross the pipe.
public sealed record TerminalAgent(int Number, string Name, string Provider, string Model, string Status);
public sealed record TerminalRow(long Id, string Speaker, string Kind, string Text);
public sealed record TerminalOption(string? Value, string Label);
public sealed record TerminalControls(string? Model, string ModelLabel, TerminalOption[] Models,
    string? Effort, string EffortLabel, TerminalOption[] Efforts, bool FastMode, bool FastAvailable, bool UltrafastMode = false);
public sealed record TerminalSetting(string Name, string? Value);
public sealed record TerminalSpeech(string State, string Hint);
public sealed record TerminalAttachment(string Id, string Name, string Kind, string Size);
public sealed record TerminalPacket(string Type, string Text = "", bool Accepted = true,
    TerminalAgent[]? Agents = null, TerminalRow[]? Rows = null, string Project = "",
    TerminalControls? Controls = null, TerminalSetting? Setting = null,
    TerminalSpeech? Speech = null, string ContextId = "", TerminalAttachment[]? Attachments = null,
    string[]? Paths = null, string? AttachmentId = null);

public static class TerminalWire
{
    public const int MaxPacketBytes = 4 * 1024 * 1024;
    public const int MaxPromptLength = 100_000;
    public const string PipePrefix = "VibeCode.Terminal.";

    public static async Task WriteAsync(Stream stream, TerminalPacket packet, CancellationToken token = default)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(packet);
        if (bytes.Length > MaxPacketBytes) throw new InvalidDataException("Terminal message is too large.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    public static async Task<TerminalPacket?> ReadAsync(Stream stream, CancellationToken token = default)
    {
        var header = new byte[4];
        var first = await stream.ReadAsync(header.AsMemory(0, 1), token).ConfigureAwait(false);
        if (first == 0) return null;
        await stream.ReadExactlyAsync(header.AsMemory(1), token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaxPacketBytes) throw new InvalidDataException("Invalid terminal message size.");
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<TerminalPacket>(bytes) ?? throw new InvalidDataException("Empty terminal message.");
    }

    // Model/tool output is untrusted terminal text: OSC, CSI, BEL, carriage returns and bidi controls must
    // never change the title, clipboard, screen or input. Only our renderer can emit terminal commands.
    public static string DisplayText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var result = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value == '\n') { result.Append('\n'); continue; }
            if (rune.Value == '\t') { result.Append("    "); continue; }
            var category = Rune.GetUnicodeCategory(rune);
            if (category is System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format) continue;
            result.Append(rune.ToString());
        }
        return result.ToString();
    }
}
