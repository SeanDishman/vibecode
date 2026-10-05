using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Threading;
using VibeCode.Protocol;
using VibeCode.Services;
using VibeCode.UI;

// Real TLS/HTTP/authentication/dispatcher/mirror, with a provider adapter that can never launch a process.
// The only listeners are loopback. Set isolation BEFORE touching any VibeCode static property.
internal static class Program
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private const int Port = 18766, ControlPort = 18767;
    private static PhoneBridgeService bridge = null!;
    private static MainViewModel vm = null!;
    private static ChatViewModel chat = null!;
    private static FixtureSession session = null!;
    private static string output = "";
    private static int restarts;

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length != 1) throw new ArgumentException("Pass a disposable artifact directory.");
        output = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(output);
        var data = Path.Combine(output, "data-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("VIBECODE_DATA_DIR", data);
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(application.Dispatcher));
        application.DispatcherUnhandledException += (_, e) =>
        {
            File.AppendAllText(Path.Combine(output, "errors.log"), e.Exception + Environment.NewLine);
            Console.Error.WriteLine(e.Exception);
        };
        AppSettings.Current.PhoneEnabled = false;
        AppSettings.Current.PhoneBridgeEnabled = false;
        AppSettings.Current.AgentMemoryEnabled = false;
        AppSettings.Current.SecondBrainEnabled = false;
        AppSettings.Current.AgentSwarmsEnabled = false;

        var workspace = Path.Combine(data, "synthetic-project");
        Directory.CreateDirectory(workspace);
        vm = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
        Set(vm, "<Chats>k__BackingField", new ObservableCollection<ChatViewModel>());
        Set(vm, "<Projects>k__BackingField", new ObservableCollection<ProjectVm>());
        Set(vm, "<RecentProjects>k__BackingField", new ObservableCollection<ProjectVm>());
        Set(vm, "<DefaultCwd>k__BackingField", workspace);
        chat = new ChatViewModel(workspace, title: "Isolated .NET bridge", accountId: "fixture-no-account", provider: "codex");
        session = new FixtureSession(chat);
        Set(chat, "_session", session);
        chat.Status = "idle";
        chat.Items.Add(new UserItem { Text = "Synthetic desktop transcript", Owner = chat });
        chat.Items.Add(new TextItem { Text = "Synthetic .NET response" });
        chat.Items.Add(new PermItem { RequestId = "permission-dotnet", ToolName = "Read", Owner = chat,
            Input = new JsonObject { ["file_path"] = Path.Combine(workspace, "fixture.txt") } });
        var question = new PermItem { RequestId = "question-dotnet", ToolName = "AskUserQuestion", Owner = chat, Input = new JsonObject() };
        var entry = new QuestionEntry { Question = "Which fixture?" };
        entry.Options.Add(new QuestionOption { Label = "First", Description = "Synthetic choice" });
        question.Questions.Add(entry);
        chat.Items.Add(question);
        chat.Items.Add(new PermItem { RequestId = "plan-dotnet", ToolName = "ExitPlanMode", Owner = chat,
            Input = new JsonObject { ["plan"] = "Synthetic plan only" } });
        var pending = (IDictionary<string, PermItem>)typeof(ChatViewModel).GetField("_pendingPerms", Hidden)!.GetValue(chat)!;
        foreach (var permission in chat.Items.OfType<PermItem>()) pending.Add(permission.RequestId, permission);
        vm.Chats.Add(chat);
        bridge = PhoneBridgeService.Instance;
        Start(bridge);
        Set(bridge, "_pairingCode", "123456");
        Set(bridge, "_pairingExpires", DateTime.UtcNow.AddHours(1));
        File.WriteAllText(Path.Combine(output, "fixture.json"), new JsonObject
        {
            ["port"] = Port, ["controlPort"] = ControlPort, ["fingerprint"] = bridge.Fingerprint,
            ["chatId"] = chat.BridgeId, ["dataDirectory"] = data,
        }.ToJsonString());
        _ = Task.Run(ControlLoop);
        Console.WriteLine($"READY loopback bridge {Port}, controls {ControlPort}; no provider processes.");
        application.Run();
    }

    private static void Set(object target, string field, object value) => target.GetType().GetField(field, Hidden)!.SetValue(target, value);
    private static void Start(PhoneBridgeService service)
    {
        service.Attach(vm, Application.Current.Dispatcher);
        service.SetPort(Port);
        typeof(PhoneBridgeService).GetMethod("Start", Hidden, new[] { typeof(IPAddress) })!.Invoke(service, new object[] { IPAddress.Loopback });
        if (!service.Running) throw new InvalidOperationException(service.Error);
    }

    private static JsonObject Control(string path)
    {
        if (path == "/fixture/restart")
        {
            bridge.Stop(remember: false);
            bridge = (PhoneBridgeService)Activator.CreateInstance(typeof(PhoneBridgeService), nonPublic: true)!;
            Start(bridge);
            restarts++;
        }
        else if (path == "/fixture/build-apk")
        {
            // Build() uses the production singleton. Reuse it on LOOPBACK, never let Build() start Any itself.
            bridge.Stop(remember: false);
            bridge = PhoneBridgeService.Instance;
            foreach (var device in bridge.Devices.ToList()) bridge.Revoke(device);
            Start(bridge);
            var first = PhoneApkBuilder.Build();
            File.Copy(first, Path.Combine(output, "personalized-first.apk"), overwrite: true);
            var second = PhoneApkBuilder.Build();
            File.Copy(second, Path.Combine(output, "personalized-update.apk"), overwrite: true);
            using var embedded = typeof(PhoneApkBuilder).Assembly.GetManifestResourceStream("VibeCode.Assets.vibecode-mobile.apk")!;
            var digest = Convert.ToHexString(SHA256.HashData(embedded));
            File.WriteAllText(Path.Combine(output, "embedded-template-sha256.txt"), digest);
            return new JsonObject { ["built"] = true, ["embeddedSha256"] = digest };
        }
        else if (path == "/fixture/stop")
        {
            bridge.Stop(remember: false);
            Application.Current.Dispatcher.BeginInvoke(Application.Current.Shutdown, DispatcherPriority.ApplicationIdle);
        }
        else if (path != "/fixture/state") throw new InvalidOperationException("Unknown fixture route");
        return new JsonObject
        {
            ["restarts"] = restarts, ["sends"] = session.Sends, ["lastContent"] = session.LastContent,
            ["mode"] = chat.Mode, ["modelCalls"] = session.ModelCalls, ["interrupts"] = session.Interrupts,
            ["permissionReplies"] = session.PermissionReplies, ["startCalls"] = session.StartCalls,
        };
    }

    private static async Task ControlLoop()
    {
        using var certificate = PhoneBridgeStore.LoadOrCreateCertificate();
        var listener = new TcpListener(IPAddress.Loopback, ControlPort);
        listener.Start();
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var tls = new SslStream(client.GetStream());
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                { ServerCertificate = certificate, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, timeout.Token);
                using var reader = new StreamReader(tls, Encoding.ASCII, false, 1024, leaveOpen: true);
                var line = await reader.ReadLineAsync(timeout.Token) ?? "";
                var total = line.Length;
                while (await reader.ReadLineAsync(timeout.Token) is { Length: > 0 } header)
                    if ((total += header.Length) > 8192) throw new InvalidDataException("Fixture header limit");
                var parts = line.Split(' ');
                if (parts.Length != 3 || parts[0] != "GET") throw new InvalidDataException("Fixture only accepts GET controls");
                var body = await Application.Current.Dispatcher.InvokeAsync(() => Control(parts[1])).Task;
                var bytes = Encoding.UTF8.GetBytes(body.ToJsonString());
                var head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
                await tls.WriteAsync(head, timeout.Token);
                await tls.WriteAsync(bytes, timeout.Token);
            }
            catch (Exception e)
            {
                File.AppendAllText(Path.Combine(output, "errors.log"), e + Environment.NewLine);
                Console.Error.WriteLine(e);
            }
        }
    }

    private sealed class FixtureSession(ChatViewModel owner) : ICodingSession
    {
#pragma warning disable CS0067
        public event Action<JsonNode>? MessageReceived;
        public event Action<PermissionRequest>? PermissionRequested;
        public event Action<string>? PermissionCancelled;
        public event Action<int, string>? Exited;
        public event Action? Initialized;
#pragma warning restore CS0067
        public JsonArray Commands { get; } = new();
        public JsonArray Models { get; } = new();
        public string? SessionId => "isolated-fixture-session";
        public bool HasExited => false;
        public int Sends, StartCalls, ModelCalls, Interrupts, PermissionReplies;
        public string LastContent = "";
        public void Start() { StartCalls++; throw new InvalidOperationException("Fixture may never start a provider"); }
        public void SendUser(JsonNode content)
        {
            Sends++;
            LastContent = (content is JsonValue
                ? new JsonArray(new JsonObject { ["text"] = content.GetValue<string>() })
                : content).ToJsonString();
            owner.Items.Add(new TextItem { Text = "Fixture adapter received Unicode prompt" });
            // Stay running so queue + stop dispatch can be checked without any model execution.
        }
        public Task InterruptAsync() { Interrupts++; owner.Status = "idle"; return Task.CompletedTask; }
        public Task SetPermissionModeAsync(string mode) => Task.CompletedTask;
        public Task SetModelAsync(string? model, string? effort = null) { ModelCalls++; return Task.CompletedTask; }
        public void RespondPermission(string requestId, JsonObject result, string? toolUseId) { PermissionReplies++; }
        public void Dispose() { }
    }
}
