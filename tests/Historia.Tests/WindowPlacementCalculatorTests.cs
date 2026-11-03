namespace Historia.Tests;

/// <summary>The pure rules that keep the main window reachable: which placements may be saved, when a
/// rectangle counts as on screen, and where an unreachable one is moved.</summary>
public sealed class WindowPlacementCalculatorTests
{
    private static readonly PixelArea Single = new(0, 0, 3440, 1400);

    private static WindowPlacement Placement(double left, double top, double width = 1536, double height = 792)
        => new() { Left = left, Top = top, Width = width, Height = height };

    [Fact]
    public void IsPersistable_RejectsMinimizedSentinel()
    {
        Assert.False(WindowPlacementCalculator.IsPersistable(Placement(-32000, -32000)));
        Assert.False(WindowPlacementCalculator.IsPersistable(Placement(-32000, 100)));
        Assert.False(WindowPlacementCalculator.IsPersistable(Placement(100, -30000)));
        Assert.True(WindowPlacementCalculator.IsPersistable(Placement(-1920, 40)));
    }

    [Fact]
    public void IsPersistable_RejectsNaNAndZeroSize()
    {
        Assert.False(WindowPlacementCalculator.IsPersistable(Placement(double.NaN, 0)));
        Assert.False(WindowPlacementCalculator.IsPersistable(Placement(0, double.PositiveInfinity)));
        Assert.False(WindowPlacementCalculator.IsPersistable(Placement(0, 0, double.NaN)));
        Assert.False(WindowPlacementCalculator.IsPersistable(Placement(0, 0, 0)));
        Assert.False(WindowPlacementCalculator.IsPersistable(Placement(0, 0, 800, -1)));
    }

    [Fact]
    public void IsVisible_FalseForUnpluggedRightMonitor()
    {
        var window = new PixelArea(3500, 200, 1536, 792);
        Assert.False(WindowPlacementCalculator.IsVisible(window, [Single]));
    }

    [Fact]
    public void IsVisible_FalseForSliverOverlap()
    {
        var window = new PixelArea(3430, 200, 1536, 792);
        Assert.False(WindowPlacementCalculator.IsVisible(window, [Single]));
    }

    [Fact]
    public void IsVisible_FalseWhenTitleStripAboveWorkArea()
    {
        var window = new PixelArea(200, -20, 1536, 792);
        Assert.False(WindowPlacementCalculator.IsVisible(window, [Single]));
    }

    [Fact]
    public void Fit_ClampsPartiallyVisibleIntoNearestWorkArea()
    {
        var window = new PixelArea(3400, 200, 1536, 792);
        Assert.False(WindowPlacementCalculator.IsVisible(window, [Single]));

        var fitted = WindowPlacementCalculator.Fit(window, [Single], 0);

        Assert.Equal(new PixelArea(3440 - 1536, 200, 1536, 792), fitted);
    }

    [Fact]
    public void Fit_CentresOnPrimaryWhenNoOverlap()
    {
        var left = new PixelArea(-1920, 0, 1920, 1040);
        var primary = new PixelArea(0, 0, 2560, 1400);
        var window = new PixelArea(6000, 500, 1000, 700);

        var fitted = WindowPlacementCalculator.Fit(window, [left, primary], 1);

        Assert.Equal(new PixelArea(780, 350, 1000, 700), fitted);
    }

    [Fact]
    public void Fit_ShrinksWindowLargerThanWorkArea()
    {
        var area = new PixelArea(0, 0, 1280, 720);
        var window = new PixelArea(-5000, -4000, 3000, 2000);

        var fitted = WindowPlacementCalculator.Fit(window, [area], 0);

        Assert.Equal(area, fitted);
    }

    [Fact]
    public void Fit_NegativeCoordinateLeftMonitor()
    {
        var window = new PixelArea(-1800, 100, 1200, 800);
        var fitted = WindowPlacementCalculator.Fit(window, [Single], 0);

        Assert.True(WindowPlacementCalculator.IsVisible(fitted, [Single]));
        Assert.Equal(new PixelArea(1120, 300, 1200, 800), fitted);
    }

    [Fact]
    public void Fit_LeavesVisibleWindowUntouched()
    {
        var window = new PixelArea(300, 200, 1536, 792);
        Assert.Equal(window, WindowPlacementCalculator.Fit(window, [Single], 0));
    }

    [Fact]
    public void Fit_EmptyWorkAreasReturnsInput()
    {
        var window = new PixelArea(-32000, -32000, 1536, 792);
        Assert.Equal(window, WindowPlacementCalculator.Fit(window, [], 0));
    }

    [Fact]
    public void Fit_PrefersWorkAreaWithLargestOverlap()
    {
        var a = new PixelArea(0, 0, 1000, 1000);
        var b = new PixelArea(1000, 0, 1000, 1000);
        // Title strip is above both areas, so the window needs correcting; most of it overlaps b.
        var window = new PixelArea(900, -100, 900, 600);

        var fitted = WindowPlacementCalculator.Fit(window, [a, b], 0);

        Assert.Equal(new PixelArea(1000, 0, 900, 600), fitted);
    }
}
