namespace HistoriaApp.Tests;

/// <summary>The cursor blink timer runs only while the control has focus in an active window.</summary>
[Trait("tier", "ui")]
public class TerminalBlinkTests
{
    private sealed class FakeTimer : IUiTimer
    {
        public bool Running { get; private set; }
        public int Starts { get; private set; }
        public void Start() { Running = true; Starts++; }
        public void Stop() => Running = false;
        public void Dispose() => Running = false;
    }

    private sealed class FakeDispatcher : IUiDispatcher
    {
        public List<FakeTimer> Timers { get; } = [];
        public bool CheckAccess() => true;
        public void Post(Action action) => action();

        public IUiTimer CreateTimer(TimeSpan interval, Action tick)
        {
            var timer = new FakeTimer();
            Timers.Add(timer);
            return timer;
        }
    }

    private static (Window Window, TerminalControl Control, FakeTimer Timer) Show()
    {
        var dispatcher = new FakeDispatcher();
        var control = new TerminalControl();
        control.Attach(TerminalRenderPoolTests.Services(dispatcher));

        var window = new Window { Content = control, Width = 640, Height = 384 };
        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return (window, control, Assert.Single(dispatcher.Timers));
    }

    // Headless windows never lose activation on their own, and the platform callbacks the window
    // subscribes to are not public, so the window's own handler is reached through them.
    private static void RaisePlatformActivation(Window window, string callback)
    {
        var impl = window.PlatformImpl!;
        var property = impl.GetType().GetProperty(callback)
            ?? throw new InvalidOperationException($"{callback} not found on {impl.GetType()}");
        ((Action?)property.GetValue(impl))?.Invoke();
    }

    [AvaloniaFact]
    public void An_unfocused_terminal_never_starts_its_timer()
    {
        var (window, control, timer) = Show();
        try
        {
            Assert.False(control.IsFocused);
            Assert.False(timer.Running);
            Assert.Equal(0, timer.Starts);
        }
        finally
        {
            window.Close();
            control.Teardown();
        }
    }

    [AvaloniaFact]
    public void Focus_starts_the_timer_and_losing_it_stops_it()
    {
        var (window, control, timer) = Show();
        try
        {
            control.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.True(control.IsFocused);
            Assert.True(timer.Running);

            TopLevel.GetTopLevel(control)!.FocusManager!.ClearFocus();
            Dispatcher.UIThread.RunJobs();
            Assert.False(control.IsFocused);
            Assert.False(timer.Running);

            control.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.True(timer.Running);
        }
        finally
        {
            window.Close();
            control.Teardown();
        }
    }

    [AvaloniaFact]
    public void A_deactivated_window_stops_the_timer_and_activation_restarts_it()
    {
        var (window, control, timer) = Show();
        try
        {
            control.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.True(timer.Running);

            RaisePlatformActivation(window, "Deactivated");
            Assert.True(control.IsFocused);
            Assert.False(timer.Running);

            RaisePlatformActivation(window, "Activated");
            Assert.True(timer.Running);
        }
        finally
        {
            window.Close();
            control.Teardown();
        }
    }

    [AvaloniaFact]
    public void Leaving_the_visual_tree_stops_the_timer()
    {
        var (window, control, timer) = Show();
        try
        {
            control.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.True(timer.Running);

            window.Content = null;
            Dispatcher.UIThread.RunJobs();
            Assert.False(timer.Running);
        }
        finally
        {
            window.Close();
            control.Teardown();
        }
    }
}
