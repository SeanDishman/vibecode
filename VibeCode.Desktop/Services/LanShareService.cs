using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using VibeCode.UI;

namespace VibeCode.Services;

/// <summary>
/// Optional, session-only LAN downloads for the generated Android apps. Each QR link exposes one pending APK;
/// directory listings, unrelated files and the encrypted phone API are never served here.
/// </summary>
public sealed class LanShareService : Observable
{
    public static LanShareService Instance { get; } = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _links = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _connections = new(8);
    private TcpListener? _listener;
    private CancellationTokenSource? _cancel;
    private volatile bool _running;
    private string _error = "";
    private int _port;

    public bool Running => _running;
    public bool Enabled { get => Running; set { if (value) Start(); else Stop(); } }
    public string Error => _error;
    public int Port => _port;
    public string[] Addresses => PhoneBridgeStore.LocalAddresses().Where(PhoneBridgeService.IsLocalNetwork)
        .Select(address => address.ToString()).ToArray();

    public string DownloadUrl(string enrolmentId, string address)
    {
        var app = PhoneBridgeService.Instance.Enrolments.FirstOrDefault(item => item.Id == enrolmentId);
        if (app is not { Pending: true } || !File.Exists(app.ApkPath)) return "";
        lock (_gate)
        {
            if (!Running || !Addresses.Contains(address)) return "";
            if (!_links.TryGetValue(enrolmentId, out var token))
                _links[enrolmentId] = token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                    .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return $"http://{address}:{_port}/s/{token}/";
        }
    }

    public void Start() => Start(IPAddress.Any);

    internal void Start(IPAddress listenAddress)
    {
        try
        {
            lock (_gate)
            {
                _error = "";
                if (Running) return;
                if (!PhoneBridgeService.Instance.Enabled || !PhoneBridgeService.Instance.Running)
                {
                    _error = "Turn on Phone access before starting LAN Share.";
                    return;
                }
                if (listenAddress.Equals(IPAddress.Any) && Addresses.Length == 0)
                {
                    _error = "Connect this PC to Wi-Fi or Ethernet, then try sharing again.";
                    return;
                }
                TcpListener? listener = null;
                try
                {
                    // The bridge port speaks pinned TLS + AES. Browser downloads use their own HTTP listener.
                    listener = new TcpListener(listenAddress, 8766);
                    try { listener.Start(); }
                    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
                    {
                        listener.Stop();
                        listener = new TcpListener(listenAddress, 0);
                        listener.Start();
                    }
                    _port = ((IPEndPoint)listener.LocalEndpoint).Port;
                    _listener = listener;
                    _cancel = new CancellationTokenSource();
                    var cancel = _cancel.Token;
                    _running = true;
                    _ = AcceptAsync(listener, cancel);
                }
                catch (Exception ex)
                {
                    listener?.Stop();
                    _listener = null;
                    _cancel?.Cancel();
                    _cancel = null;
                    _running = false;
                    _error = "LAN Share could not start: " + ex.Message;
                }
            }
        }
        finally { NotifyState(); }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _cancel?.Cancel();
            _cancel = null;
            _listener?.Stop();
            _listener = null;
            _links.Clear();
            _running = false;
            _error = "";
        }
        NotifyState();
    }

    // Notify after releasing the socket gate: UI observers also read the bridge's enrollment list.
    private void NotifyState()
    {
        Raise(nameof(Running));
        Raise(nameof(Enabled));
        Raise(nameof(Error));
        RefreshAddresses();
    }

    public void RefreshAddresses() => Raise(nameof(Addresses));

    private async Task AcceptAsync(TcpListener listener, CancellationToken cancel)
    {
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cancel).ConfigureAwait(false);
                var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
                if (!PhoneBridgeService.IsLocalNetwork(remote) || !_connections.Wait(0))
                {
                    client.Dispose();
                    continue;
                }
                _ = ServeAsync(client, cancel);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken cancel)
    {
        using (client)
        try
        {
            var stream = client.GetStream();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var header = new MemoryStream();
            var block = new byte[1024];
            var end = -1;
            while (header.Length < 8192 && end < 0)
            {
                var count = await stream.ReadAsync(block, timeout.Token).ConfigureAwait(false);
                if (count == 0) return;
                header.Write(block, 0, count);
                end = header.GetBuffer().AsSpan(0, (int)header.Length).IndexOf("\r\n\r\n"u8);
            }
            if (end < 0) return;
            var first = Encoding.ASCII.GetString(header.GetBuffer(), 0, end).Split("\r\n")[0].Split(' ');
            if (first.Length != 3 || first[0] is not ("GET" or "HEAD"))
            {
                await TextAsync(stream, 405, "Method not allowed.", cancel).ConfigureAwait(false);
                return;
            }
            var path = first[1];
            PhoneEnrolment? app = null;
            string? enrolmentId = null;
            string? pagePath = null;
            lock (_gate)
            {
                // Match literal, random routes only. Never resolve a URL to a filesystem path.
                foreach (var link in _links)
                {
                    var candidate = $"/s/{link.Value}/";
                    if (path != candidate && path != candidate + "app.apk") continue;
                    enrolmentId = link.Key;
                    pagePath = candidate;
                    break;
                }
            }
            if (enrolmentId is not null)
                app = PhoneBridgeService.Instance.Enrolments.FirstOrDefault(item => item.Id == enrolmentId && item.Pending);
            if (app is null || !File.Exists(app.ApkPath))
            {
                await TextAsync(stream, 404, "This download is no longer available. Generate an app on the PC and scan its current QR code.", cancel, first[0] == "HEAD").ConfigureAwait(false);
                return;
            }
            if (path == pagePath)
            {
                var bytes = Encoding.UTF8.GetBytes(DownloadPage(pagePath, Path.GetFileName(app.ApkPath)));
                await HeaderAsync(stream, 200, "text/html; charset=utf-8", bytes.Length, "", cancel).ConfigureAwait(false);
                if (first[0] == "GET") await stream.WriteAsync(bytes, cancel).ConfigureAwait(false);
                return;
            }
            using var file = new FileStream(app.ApkPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await HeaderAsync(stream, 200, "application/vnd.android.package-archive", file.Length,
                "Content-Disposition: attachment; filename=\"VibeCode.apk\"\r\n", cancel).ConfigureAwait(false);
            if (first[0] == "GET") await file.CopyToAsync(stream, cancel).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        finally { _connections.Release(); }
    }

    private static async Task TextAsync(Stream stream, int status, string text, CancellationToken cancel, bool head = false)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await HeaderAsync(stream, status, "text/plain; charset=utf-8", bytes.Length, "", cancel).ConfigureAwait(false);
        if (!head) await stream.WriteAsync(bytes, cancel).ConfigureAwait(false);
    }

    private static Task HeaderAsync(Stream stream, int status, string type, long length, string extra, CancellationToken cancel)
    {
        var reason = status == 200 ? "OK" : status == 404 ? "Not Found" : "Method Not Allowed";
        var bytes = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {reason}\r\nContent-Type: {type}\r\nContent-Length: {length}\r\n{extra}Cache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nX-Content-Type-Options: nosniff\r\nContent-Security-Policy: default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; frame-ancestors 'none'\r\nConnection: close\r\n\r\n");
        return stream.WriteAsync(bytes, cancel).AsTask();
    }

    private static string DownloadPage(string path, string filename) => $$"""
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        <title>VibeCode for Android</title><style>
        :root{color-scheme:dark}*{box-sizing:border-box}::selection{background:#4c8df5;color:#fff}body{margin:0;background:#1a1a1d;color:#ecedf1;font:16px/1.6 "Segoe UI",system-ui,sans-serif}
        main{max-width:34rem;margin:auto;padding:56px 24px}h1{font-size:28px;line-height:1.2;margin:0 0 20px}p{margin:0 0 20px;color:#b4b6bf}
        a{display:inline-block;background:#4c8df5;color:#101116;padding:14px 20px;border-radius:8px;font-weight:600;text-decoration:none}
        a:hover{background:#6ba0f7}a:focus-visible{outline:3px solid #ecedf1;outline-offset:4px}small{display:block;overflow-wrap:anywhere;margin-top:24px;color:#b4b6bf}
        </style></head><body><main><h1>VibeCode for Android</h1>
        <p>Download the app generated by your PC, then open the APK on your phone.</p>
        <a href="{{path}}app.apk" download="VibeCode.apk">Download Android app</a>
        <p style="margin-top:28px">Android may ask you to allow installation from your browser. After installing, open VibeCode to connect to your PC.</p>
        <p>This file sets up one phone. Keep this page private until your phone has connected.</p>
        <small>{{WebUtility.HtmlEncode(filename)}}</small></main></body></html>
        """;
}
