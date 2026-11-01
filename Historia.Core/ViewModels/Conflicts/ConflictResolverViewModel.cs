namespace Historia.ViewModels;

/// <summary>
/// The 3-way conflict resolver shell for one repository tab: the conflicted-file list, the file
/// being edited, the toolbar and banner state, and every action that saves, takes a side, resets,
/// continues, skips or aborts. The editor panes stay in the view, which hands them over as an
/// <see cref="IConflictEditorSurface"/>; this class decides when a file is loaded, what the buttons
/// may do and what the user is asked first. Destructive actions confirm in the method that makes
/// the destructive call. Git and disk work goes through <see cref="IConflictService"/>.
/// </summary>
public sealed partial class ConflictResolverViewModel : ViewModelBase
{
    private sealed class ConflictUnsavedWork : IUnsavedWork
    {
        private readonly ConflictResolverViewModel _owner;

        public ConflictUnsavedWork(ConflictResolverViewModel owner, RepositoryContext ctx, string path)
        {
            _owner = owner;
            Context = ctx;
            Description = $"the conflict resolution in {path}";
        }

        public RepositoryContext Context { get; }
        public string Description { get; }

        public Task<bool> SaveAsync() => _owner.SaveCurrentFileAsync(stage: false);

        public void Discard() => _owner.DiscardUnsavedState();
    }

    private readonly RepositoryContext _ctx;
    private readonly IConflictService _conflicts;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly IUiDispatcher _ui;
    private readonly IUnsavedWorkRegistry? _unsavedRegistry;
    private readonly INavigationBus? _nav;
    private readonly ISettingsService? _settings;

    // Cancelled on dispose to signal in-flight work, but not disposed so in-flight tasks
    // observing its token do not throw ObjectDisposedException. A plain CTS with no timer
    // or wait handle holds no unmanaged resources.
#pragma warning disable S2930 // no timer or wait handle to release; in-flight work keeps reading its token
    private readonly CancellationTokenSource _viewCts = new();
#pragma warning restore S2930
    private CancellationTokenSource? _switchCts;
    private int _switchGeneration;

    private readonly List<ConflictFile> _conflictedFiles = [];
    private ConflictWorkingCopy? _workingCopy;
    private IDisposable? _unsavedRegistration;
    private IConflictEditorSurface? _surface;
    private bool _saving;
    private string? _stopId;
    private bool _statusRefreshRunning;
    private bool _statusRefreshPending;

    public ConflictResolverViewModel(
        RepositoryContext ctx,
        IConflictService conflicts,
        IDialogService dialogs,
        INotificationService notifications,
        IUiDispatcher ui,
        IUnsavedWorkRegistry? unsavedRegistry = null,
        INavigationBus? nav = null,
        ISettingsService? settings = null)
    {
        _ctx = ctx;
        _conflicts = conflicts;
        _dialogs = dialogs;
        _notifications = notifications;
        _ui = ui;
        _unsavedRegistry = unsavedRegistry;
        _nav = nav;
        _settings = settings;
    }

    /// <summary>Raised each time <see cref="IsBusy"/> changes.</summary>
    public event EventHandler? BusyChanged;

    /// <summary>True while a save, take-side, reset, continue, skip or abort is running.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AreActionsEnabled))]
    [NotifyPropertyChangedFor(nameof(AreTextActionsEnabled))]
    [NotifyPropertyChangedFor(nameof(CanResolveFile))]
    [NotifyPropertyChangedFor(nameof(CanNavigate))]
    public partial bool IsBusy { get; private set; }

    /// <summary>True when the loaded file is text the panes can edit.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AreTextActionsEnabled))]
    [NotifyPropertyChangedFor(nameof(CanNavigate))]
    public partial bool IsEditable { get; private set; }

    /// <summary>The conflicted file in the panes or on its card; null before the first load and once none remains.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanResolveFile))]
    public partial ConflictFile? CurrentFile { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEditors))]
    [NotifyPropertyChangedFor(nameof(ShowAllResolvedCard))]
    [NotifyPropertyChangedFor(nameof(ShowFileCard))]
    public partial ConflictEditorMode Mode { get; private set; } = ConflictEditorMode.Editors;

    /// <summary>True while at least one file is still conflicted: Save and mark resolved is then the primary action.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContinueIsPrimary))]
    public partial bool FilesRemain { get; private set; }

    [ObservableProperty]
    public partial string OursCaption { get; private set; } = "";

    [ObservableProperty]
    public partial string TheirsCaption { get; private set; } = "";

    [ObservableProperty]
    public partial string OursCaptionToolTip { get; private set; } = "";

    [ObservableProperty]
    public partial string TheirsCaptionToolTip { get; private set; } = "";

    /// <summary>The Continue, Skip and Abort bar; shown only while an operation is pending.</summary>
    [ObservableProperty]
    public partial bool IsOperationBarVisible { get; private set; } = true;

    [ObservableProperty]
    public partial bool IsSkipVisible { get; private set; } = true;

    /// <summary>Shown when the operation ended outside Historia while edits are still unsaved.</summary>
    [ObservableProperty]
    public partial bool IsExternalEndedBannerVisible { get; private set; }

    [ObservableProperty]
    public partial bool IsChangedOnDiskBannerVisible { get; private set; }

    /// <summary>Closing needs a live session: once it ended nothing would bring back a hidden resolver.</summary>
    [ObservableProperty]
    public partial bool IsCloseVisible { get; private set; }

    [ObservableProperty]
    public partial bool HasConflictsCount { get; private set; }

    [ObservableProperty]
    public partial string ConflictsCountText { get; private set; } = "";

    [ObservableProperty]
    public partial string? ConflictsCountToolTip { get; private set; }

    [ObservableProperty]
    public partial bool HasDamagedMarkers { get; private set; }

    [ObservableProperty]
    public partial string DamagedMarkersText { get; private set; } = "";

    /// <summary>Conflicts the arrows step through in the current file.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanNavigate))]
    public partial int NavigableCount { get; private set; }

    [ObservableProperty]
    public partial string CardPath { get; private set; } = "";

    [ObservableProperty]
    public partial string CardStatus { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCardNote))]
    public partial string CardNote { get; private set; } = "";

    /// <summary>True when the card note is a warning rather than a plain remark.</summary>
    [ObservableProperty]
    public partial bool CardNoteIsWarning { get; private set; }

    /// <summary>False for a submodule, which no external merge tool can open.</summary>
    [ObservableProperty]
    public partial bool CardCanUseExternalTool { get; private set; } = true;

    public bool ShowEditors => Mode == ConflictEditorMode.Editors;

    public bool ShowAllResolvedCard => Mode == ConflictEditorMode.AllResolved;

    public bool ShowFileCard => Mode == ConflictEditorMode.FileCard;

    public bool HasCardNote => CardNote.Length > 0;

    public bool ContinueIsPrimary => !FilesRemain;

    /// <summary>Actions that need no editable text; they wait out a running operation.</summary>
    public bool AreActionsEnabled => !IsBusy;

    /// <summary>Actions on the editable text: save and mark resolved.</summary>
    public bool AreTextActionsEnabled => !IsBusy && IsEditable;

    /// <summary>Whole-file actions need a loaded file.</summary>
    public bool CanResolveFile => !IsBusy && CurrentFile is not null;

    public bool CanNavigate => AreTextActionsEnabled && NavigableCount > 0;

    /// <summary>True while edits to the loaded file are registered as unsaved work.</summary>
    public bool HasUnsavedWork => _unsavedRegistration != null;

    /// <summary>The unmerged files as last listed.</summary>
    public IReadOnlyList<ConflictFile> ConflictedFiles => _conflictedFiles;

    /// <summary>The loaded file's working copy as read from disk; null before a file is loaded.</summary>
    public ConflictWorkingCopy? WorkingCopy => _workingCopy;

    /// <summary>The loaded file's disk hash, compared against the disk before every save.</summary>
    public string? DiskHash { get; private set; }

    /// <summary>
    /// Hands the view model the editor panes, follows the repository's status, conflict selection
    /// and session, and starts loading the conflicted files.
    /// </summary>
    public void Attach(IConflictEditorSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        _surface = surface;

        var state = _ctx.State;
        Subscribe(() => state.StatusChanged += OnStatusChanged, () => state.StatusChanged -= OnStatusChanged);
        Subscribe(() => state.SelectedConflictPathChanged += OnSelectedConflictPathChanged, () => state.SelectedConflictPathChanged -= OnSelectedConflictPathChanged);
        Subscribe(() => state.ConflictSessionChanged += OnConflictSessionChanged, () => state.ConflictSessionChanged -= OnConflictSessionChanged);
        UpdateCloseButton();
        _ = LoadAsync();
    }

    /// <summary>
    /// Pushes the conflict counter and the damaged-markers hint, which the view computes from the
    /// parsed Result and the caret.
    /// </summary>
    public void SetConflictStats(int total, int current, int unresolved, int malformedCount, int firstMalformedLine)
    {
        HasConflictsCount = total > 0;
        ConflictsCountText = total > 0 ? $"{current}/{total}" : string.Empty;
        ConflictsCountToolTip = total > 0 ? $"Conflict {current} of {total} in this file · {unresolved} unresolved" : null;
        NavigableCount = total;

        if (malformedCount > 0)
        {
            DamagedMarkersText = $"Conflict markers damaged near line {firstMalformedLine}";
            HasDamagedMarkers = true;
        }
        else
        {
            HasDamagedMarkers = false;
        }
    }

    /// <summary>Hides the counter and the damaged-markers hint.</summary>
    public void ClearConflictStats()
    {
        HasConflictsCount = false;
        HasDamagedMarkers = false;
        NavigableCount = 0;
    }

    /// <summary>Registers the loaded file's edits as unsaved work, once per file.</summary>
    public void RegisterUnsavedWork()
    {
        if (_unsavedRegistration != null || _unsavedRegistry == null || CurrentFile == null)
        {
            return;
        }

        _unsavedRegistration = _unsavedRegistry.Register(new ConflictUnsavedWork(this, _ctx, CurrentFile.FilePath));
    }

    /// <summary>A press inside the resolver brings the changes panel back: no commit selected, repo route shown.</summary>
    public void ShowChangesPanel()
    {
        if (_ctx.State.SelectedCommitId is not null)
        {
            _ctx.State.SetSelectedCommit(null);
        }

        // Skip requesting navigation when already on the repo route because DetailRouter rebuilds the view on every request.
        if (_nav is not null)
        {
            var head = (_nav.CurrentRoute ?? string.Empty).Split('?', 2)[0].TrimStart('/');
            if (!string.Equals(head, "repo", StringComparison.Ordinal))
            {
                _nav.RequestNavigate("repo");
            }
        }
    }

    protected override void OnDisposed()
    {
        _viewCts.Cancel();
        _switchCts?.Cancel();
        _switchCts?.Dispose();
        UnregisterUnsavedWork();
    }

    partial void OnIsBusyChanged(bool value) => BusyChanged?.Invoke(this, EventArgs.Empty);

    private void UnregisterUnsavedWork()
    {
        _unsavedRegistration?.Dispose();
        _unsavedRegistration = null;
    }

    private void DiscardUnsavedState()
    {
        UnregisterUnsavedWork();
        _surface?.MarkClean();
    }

    /// <summary>Closing hides the resolver and keeps its edits; it never saves or discards.</summary>
    [RelayCommand]
    private void Close() => _ctx.State.SetConflictResolverClosed(true);

    [RelayCommand]
    private void NextConflict() => _surface?.GoToNextConflict();

    [RelayCommand]
    private void PreviousConflict() => _surface?.GoToPreviousConflict();

    private void UpdateCloseButton() => IsCloseVisible = _ctx.State.IsConflictSessionActive;

    private void SetSideCaptions(ConflictSideLabels labels)
    {
        OursCaption = $"Ours · {labels.OursTitle}";
        TheirsCaption = $"Theirs · {labels.TheirsTitle}";
        OursCaptionToolTip = labels.OursDetail;
        TheirsCaptionToolTip = labels.TheirsDetail;
    }

    private void OnStatusChanged(object? sender, StatusChangedEventArgs e)
    {
        _ui.Post(() => _ = RefreshAfterStatusChangeAsync());
    }

    // Overlapping status changes fold into one pending pass behind the running one.
    private async Task RefreshAfterStatusChangeAsync()
    {
        if (IsDisposed)
        {
            return;
        }

        if (_statusRefreshRunning)
        {
            _statusRefreshPending = true;
            return;
        }

        _statusRefreshRunning = true;
        try
        {
            do
            {
                _statusRefreshPending = false;
                await ProcessStatusChangeAsync();
            } while (_statusRefreshPending && !IsDisposed);
        }
        finally
        {
            _statusRefreshRunning = false;
        }
    }

    private async Task ProcessStatusChangeAsync()
    {
        try
        {
            var labels = await _conflicts.GetSideLabelsAsync(_ctx, _viewCts.Token);
            if (IsDisposed) return;
            bool stopChanged = _stopId != null && labels.StopId != _stopId;
            _stopId = labels.StopId;
            SetSideCaptions(labels);
            UpdateCloseButton();

            var op = _ctx.State.PendingOperation;
            if (op == OperationState.None)
            {
                IsOperationBarVisible = false;
                IsExternalEndedBannerVisible = HasUnsavedWork;
            }
            else
            {
                IsExternalEndedBannerVisible = false;
                IsOperationBarVisible = true;
                IsSkipVisible = op is OperationState.Rebasing or OperationState.CherryPicking or OperationState.Reverting;
            }

            await RefreshConflictsAsync();
            if (IsDisposed) return;
            await CheckExternalDiskChangeAsync();
            if (IsDisposed) return;

            bool currentStillConflicted = CurrentFile != null &&
                _conflictedFiles.Any(f => string.Equals(f.FilePath, CurrentFile.FilePath, StringComparison.Ordinal));

            if (stopChanged)
            {
                if (HasUnsavedWork)
                {
                    IsChangedOnDiskBannerVisible = true;
                }
                else
                {
                    if (currentStillConflicted)
                    {
                        var matching = _conflictedFiles.First(f => string.Equals(f.FilePath, CurrentFile!.FilePath, StringComparison.Ordinal));
                        await SwitchToFileAsync(matching);
                    }
                    else if (_conflictedFiles.Count > 0)
                    {
                        await SwitchToFileAsync(_conflictedFiles[0]);
                    }
                    else
                    {
                        ShowAllConflictsResolvedCard();
                    }
                }
            }
            else if (!currentStillConflicted)
            {
                if (HasUnsavedWork)
                {
                    IsChangedOnDiskBannerVisible = true;
                }
                else
                {
                    if (_conflictedFiles.Count > 0)
                    {
                        await SwitchToFileAsync(_conflictedFiles[0]);
                    }
                    else
                    {
                        ShowAllConflictsResolvedCard();
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!IsDisposed)
            {
                _notifications.Publish(NotificationSeverity.Error, ex.Message);
            }
        }
    }

    private void OnConflictSessionChanged(object? sender, EventArgs e)
        => _ui.Post(() =>
        {
            if (!IsDisposed)
            {
                UpdateCloseButton();
            }
        });

    private void OnSelectedConflictPathChanged(object? sender, SelectedConflictPathChangedEventArgs e)
    {
        var path = e.Path;
        _ui.Post(() => _ = SwitchToSelectedPathAsync(path));
    }

    private async Task SwitchToSelectedPathAsync(string? path)
    {
        if (IsDisposed)
        {
            return;
        }

        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        if (CurrentFile != null && string.Equals(CurrentFile.FilePath, path, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            var target = _conflictedFiles.FirstOrDefault(f => string.Equals(f.FilePath, path, StringComparison.Ordinal));
            if (target == null)
            {
                await RefreshConflictsAsync();
                target = _conflictedFiles.FirstOrDefault(f => string.Equals(f.FilePath, path, StringComparison.Ordinal));
                if (target == null)
                {
                    return;
                }
            }

            if (HasUnsavedWork)
            {
                var work = _unsavedRegistry?.For(_ctx) ?? [new ConflictUnsavedWork(this, _ctx, CurrentFile?.FilePath ?? "")];
                var proceed = await UnsavedWorkPrompt.ResolveAsync(work, _dialogs);
                if (!proceed)
                {
                    _ctx.State.SetSelectedConflictPath(CurrentFile?.FilePath);
                    return;
                }
            }

            await SwitchToFileAsync(target);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, $"Could not open {path}: {ex.Message}", ex.NotificationDetails());
            _ctx.State.SetSelectedConflictPath(CurrentFile?.FilePath);
        }
    }

    /// <summary>Lists the conflicted files and opens the selected one (or the first).</summary>
    public async Task LoadAsync()
    {
        try
        {
            var labels = await _conflicts.GetSideLabelsAsync(_ctx, _viewCts.Token);
            _stopId = labels.StopId;
            SetSideCaptions(labels);
            UpdateCloseButton();

            var op = _ctx.State.PendingOperation;
            if (op == OperationState.None)
            {
                IsOperationBarVisible = false;
                if (HasUnsavedWork)
                {
                    IsExternalEndedBannerVisible = true;
                }
            }
            else
            {
                IsOperationBarVisible = true;
                IsSkipVisible = op is OperationState.Rebasing or OperationState.CherryPicking or OperationState.Reverting;
            }

            await RefreshConflictsAsync();

            if (_conflictedFiles.Count == 0)
            {
                ShowAllConflictsResolvedCard();
            }
            else if (CurrentFile == null || !_conflictedFiles.Any(f => string.Equals(f.FilePath, CurrentFile.FilePath, StringComparison.Ordinal)))
            {
                if (!HasUnsavedWork)
                {
                    var initialPath = _ctx.State.SelectedConflictPath;
                    var initialFile = initialPath != null
                        ? _conflictedFiles.FirstOrDefault(f => string.Equals(f.FilePath, initialPath, StringComparison.Ordinal)) ?? _conflictedFiles[0]
                        : _conflictedFiles[0];
                    await SwitchToFileAsync(initialFile);
                }
            }
            else
            {
                CurrentFile = _conflictedFiles.First(f => string.Equals(f.FilePath, CurrentFile.FilePath, StringComparison.Ordinal));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.Message, ex.NotificationDetails());
        }
    }

    private async Task RefreshConflictsAsync()
    {
        if (IsDisposed) return;
        var files = await _conflicts.GetConflictsAsync(_ctx, _viewCts.Token);
        if (IsDisposed) return;
        _conflictedFiles.Clear();
        _conflictedFiles.AddRange(files);
        FilesRemain = _conflictedFiles.Count > 0;
    }

    private void ShowAllConflictsResolvedCard()
    {
        IsEditable = false;
        ClearConflictStats();
        IsChangedOnDiskBannerVisible = false;
        CurrentFile = null;
        _workingCopy = null;
        DiskHash = null;
        _surface?.ClearDocument();
        Mode = ConflictEditorMode.AllResolved;
        _ctx.State.SetSelectedConflictPath(null);
    }

    /// <summary>
    /// Switches the active conflict file in the resolver.
    /// Callers must hold no unsaved work or have resolved it through Save / Discard / Reload.
    /// </summary>
    private async Task SwitchToFileAsync(ConflictFile file)
    {
        int gen = ++_switchGeneration;
#pragma warning disable S6966 // synchronous cancel keeps the order; an await would let a re-entrant switch run first
        _switchCts?.Cancel();
#pragma warning restore S6966
        _switchCts?.Dispose();
        _switchCts = CancellationTokenSource.CreateLinkedTokenSource(_viewCts.Token);
        var ct = _switchCts.Token;

        ConflictWorkingCopy copy;
        try
        {
            copy = await _conflicts.LoadWorkingCopyAsync(_ctx, file.FilePath, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (gen != _switchGeneration || IsDisposed)
        {
            return;
        }

        CurrentFile = file;
        _workingCopy = copy;
        DiskHash = copy.DiskHash;
        IsChangedOnDiskBannerVisible = false;
        _surface?.LoadDocument(file.FilePath, copy.Text ?? string.Empty);
        UnregisterUnsavedWork();

        bool isEditable = !file.IsBinary && !file.IsSubmodule &&
                          !copy.IsBinary && !copy.UnsupportedEncoding &&
                          !copy.ExceedsBudget && copy.Text != null;

        if (!isEditable)
        {
            ShowFileLevelCard(file, copy);
        }
        else
        {
            IsEditable = true;
            Mode = ConflictEditorMode.Editors;
            _surface?.EnterEditMode();
        }

        _ctx.State.SetSelectedConflictPath(file.FilePath);
    }

    private void ShowFileLevelCard(ConflictFile file, ConflictWorkingCopy copy)
    {
        IsEditable = false;
        _surface?.LeaveEditMode();
        ClearConflictStats();

        CardPath = file.FilePath;
        CardStatus = $"Status: {file.Status}";
        CardCanUseExternalTool = !file.IsSubmodule;
        CardNoteIsWarning = false;
        CardNote = string.Empty;

        if (file.IsSubmodule)
        {
            CardNote = "Submodule conflict — take a side to resolve.";
            CardNoteIsWarning = true;
        }
        else if (copy.IsBinary)
        {
            CardNote = "Binary file conflict — textual merge unavailable.";
        }
        else if (copy.ExceedsBudget)
        {
            CardNote = "File exceeds merge size budget (5 MB / 50 000 lines).";
            CardNoteIsWarning = true;
        }

        Mode = ConflictEditorMode.FileCard;
    }

    /// <summary>Saves the loaded file; with <paramref name="stage"/> it also marks the file resolved and moves to the next one.</summary>
    public async Task<bool> SaveCurrentFileAsync(bool stage)
    {
        if (IsBusy || !IsEditable || CurrentFile == null || _workingCopy == null || _workingCopy.Format is null || _surface is null)
        {
            return false;
        }

        var savedRevision = _surface.CaptureRevision();
        var textToSave = _surface.ComposeOutput();

        _saving = true;
        IsBusy = true;
        try
        {
            var res = await _conflicts.SaveResolutionAsync(
                _ctx,
                CurrentFile.FilePath,
                textToSave,
                _workingCopy.Format.Value,
                DiskHash ?? string.Empty,
                stage,
                _viewCts.Token);

            if (!res.Saved)
            {
                IsChangedOnDiskBannerVisible = true;
                return false;
            }

            DiskHash = res.DiskHash;
            IsChangedOnDiskBannerVisible = false;

            if (res.StageError != null)
            {
                _notifications.Publish(NotificationSeverity.Error, $"Saved {CurrentFile.FilePath}, but could not mark it resolved: {res.StageError.Message}", res.StageError.NotificationDetails());
                return false;
            }

            bool unchanged = _surface.IsRevisionCurrent(savedRevision);
            if (unchanged)
            {
                _surface.MarkClean();
                UnregisterUnsavedWork();
            }

            if (stage && unchanged)
            {
                await RefreshConflictsAsync();
                if (_conflictedFiles.Count > 0)
                {
                    var next = _conflictedFiles.FirstOrDefault(f => !string.Equals(f.FilePath, CurrentFile.FilePath, StringComparison.Ordinal))
                               ?? _conflictedFiles[0];
                    await SwitchToFileAsync(next);
                }
                else
                {
                    ShowAllConflictsResolvedCard();
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage("Save"), ex.NotificationDetails());
            return false;
        }
        finally
        {
            _saving = false;
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task SaveAndMarkResolvedAsync()
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            if (!await ConfirmMarkResolvedAsync())
            {
                return;
            }

            await SaveCurrentFileAsync(stage: true);
        }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage("Save"), ex.NotificationDetails());
        }
    }

    /// <summary>
    /// Before marking a file resolved: warn about conflict blocks still in it, and about managed
    /// conflicts that keep no line and will save as nothing. Neither is destructive — the text is
    /// what the user sees, and it stays editable after staging.
    /// </summary>
    private async Task<bool> ConfirmMarkResolvedAsync()
    {
        var summary = _surface?.Summarize() ?? default;
        int remaining = summary.RemainingBlocks;
        int emptied = summary.EmptiedConflicts;
        if (remaining > 0)
        {
            string message = $"{remaining} conflict block(s) remain — mark resolved anyway?";
            if (emptied > 0)
            {
                message += $" {emptied} conflict(s) will save as no lines.";
            }
            return await _dialogs.ConfirmAsync("Unresolved conflicts", message, confirmLabel: "Mark resolved");
        }

        if (emptied > 0)
        {
            return await _dialogs.ConfirmAsync(
                "Emptied conflicts",
                $"{emptied} conflict(s) will save as no lines — mark resolved anyway?",
                confirmLabel: "Mark resolved");
        }

        return true;
    }

    private async Task TakeSideAsync(ConflictSide side)
    {
        if (IsBusy || CurrentFile == null)
        {
            return;
        }

        var file = CurrentFile;
        var path = file.FilePath;
        IsBusy = true;
        try
        {
            var labels = await _conflicts.GetSideLabelsAsync(_ctx, _viewCts.Token);
            bool isAbsent = side == ConflictSide.Ours
                ? (file.Status is ConflictStatus.DeletedByUs or ConflictStatus.AddedByThem or ConflictStatus.BothDeletedRenamed)
                : (file.Status is ConflictStatus.DeletedByThem or ConflictStatus.AddedByUs or ConflictStatus.BothDeletedRenamed);
            var sideTitle = side == ConflictSide.Ours ? labels.OursTitle : labels.TheirsTitle;
            string title = isAbsent ? $"Delete {path}" : (side == ConflictSide.Ours ? "Take ours" : "Take theirs");
            string message = isAbsent
                ? $"Delete {path} — {sideTitle} deleted it. Any edits to this file will be lost."
                : $"Replace {path} with {sideTitle}'s version? Any edits to this file will be lost.";
            string verb = isAbsent ? "Delete" : (side == ConflictSide.Ours ? "Take ours" : "Take theirs");

            if (!await _dialogs.ConfirmDestructiveAsync(title, message, verb))
            {
                return;
            }

            await _conflicts.ResolveWithSideAsync(_ctx, path, side, _viewCts.Token);
            UnregisterUnsavedWork();
            await RefreshConflictsAsync();
            if (_conflictedFiles.Count > 0)
            {
                var next = _conflictedFiles.FirstOrDefault(f => !string.Equals(f.FilePath, path, StringComparison.Ordinal))
                           ?? _conflictedFiles[0];
                await SwitchToFileAsync(next);
            }
            else
            {
                ShowAllConflictsResolvedCard();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage($"Take {(side == ConflictSide.Ours ? "ours" : "theirs")}"), ex.NotificationDetails());
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public Task TakeOursCurrentAsync() => TakeSideAsync(ConflictSide.Ours);

    [RelayCommand]
    public Task TakeTheirsCurrentAsync() => TakeSideAsync(ConflictSide.Theirs);

    /// <summary>Rewrites the file back to its conflicted state, base section included.</summary>
    [RelayCommand]
    public async Task ResetFileAsync()
    {
        if (IsBusy || CurrentFile == null)
        {
            return;
        }

        var path = CurrentFile.FilePath;
        IsBusy = true;
        try
        {
            if (!await _dialogs.ConfirmDestructiveAsync(
                    "Reset file to conflicted",
                    $"Rewrite {path} back to its conflicted state, including the common-ancestor (base) section? Any edits to this file will be lost.",
                    "Reset"))
            {
                return;
            }

            await _conflicts.RecreateConflictAsync(_ctx, path, _viewCts.Token);
            UnregisterUnsavedWork();
            await RefreshConflictsAsync();
            var target = _conflictedFiles.FirstOrDefault(f => string.Equals(f.FilePath, path, StringComparison.Ordinal));
            if (target != null)
            {
                await SwitchToFileAsync(target);
            }
            else if (_conflictedFiles.Count > 0)
            {
                await SwitchToFileAsync(_conflictedFiles[0]);
            }
            else
            {
                ShowAllConflictsResolvedCard();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage("Reset file to conflicted"), ex.NotificationDetails());
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task LaunchExternalToolAsync()
    {
        if (IsBusy || CurrentFile == null)
        {
            return;
        }

        try
        {
            if (HasUnsavedWork)
            {
                var work = _unsavedRegistry?.For(_ctx) ?? [new ConflictUnsavedWork(this, _ctx, CurrentFile.FilePath)];
                var proceed = await UnsavedWorkPrompt.ResolveAsync(work, _dialogs);
                if (!proceed)
                {
                    return;
                }
            }

            IsBusy = true;
            try
            {
                var path = CurrentFile.FilePath;
                var launched = await _conflicts.LaunchExternalMergeToolAsync(_ctx, path, _viewCts.Token);
                if (!launched)
                {
                    var tool = _settings?.Current.ExternalMergeTool;
                    if (!string.IsNullOrWhiteSpace(tool))
                    {
                        _notifications.Publish(NotificationSeverity.Warning, $"The merge tool exited with conflict markers still in {path}.");
                    }
                    else
                    {
                        _notifications.Publish(NotificationSeverity.Warning, "No external merge tool is configured (git config merge.tool).");
                    }
                    return;
                }

                UnregisterUnsavedWork();
                await RefreshConflictsAsync();
                var target = _conflictedFiles.FirstOrDefault(f => string.Equals(f.FilePath, path, StringComparison.Ordinal));
                if (target != null)
                {
                    await SwitchToFileAsync(target);
                }
                else if (_conflictedFiles.Count > 0)
                {
                    await SwitchToFileAsync(_conflictedFiles[0]);
                }
                else
                {
                    ShowAllConflictsResolvedCard();
                }
            }
            finally
            {
                IsBusy = false;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage("External tool"), ex.NotificationDetails());
        }
    }

    /// <summary>Continues the operation; unsaved edits are saved and marked resolved first, or discarded, as the user chooses.</summary>
    [RelayCommand]
    public async Task ContinueAsync()
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            if (HasUnsavedWork && CurrentFile != null)
            {
                var choice = await UnsavedWorkPrompt.ResolveAsync(
                    _dialogs,
                    "Unsaved changes",
                    $"{CurrentFile.FilePath} has unsaved edits. Save and mark it resolved before continuing?",
                    "Save & mark resolved",
                    "Discard changes");

                switch (choice)
                {
                    case UnsavedWorkChoice.Save:
                        if (!await ConfirmMarkResolvedAsync())
                        {
                            return;
                        }

                        var saved = await SaveCurrentFileAsync(stage: true);
                        if (!saved)
                        {
                            return;
                        }
                        break;

                    case UnsavedWorkChoice.Discard:
                        DiscardUnsavedState();
                        await SwitchToFileAsync(CurrentFile);
                        break;

                    case UnsavedWorkChoice.Cancel:
                    default:
                        return;
                }
            }

            IsBusy = true;
            try
            {
                await _conflicts.ContinueOperationAsync(_ctx, _ctx.Lifetime);
                if (_ctx.State.PendingOperation == OperationState.None)
                {
                    _ctx.State.SetKeepConflictResolverOpen(false);
                }
            }
            finally
            {
                IsBusy = false;
            }
        }
        catch (OperationCanceledException) { }
        catch (HistoriaGitException ex) when (ex.Kind == GitErrorKind.MergeConflict)
        {
            _notifications.Publish(NotificationSeverity.Warning, ex.Message);
        }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage("Continue"), ex.NotificationDetails());
        }
    }

    /// <summary>Drops the commit the operation stopped on; resolutions and unsaved edits in this stop are discarded.</summary>
    [RelayCommand]
    public async Task SkipAsync()
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            var labels = await _conflicts.GetSideLabelsAsync(_ctx, _viewCts.Token);
            var commitInfo = string.IsNullOrWhiteSpace(labels.TheirsDetail) ? "the current commit" : labels.TheirsDetail;
            if (!await _dialogs.ConfirmDestructiveAsync(
                    "Skip commit",
                    $"Skip {commitInfo}? Resolutions and unsaved edits in this stop will be discarded.",
                    "Skip"))
            {
                return;
            }

            IsBusy = true;
            try
            {
                await _conflicts.SkipCurrentAsync(_ctx, _ctx.Lifetime);
                DiscardUnsavedState();
                IsChangedOnDiskBannerVisible = false;
                if (_ctx.State.PendingOperation == OperationState.None)
                {
                    _ctx.State.SetKeepConflictResolverOpen(false);
                }
            }
            finally
            {
                IsBusy = false;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage("Skip"), ex.NotificationDetails());
        }
    }

    /// <summary>Cancels the merge, rebase or cherry-pick and restores the state before it started.</summary>
    [RelayCommand]
    public async Task AbortAsync()
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            if (!await _dialogs.ConfirmDestructiveAsync(
                    "Abort operation",
                    "Abort the in-progress merge / rebase / cherry-pick? Conflict resolutions and unsaved edits made so far will be discarded.",
                    "Abort"))
            {
                return;
            }

            IsBusy = true;
            try
            {
                await _conflicts.AbortOperationAsync(_ctx, _ctx.Lifetime);
                DiscardUnsavedState();
                IsChangedOnDiskBannerVisible = false;
                if (_ctx.State.PendingOperation == OperationState.None)
                {
                    _ctx.State.SetKeepConflictResolverOpen(false);
                }
            }
            finally
            {
                IsBusy = false;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage("Abort"), ex.NotificationDetails());
        }
    }

    private async Task CheckExternalDiskChangeAsync()
    {
        if (IsDisposed || _saving || CurrentFile == null || DiskHash == null)
        {
            return;
        }

        string currentHash;
        try
        {
            currentHash = await _conflicts.GetDiskHashAsync(_ctx, CurrentFile.FilePath, _viewCts.Token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable right now (locked mid-save, scanned): the next status change checks again.
            return;
        }

        if (IsDisposed) return;
        if (currentHash != DiskHash)
        {
            if (!HasUnsavedWork)
            {
                await SwitchToFileAsync(CurrentFile);
            }
            else
            {
                IsChangedOnDiskBannerVisible = true;
            }
        }
    }

    /// <summary>Reloads the file from disk after the user agrees to drop their edits.</summary>
    [RelayCommand]
    public async Task ReloadFromDiskAsync()
    {
        if (IsBusy || CurrentFile == null)
        {
            return;
        }

        try
        {
            if (!await _dialogs.ConfirmDestructiveAsync(
                    "Reload from disk",
                    $"Reload {CurrentFile.FilePath} from disk? Your unsaved edits will be discarded.",
                    "Reload"))
            {
                return;
            }

            IsChangedOnDiskBannerVisible = false;
            UnregisterUnsavedWork();
            await SwitchToFileAsync(CurrentFile);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage("Reload"), ex.NotificationDetails());
        }
    }

    /// <summary>Keeps the user's version: the next save overwrites the file that changed on disk.</summary>
    [RelayCommand]
    public async Task KeepMineAsync()
    {
        if (IsBusy || CurrentFile == null)
        {
            return;
        }

        var path = CurrentFile.FilePath;
        try
        {
            if (!await _dialogs.ConfirmDestructiveAsync(
                    "Keep my version",
                    $"Your next save will overwrite {path} on disk, which changed outside Historia.",
                    "Keep mine"))
            {
                return;
            }

            IsChangedOnDiskBannerVisible = false;
            IsBusy = true;
            try
            {
                var copy = await _conflicts.LoadWorkingCopyAsync(_ctx, path, _ctx.Lifetime);
                DiskHash = copy.DiskHash;
            }
            finally
            {
                IsBusy = false;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage("Keep mine"), ex.NotificationDetails());
        }
    }
}
