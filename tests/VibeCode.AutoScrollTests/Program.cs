using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using VibeCode.UI;

internal static partial class Program
{
    private static int _checks;

    [STAThread]
    private static int Main()
    {
        Environment.SetEnvironmentVariable("VIBECODE_DATA_DIR", Path.Combine(Environment.CurrentDirectory,
            "artifacts", "auto-scroll-fix", "test-data-" + Guid.NewGuid().ToString("N")));
        Environment.SetEnvironmentVariable("VIBECODE_HIDDEN", "1");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        // Exercise real WPF Loaded/layout/visibility events without taking focus or showing a taskbar window.
        var host = new Grid();
        var window = new Window
        {
            Content = host, Width = 360, Height = 260, Left = -32000, Top = -32000,
            ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
        };
        try
        {
            var list = new ListBox { Visibility = Visibility.Collapsed };
            AutoScroll.SetEnabled(list, true);
            host.Children.Add(list);
            window.Show();
            Pump("collapsed transcript must not starve rendering or input");
            Check(list.IsLoaded, "collapsed transcript receives the real Loaded event");

            list.Visibility = Visibility.Visible;
            Pump("showing the transcript resolves its template");
            Check(list.Template is not null, "visible transcript has a template");
            host.Children.Clear();
            Pump("unloading a transcript leaves the dispatcher responsive");

            var delayed = new ContentControl
            {
                Template = new ControlTemplate(typeof(ContentControl))
                {
                    VisualTree = new FrameworkElementFactory(typeof(Border)),
                },
            };
            AutoScroll.SetEnabled(delayed, true);
            host.Children.Add(delayed);
            Pump("a visible template without a scroller must not spin");

            var factory = new FrameworkElementFactory(typeof(ScrollViewer), "Scroller");
            var content = new FrameworkElementFactory(typeof(Border));
            content.SetValue(FrameworkElement.HeightProperty, 2400.0);
            factory.AppendChild(content);
            delayed.Template = new ControlTemplate(typeof(ContentControl)) { VisualTree = factory };
            Pump("a scroller introduced by a later template is attached");
            var inner = (ScrollViewer)delayed.Template.FindName("Scroller", delayed);
            Check(inner.ScrollableHeight > 0 && AtBottom(inner), "late template starts pinned to the bottom");
            host.Children.Clear();
            Pump("late template unloads cleanly");

            var transcript = new StackPanel();
            for (var i = 0; i < 40; i++) transcript.Children.Add(new Border { Height = 40 });
            var scroll = new ScrollViewer { Content = transcript };
            AutoScroll.SetEnabled(scroll, true);
            host.Children.Add(scroll);
            Pump("direct scroll viewer becomes usable");
            Check(AtBottom(scroll), "initial transcript is pinned");
            transcript.Children.Add(new Border { Height = 200 });
            Pump("new output remains pinned");
            Check(AtBottom(scroll), "new output follows the bottom");
            scroll.ScrollToVerticalOffset(120);
            Pump("user can scroll upward");
            var readingOffset = scroll.VerticalOffset;
            transcript.Children.Add(new Border { Height = 200 });
            Pump("new output preserves reading position");
            Check(Math.Abs(scroll.VerticalOffset - readingOffset) < 1, "reading position is preserved");
            scroll.Visibility = Visibility.Collapsed;
            Pump("hide transcript");
            scroll.Visibility = Visibility.Visible;
            Pump("show transcript again");
            Check(Math.Abs(scroll.VerticalOffset - readingOffset) < 1, "hide/show preserves reading position");

            scroll.ScrollToBottom();
            Pump("return to newest output");
            AutoScroll.SetEnabled(scroll, false);
            // Use a finite offset within the behavior's sticky zone; ScrollToBottom itself asks WPF to keep
            // following the bottom (infinite offset), even without this behavior.
            scroll.ScrollToVerticalOffset(scroll.ScrollableHeight - 10);
            Pump("disabled behavior accepts a finite offset");
            var disabledOffset = scroll.VerticalOffset;
            transcript.Children.Add(new Border { Height = 200 });
            Pump("disabled behavior does not scroll new output");
            Check(Math.Abs(scroll.VerticalOffset - disabledOffset) < 1, "disabling removes behavior handlers");
            AutoScroll.SetEnabled(scroll, true);
            Pump("reenabling the behavior remains responsive");
            Check(AtBottom(scroll), "reenabling attaches and pins once");
            host.Children.Clear();
            Pump("plain transcript unloads before virtualization checks");
            VerifyVirtualizedTranscript(host, window);
            host.Children.Clear();
            Pump("virtualized transcript unloads before main-chat checks");
            VerifyMainChatScrolling(host, window);
            Console.WriteLine($"PASS: {_checks} auto-scroll checks");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: " + ex.Message);
            foreach (var change in ScrollEvents) Console.Error.WriteLine(change);
            return 1;
        }
        finally
        {
            window.Close();
            app.Shutdown();
        }
    }

    private static bool AtBottom(ScrollViewer scroll) => Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < 1;

    private static void Pump(string assertion)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        var idle = false;
        var marker = dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            idle = true;
            frame.Continue = false;
        }));
        // An external deadline still fires when a buggy callback endlessly requeues above input priority.
        using var deadline = new System.Threading.Timer(_ => dispatcher.BeginInvoke(DispatcherPriority.Send,
            new Action(() => frame.Continue = false)), null, 2000, Timeout.Infinite);
        Dispatcher.PushFrame(frame);
        marker.Abort();
        Check(idle, assertion);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _checks++;
    }
}
