using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;

namespace VibeCode.Services;

/// <summary>
/// Builds the "install this on your phone" APK.
///
/// The flow this exists to support: the user turns phone access on, and instead of being told to type a
/// six-digit code into an app they have to find first, they get a file. That file already knows this machine's
/// address and certificate, and carries a one-shot secret it trades for a real credential the first time it
/// runs. After that the secret is dead and the app is bound to this PC by a pinned certificate it can never be
/// talked out of.
/// </summary>
public static class PhoneApkBuilder
{
    private const string TemplateResource = "VibeCode.Assets.vibecode-mobile.apk";

    /// <summary>False on a build that did not ship the mobile APK, so the UI can say so instead of failing.</summary>
    public static bool TemplateAvailable =>
        typeof(PhoneApkBuilder).Assembly.GetManifestResourceNames().Contains(TemplateResource);

    /// <summary>
    /// Mints a fresh enrolment and writes a signed, personalised APK. Returns the path it was written to.
    /// Any previously generated APK stops working, because issuing a new enrolment retires the old secret.
    /// </summary>
    public static string Build()
    {
        if (!TemplateAvailable)
            throw new InvalidOperationException(
                "This build does not include the phone app. Build mobile/VibeCodeMobile and rebuild VibeCode.");

        var bridge = PhoneBridgeService.Instance;
        if (!bridge.Running) bridge.Start();
        if (!bridge.Running)
            throw new InvalidOperationException($"Phone access could not start: {bridge.Error}");

        var addresses = PhoneBridgeStore.LocalAddresses().Select(a => a.ToString()).ToList();
        if (addresses.Count == 0)
            throw new InvalidOperationException("This PC is not on a network, so a phone would have nothing to reach.");

        // Issued last, so a failure above never burns a secret. From here on the old APK is dead.
        var secret = bridge.IssueEnrolment();

        var hosts = new JsonArray();
        foreach (var address in addresses) hosts.Add(address);

        var payload = new JsonObject
        {
            ["configured"] = true,
            // Every address this machine answers on, best guess first. A phone tries them in order, which is what
            // keeps the app working when the PC has both Wi-Fi and Ethernet, or when DHCP shuffles one of them.
            ["hosts"] = hosts,
            ["port"] = bridge.Port,
            ["fingerprint"] = bridge.Fingerprint,
            ["pcName"] = Environment.MachineName,
            ["secret"] = secret,
            ["issued"] = DateTimeOffset.UtcNow.ToString("O"),
        }.ToJsonString();

        var template = ExtractTemplate();
        try
        {
            var safeName = string.Concat(Environment.MachineName.Split(Path.GetInvalidFileNameChars()));
            var destination = Path.Combine(PhoneBridgeStore.ApkDir, $"VibeCode-{safeName}.apk");
            using var signer = ApkSigningIdentity.LoadOrCreate();
            ApkPersonalizer.Personalize(template, destination, payload, signer);
            bridge.NoteEnrolmentApk(destination);
            return destination;
        }
        finally
        {
            try { File.Delete(template); } catch { /* temp file */ }
        }
    }

    private static string ExtractTemplate()
    {
        using var stream = typeof(PhoneApkBuilder).Assembly.GetManifestResourceStream(TemplateResource)
                           ?? throw new InvalidOperationException("the packaged phone app could not be read");
        var path = Path.Combine(Path.GetTempPath(), $"vibecode-template-{Guid.NewGuid():N}.apk");
        using var file = File.Create(path);
        stream.CopyTo(file);
        return path;
    }
}
