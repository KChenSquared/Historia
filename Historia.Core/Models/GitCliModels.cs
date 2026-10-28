namespace Historia.Models;

/// <summary>
/// Result of a single Git CLI invocation. Raw stderr is carried but never surfaced
/// unmodified; <see cref="ErrorKind"/> is what the UI maps to an actionable message.
/// </summary>
public sealed record GitCliResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    GitErrorKind ErrorKind,
    TimeSpan Duration)
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private string? _standardOutput = StandardOutput;

    /// <summary>A result whose stdout is held as bytes only: <see cref="StandardOutput"/> decodes
    /// them as UTF-8 on first access and keeps the string, so a caller that reads
    /// <see cref="StandardOutputBytes"/> never pays for the text copy.</summary>
    public GitCliResult(
        int exitCode, byte[] standardOutputBytes, string standardError, GitErrorKind errorKind, TimeSpan duration)
        : this(exitCode, (string)null!, standardError, errorKind, duration)
    {
        StandardOutputBytes = standardOutputBytes;
    }

    /// <summary>Stdout as text. For a result built from bytes this is decoded on first access.</summary>
    public string StandardOutput
    {
        get => _standardOutput ??= StandardOutputBytes is { } bytes ? Utf8NoBom.GetString(bytes) : string.Empty;
        init => _standardOutput = value;
    }

    public bool IsSuccess => ExitCode == 0 && ErrorKind == GitErrorKind.None;

    /// <summary>Stdout exactly as git wrote it — set only for a
    /// <see cref="GitCliRequest.RawStandardOutput"/> request, null otherwise.</summary>
    public byte[]? StandardOutputBytes { get; init; }

    /// <summary>True when stdout exceeded <see cref="GitCliRequest.MaxStandardOutputBytes"/>,
    /// stopping the read and terminating the process tree early.</summary>
    public bool OutputTruncated { get; init; }

    // Value equality that does not depend on whether StandardOutput has been decoded yet: a raw
    // result compares its bytes, a text result compares its text. The compiler-generated version
    // would compare the lazy decode cache and the byte array by reference.
    public bool Equals(GitCliResult? other)
    {
        if (other is null)
            return false;
        if (ReferenceEquals(this, other))
            return true;
        if (ExitCode != other.ExitCode || ErrorKind != other.ErrorKind || Duration != other.Duration
            || OutputTruncated != other.OutputTruncated || StandardError != other.StandardError)
            return false;

        var bytes = StandardOutputBytes;
        var otherBytes = other.StandardOutputBytes;
        if (bytes is null || otherBytes is null)
            return bytes is null && otherBytes is null && StandardOutput == other.StandardOutput;
        return bytes.AsSpan().SequenceEqual(otherBytes);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(ExitCode);
        hash.Add(StandardError);
        hash.Add(ErrorKind);
        hash.Add(Duration);
        hash.Add(OutputTruncated);
        if (StandardOutputBytes is { } bytes)
            hash.Add(bytes.Length);
        else
            hash.Add(StandardOutput);
        return hash.ToHashCode();
    }

    // Reports the size of stdout instead of its text, so printing a result never decodes it.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"ExitCode = {ExitCode}, ErrorKind = {ErrorKind}, Duration = {Duration}, ");
        builder.Append($"StandardOutput = {(StandardOutputBytes?.Length ?? _standardOutput?.Length ?? 0)} ");
        builder.Append(StandardOutputBytes is null ? "chars" : "bytes");
        builder.Append($", StandardError = {StandardError}, OutputTruncated = {OutputTruncated}");
        return true;
    }
}

/// <summary>How a git invocation competes with the rest of the app.</summary>
public enum GitCliPriority
{
    /// <summary>User-driven work whose result the user is waiting for. Runs at normal priority and
    /// never waits for a background slot.</summary>
    Interactive,

    /// <summary>The default: ordinary foreground work. Runs at normal priority and never waits for a
    /// background slot.</summary>
    Normal,

    /// <summary>Local, self-contained work nobody is waiting for (a commit-graph write, an index
    /// stat refresh). Runs at below-normal priority, and at most two such processes run at a time
    /// across the app; the rest wait for a slot before they start. A call made inside a
    /// <see cref="GitWriteScope"/> never waits for a slot.</summary>
    Background,

    /// <summary>Background network work (auto-fetch). Runs at below-normal priority but does not take
    /// a background slot: a fetch can run for a long time on a bad network and must not hold local
    /// background work back.</summary>
    BackgroundNetwork,
}

/// <summary>What a cancellation or timeout does to a git process that has already started.</summary>
public enum GitCliCancelPolicy
{
    /// <summary>The process tree is killed and the call reports <see cref="GitErrorKind.Cancelled"/>
    /// or <see cref="GitErrorKind.TimedOut"/>.</summary>
    KillOnCancel,

    /// <summary>Once the process has started it is never killed: the call keeps awaiting it and
    /// returns its real exit code. For writers, which clear their lock files only from git's own
    /// exit path — a killed writer leaves <c>index.lock</c> behind and a half-applied change.
    /// A cancellation that arrives before the process starts still returns
    /// <see cref="GitErrorKind.Cancelled"/> without starting it.</summary>
    NeverAfterStart,
}

/// <summary>
/// One Git CLI invocation request. Arguments are passed through
/// <c>ProcessStartInfo.ArgumentList</c> — never a concatenated string.
/// </summary>
public sealed record GitCliRequest
{
    /// <summary>Arguments after the deterministic <c>-c</c> prefix. Each element is one argv slot.</summary>
    public required IReadOnlyList<string> Arguments { get; init; }

    /// <summary>Working directory (repo working dir, or parent dir for <c>clone</c>).</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>Active profile whose SSH key / identity scopes this invocation's environment.</summary>
    public GitProfile? Profile { get; init; }

    /// <summary>Extra per-invocation environment entries. Applied on top of the deterministic base.</summary>
    public IReadOnlyDictionary<string, string>? ExtraEnvironment { get; init; }

    /// <summary>Local-operation wall-clock timeout. Network operations pass <see cref="Timeout.InfiniteTimeSpan"/>.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>When true, stdout/stderr line callbacks fire as output streams in.</summary>
    public bool StreamOutput { get; init; }

    /// <summary>Capture stdout as bytes, with no line splitting — for machine-parsed output whose
    /// line breaks, lone CRs or NULs must survive exactly (<see cref="GitCliResult.StandardOutputBytes"/>;
    /// <see cref="GitCliResult.StandardOutput"/> is those bytes decoded as UTF-8). Line mode
    /// splits on CR as well as LF and rejoins lines with CRLF. Can't be combined with
    /// <see cref="StreamOutput"/> or a stdout line callback.</summary>
    public bool RawStandardOutput { get; init; }

    /// <summary>Maximum bytes of stdout to buffer before stopping the read, killing the process
    /// tree, and flagging <see cref="GitCliResult.OutputTruncated"/>. Null means unlimited.</summary>
    public long? MaxStandardOutputBytes { get; init; }

    /// <summary>When set, written to the process's stdin and then closed (e.g. a patch for <c>git apply -</c>).</summary>
    public string? StandardInput { get; init; }

    /// <summary>How urgently the invocation competes for CPU and for the background slots; see
    /// <see cref="GitCliPriority"/>. It says nothing about cancellation: a cancelled token or an
    /// elapsed <see cref="Timeout"/> kills the process tree like any other request, unless
    /// <see cref="CancelPolicy"/> or an enclosing <see cref="GitWriteScope"/> forbids it.</summary>
    public GitCliPriority Priority { get; init; } = GitCliPriority.Normal;

    /// <summary>Shorthand for <see cref="GitCliPriority.Background"/>: setting it to true on a
    /// request that has no other priority makes the request a background one; it reads true for
    /// both background priorities.</summary>
    public bool LowerPriority
    {
        get => Priority is GitCliPriority.Background or GitCliPriority.BackgroundNetwork;
        init
        {
            if (value && Priority == GitCliPriority.Normal)
                Priority = GitCliPriority.Background;
        }
    }

    /// <summary>What a cancellation or <see cref="Timeout"/> does once the process has started.
    /// A call made inside a <see cref="GitWriteScope"/> behaves as
    /// <see cref="GitCliCancelPolicy.NeverAfterStart"/> whatever this says.</summary>
    public GitCliCancelPolicy CancelPolicy { get; init; } = GitCliCancelPolicy.KillOnCancel;
}

/// <summary>Phase-aware progress for a streamed network operation.</summary>
public sealed record GitProgress
{
    public required string Phase { get; init; }
    public double? Percent { get; init; }
    public long? ObjectsDone { get; init; }
    public long? ObjectsTotal { get; init; }
    public double? BytesPerSecond { get; init; }

    /// <summary>Raw progress line, for a details/log view.</summary>
    public string? RawLine { get; init; }
}

/// <summary>Discovered <c>git.exe</c> location and version.</summary>
public sealed record GitExecutableInfo
{
    public required bool Found { get; init; }
    public string? Path { get; init; }
    public Version? Version { get; init; }
    public string? NotFoundReason { get; init; }

    /// <summary>
    /// Minimum supported version is 2.46, the release where an empty <c>remote.&lt;name&gt;.url</c>
    /// clears the URL list, which per-invocation transport routing relies on. It also covers
    /// <c>stash push --staged</c> and <c>checkout --conflict=zdiff3</c> (2.35).
    /// </summary>
    public static readonly Version MinimumVersion = new(2, 46);

    public static GitExecutableInfo NotFound { get; } = new() { Found = false };
}
