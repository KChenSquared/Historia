namespace Historia.Services.Git;

/// <summary>
/// Pull-based <see cref="IEnumerator{T}"/> over a live <c>git log --date-order</c> process — the
/// commit-graph walk (see <c>Historia.Services.Graph.GraphLayoutService</c>). Git's own in-degree
/// topological sort expands lazily by generation number (using a commit-graph file when present)
/// instead of sorting the whole history up front, so on a huge history the first rows arrive in
/// a fraction of a second.
/// <para>
/// Tip SHAs are written to the process's stdin (<c>--stdin</c>) and then closed — a large tag/
/// branch count can exceed the ~32K Windows command-line limit if passed as arguments. A long
/// list is written on its own thread so starting the stream never waits on git reading it. Pipe
/// back-pressure between pulls means the walk's in-degree state lives entirely inside the git
/// process; pausing (letting a caller stop pulling) and resuming (pulling again later) costs
/// nothing extra.
/// </para>
/// </summary>
public sealed class GitLogStream : IEnumerator<RawCommit>
{
    // %H commit, %P space-separated parents, %aI/%cI strict ISO-8601 author/commit dates,
    // %an/%ae author name/email, %s subject. One field per record, split on 0x1f (unit
    // separator); -z terminates each record with NUL instead of the default blank line, so a
    // record can be read byte-by-byte without ambiguity even though none of these fields can
    // themselves contain a NUL or embedded newline.
    private const string Format = "%H%x1f%P%x1f%aI%x1f%cI%x1f%an%x1f%ae%x1f%s";
    private const char FieldSep = '\x1f';

    // Tip counts above this are written to stdin on a dedicated thread.
    private const int InlineTipLimit = 50;

    // Started and not yet disposed, by process id. Only Dispose removes an entry — a stream
    // dropped without Dispose stays listed. Holds no reference to the stream or its process.
    private static readonly ConcurrentDictionary<int, string> Undisposed = new();

    private readonly Process _process;
    private readonly int _processId;
    private readonly NulRecordReader _reader;
    private readonly StringBuilder _stderr;
    private readonly Task _stderrDrain;
    private readonly Thread? _stdinWriter;
    private bool _disposed;
    private int _lowPriority;

    public RawCommit Current { get; private set; }
    object IEnumerator.Current => Current;

    /// <summary>True once the underlying process has failed — exited non-zero, or produced a
    /// record this parser could not understand. The caller should dispose it; the rows already
    /// yielded stay valid, but the walk cannot continue.</summary>
    public bool Failed { get; private set; }

    /// <summary>Why the stream <see cref="Failed"/> — the exit code and a tail of git's stderr, or
    /// the read/parse error. Null while it hasn't failed.</summary>
    public string? FailureReason { get; private set; }

    private GitLogStream(Process process, Task stderrDrain, StringBuilder stderr, Thread? stdinWriter)
    {
        _process = process;
        try { _processId = process.Id; } catch (InvalidOperationException) { _processId = -1; }
        _reader = new NulRecordReader(process.StandardOutput.BaseStream);
        _stderrDrain = stderrDrain;
        _stderr = stderr;
        _stdinWriter = stdinWriter;
    }

    /// <summary>
    /// Starts <c>git log --date-order -z --stdin --format=...</c> over <paramref name="tipShas"/>.
    /// Returns null (spawning nothing further) when git isn't available, there are no tips, or
    /// the process could not be started.
    /// <para>
    /// A non-empty <paramref name="pathFilter"/> limits the walk to commits touching that path
    /// (file or folder), matched literally (<c>GIT_LITERAL_PATHSPECS=1</c>). It adds
    /// <c>--parents</c>, which turns on parent rewriting so <c>%P</c> names the nearest shown
    /// ancestors instead of parents that never appear in the output — without it the graph
    /// would be full of edges to missing rows. Renames are not followed. The pathspec goes on the
    /// command line after <c>--</c>; <c>--stdin</c> still reads only revisions, since paths are
    /// taken from stdin only after a <c>--</c> line there.
    /// </para>
    /// </summary>
    public static GitLogStream? TryStart(
        IGitExecutableLocator locator, string repositoryPath, IReadOnlyList<string> tipShas, GitProfile? profile = null,
        string? pathFilter = null)
    {
        var git = locator.Current;
        if (!git.Found || git.Path is null || tipShas.Count == 0)
            return null;

        var psi = new ProcessStartInfo(git.Path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            WorkingDirectory = repositoryPath,
        };

        foreach (var arg in GitCliProcessSetup.DeterministicPrefix)
            psi.ArgumentList.Add(arg);
        psi.ArgumentList.Add("log");
        psi.ArgumentList.Add("--date-order");
        psi.ArgumentList.Add("-z");
        psi.ArgumentList.Add("--no-mailmap");
        psi.ArgumentList.Add("--no-show-signature");
        psi.ArgumentList.Add("--stdin");
        psi.ArgumentList.Add($"--format={Format}");
        var filtered = !string.IsNullOrEmpty(pathFilter);
        if (filtered)
        {
            psi.ArgumentList.Add("--parents");
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add(pathFilter!);
        }

        GitCliProcessSetup.ComposeEnvironment(psi, profile, extraEnvironment: null);
        // Graph rows must name real commit objects consistently across details, diffs, and decorations;
        // ignore replace refs entirely.
        psi.Environment["GIT_NO_REPLACE_OBJECTS"] = "1";
        if (filtered)
            psi.Environment["GIT_LITERAL_PATHSPECS"] = "1";

        var process = new Process { StartInfo = psi, EnableRaisingEvents = false };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Diagnostics.PerfLog.Mark($"GitLogStream: failed to start git: {ex.Message}");
            return null;
        }

        Thread? stdinWriter = null;
        if (tipShas.Count > InlineTipLimit)
            stdinWriter = StartBackgroundThread(() => WriteTips(process, tipShas), "GitLogStream stdin");
        else
            WriteTips(process, tipShas);

        var stderr = new StringBuilder();
        var drain = StartStderrDrain(process, stderr);
        var stream = new GitLogStream(process, drain, stderr, stdinWriter);
        Undisposed[stream._processId] = repositoryPath;
        return stream;
    }

    private static Thread StartBackgroundThread(ThreadStart body, string name)
    {
        var thread = new Thread(body, maxStackSize: 256 * 1024) { IsBackground = true, Name = name };
        thread.Start();
        return thread;
    }

    private static void WriteTips(Process process, IReadOnlyList<string> tipShas)
    {
        try
        {
            var writer = process.StandardInput;
            writer.NewLine = "\n";
            foreach (var sha in tipShas)
                writer.WriteLine(sha);
            writer.Close();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The process may have exited, or the stream been disposed, before git consumed all of
            // stdin — MoveNext observes the outcome.
        }
    }

    // A blocking read on its own thread: the pool is never parked on a git pipe.
    private static Task StartStderrDrain(Process process, StringBuilder into)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        StartBackgroundThread(() =>
        {
            try
            {
                var text = process.StandardError.ReadToEnd();
                lock (into)
                    into.Append(text);
            }
            catch
            {
                // Best-effort — surfaced only in a failure PerfLog mark.
            }
            finally
            {
                done.TrySetResult();
            }
        }, "GitLogStream stderr");
        return done.Task;
    }

    /// <summary>
    /// Runs the git process at <c>BelowNormal</c> priority when <paramref name="low"/> is true and
    /// at <c>Normal</c> otherwise, so a background walk yields the CPU to interactive work.
    /// Idempotent and best-effort: a process that already exited, or a platform refusal, is
    /// ignored.
    /// </summary>
    public void SetLowPriority(bool low)
    {
        if (_disposed || Interlocked.Exchange(ref _lowPriority, low ? 1 : 0) == (low ? 1 : 0))
            return;
        try
        {
            if (!_process.HasExited)
                _process.PriorityClass = low ? ProcessPriorityClass.BelowNormal : ProcessPriorityClass.Normal;
        }
        catch
        {
            // Best-effort — the process may be gone or the platform may refuse the change.
        }
    }

    /// <summary>Test seam: the git process's current priority class, or null when unreadable.</summary>
    internal ProcessPriorityClass? PriorityClassForTesting()
    {
        try { return _process.PriorityClass; }
        catch { return null; }
    }

    /// <summary>Test seam: the id of the git process, or -1 when it could not be read.</summary>
    internal int ProcessIdForTesting => _processId;

    /// <summary>Test seam: streams started for <paramref name="repositoryPath"/> and not yet
    /// disposed.</summary>
    internal static int LiveCountForTesting(string repositoryPath) =>
        Undisposed.Count(kv => string.Equals(kv.Value, repositoryPath, StringComparison.OrdinalIgnoreCase));

    public bool MoveNext()
    {
        if (_disposed || Failed)
            return false;

        string? record;
        try
        {
            record = _reader.ReadRecord();
        }
        catch (Exception ex)
        {
            Fail($"read failure: {ex.Message}");
            return false;
        }

        if (record is null)
        {
            // EOF on stdout. A clean exit means the walk is exhausted; anything else is a
            // failure.
            try { _process.WaitForExit(1_000); } catch { /* best effort */ }
            var exitCode = SafeExitCode();
            if (exitCode != 0)
                Fail($"git log exited {exitCode}: {StderrTail()}");
            return false;
        }

        try
        {
            Current = ParseRecord(record);
            return true;
        }
        catch (Exception ex)
        {
            Fail($"parse failure: {ex.Message} (record: {Truncate(record, 200)})");
            return false;
        }
    }

    private void Fail(string reason)
    {
        Failed = true;
        FailureReason = reason;
        Diagnostics.PerfLog.Mark($"GitLogStream: {reason}");
    }

    private string StderrTail()
    {
        try { _stderrDrain.Wait(1_000); } catch { /* best effort */ }
        lock (_stderr)
            return Truncate(_stderr.ToString().Trim(), 300);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private int SafeExitCode()
    {
        try { return _process.ExitCode; }
        catch (InvalidOperationException) { return -1; }
    }

    /// <summary>Splits <see cref="RawCommit"/> field text — internal (not private) so tests can
    /// feed it fixture records directly without spawning a process.</summary>
    internal static RawCommit ParseRecord(string record)
    {
        var span = record.AsSpan();
        Span<Range> fields = stackalloc Range[7];
        var count = 0;
        var start = 0;
        while (count < fields.Length)
        {
            var sep = span[start..].IndexOf(FieldSep);
            var end = sep < 0 ? span.Length : start + sep;
            fields[count++] = start..end;
            if (sep < 0)
                break;
            start = end + 1;
        }

        if (count < fields.Length)
            throw new FormatException($"expected 7 fields, got {count}");

        var id = GitObjectId.Parse(span[fields[0]]);
        var parentsText = span[fields[1]];
        var parentIds = parentsText.Length == 0 ? Array.Empty<GitObjectId>() : ParseParents(parentsText);

        var authorWhen = DateTimeOffset.Parse(span[fields[2]], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        var commitWhen = DateTimeOffset.Parse(span[fields[3]], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

        var metadata = new CommitMetadata
        {
            Id = id,
            MessageShort = record[fields[6]],
            AuthorName = record[fields[4]],
            AuthorEmail = record[fields[5]],
            AuthorWhen = authorWhen,
            CommitWhen = commitWhen,
            ParentCount = parentIds.Length,
        };

        return new RawCommit(id, parentIds, metadata);
    }

    private static GitObjectId[] ParseParents(ReadOnlySpan<char> text)
    {
        var count = 0;
        foreach (var range in text.Split(' '))
        {
            if (range.End.Value > range.Start.Value)
                count++;
        }

        var parents = new GitObjectId[count];
        var next = 0;
        foreach (var range in text.Split(' '))
        {
            if (range.End.Value > range.Start.Value)
                parents[next++] = GitObjectId.Parse(text[range]);
        }

        return parents;
    }

    public void Reset() => throw new NotSupportedException("GitLogStream is a forward-only, one-shot enumerator.");

    private static void DisposeProcess(Process process)
    {
        try { process.Dispose(); } catch { /* best effort */ }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Undisposed.TryRemove(_processId, out _);

        // The kill runs on ProcessKiller's thread, and a disposed Process can no longer be killed:
        // wait for the kill to be attempted before disposing. The wait is bounded so a backed-up
        // killer never stalls a caller; a kill still pending after it disposes the process itself.
        var killed = ProcessKiller.KillAsync(_process);
        try { killed.Wait(1_000); } catch { /* best effort */ }
        try { _stderrDrain.Wait(1_000); } catch { /* best effort */ }
        try { _stdinWriter?.Join(1_000); } catch { /* best effort */ }

        if (killed.IsCompleted)
            DisposeProcess(_process);
        else
            _ = killed.ContinueWith(static (_, state) => DisposeProcess((Process)state!), _process,
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        _reader.Dispose();
    }
}

/// <summary>
/// Reads NUL-delimited (<c>git log -z</c>) UTF-8 text records out of a <see cref="Stream"/>, one
/// at a time, buffering only the bytes of the record currently being assembled. Internal (not
/// private to <see cref="GitLogStream"/>) so a test can feed it a <see cref="MemoryStream"/> of
/// fixture bytes without spawning a real git process.
/// </summary>
internal sealed class NulRecordReader(Stream stream) : IDisposable
{
    private readonly MemoryStream _recordBuf = new(256);
    private readonly byte[] _readBuf = new byte[64 * 1024];
    private int _readPos;
    private int _readLen;
    private bool _eofSeen;

    /// <summary>Reads up to the next NUL byte and decodes it as UTF-8. Returns null at end of
    /// stream (no more bytes, including a trailing terminator-less final record — git always
    /// terminates the last record too, but this is defensive).</summary>
    public string? ReadRecord()
    {
        _recordBuf.SetLength(0);
        while (true)
        {
            if (_readPos >= _readLen)
            {
                if (_eofSeen)
                    break;
                _readLen = stream.Read(_readBuf, 0, _readBuf.Length);
                _readPos = 0;
                if (_readLen <= 0)
                {
                    _eofSeen = true;
                    break;
                }
            }

            var start = _readPos;
            var span = new ReadOnlySpan<byte>(_readBuf, start, _readLen - start);
            var nul = span.IndexOf((byte)0);
            if (nul < 0)
            {
                _recordBuf.Write(_readBuf, start, _readLen - start);
                _readPos = _readLen;
                continue;
            }

            _recordBuf.Write(_readBuf, start, nul);
            _readPos = start + nul + 1;
            return Encoding.UTF8.GetString(_recordBuf.GetBuffer(), 0, (int)_recordBuf.Length);
        }

        return _recordBuf.Length > 0
            ? Encoding.UTF8.GetString(_recordBuf.GetBuffer(), 0, (int)_recordBuf.Length)
            : null;
    }

    public void Dispose() => _recordBuf.Dispose();
}
