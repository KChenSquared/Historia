namespace Historia.Tests;

[Trait("tier", "unit")]
[Collection("serial")]
public sealed class AppLogTests : IDisposable
{
    private readonly StateDir _dir = new();

    public AppLogTests()
    {
        AppLog.Enabled = false;
        AppLog.ResetForTesting();
    }

    public void Dispose()
    {
        AppLog.Enabled = false;
        AppLog.ResetForTesting();
        _dir.Dispose();
    }

    private string[] SessionFiles() =>
        Directory.Exists(AppPaths.LogsDirectory)
            ? Directory.GetFiles(AppPaths.LogsDirectory, "dev-*.log")
            : [];

    /// <summary>Reads the session file's content. Disables logging first — the session writer
    /// holds the file open for the whole session (not opened and closed per line),
    /// so the handle must be closed before a second reader can open the same file.</summary>
    private string ReadSession()
    {
        AppLog.Enabled = false;
        return File.ReadAllText(SessionFiles().Single());
    }

    [Fact]
    public void Disabled_writes_nothing()
    {
        AppLog.Write(AppLogLevel.Error, "test", "should not appear");
        using (AppLog.Operation("test", "op")) { }

        Assert.Empty(SessionFiles());
        Assert.Null(AppLog.CurrentLogPath);
    }

    [Fact]
    public void Disabled_never_formats_interpolated_arguments()
    {
        var bomb = new PerfLogTests.FormatBomb();

        AppLog.Write(AppLogLevel.Error, "test", $"never {bomb}");
        AppLog.Write(AppLogLevel.Error, "test", $"never {bomb,8:x}", new InvalidOperationException("x"));
        using (AppLog.Operation("test", $"never {bomb}")) { }

        Assert.Equal(0, bomb.Calls);
        Assert.Empty(SessionFiles());
    }

    [Fact]
    public void Disabled_never_formats_interpolated_arguments_passed_through_IAppLog()
    {
        var bomb = new PerfLogTests.FormatBomb();
        IAppLog log = AppLog.Current;

        log.Trace("test", $"never {bomb}");
        log.Debug("test", $"never {bomb}");
        log.Info("test", $"never {bomb,8:x}");
        log.Warn("test", $"never {bomb}");
        log.Warn("test", $"never {bomb}", new InvalidOperationException("x"));
        log.Error("test", $"never {bomb}", new InvalidOperationException("x"));

        Assert.Equal(0, bomb.Calls);
        Assert.Empty(SessionFiles());
    }

    [Fact]
    public void An_interpolated_IAppLog_call_logs_the_same_text_as_the_string_overload()
    {
        var saved = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("fi-FI");
            AppLog.Enabled = true;
            IAppLog log = AppLog.Current;
            var value = 1234.5;
            var name = "abc";

            log.Info("parity-a", $"v {value:F2} [{name,6}] [{name,-6}] {null as string}|");
            log.Info("parity-b", string.Format("v {0:F2} [{1,6}] [{2,-6}] {3}|", value, name, name, null as string));
            log.Warn("parity-w", $"w {name}", new InvalidOperationException("boom"));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = saved;
        }

        var text = ReadSession();
        var a = Assert.Single(text.Split(Environment.NewLine), l => l.Contains("[parity-a]"));
        var b = Assert.Single(text.Split(Environment.NewLine), l => l.Contains("[parity-b]"));
        Assert.Equal(a[(a.IndexOf("[parity-a]", StringComparison.Ordinal) + "[parity-a]".Length)..],
            b[(b.IndexOf("[parity-b]", StringComparison.Ordinal) + "[parity-b]".Length)..]);
        Assert.Contains("[parity-w]  w abc", text);
        Assert.Contains("InvalidOperationException: boom", text);
    }

    [Fact]
    public void An_interpolated_write_logs_the_same_text_as_the_string_overload()
    {
        var saved = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("fi-FI");
            AppLog.Enabled = true;
            var value = 1234.5;
            var name = "abc";

            AppLog.Write(AppLogLevel.Warn, "parity", $"v {value:F2} [{name,6}] [{name,-6}] {null as string}|");
            string viaString = $"v {value:F2} [{name,6}] [{name,-6}] {null as string}|";
            AppLog.Write(AppLogLevel.Warn, "parity", viaString);
            using (AppLog.Operation("parity", $"op {value:F1}")) { }

            var lines = ReadSession().Split(Environment.NewLine);
            var written = lines.Where(l => l.Contains("[parity]  v ", StringComparison.Ordinal)).ToArray();
            Assert.Equal(2, written.Length);
            Assert.EndsWith($"[parity]  {viaString}", written[0], StringComparison.Ordinal);
            Assert.EndsWith($"[parity]  {viaString}", written[1], StringComparison.Ordinal);
            Assert.Contains(lines, l => l.EndsWith("[parity]  -> op 1234,5", StringComparison.Ordinal));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public void Enabling_creates_one_session_file_with_a_header()
    {
        AppLog.Enabled = true;

        var file = Assert.Single(SessionFiles());
        Assert.Equal(file, AppLog.CurrentLogPath);
        AppLog.Enabled = false; // close the session writer before reopening the file for read
        var text = File.ReadAllText(file);
        Assert.Contains("# Historia developer log", text);
        Assert.Contains("logging started", text);
    }

    [Fact]
    public void Write_formats_level_category_and_message()
    {
        AppLog.Enabled = true;
        AppLog.Write(AppLogLevel.Info, "git", "checkout main");

        Assert.Matches(@"\d\d:\d\d:\d\d\.\d\d\d  INFO   \[git\]  checkout main", ReadSession());
    }

    [Fact]
    public void Operation_logs_enter_and_exit_with_a_duration_and_outcome()
    {
        AppLog.Enabled = true;
        using (var op = AppLog.Operation("git", "CommitAsync"))
            op.Outcome("abc1234567");

        var text = ReadSession();
        Assert.Contains("-> CommitAsync", text);
        Assert.Matches(@"<- CommitAsync  \(abc1234567, \d+(\.\d+)?ms\)", text);
    }

    [Fact]
    public void Operation_Fail_records_the_exception_on_the_exit_line()
    {
        AppLog.Enabled = true;
        using (var op = AppLog.Operation("git", "PushAsync"))
            op.Fail(new InvalidOperationException("no upstream"));

        Assert.Contains("FAILED: InvalidOperationException: no upstream", ReadSession());
    }

    public static TheoryData<Exception> Cancellations() =>
    [
        new OperationCanceledException(),
        new TaskCanceledException(),
    ];

    [Theory]
    [MemberData(nameof(Cancellations))]
    public void Operation_Fail_with_a_cancellation_records_cancelled(Exception ex)
    {
        AppLog.Enabled = true;
        using (var op = AppLog.Operation("git", "GetStatusAsync"))
            op.Fail(ex);

        var text = ReadSession();
        Assert.Contains("(cancelled, ", text);
        Assert.DoesNotContain("FAILED", text);
    }

    [Fact]
    public void Write_with_exception_renders_a_type_message_and_stack_block()
    {
        AppLog.Enabled = true;
        Exception caught;
        try { throw new InvalidOperationException("boom"); }
        catch (Exception ex) { caught = ex; }

        AppLog.Write(AppLogLevel.Error, "unhandled", "crash", caught);

        var text = ReadSession();
        Assert.Contains("System.InvalidOperationException: boom", text);
        Assert.Contains("   at ", text);
    }

    [Fact]
    public void Credentials_in_urls_are_redacted()
    {
        AppLog.Enabled = true;
        AppLog.Write(AppLogLevel.Info, "git-cli", "git push https://alice:s3cr3t@example.com/r.git");

        var text = ReadSession();
        Assert.DoesNotContain("s3cr3t", text);
        Assert.Contains("://<redacted>@example.com", text);
    }

    [Fact]
    public void Does_not_prune_by_count()
    {
        Directory.CreateDirectory(AppPaths.LogsDirectory);
        for (var i = 0; i < 12; i++)
            File.WriteAllText(Path.Combine(AppPaths.LogsDirectory, $"dev-2099010{i:D2}-000000.log"), "fresh");

        AppLog.Enabled = true;

        // 12 fresh + the one just opened.
        Assert.Equal(13, SessionFiles().Length);
    }

    [Fact]
    public void The_session_file_stays_open_for_writing_across_multiple_entries()
    {
        // One handle spans the whole session rather than the file being reopened, written and
        // closed per entry: a second attempt to open the same file for writing must be rejected while it's live.
        AppLog.Enabled = true;
        AppLog.Write(AppLogLevel.Info, "test", "one");

        var file = AppLog.CurrentLogPath!;
        Assert.Throws<IOException>(() => File.Open(file, FileMode.Open, FileAccess.Write, FileShare.None));

        // The held-open handle keeps working for later entries too.
        AppLog.Write(AppLogLevel.Info, "test", "two");
        Assert.Contains("two", ReadSession());
    }

    [Fact]
    public void Flush_puts_every_written_line_in_the_live_session_file()
    {
        AppLog.Enabled = true;
        for (var i = 0; i < 200; i++)
            AppLog.Write(AppLogLevel.Info, "test", $"line {i}");

        Assert.True(AppLog.Flush(TimeSpan.FromSeconds(10)));
        // Still enabled: the session holds the file open, so read with a sharing reader.
        var lines = QueuedLineWriterTests.ReadShared(AppLog.CurrentLogPath!);
        var text = string.Join(Environment.NewLine, lines);
        Assert.Contains("line 0", text);
        Assert.Contains("line 199", text);
    }

    [Fact]
    public void Disabling_stops_writes_and_clears_the_current_path()
    {
        AppLog.Enabled = true;
        var file = AppLog.CurrentLogPath!;
        AppLog.Enabled = false;

        AppLog.Write(AppLogLevel.Error, "test", "after disable");

        Assert.Null(AppLog.CurrentLogPath);
        Assert.DoesNotContain("after disable", File.ReadAllText(file));
    }

    [Fact]
    public async Task Sessions_are_isolated_between_concurrent_flows()
    {
        using var barrier = new Barrier(2);
        string? session1File = null;
        string? session2File = null;

        using var dir1 = new StateDir();
        using var dir2 = new StateDir();

        var t1 = Task.Run(() =>
        {
            AppPaths.OverrideForTesting(dir1.Path);
            using var session1 = AppLog.StartSessionForTesting();
            session1File = session1.SessionPath;
            barrier.SignalAndWait();
            AppLog.Write(AppLogLevel.Info, "flow1", "message from flow 1");
        });

        var t2 = Task.Run(() =>
        {
            AppPaths.OverrideForTesting(dir2.Path);
            using var session2 = AppLog.StartSessionForTesting();
            session2File = session2.SessionPath;
            barrier.SignalAndWait();
            AppLog.Write(AppLogLevel.Info, "flow2", "message from flow 2");
        });

        await Task.WhenAll(t1, t2);

        Assert.NotNull(session1File);
        Assert.NotNull(session2File);
        Assert.NotEqual(session1File, session2File);

        var text1 = File.ReadAllText(session1File);
        var text2 = File.ReadAllText(session2File);

        Assert.Contains("message from flow 1", text1);
        Assert.DoesNotContain("message from flow 2", text1);

        Assert.Contains("message from flow 2", text2);
        Assert.DoesNotContain("message from flow 1", text2);
    }

    [Fact]
    public void A_disposed_session_hands_the_logger_back_to_the_global_writer()
    {
        string sessionPath;
        using (var session = AppLog.StartSessionForTesting(_dir.Path))
        {
            sessionPath = session.SessionPath;
        }

        AppLog.Enabled = true;
        try
        {
            AppLog.Write(AppLogLevel.Info, "global", "after session disposal");

            var globalPath = AppLog.CurrentLogPath;
            Assert.NotNull(globalPath);
            Assert.NotEqual(sessionPath, globalPath);

            AppLog.Enabled = false;
            var text = File.ReadAllText(globalPath);
            Assert.Contains("after session disposal", text);
        }
        finally
        {
            AppLog.Enabled = false;
        }
    }

    [Fact]
    public void Enabling_after_a_disposed_session_opens_a_real_session_file()
    {
        using (AppLog.StartSessionForTesting(_dir.Path)) { }

        AppLog.Enabled = true;
        try
        {
            var path = AppLog.CurrentLogPath;
            Assert.NotNull(path);
            Assert.True(File.Exists(path));
        }
        finally
        {
            AppLog.Enabled = false;
        }
    }

    [Fact]
    public void Enabling_via_switch_opens_and_closes_session_without_marker_files()
    {
        LoggingSwitch.Set(true);
        var file = Assert.Single(SessionFiles());
        Assert.Equal(file, AppLog.CurrentLogPath);

        LoggingSwitch.Set(false);
        Assert.Null(AppLog.CurrentLogPath);

        Assert.Empty(Directory.GetFiles(AppPaths.MachineDirectory, "*logging.on"));
    }

    [Fact]
    public void Stale_enable_event_reconciles_to_the_switch()
    {
        LoggingSwitch.Set(false);
        AppLog.OnLoggingSwitchChanged(true);

        Assert.Null(AppLog.CurrentLogPath);
        Assert.Empty(SessionFiles());
    }

    [Fact]
    public void Flow_override_never_opens_a_process_session()
    {
        LoggingSwitch.Set(false);
        LoggingSwitch.OverrideForTesting(true);
        try
        {
            AppLog.Reconcile();

            Assert.Null(AppLog.CurrentLogPath);
            Assert.Empty(SessionFiles());
        }
        finally
        {
            LoggingSwitch.OverrideForTesting(null);
        }
    }

    [Fact]
    public void Reconcile_opens_and_closes_with_the_switch()
    {
        LoggingSwitch.Set(true);
        AppLog.Reconcile();

        var file = Assert.Single(SessionFiles());
        Assert.Equal(file, AppLog.CurrentLogPath);

        LoggingSwitch.Set(false);
        AppLog.Reconcile();

        Assert.Null(AppLog.CurrentLogPath);
        var text = File.ReadAllText(file);
        Assert.Contains("logging stopped", text);
    }
}
