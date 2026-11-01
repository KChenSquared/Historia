namespace Historia.ViewModels;

/// <summary>
/// The selected commit's detail panel: author, hash, date, parents, the reflowed message, and the
/// changed files as a flat list in path or tree mode. <see cref="SetCommit"/> starts a debounced,
/// cancellable load; a load that was superseded never touches the bound state. The files mode
/// follows <see cref="AppSettings.CommitFilesView"/> and the date follows
/// <see cref="AppSettings.ShowAuthorDate"/>, both live.
/// </summary>
public sealed partial class CommitDetailViewModel : ViewModelBase
{
    /// <summary>Delay before a load reads git, so holding an arrow key through the graph does not
    /// diff every commit it passes.</summary>
    public static readonly TimeSpan LoadDebounce = TimeSpan.FromMilliseconds(60);

    internal const double RowBaseIndent = 12;
    internal const double IndentStep = 14;

    private readonly IGitGateway _gateway;
    private readonly IRepositoryStateService _state;
    private readonly ISettingsService _settings;
    private readonly INotificationService _notifications;
    private readonly IExternalLauncher _launcher;
    private readonly IUiDispatcher _ui;
    private readonly IClipboardService _clipboard;

    private readonly HashSet<string> _collapsedFolders = new(StringComparer.Ordinal);
    private CancellationTokenSource? _loadCts;
    private GitObjectId _id;
    private IReadOnlyList<FileStatusEntry> _files = [];
    private Dictionary<string, List<FileStatusEntry>> _filesByFolder = new(StringComparer.Ordinal);
    private Dictionary<string, SortedSet<string>> _childFolders = new(StringComparer.Ordinal);

    /// <param name="repoId">Identifies the repository tab; the view keys its split height on it.</param>
    public CommitDetailViewModel(
        IGitGateway gateway,
        IRepositoryStateService state,
        string repoId,
        GitObjectId id,
        ISettingsService settings,
        INotificationService notifications,
        IExternalLauncher launcher,
        IUiDispatcher ui,
        IClipboardService? clipboard = null)
    {
        _gateway = gateway;
        _state = state;
        RepoId = repoId;
        _id = id;
        _settings = settings;
        _notifications = notifications;
        _launcher = launcher;
        _ui = ui;
        _clipboard = clipboard ?? NullClipboardService.Instance;

        FilesMode = settings.Current.CommitFilesView;
        Subscribe(() => _settings.Changed += OnSettingsChanged, () => _settings.Changed -= OnSettingsChanged);
        QueueLoad();
    }

    public string RepoId { get; }

    /// <summary>The commit being shown or loaded.</summary>
    public GitObjectId CommitId => _id;

    /// <summary>The loaded commit, or null while a load is pending.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetail))]
    [NotifyPropertyChangedFor(nameof(HasNoParents))]
    [NotifyPropertyChangedFor(nameof(AuthorName))]
    [NotifyPropertyChangedFor(nameof(AuthorEmail))]
    [NotifyPropertyChangedFor(nameof(ShortHash))]
    [NotifyPropertyChangedFor(nameof(FullHash))]
    [NotifyPropertyChangedFor(nameof(DateText))]
    [NotifyPropertyChangedFor(nameof(DateToolTip))]
    public partial CommitDetail? Detail { get; private set; }

    public bool HasDetail => Detail is not null;

    public string AuthorName => Detail?.AuthorName ?? "";

    public string AuthorEmail => Detail?.AuthorEmail ?? "";

    public string ShortHash => Detail?.Id.ToShortString(10) ?? "";

    public string FullHash => Detail?.Id.ToString() ?? "";

    /// <summary>The commit date as <c>yyyy-MM-dd</c>: the author date or the commit date, per the setting.</summary>
    public string DateText => Detail is { } d
        ? $"{CommitDateDisplay.Pick(d.AuthorWhen, d.CommitWhen, _settings.Current.ShowAuthorDate):yyyy-MM-dd}"
        : "";

    public string DateToolTip => Detail is { } d
        ? $"Committed {d.CommitWhen:yyyy-MM-dd HH:mm}\nAuthored {d.AuthorWhen:yyyy-MM-dd HH:mm}"
        : "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasParents))]
    [NotifyPropertyChangedFor(nameof(HasNoParents))]
    public partial IReadOnlyList<CommitParentViewModel> Parents { get; private set; } = [];

    public bool HasParents => Parents.Count > 0;

    /// <summary>True for a loaded root commit.</summary>
    public bool HasNoParents => HasDetail && Parents.Count == 0;

    /// <summary>The reflowed message split into plain and linked runs, per the issue autolink rules.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<AutolinkRun> MessageRuns { get; private set; } = [];

    /// <summary>The reflowed message as plain text.</summary>
    public string MessageText => string.Concat(MessageRuns.Select(r => r.Text));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPathMode))]
    [NotifyPropertyChangedFor(nameof(IsTreeMode))]
    public partial CommitFilesView FilesMode { get; private set; }

    public bool IsPathMode => FilesMode == CommitFilesView.Path;

    public bool IsTreeMode => FilesMode == CommitFilesView.Tree;

    /// <summary>The files list: every file in path mode, folders and files in tree mode. Replaced
    /// wholesale when the commit, the mode or a folder's collapsed state changes.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<CommitRowViewModel> Rows { get; private set; } = [];

    /// <summary>"Loading file changes…" while a load is pending, "No file changes." for an empty
    /// commit, empty once the files are listed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFilesStatus))]
    public partial string FilesStatus { get; private set; } = "";

    public bool HasFilesStatus => FilesStatus.Length > 0;

    /// <summary>Shows another commit. Does nothing when it is already the one shown.</summary>
    public void SetCommit(GitObjectId id)
    {
        if (IsDisposed || _id == id)
            return;

        _id = id;
        OnPropertyChanged(nameof(CommitId));
        QueueLoad();
    }

    /// <summary>Collapses an expanded folder or expands a collapsed one.</summary>
    public void ToggleFolder(string folderPath)
    {
        if (!_collapsedFolders.Add(folderPath))
            _collapsedFolders.Remove(folderPath);
        RebuildRows();
    }

    /// <summary>Opens the diff of a file in the shown commit against its first parent.</summary>
    public void OpenFile(string path)
    {
        try
        {
            _state.SetOpenDiff(new DiffRequest
            {
                Source = DiffSource.Commit,
                Path = path,
                CommitId = _id,
                ParentIndex = 1,
                ContextLines = _settings.Current.DiffContextLines,
            });
        }
        catch (Exception ex)
        {
            AppLog.Write(AppLogLevel.Warn, "ViewModel", "Could not open the diff for a commit file.", ex);
        }
    }

    /// <summary>Shows a file of the working tree in the file manager.</summary>
    public void RevealFile(string path)
    {
        try
        {
            _launcher.RevealInFileManager(AbsolutePath(path));
        }
        catch (Exception ex)
        {
            AppLog.Write(AppLogLevel.Warn, "ViewModel", "Could not reveal a commit file.", ex);
        }
    }

    /// <summary>Puts a file's absolute path on the clipboard.</summary>
    public async Task CopyFilePathAsync(string path)
    {
        try
        {
            await _clipboard.SetTextAsync(AbsolutePath(path));
        }
        catch (Exception ex)
        {
            AppLog.Write(AppLogLevel.Warn, "ViewModel", "Could not copy a commit file path.", ex);
        }
    }

    private string AbsolutePath(string path) => System.IO.Path.Combine(_gateway.RepositoryPath, path);

    [RelayCommand]
    private async Task CopyHashAsync()
    {
        if (Detail is not { } detail)
            return;

        try
        {
            await _clipboard.SetTextAsync(detail.Id.ToString());
        }
        catch (Exception ex)
        {
            AppLog.Write(AppLogLevel.Warn, "ViewModel", "Could not copy the commit hash.", ex);
        }
    }

    [RelayCommand]
    private void GoToParent(CommitParentViewModel? parent)
    {
        if (parent is null)
            return;

        try
        {
            _state.SetSelectedCommit(parent.Id);
            _state.RequestRevealCommit(parent.Id);
        }
        catch (Exception ex)
        {
            AppLog.Write(AppLogLevel.Warn, "ViewModel", "Could not go to the parent commit.", ex);
        }
    }

    [RelayCommand]
    private void OpenUrl(string? url)
    {
        if (string.IsNullOrEmpty(url))
            return;

        try
        {
            _launcher.OpenUrl(url);
        }
        catch (Exception ex)
        {
            AppLog.Write(AppLogLevel.Warn, "ViewModel", "Could not open a link from a commit message.", ex);
        }
    }

    [RelayCommand]
    private Task ShowPathAsync() => SetFilesModeAsync(CommitFilesView.Path);

    [RelayCommand]
    private Task ShowTreeAsync() => SetFilesModeAsync(CommitFilesView.Tree);

    private async Task SetFilesModeAsync(CommitFilesView mode)
    {
        if (_settings.Current.CommitFilesView == mode)
            return;

        try
        {
            await _settings.UpdateAsync(s => s with { CommitFilesView = mode }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            AppLog.Write(AppLogLevel.Warn, "ViewModel", "Could not save the commit files view.", ex);
        }

        SyncFilesMode();
    }

    // Starts a load for the current id: the previous one is cancelled, the shown commit is
    // dropped so a settings change cannot render it over the new one, and the list says it is loading.
    private void QueueLoad()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        Detail = null;
        Parents = [];
        MessageRuns = [];
        _files = [];
        Rows = [];
        FilesStatus = "Loading file changes…";

        _ = LoadAsync(_id, ct);
    }

    // The awaits resume on the UI context, so the results are applied to bound state directly.
    private async Task LoadAsync(GitObjectId id, CancellationToken ct)
    {
        try
        {
            await Task.Delay(LoadDebounce, ct);
            if (ct.IsCancellationRequested)
                return;

            var detail = await _gateway.GetCommitDetailAsync(id, ct);
            var files = await _gateway.GetCommitFilesAsync(id, 1, ct);

            if (ct.IsCancellationRequested || IsDisposed || _id != id)
                return;

            Apply(detail, files);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested && !IsDisposed)
                _notifications.Publish(NotificationSeverity.Error, $"Could not load commit: {ex.Message}", ex.NotificationDetails());
        }
    }

    private void Apply(CommitDetail detail, IReadOnlyList<FileStatusEntry> files)
    {
        var rules = _settings.Current.IssueAutolinks;
        MessageRuns = IssueAutolinker.Linkify(CommitMessageReflow.Reflow(detail.Message) ?? "", rules);
        Parents = [.. detail.ParentIds.Select(p => new CommitParentViewModel(p))];

        _files = files;
        BuildTreeCache(files);
        FilesMode = _settings.Current.CommitFilesView;
        FilesStatus = files.Count == 0 ? "No file changes." : "";
        OnPropertyChanged(nameof(MessageText));
        RebuildRows();
        Detail = detail;
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        // The settings service raises on the thread that changed it; the work touches bound state.
        if (e.NewSettings.ShowAuthorDate != e.OldSettings.ShowAuthorDate)
        {
            _ui.Post(() =>
            {
                if (IsDisposed || Detail is null)
                    return;
                OnPropertyChanged(nameof(DateText));
            });
        }

        if (e.NewSettings.CommitFilesView != e.OldSettings.CommitFilesView)
        {
            _ui.Post(() =>
            {
                if (IsDisposed)
                    return;
                SyncFilesMode();
            });
        }
    }

    private void SyncFilesMode()
    {
        if (IsDisposed || FilesMode == _settings.Current.CommitFilesView)
            return;

        FilesMode = _settings.Current.CommitFilesView;
        RebuildRows();
    }

    private void RebuildRows()
    {
        if (_files.Count == 0)
        {
            Rows = [];
            return;
        }

        Rows = FilesMode == CommitFilesView.Tree
            ? FlattenTree()
            : [.. _files.Select(f => new CommitFileRowViewModel(this, f, f.Path, 0))];
    }

    private void BuildTreeCache(IReadOnlyList<FileStatusEntry> files)
    {
        var byFolder = new Dictionary<string, List<FileStatusEntry>>(StringComparer.Ordinal);
        var childFolders = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);

        foreach (var f in files)
        {
            var slash = f.Path.LastIndexOf('/');
            var folder = slash < 0 ? "" : f.Path[..slash];
            if (!byFolder.TryGetValue(folder, out var list))
            {
                list = [];
                byFolder[folder] = list;
            }
            list.Add(f);

            // Register every ancestor-to-child folder edge.
            var parts = folder.Length == 0 ? [] : folder.Split('/');
            var acc = "";
            foreach (var seg in parts)
            {
                var next = acc.Length == 0 ? seg : acc + "/" + seg;
                if (!childFolders.TryGetValue(acc, out var cs))
                {
                    cs = new(StringComparer.Ordinal);
                    childFolders[acc] = cs;
                }
                cs.Add(next);
                acc = next;
            }
        }

        foreach (var list in byFolder.Values)
            list.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.Ordinal));

        _filesByFolder = byFolder;
        _childFolders = childFolders;
    }

    // Folders first (sorted), then the folder's own files (sorted), depth first; the contents of a
    // collapsed folder are skipped.
    private List<CommitRowViewModel> FlattenTree()
    {
        var rows = new List<CommitRowViewModel>();

        void Emit(string folder, int depth)
        {
            if (_childFolders.TryGetValue(folder, out var subs))
            {
                foreach (var sub in subs)
                {
                    var name = sub[(sub.LastIndexOf('/') + 1)..];
                    var collapsed = _collapsedFolders.Contains(sub);
                    rows.Add(new CommitFolderRowViewModel(this, sub, name, depth, collapsed));
                    if (!collapsed)
                        Emit(sub, depth + 1);
                }
            }

            if (_filesByFolder.TryGetValue(folder, out var files))
            {
                foreach (var f in files)
                    rows.Add(new CommitFileRowViewModel(this, f, f.Path[(f.Path.LastIndexOf('/') + 1)..], depth));
            }
        }

        Emit("", 0);
        return rows;
    }

    protected override void OnDisposed()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
    }
}
