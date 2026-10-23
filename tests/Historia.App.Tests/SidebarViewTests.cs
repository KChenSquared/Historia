namespace HistoriaApp.Tests;

/// <summary>
/// The sidebar column: the Branches/CMDR toggle under the Branch shortcut bar, its unseen-count
/// circle, and the agent queue's rows, which open on a press and leave on the trash button.
/// </summary>
[Trait("tier", "ui")]
public sealed class SidebarViewTests
{
    /// <summary>An in-memory queue that raises <see cref="Changed"/> inline.</summary>
    internal sealed class FakeQueue : ICmdrQueueService
    {
        private readonly List<CmdrQueueItem> _items = [];

        public IReadOnlyList<CmdrQueueItem> Items => [.. _items];
        public int UnseenCount => _items.Count(i => !i.Seen);
        public event EventHandler? Changed;

        public void Enqueue(CmdrQueueItem item)
        {
            _items.Add(item);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void MarkSeen(string id)
        {
            var at = _items.FindIndex(i => i.Id == id);
            if (at < 0)
                return;
            _items[at] = _items[at] with { Seen = true };
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Remove(string id)
        {
            if (_items.RemoveAll(i => i.Id == id) > 0)
                Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>A navigator that records each target and succeeds at once.</summary>
    internal sealed class FakeNavigator : IControlNavigator
    {
        public List<ControlTarget> Targets { get; } = [];

        public Task<(ControlResponse Response, string? RepoName)> NavigateAsync(ControlTarget target, CancellationToken ct)
        {
            Targets.Add(target);
            return Task.FromResult<(ControlResponse, string?)>((new ControlResponse { Ok = true, Repo = target.RepoPath }, "app"));
        }
    }

    /// <summary>A dialog service that records that it was asked anything and always declines.</summary>
    private sealed class RecordingDialogs : IDialogService
    {
        public List<string> Calls { get; } = [];

        public Task<string?> PickFolderAsync(string title, string? initialDirectory = null) { Calls.Add(title); return Task.FromResult<string?>(null); }
        public Task<string?> PickFileAsync(string title, string filter, string? initialDirectory = null) { Calls.Add(title); return Task.FromResult<string?>(null); }
        public Task<bool> ConfirmAsync(string title, string message, bool destructive = false, string confirmLabel = "OK",
            bool defaultToCancel = false, DialogIcon icon = DialogIcon.None)
        { Calls.Add(title); return Task.FromResult(false); }
        public Task<bool> ConfirmDestructiveAsync(string title, string effect, string confirmLabel) { Calls.Add(title); return Task.FromResult(false); }
        public Task<int> ChooseAsync(string title, string message, IReadOnlyList<string> options, int defaultIndex = 0,
            int destructiveIndex = -1, DialogIcon icon = DialogIcon.None)
        { Calls.Add(title); return Task.FromResult(-1); }
        public Task ShowMessageAsync(string title, string message) { Calls.Add(title); return Task.CompletedTask; }
        public Task<string?> PromptAsync(string title, string message, string? defaultValue = null, string confirmLabel = "OK",
            bool primaryConfirm = false)
        { Calls.Add(title); return Task.FromResult<string?>(null); }
    }

    private sealed class FakeThemeProbe : ISystemThemeProbe
    {
        public bool IsLightTheme => false;
        public bool IsHighContrast => false;
#pragma warning disable CS0067
        public event EventHandler? Changed;
#pragma warning restore CS0067
    }

    private sealed class FakeVault : ICredentialVaultService
    {
        public Task StoreTokenAsync(string key, string token, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> RetrieveTokenAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task DeleteTokenAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class Harness : IDisposable
    {
        public FakeQueue Queue { get; } = new();
        public FakeNavigator Navigator { get; } = new();
        public RecordingDialogs Dialogs { get; } = new();
        public ServiceProvider Services { get; }
        public SidebarView View { get; }
        public Window Window { get; }

        public Harness(params CmdrQueueItem[] items)
        {
            foreach (var item in items)
                Queue.Enqueue(item);

            var sc = new ServiceCollection();
            var settings = SettingsService.Load();
            sc.AddSingleton<ISettingsService>(settings);
            sc.AddSingleton<SettingsService>(settings);
            sc.AddSingleton<IUiDispatcher, Historia.Platform.UiDispatcher>();
            sc.AddSingleton<IDialogService>(Dialogs);
            sc.AddSingleton<IScrollDirectionProbe, Historia.Platform.ScrollDirectionProbe>();
            sc.AddSingleton<IRepoActionBus, RepoActionBus>();
            sc.AddSingleton<IRepoNetworkActionRunner, RepoNetworkActionRunner>();
            sc.AddSingleton<IAutoFetchHost, AutoFetchHost>();
            sc.AddSingleton<IAutoFetchService, AutoFetchService>();
            sc.AddSingleton<INavigationBus, NavigationBus>();
            sc.AddSingleton<IKeyboardCommandRouter, KeyboardCommandRouter>();
            sc.AddSingleton<INotificationService, NotificationService>();
            sc.AddSingleton<IRepoLayoutService, RepoLayoutService>();
            sc.AddSingleton<ISystemThemeProbe, FakeThemeProbe>();
            sc.AddSingleton<IThemeService, ThemeService>();
            sc.AddSingleton<ICustomThemeService, CustomThemeService>();
            sc.AddSingleton<IWorkspaceService, WorkspaceService>();
            sc.AddSingleton<IProfileService, ProfileService>();
            sc.AddSingleton<IGitCliService, GitCliService>();
            var locator = new GitExecutableLocator();
            locator.ResolveAsync(null).GetAwaiter().GetResult();
            sc.AddSingleton<IGitExecutableLocator>(locator);
            sc.AddSingleton<IBackgroundWorkGate, BackgroundWorkGate>();
            sc.AddSingleton<IGraphLayoutService, GraphLayoutService>();
            sc.AddSingleton<IRepoStateCache, RepoStateCache>();
            sc.AddSingleton<IStashService, StashService>();
            sc.AddSingleton<IConflictService, ConflictService>();
            sc.AddSingleton<IPatchService, PatchService>();
            sc.AddSingleton<ICherryPickService, CherryPickService>();
            sc.AddSingleton<IRebaseService, RebaseService>();
            sc.AddSingleton<ISubmoduleService, SubmoduleService>();
            sc.AddSingleton<IWorktreeService, WorktreeService>();
            sc.AddSingleton<ISshKeyService, SshKeyService>();
            sc.AddSingleton<ICloneService, CloneService>();
            sc.AddSingleton<ITerminalBackend, PortaPtyBackend>();
            sc.AddSingleton<ITerminalSessionService, TerminalSessionService>();
            sc.AddSingleton<IRecentRepositoryService, RecentRepositoryService>();
            sc.AddSingleton<IRepoIdentityService, RepoIdentityService>();
            sc.AddSingleton<ICredentialVaultService, FakeVault>();
            sc.AddSingleton<IExternalLauncher, ExternalLauncher>();
            sc.AddSingleton<ICmdrQueueService>(Queue);
            sc.AddSingleton<IControlNavigator>(Navigator);
            Services = sc.BuildServiceProvider();

            View = new SidebarView(Services);
            Window = new Window { Width = 300, Height = 700, Content = View };
            Window.Show();
            Dispatcher.UIThread.RunJobs();
        }

        public T Find<T>(string name) where T : Control => View.FindControl<T>(name)
            ?? throw new InvalidOperationException($"No control named {name}.");

        public void Settle()
        {
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }

        public void Click(Control control)
        {
            var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), Window)
                ?? throw new InvalidOperationException("The control is not in the window.");
            Window.MouseDown(point, MouseButton.Left);
            Window.MouseUp(point, MouseButton.Left);
            Settle();
        }

        public List<Border> ItemBorders() =>
            View.Cmdr.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("cmdr-item")).ToList();

        public void Dispose()
        {
            try { Window.Close(); } catch { }
            try { View.Dispose(); } catch { }
            try { Services.Dispose(); } catch { }
        }
    }

    private static CmdrQueueItem Item(string? note = "Look at the parser") => new()
    {
        RepoRoot = @"C:\repos\app",
        RepoName = "app",
        Target = ControlVerb.Show,
        Rev = "90236c7a1b2c3d4e5f60718293a4b5c6d7e8f901",
        CommitSubject = "Parse the new header",
        Note = note,
    };

    [AvaloniaFact]
    public void A_fresh_sidebar_shows_branches_with_the_branches_half_primary()
    {
        using var h = new Harness();
        var branches = h.Find<Button>("BranchesToggle");
        var cmdr = h.Find<Button>("CmdrToggle");

        Assert.True(h.View.ViewModel.IsBranchesActive);
        Assert.Contains("primary", branches.Classes);
        Assert.DoesNotContain("flat", branches.Classes);
        Assert.Contains("flat", cmdr.Classes);
        Assert.DoesNotContain("primary", cmdr.Classes);
        Assert.True(h.Find<ContentControl>("BranchesHost").IsVisible);
        Assert.False(h.Find<ContentControl>("CmdrHost").IsVisible);
        Assert.Same(h.View.Branches, h.Find<ContentControl>("BranchesHost").Content);
    }

    [AvaloniaFact]
    public void The_toggle_row_is_as_tall_as_the_working_copy_button()
    {
        using var h = new Harness();
        h.Settle();

        Assert.Equal(SectionFrame.TopBarHeight, h.Find<Grid>("ToggleRow").Bounds.Height);
        Assert.Equal(SectionFrame.TopBarHeight, h.Find<Button>("BranchesToggle").Bounds.Height);
        Assert.Equal(h.Find<Button>("BranchesToggle").Bounds.Width, h.Find<Button>("CmdrToggle").Bounds.Width, 1);
        Assert.Equal(new CornerRadius(0), h.Find<Button>("CmdrToggle").CornerRadius);
    }

    [AvaloniaFact]
    public void Clicking_cmdr_swaps_the_primary_half_and_keeps_both_panes()
    {
        using var h = new Harness();
        var branchesPane = h.View.Branches;

        h.Click(h.Find<Button>("CmdrToggle"));

        Assert.True(h.View.ViewModel.IsCmdrActive);
        Assert.Contains("primary", h.Find<Button>("CmdrToggle").Classes);
        Assert.Contains("flat", h.Find<Button>("BranchesToggle").Classes);
        Assert.False(h.Find<ContentControl>("BranchesHost").IsVisible);
        Assert.True(h.Find<ContentControl>("CmdrHost").IsVisible);

        h.Click(h.Find<Button>("BranchesToggle"));

        Assert.True(h.View.ViewModel.IsBranchesActive);
        Assert.Same(branchesPane, h.Find<ContentControl>("BranchesHost").Content);
    }

    [AvaloniaFact]
    public void The_branches_icon_follows_the_button_foreground_only_while_active()
    {
        using var h = new Harness();
        var icon = h.Find<IconGlyph>("BranchesIcon");

        Assert.True(icon.FollowForeground);
        var path = Assert.IsType<Avalonia.Controls.Shapes.Path>(icon.Content);
        Assert.Same(h.Find<Button>("BranchesToggle").Foreground, path.Fill);

        h.View.ViewModel.ShowCmdrCommand.Execute(null);
        h.Settle();

        Assert.False(icon.FollowForeground);
        var muted = Assert.IsType<Avalonia.Controls.Shapes.Path>(icon.Content);
        Assert.Equal(
            HistoriaPalette.Get(EffectiveTheme.Dark, "text-muted").ToAvalonia(),
            Assert.IsAssignableFrom<ISolidColorBrush>(muted.Fill).Color);
    }

    [AvaloniaFact]
    public void The_count_circle_is_unseen_on_either_half_and_ringed_only_on_the_active_half()
    {
        using var h = new Harness();
        var circle = h.Find<Border>("CmdrCount");
        Assert.Equal("0", h.Find<TextBlock>("CmdrCountText").Text);
        Assert.DoesNotContain("unseen", circle.Classes);
        Assert.DoesNotContain("on-active", circle.Classes);

        h.Queue.Enqueue(Item());
        h.Settle();

        Assert.Equal("1", h.Find<TextBlock>("CmdrCountText").Text);
        Assert.Contains("unseen", circle.Classes);
        Assert.DoesNotContain("on-active", circle.Classes);

        h.View.ViewModel.ShowCmdrCommand.Execute(null);
        h.Settle();

        Assert.Contains("unseen", circle.Classes);
        Assert.Contains("on-active", circle.Classes);
        Assert.Equal(1.5, circle.BorderThickness.Left);
    }

    [AvaloniaFact]
    public void A_one_line_item_is_two_rows_and_a_long_note_is_three_with_an_ellipsis()
    {
        var shortItem = Item("Short note");
        var longItem = Item(string.Join(' ', Enumerable.Repeat("This note goes on well past the width of the sidebar column.", 5)));
        using var h = new Harness(shortItem, longItem);
        h.View.ViewModel.ShowCmdrCommand.Execute(null);
        h.Settle();

        var items = h.ItemBorders();
        Assert.Equal(2, items.Count);
        var padding = CmdrQueueView.ItemPadding.Top + CmdrQueueView.ItemPadding.Bottom;
        Assert.Equal(2 * CmdrQueueView.RowHeight + padding, items[0].Bounds.Height, 0.5);
        Assert.Equal(3 * CmdrQueueView.RowHeight + padding, items[1].Bounds.Height, 0.5);

        var text = items[1].GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("cmdr-text"));
        Assert.Equal(2, text.TextLayout.TextLines.Count);
        // The full note stays the text; the layout ends its second line with an ellipsis.
        Assert.Equal(longItem.Note, text.Text);
        Assert.Equal(2, text.TextLayout.TextLines.Count);
        Assert.EndsWith("…", Shown(text.TextLayout));
        Assert.DoesNotContain("…", Shown(items[0].GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("cmdr-text")).TextLayout));
    }

    private static string Shown(Avalonia.Media.TextFormatting.TextLayout layout) =>
        string.Concat(layout.TextLines.SelectMany(l => l.TextRuns).Select(r => r.Text.ToString()));

    [AvaloniaFact]
    public void Clicking_an_item_opens_it_through_the_navigator()
    {
        var item = Item();
        using var h = new Harness(item);
        h.View.ViewModel.ShowCmdrCommand.Execute(null);
        h.Settle();

        var row = h.ItemBorders().Single();
        var repoName = row.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "app");
        h.Click(repoName);

        var target = Assert.Single(h.Navigator.Targets);
        Assert.Equal(item.RepoRoot, target.RepoPath);
        Assert.Equal(ControlVerb.Show, target.Verb);
        Assert.True(Assert.Single(h.Queue.Items).Seen);
        Assert.True(h.View.ViewModel.IsCmdrActive);
        Assert.Equal("0", h.Find<TextBlock>("CmdrCountText").Text);

        // An opened item stays listed, dimmed, its note included.
        Assert.Contains("seen", row.Classes);
        Assert.All(row.GetVisualDescendants().OfType<TextBlock>(), t => Assert.True(t.Opacity < 1));
    }

    [AvaloniaFact]
    public void The_trash_button_removes_the_item_without_a_dialog_or_opening_it()
    {
        using var h = new Harness(Item(), Item("Keep this"));
        h.View.ViewModel.ShowCmdrCommand.Execute(null);
        h.Settle();

        var trash = h.ItemBorders()[0].GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("cmdr-remove"));
        h.Click(trash);

        Assert.Equal("Keep this", Assert.Single(h.Queue.Items).Note);
        Assert.Empty(h.Dialogs.Calls);
        Assert.Empty(h.Navigator.Targets);
    }
}
