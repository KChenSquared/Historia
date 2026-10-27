namespace HistoriaApp.Tests;

[Trait("tier", "fast")]
public class BodyColumnLayoutTests
{
    private static void AssertFillsTotal(double total, (double Left, double Centre, double Right) columns)
    {
        Assert.True(columns.Left >= 0 && double.IsFinite(columns.Left), $"left {columns.Left}");
        Assert.True(columns.Centre >= 0 && double.IsFinite(columns.Centre), $"centre {columns.Centre}");
        Assert.True(columns.Right >= 0 && double.IsFinite(columns.Right), $"right {columns.Right}");
        Assert.Equal(total, columns.Left + columns.Centre + columns.Right, 9);
    }

    [Fact]
    public void Resolve_never_returns_a_negative_centre_at_the_recorded_failures()
    {
        var first = BodyColumnLayout.Resolve(1000, 520.2, 479.8, 250, 360);
        AssertFillsTotal(1000, first);

        var second = BodyColumnLayout.Resolve(1569.6, 637.6, 932.0, 250, 360);
        AssertFillsTotal(1569.6, second);
    }

    [Fact]
    public void Resolve_never_returns_a_negative_width_for_random_tenth_pixel_inputs()
    {
        var random = new Random(20261026);
        for (var i = 0; i < 200_000; i++)
        {
            double Tenth(int max) => random.Next(0, max * 10 + 1) / 10.0;

            var total = Tenth(2400);
            var leftMin = 250 + Tenth(100);
            var rightMin = 360 + Tenth(100);
            var leftIntent = Tenth(1500);
            var rightIntent = Tenth(1500);
            if (random.Next(3) == 0)
                total = leftIntent + rightIntent;

            var columns = BodyColumnLayout.Resolve(total, leftIntent, rightIntent, leftMin, rightMin);
            AssertFillsTotal(total, columns);
        }
    }

    [Fact]
    public void Resolve_gives_the_remainder_to_the_right_column_when_clamping()
    {
        var (left, centre, right) = BodyColumnLayout.Resolve(700, 250, 800, 250, 360);
        Assert.Equal(250, left);
        Assert.Equal(0, centre);
        Assert.Equal(450, right);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-5.0)]
    public void Resolve_treats_a_bad_input_as_zero(double bad)
    {
        AssertFillsTotal(1000, BodyColumnLayout.Resolve(1000, bad, 400, 250, 360));
        AssertFillsTotal(1000, BodyColumnLayout.Resolve(1000, 300, bad, 250, 360));
        AssertFillsTotal(1000, BodyColumnLayout.Resolve(1000, 300, 400, bad, bad));
        Assert.Equal((0.0, 0.0, 0.0), BodyColumnLayout.Resolve(bad, 300, 400, 250, 360));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.15)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(1.75)]
    public void RoundColumns_stays_inside_the_total_on_the_pixel_grid(double scale)
    {
        var random = new Random(20261026 + (int)(scale * 100));
        for (var i = 0; i < 50_000; i++)
        {
            // Totals off the pixel grid, including ones below the pane minimums.
            var total = random.Next(0, 24_000) / 10.0 + random.NextDouble() * 0.1;
            var (left, _, right) = BodyColumnLayout.Resolve(
                total, random.Next(0, 15_000) / 10.0, random.Next(0, 15_000) / 10.0, 250, 360);
            var (roundedLeft, roundedRight) = BodyColumnLayout.RoundColumns(total, left, right, scale);

            var context = $"scale {scale}, total {total}, left {left}, right {right}";
            Assert.True(roundedLeft >= 0 && double.IsFinite(roundedLeft), context);
            Assert.True(roundedRight >= 0 && double.IsFinite(roundedRight), context);
            Assert.True(roundedLeft + roundedRight <= total + 1e-5, context);
            Assert.True(OnPixelGrid(roundedLeft, scale), context);
            Assert.True(OnPixelGrid(roundedRight, scale), context);
        }
    }

    [Fact]
    public void RoundColumns_keeps_the_pixel_that_float_error_would_cost()
    {
        var (left, right) = BodyColumnLayout.RoundColumns(924, 667.2, 256.8, 1.25);
        Assert.Equal(667.2, left, 9);
        Assert.Equal(256.8, right, 9);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-5.0)]
    public void RoundColumns_treats_a_bad_input_as_zero(double bad)
    {
        var (left, right) = BodyColumnLayout.RoundColumns(bad, 300, 400, 1.25);
        Assert.Equal((0.0, 0.0), (left, right));

        (left, right) = BodyColumnLayout.RoundColumns(1000, bad, 400, bad);
        Assert.Equal(0, left);
        Assert.Equal(400, right);
    }

    private static bool OnPixelGrid(double value, double scale)
    {
        var pixels = value * scale;
        return Math.Abs(pixels - Math.Round(pixels)) < 1e-6;
    }
}
