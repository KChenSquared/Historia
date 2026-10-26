namespace HistoriaApp.Tests;

/// <summary>An exception inside a layout pass leaves the layout manager's queued flag set, so no
/// later invalidation lays anything out. <see cref="LayoutRecovery"/> re-runs layout from the
/// dispatcher's unhandled-exception handler, capped per time window.</summary>
[Trait("tier", "ui")]
public class LayoutRecoveryTests
{
    /// <summary>Throws from <see cref="ArrangeOverride"/> while <see cref="ThrowsRemaining"/> is
    /// positive (counting down) or negative (always).</summary>
    private sealed class Thrower : Control
    {
        public int ThrowsRemaining;

        protected override Size ArrangeOverride(Size finalSize)
        {
            if (ThrowsRemaining != 0)
            {
                if (ThrowsRemaining > 0) ThrowsRemaining--;
                throw new InvalidOperationException("arrange failed");
            }

            return base.ArrangeOverride(finalSize);
        }
    }

    private sealed class Rig : IDisposable
    {
        private readonly DispatcherUnhandledExceptionEventHandler _handler;
        public Window Window { get; }
        public Thrower Thrower { get; } = new() { Height = 10 };
        public Border Marker { get; } = new() { Height = 10, Width = 50 };
        public TimeSpan Now { get; set; }
        public int RecoveryPasses { get; set; }
        public int Unhandled { get; private set; }

        public Rig(bool withRecovery)
        {
            Window = new Window
            {
                Width = 300,
                Height = 200,
                Content = new StackPanel { Children = { Marker, Thrower } },
            };

            var recovery = new LayoutRecovery(
                () => { RecoveryPasses++; return [Window]; },
                action => Dispatcher.UIThread.Post(action, DispatcherPriority.Background),
                () => Now);

            _handler = (_, args) =>
            {
                Unhandled++;
                if (withRecovery) recovery.OnDispatcherException();
                args.Handled = true;
            };
            Dispatcher.UIThread.UnhandledException += _handler;

            Window.Show();
            Pump();
        }

        public bool Queued
        {
            get
            {
                const BindingFlags all = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                var manager = typeof(ILayoutRoot).GetProperty("LayoutManager", all)!.GetValue(Window)!;
                var queued = manager.GetType().GetField("_queued", all)!;
                return (bool)queued.GetValue(manager)!;
            }
        }

        public void Pump()
        {
            for (var i = 0; i < 4; i++)
            {
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
            }
        }

        /// <summary>Makes the next queued layout pass throw once, then lets it run.</summary>
        public void ThrowInNextPass()
        {
            Thrower.ThrowsRemaining = 1;
            Thrower.InvalidateArrange();
            Pump();
        }

        public void Dispose()
        {
            Dispatcher.UIThread.UnhandledException -= _handler;
            Window.Close();
        }
    }

    [AvaloniaFact]
    public void Without_recovery_an_exception_in_a_queued_pass_wedges_layout()
    {
        using var rig = new Rig(withRecovery: false);

        rig.ThrowInNextPass();
        Assert.Equal(1, rig.Unhandled);
        Assert.True(rig.Queued);

        rig.Marker.Width = 80;
        rig.Pump();

        Assert.Equal(50, rig.Marker.Bounds.Width);
    }

    [AvaloniaFact]
    public void Recovery_after_an_exception_in_a_queued_pass_lays_out_later_invalidations()
    {
        using var rig = new Rig(withRecovery: true);

        rig.ThrowInNextPass();
        Assert.Equal(1, rig.Unhandled);
        Assert.False(rig.Queued);
        Assert.Equal(1, rig.RecoveryPasses);

        rig.Marker.Width = 80;
        rig.Pump();

        Assert.Equal(80, rig.Marker.Bounds.Width);
        Assert.Equal(1, rig.Unhandled);
    }

    [AvaloniaFact]
    public void A_control_that_always_throws_cannot_make_recovery_loop()
    {
        using var rig = new Rig(withRecovery: true);
        rig.Thrower.ThrowsRemaining = -1;

        rig.Thrower.InvalidateArrange();
        rig.Pump();

        Assert.Equal(1, rig.Unhandled);
        Assert.Equal(1, rig.RecoveryPasses);

        for (var i = 0; i < 10; i++)
        {
            rig.Marker.Width = 60 + i;
            rig.Pump();
        }

        Assert.True(rig.RecoveryPasses <= LayoutRecovery.MaxRecoveriesPerWindow);
    }

    [AvaloniaFact]
    public void Recoveries_are_capped_per_window_and_resume_once_it_elapses()
    {
        using var rig = new Rig(withRecovery: true);
        var recovery = new LayoutRecovery(
            () => { rig.RecoveryPasses++; return []; },
            action => Dispatcher.UIThread.Post(action, DispatcherPriority.Background),
            () => rig.Now);
        var before = rig.RecoveryPasses;

        for (var i = 0; i < 8; i++)
        {
            rig.Now = TimeSpan.FromSeconds(i);
            recovery.OnDispatcherException();
            rig.Pump();
        }

        Assert.Equal(before + LayoutRecovery.MaxRecoveriesPerWindow, rig.RecoveryPasses);

        rig.Now = LayoutRecovery.Window;
        recovery.OnDispatcherException();
        rig.Pump();

        Assert.Equal(before + LayoutRecovery.MaxRecoveriesPerWindow + 1, rig.RecoveryPasses);
    }
}
