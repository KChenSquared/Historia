namespace Historia.Services.ExternalControl;

/// <summary>Timing knobs of <see cref="ExternalControlHost"/>; tests shorten them.</summary>
public sealed record ExternalControlHostOptions
{
    /// <summary>Overall time one request may take, including waiting for an earlier request.</summary>
    public TimeSpan RequestBudget { get; init; } = TimeSpan.FromSeconds(25);
}

/// <summary>
/// Executes control requests from <c>historia-cmdr</c>. A live request navigates through
/// <see cref="IControlNavigator"/> — it opens or focuses a repository tab, selects a commit, opens a
/// diff, and for a commit proposal offers text to the commit box — then brings the window forward
/// when asked. A queued request is checked with read-only git and added to the agent queue for the
/// user to open later, without touching any tab or the window. Nothing here writes to a repository,
/// its index or refs, or to settings, and nothing commits. Requests run one at a time.
/// </summary>
public sealed class ExternalControlHost
{
    private readonly IWorkspaceService _workspace;
    private readonly ISettingsService _settings;
    private readonly IGitCliService _cli;
    private readonly IUiDispatcher _ui;
    private readonly IAppLog _log;
    private readonly IControlNavigator _navigator;
    private readonly ICmdrQueueService _queue;
    private readonly Action _activateWindow;
    private readonly ExternalControlHostOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ExternalControlHost(
        IWorkspaceService workspace,
        ISettingsService settings,
        IGitCliService cli,
        IUiDispatcher ui,
        IAppLog log,
        IControlNavigator navigator,
        ICmdrQueueService queue,
        Action activateWindow,
        ExternalControlHostOptions? options = null)
    {
        _workspace = workspace;
        _settings = settings;
        _cli = cli;
        _ui = ui;
        _log = log;
        _navigator = navigator;
        _queue = queue;
        _activateWindow = activateWindow;
        _options = options ?? new ExternalControlHostOptions();
    }

    public async Task<ControlResponse> HandleAsync(ControlRequest request, CancellationToken ct)
    {
        var verb = request.Verb == ControlVerb.Queue && request.Target is { } target
            ? $"queue {ControlVerbs.ToWire(target)}"
            : ControlVerbs.ToWire(request.Verb);
        ControlResponse response;
        string? repoName = null;
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(_options.RequestBudget);

            await _gate.WaitAsync(budget.Token).ConfigureAwait(false);
            try
            {
                (response, repoName) = await ExecuteAsync(request, budget.Token).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            response = ControlResponse.Fail(ControlError.Timeout, "Historia did not finish in time.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Error("control", $"{verb} failed", ex);
            response = ControlResponse.Fail(ControlError.Internal, "Historia could not handle the request.");
        }

        var outcome = response.Ok
            ? "ok"
            : JsonNamingPolicy.KebabCaseLower.ConvertName((response.Error ?? ControlError.Internal).ToString());
        _log.Info("control", repoName is null ? $"{verb} -> {outcome}" : $"{verb} {repoName} -> {outcome}");
        return response;
    }

    private async Task<(ControlResponse Response, string? RepoName)> ExecuteAsync(ControlRequest request, CancellationToken ct)
    {
        if (!_settings.Current.ExternalControlEnabled)
            return (ControlResponse.Fail(ControlError.Disabled, "External control is turned off in Settings."), null);

        switch (request.Verb)
        {
            case ControlVerb.Ping:
                return (ControlResponse.Success(), null);
            case ControlVerb.Selection:
                return (await OnUiAsync(ReadSelection).ConfigureAwait(false), null);
            case ControlVerb.Queue:
                return await EnqueueAsync(request, ct).ConfigureAwait(false);
        }

        string? message = null;
        if (request.Verb == ControlVerb.ProposeCommit
            && !ControlInputValidation.NormalizeCommitMessage(request.CommitMessage, out message, out var messageError))
        {
            return (ControlResponse.Fail(ControlError.Usage, messageError), null);
        }

        var navigated = await _navigator.NavigateAsync(ControlTarget.FromRequest(request, message), ct).ConfigureAwait(false);

        if (navigated.Response.Ok && request.WantsFocus)
            _ui.Post(_activateWindow);

        return navigated;
    }

    /// <summary>
    /// Checks a queued request with read-only git and adds it to the queue for the user to open
    /// later. It runs off the UI thread and touches no tab: nothing is opened, focused or fetched,
    /// and the window stays where it is.
    /// </summary>
    private async Task<(ControlResponse Response, string? RepoName)> EnqueueAsync(ControlRequest request, CancellationToken ct)
    {
        if (request.Target is not { } target || !ControlVerbs.IsQueueable(target))
            return (ControlResponse.Fail(ControlError.Usage, "A queued request needs a navigation to queue."), null);

        string? note = null;
        if (request.Note is not null && !ControlInputValidation.NormalizeNote(request.Note, out note, out var noteError))
            return (ControlResponse.Fail(ControlError.Usage, noteError), null);

        string? message = null;
        if (target == ControlVerb.ProposeCommit
            && !ControlInputValidation.NormalizeCommitMessage(request.CommitMessage, out message, out var messageError))
        {
            return (ControlResponse.Fail(ControlError.Usage, messageError), null);
        }

        var requested = request.Paths ?? (request.Path is null ? [] : [request.Path]);
        if (requested.Count > ControlArgs.MaxQueuedFiles || (requested.Count > 1 && target != ControlVerb.Changes))
            return (ControlResponse.Fail(ControlError.Usage, $"Only a change list takes several files, at most {ControlArgs.MaxQueuedFiles}."), null);

        var folder = !string.IsNullOrWhiteSpace(request.Repo) ? request.Repo : request.Cwd;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return (ControlResponse.Fail(ControlError.RepoNotFound, "That folder does not exist."), null);

        var root = await RevisionResolver.ReadTopLevelAsync(_cli, folder, ct).ConfigureAwait(false);
        if (root is null)
            return (ControlResponse.Fail(ControlError.RepoNotFound, "That folder is not inside a Git repository."), null);

        var name = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(root));
        if (string.IsNullOrEmpty(name))
            name = root;
        var cwd = string.IsNullOrWhiteSpace(request.Cwd) ? root : request.Cwd;

        var (rev, otherRev, revError) = await RevisionResolver.ResolveTargetRevisionsAsync(
            _cli, root, target, request.Rev, request.OtherRev, ct).ConfigureAwait(false);
        if (revError is not null)
            return (ControlResponse.Fail(ControlError.RevNotFound, revError), name);

        string? subject = null;
        if (target is ControlVerb.Show or ControlVerb.Diff)
            subject = await RevisionResolver.ReadSubjectAsync(_cli, root, rev!.Value, ct).ConfigureAwait(false);

        var paths = new List<string>();
        if (target is ControlVerb.Diff or ControlVerb.Compare or ControlVerb.Changes)
        {
            if (requested.Count == 0)
                return (ControlResponse.Fail(ControlError.PathInvalid, "That path is not inside the repository."), name);
            foreach (var path in requested)
            {
                if (!ControlInputValidation.TryResolveRepoRelativePath(root, cwd, path, out var relative))
                    return (ControlResponse.Fail(ControlError.PathInvalid, $"'{path}' is not inside the repository."), name);
                paths.Add(ControlInputValidation.MatchOnDiskCase(root, relative));
            }
        }

        IReadOnlyList<string> stagedPaths = [];
        if (target == ControlVerb.ProposeCommit)
        {
            var staged = await RevisionResolver.ReadStagedPathsAsync(_cli, root, ct).ConfigureAwait(false);
            if (staged is null)
                return (ControlResponse.Fail(ControlError.Internal, "Historia could not read the staged files."), name);
            if (staged.Count == 0)
                return (ControlResponse.Fail(ControlError.NothingStaged, "Nothing is staged."), name);
            stagedPaths = staged;
        }

        var item = new CmdrQueueItem
        {
            RepoRoot = root,
            RepoName = name,
            Target = target,
            Rev = rev?.ToString(),
            OtherRev = otherRev?.ToString(),
            Paths = paths,
            Line = request.Line,
            Staged = request.Staged,
            CommitMessage = message,
            CommitSubject = subject,
            StagedPaths = stagedPaths,
            Note = note,
        };
        _queue.Enqueue(item);

        return (new ControlResponse
        {
            Ok = true,
            Queued = true,
            QueueId = item.Id,
            Repo = root,
            Commit = item.Rev,
            OtherCommit = item.OtherRev,
            Path = paths.Count > 0 ? paths[0] : null,
            Line = request.Line,
            StagedPaths = target == ControlVerb.ProposeCommit ? stagedPaths : null,
        }, name);
    }

    private ControlResponse ReadSelection()
    {
        var ctx = _workspace.Focused;
        if (ctx is null)
            return new ControlResponse { Ok = true, Selection = new ControlSelection() };

        var state = ctx.State;
        var diff = state.OpenDiff;
        return new ControlResponse
        {
            Ok = true,
            Selection = new ControlSelection
            {
                Repo = ctx.RepositoryPath,
                Commit = state.SelectedCommitId?.ToString(),
                Diff = diff is null
                    ? null
                    : new ControlSelectionDiff
                    {
                        Source = diff.Source switch
                        {
                            DiffSource.Commit => "commit",
                            DiffSource.Comparison => "comparison",
                            DiffSource.StagedChanges => "staged",
                            _ => "unstaged",
                        },
                        Path = diff.Path,
                        Commit = diff.CommitId?.ToString(),
                        OtherCommit = diff.OtherCommitId?.ToString(),
                    },
            },
        };
    }

    /// <summary>Runs <paramref name="work"/> on the UI thread, where the workspace's state lives,
    /// and completes with its result without ever blocking that thread.</summary>
    private Task<T> OnUiAsync<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.Post(() =>
        {
            try
            {
                done.TrySetResult(work());
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        });
        return done.Task;
    }
}
