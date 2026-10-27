namespace Historia.Services.PowerTools;

/// <inheritdoc cref="ICherryPickService"/>
public sealed class CherryPickService(IGitCliService cli) : ICherryPickService
{
    private readonly ConcurrentDictionary<string, string> _origHeads = new();

    public Task CherryPickAsync(RepositoryContext context, IReadOnlyList<GitObjectId> oldestFirst,
        bool noCommit = false, int? mainline = null, CancellationToken ct = default)
        => RunSequenceAsync(context, "cherry-pick", oldestFirst, noCommit, mainline, ct);

    public Task RevertAsync(RepositoryContext context, IReadOnlyList<GitObjectId> oldestFirst,
        int? mainline = null, CancellationToken ct = default)
        => RunSequenceAsync(context, "revert", oldestFirst, noCommit: false, mainline, ct);

    public string? LastOrigHead(RepositoryContext context)
        => _origHeads.GetValueOrDefault(context.Id);

    public async Task UndoToOrigHeadAsync(RepositoryContext context, CancellationToken ct = default)
    {
        if (!_origHeads.TryGetValue(context.Id, out var sha))
            throw new HistoriaGitException(GitErrorKind.Unknown, "There is no operation to undo this session.");

        using var op = context.BeginSelfOperation();
        var result = await context.WithWriteLockAsync(innerCt => cli.RunAsync(new GitCliRequest
        {
            Arguments = ["reset", "--hard", sha],
            WorkingDirectory = context.RepositoryPath,
            // No wall clock: hooks and large tree updates run long, and a kill midway leaves a
            // half-updated tree and a stale index.lock.
            // Cancelling stops it only before git starts; once running it is never killed.
            Timeout = Timeout.InfiniteTimeSpan,
        }, innerCt), ct).ConfigureAwait(false);
        if (!result.IsSuccess)
            throw HistoriaGitException.FromCli(result, "Undo");

        _origHeads.TryRemove(context.Id, out _);
        await context.State.RefreshAllAsync(context.Lifetime).ConfigureAwait(false);
    }

    private async Task RunSequenceAsync(RepositoryContext context, string verb, IReadOnlyList<GitObjectId> oldestFirst,
        bool noCommit, int? mainline, CancellationToken ct)
    {
        if (oldestFirst.Count == 0)
            return;

        // Record HEAD for the session undo.
        var headResult = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["rev-parse", "--verify", "-q", "HEAD"],
            WorkingDirectory = context.RepositoryPath,
        }, ct).ConfigureAwait(false);
        if (headResult.IsSuccess && !string.IsNullOrWhiteSpace(headResult.StandardOutput))
            _origHeads[context.Id] = headResult.StandardOutput.Trim();

        using var op = context.BeginSelfOperation();

        var args = new List<string> { verb };
        if (noCommit) args.Add("-n");
        if (mainline is { } m) { args.Add("-m"); args.Add(m.ToString()); }

        // Explicit oldest-first list — never the exclusive A..B range form.
        foreach (var id in oldestFirst)
            args.Add(id.ToString());

        var env = new Dictionary<string, string> { ["GIT_EDITOR"] = "true" };
        var result = await context.WithWriteLockAsync(innerCt => cli.RunAsync(new GitCliRequest
        {
            Arguments = args,
            WorkingDirectory = context.RepositoryPath,
            ExtraEnvironment = env,
            Timeout = Timeout.InfiniteTimeSpan,
        }, innerCt), ct).ConfigureAwait(false);

        await context.State.RefreshAllAsync(context.Lifetime).ConfigureAwait(false);

        if (!result.IsSuccess && result.ErrorKind != GitErrorKind.MergeConflict)
            throw HistoriaGitException.FromCli(result, verb == "cherry-pick" ? "Cherry-pick" : "Revert");
    }
}
