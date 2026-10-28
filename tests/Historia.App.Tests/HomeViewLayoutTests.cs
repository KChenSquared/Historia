namespace HistoriaApp.Tests;

public sealed class HomeViewLayoutTests
{
    // The root keeps a 32 px margin on each side, so a window this much wider than a content
    // width gives the action row exactly that width.
    private const double RootMargins = 64;

    private static async Task<(HomeView View, ServiceProvider Services)> CreateAsync(int recents = 0)
    {
        var services = CloneSubmoduleOfferTests.CreateServices();
        var recentRepositories = services.GetRequiredService<IRecentRepositoryService>();
        await recentRepositories.ClearAsync();
        for (var i = 0; i < recents; i++)
            await recentRepositories.AddAsync(TestScratch.NewPath($"home-recent-{i}-"), $"Recent repo {i}");
        return (new HomeView(services), services);
    }

    private static (Button Open, Button Clone, Button New) ActionButtons(HomeView view)
    {
        var buttons = view.ActionRow.Children.OfType<Button>().ToList();
        Assert.Equal(["Open repository…", "Clone…", "New repository…"], buttons.Select(b => b.Content));
        return (buttons[0], buttons[1], buttons[2]);
    }

    [AvaloniaFact]
    public async Task ActionRow_WrapsNewRepository_WhenNarrow()
    {
        var (view, services) = await CreateAsync();
        var window = new Window { Width = 260 + RootMargins, Height = 600, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var (openBtn, _, newBtn) = ActionButtons(view);
            var panel = view.ActionRow;

            Assert.True(newBtn.Bounds.Y > openBtn.Bounds.Y,
                $"Expected New repository ({newBtn.Bounds}) to wrap below Open repository ({openBtn.Bounds})");
            Assert.True(newBtn.Bounds.Right <= panel.Bounds.Width + 0.5,
                $"Expected New repository right edge ({newBtn.Bounds.Right}) <= panel width ({panel.Bounds.Width})");
        }
        finally
        {
            window.Close();
            view.Dispose();
            await services.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task ActionRow_AllInline_WhenWide()
    {
        var (view, services) = await CreateAsync();
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var (openBtn, cloneBtn, newBtn) = ActionButtons(view);

            // Primary and secondary buttons differ in height, so a shared line means a shared centre.
            Assert.Equal(openBtn.Bounds.Center.Y, cloneBtn.Bounds.Center.Y);
            Assert.Equal(openBtn.Bounds.Center.Y, newBtn.Bounds.Center.Y);
        }
        finally
        {
            window.Close();
            view.Dispose();
            await services.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task ActionRow_ReturnsInline_AfterWidening()
    {
        var (view, services) = await CreateAsync();
        var window = new Window { Width = 260 + RootMargins, Height = 600, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var (openBtn, cloneBtn, newBtn) = ActionButtons(view);

            Assert.True(newBtn.Bounds.Y > openBtn.Bounds.Y);

            window.Width = 800;
            Dispatcher.UIThread.RunJobs();

            // Primary and secondary buttons differ in height, so a shared line means a shared centre.
            Assert.Equal(openBtn.Bounds.Center.Y, cloneBtn.Bounds.Center.Y);
            Assert.Equal(openBtn.Bounds.Center.Y, newBtn.Bounds.Center.Y);
        }
        finally
        {
            window.Close();
            view.Dispose();
            await services.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task RecentScroller_ScrollBarDoesNotOverlapRows()
    {
        var (view, services) = await CreateAsync(recents: 20);
        var scroller = view.RecentScroller;
        scroller.AllowAutoHide = false;

        var window = new Window { Width = 600, Height = 900, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var rows = view.RecentList.ItemsPanelRoot!;
            Assert.Equal(20, rows.Children.Count);

            var scrollBar = scroller.GetVisualDescendants()
                .OfType<ScrollBar>()
                .FirstOrDefault(s => s.Orientation == Avalonia.Layout.Orientation.Vertical);

            Assert.NotNull(scrollBar);
            Assert.True(scrollBar.IsVisible);

            var scrollBarTopLeft = scrollBar.TranslatePoint(new Point(0, 0), rows);
            var scrollBarBottomRight = scrollBar.TranslatePoint(new Point(scrollBar.Bounds.Width, scrollBar.Bounds.Height), rows);
            Assert.NotNull(scrollBarTopLeft);
            Assert.NotNull(scrollBarBottomRight);

            var scrollBarBoundsInContent = new Rect(scrollBarTopLeft.Value, scrollBarBottomRight.Value);

            foreach (var child in rows.Children)
            {
                Assert.False(
                    scrollBarBoundsInContent.Intersects(child.Bounds),
                    $"ScrollBar overlaps row at {child.Bounds}; scrollbar in content space is {scrollBarBoundsInContent}");
            }
        }
        finally
        {
            window.Close();
            view.Dispose();
            await services.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task RecentScroller_bar_is_lane_wide()
    {
        var (view, services) = await CreateAsync(recents: 20);
        var window = new Window { Width = 600, Height = 900, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var scrollBar = view.RecentScroller.GetVisualDescendants()
                .OfType<ScrollBar>()
                .Single(s => s.Orientation == Orientation.Vertical);

            Assert.True(scrollBar.IsVisible);
            Assert.Equal(ScrollBarMetrics.Lane, scrollBar.Bounds.Width);
        }
        finally
        {
            window.Close();
            view.Dispose();
            await services.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task Recents_render_one_row_each_and_the_empty_note_only_when_there_are_none()
    {
        var (empty, emptyServices) = await CreateAsync();
        var (filled, filledServices) = await CreateAsync(recents: 3);
        var window = new Window { Width = 600, Height = 900, Content = new StackPanel { Children = { empty, filled } } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            Assert.Empty(empty.RecentList.ItemsPanelRoot!.Children);
            Assert.Contains(empty.GetVisualDescendants().OfType<TextBlock>(),
                t => t.Text == "No repositories opened yet." && t.IsEffectivelyVisible);

            Assert.Equal(3, filled.RecentList.ItemsPanelRoot!.Children.Count);
            Assert.DoesNotContain(filled.GetVisualDescendants().OfType<TextBlock>(),
                t => t.Text == "No repositories opened yet." && t.IsEffectivelyVisible);
            Assert.Contains(filled.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Recent repo 0  (missing)");
        }
        finally
        {
            window.Close();
            empty.Dispose();
            filled.Dispose();
            await emptyServices.DisposeAsync();
            await filledServices.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task Clicking_a_recent_row_opens_that_repository_and_navigates_to_it()
    {
        var repoPath = CloneSubmoduleOfferTests.CreateTestRepo("home-click");
        var services = CloneSubmoduleOfferTests.CreateServices();
        var workspace = services.GetRequiredService<IWorkspaceService>();
        var recentRepositories = services.GetRequiredService<IRecentRepositoryService>();
        await recentRepositories.ClearAsync();
        await recentRepositories.AddAsync(repoPath, "clicked");
        var routes = new List<string>();
        services.GetRequiredService<INavigationBus>().NavigationRequested += routes.Add;

        var view = new HomeView(services);
        var window = new Window { Width = 600, Height = 600, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var row = view.RecentList.ItemsPanelRoot!.GetVisualDescendants().OfType<Border>()
                .Single(b => b.Classes.Contains("recent-row"));
            var inside = row.TranslatePoint(new Point(row.Bounds.Width / 3, row.Bounds.Height / 2), window);
            Assert.NotNull(inside);

            window.MouseDown(inside.Value, MouseButton.Left);
            window.MouseUp(inside.Value, MouseButton.Left);

            for (var i = 0; i < 200 && routes.Count == 0; i++)
            {
                await Task.Delay(50);
                Dispatcher.UIThread.RunJobs();
            }

            Assert.Equal(["repo"], routes);
            Assert.Equal(repoPath, Assert.Single(workspace.OpenRepositories).RepositoryPath);
        }
        finally
        {
            window.Close();
            view.Dispose();
            foreach (var ctx in workspace.OpenRepositories.ToList())
                await workspace.CloseAsync(ctx);
            await services.DisposeAsync();
            TestScratch.Delete(repoPath);
        }
    }

    [AvaloniaFact]
    public async Task LogoMark_IsTheSameBitmapAcrossHomeNavigations()
    {
        var (first, firstServices) = await CreateAsync();
        var (second, secondServices) = await CreateAsync();
        try
        {
            var firstSource = Assert.IsAssignableFrom<Avalonia.Media.Imaging.Bitmap>(first.MarkImage.Source);
            var secondSource = Assert.IsAssignableFrom<Avalonia.Media.Imaging.Bitmap>(second.MarkImage.Source);

            Assert.Same(firstSource, secondSource);
        }
        finally
        {
            first.Dispose();
            second.Dispose();
            await firstServices.DisposeAsync();
            await secondServices.DisposeAsync();
        }
    }
}
