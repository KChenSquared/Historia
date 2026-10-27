namespace Historia.Tests;

[Trait("tier", "unit")]
public sealed class QueuedLineWriterTests : IDisposable
{
    private readonly string _dir = TestScratch.NewDirectory("queued-writer-");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>A stream whose first write blocks until released — a write the disk or a virus
    /// scanner is holding up.</summary>
    internal sealed class StallingStream(Stream inner, ManualResetEventSlim release, ManualResetEventSlim entered) : Stream
    {
        private bool _stalled;
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (!_stalled)
            {
                _stalled = true;
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(30));
            }
            inner.Write(buffer, offset, count);
        }
        public override void Flush() => inner.Flush();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => inner.SetLength(value);
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>Heap growth since <paramref name="before"/>, taking the lowest of a few forced-GC
    /// readings. The heap is process-wide, so tests running in parallel add transient garbage and
    /// live data of their own; a queue that really retains the backlog stays above the limit in
    /// every reading, noise does not.</summary>
    private static long LowestRetained(long before, long limit)
    {
        var lowest = long.MaxValue;
        for (var attempt = 0; attempt < 10 && lowest >= limit; attempt++)
        {
            if (attempt > 0)
                Thread.Sleep(200);
            lowest = Math.Min(lowest, GC.GetTotalMemory(forceFullCollection: true) - before);
        }

        return lowest;
    }

    internal static Stream Append(string path) => new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);

    internal static string[] ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);
        return reader.ReadToEnd().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
    }

    [Fact]
    public void Lines_land_in_enqueue_order_and_flush_waits_for_them()
    {
        var path = Path.Combine(_dir, "a.log");
        using var writer = QueuedLineWriter.OpenPerBatch("test-writer");
        for (var i = 0; i < 500; i++)
            writer.Enqueue(path, $"line {i}");

        Assert.True(writer.Flush(TimeSpan.FromSeconds(10)));
        Assert.Equal(Enumerable.Range(0, 500).Select(i => $"line {i}"), File.ReadAllLines(path));
    }

    [Fact]
    public void A_per_batch_file_is_closed_once_flushed_so_anyone_can_read_it()
    {
        var path = Path.Combine(_dir, "b.log");
        using var writer = QueuedLineWriter.OpenPerBatch("test-writer");
        writer.Enqueue(path, "one");
        Assert.True(writer.Flush(TimeSpan.FromSeconds(10)));

        // File.ReadAllLines shares Read only — it fails while any writer still holds the file.
        Assert.Equal(["one"], File.ReadAllLines(path));
    }

    [Fact]
    public void Enqueue_never_blocks_while_a_write_is_stalled()
    {
        var path = Path.Combine(_dir, "c.log");
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        var writer = QueuedLineWriter.OpenPerBatch("test-writer",
            p => new StallingStream(Append(p), release, entered));
        try
        {
            writer.Enqueue(path, "first");
            writer.Flush(TimeSpan.Zero); // nudge: the write happens at the latest on flush
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "the writer thread never reached the sink");

            // The stall holds until release, so an Enqueue blocked behind it would never return;
            // the budget only has to clear a slow runner's cost for the loop itself.
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < 1000; i++)
                writer.Enqueue(path, $"queued {i}");
            Assert.True(sw.ElapsedMilliseconds < 2000, $"Enqueue blocked behind a stalled write: {sw.ElapsedMilliseconds} ms");

            Assert.False(writer.Flush(TimeSpan.FromMilliseconds(100)), "Flush must time out while the sink is stalled");
        }
        finally
        {
            release.Set();
            writer.Dispose();
        }

        var lines = ReadShared(path);
        Assert.Equal("first", lines[0]);
        Assert.Equal(1001, lines.Length);
    }

    [Fact]
    public void A_full_backlog_drops_lines_and_says_how_many()
    {
        var path = Path.Combine(_dir, "d.log");
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        var writer = QueuedLineWriter.OpenPerBatch("test-writer",
            p => new StallingStream(Append(p), release, entered), maxBacklog: 10);

        writer.Enqueue(path, "first");
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        for (var i = 0; i < 25; i++)
            writer.Enqueue(path, $"queued {i}");
        release.Set();
        Assert.True(writer.Flush(TimeSpan.FromSeconds(10)));
        writer.Dispose();

        var lines = ReadShared(path);
        Assert.Contains(lines, l => l.Contains("15 log lines dropped"));
        Assert.Contains("queued 9", lines);
        Assert.DoesNotContain("queued 10", lines);
    }

    [Fact]
    public void A_line_over_the_cap_is_written_cut_with_a_marker_saying_how_much_was_left_out()
    {
        var path = Path.Combine(_dir, "long.log");
        using var writer = QueuedLineWriter.OpenPerBatch("test-writer");
        writer.Enqueue(path, new string('a', 20_000));
        writer.Enqueue(path, "short");
        Assert.True(writer.Flush(TimeSpan.FromSeconds(10)));

        var lines = ReadShared(path);
        Assert.Equal(2, lines.Length);
        Assert.Equal(new string('a', QueuedLineWriter.MaxLineChars) + "… [4000 more characters not logged]", lines[0]);
        Assert.Equal("short", lines[1]);
    }

    [Fact]
    public void A_line_exactly_at_the_cap_is_written_whole()
    {
        var path = Path.Combine(_dir, "exact.log");
        using var writer = QueuedLineWriter.OpenPerBatch("test-writer");
        writer.Enqueue(path, new string('b', QueuedLineWriter.MaxLineChars));
        Assert.True(writer.Flush(TimeSpan.FromSeconds(10)));

        Assert.Equal([new string('b', QueuedLineWriter.MaxLineChars)], ReadShared(path));
    }

    [Fact]
    public void A_cut_never_leaves_half_a_surrogate_pair()
    {
        var path = Path.Combine(_dir, "surrogate.log");
        // The emoji's high surrogate is the last character that would be kept.
        var line = new string('a', QueuedLineWriter.MaxLineChars - 1) + "\U0001F600" + new string('b', 100);
        using var writer = QueuedLineWriter.OpenPerBatch("test-writer");
        writer.Enqueue(path, line);
        Assert.True(writer.Flush(TimeSpan.FromSeconds(10)));

        var written = Assert.Single(ReadShared(path));
        Assert.DoesNotContain('\uFFFD', written);
        Assert.Equal(new string('a', QueuedLineWriter.MaxLineChars - 1) + $"… [{line.Length - (QueuedLineWriter.MaxLineChars - 1)} more characters not logged]", written);
    }

    [Fact]
    public void A_stalled_disk_does_not_let_huge_lines_pile_up_in_memory()
    {
        var path = Path.Combine(_dir, "memory.log");
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        var writer = QueuedLineWriter.OpenPerBatch("test-writer",
            p => new StallingStream(Append(p), release, entered));
        try
        {
            writer.Enqueue(path, "first");
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "the writer thread never reached the sink");

            var before = GC.GetTotalMemory(forceFullCollection: true);
            // Each line is a fresh megacharacter string: kept whole, 100 of them would hold 200 MB.
            for (var i = 0; i < 100; i++)
                writer.Enqueue(path, new string('x', 1_000_000));
            var retained = LowestRetained(before, 40L * 1024 * 1024);

            Assert.True(retained < 40L * 1024 * 1024, $"the queue retained {retained / (1024 * 1024)} MB behind a stalled write");
        }
        finally
        {
            release.Set();
            writer.Dispose();
        }

        var lines = ReadShared(path);
        Assert.Equal(101, lines.Length);
        Assert.All(lines.Skip(1), l => Assert.Equal(QueuedLineWriter.MaxLineChars + "… [984000 more characters not logged]".Length, l.Length));
    }

    [Fact]
    public void A_stalled_disk_does_not_let_many_line_cap_sized_lines_pile_up_in_memory()
    {
        var path = Path.Combine(_dir, "memory-chars.log");
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        var writer = QueuedLineWriter.OpenPerBatch("test-writer",
            p => new StallingStream(Append(p), release, entered));
        try
        {
            writer.Enqueue(path, "first");
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "the writer thread never reached the sink");

            var before = GC.GetTotalMemory(forceFullCollection: true);
            // 1,000 lines, far fewer than the line cap and each under the line-length cap: only the
            // character backlog can stop them. Kept whole they would hold about 30 MB.
            for (var i = 0; i < 1_000; i++)
                writer.Enqueue(path, new string('z', 15_000));
            var retained = LowestRetained(before, 12L * 1024 * 1024);

            Assert.True(retained < 12L * 1024 * 1024, $"the queue retained {retained / (1024 * 1024)} MB behind a stalled write");
        }
        finally
        {
            release.Set();
            writer.Dispose();
        }
    }

    [Fact]
    public void A_refused_oversized_line_is_counted_by_its_cut_length_and_leaves_no_drift()
    {
        var path = Path.Combine(_dir, "refused.log");
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        var writer = QueuedLineWriter.OpenPerBatch("test-writer",
            p => new StallingStream(Append(p), release, entered));

        writer.Enqueue(path, "first");
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        // Oversized lines cost their cut length (about 16 k each), so 250 fill the character cap.
        for (var i = 0; i < 400; i++)
            writer.Enqueue(path, new string('w', 1_000_000));
        release.Set();
        Assert.True(writer.Flush(TimeSpan.FromSeconds(30)));

        // Everything drained: a counter that drifted would refuse this line or the next ones.
        for (var i = 0; i < 5; i++)
            writer.Enqueue(path, "after " + i);
        Assert.True(writer.Flush(TimeSpan.FromSeconds(30)));
        writer.Dispose();

        var lines = ReadShared(path);
        var cutLength = QueuedLineWriter.MaxLineChars + "… [984000 more characters not logged]".Length;
        var written = lines.Count(l => l.Length == cutLength && l[0] == 'w');
        Assert.InRange(written, 1, 400 - 1);
        Assert.True((long)written * cutLength <= QueuedLineWriter.MaxBacklogChars);
        Assert.Equal(Enumerable.Range(0, 5).Select(i => "after " + i), lines.TakeLast(5));
    }

    [Fact]
    public void A_full_character_backlog_drops_lines_and_the_note_names_characters()
    {
        var path = Path.Combine(_dir, "chars.log");
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        var writer = QueuedLineWriter.OpenPerBatch("test-writer",
            p => new StallingStream(Append(p), release, entered));

        writer.Enqueue(path, "first");
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        const int count = 400;
        const int lineChars = 15_000; // under the line cap, so only the character backlog can refuse them
        for (var i = 0; i < count; i++)
            writer.Enqueue(path, new string('y', lineChars));
        release.Set();
        Assert.True(writer.Flush(TimeSpan.FromSeconds(30)));
        writer.Dispose();

        var lines = ReadShared(path);
        var note = Assert.Single(lines, l => l.Contains("log lines dropped"));
        Assert.Contains($"({QueuedLineWriter.DefaultMaxBacklog} lines or {QueuedLineWriter.MaxBacklogChars} characters)", note);
        var dropped = int.Parse(Regex.Match(note, @"(\d+) log lines dropped").Groups[1].Value);
        var written = lines.Count(l => l.Length == lineChars && l[0] == 'y');

        Assert.True(dropped > 0, "the character backlog never refused a line");
        Assert.Equal(count, written + dropped);
        Assert.True((long)written * lineChars <= QueuedLineWriter.MaxBacklogChars, $"{written} lines of {lineChars} characters passed the backlog cap");
    }

    [Fact]
    public void A_held_open_file_stays_open_until_dispose_which_drains_the_queue()
    {
        var path = Path.Combine(_dir, "e.log");
        var writer = QueuedLineWriter.HoldOpen(path, "test-writer");
        for (var i = 0; i < 100; i++)
            writer.Enqueue($"line {i}");
        Assert.True(writer.Flush(TimeSpan.FromSeconds(10)));

        Assert.Throws<IOException>(() => File.Open(path, FileMode.Open, FileAccess.Write, FileShare.None));

        writer.Enqueue("last");
        writer.Dispose();
        var lines = File.ReadAllLines(path);
        Assert.Equal(101, lines.Length);
        Assert.Equal("last", lines[^1]);
    }

    [Fact]
    public void Lines_after_dispose_are_ignored()
    {
        var path = Path.Combine(_dir, "f.log");
        var writer = QueuedLineWriter.OpenPerBatch("test-writer");
        writer.Dispose();
        writer.Enqueue(path, "late");
        Assert.True(writer.Flush(TimeSpan.FromSeconds(1)));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void A_path_change_between_lines_switches_files()
    {
        var a = Path.Combine(_dir, "g1.log");
        var b = Path.Combine(_dir, "sub", "g2.log");
        using var writer = QueuedLineWriter.OpenPerBatch("test-writer");
        writer.Enqueue(a, "a1");
        writer.Enqueue(b, "b1");
        writer.Enqueue(a, "a2");
        Assert.True(writer.Flush(TimeSpan.FromSeconds(10)));

        Assert.Equal(["a1", "a2"], File.ReadAllLines(a));
        Assert.Equal(["b1"], File.ReadAllLines(b));
    }

    [Fact]
    public async Task RunOnWriterAsync_runs_after_queued_lines_with_sink_closed_and_orders_subsequent_lines()
    {
        var path = Path.Combine(_dir, "h.log");
        using var writer = QueuedLineWriter.HoldOpen(path, "test-hold-open-writer");

        writer.Enqueue("line 1");
        writer.Enqueue("line 2");

        var actionRan = false;
        var task = writer.RunOnWriterAsync(() =>
        {
            // Verify sink is closed and previously queued lines are on disk
            using (var fs = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                using var reader = new StreamReader(fs);
                var content = reader.ReadToEnd();
                Assert.Contains("line 1", content);
                Assert.Contains("line 2", content);
            }
            // Enqueue a line while action is running
            writer.Enqueue("line enqueued during action");
            actionRan = true;
        });

        await task;
        Assert.True(actionRan);

        writer.Enqueue("line 3");
        writer.Dispose();

        var lines = File.ReadAllLines(path);
        Assert.Equal(["line 1", "line 2", "line enqueued during action", "line 3"], lines);
    }

    [Fact]
    public async Task RunOnWriterAsync_propagates_exceptions()
    {
        var path = Path.Combine(_dir, "h-ex.log");
        using var writer = QueuedLineWriter.OpenPerBatch("test-writer");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            writer.RunOnWriterAsync(() => throw new InvalidOperationException("action failed")));

        Assert.Equal("action failed", ex.Message);
    }

    [Fact]
    public async Task RunOnWriterAsync_on_disposed_writer_returns_completed_task()
    {
        var writer = QueuedLineWriter.OpenPerBatch("test-writer");
        writer.Dispose();

        var ran = false;
        var task = writer.RunOnWriterAsync(() => ran = true);

        Assert.True(task.IsCompletedSuccessfully);
        await task;
        Assert.False(ran);
    }
}
