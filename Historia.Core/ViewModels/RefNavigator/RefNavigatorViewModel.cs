namespace Historia.ViewModels;

/// <summary>
/// The sidebar ref navigator: local, remote and tag trees plus the stash, submodule and worktree
/// lists of the focused repository. Each <see cref="RefSectionViewModel"/> holds a flat list of rows
/// (folders, branches, tags and so on) that is rebuilt from the repository's refs whenever they,
/// a filter or an expanded folder change, so the view can virtualize it. Folders and sections keep
/// their open state for the session, across repository switches, until the repository being shown
/// is closed. The stash, submodule and worktree lists are seeded from the on-disk cache, then loaded
/// once the repository's first graph paint has landed. The cache is read and written on the thread
/// pool, one operation at a time, so no file is touched on the UI thread.
/// </summary>
public sealed partial class RefNavigatorViewModel : ViewModelBase
{
    /// <summary>Left edge shared by leaves and folder labels, past the section header's caret.</summary>
    public const double LeafIndent = 30;

    /// <summary>How far each nested folder level moves its rows right.</summary>
    public const double IndentStep = 14;

    /// <summary>The width of the cloud on a remote's root row.</summary>
    public const double RemoteCloudSize = 14;

    /// <summary>The cloud plus its gap to the remote's name.</summary>
    public const double RemoteCloudSlot = RemoteCloudSize + 3;

    private const string AllSubmodulesBusyKey = "*";

    private static readonly TimeSpan PowerToolsPaintCeiling = TimeSpan.FromSeconds(8);

    private readonly IWorkspaceService _workspace;
    private readonly INavigationBus _nav;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly IStashService _stashes;
    private readonly ISubmoduleService _submodules;
    private readonly IWorktreeService _worktrees;
    private readonly IRepoStateCache _repoCache;
    private readonly IRepoActionBus _actions;
    private readonly IUiDispatcher _ui;
    private readonly IRemoteDialogs _remoteDialogs;

    private readonly RefSectionViewModel[] _all;
    private RepositoryContext? _repo;

    // Folders the user toggled away from their depth's default, keyed by RefTreeNode.FullPath (or
    // "remote:<name>"). Depth 0 (a remote's own root, a top-level local folder) defaults to open and
    // everything nested inside defaults to collapsed, so the set records deviations, not open state.
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);

    // Power-tool lists; null means not loaded yet. Filled once per bind and refs change, so a section
    // toggle re-renders at once instead of flashing empty during a re-fetch.
    private IReadOnlyList<StashEntry>? _stashList;
    private IReadOnlyList<SubmoduleInfo>? _submoduleList;
    private IReadOnlyList<WorktreeInfo>? _worktreeList;
    private bool _submodulesFetched;
    private bool _worktreesFetched;

    private long? _loadedStashStamp;
    private IReadOnlyList<BranchInfo>? _powerToolsBranches;
    private IReadOnlyList<RemoteInfo>? _powerToolsRemotes;
    private int _stashListGen;

    // Bumped on every bind and on every refs change that re-reads the cache, so a cache read that
    // lands after a newer one started is not applied over it.
    private int _cacheGen;

    // Cache reads and writes run one after another on the thread pool, so a read queued after a write
    // sees it.
    private readonly object _cacheIoSync = new();
    private Task _cacheIoTail = Task.CompletedTask;

    // What the Local, Remotes and Tags sections (indexed by RefSectionKind) were last built from; a
    // refs change rebuilds only the sections whose inputs differ.
    private readonly RefInputs[] _builtInputs = new RefInputs[3];

    private readonly record struct RefInputs(
        IReadOnlyList<BranchInfo>? Branches,
        IReadOnlyList<RemoteInfo>? Remotes,
        IReadOnlyList<TagInfo>? Tags,
        GitObjectId? DetachedHead);

    // Submodule paths (or AllSubmodulesBusyKey for "Update all") with an Init and update in flight;
    // the row shows a busy label instead of its menu while present.
    private readonly HashSet<string> _submodulesBusy = new(StringComparer.Ordinal);

    public RefNavigatorViewModel(
        IWorkspaceService workspace,
        INavigationBus nav,
        IDialogService dialogs,
        INotificationService notifications,
        IStashService stashes,
        ISubmoduleService submodules,
        IWorktreeService worktrees,
        IRepoStateCache repoCache,
        IRepoActionBus actions,
        IUiDispatcher ui,
        IRemoteDialogs? remoteDialogs = null)
    {
        _workspace = workspace;
        _nav = nav;
        _dialogs = dialogs;
        _notifications = notifications;
        _stashes = stashes;
        _submodules = submodules;
        _worktrees = worktrees;
        _repoCache = repoCache;
        _actions = actions;
        _ui = ui;
        _remoteDialogs = remoteDialogs ?? NullRemoteDialogs.Instance;

        Local = new RefSectionViewModel(this, RefSectionKind.Local, openByDefault: true);
        Remotes = new RefSectionViewModel(this, RefSectionKind.Remotes, openByDefault: true);
        Tags = new RefSectionViewModel(this, RefSectionKind.Tags, openByDefault: false);
        Stashes = new RefSectionViewModel(this, RefSectionKind.Stashes, openByDefault: false);
        Submodules = new RefSectionViewModel(this, RefSectionKind.Submodules, openByDefault: false);
        Worktrees = new RefSectionViewModel(this, RefSectionKind.Worktrees, openByDefault: false);
        _all = [Local, Remotes, Tags, Stashes, Submodules, Worktrees];

        Subscribe(() => _workspace.FocusedRepositoryChanged += OnFocusChanged,
            () => _workspace.FocusedRepositoryChanged -= OnFocusChanged);
        Bind(_workspace.Focused);
    }

    public RefSectionViewModel Local { get; }

    public RefSectionViewModel Remotes { get; }

    public RefSectionViewModel Tags { get; }

    public RefSectionViewModel Stashes { get; }

    public RefSectionViewModel Submodules { get; }

    public RefSectionViewModel Worktrees { get; }

    /// <summary>The sections of the focused repository, top to bottom; empty when none is focused.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<RefSectionViewModel> Sections { get; private set; } = [];

    /// <summary>The focused repository, or null when none is.</summary>
    public RepositoryContext? Repository => _repo;

    /// <summary>The stash stamp the stash list was last loaded for; null before the first bind to a repository.</summary>
    public long? LoadedStashStamp => _loadedStashStamp;

    /// <summary>The row text for a stash: its 1-based position and message.</summary>
    public static string StashLabel(StashEntry s) => $"{s.Index + 1}. {s.Message}";

    /// <summary>The submodule row's label, busy or not. Shows the leaf folder name only; the full path
    /// is the row's tooltip, since nested submodule paths get too long to read in the panel.</summary>
    public static string SubmoduleRowLabel(SubmoduleInfo sm, bool busy)
    {
        var name = SubmoduleFolderName(sm.Name);
        return busy ? $"{name}  — Initializing…" : $"{name}  ({sm.Status})";
    }

    /// <summary>Leaf folder name of a submodule path, which always uses '/' separators (it comes from
    /// .gitmodules and git plumbing, even on Windows).</summary>
    public static string SubmoduleFolderName(string path)
    {
        var trimmed = path.TrimEnd('/');
        var i = trimmed.LastIndexOf('/');
        return i >= 0 ? trimmed[(i + 1)..] : trimmed;
    }

    // ── Binding to the focused repository ──

    private void OnFocusChanged(object? sender, FocusedRepositoryChangedEventArgs e)
        => _ui.Post(() =>
        {
            if (!IsDisposed)
                Bind(e.New);
        });

    private void Bind(RepositoryContext? ctx)
    {
        if (_repo is not null)
            _repo.State.RefsChanged -= OnRefsChanged;

        // A repository that has left the open set was closed from the tab strip rather than just
        // switched away from: the next one starts from the default layout instead of inheriting
        // the closed repository's expand and collapse state.
        if (_repo is not null && !_workspace.OpenRepositories.Contains(_repo))
            ResetSectionState();

        _repo = ctx;
        var gen = ++_cacheGen;
        _submodulesFetched = false;
        _worktreesFetched = false;
        _stashList = null;
        _submoduleList = null;
        _worktreeList = null;
        if (_repo is not null)
            _repo.State.RefsChanged += OnRefsChanged;
        Rebuild();
        if (_repo is not null)
        {
            _loadedStashStamp = _repo.State.StashStamp;
            _powerToolsBranches = _repo.State.Branches;
            _powerToolsRemotes = _repo.State.Remotes;
            _ = SeedPowerToolsFromCacheAsync(_repo, gen);
            _ = LoadPowerToolsAsync(_repo, listStashes: true);
        }
        else
        {
            _loadedStashStamp = null;
            _powerToolsBranches = null;
            _powerToolsRemotes = null;
        }
    }

    protected override void OnDisposed()
    {
        if (_repo is not null)
            _repo.State.RefsChanged -= OnRefsChanged;
    }

    private void ResetSectionState()
    {
        Local.SetOpen(true);
        Remotes.SetOpen(true);
        Tags.SetOpen(false);
        Stashes.SetOpen(false);
        Submodules.SetOpen(false);
        Worktrees.SetOpen(false);
        foreach (var section in _all)
        {
            section.SetFilterOpen(false);
            section.ClearFilterQuietly();
        }
        _expanded.Clear();
    }

    /// <summary>Handles the focused repository's refs changing. A raise that belongs to a previously
    /// bound repository is ignored, and stashes, submodules and worktrees are re-listed only when what
    /// they depend on changed.</summary>
    public void OnRefsChanged(object? sender, RefsChangedEventArgs e)
        => _ui.Post(() =>
        {
            // A raise posted just before a rebind belongs to the previous repo; comparing it
            // against this repo's baselines would overwrite them with the old repo's state.
            if (IsDisposed || _repo is null || !ReferenceEquals(sender, _repo.State))
                return;

            var stashChanged = _loadedStashStamp is null || e.StashStamp != _loadedStashStamp;
            var refsChanged = _powerToolsBranches is null || _powerToolsRemotes is null
                || !e.Branches.SequenceEqual(_powerToolsBranches)
                || !e.Remotes.SequenceEqual(_powerToolsRemotes);

            // Stashes depend only on the stash stamp, and submodules and worktrees don't depend on
            // tags. When neither changed (a tags-only refresh), just rebuild the sections whose
            // inputs changed.
            if (!stashChanged && !refsChanged)
            {
                RebuildChangedRefSections();
                return;
            }

            if (stashChanged)
                _loadedStashStamp = e.StashStamp;
            if (refsChanged)
            {
                _powerToolsBranches = e.Branches;
                _powerToolsRemotes = e.Remotes;
                _submodulesFetched = false;
                _worktreesFetched = false;
            }

            RebuildChangedRefSections();
            _ = ReloadPowerToolsAsync(_repo, ++_cacheGen, stashChanged, refsChanged);
        });

    // Last-known stash, submodule and worktree lists from the on-disk cache while the real
    // enumeration runs; a list stays null (shown as "Loading…") when nothing is cached. A list the
    // real enumeration has already filled is kept. A refs change that bumped the generation while the
    // read ran does not drop the seed: it only fills lists that are still null.
    private async Task SeedPowerToolsFromCacheAsync(RepositoryContext repo, int gen)
    {
        var snap = await ReadCacheAsync(repo.RepositoryPath);
        if (snap is null || !IsCurrentCacheRead(repo, gen, requireLatest: false))
            return;

        var applied = false;
        if (_stashList is null && snap.Stashes is not null)
        {
            _stashList = snap.Stashes;
            applied = true;
        }
        if (_submoduleList is null && snap.Submodules is not null)
        {
            _submoduleList = snap.Submodules;
            applied = true;
        }
        if (_worktreeList is null && snap.Worktrees is not null)
        {
            _worktreeList = snap.Worktrees;
            applied = true;
        }
        if (applied)
            RebuildPowerTools();
    }

    // A refs change shows the cached lists for what changed, then re-lists. The re-list runs even
    // when a newer change superseded this one's cache read: the newer change may not need the lists
    // this one does.
    private async Task ReloadPowerToolsAsync(RepositoryContext repo, int gen, bool stashChanged, bool refsChanged)
    {
        var snap = await ReadCacheAsync(repo.RepositoryPath);
        if (!IsCurrentCacheRead(repo, gen, requireLatest: false))
            return;

        if (gen == _cacheGen)
        {
            if (stashChanged)
                _stashList = snap?.Stashes;
            if (refsChanged)
            {
                _submoduleList = snap?.Submodules;
                _worktreeList = snap?.Worktrees;
            }
            RebuildPowerTools();
        }
        await LoadPowerToolsAsync(repo, listStashes: stashChanged);
    }

    private bool IsCurrentCacheRead(RepositoryContext repo, int gen, bool requireLatest = true)
        => !IsDisposed && ReferenceEquals(repo, _repo) && (!requireLatest || gen == _cacheGen);

    private async Task<RepoStateSnapshot?> ReadCacheAsync(string repoPath)
    {
        try
        {
            return await RunCacheIo(() => _repoCache.TryLoadPowerTools(repoPath));
        }
        catch (Exception ex)
        {
            AppLog.Write(AppLogLevel.Warn, "RefNavigator", "Reading the sidebar cache failed.", ex);
            return null;
        }
    }

    private Task<T> RunCacheIo<T>(Func<T> op)
    {
        lock (_cacheIoSync)
        {
            var next = _cacheIoTail.ContinueWith(_ => op(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            _cacheIoTail = next;
            return next;
        }
    }

    private async Task LoadPowerToolsAsync(RepositoryContext repo, bool listStashes = true)
    {
        try
        {
            await LoadPowerToolsCoreAsync(repo, listStashes);
        }
        catch (OperationCanceledException)
        {
            // The tab closed mid-load.
        }
        catch (Exception ex)
        {
            AppLog.Write(AppLogLevel.Warn, "RefNavigator", "Loading the stash, submodule and worktree lists failed.", ex);
        }
    }

    private async Task LoadPowerToolsCoreAsync(RepositoryContext repo, bool listStashes)
    {
        await Task.WhenAny(repo.FirstGraphPaint, Task.Delay(PowerToolsPaintCeiling, repo.Lifetime));
        if (IsDisposed || !ReferenceEquals(repo, _repo) || repo.Lifetime.IsCancellationRequested)
            return;

        var stashGen = 0;
        Task<IReadOnlyList<StashEntry>?>? stashTask = null;
        if (listStashes)
        {
            stashGen = ++_stashListGen;
            stashTask = Safe(() => _stashes.ListAsync(repo, repo.Lifetime));
        }

        var subsOpen = Submodules.IsOpen;
        var wtsOpen = Worktrees.IsOpen;
        var subsTask = subsOpen
            ? Safe(() => _submodules.ListAsync(repo, repo.Lifetime))
            : Task.FromResult<IReadOnlyList<SubmoduleInfo>?>(_submoduleList);
        var wtsTask = wtsOpen
            ? Safe(() => _worktrees.ListAsync(repo, repo.Lifetime))
            : Task.FromResult<IReadOnlyList<WorktreeInfo>?>(_worktreeList);

        var stash = stashTask is not null ? await stashTask : null;
        var subs = await subsTask;
        var wts = await wtsTask;
        if (IsDisposed || !ReferenceEquals(repo, _repo) || repo.Lifetime.IsCancellationRequested)
            return;

        if (listStashes && stashGen == _stashListGen)
            _stashList = stash ?? _stashList ?? [];

        if (subsOpen)
        {
            _submoduleList = subs ?? _submoduleList ?? [];
            _submodulesFetched = true;
        }
        if (wtsOpen)
        {
            _worktreeList = wts ?? _worktreeList ?? [];
            _worktreesFetched = true;
        }
        var (stashes, submodules, worktrees) = (_stashList, _submoduleList, _worktreeList);
        RebuildPowerTools();
        await RunCacheIo(() =>
        {
            _repoCache.SavePowerTools(repo.RepositoryPath, stashes, submodules, worktrees);
            return true;
        });

        static async Task<T?> Safe<T>(Func<Task<T>> op)
        {
            try { return await op(); }
            catch { return default; }
        }
    }

    // ── Section state ──

    internal void ToggleSection(RefSectionViewModel section)
    {
        section.SetOpen(!section.IsOpen);
        RebuildSection(section);
        if (!section.IsOpen || _repo is not { } repo)
            return;

        if (section.Kind == RefSectionKind.Submodules && (!_submodulesFetched || _submoduleList is null))
            _ = LoadPowerToolsAsync(repo);
        else if (section.Kind == RefSectionKind.Worktrees && (!_worktreesFetched || _worktreeList is null))
            _ = LoadPowerToolsAsync(repo);
    }

    internal void ToggleFilter(RefSectionViewModel section)
    {
        if (!section.HasFilter)
            return;
        section.SetFilterOpen(!section.IsFilterOpen);
        if (section.IsFilterOpen)
            section.SetOpen(true);
        RebuildSection(section);
        if (section.IsFilterOpen)
            section.RequestFilterFocus();
    }

    internal void CloseFilter(RefSectionViewModel section)
    {
        section.SetFilterOpen(false);
        section.ClearFilterQuietly();
        RebuildSection(section);
    }

    internal void OnFilterTextChanged(RefSectionViewModel section)
    {
        if (section.IsOpen)
            RebuildSection(section);
    }

    // ── Building rows ──

    private void Rebuild()
    {
        if (IsDisposed)
            return;

        if (_repo is null)
        {
            foreach (var section in _all)
                section.SetRows([]);
            Sections = [];
            return;
        }

        foreach (var section in _all)
            RebuildSection(section);
        Sections = _all;
    }

    // Rebuilds the branch, remote and tag sections whose inputs differ from what they were last
    // built from; the rest keep their rows.
    private void RebuildChangedRefSections()
    {
        if (IsDisposed || _repo is not { } repo)
            return;

        var state = repo.State;
        foreach (var section in (ReadOnlySpan<RefSectionViewModel>)[Local, Remotes, Tags])
        {
            if (!SameInputs(_builtInputs[(int)section.Kind], InputsFor(section.Kind, state)))
                RebuildSection(section);
        }
    }

    private static RefInputs InputsFor(RefSectionKind kind, IRepositoryStateService state) => kind switch
    {
        RefSectionKind.Local => new(state.Branches, null, null,
            state.Repository is { IsHeadDetached: true } info ? info.HeadCommitId : null),
        RefSectionKind.Remotes => new(state.Branches, state.Remotes, null, null),
        _ => new(null, state.Remotes, state.Tags, null),
    };

    private static bool SameInputs(RefInputs a, RefInputs b)
        => SameList(a.Branches, b.Branches)
            && SameList(a.Remotes, b.Remotes)
            && SameList(a.Tags, b.Tags)
            && Equals(a.DetachedHead, b.DetachedHead);

    private static bool SameList<T>(IReadOnlyList<T>? a, IReadOnlyList<T>? b)
        => ReferenceEquals(a, b) || (a is not null && b is not null && a.Count == b.Count && a.SequenceEqual(b));

    // The stash, submodule and worktree lists change on their own schedule; the branch and tag
    // sections are untouched by them.
    private void RebuildPowerTools()
    {
        RebuildSection(Stashes);
        RebuildSection(Submodules);
        RebuildSection(Worktrees);
    }

    private void RebuildSection(RefSectionViewModel section)
    {
        if (IsDisposed || _repo is not { } repo)
            return;

        var state = repo.State;
        switch (section.Kind)
        {
            case RefSectionKind.Local:
                _builtInputs[0] = InputsFor(RefSectionKind.Local, state);
                section.SetTitle($"Local Branches ({state.Branches.Count(b => !b.IsRemote)})");
                section.SetRows(section.IsOpen ? BuildLocalRows(repo, section) : []);
                break;
            case RefSectionKind.Remotes:
                _builtInputs[1] = InputsFor(RefSectionKind.Remotes, state);
                section.SetTitle($"Remotes ({state.Remotes.Count})");
                section.SetRows(section.IsOpen ? BuildRemoteRows(repo, section) : []);
                break;
            case RefSectionKind.Tags:
                _builtInputs[2] = InputsFor(RefSectionKind.Tags, state);
                section.SetTitle($"Tags ({state.Tags.Count})");
                section.SetRows(section.IsOpen ? BuildTagRows(repo, section) : []);
                break;
            case RefSectionKind.Stashes:
                section.SetTitle($"Stashes{Count(_stashList)}");
                section.SetRows(section.IsOpen ? BuildStashRows(section) : []);
                break;
            case RefSectionKind.Submodules:
                section.SetTitle($"Submodules{Count(_submoduleList)}");
                section.SetRows(section.IsOpen ? BuildSubmoduleRows() : []);
                break;
            default:
                section.SetTitle($"Worktrees{Count(_worktreeList)}");
                section.SetRows(section.IsOpen ? BuildWorktreeRows() : []);
                break;
        }
    }

    private static string Count<T>(IReadOnlyList<T>? list) => list is null ? "" : $" ({list.Count})";

    private bool IsOpen(string path, int depth) => (depth == 0) ^ _expanded.Contains(path);

    private RefLeafRowViewModel Message(string text, RefRowTone tone = RefRowTone.Faint) => new(this, text, 0, tone);

    private List<RefRowViewModel> BuildLocalRows(RepositoryContext repo, RefSectionViewModel section)
    {
        var rows = new List<RefRowViewModel>();
        var local = repo.State.Branches.Where(b => !b.IsRemote)
            .OrderByDescending(b => b.IsCurrent).ThenBy(b => b.Name, StringComparer.Ordinal).ToList();
        var currentCanonical = repo.State.CurrentBranch?.CanonicalName;

        var repoInfo = repo.State.Repository;
        if (repoInfo.IsHeadDetached && repoInfo.HeadCommitId is { } head)
        {
            rows.Add(new RefLeafRowViewModel(this, $"HEAD (detached at {head.ToShortString(7)})", 0, RefRowTone.Accent,
                press: () => SelectCommit(head), isCurrent: true, tooltip: head.ToString()));
        }

        if (section.HasActiveFilter)
        {
            var ranked = RefSearch.Rank(local, b => b.Name, FuzzyScorer.PrepareQuery(section.FilterText));
            if (ranked.Count == 0)
            {
                rows.Add(Message("No matches."));
            }
            else
            {
                foreach (var item in ranked)
                {
                    var b = item.Item;
                    var current = string.Equals(b.CanonicalName, currentCanonical, StringComparison.Ordinal);
                    rows.Add(new RefBranchRowViewModel(this, b, isRemote: false, b.Name, 0, current,
                        () => SelectCommit(b.TipCommitId)));
                }
            }
            rows.Add(new RefSeparatorRowViewModel(this));
        }

        foreach (var node in RefTree.Build(local, b => b.Name))
            AddNode(rows, node, depth: 0, isRemote: false, currentCanonical, RefSectionKind.Local);
        return rows;
    }

    private List<RefRowViewModel> BuildRemoteRows(RepositoryContext repo, RefSectionViewModel section)
    {
        var rows = new List<RefRowViewModel>();
        var remote = repo.State.Branches.Where(b => b.IsRemote).OrderBy(b => b.Name, StringComparer.Ordinal).ToList();

        if (section.HasActiveFilter)
        {
            var ranked = RefSearch.Rank(remote, b => b.Name, FuzzyScorer.PrepareQuery(section.FilterText));
            if (ranked.Count == 0)
            {
                rows.Add(Message("No matches."));
            }
            else
            {
                foreach (var item in ranked)
                {
                    var b = item.Item;
                    rows.Add(new RefBranchRowViewModel(this, b, isRemote: true, b.Name, 0, isCurrent: false,
                        () => SelectCommit(b.TipCommitId)));
                }
            }
            rows.Add(new RefSeparatorRowViewModel(this));
        }

        // Every configured remote gets a row, including one with no remote-tracking branches yet.
        // Tracking-branch groups whose alias is no longer configured (stale refs) still render,
        // without a remote menu or a number. Rows sort with the numbering's comparer, so a remote's
        // number matches its position among the configured rows.
        var configured = repo.State.Remotes.ToDictionary(r => r.Name, StringComparer.Ordinal);
        var numbering = RemoteNumbering.From(configured.Keys);
        var byRemote = remote
            .GroupBy(b => b.RemoteName ?? (b.Name.Contains('/') ? b.Name[..b.Name.IndexOf('/')] : "origin"))
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        foreach (var alias in configured.Keys.Union(byRemote.Keys, StringComparer.Ordinal).OrderBy(a => a, RemoteNumbering.Comparer))
        {
            var key = "remote:" + alias;
            configured.TryGetValue(alias, out var info);
            var open = IsOpen(key, depth: 0);
            rows.Add(new RefFolderRowViewModel(this, RefSectionKind.Remotes, alias, key, depth: 0, open,
                numbering.TooltipOf(alias), info, isRemoteRoot: true, numbering.NumberOf(alias)));
            if (open && byRemote.TryGetValue(alias, out var branchesOfRemote))
            {
                var prefix = alias + "/";
                foreach (var node in RefTree.Build(branchesOfRemote,
                             b => b.Name.StartsWith(prefix, StringComparison.Ordinal) ? b.Name[prefix.Length..] : b.Name))
                    AddNode(rows, node, depth: 1, isRemote: true, currentCanonical: null, RefSectionKind.Remotes);
            }
        }
        return rows;
    }

    // Case-insensitive first, ordinal as the tie-break, culture-free: the same order the branch
    // tree uses, so tags and branches agree.
    private static readonly Comparer<string> TagNameComparer = Comparer<string>.Create((a, b) =>
    {
        var c = string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        return c != 0 ? c : string.CompareOrdinal(a, b);
    });

    private List<RefRowViewModel> BuildTagRows(RepositoryContext repo, RefSectionViewModel section)
    {
        var rows = new List<RefRowViewModel>();
        var tags = repo.State.Tags.OrderByDescending(t => t.Name, TagNameComparer).ToList();
        var remotes = repo.State.Remotes.Select(r => r.Name).ToList();

        if (section.HasActiveFilter)
        {
            var ranked = RefSearch.Rank(tags, t => t.Name, FuzzyScorer.PrepareQuery(section.FilterText));
            if (ranked.Count == 0)
            {
                rows.Add(Message("No matches."));
            }
            else
            {
                foreach (var item in ranked)
                {
                    var tag = item.Item;
                    rows.Add(new RefTagRowViewModel(this, tag, remotes, () => SelectCommit(tag.TargetCommitId)));
                }
            }
            rows.Add(new RefSeparatorRowViewModel(this));
        }

        if (tags.Count == 0)
            rows.Add(Message("No tags."));
        foreach (var tag in tags)
            rows.Add(new RefTagRowViewModel(this, tag, remotes, () => SelectCommit(tag.TargetCommitId)));
        return rows;
    }

    private List<RefRowViewModel> BuildStashRows(RefSectionViewModel section)
    {
        var rows = new List<RefRowViewModel>();
        if (_stashList is null)
        {
            rows.Add(Message("Loading…"));
        }
        else if (_stashList.Count == 0)
        {
            rows.Add(Message("No stashes."));
        }
        else
        {
            if (section.HasActiveFilter)
            {
                var ranked = RefSearch.Rank(_stashList, StashLabel, FuzzyScorer.PrepareQuery(section.FilterText));
                if (ranked.Count == 0)
                {
                    rows.Add(Message("No matches."));
                }
                else
                {
                    foreach (var item in ranked)
                    {
                        var entry = item.Item;
                        rows.Add(new RefStashRowViewModel(this, entry, () => SelectCommitOnly(entry.CommitId)));
                    }
                }
                rows.Add(new RefSeparatorRowViewModel(this));
            }

            foreach (var entry in _stashList)
                rows.Add(new RefStashRowViewModel(this, entry, () => SelectCommitOnly(entry.CommitId)));
        }
        return rows;
    }

    private List<RefRowViewModel> BuildSubmoduleRows()
    {
        var rows = new List<RefRowViewModel>();
        if (_submoduleList is null)
        {
            rows.Add(Message("Loading…"));
            return rows;
        }
        if (_submoduleList.Count == 0)
        {
            rows.Add(Message("No submodules."));
            return rows;
        }

        foreach (var sm in _submoduleList)
        {
            if (_submodulesBusy.Contains(sm.Path))
                rows.Add(new RefLeafRowViewModel(this, SubmoduleRowLabel(sm, busy: true), 0, RefRowTone.Faint, tooltip: sm.Path));
            else
                rows.Add(new RefSubmoduleRowViewModel(this, sm));
        }

        var allBusy = _submodulesBusy.Contains(AllSubmodulesBusyKey);
        rows.Add(new RefLeafRowViewModel(this, allBusy ? "Update all — Initializing…" : "Update all", 0, RefRowTone.Muted,
            press: allBusy ? null : () => _ = InitAndUpdateSubmodulesAsync(null)));
        return rows;
    }

    private List<RefRowViewModel> BuildWorktreeRows()
    {
        var rows = new List<RefRowViewModel>();
        if (_worktreeList is null)
        {
            rows.Add(Message("Loading…"));
            return rows;
        }
        if (_worktreeList.Count == 0)
        {
            rows.Add(Message("No linked worktrees."));
            return rows;
        }

        foreach (var w in _worktreeList)
            rows.Add(new RefWorktreeRowViewModel(this, w));
        rows.Add(new RefLeafRowViewModel(this, "Prune stale", 0, RefRowTone.Muted,
            press: () => _ = PowerOp(r => _worktrees.PruneAsync(r, r.Lifetime))));
        return rows;
    }

    private void AddNode(List<RefRowViewModel> rows, RefTreeNode node, int depth, bool isRemote,
        string? currentCanonical, RefSectionKind section)
    {
        if (node.IsFolder)
        {
            var open = IsOpen(node.FullPath, depth);
            rows.Add(new RefFolderRowViewModel(this, section, node.Segment, node.FullPath, depth, open, node.Segment));
            if (open)
                foreach (var child in node.Children)
                    AddNode(rows, child, depth + 1, isRemote, currentCanonical, section);
            return;
        }

        var b = node.Leaf!;
        var current = !isRemote && string.Equals(b.CanonicalName, currentCanonical, StringComparison.Ordinal);
        rows.Add(new RefBranchRowViewModel(this, b, isRemote, node.Segment, depth, current, () => SelectCommit(b.TipCommitId)));
    }

    // ── Selecting ──

    private void SelectCommit(GitObjectId? id)
    {
        if (id is null || _repo is null)
            return;
        _repo.State.SetSelectedCommit(id);
        _repo.State.RequestRevealCommit(id.Value);
    }

    // Stash commits are never in the graph, so asking the graph to reveal one would walk the whole history for nothing.
    private void SelectCommitOnly(GitObjectId? id)
    {
        if (id is null || _repo is null)
            return;
        _repo.State.SetSelectedCommit(id);
    }

    /// <summary>Runs a leaf row's press: selects (and reveals) the commit it stands for, or runs its action.</summary>
    [RelayCommand]
    private void Select(RefLeafRowViewModel row) => row.Press?.Invoke();

    /// <summary>Runs a leaf row's double-click: checks a branch out, or opens a submodule's tab.</summary>
    [RelayCommand]
    private void Activate(RefLeafRowViewModel row)
    {
        switch (row)
        {
            case RefBranchRowViewModel branch:
                CheckoutCommand.Execute(branch);
                break;
            case RefSubmoduleRowViewModel submodule:
                OpenSubmoduleTabCommand.Execute(submodule);
                break;
        }
    }

    /// <summary>Expands or collapses a folder row.</summary>
    [RelayCommand]
    private void ToggleFolder(RefFolderRowViewModel folder)
    {
        if (!_expanded.Remove(folder.Path))
            _expanded.Add(folder.Path);

        var section = folder.Section == RefSectionKind.Local ? Local : Remotes;
        RebuildSection(section);
    }

    // ── Branches and remotes ──

    /// <summary>Checks a branch out. A remote row whose short name has no local counterpart creates a
    /// tracking branch; every other row checks the local branch out.</summary>
    [RelayCommand]
    private async Task Checkout(RefBranchRowViewModel row)
    {
        var repo = _repo;
        var b = row.Branch;
        if (repo is null || (b.IsCurrent && !row.IsRemote))
            return;

        try
        {
            await BranchActions.CheckoutBranchAsync(repo, _notifications, _stashes, b,
                onSwitched: name => BranchActions.AnnounceSwitch(_notifications, name));
        }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage("Checkout"), ex.NotificationDetails());
        }
    }

    /// <summary>Asks for a new remote, then adds it and optionally fetches it.</summary>
    [RelayCommand]
    private async Task AddRemote()
    {
        if (_repo is not { } repo)
            return;
        try
        {
            await RemoteActions.AddAsync(repo, _notifications, _actions, _remoteDialogs);
        }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage("Add remote"), ex.NotificationDetails());
        }
    }

    /// <summary>Pushes one local tag to one remote; the network runner reports the outcome.</summary>
    [RelayCommand]
    private void PushTag(RefTagPushTarget target)
    {
        if (_repo is not { } repo)
            return;
        _actions.RequestNetworkAction(new RepoNetworkRequest(
            RepoNetworkAction.PushTag, target.Remote, null, Repo: repo, Tag: target.Tag.Name));
    }

    /// <summary>Deletes a local tag after confirming.</summary>
    [RelayCommand]
    private async Task DeleteTag(RefTagRowViewModel row)
    {
        if (_repo is not { } repo)
            return;
        var tag = row.Tag;
        if (!await _dialogs.ConfirmDestructiveAsync("Delete tag", $"Delete local tag '{tag.Name}'? This cannot be undone.", "Delete"))
            return;
        try
        {
            await repo.Gateway.DeleteTagAsync(tag.Name, repo.Lifetime);
            await repo.State.RefreshRefsAsync(repo.Lifetime);
        }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage("Delete"), ex.NotificationDetails());
        }
    }

    // ── Stashes ──

    private async Task StashOp(Func<RepositoryContext, Task> op)
    {
        if (_repo is not { } repo)
            return;
        try
        {
            await op(repo);
            await repo.State.RefreshAllAsync(repo.Lifetime);
            _stashList = null;
            RebuildPowerTools();
            _ = LoadPowerToolsAsync(repo);
        }
        catch (OperationCanceledException) { /* tab closed or torn down mid-operation — not a failure */ }
        catch (Exception ex) { _notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage("Stash op")); }
    }

    /// <summary>Applies a stash, keeping it in the list and restoring the index.</summary>
    [RelayCommand]
    private Task StashApply(RefStashRowViewModel row) =>
        StashOp(r => _stashes.ApplyAsync(r, row.Entry.CommitId, restoreIndex: true, r.Lifetime));

    /// <summary>Applies a stash and removes it from the list. The stash is applied, not discarded.</summary>
    [RelayCommand]
    private Task StashApplyAndDrop(RefStashRowViewModel row) =>
        StashOp(r => _stashes.ApplyAndDropAsync(r, row.Entry.CommitId, r.Lifetime));

    /// <summary>Asks for a name and creates a branch from a stash.</summary>
    [RelayCommand]
    private async Task StashBranch(RefStashRowViewModel row)
    {
        if (_repo is null)
            return;
        var name = await _dialogs.PromptAsync("Branch from stash", "New branch name:");
        if (string.IsNullOrWhiteSpace(name))
            return;
        await StashOp(r => _stashes.BranchFromAsync(r, row.Entry.CommitId, name.Trim(), r.Lifetime));
    }

    /// <summary>Drops a stash after confirming.</summary>
    [RelayCommand]
    private async Task StashDrop(RefStashRowViewModel row)
    {
        if (_repo is null)
            return;
        var entry = row.Entry;
        if (!await _dialogs.ConfirmDestructiveAsync("Drop stash", $"Drop '{entry.Message}'? This cannot be undone.", "Drop"))
            return;
        await StashOp(r => _stashes.DropAsync(r, entry.CommitId, r.Lifetime));
    }

    // ── Submodules and worktrees ──

    private async Task PowerOp(Func<RepositoryContext, Task> op)
    {
        if (_repo is not { } repo)
            return;
        try
        {
            await op(repo);
            await repo.State.RefreshAllAsync(repo.Lifetime);
            _submoduleList = null;
            _worktreeList = null;
            RebuildPowerTools();
            _ = LoadPowerToolsAsync(repo);
        }
        catch (OperationCanceledException) { /* tab closed or torn down mid-operation — not a failure */ }
        catch (Exception ex) { _notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage("Operation"), ex.NotificationDetails()); }
    }

    // Wraps a submodule Init and update (one row, or every submodule when path is null) with a busy
    // state so the row shows feedback instead of looking hung.
    private async Task InitAndUpdateSubmodulesAsync(string? path)
    {
        var key = path ?? AllSubmodulesBusyKey;
        _submodulesBusy.Add(key);
        RebuildSection(Submodules);
        try
        {
            await PowerOp(r => _submodules.InitAndUpdateAsync(r, path, true, r.Lifetime));
        }
        finally
        {
            _submodulesBusy.Remove(key);
            RebuildSection(Submodules);
        }
    }

    /// <summary>Initialises and updates one submodule, or every submodule when the row is null.</summary>
    [RelayCommand]
    private Task SubmoduleInitAndUpdate(RefSubmoduleRowViewModel? row) =>
        InitAndUpdateSubmodulesAsync(row?.Submodule.Path);

    /// <summary>Re-reads a submodule's URL from .gitmodules.</summary>
    [RelayCommand]
    private Task SubmoduleSync(RefSubmoduleRowViewModel row) =>
        PowerOp(r => _submodules.SyncAsync(r, row.Submodule.Path, r.Lifetime));

    /// <summary>Opens (or focuses) a submodule's linked tab.</summary>
    [RelayCommand]
    private async Task OpenSubmoduleTab(RefSubmoduleRowViewModel row)
    {
        if (_repo is not { } repo)
            return;
        try
        {
            await _workspace.OpenSubmoduleAsync(repo, row.Submodule, repo.Lifetime);
            _nav.RequestNavigate("repo");
        }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error,
                $"Could not open submodule: {ex.Message}", ex.NotificationDetails());
        }
    }

    /// <summary>Removes a submodule after confirming. Removing deinitialises it, removes it from the
    /// index and deletes .git/modules/&lt;name&gt; — the submodule's own repository — so its uncommitted
    /// changes and unpushed commits are gone for good.</summary>
    [RelayCommand]
    private async Task SubmoduleRemove(RefSubmoduleRowViewModel row)
    {
        if (_repo is null)
            return;
        var sm = row.Submodule;
        if (!await _dialogs.ConfirmDestructiveAsync("Remove submodule",
                $"Remove submodule '{sm.Path}'? This deinitialises it, removes it from the " +
                $"index and deletes .git/modules/{sm.Name} — uncommitted changes and unpushed " +
                "commits inside the submodule are lost.", "Remove"))
            return;
        await PowerOp(r => _submodules.RemoveAsync(r, sm, r.Lifetime));
    }

    /// <summary>Locks a linked worktree so it is not pruned.</summary>
    [RelayCommand]
    private Task WorktreeLock(RefWorktreeRowViewModel row) =>
        PowerOp(r => _worktrees.LockAsync(r, row.Worktree.Path, "", r.Lifetime));

    /// <summary>Unlocks a linked worktree.</summary>
    [RelayCommand]
    private Task WorktreeUnlock(RefWorktreeRowViewModel row) =>
        PowerOp(r => _worktrees.UnlockAsync(r, row.Worktree.Path, r.Lifetime));

    /// <summary>Removes a linked worktree after confirming.</summary>
    [RelayCommand]
    private async Task WorktreeRemove(RefWorktreeRowViewModel row)
    {
        if (_repo is null)
            return;
        var wt = row.Worktree;
        if (!await _dialogs.ConfirmDestructiveAsync("Remove worktree", $"Remove the worktree at '{wt.Path}'?", "Remove"))
            return;
        await PowerOp(r => _worktrees.RemoveAsync(r, wt.Path, force: false, r.Lifetime));
    }
}
