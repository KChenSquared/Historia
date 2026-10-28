namespace Historia.Tests;

/// <summary>In-memory <see cref="ISettingsService"/> — no file, no debounce.</summary>
public sealed class FakeSettingsService(AppSettings? initial = null) : ISettingsService
{
    public AppSettings Current { get; private set; } = initial ?? new AppSettings();

    public event EventHandler<SettingsChangedEventArgs>? Changed;

    public Task UpdateAsync(Func<AppSettings, AppSettings> mutate, CancellationToken ct = default)
    {
        var old = Current;
        Current = mutate(old);
        Changed?.Invoke(this, new SettingsChangedEventArgs(old, Current));
        return Task.CompletedTask;
    }

    public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>Runs everything inline on the calling thread; the coalesce timer fires its tick
/// synchronously on <see cref="IUiTimer.Start"/>. Deterministic for terminal-session tests.</summary>
public sealed class ImmediateUiDispatcher : IUiDispatcher
{
    public bool CheckAccess() => true;
    public void Post(Action action) => action();
    public IUiTimer CreateTimer(TimeSpan interval, Action tick) => new ImmediateTimer(tick);

    private sealed class ImmediateTimer(Action tick) : IUiTimer
    {
        public void Start() => tick();
        public void Stop() { }
        public void Dispose() { }
    }
}

/// <summary>Queues <see cref="Post"/> actions and the timer's <c>Start</c>/<c>Stop</c> calls
/// instead of running anything inline, so a test can control exactly when each fires and count
/// how many were queued in between — the thing <see cref="ImmediateUiDispatcher"/> can't show,
/// since it collapses a whole Post→Start→tick chain into one synchronous call.</summary>
public sealed class RecordingUiDispatcher : IUiDispatcher
{
    private readonly Queue<Action> _posted = new();

    public int PostCount { get; private set; }

    /// <summary>What <see cref="CheckAccess"/> reports; true by default, as if the caller were already
    /// on the UI thread. Set false to exercise the marshalling path.</summary>
    public bool OnUiThread { get; set; } = true;

    public bool CheckAccess() => OnUiThread;

    public void Post(Action action)
    {
        PostCount++;
        _posted.Enqueue(action);
    }

    /// <summary>Runs every queued post, in order, until none remain (a post can itself queue
    /// another — e.g. the terminal session's Start() call queues nothing further today, but
    /// draining to a fixed point keeps the helper honest either way).</summary>
    public void DrainPosted()
    {
        while (_posted.Count > 0)
            _posted.Dequeue()();
    }

    /// <summary>The most recently created timer — a session creates exactly one.</summary>
    public ManualTimer? LastTimer { get; private set; }

    public IUiTimer CreateTimer(TimeSpan interval, Action tick) => LastTimer = new ManualTimer(tick);

    /// <summary>A timer that only ticks when the test calls <see cref="Fire"/> — <c>Start</c>
    /// and <c>Stop</c> just record the running state.</summary>
    public sealed class ManualTimer(Action tick) : IUiTimer
    {
        public bool IsRunning { get; private set; }
        public int StartCount { get; private set; }

        public void Start()
        {
            StartCount++;
            IsRunning = true;
        }

        public void Stop() => IsRunning = false;
        public void Dispose() => IsRunning = false;
        public void Fire() => tick();
    }
}

/// <summary>A scripted <see cref="IPtyConnection"/>: records writes/resizes, replays bytes on
/// demand, and exits with a chosen code. Drives every terminal test.</summary>
public sealed class FakePtyConnection : IPtyConnection
{
    public readonly List<byte[]> Writes = [];
    public readonly List<(int Cols, int Rows)> Resizes = [];
    public int KillCount { get; private set; }
    public bool Disposed { get; private set; }

    public event Action<ReadOnlyMemory<byte>>? OutputReceived;
    public event Action<int>? Exited;

    public void Write(ReadOnlySpan<byte> data) => Writes.Add(data.ToArray());
    public void Resize(int columns, int rows) => Resizes.Add((columns, rows));
    public void Kill() => KillCount++;
    public void Dispose() => Disposed = true;

    public void EmitOutput(string text) => OutputReceived?.Invoke(System.Text.Encoding.UTF8.GetBytes(text));
    public void EmitOutput(byte[] bytes) => OutputReceived?.Invoke(bytes);
    public void EmitExit(int code) => Exited?.Invoke(code);

    public string AllWrittenText() => System.Text.Encoding.UTF8.GetString(Writes.SelectMany(w => w).ToArray());
}

/// <summary>Hands out <see cref="FakePtyConnection"/>s and records the <see cref="PtyStartInfo"/>
/// it was asked for. Never touches a real pseudoterminal.</summary>
public sealed class FakeTerminalBackend : ITerminalBackend
{
    public readonly List<PtyStartInfo> Starts = [];
    public readonly List<FakePtyConnection> Connections = [];
    public int StartDelayMs { get; set; }
    public Func<PtyStartInfo, Exception?>? FailWith { get; set; }

    public async Task<IPtyConnection> StartAsync(PtyStartInfo info, CancellationToken ct)
    {
        if (StartDelayMs > 0)
            await Task.Delay(StartDelayMs, ct).ConfigureAwait(false);

        if (FailWith?.Invoke(info) is { } ex)
            throw ex;

        Starts.Add(info);
        var conn = new FakePtyConnection();
        Connections.Add(conn);
        return conn;
    }
}

/// <summary>In-memory credential vault for tests — never touches Windows Credential Manager.</summary>
public sealed class FakeVault : ICredentialVaultService
{
    private readonly ConcurrentDictionary<string, string> _store = new();

    public Task StoreTokenAsync(string key, string token, CancellationToken ct = default)
    {
        _store[key] = token;
        return Task.CompletedTask;
    }

    public Task<string?> RetrieveTokenAsync(string key, CancellationToken ct = default)
        => Task.FromResult(_store.GetValueOrDefault(key));

    public Task DeleteTokenAsync(string key, CancellationToken ct = default)
    {
        _store.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public int Count => _store.Count;
}

/// <summary>A CLI service that reports Git as unavailable, so config writes become no-ops.</summary>
public sealed class NoGitCli : IGitCliService
{
    public bool IsGitAvailable => false;

    public Task<GitCliResult> RunAsync(GitCliRequest request, CancellationToken ct = default)
        => Task.FromResult(new GitCliResult(-1, "", "git not available", GitErrorKind.Unknown, TimeSpan.Zero));

    public Task<GitCliResult> RunAsync(GitCliRequest request, IProgress<GitProgress>? progress, Action<string>? onStdoutLine, CancellationToken ct = default)
        => RunAsync(request, ct);
}

/// <summary>Records every invocation and reports success without touching a real process —
/// for asserting exactly which git commands a gateway operation issued.</summary>
public sealed class RecordingGitCliService : IGitCliService
{
    public List<GitCliRequest> Requests { get; } = [];

    public bool IsGitAvailable => true;

    public Task<GitCliResult> RunAsync(GitCliRequest request, CancellationToken ct = default)
        => RunAsync(request, progress: null, onStdoutLine: null, ct);

    public Task<GitCliResult> RunAsync(
        GitCliRequest request, IProgress<GitProgress>? progress, Action<string>? onStdoutLine, CancellationToken ct = default)
    {
        Requests.Add(request);
        return Task.FromResult(new GitCliResult(0, "", "", GitErrorKind.None, TimeSpan.Zero));
    }
}

/// <summary>Delegates to a real CLI, records every request, and lets a test replace the result
/// of chosen requests (<see cref="Intercept"/> returns null to pass a request through).</summary>
public sealed class InterceptingGitCliService(IGitCliService inner) : IGitCliService
{
    private readonly ConcurrentQueue<GitCliRequest> _requests = new();

    public IReadOnlyList<GitCliRequest> Requests => _requests.ToList();
    public Func<GitCliRequest, GitCliResult?>? Intercept { get; set; }
    public bool IsGitAvailable => inner.IsGitAvailable;

    public Task<GitCliResult> RunAsync(GitCliRequest request, CancellationToken ct = default)
        => RunAsync(request, progress: null, onStdoutLine: null, ct);

    public Task<GitCliResult> RunAsync(
        GitCliRequest request, IProgress<GitProgress>? progress, Action<string>? onStdoutLine, CancellationToken ct = default)
    {
        _requests.Enqueue(request);
        return Intercept?.Invoke(request) is { } replaced
            ? Task.FromResult(replaced)
            : inner.RunAsync(request, progress, onStdoutLine, ct);
    }

    public static GitCliResult Failure(GitErrorKind kind = GitErrorKind.Unknown, int exitCode = 128)
        => new(exitCode, "", "fatal: injected failure", kind, TimeSpan.Zero);
}

/// <summary>An <see cref="IRepoIdentityService"/> that blocks resolution for a given repository
/// path until <see cref="Release"/> is called for that path (or the caller's token is cancelled)
/// — for tests that need a deterministic window where a specific context's identity resolution
/// is known to still be in flight, independent of any other context's.</summary>
public sealed class SlowIdentityService : IRepoIdentityService
{
    private readonly Dictionary<string, TaskCompletionSource> _gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    // RepositoryContext.RepositoryPath is libgit2's WorkingDirectory, which always ends in a
    // trailing separator and is a fully-qualified path — normalize so a caller's plain test path
    // (e.g. TestRepositoryBuilder.Path) keys to the same gate.
    private static string Norm(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private TaskCompletionSource GateFor(string path)
    {
        var key = Norm(path);
        lock (_sync)
        {
            if (!_gates.TryGetValue(key, out var tcs))
                _gates[key] = tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return tcs;
        }
    }

    public void Release(string path) => GateFor(path).TrySetResult();

    public async Task<RepoIdentityKey> ResolveAsync(RepositoryContext context, CancellationToken ct = default)
    {
        await GateFor(context.RepositoryPath).Task.WaitAsync(ct).ConfigureAwait(false);
        return new RepoIdentityKey(context.RepositoryPath);
    }
}

/// <summary>Delegates to a real CLI, records every request, and holds the first request that
/// <c>block</c> matches until <see cref="Release"/> — or until its own token is cancelled, which it
/// then reports as a cancelled run (as the real CLI does when it kills the process) and records in
/// <see cref="CancelObserved"/>.</summary>
public sealed class BlockingGitCliService(IGitCliService inner, Func<GitCliRequest, bool> block) : IGitCliService
{
    private readonly ConcurrentQueue<GitCliRequest> _requests = new();
    private int _blocked;

    public IReadOnlyList<GitCliRequest> Requests => _requests.ToList();
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource CancelObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool IsGitAvailable => inner.IsGitAvailable;

    public Task<GitCliResult> RunAsync(GitCliRequest request, CancellationToken ct = default)
        => RunAsync(request, progress: null, onStdoutLine: null, ct);

    public async Task<GitCliResult> RunAsync(
        GitCliRequest request, IProgress<GitProgress>? progress, Action<string>? onStdoutLine, CancellationToken ct = default)
    {
        _requests.Enqueue(request);
        if (block(request) && Interlocked.Exchange(ref _blocked, 1) == 0)
        {
            Entered.TrySetResult();
            try
            {
                await Release.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                CancelObserved.TrySetResult();
                return new GitCliResult(-1, "", "", GitErrorKind.Cancelled, TimeSpan.Zero);
            }
        }
        return await inner.RunAsync(request, progress, onStdoutLine, ct).ConfigureAwait(false);
    }

    public static bool IsIn(GitCliRequest request, string repositoryPath) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.WorkingDirectory)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath)), StringComparison.OrdinalIgnoreCase);
}

/// <summary>An <see cref="IDialogService"/> that never opens a window. Each method answers from a
/// script queued with the <c>Enqueue…</c> methods, falling back to the safe answer (no, cancel,
/// null) once the script runs out. Every call is logged; destructive confirmations are also
/// listed on their own so a test can assert that a destructive action asked first.</summary>
public sealed class FakeDialogService : IDialogService
{
    /// <summary>One call to any dialog method. <see cref="Detail"/> is the message or effect text.</summary>
    public sealed record Call(string Method, string Title, string? Detail);

    /// <summary>One <see cref="ConfirmDestructiveAsync"/> call.</summary>
    public sealed record DestructiveCall(string Title, string Effect, string ConfirmLabel);

    private readonly Queue<string?> _folders = new();
    private readonly Queue<string?> _files = new();
    private readonly Queue<bool> _confirms = new();
    private readonly Queue<bool> _destructiveConfirms = new();
    private readonly Queue<int> _choices = new();
    private readonly Queue<string?> _prompts = new();

    public List<Call> Calls { get; } = [];
    public List<DestructiveCall> ConfirmDestructiveCalls { get; } = [];

    public FakeDialogService EnqueueFolder(string? answer) { _folders.Enqueue(answer); return this; }
    public FakeDialogService EnqueueFile(string? answer) { _files.Enqueue(answer); return this; }
    public FakeDialogService EnqueueConfirm(bool answer) { _confirms.Enqueue(answer); return this; }
    public FakeDialogService EnqueueConfirmDestructive(bool answer) { _destructiveConfirms.Enqueue(answer); return this; }
    public FakeDialogService EnqueueChoice(int answer) { _choices.Enqueue(answer); return this; }
    public FakeDialogService EnqueuePrompt(string? answer) { _prompts.Enqueue(answer); return this; }

    public Task<string?> PickFolderAsync(string title, string? initialDirectory = null)
    {
        Calls.Add(new Call(nameof(PickFolderAsync), title, initialDirectory));
        return Task.FromResult(_folders.Count > 0 ? _folders.Dequeue() : null);
    }

    public Task<string?> PickFileAsync(string title, string filter, string? initialDirectory = null)
    {
        Calls.Add(new Call(nameof(PickFileAsync), title, filter));
        return Task.FromResult(_files.Count > 0 ? _files.Dequeue() : null);
    }

    public Task<bool> ConfirmAsync(string title, string message, bool destructive = false,
        string confirmLabel = "OK", bool defaultToCancel = false, DialogIcon icon = DialogIcon.None)
    {
        Calls.Add(new Call(nameof(ConfirmAsync), title, message));
        return Task.FromResult(_confirms.Count > 0 && _confirms.Dequeue());
    }

    public Task<bool> ConfirmDestructiveAsync(string title, string effect, string confirmLabel)
    {
        Calls.Add(new Call(nameof(ConfirmDestructiveAsync), title, effect));
        ConfirmDestructiveCalls.Add(new DestructiveCall(title, effect, confirmLabel));
        return Task.FromResult(_destructiveConfirms.Count > 0 && _destructiveConfirms.Dequeue());
    }

    public Task<int> ChooseAsync(string title, string message, IReadOnlyList<string> options,
        int defaultIndex = 0, int destructiveIndex = -1, DialogIcon icon = DialogIcon.None)
    {
        Calls.Add(new Call(nameof(ChooseAsync), title, message));
        return Task.FromResult(_choices.Count > 0 ? _choices.Dequeue() : -1);
    }

    public Task ShowMessageAsync(string title, string message)
    {
        Calls.Add(new Call(nameof(ShowMessageAsync), title, message));
        return Task.CompletedTask;
    }

    public Task<string?> PromptAsync(string title, string message, string? defaultValue = null,
        string confirmLabel = "OK", bool primaryConfirm = false)
    {
        Calls.Add(new Call(nameof(PromptAsync), title, message));
        return Task.FromResult(_prompts.Count > 0 ? _prompts.Dequeue() : null);
    }
}

/// <summary>An <see cref="INotificationService"/> that keeps what was published and raises
/// <see cref="Changed"/> on every publish and dismiss, without coalescing or timing.</summary>
public sealed class FakeNotificationService : INotificationService
{
    private readonly List<Notification> _current = [];

    /// <summary>Every notification ever published, in order, including dismissed ones.</summary>
    public List<Notification> Published { get; } = [];

    public IReadOnlyList<Notification> Current => _current;

    public event EventHandler<NotificationsChangedEventArgs>? Changed;

    public Notification Publish(NotificationSeverity severity, string message, string? details = null,
        IReadOnlyList<NotificationAction>? actions = null)
    {
        var notification = new Notification
        {
            Id = Guid.NewGuid(),
            Severity = severity,
            Message = message,
            Details = details,
            Actions = actions ?? [],
            CreatedAt = DateTimeOffset.UtcNow,
        };
        Published.Add(notification);
        _current.Add(notification);
        Changed?.Invoke(this, new NotificationsChangedEventArgs([.. _current]));
        return notification;
    }

    public void Dismiss(Guid id)
    {
        if (_current.RemoveAll(n => n.Id == id) > 0)
            Changed?.Invoke(this, new NotificationsChangedEventArgs([.. _current]));
    }
}

/// <summary>An <see cref="IClipboardService"/> that records the text it was given.</summary>
public sealed class FakeClipboardService : IClipboardService
{
    public List<string> Copied { get; } = [];

    public string? Last => Copied.Count > 0 ? Copied[^1] : null;

    public Task SetTextAsync(string text)
    {
        Copied.Add(text);
        return Task.CompletedTask;
    }
}

/// <summary>An <see cref="IRemoteDialogs"/> that answers from values set by the test and records
/// what it was asked.</summary>
public sealed class FakeRemoteDialogs : IRemoteDialogs
{
    public AddRemoteResult? AddAnswer { get; set; }
    public EditRemoteResult? EditAnswer { get; set; }

    public List<IReadOnlyList<RemoteInfo>> AddCalls { get; } = [];
    public List<RemoteInfo> EditCalls { get; } = [];

    public Task<AddRemoteResult?> ShowAddAsync(IReadOnlyList<RemoteInfo> existing)
    {
        AddCalls.Add(existing);
        return Task.FromResult(AddAnswer);
    }

    public Task<EditRemoteResult?> ShowEditAsync(RemoteInfo remote, IReadOnlyCollection<string> otherNames,
        IReadOnlyDictionary<string, string> urlOwners)
    {
        EditCalls.Add(remote);
        return Task.FromResult(EditAnswer);
    }
}

/// <summary>An in-memory <see cref="ICommitDraftStore"/> that exposes its contents to the test.</summary>
public sealed class FakeCommitDraftStore : ICommitDraftStore
{
    public Dictionary<string, string> Drafts { get; } = new(StringComparer.Ordinal);

    public string Get(string repoKey) => Drafts.GetValueOrDefault(repoKey, "");

    public void Set(string repoKey, string? draft)
    {
        if (string.IsNullOrEmpty(draft))
            Drafts.Remove(repoKey);
        else
            Drafts[repoKey] = draft;
    }
}

/// <summary>An in-memory <see cref="ICmdrQueueService"/> that raises <see cref="Changed"/> inline
/// and records every call in order, so a test can check what was marked or removed and when.</summary>
public sealed class FakeCmdrQueueService : ICmdrQueueService
{
    private readonly List<CmdrQueueItem> _items = [];

    /// <summary>Every call, in order: <c>Enqueue:id</c>, <c>MarkSeen:id</c>, <c>Remove:id</c>.</summary>
    public List<string> Calls { get; } = [];

    public IReadOnlyList<CmdrQueueItem> Items => [.. _items];

    public int UnseenCount => _items.Count(i => !i.Seen);

    public event EventHandler? Changed;

    public void Enqueue(CmdrQueueItem item)
    {
        Calls.Add($"Enqueue:{item.Id}");
        _items.Add(item);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void MarkSeen(string id)
    {
        Calls.Add($"MarkSeen:{id}");
        var at = _items.FindIndex(i => i.Id == id);
        if (at < 0 || _items[at].Seen)
            return;
        _items[at] = _items[at] with { Seen = true };
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(string id)
    {
        Calls.Add($"Remove:{id}");
        if (_items.RemoveAll(i => i.Id == id) > 0)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The number of handlers attached to <see cref="Changed"/>.</summary>
    public int ChangedSubscriberCount => Changed?.GetInvocationList().Length ?? 0;
}

/// <summary>An <see cref="IControlNavigator"/> that records each target and answers from a script:
/// by default it succeeds at once; <see cref="Next"/> lets a test hold a navigation open or fail it.</summary>
public sealed class FakeControlNavigator : IControlNavigator
{
    /// <summary>One navigation: the target and the token it was given.</summary>
    public sealed record Call(ControlTarget Target, CancellationToken Token);

    private readonly Queue<Func<ControlTarget, CancellationToken, Task<(ControlResponse, string?)>>> _script = new();

    public List<Call> Calls { get; } = [];

    /// <summary>Runs before each navigation is answered; a test can inspect state at that moment.</summary>
    public Action<ControlTarget>? OnNavigate { get; set; }

    /// <summary>Queues the answer for the next navigation.</summary>
    public FakeControlNavigator Next(Func<ControlTarget, CancellationToken, Task<(ControlResponse, string?)>> answer)
    {
        _script.Enqueue(answer);
        return this;
    }

    public Task<(ControlResponse Response, string? RepoName)> NavigateAsync(ControlTarget target, CancellationToken ct)
    {
        Calls.Add(new Call(target, ct));
        OnNavigate?.Invoke(target);
        if (_script.Count > 0)
            return _script.Dequeue()(target, ct);
        return Task.FromResult<(ControlResponse, string?)>((new ControlResponse { Ok = true, Repo = target.RepoPath }, "repo"));
    }
}
