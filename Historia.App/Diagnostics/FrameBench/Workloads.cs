namespace Historia.Diagnostics;

/// <summary>
/// One deterministic benchmark workload, driven a frame at a time by <see cref="FrameRecorder"/>.
/// <see cref="Begin"/> runs in the first frame's callback, <see cref="Step"/> in every callback
/// (the first included) with the seconds since the first, and returns false once the workload is
/// done. Neither may allocate per frame.
/// </summary>
internal interface IFrameWorkload
{
    /// <summary>The workload's name in output file names and the summary.</summary>
    string Name { get; }

    void Begin();

    bool Step(double elapsedSeconds);

    /// <summary>Whether the workload covered its intended range, read after the last frame.</summary>
    WorkloadCoverage Coverage();
}

/// <summary>
/// Whether a workload covered its intended range, why not when it did not, and the numbers that
/// decided it.
/// </summary>
internal sealed record WorkloadCoverage(bool Covered, string? Shortfall, IReadOnlyDictionary<string, double> Values);

/// <summary>The workload names, shared with <see cref="FrameStats"/>'s verdict rule.</summary>
internal static class FrameWorkloadNames
{
    public const string GraphScroll = FrameStats.GraphScrollWorkload;
    public const string GraphJump = FrameStats.GraphJumpWorkload;
    public const string DiffScroll = "diff-scroll";
    public const string Terminal = "terminal";

    /// <summary>Summary order.</summary>
    public static readonly string[] Ordered = [GraphScroll, GraphJump, DiffScroll, Terminal];
}

/// <summary>Shared constants for the scroll workloads.</summary>
internal static class FrameWorkloadRates
{
    /// <summary>Continuous scroll speed of the graph and diff scroll workloads.</summary>
    public const double ScrollPixelsPerSecond = 3000;

    /// <summary>The share of the planned distance or jump count a run must reach to be valid.</summary>
    public const double RequiredCoverage = 0.9;
}

/// <summary><c>graph-scroll</c>: scrolls the commit graph down from the top at a constant
/// <see cref="FrameWorkloadRates.ScrollPixelsPerSecond"/>, through the wheel's scroll path.</summary>
internal sealed class GraphScrollWorkload(CommitGraphControl graph, double seconds) : IFrameWorkload
{
    private double _start;

    public string Name => FrameWorkloadNames.GraphScroll;

    public void Begin() => _start = graph.ScrollOffset;

    public bool Step(double elapsedSeconds)
    {
        var t = Math.Min(elapsedSeconds, seconds);
        graph.SetVerticalOffsetForBench(_start + FrameWorkloadRates.ScrollPixelsPerSecond * t);
        return elapsedSeconds < seconds;
    }

    public WorkloadCoverage Coverage()
    {
        var planned = FrameWorkloadRates.ScrollPixelsPerSecond * seconds;
        var covered = graph.ScrollOffset - _start;
        var rowHeight = graph.RowHeightValue;
        var ok = covered >= FrameWorkloadRates.RequiredCoverage * planned;
        return new WorkloadCoverage(ok,
            ok ? null : string.Create(CultureInfo.InvariantCulture, $"scrolled {covered:F0} of {planned:F0} px"),
            new Dictionary<string, double>
            {
                ["startOffsetPx"] = _start,
                ["endOffsetPx"] = graph.ScrollOffset,
                ["plannedPx"] = planned,
                ["coveredPx"] = covered,
                ["rowHeightPx"] = rowHeight,
                ["rowsAdvanced"] = rowHeight > 0 ? covered / rowHeight : 0,
                ["rowsLoaded"] = graph.RowCountForTesting,
            });
    }
}

/// <summary><c>graph-jump</c>: every <see cref="JumpIntervalSeconds"/>, jumps the commit graph to a
/// seeded-random offset over every row loaded at that moment, so the rows it lands on are
/// cold.</summary>
internal sealed class GraphJumpWorkload(CommitGraphControl graph, double seconds, int seed) : IFrameWorkload
{
    public const double JumpIntervalSeconds = 0.25;

    private Random _random = new(seed);
    private double _nextJump;
    private int _jumps;
    private double _maxOffsetSeen;

    public string Name => FrameWorkloadNames.GraphJump;

    public void Begin()
    {
#pragma warning disable S2245 // a repeatable seeded sequence for the benchmark, not a security use
        _random = new Random(seed);
#pragma warning restore S2245
        _nextJump = 0;
        _jumps = 0;
        _maxOffsetSeen = 0;
    }

    public bool Step(double elapsedSeconds)
    {
        if (elapsedSeconds >= seconds)
            return false;
        if (elapsedSeconds >= _nextJump)
        {
            var max = graph.MaxVerticalOffsetForBench;
            _maxOffsetSeen = Math.Max(_maxOffsetSeen, max);
            graph.SetVerticalOffsetForBench(_random.NextDouble() * max);
            _jumps++;
            _nextJump += JumpIntervalSeconds;
        }
        return true;
    }

    public WorkloadCoverage Coverage()
    {
        var planned = (int)Math.Ceiling(seconds / JumpIntervalSeconds);
        var rowHeight = graph.RowHeightValue;
        string? shortfall = null;
        if (_maxOffsetSeen <= 0)
            shortfall = "the graph had no scroll range to jump over";
        else if (_jumps < FrameWorkloadRates.RequiredCoverage * planned)
            shortfall = $"jumped {_jumps} of {planned} times";
        return new WorkloadCoverage(shortfall is null, shortfall, new Dictionary<string, double>
        {
            ["jumps"] = _jumps,
            ["plannedJumps"] = planned,
            ["maxOffsetPx"] = _maxOffsetSeen,
            ["rowRange"] = rowHeight > 0 ? _maxOffsetSeen / rowHeight : 0,
            ["rowsLoaded"] = graph.RowCountForTesting,
            ["seed"] = seed,
        });
    }
}

/// <summary><c>diff-scroll</c>: scrolls an open file diff down at a constant
/// <see cref="FrameWorkloadRates.ScrollPixelsPerSecond"/>, through the editor's scroll
/// viewer.</summary>
internal sealed class DiffScrollWorkload(ScrollViewer viewer, int documentLines, double seconds) : IFrameWorkload
{
    private double _start;

    public string Name => FrameWorkloadNames.DiffScroll;

    public void Begin() => _start = viewer.Offset.Y;

    public bool Step(double elapsedSeconds)
    {
        var t = Math.Min(elapsedSeconds, seconds);
        var max = Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height);
        var y = Math.Min(_start + FrameWorkloadRates.ScrollPixelsPerSecond * t, max);
        viewer.Offset = new Vector(viewer.Offset.X, y);
        return elapsedSeconds < seconds;
    }

    public WorkloadCoverage Coverage()
    {
        var planned = FrameWorkloadRates.ScrollPixelsPerSecond * seconds;
        var covered = viewer.Offset.Y - _start;
        var ok = covered >= FrameWorkloadRates.RequiredCoverage * planned;
        return new WorkloadCoverage(ok,
            ok ? null : string.Create(CultureInfo.InvariantCulture, $"scrolled {covered:F0} of {planned:F0} px"),
            new Dictionary<string, double>
            {
                ["startOffsetPx"] = _start,
                ["endOffsetPx"] = viewer.Offset.Y,
                ["plannedPx"] = planned,
                ["coveredPx"] = covered,
                ["extentPx"] = viewer.Extent.Height,
                ["documentLines"] = documentLines,
            });
    }
}

/// <summary>
/// <c>terminal</c>: types a fixed command into an open repository shell — a loop that echoes
/// 200,000 numbered lines — and records until the shell exits after it or the time is up.
/// </summary>
internal sealed class TerminalWorkload(TerminalSession session, double seconds) : IFrameWorkload
{
    public const int Lines = 200_000;

    // cmd's loop body must sit in parentheses so the trailing "& exit" runs once, after it.
    private static readonly byte[] WindowsInput = System.Text.Encoding.UTF8.GetBytes(
        $"(for /L %i in (1,1,{Lines}) do @echo %i The quick brown fox jumps over the lazy dog) & exit\r");

    private static readonly byte[] PosixInput = System.Text.Encoding.UTF8.GetBytes(
        $"i=1; while [ $i -le {Lines} ]; do echo \"$i The quick brown fox jumps over the lazy dog\"; i=$((i+1)); done; exit\n");

    /// <summary>The shell the terminal workload runs: cmd without AutoRun on Windows, sh
    /// elsewhere — so the typed command means the same on every machine.</summary>
    public static (string Path, IReadOnlyList<string> Args) Shell()
    {
        if (OperatingSystem.IsWindows())
        {
            var comSpec = Environment.GetEnvironmentVariable("ComSpec");
            return (string.IsNullOrWhiteSpace(comSpec) ? @"C:\Windows\System32\cmd.exe" : comSpec, ["/d"]);
        }
        return ("/bin/sh", []);
    }

    /// <summary>The command line as typed, for the run's output.</summary>
    public static string CommandText =>
        System.Text.Encoding.UTF8.GetString(OperatingSystem.IsWindows() ? WindowsInput : PosixInput).TrimEnd('\r', '\n');

    private double _elapsed;

    public string Name => FrameWorkloadNames.Terminal;

    public void Begin() => session.Write(OperatingSystem.IsWindows() ? WindowsInput : PosixInput);

    public bool Step(double elapsedSeconds)
    {
        _elapsed = elapsedSeconds;
        if (session.State is TerminalSessionState.Exited or TerminalSessionState.Failed)
            return false;
        return elapsedSeconds < seconds;
    }

    public WorkloadCoverage Coverage()
    {
        var exited = session.State == TerminalSessionState.Exited;
        var lastLine = LastEchoedLine();
        string? shortfall = null;
        if (session.State == TerminalSessionState.Failed)
            shortfall = "the shell failed";
        else if (lastLine <= 0)
            shortfall = "the command printed nothing";
        else if (exited && session.ExitCode is { } code && code != 0)
            shortfall = $"the shell exited with {code}";
        else if (!exited && _elapsed < FrameWorkloadRates.RequiredCoverage * seconds)
            shortfall = string.Create(CultureInfo.InvariantCulture, $"recorded {_elapsed:F1} of {seconds:F1} s");
        return new WorkloadCoverage(shortfall is null, shortfall, new Dictionary<string, double>
        {
            ["lastEchoedLine"] = lastLine,
            ["plannedLines"] = Lines,
            ["exited"] = exited ? 1 : 0,
            ["exitCode"] = session.ExitCode ?? -1,
            ["recordedSeconds"] = _elapsed,
        });
    }

    /// <summary>The highest line number the loop has printed, read from the bottom of the
    /// terminal buffer.</summary>
    public int LastEchoedLine() => session.ReadBuffer(view =>
    {
        var best = 0;
        var end = view.TotalLines - 1;
        for (var line = end; line >= 0 && line >= end - 200; line--)
        {
            var text = view.GetLineText(line);
            var digits = 0;
            while (digits < text.Length && char.IsAsciiDigit(text[digits]))
                digits++;
            if (digits > 0 && digits < text.Length && text[digits] == ' '
                && int.TryParse(text.AsSpan(0, digits), NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                best = Math.Max(best, n);
        }
        return best;
    });
}
