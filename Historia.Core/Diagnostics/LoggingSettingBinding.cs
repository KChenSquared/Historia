namespace Historia.Diagnostics;

/// <summary>
/// Binds changes in <see cref="AppSettings.LoggingEnabled"/> to <see cref="LoggingSwitch"/> on a background thread.
/// Applying the switch can drain a log writer, which must not run on the thread that changed the setting.
/// </summary>
public sealed class LoggingSettingBinding : IDisposable
{
    private const int StateIdle = 0;
    private const int StateRunning = 1;
    private const int StateRunningDirty = 2;

    private readonly ISettingsService _settings;
    private int _state;
    private bool _disposed;

    private LoggingSettingBinding(ISettingsService settings)
    {
        _settings = settings;
    }

    /// <summary>
    /// Attaches a binding that observes settings changes and applies <see cref="AppSettings.LoggingEnabled"/>
    /// to <see cref="LoggingSwitch"/> off the calling thread.
    /// </summary>
    public static LoggingSettingBinding Attach(ISettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var binding = new LoggingSettingBinding(settings);
        settings.Changed += binding.OnSettingsChanged;
        return binding;
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (_disposed)
            return;

        if (e.OldSettings.LoggingEnabled == e.NewSettings.LoggingEnabled)
            return;

        ScheduleApply();
    }

    private void ScheduleApply()
    {
        while (true)
        {
            var current = Volatile.Read(ref _state);
            if (current == StateIdle)
            {
                if (Interlocked.CompareExchange(ref _state, StateRunning, StateIdle) == StateIdle)
                {
                    ThreadPool.UnsafeQueueUserWorkItem(static state => state.RunApply(), this, preferLocal: false);
                    return;
                }
            }
            else if (current == StateRunning)
            {
                if (Interlocked.CompareExchange(ref _state, StateRunningDirty, StateRunning) == StateRunning)
                {
                    return;
                }
            }
            else
            {
                // Already StateRunningDirty; worker will re-read Current before returning to idle.
                return;
            }
        }
    }

    private void RunApply()
    {
        while (true)
        {
            try
            {
                LoggingSwitch.Set(_settings.Current.LoggingEnabled);
            }
            catch
            {
                // Diagnostics must never throw.
            }

            while (true)
            {
                var current = Volatile.Read(ref _state);
                if (current == StateRunningDirty)
                {
                    if (Interlocked.CompareExchange(ref _state, StateRunning, StateRunningDirty) == StateRunningDirty)
                    {
                        break;
                    }
                }
                else
                {
                    if (Interlocked.CompareExchange(ref _state, StateIdle, StateRunning) == StateRunning)
                    {
                        return;
                    }
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _settings.Changed -= OnSettingsChanged;
    }
}
