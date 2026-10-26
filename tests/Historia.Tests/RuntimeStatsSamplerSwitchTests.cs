namespace Historia.Tests;

/// <summary>
/// The production sampler against the real, process-wide <see cref="LoggingSwitch"/>. The switch is
/// global, so these run in the serial collection.
/// </summary>
[Trait("tier", "unit")]
[Collection("serial")]
public sealed class RuntimeStatsSamplerSwitchTests : IDisposable
{
    private readonly string? _envBefore = Environment.GetEnvironmentVariable("HISTORIA_LOG");
    private readonly bool _switchBefore = LoggingSwitch.ProcessEnabled;

    public RuntimeStatsSamplerSwitchTests()
    {
        Environment.SetEnvironmentVariable("HISTORIA_LOG", null);
        LoggingSwitch.Set(false);
    }

    public void Dispose()
    {
        LoggingSwitch.Set(_switchBefore);
        Environment.SetEnvironmentVariable("HISTORIA_LOG", _envBefore);
    }

    [Fact]
    public void The_production_sampler_follows_the_logging_switch()
    {
        using var sampler = new RuntimeStatsSampler();
        Assert.False(sampler.IsRunning);

        LoggingSwitch.Set(true);
        Assert.True(sampler.IsRunning);

        LoggingSwitch.Set(false);
        Assert.False(sampler.IsRunning);
    }

    [Fact]
    public void A_disposed_production_sampler_no_longer_follows_the_switch()
    {
        var sampler = new RuntimeStatsSampler();
        sampler.Dispose();

        LoggingSwitch.Set(true);

        Assert.False(sampler.IsRunning);
    }
}
