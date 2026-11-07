namespace HistoriaApp.Tests;

/// <summary>
/// The Changes and Staged row menus: stage or unstage one file, the same for every checked file,
/// and add one or the checked files to the root <c>.gitignore</c>. "Selected" is the row checkbox.
/// </summary>
[Trait("tier", "ui")]
public class StagingPanelRowMenuTests : IDisposable
{
    private readonly TestRepoFixture _fixture;
    private readonly ServiceProvider _sp;
    private readonly List<Window> _windows = [];
    private readonly RepositoryContext _context;

    public StagingPanelRowMenuTests()
    {
        _fixture = new TestRepoFixture();
        _sp = CreateServices();
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

    private string FilePath(string name) => System.IO.Path.Combine(_fixture.Path, name);

    private StagingPanelView ShowPanel(out Window window)
    {
        var panel = new StagingPanelView(_sp, _context);
        window = new Window { Width = 800, Height = 900, Content = panel };
        _windows.Add(window);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return panel;
    }

    private async Task WriteUntrackedAsync(params string[] names)
    {
        foreach (var name in names)
            await File.WriteAllTextAsync(FilePath(name), name + "\n");
        await WaitUntilAsync(() => names.All(n => _context.State.Status.Untracked.Any(f => f.Path == n)));
    }

    /// <summary>Refreshes the tab's status until <paramref name="condition"/> holds, since a menu
    /// command returns when git does and the status refresh continues in the background.</summary>
    private async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        do
        {
            await _context.State.RefreshStatusAsync();
            Dispatcher.UIThread.RunJobs();
        }
        while (!condition() && DateTime.UtcNow < deadline);
        Assert.True(condition(), "The status did not reach the expected state.");
    }

    private static FileRowViewModel Row(StagingPanelView panel, string path, bool staged = false)
        => (staged ? panel.ViewModel.Staged : panel.ViewModel.Changes).OfType<FileRowViewModel>().Single(r => r.Path == path);

    private static string[] Paths(IEnumerable<FileStatusEntry> entries) => [.. entries.Select(f => f.Path).Order(StringComparer.Ordinal)];

    private async Task StageDirectAsync(params string[] paths)
    {
        await _context.Gateway.StageAsync(paths);
        await WaitUntilAsync(() => _context.State.Status.Staged.Count == paths.Length);
    }

    [AvaloniaFact]
    public async Task Stage_file_stages_only_that_row()
    {
        await WriteUntrackedAsync("one.txt", "two.txt");
        var panel = ShowPanel(out _);
        Row(panel, "one.txt").IsChecked = true;
        Row(panel, "two.txt").IsChecked = true;
        var one = Row(panel, "one.txt");

        await one.StageCommand.ExecuteAsync(null);
        // The clicked row leaves the checked set as soon as the command runs, before the refresh.
        Assert.False(one.IsChecked);
        Assert.Null(panel.ViewModel.ChangesCheckState);
        await WaitUntilAsync(() => _context.State.Status.Staged.Count == 1);

        Assert.Equal(["one.txt"], Paths(_context.State.Status.Staged));
        Assert.Contains(_context.State.Status.Untracked, f => f.Path == "two.txt");
        Assert.True(Row(panel, "two.txt").IsChecked);
    }

    [AvaloniaFact]
    public async Task Stage_file_on_rename_moves_both_halves()
    {
        await File.WriteAllTextAsync(FilePath("a.txt"), "same content across move\n");
        await _context.Gateway.StageAsync(["a.txt"]);
        await _context.Gateway.CommitAsync(new CommitRequest { Message = "add a.txt", AuthorName = "Dev", AuthorEmail = "dev@example.com" });
        File.Move(FilePath("a.txt"), FilePath("b.txt"));
        await WaitUntilAsync(() => _context.State.Status.Unstaged.Any(f => f.Path == "b.txt" && f.OldPath == "a.txt"));
        var panel = ShowPanel(out _);

        await Row(panel, "b.txt").StageCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => _context.State.Status.Staged.Count == 1);

        var staged = Assert.Single(_context.State.Status.Staged);
        Assert.Equal("b.txt", staged.Path);
        Assert.Equal("a.txt", staged.OldPath);
        Assert.Empty(_context.State.Status.Unstaged);
    }

    [AvaloniaFact]
    public async Task Unstage_file_unstages_only_that_row()
    {
        await WriteUntrackedAsync("one.txt", "two.txt");
        await StageDirectAsync("one.txt", "two.txt");
        var panel = ShowPanel(out _);

        await Row(panel, "one.txt", staged: true).UnstageCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => _context.State.Status.Staged.Count == 1);

        Assert.Equal(["two.txt"], Paths(_context.State.Status.Staged));
        Assert.Contains(_context.State.Status.Untracked, f => f.Path == "one.txt");
    }

    [AvaloniaFact]
    public async Task Selected_items_shown_only_when_row_checked()
    {
        await WriteUntrackedAsync("one.txt", "two.txt");
        var panel = ShowPanel(out _);
        var one = Row(panel, "one.txt");
        var changeRow = panel.ChangesList.GetVisualDescendants().OfType<Border>()
            .Single(b => b.Name == "ChangeRow" && b.DataContext == one);
        var menu = changeRow.ContextMenu!;
        menu.Open(changeRow);
        Dispatcher.UIThread.RunJobs();

        MenuItem Item(string header) => menu.Items.OfType<MenuItem>().Single(mi => mi.Header?.ToString() == header);
        Assert.Equal(
            ["Open file location", "Copy path", "Stage file", "Stage selected file(s)", "Ignore file", "Ignore selected file(s)", "Discard changes"],
            menu.Items.OfType<MenuItem>().Select(mi => mi.Header?.ToString()));
        Assert.False(one.ShowSelectedItems);
        Assert.False(Item("Stage selected file(s)").IsVisible);
        Assert.False(Item("Ignore selected file(s)").IsVisible);
        Assert.True(Item("Stage file").IsVisible);
        Assert.True(Item("Ignore file").IsVisible);

        one.IsChecked = true;
        Dispatcher.UIThread.RunJobs();

        Assert.True(one.ShowSelectedItems);
        Assert.True(Item("Stage selected file(s)").IsVisible);
        Assert.True(Item("Ignore selected file(s)").IsVisible);
        Assert.False(Row(panel, "two.txt").ShowSelectedItems);
        menu.Close();
    }

    [AvaloniaFact]
    public async Task Staged_menu_offers_unstage_and_ignore()
    {
        await WriteUntrackedAsync("one.txt");
        await StageDirectAsync("one.txt");
        var panel = ShowPanel(out _);
        var stagedRow = panel.StagedList.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "StagedRow");

        Assert.Equal(
            ["Open file location", "Copy path", "Unstage file", "Unstage selected file(s)", "Ignore file", "Ignore selected file(s)"],
            stagedRow.ContextMenu!.Items.OfType<MenuItem>().Select(mi => mi.Header?.ToString()));
    }

    [AvaloniaFact]
    public async Task Stage_selected_stages_all_checked()
    {
        await WriteUntrackedAsync("one.txt", "two.txt", "three.txt");
        var panel = ShowPanel(out _);
        Row(panel, "one.txt").IsChecked = true;
        Row(panel, "two.txt").IsChecked = true;

        await Row(panel, "one.txt").MoveSelectedCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => _context.State.Status.Staged.Count == 2);

        Assert.Equal(["one.txt", "two.txt"], Paths(_context.State.Status.Staged));
        Assert.Contains(_context.State.Status.Untracked, f => f.Path == "three.txt");
    }

    [AvaloniaFact]
    public async Task Unstage_selected_unstages_all_checked()
    {
        await WriteUntrackedAsync("one.txt", "two.txt", "three.txt");
        await StageDirectAsync("one.txt", "two.txt", "three.txt");
        var panel = ShowPanel(out _);
        Row(panel, "one.txt", staged: true).IsChecked = true;
        Row(panel, "three.txt", staged: true).IsChecked = true;

        await Row(panel, "three.txt", staged: true).MoveSelectedCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => _context.State.Status.Staged.Count == 1);

        Assert.Equal(["two.txt"], Paths(_context.State.Status.Staged));
    }

    [AvaloniaFact]
    public async Task Ignore_file_appends_entry_and_row_disappears()
    {
        await WriteUntrackedAsync("one.txt", "two.txt");
        var panel = ShowPanel(out _);

        await Row(panel, "one.txt").IgnoreCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => _context.State.Status.Untracked.All(f => f.Path != "one.txt"));

        Assert.Equal("/one.txt\n", await File.ReadAllTextAsync(FilePath(".gitignore")));
        Assert.DoesNotContain(panel.ViewModel.Changes.OfType<FileRowViewModel>(), r => r.Path == "one.txt");
        Assert.Contains(panel.ViewModel.Changes.OfType<FileRowViewModel>(), r => r.Path == "two.txt");
        var toasts = _sp.GetRequiredService<INotificationService>().Current;
        Assert.Contains(toasts, n => n.Severity == NotificationSeverity.Success && n.Message.StartsWith("Added 1 entry to .gitignore", StringComparison.Ordinal));
        Assert.DoesNotContain(toasts, n => n.Message.Contains("tracking", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task Ignore_selected_appends_all_checked()
    {
        await WriteUntrackedAsync("one.txt", "two.txt", "three.txt");
        var panel = ShowPanel(out _);
        Row(panel, "one.txt").IsChecked = true;
        Row(panel, "three.txt").IsChecked = true;

        await Row(panel, "one.txt").IgnoreSelectedCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => _context.State.Status.Untracked.All(f => f.Path is not ("one.txt" or "three.txt")));

        Assert.Equal("/one.txt\n/three.txt\n", await File.ReadAllTextAsync(FilePath(".gitignore")));
        Assert.Contains(_context.State.Status.Untracked, f => f.Path == "two.txt");
    }

    [AvaloniaFact]
    public async Task Ignore_tracked_file_publishes_info_note()
    {
        await File.WriteAllTextAsync(FilePath("f.txt"), "edited\n");
        await WaitUntilAsync(() => _context.State.Status.Unstaged.Any(f => f.Path == "f.txt"));
        var panel = ShowPanel(out _);

        await Row(panel, "f.txt").IgnoreCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => File.Exists(FilePath(".gitignore")));

        Assert.Equal("/f.txt\n", await File.ReadAllTextAsync(FilePath(".gitignore")));
        var toasts = _sp.GetRequiredService<INotificationService>().Current;
        Assert.Contains(toasts, n => n.Severity == NotificationSeverity.Info && n.Message.Contains("keeps tracking", StringComparison.Ordinal));
        Assert.Contains(_context.State.Status.Unstaged, f => f.Path == "f.txt");
    }

    [AvaloniaFact]
    public async Task Ignore_staged_deletion_publishes_no_tracking_note()
    {
        await File.WriteAllTextAsync(FilePath("gone.txt"), "gone\n");
        await _context.Gateway.StageAsync(["gone.txt"]);
        await _context.Gateway.CommitAsync(new CommitRequest { Message = "add gone.txt", AuthorName = "Dev", AuthorEmail = "dev@example.com" });
        File.Delete(FilePath("gone.txt"));
        await WaitUntilAsync(() => _context.State.Status.Unstaged.Any(f => f.Path == "gone.txt"));
        await StageDirectAsync("gone.txt");
        var panel = ShowPanel(out _);

        await Row(panel, "gone.txt", staged: true).IgnoreCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => File.Exists(FilePath(".gitignore")));

        Assert.Equal("/gone.txt\n", await File.ReadAllTextAsync(FilePath(".gitignore")));
        var toasts = _sp.GetRequiredService<INotificationService>().Current;
        Assert.Contains(toasts, n => n.Severity == NotificationSeverity.Success && n.Message.StartsWith("Added 1 entry", StringComparison.Ordinal));
        Assert.DoesNotContain(toasts, n => n.Message.Contains("keeps tracking", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task Menu_items_disabled_while_busy()
    {
        await WriteUntrackedAsync("zero.txt");
        await StageDirectAsync("zero.txt");
        await WriteUntrackedAsync("one.txt", "two.txt");
        var panel = ShowPanel(out _);
        var changes = Row(panel, "one.txt");
        var stagedZero = Row(panel, "zero.txt", staged: true);
        Assert.True(changes.StageCommand.CanExecute(null));
        Assert.True(stagedZero.UnstageCommand.CanExecute(null));

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = _context.WithWriteLockAsync(async ct =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(ct);
        }, label: "Commit");
        await entered.Task;

        var stageAll = panel.ViewModel.StageAllChangesCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(panel.ViewModel.IsStagingBusy);
        Assert.False(changes.StageCommand.CanExecute(null));
        Assert.False(changes.MoveSelectedCommand.CanExecute(null));
        Assert.False(changes.IgnoreCommand.CanExecute(null));
        Assert.False(changes.IgnoreSelectedCommand.CanExecute(null));
        Assert.False(stagedZero.UnstageCommand.CanExecute(null));
        Assert.False(stagedZero.MoveSelectedCommand.CanExecute(null));
        Assert.False(stagedZero.IgnoreCommand.CanExecute(null));
        Assert.False(stagedZero.IgnoreSelectedCommand.CanExecute(null));

        release.SetResult();
        await holder;
        await stageAll;
        Dispatcher.UIThread.RunJobs();

        Assert.False(panel.ViewModel.IsStagingBusy);
        // The panel rebuilds its rows after the status lands, so wait for the row itself.
        await WaitUntilAsync(() => _context.State.Status.Staged.Count == 3
            && panel.ViewModel.Staged.OfType<FileRowViewModel>().Any(r => r.Path == "one.txt"));
        var staged = Row(panel, "one.txt", staged: true);
        Assert.True(staged.UnstageCommand.CanExecute(null));
        Assert.True(staged.IgnoreCommand.CanExecute(null));
    }

    private static ServiceProvider CreateServices()
    {
        var sc = new ServiceCollection();
        var settings = SettingsService.Load();
        sc.AddSingleton<ISettingsService>(settings);
        sc.AddSingleton<SettingsService>(settings);
        sc.AddSingleton<IUiDispatcher, UiDispatcher>();
        sc.AddSingleton<IDialogService>(new StubDialogService());
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
            Path = TestScratch.NewPath("historia-staging-rowmenu-tests-");
            RepoTemplates.Initial(Path);
        }

        public void Dispose() => RepoTemplates.Delete(Path);
    }
}
