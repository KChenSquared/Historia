namespace Historia.Diagnostics;

/// <summary>
/// Detects UI-thread stalls. A timer that runs off the UI thread posts a no-op probe at input
/// priority every few hundred milliseconds; when the probe runs later than a threshold, the full
/// time it waited is written to <see cref="PerfLog"/> as <c>ui stall</c>. The timer is a
/// <see cref="System.Threading.Timer"/> because a dispatcher timer would stop ticking exactly when
/// the thread it measures is stuck. At most one probe is in flight: ticks while a probe is still
/// waiting are skipped, and the one stall that ends is reported once, with its whole duration.
/// <para>
/// While the <c>shouldRun</c> answer is false no timer exists; <see cref="Reconcile"/> applies
/// a change in that answer. The production heartbeat follows <see cref="LoggingSwitch"/> itself.
/// </para>
/// </summary>
public sealed class UiHeartbeat : IDisposable
{
    internal static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(250);
    internal static readonly TimeSpan DefaultThreshold = TimeSpan.FromMilliseconds(100);

    private readonly object _gate = new();
    private readonly Dispatcher _dispatcher;
    private readonly Func<bool> _shouldRun;
    private readonly Action<string> _write;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _threshold;
    private readonly bool _followLoggingSwitch;
    private System.Threading.Timer? _timer;
    private bool _disposed;
    private int _inFlight;
    private long _probesPosted;
    private long _probesCompleted;

    /// <summary>Follows the process-wide logging switch and writes stalls to <see cref="PerfLog"/>.</summary>
    public UiHeartbeat(Dispatcher dispatcher)
        : this(dispatcher, static () => LoggingSwitch.ProcessEnabled, PerfLog.Mark, DefaultInterval, DefaultThreshold, followLoggingSwitch: true)
    {
    }

    internal UiHeartbeat(
        Dispatcher dispatcher,
        Func<bool> shouldRun,
        Action<string> write,
        TimeSpan interval,
        TimeSpan threshold,
        bool followLoggingSwitch = false)
    {
        _dispatcher = dispatcher;
        _shouldRun = shouldRun;
        _write = write;
        _interval = interval;
        _threshold = threshold;
        _followLoggingSwitch = followLoggingSwitch;
        if (followLoggingSwitch)
            LoggingSwitch.Changed += OnLoggingSwitchChanged;
        Reconcile();
    }

    /// <summary>Whether the probe timer exists.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_gate)
                return _timer is not null;
        }
    }

    internal bool ProbeInFlight => Volatile.Read(ref _inFlight) == 1;

    internal long ProbesPosted => Interlocked.Read(ref _probesPosted);

    internal long ProbesCompleted => Interlocked.Read(ref _probesCompleted);

    private void OnLoggingSwitchChanged(bool enabled) => Reconcile();

    /// <summary>Starts or stops the timer to match <c>shouldRun</c>, repeating until the two agree
    /// so concurrent changes cannot leave the last one unapplied.</summary>
    public void Reconcile()
    {
        while (true)
        {
            var want = _shouldRun();
            lock (_gate)
            {
                if (_disposed)
                    return;

                if (want && _timer is null)
                    _timer = new System.Threading.Timer(static state => ((UiHeartbeat)state!).Tick(), this, _interval, _interval);
                else if (!want && _timer is not null)
                {
                    _timer.Dispose();
                    _timer = null;
                }
            }

            if (_shouldRun() == IsRunning)
                return;
        }
    }

    private void Tick()
    {
        if (Volatile.Read(ref _disposed))
            return;

        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0)
            return;

        var posted = Stopwatch.GetTimestamp();
        Interlocked.Increment(ref _probesPosted);
        try
        {
            _dispatcher.Post(() => Probe(posted), DispatcherPriority.Input);
        }
        catch
        {
            Volatile.Write(ref _inFlight, 0);
        }
    }

    private void Probe(long posted)
    {
        var waited = Stopwatch.GetElapsedTime(posted);
        Interlocked.Increment(ref _probesCompleted);
        Volatile.Write(ref _inFlight, 0);
        if (Volatile.Read(ref _disposed) || waited <= _threshold)
            return;

        try { _write(string.Create(CultureInfo.InvariantCulture, $"ui stall {waited.TotalMilliseconds:F0}ms")); }
        catch { /* diagnostics must never throw */ }
    }

    /// <summary>Stops the timer. A tick or probe already running when this returns does nothing
    /// visible: both check the disposed flag, because <see cref="System.Threading.Timer.Dispose()"/>
    /// does not wait for a running callback and waiting here would block the UI thread.</summary>
    public void Dispose()
    {
        if (_followLoggingSwitch)
            LoggingSwitch.Changed -= OnLoggingSwitchChanged;

        lock (_gate)
        {
            Volatile.Write(ref _disposed, true);
            _timer?.Dispose();
            _timer = null;
        }
    }
}
