namespace Historia.Services.Git;

/// <inheritdoc cref="IGitCliService"/>
public sealed class GitCliService : IGitCliService
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Each pipe reader is a parked thread for the life of the process; the stack only
    /// has to hold a line loop.</summary>
    private const int PipeThreadStackBytes = 256 * 1024;

    /// <summary>A stdin payload up to this many characters fits the pipe buffer and is written
    /// inline; a larger one goes on its own thread so a slow consumer never blocks the caller.</summary>
    private const int InlineStdinMaxChars = 2048;

    /// <summary>Captured stderr keeps its tail only; progress lines are still parsed as they arrive.</summary>
    private const long MaxStandardErrorBytes = 1024 * 1024;

    private static readonly TimeSpan PipePollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>How many <see cref="GitCliPriority.Background"/> processes may run at once across
    /// the whole app, whatever the number of repositories or <see cref="GitCliService"/> instances.</summary>
    private const int MaxBackgroundProcesses = 2;

    private static readonly TimeSpan BackgroundSlotWarning = TimeSpan.FromSeconds(60);

    private static readonly SemaphoreSlim BackgroundSlots = new(MaxBackgroundProcesses, MaxBackgroundProcesses);

    private readonly IGitExecutableLocator _locator;

    public GitCliService(IGitExecutableLocator locator)
    {
        _locator = locator;
    }

    public bool IsGitAvailable => _locator.Current.Found;

    /// <summary>How long every unfinished pipe reader must have sat blocked inside its read, with
    /// git already gone, before the call gives up on the pipes and returns what it captured. A
    /// leftover child process (ssh ControlPersist, a credential helper) can hold a pipe open long
    /// after git exits.</summary>
    internal TimeSpan PipeDrainGrace { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Delays the start of every pipe reader thread — a seam for tests that need output to
    /// wait in the pipe while git exits.</summary>
    internal TimeSpan ReaderStartDelay { get; init; } = TimeSpan.Zero;

    /// <summary>Called right after every git process has started and its priority has been applied,
    /// on the thread that started it — a seam for tests that inspect the live process.</summary>
    internal Action<Process>? ProcessStarted { get; init; }

    public Task<GitCliResult> RunAsync(GitCliRequest request, CancellationToken ct = default)
        => RunAsync(request, progress: null, onStdoutLine: null, ct);

    public async Task<GitCliResult> RunAsync(
        GitCliRequest request,
        IProgress<GitProgress>? progress,
        Action<string>? onStdoutLine,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Resolving git, building the process and Process.Start itself cost milliseconds that must
        // not land on a UI thread. Task.Yield would post straight back to a UI context's queue;
        // ForceYielding schedules the continuation on the thread pool instead.
        if (SynchronizationContext.Current is not null)
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);

        await _locator.WhenResolvedAsync(ct).ConfigureAwait(false);

        if (!AppLog.Enabled)
            return await RunCoreAsync(request, progress, onStdoutLine, ct).ConfigureAwait(false);

        var op = AppLog.Operation("git-cli", $"git {string.Join(' ', request.Arguments)}  (cwd={request.WorkingDirectory})");
        try
        {
            var result = await RunCoreAsync(request, progress, onStdoutLine, ct).ConfigureAwait(false);
            op.Outcome($"exit={result.ExitCode} kind={result.ErrorKind} ({result.Duration.TotalMilliseconds:F0}ms)");
            if (result.ExitCode != 0 && !string.IsNullOrWhiteSpace(result.StandardError))
                AppLog.Write(AppLogLevel.Warn, "git-cli", GitStderr.SummaryLine(result.StandardError));
            return result;
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
        finally
        {
            op.Dispose();
        }
    }


    private async Task<GitCliResult> RunCoreAsync(
        GitCliRequest request,
        IProgress<GitProgress>? progress,
        Action<string>? onStdoutLine,
        CancellationToken ct)
    {
        var raw = request.RawStandardOutput;
        if (raw && (request.StreamOutput || onStdoutLine is not null))
            throw new ArgumentException("Raw stdout capture can't also stream stdout lines.", nameof(request));

        var git = _locator.Current;
        if (!git.Found || git.Path is null)
        {
            return new GitCliResult(-1, string.Empty,
                git.NotFoundReason ?? "Git was not found. Set its location in Settings.",
                GitErrorKind.Unknown, TimeSpan.Zero);
        }

        // Read here, after the hop off the caller's thread: the write scope is an ambient flag that
        // flows with the execution context.
        var inWriteScope = GitWriteScope.IsActive;

        // Only background work outside a write waits for a slot: a write, an interactive request
        // and a network request never queue behind the cap.
        if (request.Priority != GitCliPriority.Background || inWriteScope)
            return await StartAndRunAsync(request, git.Path, inWriteScope, progress, onStdoutLine, ct).ConfigureAwait(false);

        try
        {
            await BackgroundSlots.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new GitCliResult(-1, string.Empty, string.Empty, GitErrorKind.Cancelled, TimeSpan.Zero);
        }

        // A hung background process would hold its slot for good and silently stall all background
        // git work; the log says which command it is.
        using var slotWatchdog = new Timer(
            static state => AppLog.Write(AppLogLevel.Warn, "git-cli",
                $"A background git slot has been held for over {BackgroundSlotWarning.TotalSeconds:0} s by: {state}"),
            $"git {string.Join(' ', request.Arguments)}", BackgroundSlotWarning, Timeout.InfiniteTimeSpan);
        try
        {
            return await StartAndRunAsync(request, git.Path, inWriteScope, progress, onStdoutLine, ct).ConfigureAwait(false);
        }
        finally
        {
            BackgroundSlots.Release();
        }
    }

    private async Task<GitCliResult> StartAndRunAsync(
        GitCliRequest request,
        string gitPath,
        bool inWriteScope,
        IProgress<GitProgress>? progress,
        Action<string>? onStdoutLine,
        CancellationToken ct)
    {
        var raw = request.RawStandardOutput;

        var psi = new ProcessStartInfo(gitPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
            WorkingDirectory = request.WorkingDirectory,
            RedirectStandardInput = request.StandardInput is not null,
        };

        foreach (var arg in GitCliProcessSetup.DeterministicPrefix)
            psi.ArgumentList.Add(arg);
        foreach (var arg in request.Arguments)
            psi.ArgumentList.Add(arg);

        GitCliProcessSetup.ComposeEnvironment(psi, request.Profile, request.ExtraEnvironment);

        var neverKill = request.CancelPolicy == GitCliCancelPolicy.NeverAfterStart || inWriteScope;

        if (ct.IsCancellationRequested)
            return new GitCliResult(-1, string.Empty, string.Empty, GitErrorKind.Cancelled, TimeSpan.Zero);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        var stopwatch = Stopwatch.StartNew();

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new GitCliResult(-1, string.Empty, ex.Message, GitErrorKind.Unknown, stopwatch.Elapsed);
        }

        if (request.Priority is GitCliPriority.Background or GitCliPriority.BackgroundNetwork)
        {
            // Best-effort: the process may already have exited (or the platform may refuse the
            // change) by the time this runs — never let a priority tweak fail the invocation.
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
            catch { }
        }

        ProcessStarted?.Invoke(process);

        using var timeoutCts = new CancellationTokenSource();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        if (request.Timeout != Timeout.InfiniteTimeSpan && request.Timeout > TimeSpan.Zero)
            timeoutCts.CancelAfter(request.Timeout);

        var describe = $"git {string.Join(' ', request.Arguments)}";
        var cancelled = false;

        // Registered before any pipe work, so a stuck stdin write can be cancelled too.
        await using (linkedCts.Token.Register(() =>
        {
            if (neverKill)
            {
                if (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
                    AppLog.Write(AppLogLevel.Warn, "git-cli",
                        $"{describe}: timeout of {request.Timeout} elapsed; a started write is never killed, waiting for it");
                return;
            }

            cancelled = true;
            GitCliProcessSetup.TryKill(process);
        }))
        {
            // Every redirected pipe has its own thread, all draining while git runs — a blocking
            // read on a pool thread would park it for the life of the process, and a few dozen
            // concurrent git calls would starve the pool. In raw mode stdout is copied as bytes
            // instead of being read line by line.
            var gate = new object();
            var closed = false;
            var outputTruncated = false;
            long lineBufferedBytes = 0;
            var stdout = new StringBuilder();
            var rawStdout = raw ? new MemoryStream() : null;
            var stderrLines = new Queue<string>();
            long stderrBytes = 0;
            var pipes = new List<PipeThread>(3);

            void ReadStdoutLines(StreamReader reader)
            {
                while (reader.ReadLine() is { } line)
                {
                    var lineBytes = Utf8NoBom.GetByteCount(line) + Environment.NewLine.Length;
                    var overflow = false;
                    lock (gate)
                    {
                        if (closed) return;
                        if (outputTruncated) continue; // Keep draining so git never blocks on a full pipe.

                        if (request.MaxStandardOutputBytes is { } max && lineBufferedBytes + lineBytes > max)
                        {
                            outputTruncated = true;
                            overflow = true;
                        }
                        else
                        {
                            lineBufferedBytes += lineBytes;
                            stdout.AppendLine(line);
                        }
                    }

                    // A started write is never killed: it keeps draining instead.
                    if (overflow)
                    {
                        if (!neverKill)
                            GitCliProcessSetup.TryKill(process);
                    }
                    else
                        onStdoutLine?.Invoke(line);
                }
            }

            void ReadStdoutRaw(Stream stream)
            {
                var buffer = new byte[16 * 1024];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    var overflow = false;
                    lock (gate)
                    {
                        if (closed) return;
                        if (outputTruncated) continue;

                        var toWrite = read;
                        if (request.MaxStandardOutputBytes is { } max)
                        {
                            toWrite = (int)Math.Min(read, Math.Max(max - rawStdout!.Length, 0));
                            if (toWrite < read)
                            {
                                outputTruncated = true;
                                overflow = true;
                            }
                        }

                        rawStdout!.Write(buffer, 0, toWrite);
                    }

                    if (overflow && !neverKill)
                        GitCliProcessSetup.TryKill(process);
                }
            }

            void ReadStderr(StreamReader reader)
            {
                while (reader.ReadLine() is { } line)
                {
                    lock (gate)
                    {
                        if (closed) return;
                        stderrLines.Enqueue(line);
                        stderrBytes += Utf8NoBom.GetByteCount(line) + Environment.NewLine.Length;
                        while (stderrBytes > MaxStandardErrorBytes && stderrLines.Count > 1)
                            stderrBytes -= Utf8NoBom.GetByteCount(stderrLines.Dequeue()) + Environment.NewLine.Length;
                    }

                    if (progress is not null && GitProgressParser.TryParse(line, out var gp))
                        progress.Report(gp);
                }
            }

            var stdoutPipe = new BlockTrackingStream(process.StandardOutput.BaseStream);
            if (raw)
            {
                pipes.Add(PipeThread.Start("git-stdout", process, neverKill, stdoutPipe, ReaderStartDelay,
                    () => ReadStdoutRaw(stdoutPipe)));
            }
            else
            {
                var reader = new StreamReader(stdoutPipe, Utf8NoBom, detectEncodingFromByteOrderMarks: true, bufferSize: 16 * 1024);
                pipes.Add(PipeThread.Start("git-stdout", process, neverKill, stdoutPipe, ReaderStartDelay,
                    () => ReadStdoutLines(reader)));
            }

            var stderrPipe = new BlockTrackingStream(process.StandardError.BaseStream);
            var stderrReader = new StreamReader(stderrPipe, Utf8NoBom, detectEncodingFromByteOrderMarks: true, bufferSize: 16 * 1024);
            pipes.Add(PipeThread.Start("git-stderr", process, neverKill, stderrPipe, ReaderStartDelay,
                () => ReadStderr(stderrReader)));

            if (request.StandardInput is { } stdin)
            {
                var writer = process.StandardInput;
                // Git expects LF-terminated patch text on stdin, not the platform newline.
                writer.NewLine = "\n";

                if (stdin.Length <= InlineStdinMaxChars)
                    WriteStdin(writer, stdin);
                else
                    pipes.Add(PipeThread.Start("git-stdin", process, neverKill, tracked: null, TimeSpan.Zero,
                        () => WriteStdin(writer, stdin)));
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await DrainPipesAsync(pipes, Stopwatch.GetTimestamp(), describe).ConfigureAwait(false);

            stopwatch.Stop();

            string stderrText;
            string? stdoutText = null;
            byte[]? stdoutBytes = null;
            bool truncated;
            lock (gate)
            {
                // From here any reader still stuck in a read drops what it sees.
                closed = true;
                truncated = outputTruncated;
                var errBuilder = new StringBuilder();
                foreach (var line in stderrLines)
                    errBuilder.AppendLine(line);
                stderrText = errBuilder.ToString();
                if (rawStdout is not null)
                    stdoutBytes = rawStdout.ToArray();
                else
                    stdoutText = stdout.ToString();
            }

            GitCliResult Result(int exitCode, GitErrorKind kind) => stdoutBytes is not null
                ? new GitCliResult(exitCode, stdoutBytes, stderrText, kind, stopwatch.Elapsed) { OutputTruncated = truncated }
                : new GitCliResult(exitCode, stdoutText!, stderrText, kind, stopwatch.Elapsed) { OutputTruncated = truncated };

            // The caller's own token and the per-request Timeout both trip `cancelled` via the same
            // linked registration — tell them apart so a stalled invocation (killed by the timeout,
            // with nothing in stderr to classify) isn't reported as an opaque Unknown failure.
            if (cancelled && ct.IsCancellationRequested)
                return Result(-1, GitErrorKind.Cancelled);

            if (cancelled && timeoutCts.IsCancellationRequested)
                return Result(-1, GitErrorKind.TimedOut);

            var exitCode = SafeExitCode(process);
            var kind = exitCode == 0 || truncated
                ? GitErrorKind.None
                : GitErrorClassifier.Classify(exitCode, stderrText);

            return Result(exitCode, kind);
        }
    }

    private static void WriteStdin(StreamWriter writer, string stdin)
    {
        try
        {
            writer.Write(stdin);
            writer.Close();
        }
        catch (IOException)
        {
            // The process may have exited before consuming stdin; the exit code tells the story.
        }
    }

    /// <summary>Waits for every pipe thread after git has exited. Output still sitting in a pipe is
    /// always read in full: the wait ends early only when every unfinished reader has been blocked
    /// inside its read call for <see cref="PipeDrainGrace"/> since git exited. A reader blocked in
    /// a read means the pipe is empty, so something else — a leftover child process — is holding
    /// it open. The time of the last byte is deliberately not used: under CPU load a reader that
    /// has not been scheduled yet looks idle while output still waits in the pipe.</summary>
    private async Task DrainPipesAsync(List<PipeThread> pipes, long exitedAt, string describe)
    {
        var all = Task.WhenAll(pipes.Select(p => p.Completion));

        while (true)
        {
            try
            {
                // A failed reader (a throwing callback) rethrows here.
                await all.WaitAsync(PipePollInterval).ConfigureAwait(false);
                return;
            }
            catch (TimeoutException)
            {
            }

            var now = Stopwatch.GetTimestamp();
            if (pipes.Where(p => !p.Completion.IsCompleted).All(p => p.StuckFor(exitedAt, now) >= PipeDrainGrace))
            {
                AppLog.Write(AppLogLevel.Warn, "git-cli",
                    $"{describe}: git exited but a child process still holds its pipes open; returning the output captured so far");
                return;
            }
        }
    }

    private static int SafeExitCode(Process process)
    {
        try { return process.ExitCode; }
        catch (InvalidOperationException) { return -1; }
    }

    /// <summary>One dedicated background thread running a blocking pipe loop. The thread's
    /// completion is a task; a failure other than a closed pipe kills the process (so it cannot
    /// block on an undrained pipe) and surfaces through that task.</summary>
    private sealed class PipeThread
    {
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly BlockTrackingStream? _tracked;

        private PipeThread(BlockTrackingStream? tracked) => _tracked = tracked;

        public Task Completion => _done.Task;

        /// <summary>How long this thread has been stuck: blocked inside a read (when it reads a
        /// pipe) since the later of <paramref name="exitedAt"/> and the moment it entered the read.
        /// Zero when it is not blocked in a read. A thread with no pipe to read (the stdin
        /// writer) counts from <paramref name="exitedAt"/>.</summary>
        public TimeSpan StuckFor(long exitedAt, long now)
        {
            var since = exitedAt;
            if (_tracked is not null)
            {
                var blockedSince = _tracked.BlockedSince;
                if (blockedSince == 0)
                    return TimeSpan.Zero;
                since = Math.Max(blockedSince, exitedAt);
            }

            return Stopwatch.GetElapsedTime(since, now);
        }

        public static PipeThread Start(
            string name, Process process, bool neverKill, BlockTrackingStream? tracked, TimeSpan startDelay, Action body)
        {
            var pipe = new PipeThread(tracked);
            new Thread(() =>
            {
                try
                {
                    if (startDelay > TimeSpan.Zero)
                        Thread.Sleep(startDelay);
                    body();
                    pipe._done.TrySetResult();
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    // The pipe was closed under the read (a kill, or the process was disposed).
                    pipe._done.TrySetResult();
                }
                catch (Exception ex)
                {
                    // A reader that died leaves the pipe undrained and git blocked on it — except
                    // for a started write, which is never killed; its exception still surfaces.
                    if (!neverKill)
                        GitCliProcessSetup.TryKill(process);
                    pipe._done.TrySetException(ex);
                }
            }, PipeThreadStackBytes)
            { IsBackground = true, Name = name }.Start();
            return pipe;
        }
    }

    /// <summary>Read-only pass-through that records when a read call is in flight, so the drain
    /// logic can tell a reader blocked on an empty pipe from one that simply has not run yet.</summary>
    private sealed class BlockTrackingStream(Stream inner) : Stream
    {
        private long _blockedSince;

        /// <summary><see cref="Stopwatch.GetTimestamp"/> at which the current read began, or 0
        /// when no read is in flight.</summary>
        public long BlockedSince => Volatile.Read(ref _blockedSince);

        public override int Read(byte[] buffer, int offset, int count)
        {
            Volatile.Write(ref _blockedSince, Stopwatch.GetTimestamp());
            try { return inner.Read(buffer, offset, count); }
            finally { Volatile.Write(ref _blockedSince, 0); }
        }

        public override int Read(Span<byte> buffer)
        {
            Volatile.Write(ref _blockedSince, Stopwatch.GetTimestamp());
            try { return inner.Read(buffer); }
            finally { Volatile.Write(ref _blockedSince, 0); }
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
