using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;

namespace VibeCode.UI;

/// <summary>
/// The small "i" beside a title. Settings used to print every explanation as a paragraph under its row, which is
/// most of what made the window a wall of text; the explanation now lives here and shows on hover, on keyboard
/// focus, or on a click. The look lives in SettingsWindow.xaml; this type only owns the text and the tooltip.
/// </summary>
public class InfoTip : Control
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(InfoTip), new PropertyMetadata("", OnTextChanged));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }

    public InfoTip()
    {
        Focusable = true;
        // An explanation is read, not glanced at: show it at once and keep it up for as long as it is hovered
        // (the stock five seconds cuts a paragraph off mid-sentence). Disabled rows still explain themselves.
        ToolTipService.SetInitialShowDelay(this, 120);
        ToolTipService.SetBetweenShowDelay(this, 0);
        ToolTipService.SetShowDuration(this, 120_000);
        ToolTipService.SetShowOnDisabled(this, true);
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var tip = (InfoTip)d;
        var text = (e.NewValue as string ?? "").Trim();
        AutomationProperties.SetHelpText(tip, text);
        tip.ToolTip = text.Length == 0 ? null : new ToolTip
        {
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 330, LineHeight = 17.5 },
            Padding = new Thickness(12, 9, 12, 10),
            Placement = PlacementMode.Bottom,
            HorizontalOffset = -10,
            VerticalOffset = 4,
        };
    }

    private void Show(bool open)
    {
        if (ToolTip is not ToolTip tip || tip.IsOpen == open) return;
        if (open) tip.PlacementTarget = this;
        tip.IsOpen = open;
    }

    // A click is the impatient form of a hover, and it must not also count as a click on the row behind it.
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Show(true);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        e.Handled = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        Show(false);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is not (Key.Enter or Key.Space) || ToolTip is not ToolTip tip) return;
        Show(!tip.IsOpen);
        e.Handled = true;
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        Show(false);
    }

    // A bare Control has no automation peer, so without this the button - and the explanation it holds - would
    // not exist for a screen reader at all.
    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed class Peer(InfoTip owner) : FrameworkElementAutomationPeer(owner), IInvokeProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;
        protected override string GetClassNameCore() => nameof(InfoTip);
        protected override string GetHelpTextCore() => owner.Text;
        // One with nothing to say is collapsed on screen; keep it out of the screen reader's tree as well.
        protected override bool IsControlElementCore() => owner.Text.Length > 0;
        protected override bool IsContentElementCore() => owner.Text.Length > 0;

        public override object? GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Invoke ? this : base.GetPattern(patternInterface);

        public void Invoke()
        {
            if (!IsEnabled()) throw new ElementNotEnabledException();
            owner.Dispatcher.BeginInvoke(new Action(() => owner.Show(true)));
        }
    }
}

/// <summary>
/// One setting, rendered the same way everywhere: a tinted glyph tile, a short title with an <see cref="InfoTip"/>
/// carrying the explanation, and the control on the right - switch, dropdown, slider or button, always in the
/// same slot. Anything that genuinely needs the full width (a picker grid, an extension's setup form, a list of
/// servers) goes in <see cref="Footer"/> under the row. Before this, every pane hand-rolled its rows and they
/// drifted: checkboxes on one page, switches on the next, dropdowns under the text here and beside it there.
/// The template lives in SettingsWindow.xaml; this type only carries the content.
/// </summary>
public class SettingCard : ContentControl
{
    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(SettingCard), new PropertyMetadata(null));

    /// <summary>Face the glyph is drawn in. Defaults to the icon font; set it when the tile shows a plain character.</summary>
    public static readonly DependencyProperty GlyphFontProperty =
        DependencyProperty.Register(nameof(GlyphFont), typeof(FontFamily), typeof(SettingCard), new PropertyMetadata(null));

    /// <summary>Colour of the tile glyph. Defaults to the accent; an extension sets its service's brand colour.</summary>
    public static readonly DependencyProperty TintProperty =
        DependencyProperty.Register(nameof(Tint), typeof(Brush), typeof(SettingCard), new PropertyMetadata(null));

    /// <summary>The tile's fill - the same colour at card-surface strength.</summary>
    public static readonly DependencyProperty TintSoftProperty =
        DependencyProperty.Register(nameof(TintSoft), typeof(Brush), typeof(SettingCard), new PropertyMetadata(null));

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(SettingCard), new PropertyMetadata(""));

    /// <summary>The explanation behind the info button. Never printed in the row itself.</summary>
    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingCard),
            new PropertyMetadata("", (d, _) => ((SettingCard)d).DescribeControl()));

    /// <summary>Extra words the settings search should match that appear in neither the title nor the description.</summary>
    public static readonly DependencyProperty KeywordsProperty =
        DependencyProperty.Register(nameof(Keywords), typeof(string), typeof(SettingCard), new PropertyMetadata(""));

    /// <summary>Small state readout left of the control - a pill or a count. Reserved for something the control
    /// cannot already tell you ("Connected", "3 servers").</summary>
    public static readonly DependencyProperty StatusProperty =
        DependencyProperty.Register(nameof(Status), typeof(object), typeof(SettingCard),
            new PropertyMetadata(null, OnLogicalSlotChanged));

    /// <summary>Live readout right of the control (a slider's current number). Empty hides it.</summary>
    public static readonly DependencyProperty ValueTextProperty =
        DependencyProperty.Register(nameof(ValueText), typeof(string), typeof(SettingCard), new PropertyMetadata(""));

    /// <summary>Full-width content under the row, for the few things that cannot fit the control slot.</summary>
    public static readonly DependencyProperty FooterProperty =
        DependencyProperty.Register(nameof(Footer), typeof(object), typeof(SettingCard),
            new PropertyMetadata(null, OnLogicalSlotChanged));

    /// <summary>Adds the chevron and makes the row header a click target that opens and closes <see cref="Footer"/>.</summary>
    public static readonly DependencyProperty IsExpandableProperty =
        DependencyProperty.Register(nameof(IsExpandable), typeof(bool), typeof(SettingCard), new PropertyMetadata(false));

    /// <summary>Whether <see cref="Footer"/> is showing. A plain row leaves this true; a dropdown row starts it
    /// false; an extension binds it to the service's own Enabled flag so its body follows what was persisted.</summary>
    public static readonly DependencyProperty IsExpandedProperty =
        DependencyProperty.Register(nameof(IsExpanded), typeof(bool), typeof(SettingCard), new PropertyMetadata(true));

    /// <summary>Set on every card in a group except the first, so grouped rows get one hairline between them
    /// instead of each row drawing its own box.</summary>
    public static readonly DependencyProperty ShowDividerProperty =
        DependencyProperty.Register(nameof(ShowDivider), typeof(bool), typeof(SettingCard), new PropertyMetadata(true));

    public string? Glyph { get => (string?)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public FontFamily? GlyphFont { get => (FontFamily?)GetValue(GlyphFontProperty); set => SetValue(GlyphFontProperty, value); }
    public Brush? Tint { get => (Brush?)GetValue(TintProperty); set => SetValue(TintProperty, value); }
    public Brush? TintSoft { get => (Brush?)GetValue(TintSoftProperty); set => SetValue(TintSoftProperty, value); }
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
    public string Keywords { get => (string)GetValue(KeywordsProperty); set => SetValue(KeywordsProperty, value); }
    public object? Status { get => GetValue(StatusProperty); set => SetValue(StatusProperty, value); }
    public string ValueText { get => (string)GetValue(ValueTextProperty); set => SetValue(ValueTextProperty, value); }
    public object? Footer { get => GetValue(FooterProperty); set => SetValue(FooterProperty, value); }
    public bool IsExpandable { get => (bool)GetValue(IsExpandableProperty); set => SetValue(IsExpandableProperty, value); }
    public bool IsExpanded { get => (bool)GetValue(IsExpandedProperty); set => SetValue(IsExpandedProperty, value); }
    public bool ShowDivider { get => (bool)GetValue(ShowDividerProperty); set => SetValue(ShowDividerProperty, value); }

    private FrameworkElement? _header;
    private UIElement? _highlight;

    // Status and Footer are logical children, exactly as Content already is. Without that their content's only
    // parent is a ContentPresenter inside this control's TEMPLATE, so an ElementName binding written in the
    // window resolved against the template's namescope and silently found nothing.
    private static void OnLogicalSlotChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (SettingCard)d;
        if (e.OldValue is not null) card.RemoveLogicalChild(e.OldValue);
        if (e.NewValue is not null) card.AddLogicalChild(e.NewValue);
    }

    protected override IEnumerator LogicalChildren
    {
        get
        {
            var children = new ArrayList(3);
            if (Content is not null) children.Add(Content);
            if (Status is not null) children.Add(Status);
            if (Footer is not null) children.Add(Footer);
            return children.GetEnumerator();
        }
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (_header is not null) _header.MouseLeftButtonUp -= OnHeaderClick;
        _header = GetTemplateChild("PART_Header") as FrameworkElement;
        if (_header is not null) _header.MouseLeftButtonUp += OnHeaderClick;
        _highlight = GetTemplateChild("PART_Highlight") as UIElement;
    }

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);
        DescribeControl();
    }

    /// <summary>The explanation is behind a hover for sighted users, so hand the same sentence to assistive
    /// technology as the control's own help text rather than leaving it reachable only by pointer.</summary>
    private void DescribeControl()
    {
        if (Description.Length == 0) return;
        IEnumerable<Control> controls = Content switch
        {
            Panel panel => panel.Children.OfType<Control>(),
            Control one => [one],
            _ => [],
        };
        foreach (var control in controls)
            if (string.IsNullOrEmpty(AutomationProperties.GetHelpText(control)))
                AutomationProperties.SetHelpText(control, Description);
    }

    private void OnHeaderClick(object sender, MouseButtonEventArgs e)
    {
        if (!IsExpandable || e.Handled) return;
        // A click that landed on the row's own control (Add server, Restore all) belongs to that control.
        for (var node = e.OriginalSource as DependencyObject; node is not null && !ReferenceEquals(node, _header);
             node = node is Visual or Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if (node is ButtonBase or Selector or RangeBase or TextBoxBase or InfoTip) return;
        IsExpanded = !IsExpanded;
        e.Handled = true;
    }

    /// <summary>Puts keyboard focus on the setting's own control rather than on the row.</summary>
    public bool FocusPrimaryControl()
    {
        if (Content is UIElement control)
        {
            if (control.Focusable && control.IsEnabled && control.Focus()) return true;
            if (control.MoveFocus(new TraversalRequest(FocusNavigationDirection.First))) return true;
        }
        return MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    /// <summary>Briefly tints the row, so the eye lands on it after a search result or Jarvis opens the page.</summary>
    public void Flash()
    {
        if (_highlight is null) ApplyTemplate();
        SettingFlash.Run(_highlight);
    }
}

/// <summary>
/// A captioned run of <see cref="SettingCard"/>s sharing one card surface, divided by hairlines - the grouped-row
/// shape modern settings apps use, instead of a loose stack of separate boxes. Context the rows should not each
/// repeat goes in <see cref="Description"/>, behind the caption's info button.
///
/// The divider is suppressed on the first row automatically: <see cref="ItemsControl"/> stamps
/// <c>AlternationIndex</c> onto each container, and because the items already ARE UIElements they are their own
/// containers, so index 0 is genuinely the first card.
/// </summary>
public class SettingGroup : ItemsControl
{
    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.Register(nameof(Header), typeof(string), typeof(SettingGroup), new PropertyMetadata(""));

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingGroup), new PropertyMetadata(""));

    /// <summary>Extra search words for every row in the group.</summary>
    public static readonly DependencyProperty KeywordsProperty =
        DependencyProperty.Register(nameof(Keywords), typeof(string), typeof(SettingGroup), new PropertyMetadata(""));

    public string Header { get => (string)GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }
    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
    public string Keywords { get => (string)GetValue(KeywordsProperty); set => SetValue(KeywordsProperty, value); }

    private UIElement? _highlight;

    public SettingGroup()
    {
        AlternationCount = 200;
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _highlight = GetTemplateChild("PART_Highlight") as UIElement;
    }

    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        base.PrepareContainerForItemOverride(element, item);
        if (element is SettingCard card) card.ShowDivider = GetAlternationIndex(element) > 0;
    }

    /// <summary>Briefly outlines the group - where a whole section, not one row, is what was asked for.</summary>
    public void Flash()
    {
        if (_highlight is null) ApplyTemplate();
        SettingFlash.Run(_highlight);
    }

    // Announced as a named group ("Notifications"), so the sections that used to be whole categories are still
    // places a screen reader can jump between.
    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed class Peer(SettingGroup owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;
        protected override string GetClassNameCore() => nameof(SettingGroup);
        protected override string GetNameCore() => base.GetNameCore() is { Length: > 0 } name ? name : owner.Header;
        protected override string GetHelpTextCore() => owner.Description;
    }
}

/// <summary>A page's title, with the page's one-sentence purpose behind its info button.</summary>
public class SettingHeading : Control
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(SettingHeading), new PropertyMetadata(""));

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingHeading), new PropertyMetadata(""));

    /// <summary>Search words that should land on this page as a whole.</summary>
    public static readonly DependencyProperty KeywordsProperty =
        DependencyProperty.Register(nameof(Keywords), typeof(string), typeof(SettingHeading), new PropertyMetadata(""));

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
    public string Keywords { get => (string)GetValue(KeywordsProperty); set => SetValue(KeywordsProperty, value); }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed class Peer(SettingHeading owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;
        protected override string GetClassNameCore() => nameof(SettingHeading);
        protected override string GetNameCore() => owner.Title;
        protected override AutomationHeadingLevel GetHeadingLevelCore() => AutomationHeadingLevel.Level1;
    }
}

/// <summary>
/// A settings page: a vertical stack that fills the pane up to <see cref="MaxContentWidth"/> and stays against the
/// left edge beyond it. A plain StackPanel cannot do both - left-aligned it shrinks to its widest row, so two
/// pages came out different widths, and stretched with a MaxWidth it centres itself.
/// </summary>
public class SettingPage : StackPanel
{
    public static readonly DependencyProperty MaxContentWidthProperty =
        DependencyProperty.Register(nameof(MaxContentWidth), typeof(double), typeof(SettingPage),
            new FrameworkPropertyMetadata(660.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>True for everything on a page narrower than <see cref="CompactBelow"/> - the window near its
    /// minimum size. Inherited, so the shared dropdown and slider styles can give some width back to the title
    /// there instead of squeezing it onto two lines.</summary>
    public static readonly DependencyProperty IsCompactProperty =
        DependencyProperty.RegisterAttached("IsCompact", typeof(bool), typeof(SettingPage),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public const double CompactBelow = 540;

    public double MaxContentWidth { get => (double)GetValue(MaxContentWidthProperty); set => SetValue(MaxContentWidthProperty, value); }
    public static bool GetIsCompact(DependencyObject element) => (bool)element.GetValue(IsCompactProperty);
    public static void SetIsCompact(DependencyObject element, bool value) => element.SetValue(IsCompactProperty, value);

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        // Depends on the pane's width alone, never on the rows it resizes, so it cannot oscillate.
        SetIsCompact(this, Math.Min(sizeInfo.NewSize.Width, MaxContentWidth) < CompactBelow);
    }

    protected override Size MeasureOverride(Size constraint)
    {
        var width = Math.Min(constraint.Width, MaxContentWidth);
        var desired = base.MeasureOverride(new Size(width, constraint.Height));
        return new Size(double.IsInfinity(constraint.Width) ? desired.Width : width, desired.Height);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        base.ArrangeOverride(new Size(Math.Min(arrangeSize.Width, MaxContentWidth), arrangeSize.Height));
        return arrangeSize;
    }
}

/// <summary>Marks the rows of an open dropdown, so an item template can show something there (the notification
/// sound's play button) that would be dead chrome in the collapsed box. Inherited, so it reaches into the row's
/// template without a binding that has to go looking for an ancestor.</summary>
public static class SettingPicker
{
    public static readonly DependencyProperty IsDropDownRowProperty =
        DependencyProperty.RegisterAttached("IsDropDownRow", typeof(bool), typeof(SettingPicker),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetIsDropDownRow(DependencyObject element) => (bool)element.GetValue(IsDropDownRowProperty);
    public static void SetIsDropDownRow(DependencyObject element, bool value) => element.SetValue(IsDropDownRowProperty, value);
}

/// <summary>Group captions are authored in sentence case (so search results can quote them) and shown in capitals.</summary>
public sealed class UpperCaseConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => (value as string ?? "").ToUpper(c);
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

internal static class SettingFlash
{
    public static void Run(UIElement? highlight)
    {
        // A locator, not decoration - but it is still motion, so it follows the Windows animation preference.
        if (highlight is null || !SystemParameters.ClientAreaAnimation) return;
        var pulse = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(1.5) };
        pulse.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.1)));
        pulse.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.55)));
        pulse.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
        highlight.BeginAnimation(UIElement.OpacityProperty, pulse);
    }
}
