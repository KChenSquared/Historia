namespace HistoriaApp.Tests;

/// <summary>
/// The runner's merge-then-push of a diverged branch: <see cref="RepoNetworkAction.MergeAndPush"/>
/// asks once, checks a non-current branch out first, merges the remote branch (never rebasing)
/// and pushes; a conflict stops it before the push. A rejected push of a branch that is not
/// checked out offers "Check out, merge, then push" beside a force push that still asks its own
/// destructive question.
/// </summary>
[Trait("tier", "ui")]
public sealed class RepoNetworkMergeAndPushTests
{
    [AvaloniaFact]
    public async Task MergeAndPush_current_branch_merges_and_pushes()
    {
        using var fixture = new BranchSyncFixture().Diverge();
        await using var rig = new BranchSyncRig();
        rig.Dialogs.ConfirmAnswers.Enqueue(true);
        var repo = await rig.OpenAsync(fixture.LocalPath);

        await rig.RunToIdleAsync(() => rig.Bus.RequestNetworkAction(
            new RepoNetworkRequest(RepoNetworkAction.MergeAndPush, "origin", "main", "main", Repo: repo)));

        var confirm = Assert.Single(rig.Dialogs.Confirms);
        Assert.Contains("Merge origin/main into main, then push main to origin.", confirm.Message);
        Assert.DoesNotContain("checks out", confirm.Message);
        Assert.Equal(fixture.Rev(fixture.LocalPath, "main"), fixture.Rev(fixture.RemotePath, "main"));
        Assert.Equal(2, fixture.ParentCount("main"));
        Assert.Contains(rig.Notify.Published, n => n.Severity == NotificationSeverity.Success && n.Message == "Pushed to origin.");
        Assert.DoesNotContain(rig.Notify.Published, n => n.Severity == NotificationSeverity.Error);
    }

    [AvaloniaFact]
    public async Task MergeAndPush_non_current_branch_checks_out_first()
    {
        using var fixture = new BranchSyncFixture().Diverge().CheckoutNew("feature");
        await using var rig = new BranchSyncRig();
        rig.Dialogs.ConfirmAnswers.Enqueue(true);
        var repo = await rig.OpenAsync(fixture.LocalPath);

        await rig.RunToIdleAsync(() => rig.Bus.RequestNetworkAction(
            new RepoNetworkRequest(RepoNetworkAction.MergeAndPush, "origin", "main", "main", Repo: repo)));

        var confirm = Assert.Single(rig.Dialogs.Confirms);
        Assert.Contains("checks out main first", confirm.Message);
        Assert.Equal("main", fixture.CurrentBranch());
        Assert.Equal(fixture.Rev(fixture.LocalPath, "main"), fixture.Rev(fixture.RemotePath, "main"));
        Assert.Equal(2, fixture.ParentCount("main"));
    }

    [AvaloniaFact]
    public async Task MergeAndPush_declined_runs_nothing()
    {
        using var fixture = new BranchSyncFixture().Diverge().CheckoutNew("feature");
        await using var rig = new BranchSyncRig();
        rig.Dialogs.ConfirmAnswers.Enqueue(false);
        var repo = await rig.OpenAsync(fixture.LocalPath);
        var localBefore = fixture.Rev(fixture.LocalPath, "main");
        var remoteBefore = fixture.Rev(fixture.RemotePath, "main");

        rig.Bus.RequestNetworkAction(
            new RepoNetworkRequest(RepoNetworkAction.MergeAndPush, "origin", "main", "main", Repo: repo));
        await rig.WaitUntilAsync(() => rig.Dialogs.Confirms.Count == 1);
        await rig.PumpAsync(TimeSpan.FromMilliseconds(300));

        Assert.Equal("feature", fixture.CurrentBranch());
        Assert.Equal(localBefore, fixture.Rev(fixture.LocalPath, "main"));
        Assert.Equal(remoteBefore, fixture.Rev(fixture.RemotePath, "main"));
        Assert.Empty(rig.Notify.Published);
        Assert.Null(rig.Runner.CurrentProgress(repo));
    }

    [AvaloniaFact]
    public async Task MergeAndPush_conflict_stops_before_push_with_warning()
    {
        using var fixture = new BranchSyncFixture(conflicting: true).Diverge();
        await using var rig = new BranchSyncRig();
        rig.Dialogs.ConfirmAnswers.Enqueue(true);
        var repo = await rig.OpenAsync(fixture.LocalPath);
        var remoteBefore = fixture.Rev(fixture.RemotePath, "main");

        await rig.RunToIdleAsync(() => rig.Bus.RequestNetworkAction(
            new RepoNetworkRequest(RepoNetworkAction.MergeAndPush, "origin", "main", "main", Repo: repo)));

        Assert.Contains(rig.Notify.Published,
            n => n.Severity == NotificationSeverity.Warning && n.Message == "Resolve the conflicts, then push again.");
        Assert.DoesNotContain(rig.Notify.Published, n => n.Severity == NotificationSeverity.Success);
        Assert.Equal(remoteBefore, fixture.Rev(fixture.RemotePath, "main"));
    }

    [AvaloniaFact]
    public async Task Rejected_push_of_non_current_branch_offers_checkout_merge_push()
    {
        using var fixture = new BranchSyncFixture().Diverge().CheckoutNew("feature");
        await using var rig = new BranchSyncRig();
        rig.Dialogs.ChooseAnswers.Enqueue(0);
        var repo = await rig.OpenAsync(fixture.LocalPath);

        await rig.RunToIdleAsync(() => rig.Bus.RequestNetworkAction(
            new RepoNetworkRequest(RepoNetworkAction.Push, "origin", "main", Repo: repo)));

        var choose = Assert.Single(rig.Dialogs.Chooses);
        Assert.Equal(["Check out, merge, then push", "Force push (with lease)"], choose.Options);
        Assert.Equal(0, choose.DefaultIndex);
        Assert.Equal(1, choose.DestructiveIndex);
        Assert.Equal("main", fixture.CurrentBranch());
        Assert.Equal(fixture.Rev(fixture.LocalPath, "main"), fixture.Rev(fixture.RemotePath, "main"));
        Assert.Equal(2, fixture.ParentCount("main"));
        Assert.Equal(0, rig.Dialogs.ConfirmDestructiveCalls);
    }

    [AvaloniaFact]
    public async Task Force_choice_still_requires_destructive_confirm()
    {
        using var fixture = new BranchSyncFixture().Diverge().CheckoutNew("feature");
        await using var rig = new BranchSyncRig();
        rig.Dialogs.ChooseAnswers.Enqueue(1);
        rig.Dialogs.ConfirmDestructiveAnswers.Enqueue(false);
        var repo = await rig.OpenAsync(fixture.LocalPath);
        var remoteBefore = fixture.Rev(fixture.RemotePath, "main");

        await rig.RunToIdleAsync(() => rig.Bus.RequestNetworkAction(
            new RepoNetworkRequest(RepoNetworkAction.Push, "origin", "main", Repo: repo)));

        Assert.Equal(1, rig.Dialogs.ConfirmDestructiveCalls);
        Assert.Equal("feature", fixture.CurrentBranch());
        Assert.Equal(remoteBefore, fixture.Rev(fixture.RemotePath, "main"));
        Assert.DoesNotContain(rig.Notify.Published, n => n.Severity == NotificationSeverity.Success);
    }
}

/// <summary>A bare remote and a clone ("local") with <c>main</c> pushed and one more local commit
/// on it. <see cref="Diverge"/> pushes a commit to the remote's <c>main</c> from another clone, so
/// local <c>main</c> and <c>origin/main</c> have diverged; with <c>conflicting</c> both sides edit
/// the same line.</summary>
internal sealed class BranchSyncFixture : IDisposable
{
    private readonly string _root = TestScratch.NewDirectory("historia-branchsync-");

    public string RemotePath { get; }
    public string LocalPath { get; }

    public BranchSyncFixture(bool conflicting = false)
    {
        RemotePath = Path.Combine(_root, "remote.git");
        LocalPath = Path.Combine(_root, "local");
        Directory.CreateDirectory(RemotePath);
        Git(RemotePath, "init", "--bare", "-b", "main");
        Git(_root, "clone", RemotePath, LocalPath);
        Configure(LocalPath);
        File.WriteAllText(Path.Combine(LocalPath, "f.txt"), "init\n");
        Git(LocalPath, "add", "f.txt");
        Git(LocalPath, "commit", "-m", "init");
        Git(LocalPath, "branch", "-M", "main");
        Git(LocalPath, "push", "-u", "origin", "main");
        var local = conflicting ? "f.txt" : "g.txt";
        File.WriteAllText(Path.Combine(LocalPath, local), "local\n");
        Git(LocalPath, "add", local);
        Git(LocalPath, "commit", "-m", "local commit");
    }

    /// <summary>Pushes a commit on <c>main</c> from another clone and fetches it into the local
    /// clone, so <c>origin/main</c> is one commit the local <c>main</c> lacks.</summary>
    public BranchSyncFixture Diverge()
    {
        var other = Path.Combine(_root, "other");
        Git(_root, "clone", RemotePath, other);
        Configure(other);
        Git(other, "checkout", "main");
        File.WriteAllText(Path.Combine(other, "f.txt"), "other\n");
        Git(other, "add", "f.txt");
        Git(other, "commit", "-m", "other commit");
        Git(other, "push", "origin", "main");
        Git(LocalPath, "fetch", "origin");
        return this;
    }

    /// <summary>Moves the remote's <c>main</c> one commit ahead of the local <c>main</c> (which
    /// keeps no commit of its own) and fetches it.</summary>
    public BranchSyncFixture AdvanceRemoteOnly()
    {
        Git(LocalPath, "reset", "--hard", "origin/main");
        return Diverge();
    }

    public BranchSyncFixture CheckoutNew(string branch)
    {
        Git(LocalPath, "checkout", "-b", branch);
        return this;
    }

    public string Rev(string dir, string rev) =>
        TestGit.Run(dir, ["rev-parse", rev], testIdentity: false).Stdout.Trim();

    public string CurrentBranch() =>
        TestGit.Run(LocalPath, ["symbolic-ref", "--short", "HEAD"], testIdentity: false).Stdout.Trim();

    public int ParentCount(string rev) =>
        TestGit.Run(LocalPath, ["rev-list", "--parents", "-n", "1", rev], testIdentity: false)
            .Stdout.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length - 1;

    private static void Configure(string dir)
    {
        Git(dir, "config", "user.name", "Test");
        Git(dir, "config", "user.email", "test@example.com");
        Git(dir, "config", "commit.gpgsign", "false");
    }

    private static void Git(string dir, params string[] args) => TestGit.Run(dir, args);

    public void Dispose() => TestScratch.Delete(_root);
}

/// <summary>The real runner over a real workspace, with captured notifications and a dialog
/// service that answers from queues (Cancel / No when a queue is empty) and records every
/// question.</summary>
internal sealed class BranchSyncRig : IAsyncDisposable
{
    private readonly List<RepositoryContext> _opened = [];

    public CapturingNotifications Notify { get; } = new();
    public ScriptedDialogs Dialogs { get; } = new();
    public ServiceProvider Services { get; }
    public IWorkspaceService Workspace => Services.GetRequiredService<IWorkspaceService>();
    public IRepoActionBus Bus => Services.GetRequiredService<IRepoActionBus>();
    public IRepoNetworkActionRunner Runner => Services.GetRequiredService<IRepoNetworkActionRunner>();

    public BranchSyncRig()
    {
        Services = CreateServices(Notify, Dialogs);
        // The runner subscribes to the bus when it is built.
        _ = Runner;
    }

    public async Task<RepositoryContext> OpenAsync(string path)
    {
        var ctx = await Workspace.OpenAsync(path);
        _opened.Add(ctx);
        ctx.MarkFirstGraphPaintDone();
        ctx.MarkFirstFullLoadDone();
        await ctx.State.RefreshAllAsync();
        Workspace.Focus(ctx);
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
            await WaitUntilAsync(() => final is not null);
        }
        finally
        {
            Runner.ProgressChanged -= OnProgress;
        }
    }

    public async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 1000 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
        Assert.True(condition());
    }

    public async Task PumpAsync(TimeSpan duration)
    {
        var end = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < end)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var ctx in _opened)
            await Workspace.CloseAsync(ctx);
        await Services.DisposeAsync();
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

    internal sealed class CapturingNotifications : INotificationService
    {
        public List<Notification> Published { get; } = [];
        public IReadOnlyList<Notification> Current => Published;

        public Notification Publish(NotificationSeverity severity, string message, string? details = null,
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

    internal sealed record ChooseCall(string Title, string Message, IReadOnlyList<string> Options, int DefaultIndex, int DestructiveIndex);

    internal sealed record ConfirmCall(string Title, string Message, string ConfirmLabel);

    internal sealed class ScriptedDialogs : IDialogService
    {
        public Queue<int> ChooseAnswers { get; } = new();
        public Queue<bool> ConfirmAnswers { get; } = new();
        public Queue<bool> ConfirmDestructiveAnswers { get; } = new();
        public List<ChooseCall> Chooses { get; } = [];
        public List<ConfirmCall> Confirms { get; } = [];
        public int ConfirmDestructiveCalls { get; private set; }

        public Task<int> ChooseAsync(string title, string message, IReadOnlyList<string> options,
            int defaultIndex = 0, int destructiveIndex = -1, DialogIcon icon = DialogIcon.None)
        {
            Chooses.Add(new ChooseCall(title, message, options, defaultIndex, destructiveIndex));
            return Task.FromResult(ChooseAnswers.TryDequeue(out var answer) ? answer : -1);
        }

        public Task<bool> ConfirmAsync(string title, string message, bool destructive = false,
            string confirmLabel = "OK", bool defaultToCancel = false, DialogIcon icon = DialogIcon.None)
        {
            Confirms.Add(new ConfirmCall(title, message, confirmLabel));
            return Task.FromResult(ConfirmAnswers.TryDequeue(out var answer) && answer);
        }

        public Task<bool> ConfirmDestructiveAsync(string title, string effect, string confirmLabel)
        {
            ConfirmDestructiveCalls++;
            return Task.FromResult(ConfirmDestructiveAnswers.TryDequeue(out var answer) && answer);
        }

        public Task<string?> PickFolderAsync(string title, string? initialDirectory = null) => throw new NotSupportedException();
        public Task<string?> PickFileAsync(string title, string filter, string? initialDirectory = null) => throw new NotSupportedException();
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
}
