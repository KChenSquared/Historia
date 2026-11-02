namespace Historia.Platform;

/// <summary>
/// Recognizes failures from Avalonia's optional Linux desktop integrations (global app menu,
/// tray icon, input methods, portal settings and the portal file chooser) when the D-Bus service
/// behind them is not running, as on WSL or a desktop without a global menu bar. Avalonia starts
/// some of these calls without awaiting them, so the failure reaches the last-chance handlers as
/// an unobserved task exception even though the app keeps working without the feature.
/// </summary>
internal static class MissingDesktopService
{
    private static readonly HashSet<string> MissingServiceErrors = new(StringComparer.Ordinal)
    {
        "org.freedesktop.DBus.Error.ServiceUnknown",
        "org.freedesktop.DBus.Error.NameHasNoOwner",
    };

    /// <summary>
    /// True when <paramref name="ex"/> is, or aggregates only, D-Bus errors saying the requested
    /// service does not exist on the session bus.
    /// </summary>
    public static bool IsOnlyMissingService(Exception ex)
    {
        if (ex is AggregateException aggregate)
        {
            var inner = aggregate.Flatten().InnerExceptions;
            return inner.Count > 0 && inner.All(IsMissingService);
        }
        return IsMissingService(ex);
    }

    private static bool IsMissingService(Exception ex) =>
        ex is DBusException dbus && MissingServiceErrors.Contains(dbus.ErrorName);
}
