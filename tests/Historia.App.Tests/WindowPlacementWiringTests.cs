namespace HistoriaApp.Tests;

/// <summary>The main window persists only its restored geometry and opens on a connected screen
/// even when the saved file holds a minimized-window sentinel. Each test points <c>window.json</c>
/// at a temporary directory.</summary>
[Trait("tier", "ui")]
public sealed class WindowPlacementWiringTests : IDisposable
{
    private readonly string _dir = TestScratch.NewDirectory("historia-winplace-");

    public WindowPlacementWiringTests() => AppPaths.OverrideForTesting(_dir);

    public void Dispose()
    {
        AppPaths.OverrideForTesting(null);
        TestScratch.Delete(_dir);
    }

    private static WindowPlacement ReadSaved()
        => System.Text.Json.JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(AppPaths.WindowPlacementFile), HistoriaJson.Options)!;

    private static void ShowLoaded(Window window)
    {
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.IsLoaded);
    }

    // Placement saves wait for the dispatcher to settle, so drain it before flushing the writer.
    private static async Task SettleAsync(AtomicFileWriter writer)
    {
        Dispatcher.UIThread.RunJobs();
        await writer.FlushAsync();
    }

    private static List<PixelArea> WorkAreas(Window window)
    {
        Assert.NotNull(window.Screens);
        return window.Screens.All
            .Select(s => new PixelArea(s.WorkingArea.X, s.WorkingArea.Y, s.WorkingArea.Width, s.WorkingArea.Height))
            .ToList();
    }

    private static PixelArea PixelBounds(Window window)
    {
        var scaling = window.RenderScaling > 0 ? window.RenderScaling : 1;
        return new PixelArea(
            window.Position.X,
            window.Position.Y,
            (int)Math.Round(window.Bounds.Width * scaling),
            (int)Math.Round(window.Bounds.Height * scaling));
    }

    [AvaloniaFact]
    public async Task MinimizedWindow_DoesNotPersistPlacement()
    {
        var window = new MainWindow();
        var writer = new AtomicFileWriter(AppPaths.WindowPlacementFile);
        window.AttachPlacementWriter(writer);
        ShowLoaded(window);
        try
        {
            window.Position = new PixelPoint(220, 140);
            await SettleAsync(writer);
            var before = File.ReadAllText(AppPaths.WindowPlacementFile);

            window.WindowState = WindowState.Minimized;
            window.Position = new PixelPoint(-32000, -32000);
            await SettleAsync(writer);

            Assert.Equal(before, File.ReadAllText(AppPaths.WindowPlacementFile));
            Assert.Equal(220, ReadSaved().Left);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task MaximizedWindow_PersistsLastNormalRectangle()
    {
        var window = new MainWindow();
        var writer = new AtomicFileWriter(AppPaths.WindowPlacementFile);
        window.AttachPlacementWriter(writer);
        ShowLoaded(window);
        try
        {
            window.Position = new PixelPoint(220, 140);
            await SettleAsync(writer);

            // Win32 order: the window moves to the monitor corner before WindowState changes.
            window.Position = new PixelPoint(-8, -8);
            window.WindowState = WindowState.Maximized;
            await SettleAsync(writer);

            var saved = ReadSaved();
            Assert.True(saved.Maximized);
            Assert.Equal(220, saved.Left);
            Assert.Equal(140, saved.Top);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void SentinelPlacementFile_OpensOnScreen()
    {
        File.WriteAllText(AppPaths.WindowPlacementFile,
            """{ "left": -32000, "top": -32000, "width": 1536, "height": 792, "maximized": false }""");

        var window = new MainWindow();
        Assert.Equal(WindowStartupLocation.CenterScreen, window.WindowStartupLocation);
        ShowLoaded(window);
        try
        {
            Assert.True(WindowPlacementCalculator.IsVisible(PixelBounds(window), WorkAreas(window)));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void OffScreenPlacementFile_IsCorrectedBeforeShow()
    {
        File.WriteAllText(AppPaths.WindowPlacementFile,
            """{ "left": 9000, "top": 400, "width": 1000, "height": 700, "maximized": false }""");

        var window = new MainWindow();
        try
        {
            Assert.True(WindowPlacementCalculator.IsVisible(
                new PixelArea(window.Position.X, window.Position.Y, 600, 400), WorkAreas(window)));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task MaximizedOffScreenPlacementFile_SavesCorrectedNormalRectangle()
    {
        File.WriteAllText(AppPaths.WindowPlacementFile,
            """{ "left": 9000, "top": 400, "width": 1000, "height": 700, "maximized": true }""");

        var window = new MainWindow();
        var writer = new AtomicFileWriter(AppPaths.WindowPlacementFile);
        window.AttachPlacementWriter(writer);
        ShowLoaded(window);
        try
        {
            Assert.Equal(WindowState.Maximized, window.WindowState);
            window.Position = new PixelPoint(-8, -8);
            await SettleAsync(writer);

            var saved = ReadSaved();
            Assert.True(saved.Maximized);
            Assert.True(WindowPlacementCalculator.IsVisible(
                new PixelArea((int)saved.Left, (int)saved.Top, 600, 400), WorkAreas(window)));
        }
        finally
        {
            window.Close();
        }
    }
}
