namespace Historia.Tests;

[Trait("tier", "unit")]
public class AiClientTests
{
    private const string Host = "ai.example.test";
    private static readonly string Server = $"https://{Host}/v1";
    private static readonly string GeminiServer = $"https://{Host}/v1beta";

    private sealed record Seen(HttpMethod Method, Uri Uri, HttpRequestHeaders Headers, string? Body);

    /// <summary>Replays queued responses and records what was sent.</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _responses = new();
        public List<Seen> Requests { get; } = [];

        public FakeHandler Enqueue(HttpStatusCode status, string body, Action<HttpResponseMessage>? configure = null)
        {
            _responses.Enqueue(() =>
            {
                var r = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                configure?.Invoke(r);
                return r;
            });
            return this;
        }

        public FakeHandler EnqueueThrow(Exception ex)
        {
            _responses.Enqueue(() => throw ex);
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Requests.Add(new Seen(request.Method, request.RequestUri!, request.Headers, body));
            return _responses.Dequeue()();
        }
    }

    private static AiHttp Http(FakeHandler handler, TimeSpan? overall = null, List<TimeSpan>? delays = null) =>
        new(handler, overallDeadline: overall, delay: (wait, ct) =>
        {
            delays?.Add(wait);
            return overall is null ? Task.CompletedTask : Task.Delay(wait, ct);
        });

    private static AiChatRequest Req(string server, string? key = "sk-secret", string model = "m1",
        string effort = "unspecified", string headers = "") => new(server, key, model, effort, headers, "PROMPT");

    private const string OpenAiReply = """{"choices":[{"finish_reason":"stop","message":{"content":"feat: hi"}}]}""";
    private const string GeminiReply = """{"candidates":[{"finishReason":"STOP","content":{"parts":[{"text":"feat: "},{"text":"hi"}]}}]}""";

    // ── OpenAI-compatible ──

    [Fact]
    public async Task OpenAi_posts_the_chat_completion_with_a_bearer_key()
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.OK, OpenAiReply);

        var text = await new OpenAiCompatibleClient(Http(h)).CompleteAsync(Req($"{Server}/"), default);

        Assert.Equal("feat: hi", text);
        var seen = Assert.Single(h.Requests);
        Assert.Equal(HttpMethod.Post, seen.Method);
        Assert.Equal($"{Server}/chat/completions", seen.Uri.ToString());
        Assert.Equal("Bearer sk-secret", seen.Headers.Authorization!.ToString());
        var json = JsonNode.Parse(seen.Body!)!;
        Assert.Equal("m1", json["model"]!.GetValue<string>());
        Assert.Equal("user", json["messages"]![0]!["role"]!.GetValue<string>());
        Assert.Equal("PROMPT", json["messages"]![0]!["content"]!.GetValue<string>());
        Assert.Null(json["reasoning_effort"]);
    }

    [Fact]
    public async Task OpenAi_omits_the_bearer_header_when_the_key_is_blank()
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.OK, OpenAiReply);

        await new OpenAiCompatibleClient(Http(h)).CompleteAsync(Req(Server, key: ""), default);

        Assert.Null(h.Requests[0].Headers.Authorization);
    }

    [Fact]
    public async Task OpenAi_sends_reasoning_effort_unless_unspecified()
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.OK, OpenAiReply);

        await new OpenAiCompatibleClient(Http(h)).CompleteAsync(Req(Server, effort: "high"), default);

        Assert.Equal("high", JsonNode.Parse(h.Requests[0].Body!)!["reasoning_effort"]!.GetValue<string>());
    }

    [Fact]
    public async Task OpenAi_applies_extra_headers_but_never_overrides_auth()
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.OK, OpenAiReply);

        await new OpenAiCompatibleClient(Http(h)).CompleteAsync(
            Req(Server, headers: "X-Team: core\nAuthorization: Bearer evil\nno colon\n: nameless\nEmpty:"), default);

        var seen = h.Requests[0];
        Assert.Equal("Bearer sk-secret", seen.Headers.Authorization!.ToString());
        Assert.Equal("core", Assert.Single(seen.Headers.GetValues("X-Team")));
        Assert.False(seen.Headers.Contains("Empty"));
    }

    [Fact]
    public async Task OpenAi_lists_models_from_data()
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.OK, """{"data":[{"id":"a"},{"id":"b"},{"id":"a"}]}""");

        var models = await new OpenAiCompatibleClient(Http(h)).ListModelsAsync(Req(Server), default);

        Assert.Equal(["a", "b"], models);
        Assert.Equal(HttpMethod.Get, h.Requests[0].Method);
        Assert.Equal($"{Server}/models", h.Requests[0].Uri.ToString());
    }

    [Theory]
    [InlineData("length", AiErrorKind.Truncated)]
    [InlineData("content_filter", AiErrorKind.ContentFiltered)]
    public async Task OpenAi_maps_finish_reasons(string reason, AiErrorKind kind)
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.OK,
            $$$"""{"choices":[{"finish_reason":"{{{reason}}}","message":{"content":"x"}}]}""");

        var ex = await Assert.ThrowsAsync<AiException>(() => new OpenAiCompatibleClient(Http(h)).CompleteAsync(Req(Server), default));
        Assert.Equal(kind, ex.Kind);
    }

    [Fact]
    public async Task OpenAi_html_reply_is_a_bad_response()
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.OK, "<html>nope</html>");

        var ex = await Assert.ThrowsAsync<AiException>(() => new OpenAiCompatibleClient(Http(h)).CompleteAsync(Req(Server), default));
        Assert.Equal(AiErrorKind.BadResponse, ex.Kind);
    }

    // ── Gemini ──

    [Fact]
    public async Task Gemini_posts_generateContent_with_the_key_in_a_header_never_the_query()
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.OK, GeminiReply);

        var text = await new GeminiClient(Http(h)).CompleteAsync(Req(GeminiServer, model: "gemini-2.5-flash"), default);

        Assert.Equal("feat: hi", text);
        var seen = Assert.Single(h.Requests);
        Assert.Equal($"{GeminiServer}/models/gemini-2.5-flash:generateContent", seen.Uri.ToString());
        Assert.Equal("sk-secret", Assert.Single(seen.Headers.GetValues("x-goog-api-key")));
        Assert.DoesNotContain("key=", seen.Uri.Query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sk-secret", seen.Uri.ToString());
        var json = JsonNode.Parse(seen.Body!)!;
        Assert.Equal("PROMPT", json["contents"]![0]!["parts"]![0]!["text"]!.GetValue<string>());
        Assert.Null(json["generationConfig"]);
    }

    [Theory]
    [InlineData("low", "thinkingLevel", "low")]
    [InlineData("high", "thinkingLevel", "high")]
    public async Task Gemini_maps_reasoning_effort_to_a_thinking_level(string effort, string field, string expected)
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.OK, GeminiReply);

        await new GeminiClient(Http(h)).CompleteAsync(Req(GeminiServer, effort: effort), default);

        var thinking = JsonNode.Parse(h.Requests[0].Body!)!["generationConfig"]!["thinkingConfig"]!;
        Assert.Equal(expected, thinking[field]!.GetValue<string>());
    }

    [Fact]
    public async Task Gemini_none_turns_thinking_off_with_a_zero_budget()
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.OK, GeminiReply);

        await new GeminiClient(Http(h)).CompleteAsync(Req(GeminiServer, effort: "none"), default);

        var thinking = JsonNode.Parse(h.Requests[0].Body!)!["generationConfig"]!["thinkingConfig"]!;
        Assert.Equal(0, thinking["thinkingBudget"]!.GetValue<int>());
    }

    [Fact]
    public async Task Gemini_accepts_a_model_with_the_models_prefix()
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.OK, GeminiReply);

        await new GeminiClient(Http(h)).CompleteAsync(Req(GeminiServer, model: "models/gemini-2.5-flash"), default);

        Assert.EndsWith("/models/gemini-2.5-flash:generateContent", h.Requests[0].Uri.AbsolutePath);
    }

    [Fact]
    public async Task Gemini_lists_generateContent_models_across_pages_and_strips_the_prefix()
    {
        var h = new FakeHandler()
            .Enqueue(HttpStatusCode.OK, """
                {"models":[
                  {"name":"models/gemini-2.5-flash","supportedGenerationMethods":["generateContent","countTokens"]},
                  {"name":"models/embedding-001","supportedGenerationMethods":["embedContent"]}],
                 "nextPageToken":"tok 1"}
                """)
            .Enqueue(HttpStatusCode.OK, """
                {"models":[{"name":"models/gemini-2.5-pro","supportedGenerationMethods":["generateContent"]}]}
                """);

        var models = await new GeminiClient(Http(h)).ListModelsAsync(Req(GeminiServer), default);

        Assert.Equal(["gemini-2.5-flash", "gemini-2.5-pro"], models);
        Assert.Equal(2, h.Requests.Count);
        Assert.Equal($"{GeminiServer}/models?pageSize=1000", h.Requests[0].Uri.ToString());
        Assert.Equal($"{GeminiServer}/models?pageSize=1000&pageToken=tok%201", h.Requests[1].Uri.OriginalString);
        Assert.All(h.Requests, r => Assert.DoesNotContain("key=", r.Uri.Query, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("SAFETY")]
    [InlineData("PROHIBITED_CONTENT")]
    public async Task Gemini_safety_finish_is_content_filtered(string finish)
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.OK, $$"""{"candidates":[{"finishReason":"{{finish}}"}]}""");

        var ex = await Assert.ThrowsAsync<AiException>(() => new GeminiClient(Http(h)).CompleteAsync(Req(GeminiServer), default));
        Assert.Equal(AiErrorKind.ContentFiltered, ex.Kind);
    }

    [Fact]
    public async Task Gemini_a_blocked_prompt_is_content_filtered()
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.OK, """{"promptFeedback":{"blockReason":"SAFETY"}}""");

        var ex = await Assert.ThrowsAsync<AiException>(() => new GeminiClient(Http(h)).CompleteAsync(Req(GeminiServer), default));
        Assert.Equal(AiErrorKind.ContentFiltered, ex.Kind);
    }

    [Fact]
    public async Task Gemini_max_tokens_is_truncated()
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.OK,
            """{"candidates":[{"finishReason":"MAX_TOKENS","content":{"parts":[{"text":"half"}]}}]}""");

        var ex = await Assert.ThrowsAsync<AiException>(() => new GeminiClient(Http(h)).CompleteAsync(Req(GeminiServer), default));
        Assert.Equal(AiErrorKind.Truncated, ex.Kind);
    }

    [Fact]
    public async Task Gemini_a_NOT_FOUND_json_body_is_a_missing_model()
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.NotFound,
            """{"error":{"code":404,"message":"models/gemini-1.0-pro is not found for API version v1beta","status":"NOT_FOUND"}}""");

        var ex = await Assert.ThrowsAsync<AiException>(() => new GeminiClient(Http(h)).CompleteAsync(Req(GeminiServer), default));
        Assert.Equal(AiErrorKind.ModelNotFound, ex.Kind);
    }

    [Fact]
    public async Task Gemini_an_html_404_is_a_wrong_server_not_a_missing_model()
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.NotFound, "<html>404</html>");

        var ex = await Assert.ThrowsAsync<AiException>(() => new GeminiClient(Http(h)).CompleteAsync(Req(GeminiServer), default));
        Assert.Equal(AiErrorKind.NotFound, ex.Kind);
    }

    [Fact]
    public async Task Gemini_a_400_saying_the_model_is_not_supported_is_a_missing_model()
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.BadRequest,
            """{"error":{"code":400,"message":"Model x is not supported for generateContent.","status":"INVALID_ARGUMENT"}}""");

        var ex = await Assert.ThrowsAsync<AiException>(() => new GeminiClient(Http(h)).CompleteAsync(Req(GeminiServer), default));
        Assert.Equal(AiErrorKind.ModelNotFound, ex.Kind);
    }

    [Theory]
    [InlineData("Thinking level MEDIUM is not supported for this model.")]
    [InlineData("thinking_budget and thinking_level are not supported together for this model")]
    public async Task Gemini_an_unsupported_thinking_option_is_not_a_missing_model(string message)
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.BadRequest,
            $$$"""{"error":{"code":400,"message":"{{{message}}}","status":"INVALID_ARGUMENT"}}""");

        var ex = await Assert.ThrowsAsync<AiException>(() => new GeminiClient(Http(h)).CompleteAsync(Req(GeminiServer), default));

        Assert.Equal(AiErrorKind.UnsupportedOption, ex.Kind);
        Assert.Contains("Reasoning Effort Level", ex.Message);
        Assert.Contains(message, ex.Details);
        Assert.Single(h.Requests);
    }

    [Theory]
    [InlineData("""{"error":{"code":400,"message":"API key not valid. Please pass a valid API key.","status":"INVALID_ARGUMENT","details":[{"@type":"type.googleapis.com/google.rpc.ErrorInfo","reason":"API_KEY_INVALID","domain":"googleapis.com"}]}}""")]
    [InlineData("""{"error":{"code":400,"message":"API key not valid. Please pass a valid API key.","status":"INVALID_ARGUMENT"}}""")]
    [InlineData("""{"error":{"code":400,"message":"Request rejected.","status":"INVALID_ARGUMENT","details":[{"reason":"API_KEY_INVALID"}]}}""")]
    public async Task Gemini_an_invalid_key_400_is_unauthorized(string body)
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.BadRequest, body);

        var ex = await Assert.ThrowsAsync<AiException>(() => new GeminiClient(Http(h)).CompleteAsync(Req(GeminiServer), default));

        Assert.Equal(AiErrorKind.Unauthorized, ex.Kind);
    }

    [Fact]
    public async Task Gemini_a_400_with_a_non_object_error_body_falls_through_to_classification()
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.BadRequest, "[]");

        var ex = await Assert.ThrowsAsync<AiException>(() => new GeminiClient(Http(h)).CompleteAsync(Req(GeminiServer), default));

        Assert.Equal(AiErrorKind.Unknown, ex.Kind);
    }

    [Fact]
    public async Task Gemini_a_size_400_is_still_context_too_large()
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.BadRequest,
            """{"error":{"code":400,"message":"The input token count exceeds the maximum number of tokens allowed.","status":"INVALID_ARGUMENT"}}""");

        var ex = await Assert.ThrowsAsync<AiException>(() => new GeminiClient(Http(h)).CompleteAsync(Req(GeminiServer), default));

        Assert.Equal(AiErrorKind.ContextTooLarge, ex.Kind);
    }

    [Fact]
    public async Task Gemini_a_non_string_model_name_is_a_bad_response()
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.OK, """{"models":[{"name":5,"supportedGenerationMethods":["generateContent"]}]}""");

        var ex = await Assert.ThrowsAsync<AiException>(() => new GeminiClient(Http(h)).ListModelsAsync(Req(GeminiServer), default));

        Assert.Equal(AiErrorKind.BadResponse, ex.Kind);
    }

    [Fact]
    public async Task Gemini_an_array_reply_is_a_bad_response()
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.OK, "[]");

        var ex = await Assert.ThrowsAsync<AiException>(() => new GeminiClient(Http(h)).CompleteAsync(Req(GeminiServer), default));

        Assert.Equal(AiErrorKind.BadResponse, ex.Kind);
    }

    [Fact]
    public async Task OpenAi_an_unsupported_reasoning_effort_is_an_unsupported_option()
    {
        const string message = "Unsupported value: 'reasoning_effort' does not support 'minimal' with this model.";
        var h = new FakeHandler().Enqueue(HttpStatusCode.BadRequest,
            $$$"""{"error":{"message":"{{{message}}}","type":"invalid_request_error","param":"reasoning_effort"}}""");

        var ex = await Assert.ThrowsAsync<AiException>(
            () => new OpenAiCompatibleClient(Http(h)).CompleteAsync(Req(Server, effort: "minimal"), default));

        Assert.Equal(AiErrorKind.UnsupportedOption, ex.Kind);
        Assert.Contains("reasoning_effort", ex.Details);
        Assert.Single(h.Requests);
    }

    [Fact]
    public async Task OpenAi_an_array_model_list_is_a_bad_response()
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.OK, "[]");

        var ex = await Assert.ThrowsAsync<AiException>(() => new OpenAiCompatibleClient(Http(h)).ListModelsAsync(Req(Server), default));

        Assert.Equal(AiErrorKind.BadResponse, ex.Kind);
    }

    [Fact]
    public async Task OpenAi_a_non_string_finish_reason_is_a_bad_response()
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.OK, """{"choices":[{"finish_reason":5,"message":{"content":"x"}}]}""");

        var ex = await Assert.ThrowsAsync<AiException>(() => new OpenAiCompatibleClient(Http(h)).CompleteAsync(Req(Server), default));

        Assert.Equal(AiErrorKind.BadResponse, ex.Kind);
    }

    [Fact]
    public async Task OpenAi_a_content_filter_still_passes_through_the_shape_guard()
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.OK, """{"choices":[{"finish_reason":"content_filter","message":{"content":""}}]}""");

        var ex = await Assert.ThrowsAsync<AiException>(() => new OpenAiCompatibleClient(Http(h)).CompleteAsync(Req(Server), default));

        Assert.Equal(AiErrorKind.ContentFiltered, ex.Kind);
    }

    [Fact]
    public async Task Gemini_listing_never_classifies_a_404_as_a_missing_model()
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.NotFound,
            """{"error":{"code":404,"message":"nope","status":"NOT_FOUND"}}""");

        var ex = await Assert.ThrowsAsync<AiException>(() => new GeminiClient(Http(h)).ListModelsAsync(Req(GeminiServer), default));
        Assert.Equal(AiErrorKind.NotFound, ex.Kind);
    }

    // ── Shared pipeline ──

    [Fact]
    public async Task A_429_with_retry_after_is_retried_then_succeeds()
    {
        var delays = new List<TimeSpan>();
        var h = new FakeHandler()
            .Enqueue((HttpStatusCode)429, "slow down", r => r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1)))
            .Enqueue(HttpStatusCode.OK, OpenAiReply);

        var text = await new OpenAiCompatibleClient(Http(h, delays: delays)).CompleteAsync(Req(Server), default);

        Assert.Equal("feat: hi", text);
        Assert.Equal(2, h.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(1)], delays);
    }

    [Fact]
    public async Task Transient_failures_back_off_exponentially_for_two_retries_then_fail()
    {
        var delays = new List<TimeSpan>();
        var h = new FakeHandler()
            .Enqueue(HttpStatusCode.ServiceUnavailable, "a")
            .Enqueue(HttpStatusCode.ServiceUnavailable, "b")
            .Enqueue(HttpStatusCode.ServiceUnavailable, "c");

        var ex = await Assert.ThrowsAsync<AiException>(() => new OpenAiCompatibleClient(Http(h, delays: delays)).CompleteAsync(Req(Server), default));

        Assert.Equal(AiErrorKind.Server, ex.Kind);
        Assert.Equal(3, h.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], delays);
    }

    [Fact]
    public async Task A_retry_after_longer_than_ten_seconds_fails_as_rate_limited_without_waiting()
    {
        var delays = new List<TimeSpan>();
        var h = new FakeHandler()
            .Enqueue((HttpStatusCode)429, "x", r => r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30)));

        var ex = await Assert.ThrowsAsync<AiException>(() => new OpenAiCompatibleClient(Http(h, delays: delays)).CompleteAsync(Req(Server), default));

        Assert.Equal(AiErrorKind.RateLimited, ex.Kind);
        Assert.Single(h.Requests);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task A_503_with_a_long_retry_after_fails_as_a_server_error()
    {
        var delays = new List<TimeSpan>();
        var h = new FakeHandler()
            .Enqueue(HttpStatusCode.ServiceUnavailable, "overloaded", r => r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(60)));

        var ex = await Assert.ThrowsAsync<AiException>(() => new OpenAiCompatibleClient(Http(h, delays: delays)).CompleteAsync(Req(Server), default));

        Assert.Equal(AiErrorKind.Server, ex.Kind);
        Assert.DoesNotContain("rate limit", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(h.Requests);
        Assert.Empty(delays);
    }

    private static string GeminiQuotaBody(string retryDelay = "48s", string quotaValue = "\"250000\"") => $$"""
        {
          "error": {
            "code": 429,
            "message": "You exceeded your current quota, please check your plan and billing details.",
            "status": "RESOURCE_EXHAUSTED",
            "details": [
              {
                "@type": "type.googleapis.com/google.rpc.QuotaFailure",
                "violations": [
                  {
                    "quotaMetric": "generativelanguage.googleapis.com/generate_content_free_tier_input_token_count",
                    "quotaId": "GenerateContentInputTokensPerModelPerMinute-FreeTier",
                    "quotaDimensions": { "location": "global", "model": "gemini-2.5-pro" },
                    "quotaValue": {{quotaValue}}
                  }
                ]
              },
              { "@type": "type.googleapis.com/google.rpc.Help", "links": [ { "description": "Learn more about Gemini API quotas" } ] },
              { "@type": "type.googleapis.com/google.rpc.RetryInfo", "retryDelay": "{{retryDelay}}" }
            ]
          }
        }
        """;

    private static async Task<AiException> RateLimitedFrom(string body, Action<HttpResponseMessage>? configure = null)
    {
        // A 429 is retried twice when the wait is short, so queue enough replies for all attempts.
        var h = new FakeHandler()
            .Enqueue((HttpStatusCode)429, body, configure)
            .Enqueue((HttpStatusCode)429, body, configure)
            .Enqueue((HttpStatusCode)429, body, configure);
        return await Assert.ThrowsAsync<AiException>(() => new GeminiClient(Http(h)).CompleteAsync(Req(GeminiServer), default));
    }

    [Fact]
    public async Task RateLimited_summary_names_retry_delay_from_body()
    {
        var ex = await RateLimitedFrom(GeminiQuotaBody(retryDelay: "47.2s"));

        Assert.Equal(AiErrorKind.RateLimited, ex.Kind);
        Assert.Contains("Try again in 48 s.", ex.Message);
        Assert.Contains("RESOURCE_EXHAUSTED", ex.Details);
    }

    [Fact]
    public async Task RateLimited_summary_names_quota_metric_and_limit()
    {
        var ex = await RateLimitedFrom(GeminiQuotaBody());

        Assert.Equal(
            "The AI service's rate limit or quota was reached (free-tier input tokens, limit 250000). Try again in 48 s.",
            ex.Message);
        Assert.DoesNotContain("://", ex.Message);
        Assert.DoesNotContain("googleapis", ex.Message);
    }

    [Fact]
    public async Task RateLimited_accepts_a_numeric_quota_value()
    {
        var ex = await RateLimitedFrom(GeminiQuotaBody(quotaValue: "250000"));

        Assert.Contains("limit 250000", ex.Message);
    }

    [Fact]
    public async Task RateLimited_prefers_the_retry_after_header_over_the_body()
    {
        var ex = await RateLimitedFrom(GeminiQuotaBody(), r => r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30)));

        Assert.Contains("Try again in 30 s.", ex.Message);
    }

    [Fact]
    public async Task RateLimited_with_short_body_delay_retries()
    {
        var delays = new List<TimeSpan>();
        var h = new FakeHandler()
            .Enqueue((HttpStatusCode)429, GeminiQuotaBody(retryDelay: "2s"))
            .Enqueue(HttpStatusCode.OK, GeminiReply);

        var text = await new GeminiClient(Http(h, delays: delays)).CompleteAsync(Req(GeminiServer), default);

        Assert.Equal("feat: hi", text);
        Assert.Equal(2, h.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(2)], delays);
    }

    [Fact]
    public async Task RateLimited_after_the_retries_still_names_the_quota()
    {
        var h = new FakeHandler()
            .Enqueue((HttpStatusCode)429, GeminiQuotaBody(retryDelay: "1s"))
            .Enqueue((HttpStatusCode)429, GeminiQuotaBody(retryDelay: "1s"))
            .Enqueue((HttpStatusCode)429, GeminiQuotaBody(retryDelay: "1s"));

        var ex = await Assert.ThrowsAsync<AiException>(() => new GeminiClient(Http(h)).CompleteAsync(Req(GeminiServer), default));

        Assert.Equal(AiErrorKind.RateLimited, ex.Kind);
        Assert.Equal(3, h.Requests.Count);
        Assert.Contains("free-tier input tokens", ex.Message);
        Assert.Contains("Try again in 1 s.", ex.Message);
    }

    [Fact]
    public async Task RateLimited_without_details_keeps_generic_text()
    {
        var ex = await RateLimitedFrom("""{"error":{"code":429,"message":"slow down","status":"RESOURCE_EXHAUSTED"}}""");

        Assert.Equal(AiErrorKind.RateLimited, ex.Kind);
        Assert.Equal("The AI service is rate limiting requests. Try again in a moment.", ex.Message);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("[1,2,3]")]
    [InlineData("""{"error":{"details":"nope"}}""")]
    [InlineData("""{"error":{"details":[1,"x",null,{"violations":"x","retryDelay":5},{"violations":[7],"retryDelay":"soon"}]}}""")]
    [InlineData("""{"error":{"details":[{"violations":[{"quotaMetric":"a b","quotaValue":{"x":1}}],"retryDelay":"-3s"}]}}""")]
    public async Task Malformed_429_body_does_not_throw(string body)
    {
        var ex = await RateLimitedFrom(body);

        Assert.Equal(AiErrorKind.RateLimited, ex.Kind);
        Assert.Equal("The AI service is rate limiting requests. Try again in a moment.", ex.Message);
    }

    [Fact]
    public void The_overall_deadline_is_four_minutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(4), AiHttp.OverallDeadline);
    }

    [Fact]
    public void The_shared_pipeline_uses_the_real_deadlines()
    {
        Assert.True(AiHttp.Shared.OverallDeadlineValue > TimeSpan.Zero);
        Assert.True(AiHttp.Shared.AttemptTimeoutValue > TimeSpan.Zero);
        Assert.Equal(AiHttp.OverallDeadline, AiHttp.Shared.OverallDeadlineValue);
        Assert.Equal(AiHttp.AttemptTimeout, AiHttp.Shared.AttemptTimeoutValue);
    }

    [Fact]
    public void The_attempt_timeout_is_three_minutes()
    {
        Assert.Equal(TimeSpan.FromSeconds(180), AiHttp.AttemptTimeout);
    }

    [Fact]
    public async Task A_400_is_not_retried()
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.BadRequest, "bad");

        var ex = await Assert.ThrowsAsync<AiException>(() => new OpenAiCompatibleClient(Http(h)).CompleteAsync(Req(Server), default));

        Assert.Equal(AiErrorKind.Unknown, ex.Kind);
        Assert.Single(h.Requests);
    }

    [Fact]
    public async Task The_overall_deadline_stops_retrying()
    {
        var h = new FakeHandler()
            .Enqueue((HttpStatusCode)429, "x", r => r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(5)))
            .Enqueue(HttpStatusCode.OK, OpenAiReply);

        var ex = await Assert.ThrowsAsync<AiException>(
            () => new OpenAiCompatibleClient(Http(h, overall: TimeSpan.FromMilliseconds(100))).CompleteAsync(Req(Server), default));

        Assert.Equal(AiErrorKind.RateLimited, ex.Kind);
        Assert.Single(h.Requests);
    }

    [Theory]
    [InlineData(401, AiErrorKind.Unauthorized)]
    [InlineData(403, AiErrorKind.Unauthorized)]
    [InlineData(404, AiErrorKind.NotFound)]
    [InlineData(413, AiErrorKind.ContextTooLarge)]
    [InlineData(418, AiErrorKind.Unknown)]
    public async Task Statuses_map_to_kinds(int status, AiErrorKind kind)
    {
        var h = new FakeHandler().Enqueue((HttpStatusCode)status, "{}");

        var ex = await Assert.ThrowsAsync<AiException>(() => new OpenAiCompatibleClient(Http(h)).CompleteAsync(Req(Server), default));

        Assert.Equal(kind, ex.Kind);
    }

    [Fact]
    public async Task A_413_suggests_a_diff_size_limit()
    {
        var h = new FakeHandler().Enqueue((HttpStatusCode)413, "{}");

        var ex = await Assert.ThrowsAsync<AiException>(() => new OpenAiCompatibleClient(Http(h)).CompleteAsync(Req(Server), default));

        Assert.Contains("Diff size limit", ex.Message);
    }

    [Theory]
    [InlineData("This model's maximum context length is 8192 tokens")]
    [InlineData("request too large")]
    [InlineData("input exceeds the limit")]
    public async Task A_400_that_mentions_size_is_context_too_large(string message)
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.BadRequest, $$$"""{"error":{"message":"{{{message}}}"}}""");

        var ex = await Assert.ThrowsAsync<AiException>(() => new OpenAiCompatibleClient(Http(h)).CompleteAsync(Req(Server), default));

        Assert.Equal(AiErrorKind.ContextTooLarge, ex.Kind);
    }

    [Fact]
    public async Task A_connection_failure_is_a_network_error()
    {
        var h = new FakeHandler().EnqueueThrow(new HttpRequestException("no route"));

        var ex = await Assert.ThrowsAsync<AiException>(() => new OpenAiCompatibleClient(Http(h)).CompleteAsync(Req(Server), default));

        Assert.Equal(AiErrorKind.Network, ex.Kind);
    }

    [Fact]
    public async Task An_attempt_that_outlives_its_timeout_is_a_timeout()
    {
        var http = new AiHttp(new HangingHandler(), attemptTimeout: TimeSpan.FromMilliseconds(50));

        var ex = await Assert.ThrowsAsync<AiException>(() => new OpenAiCompatibleClient(http).CompleteAsync(Req(Server), default));

        Assert.Equal(AiErrorKind.Timeout, ex.Kind);
        Assert.Equal("The AI service took too long to respond.", ex.Message);
        Assert.Equal("No reply within 1 s.", ex.Details);
    }

    [Fact]
    public async Task The_overall_deadline_elapsing_mid_attempt_is_a_timeout()
    {
        var http = new AiHttp(new HangingHandler(), overallDeadline: TimeSpan.FromMilliseconds(50));

        var ex = await Assert.ThrowsAsync<AiException>(() => new OpenAiCompatibleClient(http).CompleteAsync(Req(Server), default));

        Assert.Equal(AiErrorKind.Timeout, ex.Kind);
        Assert.Equal("No reply within 1 s, retries included.", ex.Details);
    }

    [Fact]
    public async Task A_cancellation_nobody_asked_for_is_not_a_timeout()
    {
        var h = new FakeHandler().EnqueueThrow(new TaskCanceledException("boom sk-secret"));

        var ex = await Assert.ThrowsAsync<AiException>(() => new OpenAiCompatibleClient(Http(h)).CompleteAsync(Req(Server), default));

        Assert.Equal(AiErrorKind.Unknown, ex.Kind);
        Assert.Equal("The request to the AI service was cancelled unexpectedly.", ex.Message);
        Assert.Equal("boom •••", ex.Details);
    }

    [Fact]
    public async Task Caller_cancellation_surfaces_as_cancellation_not_an_error()
    {
        var http = new AiHttp(new HangingHandler());
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new OpenAiCompatibleClient(http).CompleteAsync(Req(Server), cts.Token));
    }

    [Fact]
    public async Task Details_are_truncated_and_have_every_secret_replaced()
    {
        var body = "bad key sk-secret and header-secret " + new string('x', 5000);
        var h = new FakeHandler().Enqueue(HttpStatusCode.Unauthorized, body);

        var ex = await Assert.ThrowsAsync<AiException>(() => new OpenAiCompatibleClient(Http(h)).CompleteAsync(
            Req(Server, headers: "X-Org: header-secret"), default));

        Assert.NotNull(ex.Details);
        Assert.DoesNotContain("sk-secret", ex.Details);
        Assert.DoesNotContain("header-secret", ex.Details);
        Assert.Contains("•••", ex.Details);
        Assert.True(Encoding.UTF8.GetByteCount(ex.Details) < 2100);
        Assert.DoesNotContain("sk-secret", ex.Message);
    }

    [Fact]
    public async Task A_truncated_secret_cannot_survive_as_a_fragment()
    {
        var key = "sk-" + new string('k', 40);
        var body = new string('x', 2040) + key;
        var h = new FakeHandler().Enqueue(HttpStatusCode.Unauthorized, body);

        var ex = await Assert.ThrowsAsync<AiException>(() => new OpenAiCompatibleClient(Http(h)).CompleteAsync(Req(Server, key: key), default));

        Assert.DoesNotContain("sk-kkk", ex.Details);
    }

    // Two-byte characters with both paddings, so one of the two cases cuts mid-character.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Truncated_details_never_end_in_half_a_character(int padding)
    {
        var body = new string('x', padding) + new string('é', 2000);
        var h = new FakeHandler().Enqueue(HttpStatusCode.BadRequest, body);

        var ex = await Assert.ThrowsAsync<AiException>(() => new OpenAiCompatibleClient(Http(h)).CompleteAsync(Req(Server), default));

        Assert.NotNull(ex.Details);
        Assert.DoesNotContain('�', ex.Details);
        Assert.EndsWith("é…", ex.Details);
        Assert.InRange(Encoding.UTF8.GetByteCount(ex.Details[..^1]), 2047, 2048);
    }

    [Theory]
    [InlineData("localhost:11434/v1")]
    [InlineData("ollama.local/v1")]
    [InlineData("http://")]
    [InlineData("ftp://{host}/v1")]
    [InlineData("http://[bad")]
    public async Task A_server_that_is_not_an_http_url_is_refused_before_sending(string server)
    {
        server = server.Replace("{host}", Host, StringComparison.Ordinal);
        var h = new FakeHandler();

        var openAi = await Assert.ThrowsAsync<AiException>(() => new OpenAiCompatibleClient(Http(h)).ListModelsAsync(Req(server), default));
        var gemini = await Assert.ThrowsAsync<AiException>(() => new GeminiClient(Http(h)).CompleteAsync(Req(server), default));

        Assert.Equal(AiErrorKind.InvalidServer, openAi.Kind);
        Assert.Equal(AiErrorKind.InvalidServer, gemini.Kind);
        Assert.Contains("Server", openAi.Message);
        Assert.Empty(h.Requests);
    }

    [Fact]
    public void The_factory_picks_the_client_for_each_kind()
    {
        var factory = new AiClientFactory(new AiHttp(new FakeHandler()));

        Assert.IsType<OpenAiCompatibleClient>(factory.For(AiServiceKind.Custom));
        Assert.IsType<GeminiClient>(factory.For(AiServiceKind.Gemini));
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException();
        }
    }
}
