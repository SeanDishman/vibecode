using System.Windows;
using System.Windows.Controls;

namespace VibeCode.UI;

/// <summary>
/// One setting, rendered the same way everywhere: a tinted glyph tile, a title, a wrapped explanation, and a
/// control on the right. Before this, every row in Settings hand-rolled the same fifteen-line DockPanel, so
/// each one drifted a little - different paddings, different type sizes, icons that sometimes had a tile and
/// sometimes did not. The template lives in SettingsWindow.xaml; this type only carries the content.
/// </summary>
public class SettingCard : ContentControl
{
    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(SettingCard), new PropertyMetadata(null));

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(SettingCard), new PropertyMetadata(""));

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingCard), new PropertyMetadata(""));

    /// <summary>Full-width content under the description - sliders and anything else that needs the whole row
    /// rather than the narrow control slot on the right.</summary>
    public static readonly DependencyProperty FooterProperty =
        DependencyProperty.Register(nameof(Footer), typeof(object), typeof(SettingCard), new PropertyMetadata(null));

    /// <summary>Live readout beside the title (a slider's current number). Empty hides it.</summary>
    public static readonly DependencyProperty ValueTextProperty =
        DependencyProperty.Register(nameof(ValueText), typeof(string), typeof(SettingCard), new PropertyMetadata(""));

    /// <summary>Set on every card in a group except the first, so grouped rows get one hairline between them
    /// instead of each row drawing its own box.</summary>
    public static readonly DependencyProperty ShowDividerProperty =
        DependencyProperty.Register(nameof(ShowDivider), typeof(bool), typeof(SettingCard), new PropertyMetadata(true));

    public string? Glyph { get => (string?)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
    public object? Footer { get => GetValue(FooterProperty); set => SetValue(FooterProperty, value); }
    public string ValueText { get => (string)GetValue(ValueTextProperty); set => SetValue(ValueTextProperty, value); }
    public bool ShowDivider { get => (bool)GetValue(ShowDividerProperty); set => SetValue(ShowDividerProperty, value); }
}

/// <summary>
/// A titled run of <see cref="SettingCard"/>s sharing one card surface, divided by hairlines - the grouped-row
/// shape modern settings apps use, instead of a loose stack of separate boxes.
///
/// The divider is suppressed on the first row automatically: <see cref="ItemsControl"/> stamps
/// <c>AlternationIndex</c> onto each container, and because the items already ARE UIElements they are their own
/// containers, so index 0 is genuinely the first card.
/// </summary>
public class SettingGroup : ItemsControl
{
    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.Register(nameof(Header), typeof(string), typeof(SettingGroup), new PropertyMetadata(""));

    /// <summary>Optional sentence under the group header, for context the individual rows should not repeat.</summary>
    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingGroup), new PropertyMetadata(""));

    public string Header { get => (string)GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }
    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }

    public SettingGroup()
    {
        AlternationCount = 200;
    }

    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        base.PrepareContainerForItemOverride(element, item);
        if (element is SettingCard card) card.ShowDivider = GetAlternationIndex(element) > 0;
    }
}
