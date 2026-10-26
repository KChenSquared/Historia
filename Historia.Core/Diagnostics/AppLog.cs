namespace Historia.Diagnostics;

/// <summary>
/// The application log. One structured logger for the whole app: every unhandled
/// exception, every <c>Warning</c>/<c>Error</c> notification and classified git failure, every git
/// operation (through <c>LoggingGitGateway</c>) and every git CLI invocation, plus repository
/// open/close. Terminal session I/O is deliberately not logged.
/// <para>
/// Off unless turned on by the Enable logging setting in Settings → Advanced or the
/// <c>HISTORIA_LOG</c> environment variable, at which point one file per session is written
/// under <see cref="AppPaths.LogsDirectory"/> (<c>dev-&lt;timestamp&gt;.log</c>); logs older than
/// 48 hours are deleted each auto-fetch round.
/// </para>
/// <para>
/// This is <b>not</b> <see cref="PerfLog"/> — that stays a separate, perf-timing-only tool. Do not
/// add a third logging system; extend this one. Every method is a no-op when disabled and never throws.
/// </para>
/// </summary>
public static partial class AppLog
{
    private static readonly object Gate = new();
    private static string? _sessionPath;

    /// <summary>The session file's writer: the file is held open for as long as logging is
    /// enabled, and lines are written on the writer's own thread, so logging never waits on the
    /// disk. Trap: reopening the file per entry, then writing a held-open file under
    /// <see cref="Gate"/>, both put file I/O on every logging thread — one held-up write stalled
    /// them all.</summary>
    private static QueuedLineWriter? _writer;

    static AppLog()
    {
        try
        {
            LoggingSwitch.Changed += OnLoggingSwitchChanged;
            Reconcile();
        }
        catch { /* logging must never throw */ }
    }

    internal static void OnLoggingSwitchChanged(bool enabled)
    {
        Reconcile();
    }

    internal static void Reconcile()
    {
        QueuedLineWriter? retired = null;
        lock (Gate)
        {
            if (LoggingSwitch.ProcessEnabled)
            {
                if (_writer is null)
                    OpenSessionLocked();
            }
            else
            {
                if (_writer is not null)
                {
                    WriteLineLocked($"{Stamp()}  INFO   [app]  logging stopped");
                    _sessionPath = null;
                    retired = DetachWriterLocked();
                }
            }
        }
        retired?.Dispose();
    }

    /// <summary>The shared injectable instance handed to the DI container.</summary>
    public static IAppLog Current { get; } = new AppLogFacade();

    private static readonly AsyncLocal<AppLogSession?> _testSession = new();

    /// <summary>
    /// Flow-local test session for running tests in parallel without shared log writer state.
    /// Outside tests this is null. Disposal releases the flow's interception in the flow that
    /// disposes it; a session disposed from a deeper async frame still needs <see cref="ResetForTesting"/>
    /// because an <see cref="AsyncLocal{T}"/> write does not propagate back up the async call tree.
    /// </summary>
    internal sealed class AppLogSession : IDisposable
    {
        private readonly object _gate = new();
        private QueuedLineWriter? _writer;
        private bool _disposed;

        public string SessionPath { get; }
        public bool Enabled { get; set; } = true;

        public AppLogSession(string? logsDirectory = null)
        {
            var dir = logsDirectory ?? AppPaths.LogsDirectory;
            Directory.CreateDirectory(dir);
            SessionPath = Path.Combine(dir, $"dev-{DateTime.Now:yyyyMMdd-HHmmss-fffffff}.log");
            _writer = QueuedLineWriter.HoldOpen(SessionPath, "app-log-test-writer");
            var asm = typeof(AppLog).Assembly.GetName();
            WriteLine($"# Historia developer log — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            WriteLine($"# {asm.Name} {asm.Version}  |  {RuntimeInformation.OSDescription}  |  pid {Environment.ProcessId}");
            WriteLine($"{Stamp()}  INFO   [app]  logging started");
        }

        public void WriteLine(string line)
        {
            lock (_gate)
            {
                if (_disposed || _writer is null) return;
                _writer.Enqueue(line);
            }
        }

        public bool Flush(TimeSpan timeout)
        {
            QueuedLineWriter? writer;
            lock (_gate) writer = _writer;
            return writer is null || writer.Flush(timeout);
        }

        public void Dispose()
        {
            QueuedLineWriter? writer;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                Enabled = false;
                writer = _writer;
                _writer = null;
            }
            try { writer?.Dispose(); } catch { }

            if (ReferenceEquals(_testSession.Value, this))
                _testSession.Value = null;
        }
    }

    /// <summary>Whether anything is written. Setting it updates the process-wide switch and
    /// opens a fresh session file / closes the current one.</summary>
    public static bool Enabled
    {
        get => _testSession.Value?.Enabled ?? LoggingSwitch.Enabled;
        set
        {
            if (_testSession.Value is { } testSession)
            {
                testSession.Enabled = value;
                return;
            }

            LoggingSwitch.Set(value);
        }
    }

    /// <summary>Full path of the active session log, or null when disabled. Shown in the Developer panel.</summary>
    public static string? CurrentLogPath => _testSession.Value?.SessionPath ?? _sessionPath;

    /// <summary>Write one entry. No-op unless <see cref="Enabled"/>.</summary>
    public static void Write(AppLogLevel level, string category, string message, Exception? ex = null)
    {
        if (!Enabled)
            return;

        try
        {
            var sb = new StringBuilder(128);
            sb.Append(Stamp()).Append("  ").Append(LevelTag(level)).Append("  [").Append(category).Append("]  ")
              .Append(Redact(message));

            if (ex is not null)
            {
                sb.Append(Environment.NewLine);
                AppendException(sb, ex);
            }

            var line = sb.ToString();
            if (_testSession.Value is { } session)
            {
                session.WriteLine(line);
                return;
            }

            lock (Gate)
                WriteLineLocked(line);
        }
        catch { /* logging must never throw */ }
    }

    /// <summary>Write one entry. An interpolated message binds here rather than to the
    /// <see cref="string"/> overload, and its holes are not formatted while logging is off.</summary>
    public static void Write(AppLogLevel level, string category, MessageHandler message, Exception? ex = null)
    {
        var active = message.Active;
        var text = message.ToStringAndClear();
        if (active)
            Write(level, category, text, ex);
    }

    /// <summary>Logs <c>-&gt; message</c> now and <c>&lt;- message (outcome, N.Nms)</c> on dispose.
    /// Cheap default-struct no-op when disabled.</summary>
    public static OperationScope Operation(string category, string message)
        => Enabled ? new OperationScope(category, Redact(message)) : default;

    /// <summary>As <see cref="Operation(string, string)"/>; an interpolated message is not
    /// formatted while logging is off.</summary>
    public static OperationScope Operation(string category, MessageHandler message)
    {
        var active = message.Active;
        var text = message.ToStringAndClear();
        return active && Enabled ? new OperationScope(category, Redact(text)) : default;
    }

    /// <summary>Builds an interpolated message only while logging is enabled: the compiler skips
    /// every <c>Append*</c> call, and so every hole's <c>ToString</c>, when
    /// <c>shouldAppend</c> comes back false. Formats under the current culture, exactly as an
    /// interpolated <see cref="string"/> does.</summary>
    [InterpolatedStringHandler]
    public ref struct MessageHandler
    {
        private DefaultInterpolatedStringHandler _text;

        public MessageHandler(int literalLength, int formattedCount, out bool shouldAppend)
        {
            Active = shouldAppend = Enabled;
            _text = Active ? new DefaultInterpolatedStringHandler(literalLength, formattedCount) : default;
        }

        /// <summary>True when logging was enabled as the message started and the holes were formatted.</summary>
        internal bool Active { get; }

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

    /// <summary>Test seam: starts an isolated per-flow logging session.</summary>
    internal static AppLogSession StartSessionForTesting(string? dir = null)
    {
        var session = new AppLogSession(dir);
        _testSession.Value = session;
        return session;
    }

    /// <summary>Test seam: forget the current session without touching the marker file.</summary>
    internal static void ResetForTesting()
    {
        if (_testSession.Value is { } session)
        {
            session.Dispose();
            _testSession.Value = null;
        }

        QueuedLineWriter? retired;
        lock (Gate)
        {
            _sessionPath = null;
            retired = DetachWriterLocked();
        }
        retired?.Dispose();
    }

    /// <summary>Waits (at most <paramref name="timeout"/>) until every line written so far is in
    /// the session file — for crash handlers and exit, where the process may end right after.
    /// True when it got there in time.</summary>
    public static bool Flush(TimeSpan timeout)
    {
        if (_testSession.Value is { } session)
            return session.Flush(timeout);
        QueuedLineWriter? writer;
        lock (Gate) writer = _writer;
        return writer is null || writer.Flush(timeout);
    }

    // ── session lifecycle ──

    private static void OpenSessionLocked()
    {
        var dir = AppPaths.LogsDirectory;
        Directory.CreateDirectory(dir);

        // In case a session is still open. Disposed here, not deferred: the new session file can
        // have the same name (same second), and the old handle must be gone before it reopens.
        DetachWriterLocked()?.Dispose();

        _sessionPath = Path.Combine(dir, $"dev-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        try
        {
            // Held open for the whole session with FileShare.Read, so anything that wants to read
            // the file while it's live — "Open logs folder" + a text editor, `Get-Content -Wait` —
            // still can. No BOM: lines are appended.
            _writer = QueuedLineWriter.HoldOpen(_sessionPath, "app-log-writer");
        }
        catch
        {
            // Can't open the file — WriteLineLocked below (and every later call) becomes a
            // silent no-op via the null check, same as any other diagnostics failure.
            _writer = null;
        }

        var asm = typeof(AppLog).Assembly.GetName();
        WriteLineLocked($"# Historia developer log — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        WriteLineLocked($"# {asm.Name} {asm.Version}  |  {RuntimeInformation.OSDescription}  |  pid {Environment.ProcessId}");
        WriteLineLocked($"{Stamp()}  INFO   [app]  logging started");
    }

    private static QueuedLineWriter? DetachWriterLocked()
    {
        var writer = _writer;
        _writer = null;
        return writer;
    }

    private static void WriteLineLocked(string line)
    {
        _writer?.Enqueue(line);
    }

    // ── formatting ──

    private static string Stamp() => DateTime.Now.ToString("HH:mm:ss.fff");

    private static string LevelTag(AppLogLevel level) => level switch
    {
        AppLogLevel.Trace => "TRACE",
        AppLogLevel.Debug => "DEBUG",
        AppLogLevel.Info => "INFO ",
        AppLogLevel.Warn => "WARN ",
        _ => "ERROR",
    };

    private static void AppendException(StringBuilder sb, Exception ex, int depth = 0)
    {
        var pad = new string(' ', 4 + depth * 2);
        sb.Append(pad).Append(ex.GetType().FullName).Append(": ").Append(Redact(ex.Message));

        if (ex is Historia.Services.Git.HistoriaGitException { Details: { Length: > 0 } details })
        {
            sb.Append(Environment.NewLine).Append(pad).Append("  git: ")
              .Append(Redact(Truncate(details.Replace(Environment.NewLine, " ").Replace('\n', ' '), 500)));
        }

        if (!string.IsNullOrEmpty(ex.StackTrace))
        {
            foreach (var frame in ex.StackTrace.Split('\n'))
                sb.Append(Environment.NewLine).Append(pad).Append("  ").Append(frame.TrimEnd('\r'));
        }

        if (ex.InnerException is { } inner && depth < 5)
        {
            sb.Append(Environment.NewLine).Append(pad).Append("caused by:").Append(Environment.NewLine);
            AppendException(sb, inner, depth + 1);
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    /// <summary>Strip <c>user:pass</c> / token userinfo from any URL before it reaches the file —
    /// git remote URLs turn up in CLI argument lines.</summary>
    internal static string Redact(string s)
        => string.IsNullOrEmpty(s) ? s : UserInfoPattern().Replace(s, "://<redacted>@");

    [GeneratedRegex(@"://[^/\s@]+@", RegexOptions.CultureInvariant)]
    private static partial Regex UserInfoPattern();

    /// <summary>
    /// Enter/exit token for <see cref="Operation"/>. A <c>default</c> instance (logging disabled)
    /// disposes to nothing.
    /// </summary>
    public readonly struct OperationScope : IDisposable
    {
        private readonly string? _category;
        private readonly string _message;
        private readonly long _start;
        private readonly string?[]? _outcome; // 1-slot mutable cell shared with copies of this struct

        internal OperationScope(string category, string message)
        {
            _category = category;
            _message = message;
            _start = Stopwatch.GetTimestamp();
            _outcome = new string?[1];
            Write(AppLogLevel.Info, category, "-> " + message);
        }

        /// <summary>Record a failure; the exit line reads <c>cancelled</c> for a cancellation and <c>FAILED: &lt;type&gt;: &lt;message&gt;</c> for anything else.</summary>
        public void Fail(Exception ex)
        {
            if (_outcome is not null)
            {
                _outcome[0] = ex is OperationCanceledException
                    ? "cancelled"
                    : $"FAILED: {ex.GetType().Name}: {ex.Message}";
            }
        }

        /// <summary>Record a custom outcome string for the exit line (default is <c>ok</c>).</summary>
        public void Outcome(string text)
        {
            if (_outcome is not null)
                _outcome[0] = text;
        }

        public void Dispose()
        {
            if (_category is null)
                return;
            var ms = Stopwatch.GetElapsedTime(_start).TotalMilliseconds;
            Write(AppLogLevel.Info, _category, $"<- {_message}  ({_outcome?[0] ?? "ok"}, {ms:F1}ms)");
        }
    }
}
