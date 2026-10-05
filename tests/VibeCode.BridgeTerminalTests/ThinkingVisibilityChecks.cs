using System.Collections.Specialized;
using VibeCode.UI;

internal static partial class Program
{
    private static void VerifyThinkingVisibility()
    {
        var (vm, team) = Team("thinking-visibility", 3);
        foreach (var agent in team) agent.Status = "idle";
        var before = new TextItem { Text = "Working on the request." };
        var coordinatorThought = new ThinkingItem { Text = "Coordinator reasoning.", Streaming = false };
        var workerThought = new ThinkingItem { Text = "Worker reasoning.", Streaming = true };
        var after = new TextItem { Text = "The change is ready." };
        team[0].Items.Add(before);
        team[0].Items.Add(coordinatorThought);
        team[1].Items.Add(workerThought);
        team[0].Items.Add(after);
        vm.SetBridgeTerminalMode(true);
        var panel = vm.SharedBridgeTerminal;
        Check("single terminal without an orchestrator shows thought process", panel.Activity.Cast<BridgeTranscriptEntry>()
            .Select(entry => entry.Item).SequenceEqual(new ItemVm[] { before, coordinatorThought, workerThought, after }));

        team[0].IsBridgeManager = true;
        Check("promoting an orchestrator removes all existing shared thinking rows", panel.Entries.All(entry => entry.Item is not ThinkingItem) &&
            panel.Activity.Cast<BridgeTranscriptEntry>().Select(entry => entry.Item).SequenceEqual(new[] { before, after }));
        Check("hidden thinking does not split consecutive messages from the same agent", panel.Entries[0].StartsAgentGroup &&
            !panel.Entries[0].EndsAgentGroup && !panel.Entries[1].StartsAgentGroup && panel.Entries[1].EndsAgentGroup);
        Check("original chats retain their thought process", team[0].Items.Contains(coordinatorThought) && team[1].Items.Contains(workerThought));

        var newThought = new ThinkingItem { Streaming = true };
        var activityChanges = 0;
        NotifyCollectionChangedEventHandler changed = (_, _) => activityChanges++;
        panel.Activity.CollectionChanged += changed;
        team[2].Items.Add(newThought);
        workerThought.Text = "";
        workerThought.Append("New reasoning delta.");
        newThought.Append("Streaming reasoning.");
        Check("new and streaming thoughts cause no shared row changes", activityChanges == 0 && panel.Entries.Count == 2);
        panel.Activity.CollectionChanged -= changed;

        panel.Review(team[1]);
        Check("agent review inside an orchestrated single terminal also omits thinking", !panel.Activity.Cast<BridgeTranscriptEntry>().Any());
        panel.ShowAll();
        vm.SetBridgeTerminalMode(false);
        Check("separate terminals preserve and restore thinking in its original order", panel.Entries.Select(entry => entry.Item)
            .SequenceEqual(new ItemVm[] { before, coordinatorThought, workerThought, after, newThought }) &&
            team[1].Items.OfType<ThinkingItem>().Single() == workerThought);
        vm.SetBridgeTerminalMode(true);
        Check("returning to an orchestrated single terminal hides thinking again", panel.Entries.All(entry => entry.Item is not ThinkingItem));

        team[1].IsBridgeManager = true;
        team[0].IsBridgeManager = false;
        Check("thinking stays hidden while another orchestrator remains", panel.Entries.All(entry => entry.Item is not ThinkingItem));
        team[1].IsBridgeManager = false;
        Check("removing the last orchestrator restores existing and streamed thinking once", panel.Entries.Count(entry => entry.Item is ThinkingItem) == 3 &&
            panel.Entries.Select(entry => entry.Item).Distinct().Count() == panel.Entries.Count &&
            panel.Activity.Cast<BridgeTranscriptEntry>().Any(entry => ReferenceEquals(entry.Item, newThought)));
        workerThought.Text = "";
        Check("restored thinking still updates its visibility", !panel.Activity.Cast<BridgeTranscriptEntry>().Any(entry => ReferenceEquals(entry.Item, workerThought)));
        workerThought.Append("Reasoning restored.");
        Check("restored thinking becomes visible when streaming resumes", panel.Activity.Cast<BridgeTranscriptEntry>().Any(entry => ReferenceEquals(entry.Item, workerThought)));

        team[0].IsBridgeManager = true;
        team[1].Items.Clear();
        var replacement = new ThinkingItem { Text = "Replacement after history reset." };
        team[1].Items.Add(replacement);
        team[2].Items.Remove(newThought);
        Check("reset and removal cannot reintroduce shared thinking", panel.Entries.All(entry => entry.Item is not ThinkingItem));
        vm.RemoveBridgePane(team[0]);
        Check("orchestrator departure restores only remaining thoughts", panel.Entries.Count(entry => entry.Item is ThinkingItem) == 1 &&
            ReferenceEquals(panel.Entries.Single(entry => entry.Item is ThinkingItem).Item, replacement));

        var (largeVm, largeTeam) = Team("thinking-twenty-agents", 20);
        foreach (var agent in largeTeam)
        {
            agent.Status = "idle";
            agent.Items.Add(new ThinkingItem { Text = "Historical reasoning.", Streaming = true });
            agent.Items.Add(new TextItem { Text = "Agent progress." });
        }
        largeTeam[0].IsBridgeManager = true;
        largeVm.SetBridgeTerminalMode(true);
        var largePanel = largeVm.SharedBridgeTerminal;
        Check("opening an existing twenty-agent orchestrated bridge omits historical thinking", largePanel.Entries.Count == 20 &&
            largePanel.Entries.All(entry => entry.Item is TextItem));
        var streamedChanges = 0;
        largePanel.Activity.CollectionChanged += (_, _) => streamedChanges++;
        for (var delta = 0; delta < 100; delta++)
            foreach (var agent in largeTeam)
            {
                var thought = agent.Items.OfType<ThinkingItem>().Single();
                thought.Text = "";
                thought.Append("Another reasoning delta.");
            }
        Check("two thousand thinking updates create no shared transcript changes", streamedChanges == 0 && largePanel.Entries.Count == 20);
        Check("large-team original transcripts still keep their thinking", largeTeam.All(agent => agent.Items.OfType<ThinkingItem>().Single().HasText));
        largeVm.CloseBridge();
        Check("ending a bridge preserves thought process in the remaining ordinary chat", !largeTeam[0].BridgeSingleTerminal &&
            !largeVm.ShowBridge && largeTeam[0].Items.OfType<ThinkingItem>().Single().HasText);
    }
}
