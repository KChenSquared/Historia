namespace Historia.Tests;

/// <summary>A header is renamed into place after its data is written but not flushed, so a crash
/// can keep the header and lose the data. Opening such a store must reject it and delete it,
/// so the next walk re-spills instead of serving blank rows as a complete history.</summary>
[Trait("tier", "unit")]
public sealed class GraphMetaTornStoreTests : IDisposable
{
    private readonly StateDir _state = new();
    private const long WalkKey = 0x0bad_5eed_0bad_5eed;
    private const string Repo = @"C:\repos\torn-demo";
    private const int Rows = 12;

    public void Dispose() => _state.Dispose();

    private static GitObjectId Oid(int i) => GitObjectId.Parse((i + 1).ToString("x40"));

    private static RawCommit Commit(int i) => new(
        Oid(i),
        i + 1 < Rows ? [Oid(i + 1)] : [],
        new CommitMetadata
        {
            Id = Oid(i),
            MessageShort = $"commit {i}",
            AuthorName = "Ada Lovelace",
            AuthorEmail = "ada@example.com",
            AuthorWhen = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(-i),
            CommitWhen = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(-i),
            ParentCount = i + 1 < Rows ? 1 : 0,
        });

    private static void WriteExhaustedStore()
    {
        var rows = Enumerable.Range(0, Rows).Select(Commit).ToList();
        var outcome = new GraphMetaWriter(Repo, WalkKey, includeRemoteBranches: true)
            .AppendSpillBatch(rows, from: 0, to: Rows, exhausted: true);
        Assert.Equal(SpillOutcome.Appended, outcome.Outcome);
    }

    private static string FileOf(string ext) => GraphCachePaths.File(Repo, ext);

    [Fact]
    public void A_healthy_store_opens()
    {
        WriteExhaustedStore();
        using var store = GraphMetaStore.TryOpen(Repo, WalkKey, includeRemoteBranches: true, wantCount: Rows);
        Assert.NotNull(store);
        Assert.Equal(Rows, store.Count);
    }

    [Fact]
    public void A_header_naming_more_records_than_the_index_holds_is_discarded()
    {
        WriteExhaustedStore();
        using (var idx = new FileStream(FileOf(GraphMetaStore.IdxExt), FileMode.Open, FileAccess.Write))
            idx.SetLength((Rows - 3) * 8);

        Assert.Null(GraphMetaStore.TryOpen(Repo, WalkKey, includeRemoteBranches: true, wantCount: Rows));
        Assert.False(File.Exists(FileOf(GraphMetaStore.HdrExt)));
        Assert.False(GraphMetaStore.Probe(Repo, WalkKey, includeRemoteBranches: true).Ok);
    }

    [Fact]
    public void A_zero_filled_last_record_is_discarded()
    {
        WriteExhaustedStore();
        var dat = FileOf(GraphMetaStore.DatExt);
        var idxBytes = File.ReadAllBytes(FileOf(GraphMetaStore.IdxExt));
        var lastStart = BitConverter.ToInt64(idxBytes, (Rows - 1) * 8);
        using (var fs = new FileStream(dat, FileMode.Open, FileAccess.Write))
        {
            fs.Position = lastStart;
            fs.Write(new byte[fs.Length - lastStart]);
        }

        Assert.Null(GraphMetaStore.TryOpen(Repo, WalkKey, includeRemoteBranches: true, wantCount: Rows));
        Assert.False(File.Exists(FileOf(GraphMetaStore.HdrExt)));
    }

    [Fact]
    public void An_order_file_shorter_than_its_rows_is_discarded()
    {
        WriteExhaustedStore();
        var writer = new GraphMetaWriter(Repo, WalkKey + 1, includeRemoteBranches: true);
        var order = Enumerable.Range(0, Rows).ToArray();
        Assert.True(writer.TryCommitReorder(WalkKey, true, 0, Rows, 0, order, [Oid(0)]));
        var orderFile = GraphMetaStore.OrderFile(Repo, 1);
        File.WriteAllBytes(orderFile, File.ReadAllBytes(orderFile)[..((Rows - 2) * 4)]);

        Assert.Null(GraphMetaStore.TryOpen(Repo, WalkKey + 1, includeRemoteBranches: true, wantCount: Rows));
        Assert.False(File.Exists(FileOf(GraphMetaStore.HdrExt)));
    }

    [Fact]
    public void A_store_a_writer_is_still_growing_is_not_discarded()
    {
        WriteExhaustedStore();
        // Records appended past the header's count (the header write not yet done) are not torn:
        // the header names fewer records than the files hold.
        var more = Enumerable.Range(0, 2).Select(i => Commit(i)).ToList();
        Assert.True(new GraphMetaWriter(Repo, WalkKey, includeRemoteBranches: true).Append(more, 0, more.Count));

        Assert.False(GraphMetaStore.DiscardIfTorn(Repo, includeRemoteBranches: true));
        using var store = GraphMetaStore.TryOpen(Repo, WalkKey, includeRemoteBranches: true, wantCount: Rows);
        Assert.NotNull(store);
    }

    [Fact]
    public void A_last_record_longer_than_the_read_window_opens()
    {
        var rows = Enumerable.Range(0, Rows).Select(Commit).ToList();
        var last = rows[^1];
        rows[^1] = last with { Metadata = last.Metadata with { MessageShort = new string('m', 100_000) } };
        new GraphMetaWriter(Repo, WalkKey, includeRemoteBranches: true).AppendSpillBatch(rows, from: 0, to: Rows, exhausted: true);

        using var store = GraphMetaStore.TryOpen(Repo, WalkKey, includeRemoteBranches: true, wantCount: Rows);
        Assert.NotNull(store);
        Assert.Equal(Rows, store.Count);
    }
}
