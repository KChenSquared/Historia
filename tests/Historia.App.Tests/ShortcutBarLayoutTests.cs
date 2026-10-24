namespace HistoriaApp.Tests;

[Trait("tier", "ui")]

public class ShortcutBarLayoutTests
{
    private static ServiceProvider CreateServices()
    {
        var sc = new ServiceCollection();
        var settings = SettingsService.Load();
        sc.AddSingleton<ISettingsService>(settings);
        sc.AddSingleton<SettingsService>(settings);
        sc.AddSingleton<IUiDispatcher, Historia.Platform.UiDispatcher>();
        sc.AddSingleton<IDialogService, Historia.Platform.AvaloniaDialogService>();
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
        sc.AddSingleton<ICmdrPathService, FakeCmdrPathService>();
        sc.AddSingleton<ICmdrQueueService, SidebarViewTests.FakeQueue>();
        sc.AddSingleton<IControlNavigator, SidebarViewTests.FakeNavigator>();
        return sc.BuildServiceProvider();
    }

    // The side layout the bar kept: a Grid on the left, a StackPanel on the right.
    private static Control LayoutOf(ShortcutsBarView bar)
        => (Control)((Panel)((Border)bar.Content!).Child!).Children.Single();

    // Each button draws its icon through an IconGlyph, whose content is the glyph path.
    private static IEnumerable<Avalonia.Controls.Shapes.Path> GlyphPaths(StackPanel buttonContent)
        => buttonContent.Children.OfType<IconGlyph>().Select(g => g.Content).OfType<Avalonia.Controls.Shapes.Path>();

    [AvaloniaFact]
    public async Task Left_bar_has_branch_then_terminal()
    {
        await using var sp = CreateServices();
        var bar = new ShortcutsBarView(sp, ShortcutsBarSide.Left);
        var grid = (Grid)LayoutOf(bar);
        var leadGroup = (StackPanel)grid.Children[0];
        var trailGroup = (StackPanel)grid.Children[1];

        var buttons = leadGroup.Children.OfType<Button>().Concat(trailGroup.Children.OfType<Button>()).ToList();
        var tips = buttons.Select(b => ToolTip.GetTip(b)?.ToString()).ToList();

        Assert.Equal(
            ["Create a branch at the selected commit", "Open terminal in this repository"],
            tips);
    }

    [AvaloniaFact]
    public async Task Left_bar_utilities_are_icon_only()
    {
        await using var sp = CreateServices();
        var bar = new ShortcutsBarView(sp, ShortcutsBarSide.Left);
        var grid = (Grid)LayoutOf(bar);
        var leadGroup = (StackPanel)grid.Children[0];
        var trailGroup = (StackPanel)grid.Children[1];

        var branchBtn = leadGroup.Children.OfType<Button>().Single();
        var branchStack = (StackPanel)branchBtn.Content!;
        Assert.NotEmpty(GlyphPaths(branchStack));
        Assert.Equal("Branch", branchStack.Children.OfType<TextBlock>().Single().Text);

        var utilityBtns = trailGroup.Children.OfType<Button>().ToList();
        Assert.Single(utilityBtns);
        foreach (var btn in utilityBtns)
        {
            var stack = (StackPanel)btn.Content!;
            Assert.NotEmpty(GlyphPaths(stack));
            Assert.Empty(stack.Children.OfType<TextBlock>());
            Assert.False(string.IsNullOrEmpty(ToolTip.GetTip(btn)?.ToString()));
        }
    }

    [AvaloniaFact]
    public async Task Right_bar_has_fetch_pull_push_stash_in_order()
    {
        await using var sp = CreateServices();
        var bar = new ShortcutsBarView(sp, ShortcutsBarSide.Right);
        var panel = (StackPanel)LayoutOf(bar);
        var labels = panel.Children
            .OfType<Button>()
            .Select(b => ((StackPanel)b.Content!).Children.OfType<TextBlock>().First().Text)
            .ToList();

        Assert.Equal(["Fetch", "Pull", "Push", "Stash"], labels);
    }

    [AvaloniaFact]
    public async Task Left_bar_puts_branch_left_and_utilities_right()
    {
        await using var sp = CreateServices();
        var bar = new ShortcutsBarView(sp, ShortcutsBarSide.Left);
        var grid = (Grid)LayoutOf(bar);
        var leadGroup = (StackPanel)grid.Children[0];
        var trailGroup = (StackPanel)grid.Children[1];

        Assert.Equal(0, Grid.GetColumn(leadGroup));
        Assert.Equal(Avalonia.Layout.HorizontalAlignment.Left, leadGroup.HorizontalAlignment);
        Assert.Single(leadGroup.Children.OfType<Button>());

        Assert.Equal(2, Grid.GetColumn(trailGroup));
        Assert.Equal(Avalonia.Layout.HorizontalAlignment.Right, trailGroup.HorizontalAlignment);
        Assert.Single(trailGroup.Children.OfType<Button>());
    }

    [AvaloniaFact]
    public async Task Left_bar_natural_width_stays_under_base_left_min()
    {
        await using var sp = CreateServices();
        var bar = new ShortcutsBarView(sp, ShortcutsBarSide.Left);
        var window = new Window { Width = 800, Height = 600, Content = bar };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.True(bar.NaturalWidth < 250, $"Left natural width was {bar.NaturalWidth}, expected < 250");

        window.Close();
    }

    [AvaloniaFact]
    public async Task Right_bar_natural_width_is_unchanged()
    {
        await using var sp = CreateServices();
        var bar = new ShortcutsBarView(sp, ShortcutsBarSide.Right);
        var window = new Window { Width = 800, Height = 600, Content = bar };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.InRange(bar.NaturalWidth, 356.0, 360.0);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Shortcut_bar_buttons_match_appband_typography_and_icon_size()
    {
        await using var sp = CreateServices();
        var bar = new ShortcutsBarView(sp, ShortcutsBarSide.Left);
        var grid = (Grid)LayoutOf(bar);
        var leadGroup = (StackPanel)grid.Children[0];
        var btn = leadGroup.Children.OfType<Button>().First();
        var stack = (StackPanel)btn.Content!;
        var path = GlyphPaths(stack).First();
        var tb = stack.Children.OfType<TextBlock>().First();

        Assert.Equal(18.0, path.Width);
        Assert.Equal(18.0, path.Height);
        Assert.Equal(14.0, tb.FontSize);
    }

    [AvaloniaFact]
    public async Task Bars_match_the_graph_header_row()
    {
        Assert.Equal(CommitGraphControl.HeaderHeight, ShortcutsBarView.BarHeight);

        await using var sp = CreateServices();
        var window = new MainWindow(sp);
        window.Width = 1280;
        window.Height = 760;
        window.Measure(new Size(1280, 760));
        window.Arrange(new Rect(0, 0, 1280, 760));

        var leftHost = window.FindControl<ContentControl>("LeftShortcutsHost")!;
        var rightHost = window.FindControl<ContentControl>("RightShortcutsHost")!;

        Assert.Equal(0, leftHost.Bounds.Top);
        Assert.Equal(0, rightHost.Bounds.Top);
        Assert.Equal(CommitGraphControl.HeaderHeight, leftHost.Bounds.Height);
        Assert.Equal(CommitGraphControl.HeaderHeight, rightHost.Bounds.Height);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Bars_have_no_profile_or_ssh_controls()
    {
        await using var sp = CreateServices();
        var left = new ShortcutsBarView(sp, ShortcutsBarSide.Left);
        var right = new ShortcutsBarView(sp, ShortcutsBarSide.Right);

        static void AssertClean(Control control)
        {
            var toggleButtons = control.GetVisualDescendants().OfType<ToggleButton>().ToList();
            Assert.Empty(toggleButtons);

            var texts = control.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            Assert.DoesNotContain(texts, t => t != null && (t.Contains("SSH", StringComparison.OrdinalIgnoreCase) || t.Contains("Local config", StringComparison.OrdinalIgnoreCase)));
        }

        AssertClean(left);
        AssertClean(right);
    }

    [Fact]
    public void Effective_min_never_hides_bar_content()
    {
        Assert.True(BodyColumnLayout.EffectiveMin(250, 150) >= 150);
        Assert.True(BodyColumnLayout.EffectiveMin(250, 250) >= 250);
        Assert.True(BodyColumnLayout.EffectiveMin(250, 310.2) >= 310.2);
    }

    [Fact]
    public void Resolve_keeps_side_panes_at_min_when_window_is_narrow()
    {
        // total below the three intents (264 + 430 = 694) -> centre shrinks first, both sides stay >= their mins
        var (left1, centre1, right1) = BodyColumnLayout.Resolve(600, 264, 430, 250, 250);
        Assert.Equal(0, centre1);
        Assert.True(left1 >= 250);
        Assert.True(right1 >= 250);
        Assert.Equal(600, left1 + right1);

        // total below leftMin + rightMin (500) -> left kept, right takes the rest
        var (left2, centre2, right2) = BodyColumnLayout.Resolve(400, 264, 430, 250, 250);
        Assert.Equal(0, centre2);
        Assert.Equal(250, left2);
        Assert.Equal(150, right2);
    }

    [Fact]
    public void Resolve_matches_previous_behaviour_when_space_allows()
    {
        // At 1280 px window (content width 1280 - 2 * 4 = 1272), default intents 264 and 430
        var (left, centre, right) = BodyColumnLayout.Resolve(1272, 264, 430, 250, 250);
        Assert.Equal(264, left);
        Assert.Equal(430, right);
        Assert.Equal(1272 - 264 - 430, centre);
    }

    [AvaloniaFact]
    public async Task Bars_follow_their_columns()
    {
        await using var sp = CreateServices();
        var window = new MainWindow(sp);
        window.Width = 1600;
        window.Height = 800;
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var leftHost = window.FindControl<ContentControl>("LeftShortcutsHost")!;
        var rightHost = window.FindControl<ContentControl>("RightShortcutsHost")!;
        var bodyGrid = window.FindControl<Grid>("BodyGrid")!;

        var leftCol = bodyGrid.ColumnDefinitions[0];
        var rightCol = bodyGrid.ColumnDefinitions[4];

        Assert.Equal(leftCol.Width.Value, leftHost.Bounds.Width, 1);
        Assert.Equal(rightCol.Width.Value, rightHost.Bounds.Width, 1);

        // Move the right divider through ApplyBodyLayout intents
        window.SetIntents(window.LeftMin, 500);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(leftCol.Width.Value, leftHost.Bounds.Width, 1);
        Assert.Equal(rightCol.Width.Value, rightHost.Bounds.Width, 1);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Route_change_does_not_replace_bars()
    {
        await using var sp = CreateServices();
        var window = new MainWindow(sp);
        window.Width = 1280;
        window.Height = 760;
        window.Measure(new Size(1280, 760));
        window.Arrange(new Rect(0, 0, 1280, 760));

        var leftHost = window.FindControl<ContentControl>("LeftShortcutsHost")!;
        var rightHost = window.FindControl<ContentControl>("RightShortcutsHost")!;

        var leftBar = leftHost.Content;
        var rightBar = rightHost.Content;
        Assert.NotNull(leftBar);
        Assert.NotNull(rightBar);

        var nav = sp.GetRequiredService<INavigationBus>();
        nav.RequestNavigate("settings");
        Dispatcher.UIThread.RunJobs();

        Assert.Same(leftBar, leftHost.Content);
        Assert.Same(rightBar, rightHost.Content);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Natural_width_is_stable_and_does_not_loop_on_layout()
    {
        await using var sp = CreateServices();
        var window = new MainWindow(sp);
        window.Width = 1280;
        window.Height = 760;
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var leftHost = window.FindControl<ContentControl>("LeftShortcutsHost")!;
        var leftBar = (ShortcutsBarView)leftHost.Content!;
        var rightHost = window.FindControl<ContentControl>("RightShortcutsHost")!;
        var rightBar = (ShortcutsBarView)rightHost.Content!;

        var leftW = leftBar.NaturalWidth;
        var rightW = rightBar.NaturalWidth;

        Assert.True(leftW >= 150, $"Left natural width was {leftW}");
        Assert.True(rightW >= 150, $"Right natural width was {rightW}");

        // Drive multiple layout / render cycles to ensure no infinite layout loop
        for (var i = 0; i < 10; i++)
        {
            window.InvalidateMeasure();
            Dispatcher.UIThread.RunJobs();
        }

        Assert.Equal(leftW, leftBar.NaturalWidth, 1);
        Assert.Equal(rightW, rightBar.NaturalWidth, 1);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Right_pane_minimum_is_the_declared_floor()
    {
        await using var sp = CreateServices();
        var window = new MainWindow(sp);
        window.Width = 1280;
        window.Height = 760;
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(360.0, window.RightMin);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Window_min_width_matches_the_two_pane_floors()
    {
        await using var sp = CreateServices();
        var window = new MainWindow(sp);
        window.Width = 1280;
        window.Height = 760;
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(window.LeftMin + window.RightMin + 8, window.MinWidth);
        Assert.Equal(618.0, window.MinWidth);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Status_bar_text_follows_the_window_view_model()
    {
        await using var sp = CreateServices();
        var window = new MainWindow(sp);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var status = window.FindControl<TextBlock>("StatusText")!;
        Assert.Same(window.ViewModel, window.DataContext);
        Assert.Equal("Ready", status.Text);

        window.ViewModel.StatusText = "Fetching";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Fetching", status.Text);

        window.Close();
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

    private sealed class FakeCmdrPathService : ICmdrPathService
    {
        public CmdrPathStatus GetStatus() => new CmdrPathStatus.Unavailable();
        public CmdrPathApplyResult Apply(PathChange confirmed) => new CmdrPathApplyResult.Failed("Unavailable in test.");
    }
}
