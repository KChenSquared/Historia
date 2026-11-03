namespace Historia.Models;

/// <summary>The protocol an AI integration speaks, which is also its template.</summary>
public enum AiServiceKind
{
    /// <summary>OpenAI-compatible chat completions (OpenAI, Azure's v1 endpoint, Ollama, …).</summary>
    Custom,

    /// <summary>The native Gemini REST API.</summary>
    Gemini,
}

/// <summary>The reasoning-effort choices a profile offers.</summary>
public static class AiReasoningEffort
{
    /// <summary>Sent to the service only when it is not <see cref="Unspecified"/>.</summary>
    public const string Unspecified = "unspecified";

    public static IReadOnlyList<string> Levels { get; } = [Unspecified, "none", "minimal", "low", "medium", "high"];
}

/// <summary>
/// The per-profile Diff size limit, in tokens; 0 is uncapped. No tokenizer ships with Historia, so
/// the limit is converted to bytes at <see cref="BytesPerToken"/>, a rough average for English and code.
/// </summary>
public static class AiDiffCap
{
    public const int DefaultTokens = 25_000;
    public const int BytesPerToken = 4;

    /// <summary>The choices the editor offers, in order; 0 is uncapped.</summary>
    public static IReadOnlyList<int> TokenSteps { get; } = [5_000, 10_000, 25_000, 50_000, 100_000, 0];

    /// <summary>Null means uncapped (any value of 0 or less).</summary>
    public static long? ToBytes(int tokens) => tokens <= 0 ? null : (long)tokens * BytesPerToken;

    /// <summary>The offered choice closest to <paramref name="tokens"/>. A value of 0 or less is
    /// uncapped; a value above the largest cap stays at the largest cap.</summary>
    public static int NearestStep(int tokens)
    {
        if (tokens <= 0)
            return 0;
        var best = TokenSteps[0];
        foreach (var step in TokenSteps)
        {
            if (step > 0 && Math.Abs((long)step - tokens) < Math.Abs((long)best - tokens))
                best = step;
        }
        return best;
    }
}

/// <summary>
/// One AI integration used to write commit messages. <see cref="ApiKey"/> is stored in plain
/// text in <c>settings.json</c>; in environment mode it holds the variable's name instead.
/// </summary>
public sealed record AiServiceProfile
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "";
    public AiServiceKind Kind { get; init; } = AiServiceKind.Custom;
    public string Server { get; init; } = "";
    public string Model { get; init; } = "";
    public bool AutoFetchModels { get; init; } = true;

    /// <summary>When on, <see cref="ApiKey"/> is the name of an environment variable.</summary>
    public bool ReadApiKeyFromEnv { get; init; }

    public string ApiKey { get; init; } = "";
    public string ReasoningEffort { get; init; } = AiReasoningEffort.Unspecified;
    public string AdditionalPrompt { get; init; } = "";

    /// <summary>One <c>Name: Value</c> per line.</summary>
    public string ExtraHeaders { get; init; } = "";

    /// <summary>Approximate token limit for the diff sent to the model; 0 is uncapped. See <see cref="AiDiffCap"/>.</summary>
    public int DiffCapTokens { get; init; } = AiDiffCap.DefaultTokens;

    /// <summary>For information only. Display order is list order.</summary>
    public DateTime CreatedUtc { get; init; }
}
