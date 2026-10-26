namespace Historia.Tests;

/// <summary>
/// "Terminal output costs the same no matter how much scrollback is held": a small chunk fed to a
/// session whose scrollback is full allocates a small, fixed amount, whatever the scrollback
/// size. This is session code, not the graph render path, so a byte bound is measured directly.
/// </summary>
[Trait("tier", "unit")]
public class TerminalOutputCostTests
{
    private const int ChunkCount = 200;
    private const int MaxBytesPerChunk = 4 * 1024;

    private static byte[] Line(int n)
        => Encoding.UTF8.GetBytes($"line {n:D6} the quick brown fox jumps over the lazy dog\r\n");

    /// <summary>Fills a session past <paramref name="scrollback"/> lines, warms the output path,
    /// then returns the average bytes allocated per small chunk over <see cref="ChunkCount"/>
    /// chunks. The chunks are encoded beforehand so only the session's own cost is counted.</summary>
    private static long BytesPerChunk(int scrollback)
    {
        var pty = new FakePtyConnection();
        using var session = new TerminalSession(
            TerminalOrigin.Home("/home/me"), pty, new ImmediateUiDispatcher(), 80, 24, scrollback);

        var total = scrollback + 24 + 500;
        for (var i = 0; i < total; i++)
            pty.EmitOutput(Line(i));

        Assert.True(session.ReadBuffer(v => v.TotalLines) > scrollback,
            "the scrollback must be full before the measurement starts");

        var chunks = Enumerable.Range(total, ChunkCount + 50).Select(Line).ToArray();
        for (var i = 0; i < 50; i++)
            pty.EmitOutput(chunks[i]);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 50; i < chunks.Length; i++)
            pty.EmitOutput(chunks[i]);
        var after = GC.GetAllocatedBytesForCurrentThread();

        return (after - before) / ChunkCount;
    }

    [Fact]
    public void SmallChunk_AtDefaultScrollback_AllocatesUnderFourKilobytes()
        => Assert.True(BytesPerChunk(5_000) < MaxBytesPerChunk);

    [Fact]
    public void SmallChunk_CostDoesNotGrowWithScrollbackSize()
    {
        var small = BytesPerChunk(5_000);
        var large = BytesPerChunk(20_000);

        Assert.True(large < MaxBytesPerChunk, $"{large} bytes per chunk at 20,000 lines");
        // Per-line work would make the 20,000-line session roughly four times as costly.
        Assert.True(large < small * 2 + 512, $"{small} bytes per chunk at 5,000 lines, {large} at 20,000");
    }
}
