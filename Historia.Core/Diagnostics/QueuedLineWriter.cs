namespace Historia.Diagnostics;

/// <summary>
/// Appends log lines to files from one dedicated thread. Callers only enqueue, so a write that
/// the disk or a virus scanner holds up never blocks them — not the UI thread, not a thread-pool
/// thread. Lines reach their file in enqueue order; <see cref="Flush"/> waits, bounded, for every
/// line enqueued before it, and <see cref="Dispose"/> drains, bounded, and closes.
/// <para>
/// Trap: appending to the file inside a process-wide lock, on the calling thread, looked cheap.
/// One append held up for seconds stalled every thread that logged — the UI thread and every
/// thread-pool thread behind the lock — so the whole app froze and then resumed at once, and the
/// stall landed inside the very timings the log was recording.
/// </para>
/// <para>
/// Two modes. <see cref="HoldOpen"/> opens one file up front and keeps it open (a session log).
/// <see cref="OpenPerBatch"/> takes a path per line and keeps a file open only while it has lines
/// queued, so the file is readable by anything between batches and a path that changes between
/// lines (tests isolate state per flow) just switches files.
/// </para>
/// </summary>
internal sealed class QueuedLineWriter : IDisposable
{
    internal const int DefaultMaxBacklog = 100_000;

    /// <summary>Longest line kept; the rest is cut and replaced by a marker saying how much.</summary>
    internal const int MaxLineChars = 16_000;

    /// <summary>Most characters queued at once. With a stalled disk the queue holds this much
    /// (about twice that in bytes) and no more, however many lines arrive.</summary>
    internal const int MaxBacklogChars = 4_000_000;
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly TimeSpan DisposeDrainLimit = TimeSpan.FromSeconds(5);

    private readonly record struct Item(string? Path, string? Line, TaskCompletionSource? Flushed, Action? Action, TaskCompletionSource? ActionDone);

    private readonly BlockingCollection<Item> _queue = new(new ConcurrentQueue<Item>());
    private readonly Func<string, Stream> _openStream;
    private readonly bool _holdOpen;
    private readonly string? _fixedPath;
    private readonly int _maxBacklog;
    private readonly Thread _thread;
    private int _backlog;
    private int _backlogChars;
    private long _dropped;
    private int _disposed;

    // Writer-thread state only.
    private StreamWriter? _sink;
    private string? _sinkPath;

    private QueuedLineWriter(string? fixedPath, StreamWriter? openSink, bool holdOpen,
        Func<string, Stream>? openStream, int maxBacklog, string threadName)
    {
        _fixedPath = fixedPath;
        _sink = openSink;
        _sinkPath = openSink is null ? null : fixedPath;
        _holdOpen = holdOpen;
        _openStream = openStream ?? OpenAppend;
        _maxBacklog = maxBacklog;
        _thread = new Thread(Run) { IsBackground = true, Name = threadName };
        _thread.Start();
    }

    /// <summary>Opens <paramref name="path"/> for append now, on the caller, and keeps it open
    /// until <see cref="Dispose"/>. Throws when the file can't be opened, as opening it directly
    /// would.</summary>
    public static QueuedLineWriter HoldOpen(string path, string threadName,
        Func<string, Stream>? openStream = null, int maxBacklog = DefaultMaxBacklog)
    {
        var stream = (openStream ?? OpenAppend)(path);
        return new QueuedLineWriter(path, new StreamWriter(stream, Utf8NoBom), holdOpen: true,
            openStream, maxBacklog, threadName);
    }

    /// <summary>Takes a path per line (<see cref="Enqueue(string, string)"/>); a file is open only
    /// while lines for it are queued.</summary>
    public static QueuedLineWriter OpenPerBatch(string threadName,
        Func<string, Stream>? openStream = null, int maxBacklog = DefaultMaxBacklog)
        => new(null, null, holdOpen: false, openStream, maxBacklog, threadName);

    /// <summary>Queues a line for the file this writer holds open. Never blocks.</summary>
    public void Enqueue(string line) => Enqueue(_fixedPath!, line);

    /// <summary>Queues a line for <paramref name="path"/>. Never blocks. A line longer than
    /// <see cref="MaxLineChars"/> is cut and says how much was left out. Beyond the line or
    /// character backlog cap the line is dropped and counted; the count is written once the writer
    /// catches up.</summary>
    public void Enqueue(string path, string line)
    {
        // The count the check uses is the length the line will have once cut, worked out without
        // building the cut text: a refused line allocates nothing.
        var keep = KeptChars(line);
        var chars = keep == line.Length ? line.Length : keep + MarkerLength(line.Length - keep);

        // Both counters are claimed before the check and given back together when the line is
        // refused, so neither drifts however the callers interleave.
        var overLines = Interlocked.Increment(ref _backlog) > _maxBacklog;
        var overChars = Interlocked.Add(ref _backlogChars, chars) > MaxBacklogChars;
        if (overLines || overChars)
        {
            Interlocked.Decrement(ref _backlog);
            Interlocked.Add(ref _backlogChars, -chars);
            Interlocked.Increment(ref _dropped);
            return;
        }

        if (keep != line.Length)
            line = string.Concat(line.AsSpan(0, keep), CutMarker(line.Length - keep));

        if (!TryAdd(new Item(path, line, null, null, null)))
        {
            Interlocked.Decrement(ref _backlog);
            Interlocked.Add(ref _backlogChars, -chars);
        }
    }

    private const string CutMarkerEnd = " more characters not logged]";

    /// <summary>How many leading characters of <paramref name="line"/> are kept.</summary>
    private static int KeptChars(string line)
    {
        if (line.Length <= MaxLineChars)
            return line.Length;

        var keep = MaxLineChars;
        if (char.IsHighSurrogate(line[keep - 1]))
            keep--; // never end the kept text on half a surrogate pair
        return keep;
    }

    private static string CutMarker(int cut) => $"… [{cut}{CutMarkerEnd}";

    private static int MarkerLength(int cut)
    {
        var digits = 1;
        for (var n = cut; n >= 10; n /= 10)
            digits++;
        return "… [".Length + digits + CutMarkerEnd.Length;
    }

    /// <summary>Waits until every line enqueued before this call is written and flushed to disk
    /// (and, per-batch, the file closed), or <paramref name="timeout"/> passes. True when it got
    /// there in time.</summary>
    public bool Flush(TimeSpan timeout)
    {
        if (Volatile.Read(ref _disposed) != 0 || Thread.CurrentThread == _thread)
            return true;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!TryAdd(new Item(null, null, done, null, null)))
            return true;
        return done.Task.Wait(timeout);
    }

    /// <summary>
    /// Queues a maintenance action to run on the writer thread. The writer thread flushes and closes
    /// its sink, runs <paramref name="action"/>, and completes the returned task.
    /// Preserves enqueue order. A disposed writer returns a completed task and does not run the action.
    /// </summary>
    public Task RunOnWriterAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (Volatile.Read(ref _disposed) != 0)
            return Task.CompletedTask;

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!TryAdd(new Item(null, null, null, action, done)))
            return Task.CompletedTask;

        return done.Task;
    }

    private bool TryAdd(Item item)
    {
        try { return _queue.TryAdd(item); }
        catch (InvalidOperationException) { return false; } // completed (disposing) or disposed
    }

    private void Run()
    {
        try
        {
            foreach (var item in _queue.GetConsumingEnumerable())
            {
                if (item.Flushed is { } flushed)
                {
                    FlushSink(closeIfPerBatch: true);
                    flushed.TrySetResult();
                    continue;
                }

                if (item.Action is { } action)
                {
                    FlushSink(closeIfPerBatch: true);
                    CloseSink();
                    try
                    {
                        action();
                        item.ActionDone?.TrySetResult();
                    }
                    catch (Exception ex)
                    {
                        item.ActionDone?.TrySetException(ex);
                    }
                    continue;
                }

                Interlocked.Decrement(ref _backlog);
                Interlocked.Add(ref _backlogChars, -item.Line!.Length);
                Write(item.Path!, item.Line);
                if (_queue.Count == 0)
                    FlushSink(closeIfPerBatch: true);
            }
        }
        catch
        {
            // diagnostics must never throw — not even from the writer thread
        }
        finally
        {
            FlushSink(closeIfPerBatch: true);
            if (_holdOpen)
                CloseSink();
        }
    }

    private void Write(string path, string line)
    {
        try
        {
            if (_sink is null || !string.Equals(_sinkPath, path, StringComparison.OrdinalIgnoreCase))
            {
                if (_holdOpen && _sink is not null)
                    return; // a held-open writer only ever writes its own file
                CloseSink();
                if (Path.GetDirectoryName(path) is { Length: > 0 } dir)
                    Directory.CreateDirectory(dir);
                _sink = new StreamWriter(_openStream(path), Utf8NoBom);
                _sinkPath = path;
            }

            var dropped = Interlocked.Exchange(ref _dropped, 0);
            if (dropped > 0)
                _sink.WriteLine($"… {dropped} log lines dropped: the writer fell too far behind ({_maxBacklog} lines or {MaxBacklogChars} characters)");
            _sink.WriteLine(line);
        }
        catch
        {
            // disk full / permission / file gone — drop the line, and reopen next time
            CloseSink();
        }
    }

    private void FlushSink(bool closeIfPerBatch)
    {
        try { _sink?.Flush(); }
        catch { CloseSink(); }
        if (closeIfPerBatch && !_holdOpen)
            CloseSink();
    }

    private void CloseSink()
    {
        try { _sink?.Dispose(); }
        catch { /* best effort */ }
        _sink = null;
        _sinkPath = null;
    }

    private static Stream OpenAppend(string path) =>
        // FileShare.Read: anything that wants to read the log while it's live can.
        new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);

    /// <summary>Stops taking lines, writes what is queued (for at most a few seconds — a stalled
    /// disk must not hang the caller), and closes the file.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        try { _queue.CompleteAdding(); } catch { /* best effort */ }
        if (Thread.CurrentThread != _thread)
            _thread.Join(DisposeDrainLimit);
    }
}
