namespace Historia.Services.Terminal;

/// <inheritdoc cref="ITerminalSessionService"/>
public sealed class TerminalSessionService : ITerminalSessionService, IDisposable
{
    private const int DefaultColumns = 80;
    private const int DefaultRows = 24;

    // Upper bound on how long a shell spawn may take before we give up, free the origin key,
    // and surface an error — so a backend that never returns (a wedged ConPTY, an unreachable
    // working directory) cannot leave that repo's terminal permanently unopenable.
    private static readonly TimeSpan SpawnTimeout = TimeSpan.FromSeconds(20);

    private readonly ITerminalBackend _backend;
    private readonly ISettingsService _settings;
    private readonly IUiDispatcher _dispatcher;
    private readonly TerminalHostEnvironment _host;
    private readonly Func<TerminalOrigin, string?> _sshKeyResolver;
    private readonly INotificationService? _notifications;

    private readonly object _sync = new();
    private readonly List<TerminalSession> _sessions = [];
    private readonly Dictionary<TerminalOriginKey, Task<TerminalSession>> _inFlight = [];
    private TerminalSession? _focused;
    private bool _disposed;

    public event EventHandler? SessionsChanged;
    public event EventHandler? FocusedSessionChanged;
    public event EventHandler<TerminalSession>? SessionExited;
    public event EventHandler? ActivationRequested;
    public event EventHandler<TerminalClipboardWriteEventArgs>? ClipboardWriteRequested;

    public TerminalSessionService(
        ITerminalBackend backend,
        ISettingsService settings,
        IUiDispatcher dispatcher,
        TerminalHostEnvironment? host = null,
        Func<TerminalOrigin, string?>? sshKeyResolver = null,
        INotificationService? notifications = null,
        IGitExecutableLocator? locator = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _notifications = notifications;
        _host = host ?? (locator is null ? TerminalHostEnvironment.System : TerminalHostEnvironment.CreateSystem(() => locator.Current));
        _sshKeyResolver = sshKeyResolver ?? (_ => null);
    }

    public IReadOnlyList<TerminalSession> Sessions
    {
        get { lock (_sync) return _sessions.ToArray(); }
    }

    public TerminalSession? Focused
    {
        get { lock (_sync) return _focused; }
    }

    public Task<TerminalSession> OpenOrFocusAsync(TerminalOrigin origin, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(origin);

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var existing = _sessions.FirstOrDefault(s => s.Origin.Key == origin.Key);
            if (existing is not null)
            {
                SetFocusedNoLock(existing);
                RaiseActivationRequested();
                return Task.FromResult(existing);
            }

            // Re-entrancy guard (trap): a double-click must produce one process.
            // Return a pending start only while it is genuinely in-flight. A completed entry
            // is always stale: if successful it is already in _sessions above, and if faulted
            // or cancelled it must not block retrying.
            if (_inFlight.TryGetValue(origin.Key, out var pending))
            {
                if (!pending.IsCompleted)
                    return pending;
                _inFlight.Remove(origin.Key);
            }

            var task = StartAsync(origin, ct);
            // When StartAsync completes synchronously (e.g. Porta.Pty on Windows returning
            // Task.FromResult), its finally block has already run and removed the key before
            // we reached here. Only register genuine async tasks. For async starts, StartAsync's
            // finally will acquire _sync after this lock is released and clean up.
            if (!task.IsCompleted)
                _inFlight[origin.Key] = task;
            return task;
        }
    }

    public Task<TerminalSession> OpenNewAsync(TerminalOrigin origin, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(origin);
        lock (_sync)
            ObjectDisposedException.ThrowIf(_disposed, this);

        // Deliberately bypasses the reuse / in-flight checks — an explicit user request for a
        // second shell. StartAsync focuses it and raises ActivationRequested.
        return StartAsync(origin, ct);
    }

    public void RequestActivation() => RaiseActivationRequested();

    private async Task<TerminalSession> StartAsync(TerminalOrigin origin, CancellationToken ct)
    {
        try
        {
            var settings = _settings.Current;

            var shell = ShellResolver.Resolve(
                settings.TerminalShellPath,
                settings.TerminalShellArgs,
                _host.Platform,
                _host.FileExists,
                _host.GetEnvironmentVariable,
                _host.FindOnPath,
                _host.GetPasswdShell);

            var resolvedDir = TerminalStartDirectory.Resolve(
                origin.WorkingDirectory,
                _host.HomeDirectory,
                _host.CurrentDirectory,
                _host.DirectoryExists);

            string? notice = null;
            if (!string.IsNullOrWhiteSpace(origin.WorkingDirectory)
                && !_host.DirectoryExists(origin.WorkingDirectory)
                && !string.Equals(resolvedDir, origin.WorkingDirectory, StringComparison.Ordinal))
            {
                notice = $"[Historia] start directory not available: {origin.WorkingDirectory} — using {resolvedDir ?? "the shell's default"}";
            }

            var env = TerminalEnvironment.Build(
                _host.GetInheritedEnvironment(),
                _sshKeyResolver(origin),
                _host.ExecutablePath,
                _host.Version);

            var startInfo = new PtyStartInfo
            {
                ShellPath = shell.Path,
                Arguments = shell.Arguments,
                WorkingDirectory = resolvedDir,
                Environment = env,
                Columns = DefaultColumns,
                Rows = DefaultRows,
            };

            using var spawnCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var spawnTask = _backend.StartAsync(startInfo, spawnCts.Token);
            var settled = await Task.WhenAny(spawnTask, Task.Delay(SpawnTimeout, ct)).ConfigureAwait(false);
            if (!ReferenceEquals(settled, spawnTask))
            {
                // The delay task won. Cancel the spawn best-effort, let the orphan clean up its
                // own connection if it ever completes, and free the key so a retry is possible.
                spawnCts.Cancel();
                _ = spawnTask.ContinueWith(
                    t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); },
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                // If the caller's own token fired, this is a cancellation, not a hang — throw
                // OperationCanceledException so it stays silent (no "timed out" error toast).
                ct.ThrowIfCancellationRequested();
                throw new TimeoutException(
                    $"Timed out after {SpawnTimeout.TotalSeconds:0}s starting a shell for {origin.Title}.");
            }
            var pty = await spawnTask.ConfigureAwait(false);

            var session = new TerminalSession(
                origin, pty, _dispatcher,
                DefaultColumns, DefaultRows,
                AppSettings.ClampTerminalScrollback(settings.TerminalScrollbackLines),
                notice,
                settings.TerminalKittyKeyboard);

            session.StateChanged += OnSessionStateChanged;
            session.ClipboardWriteRequested += OnSessionClipboardWrite;

            lock (_sync)
            {
                _sessions.Add(session);
                SetFocusedNoLock(session);
            }

            RaiseSessionsChanged();
            RaiseActivationRequested();
            return session;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fire-and-forget is the norm at every call site, so a failed spawn would otherwise
            // be completely silent — the user clicks Terminal and nothing happens.
            _notifications?.Publish(NotificationSeverity.Error,
                $"Couldn't open a terminal for {origin.Title}: {ex.Message}");
            throw;
        }
        finally
        {
            lock (_sync)
                _inFlight.Remove(origin.Key);
        }
    }

    private void OnSessionStateChanged(object? sender, EventArgs e)
    {
        if (sender is not TerminalSession session)
            return;
        if (session.State is TerminalSessionState.Exited or TerminalSessionState.Failed)
            SessionExited?.Invoke(this, session);
    }

    // Already on the UI thread: the session posted it. The setting is read per write, so
    // turning it off stops the next write in a terminal that is already open.
    private void OnSessionClipboardWrite(object? sender, TerminalClipboardWriteEventArgs e)
    {
        if (_settings.Current.TerminalAllowClipboardWrite)
            ClipboardWriteRequested?.Invoke(this, e);
    }

    public void Focus(TerminalSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_sync)
        {
            if (!_sessions.Contains(session))
                return;
            SetFocusedNoLock(session);
        }
    }

    private void SetFocusedNoLock(TerminalSession session)
    {
        if (ReferenceEquals(_focused, session))
            return;
        _focused = session;
        _dispatcher.Post(() => FocusedSessionChanged?.Invoke(this, EventArgs.Empty));
    }

    public Task CloseAsync(Guid sessionId)
    {
        TerminalSession? toClose;
        bool focusedChanged = false;
        lock (_sync)
        {
            toClose = _sessions.FirstOrDefault(s => s.Id == sessionId);
            if (toClose is null)
                return Task.CompletedTask;

            _sessions.Remove(toClose);
            if (ReferenceEquals(_focused, toClose))
            {
                _focused = _sessions.LastOrDefault();
                focusedChanged = true;
            }
        }

        toClose.StateChanged -= OnSessionStateChanged;
        toClose.ClipboardWriteRequested -= OnSessionClipboardWrite;
        toClose.Kill();
        toClose.Dispose();

        RaiseSessionsChanged();
        if (focusedChanged)
            _dispatcher.Post(() => FocusedSessionChanged?.Invoke(this, EventArgs.Empty));
        return Task.CompletedTask;
    }

    public Task CloseForTabAsync(string tabId)
    {
        if (string.IsNullOrEmpty(tabId))
            return Task.CompletedTask;

        List<TerminalSession> matches;
        lock (_sync)
        {
            matches = _sessions.Where(s => s.Origin.TabId == tabId).ToList();
        }

        foreach (var s in matches)
            _ = CloseAsync(s.Id);

        if (matches.Count > 0)
        {
            var names = string.Join(", ", matches.Select(s => s.ReportedTitle ?? s.Origin.Title));
            _notifications?.Publish(NotificationSeverity.Info,
                matches.Count == 1
                    ? $"Closed the terminal session for {names} — its shell was terminated."
                    : $"Closed {matches.Count} terminal sessions ({names}) — their shells were terminated.");
        }

        return Task.CompletedTask;
    }

    private void RaiseSessionsChanged()
        => _dispatcher.Post(() => SessionsChanged?.Invoke(this, EventArgs.Empty));

    private void RaiseActivationRequested()
        => _dispatcher.Post(() => ActivationRequested?.Invoke(this, EventArgs.Empty));

    public void Dispose()
    {
        Historia.Diagnostics.PerfLog.Mark("TerminalSessionService.Dispose start");
        List<TerminalSession> toDispose;
        lock (_sync)
        {
            if (_disposed)
            {
                Historia.Diagnostics.PerfLog.Mark("TerminalSessionService.Dispose already disposed");
                return;
            }
            _disposed = true;
            toDispose = _sessions.ToList();
            _sessions.Clear();
            _focused = null;
        }
        Historia.Diagnostics.PerfLog.Mark($"TerminalSessionService.Dispose {toDispose.Count} session(s)");

        // Kill all sessions first so ConPTY pipe handles start closing simultaneously,
        // then dispose in parallel. Sequential disposal would make shutdown slow
        // (each PtyIoPump.Dispose waits for its background threads to join).
        foreach (var s in toDispose)
        {
            s.StateChanged -= OnSessionStateChanged;
            s.ClipboardWriteRequested -= OnSessionClipboardWrite;
            try { s.Kill(); } catch { /* best effort */ }
        }

        using (Historia.Diagnostics.PerfLog.Scope("TerminalSessionService Parallel.ForEach dispose"))
        {
            Parallel.ForEach(toDispose, s =>
            {
                try { s.Dispose(); } catch { /* best effort */ }
            });
        }
        Historia.Diagnostics.PerfLog.Mark("TerminalSessionService.Dispose done");
    }
}
