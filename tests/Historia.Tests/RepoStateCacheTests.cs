namespace Historia.Tests;

[Trait("tier", "unit")]
public sealed class RepoStateCacheTests : IDisposable
{
    private readonly StateDir _state = new();

    public void Dispose()
    {
        _state.Dispose();
    }

    [Fact]
    public void Round_trip_all_six_entity_kinds_through_save_and_load()
    {
        var settings = new FakeSettingsService(new AppSettings { PersistRepoCache = true });
        var cache = new RepoStateCache(settings);
        const string repoPath = "C:/test-repo";

        var oid1 = new GitObjectId("1111111111111111111111111111111111111111");
        var oid2 = new GitObjectId("2222222222222222222222222222222222222222");
        var oid3 = new GitObjectId("3333333333333333333333333333333333333333");
        var oid4 = new GitObjectId("4444444444444444444444444444444444444444");
        var oid5 = new GitObjectId("5555555555555555555555555555555555555555");
        var oid6 = new GitObjectId("6666666666666666666666666666666666666666");

        var tagTime = DateTimeOffset.UtcNow;
        var stashTime = DateTimeOffset.UtcNow.AddMinutes(-5);

        var branches = new List<BranchInfo>
        {
            new()
            {
                Name = "main",
                CanonicalName = "refs/heads/main",
                IsRemote = false,
                IsCurrent = true,
                TipCommitId = oid1,
                RemoteName = "origin",
                UpstreamName = "refs/remotes/origin/main",
            },
        };

        var tags = new List<TagInfo>
        {
            new()
            {
                Name = "v1.0.0",
                TargetCommitId = oid2,
                IsAnnotated = true,
                Message = "release v1.0",
                TaggerName = "Release Bot",
                TaggerWhen = tagTime,
            },
        };

        var remotes = new List<RemoteInfo>
        {
            new() { Name = "origin", Url = "https://example.com/repo.git", PushUrl = "https://example.com/repo-push.git" },
        };

        var stashes = new List<StashEntry>
        {
            new()
            {
                Index = 0,
                CommitId = oid3,
                Message = "WIP changes",
                AuthorName = "Dev",
                AuthorEmail = "dev@example.com",
                CreatedAt = stashTime,
                BranchName = "main",
                FileCount = 3,
                IncludesUntracked = true,
            },
        };

        var submodules = new List<SubmoduleInfo>
        {
            new()
            {
                Name = "sub",
                Path = "extern/sub",
                Url = "https://example.com/sub.git",
                HeadCommitId = oid4,
                RegisteredCommitId = oid5,
                Status = SubmoduleStatus.Modified,
                BranchTracking = "main",
            },
        };

        var worktrees = new List<WorktreeInfo>
        {
            new()
            {
                Path = "C:/wt/feature",
                Branch = "feature",
                HeadCommitId = oid6,
                IsMainWorktree = false,
                IsLocked = true,
                LockReason = "testing",
                IsPrunable = false,
            },
        };

        cache.SaveRefs(repoPath, branches, tags, remotes);
        cache.SavePowerTools(repoPath, stashes, submodules, worktrees);

        var snapshot = cache.TryLoad(repoPath);
        Assert.NotNull(snapshot);

        // Branches
        var b = Assert.Single(snapshot.Branches!);
        Assert.Equal("main", b.Name);
        Assert.Equal("refs/heads/main", b.CanonicalName);
        Assert.False(b.IsRemote);
        Assert.True(b.IsCurrent);
        Assert.Equal(oid1, b.TipCommitId);
        Assert.Equal("origin", b.RemoteName);
        Assert.Equal("refs/remotes/origin/main", b.UpstreamName);

        // Tags
        var t = Assert.Single(snapshot.Tags!);
        Assert.Equal("v1.0.0", t.Name);
        Assert.Equal(oid2, t.TargetCommitId);
        Assert.True(t.IsAnnotated);
        Assert.Equal("release v1.0", t.Message);
        Assert.Equal("Release Bot", t.TaggerName);
        Assert.Equal(tagTime, t.TaggerWhen);

        // Remotes
        var r = Assert.Single(snapshot.Remotes!);
        Assert.Equal("origin", r.Name);
        Assert.Equal("https://example.com/repo.git", r.Url);
        Assert.Equal("https://example.com/repo-push.git", r.PushUrl);

        // Stashes
        var s = Assert.Single(snapshot.Stashes!);
        Assert.Equal(0, s.Index);
        Assert.Equal(oid3, s.CommitId);
        Assert.Equal("WIP changes", s.Message);
        Assert.Equal("Dev", s.AuthorName);
        Assert.Equal("dev@example.com", s.AuthorEmail);
        Assert.Equal(stashTime, s.CreatedAt);
        Assert.Equal("main", s.BranchName);
        Assert.Equal(3, s.FileCount);
        Assert.True(s.IncludesUntracked);

        // Submodules
        var sm = Assert.Single(snapshot.Submodules!);
        Assert.Equal("sub", sm.Name);
        Assert.Equal("extern/sub", sm.Path);
        Assert.Equal("https://example.com/sub.git", sm.Url);
        Assert.Equal(oid4, sm.HeadCommitId);
        Assert.Equal(oid5, sm.RegisteredCommitId);
        Assert.Equal(SubmoduleStatus.Modified, sm.Status);
        Assert.Equal("main", sm.BranchTracking);

        // Worktrees
        var w = Assert.Single(snapshot.Worktrees!);
        Assert.Equal("C:/wt/feature", w.Path);
        Assert.Equal("feature", w.Branch);
        Assert.Equal(oid6, w.HeadCommitId);
        Assert.False(w.IsMainWorktree);
        Assert.True(w.IsLocked);
        Assert.Equal("testing", w.LockReason);
        Assert.False(w.IsPrunable);
    }

    [Fact]
    public void Branch_ahead_and_behind_counts_are_not_persisted()
    {
        var settings = new FakeSettingsService(new AppSettings { PersistRepoCache = true });
        var cache = new RepoStateCache(settings);
        const string repoPath = "C:/test-repo-ahead-behind";

        var branch = new BranchInfo
        {
            Name = "feature",
            CanonicalName = "refs/heads/feature",
            IsRemote = false,
            IsCurrent = false,
            TipCommitId = new GitObjectId("1111111111111111111111111111111111111111"),
            AheadBy = 7,
            BehindBy = 3,
        };

        cache.SaveRefs(repoPath, [branch], [], []);

        var snapshot = cache.TryLoad(repoPath);
        Assert.NotNull(snapshot);
        var loaded = Assert.Single(snapshot.Branches!);

        Assert.Equal("feature", loaded.Name);
        Assert.Null(loaded.AheadBy);
        Assert.Null(loaded.BehindBy);
    }

    [Fact]
    public void Nullable_and_zero_object_ids_round_trip_cleanly()
    {
        var settings = new FakeSettingsService(new AppSettings { PersistRepoCache = true });
        var cache = new RepoStateCache(settings);
        const string repoPath = "C:/test-repo-oids";

        var branch = new BranchInfo
        {
            Name = "empty-tip",
            CanonicalName = "refs/heads/empty-tip",
            IsRemote = false,
            IsCurrent = false,
            TipCommitId = null,
        };

        var tag = new TagInfo
        {
            Name = "zero-tag",
            TargetCommitId = GitObjectId.Zero,
            IsAnnotated = false,
        };

        var stash = new StashEntry
        {
            Index = 0,
            CommitId = GitObjectId.Zero,
            Message = "stash0",
            AuthorName = "A",
            AuthorEmail = "a@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            BranchName = "main",
            FileCount = 0,
            IncludesUntracked = false,
        };

        var sub = new SubmoduleInfo
        {
            Name = "sub",
            Path = "sub",
            Url = "url",
            HeadCommitId = null,
            RegisteredCommitId = null,
            Status = SubmoduleStatus.Clean,
            BranchTracking = null,
        };

        var wt = new WorktreeInfo
        {
            Path = "wt",
            Branch = "b",
            HeadCommitId = null,
            IsMainWorktree = true,
            IsLocked = false,
            LockReason = null,
            IsPrunable = false,
        };

        cache.SaveRefs(repoPath, [branch], [tag], []);
        cache.SavePowerTools(repoPath, [stash], [sub], [wt]);

        var snapshot = cache.TryLoad(repoPath);
        Assert.NotNull(snapshot);

        Assert.Null(snapshot.Branches![0].TipCommitId);
        Assert.Equal(GitObjectId.Zero, snapshot.Tags![0].TargetCommitId);
        Assert.Equal(GitObjectId.Zero, snapshot.Stashes![0].CommitId);
        Assert.Null(snapshot.Submodules![0].HeadCommitId);
        Assert.Null(snapshot.Submodules![0].RegisteredCommitId);
        Assert.Null(snapshot.Worktrees![0].HeadCommitId);
    }

    [Fact]
    public void Version_mismatch_is_rejected_rather_than_half_read()
    {
        var settings = new FakeSettingsService(new AppSettings { PersistRepoCache = true });
        var cache = new RepoStateCache(settings);
        const string repoPath = "C:/test-repo-version";

        cache.SaveRefs(repoPath, [new BranchInfo { Name = "b1", CanonicalName = "refs/heads/b1", IsRemote = false, IsCurrent = false, TipCommitId = null }], [], []);

        // Corrupt the version on disk
        var files = Directory.GetFiles(AppPaths.CacheDirectory, "repo-refs-*.json.gz");
        var refsFile = Assert.Single(files);

        // Rewrite with version 99
        byte[] uncompressed;
        using (var inFs = File.OpenRead(refsFile))
        using (var inGz = new GZipStream(inFs, CompressionMode.Decompress))
        using (var ms = new MemoryStream())
        {
            inGz.CopyTo(ms);
            uncompressed = ms.ToArray();
        }

        var jsonNode = JsonNode.Parse(uncompressed)!;
        jsonNode["version"] = 99;

        using (var outFs = File.Create(refsFile))
        using (var outGz = new GZipStream(outFs, CompressionLevel.Fastest))
        {
            var bytes = Encoding.UTF8.GetBytes(jsonNode.ToJsonString());
            outGz.Write(bytes, 0, bytes.Length);
        }

        var snapshot = cache.TryLoad(repoPath);
        Assert.Null(snapshot?.Branches);
    }

    [Fact]
    public void Version_1_cache_file_is_cleanly_rejected()
    {
        var settings = new FakeSettingsService(new AppSettings { PersistRepoCache = true });
        var cache = new RepoStateCache(settings);
        const string repoPath = "C:/test-repo-version1";

        cache.SaveRefs(repoPath, [new BranchInfo { Name = "b1", CanonicalName = "refs/heads/b1", IsRemote = false, IsCurrent = false, TipCommitId = null }], [], []);

        var files = Directory.GetFiles(AppPaths.CacheDirectory, "repo-refs-*.json.gz");
        var refsFile = Assert.Single(files);

        byte[] uncompressed;
        using (var inFs = File.OpenRead(refsFile))
        using (var inGz = new GZipStream(inFs, CompressionMode.Decompress))
        using (var ms = new MemoryStream())
        {
            inGz.CopyTo(ms);
            uncompressed = ms.ToArray();
        }

        var jsonNode = JsonNode.Parse(uncompressed)!;
        jsonNode["version"] = 1;

        using (var outFs = File.Create(refsFile))
        using (var outGz = new GZipStream(outFs, CompressionLevel.Fastest))
        {
            var bytes = Encoding.UTF8.GetBytes(jsonNode.ToJsonString());
            outGz.Write(bytes, 0, bytes.Length);
        }

        var snapshot = cache.TryLoad(repoPath);
        Assert.Null(snapshot?.Branches);
    }

    [Fact]
    public void WipeAll_removes_cache_files()
    {
        var settings = new FakeSettingsService(new AppSettings { PersistRepoCache = true });
        var cache = new RepoStateCache(settings);
        const string repoPath = "C:/test-repo-wipe";

        cache.SaveRefs(repoPath, [new BranchInfo { Name = "b1", CanonicalName = "refs/heads/b1", IsRemote = false, IsCurrent = false, TipCommitId = null }], [], []);
        Assert.NotEmpty(Directory.GetFiles(AppPaths.CacheDirectory, "repo-refs-*.json.gz"));

        var removed = cache.WipeAll();
        Assert.True(removed > 0);
        Assert.Empty(Directory.GetFiles(AppPaths.CacheDirectory, "repo-refs-*.json.gz"));

        var snapshot = cache.TryLoad(repoPath);
        Assert.Null(snapshot);
    }

    [Fact]
    public void TryLoadPowerTools_returns_only_the_power_tools_file_and_never_the_refs()
    {
        var settings = new FakeSettingsService(new AppSettings { PersistRepoCache = true });
        var cache = new RepoStateCache(settings);
        const string repoPath = "C:/test-repo-pt-only";
        var stash = new StashEntry
        {
            Index = 0,
            CommitId = new GitObjectId("3333333333333333333333333333333333333333"),
            Message = "WIP",
            AuthorName = "Dev",
            AuthorEmail = "dev@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            BranchName = "main",
        };

        // Refs alone: there is nothing power-tools to load, though TryLoad finds a snapshot.
        cache.SaveRefs(repoPath, [new BranchInfo { Name = "main", CanonicalName = "refs/heads/main", IsRemote = false, IsCurrent = true, TipCommitId = null }], [], []);
        Assert.NotNull(cache.TryLoad(repoPath));
        Assert.Null(cache.TryLoadPowerTools(repoPath));

        // With a power-tools file the refs half of the snapshot stays unread even though it exists.
        cache.SavePowerTools(repoPath, [stash], [], []);
        var snapshot = cache.TryLoadPowerTools(repoPath);
        Assert.NotNull(snapshot);
        Assert.Null(snapshot.Branches);
        Assert.Null(snapshot.Tags);
        Assert.Null(snapshot.Remotes);
        Assert.Equal("WIP", Assert.Single(snapshot.Stashes!).Message);
        Assert.Empty(snapshot.Submodules!);
        Assert.Empty(snapshot.Worktrees!);
        Assert.NotNull(cache.TryLoad(repoPath)!.Branches);

        // A cache that is switched off loads nothing.
        var off = new RepoStateCache(new FakeSettingsService(new AppSettings { PersistRepoCache = false }));
        Assert.Null(off.TryLoadPowerTools(repoPath));
    }

    [Fact]
    public void Cached_remote_HEAD_entry_is_dropped_on_load()
    {
        var settings = new FakeSettingsService(new AppSettings { PersistRepoCache = true });
        var cache = new RepoStateCache(settings);
        const string repoPath = "C:/test-repo-cached-remote-head";

        var oid1 = new GitObjectId("1111111111111111111111111111111111111111");
        var oid2 = new GitObjectId("2222222222222222222222222222222222222222");

        var branches = new List<BranchInfo>
        {
            new()
            {
                Name = "main",
                CanonicalName = "refs/heads/main",
                IsRemote = false,
                IsCurrent = true,
                TipCommitId = oid1,
            },
            new()
            {
                Name = "origin/HEAD",
                CanonicalName = "refs/remotes/origin/HEAD",
                IsRemote = true,
                IsCurrent = false,
                RemoteName = "origin",
                TipCommitId = oid1,
            },
            new()
            {
                Name = "origin/main",
                CanonicalName = "refs/remotes/origin/main",
                IsRemote = true,
                IsCurrent = false,
                RemoteName = "origin",
                TipCommitId = oid2,
            },
        };

        cache.SaveRefs(repoPath, branches, [], []);

        var snapshot = cache.TryLoad(repoPath);
        Assert.NotNull(snapshot);
        Assert.NotNull(snapshot.Branches);
        Assert.Equal(2, snapshot.Branches.Count);
        Assert.DoesNotContain(snapshot.Branches, b => b.CanonicalName == "refs/remotes/origin/HEAD");
        Assert.Contains(snapshot.Branches, b => b.CanonicalName == "refs/heads/main");
        Assert.Contains(snapshot.Branches, b => b.CanonicalName == "refs/remotes/origin/main");
    }
}
