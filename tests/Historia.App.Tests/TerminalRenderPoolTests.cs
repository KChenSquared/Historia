namespace HistoriaApp.Tests;

/// <summary>
/// The terminal control's own render pools and glyph-run cache, seen through the miss counters it
/// exposes. The Core <c>TerminalRenderAllocationTests</c> only mirrors the control and cannot see
/// them.
/// </summary>
[Trait("tier", "ui")]
public class TerminalRenderPoolTests
{
    internal sealed class EmittingPty : IPtyConnection
    {
        public event Action<ReadOnlyMemory<byte>>? OutputReceived;
#pragma warning disable CS0067
        public event Action<int>? Exited;
#pragma warning restore CS0067

        public void Emit(string text) => OutputReceived?.Invoke(System.Text.Encoding.UTF8.GetBytes(text));
        public void Write(ReadOnlySpan<byte> data) { }
        public void Resize(int columns, int rows) { }
        public void Kill() { }
        public void Dispose() { }
    }

    internal sealed class StubTheme : IThemeService
    {
        public EffectiveTheme Effective => EffectiveTheme.Dark;
        public ThemeMode CurrentMode => ThemeMode.Dark;
        public bool HighContrastEnabled => false;
        public string EffectiveCssThemeName => "dark";
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

    internal static IServiceProvider Services(IUiDispatcher? dispatcher = null)
    {
        var sc = new ServiceCollection();
        sc.AddSingleton<IThemeService, StubTheme>();
        sc.AddSingleton(dispatcher ?? new Historia.Platform.UiDispatcher());
        sc.AddSingleton<ISettingsService>(new ConflictResolverViewTests.FakeSettingsService());
        sc.AddSingleton<IExternalLauncher, ExternalLauncher>();
        return sc.BuildServiceProvider();
    }

    private static (Window Window, TerminalControl Control, EmittingPty Pty, TerminalSession Session) Show(
        double width = 640, double height = 384)
    {
        var services = Services();
        var control = new TerminalControl();
        control.Attach(services);

        var pty = new EmittingPty();
        var session = new TerminalSession(
            TerminalOrigin.Home("/home/me"), pty, services.GetRequiredService<IUiDispatcher>(), 80, 24, 1000);
        control.SetSession(session);

        var window = new Window { Content = control, Width = width, Height = height };
        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return (window, control, pty, session);
    }

    private static void RenderFrame(Control control)
    {
        var size = new PixelSize(Math.Max(1, (int)control.Bounds.Width), Math.Max(1, (int)control.Bounds.Height));
        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(size);
        using var dc = bitmap.CreateDrawingContext();
        control.Render(dc);
    }

    private const string Content = "plain text\r\n\x1b[31mred text\x1b[0m and \x1b[4munderlined\x1b[0m\r\nthird row";

    [AvaloniaFact]
    public void Second_frame_of_an_unfocused_terminal_records_no_pen_misses()
    {
        var (window, control, pty, session) = Show();
        try
        {
            Assert.False(control.IsFocused);
            // Undecorated text, so the unfocused cursor outline is the only pen in the frame.
            pty.Emit("plain text\r\nsecond row");

            RenderFrame(control);
            Assert.Equal(1, control.PenMissesForTesting);

            control.ResetRenderCountersForTesting();
            RenderFrame(control);

            Assert.Equal(0, control.PenMissesForTesting);
            Assert.Equal(0, control.RunMissesForTesting);
        }
        finally
        {
            window.Close();
            control.Teardown();
            session.Dispose();
        }
    }

    [AvaloniaFact]
    public void Glyph_runs_are_reused_until_the_row_text_changes()
    {
        var (window, control, pty, session) = Show();
        try
        {
            pty.Emit(Content);
            RenderFrame(control);
            var cached = control.CachedRunCountForTesting;

            control.ResetRenderCountersForTesting();
            RenderFrame(control);
            Assert.Equal(0, control.RunMissesForTesting);
            Assert.Equal(cached, control.CachedRunCountForTesting);

            Assert.True(cached > 0, "the headless platform resolves a glyph typeface, so runs are cached");

            pty.Emit("\r\nanother row of text");
            RenderFrame(control);
            Assert.True(control.RunMissesForTesting > 0, "changed text must be shaped again");
            Assert.True(control.CachedRunCountForTesting >= cached);
        }
        finally
        {
            window.Close();
            control.Teardown();
            session.Dispose();
        }
    }

    // The headless font resolves a glyph for every code point, so the missing-glyph path is reached by
    // dropping the control's glyph typefaces: every run then takes the font-fallback layout.
    private static string FallbackText(TerminalControl control)
    {
        control.DropGlyphTypefacesForTesting();
        return char.ConvertFromUtf32(0x4E2D);
    }

    [AvaloniaFact]
    public void A_run_with_a_glyph_missing_from_the_font_is_cached_and_not_reshaped_per_frame()
    {
        var (window, control, pty, session) = Show();
        try
        {
            pty.Emit("ab" + FallbackText(control) + "cd");
            RenderFrame(control);
            Assert.True(control.RunMissesForTesting > 0);
            Assert.True(control.CachedRunCountForTesting > 0);

            control.ResetRenderCountersForTesting();
            RenderFrame(control);
            RenderFrame(control);

            Assert.Equal(0, control.RunMissesForTesting);
        }
        finally
        {
            window.Close();
            control.Teardown();
            session.Dispose();
        }
    }

    [AvaloniaFact]
    public void A_fallback_run_is_shaped_again_when_its_colour_changes()
    {
        var (window, control, pty, session) = Show();
        try
        {
            var missing = FallbackText(control);
            pty.Emit("\x1b[31m" + missing + "\x1b[0m");
            RenderFrame(control);

            control.ResetRenderCountersForTesting();
            pty.Emit("\r\x1b[32m" + missing + "\x1b[0m");
            RenderFrame(control);

            Assert.Equal(1, control.RunMissesForTesting);
        }
        finally
        {
            window.Close();
            control.Teardown();
            session.Dispose();
        }
    }

    [AvaloniaFact]
    public void Runs_of_rows_no_longer_drawn_are_dropped_after_the_next_frame()
    {
        var (window, control, pty, session) = Show();
        try
        {
            pty.Emit(Content);
            RenderFrame(control);
            Assert.True(control.CachedRunCountForTesting >= 3);

            pty.Emit("\x1b[2J\x1b[Hone");
            RenderFrame(control);

            Assert.Equal(1, control.CachedRunCountForTesting);
        }
        finally
        {
            window.Close();
            control.Teardown();
            session.Dispose();
        }
    }

    [AvaloniaFact]
    public void A_frame_with_more_runs_than_the_old_cap_is_still_served_from_the_cache()
    {
        var (window, control, pty, session) = Show(2400, 1200);
        try
        {
            // One colour change per cell makes every cell its own run.
            var text = new System.Text.StringBuilder();
            for (var i = 0; i < 12000; i++)
                text.Append("\x1b[").Append(i % 2 == 0 ? "31" : "32").Append('m').Append('x');
            pty.Emit(text.ToString());

            RenderFrame(control);
            Assert.True(control.CachedRunCountForTesting > 4096,
                $"the frame draws more runs than the old cap, got {control.CachedRunCountForTesting}");

            control.ResetRenderCountersForTesting();
            RenderFrame(control);

            Assert.Equal(0, control.RunMissesForTesting);
        }
        finally
        {
            window.Close();
            control.Teardown();
            session.Dispose();
        }
    }

    [AvaloniaFact]
    public void A_bold_run_is_shaped_with_the_bold_face_and_regular_text_is_not_reused_for_it()
    {
        var (window, control, pty, session) = Show();
        try
        {
            var regular = control.GlyphFaceForTesting(0);
            var bold = control.GlyphFaceForTesting(1);
            Assert.NotNull(regular);
            Assert.NotNull(bold);
            // The face is the family's real bold, or the regular stands in; never a synthetic one.
            Assert.Equal(FontSimulations.None, bold.FontSimulations);

            pty.Emit("hello");
            RenderFrame(control);

            control.ResetRenderCountersForTesting();
            pty.Emit("\r\x1b[1mhello\x1b[0m");
            RenderFrame(control);

            Assert.Equal(1, control.RunMissesForTesting);
        }
        finally
        {
            window.Close();
            control.Teardown();
            session.Dispose();
        }
    }

    [AvaloniaFact]
    public void Teardown_drops_every_cached_run()
    {
        var (window, control, pty, session) = Show();
        try
        {
            pty.Emit(Content);
            RenderFrame(control);

            control.Teardown();

            Assert.Equal(0, control.CachedRunCountForTesting);
        }
        finally
        {
            window.Close();
            session.Dispose();
        }
    }
}
