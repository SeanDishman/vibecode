using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace VibeCode.Services;

/// <summary>
/// Turns the shipped template APK into one that belongs to this PC and no other.
///
/// The generated app has this machine's address, its TLS certificate fingerprint, and a single-use enrolment
/// secret baked into it, which is what makes "install this and it just works" possible without the user typing a
/// pairing code — and what makes the app refuse to talk to any other machine for the rest of its life.
///
/// Two design choices are worth stating because they are what keep this from being fragile:
///
///   * The APK is NOT rebuilt. The template already contains a 2 KB uncompressed placeholder asset, so
///     personalising it is an overwrite of a fixed byte range plus two CRC patches. No entry moves, nothing is
///     recompressed, and the resources.arsc alignment that Android 11+ insists on is preserved for free.
///   * Signing is implemented here rather than shelled out to apksigner, because a user's machine has no JDK and
///     no Android SDK on it. APK Signature Scheme v2 is a SHA-256 Merkle-ish digest plus one RSA signature, so
///     the whole thing is a few hundred lines of .NET crypto and no external dependency at all.
/// </summary>
public static class ApkPersonalizer
{
    /// <summary>Name of the placeholder asset inside the template. Must match the file in the mobile project.</summary>
    private const string EnrolmentEntry = "assets/vibecode-enroll.vcenroll";
    /// <summary>Fixed on-disk size of that asset. The payload is padded with spaces to exactly this many bytes.</summary>
    private const int EnrolmentSlotBytes = 2048;

    private static readonly byte[] SigningBlockMagic = Encoding.ASCII.GetBytes("APK Sig Block 42");
    private const uint V2BlockId = 0x7109871a;
    /// <summary>RSASSA-PKCS1-v1_5 over SHA-256, with chunked SHA-256 content digest.</summary>
    private const uint SigAlgRsaPkcs1Sha256 = 0x0103;
    private const int ChunkSize = 1024 * 1024;

    /// <summary>
    /// Writes a personalised, signed APK.
    /// </summary>
    /// <param name="templatePath">The APK produced by the mobile build.</param>
    /// <param name="destinationPath">Where to write the result.</param>
    /// <param name="payloadJson">Enrolment JSON to bake in. Must fit the fixed slot.</param>
    /// <param name="signer">Certificate with a private key, from <see cref="ApkSigningIdentity"/>.</param>
    public static void Personalize(string templatePath, string destinationPath, string payloadJson,
        X509Certificate2 signer)
    {
        var apk = File.ReadAllBytes(templatePath);
        var zip = ZipLayout.Read(apk);

        WriteEnrolment(apk, zip, payloadJson);

        // Everything after the entries is rebuilt: the template arrives signed by the mobile build's own key, and
        // that block has to go before ours can be the one Android checks.
        var signed = Sign(apk, zip, signer);

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var tmp = destinationPath + ".tmp";
        File.WriteAllBytes(tmp, signed);
        File.Move(tmp, destinationPath, overwrite: true);
    }

    // ---------------- the enrolment slot ----------------

    private static void WriteEnrolment(byte[] apk, ZipLayout zip, string payloadJson)
    {
        var payload = Encoding.UTF8.GetBytes(payloadJson);
        if (payload.Length > EnrolmentSlotBytes)
            throw new InvalidOperationException(
                $"enrolment payload is {payload.Length} bytes and the slot is {EnrolmentSlotBytes}");

        var entry = zip.Entries.FirstOrDefault(e => e.Name == EnrolmentEntry)
                    ?? throw new InvalidOperationException(
                        $"the template APK has no '{EnrolmentEntry}' - rebuild the mobile app");
        if (entry.CompressionMethod != 0)
            throw new InvalidOperationException(
                $"'{EnrolmentEntry}' is compressed; it must be STORED (check noCompress in build.gradle.kts)");
        if (entry.UncompressedSize != EnrolmentSlotBytes)
            throw new InvalidOperationException(
                $"'{EnrolmentEntry}' is {entry.UncompressedSize} bytes, expected {EnrolmentSlotBytes}");

        // Space padding keeps the length identical and stays valid JSON-with-trailing-whitespace on the phone.
        var slot = apk.AsSpan(entry.DataOffset, EnrolmentSlotBytes);
        payload.CopyTo(slot);
        slot[payload.Length..].Fill((byte)' ');

        // The CRC lives in two places and Android checks both.
        var crc = Crc32(slot);
        BinaryPrimitives.WriteUInt32LittleEndian(apk.AsSpan(entry.LocalHeaderOffset + 14), crc);
        BinaryPrimitives.WriteUInt32LittleEndian(apk.AsSpan(entry.CentralHeaderOffset + 16), crc);
    }

    // ---------------- APK Signature Scheme v2 ----------------

    private static byte[] Sign(byte[] apk, ZipLayout zip, X509Certificate2 signer)
    {
        // The three regions the v2 digest covers. The old signing block sits between entries and the central
        // directory and is deliberately not one of them - it is replaced wholesale.
        var entries = apk.AsMemory(0, zip.EntriesEnd);
        var centralDirectory = apk.AsMemory(zip.CentralDirectoryOffset, zip.CentralDirectorySize);

        // The digested EOCD claims the central directory starts where the signing block will start. Android
        // recomputes it the same way, which is what stops the block's own size from changing the digest.
        var eocd = apk.AsSpan(zip.EocdOffset, apk.Length - zip.EocdOffset).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(eocd.AsSpan(16), (uint)zip.EntriesEnd);

        var digest = ChunkedSha256(entries, centralDirectory, eocd);
        var signedData = BuildSignedData(digest, signer);

        using var rsa = signer.GetRSAPrivateKey()
                        ?? throw new InvalidOperationException("the APK signing key is not an RSA key");
        var signature = rsa.SignData(signedData, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var block = BuildSigningBlock(signedData, signature, rsa);

        var output = new byte[zip.EntriesEnd + block.Length + zip.CentralDirectorySize + eocd.Length];
        entries.Span.CopyTo(output);
        block.CopyTo(output.AsSpan(zip.EntriesEnd));
        centralDirectory.Span.CopyTo(output.AsSpan(zip.EntriesEnd + block.Length));
        eocd.CopyTo(output.AsSpan(zip.EntriesEnd + block.Length + zip.CentralDirectorySize));

        // The EOCD that actually ships points past the block at the real central directory.
        BinaryPrimitives.WriteUInt32LittleEndian(
            output.AsSpan(zip.EntriesEnd + block.Length + zip.CentralDirectorySize + 16),
            (uint)(zip.EntriesEnd + block.Length));

        return output;
    }

    /// <summary>
    /// The v2 content digest: each 1 MB chunk of the covered regions is hashed with an 0xa5 prefix, and the
    /// concatenated chunk digests are hashed again with an 0x5a prefix and the chunk count.
    /// </summary>
    private static byte[] ChunkedSha256(ReadOnlyMemory<byte> entries, ReadOnlyMemory<byte> centralDirectory,
        byte[] eocd)
    {
        var regions = new[] { entries, centralDirectory, new ReadOnlyMemory<byte>(eocd) };

        var count = 0;
        foreach (var region in regions) count += (region.Length + ChunkSize - 1) / ChunkSize;

        var concatenated = new byte[5 + count * 32];
        concatenated[0] = 0x5a;
        BinaryPrimitives.WriteInt32LittleEndian(concatenated.AsSpan(1), count);

        var header = new byte[5];
        header[0] = 0xa5;
        var written = 5;
        foreach (var region in regions)
        {
            for (var offset = 0; offset < region.Length; offset += ChunkSize)
            {
                var length = Math.Min(ChunkSize, region.Length - offset);
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1), length);

                using var sha = SHA256.Create();
                sha.TransformBlock(header, 0, header.Length, null, 0);
                var chunk = region.Slice(offset, length).ToArray();
                sha.TransformFinalBlock(chunk, 0, chunk.Length);
                sha.Hash!.CopyTo(concatenated, written);
                written += 32;
            }
        }

        return SHA256.HashData(concatenated);
    }

    private static byte[] BuildSignedData(byte[] digest, X509Certificate2 signer)
    {
        // sequence of digests: one entry, our algorithm paired with the content digest
        var digestEntry = Concat(UInt32(SigAlgRsaPkcs1Sha256), LengthPrefixed(digest));
        var digests = LengthPrefixed(LengthPrefixed(digestEntry));

        var certificates = LengthPrefixed(LengthPrefixed(signer.RawData));

        // No additional attributes. The v3 "proof of rotation" attribute lives here when a key is being rotated;
        // this signer never rotates, so the sequence is empty rather than absent.
        var attributes = LengthPrefixed(Array.Empty<byte>());

        return Concat(digests, certificates, attributes);
    }

    private static byte[] BuildSigningBlock(byte[] signedData, byte[] signature, RSA publicKey)
    {
        var signatureEntry = Concat(UInt32(SigAlgRsaPkcs1Sha256), LengthPrefixed(signature));
        var signatures = LengthPrefixed(LengthPrefixed(signatureEntry));
        var publicKeyInfo = LengthPrefixed(publicKey.ExportSubjectPublicKeyInfo());

        var signer = Concat(LengthPrefixed(signedData), signatures, publicKeyInfo);
        var signers = LengthPrefixed(LengthPrefixed(signer));

        // One ID-value pair. The uint64 length covers the 4-byte id plus the value, but not itself.
        var pair = Concat(UInt64(4 + (ulong)signers.Length), UInt32(V2BlockId), signers);

        // Block = size || pairs || size || magic, where size counts everything after the first size field.
        var sizeOfBlock = (ulong)pair.Length + 8 + 16;
        return Concat(UInt64(sizeOfBlock), pair, UInt64(sizeOfBlock), SigningBlockMagic);
    }

    // ---------------- little-endian helpers ----------------

    private static byte[] UInt32(uint value)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        return b;
    }

    private static byte[] UInt64(ulong value)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(b, value);
        return b;
    }

    private static byte[] LengthPrefixed(byte[] payload) => Concat(UInt32((uint)payload.Length), payload);

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }
        return result;
    }

    // ---------------- just enough ZIP ----------------

    private sealed class ZipEntry
    {
        public string Name = "";
        public int CentralHeaderOffset;
        public int LocalHeaderOffset;
        public int DataOffset;
        public ushort CompressionMethod;
        public uint UncompressedSize;
    }

    private sealed class ZipLayout
    {
        public int EocdOffset;
        public int CentralDirectoryOffset;
        public int CentralDirectorySize;
        /// <summary>Where the ZIP entries stop - i.e. where any APK Signing Block begins, or the central
        /// directory if there is none.</summary>
        public int EntriesEnd;
        public List<ZipEntry> Entries = new();

        public static ZipLayout Read(byte[] apk)
        {
            var eocd = FindEocd(apk);
            var directorySize = BinaryPrimitives.ReadUInt32LittleEndian(apk.AsSpan(eocd + 12));
            var directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(apk.AsSpan(eocd + 16));
            if (directoryOffset == uint.MaxValue || directorySize == uint.MaxValue)
                throw new InvalidOperationException("ZIP64 APKs are not supported here");
            if ((ulong)directoryOffset + directorySize > (ulong)eocd)
                throw new InvalidOperationException("the APK central directory is outside the package");
            var layout = new ZipLayout
            {
                EocdOffset = eocd,
                CentralDirectorySize = (int)directorySize,
                CentralDirectoryOffset = (int)directoryOffset,
            };

            layout.EntriesEnd = layout.CentralDirectoryOffset;
            // An APK that has been signed already carries a signing block immediately before the central
            // directory. It is not part of the entries and must not survive into the output.
            if (layout.CentralDirectoryOffset >= 24 &&
                apk.AsSpan(layout.CentralDirectoryOffset - 16, 16).SequenceEqual(SigningBlockMagic))
            {
                var size = BinaryPrimitives.ReadUInt64LittleEndian(apk.AsSpan(layout.CentralDirectoryOffset - 24));
                var start = layout.CentralDirectoryOffset - 8 - (int)size;
                if (start < 0 || start > layout.CentralDirectoryOffset)
                    throw new InvalidOperationException("the template APK's signing block is malformed");
                layout.EntriesEnd = start;
            }

            layout.ReadCentralDirectory(apk);
            return layout;
        }

        private void ReadCentralDirectory(byte[] apk)
        {
            var offset = CentralDirectoryOffset;
            var end = CentralDirectoryOffset + CentralDirectorySize;
            while (offset < end)
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(apk.AsSpan(offset)) != 0x02014b50) break;
                var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(apk.AsSpan(offset + 28));
                var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(apk.AsSpan(offset + 30));
                var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(apk.AsSpan(offset + 32));
                var localHeader = (int)BinaryPrimitives.ReadUInt32LittleEndian(apk.AsSpan(offset + 42));

                var entry = new ZipEntry
                {
                    Name = Encoding.UTF8.GetString(apk, offset + 46, nameLength),
                    CentralHeaderOffset = offset,
                    LocalHeaderOffset = localHeader,
                    CompressionMethod = BinaryPrimitives.ReadUInt16LittleEndian(apk.AsSpan(offset + 10)),
                    UncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(apk.AsSpan(offset + 24)),
                };

                // The local header repeats the name and extra fields, and its extra field length routinely
                // differs from the central one (that is where zipalign puts its padding), so the data offset has
                // to be read from the local header rather than assumed.
                var localNameLength = BinaryPrimitives.ReadUInt16LittleEndian(apk.AsSpan(localHeader + 26));
                var localExtraLength = BinaryPrimitives.ReadUInt16LittleEndian(apk.AsSpan(localHeader + 28));
                entry.DataOffset = localHeader + 30 + localNameLength + localExtraLength;

                Entries.Add(entry);
                offset += 46 + nameLength + extraLength + commentLength;
            }
        }

        private static int FindEocd(byte[] apk)
        {
            // The EOCD is at the end, but a trailing comment can push it back up to 64 KB.
            var limit = Math.Max(0, apk.Length - 0x10000 - 22);
            for (var i = apk.Length - 22; i >= limit; i--)
                if (BinaryPrimitives.ReadUInt32LittleEndian(apk.AsSpan(i)) == 0x06054b50)
                    return i;
            throw new InvalidOperationException("not a ZIP file - no end of central directory found");
        }
    }

    // ---------------- CRC32 ----------------

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}
