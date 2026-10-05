using System.IO;

namespace VibeCode.Services;

/// <summary>Planning and execution are separate. User-visible action outcomes come from verified app receipts.</summary>
public sealed class JarvisRuntime(IJarvisPlanner planner, IJarvisActionHost host, IJarvisDesktopActions? desktop = null)
{
    private readonly IJarvisDesktopActions _desktop = desktop ?? new JarvisDesktopActions();
    private readonly List<JarvisDialogueLine> _history = [];
    private readonly SemaphoreSlim _turnGate = new(1, 1);
    private int _historyVersion;
    private IReadOnlyList<JarvisMemoryAccess> _currentMemorySources = [];
    public bool IsPlanning { get; private set; }
    public bool CurrentMemoryRevoked => _currentMemorySources.Any(source => !source.IsAllowed);
    public IReadOnlyList<JarvisDialogueLine> History { get { RefreshMemoryAccess(); return _history.ToArray(); } }
    public void RefreshMemoryAccess() => _history.RemoveAll(line => line.MemorySources.Any(source => !source.IsAllowed));

    public void ClearConversation() { _historyVersion++; _history.Clear(); }

    public async Task<string> SubmitAsync(string request, JarvisSelection selection, CancellationToken cancellationToken,
        Action<JarvisActionReceipt>? onAction = null)
    {
        if (string.IsNullOrWhiteSpace(request)) throw new ArgumentException("Tell Jarvis what you need.");
        if (request.Length > 12_000) throw new ArgumentException("Keep a Jarvis request below 12,000 characters.");
        await _turnGate.WaitAsync(cancellationToken);
        var outcomes = new List<JarvisActionReceipt>();
        IReadOnlyList<JarvisMemoryAccess> memorySources = [];
        var historyVersion = _historyVersion;
        try
        {
            var context = host is IJarvisContextHost contextHost
                ? await contextHost.GetContextAsync(request, cancellationToken) : host.GetContext();
            RefreshMemoryAccess();
            memorySources = _currentMemorySources = JarvisMemoryPolicy.Sources(context, _history);
            IsPlanning = true;
            JarvisTurn turn;
            try { turn = await planner.PlanAsync(selection, context, _history.ToArray(), request, cancellationToken); }
            finally { IsPlanning = false; }
            memorySources = _currentMemorySources = turn.MemorySources;
            cancellationToken.ThrowIfCancellationRequested();
            if (memorySources.Any(source => !source.IsAllowed))
                throw new OperationCanceledException("Second Brain access changed. Stale memory was discarded.", cancellationToken);
            foreach (var action in turn.Plan.Actions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                JarvisActionReceipt outcome;
                var receiptIndex = -1;
                switch (action.Kind)
                {
                    case "open_project":
                        JarvisPathPolicy.RequireExisting(action.Path);
                        outcome = await host.OpenProjectAsync(action.Path, selection, cancellationToken);
                        break;
                    case "create_project":
                        // Only this fixed operation creates anything. Templates and coding belong to a normal chat.
                        JarvisPathPolicy.CreateNew(action.Path);
                        receiptIndex = outcomes.Count;
                        outcomes.Add(new("create_project", action.Path, $"Created {action.Path}."));
                        onAction?.Invoke(outcomes[receiptIndex]);
                        try
                        {
                            var opened = await host.OpenProjectAsync(action.Path, selection, cancellationToken);
                            outcome = opened with { Kind = "create_project", Message = $"Created and opened {action.Path}." };
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            throw new IOException($"Created {action.Path}, but VibeCode could not open it. {ex.Message}", ex);
                        }
                        break;
                    case "start_chat":
                        JarvisPathPolicy.RequireExisting(action.Path);
                        outcome = await host.StartCodingChatAsync(action.Path, action.Task, selection, cancellationToken);
                        break;
                    case "create_chats":
                        JarvisPathPolicy.RequireExisting(action.Path);
                        if (host is not IJarvisNewChatsHost newChatsHost)
                            throw new InvalidOperationException("Creating chats is unavailable in this window.");
                        outcome = await newChatsHost.CreateChatsAsync(action.Path, action.Count, cancellationToken);
                        break;
                    case "list_chats":
                    case "open_chat":
                    case "close_chats":
                        if (host is not IJarvisChatActionHost chatHost || action.ChatFilter is not { } filter)
                            throw new InvalidOperationException("Chat controls are unavailable in this window.");
                        if (filter.ChatId == "current")
                        {
                            if (context.CurrentChatId is null) throw new InvalidOperationException("There is no active chat.");
                            filter = filter with { ChatId = context.CurrentChatId };
                        }
                        outcome = await chatHost.ExecuteChatActionAsync(action with { ChatFilter = filter }, cancellationToken);
                        break;
                    default:
                        if (JarvisSettingsActions.IsSettingsAction(action.Kind))
                        {
                            if (host is not IJarvisSettingsActionHost settingsHost)
                                throw new InvalidOperationException("Settings controls are unavailable in this window.");
                            outcome = await settingsHost.ExecuteSettingsActionAsync(action, cancellationToken);
                            break;
                        }
                        if (!JarvisDesktopPolicy.IsDesktopAction(action.Kind)) throw new InvalidOperationException("Unsupported Jarvis action.");
                        outcome = await _desktop.ExecuteAsync(action, cancellationToken);
                        break;
                }
                if (receiptIndex >= 0) outcomes[receiptIndex] = outcome;
                else outcomes.Add(outcome);
                onAction?.Invoke(outcome);
            }
            var answer = outcomes.Count == 0 ? turn.Plan.Reply : string.Join("\n\n", outcomes.Select(o => o.Message));
            if (historyVersion == _historyVersion && memorySources.All(source => source.IsAllowed)) Remember(request, answer, memorySources);
            return answer;
        }
        catch (OperationCanceledException)
        {
            var detail = outcomes.Count > 0 ? " " + string.Join(" ", outcomes.Select(o => o.Message)) : "";
            if (historyVersion == _historyVersion && memorySources.All(source => source.IsAllowed)) Remember(request, "Cancelled." + detail, memorySources);
            throw new OperationCanceledException("Cancelled." + detail, cancellationToken);
        }
        catch (Exception ex)
        {
            if (historyVersion == _historyVersion && memorySources.All(source => source.IsAllowed))
                Remember(request, (outcomes.Count > 0 ? string.Join(" ", outcomes.Select(o => o.Message)) + " " : "")
                    + "The request failed: " + ex.Message, memorySources);
            if (outcomes.Count > 0)
                throw new InvalidOperationException(string.Join("\n\n", outcomes.Select(o => o.Message))
                    + "\n\nThe remaining request failed: " + ex.Message, ex);
            throw;
        }
        finally { IsPlanning = false; RefreshMemoryAccess(); _turnGate.Release(); }
    }

    private void Remember(string request, string answer, IReadOnlyList<JarvisMemoryAccess> memorySources)
    {
        _history.Add(new("user", request) { MemorySources = memorySources });
        _history.Add(new("assistant", answer) { MemorySources = memorySources });
        while (_history.Count > 20) _history.RemoveRange(0, 2);
    }
}
