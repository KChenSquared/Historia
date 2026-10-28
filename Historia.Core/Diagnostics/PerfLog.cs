namespace Historia.Diagnostics;

/// <summary>
/// Stopwatch logging for startup / repo-open latency work. Off unless turned on by the
/// Enable logging setting in Settings → Advanced or the <c>HISTORIA_LOG</c> environment variable;
/// lines append to <c>&lt;logs&gt;/perf.log</c>.
/// </summary>
public static class PerfLog
{
    /// <summary>Whether marks are written. Setting it updates the process-wide switch.</summary>
    public static bool Enabled
    {
        get => LoggingSwitch.Enabled;
        set => LoggingSwitch.Set(value);
    }

    /// <summary>Test seam: overrides Enabled for the current async flow.</summary>
    internal static void OverrideEnabledForTesting(bool? enabled)
    {
        LoggingSwitch.OverrideForTesting(enabled);
    }

    /// <summary>Full path of the log file, for surfacing in the Developer panel.</summary>
    public static string LogPath => Path.Combine(AppPaths.LogsDirectory, "perf.log");

    /// <summary>
    /// Trims the perf log file to entries newer than <paramref name="cutoff"/>.
    /// Runs on the writer's dedicated thread if one is active, or inline if no handle is open.
    /// </summary>
    internal static Task TrimAsync(DateTime cutoff, string? path = null)
    {
        var targetPath = path ?? LogPath;
        var writer = Volatile.Read(ref _writer);
        if (writer is null)
        {
            try
            {
                LogRetention.TrimPerfFile(targetPath, cutoff);
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                return Task.FromException(ex);
            }
        }

        return writer.RunOnWriterAsync(() => LogRetention.TrimPerfFile(targetPath, cutoff));
    }

    private static readonly long Origin = Stopwatch.GetTimestamp();
    private static QueuedLineWriter? _writer;

    private static QueuedLineWriter Writer
    {
        get
        {
            var w = Volatile.Read(ref _writer);
            if (w is not null)
                return w;
            var created = QueuedLineWriter.OpenPerBatch("perf-log-writer");
            if (Interlocked.CompareExchange(ref _writer, created, null) is { } raced)
            {
                created.Dispose();
                return raced;
            }
            return created;
        }
    }

    /// <summary>Queues one timestamped line. The timestamp is taken here; the file write happens
    /// on the log writer's own thread, so a mark never waits on the disk.</summary>
    public static void Mark(string message)
    {
        if (!Enabled)
            return;

        var ms = Stopwatch.GetElapsedTime(Origin).TotalMilliseconds;
        // Invariant: the trim parses the leading date back, and some cultures change the time
        // separator or the calendar year.
        var line = string.Create(CultureInfo.InvariantCulture, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  +{ms,10:F1}ms  {message}");
        try { Writer.Enqueue(LogPath, line); }
        catch { /* diagnostics must never throw */ }
    }

    /// <summary>Queues one timestamped line. An interpolated string binds here rather than to
    /// <see cref="Mark(string)"/>, and its holes are not formatted while logging is off.</summary>
    public static void Mark(MessageHandler message)
    {
        var active = message.Active;
        var text = message.ToStringAndClear();
        if (active)
            Mark(text);
    }

    /// <summary>Waits (at most <paramref name="timeout"/>, default 2 s) until every mark queued so
    /// far is in the file. True when it got there in time.</summary>
    public static bool Flush(TimeSpan? timeout = null)
        => Volatile.Read(ref _writer) is not { } w || w.Flush(timeout ?? TimeSpan.FromSeconds(2));

    /// <summary>Test seam: replaces the line writer, returning the previous one.</summary>
    internal static QueuedLineWriter? SwapWriterForTesting(QueuedLineWriter? writer)
        => Interlocked.Exchange(ref _writer, writer);

    /// <summary>Times the scope and logs <c>label: N.Nms</c> on dispose.</summary>
    public static IDisposable Scope(string label) =>
        Enabled ? new Timer(label) : Noop.Instance;

    /// <summary>Times the scope and logs <c>label: N.Nms</c> on dispose. An interpolated label
    /// is not formatted while logging is off.</summary>
    public static IDisposable Scope(MessageHandler label)
    {
        var active = label.Active;
        var text = label.ToStringAndClear();
        return active && Enabled ? new Timer(text) : Noop.Instance;
    }

    /// <summary>
    /// One line of GC/process memory stats,
    /// tagged with <paramref name="label"/> so a run can be sliced by phase (quick paint, first
    /// paint, every 50k rows of silent pre-load, every 100k rows of the disk writer, before/after
    /// a tab-switch purge, 60s idle). Cheap enough to call from a hot loop when disabled — the
    /// early-out below is the only cost — but every field here is itself a GC query, so callers
    /// still gate frequent call sites behind a row-count stride rather than calling per row.
    /// </summary>
    public static void Memory(string label)
    {
        if (!Enabled)
            return;
        try
        {
            var info = GC.GetGCMemoryInfo();
            var gen = info.GenerationInfo;
            var loh = gen.Length > 3 ? gen[3].SizeAfterBytes : 0L;
            var poh = gen.Length > 4 ? gen[4].SizeAfterBytes : 0L;
            static long Mb(long bytes) => bytes / (1024 * 1024);
            Mark(
                $"memory: {label}  heap={Mb(info.HeapSizeBytes)}MB  frag={Mb(info.FragmentedBytes)}MB  " +
                $"loh={Mb(loh)}MB  poh={Mb(poh)}MB  committed={Mb(info.TotalCommittedBytes)}MB  " +
                $"workingSet={Mb(Environment.WorkingSet)}MB  totalAllocated={Mb(GC.GetTotalAllocatedBytes())}MB");
        }
        catch { /* diagnostics must never throw */ }
    }

    /// <summary>Builds an interpolated message only while logging is enabled: the compiler skips
    /// every <c>Append*</c> call, and so every hole's <c>ToString</c>, when
    /// <c>shouldAppend</c> comes back false. Formats under the current culture, exactly as an
    /// interpolated <see cref="string"/> does.</summary>
    [InterpolatedStringHandler]
    public ref struct MessageHandler
    {
        private LogMessageText _text;

        public MessageHandler(int literalLength, int formattedCount, out bool shouldAppend)
        {
            _text = new LogMessageText(literalLength, formattedCount, Enabled);
            shouldAppend = _text.Active;
        }

        /// <summary>True when logging was enabled as the message started and the holes were formatted.</summary>
        internal bool Active => _text.Active;

        public void AppendLiteral(string value) => _text.AppendLiteral(value);
        public void AppendFormatted(string? value) => _text.AppendFormatted(value);
        public void AppendFormatted(string? value, int alignment = 0, string? format = null) => _text.AppendFormatted(value, alignment, format);
        public void AppendFormatted(ReadOnlySpan<char> value) => _text.AppendFormatted(value);
        public void AppendFormatted(ReadOnlySpan<char> value, int alignment = 0, string? format = null) => _text.AppendFormatted(value, alignment, format);
        public void AppendFormatted<T>(T value) => _text.AppendFormatted(value);
        public void AppendFormatted<T>(T value, string? format) => _text.AppendFormatted(value, format);
        public void AppendFormatted<T>(T value, int alignment) => _text.AppendFormatted(value, alignment);
        public void AppendFormatted<T>(T value, int alignment, string? format) => _text.AppendFormatted(value, alignment, format);

        internal string ToStringAndClear() => _text.ToStringAndClear();
    }

    private sealed class Timer(string label) : IDisposable
    {
        private readonly long _start = Stopwatch.GetTimestamp();
        public void Dispose() => Mark($"{label}: {Stopwatch.GetElapsedTime(_start).TotalMilliseconds:F1}ms");
    }

    private sealed class Noop : IDisposable
    {
        public static readonly Noop Instance = new();
        public void Dispose() { }
    }
}
