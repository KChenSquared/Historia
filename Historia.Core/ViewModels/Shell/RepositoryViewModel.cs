namespace Historia.ViewModels;

/// <summary>What the repository view's body shows.</summary>
public enum RepositoryDetailMode
{
    /// <summary>No repository is open.</summary>
    None,

    /// <summary>The working-copy staging panel.</summary>
    Staging,

    /// <summary>The detail of the selected commit.</summary>
    CommitDetail,
}

/// <summary>
/// The repository detail pane: a top bar with the current branch and ahead/behind count (or, over
/// a commit's detail, a button back to the working copy), the banner for an operation that is in
/// progress with no conflicts left, the inline progress of a running Fetch / Pull / Push, and
/// which body shows (staging or the selected commit). It follows the focused repository and
/// recomputes from the repository's state events.
/// </summary>
public sealed partial class RepositoryViewModel : ViewModelBase
{
    private readonly IWorkspaceService _workspace;
    private readonly INotificationService _notifications;
    private readonly IDialogService _dialogs;
    private readonly IRepoNetworkActionRunner _networkRunner;
    private readonly IConflictService _conflicts;
    private readonly IUnsavedWorkRegistry? _unsavedWork;
    private readonly IUiDispatcher _ui;

    private RepositoryContext? _repo;

    /// <param name="unsavedWork">When given, unsaved work of the repository (a closed resolver can
    /// still hold edits) is offered for saving before an operation continues.</param>
    public RepositoryViewModel(
        IWorkspaceService workspace,
        INotificationService notifications,
        IDialogService dialogs,
        IRepoNetworkActionRunner networkRunner,
        IConflictService conflicts,
        IUiDispatcher ui,
        IUnsavedWorkRegistry? unsavedWork = null)
    {
        _workspace = workspace;
        _notifications = notifications;
        _dialogs = dialogs;
        _networkRunner = networkRunner;
        _conflicts = conflicts;
        _ui = ui;
        _unsavedWork = unsavedWork;

        Subscribe(() => _networkRunner.ProgressChanged += OnNetworkProgressChanged, () => _networkRunner.ProgressChanged -= OnNetworkProgressChanged);
        Subscribe(() => _workspace.FocusedRepositoryChanged += OnFocusChanged, () => _workspace.FocusedRepositoryChanged -= OnFocusChanged);
        Bind(_workspace.Focused);
    }

    /// <summary>Raised after every refresh, so the view can match its body to <see cref="DetailMode"/>,
    /// <see cref="SelectedCommitId"/> and <see cref="Repo"/>.</summary>
    public event EventHandler? DetailChanged;

    /// <summary>The repository this pane shows, or null when none is open.</summary>
    public RepositoryContext? Repo => _repo;

    [ObservableProperty]
    public partial string BranchText { get; private set; } = "No repository open";

    [ObservableProperty]
    public partial string AheadBehindText { get; private set; } = "";

    /// <summary>The branch and ahead/behind count show over the staging panel; over a commit's
    /// detail the working-copy button takes the bar instead.</summary>
    [ObservableProperty]
    public partial bool IsBranchBarVisible { get; private set; } = true;

    [ObservableProperty]
    public partial bool IsWorkingCopyVisible { get; private set; }

    [ObservableProperty]
    public partial string WorkingCopyText { get; private set; } = "";

    /// <summary>True when the working copy has changes; the button is then the primary one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWorkingCopyClean))]
    public partial bool HasWorkingCopyChanges { get; private set; }

    public bool IsWorkingCopyClean => !HasWorkingCopyChanges;

    [ObservableProperty]
    public partial bool IsOpBannerVisible { get; private set; }

    [ObservableProperty]
    public partial string OpBannerText { get; private set; } = "";

    /// <summary>The operation can be continued or aborted; a state such as a bisect shows the
    /// banner without the buttons.</summary>
    [ObservableProperty]
    public partial bool ShowOpActions { get; private set; }

    /// <summary>A continue or abort is running; both buttons are disabled meanwhile.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AreOpButtonsEnabled))]
    public partial bool IsOpBusy { get; private set; }

    public bool AreOpButtonsEnabled => !IsOpBusy;

    [ObservableProperty]
    public partial bool IsNetworkProgressVisible { get; private set; }

    [ObservableProperty]
    public partial string NetworkProgressText { get; private set; } = "";

    [ObservableProperty]
    public partial RepositoryDetailMode DetailMode { get; private set; }

    /// <summary>The commit whose detail shows while <see cref="DetailMode"/> is
    /// <see cref="RepositoryDetailMode.CommitDetail"/>.</summary>
    [ObservableProperty]
    public partial GitObjectId? SelectedCommitId { get; private set; }

    /// <summary>The number of distinct paths with a staged, unstaged, untracked or conflicted change.</summary>
    public static int WorkingCopyChangeCount(RepositoryStatus status)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in status.Staged) set.Add(f.Path);
        foreach (var f in status.Unstaged) set.Add(f.Path);
        foreach (var f in status.Untracked) set.Add(f.Path);
        foreach (var f in status.Conflicted) set.Add(f.Path);
        return set.Count;
    }

    protected override void OnDisposed() => DetachState();

    private void OnFocusChanged(object? sender, FocusedRepositoryChangedEventArgs e)
        => _ui.Post(() =>
        {
            if (!IsDisposed)
                Bind(e.New);
        });

    private void OnStatusChanged(object? sender, StatusChangedEventArgs e)
    {
        if (_ui.CheckAccess())
            Refresh();
        else
            _ui.Post(Refresh);
    }

    private void OnResolverClosedChanged(object? sender, EventArgs e) => _ui.Post(Refresh);

    private void OnConflictSessionChanged(object? sender, EventArgs e) => _ui.Post(Refresh);

    private void OnRefsChanged(object? sender, RefsChangedEventArgs e) => _ui.Post(Refresh);

    private void OnSelectedCommitChanged(object? sender, SelectionChangedEventArgs e) => _ui.Post(Refresh);

    // Mirrors the runner's progress into the inline box, but only while it reports for the
    // repository shown here; the runner keeps running Fetch / Pull / Push whether or not a pane
    // is mounted. Applied in place when already on the UI thread.
    private void OnNetworkProgressChanged(object? sender, RepoNetworkProgress e)
    {
        if (_ui.CheckAccess())
            ApplyNetworkProgress(e);
        else
            _ui.Post(() => ApplyNetworkProgress(e));
    }

    private void ApplyNetworkProgress(RepoNetworkProgress e)
    {
        if (IsDisposed || _repo is null || !ReferenceEquals(e.Repo, _repo))
            return;
        ShowNetworkProgress(e.Progress);
    }

    private void Bind(RepositoryContext? ctx)
    {
        DetachState();
        _repo = ctx;
        OnPropertyChanged(nameof(Repo));
        if (_repo is not null)
        {
            _repo.State.StatusChanged += OnStatusChanged;
            _repo.State.ConflictResolverClosedChanged += OnResolverClosedChanged;
            _repo.State.ConflictSessionChanged += OnConflictSessionChanged;
            _repo.State.SelectedCommitChanged += OnSelectedCommitChanged;
            _repo.State.RefsChanged += OnRefsChanged;
        }

        ShowNetworkProgress(_repo is null ? null : _networkRunner.CurrentProgress(_repo));
        Refresh();
    }

    private void DetachState()
    {
        if (_repo is null)
            return;

        _repo.State.StatusChanged -= OnStatusChanged;
        _repo.State.ConflictResolverClosedChanged -= OnResolverClosedChanged;
        _repo.State.ConflictSessionChanged -= OnConflictSessionChanged;
        _repo.State.SelectedCommitChanged -= OnSelectedCommitChanged;
        _repo.State.RefsChanged -= OnRefsChanged;
    }

    private void ShowNetworkProgress(GitProgress? progress)
    {
        if (progress is null)
        {
            IsNetworkProgressVisible = false;
            return;
        }

        NetworkProgressText = $"{progress.Phase} {(progress.Percent is { } pct ? $"{pct:0}%" : "")}".Trim();
        IsNetworkProgressVisible = true;
    }

    private void Refresh()
    {
        if (IsDisposed)
            return;

        if (_repo is null)
        {
            BranchText = "No repository open";
            AheadBehindText = "";
            IsBranchBarVisible = true;
            IsWorkingCopyVisible = false;
            IsOpBannerVisible = false;
            SelectedCommitId = null;
            DetailMode = RepositoryDetailMode.None;
            DetailChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        var state = _repo.State;
        var info = state.Repository;
        BranchText = info.IsHeadDetached ? $"HEAD detached {info.CurrentBranchName}"
            : info.IsHeadUnborn ? "Unborn HEAD — make the first commit"
            : info.CurrentBranchName ?? "";
        var status = state.Status;
        AheadBehindText = (status.AheadBy > 0 ? $"↑{status.AheadBy} " : "") + (status.BehindBy > 0 ? $"↓{status.BehindBy}" : "");

        var op = state.PendingOperation;
        var sessionActive = state.IsConflictSessionActive;
        var conflictsRemain = status.Conflicted.Count > 0;
        if (op != OperationState.None && !conflictsRemain && (!sessionActive || state.IsConflictResolverClosed))
        {
            OpBannerText = $"Repository is in {op} state with no conflicts.";
            ShowOpActions = op is OperationState.Merging or OperationState.Rebasing or OperationState.CherryPicking or OperationState.Reverting;
            IsOpBannerVisible = true;
        }
        else
        {
            IsOpBannerVisible = false;
        }

        if (state.SelectedCommitId is { } id)
        {
            var count = WorkingCopyChangeCount(status);
            WorkingCopyText = $"← Working Copy (Changes: {count})";
            HasWorkingCopyChanges = count > 0;
            IsWorkingCopyVisible = true;
            IsBranchBarVisible = false;
            SelectedCommitId = id;
            DetailMode = RepositoryDetailMode.CommitDetail;
        }
        else
        {
            IsWorkingCopyVisible = false;
            IsBranchBarVisible = true;
            SelectedCommitId = null;
            DetailMode = RepositoryDetailMode.Staging;
        }

        DetailChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ShowWorkingCopy() => _repo?.State.SetSelectedCommit(null);

    [RelayCommand]
    private void CancelNetwork()
    {
        if (_repo is not null)
            _networkRunner.Cancel(_repo);
    }

    [RelayCommand]
    private async Task ContinueAsync()
    {
        if (_repo is not { } repo || IsOpBusy)
            return;

        var busy = false;
        try
        {
            // A closed resolver can still hold edits; continuing would commit without them.
            var unsaved = _unsavedWork?.For(repo);
            if (unsaved is { Count: > 0 } && !await UnsavedWorkPrompt.ResolveAsync(unsaved, _dialogs))
                return;

            if (IsOpBusy)
                return;
            busy = IsOpBusy = true;
            await _conflicts.ContinueOperationAsync(repo);
        }
        catch (HistoriaGitException ex) when (ex.Kind == GitErrorKind.MergeConflict)
        {
            _notifications.Publish(NotificationSeverity.Warning, ex.Message);
        }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage("Continue"), ex.NotificationDetails());
        }
        finally
        {
            if (busy)
                IsOpBusy = false;
        }
    }

    [RelayCommand]
    private async Task AbortAsync()
    {
        if (_repo is not { } repo || IsOpBusy)
            return;

        var busy = false;
        try
        {
            if (!await _dialogs.ConfirmDestructiveAsync(
                    "Abort operation",
                    "Abort the in-progress merge / rebase / cherry-pick? Conflict resolutions made so far will be discarded.",
                    "Abort"))
            {
                return;
            }

            if (IsOpBusy)
                return;
            busy = IsOpBusy = true;
            await _conflicts.AbortOperationAsync(repo);
        }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage("Abort"), ex.NotificationDetails());
        }
        finally
        {
            if (busy)
                IsOpBusy = false;
        }
    }
}
