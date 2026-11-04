namespace HistoriaApp.Tests;

/// <summary>
/// The folded-badge ref list on a real repository: it stays open while a context menu opened from
/// one of its rows is open, closes on the next hover-close tick once that menu closes with the
/// pointer elsewhere, and without a menu closes on the tick as before.
/// </summary>
[Trait("tier", "ui")]
public sealed class GraphRefListMenuTests
{
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

    private sealed class ScriptedDialogs : IDialogService
    {
        public Task<bool> ConfirmDestructiveAsync(string title, string effect, string confirmLabel) => Task.FromResult(false);
        public Task<string?> PickFolderAsync(string title, string? initialDirectory = null) => Task.FromResult<string?>(null);
        public Task<string?> PickFileAsync(string title, string filter, string? initialDirectory = null) => Task.FromResult<string?>(null);

        public Task<bool> ConfirmAsync(string title, string message, bool destructive = false,
            string confirmLabel = "OK", bool defaultToCancel = false, DialogIcon icon = DialogIcon.None) => Task.FromResult(false);

        public Task<int> ChooseAsync(string title, string message, IReadOnlyList<string> options,
            int defaultIndex = 0, int destructiveIndex = -1, DialogIcon icon = DialogIcon.None) => Task.FromResult(-1);

        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;

        public Task<string?> PromptAsync(string title, string message, string? defaultValue = null,
            string confirmLabel = "OK", bool primaryConfirm = false) => Task.FromResult<string?>(null);
    }

    /// <summary>Two commits on <c>main</c>; the tip also carries <c>feat</c>, tag <c>v1</c> and
    /// <c>origin/main</c>.</summary>
    private sealed class RepoFixture : IDisposable
    {
        public string Path { get; }

        public RepoFixture()
        {
            Path = TestScratch.NewDirectory("historia-reflistmenu-");
            RunGit("init", "-q", "-b", "main");
            RunGit("config", "user.name", "Test");
            RunGit("config", "user.email", "test@example.com");
            for (var i = 0; i < 2; i++)
            {
                File.WriteAllText(System.IO.Path.Combine(Path, "f.txt"), $"{i}\n");
                RunGit("add", ".");
                RunGit("commit", "-q", "-m", $"c{i}", "--no-gpg-sign");
            }
            RunGit("branch", "feat");
            RunGit("tag", "v1");
            RunGit("remote", "add", "origin", "git@example.com:o/origin.git");
            RunGit("update-ref", "refs/remotes/origin/main", "HEAD");
        }

        private void RunGit(params string[] args) => TestGit.RunChecked(Path, args, testIdentity: false);

        public void Dispose() => TestScratch.Delete(Path);
    }

    private static ServiceProvider CreateServices()
    {
        var sc = new ServiceCollection();
        var settings = SettingsService.Load();
        sc.AddSingleton<ISettingsService>(settings);
        sc.AddSingleton<SettingsService>(settings);
        sc.AddSingleton<IUiDispatcher, Historia.Platform.UiDispatcher>();
        sc.AddSingleton<IDialogService>(new ScriptedDialogs());
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

    private sealed record Setup(
        CommitGraphControl Graph, ServiceProvider Sp, RepoFixture Repo, RepositoryContext Context, Window Window) : IDisposable
    {
        public void Dispose()
        {
            try { Window.Close(); } catch { }
            try { Context.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
            try { Sp.Dispose(); } catch { }
            try { Repo.Dispose(); } catch { }
        }
    }

    private static Setup CreateGraph()
    {
        var repo = new RepoFixture();
        var sp = CreateServices();
        var ws = sp.GetRequiredService<IWorkspaceService>();
        var ctx = ws.OpenAsync(repo.Path).GetAwaiter().GetResult();
        ctx.MarkFirstGraphPaintDone();
        ctx.MarkFirstFullLoadDone();
        ctx.State.RefreshRefsAsync().GetAwaiter().GetResult();

        var graph = new CommitGraphControl();
        graph.Initialize(sp);
        var window = new Window { Width = 1400, Height = 600, Content = graph };
        window.Show();
        ws.Focus(ctx);
        Dispatcher.UIThread.RunJobs();

        for (var i = 0; i < 500 && graph.RowCountForTesting < 2; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Assert.Equal(2, graph.RowCountForTesting);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        RenderFrame(graph);
        return new Setup(graph, sp, repo, ctx, window);
    }

    private static void RenderFrame(CommitGraphControl graph)
    {
        var size = new PixelSize((int)graph.Bounds.Width, (int)graph.Bounds.Height);
        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(size);
        using var dc = bitmap.CreateDrawingContext();
        graph.Render(dc);
    }

    /// <summary>Opens the ref list for the tip's <c>main</c> label, as hovering its badge does.</summary>
    private static void OpenRefList(Setup s)
    {
        var pill = s.Graph.PillRectsForTesting.First(p => p.Deco.Label == "main");
        s.Graph.OpenRefListForTesting(pill.Deco, pill.Rect);
        Dispatcher.UIThread.RunJobs();
        Assert.True(s.Graph.RefListOpenForTesting);
    }

    private static Border FirstRefListRow(Setup s)
    {
        var shell = Assert.IsType<Border>(s.Graph.RefListContentForTesting);
        var stack = Assert.IsType<StackPanel>(shell.Child);
        return stack.Children.OfType<Border>().First();
    }

    private static void RightClickRow(Border row)
    {
        var p = new Avalonia.Input.Pointer(1, Avalonia.Input.PointerType.Mouse, true);
        var pe = new PointerPressedEventArgs(row, p, row, new Point(5, 5), 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.RightButtonPressed), KeyModifiers.None);
        row.RaiseEvent(pe);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Puts the pointer over the graph's empty second row, away from the list and badge.</summary>
    private static void MovePointerAway(Setup s)
    {
        var y = s.Graph.PillRectsForTesting.First(p => p.Deco.Label == "main").Rect.Bottom + s.Graph.RowHeightForTesting;
        s.Window.MouseMove(new Point(s.Graph.Bounds.Width - 40, y));
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void RefList_stays_open_while_row_context_menu_is_open()
    {
        using var s = CreateGraph();
        OpenRefList(s);

        RightClickRow(FirstRefListRow(s));
        Assert.NotNull(s.Graph.OpenMenuForTesting);
        Assert.True(s.Graph.RefListOpenForTesting);

        MovePointerAway(s);
        s.Graph.TickRefListCloseTimerForTesting();

        Assert.True(s.Graph.RefListOpenForTesting);
        Assert.NotNull(s.Graph.OpenMenuForTesting);
    }

    [AvaloniaFact]
    public void RefList_closes_after_menu_closes_when_pointer_left()
    {
        using var s = CreateGraph();
        OpenRefList(s);
        RightClickRow(FirstRefListRow(s));
        var menu = s.Graph.OpenMenuForTesting;
        Assert.NotNull(menu);
        MovePointerAway(s);
        s.Graph.TickRefListCloseTimerForTesting();
        Assert.True(s.Graph.RefListOpenForTesting);

        menu.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.Null(s.Graph.OpenMenuForTesting);
        Assert.True(s.Graph.RefListOpenForTesting);

        s.Graph.TickRefListCloseTimerForTesting();
        Assert.False(s.Graph.RefListOpenForTesting);
    }

    [AvaloniaFact]
    public void RefList_hover_close_unchanged_without_menu()
    {
        using var s = CreateGraph();
        OpenRefList(s);
        MovePointerAway(s);
        Assert.Null(s.Graph.OpenMenuForTesting);

        s.Graph.TickRefListCloseTimerForTesting();

        Assert.False(s.Graph.RefListOpenForTesting);
    }
}
