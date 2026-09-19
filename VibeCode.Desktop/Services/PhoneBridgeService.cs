using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using VibeCode.UI;

namespace VibeCode.Services;

/// <summary>
/// Serves this desktop's chats to the VibeCode phone app over the local network.
///
/// Threat model, because "a server in my chat app" deserves one written down:
///   * The transport is TLS 1.2/1.3 with a self-signed certificate that lives for the life of the install. The
///     phone pins its SHA-256 at pairing time and refuses any other certificate afterwards, so a device that has
///     paired once cannot be talked into trusting a machine impersonating this one on the same Wi-Fi.
///   * Nothing is served without a bearer token. Tokens are 256 bits of CSPRNG output, handed out exactly once
///     (at pairing) and stored here only as a SHA-256, compared in constant time.
///   * Pairing is the only unauthenticated write, and it is closed by default: the user has to open a window on
///     the desktop, which mints a 6-digit code good for five minutes, one successful use, and five wrong guesses.
///   * Everything is scoped to the LAN. There is no relay, no cloud, no port mapping - if the phone is not on the
///     same network as the PC, it cannot see anything.
///
/// What a paired phone can do, stated plainly, because the endpoint list has grown well past "read my chats":
/// it can send prompts, answer permission prompts (including "always allow"), switch the permission mode as far
/// as bypassPermissions, change model and effort, start a new chat in any existing folder, rewind a turn, and
/// close a pane. That is deliberate - the point of the app is to be the desktop from the sofa - and it is not a
/// privilege escalation, because a device that can send one prompt to an agent with Bash already has arbitrary
/// code execution on this machine. The pairing token IS the trust boundary; there is no weaker tier below it.
/// The two things a phone deliberately cannot do are delete a chat (transcripts are not destroyable from a
/// device the user cannot see) and pair another device.
/// </summary>
public sealed class PhoneBridgeService : Observable
{
    public static PhoneBridgeService Instance { get; } = new();

    public const int DefaultPort = 8765;
    private static readonly TimeSpan PairingWindow = TimeSpan.FromMinutes(5);
    private const int MaxPairingAttempts = 5;
    private const int MaxAuthFailuresPerAddress = 10;
    private static readonly TimeSpan AuthLockout = TimeSpan.FromMinutes(1);
    /// <summary>Ceiling on the per-address failure table before expired entries are swept.</summary>
    private const int MaxTrackedAddresses = 1024;
    /// <summary>Sockets served at once. A handful of phones need single digits; this is purely a flood stop.</summary>
    private const int MaxConcurrentConnections = 64;
    /// <summary>Unauthenticated requests one address may make per minute before it is shut out. A phone makes two
    /// during discovery; a scanner makes hundreds.</summary>
    private const int MaxAnonymousRequestsPerMinute = 30;
    private const int MaxHeaderBytes = 16 * 1024;
    private const int MaxBodyBytes = 1024 * 1024;
    private static readonly TimeSpan MaxLongPoll = TimeSpan.FromSeconds(30);
    /// <summary>How long a first-of-its-kind request may wait for the dispatcher to mirror what it asked for.</summary>
    private static readonly TimeSpan FirstBuildWait = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private TcpListener? _listener;
    private PhoneBeacon? _beacon;
    private CancellationTokenSource? _cancel;
    private X509Certificate2? _certificate;
    private PhoneBridgeMirror? _mirror;
    private MainViewModel? _vm;
    private Dispatcher? _ui;
    private readonly List<PhoneDevice> _devices = new();
    private readonly Dictionary<string, (int Failures, DateTime Until)> _lockouts = new(StringComparer.Ordinal);
    /// <summary>Per-address budget for requests that carry no valid token, refilled every minute.</summary>
    private readonly Dictionary<string, (int Count, DateTime Window)> _anonymous = new(StringComparer.Ordinal);
    private int _openConnections;
    private long _blockedRemotes;
    private DateTime _lastBlockedNote;

    private string? _pairingCode;
    private DateTime _pairingExpires;
    private int _pairingAttempts;
    private PhoneEnrolment? _enrolment;
    private int _enrolAttempts;

    private PhoneBridgeService()
    {
        _devices.AddRange(PhoneBridgeStore.LoadDevices());
        _enrolment = PhoneBridgeStore.LoadEnrolment();
        SyncDeviceView();
    }

    // ---------------- observable surface (bound by PhoneBridgeWindow) ----------------

    private bool _running;
    public bool Running { get => _running; private set { if (Set(ref _running, value)) { Raise(nameof(StatusText)); Raise(nameof(Address)); } } }

    private string _error = "";
    public string Error { get => _error; private set { if (Set(ref _error, value)) { Raise(nameof(HasError)); Raise(nameof(StatusText)); } } }
    public bool HasError => _error.Length > 0;

    private int _port = DefaultPort;
    public int Port { get => _port; private set { if (Set(ref _port, value)) Raise(nameof(Address)); } }

    private string _fingerprint = "";
    /// <summary>Full SHA-256 of the server certificate, uppercase hex.</summary>
    public string Fingerprint { get => _fingerprint; private set { if (Set(ref _fingerprint, value)) Raise(nameof(SafetyCode)); } }

    /// <summary>The first four bytes of <see cref="Fingerprint"/>, formatted so a human can compare it to what the
    /// phone shows before approving a pairing. This is the check that turns trust-on-first-use into something an
    /// attacker on the same Wi-Fi cannot quietly win.</summary>
    public string SafetyCode => _fingerprint.Length >= 8
        ? $"{_fingerprint[..4]}-{_fingerprint[4..8]}"
        : "";

    /// <summary>"192.168.1.20:8765" - what the user types into the phone.</summary>
    public string Address
    {
        get
        {
            var ip = PhoneBridgeStore.LocalAddresses().FirstOrDefault();
            return ip is null ? $"(no network):{Port}" : $"{ip}:{Port}";
        }
    }

    private string _pairingDisplay = "";
    /// <summary>The live pairing code, or empty when pairing is closed.</summary>
    public string PairingDisplay { get => _pairingDisplay; private set { if (Set(ref _pairingDisplay, value)) Raise(nameof(PairingOpen)); } }
    public bool PairingOpen => _pairingDisplay.Length > 0;

    private string _pairingCountdown = "";
    public string PairingCountdown { get => _pairingCountdown; private set => Set(ref _pairingCountdown, value); }

    public ObservableCollection<PhoneDevice> Devices { get; } = new();

    public string StatusText => !_running
        ? (HasError ? $"Off — {Error}" : "Off")
        : HasOutstandingEnrolment ? "On — waiting for the app you generated to be installed"
        : Devices.Count == 0 ? "On — no phones paired yet"
        : $"On — {Devices.Count} phone{(Devices.Count == 1 ? "" : "s")} paired";

    // ---------------- extension ----------------

    /// <summary>Whether the Phone extension is turned on (mirrors AppSettings so the titlebar can bind to it).
    /// Distinct from <see cref="Running"/>: this is whether the feature is present in the UI at all, while Running
    /// is whether the socket is actually listening. Switching the extension off also stops the bridge - leaving a
    /// listening socket behind for a feature the user just hid is exactly what the threat model above rules out.
    /// Switching it back on only restores the titlebar button; the bridge is still started from its own window.</summary>
    public bool Enabled
    {
        get => AppSettings.Current.PhoneEnabled;
        set
        {
            if (AppSettings.Current.PhoneEnabled == value) return;
            AppSettings.Current.PhoneEnabled = value;
            AppSettings.Current.Save();
            Raise(nameof(Enabled));
            if (!value) Stop();      // also clears PhoneBridgeEnabled, so a re-enable does not silently relisten
        }
    }

    /// <summary>Re-read the settings-backed flag after something else wrote settings.json (the titlebar button and
    /// the Extensions card both bind straight to <see cref="Enabled"/>).</summary>
    public void NotifyEnabledChanged() => Raise(nameof(Enabled));

    // ---------------- lifecycle ----------------

    /// <summary>Hands the service the live view model. Called once, from the primary window.</summary>
    public void Attach(MainViewModel vm, Dispatcher ui)
    {
        if (_vm is not null) return;
        _vm = vm;
        _ui = ui;
        _mirror = new PhoneBridgeMirror(vm, ui);
        Port = AppSettings.Current.PhoneBridgePort > 0 ? AppSettings.Current.PhoneBridgePort : DefaultPort;
        // The extension gate comes first: a disabled extension must not open a socket on launch even if the bridge
        // was left on before it was switched off.
        if (AppSettings.Current.PhoneEnabled && AppSettings.Current.PhoneBridgeEnabled) Start();
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_listener is not null) return;
            try
            {
                _certificate ??= PhoneBridgeStore.LoadOrCreateCertificate();
                Fingerprint = Convert.ToHexString(SHA256.HashData(_certificate.RawData));

                var listener = new TcpListener(IPAddress.Any, Port);
                listener.Start();
                _listener = listener;
                _cancel = new CancellationTokenSource();
                Error = "";
                Running = true;
                _ = Task.Run(() => AcceptLoopAsync(listener, _cancel.Token));

                // Lets a phone find this PC again after its address changes, which the baked-in address list
                // cannot survive on its own. Best effort - the bridge is fully usable without it.
                _beacon ??= new PhoneBeacon(this);
                _beacon.Start(Port);
            }
            catch (Exception ex)
            {
                _listener = null;
                Running = false;
                Error = ex is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse }
                    ? $"port {Port} is already in use"
                    : ex.Message;
                CrashLog.Note("PhoneBridge", $"start failed - {ex}");
                return;
            }
        }
        AppSettings.Current.PhoneBridgeEnabled = true;
        AppSettings.Current.PhoneBridgePort = Port;
        AppSettings.Current.Save();
    }

    public void Stop(bool remember = true)
    {
        lock (_gate)
        {
            _cancel?.Cancel();
            try { _listener?.Stop(); } catch { /* already down */ }
            try { _beacon?.Stop(); } catch { /* already down */ }
            _listener = null;
            _cancel = null;
            Running = false;
        }
        ClosePairing();
        if (!remember) return;
        AppSettings.Current.PhoneBridgeEnabled = false;
        AppSettings.Current.Save();
    }

    /// <summary>Rebinds on a different port. Every paired phone keeps working - it stores host and port separately
    /// and the pinned fingerprint does not change.</summary>
    public void SetPort(int port)
    {
        if (port is < 1 or > 65535 || port == Port) return;
        var wasRunning = Running;
        if (wasRunning) Stop(remember: false);
        Port = port;
        AppSettings.Current.PhoneBridgePort = port;
        AppSettings.Current.Save();
        if (wasRunning) Start();
    }

    // ---------------- pairing ----------------

    /// <summary>Opens a five-minute window in which one phone may exchange the displayed code for a token.</summary>
    public void OpenPairing()
    {
        if (!Running) Start();
        if (!Running) return;
        lock (_gate)
        {
            _pairingCode = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
            _pairingExpires = DateTime.UtcNow + PairingWindow;
            _pairingAttempts = 0;
        }
        PairingDisplay = $"{_pairingCode![..3]} {_pairingCode[3..]}";
        TickPairingCountdown();
    }

    public void ClosePairing()
    {
        lock (_gate) { _pairingCode = null; _pairingAttempts = 0; }
        PairingDisplay = "";
        PairingCountdown = "";
    }

    /// <summary>Refreshes the countdown label and retires the code when it runs out. Driven by the window's timer
    /// so nothing ticks while the pairing panel is closed.</summary>
    public void TickPairingCountdown()
    {
        string? code;
        DateTime expires;
        lock (_gate) { code = _pairingCode; expires = _pairingExpires; }
        if (code is null) { PairingCountdown = ""; return; }
        var left = expires - DateTime.UtcNow;
        if (left <= TimeSpan.Zero) { ClosePairing(); return; }
        PairingCountdown = $"expires in {left.Minutes}:{left.Seconds:D2}";
    }

    // ---------------- enrolment (the generated-APK path) ----------------

    /// <summary>How many bytes of CSPRNG go into an enrolment secret. 32 bytes is 43 base64url characters, which
    /// is both comfortably past the "at least 32 characters" bar and, more to the point, 256 bits of real entropy -
    /// the length of a password only matters if the characters are unguessable, and these are.</summary>
    private const int EnrolmentSecretBytes = 32;
    private const int MaxEnrolAttempts = 10;

    /// <summary>True while an APK has been generated but no phone has redeemed it yet.</summary>
    public bool HasOutstandingEnrolment { get { lock (_gate) return _enrolment?.Pending == true; } }

    public PhoneEnrolment? Enrolment { get { lock (_gate) return _enrolment; } }

    /// <summary>
    /// Mints a fresh single-use enrolment secret and returns it in the clear - the only moment it is ever
    /// readable. The caller's job is to bake it into an APK and then forget it; only the SHA-256 is kept here.
    /// Any previously outstanding enrolment is replaced, so there is never more than one live un-redeemed APK.
    /// </summary>
    public string IssueEnrolment()
    {
        if (!Running) Start();
        var secret = Base64Url(RandomNumberGenerator.GetBytes(EnrolmentSecretBytes));
        var enrolment = new PhoneEnrolment
        {
            Id = Guid.NewGuid().ToString("n"),
            SecretHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret))),
            IssuedAt = DateTimeOffset.Now,
            Fingerprint = Fingerprint,
        };
        lock (_gate)
        {
            _enrolment = enrolment;
            _enrolAttempts = 0;
        }
        PhoneBridgeStore.SaveEnrolment(enrolment);
        Raise(nameof(HasOutstandingEnrolment));
        Raise(nameof(Enrolment));
        Raise(nameof(StatusText));
        return secret;
    }

    /// <summary>Records where the APK ended up, so the window can point at it later.</summary>
    public void NoteEnrolmentApk(string path)
    {
        PhoneEnrolment? snapshot;
        lock (_gate)
        {
            if (_enrolment is null) return;
            _enrolment.ApkPath = path;
            snapshot = _enrolment;
        }
        PhoneBridgeStore.SaveEnrolment(snapshot);
        Raise(nameof(Enrolment));
    }

    /// <summary>Kills an un-redeemed APK. This is the answer to "I emailed it to myself and now I regret it".</summary>
    public void RevokeEnrolment()
    {
        lock (_gate) _enrolment = null;
        PhoneBridgeStore.SaveEnrolment(null);
        Raise(nameof(HasOutstandingEnrolment));
        Raise(nameof(Enrolment));
        Raise(nameof(StatusText));
    }

    public void Revoke(PhoneDevice device)
    {
        lock (_gate) _devices.RemoveAll(d => d.Id == device.Id);
        PhoneBridgeStore.SaveDevices(SnapshotDevices());
        SyncDeviceView();
    }

    /// <summary>Throws away the TLS identity and every paired phone. The nuclear option for "someone had my
    /// laptop" - after this, no previously paired handset can connect even with its token.</summary>
    public void ResetEverything()
    {
        var wasRunning = Running;
        Stop(remember: false);
        lock (_gate)
        {
            _devices.Clear();
            _certificate?.Dispose();
            _certificate = null;
            // The new identity will have a different fingerprint, so any APK generated against the old one can
            // never connect again. Clearing it here is what stops the window offering a file that cannot work.
            _enrolment = null;
        }
        PhoneBridgeStore.SaveDevices(Array.Empty<PhoneDevice>());
        PhoneBridgeStore.SaveEnrolment(null);
        PhoneBridgeStore.ResetIdentity();
        SyncDeviceView();
        Fingerprint = "";
        Raise(nameof(HasOutstandingEnrolment));
        Raise(nameof(Enrolment));
        if (wasRunning) Start();
    }

    private List<PhoneDevice> SnapshotDevices()
    {
        lock (_gate) return _devices.ToList();
    }

    private void SyncDeviceView()
    {
        var snapshot = SnapshotDevices();
        void Apply()
        {
            Devices.Clear();
            foreach (var d in snapshot.OrderByDescending(d => d.LastSeen)) Devices.Add(d);
            Raise(nameof(StatusText));
        }
        if (_ui is null || _ui.CheckAccess()) Apply();
        else _ui.BeginInvoke(Apply);
    }

    // ---------------- accept loop ----------------

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(cancel).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (Exception ex)
            {
                CrashLog.Note("PhoneBridge", $"accept failed - {ex.Message}");
                await Task.Delay(250, CancellationToken.None).ConfigureAwait(false);
                continue;
            }

            // Two cheap refusals before a single byte is read, because everything below this line costs real work
            // and a scanner should not be able to buy any of it.
            var address = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
            if (!IsLocalNetwork(address))
            {
                // Somebody reached this port from off the LAN - a router with UPnP open, a misconfigured VPN, a
                // hostile host on a routed segment. The bridge has no business talking to any of them, so the
                // connection dies here without so much as a TLS hello to fingerprint.
                NoteBlockedRemote(address);
                try { client.Close(); } catch { /* already gone */ }
                continue;
            }

            if (Interlocked.Increment(ref _openConnections) > MaxConcurrentConnections)
            {
                // A flood of half-open sockets is the cheapest denial of service against a TLS server. Refusing
                // past the cap keeps the desktop responsive instead of letting the accept loop starve.
                Interlocked.Decrement(ref _openConnections);
                try { client.Close(); } catch { /* already gone */ }
                continue;
            }

            _ = Task.Run(() => ServeAsync(client, cancel), CancellationToken.None);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken cancel)
    {
        var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "?";
        try
        {
            using (client)
            {
                client.NoDelay = true;
                await using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
                var cert = _certificate;
                if (cert is null) return;
                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                handshake.CancelAfter(TimeSpan.FromSeconds(15));
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = cert,
                    ClientCertificateRequired = false,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                }, handshake.Token).ConfigureAwait(false);

                var request = await ReadRequestAsync(ssl, cancel).ConfigureAwait(false);
                if (request is null) return;
                var response = await RouteAsync(request, remote, cancel).ConfigureAwait(false);
                await WriteAsync(ssl, response, cancel).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* shutting down, or a phone that walked away */ }
        catch (IOException) { /* connection dropped mid-request - normal on a handset */ }
        catch (AuthenticationException) { /* a client that would not accept our certificate */ }
        catch (Exception ex)
        {
            CrashLog.Note("PhoneBridge", $"request from {remote} failed - {ex.Message}");
        }
        finally
        {
            Interlocked.Decrement(ref _openConnections);
        }
    }

    // ---------------- who is allowed to even connect ----------------

    /// <summary>
    /// Whether an address belongs to a network this machine is actually sharing with a phone.
    ///
    /// This is the control that makes "somebody found the port" boring. The bridge is a LAN service by design -
    /// there is no relay and no reason for a packet from outside the local network to arrive at all - so anything
    /// that is not loopback, RFC1918, link-local, unique-local or CGNAT is dropped before the TLS handshake.
    /// CGNAT (100.64/10) is included because that is the range mesh VPNs like Tailscale hand out; it is not
    /// publicly routable, so allowing it does not put the bridge on the internet.
    /// </summary>
    public static bool IsLocalNetwork(IPAddress? address)
    {
        if (address is null) return false;
        if (IPAddress.IsLoopback(address)) return true;

        // An IPv6 socket reports IPv4 peers as ::ffff:a.b.c.d; judge the address that is really there.
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] switch
            {
                10 => true,                                     // 10.0.0.0/8
                127 => true,                                    // loopback
                169 => b[1] == 254,                             // 169.254.0.0/16 link-local
                172 => b[1] >= 16 && b[1] <= 31,                // 172.16.0.0/12
                192 => b[1] == 168,                             // 192.168.0.0/16
                100 => b[1] >= 64 && b[1] <= 127,               // 100.64.0.0/10 CGNAT / mesh VPN
                _ => false,
            };
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal) return true;
            return (address.GetAddressBytes()[0] & 0xFE) == 0xFC;   // fc00::/7 unique-local
        }

        return false;
    }

    private void NoteBlockedRemote(IPAddress? address)
    {
        // Logged once a minute at most: a scan is thousands of connections and the crash log is not a packet
        // capture. The count is what matters, not each attempt.
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            _blockedRemotes++;
            if (now - _lastBlockedNote < TimeSpan.FromMinutes(1)) return;
            _lastBlockedNote = now;
        }
        CrashLog.Note("PhoneBridge",
            $"refused {_blockedRemotes} connection(s) from outside the local network (most recent {address})");
    }

    // ---------------- HTTP/1.1 (one request per connection) ----------------

    private sealed record Request(string Method, string Path, Dictionary<string, string> Query,
        Dictionary<string, string> Headers, string Body);

    private sealed record Response(int Status, string Body, string ContentType = "application/json; charset=utf-8");

    private static async Task<Request?> ReadRequestAsync(Stream stream, CancellationToken cancel)
    {
        var buffer = new byte[8192];
        var head = new MemoryStream();
        var headerEnd = -1;
        while (headerEnd < 0)
        {
            if (head.Length > MaxHeaderBytes) return null;
            var read = await stream.ReadAsync(buffer, cancel).ConfigureAwait(false);
            if (read <= 0) return null;
            head.Write(buffer, 0, read);
            headerEnd = IndexOfDoubleCrLf(head.GetBuffer(), (int)head.Length);
        }

        var raw = head.GetBuffer();
        var headText = Encoding.ASCII.GetString(raw, 0, headerEnd);
        var lines = headText.Split("\r\n");
        var parts = lines[0].Split(' ');
        if (parts.Length < 2) return null;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var colon = lines[i].IndexOf(':');
            if (colon <= 0) continue;
            headers[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
        }

        var target = parts[1];
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var q = target.IndexOf('?');
        var path = target;
        if (q >= 0)
        {
            path = target[..q];
            foreach (var pair in target[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                if (eq < 0) query[Uri.UnescapeDataString(pair)] = "";
                else query[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..]);
            }
        }

        var bodyStart = headerEnd + 4;
        var leftover = new byte[(int)head.Length - bodyStart];
        Array.Copy(raw, bodyStart, leftover, 0, leftover.Length);

        // Chunked is not optional. A client that serialises JSON straight to the socket (System.Net.Http's own
        // JsonContent is one) cannot know its length in advance and sends Transfer-Encoding: chunked - a server
        // that only understands Content-Length silently reads an empty body and rejects every POST as malformed.
        var chunked = headers.TryGetValue("Transfer-Encoding", out var te)
                      && te.Contains("chunked", StringComparison.OrdinalIgnoreCase);

        byte[] body;
        if (chunked)
        {
            var decoded = await ReadChunkedAsync(stream, leftover, cancel).ConfigureAwait(false);
            if (decoded is null) return null;
            body = decoded;
        }
        else
        {
            var length = headers.TryGetValue("Content-Length", out var cl) && int.TryParse(cl, out var n) ? n : 0;
            if (length is < 0 or > MaxBodyBytes) return null;
            body = new byte[length];
            var copied = Math.Min(leftover.Length, length);
            if (copied > 0) Array.Copy(leftover, body, copied);
            while (copied < length)
            {
                var read = await stream.ReadAsync(body.AsMemory(copied, length - copied), cancel).ConfigureAwait(false);
                if (read <= 0) return null;
                copied += read;
            }
        }

        return new Request(parts[0].ToUpperInvariant(), Uri.UnescapeDataString(path), query, headers,
            Encoding.UTF8.GetString(body));
    }

    /// <summary>Decodes a chunked body. <paramref name="prefix"/> is whatever already arrived in the header read.
    /// Returns null on a malformed or oversized body.</summary>
    private static async Task<byte[]?> ReadChunkedAsync(Stream stream, byte[] prefix, CancellationToken cancel)
    {
        var pending = new List<byte>(prefix);
        var body = new MemoryStream();
        var scratch = new byte[8192];

        async Task<bool> FillAsync()
        {
            var read = await stream.ReadAsync(scratch, cancel).ConfigureAwait(false);
            if (read <= 0) return false;
            pending.AddRange(scratch.AsSpan(0, read).ToArray());
            return true;
        }

        while (true)
        {
            // chunk size line
            int eol;
            while ((eol = FindCrLf(pending)) < 0)
                if (!await FillAsync().ConfigureAwait(false)) return null;

            var sizeLine = Encoding.ASCII.GetString(pending.GetRange(0, eol).ToArray());
            pending.RemoveRange(0, eol + 2);
            var semi = sizeLine.IndexOf(';');          // chunk extensions: present, and ignorable
            if (semi >= 0) sizeLine = sizeLine[..semi];
            if (!int.TryParse(sizeLine.Trim(), System.Globalization.NumberStyles.HexNumber, null, out var size)
                || size < 0) return null;

            if (size == 0)
            {
                // Trailer section, ending at the blank line. Nothing here is used, but it has to be consumed.
                while (true)
                {
                    while ((eol = FindCrLf(pending)) < 0)
                        if (!await FillAsync().ConfigureAwait(false)) return body.ToArray();
                    var trailer = eol;
                    pending.RemoveRange(0, eol + 2);
                    if (trailer == 0) return body.ToArray();
                }
            }

            if (body.Length + size > MaxBodyBytes) return null;
            while (pending.Count < size + 2)
                if (!await FillAsync().ConfigureAwait(false)) return null;
            body.Write(pending.GetRange(0, size).ToArray(), 0, size);
            pending.RemoveRange(0, size + 2);          // chunk data plus its trailing CRLF
        }
    }

    private static int FindCrLf(List<byte> buffer)
    {
        for (var i = 0; i + 1 < buffer.Count; i++)
            if (buffer[i] == 13 && buffer[i + 1] == 10) return i;
        return -1;
    }

    private static int IndexOfDoubleCrLf(byte[] buffer, int length)
    {
        for (var i = 0; i + 3 < length; i++)
            if (buffer[i] == 13 && buffer[i + 1] == 10 && buffer[i + 2] == 13 && buffer[i + 3] == 10) return i;
        return -1;
    }

    private static async Task WriteAsync(Stream stream, Response response, CancellationToken cancel)
    {
        var body = Encoding.UTF8.GetBytes(response.Body);
        var reason = response.Status switch
        {
            200 => "OK", 400 => "Bad Request", 401 => "Unauthorized", 403 => "Forbidden",
            404 => "Not Found", 429 => "Too Many Requests", 503 => "Service Unavailable", _ => "Error",
        };
        var head = new StringBuilder()
            .Append("HTTP/1.1 ").Append(response.Status).Append(' ').Append(reason).Append("\r\n")
            .Append("Content-Type: ").Append(response.ContentType).Append("\r\n")
            .Append("Content-Length: ").Append(body.Length).Append("\r\n")
            // Nothing here is meant for a browser, and none of it should ever sit in a cache.
            .Append("Cache-Control: no-store\r\n")
            .Append("X-Content-Type-Options: nosniff\r\n")
            .Append("Connection: close\r\n\r\n");
        var headBytes = Encoding.ASCII.GetBytes(head.ToString());
        await stream.WriteAsync(headBytes, cancel).ConfigureAwait(false);
        await stream.WriteAsync(body, cancel).ConfigureAwait(false);
        await stream.FlushAsync(cancel).ConfigureAwait(false);
    }

    // ---------------- routing ----------------

    private async Task<Response> RouteAsync(Request request, string remote, CancellationToken cancel)
    {
        // Everything before authentication runs on a per-address budget, so a scanner cannot sit on the unauth
        // endpoints all day. A real phone spends two of these on the whole pairing flow.
        if (request.Path is "/api/ping" or "/api/pair" or "/api/enroll" && !AllowAnonymous(remote))
            return Fail(429, "too many requests");

        if (request.Path == "/api/ping")
        {
            // Deliberately thin. An unauthenticated caller learns that something called vibecode is here and
            // nothing else - not the machine name, not the version, not whether anyone is paired. The name is
            // only added while the user has explicitly opened a window for a new phone, because that is the one
            // moment it is needed (the pairing screen shows it back so the user can confirm the right PC).
            var payload = new JsonObject { ["app"] = "vibecode" };
            var open = PairingOpen || HasOutstandingEnrolment;
            if (open)
            {
                payload["name"] = Environment.MachineName;
                payload["version"] = AppVersion.Current.ToString();
                payload["pairing"] = PairingOpen;
            }
            return Json(payload);
        }

        if (request.Path == "/api/pair" && request.Method == "POST")
            return Pair(request, remote);

        if (request.Path == "/api/enroll" && request.Method == "POST")
            return Enroll(request, remote);

        var device = Authenticate(request, remote);
        if (device is null) return Fail(401, "not paired");

        device.LastSeen = DateTimeOffset.Now;
        device.LastAddress = remote;

        var mirror = _mirror;
        if (mirror is null || _vm is null) return Fail(503, "desktop not ready");
        mirror.Touch();

        if (request.Path == "/api/chats" && request.Method == "GET")
            return await ChatListAsync(mirror, request, cancel).ConfigureAwait(false);

        if (request.Path == "/api/unpair" && request.Method == "POST")
        {
            Revoke(device);
            return Json(new JsonObject { ["ok"] = true });
        }

        // Folders the desktop already knows about, for the phone's "start a chat here" list.
        if (request.Path == "/api/folders" && request.Method == "GET")
            return await FoldersAsync().ConfigureAwait(false);

        // Must be matched before the /api/chats/{id} prefix below, which would otherwise read "new" as a chat id.
        if (request.Path == "/api/chats/new" && request.Method == "POST")
            return await NewChatAsync(request).ConfigureAwait(false);

        // /api/chats/{id}/...
        const string prefix = "/api/chats/";
        if (request.Path.StartsWith(prefix, StringComparison.Ordinal))
        {
            var rest = request.Path[prefix.Length..];
            var slash = rest.IndexOf('/');
            var id = slash < 0 ? rest : rest[..slash];
            var action = slash < 0 ? "" : rest[(slash + 1)..];
            if (id.Length == 0) return Fail(404, "unknown chat");

            return action switch
            {
                "messages" when request.Method == "GET" => await MessagesAsync(mirror, id, request, cancel).ConfigureAwait(false),
                "send" when request.Method == "POST" => await SendAsync(id, request).ConfigureAwait(false),
                "stop" when request.Method == "POST" => await StopAsync(id).ConfigureAwait(false),
                "permission" when request.Method == "POST" => await PermissionAsync(id, request).ConfigureAwait(false),
                "answer" when request.Method == "POST" => await AnswerAsync(id, request).ConfigureAwait(false),
                "plan" when request.Method == "POST" => await PlanAsync(id, request).ConfigureAwait(false),
                "options" when request.Method == "GET" => await OptionsAsync(id).ConfigureAwait(false),
                "model" when request.Method == "POST" => await SetModelAsync(id, request).ConfigureAwait(false),
                "effort" when request.Method == "POST" => await SetEffortAsync(id, request).ConfigureAwait(false),
                "mode" when request.Method == "POST" => await SetModeAsync(id, request).ConfigureAwait(false),
                "fast" when request.Method == "POST" => await SetFastAsync(id, request).ConfigureAwait(false),
                "queue" when request.Method == "POST" => await QueueAsync(id, request).ConfigureAwait(false),
                "undo" when request.Method == "POST" => await UndoAsync(id, request).ConfigureAwait(false),
                "pin" when request.Method == "POST" => await PinAsync(id).ConfigureAwait(false),
                "title" when request.Method == "POST" => await TitleAsync(id, request).ConfigureAwait(false),
                "close" when request.Method == "POST" => await CloseAsync(id).ConfigureAwait(false),
                _ => Fail(404, "unknown endpoint"),
            };
        }

        return Fail(404, "unknown endpoint");
    }

    private async Task<Response> ChatListAsync(PhoneBridgeMirror mirror, Request request, CancellationToken cancel)
    {
        // A client that has never seen the list sends no v at all, and -1 can never equal a real version - so the
        // first request always gets data even though both sides start at version 0.
        var clientVersion = IntArg(request, "v", -1);
        // The mirror is built on a dispatcher tick that has not necessarily run yet. Answering "no chats" before
        // the first rebuild would be a confident lie, so the very first request always waits for one.
        var deadline = DateTime.UtcNow + (mirror.Built ? WaitSeconds(request) : FirstBuildWait);

        while (true)
        {
            var json = mirror.ChatsJson(out var version);
            if (mirror.Built && version != clientVersion) return Raw($"{{\"version\":{version},\"chats\":{json}}}");

            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) return Raw($"{{\"version\":{version},\"unchanged\":true}}");
            await mirror.WaitForChangeAsync(left, cancel).ConfigureAwait(false);
        }
    }

    private async Task<Response> MessagesAsync(PhoneBridgeMirror mirror, string id, Request request, CancellationToken cancel)
    {
        mirror.Watch(id);
        var clientVersion = IntArg(request, "v", -1);
        // Watch() only takes effect on the next rebuild, so the first request for a chat has to give the mirror
        // time to notice - otherwise opening a chat on the phone shows an empty transcript for one poll.
        var wait = WaitSeconds(request);
        if (clientVersion < 0 && wait < FirstBuildWait) wait = FirstBuildWait;
        var deadline = DateTime.UtcNow + wait;

        while (true)
        {
            var payload = mirror.MessagesJson(id, clientVersion);
            if (payload is not null) return Raw(payload);

            // Nothing new. Either the phone is current, or this chat has not been mirrored yet because this is the
            // first request for it - both resolve on the next rebuild tick, so wait for one.
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero)
            {
                if (!mirror.Knows(id) && !await ChatExistsAsync(id).ConfigureAwait(false)) return Fail(404, "unknown chat");
                return Raw($"{{\"version\":{mirror.VersionOf(id)},\"unchanged\":true}}");
            }
            await mirror.WaitForChangeAsync(left > TimeSpan.FromSeconds(1) ? left : TimeSpan.FromSeconds(1), cancel)
                .ConfigureAwait(false);
        }
    }

    private Task<bool> ChatExistsAsync(string id) => OnUi(vm => vm.Chats.Any(c => c.BridgeId == id));

    private async Task<Response> SendAsync(string id, Request request)
    {
        var text = Body(request)?["text"]?.GetValue<string>() ?? "";
        if (string.IsNullOrWhiteSpace(text)) return Fail(400, "empty message");
        if (text.Length > 100_000) return Fail(400, "message too long");

        var sent = await OnUi(vm =>
        {
            var chat = vm.Chats.FirstOrDefault(c => c.BridgeId == id);
            return chat is not null && chat.Send(text);
        }).ConfigureAwait(false);

        return sent ? Json(new JsonObject { ["ok"] = true }) : Fail(404, "chat is not accepting messages");
    }

    private async Task<Response> StopAsync(string id)
    {
        var ok = await OnUi(vm =>
        {
            var chat = vm.Chats.FirstOrDefault(c => c.BridgeId == id);
            if (chat is null) return false;
            chat.Interrupt();
            return true;
        }).ConfigureAwait(false);
        return ok ? Json(new JsonObject { ["ok"] = true }) : Fail(404, "unknown chat");
    }

    private async Task<Response> PermissionAsync(string id, Request request)
    {
        var body = Body(request);
        var requestId = body?["requestId"]?.GetValue<string>() ?? "";
        var allow = body?["allow"]?.GetValue<bool>() ?? false;
        var always = body?["always"]?.GetValue<bool>() ?? false;
        if (requestId.Length == 0) return Fail(400, "missing requestId");

        var ok = await OnUi(vm =>
        {
            var chat = vm.Chats.FirstOrDefault(c => c.BridgeId == id);
            var perm = chat?.Items.OfType<PermItem>().FirstOrDefault(p => p.RequestId == requestId && p.IsPending);
            if (chat is null || perm is null) return false;
            chat.RespondPermission(perm, allow, always: allow && always,
                denyMessage: allow ? null : "Denied from the phone app.");
            return true;
        }).ConfigureAwait(false);

        return ok ? Json(new JsonObject { ["ok"] = true }) : Fail(404, "no pending request with that id");
    }

    /// <summary>Answers an AskUserQuestion card. The phone sends the chosen labels; selection state is applied to
    /// the real option objects here so the desktop card ends up in the same state it would after a click.</summary>
    private async Task<Response> AnswerAsync(string id, Request request)
    {
        var body = Body(request);
        var requestId = body?["requestId"]?.GetValue<string>() ?? "";
        if (requestId.Length == 0) return Fail(400, "missing requestId");
        var answers = body?["answers"] as JsonArray;
        if (answers is null) return Fail(400, "missing answers");

        // Flatten the wire shape off the dispatcher; only the apply step needs the UI thread.
        var chosen = new List<(string Question, HashSet<string> Labels, string Custom)>();
        foreach (var entry in answers)
        {
            if (entry is not JsonObject o) continue;
            var labels = new HashSet<string>(StringComparer.Ordinal);
            if (o["choices"] is JsonArray list)
                foreach (var label in list)
                    if (label?.GetValue<string>() is { Length: > 0 } text) labels.Add(text);
            chosen.Add((o["q"]?.GetValue<string>() ?? "", labels, o["custom"]?.GetValue<string>() ?? ""));
        }

        var ok = await OnUi(vm =>
        {
            var chat = vm.Chats.FirstOrDefault(c => c.BridgeId == id);
            var perm = chat?.Items.OfType<PermItem>().FirstOrDefault(p => p.RequestId == requestId && p.IsPending);
            if (chat is null || perm is null || perm.Kind != "question") return false;

            foreach (var question in perm.Questions)
            {
                var match = chosen.FirstOrDefault(c => string.Equals(c.Question, question.Question, StringComparison.Ordinal));
                // An unanswered question keeps whatever the desktop already had selected rather than being cleared.
                if (match.Labels is null) continue;
                foreach (var option in question.Options) option.Selected = match.Labels.Contains(option.Label);
                question.Custom = match.Custom;
            }
            chat.AnswerQuestion(perm);
            return true;
        }).ConfigureAwait(false);

        return ok ? Json(new JsonObject { ["ok"] = true }) : Fail(404, "no pending question with that id");
    }

    /// <summary>Approves or rejects an ExitPlanMode card. Approving also chooses what mode the chat lands in,
    /// which is the whole point of the desktop's two-button plan card.</summary>
    private async Task<Response> PlanAsync(string id, Request request)
    {
        var body = Body(request);
        var requestId = body?["requestId"]?.GetValue<string>() ?? "";
        if (requestId.Length == 0) return Fail(400, "missing requestId");
        var approve = body?["approve"]?.GetValue<bool>() ?? false;
        var autoAccept = body?["autoAccept"]?.GetValue<bool>() ?? false;
        var feedback = body?["feedback"]?.GetValue<string>() ?? "";

        var ok = await OnUi(vm =>
        {
            var chat = vm.Chats.FirstOrDefault(c => c.BridgeId == id);
            var perm = chat?.Items.OfType<PermItem>().FirstOrDefault(p => p.RequestId == requestId && p.IsPending);
            if (chat is null || perm is null || perm.Kind != "plan") return false;
            chat.DecidePlan(perm, approve, autoAccept, feedback);
            return true;
        }).ConfigureAwait(false);

        return ok ? Json(new JsonObject { ["ok"] = true }) : Fail(404, "no pending plan with that id");
    }

    // ---------------- session controls ----------------

    /// <summary>The model / effort / mode menus for one chat, as the desktop would draw them right now.</summary>
    private async Task<Response> OptionsAsync(string id)
    {
        var payload = await OnUi(vm =>
        {
            var chat = vm.Chats.FirstOrDefault(c => c.BridgeId == id);
            if (chat is null) return null;

            var models = new JsonArray();
            var catalog = chat.Models.Count > 0
                ? chat.Models.ToList()
                : ProviderModelCatalog.For(ProviderModelCatalog.Normalize(chat.Provider)).ToList();
            foreach (var model in catalog)
                models.Add(new JsonObject
                {
                    ["value"] = model.Value,
                    ["label"] = model.ShortName,
                    ["description"] = model.Description,
                    ["fast"] = model.SupportsFastMode,
                });

            var efforts = new JsonArray();
            foreach (var effort in chat.EffortOptions.ToList())
                efforts.Add(new JsonObject
                {
                    ["value"] = effort.Value,
                    ["label"] = effort.Label,
                    ["description"] = effort.Description,
                    ["rank"] = effort.Rank,
                    ["steps"] = effort.MeterSteps,
                });

            var commands = new JsonArray();
            foreach (var command in chat.Commands.ToList().Take(200))
                commands.Add(new JsonObject
                {
                    ["name"] = command.Name,
                    ["description"] = command.Description,
                    ["hint"] = command.ArgumentHint,
                });

            return new JsonObject
            {
                ["models"] = models,
                ["efforts"] = efforts,
                ["commands"] = commands,
                ["model"] = chat.Model,
                ["effort"] = chat.Effort,
                ["mode"] = chat.Mode,
                ["fast"] = chat.FastMode,
                ["canFast"] = chat.ShowFastMode,
                ["provider"] = chat.Provider,
            };
        }).ConfigureAwait(false);

        return payload is null ? Fail(404, "unknown chat") : Json(payload);
    }

    private async Task<Response> SetModelAsync(string id, Request request)
    {
        var model = Body(request)?["model"]?.GetValue<string>();
        var ok = await WithChatAsync(id, chat =>
        {
            // Only ever apply an id this chat's own provider actually offers. A model string from another
            // provider's catalog would be forwarded straight to the CLI and fail the turn.
            //
            // The fallback matters: a session that has not finished initialising has an empty Models list, and
            // checking only that would wave through any string at all during the window the phone is most likely
            // to be poking at a freshly created chat. /options answers from the same static catalogue in that
            // state, so validating against it is exactly the set the phone was offered.
            if (!string.IsNullOrEmpty(model))
            {
                var catalogue = chat.Models.Count > 0
                    ? (IReadOnlyList<ModelChoice>)chat.Models.ToList()
                    : ProviderModelCatalog.For(ProviderModelCatalog.Normalize(chat.Provider));
                if (!catalogue.Any(m => string.Equals(m.Value, model, StringComparison.OrdinalIgnoreCase)))
                    return false;
            }
            chat.SetModel(model);
            return true;
        }).ConfigureAwait(false);
        return ok ? Json(new JsonObject { ["ok"] = true }) : Fail(400, "that model is not available for this chat");
    }

    private async Task<Response> SetEffortAsync(string id, Request request)
    {
        var body = Body(request);
        // A missing or null "effort" is the Auto tier, which is a real choice rather than a malformed request.
        var effort = body is not null && body.ContainsKey("effort") ? body["effort"]?.GetValue<string>() : null;
        var ok = await WithChatAsync(id, chat =>
        {
            if (!string.IsNullOrEmpty(effort)
                && !chat.EffortOptions.Any(e => string.Equals(e.Value, effort, StringComparison.OrdinalIgnoreCase)))
                return false;
            chat.SetEffort(string.IsNullOrEmpty(effort) ? null : effort);
            return true;
        }).ConfigureAwait(false);
        return ok ? Json(new JsonObject { ["ok"] = true }) : Fail(400, "that effort level is not available for this chat");
    }

    private async Task<Response> SetModeAsync(string id, Request request)
    {
        var mode = Body(request)?["mode"]?.GetValue<string>() ?? "";
        if (mode is not ("default" or "auto" or "plan" or "acceptEdits" or "bypassPermissions"))
            return Fail(400, "unknown mode");
        var ok = await WithChatAsync(id, chat => { chat.SetMode(mode, remember: true); return true; }).ConfigureAwait(false);
        return ok ? Json(new JsonObject { ["ok"] = true }) : Fail(404, "unknown chat");
    }

    private async Task<Response> SetFastAsync(string id, Request request)
    {
        var on = Body(request)?["on"]?.GetValue<bool>() ?? false;
        var ok = await WithChatAsync(id, chat =>
        {
            if (!chat.CanToggleFastMode) return false;
            chat.SetFastMode(on);
            return true;
        }).ConfigureAwait(false);
        return ok ? Json(new JsonObject { ["ok"] = true }) : Fail(400, "fast mode is not available for this chat");
    }

    // ---------------- queue, rewind, chat lifecycle ----------------

    private async Task<Response> QueueAsync(string id, Request request)
    {
        var body = Body(request);
        var ordinal = body?["ord"]?.GetValue<int>() ?? -1;
        var action = body?["action"]?.GetValue<string>() ?? "";
        if (ordinal < 0) return Fail(400, "missing ord");
        if (action is not ("send" or "cancel")) return Fail(400, "unknown action");

        var ok = await WithChatAsync(id, chat =>
        {
            // Resolved fresh under the dispatcher: the queue may have drained between the poll that produced this
            // ordinal and the tap that used it, and acting on a stale index would cancel the wrong prompt.
            var queued = chat.Items.OfType<QueuedItem>().ElementAtOrDefault(ordinal);
            if (queued is null) return false;
            return action == "send" ? chat.SendQueuedNow(queued) : chat.CancelQueued(queued);
        }).ConfigureAwait(false);

        return ok ? Json(new JsonObject { ["ok"] = true }) : Fail(404, "that queued prompt is no longer waiting");
    }

    /// <summary>Rewinds the workspace to just before the given prompt and puts its text back in the composer.</summary>
    private async Task<Response> UndoAsync(string id, Request request)
    {
        var ordinal = Body(request)?["ord"]?.GetValue<int>() ?? -1;
        if (ordinal < 0) return Fail(400, "missing ord");

        var vm = _vm;
        var ui = _ui;
        if (vm is null || ui is null) return Fail(503, "desktop not ready");

        var result = await ui.InvokeAsync(async () =>
        {
            var chat = vm.Chats.FirstOrDefault(c => c.BridgeId == id);
            var prompt = chat?.Items.OfType<UserItem>().ElementAtOrDefault(ordinal);
            if (chat is null || prompt is null) return (Found: false, Ok: false, Message: "unknown prompt");
            if (!prompt.CanRequestUndo) return (Found: true, Ok: false, Message: "that prompt can no longer be undone");
            var rollback = await chat.StopAndUndoPromptAsync(prompt).ConfigureAwait(true);
            return (Found: true, Ok: rollback.Success, Message: rollback.Message);
        }).Task.Unwrap().ConfigureAwait(false);

        if (!result.Found) return Fail(404, result.Message);
        return result.Ok
            ? Json(new JsonObject { ["ok"] = true, ["message"] = result.Message })
            : Fail(400, result.Message.Length > 0 ? result.Message : "undo failed");
    }

    private async Task<Response> PinAsync(string id)
    {
        var pinned = await OnUi(vm =>
        {
            var chat = vm.Chats.FirstOrDefault(c => c.BridgeId == id);
            if (chat is null) return (bool?)null;
            vm.TogglePin(chat);
            return chat.Pinned;
        }).ConfigureAwait(false);
        return pinned is null ? Fail(404, "unknown chat") : Json(new JsonObject { ["ok"] = true, ["pinned"] = pinned });
    }

    private async Task<Response> TitleAsync(string id, Request request)
    {
        var title = (Body(request)?["title"]?.GetValue<string>() ?? "").Trim();
        if (title.Length is 0 or > 200) return Fail(400, "title must be 1-200 characters");
        var ok = await WithChatAsync(id, chat => { chat.Title = title; return true; }).ConfigureAwait(false);
        return ok ? Json(new JsonObject { ["ok"] = true }) : Fail(404, "unknown chat");
    }

    /// <summary>Closes the pane. Deliberately not DeleteChat: a phone tap should never destroy a transcript on a
    /// machine the user cannot see, and a closed chat is still resumable from the desktop's history.</summary>
    private async Task<Response> CloseAsync(string id)
    {
        var ok = await OnUi(vm =>
        {
            var chat = vm.Chats.FirstOrDefault(c => c.BridgeId == id);
            if (chat is null) return false;
            vm.CloseChat(chat);
            return true;
        }).ConfigureAwait(false);
        return ok ? Json(new JsonObject { ["ok"] = true }) : Fail(404, "unknown chat");
    }

    // ---------------- starting a chat ----------------

    /// <summary>Folders the desktop already has on its home screen, plus every folder an open chat is using.</summary>
    private async Task<Response> FoldersAsync()
    {
        var payload = await OnUi(vm =>
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var folders = new JsonArray();

            void Add(string cwd)
            {
                if (string.IsNullOrWhiteSpace(cwd) || !seen.Add(cwd)) return;
                folders.Add(new JsonObject
                {
                    ["cwd"] = cwd,
                    ["name"] = RecentDirectoryHistory.DisplayName(cwd),
                });
            }

            foreach (var project in vm.RecentProjects.ToList()) Add(project.Cwd);
            foreach (var chat in vm.Chats.ToList()) Add(chat.Cwd);
            foreach (var project in vm.Projects.ToList()) Add(project.Cwd);

            return new JsonObject
            {
                ["folders"] = folders,
                ["preferred"] = vm.PreferredNewChatCwd,
                ["defaultProvider"] = AppSettings.Current.DefaultProvider,
            };
        }).ConfigureAwait(false);

        return payload is null ? Fail(503, "desktop not ready") : Json(payload);
    }

    private async Task<Response> NewChatAsync(Request request)
    {
        var body = Body(request);
        var cwd = (body?["cwd"]?.GetValue<string>() ?? "").Trim();
        var provider = (body?["provider"]?.GetValue<string>() ?? "").Trim();
        var title = (body?["title"]?.GetValue<string>() ?? "").Trim();

        if (cwd.Length == 0) return Fail(400, "missing cwd");
        // The folder has to already exist. This is not a sandbox - a paired phone can already make an agent run
        // anything - but it turns a typo into a clean 400 instead of a chat spawned against a path that is not there.
        if (!Directory.Exists(cwd)) return Fail(400, "that folder does not exist on the PC");
        if (provider.Length > 0 && provider is not ("claude" or "codex" or "kimi" or "grok"))
            return Fail(400, "unknown provider");

        var id = await OnUi(vm =>
        {
            var chat = vm.NewChat(
                cwd,
                title: title.Length > 0 ? title : null,
                provider: provider.Length > 0 ? provider : null,
                activatePrimary: false);
            return chat.BridgeId;
        }).ConfigureAwait(false);

        return string.IsNullOrEmpty(id) ? Fail(503, "desktop not ready") : Json(new JsonObject { ["id"] = id });
    }

    /// <summary>Runs an action against one chat on the dispatcher, returning false when the chat is gone.</summary>
    private Task<bool> WithChatAsync(string id, Func<ChatViewModel, bool> work) => OnUi(vm =>
    {
        var chat = vm.Chats.FirstOrDefault(c => c.BridgeId == id);
        return chat is not null && work(chat);
    });

    /// <summary>Runs a read/command against the view model on the WPF dispatcher. Socket threads never touch a
    /// ChatViewModel directly.</summary>
    private async Task<T> OnUi<T>(Func<MainViewModel, T> work)
    {
        var vm = _vm;
        var ui = _ui;
        if (vm is null || ui is null) return default!;
        return await ui.InvokeAsync(() => work(vm)).Task.ConfigureAwait(false);
    }

    // ---------------- auth ----------------

    private Response Pair(Request request, string remote)
    {
        var body = Body(request);
        var code = (body?["code"]?.GetValue<string>() ?? "").Where(char.IsDigit).Aggregate("", (a, c) => a + c);
        var name = (body?["name"]?.GetValue<string>() ?? "Phone").Trim();
        if (name.Length is 0 or > 64) name = "Phone";

        string? expected;
        lock (_gate)
        {
            if (_pairingCode is null || DateTime.UtcNow > _pairingExpires) return Fail(403, "pairing is not open");
            if (_pairingAttempts >= MaxPairingAttempts) return Fail(429, "too many attempts");
            expected = _pairingCode;
            _pairingAttempts++;
        }

        if (!FixedTimeEquals(code, expected))
        {
            var left = MaxPairingAttempts - _pairingAttempts;
            if (left <= 0) ClosePairing();
            return Fail(403, left > 0 ? $"wrong code ({left} left)" : "wrong code - pairing closed");
        }

        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var device = new PhoneDevice
        {
            Id = Guid.NewGuid().ToString("n"),
            Name = name,
            TokenHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token))),
            PairedAt = DateTimeOffset.Now,
            LastSeen = DateTimeOffset.Now,
            LastAddress = remote,
        };
        lock (_gate) _devices.Add(device);
        PhoneBridgeStore.SaveDevices(SnapshotDevices());
        SyncDeviceView();
        ClosePairing();   // one code, one phone

        return Json(new JsonObject
        {
            ["token"] = token,
            ["deviceId"] = device.Id,
            ["host"] = Environment.MachineName,
            ["fingerprint"] = Fingerprint,
        });
    }

    /// <summary>
    /// Trades a generated APK's one-time secret for a real device token.
    ///
    /// This is the whole point of the generated-APK flow: the secret inside the APK is not a credential you can
    /// keep using, it is a coupon. It works exactly once, it can only be redeemed by something that already
    /// trusts this machine's certificate (the fingerprint is baked into the same APK), and the moment it is
    /// redeemed it is stamped used and the APK becomes inert. A copy of the APK that leaks after the real phone
    /// has enrolled buys an attacker nothing at all.
    /// </summary>
    private Response Enroll(Request request, string remote)
    {
        var body = Body(request);
        var secret = body?["secret"]?.GetValue<string>() ?? "";
        var name = (body?["name"]?.GetValue<string>() ?? "Phone").Trim();
        if (name.Length is 0 or > 64) name = "Phone";
        if (secret.Length is 0 or > 512) return Fail(400, "missing secret");

        string expected;
        lock (_gate)
        {
            if (_enrolment is null) return Fail(403, "this PC is not expecting a new phone");
            if (!_enrolment.Pending) return Fail(403, "that app has already been used to set up a phone");
            if (_enrolAttempts >= MaxEnrolAttempts) return Fail(429, "too many attempts");
            _enrolAttempts++;
            expected = _enrolment.SecretHash;
        }

        var presented = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
        if (!FixedTimeEquals(expected, presented)) return Fail(403, "that app was not built by this PC");

        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var device = new PhoneDevice
        {
            Id = Guid.NewGuid().ToString("n"),
            Name = name,
            TokenHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token))),
            PairedAt = DateTimeOffset.Now,
            LastSeen = DateTimeOffset.Now,
            LastAddress = remote,
        };

        PhoneEnrolment? snapshot;
        lock (_gate)
        {
            // Re-checked under the lock: two phones racing the same APK must not both come away with a token.
            if (_enrolment is null || !_enrolment.Pending) return Fail(403, "that app has already been used to set up a phone");
            _enrolment.UsedAt = DateTimeOffset.Now;
            _enrolment.DeviceName = name;
            // The coupon is now spent AND named. Nothing reopens it: a second phone running the same APK file gets
            // the 403 above, and revoking this device later does not resurrect it.
            _enrolment.BoundDeviceId = device.Id;
            _enrolment.BoundAddress = remote;
            snapshot = _enrolment;
            _devices.Add(device);
        }

        PhoneBridgeStore.SaveEnrolment(snapshot);
        PhoneBridgeStore.SaveDevices(SnapshotDevices());
        SyncDeviceView();
        Raise(nameof(HasOutstandingEnrolment));
        Raise(nameof(Enrolment));

        return Json(new JsonObject
        {
            ["token"] = token,
            ["deviceId"] = device.Id,
            ["host"] = Environment.MachineName,
            ["fingerprint"] = Fingerprint,
        });
    }

    private PhoneDevice? Authenticate(Request request, string remote)
    {
        lock (_gate)
        {
            if (_lockouts.TryGetValue(remote, out var state) && DateTime.UtcNow < state.Until) return null;
        }

        if (!request.Headers.TryGetValue("Authorization", out var header) ||
            !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            NoteAuthFailure(remote);
            return null;
        }

        var presented = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(header[7..].Trim())));
        lock (_gate)
        {
            foreach (var device in _devices)
            {
                if (!FixedTimeEquals(device.TokenHash, presented)) continue;
                _lockouts.Remove(remote);
                return device;
            }
        }
        NoteAuthFailure(remote);
        return null;
    }

    /// <summary>Spends one unit of an address's anonymous request budget. False once it is empty.</summary>
    private bool AllowAnonymous(string remote)
    {
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            if (_anonymous.Count > MaxTrackedAddresses)
            {
                foreach (var stale in _anonymous.Where(a => now - a.Value.Window > TimeSpan.FromMinutes(1))
                             .Select(a => a.Key).ToList())
                    _anonymous.Remove(stale);
                if (_anonymous.Count > MaxTrackedAddresses) _anonymous.Clear();
            }

            var entry = _anonymous.TryGetValue(remote, out var found) ? found : (Count: 0, Window: now);
            if (now - entry.Window > TimeSpan.FromMinutes(1)) entry = (0, now);
            if (entry.Count >= MaxAnonymousRequestsPerMinute)
            {
                _anonymous[remote] = entry;
                return false;
            }
            _anonymous[remote] = (entry.Count + 1, entry.Window);
            return true;
        }
    }

    private void NoteAuthFailure(string remote)
    {
        lock (_gate)
        {
            // The key is attacker-controlled (one entry per source address), so the table is pruned rather than
            // left to grow for the life of the process.
            if (_lockouts.Count > MaxTrackedAddresses)
            {
                var now = DateTime.UtcNow;
                foreach (var stale in _lockouts.Where(l => l.Value.Until < now).Select(l => l.Key).ToList())
                    _lockouts.Remove(stale);
                if (_lockouts.Count > MaxTrackedAddresses) _lockouts.Clear();
            }

            _lockouts.TryGetValue(remote, out var state);
            var failures = state.Failures + 1;
            _lockouts[remote] = failures >= MaxAuthFailuresPerAddress
                ? (0, DateTime.UtcNow + AuthLockout)
                : (failures, DateTime.MinValue);
        }
    }

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // ---------------- helpers ----------------

    private static JsonObject? Body(Request request)
    {
        try { return JsonNode.Parse(request.Body) as JsonObject; }
        catch (JsonException) { return null; }
    }

    private static int IntArg(Request request, string key, int fallback = 0) =>
        request.Query.TryGetValue(key, out var raw) && int.TryParse(raw, out var n) ? n : fallback;

    private static TimeSpan WaitSeconds(Request request)
    {
        var seconds = IntArg(request, "wait");
        if (seconds <= 0) return TimeSpan.Zero;
        var wait = TimeSpan.FromSeconds(seconds);
        return wait > MaxLongPoll ? MaxLongPoll : wait;
    }

    private static Response Json(JsonObject payload) => new(200, payload.ToJsonString());
    private static Response Raw(string json) => new(200, json);
    private static Response Fail(int status, string message) =>
        new(status, new JsonObject { ["error"] = message }.ToJsonString());
}
