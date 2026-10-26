namespace Historia.Tests;

/// <summary>
/// An extend places rows into the layout state the render thread also reads, under
/// <c>lock (state)</c>. Splitting it into chunks must bound how long any one hold lasts, and
/// must leave exactly the layout a single-shot extend (and a full compute) produces.
/// </summary>
[Trait("tier", "unit")]
public sealed class GraphLayoutExtendLockTests
{
    private readonly DagLayoutEngine _engine = new();

    private static GitObjectId Sha(int n) => GitObjectId.Parse(n.ToString("x40"));

    private static RawCommit C(int id, params int[] parents) => new(
        Sha(id),
        parents.Select(Sha).ToList(),
        new CommitMetadata
        {
            Id = Sha(id),
            MessageShort = $"commit {id}",
            AuthorName = "A",
            AuthorEmail = "a@x",
            AuthorWhen = DateTimeOffset.UnixEpoch.AddMinutes(id),
            CommitWhen = DateTimeOffset.UnixEpoch.AddMinutes(id),
            ParentCount = parents.Length,
        });

    /// <summary>A single-tip history of <paramref name="count"/> commits: each commit's first
    /// parent is the next one, and every seventh also lists a second parent a few rows down, so
    /// lanes open, run alongside the trunk and close again all the way through.</summary>
    private static List<RawCommit> History(int count)
    {
        var list = new List<RawCommit>(count);
        for (var i = 0; i < count; i++)
        {
            var parents = new List<int>();
            if (i + 1 < count)
                parents.Add(i + 1);
            if (i % 7 == 0 && i + 5 < count)
                parents.Add(i + 5);
            list.Add(C(i, [.. parents]));
        }
        return list;
    }

    private static Dictionary<GitObjectId, IReadOnlyList<GraphDecorationInput>> Decorations(int count, int headRow)
    {
        var map = new Dictionary<GitObjectId, IReadOnlyList<GraphDecorationInput>>();
        for (var i = 0; i < count; i += 53)
            map[Sha(i)] = [new GraphDecorationInput(DecorationType.Tag, $"v{i}")];
        map[Sha(headRow)] = [new GraphDecorationInput(DecorationType.LocalBranch, "main", IsLocal: true, IsHead: true)];
        return map;
    }

    /// <summary><c>IsComplete</c> is not compared when <paramref name="expected"/> is a full compute:
    /// an extend reports completion only when it is handed no more commits.</summary>
    private static void AssertSameLayout(GraphLayoutResult expected, GraphLayoutResult actual, bool compareComplete = true)
    {
        var e = expected.Layout;
        var a = actual.Layout;
        Assert.Equal(e.RowCount, a.RowCount);
        Assert.Equal(e.EdgeCount, a.EdgeCount);
        Assert.Equal(e.TotalLaneCount, a.TotalLaneCount);
        if (compareComplete)
            Assert.Equal(e.IsComplete, a.IsComplete);
        Assert.Equal(e.HeadRow, a.HeadRow);
        Assert.Equal(e.Decorations, a.Decorations);
        Assert.Equal(e.Commits.Count, a.Commits.Count);
        for (var row = 0; row < e.RowCount; row++)
        {
            Assert.Equal(e.Rows[row], a.Rows[row]);
            Assert.Equal(e.Commits[row].Id, a.Commits[row].Id);
            Assert.Equal(e.GetRowSegments(row).ToArray(), a.GetRowSegments(row).ToArray());
            Assert.Equal(e.GetRowElbows(row).ToArray(), a.GetRowElbows(row).ToArray());
        }
    }

    [Fact]
    public void A_chunked_extend_equals_a_single_shot_extend_and_a_full_compute()
    {
        const int total = 6_000;
        const int first = 500;
        var commits = History(total);
        var decos = Decorations(total, headRow: 2_345);

        var single = _engine.Extend(
            _engine.Compute(commits.GetRange(0, first), decos, first),
            commits.GetRange(first, total - first), decos, total - first);

        var chunkedStart = _engine.Compute(commits.GetRange(0, first), decos, first);
        var chunked = GraphLayoutService.ExtendInChunks(
            chunkedStart, commits.GetRange(first, total - first), decos, consumed: first, additionalRows: total - first,
            chunkRows: 137);

        Assert.NotNull(chunked);
        AssertSameLayout(single, chunked!);
        AssertSameLayout(_engine.Compute(commits, decos, total), chunked, compareComplete: false);
    }

    [Fact]
    public void Successive_chunked_extends_equal_one_extend()
    {
        const int total = 5_000;
        var commits = History(total);
        var decos = Decorations(total, headRow: 4_100);

        var steps = _engine.Compute(commits.GetRange(0, 400), decos, 400);
        var consumed = 400;
        while (consumed < total)
        {
            var take = Math.Min(1_300, total - consumed);
            steps = GraphLayoutService.ExtendInChunks(
                steps, commits.GetRange(consumed, take), decos, consumed, take, chunkRows: 250)!;
            consumed += take;
        }

        AssertSameLayout(_engine.Compute(commits, decos, total), steps, compareComplete: false);
    }

    [Fact]
    public void An_extend_whose_start_row_is_stale_places_nothing()
    {
        var commits = History(1_000);
        var start = _engine.Compute(commits.GetRange(0, 300), null, 300);

        var result = GraphLayoutService.ExtendInChunks(
            start, commits.GetRange(100, 400), null, consumed: 100, additionalRows: 400);

        Assert.Null(result);
        Assert.Equal(300, start.State.Rows.Count);
    }

    [Fact]
    public void A_snapshot_taken_before_an_extend_keeps_its_lines_after_it()
    {
        const int first = 600;
        var commits = History(3_000);
        var start = _engine.Compute(commits.GetRange(0, first), null, first);
        var before = Enumerable.Range(0, first)
            .Select(r => (start.Layout.GetRowSegments(r).ToArray(), start.Layout.GetRowElbows(r).ToArray()))
            .ToList();

        var extended = GraphLayoutService.ExtendInChunks(
            start, commits.GetRange(first, 2_000), null, first, 2_000, chunkRows: 300);

        Assert.NotNull(extended);
        Assert.Equal(first, start.Layout.RowCount);
        for (var r = 0; r < first; r++)
        {
            Assert.Equal(before[r].Item1, start.Layout.GetRowSegments(r).ToArray());
            Assert.Equal(before[r].Item2, start.Layout.GetRowElbows(r).ToArray());
        }
    }

    [Fact]
    public void An_incremental_snapshot_searches_and_enumerates_the_rows_it_covers()
    {
        var commits = History(2_000);
        var start = _engine.Compute(commits.GetRange(0, 500), null, 500);
        var extended = GraphLayoutService.ExtendInChunks(
            start, commits.GetRange(500, 1_000), null, 500, 1_000, chunkRows: 400)!;
        var again = GraphLayoutService.ExtendInChunks(
            extended, commits.GetRange(1_500, 500), null, 1_500, 500, chunkRows: 400)!;

        Assert.Equal(1_500, extended.Layout.Commits.Count);
        Assert.Equal(2_000, again.Layout.Commits.Count);
        Assert.Equal(1_500, extended.Layout.Commits.Count); // the older snapshot does not grow
        Assert.Throws<ArgumentOutOfRangeException>(() => extended.Layout.Commits[1_500]);
        Assert.Equal(new[] { 1_234 }, extended.Layout.Commits.Search("commit 1234"));
        Assert.Empty(extended.Layout.Commits.Search("commit 1800"));
        Assert.Contains(1_800, again.Layout.Commits.Search("commit 1800"));
        Assert.Equal(2_000, again.Layout.Commits.Count());
    }

    /// <summary>The longest time a thread asking for <c>lock (state)</c> waited while
    /// <paramref name="extend"/> ran, and how many times it got the lock.</summary>
    private static (TimeSpan MaxWait, int Acquisitions) MeasureReaderWait(
        GraphLayoutState state, Action extend)
    {
        var maxTicks = 0L;
        var acquisitions = 0;
        using var stop = new CancellationTokenSource();
        using var ready = new ManualResetEventSlim();
        var reader = new Thread(() =>
        {
            ready.Set();
            while (!stop.IsCancellationRequested)
            {
                var t0 = Stopwatch.GetTimestamp();
                lock (state) { }
                var waited = Stopwatch.GetTimestamp() - t0;
                if (waited > maxTicks)
                    maxTicks = waited;
                acquisitions++;
                Thread.Yield();
            }
        })
        { IsBackground = true };
        reader.Start();
        ready.Wait();
        try { extend(); }
        finally
        {
            stop.Cancel();
            reader.Join();
        }
        return (Stopwatch.GetElapsedTime(0, maxTicks), acquisitions);
    }

    [Fact]
    public void A_reader_waits_for_one_chunk_not_for_the_whole_extend()
    {
        const int first = 1_000;
        const int rows = 80_000;
        var commits = History(first + rows + 10);
        var slice = commits.GetRange(first, rows + 10);

        TimeSpan chunkedBest = TimeSpan.MaxValue;
        TimeSpan singleShot = TimeSpan.Zero;
        var chunkedAcquisitions = 0;

        // A wait is also stretched by scheduling noise on a loaded machine, so the chunked run is
        // judged by its best of three against a single-shot run measured the same way.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var whole = _engine.Compute(commits.GetRange(0, first), null, first);
            singleShot = MeasureReaderWait(whole.State, () =>
                GraphLayoutService.ExtendInChunks(whole, slice, null, first, rows, chunkRows: int.MaxValue)).MaxWait;

            var chunks = _engine.Compute(commits.GetRange(0, first), null, first);
            var (wait, acquisitions) = MeasureReaderWait(chunks.State, () =>
                GraphLayoutService.ExtendInChunks(chunks, slice, null, first, rows));
            Assert.Equal(first + rows, chunks.State.Rows.Count);
            if (wait < chunkedBest)
            {
                chunkedBest = wait;
                chunkedAcquisitions = acquisitions;
            }
            if (chunkedBest < singleShot / 4)
                break;
        }

        var detail = $"a reader waited {chunkedBest.TotalMilliseconds:0.0} ms during a chunked extend of {rows} rows " +
            $"({chunkedAcquisitions} acquisitions); the same extend in one hold made it wait {singleShot.TotalMilliseconds:0.0} ms";
        Assert.True(chunkedAcquisitions >= 3, detail);
        Assert.True(chunkedBest < singleShot / 4, detail);
    }
}
