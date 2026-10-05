using System.Text.Json;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    /// <summary>Fast mode belongs to each chat. Turning it on in one Bridge pane must not reach a sibling, and a chat
    /// saved and rebuilt must come back with its own setting rather than the new-chat seed (whatever any chat toggled
    /// last), which is how one pane's toggle used to turn every Claude chat fast after the next launch.</summary>
    private static void VerifyFastModeIsolation()
    {
        AppSettings.Current.FastMode = false;
        var (vm, team) = Team("fast-mode-isolation", 3, "claude");
        team[1].SetFastMode(true);
        Check("turning fast mode on in Claude 2 leaves Claude 1 and Claude 3 alone",
            team[1].FastMode && !team[0].FastMode && !team[2].FastMode);
        Check("the toggle only seeds chats created later", AppSettings.Current.FastMode);

        // SnapshotSession refuses to write the chat list until startup restore has run (its data-loss guard).
        typeof(MainViewModel).GetField("_sessionRestored", Flags)!.SetValue(vm, true);
        Call(vm, "SnapshotSession");
        var saved = AppSettings.Current.OpenChats.ToDictionary(o => o.SessionId!);
        Check("each chat's own fast mode is saved with it",
            saved[team[0].SessionId!].FastMode == false && saved[team[1].SessionId!].FastMode == true
            && saved[team[2].SessionId!].FastMode == false);
        var reloaded = JsonSerializer.Deserialize<OpenChatState>(JsonSerializer.Serialize(saved[team[0].SessionId!]))!;
        Check("a saved fast-mode-off chat survives a settings reload as off, not as the seed",
            reloaded.FastMode == false && AppSettings.Current.FastMode);
        var legacy = JsonSerializer.Deserialize<OpenChatState>("""{"Cwd":"C:\\x","SessionId":"old","Provider":"claude"}""")!;
        Check("snapshots from before per-chat saving have no value and keep using the seed", legacy.FastMode is null);
        AppSettings.Current.FastMode = false;
    }
}
