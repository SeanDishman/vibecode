using System.Diagnostics;
using System.Text.Json.Nodes;
using VibeCode.Services;

namespace VibeCode.Protocol;

public sealed partial class CodexSession
{
    private static Task<JsonNode?> TimeStartupOrDispatch(string method, Task<JsonNode?> request) => method is
        "initialize" or "account/read" or "model/list" or "thread/start" or "thread/resume" or "thread/fork"
        or "thread/read" or "turn/start" ? RecordRequestAsync(method, request) : request;

    private static async Task<JsonNode?> RecordRequestAsync(string method, Task<JsonNode?> request)
    {
        var started = Stopwatch.GetTimestamp();
        var succeeded = false;
        try
        {
            var response = await request.ConfigureAwait(false);
            succeeded = true;
            return response;
        }
        finally
        {
            var details = $"method={method} elapsed_ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} succeeded={succeeded}";
            _ = Task.Run(() => CrashLog.Note("CodexRequestTiming", details));
        }
    }
}
