using System.IO;
using System.Text.Json.Nodes;
using VibeCode.Services;

Environment.SetEnvironmentVariable("VIBECODE_DATA_DIR", Path.Combine(Path.GetTempPath(), "VibeCode-usage-check-" + Guid.NewGuid().ToString("N")));
var checks = 0;
void Check(string name, bool okay)
{
    if (!okay) throw new Exception("FAIL: " + name);
    checks++; Console.WriteLine("PASS: " + name);
}
JsonObject Usage() => new() { ["input_tokens"] = 80L, ["cache_read_input_tokens"] = 20, ["output_tokens"] = 5.0 };
using var recorder = new BridgeSuggestionUsage("codex", "gpt-6-luna", "usage-fixture");
var result = new JsonObject { ["usage"] = Usage() };
recorder.Record(result, "planner", []);
var entry = UsageLog.Instance.Entries().Single();
Check("integer and double token values preserve cache tiers", entry.Input == 80 && entry.CacheRead == 20 && entry.Output == 5 && entry.Total == 105);
recorder.Record(result, "planner", []);
Check("a repeated completion is recorded once", UsageLog.Instance.Count == 1);
Check("planning record includes project, model and session", entry.Project == "usage-fixture" && entry.Model == "gpt-6-luna" && entry.SessionId == "planner" && entry.CostUsd > 0);

using var kimi = new BridgeSuggestionUsage("kimi", "kimi-k2.5", "usage-fixture");
kimi.Record(new JsonObject { ["usage"] = new JsonObject(), ["session_usage"] = Usage() }, "kimi-planner", []);
Check("fresh planning sessions accept Kimi session usage", UsageLog.Instance.Entries().Last().Total == 105);

using var unknown = new BridgeSuggestionUsage("codex", "default", "usage-fixture");
unknown.Record(result, "unknown-planner", new JsonArray
{
    new JsonObject { ["value"] = "default", ["resolvedModel"] = "default" },
    new JsonObject { ["value"] = "gpt-6-astra", ["resolvedModel"] = "gpt-6-astra" },
});
Check("an unknown default is never attributed to an unrelated catalog model", UsageLog.Instance.Entries().Last().Model == "unknown");

using var failed = new BridgeSuggestionUsage("codex", "gpt-6-luna", "usage-fixture");
failed.Record(new JsonObject { ["is_error"] = true, ["usage"] = Usage() }, "failed-planner", []);
Check("failed suggestions retain reported usage", UsageLog.Instance.Count == 4);
using var empty = new BridgeSuggestionUsage("codex", "gpt-6-luna", "usage-fixture");
empty.Record(new JsonObject { ["usage"] = new JsonObject() }, "empty-planner", []);
Check("an empty usage result creates no fabricated tokens", UsageLog.Instance.Count == 4);

var entries = UsageLog.Instance.Entries();
var report = UsageAnalytics.BuildSince(entries, DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddMinutes(1));
Check("Usage dashboard total includes every recorded session", report.Total == 420 && report.Turns == 4);
var deadline = DateTime.UtcNow.AddSeconds(5);
int StoredRows()
{
    if (!File.Exists(UsageLog.FilePath)) return 0;
    using var stream = new FileStream(UsageLog.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    using var reader = new StreamReader(stream);
    return reader.ReadToEnd().Count(character => character == '\n');
}
while (DateTime.UtcNow < deadline && StoredRows() != 4) Thread.Sleep(20);
Check("all four turn records reach durable history", StoredRows() == 4);
Console.WriteLine($"PASS: {checks} targeted token accounting checks. No live model calls or protected source reads.");
