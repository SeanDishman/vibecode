using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using VibeCode.Services;

internal static class ApkPackageTests
{
    public static void Run(string template)
    {
        if (!File.Exists(template))
        {
            Console.WriteLine("SKIP: optional APK test; build the mobile template to enable it");
            return;
        }
        var before = SHA256.HashData(File.ReadAllBytes(template));
        var output = Environment.GetEnvironmentVariable("VIBECODE_TEST_MOBILE_OUTPUT")
            ?? Path.Combine(Environment.CurrentDirectory, "personalized-test.apk");
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=VibeCode package test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var signer = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(30));
        var payload = new JsonObject
        {
            ["configured"] = true, ["pcName"] = "Test desktop", ["hosts"] = new JsonArray("127.0.0.1"),
            ["port"] = 8765, ["fingerprint"] = new string('0', 64), ["secret"] = "synthetic-enrollment",
        };
        ApkPersonalizer.Personalize(template, output, payload.ToJsonString(), signer);
        using (var archive = ZipFile.OpenRead(output))
        {
            var entry = archive.GetEntry("assets/vibecode-enroll.vcenroll") ?? throw new InvalidOperationException("Enrollment is missing");
            if (entry.Length != 2048 || entry.CompressedLength != 2048) throw new InvalidOperationException("Enrollment slot changed layout");
            using var reader = new StreamReader(entry.Open());
            if (!JsonNode.DeepEquals(JsonNode.Parse(reader.ReadToEnd()), payload)) throw new InvalidOperationException("Enrollment payload did not round-trip");
        }
        if (!before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(template)))) throw new InvalidOperationException("Personalization modified the source template");
        // This fixture isolates signature validation from configured-enrollment validation in the publish checks.
        ApkPersonalizer.Personalize(template, Path.Combine(Path.GetDirectoryName(output)!, "unconfigured-signed-test.apk"),
            "{\"configured\":false}", signer);
        Console.WriteLine("PASS: APK personalization preserves the template and enrollment layout");
    }
}
