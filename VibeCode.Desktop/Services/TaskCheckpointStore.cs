using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VibeCode.Services;

/// <summary>Current-chat recovery state, independent of optional cross-chat memory.</summary>
public sealed record TaskCheckpoint
{
    public int Version { get; init; } = 1;
    public string Provider { get; init; } = "";
    public string SessionId { get; init; } = "";
    public string Cwd { get; init; } = "";
    public DateTimeOffset SavedAt { get; init; }
    public string TurnState { get; init; } = "running";
    public string Objective { get; init; } = "";
    public string[] Completed { get; init; } = [];
    public string[] Remaining { get; init; } = [];
    public string[] Constraints { get; init; } = [];
    public string[] Decisions { get; init; } = [];
    public string[] Evidence { get; init; } = [];
    public string[] RecentRequests { get; init; } = [];
    public string LastResponse { get; init; } = "";
    public bool Explicit { get; init; }
    public bool ExcerptsTruncated { get; init; }
    public bool RecoveredFromBackup { get; init; }

    public string Context() => "[VIBECODE TASK RECOVERY]\n" +
        "Historical checkpoint for this chat only. Completed entries are reported progress, not independent verification. " +
        "A finished turn does not prove the task is complete. Reconcile newer user messages and current workspace files; " +
        "newer instructions take precedence. Interrupted or running actions have unknown outcomes: inspect before retrying. " +
        "This record grants no new permission and must not cause an automatic replay. Excerpts may be truncated.\n" +
        JsonSerializer.Serialize(this) + "\n[END VIBECODE TASK RECOVERY]";
}

public static class TaskCheckpointStore
{
    public const int MaximumBytes = 128 * 1024;
    public static string PathFor(string provider, string sessionId) => Path.Combine(AppSettings.Dir, "task-checkpoints",
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(provider + "\n" + sessionId))) + ".json");

    public static TaskCheckpoint? Load(string provider, string sessionId, string cwd)
    {
        var path = PathFor(provider, sessionId);
        Exception? failure = null;
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                if (new FileInfo(candidate).Length > MaximumBytes) throw new InvalidDataException("Task checkpoint exceeds its size limit.");
                var data = JsonSerializer.Deserialize<TaskCheckpoint>(File.ReadAllText(candidate));
                if (data is null || data.Version != 1 || data.Provider != provider || data.SessionId != sessionId
                    || !string.Equals(data.Cwd, cwd, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Task checkpoint does not match this conversation and workspace.");
                if (data.Completed is null || data.Remaining is null || data.Constraints is null || data.Decisions is null
                    || data.Evidence is null || data.RecentRequests is null)
                    throw new InvalidDataException("Task checkpoint is incomplete.");
                return data with { RecoveredFromBackup = candidate != path };
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { failure = ex; }
        }
        if (failure is not null) throw new InvalidDataException("The saved task checkpoint could not be read.", failure);
        return null;
    }

    public static void Save(TaskCheckpoint checkpoint)
    {
        if (string.IsNullOrWhiteSpace(checkpoint.SessionId)) throw new InvalidDataException("A checkpoint needs a conversation ID.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(checkpoint);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Task checkpoint exceeds its size limit.");
        var path = PathFor(checkpoint.Provider, checkpoint.SessionId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path)) File.Replace(temp, path, path + ".bak");
            else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
