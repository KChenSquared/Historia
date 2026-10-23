namespace Historia.Services.ExternalControl;

/// <summary>
/// One navigation to run in Historia: which repository, what to show there and, for a commit
/// proposal, the message to offer. Built from a live control request or from a queued item.
/// </summary>
public sealed record ControlTarget
{
    /// <summary>The repository folder, absolute; <see cref="Cwd"/> is used when it is empty.</summary>
    public string? RepoPath { get; init; }

    /// <summary>The folder relative paths resolve against, absolute; the repository root when empty.</summary>
    public string? Cwd { get; init; }

    /// <summary>The navigation verb: open, show, diff, compare, changes or propose-commit.</summary>
    public required ControlVerb Verb { get; init; }

    /// <summary>The commit to show, or the base of a compare.</summary>
    public string? Rev { get; init; }

    /// <summary>The head of a compare.</summary>
    public string? OtherRev { get; init; }

    /// <summary>The files to show; the diff opens on the first.</summary>
    public IReadOnlyList<string> Paths { get; init; } = [];

    /// <summary>1-based line in the first file to scroll to.</summary>
    public int? Line { get; init; }

    /// <summary>Shows the staged side of a working-copy change.</summary>
    public bool Staged { get; init; }

    /// <summary>The normalized commit message to offer for a commit proposal.</summary>
    public string? CommitMessage { get; init; }

    /// <summary>The target a live request names; <paramref name="commitMessage"/> is its message
    /// already normalized.</summary>
    public static ControlTarget FromRequest(ControlRequest request, string? commitMessage) => new()
    {
        RepoPath = request.Repo,
        Cwd = request.Cwd,
        Verb = request.Verb,
        Rev = request.Rev,
        OtherRev = request.OtherRev,
        Paths = request.Paths ?? (request.Path is null ? [] : [request.Path]),
        Line = request.Line,
        Staged = request.Staged,
        CommitMessage = commitMessage,
    };

    /// <summary>The target a queued item stores, with its repository root as both the repository
    /// and the folder its repo-relative paths resolve against.</summary>
    public static ControlTarget FromItem(CmdrQueueItem item) => new()
    {
        RepoPath = item.RepoRoot,
        Cwd = item.RepoRoot,
        Verb = item.Target,
        Rev = item.Rev,
        OtherRev = item.OtherRev,
        Paths = item.Paths,
        Line = item.Line,
        Staged = item.Staged,
        CommitMessage = item.CommitMessage,
    };
}

/// <summary>Timing knobs of <see cref="ControlNavigator"/>; tests shorten them.</summary>
public sealed record ControlNavigatorOptions
{
    /// <summary>How long to wait for a freshly opened repository's graph to paint before navigating
    /// anyway; the graph can only reveal a commit once it is listening.</summary>
    public TimeSpan GraphPaintWait { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How long to wait, after proposing a commit message, for the commit box to take it
    /// before reporting that it is only being offered.</summary>
    public TimeSpan DraftSettleTime { get; init; } = TimeSpan.FromMilliseconds(400);
}

/// <summary>Runs a navigation: opens or focuses the repository's tab, selects a commit, opens a
/// diff, or offers a commit message to the commit box.</summary>
public interface IControlNavigator
{
    /// <summary>Navigates to <paramref name="target"/> and answers as the control protocol would,
    /// with the repository's display name once it is open. Never brings the window forward.</summary>
    Task<(ControlResponse Response, string? RepoName)> NavigateAsync(ControlTarget target, CancellationToken ct);
}

/// <summary>
/// The navigation shared by live control requests and the user opening a queued item. It only
/// navigates: it opens or focuses a tab, selects a commit, opens a diff and offers a commit
/// message. It never writes to a repository, its index or refs, or to settings, never commits, and
/// never activates the window. Workspace changes run on the UI thread; the caller may be on any
/// thread, and the UI thread is never blocked.
/// </summary>
public sealed class ControlNavigator : IControlNavigator
{
    private readonly IWorkspaceService _workspace;
    private readonly ISettingsService _settings;
    private readonly IGitCliService _cli;
    private readonly IUiDispatcher _ui;
    private readonly ControlNavigatorOptions _options;

    public ControlNavigator(
        IWorkspaceService workspace,
        ISettingsService settings,
        IGitCliService cli,
        IUiDispatcher ui,
        ControlNavigatorOptions? options = null)
    {
        _workspace = workspace;
        _settings = settings;
        _cli = cli;
        _ui = ui;
        _options = options ?? new ControlNavigatorOptions();
    }

    public Task<(ControlResponse Response, string? RepoName)> NavigateAsync(ControlTarget target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        // From the UI thread, start on the pool so the folder check and path matching stay off it.
        return _ui.CheckAccess()
            ? Task.Run(() => NavigateCoreAsync(target, ct), ct)
            : NavigateCoreAsync(target, ct);
    }

    private async Task<(ControlResponse Response, string? RepoName)> NavigateCoreAsync(ControlTarget target, CancellationToken ct)
    {
        if (!ControlVerbs.IsQueueable(target.Verb))
            return (ControlResponse.Fail(ControlError.Usage, "That is not a navigation."), null);
        if (target.Verb == ControlVerb.ProposeCommit && string.IsNullOrEmpty(target.CommitMessage))
            return (ControlResponse.Fail(ControlError.Usage, "The commit message is empty."), null);

        var repoPath = !string.IsNullOrWhiteSpace(target.RepoPath) ? target.RepoPath : target.Cwd;
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
            return (ControlResponse.Fail(ControlError.RepoNotFound, "That folder does not exist."), null);

        RepositoryContext ctx;
        try
        {
            ctx = await OnUiAsync(() => _workspace.OpenAsync(repoPath, ct)).ConfigureAwait(false);
        }
        catch (HistoriaGitException ex)
        {
            return (ControlResponse.Fail(ControlError.RepoNotFound, ex.Message), null);
        }

        var name = ctx.DisplayName;
        var root = ctx.RepositoryPath;
        var cwd = string.IsNullOrWhiteSpace(target.Cwd) ? root : target.Cwd;

        GitObjectId? rev = null, otherRev = null;
        if (target.Verb is ControlVerb.Show or ControlVerb.Diff or ControlVerb.Compare)
        {
            if (!ControlInputValidation.TryValidateRevision(target.Rev, out var revError))
                return (ControlResponse.Fail(ControlError.RevNotFound, revError), name);
            rev = await RevisionResolver.ResolveCommitAsync(_cli, root, target.Rev!, ct).ConfigureAwait(false);
            if (rev is null)
                return (ControlResponse.Fail(ControlError.RevNotFound, $"'{target.Rev}' is not a commit in this repository."), name);
        }

        if (target.Verb == ControlVerb.Compare)
        {
            if (!ControlInputValidation.TryValidateRevision(target.OtherRev, out var headError))
                return (ControlResponse.Fail(ControlError.RevNotFound, headError), name);
            otherRev = await RevisionResolver.ResolveCommitAsync(_cli, root, target.OtherRev!, ct).ConfigureAwait(false);
            if (otherRev is null)
                return (ControlResponse.Fail(ControlError.RevNotFound, $"'{target.OtherRev}' is not a commit in this repository."), name);
        }

        string? relativePath = null;
        var firstPath = target.Paths.Count > 0 ? target.Paths[0] : null;
        if (target.Verb is ControlVerb.Diff or ControlVerb.Compare or ControlVerb.Changes
            && !ControlInputValidation.TryResolveRepoRelativePath(root, cwd, firstPath, out relativePath))
        {
            return (ControlResponse.Fail(ControlError.PathInvalid, "That path is not inside the repository."), name);
        }
        if (relativePath is not null)
            relativePath = ControlInputValidation.MatchOnDiskCase(root, relativePath);

        await OnUiAsync(() => _workspace.EnsureInitialized(ctx)).ConfigureAwait(false);
        if (target.Verb != ControlVerb.Open)
            await WaitForGraphAsync(ctx, ct).ConfigureAwait(false);

        var contextLines = _settings.Current.DiffContextLines;
        var state = ctx.State;
        var result = new ControlResponse
        {
            Ok = true,
            Repo = root,
            Commit = rev?.ToString(),
            OtherCommit = otherRev?.ToString(),
            Path = relativePath,
            Line = target.Line,
        };

        switch (target.Verb)
        {
            case ControlVerb.Open:
                break;

            case ControlVerb.Show:
                state.SetOpenDiff(null);
                state.SetSelectedCommit(rev);
                state.RequestRevealCommit(rev!.Value);
                break;

            case ControlVerb.Diff:
                state.SetSelectedCommit(rev);
                state.RequestRevealCommit(rev!.Value);
                state.SetOpenDiff(new DiffRequest
                {
                    Source = DiffSource.Commit,
                    CommitId = rev,
                    Path = relativePath,
                    ParentIndex = 1,
                    ContextLines = contextLines,
                    FocusLine = target.Line,
                });
                break;

            case ControlVerb.Compare:
                state.SetSelectedCommit(otherRev);
                state.RequestRevealCommit(otherRev!.Value);
                state.SetOpenDiff(new DiffRequest
                {
                    Source = DiffSource.Comparison,
                    CommitId = rev,
                    OtherCommitId = otherRev,
                    Path = relativePath,
                    ContextLines = contextLines,
                    FocusLine = target.Line,
                });
                break;

            case ControlVerb.Changes:
                state.SetSelectedCommit(null);
                state.SetOpenDiff(new DiffRequest
                {
                    Source = target.Staged ? DiffSource.StagedChanges : DiffSource.UnstagedChanges,
                    Path = relativePath,
                    ContextLines = contextLines,
                    FocusLine = target.Line,
                });
                break;

            case ControlVerb.ProposeCommit:
                var proposed = await ProposeAsync(ctx, target.CommitMessage!, contextLines, ct).ConfigureAwait(false);
                if (proposed is null)
                    return (ControlResponse.Fail(ControlError.NothingStaged, "Nothing is staged."), name);
                result = result with { StagedPaths = proposed.Value.StagedPaths, Draft = proposed.Value.Draft };
                break;
        }

        return (result, name);
    }

    private async Task<(IReadOnlyList<string> StagedPaths, ControlDraftOutcome Draft)?> ProposeAsync(
        RepositoryContext ctx, string message, int contextLines, CancellationToken ct)
    {
        var state = ctx.State;

        // The watcher's debounce could miss an agent's very recent `git add`, so ask for a fresh read.
        await state.RefreshStatusAsync(ct).ConfigureAwait(false);
        var staged = state.Status.Staged.Select(f => f.Path).ToList();
        if (staged.Count == 0)
            return null;

        state.SetSelectedCommit(null);
        state.SetOpenDiff(new DiffRequest
        {
            Source = DiffSource.StagedChanges,
            Path = staged[0],
            ContextLines = contextLines,
        });
        state.ProposeCommitMessage(message);

        // The commit box takes the text when it is idle and empty; otherwise it keeps the proposal
        // pending and offers it. Give it a moment to build and decide.
        var deadline = DateTime.UtcNow + _options.DraftSettleTime;
        while (state.PendingCommitMessage is not null && DateTime.UtcNow < deadline)
            await Task.Delay(25, ct).ConfigureAwait(false);

        return (staged, state.PendingCommitMessage is null ? ControlDraftOutcome.Filled : ControlDraftOutcome.Pending);
    }

    private async Task WaitForGraphAsync(RepositoryContext ctx, CancellationToken ct)
    {
        try
        {
            await ctx.FirstGraphPaint.WaitAsync(_options.GraphPaintWait, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The graph is slow or not shown (a background tab); navigate anyway.
        }
    }

    private Task OnUiAsync(Action work) => OnUiAsync(() => { work(); return Task.FromResult(true); });

    /// <summary>Runs <paramref name="work"/> on the UI thread — workspace changes raise events its
    /// controls listen to — and completes with its result without ever blocking that thread.</summary>
    private Task<T> OnUiAsync<T>(Func<Task<T>> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.Post(async () =>
        {
            try
            {
                done.TrySetResult(await work().ConfigureAwait(true));
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        });
        return done.Task;
    }
}
