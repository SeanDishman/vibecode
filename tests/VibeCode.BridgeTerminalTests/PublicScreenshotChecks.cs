using System.Windows;
using System.Windows.Controls;
using System.Text.Json.Nodes;
using VibeCode;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    // Real public UI, fictional content, isolated stores, and no provider process.
    // VIBECODE_TEST_OUTPUT selects the capture directory.
    private static void CapturePublicReadme()
    {
        var resources = Application.Current.Resources;
        resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(new Uri("/VibeCode;component/Themes/Dark.xaml", UriKind.Relative)));
        resources["BoolVis"] = new BooleanToVisibilityConverter();
        resources["ShowIf"] = new NonEmptyToVisibilityConverter();
        AppSettings.Current.TerminalEnabled = true;
        AppSettings.Current.WorkflowWallView = "graph";
        var vm = new MainViewModel();
        ChatViewModel Demo(string title, string provider = "codex", string status = "idle")
        {
            var chat = new ChatViewModel(@"C:\Projects\Orbit", provider: provider)
            { Title = title, Status = status };
            typeof(ChatViewModel).GetField("_session", Flags)!.SetValue(chat, new FakeSession());
            typeof(ChatViewModel).GetField("_model", Flags)!.SetValue(chat, provider == "claude" ? "claude-sonnet-5-5" : "gpt-6.1-sol");
            typeof(ChatViewModel).GetField("_lastAgentActivityAt", Flags)!.SetValue(chat, DateTimeOffset.UtcNow);
            Chats.Add(chat);
            vm.Chats.Add(chat);
            return chat;
        }
        ToolItem Step(string name, string input, string status = "done") => new()
        { Id = Guid.NewGuid().ToString("N"), Name = name, Input = JsonNode.Parse(input), Status = status, Elapsed = 0.8 };
        var chat = Demo("Build project search");
        chat.Items.Clear();
        chat.Items.Add(new UserItem { Owner = chat, Text = "Add instant search to the project library. Keep it keyboard accessible and cover the empty and loading states." });
        chat.Items.Add(new TextItem { Text = "I'll connect the search field to the project list, add keyboard navigation, and check the loading and empty states." });
        chat.Items.Add(Step("Read", """{"file_path":"C:\\Projects\\Orbit\\src\\ProjectLibrary.tsx"}"""));
        chat.Items.Add(Step("Edit", """{"file_path":"C:\\Projects\\Orbit\\src\\ProjectSearch.tsx","old_string":"const query = search;","new_string":"const query = search.trim().toLocaleLowerCase();"}"""));
        chat.Items.Add(Step("Bash", """{"command":"npm test -- project-search","description":"Run project search tests"}"""));
        chat.Items.Add(new TextItem { Text = "Project search is ready.\n\n- Results update as you type, with case-insensitive matching.\n- Arrow keys move through results; Enter opens a project.\n- Loading, empty, and no-match states have clear labels.\n\nAll 12 search tests passed, including keyboard navigation and clearing the query." });
        foreach (var task in new[] { "Connect search to the project library", "Add keyboard navigation and accessible labels", "Verify loading, empty, and no-match states" })
            chat.Todos.Add(new TodoEntry { Text = task, Status = "completed" });
        var review = Demo("Review accessibility", "claude", "running");
        review.Items.Add(Step("Read", """{"file_path":"C:\\Projects\\Orbit\\src\\ProjectSearch.tsx"}""", "running"));
        var tests = Demo("Check responsive layouts", "codex", "running");
        tests.Items.Add(Step("Bash", """{"command":"npm test -- responsive","description":"Check mobile and desktop layouts"}""", "running"));
        var docs = Demo("Update component guide", "claude");
        docs.Items.Add(Step("Edit", """{"file_path":"C:\\Projects\\Orbit\\docs\\components.md"}"""));
        vm.ActiveChat = chat;
        var shell = (MainWindow)typeof(MainWindow).GetConstructors(Flags).Single(c => c.GetParameters().Length == 3).Invoke([vm, null, true]);
        Place(shell);
        shell.Show(); Settle();
        CaptureShell(shell, 1440, 820, "workspace.png");
        var settings = new SettingsWindow();
        Place(settings);
        settings.Show(); settings.SelectCategory("Developer"); Settle();
        CaptureShell(settings, 1060, 560, "terminal-settings.png");
        settings.Close();
        var wall = new WorkflowTelemetryWindow(vm);
        Place(wall);
        wall.Show(); wall.Refresh(); Settle();
        CaptureShell(wall, 1440, 820, "workflow.png");
        wall.Close();
        shell.Close();
        Console.WriteLine("Captured public README screenshots using fictional demo data and offline sessions.");

        static void Place(Window window)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = 6100; window.Top = 300;
            window.ShowActivated = window.ShowInTaskbar = false;
            // Rendering Content alone omits the native window's background behind transparent panels.
            if (window.Content is Panel { Background: null } panel) panel.Background = window.Background;
        }
    }
}
