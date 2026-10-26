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

    private static (Window Window, TerminalControl Control, EmittingPty Pty, TerminalSession Session) Show()
    {
        var services = Services();
        var control = new TerminalControl();
        control.Attach(services);

        var pty = new EmittingPty();
        var session = new TerminalSession(
            TerminalOrigin.Home("/home/me"), pty, services.GetRequiredService<IUiDispatcher>(), 80, 24, 1000);
        control.SetSession(session);

        var window = new Window { Content = control, Width = 640, Height = 384 };
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
            pty.Emit(Content);

            RenderFrame(control);
            Assert.True(control.PenMissesForTesting > 0, "the first frame builds the unfocused cursor and underline pens");

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
