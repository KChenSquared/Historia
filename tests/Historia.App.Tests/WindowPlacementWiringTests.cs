namespace HistoriaApp.Tests;

/// <summary>The main window persists only its restored geometry and opens on a connected screen
/// even when the saved file holds a minimized-window sentinel. Each test points <c>window.json</c>
/// at a temporary directory.</summary>
[Trait("tier", "ui")]
public sealed class WindowPlacementWiringTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "historia-winplace-" + Guid.NewGuid().ToString("N"));

    public WindowPlacementWiringTests()
    {
        Directory.CreateDirectory(_dir);
        AppPaths.OverrideForTesting(_dir);
    }

    public void Dispose()
    {
        AppPaths.OverrideForTesting(null);
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best effort */ }
    }

    private static WindowPlacement ReadSaved()
        => System.Text.Json.JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(AppPaths.WindowPlacementFile), HistoriaJson.Options)!;

    private static void ShowLoaded(Window window)
    {
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.IsLoaded);
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
            await writer.FlushAsync();
            var before = File.ReadAllText(AppPaths.WindowPlacementFile);

            window.WindowState = WindowState.Minimized;
            window.Position = new PixelPoint(-32000, -32000);
            await writer.FlushAsync();

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
            window.WindowState = WindowState.Maximized;
            window.Position = new PixelPoint(-8, -8);
            await writer.FlushAsync();

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
        ShowLoaded(window);
        try
        {
            var screens = window.Screens;
            Assert.NotNull(screens);
            var workAreas = screens.All
                .Select(s => new PixelArea(s.WorkingArea.X, s.WorkingArea.Y, s.WorkingArea.Width, s.WorkingArea.Height))
                .ToList();
            Assert.True(WindowPlacementCalculator.IsVisible(
                new PixelArea(window.Position.X, window.Position.Y, 600, 400), workAreas));
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
            Assert.NotNull(window.Screens);
            var workAreas = window.Screens.All
                .Select(s => new PixelArea(s.WorkingArea.X, s.WorkingArea.Y, s.WorkingArea.Width, s.WorkingArea.Height))
                .ToList();
            Assert.True(WindowPlacementCalculator.IsVisible(
                new PixelArea(window.Position.X, window.Position.Y, 600, 400), workAreas));
        }
        finally
        {
            window.Close();
        }
    }
}
