using System.IO;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    static string Output = Path.GetFullPath(Environment.GetEnvironmentVariable("VIBECODE_TEST_OUTPUT") ?? "artifacts/telemetry-map/tests");
    [STAThread]
    static int Main(string[] args)
    {
        Directory.CreateDirectory(Output);
        Environment.SetEnvironmentVariable("VIBECODE_DATA_DIR", Path.Combine(Output, "data"));
        Environment.SetEnvironmentVariable("VIBECODE_HIDDEN", "1");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppSettings.Current.AgentMemoryEnabled = false;
        AppSettings.Current.NotifyOnTurnEnd = AppSettings.Current.NotifyOnAwaitingInput = false;
        AppSettings.Current.BridgePeerMessaging = false;
        foreach (var key in new[] { "VIBECODE_CLAUDE_ACCOUNT_STORE", "CLAUDE_CONFIG_DIR", "VIBECODE_CODEX_ACCOUNT_STORE", "VIBECODE_CODEX_LEGACY_HOME" })
            Environment.SetEnvironmentVariable(key, Path.Combine(Output, "isolated", key));
        AppSettings.Current.TelemetryLiveAnimation = false;
        app.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(new Uri("/VibeCode;component/Themes/Dark.xaml", UriKind.Relative)));
        try
        {
            if (args.Contains("--baseline"))
            {
                var hud = new UsageHudWindow(); Capture(hud, 880, 620, "hud-before.png"); hud.Close();
                return 0;
            }
            VerifySnapshots();
            VerifyNavigation();
            VerifyWall();
            Console.WriteLine($"PASS: {Checks} workflow telemetry checks. Local fixtures only.");
            return 0;
        }
        catch(Exception e) { Console.Error.WriteLine(e); return 1; }
        finally { app.Shutdown(); }
    }
    static void Capture(Window window, int width, int height, string filename)
    {
        window.Width=width; window.Height=height;
        window.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        var root=(FrameworkElement)window.Content;
        root.Measure(new Size(width,height)); root.Arrange(new Rect(0,0,width,height)); root.UpdateLayout();
        var image=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);
        image.Render(root);
        var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream=File.Create(Path.Combine(Output,filename)); encoder.Save(stream);
        Console.WriteLine("Rendered " + filename);
    }
}
