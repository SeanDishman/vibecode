using System.ComponentModel;
using System.Text.Json.Nodes;
using VibeCode.Services;

namespace VibeCode.UI;

public sealed partial class ChatViewModel
{
    internal IEnumerable<string> JarvisSearchableMessages() => Items.Select(item => item switch
    {
        UserItem user => StripInjectedPrelude(user.Text),
        TextItem assistant => assistant.Text,
        _ => "",
    }).Where(text => !string.IsNullOrWhiteSpace(text));

    internal AgentMemoryChatContext JarvisMemoryContext() => MemoryContext();
    internal async Task WaitForJarvisReadyAsync(JarvisSelection selection, CancellationToken cancellationToken,
        bool verifySelection = true)
    {
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, PropertyChangedEventArgs args)
        {
            if (Status is "error" or "closed") failed.TrySetException(new InvalidOperationException("The project chat could not start. Check the selected provider/account."));
            else if (_bridgeSessionInitialized && SessionId is not null) ready.TrySetResult();
        }
        PropertyChanged += Changed;
        try
        {
            if (Status is "error" or "closed") throw new InvalidOperationException("The selected provider could not open a project chat.");
            Changed(this, new(nameof(Status)));
            var configuration = verifySelection ? _bridgeConfigurationApplication : ready.Task;
            var completed = await Task.WhenAny(configuration, failed.Task).WaitAsync(TimeSpan.FromMinutes(2), cancellationToken);
            await completed;
            cancellationToken.ThrowIfCancellationRequested();
            if (_session is null || _session.HasExited || SessionId is null || Status is "error" or "closed")
                throw new InvalidOperationException("The project chat has no available provider session.");
            if (verifySelection && (Provider != selection.Provider || (!string.IsNullOrWhiteSpace(selection.Model)
                && selection.Model != "default" && !string.Equals(Model, selection.Model, StringComparison.OrdinalIgnoreCase))))
                throw new InvalidOperationException("The coding chat did not retain the selected Jarvis provider/model.");
        }
        finally { PropertyChanged -= Changed; }
    }

    internal async Task DispatchJarvisTaskAsync(string task, CancellationToken cancellationToken)
    {
        var session = _session ?? throw new InvalidOperationException("The coding session is unavailable.");
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Message(JsonNode node)
        {
            var type = node["type"]?.ToString();
            if (type == "result" && string.Equals(node["is_error"]?.ToString(), "true", StringComparison.OrdinalIgnoreCase))
                accepted.TrySetException(new InvalidOperationException("The coding provider rejected the task: " + node["result"]?.ToString()));
            else if (type == "assistant" || (type == "stream_event" && node["event"]?["type"]?.ToString() == "message_start")
                || type == "result") accepted.TrySetResult();
        }
        void Permission(VibeCode.Protocol.PermissionRequest request) => accepted.TrySetResult();
        void Exit(int code, string error) => accepted.TrySetException(new InvalidOperationException("The coding provider exited before starting the task."));
        session.MessageReceived += Message;
        session.PermissionRequested += Permission;
        session.Exited += Exit;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Send(task)) throw new InvalidOperationException("The normal coding chat could not accept the requested task.");
            await accepted.Task.WaitAsync(TimeSpan.FromMinutes(2), cancellationToken);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            foreach (var queued in _sendQueue.Where(q => q.Text == task).ToArray()) CancelQueued(queued);
            Interrupt();
            throw;
        }
        finally
        {
            session.MessageReceived -= Message;
            session.PermissionRequested -= Permission;
            session.Exited -= Exit;
        }
    }
}
