using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using VibeCode.Protocol;
using VibeCode.Services;
using VibeCode.UI;

namespace VibeCode;

public partial class MainWindow
{
    private void PromptNewChatForWindow(string cwd)
    {
        if (!NoDirectoryWorkspace.DirectoryExists(cwd)) return;
        var dialog = CreateNewChatDialog(cwd);
        if (dialog.ShowDialog() != true || dialog.Tag is not NewChatSelection selection) return;
        NewChatForWindow(cwd, provider: selection.Provider, accountId: selection.AccountId,
            configure: selection.Apply);
    }

    private Window CreateNewChatDialog(string cwd)
    {
        var accounts = BridgeAccountChoice.Load();
        var providerBox = new ComboBox
        {
            Name = "NewChatProvider", ItemsSource = BridgeSetupPanel.Providers,
            DisplayMemberPath = "Label", SelectedValuePath = "Id",
        };
        var accountBox = new ComboBox
        {
            Name = "NewChatAccount", DisplayMemberPath = nameof(BridgeAccountChoice.AccessibleLabel),
        };
        var accountStyle = new Style(typeof(ComboBoxItem), (Style)FindResource(typeof(ComboBoxItem)));
        accountStyle.Setters.Add(new Setter(IsEnabledProperty, new Binding(nameof(BridgeAccountChoice.IsUsable))));
        accountBox.ItemContainerStyle = accountStyle;
        var modelBox = new ComboBox { Name = "NewChatModel", DisplayMemberPath = nameof(ModelChoice.Display) };
        var hint = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Muted");

        var form = new StackPanel();
        var folder = new TextBlock
        {
            Text = NoDirectoryWorkspace.IsNoDirectory(cwd) ? NoDirectoryWorkspace.DisplayName : cwd,
            ToolTip = cwd, FontSize = 12.5, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 18),
        };
        folder.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        form.Children.Add(folder);
        AddField("AI provider", providerBox);
        AddField("Account", accountBox);
        AddField("Model", modelBox);
        form.Children.Add(hint);

        var cancel = new Button
        {
            Name = "CancelNewChat", Content = "Cancel", IsCancel = true,
            MinWidth = 86, MinHeight = 36, Padding = new Thickness(16, 8, 16, 8),
        };
        cancel.SetResourceReference(StyleProperty, "GhostButton");
        var create = new Button
        {
            Name = "CreateNewChat", Content = "Create chat", IsDefault = true,
            MinWidth = 112, MinHeight = 36, Padding = new Thickness(16, 8, 16, 8),
            Margin = new Thickness(8, 0, 0, 0),
        };
        create.SetResourceReference(StyleProperty, "PrimaryButton");
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0),
        };
        actions.Children.Add(cancel);
        actions.Children.Add(create);
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new ScrollViewer
        {
            Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false,
            MaxHeight = Math.Max(120, SystemParameters.WorkArea.Height - 180),
        });
        panel.Children.Add(actions);
        var dialog = CreateThemedDialog("New chat", panel, Math.Min(460, SystemParameters.WorkArea.Width - 48));
        dialog.SetResourceReference(FontFamilyProperty, "Ui");
        KeyboardNavigation.SetTabNavigation(dialog, KeyboardNavigationMode.Cycle);
        if (Environment.GetEnvironmentVariable("VIBECODE_HIDDEN") == "1")
        {
            dialog.WindowStartupLocation = WindowStartupLocation.Manual;
            dialog.Left = 6100; dialog.Top = 300;
        }
        var outline = new FrameworkElementFactory(typeof(Border));
        outline.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        outline.SetValue(MarginProperty, new Thickness(-3));
        outline.SetResourceReference(Border.BorderBrushProperty, "Accent");
        outline.SetResourceReference(Border.CornerRadiusProperty, "Rad8");
        var focus = new Style(typeof(Control));
        focus.Setters.Add(new Setter(TemplateProperty, new ControlTemplate(typeof(Control)) { VisualTree = outline }));
        foreach (var control in new Control[] { providerBox, accountBox, modelBox, cancel, create })
            control.FocusVisualStyle = focus;
        AutomationProperties.SetName(cancel, "Cancel new chat");
        AutomationProperties.SetName(create, "Create chat with selected account and model");
        cancel.Click += (_, _) => dialog.DialogResult = false;
        create.Click += (_, _) =>
        {
            if (accountBox.SelectedItem is not BridgeAccountChoice { IsUsable: true } account
                || modelBox.SelectedItem is not ModelChoice model || model.Provider != account.Provider) return;
            dialog.Tag = new NewChatSelection(account.Provider, account.AccountId, model.Value);
            dialog.DialogResult = true;
        };
        providerBox.SelectionChanged += (_, _) => RefreshAccounts();
        accountBox.SelectionChanged += (_, _) => RefreshModels();
        modelBox.SelectionChanged += (_, _) => RefreshHint();
        providerBox.SelectedValue = BridgeSetupPanel.Providers.Any(p => p.Id == AppSettings.Current.DefaultProvider)
            ? AppSettings.Current.DefaultProvider : "claude";
        dialog.Loaded += (_, _) => providerBox.Focus();
        return dialog;

        void AddField(string label, ComboBox box)
        {
            form.Children.Add(new Label
            {
                Content = label, Target = box, FontSize = 12.5, Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 0, 6),
            });
            box.MinHeight = 36;
            box.Margin = new Thickness(0, 0, 0, 14);
            AutomationProperties.SetName(box, "New chat " + label.ToLowerInvariant());
            form.Children.Add(box);
        }

        void RefreshAccounts()
        {
            if (providerBox.SelectedValue is not string provider) return;
            var choices = accounts.Where(a => a.Provider == provider).ToArray();
            if (choices.Length == 0)
                choices = [new(provider, "", provider == "glm" ? "No GLM account added" : "Default CLI account",
                    provider == "glm" ? "Add an API account in AI accounts" : "Sign in after creating the chat",
                    IsUsable: provider != "glm", IsDefault: true)];
            accountBox.ItemsSource = choices;
            accountBox.SelectedItem = choices.FirstOrDefault(a => a.IsDefault && a.IsUsable)
                ?? choices.FirstOrDefault(a => a.IsUsable) ?? choices[0];
        }

        void RefreshModels()
        {
            if (accountBox.SelectedItem is not BridgeAccountChoice account)
            {
                modelBox.ItemsSource = null;
                RefreshHint();
                return;
            }
            var previous = modelBox.SelectedItem as ModelChoice;
            accountBox.ToolTip = account.AccessibleLabel;
            modelBox.IsEnabled = account.IsUsable;
            var live = _vm.Chats.FirstOrDefault(chat => chat.Provider == account.Provider
                && chat.AccountId == account.AccountId && chat.Models.Count > 0);
            string? backend = null;
            IReadOnlyList<ModelChoice> models;
            if (account.Provider == "glm")
            {
                backend = ApiKeyAccountService.Instance.For("glm").FirstOrDefault(a => a.Id == account.AccountId)?.GlmBackend;
                models = GlmPreset.ModelsFor(backend).Select(m => new ModelChoice
                {
                    Provider = "glm", Value = m.Value, Display = m.Display, Description = m.Description,
                }).ToArray();
            }
            else models = live is null ? BridgeAgentConfigurationPolicy.ModelsFor(account.Provider)
                : live.Models.Where(m => m.Provider == account.Provider).ToArray();
            var preferred = previous?.Provider == account.Provider ? previous.Value
                : account.Provider == "glm" ? GlmPreset.NormalizeModel(AppSettings.Current.DefaultGlmModel, backend)
                : BridgeAgentConfigurationPolicy.Defaults(account.Provider).Model;
            modelBox.ItemsSource = models;
            modelBox.SelectedItem = models.FirstOrDefault(m => m.Value == preferred || m.ResolvedModel == preferred)
                ?? models.FirstOrDefault(m => m.Value == "default") ?? models.FirstOrDefault();
            RefreshHint();
        }

        void RefreshHint()
        {
            var account = accountBox.SelectedItem as BridgeAccountChoice;
            var model = modelBox.SelectedItem as ModelChoice;
            create.IsEnabled = account is { IsUsable: true } && model?.Provider == account.Provider;
            hint.Text = account is not { IsUsable: true }
                ? account?.Provider == "glm" ? "Add or reconnect a GLM account in AI accounts, then create your chat."
                    : "Sign in again in AI accounts, then choose an account for this chat."
                : account.AccountId.Length == 0
                    ? "You can install the CLI or sign in after creating this chat."
                    : model?.Description ?? "Your choices apply to this new chat.";
        }
    }
}
