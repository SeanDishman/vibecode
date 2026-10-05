using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VibeCode;
using VibeCode.Services;
using VibeCode.UI;

internal static class Program
{
    private const BindingFlags StaticHidden = BindingFlags.Static | BindingFlags.NonPublic;
    private static readonly DependencyProperty Phase = (DependencyProperty)typeof(OrbitSpinner)
        .GetField("PhaseProperty", StaticHidden)!.GetValue(null)!;
    private static int _checks;
    internal static string OutputDirectory = "";

    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--check-saved", var directory, var expected])
        {
            Environment.SetEnvironmentVariable("VIBECODE_DATA_DIR", directory);
            return AppSettings.Current.ThinkingOrbStyle == expected ? 0 : 1;
        }
        OutputDirectory = Path.GetFullPath(Path.Combine("artifacts", "agent1-thinking-orbs", "verification"));
        var dataDirectory = Path.Combine(OutputDirectory, "data-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDirectory);
        Environment.SetEnvironmentVariable("VIBECODE_DATA_DIR", dataDirectory);
        Environment.SetEnvironmentVariable("VIBECODE_HIDDEN", "1");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        Theme("Dark");
        typeof(App).GetMethod("InitializeThinkingOrbs", StaticHidden)!.Invoke(null, null);
        try
        {
            if (args.Contains("--review-only"))
            {
                MotionReview.Run();
                Console.WriteLine($"PASS: {_checks} visual motion checks; review artifacts in {OutputDirectory}");
                return 0;
            }
            CatalogAndFallback();
            Rendering();
            PickerAndLifecycle();
            ReloadInFreshProcess(dataDirectory);
            if (args.Contains("--review")) MotionReview.Run();
            Console.WriteLine($"PASS: {_checks} thinking-orb checks ({ThinkingOrbStyles.All.Count} distinct loops, live selection, restart persistence, keyboard selection, search, and hidden/unloaded clocks)");
            Console.WriteLine($"Previews: {OutputDirectory}");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error is TargetInvocationException { InnerException: not null } wrapped ? wrapped.InnerException : error);
            return 1;
        }
        finally
        {
            foreach (Window window in app.Windows.Cast<Window>().ToArray()) window.Close();
            app.Shutdown();
        }
    }

    private static void CatalogAndFallback()
    {
        Check(ThinkingOrbStyles.All.Count == 6, "six selectable styles");
        Check(ThinkingOrbStyles.All.Select(style => style.Id).Distinct().Count() == 6, "stable unique IDs");
        Check(ThinkingOrbStyles.All[0].Id == ThinkingOrbStyles.DefaultId && ThinkingOrbStyles.DefaultId == "globe", "Globe is the default and listed first");
        Check(JsonSerializer.Deserialize<AppSettings>("{}")!.ThinkingOrbStyle == "globe", "settings without a choice get Globe");
        foreach (var value in new string?[] { null, "", "future-orb", "  MORPH  " })
        {
            var setting = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(new { ThinkingOrbStyle = value }))!;
            Check(setting.ThinkingOrbStyle == (value == "  MORPH  " ? "morph" : "globe"), "invalid preference fallback and normalization");
        }
        // The six retired styles fall back to the default; Atom and Gyroscope keep their IDs, so anyone who had
        // chosen them keeps their choice.
        foreach (var retired in new[] { "orbit", "comet", "particles", "pulse", "ripple", "wave" })
        {
            var setting = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(new { ThinkingOrbStyle = retired }))!;
            Check(setting.ThinkingOrbStyle == "globe", $"retired '{retired}' preference falls back to Globe");
        }
        Check(ThinkingOrbStyles.Resolve("atom").Style == ThinkingOrbStyle.Atom
            && ThinkingOrbStyles.Resolve("gyroscope").Style == ThinkingOrbStyle.Gyroscope, "kept IDs survive the redesign");
    }

    private static void Rendering()
    {
        foreach (var theme in new[] { "Dark", "Cli" })
        {
            Theme(theme);
            var ink = (Brush)Application.Current.Resources["Accent"];
            var fingerprints = new HashSet<string>();
            foreach (var option in ThinkingOrbStyles.All)
            {
                var signature = new List<byte>();
                foreach (var size in new[] { 22, 32 })
                {
                    foreach (var phase in new[] { 0.0, 0.137, 0.5, 0.83 })
                    {
                        var frame = Frame(option.Style, size, ink, phase);
                        var pixels = Pixels(frame);
                        Check(pixels.Where((_, index) => index % 4 == 3).Count(alpha => alpha > 15) > 15,
                            $"{option.Name} is visible at {size}px in {theme}");
                        var loop = Pixels(Frame(option.Style, size, ink, phase + 1));
                        Check(pixels.Zip(loop, (a, b) => Math.Abs(a - b)).Max() <= 1,
                            $"{option.Name} loop closes at {size}px in {theme}");
                        signature.AddRange(pixels);
                    }
                }
                Check(fingerprints.Add(Convert.ToHexString(SHA256.HashData(signature.ToArray()))),
                    $"{option.Name} has its own animation in {theme}");
            }
            OrbPreview.RenderStyleSheet(theme);
        }
        Theme("Dark");
    }

    private static void PickerAndLifecycle()
    {
        var settings = AppSettings.Current;
        settings.AgentMemoryEnabled = false;
        settings.SpotifyEnabled = false;
        settings.WeatherEnabled = false;
        settings.PhoneEnabled = false;
        settings.GroqSpeechEnabled = false;
        settings.NotifyOnTurnEnd = false;
        settings.UiMode = "background";
        Check(settings.TrySave() is null, "initial isolated settings save succeeds");
        var existing = new OrbitSpinner { Ink = (Brush)Application.Current.Resources["Accent"], Width = 22, Height = 22 };
        var explicitPreview = new OrbitSpinner { StyleKind = ThinkingOrbStyle.Atom, Width = 32, Height = 32 };
        var host = new StackPanel { Children = { existing, explicitPreview } };
        var liveWindow = new Window { Content = host, Left = 6100, Top = 300, ShowActivated = false, Width = 100, Height = 100 };
        liveWindow.Show();
        var window = new SettingsWindow { ShowActivated = false };
        window.Show();
        Drain();
        var picker = (ListBox)window.FindName("ThinkingOrbPicker");
        var rail = (ListBox)window.FindName("Rail");
        Check(picker.Items.Count == ThinkingOrbStyles.All.Count && ((ThinkingOrbOption)picker.SelectedItem).Id == "globe", "Appearance initializes its saved selection");
        rail.SelectedIndex = 1;
        Drain();
        var previews = Descendants<OrbitSpinner>(picker).ToArray();
        Check(previews.Length == ThinkingOrbStyles.All.Count && previews.All(orb => orb.HasAnimatedProperties), "every visible preview animates");
        Check(previews.Select(orb => orb.EffectiveStyle).SequenceEqual(ThinkingOrbStyles.All.Select(option => option.Style)),
            "each real DataTemplate preview uses its own style");
        foreach (var option in ThinkingOrbStyles.All)
        {
            picker.SelectedItem = option;
            Drain();
            Check(settings.ThinkingOrbStyle == option.Id && existing.EffectiveStyle == option.Style,
                $"{option.Name} selection updates an already loaded chat orb");
            Check(new OrbitSpinner().EffectiveStyle == option.Style, $"{option.Name} selection applies to new/Bridge orbs");
            Check(explicitPreview.StyleKind == ThinkingOrbStyle.Atom, "picker previews keep their own styles");
            Check(previews.Select(orb => orb.EffectiveStyle).SequenceEqual(ThinkingOrbStyles.All.Select(item => item.Style)),
                "changing the preference preserves every template preview");
            var saved = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path.Combine(AppSettings.Dir, "settings.json")))!;
            Check(saved.ThinkingOrbStyle == option.Id, $"{option.Name} selection is saved to disk");
        }
        var selected = (ListBoxItem)picker.ItemContainerGenerator.ContainerFromItem(ThinkingOrbStyles.All[^1]);
        selected.Focus();
        Drain();
        Check(selected.IsKeyboardFocusWithin && selected.IsSelected, "keyboard focus and selected option are exposed");
        var search = (TextBox)window.FindName("SettingsSearchBox");
        foreach (var term in new[] { "thinking orb", "spinner", "gyroscope" })
        {
            search.Text = term;
            Drain();
            var results = (ListBox)window.FindName("SettingsSearchResults");
            Check(results.Items.Cast<object>().Any(result => (string?)result.GetType().GetProperty("Title")?.GetValue(result) == "Thinking orbs"),
                $"Appearance orb picker is found by '{term}'");
        }
        search.Text = "";
        rail.SelectedIndex = 1;
        Drain();
        OrbPreview.RenderAppearance(window, "Dark", 900);
        OrbPreview.RenderAppearance(window, "Dark", 740);
        Theme("Cli");
        OrbPreview.RenderAppearance(window, "Cli", 740);
        Theme("Dark");

        // The running preview must stop on category changes, hiding, and virtualized removal.
        rail.SelectedIndex = 0;
        Drain();
        Check(previews.All(orb => !orb.HasAnimatedProperties), "inactive Appearance previews stop their clocks");
        rail.SelectedIndex = 1;
        Drain();
        Check(previews.All(orb => orb.HasAnimatedProperties), "returning to Appearance restarts preview clocks");
        existing.Visibility = Visibility.Collapsed;
        Drain();
        Check(!existing.HasAnimatedProperties, "hidden chat orb stops animating");
        existing.Visibility = Visibility.Visible;
        Drain();
        Check(existing.HasAnimatedProperties, "shown chat orb resumes animating");
        host.Children.Remove(existing);
        Drain();
        Check(!existing.HasAnimatedProperties, "unloaded/recycled chat orb releases its animation");

        // Settings saves can arrive off the UI thread; preference updates must marshal safely.
        Task.Run(() => { settings.ThinkingOrbStyle = "nebula"; Check(settings.TrySave() is null, "worker-thread preference save succeeds"); })
            .GetAwaiter().GetResult();
        Drain();
        Check(new OrbitSpinner().EffectiveStyle == ThinkingOrbStyle.Nebula, "worker-thread saves refresh the UI resource");
        window.Close();
        liveWindow.Close();
        var reopened = new SettingsWindow { ShowActivated = false };
        reopened.Show();
        Drain();
        Check(((ThinkingOrbOption)((ListBox)reopened.FindName("ThinkingOrbPicker")).SelectedItem).Id == "nebula", "reopened Appearance retains the selection");
        reopened.Close();
    }

    private static void ReloadInFreshProcess(string dataDirectory)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("--check-saved");
        start.ArgumentList.Add(dataDirectory);
        start.ArgumentList.Add("nebula");
        using var child = Process.Start(start)!;
        if (!child.WaitForExit(30000)) throw new InvalidOperationException("Preference reload timed out.");
        Check(child.ExitCode == 0, "a fresh process restores the saved preference");
    }

    internal static void Theme(string name)
    {
        var resources = Application.Current.Resources;
        resources.MergedDictionaries.Clear();
        resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/VibeCode;component/Themes/{name}.xaml"),
        });
    }

    internal static RenderTargetBitmap Frame(ThinkingOrbStyle style, int size, Brush ink, double phase)
    {
        var orb = new OrbitSpinner { StyleKind = style, Width = size, Height = size, Ink = ink, Spin = false };
        orb.SetValue(Phase, phase);
        orb.Measure(new Size(size, size));
        orb.Arrange(new Rect(0, 0, size, size));
        orb.UpdateLayout();
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(orb);
        return bitmap;
    }

    internal static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static byte[] Pixels(BitmapSource bitmap)
    {
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }

    internal static void Drain() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    internal static void Check(bool condition, string label)
    {
        _checks++;
        if (!condition) throw new InvalidOperationException(label);
    }
}
