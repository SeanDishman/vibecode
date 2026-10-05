using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using VibeCode.Services;
using VibeCode.UI;
using System.Windows.Threading;

internal static class Program
{
    private static int _checks;
    private static readonly string Scratch = Path.Combine(Path.GetTempPath(), "vibecode-phone-security-" + Guid.NewGuid().ToString("n"));
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Type ServiceType = typeof(PhoneBridgeService);
    private static readonly Type RequestType = ServiceType.GetNestedType("Request", BindingFlags.NonPublic)!;

    [STAThread]
    private static async Task<int> Main()
    {
        // Set before initializing any desktop service: fixtures must never read/write the user's bridge state.
        Environment.SetEnvironmentVariable("VIBECODE_DATA_DIR", Scratch);
        Directory.CreateDirectory(Scratch);
        try
        {
            ProofTests();
            await ServiceTests();
            await HttpTests();
            Console.WriteLine($"PASS: {_checks} phone security checks. Isolated fixtures only; no listener, real phone or model prompt.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void Check(string name, bool valid)
    {
        if (!valid) throw new InvalidOperationException("FAIL: " + name);
        _checks++;
        Console.WriteLine("PASS: " + name);
    }

    private static Dictionary<string, string> Signed(PhoneRequestProof proof, ECDsa key, string token,
        string method = "POST", string target = "/api/chats/abc/send?v=1", string body = "{\"text\":\"hello\"}", long? timestamp = null)
    {
        var time = (timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var canonical = PhoneRequestProof.Canonical(proof.Epoch, time, nonce, method, target, body, token);
        return new(StringComparer.OrdinalIgnoreCase)
        {
            ["Authorization"] = "Bearer " + token,
            ["X-VibeCode-Epoch"] = proof.Epoch, ["X-VibeCode-Time"] = time, ["X-VibeCode-Nonce"] = nonce,
            ["X-VibeCode-Signature"] = Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(canonical),
                HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)),
        };
    }

    private static void ProofTests()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        var proof = new PhoneRequestProof();
        const string token = "isolated-token";
        const string body = "{\"text\":\"hello\"}";
        const string target = "/api/chats/abc/send?v=1";
        bool Verify(Dictionary<string, string> h, string m = "POST", string t = target, string b = body, string tok = token) =>
            proof.Verify(publicKey, tok, m, t, b, h, DateTimeOffset.UtcNow);
        Check("P-256 public key accepted", PhoneRequestProof.NormalizePublicKey(publicKey) == publicKey);
        Check("malformed public key rejected", PhoneRequestProof.NormalizePublicKey("not-a-public-key") is null);
        using var otherCurve = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Check("different EC curve rejected", PhoneRequestProof.NormalizePublicKey(Convert.ToBase64String(otherCurve.ExportSubjectPublicKeyInfo())) is null);
        var signed = Signed(proof, key, token);
        Check("correct device signature accepted", Verify(signed));
        Check("replayed signature rejected", !Verify(signed));
        Check("copied token without signature rejected", !Verify(new(StringComparer.OrdinalIgnoreCase) { ["Authorization"] = "Bearer " + token }));
        Check("copied token signed by another device rejected", !Verify(Signed(proof, stranger, token)));
        Check("changed method rejected", !Verify(Signed(proof, key, token), m: "GET"));
        Check("changed query rejected", !Verify(Signed(proof, key, token), t: target + "&x=1"));
        Check("changed path rejected", !Verify(Signed(proof, key, token), t: "/api/chats/other/send?v=1"));
        Check("changed body rejected", !Verify(Signed(proof, key, token), b: "{\"text\":\"changed\"}"));
        Check("changed token rejected", !Verify(Signed(proof, key, token), tok: "another-token"));
        Check("stale timestamp rejected", !Verify(Signed(proof, key, token, timestamp: DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 121)));
        Check("future timestamp rejected", !Verify(Signed(proof, key, token, timestamp: DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 125)));
        var wrongEpoch = Signed(proof, key, token);
        wrongEpoch["X-VibeCode-Epoch"] = new('0', 64);
        Check("changed epoch rejected", !Verify(wrongEpoch));
        var changedNonce = Signed(proof, key, token);
        changedNonce["X-VibeCode-Nonce"] = new('a', 32);
        Check("changed nonce rejected", !Verify(changedNonce));
        Check("restart rejects captured request", !new PhoneRequestProof().Verify(publicKey, token, "POST", target, body,
            Signed(proof, key, token), DateTimeOffset.UtcNow));

        var start = new ProcessStartInfo("java") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "InteropProof.java"));
        start.ArgumentList.Add(proof.Epoch);
        var time = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        start.ArgumentList.Add(time);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException("Java interop fixture failed: " + error);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-VibeCode-Epoch"] = proof.Epoch, ["X-VibeCode-Time"] = time,
            ["X-VibeCode-Nonce"] = "0123456789abcdef0123456789abcdef", ["X-VibeCode-Signature"] = output[1],
        };
        Check("Java JCA signature accepted by .NET verifier", proof.Verify(output[0], "isolated-interop-token", "POST",
            "/api/chats/abc/send?v=1&wait=25", Encoding.UTF8.GetString(Convert.FromBase64String(output[2])), headers, DateTimeOffset.UtcNow));
    }

    private static PhoneBridgeService NewService(string name)
    {
        Environment.SetEnvironmentVariable("VIBECODE_DATA_DIR", Path.Combine(Scratch, name));
        return (PhoneBridgeService)Activator.CreateInstance(ServiceType, nonPublic: true)!;
    }

    private static void Field(PhoneBridgeService s, string name, object? value) => ServiceType.GetField(name, Private)!.SetValue(s, value);
    private static object GetField(PhoneBridgeService s, string name) => ServiceType.GetField(name, Private)!.GetValue(s)!;
    private static object Request(string target, string method, string body, Dictionary<string, string>? headers = null) =>
        Activator.CreateInstance(RequestType, method, target.Split('?')[0], target.Contains('?')
                ? target.Split('?')[1].Split('&').Select(x => x.Split('=')).ToDictionary(x => x[0], x => x[1])
                : new Dictionary<string, string>(),
            headers ?? new(StringComparer.OrdinalIgnoreCase), body, target)!;

    private static async Task<(int Status, JsonObject Body)> Route(PhoneBridgeService service, object request, string remote = "fixture")
    {
        var task = (Task)ServiceType.GetMethod("RouteAsync", Private)!.Invoke(service, new[] { request, remote, (object)CancellationToken.None })!;
        await task;
        var response = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var status = (int)response.GetType().GetProperty("Status")!.GetValue(response)!;
        var body = JsonNode.Parse((string)response.GetType().GetProperty("Body")!.GetValue(response)!)!.AsObject();
        return (status, body);
    }

    private static object PairRequest(string key, string code = "123456") => Request("/api/pair", "POST",
        new JsonObject { ["code"] = code, ["name"] = "Fixture", ["devicePublicKey"] = key }.ToJsonString());

    private static async Task ServiceTests()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        var service = NewService("manual");
        foreach (var route in new[] { "/api/chats", "/api/folders", "/api/chats/abc/options", "/api/chats/abc/send", "/api/unpair", "/api/settings", "/api/ws" })
            Check("unauthenticated route rejected " + route, (await Route(service, Request(route, "GET", ""), route)).Status == 401);
        Check("closed pairing rejected", (await Route(service, PairRequest(publicKey))).Status == 403);
        Field(service, "_pairingCode", "123456"); Field(service, "_pairingExpires", DateTime.UtcNow.AddMinutes(-1));
        Check("expired manual pairing rejected", (await Route(service, PairRequest(publicKey))).Status == 403);
        Field(service, "_pairingExpires", DateTime.UtcNow.AddMinutes(5));
        Check("wrong manual code rejected", (await Route(service, PairRequest(publicKey, "999999"))).Status == 403);
        var claims = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(() => Route(service, PairRequest(publicKey), "claim-" + i))));
        Check("concurrent pairing issues exactly one credential", claims.Count(x => x.Status == 200) == 1 && service.Devices.Count == 1);
        var token = claims.Single(x => x.Status == 200).Body["token"]!.GetValue<string>();
        Check("a used pairing code cannot pair another phone", (await Route(service, PairRequest(publicKey))).Status == 403);
        Check("bare copied token refused by real route", (await Route(service, Request("/api/chats", "GET", "", new(StringComparer.OrdinalIgnoreCase) { ["Authorization"] = "Bearer " + token }))).Status == 403);
        var epoch = (string)GetField(service, "_requestProof").GetType().GetProperty("Epoch")!.GetValue(GetField(service, "_requestProof"))!;
        Dictionary<string, string> RequestProof(string method, string target, string body)
        {
            var p = new PhoneRequestProof();
            // The fixture verifier and production service are separate instances; sign using the service epoch.
            var h = Signed(p, key, token, method, target, body);
            h["X-VibeCode-Epoch"] = epoch;
            var canonical = PhoneRequestProof.Canonical(epoch, h["X-VibeCode-Time"], h["X-VibeCode-Nonce"], method, target, body, token);
            h["X-VibeCode-Signature"] = Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(canonical), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
            return h;
        }
        var auth = RequestProof("GET", "/api/chats", "");
        Check("signed route passes auth without desktop fixture attached", (await Route(service, Request("/api/chats", "GET", "", auth))).Status == 503);
        Check("replayed route rejected without erasing pairing", (await Route(service, Request("/api/chats", "GET", "", auth))).Status == 403);
        var badTime = RequestProof("GET", "/api/chats", "");
        badTime["X-VibeCode-Time"] = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1000).ToString();
        Check("clock skew rejection preserves pairing", (await Route(service, Request("/api/chats", "GET", "", badTime))).Status == 403);
        var oldEpoch = RequestProof("GET", "/api/chats", "");
        oldEpoch["X-VibeCode-Epoch"] = new string('0', 64);
        Check("epoch mismatch requests challenge refresh", (await Route(service, Request("/api/chats", "GET", "", oldEpoch))).Status == 401);
        for (var i = 0; i < 10; i++) await Route(service, Request("/api/chats", "GET", ""), "rate-fixture");
        Check("authentication rate limit does not erase pairing", (await Route(service, Request("/api/chats", "GET", "", RequestProof("GET", "/api/chats", "")), "rate-fixture")).Status == 429);
        var mirror = new PhoneBridgeMirror((MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel)), Dispatcher.CurrentDispatcher);
        typeof(PhoneBridgeMirror).GetField("_lastRequest", Private)!.SetValue(mirror, DateTime.UtcNow);
        typeof(PhoneBridgeMirror).GetField("_built", Private)!.SetValue(mirror, true);
        typeof(PhoneBridgeMirror).GetField("_listVersion", Private)!.SetValue(mirror, 1);
        Field(service, "_mirror", mirror);
        Field(service, "_vm", RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel)));
        const string pollTarget = "/api/chats?v=1&wait=30";
        var pendingPoll = Route(service, Request(pollTarget, "GET", "", RequestProof("GET", pollTarget, "")));
        Check("authenticated long poll waits", !pendingPoll.IsCompleted);
        Check("signed unpair revokes active device", (await Route(service, Request("/api/unpair", "POST", "{}", RequestProof("POST", "/api/unpair", "{}")))).Status == 200);
        typeof(PhoneBridgeMirror).GetField("_listVersion", Private)!.SetValue(mirror, 2);
        ((TaskCompletionSource)typeof(PhoneBridgeMirror).GetField("_pulse", Private)!.GetValue(mirror)!).TrySetResult();
        Check("revocation suppresses in-flight transcript response", (await pendingPoll).Status == 401);
        Check("fresh request from revoked device rejected", (await Route(service, Request("/api/chats", "GET", "", RequestProof("GET", "/api/chats", "")))).Status == 401);
        Check("revocation persisted", PhoneBridgeStore.LoadDevices().Count == 0);

        service = NewService("attempt-limit");
        Field(service, "_pairingCode", "123456"); Field(service, "_pairingExpires", DateTime.UtcNow.AddMinutes(5));
        for (var i = 0; i < 5; i++) await Route(service, PairRequest(publicKey, "000000"));
        Check("five failed guesses close pairing", (await Route(service, PairRequest(publicKey))).Status == 403);

        service = NewService("enrollment");
        const string secret = "isolated-enrollment-secret";
        var enrollment = new PhoneEnrolment { Id = "fixture", SecretHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret))),
            IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddHours(24) };
        Enrolments(service).Add(enrollment);
        PhoneBridgeStore.SaveEnrolments(new[] { enrollment });
        object Enroll(string supplied = secret) => Request("/api/enroll", "POST", new JsonObject {
            ["secret"] = supplied, ["devicePublicKey"] = publicKey, ["name"] = "Fixture" }.ToJsonString());
        Check("wrong enrollment secret rejected", (await Route(service, Enroll("wrong"))).Status == 403);
        enrollment.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        Check("expired enrollment rejected", (await Route(service, Enroll())).Status == 403);
        enrollment.ExpiresAt = DateTimeOffset.UtcNow.AddHours(24);
        var enrolls = await Task.WhenAll(Enumerable.Range(0, 10).Select(i => Task.Run(() => Route(service, Enroll(), "enroll-" + i))));
        Check("enrollment race issues one credential", enrolls.Count(x => x.Status == 200) == 1);
        Check("enrollment is persisted as spent", PhoneBridgeStore.LoadEnrolments().Single().UsedAt is not null);
        service.Revoke(service.Devices.Single());
        Check("revocation does not revive enrollment", (await Route(service, Enroll())).Status == 403);
        var restarted = (PhoneBridgeService)Activator.CreateInstance(ServiceType, nonPublic: true)!;
        Check("spent enrollment rejected after restart", (await Route(restarted, Enroll())).Status == 403);

        await MultiplePhoneTests(publicKey, key);

        service = NewService("legacy");
        ((List<PhoneDevice>)GetField(service, "_devices")).Add(new PhoneDevice { TokenHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes("legacy"))) });
        Check("legacy bearer-only registration fails closed", (await Route(service, Request("/api/chats", "GET", "", new(StringComparer.OrdinalIgnoreCase) { ["Authorization"] = "Bearer legacy" }))).Status == 401);

        service = NewService("disk-failure");
        Directory.CreateDirectory(AppSettings.Dir);
        File.WriteAllText(Path.Combine(AppSettings.Dir, "phone-bridge"), "fixture preventing directory creation");
        Field(service, "_pairingCode", "123456"); Field(service, "_pairingExpires", DateTime.UtcNow.AddMinutes(5));
        Check("disk failure never issues credential", (await Route(service, PairRequest(publicKey))).Status == 503 && service.Devices.Count == 0);
    }

    private static List<PhoneEnrolment> Enrolments(PhoneBridgeService service) => (List<PhoneEnrolment>)GetField(service, "_enrolments");

    /// <summary>Signs a request the way the phone app does, against this service instance's auth epoch.</summary>
    private static Dictionary<string, string> SignedFor(PhoneBridgeService service, ECDsa key, string token,
        string method = "GET", string target = "/api/chats", string body = "")
    {
        var proof = GetField(service, "_requestProof");
        var epoch = (string)proof.GetType().GetProperty("Epoch")!.GetValue(proof)!;
        var headers = Signed(new PhoneRequestProof(), key, token, method, target, body);
        headers["X-VibeCode-Epoch"] = epoch;
        var canonical = PhoneRequestProof.Canonical(epoch, headers["X-VibeCode-Time"], headers["X-VibeCode-Nonce"], method, target, body, token);
        headers["X-VibeCode-Signature"] = Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(canonical),
            HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        return headers;
    }

    /// <summary>Several phones on one PC: each pairs with its own code or its own generated app, signs with its own
    /// key, and is revoked on its own.</summary>
    private static async Task MultiplePhoneTests(string firstPublicKey, ECDsa firstKey)
    {
        using var secondKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var secondPublicKey = Convert.ToBase64String(secondKey.ExportSubjectPublicKeyInfo());
        var service = NewService("multiple-manual");
        Field(service, "_pairingCode", "123456"); Field(service, "_pairingExpires", DateTime.UtcNow.AddMinutes(5));
        var first = await Route(service, PairRequest(firstPublicKey));
        Check("a used code does not pair a second phone", (await Route(service, PairRequest(secondPublicKey))).Status == 403);
        Field(service, "_pairingCode", "654321"); Field(service, "_pairingExpires", DateTime.UtcNow.AddMinutes(5));
        var second = await Route(service, PairRequest(secondPublicKey, "654321"));
        Check("a second phone pairs with its own code while the first stays paired",
            first.Status == 200 && second.Status == 200 && service.Devices.Count == 2 && PhoneBridgeStore.LoadDevices().Count == 2);
        var firstToken = first.Body["token"]!.GetValue<string>();
        var secondToken = second.Body["token"]!.GetValue<string>();
        async Task<int> Chats(ECDsa key, string token) =>
            (await Route(service, Request("/api/chats", "GET", "", SignedFor(service, key, token)))).Status;
        // 503 means authenticated, then refused only because no desktop window is attached to this fixture.
        Check("each paired phone authenticates with its own token and key",
            await Chats(firstKey, firstToken) == 503 && await Chats(secondKey, secondToken) == 503);
        Check("a phone's token signed with the other phone's key is rejected", await Chats(firstKey, secondToken) == 403);
        service.Revoke(service.Devices.Single(d => d.Id == first.Body["deviceId"]!.GetValue<string>()));
        Check("revoking one phone leaves the other working",
            await Chats(firstKey, firstToken) == 401 && await Chats(secondKey, secondToken) == 503
            && PhoneBridgeStore.LoadDevices().Single().Id == second.Body["deviceId"]!.GetValue<string>());

        service = NewService("multiple-apps");
        static PhoneEnrolment Ticket(string id, string secret) => new()
        {
            Id = id, SecretHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret))),
            IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddHours(24),
        };
        Enrolments(service).AddRange(new[] { Ticket("app-a", "secret-for-phone-a"), Ticket("app-b", "secret-for-phone-b") });
        PhoneBridgeStore.SaveEnrolments(Enrolments(service));
        static object Enroll(string secret, string key) => Request("/api/enroll", "POST", new JsonObject
        {
            ["secret"] = secret, ["devicePublicKey"] = key, ["name"] = "Fixture",
        }.ToJsonString());
        Check("two generated apps are outstanding at once", service.PendingEnrolmentCount == 2);
        Check("the second app enrolls its phone first", (await Route(service, Enroll("secret-for-phone-b", secondPublicKey))).Status == 200);
        Check("claiming one app leaves the other pending",
            Enrolments(service).Single(e => e.Id == "app-a").Pending && !Enrolments(service).Single(e => e.Id == "app-b").Pending);
        Check("the other app still enrolls a different phone",
            (await Route(service, Enroll("secret-for-phone-a", firstPublicKey))).Status == 200
            && service.Devices.Count == 2 && PhoneBridgeStore.LoadDevices().Count == 2);
        Check("a used app cannot enroll again", (await Route(service, Enroll("secret-for-phone-b", secondPublicKey))).Status == 403);
        Check("both claims are persisted", PhoneBridgeStore.LoadEnrolments().All(e => e.UsedAt is not null));
        var restarted = (PhoneBridgeService)Activator.CreateInstance(ServiceType, nonPublic: true)!;
        Check("both phones are still paired after a restart", restarted.Devices.Count == 2);

        NewService("legacy-enrolment");
        Directory.CreateDirectory(PhoneBridgeStore.Dir);
        File.WriteAllText(Path.Combine(PhoneBridgeStore.Dir, "enrolment.json"),
            System.Text.Json.JsonSerializer.Serialize(Ticket("legacy", "secret-from-the-one-phone-era")));
        service = (PhoneBridgeService)Activator.CreateInstance(ServiceType, nonPublic: true)!;
        Check("an app generated before several phones were allowed still enrolls",
            (await Route(service, Enroll("secret-from-the-one-phone-era", firstPublicKey))).Status == 200);
        Check("the legacy enrolment file is replaced by the list",
            !File.Exists(Path.Combine(PhoneBridgeStore.Dir, "enrolment.json")) && PhoneBridgeStore.LoadEnrolments().Single().UsedAt is not null);
    }

    private static async Task HttpTests()
    {
        async Task<object?> Parse(string wire)
        {
            using var bytes = new MemoryStream(Encoding.UTF8.GetBytes(wire));
            var task = (Task)ServiceType.GetMethod("ReadRequestAsync", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, new object[] { bytes, CancellationToken.None })!;
            await task;
            return task.GetType().GetProperty("Result")!.GetValue(task);
        }
        Check("valid fixed-length body parsed", await Parse("POST /api/pair HTTP/1.1\r\nContent-Length: 2\r\n\r\n{}") is not null);
        Check("valid chunked body parsed", await Parse("POST /api/pair HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n2\r\n{}\r\n0\r\n\r\n") is not null);
        Check("duplicate authorization headers rejected", await Parse("GET /api/chats HTTP/1.1\r\nAuthorization: a\r\nAuthorization: b\r\n\r\n") is null);
        Check("ambiguous body framing rejected", await Parse("POST /api/pair HTTP/1.1\r\nTransfer-Encoding: chunked\r\nContent-Length: 0\r\n\r\n0\r\n\r\n") is null);
        Check("malformed content length rejected", await Parse("POST /api/pair HTTP/1.1\r\nContent-Length: no\r\n\r\n") is null);
        Check("oversized chunk extension rejected", await Parse("POST /api/pair HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n2;" + new string('a', 17000)) is null);
        Check("malformed chunk delimiter rejected", await Parse("POST /api/pair HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n2\r\n{}XX0\r\n\r\n") is null);
        Check("truncated trailers rejected", await Parse("POST /api/pair HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n0\r\nx-test: incomplete") is null);
        Check("oversized header rejected", await Parse("GET /api/ping HTTP/1.1\r\nX-Test: " + new string('x', 17000) + "\r\n\r\n") is null);
    }
}
