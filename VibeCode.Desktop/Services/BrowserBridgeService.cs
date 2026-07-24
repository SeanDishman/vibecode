using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32.SafeHandles;
using VibeCode.UI;

namespace VibeCode.Services;

/// <summary>
/// Publishes VibeCode's WebView2 windows through the named-pipe browser backend understood by the bundled Codex
/// Browser plugin. The plugin remains the policy/automation layer; this service supplies the missing user-visible
/// browser process, DevTools transport, and tab lifecycle.
/// </summary>
public sealed class BrowserBridgeService : IDisposable
{
    public const string BuildFlavor = Protocol.CodexEnvironment.BrowserBuildFlavor;
    private const string PipePrefix = "codex-browser-use";
    private const int PipeBufferBytes = 64 * 1024;
    private const int PipeClientSlots = BridgeAgentPolicy.MaximumAgentLimit;
    private const int SeKernelObject = 6;
    private const uint LabelSecurityInformation = 0x00000010;

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern uint SetSecurityInfo(
        SafePipeHandle handle,
        int objectType,
        uint securityInformation,
        IntPtr owner,
        IntPtr group,
        IntPtr dacl,
        IntPtr sacl);

    private static readonly string[] DevToolsEvents =
    {
        "Page.frameNavigated",
        "Page.navigatedWithinDocument",
        "Page.frameDetached",
        "Page.loadEventFired",
        "Page.domContentEventFired",
        "Page.lifecycleEvent",
        "Page.javascriptDialogOpening",
        "Page.javascriptDialogClosed",
        "Page.fileChooserOpened",
        "Page.screencastFrame",
        "Page.screencastVisibilityChanged",
        "Runtime.consoleAPICalled",
        "Runtime.exceptionThrown",
        "Runtime.bindingCalled",
        // Execution-context events are what a Playwright-style client waits on before it will evaluate
        // anything. Omitting them does not fail a call - it hangs it until the caller's own timeout, which
        // is why page interaction appeared broken while plain CDP commands looked fine.
        "Runtime.executionContextCreated",
        "Runtime.executionContextDestroyed",
        "Runtime.executionContextsCleared",
        "Log.entryAdded",
        "Page.frameAttached",
        "Page.frameStartedLoading",
        "Page.frameStoppedLoading",
        "Page.frameRequestedNavigation",
        "Page.windowOpen",
        "Page.downloadWillBegin",
        "Page.downloadProgress",
        "Network.requestWillBeSent",
        "Network.responseReceived",
        "Network.loadingFinished",
        "Network.loadingFailed",
        "Network.dataReceived",
        "Network.requestWillBeSentExtraInfo",
        "Network.responseReceivedExtraInfo",
        "Network.requestServedFromCache",
        "Fetch.requestPaused",
        "Fetch.authRequired",
        "Inspector.targetCrashed",
        "Inspector.targetReloadedAfterCrash",
        "Target.attachedToTarget",
        "Target.detachedFromTarget",
        "Target.targetCreated",
        "Target.targetDestroyed",
        "Target.targetInfoChanged",
        "Inspector.detached",
        "DOM.documentUpdated",
    };

    private readonly Dispatcher _dispatcher;
    private readonly SecurityIdentifier _ownerUser = GetCurrentUser();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _logGate = new();
    private readonly string? _logPath = Environment.GetEnvironmentVariable("VIBECODE_BROWSER_BRIDGE_LOG");
    private readonly ConcurrentDictionary<Guid, BridgeConnection> _connections = new();
    private readonly ConcurrentDictionary<int, BrowserTab> _tabs = new();
    private readonly string _pipeName = CreatePipeName();
    private Task? _acceptLoop;
    private CoreWebView2Environment? _environment;
    private int _nextTabId;
    private int _activeTabId;
    private bool _disposed;

    private static string EffectiveBuildFlavor =>
        Environment.GetEnvironmentVariable("VIBECODE_BROWSER_BUILD_FLAVOR")?.Trim() is { Length: > 0 } value
            ? value
            : BuildFlavor;
    private static string AppVersion =>
        typeof(BrowserBridgeService).Assembly.GetName().Version?.ToString() ?? "1.0.0";

    public BrowserBridgeService(Dispatcher dispatcher) => _dispatcher = dispatcher;

    internal string PipeName => _pipeName;

    private static string CreatePipeName() =>
        $"{PipePrefix}-vibecode-{Environment.ProcessId}-{Guid.NewGuid():N}";

    private static SecurityIdentifier GetCurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User
               ?? throw new InvalidOperationException("The browser bridge could not identify the current user.");
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_acceptLoop is not null) return;

        // Create every server instance before lowering the pipe object's integrity label. Windows applies the
        // label to the shared named-pipe object; attempting to add another instance after that can fail with
        // ERROR_ACCESS_DENIED even for the owning medium-integrity process. The fixed pool is recycled as clients
        // disconnect and covers the product's maximum number of simultaneous Bridge agents.
        var servers = new List<NamedPipeServerStream>(PipeClientSlots);
        try
        {
            for (var index = 0; index < PipeClientSlots; index++)
                servers.Add(CreatePipeServer(requestSecurityControl: index == 0));
            AllowLowIntegrityClients(servers[0].SafePipeHandle);

            Log($"Listening on {PipeName} with {PipeClientSlots} client slots.");
            _acceptLoop = Task.WhenAll(servers.Select(AcceptLoopAsync));
        }
        catch
        {
            foreach (var server in servers) server.Dispose();
            throw;
        }
    }

    private async Task AcceptLoopAsync(NamedPipeServerStream server)
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                await server.WaitForConnectionAsync(_shutdown.Token);
                Log("Accepted browser client connection.");

                var connection = new BridgeConnection(this, server, _shutdown.Token);
                _connections[connection.Id] = connection;
                await connection.RunAsync();
                RemoveConnection(connection);

                if (_shutdown.IsCancellationRequested) break;
                // Disconnect is valid for both connected and broken server states. Do not query IsConnected first:
                // PipeStream throws for a broken peer before NamedPipeServerStream gets the chance to reset it.
                server.Disconnect();
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_shutdown.IsCancellationRequested) Log("Accept loop error: " + DescribeException(ex));
        }
        finally
        {
            server.Dispose();
        }
    }

    private NamedPipeServerStream CreatePipeServer(bool requestSecurityControl)
    {
        var pipeName = PipeName;
        var security = new PipeSecurity();
        security.SetSecurityDescriptorSddlForm(
            $"D:P(A;;GA;;;SY)(A;;GA;;;{_ownerUser.Value})");

        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            PipeBufferBytes,
            PipeBufferBytes,
            security,
            HandleInheritability.None,
            requestSecurityControl
                ? PipeAccessRights.ChangePermissions | PipeAccessRights.TakeOwnership
                : 0);
    }

    private static void AllowLowIntegrityClients(SafePipeHandle handle)
    {
        // Objects without an integrity label are treated as medium integrity. Apply only LABEL_SECURITY_INFORMATION
        // after creation: unlike replacing the full SACL, this does not require SeSecurityPrivilege.
        var label = new RawSecurityDescriptor("S:(ML;;NW;;;LW)").SystemAcl
                    ?? throw new InvalidOperationException("The low-integrity pipe label is invalid.");
        var buffer = new byte[label.BinaryLength];
        label.GetBinaryForm(buffer, 0);
        var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var error = SetSecurityInfo(
                handle,
                SeKernelObject,
                LabelSecurityInformation,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero,
                pinned.AddrOfPinnedObject());
            if (error != 0)
                throw new Win32Exception((int)error);
        }
        finally
        {
            pinned.Free();
        }
    }

    private void RemoveConnection(BridgeConnection connection)
    {
        Log("Browser client disconnected.");
        _connections.TryRemove(connection.Id, out _);
        foreach (var tabId in connection.AttachedTabs.Keys)
        {
            if (_tabs.TryGetValue(tabId, out var tab)) tab.AttachedConnections.TryRemove(connection.Id, out _);
        }
        connection.Dispose();
    }

    private async Task<JsonNode?> DispatchAsync(BridgeConnection connection, string method, JsonObject parameters)
    {
        Log($"Request {method}.");
        CaptureSession(connection, parameters);
        Log($"Request context session={connection.SessionId ?? "<missing>"}, turn={connection.TurnId ?? "<missing>"}.");
        return method switch
        {
            "ping" => JsonValue.Create("pong"),
            "getInfo" => GetInfo(connection),
            "getTabs" => await GetTabsAsync(connection, includeOnlyOwned: true),
            "getUserTabs" => await GetTabsAsync(connection, includeOnlyOwned: true),
            "getUserHistory" => GetUserHistory(connection),
            "createTab" => await CreateTabResponseAsync(connection),
            "claimUserTab" => await ClaimTabAsync(connection, parameters),
            "attach" => Attach(connection, parameters),
            "detach" => Detach(connection, parameters),
            "attachTarget" => await AttachTargetAsync(connection, parameters),
            "detachTarget" => await DetachTargetAsync(connection, parameters),
            "executeCdp" => await ExecuteCdpAsync(connection, parameters),
            // Deliberately omit executeCdpWithCachedExpression. The browser client recognizes the standard
            // method-not-found response and transparently retries through executeCdp.
            "allowDownload" => await AllowDownloadAsync(connection, parameters),
            "moveMouse" => await MoveMouseAsync(connection, parameters),
            "markTab" => MarkTab(connection, parameters),
            "finalizeTabs" => await FinalizeTabsAsync(connection, parameters),
            "nameSession" => await NameSessionAsync(connection, parameters),
            "turnEnded" => new JsonObject(),
            "executeUnhandledCommand" => await ExecuteUnhandledCommandAsync(connection, parameters),
            _ => throw new BrowserMethodNotFoundException(method),
        };
    }

    private static void CaptureSession(BridgeConnection connection, JsonObject parameters)
    {
        if (parameters["session_id"]?.GetValue<string>() is { Length: > 0 } sessionId)
            connection.SessionId = sessionId;
        if (parameters["turn_id"]?.GetValue<string>() is { Length: > 0 } turnId)
            connection.TurnId = turnId;
    }

    private static string RequireSession(BridgeConnection connection) =>
        connection.SessionId ?? throw new InvalidOperationException("Missing required browser session_id.");

    private static JsonObject GetInfo(BridgeConnection connection)
    {
        var sessionId = RequireSession(connection);
        return new JsonObject
        {
            ["name"] = "VibeCode Browser",
            ["version"] = AppVersion,
            ["type"] = "iab",
            // The plugin's API manifest marks these members unsupportedByDefaultIn ["iab","cdp"] and strips them
            // from the object the model is handed unless the backend opts back in. BrowserUser.history was
            // missing here, which is the whole reason iab.user.history came back "is not a function" even though
            // getUserHistory was implemented and answering.
            ["apiSupportOverrides"] = new JsonObject
            {
                ["BrowserUser.claimTab"] = true,
                ["BrowserUser.history"] = true,
                ["Tab.markDeliverable"] = true,
                ["Tab.markHandoff"] = true,
                ["Tabs.finalize"] = true,
            },
            // The Browser plugin renders this list straight into the agent's instructions; an empty array is
            // published to the model as "- None" and removes the matching capability object from its API. Only
            // advertise ids this bridge genuinely implements below - a declared-but-missing capability would
            // surface to the agent as a hard failure mid-task.
            ["capabilities"] = new JsonObject
            {
                ["browser"] = new JsonArray(
                    Capability("visibility",
                        "Show or hide the VibeCode browser window, and read whether it is currently on screen."),
                    Capability("viewport",
                        "Apply or clear an explicit viewport size on the VibeCode browser window.")),
                ["tab"] = new JsonArray(
                    Capability("cdp",
                        "Send raw Chrome DevTools Protocol commands and read debugger events through this tab.")),
            },
            ["metadata"] = new JsonObject
            {
                ["codexSessionId"] = sessionId,
                ["codexAppBuildFlavor"] = EffectiveBuildFlavor,
                ["host"] = "VibeCode",
            },
        };
    }

    private static JsonObject Capability(string id, string description) => new()
    {
        ["id"] = id,
        ["description"] = description,
    };

    /// <summary>
    /// Navigations this chat's tabs actually performed, newest first. Returns a bare array, not an {items}
    /// envelope: the plugin hands this straight back from browserUser.history() and maps over it, exactly as it
    /// does for getUserTabs. An envelope here fails in the model's hands with ".map is not a function".
    /// </summary>
    private JsonNode? GetUserHistory(BridgeConnection connection)
    {
        var sessionId = RequireSession(connection);
        var items = new JsonArray();
        var visits = _tabs.Values
            .Where(tab => string.Equals(tab.OwnerSessionId, sessionId, StringComparison.Ordinal))
            .SelectMany(tab => tab.History)
            .OrderByDescending(visit => visit.VisitedUtc);
        foreach (var visit in visits)
        {
            var item = new JsonObject
            {
                ["url"] = visit.Url,
                ["dateVisited"] = visit.VisitedUtc.ToString("O"),
            };
            if (!string.IsNullOrWhiteSpace(visit.Title)) item["title"] = visit.Title;
            items.Add(item);
        }
        return items;
    }

    private async Task<JsonNode?> CreateTabResponseAsync(BridgeConnection connection)
    {
        var tab = await CreateTabAsync(RequireSession(connection));
        return await OnUiAsync(() => Snapshot(tab, active: true));
    }

    private async Task<BrowserTab> CreateTabAsync(string sessionId)
    {
        return await OnUiAsync(async () =>
        {
            Log("Creating WebView2 environment.");
            _environment ??= await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: Path.Combine(AppSettings.Dir, "browser"));
            Log("WebView2 environment is ready.");

            var id = Interlocked.Increment(ref _nextTabId);
            var window = new BrowserWindow();
            if (Application.Current?.MainWindow is { IsVisible: true } owner && !ReferenceEquals(owner, window))
                window.Owner = owner;

            if (Environment.GetEnvironmentVariable("VIBECODE_BROWSER_WINDOW_HIDDEN") == "1")
            {
                // Integration tests still need a rendered HWND for screenshots, but it can stay off-screen.
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -32000;
                window.Top = -32000;
                window.ShowActivated = false;
            }

            Log($"Showing browser tab {id}.");
            window.Show();
            try
            {
                Log($"Initializing WebView2 controller for tab {id}.");
                await window.InitializeAsync(_environment);
                Log($"Browser tab {id} is ready.");
            }
            catch
            {
                window.Close();
                throw;
            }

            var tab = new BrowserTab(id, sessionId, window);
            _tabs[id] = tab;
            _activeTabId = id;
            window.Activated += (_, _) => _activeTabId = id;
            window.Closed += (_, _) => OnTabClosed(tab);
            SubscribeDevToolsEvents(tab);
            return tab;
        });
    }

    private void SubscribeDevToolsEvents(BrowserTab tab)
    {
        foreach (var name in DevToolsEvents)
        {
            try
            {
                var eventName = name;
                var receiver = tab.Window.Core.GetDevToolsProtocolEventReceiver(eventName);
                receiver.DevToolsProtocolEventReceived += (_, args) => OnDevToolsEvent(tab, eventName, args);
                tab.EventReceivers.Add(receiver);
            }
            catch
            {
                // Runtime versions differ slightly in their event catalog. Unsupported optional events do not
                // prevent the core Page/Runtime/DOM bridge from operating.
            }
        }
    }

    private void OnDevToolsEvent(BrowserTab tab, string method, CoreWebView2DevToolsProtocolEventReceivedEventArgs args)
    {
        if (!_tabs.ContainsKey(tab.Id)) return;
        JsonNode? eventParameters;
        try { eventParameters = JsonNode.Parse(args.ParameterObjectAsJson); }
        catch { eventParameters = new JsonObject(); }

        // Top-level document navigations are the browsing history the plugin asks for through getUserHistory.
        // Sub-frames carry a parentId and must not pollute it.
        if (method == "Page.frameNavigated" &&
            eventParameters?["frame"] is JsonObject frame &&
            frame["parentId"] is null &&
            frame["url"]?.GetValue<string>() is { Length: > 0 } visitedUrl)
        {
            tab.RecordVisit(visitedUrl);
        }

        var source = new JsonObject { ["tabId"] = tab.Id };
        if (!string.IsNullOrWhiteSpace(args.SessionId)) source["sessionId"] = args.SessionId;
        var payload = new JsonObject
        {
            ["source"] = source,
            ["method"] = method,
            ["params"] = eventParameters,
        };
        Broadcast(tab, "onCDPEvent", payload);
    }

    private void OnTabClosed(BrowserTab tab)
    {
        if (!_tabs.TryRemove(tab.Id, out _)) return;
        Broadcast(tab, "onCDPDetach", new JsonObject { ["tabId"] = tab.Id });
        foreach (var connectionId in tab.AttachedConnections.Keys)
        {
            if (_connections.TryGetValue(connectionId, out var connection))
                connection.AttachedTabs.TryRemove(tab.Id, out _);
        }

        if (_activeTabId == tab.Id)
            _activeTabId = _tabs.Values.OrderByDescending(value => value.LastActivatedUtc).FirstOrDefault()?.Id ?? 0;
    }

    private void Broadcast(BrowserTab tab, string method, JsonObject parameters)
    {
        foreach (var connectionId in tab.AttachedConnections.Keys)
        {
            if (!_connections.TryGetValue(connectionId, out var connection) ||
                !string.Equals(connection.SessionId, tab.OwnerSessionId, StringComparison.Ordinal)) continue;
            _ = connection.NotifyAsync(method, parameters.DeepClone().AsObject());
        }
    }

    private async Task<JsonNode?> GetTabsAsync(BridgeConnection connection, bool includeOnlyOwned)
    {
        var sessionId = RequireSession(connection);
        return await OnUiAsync(() =>
        {
            var tabs = _tabs.Values
                .Where(tab => !includeOnlyOwned || string.Equals(tab.OwnerSessionId, sessionId, StringComparison.Ordinal))
                .OrderBy(tab => tab.Id)
                .ToList();
            var activeId = tabs.Any(tab => tab.Id == _activeTabId) ? _activeTabId : tabs.FirstOrDefault()?.Id ?? 0;
            var result = new JsonArray();
            foreach (var tab in tabs) result.Add(Snapshot(tab, tab.Id == activeId));
            return (JsonNode)result;
        });
    }

    private static JsonObject Snapshot(BrowserTab tab, bool active) => new()
    {
        ["id"] = tab.Id,
        ["url"] = tab.Window.CurrentUrl,
        ["title"] = tab.Window.CurrentTitle,
        ["active"] = active,
        ["lastOpened"] = tab.LastActivatedUtc.ToString("O"),
    };

    private async Task<JsonNode?> ClaimTabAsync(BridgeConnection connection, JsonObject parameters)
    {
        var tab = RequireOwnedTab(connection, ReadTabId(parameters["tabId"]));
        _activeTabId = tab.Id;
        tab.LastActivatedUtc = DateTime.UtcNow;
        return await OnUiAsync(() => Snapshot(tab, active: true));
    }

    private JsonNode? Attach(BridgeConnection connection, JsonObject parameters)
    {
        var tab = RequireOwnedTab(connection, ReadTabId(parameters["tabId"]));
        connection.AttachedTabs[tab.Id] = 0;
        tab.AttachedConnections[connection.Id] = 0;
        return new JsonObject();
    }

    private JsonNode? Detach(BridgeConnection connection, JsonObject parameters)
    {
        var tabId = ReadTabId(parameters["tabId"]);
        connection.AttachedTabs.TryRemove(tabId, out _);
        if (_tabs.TryGetValue(tabId, out var tab)) tab.AttachedConnections.TryRemove(connection.Id, out _);
        return new JsonObject();
    }

    private async Task<JsonNode?> ExecuteCdpAsync(BridgeConnection connection, JsonObject parameters)
    {
        var target = parameters["target"] as JsonObject
                     ?? throw new InvalidOperationException("executeCdp requires a target.");
        var tab = RequireOwnedTab(connection, ReadTabId(target["tabId"]));
        var method = parameters["method"]?.GetValue<string>()
                     ?? throw new InvalidOperationException("executeCdp requires a method.");

        if (method is "Page.close" or "Browser.close")
        {
            await CloseTabAsync(tab);
            return new JsonObject();
        }
        if (method == "Target.closeTarget")
        {
            await CloseTabAsync(tab);
            return new JsonObject { ["success"] = true };
        }
        if (method == "Page.bringToFront")
        {
            await OnUiAsync(() =>
            {
                if (tab.Window.WindowState == WindowState.Minimized) tab.Window.WindowState = WindowState.Normal;
                tab.Window.Show();
                tab.Window.Activate();
                return true;
            });
        }

        Log($"executeCdp -> {method} (tab {tab.Id}) starting.");
        var command = parameters["commandParams"]?.ToJsonString() ?? "{}";
        // A target sessionId addresses an attached child target (iframe, worker, popup). Routing it through the
        // page-level overload would silently run the command against the wrong document.
        var cdpSessionId = target["sessionId"]?.GetValue<string>();
        var call = OnUiAsync(() => string.IsNullOrWhiteSpace(cdpSessionId)
            ? tab.Window.Core.CallDevToolsProtocolMethodAsync(method, command)
            : tab.Window.Core.CallDevToolsProtocolMethodForSessionAsync(cdpSessionId, method, command));
        var timeoutMs = ReadOptionalInt(parameters["timeoutMs"]);
        var response = timeoutMs is > 0
            ? await call.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs.Value), connection.CancellationToken)
            : await call.WaitAsync(connection.CancellationToken);
        Log($"executeCdp -> {method} (tab {tab.Id}) completed.");
        return JsonNode.Parse(response) ?? new JsonObject();
    }

    /// <summary>
    /// Attach to a child target so the plugin's cdp capability can address iframes, workers and popups.
    /// flatten:true keeps the child on this same transport, which is what the sessionId route in executeCdp
    /// expects.
    /// </summary>
    private async Task<JsonNode?> AttachTargetAsync(BridgeConnection connection, JsonObject parameters)
    {
        var tab = RequireOwnedTab(connection, ReadTabId(parameters["tabId"]));
        var targetId = parameters["targetId"]?.GetValue<string>()
                       ?? throw new InvalidOperationException("attachTarget requires a targetId.");
        var request = new JsonObject { ["targetId"] = targetId, ["flatten"] = true }.ToJsonString();
        var response = await OnUiAsync(() =>
            tab.Window.Core.CallDevToolsProtocolMethodAsync("Target.attachToTarget", request));
        return JsonNode.Parse(response) ?? new JsonObject();
    }

    private async Task<JsonNode?> DetachTargetAsync(BridgeConnection connection, JsonObject parameters)
    {
        var tab = RequireOwnedTab(connection, ReadTabId(parameters["tabId"]));
        var request = new JsonObject();
        if (parameters["sessionId"]?.GetValue<string>() is { Length: > 0 } sessionId) request["sessionId"] = sessionId;
        else if (parameters["targetId"]?.GetValue<string>() is { Length: > 0 } targetId) request["targetId"] = targetId;
        else throw new InvalidOperationException("detachTarget requires a sessionId or targetId.");

        var response = await OnUiAsync(() =>
            tab.Window.Core.CallDevToolsProtocolMethodAsync("Target.detachFromTarget", request.ToJsonString()));
        return JsonNode.Parse(response) ?? new JsonObject();
    }

    /// <summary>Real cursor movement. Hover-driven menus never open if this is answered without dispatching.</summary>
    private async Task<JsonNode?> MoveMouseAsync(BridgeConnection connection, JsonObject parameters)
    {
        var tab = RequireOwnedTab(connection, ReadTabId(parameters["tabId"]));
        var request = new JsonObject
        {
            ["type"] = "mouseMoved",
            ["x"] = ReadCoordinate(parameters["x"], "x"),
            ["y"] = ReadCoordinate(parameters["y"], "y"),
        }.ToJsonString();
        await OnUiAsync(() =>
            tab.Window.Core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", request));
        return new JsonObject();
    }

    /// <summary>
    /// Record the download the user just approved, so the window's native DownloadStarting handler lets it
    /// through to a known folder. WebView2 rejects Browser.setDownloadBehavior outright, so the CDP route this
    /// originally used could only ever have thrown.
    /// </summary>
    private async Task<JsonNode?> AllowDownloadAsync(BridgeConnection connection, JsonObject parameters)
    {
        var tab = RequireOwnedTab(connection, ReadTabId(parameters["tabId"]));
        var url = parameters["url"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException("allowDownload requires the url being downloaded.");
        await OnUiAsync(() =>
        {
            tab.Window.AllowDownload(url);
            return true;
        });
        return new JsonObject();
    }

    private JsonNode? MarkTab(BridgeConnection connection, JsonObject parameters)
    {
        var tab = RequireOwnedTab(connection, ReadTabId(parameters["tabId"]));
        tab.Status = parameters["status"]?.GetValue<string>();
        return new JsonObject();
    }

    private async Task<JsonNode?> FinalizeTabsAsync(BridgeConnection connection, JsonObject parameters)
    {
        var sessionId = RequireSession(connection);
        var keep = new Dictionary<int, string?>();
        if (parameters["keep"] is JsonArray keepArray)
        {
            foreach (var node in keepArray.OfType<JsonObject>())
            {
                var id = ReadTabId(node["tabId"]);
                keep[id] = node["status"]?.GetValue<string>();
            }
        }

        var owned = _tabs.Values
            .Where(tab => string.Equals(tab.OwnerSessionId, sessionId, StringComparison.Ordinal))
            .ToList();
        foreach (var tab in owned)
        {
            if (keep.TryGetValue(tab.Id, out var status)) tab.Status = status;
            else await CloseTabAsync(tab);
        }
        return new JsonObject();
    }

    private async Task<JsonNode?> NameSessionAsync(BridgeConnection connection, JsonObject parameters)
    {
        var sessionId = RequireSession(connection);
        var name = parameters["name"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("nameSession requires a name.");
        var tabs = _tabs.Values.Where(tab => tab.OwnerSessionId == sessionId).ToList();
        await OnUiAsync(() =>
        {
            foreach (var tab in tabs) tab.Window.SetSessionName(name);
            return true;
        });
        return new JsonObject();
    }

    /// <summary>
    /// Backs the plugin capabilities advertised by getInfo. These commands are browser-scoped: the payload
    /// carries browser_id and never tab_id, so they apply across every tab this chat owns.
    /// </summary>
    private async Task<JsonNode?> ExecuteUnhandledCommandAsync(BridgeConnection connection, JsonObject parameters)
    {
        var type = parameters["type"]?.GetValue<string>() ?? "unknown";
        return type switch
        {
            "browser_visibility_get" => await GetVisibilityAsync(connection),
            "browser_visibility_set" => await SetVisibilityAsync(connection, parameters),
            "browser_viewport_set" => await SetViewportAsync(connection, parameters),
            "browser_viewport_reset" => await ResetViewportAsync(connection),
            _ => throw new InvalidOperationException($"VibeCode Browser does not support command '{type}'."),
        };
    }

    private List<BrowserTab> OwnedTabs(BridgeConnection connection)
    {
        var sessionId = RequireSession(connection);
        return _tabs.Values
            .Where(tab => string.Equals(tab.OwnerSessionId, sessionId, StringComparison.Ordinal))
            .OrderBy(tab => tab.Id)
            .ToList();
    }

    /// <summary>
    /// A chat with no tab yet has nothing on screen, so the honest answer is false. Throwing here read to the
    /// model as a broken capability during ordinary setup, before it had opened anything.
    /// </summary>
    private async Task<JsonNode?> GetVisibilityAsync(BridgeConnection connection)
    {
        var tabs = OwnedTabs(connection);
        var visible = tabs.Count != 0 && await OnUiAsync(() => tabs.Any(tab => tab.Window.IsOnScreen));
        return new JsonObject { ["visible"] = visible };
    }

    private async Task<JsonNode?> SetVisibilityAsync(BridgeConnection connection, JsonObject parameters)
    {
        if (parameters["visible"]?.GetValue<bool>() is not { } visible)
            throw new InvalidOperationException("browser_visibility_set requires a boolean visible.");

        var tabs = OwnedTabs(connection);
        // Hiding a browser that has no window is already satisfied. Showing one is not, and must not report success.
        if (tabs.Count == 0)
        {
            if (!visible) return new JsonObject();
            throw new InvalidOperationException("This chat has no browser tab to show. Open a tab first.");
        }
        await OnUiAsync(() =>
        {
            foreach (var tab in tabs) tab.Window.SetOnScreen(visible);
            return true;
        });
        return new JsonObject();
    }

    private async Task<JsonNode?> SetViewportAsync(BridgeConnection connection, JsonObject parameters)
    {
        var width = ReadPositiveInt(parameters["width"], "width");
        var height = ReadPositiveInt(parameters["height"], "height");
        var request = new JsonObject
        {
            ["width"] = width,
            ["height"] = height,
            ["deviceScaleFactor"] = 0,
            ["mobile"] = false,
        }.ToJsonString();
        await ForEachOwnedTabAsync(connection, "Emulation.setDeviceMetricsOverride", request);
        return new JsonObject();
    }

    private async Task<JsonNode?> ResetViewportAsync(BridgeConnection connection)
    {
        await ForEachOwnedTabAsync(connection, "Emulation.clearDeviceMetricsOverride", "{}");
        return new JsonObject();
    }

    private async Task ForEachOwnedTabAsync(BridgeConnection connection, string method, string request)
    {
        var tabs = OwnedTabs(connection);
        if (tabs.Count == 0) throw new InvalidOperationException("This chat has no browser tab to adjust.");
        foreach (var tab in tabs)
            await OnUiAsync(() => tab.Window.Core.CallDevToolsProtocolMethodAsync(method, request));
    }

    private BrowserTab RequireOwnedTab(BridgeConnection connection, int tabId)
    {
        if (!_tabs.TryGetValue(tabId, out var tab)) throw new InvalidOperationException($"Tab not found: {tabId}.");
        if (!string.Equals(tab.OwnerSessionId, RequireSession(connection), StringComparison.Ordinal))
            throw new UnauthorizedAccessException("This browser tab belongs to another VibeCode chat.");
        return tab;
    }

    private Task CloseTabAsync(BrowserTab tab) => OnUiAsync(() =>
    {
        if (tab.Window.IsVisible) tab.Window.Close();
        else OnTabClosed(tab);
        return true;
    });

    private static int ReadTabId(JsonNode? node)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<int>(out var number) && number > 0) return number;
            if (value.TryGetValue<string>(out var text) && int.TryParse(text, out number) && number > 0)
                return number;
        }
        throw new InvalidOperationException("Browser tab id must be a positive integer.");
    }

    private static int ReadPositiveInt(JsonNode? node, string name)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<int>(out var number) && number > 0) return number;
            if (value.TryGetValue<double>(out var floating) && floating is > 0 and <= int.MaxValue)
                return (int)floating;
        }
        throw new InvalidOperationException($"Browser {name} must be a positive integer.");
    }

    private static double ReadCoordinate(JsonNode? node, string name)
    {
        if (node is JsonValue value && value.TryGetValue<double>(out var number)) return number;
        throw new InvalidOperationException($"Browser {name} coordinate must be a number.");
    }

    private static int? ReadOptionalInt(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<int>(out var number)) return number;
        if (value.TryGetValue<double>(out var floating) && floating is >= 0 and <= int.MaxValue)
            return (int)floating;
        return null;
    }

    private async Task<T> OnUiAsync<T>(Func<T> action)
    {
        if (_dispatcher.CheckAccess()) return action();
        return await _dispatcher.InvokeAsync(action).Task;
    }

    private async Task<T> OnUiAsync<T>(Func<Task<T>> action)
    {
        if (_dispatcher.CheckAccess()) return await action();
        var pending = await _dispatcher.InvokeAsync(action).Task;
        return await pending;
    }

    private void Log(string message)
    {
        if (string.IsNullOrWhiteSpace(_logPath)) return;
        try
        {
            lock (_logGate)
            {
                var directory = Path.GetDirectoryName(_logPath);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                File.AppendAllText(_logPath, $"{DateTime.UtcNow:O} {message}{Environment.NewLine}");
            }
        }
        catch { /* diagnostics must never affect browser availability */ }
    }

    private static string DescribeException(Exception exception)
    {
        var messages = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            var detail = $"{current.GetType().Name} (0x{current.HResult:X8}): {current.Message}";
            if (!messages.Contains(detail)) messages.Add(detail);
        }
        return messages.Count == 0 ? exception.GetType().Name : string.Join(" ", messages);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdown.Cancel();
        foreach (var connection in _connections.Values) connection.Dispose();
        _connections.Clear();

        try
        {
            if (_dispatcher.CheckAccess()) CloseAllWindows();
            else if (!_dispatcher.HasShutdownStarted)
                _dispatcher.BeginInvoke(CloseAllWindows, DispatcherPriority.Send);
        }
        catch { /* process shutdown */ }
        _shutdown.Dispose();
    }

    private void CloseAllWindows()
    {
        foreach (var tab in _tabs.Values.ToList())
        {
            try { tab.Window.Close(); }
            catch { /* process shutdown */ }
        }
        _tabs.Clear();
    }

    private sealed class BrowserTab
    {
        public BrowserTab(int id, string ownerSessionId, BrowserWindow window)
        {
            Id = id;
            OwnerSessionId = ownerSessionId;
            Window = window;
        }

        private const int MaximumHistoryEntries = 200;
        private readonly object _historyGate = new();
        private readonly List<PageVisit> _history = new();

        public int Id { get; }
        public string OwnerSessionId { get; }
        public BrowserWindow Window { get; }
        public DateTime LastActivatedUtc { get; set; } = DateTime.UtcNow;
        public string? Status { get; set; }
        public List<CoreWebView2DevToolsProtocolEventReceiver> EventReceivers { get; } = new();
        public ConcurrentDictionary<Guid, byte> AttachedConnections { get; } = new();

        public IReadOnlyList<PageVisit> History
        {
            get { lock (_historyGate) return _history.ToArray(); }
        }

        public void RecordVisit(string url)
        {
            // about:blank is the tab's own starting state, not somewhere the agent or user chose to go.
            if (string.Equals(url, "about:blank", StringComparison.OrdinalIgnoreCase)) return;
            lock (_historyGate)
            {
                if (_history.Count > 0 && string.Equals(_history[^1].Url, url, StringComparison.Ordinal)) return;
                _history.Add(new PageVisit(url, Window.CurrentTitle, DateTime.UtcNow));
                if (_history.Count > MaximumHistoryEntries) _history.RemoveAt(0);
            }
        }
    }

    private sealed record PageVisit(string Url, string? Title, DateTime VisitedUtc);

    private sealed class BridgeConnection : IDisposable
    {
        private readonly BrowserBridgeService _owner;
        private readonly NamedPipeServerStream _stream;
        private readonly CancellationTokenSource _lifetime;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly List<Task> _handlers = new();
        private bool _disposed;

        public BridgeConnection(BrowserBridgeService owner, NamedPipeServerStream stream,
            CancellationToken shutdownToken)
        {
            _owner = owner;
            _stream = stream;
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
        }

        public Guid Id { get; } = Guid.NewGuid();
        public string? SessionId { get; set; }
        public string? TurnId { get; set; }
        public ConcurrentDictionary<int, byte> AttachedTabs { get; } = new();
        public CancellationToken CancellationToken => _lifetime.Token;

        public async Task RunAsync()
        {
            try
            {
                while (!_lifetime.IsCancellationRequested && _stream.IsConnected)
                {
                    var message = await BrowserBridgeProtocol.ReadAsync(_stream, _lifetime.Token);
                    if (message is null) break;
                    if (message["method"]?.GetValue<string>() is not { Length: > 0 } method) continue;

                    var id = message["id"];
                    var parameters = message["params"] as JsonObject ?? new JsonObject();

                    // Handle concurrently. Awaiting the dispatch here would stop reading the socket until the
                    // command finished, which deadlocks any protocol where completing one command requires a
                    // second one: with Fetch interception enabled, Page.navigate cannot finish until the client
                    // answers Fetch.requestPaused with Fetch.continueRequest, and that reply would never be read.
                    // Responses stay well-formed because JSON-RPC ids correlate them and _writeLock serializes
                    // the frames.
                    Track(HandleAsync(method, id, parameters));
                }

                // Let in-flight commands finish writing their responses before the caller tears the pipe down.
                await Task.WhenAll(Outstanding()).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }

        private async Task HandleAsync(string method, JsonNode? id, JsonObject parameters)
        {
            try
            {
                var result = await _owner.DispatchAsync(this, method, parameters);
                if (method == "getInfo") _owner.Log("getInfo response: " + result?.ToJsonString());
                if (id is not null)
                    await BrowserBridgeProtocol.WriteAsync(_stream,
                        BrowserBridgeProtocol.Result(id, result), _writeLock, _lifetime.Token);
            }
            catch (BrowserMethodNotFoundException ex)
            {
                if (id is not null)
                    await BrowserBridgeProtocol.WriteAsync(_stream,
                        BrowserBridgeProtocol.Error(id, -32601, ex.Message), _writeLock, _lifetime.Token);
            }
            catch (Exception ex)
            {
                _owner.Log($"Request {method} failed: {DescribeException(ex)}");
                if (id is null) return;
                try
                {
                    await BrowserBridgeProtocol.WriteAsync(_stream,
                        BrowserBridgeProtocol.Error(id, -32000, Describe(ex)), _writeLock, _lifetime.Token);
                }
                catch { /* the peer is already gone; the read loop will notice */ }
            }
        }

        private void Track(Task handler)
        {
            lock (_handlers)
            {
                _handlers.RemoveAll(task => task.IsCompleted);
                _handlers.Add(handler);
            }
        }

        private Task[] Outstanding()
        {
            lock (_handlers) return _handlers.Where(task => !task.IsCompleted).ToArray();
        }

        public async Task NotifyAsync(string method, JsonObject parameters)
        {
            if (_disposed || !_stream.IsConnected) return;
            try
            {
                await BrowserBridgeProtocol.WriteAsync(_stream, new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["method"] = method,
                    ["params"] = parameters,
                }, _writeLock, _lifetime.Token);
            }
            catch
            {
                Dispose();
            }
        }

        private static string Describe(Exception exception)
            => DescribeException(exception);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _lifetime.Cancel(); }
            catch { }
            _writeLock.Dispose();
            _lifetime.Dispose();
        }
    }

    private sealed class BrowserMethodNotFoundException : Exception
    {
        public BrowserMethodNotFoundException(string method)
            : base($"No handler registered for method: {method}") { }
    }
}
