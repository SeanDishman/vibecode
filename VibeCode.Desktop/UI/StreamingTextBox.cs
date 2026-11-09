using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace VibeCode.UI;

/// <summary>
/// Read-only streaming text that keeps WPF's native caret and selection intact. Binding directly to Text replaces
/// the entire text container on every delta, clearing selection and scrolling its reset caret into view.
/// </summary>
public sealed class StreamingTextBox : TextBox
{
    public static readonly DependencyProperty StreamTextProperty = DependencyProperty.Register(
        nameof(StreamText), typeof(string), typeof(StreamingTextBox),
        new PropertyMetadata("", (target, _) => ((StreamingTextBox)target).ApplyStreamText()));

    public string StreamText
    {
        get => (string)GetValue(StreamTextProperty);
        set => SetValue(StreamTextProperty, value);
    }

    internal bool IsReadingSelection => SelectionLength > 0 || IsMouseCaptureWithin;

    private DispatcherOperation? _pendingRefresh;
    private bool _updating;

    public StreamingTextBox()
    {
        IsReadOnly = true;
        IsUndoEnabled = false;
        // A range continues to pause updates after focus moves; keep that state visible until it is cleared.
        IsInactiveSelectionHighlightEnabled = true;
        SelectionChanged += (_, _) => QueueRefresh();
        LostMouseCapture += (_, _) => QueueRefresh();
        Loaded += (_, _) => QueueRefresh();
        Unloaded += (_, _) =>
        {
            _pendingRefresh?.Abort();
            _pendingRefresh = null;
        };
        IsVisibleChanged += (_, args) =>
        {
            // Collapsing/reopening a thought or leaving its chat must not preserve an invisible pause.
            if (args.NewValue is false) Select(CaretIndex, 0);
            QueueRefresh();
        };
        DataContextChanged += (_, _) =>
        {
            // Recycling must not carry a range from a different thought row into this one.
            Select(0, 0);
            QueueRefresh();
        };
    }

    private void QueueRefresh()
    {
        if (_updating || !IsLoaded || IsReadingSelection || _pendingRefresh is not null || Text == StreamText) return;
        // Finish the native selection/mouse event before applying any text that arrived during the gesture.
        _pendingRefresh = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _pendingRefresh = null;
            ApplyStreamText();
        }));
    }

    private void ApplyStreamText()
    {
        var incoming = StreamText ?? "";
        if (_updating || Text == incoming || IsReadingSelection) return;
        _updating = true;
        try
        {
            // New output stays in StreamText while the reader selects/copies. One latest snapshot is applied
            // after selection ends; neither the model's output nor the native mouse-selection anchor is lost.
            if (incoming.StartsWith(Text, StringComparison.Ordinal))
                AppendText(incoming[Text.Length..]);
            else
            {
                // Providers can replace a completed block, and virtualized controls can display another row.
                var caret = Math.Min(CaretIndex, incoming.Length);
                SetCurrentValue(TextProperty, incoming);
                Select(caret, 0);
            }
        }
        finally { _updating = false; }
    }
}
