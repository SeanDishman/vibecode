using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Authentication;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VibeCode;
using VibeCode.Services;

internal static class Program
{
    private const BindingFlags HiddenStatic = BindingFlags.NonPublic | BindingFlags.Static;
    private const BindingFlags HiddenInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    private static int _checks;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--capture-child"))
        {
            Console.Error.Write(new string('e', 131_072));
            Console.Out.Write(new string('o', 131_072));
            return 0;
        }
        if (args.Contains("--sleep-child")) { Thread.Sleep(5_000); return 0; }

        var output = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine(Path.GetTempPath(), "vibecode-phone-connectivity"));
        Directory.CreateDirectory(output);
        var fixture = Path.Combine(output, "isolated-data-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("VIBECODE_DATA_DIR", fixture);
        try
        {
            NetworkPolicy();
            FirewallRules();
            CaptureTimeout();
            BeaconLifecycle().GetAwaiter().GetResult();
            TlsLifecycle().GetAwaiter().GetResult();
            RenderSetup(output);
            Console.WriteLine($"PASS: {_checks} connectivity checks. Loopback fixtures only; no pairing or firewall changes.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        _checks++;
        Console.WriteLine("PASS: " + name);
    }

    private static void NetworkPolicy()
    {
        foreach (var ip in new[] { "127.0.0.1", "10.0.0.1", "172.16.0.1", "172.31.255.254", "192.168.0.1", "100.64.0.1", "100.127.255.254", "::ffff:100.64.0.1", "fd00::1" })
            Check(PhoneBridgeService.IsLocalNetwork(IPAddress.Parse(ip)), "accepts private/loopback policy " + ip);
        foreach (var ip in new[] { "8.8.8.8", "172.15.255.254", "172.32.0.1", "100.63.255.254", "100.128.0.1", "2001:4860:4860::8888" })
            Check(!PhoneBridgeService.IsLocalNetwork(IPAddress.Parse(ip)), "rejects public source policy " + ip);
        Check(!PhoneBridgeService.IsLocalNetwork(null), "rejects unknown peer address");
    }

    private static void FirewallRules()
    {
        var type = typeof(PhoneReachability).GetNestedType("FwRule", BindingFlags.NonPublic)!;
        var evaluate = typeof(PhoneReachability).GetMethod("EvaluateFirewallRules", HiddenStatic)!;
        const string exe = @"C:\Fixture\VibeCode.exe";
        object Rule(string name, int action = 1, int protocol = 6, string ports = "8765", string app = exe, int profiles = 2, bool enabled = true, int direction = 1) =>
            Activator.CreateInstance(type, name, enabled, direction, action, protocol, ports, app, profiles)!;
        PhoneReachability.FirewallVerdict Judge(params object[] rules)
        {
            var typed = Array.CreateInstance(type, rules.Length);
            for (var i = 0; i < rules.Length; i++) typed.SetValue(rules[i], i);
            return (PhoneReachability.FirewallVerdict)evaluate.Invoke(null, new object[] { typed, exe, 8765, 2 })!;
        }

        Check(!Judge(Rule("other-app", app: @"C:\Other.exe")).Allowed, "other executable cannot satisfy allow rule");
        Check(!Judge(Rule("public-only", profiles: 4)).Allowed, "inactive profile does not satisfy allow rule");
        Check(!Judge(Rule("disabled", enabled: false)).Allowed, "disabled rule does not satisfy allow rule");
        Check(!Judge(Rule("outbound", direction: 2)).Allowed, "outbound rule does not satisfy allow rule");
        Check(Judge(Rule("range", ports: "8000-9000", app: "")).Allowed, "unscoped TCP port range matches");
        Check(Judge(Rule("all", ports: "*", app: "", protocol: 256)).Allowed, "explicit unscoped allow-all matches");
        Check(Judge(Rule("udp", action: 0, protocol: 17)).BlockRules.Count == 0, "UDP-only block does not block TLS TCP");
        Check(Judge(Rule("other-port", action: 0, ports: "8766")).BlockRules.Count == 0, "other port block does not block TLS TCP");
        Check(Judge(Rule("port-block", action: 0, app: "")).BlockRules.SequenceEqual(new[] { "port-block" }), "unscoped matching block is reported");
        Check(Judge(Rule("app-block", action: 0, protocol: 256, ports: "*")).BlockRules.Count == 1, "all-protocol application block is reported");
        Check(!Judge(Rule("bad-range", ports: "9000-8000")).Allowed, "invalid port range cannot match");
    }

    private static void CaptureTimeout()
    {
        var capture = typeof(PhoneReachability).GetMethod("RunCapture", HiddenStatic)!;
        var exe = Environment.ProcessPath!;
        var result = (string)capture.Invoke(null, new object[] { exe, "--capture-child", 5_000 })!;
        Check(result.Length == 262_144 && result.Count(c => c == 'e') == 131_072, "diagnostics drain stdout and stderr without deadlock");
        var timer = Stopwatch.StartNew();
        result = (string)capture.Invoke(null, new object[] { exe, "--sleep-child", 150 })!;
        Check(result.Length == 0 && timer.Elapsed < TimeSpan.FromSeconds(3), "diagnostic timeout bounds a stalled process");
    }

    private static async Task BeaconLifecycle()
    {
        var bridge = PhoneBridgeService.Instance;
        typeof(PhoneBridgeService).GetProperty("Fingerprint")!.SetValue(bridge, new string('A', 64));
        var start = typeof(PhoneBeacon).GetMethod("Start", HiddenInstance, new[] { typeof(int), typeof(IPAddress) })!;
        var field = typeof(PhoneBeacon).GetField("_socket", HiddenInstance)!;
        var races = new List<Exception>();
        EventHandler<UnobservedTaskExceptionEventArgs> handler = (_, e) =>
        {
            lock (races) races.AddRange(e.Exception.Flatten().InnerExceptions.Where(x => x is NullReferenceException));
            e.SetObserved();
        };
        TaskScheduler.UnobservedTaskException += handler;
        try
        {
            using var beacon = new PhoneBeacon(bridge);
            for (var i = 0; i < 250; i++) { start.Invoke(beacon, new object[] { 0, IPAddress.Loopback }); beacon.Stop(); }
            start.Invoke(beacon, new object[] { 0, IPAddress.Loopback });
            var socket = (UdpClient)field.GetValue(beacon)!;
            var endpoint = (IPEndPoint)socket.Client.LocalEndPoint!;
            Check(IPAddress.IsLoopback(endpoint.Address), "discovery fixture binds loopback only");
            using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var probe = Encoding.ASCII.GetBytes("VIBECODE-DISCOVER-1".PadRight(256));
            await client.SendAsync(probe, endpoint);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var reply = await client.ReceiveAsync(timeout.Token);
            using var json = JsonDocument.Parse(reply.Buffer);
            Check(json.RootElement.GetProperty("app").GetString() == "vibecode"
                  && json.RootElement.GetProperty("fingerprint").GetString() == new string('A', 64), "discovery replies after rapid restarts");
            Check(reply.Buffer.Length <= probe.Length, "discovery response does not amplify request");
            beacon.Stop();
            using (var rebound = new UdpClient(new IPEndPoint(IPAddress.Loopback, endpoint.Port)))
                Check(rebound.Client.IsBound, "stopping discovery releases its socket");
            await Task.Delay(150);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Check(races.Count == 0, "rapid start-stop does not dereference a replaced cancellation source");
        }
        finally { TaskScheduler.UnobservedTaskException -= handler; }
    }

    private static void RenderSetup(string output)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/VibeCode;component/Themes/Dark.xaml") });
        var window = new PhoneBridgeWindow();
        var root = (FrameworkElement)window.Content;
        // These are layout fixtures, not network evidence. The running presentation is selected without
        // opening a listener, so its address list and wrapping can be inspected in the same bounded batch.
        var running = typeof(PhoneBridgeService).GetProperty("Running")!;
        var busy = typeof(PhoneBridgeWindow).GetField("_diagnosticsBusy", HiddenInstance)!;
        foreach (var state in new[] { false, true })
        {
            running.SetValue(PhoneBridgeService.Instance, state);
            var wait = Stopwatch.StartNew();
            do
            {
                var frame = new DispatcherFrame();
                window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
                Thread.Sleep(20);
            } while ((bool)busy.GetValue(window)! && wait.Elapsed < TimeSpan.FromSeconds(15));
            Check(!(bool)busy.GetValue(window)!, "setup diagnostics finish on the window dispatcher");
            foreach (var width in new[] { 520, 600 })
            {
                root.Measure(new Size(width, 690));
                root.Arrange(new Rect(0, 0, width, 690));
                root.UpdateLayout();
                var bitmap = new RenderTargetBitmap(width, 690, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(root);
                var png = new PngBitmapEncoder();
                png.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(output, $"setup-fixture-{(state ? "listening" : "off")}-{width}.png"));
                png.Save(file);
            }
        }
        running.SetValue(PhoneBridgeService.Instance, false);
        // Let queued UI callbacks run once, then close the fixture without starting the app or a listener.
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
        window.Close();
        Check(typeof(PhoneBridgeService).GetField("_listener", HiddenInstance)!.GetValue(PhoneBridgeService.Instance) is null,
            "setup rendering never starts the bridge");
        app.Shutdown();
    }

    private static async Task TlsLifecycle()
    {
        var bridge = PhoneBridgeService.Instance;
        var start = typeof(PhoneBridgeService).GetMethod("Start", HiddenInstance, new[] { typeof(IPAddress) })!;
        // Reserve only long enough to choose a local free port; the actual bridge handles bind conflicts below.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        bridge.SetPort(port);
        start.Invoke(bridge, new object[] { IPAddress.Loopback });
        Check(!bridge.Running && bridge.HasError, "occupied port reports a stopped bridge");
        probe.Stop();
        try
        {
            for (var i = 0; i < 50; i++)
            {
                start.Invoke(bridge, new object[] { IPAddress.Loopback });
                bridge.Stop(remember: false);
            }
            start.Invoke(bridge, new object[] { IPAddress.Loopback });
            Check(bridge.Running && !bridge.HasError, "TLS listener recovers after bind failure and rapid restarts");
            var listener = (TcpListener)typeof(PhoneBridgeService).GetField("_listener", HiddenInstance)!.GetValue(bridge)!;
            Check(IPAddress.IsLoopback(((IPEndPoint)listener.LocalEndpoint).Address), "TLS fixture binds loopback only");
            var pin = bridge.Fingerprint;
            using var handler = new HttpClientHandler
            {
                UseProxy = false,
                SslProtocols = SslProtocols.Tls12,
                ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert is not null
                    && Convert.ToHexString(SHA256.HashData(cert.RawData)) == pin,
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
            using var ping = await client.GetAsync($"https://127.0.0.1:{port}/api/ping");
            Check(ping.StatusCode == HttpStatusCode.OK, "real bridge responds to pinned TLS 1.2 ping");
            using var chats = await client.GetAsync($"https://127.0.0.1:{port}/api/chats");
            Check(chats.StatusCode == HttpStatusCode.Unauthorized, "real TLS listener requires credentials for chats");
            bridge.Stop(remember: false);
            start.Invoke(bridge, new object[] { IPAddress.Loopback });
            Check(bridge.Fingerprint == pin, "stop/start preserves server TLS identity");
            using var again = await client.GetAsync($"https://127.0.0.1:{port}/api/ping");
            Check(again.StatusCode == HttpStatusCode.OK, "client reconnects with original certificate pin");
        }
        finally { bridge.Stop(); }
    }
}
