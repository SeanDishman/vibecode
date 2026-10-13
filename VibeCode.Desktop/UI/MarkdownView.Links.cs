using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using VibeCode.Services;

namespace VibeCode.UI;

public partial class MarkdownView
{
    public static readonly DependencyProperty LinkBaseDirectoryProperty = DependencyProperty.Register(
        nameof(LinkBaseDirectory), typeof(string), typeof(MarkdownView), new PropertyMetadata(null));

    /// <summary>An explicit base for standalone previews. Chat replies use their owning pane's Cwd.</summary>
    public string? LinkBaseDirectory
    {
        get => (string?)GetValue(LinkBaseDirectoryProperty);
        set => SetValue(LinkBaseDirectoryProperty, value);
    }

    public MarkdownView()
    {
        // WPF copies rich selections by serializing their Hyperlink.Command. A private ICommand that holds
        // this viewer throws during both Copy and Ctrl+C; a standard routed command can be serialized safely.
        CommandBindings.Add(new CommandBinding(NavigationCommands.GoToPage,
            (_, e) => { FollowLink(e.Parameter); e.Handled = true; },
            (_, e) => { e.CanExecute = true; e.Handled = true; }));
        Engine.HyperlinkCommand = NavigationCommands.GoToPage;
    }

    private void ConfigureLinks(DependencyObject element)
    {
        if (element is Hyperlink link) link.Command = NavigationCommands.GoToPage;
        // MdXaml 1.27 resets the engine's command when an engine is replaced. Bind each newly rendered document
        // too, so engine replacement and restored FlowDocuments follow the same route as streaming text.
        foreach (var child in LogicalTreeHelper.GetChildren(element).OfType<DependencyObject>()) ConfigureLinks(child);
    }

    private string? LinkDirectory()
    {
        if (!string.IsNullOrWhiteSpace(LinkBaseDirectory)) return LinkBaseDirectory;
        for (DependencyObject? node = this; node is not null;)
        {
            if (node is FrameworkElement { DataContext: ChatViewModel chat }) return chat.Cwd;
            if (node is FrameworkElement { DataContext: PermItem { Owner: { } owner } }) return owner.Cwd;
            node = node is Visual ? VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }

    private void FollowLink(object? parameter)
    {
        var address = parameter is Uri uri ? uri.OriginalString : parameter as string;
        if (!TranscriptLink.TryResolve(address, LinkDirectory(), out var target, out var error))
        {
            ReportLinkError(error ?? "VibeCode could not read this link.");
            return;
        }
        try { OpenLinkTarget(target!); }
        catch (Exception ex) { ReportLinkError("VibeCode could not open this link.\n\n" + target!.Address + "\n\n" + ex.Message); }
    }

    protected virtual void OpenLinkTarget(TranscriptLink target)
    {
        if (target.Kind == TranscriptLinkKind.Anchor)
        {
            Fragment = target.Address;
            return;
        }
        if (target.Kind == TranscriptLinkKind.Web)
        {
            using var opened = Process.Start(new ProcessStartInfo(target.Address) { UseShellExecute = true });
            return;
        }
        if (Directory.Exists(target.Address))
        {
            using var opened = Process.Start(new ProcessStartInfo(target.Address) { UseShellExecute = true });
            return;
        }
        if (!File.Exists(target.Address)) throw new FileNotFoundException("The file no longer exists at this location.");

        // Source, Markdown and scripts are read in the app. Only document/media formats go to a file association;
        // clicking an executable or command file must never run code.
        if (Path.GetExtension(target.Address).ToLowerInvariant() is ".pdf" or ".doc" or ".docx" or ".odt"
            or ".xls" or ".xlsx" or ".ods" or ".ppt" or ".pptx" or ".odp"
            or ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".ico"
            or ".mp3" or ".wav" or ".mp4" or ".webm" or ".mov")
        {
            using var opened = Process.Start(new ProcessStartInfo(target.Address) { UseShellExecute = true });
            return;
        }
        CodeViewerWindow.OpenFile(target.Address, Window.GetWindow(this), target.Line);
    }

    protected virtual void ReportLinkError(string message)
    {
        if (Window.GetWindow(this) is { } owner)
            MessageBox.Show(owner, message, "Open link", MessageBoxButton.OK, MessageBoxImage.Information);
        else MessageBox.Show(message, "Open link", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
