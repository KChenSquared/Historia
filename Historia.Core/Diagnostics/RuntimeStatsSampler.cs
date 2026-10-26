namespace Historia.Diagnostics;

/// <summary>
/// Samples thread-pool and GC state every few seconds while logging is on and writes a
/// <see cref="PerfLog"/> line when something moved or work is queued, so a stall can be matched to
/// pool starvation or a long GC pause after the fact. While logging is off no timer exists.
/// <para>
/// The first time sampling starts it also writes one line with the GC mode and processor count.
/// Thread-pool starvation delays this sampler's own tick; the pause and collection deltas still
/// cover the whole gap, so nothing is lost, only reported late.
/// </para>
/// </summary>
public sealed class RuntimeStatsSampler : IDisposable
{
    internal static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(5);

    /// <summary>One reading of the pool and the GC. <c>Gen0</c> counts every collection, including
    /// those that also collected an older generation, as <see cref="GC.CollectionCount"/> does.</summary>
    internal readonly record struct Snapshot(
        int PoolThreads,
        long PendingWork,
        TimeSpan GcPause,
        int Gen0,
        int Gen1,
        int Gen2,
        long MemoryLoadBytes,
        long HighMemoryLoadThresholdBytes,
        long HeapBytes);

    private readonly object _gate = new();
    private readonly Action<string> _write;
    private readonly Func<Snapshot> _read;
    private readonly Func<bool> _enabled;
    private readonly TimeSpan _interval;
    private readonly bool _followLoggingSwitch;
    private System.Threading.Timer? _timer;
    private Snapshot _previous;
    private bool _modeLogged;
    private bool _disposed;
    private int _sampling;

    /// <summary>Follows the process-wide logging switch and writes to <see cref="PerfLog"/>.</summary>
    public RuntimeStatsSampler()
        : this(PerfLog.Mark, ReadCurrent, static () => LoggingSwitch.ProcessEnabled, DefaultInterval, followLoggingSwitch: true)
    {
    }

    internal RuntimeStatsSampler(
        Action<string> write,
        Func<Snapshot> read,
        Func<bool> enabled,
        TimeSpan interval,
        bool followLoggingSwitch)
    {
        _write = write;
        _read = read;
        _enabled = enabled;
        _interval = interval;
        _followLoggingSwitch = followLoggingSwitch;
        if (followLoggingSwitch)
            LoggingSwitch.Changed += OnLoggingSwitchChanged;
        Reconcile();
    }

    /// <summary>Raised, on the thread that flipped the logging switch, after sampling starts
    /// (<c>true</c>) or stops (<c>false</c>). Never raised under a lock.</summary>
    public event Action<bool>? RunningChanged;

    /// <summary>Whether the sampling timer exists.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_gate)
                return _timer is not null;
        }
    }

    private void OnLoggingSwitchChanged(bool enabled) => Reconcile();

    /// <summary>Starts or stops the timer to match the enabled state, repeating until the two agree
    /// so concurrent flips cannot leave the last one unapplied.</summary>
    internal void Reconcile()
    {
        while (true)
        {
            var want = _enabled();
            var baseline = want ? SafeRead() : default;
            string? modeLine = null;
            bool? flippedTo = null;

            lock (_gate)
            {
                if (_disposed)
                    return;

                if (want && _timer is null)
                {
                    _previous = baseline;
                    _timer = new System.Threading.Timer(static state => ((RuntimeStatsSampler)state!).Sample(), this, _interval, _interval);
                    if (!_modeLogged)
                    {
                        _modeLogged = true;
                        modeLine = ModeLine();
                    }
                    flippedTo = true;
                }
                else if (!want && _timer is not null)
                {
                    _timer.Dispose();
                    _timer = null;
                    flippedTo = false;
                }
            }

            if (modeLine is not null)
                Write(modeLine);

            if (flippedTo is { } running)
            {
                try { RunningChanged?.Invoke(running); }
                catch { /* diagnostics must never throw */ }
            }

            if (_enabled() == IsRunning)
                return;
        }
    }

    /// <summary>Reads the current state and writes a line when it differs from the previous sample
    /// in thread count, GC activity or pause time, or when work is queued on the pool. A tick that
    /// arrives while the previous one is still running is skipped.</summary>
    internal void Sample()
    {
        if (Interlocked.Exchange(ref _sampling, 1) == 1)
            return;

        try
        {
            var now = SafeRead();
            Snapshot previous;
            lock (_gate)
            {
                if (_timer is null || _disposed)
                    return;
                previous = _previous;
                _previous = now;
            }

            var line = Describe(previous, now);
            if (line is not null)
                Write(line);
        }
        finally
        {
            Volatile.Write(ref _sampling, 0);
        }
    }

    internal static string? Describe(Snapshot previous, Snapshot now)
    {
        var pause = now.GcPause - previous.GcPause;
        var gen0 = now.Gen0 - previous.Gen0;
        var gen1 = now.Gen1 - previous.Gen1;
        var gen2 = now.Gen2 - previous.Gen2;
        var changed = now.PoolThreads != previous.PoolThreads
            || gen0 != 0 || gen1 != 0 || gen2 != 0
            || pause > TimeSpan.Zero;
        if (!changed && now.PendingWork <= 0)
            return null;

        static long Mb(long bytes) => bytes / (1024 * 1024);
        var loadPercent = now.HighMemoryLoadThresholdBytes > 0
            ? now.MemoryLoadBytes * 100 / now.HighMemoryLoadThresholdBytes
            : 0;
        return string.Create(CultureInfo.InvariantCulture,
            $"runtime: pool threads={now.PoolThreads} pending={now.PendingWork} " +
            $"gcPause+={pause.TotalMilliseconds:F1}ms gen0+={gen0} gen1+={gen1} gen2+={gen2} " +
            $"load={loadPercent}% ({Mb(now.MemoryLoadBytes)}/{Mb(now.HighMemoryLoadThresholdBytes)}MB) heap={Mb(now.HeapBytes)}MB");
    }

    internal static string ModeLine() => string.Create(CultureInfo.InvariantCulture,
        $"runtime: gc server={System.Runtime.GCSettings.IsServerGC} latency={System.Runtime.GCSettings.LatencyMode} processors={Environment.ProcessorCount}");

    internal static Snapshot ReadCurrent()
    {
        var info = GC.GetGCMemoryInfo();
        return new Snapshot(
            ThreadPool.ThreadCount,
            ThreadPool.PendingWorkItemCount,
            GC.GetTotalPauseDuration(),
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2),
            info.MemoryLoadBytes,
            info.HighMemoryLoadThresholdBytes,
            GC.GetTotalMemory(forceFullCollection: false));
    }

    private Snapshot SafeRead()
    {
        try { return _read(); }
        catch { return default; }
    }

    private void Write(string line)
    {
        try { _write(line); }
        catch { /* diagnostics must never throw */ }
    }

    public void Dispose()
    {
        if (_followLoggingSwitch)
            LoggingSwitch.Changed -= OnLoggingSwitchChanged;

        lock (_gate)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }
}
