namespace HistoriaApp.Tests;

[Trait("tier", "ui")]
public class DiffViewStagingTests : IDisposable
{
    private readonly string _repoDir;
    private readonly ServiceProvider _sp;
    private readonly RepositoryContext _context;
    private readonly FakePatchService _patchService;
    private readonly FakeNotificationService _notificationService;
    private readonly SyntaxThemeHost _syntaxHost;

    private sealed class StubThemeService : IThemeService
    {
        public EffectiveTheme Effective { get; set; } = EffectiveTheme.Dark;
        public ThemeMode CurrentMode => ThemeMode.Dark;
        public bool HighContrastEnabled => Effective == EffectiveTheme.HighContrast;
        public string EffectiveCssThemeName => Effective == EffectiveTheme.Light ? "light" : "dark";
        public IReadOnlyDictionary<string, Rgb> Palette => HistoriaPalette.For(Effective);
        public IReadOnlyList<Rgb> LaneColors => [];

        public event EventHandler<ThemeChangedEventArgs>? ThemeChanged;

        public Task ApplyModeAsync(ThemeMode mode, CancellationToken ct = default) => Task.CompletedTask;
        public bool CustomThemeActive => false;
        public Task SetCustomThemeEnabledAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;
        public Task SelectCustomThemeAsync(string themeId, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetHighContrastAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;

        public void ChangeTheme(EffectiveTheme newTheme)
        {
            var old = Effective;
            Effective = newTheme;
            ThemeChanged?.Invoke(this, new ThemeChangedEventArgs(old, newTheme));
        }
    }

    private sealed class FakePatchService : IPatchService
    {
        public FileDiff? LastSource { get; set; }
        public PatchScope LastScope { get; set; }
        public IReadOnlySet<DiffLineRef>? LastSelectedLines { get; set; }
        public bool LastReverse { get; set; }
        public int GeneratePatchCount { get; set; }
        public int CheckAsyncCount { get; set; }
        public int ApplyPatchCount { get; set; }
        public string? LastAppliedPatch { get; set; }
        public PatchTarget LastAppliedTarget { get; set; }
        public bool LastAppliedReverse { get; set; }
        public Exception? ThrowOnGenerate { get; set; }
        public Exception? ThrowOnApply { get; set; }
        public PatchCheckResult CheckResult { get; set; } = new(true, null);
        public TaskCompletionSource<bool>? CheckGate { get; set; }

        public string GeneratePatch(FileDiff source, PatchScope scope, IReadOnlySet<DiffLineRef>? selectedLines = null, bool reverse = false)
        {
            if (ThrowOnGenerate is not null)
            {
                throw ThrowOnGenerate;
            }

            LastSource = source;
            LastScope = scope;
            LastSelectedLines = selectedLines;
            LastReverse = reverse;
            GeneratePatchCount++;
            return "diff --git a/test.txt b/test.txt\n--- a/test.txt\n+++ b/test.txt\n";
        }

        public async Task<PatchCheckResult> CheckAsync(RepositoryContext context, string patch, PatchTarget target, bool reverse, CancellationToken ct = default)
        {
            CheckAsyncCount++;
            if (CheckGate is not null)
            {
                await CheckGate.Task;
            }
            return CheckResult;
        }

        public Task ApplyPatchAsync(RepositoryContext context, string patch, PatchTarget target, bool reverse, CancellationToken ct = default)
        {
            if (ThrowOnApply is not null)
            {
                throw ThrowOnApply;
            }
            LastAppliedPatch = patch;
            LastAppliedTarget = target;
            LastAppliedReverse = reverse;
            ApplyPatchCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeNotificationService : INotificationService
    {
        public List<Notification> Notifications { get; } = [];
        public IReadOnlyList<Notification> Current => Notifications;

        public event EventHandler<NotificationsChangedEventArgs>? Changed;

        public Notification Publish(
            NotificationSeverity severity,
            string message,
            string? details = null,
            IReadOnlyList<NotificationAction>? actions = null)
        {
            var item = new Notification
            {
                Id = Guid.NewGuid(),
                Severity = severity,
                Message = message,
                Details = details,
                CreatedAt = DateTimeOffset.Now,
                Actions = actions ?? [],
            };
            Notifications.Add(item);
            Changed?.Invoke(this, new NotificationsChangedEventArgs(Notifications));
            return item;
        }

        public void Dismiss(Guid id)
        {
            Notifications.RemoveAll(n => n.Id == id);
            Changed?.Invoke(this, new NotificationsChangedEventArgs(Notifications));
        }
    }

    private sealed class FakeSettingsService(AppSettings initial) : ISettingsService
    {
        public AppSettings Current { get; } = initial;
        public event EventHandler<SettingsChangedEventArgs>? Changed { add { } remove { } }
        public Task UpdateAsync(Func<AppSettings, AppSettings> mutate, CancellationToken ct = default) => Task.CompletedTask;
        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeLayoutService : IRepoLayoutService
    {
        public IReadOnlyList<GraphColumn> GlobalColumns => GraphColumnDefaults.All;
        public event EventHandler<RepoLayoutChangedEventArgs>? Changed { add { } remove { } }
        public RepoLayout Get(string repoPath) => new();
        public Task UpdateAsync(string repoPath, Func<RepoLayout, RepoLayout> mutate) => Task.CompletedTask;
        public Task UpdateGlobalAsync(Func<IReadOnlyList<GraphColumn>, IReadOnlyList<GraphColumn>> mutate) => Task.CompletedTask;
        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeVault : ICredentialVaultService
    {
        public Task StoreTokenAsync(string key, string token, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> RetrieveTokenAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task DeleteTokenAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
    }

    public DiffViewStagingTests()
    {
        _repoDir = TestScratch.NewDirectory("historia-diff-staging-");
        LibGit2Sharp.Repository.Init(_repoDir);

        File.WriteAllText(System.IO.Path.Combine(_repoDir, "test.txt"), "line 1\nline 2\nline 3\n");
        using (var repo = new LibGit2Sharp.Repository(_repoDir))
        {
            LibGit2Sharp.Commands.Stage(repo, "test.txt");
            var sig = new LibGit2Sharp.Signature("Test", "test@example.com", DateTimeOffset.Now);
            repo.Commit("initial commit", sig, sig, new LibGit2Sharp.CommitOptions());
        }

        File.WriteAllText(System.IO.Path.Combine(_repoDir, "test.txt"), "line 1 modified\nline 2\nline 3 modified\n");

        _patchService = new FakePatchService();
        _notificationService = new FakeNotificationService();
        var themeService = new StubThemeService();
        _syntaxHost = new SyntaxThemeHost(themeService);

        var sc = new ServiceCollection();
        sc.AddSingleton<ISettingsService>(new FakeSettingsService(new AppSettings { DefaultDiffView = DiffViewMode.Unified }));
        sc.AddSingleton<IPatchService>(_patchService);
        sc.AddSingleton<INotificationService>(_notificationService);
        sc.AddSingleton<IThemeService>(themeService);
        sc.AddSingleton(_syntaxHost);
        sc.AddSingleton<IRepoLayoutService>(new FakeLayoutService());
        sc.AddSingleton<ICredentialVaultService>(new FakeVault());

        var locator = new GitExecutableLocator();
        locator.ResolveAsync(null).GetAwaiter().GetResult();
        sc.AddSingleton<IGitExecutableLocator>(locator);
        sc.AddSingleton<IGitCliService, GitCliService>();
        _sp = sc.BuildServiceProvider();

        _context = RepositoryContext.Open(_repoDir, _sp);
    }

    public void Dispose()
    {
        try { _syntaxHost.Dispose(); } catch { }
        try { _context.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        try { _sp.Dispose(); } catch { }
        TestScratch.Delete(_repoDir);
    }

    [AvaloniaFact]
    public async Task Hunk_action_on_unstaged_diff_calls_GeneratePatch_and_ApplyPatchAsync_forward()
    {
        var req = new DiffRequest { Source = DiffSource.UnstagedChanges, Path = "test.txt" };
        var view = new DiffView(_context, req, _sp.GetService<ISettingsService>(), _sp);
        var presenter = await WaitForPresenterAsync(view);

        var combined = Assert.IsType<CombinedDiffPresenter>(presenter);
        Assert.True(combined.AllowStaging);
        Assert.Equal("Stage", combined.StageVerb);

        await view.ViewModel.ApplyHunkAsync(0);

        Assert.Equal(PatchScope.SingleHunk, _patchService.LastScope);
        Assert.NotNull(_patchService.LastSelectedLines);
        Assert.Contains(new DiffLineRef(0, 0), _patchService.LastSelectedLines);
        Assert.False(_patchService.LastReverse);
        Assert.False(_patchService.LastAppliedReverse);
        Assert.Equal(1, _patchService.ApplyPatchCount);
    }

    [AvaloniaFact]
    public async Task Hunk_action_on_staged_diff_calls_GeneratePatch_and_ApplyPatchAsync_reverse()
    {
        using (var repo = new LibGit2Sharp.Repository(_repoDir))
        {
            LibGit2Sharp.Commands.Stage(repo, "test.txt");
        }

        var req = new DiffRequest { Source = DiffSource.StagedChanges, Path = "test.txt" };
        var view = new DiffView(_context, req, _sp.GetService<ISettingsService>(), _sp);
        var presenter = await WaitForPresenterAsync(view);

        var combined = Assert.IsType<CombinedDiffPresenter>(presenter);
        Assert.True(combined.AllowStaging);
        Assert.Equal("Unstage", combined.StageVerb);

        await view.ViewModel.ApplyHunkAsync(0);

        Assert.Equal(PatchScope.SingleHunk, _patchService.LastScope);
        Assert.NotNull(_patchService.LastSelectedLines);
        Assert.Contains(new DiffLineRef(0, 0), _patchService.LastSelectedLines);
        Assert.True(_patchService.LastReverse);
        Assert.True(_patchService.LastAppliedReverse);
        Assert.Equal(1, _patchService.ApplyPatchCount);
    }

    [AvaloniaFact]
    public async Task Selection_action_passes_SelectedLines_scope_and_exact_refs_and_same_reverse()
    {
        var req = new DiffRequest { Source = DiffSource.UnstagedChanges, Path = "test.txt" };
        var view = new DiffView(_context, req, _sp.GetService<ISettingsService>(), _sp);
        await WaitForPresenterAsync(view);

        var refs = new List<DiffLineRef> { new(0, 1), new(0, 2) };
        await view.ViewModel.ApplySelectionAsync(refs);

        Assert.Equal(PatchScope.SelectedLines, _patchService.LastScope);
        Assert.NotNull(_patchService.LastSelectedLines);
        Assert.Equal(2, _patchService.LastSelectedLines.Count);
        Assert.Contains(new DiffLineRef(0, 1), _patchService.LastSelectedLines);
        Assert.Contains(new DiffLineRef(0, 2), _patchService.LastSelectedLines);
        Assert.False(_patchService.LastReverse);
        Assert.False(_patchService.LastAppliedReverse);
        Assert.Equal(1, _patchService.ApplyPatchCount);
    }

    [AvaloniaFact]
    public async Task Commit_diff_exposes_no_staging_actions()
    {
        using var repo = new LibGit2Sharp.Repository(_repoDir);
        var headId = repo.Head.Tip.Id.ToGitObjectId();
        var req = new DiffRequest { Source = DiffSource.Commit, CommitId = headId, Path = "test.txt" };
        var view = new DiffView(_context, req, _sp.GetService<ISettingsService>(), _sp);
        var presenter = await WaitForPresenterAsync(view);

        var combined = Assert.IsType<CombinedDiffPresenter>(presenter);
        Assert.False(combined.AllowStaging);
    }

    [AvaloniaFact]
    public async Task GeneratePatch_throwing_HistoriaGitException_publishes_warning_and_skips_apply()
    {
        _patchService.ThrowOnGenerate = new HistoriaGitException(GitErrorKind.Unknown, "This selection splits an end-of-file newline change.");

        var req = new DiffRequest { Source = DiffSource.UnstagedChanges, Path = "test.txt" };
        var view = new DiffView(_context, req, _sp.GetService<ISettingsService>(), _sp);
        await WaitForPresenterAsync(view);

        await view.ViewModel.ApplyHunkAsync(0);

        Assert.Single(_notificationService.Notifications);
        Assert.Equal(NotificationSeverity.Warning, _notificationService.Notifications[0].Severity);
        Assert.Contains("This selection splits an end-of-file newline change.", _notificationService.Notifications[0].Message);
        Assert.Equal(0, _patchService.ApplyPatchCount);
    }

    [AvaloniaFact]
    public async Task Truncated_diff_shows_too_large_message_not_no_changes()
    {
        // Create large file exceeding 50,000 lines
        var largeLines = new string[50_005];
        Array.Fill(largeLines, "large line content");
        File.WriteAllLines(System.IO.Path.Combine(_repoDir, "large.txt"), largeLines);

        var req = new DiffRequest { Source = DiffSource.UnstagedChanges, Path = "large.txt" };
        var view = new DiffView(_context, req, _sp.GetService<ISettingsService>(), _sp);
        var tb = await WaitForTextBlockAsync(view);

        Assert.Equal("File too large to diff inline", tb.Text);
        Assert.NotEqual("No changes.", tb.Text);
        Assert.Equal("File too large to diff inline", view.ViewModel.Message);
        Assert.False(view.ViewModel.ShowsDiff);
        Assert.True(tb.IsVisible);
    }

    [AvaloniaFact]
    public async Task Replacing_shown_DiffView_in_host_leaves_SyntaxThemeHost_LiveCount_at_new_view_count()
    {
        var window = new Window();
        var container = new ContentControl();
        window.Content = container;
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var req = new DiffRequest { Source = DiffSource.UnstagedChanges, Path = "test.txt" };

        var view1 = new DiffView(_context, req, _sp.GetService<ISettingsService>(), _sp);
        await WaitForPresenterAsync(view1);
        container.Content = view1;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, _syntaxHost.LiveCount);

        var view2 = new DiffView(_context, req, _sp.GetService<ISettingsService>(), _sp);
        await WaitForPresenterAsync(view2);
        container.Content = view2;
        Dispatcher.UIThread.RunJobs();

        // Must remain 1 (from view2 only), not 2 (leaked from view1)
        Assert.Equal(1, _syntaxHost.LiveCount);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task Hunk_row_button_stages_hunk_in_unified()
    {
        var window = new Window { Width = 800, Height = 600 };
        var req = new DiffRequest { Source = DiffSource.UnstagedChanges, Path = "test.txt" };
        var view = new DiffView(_context, req, _sp.GetService<ISettingsService>(), _sp);
        window.Content = view;
        window.Show();
        Dispatcher.UIThread.RunJobs();

        try
        {
            var presenter = await WaitForPresenterAsync(view);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            var button = view.GetVisualDescendants()
                .OfType<Button>()
                .FirstOrDefault(b => b.Content?.ToString() == "Stage hunk");
            Assert.NotNull(button);

            button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(PatchScope.SingleHunk, _patchService.LastScope);
            Assert.False(_patchService.LastReverse);
            Assert.Equal(1, _patchService.ApplyPatchCount);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task Hunk_row_button_unstages_in_staged_diff()
    {
        using (var repo = new LibGit2Sharp.Repository(_repoDir))
        {
            LibGit2Sharp.Commands.Stage(repo, "test.txt");
        }

        var window = new Window { Width = 800, Height = 600 };
        var req = new DiffRequest { Source = DiffSource.StagedChanges, Path = "test.txt" };
        var view = new DiffView(_context, req, _sp.GetService<ISettingsService>(), _sp);
        window.Content = view;
        window.Show();
        Dispatcher.UIThread.RunJobs();

        try
        {
            var presenter = await WaitForPresenterAsync(view);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            var button = view.GetVisualDescendants()
                .OfType<Button>()
                .FirstOrDefault(b => b.Content?.ToString() == "Unstage hunk");
            Assert.NotNull(button);

            button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(PatchScope.SingleHunk, _patchService.LastScope);
            Assert.True(_patchService.LastReverse);
            Assert.Equal(1, _patchService.ApplyPatchCount);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task Commit_diff_has_no_row_buttons_or_menu_items()
    {
        using var repo = new LibGit2Sharp.Repository(_repoDir);
        var headId = repo.Head.Tip.Id.ToGitObjectId();
        var window = new Window { Width = 800, Height = 600 };
        var req = new DiffRequest { Source = DiffSource.Commit, CommitId = headId, Path = "test.txt" };
        var view = new DiffView(_context, req, _sp.GetService<ISettingsService>(), _sp);
        window.Content = view;
        window.Show();
        Dispatcher.UIThread.RunJobs();

        try
        {
            var presenter = await WaitForPresenterAsync(view);
            var combined = Assert.IsType<CombinedDiffPresenter>(presenter);

            Assert.False(combined.AllowStaging);
            Assert.Null(combined.TextArea.ContextMenu);
            Assert.DoesNotContain(combined.TextArea.TextView.ElementGenerators, g => g is DiffHunkActionGenerator);

            var buttons = view.GetVisualDescendants()
                .OfType<Button>()
                .Where(b => b.Content?.ToString()?.Contains("hunk") == true);
            Assert.Empty(buttons);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task Context_menu_stage_selected_lines_sends_selected_refs()
    {
        var window = new Window { Width = 800, Height = 600 };
        var req = new DiffRequest { Source = DiffSource.UnstagedChanges, Path = "test.txt" };
        var view = new DiffView(_context, req, _sp.GetService<ISettingsService>(), _sp);
        window.Content = view;
        window.Show();
        Dispatcher.UIThread.RunJobs();

        try
        {
            var presenter = await WaitForPresenterAsync(view);
            var combined = Assert.IsType<CombinedDiffPresenter>(presenter);

            var line2 = combined.Document.GetLineByNumber(2);
            var line3 = combined.Document.GetLineByNumber(3);
            combined.TextArea.Selection = Selection.Create(combined.TextArea, line2.Offset, line3.EndOffset);

            var selectedRefs = combined.GetSelectedLineRefs();
            Assert.NotEmpty(selectedRefs);

            Assert.NotNull(combined.TextArea.ContextMenu);
            var item = combined.TextArea.ContextMenu.Items
                .OfType<MenuItem>()
                .FirstOrDefault(m => m.Header?.ToString()?.Contains("selected lines") == true);
            Assert.NotNull(item);
            Assert.True(item.IsEnabled);

            item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(PatchScope.SelectedLines, _patchService.LastScope);
            Assert.NotNull(_patchService.LastSelectedLines);
            Assert.NotEmpty(_patchService.LastSelectedLines);
            Assert.Equal(1, _patchService.ApplyPatchCount);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task Second_hunk_request_while_first_in_flight_is_ignored()
    {
        var req = new DiffRequest { Source = DiffSource.UnstagedChanges, Path = "test.txt" };
        var view = new DiffView(_context, req, _sp.GetService<ISettingsService>(), _sp);
        await WaitForPresenterAsync(view);

        var gate = new TaskCompletionSource<bool>();
        _patchService.CheckGate = gate;

        var task1 = view.ViewModel.ApplyHunkAsync(0);
        var task2 = view.ViewModel.ApplyHunkAsync(1);

        await task2;

        Assert.Equal(1, _patchService.GeneratePatchCount);
        Assert.Equal(1, _patchService.CheckAsyncCount);
        Assert.Equal(0, _patchService.ApplyPatchCount);

        _patchService.CheckGate = null;
        gate.SetResult(true);
        await task1;

        Assert.Equal(1, _patchService.GeneratePatchCount);
        Assert.Equal(1, _patchService.CheckAsyncCount);
        Assert.Equal(1, _patchService.ApplyPatchCount);

        await view.ViewModel.ApplyHunkAsync(0);
        Assert.Equal(2, _patchService.GeneratePatchCount);
        Assert.Equal(2, _patchService.CheckAsyncCount);
        Assert.Equal(2, _patchService.ApplyPatchCount);
    }

    private static IReadOnlyList<Button> HunkButtons(Control root)
    {
        Dispatcher.UIThread.RunJobs();
        return [.. root.GetVisualDescendants().OfType<Button>().Where(b => b.Content as string == "Stage hunk")];
    }

    private async Task<(DiffView View, Window Window)> ShowViewAsync()
    {
        var req = new DiffRequest { Source = DiffSource.UnstagedChanges, Path = "test.txt" };
        var view = new DiffView(_context, req, _sp.GetService<ISettingsService>(), _sp);
        var window = new Window { Width = 900, Height = 600, Content = view };
        window.Show();
        await WaitForPresenterAsync(view);
        Dispatcher.UIThread.RunJobs();
        return (view, window);
    }

    [AvaloniaFact]
    public async Task Hunk_buttons_disabled_while_a_hunk_applies()
    {
        var (view, window) = await ShowViewAsync();
        try
        {
            var before = HunkButtons(view);
            Assert.NotEmpty(before);
            Assert.All(before, b => Assert.True(b.IsEffectivelyEnabled));

            var gate = new TaskCompletionSource<bool>();
            _patchService.CheckGate = gate;
            var apply = view.ViewModel.ApplyHunkAsync(0);

            Assert.True(view.ViewModel.IsApplyingPatch);
            var combined = Assert.IsType<CombinedDiffPresenter>(view.PresenterForTesting);
            Assert.False(combined.HunkActionsEnabled);
            var during = HunkButtons(view);
            Assert.NotEmpty(during);
            Assert.All(during, b => Assert.False(b.IsEffectivelyEnabled));

            _patchService.CheckGate = null;
            gate.SetResult(true);
            await apply;
            await WaitForPresenterAsync(view);

            Assert.False(view.ViewModel.IsApplyingPatch);
            var after = HunkButtons(view);
            Assert.NotEmpty(after);
            Assert.All(after, b => Assert.True(b.IsEffectivelyEnabled));
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task Hunk_buttons_reenable_after_a_failed_apply()
    {
        var (view, window) = await ShowViewAsync();
        try
        {
            var gate = new TaskCompletionSource<bool>();
            _patchService.CheckGate = gate;
            _patchService.ThrowOnApply = new InvalidOperationException("simulated failure");
            var apply = view.ViewModel.ApplyHunkAsync(0);
            Assert.All(HunkButtons(view), b => Assert.False(b.IsEffectivelyEnabled));

            _patchService.CheckGate = null;
            gate.SetResult(true);
            await apply;

            Assert.False(view.ViewModel.IsApplyingPatch);
            var after = HunkButtons(view);
            Assert.NotEmpty(after);
            Assert.All(after, b => Assert.True(b.IsEffectivelyEnabled));
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task Selection_request_while_hunk_in_flight_is_ignored()
    {
        var req = new DiffRequest { Source = DiffSource.UnstagedChanges, Path = "test.txt" };
        var view = new DiffView(_context, req, _sp.GetService<ISettingsService>(), _sp);
        await WaitForPresenterAsync(view);

        var gate = new TaskCompletionSource<bool>();
        _patchService.CheckGate = gate;

        var task1 = view.ViewModel.ApplyHunkAsync(0);
        var task2 = view.ViewModel.ApplySelectionAsync([new DiffLineRef(0, 1)]);

        await task2;

        Assert.Equal(1, _patchService.GeneratePatchCount);
        Assert.Equal(PatchScope.SingleHunk, _patchService.LastScope);

        _patchService.CheckGate = null;
        gate.SetResult(true);
        await task1;

        Assert.Equal(1, _patchService.ApplyPatchCount);
        Assert.Equal(PatchScope.SingleHunk, _patchService.LastScope);
    }

    [AvaloniaFact]
    public async Task Guard_clears_after_refused_check()
    {
        var req = new DiffRequest { Source = DiffSource.UnstagedChanges, Path = "test.txt" };
        var view = new DiffView(_context, req, _sp.GetService<ISettingsService>(), _sp);
        await WaitForPresenterAsync(view);

        _patchService.CheckResult = new(false, "conflict");
        await view.ViewModel.ApplyHunkAsync(0);

        Assert.Equal(1, _patchService.GeneratePatchCount);
        Assert.Equal(0, _patchService.ApplyPatchCount);
        Assert.Contains(_notificationService.Notifications, n => n.Severity == NotificationSeverity.Warning && n.Message.Contains("conflict"));

        _patchService.CheckResult = new(true, null);
        await view.ViewModel.ApplyHunkAsync(0);

        Assert.Equal(2, _patchService.GeneratePatchCount);
        Assert.Equal(1, _patchService.ApplyPatchCount);
    }

    [AvaloniaFact]
    public async Task Guard_clears_after_exception()
    {
        var req = new DiffRequest { Source = DiffSource.UnstagedChanges, Path = "test.txt" };
        var view = new DiffView(_context, req, _sp.GetService<ISettingsService>(), _sp);
        await WaitForPresenterAsync(view);

        _patchService.ThrowOnApply = new InvalidOperationException("simulated failure");
        await view.ViewModel.ApplyHunkAsync(0);

        Assert.Equal(1, _patchService.GeneratePatchCount);
        Assert.Contains(_notificationService.Notifications, n => n.Severity == NotificationSeverity.Error && n.Message.Contains("simulated failure"));

        _patchService.ThrowOnApply = null;
        await view.ViewModel.ApplyHunkAsync(0);

        Assert.Equal(2, _patchService.GeneratePatchCount);
        Assert.Equal(1, _patchService.ApplyPatchCount);
    }

    private static async Task<Control> WaitForPresenterAsync(DiffView view)
    {
        for (var i = 0; i < 50; i++)
        {
            Dispatcher.UIThread.RunJobs();
            if (view.PresenterForTesting is { } presenter and not TextBlock)
            {
                return presenter;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("Presenter did not load in time.");
    }

    private static async Task<TextBlock> WaitForTextBlockAsync(DiffView view)
    {
        for (var i = 0; i < 50; i++)
        {
            Dispatcher.UIThread.RunJobs();
            if (view.PresenterForTesting is TextBlock tb && tb.Text != "Loading diff...")
            {
                return tb;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("TextBlock did not load in time.");
    }
}
