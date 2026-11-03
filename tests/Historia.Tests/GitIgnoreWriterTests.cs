namespace Historia.Tests;

/// <summary>
/// <see cref="GitIgnoreWriter"/> writes anchored, escaped lines into the root <c>.gitignore</c>:
/// the line text, how the file is appended to, and (against real git) that each tricky name is
/// matched by its own line and by no sibling.
/// </summary>
[Trait("tier", "git")]
public sealed class GitIgnoreWriterTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly TestRepositoryBuilder _repo = TestRepositoryBuilder.Create();

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        _repo.Dispose();
    }

    private string IgnorePath => System.IO.Path.Combine(_repo.Path, ".gitignore");

    private async Task<int> AppendAsync(params string[] paths)
    {
        await using var ctx = _host.Open(_repo.Path);
        return await GitIgnoreWriter.AppendAsync(ctx, paths, CancellationToken.None);
    }

    [Fact]
    public void EntryFor_anchors_at_root()
    {
        Assert.Equal("/a.txt", GitIgnoreWriter.EntryFor("a.txt"));
        Assert.Equal("/src/deep/a.txt", GitIgnoreWriter.EntryFor("src/deep/a.txt"));
    }

    [Fact]
    public void EntryFor_escapes_leading_hash_and_bang()
    {
        Assert.Equal("/\\#notes.txt", GitIgnoreWriter.EntryFor("#notes.txt"));
        Assert.Equal("/\\!keep.txt", GitIgnoreWriter.EntryFor("!keep.txt"));
        Assert.Equal("/dir/#notes.txt", GitIgnoreWriter.EntryFor("dir/#notes.txt"));
        Assert.Equal("/a#b!c", GitIgnoreWriter.EntryFor("a#b!c"));
    }

    [Fact]
    public void EntryFor_escapes_glob_characters()
    {
        Assert.Equal("/a\\*b", GitIgnoreWriter.EntryFor("a*b"));
        Assert.Equal("/a\\?b", GitIgnoreWriter.EntryFor("a?b"));
        Assert.Equal("/a\\[b]", GitIgnoreWriter.EntryFor("a[b]"));
        Assert.Equal("/a\\\\b", GitIgnoreWriter.EntryFor("a\\b"));
    }

    [Fact]
    public void EntryFor_escapes_trailing_space()
    {
        Assert.Equal("/a\\ ", GitIgnoreWriter.EntryFor("a "));
        Assert.Equal("/a \\ ", GitIgnoreWriter.EntryFor("a  "));
        Assert.Equal("/a b", GitIgnoreWriter.EntryFor("a b"));
    }

    [Fact]
    public void EntryFor_rejects_line_breaks()
    {
        Assert.False(GitIgnoreWriter.CanWrite("a\nb"));
        Assert.False(GitIgnoreWriter.CanWrite("a\rb"));
        Assert.True(GitIgnoreWriter.CanWrite("a b"));
        Assert.Throws<ArgumentException>(() => GitIgnoreWriter.EntryFor("a\nb"));
    }

    [Fact]
    public async Task Append_creates_file_with_lf()
    {
        var added = await AppendAsync("a.txt", "dir/b.txt");

        Assert.Equal(2, added);
        var bytes = await File.ReadAllBytesAsync(IgnorePath);
        Assert.Equal("/a.txt\n/dir/b.txt\n", Encoding.UTF8.GetString(bytes));
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
    }

    [Fact]
    public async Task Append_keeps_crlf_and_adds_missing_final_newline()
    {
        await File.WriteAllTextAsync(IgnorePath, "bin/\r\nobj/");

        var added = await AppendAsync("a.txt");

        Assert.Equal(1, added);
        Assert.Equal("bin/\r\nobj/\r\n/a.txt\r\n", await File.ReadAllTextAsync(IgnorePath));
    }

    [Fact]
    public async Task Append_skips_existing_lines()
    {
        await File.WriteAllTextAsync(IgnorePath, "/a.txt\r\n");

        var added = await AppendAsync("a.txt", "b.txt", "b.txt");

        Assert.Equal(1, added);
        Assert.Equal("/a.txt\r\n/b.txt\r\n", await File.ReadAllTextAsync(IgnorePath));
        Assert.Equal(0, await AppendAsync("a.txt", "b.txt"));
    }

    [Fact]
    public async Task Append_skips_path_with_newline()
    {
        var added = await AppendAsync("bad\nname.txt", "ok.txt");

        Assert.Equal(1, added);
        Assert.Equal("/ok.txt\n", await File.ReadAllTextAsync(IgnorePath));

        Assert.Equal(0, await AppendAsync("only\rbad.txt"));
    }

    [Fact]
    public async Task Append_refuses_a_bare_repository()
    {
        using var bare = TestRepositoryBuilder.Create();
        TestGit.RunChecked(bare.Path, ["config", "core.bare", "true"]);
        await using var ctx = _host.Open(bare.Path);
        Assert.True(ctx.IsBare);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => GitIgnoreWriter.AppendAsync(ctx, ["a.txt"], CancellationToken.None));
    }

    // A backslash is not covered here: Windows git reads it in a path argument as a separator, and
    // such a name cannot exist in a Windows work tree.
    public static TheoryData<string, string> TrickyNames => new()
    {
        { "plain.txt", "other/plain.txt" },
        { "sub/plain.txt", "x/sub/plain.txt" },
        { "#hash.txt", "hash.txt" },
        { "!bang.txt", "bang.txt" },
        { "a*b.txt", "aXb.txt" },
        { "a?b.txt", "aXb.txt" },
        { "a[xy]b.txt", "axb.txt" },
        { "a[xy]b.txt", "a[xy]b.txt.bak" },
        { "has space.txt", "has.txt" },
        { "trailing ", "trailing" },
        { "dir/trailing  ", "dir/trailing" },
        { "star/*", "star/x" },
    };

    [Theory]
    [MemberData(nameof(TrickyNames))]
    public async Task Entries_match_with_git_check_ignore(string path, string sibling)
    {
        await AppendAsync(path);

        Assert.Equal(0, CheckIgnore(path).ExitCode);
        Assert.NotEqual(0, CheckIgnore(sibling).ExitCode);
    }

    private TestGitResult CheckIgnore(string path)
        => TestGit.Run(_repo.Path, ["check-ignore", "--no-index", "-q", "--", path]);
}
