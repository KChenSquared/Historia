namespace HistoriaApp.Tests;

[Trait("tier", "ui")]
public sealed class UiHeartbeatTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private sealed class Marks
    {
        private readonly object _gate = new();
        private readonly List<string> _lines = new();

        public void Add(string line)
        {
            lock (_gate)
                _lines.Add(line);
        }

        public string[] All()
        {
            lock (_gate)
                return _lines.ToArray();
        }
    }

    private static Historia.Diagnostics.UiHeartbeat Start(Marks marks, TimeSpan threshold, Func<bool>? shouldRun = null)
        => new(Dispatcher.UIThread, shouldRun ?? (() => true), marks.Add, TimeSpan.FromMilliseconds(10), threshold);

    private static void PumpUntil(Func<bool> done, string what)
    {
        var clock = Stopwatch.StartNew();
        while (!done())
        {
            Assert.True(clock.Elapsed < Patience, $"timed out waiting for {what}");
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(2);
        }
    }

    [AvaloniaFact]
    public void A_blocked_dispatcher_writes_one_stall_covering_the_whole_block()
    {
        var marks = new Marks();
        using var heartbeat = Start(marks, Historia.Diagnostics.UiHeartbeat.DefaultThreshold);

        // The probe is posted from the timer thread, so it can be in flight while this thread is
        // not pumping; block only once it is.
        var clock = Stopwatch.StartNew();
        while (!heartbeat.ProbeInFlight)
        {
            Assert.True(clock.Elapsed < Patience, "the heartbeat never posted a probe");
            Thread.Sleep(2);
        }

        Thread.Sleep(300);
        Assert.Equal(1, heartbeat.ProbesPosted);

        PumpUntil(() => marks.All().Length > 0, "the stall mark");

        var stall = Assert.Single(marks.All());
        Assert.Matches(@"^ui stall \d+ms$", stall);
        var ms = int.Parse(stall["ui stall ".Length..^"ms".Length], System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(ms >= 250, $"the stall was reported as {ms} ms for a 300 ms block");
    }

    [AvaloniaFact]
    public void An_idle_dispatcher_writes_no_stall()
    {
        var marks = new Marks();
        // The pumping thread is this test's own, so a loaded machine can delay a probe; the
        // threshold only has to stay far above that.
        using var heartbeat = Start(marks, TimeSpan.FromSeconds(5));

        PumpUntil(() => heartbeat.ProbesCompleted >= 5, "five completed probes");

        Assert.Empty(marks.All());
    }

    [AvaloniaFact]
    public void Only_one_probe_is_ever_in_flight()
    {
        var marks = new Marks();
        using var heartbeat = Start(marks, TimeSpan.FromSeconds(30));

        var clock = Stopwatch.StartNew();
        while (!heartbeat.ProbeInFlight)
        {
            Assert.True(clock.Elapsed < Patience, "the heartbeat never posted a probe");
            Thread.Sleep(2);
        }

        // Dozens of ticks fire during this wait; none may post while the first probe waits.
        Thread.Sleep(300);
        Assert.Equal(1, heartbeat.ProbesPosted);
        Assert.Equal(0, heartbeat.ProbesCompleted);

        PumpUntil(() => heartbeat.ProbesCompleted >= 1, "the first probe");
        PumpUntil(() => heartbeat.ProbesPosted >= 2, "a second probe after the first ran");
        Assert.True(heartbeat.ProbesPosted <= heartbeat.ProbesCompleted + 1);
    }

    [AvaloniaFact]
    public void Disabled_logging_creates_no_timer_and_it_follows_the_answer()
    {
        var marks = new Marks();
        var run = false;
        using var heartbeat = Start(marks, Historia.Diagnostics.UiHeartbeat.DefaultThreshold, () => run);

        Assert.False(heartbeat.IsRunning);
        Thread.Sleep(100);
        Assert.Equal(0, heartbeat.ProbesPosted);

        run = true;
        heartbeat.Reconcile();
        Assert.True(heartbeat.IsRunning);

        run = false;
        heartbeat.Reconcile();
        Assert.False(heartbeat.IsRunning);
    }
}
