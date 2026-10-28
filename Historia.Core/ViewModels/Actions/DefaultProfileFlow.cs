namespace Historia.Services.Profiles;

/// <summary>
/// Changing the default profile. Every entry point (the Profiles panel's "Set as default" and the
/// app menu's "Default profile" submenu) goes through here, so a non-global default always asks
/// whether repositories without their own selection follow it.
/// </summary>
public static class DefaultProfileFlow
{
    public static async Task SetAsync(IServiceProvider sp, GitProfile p)
    {
        var profiles = sp.GetRequiredService<IProfileService>();
        var dialogs = sp.GetRequiredService<IDialogService>();
        var notifications = sp.GetRequiredService<INotificationService>();

        if (p.IsGlobal)
        {
            await profiles.SetDefaultProfileAsync(null);
            return;
        }

        // choosing a non-global default asks whether row-less repos follow it.
        var choice = await dialogs.ChooseAsync(
            "Set as default",
            $"Make repositories that don't have their own profile use '{p.DisplayName}'?",
            ["Follow the default", "Only new repositories"]);

        if (choice < 0)
        {
            // The user declined; nothing changed, so there's nothing new to tell them.
            AppLog.Write(AppLogLevel.Info, "declined", $"Set default profile '{p.DisplayName}' cancelled");
            return;
        }

        if (choice == 1)
            await PinExistingRowlessReposAsync(sp, profiles);

        await profiles.SetDefaultProfileAsync(p.Id);
        notifications.Publish(NotificationSeverity.Info, $"'{p.DisplayName}' is now the default profile.");
    }

    /// <summary>"Only new repositories": pin every known (recent) repo that has no explicit
    /// selection row to its current resolution, so it keeps that identity when the default moves.</summary>
    private static async Task PinExistingRowlessReposAsync(IServiceProvider sp, IProfileService profiles)
    {
        var recents = sp.GetRequiredService<IRecentRepositoryService>();
        var identity = sp.GetRequiredService<IRepoIdentityService>();
        // One concurrent, timeout-guarded check per entry; a folder that is missing or does not
        // answer is skipped.
        var entries = recents.Items;
        var stale = await Task.WhenAll(entries.Select(e => recents.IsStaleAsync(e.Path)));
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (stale[i] is not false)
                continue;
            try
            {
                var path = entry.Path;
                var (key, resolved, hasRow) = await Task.Run(async () =>
                {
                    await using var ctx = await RepositoryContext.OpenAsync(path, sp);
                    var k = await identity.ResolveAsync(ctx);
                    var row = profiles.GetProfileForRepository(k) is not null || profiles.IsLocalConfigSelected(k);
                    return (k, profiles.ResolveActiveProfile(k), row);
                });

                if (hasRow)
                    continue;
                if (resolved is null)
                    await profiles.SetLocalConfigForRepositoryAsync(key, path);
                else
                    await profiles.BindProfileToRepositoryAsync(key, path, resolved.Id);
            }
            catch
            {
                // A repo that can't be opened is simply skipped.
            }
        }
    }
}
