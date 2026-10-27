namespace HistoriaApp.Tests;

[Trait("tier", "ui")]
public sealed class RefNavigatorPowerToolsReloadTests
{
    private sealed class CountingStashService(IStashService inner) : IStashService
    {
        public int ListCount { get; private set; }

        public Task<IReadOnlyList<StashEntry>> ListAsync(RepositoryContext context, CancellationToken ct = default)
        {
            ListCount++;
            return inner.ListAsync(context, ct);
        }

        public Task<bool> SaveAsync(RepositoryContext context, StashSaveOptions options, CancellationToken ct = default)
            => inner.SaveAsync(context, options, ct);

        public Task<bool> StashStagedAsync(RepositoryContext context, string? message, CancellationToken ct = default)
            => inner.StashStagedAsync(context, message, ct);

        public Task ApplyAndDropAsync(RepositoryContext context, GitObjectId commitId, CancellationToken ct = default)
            => inner.ApplyAndDropAsync(context, commitId, ct);

        public Task ApplyAsync(RepositoryContext context, GitObjectId commitId, bool restoreIndex, CancellationToken ct = default)
            => inner.ApplyAsync(context, commitId, restoreIndex, ct);

        public Task DropAsync(RepositoryContext context, GitObjectId commitId, CancellationToken ct = default)
            => inner.DropAsync(context, commitId, ct);

        public Task BranchFromAsync(RepositoryContext context, GitObjectId commitId, string branchName, CancellationToken ct = default)
            => inner.BranchFromAsync(context, commitId, branchName, ct);

        public Task ClearAsync(RepositoryContext context, CancellationToken ct = default)
            => inner.ClearAsync(context, ct);
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
        public string Path { get; }

        public TestRepoFixture()
        {
            Path = TestScratch.NewDirectory("historia-refnav-reload-");
            RunGit("init");
            RunGit("config", "user.name", "Test");
            RunGit("config", "user.email", "test@example.com");

            var file = System.IO.Path.Combine(Path, "f.txt");
            File.WriteAllText(file, "init\n");
            RunGit("add", ".");
            RunGit("commit", "-m", "c1", "--no-gpg-sign");

            RunGit("branch", "b1");
            RunGit("tag", "v1.0.0");
        }

        public void RunGit(params string[] args)
        {
            TestGit.RunChecked(Path, args, testIdentity: false);
        }

        public void Dispose() => TestScratch.Delete(Path);
    }

    private static (ServiceProvider Sp, CountingStashService Counter) CreateServices()
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

        var realStash = new StashService(new GitCliService(locator));
        var counter = new CountingStashService(realStash);
        sc.AddSingleton<IStashService>(counter);

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
        return (sc.BuildServiceProvider(), counter);
    }

    private static (RefNavigatorView View, ServiceProvider Sp, CountingStashService Counter, TestRepoFixture Fixture, RepositoryContext Context, Window Window) CreateView(bool markPaintDone = true)
    {
        var fixture = new TestRepoFixture();
        var (sp, counter) = CreateServices();
        var ws = sp.GetRequiredService<IWorkspaceService>();
        var ctx = ws.OpenAsync(fixture.Path).GetAwaiter().GetResult();
        if (markPaintDone)
            ctx.MarkFirstGraphPaintDone();
        ctx.State.RefreshRefsAsync().GetAwaiter().GetResult();
        ws.Focus(ctx);
        var view = new RefNavigatorView(sp);
        var window = new Window { Width = 300, Height = 800, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (view, sp, counter, fixture, ctx, window);
    }

    private static void Cleanup((RefNavigatorView View, ServiceProvider Sp, CountingStashService Counter, TestRepoFixture Fixture, RepositoryContext Context, Window Window) setup)
    {
        try { setup.Window.Close(); } catch { }
        try { setup.View.Dispose(); } catch { }
        try { setup.Context.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        try { setup.Sp.Dispose(); } catch { }
        try { setup.Fixture.Dispose(); } catch { }
    }

    private static List<Border> GetSectionHeaders(RefNavigatorView view) =>
        view.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("section-header")).ToList();

    private static void ExpandSection(Border header)
    {
        var p = new Pointer(1, PointerType.Mouse, true);
        var pe = new PointerPressedEventArgs(header, p, header, new Point(5, 5), 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None);
        header.RaiseEvent(pe);
        Dispatcher.UIThread.RunJobs();
    }

    private static ListBox GetSectionBody(RefNavigatorView view, int sectionIndex) =>
        GetSectionHeaders(view)[sectionIndex].GetVisualAncestors().OfType<StackPanel>().First()
            .Children.OfType<ListBox>().Single();

    private static List<string> GetRowTexts(ListBox body)
    {
        var texts = new List<string>();
        foreach (var item in body.GetVisualDescendants().OfType<ListBoxItem>().OrderBy(i => i.Bounds.Y))
        {
            var tb = item.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Text != "⋯" && t.Text != "▾" && t.Text != "▸");
            if (tb?.Text is not null)
                texts.Add(tb.Text);
        }
        return texts;
    }

    private static async Task PumpUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
        Dispatcher.UIThread.RunJobs();
        Assert.True(condition(), $"Timed out waiting for {what}.");
    }

    private static async Task PumpBrieflyAsync()
    {
        for (var i = 0; i < 10; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
    }

    [AvaloniaFact]
    public async Task First_load_waits_for_the_graph_first_paint()
    {
        var setup = CreateView(markPaintDone: false);
        try
        {
            for (var i = 0; i < 5; i++)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(20);
            }
            Assert.Equal(0, setup.Counter.ListCount);

            setup.Context.MarkFirstGraphPaintDone();

            await PumpUntilAsync(() => setup.Counter.ListCount > 0, "the stash list");
            Assert.Equal(1, setup.Counter.ListCount);
        }
        finally
        {
            Cleanup(setup);
        }
    }

    [AvaloniaFact]
    public async Task Tag_only_refresh_rebuilds_without_relisting_stashes()
    {
        var setup = CreateView(markPaintDone: true);
        try
        {
            await PumpUntilAsync(() => setup.Counter.ListCount > 0, "the stash list");
            Assert.Equal(1, setup.Counter.ListCount);

            var headers = GetSectionHeaders(setup.View);
            ExpandSection(headers[2]);

            setup.Fixture.RunGit("tag", "v9");
            await setup.Context.State.RefreshRefsAsync();

            await PumpUntilAsync(() => GetRowTexts(GetSectionBody(setup.View, 2)).Any(t => t.Contains("v9")), "the new tag row");
            await PumpBrieflyAsync();

            Assert.Equal(1, setup.Counter.ListCount);
        }
        finally
        {
            Cleanup(setup);
        }
    }

    [AvaloniaFact]
    public async Task Branch_refresh_does_not_relist_stashes()
    {
        var setup = CreateView(markPaintDone: true);
        try
        {
            await PumpUntilAsync(() => setup.Counter.ListCount > 0, "the stash list");
            Assert.Equal(1, setup.Counter.ListCount);

            setup.Fixture.RunGit("branch", "new-b");
            setup.Fixture.RunGit("tag", "v9");
            await setup.Context.State.RefreshRefsAsync();

            await PumpUntilAsync(() => GetRowTexts(GetSectionBody(setup.View, 0)).Any(t => t.Contains("new-b")), "the new branch row");
            await PumpBrieflyAsync();

            Assert.Equal(1, setup.Counter.ListCount);
        }
        finally
        {
            Cleanup(setup);
        }
    }

    [AvaloniaFact]
    public async Task External_stash_push_relists_stashes_once()
    {
        var setup = CreateView(markPaintDone: true);
        try
        {
            await PumpUntilAsync(() => setup.Counter.ListCount > 0, "the stash list");
            Assert.Equal(1, setup.Counter.ListCount);

            var headers = GetSectionHeaders(setup.View);
            ExpandSection(headers[3]);

            File.WriteAllText(System.IO.Path.Combine(setup.Fixture.Path, "f.txt"), "dirty\n");
            setup.Fixture.RunGit("stash", "push", "-m", "ext-entry");
            await setup.Context.State.RefreshRefsAsync();

            await PumpUntilAsync(
                () => setup.Counter.ListCount == 2 && GetRowTexts(GetSectionBody(setup.View, 3)).Any(t => t.Contains("ext-entry")),
                "the relisted stashes");

            Assert.Equal(2, setup.Counter.ListCount);
            var stashTexts = GetRowTexts(GetSectionBody(setup.View, 3));
            Assert.Contains(stashTexts, t => t.Contains("ext-entry"));
        }
        finally
        {
            Cleanup(setup);
        }
    }

    [AvaloniaFact]
    public async Task Rebind_relists_stashes()
    {
        var setup = CreateView(markPaintDone: true);
        using var fixture2 = new TestRepoFixture();
        try
        {
            await PumpUntilAsync(() => setup.Counter.ListCount > 0, "the stash list");
            Assert.Equal(1, setup.Counter.ListCount);

            var ws = setup.Sp.GetRequiredService<IWorkspaceService>();
            var ctx2 = await ws.OpenAsync(fixture2.Path);
            ctx2.MarkFirstGraphPaintDone();
            ws.Focus(ctx2);

            // The counter sees every repo's lists: let the second repo's bind list land first.
            await PumpUntilAsync(() => setup.Counter.ListCount > 1, "the stash list");
            Assert.Equal(2, setup.Counter.ListCount);

            ws.Focus(setup.Context);

            await PumpUntilAsync(() => setup.Counter.ListCount > 2, "the stash list");

            Assert.Equal(3, setup.Counter.ListCount);
            await ctx2.DisposeAsync();
        }
        finally
        {
            Cleanup(setup);
        }
    }

    /// <summary>A raise from the previously bound repo that lands after a rebind (it was posted
    /// before the unsubscribe ran) is ignored: nothing is listed and the new repo's baselines
    /// stay its own.</summary>
    [AvaloniaFact]
    public async Task Raise_from_the_previous_repo_after_a_rebind_is_ignored()
    {
        var setup = CreateView(markPaintDone: true);
        using var fixture2 = new TestRepoFixture();
        try
        {
            await PumpUntilAsync(() => setup.Counter.ListCount > 0, "the stash list");
            Assert.Equal(1, setup.Counter.ListCount);

            var ws = setup.Sp.GetRequiredService<IWorkspaceService>();
            var ctx2 = await ws.OpenAsync(fixture2.Path);
            ctx2.MarkFirstGraphPaintDone();
            ws.Focus(ctx2);
            await PumpUntilAsync(() => setup.Counter.ListCount > 1, "the stash list");
            Assert.Equal(2, setup.Counter.ListCount);

            var stale = new RefsChangedEventArgs([], [], []) { StashStamp = ctx2.State.StashStamp + 1 };
            setup.View.ViewModel.OnRefsChanged(setup.Context.State, stale);
            await PumpBrieflyAsync();

            Assert.Equal(2, setup.Counter.ListCount);
            Assert.Equal(ctx2.State.StashStamp, setup.View.ViewModel.LoadedStashStamp);
            await ctx2.DisposeAsync();
        }
        finally
        {
            Cleanup(setup);
        }
    }

    [AvaloniaFact]
    public async Task Stash_change_during_the_paint_wait_lists_the_latest()
    {
        var setup = CreateView(markPaintDone: false);
        try
        {
            for (var i = 0; i < 5; i++)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(20);
            }
            Assert.Equal(0, setup.Counter.ListCount);

            var headers = GetSectionHeaders(setup.View);
            ExpandSection(headers[3]);

            File.WriteAllText(System.IO.Path.Combine(setup.Fixture.Path, "f.txt"), "paint-wait\n");
            setup.Fixture.RunGit("stash", "push", "-m", "mid-paint-stash");
            await setup.Context.State.RefreshRefsAsync();

            setup.Context.MarkFirstGraphPaintDone();

            await PumpUntilAsync(
                () => GetRowTexts(GetSectionBody(setup.View, 3)).Any(t => t.Contains("mid-paint-stash")),
                "the mid-paint stash");

            var finalStashTexts = GetRowTexts(GetSectionBody(setup.View, 3));
            Assert.Contains(finalStashTexts, t => t.Contains("mid-paint-stash"));
        }
        finally
        {
            Cleanup(setup);
        }
    }
}
