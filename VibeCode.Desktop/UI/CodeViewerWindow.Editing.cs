using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using VibeCode.Services;

namespace VibeCode.UI;

public partial class CodeViewerWindow
{
    private string? _editPath;
    private EditableCodeFile? _editFile;
    private bool _loadingEditor;
    private bool _discardOnClose;
    private bool _askingToClose;

    private bool HasUnsavedChanges => _editFile?.HasChanges(Editor.Text) == true;

    private void InitializeEditing()
    {
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Save,
            (_, e) => { SaveEdits(); e.Handled = true; },
            (_, e) => { e.CanExecute = HasUnsavedChanges; e.Handled = true; }));
        InputBindings.Add(new KeyBinding(ApplicationCommands.Save, Key.S, ModifierKeys.Control));
        Editor.TextArea.TextView.LineTransformers.Add(new EditorColorizer(() => _editPath));
        var menu = new ContextMenu();
        menu.SetResourceReference(StyleProperty, "DarkContextMenu");
        foreach (var (label, command) in new (string, RoutedUICommand)[]
        {
            ("Undo", ApplicationCommands.Undo), ("Redo", ApplicationCommands.Redo),
            ("Cut", ApplicationCommands.Cut), ("Copy", ApplicationCommands.Copy),
            ("Paste", ApplicationCommands.Paste), ("Select all", ApplicationCommands.SelectAll),
        })
            menu.Items.Add(new MenuItem { Header = label, Command = command, CommandTarget = Editor.TextArea });
        Editor.ContextMenu = menu;
    }

    private void SetEditableFile(string? path)
    {
        _editPath = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
        EditButton.IsEnabled = _editPath is not null && File.Exists(_editPath);
        EditButton.ToolTip = EditButton.IsEnabled ? "Edit the current file" : "The file must exist on disk to edit it";
    }

    private void OnEdit(object sender, RoutedEventArgs e)
    {
        if (_editPath is null || _editFile is not null) return;
        try
        {
            // Always reread on entry: an agent can have updated this file since the preview opened.
            var file = EditableCodeFile.Open(_editPath);
            _loadingEditor = true;
            try
            {
                Editor.Text = file.SavedText;
                Editor.Document.UndoStack.ClearAll();
                Editor.Document.UndoStack.MarkAsOriginalFile();
                _editFile = file;
            }
            finally { _loadingEditor = false; }
            Code.Visibility = Visibility.Collapsed;
            Editor.Visibility = Visibility.Visible;
            EditButton.Visibility = Visibility.Collapsed;
            SaveButton.Visibility = Visibility.Visible;
            TitleStat.Visibility = Visibility.Collapsed;
            EditorErrorBanner.Visibility = Visibility.Collapsed;
            UpdateEditorStatus();
            Editor.Focus();
        }
        catch (Exception error) { ShowEditorError("Could not open the editor", error); }
    }

    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        _discardOnClose = false;
        if (!_loadingEditor && _editFile is not null) UpdateEditorStatus();
    }

    private void OnSave(object sender, RoutedEventArgs e) => SaveEdits();

    private bool SaveEdits()
    {
        if (_editFile is null) return true;
        try
        {
            _editFile.Save(Editor.Text);
            EditorErrorBanner.Visibility = Visibility.Collapsed;
            _copyText = Editor.Text;
            Editor.Document.UndoStack.MarkAsOriginalFile();
            UpdateEditorStatus(saved: true);
            return true;
        }
        catch (Exception error)
        {
            ShowEditorError("Could not save changes", error);
            return false;
        }
    }

    private void UpdateEditorStatus(bool saved = false)
    {
        var dirty = HasUnsavedChanges;
        var name = Path.GetFileName(_editPath);
        Title = name + (dirty ? " *" : "");
        TitleFile.Text = Title;
        TitleFile.ToolTip = dirty ? "Unsaved changes" : _editPath;
        SaveButton.IsEnabled = dirty;
        FootMode.Text = dirty ? "Editing · Unsaved changes" : saved ? "Editing · Saved" : "Editing · Ctrl+S to save";
        FootInfo.Text = $"{Editor.Document.LineCount} lines · {SyntaxHighlighter.LanguageName(_editPath)}";
        CommandManager.InvalidateRequerySuggested();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!ConfirmClose()) e.Cancel = true;
        base.OnClosing(e);
    }

    private bool ConfirmClose()
    {
        if (_discardOnClose || !HasUnsavedChanges) return true;
        if (_askingToClose) return false;
        _askingToClose = true;
        try
        {
            var dialog = new UnsavedCodeDialog(Path.GetFileName(_editPath) ?? "This file", this);
            dialog.ShowDialog();
            if (dialog.Choice == UnsavedCodeChoice.Save) return SaveEdits();
            return dialog.Choice == UnsavedCodeChoice.Discard;
        }
        finally { _askingToClose = false; }
    }

    /// <summary>Owned WPF windows are closed without their Closing event when their owner exits.</summary>
    internal static bool ConfirmCloseEditors(Window? owner = null)
    {
        var editors = Application.Current.Windows.OfType<CodeViewerWindow>()
            .Where(w => owner is null || w.Owner == owner).ToArray();
        // Do not authorize discarding until every editor agrees: Cancel in a later window keeps all open.
        foreach (var editor in editors)
            if (!editor.ConfirmClose()) return false;
        foreach (var editor in editors) editor._discardOnClose = true;
        // A later Closing handler may cancel the owner's close. In that case its editor is still open and
        // must prompt again on the next attempt. Normal owner/shutdown closure runs before this idle reset.
        Application.Current.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle,
            new Action(() => { foreach (var editor in editors) editor._discardOnClose = false; }));
        return true;
    }

    private void ShowEditorError(string title, Exception error)
    {
        FootMode.Text = title + ": " + error.Message;
        EditorErrorText.Text = title + ". " + error.Message;
        EditorErrorBanner.Visibility = Visibility.Visible;
        Editor.Focus();
    }

    private sealed class EditorColorizer(Func<string?> path) : DocumentColorizingTransformer
    {
        protected override void ColorizeLine(DocumentLine line)
        {
            int offset = line.Offset;
            foreach (var token in SyntaxHighlighter.HighlightLine(CurrentContext.Document.GetText(line), path()))
            {
                if (token.Text.Length > 0)
                    ChangeLinePart(offset, offset + token.Text.Length,
                        element => element.TextRunProperties.SetForegroundBrush(BrushFor(token.Kind)));
                offset += token.Text.Length;
            }
        }
    }
}
