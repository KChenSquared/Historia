namespace Historia.Diagnostics;

/// <summary>
/// Process-wide on/off switch for <see cref="AppLog"/> and <see cref="PerfLog"/>.
/// Starts disabled; initialized at startup from settings or the <c>HISTORIA_LOG</c>
/// environment variable.
/// </summary>
public static class LoggingSwitch
{
    private const string EnvVar = "HISTORIA_LOG";
    private static readonly AsyncLocal<bool?> TestOverride = new();
    private static bool _enabled;
    private static readonly object Gate = new();

    /// <summary>
    /// Raised after the effective process-wide logging state flips. Never raised under a lock.
    /// The argument is the value at the time of the flip; a handler that must be exact re-reads <see cref="ProcessEnabled"/>.
    /// </summary>
    public static event Action<bool>? Changed;

    /// <summary>
    /// Gets the process-wide logging switch value, ignoring any flow-local test override.
    /// </summary>
    public static bool ProcessEnabled => Volatile.Read(ref _enabled);

    /// <summary>
    /// Gets whether logging is forced on by the <c>HISTORIA_LOG</c> environment variable.
    /// </summary>
    internal static bool IsForcedByEnvironment => IsEnvForced();

    /// <summary>
    /// Gets whether logging is enabled. Flow-local test override takes precedence over the process-wide switch.
    /// </summary>
    public static bool Enabled => TestOverride.Value ?? Volatile.Read(ref _enabled);

    /// <summary>
    /// Initializes the process-wide switch at startup from the persisted setting and environment.
    /// </summary>
    public static void Initialize(bool settingValue)
    {
        SetInternal(settingValue);
    }

    /// <summary>
    /// Updates the process-wide switch for live settings changes. If <c>HISTORIA_LOG</c> is set,
    /// logging remains enabled regardless of <paramref name="settingValue"/>.
    /// </summary>
    public static void Set(bool settingValue)
    {
        SetInternal(settingValue);
    }

    /// <summary>
    /// Overrides <see cref="Enabled"/> for the current async flow.
    /// </summary>
    public static void OverrideForTesting(bool? enabled)
    {
        TestOverride.Value = enabled;
    }

    private static void SetInternal(bool settingValue)
    {
        var target = settingValue || IsEnvForced();
        lock (Gate)
        {
            if (_enabled == target)
                return;
            _enabled = target;
        }

        try
        {
            Changed?.Invoke(target);
        }
        catch { /* diagnostics events must never throw */ }
    }

    private static bool IsEnvForced() =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(EnvVar));
}
