using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    private const string LiveModel = "gpt-5.6-luna";

    private static void RunLive()
    {
        var signedInHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(signedInHome) || !File.Exists(Path.Combine(signedInHome, "auth.json")))
            throw new InvalidOperationException("Live GPT-5.6 Luna simulations require the current signed-in CODEX_HOME.");
        // Use the existing login in a disposable provider home. Simulated transcripts and usage do not enter
        // the user's account history or personal VibeCode analytics. Never print or serialize credentials.
        var isolatedHome = Path.Combine(_root, "codex-home");
        Directory.CreateDirectory(isolatedHome);
        File.Copy(Path.Combine(signedInHome, "auth.json"), Path.Combine(isolatedHome, "auth.json"));
        Environment.SetEnvironmentVariable("VIBECODE_CODEX_LEGACY_HOME", isolatedHome);
        AppSettings.Current.DefaultProvider = "codex";
        AppSettings.Current.DefaultCodexModel = LiveModel;
        AppSettings.Current.DefaultCodexEffort = "low";
        var results = new List<object>();
        try
        {
            RunLiveCase("early-stop", """
                This is a tiny text-only goal simulation. Do not use any tools, files, web, or other agents.
                Complete these two stages in separate turns: in your FIRST response, calculate 17+25,
                reply exactly STAGE_ONE: 42 and stop. When the IDE next asks whether the goal is finished,
                the missing second stage is to independently verify 42-25=17, report VERIFIED: 17,
                and complete the goal only once both stages have actually been reported.
                """, chat =>
            {
                LiveWait(chat, () => chat.Goal?.Completed == true);
                var replies = string.Join("\n", chat.Items.OfType<TextItem>().Select(t => t.Text));
                Check("Luna resumes after early stop and verifies the remaining stage", replies.Contains("STAGE_ONE: 42") && replies.Contains("VERIFIED: 17"));
                Check("early-stop check was sent automatically", GoalChecks(chat) >= 1);
            }, results);

            RunLiveCase("already-finished", """
                Tiny text-only simulation: no tools, files, web, or other agents. Calculate 13+29.
                Finish with READY_SUM: 42. Once you have reported the correct sum, this goal is complete.
                """, chat =>
            {
                LiveWait(chat, () => chat.Goal?.Completed == true);
                Check("Luna confirms a goal completed in its first work turn", chat.Items.OfType<TextItem>().Any(t => t.Text.Contains("READY_SUM: 42")));
                Check("already-finished goal receives exactly one confirmation", GoalChecks(chat) == 1);
            }, results);

            RunLiveCase("needs-user-input", """
                Tiny text-only simulation: no tools, files, web, or other agents. Ask the user for their
                favorite color and wait for their answer. Do not invent a color or assume an answer.
                Once the user answers, report COLOR_ACCEPTED: followed by their color. Then the goal is complete.
                """, chat =>
            {
                LiveWait(chat, () => chat.Goal?.Paused == true);
                Check("Luna waits for missing user input without claiming completion", !chat.Goal!.Completed && chat.Status == "idle");
                LiveQuiet(chat, "waiting-for-input goal remains quiet");
                Check("user answer is accepted", chat.Send("My favorite color is turquoise."));
                LiveWait(chat, () => chat.Goal?.Completed == true);
                Check("Luna finishes the same goal after the user answers", chat.Items.OfType<TextItem>().Any(t => t.Text.Contains("COLOR_ACCEPTED:") && t.Text.Contains("turquoise", StringComparison.OrdinalIgnoreCase)));
            }, results);
        }
        finally
        {
            File.WriteAllText(Path.Combine(_root, "results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
            // The only secret file created by this harness is known exactly; keep the non-secret evidence.
            File.Delete(Path.Combine(isolatedHome, "auth.json"));
        }
    }

    private static void RunLiveCase(string name, string goal, Action<ChatViewModel> verify, List<object> results)
    {
        Console.WriteLine($"START: {name}, GPT-5.6 Luna / low");
        var cwd = Path.Combine(_root, "workspace-" + name);
        Directory.CreateDirectory(cwd);
        var chat = new ChatViewModel(cwd, provider: "codex", title: "Goal simulation " + name)
            { Model = LiveModel, Effort = "low", ExcludeFromMemory = true };
        Chats.Add(chat);
        chat.SetMode("plan");
        chat.Start();
        LiveWait(chat, () => chat.Status == "idle");
        Console.WriteLine("SELECTED MODEL: " + chat.Model + "; catalog: " + string.Join(", ", chat.Models.Select(m => m.Value)));
        // Catalog visibility is not proof of model availability; send the exact requested model and require the
        // provider to accept it. A provider rejection is a failed simulation, never a silent model substitution.
        chat.Model = LiveModel;
        Check(name + ": requested model is selected without substitution", chat.Model == LiveModel);
        Check(name + ": initialization keeps /goal and /compact", chat.Commands.Count == 2 && chat.Commands[0].Name == "goal" && chat.Commands[1].Name == "compact");
        Check(name + ": /goal accepted", chat.Send("/goal " + goal));
        verify(chat);
        Check(name + ": exact model retained through goal checks", chat.Model == LiveModel);
        LiveQuiet(chat, name + ": finished goal stops checking");
        var reply = string.Join("\n\n", chat.Items.OfType<TextItem>().Select(t => t.Text));
        File.WriteAllText(Path.Combine(_root, name + "-transcript.txt"), reply);
        results.Add(new
        {
            scenario = name, requested_model = LiveModel, selected_model = chat.Model,
            goal = chat.Goal, automatic_checks = GoalChecks(chat), output_tokens = chat.TotalOut,
            replies = chat.Items.OfType<TextItem>().Select(t => t.RenderText).ToArray(),
        });
        chat.Close();
    }

    private static int GoalChecks(ChatViewModel chat) => chat.Items.OfType<UserItem>().Count(u => u.Text.StartsWith(GoalPolicy.CheckHeader, StringComparison.Ordinal));
    private static void LiveWait(ChatViewModel chat, Func<bool> done)
    {
        Pump(() => done() || chat.Status == "error" || chat.TotalOut > 2500, TimeSpan.FromSeconds(90));
        if (chat.Status == "error") throw new InvalidOperationException("Live simulation provider error: " + string.Join("; ", chat.Items.OfType<BannerItem>().Select(b => b.Text)));
        if (chat.TotalOut > 2500) throw new InvalidOperationException("Live simulation output budget exceeded.");
        if (!done()) throw new InvalidOperationException("Live simulation stopped before expected goal state.");
    }
    private static void LiveQuiet(ChatViewModel chat, string name)
    {
        var count = GoalChecks(chat);
        var watch = Stopwatch.StartNew();
        Pump(() => watch.Elapsed > TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4));
        Check(name, GoalChecks(chat) == count && chat.Status == "idle");
    }
}
