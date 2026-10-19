namespace Historia.Tests;

/// <summary>
/// <see cref="IGraphLayoutService.RunHeadlessCacheAsync"/>
/// brings an INACTIVE tab's on-disk graph cache up to date with no live layout and no lasting
/// entry in the service's own cache table. This also exercises the live reorder trigger —
/// it runs in the headless pipeline, since there's no concurrent foreground reader to protect
/// mid-reorder there.
/// </summary>
[Trait("tier", "git")]
public sealed class GraphHeadlessCacheTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly TestRepositoryBuilder _repo;

    private const int CommitCount = 20;

    public GraphHeadlessCacheTests()
    {
        _repo = TestRepositoryBuilder.FromTemplate("linear-20", b => b.LinearHistory(CommitCount));
    }

    public async ValueTask DisposeAsync()
    {
        _repo.Dispose();
        await _host.DisposeAsync();
    }

    private async Task<GraphLayoutService> DiskBackedServiceAsync()
    {
        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = true });
        return new GraphLayoutService(_host.Settings, _host.Locator);
    }

    private static GraphQuery Q => new() { RefScope = GraphRefScope.AllRefs };

    [Fact]
    public async Task Headless_cache_spools_a_fresh_repo_to_an_exhausted_matching_store()
    {
        var svc = await DiskBackedServiceAsync();
        await using var ctx = _host.Open(_repo.Path);

        await svc.RunHeadlessCacheAsync(ctx, Q, CancellationToken.None);

        var hdrPath = GraphCachePaths.File(_repo.Path, GraphMetaStore.HdrExt);
        Assert.True(File.Exists(hdrPath));

        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(hdrPath));
        Assert.True(doc.RootElement.GetProperty("Exhausted").GetBoolean());
        Assert.Equal(CommitCount, doc.RootElement.GetProperty("RowCount").GetInt32());
    }

    [Fact]
    public async Task Headless_cache_never_creates_a_live_layout_or_RepoCache_entry()
    {
        var svc = await DiskBackedServiceAsync();
        await using var ctx = _host.Open(_repo.Path);

        await svc.RunHeadlessCacheAsync(ctx, Q, CancellationToken.None);

        var cachesField = typeof(GraphLayoutService).GetField("_caches", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var cwt = cachesField.GetValue(svc)!;
        var tryGetValue = cwt.GetType().GetMethod("TryGetValue")!;
        var args = new object?[] { ctx, null };
        var found = (bool)tryGetValue.Invoke(cwt, args)!;

        Assert.False(found, "a headless cache pass must not leave a RepoCache entry for this context");
    }

    [Fact]
    public async Task Headless_cache_is_a_no_op_once_already_exhausted_and_matching()
    {
        var svc = await DiskBackedServiceAsync();
        await using var ctx = _host.Open(_repo.Path);
        await svc.RunHeadlessCacheAsync(ctx, Q, CancellationToken.None);

        var datPath = GraphCachePaths.File(_repo.Path, GraphMetaStore.DatExt);
        var before = File.GetLastWriteTimeUtc(datPath);
        var beforeBytes = await File.ReadAllBytesAsync(datPath);

        await using var ctx2 = _host.Open(_repo.Path);
        await svc.RunHeadlessCacheAsync(ctx2, Q, CancellationToken.None);

        var after = File.GetLastWriteTimeUtc(datPath);
        var afterBytes = await File.ReadAllBytesAsync(datPath);
        Assert.Equal(before, after);
        Assert.Equal(beforeBytes, afterBytes);
    }

    [Fact]
    public async Task A_branch_created_after_the_first_headless_cache_pass_triggers_a_reorder_not_a_full_respill()
    {
        var svc = await DiskBackedServiceAsync();
        await using (var ctx = _host.Open(_repo.Path))
            await svc.RunHeadlessCacheAsync(ctx, Q, CancellationToken.None);

        var datPath = GraphCachePaths.File(_repo.Path, GraphMetaStore.DatExt);
        var beforeLength = new FileInfo(datPath).Length;

        // A fetch/branch-create that moves the walk key: two new commits on a new branch whose
        // parent is the old trunk tip.
        _repo.Checkout("feature", create: true);
        _repo.WriteFile("f.txt", "feature 1\n").Stage().Commit("feature commit 1");
        _repo.WriteFile("f.txt", "feature 2\n").Stage().Commit("feature commit 2");

        await using (var ctx2 = _host.Open(_repo.Path))
            await svc.RunHeadlessCacheAsync(ctx2, Q, CancellationToken.None);

        var afterLength = new FileInfo(datPath).Length;
        var hdrPath = GraphCachePaths.File(_repo.Path, GraphMetaStore.HdrExt);
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(hdrPath));

        // A reorder appends only the new commits' records; a full re-spill would roughly double
        // .meta.dat (rewriting all 20 old records plus the 2 new ones instead of just adding 2).
        Assert.True(doc.RootElement.GetProperty("OrderVersion").GetInt32() > 0, "expected a reorder, not identity order");
        Assert.Equal(CommitCount + 2, doc.RootElement.GetProperty("RowCount").GetInt32());
        Assert.Equal(CommitCount + 2, doc.RootElement.GetProperty("RecordCount").GetInt32());
        var grew = afterLength - beforeLength;
        Assert.True(grew > 0 && grew < (beforeLength / 2), $"expected a small append (~2 new records), grew by {grew} bytes from {beforeLength}");
    }

    // ── The reorder streams in batches, never holding the whole new walk resident ──

    [Fact]
    public async Task A_headless_reorder_never_holds_more_than_one_batch_of_the_new_walk_resident()
    {
        var svc = await DiskBackedServiceAsync();
        await using (var ctx = _host.Open(_repo.Path))
            await svc.RunHeadlessCacheAsync(ctx, Q, CancellationToken.None);

        // Enough new commits that, without streaming, the whole new walk would land in one shot.
        _repo.Checkout("feature", create: true);
        for (var i = 0; i < 12; i++)
            _repo.WriteFile("f.txt", $"feature {i}\n").Stage().Commit($"feature commit {i:D2}");

        GraphLayoutService.TestSpillBatchRowsOverride = 5;
        var maxBatchSeen = 0;
        var batchesSeen = 0;
        GraphLayoutService.TestReorderBatchObserved = size =>
        {
            batchesSeen++;
            maxBatchSeen = Math.Max(maxBatchSeen, size);
        };
        try
        {
            await using var ctx2 = _host.Open(_repo.Path);
            await svc.RunHeadlessCacheAsync(ctx2, Q, CancellationToken.None);
        }
        finally
        {
            GraphLayoutService.TestSpillBatchRowsOverride = 0;
            GraphLayoutService.TestReorderBatchObserved = null;
        }

        Assert.True(batchesSeen >= 2, $"expected multiple batches with a 5-row override over 12 new commits, saw {batchesSeen}");
        Assert.True(maxBatchSeen > 0 && maxBatchSeen <= 5, $"expected every batch <= 5 rows, saw a max of {maxBatchSeen}");

        var hdrPath = GraphCachePaths.File(_repo.Path, GraphMetaStore.HdrExt);
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(hdrPath));
        Assert.Equal(CommitCount + 12, doc.RootElement.GetProperty("RowCount").GetInt32());
    }

    // ── Order-file cleanup and dead-record compaction ──

    [Fact]
    public async Task A_second_reorder_deletes_the_first_reorders_order_file()
    {
        var svc = await DiskBackedServiceAsync();
        await using (var ctx = _host.Open(_repo.Path))
            await svc.RunHeadlessCacheAsync(ctx, Q, CancellationToken.None); // identity order (v0)

        _repo.Checkout("feature1", create: true);
        _repo.WriteFile("f.txt", "feature1\n").Stage().Commit("feature1 commit");
        await using (var ctx2 = _host.Open(_repo.Path))
            await svc.RunHeadlessCacheAsync(ctx2, Q, CancellationToken.None); // reorder -> order.1

        var order1Path = GraphCachePaths.File(_repo.Path, ".meta.order.1");
        Assert.True(File.Exists(order1Path), "expected the first reorder to have written order.1");

        _repo.Checkout("main");
        _repo.Checkout("feature2", create: true);
        _repo.WriteFile("f.txt", "feature2\n").Stage().Commit("feature2 commit");
        await using (var ctx3 = _host.Open(_repo.Path))
            await svc.RunHeadlessCacheAsync(ctx3, Q, CancellationToken.None); // reorder -> order.2

        var order2Path = GraphCachePaths.File(_repo.Path, ".meta.order.2");
        Assert.True(File.Exists(order2Path), "expected the second reorder to have written order.2");
        Assert.False(File.Exists(order1Path), "the superseded order.1 file should be deleted once order.2 is live");
    }

    [Fact]
    public async Task A_store_past_25_percent_dead_records_is_compacted_by_a_full_respill_not_another_reorder()
    {
        var svc = await DiskBackedServiceAsync();
        await using (var ctx = _host.Open(_repo.Path))
            await svc.RunHeadlessCacheAsync(ctx, Q, CancellationToken.None); // identity order, 20/20

        // Force the header into a "past 25% dead" state directly — RecordCount far above
        // RowCount, as if several reorders had each left old records behind uncompacted. The
        // walk key is untouched, so from RunHeadlessCacheAsync's point of view this looks exactly
        // like a real repo whose store has drifted this way over time.
        var hdrPath = GraphCachePaths.File(_repo.Path, GraphMetaStore.HdrExt);
        var probeBefore = GraphMetaStore.ProbeExisting(_repo.Path, includeRemoteBranches: true);
        Assert.True(probeBefore.Ok);
        var hdrJson = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(File.ReadAllBytes(hdrPath))!;
        var inflated = new
        {
            Version = hdrJson["Version"].GetInt32(),
            Fingerprint = hdrJson["Fingerprint"].GetInt64(),
            IncludeRemoteBranches = hdrJson["IncludeRemoteBranches"].GetBoolean(),
            RowCount = hdrJson["RowCount"].GetInt32(),
            Exhausted = hdrJson["Exhausted"].GetBoolean(),
            WalkEngine = hdrJson["WalkEngine"].GetInt32(),
            RecordCount = probeBefore.RowCount * 3, // 3x the row count — well past the 25% threshold
            OrderVersion = 0,
        };
        File.WriteAllBytes(hdrPath, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(inflated));

        // Advance the tip so this is genuinely the "an exhausted store exists at a different key"
        // shape the reorder path would otherwise take — the dead-ratio check must override that.
        _repo.WriteFile("f.txt", "one more\n").Stage().Commit("one more commit");

        await using var ctx2 = _host.Open(_repo.Path);
        await svc.RunHeadlessCacheAsync(ctx2, Q, CancellationToken.None);

        var probeAfter = GraphMetaStore.ProbeExisting(_repo.Path, includeRemoteBranches: true);
        Assert.True(probeAfter.Ok);
        Assert.Equal(CommitCount + 1, probeAfter.RowCount);
        Assert.Equal(probeAfter.RowCount, probeAfter.RecordCount); // compacted: identity order, no dead records
        Assert.Equal(0, probeAfter.OrderVersion); // a full re-spill resets to identity, not another reorder
        Assert.False(File.Exists(GraphCachePaths.File(_repo.Path, ".meta.order.1")), "no order file should survive a full re-spill compaction");
    }

    [Fact]
    public async Task Rows_after_a_headless_reorder_read_back_in_the_new_walks_order()
    {
        var svc = await DiskBackedServiceAsync();
        await using (var ctx = _host.Open(_repo.Path))
            await svc.RunHeadlessCacheAsync(ctx, Q, CancellationToken.None);

        _repo.Checkout("feature", create: true);
        _repo.WriteFile("f.txt", "feature 1\n").Stage().Commit("feature commit 1");

        await using (var ctx2 = _host.Open(_repo.Path))
            await svc.RunHeadlessCacheAsync(ctx2, Q, CancellationToken.None);

        // Ground truth: a plain in-memory compute of the SAME (now-checked-out) history.
        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = false });
        var memSvc = new GraphLayoutService(_host.Settings, _host.Locator);
        await using var memCtx = _host.Open(_repo.Path);
        var expected = await memSvc.ComputeLayoutAsync(memCtx, Q);
        Assert.Equal(CommitCount + 1, expected.Layout.RowCount);

        // The headlessly-reordered disk store: ProbeExisting (not Probe) so this reads
        // through the actual OrderVersion/RecordCount the reorder wrote, not the identity order.
        var probe = GraphMetaStore.ProbeExisting(_repo.Path, includeRemoteBranches: true);
        Assert.True(probe.Ok);
        Assert.True(probe.OrderVersion > 0, "expected the reorder's order file to be in effect");
        using var store = DiskCommitStore.Open(_repo.Path, probe.RowCount, probe.RecordCount, probe.OrderVersion);
        Assert.Equal(CommitCount + 1, store.Count);
        for (var i = 0; i < store.Count; i++)
            Assert.Equal(expected.Layout.Commits[i].Id, store.ReadRaw(i).Id);
    }

    [Fact]
    public async Task Headless_cache_does_nothing_when_disk_backed_metadata_is_off()
    {
        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = false });
        var svc = new GraphLayoutService(_host.Settings, _host.Locator);
        await using var ctx = _host.Open(_repo.Path);

        await svc.RunHeadlessCacheAsync(ctx, Q, CancellationToken.None);

        Assert.False(File.Exists(GraphCachePaths.File(_repo.Path, GraphMetaStore.HdrExt)));
    }

    [Fact]
    public async Task Headless_cache_is_cancellable_before_it_starts_without_touching_disk()
    {
        var svc = await DiskBackedServiceAsync();
        await using var ctx = _host.Open(_repo.Path);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => svc.RunHeadlessCacheAsync(ctx, Q, cts.Token));

        Assert.False(File.Exists(GraphCachePaths.File(_repo.Path, GraphMetaStore.HdrExt)));
    }

    // ── Guard-4 baseline + tips on spool exhaustion ──

    [Fact]
    public async Task A_spool_reaching_exhaustion_writes_meta_tips_even_when_never_reordered()
    {
        var svc = await DiskBackedServiceAsync();
        await using var ctx = _host.Open(_repo.Path);

        await svc.RunHeadlessCacheAsync(ctx, Q, CancellationToken.None);

        var tips = GraphMetaStore.ReadTips(_repo.Path);
        Assert.NotNull(tips);
        Assert.NotEmpty(tips);
    }

    [Fact]
    public async Task A_spool_reaching_exhaustion_records_a_nonzero_full_walk_time()
    {
        var svc = await DiskBackedServiceAsync();
        await using var ctx = _host.Open(_repo.Path);

        await svc.RunHeadlessCacheAsync(ctx, Q, CancellationToken.None);

        var hdrPath = GraphCachePaths.File(_repo.Path, GraphMetaStore.HdrExt);
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(hdrPath));
        Assert.True(doc.RootElement.GetProperty("LastFullWalkMs").GetInt64() > 0);
    }

    // ── The guarded splice's live trigger ──

    [Fact]
    public async Task Splice_setting_off_still_takes_the_ordinary_reorder_path()
    {
        var spliceCalls = 0;
        GraphLayoutService.TestSpliceCommitted = (_, _) => spliceCalls++;
        try
        {
            var svc = await DiskBackedServiceAsync();
            await using (var ctx = _host.Open(_repo.Path))
                await svc.RunHeadlessCacheAsync(ctx, Q, CancellationToken.None);

            _repo.Checkout("feature", create: true);
            _repo.WriteFile("f.txt", "feature 1\n").Stage().Commit("feature commit 1");

            await using (var ctx2 = _host.Open(_repo.Path))
                await svc.RunHeadlessCacheAsync(ctx2, Q, CancellationToken.None);

            Assert.Equal(0, spliceCalls);
            var probe = GraphMetaStore.ProbeExisting(_repo.Path, includeRemoteBranches: true);
            Assert.True(probe.OrderVersion > 0, "expected the ordinary reorder to have run");
        }
        finally
        {
            GraphLayoutService.TestSpliceCommitted = null;
        }
    }

    [Fact]
    public async Task Splice_setting_on_with_lowered_thresholds_takes_the_splice_path_and_matches_a_fresh_walk()
    {
        // The real CLI engine (a live git.exe) is required — GraphMetaSplice's live trigger runs
        // GitLogStream (added commits) and `git rev-list --stdin` (removed commits) for real.
        var locator = _host.Provider.GetRequiredService<IGitExecutableLocator>();
        var cli = _host.Provider.GetRequiredService<IGitCliService>();
        var svc = new GraphLayoutService(_host.Settings, locator, cli);
        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = true, EnableGraphSplice = true });

        GraphLayoutService.TestSpliceMinRowsOverride = CommitCount;
        GraphLayoutService.TestSpliceFullWalkElapsedOverride = TimeSpan.FromMinutes(1);
        var spliceCalls = new List<(int Added, int Removed)>();
        GraphLayoutService.TestSpliceCommitted = (added, removed) => spliceCalls.Add((added, removed));
        try
        {
            await using (var ctx = _host.Open(_repo.Path))
                await svc.RunHeadlessCacheAsync(ctx, Q, CancellationToken.None);

            _repo.Checkout("feature", create: true);
            _repo.WriteFile("f.txt", "feature 1\n").Stage().Commit("feature commit 1");
            _repo.WriteFile("f.txt", "feature 2\n").Stage().Commit("feature commit 2");

            await using (var ctx2 = _host.Open(_repo.Path))
                await svc.RunHeadlessCacheAsync(ctx2, Q, CancellationToken.None);

            var single = Assert.Single(spliceCalls);
            Assert.Equal(2, single.Added);
            Assert.Equal(0, single.Removed);

            // Ground truth: a plain in-memory compute of the SAME (now-checked-out) history,
            // through a separate disk-off service/context — same pattern as the reorder test
            // above, so the splice's own foreground compute never touches the store being asserted.
            await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = false });
            var memSvc = new GraphLayoutService(_host.Settings, locator);
            await using var memCtx = _host.Open(_repo.Path);
            var expected = await memSvc.ComputeLayoutAsync(memCtx, Q);
            Assert.Equal(CommitCount + 2, expected.Layout.RowCount);

            var probe = GraphMetaStore.ProbeExisting(_repo.Path, includeRemoteBranches: true);
            Assert.True(probe.Ok);
            Assert.True(probe.OrderVersion > 0, "expected the splice's order file to be in effect");
            Assert.Equal(CommitCount + 2, probe.RowCount);

            using var store = DiskCommitStore.Open(_repo.Path, probe.RowCount, probe.RecordCount, probe.OrderVersion);
            Assert.Equal(CommitCount + 2, store.Count);
            for (var i = 0; i < store.Count; i++)
                Assert.Equal(expected.Layout.Commits[i].Id, store.ReadRaw(i).Id);
        }
        finally
        {
            GraphLayoutService.TestSpliceMinRowsOverride = null;
            GraphLayoutService.TestSpliceFullWalkElapsedOverride = null;
            GraphLayoutService.TestSpliceCommitted = null;
        }
    }

    [Fact]
    public async Task Splice_rejects_a_tips_file_that_does_not_hash_to_the_store_key_and_the_reorder_stays_correct()
    {
        var locator = _host.Provider.GetRequiredService<IGitExecutableLocator>();
        var cli = _host.Provider.GetRequiredService<IGitCliService>();
        var svc = new GraphLayoutService(_host.Settings, locator, cli);
        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = true, EnableGraphSplice = true });

        GraphLayoutService.TestSpliceMinRowsOverride = CommitCount;
        GraphLayoutService.TestSpliceFullWalkElapsedOverride = TimeSpan.FromMinutes(1);
        var spliceCalls = 0;
        GraphLayoutService.TestSpliceCommitted = (_, _) => spliceCalls++;
        try
        {
            await using (var ctx = _host.Open(_repo.Path))
                await svc.RunHeadlessCacheAsync(ctx, Q, CancellationToken.None);

            // A tips file left behind by a failed write: it names main's parent, not the tip the
            // order was built from. Diffing from it would add main's tip a second time.
            var stale = GitObjectId.Parse(_repo.Git("rev-parse", "main~1").Trim());
            File.WriteAllBytes(GraphCachePaths.File(_repo.Path, GraphMetaStore.TipsExt), stale.AsSpan().ToArray());

            _repo.Checkout("feature", create: true);
            _repo.WriteFile("f.txt", "feature 1\n").Stage().Commit("feature commit 1");
            _repo.WriteFile("f.txt", "feature 2\n").Stage().Commit("feature commit 2");

            await using (var ctx2 = _host.Open(_repo.Path))
                await svc.RunHeadlessCacheAsync(ctx2, Q, CancellationToken.None);

            Assert.Equal(0, spliceCalls);
            var probe = GraphMetaStore.ProbeExisting(_repo.Path, includeRemoteBranches: true);
            Assert.True(probe is { Ok: true, Exhausted: true });
            Assert.Equal(CommitCount + 2, probe.RowCount);
        }
        finally
        {
            GraphLayoutService.TestSpliceMinRowsOverride = null;
            GraphLayoutService.TestSpliceFullWalkElapsedOverride = null;
            GraphLayoutService.TestSpliceCommitted = null;
        }
    }

    [Fact]
    public async Task Splice_rejects_a_fetched_branch_older_than_the_trunk_and_the_reorder_matches_a_fresh_walk()
    {
        var locator = _host.Provider.GetRequiredService<IGitExecutableLocator>();
        var cli = _host.Provider.GetRequiredService<IGitCliService>();
        var svc = new GraphLayoutService(_host.Settings, locator, cli);
        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = true, EnableGraphSplice = true });

        GraphLayoutService.TestSpliceMinRowsOverride = CommitCount;
        GraphLayoutService.TestSpliceFullWalkElapsedOverride = TimeSpan.FromMinutes(1);
        var spliceCalls = new List<(int Added, int Removed)>();
        GraphLayoutService.TestSpliceCommitted = (added, removed) => spliceCalls.Add((added, removed));
        try
        {
            await using (var ctx = _host.Open(_repo.Path))
                await svc.RunHeadlessCacheAsync(ctx, Q, CancellationToken.None);

            var probeBefore = GraphMetaStore.ProbeExisting(_repo.Path, includeRemoteBranches: true);
            Assert.True(probeBefore.Ok);
            DateTimeOffset trunkCommitTime;
            using (var storeBefore = DiskCommitStore.Open(_repo.Path, probeBefore.RowCount, probeBefore.RecordCount, probeBefore.OrderVersion))
            {
                trunkCommitTime = storeBefore[0].CommitWhen;
            }

            _repo.Checkout("feature", create: true);
            _repo.WriteFile("f.txt", "feature 1\n").Stage().CommitAt("feature commit 1", trunkCommitTime.AddMinutes(-10));
            _repo.WriteFile("f.txt", "feature 2\n").Stage().CommitAt("feature commit 2", trunkCommitTime.AddMinutes(-5));

            await using (var ctx2 = _host.Open(_repo.Path))
                await svc.RunHeadlessCacheAsync(ctx2, Q, CancellationToken.None);

            // Guard 6 rejects the splice because the added commits are older than the cached top rows.
            Assert.Empty(spliceCalls);

            // Ground truth: a plain in-memory compute of the SAME (now-checked-out) history,
            // through a separate disk-off service/context — same pattern as above.
            await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = false });
            var memSvc = new GraphLayoutService(_host.Settings, locator);
            await using var memCtx = _host.Open(_repo.Path);
            var expected = await memSvc.ComputeLayoutAsync(memCtx, Q);
            Assert.Equal(CommitCount + 2, expected.Layout.RowCount);

            var probe = GraphMetaStore.ProbeExisting(_repo.Path, includeRemoteBranches: true);
            Assert.True(probe.Ok);
            Assert.True(probe.OrderVersion > 0, "expected the ordinary reorder to have run");
            Assert.Equal(CommitCount + 2, probe.RowCount);

            using var store = DiskCommitStore.Open(_repo.Path, probe.RowCount, probe.RecordCount, probe.OrderVersion);
            Assert.Equal(CommitCount + 2, store.Count);
            for (var i = 0; i < store.Count; i++)
                Assert.Equal(expected.Layout.Commits[i].Id, store.ReadRaw(i).Id);
        }
        finally
        {
            GraphLayoutService.TestSpliceMinRowsOverride = null;
            GraphLayoutService.TestSpliceFullWalkElapsedOverride = null;
            GraphLayoutService.TestSpliceCommitted = null;
        }
    }

    // ── Dead-prefix reclamation ──

    private static void InjectDeadPrefix(string repoPath, int k)
    {
        var datPath = GraphCachePaths.File(repoPath, GraphMetaStore.DatExt);
        var idxPath = GraphCachePaths.File(repoPath, GraphMetaStore.IdxExt);

        var originalDat = File.ReadAllBytes(datPath);
        var junk = new byte[k];
        new Random(42).NextBytes(junk);
        var newDat = new byte[k + originalDat.Length];
        Buffer.BlockCopy(junk, 0, newDat, 0, k);
        Buffer.BlockCopy(originalDat, 0, newDat, k, originalDat.Length);
        File.WriteAllBytes(datPath, newDat);

        var idxBytes = File.ReadAllBytes(idxPath);
        for (var i = 0; i < idxBytes.Length; i += 8)
        {
            var offset = BitConverter.ToInt64(idxBytes, i);
            BitConverter.TryWriteBytes(idxBytes.AsSpan(i, 8), offset + k);
        }
        File.WriteAllBytes(idxPath, idxBytes);
    }

    [Fact]
    public async Task A_moved_store_with_a_large_dead_prefix_is_respooled_instead_of_reordered()
    {
        var svc = await DiskBackedServiceAsync();
        await using (var ctx = _host.Open(_repo.Path))
            await svc.RunHeadlessCacheAsync(ctx, Q, CancellationToken.None);

        var datPath = GraphCachePaths.File(_repo.Path, GraphMetaStore.DatExt);
        var originalDatLen = (int)new FileInfo(datPath).Length;
        var k = originalDatLen / 2; // 50% prefix, >= 25% threshold
        InjectDeadPrefix(_repo.Path, k);

        // Commit one more change so the walk key moves
        _repo.Checkout("main");
        _repo.WriteFile("f.txt", "new commit\n").Stage().Commit("moved walk key");

        var reorderObserved = false;
        GraphLayoutService.TestReorderBatchObserved = _ => reorderObserved = true;
        try
        {
            await using var ctx2 = _host.Open(_repo.Path);
            await svc.RunHeadlessCacheAsync(ctx2, Q, CancellationToken.None);
        }
        finally
        {
            GraphLayoutService.TestReorderBatchObserved = null;
        }

        Assert.False(reorderObserved, "a store with a large dead prefix must be respooled, not reordered");
        var prefix = GraphMetaStore.ProbeDeadPrefix(_repo.Path);
        Assert.Equal(0, prefix.DeadBytes);

        var probe = GraphMetaStore.ProbeExisting(_repo.Path, includeRemoteBranches: true);
        Assert.True(probe.Ok);
        Assert.True(probe.Exhausted);
        Assert.Equal(CommitCount + 1, probe.RowCount);
        Assert.Equal(CommitCount + 1, probe.RecordCount);
    }

    [Fact]
    public async Task A_moved_store_with_a_small_dead_prefix_still_reorders()
    {
        var svc = await DiskBackedServiceAsync();
        await using (var ctx = _host.Open(_repo.Path))
            await svc.RunHeadlessCacheAsync(ctx, Q, CancellationToken.None);

        var datPath = GraphCachePaths.File(_repo.Path, GraphMetaStore.DatExt);
        var originalDatLen = (int)new FileInfo(datPath).Length;
        var k = originalDatLen / 10; // ~9% dead prefix (< 25%)
        InjectDeadPrefix(_repo.Path, k);

        _repo.Checkout("main");
        _repo.WriteFile("f.txt", "new commit\n").Stage().Commit("moved walk key");

        var reorderObserved = false;
        GraphLayoutService.TestReorderBatchObserved = _ => reorderObserved = true;
        try
        {
            await using var ctx2 = _host.Open(_repo.Path);
            await svc.RunHeadlessCacheAsync(ctx2, Q, CancellationToken.None);
        }
        finally
        {
            GraphLayoutService.TestReorderBatchObserved = null;
        }

        Assert.True(reorderObserved, "a store with a small dead prefix should still reorder");
        var prefix = GraphMetaStore.ProbeDeadPrefix(_repo.Path);
        Assert.Equal(k, prefix.DeadBytes);
    }

    [Fact]
    public async Task A_current_store_with_a_dead_prefix_is_left_alone()
    {
        var svc = await DiskBackedServiceAsync();
        await using (var ctx = _host.Open(_repo.Path))
            await svc.RunHeadlessCacheAsync(ctx, Q, CancellationToken.None);

        var datPath = GraphCachePaths.File(_repo.Path, GraphMetaStore.DatExt);
        var originalDatLen = (int)new FileInfo(datPath).Length;
        var k = originalDatLen / 2;
        InjectDeadPrefix(_repo.Path, k);

        var datBytesBefore = await File.ReadAllBytesAsync(datPath);

        await using (var ctx2 = _host.Open(_repo.Path))
            await svc.RunHeadlessCacheAsync(ctx2, Q, CancellationToken.None);

        var datBytesAfter = await File.ReadAllBytesAsync(datPath);
        Assert.Equal(datBytesBefore, datBytesAfter);
    }
}
