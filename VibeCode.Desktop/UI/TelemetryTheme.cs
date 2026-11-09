using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using VibeCode.Services;
using WpfAnimatedGif;

namespace VibeCode.UI;

/// <summary>The wallpaper the main window is showing right now, so a window that fits the app's theme wears the same
/// one - including the pick a random background made this session.</summary>
public static class BackgroundArt
{
    /// <summary>Path of the image behind the main window, or null for the built-in animated background.</summary>
    public static string? Target { get; private set; }
    public static event Action? Changed;

    internal static void Publish(string? target)
    {
        if (string.Equals(Target, target, StringComparison.OrdinalIgnoreCase)) return;
        Target = target;
        Changed?.Invoke();
    }
}

/// <summary>
/// Appearance › All telemetry windows › Fit theme. The telemetry windows - the workflow wall, the usage wall, the HUD and the

/// window's wallpaper behind it under the same readability scrim, and when the app is borderless its panels give up
/// their fills for the thin black outline the app's own panes use. CLI mode already reaches these windows through the
/// app's theme dictionaries, so a fitted window in CLI stays the flat terminal it already is.
///
/// It is window-level resource overrides plus one art layer behind the window's content, so it follows along live:
/// flip the switch, change the wallpaper or go borderless and an open window redraws without being reopened. The
/// theme's own control styles read these keys statically, so buttons, menus and pickers keep their fills.
/// </summary>
public static class TelemetryTheme
{
    private const string BuiltIn = "pack://application:,,,/VibeCode;component/Assets/background.gif";
    private static readonly Brush Clear = Frozen(new SolidColorBrush(Colors.Transparent));
    // The translucent black hairline Themes/Borderless.xaml gives every region of the main window.
    private static readonly Brush Outline = Frozen(new SolidColorBrush(Color.FromArgb(0xD9, 0, 0, 0)));
    // Only until the main window has computed its own scrim (it always has, once the app is up).
    private static readonly Brush FallbackScrim = Frozen(new SolidColorBrush(Color.FromArgb(0xC8, 0x1A, 0x1A, 0x1D)));
    // Weak, so the shared decode lives only while some fitted window still shows it.
    private static string? _decodedKey;
    private static WeakReference<BitmapImage>? _decoded;

    /// <summary>Whether the telemetry windows should wear the app's look right now.</summary>
    public static bool IsFitted => AppSettings.Current.TelemetryFitTheme && !AppSettings.IsCliMode;

    /// <summary>Keep <paramref name="window"/> fitted to the app's look - when the setting asks for it - until it closes.
    /// Call after InitializeComponent.</summary>
    public static void Attach(Window window)
    {
        var fitted = new Fitted(window);
        fitted.Apply();
        Action changed = fitted.ApplySoon;
        AppSettings.Changed += changed;
        BackgroundArt.Changed += changed;
        // Both are static events: unhook on close, or every telemetry window ever opened stays pinned in memory.
        window.Closed += (_, _) =>
        {
            AppSettings.Changed -= changed;
            BackgroundArt.Changed -= changed;
        };
    }

    private sealed class Fitted(Window window)
    {
        private Image? _art;
        private Rectangle? _scrim;
        private string? _artKey;
        private Style? _bareCard;
        private (bool Fit, bool Borderless, string? Art, bool Scrim)? _applied;
        private bool _queued;

        /// <summary>Settings saves can come from any thread, and several arrive together; apply once, on the UI thread.</summary>
        public void ApplySoon()
        {
            if (_queued) return;
            _queued = true;
            window.Dispatcher.BeginInvoke(() => { _queued = false; Apply(); });
        }

        public void Apply()
        {
            var fit = IsFitted;
            var borderless = fit && AppSettings.IsBorderless;
            // Settings are saved for all sorts of reasons (window placements, every toggle); most change nothing here.
            var state = (fit, borderless, fit ? BackgroundArt.Target : null, fit && Application.Current.TryFindResource("ScrimBridge") is not null);
            if (_applied == state) return;
            _applied = state;
            Override("Bg0", fit ? Clear : null);           // the window and its root frame: let the wallpaper through
            // The workflow wall's map stage - its canvas, as the chat column is the main window's - shows the art too.
            // The only other reader is the graph's hollow link ports, which simply stay hollow over the art.
            Override("CodeBg", fit ? Clear : null);
            Override("Bg1", borderless ? Clear : null);    // title bars, panels and cards: outlines only, when borderless
            Override("Border", borderless ? Outline : null);
            BareCards(borderless);
            PinScenes(borderless);
            if (fit) ShowArt();
            else HideArt();
        }

        private void Override(string key, Brush? brush)
        {
            if (brush is not null) window.Resources[key] = brush;
            // Only ever take back what this put there; the windows define none of these keys themselves.
            else if (window.Resources.Contains(key) && window.Resources[key] is Brush current
                     && (ReferenceEquals(current, Clear) || ReferenceEquals(current, Outline)))
                window.Resources.Remove(key);
        }

        /// <summary>The HUD's and usage wall's metric cards use the theme's Card style, whose fill is fixed inside the
        /// theme. Borderless, they get the same card with no fill and the black hairline, like the wall's own panels.</summary>
        private void BareCards(bool bare)
        {
            if (bare)
            {
                if (_bareCard is null && Application.Current.TryFindResource("Card") is Style card)
                {
                    _bareCard = new Style(card.TargetType, card);
                    _bareCard.Setters.Add(new Setter(Border.BackgroundProperty, Clear));
                    _bareCard.Setters.Add(new Setter(Border.BorderBrushProperty, Outline));
                    _bareCard.Seal();
                }
                if (_bareCard is not null) window.Resources["Card"] = _bareCard;
            }
            else if (_bareCard is not null && window.Resources.Contains("Card") && ReferenceEquals(window.Resources["Card"], _bareCard))
                window.Resources.Remove("Card");
        }

        /// <summary>The drawn wall scenes paint their hover cards from Bg1 and Border. Those cards float over pixel art,
        /// not over the wallpaper, so they keep the theme's own solid values whatever the window's chrome does.</summary>
        private void PinScenes(bool pin)
        {
            foreach (var scene in Logical<WorkflowScene>(window))
                foreach (var key in new[] { "Bg1", "Border" })
                {
                    if (pin && Application.Current.TryFindResource(key) is { } themed) scene.Resources[key] = themed;
                    else if (!pin) scene.Resources.Remove(key);
                }
        }

        private void ShowArt()
        {
            EnsureLayer();
            var target = BackgroundArt.Target;
            var key = target ?? BuiltIn;
            if (key != _artKey && Decode(target) is { } source)
            {
                if (target is null || key.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
                    ImageBehavior.SetAnimatedSource(_art!, source);
                else
                {
                    ImageBehavior.SetAnimatedSource(_art!, null);
                    _art!.Source = source;
                }
                _artKey = key;
            }
            // The main window keeps this gradient in step with the visibility slider and with borderless.
            if (Application.Current.TryFindResource("ScrimBridge") is not null) _scrim!.SetResourceReference(Shape.FillProperty, "ScrimBridge");
            else _scrim!.Fill = FallbackScrim;
            _art!.Visibility = _scrim!.Visibility = Visibility.Visible;
        }

        private void HideArt()
        {
            if (_art is null) return;
            _art.Visibility = _scrim!.Visibility = Visibility.Collapsed;
            // Let the wallpaper go while the window wears its own look again.
            ImageBehavior.SetAnimatedSource(_art, null);
            _art.Source = null;
            _artKey = null;
        }

        /// <summary>The wallpaper sits behind everything the window already draws: its content moves into a grid
        /// whose bottom layers are the art and the scrim. Named parts keep their names; nothing reads Content.</summary>
        private void EnsureLayer()
        {
            if (_art is not null) return;
            _art = new Image { Stretch = Stretch.UniformToFill, IsHitTestVisible = false };
            RenderOptions.SetBitmapScalingMode(_art, BitmapScalingMode.HighQuality);
            _scrim = new Rectangle { IsHitTestVisible = false };
            var content = window.Content;
            window.Content = null;
            var host = new Grid { ClipToBounds = true };
            host.Children.Add(_art);
            host.Children.Add(_scrim);
            if (content is UIElement element) host.Children.Add(element);
            window.Content = host;
        }
    }

    /// <summary>One decode shared by every fitted window: they all show the same wallpaper.</summary>
    private static BitmapImage? Decode(string? target)
    {
        var key = target ?? BuiltIn;
        if (key == _decodedKey && _decoded?.TryGetTarget(out var shared) == true) return shared;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(key);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            (_decodedKey, _decoded) = (key, new WeakReference<BitmapImage>(image));
            return image;
        }
        catch { return null; } // unreadable image - the window keeps whatever it showed before
    }

    private static IEnumerable<T> Logical<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (var deeper in Logical<T>(child)) yield return deeper;
        }
    }

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
