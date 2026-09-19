using System.IO;
using System.Text;

namespace VibeCode.Services;

/// <summary>A complete text file, including its encoding and the bytes last read or saved.</summary>
public sealed class EditableCodeFile
{
    public const int MaxBytes = 4_000_000;
    private byte[] _savedBytes;
    private readonly Encoding _encoding;
    private readonly byte[] _preamble;

    public string FilePath { get; }
    public string SavedText { get; private set; }

    private EditableCodeFile(string path, byte[] bytes, Encoding encoding, int preambleLength)
    {
        FilePath = path;
        _savedBytes = bytes;
        _encoding = encoding;
        _preamble = bytes[..preambleLength];
        SavedText = encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);
        if (SavedText.Contains('\0')) throw new IOException("This file is not a supported text document.");
    }

    public static EditableCodeFile Open(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var bytes = ReadBytes(fullPath);
        // UTF-32 must be tested before UTF-16 because their little-endian BOMs overlap.
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe, 0x00, 0x00 }))
            return new(fullPath, bytes, new UTF32Encoding(false, true, true), 4);
        if (bytes.AsSpan().StartsWith(new byte[] { 0x00, 0x00, 0xfe, 0xff }))
            return new(fullPath, bytes, new UTF32Encoding(true, true, true), 4);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe }))
            return new(fullPath, bytes, new UnicodeEncoding(false, true, true), 2);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff }))
            return new(fullPath, bytes, new UnicodeEncoding(true, true, true), 2);
        var utf8Bom = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf });
        return new(fullPath, bytes, new UTF8Encoding(utf8Bom, true), utf8Bom ? 3 : 0);
    }

    public bool HasChanges(string text) => !string.Equals(text, SavedText, StringComparison.Ordinal);

    public void Save(string text)
    {
        if (!HasChanges(text)) return;
        var content = _encoding.GetBytes(text);
        var bytes = new byte[_preamble.Length + content.Length];
        _preamble.CopyTo(bytes, 0);
        content.CopyTo(bytes, _preamble.Length);
        if (bytes.Length > MaxBytes) throw new IOException("The edited file exceeds the 4 MB editor limit.");
        if ((File.GetAttributes(FilePath) & FileAttributes.ReadOnly) != 0)
            throw new IOException("This file is read-only. Make it writable, then try Save again.");

        var temporary = FilePath + ".vibecode-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
            // Keep writers out during the final comparison/replacement. File.Replace also ensures that an
            // interrupted write cannot leave the destination truncated or save only a preview's first rows.
            using var current = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (current.Length != _savedBytes.Length)
                throw ChangedOnDisk();
            var currentBytes = new byte[_savedBytes.Length];
            current.ReadExactly(currentBytes);
            if (!currentBytes.AsSpan().SequenceEqual(_savedBytes)) throw ChangedOnDisk();
            ReplaceFile(temporary, FilePath);
            _savedBytes = bytes;
            SavedText = text;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch { /* A cleanup failure must not hide the save error or clear the editor's unsaved state. */ }
        }
    }

    private static IOException ChangedOnDisk() => new(
        "The file changed on disk while you were editing. Your edits are still here. " +
        "Copy them before reopening the file to compare with the newer version.");

    private static void ReplaceFile(string temporary, string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                try { File.Replace(temporary, path, null); }
                catch (UnauthorizedAccessException)
                {
                    // Content edits need not require WRITE_DAC for metadata merging. Normal file
                    // write/delete permissions still apply when an unrelated metadata merge is skipped.
                    File.Replace(temporary, path, null, ignoreMetadataErrors: true);
                }
                return;
            }
            catch (IOException error) when (attempt < 5 && (error.HResult & 0xffff) is 32 or 33 or 1175)
            {
                // File watchers and antivirus readers can briefly deny deletion after a write. These
                // errors leave both names intact; allow the reader to finish before reporting failure.
                Thread.Sleep(25 * (attempt + 1));
            }
        }
    }

    private static byte[] ReadBytes(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (input.Length > MaxBytes) throw new IOException("This file is too large to edit here (4 MB maximum).");
        var bytes = new byte[checked((int)input.Length)];
        input.ReadExactly(bytes);
        return bytes;
    }
}
