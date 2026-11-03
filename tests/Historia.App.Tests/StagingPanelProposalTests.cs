namespace HistoriaApp.Tests;

[Trait("tier", "ui")]
public class StagingPanelProposalTests : IDisposable
{
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

    private const string Proposal = "feat: from an agent\n\nBody.";

    private readonly string _repoDir;
    private readonly ServiceProvider _sp;
    private readonly RepositoryContext _context;
    private readonly AiTestSupport.Dialogs _dialogs = new();
    private readonly AiTestSupport.Notifications _notifications = new();
    private readonly InMemorySettingsService _settings = new();
    private readonly List<StagingPanelView> _views = [];
    private readonly List<Window> _windows = [];

    public StagingPanelProposalTests()
    {
        _repoDir = TestScratch.NewDirectory("historia-staging-proposal-");
        LibGit2Sharp.Repository.Init(_repoDir);
        File.WriteAllText(System.IO.Path.Combine(_repoDir, "a.txt"), "one\n");
        using (var repo = new LibGit2Sharp.Repository(_repoDir))
        {
            repo.Config.Set("user.name", "Test");
            repo.Config.Set("user.email", "test@example.com");
            repo.Config.Set("commit.gpgsign", false);
            LibGit2Sharp.Commands.Stage(repo, "a.txt");
            var sig = new LibGit2Sharp.Signature("Test", "test@example.com", DateTimeOffset.Now);
            repo.Commit("initial", sig, sig, new LibGit2Sharp.CommitOptions());
        }

        var sc = new ServiceCollection();
        sc.AddSingleton<ISettingsService>(_settings);
        sc.AddSingleton<IUiDispatcher, Historia.Platform.UiDispatcher>();
        sc.AddSingleton<IDialogService>(_dialogs);
        sc.AddSingleton<IScrollDirectionProbe, Historia.Platform.ScrollDirectionProbe>();
        sc.AddSingleton<IRepoActionBus, RepoActionBus>();
        sc.AddSingleton<IRepoNetworkActionRunner, RepoNetworkActionRunner>();
        sc.AddSingleton<IAutoFetchHost, AutoFetchHost>();
        sc.AddSingleton<IAutoFetchService, AutoFetchService>();
        sc.AddSingleton<INavigationBus, NavigationBus>();
        sc.AddSingleton<IKeyboardCommandRouter, KeyboardCommandRouter>();
        sc.AddSingleton<INotificationService>(_notifications);
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
        sc.AddSingleton<Historia.Theming.AvaloniaThemeApplier>();
        _sp = sc.BuildServiceProvider();
        _sp.GetRequiredService<Historia.Theming.AvaloniaThemeApplier>();

        var ws = _sp.GetRequiredService<IWorkspaceService>();
        _context = ws.OpenAsync(_repoDir).GetAwaiter().GetResult();
        ws.Focus(_context);
    }

    public void Dispose()
    {
        foreach (var window in _windows)
            try { window.Close(); } catch { }
        foreach (var view in _views)
            try { view.Dispose(); } catch { }
        try { _context.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        try { _sp.Dispose(); } catch { }
        TestScratch.Delete(_repoDir);
    }

    private StagingPanelView NewPanel()
    {
        var panel = new StagingPanelView(_sp, _context);
        _views.Add(panel);
        var window = new Window { Width = 800, Height = 900, Content = panel };
        _windows.Add(window);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return panel;
    }

    private void Propose(string message = Proposal)
    {
        _context.State.ProposeCommitMessage(message);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Click(Button button)
    {
        button.Command!.Execute(button.CommandParameter);
        Dispatcher.UIThread.RunJobs();
    }

    private async Task StageChangeAsync()
    {
        File.WriteAllText(System.IO.Path.Combine(_repoDir, "a.txt"), "one\ntwo\n");
        using (var repo = new LibGit2Sharp.Repository(_repoDir))
            LibGit2Sharp.Commands.Stage(repo, "a.txt");
        await _context.State.RefreshStatusAsync();
        for (var i = 0; i < 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(30);
        }
    }

    [AvaloniaFact]
    public void An_empty_box_takes_the_proposal_without_asking()
    {
        var panel = NewPanel();

        Propose();

        Assert.Equal(Proposal, panel.MessageInput.Text);
        Assert.Null(_context.State.PendingCommitMessage);
        Assert.False(panel.OfferBar.IsVisible);
        Assert.True(panel.AgentNote.IsVisible);
        Assert.Empty(_dialogs.ConfirmDestructiveCalls);
        Assert.Empty(_dialogs.ConfirmCalls);
    }

    [AvaloniaFact]
    public void The_same_text_already_in_the_box_is_taken_without_asking()
    {
        var panel = NewPanel();
        panel.MessageInput.Text = "  " + Proposal + "\n";

        Propose();

        Assert.Null(_context.State.PendingCommitMessage);
        Assert.False(panel.OfferBar.IsVisible);
        Assert.Empty(_dialogs.ConfirmDestructiveCalls);
    }

    [AvaloniaFact]
    public void A_different_draft_is_kept_and_the_proposal_is_offered()
    {
        var panel = NewPanel();
        panel.MessageInput.Text = "my own draft";

        Propose();

        Assert.Equal("my own draft", panel.MessageInput.Text);
        Assert.True(panel.OfferBar.IsVisible);
        Assert.Equal(Proposal, _context.State.PendingCommitMessage);
        Assert.False(panel.AgentNote.IsVisible);
        Assert.Empty(_dialogs.ConfirmDestructiveCalls);
        Assert.Equal(Proposal, ToolTip.GetTip(panel.OfferBar));
    }

    [AvaloniaFact]
    public void The_offer_tooltip_shows_only_the_first_300_characters()
    {
        var panel = NewPanel();
        panel.MessageInput.Text = "draft";

        Propose(new string('x', 500));

        var tip = Assert.IsType<string>(ToolTip.GetTip(panel.OfferBar));
        Assert.True(tip.Length <= 301);
    }

    [AvaloniaFact]
    public async Task Use_it_replaces_the_draft_after_a_destructive_confirm()
    {
        var panel = NewPanel();
        panel.MessageInput.Text = "my own draft";
        Propose();

        await panel.ViewModel.UseOfferCommand.ExecuteAsync(null);

        Assert.Equal(Proposal, panel.MessageInput.Text);
        Assert.Null(_context.State.PendingCommitMessage);
        Assert.False(panel.OfferBar.IsVisible);
        var confirm = Assert.Single(_dialogs.ConfirmDestructiveCalls);
        Assert.Equal("Replace commit message", confirm.Title);
        Assert.Equal("Replace your commit message draft (12 characters) with the coding agent's message? Your draft is discarded.", confirm.Effect);
        Assert.Equal("Replace", confirm.ConfirmLabel);
        Assert.Empty(_dialogs.ConfirmCalls);
    }

    [AvaloniaFact]
    public async Task Cancelling_the_confirm_keeps_the_draft_and_the_bar()
    {
        _dialogs.ConfirmDestructiveResult = false;
        var panel = NewPanel();
        panel.MessageInput.Text = "my own draft";
        Propose();

        await panel.ViewModel.UseOfferCommand.ExecuteAsync(null);

        Assert.Equal("my own draft", panel.MessageInput.Text);
        Assert.True(panel.OfferBar.IsVisible);
        Assert.Equal(Proposal, _context.State.PendingCommitMessage);
    }

    [AvaloniaFact]
    public void Dismiss_hides_the_bar_and_forgets_the_proposal_without_touching_the_draft()
    {
        var panel = NewPanel();
        panel.MessageInput.Text = "my own draft";
        Propose();

        Click(panel.OfferDismiss);

        Assert.False(panel.OfferBar.IsVisible);
        Assert.Null(_context.State.PendingCommitMessage);
        Assert.Equal("my own draft", panel.MessageInput.Text);
        Assert.Empty(_dialogs.ConfirmDestructiveCalls);
    }

    [AvaloniaFact]
    public void A_newer_proposal_replaces_the_offer()
    {
        var panel = NewPanel();
        panel.MessageInput.Text = "my own draft";
        Propose("first");

        Propose("second");

        Assert.True(panel.OfferBar.IsVisible);
        Assert.Equal("second", _context.State.PendingCommitMessage);
    }

    [AvaloniaFact]
    public void A_proposal_made_before_the_panel_exists_is_applied_on_construction()
    {
        Propose();
        Assert.Equal(Proposal, _context.State.PendingCommitMessage);

        var panel = NewPanel();

        Assert.Equal(Proposal, panel.MessageInput.Text);
        Assert.Null(_context.State.PendingCommitMessage);
    }

    [AvaloniaFact]
    public void A_proposal_never_touches_amend_or_moves_focus()
    {
        var panel = NewPanel();
        panel.AmendBox.IsChecked = true;
        panel.MessageInput.Text = "my own draft";

        Propose();

        Assert.True(panel.AmendBox.IsChecked);
        Assert.False(panel.MessageInput.IsFocused);

        panel.MessageInput.Text = "";
        Propose("another");

        Assert.True(panel.AmendBox.IsChecked);
        Assert.False(panel.MessageInput.IsFocused);
    }

    [AvaloniaFact]
    public void Editing_the_text_hides_the_agent_note()
    {
        var panel = NewPanel();
        Propose();
        Assert.True(panel.AgentNote.IsVisible);

        panel.MessageInput.Text += " edited";
        Dispatcher.UIThread.RunJobs();

        Assert.False(panel.AgentNote.IsVisible);
    }

    [AvaloniaFact]
    public async Task Committing_clears_the_pending_proposal_and_the_bar()
    {
        var panel = NewPanel();
        await StageChangeAsync();
        panel.MessageInput.Text = "feat: mine";
        Propose();
        Assert.True(panel.OfferBar.IsVisible);

        await panel.ViewModel.CommitCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(_notifications.Published.Count == 1 && _notifications.Published[0].Severity == NotificationSeverity.Success,
            string.Join(" | ", _notifications.Published.Select(n => n.Message)));
        Assert.Null(_context.State.PendingCommitMessage);
        Assert.False(panel.OfferBar.IsVisible);
        Assert.False(panel.AgentNote.IsVisible);
        Assert.Equal("", panel.MessageInput.Text);
    }

    [AvaloniaFact]
    public void A_disposed_panel_ignores_further_proposals()
    {
        var panel = NewPanel();
        panel.Dispose();

        Propose();

        Assert.Equal(Proposal, _context.State.PendingCommitMessage);
        Assert.Equal("", panel.MessageInput.Text);
    }

    [AvaloniaFact]
    public void Without_ai_services_the_amend_row_holds_only_the_checkbox()
    {
        var panel = NewPanel();

        Assert.False(panel.Wand.IsEffectivelyVisible);
        Assert.False(panel.AiSettings.IsEffectivelyVisible);
        Assert.False(panel.ModelPicker.IsEffectivelyVisible);
        var row = panel.CommitOptionsRow;
        var child = Assert.Single(row.Children, c => c.IsVisible);
        Assert.Same(panel.AmendBox, child);
        Assert.Equal(HorizontalAlignment.Right, panel.AmendBox.HorizontalAlignment);
    }
}
