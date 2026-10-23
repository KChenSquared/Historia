namespace HistoriaApp.Tests;

[Trait("tier", "ui")]
public sealed class ExternalControlHostTests : IAsyncLifetime
{
    private readonly List<string> _dirs = [];
    private readonly SettingsService _settings = SettingsService.Load();
    private readonly InlineUi _ui = new();
    private ServiceProvider _sp = null!;
    private WorkspaceService _workspace = null!;
    private TrackingCli _cli = null!;
    private ControlNavigator _navigator = null!;
    private CmdrQueueService _queue = null!;
    private ExternalControlHost _host = null!;

    public Task InitializeAsync()
    {
        var sc = new ServiceCollection();
        sc.AddSingleton<ISettingsService>(_settings);
        sc.AddSingleton<IRepoLayoutService>(new FakeLayoutService());
        sc.AddSingleton<ICredentialVaultService>(new FakeVault());
        var locator = new GitExecutableLocator();
        locator.ResolveAsync(null).GetAwaiter().GetResult();
        sc.AddSingleton<IGitExecutableLocator>(locator);
        sc.AddSingleton<IGitCliService>(sp => _cli = new TrackingCli(new GitCliService(locator)));
        _sp = sc.BuildServiceProvider();

        var cli = _sp.GetRequiredService<IGitCliService>();
        _workspace = new WorkspaceService(
            _sp, cli, new RecentRepositoryService(_settings),
            new ProfileService(_settings, new FakeVault(), cli), new RepoIdentityService(cli));
        _navigator = new ControlNavigator(_workspace, _settings, cli, _ui, new ControlNavigatorOptions
        {
            GraphPaintWait = TimeSpan.FromMilliseconds(30),
            DraftSettleTime = TimeSpan.FromMilliseconds(60),
        });
        var queueDir = TestScratch.NewDirectory("historia-ctl-queue-");
        _dirs.Add(queueDir);
        _queue = new CmdrQueueService(_settings, _ui, Historia.Diagnostics.AppLog.Current,
            filePath: System.IO.Path.Combine(queueDir, "cmdr-queue.json"));
        _host = NewHost();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var ctx in _workspace.OpenRepositories.ToList())
        {
            try { await _workspace.CloseAsync(ctx); } catch { }
        }
        try { await _workspace.DisposeAsync(); } catch { }
        try { await _sp.DisposeAsync(); } catch { }
        try { await _queue.DisposeAsync(); } catch { }
        try { await _settings.DisposeAsync(); } catch { }
        foreach (var d in _dirs)
            TestScratch.Delete(d);
    }

    private ExternalControlHost NewHost(Action? activate = null) => new(
        _workspace, _settings, _sp.GetRequiredService<IGitCliService>(), _ui, Historia.Diagnostics.AppLog.Current,
        _navigator, _queue, activate ?? (() => { }));

    // ── Fixtures ────────────────────────────────────────────────────────────────────────

    private sealed record Fixture(string Path, string First, string Second);

    private Fixture NewRepo()
    {
        var dir = TestScratch.NewDirectory("historia-ctl-");
        _dirs.Add(dir);
        LibGit2Sharp.Repository.Init(dir);

        using var repo = new LibGit2Sharp.Repository(dir);
        var sig = new LibGit2Sharp.Signature("Test", "test@example.com", DateTimeOffset.Now);
        File.WriteAllText(System.IO.Path.Combine(dir, "a.txt"), "one\ntwo\nthree\n");
        File.WriteAllText(System.IO.Path.Combine(dir, "b.txt"), "b\n");
        LibGit2Sharp.Commands.Stage(repo, "*");
        var first = repo.Commit("first", sig, sig, new LibGit2Sharp.CommitOptions());
        File.WriteAllText(System.IO.Path.Combine(dir, "a.txt"), "one\ntwo changed\nthree\n");
        LibGit2Sharp.Commands.Stage(repo, "a.txt");
        var second = repo.Commit("second", sig, sig, new LibGit2Sharp.CommitOptions());
        return new Fixture(dir, first.Sha, second.Sha);
    }

    private static void StageFiles(string repoPath, params string[] files)
    {
        using var repo = new LibGit2Sharp.Repository(repoPath);
        foreach (var f in files)
            LibGit2Sharp.Commands.Stage(repo, f);
    }

    private static string Fingerprint(string repoPath)
    {
        var index = File.ReadAllBytes(System.IO.Path.Combine(repoPath, ".git", "index"));
        var head = File.ReadAllText(System.IO.Path.Combine(repoPath, ".git", "HEAD")).Trim();
        var refPath = head.StartsWith("ref: ", StringComparison.Ordinal)
            ? System.IO.Path.Combine(repoPath, ".git", head[5..].Replace('/', System.IO.Path.DirectorySeparatorChar))
            : null;
        var tip = refPath is not null && File.Exists(refPath) ? File.ReadAllText(refPath).Trim() : head;
        return Convert.ToHexString(SHA256.HashData(index)) + "|" + tip;
    }

    private static ControlRequest Req(ControlVerb verb, Fixture f, Action<ControlRequestBuilder>? configure = null)
    {
        var b = new ControlRequestBuilder { Verb = verb, Repo = f.Path, Cwd = f.Path };
        configure?.Invoke(b);
        return b.Build();
    }

    private sealed class ControlRequestBuilder
    {
        public ControlVerb Verb;
        public ControlVerb? Target;
        public string? Repo, Cwd, Rev, OtherRev, Path, Message, Note;
        public IReadOnlyList<string>? Paths;
        public int? Line;
        public bool Staged;
        public bool Focus = true;

        public ControlRequest Build() => new()
        {
            Verb = Verb,
            Target = Target,
            Note = Note,
            Paths = Paths,
            Repo = Repo,
            Cwd = Cwd,
            Rev = Rev,
            OtherRev = OtherRev,
            Path = Path,
            Line = Line,
            Staged = Staged,
            Focus = Focus,
            CommitMessage = Message,
        };
    }

    private RepositoryContext Ctx(Fixture f) => _workspace.OpenRepositories.Single(c =>
        string.Equals(System.IO.Path.TrimEndingDirectorySeparator(c.RepositoryPath),
            System.IO.Path.TrimEndingDirectorySeparator(f.Path), StringComparison.OrdinalIgnoreCase));

    // ── Navigation ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Show_selects_and_requests_a_reveal_of_the_commit()
    {
        var f = NewRepo();
        var seen = new List<GitObjectId>();

        await _workspace.OpenAsync(f.Path);
        Ctx(f).State.RevealCommitRequested += (_, e) => seen.Add(e.CommitId);

        var response = await _host.HandleAsync(Req(ControlVerb.Show, f, b => b.Rev = "HEAD~1"), default);

        Assert.True(response.Ok, response.Message);
        var expected = new GitObjectId(f.First);
        Assert.Equal(expected, Ctx(f).State.SelectedCommitId);
        Assert.Equal([expected], seen);
        Assert.Equal(f.First, response.Commit);
        Assert.Null(Ctx(f).State.OpenDiff);
    }

    [Fact]
    public async Task Diff_opens_a_commit_diff_with_the_path_and_focus_line()
    {
        var f = NewRepo();

        var response = await _host.HandleAsync(Req(ControlVerb.Diff, f, b => { b.Rev = "HEAD"; b.Path = "a.txt"; b.Line = 2; }), default);

        Assert.True(response.Ok, response.Message);
        var open = Ctx(f).State.OpenDiff!;
        Assert.Equal(DiffSource.Commit, open.Source);
        Assert.Equal(new GitObjectId(f.Second), open.CommitId);
        Assert.Equal("a.txt", open.Path);
        Assert.Equal(2, open.FocusLine);
        Assert.Equal(1, open.ParentIndex);
        Assert.Equal(new GitObjectId(f.Second), Ctx(f).State.SelectedCommitId);
        Assert.Equal("a.txt", response.Path);
    }

    [Fact]
    public async Task Compare_opens_a_comparison_between_the_two_revisions()
    {
        var f = NewRepo();

        var response = await _host.HandleAsync(Req(ControlVerb.Compare, f, b => { b.Rev = "HEAD~1"; b.OtherRev = "HEAD"; b.Path = "a.txt"; }), default);

        Assert.True(response.Ok, response.Message);
        var open = Ctx(f).State.OpenDiff!;
        Assert.Equal(DiffSource.Comparison, open.Source);
        Assert.Equal(new GitObjectId(f.First), open.CommitId);
        Assert.Equal(new GitObjectId(f.Second), open.OtherCommitId);
        Assert.Equal(f.First, response.Commit);
        Assert.Equal(f.Second, response.OtherCommit);
    }

    [Fact]
    public async Task Changes_opens_the_unstaged_or_staged_diff()
    {
        var f = NewRepo();

        var unstaged = await _host.HandleAsync(Req(ControlVerb.Changes, f, b => b.Path = "a.txt"), default);
        Assert.True(unstaged.Ok, unstaged.Message);
        Assert.Equal(DiffSource.UnstagedChanges, Ctx(f).State.OpenDiff!.Source);
        Assert.Null(Ctx(f).State.SelectedCommitId);

        var staged = await _host.HandleAsync(Req(ControlVerb.Changes, f, b => { b.Path = "a.txt"; b.Staged = true; b.Line = 3; }), default);
        Assert.True(staged.Ok, staged.Message);
        var open = Ctx(f).State.OpenDiff!;
        Assert.Equal(DiffSource.StagedChanges, open.Source);
        Assert.Equal(3, open.FocusLine);
    }

    [Fact]
    public async Task A_repository_that_is_not_open_is_opened_as_a_new_tab()
    {
        var f = NewRepo();
        Assert.Empty(_workspace.OpenRepositories);

        var response = await _host.HandleAsync(Req(ControlVerb.Open, f), default);

        Assert.True(response.Ok, response.Message);
        Assert.Single(_workspace.OpenRepositories);
        Assert.Same(Ctx(f), _workspace.Focused);
    }

    [Fact]
    public async Task A_second_request_for_an_open_repository_reuses_its_tab()
    {
        var f = NewRepo();

        await _host.HandleAsync(Req(ControlVerb.Open, f), default);
        await _host.HandleAsync(Req(ControlVerb.Show, f, b => b.Rev = "HEAD"), default);

        Assert.Single(_workspace.OpenRepositories);
    }

    [Fact]
    public async Task An_unknown_revision_is_rev_not_found_and_changes_nothing()
    {
        var f = NewRepo();
        await _host.HandleAsync(Req(ControlVerb.Show, f, b => b.Rev = "HEAD"), default);
        var before = Ctx(f).State.SelectedCommitId;

        var response = await _host.HandleAsync(Req(ControlVerb.Show, f, b => b.Rev = "nope"), default);

        Assert.False(response.Ok);
        Assert.Equal(ControlError.RevNotFound, response.Error);
        Assert.Equal(before, Ctx(f).State.SelectedCommitId);
    }

    [Fact]
    public async Task A_hostile_revision_is_rev_not_found_before_git_runs_it()
    {
        var f = NewRepo();
        await _host.HandleAsync(Req(ControlVerb.Open, f), default);
        var calls = _cli.Calls.Count;

        var response = await _host.HandleAsync(Req(ControlVerb.Show, f, b => b.Rev = "--all"), default);

        Assert.Equal(ControlError.RevNotFound, response.Error);
        Assert.DoesNotContain(_cli.Calls.Skip(calls), c => c.Contains("--all"));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("sub/../../outside.txt")]
    public async Task A_path_outside_the_repository_is_path_invalid(string path)
    {
        var f = NewRepo();

        var response = await _host.HandleAsync(Req(ControlVerb.Diff, f, b => { b.Rev = "HEAD"; b.Path = path; }), default);

        Assert.Equal(ControlError.PathInvalid, response.Error);
        Assert.Null(Ctx(f).State.OpenDiff);
    }

    [Fact]
    public async Task A_folder_that_does_not_exist_is_repo_not_found()
    {
        var f = NewRepo();

        var response = await _host.HandleAsync(Req(ControlVerb.Open, f, b => b.Repo = f.Path + "-missing"), default);

        Assert.Equal(ControlError.RepoNotFound, response.Error);
    }

    [Fact]
    public async Task A_folder_that_is_not_a_repository_is_repo_not_found()
    {
        var plain = TestScratch.NewDirectory("historia-ctl-plain-");
        _dirs.Add(plain);

        var response = await _host.HandleAsync(new ControlRequest { Verb = ControlVerb.Open, Repo = plain, Cwd = plain }, default);

        Assert.Equal(ControlError.RepoNotFound, response.Error);
        Assert.Empty(_workspace.OpenRepositories);
    }

    [Fact]
    public async Task Turning_the_setting_off_answers_disabled_live()
    {
        var f = NewRepo();
        await _settings.UpdateAsync(s => s with { ExternalControlEnabled = false });

        var ping = await _host.HandleAsync(new ControlRequest { Verb = ControlVerb.Ping }, default);
        var show = await _host.HandleAsync(Req(ControlVerb.Show, f, b => b.Rev = "HEAD"), default);

        Assert.Equal(ControlError.Disabled, ping.Error);
        Assert.Equal(ControlError.Disabled, show.Error);
        Assert.Empty(_workspace.OpenRepositories);

        await _settings.UpdateAsync(s => s with { ExternalControlEnabled = true });
        Assert.True((await _host.HandleAsync(new ControlRequest { Verb = ControlVerb.Ping }, default)).Ok);
    }

    [Fact]
    public async Task Selection_reports_what_the_state_holds()
    {
        var f = NewRepo();
        Assert.Null((await _host.HandleAsync(new ControlRequest { Verb = ControlVerb.Selection }, default)).Selection!.Repo);

        await _host.HandleAsync(Req(ControlVerb.Diff, f, b => { b.Rev = "HEAD"; b.Path = "a.txt"; }), default);
        var selection = (await _host.HandleAsync(new ControlRequest { Verb = ControlVerb.Selection }, default)).Selection!;

        Assert.Equal(f.Second, selection.Commit);
        Assert.Equal("commit", selection.Diff!.Source);
        Assert.Equal("a.txt", selection.Diff.Path);
        Assert.Equal(f.Second, selection.Diff.Commit);
    }

    [Fact]
    public async Task Focus_activates_the_window_unless_declined()
    {
        var f = NewRepo();
        var activations = 0;
        var host = NewHost(() => activations++);

        await host.HandleAsync(Req(ControlVerb.Show, f, b => b.Rev = "HEAD"), default);
        await host.HandleAsync(Req(ControlVerb.Show, f, b => { b.Rev = "HEAD~1"; b.Focus = false; }), default);
        await host.HandleAsync(new ControlRequest { Verb = ControlVerb.Ping }, default);

        Assert.Equal(1, activations);
    }

    [Fact]
    public async Task Concurrent_requests_are_served_one_at_a_time()
    {
        var f = NewRepo();
        await _host.HandleAsync(Req(ControlVerb.Open, f), default);
        _cli.Delay = TimeSpan.FromMilliseconds(80);

        var results = await Task.WhenAll(
            _host.HandleAsync(Req(ControlVerb.Show, f, b => b.Rev = "HEAD"), default),
            _host.HandleAsync(Req(ControlVerb.Show, f, b => b.Rev = "HEAD~1"), default),
            _host.HandleAsync(Req(ControlVerb.Show, f, b => b.Rev = "HEAD"), default));

        Assert.All(results, r => Assert.True(r.Ok, r.Message));
        Assert.Equal(1, _cli.MaxConcurrentRevParse);
    }

    // ── Queue ───────────────────────────────────────────────────────────────────────────

    private static ControlRequest Queued(ControlVerb target, Fixture f, Action<ControlRequestBuilder>? configure = null) =>
        Req(ControlVerb.Queue, f, b =>
        {
            b.Target = target;
            b.Focus = false;
            configure?.Invoke(b);
        });

    private static string Leaf(string path) => System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(path));

    [Fact]
    public async Task A_queued_show_enqueues_the_commit_and_its_subject_and_leaves_the_view_alone()
    {
        var f = NewRepo();
        var other = NewRepo();
        var activations = 0;
        var host = NewHost(() => activations++);
        await host.HandleAsync(Req(ControlVerb.Show, other, b => { b.Rev = "HEAD"; b.Focus = false; }), default);
        var focused = _workspace.Focused;
        var selected = focused!.State.SelectedCommitId;
        var changes = 0;
        _queue.Changed += (_, _) => changes++;

        var response = await host.HandleAsync(Queued(ControlVerb.Show, f, b => { b.Rev = "HEAD~1"; b.Note = "  look at\nthis  "; }), default);

        Assert.True(response.Ok, response.Message);
        Assert.True(response.Queued);
        Assert.Equal(f.First, response.Commit);
        Assert.Null(response.Draft);
        var item = Assert.Single(_queue.Items);
        Assert.Equal(response.QueueId, item.Id);
        Assert.Equal(ControlVerb.Show, item.Target);
        Assert.Equal(f.First, item.Rev);
        Assert.Equal("first", item.CommitSubject);
        Assert.Equal("look at this", item.Note);
        Assert.Equal(Leaf(f.Path), item.RepoName, ignoreCase: true);
        Assert.Equal(System.IO.Path.GetFullPath(f.Path), item.RepoRoot, ignoreCase: true);
        Assert.Equal(1, changes);

        Assert.Single(_workspace.OpenRepositories);
        Assert.Same(focused, _workspace.Focused);
        Assert.Equal(selected, focused.State.SelectedCommitId);
        Assert.Equal(0, activations);
    }

    [Fact]
    public async Task A_queued_change_list_stores_every_file_and_opens_no_tab()
    {
        var f = NewRepo();

        var response = await _host.HandleAsync(
            Queued(ControlVerb.Changes, f, b => { b.Path = "A.TXT"; b.Paths = ["A.TXT", "b.txt"]; b.Staged = true; b.Line = 2; }), default);

        Assert.True(response.Ok, response.Message);
        var item = Assert.Single(_queue.Items);
        string[] expected = OperatingSystem.IsWindows() ? ["a.txt", "b.txt"] : ["A.TXT", "b.txt"];
        Assert.Equal(expected, item.Paths);
        Assert.True(item.Staged);
        Assert.Equal(2, item.Line);
        Assert.Equal(item.Paths[0], response.Path);
        Assert.Empty(_workspace.OpenRepositories);
    }

    [Fact]
    public async Task A_queued_commit_proposal_records_the_message_and_the_staged_files()
    {
        var f = NewRepo();
        File.WriteAllText(System.IO.Path.Combine(f.Path, "a.txt"), "changed a\n");
        StageFiles(f.Path, "a.txt");
        var before = Fingerprint(f.Path);

        var response = await _host.HandleAsync(Queued(ControlVerb.ProposeCommit, f, b => b.Message = "feat: x  \r\n\r\nbody\r\n"), default);

        Assert.True(response.Ok, response.Message);
        Assert.Equal(["a.txt"], response.StagedPaths);
        Assert.Null(response.Draft);
        var item = Assert.Single(_queue.Items);
        Assert.Equal("feat: x\n\nbody", item.CommitMessage);
        Assert.Equal(["a.txt"], item.StagedPaths);
        Assert.Empty(_workspace.OpenRepositories);
        Assert.Equal(before, Fingerprint(f.Path));
    }

    [Fact]
    public async Task A_queued_commit_proposal_with_nothing_staged_enqueues_nothing()
    {
        var f = NewRepo();

        var response = await _host.HandleAsync(Queued(ControlVerb.ProposeCommit, f, b => b.Message = "feat: x"), default);

        Assert.Equal(ControlError.NothingStaged, response.Error);
        Assert.Empty(_queue.Items);
    }

    [Fact]
    public async Task Queueing_with_control_off_answers_disabled()
    {
        var f = NewRepo();
        await _settings.UpdateAsync(s => s with { ExternalControlEnabled = false });
        try
        {
            var response = await _host.HandleAsync(Queued(ControlVerb.Show, f, b => b.Rev = "HEAD"), default);

            Assert.Equal(ControlError.Disabled, response.Error);
            Assert.Empty(_queue.Items);
        }
        finally
        {
            await _settings.UpdateAsync(s => s with { ExternalControlEnabled = true });
        }
    }

    [Fact]
    public async Task Queueing_from_a_folder_outside_a_repository_is_repo_not_found()
    {
        var plain = TestScratch.NewDirectory("historia-ctl-plain-");
        _dirs.Add(plain);

        var response = await _host.HandleAsync(
            new ControlRequest { Verb = ControlVerb.Queue, Target = ControlVerb.Open, Repo = plain, Cwd = plain, Focus = false }, default);

        Assert.Equal(ControlError.RepoNotFound, response.Error);
        Assert.Empty(_queue.Items);
    }

    [Fact]
    public async Task Queueing_an_unknown_revision_is_rev_not_found_and_enqueues_nothing()
    {
        var f = NewRepo();

        var response = await _host.HandleAsync(Queued(ControlVerb.Diff, f, b => { b.Rev = "nope"; b.Path = "a.txt"; }), default);

        Assert.Equal(ControlError.RevNotFound, response.Error);
        Assert.Empty(_queue.Items);
    }

    [Fact]
    public async Task Queueing_a_path_outside_the_repository_is_path_invalid()
    {
        var f = NewRepo();

        var response = await _host.HandleAsync(
            Queued(ControlVerb.Changes, f, b => { b.Path = "a.txt"; b.Paths = ["a.txt", "../outside.txt"]; }), default);

        Assert.Equal(ControlError.PathInvalid, response.Error);
        Assert.Empty(_queue.Items);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(ControlVerb.Ping)]
    [InlineData(ControlVerb.Selection)]
    [InlineData(ControlVerb.Queue)]
    public async Task A_queue_request_without_a_navigation_is_usage(ControlVerb? target)
    {
        var f = NewRepo();

        var response = await _host.HandleAsync(
            new ControlRequest { Verb = ControlVerb.Queue, Target = target, Repo = f.Path, Cwd = f.Path, Focus = false }, default);

        Assert.Equal(ControlError.Usage, response.Error);
        Assert.Empty(_queue.Items);
    }

    [Fact]
    public async Task An_item_queued_under_a_differently_cased_path_opens_the_tab_already_open()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var f = NewRepo();
        await _workspace.OpenAsync(f.Path);
        var queued = await _host.HandleAsync(Queued(ControlVerb.Show, f, b => { b.Repo = f.Path.ToUpperInvariant(); b.Cwd = b.Repo; b.Rev = "HEAD~1"; }), default);
        Assert.True(queued.Ok, queued.Message);

        var (response, _) = await _navigator.NavigateAsync(ControlTarget.FromItem(Assert.Single(_queue.Items)), default);

        Assert.True(response.Ok, response.Message);
        Assert.Single(_workspace.OpenRepositories);
        Assert.Equal(new GitObjectId(f.First), Ctx(f).State.SelectedCommitId);
    }

    [Fact]
    public async Task A_queued_change_list_navigates_to_its_first_file()
    {
        var f = NewRepo();
        await _host.HandleAsync(Queued(ControlVerb.Changes, f, b => { b.Path = "b.txt"; b.Paths = ["b.txt", "a.txt"]; b.Line = 1; }), default);

        var (response, name) = await _navigator.NavigateAsync(ControlTarget.FromItem(Assert.Single(_queue.Items)), default);

        Assert.True(response.Ok, response.Message);
        Assert.Equal(Ctx(f).DisplayName, name);
        var open = Ctx(f).State.OpenDiff!;
        Assert.Equal(DiffSource.UnstagedChanges, open.Source);
        Assert.Equal("b.txt", open.Path);
        Assert.Equal(1, open.FocusLine);
    }

    // ── Commit proposal ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Propose_commit_after_staging_two_files_opens_the_staged_diff_and_leaves_the_proposal_pending()
    {
        var f = NewRepo();
        await _host.HandleAsync(Req(ControlVerb.Show, f, b => b.Rev = "HEAD"), default);
        File.WriteAllText(System.IO.Path.Combine(f.Path, "a.txt"), "changed a\n");
        File.WriteAllText(System.IO.Path.Combine(f.Path, "b.txt"), "changed b\n");
        StageFiles(f.Path, "a.txt", "b.txt");
        var before = Fingerprint(f.Path);

        var response = await _host.HandleAsync(Req(ControlVerb.ProposeCommit, f, b => b.Message = "feat: x\n\nbody"), default);

        Assert.True(response.Ok, response.Message);
        Assert.Equal(["a.txt", "b.txt"], response.StagedPaths);
        Assert.Equal(ControlDraftOutcome.Pending, response.Draft);
        var state = Ctx(f).State;
        Assert.Null(state.SelectedCommitId);
        Assert.Equal(DiffSource.StagedChanges, state.OpenDiff!.Source);
        Assert.Equal("a.txt", state.OpenDiff.Path);
        Assert.Equal("feat: x\n\nbody", state.PendingCommitMessage);
        Assert.Equal(before, Fingerprint(f.Path));
    }

    [Fact]
    public async Task Propose_commit_reports_filled_when_the_commit_box_takes_the_message()
    {
        var f = NewRepo();
        await _host.HandleAsync(Req(ControlVerb.Open, f), default);
        File.WriteAllText(System.IO.Path.Combine(f.Path, "a.txt"), "changed a\n");
        StageFiles(f.Path, "a.txt");
        var state = Ctx(f).State;
        state.CommitMessageProposed += (_, _) => state.ClearPendingCommitMessage();

        var response = await _host.HandleAsync(Req(ControlVerb.ProposeCommit, f, b => b.Message = "fix: y"), default);

        Assert.Equal(ControlDraftOutcome.Filled, response.Draft);
    }

    [Fact]
    public async Task Propose_commit_with_nothing_staged_changes_nothing()
    {
        var f = NewRepo();
        await _host.HandleAsync(Req(ControlVerb.Diff, f, b => { b.Rev = "HEAD"; b.Path = "a.txt"; }), default);
        var state = Ctx(f).State;
        var selected = state.SelectedCommitId;
        var diff = state.OpenDiff;

        var response = await _host.HandleAsync(Req(ControlVerb.ProposeCommit, f, b => b.Message = "feat: x"), default);

        Assert.Equal(ControlError.NothingStaged, response.Error);
        Assert.Equal(selected, state.SelectedCommitId);
        Assert.Equal(diff, state.OpenDiff);
        Assert.Null(state.PendingCommitMessage);
    }

    [Fact]
    public async Task Propose_commit_with_an_oversize_message_is_usage()
    {
        var f = NewRepo();
        File.WriteAllText(System.IO.Path.Combine(f.Path, "a.txt"), "changed a\n");
        StageFiles(f.Path, "a.txt");

        var response = await _host.HandleAsync(Req(ControlVerb.ProposeCommit, f, b => b.Message = new string('x', 17 * 1024)), default);

        Assert.Equal(ControlError.Usage, response.Error);
        Assert.Empty(_workspace.OpenRepositories);
    }

    [Fact]
    public async Task No_request_changes_the_index_or_head()
    {
        var f = NewRepo();
        File.WriteAllText(System.IO.Path.Combine(f.Path, "b.txt"), "changed b\n");
        StageFiles(f.Path, "b.txt");
        var before = Fingerprint(f.Path);

        var requests = new[]
        {
            Req(ControlVerb.Open, f),
            Req(ControlVerb.Show, f, b => b.Rev = "HEAD~1"),
            Req(ControlVerb.Diff, f, b => { b.Rev = "HEAD"; b.Path = "a.txt"; }),
            Req(ControlVerb.Compare, f, b => { b.Rev = "HEAD~1"; b.OtherRev = "HEAD"; b.Path = "a.txt"; }),
            Req(ControlVerb.Changes, f, b => { b.Path = "b.txt"; b.Staged = true; }),
            Req(ControlVerb.ProposeCommit, f, b => b.Message = "feat: z"),
            Queued(ControlVerb.Show, f, b => b.Rev = "HEAD"),
            Queued(ControlVerb.Changes, f, b => { b.Path = "a.txt"; b.Paths = ["a.txt", "b.txt"]; }),
            Queued(ControlVerb.ProposeCommit, f, b => b.Message = "feat: z"),
            new ControlRequest { Verb = ControlVerb.Selection },
            new ControlRequest { Verb = ControlVerb.Ping },
        };

        foreach (var request in requests)
        {
            await _host.HandleAsync(request, default);
            Assert.Equal(before, Fingerprint(f.Path));
        }
    }

    // ── The pipe server ─────────────────────────────────────────────────────────────────

    private sealed class Server : IAsyncDisposable
    {
        public string PipeName { get; } = "Historia.Test." + Guid.NewGuid().ToString("N")[..8];
        public ConcurrentQueue<string[]> Args { get; } = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _run;

        public Server(Func<ControlRequest, CancellationToken, Task<ControlResponse>> handler, TimeSpan? clientIoTimeout = null)
        {
            _run = SingleInstance.RunServerAsync(a => Args.Enqueue(a), handler, _cts.Token, PipeName, clientIoTimeout);
        }

        public async Task<ControlResponse?> SendAsync(ControlRequest request)
        {
            var connected = await ControlPipe.ConnectClientAsync(TimeSpan.FromSeconds(5), pipeName: PipeName);
            Assert.Equal(ControlConnectOutcome.Connected, connected.Outcome);
            using var client = connected.Stream!;
            using var writer = ControlPipe.CreateWriter(client);
            using var reader = ControlPipe.CreateReader(client);
            await ControlPipe.WriteRequestAsync(writer, request);
            return await ControlPipe.ReadResponseAsync(reader);
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { await _run.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            _cts.Dispose();
        }
    }

    [Fact]
    public async Task A_control_request_over_the_pipe_is_answered_by_the_host()
    {
        var f = NewRepo();
        await using var server = new Server(_host.HandleAsync);

        var response = await server.SendAsync(Req(ControlVerb.Show, f, b => b.Rev = "HEAD"));

        Assert.True(response!.Ok, response.Message);
        Assert.Equal(new GitObjectId(f.Second), Ctx(f).State.SelectedCommitId);
    }

    [Fact]
    public async Task A_header_less_connection_still_reaches_the_launch_handler()
    {
        await using var server = new Server(_host.HandleAsync);

        using (var client = new NamedPipeClientStream(".", server.PipeName, PipeDirection.Out))
        {
            await client.ConnectAsync(5000);
            using var w = new StreamWriter(client) { AutoFlush = true };
            await w.WriteLineAsync(@"C:\one");
            await w.WriteLineAsync(@"C:\two");
        }

        string[]? received = null;
        for (var i = 0; i < 100 && !server.Args.TryDequeue(out received); i++)
            await Task.Delay(30);

        Assert.Equal([@"C:\one", @"C:\two"], received!);
    }

    [Fact]
    public async Task More_concurrent_connections_than_pipe_instances_are_all_answered_and_the_listener_survives()
    {
        var release = new TaskCompletionSource();
        var started = 0;
        await using var server = new Server(async (request, ct) =>
        {
            Interlocked.Increment(ref started);
            await release.Task.WaitAsync(ct);
            return ControlResponse.Success();
        });

        var sends = Enumerable.Range(0, 6)
            .Select(_ => server.SendAsync(new ControlRequest { Verb = ControlVerb.Ping }))
            .ToList();

        for (var i = 0; i < 100 && Volatile.Read(ref started) < 4; i++)
            await Task.Delay(30);
        Assert.Equal(4, Volatile.Read(ref started));

        release.SetResult();
        var responses = await Task.WhenAll(sends);

        Assert.All(responses, r => Assert.True(r!.Ok));
        Assert.True((await server.SendAsync(new ControlRequest { Verb = ControlVerb.Ping }))!.Ok);
    }

    [Fact]
    public async Task A_handler_that_throws_answers_internal_and_keeps_listening()
    {
        var calls = 0;
        await using var server = new Server((request, ct) =>
            ++calls == 1 ? throw new InvalidOperationException("boom") : Task.FromResult(ControlResponse.Success()));

        var first = await server.SendAsync(new ControlRequest { Verb = ControlVerb.Ping });
        var second = await server.SendAsync(new ControlRequest { Verb = ControlVerb.Ping });

        Assert.Equal(ControlError.Internal, first!.Error);
        Assert.True(second!.Ok);
    }

    [Fact]
    public async Task An_unparseable_request_is_usage()
    {
        await using var server = new Server(_host.HandleAsync);

        var connected = await ControlPipe.ConnectClientAsync(TimeSpan.FromSeconds(5), pipeName: server.PipeName);
        using var client = connected.Stream!;
        using var writer = ControlPipe.CreateWriter(client);
        using var reader = ControlPipe.CreateReader(client);
        await writer.WriteLineAsync(ControlPipe.ProtocolHeader);
        await writer.WriteLineAsync("{not json");
        await writer.FlushAsync();

        var response = await ControlPipe.ReadResponseAsync(reader);

        Assert.Equal(ControlError.Usage, response!.Error);
    }

    [Fact]
    public async Task An_integer_verb_over_the_pipe_is_usage()
    {
        var calls = 0;
        await using var server = new Server((request, ct) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(ControlResponse.Success());
        });

        var connected = await ControlPipe.ConnectClientAsync(TimeSpan.FromSeconds(5), pipeName: server.PipeName);
        using var client = connected.Stream!;
        using var writer = ControlPipe.CreateWriter(client);
        using var reader = ControlPipe.CreateReader(client);
        await writer.WriteLineAsync(ControlPipe.ProtocolHeader);
        await writer.WriteLineAsync("{\"verb\":99}");
        await writer.FlushAsync();

        var response = await ControlPipe.ReadResponseAsync(reader);

        Assert.Equal(ControlError.Usage, response!.Error);
        Assert.Equal(0, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task A_client_that_never_reads_its_answer_frees_its_slot()
    {
        await using var server = new Server(
            (request, ct) => Task.FromResult(ControlResponse.Fail(ControlError.Internal, new string('x', 300))),
            clientIoTimeout: TimeSpan.FromMilliseconds(300));

        var silent = new List<NamedPipeClientStream>();
        try
        {
            for (var i = 0; i < 4; i++)
            {
                var connected = await ControlPipe.ConnectClientAsync(TimeSpan.FromSeconds(5), pipeName: server.PipeName);
                Assert.Equal(ControlConnectOutcome.Connected, connected.Outcome);
                silent.Add(connected.Stream!);
                var w = ControlPipe.CreateWriter(connected.Stream!);
                await ControlPipe.WriteRequestAsync(w, new ControlRequest { Verb = ControlVerb.Ping });
            }

            var answered = await server.SendAsync(new ControlRequest { Verb = ControlVerb.Ping }).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(answered);

            using (var client = new NamedPipeClientStream(".", server.PipeName, PipeDirection.Out))
            {
                await client.ConnectAsync(5000);
                using var w = new StreamWriter(client) { AutoFlush = true };
                await w.WriteLineAsync(@"C:fter");
            }

            string[]? received = null;
            for (var i = 0; i < 200 && !server.Args.TryDequeue(out received); i++)
                await Task.Delay(30);
            Assert.Equal([@"C:fter"], received!);
        }
        finally
        {
            foreach (var s in silent)
                s.Dispose();
        }
    }

    // ── Test doubles ────────────────────────────────────────────────────────────────────

    private sealed class InlineUi : IUiDispatcher
    {
        public bool CheckAccess() => true;
        public void Post(Action action) => action();
        public IUiTimer CreateTimer(TimeSpan interval, Action tick) => new NoTimer();

        private sealed class NoTimer : IUiTimer
        {
            public void Start() { }
            public void Stop() { }
            public void Dispose() { }
        }
    }

    private sealed class TrackingCli(IGitCliService inner) : IGitCliService
    {
        private int _revParseInFlight;
        private int _maxRevParse;

        public ConcurrentQueue<string> Calls { get; } = new();
        public TimeSpan Delay { get; set; }
        public int MaxConcurrentRevParse => Volatile.Read(ref _maxRevParse);
        public bool IsGitAvailable => inner.IsGitAvailable;

        public Task<GitCliResult> RunAsync(GitCliRequest request, CancellationToken ct = default)
            => RunAsync(request, null, null, ct);

        public async Task<GitCliResult> RunAsync(
            GitCliRequest request, IProgress<GitProgress>? progress, Action<string>? onStdoutLine, CancellationToken ct = default)
        {
            Calls.Enqueue(string.Join(' ', request.Arguments));
            var isRevParse = request.Arguments.Count > 1 && request.Arguments[0] == "rev-parse" && request.Arguments[1] == "--verify";
            if (!isRevParse)
                return await inner.RunAsync(request, progress, onStdoutLine, ct);

            var now = Interlocked.Increment(ref _revParseInFlight);
            int seen;
            while (now > (seen = Volatile.Read(ref _maxRevParse)) && Interlocked.CompareExchange(ref _maxRevParse, now, seen) != seen) { }
            try
            {
                if (Delay > TimeSpan.Zero)
                    await Task.Delay(Delay, ct);
                return await inner.RunAsync(request, progress, onStdoutLine, ct);
            }
            finally
            {
                Interlocked.Decrement(ref _revParseInFlight);
            }
        }
    }

    private sealed class FakeLayoutService : IRepoLayoutService
    {
        public IReadOnlyList<GraphColumn> GlobalColumns => GraphColumnDefaults.All;
        public event EventHandler<RepoLayoutChangedEventArgs>? Changed { add { } remove { } }
        public RepoLayout Get(string repoPath) => new();
        public Task UpdateAsync(string repoPath, Func<RepoLayout, RepoLayout> mutate) => Task.CompletedTask;
        public Task UpdateGlobalAsync(Func<IReadOnlyList<GraphColumn>, IReadOnlyList<GraphColumn>> mutate) => Task.CompletedTask;
        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeVault : ICredentialVaultService
    {
        public Task StoreTokenAsync(string key, string token, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> RetrieveTokenAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task DeleteTokenAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
    }
}
