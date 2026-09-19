using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace VibeCode.Services;

/// <summary>
/// Answers "is there a VibeCode PC on this network?" so a phone never depends on an IP address staying still.
///
/// The generated APK bakes in whatever addresses this machine had the day it was built. DHCP reshuffles them, the
/// user moves from Ethernet to Wi-Fi, a router reboots — and the app is left retrying three dead addresses with no
/// way to learn the new one. That is the single most likely way this feature rots, and it cannot be fixed from the
/// phone side alone, so the desktop shouts its presence on request.
///
/// This is safe to answer unauthenticated because the reply contains nothing secret: a machine name, a port, and
/// the certificate fingerprint that is already public in every TLS handshake this service performs. The phone
/// treats a reply as a *hint about where to look* and nothing more — it still refuses to talk to anything whose
/// certificate does not match the fingerprint baked into its own signed package, so a hostile responder on the
/// same Wi-Fi can at worst point the app at an address where the handshake then fails.
/// </summary>
public sealed class PhoneBeacon : IDisposable
{
    /// <summary>Sent by the phone. Probes must be padded to at least this length so a reply is never larger than
    /// the request that caused it — a beacon that answers a 20-byte packet with a 200-byte one is a reflection
    /// amplifier, and there is no reason to build one.</summary>
    private const string Magic = "VIBECODE-DISCOVER-1";
    private const int MinProbeBytes = 256;
    /// <summary>Replies one source may draw per second. A phone sends three; a flood gets ignored.</summary>
    private const int MaxRepliesPerSecondPerSource = 5;

    private readonly PhoneBridgeService _bridge;
    private UdpClient? _socket;
    private CancellationTokenSource? _cancel;
    private readonly Dictionary<string, (int Count, DateTime Window)> _budget = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public PhoneBeacon(PhoneBridgeService bridge) => _bridge = bridge;

    /// <summary>Starts listening on the same port number the TCP bridge uses. Failure is non-fatal: discovery is a
    /// convenience, and the baked address list still works without it.</summary>
    public void Start(int port)
    {
        Stop();
        try
        {
            var socket = new UdpClient(AddressFamily.InterNetwork);
            socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Client.Bind(new IPEndPoint(IPAddress.Any, port));
            socket.EnableBroadcast = true;
            _socket = socket;
            _cancel = new CancellationTokenSource();
            _ = Task.Run(() => ListenAsync(socket, _cancel.Token));
        }
        catch (Exception ex)
        {
            CrashLog.Note("PhoneBridge", $"discovery beacon could not start on {port} - {ex.Message}");
            _socket = null;
        }
    }

    public void Stop()
    {
        try { _cancel?.Cancel(); } catch { /* already down */ }
        try { _socket?.Dispose(); } catch { /* already down */ }
        _cancel = null;
        _socket = null;
    }

    public void Dispose() => Stop();

    private async Task ListenAsync(UdpClient socket, CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try { received = await socket.ReceiveAsync(cancel).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }   // ICMP port-unreachable from a previous reply
            catch (Exception ex)
            {
                CrashLog.Note("PhoneBridge", $"discovery receive failed - {ex.Message}");
                return;
            }

            try { Answer(socket, received); }
            catch (Exception ex) { CrashLog.Note("PhoneBridge", $"discovery reply failed - {ex.Message}"); }
        }
    }

    private void Answer(UdpClient socket, UdpReceiveResult received)
    {
        if (received.Buffer.Length < MinProbeBytes) return;
        if (!PhoneBridgeService.IsLocalNetwork(received.RemoteEndPoint.Address)) return;

        var text = Encoding.ASCII.GetString(received.Buffer, 0, Math.Min(received.Buffer.Length, 64));
        if (!text.StartsWith(Magic, StringComparison.Ordinal)) return;
        if (!Allowed(received.RemoteEndPoint.Address.ToString())) return;

        var reply = new JsonObject
        {
            ["app"] = "vibecode",
            ["name"] = Environment.MachineName,
            ["port"] = _bridge.Port,
            ["fingerprint"] = _bridge.Fingerprint,
        }.ToJsonString();

        var bytes = Encoding.UTF8.GetBytes(reply);
        socket.Send(bytes, bytes.Length, received.RemoteEndPoint);
    }

    private bool Allowed(string source)
    {
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            if (_budget.Count > 256) _budget.Clear();
            if (!_budget.TryGetValue(source, out var entry) || now - entry.Window > TimeSpan.FromSeconds(1))
                entry = (0, now);
            if (entry.Count >= MaxRepliesPerSecondPerSource) return false;
            _budget[source] = (entry.Count + 1, entry.Window);
            return true;
        }
    }
}
