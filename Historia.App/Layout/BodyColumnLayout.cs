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

    private static double Sanitize(double value) => double.IsFinite(value) && value > 0 ? value : 0;
}
