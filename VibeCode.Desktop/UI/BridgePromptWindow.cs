using System.Windows;
using System.Windows.Controls;
using VibeCode.Services;

namespace VibeCode.UI;

/// <summary>The Bridge ⋮ menu's "Bridge prompt": one text box whose text goes at the top of every message the Bridge's
/// agents receive. Saving it empty turns it off.</summary>
internal sealed class BridgePromptWindow : Window
{
    private readonly TextBox _prompt;
    public string Prompt => _prompt.Text;

    public BridgePromptWindow(string prompt)
    {
        Title = "Bridge prompt";
        Width = 560;
        Height = 420;
        MinWidth = 380;
        MinHeight = 260;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "Bg1");
        SetResourceReference(FontFamilyProperty, "Ui");
        var intro = new TextBlock
        {
            Text = "Goes at the top of every message the agents in this Bridge receive. Leave it empty to turn it off.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        };
        intro.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _prompt = new TextBox
        {
            Text = prompt, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontSize = 12.5,
            VerticalContentAlignment = VerticalAlignment.Top,
            MaxLength = BridgeUserPrompt.MaxCharacters, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        System.Windows.Automation.AutomationProperties.SetName(_prompt, "Bridge prompt");
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(12, 5, 12, 5) };
        cancel.SetResourceReference(StyleProperty, "GhostButton");
        var save = new Button { Content = "Save", Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(8, 0, 0, 0) };
        save.SetResourceReference(StyleProperty, "PrimaryButton");
        save.Click += (_, _) => DialogResult = true;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        var layout = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(intro, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        layout.Children.Add(intro);
        layout.Children.Add(buttons);
        layout.Children.Add(_prompt);
        Content = layout;
        Loaded += (_, _) =>
        {
            _prompt.Focus();
            _prompt.CaretIndex = _prompt.Text.Length;
        };
    }
}
