namespace Historia.Tests;

[Trait("tier", "unit")]
public class CmdrQueueServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 15, 0, 0, TimeSpan.Zero);

    private readonly StateDir _state = new();
    private readonly RecordingLog _log = new();

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingLog : IAppLog
    {
        public List<string> Warnings { get; } = [];
        public void Trace(string category, string message) { }
        public void Debug(string category, string message) { }
        public void Info(string category, string message) { }
        public void Warn(string category, string message, Exception? ex = null) => Warnings.Add(message);
        public void Error(string category, string message, Exception? ex = null) => Warnings.Add(message);
        public IDisposable Operation(string category, string message) => new NoScope();

        private sealed class NoScope : IDisposable
        {
            public void Dispose() { }
        }
    }

    private CmdrQueueService Create(int expiryHours = 5, IUiDispatcher? dispatcher = null) =>
        new(new FakeSettingsService(new AppSettings { CmdrQueueExpiryHours = expiryHours }),
            dispatcher ?? new ImmediateUiDispatcher(), _log, new FixedTime(Now));

    private static CmdrQueueItem Item(string note, TimeSpan age = default) => new()
    {
        CreatedAt = Now - age,
        RepoRoot = @"D:\repos\historia",
        RepoName = "historia",
        Target = ControlVerb.Show,
        Rev = "90236c7a1b2c3d4e5f60718293a4b5c6d7e8f901",
        Note = note,
    };

    [Fact]
    public async Task Items_round_trip_through_the_file()
    {
        var full = new CmdrQueueItem
        {
            CreatedAt = Now,
            RepoRoot = @"D:\repos\historia",
            RepoName = "historia",
            Target = ControlVerb.Compare,
            Rev = "1111111111111111111111111111111111111111",
            OtherRev = "2222222222222222222222222222222222222222",
            Paths = ["src/a b.cs", "docs/ü.md"],
            Line = 42,
            Staged = true,
            CommitMessage = "subject\n\nbody",
            CommitSubject = "subject",
            StagedPaths = ["src/a b.cs"],
            Note = "look here",
        };
        await using (var first = Create())
        {
            first.Enqueue(full);
            first.Enqueue(Item("second"));
            first.MarkSeen(full.Id);
        }

        await using var second = Create();

        Assert.Equal(2, second.Items.Count);
        var loaded = second.Items[0];
        Assert.Equal(full.Id, loaded.Id);
        Assert.Equal(full.CreatedAt, loaded.CreatedAt);
        Assert.Equal(full.RepoRoot, loaded.RepoRoot);
        Assert.Equal(full.RepoName, loaded.RepoName);
        Assert.Equal(ControlVerb.Compare, loaded.Target);
        Assert.Equal(full.Rev, loaded.Rev);
        Assert.Equal(full.OtherRev, loaded.OtherRev);
        Assert.Equal(full.Paths, loaded.Paths);
        Assert.Equal(42, loaded.Line);
        Assert.True(loaded.Staged);
        Assert.Equal(full.CommitMessage, loaded.CommitMessage);
        Assert.Equal(full.CommitSubject, loaded.CommitSubject);
        Assert.Equal(full.StagedPaths, loaded.StagedPaths);
        Assert.Equal("look here", loaded.Note);
        Assert.True(loaded.Seen);
        Assert.Equal("second", second.Items[1].Note);
        Assert.Equal(1, second.UnseenCount);
    }

    [Fact]
    public async Task The_file_is_written_only_after_the_first_change()
    {
        await using (Create()) { }
        Assert.False(File.Exists(AppPaths.CmdrQueueFile));

        await using (var svc = Create())
            svc.Enqueue(Item("one"));
        Assert.True(File.Exists(AppPaths.CmdrQueueFile));
        Assert.Equal(Path.Combine(AppPaths.MachineDirectory, "cmdr-queue.json"), AppPaths.CmdrQueueFile);

        var onDisk = JsonNode.Parse(File.ReadAllText(AppPaths.CmdrQueueFile))!.AsObject();
        Assert.Equal(1, onDisk["schemaVersion"]!.GetValue<int>());
        Assert.Single(onDisk["items"]!.AsArray());
    }

    [Theory]
    [InlineData(1, new[] { "30 min" })]
    [InlineData(5, new[] { "2 h", "30 min" })]
    public async Task Loading_removes_items_older_than_the_setting(int expiryHours, string[] kept)
    {
        await using (var seed = Create(expiryHours: 168))
        {
            seed.Enqueue(Item("6 h", TimeSpan.FromHours(6)));
            seed.Enqueue(Item("2 h", TimeSpan.FromHours(2)));
            seed.Enqueue(Item("30 min", TimeSpan.FromMinutes(30)));
        }

        await using (var svc = Create(expiryHours))
            Assert.Equal(kept, svc.Items.Select(i => i.Note));

        // The purge is persisted, so a longer expiry cannot bring the removed items back.
        await using var reloaded = Create(expiryHours: 168);
        Assert.Equal(kept, reloaded.Items.Select(i => i.Note));
    }

    [Fact]
    public async Task An_out_of_range_expiry_is_clamped_when_the_store_loads()
    {
        await using (var seed = Create(expiryHours: 168))
        {
            seed.Enqueue(Item("2 h", TimeSpan.FromHours(2)));
            seed.Enqueue(Item("30 min", TimeSpan.FromMinutes(30)));
        }

        await using var svc = Create(expiryHours: 0);

        Assert.Equal(["30 min"], svc.Items.Select(i => i.Note));
    }

    [Fact]
    public async Task Nothing_is_purged_while_running()
    {
        await using var svc = Create(expiryHours: 1);

        svc.Enqueue(Item("old", TimeSpan.FromDays(3)));

        Assert.Single(svc.Items);
    }

    [Fact]
    public async Task The_cap_drops_the_oldest_items()
    {
        await using var svc = Create();
        var items = Enumerable.Range(1, CmdrQueueService.MaxItems + 1).Select(i => Item($"#{i}")).ToList();

        foreach (var item in items)
            svc.Enqueue(item);

        Assert.Equal(CmdrQueueService.MaxItems, svc.Items.Count);
        Assert.Equal("#2", svc.Items[0].Note);
        Assert.Equal($"#{CmdrQueueService.MaxItems + 1}", svc.Items[^1].Note);
    }

    [Fact]
    public async Task Mark_seen_lowers_the_unseen_count_and_persists()
    {
        var a = Item("a");
        var b = Item("b");
        await using (var svc = Create())
        {
            svc.Enqueue(a);
            svc.Enqueue(b);
            Assert.Equal(2, svc.UnseenCount);

            svc.MarkSeen(a.Id);

            Assert.Equal(1, svc.UnseenCount);
            Assert.True(svc.Items[0].Seen);
        }

        await using var reloaded = Create();
        Assert.Equal(1, reloaded.UnseenCount);
        Assert.True(reloaded.Items.Single(i => i.Id == a.Id).Seen);
    }

    [Fact]
    public async Task Remove_persists()
    {
        var a = Item("a");
        var b = Item("b");
        await using (var svc = Create())
        {
            svc.Enqueue(a);
            svc.Enqueue(b);

            svc.Remove(a.Id);

            Assert.Equal([b.Id], svc.Items.Select(i => i.Id));
        }

        await using var reloaded = Create();
        Assert.Equal([b.Id], reloaded.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task Unknown_ids_change_nothing()
    {
        var dispatcher = new RecordingUiDispatcher();
        await using var svc = Create(dispatcher: dispatcher);
        svc.Enqueue(Item("a"));
        var posts = dispatcher.PostCount;

        svc.MarkSeen("missing");
        svc.Remove("missing");

        Assert.Equal(posts, dispatcher.PostCount);
        Assert.Single(svc.Items);
    }

    [Fact]
    public async Task A_corrupt_file_loads_empty_and_is_left_on_disk()
    {
        Directory.CreateDirectory(AppPaths.MachineDirectory);
        const string corrupt = "{ \"schemaVersion\": 1, \"items\": [ { \"target\": ";
        File.WriteAllText(AppPaths.CmdrQueueFile, corrupt);

        await using (var svc = Create())
        {
            Assert.Empty(svc.Items);
            Assert.Equal(0, svc.UnseenCount);
        }

        Assert.Equal(corrupt, File.ReadAllText(AppPaths.CmdrQueueFile));
        Assert.NotEmpty(_log.Warnings);
    }

    [Fact]
    public async Task A_newer_schema_loads_empty_and_is_left_on_disk()
    {
        Directory.CreateDirectory(AppPaths.MachineDirectory);
        const string future = "{ \"schemaVersion\": 2, \"items\": [] }";
        File.WriteAllText(AppPaths.CmdrQueueFile, future);

        await using (var svc = Create())
            Assert.Empty(svc.Items);

        Assert.Equal(future, File.ReadAllText(AppPaths.CmdrQueueFile));
        Assert.NotEmpty(_log.Warnings);
    }

    [Fact]
    public async Task Changed_is_raised_through_the_dispatcher()
    {
        var dispatcher = new RecordingUiDispatcher();
        await using var svc = Create(dispatcher: dispatcher);
        var raised = 0;
        svc.Changed += (_, _) => raised++;
        var a = Item("a");

        svc.Enqueue(a);
        svc.MarkSeen(a.Id);
        svc.Remove(a.Id);

        Assert.Equal(0, raised);
        Assert.Equal(3, dispatcher.PostCount);
        dispatcher.DrainPosted();
        Assert.Equal(3, raised);
    }

    [Fact]
    public async Task Changed_runs_after_the_lock_is_released()
    {
        await using var svc = Create();
        var otherThreadRead = false;
        svc.Changed += (_, _) =>
            otherThreadRead = Task.Run(() => svc.Items.Count + svc.UnseenCount).Wait(TimeSpan.FromSeconds(5));

        svc.Enqueue(Item("a"));

        Assert.True(otherThreadRead);
    }

    public void Dispose() => _state.Dispose();
}
