using System.IO;
using System.Text.Json.Nodes;

namespace VibeCode.Services;

internal sealed record GeminiTokenRecord(string Model, long Input, long Cached, long Output, long Thoughts);

/// <summary>Gemini's native message records include cache hits and thinking, unlike ACP's quota.token_count.</summary>
internal static class GeminiSessionUsage
{
    private static readonly object IndexGate = new();
    internal static string IndexPath => Path.Combine(AppSettings.Dir, "gemini", "sessions.json");

    public static void RememberSession(string sessionId, string cwd)
    {
        lock (IndexGate)
        {
            try
            {
                var index = File.Exists(IndexPath) ? JsonNode.Parse(File.ReadAllText(IndexPath))?.AsObject() ?? new() : new JsonObject();
                index[sessionId] = new JsonObject { ["cwd"] = cwd, ["updated"] = DateTimeOffset.UtcNow.ToString("O") };
                Directory.CreateDirectory(Path.GetDirectoryName(IndexPath)!);
                File.WriteAllText(IndexPath + ".tmp", index.ToJsonString());
                File.Move(IndexPath + ".tmp", IndexPath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        }
    }

    public static IReadOnlyDictionary<string, GeminiTokenRecord>? Read(string? sessionId)
    {
        if (sessionId is null) return null;
        var root = Path.Combine(GeminiRuntime.ConfigDirectory, "tmp");
        if (!Directory.Exists(root)) return null;
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, $"session-*{sessionId[..Math.Min(8, sessionId.Length)]}*.json*", SearchOption.AllDirectories))
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                var parsed = Parse(reader.ReadToEnd(), sessionId);
                if (parsed is not null) return parsed;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }

    internal static IReadOnlyDictionary<string, GeminiTokenRecord>? Parse(string contents, string sessionId)
    {
        var records = new List<JsonObject>();
        // Older CLIs use a JSON object; current versions append message upserts as JSONL.
        try
        {
            if (JsonNode.Parse(contents) is JsonObject document && document["messages"] is JsonArray messages)
            {
                if (document["sessionId"]?.ToString() != sessionId) return null;
                records.AddRange(messages.OfType<JsonObject>());
            }
        }
        catch (System.Text.Json.JsonException) { }
        if (records.Count == 0)
        {
            var identified = false;
            foreach (var line in contents.Split('\n'))
            {
                try
                {
                    if (JsonNode.Parse(line) is not JsonObject record) continue;
                    if (record["sessionId"] is { } id)
                    {
                        if (id.ToString() != sessionId) return null;
                        identified = true;
                    }
                    if (record["id"] is not null) records.Add(record);
                }
                catch (System.Text.Json.JsonException) { /* the CLI may still be appending the last record */ }
            }
            if (!identified) return null;
        }
        var result = new Dictionary<string, GeminiTokenRecord>();
        foreach (var record in records)
        {
            if (record["type"]?.ToString() != "gemini" || record["tokens"] is not JsonObject tokens || record["id"]?.ToString() is not { } id) continue;
            result[id] = new(record["model"]?.ToString() ?? "Gemini", Number(tokens["input"]), Number(tokens["cached"]), Number(tokens["output"]), Number(tokens["thoughts"]));
        }
        return result;
    }

    internal static JsonObject TurnReport(IReadOnlyDictionary<string, GeminiTokenRecord>? before,
        IReadOnlyDictionary<string, GeminiTokenRecord>? after, JsonNode? response)
    {
        var totals = new Dictionary<string, GeminiTokenRecord>();
        var recorded = false;
        if (after is not null)
        {
            foreach (var (id, value) in after)
            {
                var old = before?.GetValueOrDefault(id);
                var prompt = Math.Max(0, value.Input - (old?.Input ?? 0));
                var read = Math.Min(prompt, Math.Max(0, value.Cached - (old?.Cached ?? 0)));
                var generated = Math.Max(0, value.Output - (old?.Output ?? 0));
                var thoughts = Math.Max(0, value.Thoughts - (old?.Thoughts ?? 0));
                if (prompt + generated + thoughts == 0) continue;
                recorded = true;
                var total = totals.GetValueOrDefault(value.Model) ?? new(value.Model, 0, 0, 0, 0);
                totals[value.Model] = total with
                {
                    Input = total.Input + prompt,
                    Cached = total.Cached + read,
                    Output = total.Output + generated,
                    Thoughts = total.Thoughts + thoughts
                };
            }
        }
        // A transcript may be only partly flushed when ACP finishes. Keep every token ACP reports,
        // retaining the cache/thinking split for records already written. Missing splits stay explicit.
        var supplemented = false;
        var quota = response?["_meta"]?["quota"];
        foreach (var entry in (quota?["model_usage"] as JsonArray ?? new()).OfType<JsonObject>())
        {
            if (entry["model"]?.ToString() is not { Length: > 0 } model || entry["token_count"] is not JsonObject tokens) continue;
            var total = totals.GetValueOrDefault(model) ?? new(model, 0, 0, 0, 0);
            var missingInput = Math.Max(0, Number(tokens["input_tokens"]) - total.Input);
            var missingOutput = Math.Max(0, Number(tokens["output_tokens"]) - total.Output);
            supplemented |= missingInput + missingOutput > 0;
            totals[model] = total with { Input = total.Input + missingInput, Output = total.Output + missingOutput };
        }
        if (quota?["token_count"] is JsonObject fallback)
        {
            var missingInput = Math.Max(0, Number(fallback["input_tokens"]) - totals.Values.Sum(v => v.Input));
            var missingOutput = Math.Max(0, Number(fallback["output_tokens"]) - totals.Values.Sum(v => v.Output));
            if (missingInput + missingOutput > 0)
            {
                supplemented = true;
                const string unknown = "Gemini (unattributed)";
                totals[unknown] = new(unknown, missingInput, 0, missingOutput, 0);
            }
        }
        var models = new JsonObject();
        foreach (var (model, total) in totals)
            models[model] = new JsonObject
            {
                ["inputTokens"] = total.Input - total.Cached,
                ["cacheReadInputTokens"] = total.Cached,
                ["outputTokens"] = total.Output + total.Thoughts,
                ["costUSD"] = 0
            };
        var input = totals.Values.Sum(v => v.Input - v.Cached);
        var cached = totals.Values.Sum(v => v.Cached);
        var output = totals.Values.Sum(v => v.Output + v.Thoughts);
        return new JsonObject
        {
            ["usage"] = new JsonObject { ["input_tokens"] = input, ["cache_read_input_tokens"] = cached, ["output_tokens"] = output },
            ["modelUsage"] = models,
            ["total_cost_usd"] = 0,
            ["usage_is_incomplete"] = supplemented || !recorded && input + cached + output > 0,
        };
    }

    internal static JsonObject? ContextReport(IReadOnlyDictionary<string, GeminiTokenRecord>? records)
    {
        if (records?.Values.LastOrDefault() is not { } latest) return null;
        return new JsonObject
        {
            ["type"] = "system",
            ["subtype"] = "usage_update",
            ["usage"] = new JsonObject { ["context_input_tokens"] = latest.Input, ["context_window"] = 1_048_576 },
        };
    }

    private static long Number(JsonNode? value) => long.TryParse(value?.ToString(), out var n) ? Math.Max(0, n) : 0;
}
