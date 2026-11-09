using System.Diagnostics;
using System.Text.Json.Nodes;

namespace VibeCode.Services;

/// <summary>Content-free timings: distinguish local prompt preparation from waiting for provider output.</summary>
internal sealed class PromptSendTiming(string provider)
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly string _id = Guid.NewGuid().ToString("N")[..8];
    private readonly List<string> _stages = [];
    private long? _dispatched;
    private bool _observed;

    internal void Stage(string name) => _stages.Add($"{name}_ms={_clock.ElapsedMilliseconds}");

    internal void Dispatched(int characters)
    {
        _dispatched = _clock.ElapsedMilliseconds;
        Write($"phase=dispatch preparation_ms={_dispatched} wire_characters={characters} " + string.Join(" ", _stages));
    }

    internal void Observe(JsonNode message)
    {
        if (_observed || _dispatched is not { } sent || message["is_replay"]?.GetValue<bool>() == true
            || message["subagent_thread_id"] is not null || message["parent_tool_use_id"] is not null) return;
        var type = message["type"]?.GetValue<string>();
        var ev = message["event"];
        var output = type == "assistant" && message["message"]?["content"] is JsonArray blocks
            && blocks.Any(block => block?["type"]?.GetValue<string>() == "tool_use"
                || (block?["type"]?.GetValue<string>() == "text" && !string.IsNullOrEmpty(block?["text"]?.GetValue<string>())));
        output |= type == "stream_event" &&
            ((ev?["type"]?.GetValue<string>() == "content_block_delta"
                && ev["delta"]?["type"]?.GetValue<string>() == "text_delta"
                && !string.IsNullOrEmpty(ev["delta"]?["text"]?.GetValue<string>()))
             || (ev?["type"]?.GetValue<string>() == "content_block_start"
                && ev["content_block"]?["type"]?.GetValue<string>() == "tool_use"));
        if (!output && type != "result") return;
        _observed = true;
        Write($"phase={(output ? "first_output" : "completed_without_output")} provider_wait_ms={_clock.ElapsedMilliseconds - sent} total_ms={_clock.ElapsedMilliseconds}");
    }

    private void Write(string details)
    {
        var entry = $"id={_id} provider={provider} {details}";
        // Logging must not add disk I/O to the UI's send/stream path. No message text, path or credentials.
        _ = Task.Run(() => CrashLog.Note("PromptSendTiming", entry));
    }
}
