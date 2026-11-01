namespace Historia.ViewModels;

public sealed partial class StagingPanelViewModel
{
    private const string WandToolTip = "Generate Commit Message";
    private const string ModelPlaceholderIdle = "Select a Model";

    private readonly IAiProfileService? _aiProfiles;
    private readonly IAiIntegrationsPanel? _aiPanel;
    private readonly ICommitMessageGenerator? _aiGenerator;
    private readonly IAiModelCatalog? _catalog;

    private CancellationTokenSource? _generateCts;
    private CancellationTokenSource? _modelLoadCts;
    private bool _syncingPicker;
    private bool _loadingModels;

    /// <summary>True when the AI services are available, which shows the wand, the settings button and the model picker.</summary>
    public bool HasAiServices => _aiProfiles is not null && _aiPanel is not null && _aiGenerator is not null && _catalog is not null;

    /// <summary>The model list request in flight, or a completed task when none is.</summary>
    public Task ModelLoad { get; private set; } = Task.CompletedTask;

    /// <summary>True while a commit message is being generated.</summary>
    [ObservableProperty]
    public partial bool IsGenerating { get; private set; }

    /// <summary>True when an AI profile is active or a generation can be cancelled.</summary>
    [ObservableProperty]
    public partial bool IsWandEnabled { get; private set; }

    /// <summary>True when an AI profile is active, which turns the wand green.</summary>
    [ObservableProperty]
    public partial bool IsWandActive { get; private set; }

    [ObservableProperty]
    public partial string WandTip { get; private set; } = WandToolTip;

    /// <summary>The models of the active profile; null when no profile is active.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string>? ModelItems { get; private set; }

    /// <summary>The picked model; choosing one saves it to the active profile.</summary>
    [ObservableProperty]
    public partial string? SelectedModel { get; set; }

    [ObservableProperty]
    public partial bool IsModelPickerEnabled { get; private set; }

    /// <summary>Reads "Select a Model" while idle and "Loading…" while the list loads.</summary>
    [ObservableProperty]
    public partial string ModelPlaceholder { get; private set; } = ModelPlaceholderIdle;

    [ObservableProperty]
    public partial string? ModelTip { get; private set; } = "Set up an AI integration to choose a model";

    private void InitializeAi()
    {
        if (!HasAiServices)
            return;

        Subscribe(() => _catalog!.Changed += OnCatalogChanged, () => _catalog!.Changed -= OnCatalogChanged);
        Subscribe(() => _aiProfiles!.Changed += OnAiProfilesChanged, () => _aiProfiles!.Changed -= OnAiProfilesChanged);
        RefreshAiButtons();
    }

    private void OnCatalogChanged(object? sender, string profileId) => _ui.Post(RefreshModelPicker);

    private void OnAiProfilesChanged(object? sender, EventArgs e) => _ui.Post(RefreshAiButtons);

    private void RefreshAiButtons()
    {
        if (IsDisposed || !HasAiServices)
            return;

        var enabled = _aiProfiles!.ActiveProfile is not null;
        var generating = _generateCts is not null;
        IsWandEnabled = enabled || generating;
        IsWandActive = enabled;
        WandTip = generating ? "Generating… click to cancel" : WandToolTip;

        RefreshModelPicker();
    }

    private void RefreshModelPicker()
    {
        if (IsDisposed || !HasAiServices)
            return;

        _syncingPicker = true;
        try
        {
            var active = _aiProfiles!.ActiveProfile;
            if (active is null)
            {
                IsModelPickerEnabled = false;
                ModelItems = null;
                SelectedModel = null;
                ModelTip = "Set up an AI integration to choose a model";
                return;
            }

            IsModelPickerEnabled = true;
            var cached = _catalog!.Get(active);
            IReadOnlyList<string> items;
            if (cached is not null)
            {
                items = cached;
            }
            else if (!string.IsNullOrWhiteSpace(active.Model))
            {
                items = [active.Model];
            }
            else
            {
                items = [];
            }

            if (!(ModelItems ?? []).SequenceEqual(items))
                ModelItems = items;

            var target = items.Contains(active.Model) ? active.Model : null;
            if (!string.Equals(SelectedModel, target, StringComparison.Ordinal))
                SelectedModel = target;

            var label = _aiProfiles.DisplayLabel(active);
            var tip = $"Model for {label}";
            if (_loadingModels)
            {
                tip += "\nLoading models…";
            }
            else if (!active.AutoFetchModels && cached is null)
            {
                tip += "\nPress refresh in AI Integrations to load models";
            }
            ModelTip = tip;
        }
        finally
        {
            _syncingPicker = false;
        }
    }

    partial void OnSelectedModelChanged(string? value)
    {
        if (_syncingPicker || IsDisposed || _aiProfiles?.ActiveProfile is not { } active)
            return;

        if (value is not null && value != active.Model)
            _ = SaveModelAsync(active.Id, value);
    }

    private async Task SaveModelAsync(string profileId, string model)
    {
        try
        {
            await _aiProfiles!.UpdateAsync(profileId, p => p with { Model = model }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            AppLog.Write(AppLogLevel.Warn, "Staging", "Saving the picked model failed.", ex);
        }
    }

    /// <summary>Opens the AI integrations panel on the active profile.</summary>
    [RelayCommand]
    private void OpenAiSettings()
    {
        if (HasAiServices)
            _aiPanel!.Open(_aiProfiles!.ActiveProfile?.Id);
    }

    /// <summary>Loads the active profile's model list the first time the picker opens, when it fetches on its own.</summary>
    [RelayCommand]
    private void OpenModelPicker()
    {
        if (IsDisposed || _aiProfiles?.ActiveProfile is not { } active)
            return;

        if (!active.AutoFetchModels || _catalog?.Get(active) is not null || !ModelLoad.IsCompleted)
            return;

        if (string.IsNullOrWhiteSpace(active.Server))
        {
            _notifications.Publish(NotificationSeverity.Warning, "Set the Server before loading models.");
            return;
        }

        ModelLoad = LoadModelsAsync(active);
    }

    private async Task LoadModelsAsync(AiServiceProfile profile)
    {
        _modelLoadCts?.Cancel();
        _modelLoadCts?.Dispose();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_ctx.Lifetime);
        _modelLoadCts = cts;

        // The placeholder shows only while no model is selected; the tooltip covers the rest.
        _loadingModels = true;
        ModelPlaceholder = "Loading…";
        RefreshModelPicker();

        try
        {
            if (_catalog is not null)
                await _catalog.LoadAsync(profile, cts.Token);
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
                _notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage("Couldn't load the model list"), ex.NotificationDetails());
        }
        finally
        {
            if (!IsDisposed && ReferenceEquals(_modelLoadCts, cts))
            {
                _loadingModels = false;
                ModelPlaceholder = ModelPlaceholderIdle;
                RefreshModelPicker();
            }
        }
    }

    /// <summary>Writes a commit message for the staged changes into the box. A second call while
    /// one is running cancels it. The box is read-only while the request runs, and the draft is
    /// replaced only after the request succeeds.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task GenerateAsync()
    {
        if (_generateCts is { } running)
        {
            running.Cancel();
            return;
        }
        var profile = _aiProfiles?.ActiveProfile;
        if (profile is null || _aiPanel is null || _aiGenerator is null)
            return;

        try
        {
            if (_status.Staged.Count == 0 && !IsAmend)
            {
                _notifications.Publish(NotificationSeverity.Warning, "Stage changes before generating a commit message.");
                return;
            }

            var keyless = profile.Kind == AiServiceKind.Custom && !profile.ReadApiKeyFromEnv && string.IsNullOrWhiteSpace(profile.ApiKey);
            if (!_aiProfiles!.HasApiKey(profile) && !keyless)
            {
                _aiPanel.Open(profile.Id);
                _notifications.Publish(NotificationSeverity.Warning,
                    $"{_aiProfiles.DisplayLabel(profile)} has no API key. Add one in AI Integrations.");
                return;
            }

            var draft = MessageText ?? "";
            if (!string.IsNullOrWhiteSpace(draft)
                && !await ConfirmReplaceDraftAsync("an AI-generated message", draft.Length))
                return;
            if (IsDisposed || _generateCts is not null)
                return;

            await GenerateIntoBoxAsync(profile);
        }
        catch (Exception ex)
        {
            AppLog.Write(AppLogLevel.Warn, "Staging", "Generating the commit message failed.", ex);
        }
    }

    private async Task GenerateIntoBoxAsync(AiServiceProfile profile)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_ctx.Lifetime);
        _generateCts = cts;
        IsGenerating = true;
        IsMessageReadOnly = true;
        RefreshAiButtons();
        try
        {
            var result = await _aiGenerator!.GenerateAsync(_ctx, IsAmend, cts.Token);
            if (IsDisposed || cts.IsCancellationRequested)
                return;

            MessageText = result.Message;
            var label = _aiProfiles!.DisplayLabel(_aiProfiles.ActiveProfile ?? profile);
            if (result.ReplacedModel is not null && result.NewModel is not null)
                _notifications.Publish(NotificationSeverity.Warning,
                    $"'{result.ReplacedModel}' is no longer available. '{label}' now uses '{result.NewModel}'.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (AiException ex)
        {
            if (!IsDisposed)
                AiIntegrationsViewModel.PublishError(_notifications, ex);
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
                _notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage("Generating the commit message"), ex.NotificationDetails());
        }
        finally
        {
            _generateCts = null;
            cts.Dispose();
            IsGenerating = false;
            if (!IsDisposed)
            {
                IsMessageReadOnly = false;
                RefreshAiButtons();
            }
        }
    }
}
