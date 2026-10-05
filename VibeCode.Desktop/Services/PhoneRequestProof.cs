using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace VibeCode.Services;

/// <summary>
/// Possession of a token alone is insufficient: every request is signed by the enrolled P-256 key.
/// The process epoch invalidates captured requests after a restart; the bounded nonce cache rejects replay
/// within the timestamp window. Callers serialize validation with enrollment and revocation.
/// </summary>
internal sealed class PhoneRequestProof
{
    private const long ClockSkewSeconds = 120;
    private const int MaxNonces = 8192;
    private readonly Dictionary<string, long> _nonces = new(StringComparer.Ordinal);
    public string Epoch { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    public static string? NormalizePublicKey(string? encoded)
    {
        if (encoded is null || encoded.Length is < 80 or > 256) return null;
        try
        {
            var bytes = Convert.FromBase64String(encoded);
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(bytes, out var read);
            if (read != bytes.Length || key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7")
                return null;
            return Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    public bool Verify(string publicKey, string token, string method, string target, string body,
        IReadOnlyDictionary<string, string> headers, DateTimeOffset now)
    {
        if (!headers.TryGetValue("X-VibeCode-Epoch", out var epoch) || epoch != Epoch ||
            !headers.TryGetValue("X-VibeCode-Time", out var timestamp) || timestamp.Length > 12 ||
            !long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ||
            seconds < now.ToUnixTimeSeconds() - ClockSkewSeconds || seconds > now.ToUnixTimeSeconds() + ClockSkewSeconds ||
            !headers.TryGetValue("X-VibeCode-Nonce", out var nonce) || nonce.Length != 32 ||
            !nonce.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') ||
            !headers.TryGetValue("X-VibeCode-Signature", out var signature) || signature.Length is < 80 or > 128 ||
            string.IsNullOrEmpty(publicKey)) return false;

        foreach (var expired in _nonces.Where(x => x.Value < now.ToUnixTimeSeconds()).Select(x => x.Key).ToArray())
            _nonces.Remove(expired);
        // Never evict live entries to make room: eviction would make a previously used signature valid again.
        if (_nonces.ContainsKey(nonce) || _nonces.Count >= MaxNonces) return false;

        try
        {
            var bytes = Convert.FromBase64String(publicKey);
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(bytes, out var read);
            if (read != bytes.Length) return false;
            var canonical = Canonical(Epoch, timestamp, nonce, method, target, body, token);
            if (!key.VerifyData(Encoding.UTF8.GetBytes(canonical), Convert.FromBase64String(signature),
                    HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)) return false;
            _nonces.Add(nonce, seconds + ClockSkewSeconds);
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return false;
        }
    }

    internal static string Canonical(string epoch, string timestamp, string nonce, string method,
        string target, string body, string token) =>
        string.Join("\n", "vibecode-device-v1", epoch, timestamp, nonce, method, target,
            Hash(body), Hash(token));

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
