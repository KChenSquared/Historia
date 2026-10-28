namespace Historia.Tests;

/// <summary>
/// <see cref="GitLogStream.TryStart"/> with a path filter: only commits touching the path are
/// yielded, parents are rewritten to the nearest shown ancestor (so the graph never points at a
/// row that isn't there), and the pathspec is matched literally. Skipped when git.exe isn't
/// discoverable by <see cref="IGitExecutableLocator"/>.
/// </summary>
[Trait("tier", "git")]
public sealed class GitLogStreamPathFilterTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly List<TestRepositoryBuilder> _repos = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var r in _repos)
            r.Dispose();
        await _host.DisposeAsync();
    }

    private TestRepositoryBuilder NewRepo()
    {
        var repo = TestRepositoryBuilder.Create();
        _repos.Add(repo);
        return repo;
    }

    private IGitExecutableLocator Locator => _host.Provider.GetRequiredService<IGitExecutableLocator>();

    private List<RawCommit> Walk(TestRepositoryBuilder repo, string? pathFilter)
    {
        using var stream = GitLogStream.TryStart(Locator, repo.Path, [repo.HeadSha], pathFilter: pathFilter);
        Assert.NotNull(stream);
        var rows = new List<RawCommit>();
        while (stream.MoveNext())
            rows.Add(stream.Current);
        Assert.False(stream.Failed);
        return rows;
    }

    private static GitObjectId Id(string sha) => GitObjectId.Parse(sha);

    [Fact]
    public void Path_filter_yields_only_commits_touching_the_path()
    {
        if (!Locator.Current.Found)
            return;

        var repo = NewRepo();
        var a1 = repo.WriteFile("a.txt", "1\n").Stage().Commit("a 1");
        repo.WriteFile("b.txt", "1\n").Stage().Commit("b 1");
        var a2 = repo.WriteFile("a.txt", "2\n").Stage().Commit("a 2");
        repo.WriteFile("b.txt", "2\n").Stage().Commit("b 2");

        var rows = Walk(repo, "a.txt");

        Assert.Equal([Id(a2), Id(a1)], rows.Select(r => r.Id));
    }

    [Fact]
    public void Path_filter_rewrites_parents_to_the_nearest_shown_commit()
    {
        if (!Locator.Current.Found)
            return;

        var repo = NewRepo();
        var p1 = repo.WriteFile("p.txt", "1\n").Stage().Commit("p 1");
        repo.WriteFile("q.txt", "1\n").Stage().Commit("q 1");
        repo.WriteFile("q.txt", "2\n").Stage().Commit("q 2");
        var p2 = repo.WriteFile("p.txt", "2\n").Stage().Commit("p 2");

        var rows = Walk(repo, "p.txt");

        var shown = rows.Select(r => r.Id).ToHashSet();
        foreach (var row in rows)
            Assert.All(row.ParentIds, parent => Assert.Contains(parent, shown));
        var top = Assert.Single(rows, r => r.Id == Id(p2));
        Assert.Equal([Id(p1)], top.ParentIds);
        Assert.Equal(1, top.Metadata.ParentCount);
        Assert.Empty(Assert.Single(rows, r => r.Id == Id(p1)).ParentIds);
    }

    [Fact]
    public void Path_filter_through_a_merge_keeps_both_rewritten_parents()
    {
        if (!Locator.Current.Found)
            return;

        var repo = NewRepo();
        repo.WriteFile("dir/base.txt", "1\n").Stage().Commit("base");
        repo.Git("checkout", "-b", "side");
        var side = repo.WriteFile("dir/side.txt", "1\n").Stage().Commit("side touches dir");
        repo.WriteFile("other.txt", "1\n").Stage().Commit("side elsewhere");
        repo.Git("checkout", "main");
        var trunk = repo.WriteFile("dir/trunk.txt", "1\n").Stage().Commit("trunk touches dir");
        repo.WriteFile("elsewhere.txt", "1\n").Stage().Commit("trunk elsewhere");
        repo.Git("merge", "--no-ff", "-m", "merge side", "side");
        var merge = repo.HeadSha;

        var rows = Walk(repo, "dir");

        var shown = rows.Select(r => r.Id).ToHashSet();
        foreach (var row in rows)
            Assert.All(row.ParentIds, parent => Assert.Contains(parent, shown));
        var mergeRow = Assert.Single(rows, r => r.Id == Id(merge));
        Assert.Equal(new HashSet<GitObjectId> { Id(side), Id(trunk) }, mergeRow.ParentIds.ToHashSet());
    }

    [Fact]
    public void Path_filter_is_literal()
    {
        if (!Locator.Current.Found)
            return;

        // "[b]" is a glob character class that would match "ab.txt"; a literal pathspec matches
        // only the file actually named "a[b].txt". ('*' and '?' are not legal Windows filenames.)
        var repo = NewRepo();
        var literal = repo.WriteFile("a[b].txt", "1\n").Stage().Commit("literal file");
        repo.WriteFile("ab.txt", "1\n").Stage().Commit("glob look-alike");
        repo.WriteFile("ab.txt", "2\n").Stage().Commit("glob look-alike again");

        var rows = Walk(repo, "a[b].txt");

        Assert.Equal([Id(literal)], rows.Select(r => r.Id));
    }

    [Fact]
    public void No_path_filter_keeps_the_unfiltered_walk_identical()
    {
        if (!Locator.Current.Found)
            return;

        var repo = NewRepo();
        repo.WriteFile("a.txt", "1\n").Stage().Commit("a 1");
        repo.Git("checkout", "-b", "side");
        repo.WriteFile("b.txt", "1\n").Stage().Commit("side");
        repo.Git("checkout", "main");
        repo.WriteFile("a.txt", "2\n").Stage().Commit("a 2");
        repo.Git("merge", "--no-ff", "-m", "merge side", "side");

        var withNull = Walk(repo, null);
        var withEmpty = Walk(repo, "");

        using var bare = GitLogStream.TryStart(Locator, repo.Path, [repo.HeadSha]);
        Assert.NotNull(bare);
        var baseline = new List<RawCommit>();
        while (bare.MoveNext())
            baseline.Add(bare.Current);

        Assert.Equal(4, baseline.Count);
        Assert.Equal(baseline.Select(r => r.Id), withNull.Select(r => r.Id));
        Assert.Equal(baseline.Select(r => r.Id), withEmpty.Select(r => r.Id));
        Assert.Equal(
            baseline.Select(r => string.Join(',', r.ParentIds)),
            withNull.Select(r => string.Join(',', r.ParentIds)));
    }

    [Fact]
    public void Priority_changes_do_not_disturb_the_walk()
    {
        if (!Locator.Current.Found)
            return;

        var repo = NewRepo();
        for (var i = 0; i < 12; i++)
        {
            repo.WriteFile("a.txt", $"{i}\n").Stage().Commit($"a {i}");
            repo.WriteFile($"other{i}.txt", "1\n").Stage().Commit($"other {i}");
        }

        var expected = Walk(repo, "a.txt").Select(r => r.Id).ToList();

        using var stream = GitLogStream.TryStart(Locator, repo.Path, [repo.HeadSha], pathFilter: "a.txt");
        Assert.NotNull(stream);
        var actual = new List<GitObjectId>();
        var toggle = 0;
        while (stream.MoveNext())
        {
            actual.Add(stream.Current.Id);
            stream.SetLowPriority(++toggle % 2 == 1);
        }

        Assert.False(stream.Failed, stream.FailureReason);
        Assert.Equal(12, actual.Count);
        Assert.Equal(expected, actual);
    }
}
