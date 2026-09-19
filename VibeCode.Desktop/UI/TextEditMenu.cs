using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace VibeCode.UI;

/// <summary>
/// Gives every text surface in the app a VibeCode right-click menu.
/// <para>
/// A WPF text control with no <see cref="FrameworkElement.ContextMenu"/> falls back to the framework's built-in
/// editing menu, which is a stock light-themed Win32 popup. On VibeCode's dark surfaces that rendered as white
/// text on white: Cut / Copy / Paste were there but invisible. Rather than hanging a menu off the couple of
/// dozen text controls in MainWindow.xaml one at a time - and missing every one added later, plus the ones
/// inside third-party templates - this hooks the loaded event for the text control classes and hands each of
/// them our own menu the first time it appears.
/// </para>
/// <para>
/// A control that already declares a ContextMenu is left alone, so the hand-written menus (BlockMenu, ChatMenu,
/// the account menus) keep working exactly as they did.
/// </para>
/// </summary>
public static class TextEditMenu
{
    private static bool _installed;

    /// <summary>Marks the menus we built, so a second pass can tell ours from one the framework installed.</summary>
    private static readonly DependencyProperty OursProperty =
        DependencyProperty.RegisterAttached("Ours", typeof(bool), typeof(TextEditMenu), new PropertyMetadata(false));

    /// <summary>Called once from App startup, before the main window is created.</summary>
    public static void Install()
    {
        if (_installed) return;
        _installed = true;

        // Hung off the right-button press, NOT off Loaded.
        //
        // Loaded looks like the obvious hook and quietly does not work here: WPF only raises it on elements
        // something is already listening to, so a class handler for Loaded fires for TextBox (whose template
        // wiring listens) and never once for FlowDocumentScrollViewer. Measured, not assumed - which is why
        // every markdown reply kept WPF's white "_Copy / Select A_ll" popup while the composer looked fixed.
        //
        // The button press is a real input event routed to every element under the pointer, and it lands
        // before the menu opens - so we only have to put the right menu in place and let WPF open it.
        EventManager.RegisterClassHandler(typeof(FrameworkElement), UIElement.PreviewMouseRightButtonDownEvent,
            new MouseButtonEventHandler(OnRightButtonDown), true);
        // Backstop for the Menu key and any path the pointer route misses.
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.ContextMenuOpeningEvent,
            new ContextMenuEventHandler(OnContextMenuOpening), true);
    }

    /// <summary>The controls that own editable or selectable text, and so would otherwise fall back to the
    /// framework's own menu. Everything else in the app supplies its own or wants none.</summary>
    private static bool IsTextSurface(object candidate) =>
        candidate is TextBoxBase or PasswordBox or FlowDocumentScrollViewer;

    private static void OnRightButtonDown(object sender, MouseButtonEventArgs e) => EnsureOurMenu(sender);

    /// <summary>Put our menu on the control if it is still showing somebody else's. Returns true if it did.</summary>
    internal static bool EnsureOurMenu(object sender)
    {
        if (!IsTextSurface(sender) || sender is not FrameworkElement target) return false;
        if (!ShouldReplace(target)) return false;
        target.ContextMenu = BuildFor(target);
        return true;
    }

    private static void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (!EnsureOurMenu(sender) || sender is not FrameworkElement target) return;

        // Reached without a right-button press (the Menu key), so the menu was chosen before we swapped it.
        // Assigning it now is too late for WPF to notice, so put ours up directly and suppress the other.
        var menu = target.ContextMenu!;
        menu.PlacementTarget = target;
        menu.Placement = PlacementMode.Center;
        e.Handled = true;
        menu.IsOpen = true;
    }

    /// <summary>
    /// Whether this control is still showing somebody else's menu.
    /// <para>
    /// Two of them arrive without anyone asking: WPF's editing menu, which reaches a FlowDocumentScrollViewer
    /// through the control's default style, and the one MdXaml's MarkdownScrollViewer installs for itself. Both
    /// are unstyled, so both render as the stock white popup - which on this app's surfaces is white on white.
    /// </para>
    /// Every menu VibeCode attaches carries the DarkContextMenu style, so an unstyled menu is by definition not
    /// one of ours and is safe to displace; a hand-written one is left exactly as it is.
    /// </summary>
    private static bool ShouldReplace(FrameworkElement target)
    {
        if (target.ContextMenu is not { } existing) return true;
        if (IsOurs(existing)) return false;
        return existing.Style is null;
    }

    private static bool IsOurs(ContextMenu menu) => (bool)menu.GetValue(OursProperty);

    private static ContextMenu BuildFor(FrameworkElement target)
    {
        var menu = new ContextMenu
        {
            Style = Application.Current?.TryFindResource("DarkContextMenu") as Style,
            MinWidth = 196,
        };
        menu.SetValue(OursProperty, true);

        switch (target)
        {
            case PasswordBox:
                // No Cut/Copy: the control refuses both, and offering greyed-out ways to read a password back
                // is worse than not offering them.
                Add(menu, "Paste", ApplicationCommands.Paste, target, "Ctrl+V");
                Add(menu, "Select all", ApplicationCommands.SelectAll, target, "Ctrl+A");
                break;

            case TextBoxBase { IsReadOnly: false }:
                Add(menu, "Undo", ApplicationCommands.Undo, target, "Ctrl+Z");
                Add(menu, "Redo", ApplicationCommands.Redo, target, "Ctrl+Y");
                Separator(menu);
                Add(menu, "Cut", ApplicationCommands.Cut, target, "Ctrl+X");
                Add(menu, "Copy", ApplicationCommands.Copy, target, "Ctrl+C");
                Add(menu, "Paste", ApplicationCommands.Paste, target, "Ctrl+V");
                Add(menu, "Delete", EditingCommands.Delete, target, "Del");
                Separator(menu);
                Add(menu, "Select all", ApplicationCommands.SelectAll, target, "Ctrl+A");
                break;

            default:
                // Read-only text: message bubbles, thinking blocks, markdown replies, tool output.
                Add(menu, "Copy", ApplicationCommands.Copy, target, "Ctrl+C");
                Add(menu, "Select all", ApplicationCommands.SelectAll, target, "Ctrl+A");
                AddTranscriptActions(menu, target);
                break;
        }

        return menu;
    }

    /// <summary>
    /// When the read-only text belongs to a message, offer the same whole-block and whole-conversation copies
    /// the surrounding card offers - otherwise right-clicking the text itself would be a downgrade from
    /// right-clicking the margin beside it.
    /// </summary>
    private static void AddTranscriptActions(ContextMenu menu, FrameworkElement target)
    {
        var item = Ancestors(target).Select(a => a.DataContext).OfType<ItemVm>().FirstOrDefault();
        var chat = Ancestors(target).Select(a => a.DataContext).OfType<ChatViewModel>().FirstOrDefault();
        if (item is null && chat is null) return;

        Separator(menu);
        if (item is not null)
            menu.Items.Add(Item("Copy message", () => SetClipboard(MainWindow.BlockText(item))));
        if (chat is not null)
            menu.Items.Add(Item("Copy whole conversation", () => SetClipboard(MainWindow.ConversationText(chat))));
    }

    private static void Add(ContextMenu menu, string header, ICommand command, IInputElement target, string gesture) =>
        menu.Items.Add(new MenuItem
        {
            Header = header,
            Command = command,
            CommandTarget = target,
            InputGestureText = gesture,
        });

    private static MenuItem Item(string header, Action run) =>
        new() { Header = header, Command = new Relay(run) };

    private static void Separator(ContextMenu menu) =>
        menu.Items.Add(new Separator
        {
            Style = Application.Current?.TryFindResource("DarkSeparator") as Style,
        });

    private static void SetClipboard(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); }
        catch { /* another process holds the clipboard; a copy must never crash the app */ }
    }

    /// <summary>Walk out through the visual tree, then keep going through logical parents so a control hosted in
    /// a popup or a template still finds the message and chat it belongs to.</summary>
    private static IEnumerable<FrameworkElement> Ancestors(DependencyObject? node)
    {
        for (var i = 0; node is not null && i < 64; i++)
        {
            if (node is FrameworkElement element) yield return element;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
    }

    private sealed class Relay(Action run) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => run();
    }
}
