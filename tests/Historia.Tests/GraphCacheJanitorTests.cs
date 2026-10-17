namespace Historia.Tests;

/// <summary><see cref="GraphCacheJanitor"/> keeps the graph cache bounded: least recently used
/// <c>.meta</c> stores go first, open tabs' stores never do, and orphaned temp files and the
/// files of the mode that is off are swept.</summary>
[Trait("tier", "unit")]
public sealed class GraphCacheJanitorTests : IDisposable
{
    private readonly StateDir _state = new();
    private static readonly DateTime Now = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose() => _state.Dispose();

    private static string RepoPath(int i) => $@"C:\repos\janitor-{i}";

    /// <summary>A fake store: a header plus a data file of <paramref name="bytes"/>, last used
    /// <paramref name="daysAgo"/> days before <see cref="Now"/>.</summary>
    private static void MakeStore(int i, int daysAgo, int bytes = 100)
    {
        Directory.CreateDirectory(AppPaths.CacheDirectory);
        var hdr = GraphCachePaths.File(RepoPath(i), GraphMetaStore.HdrExt);
        File.WriteAllText(hdr, "{}");
        File.WriteAllBytes(GraphCachePaths.File(RepoPath(i), GraphMetaStore.DatExt), new byte[bytes]);
        File.SetLastWriteTimeUtc(hdr, Now.AddDays(-daysAgo));
    }

    private static bool StoreExists(int i) => File.Exists(GraphCachePaths.File(RepoPath(i), GraphMetaStore.HdrExt));

    [Fact]
    public void Stores_past_the_count_cap_are_evicted_least_recently_used_first()
    {
        for (var i = 0; i < 5; i++)
            MakeStore(i, daysAgo: i);

        var result = GraphCacheJanitor.Sweep([], metaFromDisk: true, Now, maxStores: 3);

        Assert.Equal(2, result.StoresEvicted);
        Assert.True(StoreExists(0) && StoreExists(1) && StoreExists(2));
        Assert.False(StoreExists(3) || StoreExists(4));
        Assert.Empty(Directory.GetFiles(AppPaths.CacheDirectory, "*.retired"));
    }

    [Fact]
    public void Stores_past_the_size_cap_are_evicted()
    {
        for (var i = 0; i < 4; i++)
            MakeStore(i, daysAgo: i, bytes: 100);

        GraphCacheJanitor.Sweep([], metaFromDisk: true, Now, maxTotalBytes: 210);

        Assert.True(StoreExists(0) && StoreExists(1));
        Assert.False(StoreExists(2) || StoreExists(3));
    }

    [Fact]
    public void An_open_tabs_store_is_kept_however_old()
    {
        for (var i = 0; i < 4; i++)
            MakeStore(i, daysAgo: i);

        GraphCacheJanitor.Sweep([RepoPath(3)], metaFromDisk: true, Now, maxStores: 2);

        Assert.True(StoreExists(3));
        Assert.True(StoreExists(0));
        Assert.False(StoreExists(1) || StoreExists(2));
    }

    [Fact]
    public void The_most_recently_used_store_is_kept_even_past_the_size_cap()
    {
        MakeStore(0, daysAgo: 0, bytes: 500);
        MakeStore(1, daysAgo: 1, bytes: 10);

        GraphCacheJanitor.Sweep([], metaFromDisk: true, Now, maxTotalBytes: 100);

        Assert.True(StoreExists(0));
        Assert.False(StoreExists(1));
    }

    [Fact]
    public void Old_temp_files_and_retired_files_are_swept_and_fresh_temp_files_are_kept()
    {
        MakeStore(0, daysAgo: 0);
        var oldTmp = GraphCachePaths.File(RepoPath(0), ".meta.hdr.0123.tmp");
        var freshTmp = GraphCachePaths.File(RepoPath(0), ".meta.tips.4567.tmp");
        var retired = GraphCachePaths.File(RepoPath(0), ".meta.dat.89ab.retired");
        File.WriteAllText(oldTmp, "x");
        File.WriteAllText(freshTmp, "x");
        File.WriteAllText(retired, "x");
        File.SetLastWriteTimeUtc(oldTmp, Now.AddHours(-2));
        File.SetLastWriteTimeUtc(freshTmp, Now.AddMinutes(-5));

        GraphCacheJanitor.Sweep([], metaFromDisk: true, Now);

        Assert.False(File.Exists(oldTmp));
        Assert.True(File.Exists(freshTmp));
        Assert.False(File.Exists(retired));
        Assert.True(StoreExists(0));
    }

    [Fact]
    public void Memory_mode_deletes_every_meta_store_and_keeps_the_walk_cache()
    {
        MakeStore(0, daysAgo: 0);
        var gz = GraphCachePaths.File(RepoPath(0), ".json.gz");
        File.WriteAllText(gz, "x");

        GraphCacheJanitor.Sweep([RepoPath(0)], metaFromDisk: false, Now);

        Assert.False(StoreExists(0));
        Assert.True(File.Exists(gz));
    }

    [Fact]
    public void Disk_mode_deletes_the_unused_walk_cache_and_leaves_sidebar_files_alone()
    {
        MakeStore(0, daysAgo: 0);
        var gz = GraphCachePaths.File(RepoPath(0), ".json.gz");
        var sidebar = Path.Combine(AppPaths.CacheDirectory, "repo-refs-ABC.json.gz");
        File.WriteAllText(gz, "x");
        File.WriteAllText(sidebar, "x");

        GraphCacheJanitor.Sweep([], metaFromDisk: true, Now);

        Assert.False(File.Exists(gz));
        Assert.True(File.Exists(sidebar));
        Assert.True(StoreExists(0));
    }

    [Fact]
    public void Opening_a_store_refreshes_its_last_use_at_most_hourly()
    {
        const long key = 0x1234;
        var repo = RepoPath(9);
        var rows = new List<RawCommit>
        {
            new(GitObjectId.Parse(new string('a', 40)), [], new CommitMetadata
            {
                Id = GitObjectId.Parse(new string('a', 40)),
                MessageShort = "only",
                AuthorName = "Ada",
                AuthorEmail = "ada@example.com",
                AuthorWhen = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                CommitWhen = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                ParentCount = 0,
            }),
        };
        new GraphMetaWriter(repo, key, includeRemoteBranches: true).AppendSpillBatch(rows, 0, 1, exhausted: true);
        var hdr = GraphCachePaths.File(repo, GraphMetaStore.HdrExt);
        var stale = DateTime.UtcNow.AddDays(-30);
        File.SetLastWriteTimeUtc(hdr, stale);

        using (GraphMetaStore.TryOpen(repo, key, includeRemoteBranches: true, wantCount: 1))
        {
        }
        var stamped = File.GetLastWriteTimeUtc(hdr);
        Assert.True(stamped > stale.AddDays(29));

        using (GraphMetaStore.TryOpen(repo, key, includeRemoteBranches: true, wantCount: 1))
        {
        }
        Assert.Equal(stamped, File.GetLastWriteTimeUtc(hdr));
    }
}
