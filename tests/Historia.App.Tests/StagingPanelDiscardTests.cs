namespace HistoriaApp.Tests;

[Trait("tier", "ui")]
public class StagingPanelDiscardTests : IDisposable
{
    private readonly TestRepoFixture _fixture;
    private readonly ServiceProvider _sp;
    private readonly IWorkspaceService _workspace;
    private readonly StubDialogService _dialogs;
    private readonly List<Window> _windows = [];
    private RepositoryContext _context;

    public StagingPanelDiscardTests()
    {
        _fixture = new TestRepoFixture();
        _dialogs = new StubDialogService();
        _sp = CreateServices(_dialogs);
        _sp.GetRequiredService<AvaloniaThemeApplier>();
        _workspace = _sp.GetRequiredService<IWorkspaceService>();
        _context = _workspace.OpenAsync(_fixture.Path).GetAwaiter().GetResult();
        _context.State.RefreshRefsAsync().GetAwaiter().GetResult();
        _workspace.Focus(_context);
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
    public async Task Staged_row_menu_has_no_discard_item()
    {
        await File.WriteAllTextAsync(System.IO.Path.Combine(_fixture.Path, "staged.txt"), "staged\n");
        await File.WriteAllTextAsync(System.IO.Path.Combine(_fixture.Path, "changes.txt"), "changes\n");
        await _context.Gateway.StageAsync(["staged.txt"]);
        await _context.State.RefreshStatusAsync();

        var panel = new StagingPanelView(_sp, _context);
        var window = new Window { Width = 800, Height = 900, Content = panel };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var stagedRow = Assert.Single(panel.StagedList.GetVisualDescendants().OfType<Border>(), b => b.Name == "StagedRow");
        var stagedMenu = stagedRow.ContextMenu;
        Assert.NotNull(stagedMenu);
        var stagedItems = stagedMenu.Items.OfType<MenuItem>().ToList();
        Assert.DoesNotContain(stagedItems, mi => mi.Header?.ToString() == "Discard changes");
        Assert.Contains(stagedItems, mi => mi.Header?.ToString() == "Open file location");
        Assert.Contains(stagedItems, mi => mi.Header?.ToString() == "Copy path");

        var changesRow = Assert.Single(panel.ChangesList.GetVisualDescendants().OfType<Border>(), b => b.Name == "ChangeRow");
        var changesMenu = changesRow.ContextMenu;
        Assert.NotNull(changesMenu);
        var changesItems = changesMenu.Items.OfType<MenuItem>().ToList();
        Assert.Contains(changesItems, mi => mi.Header?.ToString() == "Discard changes");

        window.Close();
    }

    [AvaloniaFact]
    public async Task Discard_all_sends_old_path_of_a_paired_rename()
    {
        var aPath = System.IO.Path.Combine(_fixture.Path, "a.txt");
        var bPath = System.IO.Path.Combine(_fixture.Path, "b.txt");
        await File.WriteAllTextAsync(aPath, "same content across move\n");
        await _context.Gateway.StageAsync(["a.txt"]);
        await _context.Gateway.CommitAsync(new CommitRequest
        {
            Message = "add a.txt",
            AuthorName = "Dev",
            AuthorEmail = "dev@example.com",
        });

        File.Move(aPath, bPath);
        await _context.State.RefreshStatusAsync();

        var status = _context.State.Status;
        Assert.Single(status.Unstaged);
        Assert.Equal("b.txt", status.Unstaged[0].Path);
        Assert.Equal("a.txt", status.Unstaged[0].OldPath);

        var panel = new StagingPanelView(_sp, _context);
        Dispatcher.UIThread.RunJobs();

        await panel.ViewModel.DiscardAllChangesCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        await _context.State.RefreshStatusAsync();
        Assert.False(_context.State.Status.IsDirty);
        Assert.True(File.Exists(aPath));
        Assert.False(File.Exists(bPath));
        Assert.Equal("same content across move", (await File.ReadAllTextAsync(aPath)).Trim());
    }

    private StagingPanelView ShowPanel(out Window window)
    {
        var panel = new StagingPanelView(_sp, _context);
        window = new Window { Width = 800, Height = 900, Content = panel };
        _windows.Add(window);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return panel;
    }

    private async Task ModifyTwoFilesAsync()
    {
        await File.WriteAllTextAsync(System.IO.Path.Combine(_fixture.Path, "one.txt"), "one\n");
        await File.WriteAllTextAsync(System.IO.Path.Combine(_fixture.Path, "two.txt"), "two\n");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        do
        {
            await _context.State.RefreshStatusAsync();
        }
        while (_context.State.Status.Untracked.Count < 2 && DateTime.UtcNow < deadline);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The stage command returns when git does; the status refresh continues in the
    /// background, so the tab's status is refreshed until it lists the staged files.</summary>
    private async Task RefreshUntilStagedAsync(int count)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        do
        {
            await _context.State.RefreshStatusAsync();
        }
        while (_context.State.Status.Staged.Count < count && DateTime.UtcNow < deadline);
    }

    private static IReadOnlyList<CheckBox> RowCheckBoxes(ListBox list) =>
        [.. list.GetVisualDescendants().OfType<Border>().Where(b => b.Name is "ChangeRow" or "StagedRow")
            .Select(b => b.GetVisualDescendants().OfType<CheckBox>().Single())];

    private static void ClickCenter(Window window, Control control)
    {
        var center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        Avalonia.Headless.HeadlessWindowExtensions.MouseDown(window, center, MouseButton.Left);
        Avalonia.Headless.HeadlessWindowExtensions.MouseUp(window, center, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Press(Control control)
    {
        control.RaiseEvent(new PointerPressedEventArgs(
            control,
            new Pointer(1, PointerType.Mouse, true),
            control,
            new Point(5, 5),
            0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None));
    }

    [AvaloniaFact]
    public async Task The_arrows_are_enabled_only_while_rows_are_checked_and_stage_the_checked_row()
    {
        await ModifyTwoFilesAsync();
        var panel = ShowPanel(out var window);
        Assert.False(panel.StageArrow.IsEffectivelyEnabled);
        Assert.False(panel.UnstageArrow.IsEffectivelyEnabled);

        var boxes = RowCheckBoxes(panel.ChangesList);
        Assert.Equal(2, boxes.Count);
        ClickCenter(window, boxes[0]);

        Assert.True(panel.StageArrow.IsEffectivelyEnabled);
        Assert.False(panel.UnstageArrow.IsEffectivelyEnabled);
        Assert.Null(panel.ChangesSelectAll.IsChecked);

        await panel.ViewModel.StageCheckedCommand.ExecuteAsync(null);
        await RefreshUntilStagedAsync(1);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["one.txt"], _context.State.Status.Staged.Select(f => f.Path));
        Assert.False(panel.StageArrow.IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public async Task Clicking_the_select_all_box_checks_every_row_and_clicking_it_again_clears_them()
    {
        await ModifyTwoFilesAsync();
        var panel = ShowPanel(out var window);

        ClickCenter(window, panel.ChangesSelectAll);

        Assert.True(panel.ChangesSelectAll.IsChecked);
        Assert.All(RowCheckBoxes(panel.ChangesList), box => Assert.True(box.IsChecked));
        Assert.True(panel.StageArrow.IsEffectivelyEnabled);

        ClickCenter(window, panel.ChangesSelectAll);

        Assert.False(panel.ChangesSelectAll.IsChecked);
        Assert.All(RowCheckBoxes(panel.ChangesList), box => Assert.False(box.IsChecked));
        Assert.False(panel.StageArrow.IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public async Task The_row_discard_button_asks_and_restores_the_file()
    {
        await File.WriteAllTextAsync(System.IO.Path.Combine(_fixture.Path, "f.txt"), "edited\n");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        do
        {
            await _context.State.RefreshStatusAsync();
        }
        while (_context.State.Status.Unstaged.Count == 0 && DateTime.UtcNow < deadline);
        var panel = ShowPanel(out _);
        var discard = panel.ChangesList.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "DiscardRowButton");

        await ((IAsyncRelayCommand)discard.Command!).ExecuteAsync(discard.CommandParameter);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("init", (await File.ReadAllTextAsync(System.IO.Path.Combine(_fixture.Path, "f.txt"))).Trim());
    }

    [AvaloniaFact]
    public async Task A_press_on_a_row_opens_its_diff_but_a_press_on_its_checkbox_does_not()
    {
        await ModifyTwoFilesAsync();
        var panel = ShowPanel(out _);
        var rows = panel.ChangesList.GetVisualDescendants().OfType<Border>().Where(b => b.Name == "ChangeRow").ToList();
        var row = rows.Single(b => b.DataContext is FileRowViewModel { Path: "one.txt" });

        Press(row.GetVisualDescendants().OfType<CheckBox>().Single());
        Assert.Null(_context.State.OpenDiff);

        Press(row);

        Assert.Equal("one.txt", _context.State.OpenDiff?.Path);
        Assert.Equal(DiffSource.UnstagedChanges, _context.State.OpenDiff?.Source);
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

    private sealed class TestRepoFixture : IDisposable
    {
        public string Path { get; private set; }

        public TestRepoFixture()
        {
            Path = NewPath();
            RepoTemplates.Initial(Path);
        }

        private static string NewPath() =>
            TestScratch.NewPath("historia-staging-discard-tests-");

        public void Dispose() => RepoTemplates.Delete(Path);
    }
}
