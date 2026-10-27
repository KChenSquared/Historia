namespace HistoriaApp.Tests;

[Trait("tier", "ui")]
public class StagingPanelCaseCollisionTests : IDisposable
{
    private readonly ServiceProvider _sp;
    private readonly IWorkspaceService _workspace;
    private readonly IGitCliService _cli;
    private readonly StubDialogService _dialogs;
    private readonly List<string> _scratchDirs = [];
    private readonly List<Window> _windows = [];

    public StagingPanelCaseCollisionTests()
    {
        _dialogs = new StubDialogService();
        _sp = CreateServices(_dialogs);
        _sp.GetRequiredService<AvaloniaThemeApplier>();
        _workspace = _sp.GetRequiredService<IWorkspaceService>();
        _cli = _sp.GetRequiredService<IGitCliService>();
    }

    public void Dispose()
    {
        foreach (var window in _windows)
            try { window.Close(); } catch { }
        try { _sp.Dispose(); } catch { }
        foreach (var dir in _scratchDirs)
        {
            try { RepoTemplates.Delete(dir); } catch { }
        }
    }

    private StagingPanelView ShowPanel(RepositoryContext context)
    {
        var panel = new StagingPanelView(_sp, context);
        var window = new Window { Width = 800, Height = 900, Content = panel };
        _windows.Add(window);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return panel;
    }

    private static TextBlock Footnote(StagingPanelView panel) =>
        panel.ChangesList.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("footnote"));

    private static bool HasFootnote(StagingPanelView panel) =>
        panel.ChangesList.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Classes.Contains("footnote"));

    private async Task<(string RepoPath, string LosingPath, string WinningPath)?> CreateCollisionFixtureAsync()
    {
        var repoPath = TestScratch.NewPath("historia-staging-collision-");
        _scratchDirs.Add(repoPath);
        Directory.CreateDirectory(repoPath);

        if (!TestScratch.IsCaseInsensitive(repoPath))
            return null;

        await _cli.RunAsync(new GitCliRequest { Arguments = ["init", "-b", "main"], WorkingDirectory = repoPath });
        await _cli.RunAsync(new GitCliRequest { Arguments = ["config", "user.name", "Test User"], WorkingDirectory = repoPath });
        await _cli.RunAsync(new GitCliRequest { Arguments = ["config", "user.email", "test@example.com"], WorkingDirectory = repoPath });
        await _cli.RunAsync(new GitCliRequest { Arguments = ["config", "commit.gpgsign", "false"], WorkingDirectory = repoPath });

        var hash1 = await _cli.RunAsync(new GitCliRequest
        {
            Arguments = ["hash-object", "-w", "--stdin"],
            WorkingDirectory = repoPath,
            StandardInput = "blob Foo\n",
        });
        var oid1 = hash1.StandardOutput.Trim();

        var hash2 = await _cli.RunAsync(new GitCliRequest
        {
            Arguments = ["hash-object", "-w", "--stdin"],
            WorkingDirectory = repoPath,
            StandardInput = "blob foo\n",
        });
        var oid2 = hash2.StandardOutput.Trim();

        await _cli.RunAsync(new GitCliRequest
        {
            Arguments = ["update-index", "--add", "--cacheinfo", $"100644,{oid1},Foo.txt"],
            WorkingDirectory = repoPath,
        });

        await _cli.RunAsync(new GitCliRequest
        {
            Arguments = ["update-index", "--add", "--cacheinfo", $"100644,{oid2},foo.txt"],
            WorkingDirectory = repoPath,
        });

        await _cli.RunAsync(new GitCliRequest
        {
            Arguments = ["commit", "-m", "init collision"],
            WorkingDirectory = repoPath,
        });

        await _cli.RunAsync(new GitCliRequest
        {
            Arguments = ["checkout-index", "-f", "-a"],
            WorkingDirectory = repoPath,
        });

        await _cli.RunAsync(new GitCliRequest
        {
            Arguments = ["config", "core.ignorecase", "true"],
            WorkingDirectory = repoPath,
        });

        var content = (await File.ReadAllTextAsync(System.IO.Path.Combine(repoPath, "Foo.txt"))).Trim();
        string winningPath = content == "blob Foo" ? "Foo.txt" : "foo.txt";
        string losingPath = content == "blob Foo" ? "foo.txt" : "Foo.txt";

        return (repoPath, losingPath, winningPath);
    }

    [AvaloniaFact]
    public async Task Collision_footnote_shows_and_changes_header_reads_zero()
    {
        var fixture = await CreateCollisionFixtureAsync();
        if (fixture is null)
            return;

        var context = await _workspace.OpenAsync(fixture.Value.RepoPath);
        _workspace.Focus(context);
        await context.State.RefreshStatusAsync();

        var panel = ShowPanel(context);

        Assert.Equal("Changes (0)", panel.ChangesHeaderText.Text);
        var footnote = Footnote(panel);
        Assert.Equal("1 case-collision file hidden", footnote.Text);
        var tip = ToolTip.GetTip(footnote)?.ToString();
        Assert.NotNull(tip);
        Assert.Contains("These tracked files share one file on disk", tip);
        Assert.Contains(fixture.Value.LosingPath, tip);

        await context.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task With_no_collisions_the_footnote_is_hidden()
    {
        var repoPath = TestScratch.NewPath("historia-staging-no-collision-");
        _scratchDirs.Add(repoPath);
        RepoTemplates.Initial(repoPath);

        var context = await _workspace.OpenAsync(repoPath);
        _workspace.Focus(context);
        await context.State.RefreshStatusAsync();

        var panel = ShowPanel(context);

        Assert.False(HasFootnote(panel));

        await context.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task Stage_all_and_discard_all_receive_no_hidden_path()
    {
        var fixture = await CreateCollisionFixtureAsync();
        if (fixture is null)
            return;

        var context = await _workspace.OpenAsync(fixture.Value.RepoPath);
        _workspace.Focus(context);
        await context.State.RefreshStatusAsync();

        var panel = ShowPanel(context);

        var lsFilesBefore = await _cli.RunAsync(new GitCliRequest
        {
            Arguments = ["ls-files", "-s"],
            WorkingDirectory = fixture.Value.RepoPath,
        });

        await panel.ViewModel.StageAllChangesCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        var lsFilesAfterStage = await _cli.RunAsync(new GitCliRequest
        {
            Arguments = ["ls-files", "-s"],
            WorkingDirectory = fixture.Value.RepoPath,
        });
        Assert.Equal(lsFilesBefore.StandardOutput, lsFilesAfterStage.StandardOutput);

        await panel.ViewModel.DiscardAllChangesCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        await context.State.RefreshStatusAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.False(context.State.Status.IsDirty);
        Assert.Equal("Changes (0)", panel.ChangesHeaderText.Text);
        Assert.True(HasFootnote(panel));

        await context.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task Stage_all_leaves_out_ambiguous_rows_and_says_so()
    {
        var fixture = await CreateCollisionFixtureAsync();
        if (fixture is null)
            return;

        await File.AppendAllTextAsync(System.IO.Path.Combine(fixture.Value.RepoPath, fixture.Value.WinningPath), "real edit\n");
        await File.WriteAllTextAsync(System.IO.Path.Combine(fixture.Value.RepoPath, "new.txt"), "new\n");

        var context = await _workspace.OpenAsync(fixture.Value.RepoPath);
        _workspace.Focus(context);
        await context.State.RefreshStatusAsync();

        var panel = ShowPanel(context);

        Assert.Equal(2, context.State.Status.AmbiguousCaseCollisions.Count);
        Assert.Equal("2 case-collision rows left out of Stage all", Footnote(panel).Text);

        var lsFilesBefore = await _cli.RunAsync(new GitCliRequest
        {
            Arguments = ["ls-files", "-s", "--", "Foo.txt", "foo.txt"],
            WorkingDirectory = fixture.Value.RepoPath,
        });

        await panel.ViewModel.StageAllChangesCommand.ExecuteAsync(null);

        // The command returns when git does; the status refresh continues in the background.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        do
        {
            await context.State.RefreshStatusAsync();
        }
        while (context.State.Status.Staged.Count == 0 && DateTime.UtcNow < deadline);
        Dispatcher.UIThread.RunJobs();

        var lsFilesAfter = await _cli.RunAsync(new GitCliRequest
        {
            Arguments = ["ls-files", "-s", "--", "Foo.txt", "foo.txt"],
            WorkingDirectory = fixture.Value.RepoPath,
        });
        Assert.Equal(lsFilesBefore.StandardOutput, lsFilesAfter.StandardOutput);
        Assert.Equal("new.txt", Assert.Single(context.State.Status.Staged).Path);

        await context.DisposeAsync();
    }

    private static ServiceProvider CreateServices(IDialogService dialogs)
    {
        var sc = new ServiceCollection();
        var settings = SettingsService.Load();
        sc.AddSingleton<ISettingsService>(settings);
        sc.AddSingleton<SettingsService>(settings);
        sc.AddSingleton<IUiDispatcher, UiDispatcher>();
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
}
