namespace Historia.Services.Workspace;

public sealed record CloneRequest(string Url, string TargetDirectory, CloneOptions Options, Guid? ProfileId);

public sealed record CloneStatus(string Url, string TargetDirectory, GitProgress? Progress, bool CancelRequested)
{
    public const string CommitGraphPhase = "Writing commit-graph";
    public const string CachingPhase = "Caching history";

    /// <summary>Formats the live clone status for display in the Home landing panel.</summary>
    public string Describe()
    {
        if (Progress is null)
            return "Starting clone…";
        if (Progress.Phase == CachingPhase)
            return $"Caching history — {Progress.ObjectsDone:N0} commits";
        return $"{Progress.Phase} {(Progress.Percent is { } pct ? $"{pct:0}%" : "")}".Trim();
    }
}

/// <summary>
/// Owns the lifetime and progress of an active clone operation.
/// Singleton in Core so clone progress and completion survive opening and closing the Home panel.
/// </summary>
public interface ICloneService
{
    CloneStatus? Current { get; }
    bool IsRunning { get; }
    event EventHandler? Changed;
    Task StartAsync(CloneRequest request);
    void Cancel();
}

public sealed class CloneService(
    IWorkspaceService workspace,
    IProfileService profiles,
    IRepoIdentityService identity,
    INotificationService notifications,
    INavigationBus nav) : ICloneService
{
    private readonly object _sync = new();
    private CloneStatus? _current;
    private CancellationTokenSource? _cts;

    public CloneStatus? Current
    {
        get { lock (_sync) return _current; }
        private set { lock (_sync) _current = value; }
    }

    public bool IsRunning => Current is not null;

    public event EventHandler? Changed;

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public void Cancel()
    {
        CancellationTokenSource? cts;
        lock (_sync)
        {
            if (_current is null || _current.CancelRequested)
                return;
            _current = _current with { CancelRequested = true };
            cts = _cts;
        }
        RaiseChanged();
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    public async Task StartAsync(CloneRequest request)
    {
        using var cts = new CancellationTokenSource();
        lock (_sync)
        {
            if (_current is not null)
                throw new InvalidOperationException("A clone is already running.");
            _cts = cts;
            _current = new CloneStatus(request.Url, request.TargetDirectory, null, false);
        }
        RaiseChanged();

        try
        {
            var progress = new SynchronousProgress<GitProgress>(p =>
            {
                lock (_sync)
                {
                    if (_current is null) return;
                    _current = _current with { Progress = p };
                }
                RaiseChanged();
            });

            var ctx = await workspace.CloneAsync(request.Url, request.TargetDirectory, request.Options, progress, cts.Token).ConfigureAwait(false);

            bool wasCancelled;
            lock (_sync) wasCancelled = _current?.CancelRequested == true;

            if (wasCancelled)
            {
                notifications.Publish(NotificationSeverity.Info, "Cloned — history caching was cancelled; it finishes in the background.");
            }

            await ApplyCheckoutIdentityAsync(ctx, request.ProfileId).ConfigureAwait(false);
            NavigateOnSuccess();
        }
        catch (OperationCanceledException)
        {
            // The user just cancelled; AppLog.Write redacts any userinfo in the URL.
            AppLog.Write(AppLogLevel.Info, "declined", $"Clone cancelled: {request.Url}");
        }
        catch (Exception ex)
        {
            notifications.Publish(NotificationSeverity.Error, ex.Message, ex.NotificationDetails());
        }
        finally
        {
            lock (_sync)
            {
                _current = null;
                _cts = null;
            }
            RaiseChanged();
        }
    }

    private void NavigateOnSuccess()
    {
        var route = nav.CurrentRoute;
        var head = route.IndexOf('?') is var q && q >= 0 ? route[..q] : route;
        head = head.TrimStart('/');
        if (string.IsNullOrEmpty(head))
            nav.RequestNavigate("repo");
    }

    /// <summary>binds the clone dialog's chosen checkout identity to the freshly cloned
    /// repo — a profile, or the <i>Local config</i> marker when none was chosen.</summary>
    private async Task ApplyCheckoutIdentityAsync(RepositoryContext ctx, Guid? profileId)
    {
        try
        {
            var key = ctx.IdentityKey ?? await identity.ResolveAsync(ctx, ctx.Lifetime).ConfigureAwait(false);

            if (profileId is not { } id)
            {
                await profiles.SetLocalConfigForRepositoryAsync(key, ctx.RepositoryPath, ctx.Lifetime).ConfigureAwait(false);
                ctx.State.SetActiveProfile(null);
                return;
            }
            var profile = profiles.GetAllProfiles().FirstOrDefault(p => p.Id == id);
            if (profile is null)
                return;

            await profiles.BindProfileToRepositoryAsync(key, ctx.RepositoryPath, profile.Id, ctx.Lifetime).ConfigureAwait(false);
            ctx.State.SetActiveProfile(profile);
        }
        catch (Exception ex)
        {
            // Best-effort: the clone itself already succeeded, so a binding hiccup is a
            // notice, not a failure of the clone.
            notifications.Publish(NotificationSeverity.Warning,
                $"Cloned, but couldn't apply the chosen profile/network mode: {ex.Message}", ex.NotificationDetails());
        }
    }
}

internal sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
