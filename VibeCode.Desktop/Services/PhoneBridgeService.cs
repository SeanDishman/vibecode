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
///   * Protected routes require both a 256-bit token and a signature by the single enrolled P-256 key.
///     Signed method/target/body/token, timestamp, nonce and process epoch prevent substitution and replay.
///   * Pairing is the only unauthenticated write, and it is closed by default: the user has to open a window on
///     the desktop, which mints a 6-digit code good for five minutes, one successful use, and five wrong guesses.
///   * Publicly addressed peers are rejected before TLS. LAN and private VPN address ranges are accepted;
///     routing and firewall policy must also allow them. VibeCode creates no relay, VPN or port mapping.
///
/// What a paired phone can do, stated plainly, because the endpoint list has grown well past "read my chats":
/// it can send prompts, answer permission prompts (including "always allow"), switch the permission mode as far
/// as bypassPermissions, change model and effort, start a new chat in any existing folder, rewind a turn, and
/// close a pane. That is deliberate - the point of the app is to be the desktop from the sofa - and it is not a
/// privilege escalation, because a device that can send one prompt to an agent with Bash already has arbitrary
/// code execution on this machine. The paired signing key and token are the trust boundary.
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
    private readonly PhoneRequestProof _requestProof = new();
    private readonly AsyncLocal<PhoneDevice?> _requestDevice = new();
    private readonly AsyncLocal<CancellationToken> _requestCancellation = new();
    private readonly Dictionary<string, (int Failures, DateTime Until)> _lockouts = new(StringComparer.Ordinal);
    /// <summary>Per-address budget for requests that carry no valid token, refilled every minute.</summary>
    private readonly Dictionary<string, (int Count, DateTime Window)> _anonymous = new(StringComparer.Ordinal);
    private int _openConnections;
    private long _blockedRemotes;
    private DateTime _lastBlockedNote;

    private string? _pairingCode;
    private DateTime _pairingExpires;
    private int _pairingAttempts;
    /// <summary>Generated APKs, one per phone. Any number may be outstanding at once; each secret is single-use.</summary>
    private readonly List<PhoneEnrolment> _enrolments = new();
    private int _enrolAttempts;

    private PhoneBridgeService()
    {
        _devices.AddRange(PhoneBridgeStore.LoadDevices());
        _enrolments.AddRange(PhoneBridgeStore.LoadEnrolments());
        SyncDeviceView();
    }

    // ---------------- observable surface (bound by PhoneBridgeWindow) ----------------

    private bool _running;
    public bool Running { get => _running; private set { if (Set(ref _running, value)) { Raise(nameof(StatusText)); RefreshAddresses(); } } }

    private string _error = "";
    public string Error { get => _error; private set { if (Set(ref _error, value)) { Raise(nameof(HasError)); Raise(nameof(StatusText)); } } }
    public bool HasError => _error.Length > 0;

    private int _port = DefaultPort;
    public int Port { get => _port; private set { if (Set(ref _port, value)) RefreshAddresses(); } }

    private string _fingerprint = "";
    /// <summary>Full SHA-256 of the server certificate, uppercase hex.</summary>
    public string Fingerprint { get => _fingerprint; private set { if (Set(ref _fingerprint, value)) Raise(nameof(SafetyCode)); } }

    /// <summary>The first eight bytes of <see cref="Fingerprint"/>, formatted so a human can compare it to what the
    /// phone shows before approving a pairing. This is the check that turns trust-on-first-use into something an
    /// attacker on the same Wi-Fi cannot quietly win.</summary>
    public string SafetyCode => _fingerprint.Length >= 16
        ? $"{_fingerprint[..4]}-{_fingerprint[4..8]}-{_fingerprint[8..12]}-{_fingerprint[12..16]}"
        : "";

    /// <summary>The preferred IPv4 endpoint. Reachability still depends on the phone's network.</summary>
    public string Address
    {
        get
        {
            var ip = PhoneBridgeStore.LocalAddresses().FirstOrDefault();
            return ip is null ? $"(no network):{Port}" : $"{ip}:{Port}";
        }
    }

    /// <summary>All current IPv4 candidates, including configured private VPN adapters.</summary>
    public string ConnectionAddresses => string.Join(Environment.NewLine,
        PhoneBridgeStore.LocalAddresses().Select(ip => $"{ip}:{Port}"));

    public void RefreshAddresses()
    {
        Raise(nameof(Address));
        Raise(nameof(ConnectionAddresses));
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
        : Devices.Any(d => string.IsNullOrEmpty(d.PublicKey))
            ? "On — a phone uses the old app: update it, revoke its entry here, then pair it again"
        : Devices.Count == 0 && PendingEnrolmentCount > 0
            ? $"On — waiting for the app{(PendingEnrolmentCount == 1 ? "" : "s")} you generated to be installed"
        : Devices.Count == 0 ? "On — no phones paired yet"
        : $"On — {Devices.Count} phone{(Devices.Count == 1 ? "" : "s")} paired" +
          (PendingEnrolmentCount > 0 ? $" · {PendingEnrolmentCount} generated app{(PendingEnrolmentCount == 1 ? "" : "s")} not set up yet" : "");

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
        Port = AppSettings.Current.PhoneBridgePort is > 0 and <= 65535
            ? AppSettings.Current.PhoneBridgePort : DefaultPort;
        // The extension gate comes first: a disabled extension must not open a socket on launch even if the bridge
        // was left on before it was switched off.
        if (AppSettings.Current.PhoneEnabled && AppSettings.Current.PhoneBridgeEnabled) Start();
    }

    public void Start() => Start(IPAddress.Any);

    // Keep runtime tests on loopback without changing the production listener's address policy.
    internal void Start(IPAddress listenAddress)
    {
        lock (_gate)
        {
            if (_listener is not null) return;
            TcpListener? listener = null;
            try
            {
                _certificate ??= PhoneBridgeStore.LoadOrCreateCertificate();
                Fingerprint = Convert.ToHexString(SHA256.HashData(_certificate.RawData));

                listener = new TcpListener(listenAddress, Port);
                listener.Start();
                _listener = listener;
                _cancel = new CancellationTokenSource();
                // Capture this start's token before scheduling. A quick stop/restart replaces _cancel.
                var cancel = _cancel.Token;
                Error = "";
                Running = true;
                _ = Task.Run(() => AcceptLoopAsync(listener, cancel));

                // Lets a phone find this PC again after its address changes, which the baked-in address list
                // cannot survive on its own. Best effort - the bridge is fully usable without it.
                _beacon ??= new PhoneBeacon(this);
                _beacon.Start(Port, listenAddress);
            }
            catch (Exception ex)
            {
                _cancel?.Cancel();
                try { listener?.Stop(); } catch { /* startup already failed */ }
                try { _beacon?.Stop(); } catch { /* discovery is best effort */ }
                _listener = null;
                _cancel = null;
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

    /// <summary>Rebinds on a different port without changing the certificate. Phones must update their endpoint;
    /// discovery also uses the saved port and cannot find a bridge that moved to a different one.</summary>
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

    /// <summary>Opens a five-minute window in which one phone may exchange the displayed code for a token. Phones
    /// already paired keep working; each new phone needs its own code.</summary>
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

    /// <summary>True while at least one generated APK has not been redeemed by a phone yet.</summary>
    public bool HasOutstandingEnrolment => PendingEnrolmentCount > 0;

    public int PendingEnrolmentCount { get { lock (_gate) return _enrolments.Count(e => e.Pending); } }

    /// <summary>Every generated app this PC still tracks, newest first: outstanding ones and recent history.</summary>
    public IReadOnlyList<PhoneEnrolment> Enrolments
    {
        get { lock (_gate) return _enrolments.OrderByDescending(e => e.IssuedAt).ToList(); }
    }

    /// <summary>
    /// Mints a fresh single-use enrolment secret and returns it in the clear - the only moment it is ever
    /// readable. The caller's job is to bake it into an APK and then forget it; only the SHA-256 is kept here.
    /// Other outstanding enrolments are untouched: each APK sets up one phone, and a PC can have several phones.
    /// </summary>
    public string IssueEnrolment(out string enrolmentId)
    {
        if (!Running) Start();
        if (!Running) throw new InvalidOperationException("The phone bridge must be running before generating an app.");
        var secret = Base64Url(RandomNumberGenerator.GetBytes(EnrolmentSecretBytes));
        var enrolment = new PhoneEnrolment
        {
            Id = Guid.NewGuid().ToString("n"),
            SecretHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret))),
            IssuedAt = DateTimeOffset.Now,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(24),
            Fingerprint = Fingerprint,
        };
        lock (_gate)
        {
            if (!PhoneBridgeStore.SaveEnrolments(_enrolments.Append(enrolment)))
                throw new IOException("Could not persist the enrollment. No app was authorized.");
            _enrolments.Add(enrolment);
            _enrolAttempts = 0;
        }
        RaiseEnrolments();
        enrolmentId = enrolment.Id;
        return secret;
    }

    /// <summary>Records where an APK ended up, so the window can point at it later.</summary>
    public void NoteEnrolmentApk(string enrolmentId, string path)
    {
        lock (_gate)
        {
            if (_enrolments.FirstOrDefault(e => e.Id == enrolmentId) is not { } enrolment) return;
            enrolment.ApkPath = path;
            // Serialize the write with revocation/claiming. A delayed snapshot must not restore a revoked APK.
            if (!PhoneBridgeStore.SaveEnrolments(_enrolments)) Error = "Could not save the generated app's location.";
        }
        RaiseEnrolments();
    }

    /// <summary>Kills one un-redeemed APK. This is the answer to "I emailed it to myself and now I regret it".</summary>
    public void RevokeEnrolment(string enrolmentId)
    {
        lock (_gate)
        {
            var remaining = _enrolments.Where(e => e.Id != enrolmentId).ToList();
            if (!PhoneBridgeStore.SaveEnrolments(remaining))
            {
                Stop();
                Error = "Enrollment could not be revoked on disk. The bridge has been stopped; retry revocation.";
                return;
            }
            _enrolments.RemoveAll(e => e.Id == enrolmentId);
        }
        RaiseEnrolments();
    }

    private void RaiseEnrolments()
    {
        Raise(nameof(HasOutstandingEnrolment));
        Raise(nameof(PendingEnrolmentCount));
        Raise(nameof(Enrolments));
        Raise(nameof(StatusText));
    }

    public bool Revoke(PhoneDevice device)
    {
        lock (_gate)
        {
            var remaining = _devices.Where(d => d.Id != device.Id).ToList();
            if (!PhoneBridgeStore.SaveDevices(remaining))
            {
                Stop();
                Error = "The phone could not be revoked on disk. The bridge has been stopped; retry revocation.";
                return false;
            }
            _devices.RemoveAll(d => d.Id == device.Id);
        }
        SyncDeviceView();
        return true;
    }

    /// <summary>Throws away the TLS identity and every paired phone. The nuclear option for "someone had my
    /// laptop" - after this, no previously paired handset can connect even with its token.</summary>
    public void ResetEverything()
    {
        var wasRunning = Running;
        Stop(remember: false);
        lock (_gate)
        {
            if (!PhoneBridgeStore.SaveDevices(Array.Empty<PhoneDevice>()) || !PhoneBridgeStore.SaveEnrolments(Array.Empty<PhoneEnrolment>()))
            {
                Error = "Could not persist the reset. The bridge remains stopped; retry before starting it.";
                return;
            }
            _devices.Clear();
            _certificate?.Dispose();
            _certificate = null;
            // The new identity will have a different fingerprint, so any APK generated against the old one can
            // never connect again. Clearing them here is what stops the window offering files that cannot work.
            _enrolments.Clear();
        }
        PhoneBridgeStore.ResetIdentity();
        SyncDeviceView();
        Fingerprint = "";
        RaiseEnrolments();
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

                using var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                readDeadline.CancelAfter(TimeSpan.FromSeconds(15));
                var request = await ReadRequestAsync(ssl, readDeadline.Token).ConfigureAwait(false);
                if (request is null) return;
                var response = await RouteAsync(request, remote, cancel).ConfigureAwait(false);
                using var writeDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                writeDeadline.CancelAfter(TimeSpan.FromSeconds(15));
                await WriteAsync(ssl, response, writeDeadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* shutting down, or a phone that walked away */ }
        catch (IOException) { /* connection dropped mid-request - normal on a handset */ }
        catch (AuthenticationException) { /* a client that would not accept our certificate */ }
        catch (Exception ex)
        {
            CrashLog.Note("PhoneBridge", $"request from {remote} failed - {ex.GetType().Name}");
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
        Dictionary<string, string> Headers, string Body, string Target);

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
        if (headerEnd > MaxHeaderBytes) return null;
        var headText = Encoding.ASCII.GetString(raw, 0, headerEnd);
        var lines = headText.Split("\r\n");
        var parts = lines[0].Split(' ');
        if (parts.Length != 3 || parts[2] != "HTTP/1.1" || !parts[1].StartsWith('/') ||
            parts[1].Any(c => char.IsControl(c) || c == '#')) return null;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var colon = lines[i].IndexOf(':');
            if (colon <= 0 || !headers.TryAdd(lines[i][..colon].Trim(), lines[i][(colon + 1)..].Trim())) return null;
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
        var chunked = headers.TryGetValue("Transfer-Encoding", out var te);
        if (chunked && (!string.Equals(te, "chunked", StringComparison.OrdinalIgnoreCase) || headers.ContainsKey("Content-Length"))) return null;

        byte[] body;
        if (chunked)
        {
            var decoded = await ReadChunkedAsync(stream, leftover, cancel).ConfigureAwait(false);
            if (decoded is null) return null;
            body = decoded;
        }
        else
        {
            var length = 0;
            if (headers.TryGetValue("Content-Length", out var cl) && !int.TryParse(cl, out length)) return null;
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
            new UTF8Encoding(false, true).GetString(body), target);
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
                if (pending.Count > MaxHeaderBytes || !await FillAsync().ConfigureAwait(false)) return null;
            if (eol > MaxHeaderBytes) return null;

            var sizeLine = Encoding.ASCII.GetString(pending.GetRange(0, eol).ToArray());
            pending.RemoveRange(0, eol + 2);
            var semi = sizeLine.IndexOf(';');          // chunk extensions: present, and ignorable
            if (semi >= 0) sizeLine = sizeLine[..semi];
            if (!int.TryParse(sizeLine.Trim(), System.Globalization.NumberStyles.HexNumber, null, out var size)
                || size < 0) return null;

            if (size == 0)
            {
                var trailerBytes = 0;
                // Trailer section, ending at the blank line. Nothing here is used, but it has to be consumed.
                while (true)
                {
                    while ((eol = FindCrLf(pending)) < 0)
                        if (pending.Count > MaxHeaderBytes || !await FillAsync().ConfigureAwait(false)) return null;
                    var trailer = eol;
                    trailerBytes += eol + 2;
                    if (trailerBytes > MaxHeaderBytes) return null;
                    pending.RemoveRange(0, eol + 2);
                    if (trailer == 0) return body.ToArray();
                }
            }

            if (body.Length + size > MaxBodyBytes) return null;
            while (pending.Count < size + 2)
                if (!await FillAsync().ConfigureAwait(false)) return null;
            if (pending[size] != 13 || pending[size + 1] != 10) return null;
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
            var payload = new JsonObject { ["app"] = "vibecode", ["authEpoch"] = _requestProof.Epoch };
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

        var device = Authenticate(request, remote, out var failureStatus, out var failureReason);
        if (device is null) return Fail(failureStatus, failureReason);

        _requestDevice.Value = device;
        _requestCancellation.Value = cancel;
        try
        {
            var response = await RouteAuthenticatedAsync(request, remote, device, cancel).ConfigureAwait(false);
            // Long polls must not deliver a transcript after revocation while they were waiting.
            lock (_gate)
                return !cancel.IsCancellationRequested && (request.Path == "/api/unpair" || _devices.Contains(device))
                    ? response : Fail(401, "phone revoked");
        }
        finally { _requestDevice.Value = null; _requestCancellation.Value = default; }
    }

    private async Task<Response> RouteAuthenticatedAsync(Request request, string remote, PhoneDevice device,
        CancellationToken cancel)
    {

        device.LastSeen = DateTimeOffset.Now;
        device.LastAddress = remote;

        if (request.Path == "/api/unpair" && request.Method == "POST")
            return Revoke(device) ? Json(new JsonObject { ["ok"] = true }) : Fail(503, "could not persist revocation");

        var mirror = _mirror;
        if (mirror is null || _vm is null) return Fail(503, "desktop not ready");
        mirror.Touch();

        if (request.Path == "/api/chats" && request.Method == "GET")
            return await ChatListAsync(mirror, request, cancel).ConfigureAwait(false);

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
            cancel.ThrowIfCancellationRequested();
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
            cancel.ThrowIfCancellationRequested();
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

        var device = _requestDevice.Value;
        var requestCancellation = _requestCancellation.Value;
        var result = await ui.InvokeAsync(async () =>
        {
            lock (_gate)
                if (device is null || !_devices.Contains(device) || requestCancellation.IsCancellationRequested)
                    return (Found: false, Ok: false, Message: "phone revoked");
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
        var ok = await WithChatAsync(id, chat => { chat.RenameChat(title); return true; }).ConfigureAwait(false);
        return ok ? Json(new JsonObject { ["ok"] = true }) : Fail(404, "unknown chat");
    }

    /// <summary>Closes the pane. Deliberately not DeleteChat: a phone tap should never destroy a transcript on a
    /// machine the user cannot see, and a closed chat is still resumable from the desktop's history.</summary>
    private async Task<Response> CloseAsync(string id)
    {
        var result = await OnUi(vm =>
        {
            var chat = vm.Chats.FirstOrDefault(c => c.BridgeId == id);
            if (chat is null) return 404;
            if (chat.IsLocked) return 409;
            vm.CloseChat(chat);
            return 200;
        }).ConfigureAwait(false);
        return result == 200 ? Json(new JsonObject { ["ok"] = true })
            : Fail(result, result == 409 ? "This chat is locked. Unlock it on the desktop before closing it." : "unknown chat");
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
        var device = _requestDevice.Value;
        var requestCancellation = _requestCancellation.Value;
        return await ui.InvokeAsync(() =>
        {
            // Revalidate at the point of action: a request queued on the UI thread can outlive its device.
            lock (_gate)
                return device is not null && _devices.Contains(device) && !requestCancellation.IsCancellationRequested ? work(vm) : default!;
        }).Task.ConfigureAwait(false);
    }

    // ---------------- auth ----------------

    private Response Pair(Request request, string remote)
    {
        var body = Body(request);
        var code = (StringValue(body, "code") ?? "").Replace(" ", "");
        var name = DeviceName(body);
        var publicKey = PhoneRequestProof.NormalizePublicKey(StringValue(body, "devicePublicKey"));
        if (publicKey is null) return Fail(400, "a P-256 device identity is required; update the phone app");

        string token;
        PhoneDevice device;
        lock (_gate)
        {
            if (_pairingCode is null || DateTime.UtcNow > _pairingExpires) return Fail(403, "pairing is not open");
            if (_pairingAttempts >= MaxPairingAttempts) return Fail(429, "too many attempts");
            _pairingAttempts++;
            if (!FixedTimeEquals(code, _pairingCode))
            {
                var left = MaxPairingAttempts - _pairingAttempts;
                if (left <= 0) ClosePairing();
                return Fail(403, left > 0 ? $"wrong code ({left} left)" : "wrong code - pairing closed");
            }

            // Consume the code and persist the device in the same critical section as its validation.
            // Two simultaneous valid requests must never each receive a credential.
            ClosePairing();
            token = Base64Url(RandomNumberGenerator.GetBytes(32));
            device = NewDevice(name, publicKey, token, remote);
            // Outstanding APKs belong to other phones, so a manual pairing leaves them alone.
            if (!PhoneBridgeStore.SaveDevices(_devices.Append(device))) return Fail(503, "could not persist device claim");
            _devices.Add(device);
        }
        SyncDeviceView();
        return PairedResponse(device, token);
    }

    private Response Enroll(Request request, string remote)
    {
        var body = Body(request);
        var secret = StringValue(body, "secret") ?? "";
        var name = DeviceName(body);
        var publicKey = PhoneRequestProof.NormalizePublicKey(StringValue(body, "devicePublicKey"));
        if (publicKey is null) return Fail(400, "a P-256 device identity is required; update the phone app");
        if (secret.Length is 0 or > 512) return Fail(400, "missing secret");
        var presented = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

        string token;
        PhoneDevice device;
        lock (_gate)
        {
            if (_enrolments.Count == 0) return Fail(403, "this PC is not expecting a new phone");
            // Nothing left to claim is not an attempt: say so before the attempt budget is touched, as before.
            if (!_enrolments.Any(e => e.Pending))
                return Fail(403, "that app enrollment was used or expired; generate a new app on the PC");
            if (_enrolAttempts >= MaxEnrolAttempts) return Fail(429, "too many attempts");
            _enrolAttempts++;
            // Every generated app has its own secret. Compare against all of them (each in constant time) and only
            // then decide, so the answer does not depend on how far down the list the match sat.
            PhoneEnrolment? enrolment = null;
            foreach (var candidate in _enrolments)
                if (FixedTimeEquals(candidate.SecretHash, presented)) enrolment = candidate;
            if (enrolment is null) return Fail(403, "that app was not built by this PC");
            if (!enrolment.Pending) return Fail(403, "that app enrollment was used or expired; generate a new app on the PC");
            token = Base64Url(RandomNumberGenerator.GetBytes(32));
            device = NewDevice(name, publicKey, token, remote);
            SpendEnrolment(enrolment, device, remote);
            // Persist the spent coupon BEFORE issuing/persisting the device. A failure can strand an enrollment
            // (recoverable on the desktop), but must never resurrect it or issue an unrecorded credential.
            if (!PhoneBridgeStore.SaveEnrolments(_enrolments)) return Fail(503, "could not persist enrollment claim");
            if (!PhoneBridgeStore.SaveDevices(_devices.Append(device))) return Fail(503, "could not persist device claim");
            _devices.Add(device);
        }
        SyncDeviceView();
        RaiseEnrolments();
        return PairedResponse(device, token);
    }

    private static void SpendEnrolment(PhoneEnrolment enrolment, PhoneDevice device, string remote)
    {
        enrolment.UsedAt = DateTimeOffset.UtcNow;
        enrolment.DeviceName = device.Name;
        enrolment.BoundDeviceId = device.Id;
        enrolment.BoundAddress = remote;
    }

    private static PhoneDevice NewDevice(string name, string publicKey, string token, string remote) => new()
    {
        Id = Guid.NewGuid().ToString("n"),
        Name = name,
        TokenHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token))),
        PublicKey = publicKey,
        PairedAt = DateTimeOffset.UtcNow,
        LastSeen = DateTimeOffset.UtcNow,
        LastAddress = remote,
    };

    private Response PairedResponse(PhoneDevice device, string token) => Json(new JsonObject
    {
        ["token"] = token,
        ["deviceId"] = device.Id,
        ["host"] = Environment.MachineName,
        ["fingerprint"] = Fingerprint,
        ["authEpoch"] = _requestProof.Epoch,
    });

    private static string DeviceName(JsonObject? body)
    {
        var name = (StringValue(body, "name") ?? "Phone").Trim();
        return name.Length is 0 or > 64 ? "Phone" : name;
    }

    private static string? StringValue(JsonObject? body, string name) =>
        body?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private PhoneDevice? Authenticate(Request request, string remote, out int failureStatus, out string failureReason)
    {
        failureStatus = 401;
        failureReason = "This phone is not paired. Revoke its old entry on the PC, then pair the updated app again.";
        lock (_gate)
        {
            if (_lockouts.TryGetValue(remote, out var state) && DateTime.UtcNow < state.Until)
            {
                failureStatus = 429;
                failureReason = "Too many authentication attempts. Wait one minute and retry.";
                return null;
            }
            if (request.Headers.TryGetValue("Authorization", out var header) &&
                header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                var token = header[7..].Trim();
                if (token.Length is > 0 and <= 128)
                {
                    // Several phones may be paired; the token says which one is asking. Every stored hash is compared
                    // (constant time each) before choosing, and the request must then carry THAT phone's signature.
                    var presented = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
                    PhoneDevice? device = null;
                    foreach (var candidate in _devices)
                        if (FixedTimeEquals(candidate.TokenHash, presented)) device = candidate;
                    if (device is not null && !string.IsNullOrEmpty(device.PublicKey))
                    {
                        if (_requestProof.Verify(device.PublicKey, token, request.Method, request.Target, request.Body,
                                request.Headers, DateTimeOffset.UtcNow))
                        {
                            _lockouts.Remove(remote);
                            return device;
                        }
                        // An epoch mismatch is safely retried after a pinned ping. Other proof failures (including
                        // clock skew) must not make the app erase an otherwise valid pairing.
                        if (request.Headers.TryGetValue("X-VibeCode-Epoch", out var epoch) && epoch != _requestProof.Epoch)
                            failureReason = "The PC restarted. Refresh its authentication challenge and retry.";
                        else
                        {
                            failureStatus = 403;
                            failureReason = "Device signature rejected. Check automatic date and time on both devices. If this persists, revoke this phone on the PC and pair again.";
                        }
                    }
                }
            }
            NoteAuthFailure(remote);
            return null;
        }
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
