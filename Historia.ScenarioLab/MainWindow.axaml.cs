namespace Historia.ScenarioLab;

public partial class MainWindow : Window
{
    private LabSettings _currentSettings;

    private readonly ScriptRunner _runner = new();
    private CancellationTokenSource? _runCts;
    private bool _isRunning;
    private bool _isPurging;

    private bool SettingsValid => _currentSettings.IsComplete && _currentSettings.Validate(LabPaths.RepoRoot).Count == 0;

    public MainWindow()
    {
        InitializeComponent();
        _currentSettings = LabSettingsStore.Load();

        SettingsButton.Click += async (_, _) => await OpenSettingsAsync();
        ExecuteButton.Click += async (_, _) => await OnExecuteOrStopClickAsync();
        PurgeLocalsButton.Click += async (_, _) => await PurgeLocalsAsync();
        PurgeRemotesButton.Click += async (_, _) => await PurgeRemotesAsync();

        NavPanel.ScriptSelected += path => OnScriptSelected(path);
        NavPanel.ScriptRenamed += (oldPath, newPath) => OnNodeRenamed(oldPath, newPath);
        NavPanel.NodeDeleted += path => OnNodeDeleted(path);

        Opened += async (_, _) =>
        {
            UpdateSettingsStatus();
            UpdateUiState();
            if (!SettingsValid)
            {
                await OpenSettingsAsync();
            }
        };

        Closing += (_, _) =>
        {
            if (_isRunning)
            {
                _runCts?.Cancel();
                _runner.Stop();
            }
        };

        UpdateSettingsStatus();
        UpdateTitle();
        UpdateUiState();
        FileInfo.SetDisabled();
    }

    private void UpdateUiState()
    {
        var settingsValid = SettingsValid;
        var hasScript = !string.IsNullOrEmpty(ViewerPanel.CurrentPath);

        if (_isRunning)
        {
            ExecuteButton.Content = "Stop";
            ExecuteButton.Classes.Clear();
            ExecuteButton.Classes.Add("danger");
            ExecuteButton.IsEnabled = true;

            PurgeLocalsButton.IsEnabled = false;
            PurgeRemotesButton.IsEnabled = false;
            SettingsButton.IsEnabled = false;
            NavPanel.SetOperationsEnabled(false);
        }
        else if (_isPurging)
        {
            ExecuteButton.Content = "Execute";
            ExecuteButton.Classes.Clear();
            ExecuteButton.Classes.Add("primary");
            ExecuteButton.IsEnabled = false;

            PurgeLocalsButton.IsEnabled = false;
            PurgeRemotesButton.IsEnabled = false;
            SettingsButton.IsEnabled = false;
            NavPanel.SetOperationsEnabled(false);
        }
        else
        {
            ExecuteButton.Content = "Execute";
            ExecuteButton.Classes.Clear();
            ExecuteButton.Classes.Add("primary");
            ExecuteButton.IsEnabled = settingsValid && hasScript;

            PurgeLocalsButton.IsEnabled = settingsValid;
            PurgeRemotesButton.IsEnabled = settingsValid;
            SettingsButton.IsEnabled = true;
            NavPanel.SetOperationsEnabled(true);
        }
    }

    private async Task OnExecuteOrStopClickAsync()
    {
        if (_isRunning)
        {
#pragma warning disable S6966 // synchronous cancel is intended; it keeps the order here
            _runCts?.Cancel();
#pragma warning restore S6966
            _runner.Stop();
            return;
        }

        if (_isPurging)
        {
            return;
        }

        if (string.IsNullOrEmpty(ViewerPanel.CurrentPath))
        {
            LogOutput.AppendLine("No script open to execute.");
            return;
        }

        if (!SettingsValid)
        {
            LogOutput.AppendLine("Cannot execute: settings are incomplete.");
            return;
        }

        // 1. Counts for all 3 folders
        var c1 = FolderPurger.CountTopLevel(_currentSettings.Local1);
        var c2 = FolderPurger.CountTopLevel(_currentSettings.Local2);
        var c3 = FolderPurger.CountTopLevel(_currentSettings.Remotes);
        var total = c1 + c2 + c3;

        if (total > 0)
        {
            var scriptName = Path.GetFileName(ViewerPanel.CurrentPath);
            var msg = $"Purge Local 1 ({c1}), Local 2 ({c2}), Remotes ({c3}) and run '{scriptName}'?";
            var confirmed = await LabDialogs.ConfirmAsync(this, "Execute Scenario", msg, confirmText: "Purge & Run", danger: true);
            if (!confirmed) return;
        }

        // Lock UI immediately before purge begins
        _isRunning = true;
        UpdateUiState();
        _runCts = new CancellationTokenSource();

        try
        {
            // 2. Purge all three folders
            LogOutput.Clear();
            LogOutput.AppendLine("Purging Local 1, Local 2, and Remotes before run...");

            var purgeFailed = false;
            await Task.Run(() =>
            {
                try
                {
                    var r1 = FolderPurger.PurgeContents(_currentSettings.Local1, LabPaths.RepoRoot);
                    var r2 = FolderPurger.PurgeContents(_currentSettings.Local2, LabPaths.RepoRoot);
                    var r3 = FolderPurger.PurgeContents(_currentSettings.Remotes, LabPaths.RepoRoot);

                    Dispatcher.UIThread.Post(() =>
                    {
                        if (r1.Failures.Count > 0 || r2.Failures.Count > 0 || r3.Failures.Count > 0)
                        {
                            purgeFailed = true;
                            foreach (var f in r1.Failures) LogOutput.AppendLine($"  [FAIL Local 1] {f}");
                            foreach (var f in r2.Failures) LogOutput.AppendLine($"  [FAIL Local 2] {f}");
                            foreach (var f in r3.Failures) LogOutput.AppendLine($"  [FAIL Remotes] {f}");
                            LogOutput.AppendLine("[ERROR] Purge failed. Aborting execution.");
                        }
                        else
                        {
                            LogOutput.AppendLine($"Purged Local 1 ({r1.DeletedCount}), Local 2 ({r2.DeletedCount}), Remotes ({r3.DeletedCount}).");
                        }
                    });
                }
                catch (Exception ex)
                {
                    purgeFailed = true;
                    Dispatcher.UIThread.Post(() =>
                    {
                        LogOutput.AppendLine($"[ERROR] Purge failed: {ex.Message}");
                        LogOutput.AppendLine("[ERROR] Purge failed. Aborting execution.");
                    });
                }
            });

            if (purgeFailed) return;

            if (_runCts.IsCancellationRequested)
            {
                LogOutput.AppendLine("stopped");
                return;
            }

            // 3. Run
            LogOutput.AppendLine($"▶ {Path.GetFileName(ViewerPanel.CurrentPath)}");

            var scriptPath = ViewerPanel.CurrentPath;
            var res = await _runner.RunAsync(
                scriptPath,
                _currentSettings,
                line => LogOutput.AppendLine(line),
                _runCts.Token);

            if (res.WasStopped)
            {
                LogOutput.AppendLine("stopped");
            }
            else
            {
                LogOutput.AppendLine($"exit {res.ExitCode} in {res.Duration.TotalSeconds:F1}s");
            }
        }
        catch (Exception ex)
        {
            LogOutput.AppendLine($"[ERROR] Execution error: {ex.Message}");
        }
        finally
        {
            _isRunning = false;
            _runCts?.Dispose();
            _runCts = null;
            UpdateUiState();
        }
    }

    private void UpdateTitle()
    {
        var title = "Historia ScenarioLab";
        if (!string.IsNullOrEmpty(ViewerPanel.CurrentPath))
        {
            var fileName = Path.GetFileName(ViewerPanel.CurrentPath);
            title = $"Historia ScenarioLab — {fileName}";
        }
        Title = title;
    }

    private void OnScriptSelected(string newPath)
    {
        if (string.Equals(newPath, ViewerPanel.CurrentPath, StringComparison.OrdinalIgnoreCase))
            return;

        LoadScript(newPath);
    }

    private void OnNodeRenamed(string oldPath, string newPath)
    {
        if (ViewerPanel.CurrentPath is null) return;

        if (string.Equals(ViewerPanel.CurrentPath, oldPath, StringComparison.OrdinalIgnoreCase))
        {
            ViewerPanel.RetargetPath(newPath);
            StatusFilePathText.Text = newPath;
            UpdateTitle();
        }
        else if (ViewerPanel.CurrentPath.StartsWith(oldPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            var sub = ViewerPanel.CurrentPath.Substring(oldPath.Length);
            var updatedPath = newPath + sub;
            ViewerPanel.RetargetPath(updatedPath);
            StatusFilePathText.Text = updatedPath;
            UpdateTitle();
        }
    }

    private void OnNodeDeleted(string deletedPath)
    {
        if (ViewerPanel.CurrentPath is null) return;

        if (string.Equals(ViewerPanel.CurrentPath, deletedPath, StringComparison.OrdinalIgnoreCase) ||
            ViewerPanel.CurrentPath.StartsWith(deletedPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            ViewerPanel.Close();
            FileInfo.SetDisabled();
            StatusFilePathText.Text = "No script open";
            UpdateTitle();
            UpdateUiState();
        }
    }

    private void LoadScript(string path)
    {
        ViewerPanel.Load(path);
        FileInfo.SetScriptHeader(ViewerPanel.CurrentHeader.Short, ViewerPanel.CurrentHeader.Long);
        StatusFilePathText.Text = path;
        UpdateTitle();
        UpdateUiState();
    }

    private async Task PurgeLocalsAsync() =>
        await PurgeAsync("Purge Locals", "Local 1 and Local 2", (_currentSettings.Local1, "Local 1"), (_currentSettings.Local2, "Local 2"));

    private async Task PurgeRemotesAsync() =>
        await PurgeAsync("Purge Remotes", $"Remotes ({_currentSettings.Remotes})", (_currentSettings.Remotes, "Remotes"));

    private async Task PurgeAsync(string dialogTitle, string logLabel, params (string Path, string Label)[] targets)
    {
        if (_isRunning || _isPurging) return;

        if (!SettingsValid)
        {
            LogOutput.AppendLine("Cannot purge: settings are incomplete.");
            return;
        }

        var counts = targets.Select(t => (t.Path, t.Label, Count: FolderPurger.CountTopLevel(t.Path))).ToArray();
        var total = counts.Sum(c => c.Count);

        if (total > 0)
        {
            var parts = counts.Select(c => $"{c.Label} ({c.Count} items at '{c.Path}')");
            var msg = $"Purge {string.Join(" and ", parts)}?";
            var confirmed = await LabDialogs.ConfirmAsync(this, dialogTitle, msg, confirmText: "Purge", danger: true);
            if (!confirmed) return;
        }

        _isPurging = true;
        UpdateUiState();

        try
        {
            LogOutput.AppendLine($"Purging {logLabel}...");
            await Task.Run(() =>
            {
                try
                {
                    foreach (var target in targets)
                    {
                        var res = FolderPurger.PurgeContents(target.Path, LabPaths.RepoRoot);
                        Dispatcher.UIThread.Post(() =>
                        {
                            LogOutput.AppendLine($"Purged {res.DeletedCount} items from {target.Label}.");
                            foreach (var f in res.Failures)
                                LogOutput.AppendLine($"  [FAIL] {f}");
                        });
                    }
                }
                catch (Exception ex)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        LogOutput.AppendLine($"[ERROR] Purge failed: {ex.Message}");
                    });
                }
            });
        }
        finally
        {
            _isPurging = false;
            UpdateUiState();
        }
    }

    private void UpdateSettingsStatus()
    {
        if (SettingsValid)
        {
            StatusSettingsText.Text = string.Empty;
        }
        else
        {
            StatusSettingsText.Text = "Settings incomplete";
        }
    }

    private async Task OpenSettingsAsync()
    {
        var dialog = new SettingsWindow(_currentSettings);
        var result = await dialog.ShowDialog<bool>(this);
        if (result)
        {
            _currentSettings = dialog.Settings;
            UpdateSettingsStatus();
            UpdateUiState();
        }
    }
}
