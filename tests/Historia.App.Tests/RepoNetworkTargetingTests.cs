namespace HistoriaApp.Tests;

/// <summary>Pure helpers for network action targeting — refspec construction and upstream checking.</summary>
[Trait("tier", "unit")]
public sealed class RepoNetworkTargetingTests
{
    [Fact]
    public void BuildPushSpec_uses_colon_notation_when_names_differ()
    {
        var spec = RepoNetworkActionRunner.BuildPushSpec("feature", "feat-x", targetHasUpstream: true);
        Assert.Equal("feature:feat-x", spec.RefSpec);
        Assert.False(spec.SetUpstream);
    }

    [Fact]
    public void BuildPushSpec_uses_single_branch_name_when_names_equal_or_remote_null()
    {
        var specEqual = RepoNetworkActionRunner.BuildPushSpec("feature", "feature", targetHasUpstream: true);
        Assert.Equal("feature", specEqual.RefSpec);

        var specNull = RepoNetworkActionRunner.BuildPushSpec("feature", null, targetHasUpstream: true);
        Assert.Equal("feature", specNull.RefSpec);
    }

    [Fact]
    public void BuildPushSpec_requests_upstream_when_target_lacks_one()
    {
        var spec = RepoNetworkActionRunner.BuildPushSpec("feature", "feat-x", targetHasUpstream: false);
        Assert.Equal("feature:feat-x", spec.RefSpec);
        Assert.True(spec.SetUpstream);
    }

    [Fact]
    public void SetUpstream_follows_the_target_not_the_current_branch()
    {
        BranchInfo[] branches =
        [
            new()
            {
                Name = "main",
                CanonicalName = "refs/heads/main",
                IsRemote = false,
                IsCurrent = true,
                UpstreamName = null,
            },
            new()
            {
                Name = "feature",
                CanonicalName = "refs/heads/feature",
                IsRemote = false,
                IsCurrent = false,
                UpstreamName = "origin/feat-x",
            },
        ];

        // Current 'main' has no upstream, but target 'feature' does:
        var targetHasUpstream = RepoNetworkActionRunner.TargetHasUpstream(branches, "feature");
        Assert.True(targetHasUpstream);

        var spec = RepoNetworkActionRunner.BuildPushSpec("feature", "feat-x", targetHasUpstream);
        Assert.False(spec.SetUpstream);

        // Target 'main' has no upstream:
        var mainHasUpstream = RepoNetworkActionRunner.TargetHasUpstream(branches, "main");
        Assert.False(mainHasUpstream);

        var mainSpec = RepoNetworkActionRunner.BuildPushSpec("main", null, mainHasUpstream);
        Assert.True(mainSpec.SetUpstream);
    }

    [Fact]
    public void TargetHasUpstream_returns_false_for_an_unknown_branch()
    {
        BranchInfo[] branches =
        [
            new()
            {
                Name = "main",
                CanonicalName = "refs/heads/main",
                IsRemote = false,
                IsCurrent = true,
                UpstreamName = "origin/main",
            },
        ];

        Assert.False(RepoNetworkActionRunner.TargetHasUpstream(branches, "nonexistent"));
    }

    [Fact]
    public void PruneEffect_names_the_remote_and_scope()
    {
        var text = RepoNetworkActionRunner.PruneEffect("origin");

        Assert.Contains("'origin/'", text);
        Assert.Contains("on origin", text);
        Assert.Contains("local branches are not touched", text);
        Assert.DoesNotContain("Are you sure", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TargetHasUpstream_ignores_remote_branches()
    {
        BranchInfo[] branches =
        [
            new()
            {
                Name = "origin/feature",
                CanonicalName = "refs/remotes/origin/feature",
                IsRemote = true,
                IsCurrent = false,
                UpstreamName = "origin/feature",
            },
        ];

        Assert.False(RepoNetworkActionRunner.TargetHasUpstream(branches, "origin/feature"));
    }

    [Fact]
    public void RemoteBranchDeleteEffect_names_ref_and_keeps_local()
    {
        var text = RepoNetworkActionRunner.RemoteBranchDeleteEffect("origin", "feat");

        Assert.Contains("origin/feat", text);
        Assert.Contains("on origin", text);
        Assert.Contains("local branch", text);
        Assert.Contains("kept", text);
        Assert.DoesNotContain("Are you sure", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OpLabel_is_readable_for_delete_and_unchanged_for_others()
    {
        Assert.Equal("Delete remote branch", RepoNetworkActionRunner.OpLabel(RepoNetworkAction.DeleteRemoteBranch));
        Assert.Equal("Fetch", RepoNetworkActionRunner.OpLabel(RepoNetworkAction.Fetch));
        Assert.Equal("Pull", RepoNetworkActionRunner.OpLabel(RepoNetworkAction.Pull));
        Assert.Equal("Push", RepoNetworkActionRunner.OpLabel(RepoNetworkAction.Push));
        Assert.Equal("Prune", RepoNetworkActionRunner.OpLabel(RepoNetworkAction.Prune));
    }

    private sealed class ManualTime : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }

    private sealed class ManualTimer(Action tick) : IUiTimer
    {
        public bool Running { get; private set; }
        public bool Disposed { get; private set; }
        public void Start() => Running = true;
        public void Stop() => Running = false;
        public void Dispose() { Running = false; Disposed = true; }

        /// <summary>Ticks once if the timer is running, the way the UI timer does.</summary>
        public void FireIfRunning()
        {
            if (Running)
                tick();
        }
    }

    private sealed class ManualDispatcher : IUiDispatcher
    {
        public ManualTimer? Timer { get; private set; }

        /// <summary>False models a reader thread: posts queue until <see cref="RunPosted"/>.</summary>
        public bool OnUiThread { get; set; } = true;

        public List<(Action Action, UiPriority Priority)> Posted { get; } = [];

        public bool CheckAccess() => OnUiThread;

        public void Post(Action action) => Post(action, UiPriority.Normal);

        public void Post(Action action, UiPriority priority)
        {
            if (OnUiThread)
                action();
            else
                Posted.Add((action, priority));
        }

        public void RunPosted()
        {
            var queued = Posted.ToArray();
            Posted.Clear();
            foreach (var (action, _) in queued)
                action();
        }

        public IUiTimer CreateTimer(TimeSpan interval, Action tick) => Timer = new ManualTimer(tick);
    }

    private sealed class ThrottleRig
    {
        public ManualTime Time { get; } = new();
        public ManualDispatcher Ui { get; } = new();
        public List<GitProgress?> Delivered { get; } = [];
        public RepoNetworkActionRunner.ProgressThrottle Throttle { get; }

        public ThrottleRig() => Throttle = new(Time, Ui, Delivered.Add);

        public ManualTimer Timer => Ui.Timer!;

        public static GitProgress Receiving(int done) => new() { Phase = "Receiving objects", ObjectsDone = done };
    }

    [Fact]
    public void Throttle_delivers_the_first_report_at_once()
    {
        var rig = new ThrottleRig();

        rig.Throttle.Offer(ThrottleRig.Receiving(1));

        Assert.Equal(1, Assert.Single(rig.Delivered)!.ObjectsDone);
        Assert.False(rig.Timer.Running);
    }

    [Fact]
    public void Throttle_holds_reports_inside_the_interval_and_delivers_only_the_latest_when_it_ends()
    {
        var rig = new ThrottleRig();
        rig.Throttle.Offer(ThrottleRig.Receiving(1));

        rig.Time.Advance(TimeSpan.FromMilliseconds(10));
        rig.Throttle.Offer(ThrottleRig.Receiving(2));
        rig.Time.Advance(TimeSpan.FromMilliseconds(10));
        rig.Throttle.Offer(ThrottleRig.Receiving(3));

        Assert.Single(rig.Delivered);
        Assert.True(rig.Timer.Running);

        rig.Time.Advance(RepoNetworkActionRunner.ProgressThrottle.MinInterval);
        rig.Timer.FireIfRunning();

        Assert.Equal([1L, 3L], rig.Delivered.Select(p => p!.ObjectsDone!.Value));
        Assert.False(rig.Timer.Running);
    }

    [Fact]
    public void Throttle_delivers_a_report_after_the_interval_at_once()
    {
        var rig = new ThrottleRig();
        rig.Throttle.Offer(ThrottleRig.Receiving(1));

        rig.Time.Advance(RepoNetworkActionRunner.ProgressThrottle.MinInterval);
        rig.Throttle.Offer(ThrottleRig.Receiving(2));

        Assert.Equal([1L, 2L], rig.Delivered.Select(p => p!.ObjectsDone!.Value));
    }

    [Fact]
    public void Throttle_delivers_a_phase_change_at_once_and_drops_the_pending_value()
    {
        var rig = new ThrottleRig();
        rig.Throttle.Offer(ThrottleRig.Receiving(1));
        rig.Time.Advance(TimeSpan.FromMilliseconds(5));
        rig.Throttle.Offer(ThrottleRig.Receiving(2));

        rig.Time.Advance(TimeSpan.FromMilliseconds(5));
        rig.Throttle.Offer(new GitProgress { Phase = "Resolving deltas" });

        Assert.Equal(["Receiving objects", "Resolving deltas"], rig.Delivered.Select(p => p!.Phase));
        Assert.False(rig.Timer.Running);

        rig.Time.Advance(RepoNetworkActionRunner.ProgressThrottle.MinInterval);
        rig.Timer.FireIfRunning();
        Assert.Equal(2, rig.Delivered.Count);
    }

    [Fact]
    public void Throttle_delivers_at_most_ten_reports_per_second()
    {
        var rig = new ThrottleRig();
        var timerStartedAt = TimeSpan.Zero;
        var elapsed = TimeSpan.Zero;
        var deliveredAt = new List<TimeSpan>();
        var step = TimeSpan.FromMilliseconds(5);
        var seen = 0;

        for (var i = 0; i < 400; i++)
        {
            var wasRunning = rig.Ui.Timer?.Running == true;
            rig.Throttle.Offer(ThrottleRig.Receiving(i));
            if (rig.Ui.Timer!.Running && !wasRunning)
                timerStartedAt = elapsed;
            while (seen < rig.Delivered.Count)
            {
                deliveredAt.Add(elapsed);
                seen++;
            }

            rig.Time.Advance(step);
            elapsed += step;
            if (rig.Timer.Running && elapsed - timerStartedAt >= RepoNetworkActionRunner.ProgressThrottle.MinInterval)
                rig.Timer.FireIfRunning();
            while (seen < rig.Delivered.Count)
            {
                deliveredAt.Add(elapsed);
                seen++;
            }
        }

        Assert.InRange(deliveredAt.Count, 15, 21); // 2 s of reports
        for (var i = 1; i < deliveredAt.Count; i++)
            Assert.True(deliveredAt[i] - deliveredAt[i - 1] >= RepoNetworkActionRunner.ProgressThrottle.MinInterval,
                $"Deliveries {i - 1} and {i} were {(deliveredAt[i] - deliveredAt[i - 1]).TotalMilliseconds} ms apart.");
    }

    [Fact]
    public void Throttle_posts_at_most_ten_deliveries_per_second_when_offered_off_the_ui_thread()
    {
        var rig = new ThrottleRig();
        rig.Ui.OnUiThread = false;
        var timerStartedAt = TimeSpan.Zero;
        var elapsed = TimeSpan.Zero;
        var step = TimeSpan.FromMilliseconds(5);

        for (var i = 0; i < 400; i++)
        {
            var wasRunning = rig.Ui.Timer?.Running == true;
            rig.Throttle.Offer(ThrottleRig.Receiving(i));
            if (rig.Ui.Timer!.Running && !wasRunning)
                timerStartedAt = elapsed;

            rig.Time.Advance(step);
            elapsed += step;
            if (rig.Timer.Running && elapsed - timerStartedAt >= RepoNetworkActionRunner.ProgressThrottle.MinInterval)
            {
                // The timer ticks on the UI thread.
                rig.Ui.OnUiThread = true;
                rig.Timer.FireIfRunning();
                rig.Ui.OnUiThread = false;
            }
        }

        Assert.InRange(rig.Ui.Posted.Count + rig.Delivered.Count, 15, 21); // 2 s of reports
        Assert.All(rig.Ui.Posted, p => Assert.Equal(UiPriority.Background, p.Priority));
        Assert.Empty(rig.Delivered);

        rig.Ui.RunPosted();
        Assert.InRange(rig.Delivered.Count, 15, 21);
    }

    [Fact]
    public void Throttle_delivers_the_end_last_when_a_report_is_still_queued_off_the_ui_thread()
    {
        var rig = new ThrottleRig();
        rig.Ui.OnUiThread = false;

        rig.Throttle.Offer(ThrottleRig.Receiving(1));
        rig.Throttle.Complete();
        rig.Ui.RunPosted();

        Assert.Single(rig.Delivered);
        Assert.Null(rig.Delivered[0]);
    }

    [Fact]
    public void Throttle_drops_an_offer_that_arrives_after_the_end_off_the_ui_thread()
    {
        var rig = new ThrottleRig();
        rig.Ui.OnUiThread = false;

        rig.Throttle.Offer(ThrottleRig.Receiving(1));
        rig.Ui.RunPosted();
        rig.Throttle.Complete();
        rig.Throttle.Offer(ThrottleRig.Receiving(2));
        rig.Ui.RunPosted();

        Assert.Equal(2, rig.Delivered.Count);
        Assert.Equal(1, rig.Delivered[0]!.ObjectsDone);
        Assert.Null(rig.Delivered[^1]);
    }

    [Fact]
    public void Throttle_complete_delivers_the_end_at_once_and_discards_what_was_pending()
    {
        var rig = new ThrottleRig();
        rig.Throttle.Offer(ThrottleRig.Receiving(1));
        rig.Time.Advance(TimeSpan.FromMilliseconds(5));
        rig.Throttle.Offer(ThrottleRig.Receiving(2));

        rig.Throttle.Complete();
        rig.Timer.FireIfRunning();
        rig.Time.Advance(RepoNetworkActionRunner.ProgressThrottle.MinInterval);
        rig.Throttle.Offer(ThrottleRig.Receiving(3));

        Assert.Equal(2, rig.Delivered.Count);
        Assert.Null(rig.Delivered[^1]);
        Assert.True(rig.Timer.Disposed);
    }
}

/// <summary>What the user-started network runner runs and when: no follow-up fetch after a push, one
/// after a pull only while the setting is on, and the repository refresh after the gate is free. Uses a
/// local bare repository as the remote and records every git invocation.</summary>
[Trait("tier", "ui")]
public sealed class RepoNetworkOperationTests
{
    /// <summary>Delegates to the real CLI and records each invocation's verb together with whether
    /// the repository's network gate was free when it started.</summary>
    private sealed class RecordingCli(IGitCliService inner) : IGitCliService
    {
        private static readonly string[] NetworkVerbs = ["fetch", "pull", "push"];

        public Func<bool> GateIsFree { get; set; } = () => true;

        /// <summary>Awaited before each invocation starts, with its verb and whether the gate was free.</summary>
        public Func<string, bool, Task>? BeforeCall { get; set; }
        public List<(string Verb, bool GateFree)> Calls { get; } = [];

        public bool IsGitAvailable => inner.IsGitAvailable;

        public IEnumerable<string> NetworkCalls => Calls.Select(c => c.Verb).Where(v => NetworkVerbs.Contains(v));

        public async Task<GitCliResult> RunAsync(GitCliRequest request, CancellationToken ct = default)
        {
            await RecordAsync(request);
            return await inner.RunAsync(request, ct);
        }

        public async Task<GitCliResult> RunAsync(GitCliRequest request, IProgress<GitProgress>? progress, Action<string>? onStdoutLine, CancellationToken ct = default)
        {
            await RecordAsync(request);
            return await inner.RunAsync(request, progress, onStdoutLine, ct);
        }

        private async Task RecordAsync(GitCliRequest request)
        {
            var gateFree = GateIsFree();
            var verb = VerbOf(request.Arguments);
            lock (Calls)
                Calls.Add((verb, gateFree));
            if (BeforeCall is { } hook)
                await hook(verb, gateFree);
        }

        private static string VerbOf(IReadOnlyList<string> args)
        {
            for (var i = 0; i < args.Count; i++)
            {
                if (args[i] == "-c")
                    i++;
                else if (!args[i].StartsWith('-'))
                    return args[i];
            }
            return "";
        }
    }

    private sealed class SilentNotifications : INotificationService
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

    /// <summary>A bare remote, a clone ("local") with <c>main</c> pushed and tracking, and a second
    /// clone ("other") used to put a commit on the remote that local has not fetched.</summary>
    private sealed class Fixture : IDisposable
    {
        private readonly string _baseDir = TestScratch.NewDirectory("historia-netops-");
        public string RemotePath { get; }
        public string LocalPath { get; }
        public string OtherPath { get; }

        public Fixture()
        {
            RemotePath = Path.Combine(_baseDir, "remote.git");
            LocalPath = Path.Combine(_baseDir, "local");
            OtherPath = Path.Combine(_baseDir, "other");

            Directory.CreateDirectory(RemotePath);
            Git(RemotePath, "init", "--bare");
            Git(_baseDir, "clone", RemotePath, LocalPath);
            Commit(LocalPath, "f.txt", "init\n", "init");
            Git(LocalPath, "branch", "-M", "main");
            Git(LocalPath, "push", "-u", "origin", "main");
            Git(_baseDir, "clone", RemotePath, OtherPath);
            Git(OtherPath, "checkout", "main");
        }

        public static void Commit(string dir, string file, string content, string message)
        {
            Git(dir, "config", "user.name", "Test");
            Git(dir, "config", "user.email", "test@example.com");
            Git(dir, "config", "commit.gpgsign", "false");
            File.WriteAllText(Path.Combine(dir, file), content);
            Git(dir, "add", file);
            Git(dir, "commit", "-m", message, "--no-gpg-sign");
        }

        private static void Git(string dir, params string[] args) => TestGit.Run(dir, args);

        public void Dispose() => TestScratch.Delete(_baseDir);
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly ServiceProvider _services;

        public RecordingCli Cli { get; }
        public InMemorySettingsService Settings { get; }
        public SilentNotifications Notifications { get; } = new();
        public IWorkspaceService Workspace { get; }
        public IRepoActionBus Bus { get; }
        public IRepoNetworkActionRunner Runner { get; }
        public RepositoryContext Context { get; private set; } = null!;

        /// <summary>True once the runner has announced the end of the operation started last.</summary>
        public bool Done { get; private set; }

        public Rig(AppSettings settings)
        {
            Settings = new InMemorySettingsService(settings);
            var locator = new GitExecutableLocator();
            locator.ResolveAsync(null).GetAwaiter().GetResult();
            Cli = new RecordingCli(new GitCliService(locator));

            var sc = new ServiceCollection();
            sc.AddSingleton<ISettingsService>(Settings);
            sc.AddSingleton<IUiDispatcher, Historia.Platform.UiDispatcher>();
            sc.AddSingleton<IDialogService>(new ScriptedDialogs());
            sc.AddSingleton<IScrollDirectionProbe, Historia.Platform.ScrollDirectionProbe>();
            sc.AddSingleton<IRepoActionBus, RepoActionBus>();
            sc.AddSingleton<IRepoNetworkActionRunner, RepoNetworkActionRunner>();
            sc.AddSingleton<IAutoFetchHost, AutoFetchHost>();
            sc.AddSingleton<IAutoFetchService, AutoFetchService>();
            sc.AddSingleton<INavigationBus, NavigationBus>();
            sc.AddSingleton<IKeyboardCommandRouter, KeyboardCommandRouter>();
            sc.AddSingleton<INotificationService>(Notifications);
            sc.AddSingleton<IRepoLayoutService, RepoLayoutService>();
            sc.AddSingleton<ISystemThemeProbe, FakeThemeProbe>();
            sc.AddSingleton<IThemeService, ThemeService>();
            sc.AddSingleton<ICustomThemeService, CustomThemeService>();
            sc.AddSingleton<IWorkspaceService, WorkspaceService>();
            sc.AddSingleton<IProfileService, ProfileService>();
            sc.AddSingleton<IGitCliService>(Cli);
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
            _services = sc.BuildServiceProvider();

            Workspace = _services.GetRequiredService<IWorkspaceService>();
            Bus = _services.GetRequiredService<IRepoActionBus>();
            Runner = _services.GetRequiredService<IRepoNetworkActionRunner>();
            Runner.ProgressChanged += (_, p) =>
            {
                if (p.Progress is null)
                    Done = true;
            };
        }

        public async Task OpenAsync(string path)
        {
            Context = await Workspace.OpenAsync(path);
            Context.MarkFirstGraphPaintDone();
            Context.MarkFirstFullLoadDone();
            await Context.State.RefreshRefsAsync();
            Workspace.Focus(Context);
            Cli.GateIsFree = () => Context.CurrentNetworkOperation is null;
        }

        /// <summary>Requests <paramref name="action"/> without waiting for it. The end-of-operation
        /// flag is cleared first: an operation that ends before its first real await raises the end
        /// tick synchronously inside the request.</summary>
        public void Begin(RepoNetworkAction action)
        {
            Done = false;
            Bus.RequestNetworkAction(action);
        }

        public async Task RunAsync(RepoNetworkAction action)
        {
            Begin(action);
            await WaitUntilAsync(() => Done);
        }

        public static async Task WaitUntilAsync(Func<bool> condition)
        {
            for (var i = 0; i < 500 && !condition(); i++)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(20);
            }
            Assert.True(condition(), "The condition was not reached in time.");
        }

        public async ValueTask DisposeAsync()
        {
            if (Context is not null)
                await Workspace.CloseAsync(Context);
            await _services.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task A_push_runs_one_network_command_and_no_follow_up_fetch()
    {
        using var fixture = new Fixture();
        Fixture.Commit(fixture.LocalPath, "g.txt", "local\n", "local commit");
        await using var rig = new Rig(new AppSettings());
        await rig.OpenAsync(fixture.LocalPath);

        await rig.RunAsync(RepoNetworkAction.Push);

        Assert.Equal(["push"], rig.Cli.NetworkCalls);
        Assert.DoesNotContain(rig.Notifications.Published, n => n.Severity is NotificationSeverity.Error or NotificationSeverity.Warning);
    }

    [AvaloniaFact]
    public async Task A_pull_runs_a_follow_up_fetch_while_fetch_after_pull_is_on()
    {
        using var fixture = new Fixture();
        Fixture.Commit(fixture.OtherPath, "o.txt", "other\n", "other commit");
        TestGit.Run(fixture.OtherPath, ["push", "origin", "main"]);
        await using var rig = new Rig(new AppSettings { FetchAfterPull = true });
        await rig.OpenAsync(fixture.LocalPath);

        await rig.RunAsync(RepoNetworkAction.Pull);

        Assert.Equal(["pull", "fetch"], rig.Cli.NetworkCalls);
    }

    [AvaloniaFact]
    public async Task A_pull_runs_no_follow_up_fetch_while_fetch_after_pull_is_off()
    {
        using var fixture = new Fixture();
        Fixture.Commit(fixture.OtherPath, "o.txt", "other\n", "other commit");
        TestGit.Run(fixture.OtherPath, ["push", "origin", "main"]);
        await using var rig = new Rig(new AppSettings { FetchAfterPull = false });
        await rig.OpenAsync(fixture.LocalPath);

        await rig.RunAsync(RepoNetworkAction.Pull);

        Assert.Equal(["pull"], rig.Cli.NetworkCalls);
    }

    [AvaloniaFact]
    public async Task The_refresh_after_a_push_runs_once_the_network_gate_is_free()
    {
        using var fixture = new Fixture();
        Fixture.Commit(fixture.LocalPath, "g.txt", "local\n", "local commit");
        await using var rig = new Rig(new AppSettings());
        await rig.OpenAsync(fixture.LocalPath);

        // Holds the first git call made after the push while the gate is free: the repository
        // refresh, still part of the operation, is then in flight with the gate released.
        var pushed = false;
        var inFlight = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        rig.Cli.BeforeCall = async (verb, gateFree) =>
        {
            if (verb == "push")
                pushed = true;
            else if (pushed && gateFree && inFlight.TrySetResult())
                await release.Task;
        };

        rig.Begin(RepoNetworkAction.Push);
        await Rig.WaitUntilAsync(() => inFlight.Task.IsCompleted);

        Assert.False(rig.Done);
        Assert.True(rig.Context.TryBeginNetworkOperation(new NetworkOperationInfo(NetworkOperationOwner.AutoFetch, "test"), out var lease));
        lease!.Dispose();

        release.SetResult();
        await Rig.WaitUntilAsync(() => rig.Done);
    }

    [AvaloniaFact]
    public async Task The_refresh_after_a_tag_conflict_runs_once_the_network_gate_is_free()
    {
        using var fixture = new Fixture();
        TestGit.Run(fixture.LocalPath, ["tag", "v1"]);
        Fixture.Commit(fixture.OtherPath, "o.txt", "other\n", "other commit");
        TestGit.Run(fixture.OtherPath, ["tag", "v1"]);
        TestGit.Run(fixture.OtherPath, ["push", "origin", "main", "v1"]);
        await using var rig = new Rig(new AppSettings());
        await rig.OpenAsync(fixture.LocalPath);

        var fetched = false;
        var inFlight = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        rig.Cli.BeforeCall = async (verb, gateFree) =>
        {
            if (verb == "fetch")
                fetched = true;
            else if (fetched && gateFree && inFlight.TrySetResult())
                await release.Task;
        };

        rig.Begin(RepoNetworkAction.Fetch);
        await Rig.WaitUntilAsync(() => inFlight.Task.IsCompleted);

        Assert.False(rig.Done);
        Assert.Contains(rig.Notifications.Published, n => n.Severity == NotificationSeverity.Warning);
        Assert.True(rig.Context.TryBeginNetworkOperation(new NetworkOperationInfo(NetworkOperationOwner.AutoFetch, "test"), out var lease));
        lease!.Dispose();

        release.SetResult();
        await Rig.WaitUntilAsync(() => rig.Done);
    }

    [AvaloniaFact]
    public async Task The_refresh_after_a_push_recovery_runs_once_the_network_gate_is_free()
    {
        using var fixture = new Fixture();
        Fixture.Commit(fixture.OtherPath, "o.txt", "other\n", "other commit");
        TestGit.Run(fixture.OtherPath, ["push", "origin", "main"]);
        Fixture.Commit(fixture.LocalPath, "g.txt", "local\n", "local commit");
        await using var rig = new Rig(new AppSettings { FetchAfterPull = false });
        await rig.OpenAsync(fixture.LocalPath);

        // The rejected push, then the recovery's pull and second push, all hold the gate; the first
        // call after that second push is the refresh.
        var pushes = 0;
        var inFlight = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        rig.Cli.BeforeCall = async (verb, gateFree) =>
        {
            if (verb == "push")
                pushes++;
            else if (pushes >= 2 && gateFree && inFlight.TrySetResult())
                await release.Task;
        };

        rig.Begin(RepoNetworkAction.Push);
        await Rig.WaitUntilAsync(() => inFlight.Task.IsCompleted);

        Assert.False(rig.Done);
        Assert.Equal(["push", "pull", "push"], rig.Cli.NetworkCalls);
        Assert.True(rig.Context.TryBeginNetworkOperation(new NetworkOperationInfo(NetworkOperationOwner.AutoFetch, "test"), out var lease));
        lease!.Dispose();

        release.SetResult();
        await Rig.WaitUntilAsync(() => rig.Done);
    }
}
