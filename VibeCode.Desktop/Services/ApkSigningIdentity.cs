using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace VibeCode.Services;

/// <summary>
/// The key this PC signs generated APKs with.
///
/// It is created once and then kept forever, which matters more than it looks: Android identifies an app by its
/// signing certificate, so regenerating the key would make every future APK a *different* app as far as the
/// phone is concerned — the user would have to uninstall before they could install the new one, and would lose
/// the pairing in the process. Keeping the key stable means a regenerated APK installs straight over the old one.
///
/// It is not the same key as the TLS identity and must not be: one authenticates the server to the phone, the
/// other authenticates the package to Android, and giving a single key both jobs means compromising either one
/// costs you both.
/// </summary>
public static class ApkSigningIdentity
{
    private static readonly object Gate = new();
    private static string KeyPath => Path.Combine(PhoneBridgeStore.Dir, "apk-signing.bin");

    /// <summary>The signing certificate, with its private key. Created on first use.</summary>
    public static X509Certificate2 LoadOrCreate()
    {
        lock (Gate)
        {
            var existing = TryLoad();
            if (existing is not null) return existing;

            // 3072-bit RSA: comfortably past the 2048 floor, and APK signing happens once per generated app so
            // the extra cost is invisible.
            using var rsa = RSA.Create(3072);
            var request = new CertificateRequest(
                new X500DistinguishedName($"CN=VibeCode Phone, O=VibeCode, OU={Environment.MachineName}"),
                rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));

            var now = DateTimeOffset.UtcNow;
            // Android rejects an APK whose signing certificate expires before the app would plausibly stop being
            // used; the platform convention is decades, not years.
            using var created = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(30));

            var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var pfx = created.Export(X509ContentType.Pfx, password);
            Persist(pfx, password);
            return new X509Certificate2(pfx, password, X509KeyStorageFlags.Exportable);
        }
    }

    private static X509Certificate2? TryLoad()
    {
        try
        {
            if (!File.Exists(KeyPath)) return null;
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(KeyPath), null, DataProtectionScope.CurrentUser);
            if (plain.Length < 4) return null;
            var passLength = BitConverter.ToInt32(plain, 0);
            if (passLength <= 0 || passLength > plain.Length - 4) return null;
            var password = Encoding.UTF8.GetString(plain, 4, passLength);
            var pfx = plain.Skip(4 + passLength).ToArray();
            return new X509Certificate2(pfx, password, X509KeyStorageFlags.Exportable);
        }
        catch (Exception ex)
        {
            // Regenerating means the next APK is a different app to Android, so this is worth a log line rather
            // than silence - it explains why a reinstall suddenly demanded an uninstall first.
            CrashLog.Note("PhoneBridge", $"APK signing key unreadable, generating a new one - {ex.Message}");
            return null;
        }
    }

    private static void Persist(byte[] pfx, string password)
    {
        Directory.CreateDirectory(PhoneBridgeStore.Dir);
        var pass = Encoding.UTF8.GetBytes(password);
        var plain = new byte[4 + pass.Length + pfx.Length];
        BitConverter.GetBytes(pass.Length).CopyTo(plain, 0);
        pass.CopyTo(plain, 4);
        pfx.CopyTo(plain, 4 + pass.Length);
        // DPAPI to the current user: another account on this machine cannot lift the key and publish an APK that
        // Android would treat as an update to this one.
        var sealedBytes = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
        Array.Clear(plain);
        File.WriteAllBytes(KeyPath, sealedBytes);
    }
}
