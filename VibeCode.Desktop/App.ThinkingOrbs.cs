using System.Windows;
using VibeCode.Services;

namespace VibeCode;

public partial class App
{
    private static void InitializeThinkingOrbs()
    {
        RefreshThinkingOrbStyle();
        AppSettings.Changed += RefreshThinkingOrbStyle;
    }

    internal static void RefreshThinkingOrbStyle()
    {
        if (Current is not { } app || app.Dispatcher.HasShutdownStarted) return;
        // Token and account services also save settings on worker threads.
        if (!app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.BeginInvoke(new Action(RefreshThinkingOrbStyle));
            return;
        }
        var style = ThinkingOrbStyles.Resolve(AppSettings.Current.ThinkingOrbStyle).Style;
        if (app.Resources[ThinkingOrbStyles.ResourceKey] is ThinkingOrbStyle current && current == style) return;
        app.Resources[ThinkingOrbStyles.ResourceKey] = style;
    }
}
