using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VibeCode.Services;

namespace VibeCode.UI;

/// <summary>
/// "Open in…" picker for an edited file. Lists the editors actually detected on this machine, with the
/// last-used one preselected so the common case is one click. Built in code so it adds no shared XAML.
/// </summary>
public sealed class OpenInEditorDialog : Window
{
    private readonly ListBox _list = new();

    public ExternalEditor? Chosen { get; private set; }

    private static readonly Brush Bg = new SolidColorBrush(Color.FromRgb(0x12, 0x14, 0x1b));
    private static readonly Brush Fg = new SolidColorBrush(Color.FromRgb(0xf0, 0xf2, 0xf7));
    private static readonly Brush Faint = new SolidColorBrush(Color.FromRgb(0x8b, 0x93, 0xa6));

    public OpenInEditorDialog(string file, int? line)
    {
        Title = "Open in editor";
        Width = 440; Height = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Bg; Foreground = Fg;
        FontFamily = new FontFamily("Segoe UI");

        var root = new DockPanel { Margin = new Thickness(18) };

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        header.Children.Add(new TextBlock
        {
            Text = "Open in editor", FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Fg,
        });
        header.Children.Add(new TextBlock
        {
            Text = Path.GetFileName(file) + (line is > 0 ? $"  ·  line {line}" : ""),
            FontSize = 11.5, Foreground = Faint, Margin = new Thickness(0, 3, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var open = new Button { Content = "Open", Padding = new Thickness(16, 7, 16, 7), IsDefault = true };
        open.Click += (_, _) => Accept();
        buttons.Children.Add(cancel);
        buttons.Children.Add(open);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        _list.Background = Brushes.Transparent;
        _list.BorderThickness = new Thickness(0);
        _list.Foreground = Fg;
        _list.MouseDoubleClick += (_, _) => Accept();

        var editors = ExternalEditorService.Detect();
        foreach (var ed in editors)
        {
            var panel = new StackPanel { Margin = new Thickness(2, 5, 2, 5) };
            panel.Children.Add(new TextBlock { Text = ed.Name, FontSize = 13, Foreground = Fg });
            if (!string.IsNullOrEmpty(ed.Executable))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = ed.Executable, FontSize = 10, Foreground = Faint,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
            }
            _list.Items.Add(new ListBoxItem { Content = panel, Tag = ed, Padding = new Thickness(8, 2, 8, 2) });
        }

        var preferred = ExternalEditorService.PreferredId;
        var pick = _list.Items.Cast<ListBoxItem>().FirstOrDefault(i => ((ExternalEditor)i.Tag).Id == preferred);
        _list.SelectedItem = pick ?? _list.Items.Cast<ListBoxItem>().FirstOrDefault();

        root.Children.Add(new ScrollViewer
        {
            Content = _list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });

        Content = root;
        Loaded += (_, _) => _list.Focus();
    }

    private void Accept()
    {
        if (_list.SelectedItem is ListBoxItem { Tag: ExternalEditor ed })
        {
            Chosen = ed;
            DialogResult = true;
        }
    }
}
