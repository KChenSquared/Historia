namespace Historia.Tests;

/// <summary>
/// The model-list load of <see cref="AiProfileEditorViewModel"/>: a newer load cancels the one
/// before it, only the newest load clears the loading flag, and disposing the editor mid-load
/// cancels the load without an error. The catalog and profile service are local fakes.
/// </summary>
[Trait("tier", "unit")]
public class AiProfileEditorViewModelTests
{
    private sealed class PendingCatalog : IAiModelCatalog
    {
        public List<(TaskCompletionSource<IReadOnlyList<string>> Source, CancellationToken Token)> Loads { get; } = [];

        public event EventHandler<string>? Changed
        {
            add { }
            remove { }
        }

        public IReadOnlyList<string>? Get(AiServiceProfile profile) => null;

        public Task<IReadOnlyList<string>> LoadAsync(AiServiceProfile profile, CancellationToken ct = default)
        {
            var source = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
            ct.Register(() => source.TrySetCanceled(ct));
            Loads.Add((source, ct));
            return source.Task;
        }

        public void Remember(AiServiceProfile profile, IReadOnlyList<string> ids)
        {
        }

        public bool TryClaimAutoSwitch(string profileId) => false;
    }

    private sealed class StubProfiles(AiServiceProfile profile) : IAiProfileService
    {
        public IReadOnlyList<AiServiceProfile> Profiles { get; } = [profile];
        public AiServiceProfile? ActiveProfile => null;

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public string DisplayLabel(AiServiceProfile p) => p.Name;
        public Task<AiServiceProfile> AddAsync(CancellationToken ct = default) => Task.FromResult(profile);
        public Task UpdateAsync(string id, Func<AiServiceProfile, AiServiceProfile> mutate, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAiServiceAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetActiveAsync(string? id, CancellationToken ct = default) => Task.CompletedTask;
        public string? ResolveApiKey(AiServiceProfile p) => null;
        public bool HasApiKey(AiServiceProfile p) => false;
    }

    private static (AiProfileEditorViewModel Editor, PendingCatalog Catalog) NewEditor()
    {
        var profile = new AiServiceProfile { Name = "Local", Server = "http://localhost:1234/v1", Model = "model-a" };
        var catalog = new PendingCatalog();
        var editor = new AiProfileEditorViewModel(
            profile, new StubProfiles(profile), catalog, new FakeDialogService(), new FakeNotificationService(),
            committed: () => { }, toggleActive: _ => Task.CompletedTask);
        return (editor, catalog);
    }

    [Fact]
    public async Task Reload_cancels_the_previous_load()
    {
        var (editor, catalog) = NewEditor();

        var first = editor.FetchModelsCommand.ExecuteAsync(null);
        Assert.False(catalog.Loads[0].Token.IsCancellationRequested);

        var second = editor.FetchModelsCommand.ExecuteAsync(null);

        Assert.True(catalog.Loads[0].Token.IsCancellationRequested);
        Assert.False(catalog.Loads[1].Token.IsCancellationRequested);

        catalog.Loads[1].Source.SetResult(["model-a"]);
        await Task.WhenAll(first, second);
    }

    [Fact]
    public async Task Latest_load_keeps_the_loading_flag_until_it_ends()
    {
        var (editor, catalog) = NewEditor();

        var first = editor.FetchModelsCommand.ExecuteAsync(null);
        var second = editor.FetchModelsCommand.ExecuteAsync(null);
        await first;

        Assert.True(editor.IsLoadingModels);

        catalog.Loads[1].Source.SetResult(["model-a"]);
        await second;

        Assert.False(editor.IsLoadingModels);
    }

    [Fact]
    public async Task Dispose_during_load_does_not_throw()
    {
        var (editor, catalog) = NewEditor();

        var load = editor.FetchModelsCommand.ExecuteAsync(null);
        editor.Dispose();

        Assert.True(catalog.Loads[0].Token.IsCancellationRequested);
        await load;
    }
}
