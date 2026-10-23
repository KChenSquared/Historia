namespace Historia.Services.ExternalControl;

/// <summary>Serializes enums as lower-case kebab strings (<c>rev-not-found</c>, <c>propose-commit</c>)
/// and accepts nothing else: an integer is a malformed request, not an undefined member.</summary>
public sealed class ControlKebabEnumConverter<T> : JsonStringEnumConverter<T> where T : struct, Enum
{
    public ControlKebabEnumConverter() : base(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false) { }
}

/// <summary>What an external tool can ask Historia to do. Every verb navigates; only
/// <see cref="ProposeCommit"/> offers text, and that text only reaches an editable draft the
/// user must still act on. <see cref="Queue"/> carries one of the navigation verbs as its
/// <see cref="ControlRequest.Target"/>: Historia records it as an item the user opens later
/// instead of switching the view now, and never opens a tab or brings its window forward for it.</summary>
[JsonConverter(typeof(ControlKebabEnumConverter<ControlVerb>))]
public enum ControlVerb
{
    Ping,
    Open,
    Show,
    Diff,
    Compare,
    Changes,
    Selection,
    ProposeCommit,
    Queue,
}

[JsonConverter(typeof(ControlKebabEnumConverter<ControlError>))]
public enum ControlError
{
    Usage,
    Disabled,
    Unreachable,
    RepoNotFound,
    RevNotFound,
    PathInvalid,
    NothingStaged,
    Timeout,
    Internal,
}

/// <summary>How a proposed commit message reached the commit box.</summary>
[JsonConverter(typeof(ControlKebabEnumConverter<ControlDraftOutcome>))]
public enum ControlDraftOutcome
{
    /// <summary>The box was empty (or already held the same text) and now holds the message.</summary>
    Filled,
    /// <summary>The user has a different draft, or the box is busy; the message is offered instead.</summary>
    Pending,
}

public sealed record ControlRequest
{
    public required ControlVerb Verb { get; init; }

    /// <summary>For <see cref="ControlVerb.Queue"/>: the navigation verb to run when the user opens
    /// the item. Every other field describes that verb's request.</summary>
    public ControlVerb? Target { get; init; }

    /// <summary>For <see cref="ControlVerb.Queue"/>: a short message from the caller, shown on the item.</summary>
    public string? Note { get; init; }

    /// <summary>The caller's working directory, absolute. File paths are resolved against it.</summary>
    public string? Cwd { get; init; }

    /// <summary>The repository the caller means, absolute.</summary>
    public string? Repo { get; init; }

    public string? Rev { get; init; }

    /// <summary>The head revision of a <see cref="ControlVerb.Compare"/> (<see cref="Rev"/> is the base).</summary>
    public string? OtherRev { get; init; }

    public string? Path { get; init; }

    /// <summary>For a queued <see cref="ControlVerb.Changes"/> naming more than one file: every file,
    /// the first (<see cref="Path"/>) included. Null when there is one file.</summary>
    public IReadOnlyList<string>? Paths { get; init; }

    /// <summary>1-based line in the new (right-hand) file to scroll to.</summary>
    public int? Line { get; init; }

    /// <summary>For <see cref="ControlVerb.Changes"/>: show the staged change instead of the unstaged one.</summary>
    public bool Staged { get; init; }

    /// <summary>Bring Historia's window to the front. Absent means yes; see <see cref="WantsFocus"/>.</summary>
    public bool? Focus { get; init; }

    [JsonIgnore]
    public bool WantsFocus => Focus ?? true;

    /// <summary>For <see cref="ControlVerb.ProposeCommit"/>: the proposed message.</summary>
    public string? CommitMessage { get; init; }
}

public sealed record ControlSelectionDiff
{
    /// <summary>The kind of diff open: <c>commit</c>, <c>comparison</c>, <c>unstaged</c> or <c>staged</c>.</summary>
    public required string Source { get; init; }
    public string? Path { get; init; }
    public string? Commit { get; init; }
    public string? OtherCommit { get; init; }
}

public sealed record ControlSelection
{
    public string? Repo { get; init; }
    public string? Commit { get; init; }
    public ControlSelectionDiff? Diff { get; init; }
}

public sealed record ControlResponse
{
    public required bool Ok { get; init; }
    public ControlError? Error { get; init; }
    public string? Message { get; init; }
    public string? Repo { get; init; }
    public string? Commit { get; init; }
    public string? OtherCommit { get; init; }
    public string? Path { get; init; }
    public int? Line { get; init; }

    /// <summary>For <c>propose-commit</c>: every staged path, repo-relative with forward slashes.</summary>
    public IReadOnlyList<string>? StagedPaths { get; init; }

    /// <summary>For <c>propose-commit</c>: whether the message was filled in or is being offered.</summary>
    public ControlDraftOutcome? Draft { get; init; }

    /// <summary>For <c>queue</c>: true when the item was added for the user to open later.</summary>
    public bool? Queued { get; init; }

    /// <summary>For <c>queue</c>: the id of the added item.</summary>
    public string? QueueId { get; init; }

    /// <summary>For <c>selection</c>: what the focused repository is showing.</summary>
    public ControlSelection? Selection { get; init; }

    public static ControlResponse Success() => new() { Ok = true };

    public static ControlResponse Fail(ControlError error, string? message = null) =>
        new() { Ok = false, Error = error, Message = message };
}

/// <summary>Process exit codes of <c>historia-cmdr</c>.</summary>
public static class ControlExitCode
{
    public const int Ok = 0;
    /// <summary>The request reached Historia and failed (bad revision, bad path, nothing staged…).</summary>
    public const int CommandError = 1;
    /// <summary>Historia could not be reached, or external control is switched off.</summary>
    public const int Unreachable = 2;
    public const int Usage = 64;

    public static int For(ControlResponse response)
    {
        if (response.Ok)
            return Ok;

        return response.Error switch
        {
            ControlError.Usage => Usage,
            ControlError.Disabled or ControlError.Unreachable => Unreachable,
            _ => CommandError,
        };
    }
}

public static class ControlVerbs
{
    private static readonly FrozenDictionary<string, ControlVerb> ByWireName =
        Enum.GetValues<ControlVerb>().ToFrozenDictionary(ToWire, v => v, StringComparer.Ordinal);

    public static string ToWire(ControlVerb verb) =>
        JsonNamingPolicy.KebabCaseLower.ConvertName(verb.ToString());

    public static bool TryParse(string? wire, out ControlVerb verb)
    {
        verb = default;
        return wire is not null && ByWireName.TryGetValue(wire, out verb);
    }

    /// <summary>Whether the client may start Historia when none is running. Queries never do.</summary>
    public static bool MayLaunch(ControlVerb verb) => verb is not (ControlVerb.Ping or ControlVerb.Selection);

    /// <summary>Whether <paramref name="verb"/> navigates and can therefore be queued.</summary>
    public static bool IsQueueable(ControlVerb verb) => verb is ControlVerb.Open or ControlVerb.Show
        or ControlVerb.Diff or ControlVerb.Compare or ControlVerb.Changes or ControlVerb.ProposeCommit;

    /// <summary>The message a Historia that predates <see cref="ControlVerb.Queue"/> answers for it:
    /// it cannot deserialize the verb, so the whole request reads as malformed. Fixed by those
    /// builds, so it must not follow later wording changes.</summary>
    public const string PreQueueHostReply = "The request is not valid JSON.";

    /// <summary>Whether <paramref name="response"/> is a running Historia refusing a queued request
    /// because it is too old to know the verb.</summary>
    public static bool IsQueueUnsupported(ControlRequest request, ControlResponse response) =>
        request.Verb == ControlVerb.Queue
        && response is { Ok: false, Error: ControlError.Usage, Message: PreQueueHostReply };
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(ControlRequest))]
[JsonSerializable(typeof(ControlResponse))]
internal sealed partial class ControlJsonContext : JsonSerializerContext;

public static class ControlJson
{
    public static string Serialize(ControlRequest request) =>
        JsonSerializer.Serialize(request, ControlJsonContext.Default.ControlRequest);

    public static string Serialize(ControlResponse response) =>
        JsonSerializer.Serialize(response, ControlJsonContext.Default.ControlResponse);

    public static ControlRequest? DeserializeRequest(string json) =>
        JsonSerializer.Deserialize(json, ControlJsonContext.Default.ControlRequest);

    public static ControlResponse? DeserializeResponse(string json) =>
        JsonSerializer.Deserialize(json, ControlJsonContext.Default.ControlResponse);
}
