using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using VibeCode.Services;

internal static partial class Program
{
    private static async Task EncryptionTests()
    {
        using var clientKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var serverKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var clientPublic = clientKey.ExportSubjectPublicKeyInfo();
        var serverPublic = serverKey.ExportSubjectPublicKeyInfo();
        PhoneAesChannel Client(string purpose = "bridge") => PhoneAesChannel.Create(clientKey, clientPublic, serverPublic, false, purpose);
        PhoneAesChannel Server(string purpose = "bridge") => PhoneAesChannel.Create(serverKey, clientPublic, serverPublic, true, purpose);
        var plain = Encoding.UTF8.GetBytes("POST /api/chats/private/send?secret=query HTTP/1.1\r\nAuthorization: Bearer private-token\r\n\r\n{\"text\":\"café ☕\"}");
        using var client = Client();
        using var server = Server();
        var encrypted = client.Seal(plain);
        Check("AES frame hides request target, credential and body",
            !Encoding.UTF8.GetString(encrypted).Contains("private") && encrypted.Length == plain.Length + 28);
        Check("AES request authenticates and round-trips Unicode", server.Open(encrypted).SequenceEqual(plain));
        var response = Encoding.UTF8.GetBytes("HTTP/1.1 403 Forbidden\r\n\r\n{\"error\":\"private refusal\"}");
        Check("AES encrypts error responses in the other direction", client.Open(server.Seal(response)).SequenceEqual(response));

        static bool Refused(Action action)
        {
            try { action(); return false; }
            catch (CryptographicException) { return true; }
        }
        Check("AES request replay on a connection is refused", Refused(() => server.Open(encrypted)));
        Check("AES connection cannot encrypt a second request", Refused(() => client.Seal(plain)));
        foreach (var index in new[] { 0, 12, encrypted.Length - 1 })
        {
            using var target = Server();
            var changed = encrypted.ToArray();
            changed[index] ^= 1;
            Check("AES rejects altered nonce/ciphertext/tag at " + index, Refused(() => target.Open(changed)));
        }
        using (var reflected = Client())
            Check("AES cannot reflect a request as a response", Refused(() => reflected.Open(encrypted)));
        using (var discovery = Server("discovery"))
            Check("AES discovery and bridge keys are separated", Refused(() => discovery.Open(encrypted)));
        using (var otherKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256))
        using (var other = PhoneAesChannel.Create(otherKey, clientPublic, otherKey.ExportSubjectPublicKeyInfo(), true, "bridge"))
            Check("captured ciphertext cannot replay on a fresh connection", Refused(() => other.Open(encrypted)));
        using (var wrongCurve = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP384))
            Check("AES handshake rejects a different curve", Refused(() => PhoneAesChannel.Create(clientKey, clientPublic,
                wrongCurve.ExportSubjectPublicKeyInfo(), false, "bridge")));
        using (var legacy = new MemoryStream(Encoding.ASCII.GetBytes("GET /api/ping HTTP/1.1\r\nHost: localhost\r\n\r\n")))
        {
            var rejected = false;
            try { using var unused = await PhoneAesChannel.AcceptAsync(legacy, CancellationToken.None); }
            catch (CryptographicException) { rejected = true; }
            Check("TLS-only HTTP cannot negotiate the mandatory AES channel", rejected);
        }
        foreach (var length in new[] { -1, 0, int.MaxValue })
        {
            var header = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(header, length);
            using var input = new MemoryStream(header);
            var rejected = false;
            try { await PhoneAesChannel.ReadBlobAsync(input, PhoneAesChannel.MaxRequestBytes, CancellationToken.None); }
            catch (IOException) { rejected = true; }
            Check("invalid frame length rejected before allocation: " + length, rejected);
        }
    }
}
