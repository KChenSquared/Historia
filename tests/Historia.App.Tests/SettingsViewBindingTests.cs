namespace HistoriaApp.Tests;

/// <summary>
/// A <see cref="SettingsView"/> over in-memory settings and a scripted dialog service, hosted in a
/// window with every section expanded.
/// </summary>
internal sealed class SettingsViewRig : IDisposable
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

    private sealed class FakeCmdrPathService : ICmdrPathService
    {
        public CmdrPathStatus GetStatus() => new CmdrPathStatus.Unavailable();
        public CmdrPathApplyResult Apply(PathChange confirmed) => new CmdrPathApplyResult.Failed("Unavailable");
    }

    public SettingsViewRig(AppSettings settings, ScriptedDialogs? dialog = null, IGitExecutableLocator? locator = null)
    {
        Settings = new InMemorySettingsService(settings);
        Dialog = dialog ?? new ScriptedDialogs();
        var sc = new ServiceCollection();
        sc.AddSingleton<ISettingsService>(Settings);
        sc.AddSingleton<IUiDispatcher, Historia.Platform.UiDispatcher>();
        sc.AddSingleton<IDialogService>(Dialog);
        sc.AddSingleton<IScrollDirectionProbe, Historia.Platform.ScrollDirectionProbe>();
        sc.AddSingleton<IRepoActionBus, RepoActionBus>();
        sc.AddSingleton<IRepoNetworkActionRunner, RepoNetworkActionRunner>();
        sc.AddSingleton<IAutoFetchHost, AutoFetchHost>();
        sc.AddSingleton<IAutoFetchService, AutoFetchService>();
        sc.AddSingleton<Historia.Services.UI.INavigationBus, Historia.Services.UI.NavigationBus>();
        sc.AddSingleton<IKeyboardCommandRouter, KeyboardCommandRouter>();
        sc.AddSingleton<INotificationService, NotificationService>();
        sc.AddSingleton<IRepoLayoutService, RepoLayoutService>();
        sc.AddSingleton<ISystemThemeProbe, FakeThemeProbe>();
        sc.AddSingleton<IThemeService, ThemeService>();
        sc.AddSingleton<ICustomThemeService, CustomThemeService>();
        sc.AddSingleton<IWorkspaceService, WorkspaceService>();
        sc.AddSingleton<IProfileService, ProfileService>();
        sc.AddSingleton<IGitCliService, GitCliService>();
        if (locator is null)
        {
            var defaultLocator = new GitExecutableLocator();
            defaultLocator.ResolveAsync(null).GetAwaiter().GetResult();
            locator = defaultLocator;
        }
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
        Provider = sc.BuildServiceProvider();

        View = new SettingsView(Provider);
        var vm = View.ViewModel;
        foreach (var section in new[] { vm.General, vm.Network, vm.Diff, vm.Git, vm.Terminal, vm.Advanced })
            section.Toggle();
        Window = new Window { Width = 900, Height = 3000, Content = View };
        Window.Show();
        Dispatcher.UIThread.RunJobs();
    }

    public InMemorySettingsService Settings { get; }
    public ScriptedDialogs Dialog { get; }
    public ServiceProvider Provider { get; }
    public SettingsView View { get; }
    public Window Window { get; }

    /// <summary>The numeric field bound to <paramref name="setting"/>.</summary>
    public NumericUpDown Field(NumericSettingViewModel setting)
        => View.GetVisualDescendants().OfType<NumericUpDown>().Single(n => ReferenceEquals(n.DataContext, setting));

    public CheckBox Check(string label)
        => View.GetVisualDescendants().OfType<CheckBox>().Single(c => (c.Content as string) == label);

    public TextBox Box(string watermark)
        => View.GetVisualDescendants().OfType<TextBox>().Single(t => t.Watermark == watermark);

    public Button PushButton(string content)
        => View.GetVisualDescendants().OfType<Button>().First(b => (b.Content as string) == content);

    /// <summary>Presses a button the way the control does: its command runs only while it can.</summary>
    public static void Click(Button button)
    {
        var command = button.Command ?? throw new InvalidOperationException("The button has no command.");
        if (button.IsEffectivelyEnabled && command.CanExecute(button.CommandParameter))
            command.Execute(button.CommandParameter);
        Dispatcher.UIThread.RunJobs();
    }

    public void Dispose()
    {
        Window.Close();
        View.Dispose();
    }
}

/// <summary>A dialog service that never opens a window: destructive confirmations are answered
/// from <see cref="ResultToReturn"/> or the queued <see cref="Results"/>, and a file pick from
/// <see cref="PickedFile"/>.</summary>
internal sealed class ScriptedDialogs : IDialogService
{
    public bool ResultToReturn { get; set; } = true;

    /// <summary>Per-call destructive-confirm answers, in order; falls back to <see cref="ResultToReturn"/>.</summary>
    public Queue<bool>? Results { get; set; }

    public string? PickedFile { get; set; }

    public List<(string Title, string Effect, string ConfirmLabel)> DestructiveCalls { get; } = [];

    public Task<bool> ConfirmDestructiveAsync(string title, string effect, string confirmLabel)
    {
        DestructiveCalls.Add((title, effect, confirmLabel));
        return Task.FromResult(Results is { Count: > 0 } ? Results.Dequeue() : ResultToReturn);
    }

    public Task<bool> ConfirmAsync(string title, string message, bool destructive = false, string confirmLabel = "OK", bool defaultToCancel = false, DialogIcon icon = DialogIcon.None) =>
        Task.FromResult(ResultToReturn);

    public Task<int> ChooseAsync(string title, string message, IReadOnlyList<string> options, int defaultIndex = 0, int destructiveIndex = -1, DialogIcon icon = DialogIcon.None) =>
        Task.FromResult(defaultIndex);

    public Task<string?> PickFileAsync(string title, string filter, string? initialDirectory = null) => Task.FromResult(PickedFile);
    public Task<string?> PickFolderAsync(string title, string? initialDirectory = null) => Task.FromResult<string?>(null);
    public Task<string?> PromptAsync(string title, string message, string? defaultValue = null, string confirmLabel = "OK", bool primaryConfirm = false) => Task.FromResult<string?>(null);
    public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
}

/// <summary>
/// The settings page's bindings in the real view: rows show the saved settings, write them back,
/// and follow later changes without a rebuild; text boxes commit on losing focus.
/// </summary>
[Trait("tier", "ui")]
public sealed class SettingsViewBindingTests
{
    [AvaloniaFact]
    public void A_check_box_shows_the_setting_writes_it_and_follows_later_changes()
    {
        using var rig = new SettingsViewRig(new AppSettings { DiffWordWrap = false });
        var box = rig.Check("Wrap long lines");
        Assert.False(box.IsChecked);

        box.IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(rig.Settings.Current.DiffWordWrap);

        rig.Settings.UpdateAsync(s => s with { DiffWordWrap = false }).GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();
        Assert.False(box.IsChecked);
    }

    [AvaloniaFact]
    public void A_combo_box_shows_the_setting_writes_it_and_follows_later_changes()
    {
        using var rig = new SettingsViewRig(new AppSettings { WheelScrollDirection = WheelScrollDirection.Standard });
        var combo = rig.View.GetVisualDescendants().OfType<ComboBox>()
            .Single(c => c.Items.OfType<ComboBoxItem>().Any(i => (i.Content as string) == "Inverted"));
        Assert.Equal(1, combo.SelectedIndex);

        combo.SelectedIndex = 2;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(WheelScrollDirection.Inverted, rig.Settings.Current.WheelScrollDirection);

        rig.Settings.UpdateAsync(s => s with { WheelScrollDirection = WheelScrollDirection.Auto }).GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, combo.SelectedIndex);
    }

    [AvaloniaFact]
    public void The_diff_layout_and_cursor_style_combos_map_to_their_settings()
    {
        using var rig = new SettingsViewRig(new AppSettings { DefaultDiffView = DiffViewMode.SideBySide, TerminalCursorStyle = TerminalCursorStyle.Underline });
        var layout = rig.View.GetVisualDescendants().OfType<ComboBox>()
            .Single(c => c.Items.OfType<ComboBoxItem>().Any(i => (i.Content as string) == "Side by side"));
        var cursor = rig.View.GetVisualDescendants().OfType<ComboBox>()
            .Single(c => c.Items.OfType<ComboBoxItem>().Any(i => (i.Content as string) == "Underline"));
        Assert.Equal(1, layout.SelectedIndex);
        Assert.Equal(1, cursor.SelectedIndex);

        layout.SelectedIndex = 0;
        cursor.SelectedIndex = 2;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(DiffViewMode.Unified, rig.Settings.Current.DefaultDiffView);
        Assert.Equal(TerminalCursorStyle.Bar, rig.Settings.Current.TerminalCursorStyle);
    }

    [AvaloniaFact]
    public void A_text_box_commits_when_it_loses_focus_and_shows_the_trimmed_value()
    {
        using var rig = new SettingsViewRig(new AppSettings());
        var box = rig.Box("Platform default (pwsh / zsh / bash)");

        box.Text = "  /bin/zsh  ";
        Assert.Null(rig.Settings.Current.TerminalShellPath);

        box.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("/bin/zsh", rig.Settings.Current.TerminalShellPath);
        Assert.Equal("/bin/zsh", box.Text);
    }

    [AvaloniaFact]
    public void Browse_puts_the_picked_shell_in_the_box()
    {
        using var rig = new SettingsViewRig(new AppSettings(), new ScriptedDialogs { PickedFile = "/opt/bin/fish" });
        var box = rig.Box("Platform default (pwsh / zsh / bash)");

        SettingsViewRig.Click(rig.PushButton("Browse…"));

        Assert.Equal("/opt/bin/fish", box.Text);
        Assert.Equal("/opt/bin/fish", rig.Settings.Current.TerminalShellPath);
    }

    [AvaloniaFact]
    public void Autolink_rules_can_be_added_edited_and_removed()
    {
        using var rig = new SettingsViewRig(new AppSettings());
        Assert.DoesNotContain(rig.View.GetVisualDescendants().OfType<TextBox>(), t => t.Watermark == "Pattern (regex)");

        SettingsViewRig.Click(rig.PushButton("Add rule"));
        var pattern = rig.Box("Pattern (regex)");
        var url = rig.Box("Link URL ($1 = first group)");
        Assert.Single(rig.Settings.Current.IssueAutolinks);

        pattern.Text = "#(\\d+)";
        pattern.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
        url.Text = "https://tracker.example/$1";
        url.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("#(\\d+)", rig.Settings.Current.IssueAutolinks[0].Pattern);
        Assert.Equal("https://tracker.example/$1", rig.Settings.Current.IssueAutolinks[0].UrlTemplate);
        // The row the user is typing in survives the save it caused.
        Assert.Same(pattern, rig.Box("Pattern (regex)"));

        SettingsViewRig.Click(rig.PushButton("Remove"));
        Assert.Empty(rig.Settings.Current.IssueAutolinks);
        Assert.DoesNotContain(rig.View.GetVisualDescendants().OfType<TextBox>(), t => t.Watermark == "Pattern (regex)");
    }

    [AvaloniaFact]
    public void The_current_theme_mode_button_is_primary_and_choosing_another_moves_it()
    {
        using var rig = new SettingsViewRig(new AppSettings { AppTheme = ThemeMode.Dark });
        var light = rig.PushButton("Light");
        var dark = rig.PushButton("Dark");
        var system = rig.PushButton("System");
        Assert.Contains("primary", dark.Classes);
        Assert.Contains("secondary", light.Classes);
        Assert.Contains("secondary", system.Classes);
        Assert.DoesNotContain("primary", light.Classes);

        SettingsViewRig.Click(light);

        Assert.Equal(ThemeMode.Light, rig.Settings.Current.AppTheme);
        Assert.Contains("primary", light.Classes);
        Assert.DoesNotContain("secondary", light.Classes);
        Assert.Contains("secondary", dark.Classes);
        Assert.DoesNotContain("primary", dark.Classes);
    }

    [AvaloniaFact]
    public void The_close_button_returns_home_when_no_repository_is_focused()
    {
        using var rig = new SettingsViewRig(new AppSettings());
        var routes = new List<string>();
        rig.Provider.GetRequiredService<Historia.Services.UI.INavigationBus>().NavigationRequested += routes.Add;
        var close = rig.View.GetVisualDescendants().OfType<Button>().Single(b => b.Content is IconGlyph);

        SettingsViewRig.Click(close);

        Assert.Equal([""], routes);
    }

    [AvaloniaFact]
    public void A_disposed_view_stops_following_the_settings()
    {
        var rig = new SettingsViewRig(new AppSettings { DiffWordWrap = false });
        var box = rig.Check("Wrap long lines");

        rig.View.Dispose();
        rig.Settings.UpdateAsync(s => s with { DiffWordWrap = true }).GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();

        Assert.False(box.IsChecked);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void The_row_cap_fields_are_disabled_in_lightweight_mode_and_enabled_again_when_it_ends()
    {
        using var rig = new SettingsViewRig(new AppSettings { LightweightMode = true });
        var preload = rig.Field(rig.View.ViewModel.SilentCrawlRowCap);
        var resident = rig.Field(rig.View.ViewModel.ResidentRowCap);
        Assert.False(preload.IsEffectivelyEnabled);
        Assert.False(resident.IsEffectivelyEnabled);

        rig.Settings.UpdateAsync(s => s with { LightweightMode = false }).GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();

        Assert.True(preload.IsEffectivelyEnabled);
        Assert.True(resident.IsEffectivelyEnabled);
    }
}
