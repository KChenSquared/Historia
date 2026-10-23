namespace Historia.Controls;

/// <summary>
/// A monochrome icon glyph for AXAML: <c>&lt;ctl:IconGlyph Icon="close" Size="12" /&gt;</c>.
/// It draws through <see cref="ViewKit.IconGlyphPath"/>, so it looks exactly like a glyph built in
/// code. The path is rebuilt only when <see cref="Icon"/>, <see cref="Size"/>,
/// <see cref="ForegroundKey"/> or <see cref="FollowForeground"/> changes; a theme change needs no
/// rebuild because the path's fill is a dynamic resource, or a binding to the inherited
/// foreground, that re-resolves on its own.
/// </summary>
public sealed class IconGlyph : ContentControl
{
    public static readonly StyledProperty<string?> IconProperty =
        AvaloniaProperty.Register<IconGlyph, string?>(nameof(Icon));

    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<IconGlyph, double>(nameof(Size), 16d);

    public static readonly StyledProperty<string> ForegroundKeyProperty =
        AvaloniaProperty.Register<IconGlyph, string>(nameof(ForegroundKey), "ColorText");

    public static readonly StyledProperty<bool> FollowForegroundProperty =
        AvaloniaProperty.Register<IconGlyph, bool>(nameof(FollowForeground));

    static IconGlyph()
    {
        IconProperty.Changed.AddClassHandler<IconGlyph>((g, _) => g.UpdateGlyph());
        SizeProperty.Changed.AddClassHandler<IconGlyph>((g, _) => g.UpdateGlyph());
        ForegroundKeyProperty.Changed.AddClassHandler<IconGlyph>((g, _) => g.UpdateGlyph());
        FollowForegroundProperty.Changed.AddClassHandler<IconGlyph>((g, _) => g.UpdateGlyph());
    }

    /// <summary>Name of the glyph in <see cref="Historia.Components.IconPaths"/>; null or empty draws nothing.</summary>
    public string? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    /// <summary>Edge length in pixels of the square the glyph is drawn in.</summary>
    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    /// <summary>Palette resource key (for example <c>ColorTextMuted</c>) the glyph is filled with.</summary>
    public string ForegroundKey
    {
        get => GetValue(ForegroundKeyProperty);
        set => SetValue(ForegroundKeyProperty, value);
    }

    /// <summary>
    /// When true the glyph is filled with the control's own <see cref="TemplatedControl.Foreground"/>,
    /// which it inherits from its parent (a primary button's foreground has no palette key), and
    /// follows it as it changes; <see cref="ForegroundKey"/> is then ignored.
    /// </summary>
    public bool FollowForeground
    {
        get => GetValue(FollowForegroundProperty);
        set => SetValue(FollowForegroundProperty, value);
    }

    private void UpdateGlyph()
    {
        if (string.IsNullOrEmpty(Icon))
        {
            Content = null;
            return;
        }

        var path = IconGlyphPath(Icon, Size, ForegroundKey);
        if (FollowForeground)
            path.Bind(Path.FillProperty, this.GetObservable(ForegroundProperty));
        Content = path;
    }
}
