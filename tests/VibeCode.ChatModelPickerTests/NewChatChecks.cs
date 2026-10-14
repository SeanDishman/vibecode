using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VibeCode;
using VibeCode.Protocol;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    private static void VerifyFolderNewChats()
    {
        var resources = Application.Current.Resources;
        resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(new Uri("/VibeCode;component/Themes/Dark.xaml", UriKind.Relative)));
        resources["BoolVis"] = new BooleanToVisibilityConverter();
        resources["ShowIf"] = new NonEmptyToVisibilityConverter();
        foreach (var (id, label) in new[] { ("personal-picker", "Personal"), ("work-picker", "Work"), ("expired-picker", "Expired") })
        {
            var directory = Path.Combine(Environment.GetEnvironmentVariable("VIBECODE_CODEX_ACCOUNT_STORE")!, id);
            Directory.CreateDirectory(Path.Combine(directory, "home"));
            File.WriteAllText(Path.Combine(directory, "account.json"), JsonSerializer.Serialize(new { Id = id, Name = label, Plan = "plus", HomeFolder = "home" }));
            if (id != "expired-picker") File.WriteAllText(Path.Combine(directory, "home", "auth.json"),
                JsonSerializer.Serialize(new { tokens = new { account_id = id } }));
        }
        var keys = ApiKeyAccountService.Instance;
        var zai = keys.Add("glm", "fixture-zai-key", "Z.ai", GlmPreset.ZaiCodingPlan);
        var baseten = keys.Add("glm", "fixture-baseten-key", "Baseten", GlmPreset.Baseten);
        var vm = new MainViewModel();
        var original = new ChatViewModel(Root, accountId: "personal-picker", provider: "codex");
        original.Models.Add(new ModelChoice { Provider = "codex", Value = "personal-model", Display = "Personal model" });
        var work = new ChatViewModel(Root, accountId: "work-picker", provider: "codex");
        work.Models.Add(new ModelChoice { Provider = "codex", Value = "work-model", Display = "Work model" });
        Chats.AddRange([original, work]);
        vm.Chats.Add(original); vm.Chats.Add(work); vm.ActiveChat = original;
        var settings = AppSettings.Current;
        settings.DefaultProvider = "codex";
        var defaults = (settings.DefaultProvider, settings.DefaultCodexModel, settings.DefaultModel,
            settings.DefaultGlmModel, CodexAccountService.Instance.ActiveId);
        var shell = (MainWindow)typeof(MainWindow).GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(c => c.GetParameters().Length == 3).Invoke([vm, null, true]);
        shell.Left = 6100; shell.Top = 300; shell.ShowActivated = shell.ShowInTaskbar = false;
        shell.Show();
        try
        {
            using var picker = new PickerLifetime(Create());
            picker.Dialog.Show(); picker.Dialog.UpdateLayout();
            var provider = Control<ComboBox>(picker.Dialog, "NewChatProvider");
            var account = Control<ComboBox>(picker.Dialog, "NewChatAccount");
            var model = Control<ComboBox>(picker.Dialog, "NewChatModel");
            var create = Control<Button>(picker.Dialog, "CreateNewChat");
            Check("opening the folder picker creates no chat and keeps the current chat", vm.Chats.Count == 2 && vm.ActiveChat == original);
            Check("folder picker offers every supported provider", provider.Items.Cast<BridgeSetupPanel.ProviderChoice>()
                .Select(p => p.Id).SequenceEqual(new[] { "claude", "codex", "glm" }));
            Check("folder picker shows both Codex accounts and an expired account", account.Items.Count == 3);
            account.SelectedItem = account.Items.Cast<BridgeAccountChoice>().Single(a => a.AccountId == "personal-picker");
            Check("personal account uses its own live model catalog", model.Items.Cast<ModelChoice>().Single().Value == "personal-model");
            account.SelectedItem = account.Items.Cast<BridgeAccountChoice>().Single(a => a.AccountId == "work-picker");
            Check("changing account updates the model catalog without switching existing chats", model.Items.Cast<ModelChoice>().Single().Value == "work-model"
                && original.AccountId == "personal-picker" && work.AccountId == "work-picker" && vm.ActiveChat == original);
            account.SelectedItem = account.Items.Cast<BridgeAccountChoice>().Single(a => a.AccountId == "expired-picker");
            Check("an expired account cannot create a chat", !create.IsEnabled && !model.IsEnabled);
            provider.SelectedValue = "gemini";
            Check("Gemini uses only Gemini models and allows sign-in after creation", create.IsEnabled
                && model.Items.Cast<ModelChoice>().All(m => m.Provider == "gemini"));
            provider.SelectedValue = "glm";
            account.SelectedItem = account.Items.Cast<BridgeAccountChoice>().Single(a => a.AccountId == zai.Id);
            Check("Z.ai account offers Z.ai models", model.Items.Cast<ModelChoice>().Select(m => m.Value).SequenceEqual(GlmPreset.ModelsFor(zai.GlmBackend).Select(m => m.Value)));
            account.SelectedItem = account.Items.Cast<BridgeAccountChoice>().Single(a => a.AccountId == baseten.Id);
            Check("Baseten account offers Baseten models independently of Z.ai", model.Items.Cast<ModelChoice>().Select(m => m.Value).SequenceEqual(GlmPreset.ModelsFor(baseten.GlmBackend).Select(m => m.Value)));
            provider.SelectedValue = "codex";
            account.SelectedItem = account.Items.Cast<BridgeAccountChoice>().Single(a => a.AccountId == "work-picker");
            Capture(picker.Dialog, "new-chat-dark.png");
            picker.Dialog.Close();

            var trigger = new Button { DataContext = vm.ChatGroups.Groups!.Cast<CollectionViewGroup>().First().Name };
            foreach (var cancelMethod in new[] { "button", "close", "escape" })
            {
                DuringPrompt(dialog =>
                {
                    Check(cancelMethod + " opens a picker before any new chat is created", vm.Chats.Count == 2);
                    if (cancelMethod == "button") Control<Button>(dialog, "CancelNewChat").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    else if (cancelMethod == "close") Control<Button>(dialog, "DialogClose").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    else dialog.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(dialog)!, 0, Key.Escape)
                        { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                }, () => Invoke(shell, "OnNewChatInProject", trigger, new RoutedEventArgs()));
                Check(cancelMethod + " cancellation keeps existing chats and creates none", vm.Chats.Count == 2 && vm.ActiveChat == original);
            }
            ((TextBox)shell.FindName("CwdBox")).Text = Root;
            DuringPrompt(dialog =>
            {
                Check("starting from the home folder box also asks before creating a chat", vm.Chats.Count == 2
                    && Control<ComboBox>(dialog, "NewChatAccount").HasItems && Control<ComboBox>(dialog, "NewChatModel").HasItems);
                Control<Button>(dialog, "CancelNewChat").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }, () => Invoke(shell, "StartFromCwdBox"));
            Check("cancelling home-screen creation keeps the current chat", vm.Chats.Count == 2 && vm.ActiveChat == original);
            settings.DefaultProvider = "claude";
            DuringPrompt(dialog =>
            {
                Check("switching providers offers the same account and model picker", (string)Control<ComboBox>(dialog, "NewChatProvider").SelectedValue == "claude");
                Control<Button>(dialog, "CancelNewChat").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }, () => Invoke(shell, "MaybeOfferProviderChat", "claude"));
            Check("cancelling a provider offer leaves the open chat on its original provider", vm.Chats.Count == 2 && original.Provider == "codex");
            settings.DefaultProvider = "codex";
            var cliOverride = Environment.GetEnvironmentVariable("VIBECODE_CODEX_PATH");
            try
            {
                Environment.SetEnvironmentVariable("VIBECODE_CODEX_PATH", Path.ChangeExtension(Assembly.GetExecutingAssembly().Location, ".exe"));
                DuringPrompt(dialog =>
                {
                    var accounts = Control<ComboBox>(dialog, "NewChatAccount");
                    accounts.SelectedItem = accounts.Items.Cast<BridgeAccountChoice>().Single(a => a.AccountId == "work-picker");
                    Control<Button>(dialog, "CreateNewChat").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }, () => Invoke(shell, "OnNewChatInProject", trigger, new RoutedEventArgs()));
                var created = vm.ActiveChat!;
                Chats.Add(created);
                Check("confirming creates exactly one chat in the requested folder", vm.Chats.Count == 3 && created.Cwd == Root);
                Check("the new chat receives the chosen provider, account and model before launch",
                    created.Provider == "codex" && created.AccountId == "work-picker" && created.Model == "work-model");
                Check("confirming the picker preserves global provider, account and model defaults", defaults ==
                    (settings.DefaultProvider, settings.DefaultCodexModel, settings.DefaultModel,
                        settings.DefaultGlmModel, CodexAccountService.Instance.ActiveId));
                vm.CloseChat(created);
            }
            finally { Environment.SetEnvironmentVariable("VIBECODE_CODEX_PATH", cliOverride); }
            keys.Remove(zai.Id); keys.Remove(baseten.Id);
            foreach (var theme in new[] { "Cli", "Borderless" })
            {
                resources.MergedDictionaries.Clear();
                resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(new Uri($"/VibeCode;component/Themes/{(theme == "Cli" ? "Cli" : "Dark")}.xaml", UriKind.Relative)));
                if (theme == "Borderless") resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(new Uri("/VibeCode;component/Themes/Borderless.xaml", UriKind.Relative)));
                using var themedPicker = new PickerLifetime(Create());
                themedPicker.Dialog.Show(); themedPicker.Dialog.UpdateLayout();
                Control<ComboBox>(themedPicker.Dialog, "NewChatProvider").SelectedValue = "glm";
                Check(theme + " explains a missing GLM account and prevents creation", !Control<Button>(themedPicker.Dialog, "CreateNewChat").IsEnabled);
                Control<ComboBox>(themedPicker.Dialog, "NewChatProvider").SelectedValue = "codex";
                Check(theme + " picker has named keyboard-accessible fields", AutomationProperties.GetName(Control<ComboBox>(themedPicker.Dialog, "NewChatAccount")) == "New chat account"
                    && KeyboardNavigation.GetTabNavigation(themedPicker.Dialog) == KeyboardNavigationMode.Cycle);
                Capture(themedPicker.Dialog, "new-chat-" + theme.ToLowerInvariant() + ".png");
            }
        }
        finally { shell.Close(); }

        Window Create() => (Window)Invoke(shell, "CreateNewChatDialog", Root)!;
    }

    private static object? Invoke(object target, string name, params object?[] args) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);

    private static T Control<T>(DependencyObject root, string name) where T : FrameworkElement =>
        Visuals(root).OfType<T>().Single(e => e.Name == name);

    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Visuals(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static void DuringPrompt(Action<Window> action, Action open)
    {
        Exception? failure = null;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var dialog = Application.Current.Windows.Cast<Window>().Single(w => w.Title == "New chat");
            try { action(dialog); }
            catch (Exception error) { failure = error; dialog.Close(); }
        };
        timer.Start();
        try { open(); }
        finally { timer.Stop(); }
        if (failure is not null) throw failure;
    }

    private static void Capture(Window dialog, string name)
    {
        dialog.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(dialog.ActualWidth), (int)Math.Ceiling(dialog.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(dialog);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(Root, name)); encoder.Save(stream);
    }

    private sealed class PickerLifetime(Window dialog) : IDisposable
    {
        public Window Dialog => dialog;
        public void Dispose() => dialog.Close();
    }
}
