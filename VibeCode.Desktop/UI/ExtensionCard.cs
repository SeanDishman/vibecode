using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VibeCode.UI;

/// <summary>
/// One extension, rendered the same way every time: a brand-tinted glyph tile, a title with a one-line
/// explanation, an optional state pill and the on/off switch on the right, and - only once the switch is on -
/// a body indented to the title's column, so every card on the page shares one left rail.
///
/// Before this each of the five cards in Settings &gt; Extensions hand-rolled its own DockPanel and they drifted:
/// three ran their body full-bleed while Phone indented it, the in-card rules were a hairline nobody could see,
/// and "connected" was a green dot on one card and a sentence on the next. This is <see cref="SettingCard"/>'s
/// argument applied to the one pane that never got it. The template lives in SettingsWindow.xaml; this type only
/// carries the content.
/// </summary>
public class ExtensionCard : ContentControl
{
    /// <summary>Segoe Fluent Icons codepoint for the tile.</summary>
    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(ExtensionCard), new PropertyMetadata(""));

    /// <summary>Face the glyph is drawn in. Defaults to the icon font; Games overrides it because the card
    /// describes a titlebar button that is itself an emoji, and the two must match.</summary>
    public static readonly DependencyProperty GlyphFontProperty =
        DependencyProperty.Register(nameof(GlyphFont), typeof(FontFamily), typeof(ExtensionCard), new PropertyMetadata(null));

    /// <summary>Brand colour of the service: the tile glyph and any accent inside the body.</summary>
    public static readonly DependencyProperty TintProperty =
        DependencyProperty.Register(nameof(Tint), typeof(Brush), typeof(ExtensionCard), new PropertyMetadata(null));

    /// <summary>The same colour at card-surface strength - the tile's fill.</summary>
    public static readonly DependencyProperty TintSoftProperty =
        DependencyProperty.Register(nameof(TintSoft), typeof(Brush), typeof(ExtensionCard), new PropertyMetadata(null));

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(ExtensionCard), new PropertyMetadata(""));

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(ExtensionCard), new PropertyMetadata(""));

    /// <summary>Small pill left of the switch. Reserved for a state the switch cannot already tell you -
    /// "Connected", "Needs a key" - so it never just repeats the switch back at the reader.</summary>
    public static readonly DependencyProperty StatusProperty =
        DependencyProperty.Register(nameof(Status), typeof(object), typeof(ExtensionCard), new PropertyMetadata(null));

    /// <summary>Everything below the header: shown only while <see cref="IsOn"/>, indented to the title.</summary>
    public static readonly DependencyProperty BodyProperty =
        DependencyProperty.Register(nameof(Body), typeof(object), typeof(ExtensionCard), new PropertyMetadata(null));

    /// <summary>The one line that says what turning this on would get you. Shown only while it is off, so the
    /// card is never a bare title strip.</summary>
    public static readonly DependencyProperty OffHintProperty =
        DependencyProperty.Register(nameof(OffHint), typeof(string), typeof(ExtensionCard), new PropertyMetadata(""));

    /// <summary>Bound to the service's own Enabled flag rather than to the switch, so the body follows the state
    /// that was actually persisted.</summary>
    public static readonly DependencyProperty IsOnProperty =
        DependencyProperty.Register(nameof(IsOn), typeof(bool), typeof(ExtensionCard), new PropertyMetadata(false));

    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public FontFamily? GlyphFont { get => (FontFamily?)GetValue(GlyphFontProperty); set => SetValue(GlyphFontProperty, value); }
    public Brush? Tint { get => (Brush?)GetValue(TintProperty); set => SetValue(TintProperty, value); }
    public Brush? TintSoft { get => (Brush?)GetValue(TintSoftProperty); set => SetValue(TintSoftProperty, value); }
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
    public object? Status { get => GetValue(StatusProperty); set => SetValue(StatusProperty, value); }
    public object? Body { get => GetValue(BodyProperty); set => SetValue(BodyProperty, value); }
    public string OffHint { get => (string)GetValue(OffHintProperty); set => SetValue(OffHintProperty, value); }
    public bool IsOn { get => (bool)GetValue(IsOnProperty); set => SetValue(IsOnProperty, value); }
}
