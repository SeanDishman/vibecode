using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace VibeCode.Services;

/// <summary>One reason a phone cannot see this PC, and — where there is one — the button that fixes it.</summary>
/// <param name="Title">The one-line statement of what is wrong.</param>
/// <param name="Detail">Why it stops a phone, in the user's terms.</param>
/// <param name="FixLabel">Null when the user has to do something off-screen.</param>
/// <param name="Fix">Returns an empty string on success, or the reason it failed.</param>
/// <param name="Blocking">True when connection setup needs attention. A local check cannot prove reachability.</param>
public sealed record PhoneProblem(
    string Title,
    string Detail,
    string? FixLabel = null,
    Func<string>? Fix = null,
    bool Blocking = true);

/// <summary>
/// Answers the only question that matters when the phone app sits on "Connecting…" forever: what, on this machine,
/// is eating the packets?
///
/// This exists because every one of these failures is silent by construction. A VPN that blocks the local network,
/// a firewall rule that was never created because the app moved to a new version folder, a Wi-Fi marked Public —
/// none of them produce an error on the desktop, and all of them look identical from the handset: a spinner. The
/// bridge itself is working perfectly in every one of those cases, which is exactly why the user cannot debug it.
///
/// So the desktop checks its own environment and says so in words, with a button. Nothing here is a heuristic
/// dressed up as a fact: each check reads real system state (the firewall rule table, the network category, the
/// VPN's own setting) rather than inferring from a failed connection.
/// </summary>
public static class PhoneReachability
{
    /// <summary>Name of the inbound rule this app manages. Deliberately keyed on the PORT, not the executable:
    /// VibeCode installs into a per-version folder, so an executable rule silently stops matching after every
    /// update and the user gets a firewall prompt they have probably already dismissed once.</summary>
    private const string RuleName = "VibeCode Phone Access";

    private static readonly string[] MullvadPaths =
    {
        @"C:\Program Files\Mullvad VPN\resources\mullvad.exe",
        @"C:\Program Files (x86)\Mullvad VPN\resources\mullvad.exe",
    };

    /// <summary>Everything currently standing between a phone on the same Wi-Fi and this PC, worst first.</summary>
    public static List<PhoneProblem> Check()
    {
        var problems = new List<PhoneProblem>();
        var bridge = PhoneBridgeService.Instance;
        var port = bridge.Port;

        if (!bridge.Running)
        {
            problems.Add(new PhoneProblem(
                "Phone access is switched off",
                "Nothing is listening, so the app on your phone will sit on \"Connecting\" forever.",
                "Turn it on",
                () => { bridge.Start(); return bridge.Running ? "" : bridge.Error; }));
        }

        var addresses = PhoneBridgeStore.LocalAddresses();
        if (addresses.Count == 0)
        {
            problems.Add(new PhoneProblem(
                "This PC is not on a network",
                "No Wi-Fi or Ethernet connection was found, so there is no address a phone could reach."));
        }

        problems.AddRange(VpnProblems());
        problems.AddRange(FirewallProblems(port));
        problems.AddRange(NetworkCategoryProblems());

        return problems;
    }

    // ---------------- VPN ----------------

    /// <summary>
    /// A VPN that is doing its job blocks the local network, and that is by far the most confusing way for this
    /// feature to fail: everything on the desktop reports healthy, the phone is on the right Wi-Fi, and not one
    /// packet arrives. Mullvad is checked by name because it states the setting plainly and can be corrected
    /// without touching anything else; other tunnels are reported rather than guessed at.
    /// </summary>
    private static IEnumerable<PhoneProblem> VpnProblems()
    {
        var mullvad = MullvadPaths.FirstOrDefault(File.Exists);
        if (mullvad is not null)
        {
            var setting = RunCapture(mullvad, "lan get");
            if (setting.Contains("block", StringComparison.OrdinalIgnoreCase))
            {
                yield return new PhoneProblem(
                    "Mullvad VPN is blocking your local network",
                    "While \"Local network sharing\" is off, Mullvad drops every packet between this PC and "
                    + "anything else on your Wi-Fi — including your phone. Turning it on does not expose you to "
                    + "the internet; it only lets devices in your own home reach this machine.",
                    "Allow local network",
                    () =>
                    {
                        var output = RunCapture(mullvad, "lan set allow");
                        var now = RunCapture(mullvad, "lan get");
                        return now.Contains("allow", StringComparison.OrdinalIgnoreCase)
                            ? ""
                            : output.Length > 0 ? output.Trim() : "Mullvad did not accept the change.";
                    });
                yield break;
            }
            if (!setting.Contains("allow", StringComparison.OrdinalIgnoreCase))
                yield return new PhoneProblem("VPN local-network setting could not be checked",
                    "Open Mullvad and check Local network sharing if the phone cannot connect.", Blocking: false);
            yield break;
        }

        // Any other tunnel holding the default route. This is informational: plenty of VPNs pass LAN traffic
        // happily, and claiming otherwise would send the user chasing the wrong thing.
        var tunnel = DefaultRouteTunnel();
        if (tunnel is not null)
        {
            yield return new PhoneProblem(
                $"A VPN is active ({tunnel})",
                "Some VPNs block your local network while connected. If your phone cannot reach this PC, look "
                + "for a \"local network sharing\" or \"allow LAN\" setting in that app and switch it on — or "
                + "disconnect the VPN and try again.",
                Blocking: false);
        }
    }

    /// <summary>The friendly name of a tunnel adapter that owns the default route, or null when the route is
    /// leaving through ordinary hardware.</summary>
    private static string? DefaultRouteTunnel()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                var isTunnel = nic.NetworkInterfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp
                    || nic.Description.Contains("tunnel", StringComparison.OrdinalIgnoreCase)
                    || nic.Description.Contains("wireguard", StringComparison.OrdinalIgnoreCase)
                    || nic.Description.Contains("tap-", StringComparison.OrdinalIgnoreCase)
                    || nic.Description.Contains("openvpn", StringComparison.OrdinalIgnoreCase);
                if (!isTunnel) continue;
                var hasDefault = nic.GetIPProperties().GatewayAddresses
                    .Any(g => g.Address is { } a && !a.Equals(IPAddress.Any) && !a.Equals(IPAddress.IPv6Any));
                if (hasDefault) return nic.Name;
            }
        }
        catch (Exception ex)
        {
            CrashLog.Note("PhoneBridge", $"tunnel probe failed - {ex.Message}");
        }
        return null;
    }

    // ---------------- Windows Firewall ----------------

    internal sealed record FwRule(string Name, bool Enabled, int Direction, int Action, int Protocol,
        string LocalPorts, string App, int Profiles);

    /// <summary>
    /// Whether the firewall will actually let a phone in, judged from the rule table rather than from hope.
    ///
    /// Two distinct failures live here. The obvious one is that no allow rule exists. The nastier one is a BLOCK
    /// rule for VibeCode's executable — which is what Windows silently writes when the "allow this app?" prompt is
    /// dismissed with Cancel, and which then outranks any allow rule for the rest of time.
    /// </summary>
    /// <summary>What the firewall rule table says about one executable on one port.</summary>
    /// <param name="Readable">False when the rule table could not be inspected.</param>
    /// <param name="Allowed">A matching allow rule exists on an active profile. Address scope and other policy
    /// can still prevent a particular phone from connecting; this is not a reachability test.</param>
    /// <param name="BlockRules">Names of enabled inbound TCP block rules covering this executable and port.</param>
    public sealed record FirewallVerdict(bool Readable, bool Allowed, IReadOnlyList<string> BlockRules);

    /// <summary>
    /// Judges the firewall for an arbitrary executable and port. Public and parameterised purely so this can be
    /// tested against both answers — the shipping path always passes the running executable and the live port.
    /// </summary>
    public static FirewallVerdict InspectFirewall(string exe, int port)
    {
        List<FwRule> rules;
        int activeProfiles;
        try
        {
            var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (type is null) return new FirewallVerdict(false, false, Array.Empty<string>());
            dynamic policy = Activator.CreateInstance(type)!;
            activeProfiles = (int)policy.CurrentProfileTypes;
            rules = ReadRules(policy);
        }
        catch (Exception ex)
        {
            CrashLog.Note("PhoneBridge", $"firewall inspection failed - {ex.Message}");
            return new FirewallVerdict(false, false, Array.Empty<string>());
        }

        return EvaluateFirewallRules(rules, exe, port, activeProfiles);
    }

    internal static FirewallVerdict EvaluateFirewallRules(IEnumerable<FwRule> rules, string exe, int port, int activeProfiles)
    {
        var snapshot = rules.ToList();
        bool OnActiveProfile(FwRule r) => (r.Profiles & activeProfiles) != 0 || r.Profiles == int.MaxValue;
        bool ForThisApp(FwRule r) => r.App.Length == 0
            || (exe.Length > 0 && string.Equals(r.App, exe, StringComparison.OrdinalIgnoreCase));

        var blocked = snapshot.Where(r =>
                r.Enabled && r.Direction == 1 && r.Action == 0 && OnActiveProfile(r)
                && (r.Protocol is 6 or 256) && PortsCover(r.LocalPorts, port) && ForThisApp(r))
            .Select(r => r.Name).Distinct().ToList();

        // Rules for another executable do not apply. Unscoped port rules (including an explicit allow-all)
        // may apply, but a matching rule still says nothing about the phone's route or the rule's address scope.
        var allowed = snapshot.Any(r =>
            r.Enabled && r.Direction == 1 && r.Action == 1 && OnActiveProfile(r)
            && (r.Protocol is 6 or 256) && PortsCover(r.LocalPorts, port)
            && ForThisApp(r));

        return new FirewallVerdict(true, allowed, blocked);
    }

    private static IEnumerable<PhoneProblem> FirewallProblems(int port)
    {
        var exe = Environment.ProcessPath ?? "";
        var verdict = InspectFirewall(exe, port);
        if (!verdict.Readable)
        {
            yield return new PhoneProblem("Windows Firewall could not be checked",
                "Check its settings if the phone cannot connect. No conclusion about reachability is available.", Blocking: false);
            yield break;
        }

        if (verdict.BlockRules.Count > 0)
        {
            var names = string.Join(", ", verdict.BlockRules);
            yield return new PhoneProblem(
                "Windows Firewall has a matching block rule",
                $"An active inbound rule covers this app and TCP port {port} ({names}). "
                + "Review its network and address scope if the phone cannot connect. A matching block overrides an allow.",
                "Review firewall rules", OpenFirewallSettings);
        }

        if (!verdict.Allowed)
        {
            yield return new PhoneProblem(
                "No matching phone access rule found",
                $"The local check did not find an active inbound TCP rule for port {port}. If Windows Firewall "
                + "blocks the phone, this adds a rule for the local subnet on Private and Domain networks. "
                + "It does not configure access over a private VPN or test remote connectivity.",
                "Allow it through the firewall",
                () => AddFirewallRule(port));
        }
    }

    private static List<FwRule> ReadRules(dynamic policy)
    {
        var list = new List<FwRule>();
        var rules = (System.Collections.IEnumerable)policy.Rules;
        foreach (var item in rules)
        {
            try
            {
                dynamic r = item;
                list.Add(new FwRule(
                    (string)(r.Name ?? ""),
                    (bool)r.Enabled,
                    (int)r.Direction,
                    (int)r.Action,
                    (int)r.Protocol,
                    (string)(r.LocalPorts ?? ""),
                    (string)(r.ApplicationName ?? ""),
                    (int)r.Profiles));
            }
            catch
            {
                // A rule the COM layer will not describe (there are always a few) is not worth failing the whole
                // diagnosis over.
            }
        }
        return list;
    }

    /// <summary>Whether a rule's LocalPorts field covers the bridge port. "*" and an empty field both mean any.</summary>
    private static bool PortsCover(string localPorts, int port)
    {
        if (localPorts.Length == 0 || localPorts == "*") return true;
        foreach (var part in localPorts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dash = part.IndexOf('-');
            if (dash > 0)
            {
                if (int.TryParse(part[..dash], out var lo) && int.TryParse(part[(dash + 1)..], out var hi)
                    && port >= lo && port <= hi) return true;
            }
            else if (int.TryParse(part, out var single) && single == port) return true;
        }
        return false;
    }

    /// <summary>
    /// Writes the inbound rule, scoped to the local subnet and to the private/domain profiles.
    ///
    /// Deliberately narrower than what Windows' own prompt would have created: that one allows the executable on
    /// every port and, historically here, on the Public profile too. This one is a single TCP/UDP port reachable
    /// only from the network segment this machine is already on.
    /// </summary>
    private static string AddFirewallRule(int port)
    {
        var script =
            $"advfirewall firewall delete rule name=\"{RuleName}\" >nul 2>&1 & " +
            $"netsh advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow protocol=TCP " +
            $"localport={port} profile=private,domain remoteip=localsubnet & " +
            $"netsh advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow protocol=UDP " +
            $"localport={port} profile=private,domain remoteip=localsubnet";
        return Elevate(script);
    }

    // ---------------- network category ----------------

    /// <summary>
    /// A network marked Public tells Windows to refuse inbound connections and to hide this machine from the rest
    /// of the LAN. It is the correct setting in a coffee shop and the wrong one at home, and Windows picks it by
    /// default surprisingly often.
    /// </summary>
    private static IEnumerable<PhoneProblem> NetworkCategoryProblems()
    {
        // Only the adapters a phone could actually arrive on are judged. Without this, a VPN tunnel - which is
        // legitimately and permanently Public - gets reported as the user's home Wi-Fi being misconfigured, and
        // the offered "fix" would downgrade the VPN's own profile for no benefit whatsoever.
        var relevant = AdvertisedAdapterIds();
        if (relevant.Count == 0) yield break;

        List<string> publicNetworks = new();
        var anyPrivate = false;
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"));
            if (type is null) yield break;
            dynamic manager = Activator.CreateInstance(type)!;
            // NLM_ENUM_NETWORK_CONNECTED
            var connections = (System.Collections.IEnumerable)manager.GetNetworkConnections();
            foreach (var item in connections)
            {
                dynamic connection = item;
                try
                {
                    var adapter = ((Guid)connection.GetAdapterId()).ToString("B").ToUpperInvariant();
                    if (!relevant.Contains(adapter)) continue;
                    dynamic network = connection.GetNetwork();
                    // 0 = Public, 1 = Private, 2 = Domain-authenticated.
                    var category = (int)network.GetCategory();
                    if (category == 0) publicNetworks.Add((string)(network.GetName() ?? "a network"));
                    else anyPrivate = true;
                }
                catch { /* a connection that will not describe itself */ }
            }
        }
        catch (Exception ex)
        {
            CrashLog.Note("PhoneBridge", $"network category probe failed - {ex.Message}");
            yield break;
        }

        if (publicNetworks.Count == 0) yield break;

        // With a private network also present, the phone almost certainly arrives over that one, so this is a
        // note rather than a blocker.
        var names = string.Join(", ", publicNetworks.Distinct());
        yield return new PhoneProblem(
            $"A network is set to Public ({names})",
            "The built-in local-subnet rule applies to Private and Domain networks. Review the connection your "
            + "phone uses, and only mark a network Private if you trust it. VPN profiles may intentionally stay Public.",
            "Review network settings",
            () => OpenSettings("ms-settings:network-status"),
            Blocking: !anyPrivate);
    }

    /// <summary>Adapter GUIDs, in registry form, of the interfaces holding the addresses this PC hands to phones.</summary>
    private static HashSet<string> AdvertisedAdapterIds()
    {
        var wanted = PhoneBridgeStore.LocalAddresses().Select(a => a.ToString()).ToHashSet(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0) return ids;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                var mine = nic.GetIPProperties().UnicastAddresses
                    .Any(ua => ua.Address.AddressFamily == AddressFamily.InterNetwork
                               && wanted.Contains(ua.Address.ToString()));
                if (mine) ids.Add(nic.Id.ToUpperInvariant());
            }
        }
        catch (Exception ex)
        {
            CrashLog.Note("PhoneBridge", $"adapter mapping failed - {ex.Message}");
        }
        return ids;
    }

    // ---------------- running things ----------------

    /// <summary>Runs a console tool and returns everything it said. Used only for tools that answer in one line.</summary>
    internal static string RunCapture(string exe, string arguments, int timeoutMs = 10_000)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(exe, arguments)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            if (process is null) return "";
            // Drain both pipes concurrently. Reading one to completion first can deadlock on a full stderr
            // pipe, and synchronous ReadToEnd before WaitForExit made the timeout ineffective.
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(true); } catch { /* gone */ }
                return "";
            }
            if (!Task.WaitAll(new Task[] { output, error }, timeoutMs)) return "";
            return output.Result + error.Result;
        }
        catch (Exception ex)
        {
            CrashLog.Note("PhoneBridge", $"could not run {Path.GetFileName(exe)} - {ex.Message}");
            return "";
        }
    }

    private static string OpenFirewallSettings() => OpenSettings("wf.msc");

    private static string OpenSettings(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return "";
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>
    /// Runs one administrative command behind a UAC prompt. Returns "" on success.
    ///
    /// Output cannot be captured through ShellExecute, which is fine: every caller re-runs <see cref="Check"/>
    /// afterwards and believes the system state rather than an exit code.
    /// </summary>
    private static string Elevate(string arguments, bool powershell = false)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = powershell ? "powershell.exe" : "cmd.exe",
                Arguments = powershell ? arguments : $"/c netsh {arguments}",
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var process = Process.Start(psi);
            if (process is null) return "Windows would not start the command.";
            if (!process.WaitForExit(60_000)) return "The command did not finish.";
            return process.ExitCode == 0 ? "" : $"The command failed (code {process.ExitCode}).";
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // 1223 == the user clicked No on the UAC prompt. Nothing went wrong; they declined.
            return "Administrator permission is needed to change this, and the prompt was dismissed.";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
