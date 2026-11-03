namespace Historia.ViewModels;

/// <summary>
/// The editor of one AI integration. Every field is a bound property that saves the profile when
/// its value changes, unless the change came from <see cref="Sync"/> or from loading the profile.
/// <see cref="Snapshot"/> is the profile as last saved or observed, so the change event a save
/// raises is recognised as this editor's own. Models are fetched only when the model list opens or
/// the user presses refresh. Once disposed, the editor ignores late results and never saves.
/// </summary>
public sealed partial class AiProfileEditorViewModel : ViewModelBase
{
    private readonly IAiProfileService _profiles;
    private readonly IAiModelCatalog _catalog;
    private readonly IDialogService _dialogs;
    private readonly INotificationService? _notifications;
    private readonly Action _committed;
    private readonly Func<string, Task> _toggleActive;
    private CancellationTokenSource? _modelsCts;
    private bool _syncing;

    /// <param name="committed">Runs after a save, so the profile list can refresh its labels.</param>
    /// <param name="toggleActive">Makes the profile with the given id active, or turns AI off.</param>
    public AiProfileEditorViewModel(
        AiServiceProfile profile,
        IAiProfileService profiles,
        IAiModelCatalog catalog,
        IDialogService dialogs,
        INotificationService? notifications,
        Action committed,
        Func<string, Task> toggleActive)
    {
        _profiles = profiles;
        _catalog = catalog;
        _dialogs = dialogs;
        _notifications = notifications;
        _committed = committed;
        _toggleActive = toggleActive;

        Id = profile.Id;
        Snapshot = profile;

        _syncing = true;
        try
        {
            ModelItems = _catalog.Get(profile);
            Name = profile.Name;
            Server = profile.Server;
            ApiKey = profile.ApiKey;
            ReadApiKeyFromEnv = profile.ReadApiKeyFromEnv;
            AutoFetchModels = profile.AutoFetchModels;
            ReasoningEffortIndex = EffortIndex(profile);
            DiffLimitIndex = DiffLimitIndexOf(profile);
            AdditionalPrompt = profile.AdditionalPrompt;
            ExtraHeaders = profile.ExtraHeaders;
            TemplateIndex = TemplateIndexOf(profile);
            SetModelText(profile.Model);
        }
        finally
        {
            _syncing = false;
        }
        UpdateKeyState();
        RefreshActive();
    }

    public string Id { get; }

    /// <summary>The profile as last saved or observed.</summary>
    public AiServiceProfile Snapshot { get; private set; }

    public IReadOnlyList<string> TemplateNames { get; } = ["Custom (default)", "Gemini"];

    public IReadOnlyList<string> ReasoningEffortLevels => AiReasoningEffort.Levels;

    public IReadOnlyList<string> DiffLimitLabels { get; } = ["5,000 tokens", "10,000 tokens", "25,000 tokens", "50,000 tokens", "100,000 tokens", "Uncapped"];

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial string Server { get; set; }

    [ObservableProperty]
    public partial string ApiKey { get; set; }

    [ObservableProperty]
    public partial bool ReadApiKeyFromEnv { get; set; }

    [ObservableProperty]
    public partial bool AutoFetchModels { get; set; }

    [ObservableProperty]
    public partial int ReasoningEffortIndex { get; set; }

    [ObservableProperty]
    public partial int DiffLimitIndex { get; set; }

    [ObservableProperty]
    public partial string AdditionalPrompt { get; set; }

    [ObservableProperty]
    public partial string ExtraHeaders { get; set; }

    /// <summary>0 for the Custom template, 1 for Gemini.</summary>
    [ObservableProperty]
    public partial int TemplateIndex { get; set; }

    /// <summary>The model text. Typed text saves when the field is left; a picked item saves at once.</summary>
    [ObservableProperty]
    public partial string Model { get; set; }

    /// <summary>The list item matching <see cref="Model"/>, or null when the list lacks it.</summary>
    [ObservableProperty]
    public partial string? SelectedModel { get; set; }

    /// <summary>The known model ids, or null before any list was loaded.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string>? ModelItems { get; private set; }

    /// <summary>True while the model list is open; opening it may fetch or check the list.</summary>
    [ObservableProperty]
    public partial bool IsModelListOpen { get; set; }

    /// <summary>The note shown after the model was switched on its own, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasModelNote))]
    public partial string? ModelNote { get; private set; }

    public bool HasModelNote => !string.IsNullOrEmpty(ModelNote);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditModel))]
    [NotifyPropertyChangedFor(nameof(ModelPlaceholder))]
    public partial bool IsLoadingModels { get; private set; }

    public bool CanEditModel => !IsLoadingModels;

    public string? ModelPlaceholder => IsLoadingModels ? "Loading…" : null;

    /// <summary>True when no API key can be resolved, which frames the key box in red.</summary>
    [ObservableProperty]
    public partial bool KeyMissing { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveTip))]
    public partial bool IsActive { get; private set; }

    public string ActiveTip => IsActive ? "Active" : "Make active";

    /// <summary>Re-reads whether this profile is the active one.</summary>
    public void RefreshActive() => IsActive = _profiles.ActiveProfile?.Id == Id;

    /// <summary>Applies a change that came from elsewhere: each field whose value differs from the
    /// editor's takes the new value, so a field the user is typing in is left alone unless the
    /// change really concerns it.</summary>
    public void Sync(AiServiceProfile current)
    {
        if (IsDisposed || current == Snapshot)
            return;
        Snapshot = current;
        ApplySnapshot();
    }

    private void ApplySnapshot()
    {
        var current = Snapshot;
        _syncing = true;
        try
        {
            if (Name != current.Name) Name = current.Name;
            if (Server != current.Server) Server = current.Server;
            if (ApiKey != current.ApiKey) ApiKey = current.ApiKey;
            if (ReadApiKeyFromEnv != current.ReadApiKeyFromEnv) ReadApiKeyFromEnv = current.ReadApiKeyFromEnv;
            if (AutoFetchModels != current.AutoFetchModels) AutoFetchModels = current.AutoFetchModels;
            if (ReasoningEffortIndex != EffortIndex(current)) ReasoningEffortIndex = EffortIndex(current);
            if (DiffLimitIndex != DiffLimitIndexOf(current)) DiffLimitIndex = DiffLimitIndexOf(current);
            if (AdditionalPrompt != current.AdditionalPrompt) AdditionalPrompt = current.AdditionalPrompt;
            if (ExtraHeaders != current.ExtraHeaders) ExtraHeaders = current.ExtraHeaders;
            if (TemplateIndex != TemplateIndexOf(current)) TemplateIndex = TemplateIndexOf(current);
            if (Model != current.Model || SelectedModel != ListedModel(current.Model)) SetModelText(current.Model);
        }
        finally
        {
            _syncing = false;
        }
        UpdateKeyState();
    }

    /// <summary>Takes the catalog's list for this profile when it differs from the one shown.</summary>
    public void OnCatalogChanged()
    {
        if (IsDisposed)
            return;
        var list = _catalog.Get(Snapshot);
        if (list is not null && (ModelItems is null || !ModelItems.SequenceEqual(list)))
        {
            _syncing = true;
            try
            {
                ModelItems = list;
                SetModelText(Snapshot.Model);
            }
            finally
            {
                _syncing = false;
            }
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task ToggleActiveAsync() => _toggleActive(Id);

    /// <summary>Loads the model list from the service. Asks for a Server first when there is none.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task FetchModelsAsync()
    {
        var profile = Snapshot;
        if (string.IsNullOrWhiteSpace(profile.Server))
        {
            _notifications?.Publish(NotificationSeverity.Warning, "Set the Server before loading models.");
            return Task.CompletedTask;
        }
        return LoadModelsAsync(profile);
    }

    /// <summary>Opening the list uses a cached list without a request, or fetches when auto-fetch is on.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task OpenModelListAsync()
    {
        if (IsDisposed)
            return Task.CompletedTask;
        var cached = _catalog.Get(Snapshot);
        if (cached is not null)
            return SwitchMissingGeminiModelAsync(cached);
        if (Snapshot.AutoFetchModels)
            return FetchModelsAsync();
        return Task.CompletedTask;
    }

    /// <summary>Applies the template chosen in <see cref="TemplateIndex"/>. Replacing a connection that
    /// is already filled in asks first, and declining puts the previous template back.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task TemplateChangedAsync()
    {
        try
        {
            // Leave the property change first. The combo box is still pushing its new index, and
            // putting the previous template back from inside that push would not reach it.
            await Task.Yield();
            var kind = TemplateIndex == 1 ? AiServiceKind.Gemini : AiServiceKind.Custom;
            if (kind == Snapshot.Kind)
                return;

            if (AiServiceTemplates.WouldOverwrite(Snapshot, kind)
                && !await _dialogs.ConfirmAsync("Use the Gemini template", "Fill in Gemini's connection? Replaces Server and Model.", confirmLabel: "Replace"))
            {
                if (!IsDisposed)
                {
                    _syncing = true;
                    try { TemplateIndex = TemplateIndexOf(Snapshot); }
                    finally { _syncing = false; }
                }
                return;
            }
            if (IsDisposed)
                return;

            await CommitAsync(p => kind == AiServiceKind.Gemini ? AiServiceTemplates.ApplyGemini(p) : AiServiceTemplates.ApplyCustom(p));
            if (IsDisposed)
                return;
            ApplySnapshot();
        }
        catch (Exception ex)
        {
            Report(ex, "Couldn't apply the template");
        }
    }

    partial void OnNameChanged(string value) => Edit(value, p => p.Name, (p, v) => p with { Name = v });

    partial void OnServerChanged(string value) => Edit(value, p => p.Server, (p, v) => p with { Server = v });

    partial void OnApiKeyChanged(string value) => Edit(value, p => p.ApiKey, (p, v) => p with { ApiKey = v });

    partial void OnAdditionalPromptChanged(string value) => Edit(value, p => p.AdditionalPrompt, (p, v) => p with { AdditionalPrompt = v });

    partial void OnExtraHeadersChanged(string value) => Edit(value, p => p.ExtraHeaders, (p, v) => p with { ExtraHeaders = v });

    partial void OnReadApiKeyFromEnvChanged(bool value) => Edit(value, p => p.ReadApiKeyFromEnv, (p, v) => p with { ReadApiKeyFromEnv = v });

    partial void OnAutoFetchModelsChanged(bool value) => Edit(value, p => p.AutoFetchModels, (p, v) => p with { AutoFetchModels = v });

    partial void OnReasoningEffortIndexChanged(int value)
    {
        if (value >= 0 && value < AiReasoningEffort.Levels.Count)
            Edit(value, EffortIndex, (p, i) => p with { ReasoningEffort = AiReasoningEffort.Levels[i] });
    }

    partial void OnDiffLimitIndexChanged(int value)
    {
        if (value >= 0 && value < AiDiffCap.TokenSteps.Count)
            Edit(value, DiffLimitIndexOf, (p, i) => p with { DiffCapTokens = AiDiffCap.TokenSteps[i] });
    }

    partial void OnTemplateIndexChanged(int value)
    {
        if (!_syncing && !IsDisposed && value >= 0)
            TemplateChangedCommand.Execute(null);
    }

    partial void OnModelChanged(string value)
    {
        if (_syncing || IsDisposed)
            return;
        var typed = (value ?? "").Trim();
        if (typed != Snapshot.Model)
            Run(() => CommitAsync(p => p with { Model = typed }));
    }

    partial void OnSelectedModelChanged(string? value)
    {
        if (_syncing || IsDisposed || value is not { } picked || picked == Snapshot.Model)
            return;
        _syncing = true;
        try { Model = picked; }
        finally { _syncing = false; }
        Run(() => CommitAsync(p => p with { Model = picked }));
    }

    partial void OnIsModelListOpenChanged(bool value)
    {
        if (value && !IsDisposed)
            OpenModelListCommand.Execute(null);
    }

    private void Edit<T>(T value, Func<AiServiceProfile, T> read, Func<AiServiceProfile, T, AiServiceProfile> write)
    {
        if (_syncing || IsDisposed || EqualityComparer<T>.Default.Equals(value, read(Snapshot)))
            return;
        Run(() => CommitAsync(p => write(p, value)));
    }

    /// <summary>Saves a change, records it as the snapshot (so the change event it raises is
    /// recognised as this editor's own), and refreshes the list and the key frame. A new edit
    /// clears the "switched model" note.</summary>
    private Task CommitAsync(Func<AiServiceProfile, AiServiceProfile> mutate)
    {
        ModelNote = null;
        var task = _profiles.UpdateAsync(Id, mutate, CancellationToken.None);
        if (_profiles.Profiles.FirstOrDefault(p => p.Id == Id) is { } saved)
            Snapshot = saved;
        _committed();
        UpdateKeyState();
        return task;
    }

    private void UpdateKeyState() => KeyMissing = !_profiles.HasApiKey(Snapshot);

    private string? ListedModel(string model) => ModelItems is { } items && items.Contains(model) ? model : null;

    // Puts the model into the combo box: the list item that matches it, and the text itself.
    // Both are announced again even when unchanged, because replacing the list clears the combo.
    private void SetModelText(string model)
    {
        var was = _syncing;
        _syncing = true;
        try
        {
            SelectedModel = ListedModel(model);
            Model = model;
            OnPropertyChanged(nameof(SelectedModel));
            OnPropertyChanged(nameof(Model));
        }
        finally
        {
            _syncing = was;
        }
    }

    private async Task LoadModelsAsync(AiServiceProfile profile)
    {
#pragma warning disable S6966 // synchronous cancel keeps the order; an await would let a re-entrant load run first
        _modelsCts?.Cancel();
#pragma warning restore S6966
        var cts = new CancellationTokenSource();
        _modelsCts = cts;

        IsLoadingModels = true;
        try
        {
            var ids = await _catalog.LoadAsync(profile, cts.Token);
            if (cts.IsCancellationRequested || IsDisposed)
                return;

            _syncing = true;
            try { ModelItems = ids; }
            finally { _syncing = false; }
            await SwitchMissingGeminiModelAsync(ids);
            if (!IsDisposed)
                SetModelText(Snapshot.Model);
        }
        catch (OperationCanceledException)
        {
        }
        catch (AiException ex)
        {
            if (!IsDisposed)
                AiIntegrationsViewModel.PublishError(_notifications, ex, "Couldn't load the model list");
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
                _notifications?.Publish(NotificationSeverity.Error, ex.NotificationMessage("Couldn't load the model list"), ex.NotificationDetails());
        }
        finally
        {
            // Only the newest load clears the flag, and each load disposes the source it created.
            if (ReferenceEquals(_modelsCts, cts))
            {
                _modelsCts = null;
                if (!IsDisposed)
                    IsLoadingModels = false;
            }
            cts.Dispose();
        }
    }

    /// <summary>A Gemini profile whose saved model isn't in the list (or has none) takes the best
    /// replacement, at most once every five minutes; Custom profiles never switch on their own.</summary>
    private async Task SwitchMissingGeminiModelAsync(IReadOnlyList<string> ids)
    {
        try
        {
            var profile = Snapshot;
            if (profile.Kind != AiServiceKind.Gemini)
                return;

            var saved = profile.Model;
            var blank = string.IsNullOrWhiteSpace(saved);
            if (!blank && ids.Contains(saved))
                return;

            var pick = GeminiModelSelector.PickReplacement(ids, blank ? null : saved);
            if (pick is null || !_catalog.TryClaimAutoSwitch(profile.Id))
                return;

            await CommitAsync(p => p with { Model = pick });
            if (IsDisposed)
                return;
            SetModelText(pick);
            if (!blank)
                ModelNote = $"'{saved}' is no longer available — switched to '{pick}'.";
        }
        catch (Exception ex)
        {
            Report(ex, "Couldn't switch the model");
        }
    }

    // Property changes cannot await, so a save runs unobserved; a failure is reported, never thrown.
    private void Run(Func<Task> work) => _ = ObserveAsync(work);

    private async Task ObserveAsync(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            Report(ex, "Couldn't save the AI integration");
        }
    }

    private void Report(Exception ex, string what)
    {
        AppLog.Write(AppLogLevel.Warn, "ai", what, ex);
        _notifications?.Publish(NotificationSeverity.Error, ex.NotificationMessage(what), ex.NotificationDetails());
    }

    private static int EffortIndex(AiServiceProfile p) =>
        Math.Max(0, AiReasoningEffort.Levels.ToList().FindIndex(l => l == p.ReasoningEffort));

    private static int DiffLimitIndexOf(AiServiceProfile p) => AiDiffCap.TokenSteps.ToList().IndexOf(AiDiffCap.NearestStep(p.DiffCapTokens));

    private static int TemplateIndexOf(AiServiceProfile p) => p.Kind == AiServiceKind.Gemini ? 1 : 0;

    protected override void OnDisposed()
    {
        _modelsCts?.Cancel();
        _modelsCts = null;
    }
}
