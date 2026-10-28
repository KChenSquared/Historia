
namespace Historia.Services.Workspace;

/// <summary>Persistent recent-repositories list. Singleton.</summary>
public interface IRecentRepositoryService
{
    /// <summary>Most-recent first. <see cref="RecentRepository.IsStale"/> is the last value a
    /// background check produced; reading never touches the disk, so a repository that went
    /// missing shows up after <see cref="RefreshStalenessAsync"/> completes (and <see cref="Changed"/> fires).</summary>
    IReadOnlyList<RecentRepository> Items { get; }

    /// <summary>Re-checks every entry's folder off the calling thread, each with its own timeout (a
    /// check that does not answer keeps the previous value), and raises <see cref="Changed"/> when
    /// any entry's staleness moved. The default does nothing: a service without a check keeps
    /// whatever <see cref="Items"/> reports.</summary>
    Task RefreshStalenessAsync() => Task.CompletedTask;

    /// <summary>Whether the folder of <paramref name="path"/> is gone: true when missing, false
    /// when present, null when unknown (no answer in time, or a check of it is still hanging). Never
    /// blocks the calling thread. The default reports the last value in <see cref="Items"/>.</summary>
    Task<bool?> IsStaleAsync(string path)
        => Task.FromResult<bool?>(Items
            .FirstOrDefault(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase))?.IsStale);

    Task AddAsync(string path, string displayName, CancellationToken ct = default);
    Task RemoveAsync(string path, CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);

    event EventHandler? Changed;
}
