using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace VibeCode.Services;

/// <summary>
/// One request and one reply, encrypted independently of TLS. The pinned TLS connection authenticates the
/// ephemeral P-256 exchange. HKDF-SHA256 binds both public keys and the purpose, and derives separate 256-bit
/// AES-GCM keys for each direction. Only public handshake material travels outside the encrypted frames.
/// Discovery uses the same primitive with a separate purpose; its result remains an untrusted address hint.
/// </summary>
internal sealed class PhoneAesChannel : IDisposable
{
    internal static readonly byte[] Magic = Encoding.ASCII.GetBytes("VIBECODE-AES256-GCM-1\r\n\r\n");
    internal static readonly byte[] DiscoveryMagic = Encoding.ASCII.GetBytes("VIBECODE-DISCOVER-2\n");
    internal const int MaxPublicKeyBytes = 128;
    internal const int MaxRequestBytes = 1024 * 1024 + 16 * 1024 + 4;
    internal const int MaxResponseBytes = 64 * 1024 * 1024;
    internal const int DiscoveryBytes = 1024;
    private const int NonceBytes = 12, TagBytes = 16;
    private readonly byte[] _sendKey, _receiveKey, _sendAad, _receiveAad;
    private bool _sent, _received;

    private PhoneAesChannel(byte[] sendKey, byte[] receiveKey, byte[] sendAad, byte[] receiveAad)
        => (_sendKey, _receiveKey, _sendAad, _receiveAad) = (sendKey, receiveKey, sendAad, receiveAad);

    internal static PhoneAesChannel Create(ECDiffieHellman local, byte[] clientPublic, byte[] serverPublic,
        bool server, string purpose)
    {
        if (clientPublic.Length > MaxPublicKeyBytes || serverPublic.Length > MaxPublicKeyBytes)
            throw new CryptographicException("Invalid encryption handshake.");
        using var peer = ECDiffieHellman.Create();
        var peerBytes = server ? clientPublic : serverPublic;
        peer.ImportSubjectPublicKeyInfo(peerBytes, out var read);
        if (read != peerBytes.Length || peer.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7")
            throw new CryptographicException("P-256 is required.");
        var secret = local.DeriveRawSecretAgreement(peer.PublicKey);
        try
        {
            var context = Encoding.ASCII.GetBytes("vibecode-aes256-gcm-v1/" + purpose);
            var salt = SHA256.HashData(context.Concat(clientPublic).Concat(serverPublic).ToArray());
            var toPc = Encoding.ASCII.GetBytes("phone-to-pc");
            var toPhone = Encoding.ASCII.GetBytes("pc-to-phone");
            var requestKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, salt, toPc);
            var responseKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, salt, toPhone);
            var requestAad = salt.Concat(toPc).ToArray();
            var responseAad = salt.Concat(toPhone).ToArray();
            return server
                ? new(responseKey, requestKey, responseAad, requestAad)
                : new(requestKey, responseKey, requestAad, responseAad);
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    internal static async Task<PhoneAesChannel> AcceptAsync(Stream stream, CancellationToken cancel)
    {
        var magic = new byte[Magic.Length];
        await stream.ReadExactlyAsync(magic, cancel).ConfigureAwait(false);
        if (!magic.AsSpan().SequenceEqual(Magic)) throw new CryptographicException("AES-256 is required.");
        var clientPublic = await ReadBlobAsync(stream, MaxPublicKeyBytes, cancel).ConfigureAwait(false);
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var serverPublic = key.ExportSubjectPublicKeyInfo();
        var channel = Create(key, clientPublic, serverPublic, server: true, "bridge");
        try
        {
            await stream.WriteAsync(Magic, cancel).ConfigureAwait(false);
            await WriteBlobAsync(stream, serverPublic, cancel).ConfigureAwait(false);
            return channel;
        }
        catch { channel.Dispose(); throw; }
    }

    internal byte[] Seal(byte[] plain)
    {
        if (_sent) throw new CryptographicException("A connection carries only one outgoing message.");
        _sent = true;
        var frame = new byte[NonceBytes + plain.Length + TagBytes];
        RandomNumberGenerator.Fill(frame.AsSpan(0, NonceBytes));
        using var aes = new AesGcm(_sendKey, TagBytes);
        aes.Encrypt(frame.AsSpan(0, NonceBytes), plain, frame.AsSpan(NonceBytes, plain.Length),
            frame.AsSpan(NonceBytes + plain.Length, TagBytes), _sendAad);
        return frame;
    }

    internal byte[] Open(byte[] frame)
    {
        if (_received || frame.Length < NonceBytes + TagBytes)
            throw new CryptographicException("Invalid or repeated encrypted message.");
        _received = true;
        var plain = new byte[frame.Length - NonceBytes - TagBytes];
        using var aes = new AesGcm(_receiveKey, TagBytes);
        aes.Decrypt(frame.AsSpan(0, NonceBytes), frame.AsSpan(NonceBytes, plain.Length),
            frame.AsSpan(NonceBytes + plain.Length, TagBytes), plain, _receiveAad);
        return plain;
    }

    internal async Task<byte[]> ReceiveAsync(Stream stream, int maxPlainBytes, CancellationToken cancel)
        => Open(await ReadBlobAsync(stream, maxPlainBytes + NonceBytes + TagBytes, cancel).ConfigureAwait(false));

    internal Task SendAsync(Stream stream, byte[] plain, CancellationToken cancel)
        => WriteBlobAsync(stream, Seal(plain), cancel);

    internal static async Task<byte[]> ReadBlobAsync(Stream stream, int maxBytes, CancellationToken cancel)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancel).ConfigureAwait(false);
        var size = BinaryPrimitives.ReadInt32BigEndian(header);
        if (size < 1 || size > maxBytes) throw new IOException("Invalid encrypted frame length.");
        var bytes = new byte[size];
        await stream.ReadExactlyAsync(bytes, cancel).ConfigureAwait(false);
        return bytes;
    }

    internal static async Task WriteBlobAsync(Stream stream, byte[] bytes, CancellationToken cancel)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, bytes.Length);
        await stream.WriteAsync(header, cancel).ConfigureAwait(false);
        await stream.WriteAsync(bytes, cancel).ConfigureAwait(false);
        await stream.FlushAsync(cancel).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _sent = _received = true;
        CryptographicOperations.ZeroMemory(_sendKey);
        CryptographicOperations.ZeroMemory(_receiveKey);
    }
}
