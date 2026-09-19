using System.Windows;
using System.Windows.Controls;

namespace VibeCode.UI;

internal enum UnsavedCodeChoice { Cancel, Save, Discard }

/// <summary>Named actions instead of an ambiguous Yes/No prompt. Escape and the title-bar X cancel.</summary>
internal sealed class UnsavedCodeDialog : Window
{
    public UnsavedCodeChoice Choice { get; private set; } = UnsavedCodeChoice.Cancel;

    public UnsavedCodeDialog(string fileName, Window owner)
    {
        Owner = owner;
        Title = "Save your changes?";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        if (Environment.GetEnvironmentVariable("VIBECODE_HIDDEN") == "1")
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = 6200; Top = 240; ShowActivated = false;
        }
        SetResourceReference(BackgroundProperty, "Bg1");
        SetResourceReference(ForegroundProperty, "Text");
        SetResourceReference(FontFamilyProperty, "Ui");

        var body = new StackPanel { Margin = new Thickness(22) };
        body.Children.Add(new TextBlock
        {
            Text = "Save changes before closing?", FontSize = 17, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 10),
        });
        body.Children.Add(new TextBlock
        {
            Text = $"{fileName} has unsaved changes. Cancel keeps the editor open.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 22), FontSize = 13,
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var discard = AddButton(buttons, "DiscardChanges", "Don't save", UnsavedCodeChoice.Discard);
        discard.SetResourceReference(StyleProperty, "GhostButton");
        var cancel = AddButton(buttons, "CancelClose", "Cancel", UnsavedCodeChoice.Cancel);
        cancel.IsCancel = true;
        var save = AddButton(buttons, "SaveChanges", "Save", UnsavedCodeChoice.Save);
        save.SetResourceReference(StyleProperty, "PrimaryButton");
        save.IsDefault = true;
        body.Children.Add(buttons);
        Content = body;
        Loaded += (_, _) => cancel.Focus();
    }

    private Button AddButton(Panel panel, string name, string label, UnsavedCodeChoice choice)
    {
        var button = new Button
        {
            Name = name, Content = label, MinWidth = 78, Padding = new Thickness(12, 7, 12, 7),
            Margin = new Thickness(6, 0, 0, 0),
        };
        button.SetResourceReference(StyleProperty, "GhostButton");
        button.Click += (_, _) => { Choice = choice; DialogResult = choice != UnsavedCodeChoice.Cancel; };
        panel.Children.Add(button);
        return button;
    }
}
