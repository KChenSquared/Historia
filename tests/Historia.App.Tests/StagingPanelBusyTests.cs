namespace HistoriaApp.Tests;

/// <summary>
/// The busy indicator in the staging panel's Changes header: hidden while idle, hidden during the
/// short delay after a stage starts, shown with the write lock holder's name once the delay has
/// passed, and hidden again when the change is done. The delay timer is driven by the test.
/// </summary>
[Trait("tier", "ui")]
public class StagingPanelBusyTests : IDisposable
{
    private readonly TestRepoFixture _fixture;
    private readonly ServiceProvider _sp;
    private readonly BusyDelayDispatcher _dispatcher = new();
    private readonly List<Window> _windows = [];
    private readonly RepositoryContext _context;

    public StagingPanelBusyTests()
    {
        _fixture = new TestRepoFixture();
        _sp = CreateServices(new StubDialogService(), _dispatcher);
        _sp.GetRequiredService<AvaloniaThemeApplier>();
        var workspace = _sp.GetRequiredService<IWorkspaceService>();
        _context = workspace.OpenAsync(_fixture.Path).GetAwaiter().GetResult();
        _context.State.RefreshRefsAsync().GetAwaiter().GetResult();
        workspace.Focus(_context);
    }

    public void Dispose()
    {
        foreach (var window in _windows)
            try { window.Close(); } catch { }
        try { _context.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        try { _sp.Dispose(); } catch { }
        try { _fixture.Dispose(); } catch { }
    }

    [AvaloniaFact]
    public async Task The_indicator_shows_after_the_delay_while_a_stage_waits_and_hides_when_it_ends()
    {
        await File.WriteAllTextAsync(System.IO.Path.Combine(_fixture.Path, "one.txt"), "one\n");
        await File.WriteAllTextAsync(System.IO.Path.Combine(_fixture.Path, "two.txt"), "two\n");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        do
        {
            await _context.State.RefreshStatusAsync();
        }
        while (_context.State.Status.Untracked.Count < 2 && DateTime.UtcNow < deadline);
        var panel = new StagingPanelView(_sp, _context);
        var window = new Window { Width = 800, Height = 900, Content = panel };
        _windows.Add(window);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.False(panel.StagingBusyIndicator.IsEffectivelyVisible);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = _context.WithWriteLockAsync(async ct =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(ct);
        }, label: "Commit");
        await entered.Task;

        var stage = panel.ViewModel.StageAllChangesCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(panel.ViewModel.IsStagingBusy);
        Assert.False(panel.StagingBusyIndicator.IsEffectivelyVisible);

        _dispatcher.FireBusyTimers();
        Dispatcher.UIThread.RunJobs();

        Assert.True(panel.StagingBusyIndicator.IsEffectivelyVisible);
        Assert.Equal("Waiting for Commit…", panel.StagingBusyText.Text);

        release.SetResult();
        await holder;
        await stage;
        Dispatcher.UIThread.RunJobs();

        Assert.False(panel.ViewModel.IsStagingBusy);
        Assert.False(panel.StagingBusyIndicator.IsEffectivelyVisible);
    }

    /// <summary>The real UI dispatcher, except that the staging panel's busy-delay timer ticks
    /// only when the test fires it.</summary>
    private sealed class BusyDelayDispatcher : IUiDispatcher
    {
        private readonly UiDispatcher _inner = new();
        private readonly List<ManualTimer> _timers = [];

        public bool CheckAccess() => _inner.CheckAccess();

        public void Post(Action action) => _inner.Post(action);

        public IUiTimer CreateTimer(TimeSpan interval, Action tick)
        {
            if (interval != StagingPanelViewModel.BusyIndicatorDelay)
                return _inner.CreateTimer(interval, tick);
            var timer = new ManualTimer(tick);
            lock (_timers)
                _timers.Add(timer);
            return timer;
        }

        public void FireBusyTimers()
        {
            ManualTimer[] running;
            lock (_timers)
                running = [.. _timers.Where(t => t.IsRunning)];
            foreach (var timer in running)
                timer.Fire();
        }

        private sealed class ManualTimer(Action tick) : IUiTimer
        {
            public bool IsRunning { get; private set; }
            public void Start() => IsRunning = true;
            public void Stop() => IsRunning = false;
            public void Dispose() => IsRunning = false;
            public void Fire() => tick();
        }
    }

    private static ServiceProvider CreateServices(IDialogService dialogs, IUiDispatcher dispatcher)
    {
        var sc = new ServiceCollection();
        var settings = SettingsService.Load();
        sc.AddSingleton<ISettingsService>(settings);
        sc.AddSingleton<SettingsService>(settings);
        sc.AddSingleton<IUiDispatcher>(dispatcher);
        sc.AddSingleton<IDialogService>(dialogs);
        sc.AddSingleton<IScrollDirectionProbe, ScrollDirectionProbe>();
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
        sc.AddSingleton<AvaloniaThemeApplier>();
        return sc.BuildServiceProvider();
    }

    private sealed class StubDialogService : IDialogService
    {
        public Task<string?> PickFolderAsync(string title, string? initialDirectory = null) => Task.FromResult<string?>(null);
        public Task<string?> PickFileAsync(string title, string filter, string? initialDirectory = null) => Task.FromResult<string?>(null);
        public Task<bool> ConfirmAsync(string title, string message, bool destructive = false, string confirmLabel = "OK", bool defaultToCancel = false, DialogIcon icon = DialogIcon.None) => Task.FromResult(true);
        public Task<bool> ConfirmDestructiveAsync(string title, string effect, string confirmLabel) => Task.FromResult(true);
        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
        public Task<string?> PromptAsync(string title, string message, string? defaultValue = null, string confirmLabel = "OK", bool primaryConfirm = false) => Task.FromResult<string?>(null);
        public Task<int> ChooseAsync(string title, string message, IReadOnlyList<string> options, int defaultIndex = 0, int destructiveIndex = -1, DialogIcon icon = DialogIcon.None) => Task.FromResult(0);
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

    private sealed class TestRepoFixture : IDisposable
    {
        public string Path { get; private set; }

        public TestRepoFixture()
        {
            Path = NewPath();
            RepoTemplates.Initial(Path);
        }

        private static string NewPath() =>
            TestScratch.NewPath("historia-staging-busy-tests-");

        public void Dispose() => RepoTemplates.Delete(Path);
    }
}
