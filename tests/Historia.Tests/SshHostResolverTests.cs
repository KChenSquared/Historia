namespace Historia.Tests;

/// <summary>Skips a test when no OpenSSH client is on PATH.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class SshFactAttribute : FactAttribute
{
    public SshFactAttribute()
    {
        if (SshHostResolver.FindSshOnPath() is null)
            Skip = "No ssh on PATH.";
    }
}

/// <summary>Runs the real OpenSSH client's <c>ssh -G</c> against a temporary config file; skipped
/// when no <c>ssh</c> is on PATH.</summary>
[Trait("tier", "git")]
public sealed class SshHostResolverTests : IDisposable
{
    private readonly string _dir = TestScratch.NewDirectory("historia-ssh-g-");
    private readonly string _config;

    public SshHostResolverTests()
    {
        _config = Path.Combine(_dir, "config");
        File.WriteAllText(_config, "Host alias-x\n    HostName example.invalid\n    User git\n");
    }

    public void Dispose() => TestScratch.Delete(_dir);

    [SshFact]
    public async Task An_alias_resolves_to_its_host_name()
    {
        var resolver = new SshHostResolver(_config);

        Assert.Equal("example.invalid", await resolver.ResolveHostNameAsync("alias-x", CancellationToken.None));
    }

    [SshFact]
    public async Task An_unlisted_host_resolves_to_itself()
    {
        var resolver = new SshHostResolver(_config);

        Assert.Equal("github.com-work", await resolver.ResolveHostNameAsync("github.com-work", CancellationToken.None));
    }

    [SshFact]
    public async Task A_second_lookup_is_served_from_the_cache()
    {
        var resolver = new SshHostResolver(_config);

        await resolver.ResolveHostNameAsync("alias-x", CancellationToken.None);
        await resolver.ResolveHostNameAsync("alias-x", CancellationToken.None);

        Assert.Equal(1, resolver.ProcessLaunches);
    }

    [Fact]
    public async Task A_timed_out_lookup_is_asked_again_on_the_next_call()
    {
        var calls = 0;
        var resolver = new SshHostResolver((_, _) =>
            Task.FromResult(++calls == 1 ? new SshHostResolver.Lookup(null, false) : new SshHostResolver.Lookup("example.invalid", true)));

        Assert.Null(await resolver.ResolveHostNameAsync("alias-x", CancellationToken.None));
        Assert.Equal("example.invalid", await resolver.ResolveHostNameAsync("alias-x", CancellationToken.None));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task A_definite_lookup_is_cached_even_when_it_found_nothing()
    {
        var calls = 0;
        var resolver = new SshHostResolver((_, _) =>
        {
            calls++;
            return Task.FromResult(new SshHostResolver.Lookup(null, true));
        });

        Assert.Null(await resolver.ResolveHostNameAsync("alias-x", CancellationToken.None));
        Assert.Null(await resolver.ResolveHostNameAsync("alias-x", CancellationToken.None));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("-oProxyCommand=calc")]
    [InlineData("")]
    [InlineData("two words")]
    public async Task An_option_like_or_malformed_host_is_never_passed_to_ssh(string host)
    {
        var resolver = new SshHostResolver(_config);

        Assert.Null(await resolver.ResolveHostNameAsync(host, CancellationToken.None));
        Assert.Equal(0, resolver.ProcessLaunches);
    }

    [Fact]
    public void The_hostname_line_is_parsed_from_ssh_G_output()
        => Assert.Equal("github.com", SshHostResolver.ParseHostName("user git\r\nhostname github.com\r\nport 22\r\n"));
}
