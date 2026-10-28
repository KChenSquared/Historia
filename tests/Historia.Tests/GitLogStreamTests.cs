namespace Historia.Tests;

/// <summary>
/// linux cold-prime plan: <see cref="GitLogStream"/>'s NUL-record framing and per-field
/// parsing, exercised directly with fixture bytes — no real git process involved. See
/// <see cref="GitLogStreamWalkEquivalenceTests"/> for the end-to-end CLI-vs-libgit2 comparison
/// against a real repo.
/// </summary>
[Trait("tier", "unit")]
public sealed class GitLogStreamTests
{
    private const char Sep = '\x1f';

    private static string Sha(int n) => n.ToString("x40");

    private static string Record(string sha, string parents, string authorWhen, string commitWhen, string author, string email, string subject)
        => string.Join(Sep, sha, parents, authorWhen, commitWhen, author, email, subject);

    private static byte[] BuildStream(params string[] records)
    {
        using var ms = new MemoryStream();
        foreach (var r in records)
        {
            var bytes = Encoding.UTF8.GetBytes(r);
            ms.Write(bytes, 0, bytes.Length);
            ms.WriteByte(0); // git log -z record terminator
        }
        return ms.ToArray();
    }

    [Fact]
    public void Parses_a_root_commit_with_no_parents()
    {
        var record = Record(Sha(1), "", "2026-01-01T00:00:00-08:00", "2026-01-01T00:01:00-08:00",
            "Ada Lovelace", "ada@example.com", "root commit");

        var raw = GitLogStream.ParseRecord(record);

        Assert.Equal(GitObjectId.Parse(Sha(1)), raw.Id);
        Assert.Empty(raw.ParentIds);
        Assert.Equal(0, raw.Metadata.ParentCount);
        Assert.Equal("root commit", raw.Metadata.MessageShort);
        Assert.Equal("Ada Lovelace", raw.Metadata.AuthorName);
        Assert.Equal("ada@example.com", raw.Metadata.AuthorEmail);
    }

    [Fact]
    public void Parses_a_merge_commit_with_two_parents()
    {
        var record = Record(Sha(3), $"{Sha(1)} {Sha(2)}", "2026-01-02T00:00:00-08:00", "2026-01-02T00:01:00-08:00",
            "Grace Hopper", "grace@example.com", "merge branch 'feature'");

        var raw = GitLogStream.ParseRecord(record);

        Assert.Equal(2, raw.ParentIds.Count);
        Assert.Equal(GitObjectId.Parse(Sha(1)), raw.ParentIds[0]);
        Assert.Equal(GitObjectId.Parse(Sha(2)), raw.ParentIds[1]);
        Assert.Equal(2, raw.Metadata.ParentCount);
    }

    [Fact]
    public void Parses_non_UTC_author_and_commit_offsets_independently()
    {
        // Author and committer can legitimately sit in different zones (a rebase, a patch
        // applied by someone else) — the two fields must parse independently.
        var record = Record(Sha(4), Sha(1), "2026-03-01T10:15:00+05:30", "2026-03-01T11:00:00+09:00",
            "Dev", "dev@example.com", "tz test");

        var raw = GitLogStream.ParseRecord(record);

        Assert.Equal(TimeSpan.FromMinutes(5 * 60 + 30), raw.Metadata.AuthorWhen.Offset);
        Assert.Equal(TimeSpan.FromHours(9), raw.Metadata.CommitWhen.Offset);
        Assert.Equal(10, raw.Metadata.AuthorWhen.Hour);
        Assert.Equal(11, raw.Metadata.CommitWhen.Hour);
    }

    [Fact]
    public void Parses_a_unicode_subject()
    {
        var subject = "fix(ui): naïve café résumé — 日本語 emoji 🎉";
        var record = Record(Sha(5), Sha(1), "2026-01-01T00:00:00Z", "2026-01-01T00:00:00Z",
            "Dev", "dev@example.com", subject);

        var raw = GitLogStream.ParseRecord(record);

        Assert.Equal(subject, raw.Metadata.MessageShort);
    }

    [Fact]
    public void Rejects_a_record_with_too_few_fields()
    {
        Assert.ThrowsAny<Exception>(() => GitLogStream.ParseRecord("only-two" + Sep + "fields"));
    }

    [Fact]
    public void NulRecordReader_splits_multiple_records_from_one_buffered_read()
    {
        var r1 = Record(Sha(1), "", "2026-01-01T00:00:00Z", "2026-01-01T00:00:00Z", "A", "a@example.com", "first");
        var r2 = Record(Sha(2), Sha(1), "2026-01-02T00:00:00Z", "2026-01-02T00:00:00Z", "B", "b@example.com", "second");
        using var reader = new NulRecordReader(new MemoryStream(BuildStream(r1, r2)));

        Assert.Equal(r1, reader.ReadRecord());
        Assert.Equal(r2, reader.ReadRecord());
        Assert.Null(reader.ReadRecord());
    }

    [Fact]
    public void NulRecordReader_reassembles_a_record_delivered_one_byte_at_a_time()
    {
        // Exercises the "NUL not yet seen in this chunk, keep accumulating" path with the
        // smallest possible reads.
        var r1 = Record(Sha(9), "", "2026-01-01T00:00:00Z", "2026-01-01T00:00:00Z", "A", "a@example.com", "one byte at a time");
        using var reader = new NulRecordReader(new OneByteAtATimeStream(BuildStream(r1)));

        Assert.Equal(r1, reader.ReadRecord());
        Assert.Null(reader.ReadRecord());
    }

    /// <summary>Returns at most one byte per <see cref="Read"/> call, to force
    /// <see cref="NulRecordReader"/> through its multi-chunk accumulation path deterministically.</summary>
    private sealed class OneByteAtATimeStream(byte[] data) : Stream
    {
        private int _pos;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos >= data.Length)
                return 0;
            buffer[offset] = data[_pos++];
            return 1;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>
/// The span-slicing record parser against a straightforward <c>Split</c>-based reference over
/// well-formed and malformed records.
/// </summary>
[Trait("tier", "unit")]
public sealed class GitLogStreamParserEquivalenceTests
{
    private const char Sep = '\x1f';

    private static string Sha(int n) => n.ToString("x40");

    private static RawCommit ReferenceParse(string record)
    {
        var parts = record.Split(Sep);
        if (parts.Length < 7)
            throw new FormatException($"expected 7 fields, got {parts.Length}");

        var id = GitObjectId.Parse(parts[0]);
        var parentIds = parts[1].Length == 0
            ? Array.Empty<GitObjectId>()
            : parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(s => GitObjectId.Parse(s)).ToArray();
        var authorWhen = DateTimeOffset.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
        var commitWhen = DateTimeOffset.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
        var metadata = new CommitMetadata
        {
            Id = id,
            MessageShort = parts[6],
            AuthorName = parts[4],
            AuthorEmail = parts[5],
            AuthorWhen = authorWhen,
            CommitWhen = commitWhen,
            ParentCount = parentIds.Length,
        };
        return new RawCommit(id, parentIds, metadata);
    }

    private static string Rec(string parents, string subject, string author = "Dev", string email = "dev@example.com",
        string authorWhen = "2026-01-01T00:00:00-08:00", string commitWhen = "2026-01-01T00:01:00+09:00") =>
        string.Join(Sep, Sha(7), parents, authorWhen, commitWhen, author, email, subject);

    public static TheoryData<string> WellFormed() => new()
    {
        Rec("", "root"),
        Rec(Sha(1), "one parent"),
        Rec($"{Sha(1)} {Sha(2)}", "merge"),
        Rec($"{Sha(1)} {Sha(2)} {Sha(3)}", "octopus"),
        Rec($"{Sha(1)}  {Sha(2)} ", "extra spaces between and after parents"),
        Rec(" ", "parents field is only a space"),
        Rec(Sha(1), ""),
        Rec(Sha(1), "subject with 日本語 and 🎉 and \"quotes\""),
        Rec(Sha(1), "subject" + Sep + "with a stray separator"),
        Rec(Sha(1), "trailing separator" + Sep),
        Rec(Sha(1), "tz", authorWhen: "2026-03-01T10:15:00+05:30", commitWhen: "2026-03-01T11:00:00Z"),
        Rec(Sha(1), "empty names", author: "", email: ""),
    };

    public static TheoryData<string> Malformed() => new()
    {
        "",
        "only",
        "a" + Sep + "b",
        string.Join(Sep, Sha(1), "", "2026-01-01T00:00:00Z", "2026-01-01T00:00:00Z", "n", "e"),
        Rec("not-a-sha", "bad parent"),
        Rec(Sha(1), "bad date", authorWhen: "yesterday"),
        Rec(Sha(1), "bad commit date", commitWhen: ""),
        string.Join(Sep, "zz", "", "2026-01-01T00:00:00Z", "2026-01-01T00:00:00Z", "n", "e", "bad id"),
    };

    [Theory]
    [MemberData(nameof(WellFormed))]
    public void Parses_well_formed_records_like_the_split_reference(string record)
    {
        var expected = ReferenceParse(record);
        var actual = GitLogStream.ParseRecord(record);

        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.ParentIds, actual.ParentIds);
        Assert.Equal(expected.Metadata.ParentCount, actual.Metadata.ParentCount);
        Assert.Equal(expected.Metadata.MessageShort, actual.Metadata.MessageShort);
        Assert.Equal(expected.Metadata.AuthorName, actual.Metadata.AuthorName);
        Assert.Equal(expected.Metadata.AuthorEmail, actual.Metadata.AuthorEmail);
        Assert.Equal(expected.Metadata.AuthorWhen, actual.Metadata.AuthorWhen);
        Assert.Equal(expected.Metadata.AuthorWhen.Offset, actual.Metadata.AuthorWhen.Offset);
        Assert.Equal(expected.Metadata.CommitWhen, actual.Metadata.CommitWhen);
        Assert.Equal(expected.Metadata.CommitWhen.Offset, actual.Metadata.CommitWhen.Offset);
    }

    [Theory]
    [MemberData(nameof(Malformed))]
    public void Rejects_malformed_records_like_the_split_reference(string record)
    {
        var expected = Record.Exception(() => ReferenceParse(record));
        var actual = Record.Exception(() => GitLogStream.ParseRecord(record));

        Assert.NotNull(expected);
        Assert.NotNull(actual);
        Assert.Equal(expected.GetType(), actual.GetType());
    }

    [Fact]
    public void A_short_record_reports_the_field_count()
    {
        var ex = Assert.Throws<FormatException>(() => GitLogStream.ParseRecord("a" + Sep + "b" + Sep + "c"));

        Assert.Contains("got 3", ex.Message);
    }
}

/// <summary>
/// <see cref="GitLogStream"/> against a real git process: the stdin writer thread, the stderr
/// drain, priority changes and dispose. Each test returns early when git.exe is not discoverable
/// by <see cref="IGitExecutableLocator"/>.
/// </summary>
[Trait("tier", "git")]
public sealed class GitLogStreamProcessTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly TestRepositoryBuilder _repo = TestRepositoryBuilder.FromTemplate(
        "git-log-stream-process", b => b.LinearHistory(40));

    public async ValueTask DisposeAsync()
    {
        _repo.Dispose();
        await _host.DisposeAsync();
    }

    private IGitExecutableLocator Locator => _host.Provider.GetRequiredService<IGitExecutableLocator>();

    private static List<GitObjectId> Drain(GitLogStream stream)
    {
        var ids = new List<GitObjectId>();
        while (stream.MoveNext())
            ids.Add(stream.Current.Id);
        Assert.False(stream.Failed, stream.FailureReason);
        return ids;
    }

    private List<GitObjectId> Walk(IReadOnlyList<string> tips)
    {
        using var stream = GitLogStream.TryStart(Locator, _repo.Path, tips);
        Assert.NotNull(stream);
        return Drain(stream);
    }

    [Fact]
    public void A_large_tip_list_is_written_on_its_own_thread_and_fully_consumed()
    {
        if (!Locator.Current.Found)
            return;

        var expected = Walk([_repo.HeadSha]);
        Assert.Equal(40, expected.Count);

        // Duplicate tips are harmless to git; 20000 lines is far beyond the pipe buffer.
        var tips = Enumerable.Repeat(_repo.HeadSha, 20_000).ToList();
        var started = Stopwatch.StartNew();
        using var stream = GitLogStream.TryStart(Locator, _repo.Path, tips);
        Assert.NotNull(stream);
        var startedIn = started.Elapsed;
        var actual = Drain(stream);

        Assert.Equal(expected, actual);
        Assert.True(startedIn < TimeSpan.FromSeconds(10), $"TryStart took {startedIn}");
    }

    [Fact]
    public void A_short_tip_list_is_consumed()
    {
        if (!Locator.Current.Found)
            return;

        Assert.Equal(40, Walk([_repo.HeadSha, _repo.HeadSha]).Count);
    }

    [Fact]
    public void Priority_changes_do_not_disturb_the_walk()
    {
        if (!Locator.Current.Found)
            return;

        var expected = Walk([_repo.HeadSha]);

        using var stream = GitLogStream.TryStart(Locator, _repo.Path, [_repo.HeadSha]);
        Assert.NotNull(stream);
        var actual = new List<GitObjectId>();
        var toggle = 0;
        while (stream.MoveNext())
        {
            actual.Add(stream.Current.Id);
            stream.SetLowPriority(++toggle % 2 == 1);
            stream.SetLowPriority(toggle % 2 == 1);
        }

        Assert.False(stream.Failed, stream.FailureReason);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void SetLowPriority_lowers_and_restores_the_process_priority()
    {
        if (!Locator.Current.Found)
            return;

        // Enough output to fill the stdout pipe, so git is still alive and blocked on it while the
        // priority is read.
        using var big = TestRepositoryBuilder.FromTemplate("git-log-stream-priority", b => b.LinearHistory(3_000));
        using var stream = GitLogStream.TryStart(Locator, big.Path, [big.HeadSha]);
        Assert.NotNull(stream);
        Assert.True(stream.MoveNext());

        stream.SetLowPriority(true);
        stream.SetLowPriority(true);
        var low = stream.PriorityClassForTesting();
        stream.SetLowPriority(false);
        stream.SetLowPriority(false);
        var restored = stream.PriorityClassForTesting();

        Assert.Equal(ProcessPriorityClass.BelowNormal, low);
        Assert.Equal(ProcessPriorityClass.Normal, restored);
        Assert.Equal(3_000, 1 + Drain(stream).Count);
    }

    [Fact]
    public void SetLowPriority_after_dispose_is_ignored()
    {
        if (!Locator.Current.Found)
            return;

        var stream = GitLogStream.TryStart(Locator, _repo.Path, [_repo.HeadSha]);
        Assert.NotNull(stream);
        stream.Dispose();

        stream.SetLowPriority(true);
        stream.SetLowPriority(false);
    }

    [Fact]
    public void Dispose_of_an_unconsumed_stream_returns_promptly()
    {
        if (!Locator.Current.Found)
            return;

        var tips = Enumerable.Repeat(_repo.HeadSha, 5_000).ToList();
        var stream = GitLogStream.TryStart(Locator, _repo.Path, tips);
        Assert.NotNull(stream);

        var disposing = Stopwatch.StartNew();
        stream.Dispose();
        disposing.Stop();

        // Dispose waits at most one second each for stderr and the stdin writer; the rest is slack
        // for a loaded machine.
        Assert.True(disposing.Elapsed < TimeSpan.FromSeconds(8), $"Dispose took {disposing.Elapsed}");
        Assert.Equal(0, GitLogStream.LiveCountForTesting(_repo.Path));
    }

    [Fact]
    public void A_failing_walk_reports_stderr()
    {
        if (!Locator.Current.Found)
            return;

        using var stream = GitLogStream.TryStart(Locator, _repo.Path, [new string('0', 39) + "1"]);
        Assert.NotNull(stream);

        while (stream.MoveNext())
        {
        }

        Assert.True(stream.Failed);
        Assert.Contains("git log exited", stream.FailureReason);
    }
}

/// <summary>
/// Disposing a running <see cref="GitLogStream"/> must leave no live git process, even when the
/// shared <see cref="ProcessKiller"/> threads are busy. Serial: the test floods the killer's small
/// fixed pool with kills, which would delay every other test's kills if it ran alongside them.
/// </summary>
[Trait("tier", "git")]
[Collection("serial")]
public sealed class GitLogStreamDisposeKillTests
{
    [WindowsFact]
    public async Task Dispose_of_a_running_stream_kills_git_even_when_the_killer_is_backed_up()
    {
        var locator = new GitExecutableLocator();
        await locator.ResolveAsync(null);
        if (!locator.Current.Found)
            return;

        // Enough output to fill the stdout pipe, so git is alive and blocked on it.
        using var big = TestRepositoryBuilder.FromTemplate("git-log-stream-priority", b => b.LinearHistory(3_000));
        var stream = GitLogStream.TryStart(locator, big.Path, [big.HeadSha]);
        Assert.NotNull(stream);
        Assert.True(stream.MoveNext());
        using var git = Process.GetProcessById(stream.ProcessIdForTesting);
        Assert.False(git.HasExited);

        // Queue a pile of other kills ahead of the stream's, so its kill reaches the killer thread
        // late — disposing the Process before that kill runs would leave git running.
        var decoys = Enumerable.Range(0, 60).Select(_ => Process.Start(new ProcessStartInfo(
            "ping.exe", ["-n", "120", "127.0.0.1"])
        { CreateNoWindow = true, UseShellExecute = false })!).ToList();
        var decoyKills = new List<Task>();
        try
        {
            foreach (var decoy in decoys)
                decoyKills.Add(ProcessKiller.KillAsync(decoy));

            stream.Dispose();

            Assert.True(git.WaitForExit(15_000), "git was still running after the stream was disposed");
        }
        finally
        {
            // Leave the shared killer idle for whatever test runs next.
            await Task.WhenAll(decoyKills).WaitAsync(TimeSpan.FromSeconds(60));
            foreach (var decoy in decoys)
            {
                try { if (!decoy.HasExited) decoy.Kill(); } catch { /* best effort */ }
                decoy.Dispose();
            }
        }
    }
}
