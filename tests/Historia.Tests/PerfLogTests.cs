namespace Historia.Tests;

[Trait("tier", "unit")]

public sealed class PerfLogTests : IDisposable
{
    public void Dispose()
    {
        PerfLog.OverrideEnabledForTesting(null);
    }

    [Fact]
    public async Task Enabled_is_isolated_between_concurrent_flows()
    {
        using var barrier = new Barrier(2);
        bool? flow1Observed = null;
        bool? flow2Observed = null;

        var t1 = Task.Run(() =>
        {
            PerfLog.OverrideEnabledForTesting(true);
            barrier.SignalAndWait();
            flow1Observed = PerfLog.Enabled;
        });

        var t2 = Task.Run(() =>
        {
            PerfLog.OverrideEnabledForTesting(false);
            barrier.SignalAndWait();
            flow2Observed = PerfLog.Enabled;
        });

        await Task.WhenAll(t1, t2);

        Assert.True(flow1Observed);
        Assert.False(flow2Observed);
    }

    /// <summary>An argument whose formatting is observable: <see cref="Calls"/> counts every
    /// <c>ToString</c>, and each one throws.</summary>
    internal sealed class FormatBomb
    {
        public int Calls { get; private set; }

        public override string ToString()
        {
            Calls++;
            throw new InvalidOperationException("formatted while logging was disabled");
        }
    }

    [Fact]
    public void A_disabled_mark_or_scope_never_formats_its_interpolated_arguments()
    {
        PerfLog.OverrideEnabledForTesting(false);
        var bomb = new FormatBomb();

        PerfLog.Mark($"never {bomb}");
        PerfLog.Mark($"never {bomb,8} {bomb:x} {bomb,-4:x}");
        using (PerfLog.Scope($"never {bomb}")) { }

        Assert.Equal(0, bomb.Calls);
    }

    [Fact]
    public void Scope_creates_timer_only_when_enabled()
    {
        PerfLog.OverrideEnabledForTesting(false);
        using var disabledScope = PerfLog.Scope("test-disabled");

        PerfLog.OverrideEnabledForTesting(true);
        using var enabledScope = PerfLog.Scope("test-enabled");
        Assert.NotNull(enabledScope);
    }
}

/// <summary>Swaps the process-wide perf-log writer, so it runs alone.</summary>
[Trait("tier", "unit")]
[Collection("serial")]
public sealed class PerfLogWriterTests : IDisposable
{
    private readonly StateDir _state = new();

    public void Dispose()
    {
        PerfLog.OverrideEnabledForTesting(null);
        _state.Dispose();
    }

    [Fact]
    public void Marks_reach_perf_log_once_flushed()
    {
        PerfLog.OverrideEnabledForTesting(true);
        PerfLog.Mark("perf-log-writer-test: one");
        PerfLog.Mark("perf-log-writer-test: two");

        Assert.True(PerfLog.Flush(TimeSpan.FromSeconds(10)));
        var lines = File.ReadAllLines(PerfLog.LogPath);
        Assert.Contains(lines, l => l.EndsWith("perf-log-writer-test: one", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.EndsWith("perf-log-writer-test: two", StringComparison.Ordinal));
    }

    [Fact]
    public void Mark_line_starts_with_full_date()
    {
        PerfLog.OverrideEnabledForTesting(true);
        PerfLog.Mark("date test");
        Assert.True(PerfLog.Flush(TimeSpan.FromSeconds(10)));
        var lines = File.ReadAllLines(PerfLog.LogPath);
        var line = Assert.Single(lines, l => l.EndsWith("date test", StringComparison.Ordinal));
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}  \+", line);
    }

    [Theory]
    [InlineData("fi-FI")]
    [InlineData("th-TH")]
    public async Task Mark_lines_survive_a_trim_under_any_culture(string culture)
    {
        var saved = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo(culture);
            PerfLog.OverrideEnabledForTesting(true);
            PerfLog.Mark("culture test");
            Assert.True(PerfLog.Flush(TimeSpan.FromSeconds(10)));

            await PerfLog.TrimAsync(DateTime.Now.AddHours(-48));

            var line = Assert.Single(File.ReadAllLines(PerfLog.LogPath), l => l.EndsWith("culture test", StringComparison.Ordinal));
            Assert.StartsWith(DateTime.Now.ToString("yyyy-", System.Globalization.CultureInfo.InvariantCulture), line);
            Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}  \+ *\d+\.\dms", line);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public void An_interpolated_mark_writes_the_same_text_as_the_string_overload()
    {
        var saved = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("fi-FI");
            PerfLog.OverrideEnabledForTesting(true);
            var value = 1234.5;
            var name = "abc";
            var count = 1234567;

            PerfLog.Mark($"parity {value:F2} [{name,6}] [{name,-6}] {count:N0} {null as string}|");
            string viaString = $"parity {value:F2} [{name,6}] [{name,-6}] {count:N0} {null as string}|";
            PerfLog.Mark(viaString);
            Assert.True(PerfLog.Flush(TimeSpan.FromSeconds(10)));

            var lines = File.ReadAllLines(PerfLog.LogPath).Where(l => l.Contains("parity ", StringComparison.Ordinal)).ToArray();
            Assert.Equal(2, lines.Length);
            Assert.EndsWith(viaString, lines[0], StringComparison.Ordinal);
            Assert.EndsWith(viaString, lines[1], StringComparison.Ordinal);
            Assert.Contains("1234,50", viaString); // the current culture, as the string overload gets it
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public void An_interpolated_scope_logs_its_formatted_label_when_enabled()
    {
        PerfLog.OverrideEnabledForTesting(true);
        var n = 7;
        using (PerfLog.Scope($"interpolated scope {n}")) { }

        Assert.True(PerfLog.Flush(TimeSpan.FromSeconds(10)));
        Assert.Contains(File.ReadAllLines(PerfLog.LogPath), l => l.Contains("interpolated scope 7: ", StringComparison.Ordinal) && l.EndsWith("ms", StringComparison.Ordinal));
    }

    [Fact]
    public void A_mark_never_waits_on_a_stalled_write()
    {
        PerfLog.OverrideEnabledForTesting(true);
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        var stalled = QueuedLineWriter.OpenPerBatch("test-perf-writer",
            p => new QueuedLineWriterTests.StallingStream(QueuedLineWriterTests.Append(p), release, entered));
        var previous = PerfLog.SwapWriterForTesting(stalled);
        try
        {
            PerfLog.Mark("first");
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "the writer thread never reached the file");

            // The stall holds until release, so a mark waiting on it would never return; the
            // budget only has to clear a slow runner's cost for the loop itself.
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < 200; i++)
                PerfLog.Mark($"behind a stalled write {i}");
            Assert.True(sw.ElapsedMilliseconds < 2000, $"a mark waited on the stalled write: {sw.ElapsedMilliseconds} ms");
        }
        finally
        {
            release.Set();
            PerfLog.SwapWriterForTesting(previous);
            stalled.Dispose();
        }

        Assert.Equal(201, QueuedLineWriterTests.ReadShared(PerfLog.LogPath).Length);
    }
}
