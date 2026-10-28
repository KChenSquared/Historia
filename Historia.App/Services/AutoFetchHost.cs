namespace Historia.Services.UI;

/// <summary>
/// App-side bridge for the Core <see cref="IAutoFetchService"/>. Reports a tab as fully
/// loaded via <see cref="RepositoryContext.FirstFullLoad"/> — forcing a still-deferred
/// session-restore tab through its initialize path first — and runs a background <c>git fetch</c>
/// through the same gateway a manual Fetch uses, flagged so routing never raises the
/// switch-to-Local-config prompt. A failure with a local cause (a stale lock, an unsafe or missing
/// repository, a tag the remote has moved…) is reported once per repository and kind until that
/// repository fetches cleanly again; offline, sign-in and timeout failures stay in the developer
/// log, so a machine with no network is never nagged.
/// </summary>
public sealed class AutoFetchHost(
    IWorkspaceService workspace,
    ISettingsService settings,
    INotificationService notifications,
    IDialogService dialogs,
    IRepoNetworkActionRunner runner) : IAutoFetchHost
{
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<RepositoryContext, HashSet<GitErrorKind>> _warned = new();

    public Task WhenFullyLoadedAsync(RepositoryContext context, CancellationToken ct)
    {
        workspace.EnsureInitialized(context);
        return context.FirstFullLoad.WaitAsync(ct);
    }

    public async Task FetchAsync(RepositoryContext context, CancellationToken ct)
    {
        var remotes = context.State.Remotes;
        if (remotes.Count == 0)
            return;

        var remote = remotes.Any(r => r.Name == "origin") ? "origin" : remotes[0].Name;
        try
        {
            await context.Gateway
                .FetchAsync(remote, GitFetchOptions.From(settings.Current, background: true), progress: null, ct)
                .ConfigureAwait(false);
        }
        catch (HistoriaGitException ex)
        {
            if (ex.Kind == GitErrorKind.TagConflict)
            {
                // Every other ref was fetched before the tag was reported; the caller refreshes
                // once the network gate is released.
                if (FirstWarning(context, ex.Kind))
                    notifications.Publish(NotificationSeverity.Warning,
                        $"Auto-fetch of {context.DisplayName}: {TagConflictPrompt.Subject(ex)} on {remote}.", ex.Details,
                        TagConflictPrompt.ActionsFor(context, remote, ex, runner, dialogs, notifications));
                throw;
            }

            var remedy = StaleLockPrompt.ActionsFor(context, ex, dialogs, notifications);
            if ((ShouldWarn(ex.Kind) || remedy.Count > 0) && FirstWarning(context, ex.Kind))
            {
                var why = remedy.Count > 0 && ex.Kind != GitErrorKind.LockedIndex
                    ? "lock files left by a stopped git process are in the way"
                    : HistoriaGitException.Describe(ex.Kind);
                notifications.Publish(NotificationSeverity.Warning,
                    $"Auto-fetch of {context.DisplayName} failed: {why}.", ex.Details, remedy);
            }
            throw;
        }

        lock (_warned)
        {
            if (_warned.TryGetValue(context, out var kinds))
                kinds.Clear();
        }
    }

    public Task RefreshAfterFetchAsync(RepositoryContext context, CancellationToken ct)
        => context.State.Remotes.Count == 0 ? Task.CompletedTask : context.State.RefreshAllAsync(ct);

    /// <summary>Failures the user can act on locally. Transport failures (offline, sign-in,
    /// timeout, host key) and unclassified ones are expected on a laptop off the network.</summary>
    internal static bool ShouldWarn(GitErrorKind kind) => kind is not (
        GitErrorKind.AuthenticationFailed or GitErrorKind.NetworkUnreachable or GitErrorKind.TimedOut
        or GitErrorKind.HostKeyVerificationFailed or GitErrorKind.Unknown or GitErrorKind.Cancelled);

    private bool FirstWarning(RepositoryContext context, GitErrorKind kind)
    {
        lock (_warned)
            return _warned.GetOrCreateValue(context).Add(kind);
    }
}
