namespace HistoriaApp.Tests;

/// <summary>
/// How the network-action runner reports failures: a server-policy decline is an error toast
/// (never the non-fast-forward chooser or a force push), a non-fast-forward still opens the
/// chooser, the toast names the operation once, a request on a busy repository gets a notice
/// naming what is running while other repositories stay free, a request that finds auto-fetch
/// running waits for it (cancellably), a network retry started outside that flow follows the same
/// rules, and a request pinned to a repository runs there whichever tab is focused.
/// </summary>
[Trait("tier", "ui")]
public sealed class RepoNetworkErrorRoutingTests
{
    private sealed class CapturingNotificationService : INotificationService
    {
        public List<Notification> Published { get; } = new();
        public IReadOnlyList<Notification> Current => Published;

        public Notification Publish(
            NotificationSeverity severity,
            string message,
            string? details = null,
            IReadOnlyList<NotificationAction>? actions = null)
        {
            var n = new Notification
            {
                Id = Guid.NewGuid(),
                Severity = severity,
                Message = message,
                Details = details,
                Actions = actions ?? [],
                CreatedAt = DateTimeOffset.Now,
            };
            Published.Add(n);
            Changed?.Invoke(this, new NotificationsChangedEventArgs(Published));
            return n;
        }

        public void Dismiss(Guid id) { }
        public event EventHandler<NotificationsChangedEventArgs>? Changed;
    }

    /// <summary>Counts the push-rejection chooser and the destructive confirm. The chooser answers
    /// Cancel; the destructive confirm answers <see cref="ConfirmAnswer"/> (Cancel by default).</summary>
    private sealed class CountingDialogService : IDialogService
    {
        public int ChooseCalls;
        public int ConfirmDestructiveCalls;
        public bool ConfirmAnswer;
        public string? LastEffect;

        public Task<int> ChooseAsync(string title, string message, IReadOnlyList<string> options,
            int defaultIndex = 0, int destructiveIndex = -1, DialogIcon icon = DialogIcon.None)
        {
            ChooseCalls++;
            return Task.FromResult(-1);
        }

        public Task<bool> ConfirmDestructiveAsync(string title, string effect, string confirmLabel)
        {
            ConfirmDestructiveCalls++;
            LastEffect = effect;
            return Task.FromResult(ConfirmAnswer);
        }

        public Task<string?> PickFolderAsync(string title, string? initialDirectory = null) => throw new NotSupportedException();
        public Task<string?> PickFileAsync(string title, string filter, string? initialDirectory = null) => throw new NotSupportedException();
        public Task<bool> ConfirmAsync(string title, string message, bool destructive = false,
            string confirmLabel = "OK", bool defaultToCancel = false, DialogIcon icon = DialogIcon.None) => throw new NotSupportedException();
        public Task ShowMessageAsync(string title, string message) => throw new NotSupportedException();
        public Task<string?> PromptAsync(string title, string message, string? defaultValue = null,
            string confirmLabel = "OK", bool primaryConfirm = false) => throw new NotSupportedException();
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

    /// <summary>A bare remote and a clone ("local") with one unpushed commit on <c>main</c>.
    /// <see cref="Diverge"/> makes the remote move on first (non-fast-forward);
    /// <see cref="DeclineEverything"/> installs a pre-receive hook that refuses every push.</summary>
    private sealed class PushFixture : IDisposable
    {
        private readonly string _root = TestScratch.NewDirectory("historia-neterr-");

        public string RemotePath { get; }
        public string LocalPath { get; }

        public PushFixture()
        {
            RemotePath = Path.Combine(_root, "remote.git");
            LocalPath = Path.Combine(_root, "local");
            Directory.CreateDirectory(RemotePath);
            RunGit(RemotePath, "init", "--bare");
            RunGit(_root, "clone", RemotePath, LocalPath);
            Configure(LocalPath);
            File.WriteAllText(Path.Combine(LocalPath, "f.txt"), "init\n");
            RunGit(LocalPath, "add", "f.txt");
            RunGit(LocalPath, "commit", "-m", "init");
            RunGit(LocalPath, "branch", "-M", "main");
            RunGit(LocalPath, "push", "-u", "origin", "main");
            File.WriteAllText(Path.Combine(LocalPath, "g.txt"), "local\n");
            RunGit(LocalPath, "add", "g.txt");
            RunGit(LocalPath, "commit", "-m", "local commit");
        }

        public PushFixture Diverge()
        {
            var other = Path.Combine(_root, "other-" + Guid.NewGuid().ToString("N"));
            RunGit(_root, "clone", RemotePath, other);
            Configure(other);
            RunGit(other, "checkout", "main");
            File.WriteAllText(Path.Combine(other, "f.txt"), "other " + Guid.NewGuid().ToString("N") + "\n");
            RunGit(other, "add", "f.txt");
            RunGit(other, "commit", "-m", "other commit");
            RunGit(other, "push", "origin", "main");
            return this;
        }

        public PushFixture DeclineEverything()
        {
            var hook = Path.Combine(RemotePath, "hooks", "pre-receive");
            Directory.CreateDirectory(Path.GetDirectoryName(hook)!);
            File.WriteAllText(hook,
                "#!/bin/sh\n"
                + "echo 'error: File big.bin is 361.72 MB; this exceeds the file size limit of 100.00 MB' >&2\n"
                + "exit 1\n");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return this;
        }

        /// <summary>Leaves <c>refs/remotes/origin/main.lock</c> in the clone, last written
        /// <paramref name="age"/> ago — what a git process killed mid-fetch leaves behind.</summary>
        public string LeaveRefLock(TimeSpan age)
        {
            var path = Path.Combine(LocalPath, ".git", "refs", "remotes", "origin", "main.lock");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
            return path;
        }

        /// <summary>Sets the clone to prune on fetch and deletes a branch from the remote whose
        /// remote-tracking ref has a stale <c>.lock</c>, so the next fetch fails in git's own
        /// "cannot lock ref … File exists" form.</summary>
        public PushFixture BlockPruneWithStaleLock(TimeSpan age)
        {
            RunGit(LocalPath, "push", "origin", "HEAD:refs/heads/gone");
            RunGit(LocalPath, "fetch", "origin");
            RunGit(RemotePath, "branch", "-D", "gone");
            RunGit(LocalPath, "config", "fetch.prune", "true");
            var path = Path.Combine(LocalPath, ".git", "refs", "remotes", "origin", "gone.lock");
            File.WriteAllText(path, "");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
            return this;
        }

        public PushFixture PointOriginAt(string url)
        {
            RunGit(LocalPath, "remote", "set-url", "origin", url);
            return this;
        }

        public PushFixture PushBranch(string branch)
        {
            RunGit(LocalPath, "branch", branch);
            RunGit(LocalPath, "push", "origin", branch);
            return this;
        }

        public PushFixture Checkout(string branch)
        {
            RunGit(LocalPath, "checkout", branch);
            return this;
        }

        /// <summary>Replaces <paramref name="name"/> on the remote with a same-named tag, as if
        /// someone else deleted the branch, leaving the local remote-tracking ref stale.</summary>
        public PushFixture ReplaceRemoteBranchWithTag(string name)
        {
            RunGit(RemotePath, "branch", "-D", name);
            RunGit(RemotePath, "tag", name, "main");
            return this;
        }

        /// <summary>Tags the pushed <c>main</c> as <paramref name="tag"/> on the remote and in the
        /// clone, then moves the remote's tag to a commit pushed from another clone, as re-cutting
        /// a release does.</summary>
        public PushFixture MoveRemoteTag(string tag)
        {
            RunGit(LocalPath, "tag", tag, "origin/main");
            RunGit(LocalPath, "push", "origin", tag);
            Diverge();
            RunGit(RemotePath, "tag", "-f", tag, "main");
            return this;
        }

        /// <summary>Tags the clone's unpushed <c>HEAD</c> as <paramref name="tag"/>, locally only.</summary>
        public PushFixture TagLocally(string tag)
        {
            RunGit(LocalPath, "tag", tag);
            return this;
        }

        public static string Rev(string dir, string rev) =>
            TestGit.Run(dir, ["rev-parse", rev], testIdentity: false).Stdout.Trim();

        public bool RemoteHasBranch(string branch) => RemoteLists("branch", branch);

        public bool RemoteHasTag(string tag) => RemoteLists("tag", tag);

        private bool RemoteLists(string kind, string name)
        {
            var stdout = TestGit.Run(RemotePath, [kind, "--list", name], testIdentity: false).Stdout;
            return !string.IsNullOrWhiteSpace(stdout);
        }

        private static void Configure(string dir)
        {
            RunGit(dir, "config", "user.name", "Test");
            RunGit(dir, "config", "user.email", "test@example.com");
            RunGit(dir, "config", "commit.gpgsign", "false");
        }

        private static void RunGit(string dir, params string[] args)
        {
            TestGit.Run(dir, args);
        }

        public void Dispose() => TestScratch.Delete(_root);
    }

    private static ServiceProvider CreateServices(INotificationService notify, IDialogService dialogs)
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
        sc.AddSingleton(notify);
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

    private sealed class Harness : IAsyncDisposable
    {
        private readonly List<RepositoryContext> _opened = [];

        public CapturingNotificationService Notify { get; } = new();
        public CountingDialogService Dialogs { get; } = new();
        public ServiceProvider Services { get; }
        public IWorkspaceService Workspace => Services.GetRequiredService<IWorkspaceService>();
        public IRepoActionBus Bus => Services.GetRequiredService<IRepoActionBus>();
        public IRepoNetworkActionRunner Runner => Services.GetRequiredService<IRepoNetworkActionRunner>();

        public Harness() => Services = CreateServices(Notify, Dialogs);

        public async Task<RepositoryContext> OpenAsync(string path, bool focus = true)
        {
            var ctx = await Workspace.OpenAsync(path);
            _opened.Add(ctx);
            ctx.MarkFirstGraphPaintDone();
            ctx.MarkFirstFullLoadDone();
            await ctx.State.RefreshRefsAsync();
            if (focus) Workspace.Focus(ctx);
            return ctx;
        }

        /// <summary>Starts an action and waits for the runner's idle tick (subscribed first: a
        /// failure before the first real await raises it synchronously).</summary>
        public async Task RunToIdleAsync(Action start)
        {
            RepoNetworkProgress? final = null;
            void OnProgress(object? s, RepoNetworkProgress p) { if (p.Progress is null) final = p; }
            Runner.ProgressChanged += OnProgress;
            try
            {
                start();
                for (int i = 0; i < 1000 && final is null; i++)
                {
                    Dispatcher.UIThread.RunJobs();
                    await Task.Delay(20);
                }
            }
            finally
            {
                Runner.ProgressChanged -= OnProgress;
            }
            Assert.NotNull(final);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var ctx in _opened)
                await Workspace.CloseAsync(ctx);
            await Services.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task A_declined_push_is_an_error_toast_never_the_rejection_chooser()
    {
        using var fixture = new PushFixture().DeclineEverything();
        await using var h = new Harness();
        await h.OpenAsync(fixture.LocalPath);

        await h.RunToIdleAsync(() => h.Bus.RequestNetworkAction(RepoNetworkAction.Push));

        Assert.Equal(0, h.Dialogs.ChooseCalls);
        Assert.Equal(0, h.Dialogs.ConfirmDestructiveCalls);
        var error = Assert.Single(h.Notify.Published, n => n.Severity == NotificationSeverity.Error);
        Assert.Equal("Push failed: the remote declined it — its reason is in Details.", error.Message);
        Assert.Equal(1, error.Message.Split("Push").Length - 1);
        Assert.StartsWith("error: File big.bin is 361.72 MB", error.Details);
    }

    [AvaloniaFact]
    public async Task A_non_fast_forward_push_on_the_checked_out_branch_still_opens_the_chooser()
    {
        using var fixture = new PushFixture().Diverge();
        await using var h = new Harness();
        await h.OpenAsync(fixture.LocalPath);

        await h.RunToIdleAsync(() => h.Bus.RequestNetworkAction(RepoNetworkAction.Push));

        Assert.Equal(1, h.Dialogs.ChooseCalls);
    }

    [AvaloniaFact]
    public async Task An_unreachable_push_names_the_operation_once()
    {
        using var fixture = new PushFixture().PointOriginAt("https://127.0.0.1:1/r.git");
        await using var h = new Harness();
        await h.OpenAsync(fixture.LocalPath);

        await h.RunToIdleAsync(() => h.Bus.RequestNetworkAction(RepoNetworkAction.Push));

        var error = Assert.Single(h.Notify.Published, n => n.Severity == NotificationSeverity.Error);
        Assert.StartsWith("Push failed: ", error.Message);
        Assert.DoesNotContain("Push failed: Push failed", error.Message);
    }

    [AvaloniaFact]
    public async Task A_second_request_on_a_busy_repo_gets_a_notice_naming_it_and_runs_nothing()
    {
        using var fixture = new PushFixture();
        await using var h = new Harness();
        var repo = await h.OpenAsync(fixture.LocalPath);

        await h.RunToIdleAsync(() =>
        {
            h.Bus.RequestNetworkAction(RepoNetworkAction.Fetch);
            h.Bus.RequestNetworkAction(RepoNetworkAction.Fetch);
        });

        var info = Assert.Single(h.Notify.Published, n => n.Severity == NotificationSeverity.Info);
        Assert.Equal($"Fetch on {repo.DisplayName} is still running.", info.Message);
        Assert.Single(h.Notify.Published, n => n.Severity == NotificationSeverity.Success);
    }

    [AvaloniaFact]
    public async Task A_busy_repo_does_not_block_a_fetch_on_another_repo()
    {
        using var busyFixture = new PushFixture();
        using var otherFixture = new PushFixture();
        await using var h = new Harness();
        var busy = await h.OpenAsync(busyFixture.LocalPath, focus: false);
        var other = await h.OpenAsync(otherFixture.LocalPath);

        Assert.True(busy.TryBeginNetworkOperation(new NetworkOperationInfo(NetworkOperationOwner.User, "Pull"), out var held));
        using (held)
        {
            await h.RunToIdleAsync(() => h.Bus.RequestNetworkAction(
                new RepoNetworkRequest(RepoNetworkAction.Fetch, null, null, Repo: other)));
        }

        Assert.DoesNotContain(h.Notify.Published, n => n.Severity == NotificationSeverity.Info);
        Assert.Single(h.Notify.Published, n => n.Severity == NotificationSeverity.Success);
    }

    [AvaloniaFact]
    public async Task A_lock_cleanup_on_the_repo_gets_a_notice_naming_it()
    {
        using var fixture = new PushFixture();
        await using var h = new Harness();
        var repo = await h.OpenAsync(fixture.LocalPath);

        Assert.True(repo.TryBeginNetworkOperation(new NetworkOperationInfo(NetworkOperationOwner.Maintenance, "Lock cleanup"), out var held));
        using (held)
        {
            // Refused before it starts, so there is no idle tick to wait for — wait for the notice.
            _ = h.Runner; // the runner subscribes to the bus when it is first resolved
            h.Bus.RequestNetworkAction(RepoNetworkAction.Fetch);
            for (var i = 0; i < 250 && !h.Notify.Published.Any(); i++)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(20);
            }
        }

        var info = Assert.Single(h.Notify.Published, n => n.Severity == NotificationSeverity.Info);
        Assert.Equal($"Lock cleanup on {repo.DisplayName} is still running.", info.Message);
        Assert.DoesNotContain(h.Notify.Published, n => n.Severity == NotificationSeverity.Success);
    }

    [AvaloniaFact]
    public async Task A_request_during_auto_fetch_waits_for_it_then_runs()
    {
        using var fixture = new PushFixture();
        await using var h = new Harness();
        var repo = await h.OpenAsync(fixture.LocalPath);

        Assert.True(repo.TryBeginNetworkOperation(new NetworkOperationInfo(NetworkOperationOwner.AutoFetch, "Auto-fetch"), out var autoFetch));
        var phases = new List<string?>();
        void OnProgress(object? s, RepoNetworkProgress p)
        {
            phases.Add(p.Progress?.Phase);
            if (p.Progress?.Phase == "Waiting for auto-fetch…")
            {
                Assert.Equal("Waiting for auto-fetch…", h.Runner.CurrentProgress(repo)?.Phase);
                autoFetch.Dispose();
            }
        }
        h.Runner.ProgressChanged += OnProgress;
        try
        {
            await h.RunToIdleAsync(() => h.Bus.RequestNetworkAction(RepoNetworkAction.Fetch));
        }
        finally
        {
            h.Runner.ProgressChanged -= OnProgress;
        }

        Assert.Equal("Waiting for auto-fetch…", phases[0]);
        Assert.Contains("Fetch…", phases);
        Assert.Single(h.Notify.Published, n => n.Severity == NotificationSeverity.Success);
        Assert.Null(h.Runner.CurrentProgress(repo));
        Assert.Null(repo.CurrentNetworkOperation);
    }

    [AvaloniaFact]
    public async Task Cancelling_while_waiting_for_auto_fetch_ends_silently_without_running()
    {
        using var fixture = new PushFixture();
        await using var h = new Harness();
        var repo = await h.OpenAsync(fixture.LocalPath);

        Assert.True(repo.TryBeginNetworkOperation(new NetworkOperationInfo(NetworkOperationOwner.AutoFetch, "Auto-fetch"), out var autoFetch));
        using (autoFetch)
        {
            void CancelWhileWaiting(object? s, RepoNetworkProgress p)
            {
                if (p.Progress?.Phase == "Waiting for auto-fetch…")
                    h.Runner.Cancel(repo);
            }
            h.Runner.ProgressChanged += CancelWhileWaiting;
            try
            {
                await h.RunToIdleAsync(() => h.Bus.RequestNetworkAction(RepoNetworkAction.Fetch));
            }
            finally
            {
                h.Runner.ProgressChanged -= CancelWhileWaiting;
            }

            Assert.Empty(h.Notify.Published);
            Assert.Equal("Auto-fetch", repo.CurrentNetworkOperation?.Label);
        }
    }

    [AvaloniaFact]
    public async Task Waiting_behind_auto_fetch_marks_the_perf_log_with_the_holders_label()
    {
        var stateDir = TestScratch.NewDirectory("historia-netgate-perf-");
        AppPaths.OverrideForTesting(stateDir);
        Historia.Diagnostics.LoggingSwitch.OverrideForTesting(true);
        try
        {
            using var fixture = new PushFixture();
            await using var h = new Harness();
            var repo = await h.OpenAsync(fixture.LocalPath);

            Assert.True(repo.TryBeginNetworkOperation(new NetworkOperationInfo(NetworkOperationOwner.AutoFetch, "Auto-fetch"), out var autoFetch));
            _ = Task.Delay(150).ContinueWith(_ => autoFetch.Dispose(), TaskScheduler.Default);
            await h.Runner.RunExclusiveAsync(repo, "Pull", (_, _) => Task.CompletedTask);

            Historia.Diagnostics.PerfLog.Flush();
            var mark = Assert.Single(ReadGateMarks());
            Assert.Contains("behind Auto-fetch", mark);
            Assert.Contains("Pull", mark);
            Assert.DoesNotContain(", cancelled", mark);
        }
        finally
        {
            Historia.Diagnostics.LoggingSwitch.OverrideForTesting(null);
            AppPaths.OverrideForTesting(null);
            TestScratch.Delete(stateDir);
        }
    }

    [AvaloniaFact]
    public async Task A_waiter_cancelled_behind_auto_fetch_marks_the_perf_log_as_cancelled()
    {
        var stateDir = TestScratch.NewDirectory("historia-netgate-perf-");
        AppPaths.OverrideForTesting(stateDir);
        Historia.Diagnostics.LoggingSwitch.OverrideForTesting(true);
        try
        {
            using var fixture = new PushFixture();
            await using var h = new Harness();
            var repo = await h.OpenAsync(fixture.LocalPath);

            Assert.True(repo.TryBeginNetworkOperation(new NetworkOperationInfo(NetworkOperationOwner.AutoFetch, "Auto-fetch"), out var autoFetch));
            using (autoFetch)
            {
                _ = Task.Delay(150).ContinueWith(_ => Dispatcher.UIThread.Post(() => h.Runner.Cancel(repo)), TaskScheduler.Default);
                await h.Runner.RunExclusiveAsync(repo, "Pull", (_, _) => Task.CompletedTask);
            }

            Historia.Diagnostics.PerfLog.Flush();
            var mark = Assert.Single(ReadGateMarks());
            Assert.Contains("behind Auto-fetch, cancelled", mark);
        }
        finally
        {
            Historia.Diagnostics.LoggingSwitch.OverrideForTesting(null);
            AppPaths.OverrideForTesting(null);
            TestScratch.Delete(stateDir);
        }
    }

    private static string[] ReadGateMarks()
    {
        var path = Path.Combine(AppPaths.LogsDirectory, "perf.log");
        return File.Exists(path)
            ? File.ReadAllLines(path).Where(l => l.Contains("network gate wait")).ToArray()
            : [];
    }

    [AvaloniaFact]
    public async Task An_exclusive_operation_on_a_busy_repo_gets_a_notice_and_does_not_run()
    {
        using var fixture = new PushFixture();
        await using var h = new Harness();
        var repo = await h.OpenAsync(fixture.LocalPath);

        var ran = false;
        Assert.True(repo.TryBeginNetworkOperation(new NetworkOperationInfo(NetworkOperationOwner.User, "Fetch"), out var held));
        using (held)
        {
            await h.Runner.RunExclusiveAsync(repo, "Pull", (_, _) =>
            {
                ran = true;
                return Task.CompletedTask;
            });
        }

        Assert.False(ran);
        var info = Assert.Single(h.Notify.Published);
        Assert.Equal($"Fetch on {repo.DisplayName} is still running.", info.Message);
    }

    [AvaloniaFact]
    public async Task An_exclusive_operation_waits_for_auto_fetch_and_runs_holding_the_gate()
    {
        using var fixture = new PushFixture();
        await using var h = new Harness();
        var repo = await h.OpenAsync(fixture.LocalPath);

        Assert.True(repo.TryBeginNetworkOperation(new NetworkOperationInfo(NetworkOperationOwner.AutoFetch, "Auto-fetch"), out var autoFetch));
        void ReleaseOnWait(object? s, RepoNetworkProgress p)
        {
            if (p.Progress?.Phase == "Waiting for auto-fetch…")
                autoFetch.Dispose();
        }
        h.Runner.ProgressChanged += ReleaseOnWait;
        NetworkOperationInfo? heldWhileRunning = null;
        try
        {
            await h.Runner.RunExclusiveAsync(repo, "Pull", (_, _) =>
            {
                heldWhileRunning = repo.CurrentNetworkOperation;
                return Task.CompletedTask;
            });
        }
        finally
        {
            h.Runner.ProgressChanged -= ReleaseOnWait;
        }

        Assert.Equal(new NetworkOperationInfo(NetworkOperationOwner.User, "Pull"), heldWhileRunning);
        Assert.Null(repo.CurrentNetworkOperation);
        Assert.Null(h.Runner.CurrentProgress(repo));
    }

    [AvaloniaFact]
    public async Task Cancelling_an_exclusive_operation_ends_silently()
    {
        using var fixture = new PushFixture();
        await using var h = new Harness();
        var repo = await h.OpenAsync(fixture.LocalPath);

        await h.Runner.RunExclusiveAsync(repo, "Pull", async (_, ct) =>
        {
            h.Runner.Cancel(repo);
            await Task.Delay(Timeout.Infinite, ct);
        });

        Assert.Empty(h.Notify.Published);
        Assert.Null(repo.CurrentNetworkOperation);
    }

    [AvaloniaFact]
    public async Task A_request_pinned_to_a_repo_runs_there_even_when_another_is_focused()
    {
        using var diverged = new PushFixture().Diverge();
        using var other = new PushFixture();
        await using var h = new Harness();
        var target = await h.OpenAsync(diverged.LocalPath, focus: false);
        var focused = await h.OpenAsync(other.LocalPath);
        Assert.Same(focused, h.Workspace.Focused);

        await h.RunToIdleAsync(() => h.Bus.RequestNetworkAction(
            new RepoNetworkRequest(RepoNetworkAction.Push, null, null, Repo: target)));

        // Only the diverged repository's push is rejected; the focused one would have succeeded.
        Assert.Equal(1, h.Dialogs.ChooseCalls);
        Assert.DoesNotContain(h.Notify.Published, n => n.Severity == NotificationSeverity.Success);
    }

    [AvaloniaFact]
    public async Task Commit_and_push_requests_a_push_of_the_panels_own_repository()
    {
        using var fixture = new PushFixture();
        await using var h = new Harness();
        var ctx = await h.OpenAsync(fixture.LocalPath);
        await ctx.State.RefreshAllAsync();

        var request = StagingPanelViewModel.BuildPushRequest(ctx);

        Assert.NotNull(request);
        Assert.Equal(RepoNetworkAction.Push, request!.Value.Action);
        Assert.Equal("origin", request.Value.Remote);
        Assert.Equal("main", request.Value.Branch);
        Assert.Same(ctx, request.Value.Repo);
    }

    // ── moved remote tags ──

    [AvaloniaFact]
    public async Task A_fetch_refused_by_a_moved_remote_tag_offers_replacing_it_behind_a_destructive_confirm()
    {
        using var fixture = new PushFixture().MoveRemoteTag("v1");
        await using var h = new Harness();
        await h.OpenAsync(fixture.LocalPath);
        var remoteTag = PushFixture.Rev(fixture.RemotePath, "v1");

        await h.RunToIdleAsync(() => h.Bus.RequestNetworkAction(RepoNetworkAction.Fetch));

        var warning = Assert.Single(h.Notify.Published, n => n.Severity == NotificationSeverity.Warning);
        Assert.Equal("Fetched origin, but tag v1 points to a different commit there.", warning.Message);
        Assert.Equal(PushFixture.Rev(fixture.RemotePath, "main"), PushFixture.Rev(fixture.LocalPath, "origin/main"));
        var replace = Assert.Single(warning.Actions);
        Assert.Equal("Replace local v1", replace.Label);

        // Declined: the tag stays where it was.
        await replace.Invoke();
        Assert.Equal(1, h.Dialogs.ConfirmDestructiveCalls);
        Assert.Contains("'v1'", h.Dialogs.LastEffect);
        Assert.NotEqual(remoteTag, PushFixture.Rev(fixture.LocalPath, "v1"));

        // Confirmed: the tag moves to the remote's commit and the next fetch is clean.
        h.Dialogs.ConfirmAnswer = true;
        await replace.Invoke();
        Assert.Equal(remoteTag, PushFixture.Rev(fixture.LocalPath, "v1"));
        Assert.Contains(h.Notify.Published, n => n.Severity == NotificationSeverity.Success && n.Message == "Replaced v1 with origin's.");

        await h.RunToIdleAsync(() => h.Bus.RequestNetworkAction(RepoNetworkAction.Fetch));
        Assert.Contains(h.Notify.Published, n => n.Severity == NotificationSeverity.Success && n.Message == "Fetched origin.");
    }

    [AvaloniaFact]
    public async Task Auto_fetch_refused_by_a_moved_remote_tag_warns_once_with_the_replacement()
    {
        using var fixture = new PushFixture().MoveRemoteTag("v1");
        await using var h = new Harness();
        var repo = await h.OpenAsync(fixture.LocalPath);
        var host = h.Services.GetRequiredService<IAutoFetchHost>();

        var failure = await Assert.ThrowsAsync<HistoriaGitException>(() => host.FetchAsync(repo, CancellationToken.None));
        await Assert.ThrowsAsync<HistoriaGitException>(() => host.FetchAsync(repo, CancellationToken.None));

        Assert.Equal(GitErrorKind.TagConflict, failure.Kind);
        var warning = Assert.Single(h.Notify.Published, n => n.Severity == NotificationSeverity.Warning);
        Assert.Equal($"Auto-fetch of {repo.DisplayName}: tag v1 points to a different commit on origin.", warning.Message);
        Assert.Equal("Replace local v1", Assert.Single(warning.Actions).Label);
    }

    // ── stale lock files ──

    [AvaloniaFact]
    public async Task A_fetch_blocked_by_a_stale_ref_lock_offers_removal_behind_a_destructive_confirm()
    {
        using var fixture = new PushFixture().Diverge();
        var lockFile = fixture.LeaveRefLock(TimeSpan.FromHours(3));
        await using var h = new Harness();
        var repo = await h.OpenAsync(fixture.LocalPath);

        await h.RunToIdleAsync(() => h.Bus.RequestNetworkAction(RepoNetworkAction.Fetch));
        var error = Assert.Single(h.Notify.Published, n => n.Severity == NotificationSeverity.Error);
        Assert.StartsWith("Fetch failed: ", error.Message);
        var remove = Assert.Single(error.Actions);
        Assert.Equal("Remove stale lock files…", remove.Label);

        // Declined: nothing is deleted.
        await remove.Invoke();
        Assert.Equal(1, h.Dialogs.ConfirmDestructiveCalls);
        Assert.Contains("refs/remotes/origin/main.lock", h.Dialogs.LastEffect);
        Assert.True(File.Exists(lockFile));

        // Confirmed: the lock goes and the next fetch succeeds.
        h.Dialogs.ConfirmAnswer = true;
        await remove.Invoke();
        Assert.False(File.Exists(lockFile));
        Assert.Contains(h.Notify.Published, n => n.Severity == NotificationSeverity.Success
            && n.Message == $"Removed 1 stale lock file from {repo.DisplayName}.");

        await h.RunToIdleAsync(() => h.Bus.RequestNetworkAction(RepoNetworkAction.Fetch));
        Assert.Contains(h.Notify.Published, n => n.Severity == NotificationSeverity.Success && n.Message == "Fetched origin.");
    }

    [AvaloniaFact]
    public async Task A_lock_younger_than_five_minutes_is_not_offered_for_removal()
    {
        using var fixture = new PushFixture().Diverge();
        fixture.LeaveRefLock(TimeSpan.FromMinutes(1));
        await using var h = new Harness();
        await h.OpenAsync(fixture.LocalPath);

        await h.RunToIdleAsync(() => h.Bus.RequestNetworkAction(RepoNetworkAction.Fetch));

        var error = Assert.Single(h.Notify.Published, n => n.Severity == NotificationSeverity.Error);
        Assert.Empty(error.Actions);
    }

    [AvaloniaFact]
    public async Task Auto_fetch_warns_once_per_local_failure_until_a_clean_fetch()
    {
        using var fixture = new PushFixture().Diverge();
        var lockFile = fixture.LeaveRefLock(TimeSpan.FromHours(3));
        await using var h = new Harness();
        var repo = await h.OpenAsync(fixture.LocalPath);
        var host = h.Services.GetRequiredService<IAutoFetchHost>();

        var failure = await Assert.ThrowsAsync<HistoriaGitException>(() => host.FetchAsync(repo, CancellationToken.None));
        await Assert.ThrowsAsync<HistoriaGitException>(() => host.FetchAsync(repo, CancellationToken.None));
        var warning = Assert.Single(h.Notify.Published, n => n.Severity == NotificationSeverity.Warning);
        // git reports the held ref lock either as "reference already exists" or, on some
        // platforms, with its "another git process" text, which classifies as LockedIndex.
        var why = failure.Kind == GitErrorKind.LockedIndex
            ? "another git process is using the repository"
            : "lock files left by a stopped git process are in the way";
        Assert.Equal($"Auto-fetch of {repo.DisplayName} failed: {why}.", warning.Message);
        Assert.Equal("Remove stale lock files…", Assert.Single(warning.Actions).Label);

        File.Delete(lockFile);
        await host.FetchAsync(repo, CancellationToken.None);

        fixture.LeaveRefLock(TimeSpan.FromHours(3));
        fixture.Diverge();
        await Assert.ThrowsAsync<HistoriaGitException>(() => host.FetchAsync(repo, CancellationToken.None));
        Assert.Equal(2, h.Notify.Published.Count(n => n.Severity == NotificationSeverity.Warning));
    }

    [AvaloniaFact]
    public async Task Auto_fetch_blocked_by_a_stale_lock_warns_once_with_the_remedy()
    {
        using var fixture = new PushFixture().BlockPruneWithStaleLock(TimeSpan.FromHours(3));
        await using var h = new Harness();
        var repo = await h.OpenAsync(fixture.LocalPath);
        var host = h.Services.GetRequiredService<IAutoFetchHost>();

        var failure = await Assert.ThrowsAsync<HistoriaGitException>(() => host.FetchAsync(repo, CancellationToken.None));
        await Assert.ThrowsAsync<HistoriaGitException>(() => host.FetchAsync(repo, CancellationToken.None));

        Assert.Equal(GitErrorKind.LockedIndex, failure.Kind);
        var warning = Assert.Single(h.Notify.Published, n => n.Severity == NotificationSeverity.Warning);
        Assert.Equal($"Auto-fetch of {repo.DisplayName} failed: another git process is using the repository.", warning.Message);
        Assert.Equal("Remove stale lock files…", Assert.Single(warning.Actions).Label);
    }

    [AvaloniaFact]
    public async Task Auto_fetch_never_warns_when_the_remote_is_unreachable()
    {
        using var fixture = new PushFixture().PointOriginAt("https://127.0.0.1:1/r.git");
        await using var h = new Harness();
        var repo = await h.OpenAsync(fixture.LocalPath);
        var host = h.Services.GetRequiredService<IAutoFetchHost>();

        await Assert.ThrowsAsync<HistoriaGitException>(() => host.FetchAsync(repo, CancellationToken.None));

        Assert.DoesNotContain(h.Notify.Published, n => n.Severity == NotificationSeverity.Warning);
    }

    [AvaloniaFact]
    public async Task A_declined_remote_branch_delete_deletes_nothing()
    {
        using var fixture = new PushFixture().PushBranch("a").PushBranch("b");
        await using var h = new Harness();
        h.Dialogs.ConfirmAnswer = false;
        var ctx = await h.OpenAsync(fixture.LocalPath);

        await h.RunToIdleAsync(() => h.Bus.RequestNetworkAction(
            new RepoNetworkRequest(RepoNetworkAction.DeleteRemoteBranch, "origin", null, "b")));

        Assert.Equal(1, h.Dialogs.ConfirmDestructiveCalls);
        Assert.True(fixture.RemoteHasBranch("b"));
        Assert.Contains(ctx.State.Branches, br => br.IsRemote && br.Name == "origin/b");
        Assert.DoesNotContain(h.Notify.Published, n => n.Severity == NotificationSeverity.Success);
    }

    [AvaloniaFact]
    public async Task A_confirmed_remote_branch_delete_removes_it_and_its_tracking_ref()
    {
        using var fixture = new PushFixture().PushBranch("a").PushBranch("b");
        await using var h = new Harness();
        h.Dialogs.ConfirmAnswer = true;
        var ctx = await h.OpenAsync(fixture.LocalPath);

        await h.RunToIdleAsync(() => h.Bus.RequestNetworkAction(
            new RepoNetworkRequest(RepoNetworkAction.DeleteRemoteBranch, "origin", null, "b")));

        Assert.Equal(1, h.Dialogs.ConfirmDestructiveCalls);
        Assert.False(fixture.RemoteHasBranch("b"));
        Assert.True(fixture.RemoteHasBranch("a"));
        Assert.True(fixture.RemoteHasBranch("main"));
        Assert.DoesNotContain(ctx.State.Branches, br => br.IsRemote && br.Name == "origin/b");
        var success = Assert.Single(h.Notify.Published, n => n.Severity == NotificationSeverity.Success);
        Assert.Equal("Deleted origin/b.", success.Message);
    }

    [AvaloniaFact]
    public async Task A_remote_branch_delete_without_a_target_never_falls_back()
    {
        using var fixture = new PushFixture().PushBranch("a").PushBranch("b").Checkout("a");
        await using var h = new Harness();
        h.Dialogs.ConfirmAnswer = true;
        await h.OpenAsync(fixture.LocalPath);

        // Refused before the operation starts, so there is no idle tick to wait for. Resolving the
        // runner subscribes it to the bus (RunToIdleAsync does this as a side effect).
        _ = h.Runner;
        h.Bus.RequestNetworkAction(
            new RepoNetworkRequest(RepoNetworkAction.DeleteRemoteBranch, "origin", null, RemoteBranch: null));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, h.Dialogs.ConfirmDestructiveCalls);
        var warning = Assert.Single(h.Notify.Published, n => n.Severity == NotificationSeverity.Warning);
        Assert.Equal("No remote branch to delete.", warning.Message);
        Assert.True(fixture.RemoteHasBranch("a"));
        Assert.True(fixture.RemoteHasBranch("b"));
    }

    [AvaloniaFact]
    public async Task An_already_deleted_remote_branch_drops_its_stale_row_and_keeps_a_same_named_tag()
    {
        using var fixture = new PushFixture().PushBranch("b").ReplaceRemoteBranchWithTag("b");
        await using var h = new Harness();
        h.Dialogs.ConfirmAnswer = true;
        var ctx = await h.OpenAsync(fixture.LocalPath);
        Assert.Contains(ctx.State.Branches, br => br.IsRemote && br.Name == "origin/b");

        await h.RunToIdleAsync(() => h.Bus.RequestNetworkAction(
            new RepoNetworkRequest(RepoNetworkAction.DeleteRemoteBranch, "origin", null, "b")));

        Assert.Equal(1, h.Dialogs.ConfirmDestructiveCalls);
        Assert.DoesNotContain(h.Notify.Published, n => n.Severity == NotificationSeverity.Error);
        Assert.True(fixture.RemoteHasTag("b"));
        Assert.DoesNotContain(ctx.State.Branches, br => br.IsRemote && br.Name == "origin/b");
    }

    [AvaloniaFact]
    public async Task A_tag_push_sends_only_that_tag()
    {
        using var fixture = new PushFixture().TagLocally("v1").TagLocally("v2");
        await using var h = new Harness();
        var ctx = await h.OpenAsync(fixture.LocalPath);

        await h.RunToIdleAsync(() => h.Bus.RequestNetworkAction(
            new RepoNetworkRequest(RepoNetworkAction.PushTag, "origin", null, Repo: ctx, Tag: "v1")));

        Assert.True(fixture.RemoteHasTag("v1"));
        Assert.False(fixture.RemoteHasTag("v2"));
        var success = Assert.Single(h.Notify.Published, n => n.Severity == NotificationSeverity.Success);
        Assert.Equal("Pushed tag v1 to origin.", success.Message);
    }

    [AvaloniaFact]
    public async Task A_tag_the_remote_already_has_is_refused_with_an_error_and_never_overwritten()
    {
        using var fixture = new PushFixture().MoveRemoteTag("v1");
        var remoteBefore = PushFixture.Rev(fixture.RemotePath, "v1");
        await using var h = new Harness();
        var ctx = await h.OpenAsync(fixture.LocalPath);

        await h.RunToIdleAsync(() => h.Bus.RequestNetworkAction(
            new RepoNetworkRequest(RepoNetworkAction.PushTag, "origin", null, Repo: ctx, Tag: "v1")));

        Assert.Equal(0, h.Dialogs.ChooseCalls);
        var error = Assert.Single(h.Notify.Published, n => n.Severity == NotificationSeverity.Error);
        Assert.Equal("Couldn't push tag v1: origin already has a tag named v1.", error.Message);
        Assert.Contains("(already exists)", error.Details);
        Assert.Equal(remoteBefore, PushFixture.Rev(fixture.RemotePath, "v1"));
    }

    [AvaloniaFact]
    public async Task A_tag_push_without_a_remote_never_falls_back()
    {
        using var fixture = new PushFixture().TagLocally("v1");
        await using var h = new Harness();
        await h.OpenAsync(fixture.LocalPath);

        _ = h.Runner;
        h.Bus.RequestNetworkAction(new RepoNetworkRequest(RepoNetworkAction.PushTag, null, null, Tag: "v1"));
        Dispatcher.UIThread.RunJobs();

        var warning = Assert.Single(h.Notify.Published, n => n.Severity == NotificationSeverity.Warning);
        Assert.Equal("No tag or remote to push to.", warning.Message);
        Assert.False(fixture.RemoteHasTag("v1"));
    }
}
