namespace HistoriaApp.Tests;

/// <summary>
/// The AXAML markup kit: <see cref="IconGlyph"/>, <see cref="HelpHint"/>, the converters under
/// <c>Views/Converters</c> and the style classes in <c>ViewStyles.axaml</c>. Colours must come from
/// the palette through dynamic resources, so a theme swap repaints without rebuilding anything.
/// </summary>
[Trait("tier", "ui")]
public sealed class MarkupKitTests
{
    private sealed class StubThemeService : IThemeService
    {
        public EffectiveTheme Effective { get; set; } = EffectiveTheme.Dark;
        public ThemeMode CurrentMode => ThemeMode.Dark;
        public bool HighContrastEnabled => false;
        public string EffectiveCssThemeName => Effective == EffectiveTheme.Dark ? "dark" : "light";
        public IReadOnlyDictionary<string, Rgb> Palette => HistoriaPalette.For(Effective);
        public IReadOnlyList<Rgb> LaneColors => [];

#pragma warning disable CS0067
        public event EventHandler<ThemeChangedEventArgs>? ThemeChanged;
#pragma warning restore CS0067

        public Task ApplyModeAsync(ThemeMode mode, CancellationToken ct = default) => Task.CompletedTask;
        public bool CustomThemeActive => false;
        public Task SetCustomThemeEnabledAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;
        public Task SelectCustomThemeAsync(string themeId, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetHighContrastAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static Window Host(Control content)
    {
        var window = new Window { Width = 400, Height = 300, Content = content };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static Avalonia.Media.Color PaletteColor(EffectiveTheme theme, string token) => HistoriaPalette.Get(theme, token).ToAvalonia();

    private static Avalonia.Media.Color SolidColor(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    [AvaloniaFact]
    public void IconGlyph_renders_a_path_whose_fill_follows_a_theme_swap_without_rebuilding()
    {
        var theme = new StubThemeService { Effective = EffectiveTheme.Dark };
        using var applier = new AvaloniaThemeApplier(theme);
        applier.Apply(EffectiveTheme.Dark);

        var glyph = new IconGlyph { Icon = "close", Size = 12 };
        var window = Host(glyph);
        try
        {
            var path = Assert.IsType<Avalonia.Controls.Shapes.Path>(glyph.Content);
            Assert.Equal(12, path.Width);
            Assert.Equal(12, path.Height);
            Assert.Equal(PaletteColor(EffectiveTheme.Dark, "text"), SolidColor(path.Fill));

            theme.Effective = EffectiveTheme.Light;
            applier.Apply(EffectiveTheme.Light);
            Dispatcher.UIThread.RunJobs();

            Assert.Same(path, glyph.Content);
            Assert.Equal(PaletteColor(EffectiveTheme.Light, "text"), SolidColor(path.Fill));
            Assert.NotEqual(PaletteColor(EffectiveTheme.Dark, "text"), PaletteColor(EffectiveTheme.Light, "text"));
        }
        finally
        {
            window.Close();
            theme.Effective = EffectiveTheme.Dark;
            applier.Apply(EffectiveTheme.Dark);
        }
    }

    [AvaloniaFact]
    public void IconGlyph_rebuilds_its_path_when_icon_size_or_key_changes()
    {
        var theme = new StubThemeService { Effective = EffectiveTheme.Dark };
        using var applier = new AvaloniaThemeApplier(theme);
        applier.Apply(EffectiveTheme.Dark);

        var glyph = new IconGlyph { Icon = "close", Size = 12 };
        var window = Host(glyph);
        try
        {
            var first = Assert.IsType<Avalonia.Controls.Shapes.Path>(glyph.Content);

            glyph.Size = 20;
            var resized = Assert.IsType<Avalonia.Controls.Shapes.Path>(glyph.Content);
            Assert.NotSame(first, resized);
            Assert.Equal(20, resized.Width);

            glyph.ForegroundKey = "ColorTextMuted";
            Dispatcher.UIThread.RunJobs();
            var recoloured = Assert.IsType<Avalonia.Controls.Shapes.Path>(glyph.Content);
            Assert.NotSame(resized, recoloured);
            Assert.Equal(PaletteColor(EffectiveTheme.Dark, "text-muted"), SolidColor(recoloured.Fill));

            glyph.Icon = null;
            Assert.Null(glyph.Content);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void IconGlyph_draws_the_same_geometry_as_the_code_helper()
    {
        var glyph = new IconGlyph { Icon = "close", Size = 14, ForegroundKey = "ColorTextMuted" };
        var built = ViewKit.IconGlyphPath("close", 14, "ColorTextMuted");

        var path = Assert.IsType<Avalonia.Controls.Shapes.Path>(glyph.Content);
        Assert.Equal(built.Data!.Bounds, path.Data!.Bounds);
        Assert.Equal(built.Width, path.Width);
        Assert.Equal(built.Height, path.Height);
        Assert.Equal(built.Stretch, path.Stretch);
    }

    [AvaloniaFact]
    public void IconGlyph_with_FollowForeground_takes_its_parents_foreground_and_follows_it()
    {
        var first = new SolidColorBrush(Colors.White);
        var second = new SolidColorBrush(Colors.Orange);
        var glyph = new IconGlyph { Icon = "graph_8", Size = 16, ForegroundKey = "ColorTextMuted", FollowForeground = true };
        var parent = new Button { Foreground = first, Content = glyph };
        var window = Host(parent);
        try
        {
            var path = Assert.IsType<Avalonia.Controls.Shapes.Path>(glyph.Content);
            Assert.Same(first, path.Fill);

            parent.Foreground = second;
            Dispatcher.UIThread.RunJobs();

            Assert.Same(path, glyph.Content);
            Assert.Same(second, path.Fill);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void IconGlyph_without_FollowForeground_fills_from_its_key_whatever_the_parent_foreground()
    {
        var glyph = new IconGlyph { Icon = "graph_8", Size = 16, ForegroundKey = "ColorTextMuted" };
        var parent = new Button { Foreground = new SolidColorBrush(Colors.White), Content = glyph };
        var window = Host(parent);
        try
        {
            var path = Assert.IsType<Avalonia.Controls.Shapes.Path>(glyph.Content);
            Assert.Equal(PaletteColor(EffectiveTheme.Dark, "text-muted"), SolidColor(path.Fill));

            glyph.FollowForeground = true;
            Dispatcher.UIThread.RunJobs();
            Assert.Same(parent.Foreground, Assert.IsType<Avalonia.Controls.Shapes.Path>(glyph.Content).Fill);

            glyph.FollowForeground = false;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(PaletteColor(EffectiveTheme.Dark, "text-muted"),
                SolidColor(Assert.IsType<Avalonia.Controls.Shapes.Path>(glyph.Content).Fill));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void HelpHint_shows_its_text_as_a_tooltip_and_automation_help()
    {
        const string text = "First paragraph.\n\nSecond paragraph.";
        var hint = new HelpHint { Text = text };
        var window = Host(hint);
        try
        {
            Assert.Contains(ViewKit.HelpHintClass, hint.Classes);
            var tip = Assert.IsType<TextBlock>(ToolTip.GetTip(hint));
            Assert.Equal(text, tip.Text);
            Assert.Equal(TextWrapping.Wrap, tip.TextWrapping);
            Assert.Equal(text, Avalonia.Automation.AutomationProperties.GetHelpText(hint));
            Assert.Equal("Help", Avalonia.Automation.AutomationProperties.GetName(hint));
            Assert.Equal(15, hint.Width);
            Assert.Equal(15, hint.Height);
            Assert.Equal("?", Assert.IsType<TextBlock>(hint.Content).Text);

            hint.Text = "Changed.";
            Assert.Equal("Changed.", Assert.IsType<TextBlock>(ToolTip.GetTip(hint)).Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void HelpHint_border_follows_a_theme_swap()
    {
        var theme = new StubThemeService { Effective = EffectiveTheme.Dark };
        using var applier = new AvaloniaThemeApplier(theme);
        applier.Apply(EffectiveTheme.Dark);

        var hint = new HelpHint { Text = "Hint" };
        var window = Host(hint);
        try
        {
            Assert.Equal(PaletteColor(EffectiveTheme.Dark, "border"), SolidColor(hint.BorderBrush));

            theme.Effective = EffectiveTheme.Light;
            applier.Apply(EffectiveTheme.Light);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(PaletteColor(EffectiveTheme.Light, "border"), SolidColor(hint.BorderBrush));
        }
        finally
        {
            window.Close();
            theme.Effective = EffectiveTheme.Dark;
            applier.Apply(EffectiveTheme.Dark);
        }
    }

    [AvaloniaTheory]
    [InlineData("text", "text", 13, FontWeight.Normal)]
    [InlineData("muted", "text-muted", 12, FontWeight.Normal)]
    [InlineData("faint", "text-faint", 12, FontWeight.Normal)]
    [InlineData("section", "text-muted", 13, FontWeight.SemiBold)]
    [InlineData("pane-title", "text", 24, FontWeight.SemiBold)]
    public void Text_style_classes_resolve_their_palette_key(string cssClass, string token, double size, FontWeight weight)
    {
        var theme = new StubThemeService { Effective = EffectiveTheme.Dark };
        using var applier = new AvaloniaThemeApplier(theme);
        applier.Apply(EffectiveTheme.Dark);

        var block = new TextBlock { Text = "Sample" };
        block.Classes.Add(cssClass);
        var window = Host(block);
        try
        {
            Assert.Equal(PaletteColor(EffectiveTheme.Dark, token), SolidColor(block.Foreground));
            Assert.Equal(weight, block.FontWeight);
            if (cssClass is "text" or "section" or "pane-title")
            {
                Assert.Equal(size, block.FontSize);
                Assert.Equal(TextTrimming.CharacterEllipsis, block.TextTrimming);
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Muted_class_overrides_the_text_colour_when_combined()
    {
        var theme = new StubThemeService { Effective = EffectiveTheme.Dark };
        using var applier = new AvaloniaThemeApplier(theme);
        applier.Apply(EffectiveTheme.Dark);

        var block = new TextBlock { Text = "Sample" };
        block.Classes.Add("text");
        block.Classes.Add("muted");
        var window = Host(block);
        try
        {
            Assert.Equal(13, block.FontSize);
            Assert.Equal(PaletteColor(EffectiveTheme.Dark, "text-muted"), SolidColor(block.Foreground));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Card_class_resolves_surface_and_border_and_follows_a_theme_swap()
    {
        var theme = new StubThemeService { Effective = EffectiveTheme.Dark };
        using var applier = new AvaloniaThemeApplier(theme);
        applier.Apply(EffectiveTheme.Dark);

        var card = new Border { Child = new TextBlock { Text = "Card" } };
        card.Classes.Add("card");
        var window = Host(card);
        try
        {
            Assert.Equal(PaletteColor(EffectiveTheme.Dark, "surface"), SolidColor(card.Background));
            Assert.Equal(PaletteColor(EffectiveTheme.Dark, "border"), SolidColor(card.BorderBrush));
            Assert.Equal(new Thickness(1), card.BorderThickness);
            Assert.Equal(new CornerRadius(6), card.CornerRadius);
            Assert.Equal(new Thickness(12), card.Padding);

            theme.Effective = EffectiveTheme.Light;
            applier.Apply(EffectiveTheme.Light);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(PaletteColor(EffectiveTheme.Light, "surface"), SolidColor(card.Background));
            Assert.NotEqual(PaletteColor(EffectiveTheme.Dark, "surface"), PaletteColor(EffectiveTheme.Light, "surface"));
        }
        finally
        {
            window.Close();
            theme.Effective = EffectiveTheme.Dark;
            applier.Apply(EffectiveTheme.Dark);
        }
    }

    [Fact]
    public void InvertedBoolConverter_negates_both_ways_and_ignores_other_values()
    {
        var c = InvertedBoolConverter.Instance;
        var culture = System.Globalization.CultureInfo.InvariantCulture;

        Assert.Equal(false, c.Convert(true, typeof(bool), null, culture));
        Assert.Equal(true, c.Convert(false, typeof(bool), null, culture));
        Assert.Equal(true, c.ConvertBack(false, typeof(bool), null, culture));
        Assert.Same(Avalonia.Data.BindingOperations.DoNothing, c.Convert("x", typeof(bool), null, culture));
        Assert.Same(Avalonia.Data.BindingOperations.DoNothing, c.Convert(null, typeof(bool), null, culture));
    }

    [Fact]
    public void FileChangeKind_converters_cover_every_kind()
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        var expected = new Dictionary<FileChangeKind, (string Glyph, string Label)>
        {
            [FileChangeKind.Added] = ("A", "New"),
            [FileChangeKind.Modified] = ("M", "Modified"),
            [FileChangeKind.Deleted] = ("D", "Deleted"),
            [FileChangeKind.Renamed] = ("R", "Renamed"),
            [FileChangeKind.Copied] = ("C", "Copied"),
            [FileChangeKind.TypeChanged] = ("T", "Type-changed"),
            [FileChangeKind.Untracked] = ("U", "New"),
            [FileChangeKind.Ignored] = ("I", "Changed"),
            [FileChangeKind.Conflicted] = ("!", "Conflict"),
        };

        Assert.Equal(Enum.GetValues<FileChangeKind>().Length, expected.Count);
        foreach (var (kind, want) in expected)
        {
            Assert.Equal(want.Glyph, FileChangeKindGlyphConverter.Instance.Convert(kind, typeof(string), null, culture));
            Assert.Equal(want.Label, FileChangeKindLabelConverter.Instance.Convert(kind, typeof(string), null, culture));
        }

        Assert.Equal("?", FileChangeKindGlyphConverter.Instance.Convert(null, typeof(string), null, culture));
        Assert.Equal("?", FileChangeKindGlyphConverter.Glyph((FileChangeKind)999));
        Assert.Equal("Changed", FileChangeKindLabelConverter.Label((FileChangeKind)999));
    }
}
