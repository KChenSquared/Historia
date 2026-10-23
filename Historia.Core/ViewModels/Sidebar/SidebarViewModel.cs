namespace Historia.ViewModels;

/// <summary>The panes the sidebar column can show under its toggle.</summary>
public enum SidebarPane
{
    /// <summary>The ref navigator: branches, remotes, tags, stashes, submodules and worktrees.</summary>
    Branches,

    /// <summary>The list of navigations agents queued through <c>historia-cmdr</c>.</summary>
    Cmdr,
}

/// <summary>
/// The sidebar column's toggle between the ref navigator and the agent queue, and the queue's
/// unseen count shown on the toggle. The window starts on <see cref="SidebarPane.Branches"/>
/// every launch; the choice is never persisted.
/// </summary>
public sealed partial class SidebarViewModel : ViewModelBase
{
    /// <summary>The largest count the badge shows as a number; anything above reads "99+".</summary>
    public const int MaxShownCount = 99;

    private readonly ICmdrQueueService _queue;

    public SidebarViewModel(ICmdrQueueService queue)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        UnseenCount = _queue.UnseenCount;
        Subscribe(() => _queue.Changed += OnQueueChanged, () => _queue.Changed -= OnQueueChanged);
    }

    /// <summary>The pane the sidebar shows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBranchesActive))]
    [NotifyPropertyChangedFor(nameof(IsCmdrActive))]
    public partial SidebarPane ActivePane { get; private set; } = SidebarPane.Branches;

    /// <summary>True while the ref navigator is shown.</summary>
    public bool IsBranchesActive => ActivePane == SidebarPane.Branches;

    /// <summary>True while the agent queue is shown.</summary>
    public bool IsCmdrActive => ActivePane == SidebarPane.Cmdr;

    /// <summary>The number of queued items the user has not opened.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnseen))]
    [NotifyPropertyChangedFor(nameof(CountText))]
    public partial int UnseenCount { get; private set; }

    /// <summary>True when at least one queued item is unseen; the badge then takes the primary colour.</summary>
    public bool HasUnseen => UnseenCount > 0;

    /// <summary>The badge text: the unseen count, or "99+" above <see cref="MaxShownCount"/>.</summary>
    public string CountText => UnseenCount > MaxShownCount
        ? $"{MaxShownCount}+"
        : UnseenCount.ToString(CultureInfo.CurrentCulture);

    [RelayCommand]
    private void ShowBranches() => ActivePane = SidebarPane.Branches;

    [RelayCommand]
    private void ShowCmdr() => ActivePane = SidebarPane.Cmdr;

    // The queue raises on the UI thread.
    private void OnQueueChanged(object? sender, EventArgs e)
    {
        if (!IsDisposed)
            UnseenCount = _queue.UnseenCount;
    }
}
