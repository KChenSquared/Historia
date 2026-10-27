namespace Historia.Tests;

[Trait("tier", "unit")]
public sealed class RecentRepositoryServiceTests : IAsyncDisposable
{
    private readonly StateDir _state = new();
    private readonly MiniHost _host = new();

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        _state.Dispose();
    }

    private sealed class ScriptedPaths
    {
        public const int Missing = 0, Hangs = 1, Present = 2;

        private volatile int _mode = Present;
        private int _calls;
        private int _checkThreadId = -1;

        public ManualResetEventSlim Gate { get; } = new(false);

        public int Mode
        {
            get => _mode;
            set => _mode = value;
        }

        public int Calls => Volatile.Read(ref _calls);

        public int CheckThreadId => Volatile.Read(ref _checkThreadId);

        public bool Exists(string path)
        {
            Interlocked.Increment(ref _calls);
            Interlocked.CompareExchange(ref _checkThreadId, Environment.CurrentManagedThreadId, -1);
            switch (_mode)
            {
                case Hangs:
                    Gate.Wait(TimeSpan.FromSeconds(30));
                    return true;
                case Missing:
                    return false;
                default:
                    return true;
            }
        }
    }

    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    private RecentRepositoryService NewService(ScriptedPaths paths, TimeSpan? checkTimeout = null, TimeSpan? recheckAfter = null)
        => new(_host.Settings, paths.Exists, checkTimeout ?? TimeSpan.FromSeconds(30), recheckAfter ?? TimeSpan.FromHours(1));

    [Fact]
    public async Task A_hung_path_check_delays_neither_reading_nor_adding()
    {
        var paths = new ScriptedPaths { Mode = ScriptedPaths.Hangs };
        await using var svc = NewService(paths);
        try
        {
            await Task.Run(() => svc.AddAsync(Path.Combine(_state.Path, "a"), "a")).WaitAsync(Generous);
            await Task.Run(() => svc.AddAsync(Path.Combine(_state.Path, "b"), "b")).WaitAsync(Generous);

            var items = await Task.Run(() => svc.Items).WaitAsync(Generous);

            Assert.Equal(["b", "a"], items.Select(i => i.DisplayName));
            Assert.All(items, i => Assert.False(i.IsStale));
        }
        finally
        {
            paths.Gate.Set();
        }
    }

    [Fact]
    public async Task Reading_items_never_runs_the_folder_check_on_the_calling_thread()
    {
        var paths = new ScriptedPaths { Mode = ScriptedPaths.Missing };
        await using var svc = NewService(paths);
        await svc.AddAsync(Path.Combine(_state.Path, "a"), "a");
        await svc.RefreshStalenessAsync().WaitAsync(Generous);
        var callsAfterRefresh = paths.Calls;

        for (var i = 0; i < 50; i++)
            _ = svc.Items;

        Assert.Equal(callsAfterRefresh, paths.Calls);
        Assert.NotEqual(Environment.CurrentManagedThreadId, paths.CheckThreadId);
    }

    [Fact]
    public async Task Staleness_arrives_through_the_changed_event_after_the_check()
    {
        var paths = new ScriptedPaths { Mode = ScriptedPaths.Missing };
        await using var svc = NewService(paths);
        var raised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        svc.Changed += (_, _) =>
        {
            if (svc.Items.Any(i => i.IsStale))
                raised.TrySetResult();
        };

        await svc.AddAsync(Path.Combine(_state.Path, "gone"), "gone");
        await raised.Task.WaitAsync(Generous);

        Assert.True(Assert.Single(svc.Items).IsStale);
    }

    [Fact]
    public async Task A_folder_that_comes_back_stops_being_stale_on_the_next_check()
    {
        var paths = new ScriptedPaths { Mode = ScriptedPaths.Missing };
        await using var svc = NewService(paths);
        await svc.AddAsync(Path.Combine(_state.Path, "a"), "a");
        await svc.RefreshStalenessAsync().WaitAsync(Generous);
        Assert.True(Assert.Single(svc.Items).IsStale);

        paths.Mode = ScriptedPaths.Present;
        await svc.RefreshStalenessAsync().WaitAsync(Generous);

        Assert.False(Assert.Single(svc.Items).IsStale);
    }

    [Fact]
    public async Task A_check_that_times_out_keeps_the_previous_value()
    {
        var paths = new ScriptedPaths { Mode = ScriptedPaths.Missing };
        await using var svc = NewService(paths, checkTimeout: TimeSpan.FromMilliseconds(200));
        await svc.AddAsync(Path.Combine(_state.Path, "a"), "a");
        await svc.RefreshStalenessAsync().WaitAsync(Generous);
        Assert.True(Assert.Single(svc.Items).IsStale);
        var changes = 0;
        svc.Changed += (_, _) => Interlocked.Increment(ref changes);

        try
        {
            paths.Mode = ScriptedPaths.Hangs;
            await svc.RefreshStalenessAsync().WaitAsync(Generous);
            Assert.True(Assert.Single(svc.Items).IsStale);

            // The first check is still hanging; a second refresh does not start another one for the path.
            var callsWhileHung = paths.Calls;
            await svc.RefreshStalenessAsync().WaitAsync(Generous);
            Assert.Equal(callsWhileHung, paths.Calls);
            Assert.True(Assert.Single(svc.Items).IsStale);
            Assert.Equal(0, changes);
        }
        finally
        {
            paths.Gate.Set();
        }

        // Once the hung check returns, the path is checked again and the new answer is used.
        paths.Mode = ScriptedPaths.Present;
        var deadline = DateTime.UtcNow + Generous;
        while (svc.Items[0].IsStale && DateTime.UtcNow < deadline)
        {
            await svc.RefreshStalenessAsync().WaitAsync(Generous);
            await Task.Delay(20);
        }

        Assert.False(Assert.Single(svc.Items).IsStale);
    }

    [Fact]
    public async Task Reading_items_after_the_recheck_age_starts_a_new_check()
    {
        var paths = new ScriptedPaths { Mode = ScriptedPaths.Present };
        await using var svc = NewService(paths, recheckAfter: TimeSpan.Zero);
        await svc.AddAsync(Path.Combine(_state.Path, "a"), "a");
        await svc.RefreshStalenessAsync().WaitAsync(Generous);
        var raised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        svc.Changed += (_, _) => raised.TrySetResult();

        paths.Mode = ScriptedPaths.Missing;
        Assert.False(Assert.Single(svc.Items).IsStale);
        await raised.Task.WaitAsync(Generous);

        Assert.True(Assert.Single(svc.Items).IsStale);
    }

    [Fact]
    public async Task Racing_adds_leave_the_newest_list_pending()
    {
        const int rounds = 1_000;
        var svc = new RecentRepositoryService(_host.Settings);
        var writer = typeof(RecentRepositoryService).GetField("_writer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(svc)!;
        var pendingOf = typeof(AtomicFileWriter).GetProperty("PendingContentForTesting", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var pathA = Path.Combine(_state.Path, "repo-a");
        var pathB = Path.Combine(_state.Path, "repo-b");

        var inversions = 0;
        using var barrier = new Barrier(2);
        for (var r = 0; r < rounds; r++)
        {
            async Task Add(string path)
            {
                barrier.SignalAndWait();
                await svc.AddAsync(path, Path.GetFileName(path));
            }
            await Task.WhenAll(Task.Run(() => Add(pathA)), Task.Run(() => Add(pathB)));

            // A debounce that fired between rounds already put a snapshot on disk; nothing to compare.
            if (pendingOf.GetValue(writer) is not string pending)
                continue;
            using var doc = JsonDocument.Parse(pending);
            var queued = doc.RootElement.GetProperty("items").EnumerateArray()
                .Select(e => e.GetProperty("path").GetString()).ToList();
            if (!queued.SequenceEqual(svc.Items.Select(i => i.Path)))
                inversions++;
        }

        Assert.Equal(0, inversions);
    }
}
