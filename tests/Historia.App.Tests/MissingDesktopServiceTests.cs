namespace HistoriaApp.Tests;

/// <summary>Which exceptions the last-chance handlers treat as a missing optional desktop service
/// rather than an app failure.</summary>
[Trait("tier", "unit")]
public sealed class MissingDesktopServiceTests
{
    private const string ServiceUnknown = "org.freedesktop.DBus.Error.ServiceUnknown";
    private const string NameHasNoOwner = "org.freedesktop.DBus.Error.NameHasNoOwner";

    [Theory]
    [InlineData(ServiceUnknown)]
    [InlineData(NameHasNoOwner)]
    public void A_missing_service_error_is_recognized(string errorName) =>
        Assert.True(MissingDesktopService.IsOnlyMissingService(
            new DBusException(errorName, "The name com.canonical.AppMenu.Registrar was not provided by any .service files")));

    [Fact]
    public void An_unobserved_task_aggregate_of_missing_services_is_recognized()
    {
        var aggregate = new AggregateException(
            new DBusException(ServiceUnknown, "menu"),
            new AggregateException(new DBusException(NameHasNoOwner, "tray")));

        Assert.True(MissingDesktopService.IsOnlyMissingService(aggregate));
    }

    [Fact]
    public void Other_dbus_errors_are_not_recognized() =>
        Assert.False(MissingDesktopService.IsOnlyMissingService(
            new DBusException("org.freedesktop.DBus.Error.AccessDenied", "denied")));

    [Fact]
    public void An_aggregate_mixing_in_another_failure_is_not_recognized()
    {
        var aggregate = new AggregateException(
            new DBusException(ServiceUnknown, "menu"),
            new InvalidOperationException("real bug"));

        Assert.False(MissingDesktopService.IsOnlyMissingService(aggregate));
    }

    [Fact]
    public void An_empty_aggregate_is_not_recognized() =>
        Assert.False(MissingDesktopService.IsOnlyMissingService(new AggregateException()));

    [Fact]
    public void A_non_dbus_exception_is_not_recognized() =>
        Assert.False(MissingDesktopService.IsOnlyMissingService(new InvalidOperationException()));
}
