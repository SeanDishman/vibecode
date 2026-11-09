using System.IO;
using System.Windows.Threading;
using VibeCode.Services;
using VibeCode.Terminal;
using VibeCode.UI;

internal static partial class Program
{
    // Real sessions/model routing, but startup and focus are simulated: no native console or WPF window.
    private static void VerifyTerminalWindows()
    {
        var enabled = AppSettings.Current.TerminalEnabled;
        AppSettings.Current.TerminalEnabled = true;
        var (vm, original) = BridgeHost("terminal-windows", "codex", "First window");
        var firstHost = vm.OpenChatInTerminal(original);
        var created = new List<TerminalSession>();
        var starts = new Dictionary<TerminalSession, TaskCompletionSource>();
        var focused = new List<TerminalSession>();
        var desktops = new List<ChatViewModel>();
        var exits = new List<(TerminalSession Session, int Remaining)>();
        using var windows = new TerminalWindowCollection(host =>
        {
            var session = new TerminalSession(vm, host, Dispatcher.CurrentDispatcher);
            created.Add(session);
            return session;
        }, session =>
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            starts.Add(session, completion);
            return completion.Task;
        }, focused.Add);
        windows.DesktopRequested += session => desktops.Add(session.Host);
        windows.Ended += session => exits.Add((session, windows.Count));

        ChatViewModel AddHost(string title)
        {
            var chat = new ChatViewModel(original.Cwd, title: title, provider: "codex");
            typeof(ChatViewModel).GetField("_session", Flags)!.SetValue(chat, new SteerableSession());
            typeof(ChatViewModel).GetField("_bridgeSessionInitialized", Flags)!.SetValue(chat, true);
            Property(chat, "SessionId", Guid.NewGuid().ToString("N"));
            chat.Status = "idle";
            Chats.Add(chat); vm.Chats.Add(chat); Call(vm, "Track", chat);
            return vm.OpenChatInTerminal(chat);
        }
        TerminalSession Finish(Task<TerminalSession> task)
        {
            PumpUntil(() => task.IsCompleted);
            return task.GetAwaiter().GetResult();
        }
        void Fail(Task<TerminalSession> task, string message)
        {
            PumpUntil(() => task.IsCompleted);
            try { task.GetAwaiter().GetResult(); }
            catch (IOException ex) { Check(message, ex.Message == "fixture launch failed"); return; }
            throw new Exception("FAIL: " + message);
        }
        static bool Disposed(TerminalSession session) => (bool)typeof(TerminalSession).GetField("_disposed", Flags)!.GetValue(session)!;
        static void End(TerminalSession session)
        {
            // This is the same completion raised by ReceiveAsync after a console disconnects.
            session.Dispose();
            ((Action?)typeof(TerminalSession).GetField("Ended", Flags)!.GetValue(session))?.Invoke();
        }

        try
        {
            var firstOpen = windows.OpenAsync(firstHost);
            var first = windows.Find(firstHost)!;
            var duplicateOpen = windows.OpenAsync(vm.OpenChatInTerminal(firstHost));
            var secondHost = AddHost("Second window");
            var secondOpen = windows.OpenAsync(secondHost);
            var second = windows.Find(secondHost)!;
            Check("different chats can launch concurrently", windows.Count == 2 && starts.Count == 2
                && !firstOpen.IsCompleted && !secondOpen.IsCompleted);
            Check("repeated open shares a pending console and waits before focusing", created.Count == 2
                && !duplicateOpen.IsCompleted && focused.Count == 0);
            Check("each terminal owns an independent pipe", first.PipeName != second.PipeName);
            starts[second].SetResult();
            Check("second window can become ready while the first is still starting", Finish(secondOpen) == second && !firstOpen.IsCompleted);
            starts[first].SetResult();
            Check("same-team pending opens resolve to the same window", Finish(firstOpen) == first && Finish(duplicateOpen) == first
                && focused.SequenceEqual([first]) && created.Count == 2);
            Check("reopening a connected team focuses its existing window", Finish(windows.OpenAsync(firstHost)) == first
                && focused.SequenceEqual([first, first]) && windows.Count == 2);

            firstHost.Items.Add(new TextItem { Text = "Only first window history" });
            secondHost.Items.Add(new TextItem { Text = "Only second window history" });
            Check("opening another terminal never switches the first chat", first.Host == firstHost && second.Host == secondHost
                && first.Snapshot().Rows!.Any(r => r.Text.Contains("Only first window history"))
                && first.Snapshot().Rows!.All(r => !r.Text.Contains("Only second window history"))
                && second.Snapshot().Rows!.All(r => !r.Text.Contains("Only first window history")));
            Check("each window accepts its own prompt", first.HandleInput("First window prompt").Accepted && second.HandleInput("Second window prompt").Accepted);
            PumpUntil(() => Steerable(firstHost).Sent.Count == 1 && Steerable(secondHost).Sent.Count == 1);
            Check("prompts reach only their own team", Steerable(firstHost).Sent[0].Contains("First window prompt")
                && !Steerable(firstHost).Sent[0].Contains("Second window prompt")
                && Steerable(secondHost).Sent[0].Contains("Second window prompt")
                && !Steerable(secondHost).Sent[0].Contains("First window prompt"));
            first.HandleInput("/desktop"); second.HandleInput("/desktop");
            Check("desktop requests identify the requesting window's team", desktops.SequenceEqual([firstHost, secondHost]));

            var failedHost = AddHost("Failed launch");
            var failedOpen = windows.OpenAsync(failedHost);
            var failed = windows.Find(failedHost)!;
            var failedDuplicate = windows.OpenAsync(failedHost);
            starts[failed].SetException(new IOException("fixture launch failed"));
            Fail(failedOpen, "launch failure is reported to the original caller");
            Fail(failedDuplicate, "launch failure also releases a duplicate waiter");
            Check("a failed launch removes only its own session", Disposed(failed) && windows.Count == 2 && windows.Find(failedHost) is null
                && windows.Find(firstHost) == first && windows.Find(secondHost) == second && exits.Count == 0);
            var retryOpen = windows.OpenAsync(failedHost);
            var retried = windows.Find(failedHost)!;
            starts[retried].SetResult();
            Check("failed chat can reopen with a fresh connection", Finish(retryOpen) == retried && retried != failed
                && retried.PipeName != failed.PipeName && windows.Count == 3);
            End(retried);
            Check("closing one of several consoles leaves the others open", windows.Count == 2 && !Disposed(first) && !Disposed(second)
                && exits.Last() == (retried, 2));
            End(retried);
            Check("duplicate disconnect does not remove another window", windows.Count == 2 && exits.Count == 1);

            Check("new team switches only the requesting terminal", first.HandleInput("/new").Accepted && first.Host != firstHost
                && second.Host == secondHost && vm.TerminalTeamRoster(firstHost).Count == 1);
            var newHost = first.Host;
            Chats.Add(newHost);
            Check("reopening a terminal's new team focuses that same window", Finish(windows.OpenAsync(vm.OpenChatInTerminal(newHost))) == first
                && windows.Find(firstHost) is null && windows.Count == 2);
            first.HandleInput("/desktop");
            Check("desktop request follows a window after new-team navigation", desktops.Last() == newHost);
            var oldOpen = windows.OpenAsync(vm.OpenChatInTerminal(firstHost));
            var reopened = windows.Find(firstHost)!;
            starts[reopened].SetResult();
            Check("previous team can open separately after new-team navigation", Finish(oldOpen) == reopened && reopened != first
                && first.Host == newHost && second.Host == secondHost && windows.Count == 3);
            End(reopened); End(first);
            Check("last surviving window is still usable", windows.Count == 1 && windows.Find(secondHost) == second
                && Finish(windows.OpenAsync(secondHost)) == second && !Disposed(second));
            End(second);
            Check("last console exit is distinguishable from an individual exit", windows.Count == 0 && exits.Last() == (second, 0));
            Check("closing consoles preserves provider work and team identities", firstHost.Status != "closed" && secondHost.Status != "closed"
                && Steerable(firstHost).Interrupts == 0 && Steerable(secondHost).Interrupts == 0
                && vm.TerminalTeamRoster(firstHost).Count == 1 && vm.TerminalTeamRoster(secondHost).Count == 1);

            var pendingHost = AddHost("Close during startup");
            var pendingOpen = windows.OpenAsync(pendingHost);
            var pending = windows.Find(pendingHost)!;
            End(pending);
            Check("disconnect during startup releases the opening caller", pendingOpen.IsCanceled && windows.Count == 0);
            starts[pending].SetResult();
            Settle();
            Check("late startup cannot resurrect a closed console", windows.Count == 0 && pendingOpen.IsCanceled && Disposed(pending));

            var shutdownOpen = windows.OpenAsync(firstHost);
            var shutdownSession = windows.Find(firstHost)!;
            var shutdownDuplicate = windows.OpenAsync(firstHost);
            var readyOpen = windows.OpenAsync(secondHost);
            var ready = windows.Find(secondHost)!;
            starts[ready].SetResult(); Finish(readyOpen);
            var exitsBeforeShutdown = exits.Count;
            var focusBeforeShutdown = focused.Count;
            windows.Dispose();
            PumpUntil(() => shutdownDuplicate.IsCompleted);
            Check("owner shutdown disposes every ready and starting window", windows.Count == 0 && Disposed(shutdownSession) && Disposed(ready)
                && shutdownOpen.IsCanceled && shutdownDuplicate.IsCanceled);
            starts[shutdownSession].SetResult(); Settle();
            windows.Dispose();
            Check("shutdown cannot restore or focus a late terminal", windows.Count == 0 && exits.Count == exitsBeforeShutdown
                && focused.Count == focusBeforeShutdown);
            var rejected = false;
            try { windows.OpenAsync(firstHost); }
            catch (ObjectDisposedException) { rejected = true; }
            Check("a closed owner cannot start a new console", rejected);
            Check("headless checks never created a console process", created.All(s => typeof(TerminalSession).GetField("_process", Flags)!.GetValue(s) is null));
        }
        finally { AppSettings.Current.TerminalEnabled = enabled; }
    }
}
