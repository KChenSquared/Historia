namespace Historia.Tests;

[Trait("tier", "unit")]
public class CommitMessageGeneratorTests
{
    private sealed class FakeClient : IAiChatClient
    {
        public List<AiChatRequest> Completes { get; } = [];
        public List<AiChatRequest> Lists { get; } = [];
        public Queue<Func<AiChatRequest, string>> CompleteScript { get; } = new();
        public Func<IReadOnlyList<string>> ListResult { get; set; } = () => ["gemini-2.5-flash"];

        public Task<IReadOnlyList<string>> ListModelsAsync(AiChatRequest request, CancellationToken ct)
        {
            Lists.Add(request);
            return Task.FromResult(ListResult());
        }

        public Task<string> CompleteAsync(AiChatRequest request, CancellationToken ct)
        {
            Completes.Add(request);
            return Task.FromResult(CompleteScript.Count > 0 ? CompleteScript.Dequeue()(request) : "feat: generated");
        }
    }

    private sealed class FakeFactory(FakeClient client) : IAiClientFactory
    {
        public int Calls { get; private set; }

        public IAiChatClient For(AiServiceKind kind)
        {
            Calls++;
            return client;
        }
    }

    /// <summary>An <see cref="IGitGateway"/> that answers only the staged-patch read.</summary>
    public class GatewayProxy : DispatchProxy
    {
        public List<bool> Requests { get; } = [];
        public IReadOnlyList<(string Path, string Patch)> Patches { get; set; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IGitGateway.GetStagedPatchesAsync))
            {
                Requests.Add((bool)args![0]!);
                return Task.FromResult(Patches);
            }
            throw new NotSupportedException(targetMethod?.Name);
        }
    }

    private sealed class Rig
    {
        public FakeSettingsService Settings { get; } = new();
        public AiProfileService Profiles { get; }
        public FakeClient Client { get; } = new();
        public FakeFactory Factory { get; }
        public CommitMessageGenerator Generator { get; }
        public GatewayProxy Gateway { get; }
        public IGitGateway GatewayInterface { get; }

        public Rig()
        {
            Profiles = new AiProfileService(Settings);
            Factory = new FakeFactory(Client);
            Generator = new CommitMessageGenerator(Profiles, Factory);
            GatewayInterface = DispatchProxy.Create<IGitGateway, GatewayProxy>();
            Gateway = (GatewayProxy)(object)GatewayInterface;
            Gateway.Patches = [("a.txt", "diff --git a/a.txt b/a.txt\n--- a/a.txt\n+++ b/a.txt\n@@ -1 +1 @@\n-x\n+y\n")];
        }

        public async Task<AiServiceProfile> Activate(Func<AiServiceProfile, AiServiceProfile> configure)
        {
            var added = await Profiles.AddAsync();
            await Profiles.UpdateAsync(added.Id, configure);
            await Profiles.SetActiveAsync(added.Id);
            return Profiles.Profiles.Single();
        }

        public Task<AiServiceProfile> ActivateGemini(string model = "gemini-2.5-flash", int capTokens = 0) =>
            Activate(p => AiServiceTemplates.ApplyGemini(p) with { ApiKey = "key", Model = model, DiffCapTokens = capTokens });

        public Task<AiServiceProfile> ActivateCustom(string model = "gpt", string key = "key") =>
            Activate(p => p with { Server = "https://ai.example.test/v1", Model = model, ApiKey = key });

        public Task<CommitMessageResult> Run(bool amend = false, Historia.Models.RepositoryStatus? status = null) =>
            Generator.GenerateAsync(GatewayInterface, status ?? Status("a.txt"), "main", amend, default);

        public string ModelOnProfile => Profiles.Profiles.Single().Model;
    }

    private static Historia.Models.RepositoryStatus Status(params string[] staged) => Historia.Models.RepositoryStatus.Empty with
    {
        Staged = staged.Select(p => new FileStatusEntry { Path = p, Kind = FileChangeKind.Modified, IsBinary = false, IsSubmodule = false }).ToList(),
    };

    private static AiException ModelGone() => new(AiErrorKind.ModelNotFound, "gone", "details");

    [Fact]
    public async Task No_active_profile_fails_before_any_client_call()
    {
        var rig = new Rig();

        var ex = await Assert.ThrowsAsync<AiException>(() => rig.Run());

        Assert.Equal(AiErrorKind.NotConfigured, ex.Kind);
        Assert.Equal(0, rig.Factory.Calls);
        Assert.Empty(rig.Gateway.Requests);
    }

    [Fact]
    public async Task A_missing_key_fails_before_any_client_call()
    {
        var rig = new Rig();
        await rig.Activate(p => AiServiceTemplates.ApplyGemini(p));

        var ex = await Assert.ThrowsAsync<AiException>(() => rig.Run());

        Assert.Equal(AiErrorKind.MissingApiKey, ex.Kind);
        Assert.Equal(0, rig.Factory.Calls);
    }

    [Fact]
    public async Task An_env_key_with_an_unset_variable_is_a_missing_key()
    {
        var rig = new Rig();
        await rig.Activate(p => p with
        {
            Server = "https://ai.example.test/v1",
            Model = "m",
            ReadApiKeyFromEnv = true,
            ApiKey = $"HISTORIA_UNSET_{Guid.NewGuid():N}",
        });

        var ex = await Assert.ThrowsAsync<AiException>(() => rig.Run());

        Assert.Equal(AiErrorKind.MissingApiKey, ex.Kind);
    }

    [Fact]
    public async Task A_custom_profile_with_no_model_fails_before_any_client_call()
    {
        var rig = new Rig();
        await rig.ActivateCustom(model: "");

        var ex = await Assert.ThrowsAsync<AiException>(() => rig.Run());

        Assert.Equal(AiErrorKind.NoModel, ex.Kind);
        Assert.Equal(0, rig.Factory.Calls);
        Assert.Empty(rig.Client.Lists);
    }

    [Fact]
    public async Task A_keyless_custom_server_is_allowed()
    {
        var rig = new Rig();
        await rig.ActivateCustom(key: "");

        var result = await rig.Run();

        Assert.Equal("feat: generated", result.Message);
        Assert.Null(rig.Client.Completes.Single().ApiKey);
    }

    [Fact]
    public async Task Nothing_staged_is_reported_without_calling_the_service()
    {
        var rig = new Rig();
        await rig.ActivateCustom();
        rig.Gateway.Patches = [];

        var ex = await Assert.ThrowsAsync<AiException>(() => rig.Run());

        Assert.Equal(AiErrorKind.NothingStaged, ex.Kind);
        Assert.Empty(rig.Client.Completes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Amend_decides_whether_the_diff_is_taken_against_the_head_parent(bool amend)
    {
        var rig = new Rig();
        await rig.ActivateCustom();

        await rig.Run(amend);

        Assert.Equal([amend], rig.Gateway.Requests);
    }

    [Fact]
    public async Task The_profile_cap_reaches_the_prompt()
    {
        var rig = new Rig();
        await rig.ActivateGemini(capTokens: 5_000);
        rig.Gateway.Patches = [("a.txt", new string('x', 25_000)), ("b.txt", "small")];

        await rig.Run(status: Status("a.txt", "b.txt"));

        var prompt = rig.Client.Completes.Single().Prompt;
        Assert.Contains(CommitMessagePrompt.OmittedDiffNote, prompt);
        Assert.DoesNotContain(new string('x', 25_000), prompt);
        Assert.Contains("M\tb.txt", prompt);
    }

    [Fact]
    public async Task Without_a_cap_the_whole_diff_is_sent()
    {
        var rig = new Rig();
        await rig.ActivateGemini(capTokens: 0);
        rig.Gateway.Patches = [("a.txt", new string('x', 300_000))];

        await rig.Run();

        Assert.DoesNotContain(CommitMessagePrompt.OmittedDiffNote, rig.Client.Completes.Single().Prompt);
    }

    [Fact]
    public async Task The_reply_is_cleaned()
    {
        var rig = new Rig();
        await rig.ActivateCustom();
        rig.Client.CompleteScript.Enqueue(_ => "```\nfix: tidy\n\n- a\n```");

        var result = await rig.Run();

        Assert.Equal("fix: tidy\n\n- a", result.Message);
        Assert.Null(result.ReplacedModel);
        Assert.Null(result.NewModel);
    }

    [Fact]
    public async Task An_amend_lists_every_changed_file_from_the_patches()
    {
        var rig = new Rig();
        await rig.ActivateCustom();
        rig.Gateway.Patches =
        [
            ("added.txt", "diff --git a/added.txt b/added.txt\nnew file mode 100644\n--- /dev/null\n+++ b/added.txt\n@@ -0,0 +1 @@\n+x\n"),
            ("gone.txt", "diff --git a/gone.txt b/gone.txt\ndeleted file mode 100644\n--- a/gone.txt\n+++ /dev/null\n@@ -1 +0,0 @@\n-x\n"),
            ("new.txt", "diff --git a/old.txt b/new.txt\nsimilarity index 100%\nrename from old.txt\nrename to new.txt\n"),
        ];

        await rig.Run(amend: true, status: Status());

        var prompt = rig.Client.Completes.Single().Prompt;
        Assert.Contains("A\tadded.txt", prompt);
        Assert.Contains("D\tgone.txt", prompt);
        Assert.Contains("R\told.txt -> new.txt", prompt);
    }

    [Fact]
    public async Task Constructing_the_generator_makes_no_client_call()
    {
        var rig = new Rig();
        await rig.ActivateGemini();

        _ = new CommitMessageGenerator(rig.Profiles, rig.Factory);

        Assert.Equal(0, rig.Factory.Calls);
        Assert.Empty(rig.Client.Lists);
        Assert.Empty(rig.Client.Completes);
    }

    // ── Gemini model fallback ──

    [Fact]
    public async Task A_gone_model_is_replaced_saved_and_retried_once()
    {
        var rig = new Rig();
        await rig.ActivateGemini(model: "gemini-1.0-pro");
        rig.Client.ListResult = () => ["gemini-1.0-pro", "gemini-2.5-flash", "gemini-3-flash-preview"];
        rig.Client.CompleteScript.Enqueue(_ => throw ModelGone());
        rig.Client.CompleteScript.Enqueue(_ => "feat: after switch");

        var result = await rig.Run();

        Assert.Equal("feat: after switch", result.Message);
        Assert.Equal("gemini-1.0-pro", result.ReplacedModel);
        Assert.Equal("gemini-2.5-flash", result.NewModel);
        Assert.Equal("gemini-2.5-flash", rig.ModelOnProfile);
        Assert.Single(rig.Client.Lists);
        Assert.Equal(["gemini-1.0-pro", "gemini-2.5-flash"], rig.Client.Completes.Select(c => c.Model).ToArray());
    }

    [Fact]
    public async Task A_second_missing_model_is_unavailable_with_no_third_call()
    {
        var rig = new Rig();
        await rig.ActivateGemini(model: "gemini-1.0-pro");
        rig.Client.CompleteScript.Enqueue(_ => throw ModelGone());
        rig.Client.CompleteScript.Enqueue(_ => throw ModelGone());

        var ex = await Assert.ThrowsAsync<AiException>(() => rig.Run());

        Assert.Equal(AiErrorKind.ModelUnavailable, ex.Kind);
        Assert.Contains("gemini-1.0-pro", ex.Message);
        Assert.Equal(2, rig.Client.Completes.Count);
        Assert.Single(rig.Client.Lists);
    }

    [Fact]
    public async Task A_list_failure_is_unavailable_and_leaves_the_profile_alone()
    {
        var rig = new Rig();
        await rig.ActivateGemini(model: "gemini-1.0-pro");
        rig.Client.CompleteScript.Enqueue(_ => throw ModelGone());
        rig.Client.ListResult = () => throw new AiException(AiErrorKind.Network, "offline");

        var ex = await Assert.ThrowsAsync<AiException>(() => rig.Run());

        Assert.Equal(AiErrorKind.ModelUnavailable, ex.Kind);
        Assert.Equal("gemini-1.0-pro", rig.ModelOnProfile);
        Assert.Single(rig.Client.Completes);
    }

    [Fact]
    public async Task An_empty_replacement_is_unavailable_and_leaves_the_profile_alone()
    {
        var rig = new Rig();
        await rig.ActivateGemini(model: "gemini-1.0-pro");
        rig.Client.CompleteScript.Enqueue(_ => throw ModelGone());
        rig.Client.ListResult = () => ["gemini-1.0-pro"];

        var ex = await Assert.ThrowsAsync<AiException>(() => rig.Run());

        Assert.Equal(AiErrorKind.ModelUnavailable, ex.Kind);
        Assert.Equal("gemini-1.0-pro", rig.ModelOnProfile);
    }

    [Fact]
    public async Task The_switch_is_saved_even_when_the_retry_fails_another_way()
    {
        var rig = new Rig();
        await rig.ActivateGemini(model: "gemini-1.0-pro");
        rig.Client.ListResult = () => ["gemini-2.5-flash"];
        rig.Client.CompleteScript.Enqueue(_ => throw ModelGone());
        rig.Client.CompleteScript.Enqueue(_ => throw new AiException(AiErrorKind.Server, "boom"));

        var ex = await Assert.ThrowsAsync<AiException>(() => rig.Run());

        Assert.Equal(AiErrorKind.Server, ex.Kind);
        Assert.Equal("gemini-2.5-flash", rig.ModelOnProfile);
    }

    [Fact]
    public async Task A_blank_gemini_model_picks_one_before_the_first_request()
    {
        var rig = new Rig();
        await rig.ActivateGemini(model: "");
        rig.Client.ListResult = () => ["gemini-2.0-flash", "gemini-2.5-flash"];

        var result = await rig.Run();

        Assert.Null(result.ReplacedModel);
        Assert.Equal("gemini-2.5-flash", result.NewModel);
        Assert.Equal("gemini-2.5-flash", rig.Client.Completes.Single().Model);
        Assert.Equal("gemini-2.5-flash", rig.ModelOnProfile);
    }

    [Theory]
    [InlineData(AiErrorKind.InvalidServer)]
    [InlineData(AiErrorKind.Unauthorized)]
    [InlineData(AiErrorKind.Network)]
    public async Task A_blank_model_whose_list_fails_reports_the_list_failure(AiErrorKind kind)
    {
        var rig = new Rig();
        await rig.ActivateGemini(model: "");
        rig.Client.ListResult = () => throw new AiException(kind, "the real problem");

        var ex = await Assert.ThrowsAsync<AiException>(() => rig.Run());

        Assert.Equal(kind, ex.Kind);
        Assert.Equal("the real problem", ex.Message);
        Assert.Empty(rig.Client.Completes);
        Assert.Equal("", rig.ModelOnProfile);
    }

    [Fact]
    public async Task A_model_that_was_just_picked_and_is_still_missing_does_not_loop()
    {
        var rig = new Rig();
        await rig.ActivateGemini(model: "");
        rig.Client.ListResult = () => ["gemini-2.5-flash"];
        rig.Client.CompleteScript.Enqueue(_ => throw ModelGone());

        var ex = await Assert.ThrowsAsync<AiException>(() => rig.Run());

        Assert.Equal(AiErrorKind.ModelUnavailable, ex.Kind);
        Assert.Single(rig.Client.Completes);
        Assert.Single(rig.Client.Lists);
    }

    [Fact]
    public async Task A_custom_profile_never_lists_models_or_switches()
    {
        var rig = new Rig();
        await rig.ActivateCustom(model: "gpt-x");
        rig.Client.CompleteScript.Enqueue(_ => throw new AiException(AiErrorKind.NotFound, "no"));

        var ex = await Assert.ThrowsAsync<AiException>(() => rig.Run());

        Assert.Equal(AiErrorKind.NotFound, ex.Kind);
        Assert.Empty(rig.Client.Lists);
        Assert.Equal("gpt-x", rig.ModelOnProfile);
    }

    [Fact]
    public async Task A_custom_model_not_found_never_switches()
    {
        var rig = new Rig();
        await rig.ActivateCustom(model: "gpt-x");
        rig.Client.CompleteScript.Enqueue(_ => throw ModelGone());

        var ex = await Assert.ThrowsAsync<AiException>(() => rig.Run());

        Assert.Equal(AiErrorKind.ModelNotFound, ex.Kind);
        Assert.Empty(rig.Client.Lists);
    }

    [Fact]
    public async Task A_model_switch_remembers_the_fetched_list_in_the_catalog()
    {
        var rig = new Rig();
        var catalog = new AiModelCatalog(rig.Profiles, rig.Factory);
        var generator = new CommitMessageGenerator(rig.Profiles, rig.Factory, catalog);
        await rig.ActivateGemini(model: "gemini-1.0-pro");
        rig.Client.ListResult = () => ["gemini-1.0-pro", "gemini-2.5-flash", "gemini-3-flash-preview"];
        rig.Client.CompleteScript.Enqueue(_ => throw ModelGone());
        rig.Client.CompleteScript.Enqueue(_ => "feat: after switch");

        await generator.GenerateAsync(rig.GatewayInterface, Status("a.txt"), "main", false, default);

        var cached = catalog.Get(rig.Profiles.ActiveProfile!);
        Assert.NotNull(cached);
        Assert.Equal(["gemini-1.0-pro", "gemini-2.5-flash", "gemini-3-flash-preview"], cached);
    }
}
