namespace Historia.Tests;

[Trait("tier", "unit")]
public class AiSettingsPersistenceTests : IDisposable
{
    private readonly StateDir _state = new();

    public void Dispose() => _state.Dispose();

    [Fact]
    public async Task Profiles_and_active_id_round_trip_through_the_settings_file()
    {
        var first = new AiServiceProfile
        {
            Name = "Gemini",
            Kind = AiServiceKind.Gemini,
            Server = AiServiceTemplates.GeminiServer,
            Model = "gemini-2.5-flash",
            ApiKey = "secret-one",
            ReasoningEffort = "low",
            AdditionalPrompt = "- keep it short",
            ExtraHeaders = "X-Team: core",
            DiffCapTokens = 50_000,
            CreatedUtc = new DateTime(2026, 10, 6, 1, 0, 0, DateTimeKind.Utc),
        };
        var second = new AiServiceProfile
        {
            Name = "Work",
            Server = "https://example.test/v1",
            ReadApiKeyFromEnv = true,
            ApiKey = "WORK_KEY_VAR",
            AutoFetchModels = false,
        };

        var svc = SettingsService.Load();
        await svc.UpdateAsync(s => s with { AiServices = [first, second], ActiveAiServiceId = second.Id });
        await svc.FlushAsync();

        var reloaded = SettingsService.Load();
        Assert.Equal([first, second], reloaded.Current.AiServices);
        Assert.Equal(second.Id, reloaded.Current.ActiveAiServiceId);
    }

    [Fact]
    public async Task DiffCapTokens_round_trips()
    {
        var profile = new AiServiceProfile { Name = "A", DiffCapTokens = 0 };
        var svc = SettingsService.Load();
        await svc.UpdateAsync(s => s with { AiServices = [profile] });
        await svc.FlushAsync();

        Assert.Equal(0, SettingsService.Load().Current.AiServices.Single().DiffCapTokens);
    }

    [Fact]
    public void Old_DiffCapUnits_loads_with_default_tokens()
    {
        File.WriteAllText(AppPaths.SettingsFile,
            "{ \"schemaVersion\": 21, \"aiServices\": [ { \"id\": \"old\", \"name\": \"Old\", \"diffCapUnits\": 3 } ] }");

        var loaded = SettingsService.Load().Current.AiServices.Single();

        Assert.Equal("Old", loaded.Name);
        Assert.Equal(AiDiffCap.DefaultTokens, loaded.DiffCapTokens);
    }

    [Fact]
    public void An_old_settings_file_without_AI_fields_loads_empty()
    {
        File.WriteAllText(AppPaths.SettingsFile, "{ \"schemaVersion\": 22, \"appTheme\": \"dark\" }");

        var svc = SettingsService.Load();

        Assert.Empty(svc.Current.AiServices);
        Assert.Null(svc.Current.ActiveAiServiceId);
    }
}
