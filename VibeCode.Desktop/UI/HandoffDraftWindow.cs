using System.Windows;
using System.Windows.Controls;

namespace VibeCode.UI;

/// <summary>The editable first prompt for "Continue in a fresh chat". Nothing is sent from here: the draft lands in the
/// new chat's composer, where the user reviews it and sends it like any other prompt.</summary>
internal sealed class HandoffDraftWindow : Window
{
    private readonly TextBox _draft;
    public string Draft => _draft.Text;

    public HandoffDraftWindow(string draft, string providerName)
    {
        Title = "Continue in a fresh chat";
        Width = 660;
        Height = 540;
        MinWidth = 420;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "Bg1");
        SetResourceReference(FontFamilyProperty, "Ui");
        var intro = new TextBlock
        {
            Text = $"A new {providerName} chat in this project opens with this prompt in its composer. Edit it here or there, then send it.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        };
        intro.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _draft = new TextBox
        {
            Text = draft, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontSize = 12.5,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        System.Windows.Automation.AutomationProperties.SetName(_draft, "First prompt for the fresh chat");
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(12, 5, 12, 5) };
        cancel.SetResourceReference(StyleProperty, "GhostButton");
        var start = new Button { Content = "Start fresh chat", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(8, 0, 0, 0) };
        start.SetResourceReference(StyleProperty, "PrimaryButton");
        start.Click += (_, _) => DialogResult = true;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(start);
        var layout = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(intro, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        layout.Children.Add(intro);
        layout.Children.Add(buttons);
        layout.Children.Add(_draft);
        Content = layout;
        Loaded += (_, _) => _draft.Focus();
    }
}
