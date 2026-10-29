namespace HistoriaApp.Tests;

/// <summary>
/// Head row in RefNavigatorView: detached HEAD shows a row under Local Branches with short SHA,
/// attached HEAD has no row, moving between detached commits updates the row, clicking selects
/// the commit, and remote HEAD symrefs never show a row under Remotes.
/// </summary>
[Trait("tier", "ui")]
public sealed class RefNavigatorHeadRowTests
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

    private sealed class RepoFixture : IDisposable
    {
        public string Path { get; }

        public RepoFixture(bool detach = false, bool twoCommits = false)
        {
            Path = TestScratch.NewDirectory("historia-headrow-");
            RunGit("init", "-b", "main");
            RunGit("config", "user.name", "Test");
            RunGit("config", "user.email", "test@example.com");
            RunGit("config", "commit.gpgsign", "false");
            File.WriteAllText(System.IO.Path.Combine(Path, "f.txt"), "init\n");
            RunGit("add", ".");
            RunGit("commit", "-m", "c1");

            if (twoCommits)
            {
                File.WriteAllText(System.IO.Path.Combine(Path, "f.txt"), "c2\n");
                RunGit("add", ".");
                RunGit("commit", "-m", "c2");
            }

            RunGit("remote", "add", "origin", "https://HistoriaHQ.dev/repo.git");
            RunGit("update-ref", "refs/remotes/origin/main", "HEAD");
            RunGit("symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/main");

            if (detach)
            {
                RunGit("checkout", "--detach");
            }
        }

        public string RunGit(params string[] args) => RunGitIn(Path, args);

        public string HeadSha => RunGit("rev-parse", "HEAD").Trim();

        public void Dispose() => TestScratch.Delete(Path);
    }

    private static string RunGitIn(string dir, params string[] args)
    {
        return TestGit.RunChecked(dir, args, timeout: TimeSpan.FromSeconds(30), testIdentity: false);
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

    private static (RefNavigatorView View, ServiceProvider Sp, IDisposable Fixture, RepositoryContext Context, Window Window) CreateView(
        string repoPath, IDisposable fixture)
    {
        var sp = CreateServices();
        var ws = sp.GetRequiredService<IWorkspaceService>();
        var ctx = ws.OpenAsync(repoPath).GetAwaiter().GetResult();
        ctx.MarkFirstGraphPaintDone();
        ctx.State.RefreshRefsAsync().GetAwaiter().GetResult();
        ws.Focus(ctx);
        var view = new RefNavigatorView(sp);
        var window = new Window { Width = 300, Height = 800, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (view, sp, fixture, ctx, window);
    }

    private static void Cleanup((RefNavigatorView View, ServiceProvider Sp, IDisposable Fixture, RepositoryContext Context, Window Window) s)
    {
        try { s.Window.Close(); } catch { }
        try { s.View.Dispose(); } catch { }
        try { s.Context.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        try { s.Sp.Dispose(); } catch { }
        try { s.Fixture.Dispose(); } catch { }
    }

    private static IEnumerable<Border> LeafRows(RefNavigatorView view) =>
        view.GetVisualDescendants().OfType<Border>()
            .Where(b => b.Classes.Contains("ref-row") && b.DataContext is RefLeafRowViewModel)
            .OrderBy(b => b.TranslatePoint(default, view)?.Y ?? 0);

    private static TextBlock RowLabel(Border row) =>
        row.GetVisualDescendants().OfType<TextBlock>().First(t => Grid.GetColumn(t) == 2);

    private static TextBlock? SectionHeader(RefNavigatorView view, string prefix) =>
        view.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Text?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true);

    private static void Click(Border row)
    {
        row.RaiseEvent(new PointerPressedEventArgs(row,
            new Pointer(1, PointerType.Mouse, true), row, new Point(5, 5), 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None));
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Remote_HEAD_symref_has_no_row()
    {
        var fixture = new RepoFixture(detach: false);
        var setup = CreateView(fixture.Path, fixture);
        try
        {
            var leafTexts = LeafRows(setup.View).Select(r => RowLabel(r).Text).ToList();
            Assert.DoesNotContain("HEAD", leafTexts);
            Assert.Equal(2, leafTexts.Count(t => t == "main"));
        }
        finally
        {
            Cleanup(setup);
        }
    }

    [AvaloniaFact]
    public void Delete_remote_count_excludes_remote_HEAD()
    {
        var fixture = new RepoFixture(detach: false);
        var setup = CreateView(fixture.Path, fixture);
        try
        {
            var originBranches = setup.Context.State.Branches
                .Where(b => b.RemoteName == "origin")
                .ToList();
            var b = Assert.Single(originBranches);
            Assert.Equal("origin/main", b.Name);
        }
        finally
        {
            Cleanup(setup);
        }
    }

    [AvaloniaFact]
    public void Detached_head_shows_head_row_first()
    {
        var fixture = new RepoFixture(detach: true);
        var headSha = fixture.HeadSha;
        var setup = CreateView(fixture.Path, fixture);
        try
        {
            var leaves = LeafRows(setup.View).ToList();
            Assert.NotEmpty(leaves);
            var firstLabel = RowLabel(leaves[0]).Text;
            Assert.Equal($"HEAD (detached at {headSha[..7]})", firstLabel);

            var header = SectionHeader(setup.View, "Local Branches");
            Assert.NotNull(header);
            Assert.Equal("Local Branches (1)", header!.Text, ignoreCase: true);
        }
        finally
        {
            Cleanup(setup);
        }
    }

    [AvaloniaFact]
    public void Attached_head_has_no_head_row()
    {
        var fixture = new RepoFixture(detach: false);
        var setup = CreateView(fixture.Path, fixture);
        try
        {
            var leaves = LeafRows(setup.View).ToList();
            Assert.DoesNotContain(leaves, r => RowLabel(r).Text?.StartsWith("HEAD (detached at ", StringComparison.Ordinal) == true);
        }
        finally
        {
            Cleanup(setup);
        }
    }

    [AvaloniaFact]
    public async Task Moving_between_detached_commits_updates_head_row()
    {
        var fixture = new RepoFixture(detach: true, twoCommits: true);
        var headSha1 = fixture.HeadSha;
        var setup = CreateView(fixture.Path, fixture);
        try
        {
            var leaf = LeafRows(setup.View).First(r => RowLabel(r).Text?.StartsWith("HEAD (detached at ", StringComparison.Ordinal) == true);
            Assert.Equal($"HEAD (detached at {headSha1[..7]})", RowLabel(leaf).Text);

            fixture.RunGit("checkout", "--detach", "HEAD~1");
            var headSha2 = fixture.HeadSha;
            Assert.NotEqual(headSha1, headSha2);

            await setup.Context.State.RefreshRefsAsync();
            Dispatcher.UIThread.RunJobs();

            var updatedLeaf = LeafRows(setup.View).First(r => RowLabel(r).Text?.StartsWith("HEAD (detached at ", StringComparison.Ordinal) == true);
            Assert.Equal($"HEAD (detached at {headSha2[..7]})", RowLabel(updatedLeaf).Text);
        }
        finally
        {
            Cleanup(setup);
        }
    }

    [AvaloniaFact]
    public void Clicking_head_row_selects_head_commit()
    {
        var fixture = new RepoFixture(detach: true);
        var headSha = fixture.HeadSha;
        var setup = CreateView(fixture.Path, fixture);
        try
        {
            var headRow = LeafRows(setup.View).First(r => RowLabel(r).Text?.StartsWith("HEAD (detached at ", StringComparison.Ordinal) == true);
            Click(headRow);

            Assert.Equal(new GitObjectId(headSha), setup.Context.State.SelectedCommitId);
        }
        finally
        {
            Cleanup(setup);
        }
    }

    [AvaloniaFact]
    public void Detached_submodule_shows_head_row_and_no_remote_HEAD()
    {
        var innerDir = TestScratch.NewDirectory("historia-submodule-inner-");
        var superDir = TestScratch.NewDirectory("historia-submodule-super-");
        var cloneDir = TestScratch.NewDirectory("historia-submodule-clone-");

        RunGitIn(innerDir, "init", "-b", "main");
        RunGitIn(innerDir, "config", "user.name", "Test");
        RunGitIn(innerDir, "config", "user.email", "test@example.com");
        RunGitIn(innerDir, "config", "commit.gpgsign", "false");
        File.WriteAllText(System.IO.Path.Combine(innerDir, "sub.txt"), "sub1\n");
        RunGitIn(innerDir, "add", ".");
        RunGitIn(innerDir, "commit", "-m", "sub c1");

        RunGitIn(superDir, "init", "-b", "main");
        RunGitIn(superDir, "config", "user.name", "Test");
        RunGitIn(superDir, "config", "user.email", "test@example.com");
        RunGitIn(superDir, "config", "commit.gpgsign", "false");
        RunGitIn(superDir, "config", "protocol.file.allow", "always");
        File.WriteAllText(System.IO.Path.Combine(superDir, "readme.txt"), "super\n");
        RunGitIn(superDir, "add", ".");
        RunGitIn(superDir, "commit", "-m", "super c1");
        RunGitIn(superDir, "-c", "protocol.file.allow=always", "submodule", "add", innerDir.Replace('\\', '/'), "mysub");
        RunGitIn(superDir, "commit", "-m", "add submodule");

        RunGitIn(superDir, "-c", "protocol.file.allow=always", "clone", "--recurse-submodules", superDir, cloneDir);

        var registeredSha = RunGitIn(superDir, "ls-tree", "HEAD", "mysub")
            .Split([' ', '	'], StringSplitOptions.RemoveEmptyEntries)[2];

        var subPath = System.IO.Path.Combine(cloneDir, "mysub");
        RunGitIn(subPath, "symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/main");

        var cleanups = new Action(() =>
        {
            TestScratch.Delete(cloneDir);
            TestScratch.Delete(superDir);
            TestScratch.Delete(innerDir);
        });

        var setup = CreateView(subPath, new DisposableAction(cleanups));
        try
        {
            Assert.True(setup.Context.State.Repository.IsHeadDetached);

            var leaves = LeafRows(setup.View).ToList();
            var headRow = leaves.FirstOrDefault(r => RowLabel(r).Text?.StartsWith("HEAD (detached at ", StringComparison.Ordinal) == true);
            Assert.NotNull(headRow);
            Assert.Equal($"HEAD (detached at {registeredSha[..7]})", RowLabel(headRow!).Text);

            var leafTexts = leaves.Select(r => RowLabel(r).Text).ToList();
            Assert.DoesNotContain("HEAD", leafTexts);
        }
        finally
        {
            Cleanup(setup);
        }
    }

    private sealed class DisposableAction(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
