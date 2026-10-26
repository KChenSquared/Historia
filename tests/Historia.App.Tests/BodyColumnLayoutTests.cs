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
}
