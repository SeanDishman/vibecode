using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using VibeCode.Services;

namespace VibeCode.Protocol;

/// <summary>
/// Owns the Codex runtime boundary for VibeCode. Authentication and thread history live under
/// %APPDATA%\VibeCode\codex instead of the user's standalone ~/.codex home, while non-secret
/// Codex configuration and extension folders are mirrored/linked so VibeCode still behaves like
/// the user's normal Codex setup.
/// </summary>
public static class CodexEnvironment
{
    public const string BrowserBuildFlavor = "vibecode";
    /// <summary>
    /// Backends the Browser plugin may bind to. VibeCode used to pin this to "iab", which removed the user's
    /// real Chrome as an option and left a failed in-app discovery with no fallback at all. Chrome is only
    /// actually reachable when its native host is installed, so listing it is additive.
    /// </summary>
    private const string BrowserBackends = "chrome,iab";
    private static string BrowserAppVersion =>
        typeof(CodexEnvironment).Assembly.GetName().Version?.ToString() ?? BrowserBuildFlavor;
    private static readonly object PrepareLock = new();
    private static readonly HashSet<string> PreparedHomes = new(StringComparer.OrdinalIgnoreCase);

    public static string HomeDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VibeCode", "codex");

    public static string StandaloneHomeDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

    /// <summary>Prepare VibeCode's original isolated home without ever copying standalone Codex credentials.</summary>
    public static void Prepare() => Prepare(HomeDirectory);

    /// <summary>
    /// Prepare one account-specific Codex home. Every saved OpenAI account gets its own home so its managed OAuth
    /// refresh token and resumable thread store can never overwrite another account's. Only non-secret configuration
    /// is mirrored from the standalone Codex install.
    /// </summary>
    public static void Prepare(string homeDirectory)
    {
        homeDirectory = Path.GetFullPath(homeDirectory);
        lock (PrepareLock)
        {
            if (!PreparedHomes.Add(homeDirectory)) return;
            Directory.CreateDirectory(homeDirectory);

            // Keep normal Codex behavior (models, MCP servers, hooks, feature flags and global guidance), but never
            // mirror auth.json or runtime/session databases. The CLI argument added below always forces auth storage
            // to this account's CODEX_HOME even if the mirrored config prefers the Windows credential store.
            MirrorFile(homeDirectory, "config.toml");
            NormalizeIsolatedConfig(homeDirectory);
            MirrorFile(homeDirectory, "AGENTS.md");

            // Skills/plugins/rules can be large and change independently. Directory links keep them current without
            // duplicating packages. If Windows policy disallows links, Codex still starts with project-local guidance.
            LinkDirectory(homeDirectory, "skills");
            LinkDirectory(homeDirectory, "plugins");
            LinkDirectory(homeDirectory, "rules");
        }
    }

    /// <summary>
    /// Import only thread rollouts that VibeCode itself recorded before auth/home isolation was introduced.
    /// This preserves existing VibeCode Codex chats without importing unrelated Codex history or credentials.
    /// </summary>
    public static void ImportOwnedSessions(IEnumerable<string?> sessionIds)
    {
        Prepare();
        var ids = sessionIds.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).Distinct().ToArray();
        if (ids.Length == 0 || PathsEqual(HomeDirectory, StandaloneHomeDirectory)) return;

        foreach (var folderName in new[] { "sessions", "archived_sessions" })
        {
            var sourceRoot = Path.Combine(StandaloneHomeDirectory, folderName);
            if (!Directory.Exists(sourceRoot)) continue;
            foreach (var id in ids)
            {
                try
                {
                    foreach (var source in Directory.EnumerateFiles(sourceRoot, $"*{id}*.jsonl", SearchOption.AllDirectories))
                    {
                        var relative = Path.GetRelativePath(sourceRoot, source);
                        var destination = Path.Combine(HomeDirectory, folderName, relative);
                        if (File.Exists(destination)) continue;
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        File.Copy(source, destination, overwrite: false);
                    }
                }
                catch { /* a live rollout can be briefly locked; a later launch can retry */ }
            }
        }

        // Recent Codex builds use this lightweight index to find rollouts quickly. Copy only rows for VibeCode's IDs.
        try
        {
            var sourceIndex = Path.Combine(StandaloneHomeDirectory, "session_index.jsonl");
            var targetIndex = Path.Combine(HomeDirectory, "session_index.jsonl");
            if (!File.Exists(sourceIndex)) return;
            var existing = File.Exists(targetIndex) ? File.ReadAllText(targetIndex) : "";
            var rows = File.ReadLines(sourceIndex)
                .Where(line => ids.Any(id => line.Contains(id, StringComparison.OrdinalIgnoreCase)))
                .Where(line => !existing.Contains(line, StringComparison.Ordinal))
                .ToArray();
            if (rows.Length > 0) File.AppendAllLines(targetIndex, rows, new UTF8Encoding(false));
        }
        catch { /* index is an optimization; app-server can still discover rollout files */ }
    }

    /// <summary>Create an app-server process that is branded and isolated for VibeCode.</summary>
    public static ProcessStartInfo CreateAppServerStartInfo(string? workingDirectory = null, string? homeDirectory = null,
        bool? swarmsEnabled = null, int swarmMaxWorkers = SwarmPolicy.DefaultMaxWorkers,
        IReadOnlyList<McpServerDefinition>? mcpServers = null)
    {
        var targetHome = string.IsNullOrWhiteSpace(homeDirectory) ? HomeDirectory : Path.GetFullPath(homeDirectory);
        Prepare(targetHome);
        var psi = new ProcessStartInfo
        {
            FileName = CodexSession.ResolveCliPath(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        if (!string.IsNullOrWhiteSpace(workingDirectory)) psi.WorkingDirectory = workingDirectory;
        psi.Environment["CODEX_HOME"] = targetHome;
        // The Browser plugin filters in-app backends by both Codex session and host build flavor. Tag every managed
        // app-server so it selects VibeCode's own WebView2 bridge instead of discarding it as a foreign desktop host.
        psi.Environment["BROWSER_USE_CODEX_APP_BUILD_FLAVOR"] = BrowserBuildFlavor;
        psi.Environment["BROWSER_USE_AVAILABLE_BACKENDS"] = BrowserBackends;
        psi.Environment["BROWSER_USE_CODEX_APP_VERSION"] = BrowserAppVersion;
        psi.Environment["BROWSER_USE_FULL_CDP_ACCESS_ENABLED"] = "1";
        // Only set when the selected ChatGPT account is an API-key account. The Codex CLI treats a present
        // OPENAI_API_KEY as outranking an OAuth session, so setting it unconditionally would silently move
        // a subscription user onto per-token billing.
        VibeCode.Services.ApiKeyAccountService.Instance.ApplyTo(psi, "codex");
        psi.ArgumentList.Add("app-server");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("cli_auth_credentials_store=\"file\"");
        // VibeCode supplies the approval UI and forwards each thread/turn's read-only, workspace-write, or
        // full-access policy through app-server. Do not inherit `windows.sandbox = "elevated"` from a mirrored
        // standalone config: that backend performs administrator-approved setup and can raise a UAC prompt for
        // every isolated VibeCode account home. The unelevated backend keeps Codex's Windows containment in place
        // using a restricted token/ACLs, while leaving VibeCode's existing approval boundary authoritative.
        AddConfigOverride(psi, "windows.sandbox=\"unelevated\"");
        // Plugin-managed MCP env tables override the app-server process environment. Pin Browser's own Node helper
        // too, otherwise a mirrored standalone value such as "prod" makes it discard VibeCode's valid backend.
        AddConfigOverride(psi,
            "mcp_servers.node_repl.env.BROWSER_USE_AVAILABLE_BACKENDS=" + TomlString(BrowserBackends));
        // Only consulted when the plugin is running as "gaas-browser-environment"; on a desktop install it is
        // ignored. Verified end to end: with this set, raw CDP was still refused with "Full CDP access is
        // disabled in browser config". The real desktop switch is full_cdp_access_enabled in
        // <CODEX_HOME>/browser/config.toml, which is the user's own consent record - VibeCode deliberately does
        // not write it on their behalf.
        AddConfigOverride(psi,
            "mcp_servers.node_repl.env.BROWSER_USE_FULL_CDP_ACCESS_ENABLED=" + TomlString("1"));
        AddConfigOverride(psi,
            "mcp_servers.node_repl.env.BROWSER_USE_CODEX_APP_BUILD_FLAVOR=" + TomlString(BrowserBuildFlavor));
        AddConfigOverride(psi,
            "mcp_servers.node_repl.env.BROWSER_USE_CODEX_APP_VERSION=" + TomlString(BrowserAppVersion));
        if (swarmsEnabled is not null)
        {
            // Pin the stable tool family so an experimental user-level feature cannot silently change the protocol
            // shape under the UI. max_depth=1 prevents nested NÃ—N fan-out; max_threads is the runtime backstop even
            // if a model disregards VibeCode's per-turn instruction.
            AddConfigOverride(psi, $"features.multi_agent={swarmsEnabled.Value.ToString().ToLowerInvariant()}");
            AddConfigOverride(psi, "features.multi_agent_v2=false");
            if (swarmsEnabled.Value)
            {
                AddConfigOverride(psi, $"agents.max_threads={SwarmPolicy.ClampMaxWorkers(swarmMaxWorkers)}");
                AddConfigOverride(psi, "agents.max_depth=1");
            }
        }
        McpCatalog.EnsureLaunchReady(mcpServers, "codex");
        var mcp = McpCatalog.BuildCodexProjection(mcpServers);
        var projectedArgumentLength = mcp.ConfigOverrides.Sum(value => value.Length + 3);
        if (projectedArgumentLength > McpCatalog.MaxCodexProjectionCharacters)
            throw new InvalidOperationException(
                $"The selected Codex MCP configuration is too large to launch safely ({projectedArgumentLength:N0} characters). Reduce command arguments, URLs, or the number of definitions below {McpCatalog.MaxCodexProjectionCharacters:N0} characters.");
        var projectedEnvironmentLength = mcp.Environment.Sum(pair => pair.Key.Length + pair.Value.Length + 2);
        if (projectedEnvironmentLength > McpCatalog.MaxCodexProjectionEnvironmentCharacters)
            throw new InvalidOperationException(
                $"The selected Codex MCP environment is too large to launch safely ({projectedEnvironmentLength:N0} characters). Reduce environment values or headers below {McpCatalog.MaxCodexProjectionEnvironmentCharacters:N0} characters.");
        foreach (var pair in mcp.Environment) psi.Environment[pair.Key] = pair.Value;
        foreach (var value in mcp.ConfigOverrides) AddConfigOverride(psi, value);
        return psi;
    }

    private static void AddConfigOverride(ProcessStartInfo psi, string value)
    {
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(value);
    }

    /// <summary>
    /// Verify the exact native runtime VibeCode is about to trust with automatic approvals. WinVerifyTrust checks the
    /// embedded Authenticode signature and certificate chain; the publisher check prevents an unrelated, validly
    /// signed executable named codex.exe from inheriting Auto mode's approval privileges.
    /// </summary>
    internal static RuntimeIntegrityResult VerifyRuntimeIntegrity(string path)
    {
        if (!OperatingSystem.IsWindows())
            return new RuntimeIntegrityResult(false, "Authenticode verification is only available on Windows.", null);

        try
        {
            path = Path.GetFullPath(path);
            if (!File.Exists(path)) return new RuntimeIntegrityResult(false, "The Codex runtime file does not exist.", null);

            var filePath = Marshal.StringToCoTaskMemUni(path);
            var fileInfoPtr = IntPtr.Zero;
            var trustDataPtr = IntPtr.Zero;
            var actionPtr = IntPtr.Zero;
            try
            {
                var fileInfo = new WinTrustFileInfo
                {
                    Size = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                    FilePath = filePath,
                };
                fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
                Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);

                var trustData = new WinTrustData
                {
                    Size = (uint)Marshal.SizeOf<WinTrustData>(),
                    UiChoice = 2,             // WTD_UI_NONE
                    RevocationChecks = 0,     // WTD_REVOKE_NONE
                    UnionChoice = 1,          // WTD_CHOICE_FILE
                    File = fileInfoPtr,
                    ProviderFlags = 0x1010,   // WTD_REVOCATION_CHECK_NONE | WTD_CACHE_ONLY_URL_RETRIEVAL
                };
                trustDataPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustData>());
                Marshal.StructureToPtr(trustData, trustDataPtr, false);

                actionPtr = Marshal.AllocHGlobal(Marshal.SizeOf<Guid>());
                Marshal.StructureToPtr(WinTrustActionGenericVerifyV2, actionPtr, false);
                var status = WinVerifyTrust(IntPtr.Zero, actionPtr, trustDataPtr);
                if (status != 0)
                    return new RuntimeIntegrityResult(false,
                        $"Windows rejected the Codex Authenticode signature (0x{unchecked((uint)status):X8}).", null);
            }
            finally
            {
                if (actionPtr != IntPtr.Zero) Marshal.FreeHGlobal(actionPtr);
                if (trustDataPtr != IntPtr.Zero) Marshal.FreeHGlobal(trustDataPtr);
                if (fileInfoPtr != IntPtr.Zero) Marshal.FreeHGlobal(fileInfoPtr);
                Marshal.FreeCoTaskMem(filePath);
            }

#pragma warning disable SYSLIB0026 // Authenticode exposes its signer through the legacy signed-file certificate API.
            using var signed = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0026
            using var certificate = new X509Certificate2(signed);
            var signer = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
            if (string.IsNullOrWhiteSpace(signer)) signer = certificate.Subject;
            if (!signer.StartsWith("OpenAI", StringComparison.OrdinalIgnoreCase))
                return new RuntimeIntegrityResult(false, $"The Codex runtime is signed by an unexpected publisher: {signer}", signer);

            return new RuntimeIntegrityResult(true, "Valid OpenAI Authenticode signature.", signer);
        }
        catch (Exception ex)
        {
            return new RuntimeIntegrityResult(false, "Could not verify the Codex runtime: " + ex.Message, null);
        }
    }

    private static void MirrorFile(string homeDirectory, string name)
    {
        try
        {
            var source = Path.Combine(StandaloneHomeDirectory, name);
            var destination = Path.Combine(homeDirectory, name);
            if (!File.Exists(source) || PathsEqual(source, destination)) return;
            if (File.Exists(destination)
                && File.GetLastWriteTimeUtc(destination) >= File.GetLastWriteTimeUtc(source)) return;
            File.Copy(source, destination, overwrite: true);
        }
        catch { /* configuration parity is best-effort; auth isolation must still work */ }
    }

    /// <summary>
    /// The standalone config may opt into Codex's administrator-provisioned Windows sandbox and may give nested
    /// Codex-backed MCP tools an absolute standalone CODEX_HOME. Neither setting is safe to mirror verbatim into
    /// VibeCode's per-account homes: the former can raise UAC during ordinary tool startup, while the latter escapes
    /// the account boundary and reads the standalone config again. Keep the isolated copy self-contained and on the
    /// restricted-token Windows backend. The app-server command-line override remains as defense in depth.
    /// </summary>
    private static void NormalizeIsolatedConfig(string homeDirectory)
    {
        if (!OperatingSystem.IsWindows() || PathsEqual(homeDirectory, StandaloneHomeDirectory)) return;
        try
        {
            var path = Path.Combine(homeDirectory, "config.toml");
            var original = File.Exists(path) ? File.ReadAllText(path) : "";
            var normalized = BuildIsolatedConfig(original, homeDirectory);
            if (!string.Equals(original, normalized, StringComparison.Ordinal))
                File.WriteAllText(path, normalized, new UTF8Encoding(false));
        }
        catch { /* launch-time overrides still keep the primary app-server unelevated if mirroring is unavailable */ }
    }

    internal static string BuildIsolatedConfig(string source, string homeDirectory)
    {
        var newline = source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var canonical = source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = canonical.Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);

        var output = new List<string>(lines.Count + 3);
        var inWindows = false;
        var inMcpEnvironment = false;
        var sawWindows = false;
        var sawWindowsSandbox = false;

        void FinishTable()
        {
            if (inWindows && !sawWindowsSandbox)
            {
                output.Add("sandbox = \"unelevated\"");
                sawWindowsSandbox = true;
            }
        }

        foreach (var line in lines)
        {
            if (TryGetTableHeader(line, out var table))
            {
                FinishTable();
                inWindows = string.Equals(table, "windows", StringComparison.OrdinalIgnoreCase);
                inMcpEnvironment = table.StartsWith("mcp_servers.", StringComparison.OrdinalIgnoreCase)
                                   && table.EndsWith(".env", StringComparison.OrdinalIgnoreCase);
                if (inWindows) sawWindows = true;
                output.Add(line);
                continue;
            }

            if (inWindows && IsAssignment(line, "sandbox"))
            {
                output.Add(LeadingWhitespace(line) + "sandbox = \"unelevated\"");
                sawWindowsSandbox = true;
                continue;
            }

            if (inMcpEnvironment && IsAssignment(line, "CODEX_HOME"))
            {
                output.Add(LeadingWhitespace(line) + "CODEX_HOME = " + TomlString(homeDirectory));
                continue;
            }

            output.Add(line);
        }

        FinishTable();
        if (!sawWindows)
        {
            if (output.Count > 0 && output[^1].Length != 0) output.Add("");
            output.Add("[windows]");
            output.Add("sandbox = \"unelevated\"");
        }

        return string.Join(newline, output) + newline;
    }

    private static bool TryGetTableHeader(string line, out string table)
    {
        var trimmed = line.Trim();
        var close = trimmed.IndexOf(']');
        if (!trimmed.StartsWith("[", StringComparison.Ordinal) || close <= 1)
        {
            table = "";
            return false;
        }

        table = trimmed[1..close].Trim();
        return true;
    }

    private static bool IsAssignment(string line, string key)
    {
        var trimmed = line.TrimStart();
        if (!trimmed.StartsWith(key, StringComparison.OrdinalIgnoreCase)) return false;
        var rest = trimmed[key.Length..].TrimStart();
        return rest.StartsWith('=');
    }

    private static string LeadingWhitespace(string value) => value[..(value.Length - value.TrimStart().Length)];

    private static string TomlString(string value)
    {
        var result = new StringBuilder(value.Length + 2).Append('"');
        foreach (var character in value)
        {
            result.Append(character switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '\b' => "\\b",
                '\t' => "\\t",
                '\n' => "\\n",
                '\f' => "\\f",
                '\r' => "\\r",
                _ => character.ToString(),
            });
        }
        return result.Append('"').ToString();
    }

    private static void LinkDirectory(string homeDirectory, string name)
    {
        var source = Path.Combine(StandaloneHomeDirectory, name);
        var destination = Path.Combine(homeDirectory, name);
        try
        {
            if (!Directory.Exists(source) || Directory.Exists(destination) || PathsEqual(source, destination)) return;
            Directory.CreateSymbolicLink(destination, source);
        }
        catch
        {
            // Directory junctions do not require Windows Developer Mode. Use the built-in only as a narrow fallback;
            // every argument is an already-resolved local path and the destination is inside VibeCode's own home.
            try
            {
                if (!Directory.Exists(source) || Directory.Exists(destination)) return;
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                psi.ArgumentList.Add("/d");
                psi.ArgumentList.Add("/c");
                psi.ArgumentList.Add("mklink");
                psi.ArgumentList.Add("/J");
                psi.ArgumentList.Add(destination);
                psi.ArgumentList.Add(source);
                using var process = Process.Start(psi);
                process?.WaitForExit(3000);
            }
            catch { /* project-local Codex behavior still works when links are forbidden */ }
        }
    }

    private static bool PathsEqual(string a, string b) => string.Equals(
        Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
        Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
        StringComparison.OrdinalIgnoreCase);

    private static readonly Guid WinTrustActionGenericVerifyV2 =
        new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr window, IntPtr actionId, IntPtr trustData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint Size;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint Size;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr File;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }
}

internal readonly record struct RuntimeIntegrityResult(bool Trusted, string Reason, string? Signer);
