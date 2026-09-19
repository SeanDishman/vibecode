using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace VibeCode.UI;

/// <summary>
/// A single subagent inside a running Workflow (the CLI's "ultracode" multi-agent orchestrator).
/// </summary>
public sealed class WorkflowAgentVm : Observable
{
    private string _state = "start";
    private double _tokens, _toolCalls, _durationMs;
    private string _resultPreview = "";

    public required int Index { get; init; }
    public required string Label { get; set; }
    public string Model { get; set; } = "";
    public string PromptPreview { get; set; } = "";

    /// <summary>
    /// The run's own id for this agent — what names its transcript file, <c>agent-&lt;id&gt;.jsonl</c>, inside the
    /// run's directory. Empty until the agent actually STARTS: the queued event carries no id, every event after
    /// it does. That is the only honest link between this row and its conversation on disk, so an agent still
    /// waiting for a concurrency slot correctly has nothing to show.
    /// </summary>
    public string AgentId { get; set; } = "";

    /// <summary>start | done | error — the CLI's own vocabulary, kept verbatim.</summary>
    public string State
    {
        get => _state;
        set { if (Set(ref _state, value)) { Raise(nameof(IsRunning)); Raise(nameof(Mark)); Raise(nameof(StatusKey)); } }
    }

    public double Tokens { get => _tokens; set { if (Set(ref _tokens, value)) Raise(nameof(Stats)); } }
    public double ToolCalls { get => _toolCalls; set { if (Set(ref _toolCalls, value)) Raise(nameof(Stats)); } }
    public double DurationMs { get => _durationMs; set { if (Set(ref _durationMs, value)) Raise(nameof(Stats)); } }
    public string ResultPreview
    {
        get => _resultPreview;
        set { if (Set(ref _resultPreview, value)) Raise(nameof(HasResult)); }
    }

    public bool IsRunning => _state == "start";
    public bool HasResult => _resultPreview.Length > 0;

    /// <summary>Segoe Fluent glyph: running / finished / failed.</summary>
    public string Mark => _state switch { "done" => "", "error" => "", _ => "" };

    /// <summary>Feeds the shared StatusBrushConverter, so an agent row colours like every other status in the app.</summary>
    public string StatusKey => _state switch { "done" => "done", "error" => "error", _ => "running" };

    /// <summary>"16.2k tokens · 3 tools · 1.9s" — omits whatever has not happened yet, so a just-started agent
    /// shows nothing rather than a row of zeroes.</summary>
    public string Stats
    {
        get
        {
            var parts = new List<string>();
            if (_tokens > 0) parts.Add($"{ChatViewModel.FmtTokens(_tokens)} tokens");
            if (_toolCalls > 0) parts.Add($"{_toolCalls:0} tool{(_toolCalls == 1 ? "" : "s")}");
            if (_durationMs > 0) parts.Add(WorkflowRun.Duration(_durationMs));
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>One phase() group. Agents are listed under the phase they were assigned to.</summary>
public sealed class WorkflowPhaseVm : Observable
{
    public required int Index { get; init; }
    public required string Title { get; set; }
    public ObservableCollection<WorkflowAgentVm> Agents { get; } = new();
    public bool HasAgents => Agents.Count > 0;
    public void RaiseAgents() { Raise(nameof(HasAgents)); Raise(nameof(Caption)); }

    /// <summary>"3 of 5" once anything has finished — the per-phase version of the headline.</summary>
    public string Caption
    {
        get
        {
            if (Agents.Count == 0) return "";
            var done = Agents.Count(a => a.State is "done" or "error");
            return done == Agents.Count ? $"{Agents.Count} done" : $"{done} of {Agents.Count}";
        }
    }
}

/// <summary>
/// A whole Workflow run, rebuilt from the CLI's <c>task_progress.workflow_progress</c> snapshots.
///
/// The stream sends the ENTIRE progress array every time rather than deltas, and it attaches it to only some of
/// the <c>task_progress</c> events — so this applies a snapshot over the existing view models (matching agents by
/// index) instead of rebuilding the collections, which would both flicker the UI and throw away the previous
/// state on the bare progress events in between.
/// </summary>
public sealed class WorkflowRun : Observable
{
    private string _name = "";
    private string _description = "";
    private bool _finished;

    public ObservableCollection<WorkflowPhaseVm> Phases { get; } = new();

    public string Name { get => _name; set { if (Set(ref _name, value)) Raise(nameof(Title)); } }
    public string Description { get => _description; set { if (Set(ref _description, value)) Raise(nameof(Title)); } }

    /// <summary>What the card calls itself: the description reads better than the script's slug, but a workflow
    /// launched without one still has to say something.</summary>
    public string Title => _description.Length > 0 ? _description : _name.Length > 0 ? _name : "Workflow";

    public bool Finished
    {
        get => _finished;
        set { if (Set(ref _finished, value)) Raise(nameof(Headline)); }
    }

    public IEnumerable<WorkflowAgentVm> AllAgents => Phases.SelectMany(p => p.Agents);
    public int AgentCount => AllAgents.Count();
    public int DoneCount => AllAgents.Count(a => a.State is "done" or "error");
    public int ErrorCount => AllAgents.Count(a => a.State == "error");
    public bool HasPhases => Phases.Count > 0;

    /// <summary>The collapsed one-liner: how far along, how much it cost, and what is running right now.</summary>
    public string Headline
    {
        get
        {
            if (AgentCount == 0) return _finished ? "finished" : "starting…";
            var tokens = AllAgents.Sum(a => a.Tokens);
            var parts = new List<string>
            {
                _finished || DoneCount == AgentCount
                    ? $"{AgentCount} agent{(AgentCount == 1 ? "" : "s")}"
                    : $"{DoneCount}/{AgentCount} agents",
            };
            if (Phases.Count > 1) parts.Add($"{Phases.Count} phases");
            if (tokens > 0) parts.Add($"{ChatViewModel.FmtTokens(tokens)} tokens");
            if (ErrorCount > 0) parts.Add($"{ErrorCount} failed");
            var running = AllAgents.Where(a => a.IsRunning).Select(a => a.Label).ToList();
            if (running.Count == 1) parts.Add($"→ {running[0]}");
            else if (running.Count > 1) parts.Add($"→ {running.Count} running");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>Apply one <c>workflow_progress</c> snapshot. Safe to call with a partial array.</summary>
    public void Apply(JsonArray snapshot)
    {
        foreach (var node in snapshot.OfType<JsonObject>())
        {
            switch (Str(node["type"]))
            {
                case "workflow_phase":
                    Phase(Int(node["index"]), Str(node["title"]) ?? "");
                    break;
                case "workflow_agent":
                {
                    // An agent with no phase (or one the script never declared) still has to appear somewhere, so
                    // it lands in a synthetic phase 0 rather than being dropped on the floor.
                    var phase = Phase(Int(node["phaseIndex"]), Str(node["phaseTitle"]) ?? "");
                    var index = Int(node["index"]);
                    var agent = phase.Agents.FirstOrDefault(a => a.Index == index);
                    if (agent is null)
                    {
                        agent = new WorkflowAgentVm { Index = index, Label = Str(node["label"]) ?? $"agent {index}" };
                        // Same fan-out can report out of order; keep the list in the script's own order.
                        var at = phase.Agents.Count;
                        for (var i = 0; i < phase.Agents.Count; i++)
                            if (phase.Agents[i].Index > index) { at = i; break; }
                        phase.Agents.Insert(at, agent);
                        phase.RaiseAgents();
                    }
                    if (Str(node["label"]) is { Length: > 0 } label) agent.Label = label;
                    if (Str(node["model"]) is { Length: > 0 } model) agent.Model = model;
                    if (Str(node["agentId"]) is { Length: > 0 } agentId) agent.AgentId = agentId;
                    if (Str(node["promptPreview"]) is { Length: > 0 } prompt) agent.PromptPreview = prompt;
                    if (Str(node["resultPreview"]) is { Length: > 0 } result) agent.ResultPreview = result;
                    agent.Tokens = Num(node["tokens"], agent.Tokens);
                    agent.ToolCalls = Num(node["toolCalls"], agent.ToolCalls);
                    agent.DurationMs = Num(node["durationMs"], agent.DurationMs);
                    agent.State = Str(node["state"]) ?? agent.State;
                    break;
                }
            }
        }
        RaiseTotals();
    }

    /// <summary>Every agent still shown as running has stopped, because the whole run has.</summary>
    public void Complete()
    {
        foreach (var agent in AllAgents.Where(a => a.IsRunning)) agent.State = "done";
        Finished = true;
        RaiseTotals();
    }

    public void RaiseTotals()
    {
        Raise(nameof(AgentCount));
        Raise(nameof(DoneCount));
        Raise(nameof(ErrorCount));
        Raise(nameof(HasPhases));
        Raise(nameof(Headline));
        foreach (var phase in Phases) phase.RaiseAgents();
    }

    private WorkflowPhaseVm Phase(int index, string title)
    {
        if (Phases.FirstOrDefault(p => p.Index == index) is { } existing)
        {
            if (title.Length > 0 && existing.Title != title) existing.Title = title;
            return existing;
        }
        var phase = new WorkflowPhaseVm { Index = index, Title = title.Length > 0 ? title : "Work" };
        var at = Phases.Count;
        for (var i = 0; i < Phases.Count; i++)
            if (Phases[i].Index > index) { at = i; break; }
        Phases.Insert(at, phase);
        Raise(nameof(HasPhases));
        return phase;
    }

    /// <summary>"1.9s" / "2m 05s" — durations here run from milliseconds to tens of minutes.</summary>
    public static string Duration(double ms)
    {
        var seconds = ms / 1000.0;
        if (seconds < 60) return $"{seconds:0.#}s";
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}h {span.Minutes:00}m"
            : $"{span.Minutes}m {span.Seconds:00}s";
    }

    /// <summary>
    /// Read <c>meta.name</c> / <c>meta.description</c> straight out of the script the model just sent, so the card
    /// is already labelled on the very first frame — the launch result and the first progress event both arrive
    /// later, and until then the generic tool card falls back to dumping the whole script as JSON.
    /// Deliberately a regex over the source rather than a JS parse: meta is required to be a pure literal.
    /// </summary>
    public static (string Name, string Description) ReadMeta(string? script)
    {
        if (string.IsNullOrEmpty(script)) return ("", "");
        return (Field(script, "name"), Field(script, "description"));

        static string Field(string source, string key)
        {
            var m = Regex.Match(source, key + @"\s*:\s*(['""`])(?<v>(?:\\.|(?!\1).)*)\1",
                RegexOptions.None, TimeSpan.FromSeconds(1));
            return m.Success ? Regex.Unescape(m.Groups["v"].Value) : "";
        }
    }

    private static string? Str(JsonNode? n)
    {
        try { return n?.GetValue<string>(); } catch { return null; }
    }

    private static int Int(JsonNode? n)
    {
        try { return n is null ? 0 : (int)n.GetValue<double>(); } catch { return 0; }
    }

    private static double Num(JsonNode? n, double fallback)
    {
        try { return n is null ? fallback : n.GetValue<double>(); } catch { return fallback; }
    }
}
