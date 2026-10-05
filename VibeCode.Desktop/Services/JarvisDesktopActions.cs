using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace VibeCode.Services;

public interface IJarvisDesktopActions
{
    Task<JarvisActionReceipt> ExecuteAsync(JarvisAction action, CancellationToken cancellationToken);
}

public static class JarvisDesktopPolicy
{
    public static bool IsDesktopAction(string kind) => kind is "list_apps" or "open_app" or "focus_app"
        or "close_app" or "open_path" or "open_url" or "search_web" or "system_info";

    public static void Validate(string kind, string target)
    {
        if (!IsDesktopAction(kind)) throw new ArgumentException("Unsupported desktop action.");
        if (target.Length > 2048 || target.Any(char.IsControl)) throw new ArgumentException("Invalid desktop target.");
        if (kind is not ("list_apps" or "system_info") && string.IsNullOrWhiteSpace(target))
            throw new ArgumentException("Specify the app, window, folder, file, website, or search you want.");
        if (kind == "system_info" && target.Length > 0) throw new ArgumentException("System information takes no target.");
        if (kind == "open_url") _ = WebAddress(target);
        if (kind == "open_path") _ = JarvisPathPolicy.Normalize(target);
    }

    public static Uri WebAddress(string target) => Uri.TryCreate(target, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo)
            ? uri : throw new ArgumentException("Use a full http or https website address.");

    public static string WebSearchAddress(string query) => "https://www.google.com/search?q=" + Uri.EscapeDataString(query);
}

/// <summary>Windows actions independent of coding sessions. Never runs generated commands or kills processes.</summary>
public sealed class JarvisDesktopActions : IJarvisDesktopActions
{
    internal sealed record AppWindow(nint Handle, int ProcessId, string Name, string Title);
    private sealed record InstalledApp(string Name, string Path);

    public Task<JarvisActionReceipt> ExecuteAsync(JarvisAction action, CancellationToken cancellationToken) => Task.Run(() =>
    {
        JarvisDesktopPolicy.Validate(action.Kind, action.Target);
        cancellationToken.ThrowIfCancellationRequested();
        var target = action.Target.Trim();
        string result;
        switch (action.Kind)
        {
            case "list_apps":
            {
                var windows = MatchingWindows(target);
                result = windows.Count == 0 ? "No apps with visible windows match that request. Background processes aren't included."
                    : $"Apps with open windows ({windows.Select(w => w.ProcessId).Distinct().Count()} running processes):\n"
                      + string.Join("\n", windows.Take(60).Select(w => $"- {w.Name}: {w.Title}"))
                      + (windows.Count > 60 ? $"\nShowing 60 of {windows.Count} windows. Ask for an app name to narrow the list." : "")
                      + "\nThis lists visible app windows, not background services.";
                break;
            }
            case "focus_app":
            case "close_app":
            {
                var windows = MatchingWindows(target);
                if (windows.Count != 1) { result = Choice(windows, target); break; }
                var window = windows[0];
                cancellationToken.ThrowIfCancellationRequested();
                if (action.Kind == "close_app")
                {
                    if (window.ProcessId == Environment.ProcessId)
                        throw new InvalidOperationException("Use the window's Close button to close Jarvis or VibeCode itself.");
                    if (!PostMessage(window.Handle, 0x0010, 0, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    result = $"Asked {window.Name} to close \"{window.Title}\". If it has unsaved work, its save prompt stays in control.";
                }
                else
                {
                    if (IsIconic(window.Handle)) ShowWindowAsync(window.Handle, 9);
                    if (!SetForegroundWindow(window.Handle) && GetForegroundWindow() != window.Handle)
                        throw new InvalidOperationException($"Windows did not allow {window.Name} to take focus. Select its taskbar icon.");
                    result = $"Switched to {window.Name}: {window.Title}.";
                }
                break;
            }
            case "open_app":
            {
                var apps = InstalledApps();
                var exact = apps.Where(a => string.Equals(a.Name, target, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(System.IO.Path.GetFileNameWithoutExtension(a.Path), target, StringComparison.OrdinalIgnoreCase)).ToArray();
                var matches = exact.Length > 0 ? exact : apps.Where(a => a.Name.Contains(target, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length == 0) { result = $"I couldn't find an installed app named \"{target}\" in the Start menu or registered app paths. Tell me its Start menu name."; break; }
                if (matches.Length > 1) { result = "Which app do you mean? " + string.Join(", ", matches.Take(12).Select(a => a.Name)); break; }
                cancellationToken.ThrowIfCancellationRequested();
                Launch(matches[0].Path);
                result = $"Launched {matches[0].Name}.";
                break;
            }
            case "open_path":
            {
                var path = JarvisPathPolicy.Normalize(target);
                if (!Directory.Exists(path) && !File.Exists(path)) throw new FileNotFoundException("That file or folder doesn't exist: " + path);
                cancellationToken.ThrowIfCancellationRequested();
                // Opening a script should reveal it, not silently execute its associated command.
                if (File.Exists(path) && new[] { ".exe", ".com", ".bat", ".cmd", ".ps1", ".vbs", ".js", ".py", ".hta", ".reg", ".msi", ".lnk", ".scr" }
                    .Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                {
                    var info = new ProcessStartInfo(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = false };
                    info.ArgumentList.Add("/select,"); info.ArgumentList.Add(path);
                    using var launched = Process.Start(info);
                    result = $"Opened the file's location in Explorer: {path}.";
                }
                else { Launch(path); result = $"Opened {path}."; }
                break;
            }
            case "open_url":
                Launch(JarvisDesktopPolicy.WebAddress(target).AbsoluteUri);
                result = $"Opened {target} in your browser.";
                break;
            case "search_web":
                Launch(JarvisDesktopPolicy.WebSearchAddress(target));
                result = $"Opened a web search for \"{target}\" in your browser.";
                break;
            case "system_info":
                var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
                result = $"{Environment.MachineName}\n{RuntimeInformation.OSDescription}\n{Environment.ProcessorCount} logical processors\nUptime: {uptime.Days} days, {uptime.Hours} hours, {uptime.Minutes} minutes.";
                break;
            default: throw new InvalidOperationException("Unsupported desktop action.");
        }
        return new JarvisActionReceipt(action.Kind, target, result);
    }, cancellationToken);

    private static void Launch(string path)
    {
        using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    internal static List<AppWindow> MatchingWindows(string query)
    {
        var windows = new List<AppWindow>();
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle) || GetWindowTextLength(handle) == 0) return true;
            var title = new StringBuilder(512);
            GetWindowText(handle, title, title.Capacity);
            GetWindowThreadProcessId(handle, out var pid);
            try
            {
                using var process = Process.GetProcessById((int)pid);
                windows.Add(new(handle, (int)pid, process.ProcessName, title.ToString()));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { }
            return true;
        }, 0);
        var exact = windows.Where(w => string.Equals(w.Title, query, StringComparison.OrdinalIgnoreCase)).ToList();
        return (exact.Count > 0 ? exact : windows.Where(w => query.Length == 0
            || w.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || w.Title.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList())
            .OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase).ThenBy(w => w.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string Choice(List<AppWindow> windows, string target) => windows.Count == 0
        ? $"No open window matches \"{target}\". Ask me to list running apps or open it."
        : "More than one window matches. Tell me its window title:\n" + string.Join("\n", windows.Take(20).Select(w => $"- {w.Name}: {w.Title}"));

    private static List<InstalledApp> InstalledApps()
    {
        var apps = new List<InstalledApp>();
        foreach (var root in new[] { Environment.SpecialFolder.StartMenu, Environment.SpecialFolder.CommonStartMenu })
        {
            var directory = Environment.GetFolderPath(root);
            if (!Directory.Exists(directory)) continue;
            foreach (var file in Directory.EnumerateFiles(directory, "*.lnk", new EnumerationOptions
                { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                apps.Add(new(System.IO.Path.GetFileNameWithoutExtension(file), file));
        }
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths");
            if (key is null) continue;
            foreach (var name in key.GetSubKeyNames())
            {
                using var app = key.OpenSubKey(name);
                if (app?.GetValue("") is string executable && File.Exists(executable.Trim('"')))
                    apps.Add(new(System.IO.Path.GetFileNameWithoutExtension(name), executable.Trim('"')));
            }
        }
        return apps.DistinctBy(app => app.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private delegate bool EnumWindowCallback(nint hwnd, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hwnd, StringBuilder text, int maximum);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint hwnd, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(nint hwnd, uint message, nint wparam, nint lparam);
}
