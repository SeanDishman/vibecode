using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using VibeCode.Services;

namespace VibeCode.UI;

internal static class MitreReportReader
{
    private sealed class CachedReport
    {
        public string? Text;
        public MitreTacticReport? Report;
    }

    // Weak keys release closed chats. A one-second refresh never reparses unchanged message text.
    private static readonly ConditionalWeakTable<TextItem, CachedReport> Cache = new();

    public static MitreTacticReport? Latest(IList<ItemVm> items)
    {
        for (var i = items.Count - 1; i >= 0; i--)
        {
            if (items[i] is UserItem) break; // a new prompt starts with an unknown tactic
            if (items[i] is not TextItem text) continue;
            var cached = Cache.GetOrCreateValue(text);
            if (!ReferenceEquals(cached.Text, text.Text))
            {
                cached.Text = text.Text;
                cached.Report = MitreTacticCatalog.ParseReport(text.Text);
            }
            if (cached.Report is not null) return cached.Report;
        }
        return null;
    }
}

internal sealed class MitreTacticCell : Observable
{
    public MitreTacticCell(MitreTactic tactic, string? label = null) { Tactic = tactic; Label = label ?? tactic.Name; }
    public MitreTactic Tactic { get; }
    public string Label { get; }
    public string Description => $"{Tactic.Id} · {Tactic.Name}";
    private bool _isCurrent;
    public bool IsCurrent { get => _isCurrent; set => Set(ref _isCurrent, value); }
}

internal sealed class MitreAgentRow : Observable
{
    public MitreAgentRow(string id) => Id = id;
    public string Id { get; }
    private string _label = "", _detail = "", _status = "", _tacticName = "Not reported", _tacticId = "", _evidence = "";
    private bool _working, _hasTactic;
    public string Label { get => _label; private set => Set(ref _label, value); }
    private string _taskTitle = "Waiting for task summary";
    public string TaskTitle { get => _taskTitle; private set => Set(ref _taskTitle, value); }
    public string Detail { get => _detail; private set => Set(ref _detail, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string TacticName { get => _tacticName; private set => Set(ref _tacticName, value); }
    public string TacticId { get => _tacticId; private set => Set(ref _tacticId, value); }
    public string Evidence { get => _evidence; private set => Set(ref _evidence, value); }
    private string _activity = "", _lastOperation = "", _accessSummary = "", _accessDetail = "", _telemetryDetail = "";
    public string Activity { get => _activity; private set => Set(ref _activity, value); }
    public string LastOperation { get => _lastOperation; private set => Set(ref _lastOperation, value); }
    public string AccessSummary { get => _accessSummary; private set => Set(ref _accessSummary, value); }
    public string AccessDetail { get => _accessDetail; private set => Set(ref _accessDetail, value); }
    public string TelemetryDetail { get => _telemetryDetail; private set => Set(ref _telemetryDetail, value); }
    private long? _totalTokens;
    public long? TotalTokens
    {
        get => _totalTokens;
        private set { if (Set(ref _totalTokens, value)) Raise(nameof(TokensText)); }
    }
    public string TokensText => TotalTokens is { } tokens ? $"{tokens:N0} tokens" : "Tokens —";
    private string _aiActivity = "", _aiStep = "";
    private string _headline = "Waiting for an activity report", _step = "No step reported yet", _stage = "Not reported";
    private string _currentTool = "";
    public string Headline { get => _headline; private set => Set(ref _headline, value); }
    public string Step { get => _step; private set => Set(ref _step, value); }
    public string Stage { get => _stage; private set => Set(ref _stage, value); }
    public string CurrentTool
    {
        get => _currentTool;
        private set { if (Set(ref _currentTool, value)) Raise(nameof(HasCurrentTool)); }
    }
    public bool HasCurrentTool => !string.IsNullOrWhiteSpace(CurrentTool);
    private bool _hasAiReport;
    public string AiActivity { get => _aiActivity; private set => Set(ref _aiActivity, value); }
    public string AiStep { get => _aiStep; private set => Set(ref _aiStep, value); }
    public bool HasAiReport
    {
        get => _hasAiReport;
        private set { if (Set(ref _hasAiReport, value)) Raise(nameof(ReportLabel)); }
    }
    public bool HasTactic { get => _hasTactic; private set => Set(ref _hasTactic, value); }
    public bool Working
    {
        get => _working;
        private set { if (Set(ref _working, value)) Raise(nameof(ReportLabel)); }
    }
    public string ReportLabel => (HasAiReport ? "MCP REPORT" : "REPORTED TACTIC") + (Working ? " · THIS TURN" : " · LAST TURN");
    // A focused display vocabulary. All other reported tactics remain available in the report/details;
    // this strip is not an execution sequence and never directs what the agent does next.
    public ObservableCollection<MitreTacticCell> Tactics { get; } = new(
        new[] { ("TA0043", "Recon"), ("TA0007", "Discovery"), ("TA0001", "Initial access"),
            ("TA0002", "Execution"), ("TA0004", "Privilege escalation"), ("TA0009", "Collection"),
            ("TA0010", "Exfiltration"), ("TA0040", "Impact") }
            .Select(pair => new MitreTacticCell(MitreTacticCatalog.Tactics.Single(t => t.Id == pair.Item1), pair.Item2)));

    public void Update(string label, string detail, string status, bool working, MitreTacticReport? report,
        MitreRuntimeState? runtime, MitreSessionTelemetry telemetry)
    {
        // Live reports have a provider turn identity. Do not let an old transcript message that arrives
        // late overwrite the current tactic; transcript-only rows remain useful for restored history.
        if (runtime?.TurnId is not null)
        {
            report = runtime.TacticReported ? new MitreTacticReport(
                MitreTacticCatalog.Tactics.FirstOrDefault(t => t.Id == runtime.TacticId),
                runtime.TacticId is null ? "This AI reported no clear ATT&CK mapping for its work."
                    : "Reported by this AI in its latest completed runtime update.") : null;
            working = runtime.Working;
            status = working ? status : runtime.Activity;
            if (working || runtime.Activity == "Turn complete")
                status = runtime.AgentReport?.State switch { "blocked" => "Blocked", "waiting" => "Waiting", _ => status };
        }
        Label = label;
        TaskTitle = runtime?.AgentReport?.TaskTitle ?? runtime?.AgentReport?.Activity ?? "Waiting for task summary";
        Detail = detail;
        Status = status switch { "running" => "Working", "idle" => "Idle", "completed" => "Complete", _ => status };
        Working = working;
        HasTactic = report?.Tactic is not null;
        TacticName = report?.Tactic?.Name ?? (report is null ? "Not reported" : "Unmapped");
        TacticId = report?.Tactic?.Id ?? "";
        Evidence = report?.Evidence ?? "Waiting for this AI to report a tactic.";
        Activity = runtime?.Activity ?? "No live runtime events yet";
        TotalTokens = runtime?.TotalTokens;
        HasAiReport = runtime?.AgentReport is not null;
        AiActivity = runtime?.AgentReport is { } ai ? "AI report: " + ai.Activity : "";
        AiStep = runtime?.AgentReport is { } step ? (step.Step is null ? "" : "Step: " + step.Step + " · ") + "Reported state: " + step.State : "";
        Headline = runtime?.AgentReport?.Activity ?? (runtime?.TurnId is not null ? Activity : "Waiting for an activity report");
        Step = runtime?.AgentReport?.Step ?? "No step reported yet";
        Stage = TacticName;
        CurrentTool = runtime?.CurrentToolLine ?? "";
        LastOperation = string.IsNullOrEmpty(runtime?.LastOperation) ? "" : "Last operation: " + runtime.LastOperation;
        AccessSummary = runtime?.AccessSummary ?? "Access not reported by runtime";
        AccessDetail = runtime?.AccessDetail ?? "No policy reported for this agent. Parent permissions are not proof of child access.";
        TelemetryDetail = telemetry.LogError ?? (!AppSettings.Current.MitreMonitorEnabled ? "Event logging disabled in Settings"
            : $"{telemetry.LoggedEvents} session events saved" + (runtime?.UpdatedAt is { } time
                ? $" · last event {time.ToLocalTime():HH:mm:ss}" : " · awaiting live telemetry"));
        foreach (var cell in Tactics) cell.IsCurrent = cell.Tactic.Id == report?.Tactic?.Id;
    }
}

internal sealed class MitreMonitorViewModel : Observable
{
    private readonly MainViewModel _main;
    private readonly Dictionary<string, MitreAgentRow> _rows = new(StringComparer.Ordinal);
    public MitreMonitorViewModel(MainViewModel main) => _main = main;
    public ObservableCollection<MitreAgentRow> Agents { get; } = new();
    public ObservableCollection<MitreAgentRow> PageAgents { get; } = new();
    private const int AgentsPerPage = 4;
    private int _pageIndex;
    public int PageCount => Math.Max(1, (Agents.Count + AgentsPerPage - 1) / AgentsPerPage);
    public string PageLabel => $"{_pageIndex + 1} / {PageCount}";
    public bool CanPreviousPage => _pageIndex > 0;
    public bool CanNextPage => _pageIndex + 1 < PageCount;
    public Visibility PagerVisibility => PageCount > 1 ? Visibility.Visible : Visibility.Collapsed;
    public void PreviousPage() { if (CanPreviousPage) { _pageIndex--; RefreshPage(); } }
    public void NextPage() { if (CanNextPage) { _pageIndex++; RefreshPage(); } }
    private string _summary = "No Codex sessions", _clock = "";
    public string Summary { get => _summary; private set => Set(ref _summary, value); }
    public string Clock { get => _clock; private set => Set(ref _clock, value); }
    public Visibility EmptyVisibility => Agents.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public int WorkingCount => Agents.Count(a => a.Working);
    public int AgentCount => Agents.Count;
    private double _cardWidth = 850;
    public double CardWidth
    {
        get => _cardWidth;
        set { if (Set(ref _cardWidth, value)) Raise(nameof(TacticColumns)); }
    }
    public int TacticColumns => CardWidth >= 850 || (CompactCards && CardWidth >= 550) ? 8 : 4;
    private double _cardHeight = double.NaN;
    public double CardHeight
    {
        get => _cardHeight;
        set { if (Set(ref _cardHeight, value)) { Raise(nameof(CompactCards)); Raise(nameof(TacticColumns)); } }
    }
    private bool _fullScreen;
    public bool FullScreen
    {
        get => _fullScreen;
        set { if (Set(ref _fullScreen, value)) { Raise(nameof(ContentMaxWidth)); Raise(nameof(CompactCards)); Raise(nameof(TacticColumns)); } }
    }
    public double ContentMaxWidth => FullScreen ? double.PositiveInfinity : 1480;
    public bool CompactCards => FullScreen && CardHeight < 380;

    private void RefreshPage()
    {
        _pageIndex = Math.Clamp(_pageIndex, 0, PageCount - 1);
        var page = Agents.Skip(_pageIndex * AgentsPerPage).Take(AgentsPerPage).ToArray();
        // Preserve card instances during live updates so expanding details does not reset every second.
        for (var i = PageAgents.Count - 1; i >= 0; i--)
            if (!page.Contains(PageAgents[i])) PageAgents.RemoveAt(i);
        for (var i = 0; i < page.Length; i++)
        {
            var oldIndex = PageAgents.IndexOf(page[i]);
            if (oldIndex < 0) PageAgents.Insert(i, page[i]);
            else if (oldIndex != i) PageAgents.Move(oldIndex, i);
        }
        Raise(nameof(PageCount));
        Raise(nameof(PageLabel));
        Raise(nameof(CanPreviousPage));
        Raise(nameof(CanNextPage));
        Raise(nameof(PagerVisibility));
    }

    public void Refresh()
    {
        var visible = new List<MitreAgentRow>();
        foreach (var chat in _main.Chats.Concat(_main.LiveBridgePeers).Distinct())
        {
            if (chat.Status == "closed" || !chat.IsCodex || !MitreTacticCatalog.IsCodexModel(chat.MitreReportingModel))
                continue;
            var label = chat.IsBridgeAgent ? chat.BridgeLabel : chat.Title;
            if (string.IsNullOrWhiteSpace(label)) label = "Codex chat";
            var project = Path.GetFileName(chat.Cwd.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            Add(chat.BridgeId, label, $"{chat.ModelDisplay} · {project}",
                chat.IsWorking ? "Working" : chat.Status, chat.IsWorking, MitreReportReader.Latest(chat.Items),
                chat.MitreTelemetry.Get(), chat.MitreTelemetry);
            foreach (var child in chat.Subagents.Where(a => !a.IsAbandoned))
            {
                // The provider roster does not report child models. Name the parent relationship explicitly;
                // children may use another model and must never borrow the parent's tactic or transcript.
                var report = MitreReportReader.Latest(child.Transcript);
                if (child.Transcript.Count == 0) report = MitreTacticCatalog.ParseReport(child.Activity);
                Add($"{chat.BridgeId}/{child.ThreadId}", child.Label, $"Child of {label}",
                    child.StatusText, child.IsActive, report, chat.MitreTelemetry.Get(child.ThreadId), chat.MitreTelemetry);
            }
        }

        var ids = visible.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var stale in _rows.Keys.Where(id => !ids.Contains(id)).ToArray()) _rows.Remove(stale);
        for (var i = Agents.Count - 1; i >= 0; i--)
            if (!ids.Contains(Agents[i].Id)) Agents.RemoveAt(i);
        for (var i = 0; i < visible.Count; i++)
        {
            var oldIndex = Agents.IndexOf(visible[i]);
            if (oldIndex < 0) Agents.Insert(i, visible[i]);
            else if (oldIndex != i) Agents.Move(oldIndex, i);
        }
        Summary = Agents.Count == 0 ? "No Codex sessions"
            : $"{Agents.Count} AI{(Agents.Count == 1 ? "" : "s")} · {Agents.Count(a => a.Working)} working · {Agents.Count(a => a.HasTactic)} reporting a tactic";
        Clock = DateTime.Now.ToString("HH:mm:ss");
        Raise(nameof(EmptyVisibility));
        Raise(nameof(WorkingCount));
        Raise(nameof(AgentCount));
        RefreshPage();

        void Add(string id, string label, string detail, string status, bool working, MitreTacticReport? report,
            MitreRuntimeState? runtime, MitreSessionTelemetry telemetry)
        {
            if (!_rows.TryGetValue(id, out var row)) _rows[id] = row = new MitreAgentRow(id);
            row.Update(label, detail, status, working, report, runtime, telemetry);
            visible.Add(row);
        }
    }
}
