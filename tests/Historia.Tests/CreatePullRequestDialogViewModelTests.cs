namespace Historia.Tests;

[Trait("tier", "unit")]
public class CreatePullRequestDialogViewModelTests
{
    private const string GitHubUrl = "https://github.com/acme/widgets.git";

    private sealed class FakeLauncher : IExternalLauncher
    {
        public List<string> Opened { get; } = [];

        public void OpenUrl(string url) => Opened.Add(url);

        public void RevealInFileManager(string path)
        {
        }

        public void OpenFolder(string path)
        {
        }
    }

    private static BranchInfo Local(string name, bool current = false, string? upstream = null) => new()
    {
        Name = name,
        CanonicalName = $"refs/heads/{name}",
        IsRemote = false,
        IsCurrent = current,
        UpstreamName = upstream,
    };

    private static BranchInfo Remote(string remote, string name) => new()
    {
        Name = $"{remote}/{name}",
        CanonicalName = $"refs/remotes/{remote}/{name}",
        IsRemote = true,
        IsCurrent = false,
        RemoteName = remote,
    };

    private static RemoteInfo RemoteOf(string name, string url = GitHubUrl) => new() { Name = name, Url = url };

    private static (CreatePullRequestDialogViewModel Vm, FakeLauncher Launcher, List<(string Branch, string Remote)> Pushes)
        Create(
            IReadOnlyList<BranchInfo> branches,
            IReadOnlyList<RemoteInfo>? remotes = null,
            string initialSource = "",
            Func<IReadOnlyList<BranchInfo>>? refresh = null,
            bool pushResult = true)
    {
        var launcher = new FakeLauncher();
        var pushes = new List<(string, string)>();
        var request = new CreatePullRequestRequest(
            branches,
            remotes ?? [RemoteOf("origin")],
            initialSource,
            (branch, remote, _) =>
            {
                pushes.Add((branch, remote));
                return Task.FromResult(pushResult);
            },
            refresh ?? (() => branches));
        return (new CreatePullRequestDialogViewModel(request, launcher), launcher, pushes);
    }

    private static List<BranchInfo> TypicalBranches() =>
    [
        Local("main", current: true, upstream: "origin/main"),
        Local("feature", upstream: "origin/feature"),
        Remote("origin", "main"),
        Remote("origin", "feature"),
        Remote("origin", "develop"),
    ];

    [Fact]
    public void Source_lists_locals_then_remotes_with_dot_prefix()
    {
        var (vm, _, _) = Create(TypicalBranches());

        Assert.Equal(
            ["local.main", "local.feature", "origin.main", "origin.feature", "origin.develop"],
            vm.SourceItems);
    }

    [Fact]
    public void Source_defaults_to_the_requested_branch()
    {
        var (vm, _, _) = Create(TypicalBranches(), initialSource: "local.feature");

        Assert.Equal("local.feature", vm.SelectedSource);
        Assert.Equal("local.feature", vm.SourceText);
    }

    [Fact]
    public void Target_lists_remote_branches_only_without_HEAD()
    {
        var branches = TypicalBranches();
        branches.Add(Remote("origin", "HEAD"));

        var (vm, _, _) = Create(branches);

        Assert.Equal(["origin.main", "origin.feature", "origin.develop"], vm.TargetItems);
        Assert.DoesNotContain("origin.HEAD", vm.SourceItems);
    }

    [Fact]
    public void Default_target_prefers_main_then_master()
    {
        var withMain = Create(TypicalBranches(), initialSource: "local.feature");
        Assert.Equal("origin.main", withMain.Vm.SelectedTarget);

        var withMaster = Create(
            [Local("work", upstream: "origin/work"), Remote("origin", "alpha"), Remote("origin", "master"), Remote("origin", "work")],
            initialSource: "local.work");
        Assert.Equal("origin.master", withMaster.Vm.SelectedTarget);

        var withNeither = Create(
            [Local("work", upstream: "origin/work"), Remote("origin", "zeta"), Remote("origin", "alpha")],
            initialSource: "local.work");
        Assert.Equal("origin.zeta", withNeither.Vm.SelectedTarget);
    }

    [Fact]
    public void Default_target_stays_on_the_source_remote()
    {
        var (vm, _, _) = Create(
            [Local("feature", upstream: "fork/feature"), Remote("origin", "main"), Remote("fork", "main"), Remote("fork", "feature")],
            [RemoteOf("origin"), RemoteOf("fork", "https://github.com/kevin/widgets.git")],
            "local.feature");

        Assert.Equal("fork.main", vm.SelectedTarget);
    }

    [Fact]
    public void Typing_ranks_like_sidebar_search()
    {
        var (vm, _, _) = Create(TypicalBranches());

        vm.SourceText = "dev";
        var expected = RefSearch.Rank(vm.SourceItems, s => s, FuzzyScorer.PrepareQuery("dev")).Select(r => r.Item).ToList();

        Assert.Equal(expected, vm.FilteredSourceItems);
        Assert.Equal("origin.develop", vm.FilteredSourceItems[0]);
        Assert.Null(vm.SelectedSource);

        vm.TargetText = "fea";
        Assert.Equal(["origin.feature"], vm.FilteredTargetItems);

        vm.TargetText = "origin.feature";
        Assert.Equal("origin.feature", vm.SelectedTarget);
        Assert.Equal(vm.TargetItems, vm.FilteredTargetItems);
    }

    [Fact]
    public void Picking_an_item_selects_it_and_updates_the_url()
    {
        var (vm, _, _) = Create(TypicalBranches(), initialSource: "local.main");

        vm.SelectedSource = "origin.feature";
        vm.SelectedTarget = "origin.develop";

        Assert.Equal("origin.feature", vm.SourceText);
        Assert.Equal("https://github.com/acme/widgets/compare/develop...feature?expand=1", vm.Url);
    }

    [Fact]
    public void Local_source_without_remote_ref_asks_to_push()
    {
        var (vm, _, _) = Create(
            [Local("topic"), Local("main", current: true), Remote("origin", "main")],
            initialSource: "local.topic");

        Assert.Equal("", vm.Url);
        Assert.Equal("Push the source branch first.", vm.UrlProblem);
        Assert.False(vm.HasUrl);
    }

    [Fact]
    public void Local_source_with_upstream_builds_url_from_the_upstream_branch()
    {
        var (vm, _, _) = Create(
            [Local("topic", upstream: "origin/topic-remote"), Remote("origin", "topic-remote"), Remote("origin", "main")],
            initialSource: "local.topic");

        Assert.Null(vm.UrlProblem);
        Assert.Equal("https://github.com/acme/widgets/compare/main...topic-remote?expand=1", vm.Url);
    }

    [Fact]
    public void Fork_source_uses_the_owner_prefix()
    {
        var (vm, _, _) = Create(
            [Remote("origin", "main"), Remote("fork", "feature")],
            [RemoteOf("origin"), RemoteOf("fork", "https://github.com/kevin/widgets.git")],
            "fork.feature");

        vm.SelectedTarget = "origin.main";

        Assert.Equal("https://github.com/acme/widgets/compare/main...kevin:feature?expand=1", vm.Url);
    }

    [Fact]
    public void Push_enabled_only_for_local_source()
    {
        var (vm, _, _) = Create(TypicalBranches(), initialSource: "local.main");
        Assert.True(vm.PushCommand.CanExecute(null));

        vm.SelectedSource = "origin.develop";
        Assert.False(vm.PushCommand.CanExecute(null));

        vm.SourceText = "not a branch";
        Assert.False(vm.PushCommand.CanExecute(null));
    }

    [Fact]
    public void Push_is_disabled_without_remotes()
    {
        var (vm, _, _) = Create([Local("main", current: true)], remotes: [], initialSource: "local.main");

        Assert.False(vm.PushCommand.CanExecute(null));
    }

    [Fact]
    public async Task Push_success_refreshes_and_builds_url()
    {
        var branches = new List<BranchInfo> { Local("topic"), Remote("origin", "main") };
        var afterPush = new List<BranchInfo> { Local("topic", upstream: "origin/topic"), Remote("origin", "main"), Remote("origin", "topic") };
        var (vm, _, pushes) = Create(branches, initialSource: "local.topic", refresh: () => afterPush);
        Assert.Equal("Push the source branch first.", vm.UrlProblem);

        await vm.PushCommand.ExecuteAsync(null);

        Assert.Equal([("topic", "origin")], pushes);
        Assert.Null(vm.UrlProblem);
        Assert.Equal("https://github.com/acme/widgets/compare/main...topic?expand=1", vm.Url);
        Assert.Contains("origin.topic", vm.SourceItems);
        Assert.False(vm.IsPushing);
    }

    [Fact]
    public async Task Push_that_did_not_happen_leaves_the_url_unchanged()
    {
        var (vm, _, pushes) = Create(
            [Local("topic"), Remote("origin", "main")], initialSource: "local.topic", pushResult: false);

        await vm.PushCommand.ExecuteAsync(null);

        Assert.Single(pushes);
        Assert.Equal("Push the source branch first.", vm.UrlProblem);
        Assert.False(vm.IsPushing);
    }

    [Fact]
    public async Task Push_goes_to_the_first_remote_when_there_is_no_origin_or_upstream()
    {
        var (vm, _, pushes) = Create(
            [Local("topic"), Remote("mirror", "main")],
            [RemoteOf("mirror"), RemoteOf("backup")],
            "local.topic");

        await vm.PushCommand.ExecuteAsync(null);

        Assert.Equal([("topic", "mirror")], pushes);
    }

    [Fact]
    public void Open_launches_url_once()
    {
        var (vm, launcher, _) = Create(TypicalBranches(), initialSource: "local.feature");
        Assert.True(vm.OpenCommand.CanExecute(null));

        vm.OpenCommand.Execute(null);

        Assert.Equal(["https://github.com/acme/widgets/compare/main...feature?expand=1"], launcher.Opened);
    }

    [Fact]
    public void Nothing_is_opened_until_Open_runs()
    {
        var (_, launcher, _) = Create(TypicalBranches(), initialSource: "local.feature");

        Assert.Empty(launcher.Opened);
    }

    [Fact]
    public void Non_github_disables_open()
    {
        var (vm, _, _) = Create(
            TypicalBranches(),
            [RemoteOf("origin", "https://gitlab.com/acme/widgets.git")],
            "local.feature");

        Assert.False(vm.OpenCommand.CanExecute(null));
        Assert.Equal("Pull request links are built for GitHub remotes only.", vm.UrlProblem);
        Assert.Equal("", vm.Url);
    }

    [Fact]
    public void Close_raises_CloseRequested()
    {
        var (vm, _, _) = Create(TypicalBranches());
        var closed = 0;
        vm.CloseRequested += (_, _) => closed++;

        vm.CloseCommand.Execute(null);

        Assert.Equal(1, closed);
    }

    [Fact]
    public void No_branches_gives_an_empty_dialog()
    {
        var (vm, _, _) = Create([], remotes: []);

        Assert.Empty(vm.SourceItems);
        Assert.Equal("Choose a source branch.", vm.UrlProblem);
        Assert.False(vm.OpenCommand.CanExecute(null));
    }

    [Fact]
    public void ResolvePushRemote_prefers_upstream_then_origin_then_first()
    {
        var remotes = new[] { RemoteOf("alpha"), RemoteOf("origin"), RemoteOf("team/infra") };

        Assert.Equal("team/infra", CreatePullRequestDialogViewModel.ResolvePushRemote(Local("x", upstream: "team/infra/x"), remotes));
        Assert.Equal("origin", CreatePullRequestDialogViewModel.ResolvePushRemote(Local("x"), remotes));
        Assert.Equal("alpha", CreatePullRequestDialogViewModel.ResolvePushRemote(Local("x"), [RemoteOf("alpha"), RemoteOf("beta")]));
        Assert.Null(CreatePullRequestDialogViewModel.ResolvePushRemote(Local("x"), []));
    }
}
