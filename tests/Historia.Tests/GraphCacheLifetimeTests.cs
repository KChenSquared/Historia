namespace Historia.Tests;

/// <summary>
/// The graph's caches expire on their own: an unread walk-cache entry leaves memory after its TTL,
/// and the search sidecar the metadata writer produces keeps its exact bytes while it lower-cases
/// through a reused buffer.
/// </summary>
[Trait("tier", "unit")]
public sealed class GraphCacheLifetimeTests : IDisposable
{
    private readonly StateDir _state = new();
    private const string Repo = @"C:\repos\cache-lifetime-demo";
    private static readonly GraphQuery Query = new() { RefScope = GraphRefScope.AllRefs };
    private static readonly Dictionary<GitObjectId, IReadOnlyList<GraphDecorationInput>> NoDecos = [];

    public void Dispose() => _state.Dispose();

    private static GitObjectId Oid(int i) => new(i.ToString("x40"));

    private static CommitMetadata Meta(int i, string message, string author) => new()
    {
        Id = Oid(i),
        MessageShort = message,
        AuthorName = author,
        AuthorEmail = $"dev{i}@example.com",
        AuthorWhen = DateTimeOffset.UnixEpoch.AddMinutes(i),
        CommitWhen = DateTimeOffset.UnixEpoch.AddMinutes(i),
        ParentCount = i == 0 ? 0 : 1,
    };

    private static RawCommit Raw(CommitMetadata m, int i) => new(
        m.Id, i == 0 ? Array.Empty<GitObjectId>() : new[] { Oid(i - 1) }, m);

    [Fact]
    public async Task An_unread_walk_cache_entry_is_removed_after_its_ttl()
    {
        var commits = Enumerable.Range(0, 5).Select(i => Raw(Meta(i, $"c{i}", "Dev"), i)).ToList();
        new GraphWalkCache().Save(Repo, Query, fingerprint: 7, exhausted: true, commits, NoDecos);

        var cache = new GraphWalkCache(TimeSpan.FromMilliseconds(100));
        Assert.NotNull(cache.TryLoadStale(Repo, Query));
        Assert.Equal(1, cache.CachedReadCount);

        // The entry is removed at TTL + 1 s; allow generous slack for a loaded machine.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (cache.CachedReadCount != 0 && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        Assert.Equal(0, cache.CachedReadCount);
    }

    /// <summary>What the writer produced before it lower-cased through a reused buffer: clean the
    /// text, then lower-case it into a new string.</summary>
    private static string ExpectedField(string s)
        => s.Replace('\n', ' ').Replace('\r', ' ').Replace(GraphMetaStore.Sep, ' ').ToLowerInvariant();

    [Fact]
    public void The_search_sidecar_bytes_match_cleaned_lower_cased_text()
    {
        var sep = GraphMetaStore.Sep;
        var longMessage = "Long " + new string('X', 700) + " Ünïcode ÄÖÜ ΑΒΓ Ǆ";
        var texts = new (string Message, string Author)[]
        {
            ("Fix The BUG", "Ada Lovelace"),
            ("", ""),
            ("line one\nLine Two\r\nLINE THREE", "Grace\rHopper"),
            ($"has{sep}separator{sep}Inside", $"Author{sep}Name"),
            (longMessage, "İstanbul Dev ß"),
            ("short again", "Ünï Çödé"),
            ("emoji \U0001F600 Smile \U00010400 Deseret", "Dev \U0001D400"),
            (new string('a', 256), new string('B', 257)),
        };
        var metas = texts.Select((t, i) => Meta(i, t.Message, t.Author)).ToList();
        var rows = metas.Select(Raw).ToList();

        var writer = new GraphMetaWriter(Repo, fingerprint: 0x1234, includeRemoteBranches: true);
        Assert.True(writer.Reset());
        Assert.True(writer.Append(rows, 0, rows.Count));
        Assert.True(writer.Commit(rows.Count, exhausted: true));

        var expected = new StringBuilder();
        foreach (var m in metas)
            expected.Append(m.Id.ToString()).Append(sep).Append(ExpectedField(m.MessageShort))
                .Append(sep).Append(ExpectedField(m.AuthorName)).Append('\n');

        var actual = File.ReadAllBytes(GraphCachePaths.File(Repo, GraphMetaStore.SearchExt));
        Assert.Equal(new UTF8Encoding(false).GetBytes(expected.ToString()), actual);
    }
}
