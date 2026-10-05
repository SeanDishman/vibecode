using System.Windows;
using VibeCode.UI;

namespace VibeCode;

public partial class MainWindow
{
    private async void OnSteerQueued(object sender, RoutedEventArgs e)
    {
        if (Ctx<QueuedItem>(sender) is not { } queued) return;
        if (!await queued.Owner.SteerQueuedAsync(queued)) return;
        _stickToBottom = true;
        ScrollMsgToBottom();
        _vm.NoteBridgeActivity();
    }

}
