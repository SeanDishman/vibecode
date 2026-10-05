using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using VibeCode;
using VibeCode.UI;

internal static partial class Program
{
    private static void VerifyMainChatScrolling(Grid host, Window window)
    {
        var resources = Application.Current.Resources;
        resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(
            new Uri("/VibeCode;component/Themes/Dark.xaml", UriKind.Relative)));
        resources["BoolVis"] = new BooleanToVisibilityConverter();
        resources["ShowIf"] = new NonEmptyToVisibilityConverter();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        // Build the real shell without showing it or starting providers, then mount its actual message control
        // in the off-screen host. The XAML-wired scroll handler remains attached to this transcript.
        var shell = (MainWindow)typeof(MainWindow).GetConstructors(flags)
            .Single(ctor => ctor.GetParameters().Length == 3).Invoke([new MainViewModel(), null, true]);
        try
        {
            var nested = new ScrollViewer { Height = 50, Content = new Border { Height = 2000 } };
            var rows = new ObservableCollection<TranscriptRow>();
            for (var i = 0; i < 100; i++) rows.Add(new TranscriptRow { Height = 60, Body = i == 0 ? nested : null });
            var list = (ListBox)shell.FindName("MsgList");
            ((Panel)list.Parent).Children.Remove(list);
            CreateTranscript(new ListCollectionView(rows), list);
            list.Visibility = Visibility.Visible;
            host.Children.Add(list);
            Pump("real main-chat transcript loads");
            var scroll = FindScrollViewer(list)!;
            Check(AtBottom(scroll), "main-chat transcript initially follows the bottom");
            scroll.ScrollToVerticalOffset(scroll.ScrollableHeight);
            Pump("manual arrival at the main-chat bottom settles");
            rows[^1].Height += 800;
            Pump("main-chat streaming extent and offset changes settle");
            Check(AtBottom(scroll), "main chat follows streaming output through virtualization corrections");

            scroll.ScrollToVerticalOffset(scroll.ScrollableHeight);
            Pump("main-chat bottom before resizing settles");
            window.Height -= 160;
            Pump("main-chat viewport shrinks");
            Check(AtBottom(scroll), "main chat follows the bottom when its viewport shrinks");
            window.Height += 160;
            Pump("main-chat viewport grows");
            Check(AtBottom(scroll), "main chat follows the bottom when its viewport grows");

            scroll.ScrollToVerticalOffset(0);
            Pump("main-chat reader scrolls to a message with a nested viewer");
            Check(nested.IsLoaded && nested.ScrollableHeight > 0, "nested scrolling regression uses a realized message viewer");
            nested.ScrollToBottom();
            Pump("nested message viewer scrolls while reading");
            nested.ScrollToTop();
            Pump("nested message viewer returns to its top");
            rows.Add(new TranscriptRow { Height = 360 });
            typeof(MainWindow).GetMethod("OnChatItemsChanged", flags)!.Invoke(shell, null);
            Pump("new main-chat output after nested scrolling settles");
            Check(scroll.VerticalOffset < 1, "nested viewer scrolls do not reattach a reader to new output");

            scroll.ScrollToBottom();
            Pump("main-chat reader returns to live output");
            rows.Add(new TranscriptRow { Height = 360 });
            typeof(MainWindow).GetMethod("OnChatItemsChanged", flags)!.Invoke(shell, null);
            // The queued follow runs at Background priority, after WPF processes this upward scroll.
            scroll.ScrollToVerticalOffset(0);
            Pump("reader scrolls up before a queued output-follow callback runs");
            Check(scroll.VerticalOffset < 1, "queued following respects an intervening upward scroll");
            host.Children.Clear();
            Pump("main chat unloads before shared-terminal checks");
            VerifySharedTerminalScrolling(host, window, shell);
            VerifyTranscriptColumn(host, window, list);
        }
        finally
        {
            host.Children.Clear();
            shell.Close();
        }
    }
}
