namespace Historia.Layout;

/// <summary>
/// Pure column layout maths for the shell's three body panels (sidebar, commit graph, detail pane).
/// </summary>
public static class BodyColumnLayout
{
    /// <summary>
    /// Computes the effective minimum pane width taking the shortcuts bar's natural width into account.
    /// </summary>
    public static double EffectiveMin(double baseMin, double barNaturalWidth) =>
        Math.Max(baseMin, Math.Ceiling(barNaturalWidth));

    /// <summary>
    /// Resolves the width of the Left, Centre, and Right columns given the total content width,
    /// stored user intents, and effective pane minimums. Floors both side panes at their minimums
    /// (left wins over right when even that doesn't fit) and gives way centre-first. Every width is
    /// finite and non-negative and the three sum to <paramref name="total"/>; a non-finite or
    /// negative input counts as 0.
    /// </summary>
    public static (double Left, double Centre, double Right) Resolve(
        double total,
        double leftIntent,
        double rightIntent,
        double leftMin,
        double rightMin)
    {
        total = Sanitize(total);
        if (total <= 0)
            return (0, 0, 0);

        leftMin = Sanitize(leftMin);
        rightMin = Sanitize(rightMin);
        var lIntent = Math.Max(leftMin, Sanitize(leftIntent));
        var rIntent = Math.Max(rightMin, Sanitize(rightIntent));

        double left;
        double right;
        if (total >= lIntent + rIntent)
        {
            left = lIntent;
            right = rIntent;
        }
        else if (total >= leftMin + rightMin)
        {
            // Narrow space: centre collapses first and both sides stay >= their minimums.
            left = Math.Clamp(total - rightMin, leftMin, lIntent);
            right = total - left;
        }
        else
        {
            // Extremely narrow: left is kept up to total; right takes whatever space remains.
            left = Math.Min(leftMin, total);
            right = total - left;
        }

        left = Math.Min(left, total);
        var centre = total - left - right;
        if (centre < 0)
        {
            // Rounding or an over-wide intent: the right column takes the remainder.
            centre = 0;
            right = Math.Max(0, total - left);
        }

        return (left, centre, right);
    }

    /// <summary>
    /// Rounds the pinned side-column widths to the device pixel grid at <paramref name="scale"/> so
    /// that together they never exceed <paramref name="total"/>. Rounding can push the left column
    /// past the total and the right column over what is left; the left width is capped at the last
    /// whole pixel inside the total and the right width is shaved to whole pixels. The shave carries
    /// a small epsilon so float error (for example 924 * 1.25 - 667.2 * 1.25) does not cost a pixel.
    /// Both widths are finite and non-negative.
    /// </summary>
    internal static (double Left, double Right) RoundColumns(double total, double left, double right, double scale)
    {
        total = Sanitize(total);
        scale = double.IsFinite(scale) && scale > 0 ? scale : 1;

        var roundedLeft = LayoutHelper.RoundLayoutValue(Sanitize(left), scale);
        var roundedRight = LayoutHelper.RoundLayoutValue(Sanitize(right), scale);
        roundedLeft = Math.Min(roundedLeft, Math.Floor(total * scale + 1e-9) / scale);
        if (roundedLeft + roundedRight > total)
            roundedRight = Math.Floor((total - roundedLeft) * scale + 1e-6) / scale;

        return (Math.Max(0, roundedLeft), Math.Max(0, roundedRight));
    }

    private static double Sanitize(double value) => double.IsFinite(value) && value > 0 ? value : 0;
}
