namespace Historia.Services.Workspace;

/// <inheritdoc cref="IRecentRepositoryService"/>
public sealed class RecentRepositoryService : IRecentRepositoryService, IAsyncDisposable
{
    private sealed record RecentEntry(string Path, string DisplayName, DateTimeOffset LastOpenedAt);

    private sealed record RecentFile
    {
        public int SchemaVersion { get; init; } = 1;
        public List<RecentEntry> Items { get; init; } = [];
    }

    private readonly ISettingsService _settings;
    private readonly AtomicFileWriter _writer;
    private readonly Func<string, bool> _pathExists;
    private readonly TimeSpan _checkTimeout;
    private readonly object _sync = new();
    private List<RecentEntry> _entries;
    private long _persistSequence;

    // Last known staleness per entry path. A path never checked is not stale until a check says so.
    private readonly Dictionary<string, bool> _stale = new(StringComparer.OrdinalIgnoreCase);

    // Paths whose check has not returned yet: one hung check per path, never a pile of them.
    private readonly HashSet<string> _checksInFlight = new(StringComparer.OrdinalIgnoreCase);
    private Task? _refreshTask;
    private bool _refreshWanted;
    private bool _disposed;

    public event EventHandler? Changed;

    public RecentRepositoryService(ISettingsService settings)
        : this(settings, SafeExists, TimeSpan.FromSeconds(2))
    {
    }

    /// <param name="pathExists">The folder check. It may block for as long as a dead network path takes.</param>
    /// <param name="checkTimeout">How long one path's check is awaited before its previous value is kept.</param>
    internal RecentRepositoryService(ISettingsService settings, Func<string, bool> pathExists, TimeSpan checkTimeout)
    {
        _settings = settings;
        _pathExists = pathExists;
        _checkTimeout = checkTimeout;
        _writer = new AtomicFileWriter(AppPaths.RecentRepositoriesFile);
        _entries = Load();
        _ = RefreshStalenessAsync();
    }

    /// <summary>
    /// Most-recent first. Staleness is the last value a background check produced; reading never
    /// touches the disk and never starts a check.
    /// </summary>
    public IReadOnlyList<RecentRepository> Items
    {
        get
        {
            lock (_sync)
            {
                return _entries
                    .OrderByDescending(e => e.LastOpenedAt)
                    .Select(e => new RecentRepository
                    {
                        Path = e.Path,
                        DisplayName = e.DisplayName,
                        LastOpenedAt = e.LastOpenedAt,
                        IsStale = _stale.GetValueOrDefault(e.Path),
                    })
                    .ToList();
            }
        }
    }

    /// <summary>
    /// Re-checks every entry's folder off the calling thread, each with its own timeout (a check
    /// that does not answer keeps the previous value), and raises <see cref="Changed"/> when any
    /// entry's staleness moved. A call during a running refresh makes it go around once more.
    /// </summary>
    public Task RefreshStalenessAsync()
    {
        lock (_sync)
        {
            if (_disposed)
                return Task.CompletedTask;
            _refreshWanted = true;
            return _refreshTask ??= Task.Run(RefreshLoopAsync);
        }
    }

    public Task AddAsync(string path, string displayName, CancellationToken ct = default)
    {
        var normalized = Normalize(path);
        var max = Math.Max(1, _settings.Current.MaxRecentRepositories);

        lock (_sync)
        {
            _entries.RemoveAll(e => string.Equals(Normalize(e.Path), normalized, StringComparison.OrdinalIgnoreCase));
            _entries.Insert(0, new RecentEntry(path, displayName, DateTimeOffset.Now));
            if (_entries.Count > max)
                _entries = _entries.OrderByDescending(e => e.LastOpenedAt).Take(max).ToList();
        }

        Persist();
        Changed?.Invoke(this, EventArgs.Empty);
        _ = RefreshStalenessAsync();
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string path, CancellationToken ct = default)
    {
        var normalized = Normalize(path);
        lock (_sync)
            _entries.RemoveAll(e => string.Equals(Normalize(e.Path), normalized, StringComparison.OrdinalIgnoreCase));

        Persist();
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        lock (_sync)
            _entries.Clear();
        Persist();
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    private async Task RefreshLoopAsync()
    {
        var exitedCleanly = false;
        try
        {
            while (true)
            {
                string[] paths;
                lock (_sync)
                {
                    if (!_refreshWanted || _disposed)
                    {
                        _refreshTask = null;
                        exitedCleanly = true;
                        return;
                    }
                    _refreshWanted = false;
                    paths = _entries.Select(e => e.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                }

                var results = await Task.WhenAll(paths.Select(CheckPathAsync)).ConfigureAwait(false);

                bool changed;
                lock (_sync)
                {
                    changed = ApplyResults(paths, results);
                    if (_disposed)
                        changed = false;
                }

                if (changed)
                    Changed?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            // A fault (a Changed handler throwing) must not leave a dead task that every later
            // refresh request joins. A clean exit already cleared it under the lock that decided
            // to stop, and a newer loop may have started since.
            if (!exitedCleanly)
            {
                lock (_sync)
                    _refreshTask = null;
            }
        }
    }

    /// <summary>Stores the answers of one refresh; the caller holds <c>_sync</c>.</summary>
    /// <returns>Whether any entry's staleness moved.</returns>
    private bool ApplyResults(string[] paths, bool?[] results)
    {
        var changed = false;
        for (var i = 0; i < paths.Length; i++)
        {
            var path = paths[i];
            if (results[i] is not { } stale || !_entries.Exists(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase)))
                continue;
            changed |= _stale.GetValueOrDefault(path) != stale;
            _stale[path] = stale;
        }

        // Entries that were removed take their cached value with them.
        foreach (var key in _stale.Keys.ToList())
        {
            if (!_entries.Exists(e => string.Equals(e.Path, key, StringComparison.OrdinalIgnoreCase)))
                _stale.Remove(key);
        }
        return changed;
    }

    public Task<bool?> IsStaleAsync(string path) => CheckPathAsync(path);

    /// <returns>Whether the folder is gone, or null when the check did not answer in time, failed, or an earlier one is still hanging.</returns>
    private async Task<bool?> CheckPathAsync(string path)
    {
        lock (_sync)
        {
            if (!_checksInFlight.Add(path))
                return null;
        }

        // A dedicated thread: a dead network share can hold the call for tens of seconds, which
        // must not eat into the pool.
        var check = Task.Factory.StartNew(() =>
        {
            try
            {
                return (bool?)!_pathExists(path);
            }
            catch
            {
                return null;
            }
            finally
            {
                lock (_sync)
                    _checksInFlight.Remove(path);
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        try
        {
            return await check.WaitAsync(_checkTimeout).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private List<RecentEntry> Load()
    {
        try
        {
            if (!File.Exists(AppPaths.RecentRepositoriesFile))
                return [];
            var json = File.ReadAllText(AppPaths.RecentRepositoriesFile);
            var file = JsonSerializer.Deserialize<RecentFile>(json, HistoriaJson.Options);
            return file?.Items ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private void Persist()
    {
        RecentFile file;
        long sequence;
        lock (_sync)
        {
            file = new RecentFile { Items = _entries.ToList() };
            sequence = ++_persistSequence;
        }
        _writer.QueueWrite(JsonSerializer.Serialize(file, HistoriaJson.Options), sequence);
    }

    private static bool SafeExists(string path)
    {
        try { return Directory.Exists(path); }
        catch { return false; }
    }

    private static string Normalize(string path)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch { return path; }
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
            _disposed = true;
        return _writer.DisposeAsync();
    }
}
