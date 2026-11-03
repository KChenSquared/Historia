namespace Historia.Tests;

/// <summary>
/// The AI Integrations view model: the numbered profile list, selection, the confirmed delete,
/// switching the active profile, the editor's commit-on-change fields, the Gemini template, and
/// the model list (cached, fetched, and the once-per-five-minutes Gemini switch).
/// </summary>
[Trait("tier", "unit")]
public class AiIntegrationsViewModelTests
{
    private sealed class Client : IAiChatClient
    {
        public List<AiChatRequest> Lists { get; } = [];
        public Func<IReadOnlyList<string>> ListResult { get; set; } = () => [];

        public Task<IReadOnlyList<string>> ListModelsAsync(AiChatRequest request, CancellationToken ct)
        {
            Lists.Add(request);
            return Task.FromResult(ListResult());
        }

        public Task<string> CompleteAsync(AiChatRequest request, CancellationToken ct) => Task.FromResult("feat: x");
    }

    private sealed class Factory(Client client) : IAiClientFactory
    {
        public int Calls { get; private set; }

        public IAiChatClient For(AiServiceKind kind)
        {
            Calls++;
            return client;
        }
    }

    private sealed class ManualTime : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }

    /// <summary>A real catalog that counts the handlers attached to <see cref="Changed"/>.</summary>
    private sealed class CountingCatalog(IAiModelCatalog inner) : IAiModelCatalog
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

    private sealed class Panel : IAiIntegrationsPanel
    {
        public bool IsOpen { get; private set; }
        public string? FocusProfileId { get; private set; }
        public event EventHandler? OpenChanged;

        public void Open(string? focusProfileId = null)
        {
            IsOpen = true;
            FocusProfileId = focusProfileId;
            OpenChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Close()
        {
            if (!IsOpen)
                return;
            IsOpen = false;
            OpenChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class Rig
    {
        public Rig(IUiDispatcher? ui = null)
        {
            Profiles = new AiProfileService(new FakeSettingsService());
            Factory = new Factory(Client);
            Catalog = new CountingCatalog(new AiModelCatalog(Profiles, Factory, Time));
            Ui = ui ?? new ImmediateUiDispatcher();
        }

        public AiProfileService Profiles { get; }
        public Panel Panel { get; } = new();
        public FakeDialogService Dialogs { get; } = new();
        public FakeNotificationService Notifications { get; } = new();
        public Client Client { get; } = new();
        public Factory Factory { get; }
        public ManualTime Time { get; } = new();
        public CountingCatalog Catalog { get; }
        public IUiDispatcher Ui { get; }

        public AiIntegrationsViewModel Create() => new(Profiles, Panel, Dialogs, Catalog, Ui, Notifications);

        public async Task<AiServiceProfile> Add(string name = "", Func<AiServiceProfile, AiServiceProfile>? configure = null)
        {
            var profile = await Profiles.AddAsync();
            await Profiles.UpdateAsync(profile.Id, p => (configure?.Invoke(p) ?? p) with { Name = name == "" ? p.Name : name });
            return Profiles.Profiles.First(p => p.Id == profile.Id);
        }
    }

    private static string[] Labels(AiIntegrationsViewModel vm) => [.. vm.Rows.Select(r => r.Label)];

    // ── List and selection ──

    [Fact]
    public async Task Profiles_are_numbered_in_the_order_they_were_added()
    {
        var rig = new Rig();
        await rig.Add("Gemini");
        await rig.Add("Work");

        var vm = rig.Create();

        Assert.Equal(["1. Gemini", "2. Work"], Labels(vm));
    }

    [Fact]
    public async Task The_first_profile_is_selected_and_has_an_editor()
    {
        var rig = new Rig();
        var first = await rig.Add("A");
        await rig.Add("B");

        var vm = rig.Create();

        Assert.Equal(first.Id, vm.SelectedId);
        Assert.Equal(first.Id, vm.Editor?.Id);
        Assert.True(vm.Rows[0].IsSelected);
        Assert.False(vm.Rows[1].IsSelected);
        Assert.True(vm.DeleteSelectedCommand.CanExecute(null));
    }

    [Fact]
    public void With_no_profile_there_is_no_editor_and_nothing_to_delete()
    {
        var rig = new Rig();

        var vm = rig.Create();

        Assert.Null(vm.SelectedId);
        Assert.Null(vm.Editor);
        Assert.False(vm.HasEditor);
        Assert.False(vm.DeleteSelectedCommand.CanExecute(null));
    }

    [Fact]
    public async Task Opening_the_panel_on_a_profile_selects_it()
    {
        var rig = new Rig();
        await rig.Add("A");
        var b = await rig.Add("B");
        rig.Panel.Open(b.Id);

        var vm = rig.Create();

        Assert.Equal(b.Id, vm.SelectedId);
    }

    [Fact]
    public async Task Opening_the_panel_again_on_another_profile_moves_the_selection()
    {
        var rig = new Rig();
        await rig.Add("A");
        var b = await rig.Add("B");
        var vm = rig.Create();

        rig.Panel.Open(b.Id);

        Assert.Equal(b.Id, vm.SelectedId);
        Assert.Equal(b.Id, vm.Editor?.Id);
    }

    [Fact]
    public async Task A_row_selects_its_profile()
    {
        var rig = new Rig();
        await rig.Add("A");
        var b = await rig.Add("B");
        var vm = rig.Create();

        vm.Rows[1].SelectCommand.Execute(null);

        Assert.Equal(b.Id, vm.SelectedId);
        Assert.Equal(b.Id, vm.Editor?.Id);
        Assert.True(vm.Rows[1].IsSelected);
    }

    [Fact]
    public async Task Adding_a_profile_appends_and_selects_it()
    {
        var rig = new Rig();
        await rig.Add("First");
        var vm = rig.Create();

        await vm.AddCommand.ExecuteAsync(null);

        Assert.Equal(2, rig.Profiles.Profiles.Count);
        Assert.Equal(rig.Profiles.Profiles[1].Id, vm.SelectedId);
        Assert.Equal("2. Unnamed Service", vm.Rows[1].Label);
    }

    [Fact]
    public async Task The_close_command_closes_the_panel()
    {
        var rig = new Rig();
        rig.Panel.Open();
        var vm = rig.Create();

        vm.CloseCommand.Execute(null);

        Assert.False(rig.Panel.IsOpen);
        await Task.CompletedTask;
    }

    // ── Delete ──

    [Fact]
    public async Task Delete_confirms_with_the_destructive_dialog_and_removes_the_profile()
    {
        var rig = new Rig();
        await rig.Add("Gemini");
        var b = await rig.Add("Work");
        await rig.Profiles.SetActiveAsync(b.Id);
        rig.Dialogs.EnqueueConfirmDestructive(true);
        var vm = rig.Create();
        vm.Rows[1].SelectCommand.Execute(null);

        await vm.DeleteSelectedCommand.ExecuteAsync(null);

        var call = Assert.Single(rig.Dialogs.ConfirmDestructiveCalls);
        Assert.Equal("Delete", call.ConfirmLabel);
        Assert.Contains("Delete '2. Work' and its saved API key?", call.Effect);
        Assert.Contains("It is the active integration, so AI commit messages will be turned off.", call.Effect);
        Assert.Contains("This can't be undone.", call.Effect);
        Assert.Null(rig.Profiles.ActiveProfile);
        Assert.Equal(["1. Gemini"], Labels(vm));
        Assert.Equal(rig.Profiles.Profiles[0].Id, vm.SelectedId);
    }

    [Fact]
    public async Task Cancelling_the_delete_removes_nothing()
    {
        var rig = new Rig();
        await rig.Add("Only");
        rig.Dialogs.EnqueueConfirmDestructive(false);
        var vm = rig.Create();

        await vm.DeleteSelectedCommand.ExecuteAsync(null);

        Assert.Single(rig.Dialogs.ConfirmDestructiveCalls);
        Assert.Single(rig.Profiles.Profiles);
        Assert.Single(vm.Rows);
    }

    // ── Active profile ──

    [Fact]
    public async Task Toggling_a_row_activates_with_no_prompt_and_toggling_the_active_row_deactivates()
    {
        var rig = new Rig();
        var a = await rig.Add("A");
        var vm = rig.Create();
        Assert.Equal("Make active", vm.Rows[0].ActiveTip);

        await vm.Rows[0].ToggleActiveCommand.ExecuteAsync(null);
        Assert.Equal(a.Id, rig.Profiles.ActiveProfile?.Id);
        Assert.DoesNotContain(rig.Dialogs.Calls, c => c.Method == nameof(IDialogService.ChooseAsync));
        Assert.Equal("Active", vm.Rows[0].ActiveTip);
        Assert.Equal("ColorSuccess", vm.Rows[0].CheckColorKey);

        await vm.Rows[0].ToggleActiveCommand.ExecuteAsync(null);
        Assert.Null(rig.Profiles.ActiveProfile);
        Assert.Equal("Make active", vm.Rows[0].ActiveTip);
        Assert.Equal("ColorTextFaint", vm.Rows[0].CheckColorKey);
    }

    [Fact]
    public async Task Switching_the_active_profile_asks_with_Save_as_the_default_option()
    {
        var rig = new Rig();
        var a = await rig.Add("Gemini");
        var b = await rig.Add("Work");
        await rig.Profiles.SetActiveAsync(a.Id);
        rig.Dialogs.EnqueueChoice(0);
        var vm = rig.Create();

        await vm.Rows[1].ToggleActiveCommand.ExecuteAsync(null);

        var call = Assert.Single(rig.Dialogs.Calls, c => c.Method == nameof(IDialogService.ChooseAsync));
        Assert.Equal("'1. Gemini' is the active AI integration. Switch to '2. Work'?", call.Detail);
        Assert.Equal(b.Id, rig.Profiles.ActiveProfile?.Id);
    }

    [Fact]
    public async Task Cancelling_the_switch_keeps_the_old_active_profile()
    {
        var rig = new Rig();
        var a = await rig.Add("Gemini");
        await rig.Add("Work");
        await rig.Profiles.SetActiveAsync(a.Id);
        rig.Dialogs.EnqueueChoice(-1);
        var vm = rig.Create();

        await vm.Rows[1].ToggleActiveCommand.ExecuteAsync(null);

        Assert.Equal(a.Id, rig.Profiles.ActiveProfile?.Id);
    }

    [Fact]
    public async Task The_editor_button_and_the_list_check_always_agree()
    {
        var rig = new Rig();
        var a = await rig.Add("First");
        var vm = rig.Create();
        var editor = vm.Editor!;
        Assert.False(editor.IsActive);

        await editor.ToggleActiveCommand.ExecuteAsync(null);
        Assert.Equal(a.Id, rig.Profiles.ActiveProfile?.Id);
        Assert.True(editor.IsActive);
        Assert.Equal("Active", editor.ActiveTip);
        Assert.Equal("Active", vm.Rows[0].ActiveTip);

        await vm.Rows[0].ToggleActiveCommand.ExecuteAsync(null);
        Assert.False(editor.IsActive);
        Assert.Equal("Make active", editor.ActiveTip);
    }

    // ── Editor fields ──

    [Fact]
    public async Task The_key_box_has_a_red_frame_until_a_key_is_entered()
    {
        var rig = new Rig();
        await rig.Add("A");
        var editor = rig.Create().Editor!;
        Assert.True(editor.KeyMissing);

        editor.ApiKey = "sk-123";

        Assert.False(editor.KeyMissing);
        Assert.Equal("sk-123", rig.Profiles.Profiles.Single().ApiKey);
    }

    [Fact]
    public async Task The_red_frame_returns_in_env_mode_when_the_variable_is_unset()
    {
        var rig = new Rig();
        await rig.Add("A", p => p with { ApiKey = "sk-123" });
        var editor = rig.Create().Editor!;
        Assert.False(editor.KeyMissing);

        editor.ReadApiKeyFromEnv = true;

        // The saved key text now names an environment variable that doesn't exist.
        Assert.True(editor.KeyMissing);
        Assert.True(rig.Profiles.Profiles.Single().ReadApiKeyFromEnv);
    }

    [Fact]
    public async Task Committing_a_field_keeps_the_editor_and_renames_the_row()
    {
        var rig = new Rig();
        await rig.Add("A");
        var vm = rig.Create();
        var editor = vm.Editor!;

        editor.Name = "Renamed";

        Assert.Same(editor, vm.Editor);
        Assert.Equal("1. Renamed", vm.Rows[0].Label);
        Assert.Equal("Renamed", rig.Profiles.Profiles.Single().Name);
    }

    [Fact]
    public async Task Every_field_saves_its_own_profile_property()
    {
        var rig = new Rig();
        await rig.Add("A");
        var editor = rig.Create().Editor!;

        editor.Server = "https://ai.example.test/v1";
        editor.AdditionalPrompt = "- be brief";
        editor.ExtraHeaders = "X-Test: 1";
        editor.AutoFetchModels = false;
        editor.ReasoningEffortIndex = AiReasoningEffort.Levels.ToList().IndexOf("high");
        editor.DiffLimitIndex = 3;

        var saved = rig.Profiles.Profiles.Single();
        Assert.Equal("https://ai.example.test/v1", saved.Server);
        Assert.Equal("- be brief", saved.AdditionalPrompt);
        Assert.Equal("X-Test: 1", saved.ExtraHeaders);
        Assert.False(saved.AutoFetchModels);
        Assert.Equal("high", saved.ReasoningEffort);
        Assert.Equal(50_000, saved.DiffCapTokens);
    }

    [Fact]
    public async Task An_unchanged_value_saves_nothing()
    {
        var rig = new Rig();
        await rig.Add("A");
        var editor = rig.Create().Editor!;
        var before = rig.Profiles.Profiles.Single();

        editor.Name = before.Name;
        editor.DiffLimitIndex = -1;
        editor.ReasoningEffortIndex = -1;

        Assert.Same(before, rig.Profiles.Profiles.Single());
    }

    [Fact]
    public async Task An_external_change_updates_the_fields_and_keeps_the_editor()
    {
        var rig = new Rig();
        var profile = await rig.Add("A", p => AiServiceTemplates.ApplyGemini(p) with { Model = "old" });
        var vm = rig.Create();
        var editor = vm.Editor!;

        await rig.Profiles.UpdateAsync(profile.Id, p => p with { Model = "gemini-2.5-flash", Server = "https://changed.example.test" });

        Assert.Same(editor, vm.Editor);
        Assert.Equal("gemini-2.5-flash", editor.Model);
        Assert.Equal("https://changed.example.test", editor.Server);
    }

    [Fact]
    public async Task An_external_change_leaves_a_field_it_does_not_concern_alone()
    {
        var rig = new Rig();
        var profile = await rig.Add("A");
        var editor = rig.Create().Editor!;
        var edits = new List<string?>();
        editor.PropertyChanged += (_, e) => edits.Add(e.PropertyName);

        await rig.Profiles.UpdateAsync(profile.Id, p => p with { Server = "https://elsewhere.example.test" });

        Assert.DoesNotContain(nameof(AiProfileEditorViewModel.Name), edits);
        Assert.Contains(nameof(AiProfileEditorViewModel.Server), edits);
    }

    [Fact]
    public async Task An_external_delete_selects_the_first_remaining_profile()
    {
        var rig = new Rig();
        var a = await rig.Add("A");
        var b = await rig.Add("B");
        var vm = rig.Create();
        vm.Select(b.Id);

        await rig.Profiles.DeleteAiServiceAsync(b.Id);

        Assert.Equal(a.Id, vm.SelectedId);
        Assert.Equal(a.Id, vm.Editor?.Id);
    }

    // ── Template ──

    [Fact]
    public async Task Choosing_Gemini_fills_the_server_and_leaves_the_key_alone()
    {
        var rig = new Rig();
        await rig.Add("", p => p with { ApiKey = "kept" });
        var editor = rig.Create().Editor!;

        editor.TemplateIndex = 1;
        await editor.TemplateChangedCommand.ExecutionTask!;

        var saved = rig.Profiles.Profiles.Single();
        Assert.Equal(AiServiceKind.Gemini, saved.Kind);
        Assert.Equal(AiServiceTemplates.GeminiServer, saved.Server);
        Assert.Equal("Gemini", saved.Name);
        Assert.Equal("kept", saved.ApiKey);
        Assert.Equal(AiServiceTemplates.GeminiServer, editor.Server);
        Assert.Equal("Gemini", editor.Name);
        Assert.DoesNotContain(rig.Dialogs.Calls, c => c.Method == nameof(IDialogService.ConfirmAsync));
    }

    [Fact]
    public async Task Choosing_Gemini_over_a_set_server_asks_first_and_declining_keeps_everything()
    {
        var rig = new Rig();
        await rig.Add("A", p => p with { Server = "https://example.test/v1", Model = "gpt-x" });
        rig.Dialogs.EnqueueConfirm(false);
        var editor = rig.Create().Editor!;

        editor.TemplateIndex = 1;
        await editor.TemplateChangedCommand.ExecutionTask!;

        var call = Assert.Single(rig.Dialogs.Calls, c => c.Method == nameof(IDialogService.ConfirmAsync));
        Assert.Equal("Fill in Gemini's connection? Replaces Server and Model.", call.Detail);
        Assert.Equal(AiServiceKind.Custom, rig.Profiles.Profiles.Single().Kind);
        Assert.Equal("https://example.test/v1", rig.Profiles.Profiles.Single().Server);
        Assert.Equal(0, editor.TemplateIndex);
    }

    [Fact]
    public async Task Accepting_the_replacement_applies_the_Gemini_template()
    {
        var rig = new Rig();
        await rig.Add("A", p => p with { Server = "https://example.test/v1", Model = "gpt-x" });
        rig.Dialogs.EnqueueConfirm(true);
        var editor = rig.Create().Editor!;

        editor.TemplateIndex = 1;
        await editor.TemplateChangedCommand.ExecutionTask!;

        Assert.Equal(AiServiceKind.Gemini, rig.Profiles.Profiles.Single().Kind);
        Assert.Equal(AiServiceTemplates.GeminiServer, editor.Server);
        Assert.Equal(1, editor.TemplateIndex);
    }

    // ── Models ──

    [Fact]
    public async Task Constructing_the_view_model_makes_no_client_call()
    {
        var rig = new Rig();
        var gemini = await rig.Add("G", p => AiServiceTemplates.ApplyGemini(p) with { ApiKey = "k", Model = "gone" });
        await rig.Profiles.SetActiveAsync(gemini.Id);

        _ = rig.Create();

        Assert.Equal(0, rig.Factory.Calls);
        Assert.Empty(rig.Client.Lists);
    }

    [Fact]
    public async Task A_loaded_gemini_list_without_the_saved_model_selects_the_replacement_and_notes_it()
    {
        var rig = new Rig();
        await rig.Add("G", p => AiServiceTemplates.ApplyGemini(p) with { ApiKey = "k", Model = "gemini-1.0-pro" });
        rig.Client.ListResult = () => ["gemini-2.0-flash", "gemini-2.5-flash", "gemini-3-flash-preview"];
        var editor = rig.Create().Editor!;

        await editor.FetchModelsCommand.ExecuteAsync(null);

        Assert.Equal("gemini-2.5-flash", rig.Profiles.Profiles.Single().Model);
        Assert.Equal("gemini-2.5-flash", editor.Model);
        Assert.Equal("gemini-2.5-flash", editor.SelectedModel);
        Assert.True(editor.HasModelNote);
        Assert.Contains("'gemini-1.0-pro' is no longer available", editor.ModelNote);
        Assert.Contains("'gemini-2.5-flash'", editor.ModelNote);
        Assert.True(editor.CanEditModel);
    }

    [Fact]
    public async Task A_loaded_gemini_list_that_has_the_saved_model_changes_nothing()
    {
        var rig = new Rig();
        await rig.Add("G", p => AiServiceTemplates.ApplyGemini(p) with { ApiKey = "k", Model = "gemini-2.0-flash" });
        rig.Client.ListResult = () => ["gemini-2.0-flash", "gemini-2.5-flash"];
        var editor = rig.Create().Editor!;

        await editor.FetchModelsCommand.ExecuteAsync(null);

        Assert.Equal("gemini-2.0-flash", rig.Profiles.Profiles.Single().Model);
        Assert.False(editor.HasModelNote);
        Assert.Equal(["gemini-2.0-flash", "gemini-2.5-flash"], editor.ModelItems);
        Assert.Single(rig.Client.Lists);
    }

    [Fact]
    public async Task A_custom_profile_never_switches_its_model_on_its_own()
    {
        var rig = new Rig();
        await rig.Add("C", p => p with { Server = "https://example.test/v1", ApiKey = "k", Model = "old-model" });
        rig.Client.ListResult = () => ["new-a", "new-b"];
        var editor = rig.Create().Editor!;

        await editor.FetchModelsCommand.ExecuteAsync(null);

        Assert.Equal("old-model", rig.Profiles.Profiles.Single().Model);
        Assert.False(editor.HasModelNote);
    }

    [Fact]
    public async Task A_failed_model_list_publishes_a_classified_notification()
    {
        var rig = new Rig();
        await rig.Add("C", p => p with { Server = "https://example.test/v1", ApiKey = "k" });
        rig.Client.ListResult = () => throw new AiException(AiErrorKind.Unauthorized, "The AI service rejected the API key.", "HTTP 401");
        var editor = rig.Create().Editor!;

        await editor.FetchModelsCommand.ExecuteAsync(null);

        var note = Assert.Single(rig.Notifications.Published);
        Assert.Equal(NotificationSeverity.Error, note.Severity);
        Assert.Contains("rejected the API key", note.Message);
        Assert.Equal("HTTP 401", note.Details);
        Assert.True(editor.CanEditModel);
    }

    [Fact]
    public async Task An_unexpected_model_list_failure_is_reported_not_swallowed()
    {
        var rig = new Rig();
        await rig.Add("C", p => p with { Server = "https://example.test/v1", ApiKey = "k", Model = "kept" });
        rig.Client.ListResult = () => throw new InvalidOperationException("odd shape");
        var editor = rig.Create().Editor!;

        await editor.FetchModelsCommand.ExecuteAsync(null);

        var note = Assert.Single(rig.Notifications.Published);
        Assert.Equal(NotificationSeverity.Error, note.Severity);
        Assert.Contains("Couldn't load the model list", note.Message);
        Assert.True(editor.CanEditModel);
        Assert.Equal("kept", rig.Profiles.Profiles.Single().Model);
    }

    [Fact]
    public async Task Refreshing_with_no_server_asks_for_one_without_a_request()
    {
        var rig = new Rig();
        await rig.Add("C");
        var editor = rig.Create().Editor!;

        await editor.FetchModelsCommand.ExecuteAsync(null);

        Assert.Equal(NotificationSeverity.Warning, Assert.Single(rig.Notifications.Published).Severity);
        Assert.Equal(0, rig.Factory.Calls);
    }

    [Fact]
    public async Task A_list_loaded_elsewhere_fills_the_model_dropdown_without_a_request()
    {
        var rig = new Rig();
        var p = await rig.Add("A", p => p with { Server = "https://ai.example.test/v1" });
        var editor = rig.Create().Editor!;

        rig.Client.ListResult = () => ["remote-1", "remote-2"];
        await rig.Catalog.LoadAsync(p);

        Assert.Equal(["remote-1", "remote-2"], editor.ModelItems);
        Assert.Single(rig.Client.Lists);
    }

    [Fact]
    public async Task Opening_the_model_list_with_a_cached_list_makes_no_request()
    {
        var rig = new Rig();
        var p = await rig.Add("A", p => p with { Server = "https://ai.example.test/v1" });
        rig.Catalog.Remember(p, ["cached-a", "cached-b"]);
        var editor = rig.Create().Editor!;

        await editor.OpenModelListCommand.ExecuteAsync(null);

        Assert.Empty(rig.Client.Lists);
    }

    [Fact]
    public async Task Opening_the_model_list_fetches_it_when_auto_fetch_is_on()
    {
        var rig = new Rig();
        await rig.Add("A", p => p with { Server = "https://ai.example.test/v1", ApiKey = "k" });
        rig.Client.ListResult = () => ["m1", "m2"];
        var editor = rig.Create().Editor!;

        editor.IsModelListOpen = true;
        await editor.OpenModelListCommand.ExecutionTask!;

        Assert.Single(rig.Client.Lists);
        Assert.Equal(["m1", "m2"], editor.ModelItems);
    }

    [Fact]
    public async Task Opening_the_model_list_fetches_nothing_when_auto_fetch_is_off()
    {
        var rig = new Rig();
        await rig.Add("A", p => p with { Server = "https://ai.example.test/v1", AutoFetchModels = false });
        var editor = rig.Create().Editor!;

        await editor.OpenModelListCommand.ExecuteAsync(null);

        Assert.Empty(rig.Client.Lists);
    }

    [Fact]
    public async Task A_cached_gemini_list_without_the_saved_model_switches_it_when_the_list_opens()
    {
        var rig = new Rig();
        var p = await rig.Add("G", p => AiServiceTemplates.ApplyGemini(p) with { ApiKey = "k", Model = "gemini-1.0-pro" });
        rig.Catalog.Remember(p, ["gemini-2.0-flash", "gemini-2.5-flash"]);
        var editor = rig.Create().Editor!;

        await editor.OpenModelListCommand.ExecuteAsync(null);

        Assert.Equal("gemini-2.5-flash", rig.Profiles.Profiles.Single().Model);
        Assert.Equal("gemini-2.5-flash", editor.Model);
        Assert.True(editor.HasModelNote);
        Assert.Empty(rig.Client.Lists);
    }

    [Fact]
    public async Task Building_the_editor_over_a_cached_list_never_writes_the_profile()
    {
        var rig = new Rig();
        var p = await rig.Add("A", p => p with { Server = "https://ai.example.test/v1", Model = "custom-model" });
        rig.Catalog.Remember(p, ["model-1", "model-2"]);

        var editor = rig.Create().Editor!;

        Assert.Equal(["model-1", "model-2"], editor.ModelItems);
        Assert.Equal("custom-model", editor.Model);
        Assert.Null(editor.SelectedModel);
        Assert.Equal("custom-model", rig.Profiles.Profiles.Single().Model);
    }

    [Fact]
    public async Task Typing_a_model_saves_the_trimmed_text_and_picking_one_saves_it_at_once()
    {
        var rig = new Rig();
        var p = await rig.Add("A", p => p with { Server = "https://ai.example.test/v1" });
        rig.Catalog.Remember(p, ["model-1", "model-2"]);
        var editor = rig.Create().Editor!;

        editor.Model = "  typed-model  ";
        Assert.Equal("typed-model", rig.Profiles.Profiles.Single().Model);

        editor.SelectedModel = "model-2";
        Assert.Equal("model-2", rig.Profiles.Profiles.Single().Model);
        Assert.Equal("model-2", editor.Model);
    }

    [Fact]
    public async Task The_gemini_switch_runs_at_most_once_every_five_minutes_from_any_source()
    {
        var rig = new Rig();
        var p = await rig.Add("G", p => AiServiceTemplates.ApplyGemini(p) with { ApiKey = "k", Model = "gemini-1.0-pro" });
        rig.Client.ListResult = () => ["gemini-2.0-flash", "gemini-2.5-flash"];
        var vm = rig.Create();

        // The editor's own load switches the retired model.
        await vm.Editor!.FetchModelsCommand.ExecuteAsync(null);
        Assert.Equal("gemini-2.5-flash", rig.Profiles.Profiles.Single().Model);

        // Within five minutes neither opening the list nor a new editor switches it again.
        await rig.Profiles.UpdateAsync(p.Id, x => x with { Model = "gemini-typed-by-hand" });
        await vm.Editor!.OpenModelListCommand.ExecuteAsync(null);
        Assert.Equal("gemini-typed-by-hand", rig.Profiles.Profiles.Single().Model);

        vm.Dispose();
        rig.Time.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        var second = rig.Create();
        await second.Editor!.OpenModelListCommand.ExecuteAsync(null);
        Assert.Equal("gemini-typed-by-hand", rig.Profiles.Profiles.Single().Model);
        Assert.False(second.Editor.HasModelNote);

        // Five minutes after the last switch, opening the list switches again.
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        await second.Editor.OpenModelListCommand.ExecuteAsync(null);
        Assert.Equal("gemini-2.5-flash", rig.Profiles.Profiles.Single().Model);
        Assert.Single(rig.Client.Lists);
    }

    [Fact]
    public async Task A_new_edit_clears_the_switched_model_note()
    {
        var rig = new Rig();
        await rig.Add("G", p => AiServiceTemplates.ApplyGemini(p) with { ApiKey = "k", Model = "gemini-1.0-pro" });
        rig.Client.ListResult = () => ["gemini-2.0-flash", "gemini-2.5-flash"];
        var editor = rig.Create().Editor!;
        await editor.FetchModelsCommand.ExecuteAsync(null);
        Assert.True(editor.HasModelNote);

        editor.Name = "Renamed";

        Assert.False(editor.HasModelNote);
    }

    [Fact]
    public void PublishError_prefixes_the_summary_and_carries_the_details()
    {
        var notifications = new FakeNotificationService();

        AiIntegrationsViewModel.PublishError(notifications, new AiException(AiErrorKind.Unauthorized, "Rejected.", "HTTP 401"), "Couldn't load");
        AiIntegrationsViewModel.PublishError(notifications, new AiException(AiErrorKind.Unauthorized, "Rejected."));

        Assert.Equal("Couldn't load: Rejected.", notifications.Published[0].Message);
        Assert.Equal("HTTP 401", notifications.Published[0].Details);
        Assert.Equal("Rejected.", notifications.Published[1].Message);
        AiIntegrationsViewModel.PublishError(null, new AiException(AiErrorKind.Unauthorized, "Rejected."));
    }

    // ── Threading and lifetime ──

    [Fact]
    public async Task Service_events_reach_the_view_model_through_the_ui_dispatcher()
    {
        var ui = new RecordingUiDispatcher();
        var rig = new Rig(ui);
        var vm = rig.Create();
        Assert.Empty(vm.Rows);

        await rig.Add("Late");

        Assert.NotEqual(0, ui.PostCount);
        Assert.Empty(vm.Rows);
        ui.DrainPosted();
        Assert.Equal(["1. Late"], Labels(vm));
        Assert.NotNull(vm.Editor);
    }

    [Fact]
    public async Task Dispose_stops_listening_and_stops_the_editor()
    {
        var rig = new Rig();
        var a = await rig.Add("A", p => p with { Server = "https://ai.example.test/v1" });
        var vm = rig.Create();
        var editor = vm.Editor!;
        Assert.Equal(1, rig.Catalog.Subscribers);

        vm.Dispose();

        await rig.Add("B");
        rig.Catalog.Remember(a, ["unheard-model"]);
        editor.Name = "after dispose";

        Assert.Single(vm.Rows);
        Assert.Null(editor.ModelItems);
        Assert.Equal(0, rig.Catalog.Subscribers);
        Assert.Equal("A", rig.Profiles.Profiles[0].Name);
        Assert.True(editor.IsDisposed);
    }

    [Fact]
    public async Task A_queued_event_after_dispose_is_ignored()
    {
        var ui = new RecordingUiDispatcher();
        var rig = new Rig(ui);
        await rig.Add("A");
        var vm = rig.Create();
        await rig.Add("B");
        vm.Dispose();

        ui.DrainPosted();

        Assert.Single(vm.Rows);
    }
}
