namespace Historia.Services.Storage;

/// <summary>
/// Atomic, serialized file writes. Every write to a given path is funneled through a
/// per-path <see cref="SemaphoreSlim"/>; the payload is written to a temp file and then
/// atomically renamed over the target so a crash never leaves a half-written file.
/// Rapid successive writes are coalesced on a 250 ms trailing debounce — only the final
/// state reaches disk.
/// </summary>
public sealed class AtomicFileWriter : IAsyncDisposable
{
    private const int DebounceMilliseconds = 250;

    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _pendingLock = new();

    private string? _pendingContent;
    private long _lastQueuedSequence;
    private CancellationTokenSource? _debounceCts;
    private Task _debounceTask = Task.CompletedTask;

    public string FilePath { get; }

    public AtomicFileWriter(string filePath)
    {
        FilePath = filePath;
        var dir = Path.GetDirectoryName(filePath)!;
        Directory.CreateDirectory(dir);

        // Each write uses a unique temp name (so two processes writing the same target
        // cannot interleave into one shared temp), which means a hard crash mid-write can
        // leave an orphan. Sweep our own stale temps on construction — best effort.
        try
        {
            var prefix = Path.GetFileName(filePath) + ".";
            foreach (var stale in Directory.EnumerateFiles(dir, prefix + "*.tmp"))
            {
                try { File.Delete(stale); } catch { /* locked or gone */ }
            }
        }
        catch { /* directory unreadable — not fatal */ }
    }

    /// <summary>Writes immediately, bypassing the debounce. Used at shutdown and for first-write.
    /// Best-effort like every other write here — a disk failure is logged and swallowed, never
    /// thrown to the caller.</summary>
    public async Task WriteNowAsync(string content, CancellationToken ct = default)
        => await WriteNowInternalAsync(content, ct).ConfigureAwait(false);

    /// <summary>Same as <see cref="WriteNowAsync"/> but reports whether the write actually
    /// reached disk, so a caller that is still holding the payload (<see cref="FlushAsync"/>)
    /// knows whether it is safe to drop it.</summary>
    private async Task<bool> WriteNowInternalAsync(string content, CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await WriteAtomicAsync(content, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Queues a debounced write. Successive calls within the debounce window replace the
    /// pending payload; the timer restarts on each call.
    /// </summary>
    public void QueueWrite(string content)
    {
        lock (_pendingLock)
            QueuePendingLocked(content);
    }

    /// <summary>
    /// Queues a debounced write of a snapshot numbered <paramref name="sequence"/>. A snapshot
    /// numbered no higher than the newest one already queued is dropped, so a slow caller can never
    /// replace a newer snapshot with an older one.
    /// <para>Trap: a caller that took its snapshot under its own lock but published it after
    /// releasing that lock let two racing changes queue their snapshots in the opposite order, and
    /// the older one was then the one debounced to disk and flushed at shutdown. The caller takes
    /// <paramref name="sequence"/> in the same critical section as the snapshot (so the numbers
    /// follow the order the snapshots were taken in) and serializes and calls this after releasing
    /// its lock: queuing cancels a <see cref="CancellationTokenSource"/>, which is never done under a
    /// caller's lock.</para>
    /// </summary>
    public void QueueWrite(string content, long sequence)
    {
        lock (_pendingLock)
        {
            if (sequence <= _lastQueuedSequence)
                return;
            _lastQueuedSequence = sequence;
            QueuePendingLocked(content);
        }
    }

    /// <summary>The payload waiting for its debounce or flush, for tests.</summary>
    internal string? PendingContentForTesting
    {
        get { lock (_pendingLock) return _pendingContent; }
    }

    private void QueuePendingLocked(string content)
    {
        _pendingContent = content;
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;
        _debounceTask = RunDebouncedAsync(token);
    }

    /// <summary>Flushes any pending debounced write synchronously with respect to completion.</summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        Task toAwait;
        string? pending;
        lock (_pendingLock)
        {
            toAwait = _debounceTask;
            pending = _pendingContent;
        }

        try
        {
            await toAwait.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A newer QueueWrite superseded it; fall through and write the latest below.
        }

        lock (_pendingLock)
        {
            pending = _pendingContent;
        }

        if (pending is not null)
        {
            // Only drop the pending payload once it actually reached disk — a failed write
            // (disk full, a locked temp file) must leave it queued rather than disappear, since
            // nothing else remembers it once this clears (silent write-loss on failure).
            var wrote = await WriteNowInternalAsync(pending, ct).ConfigureAwait(false);
            if (wrote)
            {
                lock (_pendingLock)
                {
                    if (ReferenceEquals(_pendingContent, pending))
                        _pendingContent = null;
                }
            }
        }
    }

    private async Task RunDebouncedAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(DebounceMilliseconds, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // Peek, don't clear yet: a failed write below must leave the payload in place for the
        // next debounce cycle or FlushAsync to retry (silent write-loss on failure).
        string? content;
        lock (_pendingLock)
        {
            content = _pendingContent;
        }

        if (content is null)
            return;

        await _writeGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var wrote = await WriteAtomicAsync(content, token).ConfigureAwait(false);
            if (wrote)
            {
                lock (_pendingLock)
                {
                    // Only clear if nothing newer was queued while this write was in flight.
                    if (ReferenceEquals(_pendingContent, content))
                        _pendingContent = null;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown; _pendingContent is untouched — FlushAsync will pick it up.
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <returns>True once the content actually reached <see cref="FilePath"/>. False is a
    /// non-fatal give-up (disk full, a held lock) — logged, and the caller keeps the payload
    /// queued rather than treating the write as done. A cancellation still throws, since that
    /// path is caller-driven and handled explicitly by every caller.</returns>
    private async Task<bool> WriteAtomicAsync(string content, CancellationToken ct)
    {
        // Unique per write: a second process (or a second AtomicFileWriter) targeting the same
        // path cannot truncate a temp file this write is still appending to.
        var tempPath = $"{FilePath}.{Guid.NewGuid():N}.tmp";

        try
        {
            using (Historia.Diagnostics.PerfLog.Scope($"AtomicFileWriter WriteAllTextAsync ({System.IO.Path.GetFileName(FilePath)})"))
                await File.WriteAllTextAsync(tempPath, content, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(tempPath); } catch { /* ignore */ }
            AppLog.Write(AppLogLevel.Warn, "storage",
                $"write failed for {System.IO.Path.GetFileName(FilePath)}", ex);
            return false;
        }

        // File.Move with overwrite is atomic on the same volume on Windows (ReplaceFile semantics).
        // Documents is often OneDrive-backed and the sync client can hold a transient
        // lock across the replace. Retry three times, 50 ms apart, then give up non-fatally.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(tempPath, FilePath, overwrite: true);
                return true;
            }
            catch (IOException) when (attempt < 2)
            {
                await Task.Delay(50, ct).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException) when (attempt < 2)
            {
                await Task.Delay(50, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                try { File.Delete(tempPath); } catch { /* ignore */ }
                AppLog.Write(AppLogLevel.Warn, "storage",
                    $"move failed for {System.IO.Path.GetFileName(FilePath)}", ex);
                return false; // give-up stays non-fatal
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Historia.Diagnostics.PerfLog.Mark($"AtomicFileWriter.DisposeAsync start ({System.IO.Path.GetFileName(FilePath)})");
        try
        {
            using (Historia.Diagnostics.PerfLog.Scope($"AtomicFileWriter FlushAsync ({System.IO.Path.GetFileName(FilePath)})"))
                await FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort flush on dispose.
        }

        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _writeGate.Dispose();
        Historia.Diagnostics.PerfLog.Mark($"AtomicFileWriter.DisposeAsync done ({System.IO.Path.GetFileName(FilePath)})");
    }
}
