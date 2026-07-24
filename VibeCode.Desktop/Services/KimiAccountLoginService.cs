using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using VibeCode.Protocol;

namespace VibeCode.Services;

public sealed class KimiAccountState
{
    public bool IsInstalled { get; init; }
    public bool IsSignedIn { get; init; }
    public string? Version { get; init; }
    public string? Error { get; init; }
}

public sealed class KimiLoginResult
{
    public bool Success { get; init; }
    public string? Version { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Checks and signs in the shared Kimi Code CLI account using only Kimi's supported interfaces. Account status is
/// read through ACP's authenticate method (which neither creates a session nor spends model tokens); login runs the
/// official RFC 8628 device-code command and never reads or logs the credential files under ~/.kimi-code.
/// </summary>
public static partial class KimiAccountLoginService
{
    private static readonly SemaphoreSlim AccountGate = new(1, 1);

    public static async Task<KimiAccountState> ReadAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        var token = timeout.Token;
        var held = false;
        Process? process = null;
        Task<string>? stderrTask = null;
        try
        {
            await AccountGate.WaitAsync(token).ConfigureAwait(false);
            held = true;
            process = Start("acp");
            stderrTask = process.StandardError.ReadToEndAsync(token);

            await WriteAsync(process, 1, "initialize", new JsonObject
            {
                ["protocolVersion"] = 1,
                ["clientCapabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject
                {
                    ["name"] = "vibecode-account-check",
                    ["title"] = "VibeCode",
                    ["version"] = "1.0.0",
                },
            }, token).ConfigureAwait(false);
            var initialize = await ReadResponseAsync(process, 1, token).ConfigureAwait(false);
            ThrowIfError(initialize);
            var version = initialize["result"]?["agentInfo"]?["version"]?.GetValue<string>();

            await WriteAsync(process, 2, "authenticate", new JsonObject { ["methodId"] = "login" }, token)
                .ConfigureAwait(false);
            var auth = await ReadResponseAsync(process, 2, token).ConfigureAwait(false);
            if (auth["error"] is JsonObject error)
            {
                var code = JsonInt(error["code"]);
                if (code == -32000)
                    return new KimiAccountState { IsInstalled = true, IsSignedIn = false, Version = version };
                ThrowIfError(auth);
            }
            return new KimiAccountState { IsInstalled = true, IsSignedIn = true, Version = version };
        }
        catch (FileNotFoundException)
        {
            return new KimiAccountState { IsInstalled = false, IsSignedIn = false };
        }
        catch (OperationCanceledException)
        {
            return new KimiAccountState
            {
                IsInstalled = true,
                Error = cancellationToken.IsCancellationRequested ? "Kimi account check cancelled." : "Kimi account check timed out.",
            };
        }
        catch (Exception ex)
        {
            return new KimiAccountState { IsInstalled = true, Error = ex.Message };
        }
        finally
        {
            if (process is not null) Stop(process);
            if (stderrTask is not null)
                try { _ = await stderrTask.ConfigureAwait(false); }
                catch { /* cancellation/process teardown; the account result above is authoritative */ }
            if (held) AccountGate.Release();
        }
    }

    public static async Task<KimiLoginResult> LoginAsync(
        Action<string> openBrowser,
        Action<string>? statusChanged = null,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(6));
        var token = timeout.Token;
        var held = false;
        Process? process = null;
        try
        {
            await AccountGate.WaitAsync(token).ConfigureAwait(false);
            held = true;
            process = Start("login");
            var recent = new Queue<string>();
            var outputGate = new object();
            var opened = 0;

            void ObserveOutput(string raw)
            {
                var line = AnsiRegex().Replace(raw, "").Trim();
                if (line.Length == 0) return;
                lock (outputGate)
                {
                    recent.Enqueue(line);
                    while (recent.Count > 8) recent.Dequeue();
                }
                statusChanged?.Invoke(line);
                var match = UrlRegex().Match(line);
                if (match.Success && Interlocked.CompareExchange(ref opened, 1, 0) == 0)
                    openBrowser(match.Value.TrimEnd('.', ',', ';', ')', ']'));
            }

            async Task PumpAsync(StreamReader reader)
            {
                while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } raw)
                    ObserveOutput(raw);
            }

            // Different Kimi CLI releases and terminal modes have written the device-code URL to different streams.
            // Drain and observe both so redirected desktop login never hides the browser link or deadlocks a full pipe.
            var stdoutTask = PumpAsync(process.StandardOutput);
            var stderrTask = PumpAsync(process.StandardError);
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            if (process.ExitCode != 0)
                return new KimiLoginResult
                {
                    Error = RecentOutput(),
                };

            // Release the gate before the supported ACP status check reacquires it.
            process.Dispose();
            process = null;
            AccountGate.Release();
            held = false;
            var state = await ReadAsync(token).ConfigureAwait(false);
            return state.IsSignedIn
                ? new KimiLoginResult { Success = true, Version = state.Version }
                : new KimiLoginResult { Error = state.Error ?? "Kimi finished login but the account is still not available." };

            string RecentOutput()
            {
                lock (outputGate)
                    return recent.Count > 0
                        ? string.Join(Environment.NewLine, recent)
                        : $"Kimi login exited with code {process?.ExitCode ?? -1}.";
            }
        }
        catch (FileNotFoundException ex)
        {
            return new KimiLoginResult { Error = ex.Message };
        }
        catch (OperationCanceledException)
        {
            return new KimiLoginResult
            {
                Error = cancellationToken.IsCancellationRequested ? "Kimi sign-in cancelled." : "Kimi sign-in timed out.",
            };
        }
        catch (Exception ex)
        {
            return new KimiLoginResult { Error = ex.Message };
        }
        finally
        {
            if (process is not null) Stop(process);
            if (held) AccountGate.Release();
        }
    }

    private static Process Start(params string[] args)
    {
        var process = new Process { StartInfo = KimiSession.CreateCliStartInfo(null, args) };
        process.Start();
        ProcessJob.Assign(process);
        return process;
    }

    private static async Task WriteAsync(Process process, int id, string method, JsonObject parameters,
        CancellationToken token)
    {
        var message = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters,
        }.ToJsonString(new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        await process.StandardInput.WriteLineAsync(message.AsMemory(), token).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(token).ConfigureAwait(false);
    }

    private static async Task<JsonObject> ReadResponseAsync(Process process, int id, CancellationToken token)
    {
        while (await process.StandardOutput.ReadLineAsync(token).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonObject? message;
            try { message = JsonNode.Parse(line) as JsonObject; }
            catch { continue; }
            if (JsonInt(message?["id"]) == id) return message!;
        }
        throw new InvalidOperationException("Kimi ACP closed before it answered the account check.");
    }

    private static void ThrowIfError(JsonObject message)
    {
        if (message["error"] is not JsonObject error) return;
        throw new InvalidOperationException(error["message"]?.GetValue<string>() ?? error.ToJsonString());
    }

    private static int JsonInt(JsonNode? node)
    {
        try { return node?.GetValue<int>() ?? 0; } catch { /* wider number */ }
        try { return checked((int)(node?.GetValue<long>() ?? 0)); } catch { return 0; }
    }

    private static void Stop(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                try { process.StandardInput.Close(); } catch { /* broken pipe */ }
                if (!process.WaitForExit(1200)) process.Kill(entireProcessTree: true);
            }
        }
        catch { /* already gone */ }
        process.Dispose();
    }

    [GeneratedRegex(@"\x1B(?:[@-Z\\-_]|\[[0-?]*[ -/]*[@-~])", RegexOptions.Compiled)]
    private static partial Regex AnsiRegex();

    [GeneratedRegex(@"https://[^\s\x1b]+", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex UrlRegex();
}
