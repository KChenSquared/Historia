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

    private sealed class FixedLocator(GitExecutableInfo info) : IGitExecutableLocator
    {
        public GitExecutableInfo Current => info;
        public Task<GitExecutableInfo> ResolveAsync(string? userOverridePath, CancellationToken ct = default) => Task.FromResult(info);
        public Task<GitExecutableInfo> WhenResolvedAsync(CancellationToken ct = default) => Task.FromResult(info);
    }

    private GitExecutableInfo BundledInfo(string bundledRoot) => new()
    {
        Found = true,
        Source = GitSource.Bundled,
        BundledRoot = bundledRoot,
        Path = Path.Combine(bundledRoot, "ucrt64", "bin", "git.exe"),
        Version = new Version(2, 56, 0),
    };

    [Fact]
    public void Bundled_git_runs_the_bundled_ssh()
    {
        var bundledRoot = Path.Combine(_dir, "git");
        var usrBin = Path.Combine(bundledRoot, "usr", "bin");
        Directory.CreateDirectory(usrBin);
        var ssh = Path.Combine(usrBin, OperatingSystem.IsWindows() ? "ssh.exe" : "ssh");
        File.WriteAllText(ssh, "");

        var resolver = new SshHostResolver(new FixedLocator(BundledInfo(bundledRoot)));

        Assert.Equal(ssh, resolver.ResolveSshExecutable());
    }

    [Fact]
    public void Bundled_git_ignores_a_bundled_ssh_cmd()
    {
        var bundledRoot = Path.Combine(_dir, "git");
        var usrBin = Path.Combine(bundledRoot, "usr", "bin");
        Directory.CreateDirectory(usrBin);
        File.WriteAllText(Path.Combine(usrBin, "ssh.cmd"), "");

        var resolver = new SshHostResolver(new FixedLocator(BundledInfo(bundledRoot)));

        Assert.Equal(SshHostResolver.FindSshOnPath(), resolver.ResolveSshExecutable());
    }

    [Fact]
    public void An_installed_git_uses_the_ssh_on_path()
    {
        var installed = new GitExecutableInfo { Found = true, Source = GitSource.Installed, Path = "git", Version = new Version(2, 50, 0) };

        var resolver = new SshHostResolver(new FixedLocator(installed));

        Assert.Equal(SshHostResolver.FindSshOnPath(), resolver.ResolveSshExecutable());
    }

    [Fact]
    public void The_app_gives_every_repository_a_resolver_that_knows_the_git_in_use()
    {
        var root = RepoRoot();
        var app = File.ReadAllText(Path.Combine(root, "Historia.App", "App.axaml.cs"));
        var context = File.ReadAllText(Path.Combine(root, "Historia.Core", "Services", "Workspace", "RepositoryContext.cs"));

        Assert.Contains("new Historia.Services.Network.SshHostResolver(sp.GetRequiredService<IGitExecutableLocator>())", app);
        Assert.Contains("services.GetService<ISshHostResolver>()", context);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Historia.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
