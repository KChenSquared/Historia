namespace HistoriaApp.Tests;

[Trait("tier", "ui")]

/// <summary>UI half: a submodule-linked tab renders shorter and without its own leading
/// separator, so a group reads as one continuous visual unit — the owner keeps the full
/// treatment. Built against a real <see cref="WorkspaceService"/> (mirroring
/// <c>ShortcutBarLayoutTests</c>'s full-service DI setup) with one owner tab and one submodule
/// tab opened under it via <see cref="IWorkspaceService.OpenSubmoduleAsync"/>.</summary>
public sealed class AppBandLinkedTabsTests : IAsyncDisposable
{
    private const double RowHeight = 31;
    private const double LinkedHeight = 25;

    private readonly string _ownerDir = TestScratch.NewDirectory("historia-test-owner-");
    private readonly string _childDir;
    private ServiceProvider? _sp;

    public AppBandLinkedTabsTests()
    {
        LibGit2Sharp.Repository.Init(_ownerDir);

        _childDir = Path.Combine(_ownerDir, "vendor", "lib");
        Directory.CreateDirectory(_childDir);
        LibGit2Sharp.Repository.Init(_childDir);
        using (var childRepo = new LibGit2Sharp.Repository(_childDir))
        {
            File.WriteAllText(Path.Combine(_childDir, "a.txt"), "x");
            LibGit2Sharp.Commands.Stage(childRepo, "*");
            var sig = new LibGit2Sharp.Signature("Test User", "test@example.com", DateTimeOffset.Now);
            childRepo.Commit("c", sig, sig, new LibGit2Sharp.CommitOptions());
        }
    }

    private static ServiceProvider CreateServices()
    {
        var sc = new ServiceCollection();
        var settings = SettingsService.Load();
        sc.AddSingleton<ISettingsService>(settings);
        sc.AddSingleton<SettingsService>(settings);
        sc.AddSingleton<IUiDispatcher, Historia.Platform.UiDispatcher>();
        sc.AddSingleton<IDialogService, Historia.Platform.AvaloniaDialogService>();
        sc.AddSingleton<IScrollDirectionProbe, Historia.Platform.ScrollDirectionProbe>();
        sc.AddSingleton<IRepoActionBus, RepoActionBus>();
        sc.AddSingleton<IRepoNetworkActionRunner, RepoNetworkActionRunner>();
        sc.AddSingleton<IAutoFetchHost, AutoFetchHost>();
        sc.AddSingleton<IAutoFetchService, AutoFetchService>();
        sc.AddSingleton<INavigationBus, NavigationBus>();
        sc.AddSingleton<IKeyboardCommandRouter, KeyboardCommandRouter>();
        sc.AddSingleton<INotificationService, NotificationService>();
        sc.AddSingleton<IRepoLayoutService, RepoLayoutService>();
        sc.AddSingleton<ISystemThemeProbe, FakeThemeProbe>();
        sc.AddSingleton<IThemeService, ThemeService>();
        sc.AddSingleton<ICustomThemeService, CustomThemeService>();
        sc.AddSingleton<IWorkspaceService, WorkspaceService>();
        sc.AddSingleton<IProfileService, ProfileService>();
        sc.AddSingleton<IGitCliService, GitCliService>();
        var locator = new GitExecutableLocator();
        locator.ResolveAsync(null).GetAwaiter().GetResult();
        sc.AddSingleton<IGitExecutableLocator>(locator);
        sc.AddSingleton<IBackgroundWorkGate, BackgroundWorkGate>();
        sc.AddSingleton<IGraphLayoutService, GraphLayoutService>();
        sc.AddSingleton<IRepoStateCache, RepoStateCache>();
        sc.AddSingleton<IStashService, StashService>();
        sc.AddSingleton<IConflictService, ConflictService>();
        sc.AddSingleton<IPatchService, PatchService>();
        sc.AddSingleton<ICherryPickService, CherryPickService>();
        sc.AddSingleton<IRebaseService, RebaseService>();
        sc.AddSingleton<ISubmoduleService, SubmoduleService>();
        sc.AddSingleton<IWorktreeService, WorktreeService>();
        sc.AddSingleton<ISshKeyService, SshKeyService>();
        sc.AddSingleton<ICloneService, CloneService>();
        sc.AddSingleton<ITerminalBackend, PortaPtyBackend>();
        sc.AddSingleton<ITerminalSessionService, TerminalSessionService>();
        sc.AddSingleton<IRecentRepositoryService, RecentRepositoryService>();
        sc.AddSingleton<IRepoIdentityService, RepoIdentityService>();
        sc.AddSingleton<ICredentialVaultService, FakeVault>();
        sc.AddSingleton<IExternalLauncher, ExternalLauncher>();
        return sc.BuildServiceProvider();
    }

    [AvaloniaFact]
    public async Task Linked_tab_is_shorter_with_no_leading_border_while_owner_keeps_full_treatment()
    {
        _sp = CreateServices();
        var workspace = _sp.GetRequiredService<IWorkspaceService>();
        var owner = await workspace.OpenAsync(_ownerDir);
        var sub = new SubmoduleInfo { Name = "vendor/lib", Path = "vendor/lib", Url = _childDir, Status = SubmoduleStatus.Clean };
        var child = await workspace.OpenSubmoduleAsync(owner, sub);

        var band = new AppBandView();
        band.Initialize(_sp);

        var window = new Window { Width = 1280, Height = 40, Content = band };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var shells = band.GetVisualDescendants().OfType<Border>()
            .Where(b => b.Classes.Contains("tabShell") && b.DataContext is RepoTabViewModel)
            .ToList();

        var ownerShell = shells.Single(b => ReferenceEquals(((RepoTabViewModel)b.DataContext!).Context, owner));
        var childShell = shells.Single(b => ReferenceEquals(((RepoTabViewModel)b.DataContext!).Context, child));

        Assert.Equal(RowHeight, ownerShell.Height);
        Assert.Equal(LinkedHeight, childShell.Height);
        Assert.Equal(Avalonia.Layout.VerticalAlignment.Bottom, childShell.VerticalAlignment);
        Assert.Equal(Avalonia.Layout.VerticalAlignment.Stretch, ownerShell.VerticalAlignment);

        // The linked child's own leading separator is suppressed so the group reads as one
        // continuous unit; the owner (first tab) never had one either way.
        Assert.Equal(0, childShell.BorderThickness.Left);
        Assert.Equal(0, ownerShell.BorderThickness.Left);

        window.Close();
        band.Dispose();
    }

    [AvaloniaFact]
    public async Task LinkedTab_Is25pxHigh()
    {
        _sp = CreateServices();
        var workspace = _sp.GetRequiredService<IWorkspaceService>();
        var owner = await workspace.OpenAsync(_ownerDir);
        var sub = new SubmoduleInfo { Name = "vendor/lib", Path = "vendor/lib", Url = _childDir, Status = SubmoduleStatus.Clean };
        var child = await workspace.OpenSubmoduleAsync(owner, sub);

        var band = new AppBandView();
        band.Initialize(_sp);

        var window = new Window { Width = 1280, Height = 40, Content = band };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var shells = band.GetVisualDescendants().OfType<Border>()
            .Where(b => b.Classes.Contains("tabShell") && b.DataContext is RepoTabViewModel)
            .ToList();
        var ownerShell = shells.Single(b => ReferenceEquals(((RepoTabViewModel)b.DataContext!).Context, owner));
        var childShell = shells.Single(b => ReferenceEquals(((RepoTabViewModel)b.DataContext!).Context, child));

        Assert.Equal(RowHeight, ownerShell.Bounds.Height);
        Assert.Equal(LinkedHeight, childShell.Bounds.Height);

        window.Close();
        band.Dispose();
    }

    [AvaloniaFact]
    public async Task LinkedTab_GetsHalfBodyWidth_FixedMode()
    {
        _sp = CreateServices();
        var settings = _sp.GetRequiredService<ISettingsService>();
        var previous = settings.Current;
        await settings.UpdateAsync(s => s with { TabWidthMode = TabWidthMode.Fixed, FixedTabWidth = 200 });
        try
        {
            var workspace = _sp.GetRequiredService<IWorkspaceService>();
            var owner = await workspace.OpenAsync(_ownerDir);
            var sub = new SubmoduleInfo { Name = "vendor/lib", Path = "vendor/lib", Url = _childDir, Status = SubmoduleStatus.Clean };
            var child = await workspace.OpenSubmoduleAsync(owner, sub);

            var band = new AppBandView();
            band.Initialize(_sp);

            var window = new Window { Width = 1280, Height = 40, Content = band };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var bodies = band.GetVisualDescendants().OfType<Border>()
                .Where(b => b.Classes.Contains("tabBody") && b.DataContext is RepoTabViewModel)
                .ToList();
            var ownerBody = bodies.Single(b => ReferenceEquals(((RepoTabViewModel)b.DataContext!).Context, owner));
            var childBody = bodies.Single(b => ReferenceEquals(((RepoTabViewModel)b.DataContext!).Context, child));

            Assert.Equal(200, ownerBody.Bounds.Width);
            Assert.Equal(100, childBody.Bounds.Width);

            window.Close();
            band.Dispose();
        }
        finally
        {
            await settings.UpdateAsync(s => s with { TabWidthMode = previous.TabWidthMode, FixedTabWidth = previous.FixedTabWidth });
        }
    }

    private sealed class FakeThemeProbe : ISystemThemeProbe
    {
        public bool IsLightTheme => false;
        public bool IsHighContrast => false;
#pragma warning disable CS0067
        public event EventHandler? Changed;
#pragma warning restore CS0067
    }

    private sealed class FakeVault : ICredentialVaultService
    {
        public Task StoreTokenAsync(string key, string token, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> RetrieveTokenAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task DeleteTokenAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_sp is not null)
        {
            var workspace = _sp.GetRequiredService<IWorkspaceService>() as WorkspaceService;
            if (workspace is not null)
                await workspace.DisposeAsync();
            _sp.Dispose();
        }
        TestScratch.Delete(_ownerDir);
    }
}
