namespace Historia.Platform;

/// <summary>App-side <see cref="IUiDispatcher"/> over <see cref="Dispatcher.UIThread"/>.</summary>
public sealed class UiDispatcher : IUiDispatcher
{
    public bool CheckAccess() => Dispatcher.UIThread.CheckAccess();

    public void Post(Action action) => Dispatcher.UIThread.Post(action);

    public void Post(Action action, UiPriority priority) => Dispatcher.UIThread.Post(action, Map(priority));

    public IUiTimer CreateTimer(TimeSpan interval, Action tick) => new Timer(interval, tick, DispatcherPriority.Background);

    public IUiTimer CreateTimer(TimeSpan interval, Action tick, UiPriority priority) => new Timer(interval, tick, Map(priority));

    private static DispatcherPriority Map(UiPriority priority) => priority switch
    {
        UiPriority.Normal => DispatcherPriority.Normal,
        UiPriority.Background => DispatcherPriority.Background,
        _ => throw new ArgumentOutOfRangeException(nameof(priority), priority, null),
    };

    private sealed class Timer : IUiTimer
    {
        private readonly DispatcherTimer _timer;

        public Timer(TimeSpan interval, Action tick, DispatcherPriority priority)
        {
            _timer = new DispatcherTimer(priority) { Interval = interval };
            _timer.Tick += (_, _) => tick();
        }

        public void Start() => _timer.Start();
        public void Stop() => _timer.Stop();
        public void Dispose() => _timer.Stop();
    }
}
