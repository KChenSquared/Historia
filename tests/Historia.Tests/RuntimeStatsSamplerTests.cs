namespace Historia.Tests;

[Trait("tier", "unit")]
public sealed class RuntimeStatsSamplerTests
{
    private static readonly TimeSpan Idle = TimeSpan.FromHours(1);

    private static RuntimeStatsSampler.Snapshot Quiet(int threads = 8, long pending = 0, int gen0 = 10, TimeSpan? pause = null)
        => new(threads, pending, pause ?? TimeSpan.FromMilliseconds(5), gen0, 3, 1,
            MemoryLoadBytes: 6L * 1024 * 1024 * 1024,
            HighMemoryLoadThresholdBytes: 12L * 1024 * 1024 * 1024,
            HeapBytes: 200L * 1024 * 1024);

    private sealed class Harness : IDisposable
    {
        private readonly object _gate = new();
        public List<string> Lines { get; } = new();
        public bool Enabled { get; set; } = true;
        public RuntimeStatsSampler.Snapshot Current { get; set; } = Quiet();
        public RuntimeStatsSampler Sampler { get; }

        public Harness(TimeSpan? interval = null, bool enabled = true)
        {
            Enabled = enabled;
            Sampler = new RuntimeStatsSampler(
                line => { lock (_gate) Lines.Add(line); },
                () => Current,
                () => Enabled,
                interval ?? Idle,
                followLoggingSwitch: false);
        }

        public string[] Snapshot()
        {
            lock (_gate)
                return Lines.ToArray();
        }

        public void Dispose() => Sampler.Dispose();
    }

    [Fact]
    public void Starting_writes_the_gc_mode_line_once()
    {
        using var h = new Harness();

        var line = Assert.Single(h.Snapshot());
        Assert.StartsWith("runtime: gc server=", line, StringComparison.Ordinal);
        Assert.Contains("latency=", line, StringComparison.Ordinal);
        Assert.Contains($"processors={Environment.ProcessorCount}", line, StringComparison.Ordinal);

        h.Enabled = false;
        h.Sampler.Reconcile();
        h.Enabled = true;
        h.Sampler.Reconcile();

        Assert.Single(h.Snapshot());
    }

    [Fact]
    public void A_sample_with_nothing_changed_and_nothing_queued_writes_nothing()
    {
        using var h = new Harness();

        h.Sampler.Sample();
        h.Sampler.Sample();

        Assert.Single(h.Snapshot());
    }

    [Fact]
    public void A_sample_writes_deltas_when_the_gc_ran()
    {
        using var h = new Harness();
        h.Current = Quiet(gen0: 14, pause: TimeSpan.FromMilliseconds(47.5));

        h.Sampler.Sample();

        var line = h.Snapshot()[^1];
        Assert.StartsWith("runtime: pool threads=8 pending=0 ", line, StringComparison.Ordinal);
        Assert.Contains("gcPause+=42.5ms", line, StringComparison.Ordinal);
        Assert.Contains("gen0+=4 gen1+=0 gen2+=0", line, StringComparison.Ordinal);
        Assert.Contains("load=50%", line, StringComparison.Ordinal);
        Assert.Contains("heap=200MB", line, StringComparison.Ordinal);

        h.Sampler.Sample();
        Assert.Equal(2, h.Snapshot().Length);
    }

    [Fact]
    public void A_sample_writes_when_the_pool_thread_count_moved()
    {
        using var h = new Harness();
        h.Current = Quiet(threads: 12);

        h.Sampler.Sample();

        Assert.Contains("threads=12", h.Snapshot()[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void A_sample_writes_every_time_work_is_queued_on_the_pool()
    {
        using var h = new Harness();
        h.Current = Quiet(pending: 3);

        h.Sampler.Sample();
        h.Sampler.Sample();

        var lines = h.Snapshot();
        Assert.Equal(3, lines.Length);
        Assert.Contains("pending=3", lines[1], StringComparison.Ordinal);
        Assert.Contains("pending=3", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public void Disabled_logging_creates_no_timer_and_writes_nothing()
    {
        using var h = new Harness(enabled: false);

        Assert.False(h.Sampler.IsRunning);
        h.Sampler.Sample();
        Assert.Empty(h.Snapshot());

        h.Enabled = true;
        h.Sampler.Reconcile();
        Assert.True(h.Sampler.IsRunning);

        h.Enabled = false;
        h.Sampler.Reconcile();
        Assert.False(h.Sampler.IsRunning);
    }

    [Fact]
    public void Running_changes_are_raised_when_the_timer_starts_and_stops()
    {
        using var h = new Harness(enabled: false);
        var raised = new List<bool>();
        h.Sampler.RunningChanged += raised.Add;

        h.Enabled = true;
        h.Sampler.Reconcile();
        h.Sampler.Reconcile();
        h.Enabled = false;
        h.Sampler.Reconcile();

        Assert.Equal(new[] { true, false }, raised);
    }

    [Fact]
    public void The_timer_ticks_on_its_own()
    {
        using var h = new Harness(interval: TimeSpan.FromMilliseconds(20));
        h.Current = Quiet(pending: 1);

        var deadline = Stopwatch.StartNew();
        while (h.Snapshot().Length < 3 && deadline.Elapsed < TimeSpan.FromSeconds(30))
            Thread.Sleep(10);

        Assert.True(h.Snapshot().Length >= 3, "the timer never produced a sample");
    }

    [Fact]
    public void A_disposed_sampler_stops_sampling_and_ignores_further_reconciles()
    {
        var h = new Harness();
        h.Sampler.Dispose();

        Assert.False(h.Sampler.IsRunning);
        h.Sampler.Reconcile();
        Assert.False(h.Sampler.IsRunning);
    }

    [Fact]
    public void The_real_reading_describes_a_pool_and_a_heap()
    {
        var now = RuntimeStatsSampler.ReadCurrent();

        Assert.True(now.PoolThreads > 0);
        Assert.True(now.HeapBytes > 0);
        Assert.True(now.GcPause >= TimeSpan.Zero);
    }
}
