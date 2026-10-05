using System.IO;
using System.Text.Json.Nodes;
using VibeCode.Services;
using TurnStart = VibeCode.Services.TurnRollbackCheckpoint.TurnStartState;

namespace VibeCode.UI;

public sealed partial class ChatViewModel
{
    private const int MaxPendingBridgeEdits = 512;
    private static readonly TimeSpan ShellClaimMemory = TimeSpan.FromMinutes(30);

    /// <summary>Set by the main view model for every tracked chat. Only bridge panes record: edits whose tool result
    /// reported success, and files their shell commands changed.</summary>
    internal Action<BridgeEditObservation>? BridgeEditRecorder { get; set; }

    /// <summary>Each file-edit call's own input, by tool-use id, until its result lands. Kept apart from the cards
    /// because consecutive edits to one file are folded into a single card that only remembers the first input.</summary>
    private readonly Dictionary<string, (string Name, JsonObject Input)> _bridgeEditCalls = new(StringComparer.Ordinal);

    /// <summary>Shell commands still running, by tool-use id. Each claims the files that changed while it ran.</summary>
    private readonly HashSet<string> _bridgeShellCalls = new(StringComparer.Ordinal);

    /// <summary>Files this pane's shell commands changed lately. Codex can repeat such a change at turn end as an
    /// aggregated turn-diff card; that copy is not logged a second time.</summary>
    private readonly Dictionary<string, DateTime> _bridgeShellClaims = new(StringComparer.OrdinalIgnoreCase);

    private static bool IsShellCommandTool(string name) => name is "Bash" or "PowerShell";

    /// <summary>Remember a file-edit call, or open a shell command's window. Streamed partial input is replaced by the
    /// final payload under the same id.</summary>
    private void NoteBridgeEditCall(JsonObject block)
    {
        if (!IsBridgeAgent || BridgeEditRecorder is null) return;
        if (NodeString(block["name"]) is not { } name || NodeString(block["id"]) is not { } id) return;
        if (IsShellCommandTool(name))
        {
            if (_bridgeShellCalls.Count >= MaxPendingBridgeEdits && !_bridgeShellCalls.Contains(id)) _bridgeShellCalls.Clear();
            if (_bridgeShellCalls.Add(id)) BridgeShellEdits.For(Cwd).Open(this, id);
            return;
        }
        if (!IsFileEditTool(name) || block["input"] is not JsonObject input) return;
        // Start watching now, so a later shell diff of this file starts from what this edit leaves behind.
        BridgeShellEdits.For(Cwd);
        // A result that never arrives (stopped turn, crashed provider) must not grow this map forever.
        if (_bridgeEditCalls.Count >= MaxPendingBridgeEdits && !_bridgeEditCalls.ContainsKey(id)) _bridgeEditCalls.Clear();
        _bridgeEditCalls[id] = (name, (JsonObject)input.DeepClone());
    }

    /// <summary>A tool result arrived. Successful live edits go to the bridge edit log; failures and replayed history
    /// only clear the pending call.</summary>
    private void SettleBridgeEditCall(string id, bool live, bool failed, JsonNode? structuredResult)
    {
        if (_bridgeShellCalls.Remove(id))
        {
            SettleBridgeShellCall(id, live);
            return;
        }
        if (!_bridgeEditCalls.Remove(id, out var call) || !live || failed || !IsBridgeAgent) return;
        if (EditTarget(call.Input) is { } full)
        {
            if (call.Name == "CodexEdit" && id.StartsWith("turn-diff:", StringComparison.Ordinal)
                && _bridgeShellClaims.TryGetValue(full, out var claimed) && DateTime.UtcNow - claimed < ShellClaimMemory)
                return;
            BridgeShellEdits.Find(Cwd)?.NoteReported(full);
        }
        BridgeEditRecorder?.Invoke(new BridgeEditObservation(call.Name, call.Input,
            (structuredResult as JsonObject)?.DeepClone() as JsonObject, Cwd));
    }

    /// <summary>A shell command finished. A failing command may still have changed files, so its claims are logged.</summary>
    private void SettleBridgeShellCall(string id, bool live)
    {
        if (BridgeShellEdits.Find(Cwd) is not { } tracker) return;
        if (!live || !IsBridgeAgent) { tracker.Discard(this, id); return; }
        var now = DateTime.UtcNow;
        foreach (var (path, shared) in tracker.Claim(this, id))
        {
            var (state, before) = ShellBaseline(tracker, path);
            if (state == TurnStart.Excluded) continue;
            _bridgeShellClaims[path] = now;
            BridgeEditRecorder?.Invoke(new BridgeEditObservation("Shell", new JsonObject { ["file_path"] = path }, null, Cwd)
                { Before = before, BeforeKnown = state != TurnStart.Unknown, Shared = shared });
        }
        if (_bridgeShellClaims.Count > MaxPendingBridgeEdits)
            foreach (var stale in _bridgeShellClaims.Where(c => now - c.Value > ShellClaimMemory).Select(c => c.Key).ToList())
                _bridgeShellClaims.Remove(stale);
    }

    /// <summary>A file's text before the shell command: what the last logged edit left behind, unless this turn's
    /// rewind snapshot was taken later than that.</summary>
    private (TurnStart State, string? Text) ShellBaseline(BridgeShellEdits tracker, string path)
    {
        var state = TurnStart.Unknown;
        string? text = null;
        var snapshotAt = DateTime.MinValue;
        if (_activeRollback is { } rollback) state = rollback.GetTurnStartText(path, out text, out snapshotAt);
        if (state == TurnStart.Excluded) return (state, null);
        if (tracker.TryRecall(path, out var recalledAt, out var recalled) && (state == TurnStart.Unknown || recalledAt >= snapshotAt))
            return (recalled is null ? TurnStart.Absent : TurnStart.Captured, recalled);
        return (state, text);
    }

    private string? EditTarget(JsonObject input)
    {
        var path = NodeString(input["file_path"]) ?? NodeString(input["notebook_path"]) ?? NodeString(input["path"]);
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(Cwd, path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}
