namespace Historia.Services.Storage;

/// <summary>An axis-aligned rectangle in physical screen pixels. Core stays free of Avalonia, so the
/// window layer converts its own rectangle types to this one.</summary>
public readonly record struct PixelArea(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;

    /// <summary>The area shared with <paramref name="other"/>, or an empty area when they do not touch.</summary>
    public PixelArea Intersect(PixelArea other)
    {
        var left = Math.Max(X, other.X);
        var top = Math.Max(Y, other.Y);
        var right = Math.Min(Right, other.Right);
        var bottom = Math.Min(Bottom, other.Bottom);
        return right <= left || bottom <= top
            ? new PixelArea(left, top, 0, 0)
            : new PixelArea(left, top, right - left, bottom - top);
    }

    internal long AreaSize => (long)Width * Height;
}

/// <summary>
/// Decides whether a saved or live main-window rectangle is worth persisting and whether it can be
/// reached on the connected screens, and computes the nearest reachable rectangle when it cannot.
/// Pure arithmetic over physical pixels; no UI types.
/// </summary>
public static class WindowPlacementCalculator
{
    /// <summary>Win32 reports roughly -32000 for the position of a minimized window; anything this far
    /// off is a sentinel rather than a place the user put the window.</summary>
    public const int SentinelThreshold = -30000;

    /// <summary>Height of the strip at the top of the window that must stay reachable (the title bar).</summary>
    public const int TitleStripHeight = 32;

    /// <summary>Width of the title strip that must lie inside one screen for the window to count as on screen.</summary>
    public const int MinVisibleStripWidth = 120;

    /// <summary>False for a placement that must never be written: a sentinel or non-finite position,
    /// or an empty size.</summary>
    public static bool IsPersistable(WindowPlacement p)
    {
        if (!double.IsFinite(p.Left) || !double.IsFinite(p.Top)
            || !double.IsFinite(p.Width) || !double.IsFinite(p.Height))
            return false;
        if (p.Left <= SentinelThreshold || p.Top <= SentinelThreshold)
            return false;
        return p.Width > 0 && p.Height > 0;
    }

    /// <summary>True when one work area contains at least <see cref="MinVisibleStripWidth"/> by
    /// <see cref="TitleStripHeight"/> pixels of the window's top strip (less for a window smaller than that).</summary>
    public static bool IsVisible(PixelArea window, IReadOnlyList<PixelArea> workAreas)
    {
        if (window.Width <= 0 || window.Height <= 0)
            return false;

        var strip = new PixelArea(window.X, window.Y, window.Width, Math.Min(TitleStripHeight, window.Height));
        var needWidth = Math.Min(MinVisibleStripWidth, strip.Width);
        foreach (var area in workAreas)
        {
            var shared = strip.Intersect(area);
            if (shared.Width >= needWidth && shared.Height >= strip.Height)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Returns <paramref name="window"/> unchanged when it is reachable. Otherwise picks the work area
    /// it overlaps most (the primary one when it overlaps none), shrinks it to fit that area and clamps
    /// it inside, centring it when there was no overlap. An empty <paramref name="workAreas"/> returns
    /// the input so the caller can fall back to its own startup placement.
    /// </summary>
    public static PixelArea Fit(PixelArea window, IReadOnlyList<PixelArea> workAreas, int primaryIndex)
    {
        if (workAreas.Count == 0 || IsVisible(window, workAreas))
            return window;

        var best = -1;
        long bestOverlap = 0;
        for (var i = 0; i < workAreas.Count; i++)
        {
            var overlap = window.Intersect(workAreas[i]).AreaSize;
            if (overlap > bestOverlap)
            {
                bestOverlap = overlap;
                best = i;
            }
        }

        var overlapped = best >= 0;
        var target = workAreas[overlapped ? best : Math.Clamp(primaryIndex, 0, workAreas.Count - 1)];

        var width = Math.Max(1, Math.Min(window.Width, target.Width));
        var height = Math.Max(1, Math.Min(window.Height, target.Height));

        int x;
        int y;
        if (overlapped)
        {
            x = Math.Clamp(window.X, target.X, target.Right - width);
            y = Math.Clamp(window.Y, target.Y, target.Bottom - height);
        }
        else
        {
            x = target.X + (target.Width - width) / 2;
            y = target.Y + (target.Height - height) / 2;
        }
        return new PixelArea(x, y, width, height);
    }
}
