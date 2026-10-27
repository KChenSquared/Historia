namespace Historia.ViewModels;

/// <summary>
/// The staging panel of one repository tab: the Changes and Staged lists with their check boxes,
/// the stage, unstage, discard and stash actions, the conflict heading, and the commit box. The
/// commit box has its own draft (kept per tab in the <see cref="ICommitDraftStore"/>), the
/// Amend and author fields, AI message generation with a model picker, and the offer bar for a
/// message a coding agent proposed. The lists are replaced wholesale whenever the repository's
/// status changes in a way that shows.
/// </summary>
public sealed partial class StagingPanelViewModel : ViewModelBase
{
    private const string CaseCollisionTooltipHeader =
        "These tracked files share one file on disk with another path that differs only in letter case, and the disk holds the other one's content. Git reports them as modified; Historia hides them because any change belongs to the other name. To check out both names, enable case sensitivity for the folder (fsutil file setCaseSensitiveInfo <folder> enable, then check the files out again) or clone inside WSL.";

    private const string AmbiguousCaseCollisionTooltip =
        "Some rows are one file on disk under names that differ only in letter case, and Historia cannot tell which name the edit belongs to. Stage all leaves them out. Stage the row whose name you edited; the other name keeps its committed content.";

    private readonly RepositoryContext _ctx;
    private readonly INotificationService _notifications;
    private readonly IDialogService _dialogs;
    private readonly ISettingsService _settings;
    private readonly IExternalLauncher _launcher;
    private readonly IStashService _stash;
    private readonly IRepoActionBus _actionBus;
    private readonly IConflictService _conflicts;
    private readonly IUnsavedWorkRegistry? _unsavedWork;
    private readonly IUiDispatcher _ui;
    private readonly IClipboardService _clipboard;

    // Checked paths per section, kept across status refreshes for the paths that still exist.
    private readonly HashSet<string> _checkedChanges = new(StringComparer.Ordinal);
    private readonly HashSet<string> _checkedStaged = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FileRowViewModel> _changesCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FileRowViewModel> _stagedCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ConflictRowViewModel> _conflictCache = new(StringComparer.Ordinal);

    private static readonly Comparison<FileStatusEntry> ByPathOrdinal = (a, b) => string.CompareOrdinal(a.Path, b.Path);

    private RepositoryStatus _status;
    private RepositoryStatus? _lastRenderedStatus;
    private bool _stashing;
    private bool _aborting;

    // Stage, unstage and discard operations in flight; the busy state lasts while any is.
    private int _busyOperations;
    private IUiTimer? _busyTimer;
    private (string Label, TimeSpan Held)? _holderAtBusyStart;
    private string _busyBaseText = "";

    // Completes when every stage or unstage operation started so far, including its status
    // refresh, has finished.
    private Task _pendingStage = Task.CompletedTask;

    /// <param name="ctx">The repository tab this panel belongs to.</param>
    public StagingPanelViewModel(
        RepositoryContext ctx,
        INotificationService notifications,
        IDialogService dialogs,
        ISettingsService settings,
        IKeyboardCommandRouter keyboard,
        IExternalLauncher launcher,
        IStashService stash,
        IRepoActionBus actionBus,
        IConflictService conflicts,
        IUiDispatcher ui,
        IUnsavedWorkRegistry? unsavedWork = null,
        IAiProfileService? aiProfiles = null,
        IAiIntegrationsPanel? aiPanel = null,
        ICommitMessageGenerator? aiGenerator = null,
        IAiModelCatalog? catalog = null,
        ICommitDraftStore? drafts = null,
        IClipboardService? clipboard = null)
    {
        _ctx = ctx;
        _notifications = notifications;
        _dialogs = dialogs;
        _settings = settings;
        _launcher = launcher;
        _stash = stash;
        _actionBus = actionBus;
        _conflicts = conflicts;
        _unsavedWork = unsavedWork;
        _ui = ui;
        _clipboard = clipboard ?? NullClipboardService.Instance;
        _aiProfiles = aiProfiles;
        _aiPanel = aiPanel;
        _aiGenerator = aiGenerator;
        _catalog = catalog;
        _drafts = drafts ?? new CommitDraftStore();

        _status = ctx.State.Status;
        MessageText = _drafts.Get(ctx.Id);
        UpdateCounter();
        Track(keyboard.Register("Ctrl+Enter", () => CommitCoreAsync(push: false)));

        Subscribe(() => ctx.State.StatusChanged += OnStatusChanged, () => ctx.State.StatusChanged -= OnStatusChanged);
        Subscribe(() => ctx.State.SelectedConflictPathChanged += OnSelectedConflictPathChanged,
            () => ctx.State.SelectedConflictPathChanged -= OnSelectedConflictPathChanged);
        Subscribe(() => ctx.State.CommitMessageProposed += OnCommitMessageProposed,
            () => ctx.State.CommitMessageProposed -= OnCommitMessageProposed);
        InitializeAi();
        RenderLists();

        if (ctx.State.PendingCommitMessage is { } pending)
            ApplyProposal(pending);
    }

    /// <summary>Identifies the repository tab; the view keys its split and message heights on it.</summary>
    public string RepoId => _ctx.Id;

    /// <summary>True for a repository with no working tree.</summary>
    public bool IsBare => _ctx.IsBare;

    /// <summary>The Changes list: conflicted files first, then unstaged and untracked ones by path.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<StagingListItem> Changes { get; private set; } = [];

    /// <summary>The Staged list, by path.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<StagingListItem> Staged { get; private set; } = [];

    [ObservableProperty]
    public partial string ChangesHeader { get; private set; } = "";

    [ObservableProperty]
    public partial string StagedHeader { get; private set; } = "";

    /// <summary>The Changes select-all box: checked for all rows, indeterminate for some.</summary>
    public bool? ChangesCheckState => TriState(_checkedChanges.Count, ChangesCount);

    /// <summary>The Staged select-all box: checked for all rows, indeterminate for some.</summary>
    public bool? StagedCheckState => TriState(_checkedStaged.Count, _status.Staged.Count);

    private int ChangesCount => _status.Unstaged.Count + _status.Untracked.Count;

    private bool CanStageChecked => !IsStagingBusy && _checkedChanges.Count > 0;

    private bool CanUnstageChecked => !IsStagingBusy && _checkedStaged.Count > 0;

    private bool IsNotStagingBusy => !IsStagingBusy;

    private bool HasChanges => ChangesCount > 0;

    private bool CanStashStaged => !_stashing && _status.Staged.Count > 0;

    private bool CanAbort => !_aborting;

    // ── Busy state ──

    /// <summary>How long a stage, unstage or discard runs before the busy indicator shows.</summary>
    public static readonly TimeSpan BusyIndicatorDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>True from the start of a stage, unstage or discard until git has finished the change; the status refresh that follows is not part of it.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StageAllChangesCommand))]
    [NotifyCanExecuteChangedFor(nameof(UnstageAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(StageCheckedCommand))]
    [NotifyCanExecuteChangedFor(nameof(UnstageCheckedCommand))]
    public partial bool IsStagingBusy { get; private set; }

    /// <summary>True once the busy state has lasted <see cref="BusyIndicatorDelay"/>; the spinner and <see cref="BusyText"/> show then.</summary>
    [ObservableProperty]
    public partial bool ShowBusyIndicator { get; private set; }

    /// <summary>What the busy operation is doing, such as <c>Staging 12 files…</c>, or the operation it waits for while another one holds the repository's write lock.</summary>
    [ObservableProperty]
    public partial string BusyText { get; private set; } = "";

    private void BeginBusy(string text)
    {
        _busyBaseText = text;
        if (_busyOperations++ > 0)
        {
            BusyText = ComposeBusyText();
            return;
        }

        _holderAtBusyStart = _ctx.CurrentWriteHolder;
        BusyText = ComposeBusyText();
        IsStagingBusy = true;
        _busyTimer = _ui.CreateTimer(BusyIndicatorDelay, OnBusyTick, UiPriority.Normal);
        _busyTimer.Start();
    }

    private void EndBusy()
    {
        if (--_busyOperations > 0)
            return;

        _busyTimer?.Stop();
        _busyTimer?.Dispose();
        _busyTimer = null;
        _holderAtBusyStart = null;
        IsStagingBusy = false;
        ShowBusyIndicator = false;
        BusyText = "";
    }

    private void SetBusyText(string text)
    {
        _busyBaseText = text;
        BusyText = ComposeBusyText();
    }

    // Repeats until the busy state ends, so the text follows the write lock's holder.
    private void OnBusyTick()
    {
        if (_busyOperations == 0 || IsDisposed)
            return;
        ShowBusyIndicator = true;
        BusyText = ComposeBusyText();
    }

    // The write lock's holder counts as another operation when it was already holding the lock as
    // the busy state began and still is: the same label, held for at least as long.
    private string ComposeBusyText()
        => _holderAtBusyStart is { } started
           && _ctx.CurrentWriteHolder is { } now
           && string.Equals(now.Label, started.Label, StringComparison.Ordinal)
           && now.Held >= started.Held
            ? $"Waiting for {DescribeHolder(now.Label)}…"
            : _busyBaseText;

    /// <summary>
    /// Turns a write-lock holder label into words for the user. A label is an explicit name such as
    /// <c>Refresh index</c> or a call-site name such as <c>GitGateway.WorkingTree.CommitAsync</c>; the
    /// text after the last dot loses its <c>Async</c> suffix and its PascalCase words are separated
    /// (<c>Commit</c>, <c>Stage all</c>).
    /// </summary>
    public static string DescribeHolder(string label)
    {
        var name = label.AsSpan();
        var dot = name.LastIndexOf('.');
        if (dot >= 0 && dot < name.Length - 1)
            name = name[(dot + 1)..];
        if (name.Length > "Async".Length && name.EndsWith("Async", StringComparison.Ordinal))
            name = name[..^"Async".Length];
        name = name.Trim();
        if (name.IsEmpty)
            return label;

        var text = new System.Text.StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            var c = name[i];
            var startsWord = i > 0 && char.IsUpper(c) && char.IsLower(name[i - 1]);
            if (startsWord)
            {
                text.Append(' ');
                // Later words are lower case unless they are an acronym (the next letter is upper too).
                var acronym = i + 1 < name.Length && char.IsUpper(name[i + 1]);
                text.Append(acronym ? c : char.ToLowerInvariant(c));
            }
            else
            {
                text.Append(c);
            }
        }

        return text.ToString();
    }

    private static string DescribeBusy(string verb, int count)
        => count switch
        {
            0 => $"{verb} changes…",
            1 => $"{verb} 1 file…",
            _ => $"{verb} {count} files…",
        };

    // ── Status events ──

    private void OnStatusChanged(object? sender, StatusChangedEventArgs e)
        => OnUi(() =>
        {
            if (IsDisposed)
                return;
            if (_lastRenderedStatus is not null && StatusEquivalent(_lastRenderedStatus, e.Status))
                return;
            _status = e.Status;
            RenderLists();
        });

    private void OnSelectedConflictPathChanged(object? sender, SelectedConflictPathChangedEventArgs e)
        => OnUi(() =>
        {
            if (!IsDisposed)
                UpdateConflictedRowHighlights();
        });

    private void OnUi(Action action)
    {
        if (_ui.CheckAccess())
            action();
        else
            _ui.Post(action);
    }

    /// <summary>Takes the repository's latest status into the lists when it differs from the one shown.</summary>
    private void AdoptCurrentStatus()
    {
        var current = _ctx.State.Status;
        if (IsDisposed || (_lastRenderedStatus is not null && StatusEquivalent(_lastRenderedStatus, current)))
            return;
        _status = current;
        RenderLists();
    }

    /// <summary>True when two statuses list the same paths with the same kinds, so the lists need no change.</summary>
    public static bool StatusEquivalent(RepositoryStatus a, RepositoryStatus b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Unstaged.Count != b.Unstaged.Count || a.Untracked.Count != b.Untracked.Count
            || a.Staged.Count != b.Staged.Count || a.Conflicted.Count != b.Conflicted.Count
            || a.HiddenCaseCollisions.Count != b.HiddenCaseCollisions.Count
            || a.AmbiguousCaseCollisions.Count != b.AmbiguousCaseCollisions.Count)
            return false;

        for (int i = 0; i < a.Unstaged.Count; i++)
            if (a.Unstaged[i].Path != b.Unstaged[i].Path || a.Unstaged[i].Kind != b.Unstaged[i].Kind) return false;
        for (int i = 0; i < a.Untracked.Count; i++)
            if (a.Untracked[i].Path != b.Untracked[i].Path || a.Untracked[i].Kind != b.Untracked[i].Kind) return false;
        for (int i = 0; i < a.Staged.Count; i++)
            if (a.Staged[i].Path != b.Staged[i].Path || a.Staged[i].Kind != b.Staged[i].Kind) return false;
        for (int i = 0; i < a.Conflicted.Count; i++)
            if (a.Conflicted[i].Path != b.Conflicted[i].Path || a.Conflicted[i].Kind != b.Conflicted[i].Kind) return false;
        for (int i = 0; i < a.HiddenCaseCollisions.Count; i++)
            if (!string.Equals(a.HiddenCaseCollisions[i], b.HiddenCaseCollisions[i], StringComparison.Ordinal)) return false;
        for (int i = 0; i < a.AmbiguousCaseCollisions.Count; i++)
            if (!string.Equals(a.AmbiguousCaseCollisions[i], b.AmbiguousCaseCollisions[i], StringComparison.Ordinal)) return false;

        return true;
    }

    // ── Lists ──

    private void RenderLists()
    {
        var changes = new List<FileStatusEntry>(_status.Unstaged.Count + _status.Untracked.Count);
        changes.AddRange(_status.Unstaged);
        changes.AddRange(_status.Untracked);
        changes.Sort(ByPathOrdinal);
        var staged = new List<FileStatusEntry>(_status.Staged);
        staged.Sort(ByPathOrdinal);
        var conflicted = new List<FileStatusEntry>(_status.Conflicted);
        conflicted.Sort(ByPathOrdinal);

        // Drop checks for paths that no longer exist in the section.
        _checkedChanges.IntersectWith(changes.Select(f => f.Path));
        _checkedStaged.IntersectWith(staged.Select(f => f.Path));

        ChangesHeader = $"Changes ({changes.Count + conflicted.Count})";
        StagedHeader = $"Staged ({staged.Count})";

        var conflictedPaths = new HashSet<string>(conflicted.Select(f => f.Path), StringComparer.Ordinal);
        foreach (var stale in _conflictCache.Keys.Where(k => !conflictedPaths.Contains(k)).ToList())
            _conflictCache.Remove(stale);

        var changesItems = new List<StagingListItem>(changes.Count + conflicted.Count + 2);
        if (conflicted.Count > 0)
        {
            var pending = _ctx.State.PendingOperation;
            var abortable = pending is OperationState.Merging or OperationState.Rebasing
                or OperationState.CherryPicking or OperationState.Reverting;
            var what = pending switch
            {
                OperationState.Rebasing => "rebase",
                OperationState.CherryPicking => "cherry-pick",
                OperationState.Reverting => "revert",
                _ => "merge",
            };
            changesItems.Add(new ConflictHeaderRowViewModel(AbortOperationCommand, abortable, $"Abort the in-progress {what}"));

            foreach (var f in conflicted)
            {
                if (!_conflictCache.TryGetValue(f.Path, out var row))
                    _conflictCache[f.Path] = row = new ConflictRowViewModel(this, f.Path);
                changesItems.Add(row);
            }
        }

        changesItems.AddRange(BuildRows(changes, _checkedChanges, isStaged: false, _changesCache));
        if (changesItems.Count == 0)
            changesItems.Add(new StagingNoteRowViewModel("No changes."));

        var hiddenCount = _status.HiddenCaseCollisions.Count;
        var ambiguousCount = _status.AmbiguousCaseCollisions.Count;
        if (hiddenCount > 0 || ambiguousCount > 0)
        {
            var notes = new List<string>(2);
            if (hiddenCount > 0)
                notes.Add(hiddenCount == 1 ? "1 case-collision file hidden" : $"{hiddenCount} case-collision files hidden");
            if (ambiguousCount > 0)
                notes.Add(ambiguousCount == 1 ? "1 case-collision row left out of Stage all" : $"{ambiguousCount} case-collision rows left out of Stage all");
            changesItems.Add(new StagingNoteRowViewModel(
                string.Join(" · ", notes),
                FormatCaseCollisionTooltip(_status.HiddenCaseCollisions, ambiguousCount),
                isFootnote: true));
        }

        var stagedItems = new List<StagingListItem>(BuildRows(staged, _checkedStaged, isStaged: true, _stagedCache));
        if (stagedItems.Count == 0)
            stagedItems.Add(new StagingNoteRowViewModel("Nothing staged."));

        Changes = changesItems;
        Staged = stagedItems;
        UpdateConflictedRowHighlights();

        _lastRenderedStatus = _status;
        RefreshSelectionState();
    }

    private List<FileRowViewModel> BuildRows(
        IReadOnlyList<FileStatusEntry> files,
        HashSet<string> checkedSet,
        bool isStaged,
        Dictionary<string, FileRowViewModel> cache)
    {
        if (files.Count == 0)
        {
            cache.Clear();
            return [];
        }

        var currentPaths = new HashSet<string>(files.Select(f => f.Path), StringComparer.Ordinal);
        foreach (var stale in cache.Keys.Where(k => !currentPaths.Contains(k)).ToList())
            cache.Remove(stale);

        var rows = new List<FileRowViewModel>(files.Count);
        foreach (var file in files)
        {
            if (cache.TryGetValue(file.Path, out var cached) && cached.Entry == file)
            {
                cached.SetCheckedFromPanel(checkedSet.Contains(file.Path));
                rows.Add(cached);
                continue;
            }

            var row = new FileRowViewModel(this, file, isStaged, checkedSet.Contains(file.Path));
            cache[file.Path] = row;
            rows.Add(row);
        }

        return rows;
    }

    private static string FormatCaseCollisionTooltip(IReadOnlyList<string> hiddenPaths, int ambiguousCount)
    {
        var parts = new List<string>(2);
        if (hiddenPaths.Count > 0)
        {
            var lines = hiddenPaths.Take(10).ToList();
            if (hiddenPaths.Count > 10)
                lines.Add($"+{hiddenPaths.Count - 10} more");
            parts.Add($"{CaseCollisionTooltipHeader}\n\n{string.Join('\n', lines)}");
        }
        if (ambiguousCount > 0)
            parts.Add(AmbiguousCaseCollisionTooltip);

        return string.Join("\n\n", parts);
    }

    private void UpdateConflictedRowHighlights()
    {
        var selected = _ctx.State.SelectedConflictPath;
        foreach (var (path, row) in _conflictCache)
            row.IsActive = selected is not null && string.Equals(path, selected, StringComparison.Ordinal);
    }

    // ── Row actions ──

    internal void OpenDiff(FileRowViewModel row)
        => _ctx.State.SetOpenDiff(new DiffRequest
        {
            Source = row.IsStaged ? DiffSource.StagedChanges : DiffSource.UnstagedChanges,
            Path = row.Path,
            ContextLines = _settings.Current.DiffContextLines,
        });

    internal void OpenConflict(string path)
    {
        _ctx.State.SetOpenDiff(null);
        _ctx.State.SetConflictResolverClosed(false);
        _ctx.State.SetSelectedConflictPath(path);
    }

    internal void RevealPath(string path)
    {
        if (!IsBare)
            _launcher.RevealInFileManager(System.IO.Path.Combine(_ctx.RepositoryPath, path));
    }

    internal async Task CopyPathAsync(string path)
    {
        if (IsBare)
            return;
        try
        {
            await _clipboard.SetTextAsync(System.IO.Path.Combine(_ctx.RepositoryPath, path));
        }
        catch (Exception ex)
        {
            AppLog.Write(AppLogLevel.Warn, "Staging", "Copying a file path failed.", ex);
        }
    }

    internal void OnRowChecked(FileRowViewModel row, bool isChecked)
    {
        var set = row.IsStaged ? _checkedStaged : _checkedChanges;
        if (isChecked) set.Add(row.Path);
        else set.Remove(row.Path);
        RefreshSelectionState();
    }

    // ── Selection and the stage / unstage arrows ──

    private void RefreshSelectionState()
    {
        StageCheckedCommand.NotifyCanExecuteChanged();
        UnstageCheckedCommand.NotifyCanExecuteChanged();
        StashStagedCommand.NotifyCanExecuteChanged();
        DiscardAllChangesCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ChangesCheckState));
        OnPropertyChanged(nameof(StagedCheckState));
    }

    private static bool? TriState(int chosen, int total)
        => total == 0 || chosen == 0 ? false : chosen >= total ? true : null;

    /// <summary>Checks every row of the Changes list when <paramref name="select"/> is true, and clears all checks otherwise.</summary>
    [RelayCommand]
    private void SelectAllChanges(bool select)
    {
        var all = _status.Unstaged.Concat(_status.Untracked).Select(f => f.Path);
        SetAllChecked(_checkedChanges, all, _changesCache, select);
    }

    /// <summary>Checks every row of the Staged list when <paramref name="select"/> is true, and clears all checks otherwise.</summary>
    [RelayCommand]
    private void SelectAllStaged(bool select)
        => SetAllChecked(_checkedStaged, _status.Staged.Select(f => f.Path), _stagedCache, select);

    private void SetAllChecked(HashSet<string> set, IEnumerable<string> all, Dictionary<string, FileRowViewModel> rows, bool select)
    {
        set.Clear();
        if (select)
            foreach (var p in all) set.Add(p);
        foreach (var row in rows.Values)
            row.SetCheckedFromPanel(set.Contains(row.Path));
        RefreshSelectionState();
    }

    // ── Stage, unstage, discard ──

    /// <summary>Stages the checked rows of the Changes list.</summary>
    [RelayCommand(CanExecute = nameof(CanStageChecked))]
    private Task StageCheckedAsync() => MoveCheckedAsync(stage: true);

    /// <summary>Unstages the checked rows of the Staged list.</summary>
    [RelayCommand(CanExecute = nameof(CanUnstageChecked))]
    private Task UnstageCheckedAsync() => MoveCheckedAsync(stage: false);

    private async Task MoveCheckedAsync(bool stage)
    {
        if (IsStagingBusy) return;
        var checkedPaths = (stage ? _checkedChanges : _checkedStaged).ToList();
        if (checkedPaths.Count == 0) return;
        (stage ? _checkedChanges : _checkedStaged).Clear();
        RefreshSelectionState();

        // A checked rename must move both halves together — staging (or unstaging) only the
        // new path leaves the old path's deletion behind as a separate, unstaged entry instead of
        // one Renamed entry. Expand at send time only: the checked sets themselves stay keyed on
        // visible-row paths, which the tri-state maths counts.
        var pool = stage
            ? (IReadOnlyList<FileStatusEntry>)[.. _status.Unstaged, .. _status.Untracked]
            : _status.Staged;
        var paths = StagePathExpansion.ForStaging(pool, checkedPaths);

        await Run(DescribeBusy(stage ? "Staging" : "Unstaging", checkedPaths.Count), async () =>
        {
            await (stage
                ? _ctx.Gateway.StageAsync(paths, _ctx.Lifetime)
                : _ctx.Gateway.UnstageAsync(paths, _ctx.Lifetime));
            return true;
        });
    }

    /// <summary>Stages every unstaged and untracked file except the case-collision rows that cannot be attributed to one name.</summary>
    [RelayCommand(CanExecute = nameof(IsNotStagingBusy))]
    private Task StageAllChangesAsync() => BulkAsync(unstage: false);

    /// <summary>Unstages everything in the Staged list.</summary>
    [RelayCommand(CanExecute = nameof(IsNotStagingBusy))]
    private Task UnstageAllAsync() => BulkAsync(unstage: true);

    private static IReadOnlyList<FileStatusEntry> BulkFiles(RepositoryStatus status, bool unstage)
    {
        if (unstage)
            return status.Staged;

        // Ambiguous case-collision rows are one disk file under several names; staging them together
        // would write the edit into every entry, so only an explicit row stage takes one of them.
        var ambiguous = new HashSet<string>(status.AmbiguousCaseCollisions, StringComparer.Ordinal);
        return status.Unstaged.Where(u => !ambiguous.Contains(u.Path)).Concat(status.Untracked).ToList();
    }

    private async Task BulkAsync(bool unstage)
    {
        if (IsStagingBusy) return;
        var verb = unstage ? "Unstaging" : "Staging";
        await Run(DescribeBusy(verb, BulkFiles(_status, unstage).Count), async () =>
        {
            // The click acts on the repository as it is now: a status run already under way, or one
            // the click queues behind it, finishes first, so the list is no older than the click.
            await _ctx.State.RefreshStatusAsync(_ctx.Lifetime);
            var files = BulkFiles(_ctx.State.Status, unstage);
            if (files.Count == 0)
            {
                _notifications.Publish(NotificationSeverity.Info, unstage ? "Nothing to unstage" : "Nothing to stage");
                return false;
            }

            SetBusyText(DescribeBusy(verb, files.Count));
            // Same expansion as MoveCheckedAsync — a bulk stage/unstage must move both halves of
            // any rename in the list, not just the new path.
            var paths = StagePathExpansion.ForStaging(files, files.Select(f => f.Path));
            await (unstage ? _ctx.Gateway.UnstageAsync(paths, _ctx.Lifetime) : _ctx.Gateway.StageAsync(paths, _ctx.Lifetime));
            return true;
        });
    }

    /// <summary>Discards one file's unstaged changes after the user confirms; staged changes stay.</summary>
    [RelayCommand]
    private async Task DiscardAsync(FileRowViewModel? row)
    {
        if (row is null || row.IsStaged)
            return;
        var f = row.Entry;
        try
        {
            if (!await _dialogs.ConfirmDestructiveAsync(
                "Discard changes",
                $"Discard unstaged changes to '{f.Path}'? Staged changes are kept. This cannot be undone.",
                "Discard"))
                return;
            _checkedChanges.Remove(f.Path);
            _checkedStaged.Remove(f.Path);
            if (f.OldPath is not null)
            {
                _checkedChanges.Remove(f.OldPath);
                _checkedStaged.Remove(f.OldPath);
            }
            RefreshSelectionState();
            if (_ctx.State.OpenDiff?.Path == f.Path || (f.OldPath is not null && _ctx.State.OpenDiff?.Path == f.OldPath))
                _ctx.State.SetOpenDiff(null);

            var paths = f.OldPath is not null ? new[] { f.Path, f.OldPath } : new[] { f.Path };
            await Run(DescribeBusy("Discarding", paths.Length), async () =>
            {
                await _ctx.Gateway.DiscardAsync(paths, _ctx.Lifetime);
                return true;
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage("Discard"), ex.NotificationDetails());
        }
    }

    /// <summary>Discards every unstaged change and moves untracked files to the recycle bin, after the user confirms.</summary>
    [RelayCommand(CanExecute = nameof(HasChanges))]
    private async Task DiscardAllChangesAsync()
    {
        var files = _status.Unstaged.Concat(_status.Untracked);
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in files)
        {
            if (seen.Add(f.Path))
                paths.Add(f.Path);
            if (f.OldPath is not null && seen.Add(f.OldPath))
                paths.Add(f.OldPath);
        }

        if (paths.Count == 0) return;
        try
        {
            if (!await _dialogs.ConfirmDestructiveAsync(
                "Discard all changes",
                "Discard all unstaged changes? Untracked files are moved to the recycle bin; "
                + "changes to tracked files cannot be undone.",
                "Discard all"))
                return;
            _checkedChanges.Clear();
            RefreshSelectionState();
            if (_ctx.State.OpenDiff is { } diff && (diff.Source == DiffSource.UnstagedChanges || (diff.Path is not null && seen.Contains(diff.Path))))
                _ctx.State.SetOpenDiff(null);
            await Run(DescribeBusy("Discarding", paths.Count), async () =>
            {
                await _ctx.Gateway.DiscardAsync(paths, _ctx.Lifetime);
                return true;
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage("Discard all"), ex.NotificationDetails());
        }
    }

    /// <summary>
    /// Runs one index or working-tree change with the busy state shown. The command ends when
    /// <paramref name="op"/> returns; when it answers true the status refresh continues in the
    /// background, and a commit waits for it.
    /// </summary>
    private async Task Run(string busyText, Func<Task<bool>> op)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingStage = _pendingStage.IsCompleted ? done.Task : Task.WhenAll(_pendingStage, done.Task);
        var refresh = false;
        BeginBusy(busyText);
        try { refresh = await op(); }
        catch (Exception ex) { PublishFailure(ex); }
        finally { EndBusy(); }

        if (refresh)
            _ = RefreshInBackgroundAsync(done);
        else
            done.TrySetResult();
    }

    // Not awaited by the command, so every outcome ends here: a failure reaches the user and the
    // task never faults.
    private async Task RefreshInBackgroundAsync(TaskCompletionSource done)
    {
        try { await _ctx.State.RefreshStatusAsync(_ctx.Lifetime); }
        catch (Exception ex) { PublishFailure(ex); }
        finally { done.TrySetResult(); }
    }

    private void PublishFailure(Exception ex)
    {
        // A tab closing mid-operation cancels the refresh; there is no one left to tell.
        if (ex is OperationCanceledException && _ctx.Lifetime.IsCancellationRequested)
            return;
        _notifications.Publish(NotificationSeverity.Error, ex.Message, ex.NotificationDetails());
    }

    // ── Abort and stash ──

    /// <summary>Aborts the merge, rebase, cherry-pick or revert in progress after the user confirms,
    /// discarding the conflict resolutions made so far.</summary>
    [RelayCommand(CanExecute = nameof(CanAbort))]
    private async Task AbortOperationAsync()
    {
        if (_aborting)
            return;

        try
        {
            if (!await _dialogs.ConfirmDestructiveAsync(
                    "Abort operation",
                    "Abort the in-progress merge / rebase / cherry-pick? Conflict resolutions and unsaved edits made so far will be discarded.",
                    "Abort"))
            {
                return;
            }

            if (_aborting)
                return;

            _aborting = true;
            AbortOperationCommand.NotifyCanExecuteChanged();
            try
            {
                await _conflicts.AbortOperationAsync(_ctx, _ctx.Lifetime);
                foreach (var w in _unsavedWork?.For(_ctx).ToList() ?? [])
                    w.Discard();
                if (_ctx.State.PendingOperation == OperationState.None)
                    _ctx.State.SetKeepConflictResolverOpen(false);
            }
            finally
            {
                _aborting = false;
                AbortOperationCommand.NotifyCanExecuteChanged();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage("Abort"), ex.NotificationDetails());
        }
    }

    /// <summary>Stashes only the staged set; unstaged working-tree edits are left in place.</summary>
    [RelayCommand(CanExecute = nameof(CanStashStaged))]
    private async Task StashStagedAsync()
    {
        if (_stashing || _status.Staged.Count == 0)
            return;
        _stashing = true;
        StashStagedCommand.NotifyCanExecuteChanged();
        try
        {
            var stashed = await _stash.StashStagedAsync(_ctx, null, _ctx.Lifetime);
            _notifications.Publish(
                stashed ? NotificationSeverity.Success : NotificationSeverity.Info,
                stashed ? "Stashed the staged changes." : "Nothing staged to stash.");
        }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage("Stash staged"), ex.NotificationDetails());
        }
        finally
        {
            _stashing = false;
            RefreshSelectionState();
        }
    }
}
