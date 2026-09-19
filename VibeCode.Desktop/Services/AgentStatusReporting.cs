using System.IO;
using System.Text;
using System.Text.Json;
using VibeCode.Protocol;

namespace VibeCode.Services;

/// <summary>Registers reporting for isolated runtimes independently of the monitor window.</summary>
internal static class AgentStatusReporting
{
    private static readonly object Gate = new();
    private static readonly HashSet<string> Homes = new(StringComparer.OrdinalIgnoreCase);
    private static bool _enabled = AppSettings.Current.MitreMonitorEnabled;
    // Retain the markers so existing dashboard-scoped registrations are replaced on the next launch.
    private const string Begin = "# BEGIN VIBECODE DASHBOARD REPORTING";
    private const string End = "# END VIBECODE DASHBOARD REPORTING";
    public static event Action? Changed;

    static AgentStatusReporting() => AppSettings.Changed += OnSettingsChanged;

    private static void OnSettingsChanged()
    {
        lock (Gate)
        {
            if (_enabled == AppSettings.Current.MitreMonitorEnabled) return;
            _enabled = AppSettings.Current.MitreMonitorEnabled;
            UpdateHomes();
        }
        Changed?.Invoke();
    }

    public static void RegisterHome(string home)
    {
        home = Path.GetFullPath(home);
        if (string.Equals(home.TrimEnd('\\'), Path.GetFullPath(CodexEnvironment.StandaloneHomeDirectory).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Agent status reporting requires VibeCode's isolated Codex home.");
        lock (Gate) { Homes.Add(home); WriteRegistration(home); }
    }

    private static void UpdateHomes()
    {
        foreach (var home in Homes)
        {
            try { WriteRegistration(home); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { CrashLog.Note("AgentStatusReportingConfig", ex.GetType().Name); }
        }
    }

    private static void WriteRegistration(string home)
    {
        var path = Path.Combine(home, "config.toml");
        Directory.CreateDirectory(home);
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The isolated Codex configuration must be a regular file.");
        var original = File.Exists(path) ? File.ReadAllText(path) : "";
        var content = original;
        var start = content.IndexOf(Begin, StringComparison.Ordinal);
        if (start >= 0)
        {
            var end = content.IndexOf(End, start, StringComparison.Ordinal);
            if (end < 0) throw new IOException("The managed reporting section is incomplete.");
            content = content.Remove(start, end + End.Length - start).TrimEnd();
        }
        if (_enabled)
        {
            var server = AgentStatusMcpRegistration.Create();
            content = content.TrimEnd() + "\n\n" + Begin + "\n[mcp_servers." + server.RuntimeName + "]\n"
                + "command = " + JsonSerializer.Serialize(server.Command) + "\n"
                + "args = " + JsonSerializer.Serialize(server.Arguments) + "\n"
                + "startup_timeout_sec = 5\ntool_timeout_sec = 10\n"
                + "tools.report_status.approval_mode = \"approve\"\n"
                + "tools.list_stages.approval_mode = \"approve\"\n" + End + "\n";
        }
        if (content == original) return;
        var temp = path + ".reporting-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(Encoding.UTF8.GetBytes(content));
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temp, path, null, ignoreMetadataErrors: true);
            else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
