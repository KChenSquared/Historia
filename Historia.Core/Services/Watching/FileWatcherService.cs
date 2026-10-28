namespace Historia.Services.Watching;

/// <summary>
/// Debounced, gitignore-aware working-directory + <c>.git</c> watcher. Filters
/// ignored paths before the debounce fires so a build cannot produce an event storm; falls
/// back to refresh-on-focus when the watcher cannot be set up or the tree is too large.
/// Owned by <see cref="RepositoryContext"/>.
/// </summary>
public sealed class FileWatcherService : IAsyncDisposable
{
    private const int DebounceMs = 150;
    private const int MaxWaitMs = 1000;
    private const int LargeTreeFileThreshold = 50_000;
    private const int MaxInternalBuffer = 64 * 1024;
    private const int StackPathChars = 512;

    private readonly RepositoryContext _context;
    private readonly IRepositoryStateService _state;
    private readonly ISettingsService _settings;
    private readonly IWorkspaceService? _workspace;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly object _sync = new();
    private readonly HashSet<string> _pendingPaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Memoizes <see cref="IsBesideAProjectFile"/> per relative directory (see
    /// <see cref="IsLikelyIgnored"/>) so a build storm under one project's bin/obj pays the
    /// filesystem probe once rather than once per file event.</summary>
    private readonly ConcurrentDictionary<string, bool> _buildOutputParents = new(StringComparer.OrdinalIgnoreCase);

    private Timer? _debounceTimer;
    private DateTimeOffset _firstPendingAt;
    private bool _refsTouched;
    private bool _forceFull;
    private string? _firstRefPath;
    private long _lastOverflowLogTimestamp;
    private int _suppressedOverflowLogs;
    private Timer? _overflowFlushTimer;
    private readonly bool _watchFilesystem;
    private readonly string _gitDirRoot;
    private readonly string _workDirRoot;
    private volatile bool _disposed;

    /// <summary>Minimum gap between two logged watcher overflows; overflows inside it are counted
    /// and reported on the next logged one, so a sustained storm leaves a few lines, not thousands.</summary>
    internal TimeSpan OverflowLogWindow { get; set; } = TimeSpan.FromSeconds(10);

    public bool IsDegraded { get; private set; }
    public string? DegradedReason { get; private set; }

    /// <summary>Git tracks at least <see cref="LargeTreeFileThreshold"/> files here: the working
    /// tree is not watched, and a cold <c>git status</c> reads so many files that other git work
    /// on the same disk slows down while it runs.</summary>
    public bool IsLargeWorkingTree { get; private set; }

    /// <summary>Test seam: a fixture can't reach the tracked-file threshold.</summary>
    internal void TreatAsLargeWorkingTreeForTesting() => IsLargeWorkingTree = true;

    /// <summary>Test seam: whether the tab's workspace was known when this watcher started.</summary>
    internal bool StartedWithWorkspaceForTesting => _workspace is not null;

    public FileWatcherService(
        RepositoryContext context,
        IRepositoryStateService state,
        ISettingsService settings,
        IWorkspaceService? workspace = null)
        : this(context, state, settings, workspace, watchFilesystem: true)
    {
    }

    /// <summary>Test seam: with <paramref name="watchFilesystem"/> false no operating-system
    /// watcher is attached, so only events a test injects reach the debounce.</summary>
    internal FileWatcherService(
        RepositoryContext context,
        IRepositoryStateService state,
        ISettingsService settings,
        IWorkspaceService? workspace,
        bool watchFilesystem)
    {
        _context = context;
        _state = state;
        _settings = settings;
        _workspace = workspace ?? context.Workspace;
        _watchFilesystem = watchFilesystem;
        _gitDirRoot = NormalizePath(context.GitDirectory);
        _workDirRoot = NormalizePath(context.RepositoryPath);

        try
        {
            Start();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
        {
            EnterDegradedMode($"File watching is unavailable here ({ex.GetType().Name}). Using refresh-on-focus.");
        }
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private void Start()
    {
        _debounceTimer = new Timer(OnDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);

        var gitDir = _gitDirRoot;
        if (Directory.Exists(gitDir))
        {
            AddWatcher(gitDir, includeSubdirectories: false, isGitDir: true);
            var refsDir = Path.Combine(gitDir, "refs");
            if (Directory.Exists(refsDir))
                AddWatcher(refsDir, includeSubdirectories: true, isGitDir: true);
        }

        if (_context.IsBare)
        {
            EnterDegradedMode("Bare repository — no working tree to watch.");
            return;
        }

        var workDir = _workDirRoot;
        if (Directory.Exists(workDir) && TrackedFileCount(gitDir) >= LargeTreeFileThreshold)
        {
            IsLargeWorkingTree = true;
            EnterDegradedMode("Repository working tree is very large. Watching .git only; working tree uses refresh-on-focus.");
            return;
        }

        if (Directory.Exists(workDir))
            AddWatcher(workDir, includeSubdirectories: true, isGitDir: false);
    }

    private void AddWatcher(string path, bool includeSubdirectories, bool isGitDir)
    {
        if (!_watchFilesystem)
            return;

        var watcher = new FileSystemWatcher(path)
        {
            IncludeSubdirectories = includeSubdirectories,
            InternalBufferSize = MaxInternalBuffer,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                         | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
        };

        watcher.Changed += (_, e) => OnRawEvent(e.FullPath, isGitDir);
        watcher.Created += (_, e) => OnRawEvent(e.FullPath, isGitDir);
        watcher.Deleted += (_, e) => OnRawEvent(e.FullPath, isGitDir);
        watcher.Renamed += (_, e) =>
        {
            OnRawEvent(e.FullPath, isGitDir);
            OnRawEvent(e.OldFullPath, isGitDir);
        };
        watcher.Error += OnWatcherError;

        watcher.EnableRaisingEvents = true;
        _watchers.Add(watcher);
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        if (_disposed)
            return;

        // Buffer overflow: discard pending state and force one full refresh — never ignored.
        var now = Stopwatch.GetTimestamp();
        var logIt = false;
        var suppressed = 0;
        lock (_sync)
        {
            if (_disposed)
                return;
            _pendingPaths.Clear();
            _firstRefPath = null;
            _forceFull = true;
            _refsTouched = true;
            ScheduleLocked();

            if (_lastOverflowLogTimestamp == 0
                || Stopwatch.GetElapsedTime(_lastOverflowLogTimestamp, now) >= OverflowLogWindow)
            {
                logIt = true;
                suppressed = _suppressedOverflowLogs;
                _suppressedOverflowLogs = 0;
                _lastOverflowLogTimestamp = now;
            }
            else
            {
                _suppressedOverflowLogs++;
                ArmOverflowFlushLocked(now);
            }
        }

        if (!logIt)
            return;

        var repeats = suppressed > 0 ? $"; {suppressed} further overflow(s) in the last window not logged" : "";
        var error = e.GetException();
        AppLog.Write(
            AppLogLevel.Warn,
            "watcher",
            $"file watcher overflowed or failed on {_context.DisplayName} ({error.GetType().Name}: {error.Message}); pending changes dropped, full refresh forced{repeats}");
    }

    /// <summary>Arms a one-shot timer for the end of the log window so overflows suppressed after
    /// the last logged line are reported even when no further overflow arrives. Caller holds
    /// <see cref="_sync"/>.</summary>
    private void ArmOverflowFlushLocked(long now)
    {
        var remaining = OverflowLogWindow - Stopwatch.GetElapsedTime(_lastOverflowLogTimestamp, now);
        if (remaining < TimeSpan.Zero)
            remaining = TimeSpan.Zero;
        else if (remaining < TimeSpan.FromMilliseconds(1))
            remaining = TimeSpan.FromMilliseconds(1);
        try
        {
            if (_overflowFlushTimer is null)
                _overflowFlushTimer = new Timer(_ => FlushSuppressedOverflows(force: false), null, remaining, Timeout.InfiniteTimeSpan);
            else
                _overflowFlushTimer.Change(remaining, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Logs overflows that were counted but never reported. Unless <paramref name="force"/>,
    /// waits for the log window to expire so a sustained storm still leaves few lines.</summary>
    private void FlushSuppressedOverflows(bool force)
    {
        int suppressed;
        lock (_sync)
        {
            if (_suppressedOverflowLogs == 0)
                return;
            if (!force && Stopwatch.GetElapsedTime(_lastOverflowLogTimestamp) < OverflowLogWindow)
            {
                // The timer counts milliseconds off a coarser clock than the window, so it can
                // fire a tick before the window ends; try again for the rest of it.
                if (!_disposed)
                    ArmOverflowFlushLocked(Stopwatch.GetTimestamp());
                return;
            }
            suppressed = _suppressedOverflowLogs;
            _suppressedOverflowLogs = 0;
        }

        AppLog.Write(
            AppLogLevel.Warn,
            "watcher",
            $"{suppressed} further overflow(s) on {_context.DisplayName} not logged");
    }

    private void OnRawEvent(string fullPath, bool isGitDir)
    {
        if (_disposed)
            return;

        // Historia's own Git operations force an explicit refresh on completion.
        if (_context.IsSelfOperationInProgress)
            return;

        var path = NormalizePath(fullPath);
        if (IsSameOrUnder(path, _gitDirRoot))
        {
            // A fetch or push writes refs and objects here and refreshes once when it ends;
            // working-tree events, which are the user's own edits, fall through and still count.
            if (_context.IsGitDirectoryOperationInProgress)
                return;

            QueueEvent(path.AsSpan(_gitDirRoot.Length), underGit: true);
            return;
        }

        // A path the git-directory watcher reports outside the git directory is not one to act on.
        if (isGitDir || !IsSameOrUnder(path, _workDirRoot))
            return;

        QueueEvent(path.AsSpan(_workDirRoot.Length), underGit: false);
    }

    /// <summary>Classifies one event by its path relative to the git directory or the working tree
    /// and queues it for the debounce. The path is normalized into a stack buffer and the string a
    /// queued event needs is allocated only after the event is known to matter, so ignored paths —
    /// the bulk of a build — allocate nothing.</summary>
    private void QueueEvent(ReadOnlySpan<char> relative, bool underGit)
    {
        while (relative.Length > 0 && relative[0] is '/' or '\\')
            relative = relative[1..];

        char[]? rented = null;
        Span<char> buffer = stackalloc char[StackPathChars];
        if (relative.Length > StackPathChars)
        {
            rented = System.Buffers.ArrayPool<char>.Shared.Rent(relative.Length);
            buffer = rented;
        }

        try
        {
            var rel = buffer[..relative.Length];
            relative.CopyTo(rel);
            rel.Replace('\\', '/');

            if (underGit)
            {
                // Only a few .git entries matter; everything else (objects/**, logs/**) is noise.
                if (!IsMeaningfulGitPath(rel))
                    return;

                // The index changes on every stage, unstage and stat refresh and says nothing about
                // refs or the graph; it is a status change like a working-tree edit.
                var statusOnly = rel is "index";
                var gitPath = new string(rel);
                lock (_sync)
                {
                    if (!statusOnly)
                    {
                        _refsTouched = true;
                        _firstRefPath ??= gitPath;
                    }
                    _pendingPaths.Add(gitPath);
                    ScheduleLocked();
                }
                return;
            }

            // Working-tree path: drop ignored paths before the debounce fires.
            if (IsLikelyIgnored(rel))
                return;

            var workPath = rel.Length == 0 ? "." : new string(rel);
            lock (_sync)
            {
                _pendingPaths.Add(workPath);
                ScheduleLocked();
            }
        }
        finally
        {
            if (rented is not null)
                System.Buffers.ArrayPool<char>.Shared.Return(rented);
        }
    }

    /// <summary>True for the git-directory entries whose change affects Historia: refs and the
    /// files that move or describe them (full refresh), and the index (status only).</summary>
    private static bool IsMeaningfulGitPath(ReadOnlySpan<char> rel)
        => rel is "HEAD" or "index" or "packed-refs" or "MERGE_HEAD" or "REBASE_HEAD"
               or "CHERRY_PICK_HEAD" or "REVERT_HEAD" or "ORIG_HEAD" or "config"
           || rel.StartsWith("refs/", StringComparison.Ordinal)
           || rel.StartsWith("rebase-merge", StringComparison.Ordinal)
           || rel.StartsWith("rebase-apply", StringComparison.Ordinal);

    /// <summary>True when <paramref name="path"/> is <paramref name="dir"/> or inside it. A sibling
    /// that merely shares the prefix (<c>.gitignore</c>, <c>.github/</c> beside <c>.git</c>) is not.</summary>
    internal static bool IsSameOrUnder(string path, string dir)
    {
        if (string.IsNullOrEmpty(dir) || !path.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
            return false;
        return path.Length == dir.Length || path[dir.Length] is '/' or '\\';
    }

    private void ScheduleLocked()
    {
        var timer = _debounceTimer;
        if (timer is null || _disposed)
            return;

        var now = DateTimeOffset.UtcNow;
        if (_pendingPaths.Count == 0 && !_forceFull)
            _firstPendingAt = now;
        else if (_firstPendingAt == default)
            _firstPendingAt = now;

        var elapsed = (now - _firstPendingAt).TotalMilliseconds;
        var dueIn = elapsed >= MaxWaitMs ? 0 : Math.Min(DebounceMs, MaxWaitMs - (int)elapsed);
        try
        {
            timer.Change(Math.Max(0, dueIn), Timeout.Infinite);
        }
        catch (ObjectDisposedException) { }
    }

    private void OnDebounceElapsed(object? _)
    {
        if (_disposed)
            return;

        FlushSuppressedOverflows(force: false);

        bool refs;
        bool full;
        string? firstRefPath;
        lock (_sync)
        {
            if (_disposed)
                return;

            if (_pendingPaths.Count == 0 && !_forceFull)
                return;
            refs = _refsTouched;
            full = _forceFull;
            firstRefPath = _firstRefPath;
            _firstRefPath = null;
            _pendingPaths.Clear();
            _refsTouched = false;
            _forceFull = false;
            _firstPendingAt = default;
        }

        _ = PublishAsync(refs, full, firstRefPath);
    }

    private async Task PublishAsync(bool refsTouched, bool full, string? firstRefPath)
    {
        try
        {
            var isFull = full || refsTouched;
            if (isFull && PerfLog.Enabled)
            {
                var trigger = full ? "overflow" : $"refs:{firstRefPath}";
                PerfLog.Mark($"watcher full refresh ({_context.DisplayName}) trigger {trigger}");
            }

            var ws = _workspace ?? _context.Workspace;
            if (ws is not null)
            {
                await ws.RequestBackgroundRefreshAsync(_context, isFull, _context.Lifetime).ConfigureAwait(false);
            }
            else
            {
                if (isFull)
                    await _state.RefreshAllAsync(_context.Lifetime).ConfigureAwait(false);
                else
                    await _state.RefreshStatusAsync(_context.Lifetime).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Repo closing.
        }
        catch
        {
            // A refresh failure must not tear down the watcher.
        }
    }


    /// <summary>Internal for a direct test seam (Historia.Tests) — otherwise only called from
    /// <see cref="QueueEvent"/>. <paramref name="relPath"/> uses <c>/</c> separators.</summary>
    internal bool IsLikelyIgnored(ReadOnlySpan<char> relPath)
    {
        // Cheap synchronous pre-filter for the common build-output offenders only. The
        // authoritative .gitignore check is handled lazily during the status scan — never here:
        // this runs on the FileSystemWatcher callback thread, and expensive Git CLI operations
        // here would stall later filesystem events and can starve the thread pool.
        //
        // node_modules/.vs/.git are near-certain vendor/tooling dirs, always skipped. bin/obj
        // are also skipped, but only where they actually look like build output — beside a
        // project file — so a repo that tracks a top-level bin/ of scripts (a common layout)
        // still gets its Changes panel updated when one of those tracked files is edited.
        var start = 0;
        while (true)
        {
            var rest = relPath[start..];
            var length = rest.IndexOf('/');
            var segment = length < 0 ? rest : rest[..length];
            if (segment is "node_modules" or ".vs" or ".git")
                return true;
            if (segment is "bin" or "obj" && IsBesideAProjectFile(relPath[..Math.Max(0, start - 1)]))
                return true;
            if (length < 0)
                return false;
            start += length + 1;
        }
    }

    /// <summary>True when <paramref name="relDir"/> (a directory relative to the working tree,
    /// <c>/</c>-separated) holds a project file (<c>*.*proj</c> — csproj/fsproj/vbproj/…), i.e. a
    /// <c>bin</c>/<c>obj</c> beside it really is that project's build output. Memoized per directory
    /// so a build storm — the exact case this pre-filter exists for — pays the one filesystem probe
    /// once, not once per event.</summary>
    private bool IsBesideAProjectFile(ReadOnlySpan<char> relDir)
    {
        var cache = _buildOutputParents.GetAlternateLookup<ReadOnlySpan<char>>();
        if (cache.TryGetValue(relDir, out var cached))
            return cached;

        var result = ProbeForProjectFile(relDir.ToString(), _workDirRoot);
        cache[relDir] = result;
        return result;
    }

    private static bool ProbeForProjectFile(string dir, string workDir)
    {
        try
        {
            var full = dir.Length == 0 ? workDir : Path.Combine(workDir, dir.Replace('/', Path.DirectorySeparatorChar));
            return Directory.Exists(full) && Directory.EnumerateFiles(full, "*.*proj").Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// How many files git tracks here, read from the index header (<c>DIRC</c>, a version, then the
    /// big-endian entry count) — no walk of the working tree, so opening a tab never pays for one.
    /// With a split index the main index holds only the recent changes and the bulk sits in
    /// <c>sharedindex.&lt;hash&gt;</c> beside it, so the newest shared index counts too. 0 when
    /// there is no readable index (a fresh repo).
    /// <para>Trap: counting files on disk (capped at the threshold) cost seconds on a large tree,
    /// on the open path of every tab, just to reach this same decision.</para>
    /// </summary>
    internal static int TrackedFileCount(string gitDir)
    {
        var count = IndexHeaderEntryCount(Path.Combine(gitDir, "index"));
        try
        {
            var shared = new DirectoryInfo(gitDir).EnumerateFiles("sharedindex.*")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            if (shared is not null)
                count = Math.Max(count, IndexHeaderEntryCount(shared.FullName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable git dir: the main index's count stands.
        }
        return count;
    }

    private static int IndexHeaderEntryCount(string indexPath)
    {
        try
        {
            using var fs = new FileStream(indexPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> header = stackalloc byte[12];
            if (fs.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) != header.Length
                || header[0] != (byte)'D' || header[1] != (byte)'I' || header[2] != (byte)'R' || header[3] != (byte)'C')
                return 0;
            var count = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header[8..]);
            return count > int.MaxValue ? int.MaxValue : (int)count;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private void EnterDegradedMode(string reason)
    {
        IsDegraded = true;
        DegradedReason = reason;
        AppLog.Write(AppLogLevel.Warn, "watcher", $"file watcher degraded on {_context.DisplayName}: {reason}");
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        foreach (var w in _watchers)
        {
            try
            {
                w.EnableRaisingEvents = false;
                w.Dispose();
            }
            catch
            {
                // ignore
            }
        }

        _watchers.Clear();
        lock (_sync)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = null;
            _overflowFlushTimer?.Dispose();
            _overflowFlushTimer = null;
        }
        FlushSuppressedOverflows(force: true);
        return ValueTask.CompletedTask;
    }
}
