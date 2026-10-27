namespace Historia.Views;

/// <summary>
/// Helpers for the code-built pieces of the App: menus, the dialog service, the plain-text pages
/// and custom controls. Views themselves are AXAML.
/// </summary>
internal static class ViewKit
{
    /// <summary>Bind a control property to a theme resource key (the {DynamicResource} of code).</summary>
    public static T Dyn<T>(this T c, AvaloniaProperty p, string key) where T : Control
    {
        c.Bind(p, new DynamicResourceExtension(key));
        return c;
    }

    public static TextBlock Text(string? text, string fgKey = "ColorText", double size = 13, FontWeight weight = FontWeight.Normal)
        => new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = weight,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        }.Dyn(TextBlock.ForegroundProperty, fgKey);

    /// <summary>A bitmap from the app's embedded <c>Assets/</c> (avares:// resources).</summary>
    public static Avalonia.Media.Imaging.Bitmap? Asset(string fileName)
    {
        try
        {
            return new Avalonia.Media.Imaging.Bitmap(
                Avalonia.Platform.AssetLoader.Open(new System.Uri($"avares://Historia/Assets/{fileName}")));
        }
        catch
        {
            return null;
        }
    }

    // Each emblem is decoded once and shared by every image that shows it (a bitmap is immutable
    // and not tied to a theme variant, so the cache is keyed by the variant the file belongs to).
    private static readonly Lazy<Avalonia.Media.Imaging.Bitmap?> LightMark = new(() => LoadMark("light"));
    private static readonly Lazy<Avalonia.Media.Imaging.Bitmap?> DarkMark = new(() => LoadMark("dark"));

    private static Avalonia.Media.Imaging.Bitmap? LoadMark(string variant)
        => Asset($"historia-mark-{variant}@2x.png") ?? Asset($"historia-mark-{variant}.png");

    /// <summary>
    /// Points <paramref name="img"/> at the Historia ship emblem for the current theme — the
    /// maroon <c>historia-mark-light</c> in Light mode, the copper <c>historia-mark-dark</c> in Dark
    /// and High Contrast — and keeps it in sync on <see cref="Historia.Services.Theming.IThemeService.ThemeChanged"/>.
    /// The subscription is tied to the control's visual-tree lifetime, so it leaks nothing.
    /// </summary>
    public static Image BindThemedMark(Image img, Historia.Services.Theming.IThemeService theme)
    {
        void Apply()
        {
            var light = theme.Effective == Historia.Models.EffectiveTheme.Light;
            img.Source = light ? LightMark.Value : DarkMark.Value;
        }

        EventHandler<Historia.Services.Theming.ThemeChangedEventArgs> onChanged =
            (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(Apply);

        img.AttachedToVisualTree += (_, _) =>
        {
            theme.ThemeChanged -= onChanged;   // idempotent: guard against a re-attach doubling up
            theme.ThemeChanged += onChanged;
            Apply();
        };
        img.DetachedFromVisualTree += (_, _) => theme.ThemeChanged -= onChanged;

        Apply();                               // the first paint; the attach event re-applies and subscribes
        return img;
    }

    /// <summary>Style class on every <see cref="HelpHint"/> badge — the handle
    /// the UI tests find it by.</summary>
    public const string HelpHintClass = "help-hint";

    /// <summary>An icon glyph in <c>currentColor</c>. Material Symbols render
    /// filled on a 960-unit grid, scaled to <paramref name="size"/>.</summary>
    public static Control Icon(string name, double size = 16, string fgKey = "ColorText")
        => IconGlyphPath(name, size, fgKey);

    /// <summary>Shared <see cref="Path"/> builder for <see cref="IconPaths"/> glyphs — renders
    /// Material Symbols monochrome fill geometry on a 960-unit grid with optical padding preserved.</summary>
    public static Path IconGlyphPath(string name, double size, string fgKey)
    {
        var data = IconPaths.Get(name);
        var geometry = Geometry.Parse(data);
        geometry.Transform = new MatrixTransform(
            Matrix.CreateTranslation(0, 960) * Matrix.CreateScale(size / 960.0, size / 960.0));
        var path = new Path
        {
            Data = geometry,
            Stretch = Stretch.None,
            Width = size,
            Height = size,
        };
        return path.Dyn(Path.FillProperty, fgKey);
    }

    /// <summary>A plain <see cref="MenuItem"/> with a click handler — the code equivalent of a
    /// <c>&lt;MenuItem Header=… Click=…/&gt;</c>.</summary>
    public static MenuItem MenuLeaf(string header, Action onClick)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => onClick();
        return mi;
    }

    /// <summary>As <see cref="MenuLeaf(string, Action)"/>, for an async click handler. The task
    /// is fire-and-forget — the handler is expected to route its own failures. <see cref="Task"/>
    /// is nullable so a <c>?.</c>-chained one-liner binds here without a warning.</summary>
    public static MenuItem MenuLeaf(string header, Func<Task?> onClick)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => _ = onClick();
        return mi;
    }
}
