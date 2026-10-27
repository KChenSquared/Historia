namespace Historia.Services.Workspace;

public sealed partial class WorkspaceService
{
    /// <summary>How many non-focused tabs of a session restore open at the same time.</summary>
    private const int RestoreOpenConcurrency = 2;

    /// <summary>Opens one repository's context during a session restore; tests substitute a
    /// gated one to control when each open starts and finishes.</summary>
    internal Func<string, IServiceProvider, CancellationToken, Task<RepositoryContext>> OpenContext { get; set; } =
        (path, services, ct) => RepositoryContext.OpenAsync(path, services, ct);

    public async Task RestoreSessionAsync(CancellationToken ct = default)
    {
        Historia.Diagnostics.PerfLog.Mark("RestoreSessionAsync start");
        var file = AppPaths.SessionFile;
        if (!System.IO.File.Exists(file))
            return;

        List<string> paths = [];
        string? focusedPath = null;
        List<string?>? linkOwnerPaths = null;
        try
        {
            var node = JsonNode.Parse(await System.IO.File.ReadAllTextAsync(file, ct).ConfigureAwait(false));
            if (node is JsonArray arr) // v1: bare string[]
            {
                paths = arr.Select(n => n?.GetValue<string>()).Where(p => p is not null).Cast<string>().ToList();
            }
            else if (node is JsonObject obj) // v2+: { schemaVersion, paths, focusedPath, [linkOwners] }
            {
                if (obj["paths"] is JsonArray p)
                    paths = p.Select(n => n?.GetValue<string>()).Where(s => s is not null).Cast<string>().ToList();
                focusedPath = obj["focusedPath"]?.GetValue<string>();

                // v3: a parallel array of owner paths (or null) — a v1/v2 file has no key here,
                // so every restored tab keeps LinkOwner at its default (null), unchanged.
                var schemaVersion = obj["schemaVersion"]?.GetValue<int>() ?? 2;
                if (schemaVersion >= 3 && obj["linkOwners"] is JsonArray lo)
                    linkOwnerPaths = lo.Select(n => n?.GetValue<string>()).ToList();
            }
        }
        catch
        {
            return;
        }

        var existing = paths.FirstOrDefault(p => System.IO.Directory.Exists(p)
            && string.Equals(p, focusedPath, StringComparison.OrdinalIgnoreCase));
        var focusTarget = existing ?? paths.FirstOrDefault(System.IO.Directory.Exists);
        // Trim a trailing separator before GetFileName: RepositoryPath (the source of both
        // "paths" and "focusedPath" once persisted) may end in a separator,
        // and GetFileName returns "" for a path ending in a separator.
        var focusTargetName = focusTarget is null
            ? "(none)"
            : System.IO.Path.GetFileName(focusTarget.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
        Historia.Diagnostics.PerfLog.Mark($"RestoreSessionAsync focusTarget resolved ({paths.Count} paths, focus={focusTargetName})");

        // The focused tab's RepositoryContext.Open starts right away. Every other tab's open waits
        // for it to finish and then runs through a gate of RestoreOpenConcurrency, so a restore of
        // many tabs never launches git for all of them at once and the focused tab's open and
        // graph walk have the CPU and disk to themselves. The loop below adds tabs to _open (and
        // focuses/initializes the right one) strictly in saved-session order; each non-focused tab
        // only needs RepositoryContext.Open (no graph work -- InitializeContextAsync is deferred
        // until it is actually focused).
        var openGate = new SemaphoreSlim(RestoreOpenConcurrency);
        Task<RepositoryContext>? focusPrefetch = null;
        var prefetches = new Dictionary<string, Task<RepositoryContext>>(StringComparer.OrdinalIgnoreCase);
        if (focusTarget is not null && !prefetches.ContainsKey(focusTarget))
            // Task.Run only so the open's synchronous start (launching git) is off the caller;
            // no thread waits on git.
            prefetches[focusTarget] = focusPrefetch = Task.Run(() => OpenContext(focusTarget, _contextServices, ct), ct);

        async Task<RepositoryContext> OpenAfterFocusAsync(string p)
        {
            if (focusPrefetch is not null)
            {
                try { await focusPrefetch.ConfigureAwait(false); }
                catch { /* the focused tab failing to open must not hold the others back */ }
            }

            await openGate.WaitAsync(ct).ConfigureAwait(false);
            try { return await OpenContext(p, _contextServices, ct).ConfigureAwait(false); }
            finally { openGate.Release(); }
        }

        foreach (var p in paths)
        {
            if (System.IO.Directory.Exists(p) && !prefetches.ContainsKey(p))
                prefetches[p] = Task.Run(() => OpenAfterFocusAsync(p), ct);
        }

        try
        {
            foreach (var path in paths)
            {
                // Consume this path's prefetch exactly once -- a duplicate path later in the session
                // file (or one whose directory was already gone when the map above was built) has no
                // entry left and is skipped.
                if (!prefetches.Remove(path, out var prefetch))
                    continue;
                var isFocus = string.Equals(path, focusTarget, StringComparison.OrdinalIgnoreCase);
                try
                {
                    await OpenCoreAsync(path, focus: isFocus, initialize: isFocus, ct, prefetched: prefetch, recordRecent: false)
                        .ConfigureAwait(false);
                }
                catch { /* skip a repo that no longer opens */ }

                if (isFocus)
                {
                    Historia.Diagnostics.PerfLog.Mark("RestoreSessionAsync focus tab open+focused");
                    try { await Task.Delay(FocusPriorityYield, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
            }

            // v3: re-establish each restored tab's link group. The saved "paths" order already
            // reflects the persisted group layout, so this only needs to set LinkOwner directly
            // — no need to go through OpenSubmoduleAsync's insertion logic.
            if (linkOwnerPaths is not null)
            {
                lock (_sync)
                {
                    for (var i = 0; i < paths.Count && i < linkOwnerPaths.Count; i++)
                    {
                        var ownerPath = linkOwnerPaths[i];
                        if (ownerPath is null) continue;
                        // Raw session-file paths don't share normalized RepositoryPath
                        // formatting (trailing separator, casing), so compare normalized forms —
                        // the same helper OpenAsync's own duplicate-open check uses.
                        var childKey = NormalizeDir(paths[i]);
                        var ownerKey = NormalizeDir(ownerPath);
                        var child = _open.FirstOrDefault(c => string.Equals(NormalizeDir(c.RepositoryPath), childKey, StringComparison.OrdinalIgnoreCase));
                        var owner = _open.FirstOrDefault(c => string.Equals(NormalizeDir(c.RepositoryPath), ownerKey, StringComparison.OrdinalIgnoreCase));
                        if (child is not null && owner is not null)
                            child.LinkOwner = owner;
                    }
                }
            }
        }
        finally
        {
            // Any prefetched repo that wasn't consumed (due to cancellation, duplicates, or
            // error) must be disposed so its background resources and file watchers don't leak.
            foreach (var unconsumed in prefetches.Values)
            {
                _ = unconsumed.ContinueWith(async t =>
                {
                    if (t.Status == TaskStatus.RanToCompletion && t.Result is { } ctx)
                    {
                        try { await ctx.DisposeAsync().ConfigureAwait(false); }
                        catch { /* best effort cleanup */ }
                    }
                }, TaskScheduler.Default);
            }
        }
    }

    private void PersistSession()
    {
        try
        {
            string[] paths;
            string?[] linkOwnerPaths;
            string? focusedPath;
            long sequence;
            lock (_sync)
            {
                paths = _open.Select(c => c.RepositoryPath).ToArray();
                linkOwnerPaths = _open.Select(c => c.LinkOwner?.RepositoryPath).ToArray();
                focusedPath = _focused?.RepositoryPath;
                sequence = ++_sessionPersistSequence;
            }

            var payload = new JsonObject
            {
                ["schemaVersion"] = 3,
                ["paths"] = new JsonArray([.. paths.Select(p => (JsonNode)p!)]),
                ["linkOwners"] = new JsonArray([.. linkOwnerPaths.Select(p => p is null ? null : (JsonNode)p)]),
                ["focusedPath"] = focusedPath,
            };
            SessionWriter.QueueWrite(payload.ToJsonString(), sequence);
        }
        catch
        {
            // Session persistence is best-effort.
        }
    }
}
