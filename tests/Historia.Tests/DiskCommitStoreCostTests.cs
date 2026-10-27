namespace Historia.Tests;

/// <summary>The cost of the disk-backed commit store: a warm prefetch does no IO and allocates
/// nothing, growing the offset table is linear in the final size, search compares bytes in place,
/// and the SHA index reads the data file in large sequential windows. Byte bounds are fine here;
/// this is store code, not the render path.</summary>
[Trait("tier", "unit")]
public sealed class DiskCommitStoreCostTests : IDisposable
{
    private readonly StateDir _state = new();
    private const long Fp = 0x0123_4567_89ab_cdef;
    private const string Repo = @"C:\repos\cost";

    public void Dispose() => _state.Dispose();

    private static GitObjectId Oid(int i) => GitObjectId.Parse(i.ToString("x40"));

    private static CommitMetadata Row(int i, string? message = null, string? author = null) => new()
    {
        Id = Oid(i),
        MessageShort = message ?? $"commit number {i} fix widget {(i % 3 == 0 ? "Alpha" : "beta")}",
        AuthorName = author ?? (i % 2 == 0 ? "Ada Lovelace" : "Grace Hopper"),
        AuthorEmail = $"dev{i}@example.com",
        AuthorWhen = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(-8)).AddMinutes(i),
        CommitWhen = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(5.5)).AddMinutes(i + 1),
        ParentCount = i == 0 ? 0 : (i % 7 == 0 ? 2 : 1),
    };

    /// <summary>Rows produced on demand, so a half-million-row fixture is never resident.</summary>
    private sealed class GeneratedRows(int count, Func<int, CommitMetadata> make) : IReadOnlyList<CommitMetadata>
    {
        public int Count => count;
        public CommitMetadata this[int index] => make(index);
        public IEnumerator<CommitMetadata> GetEnumerator()
        {
            for (var i = 0; i < count; i++)
                yield return make(i);
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static GeneratedRows Generated(int count, Func<int, CommitMetadata>? make = null) => new(count, make ?? (i => Row(i)));

    private static GraphMetaWriter WriteStore(IReadOnlyList<CommitMetadata> rows, bool exhausted = true)
    {
        var w = new GraphMetaWriter(Repo, Fp, true);
        w.Reset();
        Assert.True(w.Append(rows, 0, rows.Count));
        Assert.True(w.Commit(rows.Count, exhausted));
        return w;
    }

    private static DiskCommitStore Open(int count) => GraphMetaStore.TryOpen(Repo, Fp, true, count)!;

    [Fact]
    public void Warm_prefetch_of_cached_rows_reads_nothing_and_allocates_nothing()
    {
        WriteStore(Generated(200));
        using var store = Open(200);

        store.Prefetch(20, 79);
        var reads = store.DatReadCount;
        Assert.True(reads > 0);

        for (var i = 0; i < 5; i++)
            store.Prefetch(20, 79);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 200; i++)
            store.Prefetch(20, 79);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(reads, store.DatReadCount);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Prefetch_reads_only_the_span_between_the_first_and_last_missing_rows()
    {
        WriteStore(Generated(300));
        using var store = Open(300);

        store.Prefetch(100, 119);
        store.Prefetch(140, 159);
        var reads = store.DatReadCount;

        // 100..119 and 140..159 are cached; the window 100..159 is missing only 120..139.
        store.Prefetch(100, 159);
        Assert.Equal(reads + 1, store.DatReadCount);

        for (var row = 100; row <= 159; row++)
            Assert.Equal(Oid(row), store[row].Id);
        Assert.Equal(reads + 1, store.DatReadCount);   // the indexer served every row from the cache

        // A window overlapping the cache at one end trims that end and reads only the rest.
        store.Prefetch(150, 179);
        Assert.Equal(reads + 2, store.DatReadCount);
        Assert.Equal(Oid(179), store[179].Id);
    }

    [Fact]
    public void Repeated_SetCount_growth_allocates_in_proportion_to_the_final_size()
    {
        const int Batch = 5000;
        const int Batches = 100;
        const int Final = Batch * (Batches + 1);
        var all = Generated(Final);

        var w = WriteStore(Generated(Batch), exhausted: false);
        using var store = Open(Batch);
        Assert.Equal(Batch, store.Count);

        long allocated = 0;
        for (var batch = 1; batch <= Batches; batch++)
        {
            var total = Batch * (batch + 1);
            Assert.True(w.Append(all, Batch * batch, Batch));
            var before = GC.GetAllocatedBytesForCurrentThread();
            store.SetCount(total);
            allocated += GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(total, store.Count);
            Assert.Equal(total, store.RecordCount);
        }

        // One fresh array per call would copy 8 * Batch * (1 + 2 + ... + 100) bytes, about 200 MB.
        // Capacity doubling sums to a small multiple of the final table, which is 4 MB.
        var finalTableBytes = (long)Final * 8;
        Assert.True(allocated < finalTableBytes * 5, $"allocated {allocated:N0} bytes growing a {finalTableBytes:N0}-byte table");

        foreach (var row in new[] { 0, 1, Batch - 1, Batch, Batch * 37 + 11, Final - 1 })
            Assert.Equal(Oid(row), store[row].Id);
    }

    [Fact]
    public void Rows_decode_exactly_through_the_span_decoder()
    {
        var longText = string.Concat(Enumerable.Repeat("long message segment ÄÖÜ 日本語 ", 40));
        var rows = new List<CommitMetadata>
        {
            Row(0, "", ""),
            Row(1, "plain ascii", "Ada"),
            Row(2, longText, "Zoë Über"),                 // string over 127 and 16383 bytes need a multi-byte length
            Row(3, "emoji 😀 and café", "日本語の作者"),
            Row(4, string.Concat(Enumerable.Repeat("x", 20000)), "Grace"),
            Row(7, "two parents", "Linus"),
        };
        WriteStore(rows);
        using var store = Open(rows.Count);

        for (var i = 0; i < rows.Count; i++)
        {
            var actual = store[i];
            Assert.Equal(rows[i].Id, actual.Id);
            Assert.Equal(rows[i].MessageShort, actual.MessageShort);
            Assert.Equal(rows[i].AuthorName, actual.AuthorName);
            Assert.Equal(rows[i].AuthorEmail, actual.AuthorEmail);
            Assert.Equal(rows[i].AuthorWhen, actual.AuthorWhen);
            Assert.Equal(rows[i].CommitWhen, actual.CommitWhen);
            Assert.Equal(rows[i].ParentCount, actual.ParentCount);
        }

        var raw = store.ReadRawRange(0, rows.Count);
        Assert.Equal(rows.Count, raw.Count);
        Assert.Equal(rows[3].MessageShort, raw[3].Metadata.MessageShort);
    }

    private static string Clean(string s) => s.Replace('\n', ' ').Replace('\r', ' ').Replace(GraphMetaStore.Sep, ' ');

    /// <summary>A reference scan: lower-case the term, decode each sidecar line as a string,
    /// substring on the text after the first separator, prefix on the SHA.</summary>
    private static List<int> ReferenceSearch(IReadOnlyList<CommitMetadata> rows, string term)
    {
        var t = term.Trim().ToLowerInvariant();
        var hits = new List<int>();
        if (t.Length == 0)
            return hits;
        for (var i = 0; i < rows.Count; i++)
        {
            var line = rows[i].Id.ToString() + GraphMetaStore.Sep
                + Clean(rows[i].MessageShort).ToLowerInvariant() + GraphMetaStore.Sep
                + Clean(rows[i].AuthorName).ToLowerInvariant();
            var sep = line.IndexOf(GraphMetaStore.Sep);
            if (line.AsSpan(sep + 1).Contains(t, StringComparison.Ordinal) || line.AsSpan(0, sep).StartsWith(t, StringComparison.Ordinal))
                hits.Add(i);
        }
        return hits;
    }

    [Fact]
    public void Search_matches_the_previous_scan_on_unicode_messages()
    {
        var rows = new List<CommitMetadata>
        {
            Row(0, "Fix ÄÖÜ handling in Über parser", "Zoë Müller"),
            Row(1, "日本語のコミット メッセージ", "山田 太郎"),
            Row(2, "emoji 😀 release notes", "Ada Lovelace"),
            Row(3, "Crème brûlée recipe\nsecond line", "René"),
            Row(4, "tab\tand\u001fseparator", "Grace Hopper"),
            Row(5, "ΑΘΗΝΑ greek capitals", "Σωκράτης"),
            Row(6, "İstanbul dotted capital", "Çağlar"),
            Row(7, "plain ascii message", "Ada Lovelace"),
        };
        for (var i = 8; i < 300; i++)
            rows.Add(Row(i));
        WriteStore(rows);
        using var store = Open(rows.Count);

        var terms = new[]
        {
            "ÄÖÜ", "äöü", "über", "ÜBER parser", "zoë", "müller", "日本語", "コミット メッセージ", "山田",
            "😀", "release 😀", "crème", "brûlée recipe second", "René", "rené", "tab and separator", "ΑΘΗΝΑ", "αθηνα",
            "σωκράτης", "İstanbul", "çağlar", "ada lovelace", "  Alpha  ", "widget beta", "commit number 42",
            Oid(42).ToString(), Oid(7).ToShortString(8), "0000", "no such text anywhere", "ï", "\u001f",
        };
        foreach (var term in terms)
            Assert.Equal(ReferenceSearch(rows, term), store.Search(term));

        Assert.Empty(store.Search("   "));
        Assert.Contains(1, store.Search("日本語"));
    }

    [Fact]
    public void Search_finds_lines_longer_than_its_read_buffer()
    {
        var huge = string.Concat(Enumerable.Repeat("filler ", 60000)) + "needle";   // ~420 KB, over the read buffer
        var rows = new List<CommitMetadata> { Row(0), Row(1, huge, "Ada"), Row(2), Row(3, "needle again", "Grace") };
        WriteStore(rows);
        using var store = Open(rows.Count);

        Assert.Equal(ReferenceSearch(rows, "needle"), store.Search("needle"));
        Assert.Equal(new[] { 1, 3 }, store.Search("needle"));
    }

    [Fact]
    public void BuildShaIndex_maps_every_record_through_a_few_sequential_reads()
    {
        const int Records = 5000;
        // ~450 bytes a record puts .dat past two windows, so the refill path runs.
        var filler = string.Concat(Enumerable.Repeat("padding ", 50));
        WriteStore(Generated(Records, i => Row(i, filler + i)));
        using var store = Open(Records);

        var before = store.DatReadCount;
        var index = store.BuildShaIndex();
        var reads = store.DatReadCount - before;

        Assert.Equal(Records, index.Count);
        for (var rec = 0; rec < Records; rec += 7)
        {
            Assert.True(index.TryGetValue(Oid(rec), out var found));
            Assert.Equal(rec, found);
        }
        Assert.True(index.TryGetValue(Oid(Records - 1), out var last));
        Assert.Equal(Records - 1, last);
        Assert.False(index.TryGetValue(Oid(Records + 5), out _));
        Assert.InRange(reads, 1, 20);   // the SHA index reads .dat in a few large windows
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x1F })]   // fifth byte above 0x0F overflows 32 bits
    [InlineData(new byte[] { 0x80, 0x80, 0x80, 0x80, 0x80 })]   // a sixth byte would be needed
    public void A_corrupt_length_prefix_is_reported_as_invalid_data(byte[] prefix)
    {
        var record = new byte[20 + prefix.Length];
        prefix.CopyTo(record, 20);

        Assert.Throws<InvalidDataException>(() => DiskCommitStore.DecodeFrom(record, 20));
    }
}
