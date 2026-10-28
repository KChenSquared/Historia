namespace HistoriaApp.Tests;

[Trait("tier", "ui")]
public class TerminalResizeGuardTests
{
    private sealed class RecordingPty : IPtyConnection
    {
        public readonly List<(int Cols, int Rows)> Resizes = [];
        public bool ThrowOnResize;

#pragma warning disable CS0067
        public event Action<ReadOnlyMemory<byte>>? OutputReceived;
        public event Action<int>? Exited;
#pragma warning restore CS0067

        public void Write(ReadOnlySpan<byte> data) { }
        public void Resize(int columns, int rows)
        {
            Resizes.Add((columns, rows));
            if (ThrowOnResize)
                throw new InvalidOperationException("resize failed");
        }
        public void Kill() { }
        public void Dispose() { }
    }

    private sealed class StubThemeService : IThemeService
    {
        public EffectiveTheme Effective { get; set; } = EffectiveTheme.Dark;
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

    private static IServiceProvider CreateServices()
    {
        var sc = new ServiceCollection();
        sc.AddSingleton<IThemeService, StubThemeService>();
        sc.AddSingleton<IUiDispatcher, Historia.Platform.UiDispatcher>();
        sc.AddSingleton<ISettingsService>(new ConflictResolverViewTests.FakeSettingsService());
        sc.AddSingleton<IExternalLauncher, ExternalLauncher>();
        return sc.BuildServiceProvider();
    }

    [Fact]
    public void HasUsableSize_ZeroBounds_False()
    {
        Assert.False(TerminalControl.HasUsableSize(new Size(0, 0), cellWidth: 8, cellHeight: 16));
    }

    [Fact]
    public void HasUsableSize_SubCellBounds_False()
    {
        Assert.False(TerminalControl.HasUsableSize(new Size(7, 16), cellWidth: 8, cellHeight: 16));
        Assert.False(TerminalControl.HasUsableSize(new Size(8, 15), cellWidth: 8, cellHeight: 16));
    }

    [Fact]
    public void HasUsableSize_OneCellOrMore_True()
    {
        Assert.True(TerminalControl.HasUsableSize(new Size(8, 16), cellWidth: 8, cellHeight: 16));
        Assert.True(TerminalControl.HasUsableSize(new Size(640, 384), cellWidth: 8, cellHeight: 16));
    }

    [Fact]
    public void HasUsableSize_ZeroCellMetrics_False()
    {
        Assert.False(TerminalControl.HasUsableSize(new Size(640, 384), cellWidth: 0, cellHeight: 16));
        Assert.False(TerminalControl.HasUsableSize(new Size(640, 384), cellWidth: 8, cellHeight: 0));
        Assert.False(TerminalControl.HasUsableSize(new Size(640, 384), cellWidth: -1, cellHeight: 16));
    }

    [AvaloniaFact]
    public void SetSession_OnUnlaidOutControl_DoesNotResizeSession()
    {
        var services = CreateServices();
        var control = new TerminalControl();
        control.Attach(services);

        var pty = new RecordingPty();
        var dispatcher = services.GetRequiredService<IUiDispatcher>();
        using var session = new TerminalSession(
            TerminalOrigin.Home("/home/me"),
            pty,
            dispatcher,
            80,
            24,
            1000);

        control.SetSession(session);

        Assert.Empty(pty.Resizes);
        control.Teardown();
    }

    [AvaloniaFact]
    public void FirstLayout_PushesRealSizeAfterThePass()
    {
        var services = CreateServices();
        var control = new TerminalControl();
        control.Attach(services);

        var pty = new RecordingPty();
        var dispatcher = services.GetRequiredService<IUiDispatcher>();
        using var session = new TerminalSession(
            TerminalOrigin.Home("/home/me"),
            pty,
            dispatcher,
            80,
            24,
            1000);

        control.SetSession(session);
        Assert.Empty(pty.Resizes);

        var window = new Window { Content = control, Width = 640, Height = 384 };
        window.Show();
        window.UpdateLayout();

        Assert.Empty(pty.Resizes);
        Dispatcher.UIThread.RunJobs();

        Assert.Single(pty.Resizes);
        var (cols, rows) = pty.Resizes[0];
        Assert.True(cols > 1, $"Expected cols > 1, got {cols}");
        Assert.True(rows > 1, $"Expected rows > 1, got {rows}");

        window.Close();
        control.Teardown();
    }

    [AvaloniaFact]
    public void LaterSizeChange_IsStillDebounced()
    {
        var services = CreateServices();
        var control = new TerminalControl();
        control.Attach(services);

        var pty = new RecordingPty();
        var dispatcher = services.GetRequiredService<IUiDispatcher>();
        using var session = new TerminalSession(
            TerminalOrigin.Home("/home/me"),
            pty,
            dispatcher,
            80,
            24,
            1000);

        control.SetSession(session);

        var window = new Window { Content = control, Width = 640, Height = 384 };
        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.Single(pty.Resizes);

        window.Width = 800;
        window.Height = 500;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.Single(pty.Resizes);

        window.Close();
        control.Teardown();
    }

    [AvaloniaFact]
    public void SessionResizeFailure_IsLoggedAndDoesNotEscape()
    {
        var stateDir = TestScratch.NewDirectory("historia-terminal-resize-log-");
        Historia.Diagnostics.AppLog.Enabled = false;
        AppPaths.OverrideForTesting(stateDir);
        try
        {
            var services = CreateServices();
            var control = new TerminalControl();
            control.Attach(services);

            var pty = new RecordingPty { ThrowOnResize = true };
            using var session = new TerminalSession(
                TerminalOrigin.Home("/home/me"),
                pty,
                services.GetRequiredService<IUiDispatcher>(),
                80,
                24,
                1000);
            control.SetSession(session);

            Historia.Diagnostics.AppLog.Enabled = true;

            var window = new Window { Content = control, Width = 640, Height = 384 };
            window.Show();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            Assert.Single(pty.Resizes);
            window.Close();
            control.Teardown();

            Historia.Diagnostics.AppLog.Enabled = false; // close the session writer before reading the file back
            var logFile = Assert.Single(Directory.GetFiles(AppPaths.LogsDirectory, "dev-*.log"));
            var text = File.ReadAllText(logFile);
            Assert.Contains("[terminal]", text);
            Assert.Contains("Pty resize to", text);
            Assert.Contains("failed", text);
        }
        finally
        {
            Historia.Diagnostics.AppLog.Enabled = false;
            AppPaths.OverrideForTesting(null);
            TestScratch.Delete(stateDir);
        }
    }
}
