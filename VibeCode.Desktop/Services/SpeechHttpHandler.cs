using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;

namespace VibeCode.Services;

/// <summary>Windows networking for the dedicated dictation worker, without the managed connection queue.</summary>
internal sealed class SpeechHttpHandler : HttpMessageHandler
{
    private readonly HttpMessageInvoker _proxyFallback = new(new SocketsHttpHandler());

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var uri = request.RequestUri ?? throw new InvalidOperationException("Speech request has no destination.");
        var proxy = HttpClient.DefaultProxy;
        var address = proxy.IsBypassed(uri) ? null : proxy.GetProxy(uri);
        if (address == uri) address = null;
        // WinHTTP cannot tunnel through SOCKS or TLS-to-proxy endpoints. Preserve those configurations
        // through the previous transport; direct connections and HTTP proxies use the independent worker.
        if (address is not null && address.Scheme != Uri.UriSchemeHttp)
            return _proxyFallback.Send(request, cancellationToken);
        using var body = new MemoryStream();
        request.Content?.CopyTo(body, null, cancellationToken);
        var nativeObject = Activator.CreateInstance(Type.GetTypeFromProgID("WinHttp.WinHttpRequest.5.1", throwOnError: true)!)!;
        dynamic native = nativeObject;
        try
        {
            native.SetTimeouts(10000, 15000, 120000, 120000);
            native.Open(request.Method.Method, uri.AbsoluteUri, true);
            native.Option[6] = false; // EnableRedirects: never forward the audio or bearer key to another URL.
            native.Option[9] = 0x800; // SecureProtocols: TLS 1.2, supported by every Windows version this app targets.
            native.SetAutoLogonPolicy(2); // Never send the Windows user's credentials automatically.
            ConfigureProxy(native, proxy, address);
            foreach (var header in request.Headers)
                native.SetRequestHeader(header.Key, string.Join(", ", header.Value));
            if (request.Content is not null)
                foreach (var header in request.Content.Headers)
                    native.SetRequestHeader(header.Key, string.Join(", ", header.Value));

            cancellationToken.ThrowIfCancellationRequested();
            var deadline = Stopwatch.StartNew();
            native.Send(body.ToArray());
            // WinHTTP owns the native I/O callbacks. Waiting here consumes only our speech worker;
            // HttpClient.Send still needs a shared worker to create a connection under SocketsHttpHandler.
            while (!native.WaitForResponse(1))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (deadline.Elapsed >= TimeSpan.FromMinutes(2))
                    throw new TimeoutException("Groq did not respond within two minutes.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new HttpResponseMessage((HttpStatusCode)(int)native.Status)
            {
                RequestMessage = request,
                Content = new ByteArrayContent((byte[])native.ResponseBody),
            };
        }
        catch (COMException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (COMException ex)
        {
            throw new HttpRequestException("Windows could not complete the speech request: " + ex.Message, ex);
        }
        finally
        {
            // Abort and release on the same worker/apartment. Cancellation needs neither the UI nor
            // a ThreadPool callback, including when the server has stopped sending data.
            try { native.Abort(); } catch (COMException) { }
            Marshal.FinalReleaseComObject(nativeObject);
        }
    }

    private static void ConfigureProxy(dynamic native, IWebProxy proxy, Uri? address)
    {
        if (address is null)
        {
            native.SetProxy(1); // DIRECT, matching the default HttpClient's bypass decision.
            return;
        }
        native.SetProxy(2, address.Authority);
        if (proxy.Credentials?.GetCredential(address, "Basic") is { UserName.Length: > 0 } credential)
            native.SetCredentials(string.IsNullOrEmpty(credential.Domain) ? credential.UserName : credential.Domain + "\\" + credential.UserName,
                credential.Password, 1); // Credentials explicitly configured for the proxy, not the speech service.
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Speech HTTP must run on the dedicated request worker.");

    protected override void Dispose(bool disposing)
    {
        if (disposing) _proxyFallback.Dispose();
        base.Dispose(disposing);
    }
}
