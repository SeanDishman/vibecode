using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace VibeCode.UI;

// A finish on the kanban board: the card that just landed in Done wears a gold edge for the length of the moment.
public sealed partial class WorkflowBoardView : IWorkflowCelebrating
{
    private static readonly SolidColorBrush Gold = Freeze(new SolidColorBrush(Color.FromRgb(0xF5, 0xCB, 0x5C)));

    public void Celebrate(AgentActivityNode agent, bool wholeRun)
    {
        if (!WorkflowCelebrations.MotionAllowed(this, MotionEnabled) || !_cards.TryGetValue(agent.Id, out var card)) return;
        card.BorderBrush = Gold;
        card.BorderThickness = new Thickness(wholeRun ? 3 : 2);
        var done = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(WorkflowCelebrations.Seconds) };
        done.Tick += (_, _) =>
        {
            done.Stop();
            card.ClearValue(Control.BorderThicknessProperty);
            card.SetResourceReference(Control.BorderBrushProperty, "Border");
        };
        done.Start();
    }

    private static T Freeze<T>(T freezable) where T : Freezable { freezable.Freeze(); return freezable; }
}
