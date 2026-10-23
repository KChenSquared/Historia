namespace HistoriaApp.Tests;

/// <summary>
/// The graph opens one context menu per commit on a real repository: right-clicking a label and
/// right-clicking its row give the same items, every ref folded into the row's badge gets a
/// submenu, an unlabelled commit gets the commit items only, and deleting a tag from the menu
/// goes through the destructive confirmation.
/// </summary>
[Trait("tier", "ui")]
public sealed class GraphUnifiedMenuTests
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
        public List<string> DestructiveEffects { get; } = [];

        public Task<bool> ConfirmDestructiveAsync(string title, string effect, string confirmLabel)
        {
            DestructiveEffects.Add(effect);
            return Task.FromResult(false);
        }

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

    /// <summary>Three commits on <c>main</c>; the tip also carries <c>feat</c>, tag <c>v1</c> and
    /// <c>origin/main</c>, the middle commit nothing, and the root two tags whose second name is
    /// too long for its badge to fit, so the row draws a <c>+N</c> overflow chip.</summary>
    private sealed class RepoFixture : IDisposable
    {
        public string Path { get; }

        public RepoFixture()
        {
            Path = TestScratch.NewDirectory("historia-unifiedmenu-");
            RunGit("init", "-q", "-b", "main");
            RunGit("config", "user.name", "Test");
            RunGit("config", "user.email", "test@example.com");
            for (var i = 0; i < 3; i++)
            {
                File.WriteAllText(System.IO.Path.Combine(Path, "f.txt"), $"{i}\n");
                RunGit("add", ".");
                RunGit("commit", "-q", "-m", $"c{i}", "--no-gpg-sign");
            }
            RunGit("branch", "feat");
            RunGit("tag", "v1");
            RunGit("remote", "add", "origin", "git@example.com:o/origin.git");
            RunGit("update-ref", "refs/remotes/origin/main", "HEAD");
            RunGit("tag", "t1", "HEAD~2");
            RunGit("tag", LongTag, "HEAD~2");
        }

        public static string LongTag { get; } = "z-" + new string('x', 120);

        public string Git(params string[] args) => TestGit.RunChecked(Path, args, testIdentity: false);

        private void RunGit(params string[] args) => Git(args);

        public void Dispose() => TestScratch.Delete(Path);
    }

    private static ServiceProvider CreateServices(IDialogService dialogs)
    {
        var sc = new ServiceCollection();
        var settings = SettingsService.Load();
        sc.AddSingleton<ISettingsService>(settings);
        sc.AddSingleton<SettingsService>(settings);
        sc.AddSingleton<IUiDispatcher, Historia.Platform.UiDispatcher>();
        sc.AddSingleton(dialogs);
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
        CommitGraphControl Graph, ServiceProvider Sp, RepoFixture Repo, RepositoryContext Context, Window Window,
        ScriptedDialogs Dialogs) : IDisposable
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
        var dialogs = new ScriptedDialogs();
        var sp = CreateServices(dialogs);
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

        for (var i = 0; i < 500 && graph.RowCountForTesting < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Assert.Equal(3, graph.RowCountForTesting);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return new Setup(graph, sp, repo, ctx, window, dialogs);
    }

    private static List<string> Headers(IEnumerable<Control> items) =>
        items.OfType<MenuItem>().Select(m => m.Header?.ToString() ?? "").ToList();

    private static IEnumerable<string> LeafHeaders(IEnumerable<Control> items)
    {
        foreach (var m in items.OfType<MenuItem>())
        {
            if (m.ItemsSource is IEnumerable<Control> children)
                foreach (var h in LeafHeaders(children))
                    yield return h;
            else
                yield return m.Header?.ToString() ?? "";
        }
    }

    private static GraphDecoration MainLabel => new()
    {
        RowIndex = 0,
        Type = DecorationType.LocalBranch,
        Label = "main",
        ColorIndex = 0,
        IsLocal = true,
        RemotesOrNull = ["origin"],
    };

    [AvaloniaFact]
    public void Pill_and_row_on_same_commit_build_the_same_items()
    {
        using var s = CreateGraph();

        var fromRow = LeafHeaders(s.Graph.BuildGraphContextMenuItems(0, null)).ToHashSet();
        var fromPill = LeafHeaders(s.Graph.BuildGraphContextMenuItems(0, MainLabel)).ToHashSet();

        Assert.NotEmpty(fromRow);
        Assert.Equal(fromRow.OrderBy(h => h), fromPill.OrderBy(h => h));
    }

    [AvaloniaFact]
    public void Row_lists_every_folded_ref_as_a_submenu()
    {
        using var s = CreateGraph();

        var headers = Headers(s.Graph.BuildGraphContextMenuItems(0, null));

        Assert.Equal(["main", "feat", "origin/main", "v1 (tag)"], headers.Take(4));
        Assert.DoesNotContain(headers, h => h.StartsWith("Merge ", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void Unlabelled_row_menu_has_commit_items_only()
    {
        using var s = CreateGraph();

        var items = s.Graph.BuildGraphContextMenuItems(1, null);

        Assert.Equal("Checkout this commit (detached)", Headers(items)[0]);
        Assert.Contains(Headers(items), h => h.StartsWith("Merge ", StringComparison.Ordinal) && h.EndsWith("into current branch", StringComparison.Ordinal));
    }

    private static void RenderFrame(CommitGraphControl graph)
    {
        var size = new PixelSize((int)graph.Bounds.Width, (int)graph.Bounds.Height);
        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(size);
        using var dc = bitmap.CreateDrawingContext();
        graph.Render(dc);
    }

    private static Point Center(Rect r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);

    private static List<string> OpenMenuHeaders(Setup s)
    {
        var menu = s.Graph.OpenMenuForTesting;
        Assert.NotNull(menu);
        return Headers(Assert.IsAssignableFrom<IEnumerable<Control>>(menu.ItemsSource));
    }

    private static void RightClick(Setup s, Point at)
    {
        s.Window.MouseDown(at, MouseButton.Right);
        s.Window.MouseUp(at, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
    }

    private static GitObjectId RowId(Setup s, int row) => s.Graph.LayoutForTesting!.Layout.Commits[row].Id;

    [AvaloniaFact]
    public void Right_clicking_a_label_opens_the_menu_with_its_refs_first_and_selects_its_row()
    {
        using var s = CreateGraph();
        RenderFrame(s.Graph);
        s.Context.State.SetSelectedCommit(RowId(s, 1));

        var pill = s.Graph.PillRectsForTesting.Single(p => p.Deco.Label == "main");
        RightClick(s, Center(pill.Rect));

        Assert.Equal(["main", "origin/main", "feat", "v1 (tag)"], OpenMenuHeaders(s).Take(4));
        Assert.Equal(RowId(s, 0), s.Context.State.SelectedCommitId);
    }

    [AvaloniaFact]
    public void Right_clicking_a_row_opens_the_menu_in_row_order()
    {
        using var s = CreateGraph();
        RenderFrame(s.Graph);
        s.Context.State.SetSelectedCommit(RowId(s, 1));

        var pill = s.Graph.PillRectsForTesting.Single(p => p.Deco.Label == "main");
        RightClick(s, new Point(s.Graph.Bounds.Width - 40, Center(pill.Rect).Y));

        Assert.Equal(["main", "feat", "origin/main", "v1 (tag)"], OpenMenuHeaders(s).Take(4));
        Assert.Equal(RowId(s, 0), s.Context.State.SelectedCommitId);
    }

    [AvaloniaFact]
    public void Overflow_chip_opens_the_row_menu_on_right_and_left_click()
    {
        using var s = CreateGraph();
        RenderFrame(s.Graph);
        var chip = Assert.Single(s.Graph.OverflowChipsForTesting);
        Assert.Equal(2, chip.Row);
        var longHeader = BranchMenu.TruncateBranchLabelForMenu(RepoFixture.LongTag) + " (tag)";

        RightClick(s, Center(chip.Rect));
        var fromRight = s.Graph.OpenMenuForTesting;
        Assert.Equal(["t1 (tag)", longHeader], OpenMenuHeaders(s).Take(2));
        Assert.Equal(RowId(s, 2), s.Context.State.SelectedCommitId);

        RenderFrame(s.Graph);
        s.Window.MouseDown(Center(chip.Rect), MouseButton.Left);
        s.Window.MouseUp(Center(chip.Rect), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.NotSame(fromRight, s.Graph.OpenMenuForTesting);
        Assert.Equal(["t1 (tag)", longHeader], OpenMenuHeaders(s).Take(2));
    }

    [AvaloniaFact]
    public void Delete_tag_goes_through_destructive_confirm()
    {
        using var s = CreateGraph();

        var tagMenu = s.Graph.BuildGraphContextMenuItems(0, null).OfType<MenuItem>()
            .Single(m => m.Header?.ToString() == "v1 (tag)");
        var delete = Assert.IsAssignableFrom<IEnumerable<Control>>(tagMenu.ItemsSource).OfType<MenuItem>()
            .Single(m => m.Header?.ToString() == "Delete tag v1");
        delete.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        for (var i = 0; i < 200 && s.Dialogs.DestructiveEffects.Count == 0; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        Assert.Single(s.Dialogs.DestructiveEffects);
        Assert.Contains("v1", s.Dialogs.DestructiveEffects[0]);
        Assert.Equal("v1", s.Repo.Git("tag", "-l", "v1").Trim());
    }
}
