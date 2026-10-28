namespace Historia.Tests;

/// <summary>Minimal DI provider + a real <see cref="RepositoryContext"/> for integration tests.</summary>
public sealed class MiniHost : IAsyncDisposable
{
    private static readonly Lazy<IGitExecutableLocator> SharedLocator = new(() =>
    {
        var locator = new GitExecutableLocator();
        // One timed-out `git --version` probe on a loaded machine would leave every test in the run
        // on "git not found", so a failed resolution is tried again.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (locator.ResolveAsync(null).GetAwaiter().GetResult().Found)
                break;
        }
        return locator;
    });

    private readonly StateDir _state = new();
    public ServiceProvider Provider { get; }
    public SettingsService Settings { get; }
    public IGitExecutableLocator Locator => SharedLocator.Value;

    public MiniHost()
    {
        Settings = SettingsService.Load();
        var sc = new ServiceCollection();
        sc.AddSingleton<ISettingsService>(Settings);
        sc.AddSingleton<IRepoLayoutService, RepoLayoutService>();
        sc.AddSingleton<IGitExecutableLocator>(SharedLocator.Value);
        sc.AddSingleton<IGitCliService, GitCliService>();
        sc.AddSingleton<ICredentialVaultService, FakeVault>();
        Provider = sc.BuildServiceProvider();
    }

    public RepositoryContext Open(string path) => RepositoryContext.Open(path, Provider);

    private readonly List<ServiceProvider> _extraProviders = [];

    /// <summary>Opens <paramref name="path"/> with <paramref name="cli"/> in place of the real
    /// CLI; the provider behind it is disposed with this host.</summary>
    public RepositoryContext Open(string path, IGitCliService cli)
    {
        var sc = new ServiceCollection();
        sc.AddSingleton<ISettingsService>(Settings);
        sc.AddSingleton<IRepoLayoutService, RepoLayoutService>();
        sc.AddSingleton<IGitExecutableLocator>(SharedLocator.Value);
        sc.AddSingleton(cli);
        sc.AddSingleton<ICredentialVaultService, FakeVault>();
        var provider = sc.BuildServiceProvider();
        _extraProviders.Add(provider);
        return RepositoryContext.Open(path, provider);
    }

    public IGitCliService Cli => Provider.GetRequiredService<IGitCliService>();

    public async ValueTask DisposeAsync()
    {
        foreach (var provider in _extraProviders)
            await provider.DisposeAsync();
        await Provider.DisposeAsync();
        await Settings.DisposeAsync();
        _state.Dispose();
    }
}
