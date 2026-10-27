namespace Historia.Tests;

/// <summary>
/// Many concurrent git processes must not starve the thread pool, and a call made from a thread with
/// a synchronization context (the UI thread) must not start git on that thread. Assertions measure
/// what starvation does — queued work items and probe latency — and carry generous slack, because
/// these tests run on machines that are busy with other work.
/// </summary>
[Trait("tier", "git")]
public class GitCliServicePoolTests
{
    private const int Calls = 40;
    private static readonly TimeSpan AliasSleep = TimeSpan.FromSeconds(3);

    private static async Task<GitExecutableInfo> ResolveGitAsync()
    {
        var locator = new GitExecutableLocator();
        return await locator.ResolveAsync(null);
    }

    [Fact]
    public async Task Forty_concurrent_git_calls_keep_the_thread_pool_free()
    {
        var git = await ResolveGitAsync();
        var cli = new GitCliService(new FixedLocator(git));
        var dir = Path.GetTempPath();

        // The monitor runs on its own thread: it has to keep sampling while the pool is struggling.
        var pending = new List<long>();
        var probes = new List<double>();
        using var stop = new CancellationTokenSource();
        var monitor = new Thread(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                pending.Add(ThreadPool.PendingWorkItemCount);
                var sw = Stopwatch.StartNew();
                Task.Run(() => { }).Wait();
                probes.Add(sw.Elapsed.TotalMilliseconds);
                Thread.Sleep(50);
            }
        })
        { IsBackground = true };
        monitor.Start();

        var total = Stopwatch.StartNew();
        var calls = Enumerable.Range(0, Calls).Select(_ => cli.RunAsync(new GitCliRequest
        {
            Arguments = ["-c", "alias.sl=!sleep 3", "sl"],
            WorkingDirectory = dir,
            Timeout = TimeSpan.FromSeconds(120),
        })).ToArray();
        var results = await Task.WhenAll(calls);
        total.Stop();

        stop.Cancel();
        monitor.Join();

        Assert.All(results, r => Assert.True(r.IsSuccess, r.StandardError));

        // Each call sleeps three seconds. Starting 40 processes is serialised by the OS, so the
        // wall clock gets generous slack; the pool measurements are what tell starvation apart.
        Assert.True(total.Elapsed < AliasSleep + TimeSpan.FromSeconds(15), $"40 calls took {total.Elapsed}");

        Assert.NotEmpty(probes);
        var busySamples = pending.Count(p => p > 0);
        Assert.True(busySamples <= pending.Count * 0.3,
            $"work items were queued in {busySamples} of {pending.Count} samples (max {pending.Max()})");

        probes.Sort();
        var median = probes[probes.Count / 2];
        var p95 = probes[(int)(probes.Count * 0.95)];
        Assert.True(median < 400, $"median pool probe latency {median:F0} ms");
        Assert.True(p95 < 3_000, $"p95 pool probe latency {p95:F0} ms");
    }

    [Fact]
    public async Task A_call_from_a_synchronization_context_thread_starts_git_on_another_thread()
    {
        var git = await ResolveGitAsync();
        var locator = new ThreadRecordingLocator(git);
        var cli = new GitCliService(locator);

        Task<GitCliResult>? call = null;
        var callerThread = 0;
        var caller = new Thread(() =>
        {
            // A context that never runs what is posted to it: a hop through Task.Yield or an await
            // that resumes on the captured context would hang instead of completing.
            SynchronizationContext.SetSynchronizationContext(new DiscardingContext());
            callerThread = Environment.CurrentManagedThreadId;
            call = cli.RunAsync(new GitCliRequest { Arguments = ["version"], WorkingDirectory = Path.GetTempPath() });
        });
        caller.Start();
        caller.Join();

        var result = await call!.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.True(result.IsSuccess, result.StandardError);
        Assert.NotEmpty(locator.Threads);
        Assert.DoesNotContain(callerThread, locator.Threads);
    }

    private sealed class DiscardingContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) { }

        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();
    }

    private sealed class FixedLocator(GitExecutableInfo info) : IGitExecutableLocator
    {
        public GitExecutableInfo Current => info;

        public Task<GitExecutableInfo> ResolveAsync(string? userOverridePath, CancellationToken ct = default)
            => Task.FromResult(info);

        public Task<GitExecutableInfo> WhenResolvedAsync(CancellationToken ct = default)
            => Task.FromResult(info);
    }

    /// <summary>Records the thread of every call the runner makes before and while it starts git.</summary>
    private sealed class ThreadRecordingLocator(GitExecutableInfo info) : IGitExecutableLocator
    {
        private readonly ConcurrentBag<int> _threads = [];

        public IReadOnlyCollection<int> Threads => _threads;

        public GitExecutableInfo Current
        {
            get
            {
                _threads.Add(Environment.CurrentManagedThreadId);
                return info;
            }
        }

        public Task<GitExecutableInfo> ResolveAsync(string? userOverridePath, CancellationToken ct = default)
            => Task.FromResult(info);

        public Task<GitExecutableInfo> WhenResolvedAsync(CancellationToken ct = default)
        {
            _threads.Add(Environment.CurrentManagedThreadId);
            return Task.FromResult(info);
        }
    }
}
