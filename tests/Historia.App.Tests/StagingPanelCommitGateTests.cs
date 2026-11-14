namespace HistoriaApp.Tests;

[Trait("tier", "ui")]
public class StagingPanelCommitGateTests : IDisposable
{
    private readonly ConflictResolverViewTests.TestRepoFixture _fixture;
    private readonly ServiceProvider _sp;
    private readonly RepositoryContext _context;
    private readonly List<Window> _windows = [];

    public StagingPanelCommitGateTests()
    {
        _fixture = new ConflictResolverViewTests.TestRepoFixture();
        var sc = new ServiceCollection();
        sc.AddSingleton<IConflictService, ConflictService>();
        sc.AddSingleton<IDialogService>(new ConflictResolverViewTests.FakeDialogService());
        sc.AddSingleton<IRepoActionBus, RepoActionBus>();
        sc.AddSingleton<INotificationService, NotificationService>();
        sc.AddSingleton<IWorkspaceService, WorkspaceService>();
        sc.AddSingleton<IRepoLayoutService, RepoLayoutService>();
        sc.AddSingleton<ISettingsService>(new ConflictResolverViewTests.FakeSettingsService());
        sc.AddSingleton<IStashService, StashService>();
        sc.AddSingleton<IRepoNetworkActionRunner, ConflictResolverViewTests.FakeNetworkRunner>();
        sc.AddSingleton<IGitCliService, GitCliService>();
        var locator = new GitExecutableLocator();
        locator.ResolveAsync(null).GetAwaiter().GetResult();
        sc.AddSingleton<IGitExecutableLocator>(locator);
        sc.AddSingleton<IKeyboardCommandRouter, KeyboardCommandRouter>();
        sc.AddSingleton<IExternalLauncher, ExternalLauncher>();
        sc.AddSingleton<IProfileService, ProfileService>();
        sc.AddSingleton<IRecentRepositoryService, RecentRepositoryService>();
        sc.AddSingleton<IRepoIdentityService, RepoIdentityService>();
        sc.AddSingleton<ICredentialVaultService, ConflictResolverViewTests.FakeVault>();
        sc.AddSingleton<IUiDispatcher, Historia.Platform.UiDispatcher>();
        sc.AddSingleton<IBackgroundWorkGate, BackgroundWorkGate>();
        sc.AddSingleton<IRepoStateCache, RepoStateCache>();
        sc.AddSingleton<IGraphLayoutService, GraphLayoutService>();
        sc.AddSingleton<IUnsavedWorkRegistry>(new UnsavedWorkRegistry());
        _sp = sc.BuildServiceProvider();

        var ws = _sp.GetRequiredService<IWorkspaceService>();
        _context = ws.OpenAsync(_fixture.Path).GetAwaiter().GetResult();
        ws.Focus(_context);
    }

    public void Dispose()
    {
        foreach (var window in _windows)
            try { window.Close(); } catch { }
        try { _context.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        try { _sp.Dispose(); } catch { }
        try { _fixture.Dispose(); } catch { }
    }

    /// <summary>Refreshes the tab's status and runs posted UI work until <paramref name="condition"/>
    /// holds, for at most 10 seconds. After a git command on the open repository, a refresh that
    /// joins one its file watcher already started can return a snapshot taken before the command.</summary>
    private async Task RefreshUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            await _context.State.RefreshStatusAsync();
            Dispatcher.UIThread.RunJobs();
            if (condition())
                return;
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {what}.");
            await Task.Delay(50);
        }
    }

    private StagingPanelView ShowPanel()
    {
        var panel = new StagingPanelView(_sp, _context);
        var window = new Window { Width = 800, Height = 900, Content = panel };
        _windows.Add(window);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return panel;
    }

    private static Button CommitButton(StagingPanelView panel) => panel.FindControl<Button>("CommitButton")!;

    private static Button CommitPushButton(StagingPanelView panel) => panel.FindControl<Button>("CommitPushButton")!;

    [AvaloniaFact]
    public async Task Commit_buttons_disabled_with_tooltip_when_conflicted()
    {
        _fixture.CreateConflict();
        await RefreshUntilAsync(() => _context.State.Status.Conflicted.Count > 0, "the conflicted status");

        var panel = ShowPanel();
        Dispatcher.UIThread.RunJobs();

        foreach (var button in new[] { CommitButton(panel), CommitPushButton(panel) })
        {
            Assert.False(button.IsEffectivelyEnabled);
            Assert.Equal("Resolve the conflicts before committing.", ToolTip.GetTip(button));
            Assert.True(ToolTip.GetShowOnDisabled(button));
        }
    }

    [AvaloniaFact]
    public async Task Commit_buttons_reenable_when_conflicts_resolve()
    {
        _fixture.CreateConflict();
        await RefreshUntilAsync(() => _context.State.Status.Conflicted.Count > 0, "the conflicted status");
        var panel = ShowPanel();
        Assert.False(CommitButton(panel).IsEffectivelyEnabled);

        _fixture.MarkResolved();
        // The panel re-evaluates its commit gate after the status lands, so wait for the buttons too.
        await RefreshUntilAsync(() => _context.State.Status.Conflicted.Count == 0
            && CommitButton(panel).IsEffectivelyEnabled && CommitPushButton(panel).IsEffectivelyEnabled,
            "the resolved status to re-enable the commit buttons");

        foreach (var button in new[] { CommitButton(panel), CommitPushButton(panel) })
        {
            Assert.True(button.IsEffectivelyEnabled);
            Assert.Null(ToolTip.GetTip(button));
        }
    }
}
