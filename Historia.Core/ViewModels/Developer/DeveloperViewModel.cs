namespace Historia.ViewModels;

/// <summary>
/// The developer-only tools panel: the custom-theme override, deliberate failures and sample
/// notices for testing, cache clearing, and the wipe of all Historia data. The custom-theme row
/// reads the saved themes and the stored selection once, when the panel is built.
/// </summary>
public sealed partial class DeveloperViewModel : ViewModelBase
{
    private readonly ISettingsService _settings;
    private readonly IThemeService _theme;
    private readonly INavigationBus _nav;
    private readonly IWorkspaceService _workspace;
    private readonly INotificationService _notifications;
    private readonly IDialogService _dialogs;
    private readonly IExternalLauncher _launcher;
    private readonly IGraphLayoutService _graphLayout;
    private readonly IRepoStateCache _repoStateCache;
    private readonly IReadOnlyList<CustomTheme> _savedThemes;
    private readonly Func<bool> _getAutoFetchStatus;
    private readonly Action<bool> _setAutoFetchStatus;
    private readonly Func<int> _wipeLocalData;

    private int _selectedThemeIndex;
    private bool _autoFetchStatusFallback;
    private readonly bool _ready;

    /// <param name="getAutoFetchStatus">Reads the session-wide switch for the auto-fetch colour bar on tabs;
    /// without one the switch is kept in this view model.</param>
    /// <param name="setAutoFetchStatus">Writes that switch.</param>
    /// <param name="wipeLocalData">Deletes every Historia file and returns how many were removed;
    /// defaults to <see cref="AppPaths.WipeAllLocalData"/>.</param>
    public DeveloperViewModel(
        ISettingsService settings,
        IThemeService theme,
        ICustomThemeService customThemes,
        INavigationBus nav,
        IWorkspaceService workspace,
        INotificationService notifications,
        IDialogService dialogs,
        IExternalLauncher launcher,
        IGraphLayoutService graphLayout,
        IRepoStateCache repoStateCache,
        Func<bool>? getAutoFetchStatus = null,
        Action<bool>? setAutoFetchStatus = null,
        Func<int>? wipeLocalData = null)
    {
        _settings = settings;
        _theme = theme;
        _nav = nav;
        _workspace = workspace;
        _notifications = notifications;
        _dialogs = dialogs;
        _launcher = launcher;
        _graphLayout = graphLayout;
        _repoStateCache = repoStateCache;
        _getAutoFetchStatus = getAutoFetchStatus ?? (() => _autoFetchStatusFallback);
        _setAutoFetchStatus = setAutoFetchStatus ?? (value => _autoFetchStatusFallback = value);
        _wipeLocalData = wipeLocalData ?? AppPaths.WipeAllLocalData;

        _savedThemes = customThemes.List();
        ThemeNames = _savedThemes.Select(t => t.Name).ToList();
        UseCustomTheme = settings.Current.UseCustomTheme;
        var stored = _savedThemes.ToList().FindIndex(t => t.Id == settings.Current.CustomThemeId);
        _selectedThemeIndex = stored >= 0 ? stored : 0;
        ShowAutoFetchStatus = _getAutoFetchStatus();
        _ready = true;
    }

    /// <summary>The names of the saved custom themes, in list order.</summary>
    public IReadOnlyList<string> ThemeNames { get; }

    /// <summary>True when at least one custom theme is saved, so there is something to pick.</summary>
    public bool HasSavedThemes => _savedThemes.Count > 0;

    /// <summary>The picked saved theme. The stored selection is kept whether or not the override is on;
    /// an index outside the list is ignored.</summary>
    public int SelectedThemeIndex
    {
        get => _selectedThemeIndex;
        set
        {
            if (value < 0 || value >= _savedThemes.Count || value == _selectedThemeIndex)
                return;

            _selectedThemeIndex = value;
            OnPropertyChanged();
            if (_ready && _savedThemes[value].Id != _settings.Current.CustomThemeId)
                _ = SelectThemeAsync(_savedThemes[value].Id);
        }
    }

    /// <summary>Replaces the Light / Dark / System choice while a saved theme is selected.</summary>
    [ObservableProperty]
    public partial bool UseCustomTheme { get; set; }

    /// <summary>Draws the auto-fetch state as a colour bar on each tab.</summary>
    [ObservableProperty]
    public partial bool ShowAutoFetchStatus { get; set; }

    public string LogsFolderHint => $"Opens {AppPaths.LogsDirectory}.";

    partial void OnUseCustomThemeChanged(bool value)
    {
        if (!_ready)
            return;

        _ = SetCustomThemeEnabledAsync(value);
        if (value && _savedThemes.Count == 0)
            _nav.RequestNavigate("themes");
    }

    partial void OnShowAutoFetchStatusChanged(bool value)
    {
        if (_ready)
            _setAutoFetchStatus(value);
    }

    private async Task SetCustomThemeEnabledAsync(bool enabled)
    {
        try
        {
            await _theme.SetCustomThemeEnabledAsync(enabled);
        }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage("Custom theme"), ex.NotificationDetails());
        }
    }

    private async Task SelectThemeAsync(string themeId)
    {
        try
        {
            await _theme.SelectCustomThemeAsync(themeId);
        }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage("Custom theme"), ex.NotificationDetails());
        }
    }

    [RelayCommand]
    private void ManageThemes() => _nav.RequestNavigate("themes");

    [RelayCommand]
    private void Close() => _nav.RequestNavigate(_workspace.Focused is not null ? "repo" : "");

    // Raised from a command on purpose: the panel exists to test crash handling, so the
    // exception must reach the unhandled-error path instead of being caught here.
    [RelayCommand]
    private void ThrowTestException() => throw new InvalidOperationException("Developer panel: deliberate test exception.");

    [RelayCommand]
    private void ShowSampleToast() => _notifications.Publish(NotificationSeverity.Success, "Commit created.");

    [RelayCommand]
    private async Task ShowSampleAlertAsync()
    {
        try
        {
            await _dialogs.ConfirmDestructiveAsync("Delete branch", "Delete 'sample-branch'?", "Delete");
        }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage("Sample alert"), ex.NotificationDetails());
        }
    }

    [RelayCommand]
    private void OpenLogsFolder() => _launcher.OpenFolder(AppPaths.LogsDirectory);

    [RelayCommand]
    private void ClearGraphCache()
    {
        try
        {
            var removed = _graphLayout.ClearPersistentCache();
            _notifications.Publish(
                NotificationSeverity.Success,
                removed == 0 ? "No persisted graph cache to remove." : $"Removed {removed} cached graph file(s).");
        }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage("Clear graph cache"), ex.NotificationDetails());
        }
    }

    [RelayCommand]
    private void ClearSidebarCache()
    {
        try
        {
            var removed = _repoStateCache.WipeAll();
            _notifications.Publish(
                NotificationSeverity.Success,
                removed == 0 ? "No persisted repo cache to remove." : $"Removed {removed} cached repo file(s).");
        }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage("Clear sidebar cache"), ex.NotificationDetails());
        }
    }

    [RelayCommand]
    private async Task DeleteAllAsync()
    {
        try
        {
            var confirmed = await _dialogs.ConfirmDestructiveAsync(
                "Delete all Historia data?",
                "This permanently deletes every Historia setting, profile, repo binding, theme, saved "
                + "session, cached repository/graph file and log from Historia's data and cache folders. "
                + "It cannot be undone. Restart Historia afterwards.",
                "Delete");
            if (!confirmed)
                return;

            var removed = _wipeLocalData();
            _notifications.Publish(
                NotificationSeverity.Success,
                $"Removed {removed} file(s). Restart Historia for a clean state.");
        }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage("Delete all data"), ex.NotificationDetails());
        }
    }
}
