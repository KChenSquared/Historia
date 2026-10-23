namespace Historia.ViewModels;

/// <summary>
/// The sidebar's agent queue: the navigations agents left through <c>historia-cmdr</c>, in the
/// order the user chose. Opening an item marks it seen and navigates exactly as a live request
/// would, without bringing the window forward; removing one asks nothing, because an item is a
/// pointer an agent left, not the user's work, and removing it changes no repository.
/// </summary>
public sealed partial class CmdrQueueViewModel : ViewModelBase
{
    private readonly ICmdrQueueService _queue;
    private readonly ISettingsService _settings;
    private readonly IControlNavigator _navigator;
    private readonly INavigationBus _nav;
    private readonly INotificationService _notifications;
    private readonly IUiDispatcher _ui;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, CmdrQueueItemViewModel> _rows = new(StringComparer.Ordinal);
    private CancellationTokenSource? _openCts;

    public CmdrQueueViewModel(
        ICmdrQueueService queue,
        ISettingsService settings,
        IControlNavigator navigator,
        INavigationBus nav,
        INotificationService notifications,
        IUiDispatcher ui,
        TimeProvider? time = null)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _navigator = navigator ?? throw new ArgumentNullException(nameof(navigator));
        _nav = nav ?? throw new ArgumentNullException(nameof(nav));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _time = time ?? TimeProvider.System;

        NewestFirst = _settings.Current.CmdrQueueNewestFirst;
        Sync();
        Subscribe(() => _queue.Changed += OnQueueChanged, () => _queue.Changed -= OnQueueChanged);
        Subscribe(() => _settings.Changed += OnSettingsChanged, () => _settings.Changed -= OnSettingsChanged);
    }

    /// <summary>The rows, oldest first or newest first. Rows keep their identity across changes,
    /// so the list keeps its scroll position when an item is marked seen.</summary>
    public ObservableCollection<CmdrQueueItemViewModel> Items { get; } = [];

    /// <summary>True when the list shows the most recently queued item first.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortText))]
    public partial bool NewestFirst { get; private set; }

    /// <summary>The sort button's label, naming the order shown.</summary>
    public string SortText => NewestFirst ? "Newest first" : "Oldest first";

    /// <summary>True when there is nothing queued; the list shows its empty-state text.</summary>
    [ObservableProperty]
    public partial bool IsEmpty { get; private set; } = true;

    /// <summary>Flips the order and remembers it across launches.</summary>
    [RelayCommand]
    private Task ToggleSort()
    {
        var newestFirst = !NewestFirst;
        NewestFirst = newestFirst;
        Sync();
        return _settings.UpdateAsync(s => s with { CmdrQueueNewestFirst = newestFirst });
    }

    /// <summary>Removes the item from the queue. No confirmation: the item is only a pointer.</summary>
    [RelayCommand]
    private void Remove(CmdrQueueItemViewModel? item)
    {
        if (item is not null)
            _queue.Remove(item.Id);
    }

    /// <summary>
    /// Marks the item seen and navigates to what it points at: opens or focuses its repository's
    /// tab and shows the commit, diff or proposal there, then shows the repository in the detail
    /// pane when Home or Settings covers it. A newer open cancels one still in flight. A failure
    /// leaves the item in place and says why in a warning notice.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task Open(CmdrQueueItemViewModel? item)
    {
        if (item is null || IsDisposed)
            return;

        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _openCts, cts);
        previous?.Cancel();

        _queue.MarkSeen(item.Id);
        try
        {
            var (response, _) = await _navigator.NavigateAsync(ControlTarget.FromItem(item.Item), cts.Token);
            if (cts.IsCancellationRequested || IsDisposed)
                return;

            if (response.Ok)
                ShowRepositoryPane();
            else
                PublishFailure(item, response.Message);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // A newer open took over, or the list went away.
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
                PublishFailure(item, ex.Message);
        }
        finally
        {
            Interlocked.CompareExchange(ref _openCts, null, cts);
            cts.Dispose();
        }
    }

    protected override void OnDisposed()
    {
        Interlocked.Exchange(ref _openCts, null)?.Cancel();
    }

    // Home and Settings cover the repository's detail pane, so the result would stay hidden behind
    // them. Pages with their own work in progress (rebase, profiles, themes and the developer and
    // repository-settings pages) are left as they are, as a live request leaves them.
    private void ShowRepositoryPane()
    {
        var head = (_nav.CurrentRoute ?? "").Split('?', 2)[0].TrimStart('/');
        if (head is "" or "settings")
            _nav.RequestNavigate("repo");
    }

    private void PublishFailure(CmdrQueueItemViewModel item, string? reason)
    {
        var why = string.IsNullOrWhiteSpace(reason) ? "The item could not be opened." : reason;
        _notifications.Publish(NotificationSeverity.Warning, $"Could not open the queued item for {item.RepoName}: {why}");
    }

    // The queue raises on the UI thread.
    private void OnQueueChanged(object? sender, EventArgs e)
    {
        if (!IsDisposed)
            Sync();
    }

    // Settings raise on the thread that changed them.
    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.OldSettings.CmdrQueueNewestFirst == e.NewSettings.CmdrQueueNewestFirst)
            return;
        var newestFirst = e.NewSettings.CmdrQueueNewestFirst;
        _ui.Post(() =>
        {
            if (IsDisposed || NewestFirst == newestFirst)
                return;
            NewestFirst = newestFirst;
            Sync();
        });
    }

    /// <summary>Brings <see cref="Items"/> in line with the queue in the chosen order, reusing rows
    /// by id and moving only what changed.</summary>
    private void Sync()
    {
        var items = _queue.Items;
        var wanted = new List<CmdrQueueItemViewModel>(items.Count);
        var live = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[NewestFirst ? items.Count - 1 - i : i];
            if (!live.Add(item.Id))
                continue;
            if (_rows.TryGetValue(item.Id, out var row))
                row.Update(item);
            else
                _rows[item.Id] = row = new CmdrQueueItemViewModel(item, _time, this);
            wanted.Add(row);
        }

        foreach (var id in _rows.Keys.Where(id => !live.Contains(id)).ToList())
            _rows.Remove(id);
        for (var i = Items.Count - 1; i >= 0; i--)
        {
            if (!live.Contains(Items[i].Id))
                Items.RemoveAt(i);
        }

        for (var i = 0; i < wanted.Count; i++)
        {
            var row = wanted[i];
            if (i < Items.Count && ReferenceEquals(Items[i], row))
                continue;
            var at = Items.IndexOf(row);
            if (at >= 0)
                Items.Move(at, i);
            else
                Items.Insert(i, row);
        }

        IsEmpty = Items.Count == 0;
    }
}
