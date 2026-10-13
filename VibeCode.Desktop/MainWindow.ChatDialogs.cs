using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using VibeCode.UI;

namespace VibeCode;

public partial class MainWindow
{
    /// <summary>Chat removal uses the same owned, borderless shell as Rename. Only the named action confirms.</summary>
    private Window CreateChatConfirmation(ChatViewModel chat, bool deleting)
    {
        var action = deleting ? "Delete chat" : "Close chat";
        var name = string.IsNullOrWhiteSpace(chat.Title) ? "this chat" : $"\"{chat.Title}\"";
        var copy = new StackPanel();
        var question = new TextBlock
        {
            Text = deleting ? $"Delete {name}?" : $"Close {name}?",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        question.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        copy.Children.Add(question);
        if (deleting)
        {
            AddExplanation("It will be removed from VibeCode and won't come back in the sidebar, Projects or Recent.");
            AddExplanation("The provider's own transcript file is left on disk, so this doesn't erase anything outside VibeCode.");
        }
        // Starting a provider does not imply an active turn. Match the existing close warning.
        else if (chat.Status is "preparing" or "running")
            AddExplanation($"{chat.AgentDisplay} is still working and will be stopped.");

        var cancel = new Button
        {
            Name = "CancelChatRemoval",
            Content = "Cancel",
            IsCancel = true,
            IsDefault = true,
            MinWidth = 86,
            MinHeight = 36,
            Padding = new Thickness(16, 8, 16, 8),
        };
        var confirm = new Button
        {
            Name = "ConfirmChatRemoval",
            Content = action,
            MinWidth = 104,
            MinHeight = 36,
            Padding = new Thickness(16, 8, 16, 8),
            Margin = new Thickness(8, 0, 0, 0),
        };
        cancel.SetResourceReference(StyleProperty, "GhostButton");
        confirm.SetResourceReference(StyleProperty, deleting ? "DangerButton" : "PrimaryButton");
        // The shared button styles suppress focus adorners. Use the app's accent so keyboard focus
        // remains visible on a dark surface as well as on the destructive action.
        var outline = new FrameworkElementFactory(typeof(Border));
        outline.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        outline.SetValue(MarginProperty, new Thickness(-3));
        outline.SetResourceReference(Border.BorderBrushProperty, "Accent");
        outline.SetResourceReference(Border.CornerRadiusProperty, "Rad8");
        var focus = new Style(typeof(Control));
        focus.Setters.Add(new Setter(TemplateProperty, new ControlTemplate(typeof(Control)) { VisualTree = outline }));
        foreach (var button in new[] { cancel, confirm })
        {
            button.FocusVisualStyle = focus;
            AutomationProperties.SetName(button, button.Content.ToString());
        }

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 22, 0, 0),
        };
        actions.Children.Add(cancel);
        actions.Children.Add(confirm);
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new ScrollViewer
        {
            Content = copy,
            MaxHeight = Math.Max(120, Math.Min(360, SystemParameters.WorkArea.Height - 180)),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
        });
        panel.Children.Add(actions);

        var dialog = CreateThemedDialog(action, panel, Math.Min(460, SystemParameters.WorkArea.Width - 24));
        dialog.SetResourceReference(FontFamilyProperty, "Ui");
        KeyboardNavigation.SetTabNavigation(dialog, KeyboardNavigationMode.Cycle);
        AutomationProperties.SetName(dialog, question.Text);
        confirm.Click += (_, _) => dialog.DialogResult = true;
        cancel.Click += (_, _) => dialog.DialogResult = false;
        dialog.Loaded += (_, _) => cancel.Focus();
        return dialog;

        void AddExplanation(string text)
        {
            var explanation = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                Margin = new Thickness(0, 12, 0, 0),
            };
            explanation.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            copy.Children.Add(explanation);
        }
    }
}
