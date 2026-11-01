namespace Historia.Diagnostics;

/// <summary>
/// Trims logs older than 48 hours once per auto-fetch cycle, after the idle wait.
/// Consecutive trim failures while the Enable logging setting is on are tracked and persisted across launches.
/// After three consecutive failed rounds, the setting turns itself off (even when <c>HISTORIA_LOG</c> forces logging on).
/// </summary>
public sealed class LogTrimmer : ILogTrimmer
{
    public const int FailuresBeforeDisable = 3;

    private static readonly TimeSpan CutoffAge = TimeSpan.FromHours(48);
    private static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(2);

    private readonly ISettingsService _settings;
    private readonly TimeProvider _timeProvider;
    private readonly string _logsDirectory;
    private readonly string? _stateFilePath;

    private LogTrimFailureStore? _failureStore;
    private int _failureCount;
    private bool _countLoaded;

    public LogTrimmer(
        ISettingsService settings,
        TimeProvider? timeProvider = null,
        string? logsDirectory = null,
        string? stateFile = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logsDirectory = logsDirectory ?? AppPaths.LogsDirectory;
        _stateFilePath = stateFile;
    }

    private void EnsureCountLoaded()
    {
        if (!_countLoaded)
        {
            _failureStore = new LogTrimFailureStore(_stateFilePath ?? AppPaths.LogTrimStateFile);
            _failureCount = _failureStore.Load();
            _countLoaded = true;
        }
    }

    public async Task TrimAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        EnsureCountLoaded();

        var now = _timeProvider.GetLocalNow().DateTime;
        var cutoff = now - CutoffAge;

        try
        {
            ct.ThrowIfCancellationRequested();
            LogRetention.TrimDevLogs(_logsDirectory, cutoff, AppLog.CurrentLogPath);

            ct.ThrowIfCancellationRequested();
            var perfPath = Path.Combine(_logsDirectory, "perf.log");
            await PerfLog.TrimAsync(cutoff, perfPath).ConfigureAwait(false);

            if (_failureCount != 0)
            {
                _failureCount = 0;
                _failureStore!.Save(0);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (!_settings.Current.LoggingEnabled)
            {
                if (_failureCount != 0)
                {
                    _failureCount = 0;
                    _failureStore!.Save(0);
                }

                AppLog.Write(AppLogLevel.Warn, "logs", "Trimming logs older than 48 hours failed.", ex);
                return;
            }

            _failureCount++;
            _failureStore!.Save(_failureCount);

            if (_failureCount < FailuresBeforeDisable)
            {
                AppLog.Write(
                    AppLogLevel.Warn,
                    "logs",
                    $"Trimming logs older than 48 hours failed ({_failureCount} of {FailuresBeforeDisable}). After {FailuresBeforeDisable} failed rounds in a row, the Enable logging setting is turned off.",
                    ex);
            }
            else
            {
                var msg = $"Trimming logs older than 48 hours failed {FailuresBeforeDisable} rounds in a row; the Enable logging setting was turned off.";
                if (LoggingSwitch.IsForcedByEnvironment)
                    msg += " Logging stays on while HISTORIA_LOG is set.";

                AppLog.Write(AppLogLevel.Error, "logs", msg, ex);
                AppLog.Flush(FlushTimeout);
                try
                {
                    await _settings.UpdateAsync(s => s with { LoggingEnabled = false }, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // settings failure must not throw
                }

                _failureCount = 0;
                _failureStore!.Save(0);
            }
        }
    }
}
