namespace Historia.Services.UI;

/// <summary>
/// The branch menus' pull request items. <see cref="PushAndOpenAsync"/> pushes a local branch and then
/// opens the Create pull request dialog; <see cref="OpenAsync"/> opens it without pushing. The push goes
/// through the repository's network gate (so it shows progress with Cancel and never runs beside another
/// network operation) but not the rejection chooser: a rejected push is a classified error card, and the
/// dialog does not open. The browser is never started here; only the dialog's Open button does that.
/// </summary>
internal sealed class PullRequestFlow(
    IRepoNetworkActionRunner runner, IPullRequestDialogs dialogs, INotificationService notifications)
{
    /// <summary>Builds the flow from the services it needs, or null when the runner or the notification
    /// service is not registered.</summary>
    public static PullRequestFlow? From(IServiceProvider services)
    {
        if (services.GetService<IRepoNetworkActionRunner>() is not { } runner
            || services.GetService<INotificationService>() is not { } notifications)
            return null;
        return new PullRequestFlow(runner, services.GetService<IPullRequestDialogs>() ?? NullPullRequestDialogs.Instance, notifications);
    }

    /// <summary>Pushes <paramref name="localBranch"/> to its upstream remote (else <c>origin</c>, else the
    /// first remote), setting the upstream when it has none, then opens the dialog on it. Nothing opens
    /// when the push fails, is cancelled, or is refused because the repository is busy.</summary>
    public async Task PushAndOpenAsync(RepositoryContext ctx, string localBranch)
    {
        if (FindLocal(ctx, localBranch) is not { } branch)
        {
            notifications.Publish(NotificationSeverity.Warning, $"Branch '{localBranch}' was not found.");
            return;
        }
        if (CreatePullRequestDialogViewModel.ResolvePushRemote(branch, ctx.State.Remotes) is not { } remote)
        {
            notifications.Publish(NotificationSeverity.Warning, "This repository has no remotes configured.");
            return;
        }

        if (await PushAsync(ctx, localBranch, remote, CancellationToken.None))
            Show(ctx, $"local.{localBranch}");
    }

    /// <summary>Opens the dialog with the remote-tracking branch <paramref name="remote"/>/<paramref name="branch"/>
    /// as the source.</summary>
    public Task OpenAsync(RepositoryContext ctx, string remote, string branch)
    {
        Show(ctx, $"{remote}.{branch}");
        return Task.CompletedTask;
    }

    private static BranchInfo? FindLocal(RepositoryContext ctx, string name) =>
        ctx.State.Branches.FirstOrDefault(b => !b.IsRemote && string.Equals(b.Name, name, StringComparison.Ordinal));

    // RunExclusiveAsync returns without running the delegate when the repository is busy or the
    // run is cancelled, so only the flag says the push really happened.
    private async Task<bool> PushAsync(RepositoryContext ctx, string branchName, string remote, CancellationToken ct)
    {
        var branch = FindLocal(ctx, branchName);
        if (branch is null)
            return false;

        var upstream = CreatePullRequestDialogViewModel.UpstreamOf(branch, ctx.State.Remotes);
        var spec = RepoNetworkActionRunner.BuildPushSpec(
            branchName,
            upstream is { } u && u.Remote == remote ? u.Branch : null,
            branch.UpstreamName is not null);

        var pushed = false;
        try
        {
            // Registered only around the push: once it ends, Cancel would stop whichever
            // operation holds the repository's slot next.
            using (ct.Register(() => runner.Cancel(ctx)))
            {
                await runner.RunExclusiveAsync(ctx, "Push", async (progress, token) =>
                {
                    await ctx.Gateway.PushAsync(remote, spec, progress, token);
                    pushed = true;
                });
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage("Push"), ex.NotificationDetails());
            return false;
        }

        if (!pushed)
            return false;

        try
        {
            await ctx.State.RefreshRefsAsync(ctx.Lifetime);
        }
        catch (OperationCanceledException)
        {
            // The repository closed meanwhile; there is nothing left to refresh.
        }
        catch (Exception ex)
        {
            // The push itself happened, so the dialog still opens on the branches as they were.
            notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage("Refresh"), ex.NotificationDetails());
        }
        notifications.Publish(NotificationSeverity.Success, $"Pushed {branchName} to {remote}.");
        return true;
    }

    // Deferred so the dialog parents cleanly after the menu tears down.
    private void Show(RepositoryContext ctx, string initialSource)
    {
        var request = new CreatePullRequestRequest(
            ctx.State.Branches,
            ctx.State.Remotes,
            initialSource,
            (branch, remote, ct) => PushAsync(ctx, branch, remote, ct),
            () => ctx.State.Branches);

        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await dialogs.ShowAsync(request);
            }
            catch (Exception ex)
            {
                notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage("Create pull request"), ex.NotificationDetails());
            }
        });
    }
}
