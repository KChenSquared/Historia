namespace Historia.ViewModels;

/// <summary>
/// The landing page: open, clone and initialise a repository, the recents list and the clone
/// progress box. <paramref name="intent"/> (<c>"open"</c>, <c>"clone"</c> or <c>"init"</c>, from
/// the app-band <c>+</c> menu) launches the matching flow once the panel is shown.
/// </summary>
public sealed partial class HomeViewModel : ViewModelBase
{
    private readonly IWorkspaceService _workspace;
    private readonly ICloneService _cloneService;
    private readonly IRecentRepositoryService _recents;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly INavigationBus _nav;
    private readonly IExternalLauncher _launcher;
    private readonly IProfileService _profiles;
    private readonly ISubmoduleService _submodules;
    private readonly IUiDispatcher _ui;
    private readonly CloneDialogPresenter _showCloneDialog;
    private readonly IClipboardService _clipboard;

    private bool _busy;

    /// <param name="showCloneDialog">Shows the clone dialog. It is a window, so the view supplies it.</param>
    public HomeViewModel(
        string intent,
        IWorkspaceService workspace,
        ICloneService cloneService,
        IRecentRepositoryService recents,
        IDialogService dialogs,
        INotificationService notifications,
        INavigationBus nav,
        IExternalLauncher launcher,
        IProfileService profiles,
        ISubmoduleService submodules,
        IUiDispatcher ui,
        CloneDialogPresenter showCloneDialog,
        IClipboardService? clipboard = null)
    {
        _workspace = workspace;
        _cloneService = cloneService;
        _recents = recents;
        _dialogs = dialogs;
        _notifications = notifications;
        _nav = nav;
        _launcher = launcher;
        _profiles = profiles;
        _submodules = submodules;
        _ui = ui;
        _showCloneDialog = showCloneDialog;
        _clipboard = clipboard ?? NullClipboardService.Instance;

        // Closing has nowhere to go without a focused repository, so the button is left out.
        ShowClose = _workspace.Focused is not null;
        CanCancelClone = true;
        Recents = [];

        Subscribe(() => _recents.Changed += OnRecentsChanged, () => _recents.Changed -= OnRecentsChanged);
        Subscribe(() => _cloneService.Changed += OnCloneServiceChanged, () => _cloneService.Changed -= OnCloneServiceChanged);
        RefreshRecents();
        RefreshCloneStatus();
        // The rows above carry the last known staleness; the check runs off this thread and its
        // result arrives through the change event.
        _ = _recents.RefreshStalenessAsync();

        if (intent == "clone" && _cloneService.IsRunning)
        {
            // Already running: the box is already shown.
        }
        else
        {
            Func<Task>? launch = intent switch
            {
                "open" => OpenAsync,
                "clone" => CloneAsync,
                "init" => InitAsync,
                _ => null,
            };
            if (launch is not null)
            {
                _ui.Post(() =>
                {
                    if (!IsDisposed)
                        _ = launch();
                });
            }
        }
    }

    /// <summary>The recent repositories, most recent first. Replaced as a whole on every change.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoRecents))]
    public partial IReadOnlyList<RecentRepoViewModel> Recents { get; private set; }

    public bool HasNoRecents => Recents.Count == 0;

    /// <summary>Whether the panel offers a close button.</summary>
    public bool ShowClose { get; }

    /// <summary>A clone is running (or was just cancelled and is winding down).</summary>
    [ObservableProperty]
    public partial bool IsCloneVisible { get; private set; }

    [ObservableProperty]
    public partial string CloneStatusText { get; private set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCloneCommand))]
    public partial bool CanCancelClone { get; private set; }

    // Neither service raises on the UI thread; the work hops there because it touches bound state.
    // The recents service also raises when a background folder check changes a row's staleness.
    private void OnRecentsChanged(object? sender, EventArgs e)
        => _ui.Post(() =>
        {
            if (!IsDisposed)
                RefreshRecents();
        });

    private void OnCloneServiceChanged(object? sender, EventArgs e)
        => _ui.Post(() =>
        {
            if (!IsDisposed)
                RefreshCloneStatus();
        });

    private void RefreshCloneStatus()
    {
        CloneCommand.NotifyCanExecuteChanged();
        if (_cloneService.Current is { } status)
        {
            CloneStatusText = status.Describe();
            CanCancelClone = !status.CancelRequested;
            IsCloneVisible = true;
        }
        else
        {
            IsCloneVisible = false;
        }
    }

    private void RefreshRecents()
        => Recents = [.. _recents.Items.Select(r => new RecentRepoViewModel(r, this))];

    private bool CanClone() => !_cloneService.IsRunning;

    [RelayCommand(CanExecute = nameof(CanCancelClone))]
    private void CancelClone() => _cloneService.Cancel();

    [RelayCommand]
    private void Close()
        => _nav.RequestNavigate(_workspace.Focused is not null ? "repo" : "");

    [RelayCommand]
    private async Task OpenAsync()
    {
        try
        {
            var path = await _dialogs.PickFolderAsync("Open a Git repository");
            if (path is not null)
                await OpenPathCoreAsync(path);
        }
        catch (Exception ex)
        {
            Fail("Could not open repository", ex);
        }
    }

    [RelayCommand]
    private async Task OpenPathAsync(string? path)
    {
        if (!string.IsNullOrEmpty(path))
            await OpenPathCoreAsync(path);
    }

    private async Task OpenPathCoreAsync(string path)
    {
        _busy = true;
        try
        {
            await _workspace.OpenAsync(path);
            _nav.RequestNavigate("repo");
        }
        catch (Exception ex)
        {
            Fail("Could not open repository", ex);
        }
        finally { _busy = false; }
    }

    [RelayCommand]
    private async Task InitAsync()
    {
        try
        {
            var path = await _dialogs.PickFolderAsync("Choose a folder for the new repository");
            if (path is null) return;
            var branch = await _dialogs.PromptAsync("Initial branch", "Name for the initial branch:", "main");
            if (branch is null) return;

            _busy = true;
            try
            {
                await _workspace.InitAsync(path, string.IsNullOrWhiteSpace(branch) ? "main" : branch.Trim());
                _nav.RequestNavigate("repo");
            }
            catch (Exception ex)
            {
                Fail("Could not initialize repository", ex);
            }
            finally { _busy = false; }
        }
        catch (Exception ex)
        {
            Fail("Could not initialize repository", ex);
        }
    }

    [RelayCommand(CanExecute = nameof(CanClone))]
    private async Task CloneAsync()
    {
        if (_cloneService.IsRunning)
            return;

        try
        {
            var r = await _showCloneDialog(DefaultCloneRoot(), GuessRepoName,
                _profiles.GetAllProfiles(), _profiles.GetDefaultProfile()?.Id);
            if (r is null) return;

            if (_cloneService.IsRunning)
                return;

            var options = new CloneOptions
            {
                Depth = r.Shallow ? 1 : null,
                Sparse = r.Sparse,
            };

            try
            {
                await _cloneService.StartAsync(new CloneRequest(r.Url, r.TargetDirectory, options, r.ProfileId));
            }
            catch (InvalidOperationException ex)
            {
                _notifications.Publish(NotificationSeverity.Error, ex.Message);
                return;
            }

            if (FindClonedRepository(_workspace.OpenRepositories, r.TargetDirectory) is { } cloned)
                await OfferSubmoduleInit(cloned);
        }
        catch (Exception ex)
        {
            Fail("Could not clone repository", ex);
        }
    }

    /// <summary>A freshly cloned repository with a top-level `.gitmodules` leaves its
    /// submodules uninitialized unless the user opts in here — matches how "Init & update"
    /// already behaves from the sidebar (non-destructive, so a plain <c>ConfirmAsync</c>).</summary>
    [RelayCommand]
    private async Task OfferSubmoduleInit(RepositoryContext? after)
    {
        if (after is null || after.IsBare || !File.Exists(Path.Combine(after.RepositoryPath, ".gitmodules")))
            return;

        try
        {
            var proceed = await _dialogs.ConfirmAsync(
                "Initialize submodules?",
                "This repository has submodules. Initialize and check them out now?",
                confirmLabel: "Initialize");
            if (!proceed) return;

            _notifications.Publish(NotificationSeverity.Info, "Initializing submodules…");
            try
            {
                await _submodules.InitAndUpdateAsync(after, null, true, after.Lifetime);
                await after.State.RefreshAllAsync(after.Lifetime);
                _notifications.Publish(NotificationSeverity.Success, "Submodules initialized.");
            }
            catch (Exception ex)
            {
                Fail("Could not initialize submodules", ex);
            }
        }
        catch (Exception ex)
        {
            Fail("Could not initialize submodules", ex);
        }
    }

    // A row is opened by clicking it, which is ignored while the row is missing, another open is
    // in flight or a clone is running.
    internal async Task OpenRecentAsync(RecentRepoViewModel row)
    {
        if (row.IsStale || _busy || _cloneService.IsRunning)
            return;
        await OpenPathCoreAsync(row.Path);
    }

    internal async Task RemoveRecentAsync(RecentRepoViewModel row)
    {
        try
        {
            await _recents.RemoveAsync(row.Path);
        }
        catch (Exception ex)
        {
            AppLog.Write(AppLogLevel.Warn, "home", "Removing a recent repository failed.", ex);
        }
    }

    internal void RevealRecent(RecentRepoViewModel row) => _launcher.RevealInFileManager(row.Path);

    internal async Task CopyRecentPathAsync(RecentRepoViewModel row)
    {
        try
        {
            await _clipboard.SetTextAsync(row.Path);
        }
        catch (Exception ex)
        {
            // A busy system clipboard is routine; losing a copy must not take the app down.
            AppLog.Write(AppLogLevel.Warn, "home", "Copying a repository path to the clipboard failed.", ex);
        }
    }

    private void Fail(string what, Exception ex)
    {
        AppLog.Write(AppLogLevel.Warn, "home", what + ".", ex);
        _notifications.Publish(NotificationSeverity.Error, $"{what}: {ex.Message}", ex.NotificationDetails());
    }

    /// <summary>Parent of the most recently opened repo, else the user's home folder.</summary>
    private string DefaultCloneRoot()
    {
        var recent = Recents.FirstOrDefault()?.Path;
        if (!string.IsNullOrEmpty(recent))
        {
            var parent = Path.GetDirectoryName(recent.TrimEnd('/', '\\'));
            if (!string.IsNullOrEmpty(parent))
                return parent;
        }
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    public static bool PathsEqual(string a, string b)
    {
        var normA = a.Replace('/', '\\').TrimEnd('/', '\\');
        var normB = b.Replace('/', '\\').TrimEnd('/', '\\');
        return string.Equals(normA, normB,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    public static RepositoryContext? FindClonedRepository(IEnumerable<RepositoryContext> open, string targetDirectory) =>
        open.FirstOrDefault(ctx => PathsEqual(ctx.RepositoryPath, targetDirectory));

    /// <summary>The folder name a clone URL implies: its last path segment without a trailing
    /// <c>.git</c>.</summary>
    public static string GuessRepoName(string url)
    {
        var trimmed = url.Trim().TrimEnd('/');
        var lastSlash = trimmed.LastIndexOfAny(['/', ':']);
        var name = lastSlash >= 0 ? trimmed[(lastSlash + 1)..] : trimmed;
        return name.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }
}
