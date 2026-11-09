using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace VibeCode.UI;

// A finish on the flow graph: a gold ring pulses off the agent's card (and off its project hub when a whole Bridge run
// completes). WPF animation on its own clock, removed when it ends; nothing here runs per frame.
public sealed partial class WorkflowGraph : IWorkflowCelebrating
{
    private const int RingZ = 1;
    private static readonly SolidColorBrush Gold = Freeze(new SolidColorBrush(Color.FromRgb(0xF5, 0xCB, 0x5C)));

    public void Celebrate(AgentActivityNode agent, bool wholeRun)
    {
        if (!WorkflowCelebrations.MotionAllowed(this, MotionEnabled)) return;
        if (_cards.TryGetValue(agent.Id, out var card)) Ring(card, wholeRun ? 3 : 2);
        if (wholeRun && _nodes.TryGetValue("project:" + agent.ProjectKey, out var hub) && !double.IsNaN(GetLeft(hub)))
            Ring(new Rect(GetLeft(hub), GetTop(hub), hub.Width, hub.Height), 3);
    }

    /// <summary>Count pulses of a gold ring growing out of <paramref name="bounds"/> and fading, then gone.</summary>
    private void Ring(Rect bounds, int count)
    {
        var scale = new ScaleTransform(1, 1);
        var ring = new Border
        {
            Width = bounds.Width, Height = bounds.Height, CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(3),
            BorderBrush = Gold, IsHitTestVisible = false, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = scale,
        };
        SetLeft(ring, bounds.Left);
        SetTop(ring, bounds.Top);
        SetZIndex(ring, RingZ);
        Children.Add(ring);
        var pulse = new Duration(TimeSpan.FromSeconds(WorkflowCelebrations.Seconds / count));
        var grow = new DoubleAnimation(1, 1.18, pulse) { RepeatBehavior = new RepeatBehavior(count), EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        var fade = new DoubleAnimation(1, 0, pulse) { RepeatBehavior = new RepeatBehavior(count) };
        fade.Completed += (_, _) => Children.Remove(ring);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        ring.BeginAnimation(OpacityProperty, fade);
    }

    private static T Freeze<T>(T freezable) where T : Freezable { freezable.Freeze(); return freezable; }
}
