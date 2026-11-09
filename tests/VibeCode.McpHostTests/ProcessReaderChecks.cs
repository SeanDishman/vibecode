using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using VibeCode.Services;

internal static partial class Program
{
    // A synchronous Windows pipe, as used by redirected process stdout/stderr. Observe where the actual blocking
    // read runs, not merely which thread creates the awaitable. No subprocess, native console or window is needed.
    private sealed class ObservedPipeStream(Stream inner, Action entered) : Stream
    {
        private int _started;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (Interlocked.Exchange(ref _started, 1) == 0) entered();
            return inner.Read(buffer, offset, count);
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static void VerifyProcessReaders(Dispatcher dispatcher, bool baseline)
    {
        AppSettings.Current.BridgeToolsInProcess = true;
        var (connection, calls) = NewConnection(dispatcher, "reader-pressure");
        using var owned = connection;
        var definition = Registration(connection);
        var client = new HttpMcpClient(definition.Url!, TokenOf(definition), "reader-pressure");
        client.InitializeAsync().GetAwaiter().GetResult();
        var pipes = new List<(NamedPipeServerStream Server, NamedPipeClientStream Client, StreamReader Reader)>();
        var readers = new List<Task>();
        var lines = new ConcurrentQueue<string>();
        var readThreads = new ConcurrentQueue<(bool Pool, bool Background)>();
        using var entered = new CountdownEvent(4);
        var startReader = App.GetType("VibeCode.Services.ProcessOutputReader", throwOnError: true)!.GetMethod("Run", Any)!;
        Task ReadOnOwnThread(Action action) => (Task)startReader.Invoke(null, [action, "pipe fixture"] )!;
        ThreadPool.GetMinThreads(out var oldMin, out var ioMin);
        ThreadPool.GetMaxThreads(out var oldMax, out var ioMax);
        Task<HttpReply>? request = null;
        var watch = new Stopwatch();
        try
        {
            Check(ThreadPool.SetMinThreads(4, ioMin) && ThreadPool.SetMaxThreads(4, ioMax), "only the isolated fixture limits the shared worker pool");
            for (var index = 0; index < 12; index++)
            {
                var name = "vibecode-reader-fixture-" + Guid.NewGuid().ToString("N");
                var server = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None);
                var writer = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.None);
                writer.Connect(2000);
                server.WaitForConnection();
                var stream = new ObservedPipeStream(server, () =>
                {
                    readThreads.Enqueue((Thread.CurrentThread.IsThreadPoolThread, Thread.CurrentThread.IsBackground));
                    lock (entered) { if (entered.CurrentCount > 0) entered.Signal(); }
                });
                var reader = new StreamReader(stream, new UTF8Encoding(false), leaveOpen: true);
                pipes.Add((server, writer, reader));
                if (baseline) readers.Add(OldAsyncReader(reader, lines));
                else readers.Add(ReadOnOwnThread(() => { while (reader.ReadLine() is { } line) lines.Enqueue(line); }));
            }
            Check(entered.Wait(5000), "idle pipe readers are waiting for child output");
            watch.Start();
            request = client.SendAsync(ToolCall(2, "bridge_list_agents"));
            var responsive = request.Wait(1500);
            watch.Stop();
            if (baseline)
            {
                Check(!responsive && calls.Count == 0, "old asynchronous pipe reads starve a real local MCP request before its handler runs");
                Check(readThreads.All(t => t.Pool), "old reads occupy shared pool workers while children are idle");
            }
            else
            {
                Check(responsive && request.Result.Body?["result"]?["isError"]?.GetValue<bool>() != true,
                    "MCP responds while twelve child pipes are idle and the worker pool has only four threads");
                Check(calls.Count == 1, "responsive MCP handler runs exactly once");
                Check(SpinWait.SpinUntil(() => readThreads.Count == 12, 2000) && readThreads.All(t => !t.Pool && t.Background),
                    "all blocking reads run on background threads outside the shared worker pool");
                Check(Task.Delay(50).Wait(1000), "timers remain responsive alongside idle process readers");
            }
            Console.WriteLine($"MCP response under idle-pipe pressure: {(responsive ? watch.ElapsedMilliseconds + " ms" : ">1500 ms (blocked)")}");
            // Fragmented UTF-8, CRLF and a final unterminated line must survive the reader change.
            var bytes = Encoding.UTF8.GetBytes("first\r\n\u732b second\nlast");
            foreach (var pipe in pipes)
            {
                pipe.Client.Write(bytes, 0, 9);
                pipe.Client.Write(bytes, 9, bytes.Length - 9);
                pipe.Client.Dispose();
            }
            Check(Task.WaitAll(readers.ToArray(), 5000), "readers finish when child pipes close");
            Check(lines.Count == 36 && lines.Count(l => l == "first") == 12 && lines.Count(l => l == "\u732b second") == 12
                && lines.Count(l => l == "last") == 12, "output preserves fragmented Unicode, line boundaries and final partial lines");
            Check(request.Wait(5000) && calls.Count == 1, "request is never duplicated when idle reads are released");
            if (!baseline)
            {
                var faulted = ReadOnOwnThread(() => throw new IOException("fixture read failure"));
                try { faulted.GetAwaiter().GetResult(); throw new Exception("Reader failure was hidden"); }
                catch (IOException ex) { Check(ex.Message == "fixture read failure", "read failure is observable without crashing a background thread"); }
            }
        }
        finally
        {
            foreach (var pipe in pipes) pipe.Client.Dispose();
            ThreadPool.SetMaxThreads(oldMax, ioMax);
            ThreadPool.SetMinThreads(oldMin, ioMin);
            try { Task.WaitAll(readers.ToArray(), 5000); } catch { }
            foreach (var pipe in pipes) { pipe.Reader.Dispose(); pipe.Server.Dispose(); }
        }
    }

    private static async Task OldAsyncReader(StreamReader reader, ConcurrentQueue<string> lines)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line) lines.Enqueue(line);
    }

}
