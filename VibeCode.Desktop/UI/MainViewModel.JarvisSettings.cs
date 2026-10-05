using VibeCode.Services;

namespace VibeCode.UI;

public sealed partial class MainViewModel : IJarvisSettingsActionHost, IJarvisNewChatsHost
{
    public event Action<string>? JarvisSettingsRequested;
    public event Action? JarvisAppearanceRequested;

    Task<JarvisActionReceipt> IJarvisSettingsActionHost.ExecuteSettingsActionAsync(JarvisAction action,
        CancellationToken cancellationToken)
    {
        var appearance = (AppSettings.Current.UiMode, AppSettings.Current.Borderless);
        var receipt = JarvisSettingsActions.Execute(action, cancellationToken,
            JarvisSettingsRequested is null ? null : category => JarvisSettingsRequested.Invoke(category));
        if (action.Kind == "update_settings")
        {
            if (appearance != (AppSettings.Current.UiMode, AppSettings.Current.Borderless))
                JarvisAppearanceRequested?.Invoke();
            if (action.Changes.Any(change => change.Name.StartsWith("Phone", StringComparison.Ordinal)))
            {
                var phone = PhoneBridgeService.Instance;
                phone.NotifyEnabledChanged();
                phone.SetPort(AppSettings.Current.PhoneBridgePort is > 0 and <= 65535
                    ? AppSettings.Current.PhoneBridgePort : PhoneBridgeService.DefaultPort);
                if (!AppSettings.Current.PhoneEnabled || !AppSettings.Current.PhoneBridgeEnabled) phone.Stop();
                else
                {
                    phone.Start();
                    if (!phone.Running) receipt = receipt with { Message = receipt.Message + "\nPhone access could not start: " + phone.Error };
                }
            }
        }
        return Task.FromResult(receipt);
    }

    async Task<JarvisActionReceipt> IJarvisNewChatsHost.CreateChatsAsync(string path, int count, CancellationToken cancellationToken)
    {
        JarvisPathPolicy.RequireExisting(path);
        if (count is < 1 or > 12) throw new ArgumentException("Create between 1 and 12 chats at a time.");
        cancellationToken.ThrowIfCancellationRequested();
        var created = new List<ChatViewModel>();
        try
        {
            // NewChat owns the ordinary provider, account, model and permission defaults. No task is sent.
            for (var i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                created.Add(NewChat(path));
            }
            await Task.WhenAll(created.Select(chat => chat.WaitForJarvisReadyAsync(
                new(chat.Provider, chat.Model, chat.Effort), cancellationToken, verifySelection: false)));
            HideBridge();
            ActiveChat = created[^1];
            _jarvisOpenedChat = null;
            if (created.Any(chat => !Chats.Contains(chat) || !JarvisPathPolicy.PathsEqual(chat.Cwd, path)))
                throw new InvalidOperationException("VibeCode did not open every requested chat.");
            return new("create_chats", path,
                $"Opened {created.Count} new chat{(created.Count == 1 ? "" : "s")} in {path}. Each is ready for a task.");
        }
        catch (Exception ex)
        {
            if (created.Count > 0) throw new InvalidOperationException(
                $"Opened {created.Count} of {count} requested chats in {path}. {ex.Message}", ex);
            throw;
        }
    }
}
