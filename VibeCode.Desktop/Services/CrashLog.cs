using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;

namespace VibeCode.Services;

/// <summary>
/// Append-only crash log next to chats/settings (%APPDATA%\VibeCode\crash-log.txt).
/// Safe to call from any thread, including the middle of a fatal crash — never throws.
/// It also records plain lifecycle events (start, exit, an exit nobody asked for): the log used to hold
/// exceptions ONLY, so an app that closed WITHOUT throwing - an orderly shutdown, or the process being killed
/// from outside - left no trace anywhere and was indistinguishable from "it vanished for no reason".
/// </summary>
public static class CrashLog
{
    private const long MaxLogBytes = 1024 * 1024;

    public static string LogPath => Path.Combine(AppSettings.Dir, "crash-log.txt");
    public static string LatestPath => Path.Combine(AppSettings.Dir, "crash-latest.txt");
    private static string RunsDir => Path.Combine(AppSettings.Dir, "runs");

    /// <summary>Write a full exception dump. <paramref name="source"/> tags the handler (UI / domain / task).</summary>
    public static string? Write(Exception? exception, string source, bool isTerminating = false)
    {
        try
        {
            var body = Format(exception, source, isTerminating);
            // Append full history; overwrite "latest" so the user always has one file to open first.
            Append(body);
            File.WriteAllText(LatestPath, body, Encoding.UTF8);
            return LogPath;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Record a non-exception lifecycle event. Deliberately kept out of crash-latest.txt, which stays a
    /// "the last thing that actually threw" file.</summary>
    public static void Note(string source, string details)
    {
        try
        {
            var sb = new StringBuilder(512);
            sb.AppendLine("================================================================================");
            sb.AppendLine($"VibeCode event  {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff zzz}");
            sb.AppendLine($"Source:         {source}");
            sb.AppendLine($"Process:        {Environment.ProcessId}");
            sb.AppendLine("--------------------------------------------------------------------------------");
            sb.AppendLine(details);
            sb.AppendLine();
            Append(sb.ToString());
        }
        catch { /* diagnostics must never take the app down */ }
    }

    /// <summary>Claim a marker file for this run, and report markers left behind by runs that never reached an exit
    /// handler — i.e. that were killed from outside (Task Manager, Stop-Process/taskkill) or died hard. Those exits
    /// raise no exception and Windows Error Reporting records nothing either, so a leftover marker is the only
    /// evidence they ever happened.</summary>
    public static IReadOnlyList<string> ClaimRunMarker()
    {
        var abandoned = new List<string>();
        try
        {
            Directory.CreateDirectory(RunsDir);
            foreach (var file in Directory.GetFiles(RunsDir, "run-*.txt"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (!name.StartsWith("run-", StringComparison.Ordinal) ||
                    !int.TryParse(name.AsSpan(4), out var pid)) continue;
                if (pid == Environment.ProcessId || IsRunning(pid)) continue;   // a live peer instance is not a corpse
                try { abandoned.Add(File.ReadAllText(file).Trim()); }
                catch { abandoned.Add($"pid {pid} (marker unreadable)"); }
                try { File.Delete(file); } catch { /* a later start will collect it */ }
            }

            File.WriteAllText(MarkerPath(Environment.ProcessId),
                $"pid {Environment.ProcessId}{Environment.NewLine}" +
                $"started {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff zzz}{Environment.NewLine}" +
                $"exe {Environment.ProcessPath ?? "(unknown)"}", Encoding.UTF8);
        }
        catch { /* diagnostics only - never block startup */ }
        return abandoned;
    }

    /// <summary>Drop this run's marker. Reaching here means the process left through a real exit path, so the next
    /// startup must not accuse it of having been killed.</summary>
    public static void ReleaseRunMarker()
    {
        try { File.Delete(MarkerPath(Environment.ProcessId)); }
        catch { /* nothing worth doing on the way out */ }
    }

    private static string MarkerPath(int pid) => Path.Combine(RunsDir, $"run-{pid}.txt");

    private static bool IsRunning(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch { return false; }
    }

    /// <summary>Append to the history file, rolling it over once it grows past <see cref="MaxLogBytes"/> so that a
    /// long-lived install neither grows an unbounded log nor buries the recent entries that matter.</summary>
    private static void Append(string body)
    {
        Directory.CreateDirectory(AppSettings.Dir);
        try
        {
            if (new FileInfo(LogPath) is { Exists: true, Length: > MaxLogBytes })
                File.Move(LogPath, LogPath + ".1", overwrite: true);
        }
        catch { /* rotation is a nicety - it must never cost us the entry itself */ }
        File.AppendAllText(LogPath, body, Encoding.UTF8);
    }

    public static string Format(Exception? exception, string source, bool isTerminating = false)
    {
        var sb = new StringBuilder(2048);
        sb.AppendLine("================================================================================");
        sb.AppendLine($"VibeCode crash  {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff zzz}");
        sb.AppendLine($"Source:         {source}");
        sb.AppendLine($"Terminating:    {isTerminating}");
        try
        {
            var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            var ver = asm.GetName().Version?.ToString() ?? "?";
            sb.AppendLine($"Version:        {ver}");
            sb.AppendLine($"Process:        {Environment.ProcessPath ?? "(unknown)"}");
        }
        catch { /* metadata is optional */ }
        sb.AppendLine($"OS:             {Environment.OSVersion}");
        sb.AppendLine($"64-bit:         {Environment.Is64BitProcess}");
        sb.AppendLine($"CLR:            {Environment.Version}");
        sb.AppendLine($"Data dir:       {AppSettings.Dir}");
        sb.AppendLine("--------------------------------------------------------------------------------");

        if (exception is null)
        {
            sb.AppendLine("(no exception object)");
        }
        else
        {
            var depth = 0;
            for (var ex = exception; ex is not null; ex = ex.InnerException, depth++)
            {
                if (depth > 0) sb.AppendLine($"--- inner #{depth} ---");
                sb.AppendLine($"{ex.GetType().FullName}: {ex.Message}");
                if (!string.IsNullOrWhiteSpace(ex.StackTrace))
                    sb.AppendLine(ex.StackTrace);
                if (ex is AggregateException agg)
                {
                    var i = 0;
                    foreach (var inner in agg.InnerExceptions)
                    {
                        sb.AppendLine($"--- aggregate[{i++}] ---");
                        sb.AppendLine($"{inner.GetType().FullName}: {inner.Message}");
                        if (!string.IsNullOrWhiteSpace(inner.StackTrace))
                            sb.AppendLine(inner.StackTrace);
                    }
                }
            }
        }

        sb.AppendLine();
        return sb.ToString();
    }
}
