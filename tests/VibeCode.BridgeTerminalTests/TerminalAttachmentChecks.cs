using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VibeCode.Terminal;
using VibeCode.UI;

internal static partial class Program
{
    private static void VerifyTerminalAttachments(bool headless = false)
    {
        SeedBridgeCodexAccount("attachments-fixture", "Attachments fixture");
        var log = Path.Combine(_root, "attachment-provider.log"); File.WriteAllText(log, "");
        Environment.SetEnvironmentVariable("VIBECODE_TEAM_FIXTURE_LOG", log);
        Application.Current.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(
            new Uri("/VibeCode;component/Themes/Dark.xaml", UriKind.Relative)));
        var (vm, original) = BridgeHost("files-workspace", "codex", "Attachments fixture");
        original.SetMode("default", remember: false);
        var host = vm.CreateTerminalTeam(original); Chats.Add(host);
        var image = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { 10, 180, 20, 255, 10, 180, 20, 255, 10, 180, 20, 255, 10, 180, 20, 255 }, 8);
        image.Freeze();
        var png = Path.Combine(host.Cwd, "sample image.png");
        using (var file = File.Create(png)) { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); encoder.Save(file); }
        var txt = Path.Combine(host.Cwd, "notes.txt"); File.WriteAllText(txt, "attachment-fixture-content");
        var pdf = Path.Combine(host.Cwd, "reference.pdf"); File.WriteAllText(pdf, "%PDF-1.4\nfixture\n%%EOF");
        var source = new AttachmentFixture { Files = [png], Clipboard = new(Image: image) };
        using var terminal = new TerminalSession(vm, host, Dispatcher.CurrentDispatcher, attachments: source);
        TerminalPacket Attach(string command, string[]? paths = null, string? id = null, string? context = null)
        {
            var task = terminal.HandleAttachmentsAsync(new("attachment", command, ContextId: context ?? terminal.Host.BridgeAgentId, Paths: paths, AttachmentId: id));
            PumpUntil(() => task.IsCompleted); return task.GetAwaiter().GetResult();
        }
        Check("terminal advertises attachment support without reading files or clipboard", terminal.Snapshot().Attachments is { Length: 0 } && source.Reads == 0 && source.Picks == 0);
        Check("file paths stage image, document and text together", Attach("paths", ["sample image.png", "reference.pdf", "notes.txt"]).Accepted
            && host.Attachments.Select(a => a.Kind).SequenceEqual(["image", "document", "text"]));
        Check("terminal uses original image bytes without allocating thumbnails", host.Attachments[0].Data!.SequenceEqual(File.ReadAllBytes(png)) && host.Attachments.All(a => a.Preview is null));
        Check("pipe snapshots contain names and sizes without file contents", terminal.Snapshot().Attachments is { Length: 3 } metadata && metadata[0].Name == "sample image.png"
            && !JsonSerializer.Serialize(terminal.Snapshot()).Contains("attachment-fixture-content"));
        Check("a batch with a missing file is atomic and preserves the draft", !Attach("paths", [txt, "missing.txt"]).Accepted && host.Attachments.Count == 3);
        Check("old-chat requests cannot attach to the new chat", !Attach("paths", [txt], context: "stale-chat").Accepted && host.Attachments.Count == 3);
        var tooLarge = Path.Combine(host.Cwd, "too-large.txt");
        using (var file = File.Create(tooLarge)) file.SetLength(1_000_001);
        Check("text files retain the desktop size limit", !Attach("paths", [tooLarge]).Accepted && host.Attachments.Count == 3);
        var huge = Path.Combine(host.Cwd, "too-large.pdf");
        using (var file = File.Create(huge)) file.SetLength(TerminalSession.MaxAttachmentBytes + 1);
        Check("large binary payload is rejected before loading", !Attach("paths", [huge]).Accepted && host.Attachments.Count == 3);
        Check("attachment count is bounded", !Attach("paths", Enumerable.Repeat(txt, 13).ToArray()).Accepted && host.Attachments.Count == 3);
        var remove = terminal.Snapshot().Attachments![1].Id;
        Check("removing an attachment affects only its stable ID", Attach("remove", id: remove).Accepted && host.Attachments.Count == 2 && host.Attachments.All(a => a.Kind != "document"));
        Check("stale removals preserve remaining files", !Attach("remove", id: remove).Accepted && host.Attachments.Count == 2);
        Check("clipboard images use PNG attachment payloads", Attach("paste").Accepted && source.Reads == 1 && host.Attachments.Last() is { Kind: "image", MediaType: "image/png", FileName: "pasted-image.png" });
        source.Clipboard = new(Files: [pdf]);
        Check("clipboard file drops stage the file contents", Attach("paste").Accepted && host.Attachments.Last().Kind == "document");
        source.Clipboard = new(Text: "first line\r\nsecond line");
        Check("plain clipboard text is returned as editable draft text", Attach("paste") is { Type: "clipboard-text", Text: "first line\r\nsecond line" } && host.Attachments.Count == 4);
        Check("oversized sends retain staged attachments", !terminal.HandleInput(new string('x', TerminalWire.MaxPromptLength + 1)).Accepted && host.Attachments.Count == 4);
        Check("both message aliases preserve staged files, including tab-separated commands",
            new[] { "/message 1 review", "/msg 1 review", "/MSG\t1 review" }.All(command => !terminal.HandleInput(command).Accepted && host.Attachments.Count == 4));
        Check("a completed prompt accepts its staged files", terminal.HandleInput("Review attached fixture files").Accepted);
        PumpUntil(() => host.Items.OfType<TextItem>().Any() && host.Status == "idle", 30_000);
        Check("successful send moves files from draft to saved user prompt", host.Attachments.Count == 0 && host.Items.OfType<UserItem>().Single(u => u.IsUserPrompt).Attachments?.Count == 4);
        var requests = ReadShared(log).Where(line => line.Contains("turn/start")).ToArray();
        Check("provider receives images plus document and text file content", requests.Any(line => line.Contains("localImage") && line.Contains("attachment-fixture-content") && line.Contains("A document is attached")));
        Check("sent attachment names remain visible in the transcript", terminal.Snapshot().Rows!.Any(r => r.Kind == "user" && r.Text.Contains("sample image.png") && r.Text.Contains("reference.pdf")));
        Attach("pick");
        Check("file picker selection uses the same staging path", source.Picks == 1 && host.Attachments.Count == 1);
        Check("image-only prompts reuse a finished agent", terminal.HandleInput("").Accepted && vm.TerminalTeamRoster(host).Count == 1);
        var second = vm.TerminalTeamRoster(host).Last(); Chats.Add(second);
        PumpUntil(() => second.Items.OfType<TextItem>().Any() && second.Status == "idle", 30_000);
        Check("reused teammate keeps the image-only prompt and identity", second == host
            && second.Items.OfType<UserItem>().Last(u => u.IsUserPrompt).Attachments?.Single().Kind == "image");
        Attach("pick"); var other = vm.CreateTerminalTeam(host); Chats.Add(other);
        terminal.SwitchHost(other);
        Check("switching chats preserves each chat's draft attachments", terminal.Snapshot().Attachments is { Length: 0 } && host.Attachments.Count == 1);
        terminal.SwitchHost(host); Check("returning restores the original attachment draft", terminal.Snapshot().Attachments is { Length: 1 });
        Attach("clear"); Check("clear removes only attachments", host.Attachments.Count == 0 && host.Items.OfType<UserItem>().Any());

        if (headless) return;
        // Exercise the session's actual pipe routing with injected picker/clipboard, without touching the user's clipboard.
        var start = terminal.StartAsync(); PumpUntil(() => start.IsCompleted, 30_000); start.GetAwaiter().GetResult();
        var process = (Process)typeof(TerminalSession).GetField("_process", Flags)!.GetValue(terminal)!;
        using (var cleanup = Process.GetProcessById(process.Id))
        {
            try
            {
                ProbeConsole(process, "attachment-session-initial", "Keep this draft");
                ControlProbe(process, new ControlAction("key", Key: ConsoleKey.F6));
                PumpUntil(() => host.Attachments.Count == 1);
                PumpUntil(() => ProbeConsole(process, "attachment-session-picked").Text.Contains("1 attached"));
                Check("F6 stages files through the actual terminal connection without sending", host.Items.OfType<UserItem>().Count(u => u.IsUserPrompt) == 2);
                source.Clipboard = new(Image: image);
                ControlProbe(process, new ControlAction("key", Key: ConsoleKey.V, Modifiers: 8));
                PumpUntil(() => host.Attachments.Count == 2);
                var capture = ProbeConsole(process, "attachment-session-pasted");
                Check("Ctrl+V attaches an image and preserves the typed draft", capture.Text.Contains("Keep this draft") && host.Attachments.Last().FileName == "pasted-image.png");
                ControlProbe(process, new ControlAction("key", Key: ConsoleKey.D, Modifiers: 8));
                PumpUntil(() => cleanup.HasExited);
                Check("closing the console preserves unsent attachment objects", host.Attachments.Count == 2);
            }
            finally { terminal.Dispose(); if (!cleanup.HasExited) cleanup.Kill(entireProcessTree: true); }
        }
        VerifyAttachmentConsole();
    }

    private sealed class AttachmentFixture : ITerminalAttachmentSource
    {
        public string[] Files = [];
        public TerminalClipboard Clipboard = new();
        public int Picks, Reads;
        public string[] PickFiles() { Picks++; return Files; }
        public TerminalClipboard ReadClipboard() { Reads++; return Clipboard; }
    }

    private static void VerifyAttachmentConsole()
    {
        var name = TerminalWire.PipePrefix + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var start = new ProcessStartInfo(Path.ChangeExtension(typeof(VibeCode.App).Assembly.Location, ".exe")) { UseShellExecute = false };
        start.ArgumentList.Add("--vibecode-terminal"); start.ArgumentList.Add(name);
        using var client = Process.Start(start)!;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(70));
        TerminalPacket Read() => TerminalWire.ReadAsync(pipe, stop.Token).GetAwaiter().GetResult()!;
        void Write(TerminalPacket p) => TerminalWire.WriteAsync(pipe, p, stop.Token).GetAwaiter().GetResult();
        TerminalAttachment[] attachments = [];
        var controls = new TerminalControls("astra", "GPT-6 Astra", [new("astra", "GPT-6 Astra")], "high", "High", [new("high", "High")], false, true);
        TerminalPacket State(string type = "snapshot", string text = "") => new(type, text, ContextId: "files-console", Project: @"C:\work\files", Controls: controls,
            Speech: new("idle", "F5 to dictate"), Attachments: attachments);
        try
        {
            pipe.WaitForConnectionAsync(stop.Token).GetAwaiter().GetResult(); Read(); Write(State());
            ProbeConsole(client, "files-before-resize"); ControlProbe(client, new ControlAction("resize", X: 100, Y: 28));
            var capture = ProbeConsole(client, "files-initial", "Draft stays");
            Check("production console shows Attach and Mic together", capture.Text.Contains("Attach · F6") && capture.Text.Contains("Mic · F5"));
            var rows = capture.Text.Split('\n'); var y = Array.FindIndex(rows, r => r.Contains("Attach · F6"));
            ControlProbe(client, new ControlAction("click", X: rows[y].IndexOf("Attach · F6", StringComparison.Ordinal), Y: y));
            Check("clicking Attach requests the file picker for the current chat", Read() is { Type: "attachment", Text: "pick", ContextId: "files-console" });
            attachments = [new("image-id", "screenshot.png", "image", "128 KB"), new("file-id", "notes.txt", "text", "2 KB")]; Write(State("attachments", "Files attached."));
            capture = ProbeConsole(client, "files-staged");
            Check("attachment status preserves editable text", capture.Text.Contains("2 attached") && capture.Text.Contains("screenshot.png") && capture.Text.Contains("Draft stays"));
            CaptureNativeConsole(new IntPtr(capture.Window), "terminal-attachments.png");
            ProbeConsole(client, "files-submit", "\r");
            Check("sending a draft binds it to the current chat", Read() is { Type: "input", Text: "Draft stays", ContextId: "files-console" });
            Write(State("ack", "Fixture rejection: draft kept.") with { Accepted = false });
            capture = ProbeConsole(client, "files-rejected");
            Check("rejected sends retain both text and attachment metadata", capture.Text.Contains("Draft stays") && capture.Text.Contains("2 attached"));
            ProbeConsole(client, "files-clear-draft", new string('\b', 11) + "/attachments\r");
            capture = ProbeConsole(client, "files-list");
            Check("attachment list exposes filenames and removal numbers", capture.Text.Contains("1. screenshot.png") && capture.Text.Contains("2. notes.txt"));
            ProbeConsole(client, "files-remove", "/detach 1\r");
            Check("removal uses the listed stable ID", Read() is { Type: "attachment", Text: "remove", AttachmentId: "image-id" });
            attachments = [attachments[1]]; Write(State("attachments", "Removed.")); ProbeConsole(client, "files-removed");
            ProbeConsole(client, "files-path", "/attach \"C:\\work\\space name.png\"\r");
            Check("quoted file paths remain one attachment", Read() is { Type: "attachment", Text: "paths", Paths: [@"C:\work\space name.png"] });
            Write(State("attachments", "Fixture selection cancelled.")); ProbeConsole(client, "files-path-reply");
            ProbeConsole(client, "files-empty-submit", "\r\r");
            Check("attachment-only Enter sends a prompt", Read() is { Type: "input", Text: "" });
            attachments = []; Write(State("ack")); ProbeConsole(client, "files-accepted");
            ControlProbe(client, new ControlAction("resize", X: 40, Y: 18));
            capture = ProbeConsole(client, "files-narrow");
            Check("all five controls remain usable in a narrow console", capture.Text.Contains("Attach · F6") && capture.Text.Contains("Mic · F5") && capture.Text.Contains("Fast:"));
            CaptureNativeConsole(new IntPtr(capture.Window), "terminal-attachments-narrow.png");
            ControlProbe(client, new ControlAction("key", Key: ConsoleKey.V, Modifiers: 8));
            Check("second Enter cannot duplicate an attachment-only prompt", Read() is { Type: "attachment", Text: "paste" });
            Write(State("clipboard-text", "pasted\ntext"));
            capture = ProbeConsole(client, "files-paste-text");
            Check("clipboard text remains an unsent multiline draft", capture.Text.Contains("pasted↵text"));
            ControlProbe(client, new ControlAction("key", Key: ConsoleKey.D, Modifiers: 8));
            Check("attachment console exits normally", Read().Type == "exit" && client.WaitForExit(5000) && client.ExitCode == 0);
        }
        finally { if (!client.HasExited) client.Kill(entireProcessTree: true); }
    }
}
