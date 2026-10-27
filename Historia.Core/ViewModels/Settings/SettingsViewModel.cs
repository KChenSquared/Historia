namespace Historia.ViewModels;

/// <summary>
/// The settings page. Every row is a bound property that saves through
/// <see cref="ISettingsService.UpdateAsync"/> when the user changes it, and the page follows the
/// service, so a change made anywhere shows here without rebuilding anything. Numeric fields and
/// text boxes commit when the view says they lost focus; the historia-cmdr PATH row confirms
/// before it changes the user's PATH.
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    /// <summary>How long to let a pending key release land before a dialog opens over the field
    /// that the key came from.</summary>
    public static readonly TimeSpan KeyReleaseSettle = TimeSpan.FromMilliseconds(150);

    private static readonly WheelScrollDirection[] WheelDirections =
        [WheelScrollDirection.Auto, WheelScrollDirection.Standard, WheelScrollDirection.Inverted];

    private static readonly DiffViewMode[] DiffViews = [DiffViewMode.Unified, DiffViewMode.SideBySide];

    private static readonly TerminalCursorStyle[] CursorStyles =
        [TerminalCursorStyle.Block, TerminalCursorStyle.Underline, TerminalCursorStyle.Bar];

    private readonly ISettingsService _cfg;
    private readonly IThemeService _theme;
    private readonly IDialogService _dialogs;
    private readonly IRepoStateCache _repoCache;
    private readonly ICmdrPathService _cmdrPath;
    private readonly IUiDispatcher _ui;
    private readonly INavigationBus _nav;
    private readonly IWorkspaceService _workspace;
    private readonly IAiIntegrationsPanel? _aiPanel;

    private CmdrPathStatus? _cmdrStatus;
    private bool _cmdrApplyInProgress;

    // True while the page is being brought in line with the settings, so the property changes
    // that does are not saved back. It starts true and is released at the end of construction.
    private bool _syncing = true;

    /// <param name="developerMode">Whether the Developer panel's custom theme overrides Light,
    /// Dark and System, which the General hint mentions.</param>
    /// <param name="aiPanel">The AI Integrations window; the AI section exists only with it.</param>
    public SettingsViewModel(
        ISettingsService cfg,
        IThemeService theme,
        IDialogService dialogs,
        IRepoStateCache repoCache,
        ICmdrPathService cmdrPath,
        IUiDispatcher ui,
        INavigationBus nav,
        IWorkspaceService workspace,
        bool developerMode = false,
        IAiIntegrationsPanel? aiPanel = null)
    {
        _cfg = cfg;
        _theme = theme;
        _dialogs = dialogs;
        _repoCache = repoCache;
        _cmdrPath = cmdrPath;
        _ui = ui;
        _nav = nav;
        _workspace = workspace;
        _aiPanel = aiPanel;

        var generalHint = "Graph columns are set in Repository settings: click the cog in the graph's GRAPH header.";
        if (developerMode)
            generalHint += "\n\nIn developer mode, a custom theme from the Developer panel overrides Light, Dark and System.";
        GeneralHint = generalHint;

        General = new SettingsSectionViewModel("General", generalHint);
        Network = new SettingsSectionViewModel("Privacy & Network", "Identity and SSH-only are set per repository, in Repository settings.");
        Diff = new SettingsSectionViewModel("Diff viewer");
        Git = new SettingsSectionViewModel("Git");
        Terminal = new SettingsSectionViewModel("Terminal");
        Advanced = new SettingsSectionViewModel("Advanced");
        if (aiPanel is not null)
        {
            Ai = new SettingsSectionViewModel("AI Integration", null, open =>
            {
                if (open)
                    aiPanel.Open();
                else
                    aiPanel.Close();
            });
            Ai.Sync(aiPanel.IsOpen);
        }

        MaxTabRows = new NumericSettingViewModel(2, 5, 1, v => Math.Clamp(v, 2, 5),
            next => SaveAsync(s => s with { MaxTabRows = next }));
        FixedTabWidth = new NumericSettingViewModel(AppSettings.MinFixedTabWidth, AppSettings.MaxFixedTabWidth, 4,
            AppSettings.ClampFixedTabWidth, next => SaveAsync(s => s with { FixedTabWidth = next }));
        DiffContextLines = new NumericSettingViewModel(0, 20, 1, v => v,
            next => SaveAsync(s => s with { DiffContextLines = next }));
        TerminalFontSize = new NumericSettingViewModel(8, 32, 1, v => v,
            next => SaveAsync(s => s with { TerminalFontSize = next }));
        TerminalScrollbackLines = new NumericSettingViewModel(AppSettings.MinTerminalScrollback, AppSettings.MaxTerminalScrollback, 500,
            AppSettings.ClampTerminalScrollback, next => SaveAsync(s => s with { TerminalScrollbackLines = next }));
        SilentCrawlRowCap = new NumericSettingViewModel(AppSettings.MinSilentCrawlRowCap, AppSettings.MaxSilentCrawlRowCap, 10_000,
            AppSettings.ClampSilentCrawlRowCap, next => SaveAsync(s => s with { SilentCrawlRowCap = next }));
        ResidentRowCap = new NumericSettingViewModel(AppSettings.MinResidentRowCap, AppSettings.MaxResidentRowCap, 100_000,
            AppSettings.ClampResidentRowCap, next => SaveAsync(s => s with { ResidentRowCap = next }), ConfirmResidentRowCapAsync);
        CmdrQueueExpiryHours = new NumericSettingViewModel(AppSettings.MinCmdrQueueExpiryHours, AppSettings.MaxCmdrQueueExpiryHours, 1,
            AppSettings.ClampCmdrQueueExpiryHours, next => SaveAsync(s => s with { CmdrQueueExpiryHours = next }));

        IssueAutolinks = [];
        TerminalShellPath = "";
        TerminalShellArgs = "";
        TerminalFontFamily = "";
        Refresh();
        _syncing = false;
        RefreshCmdrStatus();

        Subscribe(() => _cfg.Changed += OnSettingsChanged, () => _cfg.Changed -= OnSettingsChanged);
        Subscribe(() => _theme.ThemeChanged += OnThemeChanged, () => _theme.ThemeChanged -= OnThemeChanged);
        if (_aiPanel is not null)
            Subscribe(() => _aiPanel.OpenChanged += OnAiPanelChanged, () => _aiPanel.OpenChanged -= OnAiPanelChanged);
    }

    public SettingsSectionViewModel General { get; }

    public SettingsSectionViewModel Network { get; }

    public SettingsSectionViewModel Diff { get; }

    public SettingsSectionViewModel Git { get; }

    public SettingsSectionViewModel Terminal { get; }

    public SettingsSectionViewModel Advanced { get; }

    /// <summary>The AI Integration section; null without the AI Integrations window.</summary>
    public SettingsSectionViewModel? Ai { get; }

    public string GeneralHint { get; }

    public NumericSettingViewModel MaxTabRows { get; }

    public NumericSettingViewModel FixedTabWidth { get; }

    public NumericSettingViewModel DiffContextLines { get; }

    public NumericSettingViewModel TerminalFontSize { get; }

    public NumericSettingViewModel TerminalScrollbackLines { get; }

    public NumericSettingViewModel SilentCrawlRowCap { get; }

    public NumericSettingViewModel ResidentRowCap { get; }

    /// <summary>Hours an agent-queued item stays in the CMDR list before a launch removes it.</summary>
    public NumericSettingViewModel CmdrQueueExpiryHours { get; }

    /// <summary>The terminal font box's placeholder: the fonts the platform default tries.</summary>
    public string TerminalFontFamilyWatermark => OperatingSystem.IsWindows() ? "Cascadia Mono, Consolas" : "platform monospace";

    // ── Theme ──

    public bool IsLightMode => _theme.CurrentMode == ThemeMode.Light;

    public bool IsDarkMode => _theme.CurrentMode == ThemeMode.Dark;

    public bool IsSystemMode => _theme.CurrentMode == ThemeMode.System;

    [RelayCommand]
    private async Task SetThemeModeAsync(ThemeMode mode)
    {
        try
        {
            await _theme.ApplyModeAsync(mode);
        }
        catch (Exception ex)
        {
            AppLog.Write(AppLogLevel.Warn, "settings", "Could not apply the theme mode", ex);
        }
    }

    [ObservableProperty]
    public partial bool HighContrast { get; set; }

    partial void OnHighContrastChanged(bool value)
    {
        if (_syncing)
            return;
        _ = RunAsync(() => _theme.SetHighContrastAsync(value));
    }

    // ── General ──

    [ObservableProperty]
    public partial bool ShowSplashScreen { get; set; }

    partial void OnShowSplashScreenChanged(bool value) => Save(s => s with { ShowSplashScreen = value });

    [ObservableProperty]
    public partial bool EnableMultiLineTabs { get; set; }

    partial void OnEnableMultiLineTabsChanged(bool value) => Save(s => s with { EnableMultiLineTabs = value });

    [ObservableProperty]
    public partial bool UseFixedTabWidth { get; set; }

    partial void OnUseFixedTabWidthChanged(bool value)
        => Save(s => s with { TabWidthMode = value ? TabWidthMode.Fixed : TabWidthMode.Dynamic });

    [ObservableProperty]
    public partial bool ShowAuthorDate { get; set; }

    partial void OnShowAuthorDateChanged(bool value) => Save(s => s with { ShowAuthorDate = value });

    /// <summary>Index into Match system, Standard, Inverted.</summary>
    [ObservableProperty]
    public partial int WheelScrollDirectionIndex { get; set; }

    partial void OnWheelScrollDirectionIndexChanged(int value)
    {
        if (value < 0 || value >= WheelDirections.Length)
            return;
        var direction = WheelDirections[value];
        Save(s => s with { WheelScrollDirection = direction });
    }

    [ObservableProperty]
    public partial bool PersistRepoCache { get; set; }

    partial void OnPersistRepoCacheChanged(bool value)
    {
        if (_syncing)
            return;

        Save(s => s with { PersistRepoCache = value });
        if (value)
            return;

        // Turning the cache off also clears what is on disk.
        try
        {
            _repoCache.WipeAll();
        }
        catch (Exception ex)
        {
            AppLog.Write(AppLogLevel.Warn, "settings", "Could not clear the repository cache", ex);
        }
    }

    // ── Privacy and network ──

    [ObservableProperty]
    public partial bool AutoFetchEnabled { get; set; }

    partial void OnAutoFetchEnabledChanged(bool value) => Save(s => s with { AutoFetchEnabled = value });

    [ObservableProperty]
    public partial bool ExternalControlEnabled { get; set; }

    partial void OnExternalControlEnabledChanged(bool value) => Save(s => s with { ExternalControlEnabled = value });

    // ── Diff viewer ──

    /// <summary>Index into Unified, Side by side.</summary>
    [ObservableProperty]
    public partial int DefaultDiffViewIndex { get; set; }

    partial void OnDefaultDiffViewIndexChanged(int value)
    {
        if (value < 0 || value >= DiffViews.Length)
            return;
        var view = DiffViews[value];
        Save(s => s with { DefaultDiffView = view });
    }

    [ObservableProperty]
    public partial bool DiffWordWrap { get; set; }

    partial void OnDiffWordWrapChanged(bool value) => Save(s => s with { DiffWordWrap = value });

    // ── Git ──

    [ObservableProperty]
    public partial bool PruneOnFetch { get; set; }

    partial void OnPruneOnFetchChanged(bool value) => Save(s => s with { PruneOnFetch = value });

    [ObservableProperty]
    public partial bool PullWithRebase { get; set; }

    partial void OnPullWithRebaseChanged(bool value) => Save(s => s with { PullWithRebase = value });

    [ObservableProperty]
    public partial bool FetchAfterPull { get; set; }

    partial void OnFetchAfterPullChanged(bool value) => Save(s => s with { FetchAfterPull = value });

    [ObservableProperty]
    public partial bool RestoreSessionTabs { get; set; }

    partial void OnRestoreSessionTabsChanged(bool value) => Save(s => s with { RestoreSessionTabs = value });

    [ObservableProperty]
    public partial bool UseCustomStashNames { get; set; }

    partial void OnUseCustomStashNamesChanged(bool value) => Save(s => s with { UseCustomStashNames = value });

    // ── Terminal ──

    [ObservableProperty]
    public partial string TerminalShellPath { get; set; }

    partial void OnTerminalShellPathChanged(string value)
    {
        if (_syncing)
            return;
        var path = (value ?? "").Trim();
        Resync(() => TerminalShellPath = path);
        Save(s => s with { TerminalShellPath = path.Length == 0 ? null : path });
    }

    [RelayCommand]
    private async Task BrowseShellAsync()
    {
        try
        {
            var picked = await _dialogs.PickFileAsync("Terminal shell", "Executable|*.exe;*|All files|*");
            if (!string.IsNullOrWhiteSpace(picked))
                TerminalShellPath = picked;
        }
        catch (Exception ex)
        {
            AppLog.Write(AppLogLevel.Warn, "settings", "Could not pick a terminal shell", ex);
        }
    }

    /// <summary>The shell's arguments as one space-separated line.</summary>
    [ObservableProperty]
    public partial string TerminalShellArgs { get; set; }

    partial void OnTerminalShellArgsChanged(string value)
    {
        if (_syncing)
            return;
        var args = (value ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Resync(() => TerminalShellArgs = string.Join(' ', args));
        Save(s => s with { TerminalShellArgs = args });
    }

    [ObservableProperty]
    public partial string TerminalFontFamily { get; set; }

    partial void OnTerminalFontFamilyChanged(string value)
    {
        if (_syncing)
            return;
        var family = (value ?? "").Trim();
        Resync(() => TerminalFontFamily = family);
        Save(s => s with { TerminalFontFamily = family.Length == 0 ? null : family });
    }

    [ObservableProperty]
    public partial bool TerminalKittyKeyboard { get; set; }

    partial void OnTerminalKittyKeyboardChanged(bool value) => Save(s => s with { TerminalKittyKeyboard = value });

    [ObservableProperty]
    public partial bool TerminalAllowClipboardWrite { get; set; }

    partial void OnTerminalAllowClipboardWriteChanged(bool value) => Save(s => s with { TerminalAllowClipboardWrite = value });

    /// <summary>Index into Block, Underline, Bar.</summary>
    [ObservableProperty]
    public partial int TerminalCursorStyleIndex { get; set; }

    partial void OnTerminalCursorStyleIndexChanged(int value)
    {
        if (value < 0 || value >= CursorStyles.Length)
            return;
        var style = CursorStyles[value];
        Save(s => s with { TerminalCursorStyle = style });
    }

    [ObservableProperty]
    public partial bool TerminalCursorBlink { get; set; }

    partial void OnTerminalCursorBlinkChanged(bool value) => Save(s => s with { TerminalCursorBlink = value });

    // ── Advanced ──

    [ObservableProperty]
    public partial bool LoggingEnabled { get; set; }

    partial void OnLoggingEnabledChanged(bool value) => Save(s => s with { LoggingEnabled = value });

    [ObservableProperty]
    public partial bool AutoHideNotices { get; set; }

    partial void OnAutoHideNoticesChanged(bool value) => Save(s => s with { AutoHideNotices = value });

    [ObservableProperty]
    public partial bool GraphMetadataFromDisk { get; set; }

    partial void OnGraphMetadataFromDiskChanged(bool value) => Save(s => s with { GraphMetadataFromDisk = value });

    [ObservableProperty]
    public partial bool EnableGraphSplice { get; set; }

    partial void OnEnableGraphSpliceChanged(bool value) => Save(s => s with { EnableGraphSplice = value });

    /// <summary>Lightweight mode caps the rows in memory, which disables both row-cap fields.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RowCapsEnabled))]
    public partial bool LightweightMode { get; set; }

    partial void OnLightweightModeChanged(bool value) => Save(s => s with { LightweightMode = value });

    public bool RowCapsEnabled => !LightweightMode;

    [ObservableProperty]
    public partial bool DeveloperPanelEnabled { get; set; }

    partial void OnDeveloperPanelEnabledChanged(bool value) => Save(s => s with { DeveloperPanelEnabled = value });

    /// <summary>The issue-autolink rule rows, replaced as a whole when the saved rules differ.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIssueAutolinks))]
    public partial IReadOnlyList<IssueAutolinkRuleViewModel> IssueAutolinks { get; private set; }

    public bool HasIssueAutolinks => IssueAutolinks.Count > 0;

    [RelayCommand]
    private void AddRule()
        => Save(s => s with { IssueAutolinks = [.. s.IssueAutolinks, new IssueAutolinkRule { Pattern = "", UrlTemplate = "" }] });

    private void UpdateRule(IssueAutolinkRuleViewModel row, Func<IssueAutolinkRule, IssueAutolinkRule> edit)
    {
        var index = IndexOfRule(row);
        if (index < 0)
            return;
        Save(s => s with { IssueAutolinks = [.. s.IssueAutolinks.Select((r, j) => j == index ? edit(r) : r)] });
    }

    private void RemoveRule(IssueAutolinkRuleViewModel row)
    {
        var index = IndexOfRule(row);
        if (index < 0)
            return;
        Save(s => s with { IssueAutolinks = [.. s.IssueAutolinks.Where((_, j) => j != index)] });
    }

    private int IndexOfRule(IssueAutolinkRuleViewModel row)
    {
        for (var i = 0; i < IssueAutolinks.Count; i++)
        {
            if (ReferenceEquals(IssueAutolinks[i], row))
                return i;
        }

        return -1;
    }

    // ── historia-cmdr on the user PATH ──

    /// <summary>The last PATH status check, which finishes when its result has been posted to the UI thread.</summary>
    public Task CmdrStatusTask { get; private set; } = Task.CompletedTask;

    /// <summary>The row exists once the status is known and historia-cmdr is supported.</summary>
    public bool ShowCommandLine => _cmdrStatus is not null and not CmdrPathStatus.Unavailable;

    public string CommandLineButtonText => _cmdrStatus is CmdrPathStatus.Current ? "On PATH" : "Add to PATH";

    /// <summary>The PATH can be updated and no update is running or waiting for its new status.</summary>
    public bool CanAddToPath => _cmdrStatus is CmdrPathStatus.NeedsUpdate && !_cmdrApplyInProgress;

    private void RefreshCmdrStatus()
    {
        CmdrStatusTask = Task.Run(() =>
        {
            CmdrPathStatus status;
            try
            {
                status = _cmdrPath.GetStatus();
            }
            catch (Exception ex)
            {
                AppLog.Write(AppLogLevel.Warn, "cmdr-path", "Could not check historia-cmdr PATH status", ex);
                status = new CmdrPathStatus.Unavailable();
            }

            _ui.Post(() =>
            {
                if (IsDisposed)
                    return;
                _cmdrStatus = status;
                _cmdrApplyInProgress = false;
                NotifyCmdrChanged();
            });
        });
    }

    private void NotifyCmdrChanged()
    {
        OnPropertyChanged(nameof(ShowCommandLine));
        OnPropertyChanged(nameof(CommandLineButtonText));
        OnPropertyChanged(nameof(CanAddToPath));
        AddToPathCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanAddToPath))]
    private async Task AddToPathAsync()
    {
        try
        {
            if (_cmdrStatus is not CmdrPathStatus.NeedsUpdate update || _cmdrApplyInProgress)
                return;

            var preview = update.Preview;
            var effect = DescribePathChange(update);
            var confirmed = await _dialogs.ConfirmDestructiveAsync("Update PATH", effect, "Add to PATH");
            if (!confirmed)
                return;

            _cmdrApplyInProgress = true;
            NotifyCmdrChanged();

            try
            {
                var result = await Task.Run(() => _cmdrPath.Apply(preview));
                switch (result)
                {
                    case CmdrPathApplyResult.Written or CmdrPathApplyResult.AlreadyCurrent:
                        break;
                    case CmdrPathApplyResult.Changed:
                        await _dialogs.ShowMessageAsync("Update PATH", "PATH changed while the dialog was open; try again.");
                        break;
                    case CmdrPathApplyResult.Failed failed:
                        await _dialogs.ShowMessageAsync("Update PATH", failed.Message);
                        break;
                }
            }
            catch (Exception ex)
            {
                AppLog.Write(AppLogLevel.Error, "cmdr-path", "Failed to apply historia-cmdr to user PATH", ex);
                await _dialogs.ShowMessageAsync("Update PATH", ex.Message);
            }

            RefreshCmdrStatus();
        }
        catch (Exception ex)
        {
            AppLog.Write(AppLogLevel.Error, "cmdr-path", "The PATH update could not finish", ex);
        }
    }

    private static string DescribePathChange(CmdrPathStatus.NeedsUpdate update)
    {
        var preview = update.Preview;
        var ourFolder = update.OurFolder;
        var sb = new StringBuilder();
        if (preview.Added is not null)
            sb.AppendLine($"Adds {ourFolder} to your user PATH.");
        else
            sb.AppendLine($"Keeps {ourFolder} on your user PATH.");

        var duplicates = new List<string>();
        var removes = new List<string>();
        foreach (var r in preview.Removed)
        {
            if (UserPathEditor.SameFolder(r, ourFolder))
                duplicates.Add(r);
            else
                removes.Add(r);
        }

        if (removes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Removes from your user PATH:");
            foreach (var r in removes)
                sb.AppendLine(r);
        }

        if (duplicates.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Removes duplicates of that folder:");
            foreach (var d in duplicates)
                sb.AppendLine(d);
        }

        if (update.MachineCopies.Count > 0)
        {
            sb.AppendLine();
            foreach (var m in update.MachineCopies)
                sb.AppendLine($"A historia-cmdr in the system PATH ({m}) still runs first; an administrator must remove it.");
        }

        sb.AppendLine();
        sb.AppendLine($"Your current PATH is backed up to {update.BackupFolder} first.");
        sb.Append("Open a new terminal to use it.");
        return sb.ToString();
    }

    // ── Max rows in memory ──

    // A change in either direction is confirmed, and a value above the warning threshold gets a
    // second, heavier confirmation, so a large cap is never saved on one reflexive click.
    private async Task<bool> ConfirmResidentRowCapAsync(int next)
    {
        var proceed = await _dialogs.ConfirmDestructiveAsync(
            "Change the resident row cap?",
            "Changing this value affects how many rows the graph will keep resident in memory for large repositories. Applies after restart of Historia.",
            "Continue");

        if (proceed && next > AppSettings.ResidentRowCapWarnThreshold)
        {
            // The first dialog may have been confirmed from the keyboard, so the second waits
            // out the same key release the field's commit does.
            await Task.Delay(KeyReleaseSettle);
            proceed = await _dialogs.ConfirmDestructiveAsync(
                "Keep this many rows in memory?",
                $"Keeping more than {AppSettings.ResidentRowCapWarnThreshold:N0} rows in memory could drain memory heavily on large repositories. Do you really want to save {next:N0} rows?",
                "Save anyway");
        }

        return proceed;
    }

    // ── Navigation ──

    /// <summary>Closes the page: back to the focused repository, or Home when there is none.</summary>
    [RelayCommand]
    private void Close() => _nav.RequestNavigate(_workspace.Focused is not null ? "repo" : "");

    // ── Following the services ──

    // Every service raises on the thread that changed it; the page updates on the UI thread.
    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e) => _ui.Post(Refresh);

    private void OnThemeChanged(object? sender, ThemeChangedEventArgs e) => _ui.Post(Refresh);

    private void OnAiPanelChanged(object? sender, EventArgs e)
    {
        _ui.Post(() =>
        {
            if (!IsDisposed && _aiPanel is not null)
                Ai?.Sync(_aiPanel.IsOpen);
        });
    }

    private void Refresh()
    {
        if (IsDisposed)
            return;

        var c = _cfg.Current;
        var was = _syncing;
        _syncing = true;
        try
        {
            ShowSplashScreen = c.ShowSplashScreen;
            HighContrast = _theme.HighContrastEnabled;
            EnableMultiLineTabs = c.EnableMultiLineTabs;
            MaxTabRows.Sync(c.MaxTabRows);
            UseFixedTabWidth = c.TabWidthMode == TabWidthMode.Fixed;
            FixedTabWidth.Sync(c.FixedTabWidth);
            ShowAuthorDate = c.ShowAuthorDate;
            WheelScrollDirectionIndex = Math.Max(0, Array.IndexOf(WheelDirections, c.WheelScrollDirection));
            PersistRepoCache = c.PersistRepoCache;

            AutoFetchEnabled = c.AutoFetchEnabled;
            ExternalControlEnabled = c.ExternalControlEnabled;

            DefaultDiffViewIndex = Math.Max(0, Array.IndexOf(DiffViews, c.DefaultDiffView));
            DiffContextLines.Sync(c.DiffContextLines);
            DiffWordWrap = c.DiffWordWrap;

            PruneOnFetch = c.PruneOnFetch;
            PullWithRebase = c.PullWithRebase;
            FetchAfterPull = c.FetchAfterPull;
            RestoreSessionTabs = c.RestoreSessionTabs;
            UseCustomStashNames = c.UseCustomStashNames;

            TerminalShellPath = c.TerminalShellPath ?? "";
            TerminalShellArgs = string.Join(' ', c.TerminalShellArgs);
            TerminalFontFamily = c.TerminalFontFamily ?? "";
            TerminalFontSize.Sync(c.TerminalFontSize);
            TerminalScrollbackLines.Sync(c.TerminalScrollbackLines);
            TerminalKittyKeyboard = c.TerminalKittyKeyboard;
            TerminalAllowClipboardWrite = c.TerminalAllowClipboardWrite;
            TerminalCursorStyleIndex = Math.Max(0, Array.IndexOf(CursorStyles, c.TerminalCursorStyle));
            TerminalCursorBlink = c.TerminalCursorBlink;

            LoggingEnabled = c.LoggingEnabled;
            AutoHideNotices = c.AutoHideNotices;
            GraphMetadataFromDisk = c.GraphMetadataFromDisk;
            EnableGraphSplice = c.EnableGraphSplice;
            LightweightMode = c.LightweightMode;
            SilentCrawlRowCap.Sync(c.SilentCrawlRowCap);
            ResidentRowCap.Sync(c.ResidentRowCap);
            CmdrQueueExpiryHours.Sync(c.CmdrQueueExpiryHours);
            SyncAutolinks(c.IssueAutolinks);
            DeveloperPanelEnabled = c.DeveloperPanelEnabled;
        }
        finally
        {
            _syncing = was;
        }

        OnPropertyChanged(nameof(IsLightMode));
        OnPropertyChanged(nameof(IsDarkMode));
        OnPropertyChanged(nameof(IsSystemMode));
    }

    // Rows are replaced only when the saved rules differ from what the rows show, so a box the
    // user is typing in keeps its focus while another setting changes.
    private void SyncAutolinks(IReadOnlyList<IssueAutolinkRule> rules)
    {
        var rows = IssueAutolinks;
        if (rows.Count == rules.Count)
        {
            var same = true;
            for (var i = 0; i < rows.Count && same; i++)
                same = rows[i].Shows(rules[i]);
            if (same)
                return;
        }

        IssueAutolinks = [.. rules.Select(r => new IssueAutolinkRuleViewModel(r, UpdateRule, RemoveRule))];
    }

    // ── Saving ──

    private void Save(Func<AppSettings, AppSettings> mutate)
    {
        if (_syncing)
            return;
        _ = SaveAsync(mutate);
    }

    private async Task SaveAsync(Func<AppSettings, AppSettings> mutate)
    {
        try
        {
            await _cfg.UpdateAsync(mutate);
        }
        catch (Exception ex)
        {
            AppLog.Write(AppLogLevel.Warn, "settings", "Could not save a setting", ex);
        }
    }

    private static async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            AppLog.Write(AppLogLevel.Warn, "settings", "A setting could not be applied", ex);
        }
    }

    // Writes a normalised value back into a box without saving it again.
    private void Resync(Action set)
    {
        var was = _syncing;
        _syncing = true;
        try
        {
            set();
        }
        finally
        {
            _syncing = was;
        }
    }
}
