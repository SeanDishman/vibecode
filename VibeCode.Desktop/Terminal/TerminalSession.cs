using System.Diagnostics;
using System.ComponentModel;
using System.Collections.Specialized;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Windows.Threading;
using VibeCode.Services;
using VibeCode.UI;

namespace VibeCode.Terminal;

/// <summary>Owns only a console connection. Agents and their saved conversations stay owned by MainViewModel.</summary>
public sealed partial class TerminalSession : IDisposable
{
    private readonly MainViewModel _model;
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly NamedPipeServerStream _pipe;
    private readonly DispatcherTimer _refresh;
    private readonly DispatcherTimer _idleTimer;
    private readonly DispatcherTimer _dictationProgress;
    private readonly TerminalDictation _dictation;
    private readonly HashSet<INotifyPropertyChanged> _observed = [];
    private readonly HashSet<INotifyCollectionChanged> _collections = [];
    private readonly Dictionary<ChatViewModel, DateTimeOffset> _idleSince = [];
    private Process? _process;
    private string? _lastSnapshot;
    private bool _disposed;
    private bool _snapshotWriting;
    private bool _dirty = true, _bindingsDirty = true, _connected;
    public ChatViewModel Host { get; private set; }
    public event Action? DesktopRequested;
    public event Action? Ended;

    public TerminalSession(MainViewModel model, ChatViewModel host, Dispatcher dispatcher, ITerminalSpeechSource? speech = null,
        ITerminalAttachmentSource? attachments = null)
    {
        _model = model;
        Host = host;
        _dispatcher = dispatcher;
        _attachmentSource = attachments ?? new DesktopTerminalAttachments();
        PipeName = TerminalWire.PipePrefix + Guid.NewGuid().ToString("N");
        _pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        _refresh = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background,
            (_, _) => PublishSnapshot(), dispatcher);
        _refresh.Stop();
        _idleTimer = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background,
            (_, _) => ReleaseIdleAgents(DateTimeOffset.UtcNow), dispatcher);
        _idleTimer.Stop();
        _dictation = new TerminalDictation(speech ?? new DesktopTerminalSpeech(), host.BridgeAgentId, action =>
        {
            if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(action);
        });
        _dictationProgress = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
            (_, _) => _dictation.RefreshProgress(), dispatcher);
        _dictationProgress.Stop();
        _dictation.PacketReady += packet =>
        {
            if (_dictation.NeedsProgressUpdates) _dictationProgress.Start(); else _dictationProgress.Stop();
            if (_connected && !_disposed) _ = SendSafelyAsync(packet);
        };
        _model.PropertyChanged += OnModelChanged;
        AppSettings.Changed += OnSettingsChanged;
    }

    public string PipeName { get; }

    public void SwitchHost(ChatViewModel host)
    {
        if (ReferenceEquals(Host, host)) return;
        _dictation.SwitchContext(host.BridgeAgentId);
        Host = host;
        _launchConfiguration = null;
        _lastSnapshot = null;
        _idleSince.Clear();
        _bindingsDirty = true;
        MarkDirty();
    }

    public async Task StartAsync()
    {
        var executable = Environment.ProcessPath ?? throw new IOException("Could not locate VibeCode.exe.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Host.Cwd };
        start.ArgumentList.Add("--vibecode-terminal");
        start.ArgumentList.Add(PipeName);
        // This is the user-requested interactive console, so it deliberately gets its own visible window.
        _process = Process.Start(start) ?? throw new IOException("Windows could not open Vibe Code Terminal.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
            await _pipe.WaitForConnectionAsync(timeout.Token);
            var hello = await TerminalWire.ReadAsync(_pipe, timeout.Token);
            if (hello?.Type != "hello") throw new IOException("The terminal did not complete its connection.");
            _connected = true;
            ObserveVisibleState();
            await SendAsync(Snapshot());
            _dirty = false;
            _idleTimer.Start();
            _ = ReceiveAsync();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Focus()
    {
        if (_process is null || _process.HasExited) return;
        // Console window handles may belong to conhost rather than the client process. The client focuses its
        // own console on receipt, which also works after it has been minimized.
        _ = SendSafelyAsync(new("focus"));
    }

    private async Task ReceiveAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested && await TerminalWire.ReadAsync(_pipe, _lifetime.Token) is { } packet)
            {
                if (packet.Type == "exit") break;
                if (packet.Type == "attachment")
                {
                    var response = await (await _dispatcher.InvokeAsync(() => HandleAttachmentsAsync(packet)));
                    await SendAsync(response);
                    continue;
                }
                if (packet.Type == "dictation")
                {
                    await _dispatcher.InvokeAsync(() => _dictation.Handle(packet.Text));
                    continue;
                }
                if (packet.Type == "configure")
                {
                    var configured = await _dispatcher.InvokeAsync(() => HandleSetting(packet.Setting));
                    await SendAsync(configured);
                    continue;
                }
                if (packet.Type != "input") continue;
                var result = await _dispatcher.InvokeAsync(() => HandleSubmission(packet));
                await SendAsync(result);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or JsonException)
        {
            // A closed console or desktop is an ordinary disconnect. No provider session is interrupted.
        }
        finally
        {
            if (!_dispatcher.HasShutdownStarted)
                await _dispatcher.InvokeAsync(() => { Dispose(); Ended?.Invoke(); });
        }
    }

    public TerminalPacket HandleInput(string input)
    {
        var text = input.Trim();
        if (text.Length == 0 && Host.Attachments.Count == 0) return new("ack");
        if (text.Length > TerminalWire.MaxPromptLength) return new("ack", "Prompt is too long (100,000 characters maximum).", false);
        try
        {
            if (text.Equals("/desktop", StringComparison.OrdinalIgnoreCase)) { DesktopRequested?.Invoke(); return new("ack", "Desktop opened. Agents keep running."); }
            if (text.Equals("/new", StringComparison.OrdinalIgnoreCase))
            {
                var launch = _launchConfiguration;
                SwitchHost(_model.CreateTerminalTeam(Host));
                _launchConfiguration = launch;
                return new("ack", "Started a new team. The previous team's work remains in VibeCode.");
            }
            if (TryRespond(text, out var response)) return response;
            if (text.Equals("/sleep", StringComparison.OrdinalIgnoreCase))
            {
                var slept = _model.TerminalTeamRoster(Host).Count(a => a.SleepWhileIdle());
                return new("ack", $"Released {slept} eligible idle agent process(es). Their sessions, tools and mailboxes resume when needed.");
            }
            if (Host.Attachments.Count > 0 && AgentTeamPolicy.TryParseMessage(text, out _))
                return new("ack", "/message and /msg send text only. Send a new prompt with these files, or remove them first. Your draft is kept.", false);
            if (_model.TryRunTerminalTeamCommand(Host, text)) return new("ack");
            if (text.StartsWith('/')) return new("ack", "Unknown command. Use /help, or /message <agent> <text> to address a teammate.", false);
            var attachments = Host.Attachments.ToArray();
            if (_model.SendToTerminalTeam(Host, text, NextConfiguration(), attachments))
            {
                foreach (var attachment in attachments) Host.Attachments.Remove(attachment);
                MarkDirty();
                return new("ack");
            }
            return new("ack", $"No agent could take this prompt. The team limit is {AppSettings.Current.BridgeAgentLimit}. " +
                "Wait for a teammate to finish, use /new for another team, or /desktop to check accounts and the limit. Your draft is kept.", false);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "Terminal.Input");
            return new("ack", "Could not send: " + ex.Message + " Your draft is kept.", false);
        }
    }

    private bool TryRespond(string text, out TerminalPacket response)
    {
        response = new("ack");
        var parts = text.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        var command = parts[0].ToLowerInvariant();
        if (command is not ("/allow" or "/deny" or "/answer")) return false;
        if (parts.Length < 2 || !long.TryParse(parts[1], out var id))
        {
            response = new("ack", "Use /allow <request>, /deny <request> [reason], or /answer <request> <answer>.", false);
            return true;
        }
        var match = _model.TerminalTeamRoster(Host).SelectMany(agent => agent.Items.OfType<PermItem>().Select(item => (agent, item)))
            .FirstOrDefault(pair => pair.item.DisplaySequence == id && pair.item.IsPending);
        if (match.item is null) { response = new("ack", "That request is no longer pending. Use /requests to see current requests.", false); return true; }
        var (owner, request) = match;
        var value = parts.Length > 2 ? parts[2] : "";
        if (command == "/answer")
        {
            var answers = value.Split('|', StringSplitOptions.TrimEntries);
            if (request.Kind != "question" || answers.Length != request.Questions.Count || answers.Any(string.IsNullOrWhiteSpace))
            {
                response = new("ack", "Answer each question in order, separated by |. Example: /answer 42 first answer | second answer", false);
                return true;
            }
            for (var i = 0; i < answers.Length; i++)
            {
                foreach (var option in request.Questions[i].Options) option.Selected = false;
                request.Questions[i].Custom = answers[i];
            }
            owner.AnswerQuestion(request);
        }
        else if (command == "/allow" && request.Kind == "question")
        { response = new("ack", "Use /answer for a question; it has no default approval.", false); return true; }
        else if (request.Kind == "plan") owner.DecidePlan(request, command == "/allow", false, value);
        else owner.RespondPermission(request, command == "/allow", denyMessage: value);
        response = new("ack", request.IsPending ? "The request could not be answered; open /desktop to inspect it." : "Response sent.", !request.IsPending);
        return true;
    }

    public TerminalPacket Snapshot()
    {
        var roster = _model.TerminalTeamRoster(Host);
        var agents = roster.Select(a => new TerminalAgent(a.AgentTeamNumber,
            a.BridgeTerminalIdentity, a.AgentDisplay, a.ChosenModel ?? "default", a.IsWaitingForAgentTeamPrompt ? "ready" : a.IsIdleAsleep ? "sleeping" : a.Status)).ToArray();
        var all = roster.SelectMany(a => RecentItems(a).Select(item => (owner: a, item))).OrderBy(pair => pair.item.DisplaySequence);
        // Pending questions stay visible even when a busy teammate has produced many newer rows.
        var pending = roster.SelectMany(a => a.Items.OfType<PermItem>().Where(p => p.IsPending).Select(p => (owner: a, item: (ItemVm)p))).ToArray();
        // Bound the display bytes as well as the row count. Full context/history remains in the desktop.
        // Reserve room for every pending request before filling the budget with the newest transcript rows.
        var rows = new List<TerminalRow>();
        var budget = 128_000;
        var requestLimit = Math.Min(12_000, 64_000 / Math.Max(1, pending.Length));
        foreach (var pair in pending)
        {
            var row = LimitRow(Present(pair.owner, pair.item), requestLimit);
            rows.Add(row); budget -= row.Text.Length;
        }
        foreach (var pair in all.TakeLast(240).Reverse().Where(p => p.item is not PermItem { IsPending: true }))
        {
            if (budget <= 0) break;
            var row = LimitRow(Present(pair.owner, pair.item), budget);
            if (row.Text.Length == 0) continue;
            rows.Add(row); budget -= row.Text.Length;
        }
        return new("snapshot", AppSettings.Current.BridgePeerMessaging ? "Peer communication on" : "Peer communication off in Bridge settings",
            Agents: agents, Rows: rows.OrderBy(r => r.Id).ToArray(), Project: Host.Cwd, Controls: ControlSnapshot(),
            Speech: _dictation.Snapshot(), ContextId: Host.BridgeAgentId, Attachments: AttachmentSnapshot());
    }

    private static IEnumerable<ItemVm> RecentItems(ChatViewModel agent)
    {
        var count = 0;
        for (var i = agent.Items.Count - 1; i >= 0 && count < 240; i--)
            // Count conversation rows, not tool activity. A long run of lookups must not evict the last reply.
            if (agent.Items[i] is UserItem or TextItem or BannerItem or QueuedItem or PermItem { IsPending: true })
            { count++; yield return agent.Items[i]; }
    }

    private static TerminalRow LimitRow(TerminalRow row, int limit)
    {
        if (row.Text.Length <= limit) return row;
        const string suffix = "\n[More in /desktop]";
        return row with { Text = limit < suffix.Length ? row.Text[..limit] : row.Text[..(limit - suffix.Length)] + suffix };
    }

    private static TerminalRow Present(ChatViewModel owner, ItemVm item)
    {
        var (kind, text) = item switch
        {
            UserItem user => (user.IsUserPrompt ? "user" : "peer", user.Text +
                (user.Attachments is { Count: > 0 } ? "\nAttached: " + string.Join(", ", user.Attachments.Select(a => a.FileName)) : "")),
            TextItem reply => ("reply", reply.Text),
            BannerItem banner => (banner.Level is "error" or "auth" ? "error" : "notice", banner.Text),
            PermItem permission => (permission.IsPending ? "request" : "notice", PermissionText(permission)),
            QueuedItem queued => ("notice", "Queued: " + queued.Text),
            _ => ("notice", ""),
        };
        if (text.Length > 12_000) text = text[..12_000] + "\n[More in /desktop]";
        return new(item.DisplaySequence, owner.BridgeTerminalIdentity, kind, text);
    }

    private static string PermissionText(PermItem item)
    {
        if (!item.IsPending) return $"Request {item.DisplaySequence}: {item.DecisionText}";
        if (item.Kind == "question") return $"Request {item.DisplaySequence} — answer needed\n" +
            string.Join('\n', item.Questions.Select((q, i) => $"{i + 1}. {q.Question}\n" + string.Join('\n', q.Options.Select(o => "   " + o.Label + " — " + o.Description)))) +
            $"\n/answer {item.DisplaySequence} <answer> (separate multiple answers with |)";
        return $"Request {item.DisplaySequence} — {item.ToolName}\n{item.Summary}\n" +
            (item.Kind == "plan" ? item.PlanMarkdown : item.BodyText) +
            $"\n/allow {item.DisplaySequence}  /deny {item.DisplaySequence} [reason]";
    }

    private async void PublishSnapshot()
    {
        _refresh.Stop();
        if (_disposed || !_connected) return;
        if (_snapshotWriting) { _refresh.Start(); return; }
        if (!_dirty) return;
        _dirty = false;
        _snapshotWriting = true;
        try
        {
            if (_bindingsDirty) ObserveVisibleState();
            var snapshot = Snapshot();
            var serialized = JsonSerializer.Serialize(snapshot);
            if (serialized == _lastSnapshot) return;
            await SendAsync(snapshot);
            _lastSnapshot = serialized;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        { Dispose(); }
        finally { _snapshotWriting = false; if (_dirty && !_disposed) _refresh.Start(); }
    }

    private void MarkDirty()
    {
        if (_disposed) return;
        if (!_dispatcher.CheckAccess()) { _dispatcher.BeginInvoke(new Action(MarkDirty)); return; }
        _dirty = true;
        if (_connected && !_refresh.IsEnabled) _refresh.Start();
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e) => MarkDirty();
    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) { _bindingsDirty = true; MarkDirty(); }
    private void OnSettingsChanged() => MarkDirty();
    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is "IsBridge" or "ShowBridge" or "SecondaryShowBridge" or "BridgePanes" or "SecondaryBridgePanes")
        { _bindingsDirty = true; MarkDirty(); }
    }

    private void ObserveVisibleState()
    {
        _bindingsDirty = false;
        var next = new HashSet<INotifyPropertyChanged>();
        var collections = new HashSet<INotifyCollectionChanged>();
        collections.Add(Host.Attachments);
        var roster = _model.TerminalTeamRoster(Host);
        if (roster is INotifyCollectionChanged changingRoster) collections.Add(changingRoster);
        foreach (var agent in roster)
        {
            next.Add(agent);
            collections.Add(agent.Items);
            collections.Add(agent.Models);
            // Follow the same visible window as Snapshot, including replies older than a burst of tools.
            // Tool output stays in the desktop history and must not schedule terminal redraws.
            foreach (var item in RecentItems(agent).Concat(agent.Items.OfType<PermItem>().Where(p => p.IsPending)).Distinct())
                next.Add(item);
        }
        foreach (var old in _observed.Except(next)) old.PropertyChanged -= OnStateChanged;
        foreach (var added in next.Except(_observed)) added.PropertyChanged += OnStateChanged;
        foreach (var old in _collections.Except(collections)) old.CollectionChanged -= OnCollectionChanged;
        foreach (var added in collections.Except(_collections)) added.CollectionChanged += OnCollectionChanged;
        _observed.Clear(); _observed.UnionWith(next);
        _collections.Clear(); _collections.UnionWith(collections);
    }

    /// <summary>Eligible completed processes release RAM sooner and resume the same session when a new prompt needs them.
    /// The existing idle-sleep guard retains pending tools, questions, subagents, background jobs and recovery work.</summary>
    public int ReleaseIdleAgents(DateTimeOffset now)
    {
        if (AppSettings.Current.IdleSleepMinutes <= 0) return 0;
        var roster = _model.TerminalTeamRoster(Host);
        var slept = 0;
        foreach (var old in _idleSince.Keys.Except(roster).ToArray()) _idleSince.Remove(old);
        foreach (var agent in roster)
        {
            if (agent.IdleSleepBlocker is not null) { _idleSince.Remove(agent); continue; }
            if (!_idleSince.TryGetValue(agent, out var since)) { _idleSince[agent] = now; continue; }
            var activity = new[] { since, agent.LastActivityAt ?? since, agent.LastAgentActivityAt ?? since }.Max();
            if (now - activity >= TimeSpan.FromSeconds(45) && agent.SleepWhileIdle()) { _idleSince.Remove(agent); slept++; }
        }
        return slept;
    }

    private async Task SendSafelyAsync(TerminalPacket packet)
    {
        try { await SendAsync(packet); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    private async Task SendAsync(TerminalPacket packet)
    {
        await _writer.WaitAsync(_lifetime.Token);
        try { await TerminalWire.WriteAsync(_pipe, packet, _lifetime.Token); }
        finally { _writer.Release(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _refresh.Stop();
        _idleTimer.Stop();
        _dictationProgress.Stop();
        _dictation.Dispose();
        _model.PropertyChanged -= OnModelChanged;
        AppSettings.Changed -= OnSettingsChanged;
        foreach (var item in _observed) item.PropertyChanged -= OnStateChanged;
        foreach (var collection in _collections) collection.CollectionChanged -= OnCollectionChanged;
        _observed.Clear(); _collections.Clear(); _idleSince.Clear();
        _lifetime.Cancel();
        _pipe.Dispose();
        _process?.Dispose();
        // Pending IO owns the semaphore/token until its continuations have completed. They are managed objects;
        // do not dispose them under an in-flight WriteAsync/Release.
    }
}
