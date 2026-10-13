using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    private static void VerifyLegacyManagerRemoval()
    {
        foreach (var managerIndex in new[] { 0, 1 })
        {
            var (originalVm, original) = Team("retired-manager-" + managerIndex, 2);
            foreach (var pane in original) pane.Status = "idle";
            original[0].Draft = "Keep the host draft";
            original[1].Draft = "Keep the peer draft";
            originalVm.SaveBridge();
            var saved = JsonSerializer.Deserialize<SavedBridgeState>(JsonSerializer.Serialize(
                AppSettings.Current.SavedBridges.Single(s => s.HostSessionId == original[0].SessionId)))!;
            if (managerIndex == 0)
            {
                saved.HostIsManager = true;
                saved.HostTaskName = "Manager";
                saved.Peers[0].CoordinatorSessionId = saved.HostSessionId;
            }
            else
            {
                saved.Peers[0].IsManager = true;
                saved.Peers[0].TaskName = "Manager";
                saved.HostCoordinatorSessionId = saved.Peers[0].SessionId;
            }
            AppSettings.Current.UpsertSavedBridge(saved);
            var vm = new MainViewModel();
            var resumed = original.Select(source =>
            {
                var pane = new ChatViewModel(source.Cwd, provider: source.Provider)
                { Status = "idle", Draft = source.Draft };
                typeof(ChatViewModel).GetField("_session", Flags)!.SetValue(pane, new FakeSession());
                Property(pane, "SessionId", source.SessionId!);
                Chats.Add(pane); vm.Chats.Add(pane); Call(vm, "Track", pane);
                return pane;
            }).ToArray();
            var label = managerIndex == 0 ? "host" : "peer";
            Check($"retired {label} manager bridge restores both conversations", vm.RestoreBridge(resumed[0]) && vm.BridgePanes.Count == 2);
            Check($"retired {label} manager and workers restore as independent peers", resumed.All(p =>
                !p.IsBridgeManager && !p.BridgeCoordinatesOnly && p.BridgeCoordinatorAgentId is null));
            Check($"retired {label} manager instructions are replaced", resumed.All(p =>
                p.AppendSystemPrompt!.Contains("There is no manager role or automatic work dispatch.") &&
                !p.AppendSystemPrompt.Contains("[BRIDGE MANAGER")));
            Check($"retiring the {label} manager preserves drafts and sessions", resumed[0].Draft == original[0].Draft &&
                resumed[1].Draft == original[1].Draft && resumed.Select(p => p.SessionId).SequenceEqual(original.Select(p => p.SessionId)));
            Check($"retired {label} manager is exposed as a regular agent", Tool(resumed[managerIndex], "bridge_list_agents")["agents"]!
                .AsArray().All(a => a!["role"]!.ToString() == "agent"));
            Reject($"retired {label} manager cannot use advanced dispatch", () => Tool(resumed[managerIndex], "bridge_dispatch_task", new()
            {
                ["recipient"] = resumed[1 - managerIndex].BridgeAgentId,
                ["task_name"] = "Old order",
                ["message"] = "This must not become a work order."
            }));
            var sender = resumed[managerIndex];
            sender.Status = "running";
            sender.Items.Add(new TextItem { Text = "@@DISPATCH agent=" + (2 - managerIndex) + "\nRetired order\n@@END" });
            sender.Status = "idle";
            Check($"retired {label} manager text dispatch never starts a worker", resumed.All(p => Session(p).Sent.Count == 0 && !p.HasQueued));
            var delivered = Tool(resumed[0], "bridge_send_message", new()
            { ["recipient"] = resumed[1].BridgeAgentId, ["message"] = "I am editing the catalogue; please coordinate overlapping work." });
            Check($"regular peer messages still work after retiring the {label} manager", delivered["delivered"]!.AsArray().Count == 1 &&
                Tool(resumed[1], "bridge_read_messages")["messages"]!.AsArray().Any(m => m!["message"]!.ToString().Contains("catalogue")));
            vm.SaveBridge();
            var updated = AppSettings.Current.SavedBridges.Single(s => s.HostSessionId == resumed[0].SessionId);
            Check($"retired {label} manager stays removed in the next snapshot", !updated.HostIsManager &&
                updated.Peers.All(p => !p.IsManager && p.CoordinatorSessionId is null) &&
                updated.HostCoordinatorSessionId is null && !resumed[0].IsAdvancedBridgeHost);
        }

        // Even stale in-memory legacy flags cannot revive the text-dispatch loop or implicit worker ownership.
        var (legacyVm, legacy) = Team("stale-manager-role", 2);
        foreach (var pane in legacy) pane.Status = "idle";
        legacy[0].IsBridgeManager = true;
        legacy[0].Status = "running";
        legacy[0].Items.Add(new TextItem { Text = "@@DISPATCH agent=2\nOld work order\n@@END" });
        legacy[0].Status = "idle";
        Check("stale manager flags cannot deliver text work orders", Session(legacy[1]).Sent.Count == 0 && !legacy[1].HasQueued);
        legacy[1].Status = "running";
        legacy[1].Items.Add(new TextItem { Text = "Independent peer finished its user's task." });
        legacy[1].Status = "idle";
        Check("independent peer results do not wake a stale manager", Session(legacy[0]).Sent.Count == 0 && !legacy[0].HasQueued);
        Check("stale manager does not gain implicit worker ownership", Call(legacyVm, "CoordinatorFor", legacy[1], legacy) is null);
    }
}
