namespace HistoriaApp.Tests;

/// <summary>Fakes shared by the AI view tests.</summary>
internal static class AiTestSupport
{
    internal sealed class Dialogs : IDialogService
    {
        public bool ConfirmResult { get; set; } = true;
        public bool ConfirmDestructiveResult { get; set; } = true;
        public int ChooseResult { get; set; } = 0;
        public List<(string Title, string Message, bool Destructive)> ConfirmCalls { get; } = [];
        public List<(string Title, string Effect, string ConfirmLabel)> ConfirmDestructiveCalls { get; } = [];
        public List<(string Title, string Message, IReadOnlyList<string> Options, int DefaultIndex)> ChooseCalls { get; } = [];

        public Task<string?> PickFolderAsync(string title, string? initialDirectory = null) => Task.FromResult<string?>(null);
        public Task<string?> PickFileAsync(string title, string filter, string? initialDirectory = null) => Task.FromResult<string?>(null);

        public Task<bool> ConfirmAsync(string title, string message, bool destructive = false, string confirmLabel = "OK",
            bool defaultToCancel = false, DialogIcon icon = DialogIcon.None)
        {
            ConfirmCalls.Add((title, message, destructive));
            return Task.FromResult(ConfirmResult);
        }

        public Task<bool> ConfirmDestructiveAsync(string title, string effect, string confirmLabel)
        {
            ConfirmDestructiveCalls.Add((title, effect, confirmLabel));
            return Task.FromResult(ConfirmDestructiveResult);
        }

        public Task<int> ChooseAsync(string title, string message, IReadOnlyList<string> options, int defaultIndex = 0,
            int destructiveIndex = -1, DialogIcon icon = DialogIcon.None)
        {
            ChooseCalls.Add((title, message, options, defaultIndex));
            return Task.FromResult(ChooseResult);
        }

        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;

        public Task<string?> PromptAsync(string title, string message, string? defaultValue = null,
            string confirmLabel = "OK", bool primaryConfirm = false) => Task.FromResult<string?>(null);
    }

    internal sealed class Notifications : INotificationService
    {
        public List<Notification> Published { get; } = [];
        public IReadOnlyList<Notification> Current => Published;
        public event EventHandler<NotificationsChangedEventArgs>? Changed;

        public Notification Publish(NotificationSeverity severity, string message, string? details = null,
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
            Published.Add(item);
            Changed?.Invoke(this, new NotificationsChangedEventArgs(Published));
            return item;
        }

        public void Dismiss(Guid id) => Published.RemoveAll(n => n.Id == id);
    }

    internal sealed class Client : IAiChatClient
    {
        public List<AiChatRequest> Lists { get; } = [];
        public List<AiChatRequest> Completes { get; } = [];
        public Func<IReadOnlyList<string>> ListResult { get; set; } = () => [];

        /// <summary>When set, list calls wait for it before answering.</summary>
        public TaskCompletionSource? ListGate { get; set; }

        public Task<IReadOnlyList<string>> ListModelsAsync(AiChatRequest request, CancellationToken ct)
        {
            Lists.Add(request);
            return ListGate is { } gate ? GatedListAsync(gate, ct) : Task.FromResult(ListResult());
        }

        private async Task<IReadOnlyList<string>> GatedListAsync(TaskCompletionSource gate, CancellationToken ct)
        {
            await gate.Task.WaitAsync(ct);
            return ListResult();
        }

        public Task<string> CompleteAsync(AiChatRequest request, CancellationToken ct)
        {
            Completes.Add(request);
            return Task.FromResult("feat: x");
        }
    }

    /// <summary>A real catalog that counts the handlers attached to <see cref="Changed"/>.</summary>
    internal sealed class CountingCatalog(IAiModelCatalog inner) : IAiModelCatalog
    {
        public int Subscribers { get; private set; }

        public event EventHandler<string>? Changed
        {
            add
            {
                inner.Changed += value;
                Subscribers++;
            }
            remove
            {
                inner.Changed -= value;
                Subscribers--;
            }
        }

        public IReadOnlyList<string>? Get(AiServiceProfile profile) => inner.Get(profile);
        public Task<IReadOnlyList<string>> LoadAsync(AiServiceProfile profile, CancellationToken ct = default) => inner.LoadAsync(profile, ct);
        public void Remember(AiServiceProfile profile, IReadOnlyList<string> ids) => inner.Remember(profile, ids);
        public bool TryClaimAutoSwitch(string profileId) => inner.TryClaimAutoSwitch(profileId);
    }

    /// <summary>A clock that moves only when told to.</summary>
    internal sealed class ManualTime : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }

    internal sealed class Factory(Client client) : IAiClientFactory
    {
        public int Calls { get; private set; }

        public IAiChatClient For(AiServiceKind kind)
        {
            Calls++;
            return client;
        }
    }

    /// <summary>Waits, pumping the UI thread, until <paramref name="condition"/> holds.</summary>
    internal static async Task PumpUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            Dispatcher.UIThread.RunJobs();
            if (condition() || Environment.TickCount64 > deadline)
                break;
            await Task.Delay(10);
        }
        Dispatcher.UIThread.RunJobs();
        Assert.True(condition(), "Condition was not met in time.");
    }
}

/// <summary>
/// The AI Integrations view hosted in a window: the markup renders the view model's rows and
/// editor, and the controls' own events (clicks, losing focus, Enter, the model list) reach it.
/// The logic behind them is covered by the view model's tests.
/// </summary>
[Trait("tier", "ui")]
public class AiIntegrationsViewTests
{
    private sealed class Rig
    {
        public InMemorySettingsService Settings { get; } = new();
        public AiProfileService Profiles { get; }
        public AiIntegrationsPanel Panel { get; } = new();
        public AiTestSupport.Dialogs Dialogs { get; } = new();
        public AiTestSupport.Notifications Notifications { get; } = new();
        public AiTestSupport.Client Client { get; } = new();
        public AiTestSupport.Factory Factory { get; }
        public AiTestSupport.CountingCatalog Catalog { get; }
        public AiTestSupport.ManualTime Time { get; } = new();
        public ServiceProvider Services { get; }

        public Rig()
        {
            Profiles = new AiProfileService(Settings);
            Factory = new AiTestSupport.Factory(Client);
            Catalog = new AiTestSupport.CountingCatalog(new AiModelCatalog(Profiles, Factory, Time));
            var sc = new ServiceCollection();
            sc.AddSingleton<IAiProfileService>(Profiles);
            sc.AddSingleton<IAiIntegrationsPanel>(Panel);
            sc.AddSingleton<IDialogService>(Dialogs);
            sc.AddSingleton<INotificationService>(Notifications);
            sc.AddSingleton<IAiClientFactory>(Factory);
            sc.AddSingleton<IAiModelCatalog>(Catalog);
            Services = sc.BuildServiceProvider();
        }

        public async Task<AiServiceProfile> Add(string name = "", Func<AiServiceProfile, AiServiceProfile>? configure = null)
        {
            var profile = await Profiles.AddAsync();
            await Profiles.UpdateAsync(profile.Id, p => (configure?.Invoke(p) ?? p) with { Name = name == "" ? p.Name : name });
            return Profiles.Profiles.First(p => p.Id == profile.Id);
        }

        /// <summary>Builds the view and shows it, so the editor's template is applied.</summary>
        public AiIntegrationsView View()
        {
            var view = new AiIntegrationsView(Services);
            new Window { Width = 900, Height = 700, Content = view }.Show();
            Dispatcher.UIThread.RunJobs();
            return view;
        }
    }

    private static T Find<T>(AiIntegrationsView view, string name) where T : Control
        => view.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    private static bool Has(AiIntegrationsView view, string name)
        => view.GetVisualDescendants().OfType<Control>().Any(c => c.Name == name);

    private static string[] RowLabels(AiIntegrationsView view)
        => [.. Find<ItemsControl>(view, "RowList").GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "")];

    private static IEnumerable<Border> RowBorders(AiIntegrationsView view)
        => Find<ItemsControl>(view, "RowList").GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("profile-row"));

    private static Button RowCheck(AiIntegrationsView view, int index)
        => RowBorders(view).ElementAt(index).GetVisualDescendants().OfType<Button>().Single();

    // A button runs its command from its own click handling, so the command is executed the same way.
    private static void Click(Button button)
    {
        if (button.IsEffectivelyEnabled && button.Command is { } command && command.CanExecute(button.CommandParameter))
            command.Execute(button.CommandParameter);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Commit(TextBox box, string text)
    {
        box.Text = text;
        box.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private static void PressRow(Border row)
    {
        row.RaiseEvent(new PointerPressedEventArgs(
            row, new Pointer(0, PointerType.Mouse, true), row,
            default, 0, new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None));
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task Profiles_are_listed_numbered_in_the_order_they_were_added()
    {
        var rig = new Rig();
        await rig.Add("Gemini");
        await rig.Add("Work");

        var view = rig.View();

        Assert.Equal(["1. Gemini", "2. Work"], RowLabels(view));
        Assert.Equal(2, RowBorders(view).Count());
        Assert.Contains("selected", RowBorders(view).First().Classes);
        Assert.DoesNotContain("selected", RowBorders(view).Last().Classes);
    }

    [AvaloniaFact]
    public void With_no_profile_the_view_asks_for_one_and_has_no_editor()
    {
        var rig = new Rig();

        var view = rig.View();

        Assert.False(Has(view, "NameBox"));
        Assert.False(Find<Button>(view, "DeleteButton").IsEffectivelyEnabled);
        Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Add an AI integration with + to get started." && t.IsVisible);
    }

    [AvaloniaFact]
    public async Task Adding_a_profile_appends_and_selects_it()
    {
        var rig = new Rig();
        await rig.Add("First");
        var view = rig.View();

        Click(Find<Button>(view, "AddButton"));
        await AiTestSupport.PumpUntil(() => rig.Profiles.Profiles.Count == 2 && RowLabels(view).Length == 2);

        Assert.Equal("2. Unnamed Service", RowLabels(view)[1]);
        Assert.Contains("selected", RowBorders(view).Last().Classes);
        Assert.Equal(rig.Profiles.Profiles[1].Id, view.ViewModel.SelectedId);
    }

    [AvaloniaFact]
    public async Task Pressing_a_row_selects_it_and_shows_its_editor()
    {
        var rig = new Rig();
        await rig.Add("A");
        var b = await rig.Add("B");
        var view = rig.View();
        Assert.Equal("A", Find<TextBox>(view, "NameBox").Text);

        PressRow(RowBorders(view).ElementAt(1));

        Assert.Equal(b.Id, view.ViewModel.SelectedId);
        Assert.Equal("B", Find<TextBox>(view, "NameBox").Text);
        Assert.Contains("selected", RowBorders(view).ElementAt(1).Classes);
    }

    [AvaloniaFact]
    public async Task The_key_box_has_a_red_frame_until_a_key_is_entered()
    {
        var rig = new Rig();
        await rig.Add("A");
        var view = rig.View();
        var frame = Find<Border>(view, "KeyFrame");
        Assert.Equal(new Thickness(2), frame.BorderThickness);

        Commit(Find<TextBox>(view, "KeyBox"), "sk-123");

        Assert.Equal(new Thickness(0), frame.BorderThickness);
        Assert.Equal("sk-123", rig.Profiles.Profiles.Single().ApiKey);
    }

    [AvaloniaFact]
    public async Task The_key_is_masked_and_env_mode_reveals_it_and_brings_the_red_frame_back()
    {
        var rig = new Rig();
        await rig.Add("A", p => p with { ApiKey = "sk-123" });
        var view = rig.View();
        var key = Find<TextBox>(view, "KeyBox");

        Assert.Equal("sk-123", key.Text);
        Assert.Equal('•', key.PasswordChar);
        Assert.False(key.RevealPassword);
        Assert.Equal(new Thickness(0), Find<Border>(view, "KeyFrame").BorderThickness);

        Find<CheckBox>(view, "EnvKeyBox").IsChecked = true;
        Dispatcher.UIThread.RunJobs();

        Assert.True(key.RevealPassword);
        Assert.Equal(new Thickness(2), Find<Border>(view, "KeyFrame").BorderThickness);
        Assert.True(rig.Profiles.Profiles.Single().ReadApiKeyFromEnv);
    }

    [AvaloniaFact]
    public async Task A_text_box_saves_when_it_loses_focus_and_not_while_typing()
    {
        var rig = new Rig();
        await rig.Add("A");
        var view = rig.View();
        var name = Find<TextBox>(view, "NameBox");

        name.Text = "Typing";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("A", rig.Profiles.Profiles.Single().Name);

        name.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Typing", rig.Profiles.Profiles.Single().Name);
        Assert.Equal(["1. Typing"], RowLabels(view));
    }

    [AvaloniaFact]
    public async Task Enter_saves_a_single_line_box_at_once()
    {
        var rig = new Rig();
        await rig.Add("A");
        var view = rig.View();
        var server = Find<TextBox>(view, "ServerBox");

        server.Text = "https://ai.example.test/v1";
        server.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = server });
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("https://ai.example.test/v1", rig.Profiles.Profiles.Single().Server);
    }

    [AvaloniaFact]
    public async Task Committing_a_field_does_not_rebuild_the_editor()
    {
        var rig = new Rig();
        await rig.Add("A");
        var view = rig.View();
        var server = Find<TextBox>(view, "ServerBox");

        Commit(Find<TextBox>(view, "NameBox"), "Renamed");

        Assert.Same(server, Find<TextBox>(view, "ServerBox"));
        Assert.Equal("1. Renamed", RowLabels(view)[0]);
        Assert.Equal("Renamed", rig.Profiles.Profiles.Single().Name);
    }

    [AvaloniaFact]
    public async Task Clicking_a_check_activates_with_no_prompt_and_clicking_the_green_check_deactivates()
    {
        var rig = new Rig();
        var a = await rig.Add("A");
        var view = rig.View();

        Click(RowCheck(view, 0));
        await AiTestSupport.PumpUntil(() => rig.Profiles.ActiveProfile?.Id == a.Id);
        Assert.Empty(rig.Dialogs.ChooseCalls);
        Assert.Equal("Active", ToolTip.GetTip(RowCheck(view, 0)));

        Click(RowCheck(view, 0));
        await AiTestSupport.PumpUntil(() => rig.Profiles.ActiveProfile is null);
        Assert.Equal("Make active", ToolTip.GetTip(RowCheck(view, 0)));
    }

    [AvaloniaFact]
    public async Task Switching_the_active_profile_asks_with_Save_as_the_default_option()
    {
        var rig = new Rig();
        var a = await rig.Add("Gemini");
        var b = await rig.Add("Work");
        await rig.Profiles.SetActiveAsync(a.Id);
        var view = rig.View();

        Click(RowCheck(view, 1));
        await AiTestSupport.PumpUntil(() => rig.Profiles.ActiveProfile?.Id == b.Id);

        var call = Assert.Single(rig.Dialogs.ChooseCalls);
        Assert.Equal("'1. Gemini' is the active AI integration. Switch to '2. Work'?", call.Message);
        Assert.Equal(["Save"], call.Options);
        Assert.Equal(0, call.DefaultIndex);
    }

    [AvaloniaFact]
    public async Task Delete_confirms_with_the_destructive_dialog_and_removes_the_profile()
    {
        var rig = new Rig();
        await rig.Add("Gemini");
        var b = await rig.Add("Work");
        await rig.Profiles.SetActiveAsync(b.Id);
        var view = rig.View();
        PressRow(RowBorders(view).ElementAt(1));
        Assert.Equal(b.Id, view.ViewModel.SelectedId);

        Click(Find<Button>(view, "DeleteButton"));
        await AiTestSupport.PumpUntil(() => rig.Profiles.Profiles.Count == 1);

        var call = Assert.Single(rig.Dialogs.ConfirmDestructiveCalls);
        Assert.Equal("Delete", call.ConfirmLabel);
        Assert.Contains("Delete '2. Work' and its saved API key?", call.Effect);
        Assert.Null(rig.Profiles.ActiveProfile);
        Assert.Equal(["1. Gemini"], RowLabels(view));
    }

    [AvaloniaFact]
    public async Task Cancelling_the_delete_removes_nothing()
    {
        var rig = new Rig();
        await rig.Add("Only");
        rig.Dialogs.ConfirmDestructiveResult = false;
        var view = rig.View();

        Click(Find<Button>(view, "DeleteButton"));
        await AiTestSupport.PumpUntil(() => rig.Dialogs.ConfirmDestructiveCalls.Count == 1);

        Assert.Single(rig.Profiles.Profiles);
        Assert.Single(RowBorders(view));
    }

    [AvaloniaFact]
    public async Task Choosing_Gemini_fills_the_server_box_and_leaves_the_key_alone()
    {
        var rig = new Rig();
        await rig.Add("", p => p with { ApiKey = "kept" });
        var view = rig.View();

        Find<ComboBox>(view, "TemplateBox").SelectedIndex = 1;
        await AiTestSupport.PumpUntil(() => rig.Profiles.Profiles.Single().Kind == AiServiceKind.Gemini);
        Dispatcher.UIThread.RunJobs();

        var saved = rig.Profiles.Profiles.Single();
        Assert.Equal(AiServiceTemplates.GeminiServer, saved.Server);
        Assert.Equal("kept", saved.ApiKey);
        Assert.Equal(AiServiceTemplates.GeminiServer, Find<TextBox>(view, "ServerBox").Text);
        Assert.Equal("Gemini", Find<TextBox>(view, "NameBox").Text);
        Assert.Empty(rig.Dialogs.ConfirmCalls);
    }

    [AvaloniaFact]
    public async Task Declining_the_Gemini_replacement_puts_the_template_box_back()
    {
        var rig = new Rig();
        await rig.Add("A", p => p with { Server = "https://example.test/v1", Model = "gpt-x" });
        rig.Dialogs.ConfirmResult = false;
        var view = rig.View();
        var template = Find<ComboBox>(view, "TemplateBox");

        template.SelectedIndex = 1;
        await AiTestSupport.PumpUntil(() => rig.Dialogs.ConfirmCalls.Count == 1);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(AiServiceKind.Custom, rig.Profiles.Profiles.Single().Kind);
        Assert.Equal(0, template.SelectedIndex);
    }

    [AvaloniaFact]
    public async Task The_effort_and_diff_limit_boxes_show_and_save_their_choice()
    {
        var rig = new Rig();
        await rig.Add("A", p => p with { ReasoningEffort = "low", DiffCapTokens = 25_000 });
        var view = rig.View();
        var effort = Find<ComboBox>(view, "EffortBox");
        var limit = Find<ComboBox>(view, "DiffLimitBox");

        Assert.Equal(AiReasoningEffort.Levels.ToList().IndexOf("low"), effort.SelectedIndex);
        Assert.Equal(2, limit.SelectedIndex);
        Assert.Equal(6, limit.ItemCount);
        Assert.Equal(["5,000 tokens", "10,000 tokens", "25,000 tokens", "50,000 tokens", "100,000 tokens", "Uncapped"],
            limit.Items.Cast<string>());

        limit.SelectedIndex = 4;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(100_000, rig.Profiles.Profiles.Single().DiffCapTokens);
    }

    [AvaloniaFact]
    public async Task Constructing_the_view_makes_no_client_call()
    {
        var rig = new Rig();
        var gemini = await rig.Add("G", p => AiServiceTemplates.ApplyGemini(p) with { ApiKey = "k", Model = "gone" });
        await rig.Profiles.SetActiveAsync(gemini.Id);

        _ = rig.View();

        Assert.Equal(0, rig.Factory.Calls);
        Assert.Empty(rig.Client.Lists);
    }

    [AvaloniaFact]
    public void The_X_button_closes_the_panel()
    {
        var rig = new Rig();
        rig.Panel.Open();
        var view = rig.View();
        var close = Find<Button>(view, "CloseButton");

        Assert.Equal("X", close.Content);
        Assert.Equal("Close", ToolTip.GetTip(close));
        Click(close);

        Assert.False(rig.Panel.IsOpen);
    }

    [AvaloniaFact]
    public async Task Opening_the_panel_on_a_profile_selects_it()
    {
        var rig = new Rig();
        await rig.Add("A");
        var b = await rig.Add("B");
        rig.Panel.Open(b.Id);

        var view = rig.View();

        Assert.Equal(b.Id, view.ViewModel.SelectedId);
        Assert.Equal("B", Find<TextBox>(view, "NameBox").Text);
    }

    [AvaloniaFact]
    public async Task A_loaded_gemini_list_without_the_saved_model_shows_the_replacement_and_the_note()
    {
        var rig = new Rig();
        await rig.Add("G", p => AiServiceTemplates.ApplyGemini(p) with { ApiKey = "k", Model = "gemini-1.0-pro" });
        rig.Client.ListResult = () => ["gemini-2.0-flash", "gemini-2.5-flash", "gemini-3-flash-preview"];
        var view = rig.View();
        var model = Find<ComboBox>(view, "ModelBox");
        Assert.Equal("gemini-1.0-pro", model.Text);

        Click(Find<Button>(view, "ModelRefreshButton"));
        await AiTestSupport.PumpUntil(() => rig.Profiles.Profiles.Single().Model == "gemini-2.5-flash");
        await view.ViewModel.Editor!.FetchModelsCommand.ExecutionTask!;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("gemini-2.5-flash", model.Text);
        Assert.Equal("gemini-2.5-flash", model.SelectedItem);
        var note = Find<TextBlock>(view, "ModelNoteText");
        Assert.True(note.IsVisible);
        Assert.Contains("'gemini-1.0-pro' is no longer available", note.Text);
        Assert.True(model.IsEnabled);
    }

    [AvaloniaFact]
    public async Task An_unexpected_model_list_failure_leaves_the_model_box_usable()
    {
        var rig = new Rig();
        await rig.Add("C", p => p with { Server = "https://example.test/v1", ApiKey = "k", Model = "kept" });
        rig.Client.ListResult = () => throw new InvalidOperationException("odd shape");
        var view = rig.View();

        Click(Find<Button>(view, "ModelRefreshButton"));
        await view.ViewModel.Editor!.FetchModelsCommand.ExecutionTask!;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(NotificationSeverity.Error, Assert.Single(rig.Notifications.Published).Severity);
        Assert.True(Find<ComboBox>(view, "ModelBox").IsEnabled);
        Assert.Equal("kept", Find<ComboBox>(view, "ModelBox").Text);
    }

    [AvaloniaFact]
    public async Task A_list_loaded_elsewhere_fills_the_model_dropdown_and_keeps_the_text()
    {
        var rig = new Rig();
        var p = await rig.Add("A", p => p with { Server = "https://ai.example.test/v1", Model = "mine" });
        var view = rig.View();
        var model = Find<ComboBox>(view, "ModelBox");

        rig.Client.ListResult = () => ["remote-1", "remote-2"];
        await rig.Catalog.LoadAsync(p);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["remote-1", "remote-2"], Assert.IsAssignableFrom<IEnumerable<string>>(model.ItemsSource).ToArray());
        Assert.Equal("mine", model.Text);
        Assert.Equal("mine", rig.Profiles.Profiles.Single().Model);
        Assert.Single(rig.Client.Lists);
    }

    [AvaloniaFact]
    public async Task An_external_model_change_refreshes_the_model_box_but_not_the_other_fields()
    {
        var rig = new Rig();
        var profile = await rig.Add("A", p => AiServiceTemplates.ApplyGemini(p) with { Model = "old" });
        var view = rig.View();
        var name = Find<TextBox>(view, "NameBox");
        name.Text = "being typed";

        await rig.Profiles.UpdateAsync(profile.Id, p => p with { Model = "gemini-2.5-flash" });
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("gemini-2.5-flash", Find<ComboBox>(view, "ModelBox").Text);
        Assert.Equal("being typed", name.Text);
    }

    [AvaloniaFact]
    public async Task Picking_a_model_from_the_list_saves_it_at_once()
    {
        var rig = new Rig();
        var p = await rig.Add("A", p => p with { Server = "https://ai.example.test/v1" });
        rig.Catalog.Remember(p, ["model-1", "model-2"]);
        var view = rig.View();
        var model = Find<ComboBox>(view, "ModelBox");

        model.SelectedItem = "model-2";
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("model-2", rig.Profiles.Profiles.Single().Model);
    }

    [AvaloniaFact]
    public async Task Typing_a_model_saves_it_when_the_box_loses_focus()
    {
        var rig = new Rig();
        await rig.Add("A");
        var view = rig.View();
        var model = Find<ComboBox>(view, "ModelBox");

        model.Text = "  typed  ";
        model.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("typed", rig.Profiles.Profiles.Single().Model);
    }

    [AvaloniaFact]
    public async Task Building_the_editor_over_a_cached_list_shows_it_and_never_writes_the_profile()
    {
        var rig = new Rig();
        var p = await rig.Add("A", p => p with { Server = "https://ai.example.test/v1", Model = "custom-model" });
        rig.Catalog.Remember(p, ["model-1", "model-2"]);

        var view = rig.View();
        var model = Find<ComboBox>(view, "ModelBox");

        Assert.Equal(["model-1", "model-2"], Assert.IsAssignableFrom<IEnumerable<string>>(model.ItemsSource).ToArray());
        Assert.Equal("custom-model", model.Text);
        Assert.Equal("custom-model", rig.Profiles.Profiles.Single().Model);
    }

    [AvaloniaFact]
    public async Task Dispose_stops_listening()
    {
        var rig = new Rig();
        var a = await rig.Add("A", p => p with { Server = "https://ai.example.test/v1" });
        var view = rig.View();
        Assert.Equal(1, rig.Catalog.Subscribers);
        view.Dispose();

        await rig.Add("B");
        rig.Catalog.Remember(a, ["unheard-model"]);
        Dispatcher.UIThread.RunJobs();

        Assert.Single(RowBorders(view));
        Assert.Null(Find<ComboBox>(view, "ModelBox").ItemsSource);
        Assert.Equal(0, rig.Catalog.Subscribers);
    }

    [AvaloniaFact]
    public async Task The_active_button_reads_Active_and_sits_at_the_top_right_of_the_editor()
    {
        var rig = new Rig();
        await rig.Add("First");
        var view = rig.View();
        var active = Find<Button>(view, "ActiveButton");

        Assert.Equal("Active", active.Content);
        Assert.Equal(1, Grid.GetColumn(active));
        Assert.Equal(HorizontalAlignment.Right, active.HorizontalAlignment);
    }

    [AvaloniaFact]
    public async Task The_active_button_is_green_when_the_selected_profile_is_active_and_grey_when_not()
    {
        var rig = new Rig();
        var p = await rig.Add("First");
        var view = rig.View();
        var btn = Find<Button>(view, "ActiveButton");

        Assert.DoesNotContain("success", btn.Classes);
        if (Application.Current is not null && Application.Current.TryFindResource("ColorTextFaint", out var faint) && faint is IBrush faintBrush)
            Assert.Equal(faintBrush, btn.Foreground);
        if (Application.Current is not null && Application.Current.TryFindResource("ColorBorder", out var border) && border is IBrush borderBrush)
            Assert.Equal(borderBrush, btn.BorderBrush);

        await rig.Profiles.SetActiveAsync(p.Id);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("success", btn.Classes);
        if (Application.Current is not null && Application.Current.TryFindResource("ColorSuccess", out var success) && success is IBrush successBrush)
            Assert.Equal(successBrush, btn.Foreground);
    }

    [AvaloniaFact]
    public async Task The_active_button_and_the_list_check_always_agree()
    {
        var rig = new Rig();
        var a = await rig.Add("First");
        var view = rig.View();
        var btn = Find<Button>(view, "ActiveButton");

        Click(btn);
        await AiTestSupport.PumpUntil(() => rig.Profiles.ActiveProfile?.Id == a.Id);
        Assert.Contains("success", btn.Classes);
        Assert.Equal("Active", ToolTip.GetTip(RowCheck(view, 0)));

        Click(RowCheck(view, 0));
        await AiTestSupport.PumpUntil(() => rig.Profiles.ActiveProfile is null);
        Assert.DoesNotContain("success", btn.Classes);
        Assert.Equal("Make active", ToolTip.GetTip(RowCheck(view, 0)));
    }

    [AvaloniaFact]
    public async Task Switching_with_the_active_button_asks_with_Save_like_the_list_check()
    {
        var rig = new Rig();
        var a = await rig.Add("Gemini");
        var b = await rig.Add("Work");
        await rig.Profiles.SetActiveAsync(a.Id);
        var view = rig.View();

        PressRow(RowBorders(view).ElementAt(1));
        Assert.Equal(b.Id, view.ViewModel.SelectedId);

        Click(Find<Button>(view, "ActiveButton"));
        await AiTestSupport.PumpUntil(() => rig.Profiles.ActiveProfile?.Id == b.Id);

        var call = Assert.Single(rig.Dialogs.ChooseCalls);
        Assert.Equal("'1. Gemini' is the active AI integration. Switch to '2. Work'?", call.Message);
        Assert.Equal(["Save"], call.Options);
    }
}
