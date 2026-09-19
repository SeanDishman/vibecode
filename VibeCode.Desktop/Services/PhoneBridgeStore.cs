using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VibeCode.Services;

/// <summary>One phone that has completed pairing. The token itself is never stored - only its SHA-256 - so
/// reading devices.json off disk does not hand anybody a working credential.</summary>
public sealed class PhoneDevice
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Base64 SHA-256 of the bearer token. Compared in constant time.</summary>
    public string TokenHash { get; set; } = "";
    public DateTimeOffset PairedAt { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    /// <summary>Last address this device was seen from. Display only - never used to authorise anything.</summary>
    public string LastAddress { get; set; } = "";
}

/// <summary>
/// An APK that has been generated but whose phone has not enrolled yet.
///
/// The secret itself is never written down - only its SHA-256 - so the outstanding credential exists in exactly
/// two places: inside the APK the user is about to install, and in the memory of whoever is holding it. Once a
/// phone trades it for a real device token, <see cref="UsedAt"/> is stamped and the secret is dead for good.
/// </summary>
public sealed class PhoneEnrolment
{
    public string Id { get; set; } = "";
    /// <summary>Base64 SHA-256 of the enrolment secret. Compared in constant time.</summary>
    public string SecretHash { get; set; } = "";
    public DateTimeOffset IssuedAt { get; set; }
    /// <summary>Null while the APK is still waiting for its phone. Stamped exactly once.</summary>
    public DateTimeOffset? UsedAt { get; set; }
    /// <summary>Name the enrolling phone reported, for the devices list.</summary>
    public string DeviceName { get; set; } = "";
    /// <summary>The one device this APK was claimed by. Recorded so the desktop can name it, and so revoking that
    /// device does not quietly hand the coupon back to whoever else has a copy of the file - a spent enrolment
    /// stays spent for good, and a second phone needs a second APK.</summary>
    public string BoundDeviceId { get; set; } = "";
    /// <summary>Address the claiming phone came from, for the "was that me?" question.</summary>
    public string BoundAddress { get; set; } = "";
    /// <summary>Where the generated APK was written, so the window can offer to show it again.</summary>
    public string ApkPath { get; set; } = "";
    /// <summary>Certificate fingerprint baked into that APK. An identity reset makes the APK useless, and this
    /// is how the desktop knows to say so instead of leaving the user to guess.</summary>
    public string Fingerprint { get; set; } = "";

    public bool Pending => UsedAt is null;
}

/// <summary>
/// Everything the phone bridge keeps on disk: the paired devices and the TLS identity it serves under.
///
/// The certificate is self-signed and generated once, because there is no CA on a home LAN that could vouch for
/// "the desktop in the next room". The phone pins its SHA-256 on first pair instead (see PhoneBridgeService), so
/// the identity has to be STABLE across restarts - regenerating it would silently invalidate every paired phone.
/// The PFX is sealed with DPAPI to the current Windows user, so another account on the same machine cannot lift
/// the server key and impersonate the bridge.
/// </summary>
public static class PhoneBridgeStore
{
    private static readonly object Gate = new();

    public static string Dir => Path.Combine(AppSettings.Dir, "phone-bridge");
    private static string DevicesPath => Path.Combine(Dir, "devices.json");
    private static string CertPath => Path.Combine(Dir, "identity.bin");
    private static string EnrolmentPath => Path.Combine(Dir, "enrolment.json");
    /// <summary>Where generated APKs are written, one per enrolment.</summary>
    public static string ApkDir => Path.Combine(Dir, "apk");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ---------------- devices ----------------

    public static List<PhoneDevice> LoadDevices()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(DevicesPath)) return new List<PhoneDevice>();
                var text = File.ReadAllText(DevicesPath);
                return JsonSerializer.Deserialize<List<PhoneDevice>>(text, Json) ?? new List<PhoneDevice>();
            }
            catch
            {
                // A corrupt device list must not lock the user out of their own desktop app. Pairing again is a
                // 20-second recovery; a startup crash is not.
                return new List<PhoneDevice>();
            }
        }
    }

    public static void SaveDevices(IEnumerable<PhoneDevice> devices)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var tmp = DevicesPath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(devices.ToList(), Json));
                File.Move(tmp, DevicesPath, overwrite: true);
            }
            catch (Exception ex)
            {
                CrashLog.Note("PhoneBridge", $"device save failed - {ex.Message}");
            }
        }
    }

    // ---------------- outstanding enrolment ----------------

    public static PhoneEnrolment? LoadEnrolment()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(EnrolmentPath)) return null;
                return JsonSerializer.Deserialize<PhoneEnrolment>(File.ReadAllText(EnrolmentPath), Json);
            }
            catch
            {
                // Same reasoning as the device list: a corrupt file means "generate another APK", not a crash.
                return null;
            }
        }
    }

    public static void SaveEnrolment(PhoneEnrolment? enrolment)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                if (enrolment is null)
                {
                    if (File.Exists(EnrolmentPath)) File.Delete(EnrolmentPath);
                    return;
                }
                var tmp = EnrolmentPath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(enrolment, Json));
                File.Move(tmp, EnrolmentPath, overwrite: true);
            }
            catch (Exception ex)
            {
                CrashLog.Note("PhoneBridge", $"enrolment save failed - {ex.Message}");
            }
        }
    }

    // ---------------- TLS identity ----------------

    /// <summary>The bridge's long-lived server certificate, created on first use and reused forever after.</summary>
    public static X509Certificate2 LoadOrCreateCertificate()
    {
        lock (Gate)
        {
            var existing = TryLoadCertificate();
            if (existing is not null) return existing;

            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest(
                new X500DistinguishedName("CN=VibeCode Phone Bridge, O=VibeCode"),
                rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));   // server auth

            // The phone verifies by pinned fingerprint, not by name, but a cert with no SAN at all is rejected
            // outright by some TLS stacks before pinning ever gets a look in.
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("vibecode-phone-bridge");
            san.AddIpAddress(IPAddress.Loopback);
            foreach (var ip in LocalAddresses()) san.AddIpAddress(ip);
            request.CertificateExtensions.Add(san.Build());

            var now = DateTimeOffset.UtcNow;
            using var created = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(10));

            var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var pfx = created.Export(X509ContentType.Pfx, password);
            Persist(pfx, password);

            // Export/re-import so the returned handle owns a persisted key the SslStream can actually use.
            return Reload(pfx, password);
        }
    }

    private static X509Certificate2? TryLoadCertificate()
    {
        try
        {
            if (!File.Exists(CertPath)) return null;
            var sealedBytes = File.ReadAllBytes(CertPath);
            var plain = System.Security.Cryptography.ProtectedData.Unprotect(
                sealedBytes, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            // layout: [4-byte password length][utf8 password][pfx]
            if (plain.Length < 4) return null;
            var passLen = BitConverter.ToInt32(plain, 0);
            if (passLen <= 0 || passLen > plain.Length - 4) return null;
            var password = Encoding.UTF8.GetString(plain, 4, passLen);
            var pfx = plain.Skip(4 + passLen).ToArray();
            return Reload(pfx, password);
        }
        catch (Exception ex)
        {
            CrashLog.Note("PhoneBridge", $"certificate load failed, regenerating - {ex.Message}");
            return null;
        }
    }

    // Deliberately NOT EphemeralKeySet: on Windows, SslStream cannot use an ephemeral private key as a server
    // credential and the handshake fails with a bare "authentication failed". The default flags give a temporary
    // user key container that Schannel accepts and that goes away with the handle.
    private static X509Certificate2 Reload(byte[] pfx, string password) =>
        new(pfx, password, X509KeyStorageFlags.Exportable);

    private static void Persist(byte[] pfx, string password)
    {
        Directory.CreateDirectory(Dir);
        var pass = Encoding.UTF8.GetBytes(password);
        var plain = new byte[4 + pass.Length + pfx.Length];
        BitConverter.GetBytes(pass.Length).CopyTo(plain, 0);
        pass.CopyTo(plain, 4);
        pfx.CopyTo(plain, 4 + pass.Length);
        var sealedBytes = System.Security.Cryptography.ProtectedData.Protect(
            plain, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
        Array.Clear(plain);
        File.WriteAllBytes(CertPath, sealedBytes);
    }

    /// <summary>Deletes the TLS identity. Every paired phone has pinned the old fingerprint, so this necessarily
    /// unpairs all of them - callers must clear the device list in the same breath.</summary>
    public static void ResetIdentity()
    {
        lock (Gate)
        {
            try { if (File.Exists(CertPath)) File.Delete(CertPath); }
            catch (Exception ex) { CrashLog.Note("PhoneBridge", $"identity reset failed - {ex.Message}"); }
        }
    }

    // ---------------- addressing ----------------

    /// <summary>Every routable IPv4 this machine answers on, best candidate first. Used for the "type this on your
    /// phone" line and for the certificate's SAN list.</summary>
    public static List<IPAddress> LocalAddresses()
    {
        var found = new List<(int Rank, IPAddress Address)>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var rank = Rank(nic);
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var bytes = ua.Address.GetAddressBytes();
                    if (bytes[0] == 127) continue;
                    if (bytes[0] == 169 && bytes[1] == 254) continue;   // link-local / no DHCP
                    found.Add((rank, ua.Address));
                }
            }
        }
        catch (Exception ex)
        {
            CrashLog.Note("PhoneBridge", $"address enumeration failed - {ex.Message}");
        }
        return found.OrderBy(f => f.Rank).Select(f => f.Address).Distinct().ToList();
    }

    /// <summary>
    /// How likely a phone is to actually reach this adapter. Lower is better; the phone dials the list in order.
    ///
    /// Ordering matters more than it looks. A wrong address does not fail fast - it burns the full TCP connect
    /// timeout, so a machine with VirtualBox and a VPN installed can spend the best part of half a minute failing
    /// before it ever tries the address that works. Virtual and tunnel adapters are therefore pushed behind real
    /// hardware rather than left to the whim of enumeration order.
    ///
    /// Tunnels are ranked last but deliberately NOT dropped: a mesh VPN like Tailscale is a perfectly good way for
    /// a phone to reach this PC, and is sometimes the only way when the two are not on the same Wi-Fi.
    /// </summary>
    private static int Rank(NetworkInterface nic)
    {
        var description = nic.Description ?? "";
        var name = nic.Name ?? "";

        bool Mentions(params string[] words) => words.Any(w =>
            description.Contains(w, StringComparison.OrdinalIgnoreCase) ||
            name.Contains(w, StringComparison.OrdinalIgnoreCase));

        // A VPN or mesh tunnel. Reachable in some setups, never the first thing to try.
        if (nic.NetworkInterfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp
            || Mentions("tunnel", "wireguard", "openvpn", "tap-", "tailscale", "zerotier", "mullvad", "vpn"))
            return 4;

        // Host-only and NAT adapters belonging to a hypervisor or container runtime. These carry addresses that
        // look entirely routable and are visible to nothing outside this machine.
        if (Mentions("virtualbox", "vmware", "hyper-v", "docker", "wsl", "npcap", "loopback", "bluetooth"))
            return 3;

        if (nic.NetworkInterfaceType is NetworkInterfaceType.Wireless80211) return 0;
        if (nic.NetworkInterfaceType is NetworkInterfaceType.Ethernet
            or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT) return 1;
        return 2;
    }
}
