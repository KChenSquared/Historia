namespace Historia.Tests;

[Trait("tier", "unit")]
public class AiServiceTemplatesTests
{
    [Fact]
    public void New_custom_profile_is_blank()
    {
        var now = new DateTime(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc);
        var p = AiServiceTemplates.NewCustom(now);

        Assert.Equal(AiServiceTemplates.DefaultName, p.Name);
        Assert.Equal(AiServiceKind.Custom, p.Kind);
        Assert.Equal("", p.Server);
        Assert.Equal("", p.Model);
        Assert.Equal("", p.ApiKey);
        Assert.Equal(now, p.CreatedUtc);
        Assert.False(string.IsNullOrEmpty(p.Id));
    }

    [Fact]
    public void Gemini_fills_server_and_model_and_never_touches_the_key()
    {
        var p = AiServiceTemplates.NewCustom(DateTime.UtcNow) with
        {
            ApiKey = "GEMINI_KEY",
            ReadApiKeyFromEnv = true,
            Server = "https://example.test/v1",
            Model = "old",
            AutoFetchModels = false,
        };

        var g = AiServiceTemplates.ApplyGemini(p);

        Assert.Equal(AiServiceKind.Gemini, g.Kind);
        Assert.Equal(AiServiceTemplates.GeminiServer, g.Server);
        Assert.Equal("", g.Model);
        Assert.True(g.AutoFetchModels);
        Assert.Equal("GEMINI_KEY", g.ApiKey);
        Assert.True(g.ReadApiKeyFromEnv);
        Assert.Equal(p.Id, g.Id);
    }

    [Theory]
    [InlineData("", "Gemini")]
    [InlineData("Unnamed Service", "Gemini")]
    [InlineData("Work", "Work")]
    public void Gemini_renames_only_a_blank_or_default_name(string name, string expected)
    {
        var p = AiServiceTemplates.NewCustom(DateTime.UtcNow) with { Name = name };
        Assert.Equal(expected, AiServiceTemplates.ApplyGemini(p).Name);
    }

    [Fact]
    public void Custom_switches_the_protocol_and_keeps_fields()
    {
        var p = AiServiceTemplates.ApplyGemini(AiServiceTemplates.NewCustom(DateTime.UtcNow)) with { Model = "gemini-2.5-flash" };

        var c = AiServiceTemplates.ApplyCustom(p);

        Assert.Equal(AiServiceKind.Custom, c.Kind);
        Assert.Equal(p with { Kind = AiServiceKind.Custom }, c);
    }

    [Theory]
    [InlineData("", "", AiServiceKind.Gemini, false)]
    [InlineData("  ", "  ", AiServiceKind.Gemini, false)]
    [InlineData(AiServiceTemplates.GeminiServer, "", AiServiceKind.Gemini, false)]
    [InlineData("https://example.test/v1", "", AiServiceKind.Gemini, true)]
    [InlineData("", "gpt-4o", AiServiceKind.Gemini, true)]
    [InlineData(AiServiceTemplates.GeminiServer, "gemini-2.5-flash", AiServiceKind.Gemini, true)]
    [InlineData("https://example.test/v1", "gpt-4o", AiServiceKind.Custom, false)]
    public void WouldOverwrite_truth_table(string server, string model, AiServiceKind kind, bool expected)
    {
        var p = AiServiceTemplates.NewCustom(DateTime.UtcNow) with { Server = server, Model = model };
        Assert.Equal(expected, AiServiceTemplates.WouldOverwrite(p, kind));
    }

    [Theory]
    [InlineData(5_000, 20_000L)]
    [InlineData(25_000, 100_000L)]
    [InlineData(100_000, 400_000L)]
    [InlineData(0, null)]
    [InlineData(-1, null)]
    public void Diff_cap_bytes(int tokens, long? expected) => Assert.Equal(expected, AiDiffCap.ToBytes(tokens));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-5, 0)]
    [InlineData(5_000, 5_000)]
    [InlineData(7_000, 5_000)]
    [InlineData(8_000, 10_000)]
    [InlineData(30_000, 25_000)]
    [InlineData(1_000_000, 100_000)]
    public void Diff_cap_snaps_to_the_nearest_choice(int tokens, int expected) => Assert.Equal(expected, AiDiffCap.NearestStep(tokens));

    [Fact]
    public void New_profiles_default_to_25000_tokens() =>
        Assert.Equal(25_000, AiServiceTemplates.NewCustom(DateTime.UtcNow).DiffCapTokens);
}
